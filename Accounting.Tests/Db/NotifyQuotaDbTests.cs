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
/// รอบ 212 แพ็ก A (คำตัดสินข้อ 171) — โควตาข้อความช่องกลางบน PostgreSQL จริง: ตัวนับ <c>UPDATE … WHERE "Used" &lt; limit RETURNING</c>
/// ต้องไม่นับเกินเมื่อคำขอชนกัน · คีย์ข้อความซ้ำ = ส่งครั้งเดียว · claim หมด lease แล้วรับช่วงได้ · เตือน 80% ครั้งเดียว · โบนัส/ซื้อแพ็ก
/// </summary>
[Trait("Category", "Db")]
public class NotifyQuotaDbTests
{
    private readonly ITestOutputHelper _out;
    public NotifyQuotaDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<Guid> SeedAsync(Accounting.Data.AccountingDbContext db, int limit, int bonus = 0, DateTime? bonusExpires = null)
    {
        var company = new Company { Name = "ร้านทดสอบโควตาข้อความ", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.Subscriptions.Add(new Subscription
        {
            CompanyId = company.Id, Plan = SubscriptionPlan.Basic, Status = SubscriptionStatus.Active,
            StartDate = DateTime.UtcNow.AddDays(-1), EndDate = DateTime.UtcNow.AddDays(30), UsageResetDate = DateTime.UtcNow.AddDays(20),
            // เพดานเฉพาะบริษัท ⇒ ไม่ขึ้นกับแม่แบบแพ็กเกจในฐานร่วม
            MaxSharedMessagesPerMonth = limit, NotifyBonusQuota = bonus, NotifyBonusExpiresAt = bonusExpires,
        });
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static NotifyQuotaService Svc(Accounting.Data.AccountingDbContext db, IUsageMeteringService? metering = null)
        => new(db, NullLogger<NotifyQuotaService>.Instance, null, metering);

    private static async Task<bool> ReserveOnceAsync(Guid cid)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        return (await Svc(db).TryReserveAsync(cid, essential: false)).Ok;
    }

    [Fact]
    public async Task Concurrent_reservations_at_limit_minus_one_let_exactly_one_through()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedAsync(seed, limit: 5);
        for (var i = 0; i < 4; i++) Assert.True(await ReserveOnceAsync(cid));

        var results = await Task.WhenAll(Enumerable.Range(1, 6).Select(_ => Task.Run(() => ReserveOnceAsync(cid))));
        Assert.Equal(1, results.Count(ok => ok));
        using var db = DbTestDatabase.TryCreateContext()!;
        var period = NotifyQuotaPolicy.PeriodOf(DateTime.UtcNow);
        Assert.Equal(5, await db.NotifyUsageCounters.Where(c => c.CompanyId == cid && c.Period == period).Select(c => c.Used).SingleAsync());
    }

    [Fact]
    public async Task Release_returns_unit_and_bonus_is_used_after_plan_then_expired_bonus_is_not()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(seed, limit: 1, bonus: 1, bonusExpires: DateTime.UtcNow.AddDays(5));
        using var db = DbTestDatabase.TryCreateContext()!;
        var svc = Svc(db);
        var first = await svc.TryReserveAsync(cid, false);
        Assert.Equal(NotifyQuotaService.KindPlan, first.Kind);
        await svc.ReleaseAsync(cid, first);                       // ส่งล้ม ⇒ คืน
        Assert.Equal(NotifyQuotaService.KindPlan, (await svc.TryReserveAsync(cid, false)).Kind);
        var bonus = await svc.TryReserveAsync(cid, false);
        Assert.Equal(NotifyQuotaService.KindBonus, bonus.Kind);
        Assert.False((await svc.TryReserveAsync(cid, false)).Ok);  // แพ็กเกจ + โบนัสหมด
        Assert.True((await svc.TryReserveAsync(cid, essential: true)).Ok);   // ข้อความจำเป็นนับเกินได้

        var cid2 = await SeedAsync(seed, limit: 0, bonus: 10, bonusExpires: DateTime.UtcNow.AddDays(-1));
        Assert.False((await svc.TryReserveAsync(cid2, false)).Ok);  // โบนัสหมดอายุ = ไม่นับ
    }

    [Fact]
    public async Task Same_message_key_is_claimed_once_and_lease_expiry_allows_takeover()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(seed, limit: 10);
        using var db = DbTestDatabase.TryCreateContext()!;
        var svc = Svc(db);
        var key = "booking:" + Guid.NewGuid().ToString("N") + ":booking.confirmed";
        var c1 = await svc.ClaimAsync(cid, key, CustomerNotifyEvents.BookingConfirmed, "booking", null);
        Assert.True(c1.Claimed);
        var c2 = await svc.ClaimAsync(cid, key, CustomerNotifyEvents.BookingConfirmed, "booking", null);
        Assert.False(c2.Claimed);           // คำขอแรกกำลังส่ง
        Assert.False(c2.AlreadySent);

        // คำขอแรกค้าง (lease หมด) ⇒ รับช่วงได้
        await db.Database.ExecuteSqlRawAsync("""UPDATE "CustomerNotifyLogs" SET "ClaimedUntil" = now() - interval '1 minute' WHERE "Id" = {0}""", c1.LogId!.Value);
        var c3 = await svc.ClaimAsync(cid, key, CustomerNotifyEvents.BookingConfirmed, "booking", null);
        Assert.True(c3.Claimed);
        await svc.CompleteAsync(cid, c3.LogId, NotifyLogStatus.Sent, "email", CustomerNotifyPolicy.SrcSystemSmtp, true, null);
        var c4 = await svc.ClaimAsync(cid, key, CustomerNotifyEvents.BookingConfirmed, "booking", null);
        Assert.False(c4.Claimed);
        Assert.True(c4.AlreadySent);        // ส่งถึงแล้ว ⇒ ไม่ส่งซ้ำ
        Assert.Equal("email", c4.PreviousChannel);

        // ทิศตรงข้าม: บริษัทอื่นใช้คีย์เดียวกันได้ (กันซ้ำต่อบริษัท)
        var other = await SeedAsync(seed, limit: 10);
        Assert.True((await svc.ClaimAsync(other, key, CustomerNotifyEvents.BookingConfirmed, "booking", null)).Claimed);
    }

    [Fact]
    public async Task Warn_80_is_stamped_once_per_period()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(seed, limit: 5);
        using var db = DbTestDatabase.TryCreateContext()!;
        var svc = Svc(db);
        NotifyReservation? crossed = null;
        for (var i = 0; i < 4; i++)
        {
            var r = await svc.TryReserveAsync(cid, false);
            if (r.CrossedLevel == 1) crossed = r;
        }
        Assert.NotNull(crossed);              // 4/5 = 80%
        await svc.AlertIfCrossedAsync(cid, crossed!);
        var period = NotifyQuotaPolicy.PeriodOf(DateTime.UtcNow);
        var first = await db.NotifyUsageCounters.AsNoTracking().Where(c => c.CompanyId == cid && c.Period == period).Select(c => c.Warned80At).SingleAsync();
        Assert.NotNull(first);
        await svc.AlertIfCrossedAsync(cid, crossed!);
        var second = await db.NotifyUsageCounters.AsNoTracking().Where(c => c.CompanyId == cid && c.Period == period).Select(c => c.Warned80At).SingleAsync();
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Purchase_topup_records_one_usage_event_and_grants_bonus()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(seed, limit: 0);
        var recorded = new List<UsageRecordRequest>();
        var metering = F4Proxy<IUsageMeteringService>.Create((m, a) =>
        {
            if (m == nameof(IUsageMeteringService.RecordAsync))
            {
                recorded.Add((UsageRecordRequest)a[0]!);
                return new UsageRecordResult(true, Guid.NewGuid(), 99m, false, false);
            }
            return null;
        });
        using var db = DbTestDatabase.TryCreateContext()!;
        var status = await Svc(db, metering).PurchaseTopUpAsync(cid, 1, "tester");
        Assert.Single(recorded);
        Assert.Equal(AddOnCodes.NotifyTopUp, recorded[0].FeatureCode);
        Assert.StartsWith("topup:notify.topup:", recorded[0].IdempotencyKey);
        Assert.Equal(AddOnCodes.MessagesPerNotifyTopUpPack, status.BonusRemaining);
        Assert.True((await Svc(db).TryReserveAsync(cid, false)).Ok);   // เพดานแพ็กเกจ 0 ⇒ ใช้โบนัสที่ซื้อ
    }

    // ═════════ ฝ่ายค้านรอบ 212 B1/B2/S2 ═════════

    private static IUsageMeteringService RecordingMeter(List<string> keys, Func<string, bool>? duplicate = null)
        => F4Proxy<IUsageMeteringService>.Create((m, a) =>
        {
            if (m != nameof(IUsageMeteringService.RecordAsync)) return null;
            var req = (UsageRecordRequest)a[0]!;
            lock (keys)
            {
                var dup = duplicate?.Invoke(req.IdempotencyKey!) ?? false;
                keys.Add(req.IdempotencyKey!);
                return dup ? new UsageRecordResult(false, null, 0m, false, true) : new UsageRecordResult(true, Guid.NewGuid(), 99m, false, false);
            }
        });

    private static async Task<int> BonusAsync(Guid cid)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        return await db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == cid).Select(s => s.NotifyBonusQuota).SingleAsync();
    }

    [Fact]
    public async Task B1_concurrent_purchases_and_reserves_never_lose_an_update()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        // เพดานแพ็กเกจ 0 + โบนัส 10 ที่ยังไม่หมดอายุ ⇒ ทุกข้อความหักโบนัส
        var cid = await SeedAsync(seed, limit: 0, bonus: 10, bonusExpires: DateTime.UtcNow.AddDays(5));
        var keys = new List<string>();
        var purchases = Enumerable.Range(1, 2).Select(_ => Task.Run(async () =>
        {
            using var db = DbTestDatabase.TryCreateContext()!;
            await Svc(db, RecordingMeter(keys)).PurchaseTopUpAsync(cid, 1, "tester");
        }));
        var reserves = Enumerable.Range(1, 5).Select(_ => Task.Run(() => ReserveOnceAsync(cid)));
        await Task.WhenAll(purchases);
        var ok = await Task.WhenAll(reserves);
        Assert.Equal(2, keys.Count);
        // จ่าย 2 แพ็ก ⇒ ได้ 2 × 500 · ข้อความที่หักโบนัสสำเร็จทุกข้อความถูกหักจริง (ไม่ถูกเขียนทับด้วยค่าสัมบูรณ์)
        Assert.Equal(10 + 2 * AddOnCodes.MessagesPerNotifyTopUpPack - ok.Count(x => x), await BonusAsync(cid));
    }

    [Fact]
    public async Task B2_sequence_counts_by_bangkok_period_key_prefix()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(seed, limit: 0);
        var period = AddOnBilling.PeriodOf(DateTime.UtcNow);
        // การซื้อครั้งแรกของงวดนี้ลงเวลาไว้ "ก่อนต้นเดือน UTC" (เช่นซื้อ 00:30 ไทยวันที่ 1 = 17:30 UTC ของเดือนก่อน) — สูตรเดิมไม่นับแถวนี้ ⇒ คีย์ลำดับ 1 ซ้ำ
        seed.UsageEvents.Add(new UsageEvent
        {
            CompanyId = cid, FeatureCode = AddOnCodes.NotifyTopUp, Quantity = 1,
            IdempotencyKey = AddOnBilling.TopUpKey(AddOnCodes.NotifyTopUp, period, 1),
            OccurredAt = DateTime.UtcNow.AddDays(-40),
        });
        await seed.SaveChangesAsync();
        var keys = new List<string>();
        using var db = DbTestDatabase.TryCreateContext()!;
        await Svc(db, RecordingMeter(keys)).PurchaseTopUpAsync(cid, 1, "tester");
        Assert.Equal(AddOnBilling.TopUpKey(AddOnCodes.NotifyTopUp, period, 2), keys.Single());
    }

    [Fact]
    public async Task S2_same_idempotency_key_returns_same_result_without_second_grant()
    {
        using var seed = DbTestDatabase.TryCreateContext();
        if (seed == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedAsync(seed, limit: 0);
        var keys = new List<string>();
        var seen = new HashSet<string>();
        var meter = RecordingMeter(keys, k => !seen.Add(k));
        using (var db = DbTestDatabase.TryCreateContext()!)
            await Svc(db, meter).PurchaseTopUpAsync(cid, 1, "tester", default, "click-1");
        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var again = await Svc(db, meter).PurchaseTopUpAsync(cid, 1, "tester", default, "click-1");   // ส่งซ้ำ ⇒ ไม่โยน · ผลเดิม
            Assert.Equal(AddOnCodes.MessagesPerNotifyTopUpPack, again.BonusRemaining);
        }
        Assert.Equal(2, keys.Count);
        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(AddOnCodes.MessagesPerNotifyTopUpPack, await BonusAsync(cid));
    }
}
