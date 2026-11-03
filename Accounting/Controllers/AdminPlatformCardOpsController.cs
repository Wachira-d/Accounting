using System.Security.Claims;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Services.Background;
using Accounting.Services.Payments;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Accounting.Controllers;

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §5.2/§5.3) — หน้าแอดมินแพลตฟอร์ม <c>/admin/platform-card-ops.html</c>:
/// สวิตช์ตัดบัตรอัตโนมัติทั้งระบบ · คำสั่งตัดของแต่ละบริษัท (ระงับ/ยกเลิกระงับ) · ความพยายามตัดล่าสุด · "ตัดตอนนี้" ·
/// รอบโอนที่ดึงจากผู้ให้บริการ + ร่างรอบโอน (ดึงตอนนี้ · จับคู่ใหม่) — <b>ไม่มีปุ่มลงบัญชีที่นี่</b> (ลงบัญชีที่หน้ารอบโอนของบริษัทผู้ให้บริการ โดยผู้ทำบัญชี)
/// </summary>
[ApiController]
[Route("api/admin/platform-card-ops")]
[Authorize(Roles = "SystemAdmin")]
public class AdminPlatformCardOpsController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly IPlatformSavedCardService _cards;
    private readonly IPlatformAutoChargeService _autoCharge;
    private readonly IPlatformTransferReconciliationService _transfers;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AdminPlatformCardOpsController> _logger;

    public AdminPlatformCardOpsController(AccountingDbContext db, IPlatformSavedCardService cards, IPlatformAutoChargeService autoCharge,
        IPlatformTransferReconciliationService transfers, IServiceScopeFactory scopes, ILogger<AdminPlatformCardOpsController> logger)
    { _db = db; _cards = cards; _autoCharge = autoCharge; _transfers = transfers; _scopes = scopes; _logger = logger; }

    private PlatformCardActor Actor() => new(JwtHelper.GetUserIdFromClaims(User), User.FindFirstValue(ClaimTypes.Email),
        HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString());

    [HttpGet]
    public async Task<ActionResult<ApiResponse<object>>> Get(CancellationToken ct)
        => Ok(new ApiResponse<object>(true, new
        {
            autoChargeEnabled = await _cards.GlobalEnabledAsync(ct),
            consentVersion = await _cards.CurrentConsentVersionAsync(ct),
            mandates = await _cards.AdminListAsync(ct),
            attempts = await _autoCharge.RecentAttemptsAsync(100, ct),
            transfers = await _transfers.ListAsync(100, ct),
            guidance = "ตัดบัตรอัตโนมัติ: ค่าตั้งต้นปิด — เปิดหลังทดสอบบัตรที่บันทึก + การตัดซ้ำ (Idempotency-Key) กับระบบทดสอบของผู้ให้บริการแล้ว · "
                + "รอบโอน: ระบบสร้างเฉพาะ \"ร่าง\" ในบริษัทผู้ให้บริการ — ผู้ทำบัญชีตรวจยอดกับสเตทเมนต์แล้วลงบัญชีที่หน้ารอบโอน (ยอดไม่ตรง = ระบบบล็อก)",
        }));

    public sealed record GlobalRequest(bool Enabled);

    [HttpPost("auto-charge")]
    public async Task<ActionResult<ApiResponse<object>>> SetGlobal([FromBody] GlobalRequest req, CancellationToken ct)
    {
        await _cards.SetGlobalAsync(req.Enabled, Actor(), ct);
        return Ok(new ApiResponse<object>(true, new { autoChargeEnabled = req.Enabled },
            req.Enabled ? "เปิดตัดบัตรอัตโนมัติทั้งระบบแล้ว" : "ปิดตัดบัตรอัตโนมัติทั้งระบบแล้ว (ไม่ตัดใบใดอีกจนกว่าจะเปิด)"));
    }

    public sealed record BlockRequest(bool Blocked, string? Reason);

    [HttpPost("mandates/{payerCompanyId:guid}/block")]
    public async Task<ActionResult<ApiResponse<object>>> Block(Guid payerCompanyId, [FromBody] BlockRequest req, CancellationToken ct)
    {
        await _cards.AdminBlockAsync(payerCompanyId, req.Blocked, req.Reason, Actor(), ct);
        return Ok(new ApiResponse<object>(true, null, req.Blocked ? "ระงับการตัดอัตโนมัติของบริษัทนี้แล้ว" : "ยกเลิกการระงับแล้ว"));
    }

    /// <summary>ตัดตอนนี้ — JobLock ตัวเดียวกับงานรายชั่วโมง (อีกเครื่องกำลังตัด ⇒ 409)</summary>
    [HttpPost("auto-charge/run")]
    public async Task<ActionResult<ApiResponse<object>>> RunNow(CancellationToken ct)
    {
        var result = await PlatformAutoChargeJob.RunLockedAsync(_scopes, _logger, ct);
        return result == null
            ? StatusCode(409, new ApiResponse<object>(false, null, "งานตัดบัตรอัตโนมัติกำลังทำงานอยู่ที่เครื่องอื่น — รอสักครู่แล้วโหลดใหม่"))
            : Ok(new ApiResponse<object>(true, result, result.Message));
    }

    /// <summary>ดึงรอบโอนตอนนี้ — JobLock ตัวเดียวกับงานทุก 6 ชม.</summary>
    [HttpPost("transfers/pull")]
    public async Task<ActionResult<ApiResponse<object>>> Pull(CancellationToken ct)
    {
        TransferPullResult? result = null;
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.BackgroundJob, nameof(PlatformTransferPullJob),
            async () => { result = await _transfers.PullAsync(JwtHelper.GetUserIdFromClaims(User), ct); }, _logger, ct: ct);
        if (!ran) return StatusCode(409, new ApiResponse<object>(false, null, "กำลังดึงรอบโอนอยู่ที่เครื่องอื่น — รอสักครู่แล้วโหลดใหม่"));
        return Ok(new ApiResponse<object>(true, result, result?.Message));
    }

    [HttpPost("transfers/{transferId:guid}/rematch")]
    public async Task<ActionResult<ApiResponse<object>>> Rematch(Guid transferId, CancellationToken ct)
    {
        // ฝ่ายค้าน S5: JobLock ตัวเดียวกับงานดึงรอบโอน — กดจับคู่ใหม่ขณะงานกำลังร่างไม่ได้ร่างซ้อน
        PlatformTransferRow? row = null;
        var ran = await JobLock.RunExclusiveAsync(_db, AdvisoryLockKey.BackgroundJob, nameof(PlatformTransferPullJob),
            async () => { row = await _transfers.RematchAsync(transferId, JwtHelper.GetUserIdFromClaims(User), ct); }, _logger, ct: ct);
        if (!ran || row == null) return StatusCode(409, new ApiResponse<object>(false, null, "กำลังดึง/ร่างรอบโอนอยู่ที่เครื่องอื่น — รอสักครู่แล้วกดใหม่"));
        return Ok(new ApiResponse<object>(true, row, row.SettlementBatchId != null ? "สร้างร่างรอบโอนแล้ว — ตรวจและลงบัญชีที่หน้ารอบโอน" : (row.Problem ?? "ยังจับคู่ไม่ได้")));
    }
}
