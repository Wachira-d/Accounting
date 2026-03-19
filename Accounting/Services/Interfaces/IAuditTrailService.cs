using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Audit;

namespace Accounting.Services.Interfaces;

public interface IAuditTrailService
{
    Task<PagedResponse<AuditLogResponse>> GetLogsAsync(Guid companyId, AuditLogQueryRequest request);
    Task<AuditSummaryResponse> GetSummaryAsync(Guid companyId, DateTime fromDate, DateTime toDate);
    Task<List<AuditLogResponse>> GetEntityHistoryAsync(Guid companyId, string entityType, string entityId);
    Task<List<AuditLogResponse>> GetUserActivityAsync(Guid companyId, Guid userId, int limit = 100);
}
