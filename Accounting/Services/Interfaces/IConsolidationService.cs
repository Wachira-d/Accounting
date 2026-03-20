using Accounting.Models.DTOs;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface IConsolidationService
{
    // Group management
    Task<ConsolidationGroupResponse> CreateGroupAsync(CreateConsolidationGroupRequest request, string createdBy);
    Task<ConsolidationGroupResponse> GetGroupAsync(Guid groupId);
    Task<List<ConsolidationGroupResponse>> GetGroupsAsync(Guid parentCompanyId);
    Task<ConsolidationGroupResponse> AddMemberAsync(Guid groupId, AddConsolidationMemberRequest request);
    Task RemoveMemberAsync(Guid groupId, Guid memberId);

    // Consolidated reports
    Task<ConsolidatedReportResponse> GenerateConsolidatedBalanceSheetAsync(Guid groupId, DateTime asOfDate);
    Task<ConsolidatedReportResponse> GenerateConsolidatedPnLAsync(Guid groupId, DateTime fromDate, DateTime toDate);
    Task<List<EliminationEntryResponse>> GetEliminationEntriesAsync(Guid groupId, DateTime asOfDate);
}

public record CreateConsolidationGroupRequest(string Name, string? Description, Guid ParentCompanyId, string Currency, int FiscalYearStartMonth);
public record AddConsolidationMemberRequest(Guid CompanyId, decimal OwnershipPercent, ConsolidationMethod Method);
public record ConsolidationGroupResponse(Guid Id, string Name, string? Description, Guid ParentCompanyId, string ParentCompanyName, string Currency, bool IsActive, List<ConsolidationMemberResponse> Members);
public record ConsolidationMemberResponse(Guid Id, Guid CompanyId, string CompanyName, decimal OwnershipPercent, ConsolidationMethod Method, bool IsActive);
public record ConsolidatedReportResponse(Guid GroupId, string GroupName, string ReportType, DateTime AsOfDate, DateTime? FromDate, DateTime? ToDate, string ReportDataJson, string? EliminationEntriesJson, string? MinorityInterestJson, DateTime GeneratedAt);
public record EliminationEntryResponse(string Description, string SourceCompany, string TargetCompany, decimal Amount, string AccountCode, string AccountName);
