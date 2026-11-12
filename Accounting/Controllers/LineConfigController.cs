using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Line;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
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
    /// <summary>รอบ 257 C-H1: ค่าตั้ง OA กลาง (ผู้อ่านตัวเดียว) — กัน token/secret ของ OA กลางถูกใส่เป็น "OA ของร้านเอง" · null (สร้างนอก DI) = ตรวจได้เฉพาะบริษัทผู้ให้บริการ</summary>
    private readonly INextAccLineConfig? _nextAcc;
    /// <summary>รอบ 260: เครื่องนี้อ่านยอดจากรูปสลิปได้ไหม (แถวความพร้อม "รับสลิปจากแชต") · null = ไม่รู้ ⇒ ถือว่าอ่านไม่ได้</summary>
    private readonly Accounting.Services.Implementations.ISlipImageReader? _slipReader;
    /// <summary>รอบ 260-4 (ฝ่ายค้าน L2): บันทึกการเข้าถึงข้อมูลส่วนบุคคลเมื่อทดสอบส่งถึงผู้รับของผู้ติดต่อ (ม.37) · null (นอก DI) = audit อย่างเดียว</summary>
    private readonly Accounting.Services.Implementations.Pdpa.IPdpaService? _pdpa;

    public LineConfigController(AccountingDbContext db, ISecretProtector secrets, ILineNotifyService line,
        ICustomerLineLinkService? customerLinks = null, ICustomerRelinkInviteService? relink = null, ILogger<LineConfigController>? logger = null,
        INextAccLineConfig? nextAcc = null, Accounting.Services.Implementations.ISlipImageReader? slipReader = null,
        Accounting.Services.Implementations.Pdpa.IPdpaService? pdpa = null)
    {
        _pdpa = pdpa;
        _nextAcc = nextAcc;
        _slipReader = slipReader;
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
        // ฝ่ายค้าน (B+D) ข้อ 2: token ใหม่ ⇒ ถาม LINE ว่าเป็นบอทไหน (ก่อนแก้อะไร) · เปลี่ยน token โดยไม่ใส่ secret ทั้งที่มี secret เดิม ⇒ ต้องเป็นบอทเดิมที่จำไว้
        // ไม่งั้นปฏิเสธ (secret ของ OA เก่า + token ของ OA ใหม่ = webhook ของ OA เก่าผ่านลายเซ็นแล้วพลิกบอทกลับ/เชิญลูกค้าผิด ๆ)
        // รอบ 257 ฝ่ายค้าน L6: ช่องว่างล้วน = ล้าง (เดิมนับเป็น "ตั้งค่าใหม่" แล้วเข้ารหัสช่องว่างเก็บไว้)
        var tokenChanged = LineOwnOaGuard.Classify(req.ChannelAccessToken) == LineOwnOaGuard.InputKind.Set;
        var secretChanged = LineOwnOaGuard.Classify(req.ChannelSecret) == LineOwnOaGuard.InputKind.Set;
        var ct = HttpContext?.RequestAborted ?? default;
        string? fetchedBot = null;
        if (tokenChanged && _relink != null)
            fetchedBot = await _relink.FetchBotUserIdAsync(req.ChannelAccessToken!.Trim(), ct);
        // รอบ 257 C-H1: token/secret ของ OA กลาง ห้ามเป็น "OA ของร้านเอง" (ทุกบริษัท) · บริษัทผู้ให้บริการตั้ง OA ของตัวเองไม่ได้ (ล้างได้) — ก่อนแก้อะไร
        var central = _nextAcc == null ? null : await _nextAcc.GetAsync(ct);
        // รอบ 257 ฝ่ายค้าน L7: Basic ID / Group ID ทีมห้ามเป็นค่าของ OA กลาง (ทุกบริษัท · ก่อนแก้อะไร)
        if (LineOwnOaGuard.CentralValueRefusal(string.IsNullOrWhiteSpace(req.OaBasicId) ? null : CustomerLineLinkRule.NormalizeBasicId(req.OaBasicId),
                req.DefaultGroupId, central?.BasicId, _nextAcc == null ? null : await _nextAcc.PlatformGroupIdsAsync(ct),
                // รอบ 257 ปิดท้าย MED-6: ปฏิเสธเฉพาะค่าที่ต่างจากที่เก็บอยู่ (ค่าเดิมที่ส่งกลับมา = ไม่ได้ตั้งใหม่ — ไม่ขวางการล้างรหัสเชื่อม/แก้ช่องอื่น)
                storedBasicId: s.LineOaBasicId, storedGroupId: s.LineDefaultGroupId) is string centralValue)
            return BadRequest(new ApiResponse<LineConfigResponse>(false, null, centralValue));
        if (tokenChanged || secretChanged)
        {
            var isPlatform = LineOwnOaGuard.IsPlatformCompany(companyId, await PlatformCompanyLookup.IdAsync(_db, ct));
            var centralBot = central?.BotUserId;
            if (tokenChanged && string.IsNullOrWhiteSpace(centralBot) && central?.HasToken == true && _relink != null)
                centralBot = await _relink.FetchBotUserIdAsync(central.AccessToken!, ct);
            if (LineOwnOaGuard.Refusal(isPlatform, req.ChannelAccessToken, req.ChannelSecret, fetchedBot, centralBot,
                    central?.AccessToken, central?.ChannelSecret) is string oaRefusal)
                return BadRequest(new ApiResponse<LineConfigResponse>(false, null, oaRefusal));
        }
        // รอบ 260-4: ชั่วโมงเงียบ — ตรวจก่อนแก้อะไร (ช่วงผิด = ปฏิเสธทั้งคำขอ ไม่เก็บครึ่งเดียว)
        var quietStart = req.QuietStartHour ?? s.LineQuietStartHour;
        var quietEnd = req.QuietEndHour ?? s.LineQuietEndHour;
        if ((req.QuietHoursEnabled ?? s.LineQuietHoursEnabled) && LineQuietHours.Problem(quietStart, quietEnd) is string quietProblem)
            return BadRequest(new ApiResponse<LineConfigResponse>(false, null, quietProblem));
        var pairProblem = CustomerRelinkInvitePlan.TokenWithoutSecretProblem(tokenChanged, secretChanged, _secrets.IsUsable(s.LineChannelSecret),
            fetchedBot, s.LineOaDestination);
        if (pairProblem != null) return BadRequest(new ApiResponse<LineConfigResponse>(false, null, pairProblem));
        s.LineEnabled = req.Enabled;
        // ฝ่ายค้าน 260-4 L5: ปิดชั่วโมงเงียบ ⇒ การ์ดที่เลื่อนไว้ส่งรอบถัดไป (ไม่ต้องรอถึง 08:00 เดิม)
        var releaseQuiet = req.QuietHoursEnabled == false && s.LineQuietHoursEnabled;
        if (req.QuietHoursEnabled is bool quietOn) s.LineQuietHoursEnabled = quietOn;
        if (req.QuietStartHour is int qs) s.LineQuietStartHour = qs;
        if (req.QuietEndHour is int qe) s.LineQuietEndHour = qe;
        if (req.ChatSlipIntake is bool chatSlip) s.LineChatSlipIntakeEnabled = chatSlip;   // รอบ 260 · null = คงเดิม (ปุ่มล้างรหัสไม่แตะ)
        // รอบ 257 C-M1: null = คงเดิม · "" = ล้าง · ค่า = ตั้งใหม่ (เดิม "" ถูกถือว่าคงเดิม ⇒ ล้างไม่ได้เงียบ ๆ)
        var tokenCleared = LineOwnOaGuard.Classify(req.ChannelAccessToken) == LineOwnOaGuard.InputKind.Clear;
        var secretCleared = LineOwnOaGuard.Classify(req.ChannelSecret) == LineOwnOaGuard.InputKind.Clear;
        if (tokenCleared) { s.LineChannelAccessToken = null; s.LineOaDestination = null; }
        else if (tokenChanged)
            s.LineChannelAccessToken = _secrets.Protect(req.ChannelAccessToken!.Trim());
        if (secretCleared) s.LineChannelSecret = null;
        else if (secretChanged)
            s.LineChannelSecret = _secrets.Protect(req.ChannelSecret!.Trim());
        if (req.DefaultGroupId != null) s.LineDefaultGroupId = string.IsNullOrWhiteSpace(req.DefaultGroupId) ? null : req.DefaultGroupId.Trim();
        // รอบ 211: Basic ID ใช้ทำปุ่มเพิ่มเพื่อน/ส่งรหัสของลูกค้า ⇒ รูปแบบผิดต้องบอก (ไม่เก็บเงียบแล้วปุ่มพัง)
        if (req.OaBasicId != null)
        {
            if (string.IsNullOrWhiteSpace(req.OaBasicId)) s.LineOaBasicId = null;
            else if (CustomerLineLinkRule.NormalizeBasicId(req.OaBasicId) is string basic) s.LineOaBasicId = basic;
            else return BadRequest(new ApiResponse<LineConfigResponse>(false, null, "LINE OA Basic ID ไม่ถูกต้อง — ใช้รูป @xxxx (ตัวอักษรอังกฤษ/ตัวเลข/จุด/ขีด)"));
        }
        // เปลี่ยน token ⇒ อาจเป็น OA ใหม่ ⇒ ใช้บอทที่ถามได้ข้างบน (รอบ 212 แพ็ก B — รู้ทันทีว่าเปลี่ยน OA หรือแค่ออก token ใหม่) ·
        // ถามไม่ได้ ⇒ ลืม bot userId เดิม (webhook ครั้งถัดไปประทับใหม่ + ตรวจการสลับ · ลิงก์ของ OA เดิมตัดสินด้วย scope จนกว่าจะรู้)
        if (tokenChanged) s.LineOaDestination = fetchedBot;
        // ฝ่ายค้าน (B+D) ข้อ 2: เปลี่ยน token/secret แล้ว ⇒ ปัญหา "secret ของ OA อื่น" ที่จดไว้ต้องตรวจใหม่จาก webhook ครั้งถัดไป
        if (tokenChanged || secretChanged || tokenCleared || secretCleared) s.LineWebhookDestinationMismatch = null;
        s.UpdatedAt = DateTime.UtcNow;
        // Reset configured flag after change — admin must re-test
        s.LineConfigured = false;
        await _db.SaveChangesAsync();
        var released = releaseQuiet ? await ReleaseQuietDeferredAsync(companyId) : 0;

        var message = "บันทึกการตั้งค่า LINE เรียบร้อย" + (released > 0 ? $" · การ์ด LINE ที่รอชั่วโมงเงียบ {released} รายการจะส่งรอบถัดไป" : "");
        var relinkNote = await RecordOaSwitchAsync(companyId, before);
        if (relinkNote != null) message += " · " + relinkNote;
        return Ok(new ApiResponse<LineConfigResponse>(true, await BuildAsync(companyId, s), message));
    }

    /// <summary>รอบ 260-4 (ฝ่ายค้าน L5): แถวรอส่งของบริษัทนี้ที่ถูกเลื่อนด้วยชั่วโมงเงียบ ⇒ ถึงเวลาส่งเดี๋ยวนี้ (แบบมีเงื่อนไขคำสั่งเดียว — แถวที่ตัวส่งจองไปแล้วไม่ถูกแตะ)</summary>
    private Task<int> ReleaseQuietDeferredAsync(Guid companyId)
    {
        var now = DateTime.UtcNow;
        return _db.LineQueues
            .Where(q => q.CompanyId == companyId && q.Status == ContactLineChannel.StatusPending && q.RuleCode == LineQuietHours.RuleCode && q.ScheduledFor > now)
            .ExecuteUpdateAsync(x => x.SetProperty(q => q.ScheduledFor, now).SetProperty(q => q.UpdatedAt, (DateTime?)now));
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
    [Accounting.Filters.RejectApiKey("ทดสอบส่ง LINE")]   // รอบ 258 (A-L9): คู่กับ PUT — ส่งข้อความในนามบริษัท + ประทับ "ตั้งค่าแล้ว"
    public async Task<ActionResult<ApiResponse<LineTestResult>>> Test(
        Guid companyId, [FromBody] TestLineConfigRequest req)
    {
        // รอบ 259 (A-M6): เลือกปลายทางที่ระบบรู้จัก (ไม่ต้องพิมพ์ C…) หรือพิมพ์เอง — ตัวตัดสินเดียว LineOaHealth.TestTarget
        // รอบ 260-4 (260-4-3): ปลายทางของผู้ติดต่อที่ผูกกับ OA ของบริษัท (key "ep:…") ⇒ หาใหม่ในบริษัทนี้เท่านั้น (ตัวเดียวกับรายการ — LineTestTargets.ResolveAsync)
        // ฝ่ายค้าน 260-4 H1: ตัวตัดสินผู้รับเดียว (ความยินยอม · ผู้ติดต่อไม่ถูกลบ) · L2: ส่งถึงผู้รับของผู้ติดต่อ ⇒ audit + PiiAccessLog ก่อนส่ง
        string? to, targetProblem;
        var rct = HttpContext?.RequestAborted ?? default;
        if (LineOaHealth.ParseEndpointKey(req?.TargetKey) is Guid endpointId)
        {
            var hit = await LineTestTargets.ResolveAsync(_db, _secrets, companyId, endpointId, await PlatformGroupsAsync(rct), rct);
            (to, targetProblem) = (hit.To, hit.Problem);
            if (to != null && hit.ContactId is Guid contactId) await RecordContactTestAsync(companyId, endpointId, contactId, rct);
        }
        else
        {
            var current = await _db.CompanySettings.AsNoTracking().Where(x => x.CompanyId == companyId).Select(x => x.LineDefaultGroupId).FirstOrDefaultAsync();
            (to, targetProblem) = LineOaHealth.TestTarget(req?.TargetKey, req?.ToLineId, current);
        }
        if (to == null) return BadRequest(new ApiResponse<LineTestResult>(false, new LineTestResult(false, targetProblem!), targetProblem));
        var (ok, msg) = await _line.TestConfigAsync(companyId, to);
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

    /// <summary>รอบ 260-4 (team-A · คำตัดสิน 260-4-3): ปลายทางทดสอบที่ระบบรู้จัก — กลุ่มของทีมที่ตั้งไว้ + ปลายทาง LINE ของผู้ติดต่อที่ผูกกับ OA ของบริษัท
    /// (ชื่อผู้ติดต่อ/กลุ่ม · ไม่ส่ง LINE id ให้หน้าเว็บ — key เท่านั้น) · ด่านเดียวกับหน้าตั้งค่า LINE (แก้ค่าตั้งบริษัท + ห้ามคีย์) — GET line-config ไม่มีด่านสิทธิ์
    /// ⇒ รายชื่อผู้ติดต่อไม่อยู่ใน GET นั้น</summary>
    [HttpGet("test-targets")]
    [Accounting.Filters.RequirePermission(Accounting.Models.Constants.PermissionKeys.CompanySettingsEdit)]
    [Accounting.Filters.RejectApiKey("ดูปลายทางทดสอบ LINE")]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<LineTestTarget>>>> TestTargets(Guid companyId)
    {
        var rct = HttpContext?.RequestAborted ?? default;
        var targets = await LineTestTargets.ListAsync(_db, _secrets, companyId, await PlatformGroupsAsync(rct), rct);
        return Ok(new ApiResponse<IReadOnlyList<LineTestTarget>>(true, targets));
    }

    /// <summary>กลุ่ม LINE กลางของระบบ (ตัวตัดสินผู้รับตัดออก) — ไม่มีตัวอ่านค่าตั้ง OA กลาง (นอก DI) ⇒ ว่าง</summary>
    private async Task<IReadOnlyCollection<string>> PlatformGroupsAsync(CancellationToken ct)
        => _nextAcc == null ? Array.Empty<string>() : await _nextAcc.PlatformGroupIdsAsync(ct);

    /// <summary>รอบ 260-4 (ฝ่ายค้าน L2 · คำตัดสิน 260-4-10): ทดสอบส่งถึงผู้รับของผู้ติดต่อ = ใช้ LINE id ของบุคคลภายนอกในนามบริษัท ⇒
    /// audit hash chain หนึ่งแถว (ปลายทาง · ผู้กด) + PiiAccessLog (ม.37) ก่อนส่ง · ไม่มี LINE id ในบันทึก</summary>
    private async Task RecordContactTestAsync(Guid companyId, Guid endpointId, Guid contactId, CancellationToken ct)
    {
        var actorEmail = User?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? User?.Identity?.Name ?? "";
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserEmail = actorEmail, Action = Accounting.Models.Enums.AuditAction.Create, EntityType = LineOaHealth.TestAuditEntityType,
            EntityId = endpointId.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "LineTestToContact", endpointId, contactId, ruleCode = LineOaHealth.RuleCode }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        if (_pdpa != null && User != null)
            await _pdpa.LogPiiAccessAsync(companyId, JwtHelper.GetUserIdFromClaims(User), actorEmail, subjectType: "Contact", subjectId: contactId,
                fieldName: "LINE ของผู้รับ (ทดสอบส่ง)", operation: "Use", purpose: "ทดสอบการแจ้งเตือนทาง LINE ของบริษัท",
                ipAddress: HttpContext?.Connection?.RemoteIpAddress?.ToString(), userAgent: HttpContext?.Request?.Headers.UserAgent.ToString(), ct: ct);
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
    {
        // รอบ 257 C-H1: บริษัทผู้ให้บริการใช้ OA กลางอยู่แล้ว ⇒ หน้าเว็บซ่อนส่วน "OA ของร้านเอง" + แสดงคำอธิบายจากเซิร์ฟเวอร์
        var isPlatform = LineOwnOaGuard.IsPlatformCompany(companyId, await PlatformCompanyLookup.IdAsync(_db));
        return Build(s) with
        {
            IsPlatformCompany = isPlatform,
            PlatformOaNotice = LineOwnOaGuard.PlatformNotice(isPlatform),
            PlatformLegacyOwnOaNotice = LineOwnOaGuard.PlatformLegacyNotice(isPlatform, !string.IsNullOrEmpty(s.LineChannelAccessToken), !string.IsNullOrEmpty(s.LineChannelSecret)),
            CustomerChannel = _customerLinks == null ? null : await _customerLinks.GetChannelInfoAsync(companyId),
            RelinkInvite = _relink == null ? null : await _relink.GetProgressAsync(companyId),
            WebhookProblem = string.IsNullOrWhiteSpace(s.LineWebhookDestinationMismatch) ? null : CustomerRelinkInvitePlan.SecretOfOtherOaProblem,
            // ฝ่ายค้านรอบ 231 S1: ค่ากำกวม (base64 ถอดไม่ออก — ใช้เป็นค่าดิบ) ⇒ คำใบ้ให้กรอกใหม่ถ้าใช้ไม่ติด
            SecretHint = SecretWarnings.BuildHints(_secrets, ("Channel Access Token", s.LineChannelAccessToken), ("Channel Secret", s.LineChannelSecret)),
            // รอบ 232 (232-Notify): ไม่มี OA ของร้าน ⇒ แจ้งเตือนธุรกิจไปกระดิ่ง/อีเมล ไม่ถอยไป OA กลาง — บอกผู้ใช้ตรงนี้ (ห้าม fallback เงียบ)
            TenantLineNotice = NotificationAudiencePolicy.CompanyGroupNotice(
                s.LineEnabled && _secrets.IsUsable(s.LineChannelAccessToken), !string.IsNullOrWhiteSpace(s.LineDefaultGroupId)),
            // รอบ 259 (A-M5 · A-L3): ความพร้อมจริง (webhook เข้าจริง · Basic ID · ผลทดสอบตามสถานะทดสอบ ไม่ใช่ธงที่รีเซ็ตตอนบันทึก)
            Health = WithChatSlipRow(LineOaHealth.Rows(s.LineEnabled, _secrets.IsUsable(s.LineChannelAccessToken), _secrets.IsUsable(s.LineChannelSecret),
                s.LineLastWebhookAt, !string.IsNullOrWhiteSpace(s.LineWebhookDestinationMismatch), s.LineOaBasicId,
                s.LineLastTestedAt, s.LineLastTestStatus, isPlatform), s, isPlatform),
            QuietHoursNote = LineQuietHours.Sentence(s.LineQuietHoursEnabled, s.LineQuietStartHour, s.LineQuietEndHour),
        } with
        {
            // รอบ 262 (คำตัดสิน 262-1-7): บริษัทผู้ให้บริการ — OA กลางคือ OA ของบริษัทสำหรับผู้ติดต่อ ⇒ ประโยค + แถวความพร้อมจากเซิร์ฟเวอร์ (ไม่อ้างส่วนที่ซ่อน)
            ContactSendNotice = ProviderCentralOa.ContactSendNotice(isPlatform, isPlatform ? await CentralBasicIdAsync() : null),
            ProviderLineReadiness = isPlatform ? await ProviderReadinessAsync(companyId) : Array.Empty<LineOaHealth.Row>(),
        };
    }

    private async Task<string?> CentralBasicIdAsync() => _nextAcc == null ? null : (await _nextAcc.GetAsync()).BasicId;

    /// <summary>รอบ 262: ข้อเท็จจริงของแถวความพร้อม (OA กลาง · ลูกค้าที่ผูก LINE ผ่าน OA กลาง · ค่าตั้งการแจ้งค่าบริการ) → ตัวตัดสินเดียว <c>ProviderCentralOa.ReadinessRows</c></summary>
    private async Task<IReadOnlyList<LineOaHealth.Row>> ProviderReadinessAsync(Guid companyId)
    {
        var nx = _nextAcc == null ? null : await _nextAcc.GetAsync();
        var bound = await _db.ContactNotifyEndpoints.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.OaScope == NotifyEndpointKind.ScopeNextAcc && e.UnlinkedAt == null && !e.IsDeleted
                && (e.Kind == NotifyEndpointKind.LineUser || e.Kind == NotifyEndpointKind.LineGroup))
            .Select(e => e.ContactId).Distinct().CountAsync();
        var contacts = await _db.Contacts.AsNoTracking().CountAsync(c => c.CompanyId == companyId && !c.IsDeleted);
        var policy = await Accounting.Services.Implementations.PlatformBillingSettingsService.LoadPolicyAsync(_db);
        // ฝ่ายค้าน L2 (262-1-14): Bot User ID ที่ตั้งไว้ต้องตรงบอทของ token (ตั้งผิด ⇒ ผูกทุกครั้งตอบ "เปลี่ยนบัญชี") — ถาม LINE ด้วยตัวอ่านเดียวกับด่าน 257-1-7
        string? fetchedBot = null;
        if (nx?.HasToken == true && _relink != null && !string.IsNullOrWhiteSpace(nx.BotUserId))
        {
            try { fetchedBot = await _relink.FetchBotUserIdAsync(nx.AccessToken!); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _logger?.LogWarning(ex, "อ่าน Bot User ID ของ OA กลางจาก LINE ไม่ได้ (แถวความพร้อม)"); }
        }
        return ProviderCentralOa.ReadinessRows(true, new ProviderCentralOa.ReadinessFacts(nx?.HasToken == true, nx?.HasSecret == true, nx?.Problem, nx?.BasicId,
            bound, contacts, policy.ReminderLineEnabled, policy.ReminderEmailEnabled) { CentralBotConfigured = nx?.BotUserId, CentralBotFetched = fetchedBot });
    }

    /// <summary>รอบ 260: แถวความพร้อม "รับสลิปจากแชต" ต่อท้าย (ตัวตัดสินเดียว LineChatSlipRule.ReadinessRow) · บริษัทผู้ให้บริการไม่มี OA ของร้าน = ไม่มีแถว</summary>
    private IReadOnlyList<LineOaHealth.Row> WithChatSlipRow(IReadOnlyList<LineOaHealth.Row> rows, CompanySettings s, bool isPlatform)
    {
        if (isPlatform) return rows;
        var oaReady = CompanyLineReadiness.Evaluate(s.LineEnabled, _secrets.IsUsable(s.LineChannelAccessToken), _secrets.IsUsable(s.LineChannelSecret)).Ready;
        return rows.Append(LineChatSlipRule.ReadinessRow(s.LineChatSlipIntakeEnabled, oaReady,
            s.LineLastWebhookAt != null && string.IsNullOrWhiteSpace(s.LineWebhookDestinationMismatch), _slipReader?.IsAvailable == true)).ToList();
    }

    private static LineConfigResponse Build(CompanySettings s) => new(
        Enabled: s.LineEnabled,
        HasAccessToken: !string.IsNullOrEmpty(s.LineChannelAccessToken),
        HasChannelSecret: !string.IsNullOrEmpty(s.LineChannelSecret),
        DefaultGroupId: s.LineDefaultGroupId,
        Configured: s.LineConfigured,
        LastTestedAt: s.LineLastTestedAt,
        LastTestStatus: s.LineLastTestStatus,
        OaBasicId: s.LineOaBasicId,
        QuietHoursEnabled: s.LineQuietHoursEnabled,
        QuietStartHour: s.LineQuietStartHour,
        QuietEndHour: s.LineQuietEndHour,
        ChatSlipIntake: s.LineChatSlipIntakeEnabled);
}
