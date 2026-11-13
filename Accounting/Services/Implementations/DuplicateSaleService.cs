using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 266 แพ็ก C — <b>ตัวช่วย "ใบซ้ำ"</b> (ใบขายสด ↔ ใบแจ้งหนี้ของการขายเดียวกัน · คำตัดสิน 266-1-5 · 266-1-36…38) · ไฟล์ใหม่ (F4 ข้อ 7)
///
/// <para>ทุกทางเข้า (หน้าใบขายสด · REST) เดินตัวตัดสินเดียว <see cref="DuplicateSalePolicy.Decide"/> แล้วส่งต่อไปเส้นเดิมเท่านั้น:
/// <list type="bullet">
/// <item><b>VoidInvoice</b> — <see cref="IDocumentService.VoidDocumentAsync"/> ของใบแจ้งหนี้ พร้อมยอดรับชำระที่พรีวิวเห็น (ตรวจใต้ล็อกแถวของเส้นยกเลิก — รับชำระซ้อน = 409)</item>
/// <item><b>CreditNoteInvoice</b> — <see cref="IDocumentService.ConvertDocumentAsync"/> ใบลดหนี้ฉบับร่างเต็มใบ (ผู้ทำบัญชีเลือกเหตุผลแล้วอนุมัติ — ด่าน §86/10 ของการอนุมัติเดิมทำงาน)</item>
/// <item><b>VoidCashSaleAndRecordPayment</b> — ยกเลิกใบขายสด แล้ว <see cref="IDocumentService.CreatePaymentAsync"/> ที่ใบแจ้งหนี้ ลงวันที่รับเงินจริงด้วยบัญชีเงินเดิม ·
/// สองเส้นมีธุรกรรมของตัวเอง (ห่อซ้อนไม่ได้) ⇒ ขั้นที่สองล้ม = throw <see cref="DuplicateSalePolicy.PartialCode"/> พร้อมทางไปต่อ (ล้มดัง ไม่กลืน · F4 E)</item>
/// </list>
/// ไม่มีเส้น "แปลงความหมาย" ใบขายสดเป็นใบรับชำระ และไม่เขียน <c>RelatedDocumentId</c> ของใบที่ออกเลขแล้ว (266-1-2/266-1-5) · tenant ทุก query · ใบลับที่ผู้ใช้ไม่เห็น = ไม่พบ</para>
/// </summary>
public class DuplicateSaleService : IDuplicateSaleService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _documents;
    private readonly IPermissionService? _permissions;
    private readonly ISensitivityService? _sensitivity;
    private const int CandidateCap = 20;

    public DuplicateSaleService(AccountingDbContext db, IDocumentService documents, IPermissionService? permissions = null, ISensitivityService? sensitivity = null)
    {
        _db = db;
        _documents = documents;
        _permissions = permissions;
        _sensitivity = sensitivity;
    }

    private static string Money(decimal v) => v.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<HashSet<SensitivityKind>?> VisibleKindsAsync(Guid companyId, Guid userId)
        => _sensitivity == null ? null : await _sensitivity.GetVisibleKindsAsync(companyId, userId);

    private static bool Visible(HashSet<SensitivityKind>? kinds, SensitivityKind k)
        => kinds == null || k == SensitivityKind.None || kinds.Contains(k);

    private async Task<Document> LoadVisibleAsync(Guid companyId, Guid id, HashSet<SensitivityKind>? kinds, string what, CancellationToken ct)
    {
        var d = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId && !x.IsDeleted, ct)
                ?? throw new KeyNotFoundException("ไม่พบ" + what);
        if (!Visible(kinds, d.Sensitivity)) throw new KeyNotFoundException("ไม่พบ" + what);
        return d;
    }

    private static bool IsCashSale(Document d) => DuplicateSalePolicy.OffersWizardButton(ReceiptLinkFacts.Source(d));

    private static bool IsInvoiceSide(Document d)
        => DuplicateSalePolicy.IsInvoiceType(d.DocumentType) && !ReceiptLinkFacts.Invoice(d).ServesAsReceipt;

    public async Task<DuplicateSaleCandidatesResponse> CandidatesAsync(Guid companyId, Guid documentId, Guid userId, CancellationToken ct = default)
    {
        var kinds = await VisibleKindsAsync(companyId, userId);
        var origin = await LoadVisibleAsync(companyId, documentId, kinds, "เอกสาร", ct);
        var items = new List<DuplicateSaleCandidate>();
        var lo = origin.TotalAmount - 0.01m;
        var hi = origin.TotalAmount + 0.01m;
        if (IsCashSale(origin))
        {
            var types = new[] { DocumentType.Invoice, DocumentType.TaxInvoice };
            var open = ReceiptLinkPolicy.OpenStatuses.ToArray();
            var invoices = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.ContactId == origin.ContactId && d.Id != origin.Id
                    && types.Contains(d.DocumentType) && !d.IssuedAsCashReceipt && open.Contains(d.Status) && d.ReplacedByDocumentId == null
                    && d.TotalAmount >= lo && d.TotalAmount <= hi)
                .OrderBy(d => d.DocumentDate).Take(CandidateCap).ToListAsync(ct);
            foreach (var inv in invoices.Where(d => Visible(kinds, d.Sensitivity) && IsInvoiceSide(d)))
                items.Add(new DuplicateSaleCandidate(origin.Id, origin.DocumentNumber, origin.DocumentDate, inv.Id, inv.DocumentNumber, inv.DocumentDate,
                    inv.TotalAmount, $"{DocumentTypeNames.Title(inv.DocumentType, null)} {inv.DocumentNumber} · {DuplicateSalePolicy.Day(inv.DocumentDate)} · {Money(inv.TotalAmount)} · ค้าง {Money(inv.BalanceDue)}"));
            return new DuplicateSaleCandidatesResponse(origin.Id, origin.DocumentNumber, "cashSale",
                items.Count == 0 ? "ผู้ติดต่อรายนี้ไม่มีใบแจ้งหนี้/ใบกำกับที่อนุมัติแล้วยอดเท่ากับใบนี้ — ไม่มีใบที่อาจซ้ำ" : null, items);
        }
        if (IsInvoiceSide(origin) && origin.ContactId != Guid.Empty)
        {
            var cashTypes = new[] { DocumentType.Receipt, DocumentType.ReceiptVoucher, DocumentType.TaxInvoice };
            var issued = new[] { DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.Paid };
            var sales = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.ContactId == origin.ContactId && d.Id != origin.Id
                    && cashTypes.Contains(d.DocumentType) && issued.Contains(d.Status) && d.ReplacedByDocumentId == null
                    && d.TotalAmount >= lo && d.TotalAmount <= hi)
                .OrderBy(d => d.DocumentDate).Take(CandidateCap).ToListAsync(ct);
            foreach (var cash in sales.Where(d => Visible(kinds, d.Sensitivity) && IsCashSale(d)))
                items.Add(new DuplicateSaleCandidate(cash.Id, cash.DocumentNumber, cash.DocumentDate, origin.Id, origin.DocumentNumber, origin.DocumentDate,
                    cash.TotalAmount, $"ใบขายสด {cash.DocumentNumber} · {DuplicateSalePolicy.Day(cash.DocumentDate)} · {Money(cash.TotalAmount)}"));
            return new DuplicateSaleCandidatesResponse(origin.Id, origin.DocumentNumber, "invoice",
                items.Count == 0 ? "ผู้ติดต่อรายนี้ไม่มีใบขายสดยอดเท่ากับใบนี้ — ไม่มีใบที่อาจซ้ำ" : null, items);
        }
        // ไม่ใช่ใบขายสด/ใบแจ้งหนี้ (หรือไม่ระบุผู้ติดต่อ) — บอกเหตุจริง (ไม่ใช่รายการว่างเงียบ)
        return new DuplicateSaleCandidatesResponse(origin.Id, origin.DocumentNumber, null,
            "ตัวช่วยใบซ้ำใช้กับใบขายสดที่ออกเลขแล้วและระบุผู้ติดต่อ (หรือใบแจ้งหนี้/ใบกำกับของผู้ติดต่อนั้น) เท่านั้น", items);
    }

    public async Task<DuplicateSalePreviewResponse> PreviewAsync(Guid companyId, Guid cashSaleId, Guid invoiceId, Guid userId, CancellationToken ct = default)
    {
        var kinds = await VisibleKindsAsync(companyId, userId);
        await LoadVisibleAsync(companyId, cashSaleId, kinds, "ใบขายสด", ct);
        await LoadVisibleAsync(companyId, invoiceId, kinds, "ใบแจ้งหนี้", ct);
        var snap = await _documents.LoadDuplicateSaleFactsAsync(companyId, cashSaleId, invoiceId);
        var options = DuplicateSalePolicy.Options(snap.Cash, snap.Invoice, snap.Context)
            .Select(v => new DuplicateSaleOption(v.Keep.ToString(), v.Allowed, v.Action.ToString(), v.RuleCode, v.Message, v.WayForward,
                DuplicateSalePolicy.Effects(v, snap.Cash, snap.Invoice).Select(e => new ReceiptLinkEffectRow(e.Label, e.Detail)).ToList(),
                v.Allowed ? DuplicateSalePolicy.PreviewToken(v.Keep, v.Action, snap.Cash, snap.Invoice) : null))
            .ToList();
        return new DuplicateSalePreviewResponse(snap.Cash.Id, snap.Cash.Number, snap.Invoice.Id, snap.Invoice.Number, snap.Cash.TotalAmount,
            options, DuplicateSalePolicy.LegalReference);
    }

    private static BusinessRuleException Refused(DuplicateSaleVerdict v) =>
        new((v.Message ?? "ทำไม่ได้") + (v.WayForward is { } w ? " · ทางไปต่อ: " + w : ""), v.RuleCode, 409);

    /// <summary>สิทธิ์ของเส้นที่จะเดินจริง (ตัดสินจากผลตัดสิน ไม่ใช่จากตัวเลือกที่ส่งมา): ยกเลิกใบที่ซ้ำ · สร้างใบลดหนี้ · อนุมัติใบที่รับเงินเข้า (ระดับเดียวกับปุ่มผูก 266-1-10)</summary>
    private async Task EnsurePermittedAsync(Guid companyId, Guid userId, DuplicateSaleAction action, DuplicateSaleSnapshot snap)
    {
        if (_permissions == null) return;
        string? missing = null;
        if (action == DuplicateSaleAction.VoidInvoice)
        {
            if (!await DocumentPermissionHelper.CanVoidAsync(_permissions, companyId, userId, snap.Invoice.Type))
                missing = $"ยกเลิก{DocumentTypeNames.Title(snap.Invoice.Type, null)} (Document.Void หรือ Document.Revenue.Void)";
        }
        else if (action == DuplicateSaleAction.CreditNoteInvoice)
        {
            if (!await DocumentPermissionHelper.CanCreateAsync(_permissions, companyId, userId, DocumentType.CreditNote))
                missing = "สร้างใบลดหนี้ (Document.Create ของ CreditNote)";
        }
        else if (action == DuplicateSaleAction.VoidCashSaleAndRecordPayment)
        {
            if (!await DocumentPermissionHelper.CanVoidAsync(_permissions, companyId, userId, snap.Cash.Type))
                missing = $"ยกเลิก{DocumentTypeNames.Title(snap.Cash.Type, null)} (Document.Void หรือ Document.Revenue.Void)";
            else if (!await DocumentPermissionHelper.CanApproveAsync(_permissions, companyId, userId, snap.Invoice.Type))
                missing = $"รับชำระ{DocumentTypeNames.Title(snap.Invoice.Type, null)} (Document.Approve หรือ Document.Revenue.Approve)";
        }
        if (missing != null)
            throw new BusinessRuleException($"ไม่มีสิทธิ์{missing} — ให้ผู้มีสิทธิ์เป็นผู้ทำรายการ หรือขอสิทธิ์จากผู้ดูแลบริษัท", DuplicateSalePolicy.PermissionCode, 403);
    }

    /// <summary>แถว audit ของแต่ละขั้น (เส้นเดิม commit ธุรกรรมของตัวเองแล้ว — มี audit ของเส้นเดิมเอง) · บันทึกแถวนี้ไม่สำเร็จ ⇒ ไม่คืนข้อผิดพลาดของการกระทำที่สำเร็จแล้ว
    /// แต่คืนข้อความดังถึงผู้กด (รูปเดียวกับปุ่มผูก · ฝ่ายค้าน L5 รอบ 266 แพ็ก A) · ชื่อช่องใน NewValues ห้ามซ้ำกันแบบไม่สนตัวพิมพ์ (ผล CI run 43370997579 — commit d3567af4)</summary>
    private async Task<string?> AuditAsync(Guid companyId, Guid userId, string action, DuplicateSaleVerdict v, DuplicateSaleSnapshot snap, string reason,
        object? extra, CancellationToken ct)
    {
        try
        {
            _db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserId = userId,
                Action = AuditAction.Update,
                EntityType = "Document",
                EntityId = (v.Keep == DuplicateSaleKeep.CashSale ? snap.Invoice.Id : snap.Cash.Id).ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action,
                    RuleCode = DuplicateSalePolicy.RuleCode,
                    DuplicateSalePolicy.LegalReference,
                    Keep = v.Keep.ToString(),
                    Route = v.Action.ToString(),   // ห้ามชื่อ "Action" — ชนกับ "action" แบบไม่สนตัวพิมพ์ตอนตัวปกปิดข้อมูลอ่าน JSON กลับ (ร่องรอยหายเงียบ)
                    CashSale = snap.Cash.Number,
                    Invoice = snap.Invoice.Number,
                    snap.Cash.TotalAmount,
                    reason,
                    extra,
                    by = userId.ToString(),
                }),
            });
            await _db.SaveChangesAsync(ct);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            return $"รายการสำเร็จแล้ว แต่บันทึกร่องรอย {DuplicateSalePolicy.RuleCode} ไม่สำเร็จ ({ex.GetType().Name}: {ex.GetBaseException().Message}) — ร่องรอยของการยกเลิก/รับชำระเดิมยังอยู่ครบ · แจ้งผู้ดูแลระบบ";
        }
    }

    private static IReadOnlyList<string> Notices(params string?[] xs) => xs.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToList();

    public async Task<DuplicateSaleResult> ExecuteAsync(Guid companyId, DuplicateSaleRequest request, Guid userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.TryParse<DuplicateSaleKeep>(request.Keep, out var keep))
            throw new BusinessRuleException("เลือกใบที่จะคงไว้ (CashSale = คงใบขายสด · Invoice = คงใบแจ้งหนี้)", DuplicateSalePolicy.RuleCode, 400);
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new BusinessRuleException("กรุณาระบุเหตุผลว่าทำไมสองใบนี้เป็นการขายเดียวกัน (เก็บในร่องรอยตรวจสอบให้ผู้สอบบัญชี)", DuplicateSalePolicy.ReasonCode, 400);
        var reason = request.Reason.Trim();
        var kinds = await VisibleKindsAsync(companyId, userId);
        await LoadVisibleAsync(companyId, request.CashSaleId, kinds, "ใบขายสด", ct);
        await LoadVisibleAsync(companyId, request.InvoiceId, kinds, "ใบแจ้งหนี้", ct);

        // ตัดสินซ้ำจากข้อเท็จจริงล่าสุด + เทียบกุญแจพรีวิว (เส้นเดิมแต่ละเส้นตรวจซ้ำใต้ล็อกของตัวเองอีกชั้น)
        var snap = await _documents.LoadDuplicateSaleFactsAsync(companyId, request.CashSaleId, request.InvoiceId);
        var v = DuplicateSalePolicy.Decide(snap.Cash, snap.Invoice, snap.Context, keep);
        if (!v.Allowed) throw Refused(v);
        if (string.IsNullOrWhiteSpace(request.PreviewToken))
            throw new BusinessRuleException("ต้องเปิดพรีวิวผลก่อนยืนยัน (ส่ง previewToken จากพรีวิว)", DuplicateSalePolicy.StaleCode, 400);
        if (!string.Equals(DuplicateSalePolicy.PreviewToken(v.Keep, v.Action, snap.Cash, snap.Invoice), request.PreviewToken, StringComparison.Ordinal))
            throw new BusinessRuleException(DuplicateSalePolicy.StaleMessage, DuplicateSalePolicy.StaleCode, 409);
        await EnsurePermittedAsync(companyId, userId, v.Action, snap);
        var actor = userId.ToString();

        switch (v.Action)
        {
            case DuplicateSaleAction.VoidInvoice:
            {
                var voided = await _documents.VoidDocumentAsync(companyId, snap.Invoice.Id, reversalDate: null, expectedPaidAmount: snap.Invoice.PaidAmount);
                _db.ChangeTracker.Clear();
                var note = await AuditAsync(companyId, userId, "VoidDuplicateInvoice", v, snap, reason, null, ct);
                return new DuplicateSaleResult(keep.ToString(), v.Action.ToString(), snap.Invoice.Id, snap.Invoice.Number, null, null, null, null,
                    Notices($"ยกเลิก {snap.Invoice.Number} แล้ว — คง {snap.Cash.Number} เป็นใบของการขายนี้ (แจ้งลูกค้าให้ใช้ {snap.Cash.Number} เคลมภาษีซื้อ)",
                        voided.EtaxCancellationFlag, voided.OutputVatNotice, voided.CreditNotice, note));
            }
            case DuplicateSaleAction.CreditNoteInvoice:
            {
                var cn = await _documents.ConvertDocumentAsync(companyId, snap.Invoice.Id, DocumentType.CreditNote, actor);
                _db.ChangeTracker.Clear();
                var note = await AuditAsync(companyId, userId, "CreditNoteForDuplicateInvoice", v, snap, reason, new { creditNote = cn.Id, cn.DocumentNumber }, ct);
                return new DuplicateSaleResult(keep.ToString(), v.Action.ToString(), null, null, cn.Id, cn.DocumentNumber, null, null,
                    Notices($"สร้างใบลดหนี้ฉบับร่าง {cn.DocumentNumber} เต็มใบอ้าง {snap.Invoice.Number} แล้ว — เลือกเหตุผล (คืนสินค้า/ปรับยอด) แล้วอนุมัติตามขั้นตอนปกติ "
                        + $"({snap.Invoice.Number} ยกเลิกไม่ได้: {snap.Invoice.TaxLockReason})", note));
            }
            case DuplicateSaleAction.VoidCashSaleAndRecordPayment:
            {
                // ขั้นที่ 1: ยกเลิกใบขายสด (เส้นเดิม — กลับรายได้/ภาษีขาย/เงินสด/สต็อก) · ยอดที่พรีวิวเห็นตรวจใต้ล็อกแถว
                var voided = await _documents.VoidDocumentAsync(companyId, snap.Cash.Id, reversalDate: null, expectedPaidAmount: snap.Cash.PaidAmount);
                _db.ChangeTracker.Clear();
                var voidNote = await AuditAsync(companyId, userId, "VoidDuplicateCashSale", v, snap, reason, new { next = "RecordPayment", snap.PaymentDate }, ct);
                // ขั้นที่ 2: บันทึกรับชำระใบแจ้งหนี้ลงวันที่รับเงินจริง เข้าบัญชีเงินเดิมของใบขายสด (ภาษีถูกหัก 0 — ใบขายสดรับเงินเต็ม · ตัวตัดสินปฏิเสธเคสที่มีภาษีถูกหักแล้ว)
                PaymentResponse pay;
                try
                {
                    pay = await _documents.CreatePaymentAsync(companyId, new CreatePaymentRequest(
                            snap.Invoice.Id, snap.PaymentDate, snap.Invoice.BalanceDue,
                            snap.CashBankAccountId != null ? PaymentMethod.BankTransfer : PaymentMethod.Cash,
                            Reference: snap.Cash.Number, BankAccount: null,
                            Notes: $"รับชำระด้วยเงินของใบขายสด {snap.Cash.Number} ที่ยกเลิกเพราะออกซ้ำกับใบนี้ (ตัวช่วยใบซ้ำ) · {reason}",
                            OverrideBankAccountId: snap.CashBankAccountId, OverridePaymentAccountId: snap.CashPaymentAccountId,
                            WithholdingTaxAmount: 0m),
                        actor, PaymentEntrySource.Manual);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // ล้มดัง (ไม่กลืน · F4 E): ขั้นที่ 1 commit แล้ว — บอกสภาพจริง + ทางไปต่อ · เงินไม่หาย (เงินสดของใบขายสดถูกกลับ รอลงที่การรับชำระ)
                    _db.ChangeTracker.Clear();
                    throw new BusinessRuleException(
                        $"ยกเลิก {snap.Cash.Number} แล้ว แต่บันทึกรับชำระ {snap.Invoice.Number} ไม่สำเร็จ: {ex.Message} — "
                        + $"ทางไปต่อ: กด \"💰 บันทึกชำระเงิน\" ที่ {snap.Invoice.Number} ลงวันที่ {DuplicateSalePolicy.Day(snap.PaymentDate)} ยอด {Money(snap.Invoice.BalanceDue)} เข้าบัญชีเงินเดิมของ {snap.Cash.Number}",
                        ex, DuplicateSalePolicy.PartialCode, 409);
                }
                _db.ChangeTracker.Clear();
                var payNote = await AuditAsync(companyId, userId, "RecordPaymentForDuplicateCashSale", v, snap, reason, new { payment = pay.Id, pay.PaymentNumber }, ct);
                return new DuplicateSaleResult(keep.ToString(), v.Action.ToString(), snap.Cash.Id, snap.Cash.Number, null, null, pay.Id, pay.PaymentNumber,
                    Notices($"ยกเลิก {snap.Cash.Number} และบันทึกรับชำระ {snap.Invoice.Number} ({pay.PaymentNumber}) ลงวันที่ {DuplicateSalePolicy.Day(snap.PaymentDate)} แล้ว",
                        "ถ้าจับคู่รายการเดินบัญชีธนาคารไว้กับใบขายสด ให้จับคู่ใหม่กับการรับชำระ",
                        voided.EtaxCancellationFlag, voided.OutputVatNotice, voided.CreditNotice, voidNote, payNote));
            }
            default:
                throw Refused(v);
        }
    }
}
