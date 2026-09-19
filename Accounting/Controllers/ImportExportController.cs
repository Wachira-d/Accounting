using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Import;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/import-export")]
[Authorize]
public class ImportExportController : ControllerBase
{
    private readonly IImportExportService _importExportService;
    private readonly IPermissionService _permissions;

    public ImportExportController(IImportExportService importExportService,
        IPermissionService permissions)
    {
        _importExportService = importExportService;
        _permissions = permissions;
    }

    /// <summary>
    /// ด่านสิทธิ์ของเส้นนำเข้า/ส่งออกไฟล์ — คืน <c>null</c> เมื่อผ่าน
    ///
    /// <para><b>ทำไมเป็นเมธอดไม่ใช่ attribute</b>: สิทธิ์ที่ต้องใช้ขึ้นกับ
    /// <c>entityType</c> ใน body/route ซึ่ง attribute แบบคงที่มองไม่เห็น —
    /// ถ้าใช้คีย์เดียวคลุมทุกชนิด จะได้ด่านที่หลวมเกินสำหรับทะเบียนพนักงาน
    /// และแน่นเกินสำหรับผู้ติดต่อ. แผนที่ชนิด→คีย์อยู่ใน
    /// <c>Helpers/ImportExportPermissionScope</c> ตัวเดียว</para>
    ///
    /// <para><c>[Authorize]</c> ระดับคลาสตอบแค่ "ล็อกอินไหม" · <c>TenantAccessMiddleware</c>
    /// ตอบแค่ "อยู่บริษัทนี้ไหม" — ทั้งคู่<b>ไม่ได้</b>ตอบ "ทำสิ่งนี้ได้ไหม"</para>
    /// </summary>
    private async Task<ActionResult?> DenyAsync(Guid companyId, string? entityType, bool isExport)
    {
        var key = isExport
            ? ImportExportPermissionScope.ForExport(entityType)
            : ImportExportPermissionScope.ForImport(entityType);
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await _permissions.HasPermissionAsync(companyId, userId, key)) return null;
        return StatusCode(403, new ApiResponse<string>(false, null,
            ImportExportPermissionScope.DeniedMessage(entityType, key, isExport)));
    }

    [HttpPost("import")]
    public async Task<ActionResult<ApiResponse<ImportResult>>> Import(
        Guid companyId, [FromBody] ImportRequest request)
    {
        var deny = await DenyAsync(companyId, request?.EntityType, isExport: false);
        if (deny != null) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _importExportService.ImportAsync(companyId, request!, userId);
        return Ok(new ApiResponse<ImportResult>(true, result,
            $"นำเข้าสำเร็จ {result.SuccessCount}/{result.TotalRows} รายการ"));
    }

    [HttpPost("validate")]
    public async Task<ActionResult<ApiResponse<ImportResult>>> Validate(
        Guid companyId, [FromBody] ImportRequest request)
    {
        // ตรวจก่อนนำเข้าต้องเดินด่านเดียวกับนำเข้าจริง — ไม่งั้นผู้ใช้ที่ไม่มีสิทธิ์
        // จะเห็น "ผ่าน" แล้วไปเจอ 403 ตอนกดนำเข้า (และด่านตรวจยังอ่านฐานได้ด้วย)
        var deny = await DenyAsync(companyId, request?.EntityType, isExport: false);
        if (deny != null) return deny;
        var result = await _importExportService.ValidateImportAsync(companyId, request!);
        return Ok(new ApiResponse<ImportResult>(true, result));
    }

    /// <summary>Scan the data against the DB and return rows whose natural
    /// key already exists with different field values. Client uses the
    /// returned conflicts to render a Skip/Overwrite/Merge picker before
    /// calling /import with Resolutions populated.</summary>
    [HttpPost("preview-conflicts")]
    public async Task<ActionResult<ApiResponse<ConflictPreviewResponse>>> PreviewConflicts(
        Guid companyId, [FromBody] ImportRequest request)
    {
        // คืน "ค่าที่ต่างกันของแถวเดิม" = การเปิดเผยข้อมูลในฐาน ⇒ ต้องมีด่านเท่ากัน
        var deny = await DenyAsync(companyId, request?.EntityType, isExport: false);
        if (deny != null) return deny;
        var result = await _importExportService.PreviewConflictsAsync(companyId, request!);
        return Ok(new ApiResponse<ConflictPreviewResponse>(true, result,
            $"พบ {result.Conflicts.Count} รายการขัดแย้ง · ใหม่ {result.NewRowCount} · ซ้ำเหมือนกัน {result.DuplicateExactCount} (รวม {result.TotalRows} แถว)"));
    }

    [HttpGet("templates/{entityType}")]
    public async Task<ActionResult<ApiResponse<ImportTemplateResponse>>> GetTemplate(Guid companyId, string entityType)
    {
        var result = await _importExportService.GetImportTemplateAsync(entityType);
        return Ok(new ApiResponse<ImportTemplateResponse>(true, result));
    }

    [HttpPost("export")]
    public async Task<ActionResult> Export(Guid companyId, [FromBody] ExportRequest request)
    {
        var deny = await DenyAsync(companyId, request?.EntityType, isExport: true);
        if (deny != null) return deny;
        var result = await _importExportService.ExportAsync(companyId, request!);
        return File(result.Data, result.ContentType, result.FileName);
    }

    [HttpGet("exportable-entities")]
    public async Task<ActionResult<ApiResponse<List<string>>>> GetExportableEntities(Guid companyId)
    {
        var result = await _importExportService.GetExportableEntitiesAsync();
        return Ok(new ApiResponse<List<string>>(true, result));
    }

    // ===== Smart Import - AI Column Matching =====

    /// <summary>อัพโหลดไฟล์และให้ AI วิเคราะห์จับคู่ column อัตโนมัติ</summary>
    [HttpPost("smart-import/upload")]
    public async Task<ActionResult<ApiResponse<SmartImportSessionResponse>>> SmartUpload(
        Guid companyId, [FromBody] SmartImportUploadRequest request)
    {
        var deny = await DenyAsync(companyId, request?.EntityType, isExport: false);
        if (deny != null) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _importExportService.UploadAndAnalyzeAsync(companyId, request!, userId);
        return Ok(new ApiResponse<SmartImportSessionResponse>(true, result,
            result.RequiresManualMapping
                ? $"วิเคราะห์เสร็จแล้ว — มี {result.UnmappedColumns} column ที่ต้องจับคู่ด้วยตนเอง"
                : "วิเคราะห์เสร็จแล้ว — จับคู่ column อัตโนมัติทั้งหมด"));
    }

    /// <summary>ดึงสถานะ session</summary>
    [HttpGet("smart-import/sessions/{sessionId:guid}")]
    public async Task<ActionResult<ApiResponse<SmartImportSessionResponse>>> GetSession(
        Guid companyId, Guid sessionId)
    {
        var result = await _importExportService.GetSessionAsync(companyId, sessionId);
        return Ok(new ApiResponse<SmartImportSessionResponse>(true, result));
    }

    /// <summary>ส่ง Manual Mapping สำหรับ column ที่ AI จับคู่ไม่ได้</summary>
    [HttpPost("smart-import/manual-mapping")]
    public async Task<ActionResult<ApiResponse<SmartImportSessionResponse>>> SubmitManualMapping(
        Guid companyId, [FromBody] ManualMappingRequest request)
    {
        // ชนิดข้อมูลอยู่ใน session ไม่ใช่ใน body — รูปเดียวกับ ConfirmImport ข้างล่าง
        // การจับคู่คอลัมน์คือการตัดสินว่า "คอลัมน์นี้คือเงินเดือน/เลขบัตรประชาชน" ⇒
        // เป็นการเขียนแผนนำเข้าจริง ไม่ใช่การอ่าน
        var mapSession = await _importExportService.GetSessionAsync(companyId, request.SessionId);
        var denyMap = await DenyAsync(companyId, mapSession.EntityType, isExport: false);
        if (denyMap != null) return denyMap;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _importExportService.SubmitManualMappingAsync(companyId, request, userId);
        return Ok(new ApiResponse<SmartImportSessionResponse>(true, result,
            result.RequiresManualMapping
                ? $"ยังมี {result.UnmappedColumns} column ที่ต้องจับคู่"
                : "Mapping เรียบร้อย — พร้อม Import"));
    }

    /// <summary>หา conflict ของ session (เทียบกับฐานข้อมูลปัจจุบัน) ก่อน confirm</summary>
    [HttpGet("smart-import/sessions/{sessionId:guid}/preview-conflicts")]
    public async Task<ActionResult<ApiResponse<ConflictPreviewResponse>>> SmartPreviewConflicts(
        Guid companyId, Guid sessionId)
    {
        var result = await _importExportService.PreviewSmartConflictsAsync(companyId, sessionId);
        return Ok(new ApiResponse<ConflictPreviewResponse>(true, result,
            $"พบ {result.Conflicts.Count} รายการขัดแย้ง · ใหม่ {result.NewRowCount} · ซ้ำเหมือนกัน {result.DuplicateExactCount}"));
    }

    /// <summary>AI quality review of the staged import. One DeepSeek call covers
    /// type normalizations (Buddhist year → western, "1.234,50" → 1234.50,
    /// "ใช่/Y" → true), fuzzy duplicate detection beyond exact-key match
    /// (catches "บจก.ABC" vs "บริษัท เอบีซี จำกัด"), per-row quality flags
    /// (price 10× supplier average, name is just initials), semantic field
    /// validation (TaxId checksum, email domain plausibility), and
    /// batch-level pattern detection (price-list vs regional dump vs
    /// re-upload). Operator reviews the result and applies fixes before
    /// committing. Degrades to empty result + UsedAi=false when AI is
    /// disabled or fails — never blocks the import.</summary>
    [HttpPost("smart-import/sessions/{sessionId:guid}/ai-review")]
    public async Task<ActionResult<ApiResponse<ImportAiReviewResponse>>> AiReviewSession(
        Guid companyId, Guid sessionId)
    {
        // ปุ่มนี้ทั้ง **เขียนผลรีวิวลง session** และ **เรียก provider (คิดเงินต่อ call)**
        // จากข้อมูลที่อาจมีเลขบัตรประชาชน/เงินเดือน ⇒ ต้องเป็นคนที่มีสิทธิ์นำเข้าชนิดนั้นจริง
        var reviewSession = await _importExportService.GetSessionAsync(companyId, sessionId);
        var denyReview = await DenyAsync(companyId, reviewSession.EntityType, isExport: false);
        if (denyReview != null) return denyReview;
        var result = await _importExportService.AiReviewSessionAsync(companyId, sessionId);
        var msgParts = new List<string>();
        if (result.UsedAi)
        {
            if (result.Normalizations.Count > 0) msgParts.Add($"normalize {result.Normalizations.Count}");
            if (result.FuzzyDuplicates.Count > 0) msgParts.Add($"fuzzy dup {result.FuzzyDuplicates.Count}");
            if (result.QualityFlags.Count > 0) msgParts.Add($"flag {result.QualityFlags.Count}");
            if (result.FieldValidations.Count > 0) msgParts.Add($"validate {result.FieldValidations.Count}");
            if (result.BatchPatterns.Count > 0) msgParts.Add($"pattern {result.BatchPatterns.Count}");
        }
        var msg = result.UsedAi
            ? (msgParts.Count == 0 ? "AI ตรวจแล้ว ไม่พบประเด็น" : "AI พบ: " + string.Join(" · ", msgParts))
            : "AI ปิดอยู่หรือไม่พร้อม — ใช้ heuristic อย่างเดียว";
        return Ok(new ApiResponse<ImportAiReviewResponse>(true, result, msg));
    }

    /// <summary>ยืนยันและเริ่ม Import ข้อมูล</summary>
    [HttpPost("smart-import/confirm")]
    public async Task<ActionResult<ApiResponse<SmartImportResult>>> ConfirmImport(
        Guid companyId, [FromBody] SmartImportConfirmRequest request)
    {
        // ชนิดข้อมูลอยู่ใน session ไม่ใช่ใน body — ต้องไปอ่านมาก่อนตั้งด่าน
        // (ถ้าข้ามตรงนี้ เส้น smart-import จะเป็นประตูหลังของเส้นนำเข้าปกติ)
        var session = await _importExportService.GetSessionAsync(companyId, request.SessionId);
        var deny = await DenyAsync(companyId, session.EntityType, isExport: false);
        if (deny != null) return deny;
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var result = await _importExportService.ConfirmAndImportAsync(companyId, request, userId);
        return Ok(new ApiResponse<SmartImportResult>(true, result,
            $"นำเข้าสำเร็จ {result.SuccessCount}/{result.TotalRows} รายการ"));
    }

    // ===== Template Downloads =====

    /// <summary>ดาวน์โหลด Template มาตรฐานสำหรับ Import</summary>
    [HttpGet("templates/{entityType}/download")]
    public async Task<ActionResult> DownloadTemplate(Guid companyId, string entityType, [FromQuery] string format = "csv")
    {
        var result = await _importExportService.DownloadTemplateAsync(entityType, format);
        return File(result.FileData, result.ContentType, result.FileName);
    }

    /// <summary>ดึงรายการ Entity ที่สามารถ Import ได้ พร้อมรายละเอียด field</summary>
    [HttpGet("importable-entities")]
    public async Task<ActionResult<ApiResponse<List<ImportableEntityInfo>>>> GetImportableEntities(Guid companyId)
    {
        var result = await _importExportService.GetImportableEntitiesAsync();
        return Ok(new ApiResponse<List<ImportableEntityInfo>>(true, result));
    }
}
