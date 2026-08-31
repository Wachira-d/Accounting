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
    private readonly Services.Interfaces.IPermissionService _permissions;

    public PdpaController(IPdpaService svc, Services.Interfaces.IPermissionService permissions)
    {
        _svc = svc;
        _permissions = permissions;
    }

    /// <summary>
    /// ด่านสิทธิ์ของงาน DSR/DPO — ต้องมี <c>perm:Pii.View</c> (Owner/SystemAdmin ผ่านเอง)
    ///
    /// ═══ ทำไมต้องมี ═══
    /// เดิมคลาสนี้มีแค่ <c>[Authorize]</c> ⇒ <b>สมาชิกคนไหนของบริษัทก็ได้</b>
    /// เรียก <c>dsr/access</c> เพื่อดัมพ์โปรไฟล์ + เอกสาร + การชำระเงิน +
    /// <b>ประวัติการเข้าถึง 1 ปี</b> ของใครก็ได้ · <c>dsr/portability</c> โหลดเป็น
    /// ไฟล์ · <c>dsr/rectify</c> แก้ข้อมูลคนอื่น · <c>dsr/erase</c>
    /// <b>anonymize ถาวร</b> — ทั้งที่หน้าเงินเดือนในเรพเดียวกันยังต้องมี
    /// <c>Pii.View</c> ถึงจะเห็นเลขบัตรแบบไม่ mask. ด่านที่อ่อนกว่าแต่คืนข้อมูล
    /// มากกว่า = ช่องที่ใหญ่ที่สุด (PDPA ม.37(1) บังคับให้มีมาตรการควบคุมการเข้าถึง)
    /// </summary>
    private async Task<ActionResult?> RequireDpoAsync(Guid companyId)
    {
        var userId = Helpers.JwtHelper.GetUserIdFromClaims(User);
        if (await _permissions.HasPermissionAsync(companyId, userId,
                Models.Constants.PermissionKeys.PiiView))
            return null;
        return StatusCode(403, new ApiResponse<object>(false, new
        {
            requiredPermission = Models.Constants.PermissionKeys.PiiView,
        }, "ต้องมีสิทธิ์ดูข้อมูลส่วนบุคคล (Pii.View) จึงจะทำงาน PDPA/DSR ได้"));
    }

    /// <summary>บันทึกการเข้าถึง/แก้ไข PII ลง <c>PiiAccessLog</c> (ม.37(4))
    ///
    /// <para>⚠️ <c>LogPiiAccessAsync</c> ถูกเขียนไว้ตั้งแต่ต้นและ doc-comment ของ
    /// <c>PermissionKeys.PiiView</c> ก็ระบุว่า "ทุกครั้งที่ field ถูกอ่านแบบ raw
    /// ต้อง log ลง PiiAccessLog" — แต่ <b>ไม่มี call site เลยทั้งเรพ</b> ⇒ ตาราง
    /// ว่างเปล่าตลอด = ไม่มี control จริง (doc-comment เป็นเจตนา ไม่ใช่หลักฐาน)</para>
    /// </summary>
    private async Task LogPiiAsync(Guid companyId, string subjectType, Guid subjectId,
        string fieldName, string operation, string purpose, CancellationToken ct)
    {
        var actorId = Helpers.JwtHelper.GetUserIdFromClaims(User);
        var actorEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "";
        await _svc.LogPiiAccessAsync(companyId, actorId, actorEmail, subjectType, subjectId,
            fieldName, operation, purpose,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(), ct);
    }

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
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
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
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
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
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
        var rows = await _svc.ListOverdueAsync(companyId, ct);
        return Ok(new ApiResponse<IReadOnlyList<PdpaDataSubjectRequest>>(true, rows,
            $"คำขอ PDPA เกินกำหนด {rows.Count} รายการ"));
    }

    [HttpGet("erasure-impact")]
    public async Task<ActionResult<ApiResponse<ErasureImpactReport>>> ErasureImpact(
        Guid companyId, [FromQuery] Guid? userId, [FromQuery] Guid? contactId,
        CancellationToken ct)
    {
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
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
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
        var r = await _svc.GenerateAccessReportAsync(companyId, userId, contactId, ct);
        await LogPiiAsync(companyId, userId.HasValue ? "User" : "Contact",
            userId ?? contactId ?? Guid.Empty, "*", "Read", "DSR ม.30 สิทธิเข้าถึง", ct);
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
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
        var r = await _svc.GenerateAccessReportAsync(companyId, userId, contactId, ct);
        await LogPiiAsync(companyId, userId.HasValue ? "User" : "Contact",
            userId ?? contactId ?? Guid.Empty, "*", "Export", "DSR ม.31 portability", ct);
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
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
        if (req.FieldUpdates == null || req.FieldUpdates.Count == 0)
            return BadRequest(new ApiResponse<object>(false, null, "FieldUpdates ว่าง"));
        try
        {
            var changed = await _svc.ApplyRectificationAsync(companyId, req.UserId,
                req.ContactId, req.FieldUpdates, ct);
            await LogPiiAsync(companyId, req.UserId.HasValue ? "User" : "Contact",
                req.UserId ?? req.ContactId ?? Guid.Empty,
                string.Join(",", req.FieldUpdates.Keys), "Update", "DSR ม.31 สิทธิแก้ไข", ct);
            return Ok(new ApiResponse<int>(true, changed,
                $"แก้ไขข้อมูล {changed} แหล่ง (User/Contact)"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
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
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
        if (req.Acknowledgement != "I confirm")
            return BadRequest(new ApiResponse<object>(false, null,
                "ต้องยืนยัน Acknowledgement='I confirm' ก่อน erasure (irreversible action)"));
        var userId = Helpers.JwtHelper.GetUserIdFromClaims(User).ToString();
        try
        {
            var changed = await _svc.ApplyErasureAsync(companyId, req.UserId,
                req.ContactId, userId, ct);
            await LogPiiAsync(companyId, req.UserId.HasValue ? "User" : "Contact",
                req.UserId ?? req.ContactId ?? Guid.Empty, "*", "Erase", "DSR ม.33 สิทธิลบ", ct);
            return Ok(new ApiResponse<int>(true, changed,
                $"Anonymize เสร็จ {changed} แหล่ง (financial records ที่ยัง retain อยู่ภายใน 5 ปียังคงไว้)"));
        }
        catch (InvalidOperationException ex) { return BadRequest(new ApiResponse<object>(false, null, ex.Message)); }
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
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
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
        // ถอนความยินยอม "ของคนอื่น" ต้องเป็นงาน DPO — ผู้ใช้ทั่วไปถอนของตัวเอง
        // ผ่านหน้าตั้งค่าของตัวเอง ไม่ใช่ endpoint ที่รับ consentId ตรง ๆ
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
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
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
        var row = await _svc.UpdateBreachAsync(companyId, breachId, req.Status,
            req.PdpcNotifiedAt, req.PdpcReferenceNumber, req.SubjectsNotifiedAt,
            req.Mitigation, req.RootCause, ct);
        return Ok(new ApiResponse<PdpaBreachIncident>(true, row, "อัปเดต breach แล้ว"));
    }

    /// <summary>รายการเหตุการณ์ข้อมูลรั่ว — เป็นข้อมูลภายในที่อ่อนไหว
    /// (ระบุว่ารั่วอะไร กระทบกี่คน ยังไม่แจ้ง PDPC หรือยัง) จึงจำกัดสิทธิ์
    /// ต่างจากการ **แจ้ง** เหตุ (POST /breach) ที่เปิดให้ทุกคนรายงานได้</summary>
    [HttpGet("breach/alerts")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<PdpaBreachIncident>>>> BreachAlerts(
        Guid companyId, CancellationToken ct)
    {
        var block = await RequireDpoAsync(companyId); if (block != null) return block;
        return Ok(new ApiResponse<IReadOnlyList<PdpaBreachIncident>>(true,
            await _svc.ListBreachAlertsAsync(companyId, ct)));
    }
}
