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
///   2. Resolve each role → list of UserIds (via OrganizationService
///      for DirectManager / DepartmentHead, CompanyUser / CompanyRole
///      for Owner / HrAdmin / Accounting).
///   3. For each (userId, channels) pair, apply that user's
///      NotificationPreference suppressions, then fire each enabled
///      channel. The actor (event trigger) is always excluded — no
///      self-notifications.
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

            // recipientUserId → effective channels (after merging multiple roles).
            var effective = new Dictionary<Guid, (bool Sys, bool Email, bool Line)>();

            foreach (var s in settings)
            {
                var userIds = await ResolveRoleAsync(companyId, s.RecipientRole, context);
                foreach (var uid in userIds)
                {
                    if (context.ActorUserId == uid) continue;  // never notify the actor
                    var prev = effective.TryGetValue(uid, out var v) ? v : (Sys: false, Email: false, Line: false);
                    effective[uid] = (
                        prev.Sys   || s.EnableSystem,
                        prev.Email || s.EnableEmail,
                        prev.Line  || s.EnableLine);
                }
            }
            if (effective.Count == 0) return;

            // Personal suppressions: a row in NotificationPreferences
            // for (company, user, event) flips off the matching channels.
            var prefs = await _db.NotificationPreferences
                .AsNoTracking()
                .Where(p => p.CompanyId == companyId && p.EventKey == eventKey
                    && effective.Keys.Contains(p.UserId))
                .ToListAsync();
            var prefMap = prefs.ToDictionary(p => p.UserId);

            // Pre-load each recipient's Email / LineUserId in one query.
            var userIdsList = effective.Keys.ToList();
            var users = await _db.Users
                .AsNoTracking()
                .Where(u => userIdsList.Contains(u.Id) && u.Status != UserStatus.Inactive)
                .Select(u => new { u.Id, u.Email, u.FullName, u.LineUserId })
                .ToDictionaryAsync(u => u.Id);

            foreach (var (userId, ch) in effective)
            {
                if (!users.TryGetValue(userId, out var u)) continue;  // inactive / deleted
                var supSys   = prefMap.TryGetValue(userId, out var pp) && pp.SuppressSystem;
                var supEmail = pp != null && pp.SuppressEmail;
                var supLine  = pp != null && pp.SuppressLine;

                if (ch.Sys && !supSys)
                    await DispatchSystemAsync(userId, companyId, context);

                if (ch.Email && !supEmail && !string.IsNullOrWhiteSpace(u.Email))
                    await DispatchEmailAsync(companyId, u.Email!, u.FullName, context);

                if (ch.Line && !supLine && !string.IsNullOrWhiteSpace(u.LineUserId))
                    await DispatchLineAsync(u.LineUserId!, context);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NotificationEngine.Dispatch failed for {EventKey} (company {CompanyId})",
                eventKey, companyId);
        }
    }

    /// <summary>Resolve a logical recipient role to concrete user IDs.
    /// Empty list means "no one is registered for that role" — engine
    /// silently skips. Errors are logged and swallowed so one bad role
    /// doesn't block the others.</summary>
    private async Task<List<Guid>> ResolveRoleAsync(Guid companyId, string role, NotificationContext ctx)
    {
        try
        {
            switch (role)
            {
                case NotificationRecipientRoles.Requester:
                    if (!ctx.RequesterEmployeeId.HasValue) return new();
                    var requesterUserId = await _db.Employees
                        .Where(e => e.Id == ctx.RequesterEmployeeId.Value && e.CompanyId == companyId)
                        .Select(e => e.UserId)
                        .FirstOrDefaultAsync();
                    return requesterUserId.HasValue ? new List<Guid> { requesterUserId.Value } : new();

                case NotificationRecipientRoles.DirectManager:
                    if (!ctx.RequesterEmployeeId.HasValue) return new();
                    var info = await _organization.GetDirectManagerInfoAsync(companyId, ctx.RequesterEmployeeId.Value);
                    return info.ManagerUserId.HasValue ? new List<Guid> { info.ManagerUserId.Value } : new();

                case NotificationRecipientRoles.DepartmentHead:
                    if (!ctx.RequesterEmployeeId.HasValue) return new();
                    var head = await _organization.GetDirectManagerInfoAsync(companyId, ctx.RequesterEmployeeId.Value);
                    if (!head.DepartmentHeadEmployeeId.HasValue) return new();
                    var headUserId = await _db.Employees
                        .Where(e => e.Id == head.DepartmentHeadEmployeeId.Value)
                        .Select(e => e.UserId)
                        .FirstOrDefaultAsync();
                    return headUserId.HasValue ? new List<Guid> { headUserId.Value } : new();

                case NotificationRecipientRoles.Owner:
                    return await _db.Set<CompanyUser>()
                        .Where(cu => cu.CompanyId == companyId && cu.Role == UserRole.Owner)
                        .Select(cu => cu.UserId)
                        .ToListAsync();

                case NotificationRecipientRoles.HrAdmin:
                    // Granted via a CompanyRolePermission row with perm:HR.Admin,
                    // or anyone with UserRole.SystemAdmin in this tenant.
                    return await ResolveByPermissionOrRoleAsync(companyId, PermissionKeys.HrAdmin,
                        UserRole.SystemAdmin);

                case NotificationRecipientRoles.Accounting:
                    return await ResolveByPermissionOrRoleAsync(companyId, PermissionKeys.AccountingView,
                        UserRole.Accountant);

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

    private async Task<List<Guid>> ResolveByPermissionOrRoleAsync(Guid companyId, string permissionKey, UserRole role)
    {
        // Union of:
        //   (a) users with companyRole that has the permission key granted
        //   (b) users whose system UserRole matches
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

    private async Task DispatchEmailAsync(Guid companyId, string toEmail, string toName, NotificationContext context)
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
            msg.To.Add(new MailAddress(toEmail, toName));
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

    private async Task DispatchLineAsync(string lineUserId, NotificationContext context)
    {
        var text = $"{context.Title}\n\n{context.Message}";
        if (!string.IsNullOrEmpty(context.ActionUrl))
            text += $"\n\n🔗 {context.ActionUrl}";
        await _line.PushToUserAsync(lineUserId, text);
    }

    private static string WebUtilityEncode(string s) =>
        System.Net.WebUtility.HtmlEncode(s ?? "").Replace("&#10;", "<br>");
}
