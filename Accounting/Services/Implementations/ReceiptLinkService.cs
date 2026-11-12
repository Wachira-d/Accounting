using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 266 แพ็ก A — บริการเดียวของ "ผูกใบเสร็จ/เงินที่รับไว้แล้วกับใบแจ้งหนี้ (เจ้าเดียวกัน)" (ไฟล์ใหม่ — F4 ข้อ 7 ห้ามโตใน <c>DocumentService.cs</c>)
///
/// <para>ทุกทางเข้า (หน้าใบแจ้งหนี้ · หน้าใบเสร็จ · REST) เดินตัวตัดสินเดียว <see cref="ReceiptLinkPolicy.Decide"/> แล้วส่งต่อไปเส้นเดิม:
/// <list type="bullet">
/// <item><b>SetDraftSource</b> — เขียน <c>RelatedDocumentId</c> ของ<b>ใบเสร็จร่างเท่านั้น</b> (266-1-2) ใต้ล็อกแถวทั้งสองใบ · ผลทางบัญชีเกิดตอนอนุมัติใบเสร็จ (ด่าน G-1 ตรวจซ้ำ)</item>
/// <item><b>AllocateCredit</b> — <see cref="ICustomerCreditService.AllocateAsync"/> (ล็อก/ด่าน/JE/ภาษีของเส้นเดิม · ส่งยอดคงเหลือที่พรีวิวเห็นเป็น <c>ExpectedUnappliedBefore</c>)</item>
/// <item><b>ApplyDeposit</b> — <see cref="IDocumentService.ApplyDepositToInvoiceAsync"/> (ล็อก/ด่าน VAT ซ้ำของเส้นเดิม)</item>
/// </list>
/// เส้นที่มีธุรกรรมของตัวเอง (สองเส้นหลัง) ตัดสินก่อนส่งต่อ แล้วตัวเส้นเดิมตรวจซ้ำใต้ล็อกของมันเอง — ไม่ห่อธุรกรรมซ้อน · ล้ม = throw (ห้าม <c>catch {}</c> ในเส้นเงิน · F4 E)</para>
/// <para>ทุก query กรอง <c>CompanyId</c> · ผู้สมัครกรองชั้นความลับ (<see cref="ISensitivityService"/>) · ไม่แตะไฟล์ของรอบ 265 (ออกใบเสร็จจากเงินเครดิต/ใบเสร็จรวม)</para>
/// </summary>
public class ReceiptLinkService : IReceiptLinkService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _documents;
    private readonly ICustomerCreditService _credits;
    private readonly ISensitivityService? _sensitivity;

    private const decimal Tol = 0.01m;
    private const int CandidateCap = 30;

    public ReceiptLinkService(AccountingDbContext db, IDocumentService documents, ICustomerCreditService credits, ISensitivityService? sensitivity = null)
    {
        _db = db;
        _documents = documents;
        _credits = credits;
        _sensitivity = sensitivity;
    }

    private sealed record Evaluation(
        ReceiptLinkVerdict Verdict, ReceiptLinkSourceFacts Source, ReceiptLinkInvoiceFacts Invoice, decimal Amount, decimal? Available,
        Guid SourceId, Guid? ReceiptDocumentId, Guid? PaymentId, Guid? DepositDocumentId, DocumentType InvoiceType, DocumentType? ReceiptType);

    // ───────── การอ่าน ─────────

    private async Task<HashSet<SensitivityKind>?> VisibleKindsAsync(Guid companyId, Guid userId)
        => _sensitivity == null ? null : await _sensitivity.GetVisibleKindsAsync(companyId, userId);

    private static bool Visible(HashSet<SensitivityKind>? kinds, SensitivityKind k)
        => kinds == null || k == SensitivityKind.None || kinds.Contains(k);

    private async Task<bool> SameEntityOtherBranchAsync(Guid companyId, Guid a, Guid b, CancellationToken ct)
        => a != b && a != Guid.Empty && b != Guid.Empty
           && (await ContactTaxBranchKey.SameEntityIdsAsync(_db.Contacts.AsNoTracking(), companyId, a, ct)).Contains(b);

    private Task<int> AllocationsAtReceiptAsync(Guid companyId, Guid? paymentId, CancellationToken ct)
        => paymentId is Guid p
            ? _db.PaymentAllocations.AsNoTracking().CountAsync(a => a.CompanyId == companyId && a.PaymentId == p && !a.IsLateAllocation, ct)
            : Task.FromResult(0);

    private async Task<Document> LoadDocAsync(Guid companyId, Guid id, HashSet<SensitivityKind>? kinds, string what, CancellationToken ct)
    {
        var d = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId && !x.IsDeleted, ct)
                ?? throw new KeyNotFoundException("ไม่พบ" + what);
        // ใบลับที่ผู้ใช้ไม่เห็น = ไม่พบ (ไม่บอกว่ามีอยู่ — ชุดเดียวกับทางอ่านเอกสารอื่น)
        if (!Visible(kinds, d.Sensitivity)) throw new KeyNotFoundException("ไม่พบ" + what);
        return d;
    }

    /// <summary>ตัดสินคู่ (ใบเสร็จ/เงิน, ใบแจ้งหนี้) จากข้อเท็จจริงในฐาน — ตัวเดียวที่ผู้สมัคร · พรีวิว · ผูก ใช้</summary>
    private async Task<Evaluation> EvaluateAsync(Guid companyId, Document inv, Document? receipt, Payment? bareCredit, decimal? requestedAmount,
        CancellationToken ct)
    {
        var invFacts = ReceiptLinkFacts.Invoice(inv);
        ReceiptLinkSourceFacts src;
        Guid? paymentId = null;
        if (receipt != null)
        {
            src = ReceiptLinkFacts.Source(receipt, await AllocationsAtReceiptAsync(companyId, receipt.SettlementPaymentId, ct));
            paymentId = receipt.SettlementPaymentId;
        }
        else
        {
            src = ReceiptLinkFacts.Credit(bareCredit!);
            paymentId = bareCredit!.Id;
        }
        var kind = ReceiptLinkPolicy.Classify(src);
        var sameEntity = await SameEntityOtherBranchAsync(companyId, src.ContactId, inv.ContactId, ct);

        decimal amount;
        decimal? available = null;
        string? creditRefusal = null;
        string? depositBlocked = null;
        Guid? depositId = null;
        switch (kind)
        {
            case ReceiptLinkKind.CreditReceipt:
            case ReceiptLinkKind.UnappliedCredit:
            {
                // ด่านของเส้นจัดสรรเดิม (CustomerCreditAllocation.Check ผ่าน ForDocumentAsync) — ไม่เขียนสำเนา
                var forDoc = await _credits.ForDocumentAsync(companyId, inv.Id, ct);
                var row = forDoc.Credits.FirstOrDefault(c => c.PaymentId == paymentId);
                var pay = await _db.Payments.AsNoTracking()
                    .Where(p => p.Id == paymentId && p.CompanyId == companyId && !p.IsDeleted)
                    .Select(p => new { p.UnappliedAmount, p.PaymentNumber }).FirstOrDefaultAsync(ct);
                available = row?.UnappliedAmount ?? pay?.UnappliedAmount ?? 0m;
                creditRefusal = row?.BlockedReason
                    ?? (row == null ? $"เงินรอจัดสรรของ {pay?.PaymentNumber ?? src.Number} ใช้กับใบ {inv.DocumentNumber} ไม่ได้ (ไม่มีคงเหลือ · เป็นรายการที่ต้องตรวจ หรือใบนี้รับการจัดสรรไม่ได้)" : null);
                var suggested = row != null && row.Usable > 0m ? row.Usable : Math.Min(available.Value, Math.Max(0m, inv.BalanceDue));
                amount = requestedAmount ?? suggested;
                break;
            }
            case ReceiptLinkKind.DepositReceipt:
            {
                depositId = receipt!.Id;
                var summary = await _documents.GetContactDepositSummaryAsync(companyId, receipt.ContactId);
                var dep = summary.Deposits.FirstOrDefault(x => x.Id == receipt.Id);
                available = dep == null ? 0m : ReceiptLinkPolicy.DepositGrossOutstanding(dep.BaseAmount, dep.TotalAmount, dep.OutstandingAmount);
                if (DepositPolicyResolver.GrossApplyBlocked(receipt.VatAmount,
                        receipt.DepositOutputVatDeferred && receipt.DepositOutputVatRecognizedAt == null))
                    depositBlocked = DepositPolicyResolver.GrossApplyBlockedMessage(receipt.DocumentNumber);
                amount = requestedAmount ?? Math.Min(available.Value, Math.Max(0m, inv.BalanceDue));
                break;
            }
            default:
                amount = src.TotalAmount;   // ใบเสร็จร่าง = ยอดของใบเสมอ (ผู้ใช้แก้ยอดที่ฟอร์มของใบร่าง)
                break;
        }
        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        var verdict = ReceiptLinkPolicy.Decide(src, invFacts, new ReceiptLinkContext(amount, sameEntity, creditRefusal,
            creditRefusal == null ? null : CustomerCreditAllocation.RuleCode, available ?? 0m, depositBlocked));
        return new Evaluation(verdict, src, invFacts, amount, available, receipt?.Id ?? bareCredit!.Id,
            receipt?.Id, paymentId, depositId, inv.DocumentType, receipt?.DocumentType);
    }

    private static ReceiptLinkCandidate ToCandidate(Evaluation e, string number, DateTime date, string label) => new(
        e.Invoice.Id, e.Invoice.Number, e.ReceiptDocumentId, (e.Verdict.Kind is ReceiptLinkKind.CreditReceipt or ReceiptLinkKind.UnappliedCredit) ? e.PaymentId : null,
        number, date, e.Verdict.Kind.ToString(), e.Verdict.Route?.ToString(), e.Available ?? e.Source.TotalAmount, e.Amount,
        AmountEditable: e.Verdict.Route is ReceiptLinkRoute.AllocateCredit or ReceiptLinkRoute.ApplyDeposit,
        e.Verdict.Allowed, e.Verdict.Allowed ? null : e.Verdict.RuleCode, e.Verdict.Message, e.Verdict.WayForward, label);

    private static string KindLabel(ReceiptLinkKind k) => k switch
    {
        ReceiptLinkKind.DraftReceipt => "ใบเสร็จร่าง",
        ReceiptLinkKind.CreditReceipt => "ใบเสร็จของเงินรอจัดสรร",
        ReceiptLinkKind.UnappliedCredit => "เงินรอจัดสรร",
        ReceiptLinkKind.DepositReceipt => "ใบมัดจำ",
        ReceiptLinkKind.SettlementOfOther => "ใบเสร็จของการรับชำระใบอื่น",
        ReceiptLinkKind.ManualSettlementOfOther => "ใบเสร็จที่รับชำระใบอื่น",
        ReceiptLinkKind.StandaloneSale => "ใบขายสด",
        ReceiptLinkKind.CombinedReceipt => "ใบเสร็จรวม",
        _ => "เอกสาร",
    };

    private static string Money(decimal v) => v.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<ReceiptLinkCandidatesResponse> CandidatesAsync(Guid companyId, Guid fromDocumentId, Guid userId, CancellationToken ct = default)
    {
        var kinds = await VisibleKindsAsync(companyId, userId);
        var origin = await LoadDocAsync(companyId, fromDocumentId, kinds, "เอกสาร", ct);
        var items = new List<ReceiptLinkCandidate>();

        // ── เปิดจากใบแจ้งหนี้ ──
        if (ReceiptLinkPolicy.TargetTypes.Contains(origin.DocumentType))
        {
            if (!ReceiptLinkPolicy.OpenStatuses.Contains(origin.Status) || origin.BalanceDue <= Tol)
                return new ReceiptLinkCandidatesResponse(origin.Id, origin.DocumentNumber, "invoice", null, null, null, false, items);

            // ใบเสร็จร่างที่สร้างเองของผู้ติดต่อเดียวกัน
            var drafts = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.ContactId == origin.ContactId && d.Id != origin.Id
                    && (d.DocumentType == DocumentType.Receipt || d.DocumentType == DocumentType.ReceiptVoucher)
                    && (d.Status == DocumentStatus.Draft || d.Status == DocumentStatus.Rejected)
                    && !d.IsSettlementReceipt && d.SettlementPaymentId == null && !d.IsDeposit
                    && (d.RelatedDocumentId == null || d.RelatedDocumentId != origin.Id))
                .OrderByDescending(d => d.DocumentDate).Take(CandidateCap).ToListAsync(ct);
            // เงินรอจัดสรรของลูกค้า (ตัวอ่านเดิมของปุ่ม "ใช้เงินรอจัดสรร")
            var forDoc = await _credits.ForDocumentAsync(companyId, origin.Id, ct);
            var creditIds = forDoc.Credits.Select(c => c.PaymentId).ToList();
            var creditPayments = await _db.Payments.AsNoTracking()
                .Where(p => p.CompanyId == companyId && creditIds.Contains(p.Id) && !p.IsDeleted).ToListAsync(ct);
            // ใบเสร็จของเงินรอจัดสรร (รอบ 265: SettlementPaymentId + ไม่อ้างใบแจ้งหนี้) — ถ้ามี แสดงเป็นใบเสร็จแทนแถวเงินเปล่า
            var creditReceipts = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.SettlementPaymentId != null && creditIds.Contains(d.SettlementPaymentId.Value)
                    && d.RelatedDocumentId == null && d.Status != DocumentStatus.Voided && d.ReplacedByDocumentId == null)
                .ToListAsync(ct);
            // ใบมัดจำคงเหลือของลูกค้า (ตัวอ่านเดิมของแบนเนอร์หักมัดจำ)
            var depSummary = await _documents.GetContactDepositSummaryAsync(companyId, origin.ContactId);
            var depIds = depSummary.Deposits.Select(x => x.Id).ToList();
            var deposits = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && depIds.Contains(d.Id) && !d.IsDeleted).ToListAsync(ct);
            // ใบเสร็จอื่นที่ออกเลขแล้ว (ของการรับชำระใบอื่น/ขายสด) — แสดงพร้อมเหตุที่ผูกไม่ได้ + ทางไปต่อ (ไม่ใช่ซ่อนเงียบ · §4.4)
            var issued = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.ContactId == origin.ContactId && d.Id != origin.Id
                    && (((d.DocumentType == DocumentType.Receipt || d.DocumentType == DocumentType.ReceiptVoucher) && !d.IsDeposit)
                        || (d.DocumentType == DocumentType.TaxInvoice && d.IssuedAsCashReceipt))
                    && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Sent || d.Status == DocumentStatus.Paid)
                    && (d.RelatedDocumentId == null || d.RelatedDocumentId != origin.Id)
                    && !(d.SettlementPaymentId != null && creditIds.Contains(d.SettlementPaymentId.Value)))
                .OrderByDescending(d => d.DocumentDate).Take(10).ToListAsync(ct);

            foreach (var r in drafts.Where(d => Visible(kinds, d.Sensitivity)))
                items.Add(ToCandidate(await EvaluateAsync(companyId, origin, r, null, null, ct), r.DocumentNumber, r.DocumentDate,
                    $"{KindLabel(ReceiptLinkKind.DraftReceipt)} {r.DocumentNumber} · {Money(r.TotalAmount)}"));
            foreach (var p in creditPayments)
            {
                var rc = creditReceipts.FirstOrDefault(d => d.SettlementPaymentId == p.Id);
                if (rc != null && !Visible(kinds, rc.Sensitivity)) continue;
                var e = await EvaluateAsync(companyId, origin, rc, rc == null ? p : null, null, ct);
                items.Add(ToCandidate(e, rc?.DocumentNumber ?? p.PaymentNumber, rc?.DocumentDate ?? p.PaymentDate,
                    $"{KindLabel(e.Verdict.Kind)} {rc?.DocumentNumber ?? p.PaymentNumber} · คงเหลือ {Money(e.Available ?? 0m)}"));
            }
            foreach (var d in deposits.Where(d => Visible(kinds, d.Sensitivity)))
            {
                var e = await EvaluateAsync(companyId, origin, d, null, null, ct);
                items.Add(ToCandidate(e, d.DocumentNumber, d.DocumentDate, $"{KindLabel(ReceiptLinkKind.DepositReceipt)} {d.DocumentNumber} · คงเหลือ {Money(e.Available ?? 0m)} (รวม VAT)"));
            }
            foreach (var d in issued.Where(d => Visible(kinds, d.Sensitivity)))
            {
                var e = await EvaluateAsync(companyId, origin, d, null, null, ct);
                items.Add(ToCandidate(e, d.DocumentNumber, d.DocumentDate, $"{KindLabel(e.Verdict.Kind)} {d.DocumentNumber} · {Money(d.TotalAmount)}"));
            }
            return new ReceiptLinkCandidatesResponse(origin.Id, origin.DocumentNumber, "invoice", null, null, null, items.Count > 0, items);
        }

        // ── เปิดจากใบเสร็จ ──
        var fromFacts = ReceiptLinkFacts.Source(origin, await AllocationsAtReceiptAsync(companyId, origin.SettlementPaymentId, ct));
        var kind = ReceiptLinkPolicy.Classify(fromFacts);
        var canUndo = kind == ReceiptLinkKind.DraftReceipt && origin.RelatedDocumentId.HasValue;
        if (kind == ReceiptLinkKind.NotLinkable)
            return new ReceiptLinkCandidatesResponse(origin.Id, origin.DocumentNumber, null, kind.ToString(), null, null, false, items);
        var targetTypes = ReceiptLinkPolicy.TargetTypes.ToArray();
        var openStatuses = ReceiptLinkPolicy.OpenStatuses.ToArray();
        var open = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.ContactId == origin.ContactId && d.Id != origin.Id
                && targetTypes.Contains(d.DocumentType) && openStatuses.Contains(d.Status)
                && d.BalanceDue > Tol && d.ReplacedByDocumentId == null && d.Id != origin.RelatedDocumentId)
            .OrderBy(d => d.DocumentDate).Take(CandidateCap).ToListAsync(ct);
        open = open.Where(d => Visible(kinds, d.Sensitivity)).ToList();
        if (open.Count == 0)
            return new ReceiptLinkCandidatesResponse(origin.Id, origin.DocumentNumber, "receipt", kind.ToString(), null, null, canUndo, items, canUndo);
        if (kind is ReceiptLinkKind.DraftReceipt or ReceiptLinkKind.CreditReceipt or ReceiptLinkKind.DepositReceipt)
        {
            foreach (var inv in open)
            {
                var e = await EvaluateAsync(companyId, inv, origin, null, null, ct);
                items.Add(ToCandidate(e, inv.DocumentNumber, inv.DocumentDate,
                    $"{DocumentTypeNames.Title(inv.DocumentType, null)} {inv.DocumentNumber} · ค้าง {Money(inv.BalanceDue)}"));
            }
            return new ReceiptLinkCandidatesResponse(origin.Id, origin.DocumentNumber, "receipt", kind.ToString(), null, null, true, items, canUndo);
        }
        // ชนิดที่ผูกไม่ได้ในแพ็ก A (ของการชำระใบอื่น · ขายสด · ใบรวม) — บอกเหตุผล + ทางไปต่อระดับใบ (ไม่ซ่อนเงียบเมื่อมีใบค้างที่อาจซ้ำ)
        var sample = await EvaluateAsync(companyId, open[0], origin, null, null, ct);
        return new ReceiptLinkCandidatesResponse(origin.Id, origin.DocumentNumber, "receipt", kind.ToString(),
            sample.Verdict.Message, sample.Verdict.WayForward, true, items);
    }

    private async Task<Evaluation> EvaluateRequestAsync(Guid companyId, ReceiptLinkRequest request, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kinds = await VisibleKindsAsync(companyId, userId);
        var inv = await LoadDocAsync(companyId, request.InvoiceId, kinds, "ใบแจ้งหนี้", ct);
        if (request.ReceiptDocumentId is Guid rid)
            return await EvaluateAsync(companyId, inv, await LoadDocAsync(companyId, rid, kinds, "ใบเสร็จ", ct), null, request.Amount, ct);
        if (request.PaymentId is Guid pid)
        {
            var pay = await _db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid && p.CompanyId == companyId && !p.IsDeleted, ct)
                      ?? throw new KeyNotFoundException("ไม่พบการรับชำระ");
            return await EvaluateAsync(companyId, inv, null, pay, request.Amount, ct);
        }
        throw new BusinessRuleException("ระบุใบเสร็จ (receiptDocumentId) หรือเงินรอจัดสรร (paymentId) ที่จะผูก", ReceiptLinkPolicy.RuleCode);
    }

    private static string? Token(Evaluation e) => e.Verdict.Allowed && e.Verdict.Route is ReceiptLinkRoute route
        ? ReceiptLinkPolicy.PreviewToken(route, e.SourceId, e.Source, e.Invoice, e.Amount, e.Available)
        : null;

    public async Task<ReceiptLinkPreviewResponse> PreviewAsync(Guid companyId, ReceiptLinkRequest request, Guid userId, CancellationToken ct = default)
    {
        var e = await EvaluateRequestAsync(companyId, request, userId, ct);
        var v = e.Verdict;
        var effects = v.Allowed && v.Route is ReceiptLinkRoute route
            ? ReceiptLinkPolicy.Effects(route, e.Source, e.Invoice, e.Amount, e.Available is decimal a ? a - e.Amount : null)
                .Select(x => new ReceiptLinkEffectRow(x.Label, x.Detail)).ToList()
            : new List<ReceiptLinkEffectRow>();
        return new ReceiptLinkPreviewResponse(v.Allowed, v.Kind.ToString(), v.Route?.ToString(), v.RuleCode, v.Message, v.WayForward, v.LegalReference,
            e.Amount, e.Invoice.BalanceDue, v.Allowed ? Math.Max(0m, e.Invoice.BalanceDue - e.Amount) : e.Invoice.BalanceDue, effects, Token(e));
    }

    private static BusinessRuleException Refused(ReceiptLinkVerdict v) =>
        new((v.Message ?? "ผูกไม่ได้") + (v.WayForward is { } w ? " · ทางไปต่อ: " + w : ""), v.RuleCode, 409);

    private static void EnsureFresh(Evaluation e, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new BusinessRuleException("ต้องเปิดพรีวิวผลก่อนยืนยันการผูก (ส่ง previewToken จากพรีวิว)", ReceiptLinkPolicy.StaleCode, 400);
        if (!string.Equals(Token(e), token, StringComparison.Ordinal))
            throw new BusinessRuleException(ReceiptLinkPolicy.StaleMessage, ReceiptLinkPolicy.StaleCode, 409);
    }

    private void AuditLink(Guid companyId, string action, Evaluation e, string actor, object? extra = null)
        => _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId,
            UserId = Guid.TryParse(actor, out var uid) ? uid : null,
            Action = AuditAction.Update,
            EntityType = "Document",
            EntityId = e.Invoice.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action,
                RuleCode = ReceiptLinkPolicy.RuleCode,
                ReceiptLinkPolicy.LegalReference,
                Route = e.Verdict.Route?.ToString(),
                Kind = e.Verdict.Kind.ToString(),
                Invoice = e.Invoice.Number,
                Source = e.Source.Number,
                e.Amount,
                InvoiceBalanceBefore = e.Invoice.BalanceDue,
                extra,
                by = actor,
            }),
        });

    public async Task<ReceiptLinkResult> LinkAsync(Guid companyId, ReceiptLinkRequest request, Guid userId, CancellationToken ct = default)
    {
        var actor = userId.ToString();
        var e = await EvaluateRequestAsync(companyId, request, userId, ct);
        if (!e.Verdict.Allowed) throw Refused(e.Verdict);
        EnsureFresh(e, request.PreviewToken);

        switch (e.Verdict.Route)
        {
            case ReceiptLinkRoute.SetDraftSource:
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                // ล็อกสองแถวเรียงตาม Id (ลำดับเดียวทุกคำขอ — ไม่ deadlock) แล้ว "ตัดสินซ้ำใต้ล็อก" ก่อนเขียน (กันอนุมัติ/ผูกซ้อน)
                foreach (var lid in new[] { e.Invoice.Id, e.ReceiptDocumentId!.Value }.OrderBy(x => x))
                    await _db.Database.ExecuteSqlRawAsync(
                        "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", lid, companyId);
                var locked = await EvaluateRequestAsync(companyId, request, userId, ct);
                if (!locked.Verdict.Allowed || locked.Verdict.Route != ReceiptLinkRoute.SetDraftSource) throw Refused(locked.Verdict);
                EnsureFresh(locked, request.PreviewToken);
                var receipt = await _db.Documents.FirstAsync(d => d.Id == locked.ReceiptDocumentId && d.CompanyId == companyId, ct);
                var before = receipt.RelatedDocumentId;
                receipt.RelatedDocumentId = locked.Invoice.Id;   // ใบร่างเท่านั้น (Decide ตัดสินแล้ว · 266-1-2)
                receipt.UpdatedAt = DateTime.UtcNow;
                receipt.UpdatedBy = actor;
                AuditLink(companyId, "SetDraftReceiptSource", locked, actor, new { receipt = receipt.Id, previousSource = before });
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new ReceiptLinkResult(ReceiptLinkRoute.SetDraftSource.ToString(), locked.Invoice.Id, locked.Invoice.Number,
                    locked.Invoice.BalanceDue, locked.Invoice.Status.ToString(), receipt.Id, null, null,
                    new[] { $"ผูก {receipt.DocumentNumber} กับ {locked.Invoice.Number} แล้ว — อนุมัติใบเสร็จเพื่อตัดยอดค้าง" });
            }
            case ReceiptLinkRoute.AllocateCredit:
            {
                // เส้นเดิมล็อกใบ → การชำระ แล้วตรวจ CustomerCreditAllocation.Check ใต้ล็อก · ยอดคงเหลือที่พรีวิวเห็นต้องตรงของจริง (กดซ้ำ/สองแท็บ)
                var r = await _credits.AllocateAsync(companyId, e.PaymentId!.Value,
                    new AllocateCustomerCreditRequest(e.Invoice.Id, e.Amount, request.ApplyDate, ExpectedUnappliedBefore: e.Available), actor, ct);
                var allocationId = await _db.PaymentAllocations.AsNoTracking()
                    .Where(a => a.CompanyId == companyId && a.PaymentId == r.PaymentId && a.DocumentId == r.DocumentId && a.IsLateAllocation)
                    .OrderByDescending(a => a.CreatedAt).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
                AuditLink(companyId, "AllocateCreditViaReceiptLink", e, actor, new { r.PaymentNumber, allocationId, r.RemainingUnapplied });
                await _db.SaveChangesAsync(ct);
                return new ReceiptLinkResult(ReceiptLinkRoute.AllocateCredit.ToString(), r.DocumentId, r.DocumentNumber, r.DocumentBalanceDue,
                    r.DocumentStatus.ToString(), e.ReceiptDocumentId, r.PaymentId, allocationId, r.Notices);
            }
            case ReceiptLinkRoute.ApplyDeposit:
            {
                var res = await _documents.ApplyDepositToInvoiceAsync(companyId, e.Invoice.Id,
                    new ApplyDepositRequest(e.DepositDocumentId!.Value, e.Amount, request.ApplyDate), actor);
                AuditLink(companyId, "ApplyDepositViaReceiptLink", e, actor, new { deposit = e.DepositDocumentId });
                await _db.SaveChangesAsync(ct);
                return new ReceiptLinkResult(ReceiptLinkRoute.ApplyDeposit.ToString(), res.Id, res.DocumentNumber, res.BalanceDue,
                    res.Status.ToString(), e.DepositDocumentId, null, null, Array.Empty<string>());
            }
            default:
                throw Refused(e.Verdict);
        }
    }

    public async Task<ReceiptLinkResult> UndoAsync(Guid companyId, ReceiptLinkUndoRequest request, Guid userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var actor = userId.ToString();
        if (!Enum.TryParse<ReceiptLinkRoute>(request.Route, out var route))
            throw new BusinessRuleException("ไม่รู้จักชนิดการผูกที่จะถอด", ReceiptLinkPolicy.RuleCode);
        switch (route)
        {
            case ReceiptLinkRoute.SetDraftSource when request.ReceiptDocumentId is Guid rid:
            {
                var kinds = await VisibleKindsAsync(companyId, userId);
                await LoadDocAsync(companyId, rid, kinds, "ใบเสร็จ", ct);
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                await _db.Database.ExecuteSqlRawAsync(
                    "SELECT 1 FROM \"Documents\" WHERE \"Id\" = {0} AND \"CompanyId\" = {1} FOR UPDATE", rid, companyId);
                var receipt = await _db.Documents.FirstAsync(d => d.Id == rid && d.CompanyId == companyId, ct);
                await _db.Entry(receipt).ReloadAsync(ct);
                // ถอดได้เฉพาะใบร่างที่สร้างเอง (ใบที่ออกเลขแล้วลงบัญชีตัดลูกหนี้ไปแล้ว — ยกเลิกใบแทน)
                if (ReceiptLinkPolicy.Classify(ReceiptLinkFacts.Source(receipt)) != ReceiptLinkKind.DraftReceipt || receipt.RelatedDocumentId is not Guid prev)
                    throw new BusinessRuleException(ReceiptLinkPolicy.SourceLockedMessage(receipt.DocumentNumber), ReceiptLinkPolicy.SourceCode, 409);
                var inv = await _db.Documents.AsNoTracking().Where(d => d.Id == prev && d.CompanyId == companyId)
                    .Select(d => new { d.Id, d.DocumentNumber, d.BalanceDue, d.Status }).FirstOrDefaultAsync(ct);
                receipt.RelatedDocumentId = null;
                receipt.UpdatedAt = DateTime.UtcNow;
                receipt.UpdatedBy = actor;
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId,
                    UserId = userId,
                    Action = AuditAction.Update,
                    EntityType = "Document",
                    EntityId = receipt.Id.ToString(),
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new { RelatedDocumentId = prev }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "UnlinkDraftReceiptSource", RuleCode = ReceiptLinkPolicy.RuleCode, ReceiptLinkPolicy.LegalReference,
                        receipt.DocumentNumber, Invoice = inv?.DocumentNumber, by = actor,
                    }),
                });
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return new ReceiptLinkResult(route.ToString(), prev, inv?.DocumentNumber ?? "", inv?.BalanceDue ?? 0m, inv?.Status.ToString() ?? "",
                    receipt.Id, null, null, new[] { $"ถอดการผูก {receipt.DocumentNumber} แล้ว — ใบร่างกลับเป็นใบเสร็จอิสระ (อนุมัติแบบนี้ = ขายสด)" });
            }
            case ReceiptLinkRoute.AllocateCredit when request.PaymentId is Guid pid && request.AllocationId is Guid aid:
            {
                // เส้นเดิม (กลับ JE · คืนยอดค้าง · ถอยภาษี · งวดยื่นแล้ว = ปฏิเสธ)
                var r = await _credits.UndoAllocationAsync(companyId, pid, aid, actor, ct);
                _db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = companyId, UserId = userId, Action = AuditAction.Update, EntityType = "Document", EntityId = r.DocumentId.ToString(),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "UndoAllocateCreditViaReceiptLink", RuleCode = ReceiptLinkPolicy.RuleCode, ReceiptLinkPolicy.LegalReference,
                        r.PaymentNumber, r.DocumentNumber, allocationId = aid, by = actor,
                    }),
                });
                await _db.SaveChangesAsync(ct);
                return new ReceiptLinkResult(route.ToString(), r.DocumentId, r.DocumentNumber, r.DocumentBalanceDue, r.DocumentStatus.ToString(),
                    null, r.PaymentId, aid, r.Notices);
            }
            case ReceiptLinkRoute.ApplyDeposit:
                throw new BusinessRuleException("การหักมัดจำไม่มีปุ่มถอดในรุ่นนี้ — ยกเลิกใบแจ้งหนี้แล้วระบบคืนยอดมัดจำให้ (เส้นเดิม) หรือให้ผู้ทำบัญชีบันทึกปรับปรุง",
                    ReceiptLinkPolicy.DepositCode, 409);
            default:
                throw new BusinessRuleException("ถอดการผูกชนิดนี้ไม่ได้ (ต้องระบุใบเสร็จร่าง หรือการชำระ + แถวจัดสรร)", ReceiptLinkPolicy.RuleCode);
        }
    }
}
