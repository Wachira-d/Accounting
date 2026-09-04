using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.FixedAsset;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/[controller]")]
[Authorize]
public class FixedAssetController : ControllerBase
{
    private readonly IFixedAssetService _assetService;

    public FixedAssetController(IFixedAssetService assetService)
    {
        _assetService = assetService;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<FixedAssetResponse>>>> GetAll(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null)
    {
        var result = await _assetService.GetAllAsync(companyId, new PagedRequest(page, pageSize, search));
        return Ok(new ApiResponse<PagedResponse<FixedAssetResponse>>(true, result));
    }

    [HttpGet("{assetId:guid}")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> GetById(Guid companyId, Guid assetId)
    {
        var result = await _assetService.GetByIdAsync(companyId, assetId);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result));
    }

    /// <summary>สินทรัพย์ที่ระบบสร้างอัตโนมัติจาก PV/PI ที่ผู้ใช้ยังไม่ได้
    /// "ยืนยัน" (NeedsReview=true) — บังคับเติมอายุการใช้งาน + วิธีคิดค่าเสื่อม
    /// + รายละเอียดอื่นก่อนถึงจะลงตารางคิดค่าเสื่อมจริง (กฎเหล็ก #2 §65 ตรี
    /// + TFRS for NPAEs บทที่ 10 ที่ระบุ UsefulLifeYears ต้องผ่านการพิจารณา).
    /// คืน list สั้น + จำนวน — UI โชว์ป้าย badge ที่ sidebar + banner ใน
    /// หน้ารายการสินทรัพย์.</summary>
    [HttpGet("needs-review")]
    public async Task<ActionResult<ApiResponse<List<FixedAssetResponse>>>> GetNeedsReview(Guid companyId)
    {
        var result = await _assetService.GetNeedsReviewAsync(companyId);
        return Ok(new ApiResponse<List<FixedAssetResponse>>(true, result));
    }

    /// <summary>สินทรัพย์ที่ลงทะเบียนจากเอกสารใบนี้ — หน้ารายละเอียดเอกสารเรียก
    /// เพื่อตอบว่า "ใบนี้ลงเป็นสินทรัพย์แล้วหรือยัง" แบบถาวร (ไม่ใช่ toast ที่หายไป)</summary>
    [HttpGet("by-document/{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<List<FixedAssetResponse>>>> GetByDocument(Guid companyId, Guid documentId)
        => Ok(new ApiResponse<List<FixedAssetResponse>>(true,
            await _assetService.GetByDocumentAsync(companyId, documentId)));

    [HttpPost]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> Create(
        Guid companyId, [FromBody] CreateFixedAssetRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.CreateAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<FixedAssetResponse>(true, result, "สร้างสินทรัพย์ถาวรสำเร็จ"));
    }

    [HttpPut("{assetId:guid}")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> Update(
        Guid companyId, Guid assetId, [FromBody] UpdateFixedAssetRequest request)
    {
        var result = await _assetService.UpdateAsync(companyId, assetId, request);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result));
    }

    /// <summary>ลบสินทรัพย์ที่ลงทะเบียนผิด (เฉพาะที่ยังไม่คิดค่าเสื่อมจริง).
    /// asset ที่ใช้งาน/คิดค่าเสื่อมแล้วต้องใช้ dispose/write-off แทน.</summary>
    [HttpDelete("{assetId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid assetId)
    {
        await _assetService.DeleteAsync(companyId, assetId);
        return Ok(new ApiResponse<string>(true, "ลบสินทรัพย์สำเร็จ"));
    }

    [HttpPost("{assetId:guid}/dispose")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> Dispose(
        Guid companyId, Guid assetId, [FromBody] DisposeAssetRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.DisposeAsync(companyId, assetId, request, userId);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result, "จำหน่ายสินทรัพย์สำเร็จ"));
    }

    [HttpPost("{assetId:guid}/writeoff")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> WriteOff(
        Guid companyId, Guid assetId, [FromBody] WriteOffAssetRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.WriteOffAsync(companyId, assetId, request, userId);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result, "ตัดจำหน่ายสินทรัพย์สำเร็จ"));
    }

    [HttpPut("{assetId:guid}/adjust-life")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> AdjustUsefulLife(
        Guid companyId, Guid assetId, [FromBody] AdjustUsefulLifeRequest request)
    {
        var result = await _assetService.AdjustUsefulLifeAsync(companyId, assetId, request);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result, "ปรับอายุการใช้งานสำเร็จ"));
    }

    [HttpGet("{assetId:guid}/depreciations")]
    public async Task<ActionResult<ApiResponse<List<DepreciationResponse>>>> GetDepreciations(Guid companyId, Guid assetId)
    {
        var result = await _assetService.GetDepreciationsAsync(companyId, assetId);
        return Ok(new ApiResponse<List<DepreciationResponse>>(true, result));
    }

    [HttpPost("depreciate")]
    public async Task<ActionResult<ApiResponse<List<DepreciationResponse>>>> CalculateDepreciation(
        Guid companyId, [FromBody] CalculateDepreciationRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.CalculateDepreciationAsync(companyId, request, userId);
        return Ok(new ApiResponse<List<DepreciationResponse>>(true, result, $"คำนวณค่าเสื่อมราคาสำเร็จ {result.Count} รายการ"));
    }

    [HttpPost("{assetId:guid}/revalue")]
    public async Task<ActionResult<ApiResponse<RevaluationResponse>>> Revalue(
        Guid companyId, Guid assetId, [FromBody] RevalueAssetRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.RevalueAsync(companyId, assetId, request, userId);
        return Ok(new ApiResponse<RevaluationResponse>(true, result, "ตีราคาสินทรัพย์ใหม่สำเร็จ"));
    }

    [HttpGet("categories")]
    public async Task<ActionResult<ApiResponse<List<AssetCategoryResponse>>>> GetCategories(Guid companyId)
    {
        var result = await _assetService.GetCategoriesAsync(companyId);
        return Ok(new ApiResponse<List<AssetCategoryResponse>>(true, result));
    }

    [HttpGet("report/register")]
    public async Task<ActionResult<ApiResponse<AssetRegisterReport>>> GetAssetRegisterReport(Guid companyId)
    {
        var result = await _assetService.GetAssetRegisterReportAsync(companyId);
        return Ok(new ApiResponse<AssetRegisterReport>(true, result));
    }

    [HttpGet("{assetId:guid}/report/depreciation-schedule")]
    public async Task<ActionResult<ApiResponse<DepreciationScheduleReport>>> GetDepreciationSchedule(
        Guid companyId, Guid assetId)
    {
        var result = await _assetService.GetDepreciationScheduleAsync(companyId, assetId);
        return Ok(new ApiResponse<DepreciationScheduleReport>(true, result));
    }

    [HttpPost("import")]
    public async Task<ActionResult<ApiResponse<ImportFixedAssetsResult>>> Import(
        Guid companyId, [FromBody] List<ImportFixedAssetRow> rows)
    {
        if (rows.Count > 1000) return BadRequest(new ApiResponse<object>(false, null, "สูงสุด 1,000 รายการต่อครั้ง"));
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.ImportAsync(companyId, rows, userId);
        return Ok(new ApiResponse<ImportFixedAssetsResult>(true, result,
            $"นำเข้าสำเร็จ {result.SuccessCount}/{result.TotalRows} รายการ"));
    }
}
