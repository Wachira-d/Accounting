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
/// **เงินโอนเข้าในรายการเดินบัญชี ⇒ ปิดหนี้ใบค้าง** (รอบ 252 WP-12A · AUTOPILOT §5 · รอบ 250 ของแผน · G-T1/G-T2/G-T3/G-T8 · แก้ผลฝ่ายค้าน H1–H4 · M1–M7)
/// <list type="bullet">
/// <item>ตัวตัดสินตัวเดียว <see cref="BankReceiptAutoSettle.Decide"/> (pure · ไม่มี AI) — ผู้สมัคร = เอกสารที่ลิงก์ชำระรับได้ (ตัวตัดสินเดียว
/// <c>DocumentPayLinkPolicy.IssueRefusal</c> = null · ใบแจ้งหนี้ที่มีใบกำกับแล้วไม่ใช่ผู้สมัคร · ใบเพิ่มหนี้ฝั่งขาย) ของบริษัทนี้ ·
/// ยอดตรงทั้งหมดไม่ตัดจำนวน (กติกายอดซ้ำต้องเห็นทุกใบ) · ยอดที่ต้องโอน/WHT จาก <see cref="PayLinkTransferWht"/> (สูตรเดียวกับลิงก์ชำระ)</item>
/// <item>แหล่งของบรรทัด (H1): ไฟล์/API/พิมพ์มือที่ผู้ใช้ส่งเอง = เสนอเท่านั้น · ไฟล์หน้าธนาคาร/feed ที่ผู้ใช้ตั้ง URL = ปิดเองได้เมื่อผู้นำเข้ามีสิทธิ์อนุมัติใบชนิดนั้น ณ ตอนตัดสิน</item>
/// <item>AutoSettle (บริษัทเปิด — ค่าตั้งต้นปิด) ⇒ <c>IDocumentService.CreatePaymentAsync</c> (ทางเดียว · source <see cref="PaymentEntrySource.BankAutoSettle"/>) ⇒ ผูกบรรทัด
/// + audit ในธุรกรรมเดียว · <b>ไม่มีทางประทับ "ชำระแล้ว" โดยไม่มีรายการเดินบัญชีจริงหรือคนยืนยัน</b></item>
/// <item>การชำระที่มีเลขอ้างอิง <c>BANK:{lineId}</c> อยู่แล้ว (สร้างสำเร็จแต่ขั้นหลัง commit ล้ม/เครื่องดับ) ⇒ ผูก ไม่สร้างซ้ำ ไม่ประทับล้ม (H2)</item>
/// </list>
/// </summary>
public class BankReceiptSettleService : IBankReceiptSettleService
{
    /// <summary>แถวต่อรอบของงานเบื้องหลัง</summary>
    public const int BatchSize = 100;
    /// <summary>ประเมินเฉพาะรายการที่ลงวันที่ไม่เกินนี้ (เงินโอนเก่ากว่านี้ไม่เดาให้) — บรรทัด Applying กู้ได้เสมอ ไม่ติดกรอบนี้</summary>
    public const int LookbackDays = 14;
    /// <summary>ประเมินซ้ำบรรทัดที่ยังไม่จับคู่ทุก ๆ เท่านี้ (ใบออกทีหลังเงินเข้า · สลิปมาทีหลัง)</summary>
    public static readonly TimeSpan ReevaluateAfter = TimeSpan.FromHours(6);
    /// <summary>ผู้สมัครยอด "ใกล้เคียง" (ไม่ตรง) สูงสุด — เรียงตามส่วนต่างยอด · ผู้สมัครยอดตรงไม่ถูกจำกัด (M1)</summary>
    public const int MaxCandidates = 30;
    /// <summary>บรรทัดที่ค้าง "Applying" โดยไม่มีการชำระนานเกินนี้ = ล้มก่อนสร้างการชำระ ⇒ ปลดให้ตัดสินใหม่ (ไม่ค้างตลอดกาล · ไม่แจ้งซ้ำทุกรอบ)</summary>
    public static readonly TimeSpan ApplyingStaleAfter = TimeSpan.FromMinutes(15);

    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    private readonly ILogger<BankReceiptSettleService> _logger;
    private readonly INotificationEngine? _notify;
    /// <summary>ตรวจสิทธิ์อนุมัติของผู้นำเข้าบรรทัด (H1) — null = ไม่รู้ ⇒ แหล่งที่ต้องตรวจสิทธิ์ = เสนอเท่านั้น</summary>
    private readonly IPermissionService? _perms;

    public BankReceiptSettleService(AccountingDbContext db, IDocumentService docs, ILogger<BankReceiptSettleService> logger, INotificationEngine? notify = null,
        IPermissionService? perms = null)
    {
        _db = db;
        _docs = docs;
        _logger = logger;
        _notify = notify;
        _perms = perms;
    }

    /// <summary>เลขอ้างอิงของการชำระที่ระบบสร้างจากบรรทัดนี้ — กุญแจกันสร้างซ้ำ (กู้หลังล้มกลางทาง) · ข้อความที่คนอ่านอยู่ในหมายเหตุของการชำระ</summary>
    public static string PaymentReference(Guid lineId) => "BANK:" + lineId.ToString("N");

    private static readonly DocumentType[] PayableTypes = { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote };
    private static readonly DocumentStatus[] OpenStatuses = { DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.PartiallyPaid, DocumentStatus.Overdue };

    // ═══════════════ ผู้สมัคร + คำตัดสิน ═══════════════

    private sealed record Candidate(BankReceiptAutoSettle.Candidate Facts, string? ContactName, DocumentType Type);

    /// <summary>ใบยอดตรงแบบเบา (ไม่โหลดข้อเท็จจริงต่อใบ) — ใช้เป็นตัวเลือกในคิวเมื่อมีใบยอดเท่ากันมาก (ฝ่ายค้านรอบสอง M-E · LOW ChoicesFrom)</summary>
    private sealed record ExactLite(Guid DocumentId, string DocumentNumber, string? ContactName, decimal BalanceDue, DateTime DocumentDate);

    /// <summary>ผู้สมัครของบรรทัด — <paramref name="TooManyReason"/> ≠ null = ใบยอดตรงเกิน <see cref="BankReceiptAutoSettle.MaxExactForFacts"/> และไม่อ้างเลขใบ ⇒ เสนอโดยไม่เลือกใบ</summary>
    private sealed record CandidateSet(List<Candidate> Items, string? TooManyReason, List<ExactLite> ExactLites)
    {
        public static CandidateSet Empty => new(new List<Candidate>(), null, new List<ExactLite>());
    }

    private async Task<CandidateSet> CandidatesAsync(Guid companyId, BankTransaction line, CancellationToken ct)
    {
        var amount = Math.Abs(line.Amount);
        var near = Math.Max(BankReceiptAutoSettle.NearAbsolute, Math.Round(amount * BankReceiptAutoSettle.NearPercent, 2, MidpointRounding.AwayFromZero));
        var lo = amount - near;
        var hi = amount + near;
        // ยอดเต็มก่อนหัก (ลูกค้าไม่หัก) ⇒ ยอดค้างต่ำกว่าเงินเข้าได้ถึง ~15% (WHT สูงสุดทั่วไป) — ตัวตัดสินเลือกจาก ExpectedGross เอง
        var loGross = Math.Round(amount * 0.85m, 2, MidpointRounding.AwayFromZero);
        var latest = line.TransactionDate.Date.AddDays(BankReceiptAutoSettle.DocumentAfterLineDays + 1);
        var open = _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && PayableTypes.Contains(d.DocumentType) && OpenStatuses.Contains(d.Status)
                && d.BalanceDue > 0m && d.DocumentDate < latest);
        var lineText = $"{line.Description} {line.Reference} {line.Payee}";
        // M1 + ฝ่ายค้านรอบสอง M-E: นับยอดตรงใน SQL ก่อน — เกิน 8 ใบ ⇒ โหลดเฉพาะใบที่รายการอ้างเลข (ไม่อ้าง = เสนอ "ยอดเท่ากันจำนวนมาก" ไม่โหลดข้อเท็จจริงทุกใบ)
        var exactCount = await open.CountAsync(d => d.BalanceDue == amount, ct);
        List<Document> exactDocs;
        string? tooMany = null;
        var exactLites = new List<ExactLite>();
        if (exactCount <= BankReceiptAutoSettle.MaxExactForFacts)
            exactDocs = await open.Where(d => d.BalanceDue == amount).ToListAsync(ct);
        else
        {
            var lite = await open.Where(d => d.BalanceDue == amount && d.DocumentType != DocumentType.DebitNote)
                .OrderByDescending(d => d.DocumentDate).Take(500)
                .Select(d => new { d.Id, d.DocumentNumber, d.ContactId, d.BalanceDue, d.DocumentDate, d.Currency }).ToListAsync(ct);
            var citedIds = lite.Where(x => BankReceiptAutoSettle.CitesDocumentNumber(lineText, x.DocumentNumber)).Select(x => x.Id).ToList();
            exactDocs = citedIds.Count == 0 ? new List<Document>() : await open.Where(d => citedIds.Contains(d.Id)).ToListAsync(ct);
            if (citedIds.Count == 0)
            {
                tooMany = BankReceiptAutoSettle.TooManyEqualReason;
                // ตัวเลือกในคิว: ใบยอดตรงล่าสุด 30 ใบ ที่ลิงก์ชำระรับได้ (ใบแจ้งหนี้ที่มีใบกำกับแล้ว/สกุลอื่นตัดออก — ชุดเดียวกับ IssueRefusal ส่วนที่ไม่ต้องโหลดรายใบ)
                var top = lite.Where(x => DocumentPayLinkPolicy.IsThb(x.Currency)).Take(30).ToList();
                var topIds = top.Select(x => x.Id).ToList();
                var superseded = await _db.Documents.AsNoTracking()
                    .Where(c => c.CompanyId == companyId && c.RelatedDocumentId != null && topIds.Contains(c.RelatedDocumentId.Value) && !c.IsDeleted
                        && c.DocumentType == DocumentType.TaxInvoice && c.Status != DocumentStatus.Draft && c.Status != DocumentStatus.WaitingApproval
                        && c.Status != DocumentStatus.Rejected && c.Status != DocumentStatus.Voided)
                    .Select(c => c.RelatedDocumentId!.Value).Distinct().ToListAsync(ct);
                var liteContactIds = top.Select(x => x.ContactId).Distinct().ToList();
                var liteNames = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                    .Where(c => c.CompanyId == companyId && liteContactIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
                exactLites = top.Where(x => !superseded.Contains(x.Id))
                    .Select(x => new ExactLite(x.Id, x.DocumentNumber, liteNames.GetValueOrDefault(x.ContactId), x.BalanceDue, x.DocumentDate)).ToList();
            }
        }
        var nearDocs = await open
            .Where(d => d.BalanceDue != amount
                && ((d.BalanceDue >= lo && d.BalanceDue <= hi) || (d.WithholdingTaxAmount > 0m && d.BalanceDue >= loGross && d.BalanceDue < amount)))
            .OrderBy(d => Math.Abs(d.BalanceDue - amount)).Take(MaxCandidates)
            .ToListAsync(ct);
        // ยอดตรงจำนวนมากและไม่อ้างเลขใบ ⇒ ไม่ประเมินใบใกล้เคียงด้วย (คำตัดสิน = เสนอให้คนเลือก)
        if (tooMany != null) return new CandidateSet(new List<Candidate>(), tooMany, exactLites);
        var docs = exactDocs.Concat(nearDocs).GroupBy(d => d.Id).Select(g => g.First()).ToList();
        if (docs.Count == 0) return CandidateSet.Empty;

        var docIds = docs.Select(d => d.Id).ToList();
        var contactIds = docs.Select(d => d.ContactId).Distinct().ToList();
        var contacts = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.BankAutoSettleDisabled, c.IsSupplier, c.IsCustomer }).ToListAsync(ct);
        var relatedIds = docs.Where(d => d.RelatedDocumentId != null).Select(d => d.RelatedDocumentId!.Value).Distinct().ToList();
        var relatedTypes = await _db.Documents.AsNoTracking().IgnoreQueryFilters()
            .Where(r => r.CompanyId == companyId && relatedIds.Contains(r.Id))
            .Select(r => new { r.Id, r.DocumentType }).ToDictionaryAsync(r => r.Id, r => r.DocumentType, ct);
        // M6: นับเฉพาะสลิปที่ยังรอตรวจ
        var intents = await _db.PaymentIntents.AsNoTracking()
            .Where(i => i.CompanyId == companyId && i.SourceKind == PaymentSourceKind.Document && docIds.Contains(i.SourceId))
            .Select(i => new { i.SourceId, i.Status, i.MethodKind, i.SlipBankRef }).ToListAsync(ct);

        var approveByType = new Dictionary<DocumentType, bool>();
        var list = new List<Candidate>();
        foreach (var d in docs)
        {
            var contact = contacts.FirstOrDefault(c => c.Id == d.ContactId);
            // H3: ตัวตัดสินเดียวกับลิงก์ชำระ — ใบแจ้งหนี้ที่ออกใบกำกับแล้ว (ลูกหนี้ย้ายไปใบกำกับ) · ใบเพิ่มหนี้ฝั่งซื้อ · สกุลอื่น ⇒ ไม่ใช่ผู้สมัคร
            DocumentType? relatedType = null;
            if (d.RelatedDocumentId is Guid rid && relatedTypes.TryGetValue(rid, out var rt)) relatedType = rt;
            var saleSide = DocumentPayLinkPolicy.IsSaleSide(d.DocumentType, relatedType,
                d.CnDnPurchaseSideOverride, contact != null && contact.IsSupplier && !contact.IsCustomer);
            var superseding = await PaymentQrService.SupersedingTaxInvoiceNumberAsync(_db, companyId, d, ct);
            if (DocumentPayLinkPolicy.IssueRefusal(d.DocumentType, d.Status, d.BalanceDue, saleSide, d.Currency, superseding) != null) continue;

            if (!approveByType.TryGetValue(d.DocumentType, out var mayApprove))
            {
                mayApprove = line.CreatedByUserId is Guid uid && _perms != null
                    && await DocumentPermissionHelper.CanApproveAsync(_perms, companyId, uid, d.DocumentType);
                approveByType[d.DocumentType] = mayApprove;
            }
            var fig = await PayLinkTransferWht.ForAsync(_db, companyId, d, d.BalanceDue, ct);
            var score = BankMatchScorer.Score(new BankMatchScorer.Input(fig.ExpectedNet, amount, d.DocumentDate, line.TransactionDate,
                CandidateDocNumber: d.DocumentNumber, CandidateName: contact?.Name,
                BankDescription: line.Description, BankReference: line.Reference, BankPayee: line.Payee));
            var mine = intents.Where(i => i.SourceId == d.Id).ToList();
            list.Add(new Candidate(new BankReceiptAutoSettle.Candidate(
                d.Id, d.DocumentNumber, d.DocumentDate, fig.ExpectedNet, fig.ExpectedGross,
                CitesDocumentNumber: BankReceiptAutoSettle.CitesDocumentNumber(lineText, d.DocumentNumber),
                NameConfident: score.NameConfident,
                SlipRefMatched: mine.Any(i => i.MethodKind == PaymentMethodKind.ManualSlip
                    && (i.Status == PaymentIntentStatus.Created || i.Status == PaymentIntentStatus.Pending)
                    && SlipEvidenceRule.RefMatchesLine(i.SlipBankRef, line.Reference)),
                HasPendingOnlinePayment: mine.Any(i => i.MethodKind != PaymentMethodKind.ManualSlip
                    && (i.Status == PaymentIntentStatus.Created || i.Status == PaymentIntentStatus.Pending)),
                ContactAutoSettleDisabled: contact?.BankAutoSettleDisabled ?? false,
                CreatorMayApprove: mayApprove,
                ContactIsSupplier: contact?.IsSupplier ?? false), contact?.Name, d.DocumentType));
        }
        return new CandidateSet(list, null, new List<ExactLite>());
    }

    private async Task<(BankReceiptAutoSettle.Result Result, CandidateSet Candidates)> DecideAsync(Guid companyId, BankTransaction line, bool enabled,
        CancellationToken ct)
    {
        var candidates = await CandidatesAsync(companyId, line, ct);
        if (candidates.TooManyReason != null)
            return (new BankReceiptAutoSettle.Result(BankReceiptAutoSettle.Verdict.Suggest, null, BankReceiptAutoSettle.RuleSuggest, candidates.TooManyReason, false), candidates);
        var result = BankReceiptAutoSettle.Decide(
            new BankReceiptAutoSettle.Line(Math.Abs(line.Amount), line.TransactionDate, line.TransactionType == BankTransactionType.Deposit,
                line.EntrySource, line.MatchedPaymentId != null || line.MatchedJournalEntryId != null || line.ReconciliationStatus != ReconciliationStatus.Unmatched),
            candidates.Items.Select(c => c.Facts).ToList(), enabled);
        return (result, candidates);
    }

    /// <summary>H4: จับคู่ข้ามสกุลไม่ทำ — บัญชีที่ไม่ใช่ THB ไม่เข้าเส้นนี้ (ลิงก์ชำระ/ใบค้างเป็น THB)</summary>
    private async Task<bool> AccountIsThbAsync(Guid companyId, Guid bankAccountId, CancellationToken ct)
    {
        var cur = await _db.BankAccounts.AsNoTracking().IgnoreQueryFilters()
            .Where(a => a.Id == bankAccountId && a.CompanyId == companyId).Select(a => a.Currency).FirstOrDefaultAsync(ct);
        return DocumentPayLinkPolicy.IsThb(cur);   // ว่าง = บาท (ตัวเดียวกับลิงก์ชำระ — ฝ่ายค้านรอบสอง LOW)
    }

    // ═══════════════ งานเบื้องหลัง ═══════════════

    /// <summary>หนึ่งรอบ (ทุกบริษัท · เรียกจาก <c>AutopilotJob</c> ใต้ <see cref="JobLock"/>) — บรรทัดเงินเข้าที่ยังไม่จับคู่ ลงวันที่ ≤ 14 วัน (Applying กู้ได้ทุกอายุ) ·
    /// ยังไม่ประเมิน หรือประเมินเป็น None/Suggest นานกว่า 6 ชั่วโมง · Rejected/Failed/Settled ไม่ประเมินซ้ำ · เรียงที่ยังไม่เคยประเมินก่อน ·
    /// บรรทัดที่ล้มถูกประทับเวลา + เหตุ (ไปท้ายคิว) และรอบนี้โยนสรุปตอนจบ ⇒ ผลรอบของงาน = ล้ม (M7)</summary>
    public async Task<int> EvaluatePendingAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var since = nowUtc.Date.AddDays(-LookbackDays);
        var stale = nowUtc - ReevaluateAfter;
        var lines = await _db.BankTransactions
            .Where(t => !t.IsDeleted && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && t.TransactionType == BankTransactionType.Deposit && t.Amount > 0m
                && (t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Applying
                    || (t.TransactionDate >= since
                        // ฝ่ายค้านรอบสอง M7: ยังไม่มีคำตัดสินแต่เคยล้ม (มีเวลาประเมิน) ก็รอ 6 ชม. เหมือนแถวอื่น — ไม่วนหัวคิว
                        && (t.AutoSettleVerdict == null || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.None || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Suggest)
                        && (t.AutoSettleEvaluatedAt == null || t.AutoSettleEvaluatedAt < stale))))
            .OrderBy(t => t.AutoSettleEvaluatedAt != null).ThenBy(t => t.AutoSettleEvaluatedAt).ThenBy(t => t.TransactionDate)
            .Take(BatchSize)
            .Select(t => new { t.Id, t.CompanyId }).ToListAsync(ct);
        var enabledByCompany = new Dictionary<Guid, bool>();
        var done = 0;
        var failed = 0;
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
                // M7: ล้มที่บรรทัดไหน = ประทับเวลา + เหตุบนแถว (ไปท้ายคิว ไม่วนหัวคิวทุก 5 นาที) · รอบนี้จบแล้วโยนสรุป
                failed++;
                _logger.LogError(ex, "ประเมินเงินโอนเข้า {Line} ของบริษัท {Company} ล้ม", l.Id, l.CompanyId);
                _db.ChangeTracker.Clear();
                var why = "ประเมินไม่สำเร็จ: " + (ex is BusinessRuleException or InvalidOperationException ? ex.Message : "ข้อผิดพลาดของระบบ");
                await _db.BankTransactions.Where(t => t.Id == l.Id && t.CompanyId == l.CompanyId)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(t => t.AutoSettleEvaluatedAt, (DateTime?)nowUtc)
                        .SetProperty(t => t.AutoSettleFailCount, t => t.AutoSettleFailCount + 1)
                        .SetProperty(t => t.AutoSettleReason, why), ct);
                // ฝ่ายค้านรอบสอง M7: ล้มครบ N ครั้ง ⇒ พักไว้ให้คนดู (Held · อยู่ในคิว · ไม่ประเมินซ้ำ)
                var heldWhy = "พักไว้ (ประเมินล้มซ้ำหลายครั้ง): " + why;
                await _db.BankTransactions
                    .Where(t => t.Id == l.Id && t.CompanyId == l.CompanyId && t.AutoSettleFailCount >= BankReceiptAutoSettle.PoisonFailures
                        && t.AutoSettleVerdict != BankReceiptAutoSettle.Stamps.Applying)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(t => t.AutoSettleVerdict, BankReceiptAutoSettle.Stamps.Held)
                        .SetProperty(t => t.AutoSettleReason, heldWhy), ct);
            }
        }
        if (failed > 0)
            throw new InvalidOperationException($"ประเมินเงินโอนเข้า {done + failed} รายการ ล้ม {failed} รายการ — เหตุอยู่บนแต่ละรายการ");
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
            var paid = await ExistingPaymentAsync(companyId, line, ct);
            if (paid is { } found)
            {
                if (!found.Matches) { await MismatchAsync(companyId, line, found.DocumentId, ct); return; }
                await LinkAsync(companyId, line, found.PaymentId, found.DocumentId, BankMatchAttribution.BankAutoSettle, BankReceiptAutoSettle.RuleAuto,
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

        // H4: บัญชีไม่ใช่ THB ⇒ ไม่เข้าเส้นนี้ (ประทับเหตุ ไม่ถูกหยิบซ้ำจนกว่าจะครบรอบประเมินใหม่)
        if (!await AccountIsThbAsync(companyId, line.BankAccountId, ct))
        {
            await StampAsync(companyId, line.Id, BankReceiptAutoSettle.Stamps.None, "บัญชีธนาคารนี้ไม่ใช่สกุลบาท — ระบบไม่จับคู่ใบค้างข้ามสกุล", null, nowUtc, ct);
            return;
        }

        var (result, _) = await DecideAsync(companyId, line, companyEnabled, ct);
        if (result.Kind == BankReceiptAutoSettle.Verdict.AutoSettle && result.DocumentId is Guid docId)
        {
            var (ok, error) = await ApplyAsync(companyId, line, docId, "bank-auto-settle", BankMatchAttribution.BankAutoSettle, BankReceiptAutoSettle.RuleAuto,
                result.Reason, null, nowUtc, ct);
            // error null = อีกทางกำลังบันทึก/จับคู่ไปแล้ว (ไม่ใช่ความล้มเหลว — ไม่แจ้ง)
            if (!ok && error != null) await NotifyFailedAsync(companyId, line, docId, error, ct);
            return;
        }
        var stamp = result.Kind == BankReceiptAutoSettle.Verdict.Suggest ? BankReceiptAutoSettle.Stamps.Suggest : BankReceiptAutoSettle.Stamps.None;
        await StampAsync(companyId, line.Id, stamp, result.Reason, result.DocumentId, nowUtc, ct);
    }

    private async Task StampAsync(Guid companyId, Guid lineId, string stamp, string reason, Guid? documentId, DateTime nowUtc, CancellationToken ct)
        => await _db.BankTransactions.Where(t => t.Id == lineId && t.CompanyId == companyId && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.AutoSettleVerdict, stamp)
                .SetProperty(t => t.AutoSettleReason, reason)
                .SetProperty(t => t.AutoSettleDocumentId, documentId)
                .SetProperty(t => t.AutoSettleFailCount, 0)
                .SetProperty(t => t.AutoSettleEvaluatedAt, (DateTime?)nowUtc), ct);

    /// <summary>การชำระที่ระบบสร้างจากบรรทัดนี้แล้ว (เลขอ้างอิง <c>BANK:{lineId}</c> · บริษัทนี้) — กุญแจกันสร้างซ้ำ</summary>
    /// <remarks>Matches — ฝ่ายค้านรอบสอง LOW: ยอด = ยอดบรรทัด และบัญชีธนาคารของการชำระ = บัญชีของบรรทัด · ไม่ตรง ⇒ ห้ามผูก (แจ้งดัง)</remarks>
    private async Task<(Guid PaymentId, Guid DocumentId, bool Matches)?> ExistingPaymentAsync(Guid companyId, BankTransaction line, CancellationToken ct)
    {
        var reference = PaymentReference(line.Id);
        var p = await _db.Payments.AsNoTracking().Where(x => x.CompanyId == companyId && x.Reference == reference && !x.IsDeleted)
            .Select(x => new { x.Id, x.DocumentId, x.Amount, x.BankAccountId, x.OverrideBankAccountId }).FirstOrDefaultAsync(ct);
        if (p == null) return null;
        var matches = p.Amount == Math.Abs(line.Amount)
            && (p.OverrideBankAccountId == line.BankAccountId || p.BankAccountId == line.BankAccountId);
        return (p.Id, p.DocumentId, matches);
    }

    /// <summary>พบการชำระเลขอ้างอิงของบรรทัดแต่ยอด/บัญชีไม่ตรง ⇒ ไม่ผูก · ประทับ Failed + แจ้งดัง (คนตรวจ)</summary>
    private async Task MismatchAsync(Guid companyId, BankTransaction line, Guid documentId, CancellationToken ct)
    {
        const string why = "พบการชำระที่ใช้เลขอ้างอิงของรายการนี้แต่ยอด/บัญชีธนาคารไม่ตรง — ไม่ผูกอัตโนมัติ ตรวจที่หน้ากระทบยอดธนาคาร";
        await _db.BankTransactions.Where(t => t.Id == line.Id && t.CompanyId == companyId && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
            .ExecuteUpdateAsync(u => u
                .SetProperty(t => t.AutoSettleVerdict, BankReceiptAutoSettle.Stamps.Failed)
                .SetProperty(t => t.AutoSettleReason, why), ct);
        await NotifyFailedAsync(companyId, line, documentId, why, ct);
    }

    // ═══════════════ ลงมือ (ทางเดียวของทั้งระบบและคนยืนยัน) ═══════════════

    /// <summary>บันทึกรับชำระจากบรรทัด → ผูกบรรทัด · คืน (สำเร็จ, เหตุเมื่อล้ม) · <b>ลำดับ</b>: ประทับ Applying → (มีการชำระของบรรทัดนี้แล้ว? ผูก) →
    /// <c>CreatePaymentAsync</c> → ผูก · ล้มหลังสร้างการชำระสำเร็จ (ขั้นหลัง commit) ⇒ ค้นพบด้วยเลขอ้างอิงแล้วผูก ไม่ประทับล้ม (H2)</summary>
    private async Task<(bool Ok, string? Error)> ApplyAsync(Guid companyId, BankTransaction line, Guid documentId, string actor, string reconciledBy,
        string ruleCode, string reason, Guid? userId, DateTime nowUtc, CancellationToken ct)
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

        // H2: มีการชำระของบรรทัดนี้อยู่แล้ว (รอบก่อนสร้างสำเร็จแล้วล้มก่อนผูก) ⇒ ผูก ไม่สร้างซ้ำ
        if (await ExistingPaymentAsync(companyId, line, ct) is { } before)
        {
            if (!before.Matches) { await MismatchAsync(companyId, line, before.DocumentId, ct); return (false, null); }
            if (await LinkAsync(companyId, line, before.PaymentId, before.DocumentId, reconciledBy, ruleCode, reason, userId, nowUtc, ct)) return (true, null);
            return (false, "ผูกรายการเดินบัญชีกับการชำระที่มีอยู่ไม่สำเร็จ — ตรวจที่หน้ากระทบยอดธนาคาร");
        }

        Guid paymentId;
        try
        {
            var doc = await _db.Documents.AsNoTracking().FirstAsync(d => d.Id == documentId && d.CompanyId == companyId, ct);
            var amount = Math.Abs(line.Amount);
            var fig = await PayLinkTransferWht.ForAsync(_db, companyId, doc, amount, ct);
            var paymentWht = PayLinkTransferWht.PaymentWht(fig, amount, doc.BalanceDue);
            var bankRef = string.IsNullOrWhiteSpace(line.Reference) ? "" : $" อ้างอิงธนาคาร {line.Reference!.Trim()}";
            var created = await _docs.CreatePaymentAsync(companyId, new Models.DTOs.Document.CreatePaymentRequest(
                DocumentId: documentId,
                PaymentDate: line.TransactionDate,
                Amount: amount,
                PaymentMethod: PaymentMethod.BankTransfer,
                Reference: PaymentReference(line.Id),
                BankAccount: null,
                Notes: $"รับเงินโอนจากรายการเดินบัญชี {line.TransactionDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}{bankRef} — {doc.DocumentNumber}",
                OverrideBankAccountId: line.BankAccountId,
                WithholdingTaxAmount: paymentWht
            ), actor, PaymentEntrySource.BankAutoSettle);
            paymentId = created.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ล้าง tracker กันแถวที่ค้างจากธุรกรรมที่ rollback ไปปนการบันทึกถัดไป
            _db.ChangeTracker.Clear();
            // H2: การชำระอาจ commit แล้วแต่ขั้นหลัง commit ล้ม ⇒ ค้นด้วยเลขอ้างอิงอีกครั้ง — มี = ผูก (ไม่ประทับล้ม · ไม่สร้างซ้ำรอบหน้า)
            if (await ExistingPaymentAsync(companyId, line, ct) is { } committed)
            {
                _logger.LogError(ex, "บันทึกรับชำระจากเงินโอน {Line} สำเร็จแต่ขั้นหลังบันทึกล้ม — ผูกกับการชำระที่สร้างแล้ว", line.Id);
                if (!committed.Matches) { await MismatchAsync(companyId, line, committed.DocumentId, ct); return (false, null); }
                if (await LinkAsync(companyId, line, committed.PaymentId, committed.DocumentId, reconciledBy, ruleCode, reason, userId, nowUtc, ct)) return (true, null);
                return (false, "ผูกรายการเดินบัญชีกับการชำระที่สร้างแล้วไม่สำเร็จ — ตรวจที่หน้ากระทบยอดธนาคาร");
            }
            var msg = ex is BusinessRuleException or InvalidOperationException or KeyNotFoundException ? ex.Message : "ข้อผิดพลาดของระบบระหว่างบันทึกรับชำระ";
            if (ex is not (BusinessRuleException or InvalidOperationException or KeyNotFoundException))
                _logger.LogError(ex, "บันทึกรับชำระจากเงินโอน {Line} ล้ม", line.Id);
            await _db.BankTransactions.Where(t => t.Id == line.Id && t.CompanyId == companyId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.AutoSettleVerdict, BankReceiptAutoSettle.Stamps.Failed)
                    .SetProperty(t => t.AutoSettleReason, "บันทึกรับชำระไม่สำเร็จ: " + msg), ct);
            return (false, msg);
        }

        if (await LinkAsync(companyId, line, paymentId, documentId, reconciledBy, ruleCode, reason, userId, nowUtc, ct)) return (true, null);
        return (false, "บันทึกรับชำระแล้วแต่ผูกรายการเดินบัญชีไม่สำเร็จ — ตรวจที่หน้ากระทบยอดธนาคาร");
    }

    /// <summary>ผูกบรรทัดกับการชำระ + audit ใน<b>ธุรกรรมเดียว</b> (M5) — เฉพาะบรรทัดที่ยัง Applying + ไม่จับคู่ (ทางมืออื่นจับไปก่อน = 0 แถว ⇒ แจ้งดัง · คืน false)</summary>
    private async Task<bool> LinkAsync(Guid companyId, BankTransaction line, Guid paymentId, Guid documentId, string reconciledBy, string ruleCode, string reason,
        Guid? userId, DateTime nowUtc, CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        var linked = await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var n = await _db.BankTransactions
                .Where(t => t.Id == line.Id && t.CompanyId == companyId
                    && t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Applying && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(t => t.MatchedPaymentId, (Guid?)paymentId)
                    .SetProperty(t => t.ReconciliationStatus, ReconciliationStatus.Matched)
                    .SetProperty(t => t.ReconciledAt, (DateTime?)nowUtc)
                    .SetProperty(t => t.ReconciledBy, reconciledBy)
                    .SetProperty(t => t.MatchRuleCode, ruleCode)
                    .SetProperty(t => t.MatchReason, reason)
                    .SetProperty(t => t.AutoSettleVerdict, BankReceiptAutoSettle.Stamps.Settled)
                    .SetProperty(t => t.AutoSettleDocumentId, (Guid?)documentId), ct);
            if (n == 0)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                Action = AuditAction.Update,
                EntityType = "BankTransaction",
                EntityId = line.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new
                {
                    action = ruleCode == BankReceiptAutoSettle.RuleConfirm ? "bank-confirm-suggest" : "bank-auto-settle", ruleCode, paymentId, documentId, amount = Math.Abs(line.Amount),
                    lineDate = line.TransactionDate, entrySource = line.EntrySource,
                    // H1(c): ใครนำเข้าบรรทัดที่ใช้เป็นหลักฐาน
                    lineCreatedBy = line.CreatedBy, lineCreatedByUserId = line.CreatedByUserId,
                    reconciledBy, reason,
                }),
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        });
        if (!linked)
        {
            // M5: มีการชำระแต่บรรทัดถูกจับคู่/เปลี่ยนสถานะโดยทางอื่น — เงินก้อนเดียวอาจถูกนับสองครั้ง ⇒ ดัง
            _logger.LogError("ผูกเงินโอน {Line} กับการชำระ {Payment} ไม่ได้ — บรรทัดไม่อยู่ในสถานะกำลังบันทึกแล้ว", line.Id, paymentId);
            await NotifyFailedAsync(companyId, line, documentId,
                "บันทึกรับชำระแล้วแต่รายการเดินบัญชีถูกจับคู่/เปลี่ยนสถานะโดยทางอื่นระหว่างบันทึก — ตรวจว่าไม่ได้นับเงินก้อนเดียวสองครั้ง", ct);
        }
        return linked;
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
                && (t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Suggest || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Failed
                    || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Held))
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

    /// <summary>ตัวเลือกในคิว — ใบที่อ้างเลขก่อน · ยอดตรงทุกใบ (สูงสุด 30) · แล้วใบใกล้เคียง (ฝ่ายค้านรอบสอง LOW: เดิมตัด 8 ใบ ⇒ ใบที่ถูกอาจหาย)</summary>
    private static List<BankAutoSettleChoiceDto> ChoicesFrom(BankTransaction line, CandidateSet set)
    {
        var amount = Math.Abs(line.Amount);
        var near = Math.Max(BankReceiptAutoSettle.NearAbsolute, Math.Round(amount * BankReceiptAutoSettle.NearPercent, 2, MidpointRounding.AwayFromZero));
        var full = set.Items
            .Where(c => Math.Abs(c.Facts.ExpectedNet - amount) <= near || c.Facts.DocumentId == line.AutoSettleDocumentId)
            .OrderByDescending(c => c.Facts.CitesDocumentNumber).ThenBy(c => Math.Abs(c.Facts.ExpectedNet - amount)).ThenByDescending(c => c.Facts.DocumentDate)
            .Select(c => new BankAutoSettleChoiceDto(c.Facts.DocumentId, c.Facts.DocumentNumber, c.ContactName, c.Facts.ExpectedNet));
        var lite = set.ExactLites.Select(x => new BankAutoSettleChoiceDto(x.DocumentId, x.DocumentNumber, x.ContactName, x.BalanceDue));
        return full.Concat(lite).GroupBy(c => c.DocumentId).Select(g => g.First()).Take(30).ToList();
    }

    /// <summary>M4: ชนิดของเอกสาร (บริษัทนี้) — ผู้เรียกตรวจสิทธิ์อนุมัติชนิดนั้นก่อนยืนยัน · null = ไม่พบ</summary>
    public async Task<DocumentType?> DocumentTypeAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
        => await _db.Documents.AsNoTracking().Where(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted)
            .Select(d => (DocumentType?)d.DocumentType).FirstOrDefaultAsync(ct);

    /// <summary>คนยืนยันข้อเสนอ — ใบต้องอยู่ในชุดตัวเลือกของบรรทัดนี้ (ด่านกันกดผิดใบ) · ล้ม = ข้อความไทยถึงผู้กด (ไม่แจ้งซ้ำทางกระดิ่ง)</summary>
    public async Task<string> ConfirmAsync(Guid companyId, Guid lineId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        var line = await _db.BankTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == lineId && t.CompanyId == companyId && !t.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบรายการเดินบัญชี");
        if (line.ReconciliationStatus != ReconciliationStatus.Unmatched || line.MatchedPaymentId != null)
            throw new BusinessRuleException("รายการนี้จับคู่ไปแล้ว — โหลดหน้าใหม่", BankReceiptAutoSettle.RuleAuto, 409);
        if (line.TransactionType != BankTransactionType.Deposit)
            throw new BusinessRuleException("ยืนยันได้เฉพาะรายการเงินเข้า", BankReceiptAutoSettle.RuleAuto, 400);
        if (!await AccountIsThbAsync(companyId, line.BankAccountId, ct))
            throw new BusinessRuleException("บัญชีธนาคารนี้ไม่ใช่สกุลบาท — บันทึกรับชำระที่หน้าเอกสาร (ระบุอัตราแลกเปลี่ยน) แทน", BankReceiptAutoSettle.RuleAuto, 400);
        var cands = await CandidatesAsync(companyId, line, ct);
        if (!ChoicesFrom(line, cands).Any(c => c.DocumentId == documentId))
            throw new BusinessRuleException("ใบที่เลือกไม่อยู่ในชุดใบค้างที่ยอดตรง/ใกล้เคียงกับเงินเข้านี้ — บันทึกรับชำระที่หน้าเอกสารแทน", BankReceiptAutoSettle.RuleAuto, 400);
        var (ok, error) = await ApplyAsync(companyId, line, documentId, userId.ToString(), BankMatchAttribution.Person(userId),
            BankReceiptAutoSettle.RuleConfirm, "ผู้ใช้ยืนยันข้อเสนอเงินโอนเข้า", userId, DateTime.UtcNow, ct);
        if (!ok) throw new BusinessRuleException(error ?? "รายการนี้กำลังถูกบันทึกหรือถูกจับคู่ไปแล้ว — โหลดหน้าใหม่", BankReceiptAutoSettle.RuleAuto, 409);
        return "บันทึกรับชำระและจับคู่รายการเดินบัญชีแล้ว — ใบเสร็จออก/ส่งตามค่าตั้งงานหลังรับชำระ";
    }

    /// <summary>คนปฏิเสธข้อเสนอ — บรรทัดค้างไม่จับคู่ตามเดิม · ไม่เสนอซ้ำอัตโนมัติ (จับคู่เองที่หน้ากระทบยอดธนาคารได้)</summary>
    public async Task RejectAsync(Guid companyId, Guid lineId, Guid userId, CancellationToken ct = default)
    {
        var n = await _db.BankTransactions
            .Where(t => t.Id == lineId && t.CompanyId == companyId && !t.IsDeleted && t.ReconciliationStatus == ReconciliationStatus.Unmatched
                && (t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Suggest || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Failed
                    || t.AutoSettleVerdict == BankReceiptAutoSettle.Stamps.Held))
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

    /// <summary>ผลจำลองย้อนหลัง N วัน (อ่านอย่างเดียว · ไม่เขียนอะไร) — คำตัดสินเมื่อ "สมมติว่าเปิด" ของบรรทัดเงินเข้าที่ยังไม่จับคู่ในช่วงนั้น (ใช้ยอดค้างปัจจุบัน) ·
    /// M2: นับแยกบรรทัด "ไม่ทราบแหล่ง (แถวเก่า)" และ "แหล่งที่ระบบไม่ปิดเอง (ไฟล์/API/พิมพ์มือ)" — ไม่ปนกับ "เสนอ"</summary>
    public async Task<BankAutoSettleSimulationDto> SimulateAsync(Guid companyId, int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 90);
        var since = DateTime.UtcNow.Date.AddDays(-days);
        var lines = await _db.BankTransactions.AsNoTracking()
            .Where(t => t.CompanyId == companyId && !t.IsDeleted && t.TransactionType == BankTransactionType.Deposit && t.Amount > 0m
                && t.TransactionDate >= since && t.ReconciliationStatus == ReconciliationStatus.Unmatched)
            .OrderByDescending(t => t.TransactionDate).Take(500).ToListAsync(ct);
        var results = new List<BankReceiptAutoSettle.Result>();
        var thbByAccount = new Dictionary<Guid, bool>();
        foreach (var l in lines)
        {
            // ฝ่ายค้านรอบสอง LOW: บัญชีไม่ใช่บาทไม่เข้าเส้น ⇒ นับเป็นไม่พบ (เหมือนงานจริง)
            if (!thbByAccount.TryGetValue(l.BankAccountId, out var thb))
            {
                thb = await AccountIsThbAsync(companyId, l.BankAccountId, ct);
                thbByAccount[l.BankAccountId] = thb;
            }
            if (!thb)
            {
                results.Add(new BankReceiptAutoSettle.Result(BankReceiptAutoSettle.Verdict.None, null, BankReceiptAutoSettle.RuleNone, "บัญชีไม่ใช่สกุลบาท", false));
                continue;
            }
            results.Add((await DecideAsync(companyId, l, enabled: true, ct)).Result);
        }
        var s = BankReceiptAutoSettle.Summarize(results);
        var legacy = lines.Count(l => l.EntrySource == null);
        var userSupplied = lines.Count(l => l.EntrySource is BankReceiptAutoSettle.SourceUserFile or BankReceiptAutoSettle.SourceApi or BankReceiptAutoSettle.SourceManual);
        return new BankAutoSettleSimulationDto(days, s.Lines, s.WouldAutoSettle, s.WouldSuggest, s.NoMatch, legacy, userSupplied,
            "จำลองกับเงินเข้าที่ยังไม่จับคู่ในช่วงนี้ เทียบกับยอดค้างปัจจุบัน — ไม่บันทึกอะไร · \"ปิดเองได้\" = เข้าเกณฑ์ทุกข้อ (ยอดตรงเป๊ะ + ตัวระบุตัว + ยอดไม่ซ้ำ + มาจากธนาคาร/ผู้นำเข้ามีสิทธิ์อนุมัติ) · " +
            $"ไม่ทราบแหล่ง (รายการก่อนรอบ 252) {legacy} รายการ และไฟล์/API/พิมพ์มือ {userSupplied} รายการ = เสนอเท่านั้นเสมอ");
    }
}
