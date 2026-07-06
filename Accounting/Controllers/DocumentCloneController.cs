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
                vatNonClaimableReason = l.VatNonClaimableReason
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

        var lines = src.Lines.OrderBy(l => l.LineOrder).Select(l => new DocumentLineRequest(
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
            VatNonClaimableReason: l.VatNonClaimableReason
        )).ToList();

        var docDate = options?.OverrideDate ?? DateTime.UtcNow.Date;
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
            PricesIncludeVat: src.PricesIncludeVat
        );

        // ResolveSignersAsync (PDF ผู้จัดทำ) parse CreatedBy เป็น user GUID —
        // Identity.Name เป็นชื่อ/อีเมล ทำให้เอกสารโคลนไม่มีลายเซ็นผู้จัดทำ
        var createdBy = Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();
        var newDoc = await _docs.CreateDocumentAsync(companyId, req, createdBy);
        return Ok(new ApiResponse<object>(true, new
        {
            newDocumentId = newDoc.Id,
            newDocumentNumber = newDoc.DocumentNumber,
            sourceDocumentNumber = src.DocumentNumber
        }, $"คัดลอกจาก {src.DocumentNumber} → {newDoc.DocumentNumber} เรียบร้อย"));
    }
}
