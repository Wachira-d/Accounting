using Accounting.Helpers;
using Accounting.Models.Constants;
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
    private readonly IPermissionService _permissions;

    public FixedAssetController(IFixedAssetService assetService, IPermissionService permissions)
    {
        _assetService = assetService;
        _permissions = permissions;
    }

    /// <summary>
    /// ด่านสิทธิ์ตัวเดียวของคอนโทรลเลอร์นี้ — ข้อความปฏิเสธต้องบอก **ชื่อคีย์ที่ต้องขอ**
    /// ให้ตรงกันทุกจุด (จุดที่ต่างคนต่างแต่งข้อความจะ drift แน่นอน)
    ///
    /// ⚠️ ที่มา (ผลตรวจทีม E · E-04): ทั้งไฟล์มีแค่ <c>[Authorize]</c> ระดับคลาส
    /// ซึ่งตอบแค่ "ล็อกอินอยู่ไหม" ⇒ สมาชิกคนไหนก็โพสต์ JE ค่าเสื่อม · จำหน่าย
    /// (ลง JE กำไร/ขาดทุน 43030/57110) · ตัดจำหน่าย · ตีราคาใหม่ · ทบทวนอายุได้
    /// </summary>
    private async Task<ActionResult?> RequireAssetAsync(Guid companyId, string permKey, string verb)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await _permissions.HasPermissionAsync(companyId, userId, permKey))
            return null;
        return StatusCode(403, new ApiResponse<object>(false, new
        {
            requiredPermission = permKey.Replace("perm:", ""),
        }, $"ไม่มีสิทธิ์{verb} — ต้องได้รับสิทธิ์ \u201c{permKey.Replace("perm:", "")}\u201d จากเจ้าของบริษัทก่อน"));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResponse<FixedAssetResponse>>>> GetAll(
        Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? search = null,
        [FromQuery] string? assetType = null, [FromQuery] string? status = null, [FromQuery] bool? needsReview = null)
    {
        // รอบ 250 WP-03 (D §2.5): กรองที่เซิร์ฟเวอร์ + แบ่งหน้าจริง (เดิม status ถูกส่งมาแต่ไม่มีผู้อ่าน · ชนิดกรองฝั่งหน้าเว็บจาก 200 แถวแรก)
        var result = await _assetService.GetAllAsync(companyId, new PagedRequest(page, pageSize, search),
            ParseEnum<global::Accounting.Models.Enums.AssetType>(assetType),
            ParseEnum<global::Accounting.Models.Enums.AssetStatus>(status), needsReview);
        return Ok(new ApiResponse<PagedResponse<FixedAssetResponse>>(true, result));
    }

    /// <summary>ตัวนับบนแท็บชนิด + จำนวนรอตรวจ — นับทั้งฐานตามคำค้น/สถานะ (ไม่ใช่เฉพาะหน้าที่โหลด)</summary>
    [HttpGet("counts")]
    public async Task<ActionResult<ApiResponse<FixedAssetTypeCounts>>> GetCounts(
        Guid companyId, [FromQuery] string? search = null, [FromQuery] string? status = null)
    {
        var result = await _assetService.GetTypeCountsAsync(companyId, search,
            ParseEnum<global::Accounting.Models.Enums.AssetStatus>(status));
        return Ok(new ApiResponse<FixedAssetTypeCounts>(true, result));
    }

    /// <summary>ค่าว่าง/สะกดไม่ตรง = ไม่กรอง (ไม่ใช่ 400 — ตัวกรองเป็นตัวเลือกของหน้าจอ)</summary>
    private static TEnum? ParseEnum<TEnum>(string? raw) where TEnum : struct, Enum
        => !string.IsNullOrWhiteSpace(raw) && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : null;

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

    /// <summary>บรรทัดของเอกสารเทียบกับทะเบียนสินทรัพย์ (อ่านอย่างเดียว) — แผงสินทรัพย์บนหน้า
    /// เอกสารใช้วาด “บรรทัดไหนยังไม่ขึ้นทะเบียน” โดยไม่เดา prefix ผังเอง</summary>
    [HttpGet("document-lines/{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<DocumentAssetLinesResponse>>> GetDocumentLines(Guid companyId, Guid documentId)
        => Ok(new ApiResponse<DocumentAssetLinesResponse>(true,
            await _assetService.GetDocumentAssetLinesAsync(companyId, documentId)));

    /// <summary>ขึ้นทะเบียนสินทรัพย์จากเอกสารที่โพสต์แล้ว — ทั้งใบ หรือเฉพาะ <c>lineId</c>
    /// (ซ่อมเคสที่ auto-register ตอนอนุมัติล้ม/ถูกข้าม) · ตรรกะเดียวกับตอนอนุมัติ</summary>
    [HttpPost("from-document/{documentId:guid}")]
    public async Task<ActionResult<ApiResponse<RegisterFromDocumentResult>>> RegisterFromDocument(
        Guid companyId, Guid documentId, [FromQuery] Guid? lineId = null)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "ขึ้นทะเบียนสินทรัพย์") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.RegisterFromDocumentAsync(companyId, documentId, lineId, userId);
        var msg = result.Created > 0
            ? $"ขึ้นทะเบียนสินทรัพย์ {result.Created} รายการ (รอตรวจสอบและยืนยัน)"
            : "ไม่มีรายการใหม่ที่ขึ้นทะเบียน — " + string.Join(" · ", result.Notes);
        return Ok(new ApiResponse<RegisterFromDocumentResult>(true, result, msg));
    }

    // ═══ รอบ 249 — สินทรัพย์ 1 ชิ้น ↔ เอกสารซื้อหลายใบ (erp-review/2026-11-06/ASSET_MULTI_DOCUMENT_DESIGN.md §5) ═══

    /// <summary>บรรทัดเอกสารซื้อที่ผูกเป็นต้นทุนสินทรัพย์ได้ (มัดจำ 118xx · งานระหว่างก่อสร้าง · ผังสินทรัพย์) — อ่านอย่างเดียว</summary>
    [HttpGet("linkable-lines")]
    public async Task<ActionResult<ApiResponse<List<LinkableCostLine>>>> GetLinkableLines(
        Guid companyId, [FromQuery] string? search = null, [FromQuery] Guid? documentId = null)
    {
        // ฝ่ายค้าน L10: ค้นบรรทัดเอกสารซื้อทั้งบริษัท (ยอด · ผู้ขาย) = ใช้ตอนผูก ⇒ สิทธิ์เดียวกับการผูก
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "ค้นเอกสารต้นทุนสินทรัพย์") is { } deny) return deny;
        return Ok(new ApiResponse<List<LinkableCostLine>>(true,
            await _assetService.GetLinkableCostLinesAsync(companyId, search, documentId)));
    }

    /// <summary>สร้างสินทรัพย์ระหว่างจัดหาจากหลายบรรทัด (มัดจำ + งวด + ใบสุดท้าย) — ยังไม่คิดค่าเสื่อม ไม่ลง JE</summary>
    [HttpPost("from-sources")]
    public async Task<ActionResult<ApiResponse<AttachCostSourcesResult>>> CreateFromSources(
        Guid companyId, [FromBody] CreateAssetFromSourcesRequest request)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "ขึ้นทะเบียนสินทรัพย์") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.CreateFromSourcesAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<AttachCostSourcesResult>(true, result,
            $"สร้างสินทรัพย์ระหว่างจัดหาแล้ว — ผูกเอกสาร {result.Linked} รายการ"
            + (result.AbsorbedDraftAssetCodes.Count > 0 ? $" (รวมทะเบียนร่าง {string.Join(", ", result.AbsorbedDraftAssetCodes)} เข้ามาแล้ว)" : "")));
    }

    /// <summary>เอกสารที่เป็นต้นทุนของสินทรัพย์นี้ + สถานะ + ปัญหาที่ตรวจพบ</summary>
    [HttpGet("{assetId:guid}/cost-sources")]
    public async Task<ActionResult<ApiResponse<AssetCostSourcesResponse>>> GetCostSources(Guid companyId, Guid assetId)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "ดูเอกสารต้นทุนสินทรัพย์") is { } deny) return deny;   // L10
        return Ok(new ApiResponse<AssetCostSourcesResponse>(true, await _assetService.GetCostSourcesAsync(companyId, assetId)));
    }

    /// <summary>ผูกบรรทัดเอกสารเพิ่ม — ระหว่างจัดหา: สะสมต้นทุน · โอนแล้ว: ลง JE ย้ายผัง + ปรับต้นทุนไปข้างหน้า</summary>
    [HttpPost("{assetId:guid}/cost-sources")]
    public async Task<ActionResult<ApiResponse<AttachCostSourcesResult>>> AttachCostSources(
        Guid companyId, Guid assetId, [FromBody] AttachCostSourcesRequest request)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "ผูกเอกสารต้นทุนสินทรัพย์") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.AttachCostSourcesAsync(companyId, assetId, request, userId);
        return Ok(new ApiResponse<AttachCostSourcesResult>(true, result,
            $"ผูกเอกสาร {result.Linked} รายการ"
            + (result.JournalEntryId.HasValue ? " และลงรายการโอนต้นทุนแล้ว" : "")
            + (result.AbsorbedDraftAssetCodes.Count > 0 ? $" (รวมทะเบียนร่าง {string.Join(", ", result.AbsorbedDraftAssetCodes)} เข้ามาแล้ว)" : "")));
    }

    /// <summary>ปลดเอกสารที่ยังไม่โอนออกจากสินทรัพย์ (ต้องมีเหตุผล · แถวยังอยู่เป็นประวัติ)</summary>
    [HttpPost("{assetId:guid}/cost-sources/{sourceId:guid}/release")]
    public async Task<ActionResult<ApiResponse<AssetCostSourcesResponse>>> ReleaseCostSource(
        Guid companyId, Guid assetId, Guid sourceId, [FromBody] ReleaseCostSourceRequest request)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "ปลดเอกสารต้นทุนสินทรัพย์") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.ReleaseCostSourceAsync(companyId, assetId, sourceId, request, userId);
        return Ok(new ApiResponse<AssetCostSourcesResponse>(true, result, "ปลดเอกสารออกจากสินทรัพย์แล้ว"));
    }

    /// <summary>โอนเป็นสินทรัพย์พร้อมใช้ — JE Dr ผังสินทรัพย์ / Cr ผังมัดจำ·งานระหว่างก่อสร้าง · ค่าเสื่อมเริ่มจากวันพร้อมใช้</summary>
    [HttpPost("{assetId:guid}/place-in-service")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> PlaceInService(
        Guid companyId, Guid assetId, [FromBody] PlaceInServiceRequest request)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "โอนเป็นสินทรัพย์พร้อมใช้") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.PlaceInServiceAsync(companyId, assetId, request, userId);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result, $"โอน {result.AssetCode} เป็นสินทรัพย์พร้อมใช้แล้ว — ค่าเสื่อมเริ่มเดือนถัดจากวันพร้อมใช้"));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> Create(
        Guid companyId, [FromBody] CreateFixedAssetRequest request)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "ขึ้นทะเบียนสินทรัพย์") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.CreateAsync(companyId, request, userId);
        return StatusCode(201, new ApiResponse<FixedAssetResponse>(true, result, "สร้างสินทรัพย์ถาวรสำเร็จ"));
    }

    [HttpPut("{assetId:guid}")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> Update(
        Guid companyId, Guid assetId, [FromBody] UpdateFixedAssetRequest request)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "แก้ไขทะเบียนสินทรัพย์") is { } deny) return deny;
        var result = await _assetService.UpdateAsync(companyId, assetId, request);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result));
    }

    /// <summary>ลบสินทรัพย์ที่ลงทะเบียนผิด (เฉพาะที่ยังไม่คิดค่าเสื่อมจริง).
    /// asset ที่ใช้งาน/คิดค่าเสื่อมแล้วต้องใช้ dispose/write-off แทน.</summary>
    [HttpDelete("{assetId:guid}")]
    public async Task<ActionResult<ApiResponse<string>>> Delete(Guid companyId, Guid assetId)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "ลบสินทรัพย์") is { } deny) return deny;
        await _assetService.DeleteAsync(companyId, assetId);
        return Ok(new ApiResponse<string>(true, "ลบสินทรัพย์สำเร็จ"));
    }

    [HttpPost("{assetId:guid}/dispose")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> Dispose(
        Guid companyId, Guid assetId, [FromBody] DisposeAssetRequest request)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetDispose, "จำหน่ายสินทรัพย์") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.DisposeAsync(companyId, assetId, request, userId);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result, "จำหน่ายสินทรัพย์สำเร็จ"));
    }

    [HttpPost("{assetId:guid}/writeoff")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> WriteOff(
        Guid companyId, Guid assetId, [FromBody] WriteOffAssetRequest request)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetDispose, "ตัดจำหน่ายสินทรัพย์") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.WriteOffAsync(companyId, assetId, request, userId);
        return Ok(new ApiResponse<FixedAssetResponse>(true, result, "ตัดจำหน่ายสินทรัพย์สำเร็จ"));
    }

    [HttpPut("{assetId:guid}/adjust-life")]
    public async Task<ActionResult<ApiResponse<FixedAssetResponse>>> AdjustUsefulLife(
        Guid companyId, Guid assetId, [FromBody] AdjustUsefulLifeRequest request)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "ทบทวนอายุการใช้งาน") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.AdjustUsefulLifeAsync(companyId, assetId, request, userId);
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
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetDepreciate, "โพสต์ค่าเสื่อมราคาประจำงวด") is { } deny) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.CalculateDepreciationAsync(companyId, request, userId);
        return Ok(new ApiResponse<List<DepreciationResponse>>(true, result, $"คำนวณค่าเสื่อมราคาสำเร็จ {result.Count} รายการ"));
    }

    [HttpPost("{assetId:guid}/revalue")]
    public async Task<ActionResult<ApiResponse<RevaluationResponse>>> Revalue(
        Guid companyId, Guid assetId, [FromBody] RevalueAssetRequest request)
    {
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetDispose, "ตีราคาสินทรัพย์ใหม่") is { } deny) return deny;
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
        if (await RequireAssetAsync(companyId, PermissionKeys.AssetManage, "นำเข้าทะเบียนสินทรัพย์") is { } deny) return deny;
        if (rows.Count > 1000) return BadRequest(new ApiResponse<object>(false, null, "สูงสุด 1,000 รายการต่อครั้ง"));
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _assetService.ImportAsync(companyId, rows, userId);
        return Ok(new ApiResponse<ImportFixedAssetsResult>(true, result,
            $"นำเข้าสำเร็จ {result.SuccessCount}/{result.TotalRows} รายการ"));
    }
}
