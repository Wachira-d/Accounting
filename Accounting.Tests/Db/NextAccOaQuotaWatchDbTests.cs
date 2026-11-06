using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Background;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 254 WP-11 L4 (N8) บน PostgreSQL จริง — งานเฝ้าต้นทุน OA กลาง: เก็บยอด push จาก LINE · ถึง 90% ⇒ เตือนกลุ่มผู้ดูแลครั้งเดียวต่องวด ·
/// ส่งเตือนไม่สำเร็จ ⇒ ไม่ประทับ (รอบหน้าลองใหม่ — ทิศตรงข้าม) · ตัวนับ reply บวกแบบ atomic
/// </summary>
[Trait("Category", "Db")]
public class NextAccOaQuotaWatchDbTests
{
    private readonly ITestOutputHelper _out;
    public NextAccOaQuotaWatchDbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static IServiceProvider Services(ILineNotifyService line)
        => new ServiceCollection()
            .AddScoped(_ => DbTestDatabase.TryCreateContext()!)
            .AddSingleton(line)
            .BuildServiceProvider();

    private static ILineNotifyService FakeLine(NextAccOaQuotaReading? reading, List<string> pushed, bool pushOk = true)
        => F4Proxy<ILineNotifyService>.Create((m, a) => m switch
        {
            nameof(ILineNotifyService.TryReadNextAccQuotaAsync) => (object?)reading,
            nameof(ILineNotifyService.TryPushPlatformOpsAsync) => Push(pushed, (string)a[0]!, pushOk),
            _ => null,
        });

    private static object Push(List<string> pushed, string message, bool ok)
    {
        pushed.Add(message);
        return ok ? LinePushResult.Success : LinePushResult.Fail("กลุ่มผู้ดูแลยังไม่ได้ตั้ง");
    }

    /// <summary>งวดสมมุติที่ไม่ชนงวดจริง (ล้างแถวของงวดนี้ก่อนทุกเทสต์)</summary>
    private static async Task<DateTime> FreshPeriodAsync(AccountingDbContext db, int year)
    {
        var now = new DateTime(year, 3, 15, 5, 0, 0, DateTimeKind.Utc);
        var period = NextAccOaQuotaRule.PeriodOf(now);
        await db.NextAccOaUsageCounters.Where(c => c.Period == period).ExecuteDeleteAsync();
        return now;
    }

    [Fact]
    public async Task push_ถึง90_เตือนครั้งเดียวต่องวด_และเก็บยอดจาก_LINE()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var now = await FreshPeriodAsync(db, 2001);
        var pushed = new List<string>();
        var sp = Services(FakeLine(new NextAccOaQuotaReading(5000, 4600), pushed));

        var first = await NextAccOaQuotaWatch.RunOnceAsync(sp, NullLogger.Instance, now, default);
        Assert.Single(first);
        var second = await NextAccOaQuotaWatch.RunOnceAsync(sp, NullLogger.Instance, now, default);
        Assert.Empty(second);                                   // งวดเดียวกัน ⇒ ไม่เตือนซ้ำ
        Assert.Single(pushed);

        var row = await db.NextAccOaUsageCounters.AsNoTracking().SingleAsync(c => c.Period == NextAccOaQuotaRule.PeriodOf(now) && c.Kind == NextAccOaQuotaRule.KindPush);
        Assert.Equal(4600, row.Count);
        Assert.Equal(5000, row.Limit);
        Assert.NotNull(row.WarnedAt);
    }

    [Fact]
    public async Task ส่งเตือนไม่สำเร็จ_ไม่ประทับ_รอบหน้าลองใหม่_และต่ำกว่าเกณฑ์ไม่เตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var now = await FreshPeriodAsync(db, 2002);
        var pushed = new List<string>();
        Assert.Empty(await NextAccOaQuotaWatch.RunOnceAsync(Services(FakeLine(new NextAccOaQuotaReading(5000, 4600), pushed, pushOk: false)),
            NullLogger.Instance, now, default));
        Assert.Single(pushed);                                  // พยายามแล้ว แต่ไม่สำเร็จ
        Assert.Null((await db.NextAccOaUsageCounters.AsNoTracking().SingleAsync(c => c.Period == NextAccOaQuotaRule.PeriodOf(now) && c.Kind == NextAccOaQuotaRule.KindPush)).WarnedAt);
        Assert.Single(await NextAccOaQuotaWatch.RunOnceAsync(Services(FakeLine(new NextAccOaQuotaReading(5000, 4600), pushed)), NullLogger.Instance, now, default));

        var below = await FreshPeriodAsync(db, 2003);
        var none = new List<string>();
        Assert.Empty(await NextAccOaQuotaWatch.RunOnceAsync(Services(FakeLine(new NextAccOaQuotaReading(5000, 100), none)), NullLogger.Instance, below, default));
        Assert.Empty(await NextAccOaQuotaWatch.RunOnceAsync(Services(FakeLine(null, none)), NullLogger.Instance, below, default));   // LINE ไม่ตอบ ⇒ ไม่ล้ม
        Assert.Empty(none);
    }

    [Fact]
    public async Task reply_นับแบบ_atomic_และถึงเกณฑ์ฟรีแล้วเตือน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var period = NextAccOaQuotaRule.PeriodOf(DateTime.UtcNow);
        var before = await db.NextAccOaUsageCounters.AsNoTracking()
            .Where(c => c.Period == period && c.Kind == NextAccOaQuotaRule.KindReply).Select(c => (long?)c.Count).FirstOrDefaultAsync() ?? 0;
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            using var d = DbTestDatabase.TryCreateContext()!;
            await NextAccOaUsage.IncrementAsync(d, NextAccOaQuotaRule.KindReply, NullLogger.Instance);
        })));
        var after = await db.NextAccOaUsageCounters.AsNoTracking()
            .Where(c => c.Period == period && c.Kind == NextAccOaQuotaRule.KindReply).Select(c => c.Count).SingleAsync();
        Assert.Equal(before + 5, after);

        var now = await FreshPeriodAsync(db, 2004);
        await NextAccOaUsage.SetAsync(db, NextAccOaQuotaRule.PeriodOf(now), NextAccOaQuotaRule.KindReply, 23000, null);
        var pushed = new List<string>();
        var sent = await NextAccOaQuotaWatch.RunOnceAsync(Services(FakeLine(null, pushed)), NullLogger.Instance, now, default);
        Assert.Contains("ไม่ตัดโควตาผู้เช่า", Assert.Single(sent));
    }
}
