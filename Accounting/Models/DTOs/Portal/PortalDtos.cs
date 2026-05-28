namespace Accounting.Models.DTOs.Portal;

public record CreatePortalAccessRequest(Guid ContactId, string Email, string Password, string? DisplayName, bool CanViewInvoices, bool CanViewStatements, bool CanDownloadPdf, bool CanMakePayment);
public record UpdatePortalAccessRequest(bool? CanViewInvoices, bool? CanViewStatements, bool? CanDownloadPdf, bool? CanMakePayment, bool? IsActive);
public record PortalAccessResponse(Guid Id, Guid ContactId, string ContactName, string Email, string? DisplayName, bool IsActive, DateTime? LastLoginAt, bool CanViewInvoices, bool CanViewStatements, bool CanDownloadPdf, bool CanMakePayment);

public record PortalLoginRequest(string Email, string Password, Guid CompanyId);
public record PortalLoginResponse(string AccessToken, string RefreshToken, Guid ContactId, string ContactName, Guid CompanyId, string CompanyName);
public record PortalRefreshRequest(string RefreshToken);

public record PortalDocumentResponse(Guid Id, string DocumentNumber, string DocumentType, DateTime DocumentDate, DateTime? DueDate, decimal TotalAmount, decimal PaidAmount, decimal BalanceDue, string Status);
public record PortalStatementResponse(DateTime FromDate, DateTime ToDate, decimal OpeningBalance, decimal TotalCharged, decimal TotalPaid, decimal ClosingBalance, List<PortalStatementLine> Lines);
public record PortalStatementLine(DateTime Date, string DocumentNumber, string Description, decimal Amount, decimal Balance);
public record PortalPaymentResponse(Guid Id, DateTime PaymentDate, decimal Amount, string PaymentMethod, string? Reference, string DocumentNumber);

public record PortalSlipUploadResponse(Guid PaymentId, Guid DocumentId, string SlipUrl, decimal Amount, DateTime UploadedAt);
