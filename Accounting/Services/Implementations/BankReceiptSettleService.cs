using System.Globalization;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Autopilot;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **เงินโอนเข้าในรายการเดินบัญชี ⇒ ปิดหนี้ใบค้าง** (รอบ 252 WP-12A · AUTOPILOT §5 · รอบ 250 ของแผน · G-T1/G-T2/G-T3/G-T8)
/// <list type="bullet">
/// <item>ตัวตัดสินตัวเดียว <see cref="BankReceiptAutoSettle.Decide"/> (pure · ไม่มี AI) — ผู้สมัคร = ใบขายค้าง (ใบแจ้งหนี้/ใบกำกับ) ของบริษัทนี้ที่ยอดใกล้เงินเข้า ·
/// ยอดที่ต้องโอน/WHT จาก <see cref="PayLinkTransferWht"/> (สูตรเดียวกับลิงก์ชำระ)</item>
/// <item>AutoSettle (บริษัทเปิด — ค่าตั้งต้นปิด) ⇒ <c>IDocumentService.CreatePaymentAsync</c> (ทางเดียว · source <see cref="PaymentEntrySource.BankAutoSettle"/> ⇒ งานหลังรับชำระ
/// เข้าคิวในแกน) ⇒ ผูกบรรทัดกับการชำระ (Matched · <see cref="BankMatchAttribution.BankAutoSettle"/>) + audit chain · <b>ไม่มีทางประทับ "ชำระแล้ว" โดยไม่มีรายการเดินบัญชีจริง</b></item>
/// <item>Suggest ⇒ ประทับคำตัดสินบนบรรทัด (คิว "เงินเข้าที่รอยืนยัน" ในหน้า Autopilot) · คนกดยืนยัน ⇒ ทางเดียวกัน (ผู้บันทึก = คนกด)</item>
/// <item>ล้ม (งวดปิด · ยอดเปลี่ยน ฯลฯ) ⇒ ประทับ Failed + เหตุบนบรรทัด + แจ้งทันที (E-14b) · ใบยังค้าง · ล้มกลางทางหลังสร้างการชำระ ⇒ รอบหน้ากู้ด้วยเลขอ้างอิงการชำระ
/// <c>BANK:{lineId}</c> (ไม่สร้างซ้ำ)</item>
/// </list>
/// </summary>
public class BankReceiptSettleService : IBankReceiptSettleService
{
    /// <summary>แถวต่อรอบของงานเบื้องหลัง</summary>
    public const int BatchSize = 100;
    /// <summary>ประเมินเฉพาะรายการที่ลงวันที่ไม่เกินนี้ (เงินโอนเก่ากว่านี้ไม่เดาให้)</summary>
    public const int LookbackDays = 14;
    /// <summary>ประเมินซ้ำบรรทัดที่ยังไม่จับคู่ทุก ๆ เท่านี้ (ใบออกทีหลังเงินเข้า · สลิปมาทีหลัง)</summary>
    public static readonly TimeSpan ReevaluateAfter = TimeSpan.FromHours(6);
    public const int MaxCandidates = 30;
    /// <summary>บรรทัดที่ค้าง "Applying" โดยไม่มีการชำระนานเกินนี้ = ล้มก่อนสร้างการชำระ ⇒ ปลดให้ตัดสินใหม่ (ไม่ค้างตลอดกาล · ไม่แจ้งซ้ำทุกรอบ)</summary>
    public static readonly TimeSpan ApplyingStaleAfter = TimeSpan.FromMinutes(15);

    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly ILogger<BankReceiptSettleService> _logger;
    private readonly INotificationEngine? _notify;

    public BankReceiptSettleService(AccountingDbContext db, IDocumentService docs, ILogger<BankReceiptSettleService> logger, INotificationEngine? notify = null)
    {
        _db = db;
        _docs = docs;
        _logger = logger;
        _notify = notify;
    }

    /// <summary>เลขอ้างอิงของการชำระที่ระบบสร้างจากบรรทัดนี้ — กุญแจกันสร้างซ้ำ (กู้หลังล้มกลางทาง)</summary>
    public static string PaymentReference(Guid lineId) => "BANK:" + lineId.ToString("N");

    private static readonly DocumentType[] SaleTypes = { DocumentType.Invoice, DocumentType.TaxInvoice };
    private static readonly DocumentStatus[] OpenStatuses = { DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.PartiallyPaid, DocumentStatus.Overdue };

    // ═══════════════ ผู้สมัคร + คำตัดสิน ═══════════════

    private sealed record Candidate(BankReceiptAutoSettle.Candidate Facts, string? ContactName);

    private async Task<List<Candidate>> CandidatesAsync(Guid companyId, BankTransaction line, CancellationToken ct)
    {
        var amount = Math.Abs(line.Amount);
        var near = Math.Max(BankReceiptAutoSettle.NearAbsolute, Math.Round(amount * BankReceiptAutoSettle.NearPercent, 2, MidpointRounding.AwayFromZero));
        var lo = amount - near;
        var hi = amount + near;
        // ยอดเต็มก่อนหัก (ลูกค้าไม่หัก) ⇒ ยอดค้างต่ำกว่าเงินเข้าได้ถึง ~15% (WHT สูงสุดทั่วไป) — ตัวตัดสินเลือกจาก ExpectedGross เอง
        var loGross = Math.Round(amount * 0.85m, 2, MidpointRounding.AwayFromZero);
        var latest = line.TransactionDate.Date.AddDays(BankReceiptAutoSettle.DocumentAfterLineDays + 1);
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && SaleTypes.Contains(d.DocumentType) && OpenStatuses.Contains(d.Status)
                && d.BalanceDue > 0m && d.Currency == "THB" && d.DocumentDate < latest
                && ((d.BalanceDue >= lo && d.BalanceDue <= hi) || (d.WithholdingTaxAmount > 0m && d.BalanceDue >= loGross && d.BalanceDue < amount)))
            .OrderByDescending(d => d.DocumentDate).Take(MaxCandidates)
            .ToListAsync(ct);
        if (docs.Count == 0) return new List<Candidate>();

        var docIds = docs.Select(d => d.Id).ToList();
        var contactIds = docs.Select(d => d.ContactId).Distinct().ToList();
        var contacts = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.BankAutoSettleDisabled }).ToListAsync(ct);
        var intents = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.Document && docIds.Contains(i.SourceId))
            .Select(i => new { i.SourceId, i.Status, i.MethodKind, i.SlipBankRef }).ToListAsync(ct);

        var lineText = $"{line.Description} {line.Reference} {line.Payee}";
        var list = new List<Candidate>();
        foreach (var d in docs)
        {
            var fig = await PayLinkTransferWht.ForAsync(_db, companyId, d, d.BalanceDue, ct);
            var contact = contacts.FirstOrDefault(c => c.Id == d.ContactId);
            var score = BankMatchScorer.Score(new BankMatchScorer.Input(fig.ExpectedNet, amount, d.DocumentDate, line.TransactionDate,
                CandidateDocNumber: d.DocumentNumber, CandidateName: contact?.Name,
                BankDescription: line.Description, BankReference: line.Reference, BankPayee: line.Payee));
            var mine = intents.Where(i => i.SourceId == d.Id).ToList();
            list.Add(new Candidate(new BankReceiptAutoSettle.Candidate(
                d.Id, d.DocumentNumber, d.DocumentDate, fig.ExpectedNet, fig.ExpectedGross,
                CitesDocumentNumber: BankReceiptAutoSettle.CitesDocumentNumber(lineText, d.DocumentNumber),
                NameConfident: score.NameConfident,
                SlipRefMatched: mine.Any(i => i.MethodKind == PaymentMethodKind.ManualSlip
                    && SlipEvidenceRule.RefMatchesLine(i.SlipBankRef, line.Reference, line.Description)),
                HasPendingOnlinePayment: mine.Any(i => i.MethodKind != PaymentMethodKind.ManualSlip
                    && (i.Status == PaymentIntentStatus.Created || i.Status == PaymentIntentStatus.Pending)),
                ContactAutoSettleDisabled: contact?.BankAutoSettleDisabled ?? false), contact?.Name));
        }
        return list;
    }

    private async Task<(BankReceiptAutoSettle.Result Result, List<Candidate> Candidates)> DecideAsync(Guid companyId, BankTransaction line, bool enabled,
        CancellationToken ct)
    {
        var candidates = await CandidatesAsync(companyId, line, ct);
        var result = BankReceiptAutoSettle.Decide(
            new BankReceiptAutoSettle.Line(Math.Abs(line.Amount), line.TransactionDate, line.TransactionType == BankTransactionType.Deposit,
                line.EntrySource, line.MatchedPaymentId != null || line.MatchedJournalEntryId != null || line.ReconciliationStatus != ReconciliationStatus.Unmatched),
            candidates.Select(c => c.Facts).ToList(), enabled);
        return (result, candidates);
    }

    // ═══════════════ งานเบื้องหลัง ═══════════════

    /// <summary>หนึ่งรอบ (ทุกบริษัท · เรียกจาก <c>AutopilotJob</c> ใต้ <see cref="JobLock"/>) — บรรทัดเงินเข้าที่ยังไม่จับคู่ ลงวันที่ ≤ 14 วัน ·
    /// ยังไม่ประเมิน หรือประเมินเป็น None/Suggest นานกว่า 6 ชั่วโมง · Rejected/Failed/Settled ไม่ประเมินซ้ำ (คนตัดสินแล้ว/ต้องให้คนแก้)</summary>
    public async Task<int> EvaluatePendingAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var since = nowUtc.Date.AddDays(-LookbackDays);
        var stale = nowUtc - ReevaluateAfter;
        var lines = await _db.BankTransactions
            .Where(t => !t.IsDeleted && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && t.TransactionType == BankTransactionType.Deposit && t.Amount > 0m && t.TransactionDate >= since
                && (t.AutoSettleVerdict == null
                    || ((t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.None || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Suggest)
                        && (t.AutoSettleEvaluatedAt == null || t.AutoSettleEvaluatedAt < stale))
                    || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Applying))
            .OrderBy(t => t.TransactionDate).Take(BatchSize)
            .Select(t => new { t.Id, t.CompanyId }).ToListAsync(ct);
        var enabledByCompany = new Dictionary<Guid, bool>();
        var done = 0;
        foreach (var l in lines)
        {
            if (!enabledByCompany.TryGetValue(l.CompanyId, out var enabled))
            {
                enabled = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == l.CompanyId)
                    .Select(s => s.BankAutoSettleEnabled).FirstOrDefaultAsync(ct);
                enabledByCompany[l.CompanyId] = enabled;
            }
            try
            {
                await EvaluateLineAsync(l.CompanyId, l.Id, enabled, nowUtc, ct);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ล้มที่บรรทัดไหน = บรรทัดนั้นมีเหตุบนแถว (ประทับใน EvaluateLineAsync เมื่อทำได้) — ไม่ล้มทั้งรอบ
                _logger.LogError(ex, "ประเมินเงินโอนเข้า {Line} ของบริษัท {Company} ล้ม", l.Id, l.CompanyId);
                _db.ChangeTracker.Clear();
            }
        }
        return done;
    }

    /// <summary>ประเมิน + ลงมือหนึ่งบรรทัด (บริษัทเปิด = ปิดหนี้เมื่อเข้าเกณฑ์ · ปิด = ประทับข้อเสนอ)</summary>
    public async Task EvaluateLineAsync(Guid companyId, Guid lineId, bool companyEnabled, DateTime nowUtc, CancellationToken ct = default)
    {
        var line = await _db.BankTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == lineId && t.CompanyId == companyId && !t.IsDeleted, ct);
        if (line == null) return;

        // ล้มกลางทางรอบก่อน (สร้างการชำระแล้วแต่ยังไม่ผูก) ⇒ กู้ด้วยเลขอ้างอิง ไม่ประเมินใหม่ (ยอดค้างเปลี่ยนไปแล้ว)
        if (line.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Applying)
        {
            var reference = PaymentReference(line.Id);
            var paid = await _db.Payments.AsNoTracking().Where(p => p.CompanyId == companyId && p.Reference == reference && !p.IsDeleted)
                .Select(p => new { p.Id, p.DocumentId }).FirstOrDefaultAsync(ct);
            if (paid != null)
            {
                await LinkAsync(companyId, line, paid.Id, paid.DocumentId, BankMatchAttribution.BankAutoSettle,
                    "กู้การผูกหลังล้มกลางทาง — พบการชำระที่ระบบสร้างจากรายการนี้แล้ว", null, nowUtc, ct);
                return;
            }
            // ยังไม่มีการชำระ: อาจกำลังบันทึกอยู่ (คนกดยืนยัน/อีกเครื่อง) ⇒ รอ · ค้างนานเกิน ⇒ ล้มก่อนสร้างการชำระ ⇒ ปลดให้ตัดสินใหม่
            if (line.AutoSettleEvaluatedAt != null && line.AutoSettleEvaluatedAt > nowUtc - ApplyingStaleAfter) return;
            var released = await _db.BankTransactions
                .Where(t => t.Id == line.Id && t.CompanyId == companyId && t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Applying)
                .ExecuteUpdateAsync(u => u.SetProperty(t => t.AutoSettleVerdict, (string?)null), ct);
            if (released == 0) return;
            line.AutoSettleVerdict = null;
        }

        var (result, _) = await DecideAsync(companyId, line, companyEnabled, ct);
        if (result.Kind == BankReceiptAutoSettle.Verdict.AutoSettle && result.DocumentId is Guid docId)
        {
            var (ok, error) = await ApplyAsync(companyId, line, docId, "bank-auto-settle", BankMatchAttribution.BankAutoSettle, result.Reason, null, nowUtc, ct);
            // error null = อีกทางกำลังบันทึก/จับคู่ไปแล้ว (ไม่ใช่ความล้มเหลว — ไม่แจ้ง)
            if (!ok && error != null) await NotifyFailedAsync(companyId, line, docId, error, ct);
            return;
        }
        var stamp = result.Kind == BankReceiptAutoSettle.Verdict.Suggest ? BankReceiptAutoSettle.Stamps.Suggest : BankReceiptAutoSettle.Stamps.None;
        await _db.BankTransactions.Where(t => t.Id == line.Id && t.CompanyId == companyId && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.AutoSettleVerdict, stamp)
                .SetProperty(t => t.AutoSettleReason, result.Reason)
                .SetProperty(t => t.AutoSettleDocumentId, result.DocumentId)
                .SetProperty(t => t.AutoSettleEvaluatedAt, (DateTime?)nowUtc), ct);
    }

    // ═══════════════ ลงมือ (ทางเดียวของทั้งระบบและคนยืนยัน) ═══════════════

    /// <summary>บันทึกรับชำระจากบรรทัด → ผูกบรรทัด · คืน (สำเร็จ, เหตุเมื่อล้ม) · <b>ลำดับ</b>: ประทับ Applying → <c>CreatePaymentAsync</c> → ผูก (กู้ได้ถ้าล้มระหว่างทาง)</summary>
    private async Task<(bool Ok, string? Error)> ApplyAsync(Guid companyId, BankTransaction line, Guid documentId, string actor, string reconciledBy,
        string reason, Guid? userId, DateTime nowUtc, CancellationToken ct)
    {
        // กันสองทาง (งานเบื้องหลัง × คนกดยืนยัน) ลงมือกับบรรทัดเดียวพร้อมกัน — ผู้ชนะคือคนที่เปลี่ยนสถานะจากที่ไม่ใช่ Applying/Settled ได้
        var claimed = await _db.BankTransactions
            .Where(t => t.Id == line.Id && t.CompanyId == companyId && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && t.MatchedPaymentId == null
                && (t.AutoSettleVerdict == null || (t.AutoSettleVerdict != BankReceiptAutoSettle.Stamps.Applying && t.AutoSettleVerdict != BankReceiptAutoSettle.Stamps.Settled)))
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.AutoSettleVerdict, BankReceiptAutoSettle.Stamps.Applying)
                .SetProperty(t => t.AutoSettleDocumentId, (Guid?)documentId)
                .SetProperty(t => t.AutoSettleReason, reason)
                .SetProperty(t => t.AutoSettleEvaluatedAt, (DateTime?)nowUtc), ct);
        if (claimed == 0)
            return (false, null);   // อีกทางกำลังบันทึก/จับคู่ไปแล้ว — ผู้เรียกตัดสินเองว่าจะบอกอะไร

        Guid paymentId;
        try
        {
            var doc = await _db.Documents.AsNoTracking().FirstAsync(d => d.Id == documentId && d.CompanyId == companyId, ct);
            var amount = Math.Abs(line.Amount);
            var fig = await PayLinkTransferWht.ForAsync(_db, companyId, doc, amount, ct);
            var paymentWht = PayLinkTransferWht.PaymentWht(fig, amount, doc.BalanceDue);
            var created = await _docs.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                DocumentId: documentId,
                PaymentDate: line.TransactionDate,
                Amount: amount,
                PaymentMethod: PaymentMethod.BankTransfer,
                Reference: PaymentReference(line.Id),
                BankAccount: null,
                Notes: $"รับเงินโอนจากรายการเดินบัญชี {line.TransactionDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} — {doc.DocumentNumber}",
                OverrideBankAccountId: line.BankAccountId,
                WithholdingTaxAmount: paymentWht
            ), actor, PaymentEntrySource.BankAutoSettle);
            paymentId = created.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ล้มดัง: เหตุอยู่บนบรรทัด (คิวเงินเข้า) + ผู้เรียกแจ้ง/ตอบ · ล้าง tracker กันแถวที่ค้างจากธุรกรรมที่ rollback ไปปนการบันทึกถัดไป
            _db.ChangeTracker.Clear();
            var msg = ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException ? ex.Message : "ข้อผิดพลาดของระบบระหว่างบันทึกรับชำระ";
            if (ex is not (BusinessRuleException or InvalidOperationException or KeyNotFoundException))
                _logger.LogError(ex, "บันทึกรับชำระจากเงินโอน {Line} ล้ม", line.Id);
            await _db.BankTransactions.Where(t => t.Id == line.Id && t.CompanyId == companyId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.AutoSettleVerdict, BankReceiptAutoSettle.Stamps.Failed)
                    .SetProperty(t => t.AutoSettleReason, "บันทึกรับชำระไม่สำเร็จ: " + msg), ct);
            return (false, msg);
        }

        await LinkAsync(companyId, line, paymentId, documentId, reconciledBy, reason, userId, nowUtc, ct);
        return (true, null);
    }

    private async Task LinkAsync(Guid companyId, BankTransaction line, Guid paymentId, Guid documentId, string reconciledBy, string reason, Guid? userId,
        DateTime nowUtc, CancellationToken ct)
    {
        await _db.BankTransactions.Where(t => t.Id == line.Id && t.CompanyId == companyId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.MatchedPaymentId, (Guid?)paymentId)
                .SetProperty(t => t.ReconciliationStatus, ReconciliationStatus.Matched)
                .SetProperty(t => t.ReconciledAt, (DateTime?)nowUtc)
                .SetProperty(t => t.ReconciledBy, reconciledBy)
                .SetProperty(t => t.MatchRuleCode, BankReceiptAutoSettle.RuleAuto)
                .SetProperty(t => t.MatchReason, reason)
                .SetProperty(t => t.AutoSettleVerdict, BankReceiptAutoSettle.Stamps.Settled)
                .SetProperty(t => t.AutoSettleDocumentId, (Guid?)documentId), ct);
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = userId,
            Action = AuditAction.Update,
            EntityType = "BankTransaction",
            EntityId = line.Id.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                action = "bank-auto-settle", ruleCode = BankReceiptAutoSettle.RuleAuto, paymentId, documentId, amount = Math.Abs(line.Amount),
                lineDate = line.TransactionDate, entrySource = line.EntrySource, reconciledBy, reason,
            }),
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task NotifyFailedAsync(Guid companyId, BankTransaction line, Guid documentId, string error, CancellationToken ct)
    {
        if (_notify == null) return;
        var number = await _db.Documents.AsNoTracking().Where(d => d.Id == documentId && d.CompanyId == companyId)
            .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct) ?? "ใบค้าง";
        var (title, message) = AutopilotNotice.BankSettleFailed(line.TransactionDate, Math.Abs(line.Amount), number, error);
        await _notify.DispatchAsync(companyId, NotificationEvents.AutopilotBankSettleFailed, new NotificationContext
        {
            Title = title, Message = message, ActionUrl = "/pages/autopilot.html", EntityType = "BankTransaction", EntityId = line.Id,
        });
    }

    // ═══════════════ คิว "เงินเข้าที่รอยืนยัน" (คน) ═══════════════

    public async Task<IReadOnlyList<BankAutoSettleQueueRowDto>> QueueAsync(Guid companyId, CancellationToken ct = default)
    {
        var lines = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && (t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Suggest || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Failed))
            .OrderByDescending(t => t.TransactionDate).Take(100).ToListAsync(ct);
        if (lines.Count == 0) return Array.Empty<BankAutoSettleQueueRowDto>();
        var docIds = lines.Where(l => l.AutoSettleDocumentId != null).Select(l => l.AutoSettleDocumentId!.Value).Distinct().ToList();
        var docs = await _db.Documents.AsNoTracking().Where(d => d.CompanyId == companyId && docIds.Contains(d.Id))
            .Select(d => new { d.Id, d.DocumentNumber, d.ContactId }).ToDictionaryAsync(d => d.Id, ct);
        var contactIds = docs.Values.Select(d => d.ContactId).Distinct().ToList();
        var contacts = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.BankAutoSettleDisabled }).ToDictionaryAsync(c => c.Id, ct);
        var rows = new List<BankAutoSettleQueueRowDto>();
        foreach (var l in lines)
        {
            // ตัวเลือก = ผู้สมัครที่ตัวตัดสินเห็น (ยอดตรง/ใกล้) — เซิร์ฟเวอร์ประกอบ · ยืนยันได้เฉพาะใบในชุดนี้ (ConfirmAsync ตรวจซ้ำ)
            var cands = await CandidatesAsync(companyId, l, ct);
            var choices = ChoicesFrom(l, cands);
            var doc = l.AutoSettleDocumentId is Guid g && docs.TryGetValue(g, out var dd) ? dd : null;
            var contact = doc != null && contacts.TryGetValue(doc.ContactId, out var cc) ? cc : null;
            rows.Add(new BankAutoSettleQueueRowDto(l.Id, l.TransactionDate, Math.Abs(l.Amount), l.Description, l.Reference,
                l.AutoSettleVerdict ?? BankReceiptAutoSettle.Stamps.Suggest, l.AutoSettleReason ?? "",
                l.AutoSettleDocumentId, doc?.DocumentNumber, choices, contact?.Id, contact?.Name, contact?.BankAutoSettleDisabled ?? false));
        }
        return rows;
    }

    private static List<BankAutoSettleChoiceDto> ChoicesFrom(BankTransaction line, List<Candidate> cands)
    {
        var amount = Math.Abs(line.Amount);
        var near = Math.Max(BankReceiptAutoSettle.NearAbsolute, Math.Round(amount * BankReceiptAutoSettle.NearPercent, 2, MidpointRounding.AwayFromZero));
        return cands
            .Where(c => Math.Abs(c.Facts.ExpectedNet - amount) <= near || c.Facts.DocumentId == line.AutoSettleDocumentId)
            .OrderBy(c => Math.Abs(c.Facts.ExpectedNet - amount)).ThenByDescending(c => c.Facts.DocumentDate)
            .Take(8)
            .Select(c => new BankAutoSettleChoiceDto(c.Facts.DocumentId, c.Facts.DocumentNumber, c.ContactName, c.Facts.ExpectedNet))
            .ToList();
    }

    /// <summary>คนยืนยันข้อเสนอ — ใบต้องอยู่ในชุดตัวเลือกของบรรทัดนี้ (ด่านกันกดผิดใบ) · ล้ม = ข้อความไทยถึงผู้กด (ไม่แจ้งซ้ำทางกระดิ่ง)</summary>
    public async Task<string> ConfirmAsync(Guid companyId, Guid lineId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        var line = await _db.BankTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == lineId && t.CompanyId == companyId && !t.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการเดินบัญชี");
        if (line.ReconciliationStatus != ReconciliationStatus.Unmatched || line.MatchedPaymentId != null)
            throw new BusinessRuleException("รายการนี้จับคู่ไปแล้ว — โหลดหน้าใหม่", BankReceiptAutoSettle.RuleAuto, 409);
        if (line.TransactionType != BankTransactionType.Deposit)
            throw new BusinessRuleException("ยืนยันได้เฉพาะรายการเงินเข้า", BankReceiptAutoSettle.RuleAuto, 400);
        var cands = await CandidatesAsync(companyId, line, ct);
        if (!ChoicesFrom(line, cands).Any(c => c.DocumentId == documentId))
            throw new BusinessRuleException("ใบที่เลือกไม่อยู่ในชุดใบค้างที่ยอดตรง/ใกล้เคียงกับเงินเข้านี้ — บันทึกรับชำระที่หน้าเอกสารแทน", BankReceiptAutoSettle.RuleAuto, 400);
        var (ok, error) = await ApplyAsync(companyId, line, documentId, userId.ToString(), BankMatchAttribution.Person(userId),
            "ผู้ใช้ยืนยันข้อเสนอเงินโอนเข้า", userId, DateTime.UtcNow, ct);
        if (!ok) throw new BusinessRuleException(error ?? "รายการนี้กำลังถูกบันทึกหรือถูกจับคู่ไปแล้ว — โหลดหน้าใหม่", BankReceiptAutoSettle.RuleAuto, 409);
        return "บันทึกรับชำระและจับคู่รายการเดินบัญชีแล้ว — ใบเสร็จออก/ส่งตามค่าตั้งงานหลังรับชำระ";
    }

    /// <summary>คนปฏิเสธข้อเสนอ — บรรทัดค้างไม่จับคู่ตามเดิม · ไม่เสนอซ้ำอัตโนมัติ (จับคู่เองที่หน้ากระทบยอดธนาคารได้)</summary>
    public async Task RejectAsync(Guid companyId, Guid lineId, Guid userId, CancellationToken ct = default)
    {
        var n = await _db.BankTransactions
            .Where(t => t.Id == lineId && t.CompanyId == companyId && !t.IsDeleted && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && (t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Suggest || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Failed))
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.AutoSettleVerdict, BankReceiptAutoSettle.Stamps.Rejected)
                .SetProperty(t => t.AutoSettleReason, "ผู้ใช้ปฏิเสธข้อเสนอ — ไม่เสนอซ้ำ (จับคู่เองที่หน้ากระทบยอดธนาคาร)"), ct);
        if (n == 0) throw new KeyNotFoundException("ไม่พบข้อเสนอเงินโอนเข้านี้ (อาจถูกยืนยัน/ปฏิเสธไปแล้ว)");
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Reject, EntityType = "BankTransaction", EntityId = lineId.ToString(),
            NewValues = JsonSerializer.Serialize(new { action = "bank-auto-settle-reject", ruleCode = BankReceiptAutoSettle.RuleSuggest }),
        });
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>ผลจำลองย้อนหลัง N วัน (อ่านอย่างเดียว · ไม่เขียนอะไร) — คำตัดสินเมื่อ "สมมติว่าเปิด" ของบรรทัดเงินเข้าในช่วงนั้น (รวมที่จับคู่แล้ว · ใช้ยอดค้างปัจจุบัน)</summary>
    public async Task<BankAutoSettleSimulationDto> SimulateAsync(Guid companyId, int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        var since = DateTime.UtcNow.Date.AddDays(-days);
        var lines = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.TransactionType == BankTransactionType.Deposit && t.Amount > 0m
                && t.TransactionDate >= since && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
            .OrderByDescending(t => t.TransactionDate).Take(500).ToListAsync(ct);
        var results = new List<BankReceiptAutoSettle.Result>();
        foreach (var l in lines)
            results.Add((await DecideAsync(companyId, l, enabled: true, ct)).Result);
        var s = BankReceiptAutoSettle.Summarize(results);
        return new BankAutoSettleSimulationDto(days, s.Lines, s.WouldAutoSettle, s.WouldSuggest, s.NoMatch,
            "จำลองกับเงินเข้าที่ยังไม่จับคู่ในช่วงนี้ เทียบกับยอดค้างปัจจุบัน — ไม่บันทึกอะไร · \"ปิดเองได้\" = เข้าเกณฑ์ทุกข้อ (ยอดตรงเป๊ะ + ตัวระบุตัว + ยอดไม่ซ้ำ + มาจากธนาคาร)");
    }
}
