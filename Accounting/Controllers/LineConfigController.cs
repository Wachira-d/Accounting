using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Line;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>
/// Per-company LINE Messaging API configuration. Each tenant supplies
/// their own Channel Access Token + Secret so notifications go through
/// their own LINE Official Account (separate billing, branding, opt-in).
/// Secrets are encrypted at rest via SecretProtector.
/// </summary>
[ApiController]
[Route("api/companies/{companyId:guid}/line-config")]
[Authorize]
public class LineConfigController : ControllerBase
{
    private readonly AccountingDbContext _db;
    private readonly ISecretProtector _secrets;
    private readonly ILineNotifyService _line;
    private readonly ICustomerLineLinkService? _customerLinks;
    private readonly ICustomerRelinkInviteService? _relink;
    private readonly ILogger<LineConfigController>? _logger;

    public LineConfigController(AccountingDbContext db, ISecretProtector secrets, ILineNotifyService line,
        ICustomerLineLinkService? customerLinks = null, ICustomerRelinkInviteService? relink = null, ILogger<LineConfigController>? logger = null)
    {
        _db = db;
        _secrets = secrets;
        _line = line;
        _customerLinks = customerLinks;
        _relink = relink;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<LineConfigResponse>>> Get(Guid companyId)
    {
        var s = await GetOrCreateSettings(companyId);
        return Ok(new ApiResponse<LineConfigResponse>(true, await BuildAsync(companyId, s)));
    }

    // รอบ 193 (ฝ่ายค้าน W-C2): เดิมไม่มีด่านเลย — สมาชิกคนไหน/คีย์ไหนก็เปลี่ยน LINE channel token/secret ได้
    [HttpPut]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ตั้งค่า LINE")]
    public async Task<ActionResult<ApiResponse<LineConfigResponse>>> Update(
        Guid companyId, [FromBody] UpdateLineConfigRequest req)
    {
        var s = await GetOrCreateSettings(companyId);
        // รอบ 212 แพ็ก B: OA ที่ "ส่งจริง" ก่อนบันทึก (ตัวตัดสินเดียว ResolveChannelAsync — อ่านจากฐาน ไม่ใช่ค่าที่กำลังแก้)
        var before = _customerLinks == null ? null : await _customerLinks.ResolveChannelAsync(companyId);
        s.LineEnabled = req.Enabled;
        if (!string.IsNullOrEmpty(req.ChannelAccessToken))
            s.LineChannelAccessToken = _secrets.Protect(req.ChannelAccessToken);
        if (!string.IsNullOrEmpty(req.ChannelSecret))
            s.LineChannelSecret = _secrets.Protect(req.ChannelSecret);
        if (req.DefaultGroupId != null) s.LineDefaultGroupId = req.DefaultGroupId;
        // รอบ 211: Basic ID ใช้ทำปุ่มเพิ่มเพื่อน/ส่งรหัสของลูกค้า ⇒ รูปแบบผิดต้องบอก (ไม่เก็บเงียบแล้วปุ่มพัง)
        if (req.OaBasicId != null)
        {
            if (string.IsNullOrWhiteSpace(req.OaBasicId)) s.LineOaBasicId = null;
            else if (CustomerLineLinkRule.NormalizeBasicId(req.OaBasicId) is string basic) s.LineOaBasicId = basic;
            else return BadRequest(new ApiResponse<LineConfigResponse>(false, null, "LINE OA Basic ID ไม่ถูกต้อง — ใช้รูป @xxxx (ตัวอักษรอังกฤษ/ตัวเลข/จุด/ขีด)"));
        }
        // เปลี่ยน token ⇒ อาจเป็น OA ใหม่ ⇒ ถาม LINE ว่า token นี้เป็น bot ไหน (รอบ 212 แพ็ก B — รู้ทันทีว่าเปลี่ยน OA หรือแค่ออก token ใหม่) ·
        // ถามไม่ได้ ⇒ ลืม bot userId เดิม (webhook ครั้งถัดไปประทับใหม่ + ตรวจการสลับ · ลิงก์ของ OA เดิมตัดสินด้วย scope จนกว่าจะรู้)
        if (!string.IsNullOrEmpty(req.ChannelAccessToken))
            s.LineOaDestination = _relink == null ? null : await _relink.FetchBotUserIdAsync(req.ChannelAccessToken, HttpContext?.RequestAborted ?? default);
        s.UpdatedAt = DateTime.UtcNow;
        // Reset configured flag after change — admin must re-test
        s.LineConfigured = false;
        await _db.SaveChangesAsync();

        var message = "บันทึกการตั้งค่า LINE เรียบร้อย";
        var relinkNote = await RecordOaSwitchAsync(companyId, before);
        if (relinkNote != null) message += " · " + relinkNote;
        return Ok(new ApiResponse<LineConfigResponse>(true, await BuildAsync(companyId, s), message));
    }

    /// <summary>รอบ 212 แพ็ก B: OA ที่ส่งจริงเปลี่ยน (ร้าน↔NextAcc หรือ bot ใหม่) ⇒ บันทึกการสลับให้งานเบื้องหลังเชิญลูกค้าที่ผูกไว้เดิมผูกใหม่ ·
    /// ไม่ส่งในคำขอ HTTP · บันทึกไม่ได้ ⇒ บอกในข้อความตอบกลับ (ค่าตั้งบันทึกแล้ว — ไม่ล้มทั้งคำขอ) · คืนข้อความเพิ่มหรือ null</summary>
    private async Task<string?> RecordOaSwitchAsync(Guid companyId, CustomerLineChannel? before)
    {
        if (_customerLinks == null || _relink == null || before == null) return null;
        try
        {
            var after = await _customerLinks.ResolveChannelAsync(companyId);
            if (!CustomerRelinkInvitePlan.IsSwitch(before.Scope, before.CurrentDestination, after.Scope, after.CurrentDestination)) return null;
            var actor = User?.Identity?.Name;
            await _relink.RecordSwitchAsync(companyId, before.Scope, before.CurrentDestination, after.Scope, after.CurrentDestination,
                CustomerRelinkInvitePlan.SourceConfigPut, actor);
            return "เปลี่ยนบัญชี LINE ที่ใช้แจ้งลูกค้าแล้ว — ระบบจะทยอยส่งคำเชิญให้ลูกค้าที่เคยผูกไว้ผูกใหม่ (ดูความคืบหน้าในส่วน \"แจ้งเตือนลูกค้าทาง LINE\" · ปิดได้ที่ตารางการแจ้งเตือนลูกค้า แถว \"เชิญผูก LINE ใหม่\")";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.LogError(ex, "บันทึกการเปลี่ยน LINE OA ของบริษัท {CompanyId} ไม่สำเร็จ", companyId);
            return "⚠️ บันทึกการเปลี่ยนบัญชี LINE เพื่อเชิญลูกค้าผูกใหม่ไม่สำเร็จ — ลูกค้าที่ผูกไว้เดิมจะยังไม่ได้รับคำเชิญอัตโนมัติ กรุณาแจ้งผู้ดูแลระบบ";
        }
    }

    [HttpPost("test")]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.CompanySettingsEdit)]
    public async Task<ActionResult<ApiResponse<LineTestResult>>> Test(
        Guid companyId, [FromBody] TestLineConfigRequest req)
    {
        var (ok, msg) = await _line.TestConfigAsync(companyId, req.ToLineId);
        if (ok)
        {
            var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId);
            if (s != null)
            {
                s.LineConfigured = true;
                s.LineLastTestedAt = DateTime.UtcNow;
                s.LineLastTestStatus = "OK";
                await _db.SaveChangesAsync();
            }
        }
        else
        {
            var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId);
            if (s != null)
            {
                s.LineLastTestedAt = DateTime.UtcNow;
                s.LineLastTestStatus = msg;
                await _db.SaveChangesAsync();
            }
        }
        return Ok(new ApiResponse<LineTestResult>(ok, new LineTestResult(ok, msg), msg));
    }

    private async Task<CompanySettings> GetOrCreateSettings(Guid companyId)
    {
        var s = await _db.CompanySettings.FirstOrDefaultAsync(x => x.CompanyId == companyId);
        if (s == null)
        {
            s = await CompanySettingsFactory.AddNewAsync(_db, companyId);   // seed VAT จากบริษัท (S-01)
            await _db.SaveChangesAsync();
        }
        return s;
    }

    private async Task<LineConfigResponse> BuildAsync(Guid companyId, CompanySettings s)
        => Build(s) with
        {
            CustomerChannel = _customerLinks == null ? null : await _customerLinks.GetChannelInfoAsync(companyId),
            RelinkInvite = _relink == null ? null : await _relink.GetProgressAsync(companyId),
        };

    private static LineConfigResponse Build(CompanySettings s) => new(
        Enabled: s.LineEnabled,
        HasAccessToken: !string.IsNullOrEmpty(s.LineChannelAccessToken),
        HasChannelSecret: !string.IsNullOrEmpty(s.LineChannelSecret),
        DefaultGroupId: s.LineDefaultGroupId,
        Configured: s.LineConfigured,
        LastTestedAt: s.LineLastTestedAt,
        LastTestStatus: s.LineLastTestStatus,
        OaBasicId: s.LineOaBasicId);
}
