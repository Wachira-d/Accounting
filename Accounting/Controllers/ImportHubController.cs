using System.Security.Claims;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Import;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// **ศูนย์นำเข้าข้อมูล (Import Hub / ETL)** — ทางเข้า HTTP ของ pipeline เดียว (รอบ 205 ทีม E · IMPORT_ETL_REVIEW §2.7) ·
/// หน้า <c>wwwroot/pages/import-hub.html</c>
///
/// <para>═══ ด่าน ═══ service ของทีม A/B กรอง <c>CompanyId</c> เองแต่<b>ไม่ตรวจสิทธิ์</b> (สัญญา <see cref="IImportPipelineService"/>) ⇒
/// ทุก action ที่นี่ผ่าน <see cref="DenyAsync"/> ซึ่งถาม <see cref="ImportExportPermissionScope.ForImport"/> ของ <b>ชนิดข้อมูลของ batch</b>
/// (อ่านจากฐานก่อนเรียก service — batch ของบริษัทอื่น/ไม่มีอยู่ ⇒ 404 โดยไม่แตะ service) · ตารางชนิด→คีย์ตัวเดียวกับ
/// <c>ImportExportController</c> และกับด่านไฟล์ต้นทาง (<see cref="AttachmentPermissionScope.ImportBatchKey"/> = <c>ForImport</c>) ⇒ คนที่ดาวน์โหลด
/// ไฟล์ต้นทางได้ = คนที่นำเข้าชนิดนั้นได้ · <c>[Authorize]</c> ตอบแค่ "ล็อกอินไหม" · <c>TenantAccessMiddleware</c> ตอบ "อยู่บริษัทนี้ไหม"</para>
///
/// <para>ข้อยกเว้นที่ตั้งใจ: <c>GET entities</c> ไม่ปิดด้วยคีย์ เพราะเป็นตัวบอกผู้ใช้ว่า "ชนิดไหนนำเข้าได้/ล็อกเพราะขาดสิทธิ์อะไร"
/// (หน้าเว็บล็อกปุ่มพร้อมเหตุผล — ห้าม silent no-op) · ค่า <c>UserCanImport</c> คำนวณที่นี่ด้วยด่านเดียวกับ action อื่น (ไม่เชื่อค่าที่ service ส่งมา)</para>
///
/// <para>ไฟล์นี้อยู่ใน WATCHED ของ <c>tools/write_permission_gate_check.py</c> (ใส่ตอนเขียวตั้งแต่ไฟล์เกิด · ratchet) · ข้อผิดพลาดธุรกิจ =
/// <see cref="BusinessRuleException"/> ของ service ⇒ <c>ExceptionMiddleware</c> ตอบสถานะ + ข้อความไทย</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/import")]
[Authorize]
public class ImportHubController : ControllerBase
{
    /// <summary>เพดานคำขอ multipart (ไฟล์ ≤ 25 MB + ฟิลด์ฟอร์ม) — ไฟล์เองถูกตรวจกับ <see cref="ImportTabularReader.MaxFileBytes"/> อีกชั้น</summary>
    public const long MaxRequestBytes = 30L * 1024 * 1024;

    private readonly IImportPipelineService _pipeline;
    private readonly IImportMappingTemplateStore _templates;
    private readonly IImportTargetRegistry _targets;
    private readonly IOpeningBalanceReconciliation _reconciliation;
    private readonly IPermissionService _permissions;
    private readonly AccountingDbContext _db;

    public ImportHubController(IImportPipelineService pipeline, IImportMappingTemplateStore templates, IImportTargetRegistry targets,
        IOpeningBalanceReconciliation reconciliation, IPermissionService permissions, AccountingDbContext db)
    {
        _pipeline = pipeline;
        _templates = templates;
        _targets = targets;
        _reconciliation = reconciliation;
        _permissions = permissions;
        _db = db;
    }

    // ═════════════════════════════ ด่าน ═════════════════════════════

    private Guid UserId => JwtHelper.GetUserIdFromClaims(User);

    /// <summary>ผู้สั่งงาน (audit / CreatedBy) — ชื่อจาก JWT · IP จากการเชื่อมต่อ</summary>
    private ImportActor Actor()
    {
        var uid = UserId;
        var name = User.FindFirst(ClaimTypes.Name)?.Value;
        return new ImportActor(uid, string.IsNullOrWhiteSpace(name) ? uid.ToString() : name,
            HttpContext?.Connection?.RemoteIpAddress?.ToString());
    }

    /// <summary>ชื่อไทยของชนิดข้อมูล (จากทะเบียน target) — ใช้ในข้อความปฏิเสธแทนคีย์อังกฤษ · ไม่รู้จัก = คีย์เดิม</summary>
    private string EntityLabel(string? entityType)
        => _targets.Find(entityType)?.Label is { Length: > 0 } label ? label : (entityType ?? "");

    /// <summary>
    /// ด่านสิทธิ์นำเข้าของชนิดข้อมูลนี้ — คืน <c>null</c> เมื่อผ่าน · ไม่ผ่าน = 403 + ข้อความไทยที่บอกทางไปต่อ +
    /// <c>requiredPermission</c> (api.js อ่านต่อ) · <paramref name="cache"/> = ผลต่อคีย์ในคำขอเดียว (รายการหลายแถว)
    /// </summary>
    private async Task<ActionResult?> DenyAsync(Guid companyId, string? entityType, Dictionary<string, bool>? cache = null)
    {
        var key = ImportExportPermissionScope.ForImport(entityType);
        if (await CanAsync(companyId, key, cache)) return null;
        return StatusCode(403, new ApiResponse<object>(false, new { requiredPermission = key },
            ImportExportPermissionScope.DeniedMessage(EntityLabel(entityType), key, isExport: false)));
    }

    private async Task<bool> CanAsync(Guid companyId, string key, Dictionary<string, bool>? cache)
    {
        if (cache != null && cache.TryGetValue(key, out var hit)) return hit;
        var ok = await _permissions.HasPermissionAsync(companyId, UserId, key);
        if (cache != null) cache[key] = ok;
        return ok;
    }

    /// <summary>ชนิดข้อมูลของ batch — กรอง CompanyId (batch ของบริษัทอื่น = ไม่พบ) · อ่านคอลัมน์เดียว ไม่ผ่าน service (ด่านต้องมาก่อน service)</summary>
    private async Task<string?> BatchEntityTypeAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        return await _db.SmartImportSessions.AsNoTracking()
            .Where(s => s.Id == batchId && s.CompanyId == companyId)
            .Select(s => s.EntityType)
            .FirstOrDefaultAsync(ct);
    }

    private ObjectResult BatchNotFound()
        => StatusCode(404, new ApiResponse<object>(false, null,
            "ไม่พบชุดนำเข้านี้ในบริษัท — อาจถูกยกเลิก/ลบไปแล้ว หรือเปิดลิงก์ของบริษัทอื่น · กลับไปที่รายการประวัติการนำเข้าแล้วเลือกใหม่"));

    /// <summary>ด่านของ action ที่ระบุ batch: หา batch (กรอง CompanyId — ของบริษัทอื่น/ไม่มี ⇒ 404) แล้ว <see cref="DenyAsync"/> ของชนิดข้อมูลของมัน ·
    /// คืน null เมื่อผ่าน · เรียก<b>ก่อน</b> service ทุกครั้ง (service ไม่ตรวจสิทธิ์ตามสัญญา)</summary>
    private async Task<ActionResult?> DenyBatchAsync(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var entityType = await BatchEntityTypeAsync(companyId, batchId, ct);
        if (entityType == null) return BatchNotFound();
        return await DenyAsync(companyId, entityType);
    }

    // ═════════════════════════════ แคตตาล็อก ═════════════════════════════

    /// <summary>ชนิดข้อมูลที่นำเข้าได้ + สิทธิ์ของผู้ใช้ต่อชนิด + ลำดับย้ายระบบ (§2.10) — ไม่ปิดด้วยคีย์โดยตั้งใจ (ดูคำอธิบายคลาส)</summary>
    [HttpGet("entities")]
    public async Task<ActionResult<ApiResponse<ImportEntityCatalogueDto>>> Entities(Guid companyId, CancellationToken ct)
    {
        var cat = await _pipeline.GetEntityCatalogueAsync(companyId, UserId, ct);
        var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var entities = new List<ImportEntityInfoDto>(cat.Entities.Count);
        foreach (var e in cat.Entities)
        {
            // ด่านเดียวกับทุก action — หน้าเว็บล็อกปุ่มตามค่านี้ ⇒ "ปุ่มเปิดแต่กดแล้ว 403" เกิดไม่ได้
            var key = ImportExportPermissionScope.ForImport(e.EntityType);
            entities.Add(e with { PermissionKey = key, UserCanImport = await CanAsync(companyId, key, cache) });
        }
        return Ok(new ApiResponse<ImportEntityCatalogueDto>(true, cat with { Entities = entities }));
    }

    // ═════════════════════════════ batch ═════════════════════════════

    /// <summary>ประวัติการนำเข้า — ระบุชนิด = ด่านของชนิดนั้น · ไม่ระบุ = ตัดชุดของชนิดที่ผู้ใช้ไม่มีสิทธิ์ออก แล้วบอกจำนวนที่ซ่อน (HiddenCount)</summary>
    [HttpGet("batches")]
    public async Task<ActionResult<ApiResponse<ImportBatchListDto>>> ListBatches(Guid companyId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] string? entityType = null,
        [FromQuery] ImportSessionStatus? status = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            var deny = await DenyAsync(companyId, entityType);
            if (deny != null) return deny;
        }
        else if (!await CanImportAnythingAsync(companyId))
        {
            return NoImportKeyAtAll();
        }
        var list = await _pipeline.ListAsync(companyId,
            new ImportBatchListQuery(Math.Max(1, page), Math.Clamp(pageSize, 1, 100), string.IsNullOrWhiteSpace(entityType) ? null : entityType, status), ct);
        if (!string.IsNullOrWhiteSpace(entityType)) return Ok(new ApiResponse<ImportBatchListDto>(true, list));

        // รายการรวมทุกชนิด: ชุดทะเบียนพนักงานต้องไม่โผล่ให้คนที่ไม่มีสิทธิ์ HR (ชื่อไฟล์/ยอดแถวก็เป็นข้อมูล) — ซ่อนแล้วบอกจำนวน (ไม่โกหกว่าไม่มี)
        var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var visible = new List<ImportBatchSummaryDto>(list.Items.Count);
        foreach (var item in list.Items)
            if (await CanAsync(companyId, ImportExportPermissionScope.ForImport(item.EntityType), cache)) visible.Add(item);
        var hidden = list.Items.Count - visible.Count;
        return Ok(new ApiResponse<ImportBatchListDto>(true, list with { Items = visible, HiddenCount = list.HiddenCount + hidden },
            hidden > 0 ? $"ซ่อน {hidden} ชุดในหน้านี้ที่เป็นข้อมูลชนิดที่บัญชีของคุณไม่มีสิทธิ์นำเข้า" : null));
    }

    /// <summary>ผู้ใช้มีสิทธิ์นำเข้าอย่างน้อยหนึ่งชนิดที่ระบบรู้จัก (ใช้กับคำขอที่ยังไม่รู้ชนิด — อัปโหลดให้ระบบเดา · รายการรวม)</summary>
    private async Task<bool> CanImportAnythingAsync(Guid companyId)
    {
        var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var t in _targets.All)
            if (await CanAsync(companyId, ImportExportPermissionScope.ForImport(t.EntityType), cache)) return true;
        return false;
    }

    private ObjectResult NoImportKeyAtAll()
        => StatusCode(403, new ApiResponse<object>(false, new { requiredPermission = ImportExportPermissionScope.FallbackKey },
            "บัญชีของคุณไม่มีสิทธิ์นำเข้าข้อมูลชนิดใดเลย — ให้เจ้าของบริษัทเพิ่มสิทธิ์ของชนิดข้อมูลที่ต้องนำเข้า (เช่น \"แก้ผู้ติดต่อ\" · \"แก้สินค้า\") "
            + "ให้บทบาทของคุณที่หน้าบทบาท/สิทธิ์ แล้วลองใหม่"));

    /// <summary>
    /// อัปโหลดไฟล์ + สร้าง batch (multipart: <c>file</c> + ฟิลด์ของ <see cref="ImportCreateBatchRequest"/>) — ทุกฟิลด์เว้นได้ เซิร์ฟเวอร์เดาเอง ·
    /// ระบุชนิด = ด่านก่อนอ่านไฟล์ · ไม่ระบุ = ต้องมีสิทธิ์นำเข้าอย่างน้อยหนึ่งชนิด แล้วตรวจชนิดที่เดาได้อีกรอบ (ไม่ผ่าน ⇒ ยกเลิกชุด + 403 พร้อมทางไปต่อ)
    /// </summary>
    [HttpPost("batches")]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<ActionResult<ApiResponse<ImportBatchDetailDto>>> CreateBatch(Guid companyId, IFormFile? file,
        [FromForm] string? entityType, [FromForm] string? sheetName, [FromForm] string? sourceSystem, [FromForm] Guid? templateId,
        [FromForm] int? headerRowIndex, [FromForm] string? dateOrder, [FromForm] string? options, CancellationToken ct)
    {
        var chosen = string.IsNullOrWhiteSpace(entityType) ? null : entityType.Trim();
        if (chosen != null)
        {
            var deny = await DenyAsync(companyId, chosen);
            if (deny != null) return deny;
        }
        else if (!await CanImportAnythingAsync(companyId))
        {
            return NoImportKeyAtAll();
        }

        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<ImportBatchDetailDto>(false, null,
                "เลือกไฟล์ที่จะนำเข้าก่อน (CSV · Excel .xlsx · ไฟล์ที่ส่งออกจากโปรแกรมบัญชีอื่น)"));
        if (file.Length > ImportTabularReader.MaxFileBytes)
            return BadRequest(new ApiResponse<ImportBatchDetailDto>(false, null,
                $"ไฟล์ใหญ่เกิน {ImportTabularReader.MaxFileBytes / (1024 * 1024)} MB — แบ่งไฟล์ (ช่วงวันที่สั้นลง/ทีละชีต) แล้วนำเข้าทีละไฟล์"));
        var order = ImportDateOrder.Auto;
        if (!string.IsNullOrWhiteSpace(dateOrder)
            && (!Enum.TryParse(dateOrder.Trim(), ignoreCase: true, out order) || !Enum.IsDefined(order)))
            return BadRequest(new ApiResponse<ImportBatchDetailDto>(false, null,
                "รูปแบบวันที่ไม่ถูกต้อง — เลือก \"ให้ระบบเดา\" · \"วัน/เดือน/ปี\" หรือ \"เดือน/วัน/ปี\""));

        IReadOnlyDictionary<string, string?>? batchOptions = null;
        if (!string.IsNullOrWhiteSpace(options))
        {
            try
            {
                batchOptions = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(options);
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest(new ApiResponse<ImportBatchDetailDto>(false, null,
                    "ตัวเลือกของชุดนำเข้าอ่านไม่ได้ — เปิดหน้าศูนย์นำเข้าข้อมูลใหม่จากเมนูแล้วลองอีกครั้ง"));
            }
        }

        byte[] bytes;
        await using (var stream = file.OpenReadStream())
        using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }
        var request = new ImportCreateBatchRequest(chosen, Blank(sheetName), Blank(sourceSystem), templateId, headerRowIndex, order, batchOptions);
        var detail = await _pipeline.CreateAsync(companyId, new ImportUploadedFile(file.FileName, file.ContentType, bytes), request, Actor(), ct);

        if (chosen == null)
        {
            // ระบบเดาชนิดเอง ⇒ ด่านของชนิดที่เดาได้ (ไฟล์ทะเบียนพนักงานที่ผู้ไม่มีสิทธิ์ HR อัปโหลดต้องไม่เปิดให้ตรวจ/ลงต่อ)
            var detected = detail.Summary.EntityType;
            var key = ImportExportPermissionScope.ForImport(detected);
            if (!await _permissions.HasPermissionAsync(companyId, UserId, key))
            {
                await _pipeline.CancelAsync(companyId, detail.Summary.Id, Actor(), ct);
                return StatusCode(403, new ApiResponse<object>(false, new { requiredPermission = key },
                    $"ระบบอ่านแล้วเห็นว่าไฟล์นี้เป็นข้อมูล \"{EntityLabel(detected)}\" ซึ่งบัญชีของคุณไม่มีสิทธิ์นำเข้า (ยกเลิกชุดนี้ให้แล้ว) — "
                    + "ถ้าระบบเดาผิด ให้เลือกชนิดข้อมูลเองแล้วอัปโหลดใหม่ · ถ้าถูก ให้เจ้าของบริษัทเพิ่มสิทธิ์ \"" + key + "\" ให้บทบาทของคุณ"));
            }
        }
        return Ok(new ApiResponse<ImportBatchDetailDto>(true, detail));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [HttpGet("batches/{batchId:guid}")]
    public async Task<ActionResult<ApiResponse<ImportBatchDetailDto>>> GetBatch(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        var detail = await _pipeline.GetAsync(companyId, batchId, ct);
        if (detail == null) return BatchNotFound();
        return Ok(new ApiResponse<ImportBatchDetailDto>(true, detail));
    }

    /// <summary>แถวของ batch (server-paged · ค่าเริ่ม 100 แถว/หน้า · สูงสุด 500)</summary>
    [HttpGet("batches/{batchId:guid}/rows")]
    public async Task<ActionResult<ApiResponse<ImportRowPageDto>>> GetRows(Guid companyId, Guid batchId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 100, [FromQuery] ImportRowStatus? status = null,
        [FromQuery] string? issueCode = null, [FromQuery] string? search = null, CancellationToken ct = default)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        var rows = await _pipeline.GetRowsAsync(companyId, batchId,
            new ImportRowQuery(Math.Max(1, page), Math.Clamp(pageSize, 1, 500), status, Blank(issueCode), Blank(search)), ct);
        return Ok(new ApiResponse<ImportRowPageDto>(true, rows));
    }

    /// <summary>วิเคราะห์ใหม่ (ชนิดข้อมูล/ชีต/แถวหัวตาราง/แม่แบบ/ลำดับวันที่) — <b>เปลี่ยนชนิดข้อมูล = ต้องผ่านด่านของทั้งชนิดเดิมและชนิดใหม่</b>
    /// (ไม่งั้นเปลี่ยน "ผู้ติดต่อ" เป็น "พนักงาน" เพื่อเลี่ยงสิทธิ์ HR ได้)</summary>
    [HttpPost("batches/{batchId:guid}/analyze")]
    public async Task<ActionResult<ApiResponse<ImportBatchDetailDto>>> Analyze(Guid companyId, Guid batchId,
        [FromBody] ImportAnalyzeRequest? request, CancellationToken ct)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        var req = request ?? new ImportAnalyzeRequest();
        if (!string.IsNullOrWhiteSpace(req.EntityType))
        {
            var denyNew = await DenyAsync(companyId, req.EntityType);
            if (denyNew != null) return denyNew;
        }
        var detail = await _pipeline.AnalyzeAsync(companyId, batchId, req, Actor(), ct);
        return Ok(new ApiResponse<ImportBatchDetailDto>(true, detail, "วิเคราะห์ไฟล์ใหม่แล้ว — แถวที่คุณแก้เองไม่ถูกทับ"));
    }

    /// <summary>ผู้ใช้จับคู่คอลัมน์เอง ⇒ แปลงแถวใหม่ (ยกเว้นเซลล์ที่ผู้ใช้แก้) · ปิดลูปการเรียนรู้ของการจับคู่คอลัมน์ (service)</summary>
    [HttpPut("batches/{batchId:guid}/mapping")]
    public async Task<ActionResult<ApiResponse<ImportBatchDetailDto>>> Remap(Guid companyId, Guid batchId,
        [FromBody] ImportRemapRequest? request, CancellationToken ct)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        if (request?.Columns == null || request.Columns.Count == 0)
            return BadRequest(new ApiResponse<ImportBatchDetailDto>(false, null, "ไม่มีการจับคู่คอลัมน์ที่ส่งมา — เลือกช่องปลายทางของคอลัมน์อย่างน้อยหนึ่งคอลัมน์"));
        var detail = await _pipeline.RemapAsync(companyId, batchId, request, Actor(), ct);
        return Ok(new ApiResponse<ImportBatchDetailDto>(true, detail, "บันทึกการจับคู่คอลัมน์แล้ว — ตรวจแถวใหม่ตามการจับคู่นี้"));
    }

    /// <summary>แก้แถวเดียว (เซลล์ · action · คู่ที่จับ · ตัดออก · รับ/ปฏิเสธคำแนะนำ)</summary>
    [HttpPatch("batches/{batchId:guid}/rows/{rowId:guid}")]
    public async Task<ActionResult<ApiResponse<ImportRowDto>>> EditRow(Guid companyId, Guid batchId, Guid rowId,
        [FromBody] ImportEditRowRequest? request, CancellationToken ct)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        var row = await _pipeline.EditRowAsync(companyId, batchId, rowId, request ?? new ImportEditRowRequest(), Actor(), ct);
        return Ok(new ApiResponse<ImportRowDto>(true, row));
    }

    /// <summary>คำสั่งยกชุด ("ใช้คำแนะนำทั้งหมด" · ตั้ง action · ตัดออก/รวมกลับ · ตั้งค่าช่อง)</summary>
    [HttpPost("batches/{batchId:guid}/bulk")]
    public async Task<ActionResult<ApiResponse<ImportBulkResultDto>>> Bulk(Guid companyId, Guid batchId,
        [FromBody] ImportBulkActionRequest? request, CancellationToken ct)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        if (request == null)
            return BadRequest(new ApiResponse<ImportBulkResultDto>(false, null, "ไม่ระบุคำสั่ง — เลือกคำสั่งที่จะทำกับแถวที่เลือกก่อน"));
        var result = await _pipeline.BulkActionAsync(companyId, batchId, request, Actor(), ct);
        return Ok(new ApiResponse<ImportBulkResultDto>(true, result, $"ปรับแล้ว {result.Affected} แถว"));
    }

    /// <summary>ลงข้อมูลจริง — เอกสารเป็นร่าง + เลข DRAFT เสมอ (คำตัดสินข้อ 151) · ไฟล์ใหญ่ = Queued (หน้าเว็บ poll GET batches/{id})</summary>
    [HttpPost("batches/{batchId:guid}/commit")]
    public async Task<ActionResult<ApiResponse<ImportCommitResultDto>>> Commit(Guid companyId, Guid batchId,
        [FromBody] ImportCommitOptions? options, CancellationToken ct)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        var result = await _pipeline.CommitAsync(companyId, batchId, options ?? new ImportCommitOptions(), Actor(), ct);
        var msg = result.Queued
            ? "รับคำสั่งแล้ว — ไฟล์ใหญ่ ระบบลงข้อมูลต่อเบื้องหลัง (ดูความคืบหน้าในหน้านี้ได้)"
            : $"ลงข้อมูลแล้ว {result.Committed} แถว · ไม่สำเร็จ {result.Failed} · ข้าม {result.Skipped}";
        return Ok(new ApiResponse<ImportCommitResultDto>(true, result, msg));
    }

    /// <summary>ตัวอย่างการย้อน (อ่านอย่างเดียว) — อะไรย้อนได้/ไม่ได้พร้อมทางไปต่อ</summary>
    [HttpGet("batches/{batchId:guid}/undo-preview")]
    public async Task<ActionResult<ApiResponse<ImportUndoPreviewDto>>> UndoPreview(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        return Ok(new ApiResponse<ImportUndoPreviewDto>(true, await _pipeline.PreviewUndoAsync(companyId, batchId, ct)));
    }

    /// <summary>ย้อนการนำเข้า — เหตุผลบังคับ (service ตรวจ + ตอบไทย · audit IMPORT-UNDO)</summary>
    [HttpPost("batches/{batchId:guid}/undo")]
    public async Task<ActionResult<ApiResponse<ImportUndoResultDto>>> Undo(Guid companyId, Guid batchId,
        [FromBody] ImportUndoRequest? request, CancellationToken ct)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        var result = await _pipeline.UndoAsync(companyId, batchId, request?.Reason, Actor(), ct);
        return Ok(new ApiResponse<ImportUndoResultDto>(true, result,
            result.Blocked > 0 ? $"ย้อนแล้ว {result.Undone} แถว · ย้อนไม่ได้ {result.Blocked} แถว (ดูทางไปต่อในรายการ)" : $"ย้อนแล้ว {result.Undone} แถว"));
    }

    /// <summary>ยกเลิกชุดที่ยังไม่ลงข้อมูล</summary>
    [HttpPost("batches/{batchId:guid}/cancel")]
    public async Task<ActionResult<ApiResponse<ImportBatchSummaryDto>>> Cancel(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        return Ok(new ApiResponse<ImportBatchSummaryDto>(true, await _pipeline.CancelAsync(companyId, batchId, Actor(), ct), "ยกเลิกชุดนำเข้าแล้ว"));
    }

    /// <summary>CSV ของแถวที่ผิด/ล้ม (ค่าดิบ + คอลัมน์ "ปัญหา") — แก้ในโปรแกรมต้นทางแล้วนำเข้าใหม่</summary>
    [HttpGet("batches/{batchId:guid}/errors.csv")]
    public async Task<ActionResult> ErrorsCsv(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        var bytes = await _pipeline.ExportErrorRowsCsvAsync(companyId, batchId, ct);
        return File(bytes, "text/csv; charset=utf-8", $"import-errors-{batchId.ToString("N")[..8]}.csv");
    }

    /// <summary>ไฟล์ต้นทางของ batch — คีย์ = <see cref="AttachmentPermissionScope.ImportBatchKey"/> (= ด่านนำเข้าชนิดของ batch · ไฟล์มี PII ได้)</summary>
    [HttpGet("batches/{batchId:guid}/source-file")]
    public async Task<ActionResult> SourceFile(Guid companyId, Guid batchId, CancellationToken ct)
    {
        var deny = await DenyBatchAsync(companyId, batchId, ct);
        if (deny != null) return deny;
        var file = await _pipeline.GetSourceFileAsync(companyId, batchId, ct);
        if (file == null)
            return StatusCode(404, new ApiResponse<object>(false, null,
                "ชุดนี้ไม่มีไฟล์ต้นทางเก็บไว้ (ชุดที่สร้างก่อนมีศูนย์นำเข้าข้อมูล หรือไฟล์ถูกลบตามระยะเก็บรักษา)"));
        return File(file.Content, file.ContentType, file.FileName);
    }

    // ═════════════════════════════ แม่แบบ ═════════════════════════════

    /// <summary>แม่แบบจับคู่คอลัมน์ — ระบุชนิด = ด่านของชนิดนั้น · ไม่ระบุ = เฉพาะชนิดที่ผู้ใช้มีสิทธิ์</summary>
    [HttpGet("templates")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<ImportTemplateDto>>>> ListTemplates(Guid companyId,
        [FromQuery] string? entityType = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            var deny = await DenyAsync(companyId, entityType);
            if (deny != null) return deny;
            return Ok(new ApiResponse<IReadOnlyList<ImportTemplateDto>>(true, await _templates.ListAsync(companyId, entityType, ct)));
        }
        if (!await CanImportAnythingAsync(companyId)) return NoImportKeyAtAll();
        var all = await _templates.ListAsync(companyId, null, ct);
        var cache = new Dictionary<string, bool>(StringComparer.Ordinal);
        var visible = new List<ImportTemplateDto>(all.Count);
        foreach (var t in all)
            if (await CanAsync(companyId, ImportExportPermissionScope.ForImport(t.EntityType), cache)) visible.Add(t);
        return Ok(new ApiResponse<IReadOnlyList<ImportTemplateDto>>(true, visible));
    }

    /// <summary>แก้ชื่อ/คอลัมน์/AutoCommitWhenClean (opt-in ของผู้ใช้ · คำตัดสินข้อ 151 (3))</summary>
    [HttpPut("templates/{templateId:guid}")]
    public async Task<ActionResult<ApiResponse<ImportTemplateDto>>> UpdateTemplate(Guid companyId, Guid templateId,
        [FromBody] ImportTemplateUpsertRequest? request, CancellationToken ct)
    {
        var existing = await _templates.GetAsync(companyId, templateId, ct);
        if (existing == null) return TemplateNotFound();
        var deny = await DenyAsync(companyId, existing.EntityType);
        if (deny != null) return deny;
        if (request == null)
            return BadRequest(new ApiResponse<ImportTemplateDto>(false, null, "ไม่มีข้อมูลแม่แบบที่จะบันทึก"));
        var updated = await _templates.UpdateAsync(companyId, templateId, request, Actor(), ct);
        if (updated == null) return TemplateNotFound();
        return Ok(new ApiResponse<ImportTemplateDto>(true, updated, "บันทึกแม่แบบแล้ว"));
    }

    [HttpDelete("templates/{templateId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> DeleteTemplate(Guid companyId, Guid templateId, CancellationToken ct)
    {
        var existing = await _templates.GetAsync(companyId, templateId, ct);
        if (existing == null) return TemplateNotFound();
        var deny = await DenyAsync(companyId, existing.EntityType);
        if (deny != null) return deny;
        if (!await _templates.DeleteAsync(companyId, templateId, Actor(), ct)) return TemplateNotFound();
        return Ok(new ApiResponse<object>(true, null, "ลบแม่แบบแล้ว — ไฟล์รูปแบบนี้ครั้งหน้าระบบจะจับคู่คอลัมน์ใหม่ตั้งแต่ต้น"));
    }

    private ObjectResult TemplateNotFound()
        => StatusCode(404, new ApiResponse<object>(false, null, "ไม่พบแม่แบบนี้ในบริษัท — อาจถูกลบไปแล้ว"));

    // ═════════════════════════════ กระทบยอดหลังย้ายระบบ (§2.10 ขั้นสุดท้าย) ═════════════════════════════

    /// <summary>กระทบยอดหลังย้ายระบบ (อ่านอย่างเดียว · ทีม D) — ด่าน = สิทธิ์นำเข้างบทดลอง/ยอดยกมา (Journal.Manage · ชั้นที่เข้มที่สุดของชุดนำเข้า)</summary>
    [HttpGet("migration/reconciliation")]
    public async Task<ActionResult<ApiResponse<ImportReconciliationReportDto>>> Reconciliation(Guid companyId,
        [FromQuery] DateTime? asOf = null, CancellationToken ct = default)
    {
        var deny = await DenyAsync(companyId, OpeningBalanceEntityType);
        if (deny != null) return deny;
        var report = await _reconciliation.ReconcileAsync(companyId, (asOf ?? DateTime.UtcNow).Date, ct);
        return Ok(new ApiResponse<ImportReconciliationReportDto>(true, report));
    }

    /// <summary>ชนิดข้อมูลที่ใช้เป็นด่านของรายงานกระทบยอด — ยอดยกมาลูกหนี้/งบทดลอง (ForImport ⇒ Journal.Manage)</summary>
    public const string OpeningBalanceEntityType = "openingar";
}
