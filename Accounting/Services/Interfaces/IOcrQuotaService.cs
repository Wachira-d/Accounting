using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface IOcrQuotaService
{
    Task<OcrQuotaStatus> GetQuotaStatusAsync(Guid companyId);
    Task<bool> CanScanAsync(Guid companyId);
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
