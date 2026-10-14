namespace Accounting.Services.Import;

/// <summary>
/// จับคู่แถวนำเข้ากับข้อมูลเดิม — <b>โครง (T0 รอบ 205)</b> · สัญญาที่ <see cref="IImportEntityResolver"/>
/// <para>ไฟล์นี้ (<see cref="MatchAsync"/>) = <b>ทีม B</b> · <c>ImportEntityResolver.Accounts.cs</c> (<c>ResolveAccountsAsync</c>) = <b>ทีม D</b> —
/// partial class เพื่อให้สองทีมแก้คนละไฟล์ · constructor + dependency อยู่ที่ไฟล์นี้ (ทีม D ต้องการ dependency เพิ่ม ⇒ ประสานทีม B)</para>
/// </summary>
public partial class ImportEntityResolver : IImportEntityResolver
{
    /// <summary>ทีม D (รอบ 205): <paramref name="accountMatcher"/> = ตัวหาบัญชีของ <c>ResolveAccountsAsync</c> (ImportEntityResolver.Accounts.cs) ·
    /// ทีม B เพิ่ม dependency ของ <c>MatchAsync</c> ใน constructor ตัวเดียวกันนี้ (ห้ามแยกเป็นสอง constructor — DI เลือกตัวยาวสุดแล้วอีกฝั่งได้ null)</summary>
    public ImportEntityResolver(ImportAccountMatcher accountMatcher)
    {
        _accountMatcher = accountMatcher;
    }

    public Task<IReadOnlyList<ImportEntityMatchResult>> MatchAsync(Guid companyId, string entityType, IReadOnlyList<ImportTargetRow> rows,
        ImportResolveOptions options, CancellationToken ct = default)
        => throw new NotImplementedException("ทีม B (รอบ 205): ImportEntityResolver.MatchAsync — ยังไม่ต่อสาย (IMPORT_ETL_REVIEW §2.4)");
}
