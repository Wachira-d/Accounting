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
    private readonly Accounting.Services.Interfaces.IPermissionService _perms;
    public ScheduledReportController(AccountingDbContext db, Accounting.Services.Interfaces.IPermissionService perms) { _db = db; _perms = perms; }

    /// <summary>รอบ 284 ฝ่ายค้าน HIGH-3: ด่านเดียวของสร้าง/แก้ — สิทธิ์ตามชนิดรายงาน + ผู้รับภายนอกต้องเป็นรายชื่อที่เจ้าของอนุมัติ ·
    /// คืน (ข้อความปฏิเสธ, รายชื่อภายนอกที่อนุมัติหลังบันทึก)</summary>
    private async Task<(ActionResult<ApiResponse<object>>? Deny, List<string> Approved)> GateAsync(Guid companyId, UpsertRequest req, string? previouslyApprovedJson)
    {
        var userId = Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User);
        if (await Accounting.Helpers.ScheduledReportAccess.UserProblemAsync(_db, _perms, companyId, userId, req.ReportCode) is string problem)
            return (StatusCode(403, new ApiResponse<object>(false, new { ruleCode = Accounting.Helpers.ScheduledReportAccess.RuleCode }, problem)), new());
        var members = await Accounting.Helpers.ScheduledReportAccess.MemberEmailsAsync(_db, companyId);
        var role = await _db.CompanyUsers.AsNoTracking().Where(cu => cu.CompanyId == companyId && cu.UserId == userId)
            .Select(cu => (Accounting.Models.Enums.UserRole?)cu.Role).FirstOrDefaultAsync();
        var owner = role is { } r && Accounting.Helpers.BuiltInRolePermissions.GrantsAll(r);
        var prev = Accounting.Helpers.ScheduledReportAccess.ParseList(previouslyApprovedJson);
        if (!owner)
        {
            var bad = Accounting.Helpers.ScheduledReportAccess.UnapprovedRecipients(req.Recipients, members, prev);
            if (bad.Count > 0)
                return (BadRequest(new ApiResponse<object>(false, new { unapprovedRecipients = bad, ruleCode = Accounting.Helpers.ScheduledReportAccess.RuleCode },
                    $"ผู้รับ {string.Join(", ", bad)} ไม่ใช่สมาชิกบริษัท — ผู้รับภายนอกต้องให้เจ้าของบริษัทเพิ่ม (เจ้าของบันทึก = อนุมัติรายชื่อ) · หรือใช้อีเมลของสมาชิกในทีม")), new());
        }
        return (null, Accounting.Helpers.ScheduledReportAccess.ApprovedAfterSave(req.Recipients, members, prev, owner));
    }

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
        // รอบ 284 ฝ่ายค้าน HIGH-3: บอกคีย์ที่รายงานแต่ละชนิดต้องใช้ (server computes · หน้าเว็บแสดงอย่างเดียว) · ชนิดที่ไม่รู้จัก = null
        return Ok(new ApiResponse<object>(true, rows.Select(r => new
        {
            r.Id, r.Name, r.ReportCode, r.Frequency, r.DayOfWeek, r.DayOfMonth, r.HourBangkok, r.Recipients,
            r.Format, r.IsActive, r.LastSentAt, r.LastResult,
            RequiredPermissions = Accounting.Helpers.ScheduledReportAccess.KeysFor(r.ReportCode)?.Select(k => k.Replace("perm:", "")).ToList(),
        }).ToList()));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<object>>> Create(
        Guid companyId, [FromBody] UpsertRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.ReportCode))
            return BadRequest(new ApiResponse<object>(false, null, "ใส่ Name + ReportCode"));
        if (req.Recipients == null || req.Recipients.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ต้องระบุ recipient อย่างน้อย 1 คน"));

        var (deny, approved) = await GateAsync(companyId, req, null);
        if (deny != null) return deny;
        var s = new ScheduledReport
        {
            CompanyId = companyId,
            CreatedBy = Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User).ToString(),   // ผู้รับผิดชอบ (ตรวจสิทธิ์อีกครั้งตอนส่ง)
            ApprovedExternalRecipientsJson = System.Text.Json.JsonSerializer.Serialize(approved),
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
        var (deny, approved) = await GateAsync(companyId, req, s.ApprovedExternalRecipientsJson);
        if (deny != null) return deny;
        s.ApprovedExternalRecipientsJson = System.Text.Json.JsonSerializer.Serialize(approved);
        s.UpdatedBy = Accounting.Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();   // ผู้บันทึกล่าสุด = ผู้รับผิดชอบ
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

    /// <summary>ส่งทันที (ไม่รอรอบตามเวลา) — ส่งจริงผ่านตัวส่งเดียวกับรอบตามเวลา แล้วตอบผลจริง
    /// · ID#25 (รอบ 2026-10-14 ทีม F4): เดิมประทับ LastSentAt + "OK" โดยไม่ได้ส่งอะไร ⇒ หน้าจอบอกส่งแล้ว และรอบจริงถูกข้าม 23 ชม. ·
    /// ฝ่ายค้าน F4: ตอนนี้ส่งตัวเลขการเงินออกนอกระบบจริง ⇒ ต้องมีสิทธิ์ส่งออกรายงาน · ไม่ส่งรายงานที่ถูกลบ · ล็อกเดียวกับรอบตามเวลา (กันส่งซ้ำ)</summary>
    [HttpPost("{id:guid}/send-now")]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.ReportsExport)]
    public async Task<ActionResult<ApiResponse<object>>> SendNow(
        Guid companyId, Guid id,
        [FromServices] IServiceProvider sp,
        [FromServices] ILogger<ScheduledReportController> logger)
    {
        var s = await _db.ScheduledReports
            .FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId && !x.IsDeleted);
        if (s == null) return NotFound(new ApiResponse<object>(false, null, "ไม่พบรายงานตามกำหนดเวลานี้ (อาจถูกลบแล้ว) — รีเฟรชรายการ"));

        Accounting.Helpers.ScheduledReportOutcome.Result? outcome = null;
        // ฝ่ายค้านรอบ 2 P1-2: ล็อก<b>ต่อรายงาน</b> (ตัวเดียวกับรอบตามเวลา) — เดิมถือล็อกรวมของตัวส่งระหว่างสร้าง/ส่ง ⇒ รอบรายชั่วโมงข้ามรายงานของทุกบริษัท
        var ran = await Accounting.Helpers.JobLock.RunExclusiveAsync(_db, Accounting.Helpers.AdvisoryLockKey.ScheduledReportSend,
            s.Id.ToString(), async () =>
            {
                outcome = await Accounting.Services.Implementations.ScheduledReportDispatcher.DispatchAndRecordAsync(
                    sp, _db, s, logger, HttpContext.RequestAborted);
                await _db.SaveChangesAsync();
            }, logger, s.CompanyId, HttpContext.RequestAborted);
        if (!ran || outcome == null)
            return Conflict(new ApiResponse<object>(false, null,
                "รายงานนี้กำลังถูกส่งอยู่ (รอบอัตโนมัติหรือหน้าต่างอื่น) — รอสักครู่แล้วรีเฟรชดูผล ก่อนกด “ส่งทันที” อีกครั้ง (กันผู้รับได้รายงานซ้ำ)"));
        return Ok(new ApiResponse<object>(outcome.AnyDelivered, new { s.LastSentAt, s.LastResult }, s.LastResult));
    }
}
