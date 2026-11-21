using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **รอบ 286 — ผู้จ่ายที่ไว้ใจได้: เรียนรู้จากรายการเดินบัญชีจริง · ตรวจย้อนรายการรับเงินล่วงหน้า · ถอนความไว้ใจเมื่อไม่พบเงินเข้า** (คำตัดสิน 286-2/286-4/286-5)
/// <list type="bullet">
/// <item><see cref="LearnAsync"/>: กุญแจบนรายการชำระ (<c>PaymentIntent.TrustKeyHash</c>) เกิดความไว้ใจเฉพาะเมื่อเอกสารมีการชำระยอดเดียวกันที่<b>ผูกกับรายการเดินบัญชีเงินเข้า</b>
/// ของบัญชีผู้รับในกุญแจ (ตัดชำระจากรายการเดินบัญชี หรือผู้ขายยืนยันแล้วรายการเดินบัญชีถูกจับคู่ภายหลัง) — การกดยืนยันอย่างเดียวไม่นับ</item>
/// <item><see cref="TryVerifyLineAsync"/>: ผู้เรียก <c>BankReceiptSettleService.EvaluateLineAsync</c> (งานตัดชำระเดิม — ก่อนหาใบค้าง) — รายการเดินบัญชีที่ตรงรายการล่วงหน้า
/// ⇒ ผูกกับการชำระเดิม (ไม่สร้างการชำระที่สอง) + ประทับ "ยืนยันแล้ว"</item>
/// <item><see cref="SweepAsync"/>: ผู้เรียก <c>AutopilotJob</c> ขั้น TrustedSlipPayers — เรียนกุญแจที่พิสูจน์แล้ว · ตรวจย้อนรายการล่วงหน้า · ครบกำหนดไม่พบ ⇒ แจ้งแรง + ถอนความไว้ใจ
/// (ไม่ยกเลิกการรับเงินเอง — ผู้ขายเลือก)</item>
/// <item>ทุก query ผูกบริษัทของแถว · ไม่มี AI (kill-switch)</item>
/// </list>
/// </summary>
public sealed class TrustedSlipVerifyService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<TrustedSlipVerifyService> _logger;
    private readonly INotificationEngine? _notify;

    public TrustedSlipVerifyService(AccountingDbContext db, ILogger<TrustedSlipVerifyService> logger, INotificationEngine? notify = null)
    { _db = db; _logger = logger; _notify = notify; }

    public const string Actor = "slip-trusted-verify";
    public const int Batch = 200;
    /// <summary>เรียนจากรายการชำระที่สร้างไม่เกินกี่วัน (รายการเดินบัญชีมักตามมาภายในสัปดาห์)</summary>
    public const int LearnLookbackDays = 60;
    /// <summary>รายการเดินบัญชีลงวันที่ได้ตั้งแต่ 1 วันก่อน ถึง 7 วันหลังวันที่บนสลิป (โอนข้ามคืน/วันหยุด)</summary>
    public const int LineBeforeDays = 1;
    public const int LineAfterDays = 7;

    private static readonly string[] LinkBlocked = { BankReceiptAutoSettle.Stamps.Applying, BankReceiptAutoSettle.Stamps.Settled };

    // ═══════════════ เรียนรู้ ═══════════════

    /// <summary>รายการที่มีหลักฐานเงินเข้าจริง: การชำระของเอกสาร ยอดเท่าสลิป สร้างหลังสลิป ผูกกับรายการเดินบัญชีของบัญชีผู้รับในกุญแจ (ตัวตั้งเดียวของการเรียน)</summary>
    private IQueryable<PaymentIntent> Proven(IQueryable<PaymentIntent> q)
        => q.Where(i => i.TrustKeyHash != null && i.TrustKeyAccountId != null && i.TrustLearnedAt == null && i.TrustFailedAt == null
            && (i.TrustProvisionalAt == null || i.TrustVerifiedAt != null)
            && _db.Payments.IgnoreQueryFilters().Any(p => p.CompanyId == i.CompanyId && p.DocumentId == i.SourceId && !p.IsDeleted
                && p.Amount == i.Amount && p.CreatedAt >= i.CreatedAt
                && _db.BankTransactions.IgnoreQueryFilters().Any(t => t.CompanyId == i.CompanyId && t.MatchedPaymentId == p.Id && !t.IsDeleted
                    && t.TransactionType == BankTransactionType.Deposit && t.BankAccountId == i.TrustKeyAccountId
                    && (t.Amount == i.Amount || t.Amount == -i.Amount))));

    /// <summary>เรียนกุญแจของรายการนี้ถ้ามีหลักฐานรายการเดินบัญชี (ครั้งเดียวต่อรายการ — compare-and-set) · คืน true = เรียนแล้ว</summary>
    public async Task<bool> LearnAsync(Guid companyId, Guid intentId, DateTime nowUtc, CancellationToken ct = default)
    {
        var i = await Proven(_db.PaymentIntents.AsNoTracking().IgnoreQueryFilters().Where(x => x.Id == intentId && x.CompanyId == companyId))
            .Select(x => new { x.TrustKeyHash, x.TrustKeyLabel, x.TrustKeyAccountId, x.Amount, x.SourceId }).FirstOrDefaultAsync(ct);
        if (i == null) return false;
        var contactId = await _db.Documents.AsNoTracking().IgnoreQueryFilters().Where(d => d.Id == i.SourceId && d.CompanyId == companyId)
            .Select(d => (Guid?)d.ContactId).FirstOrDefaultAsync(ct);
        if (contactId == null) return false;

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var claimed = await _db.PaymentIntents.IgnoreQueryFilters()
                .Where(x => x.Id == intentId && x.CompanyId == companyId && x.TrustLearnedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.TrustLearnedAt, (DateTime?)nowUtc), ct);
            if (claimed == 0) { await tx.RollbackAsync(ct); return false; }
            // แถวกุญแจหนึ่งแถวต่อบริษัท × ลายนิ้วมือ (unique) — สร้างถ้ายังไม่มี (ON CONFLICT) แล้วล็อกแถวก่อนแก้ ⇒ สองรายการพร้อมกันนับครบ ไม่ชน unique
            var hash = i.TrustKeyHash!;
            var label = i.TrustKeyLabel ?? "";
            var account = i.TrustKeyAccountId!.Value;
            await _db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "TrustedSlipPayers" ("Id", "CompanyId", "ContactId", "KeyHash", "Label", "ReceivingBankAccountId", "MaxConfirmedAmount", "ConfirmedCount",
                    "FirstTrustedAt", "LastConfirmedAt", "LearnedFromIntentId", "CreatedAt", "CreatedBy", "IsDeleted")
                VALUES ({Guid.NewGuid()}, {companyId}, {contactId.Value}, {hash}, {label}, {account}, {i.Amount}, 0, {nowUtc}, {nowUtc}, {intentId}, {nowUtc}, {Actor}, false)
                ON CONFLICT ("CompanyId", "KeyHash") DO NOTHING
                """, ct);
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"TrustedSlipPayers\" WHERE \"CompanyId\" = {companyId} AND \"KeyHash\" = {hash} FOR UPDATE", ct);
            var row = await _db.TrustedSlipPayers.IgnoreQueryFilters().FirstAsync(t => t.CompanyId == companyId && t.KeyHash == hash, ct);
            var created = row.ConfirmedCount == 0;
            // ถูกถอนแล้วคงถอน (ผู้ขายให้คืนเองที่หน้าตั้งค่า) — นับหลักฐานเพิ่มได้
            row.MaxConfirmedAmount = created ? i.Amount : Math.Max(row.MaxConfirmedAmount, i.Amount);
            row.ConfirmedCount += 1;
            row.LastConfirmedAt = nowUtc;
            if (!created) { row.UpdatedAt = nowUtc; row.UpdatedBy = Actor; }
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = null, UserEmail = Actor, Action = created ? AuditAction.Create : AuditAction.Update,
                EntityType = "TrustedSlipPayer", EntityId = row.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new
                {
                    @event = created ? "TrustedPayerLearned" : "TrustedPayerConfirmedAgain", intentId, amount = i.Amount, maxConfirmed = row.MaxConfirmedAmount,
                    count = row.ConfirmedCount, revoked = row.RevokedAt != null, label = row.Label,
                    ruleCode = TrustedSlipPayerRule.RuleCode, legalReference = TrustedSlipPayerRule.LegalReference, usedAi = false,
                }),
                Timestamp = nowUtc,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
    }

    // ═══════════════ ตรวจย้อนรายการล่วงหน้า ═══════════════

    private sealed record ProvisionalRow(Guid Id, Guid CompanyId, Guid SourceId, decimal Amount, Guid AccountId, DateTime? PaidAt, DateTime ProvisionalAt,
        string? SlipBankRef, string? KeyHash, DateTime? FailedAt);

    /// <summary>รายการเดินบัญชีเงินเข้าหนึ่งรายการ ⇒ ตรงรายการรับเงินล่วงหน้าที่ยังไม่ยืนยันไหม (บัญชี · ยอด · วันในช่วง · เลขอ้างอิงธนาคารก่อน) · ตรงหนึ่งเดียว ⇒ ผูกกับการชำระเดิม ·
    /// คืน true = บรรทัดนี้ถูกใช้ยืนยันแล้ว (ผู้เรียกไม่ต้องหาใบค้างต่อ)</summary>
    public async Task<bool> TryVerifyLineAsync(Guid companyId, BankTransaction line, DateTime nowUtc, CancellationToken ct = default)
    {
        if (line.TransactionType != BankTransactionType.Deposit || line.MatchedPaymentId != null || line.MatchedJournalEntryId != null
            || line.ReconciliationStatus != ReconciliationStatus.Unmatched) return false;
        var amount = Math.Abs(line.Amount);
        var from = line.TransactionDate.Date.AddDays(-LineAfterDays);
        var to = line.TransactionDate.Date.AddDays(LineBeforeDays + 1);
        var cands = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.TrustProvisionalAt != null && i.TrustVerifiedAt == null
                && i.TrustKeyAccountId == line.BankAccountId && i.Amount == amount && i.PaidAt >= from && i.PaidAt < to)
            .Select(i => new ProvisionalRow(i.Id, i.CompanyId, i.SourceId, i.Amount, i.TrustKeyAccountId!.Value, i.PaidAt, i.TrustProvisionalAt!.Value, i.SlipBankRef,
                i.TrustKeyHash, i.TrustFailedAt))
            .Take(5).ToListAsync(ct);
        var pick = Pick(cands, line.Reference);
        if (pick == null) return false;
        return await LinkAsync(pick, line.Id, nowUtc, ct);
    }

    /// <summary>เลขอ้างอิงธนาคารตรงหนึ่งเดียว ⇒ ตัวนั้น · ไม่มีใครตรงเลขและมีผู้สมัครเดียว ⇒ ตัวนั้น · อื่น ๆ ⇒ ไม่เดา</summary>
    private static ProvisionalRow? Pick(IReadOnlyList<ProvisionalRow> cands, string? lineReference)
    {
        var byRef = cands.Where(c => SlipEvidenceRule.RefMatchesLine(c.SlipBankRef, lineReference)).ToList();
        if (byRef.Count == 1) return byRef[0];
        if (byRef.Count > 1) return null;
        return cands.Count == 1 ? cands[0] : null;
    }

    /// <summary>ผูกบรรทัดกับการชำระของรายการล่วงหน้า + ประทับยืนยัน + audit ในธุรกรรมเดียว · การชำระต้องยังอยู่ ยอด/บัญชีตรง</summary>
    private async Task<bool> LinkAsync(ProvisionalRow p, Guid lineId, DateTime nowUtc, CancellationToken ct)
    {
        var companyId = p.CompanyId;
        var reference = GatewayPaymentRecord.Reference(p.Id);
        var pay = await _db.Payments.AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.CompanyId == companyId && x.Reference == reference && !x.IsDeleted)
            .Select(x => new { x.Id, x.Amount, x.BankAccountId, x.OverrideBankAccountId, x.DocumentId }).FirstOrDefaultAsync(ct);
        if (pay == null || pay.Amount != p.Amount || (pay.BankAccountId != p.AccountId && pay.OverrideBankAccountId != p.AccountId)) return false;

        var why = "ยืนยันรายการรับเงินล่วงหน้าจากผู้จ่ายที่ไว้ใจได้ด้วยรายการเดินบัญชีนี้ (ยอด/บัญชี/วันตรง)";
        var strategy = _db.Database.CreateExecutionStrategy();
        var linked = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var n = await _db.BankTransactions
                .Where(t => t.Id == lineId && t.CompanyId == companyId && !t.IsDeleted && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                    && t.MatchedPaymentId == null && t.MatchedJournalEntryId == null && (t.AutoSettleVerdict == null || !LinkBlocked.Contains(t.AutoSettleVerdict)))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.MatchedPaymentId, (Guid?)pay.Id)
                    .SetProperty(t => t.ReconciliationStatus, ReconciliationStatus.Matched)
                    .SetProperty(t => t.ReconciledAt, (DateTime?)nowUtc)
                    .SetProperty(t => t.ReconciledBy, Actor)
                    .SetProperty(t => t.MatchRuleCode, TrustedSlipPayerRule.RuleCode)
                    .SetProperty(t => t.MatchReason, why)
                    .SetProperty(t => t.AutoSettleVerdict, BankReceiptAutoSettle.Stamps.Settled)
                    .SetProperty(t => t.AutoSettleReason, why)
                    .SetProperty(t => t.AutoSettleDocumentId, (Guid?)pay.DocumentId), ct);
            if (n == 0) { await tx.RollbackAsync(ct); return false; }
            await _db.PaymentIntents.Where(i => i.Id == p.Id && i.CompanyId == companyId && i.TrustVerifiedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.TrustVerifiedAt, (DateTime?)nowUtc)
                    .SetProperty(i => i.AutoRecordReason, (string?)("รับเงินล่วงหน้าแล้ว — รายการเดินบัญชียืนยันเงินเข้าแล้ว" + (p.FailedAt != null ? " (หลังครบกำหนด)" : ""))), ct);
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = null, UserEmail = Actor, Action = AuditAction.Update, EntityType = "PaymentIntent", EntityId = p.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new
                {
                    @event = "SlipTrustedVerified", bankTransactionId = lineId, paymentId = pay.Id, amount = p.Amount, late = p.FailedAt != null,
                    ruleCode = TrustedSlipPayerRule.RuleCode, legalReference = TrustedSlipPayerRule.LegalReference, usedAi = false,
                }),
                Timestamp = nowUtc,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
        if (!linked) return false;
        if (p.FailedAt == null) await LearnAsync(companyId, p.Id, nowUtc, ct);
        else
            await NotifyAsync(companyId, NotificationEvents.AutopilotSlipTrustedProvisional,
                $"พบเงินเข้า {p.Amount:N2} บาท ของรายการรับเงินล่วงหน้าแล้ว (หลังครบกำหนด)",
                "รายการเดินบัญชียืนยันแล้ว — ความไว้ใจของผู้โอนนี้ยังถูกถอนอยู่ ให้คืนได้ที่หน้าตั้งค่า LINE ถ้าตรวจแล้วถูกต้อง",
                "/pages/documents.html?openDoc=" + pay.DocumentId, p.Id, ct);
        return true;
    }

    /// <summary>หนึ่งรอบ (ทุกบริษัท · ใต้ JobLock ของขั้น) — เรียนกุญแจที่พิสูจน์แล้ว · ตรวจย้อนรายการล่วงหน้า · คืนจำนวนงานที่ทำ</summary>
    public async Task<int> SweepAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var n = 0;
        var since = nowUtc.AddDays(-LearnLookbackDays);
        var learn = await Proven(_db.PaymentIntents.AsNoTracking().IgnoreQueryFilters().Where(i => i.CreatedAt >= since))
            .OrderBy(i => i.CreatedAt).Select(i => new { i.Id, i.CompanyId }).Take(Batch).ToListAsync(ct);
        foreach (var l in learn)
            if (await LearnAsync(l.CompanyId, l.Id, nowUtc, ct)) n++;

        var pending = await _db.PaymentIntents.AsNoTracking().IgnoreQueryFilters()
            .Where(i => i.TrustProvisionalAt != null && i.TrustVerifiedAt == null && i.TrustFailedAt == null && i.TrustKeyAccountId != null)
            .OrderBy(i => i.TrustReviewDueAt).Take(Batch)
            .Select(i => new { P = new ProvisionalRow(i.Id, i.CompanyId, i.SourceId, i.Amount, i.TrustKeyAccountId!.Value, i.PaidAt, i.TrustProvisionalAt!.Value, i.SlipBankRef,
                i.TrustKeyHash, i.TrustFailedAt), i.TrustReviewDueAt })
            .ToListAsync(ct);
        foreach (var r in pending)
        {
            if (await VerifyOneAsync(r.P, r.TrustReviewDueAt, nowUtc, ct)) n++;
        }
        return n;
    }

    /// <summary>หนึ่งรายการล่วงหน้า: ผูกแล้วทางอื่น ⇒ ยืนยัน · หาเจอ ⇒ ผูก · ผู้ขายยกเลิกการรับเงิน หรือครบกำหนดไม่พบ ⇒ ล้ม + ถอนความไว้ใจ + แจ้งแรง</summary>
    private async Task<bool> VerifyOneAsync(ProvisionalRow p, DateTime? dueUtc, DateTime nowUtc, CancellationToken ct)
    {
        var companyId = p.CompanyId;
        var reference = GatewayPaymentRecord.Reference(p.Id);
        var pay = await _db.Payments.AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.CompanyId == companyId && x.Reference == reference && !x.IsDeleted)
            .Select(x => new { x.Id, x.DocumentId }).FirstOrDefaultAsync(ct);
        if (pay == null)
            return await FailAsync(p, "ผู้ขายยกเลิกการรับเงินรายการนี้แล้ว (ไม่พบการชำระ) — ถอนความไว้ใจของผู้โอนนี้", nowUtc, ct);

        // คน/ทางอื่นจับคู่รายการเดินบัญชีกับการชำระนี้แล้ว (หน้ากระทบยอด) ⇒ ยืนยัน (ผลจริง ไม่ประทับเอง)
        if (await _db.BankTransactions.AsNoTracking().AnyAsync(t => t.CompanyId == companyId && t.MatchedPaymentId == pay.Id && !t.IsDeleted
                && t.BankAccountId == p.AccountId && (t.Amount == p.Amount || t.Amount == -p.Amount), ct))
        {
            var n = await _db.PaymentIntents.Where(i => i.Id == p.Id && i.CompanyId == companyId && i.TrustVerifiedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.TrustVerifiedAt, (DateTime?)nowUtc)
                    .SetProperty(i => i.AutoRecordReason, (string?)"รับเงินล่วงหน้าแล้ว — รายการเดินบัญชียืนยันเงินเข้าแล้ว"), ct);
            if (n > 0) await LearnAsync(companyId, p.Id, nowUtc, ct);
            return n > 0;
        }

        if (p.PaidAt is DateTime day)
        {
            var from = day.Date.AddDays(-LineBeforeDays);
            var to = day.Date.AddDays(LineAfterDays + 1);
            var lines = await _db.BankTransactions.AsNoTracking()
                .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.BankAccountId == p.AccountId && t.TransactionType == BankTransactionType.Deposit
                    && (t.Amount == p.Amount || t.Amount == -p.Amount) && t.TransactionDate >= from && t.TransactionDate < to
                    && t.ReconciliationStatus == ReconciliationStatus.Unmatched && t.MatchedPaymentId == null && t.MatchedJournalEntryId == null
                    && (t.AutoSettleVerdict == null || !LinkBlocked.Contains(t.AutoSettleVerdict)))
                .Select(t => new { t.Id, t.Reference }).Take(3).ToListAsync(ct);
            var byRef = lines.Where(l => SlipEvidenceRule.RefMatchesLine(p.SlipBankRef, l.Reference)).ToList();
            var line = byRef.Count == 1 ? byRef[0] : byRef.Count == 0 && lines.Count == 1 ? lines[0] : null;
            if (line != null && await LinkAsync(p, line.Id, nowUtc, ct)) return true;
        }

        if (dueUtc is DateTime due && nowUtc >= due)
            return await FailAsync(p, "ครบกำหนดแล้วยังไม่พบรายการเดินบัญชีเงินเข้า (ยอด/บัญชี/วันตรง) — อาจเป็นสลิปที่แต่งขึ้น หรือยังไม่ได้นำเข้ารายการเดินบัญชี", nowUtc, ct);
        return false;
    }

    /// <summary>ล้ม: ประทับบนรายการ + ถอนความไว้ใจของกุญแจ + audit ในธุรกรรมเดียว (compare-and-set) แล้วแจ้งแรง · <b>ไม่ยกเลิกการรับเงินเอง</b></summary>
    private async Task<bool> FailAsync(ProvisionalRow p, string reason, DateTime nowUtc, CancellationToken ct)
    {
        var companyId = p.CompanyId;
        var strategy = _db.Database.CreateExecutionStrategy();
        var failed = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var r = LineMessageLimits.Truncate(reason, 500);
            var n = await _db.PaymentIntents.IgnoreQueryFilters()
                .Where(i => i.Id == p.Id && i.CompanyId == companyId && i.TrustFailedAt == null && i.TrustVerifiedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.TrustFailedAt, (DateTime?)nowUtc)
                    .SetProperty(i => i.AutoRecordCode, TrustedSlipPayerRule.Codes.Unverified)
                    .SetProperty(i => i.AutoRecordReason, r), ct);
            if (n == 0) { await tx.RollbackAsync(ct); return false; }
            var revoked = p.KeyHash == null ? 0 : await _db.TrustedSlipPayers.IgnoreQueryFilters()
                .Where(t => t.CompanyId == companyId && t.KeyHash == p.KeyHash && t.RevokedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.RevokedAt, (DateTime?)nowUtc).SetProperty(t => t.RevokedBy, "system")
                    .SetProperty(t => t.RevokeReason, r).SetProperty(t => t.UpdatedAt, (DateTime?)nowUtc), ct);
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, UserId = null, UserEmail = Actor, Action = AuditAction.Update, EntityType = "PaymentIntent", EntityId = p.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new
                {
                    @event = "SlipTrustedUnverified", reason, trustRevoked = revoked > 0, amount = p.Amount,
                    ruleCode = TrustedSlipPayerRule.RuleCode, legalReference = TrustedSlipPayerRule.LegalReference, usedAi = false,
                }),
                Timestamp = nowUtc,
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
        if (!failed) return false;
        var number = await _db.Documents.AsNoTracking().IgnoreQueryFilters().Where(d => d.Id == p.SourceId && d.CompanyId == companyId)
            .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct);
        await NotifyAsync(companyId, NotificationEvents.AutopilotSlipTrustedUnverified,
            $"⚠️ ไม่พบเงินเข้า {p.Amount:N2} บาท ของ {number ?? "เอกสาร"} ที่ระบบรับเงินล่วงหน้าไว้",
            reason + " · ระบบถอนความไว้ใจของผู้โอนนี้แล้ว · ตรวจรายการเดินบัญชี — ถ้าเงินไม่เข้าจริง ให้ยกเลิกการรับเงินนี้ที่เอกสาร (ใบเสร็จ/ใบกำกับที่ออกต้องออกใบยกเลิก) · ระบบไม่ยกเลิกเอง",
            "/pages/documents.html?openDoc=" + p.SourceId, p.Id, ct);
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
            _logger.LogError(ex, "แจ้งบริษัท {Company} เรื่องผู้จ่ายที่ไว้ใจได้ ({Event}) ไม่สำเร็จ — สถานะ/เหตุบันทึกบนรายการชำระแล้ว", companyId, eventKey);
        }
    }
}
