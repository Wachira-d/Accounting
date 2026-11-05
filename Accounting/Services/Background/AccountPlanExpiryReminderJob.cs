using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// Daily job: nudges AccountSubscription owners ahead of their plan expiring,
/// then moves Status through the grace period. Uses ExpiryRemindersSentMask + LastExpiryReminderAt
/// on the account row so a restart / crash doesn't re-send the same warning.
///
/// Reminder buckets (bitmask):
///   1 = 7-day warning
///   2 = 3-day warning
///   4 = 1-day warning
///   8 = "your plan has expired" notification
///   16 = รอบ 238: แจ้งเจ้าของบริษัทใต้ License (ไม่ใช่ผู้ถือ) ล่วงหน้า 7 วัน
///   32 = รอบ 238: แจ้งเจ้าของบริษัทใต้ License (ไม่ใช่ผู้ถือ) ในวันหมดอายุ
///
/// <para>รอบ 238 (คำตัดสิน 236-3): สถานะ License หลังวันหมดอายุมาจาก <c>PlanIdentityResolver.LicenseStatusAfter</c> ตัวเดียว
/// (ในระยะผ่อนผัน = PastDue เขียนได้เต็ม · พ้นผ่อนผัน = Expired) — เดิมตั้ง Expired ทันทีที่ถึงวันหมดอายุ ⇒ ผ่อนผัน 0 วันทั้งที่อีเมลสัญญา N วัน ·
/// <b>ไม่เขียนแถว Subscription ของบริษัทอีกแล้ว</b> (เดิม cascade เป็น Expired + ประวัติ <c>CascadeExpiredFromLicense</c> — ขัด 236-1/236-4: แถวบริษัทที่ License คุมไม่มีผล
/// ห้ามเดินสถานะ · ถอดออกแล้วแพ็กเกจที่จ่ายไว้ต้องกลับมา) · แถวที่ถูก cascade ไปแล้วคืนสถานะด้วย <c>Helpers/LicenseLapseMigration</c></para>
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
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "AccountPlanExpiryReminderJob cycle failed"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    /// <summary>กันสอง instance ทำงานรอบเดียวกันพร้อมกัน (ผลตรวจ F-09)
    ///
    /// <para>ใช้ <b>try</b> ไม่ใช่ wait — งานตามตารางที่อีกเครื่องกำลังทำอยู่
    /// การรอคือทำงานเดิมซ้ำเปล่า ๆ ข้ามไปรอบหน้าถูกกว่า</para></summary>
    private async Task RunGuardedAsync(CancellationToken ct)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await Accounting.Helpers.JobLock.RunExclusiveAsync(
            lockDb, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(AccountPlanExpiryReminderJob),
            () => RunOnce(ct), _logger, ct: ct);
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

        var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
        var jobStart = now;
        int processed = 0, mailSent = 0, mailFailed = 0;
        foreach (var a in rows)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var (sent, failed) = await ProcessOne(db, email, a, now, ct);
                processed++; mailSent += sent; mailFailed += failed;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Reminder failed for AccountSub {Id}", a.Id); }
        }
        // รอบ 232 (ฝ่ายค้าน B1 ข): แถว ExpiryNeedsReview ที่งานต่ออายุ/เตือน/ปิดใช้ข้าม — นับให้เห็นใน JobRun (ไม่ใช่แค่ LogWarning)
        var reviewSvc = scope.ServiceProvider.GetService<ISubscriptionService>();
        var needsReview = 0;
        if (reviewSvc != null) { try { needsReview = await reviewSvc.CountExpiryNeedsReviewAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "นับ ExpiryNeedsReview ไม่สำเร็จ"); } }
        if (recorder != null)
            // รอบ 238 ฝ่ายค้าน N2: อีเมลล้มต้องมีร่องรอยใน JobRun (ไม่ใช่แค่ LogWarning) · ล้ม ⇒ งานรอบนี้ไม่ "สำเร็จสะอาด"
            await recorder.RecordAsync("AccountPlanExpiryReminder", mailFailed == 0,
                $"ตรวจ {rows.Count} License, ประมวลผล {processed} · อีเมลส่งได้ {mailSent} ล้ม {mailFailed}" + (needsReview > 0 ? $" · ข้าม {needsReview} บริษัท (แผนจ่ายเงินไม่มีรอบ — รอแอดมินกำหนดรอบ)" : ""), processed, jobStart);

        // WP-A3: per-company subscription lifecycle — เดิมเป็น manual-only
        // (เรียกจาก AdminController เท่านั้น) ทำให้ Active→PastDue→Suspended
        // ไม่เคย flip เองถ้า admin ไม่กด. ผูกเข้ากับ job นี้ให้รันอัตโนมัติ
        // ทุกรอบ (6 ชม.) — idempotent อยู่แล้ว (เช็ค status ก่อนเปลี่ยน).
        var subscriptionService = scope.ServiceProvider.GetService<ISubscriptionService>();
        if (subscriptionService != null)
        {
            try { await subscriptionService.ProcessExpiredSubscriptionsAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "ProcessExpiredSubscriptionsAsync failed"); }
            try { await subscriptionService.ProcessSubscriptionNotificationsAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "ProcessSubscriptionNotificationsAsync failed"); }
            // รอบ 230 (ฝ่ายค้าน S3): รายการค่าแพ็กเกจที่มีคูปอง/ส่วนต่างค้างเกินระยะจอง ⇒ ยกเลิก + ปลดคูปอง (ไม่กินเพดานคนอื่น)
            try { await subscriptionService.ExpireStaleQuotedPaymentsAsync(); }
            catch (Exception ex) { _logger.LogError(ex, "ExpireStaleQuotedPaymentsAsync failed"); }
        }
    }

    /// <summary>License หนึ่งแถวต่อรอบ — internal ให้ Db test เรียกตรง (<c>LicenseLapseRound238DbTests</c>) · ผู้เรียกจริง: <see cref="RunOnce"/></summary>
    /// <returns>(อีเมลส่งได้, ล้ม) — ฝ่ายค้าน N2 ผู้เรียกรวมลง JobRun</returns>
    internal async Task<(int Sent, int Failed)> ProcessOne(AccountingDbContext db, IEmailService email, AccountSubscription a, DateTime now, CancellationToken ct)
    {
        var daysLeft = (int)Math.Ceiling((a.EndDate - now).TotalDays);
        // Bucket selection — only fire the highest-priority bucket we haven't
        // sent yet so the owner doesn't get 3 emails in 7 days.
        int bucket = 0;
        string? subject = null; string? body = null;
        var planName = a.PlanTemplate?.Name ?? "Plan";
        // รอบ 236 (ฝ่ายค้าน B1): ทางต่ออายุที่มีอยู่จริง — ข้อความเดียว (PlanIdentityResolver) · HtmlEncode เพราะชื่อแพ็กมาจากแอดมิน
        var howTo = System.Net.WebUtility.HtmlEncode(Accounting.Helpers.PlanIdentityResolver.LicenseRenewalHowTo(planName));
        var planHtml = System.Net.WebUtility.HtmlEncode(planName);

        // รอบ 238 (คำตัดสิน 236-3 ข้อ 1): สถานะหลังวันหมดอายุจากตัวตัดสินเดียว — ในระยะผ่อนผัน = PastDue (เขียนได้เต็ม) · พ้นผ่อนผัน = Expired ·
        // ไม่แตะแถว Subscription ของบริษัท (ข้อ 2 — เลิก cascade: แถวบริษัทที่ License คุมไม่มีผล · ถอดออกแล้วแพ็กเกจที่จ่ายไว้ต้องกลับมาตามเดิม)
        var fromStatus = a.Status;
        var nextStatus = Accounting.Helpers.PlanIdentityResolver.LicenseStatusAfter(a.Status, a.EndDate, a.GracePeriodDays, now);
        var statusChanged = nextStatus != fromStatus;
        if (statusChanged) a.Status = nextStatus;
        var graceEndText = Accounting.Helpers.PlatformPurchaseEmailText.BeDate(
            Accounting.Helpers.PlanIdentityResolver.LicenseGraceEndsAt(a.EndDate, a.GracePeriodDays));

        if (daysLeft <= 0 && (a.ExpiryRemindersSentMask & 8) == 0)
        {
            bucket = 8;
            subject = $"⏰ Account Plan ({planName}) ของคุณหมดอายุแล้ว";
            body = $"<p>เรียน คุณ{System.Net.WebUtility.HtmlEncode(a.Owner.FullName)}</p>"
                 + $"<p>Account Plan <strong>{planHtml}</strong> ของคุณหมดอายุเมื่อวันที่ <strong>{Accounting.Helpers.PlatformPurchaseEmailText.BeDate(a.EndDate)}</strong></p>"
                 + $"<p>บริษัทใต้ Plan ใช้งานได้เต็มต่อถึงสิ้นระยะผ่อนผัน <strong>{graceEndText}</strong> ({a.GracePeriodDays} วัน) · "
                 + "หลังจากนั้นเป็นโหมดอ่านอย่างเดียว (ดู/ส่งออกข้อมูล/สร้างไฟล์ยื่นแบบได้ · สร้าง/แก้ไขเอกสารไม่ได้)</p>"
                 + $"<p>โปรดต่ออายุเพื่อใช้งานบริษัทใต้ Plan ต่อเนื่อง</p>"
                 + $"<p>{howTo}</p>";   // รอบ 236 B1: ทางต่ออายุที่มีอยู่จริง
        }
        else if (daysLeft <= 1 && daysLeft > 0 && (a.ExpiryRemindersSentMask & 4) == 0)
        {
            bucket = 4;
            subject = $"⚠️ Account Plan ({planName}) จะหมดอายุพรุ่งนี้";
            body = $"<p>เรียน คุณ{a.Owner.FullName}</p>"
                 + $"<p>Account Plan <strong>{planName}</strong> จะหมดอายุ <strong>พรุ่งนี้</strong> ({a.EndDate:yyyy-MM-dd})</p>"
                 + $"<p>เพื่อให้บริษัทใต้ Plan ทุกแห่งใช้งานต่อเนื่อง — โปรดต่ออายุก่อนหมดอายุ</p>"
                 + $"<p>{howTo}</p>";   // รอบ 236 B1: ทางต่ออายุที่มีอยู่จริง
        }
        else if (daysLeft <= 3 && daysLeft > 1 && (a.ExpiryRemindersSentMask & 2) == 0)
        {
            bucket = 2;
            subject = $"📅 Account Plan ({planName}) จะหมดอายุใน 3 วัน";
            body = $"<p>เรียน คุณ{a.Owner.FullName}</p>"
                 + $"<p>Account Plan <strong>{planName}</strong> ของคุณจะหมดอายุในวันที่ <strong>{a.EndDate:yyyy-MM-dd}</strong></p>"
                 + $"<p>เหลือเวลาอีก {daysLeft} วัน — แนะนำให้ต่ออายุล่วงหน้าเพื่อกัน Service หยุดชะงัก</p>"
                 + $"<p>{howTo}</p>";   // รอบ 236 B1: ทางต่ออายุที่มีอยู่จริง
        }
        else if (daysLeft <= 7 && daysLeft > 3 && (a.ExpiryRemindersSentMask & 1) == 0)
        {
            bucket = 1;
            subject = $"📅 Account Plan ({planName}) จะหมดอายุใน {daysLeft} วัน";
            body = $"<p>เรียน คุณ{a.Owner.FullName}</p>"
                 + $"<p>Account Plan <strong>{planName}</strong> ของคุณจะหมดอายุในวันที่ <strong>{a.EndDate:yyyy-MM-dd}</strong></p>"
                 + $"<p>เหลือเวลา {daysLeft} วัน — โปรดต่ออายุเพื่อให้ License คุ้มครองบริษัทใต้ Plan ต่อ</p>"
                 + $"<p>{howTo}</p>";   // รอบ 236 B1: ทางต่ออายุที่มีอยู่จริง
        }

        // รอบ 238 (236-3 ข้อ 5): แจ้งเจ้าของบริษัทใต้ License (ไม่รวมผู้ถือ — ได้อีเมลข้างบนแล้ว) ล่วงหน้า 7 วัน (บิต 16) และวันหมดอายุ (บิต 32) ·
        // ข้อความเดียว PlanIdentityResolver.LicenseLapseCompanyNotice (วันสิ้นผ่อนผัน พ.ศ. · อ่าน/ส่งออก/ยื่นแบบได้เสมอ · ทางถอดออก)
        int coBucket = 0;
        if (daysLeft <= 0 && (a.ExpiryRemindersSentMask & 32) == 0) coBucket = 32;
        else if (daysLeft <= 7 && daysLeft > 0 && (a.ExpiryRemindersSentMask & 16) == 0) coBucket = 16;
        var coMails = new List<(string To, string Subject, string Body)>();
        if (coBucket != 0)
        {
            var holderId = a.OwnerUserId;
            var owners = await (from s in db.Subscriptions
                                join cu in db.CompanyUsers on s.CompanyId equals cu.CompanyId
                                where s.AccountSubscriptionId == a.Id && !s.IsDeleted
                                      && cu.Role == UserRole.Owner && cu.UserId != holderId
                                select new { CompanyName = s.Company.Name, cu.User.Email, cu.User.FullName })
                               .ToListAsync(ct);
            var holderName = string.IsNullOrWhiteSpace(a.Owner.FullName) ? a.Owner.Email : a.Owner.FullName;
            foreach (var o in owners.Where(o => !string.IsNullOrWhiteSpace(o.Email)))
            {
                var (coSubject, lines) = Accounting.Helpers.PlanIdentityResolver.LicenseLapseCompanyNotice(
                    o.CompanyName, planName, holderName, a.EndDate, a.GracePeriodDays, expiryDay: coBucket == 32, daysLeft);
                var coBody = $"<p>เรียน คุณ{System.Net.WebUtility.HtmlEncode(o.FullName)}</p>"
                           + string.Concat(lines.Select(l => "<p>" + System.Net.WebUtility.HtmlEncode(l) + "</p>"));
                coMails.Add((o.Email, coSubject, coBody));
            }
        }

        if (!statusChanged && coBucket == 0 && (bucket == 0 || subject == null || body == null)) return (0, 0);

        // Stamp first, then send — better to risk a missed email than a spam.
        if (bucket != 0)
        {
            a.ExpiryRemindersSentMask |= bucket;
            a.LastExpiryReminderAt = now;
        }
        if (coBucket != 0) a.ExpiryRemindersSentMask |= coBucket;
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
            if (statusChanged)
                db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    // รอบ 238: ประวัติสถานะของ License (ไม่ใช่สถานะแถวบริษัท — แถวบริษัทไม่ถูกแตะ) · ชื่อขึ้นต้น "License" ⇒ LicenseLapseMigration ไม่นับเป็นการเปลี่ยนสถานะแถวบริษัท
                    SubscriptionId = anchorSubId.Value,
                    AccountSubscriptionId = a.Id,
                    Action = "LicenseStatusAfterGrace",
                    FromStatus = fromStatus,
                    ToStatus = a.Status,
                    Notes = $"License ({planName}) หมดอายุ {a.EndDate:yyyy-MM-dd} · ผ่อนผันถึง {graceEndText} ⇒ สถานะ License {fromStatus} → {a.Status} (แถวบริษัทไม่ถูกแตะ)",
                    PerformedBy = "system:reminder-job"
                });
            if (bucket != 0)
                db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = anchorSubId.Value,
                    AccountSubscriptionId = a.Id,
                    Action = $"AccountPlanReminder.{bucket}",
                    ToStatus = a.Status,
                    Notes = $"Reminder sent: {subject} (daysLeft={daysLeft})",
                    PerformedBy = "system:reminder-job"
                });
            if (coBucket != 0)
                db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = anchorSubId.Value,
                    AccountSubscriptionId = a.Id,
                    Action = $"AccountPlanReminder.{coBucket}",
                    ToStatus = a.Status,
                    Notes = $"แจ้งเจ้าของบริษัทใต้ License {coMails.Count} ราย (daysLeft={daysLeft})",
                    PerformedBy = "system:reminder-job"
                });
        }

        await db.SaveChangesAsync(ct);

        // Send after the save so a transient SMTP failure doesn't leave us in
        // a "sent but un-stamped" state where the next cycle resends.
        // รอบ 238 ฝ่ายค้าน N2: นับผลการส่ง — ล้มต้องมีร่องรอยในประวัติของ License (และ JobRun ผ่านค่าที่คืน) ไม่ใช่แค่ LogWarning
        int sent = 0, failed = 0;
        var failedTo = new List<string>();
        if (bucket != 0 && subject != null && body != null)
        {
            try
            {
                await email.SendAsync(a.Owner.Email, subject, body);
                sent++;
                _logger.LogInformation("Sent Account Plan reminder bucket={Bucket} to {Email} for AccountSub {Id}",
                    bucket, a.Owner.Email, a.Id);
            }
            catch (Exception ex)
            {
                failed++; failedTo.Add("ผู้ถือ License");
                _logger.LogWarning(ex, "Reminder email send failed for AccountSub {Id}; mask already set", a.Id);
            }
        }
        foreach (var m in coMails)
        {
            try { await email.SendAsync(m.To, m.Subject, m.Body); sent++; }
            catch (Exception ex)
            {
                failed++; failedTo.Add("เจ้าของบริษัท");
                _logger.LogWarning(ex, "แจ้งเจ้าของบริษัทใต้ License {Id} ไม่สำเร็จ; mask already set", a.Id);
            }
        }
        if (failed > 0 && anchorSubId.HasValue)
        {
            db.SubscriptionHistories.Add(new SubscriptionHistory
            {
                SubscriptionId = anchorSubId.Value,
                AccountSubscriptionId = a.Id,
                Action = "LicenseReminderDeliveryFailed",
                ToStatus = a.Status,
                Notes = $"ส่งอีเมลเตือน License ไม่สำเร็จ {failed} จาก {sent + failed} ฉบับ ({string.Join(", ", failedTo.Distinct())}) — ไม่ส่งซ้ำอัตโนมัติ (มาสก์ประทับแล้ว) · โปรดแจ้งเอง",
                PerformedBy = "system:reminder-job"
            });
            await db.SaveChangesAsync(ct);
        }
        return (sent, failed);
    }
}
