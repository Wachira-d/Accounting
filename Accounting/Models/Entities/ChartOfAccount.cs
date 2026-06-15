using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// ผังบัญชี (Chart of Accounts)
/// </summary>
public class ChartOfAccount : TenantEntity
{
    public string AccountCode { get; set; } = null!;   // e.g. "1100", "4100"
    public string AccountName { get; set; } = null!;    // e.g. "เงินสด"
    public string? AccountNameEn { get; set; }
    public AccountType AccountType { get; set; }
    public Guid? ParentAccountId { get; set; }
    public ChartOfAccount? ParentAccount { get; set; }
    public int Level { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public bool IsSystemAccount { get; set; } = false;  // บัญชีที่ระบบสร้างให้
    public string? Description { get; set; }

    /// <summary>
    /// False = input VAT (ภาษีซื้อ) posted to this account is PROHIBITED
    /// (ภาษีซื้อต้องห้าม) per Revenue Code §82/5 and must NOT be claimed on
    /// the ภ.พ.30 — e.g. ค่ารับรอง/เลี้ยงรับรอง (client entertainment),
    /// passenger-car purchases. Default true. Seeded false for the
    /// entertainment account; admins can toggle others.
    /// </summary>
    public bool InputVatClaimable { get; set; } = true;

    /// <summary>หมวด Cash Flow Statement — null/None = engine fallback
    /// ใช้ code-prefix heuristics เดิม (operating / investing / financing
    /// resolved by AccountCode pattern). ตั้งค่าได้สำหรับผังบัญชี custom
    /// ที่ไม่ตามรหัสเริ่มต้น (เช่นบัญชี 5xxxx ที่ admin map เป็น
    /// investing — เครื่องจักรซ่อม).</summary>
    public CashFlowSectionType CashFlowSection { get; set; } = CashFlowSectionType.None;

    /// <summary>
    /// Cost behavior classification used by the Fix-vs-Variable cost
    /// report. "Fixed" = incurred regardless of activity (rent, salaried
    /// staff, depreciation, insurance). "Variable" = scales with activity
    /// (hourly labor, materials, commissions). "Mixed" = semi-variable
    /// (utilities with a fixed minimum). Defaults Null for accounts that
    /// don't affect costing (cash, AR, AP, equity).
    /// </summary>
    public string? CostBehavior { get; set; }

    // Navigation
    public ICollection<ChartOfAccount> ChildAccounts { get; set; } = new List<ChartOfAccount>();
    public ICollection<JournalEntryLine> JournalEntryLines { get; set; } = new List<JournalEntryLine>();
}
