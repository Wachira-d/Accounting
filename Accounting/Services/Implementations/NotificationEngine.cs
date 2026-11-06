using System.Net.Mail;
using System.Net;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

/// <summary>
/// Central omnichannel dispatcher. See <see cref="INotificationEngine"/>
/// for the contract. Lookup ordering:
///   1. NotificationSettings rows for (company, event) — gives the
///      set of (recipient role × enabled channels).
///   2. Resolve each role → list of Recipient (UserId-bound users +
///      direct Employee.LineId/Email for staff that aren't system users).
///   3. For each recipient, apply NotificationPreference suppressions
///      (only for user-bound recipients), then fire each enabled
///      channel. The actor (event trigger) is always excluded.
///
/// Every dispatch is wrapped in catch-all logging so a notification
/// failure can never roll back the surrounding business transaction.
/// </summary>
public class NotificationEngine : INotificationEngine
{
    private readonly AccountingDbContext _db;
    private readonly ILineNotifyService _line;
    private readonly IOrganizationService _organization;
    private readonly ILogger<NotificationEngine> _logger;
    private readonly ISecretProtector _secrets;
    /// <summary>รอบ 249: อ่าน <c>App:BaseUrl</c> (ผ่าน <see cref="SystemBaseUrl.ResolveAsync"/>) เพื่อทำลิงก์เต็มใน LINE/อีเมล · null (เทสต์ที่สร้างตรง) = อ่านค่าที่ผู้ดูแลตั้งอย่างเดียว</summary>
    private readonly IConfiguration? _config;

    public NotificationEngine(AccountingDbContext db, ILineNotifyService line,
        IOrganizationService organization, ILogger<NotificationEngine> logger,
        ISecretProtector secrets, IConfiguration? config = null)
    {
        _db = db;
        _line = line;
        _organization = organization;
        _logger = logger;
        _secrets = secrets;
        _config = config;
    }

    /// <summary>Unified target — UserId เมื่อปลายทางเป็น user ในระบบ,
    /// หรือ direct Email/LineUserId เมื่อปลายทางเป็นพนักงาน "ตรง" ที่ไม่ได้
    /// ผูกบัญชี user (เช่นพนักงานทั่วไปที่มีแค่ LINE ID ในระบบ HR).</summary>
    private sealed record Recipient(
        Guid? UserId,
        string? Email,
        string? LineUserId,
        string? FullName);

    public async Task DispatchAsync(Guid companyId, string eventKey, NotificationContext context)
    {
        try
        {
            // รอบ 249: อ่าน "ทุกแถว" ของคีย์ (รวมแถวที่ปิดทุกช่อง) — เดิมกรองเฉพาะแถวที่เปิดบางช่อง ⇒ บริษัทที่เอาติ๊กค่าตั้งต้นออกแล้วบันทึก
            // (หน้าเก็บแถวปิดทุกช่อง · หน้าแสดง "ปิด") ถูกตีความว่า "ยังไม่ตั้ง" แล้วได้ DefaultMatrix (กระดิ่ง + กลุ่ม LINE) กลับมาเงียบ ๆ = ปิดไม่ได้
            var savedRows = await _db.NotificationSettings
                .AsNoTracking()
                .Where(s => s.CompanyId == companyId && s.EventKey == eventKey)
                .ToListAsync();
            var settings = savedRows
                .Where(s => s.EnableSystem || s.EnableEmail || s.EnableLine || s.EnableLineGroup)
                .ToList();
            // ถ้า caller ระบุ RecipientUserId override + ไม่มี NotificationSettings
            // ใน event นี้ → ใช้ default channels (System+Email) สำหรับ user
            // คนนั้น แทนที่จะ no-op. ใช้กับ approval/subscription event
            // ที่ recipient เป็น user รู้ตัวอยู่แล้ว ไม่ใช่ role.
            if (settings.Count == 0 && context.RecipientUserId.HasValue)
            {
                await DispatchDirectAsync(companyId, eventKey, context.RecipientUserId.Value, context, await ExternalLinkAsync(context));
                return;
            }
            // รอบ 232 (232-Notify): คีย์ที่ย้ายจากประกาศกลุ่ม LINE เดิม — บริษัทยังไม่ตั้ง ⇒ ค่าตั้งต้นจากตัวตัดสินเดียว
            // (กระดิ่งถึงเจ้าของ + กลุ่ม LINE ของบริษัทเมื่อผูกไว้แล้ว) · คีย์อื่น = ว่าง = ไม่ส่ง (พฤติกรรมเดิม)
            // "ยังไม่ตั้ง" = ไม่มีแถวของคีย์นี้เลย — เกณฑ์เดียวกับที่หน้าตั้งค่าใช้แสดงป้าย "ค่าตั้งต้นของระบบ" (NotificationConfigService.GetCompanySettingsAsync)
            if (savedRows.Count == 0)
            {
                settings = NotificationAudiencePolicy.DefaultMatrix(eventKey, await CompanyLineGroupConfiguredAsync(companyId))
                    .Select(d => new NotificationSetting
                    {
                        CompanyId = companyId, EventKey = eventKey, RecipientRole = d.RecipientRole,
                        EnableSystem = d.EnableSystem, EnableEmail = d.EnableEmail, EnableLine = d.EnableLine, EnableLineGroup = d.EnableLineGroup,
                    })
                    .ToList();
            }
            if (settings.Count == 0) return;

            // รอบ 249: ลิงก์ของช่องที่ออกนอกแอป (LINE · อีเมล) — คำนวณครั้งเดียวต่อเหตุการณ์ · กระดิ่งยังใช้ path สัมพัทธ์ (นำทางในแอป)
            var link = settings.Any(s => s.EnableEmail || s.EnableLine || s.EnableLineGroup) ? await ExternalLinkAsync(context) : null;

            // ช่อง "กลุ่ม LINE ของบริษัท" — หนึ่งครั้งต่อเหตุการณ์ (ไม่ใช่ต่อผู้รับ) · OA ของบริษัทเท่านั้น · ไม่มี OA ⇒ ผลบอกเหตุ (ไม่ถอยไปกลุ่มกลาง)
            if (settings.Any(s => s.EnableLineGroup))
                await DispatchCompanyGroupAsync(companyId, eventKey, context, link);

            // recipientKey → effective (channels + Recipient). Key combines
            // UserId.ToString() for system users, or "email:" / "line:"
            // prefixes for direct-employee targets — so the same employee
            // resolved by multiple roles dedupes correctly.
            var effective = new Dictionary<string, (Recipient R, bool Sys, bool Email, bool Line)>();

            foreach (var s in settings)
            {
                var recipients = await ResolveRoleAsync(companyId, s.RecipientRole, context);
                foreach (var r in recipients)
                {
                    if (r.UserId.HasValue && context.ActorUserId == r.UserId) continue;
                    var key = r.UserId?.ToString()
                        ?? (r.LineUserId != null ? $"line:{r.LineUserId}" : null)
                        ?? (r.Email != null ? $"email:{r.Email}" : null);
                    if (key == null) continue;
                    var prev = effective.TryGetValue(key, out var v)
                        ? v
                        : (R: r, Sys: false, Email: false, Line: false);
                    effective[key] = (
                        prev.R,
                        prev.Sys   || s.EnableSystem,
                        prev.Email || s.EnableEmail,
                        prev.Line  || s.EnableLine);
                }
            }
            if (effective.Count == 0) return;

            // Personal suppressions only apply to user-bound recipients.
            var userIds = effective.Values
                .Where(v => v.R.UserId.HasValue)
                .Select(v => v.R.UserId!.Value)
                .ToList();
            var prefMap = userIds.Count == 0
                ? new Dictionary<Guid, NotificationPreference>()
                : (await _db.NotificationPreferences
                    .AsNoTracking()
                    .Where(p => p.CompanyId == companyId && p.EventKey == eventKey
                        && userIds.Contains(p.UserId))
                    .ToListAsync()).ToDictionary(p => p.UserId);

            // Refresh user contact info (email/LineUserId can change after roles resolved).
            var users = userIds.Count == 0
                ? new Dictionary<Guid, (string? Email, string? FullName, string? LineUserId)>()
                : (await _db.Users.AsNoTracking()
                    .Where(u => userIds.Contains(u.Id) && u.Status != UserStatus.Inactive)
                    .Select(u => new { u.Id, u.Email, u.FullName, u.LineUserId })
                    .ToListAsync())
                    .ToDictionary(u => u.Id, u => (
                        Email: (string?)u.Email,
                        FullName: (string?)u.FullName,
                        LineUserId: u.LineUserId));

            foreach (var (_, e) in effective)
            {
                var r = e.R;
                string? email = r.Email;
                string? lineUserId = r.LineUserId;
                string? fullName = r.FullName;
                if (r.UserId.HasValue)
                {
                    if (!users.TryGetValue(r.UserId.Value, out var u)) continue;
                    email = u.Email ?? email;
                    lineUserId = u.LineUserId ?? lineUserId;
                    fullName = u.FullName ?? fullName;
                }

                var supSys = false; var supEmail = false; var supLine = false;
                if (r.UserId.HasValue && prefMap.TryGetValue(r.UserId.Value, out var pp))
                {
                    supSys = pp.SuppressSystem;
                    supEmail = pp.SuppressEmail;
                    supLine = pp.SuppressLine;
                }

                if (e.Sys && !supSys && r.UserId.HasValue)
                    await DispatchSystemAsync(r.UserId.Value, companyId, context);

                if (e.Email && !supEmail && !string.IsNullOrWhiteSpace(email))
                    await DispatchEmailAsync(companyId, email!, fullName, context, link);

                if (e.Line && !supLine && !string.IsNullOrWhiteSpace(lineUserId))
                    await DispatchLineAsync(companyId, lineUserId!, context, eventKey, link);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NotificationEngine.Dispatch failed for {EventKey} (company {CompanyId})",
                eventKey, companyId);
        }
    }

    /// <summary>Resolve a logical recipient role to concrete recipients.
    /// Empty list means "no one is registered for that role" — engine
    /// silently skips. Errors are logged and swallowed so one bad role
    /// doesn't block the others. Returns Recipient objects so we can
    /// notify employees who have Email/LineId but no system User account.</summary>
    private async Task<List<Recipient>> ResolveRoleAsync(Guid companyId, string role, NotificationContext ctx)
    {
        try
        {
            switch (role)
            {
                case NotificationRecipientRoles.Requester:
                    if (!ctx.RequesterEmployeeId.HasValue) return new();
                    var emp = await _db.Set<Employee>()
                        .Where(e => e.Id == ctx.RequesterEmployeeId.Value && e.CompanyId == companyId)
                        .Select(e => new { e.UserId, e.Email, e.LineId,
                            FullName = (e.TitleTh ?? "") + e.FirstNameTh + " " + e.LastNameTh })
                        .FirstOrDefaultAsync();
                    if (emp == null) return new();
                    return new List<Recipient> { new(emp.UserId, emp.Email, emp.LineId, emp.FullName.Trim()) };

                case NotificationRecipientRoles.DirectManager:
                    if (!ctx.RequesterEmployeeId.HasValue) return new();
                    var info = await _organization.GetDirectManagerInfoAsync(companyId, ctx.RequesterEmployeeId.Value);
                    return await ResolveManagerEmployeeAsync(companyId, info.ManagerUserId, info.ManagerEmployeeId);

                case NotificationRecipientRoles.DepartmentHead:
                    if (!ctx.RequesterEmployeeId.HasValue) return new();
                    var head = await _organization.GetDirectManagerInfoAsync(companyId, ctx.RequesterEmployeeId.Value);
                    return await ResolveManagerEmployeeAsync(companyId, null, head.DepartmentHeadEmployeeId);

                case NotificationRecipientRoles.Owner:
                    return (await _db.Set<CompanyUser>()
                        .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner)
                        .Select(cu => cu.UserId)
                        .ToListAsync())
                        .Select(uid => new Recipient(uid, null, null, null))
                        .ToList();

                case NotificationRecipientRoles.HrAdmin:
                    return (await ResolveByPermissionOrRoleAsync(companyId, PermissionKeys.HrAdmin, UserRole.SystemAdmin))
                        .Select(uid => new Recipient(uid, null, null, null))
                        .ToList();

                case NotificationRecipientRoles.Accounting:
                    return (await ResolveByPermissionOrRoleAsync(companyId, PermissionKeys.AccountingView, UserRole.Accountant))
                        .Select(uid => new Recipient(uid, null, null, null))
                        .ToList();

                default:
                    _logger.LogWarning("Unknown recipient role '{Role}'", role);
                    return new();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ResolveRoleAsync({Role}) failed", role);
            return new();
        }
    }

    /// <summary>Resolve manager — preferring system User (so they get bell)
    /// but falling back to direct Employee.Email/LineId when manager has
    /// no user account.</summary>
    private async Task<List<Recipient>> ResolveManagerEmployeeAsync(Guid companyId, Guid? userId, Guid? employeeId)
    {
        if (!employeeId.HasValue && !userId.HasValue) return new();
        if (employeeId.HasValue)
        {
            var mgr = await _db.Set<Employee>()
                .Where(e => e.Id == employeeId.Value && e.CompanyId == companyId)
                .Select(e => new { e.UserId, e.Email, e.LineId,
                    FullName = (e.TitleTh ?? "") + e.FirstNameTh + " " + e.LastNameTh })
                .FirstOrDefaultAsync();
            if (mgr != null)
                return new List<Recipient> { new(mgr.UserId ?? userId, mgr.Email, mgr.LineId, mgr.FullName.Trim()) };
        }
        return new List<Recipient> { new(userId, null, null, null) };
    }

    private async Task<List<Guid>> ResolveByPermissionOrRoleAsync(Guid companyId, string permissionKey, UserRole role)
    {
        var byPermission = await _db.Set<CompanyUser>()
            .Where(cu => cu.CompanyId == companyId
                && cu.CompanyRoleId != null
                && _db.Set<CompanyRolePermission>().Any(p =>
                    p.CompanyRoleId == cu.CompanyRoleId
                    && p.MenuItemId == permissionKey
                    && p.CanAccess))
            .Select(cu => cu.UserId)
            .ToListAsync();
        var byRole = await _db.Set<CompanyUser>()
            .Where(cu => cu.CompanyId == companyId && cu.Role == role)
            .Select(cu => cu.UserId)
            .ToListAsync();
        return byPermission.Concat(byRole).Distinct().ToList();
    }

    /// <summary>Direct user dispatch — bypass NotificationSettings (role-based)
    /// สำหรับ event ที่ recipient เป็น user รู้ตัวอยู่แล้ว (Approval/Subscription).
    /// ยัง apply per-user preference suppression + check user active.</summary>
    private async Task DispatchDirectAsync(Guid companyId, string eventKey,
        Guid recipientUserId, NotificationContext context, string? link)
    {
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == recipientUserId && u.Status != UserStatus.Inactive)
            .Select(u => new { u.Id, u.Email, u.FullName, u.LineUserId })
            .FirstOrDefaultAsync();
        if (user == null) return;
        if (context.ActorUserId == recipientUserId) return;   // ไม่ notify ตัวเอง

        var pp = await _db.NotificationPreferences.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.EventKey == eventKey
                && p.UserId == recipientUserId);
        var supSys = pp?.SuppressSystem ?? false;
        var supEmail = pp?.SuppressEmail ?? false;

        if (!supSys)
            await DispatchSystemAsync(recipientUserId, companyId, context);
        if (!supEmail && !string.IsNullOrWhiteSpace(user.Email))
            await DispatchEmailAsync(companyId, user.Email!, user.FullName, context, link);
    }

    // ===== Channel dispatchers =====

    private async Task DispatchSystemAsync(Guid userId, Guid companyId, NotificationContext context)
    {
        var n = new Notification
        {
            UserId = userId,
            CompanyId = companyId,
            Type = context.BellType,
            Channel = NotificationChannel.InApp,
            Title = context.Title,
            Message = context.Message,
            ActionUrl = context.ActionUrl,
            EntityType = context.EntityType,
            EntityId = context.EntityId,
        };
        _db.Set<Notification>().Add(n);
        await _db.SaveChangesAsync();
    }

    private async Task DispatchEmailAsync(Guid companyId, string toEmail, string? toName, NotificationContext context, string? link)
    {
        try
        {
            var s = await _db.Set<CompanySettings>().AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId);
            if (s == null || !s.EmailConfigured
                || string.IsNullOrEmpty(s.EmailSmtpHost) || string.IsNullOrEmpty(s.EmailFromAddress))
            {
                _logger.LogInformation("Email channel skipped — company {CompanyId} has no SMTP configured", companyId);
                return;
            }
            // รอบ 249: href ต้องเป็น URL เต็ม (NotificationLink) — path สัมพัทธ์ในอีเมลกดไม่ได้ · ไม่รู้โดเมนของระบบ ⇒ ไม่ใส่ปุ่ม
            var actionLink = !string.IsNullOrEmpty(link)
                ? $"<p><a href='{WebUtilityEncode(link)}' style='display:inline-block;padding:8px 16px;background:#4F46E5;color:#fff;text-decoration:none;border-radius:4px'>ดูรายละเอียด</a></p>"
                : "";
            var body = $@"<div style='font-family:Tahoma,sans-serif;font-size:14px;color:#111;max-width:560px'>
                <h3 style='margin:0 0 12px;color:#4F46E5'>{WebUtilityEncode(context.Title)}</h3>
                <p style='white-space:pre-wrap'>{WebUtilityEncode(context.Message)}</p>
                {actionLink}
                <hr style='border:none;border-top:1px solid #e5e7eb;margin:16px 0'>
                <p style='font-size:11px;color:#94a3b8'>การแจ้งเตือนจากระบบ NextAcc — ตั้งค่าการแจ้งเตือนได้ที่หน้าโปรไฟล์</p>
            </div>";
            using var msg = new MailMessage
            {
                From = new MailAddress(s.EmailFromAddress, s.EmailFromName ?? "NextAcc"),
                Subject = context.Title,
                Body = body,
                IsBodyHtml = true,
            };
            msg.To.Add(new MailAddress(toEmail, toName ?? toEmail));
            using var client = new SmtpClient(s.EmailSmtpHost, s.EmailSmtpPort)
            {
                EnableSsl = s.EmailSmtpUseSsl,
                Credentials = !string.IsNullOrEmpty(s.EmailSmtpUsername)
                    ? new NetworkCredential(s.EmailSmtpUsername, _secrets.Unprotect(s.EmailSmtpPassword) ?? "")
                    : null,
            };
            await client.SendMailAsync(msg);
        }
        catch (Exception ex) { _logger.LogError(ex, "Email dispatch to {Email} failed", toEmail); }
    }

    /// <summary>รอบ 212 C (บั๊กแฝง §0 ของ NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN): ผู้รับของ engine = ผู้ใช้ ERP (<c>User.LineUserId</c> จาก "ผูก 123456") หรือ
    /// พนักงาน HR (<c>Employee.LineId</c> จาก "สลิป 123456") — ทั้งคู่ผูกผ่าน webhook ของ <b>OA กลาง</b>เท่านั้น ⇒ push ต้องออก OA กลางแบบระบุชัด ·
    /// เดิม <c>PushToUserAsync(companyId, …)</c> เลือก OA ของร้านเมื่อร้านเปิด LINE ⇒ userId ข้าม provider ⇒ ล้มทุกฉบับเงียบ ๆ ·
    /// แจ้งเตือนรายเหตุการณ์<b>ไม่นับ</b>โควตาข้อความช่องกลาง (§10) · ล้ม ⇒ log พร้อมเหตุ (ช่องอื่นของผู้รับยังทำงานตามเดิม)</summary>
    private async Task DispatchLineAsync(Guid companyId, string lineUserId, NotificationContext context, string eventKey, string? link)
    {
        // รอบ 232 (232-Notify): ผู้รับตามบทบาทของบริษัทนี้ = สมาชิกของผู้เช่า (TenantMember) ผ่านบอทกลางที่เขาผูกเอง — ตัดสินก่อนยิงทุกครั้ง
        var route = NotificationAudiencePolicy.LineRoute(NotificationAudiencePolicy.Classify(eventKey), LineOaKind.NextAccOa, LineTargetKind.TenantMember, companyOaConfigured: false);
        if (!route.Allowed)
        {
            _logger.LogWarning("NotificationEngine: นโยบายผู้รับกั้น LINE 1:1 ของ {EventKey} (company {CompanyId}) [{Rule}]: {Reason}", eventKey, companyId, route.RuleCode, route.Reason);
            return;
        }
        var r = await _line.TryPushViaNextAccAsync(lineUserId, new[] { LineText(context, link) });
        if (!r.Ok)
            _logger.LogWarning("NotificationEngine: LINE ถึงพนักงานไม่สำเร็จ (company {CompanyId}): {Error}", companyId, r.Error);
    }

    /// <summary>รอบ 232: ช่อง "กลุ่ม LINE ของบริษัท" — ตัวตัดสิน OA/ผู้รับอยู่ใน <c>LineNotifyService.TryPushCompanyGroupAsync</c> (OA ของบริษัทเท่านั้น) ·
    /// ถูกนโยบายกั้น (ไม่มี OA) = log Information ครั้งเดียวต่อเหตุการณ์ (ไม่ spam — หน้าตั้งค่าบอกผู้ใช้อยู่แล้ว) · ล้มจริง = Warning</summary>
    private async Task DispatchCompanyGroupAsync(Guid companyId, string eventKey, NotificationContext context, string? link)
    {
        var audience = NotificationAudiencePolicy.Classify(eventKey);
        if (audience != NotificationAudience.TenantBusiness)
        {
            _logger.LogWarning("NotificationEngine: {EventKey} ไม่ใช่เหตุการณ์ธุรกิจของผู้เช่า — ไม่ประกาศเข้ากลุ่ม LINE ของบริษัท {CompanyId}", eventKey, companyId);
            return;
        }
        var r = await _line.TryPushCompanyGroupAsync(companyId, LineText(context, link));
        if (r.Ok) return;
        if (r.RuleCode != null)
            _logger.LogInformation("NotificationEngine: กลุ่ม LINE ของบริษัท {CompanyId} ไม่ส่ง ({EventKey}) [{Rule}]: {Reason}", companyId, eventKey, r.RuleCode, r.Error);
        else
            _logger.LogWarning("NotificationEngine: ประกาศกลุ่ม LINE ของบริษัท {CompanyId} ไม่สำเร็จ ({EventKey}): {Error}", companyId, eventKey, r.Error);
    }

    /// <summary>ข้อความ LINE — <paramref name="link"/> = URL เต็มจาก <see cref="ExternalLinkAsync"/> · null ⇒ ไม่มีบรรทัดลิงก์
    /// (รอบ 249: เดิมต่อ <c>context.ActionUrl</c> ซึ่งเป็น path สัมพัทธ์ ⇒ "🔗 /pages/documents.html?id=…" กดไม่ได้ใน LINE)</summary>
    private static string LineText(NotificationContext context, string? link)
    {
        var text = $"{context.Title}\n\n{context.Message}";
        if (!string.IsNullOrEmpty(link))
            text += $"\n\n🔗 {link}";
        return text;
    }

    /// <summary>รอบ 249: ลิงก์เต็มของช่องที่ออกนอกแอป — origin จาก <see cref="SystemBaseUrl.ResolveAsync"/> (ตัวตั้งตัวเดียว) + <see cref="NotificationLink.Absolute"/> ·
    /// ไม่ได้ตั้ง URL ของระบบ / อ่านค่าตั้งไม่ได้ ⇒ null (ข้อความยังส่ง แค่ไม่มีลิงก์ — log ครั้งเดียวต่อเหตุการณ์)</summary>
    private async Task<string?> ExternalLinkAsync(NotificationContext context)
    {
        if (string.IsNullOrWhiteSpace(context.ActionUrl)) return null;
        try
        {
            var link = NotificationLink.Absolute(await SystemBaseUrl.ResolveAsync(_db, _config), context.ActionUrl);
            if (link == null)
                _logger.LogInformation("NotificationEngine: ไม่ใส่ลิงก์ในช่อง LINE/อีเมล — ยังไม่ได้ตั้ง URL ของระบบ (แอดมิน → ตั้งค่าระบบ / App:BaseUrl) หรือ ActionUrl ไม่ใช่ path ภายใน");
            return link;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NotificationEngine: อ่าน URL ของระบบไม่สำเร็จ — ส่งแจ้งเตือนโดยไม่มีลิงก์");
            return null;
        }
    }

    /// <summary>บริษัทผูกกลุ่ม LINE ของตัวเองไว้แล้วไหม (ตัวตัดสิน <see cref="NotificationAudiencePolicy.CompanyLineGroupConfigured"/>) — ใช้เลือกค่าตั้งต้นของคีย์ที่ย้ายมา</summary>
    private async Task<bool> CompanyLineGroupConfiguredAsync(Guid companyId)
    {
        var s = await _db.Set<CompanySettings>().AsNoTracking().Where(x => x.CompanyId == companyId)
            .Select(x => new { x.LineEnabled, x.LineChannelAccessToken, x.LineDefaultGroupId }).FirstOrDefaultAsync();
        return s != null && NotificationAudiencePolicy.CompanyLineGroupConfigured(s.LineEnabled, s.LineChannelAccessToken, s.LineDefaultGroupId);
    }

    private static string WebUtilityEncode(string s) =>
        System.Net.WebUtility.HtmlEncode(s ?? "").Replace("&#10;", "<br>");
}
