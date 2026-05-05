using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Recurring;

namespace Accounting.Services.Interfaces;

public interface IRecurringTransactionService
{
    Task<RecurringTransactionResponse> CreateAsync(Guid companyId, CreateRecurringTransactionRequest request, string createdBy);
    Task<RecurringTransactionResponse> GetByIdAsync(Guid companyId, Guid id);
    Task<PagedResponse<RecurringTransactionResponse>> GetAllAsync(Guid companyId, PagedRequest request, string? status = null);
    Task<RecurringTransactionResponse> UpdateAsync(Guid companyId, Guid id, UpdateRecurringTransactionRequest request);
    Task DeleteAsync(Guid companyId, Guid id);
    Task<RecurringTransactionResponse> PauseAsync(Guid companyId, Guid id);
    Task<RecurringTransactionResponse> ResumeAsync(Guid companyId, Guid id);
    Task<RecurringTransactionResponse> RunNowAsync(Guid companyId, Guid id, string performedBy);
    Task ProcessDueRecurringTransactionsAsync(); // Background job
}
