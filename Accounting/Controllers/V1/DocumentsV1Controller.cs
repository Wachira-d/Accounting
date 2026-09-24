using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Matching;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers.V1;

/// <summary>
/// `/api/v1/documents` — สร้างเอกสารในระบบเราแล้วคืนข้อมูลให้ยิงกลับ ERP ปลายทาง
///
/// ต่างจาก `/api/integration/*` เดิม (ซึ่งผูกกับ contract ของ TakeTime) ตรงที่
/// เส้นนี้เป็น **contract สาธารณะ v1** ที่ล็อกรูปแบบไว้ + รับ `contactExternalId`
/// เพื่อผูกเจ้าหนี้ให้ตรงกับทะเบียนของลูกค้า และคิดเงินตามการใช้จริง
///
/// **ผู้ติดต่อ resolve 3 ชั้น** (ตามลำดับความแน่นอน):
///   1. `contactExternalId` — รหัสในระบบลูกค้า แน่นอนที่สุด
///   2. เลขผู้เสียภาษี + <c>contactBranchCode</c> (Helpers/ContactTaxBranchKey) — เลขนี้มีอยู่แล้วแต่ไม่มีสาขาที่ส่งมา
///      ⇒ สร้างผู้ติดต่อของสาขานั้นแยก (ไม่ถอยไปเทียบชื่อ ซึ่งจะได้แถวสาขาอื่นของเลขเดียวกันกลับมา)
///   3. ชื่อ (ข้ามภาษา/ทนชื่อถูกตัด) — หลักฐานอ่อน ใช้เมื่อไม่มี 2 อย่างแรก
///   ไม่เจอเลย → สร้าง contact ใหม่ **ที่ยังไม่มี ExternalId** แล้วโผล่ใน
///   `/contacts/unmapped` ให้ลูกค้ามาผูกรหัสภายหลัง (ปิดวงจร ไม่ปล่อยค้าง)
/// </summary>
[ApiController]
[Route("api/v1/documents")]
[Authorize]
public class DocumentsV1Controller : PublicApiControllerBase
{
    private const string Feature = "document.create";

    private readonly IDocumentService _documents;
    private readonly IPermissionService _permissions;
    private readonly ILogger<DocumentsV1Controller> _logger;

    public DocumentsV1Controller(AccountingDbContext db, IUsageMeteringService metering,
        IDocumentService documents, IPermissionService permissions,
        ILogger<DocumentsV1Controller> logger) : base(db, metering)
    { _documents = documents; _permissions = permissions; _logger = logger; }

    public record LineRequest(string Description, decimal Quantity, decimal UnitPrice,
        decimal VatRate = 7, string? Unit = null, decimal WithholdingTaxRate = 0);

    public record CreateRequest(
        string DocumentType,
        DateTime? DocumentDate,
        string? Reference,
        string? SupplierInvoiceNumber,
        string? Notes,
        // ผู้ติดต่อ — ส่งอย่างใดอย่างหนึ่งก็พอ (เรียงตามความแน่นอน)
        Guid? ContactId,
        string? ContactExternalId,
        string? ContactTaxId,
        string? ContactName,
        List<LineRequest>? Lines,
        // รหัสสาขา 5 หลักของคู่ค้า (ไม่บังคับ · รอบ 193 ทีม C3) — ใช้คู่กับ contactTaxId: เลขเดียวกันคนละสาขา = คนละผู้ติดต่อ
        // (ประกาศอธิบดีฯ 199 · §86/4) · ไม่ส่ง = "ไม่ระบุ" (แถวสำนักงานใหญ่ก่อน) · ส่งมาแล้วยังไม่มีแถวสาขานี้ = สร้างให้
        string? ContactBranchCode = null);

    /// <summary>สร้างเอกสาร (ค่าเริ่มต้นเป็นฉบับร่าง — เลขจริงออกตอนอนุมัติตาม §86/4)
    ///
    /// <para><b>ด่านกันซ้ำ</b>: <c>supplierInvoiceNumber</c> เดิมของคู่ค้าเดิม =
    /// ปฏิเสธ 409 พร้อมชี้เอกสารที่มีอยู่ — retry ของพาร์ตเนอร์/cron ซ้อนกันเป็น
    /// เรื่องปกติของ API ⇒ ถ้าไม่กัน ใบกำกับซื้อใบเดียวจะถูกตั้งหนี้และเคลมภาษีซื้อ
    /// สองครั้ง (§82/5 → เบี้ยปรับ)</para>
    ///
    /// <para><b>ยังไม่มี <c>/dry-run</c> ของเส้นนี้ — และตั้งใจไม่ใส่</b>:
    /// ด่านจริงอยู่ลึกใน <c>DocumentService.CreateDocumentAsync</c> ซึ่งเปิด
    /// transaction ของตัวเอง<b>เสมอ</b> (<c>DocumentService.cs:960</c>) ⇒ การห่อ
    /// transaction ไว้ข้างนอกเพื่อ rollback จะโยน "transaction already started"
    /// ทุกครั้ง และการตรวจด้วยด่านชุดที่สองที่เขียนเองคือ <b>dry-run ที่โกหก</b>
    /// ซึ่งแย่กว่าไม่มี (พาร์ตเนอร์จะเชื่อว่าผ่านแล้วไปล้มของจริง).
    /// เปิดได้เมื่อ <c>CreateDocumentAsync</c> ใช้รูป
    /// <c>ownsTransaction = _db.Database.CurrentTransaction == null</c>
    /// แบบเดียวกับ <c>DocumentService.cs:11950</c> แล้ว — สเปกอยู่ในรายงานรอบนี้</para>
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req, CancellationToken ct)
    {
        var (ctx, error) = await ResolveCallerAsync("documents:write", Feature, ct);
        if (error != null) return error;

        if (!Enum.TryParse<DocumentType>(req?.DocumentType, true, out var docType))
            return BadRequest(new ApiResponse<string>(false, null,
                "documentType ไม่ถูกต้อง — เช่น PaymentVoucher, Expense, PurchaseInvoice, Invoice, TaxInvoice"));

        if (req!.Lines == null || req.Lines.Count == 0)
            return BadRequest(new ApiResponse<string>(false, null, "ต้องมีรายการอย่างน้อย 1 บรรทัด"));

        // รหัสสาขาผิดรูป = ตอบ 400 ชี้ช่อง — ห้ามถือเป็น "ไม่ระบุ" แล้วออกใบกำกับในนามสำนักงานใหญ่เงียบ ๆ (§86/4)
        if (!Helpers.TaxBranchCode.TryNormalize(req.ContactBranchCode, out _, out var branchError))
            return BadRequest(new ApiResponse<string>(false, null, $"contactBranchCode: {branchError}"));

        var contactId = await ResolveContactAsync(ctx!.CompanyId, req, docType, ct);
        if (contactId == null)
            return BadRequest(new ApiResponse<string>(false, null,
                "ระบุผู้ติดต่อไม่ได้ — ส่ง contactId, contactExternalId, contactTaxId หรือ contactName อย่างน้อยหนึ่งอย่าง"));

        // ── ด่านกันเอกสารซ้ำ (Helpers/PartnerSyncConflict) ──
        if (!string.IsNullOrWhiteSpace(req.SupplierInvoiceNumber))
        {
            var sup = req.SupplierInvoiceNumber.Trim();
            var dup = await Db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == ctx.CompanyId && d.ContactId == contactId.Value
                         && d.SupplierInvoiceNumber == sup
                         && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
                .Select(d => new { d.Id, d.DocumentNumber })
                .FirstOrDefaultAsync(ct);
            var clash = Helpers.PartnerSyncConflict.CheckDocument(sup, dup?.DocumentNumber, dup?.Id);
            if (clash.IsBlocked)
                return Conflict(new ApiResponse<object>(false, new
                {
                    code = clash.Code,
                    existingDocumentId = dup!.Id,
                    existingDocumentNumber = dup.DocumentNumber,
                }, clash.Message));
        }

        try
        {
            var create = new CreateDocumentRequest(
                DocumentType: docType,
                DocumentDate: req.DocumentDate ?? DateTime.UtcNow.AddHours(7).Date,
                DueDate: null,
                ContactId: contactId.Value,
                Reference: req.Reference,
                Notes: req.Notes,
                Lines: req.Lines.Select(l => new DocumentLineRequest(
                    Description: l.Description ?? "",
                    Quantity: l.Quantity <= 0 ? 1 : l.Quantity,
                    Unit: l.Unit ?? "หน่วย",
                    UnitPrice: l.UnitPrice,
                    DiscountPercent: 0,
                    VatRate: l.VatRate,
                    WithholdingTaxRate: l.WithholdingTaxRate,
                    AccountId: null)).ToList());

            var doc = await _documents.CreateDocumentAsync(ctx.CompanyId, create, "api:v1");

            // เลขใบกำกับของผู้ขายเก็บแยก — ใช้ตรวจซ้ำและอ้างอิงตอนเคลมภาษีซื้อ
            if (!string.IsNullOrWhiteSpace(req.SupplierInvoiceNumber))
            {
                var row = await Db.Documents.FirstOrDefaultAsync(
                    d => d.Id == doc.Id && d.CompanyId == ctx.CompanyId, ct);
                if (row != null)
                {
                    row.SupplierInvoiceNumber = req.SupplierInvoiceNumber.Trim();
                    await Db.SaveChangesAsync(ct);
                }
            }

            var usage = await MeterAsync(ctx, Feature, 1, "Document", doc.Id, ct);

            // คืนรหัสเจ้าหนี้ในระบบลูกค้าไปด้วย — ERP ปลายทางเอาไป map ต่อได้ทันที
            var contactExt = await Db.Contacts.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == contactId.Value && c.CompanyId == ctx.CompanyId, ct);

            var contactType = contactExt?.ContactType ?? ContactType.Unknown;
            // ฝ่ายค้าน P-5 (รอบ 193): ผู้ติดต่อของสาขาใหม่ที่ระบบสร้างให้ไม่มีที่อยู่ (ห้ามลอกที่อยู่ สนญ. — คนละสถานประกอบการ)
            // ⇒ บอกผู้เรียกตั้งแต่ตอนสร้างว่าอะไรยังขาดตาม §86/4 ก่อนอนุมัติ แทนที่จะไปตกด่านตอน /approve
            // (ตัวตรวจตัวเดียวกับด่านอนุมัติ/POS/PDF: TaxInvoiceCompletenessChecker.MissingBuyerFields · เฉพาะเอกสารฝั่งขาย)
            var missingBuyer = Helpers.DocumentSide.IsSales(docType)
                ? Accounting.Services.Implementations.Tax.TaxInvoiceCompletenessChecker.MissingBuyerFields(contactExt)
                : Array.Empty<string>();
            return Ok(new ApiResponse<object>(true, new
            {
                documentId = doc.Id,
                documentNumber = doc.DocumentNumber,   // DRAFT-xxx จนกว่าจะอนุมัติ
                status = doc.Status.ToString(),
                doc.SubTotal, doc.VatAmount, doc.WithholdingTaxAmount, doc.TotalAmount,
                contact = new
                {
                    id = contactId.Value,
                    name = contactExt?.Name,
                    externalId = contactExt?.ExternalId,
                    // enum ออกเป็น "ชื่อ" เสมอ — "Unknown" = ระบบยังระบุชนิดไม่ได้
                    // ⇒ แบบยื่น ภ.ง.ด.3/53 และ scheme ของ e-Tax ยังเลือกไม่ถูก
                    contactType = contactType.ToString(),
                    needsContactType = contactType == ContactType.Unknown,
                    // ไม่มีรหัส = ลูกค้าต้องไปผูกก่อน ไม่งั้น import ฝั่งเขาจะพัง
                    needsMapping = string.IsNullOrEmpty(contactExt?.ExternalId),
                    branchCode = contactExt?.BranchCode,
                    // §86/4: ช่องผู้ซื้อที่ยังขาด (เช่น "ที่อยู่ผู้ซื้อ" ของผู้ติดต่อสาขาใหม่) — ว่าง = ครบ
                    missingBuyerFields = missingBuyer,
                },
                billing = new { charged = usage.ChargedAmount, coveredByFreeQuota = usage.CoveredByFreeQuota },
            }, missingBuyer.Count == 0
                ? "สร้างเอกสารสำเร็จ (ฉบับร่าง) — เรียก /approve เพื่อออกเลขที่จริงและลงบัญชี"
                : "สร้างเอกสารสำเร็จ (ฉบับร่าง) — ผู้ติดต่อยังขาด " + string.Join(", ", missingBuyer)
                  + " (§86/4) · เติมผ่าน POST /api/v1/contacts/sync หรือหน้าผู้ติดต่อก่อนเรียก /approve"));
        }
        catch (InvalidOperationException ex)
        {
            // กฎธุรกิจ/ภาษีปฏิเสธ — ข้อความไทยจาก service ใช้บอกลูกค้าได้ตรง ๆ
            return BadRequest(new ApiResponse<string>(false, null, ex.Message));
        }
        // ★ รอบ 184 — `BusinessRuleException` (รหัสบัญชีผิด · บรรทัดติดลบ · จำนวน ≤ 0 ·
        // ส่วนลดติดลบ) **ไม่ได้สืบทอดจาก `InvalidOperationException`** จึงเคยตกไป
        // `catch (Exception)` ข้างล่าง → **500 ข้อความกลบ** ⇒ คู่ค้าเห็นแค่
        // "สร้างเอกสารไม่สำเร็จ" แล้วแก้เองไม่ได้ · รูนี้มีมาก่อนรอบนี้แล้ว
        // (รหัสบัญชีผิด) รอบนี้แค่ทำให้เจอบ่อยขึ้นเพราะด่านบรรทัดติดลบใช้ชนิดนี้
        catch (Accounting.Helpers.BusinessRuleException ex)
        {
            return BadRequest(new ApiResponse<string>(false, null, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "สร้างเอกสาร v1 ล้มเหลว company={Company}", ctx.CompanyId);
            return StatusCode(500, new ApiResponse<string>(false, null,
                "สร้างเอกสารไม่สำเร็จ — ไม่มีการคิดค่าบริการสำหรับรายการนี้"));
        }
    }

    /// <summary>อนุมัติ — ออกเลขที่จริง (gap-free §86/4) + ลงบัญชี
    ///
    /// <para><b>ด่านสิทธิ์</b>: scope <c>documents:write</c> ตอบได้แค่ "คีย์ใบนี้เขียน
    /// ข้อมูลได้ไหม" — <b>ไม่ใช่</b> "ใครอนุมัติเอกสารได้". ERP_REVIEW กำหนดว่า
    /// <b>ทุกทางเข้าอนุมัติ</b> (เว็บ/กฎ/ลายเซ็น/มือถือ/LINE) ต้องผ่าน
    /// <c>DocumentPermissionHelper.CanApproveAsync</c> ตัวเดียวกัน มิฉะนั้น
    /// ทางเข้าภายนอกกลายเป็นประตูหลังที่ข้ามสิทธิ์ทั้งชุด (ราก R5 "ทางเข้าอื่นไม่เดินด่าน")</para>
    ///
    /// <para>คีย์ไม่มี "ผู้ใช้" ในตัวเอง — ใช้ <c>ApiKey.CreatedByUserId</c> คือผู้ที่ออก
    /// คีย์ใบนี้ ซึ่งเป็นคนเดียวกับที่ <c>ApiKeyMiddleware</c> ใส่เป็น
    /// <c>ClaimTypes.NameIdentifier</c> ให้ทั้ง request อยู่แล้ว ⇒ คีย์ทำได้ไม่เกิน
    /// สิทธิ์ของคนที่ออกมัน</para></summary>
    [HttpPost("{documentId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid documentId, CancellationToken ct)
    {
        var (ctx, error) = await ResolveCallerAsync("documents:write", Feature, ct);
        if (error != null) return error;

        var docType = await Db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId && d.CompanyId == ctx!.CompanyId)
            .Select(d => (DocumentType?)d.DocumentType)
            .FirstOrDefaultAsync(ct);
        if (docType == null)
            return NotFound(new ApiResponse<string>(false, null, "ไม่พบเอกสารนี้ในบริษัทของคุณ"));

        var permError = await CheckApprovePermissionAsync(ctx!, docType.Value, ct);
        if (permError != null) return permError;

        try
        {
            DocumentResponse doc;
            IReadOnlyList<string> scanGapWarnings = Array.Empty<string>();
            try
            {
                doc = await _documents.ApproveDocumentAsync(ctx!.CompanyId, documentId, "api:v1");
            }
            catch (Accounting.Services.Implementations.DocumentApprovalWarningsException w)
                when (w.Warnings.Count > 0 && w.Warnings.All(Helpers.OcrApprovalGapWarning.IsGapWarning))
            {
                // รอบ 193 (คำตัดสินเจ้าของข้อ 12): คำเตือน "ยอดจากสแกนไม่ตรงกระดาษ" ([Σ-GAP]) ห้ามขัดจังหวะ API —
                // อนุมัติต่อ (ร่องรอย APPROVE-ACK-WARNINGS + หมายเหตุภายในบนเอกสาร โดย "api:v1") แล้วคืนธง/ข้อความในโครงเดิม
                // · คำเตือนชนิดอื่นยังเดินทางเดิม (ไม่เปลี่ยนพฤติกรรม)
                scanGapWarnings = w.Warnings;
                doc = await _documents.ApproveDocumentAsync(ctx!.CompanyId, documentId, "api:v1", acknowledgeWarnings: true);
            }
            return Ok(new ApiResponse<object>(true, new
            {
                documentId = doc.Id,
                documentNumber = doc.DocumentNumber,
                status = doc.Status.ToString(),
                doc.TotalAmount,
                // รอบ 193: ธงให้ระบบปลายทางรู้ว่าใบนี้มาจากสแกนที่ยอดไม่ตรงกระดาษ (อนุมัติแล้ว ไม่ได้หยุด)
                scanAmountGap = scanGapWarnings.Count > 0,
                warnings = scanGapWarnings,
            }, scanGapWarnings.Count > 0
                ? $"อนุมัติแล้ว — เลขที่ {doc.DocumentNumber} · หมายเหตุ: {string.Join(" · ", scanGapWarnings)}"
                : $"อนุมัติแล้ว — เลขที่ {doc.DocumentNumber}"));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<string>(false, null, ex.Message));
        }
    }

    /// <summary>
    /// ด่านสิทธิ์อนุมัติของทางเข้าภายนอก — คืน <c>null</c> เมื่อผ่าน
    ///
    /// <para>ล้มดังพร้อม "ทางไปต่อ" เสมอ: ข้อความบอกว่าต้องให้สิทธิ์ใคร หรือออกคีย์ใหม่
    /// ด้วยบัญชีไหน (กฎเหล็ก #4 F2 ข้อ 8) — ห้ามคืน 403 เปล่าที่คู่ค้าเดาต่อไม่ถูก</para>
    /// </summary>
    private async Task<IActionResult?> CheckApprovePermissionAsync(
        ApiCallerContext ctx, DocumentType docType, CancellationToken ct)
    {
        var owner = ctx.ApiKeyId.HasValue
            ? await Db.Set<ApiKey>().AsNoTracking()
                .Where(k => k.Id == ctx.ApiKeyId.Value && k.CompanyId == ctx.CompanyId)
                .Select(k => new { k.CreatedByUserId, k.Name })
                .FirstOrDefaultAsync(ct)
            : null;

        // ไม่รู้ว่าใครเป็นเจ้าของคีย์ = ตัดสินสิทธิ์ไม่ได้ → ห้ามเดาว่า "ผ่าน"
        // (เงื่อนไขที่เป็นเท็จเพราะไม่มีข้อมูล ห้ามตกเป็น "ผ่าน" — DECISION_DOCTRINE §1)
        if (owner == null || owner.CreatedByUserId == Guid.Empty)
            return StatusCode(403, new ApiResponse<string>(false, null,
                "API key ใบนี้ไม่ได้ผูกกับผู้ใช้ที่ระบุตัวได้ จึงตรวจสิทธิ์อนุมัติเอกสารไม่ได้ — "
                + "กรุณาออก API key ใบใหม่จากหน้าจัดการ API key ด้วยบัญชีที่มีสิทธิ์ \"อนุมัติเอกสาร\" "
                + "แล้วใช้คีย์ใบใหม่แทน (สร้างเอกสารเป็นฉบับร่างยังทำได้ตามเดิม)"));

        if (await Helpers.DocumentPermissionHelper.CanApproveAsync(
                _permissions, ctx.CompanyId, owner.CreatedByUserId, docType))
            return null;

        return StatusCode(403, new ApiResponse<string>(false, null,
            $"ผู้ใช้ที่ออก API key \"{owner.Name}\" ไม่มีสิทธิ์อนุมัติเอกสารชนิด {docType} — "
            + "ให้เจ้าของบริษัทเพิ่มสิทธิ์ \"อนุมัติเอกสาร\" แก่ผู้ใช้รายนี้ในหน้าบทบาท/สิทธิ์ "
            + "หรือออก API key ใบใหม่ด้วยบัญชีที่มีสิทธิ์อยู่แล้ว. "
            + "เอกสารยังคงอยู่เป็นฉบับร่างและอนุมัติจากหน้าเว็บได้ตามปกติ"));
    }

    /// <summary>
    /// resolve ผู้ติดต่อตามลำดับความแน่นอน — ดูหมายเหตุบนคลาส
    /// </summary>
    private async Task<Guid?> ResolveContactAsync(Guid companyId, CreateRequest req, DocumentType docType, CancellationToken ct)
    {
        if (req.ContactId.HasValue &&
            await Db.Contacts.AnyAsync(c => c.Id == req.ContactId.Value && c.CompanyId == companyId, ct))
            return req.ContactId;

        if (!string.IsNullOrWhiteSpace(req.ContactExternalId))
        {
            var byExt = await Db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.ExternalId == req.ContactExternalId.Trim())
                .Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
            if (byExt.HasValue) return byExt;
        }

        var taxId = Helpers.ThaiTaxIdValidator.Normalize(req.ContactTaxId);
        var taxIdExists = false;
        var taxKey = default(Helpers.ContactKeyMatch);
        if (!string.IsNullOrWhiteSpace(taxId))
        {
            // รอบ 193 ข้อ 20 + ทีม C3: ตัวจับคู่กลาง (เลขภาษี + สาขา) — ส่ง contactBranchCode ต่อจนถึงการหา/สร้าง
            // (เดิมส่ง null เสมอ ⇒ ผู้ซื้อสาขา 8 ได้ผู้ติดต่อสำนักงานใหญ่ = ใบกำกับสาขาผิดตาม §86/4 ทั้งที่
            // /contacts/resolve บอกพาร์ตเนอร์ว่า "ระบบจะสร้างผู้ติดต่อของสาขานี้แยก")
            taxKey = await Helpers.ContactTaxBranchKey.FindAsync(
                Db.Contacts.AsNoTracking(), companyId, taxId, req.ContactBranchCode, ct);
            if (taxKey.ContactId.HasValue) return taxKey.ContactId;
            taxIdExists = taxKey.TaxIdExists;
        }

        if (string.IsNullOrWhiteSpace(req.ContactName) && !taxIdExists) return null;

        // เทียบชื่อข้ามภาษา — จับได้แม้เอกสารพิมพ์ไทยแต่ทะเบียนเก็บอังกฤษ
        // มีผู้ติดต่อเลขนี้แล้วแต่คนละสาขา ⇒ ห้ามเทียบชื่อ (ชื่อเดียวกัน = แถวสาขาอื่นของเลขเดียวกัน) → สร้างแถวสาขานี้
        // ฝ่ายค้าน C-6: ผู้สมัครเทียบชื่อมาจากชุด SoftScope ตัวเดียว — เลขใหม่ ⇒ เฉพาะแถวที่ยังไม่มีเลข (เดิมเทียบทุกผู้ติดต่อ
        // ⇒ เลขใหม่ + ชื่อคล้าย = เอกสารผูกกับนิติบุคคลอื่นที่ถือเลขอื่น) · เลขนี้มีแล้วคนละสาขา ⇒ null = ห้ามเทียบ
        var softScope = Helpers.ContactTaxBranchKey.SoftScope(Db.Contacts.AsNoTracking(), companyId, taxId, taxKey);
        if (softScope != null && !string.IsNullOrWhiteSpace(req.ContactName))
        {
            var candidates = await softScope
                .Where(c => c.IsActive)
                .Select(c => new { c.Id, c.Name })
                .ToListAsync(ct);
            var best = CounterpartyNameMatcher.Best(req.ContactName!, candidates, c => c.Name);
            if (best != null && best.Value.Match.Score >= CounterpartyNameMatcher.ConfidentThreshold)
                return best.Value.Item.Id;
        }

        // ผู้ติดต่อสาขาใหม่ของเลขที่มีอยู่แล้ว: นิติบุคคลเดียวกัน ⇒ ชื่อ (เมื่อไม่ได้ส่งมา) + บทบาทลูกค้า/ผู้จำหน่าย
        // ตามแถวเดิม (ตัวจับคู่กลาง กติกา "ไม่ระบุสาขา" = แถวสำนักงานใหญ่ก่อน) — มิฉะนั้นใบกำกับของสาขาใหม่ตกด่าน
        // "ผู้ติดต่อไม่ได้ตั้งค่าเป็นลูกค้า" ใน CreateDocumentAsync
        var newName = req.ContactName?.Trim();
        bool siblingIsCustomer = false, siblingIsSupplier = false;
        if (taxIdExists)
        {
            var sibling = await Helpers.ContactTaxBranchKey.FindAsync(
                Db.Contacts.AsNoTracking(), companyId, taxId, branchCode: null, ct);
            var sib = sibling.ContactId is Guid siblingId
                ? await Db.Contacts.AsNoTracking()
                    .Where(c => c.Id == siblingId && c.CompanyId == companyId)
                    .Select(c => new { c.Name, c.IsCustomer, c.IsSupplier })
                    .FirstOrDefaultAsync(ct)
                : null;
            if (sib != null)
            {
                if (string.IsNullOrWhiteSpace(newName)) newName = sib.Name;
                siblingIsCustomer = sib.IsCustomer;
                siblingIsSupplier = sib.IsSupplier;
            }
        }
        if (string.IsNullOrWhiteSpace(newName)) return null;
        // บทบาทของแถวใหม่: ตามฝั่งของเอกสารที่กำลังสร้าง (Helpers/DocumentSide ตัวเดียว) + บทบาทของแถวเลขเดียวกัน ·
        // เดิมตั้ง IsSupplier เสมอ ⇒ เอกสารฝั่งขายของคู่ค้ารายใหม่ตกด่านบทบาทใน CreateDocumentAsync
        var isSalesDoc = Helpers.DocumentSide.IsSales(docType);

        // ไม่เจอ → สร้างใหม่ **โดยไม่ใส่ ExternalId** เพื่อให้โผล่ใน /contacts/unmapped
        // ลูกค้าจะได้เห็นและผูกรหัส แทนที่จะปล่อยค้างจนเอกสารยิงกลับไม่ได้
        // ตัวตัดสิน ภ.ง.ด.3 vs 53 และ scheme ของ e-Tax — Helpers/ContactTypeResolver
        // ตัวเดียวของระบบ. เดิมที่นี่เช็ค `Length == 13 && StartsWith('0')` เองโดยไม่ตรวจ
        // checksum แล้ว **ตัดสินไม่ได้ ⇒ เดาเป็น Individual** ⇒ คู่ค้าที่พาร์ตเนอร์ส่ง
        // มาแต่ชื่อกลายเป็น "บุคคลธรรมดาที่พิสูจน์แล้ว" เงียบ ๆ แล้วไปโผล่ใน ภ.ง.ด.3
        // ⇒ ตอนนี้ตัดสินไม่ได้ = ContactType.Unknown ที่เห็นได้บนหน้าผู้ติดต่อ
        var identity = Helpers.ContactTypeResolver.ResolveWithBranch(
            declared: null, taxId: taxId, branchCode: req.ContactBranchCode, name: newName);
        var created = new Contact
        {
            CompanyId = companyId,
            Name = newName,
            TaxId = taxId,
            ContactType = identity.Type,
            BranchCode = identity.BranchCode,
            InternalNotes = identity.IsResolved
                ? null
                : $"[สร้างจาก /api/v1/documents] ยังระบุชนิดผู้ติดต่อไม่ได้ — {identity.Reason}",
            IsCustomer = isSalesDoc || siblingIsCustomer,
            IsSupplier = !isSalesDoc || siblingIsSupplier,
            IsActive = true,
            CreatedBy = "api:v1:auto-contact",
        };
        Db.Contacts.Add(created);
        await Db.SaveChangesAsync(ct);
        return created.Id;
    }
}
