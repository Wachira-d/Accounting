using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Pdpa;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

[ApiController]
[Route("api/companies/{companyId:guid}/pdpa")]
[Authorize]
public class PdpaController : ControllerBase
{
    private readonly IPdpaService _svc;

    public PdpaController(IPdpaService svc) { _svc = svc; }

    public sealed record SubmitRequest(string RequesterContact, string? RequesterName,
        string RequestType, string? Description,
        Guid? LinkedUserId, Guid? LinkedContactId);

    [HttpPost("requests")]
    public async Task<ActionResult<ApiResponse<PdpaDataSubjectRequest>>> Submit(
        Guid companyId, [FromBody] SubmitRequest req, CancellationToken ct)
    {
        var r = await _svc.SubmitAsync(companyId, req.RequesterContact, req.RequesterName,
            req.RequestType, req.Description, req.LinkedUserId, req.LinkedContactId, ct);
        return Ok(new ApiResponse<PdpaDataSubjectRequest>(true, r,
            $"รับคำขอ PDPA {r.RequestNumber} — ต้องตอบกลับภายใน {r.DueBy:yyyy-MM-dd}"));
    }

    public sealed record AssignRequest(Guid DpoUserId);

    [HttpPost("requests/{requestId:guid}/assign")]
    public async Task<ActionResult<ApiResponse<PdpaDataSubjectRequest>>> Assign(
        Guid companyId, Guid requestId, [FromBody] AssignRequest req, CancellationToken ct)
    {
        try
        {
            var r = await _svc.AssignAsync(companyId, requestId, req.DpoUserId, ct);
            return Ok(new ApiResponse<PdpaDataSubjectRequest>(true, r, "มอบหมาย DPO แล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    public sealed record CompleteRequest(string CompletionNote);

    [HttpPost("requests/{requestId:guid}/complete")]
    public async Task<ActionResult<ApiResponse<PdpaDataSubjectRequest>>> Complete(
        Guid companyId, Guid requestId, [FromBody] CompleteRequest req, CancellationToken ct)
    {
        try
        {
            var r = await _svc.CompleteAsync(companyId, requestId, req.CompletionNote, ct);
            return Ok(new ApiResponse<PdpaDataSubjectRequest>(true, r, "ปิดงาน PDPA แล้ว"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
    }

    [HttpGet("overdue")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PdpaDataSubjectRequest>>>> Overdue(
        Guid companyId, CancellationToken ct)
    {
        var rows = await _svc.ListOverdueAsync(companyId, ct);
        return Ok(new ApiResponse<IReadOnlyList<PdpaDataSubjectRequest>>(true, rows,
            $"คำขอ PDPA เกินกำหนด {rows.Count} รายการ"));
    }

    [HttpGet("erasure-impact")]
    public async Task<ActionResult<ApiResponse<ErasureImpactReport>>> ErasureImpact(
        Guid companyId, [FromQuery] Guid? userId, [FromQuery] Guid? contactId,
        CancellationToken ct)
    {
        var r = await _svc.ProposeErasureImpactAsync(companyId, userId, contactId, ct);
        return Ok(new ApiResponse<ErasureImpactReport>(true, r));
    }

    // ===== DSR endpoints (Data Subject Rights ตาม PDPA ม.30-33) =====

    /// <summary>ม.30 สิทธิเข้าถึง — รวบรวมข้อมูลส่วนบุคคล + audit access log
    /// 1 ปีย้อนหลังเป็น JSON portable. ส่ง subject เพื่อ port ไป provider อื่น
    /// ได้ตามม.31 portability.</summary>
    [HttpGet("dsr/access")]
    public async Task<ActionResult<ApiResponse<DataSubjectAccessResult>>> DsrAccess(
        Guid companyId, [FromQuery] Guid? userId, [FromQuery] Guid? contactId,
        CancellationToken ct)
    {
        var r = await _svc.GenerateAccessReportAsync(companyId, userId, contactId, ct);
        return Ok(new ApiResponse<DataSubjectAccessResult>(true, r,
            $"DSR access report: {r.TotalRecords} records (within 30-day SLA)"));
    }

    /// <summary>ม.31 portability — เหมือน /access แต่ส่งเป็น file download
    /// (Content-Disposition: attachment). subject ดาวน์โหลด JSON ตรงได้</summary>
    [HttpGet("dsr/portability")]
    public async Task<IActionResult> DsrPortability(
        Guid companyId, [FromQuery] Guid? userId, [FromQuery] Guid? contactId,
        CancellationToken ct)
    {
        var r = await _svc.GenerateAccessReportAsync(companyId, userId, contactId, ct);
        var json = System.Text.Json.JsonSerializer.Serialize(r, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var name = $"PDPA-DSR-{userId ?? contactId ?? Guid.Empty:N}-{DateTime.UtcNow:yyyyMMdd}.json";
        return File(bytes, "application/json", name);
    }

    public sealed record DsrRectifyRequest(Guid? UserId, Guid? ContactId,
        Dictionary<string, string?> FieldUpdates);

    /// <summary>ม.31 สิทธิแก้ไข — apply rectification. รับเป็น field-value map
    /// ที่ DPO อนุมัติแล้ว. financial records (Document/JE) ไม่อยู่ใน scope
    /// (ม.32 กฎหมายอื่นบังคับเก็บ พ.ร.บ.บัญชี ม.10).</summary>
    [HttpPost("dsr/rectify")]
    public async Task<ActionResult<ApiResponse<int>>> DsrRectify(
        Guid companyId, [FromBody] DsrRectifyRequest req, CancellationToken ct)
    {
        if (req.FieldUpdates == null || req.FieldUpdates.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null, "FieldUpdates ว่าง"));
        var changed = await _svc.ApplyRectificationAsync(companyId, req.UserId,
            req.ContactId, req.FieldUpdates, ct);
        return Ok(new ApiResponse<int>(true, changed,
            $"แก้ไขข้อมูล {changed} แหล่ง (User/Contact)"));
    }

    public sealed record DsrEraseRequest(Guid? UserId, Guid? ContactId, string? Acknowledgement);

    /// <summary>ม.33 สิทธิลบ — cascade anonymize. legal_hold: ข้อมูลในงวด
    /// retention 5 ปี (พ.ร.บ.บัญชี ม.10) จะคงไว้แต่ replace identifying fields
    /// ด้วย "[ANONYMIZED]". DPO ต้องยืนยันก่อนเรียก endpoint นี้ (ส่ง
    /// Acknowledgement = "I confirm" ใน body)</summary>
    [HttpPost("dsr/erase")]
    public async Task<ActionResult<ApiResponse<int>>> DsrErase(
        Guid companyId, [FromBody] DsrEraseRequest req, CancellationToken ct)
    {
        if (req.Acknowledgement != "I confirm")
            return BadRequest(new ApiResponse<object>(false, null,
                "ต้องยืนยัน Acknowledgement='I confirm' ก่อน erasure (irreversible action)"));
        var userId = Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();
        var changed = await _svc.ApplyErasureAsync(companyId, req.UserId,
            req.ContactId, userId, ct);
        return Ok(new ApiResponse<int>(true, changed,
            $"Anonymize เสร็จ {changed} แหล่ง (financial records ที่ยัง retain อยู่ภายใน 5 ปียังคงไว้)"));
    }

    // ===== Wave 3 — RoPA =====
    [HttpGet("ropa")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PdpaProcessingActivity>>>> ListRopa(
        Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<IReadOnlyList<PdpaProcessingActivity>>(true,
            await _svc.ListProcessingActivitiesAsync(companyId, ct)));

    public sealed record UpsertRopaRequest(Guid? Id, string Purpose, string LegalBasis,
        string DataCategories, string RetentionPeriod, string? Recipients,
        bool TransfersOutsideThailand, string? TransferSafeguards, string? Notes);

    [HttpPost("ropa")]
    public async Task<ActionResult<ApiResponse<PdpaProcessingActivity>>> UpsertRopa(
        Guid companyId, [FromBody] UpsertRopaRequest req, CancellationToken ct)
    {
        var actor = Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();
        var row = await _svc.UpsertProcessingActivityAsync(companyId, req.Id,
            req.Purpose, req.LegalBasis, req.DataCategories, req.RetentionPeriod,
            req.Recipients, req.TransfersOutsideThailand, req.TransferSafeguards,
            req.Notes, actor, ct);
        return Ok(new ApiResponse<PdpaProcessingActivity>(true, row,
            req.Id.HasValue ? "อัปเดต RoPA แล้ว" : "บันทึก RoPA ใหม่แล้ว"));
    }

    // ===== Wave 3 — Consent =====
    public sealed record GrantConsentRequest(Guid? SubjectUserId, Guid? SubjectContactId,
        string? SubjectContact, string Purpose, string? PolicyVersion, string? Channel,
        string? EvidenceHash);

    [HttpPost("consent")]
    public async Task<ActionResult<ApiResponse<PdpaConsentRecord>>> GrantConsent(
        Guid companyId, [FromBody] GrantConsentRequest req, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var row = await _svc.GrantConsentAsync(companyId, req.SubjectUserId, req.SubjectContactId,
            req.SubjectContact, req.Purpose, req.PolicyVersion ?? "1.0", req.Channel, ip,
            req.EvidenceHash, ct);
        return Ok(new ApiResponse<PdpaConsentRecord>(true, row, "บันทึกความยินยอมแล้ว"));
    }

    public sealed record WithdrawConsentRequest(string Reason);
    [HttpPost("consent/{consentId:guid}/withdraw")]
    public async Task<ActionResult<ApiResponse<PdpaConsentRecord>>> WithdrawConsent(
        Guid companyId, Guid consentId, [FromBody] WithdrawConsentRequest req, CancellationToken ct)
    {
        var row = await _svc.WithdrawConsentAsync(companyId, consentId, req.Reason, ct);
        return Ok(new ApiResponse<PdpaConsentRecord>(true, row, "ถอนความยินยอมแล้ว — หยุดใช้ข้อมูลตาม purpose นี้"));
    }

    // ===== Wave 3 — Breach =====
    public sealed record ReportBreachRequest(string Severity, string Description,
        string AffectedDataCategories, int? AffectedSubjectsCount);

    [HttpPost("breach")]
    public async Task<ActionResult<ApiResponse<PdpaBreachIncident>>> ReportBreach(
        Guid companyId, [FromBody] ReportBreachRequest req, CancellationToken ct)
    {
        var reporter = Helpers.JwtHelper.GetUserIdFromClaims(User);
        var row = await _svc.ReportBreachAsync(companyId, req.Severity, req.Description,
            req.AffectedDataCategories, req.AffectedSubjectsCount, reporter, ct);
        return Ok(new ApiResponse<PdpaBreachIncident>(true, row,
            $"บันทึกเหตุการณ์ {row.IncidentNumber} — ต้องแจ้ง PDPC ภายใน {row.NotifyPdpcDueBy:yyyy-MM-dd HH:mm} (72 ชม.)"));
    }

    public sealed record UpdateBreachRequest(string? Status, DateTime? PdpcNotifiedAt,
        string? PdpcReferenceNumber, DateTime? SubjectsNotifiedAt, string? Mitigation, string? RootCause);

    [HttpPost("breach/{breachId:guid}")]
    public async Task<ActionResult<ApiResponse<PdpaBreachIncident>>> UpdateBreach(
        Guid companyId, Guid breachId, [FromBody] UpdateBreachRequest req, CancellationToken ct)
    {
        var row = await _svc.UpdateBreachAsync(companyId, breachId, req.Status,
            req.PdpcNotifiedAt, req.PdpcReferenceNumber, req.SubjectsNotifiedAt,
            req.Mitigation, req.RootCause, ct);
        return Ok(new ApiResponse<PdpaBreachIncident>(true, row, "อัปเดต breach แล้ว"));
    }

    [HttpGet("breach/alerts")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PdpaBreachIncident>>>> BreachAlerts(
        Guid companyId, CancellationToken ct)
        => Ok(new ApiResponse<IReadOnlyList<PdpaBreachIncident>>(true,
            await _svc.ListBreachAlertsAsync(companyId, ct)));
}
