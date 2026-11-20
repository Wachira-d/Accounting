using Accounting.Data;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs;
using Accounting.Services.Background;
using Accounting.Services.Implementations.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 275 แพ็ก A (T4 M-1) — หน้า "สุขภาพข้อมูลเข้ารหัส" ส่วน<b>ความลับ</b>ของบริษัท (SMTP · LINE · e-Tax · ช่องทางชำระ · webhook · ธนาคาร · ปลายทาง LINE ผู้ติดต่อ)
///
/// <para>อ่านอย่างเดียว · ทะเบียนเดียวกับงานย้ายคีย์ (<see cref="SecretReprotectPass"/> โหมด <c>inspectOnly</c>) ⇒ ไม่มีรายการช่องสำเนาที่สอง ·
/// ไม่มีค่าความลับ/ciphertext ในคำตอบ (ชื่อช่อง · สถานะเป็นชื่อ enum · เหตุ · ทางไปต่อ) · ส่วน PII อยู่ที่ <c>GET payroll/tax-identity-health</c> เดิม (ด่านเงินเดือน)</para>
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/encryption-health")]
[Authorize]
[RequirePermission(PermissionKeys.CompanySettingsEdit)]
public class EncryptionHealthController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;

    public EncryptionHealthController(AccountingDbContext db, ISecretProtector secrets)
    { _db = db; _secrets = secrets; }

    [HttpGet("secrets")]
    public async Task<ActionResult<ApiResponse<object>>> GetSecrets(Guid companyId, CancellationToken ct)
    {
        var r = await SecretReprotectPass.RunAsync(_db, _secrets, ct, onlyCompanyId: companyId, inspectOnly: true);
        var key = await EncryptionKeyFingerprintService.CheckKeyAsync(_db, _secrets, persist: false, null, ct);
        var job = await LastRunAsync(_db, SecretReprotectJob.JobName, ct);
        return Ok(new ApiResponse<object>(true, new
        {
            Scanned = r.Scanned,
            Unusable = r.Unusable,
            KeyStatus = key.Verdict.ToString(),
            KeyMessage = key.TenantMessage,
            Job = job,
            Rows = (r.Findings ?? Array.Empty<SecretReprotectPass.Finding>()).Select(f => new
            {
                f.Label, f.Table, f.Column, f.RowId, State = f.State.ToString(),
                Blocking = f.State is SecretState.WrongKey or SecretState.Malformed,
                Reason = SecretReprotectPass.ReasonFor(f.State),
                Action = SecretReprotectPass.ActionFor(f.Table, f.Column, f.State),
            }),
        }));
    }

    internal static async Task<object?> LastRunAsync(AccountingDbContext db, string jobName, CancellationToken ct)
        => await db.JobRunLogs.AsNoTracking().Where(j => j.JobName == jobName && j.FinishedAt != null)
            .OrderByDescending(j => j.FinishedAt)
            .Select(j => new { j.FinishedAt, j.Success, j.Message, j.ItemsProcessed })
            .FirstOrDefaultAsync(ct);
}

/// <summary>
/// รอบ 275 แพ็ก A (T4 M-1/M-4) — มุมผู้ดูแลแพลตฟอร์ม: ลายนิ้วมือคีย์ (ไม่ใช่คีย์) · ความลับทุกบริษัทต่อสถานะ · ผลรันงานย้ายคีย์ทั้งสอง ·
/// ธง "ถอดคีย์เดิมออกได้" · ปุ่มยืนยันคีย์ปัจจุบัน (audit) — ขั้นตอนเต็มใน <c>docs/runbooks/encryption-key-rotation.md</c>
/// </summary>
[ApiController]
[Route("api/admin/encryption-health")]
[Authorize(Roles = "SystemAdmin")]
public class AdminEncryptionHealthController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;

    public AdminEncryptionHealthController(AccountingDbContext db, ISecretProtector secrets)
    { _db = db; _secrets = secrets; }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(CancellationToken ct)
    {
        var key = await EncryptionKeyFingerprintService.CheckKeyAsync(_db, _secrets, persist: false, null, ct);
        var r = await SecretReprotectPass.RunAsync(_db, _secrets, ct, inspectOnly: true);
        var piiJob = await _db.JobRunLogs.AsNoTracking().Where(j => j.JobName == PiiReprotectJob.JobName && j.FinishedAt != null)
            .OrderByDescending(j => j.FinishedAt).Select(j => new { j.FinishedAt, j.Success, j.Message, j.ItemsProcessed }).FirstOrDefaultAsync(ct);
        var secretJob = await EncryptionHealthController.LastRunAsync(_db, SecretReprotectJob.JobName, ct);
        // ถอดคีย์เดิมได้เมื่อ: ความลับไม่มีค่าบนคีย์เดิม (นับสด) + งาน PII รอบล่าสุดสำเร็จ ไม่ได้ย้ายอะไร และไม่มีค่าที่ถอดไม่ออก
        var piiClean = piiJob != null && piiJob.Success && piiJob.ItemsProcessed == 0 && !(piiJob.Message ?? "").Contains('⛔');
        return Ok(new ApiResponse<object>(true, new
        {
            Key = new
            {
                Verdict = key.Verdict.ToString(), key.Current, key.Stored, key.StoredAt, key.AdminMessage,
                PreviousKeyCount = _secrets.PreviousKeyFingerprints.Count,
                NeedsAdmin = EncryptionKeyFingerprintRule.NeedsAdmin(key.Verdict),
                // ฝ่ายค้าน MED-1: ลายนิ้วมือของแต่ละเครื่องที่ส่ง heartbeat — ผู้ดูแลเลือก "ลายนิ้วมือ" ที่ถูก ไม่ใช่ "เครื่องที่รับคำขอ"
                Instances = (key.Instances ?? Array.Empty<EncryptionKeyFingerprintService.Instance>()).Select(i => new
                {
                    i.InstanceId, i.Fingerprint, i.SeenAt, i.Recent, IsStored = i.Fingerprint == key.Stored, IsThisInstance = i.Fingerprint == key.Current && i.InstanceId == EncryptionKeyFingerprintService.InstanceId,
                }),
            },
            Secrets = new
            {
                r.Scanned, r.Unusable, r.LegacyKeyRemaining, r.PendingWrap,
                Columns = (r.States ?? new Dictionary<string, IReadOnlyDictionary<SecretState, int>>()).Select(kv => new
                {
                    Column = kv.Key,
                    Label = SecretReprotectPass.Label(kv.Key.Split('.')[0], kv.Key.Split('.')[1]),
                    States = kv.Value.ToDictionary(s => s.Key.ToString(), s => s.Value),
                }),
            },
            SecretJob = secretJob,
            PiiJob = piiJob,
            PreviousKeyRemovable = r.LegacyKeyRemaining == 0 && piiClean,
            PreviousKeyRemovableText = r.LegacyKeyRemaining == 0 && piiClean
                ? "✓ ไม่มีข้อมูลบนคีย์เดิมเหลือ — ถอด ENCRYPTION_KEY_PREVIOUS ออกได้ (แนะนำเก็บคีย์เดิมไว้ในที่ปลอดภัยอย่างน้อยหนึ่งรอบสำรองข้อมูล)"
                : "ยังถอด ENCRYPTION_KEY_PREVIOUS ไม่ได้ — " + (r.LegacyKeyRemaining > 0 ? $"ความลับยังอยู่บนคีย์เดิม {r.LegacyKeyRemaining} ค่า · " : "")
                  + (piiClean ? "" : "รอผลรันงานย้าย PII รอบที่ไม่มีอะไรต้องย้ายและไม่มีค่าที่ถอดไม่ออก"),
            Runbook = "docs/runbooks/encryption-key-rotation.md",
        }));
    }

    public sealed record AcceptKeyRequest(string? Fingerprint, string? Reason);

    /// <summary>ฝ่ายค้าน MED-1: ยืนยัน "ลายนิ้วมือที่ระบุ" (เลือกจากรายการเครื่อง) — เครื่องที่รับคำขอต้องใช้คีย์นั้น ไม่ตรง ⇒ 409 · ใช้ทั้งกรณีคีย์ไม่ตรงและยืนยันการหมุน · audit chain พร้อมเหตุผล</summary>
    [HttpPost("accept-fingerprint")]
    public async Task<ActionResult<ApiResponse<object>>> AcceptFingerprint([FromBody] AcceptKeyRequest req, CancellationToken ct)
    {
        var uid = JwtHelper.GetUserIdFromClaims(User);
        var status = await EncryptionKeyFingerprintService.AcceptAsync(_db, _secrets, req?.Fingerprint ?? "", uid == Guid.Empty ? null : uid,
            User?.Identity?.Name, req?.Reason ?? "", ct);
        return Ok(new ApiResponse<object>(true, new { Verdict = status.Verdict.ToString(), status.AdminMessage },
            EncryptionKeyFingerprintRule.NeedsAdmin(status.Verdict) ? "ยังไม่ตรง" : "บันทึกลายนิ้วมือที่เลือกแล้ว — งานย้ายคีย์ทำงานภายใน 5 นาที"));
    }
}
