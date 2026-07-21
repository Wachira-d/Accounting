using System.Text;
using System.Text.Json;
using Accounting.Models.DTOs;
using Accounting.Services.Implementations.Migration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/migration")]
[Authorize]
public class CompetitorImportController : ControllerBase
{
    private readonly ICompetitorImportCoordinator _coordinator;

    public CompetitorImportController(ICompetitorImportCoordinator coordinator)
    { _coordinator = coordinator; }

    /// <summary>Sniff which competitor format the file belongs to,
    /// return sample rows AND a list of TaxId-conflict rows so the
    /// admin can pick Skip/Overwrite/Merge per row before commit.</summary>
    [HttpPost("preview")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<ApiResponse<object>>> Preview(
        Guid companyId, IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ไฟล์ว่าง"));
        var content = await ReadAsync(file);
        var (adapter, preview) = await _coordinator.PreviewAsync(companyId, content, file.FileName, ct);
        if (adapter == null)
            return BadRequest(new ApiResponse<object>(false, null,
                "ไม่สามารถระบุประเภทไฟล์ — รองรับ Express / PEAK / FlowAccount"));
        var msg = $"พบ {preview!.TotalRows} แถว — ระบบ {adapter.SourceSystem} · " +
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
        var content = await ReadAsync(file);

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
        var result = await _coordinator.ImportAsync(companyId, content, file.FileName, options, ct);
        if (result == null)
            return BadRequest(new ApiResponse<object>(false, null,
                "ไม่พบ adapter ที่รองรับไฟล์นี้"));
        var verb = dryRun ? "ทดสอบ" : "import";
        return Ok(new ApiResponse<ImportResult>(true, result,
            $"{verb}สำเร็จ — เพิ่มใหม่ {result.RowsImported} · อัพเดต {result.RowsUpdated} · ข้าม {result.RowsSkipped} · ผิดพลาด {result.RowsFailed} (จาก {result.RowsRead} แถว)"));
    }

    private static async Task<string> ReadAsync(IFormFile file)
    {
        using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
