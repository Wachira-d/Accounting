using Accounting.Models.DTOs;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/dbd")]
[Authorize]
public class DbdLookupController : ControllerBase
{
    private readonly IDbdLookupService _dbdService;

    public DbdLookupController(IDbdLookupService dbdService)
    {
        _dbdService = dbdService;
    }

    /// <summary>ค้นหานิติบุคคลจากชื่อ (autocomplete)</summary>
    [HttpGet("search")]
    public async Task<ActionResult<ApiResponse<List<DbdCompanyResult>>>> Search(
        [FromQuery] string q, [FromQuery] int limit = 10)
    {
        var results = await _dbdService.SearchByNameAsync(q, limit);
        return Ok(new ApiResponse<List<DbdCompanyResult>>(true, results));
    }

    /// <summary>ดึงข้อมูลนิติบุคคลจากเลขทะเบียน 13 หลัก · <c>?branch=00008</c> = ข้อมูลของสาขานั้น
    /// (ชื่อ/ที่อยู่สาขา จากทะเบียน VAT กรมสรรพากร) — ว่าง/00000 = สำนักงานใหญ่ (เส้นเดิม)</summary>
    [HttpGet("juristic/{juristicId}")]
    public async Task<ActionResult<ApiResponse<DbdCompanyResult>>> GetByJuristicId(
        string juristicId, [FromQuery] string? branch = null)
    {
        var isBranch = !Accounting.Helpers.TaxBranchCode.IsHeadOffice(branch);
        var result = isBranch
            ? await _dbdService.GetBranchAsync(juristicId, branch)
            : await _dbdService.GetByJuristicIdAsync(juristicId);
        if (result == null)
            return NotFound(new ApiResponse<DbdCompanyResult>(false, null, isBranch
                ? $"ทะเบียนไม่ยืนยันว่ามี {Accounting.Helpers.TaxBranchCode.Label(branch)} ของเลขนี้ — ตรวจเลขสาขา หรือกรอกที่อยู่สาขาเอง"
                : "ไม่พบข้อมูลนิติบุคคล"));
        return Ok(new ApiResponse<DbdCompanyResult>(true, result));
    }

    /// <summary>ตรวจสอบเลขผู้เสียภาษี</summary>
    [HttpGet("verify-tin/{tin}")]
    public async Task<ActionResult<ApiResponse<TinCheckResult>>> VerifyTin(string tin)
    {
        var result = await _dbdService.VerifyTinAsync(tin);
        return Ok(new ApiResponse<TinCheckResult>(true, result));
    }
}
