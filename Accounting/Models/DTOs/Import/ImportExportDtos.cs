namespace Accounting.Models.DTOs.Import;

// ===== Import =====
public record ImportRequest(
    string EntityType,
    string FileFormat,
    bool HasHeaderRow,
    List<ColumnMapping>? ColumnMappings,
    List<Dictionary<string, string>> Data);

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
    string? DecimalSeparator);

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

public record ImportableEntityInfo(
    string EntityType,
    string DisplayName,
    string Description,
    List<ImportField> Fields);
