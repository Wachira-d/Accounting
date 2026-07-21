using Accounting.Models.DTOs.Aging;

namespace Accounting.Services.Interfaces;

public interface IAgingReportService
{
    Task<AgingReportResponse> GetReceivableAgingAsync(Guid companyId, AgingReportRequest request);
    Task<AgingReportResponse> GetPayableAgingAsync(Guid companyId, AgingReportRequest request);
    Task<AgingContactDetail> GetContactAgingDetailAsync(Guid companyId, Guid contactId, AgingReportType reportType, DateTime? asOfDate = null);
}
