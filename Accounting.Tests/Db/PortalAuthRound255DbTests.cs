using System.Security.Claims;
using System.Text.Encodings.Web;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Portal;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 255 WP-12B ฝ่ายค้าน H1/M9 — scheme "Portal" ของพอร์ทัลลูกค้า (ทดสอบตรงที่ตัว handler · ไม่มี WebApplicationFactory ในชุดเทสต์):
/// โทเคน access ผ่าน + เส้นเอกสาร/ส่งออก 200 · โทเคนพนักงาน (JWT) บนเส้นพอร์ทัล = 401 · โทเคนพอร์ทัลบนเส้นพนักงาน (JwtBearer อ่านไม่ออก) = 401 ·
/// สิทธิ์ถูกปิด = 401 · refresh ใช้แทน access ไม่ได้ (และ access ใช้ refresh ไม่ได้) · บริษัทในเส้นทางไม่ตรงโทเคน = 401
/// </summary>
public class PortalAuthRound255DbTests
{
    private readonly ITestOutputHelper _out;
    public PortalAuthRound255DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Portal:SigningKey"] = "r255-portal-signing-key-for-db-tests-0123456789",
        ["Jwt:Secret"] = "r255-staff-jwt-secret-for-db-tests-0123456789abcdef",
        ["Jwt:Issuer"] = "accounting", ["Jwt:Audience"] = "accounting",
        ["App:BaseUrl"] = "https://acc.r255.test",
    }).Build();

    private sealed class SchemeOptions : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private static async Task<AuthenticateResult> AuthAsync(IPortalService portal, string? bearer, Guid? routeCompany)
    {
        var services = new ServiceCollection().AddSingleton(portal).BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        if (bearer != null) ctx.Request.Headers.Authorization = "Bearer " + bearer;
        if (routeCompany is Guid rc) ctx.Request.RouteValues["companyId"] = rc.ToString();
        var handler = new PortalAuthenticationHandler(new SchemeOptions(), NullLoggerFactory.Instance, UrlEncoder.Default);
        await handler.InitializeAsync(new AuthenticationScheme(PortalTokenPolicy.Scheme, null, typeof(PortalAuthenticationHandler)), ctx);
        return await handler.AuthenticateAsync();
    }

    private static async Task<(Company Shop, Contact Contact, PortalAccess Access, PortalLoginResponse Session)> SeedAsync(AccountingDbContext db, PortalService svc)
    {
        var shop = new Company { Name = "ร้านพอร์ทัล 255 " + Guid.NewGuid().ToString("N")[..6], TaxId = "09" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Random.Shared.Next(10)))) };
        db.Companies.Add(shop);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = shop.Id, Name = "ลูกค้าพอร์ทัล 255", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var email = $"portal-{Guid.NewGuid():N}@cust.test";
        var access = new PortalAccess
        {
            CompanyId = shop.Id, ContactId = contact.Id, Email = email, IsActive = true,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Portal#255-pass", workFactor: 4),
        };
        db.PortalAccesses.Add(access);
        await db.SaveChangesAsync();
        var session = await svc.LoginAsync(new PortalLoginRequest(email, "Portal#255-pass", shop.Id));
        return (shop, contact, access, session);
    }

    [Fact]
    public async Task R255_H1_โทเคนaccess_ผ่าน_เส้นเอกสารและส่งออก_200()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var svc = new PortalService(db, Config());
        var s = await SeedAsync(db, svc);

        var ok = await AuthAsync(svc, s.Session.AccessToken, s.Shop.Id);
        Assert.True(ok.Succeeded);
        Assert.Equal(s.Contact.Id.ToString(), ok.Principal!.FindFirst(PortalTokenPolicy.ClaimContactId)?.Value);

        // เส้นจริงของ controller ด้วย principal ที่ handler ออกให้
        var ctl = new PortalPublicController(svc)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = ok.Principal! } },
        };
        var docs = await ctl.GetDocuments(s.Shop.Id, null);
        Assert.IsType<OkObjectResult>(docs.Result);
        var today = DateTime.UtcNow.Date;
        var export = await ctl.ExportAccountant(s.Shop.Id, today.AddDays(-30), today, default);
        Assert.IsType<FileContentResult>(export);

        // ทิศตรงข้าม: ไม่มี principal ของพอร์ทัล ⇒ 401 (ไม่ใช่ข้อมูลว่าง)
        var anon = new PortalPublicController(svc) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<UnauthorizedObjectResult>((await anon.GetDocuments(s.Shop.Id, null)).Result);
    }

    [Fact]
    public async Task R255_H1_M9_โทเคนผิดชนิด_ผิดบริษัท_สิทธิ์ปิด_โทเคนพนักงาน_ไม่ผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var cfg = Config();
        var svc = new PortalService(db, cfg);
        var s = await SeedAsync(db, svc);

        // โทเคนพนักงาน (JWT) บนเส้นพอร์ทัล ⇒ 401
        var staffJwt = JwtHelper.GenerateToken(Guid.NewGuid(), "staff@shop.test", "พนักงาน", cfg);
        Assert.False((await AuthAsync(svc, staffJwt, s.Shop.Id)).Succeeded);
        // โทเคนพอร์ทัลบนเส้นพนักงาน: JwtBearer อ่านไม่ออก ⇒ 401 (ไม่ใช่ JWT)
        Assert.False(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().CanReadToken(s.Session.AccessToken));
        // refresh ใช้แทน access ไม่ได้
        Assert.False((await AuthAsync(svc, s.Session.RefreshToken, s.Shop.Id)).Succeeded);
        Assert.Null(svc.ExtractContactIdFromToken(s.Session.RefreshToken));
        // access ใช้ที่ /refresh ไม่ได้ · refresh ใช้ได้
        await Assert.ThrowsAnyAsync<Exception>(() => svc.RefreshTokenAsync(s.Session.AccessToken));
        var renewed = await svc.RefreshTokenAsync(s.Session.RefreshToken);
        Assert.True((await AuthAsync(svc, renewed.AccessToken, s.Shop.Id)).Succeeded);
        // บริษัทในเส้นทางไม่ตรงโทเคน ⇒ 401
        Assert.False((await AuthAsync(svc, s.Session.AccessToken, Guid.NewGuid())).Succeeded);
        // ไม่มีหัว Authorization ⇒ ไม่มีผล (challenge = 401)
        Assert.True((await AuthAsync(svc, null, s.Shop.Id)).None);
        // โทเคนแก้ไบต์ ⇒ ลายเซ็นไม่ผ่าน
        var bytes = Convert.FromBase64String(s.Session.AccessToken);
        bytes[2] ^= 0x01;
        Assert.False((await AuthAsync(svc, Convert.ToBase64String(bytes), s.Shop.Id)).Succeeded);

        // สิทธิ์ถูกปิด ⇒ โทเคนเดิมใช้ไม่ได้ทันที (ตรวจทุกคำขอ)
        await db.PortalAccesses.IgnoreQueryFilters().Where(a => a.Id == s.Access.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(a => a.IsActive, false));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var svc2 = new PortalService(c2, cfg);
        Assert.False((await AuthAsync(svc2, s.Session.AccessToken, s.Shop.Id)).Succeeded);
        await Assert.ThrowsAnyAsync<Exception>(() => svc2.RefreshTokenAsync(s.Session.RefreshToken));
    }
}
