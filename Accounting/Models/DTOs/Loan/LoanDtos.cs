namespace Accounting.Models.DTOs.Loan;

public record CreateLoanRequest(
    string LoanType, string Name, string? Lender,
    Guid? ContactId, decimal PrincipalAmount,
    decimal InterestRate, string InterestType,
    int TermMonths, DateTime DisbursementDate,
    DateTime FirstPaymentDate, string RepaymentFrequency,
    Guid? LoanAccountId, Guid? InterestExpenseAccountId,
    Guid? BankAccountId);

public record UpdateLoanRequest(
    string? Name, decimal? InterestRate,
    string? Status, string? Notes);

public record LoanResponse(
    Guid Id, string LoanNumber, string LoanType, string Name,
    string? Lender, decimal PrincipalAmount,
    decimal InterestRate, string InterestType,
    int TermMonths, DateTime DisbursementDate,
    DateTime MaturityDate, decimal MonthlyPayment,
    decimal OutstandingPrincipal, decimal TotalInterestPaid,
    string Status, DateTime CreatedAt);

public record LoanScheduleResponse(
    int InstallmentNumber, DateTime DueDate,
    decimal PaymentAmount, decimal PrincipalPortion,
    decimal InterestPortion, decimal RemainingBalance,
    bool IsPaid, DateTime? PaidDate);

/// <summary>บันทึกชำระสินเชื่อ — สัญญานี้ต้องตรงกับสิ่งที่หน้า loans.html ส่งจริง
///
/// ═══ ที่มา (บั๊กที่ทำให้จ่ายหนี้แล้วยอดไม่ลดเลยตั้งแต่เขียนมา) ═══
/// เดิม record นี้รับ <c>InstallmentNumber/PrincipalPaid/InterestPaid/
/// PaymentMethod</c> แต่หน้าเว็บส่ง <c>amount/principalPortion/
/// interestPortion/notes</c> — **ชื่อไม่ตรงกันสักตัว** ⇒ model binder ได้ 0
/// ทุกช่อง ⇒ <c>OutstandingPrincipal -= 0</c> = บันทึกชำระ "สำเร็จ" แต่หนี้
/// ไม่ลด, JE เป็นศูนย์, ตารางผ่อนไม่ถูกติ๊กจ่าย (defect class เดียวกับ
/// `_buildReviewCorrection` ที่เคยเจอ: ครึ่งเดียวของสัญญา ship ไปคนเดียว)
///
/// สัญญาใหม่: ผู้ใช้กรอก "ยอดที่จ่ายจริง" เป็นหลัก การแตกต้น/ดอกเป็นทางเลือก
/// (ไม่กรอก = ระบบแตกให้จากตารางผ่อนของงวดนั้น) · งวดไม่ระบุ = งวดค้างจ่าย
/// งวดแรก · 0 ในช่องต้น/ดอกมีความหมาย ("จ่ายดอกอย่างเดียว") ต้องไม่หาย</summary>
public record MakeLoanPaymentRequest(
    DateTime PaymentDate,
    decimal? Amount = null,
    decimal? PrincipalPortion = null,
    decimal? InterestPortion = null,
    int? InstallmentNumber = null,
    decimal? LateFee = null,
    string? PaymentMethod = null,
    string? Notes = null,
    string? Reference = null);

public record LoanPaymentResponse(
    Guid Id, int InstallmentNumber, DateTime PaymentDate,
    decimal PrincipalPaid, decimal InterestPaid,
    decimal TotalPaid, decimal? LateFee, string PaymentMethod);

public record LoanSummaryResponse(
    int TotalLoans, int ActiveLoans,
    decimal TotalOutstandingPrincipal,
    decimal TotalMonthlyPayment,
    decimal TotalInterestPaidYTD,
    List<LoanResponse> Loans);
