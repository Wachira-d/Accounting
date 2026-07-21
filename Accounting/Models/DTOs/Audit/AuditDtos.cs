using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Audit;

public record AuditLogResponse(
    long Id,
    Guid? CompanyId,
    Guid? UserId,
    string? UserEmail,
    AuditAction Action,
    string EntityType,
    string? EntityId,
    string? OldValues,
    string? NewValues,
    string? IpAddress,
    string? UserAgent,
    DateTime Timestamp);

public record AuditLogQueryRequest(
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    Guid? UserId = null,
    AuditAction? Action = null,
    string? EntityType = null,
    string? EntityId = null,
    int Page = 1,
    int PageSize = 50);

public record AuditSummaryResponse(
    DateTime FromDate,
    DateTime ToDate,
    int TotalActions,
    List<AuditActionSummary> ActionSummary,
    List<AuditUserSummary> UserSummary,
    List<AuditEntitySummary> EntitySummary);

public record AuditActionSummary(
    AuditAction Action,
    int Count);

public record AuditUserSummary(
    Guid UserId,
    string? UserEmail,
    int ActionCount,
    DateTime LastActivity);

public record AuditEntitySummary(
    string EntityType,
    int CreateCount,
    int UpdateCount,
    int DeleteCount);
