using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// Two AI prompts that augment the otherwise 100%-heuristic import pipeline.
///
///   <c>BuildColumnMatch</c> — runs at upload time, only when the
///   heuristic AnalyzeColumnMatch flagged at least one column with
///   confidence &lt; 0.7. AI re-reads source headers + sample values and
///   re-ranks the candidate target fields. Particularly helpful for
///   files from PEAK / Express / FlowAccount where column names are
///   Thai abbreviations the alias table doesn't cover.
///
///   <c>BuildDataReview</c> — runs once between "save mapping" and
///   "confirm import". Single AI call returns FIVE concerns at once:
///   value normalizations, fuzzy-dup suggestions, per-row quality
///   flags, semantic field validation, batch-level patterns. The five
///   problems share input context (sample rows + target schema + a
///   slice of existing entities) so one prompt is cheaper and more
///   coherent than five separate calls.
///
/// Both prompts use <c>RawPlanResponse = true</c> so the orchestrator
/// hands the raw JSON back without trying to coerce it into the
/// primaryAnswer/alternatives shape that vendor-canon / bank-match use.
/// </summary>
public static class ImportPrompts
{
    private const string ColumnMatchSystem = @"You are a Thai accounting data-import expert. The user uploaded a CSV/Excel from another system (could be PEAK, Express, FlowAccount, or hand-rolled Excel). The heuristic matcher couldn't confidently map some columns to our schema.

For EACH source column you receive, pick the best target_field from the candidate list (or null if none fit). Consider:
- Source header semantics (Thai + English + transliterations)
- Sample values: do they LOOK like the candidate field's data type?
- Common renames: ""ลูกค้า/ผู้ขาย"" → Name, ""เลขประจำตัวผู้เสียภาษี"" → TaxId, ""ที่อยู่ติดต่อ"" → Address
- Don't force a match — if a source column looks like internal metadata (row #, system ID, color flag), return null

Strict JSON output (NO prose outside JSON):
{
  ""mappings"": [
    {
      ""sourceIndex"": <int>,
      ""targetField"": ""<fieldName or null>"",
      ""confidence"": <0.0-1.0>,
      ""reasoning"": ""<short Thai, max 60 chars>""
    }
  ]
}";

    private const string DataReviewSystem = @"You are a Thai accounting data-import quality reviewer. Before this batch is committed to the database, find problems the heuristic pipeline can't catch.

Return FIVE concerns in one JSON object:

1. normalizations[]: per-cell value corrections. Examples:
   - Buddhist year ""2568"" → western ""2025""
   - European decimal ""1.234,50"" → ""1234.50""
   - Thai bool ""ใช่/y/yes/✓/1"" → ""true"", ""ไม่/n/no/✗/0"" → ""false""
   - Whitespace-only cells → null
   - Date format reconciliation (""15/3/68"" vs ""2025-03-15"")

2. fuzzy_duplicates[]: rows whose key doesn't exact-match an existing entity but PROBABLY refers to it.
   Example: incoming Name=""บจก.ABC"" with no TaxId vs existing Name=""บริษัท เอบีซี จำกัด"" with TaxId=""0105...""
   Mark only when similarity is high enough to warrant operator review (>= 0.8).

3. quality_flags[]: per-row anomalies that aren't fatal but warrant attention.
   Examples: amount 10× the entity's typical range; name is just initials (""K.""); TaxId checksum invalid; email domain looks fake.

4. field_validation[]: per-field semantic issues.
   Examples: TaxId fails Thai mod-11 checksum; Email missing @ or invalid TLD; Phone has < 9 digits; AccountCode doesn't match the entity's known chart-of-accounts pattern.

5. batch_patterns[]: file-wide observations.
   Examples: ""all 247 rows are from the same supplier — likely a price list""; ""dates span 3 years but file is named 'jan-2025'""; ""contact addresses cluster in one province — regional dump""; ""appears to be a re-upload of a file imported 3 days ago"".

Strict JSON output (NO prose outside JSON). Empty arrays are valid:
{
  ""normalizations"": [
    { ""rowIndex"": <int, 0-based ignoring header>, ""field"": ""<targetField>"", ""original"": ""<original value>"", ""normalized"": ""<corrected value>"", ""reason"": ""<short Thai>"" }
  ],
  ""fuzzy_duplicates"": [
    { ""rowIndex"": <int>, ""incomingKey"": ""<value identifying the row>"", ""existingId"": ""<guid>"", ""existingLabel"": ""<existing entity name>"", ""similarity"": <0.0-1.0>, ""reasoning"": ""<short Thai>"" }
  ],
  ""quality_flags"": [
    { ""rowIndex"": <int>, ""severity"": ""info|warning|error"", ""message"": ""<short Thai>"" }
  ],
  ""field_validation"": [
    { ""rowIndex"": <int>, ""field"": ""<targetField>"", ""value"": ""<bad value>"", ""issue"": ""<short Thai>"", ""suggestedFix"": ""<short Thai or null>"" }
  ],
  ""batch_patterns"": [""<short Thai observation>""],
  ""overall_quality_score"": <0.0-1.0>,
  ""summary"": ""<2-3 sentence Thai summary for the operator>""
}";

    public sealed record ColumnMatchInput(
        int SourceIndex,
        string SourceHeader,
        IReadOnlyList<string> SampleValues,
        string? HeuristicGuess,
        double HeuristicScore);

    public sealed record TargetField(
        string FieldName,
        string DisplayName,
        string DataType,
        bool IsRequired,
        string? Description);

    public static AiRequest BuildColumnMatch(
        Guid companyId,
        Guid sessionId,
        string entityType,
        IReadOnlyList<TargetField> targets,
        IReadOnlyList<ColumnMatchInput> uncertainColumns)
    {
        var payload = new
        {
            task = "import_column_match",
            entity_type = entityType,
            target_fields = targets.Select(f => new
            {
                field_name = f.FieldName,
                display_name = f.DisplayName,
                data_type = f.DataType,
                is_required = f.IsRequired,
                description = f.Description,
            }),
            columns = uncertainColumns.Select(c => new
            {
                source_index = c.SourceIndex,
                source_header = c.SourceHeader,
                sample_values = c.SampleValues,
                heuristic_guess = c.HeuristicGuess,
                heuristic_score = c.HeuristicScore,
            }),
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.ImportColumnMatch,
            CompanyId = companyId,
            SystemPrompt = ColumnMatchSystem,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = null,
            LocalConfidence = null,
            LocalModelVersion = "ImportHeuristic-v1",
            SourceEntityType = "SmartImportSession",
            SourceEntityId = sessionId,
            CacheTtlOverrideDays = 7,   // mappings stable across similar files; safe to cache
            ForceProviderCall = true,
            RawPlanResponse = true,
            MaxTokensOverride = 2000,
            TimeoutSecondsOverride = 30,
        };
    }

    public sealed record ExistingEntityRef(
        string Id,
        string Label,
        string? Key);

    public static AiRequest BuildDataReview(
        Guid companyId,
        Guid sessionId,
        string entityType,
        IReadOnlyList<TargetField> targets,
        IReadOnlyList<string> mappedColumnOrder,
        IReadOnlyList<Dictionary<string, string?>> sampleRows,
        IReadOnlyList<ExistingEntityRef> existingSlice)
    {
        var payload = new
        {
            task = "import_data_review",
            entity_type = entityType,
            mapped_columns = mappedColumnOrder,
            target_field_specs = targets.Select(f => new
            {
                field_name = f.FieldName,
                display_name = f.DisplayName,
                data_type = f.DataType,
                is_required = f.IsRequired,
            }),
            row_count = sampleRows.Count,
            rows = sampleRows.Select((r, i) => new { row_index = i, fields = r }),
            existing_entities_sample = existingSlice.Select(e => new
            {
                id = e.Id,
                label = e.Label,
                key = e.Key,
            }),
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.ImportDataReview,
            CompanyId = companyId,
            SystemPrompt = DataReviewSystem,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = null,
            LocalConfidence = null,
            LocalModelVersion = "ImportHeuristic-v1",
            SourceEntityType = "SmartImportSession",
            SourceEntityId = sessionId,
            BypassCache = true,            // every import is unique data
            CacheTtlOverrideDays = 0,
            ForceProviderCall = true,
            RawPlanResponse = true,
            MaxTokensOverride = 6000,      // five concerns × up to 200 rows = long
            TimeoutSecondsOverride = 60,
        };
    }
}
