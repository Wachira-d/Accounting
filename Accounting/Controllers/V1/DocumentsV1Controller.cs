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
///   2. เลขผู้เสียภาษี — กุญแจที่ไม่กำกวม
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
        List<LineRequest>? Lines);

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

        var contactId = await ResolveContactAsync(ctx!.CompanyId, req, ct);
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
                .Where(c => c.Id == contactId.Value && c.CompanyId == ctx.CompanyId)
                .Select(c => new { c.ExternalId, c.Name, c.ContactType })
                .FirstOrDefaultAsync(ct);

            var contactType = contactExt?.ContactType ?? ContactType.Unknown;
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
                },
                billing = new { charged = usage.ChargedAmount, coveredByFreeQuota = usage.CoveredByFreeQuota },
            }, "สร้างเอกสารสำเร็จ (ฉบับร่าง) — เรียก /approve เพื่อออกเลขที่จริงและลงบัญชี"));
        }
        catch (InvalidOperationException ex)
        {
            // กฎธุรกิจ/ภาษีปฏิเสธ — ข้อความไทยจาก service ใช้บอกลูกค้าได้ตรง ๆ
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
            var doc = await _documents.ApproveDocumentAsync(ctx!.CompanyId, documentId, "api:v1");
            return Ok(new ApiResponse<object>(true, new
            {
                documentId = doc.Id,
                documentNumber = doc.DocumentNumber,
                status = doc.Status.ToString(),
                doc.TotalAmount,
            }, $"อนุมัติแล้ว — เลขที่ {doc.DocumentNumber}"));
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
    private async Task<Guid?> ResolveContactAsync(Guid companyId, CreateRequest req, CancellationToken ct)
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
        if (!string.IsNullOrWhiteSpace(taxId))
        {
            var byTax = await Db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == companyId && c.TaxId == taxId)
                .Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
            if (byTax.HasValue) return byTax;
        }

        if (string.IsNullOrWhiteSpace(req.ContactName)) return null;

        // เทียบชื่อข้ามภาษา — จับได้แม้เอกสารพิมพ์ไทยแต่ทะเบียนเก็บอังกฤษ
        var candidates = await Db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.IsActive)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct);
        var best = CounterpartyNameMatcher.Best(req.ContactName, candidates, c => c.Name);
        if (best != null && best.Value.Match.Score >= CounterpartyNameMatcher.ConfidentThreshold)
            return best.Value.Item.Id;

        // ไม่เจอ → สร้างใหม่ **โดยไม่ใส่ ExternalId** เพื่อให้โผล่ใน /contacts/unmapped
        // ลูกค้าจะได้เห็นและผูกรหัส แทนที่จะปล่อยค้างจนเอกสารยิงกลับไม่ได้
        // ตัวตัดสิน ภ.ง.ด.3 vs 53 และ scheme ของ e-Tax — Helpers/ContactTypeResolver
        // ตัวเดียวของระบบ. เดิมที่นี่เช็ค `Length == 13 && StartsWith('0')` เองโดยไม่ตรวจ
        // checksum แล้ว **ตัดสินไม่ได้ ⇒ เดาเป็น Individual** ⇒ คู่ค้าที่พาร์ตเนอร์ส่ง
        // มาแต่ชื่อกลายเป็น "บุคคลธรรมดาที่พิสูจน์แล้ว" เงียบ ๆ แล้วไปโผล่ใน ภ.ง.ด.3
        // ⇒ ตอนนี้ตัดสินไม่ได้ = ContactType.Unknown ที่เห็นได้บนหน้าผู้ติดต่อ
        var identity = Helpers.ContactTypeResolver.ResolveWithBranch(
            declared: null, taxId: taxId, branchCode: null, name: req.ContactName);
        var created = new Contact
        {
            CompanyId = companyId,
            Name = req.ContactName.Trim(),
            TaxId = taxId,
            ContactType = identity.Type,
            BranchCode = identity.BranchCode,
            InternalNotes = identity.IsResolved
                ? null
                : $"[สร้างจาก /api/v1/documents] ยังระบุชนิดผู้ติดต่อไม่ได้ — {identity.Reason}",
            IsSupplier = true,
            IsActive = true,
            CreatedBy = "api:v1:auto-contact",
        };
        Db.Contacts.Add(created);
        await Db.SaveChangesAsync(ct);
        return created.Id;
    }
}
