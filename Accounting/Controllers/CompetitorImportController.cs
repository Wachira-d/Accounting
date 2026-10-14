using System.Text.Json;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations.Migration;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/migration")]
[Authorize]
public class CompetitorImportController : ControllerBase
{
    private readonly ICompetitorImportCoordinator _coordinator;
    private readonly IPermissionService _permissions;

    public CompetitorImportController(ICompetitorImportCoordinator coordinator, IPermissionService permissions)
    { _coordinator = coordinator; _permissions = permissions; }

    /// <summary>
    /// ด่านสิทธิ์ของการย้ายข้อมูลจากโปรแกรมอื่น — คีย์ตามชนิดข้อมูลในไฟล์ (<c>EntityKind</c> ของ adapter:
    /// ผังบัญชี = ChartOfAccounts.Edit · สินค้า = Product.Edit · ผู้ติดต่อ = Contact.Edit) จากตารางเดียวกับเส้นนำเข้าไฟล์
    /// (<see cref="ImportExportPermissionScope.ForImport"/>)
    ///
    /// <para>═══ ที่มา (รอบ 193 S2 — กวาดทางเข้า <c>IFormFile</c>) ═══ controller นี้มีแค่ <c>[Authorize]</c> ⇒ สมาชิกคนไหนก็
    /// นำเข้า/เขียนทับผังบัญชีทั้งผังได้ ขณะที่ข้อมูลชุดเดียวกันผ่าน <c>ImportExportController</c> ต้องมีคีย์ (ทางเข้าอื่นไม่เดิน
    /// ด่านเดียวกัน — R5) · preview ก็ต้องมีด่าน เพราะคืน "แถวเดิมที่ขัดแย้ง" จากฐาน (ชุดเดียวกับ preview-conflicts)</para>
    /// </summary>
    private async Task<ActionResult?> DenyAsync(Guid companyId, string entityKind)
    {
        var key = ImportExportPermissionScope.ForImport(entityKind);
        var userId = JwtHelper.GetUserIdFromClaims(User);
        if (await _permissions.HasPermissionAsync(companyId, userId, key)) return null;
        return StatusCode(403, new ApiResponse<object>(false, null,
            ImportExportPermissionScope.DeniedMessage(entityKind, key, isExport: false)));
    }

    /// <summary>Sniff which competitor format the file belongs to (คะแนนหัวตาราง — ไม่ใช่ชื่อไฟล์),
    /// return sample rows AND a list of key-conflict rows so the admin can pick Skip/Overwrite/Merge per row before commit ·
    /// คำตอบมี <c>HubEntityType</c> + ป้ายไทยให้หน้าเว็บลิงก์ไปศูนย์นำเข้าข้อมูล</summary>
    [HttpPost("preview")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<ApiResponse<object>>> Preview(
        Guid companyId, IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ไฟล์ว่าง"));
        var table = await ReadAsync(file);
        var detected = _coordinator.Detect(table);
        if (detected.Adapter == null)
            return BadRequest(new ApiResponse<object>(false, null, detected.Reason));
        var deny = await DenyAsync(companyId, detected.Adapter.EntityKind);
        if (deny != null) return deny;
        var (adapter, preview) = await _coordinator.PreviewAsync(companyId, table, ct);
        if (adapter == null)
            return BadRequest(new ApiResponse<object>(false, null, detected.Reason));
        var msg = $"พบ {preview!.TotalRows} แถว — {preview.EntityLabel}ของ {adapter.SourceSystem} · " +
                  $"ใหม่ {preview.NewRowCount} · ซ้ำเหมือนกัน {preview.DuplicateExactCount} · " +
                  $"ขัดแย้ง (ต้องเลือก) {preview.Conflicts.Count}";
        return Ok(new ApiResponse<object>(true, preview, msg));
    }

    /// <summary>Run the actual import. DryRun=true validates without
    /// writing. Resolutions[taxId] = "Skip"|"Overwrite"|"Merge" gives a
    /// per-row decision for rows the preview flagged as conflicts.
    /// Rows without an explicit decision fall back to defaultConflictAction.</summary>
    [HttpPost("import")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<ApiResponse<ImportResult>>> Import(
        Guid companyId, IFormFile file,
        [FromForm] bool dryRun, [FromForm] bool skipDuplicates,
        [FromForm] string? sourceTag,
        [FromForm] string? resolutionsJson,
        [FromForm] string? defaultConflictAction,
        CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ไฟล์ว่าง"));
        var table = await ReadAsync(file);
        var detected = _coordinator.Detect(table);
        if (detected.Adapter == null)
            return BadRequest(new ApiResponse<object>(false, null, detected.Reason));
        // ทดสอบ (dryRun) ก็ต้องเดินด่านเดียวกัน — ไม่งั้นผู้ใช้เห็น "ผ่าน" แล้วไปเจอ 403 ตอนนำเข้าจริง
        var deny = await DenyAsync(companyId, detected.Adapter.EntityKind);
        if (deny != null) return deny;

        Dictionary<string, ConflictAction>? resolutions = null;
        if (!string.IsNullOrWhiteSpace(resolutionsJson))
        {
            try
            {
                var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(resolutionsJson);
                if (raw != null)
                {
                    resolutions = new Dictionary<string, ConflictAction>(raw.Count);
                    foreach (var kv in raw)
                        if (Enum.TryParse<ConflictAction>(kv.Value, ignoreCase: true, out var act))
                            resolutions[kv.Key] = act;
                }
            }
            catch (JsonException)
            {
                return BadRequest(new ApiResponse<object>(false, null,
                    "resolutionsJson ผิดรูปแบบ — ต้องเป็น JSON object {taxId: \"Skip|Overwrite|Merge\"}"));
            }
        }

        var defaultAction = ConflictAction.Skip;
        if (!string.IsNullOrWhiteSpace(defaultConflictAction)
            && Enum.TryParse<ConflictAction>(defaultConflictAction, ignoreCase: true, out var parsed))
            defaultAction = parsed;

        var options = new ImportOptions(dryRun, skipDuplicates, sourceTag, resolutions, defaultAction);
        var result = await _coordinator.ImportAsync(companyId, table, options, ct);
        if (result == null)
            return BadRequest(new ApiResponse<object>(false, null, detected.Reason));
        var verb = dryRun ? "ทดสอบ" : "import";
        return Ok(new ApiResponse<ImportResult>(true, result,
            $"{verb}สำเร็จ — เพิ่มใหม่ {result.RowsImported} · อัพเดต {result.RowsUpdated} · ข้าม {result.RowsSkipped} · ผิดพลาด {result.RowsFailed} (จาก {result.RowsRead} แถว)"));
    }

    /// <summary>รอบ 205 ทีม D — อ่านด้วยตัวอ่านกลาง <see cref="ImportTabularReader"/> (UTF-8/UTF-16/TIS-620 · xlsx) · เดิม StreamReader(UTF8)
    /// ⇒ ไฟล์ Express ที่เป็น TIS-620 กลายเป็นอักขระเสีย · ไฟล์ที่ไม่ใช่ตาราง ⇒ <see cref="ImportFormatException"/> (400 ข้อความไทยพร้อมทางไปต่อ)</summary>
    private static async Task<CompetitorTable> ReadAsync(IFormFile file)
    {
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        return CompetitorTable.Open(file.FileName, ms.ToArray());
    }
}
