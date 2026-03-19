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
    Guid? AccumulatedDepreciationAccountId = null);

public record UpdateFixedAssetRequest(
    string? Name,
    string? Description,
    string? Category,
    string? Location,
    string? SerialNumber,
    Guid? AssetAccountId,
    Guid? DepreciationExpenseAccountId,
    Guid? AccumulatedDepreciationAccountId);

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
    DateTime CreatedAt);

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
