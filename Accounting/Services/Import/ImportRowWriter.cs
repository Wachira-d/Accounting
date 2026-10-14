using Accounting.Services.Interfaces;

namespace Accounting.Services.Import;

/// <summary>
/// ตัวเขียนแถวเดียวของ writer เดิม (รอบ 205 ทีม B · สัญญาที่ <see cref="IImportRowWriter"/>) — ห่อ <see cref="IImportExportService.WriteRowAsync"/>
/// ซึ่งเป็น <b>switch เดียว</b>ของ writer เดิม (เดิมซ้ำสองชุดใน <c>ImportAsync</c> + <c>ConfirmAndImportAsync</c>) · writer เดิมอยู่ที่
/// <c>ImportExportService</c> ต่อไป (ด่านที่ checker ล็อกไว้ผูกกับชื่อเมธอดในไฟล์นั้น) — ที่นี่ไม่มีกติกาของตัวเอง
/// </summary>
public class ImportRowWriter : IImportRowWriter
{
    private readonly IImportExportService _legacy;

    public ImportRowWriter(IImportExportService legacy) => _legacy = legacy;

    public Task<ImportRowCommitResult> WriteRowAsync(Guid companyId, string entityType, ImportTargetRow row, ImportRowWriteOptions options,
        string performedBy, CancellationToken ct = default)
        => _legacy.WriteRowAsync(companyId, entityType, row, options, performedBy, ct);
}
