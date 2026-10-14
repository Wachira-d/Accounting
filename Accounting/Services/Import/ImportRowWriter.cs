namespace Accounting.Services.Import;

/// <summary>ตัวเขียนแถวเดียวของ writer เดิม — <b>โครง (T0 รอบ 205)</b> · เจ้าของ: <b>ทีม B</b> (สัญญาที่ <see cref="IImportRowWriter"/>)</summary>
public class ImportRowWriter : IImportRowWriter
{
    public Task<ImportRowCommitResult> WriteRowAsync(Guid companyId, string entityType, ImportTargetRow row, ImportRowWriteOptions options,
        string performedBy, CancellationToken ct = default)
        => throw new NotImplementedException("ทีม B (รอบ 205): ImportRowWriter.WriteRowAsync — ยังไม่ต่อสาย (IMPORT_ETL_REVIEW §2.4)");
}
