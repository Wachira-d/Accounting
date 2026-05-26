namespace Accounting.Models.DTOs.Ocr;

/// <summary>Where an OCR'd line item should be routed when the user
/// confirms the import. Determines which entity gets created and which
/// downstream flow processes it afterwards.</summary>
public enum OcrImportDestination
{
    /// <summary>ProductType.Product with TrackStock = true. Posts an
    /// "IN" StockMovement. Drawn down through normal sales/POS.</summary>
    Stock = 0,
    /// <summary>ProductType.Supplies. Posts "IN" to supplies inventory;
    /// drawn down via the /supplies "เบิกใช้" page.</summary>
    Supplies = 1,
    /// <summary>Capitalized into a FixedAsset row with depreciation
    /// schedule + initial Dr Asset / Cr AP journal entry.</summary>
    FixedAsset = 2
}

/// <summary>One match candidate for a single OCR line. The UI shows the
/// top-1 as the pre-selected choice and the rest as alternatives in the
/// dropdown ("did you mean…?"). Confidence 0..1, higher = better.</summary>
public record ProductMatchCandidate(
    Guid ProductId,
    string Code,
    string Name,
    string Unit,
    decimal CostPrice,
    decimal CurrentStock,
    double Confidence,
    string Reason);       // "exact-alias" | "vendor-alias" | "code-hit" | "trigram" | "fuzzy"

/// <summary>One row in the import preview — pairs an OCR'd line with the
/// system's best guess at which Product it represents, plus alternatives
/// and a parsed-out unit / unit-cost. UI lets the user confirm or
/// override before committing the actual stock receipt.</summary>
public record OcrStockPreviewLine(
    int LineIndex,
    string Description,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? Amount,
    string? DetectedUnit,                   // e.g. "ลิตร" parsed from "1 ลิตร"
    decimal? DetectedQuantity,              // if the OCR Quantity was null but description has "1 ลิตร"
    ProductMatchCandidate? BestMatch,
    List<ProductMatchCandidate> Alternatives,
    bool WillCreateNew,                     // true when no candidate above threshold
    // ── Price sanity check ───────────────────────────────────────────
    // When the matched product's CostPrice on file differs from the OCR'd
    // UnitPrice by more than ±30%, surface a warning so the user notices
    // they may have matched the wrong SKU/size. Null when no match or no
    // baseline to compare against.
    bool PriceAnomaly = false,
    decimal? ExpectedUnitCost = null,
    string? PriceAnomalyHint = null,
    // ── Unit-conversion auto-fill ────────────────────────────────────
    // When OCR'd unit is "1 ลัง" but the matched product Unit is "ชิ้น"
    // and a UnitConversion (ลัง → ชิ้น × 12) is on file, we pre-populate
    // ConvertedQuantity = 12 + ConvertedUnit = "ชิ้น" so the import-stock
    // call posts in the canonical base unit. The user sees both numbers
    // side by side and can untick if they actually want to keep "ลัง".
    decimal? ConvertedQuantity = null,
    string? ConvertedUnit = null,
    decimal? ConversionRate = null,
    string? ConversionHint = null,
    // ── Global federated knowledge suggestion ───────────────────────
    // Populated when the cross-tenant pool has an ACTIVE pattern for
    // this wording. UI uses these to pre-fill the "create new product"
    // form with canonical label / unit / category and shows a 🌐 badge
    // explaining the source. Null when no global consensus yet exists.
    GlobalProductSuggestion? GlobalSuggestion = null,
    // ── Fixed-asset detection hints ───────────────────────────────────
    // Populated by FixedAssetDetector + GlobalAssetCategoryLearner.
    // UI uses these to:
    //   • Pre-select the Asset destination on the row when IsLikelyAsset
    //   • Pre-fill Category + UsefulLifeMonths when user expands the
    //     asset panel
    bool IsLikelyAsset = false,
    string? SuggestedAssetCategory = null,
    int? SuggestedUsefulLifeMonths = null,
    double? AssetConfidence = null,
    List<string>? AssetReasons = null,
    OcrImportDestination DefaultDestination = OcrImportDestination.Stock,
    GlobalAssetSuggestion? GlobalAssetSuggestion = null);

public record GlobalProductSuggestion(
    string? CanonicalLabel,
    string? Brand,
    string? Unit,
    string? CategoryHint,
    int TenantCount,
    int TotalConfirms);

public record GlobalAssetSuggestion(
    string Category,
    int UsefulLifeMonths,
    string? DepreciationMethod,
    int TenantCount,
    int TotalConfirms);

public record OcrStockPreviewResponse(
    Guid ScanId,
    Guid? VendorContactId,
    string? VendorName,
    List<OcrStockPreviewLine> Lines);

/// <summary>One line of the import payload. Either bind to an existing
/// product (ProductId set) or have the server auto-create a new one
/// (ProductId null + NewProductName set). Quantity + UnitCost are what
/// the user confirmed in the modal — pre-filled but editable.</summary>
public record OcrStockImportLineRequest(
    int LineIndex,
    Guid? ProductId,
    string? NewProductName,
    string? NewProductCode,        // optional; auto-generated when null
    string? NewProductCategory,
    decimal Quantity,
    string Unit,
    decimal UnitCost,
    decimal? VatRate,
    // Legacy fast-path: same effect as Destination = Supplies when this
    // is true and Destination is the default Stock. Kept for back-
    // compat with the toggle that shipped before per-row destinations.
    bool AsSupplies = false,
    // ─── Per-row routing (NEW) ──────────────────────────────────────
    // Each OCR'd line can now go to one of three destinations. The
    // preview pre-selects FixedAsset when the FixedAssetDetector
    // flagged the line; otherwise Stock unless AsSupplies = true.
    OcrImportDestination Destination = OcrImportDestination.Stock,
    // ─── FixedAsset-only fields (ignored when Destination != FixedAsset) ──
    // PurchaseDate comes from the scan's ExtractedDate (no extra field
    // needed). Asset accounts default to the company's standard asset
    // / depreciation / accumulated-dep accounts unless overridden.
    string? AssetCode = null,            // auto-generated when null
    string? AssetCategory = null,
    int? UsefulLifeMonths = null,
    decimal? SalvageValue = null,
    string? SerialNumber = null,
    string? DepreciationMethod = null,   // "StraightLine" | "DecliningBalance" | "DoubleDecliningBalance"
    Guid? AssetAccountId = null,
    Guid? DepreciationExpenseAccountId = null,
    Guid? AccumulatedDepreciationAccountId = null);

public record OcrRejectMatchRequest(
    string OcrDescription,
    Guid RejectedProductId,
    string? Reason = null);

public record OcrStockImportRequest(
    List<OcrStockImportLineRequest> Lines,
    // When true (default), the OCR'd description for each line is saved
    // as a ProductAlias bound to the scan's vendor (Contact). Future OCR
    // scans from the same vendor instantly match this product on the
    // same description. Disable only for one-off imports.
    bool LearnAliases = true,
    // When true (default), creates a StockMovement row of type "IN" with
    // unit cost = UnitCost. Off for "match-only" runs that just want to
    // register the alias without actually moving stock.
    bool MoveStock = true);

public record OcrStockImportResult(
    int LinesProcessed,
    int ProductsCreated,
    int ProductsMatched,
    int StockMovementsCreated,
    int AliasesLearned,
    int FixedAssetsCreated,
    List<OcrStockImportLineResult> LineResults);

public record OcrStockImportLineResult(
    int LineIndex,
    Guid? ProductId,       // null when this line was routed to FixedAsset
    string ProductCode,
    string ProductName,
    decimal QuantityIn,
    decimal NewStockBalance,
    bool WasCreated,
    bool AliasLearned,
    string? ErrorMessage,
    OcrImportDestination Destination = OcrImportDestination.Stock,
    Guid? FixedAssetId = null);
