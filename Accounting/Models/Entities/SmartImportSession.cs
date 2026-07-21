using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

public class SmartImportSession : TenantEntity
{
    public string EntityType { get; set; } = null!;
    public string FileName { get; set; } = null!;
    public string FileFormat { get; set; } = "csv";
    public bool HasHeaderRow { get; set; } = true;
    public ImportSessionStatus Status { get; set; } = ImportSessionStatus.Uploading;
    public int TotalRows { get; set; }
    public int SuccessCount { get; set; }
    public int ErrorCount { get; set; }
    public int SkippedCount { get; set; }
    public string? RawDataJson { get; set; }
    public string? ErrorsJson { get; set; }
    public DateTime? CompletedAt { get; set; }

    public ICollection<SmartImportColumnMapping> ColumnMappings { get; set; } = new List<SmartImportColumnMapping>();
}

public class SmartImportColumnMapping : BaseEntity
{
    public Guid SessionId { get; set; }
    public int SourceIndex { get; set; }
    public string SourceHeader { get; set; } = null!;
    public string? TargetField { get; set; }
    public ColumnMatchType MatchType { get; set; } = ColumnMatchType.Unmapped;
    public ColumnMatchConfidence Confidence { get; set; } = ColumnMatchConfidence.None;
    public double ConfidenceScore { get; set; }
    public string? SampleValuesJson { get; set; }
    public string? SuggestionsJson { get; set; }

    public SmartImportSession Session { get; set; } = null!;
}
