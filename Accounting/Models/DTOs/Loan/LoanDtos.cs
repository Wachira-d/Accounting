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

public record MakeLoanPaymentRequest(
    int InstallmentNumber, DateTime PaymentDate,
    decimal PrincipalPaid, decimal InterestPaid,
    decimal? LateFee, string PaymentMethod, string? Reference);

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
