namespace Accounting.Models.DTOs.AdvancedArAp;

public record CreditSettingResponse(
    Guid Id, Guid ContactId, string ContactName, decimal CreditLimit,
    int CreditTermDays, decimal CurrentBalance, decimal AvailableCredit,
    string? CreditRating, bool IsOnHold, string? HoldReason,
    DateTime? LastReviewDate, DateTime? NextReviewDate);

public record UpdateCreditSettingRequest(
    decimal? CreditLimit = null, int? CreditTermDays = null,
    string? CreditRating = null, bool? IsOnHold = null, string? HoldReason = null);

public record DunningLetterResponse(
    Guid Id, Guid ContactId, string ContactName, int DunningLevel,
    DateTime LetterDate, decimal TotalOverdueAmount, int OverdueDays,
    string? Message, string Status, DateTime? SentDate);

public record PaymentReminderResponse(
    Guid Id, Guid ContactId, string ContactName, Guid DocumentId,
    string DocumentNumber, decimal Amount, DateTime DueDate,
    int DaysBeforeDue, string Status, DateTime? SentDate);

public record AccountStatementResponse(
    Guid ContactId, string ContactName, DateTime FromDate, DateTime ToDate,
    decimal OpeningBalance, decimal ClosingBalance,
    List<StatementLineResponse> Lines);

public record StatementLineResponse(
    DateTime Date, string DocumentNumber, string Description,
    decimal DebitAmount, decimal CreditAmount, decimal RunningBalance);
