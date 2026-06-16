using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Smart safety + duplicate guards + anomaly detection — single page of
/// validation endpoints ที่ Frontend ใช้ inline ตอน user พิมพ์ (debounced).
/// ลด data quality issues ตั้งแต่ entry point ก่อน save.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/smart")]
[Authorize]
public class SmartSafetyController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public SmartSafetyController(AccountingDbContext db) { _db = db; }

    /// <summary>Thai tax-ID checksum (#6). UI debounce 300ms แล้วเรียก —
    /// แสดง ✓/✗ inline ช่อง input.</summary>
    [HttpGet("validate-tax-id")]
    public ActionResult<ApiResponse<object>> ValidateTaxId([FromQuery] string taxId)
    {
        var r = ThaiTaxIdValidator.Check(taxId);
        return Ok(new ApiResponse<object>(true, new { isValid = r.IsValid, reason = r.Reason }));
    }

    /// <summary>Duplicate-invoice detector (#7) — เตือนเมื่อ vendor +
    /// amount + date ซ้ำกับใบที่บันทึกไปแล้ว 30 วันล่าสุด. ใช้ตอน
    /// frontend หลัง user กรอก contact + total amount (before save).</summary>
    [HttpGet("detect-duplicate-document")]
    public async Task<ActionResult<ApiResponse<object>>> DetectDuplicate(
        Guid companyId,
        [FromQuery] Guid contactId,
        [FromQuery] decimal totalAmount,
        [FromQuery] DateTime documentDate,
        [FromQuery] DocumentType docType,
        [FromQuery] Guid? excludeDocId)
    {
        var windowFrom = documentDate.AddDays(-30);
        var windowTo = documentDate.AddDays(30);
        var candidates = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && d.ContactId == contactId
                && d.DocumentType == docType
                && d.DocumentDate >= windowFrom && d.DocumentDate <= windowTo
                && Math.Abs(d.TotalAmount - totalAmount) < 0.01m
                && (excludeDocId == null || d.Id != excludeDocId.Value)
                && d.Status != DocumentStatus.Voided)
            .OrderByDescending(d => d.DocumentDate)
            .Take(5)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentDate, d.TotalAmount, d.Status })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, new
        {
            hasDuplicate = candidates.Count > 0,
            count = candidates.Count,
            warning = candidates.Count > 0
                ? $"⚠️ พบเอกสารใกล้เคียง {candidates.Count} ใบ (vendor+amount+date) ใน 30 วัน — ตรวจซ้ำก่อนบันทึก"
                : null,
            candidates
        }));
    }

    /// <summary>Period-close warning (#8) — ก่อน save เอกสารวันที่ <
    /// งวดปิด → return shouldWarn + period info.</summary>
    [HttpGet("check-period-status")]
    public async Task<ActionResult<ApiResponse<object>>> CheckPeriodStatus(
        Guid companyId, [FromQuery] DateTime documentDate)
    {
        var period = await _db.FiscalPeriods.AsNoTracking()
            .Where(p => p.CompanyId == companyId
                && p.StartDate <= documentDate && p.EndDate >= documentDate)
            .Select(p => new { p.Id, p.Name, p.Status })
            .FirstOrDefaultAsync();
        if (period == null)
            return Ok(new ApiResponse<object>(true, new {
                shouldWarn = false,
                message = "ไม่พบงวดบัญชี — แนะนำให้สร้างงวดบัญชีก่อนบันทึกเอกสาร"
            }));
        var isClosed = period.Status == FiscalPeriodStatus.Closed;
        return Ok(new ApiResponse<object>(true, new
        {
            periodName = period.Name,
            periodStatus = period.Status.ToString(),
            shouldWarn = isClosed,
            shouldBlock = isClosed,
            message = isClosed
                ? $"งวด \"{period.Name}\" ปิดแล้ว — แก้ไข/บันทึกเอกสารในงวดนี้ไม่ได้"
                : null
        }));
    }

    /// <summary>VAT validation (#9) — รองรับใบกำกับหลายรายการที่บางรายการ
    /// ยกเว้น/0% (mixed VAT). ถ้าส่ง taxableBase (ผลรวมเฉพาะรายการที่คิด VAT)
    /// มา → ตรวจ VAT = taxableBase × rate ตรง ๆ. ถ้าไม่ส่ง → ตรวจแบบช่วง:
    /// VAT ที่ถูกต้องอยู่ใน 0..(subTotal × rate); น้อยกว่าเพดาน = มีรายการ
    /// ยกเว้น (ปกติ ไม่ flag), เกินเพดาน/ติดลบ = ผิดแน่นอน.</summary>
    [HttpGet("validate-vat")]
    public ActionResult<ApiResponse<object>> ValidateVat(
        [FromQuery] decimal subTotal,
        [FromQuery] decimal vatAmount,
        [FromQuery] decimal vatRate = 7,
        [FromQuery] decimal? taxableBase = null)
    {
        var maxVat = Math.Round(subTotal * vatRate / 100m, 2);   // เพดาน: ทุกบาทคิด VAT
        var impliedBase = vatAmount > 0 && vatRate > 0
            ? Math.Round(vatAmount / (vatRate / 100m), 2) : 0m;    // ฐานที่คิด VAT จริง

        if (taxableBase.HasValue)
        {
            // มีฐานภาษีจริง (ผลรวมเฉพาะ line ที่คิด VAT) → ตรวจตรง ๆ
            var expected = Math.Round(taxableBase.Value * vatRate / 100m, 2);
            var diff = Math.Abs(expected - vatAmount);
            var mismatch = diff > 0.05m;
            return Ok(new ApiResponse<object>(true, new
            {
                expectedVat = expected, actualVat = vatAmount, diff, isMismatch = mismatch,
                taxableBase = taxableBase.Value, exemptBase = subTotal - taxableBase.Value,
                suggestion = mismatch
                    ? $"⚠️ VAT ไม่ตรง: ฐานคิดภาษี {taxableBase.Value:N2} × {vatRate}% = {expected:N2} แต่ได้ {vatAmount:N2}"
                    : null
            }));
        }

        // ไม่มี taxableBase → ตรวจแบบช่วง (รองรับ mixed VAT)
        var overCap = vatAmount > maxVat + 0.05m;     // เกินเพดาน = ผิดแน่
        var negative = vatAmount < -0.01m;
        var isMismatch = overCap || negative;
        var isMixed = !isMismatch && vatAmount > 0 && vatAmount < maxVat - 0.05m;
        return Ok(new ApiResponse<object>(true, new
        {
            maxVat, actualVat = vatAmount, impliedBase,
            exemptBaseEstimate = isMixed ? Math.Round(subTotal - impliedBase, 2) : 0m,
            isMismatch, isMixed,
            suggestion = isMismatch
                ? $"⚠️ VAT ผิดปกติ: {vatAmount:N2} เกินเพดาน {vatRate}% ของ {subTotal:N2} ({maxVat:N2}) หรือติดลบ"
                : isMixed
                    ? $"ℹ️ VAT {vatAmount:N2} คิดจากฐาน ~{impliedBase:N2} (มีรายการยกเว้น/0% ~{subTotal - impliedBase:N2}) — ปกติสำหรับใบหลายรายการ"
                    : null
        }));
    }

    /// <summary>Expense anomaly detection (#17) — flag ค่าใช้จ่ายเดือนนี้ที่
    /// spike จาก baseline 6 เดือน. AccountId required (เช่นค่าน้ำมัน 5402,
    /// ค่าเช่า 5102). Threshold default = baseline × 2.</summary>
    [HttpGet("detect-anomaly")]
    public async Task<ActionResult<ApiResponse<object>>> DetectAnomaly(
        Guid companyId,
        [FromQuery] Guid accountId,
        [FromQuery] int? year,
        [FromQuery] int? month)
    {
        var now = DateTime.UtcNow;
        var checkYear = year ?? now.Year;
        var checkMonth = month ?? now.Month;
        var checkMonthStart = new DateTime(checkYear, checkMonth, 1);
        var checkMonthEnd = checkMonthStart.AddMonths(1).AddDays(-1);
        var baselineStart = checkMonthStart.AddMonths(-6);

        var currentTotal = await _db.JournalEntryLines.AsNoTracking()
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.AccountId == accountId
                && l.JournalEntry.EntryDate >= checkMonthStart && l.JournalEntry.EntryDate <= checkMonthEnd)
            .SumAsync(l => l.DebitAmount - l.CreditAmount);

        var baselineMonths = await _db.JournalEntryLines.AsNoTracking()
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.AccountId == accountId
                && l.JournalEntry.EntryDate >= baselineStart && l.JournalEntry.EntryDate < checkMonthStart)
            .GroupBy(l => new { l.JournalEntry.EntryDate.Year, l.JournalEntry.EntryDate.Month })
            .Select(g => g.Sum(l => l.DebitAmount - l.CreditAmount))
            .ToListAsync();

        var avgBaseline = baselineMonths.Count > 0 ? baselineMonths.Average() : 0;
        var threshold = avgBaseline * 2m;
        var isAnomaly = avgBaseline > 0 && currentTotal > threshold;
        var deltaPct = avgBaseline > 0 ? Math.Round((currentTotal - avgBaseline) / avgBaseline * 100, 1) : 0;

        return Ok(new ApiResponse<object>(true, new
        {
            currentMonthTotal = currentTotal,
            avgBaseline,
            baselineMonthsCount = baselineMonths.Count,
            deltaPct,
            isAnomaly,
            warning = isAnomaly
                ? $"⚠️ ค่าใช้จ่ายเดือนนี้ ({currentTotal:N2}) สูงกว่า baseline 6 เดือน ({avgBaseline:N2}) +{deltaPct}% — ตรวจซ้ำ"
                : null
        }));
    }
}
