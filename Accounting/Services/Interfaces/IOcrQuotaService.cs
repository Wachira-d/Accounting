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

    /// <summary>
    /// Checks whether the tenant's plan still has Azure DI budget left
    /// this month. False means the cascade should skip Tier-1 Azure and
    /// route directly to local OCR (when fallback is enabled).
    /// Returns true when:
    ///   • Plan has no Azure-specific quota (legacy single-budget mode), OR
    ///   • Plan has Azure quota AND CurrentMonthAzureOcrPages &lt; budget.
    /// </summary>
    Task<bool> CanUseAzureAsync(Guid companyId);

    /// <summary>
    /// Same gate as CanUseAzureAsync but also returns a human-readable Thai
    /// reason when blocked — so the cascade can surface "plan ไม่ให้ Azure"
    /// vs "quota หมด" to the admin via ProcessingNotes.
    /// </summary>
    Task<(bool Allowed, string? Reason)> CheckAzureQuotaAsync(Guid companyId);

    /// <summary>
    /// Atomically check + reserve one page from the engine-specific
    /// counter. Returns false when the engine-specific quota is hit
    /// (caller may then route to a different engine).
    /// engineKind: "Azure" | "Local" | "TextLayer".
    /// </summary>
    Task<bool> TryConsumeForEngineAsync(Guid companyId, string engineKind);

    /// <summary>
    /// Post-hoc tracking — increment the engine-specific counter after
    /// scan completes. Used when the cascade routed via TryConsumeAsync
    /// (legacy total budget) and we need to record which engine the
    /// page actually went to.
    /// </summary>
    Task RecordEngineUsageAsync(Guid companyId, string engineKind);

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
    int CreditMinPurchase,
    // ─── Per-engine breakdown (populated from the Subscription's
    //     AzureOcrPagesPerMonth / LocalOcrPagesPerMonth, which were
    //     copied in from PlanTemplate at subscription create time).
    //     Null on legacy single-budget plans = "uses MaxPagesPerMonth".
    int? AzureMaxPagesPerMonth = null,
    int? AzureUsedThisMonth = null,
    int? LocalMaxPagesPerMonth = null,
    int? LocalUsedThisMonth = null,
    bool FallbackToLocalWhenAzureExhausted = true,
    string? PlanName = null);

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
