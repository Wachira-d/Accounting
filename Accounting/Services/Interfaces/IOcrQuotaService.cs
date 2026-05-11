using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IOcrQuotaService
{
    Task<OcrQuotaStatus> GetQuotaStatusAsync(Guid companyId);
    Task<bool> CanScanAsync(Guid companyId);
    /// <summary>
    /// Atomically reserves one OCR page from quota. Returns true if reservation succeeded.
    /// Caller MUST call RefundAsync if the scan ultimately fails.
    /// </summary>
    Task<bool> TryConsumeAsync(Guid companyId);
    /// <summary>Refunds a previously-consumed page when scan fails or is detected as duplicate.</summary>
    Task RefundAsync(Guid companyId);
    Task IncrementUsageAsync(Guid companyId);
    Task<OcrCreditPurchaseResponse> PurchaseCreditsAsync(Guid companyId, int pages, string performedBy);
    Task<OcrCreditPurchaseResponse> ReviewCreditPurchaseAsync(Guid purchaseId, bool approve, string? notes, string performedBy);
    Task<List<OcrCreditPurchaseResponse>> GetPurchaseHistoryAsync(Guid companyId);
    Task<List<OcrCreditPurchaseResponse>> GetPendingPurchasesAsync();
    Task ResetMonthlyUsageAsync();
}

public record OcrQuotaStatus(
    int MaxPagesPerMonth,
    int UsedThisMonth,
    int BonusPages,
    int CreditPagesRemaining,
    int TotalAvailable,
    DateTime UsageResetDate,
    decimal CreditPricePerPage,
    int CreditMinPurchase);

public record OcrCreditPurchaseResponse(
    Guid Id,
    Guid CompanyId,
    int PagesPurchased,
    int PagesRemaining,
    decimal AmountPaid,
    string Status,
    string? PaymentReference,
    DateTime CreatedAt,
    DateTime? ReviewedAt,
    string? ReviewNotes);
