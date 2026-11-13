using System.Text;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 270 (ฝ่ายค้าน M4 · คำตัดสิน 270-2-4) — ที่เก็บคีย์ idempotency ใช้ไม่ได้ (ฐานไม่ตอบ):
/// ลองซ้ำหนึ่งครั้ง · LogError · header <c>Idempotency-Status: unavailable</c> ·
/// เส้นเงิน/เอกสาร ⇒ 503 ข้อความไทย <b>ไม่เรียก controller</b> (ไม่บันทึกซ้ำ) · ทิศตรงข้าม: เส้นจองหน้าเว็บ ⇒ ปล่อยผ่าน (มีด่านซ้ำชั้นที่สอง)
/// <para>ฐานปลอม = พอร์ตที่ไม่มีใครฟัง (ไม่ต้องมี PostgreSQL)</para>
/// </summary>
public class IdempotencyStoreUnavailableTests
{
    private static readonly IConfiguration Config = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Secret"] = "test-secret-for-idempotency-hmac-0123456789abcdef" }).Build();

    private static AccountingDbContext DeadDb() => new(new DbContextOptionsBuilder<AccountingDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=none;Username=x;Password=x;Timeout=2;Pooling=false").Options);

    private static async Task<(int Status, string? Header, bool NextCalled, string Body, List<string> Logs)> RunAsync(string path)
    {
        var logger = new Db.SubmitOnceRound217DbTests.ListLogger<IdempotencyMiddleware>();
        var called = false;
        var mw = new IdempotencyMiddleware(http => { called = true; http.Response.StatusCode = 201; return Task.CompletedTask; }, logger, Config);
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Request.Method = "POST";
        ctx.Request.Path = path;
        ctx.Request.Headers[IdempotencyKeyPolicy.Header] = Guid.NewGuid().ToString();
        ctx.Request.ContentType = "application/json";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"amount\":100}"));
        var body = new MemoryStream();
        ctx.Response.Body = body;
        using var db = DeadDb();
        await mw.InvokeAsync(ctx, db);
        return (ctx.Response.StatusCode, ctx.Response.Headers[IdempotencyKeyPolicy.StatusHeader].FirstOrDefault(), called,
            Encoding.UTF8.GetString(body.ToArray()), logger.Lines.ToList());
    }

    [Fact]
    public async Task M4_เส้นเงินเอกสาร_ที่เก็บคีย์ใช้ไม่ได้_503ข้อความไทย_ไม่เรียกcontroller_LogError()
    {
        var r = await RunAsync($"/api/companies/{Guid.NewGuid()}/documents/{Guid.NewGuid()}/payments");
        Assert.Equal(503, r.Status);
        Assert.False(r.NextCalled);   // ไม่ปล่อยให้บันทึกซ้ำผ่าน
        Assert.Equal(IdempotencyKeyPolicy.StatusUnavailable, r.Header);
        Assert.Contains("ลองใหม่", r.Body);
        Assert.Contains(r.Logs, l => l.StartsWith("Error:"));
    }

    [Fact]
    public async Task M4_ทิศตรงข้าม_เส้นจองหน้าเว็บ_ปล่อยผ่านพร้อมheader_LogErrorไม่เงียบ()
    {
        var r = await RunAsync($"/api/companies/{Guid.NewGuid()}/cms/sites/{Guid.NewGuid()}/booking/bookings");
        Assert.True(r.NextCalled);
        Assert.Equal(201, r.Status);
        Assert.Equal(IdempotencyKeyPolicy.StatusUnavailable, r.Header);
        Assert.Contains(r.Logs, l => l.StartsWith("Error:"));
    }

    [Theory]
    [InlineData("/api/companies/x/documents", true)]
    [InlineData("/api/companies/x/documents/y/approve", true)]
    [InlineData("/api/companies/x/payment-intents/y/confirm", true)]
    [InlineData("/api/companies/x/public-pay/y", true)]
    [InlineData("/api/companies/x/journal-entries", true)]
    [InlineData("/api/companies/x/cms/sites/y/commerce/orders", true)]
    [InlineData("/api/companies/x/cms/sites/y/booking/bookings", false)]
    [InlineData("/api/companies/x/contacts", false)]
    [InlineData("/api/companies/x/settings", false)]
    public void M4_ชุดเส้นที่ปิดเมื่อที่เก็บคีย์ใช้ไม่ได้(string path, bool closed)
        => Assert.Equal(closed, IdempotencyKeyPolicy.FailClosedWhenStoreUnavailable(path));

    [Fact]
    public void M4_ความผิดพลาดชั่วคราวลองซ้ำ_ความผิดพลาดของคำสั่งไม่ลองซ้ำ()
    {
        Assert.True(IdempotencyKeyPolicy.IsTransientStoreError(new TimeoutException()));
        Assert.True(IdempotencyKeyPolicy.IsTransientStoreError(new InvalidOperationException("x", new TimeoutException())));
        Assert.False(IdempotencyKeyPolicy.IsTransientStoreError(new ArgumentException("bad")));
    }
}
