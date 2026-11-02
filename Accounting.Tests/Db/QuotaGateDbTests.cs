using Accounting.Models.Constants;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 229 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §C แพ็ก 2 · §B.5 แพ็ก 0/3) — บน PostgreSQL จริง:
/// <list type="bullet">
/// <item>จำนวนเว็บ CMS: เต็มแล้วสร้างไม่ได้ · ซื้อแล้วสร้างได้ทันที · โควตาหมดอายุ ⇒ <b>เว็บเดิมยังอยู่ครบ</b> (ทิศตรงข้าม — ไม่ลบ/ไม่ปิด) แต่สร้างเพิ่มไม่ได้ ·
/// ไม่ตั้ง = ไม่จำกัด · บริษัทอื่นไม่นับ (กฎ M) · สองคำขอพร้อมกันผ่านได้แค่หนึ่ง (ล็อกธุรกรรม)</item>
/// <item>ค่าเดือนโควตาเสริมแบบรายเดือน: แพ็กเกจไม่ active ⇒ ไม่คิดเงิน/ไม่ประทับงวด · active ⇒ คิดครั้งเดียวต่องวด</item>
/// </list>
/// เพดานใช้ override รายบริษัท (แยกข้อมูลต่อเทสต์ — แม่แบบแพ็กเกจเป็นของทั้งฐาน)
/// </summary>
[Trait("Category", "Db")]
public class QuotaGateDbTests
{
    private readonly ITestOutputHelper _out;
    public QuotaGateDbTests(ITestOutputHelper output) => _out = output;

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private sealed class Sp : IServiceProvider
    {
        private readonly ISubscriptionService _subs;
        public Sp(ISubscriptionService subs) => _subs = subs;
        public object? GetService(Type t) => t == typeof(ISubscriptionService) ? _subs : null;
    }

    private static IQuotaGate Gate(Accounting.Data.AccountingDbContext db) => new QuotaGateService(db, new Sp(new SubscriptionService(db, Notifications())));

    private static CmsSiteService Sites(Accounting.Data.AccountingDbContext db)
        => new(db, NullLogger<CmsSiteService>.Instance, quota: null, entitlement: null, quotaGate: Gate(db));

    private static async Task<Guid> SeedCompanyAsync(Accounting.Data.AccountingDbContext db, SubscriptionStatus status = SubscriptionStatus.Active)
    {
        var c = new Company { Name = "บริษัททดสอบโควตาเว็บ " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000003" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        db.Subscriptions.Add(new Subscription
        {
            CompanyId = c.Id, Plan = SubscriptionPlan.Pro, Status = status,
            StartDate = DateTime.UtcNow.AddDays(-10), EndDate = DateTime.UtcNow.AddDays(200), UsageResetDate = DateTime.UtcNow.AddDays(20),
        });
        await db.SaveChangesAsync();
        return c.Id;
    }

    private static CreateSiteRequest Req() => new()
    {
        Name = "เว็บทดสอบโควตา", Subdomain = "qt" + Guid.NewGuid().ToString("N")[..12],
    };

    [Fact]
    public async Task จำนวนเว็บ_เต็มบล็อก_ซื้อแล้วได้_หมดอายุแล้วเว็บเดิมยังอยู่_บริษัทอื่นไม่นับ()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var cid = await SeedCompanyAsync(db);
        var other = await SeedCompanyAsync(db);

        // ไม่ตั้งอะไร = ไม่จำกัด (deploy แล้วไม่มีใครถูกบล็อก)
        await Sites(db).CreateSiteAsync(other, Req(), "u");
        await Sites(db).CreateSiteAsync(other, Req(), "u");

        db.CompanyQuotaOverrides.Add(new CompanyQuotaOverride { CompanyId = cid, ResourceKey = QuotaResources.CmsSites, Limit = 1, Reason = "test" });
        await db.SaveChangesAsync();

        await Sites(db).CreateSiteAsync(cid, Req(), "u");                                   // 1/1 — บริษัทอื่นมี 2 เว็บแต่ไม่นับ
        var full = await Assert.ThrowsAsync<Accounting.Helpers.BusinessRuleException>(() => Sites(db).CreateSiteAsync(cid, Req(), "u"));
        Assert.Equal("QUOTA-FULL:cms.sites", full.RuleCode);

        // ซื้อเพิ่ม 1 (Active ถึงพรุ่งนี้) ⇒ สร้างได้ทันที
        var purchase = new CompanyQuotaAddOn
        {
            CompanyId = cid, OfferId = Guid.NewGuid(), ResourceKey = QuotaResources.CmsSites, Units = 1, UnitSize = 1, Mode = QuotaAddOnMode.OneOffMonth,
            PeriodStartUtc = DateTime.UtcNow.AddMinutes(-5), PeriodEndUtc = DateTime.UtcNow.AddDays(1), Status = QuotaAddOnStatus.Active,
        };
        db.CompanyQuotaAddOns.Add(purchase);
        await db.SaveChangesAsync();
        await Sites(db).CreateSiteAsync(cid, Req(), "u");
        Assert.Equal(2, await db.Sites.CountAsync(s => s.CompanyId == cid));

        // โควตาหมดอายุ ⇒ เว็บเดิมทั้งสองยังอยู่ (ไม่ลบ/ไม่ปิด — คำตัดสินข้อ 229-2) แต่สร้างเพิ่มไม่ได้
        purchase.PeriodEndUtc = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<Accounting.Helpers.BusinessRuleException>(() => Sites(db).CreateSiteAsync(cid, Req(), "u"));
        Assert.Equal(2, await db.Sites.CountAsync(s => s.CompanyId == cid));

        // ลบเว็บ ⇒ คืนช่อง (นับเฉพาะที่ยังไม่ลบ)
        var one = await db.Sites.FirstAsync(s => s.CompanyId == cid);
        one.IsDeleted = true; await db.SaveChangesAsync();
        Assert.Equal(1, await Gate(db).UsedAsync(cid, QuotaResources.CmsSites));
        Assert.False((await Gate(db).CheckAsync(cid, QuotaResources.CmsSites)).Allowed);   // 1/1 ยังเต็ม
        one.IsDeleted = false; await db.SaveChangesAsync();
    }

    [Fact]
    public async Task สองคำขอพร้อมกัน_เหลือช่องเดียว_ผ่านได้แค่หนึ่ง()
    {
        await using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var cid = await SeedCompanyAsync(seed);
        seed.CompanyQuotaOverrides.Add(new CompanyQuotaOverride { CompanyId = cid, ResourceKey = QuotaResources.CmsSites, Limit = 1, Reason = "test" });
        await seed.SaveChangesAsync();

        async Task<bool> TryCreate()
        {
            await using var db = DbTestDatabase.TryCreateContext()!;
            try { await Sites(db).CreateSiteAsync(cid, Req(), "u"); return true; }
            catch (Accounting.Helpers.BusinessRuleException) { return false; }
        }
        var results = await Task.WhenAll(TryCreate(), TryCreate(), TryCreate());
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(1, await seed.Sites.CountAsync(s => s.CompanyId == cid));
    }

    [Fact]
    public async Task ค่าเดือนรายเดือน_แพ็กเกจไม่active_ไม่คิด_active_คิดครั้งเดียวต่องวด()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var active = await SeedCompanyAsync(db);
        var cancelled = await SeedCompanyAsync(db, SubscriptionStatus.Cancelled);
        CompanyQuotaAddOn Row(Guid cid) => new()
        {
            CompanyId = cid, OfferId = Guid.NewGuid(), ResourceKey = QuotaResources.CmsSites, Units = 2, UnitSize = 1, Mode = QuotaAddOnMode.RecurringMonthly,
            PeriodStartUtc = DateTime.UtcNow.AddDays(-40), Status = QuotaAddOnStatus.Active, MonthlyUnitPriceSnapshot = 199m, LastBilledPeriod = "2000-01",
        };
        var a = Row(active); var c = Row(cancelled);
        db.CompanyQuotaAddOns.AddRange(a, c);
        await db.SaveChangesAsync();

        var subs = new SubscriptionService(db, Notifications());
        var svc = new Accounting.Services.Payments.QuotaAddOnPurchaseService(db, subs, new Sp(subs),
            NullLogger<Accounting.Services.Payments.QuotaAddOnPurchaseService>.Instance);
        var period = Accounting.Helpers.AddOnBilling.PeriodOf(DateTime.UtcNow);
        await svc.BillRecurringAsync(period);
        await svc.BillRecurringAsync(period);   // รอบซ้ำ (job ทุก 6 ชม.) ⇒ ไม่คิดซ้ำ

        var key = $"qadd:{a.Id:N}:{period}";
        var events = await db.UsageEvents.AsNoTracking().Where(u => u.CompanyId == active && u.RefEntityId == a.Id).ToListAsync();
        Assert.Single(events);
        Assert.Equal(key, events[0].IdempotencyKey);
        Assert.Equal(398m, events[0].ChargedAmount);
        Assert.Equal(QuotaResources.OfferCode(QuotaResources.CmsSites), events[0].FeatureCode);

        // แพ็กเกจยกเลิก ⇒ ไม่มีบริการ ⇒ ไม่คิด และไม่ประทับงวด (ต่ออายุแล้วคิดได้)
        Assert.False(await db.UsageEvents.AnyAsync(u => u.CompanyId == cancelled && u.RefEntityId == c.Id));
        Assert.Equal("2000-01", (await db.CompanyQuotaAddOns.AsNoTracking().FirstAsync(x => x.Id == c.Id)).LastBilledPeriod);
    }
}
