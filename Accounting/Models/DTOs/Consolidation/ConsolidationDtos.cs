using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Consolidation;

public record CreateConsolidationGroupRequest(
    string Name, string? Description,
    Guid ParentCompanyId, string Currency,
    int FiscalYearStartMonth);

public record AddConsolidationMemberRequest(
    Guid CompanyId, decimal OwnershipPercent,
    ConsolidationMethod Method);

public record ConsolidationGroupResponse(
    Guid Id, string Name, string? Description,
    Guid ParentCompanyId, string ParentCompanyName,
    string Currency, bool IsActive,
    List<ConsolidationMemberResponse> Members);

public record ConsolidationMemberResponse(
    Guid Id, Guid CompanyId, string CompanyName,
    decimal OwnershipPercent, ConsolidationMethod Method,
    bool IsActive);

public record ConsolidatedReportResponse(
    Guid GroupId, string GroupName, string ReportType,
    DateTime AsOfDate, DateTime? FromDate, DateTime? ToDate,
    string ReportDataJson, string? EliminationEntriesJson,
    string? MinorityInterestJson, DateTime GeneratedAt);

public record EliminationEntryResponse(
    string Description, string SourceCompany,
    string TargetCompany, decimal Amount,
    string AccountCode, string AccountName);
