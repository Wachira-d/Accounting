using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Notification;
using Accounting.Models.Entities;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 258 (A-H3 · E-H1 · A-H2) บน PostgreSQL จริง (trait Db):
/// <para>(1) ผู้เรียกที่ไม่ส่ง <c>enableLineGroup</c> (หน้าเก่า/ตารางสำเนา) บันทึกแล้ว ค่า "กลุ่ม LINE ของบริษัท" เดิม<b>คงอยู่</b> — เดิมถูกเขียน false ทับ</para>
/// <para>(2) เหตุการณ์ที่ยังไม่เคยตั้ง (ใช้ค่าตั้งต้นของระบบ = เปิดกลุ่มเมื่อผูกกลุ่มไว้) ถูกบันทึกโดยผู้เรียกแบบเดียวกัน ⇒ ค่าตั้งต้นถูกเก็บ ไม่หาย</para>
/// <para>(3) ทิศตรงข้าม: ผู้เรียกที่ส่ง false = ปิดจริง · ส่ง true = เปิดจริง</para>
/// <para>(4) ถอดการผูก LINE ของตัวเอง = ล้างค่า + มีแถว audit (ไม่มี LINE id เต็ม) · ยังไม่ผูก = ไม่จด</para>
/// </summary>
[Trait("Category", "Db")]
public class NotificationConfigRound258DbTests
{
    private readonly ITestOutputHelper _out;
    public NotificationConfigRound258DbTests(ITestOutputHelper output) => _out = output;

    private static async Task<Guid> SeedCompanyAsync(AccountingDbContext db, bool groupConfigured)
    {
        var company = new Company { Name = "บริษัททดสอบตารางแจ้งเตือน 258", TaxId = "0105556000258" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyId = company.Id, LineEnabled = groupConfigured, LineChannelAccessToken = groupConfigured ? "enc-token" : null,
            LineDefaultGroupId = groupConfigured ? "C258000000000000000000000000000ab" : null,
        });
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static BulkUpdateNotificationSettingsRequest Req(string eventKey, string role, bool? group, bool system = true)
        => new(new List<NotificationSettingUpdateDto> { new(eventKey, role, system, false, false, group) });

    [Fact]
    public async Task ผู้เรียกไม่ส่งช่องกลุ่ม_ค่าเดิมคงอยู่_และส่งfalseปิดจริง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedCompanyAsync(db, groupConfigured: true);
        var other = await SeedCompanyAsync(db, groupConfigured: true);
        db.NotificationSettings.Add(new NotificationSetting
        {
            CompanyId = cid, EventKey = NotificationEvents.DocumentApproved, RecipientRole = NotificationRecipientRoles.Owner,
            EnableSystem = true, EnableLineGroup = true,
        });
        db.NotificationSettings.Add(new NotificationSetting
        {
            CompanyId = other, EventKey = NotificationEvents.DocumentApproved, RecipientRole = NotificationRecipientRoles.Owner,
            EnableSystem = true, EnableLineGroup = true,
        });
        await db.SaveChangesAsync();
        var svc = new NotificationConfigService(db);

        // หน้าเก่า: ปิดกระดิ่ง ไม่ส่งช่องกลุ่ม ⇒ กระดิ่งปิด แต่กลุ่มยังเปิด
        await svc.BulkUpsertCompanySettingsAsync(cid, Req(NotificationEvents.DocumentApproved, NotificationRecipientRoles.Owner, group: null, system: false), "tester");
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var row = await check.NotificationSettings.SingleAsync(s => s.CompanyId == cid && s.EventKey == NotificationEvents.DocumentApproved
                && s.RecipientRole == NotificationRecipientRoles.Owner);
            Assert.False(row.EnableSystem);
            Assert.True(row.EnableLineGroup);
        }

        // แถวบทบาทใหม่ของเหตุการณ์เดียวกันที่ไม่ส่งช่องกลุ่ม ⇒ ตามค่าของเหตุการณ์ (เปิด)
        await svc.BulkUpsertCompanySettingsAsync(cid, Req(NotificationEvents.DocumentApproved, NotificationRecipientRoles.Accounting, group: null), "tester");

        // ทิศตรงข้าม: ส่ง false = ปิดจริง
        await svc.BulkUpsertCompanySettingsAsync(cid, Req(NotificationEvents.DocumentApproved, NotificationRecipientRoles.Owner, group: false), "tester");
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var rows = await check.NotificationSettings.Where(s => s.CompanyId == cid && s.EventKey == NotificationEvents.DocumentApproved).ToListAsync();
            Assert.False(rows.Single(r => r.RecipientRole == NotificationRecipientRoles.Owner).EnableLineGroup);
            Assert.True(rows.Single(r => r.RecipientRole == NotificationRecipientRoles.Accounting).EnableLineGroup);
            // บริษัทอื่นไม่ถูกแตะ (tenant)
            Assert.True((await check.NotificationSettings.SingleAsync(s => s.CompanyId == other)).EnableLineGroup);
        }
    }

    [Fact]
    public async Task เหตุการณ์ที่ยังไม่เคยตั้ง_ค่าตั้งต้นของระบบถูกเก็บ_ไม่หายเมื่อผู้เรียกไม่ส่งช่องกลุ่ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var withGroup = await SeedCompanyAsync(db, groupConfigured: true);
        var noGroup = await SeedCompanyAsync(db, groupConfigured: false);
        var svc = new NotificationConfigService(db);

        await svc.BulkUpsertCompanySettingsAsync(withGroup, Req(NotificationEvents.DocumentApproved, NotificationRecipientRoles.Owner, group: null), "tester");
        await svc.BulkUpsertCompanySettingsAsync(noGroup, Req(NotificationEvents.DocumentApproved, NotificationRecipientRoles.Owner, group: null), "tester");
        // ทิศตรงข้าม: ส่ง true กับบริษัทที่ค่าตั้งต้นปิด = เปิดจริง
        await svc.BulkUpsertCompanySettingsAsync(noGroup, Req(NotificationEvents.PaymentReceived, NotificationRecipientRoles.Owner, group: true), "tester");

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.True((await check.NotificationSettings.SingleAsync(s => s.CompanyId == withGroup && s.EventKey == NotificationEvents.DocumentApproved)).EnableLineGroup);
        Assert.False((await check.NotificationSettings.SingleAsync(s => s.CompanyId == noGroup && s.EventKey == NotificationEvents.DocumentApproved)).EnableLineGroup);
        Assert.True((await check.NotificationSettings.SingleAsync(s => s.CompanyId == noGroup && s.EventKey == NotificationEvents.PaymentReceived)).EnableLineGroup);
        // ที่หน้าเว็บอ่านกลับ: ไม่มีแถว IsDefault ของคีย์ที่บันทึกแล้ว และค่ากลุ่มตรงกับที่เก็บ
        var read = await new NotificationConfigService(check).GetCompanySettingsAsync(withGroup);
        var approved = Assert.Single(read, r => r.EventKey == NotificationEvents.DocumentApproved);
        Assert.False(approved.IsDefault);
        Assert.True(approved.EnableLineGroup);
    }

    [Fact]
    public async Task ถอดการผูกLINEของตัวเอง_ล้างค่า_มีauditไม่มีidเต็ม_และยังไม่ผูกไม่จด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedCompanyAsync(db, groupConfigured: false);
        var lineId = "U258" + Guid.NewGuid().ToString("N")[..28];
        var bound = new User { Email = $"r258-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ทดสอบ 258", LineUserId = lineId };
        var unbound = new User { Email = $"r258-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ทดสอบ 258 ข" };
        db.Users.AddRange(bound, unbound);
        await db.SaveChangesAsync();
        var svc = new NotificationConfigService(db);

        var before = await svc.GetLineBindingAsync(bound.Id);
        Assert.True(before.IsBound);
        Assert.Equal(lineId[^6..], before.LineUserIdTail);

        var after = await svc.ClearLineBindingAsync(bound.Id, cid, "127.0.0.1");
        Assert.False(after.IsBound);
        await svc.ClearLineBindingAsync(unbound.Id, cid, "127.0.0.1");

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Null((await check.Users.SingleAsync(u => u.Id == bound.Id)).LineUserId);
        var audits = await check.AuditLogs.Where(a => a.CompanyId == cid && a.EntityType == "LineBindCode").ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Equal(bound.Id, audit.UserId);
        Assert.Contains(lineId[^6..], audit.NewValues!, StringComparison.Ordinal);
        Assert.DoesNotContain(lineId, audit.NewValues!, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(audit.RowHash));
    }

    // ───── รอบ 258 ฝ่ายค้าน R-L3: ค่ากลุ่ม LINE ตัดสินต่อเหตุการณ์ (ตรงกับเครื่องแจ้งเตือน) ─────

    [Fact]
    public async Task ส่งfalseแถวเดียว_ทุกแถวที่เก็บไว้ของเหตุการณ์ถูกปิด_เหตุการณ์อื่นไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedCompanyAsync(db, groupConfigured: true);
        NotificationSetting Row(string key, string role) => new()
        {
            CompanyId = cid, EventKey = key, RecipientRole = role, EnableSystem = true, EnableLineGroup = true,
        };
        db.NotificationSettings.AddRange(
            Row(NotificationEvents.DocumentApproved, NotificationRecipientRoles.Owner),
            Row(NotificationEvents.DocumentApproved, NotificationRecipientRoles.Accounting),   // แถวบทบาทที่คำขอไม่ได้ส่ง
            Row(NotificationEvents.PaymentReceived, NotificationRecipientRoles.Owner));
        await db.SaveChangesAsync();

        await new NotificationConfigService(db).BulkUpsertCompanySettingsAsync(cid,
            Req(NotificationEvents.DocumentApproved, NotificationRecipientRoles.Owner, group: false), "tester");

        using var check = DbTestDatabase.TryCreateContext()!;
        var approved = await check.NotificationSettings.Where(s => s.CompanyId == cid && s.EventKey == NotificationEvents.DocumentApproved).ToListAsync();
        Assert.Equal(2, approved.Count);
        Assert.All(approved, r => Assert.False(r.EnableLineGroup));   // เดิมแถว Accounting ยังเปิด ⇒ กลุ่มยังได้ประกาศทั้งที่หน้าแสดง "ปิด"
        // ทิศตรงข้าม: เหตุการณ์อื่นไม่ถูกแตะ
        Assert.True((await check.NotificationSettings.SingleAsync(s => s.CompanyId == cid && s.EventKey == NotificationEvents.PaymentReceived)).EnableLineGroup);
    }

    // ───── รอบ 258 ฝ่ายค้าน R-M1: รอบรายการประจำล้ม/ค้างร่าง ต้องถึงคนแม้ผู้สร้างออกไปแล้ว ─────

    private static NotificationEngine Engine(AccountingDbContext db)
    {
        var line = F4Proxy<Accounting.Services.Interfaces.ILineNotifyService>.Create((_, _) => null);
        var org = F4Proxy<Accounting.Services.Interfaces.IOrganizationService>.Create((_, _) => null);
        return new NotificationEngine(db, line, org, Microsoft.Extensions.Logging.Abstractions.NullLogger<NotificationEngine>.Instance,
            new SecretProtector("db-test-key-258-notify"));
    }

    private static async Task<Guid> SeedOwnerAsync(AccountingDbContext db, Guid companyId)
    {
        var u = new User { Email = $"r258-owner-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของ 258" };
        db.Users.Add(u);
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = companyId, Role = Accounting.Models.Enums.UserRole.Owner });
        await db.SaveChangesAsync();
        return u.Id;
    }

    [Fact]
    public async Task รอบรายการประจำล้ม_ผู้สร้างออกแล้ว_บริษัทยังไม่ตั้งคีย์_เจ้าของได้กระดิ่ง_และบริษัทที่ปิดคีย์ไว้ไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedCompanyAsync(db, groupConfigured: false);
        var owner = await SeedOwnerAsync(db, cid);
        var muted = await SeedCompanyAsync(db, groupConfigured: false);
        var mutedOwner = await SeedOwnerAsync(db, muted);
        // บริษัทที่ตั้งคีย์นี้ไว้แล้วแบบปิดทุกช่อง = ตั้งใจไม่รับ
        db.NotificationSettings.Add(new NotificationSetting
        {
            CompanyId = muted, EventKey = NotificationEvents.RecurringRunFailed, RecipientRole = NotificationRecipientRoles.Owner,
        });
        await db.SaveChangesAsync();
        var title = "รายการประจำ \"ค่าเช่า\" สร้างเอกสารแล้วแต่ค้างเป็นร่าง " + Guid.NewGuid().ToString("N")[..8];
        // ผู้รับระบุตัว = null (ผู้สร้างไม่ใช่สมาชิกแล้ว — RecurringStaffNotice.Recipient)
        var ctx = new Accounting.Services.Interfaces.NotificationContext { Title = title, Message = "อนุมัติอัตโนมัติไม่ผ่าน", RecipientUserId = null };

        await Engine(db).DispatchAsync(cid, NotificationEvents.RecurringRunFailed, ctx);
        await Engine(db).DispatchAsync(muted, NotificationEvents.RecurringRunFailed, ctx);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.True(await check.Notifications.AnyAsync(n => n.CompanyId == cid && n.UserId == owner && n.Title == title));
        // ทิศตรงข้าม: บริษัทที่ปิดคีย์ไว้เองไม่ถูกแจ้ง
        Assert.False(await check.Notifications.AnyAsync(n => n.CompanyId == muted && n.UserId == mutedOwner && n.Title == title));
    }
}
