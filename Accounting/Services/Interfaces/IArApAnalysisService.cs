using Accounting.Models.DTOs.ArApAnalysis;

namespace Accounting.Services.Interfaces;

public interface IArApAnalysisService
{
    Task<ArApOverviewResponse> GetOverviewAsync(Guid companyId);
    Task<ContactArApDetailResponse> GetContactDetailAsync(Guid companyId, Guid contactId, string type);
    Task<BadDebtAnalysisResponse> GetBadDebtAnalysisAsync(Guid companyId);
}
