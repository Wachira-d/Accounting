using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 231 (erp-review/2026-11-03/LICENSE_USAGE_OVERAGE_AUDIT.md · คำตัดสินข้อ 231-1..6) — บน PostgreSQL จริง:
/// <list type="bullet">
/// <item>ตารางพฤติกรรม §B.4 ผ่าน <see cref="OverageService"/>: ไม่มีแถวนโยบาย = ล็อก · แอดมินตั้งราคา + ผู้เช่าเปิด (ยอมรับฉบับ) ⇒ ผ่าน + UsageEvent <c>overage.*</c> ราคา snapshot ·
/// ถึงเพดานเงิน ⇒ ล็อก · ขั้น 1 ค้างชำระ ⇒ ล็อก/ผู้เช่าเปิดเองไม่ได้/ชำระครบ ⇒ ปลด · ไม่มีราคา ⇒ เปิดไม่ได้ · ใบที่กฎหมายบังคับในโหมดล็อก = มิเตอร์ราคา 0</item>
/// <item>snapshot ก่อนขึ้นเดือน (<see cref="UsageSnapshotService.WriteRolloverAsync"/>): ค่าปิดเดือน = ตัวนับก่อนล้าง · Daily ไม่ทับ Rollover · Capacity เก็บค่าสูงสุด</item>
/// </list>
/// แยกข้อมูลต่อเทสต์ด้วยบริษัทใหม่ · ราคาส่วนเกินผูกแม่แบบแพ็กเกจเฉพาะของเทสต์ (ตารางราคาเป็นของทั้งฐาน — แถว "ทุกแพ็กเกจ" ไม่ถูกแตะ)
/// </summary>
[Trait("Category", "Db")]
public class OverageRound231DbTests
{
    private readonly ITestOutputHelper _out;
    public OverageRound231DbTests(ITestOutputHelper output) => _out = output;

    private static readonly PlatformBillingActor Admin = new(null, "admin@test", "127.0.0.1");
    private static readonly PlatformBillingActor Owner = new(null, "owner@test", "127.0.0.1");

    private static OverageService Svc(Accounting.Data.AccountingDbContext db)
        => new(db, NullLogger<OverageService>.Instance, new UsageMeteringService(db, NullLogger<UsageMeteringService>.Instance));

    /// <summary>บริษัท + แม่แบบแพ็กเกจเฉพาะเทสต์ (ราคาส่วนเกินผูกแม่แบบนี้ — ไม่กระทบบริษัทอื่นในฐานร่วม) + subscription</summary>
    private static async Task<(Guid CompanyId, Guid PlanTemplateId)> SeedAsync(Accounting.Data.AccountingDbContext db, int maxDocs = 10)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var c = new Company { Name = "บริษัททดสอบส่วนเกิน " + tag, TaxId = "0105556000004" };
        db.Companies.Add(c);
        var tpl = new PlanTemplate { Name = "OverageTest " + tag, Plan = SubscriptionPlan.Pro, IsActive = false, MaxDocumentsPerMonth = maxDocs, MaxUsers = 5 };
        db.PlanTemplates.Add(tpl);
        var owner = new User { Email = $"ov231-{tag}@test.local", PasswordHash = "x", FullName = "เจ้าของทดสอบส่วนเกิน" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var acct = new AccountSubscription
        {
            OwnerUserId = owner.Id,
            PlanTemplateId = tpl.Id, Status = SubscriptionStatus.Active, StartDate = DateTime.UtcNow.AddDays(-10), EndDate = DateTime.UtcNow.AddDays(200),
            MaxDocumentsPerMonth = maxDocs, MaxUsersPerCompany = 5, MaxStorageBytes = 1024L * 1024 * 1024, MaxOcrPagesPerMonth = 50, MaxCompanies = 3,
        };
        db.AccountSubscriptions.Add(acct);
        await db.SaveChangesAsync();
        db.Subscriptions.Add(new Subscription
        {
            CompanyId = c.Id, Plan = SubscriptionPlan.Pro, Status = SubscriptionStatus.Active, AccountSubscriptionId = acct.Id,
            StartDate = DateTime.UtcNow.AddDays(-10), EndDate = DateTime.UtcNow.AddDays(200), UsageResetDate = DateTime.UtcNow.AddDays(20),
            MaxDocumentsPerMonth = maxDocs, MaxUsers = 5, MaxStorageBytes = 1024L * 1024 * 1024, MaxOcrPagesPerMonth = 50,
        });
        await db.SaveChangesAsync();
        return (c.Id, tpl.Id);
    }

    [Fact]
    public async Task ไม่มีแถวนโยบาย_ล็อก_แม้มีราคา_และใบกฎหมายบังคับถูกบันทึกราคาศูนย์()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, tid) = await SeedAsync(db);
        var svc = Svc(db);
        await svc.UpsertRateAsync(OverageResources.Documents, tid, 5m, 1, "ทดสอบ", Admin);

        var d = await svc.DecideAsync(cid, OverageResources.Documents);
        Assert.False(d.Allowed);
        Assert.Equal(OverageResources.RuleNotEnabled, d.RuleCode);

        // ใบกำกับเกินโควตาในโหมดล็อก: ออกได้ · มิเตอร์ราคา 0 (ข้อมูลอย่างเดียว) — ห้ามคิดเงินที่ผู้เช่าไม่ได้ตกลง
        var r = await svc.RecordAsync(cid, OverageResources.Documents, 1, $"doc-overage:{Guid.NewGuid():N}", "Document", Guid.NewGuid(), chargeable: false, unitPriceSnapshot: 0m);
        Assert.True(r.Recorded);
        Assert.Equal(0m, r.ChargedAmount);
        var ev = await db.UsageEvents.AsNoTracking().SingleAsync(u => u.Id == r.UsageEventId);
        Assert.Equal("overage.documents", ev.FeatureCode);
        Assert.Equal(0m, ev.UnitPriceSnapshot);
        Assert.Equal(0m, await svc.SpentThisMonthAsync(cid));
    }

    [Fact]
    public async Task เปิดแล้วผ่านและคิดตามราคาแพ็กเกจ_ถึงเพดานกลับไปล็อก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, tid) = await SeedAsync(db);
        var svc = Svc(db);
        await svc.UpsertRateAsync(OverageResources.Documents, tid, 5m, 1, "ทดสอบ", Admin);

        // ไม่ยอมรับฉบับ ⇒ เปิดไม่ได้
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SetPolicyAsync(cid, new OveragePolicyUpdate(OverageMode.AllowOverage, 12m, null, false, null), Owner));
        var sum = await svc.SetPolicyAsync(cid, new OveragePolicyUpdate(OverageMode.AllowOverage, 12m, null, true, OveragePolicy.PolicyVersion), Owner);
        Assert.Equal(OverageMode.AllowOverage, sum.Mode);
        Assert.Equal(OveragePolicy.PolicyVersion, sum.AcceptedPolicyVersion);

        var d1 = await svc.DecideAsync(cid, OverageResources.Documents);
        Assert.True(d1.Allowed);
        Assert.Equal(5m, d1.UnitPrice);
        var key1 = "doc-overage:" + Guid.NewGuid().ToString("N");
        var r1 = await svc.RecordAsync(cid, OverageResources.Documents, 1, key1, "Document", Guid.NewGuid(), true, d1.UnitPrice);
        Assert.True(r1.Recorded); Assert.Equal(5m, r1.ChargedAmount);
        var r2 = await svc.RecordAsync(cid, OverageResources.Documents, 1, "doc-overage:" + Guid.NewGuid().ToString("N"), "Document", Guid.NewGuid(), true, d1.UnitPrice);
        Assert.Equal(5m, r2.ChargedAmount);
        Assert.Equal(10m, await svc.SpentThisMonthAsync(cid));

        // ใช้ไป 10 + 5 = 15 > เพดาน 12 ⇒ กลับไปล็อกพร้อมเหตุ CAP · ของที่คิดแล้วไม่ถูกถอน
        var d2 = await svc.DecideAsync(cid, OverageResources.Documents);
        Assert.False(d2.Allowed);
        Assert.Equal(OverageResources.RuleCap, d2.RuleCode);
        Assert.Equal(10m, (await svc.MonthSummaryAsync(cid)).TotalAmount);

        // คีย์ซ้ำ (idempotency key เดิม — ไม่ใช่ UsageEventId) = ไม่คิดซ้ำ
        var dup = await svc.RecordAsync(cid, OverageResources.Documents, 1, key1, "Document", Guid.NewGuid(), true, 5m);
        Assert.False(dup.Recorded);
        Assert.Equal(10m, await svc.SpentThisMonthAsync(cid));

        // audit chain มีแถวนโยบาย
        Assert.True(await db.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == cid && a.EntityType == nameof(CompanyOveragePolicy) && a.NewValues!.Contains(OverageService.RulePolicy)));
    }

    [Fact]
    public async Task ไม่มีราคาของรายการที่เลือก_เปิดไม่ได้_ราคาแพ็กเกจอื่นไม่นับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _) = await SeedAsync(db);
        var svc = Svc(db);
        // ราคาของแม่แบบอื่น (ไม่ใช่ของบริษัทนี้) ⇒ ไม่นับ
        var other = new PlanTemplate { Name = "Other " + Guid.NewGuid().ToString("N")[..6], Plan = SubscriptionPlan.Basic, IsActive = false };
        db.PlanTemplates.Add(other); await db.SaveChangesAsync();
        await svc.UpsertRateAsync(OverageResources.OcrPages, other.Id, 2m, 1, "ทดสอบ", Admin);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.SetPolicyAsync(cid, new OveragePolicyUpdate(OverageMode.AllowOverage, null, new List<string> { OverageResources.OcrPages }, true, OveragePolicy.PolicyVersion), Owner));
        Assert.Equal(OverageResources.RuleNoRate, ex.RuleCode);
        Assert.Equal(OverageMode.HardLock, (await svc.GetPolicyViewAsync(cid)).Mode);
    }

    [Fact]
    public async Task ขั้น1_ค้างชำระ_ล็อก_ผู้เช่าเปิดเองไม่ได้_ชำระครบปลด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, tid) = await SeedAsync(db);
        var svc = Svc(db);
        await svc.UpsertRateAsync(OverageResources.Documents, tid, 5m, 1, "ทดสอบ", Admin);
        await svc.SetPolicyAsync(cid, new OveragePolicyUpdate(OverageMode.AllowOverage, null, null, true, OveragePolicy.PolicyVersion), Owner);
        Assert.True((await svc.DecideAsync(cid, OverageResources.Documents)).Allowed);

        var now = DateTime.UtcNow;
        var overdue = new List<DunningInvoice> { new(Guid.NewGuid(), "USAGE-TEST-1", now.AddDays(-9), now.AddDays(-16), 120m, false) };
        Assert.Equal(OveragePolicy.UnpaidLockAction.Lock, await svc.ApplyUnpaidLockAsync(cid, overdue, 7, "USAGE-TEST-1"));
        Assert.Contains(cid, await OverageService.LockedOverageCompaniesAsync(db, default));   // ฝ่ายค้าน B1: งานทวงหนี้ต้องเห็นบริษัทนี้ต่อแม้ไม่มีใบค้างแล้ว
        var locked = await svc.DecideAsync(cid, OverageResources.Documents);
        Assert.False(locked.Allowed);
        Assert.Equal(OverageResources.RuleLocked, locked.RuleCode);
        Assert.Equal(OveragePolicy.UnpaidLockAction.None, await svc.ApplyUnpaidLockAsync(cid, overdue, 7, "USAGE-TEST-1"));   // ไม่ล็อกซ้ำ

        Assert.Contains("USAGE-TEST-1", (await svc.MonthSummaryAsync(cid)).LockedInvoiceNumber ?? "");

        // ฝ่ายค้าน N3: ระหว่างล็อก แก้เพดานได้ถ้าไม่เปลี่ยนโหมด (ยังล็อกอยู่)
        var edited = await svc.SetPolicyAsync(cid, new OveragePolicyUpdate(OverageMode.AllowOverage, 999m, null, true, OveragePolicy.PolicyVersion), Owner);
        Assert.Equal(999m, edited.MonthlyCapAmount); Assert.True(edited.Locked);
        // ปิดเองได้ (HardLock) แต่ "เปิดใหม่" ระหว่างล็อกไม่ได้ (HardLock → AllowOverage = ด่าน RuleLocked)
        Assert.Equal(OverageMode.HardLock, (await svc.SetPolicyAsync(cid, new OveragePolicyUpdate(OverageMode.HardLock, null, null, false, null), Owner)).Mode);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.SetPolicyAsync(cid, new OveragePolicyUpdate(OverageMode.AllowOverage, null, null, true, OveragePolicy.PolicyVersion), Owner));
        Assert.Equal(OverageResources.RuleLocked, ex.RuleCode);

        // ชำระครบ (ไม่มีใบค้าง) ⇒ งานทวงหนี้รอบถัดไป (ประเมินบริษัทจาก LockedOverageCompaniesAsync) ⇒ ปลด ⇒ ผ่านอีกครั้ง (ทิศตรงข้าม)
        Assert.Equal(OveragePolicy.UnpaidLockAction.Unlock, await svc.ApplyUnpaidLockAsync(cid, new List<DunningInvoice>(), 7, null));
        Assert.DoesNotContain(cid, await OverageService.LockedOverageCompaniesAsync(db, default));
        // ปลดแล้ว ⇒ ผู้เช่าเปิดใหม่ได้ ⇒ ผ่าน
        Assert.False((await svc.SetPolicyAsync(cid, new OveragePolicyUpdate(OverageMode.AllowOverage, null, null, true, OveragePolicy.PolicyVersion), Owner)).Locked);
        Assert.True((await svc.DecideAsync(cid, OverageResources.Documents)).Allowed);
        Assert.True(await db.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == cid && a.NewValues!.Contains(OverageService.RuleLocked)));
        Assert.True(await db.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == cid && a.NewValues!.Contains(OverageService.RuleUnlocked)));
    }

    [Fact]
    public async Task แอดมินแก้ราคา_ปิดแถวเดิมไม่แก้ทับ_ราคาศูนย์ปิดราคา()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, tid) = await SeedAsync(db);
        var svc = Svc(db);
        await svc.UpsertRateAsync(OverageResources.Documents, tid, 5m, 1, "ตั้งครั้งแรก", Admin);
        await svc.UpsertRateAsync(OverageResources.Documents, tid, 7m, 1, "ขึ้นราคา", Admin);
        var rows = await db.OverageRates.AsNoTracking().Where(r => r.ResourceKey == OverageResources.Documents && r.PlanTemplateId == tid).OrderBy(r => r.EffectiveFromUtc).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows[0].EffectiveToUtc);
        Assert.Null(rows[1].EffectiveToUtc);
        await svc.SetPolicyAsync(cid, new OveragePolicyUpdate(OverageMode.AllowOverage, null, null, true, OveragePolicy.PolicyVersion), Owner);
        Assert.Equal(7m, (await svc.DecideAsync(cid, OverageResources.Documents)).UnitPrice);
        await svc.UpsertRateAsync(OverageResources.Documents, tid, null, 1, "ปิดราคา", Admin);
        Assert.Equal(OverageResources.RuleNoRate, (await svc.DecideAsync(cid, OverageResources.Documents)).RuleCode);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.UpsertRateAsync(OverageResources.Documents, tid, 5m, 1, "", Admin));   // เหตุผลบังคับ
    }

    [Fact]
    public async Task snapshot_ก่อนขึ้นเดือน_ค่าปิดเดือนคงอยู่_Dailyไม่ทับRollover_Capacityเก็บสูงสุด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _) = await SeedAsync(db, maxDocs: 100);
        var sub = await db.Subscriptions.SingleAsync(s => s.CompanyId == cid);
        sub.CurrentMonthDocuments = 37; sub.CurrentMonthOcrPages = 12;
        sub.UsageResetDate = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        var now = new DateTime(2026, 11, 3, 5, 0, 0, DateTimeKind.Utc);

        await UsageSnapshotService.WriteRolloverAsync(db, sub, now);
        Assert.True(SubscriptionUsageRollover.RollIfDue(sub, now));
        await db.SaveChangesAsync();
        Assert.Equal(0, sub.CurrentMonthDocuments);

        var snap = await db.UsageMonthlySnapshots.AsNoTracking().SingleAsync(s => s.CompanyId == cid && s.Period == "2026-10" && s.ResourceKey == OverageResources.Documents);
        Assert.Equal(37, snap.Used);
        Assert.Equal(100, snap.Limit);
        Assert.Equal(UsageSnapshotSource.Rollover, snap.Source);
        Assert.Equal(12, (await db.UsageMonthlySnapshots.AsNoTracking().SingleAsync(s => s.CompanyId == cid && s.Period == "2026-10" && s.ResourceKey == QuotaResources.OcrPagesMonth)).Used);

        // ไม่ถึงวันรีเซ็ต = ไม่เขียน (ทิศตรงข้าม) · Daily ไม่ทับ Rollover · Capacity เก็บค่าสูงสุด
        await UsageSnapshotService.WriteRolloverAsync(db, sub, now);
        await UsageSnapshotService.UpsertAsync(db, cid, "2026-10", OverageResources.Documents, 1, 100, 0, UsageSnapshotSource.Daily, now, "t");
        Assert.Equal(37, (await db.UsageMonthlySnapshots.AsNoTracking().SingleAsync(s => s.CompanyId == cid && s.Period == "2026-10" && s.ResourceKey == OverageResources.Documents)).Used);
        await UsageSnapshotService.UpsertAsync(db, cid, "2026-11", QuotaResources.Users, 7, 10, 0, UsageSnapshotSource.Daily, now, "t");
        await UsageSnapshotService.UpsertAsync(db, cid, "2026-11", QuotaResources.Users, 5, 10, 0, UsageSnapshotSource.Daily, now, "t");
        Assert.Equal(7, (await db.UsageMonthlySnapshots.AsNoTracking().SingleAsync(s => s.CompanyId == cid && s.Period == "2026-11" && s.ResourceKey == QuotaResources.Users)).Used);
        await UsageSnapshotService.UpsertAsync(db, cid, "2026-11", QuotaResources.OcrPagesMonth, 9, 50, 0, UsageSnapshotSource.Daily, now, "t");
        await UsageSnapshotService.UpsertAsync(db, cid, "2026-11", QuotaResources.OcrPagesMonth, 4, 50, 0, UsageSnapshotSource.Daily, now, "t");
        Assert.Equal(4, (await db.UsageMonthlySnapshots.AsNoTracking().SingleAsync(s => s.CompanyId == cid && s.Period == "2026-11" && s.ResourceKey == QuotaResources.OcrPagesMonth)).Used);
    }

    [Fact]
    public async Task ราคาrealtime_ขนาดหน่วยต้องเป็น1_พื้นที่ตั้งขนาดหน่วยได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (_, tid) = await SeedAsync(db);
        var svc = Svc(db);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.UpsertRateAsync(OverageResources.Documents, tid, 5m, 2, "ทดสอบ", Admin));   // ฝ่ายค้าน S1
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.UpsertRateAsync(OverageResources.OcrPages, tid, 2m, 10, "ทดสอบ", Admin));
        await svc.UpsertRateAsync(OverageResources.StorageGb, tid, 200m, 10, "ทดสอบ", Admin);
        Assert.Contains(await svc.RatesAsync(), r => r.ResourceKey == OverageResources.StorageGb && r.PlanTemplateId == tid && r.UnitSize == 10);
    }

    [Fact]
    public async Task พื้นที่GBเดือน_ปิดงวดจากตัวอย่างรายวัน_ปิดส่วนเกินวันที่30ยังโดนคิด_ประทับท้ายงวด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _) = await SeedAsync(db);
        var svc = Svc(db);
        const long gb = 1024L * 1024L * 1024L;
        var prev = UsageHistoryRule.Periods(DateTime.UtcNow, 2)[1];
        UsageHistoryRule.TryParsePeriod(prev, out var y, out var m);
        var days = DateTime.DaysInMonth(y, m);
        // ตัวอย่างทุกวันของงวดก่อน: ใช้ 3 GB เพดาน 1 GB ราคา ฿20/GB-เดือน (snapshot ต่อวัน) — เขียนตอนที่ "เปิดอยู่"
        for (var d = 1; d <= days; d++)
            await UsageSnapshotService.UpsertDailyStorageAsync(db, cid, new DateTime(y, m, d), 3 * gb, 1 * gb, 20m, DateTime.UtcNow);
        // วันนี้บริษัทปิดส่วนเกินแล้ว (ไม่มีแถวนโยบาย = HardLock) และไม่มีราคาพื้นที่ — ฝ่ายค้าน B2: ปิดงวดต้องคิดจากตัวอย่างอยู่ดี
        Assert.False((await svc.DecideAsync(cid, OverageResources.StorageGb)).Allowed);
        Assert.True(await UsageSnapshotService.SettleStorageOverageAsync(db, svc, cid, prev));
        var ev = await db.UsageEvents.AsNoTracking().SingleAsync(u => u.CompanyId == cid && u.IdempotencyKey == $"storage-overage:{prev}");
        Assert.Equal(2, ev.Quantity);               // เกิน 2 GB ทุกวัน ⇒ 2 GB-เดือน
        Assert.Equal(20m, ev.UnitPriceSnapshot);    // ราคาจากตัวอย่าง ไม่ใช่ราคาปัจจุบัน (ไม่มี)
        Assert.Equal(40m, ev.ChargedAmount);
        Assert.Equal(prev, Accounting.Helpers.AddOnBilling.PeriodOf(ev.OccurredAt));   // ฝ่ายค้าน S4: ประทับท้ายงวด ไม่ใช่วันที่งานรัน
        Assert.False(await UsageSnapshotService.SettleStorageOverageAsync(db, svc, cid, prev));   // idempotent
        Assert.Equal(40m, await svc.SpentInPeriodAsync(cid, prev));

        // ทิศตรงข้าม: ไม่มีตัวอย่าง (ไม่เคยเปิด) ⇒ ไม่คิด
        var (cid2, _) = await SeedAsync(db);
        Assert.False(await UsageSnapshotService.SettleStorageOverageAsync(db, svc, cid2, prev));
    }

    /// <summary>ตัวรับแจ้งปลอม — เก็บทุก DispatchAsync (ผู้รับ · event)</summary>
    private sealed class CapturingEngine : Accounting.Services.Interfaces.INotificationEngine
    {
        public readonly List<(Guid? Recipient, string EventKey, string Title)> Sent = new();
        public Task DispatchAsync(Guid companyId, string eventKey, Accounting.Services.Interfaces.NotificationContext context)
        { Sent.Add((context.RecipientUserId, eventKey, context.Title)); return Task.CompletedTask; }
    }

    [Fact]
    public async Task ถึงเพดานเงิน_แจ้งoverage_capped_ครั้งเดียวต่อเดือน_ถึงเจ้าของและผู้มีสิทธิ์BillingManage()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, tid) = await SeedAsync(db);
        // ผู้รับ: เจ้าของ 1 · ผู้ใช้บทบาทกำหนดเองที่ได้ BillingManage 1 · พนักงานทั่วไป 1 (ไม่ได้รับ)
        var owner = new User { Email = $"ov231-owner-{cid:N}@test.local", PasswordHash = "x", FullName = "เจ้าของ" };
        var billing = new User { Email = $"ov231-billing-{cid:N}@test.local", PasswordHash = "x", FullName = "ฝ่ายการเงิน" };
        var staff = new User { Email = $"ov231-staff-{cid:N}@test.local", PasswordHash = "x", FullName = "พนักงาน" };
        db.Users.AddRange(owner, billing, staff);
        var role = new CompanyRole { CompanyId = cid, Name = "การเงิน" };
        db.CompanyRoles.Add(role);
        await db.SaveChangesAsync();
        db.CompanyRolePermissions.Add(new CompanyRolePermission { CompanyRoleId = role.Id, MenuItemId = PermissionKeys.BillingManage, CanAccess = true });
        db.CompanyUsers.AddRange(
            new CompanyUser { CompanyId = cid, UserId = owner.Id, Role = UserRole.Owner },
            new CompanyUser { CompanyId = cid, UserId = billing.Id, Role = UserRole.Staff, CompanyRoleId = role.Id },
            new CompanyUser { CompanyId = cid, UserId = staff.Id, Role = UserRole.Staff });
        await db.SaveChangesAsync();

        var recipients = await OverageService.BillingRecipientsAsync(db, cid, default);
        Assert.Equal(new[] { owner.Id, billing.Id }.OrderBy(x => x), recipients.OrderBy(x => x));

        var engine = new CapturingEngine();
        var svc = new OverageService(db, NullLogger<OverageService>.Instance, new UsageMeteringService(db, NullLogger<UsageMeteringService>.Instance), engine);
        await svc.UpsertRateAsync(OverageResources.Documents, tid, 5m, 1, "ทดสอบ", Admin);
        await svc.SetPolicyAsync(cid, new OveragePolicyUpdate(OverageMode.AllowOverage, 12m, null, true, OveragePolicy.PolicyVersion), Owner);

        // ยังไม่ถึงเพดาน ⇒ ไม่แจ้ง capped · เหตุการณ์ที่คิดเงินตัวแรก ⇒ overage.started ถึงผู้รับทั้ง 2
        Assert.True((await svc.DecideAsync(cid, OverageResources.Documents)).Allowed);
        await svc.RecordAsync(cid, OverageResources.Documents, 2, "doc-overage:" + Guid.NewGuid().ToString("N"), "Document", Guid.NewGuid(), true, 5m);
        Assert.Equal(2, engine.Sent.Count(x => x.EventKey == NotificationEvents.OverageStarted));
        Assert.DoesNotContain(engine.Sent, x => x.EventKey == NotificationEvents.OverageCapped);

        // ใช้ไป 10 + 5 > 12 ⇒ CAP ⇒ แจ้ง overage.capped ครั้งเดียว (ถามด่านซ้ำไม่แจ้งซ้ำ) · ไม่มีพนักงานทั่วไปในผู้รับ
        Assert.Equal(OverageResources.RuleCap, (await svc.DecideAsync(cid, OverageResources.Documents)).RuleCode);
        Assert.Equal(OverageResources.RuleCap, (await svc.DecideAsync(cid, OverageResources.Documents)).RuleCode);
        var capped = engine.Sent.Where(x => x.EventKey == NotificationEvents.OverageCapped).ToList();
        Assert.Equal(2, capped.Count);
        Assert.Equal(new[] { owner.Id, billing.Id }.OrderBy(x => x), capped.Select(x => x.Recipient!.Value).OrderBy(x => x));
        Assert.Equal(Accounting.Helpers.AddOnBilling.PeriodOf(DateTime.UtcNow),
            (await db.CompanyOveragePolicies.AsNoTracking().SingleAsync(p => p.CompanyId == cid)).CapNotifiedPeriod);

        // ทิศตรงข้าม: โหมดล็อก (ไม่ได้เปิด) ถูกปฏิเสธด้วย OVERAGE-OFF — ไม่ใช่ "capped" ⇒ ไม่แจ้ง
        var (cid2, tid2) = await SeedAsync(db);
        await svc.UpsertRateAsync(OverageResources.Documents, tid2, 5m, 1, "ทดสอบ", Admin);
        Assert.Equal(OverageResources.RuleNotEnabled, (await svc.DecideAsync(cid2, OverageResources.Documents)).RuleCode);
        Assert.Equal(2, engine.Sent.Count(x => x.EventKey == NotificationEvents.OverageCapped));
    }
}
