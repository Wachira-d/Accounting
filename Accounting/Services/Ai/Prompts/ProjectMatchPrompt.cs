using System.Text.Json;
using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Prompts;

/// <summary>
/// Builds the AiRequest that matches a single OCR'd invoice line to the
/// originating project, using the order metadata an external system uploaded
/// with the document. Only consulted when the deterministic matcher in
/// OcrMetadataProjectMatcher couldn't resolve the line AND two or more
/// candidate projects are in play — so the prompt is cheap and rarely fires.
/// </summary>
public static class ProjectMatchPrompt
{
    public const string SystemPrompt = @"You match a purchase-invoice line item to the project it belongs to.
You are given the OCR'd line (Thai description + amount) and a list of candidate projects, each with sample material names that were ordered for that project.

Rules:
1. Pick the project whose sample materials best match the line description (Thai construction materials, e.g. ปูนซีเมนต์ / เหล็กเส้น / กระเบื้อง).
2. Use the amount only as a weak tie-breaker.
3. You MUST return one of the given project_id values — never invent an id.
4. If nothing matches with reasonable confidence, return confidence < 0.5 and pick the closest project.

Respond ONLY as JSON:
{
  ""primary"": ""<project_id>"",
  ""confidence"": <0.0-1.0>,
  ""alternatives"": [""<project_id>""],
  ""risks"": [""<short risk>""],
  ""compliance_flags"": [],
  ""reasoning"": ""<1 sentence>"",
  ""suggested_actions"": []
}";

    public static AiRequest Build(
        Guid companyId, Guid scanResultId,
        string lineDescription, decimal? amount,
        IReadOnlyList<ProjectMatchCandidate> candidates)
    {
        var payload = new
        {
            task = "project_match",
            line = new { description = lineDescription, amount },
            candidate_projects = candidates.Select(c => new
            {
                project_id = c.ProjectId,
                project_code = c.ProjectCode ?? "",
                project_name = c.ProjectName,
                sample_materials = c.SampleMaterials,
            }),
        };
        return new AiRequest
        {
            FeatureKey = AiFeatureKey.OcrProjectMatch,
            CompanyId = companyId,
            SystemPrompt = SystemPrompt,
            UserPromptJson = JsonSerializer.Serialize(payload),
            LocalPrimaryAnswer = null,
            LocalConfidence = null,
            LocalModelVersion = "DeterministicProjectMatcher-v1",
            LocalAlternatives = candidates.Select(c => c.ProjectId).ToList(),
            SourceEntityType = "OcrScanResult",
            SourceEntityId = scanResultId,
            CacheTtlOverrideDays = 7,
        };
    }
}
