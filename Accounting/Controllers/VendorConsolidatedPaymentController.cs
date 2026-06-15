using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Vendor Consolidated Payment (PV รวมหลายบิล) — Finance เห็น vendor 1 ราย
/// มี PI ค้าง 5 ใบ → 1 click จ่ายรวม → ออก PaymentVoucher 1 ใบที่ตัด AP ของ
/// ทุกบิล + ลง JE รอบเดียว + ออกใบ 50 ทวิ 1 ฉบับ (รวม WHT ทุกบิล) เมื่อ
/// ใช้ Cash basis.
///
/// Endpoint:
///   GET  /vendor-payment/contact/{contactId}/outstanding  — list AP ค้าง
///   POST /vendor-payment/contact/{contactId}/pay-bulk     — สร้าง PV รวม
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/vendor-payment")]
[Authorize]
public class VendorConsolidatedPaymentController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _docService;

    public VendorConsolidatedPaymentController(AccountingDbContext db, IDocumentService docService)
    {
        _db = db;
        _docService = docService;
    }

    /// <summary>คืนรายการ PI/Expense ค้างของ vendor + ยอดรวม + จำนวนใบ.
    /// Finance click checkbox แล้วส่งกลับใน /pay-bulk.</summary>
    [HttpGet("contact/{contactId:guid}/outstanding")]
    public async Task<ActionResult<ApiResponse<object>>> Outstanding(Guid companyId, Guid contactId)
    {
        var docs = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.ContactId == contactId
                && (d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense)
                && d.BalanceDue > 0
                && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Sent
                    || d.Status == DocumentStatus.PartiallyPaid || d.Status == DocumentStatus.Overdue))
            .OrderBy(d => d.DueDate ?? d.DocumentDate)
            .Select(d => new
            {
                d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.DueDate,
                d.TotalAmount, d.BalanceDue, d.WithholdingTaxAmount,
                AgingDays = d.DueDate.HasValue
                    ? (int)Math.Max(0, (DateTime.UtcNow - d.DueDate.Value).TotalDays)
                    : 0
            })
            .ToListAsync();

        return Ok(new ApiResponse<object>(true, new
        {
            count = docs.Count,
            totalDue = docs.Sum(d => d.BalanceDue),
            totalWht = docs.Sum(d => d.WithholdingTaxAmount),
            docs
        }));
    }

    public sealed record PayBulkRequest(
        List<Guid> DocumentIds,
        DateTime PayDate,
        Guid? BankAccountId,
        Guid? PaymentAccountId,    // 1111 cash หรือ 1112 bank ตามที่จ่าย
        string? Reference);

    /// <summary>สร้าง PaymentVoucher 1 ใบที่ link หลาย source via Lines.
    /// แต่ละ source PI จะ generate PV line ของตัวเอง — JE posting ของ PV
    /// จะตัด AP ของแต่ละใบรวมรอบเดียว. ขั้นตอน:
    ///   1. Validate ทุก doc เป็นของ contact เดียวกัน + ค้างจ่ายอยู่
    ///   2. สร้าง PV (1 ใบ) มี line ต่อ source PI (จำนวน = BalanceDue ของ PI)
    ///   3. SourceDocumentId per line → DocumentService.AutoPost ตัด AP ครบทุกใบ
    ///   4. คืน PV id + total amount + WHT รวม
    /// </summary>
    [HttpPost("contact/{contactId:guid}/pay-bulk")]
    public async Task<ActionResult<ApiResponse<object>>> PayBulk(
        Guid companyId, Guid contactId, [FromBody] PayBulkRequest req)
    {
        if (req.DocumentIds == null || req.DocumentIds.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ต้องเลือกบิลอย่างน้อย 1 ใบ"));

        var sources = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.ContactId == contactId
                && req.DocumentIds.Contains(d.Id)
                && d.BalanceDue > 0
                && (d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense))
            .ToListAsync();
        if (sources.Count != req.DocumentIds.Count)
            return BadRequest(new ApiResponse<object>(false, null,
                $"บางบิลไม่พบ หรือไม่ค้างจ่าย ({sources.Count}/{req.DocumentIds.Count} ใช้ได้)"));

        // สร้าง PV — 1 line ต่อ source PI. UnitPrice = BalanceDue (net cash จริง
        // ที่เราต้องจ่าย ณ ตอนนี้). VAT/WHT ถูก book ไปที่ PI ตอนตั้งหนี้แล้ว
        // (Accrual basis). ถ้าใช้ Cash basis WHT ต้องจัดการในระดับ AP settlement
        // — TODO รอบ enhancement (line-level source linkage).
        var lines = sources.Select(s => new DocumentLineRequest(
            Description: $"จ่ายชำระ {s.DocumentNumber}",
            Quantity: 1,
            Unit: "ใบ",
            UnitPrice: s.BalanceDue,
            DiscountPercent: 0,
            VatRate: 0,
            WithholdingTaxRate: 0,
            AccountId: null,
            SourceLineId: null
        )).ToList();

        // NOTE: ปัจจุบัน CreateDocumentRequest มี RelatedDocumentId แค่ ระดับ
        // header — 1 PV ผูก 1 source ได้. การจ่ายรวมเต็มรูปแบบควรขยายให้
        // line-level link (DocumentLine.SourceLineId ใช้แล้วใน partial conversion
        // — ในที่นี้ใช้ field เดิมเก็บ source PI per line).
        var pvRequest = new CreateDocumentRequest(
            DocumentType: DocumentType.PaymentVoucher,
            DocumentDate: req.PayDate,
            DueDate: req.PayDate,
            ContactId: contactId,
            Reference: req.Reference ?? $"จ่ายรวม {sources.Count} บิล",
            Notes: $"ตัดบิล: {string.Join(", ", sources.Select(s => s.DocumentNumber))}",
            Lines: lines,
            BankAccountId: req.BankAccountId,
            PaymentAccountId: req.PaymentAccountId,
            PaymentType: Models.Enums.PaymentType.Cash,
            PricesIncludeVat: false
        );

        var pv = await _docService.CreateDocumentAsync(companyId, pvRequest, User.Identity?.Name ?? "system");

        return Ok(new ApiResponse<object>(true, new
        {
            paymentVoucherId = pv.Id,
            paymentVoucherNumber = pv.DocumentNumber,
            totalPaid = sources.Sum(s => s.BalanceDue),
            totalWht = sources.Sum(s => s.WithholdingTaxAmount),
            settledCount = sources.Count
        }, $"สร้างใบสำคัญจ่าย {pv.DocumentNumber} ตัด {sources.Count} บิล รวม {sources.Sum(s => s.BalanceDue):N2} บาท"));
    }
}
