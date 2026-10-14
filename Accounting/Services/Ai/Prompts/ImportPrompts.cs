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

    /// <summary>
    /// คำขอจับคู่คอลัมน์ (ImportColumnMatch · 26) — รอบ 205 ทีม A: <c>ForceProviderCall = false</c> (เดิม true = ข้าม routing + ด่านงบทุกครั้ง
    /// และนักเรียนไม่มีวันได้ตอบ) ⇒ orchestrator ถามนักเรียน <c>ImportColumnMatchDistillationModel</c> ก่อน (Hybrid ≥ 0.85 ไม่เรียก AI) ·
    /// <paramref name="sessionId"/> null = ไม่มี batch (endpoint แนะนำการจับคู่)
    /// </summary>
    public static AiRequest BuildColumnMatch(
        Guid companyId,
        Guid? sessionId,
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
                // ฝ่ายค้านรอบ ETL (PDPA ม.26): ค่าตัวอย่างส่งแค่ "รูป" ของเลขบัตร/บัญชี/เงินเดือน — ตัวตั้งเดียว Helpers/ImportPiiFields
                sample_values = c.SampleValues.Select(v => Accounting.Helpers.ImportPiiFields.RedactForPrompt(entityType, null, v)).ToList(),
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
            LocalModelVersion = "ImportColumnMapper-v1",
            SourceEntityType = sessionId == null ? null : "SmartImportSession",
            SourceEntityId = sessionId,
            CacheTtlOverrideDays = 7,   // mappings stable across similar files; safe to cache
            ForceProviderCall = false,
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
        IReadOnlyList<ExistingEntityRef> existingSlice,
        int? totalRowCount = null)
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
            // จำนวนแถวจริงของไฟล์ (ไม่ใช่ sample) — batch_patterns เช่น "ทุก 247
            // แถวมาจาก supplier เดียวกัน" ต้องอ้างจำนวนจริง ไม่ใช่ 50 ที่เห็น
            total_row_count = totalRowCount ?? sampleRows.Count,
            sample_row_count = sampleRows.Count,
            rows = sampleRows.Select((r, i) => new
            {
                row_index = i,
                // ฝ่ายค้านรอบ ETL (PDPA ม.26): ช่องอ่อนไหวส่งแค่รูปค่า (Helpers/ImportPiiFields)
                fields = r.ToDictionary(kv => kv.Key, kv => Accounting.Helpers.ImportPiiFields.RedactForPrompt(entityType, kv.Key, kv.Value)),
            }),
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
            // รอบ 205 ทีม A: false — ให้ routing/ด่านงบทำงาน + นักเรียน ImportDataReviewDistillationModel ตอบเมื่อ AI ปิด (กฎเหล็ก #1)
            ForceProviderCall = false,
            RawPlanResponse = true,
            MaxTokensOverride = 6000,      // five concerns × up to 200 rows = long
            TimeoutSecondsOverride = 60,
        };
    }

    /// <summary>schema ของศูนย์นำเข้า (<see cref="Models.DTOs.Import.ImportFieldDto"/>) → รูป target ของ prompt — ชนิดส่งเป็นชื่อ
    /// <see cref="ImportFieldType"/> (นักเรียนอ่านกลับด้วย <c>ImportSchemas.ParseFieldType</c> ตัวเดียว)</summary>
    public static IReadOnlyList<TargetField> ToTargetFields(IEnumerable<Models.DTOs.Import.ImportFieldDto> fields)
        => fields.Select(f => new TargetField(f.Field, f.Label, f.Type.ToString(), f.Required, f.Description)).ToList();

    // ═══════════════════════ รอบ 205 ทีม A — คำตอบเดี่ยว (นักเรียน = GenericFeedbackDistillationModel) ═══════════════════════

    private const string AccountMapSystem = @"You map ONE account from a Thai company's legacy chart of accounts / trial balance (exported from another accounting program)
to the matching account in the company's current chart of accounts.

Rules:
1. Answer with exactly one ""code"" from candidate_accounts. Never invent a code.
2. Use the legacy code's leading digit as the account class hint (Thai convention: 1 asset, 2 liability, 3 equity, 4 revenue, 5 expense)
   but the account NAME meaning wins (e.g. ""เงินฝากธนาคาร กสิกร"" → a bank deposit asset account).
3. If nothing fits, return the closest candidate with confidence below 0.5.

Respond ONLY as JSON:
{
  ""primary"": ""<code>"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<code>""],
  ""risks"": [],
  ""compliance_flags"": [],
  ""reasoning"": ""<1 short Thai sentence>"",
  ""suggested_actions"": []
}";

    /// <summary>ผังบัญชีหนึ่งตัวที่ส่งให้ AI เลือก (ผู้เรียกส่ง top-K ที่ชั้น local คัดมา — ไม่ส่งผังทั้งบริษัท)</summary>
    public sealed record AccountCandidate(string Code, string Name, string? Type);

    /// <summary>
    /// คำขอจับคู่ผังบัญชีของแถวนำเข้า (<see cref="AiFeatureKey.ImportAccountMap"/> · 58) — ผู้เรียก: <c>ImportAiAugmenter.SuggestAccountMapAsync</c>
    /// (ด่าน: คำตอบ ∈ รหัสผังบริษัท) · <paramref name="localCode"/> = คำตอบของ <c>ImportAccountCodeResolver</c> (ทีม D) ⇒ AI ล่ม/ปิด ยังมีคำตอบ
    /// </summary>
    public static AiRequest BuildAccountMap(Guid companyId, Guid? batchId, string? sourceCode, string? sourceName, string? hint,
        IReadOnlyList<AccountCandidate> candidates, string? localCode, decimal? localConfidence)
    {
        var payload = new
        {
            task = "import_account_map",
            legacy = new { code = sourceCode?.Trim(), name = sourceName?.Trim(), hint = hint?.Trim() },
            candidate_accounts = candidates.Select(c => new { code = c.Code, name = c.Name, type = c.Type }),
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.ImportAccountMap,
            CompanyId = companyId,
            SystemPrompt = AccountMapSystem,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = localCode,
            LocalConfidence = localConfidence,
            LocalModelVersion = "ImportAccountCodeResolver",
            LocalAlternatives = candidates.Select(c => c.Code).ToList(),
            SourceEntityType = batchId == null ? null : "SmartImportSession",
            SourceEntityId = batchId,
            CacheTtlOverrideDays = 30,
            TemperatureOverride = 0m,
        };
    }

    private const string EntityMatchSystem = @"You decide whether ONE row from an import file (contacts / products from another Thai accounting program) refers to an
EXISTING record of the company, or is NEW.

Rules:
1. Answer with exactly one ""id"" from candidates, or the literal ""NEW"" when none of them is the same real-world party/product.
2. Thai company names: ""บจก."" = ""บริษัท … จำกัด"", ""หจก."" = ""ห้างหุ้นส่วนจำกัด"", ""บมจ."" = ""บริษัท … จำกัด (มหาชน)"" — legal-form prefixes alone do not make two names different,
   but a different branch, different person name or different product size/model DOES.
3. Similar-sounding but different businesses must be ""NEW"". When unsure, answer ""NEW"" with confidence below 0.6 — a wrong merge is worse than a duplicate.

Respond ONLY as JSON:
{
  ""primary"": ""<id or NEW>"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<id or NEW>""],
  ""risks"": [],
  ""compliance_flags"": [],
  ""reasoning"": ""<1 short Thai sentence>"",
  ""suggested_actions"": []
}";

    /// <summary>ข้อมูลเดิมหนึ่งตัวที่อาจเป็นตัวเดียวกับแถว (ผู้เรียกคัดด้วยชั้น local — เลขภาษี/ชื่อตามกฎหมาย/n-gram — เหลือ ≤ 5)</summary>
    public sealed record EntityCandidate(string Id, string Label, string? Key, decimal LocalScore);

    /// <summary>
    /// คำขอจับคู่แถวนำเข้ากับข้อมูลเดิม (<see cref="AiFeatureKey.ImportEntityMatch"/> · 59) — ใช้เฉพาะแถวกำกวม (top-2 ห่าง &lt; 0.05 และไม่มีเลขภาษี ·
    /// IMPORT_ETL_REVIEW §2.4) · ผู้เรียก: <c>ImportAiAugmenter.ResolveEntityAsync</c> (ด่าน: ∈ candidate ids ∪ {"NEW"}) ·
    /// <paramref name="localAnswer"/> = id ที่ชั้น local เลือก หรือ "NEW"
    /// </summary>
    public static AiRequest BuildEntityMatch(Guid companyId, Guid? batchId, string entityType, string rowLabel, string? rowDetail,
        IReadOnlyList<EntityCandidate> candidates, string? localAnswer, decimal? localConfidence)
    {
        var payload = new
        {
            task = "import_entity_match",
            entity_type = entityType,
            row = new { label = rowLabel.Trim(), detail = rowDetail?.Trim() },
            candidates = candidates.Select(c => new { id = c.Id, label = c.Label, key = c.Key, local_score = c.LocalScore }),
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.ImportEntityMatch,
            CompanyId = companyId,
            SystemPrompt = EntityMatchSystem,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = localAnswer,
            LocalConfidence = localConfidence,
            LocalModelVersion = "ImportEntityResolver",
            LocalAlternatives = candidates.Select(c => c.Id).Append(EntityMatchNew).ToList(),
            SourceEntityType = batchId == null ? null : "SmartImportSession",
            SourceEntityId = batchId,
            CacheTtlOverrideDays = 7,
            TemperatureOverride = 0m,
        };
    }

    /// <summary>คำตอบ "เป็นรายการใหม่" ของ ImportEntityMatch</summary>
    public const string EntityMatchNew = "NEW";
}
