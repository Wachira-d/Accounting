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
