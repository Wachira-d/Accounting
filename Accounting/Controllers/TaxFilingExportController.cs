using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/tax-filing-export")]
[Authorize]
public class TaxFilingExportController : ControllerBase
{
    private readonly ITaxFilingExportService _exportService;
    private readonly ISensitivityService _sensitivity;
    private readonly Data.AccountingDbContext _db;
    private readonly Services.Implementations.IDbdXbrlExportService _xbrl;
    /// <summary>บันทึกการดาวน์โหลดไฟล์ยื่นที่มีเลขประจำตัวเต็ม (PDPA ม.37 PiiAccessLog) — null = ไม่บันทึก (เทสต์)</summary>
    private readonly Services.Implementations.Pdpa.IPdpaService? _pdpa;
    private readonly IPermissionService _permissions;

    public TaxFilingExportController(ITaxFilingExportService exportService,
        ISensitivityService sensitivity, Data.AccountingDbContext db,
        Services.Implementations.IDbdXbrlExportService xbrl,
        IPermissionService permissions,
        Services.Implementations.Pdpa.IPdpaService? pdpa = null)
    {
        _exportService = exportService;
        _sensitivity = sensitivity;
        _db = db;
        _xbrl = xbrl;
        _permissions = permissions;
        _pdpa = pdpa;
    }

    /// <summary>
    /// <b>ด่าน PII ของ ภ.ง.ด.1/1ก</b> (ฝ่ายค้าน ภ.ง.ด.1 P1-3) — แยก "ไฟล์นำเข้า" กับ "ของที่คนดู":
    /// <list type="bullet">
    /// <item>ไฟล์นำเข้า (txt · txt-tis620 · xlsx · csv) = เลขเต็มเสมอ (ไฟล์ปิดบังยื่นไม่ได้) · ด่าน Payroll.View + PiiAccessLog —
    ///   <b>แบบเดียวกับไฟล์ สปส.1-10/1-03/6-09 และ ภ.ง.ด.91</b> ในคอนโทรลเลอร์นี้ และไฟล์แนบ ภ.ง.ด.1 ของรอบเงินเดือน (ไม่เพิ่ม Pii.View
    ///   ให้เส้นยื่นแบบโดยลำพัง — ผู้ทำเงินเดือนที่ไม่มี Pii.View ยื่นแบบไม่ได้ทันที · ส่งเจ้าของตัดสินใน EXPORT_AUDIT)</item>
    /// <item>รายงานตรวจทาน + ดูตัวอย่าง = ปิดบังเลขผู้มีเงินได้เมื่อไม่มี Pii.View (ตัวตัดสิน <see cref="PiiViewGate"/> ตัวเดียวกับหน้ารายงาน
    ///   ภ.ง.ด.1 ของหน้าเงินเดือน) · เงินได้/ภาษีรายคนยังแสดงภายใต้ Payroll.View (§J คุมเลขประจำตัว — เงินเดือนเป็นข้อมูล payroll ที่ด่าน
    ///   Payroll.View คุมอยู่แล้ว และยอดรายคนจำเป็นต่อการตรวจกับหน้าแบบ)</item>
    /// </list>
    /// </summary>
    private Task<bool> Pnd1IncludePiiAsync(Guid companyId, string fmt, string formName, int year, int? month)
        => PiiViewGate.CanViewAsync(_permissions, _pdpa, User, HttpContext, companyId,
            operation: fmt == "review" ? "Export" : "Read",
            purpose: fmt == "review"
                ? $"ดาวน์โหลดรายงานตรวจทาน {formName} งวด {(month is int m && m > 0 ? $"{m:D2}/{year}" : $"{year}")} แบบไม่ปกปิด"
                : $"ดูตัวอย่างไฟล์ {formName} แบบไม่ปกปิด");

    /// <summary>ภ.ง.ด.1/1ก ตามรูปแบบที่ขอ — ไฟล์นำเข้า: เลขเต็ม + บันทึกร่องรอย · รายงานตรวจทาน: ผ่าน <see cref="Pnd1IncludePiiAsync"/></summary>
    private async Task<IActionResult> Pnd1FileAsync(Guid companyId, int year, int? month, string? format)
    {
        var fmt = Pnd1FileFormat.ParseFormat(format);
        var formName = month == null ? "ภ.ง.ด.1ก" : "ภ.ง.ด.1";
        var includePii = fmt != "review" || await Pnd1IncludePiiAsync(companyId, fmt, formName, year, month);
        var result = month is int mm
            ? await _exportService.ExportPnd1Async(companyId, year, mm, fmt, includePii)
            : await _exportService.ExportPnd1kAsync(companyId, year, fmt, includePii);
        // ไฟล์นำเข้ามีเลขเต็มเสมอ ⇒ บันทึกทุกครั้ง · รายงานตรวจทานแบบไม่ปกปิดบันทึกแล้วในตัวตัดสิน (ปิดบัง = ไม่ได้เข้าถึง PII)
        if (fmt != "review") await LogFilingPiiAsync(companyId, result.FormName, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>ไฟล์ยื่นแบบที่มาจากเงินเดือน (ภ.ง.ด.1/1ก/91 · สปส.) <b>ต้องมีเลขประจำตัวผู้เสียภาษี/เลขบัตรเต็ม</b> ตามแบบของราชการ
    /// (ปิดบังไม่ได้ — ไฟล์ปิดบังยื่นไม่ได้) ⇒ ด่านคือสิทธิ์ Payroll.View ตัวเดียวกับหน้ารายงาน ภ.ง.ด.1 + บันทึกร่องรอยทุกครั้งที่
    /// ดาวน์โหลดไฟล์จริง (PDPA ม.37(4) · รอบ 2026-10-14 ทีม X1 — เดิมดาวน์โหลดได้โดยไม่มีร่องรอย) · พรีวิว (ไม่มีไฟล์) ไม่บันทึก</summary>
    private async Task LogFilingPiiAsync(Guid companyId, string formName, int year, int? month)
    {
        if (_pdpa == null) return;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var period = month is int m && m > 0 ? $"{m:D2}/{year}" : $"{year}";
        await _pdpa.LogPiiAccessAsync(companyId, userId,
            User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "",
            subjectType: "Employee", subjectId: Guid.Empty,
            fieldName: "* (เลขประจำตัวผู้เสียภาษี/เลขบัตร + เงินได้ ในไฟล์ยื่นแบบ)",
            operation: "Export",
            purpose: $"ดาวน์โหลดไฟล์ยื่น {formName} งวด {period} (แบบของราชการต้องใช้เลขเต็ม)",
            ipAddress: HttpContext?.Connection?.RemoteIpAddress?.ToString(),
            userAgent: HttpContext?.Request?.Headers.UserAgent.ToString());
    }

    /// <summary>Verify the caller actually belongs to {companyId}. Required
    /// because [Authorize] only checks "is this a valid JWT?" — without this,
    /// a user authenticated for company A could fetch company B's tax forms
    /// by guessing the GUID. Returns null on success, 403 ActionResult on
    /// failure. Cached per-request via _membershipChecked.</summary>
    private Guid? _membershipCheckedFor;
    private async Task<ActionResult?> EnsureMemberAsync(Guid companyId)
    {
        if (_membershipCheckedFor == companyId) return null;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId).Select(u => new { u.IsSystemAdmin }).FirstOrDefaultAsync();
        if (user == null) return Unauthorized();
        if (user.IsSystemAdmin) { _membershipCheckedFor = companyId; return null; }
        var isMember = await _db.CompanyUsers.AsNoTracking()
            .AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == userId);
        if (!isMember)
            return StatusCode(403, new ApiResponse<object>(false, null,
                "คุณไม่ใช่สมาชิกของบริษัทนี้"));
        _membershipCheckedFor = companyId;
        return null;
    }

    private async Task<ActionResult?> CheckPayrollAsync(Guid companyId)
    {
        var member = await EnsureMemberAsync(companyId); if (member != null) return member;
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (!await _sensitivity.CanViewAsync(companyId, userId, SensitivityKind.Payroll))
            return StatusCode(403, new ApiResponse<object>(false, new
            {
                redacted = true, kind = "Payroll",
                requiredPermission = Models.Constants.PermissionKeys.PayrollView
            }, "ภ.ง.ด.1 มีข้อมูลเงินเดือนรายคน — ต้องมีสิทธิ์ Payroll.View"));
        return null;
    }

    /// <summary>Export ภ.ง.ด.1 (รายเดือน) — <c>format</c> = xlsx (Sheet1 ไม่มีหัว · แนะนำ) · txt (PND1.txt UTF-8 · ค่าตั้งต้น) ·
    /// txt-tis620 (PND1.txt TIS-620) · csv · review (รายงานตรวจทานมีหัว · ปิดบังเลขเมื่อไม่มี Pii.View) · sensitive (payroll) + บันทึก PiiAccessLog</summary>
    [HttpGet("pnd1")]
    public async Task<IActionResult> ExportPnd1(Guid companyId, [FromQuery] int year, [FromQuery] int month,
        [FromQuery] string? format = null)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        return await Pnd1FileAsync(companyId, year, month, format);
    }

    /// <summary>Export ภ.ง.ด.3 (Service WHT individuals) for e-Filing</summary>
    [HttpGet("pnd3")]
    public async Task<IActionResult> ExportPnd3(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var member = await EnsureMemberAsync(companyId); if (member != null) return member;
        var result = await _exportService.ExportPnd3Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.ง.ด.53 (Service WHT companies) for e-Filing</summary>
    [HttpGet("pnd53")]
    public async Task<IActionResult> ExportPnd53(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var member = await EnsureMemberAsync(companyId); if (member != null) return member;
        var result = await _exportService.ExportPnd53Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.ง.ด.1ก (Annual salary summary) — sensitive (payroll).</summary>
    [HttpGet("pnd1k")]
    public async Task<IActionResult> ExportPnd1k(Guid companyId, [FromQuery] int year, [FromQuery] string? format = null)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        return await Pnd1FileAsync(companyId, year, null, format);
    }

    /// <summary>Export ภ.ง.ด.91 (Annual personal-income summary per
    /// employee). Aggregates YTD income + WHT + SSO + PF from every
    /// month's payroll. Sensitive (payroll).</summary>
    [HttpGet("pnd91")]
    public async Task<IActionResult> ExportPnd91(Guid companyId, [FromQuery] int year)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportPnd91Async(companyId, year);
        await LogFilingPiiAsync(companyId, result.FormName, year, null);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.พ.30 (VAT filing report)</summary>
    [HttpGet("pp30")]
    public async Task<IActionResult> ExportPp30(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var member = await EnsureMemberAsync(companyId); if (member != null) return member;
        var result = await _exportService.ExportPp30Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.ง.ด.2 (Monthly dividend WHT). บริษัทจ่ายเงินปันผล
    /// 10% หัก ณ ที่จ่าย — นำส่งภายในวันที่ 7 ของเดือนถัดไป.</summary>
    [HttpGet("pnd2")]
    public async Task<IActionResult> ExportPnd2(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var member = await EnsureMemberAsync(companyId); if (member != null) return member;
        var result = await _exportService.ExportPnd2Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.พ.36 (Foreign service VAT self-assessment per
    /// §83/6). ซื้อบริการต่างประเทศ → self-assess 7% VAT.</summary>
    [HttpGet("pp36")]
    public async Task<IActionResult> ExportPp36(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var member = await EnsureMemberAsync(companyId); if (member != null) return member;
        var result = await _exportService.ExportPp36Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.ง.ด.54 (WHT จ่ายต่างประเทศ — ค่าสิทธิ์/ดอกเบี้ย/
    /// บริการ ที่หัก ณ ที่จ่ายตาม DTA).</summary>
    [HttpGet("pnd54")]
    public async Task<IActionResult> ExportPnd54(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var member = await EnsureMemberAsync(companyId); if (member != null) return member;
        var result = await _exportService.ExportPnd54Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.ง.ด.51 (Half-year CIT §67 ทวิ). คำนวณกำไรครึ่งปี + ประมาณการ
    /// ทั้งปี + ภาษีครึ่งปี = annual/2. ยื่นภายใน 2 เดือนนับจากวันสุดท้ายของ
    /// 6 เดือนแรก. รอบ < 12 เดือน (ปีแรก) ยกเว้น.</summary>
    [HttpGet("pnd51")]
    public async Task<IActionResult> ExportPnd51(Guid companyId, [FromQuery] int year)
    {
        var member = await EnsureMemberAsync(companyId); if (member != null) return member;
        var result = await _exportService.ExportPnd51Async(companyId, year);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export DBD XBRL instance document — งบการเงินรายปีตามมาตรฐาน
    /// TFRS-NPAEs taxonomy. ต้องตั้ง BookkeeperName + BookkeeperCpdNumber ใน
    /// CompanySettings ก่อน (พ.ร.บ.การบัญชี ม.7) — ไม่งั้น throw.</summary>
    [HttpGet("dbd-xbrl")]
    public async Task<IActionResult> ExportDbdXbrl(Guid companyId, [FromQuery] int year, CancellationToken ct)
    {
        var member = await EnsureMemberAsync(companyId); if (member != null) return member;
        var result = await _xbrl.ExportAnnualAsync(companyId, year, ct);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Preview XBRL totals (ไม่ดาวน์โหลด file) — สำหรับ UI โชว์ก่อนยืนยัน</summary>
    [HttpGet("dbd-xbrl/preview")]
    public async Task<ActionResult<ApiResponse<object>>> PreviewDbdXbrl(
        Guid companyId, [FromQuery] int year, CancellationToken ct)
    {
        var member = await EnsureMemberAsync(companyId);
        if (member != null) return (ActionResult<ApiResponse<object>>)member;
        try
        {
            var r = await _xbrl.ExportAnnualAsync(companyId, year, ct);
            return Ok(new ApiResponse<object>(true, new
            {
                r.FileName,
                r.TotalAssets, r.TotalLiabilities, r.TotalEquity,
                r.TotalRevenue, r.TotalExpenses, r.NetIncome,
                r.Summary,
                SizeBytes = r.FileData.Length,
            }, r.Summary));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ApiResponse<object>(false, null, ex.Message));
        }
    }

    /// <summary>Export สปส.1-10 (SSO monthly contribution) — sensitive (payroll).</summary>
    [HttpGet("sso110")]
    public async Task<IActionResult> ExportSso110(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportSso110Async(companyId, year, month);
        await LogFilingPiiAsync(companyId, result.FormName, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export สปส.1-10 เป็น Excel สำหรับแนบใน SSO e-Service
    /// (ส่งข้อมูลเงินสมทบ → แนบไฟล์) — sensitive (payroll).</summary>
    [HttpGet("sso110-excel")]
    public async Task<IActionResult> ExportSso110Excel(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportSso110ExcelAsync(companyId, year, month);
        await LogFilingPiiAsync(companyId, result.FormName, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export สปส.1-03 (ขึ้นทะเบียนผู้ประกันตน) — sensitive (payroll).</summary>
    [HttpGet("sps103")]
    public async Task<IActionResult> ExportSps103(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportSps103Async(companyId, year, month);
        await LogFilingPiiAsync(companyId, result.FormName, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export สปส.6-09 (แจ้งสิ้นสุดความเป็นผู้ประกันตน) — sensitive (payroll).</summary>
    [HttpGet("sps609")]
    public async Task<IActionResult> ExportSps609(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportSps609Async(companyId, year, month);
        await LogFilingPiiAsync(companyId, result.FormName, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Get export summary (preview) without downloading</summary>
    [HttpGet("preview/{formCode}")]
    public async Task<ActionResult<ApiResponse<TaxFilingExportResult>>> Preview(
        Guid companyId, string formCode, [FromQuery] int year, [FromQuery] int month = 0)
    {
        var member = await EnsureMemberAsync(companyId);
        if (member != null) return (ActionResult<ApiResponse<TaxFilingExportResult>>)member;
        var code = formCode.ToUpper();
        // Block preview of payroll-derived forms behind the same Payroll gate.
        // (รอบ 2026-10-14 ทีม X1: เดิมขาด PND91 + SSO110X ⇒ ผู้ไม่มีสิทธิ์เงินเดือนเห็นจำนวนคน/ยอดเงินได้/ยอดสมทบรวมผ่านพรีวิว)
        if (code is "PND1" or "PND1K" or "PND91" or "SSO110" or "SSO110X" or "SPS103" or "SPS609")
        {
            var block = await CheckPayrollAsync(companyId); if (block != null) return (ActionResult<ApiResponse<TaxFilingExportResult>>)block;
        }

        // ภ.ง.ด.1/1ก: ข้อความดูตัวอย่าง (PreviewText) ปิดบังเลขผู้มีเงินได้เมื่อไม่มี Pii.View — หน้าเว็บไม่ดึงไฟล์ยื่นจริงมาแสดงอีก (P1-3) ·
        // previewOnly: ด่านของไฟล์นำเข้า (เลขผู้หัก/สาขา) ไม่กั้นพรีวิว — แสดงเป็นคำเตือนในสรุปแทน (W1c P2-4)
        var pnd1Pii = code is "PND1" or "PND1K"
            && await Pnd1IncludePiiAsync(companyId, "preview", code == "PND1K" ? "ภ.ง.ด.1ก" : "ภ.ง.ด.1", year, code == "PND1K" ? null : month);
        var result = code switch
        {
            "PND1" => await _exportService.ExportPnd1Async(companyId, year, month, includePii: pnd1Pii, previewOnly: true),
            "PND2" => await _exportService.ExportPnd2Async(companyId, year, month),
            "PND3" => await _exportService.ExportPnd3Async(companyId, year, month),
            "PND53" => await _exportService.ExportPnd53Async(companyId, year, month),
            "PND1K" => await _exportService.ExportPnd1kAsync(companyId, year, includePii: pnd1Pii, previewOnly: true),
            "PND91" => await _exportService.ExportPnd91Async(companyId, year),
            "PP30" => await _exportService.ExportPp30Async(companyId, year, month),
            "PP36" => await _exportService.ExportPp36Async(companyId, year, month),
            "PND54" => await _exportService.ExportPnd54Async(companyId, year, month),
            "SSO110" => await _exportService.ExportSso110Async(companyId, year, month),
            "SSO110X" => await _exportService.ExportSso110ExcelAsync(companyId, year, month),
            "SPS103" => await _exportService.ExportSps103Async(companyId, year, month),
            "SPS609" => await _exportService.ExportSps609Async(companyId, year, month),
            "PND51" => await _exportService.ExportPnd51Async(companyId, year),
            _ => throw new ArgumentException($"ไม่รู้จักรหัสแบบฟอร์ม: {formCode}")
        };

        // Return preview without file data
        return Ok(new ApiResponse<TaxFilingExportResult>(true, result with { FileData = Array.Empty<byte>() }));
    }
}
