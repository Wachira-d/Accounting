using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ===================================================================
// 1. ค่าใช้จ่ายจ่ายล่วงหน้า (Prepaid Expense Amortization)
//    ตอนจ่าย: Dr 117xx (Prepaid) / Cr 111xx (Cash)
//    ตัดจ่าย: Dr 54xxx (Expense) / Cr 117xx (Prepaid)
// ===================================================================
public class PrepaidExpense : TenantEntity
{
    public string ReferenceNo { get; set; } = null!;
    public string Description { get; set; } = null!;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int TotalPeriods { get; set; }           // จำนวนงวดที่จะตัดจ่าย
    public decimal TotalAmount { get; set; }
    public decimal AmortizedAmount { get; set; }    // ยอดที่ตัดไปแล้ว
    public decimal RemainingAmount { get; set; }    // ยอดคงเหลือ
    public string AmortizationMethod { get; set; } = "StraightLine"; // StraightLine
    public string Status { get; set; } = "Active";  // Active, FullyAmortized, Cancelled

    // Account mapping
    public Guid PrepaidAccountId { get; set; }      // 117xx
    public ChartOfAccount PrepaidAccount { get; set; } = null!;
    public Guid ExpenseAccountId { get; set; }      // 54xxx
    public ChartOfAccount ExpenseAccount { get; set; } = null!;

    public ICollection<PrepaidAmortizationSchedule> Schedules { get; set; } = new List<PrepaidAmortizationSchedule>();
}

public class PrepaidAmortizationSchedule : TenantEntity
{
    public Guid PrepaidExpenseId { get; set; }
    public PrepaidExpense PrepaidExpense { get; set; } = null!;
    public int PeriodNumber { get; set; }
    public DateTime ScheduledDate { get; set; }
    public decimal Amount { get; set; }
    public bool IsProcessed { get; set; }
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
}

// ===================================================================
// 2. เงินมัดจำ (Deposit Management)
//    มัดจำจ่าย: Dr 118xx/125xx / Cr 111xx
//    มัดจำรับ: Dr 111xx / Cr 216xx
//    คืนมัดจำ: กลับรายการ
// ===================================================================
public class DepositTransaction : TenantEntity
{
    public string ReferenceNo { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string Direction { get; set; } = null!;   // "Paid" (จ่าย) หรือ "Received" (รับ)
    public string DepositType { get; set; } = null!;  // Rental, Guarantee, Advance, Other
    public decimal Amount { get; set; }
    public decimal RefundedAmount { get; set; }
    public decimal RemainingAmount { get; set; }
    public string Status { get; set; } = "Active";   // Active, PartiallyRefunded, FullyRefunded, Forfeited
    public DateTime TransactionDate { get; set; }
    public DateTime? ExpectedReturnDate { get; set; }
    public Guid? ContactId { get; set; }
    public string? ContactName { get; set; }

    // Account mapping
    public Guid DepositAccountId { get; set; }       // 118xx/125xx (จ่าย) or 216xx (รับ)
    public ChartOfAccount DepositAccount { get; set; } = null!;
    public Guid? CashAccountId { get; set; }         // 111xx
    public ChartOfAccount? CashAccount { get; set; }

    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }

    public ICollection<DepositRefund> Refunds { get; set; } = new List<DepositRefund>();
}

public class DepositRefund : TenantEntity
{
    public Guid DepositTransactionId { get; set; }
    public DepositTransaction DepositTransaction { get; set; } = null!;
    public DateTime RefundDate { get; set; }
    public decimal Amount { get; set; }
    public string? Notes { get; set; }
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
}

// ===================================================================
// 3. ค่าเผื่อหนี้สงสัยจะสูญ (Bad Debt Allowance)
//    ตั้งค่าเผื่อ: Dr 57130 (หนี้สงสัยจะสูญ) / Cr 18100 (ค่าเผื่อ)
//    ตัดหนี้สูญ: Dr 18100 / Cr 11310 (ลูกหนี้)
// ===================================================================
public class BadDebtAllowance : TenantEntity
{
    public string ReferenceNo { get; set; } = null!;
    public DateTime AllowanceDate { get; set; }
    public string Method { get; set; } = "Percentage"; // Percentage, Aging, Specific
    public decimal TotalReceivable { get; set; }
    public decimal AllowanceAmount { get; set; }
    public decimal PreviousAllowance { get; set; }
    public decimal AdjustmentAmount { get; set; }     // Increase/decrease in allowance
    public string Status { get; set; } = "Draft";     // Draft, Posted
    public string? Notes { get; set; }
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public ICollection<BadDebtAllowanceLine> Lines { get; set; } = new List<BadDebtAllowanceLine>();
}

public class BadDebtAllowanceLine : TenantEntity
{
    public Guid BadDebtAllowanceId { get; set; }
    public BadDebtAllowance BadDebtAllowance { get; set; } = null!;
    public Guid? ContactId { get; set; }
    public string? ContactName { get; set; }
    public string AgingBucket { get; set; } = null!;   // Current, 1-30, 31-60, 61-90, 90+
    public decimal OutstandingAmount { get; set; }
    public decimal AllowancePercentage { get; set; }
    public decimal AllowanceAmount { get; set; }
}

// ===================================================================
// 4. ค่าเผื่อสินค้าล้าสมัย (Inventory Obsolescence Allowance)
//    ตั้งค่าเผื่อ: Dr 51xxx (ต้นทุน) / Cr 18200 (ค่าเผื่อ)
// ===================================================================
public class InventoryObsolescenceAllowance : TenantEntity
{
    public string ReferenceNo { get; set; } = null!;
    public DateTime AllowanceDate { get; set; }
    public string Method { get; set; } = "Aging";      // Aging, Percentage, Specific
    public decimal TotalInventoryValue { get; set; }
    public decimal AllowanceAmount { get; set; }
    public decimal PreviousAllowance { get; set; }
    public decimal AdjustmentAmount { get; set; }
    public string Status { get; set; } = "Draft";
    public string? Notes { get; set; }
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
    public ICollection<InventoryObsolescenceLine> Lines { get; set; } = new List<InventoryObsolescenceLine>();
}

public class InventoryObsolescenceLine : TenantEntity
{
    public Guid AllowanceId { get; set; }
    public InventoryObsolescenceAllowance Allowance { get; set; } = null!;
    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;
    public string AgingBucket { get; set; } = null!;
    public decimal CurrentStock { get; set; }
    public decimal StockValue { get; set; }
    public decimal AllowancePercentage { get; set; }
    public decimal AllowanceAmount { get; set; }
}

// ===================================================================
// 5. ค่าใช้จ่ายค้างจ่าย (Accrued Expense Management)
//    ตั้งค้าง: Dr 54xxx (Expense) / Cr 215xx (Accrued)
//    จ่ายจริง: Dr 215xx / Cr 111xx (Cash)
// ===================================================================
public class AccruedExpense : TenantEntity
{
    public string ReferenceNo { get; set; } = null!;
    public string Description { get; set; } = null!;
    public string ExpenseType { get; set; } = null!;   // Utilities, Salary, Interest, Other
    public DateTime AccrualDate { get; set; }
    public decimal Amount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal RemainingAmount { get; set; }
    public string Status { get; set; } = "Accrued";    // Accrued, PartiallyPaid, Paid, Reversed
    public bool IsRecurring { get; set; }
    public string? RecurringFrequency { get; set; }    // Monthly, Quarterly

    // Account mapping
    public Guid ExpenseAccountId { get; set; }         // 54xxx
    public ChartOfAccount ExpenseAccount { get; set; } = null!;
    public Guid AccruedAccountId { get; set; }         // 215xx
    public ChartOfAccount AccruedAccount { get; set; } = null!;

    public Guid? AccrualJournalId { get; set; }
    public JournalEntry? AccrualJournal { get; set; }
    public Guid? PaymentJournalId { get; set; }
    public JournalEntry? PaymentJournal { get; set; }
}

// ===================================================================
// 6. ภาษีเงินได้นิติบุคคล (Corporate Income Tax)
//    คำนวณ: Dr 58000 (CIT Expense) / Cr 21920 (CIT Payable)
// ===================================================================
public class CorporateIncomeTax : TenantEntity
{
    public string TaxYear { get; set; } = null!;       // e.g. "2567"
    public string TaxPeriod { get; set; } = null!;     // "HalfYear" (ภ.ง.ด.51), "Annual" (ภ.ง.ด.50)
    public decimal TotalRevenue { get; set; }
    public decimal TotalExpenses { get; set; }
    public decimal AccountingProfit { get; set; }      // กำไรทางบัญชี
    public decimal AddBackItems { get; set; }          // รายจ่ายบวกกลับ
    public decimal DeductionItems { get; set; }        // รายจ่ายหักเพิ่ม
    public decimal TaxableProfit { get; set; }         // กำไรสุทธิทางภาษี
    public decimal TaxRate { get; set; } = 20;         // อัตราภาษี %
    public decimal TaxAmount { get; set; }             // ภาษีที่ต้องชำระ
    public decimal WithholdingTaxCredit { get; set; }  // ภาษีหัก ณ ที่จ่ายที่ถูกหักไว้
    public decimal PrepaidTaxCredit { get; set; }      // ภาษีจ่ายล่วงหน้า (ภ.ง.ด.51)
    public decimal NetTaxPayable { get; set; }         // ภาษีที่ต้องจ่ายสุทธิ
    public string Status { get; set; } = "Draft";     // Draft, Calculated, Filed
    public string? Notes { get; set; }
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
}

// ===================================================================
// 7. จัดสรรกำไร / เงินปันผล (Profit Appropriation & Dividend)
//    สำรองตามกฎหมาย: Dr 32020 (กำไรสะสม) / Cr 32010 (สำรองฯ)
//    จ่ายเงินปันผล: Dr 32020 / Cr 218xx or 111xx
// ===================================================================
public class ProfitAppropriation : TenantEntity
{
    public string ReferenceNo { get; set; } = null!;
    public DateTime ApprovalDate { get; set; }        // วันที่ประชุมอนุมัติ
    public string FiscalYear { get; set; } = null!;    // ปีบัญชี
    public decimal NetProfit { get; set; }             // กำไรสุทธิ
    public decimal LegalReserve { get; set; }          // สำรองตามกฎหมาย 5%
    public decimal DividendAmount { get; set; }        // เงินปันผล
    public decimal RetainedAmount { get; set; }        // กำไรสะสมคงเหลือ
    public decimal DividendPerShare { get; set; }
    public string Status { get; set; } = "Draft";     // Draft, Approved, Distributed
    public string? Notes { get; set; }
    public Guid? ReserveJournalId { get; set; }
    public JournalEntry? ReserveJournal { get; set; }
    public Guid? DividendJournalId { get; set; }
    public JournalEntry? DividendJournal { get; set; }
}

// ===================================================================
// 8. เพิ่มทุน/ลดทุน (Capital Management)
//    เพิ่มทุน: Dr 111xx (Cash) / Cr 31020 (ทุนชำระแล้ว) + 31030 (ส่วนเกิน)
//    ลดทุน: Dr 31020 / Cr 111xx
// ===================================================================
public class CapitalTransaction : TenantEntity
{
    public string ReferenceNo { get; set; } = null!;
    public DateTime TransactionDate { get; set; }
    public string TransactionType { get; set; } = null!; // Increase, Decrease, Initial
    public decimal ShareQuantity { get; set; }
    public decimal ParValue { get; set; }              // มูลค่าหุ้นที่ตราไว้
    public decimal PaidAmount { get; set; }            // เงินที่ได้รับ/จ่ายคืน
    public decimal SharePremium { get; set; }          // ส่วนเกิน/ต่ำกว่ามูลค่าหุ้น
    public string Status { get; set; } = "Draft";     // Draft, Registered, Completed
    public string? BoardResolutionRef { get; set; }    // เลขที่มติที่ประชุม
    public string? DbrRegistrationRef { get; set; }    // เลขที่จดทะเบียน กรมพัฒนาธุรกิจ
    public string? Notes { get; set; }
    public Guid? JournalEntryId { get; set; }
    public JournalEntry? JournalEntry { get; set; }
}

// ===================================================================
// 9. เงินลงทุนชั่วคราว (Short-term Investment)
//    ซื้อ: Dr 112xx / Cr 111xx
//    ขาย: Dr 111xx / Cr 112xx + กำไร(43xxx)/ขาดทุน(57xxx)
// ===================================================================
public class ShortTermInvestment : TenantEntity
{
    public string ReferenceNo { get; set; } = null!;
    public string InvestmentType { get; set; } = null!; // FixedDeposit, Bond, MutualFund, Stock, Other
    public string Description { get; set; } = null!;
    public DateTime PurchaseDate { get; set; }
    public DateTime? MaturityDate { get; set; }
    public DateTime? SaleDate { get; set; }
    public decimal PurchaseCost { get; set; }
    public decimal CurrentValue { get; set; }
    public decimal? SaleProceeds { get; set; }
    public decimal? GainLoss { get; set; }
    public decimal InterestRate { get; set; }
    public string Status { get; set; } = "Active";    // Active, Matured, Sold
    public string? InstitutionName { get; set; }       // ชื่อสถาบัน/ธนาคาร
    public string? AccountNumber { get; set; }

    // Account mapping
    public Guid InvestmentAccountId { get; set; }      // 112xx
    public ChartOfAccount InvestmentAccount { get; set; } = null!;

    public Guid? PurchaseJournalId { get; set; }
    public JournalEntry? PurchaseJournal { get; set; }
    public Guid? SaleJournalId { get; set; }
    public JournalEntry? SaleJournal { get; set; }
}
