using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Budget;

namespace Accounting.Services.Interfaces;

public interface IBudgetService
{
    Task<BudgetResponse> CreateAsync(Guid companyId, CreateBudgetRequest request, string createdBy);
    Task<BudgetResponse> GetByIdAsync(Guid companyId, Guid budgetId);
    Task<List<BudgetResponse>> GetAllAsync(Guid companyId, int? fiscalYear = null);
    Task<PagedResponse<BudgetResponse>> GetAllPagedAsync(Guid companyId, int? fiscalYear, PagedRequest request);
    Task<BudgetResponse> UpdateAsync(Guid companyId, Guid budgetId, UpdateBudgetRequest request);
    Task DeleteAsync(Guid companyId, Guid budgetId);
    Task<BudgetVsActualResponse> GetBudgetVsActualAsync(Guid companyId, Guid budgetId);
}
