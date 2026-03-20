using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IPortalService
{
    // Portal access management (by company)
    Task<PortalAccessResponse> CreateAccessAsync(Guid companyId, CreatePortalAccessRequest request);
    Task<List<PortalAccessResponse>> GetAccessesAsync(Guid companyId);
    Task<PortalAccessResponse> UpdateAccessAsync(Guid companyId, Guid accessId, UpdatePortalAccessRequest request);
    Task DeactivateAccessAsync(Guid companyId, Guid accessId);

    // Portal auth (by customer/supplier)
    Task<PortalLoginResponse> LoginAsync(PortalLoginRequest request);
    Task<PortalLoginResponse> RefreshTokenAsync(string refreshToken);

    // Portal data (by customer/supplier)
    Task<List<PortalDocumentResponse>> GetMyDocumentsAsync(Guid companyId, Guid contactId, string? documentType = null);
    Task<PortalDocumentResponse> GetDocumentAsync(Guid companyId, Guid contactId, Guid documentId);
    Task<byte[]> DownloadDocumentPdfAsync(Guid companyId, Guid contactId, Guid documentId);
    Task<PortalStatementResponse> GetMyStatementAsync(Guid companyId, Guid contactId, DateTime fromDate, DateTime toDate);
    Task<List<PortalPaymentResponse>> GetMyPaymentsAsync(Guid companyId, Guid contactId);
}

public record CreatePortalAccessRequest(Guid ContactId, string Email, string Password, string? DisplayName, bool CanViewInvoices, bool CanViewStatements, bool CanDownloadPdf, bool CanMakePayment);
public record UpdatePortalAccessRequest(bool? CanViewInvoices, bool? CanViewStatements, bool? CanDownloadPdf, bool? CanMakePayment, bool? IsActive);
public record PortalAccessResponse(Guid Id, Guid ContactId, string ContactName, string Email, string? DisplayName, bool IsActive, DateTime? LastLoginAt, bool CanViewInvoices, bool CanViewStatements, bool CanDownloadPdf, bool CanMakePayment);

public record PortalLoginRequest(string Email, string Password, Guid CompanyId);
public record PortalLoginResponse(string AccessToken, string RefreshToken, Guid ContactId, string ContactName, Guid CompanyId, string CompanyName);

public record PortalDocumentResponse(Guid Id, string DocumentNumber, string DocumentType, DateTime DocumentDate, DateTime? DueDate, decimal TotalAmount, decimal PaidAmount, decimal BalanceDue, string Status);
public record PortalStatementResponse(DateTime FromDate, DateTime ToDate, decimal OpeningBalance, decimal TotalCharged, decimal TotalPaid, decimal ClosingBalance, List<PortalStatementLine> Lines);
public record PortalStatementLine(DateTime Date, string DocumentNumber, string Description, decimal Amount, decimal Balance);
public record PortalPaymentResponse(Guid Id, DateTime PaymentDate, decimal Amount, string PaymentMethod, string? Reference, string DocumentNumber);
