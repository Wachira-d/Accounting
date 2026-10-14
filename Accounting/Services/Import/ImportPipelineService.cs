using Accounting.Data;
using Accounting.Models.DTOs.Import;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Import;

/// <summary>
/// pipeline ของศูนย์นำเข้าข้อมูล — <b>โครง (T0 รอบ 205)</b> · เจ้าของ: <b>ทีม B</b> (ดูสัญญาที่ <see cref="IImportPipelineService"/>)
///
/// <para>ยังไม่มี endpoint เรียก (ทีม E ต่อ <c>ImportHubController</c>) ⇒ เมธอดที่ยังไม่ทำโยน <see cref="NotImplementedException"/> ระบุทีม ·
/// ชิ้นที่พร้อมใช้แล้ว: <see cref="GetSourceFileAsync"/> · ตัวช่วยของขั้น Extract/Transform อยู่ที่ <see cref="ImportSourceInspector"/> (อ่านไฟล์ ·
/// ชีต · encoding) และ <see cref="ImportCellNormalizer"/> (แปลงค่าตามชนิด field → เซลล์ + issue)</para>
/// </summary>
public class ImportPipelineService : IImportPipelineService
{
    private readonly AccountingDbContext _db;
    private readonly IFileAttachmentService _files;

    public ImportPipelineService(AccountingDbContext db, IFileAttachmentService files)
    {
        _db = db;
        _files = files;
    }

    private static NotImplementedException Todo(string what)
        => new($"ทีม B (รอบ 205): {what} — ยังไม่ต่อสาย (IMPORT_ETL_REVIEW §2.4)");

    public Task<ImportBatchDetailDto> CreateAsync(Guid companyId, ImportUploadedFile file, ImportCreateBatchRequest request, ImportActor actor,
        CancellationToken ct = default) => throw Todo(nameof(CreateAsync));

    public Task<ImportBatchDetailDto> AnalyzeAsync(Guid companyId, Guid batchId, ImportAnalyzeRequest request, ImportActor actor,
        CancellationToken ct = default) => throw Todo(nameof(AnalyzeAsync));

    public Task<ImportBatchListDto> ListAsync(Guid companyId, ImportBatchListQuery query, CancellationToken ct = default)
        => throw Todo(nameof(ListAsync));

    public Task<ImportBatchDetailDto?> GetAsync(Guid companyId, Guid batchId, CancellationToken ct = default)
        => throw Todo(nameof(GetAsync));

    public Task<ImportRowPageDto> GetRowsAsync(Guid companyId, Guid batchId, ImportRowQuery query, CancellationToken ct = default)
        => throw Todo(nameof(GetRowsAsync));

    public Task<ImportBatchDetailDto> RemapAsync(Guid companyId, Guid batchId, ImportRemapRequest request, ImportActor actor,
        CancellationToken ct = default) => throw Todo(nameof(RemapAsync));

    public Task<ImportRowDto> EditRowAsync(Guid companyId, Guid batchId, Guid rowId, ImportEditRowRequest request, ImportActor actor,
        CancellationToken ct = default) => throw Todo(nameof(EditRowAsync));

    public Task<ImportBulkResultDto> BulkActionAsync(Guid companyId, Guid batchId, ImportBulkActionRequest request, ImportActor actor,
        CancellationToken ct = default) => throw Todo(nameof(BulkActionAsync));

    public Task<ImportCommitResultDto> CommitAsync(Guid companyId, Guid batchId, ImportCommitOptions options, ImportActor actor,
        CancellationToken ct = default) => throw Todo(nameof(CommitAsync));

    public Task<ImportUndoPreviewDto> PreviewUndoAsync(Guid companyId, Guid batchId, CancellationToken ct = default)
        => throw Todo(nameof(PreviewUndoAsync));

    public Task<ImportUndoResultDto> UndoAsync(Guid companyId, Guid batchId, string? reason, ImportActor actor, CancellationToken ct = default)
        => throw Todo(nameof(UndoAsync));

    public Task<ImportBatchSummaryDto> CancelAsync(Guid companyId, Guid batchId, ImportActor actor, CancellationToken ct = default)
        => throw Todo(nameof(CancelAsync));

    public Task<byte[]> ExportErrorRowsCsvAsync(Guid companyId, Guid batchId, CancellationToken ct = default)
        => throw Todo(nameof(ExportErrorRowsCsvAsync));

    public async Task<ImportSourceFileDto?> GetSourceFileAsync(Guid companyId, Guid batchId, CancellationToken ct = default)
    {
        // tenant isolation: CompanyId ทั้งตอนหา batch และตอนอ่านไฟล์ (ReadBytesAsync กรองซ้ำอีกชั้น)
        var batch = await _db.SmartImportSessions.AsNoTracking()
            .Where(s => s.Id == batchId && s.CompanyId == companyId)
            .Select(s => new { s.FileName, s.SourceFileAttachmentId })
            .FirstOrDefaultAsync(ct);
        if (batch?.SourceFileAttachmentId is not { } fileId) return null;
        var meta = await _files.GetByIdAsync(companyId, fileId);
        var bytes = await _files.ReadBytesAsync(companyId, fileId, ct);
        if (meta == null || bytes == null) return null;
        return new ImportSourceFileDto(string.IsNullOrWhiteSpace(meta.OriginalFileName) ? batch.FileName : meta.OriginalFileName,
            string.IsNullOrWhiteSpace(meta.ContentType) ? "application/octet-stream" : meta.ContentType, bytes);
    }

    public Task<ImportEntityCatalogueDto> GetEntityCatalogueAsync(Guid companyId, Guid userId, CancellationToken ct = default)
        => throw Todo(nameof(GetEntityCatalogueAsync));

    public Task<bool> ProcessQueuedAsync(Guid companyId, Guid batchId, string workerId, CancellationToken ct = default)
        => throw Todo(nameof(ProcessQueuedAsync));
}
