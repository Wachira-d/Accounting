using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// #4 — "คัดลอกจากเอกสารเก่า" — เริ่ม invoice ใหม่จาก template doc
/// ก่อนหน้า (มี contact + lines + accounts ครบ). ลด re-typing โดย
/// เฉพาะลูกค้าประจำที่ออกบิลซ้ำๆ.
///
/// Strategy:
///   1. โหลด source doc + lines
///   2. คืน CreateDocumentRequest ที่ pre-fill data (frontend นำไป
///      paint form → user แก้/ยืนยัน → submit ปกติผ่าน /documents)
///   3. Optional auto-create = true → server สร้าง draft doc ทันที
///      คืน id ของ doc ใหม่
///
/// ตัด field ที่ไม่ควร copy:
///   - DocumentNumber (auto-gen ใหม่)
///   - PaidAmount / BalanceDue (reset)
///   - Status (Draft เสมอ)
///   - Etax / Signature data
///   - PaymentDate / DueDate (recompute จาก vendor terms)
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/documents/{sourceId:guid}/clone")]
[Authorize]
public class DocumentCloneController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docs;
    public DocumentCloneController(AccountingDbContext db, IDocumentService docs) { _db = db; _docs = docs; }

    public sealed record CloneOptions(
        DocumentType? OverrideType,
        DateTime? OverrideDate,
        bool AutoCreate);

    /// <summary>GET — preview ของที่จะ clone (ไม่บันทึก). Frontend
    /// fill form แล้วให้ user แก้/ยืนยันก่อน submit ผ่าน /documents POST.</summary>
    [HttpGet("preview")]
    public async Task<ActionResult<ApiResponse<object>>> Preview(
        Guid companyId, Guid sourceId)
    {
        var src = await _db.Documents.AsNoTracking()
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == sourceId && d.CompanyId == companyId);
        if (src == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสารต้นแบบ"));
        // ฝ่ายค้าน P3-6 (รอบ 204): ผู้ถูกหักที่ผู้ติดต่อถูกลบ ⇒ ไม่พรีฟิล (ตัวสืบทอดเดียวกับ POST — WhtPayeeSplit.InheritPayee)
        var previewPayerSide = Accounting.Helpers.WhtCertIssuePlan.AppliesTo(src.DocumentType);
        var previewActivePayees = await Accounting.Helpers.ContactHydration.ActiveContactIdsAsync(_db, companyId,
            src.Lines.Select(l => l.WhtPayeeContactId).Append(src.WhtPayeeContactId));

        return Ok(new ApiResponse<object>(true, new
        {
            documentType = src.DocumentType,
            contactId = src.ContactId,
            documentDate = DateTime.UtcNow.Date,
            dueDate = src.CreditDays.HasValue ? DateTime.UtcNow.Date.AddDays(src.CreditDays.Value) : (DateTime?)null,
            currency = src.Currency,
            exchangeRate = src.ExchangeRate,
            paymentType = src.PaymentType,
            pricesIncludeVat = src.PricesIncludeVat,
            roundingAdjustment = src.RoundingAdjustment,
            // ส่วนลดท้ายบิล (ส่วนลดการค้า) ตามต้นแบบ — ฐานมัดจำที่หัก (DepositBaseDeducted) ไม่ตาม: มัดจำใช้ได้ครั้งเดียว (ดู POST)
            billDiscountPercent = src.BillDiscountPercent,
            billDiscountAmount = src.BillDiscountPercent > 0 ? 0m : src.BillDiscountAmount,
            // ภาษาที่ตรึงกับใบต้นแบบตามไปด้วย — ลูกค้าประจำที่ใช้ใบอังกฤษ
            // คือกลุ่มเดียวกับที่ใช้ clone บ่อยที่สุด (ออกบิลซ้ำทุกเดือน)
            documentLanguage = src.DocumentLanguage,
            // flag หัวเอกสาร — ให้ preview/prefill ตรงกับที่ POST /clone สร้างจริง
            combinedInvoiceTaxInvoice = src.CombinedInvoiceTaxInvoice,
            buyerDeclinedTaxInvoice = src.BuyerDeclinedTaxInvoice,
            issuedAsCashReceipt = src.IssuedAsCashReceipt,
            // รอบ 194 — ใบมัดจำโคลนเป็นใบมัดจำ (เดิมหาย ⇒ รายได้แทนหนี้สิน) พร้อมประเภท/ธงเดิม — ให้ตรงกับที่ POST สร้างจริง
            isDeposit = src.IsDeposit,
            depositKindId = src.IsDeposit ? src.DepositKindId : null,
            depositOutputVatDeferred = src.IsDeposit && src.DepositOutputVatDeferred,
            // คำขอเจ้าของ 2026-10-12 — 50 ทวิ: ติ๊กสร้างอัตโนมัติ + ผู้ถูกหักตามต้นแบบ (ให้ตรงกับที่ POST สร้างจริง)
            whtCertAutoIssue = src.WhtCertAutoIssue,
            whtPayeeContactId = Accounting.Helpers.WhtPayeeSplit.InheritPayee(src.WhtPayeeContactId, previewPayerSide, previewActivePayees),
            // รอบ 204 — ผู้จ่ายออกภาษีแทนตามต้นแบบ (ตัวสืบทอดเดียว WhtGrossUp.Inherit · ให้ตรงกับที่ POST สร้างจริง)
            whtCertType = Accounting.Helpers.WhtGrossUp.Inherit(src.WhtCertType, src.DocumentType, src.DocumentType),
            depositDeferredAccountCode = src.IsDeposit ? src.DepositDeferredAccountCode : null,
            reference = $"คัดลอกจาก {src.DocumentNumber}",
            projectId = src.ProjectId,
            lines = src.Lines.OrderBy(l => l.LineOrder).Select(l => new
            {
                description = l.Description,
                quantity = l.Quantity,
                unit = l.Unit,
                unitPrice = l.UnitPrice,
                discountPercent = l.DiscountPercent,
                vatRate = l.VatRate,
                withholdingTaxRate = l.WithholdingTaxRate,
                accountId = l.AccountId,
                projectId = l.ProjectId,
                productCode = l.ProductCode,
                isVatClaimable = l.IsVatClaimable,
                vatNonClaimableReason = l.VatNonClaimableReason,
                incomeTypeCode = l.IncomeTypeCode,
                // รอบ 204 (ข้อ 143): ผู้ถูกหักรายบรรทัดตามต้นแบบ (ให้ตรงกับที่ POST สร้างจริง)
                whtPayeeContactId = Accounting.Helpers.WhtPayeeSplit.InheritPayee(l.WhtPayeeContactId, previewPayerSide, previewActivePayees)
            })
        }));
    }

    /// <summary>POST — สร้าง draft doc ใหม่ทันที (auto). คืน id ใหม่
    /// ให้ frontend navigate ไปเปิด edit form.</summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Clone(
        Guid companyId, Guid sourceId, [FromBody] CloneOptions? options = null)
    {
        var src = await _db.Documents.AsNoTracking()
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == sourceId && d.CompanyId == companyId);
        if (src == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสารต้นแบบ"));

        // ฝ่ายค้านชุดสอง (C-02/C-05): ชั้นความลับของต้นแบบ — ผู้ที่ดูใบลับไม่ได้ต้องโคลนใบนั้นไม่ได้ (โคลนแล้วได้ยอด/คู่ค้าทั้งหมด)
        if (Accounting.Helpers.SensitivityAccess.NeedsCheck(src.Sensitivity)
            && HttpContext.RequestServices.GetService(typeof(ISensitivityService)) is ISensitivityService sens
            && !await sens.CanViewAsync(companyId, Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User), src.Sensitivity))
            return StatusCode(403, new ApiResponse<object>(false, null,
                Accounting.Helpers.SensitivityAccess.DeniedMessage(src.Sensitivity, "คัดลอก")));
        // ใบวางบิลรวมใบแจ้งหนี้ — โคลน (โดยเฉพาะโคลนเป็นใบเสร็จ) = ยอดที่ตั้งหนี้ไปแล้วถูกเก็บเป็นขายสดอีกรอบ (รายได้ซ้ำ)
        if (Accounting.Helpers.BillingNoteKind.IsRollup(src.DocumentType, src.Lines.Any(l => !l.IsDeleted && l.SourceDocumentId != null)))
            return StatusCode(409, new ApiResponse<object>(false, null,
                "ใบวางบิลนี้รวมใบแจ้งหนี้ที่ตั้งลูกหนี้แล้ว — คัดลอกไม่ได้ (ยอดจะถูกเก็บซ้ำ) · สร้างใบวางบิลใหม่จาก \"รวมใบค้างชำระ\""));

        // ฝ่ายค้าน P3-6 (รอบ 204): ผู้ถูกหัก (หัวเอกสาร + รายบรรทัด) ที่ผู้ติดต่อถูกลบ ⇒ สืบทอดเป็น null + หมายเหตุบนใบใหม่ (เดิมคัดลอกล้มทุกครั้ง)
        var clonePayerSide = Accounting.Helpers.WhtCertIssuePlan.AppliesTo(options?.OverrideType ?? src.DocumentType);
        var cloneSrcLines = src.Lines.Where(l => !l.IsDeleted).ToList();
        var cloneActivePayees = await Accounting.Helpers.ContactHydration.ActiveContactIdsAsync(_db, companyId,
            cloneSrcLines.Select(l => l.WhtPayeeContactId).Append(src.WhtPayeeContactId));
        // ฝ่ายค้านรอบ 204 (หลัง 0f428969): นับเฉพาะผู้ถูกหักที่มีผลกับภาษีหัก · ตัดทิ้ง ⇒ ป้าย "50 ทวิ ต้องตรวจ" (Decide/คำเตือนอนุมัติอ่าน) ·
        // หัวเอกสารที่สืบทอด "ยกตามบรรทัด" ก่อนส่ง (ตัวยกเดียว) — ไม่ใช่ค่าที่ผู้ใช้ระบุ ห้ามชนด่านหัวเอกสารขัดกับรายบรรทัด
        var cloneDroppedPayees = Accounting.Helpers.WhtPayeeSplit.DroppedInheritedWhtPayees(src.WhtPayeeContactId, clonePayerSide,
            cloneSrcLines.Select(l => (Payee: l.WhtPayeeContactId, CarriesWht: l.WithholdingTaxAmount > 0m)).ToList(), clonePayerSide, cloneActivePayees);
        var cloneHeaderPayee = Accounting.Helpers.WhtPayeeSplit.InheritPayee(src.WhtPayeeContactId, clonePayerSide, cloneActivePayees);
        if (clonePayerSide)
            cloneHeaderPayee = Accounting.Helpers.WhtPayeeSplit.LiftSinglePayee(src.ContactId, cloneHeaderPayee,
                cloneSrcLines.OrderBy(l => l.LineOrder).Select(l => new Accounting.Helpers.WhtPayeeLine(
                    Accounting.Helpers.WhtPayeeSplit.InheritPayee(l.WhtPayeeContactId, clonePayerSide, cloneActivePayees),
                    l.WithholdingTaxAmount)).ToList()).DocPayeeOverride;

        var lines = src.Lines.Where(l => !l.IsDeleted).OrderBy(l => l.LineOrder).Select(l => new DocumentLineRequest(
            Description: l.Description,
            Quantity: l.Quantity,
            Unit: l.Unit,
            UnitPrice: l.UnitPrice,
            DiscountPercent: l.DiscountPercent,
            VatRate: l.VatRate,
            WithholdingTaxRate: l.WithholdingTaxRate,
            AccountId: l.AccountId,
            ProjectId: l.ProjectId,
            ProductCode: l.ProductCode,
            IsVatClaimable: l.IsVatClaimable,
            VatNonClaimableReason: l.VatNonClaimableReason,
            // ส่วนลดบาทรายบรรทัดต้องตามไปเหมือนเส้นแปลง (ConvertCoreAsync) — เดิมหาย ⇒ ใบโคลนแพงกว่าต้นแบบ
            // และผลต่างปัดเศษที่ยกไปจะไม่ใช่ของบรรทัดชุดเดียวกัน
            DiscountAmount: l.DiscountAmount > 0 ? l.DiscountAmount : null,
            // รอบ 203: ประเภทเงินได้ ม.40 ตามไปกับอัตราหัก (เอกสารลูกต้องสืบทอด — null เป็น null)
            // ฝ่ายค้าน M4: ผ่านตัวสืบทอดเดียวกับเส้นแปลง — ค่าเก่าที่ตารางชี้ขาดไม่ได้ = ไม่ระบุ (ไม่ทำให้คัดลอกล้ม)
            IncomeTypeCode: Accounting.Helpers.DocumentLineIncomeType.Inherit(l.IncomeTypeCode,
                Accounting.Helpers.WhtCertIssuePlan.AppliesTo(options?.OverrideType ?? src.DocumentType)),
            // รอบ 204 (ข้อ 143): ผู้ถูกหักรายบรรทัดสืบทอด (ปลายทางฝั่งอื่น = null)
            WhtPayeeContactId: Accounting.Helpers.WhtPayeeSplit.InheritPayee(l.WhtPayeeContactId, clonePayerSide, cloneActivePayees)
        )).ToList();

        var docDate = options?.OverrideDate ?? DateTime.UtcNow.Date;
        // flag หัวเอกสารผูกความหมายกับชนิดต้นแบบ — override เป็นชนิดอื่นแล้ว
        // ห้ามพ่วง flag ไป (combined บน Invoice เปล่า ๆ ไม่มีความหมาย)
        var cloneSameType = options?.OverrideType == null
            || options.OverrideType == src.DocumentType;
        var req = new CreateDocumentRequest(
            DocumentType: options?.OverrideType ?? src.DocumentType,
            DocumentDate: docDate,
            DueDate: src.CreditDays.HasValue ? docDate.AddDays(src.CreditDays.Value) : null,
            ContactId: src.ContactId,
            Reference: $"คัดลอกจาก {src.DocumentNumber}",
            Notes: src.Notes,
            Lines: lines,
            ProjectId: src.ProjectId,
            BankAccountId: src.BankAccountId,
            PaymentAccountId: src.PaymentAccountId,
            Currency: src.Currency,
            ExchangeRate: src.ExchangeRate,
            PaymentType: src.PaymentType,
            PricesIncludeVat: src.PricesIncludeVat,
            // ชื่อทางการค้า + รูปแบบเอกสารที่ตรึงกับใบต้นแบบ — เดิมตกทั้งคู่ ⇒
            // โคลนใบแบรนด์รายเดือนแล้วใบใหม่กลับเป็นชื่อบริษัทเงียบ ๆ
            // (defect class เดียวกับ DocumentLanguage ด้านล่าง — ผลตรวจข้อ 7)
            BrandId: src.BrandId,
            Sensitivity: src.Sensitivity,   // ชั้นความลับสืบทอด (กฎ #4 A · ฝ่ายค้านชุดสอง)
            // สาขาที่ออกใบต้นแบบ — โคลนใบของสาขาเชียงใหม่แล้วใบใหม่กลับเป็น
            // สำนักงานใหญ่ = รหัสสาขาบนใบกำกับผิด §86/4 + เข้ารายงานผิดสาขา §87
            BranchId: src.BranchId,
            DocumentTemplateId: cloneSameType ? src.DocumentTemplateId : null,
            // ภาษาที่ตรึงกับใบต้นแบบต้องตามมา — เดิมหาย ⇒ clone ใบอังกฤษของ
            // ลูกค้าต่างชาติแล้วใบใหม่กลับเป็นไทยเงียบ ๆ
            DocumentLanguage: src.DocumentLanguage,
            // ── flag หัวเอกสาร (policy "ทำงานตามหัวกระดาษ") ต้องตามต้นแบบ ──
            // เดิมหาย ⇒ clone ใบ "ใบแจ้งหนี้/ใบกำกับภาษี" ที่ออกซ้ำทุกเดือน แล้ว
            // ใบใหม่กลายเป็นใบกำกับหัวเดี่ยวเงียบ ๆ / ใบ "ไม่ประสงค์รับใบกำกับ"
            // กลับพิมพ์หัวใบกำกับ (defect class "เอกสารลูกต้องสืบทอด")
            CombinedInvoiceTaxInvoice: cloneSameType && src.CombinedInvoiceTaxInvoice,
            BuyerDeclinedTaxInvoice: cloneSameType ? src.BuyerDeclinedTaxInvoice : (bool?)null,
            IssuedAsCashReceipt: cloneSameType ? src.IssuedAsCashReceipt : (bool?)null,
            // ข้อความเฉพาะฉบับที่ลูกค้าเห็นบนกระดาษ — สืบทอดเช่นเดียวกับ convert
            CustomAppendix: src.CustomAppendix,
            CustomFooterNotes: src.CustomFooterNotes,
            CustomTermsAndConditions: src.CustomTermsAndConditions,
            // รอบ 193 (ฝ่ายค้าน C2): โคลนยกทุกบรรทัดครบจำนวน ⇒ ผลต่างปัดเศษตามไป (Helpers/DocumentRounding.Inherit)
            RoundingAdjustment: Accounting.Helpers.DocumentRounding.Inherit(src.RoundingAdjustment, true),
            // รอบ 193 ฝ่ายค้านรอบสาม R3-1: ส่วนลดท้ายบิล (ส่วนลดการค้า) ตามต้นแบบ — เดิมหาย ⇒ ใบโคลนแพงกว่าต้นแบบ ·
            // **ฐานมัดจำที่หัก (DepositBaseDeducted) + เลขใบมัดจำ ไม่ตาม โดยเจตนา**: ใบโคลน = การขายใหม่ มัดจำใบเดิมถูกใช้/รับรู้
            // กับใบต้นแบบไปแล้ว ⇒ พ่วงไป = หักมัดจำที่ใช้แล้วซ้ำ (อนุมัติล้ม หรือรับรู้มัดจำก้อนอื่นของลูกค้าเกิน)
            BillDiscountPercent: src.BillDiscountPercent > 0 ? src.BillDiscountPercent : null,
            BillDiscountAmount: src.BillDiscountPercent <= 0 && src.BillDiscountAmount > 0 ? src.BillDiscountAmount : null,
            // คำขอเจ้าของ 2026-10-12 — เอกสารลูกสืบทอด (กฎ #4 A): ค่าเบี้ยประกันรายเดือนผ่านโบรกเกอร์ = เคสหลักของการโคลน ·
            // ผู้ถูกหักตามไปเฉพาะเมื่อชนิดปลายทางเป็นฝั่งผู้หัก (ชนิดอื่นถูกปฏิเสธ)
            WhtCertAutoIssue: src.WhtCertAutoIssue,
            WhtPayeeContactId: cloneHeaderPayee,
            // รอบ 204 — ผู้จ่ายออกภาษีแทนตามต้นแบบเมื่อปลายทางเป็นฝั่งผู้จ่าย (ค่าธรรมเนียมต่างประเทศรายเดือนที่ออกภาษีแทน = เคสโคลน)
            WhtCertType: Accounting.Helpers.WhtGrossUp.Inherit(src.WhtCertType, src.DocumentType, options?.OverrideType ?? src.DocumentType)
        );

        // ── รอบ 194 — โคลนใบมัดจำ (ช่องโหว่เดิม: ไม่พา IsDeposit ⇒ ใบโคลนลงรายได้แทนหนี้สินมัดจำ) ──
        // พา IsDeposit + ประเภทไปด้วยเฉพาะเมื่อโคลนเป็นชนิดเดิม (ใบเสร็จ/ใบสำคัญรับ) · ใบใหม่เป็นร่างผ่าน CreateDocumentAsync ⇒ ประเภท
        // ถูกตัดสินใหม่ด้วยตัวตัดสินตัวเดียว (ค่าตั้งปัจจุบัน ไม่ใช่สำเนาของใบเดิม) · ประเภทที่ถูกปิด/ลบไปแล้ว ⇒ โคลนแบบไม่มีประเภท
        // (ธง/บัญชีตามใบต้นแบบ = พฤติกรรมของใบเดิมที่ไม่มีประเภท) แทนการล้มทั้งคำขอ · ใบเดิมที่ไม่มีประเภท ⇒ ไม่มีประเภท (null สืบทอดเป็น null)
        if (cloneSameType && src.IsDeposit)
        {
            var kindUsable = src.DepositKindId is Guid srcKind
                && await _db.DepositKinds.AsNoTracking().AnyAsync(k => k.Id == srcKind && k.CompanyId == companyId && !k.IsDeleted && k.IsActive);
            req = req with
            {
                IsDeposit = true,
                DepositKindId = kindUsable ? src.DepositKindId : null,
                DepositOutputVatDeferred = src.DepositOutputVatDeferred,
                DepositDeferredAccountCode = src.DepositDeferredAccountCode,
            };
        }

        // ResolveSignersAsync (PDF ผู้จัดทำ) parse CreatedBy เป็น user GUID —
        // Identity.Name เป็นชื่อ/อีเมล ทำให้เอกสารโคลนไม่มีลายเซ็นผู้จัดทำ
        var createdBy = Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();
        var newDoc = await _docs.CreateDocumentAsync(companyId, req, createdBy);
        // ฝ่ายค้าน P3-6: ผู้ถูกหักถูกตัดทิ้ง ⇒ ป้าย "50 ทวิ ต้องตรวจ" บนใบใหม่ + บอกในคำตอบ — ไม่เปลี่ยนผู้ถูกหักเงียบ ๆ ·
        // ฝ่ายค้านรอบ 204 (หลัง 0f428969): ป้ายเดียวกับที่ WhtCertIssuePlan.Decide/คำเตือนอนุมัติอ่าน (เดิมหมายเหตุธรรมดา ⇒ 50 ทวิ ออกผิดคนเงียบ)
        var droppedNote = Accounting.Helpers.WhtPayeeSplit.DroppedInheritedPayeeNote(src.DocumentNumber, cloneDroppedPayees);
        // ฝ่ายค้านรอบ 204 (หลัง 7b88c053): ต้นทางที่ยังมีป้ายผู้ถูกหักถูกตัดทิ้ง (ไม่รับทราบ) ⇒ ใบที่คัดลอกติดป้ายเดียวกัน (มิฉะนั้นเดือนถัดไป 50 ทวิ ออกในนามโบรกเกอร์อัตโนมัติ)
        var srcPendingDropped = Accounting.Helpers.WhtPayeeSplit.PendingDroppedPayeeReview(src.InternalNotes);
        if ((droppedNote != null || srcPendingDropped != null)
            && await _db.Documents.FirstOrDefaultAsync(d => d.Id == newDoc.Id && d.CompanyId == companyId) is { } cloned)
        {
            cloned.InternalNotes = Accounting.Helpers.WhtPayeeSplit.MarkDroppedInheritedPayees(cloned.InternalNotes, src.DocumentNumber, cloneDroppedPayees);
            cloned.InternalNotes = Accounting.Helpers.WhtPayeeSplit.InheritPendingDroppedReview(cloned.InternalNotes, src.InternalNotes,
                Accounting.Helpers.WhtCertIssuePlan.AppliesTo(cloned.DocumentType));
            await _db.SaveChangesAsync();
        }
        return Ok(new ApiResponse<object>(true, new
        {
            newDocumentId = newDoc.Id,
            newDocumentNumber = newDoc.DocumentNumber,
            sourceDocumentNumber = src.DocumentNumber
        }, $"คัดลอกจาก {src.DocumentNumber} → {newDoc.DocumentNumber} เรียบร้อย" + (droppedNote != null ? " · " + droppedNote : "")));
    }
}
