namespace Accounting.Models.DTOs.Accounting;

// ═══ งบกำไรขาดทุนเปรียบเทียบ + drill-down (ทีม M · 2026-10-22) ═══
// ตัวเลขทุกช่องมาจาก GetProfitAndLossAsync ตัวเดียว (ต่อช่วง) — ประกอบตารางที่ Helpers/PnlComparison · หน้าแสดงอย่างเดียว

/// <summary>คอลัมน์หนึ่งช่วง — <paramref name="IsPartial"/> = ช่วงถูกตัดโดยวันเริ่ม/วันสิ้นสุดที่เลือก (ไม่เต็มเดือน/ไตรมาส/ปีบัญชี) ·
/// Prior* = ช่วงเดียวกันปีก่อน (null เมื่อไม่ได้ขอเปรียบเทียบ)</summary>
public record PnlComparePeriodInfo(
    string Label,
    DateTime Start,
    DateTime EndInclusive,
    bool IsPartial,
    DateTime? PriorStart,
    DateTime? PriorEndInclusive);

/// <summary>หนึ่งแถวของตาราง — <paramref name="Kind"/> = "Account" | "TotalRevenue" | "TotalExpenses" | "NetIncome" ·
/// <paramref name="Amounts"/> ต่อคอลัมน์ (ลำดับเดียวกับ Periods) · <paramref name="Total"/> = ผลรวมของ Amounts (= ยอดทั้งงวด) ·
/// <paramref name="ChangeVsPrevious"/> = % เทียบคอลัมน์ก่อนหน้า (ช่องแรก/ฐาน 0 = null) ·
/// Prior* / ChangeVsPriorYear = ช่วงเดียวกันปีก่อน (null เมื่อไม่ได้ขอ)</summary>
public record PnlCompareRow(
    string Kind,
    Guid? AccountId,
    string AccountCode,
    string AccountName,
    List<decimal> Amounts,
    decimal Total,
    List<decimal?> ChangeVsPrevious,
    List<decimal>? PriorAmounts,
    decimal? PriorTotal,
    List<decimal?>? ChangeVsPriorYear,
    decimal? TotalChangeVsPriorYear);

public record PnlComparisonResponse(
    DateTime FromDate,
    DateTime ToDate,
    string Granularity,
    int FiscalYearStartMonth,
    bool ComparePriorYear,
    List<PnlComparePeriodInfo> Periods,
    List<PnlCompareRow> Revenue,
    List<PnlCompareRow> Expenses,
    PnlCompareRow TotalRevenue,
    PnlCompareRow TotalExpenses,
    PnlCompareRow NetIncome,
    List<string> Notes);

/// <summary>ลิงก์ไปเอกสารต้นทางของใบสำคัญ — <paramref name="Kind"/> เป็นชื่อ (Document · PosOrder · Payroll · Depreciation ·
/// ExpenseClaim · Reversal · Hidden · MissingDocument · AutoNoSource · Manual) · <paramref name="Href"/> = null ⇒ ไม่มีเอกสารให้เปิด
/// (แสดงตามจริง ไม่แต่งลิงก์)</summary>
public record PnlSourceLink(string Kind, string Label, string? Href, string? Note);

/// <summary>หนึ่งแถวของ drill-down = บรรทัดบัญชีของผังนี้ในใบสำคัญหนึ่งใบ ·
/// <paramref name="Amount"/> = ยอดตามด้านปกติของผัง (รายได้ = เครดิต − เดบิต · ค่าใช้จ่าย = เดบิต − เครดิต) — ผลรวมทุกแถว = ตัวเลขในช่องที่คลิก</summary>
public record PnlDrilldownLine(
    Guid JournalEntryId,
    string EntryNumber,
    DateTime EntryDate,
    string JournalType,
    string? Description,
    string? Reference,
    decimal Debit,
    decimal Credit,
    decimal Amount,
    string JournalHref,
    PnlSourceLink Source);

public record PnlDrilldownResponse(
    Guid AccountId,
    string AccountCode,
    string AccountName,
    string AccountType,
    DateTime FromDate,
    DateTime ToDate,
    decimal Total,
    int TotalCount,
    int Page,
    int PageSize,
    int WithoutSourceCount,
    List<PnlDrilldownLine> Lines,
    List<string> Notes);
