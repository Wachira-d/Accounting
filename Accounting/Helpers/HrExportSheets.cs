using Accounting.Models.DTOs.Expense;
using Accounting.Models.DTOs.Payroll;
using K = Accounting.Helpers.TabularCellKind;

namespace Accounting.Helpers;

/// <summary>
/// **คอลัมน์ไฟล์ส่งออกของงาน HR นอกรอบเงินเดือน** (เบิกเงินล่วงหน้า · ใบเบิกค่าใช้จ่าย) — X4 · 2026-10-14
/// <para>รับ DTO ตัวเดียวกับรายการบนจอ (ไม่คิวรี/คำนวณซ้ำ) · enum ออกเป็นชื่อ · วันที่ ISO ค.ศ. · เวลาประทับเป็นเวลาไทย</para>
/// </summary>
public static class HrExportSheets
{
    public static IReadOnlyList<TabularColumn<SalaryAdvanceResponse>> SalaryAdvances { get; } = new List<TabularColumn<SalaryAdvanceResponse>>
    {
        new("เลขที่", a => a.AdvanceNumber),
        new("รหัสพนักงาน", a => a.EmployeeCode),
        new("ชื่อพนักงาน", a => a.EmployeeName),
        new("วันที่ขอ", a => a.RequestDate, K.Date),
        new("จำนวนเงิน", a => a.Amount, K.Money),
        new("หักต่องวด", a => a.MonthlyDeduction, K.Money),
        new("หักคืนแล้ว", a => a.ClearedAmount, K.Money),
        new("คงค้าง", a => a.OutstandingAmount, K.Money),
        new("สถานะ", a => a.Status),
        new("เหตุผล", a => a.Reason),
        new("อนุมัติเมื่อ", a => a.ApprovedAt, K.Timestamp),
        new("ผู้อนุมัติ", a => a.ApprovedByName),
        new("หมายเหตุอนุมัติ", a => a.ApprovalNotes),
        new("เหตุผลที่ปฏิเสธ", a => a.RejectionReason),
        new("จ่ายเงินเมื่อ", a => a.DisbursedAt, K.Timestamp),
        new("สร้างเมื่อ", a => a.CreatedAt, K.Timestamp),
    };

    /// <summary>หนึ่งแถวต่อใบเบิก (ตรงกับรายการบนจอ) · ยอดระดับใบ = ยอดที่ระบบใช้ตั้งใบสำคัญจ่าย · รายการย่อยสรุปเป็นข้อความ</summary>
    public static IReadOnlyList<TabularColumn<ExpenseClaimResponse>> ExpenseClaims { get; } = new List<TabularColumn<ExpenseClaimResponse>>
    {
        new("เลขที่ใบเบิก", c => c.ClaimNumber),
        new("วันที่ค่าใช้จ่าย", c => c.ExpenseDate, K.Date),
        new("เรื่อง", c => c.Title),
        new("รายละเอียด", c => c.Description),
        new("สถานะ", c => c.Status.ToString()),
        new("ผู้ยื่น", c => c.SubmittedByName),
        new("ยอดก่อน VAT", c => c.SubTotal, K.Money),
        new("VAT", c => c.VatAmount, K.Money),
        new("หัก ณ ที่จ่าย", c => c.WithholdingTaxAmount, K.Money),
        new("ยอดรวม", c => c.TotalAmount, K.Money),
        new("จำนวนรายการ", c => c.Lines.Count, K.Integer),
        new("รายการ", c => string.Join(" | ", c.Lines.Select(l =>
            $"{l.Description} [{l.AccountName ?? l.Category ?? "-"}] {l.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}"))),
        new("ไม่มีใบเสร็จ", c => c.NoReceipt ? "ใช่" : "ไม่ใช่"),
        new("เหตุผลไม่มีใบเสร็จ", c => c.NoReceiptReason),
        new("ใบรับรองแทนใบเสร็จ", c => c.CertificateInLieuDocumentNumber),
        new("อนุมัติเมื่อ", c => c.ApprovedAt, K.Timestamp),
        new("ผู้อนุมัติ", c => c.ApprovedByName),
        new("จ่ายเมื่อ", c => c.PaidAt, K.Timestamp),
        new("อ้างอิงการจ่าย", c => c.PaidReference),
        new("สร้างเมื่อ", c => c.CreatedAt, K.Timestamp),
    };
}
