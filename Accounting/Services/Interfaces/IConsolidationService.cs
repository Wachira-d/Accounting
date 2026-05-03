using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Consolidation;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IConsolidationService
{
    // Group management
    Task<ConsolidationGroupResponse> CreateGroupAsync(CreateConsolidationGroupRequest request, string createdBy);
    Task<ConsolidationGroupResponse> GetGroupAsync(Guid groupId);
    Task<List<ConsolidationGroupResponse>> GetGroupsAsync(Guid parentCompanyId);
    Task<ConsolidationGroupResponse> AddMemberAsync(Guid groupId, AddConsolidationMemberRequest request);
    Task<ConsolidationGroupResponse> UpdateGroupAsync(Guid groupId, UpdateConsolidationGroupRequest request);
    Task DeleteGroupAsync(Guid groupId);
    Task RemoveMemberAsync(Guid groupId, Guid memberId);

    // Consolidated reports
    Task<ConsolidatedReportResponse> GenerateConsolidatedBalanceSheetAsync(Guid groupId, DateTime asOfDate);
    Task<ConsolidatedReportResponse> GenerateConsolidatedPnLAsync(Guid groupId, DateTime fromDate, DateTime toDate);
    Task<List<EliminationEntryResponse>> GetEliminationEntriesAsync(Guid groupId, DateTime asOfDate);
}
