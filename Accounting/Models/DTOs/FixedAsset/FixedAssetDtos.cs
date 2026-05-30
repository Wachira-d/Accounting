using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.FixedAsset;

public record CreateFixedAssetRequest(
    string AssetCode,
    string Name,
    string? Description,
    string? Category,
    string? Location,
    string? SerialNumber,
    DateTime PurchaseDate,
    decimal PurchaseCost,
    decimal SalvageValue,
    int UsefulLifeMonths,
    DepreciationMethod DepreciationMethod = DepreciationMethod.StraightLine,
    Guid? AssetAccountId = null,
    Guid? DepreciationExpenseAccountId = null,
    Guid? AccumulatedDepreciationAccountId = null,
    // When true, CreateAsync posts an acquisition entry Dr Asset / Cr
    // CreditAccountId (cash or A/P). The OCR "Register Asset" path posts
    // its own entry and leaves this false to avoid a double post.
    bool PostAcquisitionJournalEntry = false,
    Guid? CreditAccountId = null,
    // Asset type + lease-specific fields. Default Tangible keeps the create
    // flow exactly as it was for existing callers.
    AssetType AssetType = AssetType.Tangible,
    int? LeaseTermMonths = null,
    string? LessorName = null,
    decimal? MonthlyLeasePayment = null,
    Guid? LeaseLiabilityAccountId = null,
    /// <summary>Optional project the asset was acquired for. Periodic
    /// depreciation JEs will inherit this so depreciation cost lands
    /// in the right project's P&amp;L automatically.</summary>
    Guid? ProjectId = null);

public record UpdateFixedAssetRequest(
    string? Name,
    string? Description,
    string? Category,
    string? Location,
    string? SerialNumber,
    Guid? AssetAccountId,
    Guid? DepreciationExpenseAccountId,
    Guid? AccumulatedDepreciationAccountId,
    Guid? ProjectId = null);

public record FixedAssetResponse(
    Guid Id,
    string AssetCode,
    string Name,
    string? Description,
    string? Category,
    string? Location,
    string? SerialNumber,
    DateTime PurchaseDate,
    decimal PurchaseCost,
    decimal SalvageValue,
    int UsefulLifeMonths,
    DepreciationMethod DepreciationMethod,
    decimal AccumulatedDepreciation,
    decimal NetBookValue,
    AssetStatus Status,
    DateTime? DisposalDate,
    decimal? DisposalAmount,
    DateTime CreatedAt,
    Guid? AssetAccountId = null,
    Guid? DepreciationExpenseAccountId = null,
    Guid? AccumulatedDepreciationAccountId = null,
    AssetType AssetType = AssetType.Tangible,
    int? LeaseTermMonths = null,
    string? LessorName = null,
    decimal? MonthlyLeasePayment = null,
    Guid? ProjectId = null);

public record DepreciationResponse(
    Guid Id,
    Guid FixedAssetId,
    int Year,
    int Month,
    decimal Amount,
    decimal AccumulatedAmount,
    decimal NetBookValue,
    bool IsPosted);

public record DisposeAssetRequest(
    DateTime DisposalDate,
    decimal DisposalAmount);

public record CalculateDepreciationRequest(
    int Year,
    int Month);

public record RevalueAssetRequest(
    decimal NewFairValue,
    DateTime RevaluationDate,
    string? Notes);

public record RevaluationResponse(
    Guid AssetId,
    string AssetCode,
    string AssetName,
    decimal OldNetBookValue,
    decimal NewFairValue,
    decimal RevaluationSurplus,
    DateTime RevaluationDate);

public record WriteOffAssetRequest(
    DateTime WriteOffDate,
    string? Reason);

public record AdjustUsefulLifeRequest(
    int NewUsefulLifeMonths,
    decimal? NewSalvageValue);

public record AssetCategoryResponse(
    string Category,
    int AssetCount,
    decimal TotalCost,
    decimal TotalNetBookValue);

public record AssetRegisterReportItem(
    string AssetCode,
    string Name,
    string? Category,
    string? Location,
    DateTime PurchaseDate,
    decimal PurchaseCost,
    decimal SalvageValue,
    int UsefulLifeMonths,
    string DepreciationMethod,
    decimal AccumulatedDepreciation,
    decimal NetBookValue,
    string Status,
    DateTime? DisposalDate,
    decimal? DisposalAmount);

public record AssetRegisterReport(
    DateTime ReportDate,
    List<AssetRegisterReportItem> Items,
    decimal TotalCost,
    decimal TotalAccumulatedDepreciation,
    decimal TotalNetBookValue,
    int TotalActive,
    int TotalDisposed,
    int TotalFullyDepreciated);

public record DepreciationScheduleItem(
    int Year,
    int Month,
    decimal OpeningNBV,
    decimal DepreciationAmount,
    decimal AccumulatedDepreciation,
    decimal ClosingNBV);

public record DepreciationScheduleReport(
    Guid AssetId,
    string AssetCode,
    string AssetName,
    decimal PurchaseCost,
    decimal SalvageValue,
    int UsefulLifeMonths,
    string DepreciationMethod,
    List<DepreciationScheduleItem> Schedule);

public record ImportFixedAssetRow(
    string AssetCode,
    string Name,
    string? Category,
    string? Location,
    string? SerialNumber,
    DateTime PurchaseDate,
    decimal PurchaseCost,
    decimal SalvageValue,
    int UsefulLifeMonths,
    string DepreciationMethod);

public record ImportFixedAssetsResult(
    int TotalRows,
    int SuccessCount,
    int ErrorCount,
    List<string> Errors);
