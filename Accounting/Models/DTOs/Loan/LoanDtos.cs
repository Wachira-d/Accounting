namespace Accounting.Models.DTOs.Loan;

/// <summary>สร้างสินเชื่อ — สัญญานี้ต้องตรงกับที่ <c>loans.html</c> ส่งจริง (ทีม F1 · 2026-10-14 · PAYMENT_CHANNEL_AUDIT ส่วนที่ 2 ข้อ 2):
/// เดิมหน้าเว็บส่ง <c>loanNumber/lenderName/startDate/maturityDate</c> ⇒ <c>Name</c> ขาด = 400 ทุกครั้ง · วันที่เป็น 0001-01-01 · ไม่มีผังบัญชีเลย
///
/// <para>ช่องทางเงิน: <paramref name="BankAccountId"/> = <b>BankAccounts.Id</b> (เซิร์ฟเวอร์แปลงเป็นผังที่ผูก) <b>หรือ</b>
/// <paramref name="MoneyAccountId"/> = ผังเงินสดตรง (ChartOfAccounts.Id) — อย่างใดอย่างหนึ่ง · เก็บเป็น <c>Loan.MoneyAccountId</c> (ผังบัญชี)</para>
/// <para><paramref name="PostDisbursementEntry"/>: true = ลง JE รับเงินกู้ (ต้องมีผังเงินกู้ + ช่องทางเงิน) · false = ไม่ลง (สินเชื่อเดิมที่ยอดอยู่ใน
/// งบยกมาแล้ว) · null = แบบเดิมของผู้เรียก API (ลงเมื่อมีครบทั้งสอง · มีครึ่งเดียว = ปฏิเสธ ไม่ข้ามเงียบ)</para>
/// <para><paramref name="FirstPaymentDate"/> ไม่ส่ง = หนึ่งเดือนหลังวันรับเงิน · วันครบกำหนดเซิร์ฟเวอร์คำนวณเอง (หน้าไม่ส่ง)</para></summary>
public record CreateLoanRequest(
    string LoanType, string Name, string? Lender,
    Guid? ContactId, decimal PrincipalAmount,
    decimal InterestRate, string InterestType,
    int TermMonths, DateTime DisbursementDate,
    DateTime? FirstPaymentDate, string? RepaymentFrequency,
    Guid? LoanAccountId, Guid? InterestExpenseAccountId,
    Guid? BankAccountId,
    Guid? MoneyAccountId = null,
    bool? PostDisbursementEntry = null);

/// <summary>แก้สินเชื่อ — null = ไม่เปลี่ยนช่องนั้น · ผังบัญชีแก้ได้ที่นี่ (เดิมไม่มีช่องผังเลย ขณะที่ข้อความตอนบันทึกชำระบอกให้
/// "ตั้งค่าในรายละเอียดสินเชื่อ" = ทางตัน) · ช่องทางเงินส่ง <paramref name="BankAccountId"/> (BankAccounts.Id) หรือ <paramref name="MoneyAccountId"/>
/// (ผังเงินสด) อย่างใดอย่างหนึ่ง</summary>
public record UpdateLoanRequest(
    string? Name, decimal? InterestRate,
    string? Status, string? Notes,
    Guid? LoanAccountId = null,
    Guid? InterestExpenseAccountId = null,
    Guid? BankAccountId = null,
    Guid? MoneyAccountId = null);

/// <param name="MoneyAccountId">ผังบัญชีที่เงินกู้รับเข้า/ค่างวดจ่ายออก (ChartOfAccounts.Id — ไม่ใช่ BankAccounts.Id) · หน้าเลือกบัญชีธนาคารที่ผูกผังนี้ให้เอง</param>
/// <param name="Notice">ข้อสังเกตจากการแก้ (ไม่บล็อก) — เช่นเปลี่ยนผังเงินกู้หลังมีการชำระแล้ว ยอดเดิมยังอยู่ในผังเก่า</param>
public record LoanResponse(
    Guid Id, string LoanNumber, string LoanType, string Name,
    string? Lender, decimal PrincipalAmount,
    decimal InterestRate, string InterestType,
    int TermMonths, DateTime DisbursementDate,
    DateTime MaturityDate, decimal MonthlyPayment,
    decimal OutstandingPrincipal, decimal TotalInterestPaid,
    string Status, DateTime CreatedAt,
    DateTime? FirstPaymentDate = null,
    string? RepaymentFrequency = null,
    Guid? ContactId = null,
    Guid? LoanAccountId = null,
    Guid? InterestExpenseAccountId = null,
    Guid? MoneyAccountId = null,
    string? Notice = null);

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
/// งวดแรก · 0 ในช่องต้น/ดอกมีความหมาย ("จ่ายดอกอย่างเดียว") ต้องไม่หาย
///
/// จ่ายจากบัญชีไหน (ทีม F1 · 2026-10-14): <c>BankAccountId</c> (BankAccounts.Id) หรือ <c>MoneyAccountId</c> (ผังเงินสด) ที่ส่งมา &gt;
/// ผังเงินของสินเชื่อ (<c>Loan.MoneyAccountId</c>) &gt; ปฏิเสธพร้อมบอกให้เลือก — เดิมไม่สนผังของสินเชื่อแล้วจ่ายจาก 11122/111x ตัวแรกเสมอ</summary>
public record MakeLoanPaymentRequest(
    DateTime PaymentDate,
    decimal? Amount = null,
    decimal? PrincipalPortion = null,
    decimal? InterestPortion = null,
    int? InstallmentNumber = null,
    decimal? LateFee = null,
    string? PaymentMethod = null,
    string? Notes = null,
    string? Reference = null,
    Guid? BankAccountId = null,
    Guid? MoneyAccountId = null);

public record LoanPaymentResponse(
    Guid Id, int InstallmentNumber, DateTime PaymentDate,
    decimal PrincipalPaid, decimal InterestPaid,
    decimal TotalPaid, decimal? LateFee, string PaymentMethod,
    string? Reference = null);

public record LoanSummaryResponse(
    int TotalLoans, int ActiveLoans,
    decimal TotalOutstandingPrincipal,
    decimal TotalMonthlyPayment,
    decimal TotalInterestPaidYTD,
    List<LoanResponse> Loans);

/// <summary>ผังที่เสนอเป็นค่าตั้งต้นของฟอร์มสินเชื่อ (ตัวตัดสิน <c>Helpers/LoanPostingPlan.SuggestLoanAccountCode</c>) · null = ผังบริษัทไม่มี</summary>
public record LoanAccountDefaultsResponse(Guid? LoanAccountId, Guid? InterestExpenseAccountId);
