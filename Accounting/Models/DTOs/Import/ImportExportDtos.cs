namespace Accounting.Models.DTOs.Import;

// ===== Import =====
public record ImportRequest(
    string EntityType,
    string FileFormat,
    bool HasHeaderRow,
    List<ColumnMapping>? ColumnMappings,
    List<Dictionary<string, string>> Data,
    // Per-row decisions for rows that conflict with existing DB data —
    // key (TaxId/Code/AccountCode) → "Skip"|"Overwrite"|"Merge".
    // Populated by the client after calling /preview-conflicts.
    Dictionary<string, string>? Resolutions = null,
    string? DefaultConflictAction = null);  // fallback for conflicts not in Resolutions

public record ConflictRow(
    string Key,
    string ExistingLabel,
    string IncomingLabel,
    Dictionary<string, string?> Existing,
    Dictionary<string, string?> Incoming,
    List<string> DiffFields);

public record ConflictPreviewResponse(
    string EntityType,
    int TotalRows,
    int NewRowCount,
    int DuplicateExactCount,
    List<ConflictRow> Conflicts,
    List<string> Warnings);

public record ColumnMapping(
    string SourceColumn,
    string TargetField,
    string? DefaultValue,
    string? DateFormat);

public record ImportResult(
    string EntityType,
    int TotalRows,
    int SuccessCount,
    int ErrorCount,
    int SkippedCount,
    List<ImportError> Errors,
    DateTime ImportedAt);

public record ImportError(
    int RowNumber,
    string Field,
    string Value,
    string ErrorMessage);

// ===== Export =====
public record ExportRequest(
    string EntityType,
    string FileFormat,
    DateTime? FromDate = null,
    DateTime? ToDate = null,
    List<string>? Fields = null,
    string? SortBy = null,
    bool SortDesc = false);

public record ExportResult(
    string EntityType,
    string FileFormat,
    int TotalRows,
    string FileName,
    string ContentType,
    byte[] Data);

// ===== Template =====
public record ImportTemplateResponse(
    string EntityType,
    List<ImportField> Fields,
    List<Dictionary<string, string>> SampleData);

public record ImportField(
    string FieldName,
    string DisplayName,
    string DataType,
    bool IsRequired,
    string? Description,
    List<string>? AllowedValues);

// ===== Smart Import (AI Column Matching) =====

public record SmartImportUploadRequest(
    string EntityType,
    string FileName,
    string FileFormat,
    bool HasHeaderRow,
    List<List<string>> RawData);

public record SmartImportSessionResponse(
    Guid SessionId,
    string EntityType,
    string Status,
    List<SmartColumnMappingDto> ColumnMappings,
    int TotalRows,
    int UnmappedColumns,
    bool RequiresManualMapping,
    List<List<string>> PreviewData);

public record SmartColumnMappingDto(
    int SourceIndex,
    string SourceHeader,
    string? TargetField,
    string? TargetDisplayName,
    string MatchType,
    string Confidence,
    double ConfidenceScore,
    List<string> SampleValues,
    List<SuggestedMappingDto> Suggestions);

public record SuggestedMappingDto(
    string TargetField,
    string TargetDisplayName,
    double Score,
    string Reason);

public record ManualMappingRequest(
    Guid SessionId,
    List<ManualColumnMappingEntry> Mappings);

public record ManualColumnMappingEntry(
    int SourceIndex,
    string? TargetField);

public record SmartImportConfirmRequest(
    Guid SessionId,
    string? DateFormat,
    string? DecimalSeparator,
    // Mirror of ImportRequest's conflict-resolution fields. Populated by
    // the client after calling /smart-import/preview-conflicts.
    Dictionary<string, string>? Resolutions = null,
    string? DefaultConflictAction = null);

public record SmartImportResult(
    Guid SessionId,
    string EntityType,
    string Status,
    int TotalRows,
    int SuccessCount,
    int ErrorCount,
    int SkippedCount,
    List<ImportError> Errors,
    DateTime ImportedAt);

public record ImportTemplateDownloadResponse(
    string EntityType,
    string FileName,
    string ContentType,
    byte[] FileData,
    List<ImportField> Fields);

// ===== AI Review (Smart Import) =====
// Returned by POST /smart-import/sessions/{id}/ai-review — one AI call
// after mapping, before commit. Bundles five quality concerns the UI
// can render as a single "review" screen.

public record ImportAiReviewResponse(
    Guid SessionId,
    bool UsedAi,
    string? Summary,
    decimal? OverallQualityScore,
    List<ImportAiNormalizationDto> Normalizations,
    List<ImportAiFuzzyDuplicateDto> FuzzyDuplicates,
    List<ImportAiQualityFlagDto> QualityFlags,
    List<ImportAiFieldValidationDto> FieldValidations,
    List<string> BatchPatterns);

public record ImportAiNormalizationDto(
    int RowIndex, string Field, string? Original, string? Normalized, string? Reason);

public record ImportAiFuzzyDuplicateDto(
    int RowIndex, string? IncomingKey, string? ExistingId, string? ExistingLabel,
    decimal Similarity, string? Reasoning);

public record ImportAiQualityFlagDto(
    int RowIndex, string Severity, string Message);

public record ImportAiFieldValidationDto(
    int RowIndex, string Field, string? Value, string Issue, string? SuggestedFix);

public record ImportableEntityInfo(
    string EntityType,
    string DisplayName,
    string Description,
    List<ImportField> Fields);
