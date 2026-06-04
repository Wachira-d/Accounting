using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Per-tenant endpoint for recording the user's final choice after the
/// UI has shown an AI suggestion. Called by the OCR review modal, the
/// document approval flow, and any other call site that surfaced an
/// AiResponse to the user.
///
/// The FeedbackId comes from AiResponse.FeedbackId returned by the
/// original orchestrator call. Recording the user's choice is what
/// converts a one-shot AI call into a training signal — without this
/// step the LocalModelHealth job sees the row as "unreviewed" and
/// discards it after 7 days.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/ai-feedback")]
[Authorize]
public class AiFeedbackController : ControllerBase
{
    private readonly IAiOrchestrator _orchestrator;
    private readonly Accounting.Data.AccountingDbContext _db;

    public AiFeedbackController(IAiOrchestrator orchestrator, Accounting.Data.AccountingDbContext db)
    { _orchestrator = orchestrator; _db = db; }

    public sealed record RecordChoiceRequest(Guid FeedbackId, string ChosenAnswer, bool AcceptedAi);

    /// <summary>
    /// Records the user's choice. AcceptedAi=true means the user took
    /// the AI's primary answer verbatim. AcceptedAi=false means they
    /// picked an alternative OR typed/picked something different — in
    /// that case ChosenAnswer is the new value (so the local model can
    /// learn "AI said X but the user wanted Y").
    /// </summary>
    [HttpPost("record")]
    public async Task<ActionResult<ApiResponse<object>>> RecordChoice(
        Guid companyId, [FromBody] RecordChoiceRequest req, CancellationToken ct)
    {
        if (req.FeedbackId == Guid.Empty)
            return BadRequest(new ApiResponse<object>(false, null, "FeedbackId ว่าง"));
        if (string.IsNullOrWhiteSpace(req.ChosenAnswer))
            return BadRequest(new ApiResponse<object>(false, null, "ChosenAnswer ห้ามว่าง"));

        await _orchestrator.RecordUserChoiceAsync(req.FeedbackId, req.ChosenAnswer, req.AcceptedAi, ct);
        return Ok(new ApiResponse<object>(true, null, "บันทึก feedback สำเร็จ"));
    }

    /// <summary>Return the full prompt + AI response payload for a feedback
    /// row — used by the UI's '🔍 ดู AI response' button to surface what
    /// DeepSeek actually replied (incl. truncation). Tenant-scoped: a row from
    /// another company returns 404 even if the id is known.</summary>
    [HttpGet("{feedbackId:guid}/raw")]
    public async Task<ActionResult<ApiResponse<object>>> GetRaw(
        Guid companyId, Guid feedbackId, CancellationToken ct)
    {
        var row = await _db.AiSuggestionFeedbacks
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == feedbackId && f.CompanyId == companyId, ct);
        if (row == null)
            return NotFound(new ApiResponse<object>(false, null, "ไม่พบ feedback id (อาจหมดอายุหรือคนละบริษัท)"));

        return Ok(new ApiResponse<object>(true, new
        {
            id = row.Id,
            featureKey = row.FeatureKey,
            status = row.Status.ToString(),
            providerUsed = row.ProviderUsed.ToString(),
            modelVersion = row.ModelVersion,
            latencyMs = row.LatencyMs,
            inputTokens = row.InputTokens,
            outputTokens = row.OutputTokens,
            costUsd = row.CostUsd,
            errorMessage = row.ErrorMessage,
            aiPrimaryAnswer = row.AiPrimaryAnswer,
            aiConfidence = row.AiConfidence,
            promptJson = row.PromptJson,           // already JSON-typed
            responseJson = row.ResponseJson,       // ditto (or {"raw":"..."} wrapped)
            localModelAnswer = row.LocalModelAnswer,
            localModelConfidence = row.LocalModelConfidence,
            localModelVersion = row.LocalModelVersion,
            createdAt = row.CreatedAt,
        }));
    }
}
