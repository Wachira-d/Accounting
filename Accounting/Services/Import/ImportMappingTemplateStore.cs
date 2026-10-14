using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;

namespace Accounting.Services.Import;

/// <summary>คลังแม่แบบจับคู่คอลัมน์ — <b>โครง (T0 รอบ 205)</b> · เจ้าของ: <b>ทีม A</b> (สัญญาที่ <see cref="IImportMappingTemplateStore"/>)</summary>
public class ImportMappingTemplateStore : IImportMappingTemplateStore
{
    private static NotImplementedException Todo(string what)
        => new($"ทีม A (รอบ 205): ImportMappingTemplateStore.{what} — ยังไม่ต่อสาย (IMPORT_ETL_REVIEW §2.2)");

    public Task<ImportMappingTemplate?> FindAsync(Guid companyId, string entityType, string headerSignature, CancellationToken ct = default)
        => throw Todo(nameof(FindAsync));

    public Task<IReadOnlyList<ImportTemplateDto>> ListAsync(Guid companyId, string? entityType, CancellationToken ct = default)
        => throw Todo(nameof(ListAsync));

    public Task<ImportTemplateDto?> GetAsync(Guid companyId, Guid templateId, CancellationToken ct = default)
        => throw Todo(nameof(GetAsync));

    public Task<ImportTemplateDto> SaveFromBatchAsync(Guid companyId, Guid batchId, string? name, bool confirmedByUser, ImportActor actor,
        CancellationToken ct = default) => throw Todo(nameof(SaveFromBatchAsync));

    public Task<ImportTemplateDto?> UpdateAsync(Guid companyId, Guid templateId, ImportTemplateUpsertRequest request, ImportActor actor,
        CancellationToken ct = default) => throw Todo(nameof(UpdateAsync));

    public Task<bool> DeleteAsync(Guid companyId, Guid templateId, ImportActor actor, CancellationToken ct = default)
        => throw Todo(nameof(DeleteAsync));

    public Task MarkUsedAsync(Guid companyId, Guid templateId, CancellationToken ct = default)
        => throw Todo(nameof(MarkUsedAsync));
}
