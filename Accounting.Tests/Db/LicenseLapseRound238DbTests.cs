using System.Security.Claims;
using Accounting.Controllers;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Background;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Platform;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 238 (คำตัดสิน 236-3) — License หมดอายุ/ระงับบน PostgreSQL จริง (ข้ามเมื่อไม่มีฐาน) ·
/// งานเตือน: วันหมดอายุ ⇒ License เป็น PastDue (ผ่อนผันมีจริง) · แถวบริษัทไม่ถูกแตะ (เลิก cascade) · แจ้งเจ้าของบริษัทใต้ License ·
/// ถอด: แพ็กเกจที่จ่ายไว้กลับมา · แถวที่หมดอายุซื้อได้ทันที · ผู้ถือ (ไม่ใช่เจ้าของ) ถอดได้ / สมาชิกอื่น 403 / audit chain ·
/// migration: แถวที่ถูก cascade คืนสถานะ · แถวที่หมดอายุจริงไม่ถูกแตะ · รันซ้ำไม่เปลี่ยนอะไร
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class LicenseLapseRound238DbTests
{
    private readonly ITestOutputHelper _out;
    public LicenseLapseRound238DbTests(ITestOutputHelper output) => _out = output;

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private sealed record Seed(Guid HolderId, Guid OwnerId, Guid MemberId, Guid CompanyId, Guid SubId, Guid LicenseId);

    /// <summary>ผู้ถือ License (ไม่ใช่เจ้าของบริษัท) + เจ้าของบริษัท + สมาชิกอื่น · License Enterprise · แถวบริษัท Pro จ่ายเอง (ค่าตั้งต้นยังไม่หมด)</summary>
    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, SubscriptionStatus licenseStatus, DateTime licenseEnd,
        SubscriptionStatus companyStatus = SubscriptionStatus.Active, DateTime? companyEnd = null)
    {
        var tpl = await db.PlanTemplates.FirstOrDefaultAsync(x => x.Plan == SubscriptionPlan.Enterprise && x.IsActive && !x.IsPermanentFree);
        if (tpl == null)
        {
            tpl = new PlanTemplate
            {
                Name = "แพ็กเกจทดสอบ 238 Enterprise", Plan = SubscriptionPlan.Enterprise, IsActive = true,
                MonthlyPrice = 833.33m, QuarterlyPrice = 2500m, SemiAnnualPrice = 5000m, AnnualPrice = 10000m,
                MaxUsers = 999, MaxCompanies = 10, MaxDocumentsPerMonth = 99999, MaxJournalEntriesPerMonth = 99999,
            };
            db.PlanTemplates.Add(tpl);
            await db.SaveChangesAsync();
        }
        var tag = Guid.NewGuid().ToString("N")[..6];
        var holder = new User { Email = $"lic238-{tag}@test.local", PasswordHash = "x", FullName = "ผู้ถือ License 238 " + tag };
        var owner = new User { Email = $"own238-{tag}@test.local", PasswordHash = "x", FullName = "เจ้าของบริษัท 238 " + tag };
        var member = new User { Email = $"mem238-{tag}@test.local", PasswordHash = "x", FullName = "สมาชิก 238 " + tag };
        db.Users.AddRange(holder, owner, member);
        var c = new Company { Name = "บริษัท ทดสอบ 238 " + tag, TaxId = "0105556000114" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = owner.Id, CompanyId = c.Id, Role = UserRole.Owner });
        db.CompanyUsers.Add(new CompanyUser { UserId = member.Id, CompanyId = c.Id, Role = UserRole.Accountant });
        var lic = new AccountSubscription
        {
            OwnerUserId = holder.Id, PlanTemplateId = tpl.Id, Status = licenseStatus, StartDate = licenseEnd.AddDays(-365), EndDate = licenseEnd,
            MaxCompanies = 10, MaxUsersPerCompany = 999, MaxDocumentsPerMonth = 99999, MaxJournalEntriesPerMonth = 99999,
            EnabledFeatures = tpl.EnabledFeatures, GracePeriodDays = 7,
        };
        db.AccountSubscriptions.Add(lic);
        await db.SaveChangesAsync();
        var sub = new Subscription
        {
            CompanyId = c.Id, Plan = SubscriptionPlan.Pro, Status = companyStatus, BillingCycle = BillingCycle.Monthly, Kind = SubscriptionKind.Paid,
            PricePerCycle = 490m, LastPaymentDate = DateTime.UtcNow.AddDays(-30),
            StartDate = DateTime.UtcNow.AddDays(-30), EndDate = companyEnd ?? DateTime.UtcNow.AddDays(60), UsageResetDate = DateTime.UtcNow.AddDays(20),
            AccountSubscriptionId = lic.Id,
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        return new Seed(holder.Id, owner.Id, member.Id, c.Id, sub.Id, lic.Id);
    }

    private static AccountSubscriptionController Controller(Accounting.Data.AccountingDbContext db, Guid userId)
        => new(db, new SubscriptionService(db, Notifications()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Email, $"u-{userId:N}@test.local"),
                    }, "Test")),
                },
            },
        };

    private static IEmailService Mailbox(List<(string To, string Subject)> sent)
        => F4Proxy<IEmailService>.Create((m, a) =>
        {
            if (m is nameof(IEmailService.SendAsync) or nameof(IEmailService.TrySendAsync))
            {
                lock (sent) sent.Add(((string)a[0]!, (string)a[1]!));
                return m == nameof(IEmailService.TrySendAsync) ? true : null;
            }
            return null;
        });

    private static AccountPlanExpiryReminderJob Job() => new(null!, NullLogger<AccountPlanExpiryReminderJob>.Instance);

    private static Task<AccountSubscription> LoadLicenseAsync(Accounting.Data.AccountingDbContext db, Guid id)
        => db.AccountSubscriptions.Include(a => a.Owner).Include(a => a.PlanTemplate).SingleAsync(a => a.Id == id);

    [Fact]
    public async Task R238_วันหมดอายุ_Licenseเป็นPastDue_แถวบริษัทไม่ถูกแตะ_ผ่อนผันใช้งานได้_พ้นผ่อนผันไม่ได้_แจ้งเจ้าของบริษัท()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var now = DateTime.UtcNow;
        var s = await SeedAsync(db, SubscriptionStatus.Active, now.AddHours(-1));
        var before = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == s.SubId);
        var sent = new List<(string To, string Subject)>();
        var job = Job();

        await job.ProcessOne(db, Mailbox(sent), await LoadLicenseAsync(db, s.LicenseId), now, CancellationToken.None);

        var lic = await db.AccountSubscriptions.AsNoTracking().SingleAsync(a => a.Id == s.LicenseId);
        Assert.Equal(SubscriptionStatus.PastDue, lic.Status);   // เดิม Expired ทันที (ผ่อนผัน 0 วัน)
        Assert.Equal(8 | 32, lic.ExpiryRemindersSentMask & (8 | 32));
        var after = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == s.SubId);
        Assert.Equal(before.Status, after.Status);   // แถวบริษัทไม่ถูกแตะ (เดิม cascade เป็น Expired)
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
        Assert.False(await db.SubscriptionHistories.AnyAsync(h => h.SubscriptionId == s.SubId && h.Action == "CascadeExpiredFromLicense"));
        var ownerEmail = await db.Users.Where(u => u.Id == s.OwnerId).Select(u => u.Email).SingleAsync();
        var holderEmail = await db.Users.Where(u => u.Id == s.HolderId).Select(u => u.Email).SingleAsync();
        Assert.Contains(sent, m => m.To == holderEmail);
        Assert.Contains(sent, m => m.To == ownerEmail && m.Subject.Contains("ใช้งานเต็มได้ถึง"));
        var memberEmail = await db.Users.Where(u => u.Id == s.MemberId).Select(u => u.Email).SingleAsync();
        Assert.DoesNotContain(sent, m => m.To == memberEmail);   // เฉพาะเจ้าของบริษัท

        var svc = new SubscriptionService(db, Notifications());
        var eff = await svc.GetEffectivePlanAsync(s.CompanyId);
        Assert.True(eff!.IsActive);   // ในผ่อนผัน = เขียนได้เต็ม
        Assert.True(eff.InGrace);

        // รอบซ้ำในวันเดียวกัน ⇒ ไม่ส่งซ้ำ
        var count = sent.Count;
        await job.ProcessOne(db, Mailbox(sent), await LoadLicenseAsync(db, s.LicenseId), now.AddHours(1), CancellationToken.None);
        Assert.Equal(count, sent.Count);

        // พ้นผ่อนผัน ⇒ Expired · ใช้งานไม่ได้ (อ่านอย่างเดียว) · แถวบริษัทยังไม่ถูกแตะ
        await job.ProcessOne(db, Mailbox(sent), await LoadLicenseAsync(db, s.LicenseId), now.AddDays(8), CancellationToken.None);
        Assert.Equal(SubscriptionStatus.Expired, (await db.AccountSubscriptions.AsNoTracking().SingleAsync(a => a.Id == s.LicenseId)).Status);
        db.ChangeTracker.Clear();
        Assert.False((await new SubscriptionService(db, Notifications()).GetEffectivePlanAsync(s.CompanyId))!.IsActive);
        Assert.Equal(before.Status, (await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == s.SubId)).Status);
    }

    [Fact]
    public async Task R238_ล่วงหน้า7วัน_แจ้งเจ้าของบริษัทครั้งเดียว_สถานะLicenseไม่เปลี่ยน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var now = DateTime.UtcNow;
        var s = await SeedAsync(db, SubscriptionStatus.Active, now.AddDays(6.5));
        var sent = new List<(string To, string Subject)>();
        await Job().ProcessOne(db, Mailbox(sent), await LoadLicenseAsync(db, s.LicenseId), now, CancellationToken.None);
        var lic = await db.AccountSubscriptions.AsNoTracking().SingleAsync(a => a.Id == s.LicenseId);
        Assert.Equal(SubscriptionStatus.Active, lic.Status);   // ทิศตรงข้าม: ก่อนวันหมดอายุไม่แตะสถานะ
        Assert.Equal(16, lic.ExpiryRemindersSentMask & 16);
        var ownerEmail = await db.Users.Where(u => u.Id == s.OwnerId).Select(u => u.Email).SingleAsync();
        Assert.Single(sent, m => m.To == ownerEmail);
        await Job().ProcessOne(db, Mailbox(sent), await LoadLicenseAsync(db, s.LicenseId), now.AddHours(6), CancellationToken.None);
        Assert.Single(sent, m => m.To == ownerEmail);
    }

    [Fact]
    public async Task R238_ถอด_แถวProที่จ่ายไว้กลับมามีผล_และมีauditchain()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, SubscriptionStatus.Expired, DateTime.UtcNow.AddDays(-30));
        var svc = new SubscriptionService(db, Notifications());
        Assert.False((await svc.GetEffectivePlanAsync(s.CompanyId))!.IsActive);   // License คุม (ไม่ถอยอัตโนมัติ)

        var preview = await Controller(db, s.OwnerId).DetachPreview(s.CompanyId);
        Assert.IsType<OkObjectResult>(preview.Result);

        var sent = new List<(string To, string Subject)>();
        var res = await Controller(db, s.OwnerId).Detach(new AccountSubscriptionController.AttachRequest(s.CompanyId), Mailbox(sent));
        Assert.IsType<OkObjectResult>(res.Result);
        Assert.Contains("หลังถอดจะใช้", ((res.Result as OkObjectResult)!.Value as ApiResponse<string>)!.Message);

        db.ChangeTracker.Clear();
        var eff = await new SubscriptionService(db, Notifications()).GetEffectivePlanAsync(s.CompanyId);
        Assert.Equal("Company", eff!.Source);
        Assert.Equal(SubscriptionPlan.Pro, eff.Plan);
        Assert.True(eff.IsActive);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityId == s.SubId.ToString() && a.NewValues!.Contains("DetachedFromLicense") && a.RowHash != null));
        var holderEmail = await db.Users.Where(u => u.Id == s.HolderId).Select(u => u.Email).SingleAsync();
        Assert.Contains(sent, m => m.To == holderEmail);   // แจ้งอีกฝ่าย (ผู้ถือ)
    }

    [Fact]
    public async Task R238_ถอดแถวที่หมดอายุแล้ว_พรีวิวบอกซื้อได้ทันที_และซื้อได้จริง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, SubscriptionStatus.Expired, DateTime.UtcNow.AddDays(-30),
            companyStatus: SubscriptionStatus.Expired, companyEnd: DateTime.UtcNow.AddDays(-10));
        Assert.False((await PlanIdentityReader.PurchaseVerdictAsync(db, s.CompanyId)).Allowed);   // ยังผูก ⇒ ซื้อไม่ได้ (236-3)
        var sub = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == s.SubId);
        var p = await PlanIdentityReader.DetachPreviewAsync(db, sub);
        Assert.False(p.Resumes);
        Assert.True(p.CanBuyNow);

        var res = await Controller(db, s.OwnerId).Detach(new AccountSubscriptionController.AttachRequest(s.CompanyId), null);
        Assert.IsType<OkObjectResult>(res.Result);
        db.ChangeTracker.Clear();
        Assert.True((await PlanIdentityReader.PurchaseVerdictAsync(db, s.CompanyId)).Allowed);
    }

    [Fact]
    public async Task R238_ผู้ถือที่ไม่ใช่เจ้าของถอดได้_สมาชิกอื่น403()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, SubscriptionStatus.Suspended, DateTime.UtcNow.AddDays(100));

        var denied = await Controller(db, s.MemberId).Detach(new AccountSubscriptionController.AttachRequest(s.CompanyId), null);
        Assert.Equal(403, (denied.Result as ObjectResult)?.StatusCode);
        var deniedPreview = await Controller(db, s.MemberId).DetachPreview(s.CompanyId);
        Assert.Equal(404, (deniedPreview.Result as ObjectResult)?.StatusCode);   // ฝ่ายค้าน S3: พรีวิวไม่มีสิทธิ์ = 404 (ไม่บอกว่ามีบริษัท)
        Assert.NotNull((await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == s.SubId)).AccountSubscriptionId);

        var sent = new List<(string To, string Subject)>();
        var ok = await Controller(db, s.HolderId).Detach(new AccountSubscriptionController.AttachRequest(s.CompanyId), Mailbox(sent));
        Assert.IsType<OkObjectResult>(ok.Result);
        db.ChangeTracker.Clear();
        Assert.Null((await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == s.SubId)).AccountSubscriptionId);
        Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityId == s.SubId.ToString() && a.NewValues!.Contains("LicenseHolder")));
        var ownerEmail = await db.Users.Where(u => u.Id == s.OwnerId).Select(u => u.Email).SingleAsync();
        Assert.Contains(sent, m => m.To == ownerEmail);   // แจ้งเจ้าของบริษัท
    }

    [Fact]
    public async Task R238_migration_แถวที่ถูกcascadeคืนสถานะ_แถวหมดอายุจริงไม่ถูกแตะ_Licenseในผ่อนผันเป็นPastDue_รันซ้ำไม่เปลี่ยน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var now = DateTime.UtcNow;
        // A: ถูก cascade (Active → Expired) ตามด้วยแถวเตือนของ License ที่ยึดแถวนี้เป็นที่เกาะ
        var a = await SeedAsync(db, SubscriptionStatus.Expired, now.AddDays(-2), companyStatus: SubscriptionStatus.Expired);
        db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = a.SubId, AccountSubscriptionId = a.LicenseId, Action = "CascadeExpiredFromLicense",
            FromStatus = SubscriptionStatus.Active, ToStatus = SubscriptionStatus.Expired, PerformedBy = "system:reminder-job", CreatedAt = now.AddDays(-2),
        });
        db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = a.SubId, AccountSubscriptionId = a.LicenseId, Action = "AccountPlanReminder.8",
            ToStatus = SubscriptionStatus.Expired, PerformedBy = "system:reminder-job", CreatedAt = now.AddDays(-2).AddSeconds(1),
        });
        // B: หมดอายุเองจริง (ประวัติล่าสุดไม่ใช่ cascade) — License หมดเกินผ่อนผันแล้ว
        var b = await SeedAsync(db, SubscriptionStatus.Expired, now.AddDays(-40), companyStatus: SubscriptionStatus.Expired);
        db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = b.SubId, AccountSubscriptionId = b.LicenseId, Action = "CascadeExpiredFromLicense",
            FromStatus = SubscriptionStatus.Active, ToStatus = SubscriptionStatus.Expired, PerformedBy = "system:reminder-job", CreatedAt = now.AddDays(-40),
        });
        db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = b.SubId, Action = "Expired", FromStatus = SubscriptionStatus.PastDue, ToStatus = SubscriptionStatus.Expired,
            PerformedBy = "admin", CreatedAt = now.AddDays(-5),
        });
        await db.SaveChangesAsync();

        // C (ฝ่ายค้าน B2): ถูก cascade แล้วถอดออกไปแล้ว + แถวหมดอายุจริง ⇒ คง Expired (อ่านอย่างเดียว · ซื้อได้) ไม่ปลุก
        var c = await SeedAsync(db, SubscriptionStatus.Expired, now.AddDays(-40), companyStatus: SubscriptionStatus.Expired, companyEnd: now.AddDays(-20));
        db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = c.SubId, AccountSubscriptionId = c.LicenseId, Action = "CascadeExpiredFromLicense",
            FromStatus = SubscriptionStatus.Active, ToStatus = SubscriptionStatus.Expired, PerformedBy = "system:reminder-job", CreatedAt = now.AddDays(-40),
        });
        (await db.Subscriptions.SingleAsync(x => x.Id == c.SubId)).AccountSubscriptionId = null;
        // D (ฝ่ายค้าน B1): แอดมินตั้ง License เป็น Expired เองในช่วงผ่อนผัน ⇒ migration ไม่พลิก
        var d = await SeedAsync(db, SubscriptionStatus.Expired, now.AddDays(-2));
        db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = d.SubId, AccountSubscriptionId = d.LicenseId, Action = "LicenseStatusChanged",
            FromStatus = SubscriptionStatus.Active, ToStatus = SubscriptionStatus.Expired, PerformedBy = "admin", CreatedAt = now.AddHours(-1),
        });
        await db.SaveChangesAsync();

        // ตัวซ่อม (สองส่วน) — ส่วนที่ DO-block ครั้งเดียวเรียก
        await db.Database.ExecuteSqlRawAsync(LicenseLapseMigration.RestoreCascadedSql());
        await db.Database.ExecuteSqlRawAsync(LicenseLapseMigration.GraceLicenseSql());
        db.ChangeTracker.Clear();

        Assert.Equal(SubscriptionStatus.Active, (await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == a.SubId)).Status);
        Assert.Equal(1, await db.SubscriptionHistories.CountAsync(h => h.SubscriptionId == a.SubId && h.Action == LicenseLapseMigration.RestoredAction));
        Assert.Equal(SubscriptionStatus.Expired, (await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == b.SubId)).Status);
        Assert.Equal(0, await db.SubscriptionHistories.CountAsync(h => h.SubscriptionId == b.SubId && h.Action == LicenseLapseMigration.RestoredAction));
        Assert.Equal(SubscriptionStatus.Expired, (await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == c.SubId)).Status);   // ถอดแล้ว + หมดอายุ ⇒ ไม่ปลุก
        Assert.Equal(SubscriptionStatus.PastDue, (await db.AccountSubscriptions.AsNoTracking().SingleAsync(x => x.Id == a.LicenseId)).Status);   // ในผ่อนผัน
        Assert.Equal(SubscriptionStatus.Expired, (await db.AccountSubscriptions.AsNoTracking().SingleAsync(x => x.Id == b.LicenseId)).Status);  // เกินผ่อนผัน
        Assert.Equal(SubscriptionStatus.Expired, (await db.AccountSubscriptions.AsNoTracking().SingleAsync(x => x.Id == d.LicenseId)).Status);  // แอดมินตั้งเอง ⇒ คงไว้
    }

    [Fact]
    public async Task R238_migration_ครั้งเดียว_มีmarkerแล้ว_บูตถัดไปไม่สแกนไม่เปลี่ยน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var now = DateTime.UtcNow;
        // บูตแรก (หรือบูตก่อนหน้าในฐานนี้) ⇒ มี marker
        foreach (var sql in LicenseLapseMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        // แถวที่ "จะเข้าเงื่อนไข" ถ้าสแกน — ต้องไม่ถูกแตะเพราะซ่อมไปแล้วครั้งเดียว
        var a = await SeedAsync(db, SubscriptionStatus.Expired, now.AddDays(-2), companyStatus: SubscriptionStatus.Expired);
        db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = a.SubId, AccountSubscriptionId = a.LicenseId, Action = "CascadeExpiredFromLicense",
            FromStatus = SubscriptionStatus.Active, ToStatus = SubscriptionStatus.Expired, PerformedBy = "system:reminder-job", CreatedAt = now.AddDays(-2),
        });
        await db.SaveChangesAsync();
        foreach (var sql in LicenseLapseMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        db.ChangeTracker.Clear();
        Assert.Equal(SubscriptionStatus.Expired, (await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == a.SubId)).Status);
        Assert.Equal(SubscriptionStatus.Expired, (await db.AccountSubscriptions.AsNoTracking().SingleAsync(x => x.Id == a.LicenseId)).Status);
        Assert.Equal(0, await db.SubscriptionHistories.CountAsync(h => h.SubscriptionId == a.SubId && h.Action == LicenseLapseMigration.RestoredAction));
    }

    [Fact]
    public async Task R238_B2_ถอดแถวActiveที่เลยวันหมดอายุ_ตั้งExpiredตรงกับพรีวิว_ไม่ถูกระงับเงียบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, SubscriptionStatus.Active, DateTime.UtcNow.AddDays(100),
            companyStatus: SubscriptionStatus.Active, companyEnd: DateTime.UtcNow.AddDays(-60));
        var pv = await Controller(db, s.OwnerId).DetachPreview(s.CompanyId);
        var pvText = System.Text.Json.JsonSerializer.Serialize(((pv.Result as OkObjectResult)!.Value as ApiResponse<object>)!.Data);
        Assert.Contains("\"resumes\":false", pvText);
        Assert.Contains("\"canBuyNow\":true", pvText);

        var res = await Controller(db, s.OwnerId).Detach(new AccountSubscriptionController.AttachRequest(s.CompanyId), null);
        Assert.IsType<OkObjectResult>(res.Result);
        db.ChangeTracker.Clear();
        var row = await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == s.SubId);
        Assert.Equal(SubscriptionStatus.Expired, row.Status);   // ไม่ใช่ Active ที่งานรอบถัดไปจะเดินถึง Suspended (403)
        Assert.True(await db.SubscriptionHistories.AnyAsync(h => h.SubscriptionId == s.SubId && h.Action == "DetachStatusSettled"
            && h.FromStatus == SubscriptionStatus.Active && h.ToStatus == SubscriptionStatus.Expired));

        // งานหมดอายุรอบถัดไปไม่แตะแถว Expired (ไม่เดินถึง Suspended)
        await new SubscriptionService(db, Notifications()).ProcessExpiredSubscriptionsAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(SubscriptionStatus.Expired, (await db.Subscriptions.AsNoTracking().SingleAsync(x => x.Id == s.SubId)).Status);
        Assert.True((await PlanIdentityReader.PurchaseVerdictAsync(db, s.CompanyId)).Allowed);
    }

    [Fact]
    public async Task R238_S3_ผู้ใช้นอกบริษัท_effective_พรีวิว_ถอด_ได้404()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db, SubscriptionStatus.Active, DateTime.UtcNow.AddDays(100));
        var outsider = new User { Email = $"out238-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "คนนอก 238" };
        db.Users.Add(outsider);
        await db.SaveChangesAsync();
        var svc = new SubscriptionService(db, Notifications());

        Assert.Equal(404, ((await Controller(db, outsider.Id).Effective(s.CompanyId, svc)).Result as ObjectResult)?.StatusCode);
        Assert.Equal(404, ((await Controller(db, outsider.Id).DetachPreview(s.CompanyId)).Result as ObjectResult)?.StatusCode);
        Assert.Equal(404, ((await Controller(db, outsider.Id).AttachPreview(s.CompanyId)).Result as ObjectResult)?.StatusCode);
        Assert.Equal(404, ((await Controller(db, outsider.Id).Detach(new AccountSubscriptionController.AttachRequest(s.CompanyId), null)).Result as ObjectResult)?.StatusCode);
        // บริษัทที่ไม่มีอยู่ = ผลเดียวกัน (ไม่บอกว่ามีบริษัท)
        Assert.Equal(404, ((await Controller(db, outsider.Id).DetachPreview(Guid.NewGuid())).Result as ObjectResult)?.StatusCode);

        // ทิศตรงข้าม: สมาชิก (ไม่ใช่เจ้าของ) และผู้ถือ License ดู effective ได้
        Assert.IsType<OkObjectResult>((await Controller(db, s.MemberId).Effective(s.CompanyId, svc)).Result);
        Assert.IsType<OkObjectResult>((await Controller(db, s.HolderId).Effective(s.CompanyId, svc)).Result);
    }

    [Fact]
    public async Task R238_S2_ผูกเข้าLicenseในช่วงผ่อนผัน_พรีวิวและผลตอบเตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        // License ของผู้ถือ (เป็นเจ้าของบริษัทใหม่ที่ยังไม่ผูก) · PastDue พ้นวันหมดอายุ 2 วัน
        var s = await SeedAsync(db, SubscriptionStatus.PastDue, DateTime.UtcNow.AddDays(-2));
        var c2 = new Company { Name = "บริษัท ทดสอบ 238 ผูกช่วงผ่อนผัน", TaxId = "0105556000114" };
        db.Companies.Add(c2);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = s.HolderId, CompanyId = c2.Id, Role = UserRole.Owner });
        db.Subscriptions.Add(new Subscription
        {
            CompanyId = c2.Id, Plan = SubscriptionPlan.FreeTrial, Status = SubscriptionStatus.Trial, BillingCycle = BillingCycle.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-1), EndDate = DateTime.UtcNow.AddDays(13), UsageResetDate = DateTime.UtcNow.AddDays(20),
        });
        await db.SaveChangesAsync();

        var pv = await Controller(db, s.HolderId).AttachPreview(c2.Id);
        var pvText = System.Text.Json.JsonSerializer.Serialize(((pv.Result as OkObjectResult)!.Value as ApiResponse<object>)!.Data);
        Assert.Contains("ช่วงผ่อนผัน", System.Text.RegularExpressions.Regex.Unescape(pvText));
        var res = await Controller(db, s.HolderId).Attach(new AccountSubscriptionController.AttachRequest(c2.Id));
        Assert.Contains("ช่วงผ่อนผัน", ((res.Result as OkObjectResult)!.Value as ApiResponse<string>)!.Message);
    }
}
