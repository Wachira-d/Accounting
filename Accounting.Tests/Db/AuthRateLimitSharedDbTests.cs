using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 235 (OMISE_READINESS S4 · กฎ #4 D) — ถังสมัครบัญชีของ <see cref="AuthRateLimitPolicy"/> นับบน PostgreSQL จริง
/// <b>ข้าม instance</b>: สอง <see cref="ChatRateLimiter"/> คนละ DbContext (= สองเครื่อง) ใช้ถังเดียวกัน ⇒ เพดานไม่คูณจำนวนเครื่อง ·
/// ทิศตรงข้าม: IP อื่นไม่ถูกนับรวม (ถังต่อ IP แยกกันจริง)
/// </summary>
[Trait("Category", "Db")]
public class AuthRateLimitSharedDbTests
{
    private readonly ITestOutputHelper _out;
    public AuthRateLimitSharedDbTests(ITestOutputHelper output) => _out = output;

    private static async Task EnsureTableAsync(AccountingDbContext db)
    {
        foreach (var sql in DatabaseMigrationHelper.ChatRateBucketStatements())
            await db.Database.ExecuteSqlRawAsync(sql);
    }

    [Fact]
    public async Task ถังสมัครต่อ_IP_นับรวมข้ามสองเครื่อง_และ_IP_อื่นไม่ถูกนับรวม()
    {
        using var probe = DbTestDatabase.TryCreateContext();
        if (probe == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsureTableAsync(probe);

        // IP สุ่มต่อรอบ — ฐานเดียวกันทั้ง job ไม่ลบข้อมูล
        var ip = $"198.51.{Random.Shared.Next(0, 255)}.{Random.Shared.Next(0, 255)}-{Guid.NewGuid():N}"[..40];
        var other = $"203.0.{Random.Shared.Next(0, 255)}.{Random.Shared.Next(0, 255)}-{Guid.NewGuid():N}"[..40];
        var s = new AuthRateLimitPolicy.Settings(RegisterPerMinutePerIp: 3, RegisterPerDayPerIp: 50);
        var bucket = AuthRateLimitPolicy.BucketsFor(AuthRateLimitPolicy.RouteGroup.Register, ip, s)
            .Single(b => b.Key.StartsWith("auth-register:ip:"));
        Assert.True(bucket.FailClosed);

        using var dbA = DbTestDatabase.TryCreateContext()!;
        using var dbB = DbTestDatabase.TryCreateContext()!;
        var nodeA = new ChatRateLimiter(dbA, NullLogger<ChatRateLimiter>.Instance);
        var nodeB = new ChatRateLimiter(dbB, NullLogger<ChatRateLimiter>.Instance);

        // ช่วงรอยต่อนาที (fixed window) อาจรีเซ็ตกลางเทสต์ — รอให้พ้นวินาทีท้าย ๆ ของนาทีก่อน
        if (DateTime.UtcNow.Second >= 57) await Task.Delay(TimeSpan.FromSeconds(61 - DateTime.UtcNow.Second));

        Assert.True(await nodeA.TryConsumeAsync(bucket.Key, bucket.PerMinute, bucket.PerDay, failClosed: bucket.FailClosed));
        Assert.True(await nodeB.TryConsumeAsync(bucket.Key, bucket.PerMinute, bucket.PerDay, failClosed: bucket.FailClosed));
        Assert.True(await nodeA.TryConsumeAsync(bucket.Key, bucket.PerMinute, bucket.PerDay, failClosed: bucket.FailClosed));
        // ครั้งที่ 4 บนเครื่อง B — เดิม (ตัวนับต่อเครื่อง) เครื่อง B เพิ่งเห็น 1 ครั้งจึงปล่อยผ่าน
        Assert.False(await nodeB.TryConsumeAsync(bucket.Key, bucket.PerMinute, bucket.PerDay, failClosed: bucket.FailClosed));
        // N4: สถานะแยก "เกินเพดาน" (ไม่ใช่ Unavailable) — middleware ตอบ 429 ไม่ใช่ 503
        Assert.Equal(RateConsumeOutcome.Limited, await nodeA.ConsumeAsync(bucket.Key, bucket.PerMinute, bucket.PerDay));

        var otherBucket = AuthRateLimitPolicy.BucketsFor(AuthRateLimitPolicy.RouteGroup.Register, other, s)
            .Single(b => b.Key.StartsWith("auth-register:ip:"));
        Assert.True(await nodeA.TryConsumeAsync(otherBucket.Key, otherBucket.PerMinute, otherBucket.PerDay, failClosed: true));
    }
}
