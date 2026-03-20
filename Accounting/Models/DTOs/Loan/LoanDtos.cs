using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Loan;

public record CreateLoanRequest(
    string LoanNumber, string LoanType, string LenderName,
    decimal PrincipalAmount, decimal InterestRate, string InterestType,
    int TermMonths, DateTime StartDate, DateTime MaturityDate,
    string RepaymentFrequency, Guid? LoanAccountId,
    Guid? InterestExpenseAccountId, Guid? BankAccountId);

public record UpdateLoanRequest(
    string? LenderName = null, decimal? InterestRate = null, string? Status = null);

public record LoanResponse(
    Guid Id, string LoanNumber, string LoanType, string LenderName,
    decimal PrincipalAmount, decimal InterestRate, string InterestType,
    int TermMonths, DateTime StartDate, DateTime MaturityDate,
    string RepaymentFrequency, decimal OutstandingBalance,
    decimal TotalInterestPaid, string Status, DateTime CreatedAt);

public record LoanScheduleResponse(
    Guid Id, int InstallmentNumber, DateTime DueDate,
    decimal PrincipalAmount, decimal InterestAmount, decimal TotalAmount,
    decimal OutstandingBalance, bool IsPaid, DateTime? PaidDate);

public record CreateLoanPaymentRequest(
    Guid LoanId, DateTime PaymentDate, decimal PrincipalAmount,
    decimal InterestAmount, string? Reference);

public record LoanPaymentResponse(
    Guid Id, Guid LoanId, int? InstallmentNumber, DateTime PaymentDate,
    decimal PrincipalAmount, decimal InterestAmount, decimal TotalAmount,
    string? Reference, DateTime CreatedAt);
