using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 264 P4 (team-264-a2b-design §5.4 · G1 · คำตัดสิน 264-5-10) — <b>ผู้ป้อนชั้นรายการเดินบัญชี</b> ของ <see cref="PartnerPayablePipeline.IBankLineEvidence"/> ·
/// อ่าน<b>ตารางรายการเดินบัญชีเดิม</b> (<c>BankTransactions</c> — ไม่มีตารางบรรทัดธนาคารที่สอง) ของบริษัทผู้ซื้อ แล้วตัดสินด้วย <see cref="BankPaymentLineMatch.Decide"/> ตัวเดียว
/// <list type="bullet">
/// <item><see cref="DecideAsync"/>/<see cref="FindAsync"/> = <b>อ่านอย่างเดียว</b> (ผู้เรียก: ท่อ + หน้า) · <see cref="BindAsync"/> = <b>ตัวเขียนเดียว</b> ผูกบรรทัดกับ PV
/// ทุกทางที่ปิดการแจ้งด้วย PV (ท่อ · ปุ่มเดียว · PV ของแผนแจ้งโอน · ตาข่าย — ฝ่ายค้าน 264-P4 M1) · หนึ่งบรรทัดเป็นหลักฐานได้หนึ่งการจ่าย</item>
/// <item>เลขบัญชีรับเงินของผู้ขาย = บัญชีของการรับชำระฝั่งผู้ขาย (<c>Payment.BankAccountId</c> ในบริษัทผู้ขาย — อ่านด้วย CompanyId ผู้ขายทุก query) · ใช้เทียบเท่านั้น ไม่ส่งออก/ไม่เก็บ</item>
/// <item>ไม่มีจุดเรียก AI ⇒ ไม่มี AiFeatureKey · kill-switch ไม่มีผล</item>
/// </list>
/// </summary>
public sealed class BankPaymentLineEvidence : PartnerPayablePipeline.IBankLineEvidence
{
    private readonly AccountingDbContext _db;
    private readonly IPermissionService? _perms;

    /// <summary>การแจ้งอื่นที่เปิดอยู่ที่นำมาเทียบความเป็นหนึ่งต่อหนึ่ง (บริษัทเดียวกัน · ยอดเท่ากัน · กรอบวันเดียวกัน)</summary>
    public const int MaxOthers = 50;

    public BankPaymentLineEvidence(AccountingDbContext db, IPermissionService? perms = null) { _db = db; _perms = perms; }

    /// <summary>ผลตัดสินของการแจ้งหนึ่งแถว (อ่านอย่างเดียว · tenant = <paramref name="companyId"/> ทุก query ของบริษัทผู้ซื้อ)</summary>
    public async Task<BankPaymentLineMatch.Result> DecideAsync(Guid companyId, PartnerPaymentNotice n, CancellationToken ct = default)
    {
        var payee = await PayeeDigitsAsync(_db, n, ct);
        var target = new BankPaymentLineMatch.Target(n.Id, n.CashAmount, n.PaidAt, payee);
        if (payee == null) return BankPaymentLineMatch.Decide(target, Array.Empty<BankPaymentLineMatch.Target>(), Array.Empty<BankPaymentLineMatch.Line>());
        var span = BankPaymentLineMatch.DateWindowDays + 1;
        var from = n.PaidAt.Date.AddDays(-span);
        var to = n.PaidAt.Date.AddDays(span + 1);
        var cash = Math.Round(n.CashAmount, 2, MidpointRounding.AwayFromZero);
        var rows = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.BankAccount.CompanyId == companyId
                && t.TransactionDate >= from && t.TransactionDate < to
                && (t.TransactionType == BankTransactionType.Withdrawal || t.TransactionType == BankTransactionType.Transfer)
                && (t.Amount == cash || t.Amount == -cash))
            .Select(t => new
            {
                t.Id, t.BankAccountId, t.TransactionDate, t.TransactionType, t.Amount, t.Description, t.Reference, t.Payee, t.EntrySource, t.CreatedByUserId,
                Unmatched = t.ReconciliationStatus == ReconciliationStatus.Unmatched && t.MatchedPaymentId == null && t.MatchedJournalEntryId == null
                    && t.ReconciliationGroupId == null && t.AutoSettleDocumentId == null,
            })
            .ToListAsync(ct);
        var docType = await _db.Documents.AsNoTracking().Where(d => d.Id == n.TargetDocumentId && d.CompanyId == companyId)
            .Select(d => (DocumentType?)d.DocumentType).FirstOrDefaultAsync(ct) ?? DocumentType.PurchaseInvoice;
        var lines = new List<BankPaymentLineMatch.Line>();
        foreach (var r in rows)
        {
            // กติกา H1 ตัวเดียวกับฝั่งเงินเข้า: ผู้นำเข้า statement ต้องมีสิทธิ์อนุมัติใบชนิดนั้น ณ ตอนนี้ (ไม่รู้ผู้นำเข้า = ไม่มีสิทธิ์)
            var mayApprove = r.CreatedByUserId is Guid uid && _perms != null && await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, uid, docType);
            lines.Add(new BankPaymentLineMatch.Line(r.Id, r.BankAccountId, r.TransactionDate, true, Math.Abs(r.Amount),
                string.Join(" ", new[] { r.Description, r.Reference, r.Payee }.Where(x => !string.IsNullOrWhiteSpace(x))), r.Unmatched,
                BankReceiptAutoSettle.EvidenceProblem(r.EntrySource, mayApprove)));
        }
        var others = new List<BankPaymentLineMatch.Target>();
        if (lines.Count > 0)
        {
            // ฝ่ายค้าน 264-P4 M1: นับทั้งการแจ้งที่ยังเปิด และการแจ้งที่บันทึกแล้วแต่ PV ยังไม่ผูกบรรทัดใด (เงินก้อนนั้นอาจเป็นบรรทัดนี้ — บรรทัดเดียวเป็นหลักฐานได้การจ่ายเดียว)
            var near = await _db.PartnerPaymentNotices.AsNoTracking()
                .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.Id != n.Id && x.SellerVoidedAt == null && x.CashAmount == n.CashAmount
                    && (x.Status == PartnerPaymentNoticeRule.StatusOpen || x.Status == PartnerPaymentNoticeRule.StatusRecording
                        || (x.Status == PartnerPaymentNoticeRule.StatusRecorded && x.RecordedPaymentId != null
                            && !_db.BankTransactions.Any(t => t.CompanyId == companyId && !t.IsDeleted && t.MatchedPaymentId == x.RecordedPaymentId)))
                    && x.PaidAt >= from.AddDays(-span) && x.PaidAt < to.AddDays(span))
                .OrderBy(x => x.PaidAt).Take(MaxOthers + 1).ToListAsync(ct);
            // ฝ่ายค้าน L4: เกินเพดาน ⇒ ตัดสินหนึ่งต่อหนึ่งไม่ได้ ⇒ คำแนะนำ (ไม่ตัดคู่ที่ชนกันทิ้งเงียบ ๆ)
            if (near.Count > MaxOthers)
                return new BankPaymentLineMatch.Result(BankPaymentLineMatch.Kind.Suggest, null, null, null, BankPaymentLineMatch.SharedLineReason, BankPaymentLineMatch.RuleSuggest);
            foreach (var o in near)
                others.Add(new BankPaymentLineMatch.Target(o.Id, o.CashAmount, o.PaidAt, await PayeeDigitsAsync(_db, o, ct)));
        }
        return BankPaymentLineMatch.Decide(target, others, lines);
    }

    public async Task<(Guid? BankAccountId, string? Label)> FindAsync(Guid companyId, PartnerPaymentNotice notice, CancellationToken ct)
    {
        var r = await DecideAsync(companyId, notice, ct);
        return r.IsEvidence ? (r.BankAccountId, LabelOf(r)) : (null, null);
    }

    /// <summary>ฝ่ายค้าน 264-P4 L5: วันที่ของบรรทัดที่เป็นหลักฐาน (ผล Match เท่านั้น)</summary>
    public async Task<DateTime?> EvidenceDateAsync(Guid companyId, PartnerPaymentNotice notice, CancellationToken ct)
    {
        var r = await DecideAsync(companyId, notice, ct);
        return r.IsEvidence ? r.LineDate : null;
    }

    /// <summary>ป้ายของบรรทัด (วันที่ไทย) — ไม่มีเลขบัญชีของผู้ขาย</summary>
    public static string LabelOf(BankPaymentLineMatch.Result r)
        => r.LineDate is DateTime d ? "วันที่ " + (d.TimeOfDay == TimeSpan.Zero ? d : d.AddHours(7)).ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) : "";

    /// <summary>ตัวเขียนเดียว: ท่อบันทึกจ่ายจากหลักฐานบรรทัดแล้ว ⇒ ผูกบรรทัดกับการจ่ายนั้น (เงื่อนไข: ยังไม่จับคู่ — ชนกับคนจับคู่มือได้คนเดียว) + audit chain ·
    /// คืน 1 = ผูกแล้ว · 0 = บรรทัดถูกจับคู่ไปก่อน/ไม่ตรงแล้ว (ผู้เรียกบันทึกเหตุบนแถว — ไม่เงียบ)</summary>
    public async Task<int> BindAsync(Guid companyId, PartnerPaymentNotice notice, Guid paymentId, CancellationToken ct)
    {
        var r = await DecideAsync(companyId, notice, ct);
        if (!r.IsEvidence || r.LineId is not Guid lineId) return 0;
        // ฝ่ายค้าน 264-P4 M1: ผูกเฉพาะเมื่อ PV ตัดบัญชีเดียวกับบรรทัด (คนเลือกบัญชีอื่น/เงินสด ⇒ ไม่ผูก — บรรทัดนั้นไม่ใช่เงินก้อนนี้) · PV ของบริษัทผู้เรียก · ยังไม่ถูกผูกกับบรรทัดอื่น
        var payBank = await _db.Payments.AsNoTracking().Where(p => p.Id == paymentId && p.CompanyId == companyId && !p.IsDeleted)
            .Select(p => new { p.BankAccountId }).FirstOrDefaultAsync(ct);
        if (payBank == null || payBank.BankAccountId != r.BankAccountId) return 0;
        if (await _db.BankTransactions.AsNoTracking().AnyAsync(t => t.CompanyId == companyId && !t.IsDeleted && t.MatchedPaymentId == paymentId, ct)) return 0;
        var now = DateTime.UtcNow;
        var reason = BankPaymentLineMatch.MatchReason + " · การแจ้ง " + notice.SourceDocumentNumber;
        var n = await _db.BankTransactions
            .Where(t => t.Id == lineId && t.CompanyId == companyId && !t.IsDeleted && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && t.MatchedPaymentId == null && t.MatchedJournalEntryId == null && t.ReconciliationGroupId == null && t.AutoSettleDocumentId == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.ReconciliationStatus, ReconciliationStatus.Matched)
                .SetProperty(t => t.MatchedPaymentId, (Guid?)paymentId)
                .SetProperty(t => t.ReconciledAt, (DateTime?)now)
                .SetProperty(t => t.ReconciledBy, BankMatchAttribution.PartnerPayLine)
                .SetProperty(t => t.MatchRuleCode, BankPaymentLineMatch.RuleMatch)
                .SetProperty(t => t.MatchReason, reason)
                .SetProperty(t => t.UpdatedAt, (DateTime?)now), ct);
        if (n != 1) return 0;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(BankTransaction), EntityId = lineId.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = "BankLinePartnerPaymentMatched", noticeId = notice.Id, paymentId, ruleCode = BankPaymentLineMatch.RuleMatch,
                legalReference = PartnerPaymentPlanRule.LegalReference, reconciledBy = BankMatchAttribution.PartnerPayLine, usedAi = false,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        return 1;
    }

    /// <summary>เลขบัญชีรับเงินของผู้ขาย (ตัวเลขล้วน) จากการรับชำระฝั่งผู้ขาย — ทุก query ผูก CompanyId ผู้ขาย · ไม่พบ ⇒ null (ไม่ตัดสิน)</summary>
    internal static async Task<string?> PayeeDigitsAsync(AccountingDbContext db, PartnerPaymentNotice n, CancellationToken ct)
    {
        if (n.SellerPaymentId is not Guid sp) return null;
        var number = await db.Payments.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.Id == sp && p.CompanyId == n.SourceCompanyId && p.BankAccountId != null)
            .Join(db.BankAccounts.AsNoTracking().IgnoreQueryFilters().Where(b => b.CompanyId == n.SourceCompanyId), p => p.BankAccountId, b => (Guid?)b.Id, (p, b) => b.AccountNumber)
            .FirstOrDefaultAsync(ct);
        var digits = new string((number ?? "").Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= BankPaymentLineMatch.MinVisibleDigits ? digits : null;
    }
}
