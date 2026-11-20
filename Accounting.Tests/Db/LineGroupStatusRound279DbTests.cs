using System.Text.Json;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Notification;
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
/// รอบ 279 (คำตัดสิน 279-1/279-2) บน PostgreSQL จริง — เจ้าของรายงาน: กลุ่ม "[TakeTime] Accounting" ผูกกับผู้ติดต่อแล้ว (บอทประกาศ "✅ กลุ่มนี้ผูกกับ …")
/// แต่พิมพ์ <c>#HeyNextAcc</c> อีกครั้งได้ "🔗 รหัสผูกกลุ่ม GB-…" ใหม่ · สาเหตุ: <c>IssueCodeAsync</c> ไม่เคยดูว่ากลุ่มผูกอยู่แล้ว ·
/// แก้: ผูกแล้ว ⇒ การ์ดสถานะ (ผูกกับใคร · เรื่องที่ส่ง · ความยินยอมบันทึกสลิปภาษี 3 สถานะ · ทางไปต่อ) ไม่ออกรหัส ·
/// ทิศตรงข้าม: ยังไม่ผูก ⇒ รหัสเหมือนเดิม · <c>#HeyNextAcc เพิ่ม</c> ⇒ รหัส · tenant isolation: การผูกของบริษัทอื่นไม่ถูกนับ/ไม่ถูกแสดง
/// <para>ตั้ง <c>SiteSettings.PlatformCompanyId</c> (แถวเดียวของระบบ) ⇒ collection เดียวกับเทสต์ที่แตะค่านี้ · คืนค่าเดิมเสมอ</para>
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class LineGroupStatusRound279DbTests
{
    private readonly ITestOutputHelper _out;
    public LineGroupStatusRound279DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("db-test-key-279-status");
    private const string AdminGroup = "Cadmin279s0000000000000000000001";
    private const string CentralBot = "Ucentral279s000000000000000000001";
    private const string CompanyBot = "Ucompany279s000000000000000000001";

    private static INextAccLineConfig Central() => F4Proxy<INextAccLineConfig>.Create((m, _) => m == nameof(INextAccLineConfig.PlatformGroupIdsAsync)
        ? NextAccLineConfigRule.PlatformGroupIds(AdminGroup, null)
        : new NextAccLineSettings("CENTRAL-TOKEN-279", "S", "@279nextacc", CentralBot, AdminGroup, NextAccLineConfigRule.SourceAdmin));

    private static ILineNotifyService FakeLine(List<string> log)
        => F4Proxy<ILineNotifyService>.Create((m, a) => m switch
        {
            nameof(ILineNotifyService.TryGetNextAccGroupSummaryAsync) => Note(log, "summary:central", new LineGroupSummary("[TakeTime] Accounting", 6)),
            nameof(ILineNotifyService.TryGetCompanyGroupSummaryAsync) => Note(log, "summary:company", new LineGroupSummary("กลุ่มบริษัท", 6)),
            nameof(ILineNotifyService.TryPushContactTextAsync) => Note(log, "text:" + a[1] + ":" + a[3], LinePushResult.Success),
            _ => null,
        });

    private static object Note(List<string> log, string s, object result) { log.Add(s); return result; }

    private sealed class NoPerms : IPermissionService
    {
        public Task<bool> HasPermissionAsync(Guid companyId, Guid userId, string permissionKey) => Task.FromResult(false);
        public Task<List<string>> GetUserPermissionsAsync(Guid companyId, Guid userId) => Task.FromResult(new List<string>());
    }

    private static ContactLineGroupService Groups(AccountingDbContext db, List<string> log, bool withConsent = true)
        => new(db, Secrets, FakeLine(log), new ContactRecipientResolver(db, Secrets, Central()), Central(),
            F4Proxy<IChatRateLimiter>.Create((m, _) => m == nameof(IChatRateLimiter.TryConsumeAsync) ? (object)true : 0),
            NullLogger<ContactLineGroupService>.Instance,
            withConsent ? new ProviderConsentService(db, new NoPerms(), NullLogger<ProviderConsentService>.Instance) : null);

    private static JsonElement Ev(string groupId, string text)
        => JsonDocument.Parse(JsonSerializer.Serialize(new { type = "message", source = new { type = "group", groupId }, message = new { type = "text", text } })).RootElement.Clone();

    private static string CodeFrom(string? reply) => Regex.Match(reply ?? "", @"GB-[A-Z0-9]{8}").Value;

    private sealed record Seed(Guid Provider, Guid Client, Guid Owner, Guid TenantContact, Guid PlainContact, Guid Other, Guid OtherContact);

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var provider = new Company { Name = "บริษัท เน็ก แอค 279 " + tag, TaxId = "" };
        var client = new Company { Name = "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส 279 " + tag, TaxId = "" };
        var other = new Company { Name = "บริษัทอื่น 279 " + tag, TaxId = "" };
        db.Companies.AddRange(provider, client, other);
        var owner = new User { Email = $"r279-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของทดสอบ" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { UserId = owner.Id, CompanyId = client.Id, Role = UserRole.Owner });
        var tenant = new Contact { CompanyId = provider.Id, Name = "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส", IsCustomer = true, TaxId = "0103555012345",
            ExternalSystem = PlatformInvoiceRules.TenantContactSystem, ExternalId = client.Id.ToString() };
        var plain = new Contact { CompanyId = provider.Id, Name = "ลูกค้าทั่วไป 279", IsCustomer = true, TaxId = "" };
        var otherContact = new Contact { CompanyId = other.Id, Name = "ลับเฉพาะบริษัทอื่น 279", IsCustomer = true, TaxId = "" };
        db.Contacts.AddRange(tenant, plain, otherContact);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Seed(provider.Id, client.Id, owner.Id, tenant.Id, plain.Id, other.Id, otherContact.Id);
    }

    /// <summary>ปลายทางกลุ่มที่ผูกแล้ว (รูปเดียวกับที่ <c>ConfirmAsync</c> เขียน) — ใช้ตรงเพื่อจำลองการผูกที่ค้างของบริษัทอื่น/OA บริษัท</summary>
    private static async Task BindDirectAsync(AccountingDbContext db, Guid companyId, Guid contactId, string dest, string group, string scope, params NotifyTopic[] topics)
    {
        db.ContactNotifyEndpoints.Add(new ContactNotifyEndpoint
        {
            CompanyId = companyId, ContactId = contactId, Kind = NotifyEndpointKind.LineGroup, LineGroupId = Secrets.Protect(group),
            LineIdHash = NotifyEndpointKind.LineIdHash(dest, group), OaScope = scope, OaDestination = dest, GroupNameSnapshot = "[TakeTime] Accounting",
            MemberCountSnapshot = 6, TopicsJson = NotifyTopics.Serialize(topics), Verified = true, Source = LineGroupBindRule.SourceGroupCode, LinkedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static async Task WithPlatformAsync(AccountingDbContext db, Guid? platformId, Func<Task> body)
    {
        var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        var created = s == null;
        if (s == null) { s = new SiteSettings(); db.SiteSettings.Add(s); }
        var previous = s.PlatformCompanyId;
        s.PlatformCompanyId = platformId;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        try { await body(); }
        finally
        {
            db.ChangeTracker.Clear();
            var again = await db.SiteSettings.FirstAsync(x => x.Id == s.Id);
            if (created) db.SiteSettings.Remove(again);
            else again.PlatformCompanyId = previous;
            await db.SaveChangesAsync();
        }
    }

    private static async Task<int> CodeRowsAsync(AccountingDbContext db, string group, string dest)
    {
        var h = NotifyEndpointKind.LineIdHash(dest, group);
        return await db.LineGroupBindCodes.AsNoTracking().CountAsync(r => r.GroupIdHash == h);
    }

    [Fact]
    public async Task กลุ่มผูกแล้ว_HeyNextAcc_ได้การ์ดสถานะ_ไม่ออกรหัส_ยังไม่ผูกได้รหัส_คำสั่งเพิ่มได้รหัส()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var group = "C279s" + Guid.NewGuid().ToString("N")[..27];
        var log = new List<string>();
        await WithPlatformAsync(db, s.Provider, async () =>
        {
            // ทิศตรงข้าม: ยังไม่ผูก ⇒ รหัสเหมือนเดิม
            var first = await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#HeyNextAcc"));
            var code = CodeFrom(first.Reply);
            Assert.False(string.IsNullOrEmpty(code), first.Reply);
            await Groups(db, log, withConsent: false).ConfirmAsync(s.Provider, s.PlainContact, new ConfirmContactLineGroupRequest(code, null, null, true),
                new ContactNotifyActor(null, "owner@p279.local", "127.0.0.1"));
            db.ChangeTracker.Clear();
            var codesBefore = await CodeRowsAsync(db, group, CentralBot);

            // บั๊กที่รายงาน: ผูกแล้ว + #HeyNextAcc ⇒ เดิมได้รหัสใหม่ · ตอนนี้ได้การ์ดสถานะ
            var status = await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#HeyNextAcc"));
            Assert.False(status.LeaveAfterReply);
            Assert.DoesNotContain("GB-", status.Reply);
            Assert.DoesNotContain("รหัสผูกกลุ่ม", status.Reply);
            Assert.Contains("สถานะกลุ่ม", status.Reply);
            Assert.Contains("[TakeTime] Accounting", status.Reply);
            Assert.Contains("บริษัท เน็ก แอค 279", status.Reply);
            Assert.Contains("ลูกค้าทั่วไป 279 — เรื่องที่ส่ง: ", status.Reply);
            Assert.DoesNotContain("บันทึกการชำระภาษีจากสลิป", status.Reply);   // ผู้ติดต่อทั่วไป ⇒ ไม่ใช้ความยินยอม
            Assert.Contains(LineGroupBindRule.AddCommandText, status.Reply);
            Assert.Equal(codesBefore, await CodeRowsAsync(db, group, CentralBot));   // ไม่มีแถวรหัสใหม่

            // #HeyNextAcc เพิ่ม ⇒ รหัส (ผูกผู้ติดต่ออีกราย)
            var add = await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#HeyNextAcc เพิ่ม"));
            Assert.False(string.IsNullOrEmpty(CodeFrom(add.Reply)), add.Reply);
            Assert.Equal(codesBefore + 1, await CodeRowsAsync(db, group, CentralBot));

            // ยกเลิกแล้ว ⇒ กลับเป็นยังไม่ผูก ⇒ #HeyNextAcc ได้รหัสอีกครั้ง
            await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#ByeNextAcc"));
            var again = await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#HeyNextAcc"));
            Assert.False(string.IsNullOrEmpty(CodeFrom(again.Reply)), again.Reply);
        });
    }

    [Fact]
    public async Task การ์ดสถานะ_ความยินยอม_ยังไม่ได้ขอ_รอยืนยัน_ยินยอมแล้ว_และหน้าผู้ติดต่อตรงกัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var group = "C279c" + Guid.NewGuid().ToString("N")[..27];
        await BindDirectAsync(db, s.Provider, s.TenantContact, CentralBot, group, NotifyEndpointKind.ScopeNextAcc,
            NotifyTopic.Document, NotifyTopic.Dunning, NotifyTopic.Receipt, NotifyTopic.Wht);
        var log = new List<string>();
        await WithPlatformAsync(db, s.Provider, async () =>
        {
            // 1) ยังไม่ได้ขอ
            var r1 = (await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#HeyNextAcc"))).Reply!;
            Assert.Contains("บันทึกการชำระภาษีจากสลิป: " + ProviderConsent.StateLabel(ProviderConsent.GroupConsentState.NotRequested), r1);
            Assert.Contains(ProviderConsent.StaffRequestMenuPath, r1);
            Assert.Contains("ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส — เรื่องที่ส่ง: เอกสาร", r1);
            Assert.DoesNotContain("0103555012345", r1);   // เลขภาษีไม่หลุดเข้ากลุ่ม
            Assert.DoesNotContain("GB-", r1);
            var v1 = await Groups(db, log).ProviderConsentViewAsync(s.Provider, s.TenantContact);
            Assert.Equal(nameof(ProviderConsent.GroupConsentState.NotRequested), v1!.State);
            Assert.True(v1.CanRequest);
            Assert.True(v1.HasCentralGroup);
            Assert.Equal($"/api/companies/{s.Provider:D}/tax-slip-desk/clients/{s.TenantContact:D}/request-consent", v1.RequestApiPath);

            // 2) พนักงานส่งคำขอ ⇒ รอยืนยัน
            var consent = new ProviderConsentService(db, new NoPerms(), NullLogger<ProviderConsentService>.Instance);
            var (req, isNew) = await consent.EnsureRequestAsync(s.Client, s.Provider, ProviderConsentService.ViaStaff, null);
            Assert.True(isNew && req != null);
            db.ChangeTracker.Clear();
            var r2 = (await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#HeyNextAcc"))).Reply!;
            Assert.Contains(ProviderConsent.StateLabel(ProviderConsent.GroupConsentState.Pending), r2);
            Assert.Equal(nameof(ProviderConsent.GroupConsentState.Pending), (await Groups(db, log).ProviderConsentViewAsync(s.Provider, s.TenantContact))!.State);

            // 3) เจ้าของบริษัทลูกค้ายินยอมบนเว็บ ⇒ ยินยอมแล้ว · ไม่ต้องบอกทางยืนยัน/ทางส่งคำขอ
            var at = PartnerConsent.TruncateToSecond(DateTime.UtcNow);
            const string types = "VatPp30,VatPp36,WhtPnd53";
            db.ProviderAutomationConsents.Add(new ProviderAutomationConsent
            {
                CompanyId = s.Client, ProviderCompanyId = s.Provider, Scope = ProviderConsent.Scope, TaxTypes = types, TextVersion = 1, HashVersion = ProviderConsent.HashVersion,
                EvidenceSource = ProviderConsent.SourceWeb, GrantedByUserId = s.Owner, GrantedAt = at,
                EvidenceHash = ProviderConsent.Hash(s.Client, s.Provider, types, null, null, false, 1, ProviderConsent.TextFor(1)!, s.Owner, at),
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var r3 = (await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#HeyNextAcc"))).Reply!;
            Assert.Contains(ProviderConsent.StateLabel(ProviderConsent.GroupConsentState.Granted), r3);
            Assert.DoesNotContain(ProviderConsent.StaffRequestMenuPath, r3);
            Assert.DoesNotContain("เจ้าของบริษัทยืนยันบนเว็บ", r3);
            var v3 = await Groups(db, log).ProviderConsentViewAsync(s.Provider, s.TenantContact);
            Assert.Equal(nameof(ProviderConsent.GroupConsentState.Granted), v3!.State);
            Assert.False(v3.CanRequest);
            Assert.Null(v3.ClientHowTo);

            // ผู้ติดต่อทั่วไป / บริษัทที่ไม่ใช่ผู้ให้บริการ ⇒ ไม่มีกล่องความยินยอม
            Assert.Null(await Groups(db, log).ProviderConsentViewAsync(s.Provider, s.PlainContact));
            Assert.Null(await Groups(db, log).ProviderConsentViewAsync(s.Other, s.OtherContact));
        });
    }

    /// <summary>รอบ 280 (ฝ่ายค้าน LOW-2 ของการ์ด 279): กลุ่มเดียวผูกผู้ติดต่อหลายราย ⇒ การ์ดไม่แสดงชื่อ/ความยินยอมรายผู้ติดต่อ (ลูกค้ารายหนึ่งไม่เห็นของอีกราย) ·
    /// ลิงก์ในกลุ่มไม่มี id บริษัทลูกค้า · ผูกรายเดียว ⇒ แสดงสถานะตามเดิม (ทิศตรงข้าม)</summary>
    [Fact]
    public async Task R280_LOW2_กลุ่มผูกหลายผู้ติดต่อ_การ์ดไม่เปิดเผยความยินยอมรายผู้ติดต่อ_ลิงก์ไม่มีGUID()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var group = "C280m" + Guid.NewGuid().ToString("N")[..27];
        await BindDirectAsync(db, s.Provider, s.TenantContact, CentralBot, group, NotifyEndpointKind.ScopeNextAcc, NotifyTopic.Document, NotifyTopic.Wht);
        var log = new List<string>();
        await WithPlatformAsync(db, s.Provider, async () =>
        {
            // ผูกรายเดียว ⇒ สถานะความยินยอมตามเดิม · ลิงก์/ทางยืนยันไม่มี GUID ของบริษัทลูกค้า
            var single = (await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#HeyNextAcc"))).Reply!;
            Assert.Contains("บันทึกการชำระภาษีจากสลิป", single);
            Assert.DoesNotContain(s.Client.ToString("D"), single);

            await BindDirectAsync(db, s.Provider, s.PlainContact, CentralBot, group, NotifyEndpointKind.ScopeNextAcc, NotifyTopic.Dunning);
            var multi = (await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#HeyNextAcc"))).Reply!;
            Assert.Contains("ผู้ติดต่อ 2 ราย", multi);
            Assert.Contains(LineGroupBindRule.Replies.MultiContactWebHint, multi);
            Assert.DoesNotContain("บันทึกการชำระภาษีจากสลิป", multi);
            Assert.DoesNotContain("ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส", multi);          // ไม่มีชื่อลูกค้ารายผู้ติดต่อ
            Assert.DoesNotContain(s.Client.ToString("D"), multi);
            Assert.Contains("เอกสาร", multi);
        });
    }

    [Fact]
    public async Task tenant_isolation_การผูกของบริษัทอื่นไม่นับ_ไม่แสดง_และ_OA_บริษัทได้การ์ดสถานะของตัวเอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var group = "C279i" + Guid.NewGuid().ToString("N")[..27];
        // ปลายทาง nextacc ค้างของบริษัทอื่น (เช่น เคยเป็นบริษัทผู้ให้บริการ) บนกลุ่มเดียวกัน
        await BindDirectAsync(db, s.Other, s.OtherContact, CentralBot, group, NotifyEndpointKind.ScopeNextAcc, NotifyTopic.Document);
        var log = new List<string>();
        await WithPlatformAsync(db, s.Provider, async () =>
        {
            var hey = await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#HeyNextAcc"));
            Assert.False(string.IsNullOrEmpty(CodeFrom(hey.Reply)), hey.Reply);   // บริษัทผู้ให้บริการยังไม่ผูก ⇒ รหัส
            Assert.DoesNotContain("ลับเฉพาะบริษัทอื่น", hey.Reply);

            await BindDirectAsync(db, s.Provider, s.PlainContact, CentralBot, group, NotifyEndpointKind.ScopeNextAcc, NotifyTopic.Document);
            var status = await Groups(db, log).HandleCentralGroupEventAsync(CentralBot, Ev(group, "#HeyNextAcc"));
            Assert.Contains("1 ผู้ติดต่อ", status.Reply);
            Assert.Contains("ลูกค้าทั่วไป 279", status.Reply);
            Assert.DoesNotContain("ลับเฉพาะบริษัทอื่น", status.Reply);
        });

        // OA ของบริษัทเอง (scope company · แถวเก่า OaScope = "") — ผูกแล้ว ⇒ การ์ดสถานะของบริษัทนั้น · ไม่มีบรรทัดความยินยอม (ใช้เฉพาะ OA กลาง)
        var companyGroup = "C279o" + Guid.NewGuid().ToString("N")[..27];
        await BindDirectAsync(db, s.Other, s.OtherContact, CompanyBot, companyGroup, "", NotifyTopic.Document, NotifyTopic.Receipt);
        var mine = await Groups(db, log).HandleCompanyGroupEventAsync(s.Other, CompanyBot, Ev(companyGroup, "#HeyNextAcc"));
        Assert.Contains("ลับเฉพาะบริษัทอื่น 279 — เรื่องที่ส่ง: เอกสาร, ใบเสร็จ", mine.Reply);
        Assert.DoesNotContain("บันทึกการชำระภาษีจากสลิป", mine.Reply);
        Assert.DoesNotContain("GB-", mine.Reply);
        // บริษัทผู้ให้บริการ (คนละบริษัท) ส่ง webhook ของ OA ตัวเองจากกลุ่มเดียวกัน ⇒ ไม่เห็นการผูกของบริษัทอื่น ⇒ ออกรหัสตามปกติ
        var theirs = await Groups(db, log).HandleCompanyGroupEventAsync(s.Provider, CompanyBot, Ev(companyGroup, "#HeyNextAcc"));
        Assert.False(string.IsNullOrEmpty(CodeFrom(theirs.Reply)), theirs.Reply);
        Assert.DoesNotContain("ลับเฉพาะบริษัทอื่น", theirs.Reply);
    }
}
