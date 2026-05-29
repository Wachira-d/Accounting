using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>Migration wizard endpoints — drive the multi-step UI for
/// COA mapping / trial balance import / sub-ledger validation.</summary>
[ApiController]
[Route("api/companies/{companyId:guid}/migration")]
[Authorize]
public class MigrationWizardController : ControllerBase
{
    private readonly IMigrationWizardService _svc;

    public MigrationWizardController(IMigrationWizardService svc) => _svc = svc;

    public record CreateSessionRequest(string Name, MigrationType Type, Guid? TargetFiscalPeriodId);

    [HttpPost("sessions")]
    public async Task<ActionResult<ApiResponse<object>>> Create(Guid companyId, [FromBody] CreateSessionRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var s = await _svc.CreateSessionAsync(companyId, request.Name, request.Type, request.TargetFiscalPeriodId, userId);
        return Ok(new ApiResponse<object>(true, new { s.Id, s.SessionName, s.Status, s.MigrationType }, "สร้าง session สำเร็จ"));
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<ApiResponse<object>>> List(Guid companyId)
    {
        var sessions = await _svc.ListSessionsAsync(companyId);
        return Ok(new ApiResponse<object>(true, sessions.Select(s => new {
            s.Id, s.SessionName, s.Status, s.MigrationType, s.CreatedAt, s.CompletedAt,
        }).ToList()));
    }

    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Get(Guid companyId, Guid sessionId)
    {
        var s = await _svc.GetSessionAsync(companyId, sessionId);
        var mappings = await _svc.GetMappingsAsync(companyId, sessionId);
        return Ok(new ApiResponse<object>(true, new {
            session = new { s.Id, s.SessionName, s.Status, s.MigrationType, s.TargetFiscalPeriodId, s.CreatedAt, s.CompletedAt, s.ImportSummaryJson },
            mappings = mappings.Select(m => new {
                m.Id, m.LegacyCode, m.LegacyName, m.LegacyDebit, m.LegacyCredit,
                m.MappedAccountId,
                MappedAccountCode = m.MappedAccount?.AccountCode,
                MappedAccountName = m.MappedAccount?.AccountName,
            }).ToList(),
        }));
    }

    public record LegacyAccountRow(string Code, string? Name, decimal Debit, decimal Credit);
    public record UploadRequest(List<LegacyAccountRow> Rows);

    [HttpPost("sessions/{sessionId:guid}/upload")]
    public async Task<ActionResult<ApiResponse<int>>> Upload(Guid companyId, Guid sessionId, [FromBody] UploadRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _svc.UploadLegacyAccountsAsync(companyId, sessionId,
            request.Rows.Select(r => (r.Code, r.Name, r.Debit, r.Credit)).ToList(),
            userId);
        return Ok(new ApiResponse<int>(true, request.Rows.Count, $"อัพโหลด {request.Rows.Count} legacy accounts"));
    }

    public record ApplyMappingsRequest(Dictionary<string, Guid> CodeToAccountId);

    [HttpPost("sessions/{sessionId:guid}/mappings")]
    public async Task<ActionResult<ApiResponse<string>>> ApplyMappings(Guid companyId, Guid sessionId, [FromBody] ApplyMappingsRequest request)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _svc.ApplyMappingsAsync(companyId, sessionId, request.CodeToAccountId, userId);
        return Ok(new ApiResponse<string>(true, null, $"Apply mapping {request.CodeToAccountId.Count} รายการสำเร็จ"));
    }

    [HttpPost("sessions/{sessionId:guid}/validate")]
    public async Task<ActionResult<ApiResponse<object>>> Validate(Guid companyId, Guid sessionId)
    {
        var issues = await _svc.ValidateSessionAsync(companyId, sessionId);
        return Ok(new ApiResponse<object>(true, new { isReady = issues.Count == 0, issues }));
    }

    [HttpPost("sessions/{sessionId:guid}/commit")]
    public async Task<ActionResult<ApiResponse<object>>> Commit(Guid companyId, Guid sessionId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var s = await _svc.CommitAsync(companyId, sessionId, userId);
        return Ok(new ApiResponse<object>(true, new { s.Id, s.Status, s.CompletedAt, s.ImportSummaryJson }, "Commit migration สำเร็จ"));
    }

    /// <summary>Undo a Committed session — deletes the opening balances it
    /// wrote and flips the status to RolledBack. Blocked when the target
    /// fiscal period is closed/locked.</summary>
    [HttpPost("sessions/{sessionId:guid}/rollback")]
    public async Task<ActionResult<ApiResponse<object>>> Rollback(Guid companyId, Guid sessionId)
    {
        var userId = JwtHelper.GetUserIdFromClaims(User).ToString();
        var s = await _svc.RollbackAsync(companyId, sessionId, userId);
        return Ok(new ApiResponse<object>(true, new { s.Id, s.Status, s.ImportSummaryJson }, "ย้อนการ commit สำเร็จ"));
    }
}
