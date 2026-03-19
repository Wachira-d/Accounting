using Accounting.Models.DTOs;
using Accounting.Models.DTOs.FixedAsset;

namespace Accounting.Services.Interfaces;

public interface IFixedAssetService
{
    Task<FixedAssetResponse> CreateAsync(Guid companyId, CreateFixedAssetRequest request, string createdBy);
    Task<FixedAssetResponse> GetByIdAsync(Guid companyId, Guid assetId);
    Task<PagedResponse<FixedAssetResponse>> GetAllAsync(Guid companyId, PagedRequest request);
    Task<FixedAssetResponse> UpdateAsync(Guid companyId, Guid assetId, UpdateFixedAssetRequest request);
    Task<FixedAssetResponse> DisposeAsync(Guid companyId, Guid assetId, DisposeAssetRequest request, string performedBy);
    Task<List<DepreciationResponse>> GetDepreciationsAsync(Guid companyId, Guid assetId);
    Task<List<DepreciationResponse>> CalculateDepreciationAsync(Guid companyId, CalculateDepreciationRequest request, string performedBy);
}
