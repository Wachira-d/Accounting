using Accounting.Models.DTOs.Import;

namespace Accounting.Services.Import;

/// <summary>กระทบยอดหลังย้ายระบบ — <b>โครง (T0 รอบ 205)</b> · เจ้าของ: <b>ทีม D</b> (สัญญาที่ <see cref="IOpeningBalanceReconciliation"/>)</summary>
public class OpeningBalanceReconciliation : IOpeningBalanceReconciliation
{
    public Task<ImportReconciliationReportDto> ReconcileAsync(Guid companyId, DateTime asOf, CancellationToken ct = default)
        => throw new NotImplementedException("ทีม D (รอบ 205): OpeningBalanceReconciliation.ReconcileAsync — ยังไม่ต่อสาย (IMPORT_ETL_REVIEW §2.10)");
}
