using Accounting.Helpers;
using Accounting.Middleware;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 235 (OMISE_READINESS S4 · กฎ #4 D · ฝ่ายค้าน S1/N4) — เส้นสมัคร/รีเซ็ตรหัสผ่าน นับที่ฐานข้อมูลต่อ IP แบบ fail-closed ผ่าน
/// <see cref="AuthRateLimitPolicy"/> ตัวเดียว · ถังทั้งระบบเป็นสัญญาณเตือนแอดมิน (ไม่ปฏิเสธ) · IPv6 นับต่อ /64 · ตารางนับใช้ไม่ได้ = 503 ข้อความกลาง ·
/// เส้นล็อกอินคงถังเดิม (fail-open) · เส้นที่ไม่ใช่ auth ไม่แตะฐานข้อมูล
/// </summary>
public class AuthRateLimitRound235Tests
{
    private static readonly AuthRateLimitPolicy.Settings S = new();

    [Theory]
    [InlineData("/api/auth/register", AuthRateLimitPolicy.RouteGroup.Register)]
    [InlineData("/API/Auth/Register", AuthRateLimitPolicy.RouteGroup.Register)]
    [InlineData("/api/auth/forgot-password", AuthRateLimitPolicy.RouteGroup.PasswordReset)]
    [InlineData("/api/auth/reset-password", AuthRateLimitPolicy.RouteGroup.PasswordReset)]
    [InlineData("/api/auth/login", AuthRateLimitPolicy.RouteGroup.SignIn)]
    [InlineData("/api/auth/refresh", AuthRateLimitPolicy.RouteGroup.SignIn)]
    [InlineData("/api/auth/sso", AuthRateLimitPolicy.RouteGroup.SignIn)]
    [InlineData("/api/auth/external-logins/link", AuthRateLimitPolicy.RouteGroup.SignIn)]
    [InlineData("/api/portal/login", AuthRateLimitPolicy.RouteGroup.SignIn)]
    [InlineData("/api/companies/abc/portal/login", AuthRateLimitPolicy.RouteGroup.SignIn)]
    [InlineData("/api/portal/register", AuthRateLimitPolicy.RouteGroup.PortalRegister)]
    [InlineData("/api/companies/abc/portal/register", AuthRateLimitPolicy.RouteGroup.PortalRegister)]
    [InlineData("/api/auth/profile", AuthRateLimitPolicy.RouteGroup.None)]
    [InlineData("/api/public/checkout/catalog", AuthRateLimitPolicy.RouteGroup.None)]
    [InlineData("/index.html", AuthRateLimitPolicy.RouteGroup.None)]
    [InlineData(null, AuthRateLimitPolicy.RouteGroup.None)]
    public void Classify_รายชื่อเส้นเดิมครบ_และแยกกลุ่ม(string? path, AuthRateLimitPolicy.RouteGroup expected)
        => Assert.Equal(expected, AuthRateLimitPolicy.Classify(path));

    [Fact]
    public void สมัครบัญชี_ถังเดิม_fail_open_ต่อ_IP_fail_closed_ทั้งระบบเป็นสัญญาณ()
    {
        var b = AuthRateLimitPolicy.BucketsFor(AuthRateLimitPolicy.RouteGroup.Register, "1.2.3.4", S);
        Assert.Equal(3, b.Count);
        Assert.Equal(new AuthRateLimitPolicy.Bucket("auth:1.2.3.4", 10, 600, false), b[0]);
        Assert.Equal("auth-register:ip:1.2.3.4", b[1].Key);
        Assert.True(b[1].FailClosed);
        Assert.True(b[1].Enforce);
        Assert.Equal("auth-register:global", b[2].Key);
        Assert.False(b[2].Enforce);   // S1: ไม่ใช่ตัวปฏิเสธ
    }

    [Fact]
    public void ล็อกอิน_คงพฤติกรรมเดิม_ถังเดียว_fail_open()
    {
        var b = AuthRateLimitPolicy.BucketsFor(AuthRateLimitPolicy.RouteGroup.SignIn, "1.2.3.4", S);
        Assert.Single(b);
        Assert.False(b[0].FailClosed);
        Assert.Equal("auth:1.2.3.4", b[0].Key);
    }

    [Fact]
    public void สมัครประตูลูกค้า_ไม่มีถังทั้งระบบ_ร้านหนึ่งไม่กันร้านอื่น()
    {
        var b = AuthRateLimitPolicy.BucketsFor(AuthRateLimitPolicy.RouteGroup.PortalRegister, "1.2.3.4", S);
        Assert.DoesNotContain(b, x => x.Key.Contains("global"));
        Assert.Contains(b, x => x.FailClosed);
    }

    [Fact]
    public void ไม่ใช่เส้น_auth_ไม่มีถัง_และคีย์ว่างไม่ทำให้คีย์ว่าง()
    {
        Assert.Empty(AuthRateLimitPolicy.BucketsFor(AuthRateLimitPolicy.RouteGroup.None, "1.2.3.4", S));
        Assert.Equal("auth:unknown", AuthRateLimitPolicy.BucketsFor(AuthRateLimitPolicy.RouteGroup.SignIn, null, S)[0].Key);
    }

    // ── S1: คีย์ต่อ IP ──
    [Fact]
    public void ClientKey_IPv4_เต็มที่อยู่_IPv6_ต่อ_64_IPv4mapped_เป็น_IPv4()
    {
        Assert.Equal("203.0.113.7", AuthRateLimitPolicy.ClientKey(System.Net.IPAddress.Parse("203.0.113.7")));
        Assert.Equal("203.0.113.7", AuthRateLimitPolicy.ClientKey(System.Net.IPAddress.Parse("::ffff:203.0.113.7")));
        var a = AuthRateLimitPolicy.ClientKey(System.Net.IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:1"));
        var b = AuthRateLimitPolicy.ClientKey(System.Net.IPAddress.Parse("2001:db8:1:2:ffff:0:0:9"));
        var other = AuthRateLimitPolicy.ClientKey(System.Net.IPAddress.Parse("2001:db8:1:3::1"));
        Assert.Equal(a, b);              // /64 เดียวกัน = ถังเดียว (เลี่ยงด้วยการเปลี่ยนที่อยู่ภายใน /64 ไม่ได้)
        Assert.NotEqual(a, other);       // ทิศตรงข้าม: /64 อื่นแยกถัง
        Assert.EndsWith("/64", a);
        Assert.Equal("unknown", AuthRateLimitPolicy.ClientKey(null));
    }

    // ── S1/N4: คำตัดสินต่อถัง ──
    [Fact]
    public void Decide_ต่อ_IP_fail_closed_ทั้งระบบเป็นสัญญาณ_ล็อกอิน_fail_open()
    {
        var perIp = new AuthRateLimitPolicy.Bucket("x", 1, 1, FailClosed: true);
        var global = new AuthRateLimitPolicy.Bucket("g", 1, 1, FailClosed: false, Enforce: false);
        var login = new AuthRateLimitPolicy.Bucket("l", 1, 1, FailClosed: false);
        Assert.Equal(AuthRateLimitPolicy.Verdict.Pass, AuthRateLimitPolicy.Decide(perIp, RateConsumeOutcome.Allowed));
        Assert.Equal(AuthRateLimitPolicy.Verdict.Deny, AuthRateLimitPolicy.Decide(perIp, RateConsumeOutcome.Limited));
        Assert.Equal(AuthRateLimitPolicy.Verdict.DenyUnavailable, AuthRateLimitPolicy.Decide(perIp, RateConsumeOutcome.Unavailable));
        Assert.Equal(AuthRateLimitPolicy.Verdict.DenyUnavailable, AuthRateLimitPolicy.Decide(perIp, null));
        Assert.Equal(AuthRateLimitPolicy.Verdict.Alert, AuthRateLimitPolicy.Decide(global, RateConsumeOutcome.Limited));
        Assert.Equal(AuthRateLimitPolicy.Verdict.Pass, AuthRateLimitPolicy.Decide(global, RateConsumeOutcome.Unavailable));
        Assert.Equal(AuthRateLimitPolicy.Verdict.Pass, AuthRateLimitPolicy.Decide(login, RateConsumeOutcome.Unavailable));
        Assert.Equal(AuthRateLimitPolicy.Verdict.Deny, AuthRateLimitPolicy.Decide(login, RateConsumeOutcome.Limited));
    }

    [Fact]
    public void ค่าตั้งอ่านจาก_config_ค่าเสีย_ใช้ค่าเริ่มต้น()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimit:Register:PerMinuteGlobal"] = "7",
            ["RateLimit:Register:PerDayPerIp"] = "abc",
        }).Build();
        var s = AuthRateLimitPolicy.Settings.From(cfg);
        Assert.Equal(7, s.RegisterPerMinuteGlobal);
        Assert.Equal(new AuthRateLimitPolicy.Settings().RegisterPerDayPerIp, s.RegisterPerDayPerIp);
    }

    // ── ต่อสายใน middleware จริง (fake ตัวนับ) ──

    private sealed class FakeLimiter : IChatRateLimiter
    {
        public readonly List<string> Calls = new();
        public Func<string, RateConsumeOutcome> Outcome = _ => RateConsumeOutcome.Allowed;
        public Task<bool> TryConsumeAsync(string key, int perMinute, int perDay, CancellationToken ct = default, bool failClosed = false)
            => Task.FromResult(Outcome(key) == RateConsumeOutcome.Allowed || (!failClosed && Outcome(key) == RateConsumeOutcome.Unavailable));
        public Task<RateConsumeOutcome> ConsumeAsync(string key, int perMinute, int perDay, CancellationToken ct = default)
        { Calls.Add(key); return Task.FromResult(Outcome(key)); }
        public Task<int> RecordStrikeAsync(string key, CancellationToken ct = default) => Task.FromResult(1);
        public Task<int> PurgeOldAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private static async Task<(int Status, bool NextCalled, string Body)> Run(string path, IChatRateLimiter? limiter, string ip)
    {
        var services = new ServiceCollection();
        if (limiter != null) services.AddSingleton<IChatRateLimiter>(limiter);
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.Request.Path = path;
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(ip);
        var body = new MemoryStream();
        ctx.Response.Body = body;
        var next = false;
        // IP สุ่มต่อเทสต์ — ตัวนับในหน่วยความจำของ middleware เป็น static ห้ามให้เทสต์อื่นมาชน
        var mw = new RateLimitMiddleware(_ => { next = true; return Task.CompletedTask; },
            new ConfigurationBuilder().Build());
        await mw.InvokeAsync(ctx);
        // ข้อความไทยใน JSON ถูก escape (\uXXXX) — อ่าน message ผ่าน JsonDocument
        var raw = System.Text.Encoding.UTF8.GetString(body.ToArray());
        var message = raw.Length == 0 ? "" : System.Text.Json.JsonDocument.Parse(raw).RootElement.GetProperty("message").GetString() ?? "";
        return (ctx.Response.StatusCode, next, message);
    }

    private static string RandomIp() => $"10.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}";

    [Fact]
    public async Task สมัครบัญชี_นับถังต่อ_IP_และถังทั้งระบบผ่านตัวนับร่วม()
    {
        var f = new FakeLimiter();
        var ip = RandomIp();
        var (status, next, _) = await Run("/api/auth/register", f, ip);
        Assert.True(next);
        Assert.Equal(200, status);
        Assert.Contains($"auth-register:ip:{ip}", f.Calls);
        Assert.Contains("auth-register:global", f.Calls);
        Assert.Contains($"auth:{ip}", f.Calls);
    }

    [Fact]
    public async Task ถังทั้งระบบเต็ม_ไม่ปฏิเสธ_แต่เตือนวันละครั้ง()
    {
        var f = new FakeLimiter { Outcome = k => k == "auth-register:global" ? RateConsumeOutcome.Limited : RateConsumeOutcome.Allowed };
        var (status, next, _) = await Run("/api/auth/register", f, RandomIp());
        Assert.True(next);
        Assert.Equal(200, status);
        Assert.Contains(f.Calls, k => k.StartsWith("auth-alert:auth-register:global:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ถังต่อ_IP_เต็ม_429_ข้อความเกินเพดาน()
    {
        var ip = RandomIp();
        var f = new FakeLimiter { Outcome = k => k == $"auth-register:ip:{ip}" ? RateConsumeOutcome.Limited : RateConsumeOutcome.Allowed };
        var (status, next, body) = await Run("/api/auth/register", f, ip);
        Assert.Equal(429, status);
        Assert.False(next);
        Assert.Contains("บ่อยเกินไป", body);
    }

    [Fact]
    public async Task ตารางนับใช้ไม่ได้_สมัคร_503_ข้อความกลาง_ล็อกอินยังผ่าน()
    {
        var f = new FakeLimiter { Outcome = _ => RateConsumeOutcome.Unavailable };
        var reg = await Run("/api/auth/register", f, RandomIp());
        Assert.Equal(503, reg.Status);
        Assert.Contains(AuthRateLimitPolicy.UnavailableMessage, reg.Body);
        Assert.True((await Run("/api/auth/login", f, RandomIp())).NextCalled);
    }

    [Fact]
    public async Task ไม่มีตัวนับร่วม_สมัครและรีเซ็ต_ปฏิเสธ_แต่ล็อกอินยังผ่าน()
    {
        Assert.Equal(503, (await Run("/api/auth/register", null, RandomIp())).Status);
        Assert.Equal(503, (await Run("/api/auth/forgot-password", null, RandomIp())).Status);
        Assert.True((await Run("/api/auth/login", null, RandomIp())).NextCalled);
    }

    [Fact]
    public async Task เส้นทั่วไป_ไม่แตะตัวนับร่วม()
    {
        var f = new FakeLimiter();
        var (_, next, _) = await Run("/api/public/checkout/catalog", f, RandomIp());
        Assert.True(next);
        Assert.Empty(f.Calls);
    }
}
