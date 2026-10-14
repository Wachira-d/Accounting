namespace Accounting.Services.Import;

/// <summary>หาบัญชีในผังบริษัทให้แถวนำเข้า — <b>โครง (T0 รอบ 205)</b> · เจ้าของ: <b>ทีม D</b> (สัญญาที่ <see cref="IImportEntityResolver.ResolveAccountsAsync"/>)</summary>
public partial class ImportEntityResolver
{
    public Task<IReadOnlyList<ImportAccountMatchResult>> ResolveAccountsAsync(Guid companyId, IReadOnlyList<ImportAccountQuery> queries,
        ImportResolveOptions options, CancellationToken ct = default)
        => throw new NotImplementedException("ทีม D (รอบ 205): ImportEntityResolver.ResolveAccountsAsync — ยังไม่ต่อสาย (IMPORT_ETL_REVIEW §2.6)");
}
