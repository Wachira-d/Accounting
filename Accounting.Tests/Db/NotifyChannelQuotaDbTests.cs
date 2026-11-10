using Accounting.Helpers;
using Accounting.Models.Constants;
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
/// รอบ 254 WP-11 L4 (Q3/Q4 · N7/N9) บน PostgreSQL จริง — ตัวนับแยกช่อง (PK CompanyId+Period+Channel) · เพดานแยกช่องเต็ม ⇒ แพ็กของช่อง ⇒ แพ็กรวมเดิม ·
/// คืนหน่วยเข้าถังที่หักไป · ช่องที่ไม่แยกยังนับก้อนรวม (ลูกค้าเดิมไม่สะดุด) · อีเมลเอกสารนับแถวอีเมลแบบไม่จำกัดโดยไม่กินก้อนรวม · ซื้อแพ็ก LINE ⇒ ถัง LINE
/// </summary>
[Trait("Category", "Db")]
public class NotifyChannelQuotaDbTests
{
    private readonly ITestOutputHelper _out;
    public NotifyChannelQuotaDbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static async Task<Guid> SeedAsync(Accounting.Data.AccountingDbContext db, int? combined, int? line, int? email,
        int combinedBonus = 0, int lineBonus = 0)
    {
        var company = new Company { Name = "ร้านทดสอบโควตาแยกช่อง", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.Subscriptions.Add(new Subscription
        {
            CompanyId = company.Id, Plan = SubscriptionPlan.Basic, Status = SubscriptionStatus.Active,
            StartDate = DateTime.UtcNow.AddDays(-1), EndDate = DateTime.UtcNow.AddDays(30), UsageResetDate = DateTime.UtcNow.AddDays(20),
            // เพดานเฉพาะบริษัท ⇒ ไม่ขึ้นกับแม่แบบแพ็กเกจในฐานร่วม (ก้อนรวม null = ไม่จำกัดเมื่อแม่แบบว่าง — ใส่ตัวเลขให้แน่นอน)
            MaxSharedMessagesPerMonth = combined ?? -1, MaxSharedLineMessagesPerMonth = line, MaxSharedEmailsPerMonth = email,
            NotifyBonusQuota = combinedBonus, NotifyBonusExpiresAt = combinedBonus > 0 ? DateTime.UtcNow.AddDays(5) : null,
            NotifyBonusLineQuota = lineBonus, NotifyBonusLineExpiresAt = lineBonus > 0 ? DateTime.UtcNow.AddDays(5) : null,
        });
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static NotifyQuotaService Svc(Accounting.Data.AccountingDbContext db, IUsageMeteringService? metering = null)
        => new(db, NullLogger<NotifyQuotaService>.Instance, null, metering);

    private static async Task<Dictionary<string, int>> UsedAsync(Accounting.Data.AccountingDbContext db, Guid cid)
    {
        var period = NotifyQuotaPolicy.PeriodOf(DateTime.UtcNow);
        return await db.NotifyUsageCounters.AsNoTracking().Where(c => c.CompanyId == cid && c.Period == period)
            .ToDictionaryAsync(c => c.Channel, c => c.Used + c.BonusUsed);
    }

    [Fact]
    public async Task เพดาน_LINE_แยก_เต็มแล้วใช้แพ็ก_LINE_ก่อน_แล้วแพ็กรวม_คืนหน่วยเข้าถังที่หัก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var cid = await SeedAsync(db, combined: 0, line: 1, email: null, combinedBonus: 1, lineBonus: 1);
        var svc = Svc(db);

        var plan = await svc.TryReserveAsync(cid, false, default, NotifyQuotaPolicy.ChannelLine);
        Assert.Equal(NotifyQuotaService.KindPlan, plan.Kind);
        Assert.Equal(NotifyQuotaPolicy.ChannelLine, plan.Channel);          // ก้อนรวมเป็น 0 แต่ LINE แยก ⇒ ไม่ถูกก้อนรวมกัน

        var lineBonus = await svc.TryReserveAsync(cid, false, default, NotifyQuotaPolicy.ChannelLine);
        Assert.Equal(NotifyQuotaService.KindChannelBonus, lineBonus.Kind);
        var combinedBonus = await svc.TryReserveAsync(cid, false, default, NotifyQuotaPolicy.ChannelLine);
        Assert.Equal(NotifyQuotaService.KindBonus, combinedBonus.Kind);     // โบนัสก้อนรวมเดิมใช้ได้ทั้งสองช่อง (NOTIFY §5 ข้อ 6)
        var full = await svc.TryReserveAsync(cid, false, default, NotifyQuotaPolicy.ChannelLine);
        Assert.False(full.Ok);
        Assert.Equal(NotifyQuotaPolicy.ChannelLine, full.Channel);

        // ส่งล้ม ⇒ คืนเข้าถังที่หัก (แพ็ก LINE)
        await svc.ReleaseAsync(cid, lineBonus);
        var sub = await db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == cid)
            .Select(s => new { s.NotifyBonusLineQuota, s.NotifyBonusQuota }).SingleAsync();
        Assert.Equal(1, sub.NotifyBonusLineQuota);
        Assert.Equal(0, sub.NotifyBonusQuota);

        var used = await UsedAsync(db, cid);
        Assert.Equal(2, used[NotifyQuotaPolicy.ChannelLine]);               // plan 1 + bonus (ก้อนรวม) 1 · แพ็ก LINE คืนแล้ว
        Assert.False(used.ContainsKey(NotifyQuotaPolicy.ChannelAll));        // ไม่แตะแถวก้อนรวม
    }

    [Fact]
    public async Task ช่องที่ไม่แยก_ยังนับก้อนรวมเดิม_ทิศตรงข้าม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var cid = await SeedAsync(db, combined: 1, line: null, email: null);
        var svc = Svc(db);
        var first = await svc.TryReserveAsync(cid, false, default, NotifyQuotaPolicy.ChannelLine);
        Assert.Equal(NotifyQuotaPolicy.ChannelAll, first.Channel);
        Assert.False((await svc.TryReserveAsync(cid, false, default, NotifyQuotaPolicy.ChannelEmail)).Ok);   // ก้อนรวมเต็ม ⇒ อีเมลแจ้งลูกค้าก็เต็ม (เดิม)
        var used = await UsedAsync(db, cid);
        Assert.Equal(1, used[NotifyQuotaPolicy.ChannelAll]);
        Assert.Single(used);
    }

    [Fact]
    public async Task อีเมลเอกสาร_ไม่ตั้งเพดาน_นับแถวอีเมลอย่างเดียว_ไม่กินก้อนรวม_ตั้งแล้วเต็มจึงหยุด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var cid = await SeedAsync(db, combined: 0, line: null, email: null);
        var svc = Svc(db);
        for (var i = 0; i < 3; i++)
        {
            var r = await svc.ReservePlatformEmailAsync(cid);
            Assert.True(r.Ok);                                               // ก้อนรวม 0 แต่อีเมลเอกสารไม่ถูกกัน (ลูกค้าเดิมไม่สะดุด)
            Assert.Equal(NotifyQuotaPolicy.ChannelEmail, r.Channel);
        }
        Assert.Equal(3, (await UsedAsync(db, cid))[NotifyQuotaPolicy.ChannelEmail]);

        // ทับรายบริษัท (N9) ⇒ เพดานมีผลทันที (ตัวนับเดือนนี้ไม่รีเซ็ต)
        await db.Subscriptions.Where(s => s.CompanyId == cid).ExecuteUpdateAsync(s => s.SetProperty(x => x.MaxSharedEmailsPerMonth, (int?)4));
        Assert.True((await svc.ReservePlatformEmailAsync(cid)).Ok);
        var full = await svc.ReservePlatformEmailAsync(cid);
        Assert.False(full.Ok);
        Assert.Contains("4", NotifyQuotaPolicy.EmailMonthlyFullReason(full.Limit));
        var status = await svc.GetStatusAsync(cid);
        var email = Assert.Single(status.Channels, c => c.Channel == NotifyQuotaPolicy.ChannelEmail);
        Assert.True(email.OwnLimit);
        Assert.Equal(4, email.Used);
        Assert.Equal(0, email.Remaining);
    }

    [Fact]
    public async Task ซื้อแพ็ก_LINE_บันทึกมิเตอร์ของช่อง_และให้ถัง_LINE_ไม่แตะถังรวม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var cid = await SeedAsync(db, combined: 0, line: 0, email: null);
        var recorded = new List<UsageRecordRequest>();
        var metering = F4Proxy<IUsageMeteringService>.Create((m, a) =>
        {
            if (m == nameof(IUsageMeteringService.RecordAsync))
            {
                recorded.Add((UsageRecordRequest)a[0]!);
                return new UsageRecordResult(true, Guid.NewGuid(), 290m, false, false);
            }
            return null;
        });
        var status = await Svc(db, metering).PurchaseTopUpAsync(cid, 1, "tester", channel: NotifyQuotaPolicy.ChannelLine);
        var rec = Assert.Single(recorded);
        Assert.Equal(AddOnCodes.NotifyTopUpLine, rec.FeatureCode);
        Assert.StartsWith("topup:notify.topup.line:", rec.IdempotencyKey);
        Assert.Equal(NotifyQuotaPolicy.ChannelLine, status.GrantedChannel);
        var sub = await db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == cid)
            .Select(s => new { s.NotifyBonusLineQuota, s.NotifyBonusQuota }).SingleAsync();
        Assert.Equal(status.Granted, sub.NotifyBonusLineQuota);
        Assert.Equal(0, sub.NotifyBonusQuota);
        Assert.True((await Svc(db).TryReserveAsync(cid, false, default, NotifyQuotaPolicy.ChannelLine)).Ok);   // เพดาน LINE 0 ⇒ ใช้แพ็กที่ซื้อ
        Assert.False((await Svc(db).TryReserveAsync(cid, false, default, NotifyQuotaPolicy.ChannelEmail)).Ok); // อีเมลแจ้งลูกค้าไม่ได้แพ็ก LINE
    }

    // ───────── ฝ่ายค้าน fix2 ─────────

    private static IUsageMeteringService PricedMeter(List<UsageRecordRequest> recorded)
        => F4Proxy<IUsageMeteringService>.Create((m, a) => m switch
        {
            nameof(IUsageMeteringService.RecordAsync) => Record(recorded, (UsageRecordRequest)a[0]!),
            nameof(IUsageMeteringService.ResolveEffectivePlanAsync) => new ApiPricingPlan { UnitPrice = 290m },
            _ => null,
        });

    private static object Record(List<UsageRecordRequest> recorded, UsageRecordRequest r)
    {
        recorded.Add(r);
        return new UsageRecordResult(true, Guid.NewGuid(), 290m, false, false);
    }

    [Fact]
    public async Task H1_แพ็ก_LINE_ของบริษัทที่ไม่แยกเพดาน_ปฏิเสธ409ก่อนคิดเงิน_และไม่มีราคาในสถานะ_ทิศตรงข้ามแยกแล้วขายได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var notSplit = await SeedAsync(db, combined: 10, line: null, email: null);
        var recorded = new List<UsageRecordRequest>();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Svc(db, PricedMeter(recorded)).PurchaseTopUpAsync(notSplit, 1, "tester", channel: NotifyQuotaPolicy.ChannelLine));
        Assert.Equal(409, ex.StatusCode);
        Assert.Empty(recorded);                                                       // ไม่คิดเงิน
        var st = await Svc(db, PricedMeter(recorded)).GetStatusAsync(notSplit);
        var line = Assert.Single(st.Channels, c => c.Channel == NotifyQuotaPolicy.ChannelLine);
        Assert.False(line.OwnLimit);
        Assert.Null(line.PackPrice);                                                 // หน้าเว็บไม่แสดงปุ่ม

        var split = await SeedAsync(db, combined: 10, line: 5, email: null);
        var st2 = await Svc(db, PricedMeter(recorded)).GetStatusAsync(split);
        Assert.Equal(290m, Assert.Single(st2.Channels, c => c.Channel == NotifyQuotaPolicy.ChannelLine).PackPrice);
        await Svc(db, PricedMeter(recorded)).PurchaseTopUpAsync(split, 1, "tester", channel: NotifyQuotaPolicy.ChannelLine);
        Assert.Equal(AddOnCodes.NotifyTopUpLine, Assert.Single(recorded).FeatureCode);
    }

    /// <summary>รอบ 257 ชุด 4 (C-M2): เพดานไม่จำกัด (-1) ⇒ แพ็กไม่มีผล — ก้อนรวมและช่อง: ไม่มีราคาในสถานะ · ซื้อ ⇒ 409 ก่อนคิดเงิน ·
    /// ทิศตรงข้าม: เพดานจริง (0) ก้อนรวมมีราคา ซื้อได้</summary>
    [Fact]
    public async Task R257_ไม่จำกัด_แพ็กรวมและแพ็กช่อง_ไม่มีราคา_ซื้อ409ไม่คิดเงิน_ทิศตรงข้ามเพดานจริงขายได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var unlimited = await SeedAsync(db, combined: null, line: -1, email: null);
        var recorded = new List<UsageRecordRequest>();
        var st = await Svc(db, PricedMeter(recorded)).GetStatusAsync(unlimited);
        Assert.True(st.Unlimited);
        Assert.Null(st.PackPrice);
        Assert.Null(Assert.Single(st.Channels, c => c.Channel == NotifyQuotaPolicy.ChannelLine).PackPrice);
        var exAll = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db, PricedMeter(recorded)).PurchaseTopUpAsync(unlimited, 1, "tester"));
        Assert.Equal(409, exAll.StatusCode);
        var exLine = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Svc(db, PricedMeter(recorded)).PurchaseTopUpAsync(unlimited, 1, "tester", channel: NotifyQuotaPolicy.ChannelLine));
        Assert.Equal(409, exLine.StatusCode);
        Assert.Contains("ไม่จำกัด", exLine.Message);
        Assert.Empty(recorded);

        var capped = await SeedAsync(db, combined: 0, line: null, email: null);
        var st2 = await Svc(db, PricedMeter(recorded)).GetStatusAsync(capped);
        Assert.Equal(290m, st2.PackPrice);
        await Svc(db, PricedMeter(recorded)).PurchaseTopUpAsync(capped, 1, "tester");
        Assert.Single(recorded);
    }

    [Fact]
    public async Task M1_อีเมลแพลตฟอร์มเต็ม_ไม่หักแพ็กรวม_แต่อีเมลแจ้งลูกค้ายังใช้แพ็กรวมได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var cid = await SeedAsync(db, combined: 0, line: null, email: 0, combinedBonus: 1);
        var svc = Svc(db);
        Assert.False((await svc.ReservePlatformEmailAsync(cid)).Ok);
        Assert.Equal(1, await db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == cid).Select(s => s.NotifyBonusQuota).SingleAsync());
        var notify = await svc.TryReserveAsync(cid, false, default, NotifyQuotaPolicy.ChannelEmail);   // ข้อความแจ้งลูกค้า (BookingNotifier)
        Assert.Equal(NotifyQuotaService.KindBonus, notify.Kind);
    }

    [Fact]
    public async Task M2_อีเมลแพลตฟอร์มเต็ม_ไม่ใช้ส่วนเกิน_ข้อความแจ้งลูกค้ายังใช้ส่วนเกินได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var cid = await SeedAsync(db, combined: 0, line: null, email: 0);
        var overage = F4Proxy<IOverageService>.Create((m, a) => m switch
        {
            nameof(IOverageService.DecideAsync) => new OverageDecision(true, (string)a[1]!, 1, 1m, 1m, null, null),
            nameof(IOverageService.RecordAsync) => new UsageRecordResult(true, Guid.NewGuid(), 1m, false, false),
            _ => null,
        });
        var svc = new NotifyQuotaService(db, NullLogger<NotifyQuotaService>.Instance, null, null, null, null, overage);
        var email = await svc.ReservePlatformEmailAsync(cid);
        Assert.False(email.Ok);                                                      // เต็ม = ไม่ส่ง (429/Failed)
        Assert.Equal(NotifyQuotaService.KindOverage, (await svc.TryReserveAsync(cid, false)).Kind);   // ทิศตรงข้าม: แจ้งลูกค้าใช้ส่วนเกินตามนโยบายเดิม
    }
}
