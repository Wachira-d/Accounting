using System.Net.Mail;
using System.Net;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
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

    public NotificationEngine(AccountingDbContext db, ILineNotifyService line,
        IOrganizationService organization, ILogger<NotificationEngine> logger,
        ISecretProtector secrets)
    {
        _db = db;
        _line = line;
        _organization = organization;
        _logger = logger;
        _secrets = secrets;
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
            var settings = await _db.NotificationSettings
                .AsNoTracking()
                .Where(s => s.CompanyId == companyId && s.EventKey == eventKey
                    && (s.EnableSystem || s.EnableEmail || s.EnableLine))
                .ToListAsync();
            if (settings.Count == 0) return;

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
                    .ToDictionary(u => u.Id, u => (u.Email, u.FullName, u.LineUserId));

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
                    await DispatchEmailAsync(companyId, email!, fullName, context);

                if (e.Line && !supLine && !string.IsNullOrWhiteSpace(lineUserId))
                    await DispatchLineAsync(companyId, lineUserId!, context);
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

    private async Task DispatchEmailAsync(Guid companyId, string toEmail, string? toName, NotificationContext context)
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
            var actionLink = !string.IsNullOrEmpty(context.ActionUrl)
                ? $"<p><a href='{WebUtilityEncode(context.ActionUrl)}' style='display:inline-block;padding:8px 16px;background:#4F46E5;color:#fff;text-decoration:none;border-radius:4px'>ดูรายละเอียด</a></p>"
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

    private async Task DispatchLineAsync(Guid companyId, string lineUserId, NotificationContext context)
    {
        var text = $"{context.Title}\n\n{context.Message}";
        if (!string.IsNullOrEmpty(context.ActionUrl))
            text += $"\n\n🔗 {context.ActionUrl}";
        await _line.PushToUserAsync(companyId, lineUserId, text);
    }

    private static string WebUtilityEncode(string s) =>
        System.Net.WebUtility.HtmlEncode(s ?? "").Replace("&#10;", "<br>");
}
