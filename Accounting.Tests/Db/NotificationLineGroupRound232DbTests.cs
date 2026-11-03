using System.Net;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ฝ่ายค้าน 232-Notify (B1 · S2) บนฐาน PostgreSQL จริง (trait Db · job <c>db-test</c>):
/// <para><b>B1</b> backfill <see cref="NotificationLineGroupBackfill.UpdateSql"/> — บริษัทที่ผูก OA+กลุ่ม และมีแถว <c>document.approved</c> ก่อนรอบ 232 ⇒ หลัง migration
/// <c>NotificationEngine.DispatchAsync</c> เรียก <c>TryPushCompanyGroupAsync</c> · ทิศตรงข้าม: บริษัทไม่มี OA/ไม่มีกลุ่ม ไม่ถูกแตะ ·
/// <see cref="NotificationLineGroupBackfill.MigrationSql"/> เมื่อคอลัมน์มีแล้ว (บูตถัดไป) = no-op ⇒ แถวที่ผู้ใช้ปิดกลุ่มเองไม่ถูกทับ</para>
/// <para><b>S2</b> <see cref="LineNotifyService"/> อ่านค่าตั้งจากฐานจริง: token ถอดไม่ได้ / ปิด LINE แต่ OA กลางมี token+กลุ่ม ⇒ 0 HTTP + RuleCode (ไม่ถอยไปกลุ่มกลาง)</para>
/// </summary>
[Trait("Category", "Db")]
public class NotificationLineGroupRound232DbTests
{
    private readonly ITestOutputHelper _out;
    public NotificationLineGroupRound232DbTests(ITestOutputHelper output) => _out = output;

    private static readonly SecretProtector Secrets = new("db-test-key-232");
    private const string Group = "C00000000000000000000000000000232";

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<(string? Auth, string Body)> Requests { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Headers.Authorization?.Parameter, request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }
    }

    private static async Task<Guid> SeedCompanyAsync(Accounting.Data.AccountingDbContext db, bool lineEnabled, string? storedToken, string? groupId)
    {
        var company = new Company { Name = "บริษัททดสอบกลุ่ม LINE 232", TaxId = "0105556000232" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id, LineEnabled = lineEnabled, LineChannelAccessToken = storedToken, LineDefaultGroupId = groupId });
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static NotificationSetting ApprovedRow(Guid companyId) => new()
    {
        CompanyId = companyId, EventKey = NotificationEvents.DocumentApproved, RecipientRole = NotificationRecipientRoles.Accounting,
        EnableSystem = true, EnableLineGroup = false,
    };

    private static LineNotifyService Line(Accounting.Data.AccountingDbContext db, CaptureHandler h, NextAccLineSettings central)
    {
        var factory = F4Proxy<IHttpClientFactory>.Create((m, a) => m == nameof(IHttpClientFactory.CreateClient) ? new HttpClient(h, disposeHandler: false) : null);
        var resolver = F4Proxy<INextAccLineConfig>.Create((m, a) => Task.FromResult(central));
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new LineNotifyService(factory, cfg, NullLogger<LineNotifyService>.Instance, db, Secrets, resolver);
    }

    private static NextAccLineSettings Central() => new("CENTRAL-TOKEN", "S", "@nextacc", null, "C" + new string('a', 32), NextAccLineConfigRule.SourceAdmin);

    // ───────── B1 ─────────

    [Fact]
    public async Task B1_บริษัทผูกOAและกลุ่ม_แถวเดิมก่อนรอบ232_หลังmigration_engineประกาศกลุ่มของบริษัท()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedCompanyAsync(db, lineEnabled: true, Secrets.Protect("company-token"), Group);
        db.NotificationSettings.Add(ApprovedRow(cid));
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(NotificationLineGroupBackfill.UpdateSql);

        using var db2 = DbTestDatabase.TryCreateContext()!;
        var row = await db2.NotificationSettings.AsNoTracking().SingleAsync(n => n.CompanyId == cid && n.EventKey == NotificationEvents.DocumentApproved);
        Assert.True(row.EnableLineGroup);
        Assert.True(row.EnableSystem);   // ช่องเดิมของผู้ใช้ไม่ถูกแตะ

        var groupCalls = new List<string>();
        var line = F4Proxy<ILineNotifyService>.Create((m, a) =>
        {
            if (m == nameof(ILineNotifyService.TryPushCompanyGroupAsync)) { groupCalls.Add((string)a[1]!); return LinePushResult.Success; }
            if (m == nameof(ILineNotifyService.TryPushViaNextAccAsync)) return LinePushResult.Success;
            return null;
        });
        var org = F4Proxy<IOrganizationService>.Create((_, _) => null);
        var engine = new NotificationEngine(db2, line, org, NullLogger<NotificationEngine>.Instance, Secrets);
        await engine.DispatchAsync(cid, NotificationEvents.DocumentApproved, new NotificationContext { Title = "อนุมัติเอกสาร INV-1", Message = "x" });
        var text = Assert.Single(groupCalls);
        Assert.Contains("อนุมัติเอกสาร INV-1", text);
    }

    [Fact]
    public async Task B1_ทิศตรงข้าม_บริษัทไม่มีOAหรือไม่มีกลุ่ม_ไม่ถูกแตะ_และบูตถัดไปไม่ทับแถวที่ผู้ใช้ปิดกลุ่ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        // (ก) บริษัทไม่มี OA (ปิด LINE) แถวเก่า
        var noOa = await SeedCompanyAsync(db, false, Secrets.Protect("t"), Group);
        db.NotificationSettings.Add(ApprovedRow(noOa));
        // (ข) มี OA แต่ไม่มีกลุ่ม
        var noGroup = await SeedCompanyAsync(db, true, Secrets.Protect("t"), null);
        db.NotificationSettings.Add(ApprovedRow(noGroup));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(NotificationLineGroupBackfill.UpdateSql);

        // (ค) ผูก OA+กลุ่ม แต่ผู้ใช้ปิดช่องกลุ่มเองหลังรอบนี้ (คอลัมน์มีอยู่แล้ว) ⇒ ก้อน migration ที่รันทุกบูตต้องไม่แตะ
        var userOff = await SeedCompanyAsync(db, true, Secrets.Protect("t"), Group);
        db.NotificationSettings.Add(ApprovedRow(userOff));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync(NotificationLineGroupBackfill.MigrationSql);
        await db.Database.ExecuteSqlRawAsync(NotificationLineGroupBackfill.MigrationSql);   // idempotent

        using var db2 = DbTestDatabase.TryCreateContext()!;
        foreach (var cid in new[] { noOa, noGroup, userOff })
        {
            var row = await db2.NotificationSettings.AsNoTracking().SingleAsync(n => n.CompanyId == cid && n.EventKey == NotificationEvents.DocumentApproved);
            Assert.False(row.EnableLineGroup);
        }
    }

    // ───────── S2 ─────────

    [Fact]
    public async Task S2_เปิดLINEแต่tokenถอดไม่ได้_ไม่ยิงHTTP_และไม่ถอยไปกลุ่มกลาง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var otherKey = new SecretProtector("another-key-232");
        var cid = await SeedCompanyAsync(db, lineEnabled: true, otherKey.Protect("company-token"), Group);   // เข้ารหัสด้วยคีย์อื่น ⇒ ถอดไม่ได้
        var h = new CaptureHandler();
        var svc = Line(db, h, Central());
        var g = await svc.TryPushCompanyGroupAsync(cid, "✅ เอกสารอนุมัติแล้ว");
        var u = await svc.TryPushToUserAsync(cid, "Ucustomer", "x");
        Assert.False(g.Ok); Assert.Equal(NotificationAudiencePolicy.RuleTenantNoCompanyOa, g.RuleCode);
        Assert.False(u.Ok); Assert.Equal(NotificationAudiencePolicy.RuleTenantNoCompanyOa, u.RuleCode);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task S2_ปิดLINEของบริษัท_แม้OAกลางมีtokenและกลุ่ม_ไม่ยิงHTTP()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedCompanyAsync(db, lineEnabled: false, Secrets.Protect("company-token"), Group);
        var h = new CaptureHandler();
        var svc = Line(db, h, Central());
        var g = await svc.TryPushCompanyGroupAsync(cid, "✅ เอกสารอนุมัติแล้ว");
        Assert.False(g.Ok);
        Assert.Equal(NotificationAudiencePolicy.RuleTenantNoCompanyOa, g.RuleCode);
        Assert.Empty(h.Requests);
        // ทิศตรงข้าม: บริษัทเดียวกันเปิด LINE + token ถอดได้ ⇒ ยิงด้วย token ของบริษัทเข้ากลุ่มของบริษัท (ไม่ใช่ token/กลุ่มกลาง)
        var s = await db.CompanySettings.SingleAsync(x => x.CompanyId == cid);
        s.LineEnabled = true;
        await db.SaveChangesAsync();
        var ok = await Line(DbTestDatabase.TryCreateContext()!, h, Central()).TryPushCompanyGroupAsync(cid, "✅ เอกสารอนุมัติแล้ว");
        Assert.True(ok.Ok);
        var req = Assert.Single(h.Requests);
        Assert.Equal("company-token", req.Auth);
        Assert.Contains("\"to\":\"" + Group + "\"", req.Body);
    }
}
