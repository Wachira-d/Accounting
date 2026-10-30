using System.Net;
using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Background;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 217 (คำตัดสินข้อ 175 · TEST_PLAN SUB-01..SUB-10) — เจ้าของรายงาน BK-2610-0003/0004 (กดจองเบิ้ล ⇒ การจองซ้ำ) บนฐานจริง:
/// <list type="bullet">
///   <item>ชั้นคีย์ (<see cref="IdempotencyMiddleware"/>): สองคำขอพร้อมกันคีย์เดียวกัน ⇒ การจองใบเดียว (อีกคำขอ 409 in-flight หรือ replay) · ยิงซ้ำหลังเสร็จ ⇒ ผลเดิมทุกไบต์ ·
///     คีย์เดิมเนื้อต่าง ⇒ 409 mismatch (ไม่สร้างใบ)</item>
///   <item>ชั้นความหมาย (<see cref="DuplicateSubmitGuard"/> ใต้ล็อกไซต์): แขกเบอร์เดิม/สมาชิกคนเดิม ยิงพร้อมกันไม่มีคีย์ ⇒ ใบเดียว ·
///     ทิศตรงข้าม: คนละคน (ความจุพอ) ⇒ ได้ทั้งคู่ · รอบอื่น ⇒ ได้ · ConfirmAdditional ⇒ ได้ · พ้นหน้าต่าง ⇒ ได้</item>
///   <item>แจ้งเตือนตามหลัง: การจองเขียนแถวคิวหนึ่งแถวในธุรกรรมเดียวกัน · ไม่ส่งในคำขอ · dispatcher ส่งครั้งเดียวแล้วปิดแถว</item>
/// </list>
/// ไม่มีฐาน (ไม่ตั้ง <c>ACCOUNTING_TEST_PG</c>) ⇒ ข้ามพร้อมข้อความ — หลักฐานคือ job <c>db-test</c>
/// </summary>
[Trait("Category", "Db")]
public class SubmitOnceRound217DbTests
{
    private readonly ITestOutputHelper _out;
    public SubmitOnceRound217DbTests(ITestOutputHelper output) => _out = output;

    /// <summary>ตัวแจ้งเตือนปลอม — นับข้อความถึงลูกค้า/ร้าน (ส่งถึงเสมอ)</summary>
    private sealed class FakeShop : ICmsShopNotifier
    {
        private int _customer, _shop;
        public int Customer => _customer;
        public int Shop => _shop;
        public Task<string?> NotifyShopAsync(Guid companyId, Guid? siteId, CmsNotification message, bool orderEvent, CancellationToken ct = default)
        { Interlocked.Increment(ref _shop); return Task.FromResult<string?>(null); }
        public Task<string?> NotifyCustomerAsync(Guid companyId, Guid? siteId, string eventKey, string messageKey, BookingContact contact, CmsNotification message,
            CancellationToken ct = default)
        { Interlocked.Increment(ref _customer); return Task.FromResult<string?>(null); }
        public Task<string?> StorefrontBaseUrlAsync(Guid companyId, Guid siteId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static DateOnly TargetDate() => DateOnly.FromDateTime(BookingSlotEngine.BangkokNow(DateTime.UtcNow)).AddDays(10);

    private static async Task<(Guid CompanyId, Guid SiteId, Guid ServiceId)> SeedAsync(AccountingDbContext db, int capacity)
    {
        var company = new Company { Name = "สปาทดสอบกดซ้ำ", TaxId = "0105556000002" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = company.Id, Name = "สปากดซ้ำ", Slug = "dup-" + tag, Subdomain = "dup-" + tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        var svc = new SiteBookingService
        {
            CompanyId = company.Id, SiteId = site.Id, Name = "นวดน้ำมันอโรมา 90 นาที", Slug = "aroma-" + tag,
            SlotMode = BookingSlotMode.Interval, DurationMinutes = 90, SlotIntervalMinutes = 90, MaxCapacity = capacity,
            MinAdvanceBookingHours = 0, MaxAdvanceBookingDays = 30, BookingType = BookingType.PrePayment,   // ไม่มีมัดจำ ⇒ ไม่ออกเอกสาร ERP
            AutoConfirm = false, IsActive = true, Price = 1200m,
        };
        db.SiteBookingServices.Add(svc);
        await db.SaveChangesAsync();
        db.SiteBookingSlots.Add(new SiteBookingSlot
        {
            CompanyId = company.Id, BookingServiceId = svc.Id, DayOfWeek = TargetDate().DayOfWeek,
            StartTime = new TimeOnly(16, 0), EndTime = new TimeOnly(22, 0), MaxCapacity = capacity, IsActive = true,
        });
        await db.SaveChangesAsync();
        return (company.Id, site.Id, svc.Id);
    }

    private static async Task<Guid> SeedMemberAsync(AccountingDbContext db, Guid companyId, Guid siteId, string phone)
    {
        var c = new SiteCustomer { CompanyId = companyId, SiteId = siteId, Email = $"m{Guid.NewGuid():N}@example.test", FullName = "สมาชิกทดสอบ", Phone = phone, IsActive = true };
        db.SiteCustomers.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    private static CreateBookingRequest Req(Guid serviceId, string start = "19:00", string? phone = "081-234-5678", Guid? customerId = null,
        bool confirmAdditional = false, string name = "คุณลูกค้า")
        => new()
        {
            BookingServiceId = serviceId, BookingDate = TargetDate().ToDateTime(TimeOnly.MinValue), StartTime = start,
            GuestName = name, GuestPhone = phone, GuestCount = 1, CustomerId = customerId, ConfirmAdditional = confirmAdditional,
        };

    private sealed record Attempt(BookingResponse? Result, string? Code, int Status);

    private static async Task<Attempt> TryBookAsync(Guid companyId, Guid siteId, CreateBookingRequest req)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        try
        {
            var r = await new CmsBookingService(db, NullLogger<CmsBookingService>.Instance).CreateBookingAsync(companyId, siteId, req, null);
            return new Attempt(r, null, 201);
        }
        catch (BusinessRuleException ex) { return new Attempt(null, ex.RuleCode, ex.StatusCode); }
    }

    private static async Task<int> ActiveBookingsAsync(Guid companyId, Guid siteId)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        return await db.SiteBookings.CountAsync(b => b.CompanyId == companyId && b.SiteId == siteId && b.Status != BookingStatus.Cancelled);
    }

    // ═════════ ชั้นความหมาย (ไม่มีคีย์) ═════════

    [Fact]
    public async Task SUB01_แขกเบอร์เดิม_กดพร้อมกันสี่ครั้ง_ได้ใบเดียว_ที่เหลือ409ซ้ำ()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 5);

        // เบอร์รูปแบบต่างกัน = คนเดียวกัน (ตัวทำให้เทียบได้ของ CmsContactResolver)
        var phones = new[] { "081-234-5678", "0812345678", "+66 81 234 5678", "081 234 5678" };
        var results = await Task.WhenAll(phones.Select(p => Task.Run(() => TryBookAsync(cid, sid, Req(svcId, phone: p)))));

        Assert.Single(results, r => r.Result != null);
        Assert.All(results.Where(r => r.Result == null), r =>
        {
            Assert.Equal(DuplicateSubmitGuard.BookingRuleCode, r.Code);
            Assert.Equal(409, r.Status);
        });
        Assert.Equal(1, await ActiveBookingsAsync(cid, sid));
    }

    [Fact]
    public async Task SUB02_สมาชิกคนเดิม_กดพร้อมกัน_ได้ใบเดียว_คำขออื่นได้ใบเดิมคืนพร้อมป้ายReused()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 5);
        var member = await SeedMemberAsync(seedDb, cid, sid, "0812345678");

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() => TryBookAsync(cid, sid, Req(svcId, phone: null, customerId: member)))));

        Assert.All(results, r => Assert.NotNull(r.Result));
        Assert.Single(results.Select(r => r.Result!.Id).Distinct());
        Assert.Equal(1, results.Count(r => !r.Result!.Reused));
        Assert.All(results.Where(r => r.Result!.Reused), r => Assert.Equal(r.Result!.BookingNumber, r.Result.DuplicateOf));
        Assert.Equal(1, await ActiveBookingsAsync(cid, sid));
    }

    [Fact]
    public async Task SUB03_ทิศตรงข้าม_คนละคน_ความจุสอง_พร้อมกัน_ได้ทั้งสองใบ()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 2);

        var results = await Task.WhenAll(
            Task.Run(() => TryBookAsync(cid, sid, Req(svcId, phone: "0811111111", name: "ก"))),
            Task.Run(() => TryBookAsync(cid, sid, Req(svcId, phone: "0822222222", name: "ข"))));

        Assert.All(results, r => Assert.NotNull(r.Result));
        Assert.Equal(2, await ActiveBookingsAsync(cid, sid));
    }

    [Fact]
    public async Task SUB04_ทิศตรงข้าม_คนเดิมคนละรอบ_และตั้งใจจองเพิ่ม_และพ้นหน้าต่าง_ได้ทุกกรณี()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 5);

        var first = await TryBookAsync(cid, sid, Req(svcId, start: "19:00"));
        var otherSlot = await TryBookAsync(cid, sid, Req(svcId, start: "16:00"));
        var dup = await TryBookAsync(cid, sid, Req(svcId, start: "19:00"));
        var additional = await TryBookAsync(cid, sid, Req(svcId, start: "19:00", confirmAdditional: true));
        Assert.NotNull(first.Result);
        Assert.NotNull(otherSlot.Result);
        Assert.Equal(DuplicateSubmitGuard.BookingRuleCode, dup.Code);
        Assert.NotNull(additional.Result);
        Assert.False(additional.Result!.Reused);

        // พ้นหน้าต่าง 10 นาที ⇒ จองรอบเดิมได้ตามปกติ (ความจุยังพอ)
        using (var db = DbTestDatabase.TryCreateContext()!)
            await db.SiteBookings.Where(b => b.CompanyId == cid && b.SiteId == sid)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.CreatedAt, DateTime.UtcNow - DuplicateSubmitGuard.Window - TimeSpan.FromMinutes(1)));
        var later = await TryBookAsync(cid, sid, Req(svcId, start: "19:00"));
        Assert.NotNull(later.Result);
        Assert.Equal(4, await ActiveBookingsAsync(cid, sid));
    }

    [Fact]
    public async Task SUB05_ใบเดิมถูกยกเลิก_จองรอบเดิมใหม่ได้ทันที()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 5);
        var first = await TryBookAsync(cid, sid, Req(svcId));
        using (var db = DbTestDatabase.TryCreateContext()!)
            await db.SiteBookings.Where(b => b.Id == first.Result!.Id && b.CompanyId == cid)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.Status, BookingStatus.Cancelled));
        var again = await TryBookAsync(cid, sid, Req(svcId));
        Assert.NotNull(again.Result);
    }

    // ═════════ แจ้งเตือนตามหลัง ═════════

    [Fact]
    public async Task SUB06_การจองเขียนแถวคิวหนึ่งแถว_ไม่ส่งในคำขอ_dispatcherส่งครั้งเดียวแล้วปิดแถว()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 5);
        var shop = new FakeShop();

        BookingResponse created;
        using (var db = DbTestDatabase.TryCreateContext()!)
            created = await new CmsBookingService(db, NullLogger<CmsBookingService>.Instance, shopNotifier: shop)
                .CreateBookingAsync(cid, sid, Req(svcId, phone: "0833333333"), null);
        Assert.Equal((0, 0), (shop.Customer, shop.Shop));   // ไม่ส่งในคำขอ (ต้นเหตุ "กดแล้วนิ่ง")

        using (var db = DbTestDatabase.TryCreateContext()!)
        {
            var row = await db.DeferredNotifications.AsNoTracking().SingleAsync(d => d.CompanyId == cid && d.EntityId == created.Id);
            Assert.Equal((DeferredNotifyPolicy.BookingCreated, sid), (row.Kind, row.SiteId));
            Assert.Null(row.DoneAt);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => DbTestDatabase.TryCreateContext()!);
        services.AddScoped<ICmsBookingService>(sp => new CmsBookingService(sp.GetRequiredService<AccountingDbContext>(),
            NullLogger<CmsBookingService>.Instance, shopNotifier: shop));
        services.AddScoped<DeferredNotifyDispatcher>();
        using var provider = services.BuildServiceProvider();
        using (var scope = provider.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetRequiredService<DeferredNotifyDispatcher>();
            var r1 = await dispatcher.RunOnceAsync(DateTime.UtcNow, default, cid);
            var r2 = await dispatcher.RunOnceAsync(DateTime.UtcNow, default, cid);
            Assert.Equal((1, 1), (r1.Claimed, r1.Done));
            Assert.Equal(0, r2.Claimed);   // ปิดแล้วไม่หยิบซ้ำ
        }
        Assert.Equal((1, 1), (shop.Customer, shop.Shop));
        using (var db = DbTestDatabase.TryCreateContext()!)
            Assert.NotNull((await db.DeferredNotifications.AsNoTracking().SingleAsync(d => d.CompanyId == cid && d.EntityId == created.Id)).DoneAt);
    }

    [Fact]
    public async Task SUB07_คำขอซ้ำที่ได้ใบเดิม_ไม่เพิ่มแถวคิว_ไม่แจ้งซ้ำ()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 5);
        var member = await SeedMemberAsync(seedDb, cid, sid, "0844444444");
        await TryBookAsync(cid, sid, Req(svcId, phone: null, customerId: member));
        await TryBookAsync(cid, sid, Req(svcId, phone: null, customerId: member));
        using var db = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await db.DeferredNotifications.CountAsync(d => d.CompanyId == cid && d.Kind == DeferredNotifyPolicy.BookingCreated));
    }

    [Fact]
    public async Task SUB08_lease_สองรอบซ้อน_ไม่หยิบแถวเดียวกัน()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, sid, _) = await SeedAsync(seedDb, capacity: 1);
        seedDb.DeferredNotifications.Add(DeferredNotifyPolicy.New(cid, sid, DeferredNotifyPolicy.BookingCreated, Guid.NewGuid(), DateTime.UtcNow.AddSeconds(-1)));
        await seedDb.SaveChangesAsync();

        using var db1 = DbTestDatabase.TryCreateContext()!;
        using var db2 = DbTestDatabase.TryCreateContext()!;
        var now = DateTime.UtcNow;
        var claims = await Task.WhenAll(DeferredNotifyDispatcher.ClaimAsync(db1, now, default, cid), DeferredNotifyDispatcher.ClaimAsync(db2, now, default, cid));
        Assert.Equal(1, claims.Sum(c => c.Count));
        // lease หมด ⇒ หยิบใหม่ได้ (เครื่องล้มกลางทาง)
        var afterLease = await DeferredNotifyDispatcher.ClaimAsync(db1, now + DeferredNotifyPolicy.Lease + TimeSpan.FromSeconds(1), default, cid);
        Assert.Single(afterLease);
        Assert.Equal(2, afterLease[0].Attempts);
    }

    // ═════════ ชั้นคีย์ (IdempotencyMiddleware บนฐานจริง) ═════════

    private static async Task EnsureIdempotencyTableAsync(AccountingDbContext db)
    {
        foreach (var sql in SubmitOnceMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
    }

    private sealed record HttpResult(int Status, string Body, string? IdemStatus);

    /// <summary>หนึ่งคำขอผ่านมิดเดิลแวร์จริง — next = controller จำลองที่สร้างการจองด้วย service จริง (หน่วง ⇒ คำขอคู่ซ้อนกันจริง)</summary>
    private static async Task<HttpResult> PostAsync(Guid cid, Guid sid, string key, string json, int holdMs)
    {
        using var db = DbTestDatabase.TryCreateContext()!;
        var ctx = new DefaultHttpContext();
        ctx.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        ctx.Request.Method = "POST";
        ctx.Request.Path = $"/api/companies/{cid}/cms/sites/{sid}/booking/bookings";
        ctx.Request.Headers[IdempotencyKeyPolicy.Header] = key;
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.7");
        var responseBody = new MemoryStream();
        ctx.Response.Body = responseBody;

        var mw = new IdempotencyMiddleware(async http =>
        {
            var req = await JsonSerializer.DeserializeAsync<CreateBookingRequest>(http.Request.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            await Task.Delay(holdMs);
            using var appDb = DbTestDatabase.TryCreateContext()!;
            var r = await new CmsBookingService(appDb, NullLogger<CmsBookingService>.Instance).CreateBookingAsync(cid, sid, req!, null);
            http.Response.StatusCode = 201;
            await http.Response.WriteAsJsonAsync(new { success = true, data = new { r.Id, r.BookingNumber } });
        }, NullLogger<IdempotencyMiddleware>.Instance);
        await mw.InvokeAsync(ctx, db);
        return new HttpResult(ctx.Response.StatusCode, Encoding.UTF8.GetString(responseBody.ToArray()),
            ctx.Response.Headers[IdempotencyKeyPolicy.StatusHeader].FirstOrDefault());
    }

    private static string BookingJson(Guid svcId, string start, string phone)
        => JsonSerializer.Serialize(new
        {
            bookingServiceId = svcId, bookingDate = TargetDate().ToDateTime(TimeOnly.MinValue), startTime = start,
            guestName = "คุณคีย์", guestPhone = phone, guestCount = 1,
        });

    [Fact]
    public async Task SUB09_คีย์เดียวกันสองคำขอพร้อมกัน_ได้การจองใบเดียว_ยิงซ้ำหลังเสร็จได้ผลเดิมทุกไบต์()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsureIdempotencyTableAsync(seedDb);
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 5);
        var key = Guid.NewGuid().ToString();
        var json = BookingJson(svcId, "19:00", "0855555555");

        var pair = await Task.WhenAll(Task.Run(() => PostAsync(cid, sid, key, json, 800)), Task.Run(() => PostAsync(cid, sid, key, json, 800)));

        Assert.Equal(1, await ActiveBookingsAsync(cid, sid));
        Assert.Contains(pair, r => r.Status == 201);
        // อีกคำขอ: กำลังทำงาน (409 in-flight) หรือมาทีหลังได้ผลเดิม (replay) — ไม่มีทางเป็นใบที่สอง
        Assert.All(pair, r => Assert.True(r.Status == 201 || (r.Status == 409 && r.IdemStatus == IdempotencyKeyPolicy.StatusInFlight)));

        var original = pair.First(r => r.Status == 201 && r.IdemStatus == null);
        var replay = await PostAsync(cid, sid, key, json, 0);
        Assert.Equal((201, IdempotencyKeyPolicy.StatusReplayed), (replay.Status, replay.IdemStatus));
        Assert.Equal(original.Body, replay.Body);
        Assert.Equal(1, await ActiveBookingsAsync(cid, sid));
    }

    [Fact]
    public async Task SUB10_คีย์เดิมเนื้อต่าง_409mismatch_ไม่สร้างใบ_คีย์ใหม่จองรอบอื่นได้()
    {
        using var seedDb = DbTestDatabase.TryCreateContext();
        if (seedDb == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsureIdempotencyTableAsync(seedDb);
        var (cid, sid, svcId) = await SeedAsync(seedDb, capacity: 5);
        var key = Guid.NewGuid().ToString();

        var first = await PostAsync(cid, sid, key, BookingJson(svcId, "19:00", "0866666666"), 0);
        var reused = await PostAsync(cid, sid, key, BookingJson(svcId, "16:00", "0866666666"), 0);
        Assert.Equal(201, first.Status);
        Assert.Equal((409, IdempotencyKeyPolicy.StatusMismatch), (reused.Status, reused.IdemStatus));
        Assert.Equal(1, await ActiveBookingsAsync(cid, sid));

        // ทิศตรงข้าม: คีย์ใหม่ + รอบอื่น ⇒ จองได้ตามปกติ
        var fresh = await PostAsync(cid, sid, Guid.NewGuid().ToString(), BookingJson(svcId, "16:00", "0866666666"), 0);
        Assert.Equal(201, fresh.Status);
        Assert.Equal(2, await ActiveBookingsAsync(cid, sid));
    }
}
