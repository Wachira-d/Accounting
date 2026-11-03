using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Settlement;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Payments;

public sealed record TransferPullResult(int Pulled, int New, int Drafted, int Unmatched, string Message);

public sealed record PlatformTransferRow(Guid Id, string ProviderCode, bool IsTestMode, string TransferRef, decimal WalletDebit, decimal NetToBank,
    decimal TransferFee, decimal TransferFeeVat, string? RawStatus, bool Paid, DateTime? ProviderCreatedAt, DateTime? PaidAt,
    int ChargeRefCount, string MatchMethod, string MatchMethodLabel, int MatchedIntentCount, string? Problem, Guid? SettlementBatchId,
    string? SettlementBatchStatus, DateTime? DraftCreatedAt, DateTime PulledAt,
    // ฝ่ายค้าน S5: ร่างที่ถูกยกเลิก/ลบที่หน้ารอบโอน = ไม่มีร่าง (แสดงตามจริง)
    bool HasLiveDraft = false);

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §5.3 · §7.1) — <b>ดึงรอบโอนของช่องทางค่าบริการ NextAcc จากผู้ให้บริการ → ร่างรอบโอน</b> ในบริษัทผู้ให้บริการ
/// <list type="number">
/// <item>ดึงผ่าน <see cref="IPaymentProvider.ListSettlementsAsync"/> ด้วยคีย์ของโหมดปัจจุบัน (ไม่มีชื่อเจ้าในไฟล์นี้) · watermark = รอบโอนล่าสุดที่เก็บ − 7 วัน
///   (เก็บ upsert ด้วย unique ผู้ให้บริการ+เลขรอบโอน ⇒ ดึงซ้ำไม่เกิดแถวซ้ำ)</item>
/// <item>รอบโอนที่จ่ายแล้ว (เงินจริง) ⇒ จับคู่ (<see cref="PlatformTransferMatch"/>) ⇒ <see cref="ISettlementImportService.ImportProviderTransferDraftAsync"/>
///   (ด่านชุดเดียวกับ "ประกอบรอบโอนจากรายการรับชำระ" · PayoutRef = เลขรอบโอน ⇒ ร่างซ้ำไม่ได้) · โหมดทดสอบ = เก็บแสดงอย่างเดียว (ไม่มีเงินจริง)</item>
/// <item><b>ไม่ลงบัญชีเอง</b> — ผู้ทำบัญชีของบริษัทผู้ให้บริการตรวจแล้วกดลงบัญชีที่หน้ารอบโอน (Dr ธนาคาร · Dr ค่าธรรมเนียม+VAT · Cr 11340 ตามแผนเดิม) ·
///   ยอดไม่ตรง ⇒ ด่านเดิมบล็อก · จับคู่ไม่ได้ ⇒ ประทับเหตุบนแถว (หน้าแอดมินเห็น) ไม่เดา</item>
/// </list>
/// </summary>
public interface IPlatformTransferReconciliationService
{
    /// <summary>ดึง + ร่าง — <paramref name="actorUserId"/> = ผู้สร้างร่าง (งานเบื้องหลัง = Guid.Empty)</summary>
    Task<TransferPullResult> PullAsync(Guid actorUserId, CancellationToken ct = default);
    Task<IReadOnlyList<PlatformTransferRow>> ListAsync(int take, CancellationToken ct = default);
    /// <summary>จับคู่/ร่างใหม่ของรอบโอนที่ยังไม่มีร่าง (หลังสร้างช่องทาง/แก้ค่าธรรมเนียม)</summary>
    Task<PlatformTransferRow> RematchAsync(Guid transferId, Guid actorUserId, CancellationToken ct = default);
}

public class PlatformTransferReconciliationService : IPlatformTransferReconciliationService
{
    public const int InitialLookbackDays = 60, OverlapDays = 7;

    private readonly AccountingDbContext _db;
    private readonly IPlatformPaymentService _platform;
    private readonly IEnumerable<IPaymentProvider> _providers;
    private readonly ISettlementImportService _settlements;
    private readonly ILogger<PlatformTransferReconciliationService> _logger;

    public PlatformTransferReconciliationService(AccountingDbContext db, IPlatformPaymentService platform, IEnumerable<IPaymentProvider> providers,
        ISettlementImportService settlements, ILogger<PlatformTransferReconciliationService> logger)
    { _db = db; _platform = platform; _providers = providers; _settlements = settlements; _logger = logger; }

    public async Task<TransferPullResult> PullAsync(Guid actorUserId, CancellationToken ct = default)
    {
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid) return new(0, 0, 0, 0, "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ");
        var cfg = await _platform.PlatformConfigAsync(ct);
        if (cfg == null) return new(0, 0, 0, 0, "ยังไม่ได้ตั้งช่องทางรับชำระของ NextAcc");
        var provider = _providers.FirstOrDefault(p => p.ProviderCode == cfg.ProviderCode);
        if (provider == null) return new(0, 0, 0, 0, "ไม่รู้จักผู้ให้บริการของช่องทาง NextAcc");
        var isTest = cfg.Mode == PaymentProviderMode.Test;

        var last = await _db.PlatformProviderTransfers.AsNoTracking()
            .Where(x => x.PlatformCompanyId == pid && x.ProviderCode == cfg.ProviderCode && x.IsTestMode == isTest)
            .MaxAsync(x => x.ProviderCreatedAt, ct);
        var since = last is DateTime l ? l.AddDays(-OverlapDays) : DateTime.UtcNow.AddDays(-InitialLookbackDays);
        // ผู้ให้บริการล้ม ⇒ โยนต่อ (งาน/ปุ่มแอดมินล้มดัง) — ยังไม่มีอะไรถูกบันทึก
        var pulled = await provider.ListSettlementsAsync(cfg, since, ct);

        var now = DateTime.UtcNow;
        var refs = pulled.Select(p => p.Ref).Distinct().ToList();
        var existing = await _db.PlatformProviderTransfers
            .Where(x => x.ProviderCode == cfg.ProviderCode && refs.Contains(x.TransferRef)).ToDictionaryAsync(x => x.TransferRef, ct);
        var fresh = 0;
        foreach (var s in pulled)
        {
            if (!existing.TryGetValue(s.Ref, out var row))
            {
                row = new PlatformProviderTransfer
                {
                    PlatformCompanyId = pid, ProviderCode = cfg.ProviderCode, ProviderConfigId = cfg.Id, IsTestMode = isTest, TransferRef = s.Ref,
                    PulledAt = now, CreatedBy = actorUserId == Guid.Empty ? "system:transfer-pull" : actorUserId.ToString(),
                };
                _db.PlatformProviderTransfers.Add(row);
                existing[s.Ref] = row;
                fresh++;
            }
            if (row.PlatformCompanyId != pid) continue;   // เลขรอบโอนเดียวกันของบริษัทอื่น (ไม่ควรเกิด) — ไม่แตะ
            if (row.SettlementBatchId == null)
            {
                // ยังไม่มีร่าง ⇒ รับค่าล่าสุดจากผู้ให้บริการ (มีร่างแล้ว = ตัวเลขของร่างเป็นของคนตรวจ ห้ามทับ)
                row.WalletDebit = s.WalletDebit; row.NetToBank = s.NetToBank; row.TransferFee = s.TransferFee; row.TransferFeeVat = s.TransferFeeVat;
                row.ProviderCreatedAt = s.CreatedAtUtc; row.ChargeRefsJson = s.ChargeRefs == null ? null : JsonSerializer.Serialize(s.ChargeRefs);
            }
            row.RawStatus = Trunc(s.RawStatus, 40);
            row.Paid = s.Paid;
            row.PaidAt = s.PaidAtUtc;
            row.PulledAt = now;
            row.UpdatedAt = now;
            var modeBad = PaymentLiveModeGuard.Mismatch(s.LiveMode, isTest);
            if (modeBad != null) row.Problem = Trunc("โหมดของรอบโอนไม่ตรงช่องทาง: " + modeBad, 500);
            else if (s.ShapeNote != null && row.SettlementBatchId == null) row.Problem = Trunc(s.ShapeNote, 500);
        }
        await _db.SaveChangesAsync(ct);

        int drafted = 0, unmatched = 0;
        var toDraft = existing.Values.Where(r => r.PlatformCompanyId == pid && r.SettlementBatchId == null && r.Paid && !r.IsTestMode
            && (r.Problem == null || !r.Problem.StartsWith("โหมดของรอบโอนไม่ตรง", StringComparison.Ordinal))).ToList();
        foreach (var row in toDraft)
        {
            if (await TryDraftAsync(pid, cfg, row, actorUserId, ct)) drafted++; else unmatched++;
        }
        return new(pulled.Count, fresh, drafted, unmatched,
            $"ดึงรอบโอน {pulled.Count} รอบ (ใหม่ {fresh}) · ร่างรอบโอน {drafted} · ยังจับคู่ไม่ได้ {unmatched}" + (isTest ? " · โหมดทดสอบ: แสดงอย่างเดียว ไม่ร่าง" : ""));
    }

    public async Task<PlatformTransferRow> RematchAsync(Guid transferId, Guid actorUserId, CancellationToken ct = default)
    {
        var pid = await _platform.PlatformCompanyIdAsync(ct) ?? throw new KeyNotFoundException("ยังไม่ได้ตั้งบริษัทผู้ให้บริการ");
        var row = await _db.PlatformProviderTransfers.FirstOrDefaultAsync(x => x.Id == transferId && x.PlatformCompanyId == pid, ct)
            ?? throw new KeyNotFoundException("ไม่พบรอบโอนนี้");
        if (row.SettlementBatchId is Guid bid)
        {
            // ฝ่ายค้าน S5: ร่างที่ถูกยกเลิก/ลบ = ไม่มีร่าง — แต่เลขรอบโอนเดิมผูกกับรอบนั้นอยู่ ⇒ ไม่ร่างซ้ำอัตโนมัติ (ประกอบเองที่หน้ารอบโอน)
            var live = await _db.SettlementBatches.AsNoTracking().AnyAsync(b => b.Id == bid && b.CompanyId == pid && b.Status != SettlementBatchStatus.Voided, ct);
            throw new BusinessRuleException(live
                ? "รอบโอนนี้มีร่างแล้ว — ตรวจ/ยกเลิกที่หน้ารอบโอนของบริษัทผู้ให้บริการ"
                : "ร่างเดิมของรอบโอนนี้ถูกยกเลิกแล้ว — ประกอบรอบโอนเองที่หน้ารอบโอนของบริษัทผู้ให้บริการ (ระบบไม่ร่างซ้ำด้วยเลขรอบโอนเดิม)",
                "PLATFORM-TRANSFER-DRAFTED", 409);
        }
        if (row.IsTestMode) throw new BusinessRuleException("รอบโอนโหมดทดสอบไม่มีเงินจริง — ไม่สร้างร่าง", "PLATFORM-TRANSFER-TEST", 409);
        if (!row.Paid) throw new BusinessRuleException("ผู้ให้บริการยังไม่ยืนยันว่าโอนเงินถึงธนาคาร — รอสถานะจ่ายแล้วก่อน", "PLATFORM-TRANSFER-UNPAID", 409);
        var cfg = await _platform.PlatformConfigAsync(ct);
        if (cfg == null || cfg.ProviderCode != row.ProviderCode)
            throw new BusinessRuleException("ช่องทางรับชำระของ NextAcc เปลี่ยนผู้ให้บริการแล้ว — ประกอบรอบโอนเองที่หน้ารอบโอน", "PLATFORM-TRANSFER-PROVIDER", 409);
        await TryDraftAsync(pid, cfg, row, actorUserId, ct);
        return (await ListAsync(500, ct)).First(r => r.Id == row.Id);
    }

    /// <summary>จับคู่ + ร่าง · true = มีร่างแล้ว · ล้ม = ประทับเหตุบนแถว (ไม่โยน — แถวอื่นในรอบเดียวกันต้องไปต่อได้ และเหตุต้องเห็นบนหน้าแอดมิน)</summary>
    private async Task<bool> TryDraftAsync(Guid pid, PaymentProviderConfig cfg, PlatformProviderTransfer stale, Guid actorUserId, CancellationToken ct)
    {
        // ตัวนำเข้าล้างตัวติดตามของ context ทุกครั้ง ⇒ แถวที่ถือมาอาจหลุดการติดตามแล้ว — อ่านใหม่ (ได้ตัวที่ติดตามอยู่) ก่อนประทับ
        var row = await _db.PlatformProviderTransfers.FirstAsync(x => x.Id == stale.Id && x.PlatformCompanyId == pid, ct);
        var channel = await _db.SettlementChannels.AsNoTracking()
            .Where(c => c.CompanyId == pid && c.Kind == SettlementChannelKind.Gateway && c.PaymentProviderConfigId == cfg.Id && c.IsActive)
            .OrderBy(c => c.CreatedAt).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (channel == null)
        {
            row.MatchMethod = PlatformTransferMatch.MethodNone;
            row.Problem = "ยังไม่มีช่องทางรอบโอน (ชนิด Gateway) ที่ผูกกับช่องทางรับชำระของ NextAcc ในบริษัทผู้ให้บริการ — สร้างที่หน้า \"ช่องทางรับเงินผ่าน wallet\" แล้วกด \"จับคู่ใหม่\"";
            row.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return false;
        }

        // ผู้สมัคร = รายการรับชำระที่ยังไม่มีเจ้าของรอบโอน (ตัวตัดสินเดียว UnclaimedForBatch — โหมดทดสอบ/ทดลองจ่ายไม่เข้า) · ยอดสุทธิสูตรเดียวกับร่าง
        var cands = (await _db.PaymentIntents.AsNoTracking()
                .Where(GatewayBatchIntentRules.UnclaimedForBatch(pid, cfg.ProviderCode, null, null))
                .Select(i => new { i.Id, i.ProviderRef, i.ConfirmedAt, i.Amount, i.RefundedAmount, i.FeeActual, i.FeeEstimated })
                .ToListAsync(ct))
            .Select(i => new TransferMatchCandidate(i.Id, i.ProviderRef, i.ConfirmedAt ?? DateTime.MinValue,
                GatewaySettlementMath.Contribution(new SettlementIntentInput(i.Id, i.Amount, i.FeeActual, i.FeeEstimated, i.RefundedAmount), cfg.FeeVatMode).Net))
            .ToList();
        var chargeRefs = string.IsNullOrEmpty(row.ChargeRefsJson) ? null : JsonSerializer.Deserialize<List<string>>(row.ChargeRefsJson);
        var match = PlatformTransferMatch.Match(chargeRefs, row.WalletDebit, row.ProviderCreatedAt, cands);
        row.MatchMethod = match.Method;
        row.UpdatedAt = DateTime.UtcNow;
        if (match.IntentIds.Count == 0)
        {
            row.Problem = Trunc(match.Problem, 500);
            await _db.SaveChangesAsync(ct);
            return false;
        }
        var payoutDate = row.PaidAt ?? row.ProviderCreatedAt ?? DateTime.UtcNow;
        var transferId = row.Id;
        try
        {
            var result = await _settlements.ImportProviderTransferDraftAsync(pid, actorUserId, new ProviderTransferDraftRequest(
                channel.Value, row.TransferRef, payoutDate, row.NetToBank, row.TransferFee, row.TransferFeeVat, match.IntentIds,
                $"ร่างจากรอบโอน {row.TransferRef} ของผู้ให้บริการ ({PlatformTransferMatch.MethodLabel(match.Method)}) — ตรวจก่อนลงบัญชี"), ct);
            // ตัวนำเข้า Clear ตัวติดตามของ context — อ่านแถวใหม่ก่อนประทับ
            var fresh = await _db.PlatformProviderTransfers.FirstAsync(x => x.Id == transferId, ct);
            fresh.MatchMethod = match.Method;
            fresh.SettlementBatchId = result.Batch.Id;
            fresh.DraftCreatedAt = DateTime.UtcNow;
            fresh.MatchedIntentIdsJson = JsonSerializer.Serialize(match.IntentIds);
            var warn = result.Warnings is { Count: > 0 } w ? string.Join(" · ", w) : null;
            fresh.Problem = Trunc(string.Join(" · ", new[] { match.Problem, warn }.Where(x => !string.IsNullOrWhiteSpace(x))), 500) is { Length: > 0 } p ? p : null;
            fresh.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (BusinessRuleException ex)
        {
            // ด่านของตัวนำเข้า (โหมดไม่ตรง · ผังพักไม่ตรง · รายการถูกหยิบ ฯลฯ) — ประทับเหตุ (ทางไปต่ออยู่ในข้อความ) · ไม่มีอะไรถูกบันทึกครึ่งทาง
            _logger.LogWarning("ร่างรอบโอน {Ref} ไม่สำเร็จ: {Why}", row.TransferRef, ex.Message);
            _db.ChangeTracker.Clear();
            var again = await _db.PlatformProviderTransfers.FirstAsync(x => x.Id == transferId, ct);
            again.MatchMethod = match.Method;
            again.Problem = Trunc("ร่างไม่สำเร็จ: " + ex.Message, 500);
            again.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return false;
        }
    }

    public async Task<IReadOnlyList<PlatformTransferRow>> ListAsync(int take, CancellationToken ct = default)
    {
        if (await _platform.PlatformCompanyIdAsync(ct) is not Guid pid) return Array.Empty<PlatformTransferRow>();
        var rows = await _db.PlatformProviderTransfers.AsNoTracking().Where(x => x.PlatformCompanyId == pid)
            .OrderByDescending(x => x.ProviderCreatedAt).ThenByDescending(x => x.PulledAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        var batchIds = rows.Where(r => r.SettlementBatchId != null).Select(r => r.SettlementBatchId!.Value).ToList();
        var batchStatus = await _db.SettlementBatches.AsNoTracking().Where(b => b.CompanyId == pid && batchIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => b.Status, ct);   // ถูกลบ (query filter) = ไม่อยู่ในชุด ⇒ ไม่มีร่าง
        static int Count(string? json) => string.IsNullOrEmpty(json) ? 0 : (JsonSerializer.Deserialize<List<string>>(json)?.Count ?? 0);
        static int CountIds(string? json) => string.IsNullOrEmpty(json) ? 0 : (JsonSerializer.Deserialize<List<Guid>>(json)?.Count ?? 0);
        return rows.Select(r => new PlatformTransferRow(r.Id, r.ProviderCode, r.IsTestMode, r.TransferRef, r.WalletDebit, r.NetToBank, r.TransferFee,
            r.TransferFeeVat, r.RawStatus, r.Paid, r.ProviderCreatedAt, r.PaidAt, Count(r.ChargeRefsJson), r.MatchMethod,
            PlatformTransferMatch.MethodLabel(r.MatchMethod), CountIds(r.MatchedIntentIdsJson), r.Problem, r.SettlementBatchId,
            r.SettlementBatchId is Guid b ? (batchStatus.TryGetValue(b, out var st) ? st.ToString() : "Deleted") : null, r.DraftCreatedAt, r.PulledAt,
            HasLiveDraft: r.SettlementBatchId is Guid lb && batchStatus.TryGetValue(lb, out var ls) && ls != SettlementBatchStatus.Voided)).ToList();
    }

    private static string? Trunc(string? s, int max) => s == null ? null : (s.Length > max ? s[..max] : s);
}
