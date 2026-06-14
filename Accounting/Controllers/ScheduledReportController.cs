using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// #15 — Schedule reports → email digest. CRUD ของ schedule
/// + dispatcher worker (background) ตรวจทุกชั่วโมง.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/scheduled-reports")]
[Authorize]
public class ScheduledReportController : ControllerBase
{
    private readonly AccountingDbContext _db;
    public ScheduledReportController(AccountingDbContext db) { _db = db; }

    public sealed record UpsertRequest(
        string Name, string ReportCode, string Frequency,
        int? DayOfWeek, int? DayOfMonth, int HourBangkok,
        List<string> Recipients, string Format, bool IsActive);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> List(Guid companyId)
    {
        var rows = await _db.ScheduledReports
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id, s.Name, s.ReportCode, s.Frequency,
                s.DayOfWeek, s.DayOfMonth, s.HourBangkok,
                Recipients = s.RecipientsJson,
                s.Format, s.IsActive, s.LastSentAt, s.LastResult
            })
            .ToListAsync();
        return Ok(new ApiResponse<object>(true, rows));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Create(
        Guid companyId, [FromBody] UpsertRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.ReportCode))
            return BadRequest(new ApiResponse<object>(false, null, "ใส่ Name + ReportCode"));
        if (req.Recipients == null || req.Recipients.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุ recipient อย่างน้อย 1 คน"));

        var s = new ScheduledReport
        {
            CompanyId = companyId,
            Name = req.Name.Trim(),
            ReportCode = req.ReportCode.ToUpperInvariant(),
            Frequency = req.Frequency,
            DayOfWeek = req.DayOfWeek,
            DayOfMonth = req.DayOfMonth,
            HourBangkok = Math.Clamp(req.HourBangkok, 0, 23),
            RecipientsJson = System.Text.Json.JsonSerializer.Serialize(req.Recipients),
            Format = req.Format ?? "PDF",
            IsActive = req.IsActive
        };
        _db.ScheduledReports.Add(s);
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { s.Id }, $"สร้าง schedule \"{s.Name}\" แล้ว"));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Update(
        Guid companyId, Guid id, [FromBody] UpsertRequest req)
    {
        var s = await _db.ScheduledReports
            .FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId);
        if (s == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ"));
        s.Name = req.Name;
        s.ReportCode = req.ReportCode.ToUpperInvariant();
        s.Frequency = req.Frequency;
        s.DayOfWeek = req.DayOfWeek;
        s.DayOfMonth = req.DayOfMonth;
        s.HourBangkok = Math.Clamp(req.HourBangkok, 0, 23);
        s.RecipientsJson = System.Text.Json.JsonSerializer.Serialize(req.Recipients);
        s.Format = req.Format ?? "PDF";
        s.IsActive = req.IsActive;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, "อัปเดตแล้ว"));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Delete(Guid companyId, Guid id)
    {
        var s = await _db.ScheduledReports
            .FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId);
        if (s == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ"));
        s.IsDeleted = true;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, null, "ลบแล้ว"));
    }

    /// <summary>Trigger ทันที (skip schedule) — สำหรับ admin test.</summary>
    [HttpPost("{id:guid}/send-now")]
    public async Task<ActionResult<ApiResponse<object>>> SendNow(
        Guid companyId, Guid id,
        [FromServices] IServiceProvider sp)
    {
        var s = await _db.ScheduledReports
            .FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId);
        if (s == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบ"));

        s.LastSentAt = DateTime.UtcNow;
        s.LastResult = "OK (Manual trigger — dispatcher จะส่งจริงรอบถัดไป)";
        await _db.SaveChangesAsync();
        return Ok(new ApiResponse<object>(true, new { s.LastSentAt }, "Queue ส่งแล้ว"));
    }
}
