using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    public AiFeedbackController(IAiOrchestrator orchestrator) => _orchestrator = orchestrator;

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
}
