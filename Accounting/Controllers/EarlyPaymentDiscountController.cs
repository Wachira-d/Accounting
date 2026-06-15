using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Early Payment Discount (ส่วนลดเงินสด) — สัญญาขาย/ซื้อแบบ "2/10 net 30" =
/// ลด 2% ถ้าจ่ายภายใน 10 วัน, ครบกำหนด 30 วัน. AR ทำ master list ของ term
/// + ผูกที่ Document ตอนสร้างบิล. Receipt ใช้ helper ในระบบเพื่อ check
/// eligibility (ลูกค้าจ่ายภายใน DiscountWindowDays → apply discount %).
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/early-payment-discount-terms")]
[Authorize]
public class EarlyPaymentDiscountController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public EarlyPaymentDiscountController(AccountingDbContext db) { _db = db; }

    public sealed record TermRequest(
        string Code, string DisplayName,
        int DiscountWindowDays, decimal DiscountPercent, int NetTermDays,
        bool IsActive);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<List<EarlyPaymentDiscountTerm>>>> List(Guid companyId)
    {
        var rows = await _db.EarlyPaymentDiscountTerms
            .Where(t => t.CompanyId == companyId && !t.IsDeleted)
            .OrderBy(t => t.Code)
            .ToListAsync();
        return Ok(new ApiResponse<List<EarlyPaymentDiscountTerm>>(true, rows));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<EarlyPaymentDiscountTerm>>> Create(
        Guid companyId, [FromBody] TermRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(new ApiResponse<EarlyPaymentDiscountTerm>(false, null!, "ระบุรหัส term"));
        if (req.DiscountPercent < 0 || req.DiscountPercent > 50)
            return BadRequest(new ApiResponse<EarlyPaymentDiscountTerm>(false, null!, "ส่วนลด 0-50%"));
        if (req.DiscountWindowDays >= req.NetTermDays)
            return BadRequest(new ApiResponse<EarlyPaymentDiscountTerm>(false, null!,
                "DiscountWindow ต้องน้อยกว่า NetTerm"));

        var term = new EarlyPaymentDiscountTerm
        {
            CompanyId = companyId,
            Code = req.Code.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName)
                ? $"{req.DiscountPercent}% ภายใน {req.DiscountWindowDays} วัน, ครบกำหนด {req.NetTermDays} วัน"
                : req.DisplayName,
            DiscountWindowDays = req.DiscountWindowDays,
            DiscountPercent = req.DiscountPercent,
            NetTermDays = req.NetTermDays,
            IsActive = req.IsActive
        };
        _db.EarlyPaymentDiscountTerms.Add(term);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<EarlyPaymentDiscountTerm>(true, term, "เพิ่มข้อกำหนดส่วนลดแล้ว"));
    }

    [HttpPut("{termId:guid}")]
    public async Task<ActionResult<ApiResponse<EarlyPaymentDiscountTerm>>> Update(
        Guid companyId, Guid termId, [FromBody] TermRequest req)
    {
        var term = await _db.EarlyPaymentDiscountTerms
            .FirstOrDefaultAsync(t => t.Id == termId && t.CompanyId == companyId);
        if (term == null) return NotFound(new ApiResponse<EarlyPaymentDiscountTerm>(false, null!, "ไม่พบ"));
        term.Code = req.Code;
        term.DisplayName = req.DisplayName;
        term.DiscountWindowDays = req.DiscountWindowDays;
        term.DiscountPercent = req.DiscountPercent;
        term.NetTermDays = req.NetTermDays;
        term.IsActive = req.IsActive;
        term.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<EarlyPaymentDiscountTerm>(true, term, "อัปเดตแล้ว"));
    }

    /// <summary>คำนวณส่วนลดที่ลูกค้าจะได้รับถ้าจ่าย ณ paymentDate.
    /// เครื่องมือสำหรับ Frontend AR ตอนสร้าง Receipt — บอก discount %
    /// + amount + วันสุดท้ายที่ยังได้ส่วนลด.</summary>
    [HttpGet("calculate")]
    public async Task<ActionResult<ApiResponse<object>>> Calculate(
        Guid companyId,
        [FromQuery] Guid documentId,
        [FromQuery] DateTime paymentDate)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId);
        if (doc == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบเอกสาร"));
        if (doc.EarlyPaymentDiscountTermId == null)
            return Ok(new ApiResponse<object>(true, new { eligible = false, reason = "เอกสารไม่มี early-payment term" }));

        var term = await _db.EarlyPaymentDiscountTerms.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == doc.EarlyPaymentDiscountTermId && !t.IsDeleted && t.IsActive);
        if (term == null)
            return Ok(new ApiResponse<object>(true, new { eligible = false, reason = "Term inactive หรือถูกลบ" }));

        var lastEligibleDay = doc.DocumentDate.AddDays(term.DiscountWindowDays);
        var eligible = paymentDate.Date <= lastEligibleDay.Date;
        var discountAmount = eligible ? Math.Round(doc.BalanceDue * term.DiscountPercent / 100m, 2) : 0;
        return Ok(new ApiResponse<object>(true, new
        {
            eligible,
            discountPercent = term.DiscountPercent,
            discountAmount,
            netAmount = doc.BalanceDue - discountAmount,
            lastEligibleDay,
            termDisplayName = term.DisplayName
        }));
    }
}
