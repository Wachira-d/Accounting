using Accounting.Models.DTOs.Tax;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

public interface ITaxService
{
    Task<TaxReportResponse> GenerateTaxReportAsync(Guid companyId, CreateTaxReportRequest request);
    Task<TaxReportResponse> GetTaxReportAsync(Guid companyId, Guid reportId);
    Task<List<TaxReportResponse>> GetTaxReportsAsync(Guid companyId, TaxType? taxType = null, int? year = null);
    Task<TaxReportResponse> FileTaxReportAsync(Guid companyId, Guid reportId);
    Task<TaxReportResponse> UpdateTaxReportAsync(Guid companyId, Guid reportId, UpdateTaxReportRequest request);
    Task<TaxReportResponse> RegenerateTaxReportAsync(Guid companyId, Guid reportId);
    Task DeleteTaxReportAsync(Guid companyId, Guid reportId);
    Task<int> AutoRefreshReportsAsync(Guid companyId, int months = 2);
    Task<object> GetVatDebugAsync(Guid companyId, int year, int month);

    /// <summary>เอกสารงวดอื่นที่มี VAT และยังไม่ถูกใช้ในรายงานใด — สำหรับดึงเข้างวดนี้</summary>
    Task<List<PullableDocumentDto>> GetPullableDocumentsAsync(Guid companyId, Guid reportId, string? search, DateTime? fromDate, DateTime? toDate);
    /// <summary>ดึงเอกสารเก่าเข้ารายงานภาษีงวดนี้เป็นบรรทัดใหม่</summary>
    Task<TaxReportResponse> PullDocumentIntoReportAsync(Guid companyId, Guid reportId, Guid documentId);

    // Task 4 of ERP upgrade
    Task<Models.Entities.VatDeferral> DeferInputVatAsync(Guid companyId, Guid documentId, int deferredToPeriod, string? reason, string userId);
    Task UnlockTaxFilingAsync(Guid companyId, Guid reportId, string userId, string reason);
    Task<TaxReportResponse> RejectAndReverseTaxReportAsync(Guid companyId, Guid reportId, string reason, Guid? nonClaimableVatAccountId, string userId);
    Task<bool> IsDocumentFilingLockedAsync(Guid companyId, Guid documentId);

    // RD pipe-delimited e-Filing export (Phase E)
    Task<Models.Entities.EFilingExport> GenerateEFilingAsync(Guid companyId, string formType, int year, int month, string userId);
}
