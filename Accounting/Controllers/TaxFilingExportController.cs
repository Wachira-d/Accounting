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

    public TaxFilingExportController(ITaxFilingExportService exportService,
        ISensitivityService sensitivity, Data.AccountingDbContext db)
    {
        _exportService = exportService;
        _sensitivity = sensitivity;
        _db = db;
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

    /// <summary>Export ภ.ง.ด.1 (Monthly salary WHT) for e-Filing — sensitive (payroll).</summary>
    [HttpGet("pnd1")]
    public async Task<IActionResult> ExportPnd1(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportPnd1Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
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
    public async Task<IActionResult> ExportPnd1k(Guid companyId, [FromQuery] int year)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportPnd1kAsync(companyId, year);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export ภ.ง.ด.91 (Annual personal-income summary per
    /// employee). Aggregates YTD income + WHT + SSO + PF from every
    /// month's payroll. Sensitive (payroll).</summary>
    [HttpGet("pnd91")]
    public async Task<IActionResult> ExportPnd91(Guid companyId, [FromQuery] int year)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportPnd91Async(companyId, year);
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

    /// <summary>Export สปส.1-10 (SSO monthly contribution) — sensitive (payroll).</summary>
    [HttpGet("sso110")]
    public async Task<IActionResult> ExportSso110(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportSso110Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export สปส.1-03 (ขึ้นทะเบียนผู้ประกันตน) — sensitive (payroll).</summary>
    [HttpGet("sps103")]
    public async Task<IActionResult> ExportSps103(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportSps103Async(companyId, year, month);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>Export สปส.6-09 (แจ้งสิ้นสุดความเป็นผู้ประกันตน) — sensitive (payroll).</summary>
    [HttpGet("sps609")]
    public async Task<IActionResult> ExportSps609(Guid companyId, [FromQuery] int year, [FromQuery] int month)
    {
        var block = await CheckPayrollAsync(companyId); if (block != null) return block;
        var result = await _exportService.ExportSps609Async(companyId, year, month);
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
        if (code is "PND1" or "PND1K" or "SSO110" or "SPS103" or "SPS609")
        {
            var block = await CheckPayrollAsync(companyId); if (block != null) return (ActionResult<ApiResponse<TaxFilingExportResult>>)block;
        }

        var result = code switch
        {
            "PND1" => await _exportService.ExportPnd1Async(companyId, year, month),
            "PND2" => await _exportService.ExportPnd2Async(companyId, year, month),
            "PND3" => await _exportService.ExportPnd3Async(companyId, year, month),
            "PND53" => await _exportService.ExportPnd53Async(companyId, year, month),
            "PND1K" => await _exportService.ExportPnd1kAsync(companyId, year),
            "PND91" => await _exportService.ExportPnd91Async(companyId, year),
            "PP30" => await _exportService.ExportPp30Async(companyId, year, month),
            "PP36" => await _exportService.ExportPp36Async(companyId, year, month),
            "PND54" => await _exportService.ExportPnd54Async(companyId, year, month),
            "SSO110" => await _exportService.ExportSso110Async(companyId, year, month),
            "SPS103" => await _exportService.ExportSps103Async(companyId, year, month),
            "SPS609" => await _exportService.ExportSps609Async(companyId, year, month),
            "PND51" => await _exportService.ExportPnd51Async(companyId, year),
            _ => throw new ArgumentException($"ไม่รู้จักรหัสแบบฟอร์ม: {formCode}")
        };

        // Return preview without file data
        return Ok(new ApiResponse<TaxFilingExportResult>(true, result with { FileData = Array.Empty<byte>() }));
    }
}
