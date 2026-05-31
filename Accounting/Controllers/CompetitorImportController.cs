using System.Text;
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

    /// <summary>Sniff which competitor format the file belongs to +
    /// return preview rows so admin can review before commit.</summary>
    [HttpPost("preview")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<ApiResponse<object>>> Preview(
        Guid companyId, IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ไฟล์ว่าง"));
        var content = await ReadAsync(file);
        var (adapter, preview) = await _coordinator.PreviewAsync(content, file.FileName, ct);
        if (adapter == null)
            return BadRequest(new ApiResponse<object>(false, null,
                "ไม่สามารถระบุประเภทไฟล์ — รองรับ Express / PEAK / FlowAccount"));
        return Ok(new ApiResponse<object>(true, preview,
            $"พบ {preview!.TotalRows} แถว — ระบบ {adapter.SourceSystem}"));
    }

    /// <summary>Run the actual import. DryRun=true validates without
    /// writing. SkipDuplicates is honoured by each adapter.</summary>
    public sealed record ImportRequest(bool DryRun, bool SkipDuplicates, string? SourceTag);

    [HttpPost("import")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<ApiResponse<ImportResult>>> Import(
        Guid companyId, IFormFile file,
        [FromForm] bool dryRun, [FromForm] bool skipDuplicates,
        [FromForm] string? sourceTag, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new ApiResponse<object>(false, null, "ไฟล์ว่าง"));
        var content = await ReadAsync(file);
        var options = new ImportOptions(dryRun, skipDuplicates, sourceTag);
        var result = await _coordinator.ImportAsync(companyId, content, file.FileName, options, ct);
        if (result == null)
            return BadRequest(new ApiResponse<object>(false, null,
                "ไม่พบ adapter ที่รองรับไฟล์นี้"));
        var verb = dryRun ? "ทดสอบ" : "import";
        return Ok(new ApiResponse<ImportResult>(true, result,
            $"{verb}สำเร็จ {result.RowsImported}/{result.RowsRead} แถว, ข้าม {result.RowsSkipped}, ผิดพลาด {result.RowsFailed}"));
    }

    private static async Task<string> ReadAsync(IFormFile file)
    {
        using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}
