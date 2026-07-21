using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Bank;

/// <summary>
/// Resolves SYMBOLIC default-account keys (e.g. "BankCharges", "WhtReceivable",
/// "FxGain", "FxLoss", "MiscAdjustment", "BillCharges") to the company's
/// ACTUAL ChartOfAccount row — code + name. BankFeeDictionary previously
/// emitted hard-coded codes ("5503 - Bank Charges") that may or may not exist
/// in the user's COA; this resolver looks at the real chart and returns
/// either the matched row or null. When no match is found, the caller falls
/// back to the generic label so the user still sees what the fee IS, just
/// not which account to use.
///
/// Resolution strategy (in order):
///   1. AccountCode exact match (admin pre-seeded a known code).
///   2. AccountCode starts with a category prefix (5* for expense, 4* for
///      revenue, 1* for asset, 2* for liability) AND name contains the
///      Thai/English keyword for the key.
///   3. AccountName contains keyword + AccountType matches expected role.
///
/// Cached per company in memory for the request lifetime.
/// </summary>
public sealed class CompanyChartOfAccountsResolver
{
    public sealed record AccountRef(Guid Id, string Code, string Name, AccountType Type);

    private readonly IReadOnlyList<AccountRef> _accounts;

    private CompanyChartOfAccountsResolver(IReadOnlyList<AccountRef> accounts)
    {
        _accounts = accounts;
    }

    public IReadOnlyList<AccountRef> Active => _accounts;

    public static async Task<CompanyChartOfAccountsResolver> LoadAsync(
        AccountingDbContext db, Guid companyId, CancellationToken ct)
    {
        var rows = await db.ChartOfAccounts.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive)
            .OrderBy(a => a.AccountCode)
            .Select(a => new AccountRef(a.Id, a.AccountCode, a.AccountName, a.AccountType))
            .ToListAsync(ct);
        return new CompanyChartOfAccountsResolver(rows);
    }

    /// <summary>Symbolic keys → keyword lists + expected account type. Order
    /// of keywords reflects specificity (more specific first wins ties).</summary>
    private static readonly Dictionary<string, (string[] Keywords, AccountType Type, char? CodePrefix)> _keys = new()
    {
        ["BankCharges"]    = (new[] { "ค่าธรรมเนียมธนาคาร", "bank charges", "bank charge", "bank fee", "ค่าธรรมเนียม" }, AccountType.Expense, '5'),
        ["BillCharges"]    = (new[] { "ค่าธรรมเนียมชำระบิล", "bill payment fee", "ค่าธรรมเนียมบิล" }, AccountType.Expense, '5'),
        ["WhtReceivable"]  = (new[] { "ภาษีหัก ณ ที่จ่าย รอเรียกคืน", "withholding tax receivable", "wht receivable", "ภาษีถูกหัก ณ ที่จ่าย", "ภาษีหัก ณ ที่จ่าย" }, AccountType.Asset, '1'),
        ["WhtPayable"]     = (new[] { "ภาษีหัก ณ ที่จ่ายค้างจ่าย", "withholding tax payable", "wht payable" }, AccountType.Liability, '2'),
        ["FxGain"]         = (new[] { "กำไรอัตราแลกเปลี่ยน", "fx gain", "foreign exchange gain", "exchange gain" }, AccountType.Revenue, '4'),
        ["FxLoss"]         = (new[] { "ขาดทุนอัตราแลกเปลี่ยน", "fx loss", "foreign exchange loss", "exchange loss" }, AccountType.Expense, '5'),
        ["MiscAdjustment"] = (new[] { "ปัดเศษ", "rounding", "misc adjustment", "ปรับปรุงเศษ", "miscellaneous" }, AccountType.Expense, null),
        ["InterestIncome"] = (new[] { "ดอกเบี้ยรับ", "interest income", "interest earned" }, AccountType.Revenue, '4'),
        ["InterestExpense"] = (new[] { "ดอกเบี้ยจ่าย", "interest expense", "ดอกเบี้ยเงินกู้" }, AccountType.Expense, '5'),
    };

    /// <summary>Find the COA row best matching the symbolic key for this
    /// company. Returns null when no reasonable match is found.</summary>
    public AccountRef? Resolve(string symbolicKey)
    {
        if (!_keys.TryGetValue(symbolicKey, out var spec)) return null;
        var (keywords, expectedType, prefix) = spec;

        // Pass 1: name contains keyword + type matches + (optional) prefix matches
        foreach (var kw in keywords)
        {
            var hit = _accounts.FirstOrDefault(a =>
                a.Type == expectedType
                && a.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)
                && (prefix == null || a.Code.StartsWith(prefix.Value)));
            if (hit != null) return hit;
        }
        // Pass 2: drop the prefix constraint
        foreach (var kw in keywords)
        {
            var hit = _accounts.FirstOrDefault(a =>
                a.Type == expectedType
                && a.Name.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        // Pass 3: drop the type constraint (name only)
        foreach (var kw in keywords)
        {
            var hit = _accounts.FirstOrDefault(a =>
                a.Name.Contains(kw, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>One-line hint string suitable for a UI reasoning field, or
    /// the original fallback label when nothing in COA matches.</summary>
    public string FormatHint(string symbolicKey, string fallbackLabel)
    {
        var hit = Resolve(symbolicKey);
        return hit == null ? fallbackLabel : $"{hit.Code} - {hit.Name}";
    }
}
