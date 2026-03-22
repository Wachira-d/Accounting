using Accounting.Models.DTOs;
using Accounting.Models.DTOs.AdvancedArAp;

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
