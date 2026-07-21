namespace Accounting.Models.DTOs.AdvancedArAp;

public record SetCreditLimitRequest(
    decimal CreditLimit, int CreditTermDays, string? CreditRating);

public record CreditSettingResponse(
    Guid ContactId, string ContactName, decimal CreditLimit,
    int CreditTermDays, decimal CurrentBalance,
    decimal AvailableCredit, string? CreditRating,
    bool IsOnHold, string? HoldReason);

public record CreditCheckResponse(
    bool IsApproved, decimal RequestedAmount,
    decimal AvailableCredit, decimal CurrentBalance,
    decimal CreditLimit, string? Reason);

public record DunningLetterResponse(
    Guid Id, string LetterNumber, Guid ContactId,
    string ContactName, int DunningLevel, DateTime LetterDate,
    decimal TotalOverdueAmount, int OldestOverdueDays,
    string Status, DateTime? SentAt, int LineCount);

public record PaymentReminderResponse(
    Guid Id, Guid DocumentId, string DocumentNumber,
    Guid ContactId, string ContactName, int ReminderLevel,
    DateTime ReminderDate, string Status, DateTime? SentAt);

public record StatementResponse(
    Guid ContactId, string ContactName,
    DateTime FromDate, DateTime ToDate,
    decimal OpeningBalance, decimal TotalInvoiced,
    decimal TotalPaid, decimal ClosingBalance,
    List<StatementLine> Lines);

public record StatementLine(
    DateTime Date, string DocumentNumber, string Description,
    decimal Debit, decimal Credit, decimal Balance);
