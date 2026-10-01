using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>แถววันหยุดราชการของแพลตฟอร์ม</summary>
public record PlatformHolidayDto(Guid Id, DateTime Date, string NameTh, string? NameEn, string Kind, string? SourceReference,
    DateTime CreatedAt, string? CreatedBy);

/// <summary>เพิ่มวันหยุด — วันที่ (ปฏิทิน ค.ศ.) + ชื่อไทยบังคับ · ประเภท Public/Substitute/Special</summary>
public record CreatePlatformHolidayRequest(DateTime? Date, string? NameTh, string? NameEn, string? Kind, string? SourceReference);

/// <summary>ภาพรวมของปี + กำหนดยื่นที่เลื่อนเพราะวันหยุด (ให้แอดมินเห็นผลทันทีว่าตารางกระทบอะไร)</summary>
public record PlatformHolidayYearDto(int Year, List<PlatformHolidayDto> Holidays, List<string> ShiftedDeadlines, string Guidance);

/// <summary>
/// วันหยุดราชการระดับแพลตฟอร์ม (รอบ 201 ทีม PL · B-9) — แอดมินแพลตฟอร์มกรอกจากประกาศ ครม./สำนักนายกฯ (ข้อมูลภายนอก · ระบบไม่แต่ง) ·
/// ผู้อ่าน: กำหนดยื่นในปฏิทินภาษี + หน้านำส่ง (<c>PlatformHolidayStore</c> → <c>TaxFilingDeadline</c>) · ตารางว่าง = เลื่อนเฉพาะเสาร์/อาทิตย์ (เดิม)
/// </summary>
[ApiController]
[Route("api/admin/platform-holidays")]
[Authorize(Roles = "SystemAdmin")]
public class AdminPlatformHolidayController : ControllerBase
{
    private static readonly string[] Kinds = { "Public", "Substitute", "Special" };
    private static readonly string[] MonthlyKeys = { "VatPp30", "VatPp36", "WhtPnd1", "WhtPnd3", "WhtPnd53", "WhtPnd54", "SsoSps110" };

    private readonly AccountingDbContext _db;
    private readonly ILogger<AdminPlatformHolidayController> _logger;

    public AdminPlatformHolidayController(AccountingDbContext db, ILogger<AdminPlatformHolidayController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private const string Guidance =
        "กรอกวันหยุดราชการ/วันหยุดชดเชย/วันหยุดพิเศษตามประกาศของแต่ละปี — ระบบใช้เลื่อนกำหนดยื่นแบบภาษีที่ตรงวันหยุดไปวันทำการถัดไป (ป.พ.พ. §193/8) " +
        "ในปฏิทินภาษีและหน้านำส่งภาษี · ยังไม่กรอก = เลื่อนเฉพาะเสาร์/อาทิตย์ (เตือนเร็วกว่ากำหนดจริงในบางงวด — ไม่เตือนช้า) · " +
        "ปฏิทินภาษีที่สร้างไว้แล้วอัปเดตเมื่อกด “สร้างปฏิทินใหม่” ของบริษัทนั้น";

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PlatformHolidayYearDto>>> Get([FromQuery] int? year, CancellationToken ct = default)
    {
        var y = year is >= 2018 and <= 2100 ? year.Value : DateTime.UtcNow.AddHours(7).Year;
        var from = new DateTime(y, 1, 1);
        var to = from.AddYears(1);
        var rows = await _db.PlatformHolidays.AsNoTracking()
            .Where(h => h.Date >= from && h.Date < to)
            .OrderBy(h => h.Date)
            .Select(h => new PlatformHolidayDto(h.Id, h.Date, h.NameTh, h.NameEn, h.Kind, h.SourceReference, h.CreatedAt, h.CreatedBy))
            .ToListAsync(ct);
        // ผลกระทบ: งวดรายเดือนของปีนี้ที่วันครบกำหนดเลื่อนเพราะวันหยุดในตาราง (เทียบกับเสาร์/อาทิตย์อย่างเดียว) — ตัวตัดสินเดียวกับผู้อ่านจริง
        var set = BusinessDayCalendar.ToSet(await _db.PlatformHolidays.AsNoTracking()
            .Where(h => h.Date >= from && h.Date < to.AddMonths(1)).Select(h => h.Date).ToListAsync(ct));
        var shifted = new List<string>();
        foreach (var key in MonthlyKeys)
            for (var m = 1; m <= 12; m++)
            {
                var before = TaxFilingDeadline.For(key, y, m);
                var after = TaxFilingDeadline.For(key, y, m, set);
                if (before != after)
                    shifted.Add($"{key} งวด {m:00}/{y}: กระดาษ {before.Paper:dd/MM} → {after.Paper:dd/MM} · e-Filing {before.EFiling:dd/MM} → {after.EFiling:dd/MM}");
            }
        return Ok(new ApiResponse<PlatformHolidayYearDto>(true, new PlatformHolidayYearDto(y, rows, shifted, Guidance)));
    }

    [HttpPost]
    [Authorize(Roles = "SystemAdmin")]
    [Accounting.Filters.RejectApiKey("เพิ่มวันหยุดราชการของแพลตฟอร์ม")]
    public async Task<ActionResult<ApiResponse<PlatformHolidayDto>>> Create([FromBody] CreatePlatformHolidayRequest request, CancellationToken ct = default)
    {
        if (request?.Date is not DateTime d)
            return BadRequest(new ApiResponse<PlatformHolidayDto>(false, null, "ต้องระบุวันที่"));
        var name = (request.NameTh ?? "").Trim();
        if (name.Length == 0)
            return BadRequest(new ApiResponse<PlatformHolidayDto>(false, null, "ต้องระบุชื่อวันหยุด (ภาษาไทย)"));
        var kind = string.IsNullOrWhiteSpace(request.Kind) ? "Public" : request.Kind.Trim();
        if (!Kinds.Contains(kind))
            return BadRequest(new ApiResponse<PlatformHolidayDto>(false, null, "ประเภทต้องเป็น Public (ราชการ) · Substitute (ชดเชย) · Special (พิเศษ)"));
        var date = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Unspecified);
        if (await _db.PlatformHolidays.AnyAsync(h => h.Date == date, ct))
            return Conflict(new ApiResponse<PlatformHolidayDto>(false, null, $"มีวันหยุดวันที่ {date:dd/MM/yyyy} อยู่แล้ว"));
        var row = new PlatformHoliday
        {
            Date = date,
            NameTh = name.Length > 200 ? name[..200] : name,
            NameEn = string.IsNullOrWhiteSpace(request.NameEn) ? null : request.NameEn.Trim(),
            Kind = kind,
            SourceReference = string.IsNullOrWhiteSpace(request.SourceReference) ? null : request.SourceReference.Trim(),
            CreatedBy = JwtHelper.GetUserIdFromClaims(User).ToString(),
        };
        _db.PlatformHolidays.Add(row);
        await _db.SaveChangesAsync(ct);   // BaseEntity ⇒ audit trail (hash chain) จับให้อัตโนมัติ
        _logger.LogWarning("เพิ่มวันหยุดราชการแพลตฟอร์ม {Date:yyyy-MM-dd} {Name} โดย {User}", date, row.NameTh, row.CreatedBy);
        return Ok(new ApiResponse<PlatformHolidayDto>(true,
            new PlatformHolidayDto(row.Id, row.Date, row.NameTh, row.NameEn, row.Kind, row.SourceReference, row.CreatedAt, row.CreatedBy),
            "เพิ่มวันหยุดแล้ว"));
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = "SystemAdmin")]
    [Accounting.Filters.RejectApiKey("ลบวันหยุดราชการของแพลตฟอร์ม")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(Guid id, CancellationToken ct = default)
    {
        var row = await _db.PlatformHolidays.FirstOrDefaultAsync(h => h.Id == id, ct);
        if (row == null) return NotFound(new ApiResponse<bool>(false, false, "ไม่พบวันหยุดนี้"));
        row.IsDeleted = true;   // soft delete — ร่องรอยคงอยู่ใน audit
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("ลบวันหยุดราชการแพลตฟอร์ม {Date:yyyy-MM-dd} {Name} โดย {User}", row.Date, row.NameTh, row.UpdatedBy);
        return Ok(new ApiResponse<bool>(true, true, "ลบวันหยุดแล้ว"));
    }
}
