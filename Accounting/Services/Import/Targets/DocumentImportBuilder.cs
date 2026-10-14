using Accounting.Models.DTOs.Import;

namespace Accounting.Services.Import.Targets;

/// <summary>ประกอบคำขอสร้างเอกสารจากแถวนำเข้า — <b>โครง (T0 รอบ 205)</b> · เจ้าของ: <b>ทีม D</b> (สัญญาที่ <see cref="IDocumentImportBuilder"/>)</summary>
public class DocumentImportBuilder : IDocumentImportBuilder
{
    public Task<ImportDocumentBuild> BuildAsync(Guid companyId, string entityType, IReadOnlyList<ImportTargetRow> groupRows,
        ImportCommitOptions options, CancellationToken ct = default)
        => throw new NotImplementedException("ทีม D (รอบ 205): DocumentImportBuilder.BuildAsync — ยังไม่ต่อสาย (IMPORT_ETL_REVIEW §2.10)");
}
