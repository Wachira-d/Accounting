using System.Security.Claims;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 245 L1 (LINE_MULTICHANNEL_DESIGN §4.3/§7.1/§8 L1) บน PostgreSQL จริง (trait Db · job <c>db-test</c>):
/// <para>(1) one-shot <see cref="ContactNotifyEndpointMigration.MigrateLegacyLineUserIdsAsync"/>: <c>Contact.LineUserId</c> ⇒ แถว legacy ที่เข้ารหัส (OA ไม่รู้ · ไม่ใช้ส่ง) +
/// ล้างคอลัมน์ · ผู้ติดต่อที่ลบ = ล้างอย่างเดียว · ครั้งที่สองไม่ทำซ้ำ · audit ไม่มี LINE id · <c>Contact.Email</c> ไม่ถูกคัดลอกและยังได้ทุกเรื่อง (ไม่ถดถอย)</para>
/// <para>(2) tenant: ผู้ติดต่อของบริษัทอื่น ⇒ resolver ชุดว่าง · service/controller 404 · ความยินยอมของบริษัทอื่นไม่นับ</para>
/// <para>(3) CRUD + audit chain + ด่านสิทธิ์ (Viewer 403 ไม่เขียนอะไร · Owner ผ่าน) · กลุ่มกลางของระบบ (Q1(b)) ไม่เป็นปลายทาง</para>
/// </summary>
[Trait("Category", "Db")]
public class ContactNotifyEndpointRound245DbTests
{
    private readonly ITestOutputHelper _out;
    public ContactNotifyEndpointRound245DbTests(ITestOutputHelper output) => _out = output;

    private static readonly SecretProtector Secrets = new("db-test-key-245");
    private const string PlatformGroup = "C245000000000000000000000000000ab";

    /// <summary>ฝ่ายค้าน S2: กลุ่มกลางจาก config (Line:GroupId) — คนละค่ากับหน้าแอดมิน</summary>
    private const string ConfigGroup = "C245cccccccccccccccccccccccccccc0c";

    /// <summary>ผู้อ่านค่าตั้ง OA กลางปลอม — <c>PlatformGroupIdsAsync</c> คืนทั้งสองแหล่ง (ตัวจริงแยกอ่านหน้าแอดมิน + config)</summary>
    private static INextAccLineConfig Central(string? adminGroup = PlatformGroup, string? configGroup = ConfigGroup)
        => F4Proxy<INextAccLineConfig>.Create((m, a) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
            ? Task.FromResult(NextAccLineConfigRule.PlatformGroupIds(adminGroup, configGroup))
            : Task.FromResult(new NextAccLineSettings("T", "S", "@nextacc", null, adminGroup, NextAccLineConfigRule.SourceAdmin)));

    private static ContactRecipientResolver Resolver(AccountingDbContext db) => new(db, Secrets, Central());
    private static ContactNotifyEndpointService Service(AccountingDbContext db) => new(db, Resolver(db));
    private static ContactNotifyActor Actor(Guid? uid = null) => new(uid, "tester@r245.local", "127.0.0.1");

    private static async Task<(Guid CompanyId, Guid ContactId)> SeedAsync(AccountingDbContext db, string? email = "ap@r245.co.th", string? lineUserId = null, bool deleted = false)
    {
        var company = new Company { Name = "บริษัททดสอบช่องทางแจ้งเตือน 245", TaxId = "0105556000245" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var c = new Contact { CompanyId = company.Id, Name = "ลูกค้า 245", Email = email, LineUserId = lineUserId, IsCustomer = true, IsDeleted = deleted };
        db.Contacts.Add(c);
        await db.SaveChangesAsync();
        return (company.Id, c.Id);
    }

    private static async Task<Guid> SeedUserAsync(AccountingDbContext db, Guid companyId, UserRole role)
    {
        var u = new User { Email = $"r245-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ทดสอบ 245" };
        db.Users.Add(u);
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = companyId, Role = role });
        await db.SaveChangesAsync();
        return u.Id;
    }

    private static ContactNotifyEndpointsController Controller(AccountingDbContext db, Guid userId)
    {
        var ctl = new ContactNotifyEndpointsController(Resolver(db), Service(db), new PermissionService(db));
        ctl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(ClaimTypes.Email, "ctl@r245.local"),
                }, "test")),
            },
        };
        return ctl;
    }

    private static int? Status<T>(ActionResult<T> r) => r.Result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => null,
    };

    private static async Task RunMigrationAgainAsync(AccountingDbContext db)
    {
        foreach (var sql in ContactNotifyEndpointMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        // marker เป็นของทั้งฐาน (job เดียวกันทุกเทสต์) ⇒ ลบเฉพาะ marker ของรอบนี้แล้วรันใหม่ (เส้นเดียวกับบูต)
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OneShotDataFixes" WHERE "Key" = {0}""", ContactNotifyEndpointMigration.MarkerKey);
        await ContactNotifyEndpointMigration.MigrateLegacyLineUserIdsAsync(db, Secrets);
    }

    // ───── (1) migration ─────

    [Fact]
    public async Task Migration_LineUserIdเดิม_เป็นแถวlegacyเข้ารหัส_ล้างคอลัมน์_ไม่ใช้ส่ง_อีเมลเดิมไม่ถดถอย()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        const string lineId = "U245aaaaaaaaaaaaaaaaaaaaaaaaaaaaa1";
        var (cid, contactId) = await SeedAsync(db, email: "ap@r245.co.th", lineUserId: lineId);
        var (cid2, deletedContact) = await SeedAsync(db, email: null, lineUserId: "U245bbbbbbbbbbbbbbbbbbbbbbbbbbbbb2", deleted: true);
        var (cid3, emailOnly) = await SeedAsync(db, email: "only@r245.co.th");

        await RunMigrationAgainAsync(db);

        using var db2 = DbTestDatabase.TryCreateContext()!;
        var row = await db2.ContactNotifyEndpoints.SingleAsync(e => e.CompanyId == cid && e.ContactId == contactId);
        Assert.Equal(NotifyEndpointKind.LineUser, row.Kind);
        Assert.Equal(NotifyEndpointKind.ScopeUnknown, row.OaScope);
        Assert.Equal(NotifyEndpointKind.SourceLegacyLineUserId, row.Source);
        Assert.False(row.Verified);
        Assert.NotEqual(lineId, row.LineUserId);
        Assert.Equal(lineId, Secrets.Unprotect(row.LineUserId));
        Assert.Equal(NotifyEndpointKind.LineIdHash(null, lineId), row.LineIdHash);
        Assert.Null((await db2.Contacts.IgnoreQueryFilters().SingleAsync(c => c.Id == contactId)).LineUserId);
        // ผู้ติดต่อที่ลบแล้ว: ล้างอย่างเดียว ไม่สร้างปลายทาง
        Assert.Null((await db2.Contacts.IgnoreQueryFilters().SingleAsync(c => c.Id == deletedContact)).LineUserId);
        Assert.False(await db2.ContactNotifyEndpoints.IgnoreQueryFilters().AnyAsync(e => e.CompanyId == cid2));
        // Contact.Email ไม่ถูกคัดลอก (ความจริงแหล่งเดียว)
        Assert.False(await db2.ContactNotifyEndpoints.AnyAsync(e => e.CompanyId == cid3));
        // audit: แถวสรุปต่อบริษัท + แถวของ ChangeTracker ไม่มี LINE id (ทั้งเปล่าและเข้ารหัส)
        Assert.True(await db2.AuditLogs.AnyAsync(a => a.CompanyId == cid && a.EntityType == ContactNotifyEndpointMigration.AuditEntityType && a.RowHash != null));
        var entityAudit = await db2.AuditLogs.Where(a => a.CompanyId == cid && a.EntityType == nameof(ContactNotifyEndpoint)).ToListAsync();
        Assert.NotEmpty(entityAudit);
        Assert.All(entityAudit, a => { Assert.DoesNotContain(lineId, a.NewValues ?? ""); Assert.DoesNotContain(row.LineUserId!, a.NewValues ?? ""); });

        // ไม่ใช้ส่ง + อีเมลเดิมได้ทุกเรื่อง
        var set = await Resolver(db2).ResolveAsync(cid, contactId, NotifyTopic.Document);
        Assert.Equal(new[] { "ap@r245.co.th" }, set.Emails);
        Assert.Empty(set.LineUsers);
        Assert.Contains(set.Options, o => o.Kind == NotifyEndpointKind.LineUser && !o.Available);
        var only = await Resolver(db2).ResolveAsync(cid3, emailOnly, NotifyTopic.Dunning);
        Assert.Equal(new[] { "only@r245.co.th" }, only.Emails);
        var overview = await Resolver(db2).GetOverviewAsync(cid, contactId, true);
        Assert.Contains(overview!.Endpoints, e => e.Kind == NotifyEndpointKind.LineUser && e.StatusCode == "legacy" && e.CanUnlink);

        // ครั้งที่สอง (marker มีแล้ว) ⇒ ไม่ทำอะไร
        Assert.Equal(new LegacyLineMigrationResult(0, 0), await ContactNotifyEndpointMigration.MigrateLegacyLineUserIdsAsync(db2, Secrets));
        Assert.Equal(1, await db2.ContactNotifyEndpoints.CountAsync(e => e.CompanyId == cid));
    }

    // ───── (2) tenant ─────

    [Fact]
    public async Task Tenant_ผู้ติดต่อของบริษัทอื่น_resolverว่าง_serviceและcontroller404_ความยินยอมข้ามบริษัทไม่นับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cidA, contactA) = await SeedAsync(db);
        var (cidB, _) = await SeedAsync(db, email: "b@r245.co.th");
        var ownerB = await SeedUserAsync(db, cidB, UserRole.Owner);

        var set = await Resolver(db).ResolveAsync(cidB, contactA, NotifyTopic.Document);
        Assert.Empty(set.Options);
        Assert.Null(await Resolver(db).GetOverviewAsync(cidB, contactA, true));
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Service(db).AddEmailAsync(cidB, contactA, new AddContactNotifyEmailRequest("x@r245.co.th", null, null), Actor()));
        Assert.Equal(404, ex.StatusCode);
        Assert.False(await db.ContactNotifyEndpoints.AnyAsync(e => e.ContactId == contactA));
        Assert.Equal(404, Status(await Controller(db, ownerB).Get(cidB, contactA, CancellationToken.None)));

        // ความยินยอมของบริษัทอื่น ⇒ ไม่มีความยินยอม
        var foreignConsent = new PdpaConsentRecord { CompanyId = cidB, Purpose = "contact-line-notify", SubjectContactId = contactA };
        db.PdpaConsentRecords.Add(foreignConsent);
        db.ContactNotifyEndpoints.Add(new ContactNotifyEndpoint
        {
            CompanyId = cidA, ContactId = contactA, Kind = NotifyEndpointKind.LineUser, OaScope = NotifyEndpointKind.ScopeNextAcc,
            LineUserId = Secrets.Protect("U245cccccccccccccccccccccccccccccc3"), TopicsJson = NotifyTopics.Serialize(NotifyTopics.All),
            Source = "test", ConsentRecordId = foreignConsent.Id,
        });
        await db.SaveChangesAsync();
        var overview = await Resolver(db).GetOverviewAsync(cidA, contactA, true);
        Assert.Contains(overview!.Endpoints, e => e.Kind == NotifyEndpointKind.LineUser && e.StatusCode == "no-consent");
    }

    // ───── (3) CRUD + audit + สิทธิ์ ─────

    [Fact]
    public async Task Crud_เพิ่มอีเมล_ตั้งเรื่อง_อีเมลหลักปิดเรื่อง_ยกเลิก_ลงauditchain_และresolverตามทันที()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, contactId) = await SeedAsync(db);
        var svc = Service(db);

        var o1 = await svc.AddEmailAsync(cid, contactId, new AddContactNotifyEmailRequest(" Bill@R245.co.th ", "ฝ่ายบัญชี", new List<string> { "Dunning" }), Actor());
        var added = Assert.Single(o1.Endpoints, e => !e.IsPrimary);
        Assert.Equal("bill@r245.co.th", added.Email);
        Assert.Equal(new[] { "Dunning" }, added.Topics);

        // ซ้ำ ⇒ 409 · ซ้ำอีเมลหลัก ⇒ 409
        Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.AddEmailAsync(cid, contactId, new AddContactNotifyEmailRequest("bill@r245.co.th", null, null), Actor()))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.AddEmailAsync(cid, contactId, new AddContactNotifyEmailRequest("AP@r245.co.th", null, null), Actor()))).StatusCode);

        var r = Resolver(db);
        Assert.Equal(new[] { "ap@r245.co.th", "bill@r245.co.th" }, (await r.ResolveAsync(cid, contactId, NotifyTopic.Dunning)).Emails);
        Assert.Equal(new[] { "ap@r245.co.th" }, (await r.ResolveAsync(cid, contactId, NotifyTopic.Receipt)).Emails);

        // แก้เรื่อง ⇒ ใบเสร็จด้วย · ShowAmounts บนอีเมล ⇒ 400 (ไม่ใช่ไม่มีผลเงียบ)
        await svc.UpdateAsync(cid, contactId, added.Id!.Value, new UpdateContactNotifyEndpointRequest(null, new List<string> { "Dunning", "Receipt" }, null), Actor());
        Assert.Equal(new[] { "ap@r245.co.th", "bill@r245.co.th" }, (await r.ResolveAsync(cid, contactId, NotifyTopic.Receipt)).Emails);
        Assert.Equal(400, (await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.UpdateAsync(cid, contactId, added.Id!.Value, new UpdateContactNotifyEndpointRequest(null, null, false), Actor()))).StatusCode);

        // อีเมลหลักปิดทุกเรื่อง ⇒ เอกสารไปอีเมลที่สองเท่านั้น (แถวค่าตั้งไม่เก็บที่อยู่ซ้ำ)
        await svc.UpdatePrimaryAsync(cid, contactId, new UpdateContactNotifyPrimaryRequest(new List<string>()), Actor());
        Assert.Empty((await r.ResolveAsync(cid, contactId, NotifyTopic.Document)).Emails);
        Assert.Equal(new[] { "bill@r245.co.th" }, (await r.ResolveAsync(cid, contactId, NotifyTopic.Dunning)).Emails);
        var primaryRow = await db.ContactNotifyEndpoints.AsNoTracking().SingleAsync(e => e.ContactId == contactId && e.IsPrimary);
        Assert.Null(primaryRow.Email);

        // ยกเลิก ⇒ หายจากชุดผู้รับทันที · ยกเลิกซ้ำ 409 · อีเมลหลักยกเลิกไม่ได้ 400
        var o4 = await svc.UnlinkAsync(cid, contactId, added.Id!.Value, Actor());
        Assert.Contains(o4.Endpoints, e => e.Id == added.Id && e.StatusCode == "unlinked" && !e.CanUnlink);
        Assert.Empty((await r.ResolveAsync(cid, contactId, NotifyTopic.Dunning)).Emails);
        Assert.Equal(409, (await Assert.ThrowsAsync<BusinessRuleException>(() => svc.UnlinkAsync(cid, contactId, added.Id!.Value, Actor()))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<BusinessRuleException>(() => svc.UnlinkAsync(cid, contactId, primaryRow.Id, Actor()))).StatusCode);

        // audit chain: ทุกคำสั่งมีแถว (EntityId = ผู้ติดต่อ) ที่ประทับ hash แล้ว
        var audits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == cid && a.EntityType == ContactRecipientRule.AuditEntityType && a.EntityId == contactId.ToString())
            .ToListAsync();
        Assert.Equal(4, audits.Count);
        Assert.All(audits, a => Assert.NotNull(a.RowHash));
        Assert.Contains(audits, a => a.Action == AuditAction.Delete && a.NewValues!.Contains("unlink"));
    }

    [Fact]
    public async Task Permission_Viewerเขียนไม่ได้403ไม่มีแถว_Ownerเพิ่มได้201_อ่านได้ทั้งคู่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, contactId) = await SeedAsync(db);
        var viewer = await SeedUserAsync(db, cid, UserRole.Viewer);
        var owner = await SeedUserAsync(db, cid, UserRole.Owner);
        var req = new AddContactNotifyEmailRequest("perm@r245.co.th", null, null);

        Assert.Equal(403, Status(await Controller(db, viewer).AddEmail(cid, contactId, req, CancellationToken.None)));
        Assert.Equal(403, Status(await Controller(db, viewer).UpdatePrimary(cid, contactId, new UpdateContactNotifyPrimaryRequest(new List<string>()), CancellationToken.None)));
        Assert.False(await db.ContactNotifyEndpoints.AnyAsync(e => e.ContactId == contactId));
        var viewerGet = await Controller(db, viewer).Get(cid, contactId, CancellationToken.None);
        Assert.Equal(200, Status(viewerGet));
        // ฝ่ายค้าน N3: ปุ่มบนหน้าตามสิทธิ์จริง
        var viewerOverview = (ApiResponse<ContactNotifyOverviewResponse>)((ObjectResult)viewerGet.Result!).Value!;
        Assert.False(viewerOverview.Data!.CanEdit);
        Assert.NotNull(viewerOverview.Data.ReadOnlyReason);
        Assert.All(viewerOverview.Data.Endpoints, e => Assert.False(e.CanEditTopics || e.CanEditLabel || e.CanUnlink));
        var ownerOverview = (ApiResponse<ContactNotifyOverviewResponse>)((ObjectResult)(await Controller(db, owner).Get(cid, contactId, CancellationToken.None)).Result!).Value!;
        Assert.True(ownerOverview.Data!.CanEdit);
        Assert.True(ownerOverview.Data.Endpoints.Single(e => e.IsPrimary).CanEditTopics);

        Assert.Equal(201, Status(await Controller(db, owner).AddEmail(cid, contactId, req, CancellationToken.None)));
        Assert.True(await db.ContactNotifyEndpoints.AnyAsync(e => e.ContactId == contactId && e.Email == "perm@r245.co.th"));
        Assert.Equal(400, Status(await Controller(db, owner).Recipients(cid, contactId, "Payslip", CancellationToken.None)));
        Assert.Equal(200, Status(await Controller(db, owner).Recipients(cid, contactId, "Document", CancellationToken.None)));
    }

    [Fact]
    public async Task Q1b_กลุ่มกลางของระบบที่ตั้งที่หน้าแอดมิน_ไม่เป็นปลายทาง_กลุ่มอื่นแสดงเหตุLINEยังไม่เปิด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, contactId) = await SeedAsync(db);
        var consent = new PdpaConsentRecord { CompanyId = cid, Purpose = "contact-line-notify", SubjectContactId = contactId };
        db.PdpaConsentRecords.Add(consent);
        ContactNotifyEndpoint Group(string gid) => new()
        {
            CompanyId = cid, ContactId = contactId, Kind = NotifyEndpointKind.LineGroup, OaScope = NotifyEndpointKind.ScopeNextAcc, Verified = true,
            LineGroupId = Secrets.Protect(gid), LineIdHash = NotifyEndpointKind.LineIdHash(null, gid), MemberCountSnapshot = 5,
            TopicsJson = NotifyTopics.Serialize(NotifyTopics.All), Source = "test", ConsentRecordId = consent.Id, GroupNameSnapshot = "กลุ่ม " + gid[..6],
        };
        var central = Group(PlatformGroup);
        var own = Group("C245ffffffffffffffffffffffffffff01");
        var configCentral = Group(ConfigGroup);   // ฝ่ายค้าน S2: กลุ่มกลางจาก config ต้องถูกกันด้วย
        var unreadable = Group("C245dddddddddddddddddddddddddddd0d");
        unreadable.LineGroupId = "enc1:not-a-valid-ciphertext";   // ถอดรหัสไม่ออก ⇒ ใช้ไม่ได้
        db.ContactNotifyEndpoints.AddRange(central, own, configCentral, unreadable);
        await db.SaveChangesAsync();

        var overview = await Resolver(db).GetOverviewAsync(cid, contactId, true);
        Assert.Equal("platform-group", overview!.Endpoints.Single(e => e.Id == central.Id).StatusCode);
        Assert.Equal("line-not-open", overview.Endpoints.Single(e => e.Id == own.Id).StatusCode);
        Assert.Equal("platform-group", overview.Endpoints.Single(e => e.Id == configCentral.Id).StatusCode);
        Assert.Equal("group-unreadable", overview.Endpoints.Single(e => e.Id == unreadable.Id).StatusCode);
        Assert.True(overview.Endpoints.Single(e => e.Id == own.Id).ShowAmounts);
        var set = await Resolver(db).ResolveAsync(cid, contactId, NotifyTopic.Document);
        Assert.Empty(set.LineGroups);
        Assert.Contains(set.Options, o => o.EndpointId == central.Id && o.RuleCode == ContactRecipientRule.RuleCodePlatformGroup);
        Assert.Contains(set.Options, o => o.EndpointId == configCentral.Id && o.RuleCode == ContactRecipientRule.RuleCodePlatformGroup);
    }

    // ───── ฝ่ายค้าน L1 ─────

    [Fact]
    public async Task B1_ย้ายข้อมูลล้มกลางทาง_ไม่มีแถวค้างหลุดลงฐานกับSaveChangesถัดไป_และไม่มีmarker()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid1, c1) = await SeedAsync(db, lineUserId: "U245b1aaaaaaaaaaaaaaaaaaaaaaaaaaa1");
        var (cid2, c2) = await SeedAsync(db, lineUserId: "U245b1bbbbbbbbbbbbbbbbbbbbbbbbbbb2");
        foreach (var sql in ContactNotifyEndpointMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OneShotDataFixes" WHERE "Key" = {0}""", ContactNotifyEndpointMigration.MarkerKey);
        var calls = 0;
        var failing = F4Proxy<ISecretProtector>.Create((m, a) =>
        {
            if (m != nameof(ISecretProtector.Protect)) return null;
            if (++calls >= 2) throw new InvalidOperationException("จำลองคีย์เข้ารหัสล้มกลางทาง");
            return "enc1:fake-" + calls;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => ContactNotifyEndpointMigration.MigrateLegacyLineUserIdsAsync(db, failing));
        Assert.Empty(db.ChangeTracker.Entries<ContactNotifyEndpoint>());
        // context เดิม (แบบตอนบูต) ใช้ต่อ — SaveChanges ถัดไปต้องไม่พาแถวที่ค้างลงฐาน
        db.Companies.Add(new Company { Name = "บันทึกหลังล้ม 245", TaxId = "0105556000999" });
        await db.SaveChangesAsync();
        using var db2 = DbTestDatabase.TryCreateContext()!;
        Assert.False(await db2.ContactNotifyEndpoints.AnyAsync(e => e.CompanyId == cid1 || e.CompanyId == cid2));
        Assert.Equal(0, await MarkerCountAsync(db2));
        Assert.NotNull((await db2.Contacts.SingleAsync(c => c.Id == c1)).LineUserId);   // ข้อมูลเดิมยังอยู่ (ย้อนครบ)
        Assert.NotNull((await db2.Contacts.SingleAsync(c => c.Id == c2)).LineUserId);
        // ทิศตรงข้าม: รันใหม่ด้วยคีย์ที่ถูก ⇒ ย้ายได้ครบ
        await ContactNotifyEndpointMigration.MigrateLegacyLineUserIdsAsync(db2, Secrets);
        Assert.Equal(2, await db2.ContactNotifyEndpoints.CountAsync(e => e.ContactId == c1 || e.ContactId == c2));
    }

    [Fact]
    public async Task N1_ค่าที่Protectปฏิเสธ_ข้ามแถว_คงค่าไว้_นับลงaudit_งานยังปิดได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, contactId) = await SeedAsync(db, lineUserId: "enc1:looks-like-ciphertext");
        var (cidOk, okContact) = await SeedAsync(db, lineUserId: "U245n1ooooooooooooooooooooooooooo1");
        foreach (var sql in ContactNotifyEndpointMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OneShotDataFixes" WHERE "Key" = {0}""", ContactNotifyEndpointMigration.MarkerKey);

        var r = await ContactNotifyEndpointMigration.MigrateLegacyLineUserIdsAsync(db, Secrets);
        Assert.True(r.Skipped >= 1);
        using var db2 = DbTestDatabase.TryCreateContext()!;
        Assert.False(await db2.ContactNotifyEndpoints.AnyAsync(e => e.ContactId == contactId));
        Assert.Equal("enc1:looks-like-ciphertext", (await db2.Contacts.SingleAsync(c => c.Id == contactId)).LineUserId);
        var audit = await db2.AuditLogs.SingleAsync(a => a.CompanyId == cid && a.EntityType == ContactNotifyEndpointMigration.AuditEntityType);
        Assert.Contains("\"skippedUnprotectable\":1", audit.NewValues);
        Assert.True(await db2.ContactNotifyEndpoints.AnyAsync(e => e.ContactId == okContact));   // แถวอื่นยังย้าย
        Assert.Equal(1, await MarkerCountAsync(db2));
    }

    private static Task<int> MarkerCountAsync(AccountingDbContext db)
    {
        var key = ContactNotifyEndpointMigration.MarkerKey;   // ตัวแปร ⇒ พารามิเตอร์ (ไม่ใช่ const interpolation)
        return db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "OneShotDataFixes" WHERE "Key" = {key}""").SingleAsync();
    }

    [Fact]
    public async Task S1_ทิศตรงข้าม_ผู้ติดต่อที่ผูกCustomerLineLinkแล้ว_แจ้งบัตรยังได้LINE_ส่วนContactLineUserIdไม่ถูกใช้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, linked) = await SeedAsync(db);
        var legacyContact = new Contact { CompanyId = cid, Name = "ลูกค้ามีแค่ LINE เดิม", LineUserId = "U245s1legacyxxxxxxxxxxxxxxxxxxxx1", IsCustomer = true };
        db.Contacts.Add(legacyContact);
        var legacyOnly = legacyContact.Id;
        var tag = Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = cid, Name = "เว็บ 245", Slug = "s245-" + tag, Subdomain = "s245-" + tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        var member = new SiteCustomer { CompanyId = cid, SiteId = site.Id, Email = $"m-{tag}@shop.test", FullName = "สมาชิก", ContactId = linked, ConsentGiven = true, IsActive = true };
        db.SiteCustomers.Add(member);
        await db.SaveChangesAsync();
        const string lineId = "U245s1linkedyyyyyyyyyyyyyyyyyyyy2";
        db.CustomerLineLinks.Add(new CustomerLineLink
        {
            CompanyId = cid, SiteId = site.Id, TargetKind = CustomerLineLinkRule.KindMember, TargetId = member.Id,
            LineUserId = lineId, OaScope = CustomerNotifyChannel.ScopeNextAcc,
        });
        await db.SaveChangesAsync();

        var links = new Accounting.Services.Implementations.Cms.CustomerLineLinkService(db,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), Secrets,
            F4Proxy<IHttpClientFactory>.Create((m, a) => null), F4Proxy<IChatRateLimiter>.Create((m, a) => null),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Accounting.Services.Implementations.Cms.CustomerLineLinkService>.Instance, Central());
        // เป้าหมายเดียวกับที่ CourseCreditService.Membership ส่งให้ BookingNotifier
        Assert.Equal(lineId, await links.ResolveLineUserIdAsync(cid, CustomerLineTarget.ForContact(linked)));
        // ผู้ติดต่อที่มีแค่ Contact.LineUserId (ไม่รู้ OA) ⇒ ไม่มี LINE (อีเมลอย่างเดียว · หยุดส่งโดยตั้งใจ)
        Assert.Null(await links.ResolveLineUserIdAsync(cid, CustomerLineTarget.ForContact(legacyOnly)));
    }

    [Fact]
    public async Task S3_auditไม่มีอีเมลหรือป้ายเปล่า_ทั้งแถวของChangeTrackerและแถวที่เขียนเอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, contactId) = await SeedAsync(db);
        var o = await Service(db).AddEmailAsync(cid, contactId, new AddContactNotifyEmailRequest("secret.person@r245.co.th", "คุณสมหญิง ฝ่ายบัญชี", null), Actor());
        var id = o.Endpoints.Single(e => !e.IsPrimary).Id!.Value;
        await Service(db).UnlinkAsync(cid, contactId, id, Actor());
        var audits = await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == cid).ToListAsync();
        Assert.Contains(audits, a => a.EntityType == ContactRecipientRule.AuditEntityType);
        Assert.Contains(audits, a => a.EntityType == nameof(ContactNotifyEndpoint));
        Assert.All(audits, a =>
        {
            var text = (a.OldValues ?? "") + (a.NewValues ?? "");
            Assert.DoesNotContain("secret.person", text);
            Assert.DoesNotContain("สมหญิง", text);
        });
    }

    [Fact]
    public async Task S4_รวมผู้ติดต่อ_แถวหลักของผู้ถูกรวมและอีเมลซ้ำถูกยกเลิก_แถวหลักของผู้ที่เก็บไว้มีผล()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, keep) = await SeedAsync(db, email: "ap@r245.co.th");
        var loser = new Contact { CompanyId = cid, Name = "ลูกค้า 245 (ซ้ำ)", Email = "old@r245.co.th", IsCustomer = true };
        db.Contacts.Add(loser);
        await db.SaveChangesAsync();
        var svc = Service(db);
        await svc.UpdatePrimaryAsync(cid, keep, new UpdateContactNotifyPrimaryRequest(new List<string> { "Dunning" }), Actor());
        await svc.AddEmailAsync(cid, keep, new AddContactNotifyEmailRequest("bill@r245.co.th", null, null), Actor());
        await svc.UpdatePrimaryAsync(cid, loser.Id, new UpdateContactNotifyPrimaryRequest(new List<string> { "Document", "Receipt" }), Actor());
        await svc.AddEmailAsync(cid, loser.Id, new AddContactNotifyEmailRequest("BILL@r245.co.th", null, null), Actor());
        await svc.AddEmailAsync(cid, loser.Id, new AddContactNotifyEmailRequest("ap@r245.co.th", null, null), Actor());
        await svc.AddEmailAsync(cid, loser.Id, new AddContactNotifyEmailRequest("uniq@r245.co.th", null, null), Actor());
        var loserPrimaryId = (await db.ContactNotifyEndpoints.AsNoTracking().SingleAsync(e => e.ContactId == loser.Id && e.IsPrimary)).Id;
        db.ChangeTracker.Clear();

        var docs = new DocumentService(db, new AccountingService(db), null!, null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentService>.Instance, null!, null!, null!);
        await docs.MergeContactsAsync(cid, keep, new List<Guid> { loser.Id }, "merge-tester");

        using var db2 = DbTestDatabase.TryCreateContext()!;
        var rows = await db2.ContactNotifyEndpoints.AsNoTracking().Where(e => e.ContactId == keep).ToListAsync();
        Assert.Equal(1, rows.Count(e => e.IsPrimary && e.UnlinkedAt == null));
        var loserPrimary = rows.Single(e => e.Id == loserPrimaryId);
        Assert.Equal(NotifyEndpointKind.UnlinkMerge, loserPrimary.UnlinkReason);
        Assert.Equal(2, rows.Count(e => !e.IsPrimary && e.UnlinkReason == NotifyEndpointKind.UnlinkMerge));   // BILL ซ้ำ + ap ซ้ำอีเมลหลัก
        Assert.Contains(rows, e => e.Email == "uniq@r245.co.th" && e.UnlinkedAt == null);
        var r = new ContactRecipientResolver(db2, Secrets, Central());
        // แถวหลักที่มีผล = ของผู้ที่เก็บไว้ (ทวงชำระอย่างเดียว) ⇒ เอกสารไม่ไปอีเมลหลัก
        Assert.Equal(new[] { "bill@r245.co.th", "uniq@r245.co.th" }, (await r.ResolveAsync(cid, keep, NotifyTopic.Document)).Emails);
        Assert.Equal(new[] { "ap@r245.co.th", "bill@r245.co.th", "uniq@r245.co.th" }, (await r.ResolveAsync(cid, keep, NotifyTopic.Dunning)).Emails);
    }

    [Fact]
    public async Task N5_DSR_accessรวมปลายทาง_eraseปลดทุกแถวและล้างค่า()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, contactId) = await SeedAsync(db);
        await Service(db).AddEmailAsync(cid, contactId, new AddContactNotifyEmailRequest("dsr@r245.co.th", "ป้าย DSR", null), Actor());
        const string lineId = "U245n5zzzzzzzzzzzzzzzzzzzzzzzzzzz5";
        db.ContactNotifyEndpoints.Add(new ContactNotifyEndpoint
        {
            CompanyId = cid, ContactId = contactId, Kind = NotifyEndpointKind.LineUser, OaScope = NotifyEndpointKind.ScopeNextAcc,
            LineUserId = Secrets.Protect(lineId), LineIdHash = NotifyEndpointKind.LineIdHash(null, lineId),
            TopicsJson = NotifyTopics.Serialize(NotifyTopics.All), Source = "test",
        });
        await db.SaveChangesAsync();
        var pdpa = new Accounting.Services.Implementations.Pdpa.PdpaService(db);

        var access = await pdpa.GenerateAccessReportAsync(cid, null, contactId);
        var eps = (System.Collections.IEnumerable)access.Contacts.Single()["notifyEndpoints"]!;
        var list = eps.Cast<Dictionary<string, object?>>().ToList();
        Assert.Contains(list, e => (string?)e["email"] == "dsr@r245.co.th");
        Assert.Contains(list, e => (bool)e["hasLineId"]! && (string?)e["kind"] == NotifyEndpointKind.LineUser);
        Assert.DoesNotContain(list, e => e.Values.Any(v => v is string s && s.Contains(lineId)));

        await pdpa.ApplyErasureAsync(cid, null, contactId, "dsr-tester");
        using var db2 = DbTestDatabase.TryCreateContext()!;
        var rows = await db2.ContactNotifyEndpoints.IgnoreQueryFilters().AsNoTracking().Where(e => e.ContactId == contactId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, e =>
        {
            Assert.NotNull(e.UnlinkedAt);
            Assert.Equal(NotifyEndpointKind.UnlinkErase, e.UnlinkReason);
            Assert.Null(e.Email); Assert.Null(e.Label); Assert.Null(e.LineUserId); Assert.Null(e.LineIdHash);
        });
        Assert.Empty((await new ContactRecipientResolver(db2, Secrets, Central()).ResolveAsync(cid, contactId, NotifyTopic.Document)).Emails
            .Where(x => x == "dsr@r245.co.th"));
    }

    [Fact]
    public async Task N6_CREATE_TABLEมีคอลัมน์ครบตามโมเดลEF_รวมTenantEntityและBaseEntity()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var table = "r245_cols_" + Guid.NewGuid().ToString("N")[..10];
        // ทางผ่าน CI (รอบ 270 · run 43487318055 deadlock 40P01): FK ไป Companies/Contacts ทำให้ CREATE/DROP ตารางทดลองต้องล็อกตารางหลักที่เทสต์ขนานกำลัง INSERT ⇒
        // ตัด FK ออก (เทสต์นี้ตรวจรายชื่อคอลัมน์เท่านั้น — FK ตรวจด้วยฐานจริงที่สร้างจากโมเดล)
        var create = System.Text.RegularExpressions.Regex.Replace(
            ContactNotifyEndpointMigration.Statements()[0].Replace("\"ContactNotifyEndpoints\" (", $"\"{table}\" ("),
            @",\s*CONSTRAINT ""FK_[^""]+"" FOREIGN KEY \([^)]*\) REFERENCES ""[^""]+""\(""Id""\) ON DELETE CASCADE", "");
        Assert.DoesNotContain("REFERENCES", create);
        Assert.Contains(table, create);
        await db.Database.ExecuteSqlRawAsync(create);
        try
        {
            var cols = await db.Database.SqlQuery<string>(
                $"""SELECT column_name AS "Value" FROM information_schema.columns WHERE table_name = {table}""").ToListAsync();
            var model = db.Model.FindEntityType(typeof(ContactNotifyEndpoint))!.GetProperties().Select(p => p.GetColumnName()).ToList();
            Assert.Empty(model.Except(cols));
            Assert.Empty(cols.Except(model));
            foreach (var baseCol in new[] { "Id", "CompanyId", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy", "IsDeleted" })
                Assert.Contains(baseCol, cols);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync($"DROP TABLE IF EXISTS \"{table}\"");
        }
    }
}
