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
    private readonly ILogger<DocumentsV1Controller> _logger;

    public DocumentsV1Controller(AccountingDbContext db, IUsageMeteringService metering,
        IDocumentService documents, ILogger<DocumentsV1Controller> logger) : base(db, metering)
    { _documents = documents; _logger = logger; }

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

    /// <summary>สร้างเอกสาร (ค่าเริ่มต้นเป็นฉบับร่าง — เลขจริงออกตอนอนุมัติตาม §86/4)</summary>
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
                var row = await Db.Documents.FirstOrDefaultAsync(d => d.Id == doc.Id, ct);
                if (row != null)
                {
                    row.SupplierInvoiceNumber = req.SupplierInvoiceNumber.Trim();
                    await Db.SaveChangesAsync(ct);
                }
            }

            var usage = await MeterAsync(ctx, Feature, 1, "Document", doc.Id, ct);

            // คืนรหัสเจ้าหนี้ในระบบลูกค้าไปด้วย — ERP ปลายทางเอาไป map ต่อได้ทันที
            var contactExt = await Db.Contacts.AsNoTracking()
                .Where(c => c.Id == contactId.Value)
                .Select(c => new { c.ExternalId, c.Name })
                .FirstOrDefaultAsync(ct);

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

    /// <summary>อนุมัติ — ออกเลขที่จริง (gap-free §86/4) + ลงบัญชี</summary>
    [HttpPost("{documentId:guid}/approve")]
    public async Task<IActionResult> Approve(Guid documentId, CancellationToken ct)
    {
        var (ctx, error) = await ResolveCallerAsync("documents:write", Feature, ct);
        if (error != null) return error;

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
        var created = new Contact
        {
            CompanyId = companyId,
            Name = req.ContactName.Trim(),
            TaxId = taxId,
            ContactType = taxId?.Length == 13 && taxId.StartsWith('0')
                ? ContactType.JuristicPerson : ContactType.Individual,
            IsSupplier = true,
            IsActive = true,
            CreatedBy = "api:v1:auto-contact",
        };
        Db.Contacts.Add(created);
        await Db.SaveChangesAsync(ct);
        return created.Id;
    }
}
