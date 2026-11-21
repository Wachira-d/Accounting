using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **รอบ 286 — ผู้จ่ายที่ไว้ใจได้: ประทับกุญแจบนรายการ · บันทึกรับเงินล่วงหน้า · ผู้ขายดู/ถอน/ให้คืนความไว้ใจ** (คำสั่งเจ้าของ · คำตัดสิน 286-1…286-9)
/// <list type="bullet">
/// <item>ผู้เรียกเดียวของ <see cref="TryProvisionalAsync"/>: <c>SlipAutoRecordService.TryAutoAsync</c> เมื่อด่านของ 282 ผ่านครบเหลือแต่ "ไม่มีรายการเดินบัญชี" —
/// ทุกทางเข้าของสลิป (แชต/กลุ่ม OA บริษัท · กลุ่ม OA กลาง · อีเมล) จึงเดินตัวตัดสินเดียว <see cref="TrustedSlipPayerRule.Decide"/></item>
/// <item>บันทึกเงินผ่านเส้นยืนยันตัวเดียว (<see cref="SlipIntentConfirmService"/> → <c>CreatePaymentAsync</c> ⇒ ใบเสร็จ/ใบกำกับ ณ วันรับเงินเหมือนผู้ขายกดยืนยัน — คำตัดสิน 286-6)
/// แล้วประทับ "ล่วงหน้า" + กำหนดตรวจย้อน · การยืนยัน/ล้มอยู่ที่ <see cref="TrustedSlipVerifyService"/></item>
/// <item>ไม่มี AI (kill-switch) · ทุก query ผูกบริษัท</item>
/// </list>
/// </summary>
public sealed class TrustedSlipPayerService
{
    private readonly AccountingDbContext _db;
    private readonly IPaymentIntentService _intents;
    private readonly ILogger<TrustedSlipPayerService> _logger;
    private readonly IConfiguration? _config;
    private readonly Accounting.Services.Storage.IFileStorage? _storage;
    private readonly INotificationEngine? _notify;

    public TrustedSlipPayerService(AccountingDbContext db, IPaymentIntentService intents, ILogger<TrustedSlipPayerService> logger, IConfiguration? config = null,
        Accounting.Services.Storage.IFileStorage? storage = null, INotificationEngine? notify = null)
    { _db = db; _intents = intents; _logger = logger; _config = config; _storage = storage; _notify = notify; }

    public const string Actor = "slip-trusted";
    private static readonly TimeSpan Bangkok = TimeSpan.FromHours(7);

    public sealed record Result(bool Recorded, string Code, string Reason);

    /// <summary>ลายนิ้วมือของกุญแจ (canonical <see cref="TrustedSlipPayerRule.Fingerprint"/>) · ไม่มีความลับของเซิร์ฟเวอร์ ⇒ โยน (fail-fast)</summary>
    public string Fingerprint(Guid companyId, TrustedSlipPayerRule.PayerKey key)
        => TrustedSlipPayerRule.Fingerprint(TrustedSlipPayerRule.DeriveHmacKey(_config?["Jwt:Secret"]), companyId, key);

    /// <summary>ประทับกุญแจบนรายการ (ครั้งเดียว) — ใช้ทั้งการเรียนรู้ภายหลังและการใช้ความไว้ใจ · ชื่อดิบไม่ลงฐาน</summary>
    private async Task StampKeyAsync(Guid companyId, Guid intentId, string hash, TrustedSlipPayerRule.PayerKey key, CancellationToken ct)
        => await _db.PaymentIntents.Where(i => i.Id == intentId && i.CompanyId == companyId && i.TrustKeyHash == null)
            .ExecuteUpdateAsync(u => u.SetProperty(i => i.TrustKeyHash, hash).SetProperty(i => i.TrustKeyLabel, key.Label)
                .SetProperty(i => i.TrustKeyAccountId, (Guid?)key.ReceivingBankAccountId), ct);

    /// <summary>กุญแจจากผลอ่านสลิป ⇒ ประทับบนรายการ (ทุกสลิปที่ผูกใบ — ใช้เรียนภายหลังเมื่อรายการเดินบัญชียืนยัน) · ผู้รับต้องตรงบัญชีเดียวของบริษัท (<paramref name="money"/>.Auto)</summary>
    public async Task<(TrustedSlipPayerRule.PayerKey? Key, string? Hash)> StampKeyFromSlipAsync(Guid companyId, Guid intentId, Guid? contactId,
        OcrSlipText.Reading reading, SlipMoneySource.Verdict money, CancellationToken ct = default)
    {
        var key = TrustedSlipPayerRule.KeyFrom(contactId, reading.Payer, money.Auto ? money.AccountId : null);
        if (key == null) return (null, null);
        var hash = Fingerprint(companyId, key);
        await StampKeyAsync(companyId, intentId, hash, key, ct);
        return (key, hash);
    }

    /// <param name="slipVerdict">ผลของ <see cref="SlipAutoRecordRule.Decide"/> (ไม่บันทึกเอง)</param>
    /// <param name="money">แหล่งเงินของผู้รับบนสลิป (บัญชีผู้รับของเรา = ส่วนหนึ่งของกุญแจ)</param>
    /// <returns>Recorded = บันทึกรับเงินล่วงหน้าแล้ว · อื่น ๆ = เหตุ (ผู้เรียกต่อท้ายเหตุ "รอยืนยัน")</returns>
    public async Task<Result> TryProvisionalAsync(Guid companyId, Guid intentId, OcrSlipText.Reading reading, SlipAutoRecordRule.Verdict slipVerdict,
        SlipMoneySource.Verdict money, string? sourceKind, CancellationToken ct = default)
    {
        var intent = await _db.PaymentIntents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == intentId && i.CompanyId == companyId, ct);
        if (intent == null) return new(false, TrustedSlipPayerRule.Codes.NoKey, "ไม่พบรายการชำระ");
        var doc = await _db.Documents.AsNoTracking().Where(d => d.Id == intent.SourceId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => new { d.ContactId, d.DocumentNumber }).FirstOrDefaultAsync(ct);
        var (key, hash) = await StampKeyFromSlipAsync(companyId, intentId, doc?.ContactId, reading, money, ct);
        var settings = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => new { s.SlipTrustedAutoEnabled, s.SlipAutoRecordEnabled, s.SlipTrustedDailyCapPerKey, s.SlipTrustedReviewBusinessDays })
            .FirstOrDefaultAsync(ct);
        var slipReady = slipVerdict.Code == SlipAutoRecordRule.Codes.NoBankLine;
        var settingOn = settings is { SlipTrustedAutoEnabled: true, SlipAutoRecordEnabled: true };

        async Task<TrustedSlipPayerRule.Verdict> DecideNowAsync()
        {
            TrustedSlipPayerRule.Trust? trust = null;
            decimal today = 0m;
            if (hash != null)
            {
                trust = await _db.TrustedSlipPayers.AsNoTracking().Where(t => t.CompanyId == companyId && t.KeyHash == hash)
                    .Select(t => new TrustedSlipPayerRule.Trust(t.RevokedAt != null, t.MaxConfirmedAmount)).FirstOrDefaultAsync(ct);
                var dayStartUtc = (DateTime.UtcNow + Bangkok).Date - Bangkok;
                today = await _db.PaymentIntents.AsNoTracking()
                    .Where(i => i.CompanyId == companyId && i.TrustKeyHash == hash && i.Id != intentId && i.TrustProvisionalAt >= dayStartUtc && i.TrustFailedAt == null)
                    .SumAsync(i => (decimal?)i.Amount, ct) ?? 0m;
            }
            return TrustedSlipPayerRule.Decide(new TrustedSlipPayerRule.Facts(settingOn, slipReady, key, trust, intent.Amount, today,
                settings?.SlipTrustedDailyCapPerKey ?? 0m));
        }

        var verdict = await DecideNowAsync();
        if (!verdict.Provisional) return new(false, verdict.Code, verdict.Reason);

        // กันสองสลิปของกุญแจเดียวกันพร้อมกันเกินเพดานต่อวัน: ตัดสินซ้ำ + จองรายการใต้ล็อกต่อกุญแจ (ไม่ได้ล็อก ⇒ รอยืนยันตามปกติ — ทิศปลอดภัย)
        var slipDay = reading.Date!.Value.Date;
        var holidays = await PlatformHolidayStore.LoadSetAsync(_db, slipDay.Year, slipDay.Year + 1, _logger, ct);
        var dueUtc = TrustedSlipPayerRule.ReviewDueUtc(slipDay, settings!.SlipTrustedReviewBusinessDays, holidays);
        var claimed = false;
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.TrustedSlipPayer, hash!, async () =>
        {
            verdict = await DecideNowAsync();
            if (!verdict.Provisional) return;
            claimed = await _db.PaymentIntents.Where(i => i.Id == intentId && i.CompanyId == companyId && i.TrustProvisionalAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.TrustProvisionalAt, (DateTime?)DateTime.UtcNow).SetProperty(i => i.TrustReviewDueAt, (DateTime?)dueUtc), ct) == 1;
        }, _logger, companyId, ct);
        if (!ran) return new(false, TrustedSlipPayerRule.Codes.OverDaily, "มีสลิปของผู้โอนนี้กำลังบันทึกพร้อมกัน — รอยืนยันเพื่อไม่ให้เกินเพดาน");
        if (!verdict.Provisional) return new(false, verdict.Code, verdict.Reason);
        if (!claimed) return new(false, TrustedSlipPayerRule.Codes.NoKey, "รายการนี้ถูกบันทึกล่วงหน้าไปแล้ว");

        var dueTh = dueUtc + Bangkok;
        var reason = verdict.Reason + $" · ตรวจย้อนภายใน {dueTh.AddDays(-1):dd/MM/yyyy}";
        SlipIntentConfirmService.Outcome? outcome = null;
        try
        {
            outcome = await new SlipIntentConfirmService(_db, _intents).ConfirmAsync(companyId, intent, slipDay, money.AccountId, Guid.Empty,
                Actor, reason, _storage?.Locator.AttachmentsRoot, ct, PaymentEventSource.System);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            _logger.LogError(ex, "บันทึกรับเงินล่วงหน้าของรายการ {Intent} ล้ม", intentId);
        }
        // ผลจริง (ไม่เชื่อค่าที่คืน): สถานะรายการ = รับเงินแล้ว ⇒ คงการจอง · ไม่ใช่ ⇒ คืนการจอง (ไม่มีเงินลงครึ่งทาง)
        var succeeded = await _db.PaymentIntents.AsNoTracking().AnyAsync(i => i.Id == intentId && i.CompanyId == companyId && i.Status == PaymentIntentStatus.Succeeded, ct);
        if (!succeeded)
        {
            await _db.PaymentIntents.Where(i => i.Id == intentId && i.CompanyId == companyId)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.TrustProvisionalAt, (DateTime?)null).SetProperty(i => i.TrustReviewDueAt, (DateTime?)null), ct);
            return new(false, SlipAutoRecordRule.Codes.ConfirmFailed, "บันทึกรับเงินล่วงหน้าไม่สำเร็จ: " + (outcome?.Message ?? "ข้อผิดพลาดของระบบ"));
        }

        try
        {
            var r = LineMessageLimits.Truncate(TrustedSlipPayerRule.ProvisionalLabel + $" (ภายใน {dueTh.AddDays(-1):dd/MM/yyyy})", 500);
            await _db.PaymentIntents.Where(i => i.Id == intentId && i.CompanyId == companyId)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.AutoRecordCode, TrustedSlipPayerRule.Codes.Provisional).SetProperty(i => i.AutoRecordReason, r), ct);
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = null, UserEmail = Actor, Action = AuditAction.Update, EntityType = "PaymentIntent", EntityId = intentId.ToString(),
                NewValues = JsonSerializer.Serialize(new
                {
                    @event = "SlipTrustedProvisional", code = verdict.Code, reason = verdict.Reason, amount = intent.Amount, reviewDueUtc = dueUtc,
                    bankAccountId = money.AccountId, keyLabel = key!.Label, channel = SlipAutoRecordRule.ChannelLabel(sourceKind),
                    ruleCode = TrustedSlipPayerRule.RuleCode, legalReference = TrustedSlipPayerRule.LegalReference, usedAi = false,
                }),
                Timestamp = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync(ct);
            await NotifyAsync(companyId, NotificationEvents.AutopilotSlipTrustedProvisional,
                $"⚙️ บันทึกรับเงินล่วงหน้า {intent.Amount:N2} บาท ของ {doc?.DocumentNumber ?? "เอกสาร"} (ผู้จ่ายที่ไว้ใจได้)",
                $"{key.Label} · ช่องทาง {SlipAutoRecordRule.ChannelLabel(sourceKind)} · รอรายการเดินบัญชียืนยันภายใน {dueTh.AddDays(-1):dd/MM/yyyy} — ไม่พบ ⇒ ระบบแจ้งเตือนและถอนความไว้ใจ",
                "/pages/documents.html?openDoc=" + intent.SourceId, intentId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // เงินลงแล้ว (สถานะจริง = รับเงินแล้ว) ⇒ ขั้นตามหลังล้ม = LogError ไม่ประทับกลับทิศ (ฝ่ายค้าน 282 MED-1)
            _logger.LogError(ex, "รับเงินล่วงหน้าของรายการ {Intent} สำเร็จแล้ว แต่ประทับผล/แจ้งไม่สำเร็จ", intentId);
        }
        return new(true, verdict.Code, reason);
    }

    // ═══════════════ ผู้ขายดู/ถอน/ให้คืน ═══════════════

    public sealed record Row(Guid Id, string Label, string? ContactName, string? ReceivingAccountName, decimal MaxConfirmedAmount, decimal PerSlipCeiling,
        int ConfirmedCount, DateTime FirstTrustedAt, DateTime LastConfirmedAt, bool Revoked, DateTime? RevokedAt, string? RevokedByLabel, string? RevokeReason,
        string StatusLabel);

    public async Task<IReadOnlyList<Row>> ListAsync(Guid companyId, CancellationToken ct = default)
    {
        var rows = await (from t in _db.TrustedSlipPayers.AsNoTracking()
                          where t.CompanyId == companyId
                          join c in _db.Contacts.AsNoTracking().IgnoreQueryFilters().Where(c => c.CompanyId == companyId) on t.ContactId equals c.Id into cs
                          from c in cs.DefaultIfEmpty()
                          join b in _db.BankAccounts.AsNoTracking().IgnoreQueryFilters().Where(b => b.CompanyId == companyId) on t.ReceivingBankAccountId equals b.Id into bs
                          from b in bs.DefaultIfEmpty()
                          orderby t.LastConfirmedAt descending
                          select new { t, ContactName = c == null ? null : c.Name, BankName = b == null ? null : b.AccountName })
            .Take(500).ToListAsync(ct);
        return rows.Select(x => new Row(x.t.Id, x.t.Label, x.ContactName, x.BankName, x.t.MaxConfirmedAmount, TrustedSlipPayerRule.PerSlipCeiling(x.t.MaxConfirmedAmount),
            x.t.ConfirmedCount, x.t.FirstTrustedAt, x.t.LastConfirmedAt, x.t.RevokedAt != null, x.t.RevokedAt,
            x.t.RevokedBy == null ? null : x.t.RevokedBy == "system" ? "ระบบ (ตรวจย้อนไม่พบเงินเข้า)" : "ผู้ใช้", x.t.RevokeReason,
            x.t.RevokedAt != null ? "ถอนความไว้ใจแล้ว" : "ไว้ใจ (บันทึกล่วงหน้าได้เมื่อเปิดค่าตั้ง)")).ToList();
    }

    /// <summary>ผู้ขายถอน (<paramref name="trust"/> false) / ให้คืน (true) ความไว้ใจรายกุญแจ + audit ในธุรกรรมเดียว · คืน false = ไม่พบแถวของบริษัทนี้</summary>
    public async Task<bool> SetTrustAsync(Guid companyId, Guid rowId, bool trust, Guid userId, string? reason, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var row = await _db.TrustedSlipPayers.FirstOrDefaultAsync(t => t.Id == rowId && t.CompanyId == companyId, ct);
        if (row == null) return false;
        var before = new { revokedAt = row.RevokedAt, row.RevokedBy, row.RevokeReason };
        if (trust) { row.RevokedAt = null; row.RevokedBy = null; row.RevokeReason = null; }
        else
        {
            row.RevokedAt = now;
            row.RevokedBy = "user:" + userId.ToString("N")[..12];
            row.RevokeReason = LineMessageLimits.Truncate(string.IsNullOrWhiteSpace(reason) ? "ผู้ขายถอนความไว้ใจ" : reason.Trim(), 500);
        }
        row.UpdatedAt = now;
        row.UpdatedBy = userId.ToString();
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "TrustedSlipPayer", EntityId = row.Id.ToString(),
            OldValues = JsonSerializer.Serialize(before),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = trust ? "TrustedPayerReinstated" : "TrustedPayerRevoked", reason = row.RevokeReason, label = row.Label,
                ruleCode = TrustedSlipPayerRule.RuleCode, legalReference = TrustedSlipPayerRule.LegalReference,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task NotifyAsync(Guid companyId, string eventKey, string title, string message, string url, Guid entityId, CancellationToken ct)
    {
        if (_notify == null) return;
        try
        {
            await _notify.DispatchAsync(companyId, eventKey, new NotificationContext
            {
                Title = title, Message = message, ActionUrl = url, EntityType = "PaymentIntent", EntityId = entityId,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ct;
            _logger.LogError(ex, "แจ้งบริษัท {Company} เรื่องรับเงินล่วงหน้า ({Event}) ไม่สำเร็จ — สถานะบันทึกบนรายการชำระแล้ว", companyId, eventKey);
        }
    }
}
