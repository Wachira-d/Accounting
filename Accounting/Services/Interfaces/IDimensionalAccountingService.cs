using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Dimension;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

// ===== Dimension / Cost Center / Profit Center =====
public interface IDimensionalAccountingService
{
    // Dimensions (Cost Center, Profit Center, Department, etc.)
    Task<DimensionResponse> CreateDimensionAsync(Guid companyId, CreateDimensionRequest request);
    Task<List<DimensionResponse>> GetDimensionsAsync(Guid companyId, DimensionType? type = null);
    Task<DimensionResponse> GetDimensionAsync(Guid companyId, Guid dimensionId);
    Task<DimensionResponse> UpdateDimensionAsync(Guid companyId, Guid dimensionId, UpdateDimensionRequest request);
    Task DeleteDimensionAsync(Guid companyId, Guid dimensionId);

    // Dimension allocation on journal entries
    Task AssignDimensionsAsync(Guid companyId, Guid journalEntryLineId, List<DimensionAllocationRequest> allocations);
    Task<List<DimensionAllocationResponse>> GetLineDimensionsAsync(Guid companyId, Guid journalEntryLineId);

    // Reports by dimension
    Task<DimensionPnLResponse> GetDimensionPnLAsync(Guid companyId, Guid dimensionId, DateTime fromDate, DateTime toDate);
    Task<List<DimensionSummaryResponse>> GetDimensionSummaryAsync(Guid companyId, DimensionType type, DateTime fromDate, DateTime toDate);

    // Branches
    Task<BranchResponse> CreateBranchAsync(Guid companyId, CreateBranchRequest request);
    Task<List<BranchResponse>> GetBranchesAsync(Guid companyId);
    Task<BranchResponse> UpdateBranchAsync(Guid companyId, Guid branchId, UpdateBranchRequest request);
}
