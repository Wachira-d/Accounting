using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>Migration wizard endpoints — drive the multi-step UI for
/// COA mapping / trial balance import / sub-ledger validation.
///
/// <para>รอบ 205 ทีม D: <b>ด่านสิทธิ์ทุก action</b> (เดิมมีแค่ <c>[Authorize]</c> ⇒ สมาชิกคนไหนก็ลง/ย้อนยอดยกมาทั้งงบทดลองได้ —
/// IMPORT_ETL_REVIEW §1 แถว 5) · คีย์ = ตัวตัดสินเดียวกับเส้นนำเข้าไฟล์ <see cref="ImportExportPermissionScope.ForImport"/>("opening-balances")
/// (= Journal.Manage — นักบัญชีถืออยู่แล้ว ⇒ คนที่ทำงานนี้อยู่ไม่ถูกกัน) · อ่านก็ต้องมีคีย์เดียวกัน (ยอดทุกบัญชีของบริษัท) ·
/// สถานะ/ป้ายแหล่ง/ข้อความทางไปต่อมาจากเซิร์ฟเวอร์ (หน้าเว็บไม่ถือตาราง)</para></summary>
[ApiController]
[Route("api/companies/{companyId:guid}/migration")]
[Authorize]
public class MigrationWizardController : ControllerBase
{
    /// <summary>ชนิดข้อมูลของด่าน — ชื่อเดียวกับ target ของศูนย์นำเข้า (<c>OpeningTrialBalanceImportTarget</c>)</summary>
    public const string GateEntity = "opening-balances";

    private readonly IMigrationWizardService _svc;
    private readonly IPermissionService _permissions;
    private readonly Accounting.Services.Import.IOpeningBalanceReconciliation _reconciliation;

    public MigrationWizardController(IMigrationWizardService svc, IPermissionService permissions,
        Accounting.Services.Import.IOpeningBalanceReconciliation reconciliation)
    {
        _svc = svc;
        _permissions = permissions;
        _reconciliation = reconciliation;
    }

    private async Task<ActionResult?> DenyAsync(Guid companyId)
    {
        var key = ImportExportPermissionScope.ForImport(GateEntity);
        if (await _permissions.HasPermissionAsync(companyId, JwtHelper.GetUserIdFromClaims(User), key)) return null;
        return StatusCode(403, new ApiResponse<object>(false, null,
            ImportExportPermissionScope.DeniedMessage("งบทดลองยกมา", key, isExport: false)));
    }

    private string UserIdString => JwtHelper.GetUserIdFromClaims(User).ToString();

    private static object SessionDto(Models.Entities.MigrationSession s) => new
    {
        s.Id, s.SessionName, s.Status, s.MigrationType, s.TargetFiscalPeriodId, s.CreatedAt, s.CompletedAt,
        StatusLabel = MigrationWizardLabels.Status(s.Status),
        TypeLabel = MigrationWizardLabels.Type(s.MigrationType),
        Step = MigrationWizardLabels.Step(s.Status),
        CanEdit = MigrationWizardLabels.IsEditable(s.Status),
        CanRollback = s.Status == MigrationStatus.Committed,
    };

    private static object ValidationDto(MigrationValidationResult v) => new
    {
        isReady = v.IsReady,
        v.TotalDebit,
        v.TotalCredit,
        v.Difference,
        errors = v.Errors.Select(e => new { e.Code, Label = OpeningTrialBalanceRules.Label(e.Code), e.Message, e.WayForward, e.Keys }),
        warnings = v.Warnings.Select(w => new { w.Code, Label = OpeningTrialBalanceRules.Label(w.Code), w.Message, w.WayForward, w.Keys }),
        // ทางไปต่อของงบไม่ลงตัว — หน้าเว็บโชว์ตัวเลือกบัญชีผลต่างเมื่อค่านี้ true (เซิร์ฟเวอร์ตัดสิน)
        canPostDifference = v.Errors.Any(e => e.Code == OpeningTrialBalanceRules.Unbalanced),
    };

    public record CreateSessionRequest(string Name, MigrationType Type, Guid? TargetFiscalPeriodId);

    [HttpPost("sessions")]
    public async Task<ActionResult<ApiResponse<object>>> Create(Guid companyId, [FromBody] CreateSessionRequest request)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        var s = await _svc.CreateSessionAsync(companyId, request.Name, request.Type, request.TargetFiscalPeriodId, UserIdString);
        return Ok(new ApiResponse<object>(true, SessionDto(s), "สร้างชุดย้ายระบบแล้ว"));
    }

    [HttpGet("sessions")]
    public async Task<ActionResult<ApiResponse<object>>> List(Guid companyId)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        var sessions = await _svc.ListSessionsAsync(companyId);
        return Ok(new ApiResponse<object>(true, sessions.Select(SessionDto).ToList()));
    }

    [HttpGet("sessions/{sessionId:guid}")]
    public async Task<ActionResult<ApiResponse<object>>> Get(Guid companyId, Guid sessionId)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        var s = await _svc.GetSessionAsync(companyId, sessionId);
        var mappings = await _svc.GetMappingsAsync(companyId, sessionId);
        return Ok(new ApiResponse<object>(true, new {
            session = SessionDto(s),
            summary = s.ImportSummaryJson,
            mappings = mappings.Select(m => new {
                m.Id, m.LegacyCode, m.LegacyName, m.LegacyDebit, m.LegacyCredit,
                m.MappedAccountId,
                MappedAccountCode = m.MappedAccount?.AccountCode,
                MappedAccountName = m.MappedAccount?.AccountName,
                m.SuggestedAccountId,
                m.SuggestionScore,
                SuggestionKind = m.SuggestionKind,
                SourceLabel = MigrationWizardService.SuggestionLabel(m.SuggestionKind, m.AccountMapUsedAi),
                UsedAi = m.AccountMapUsedAi,
                // < 0.85 = เหลือง (กฎเหล็ก #3 ข้อ 3) — เซิร์ฟเวอร์ตัดสิน
                LowConfidence = m.SuggestionScore.HasValue && m.SuggestionScore.Value < ImportAccountCodeResolver.AutoApplyThreshold,
                IsDifferenceLine = m.LegacyCode == OpeningTrialBalanceRules.DifferenceLineKey,
            }).ToList(),
        }));
    }

    public record LegacyAccountRow(string Code, string? Name, decimal Debit, decimal Credit);
    public record UploadRequest(List<LegacyAccountRow> Rows);

    [HttpPost("sessions/{sessionId:guid}/upload")]
    public async Task<ActionResult<ApiResponse<object>>> Upload(Guid companyId, Guid sessionId, [FromBody] UploadRequest request)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        await _svc.UploadLegacyAccountsAsync(companyId, sessionId,
            request.Rows.Select(r => (r.Code, r.Name, r.Debit, r.Credit)).ToList(),
            UserIdString);
        var suggestions = await _svc.SuggestMappingsAsync(companyId, sessionId, UserIdString);
        return Ok(new ApiResponse<object>(true, new { imported = request.Rows.Count, suggestions },
            $"อัปโหลด {request.Rows.Count} บรรทัด · ระบบเลือกบัญชีให้ {suggestions.Count(x => x.AutoApplied)} บรรทัด"));
    }

    /// <summary>รอบ 205 ทีม D — วางข้อความจาก Excel/โปรแกรมเดิม · เซิร์ฟเวอร์อ่าน (แท็บ · คำพูด · "1,234.50" · วงเล็บ = ติดลบ) แล้วแนะนำบัญชี ·
    /// <c>string?</c> โดยตั้งใจ: service ตอบภาษาไทยเองเมื่อว่าง (tools/dto_nullable_contract_check)</summary>
    public record UploadTextRequest(string? Text);

    [HttpPost("sessions/{sessionId:guid}/upload-text")]
    public async Task<ActionResult<ApiResponse<object>>> UploadText(Guid companyId, Guid sessionId, [FromBody] UploadTextRequest request,
        CancellationToken ct)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        var r = await _svc.UploadLegacyTextAsync(companyId, sessionId, request.Text, UserIdString, ct);
        var msg = $"อ่านได้ {r.Imported} บรรทัด · ระบบเลือกบัญชีให้ {r.Suggestions.Count(x => x.AutoApplied)} บรรทัด"
                  + (r.Skipped.Count > 0 ? $" · ข้าม {r.Skipped.Count} บรรทัด (ดูเหตุผล)" : "");
        return Ok(new ApiResponse<object>(true, r, msg));
    }

    /// <summary>รอบ 205 ทีม D — แนะนำบัญชีใหม่ให้แถวที่ยังไม่จับคู่ (แถวที่ผู้ใช้เลือกแล้วไม่ถูกแตะ)</summary>
    [HttpPost("sessions/{sessionId:guid}/suggest")]
    public async Task<ActionResult<ApiResponse<object>>> Suggest(Guid companyId, Guid sessionId, CancellationToken ct)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        var s = await _svc.SuggestMappingsAsync(companyId, sessionId, UserIdString, allowAi: true, ct);
        return Ok(new ApiResponse<object>(true, s, $"แนะนำ {s.Count} บรรทัด · ระบบเลือกให้ {s.Count(x => x.AutoApplied)} บรรทัด"));
    }

    public record ApplyMappingsRequest(Dictionary<string, Guid> CodeToAccountId);

    [HttpPost("sessions/{sessionId:guid}/mappings")]
    public async Task<ActionResult<ApiResponse<string>>> ApplyMappings(Guid companyId, Guid sessionId, [FromBody] ApplyMappingsRequest request)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        await _svc.ApplyMappingsAsync(companyId, sessionId, request.CodeToAccountId, UserIdString);
        return Ok(new ApiResponse<string>(true, null, $"บันทึกการจับคู่ {request.CodeToAccountId.Count} รายการแล้ว"));
    }

    [HttpPost("sessions/{sessionId:guid}/validate")]
    public async Task<ActionResult<ApiResponse<object>>> Validate(Guid companyId, Guid sessionId)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        var v = await _svc.ValidateSessionAsync(companyId, sessionId);
        return Ok(new ApiResponse<object>(true, ValidationDto(v)));
    }

    /// <summary>ทางไปต่อของงบทดลองไม่ลงตัว (คำตัดสินข้อ 150) — ผู้ใช้เลือกบัญชีผลต่างยกมาเอง</summary>
    public record PostDifferenceRequest(Guid AccountId);

    [HttpPost("sessions/{sessionId:guid}/post-difference")]
    public async Task<ActionResult<ApiResponse<object>>> PostDifference(Guid companyId, Guid sessionId, [FromBody] PostDifferenceRequest request)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        var v = await _svc.PostDifferenceToAccountAsync(companyId, sessionId, request.AccountId, UserIdString);
        return Ok(new ApiResponse<object>(true, ValidationDto(v), "เพิ่มบรรทัดผลต่างยกมาแล้ว — ตรวจคำเตือนแล้วกดลงยอด"));
    }

    /// <summary>คำเตือนที่ผู้ใช้รับทราบ (รหัสจาก validate) — คำเตือนไม่บล็อก แต่ต้องรับทราบ (คำตัดสินข้อ 150)</summary>
    public record CommitRequest(List<string>? AcknowledgedWarningCodes);

    [HttpPost("sessions/{sessionId:guid}/commit")]
    public async Task<ActionResult<ApiResponse<object>>> Commit(Guid companyId, Guid sessionId, [FromBody] CommitRequest? request)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        var s = await _svc.CommitAsync(companyId, sessionId, UserIdString, request?.AcknowledgedWarningCodes);
        return Ok(new ApiResponse<object>(true, SessionDto(s), "ลงยอดยกมาแล้ว — ย้อนได้ตราบที่งวดยังเปิด"));
    }

    /// <summary>Undo a Committed session — deletes the opening balances it created and restores the ones it overwrote,
    /// then flips the status to RolledBack. Blocked when the target fiscal period is closed/locked.</summary>
    [HttpPost("sessions/{sessionId:guid}/rollback")]
    public async Task<ActionResult<ApiResponse<object>>> Rollback(Guid companyId, Guid sessionId)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        var s = await _svc.RollbackAsync(companyId, sessionId, UserIdString);
        return Ok(new ApiResponse<object>(true, new { Session = SessionDto(s), s.ImportSummaryJson }, "ย้อนการลงยอดแล้ว — คืนยอดเดิมที่ถูกทับให้แล้ว"));
    }

    /// <summary>รอบ 205 ทีม D — กระทบยอดหลังย้ายระบบ (ขั้นสุดท้ายของลำดับ IMPORT_ETL_REVIEW §2.10): ลูกหนี้/เจ้าหนี้ vs บัญชีคุม ·
    /// มูลค่าสต็อก vs บัญชีสินค้า · ราคาทุน/ค่าเสื่อมสะสมของทะเบียนสินทรัพย์ vs งบทดลอง · อ่านอย่างเดียว · ไม่ลงตัว = Hint ทางไปต่อ (ไม่บล็อก)</summary>
    [HttpGet("reconciliation")]
    public async Task<ActionResult<ApiResponse<object>>> Reconciliation(Guid companyId, [FromQuery] DateTime? asOf, CancellationToken ct)
    {
        if (await DenyAsync(companyId) is { } deny) return deny;
        var report = await _reconciliation.ReconcileAsync(companyId, asOf?.Date ?? DateTime.UtcNow.AddHours(7).Date, ct);
        return Ok(new ApiResponse<object>(true, report, report.AllBalanced
            ? "ทุกบรรทัดลงตัว"
            : $"ไม่ลงตัว {report.Lines.Count(l => !l.Balanced)} บรรทัด — ดูคำแนะนำในแต่ละบรรทัด"));
    }
}
