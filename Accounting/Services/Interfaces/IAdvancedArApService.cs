using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IAdvancedArApService
{
    // Credit Management
    Task<CreditSettingResponse> SetCreditLimitAsync(Guid companyId, Guid contactId, SetCreditLimitRequest request);
    Task<CreditSettingResponse> GetCreditSettingAsync(Guid companyId, Guid contactId);
    Task<List<CreditSettingResponse>> GetAllCreditSettingsAsync(Guid companyId);
    Task<CreditCheckResponse> CheckCreditAsync(Guid companyId, Guid contactId, decimal amount);
    Task HoldContactAsync(Guid companyId, Guid contactId, string reason);
    Task ReleaseHoldAsync(Guid companyId, Guid contactId);

    // Dunning
    Task<DunningLetterResponse> GenerateDunningLetterAsync(Guid companyId, Guid contactId, int level);
    Task<DunningLetterResponse> SendDunningLetterAsync(Guid companyId, Guid letterId, string channel);
    Task<PagedResponse<DunningLetterResponse>> GetDunningLettersAsync(Guid companyId, PagedRequest request);

    // Payment Reminders
    Task<int> SendPaymentRemindersAsync(Guid companyId, int daysBefore = 3);
    Task<List<PaymentReminderResponse>> GetRemindersAsync(Guid companyId, Guid? documentId = null);

    // Statements
    Task<StatementResponse> GenerateStatementAsync(Guid companyId, Guid contactId, DateTime fromDate, DateTime toDate);
}

public record SetCreditLimitRequest(decimal CreditLimit, int CreditTermDays, string? CreditRating);
public record CreditSettingResponse(Guid ContactId, string ContactName, decimal CreditLimit, int CreditTermDays, decimal CurrentBalance, decimal AvailableCredit, string? CreditRating, bool IsOnHold, string? HoldReason);
public record CreditCheckResponse(bool IsApproved, decimal RequestedAmount, decimal AvailableCredit, decimal CurrentBalance, decimal CreditLimit, string? Reason);

public record DunningLetterResponse(Guid Id, string LetterNumber, Guid ContactId, string ContactName, int DunningLevel, DateTime LetterDate, decimal TotalOverdueAmount, int OldestOverdueDays, string Status, DateTime? SentAt, int LineCount);
public record PaymentReminderResponse(Guid Id, Guid DocumentId, string DocumentNumber, Guid ContactId, string ContactName, int ReminderLevel, DateTime ReminderDate, string Status, DateTime? SentAt);
public record StatementResponse(Guid ContactId, string ContactName, DateTime FromDate, DateTime ToDate, decimal OpeningBalance, decimal TotalInvoiced, decimal TotalPaid, decimal ClosingBalance, List<StatementLine> Lines);
public record StatementLine(DateTime Date, string DocumentNumber, string Description, decimal Debit, decimal Credit, decimal Balance);
