using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// Daily job: nudges AccountSubscription owners ahead of their plan expiring,
/// then flips Status to Expired once the EndDate (+ grace period for the
/// expiry email) has passed. Uses ExpiryRemindersSentMask + LastExpiryReminderAt
/// on the account row so a restart / crash doesn't re-send the same warning.
///
/// Reminder buckets (bitmask):
///   1 = 7-day warning
///   2 = 3-day warning
///   4 = 1-day warning
///   8 = "your plan has expired" notification
/// </summary>
public class AccountPlanExpiryReminderJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AccountPlanExpiryReminderJob> _logger;
    private static readonly TimeSpan Cycle = TimeSpan.FromHours(6);

    public AccountPlanExpiryReminderJob(IServiceScopeFactory scopeFactory, ILogger<AccountPlanExpiryReminderJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait a minute on boot so app finishes warming + migrations apply.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnce(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "AccountPlanExpiryReminderJob cycle failed"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var email = scope.ServiceProvider.GetService<IEmailService>();
        if (email == null) return;

        var now = DateTime.UtcNow;
        // Pull anything potentially in scope — Active / PastDue / Trial in the
        // next 8 days, plus any already past EndDate that we haven't expired
        // yet.
        var horizon = now.AddDays(8);
        var rows = await db.AccountSubscriptions
            .Include(a => a.Owner)
            .Include(a => a.PlanTemplate)
            .Where(a => !a.IsDeleted
                && (a.Status == SubscriptionStatus.Active
                    || a.Status == SubscriptionStatus.PastDue
                    || a.Status == SubscriptionStatus.Trial)
                && a.EndDate <= horizon)
            .ToListAsync(ct);

        foreach (var a in rows)
        {
            if (ct.IsCancellationRequested) break;
            try { await ProcessOne(db, email, a, now, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "Reminder failed for AccountSub {Id}", a.Id); }
        }
    }

    private async Task ProcessOne(AccountingDbContext db, IEmailService email, AccountSubscription a, DateTime now, CancellationToken ct)
    {
        var daysLeft = (int)Math.Ceiling((a.EndDate - now).TotalDays);
        // Bucket selection — only fire the highest-priority bucket we haven't
        // sent yet so the owner doesn't get 3 emails in 7 days.
        int bucket = 0;
        string? subject = null; string? body = null;
        var manageUrl = "/pages/account-subscription.html";
        var planName = a.PlanTemplate?.Name ?? "Plan";

        if (daysLeft <= 0 && (a.ExpiryRemindersSentMask & 8) == 0)
        {
            bucket = 8;
            subject = $"⏰ Account Plan ({planName}) ของคุณหมดอายุแล้ว";
            body = $"<p>เรียน คุณ{a.Owner.FullName}</p>"
                 + $"<p>Account Plan <strong>{planName}</strong> ของคุณหมดอายุเมื่อวันที่ <strong>{a.EndDate:yyyy-MM-dd}</strong></p>"
                 + $"<p>ระบบจะให้คุณใช้งานต่อในช่วง Grace Period {a.GracePeriodDays} วัน · "
                 + $"หลังจากนั้นบริษัทใต้ Plan จะกลายเป็นโหมด readonly (ดูได้ บันทึกไม่ได้)</p>"
                 + $"<p>โปรดต่ออายุที่หน้าจัดการ License เพื่อใช้งานบริษัทใต้ Plan ต่อเนื่อง</p>";
            // Schedule the per-company status update to "Expired" on Subscription
            // rows under this account — they're already gracefully degraded via
            // the resolver, but flagging the status flag makes admin dashboards
            // show the right state.
            a.Status = SubscriptionStatus.Expired;
        }
        else if (daysLeft <= 1 && daysLeft > 0 && (a.ExpiryRemindersSentMask & 4) == 0)
        {
            bucket = 4;
            subject = $"⚠️ Account Plan ({planName}) จะหมดอายุพรุ่งนี้";
            body = $"<p>เรียน คุณ{a.Owner.FullName}</p>"
                 + $"<p>Account Plan <strong>{planName}</strong> จะหมดอายุ <strong>พรุ่งนี้</strong> ({a.EndDate:yyyy-MM-dd})</p>"
                 + $"<p>เพื่อให้บริษัทใต้ Plan ทุกแห่งใช้งานต่อเนื่อง — โปรดต่ออายุก่อนหมดอายุ</p>";
        }
        else if (daysLeft <= 3 && daysLeft > 1 && (a.ExpiryRemindersSentMask & 2) == 0)
        {
            bucket = 2;
            subject = $"📅 Account Plan ({planName}) จะหมดอายุใน 3 วัน";
            body = $"<p>เรียน คุณ{a.Owner.FullName}</p>"
                 + $"<p>Account Plan <strong>{planName}</strong> ของคุณจะหมดอายุในวันที่ <strong>{a.EndDate:yyyy-MM-dd}</strong></p>"
                 + $"<p>เหลือเวลาอีก {daysLeft} วัน — แนะนำให้ต่ออายุล่วงหน้าเพื่อกัน Service หยุดชะงัก</p>";
        }
        else if (daysLeft <= 7 && daysLeft > 3 && (a.ExpiryRemindersSentMask & 1) == 0)
        {
            bucket = 1;
            subject = $"📅 Account Plan ({planName}) จะหมดอายุใน {daysLeft} วัน";
            body = $"<p>เรียน คุณ{a.Owner.FullName}</p>"
                 + $"<p>Account Plan <strong>{planName}</strong> ของคุณจะหมดอายุในวันที่ <strong>{a.EndDate:yyyy-MM-dd}</strong></p>"
                 + $"<p>เหลือเวลา {daysLeft} วัน — โปรดต่ออายุเพื่อให้ License คุ้มครองบริษัทใต้ Plan ต่อ</p>";
        }

        if (bucket == 0 || subject == null || body == null) return;

        // Stamp first, then send — better to risk a missed email than a spam.
        a.ExpiryRemindersSentMask |= bucket;
        a.LastExpiryReminderAt = now;
        a.UpdatedAt = now;

        // Audit row so admin can see what we did. SubscriptionId is required
        // — use any company subscription under the account as anchor; if none
        // attached we record a row with a synthetic Guid to satisfy the FK
        // (skipped here since SubscriptionId is required and we don't want to
        // fake one). Instead, write the audit at AccountSubscription level via
        // Notes only — keep this best-effort.
        var anchorSubId = await db.Subscriptions
            .Where(s => s.AccountSubscriptionId == a.Id && !s.IsDeleted)
            .Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct);
        if (anchorSubId.HasValue)
        {
            db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = anchorSubId.Value,
                AccountSubscriptionId = a.Id,
                Action = $"AccountPlanReminder.{bucket}",
                ToStatus = a.Status,
                Notes = $"Reminder sent: {subject} (daysLeft={daysLeft})",
                PerformedBy = "system:reminder-job"
            });
        }

        await db.SaveChangesAsync(ct);

        // Send after the save so a transient SMTP failure doesn't leave us in
        // a "sent but un-stamped" state where the next cycle resends.
        try
        {
            await email.SendAsync(a.Owner.Email, subject, body);
            _logger.LogInformation("Sent Account Plan reminder bucket={Bucket} to {Email} for AccountSub {Id}",
                bucket, a.Owner.Email, a.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reminder email send failed for AccountSub {Id}; mask already set", a.Id);
        }
    }
}
