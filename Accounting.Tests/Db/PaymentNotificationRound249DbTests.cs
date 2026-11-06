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
/// รอบ 249 บนฐาน PostgreSQL จริง (trait Db · job <c>db-test</c>) — แจ้งเตือนรับ/จ่ายชำระ (เจ้าของรายงาน 2026-11-06):
/// <para>(ก) บริษัทที่เอาติ๊กค่าตั้งต้นออกแล้วบันทึก (แถวปิดทุกช่อง) ต้อง<b>ไม่</b>ได้ประกาศกลุ่ม LINE กลับมา · ทิศตรงข้าม: ไม่มีแถวเลย ⇒ ค่าตั้งต้นยังส่งกลุ่ม</para>
/// <para>(ข) ลิงก์ในข้อความกลุ่ม LINE เป็น URL เต็ม ไม่ใช่ "🔗 /pages/…"</para>
/// <para>(ค) <see cref="NotificationEventSplit.MigrationSql"/> คัดลอกค่าตั้ง payment_received → payment_made เฉพาะบริษัทที่ยังไม่มีแถวคีย์ใหม่ · idempotent · ไม่ทับค่าที่ผู้ใช้ตั้งเอง</para>
/// </summary>
[Trait("Category", "Db")]
public class PaymentNotificationRound249DbTests
{
    private readonly ITestOutputHelper _out;
    public PaymentNotificationRound249DbTests(ITestOutputHelper output) => _out = output;

    private static readonly SecretProtector Secrets = new("db-test-key-249");
    private const string Group = "C00000000000000000000000000000249";

    private static async Task<Guid> SeedCompanyAsync(Accounting.Data.AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบแจ้งเตือนชำระ 249", TaxId = "0105556000249" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id, LineEnabled = true, LineChannelAccessToken = Secrets.Protect("company-token"), LineDefaultGroupId = Group });
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static (NotificationEngine Engine, List<string> GroupTexts) Engine(Accounting.Data.AccountingDbContext db)
    {
        var groupTexts = new List<string>();
        var line = F4Proxy<ILineNotifyService>.Create((m, a) =>
        {
            if (m == nameof(ILineNotifyService.TryPushCompanyGroupAsync)) { groupTexts.Add((string)a[1]!); return LinePushResult.Success; }
            if (m == nameof(ILineNotifyService.TryPushViaNextAccAsync)) return LinePushResult.Success;
            return null;
        });
        var org = F4Proxy<IOrganizationService>.Create((_, _) => null);
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://acc.example.co.th" }).Build();
        return (new NotificationEngine(db, line, org, NullLogger<NotificationEngine>.Instance, Secrets, cfg), groupTexts);
    }

    private static NotificationContext Ctx(Guid docId) => new()
    {
        Title = PaymentNotificationText.PaidTitle, Message = "📄 ใบตั้งหนี้ PI-1",
        ActionUrl = $"/pages/documents.html?id={docId}", EntityType = "Document", EntityId = docId,
    };

    [Fact]
    public async Task ก_เอาติ๊กค่าตั้งต้นออกแล้วบันทึก_ไม่ประกาศกลุ่มกลับมา()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedCompanyAsync(db);
        // หน้า notifications.html บันทึกทุกช่อง — ติ๊กออกหมด = แถวปิดทุกช่อง
        db.NotificationSettings.Add(new NotificationSetting { CompanyId = cid, EventKey = NotificationEvents.PaymentReceived, RecipientRole = NotificationRecipientRoles.Owner });
        await db.SaveChangesAsync();

        using var db2 = DbTestDatabase.TryCreateContext()!;
        var (engine, texts) = Engine(db2);
        await engine.DispatchAsync(cid, NotificationEvents.PaymentReceived, Ctx(Guid.NewGuid()));
        Assert.Empty(texts);
    }

    [Fact]
    public async Task ข_ทิศตรงข้าม_ยังไม่ตั้ง_ค่าตั้งต้นส่งกลุ่ม_พร้อมลิงก์เต็ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedCompanyAsync(db);
        var docId = Guid.NewGuid();

        var (engine, texts) = Engine(db);
        await engine.DispatchAsync(cid, NotificationEvents.PaymentMade, Ctx(docId));
        var text = Assert.Single(texts);
        Assert.Contains(PaymentNotificationText.PaidTitle, text);
        Assert.DoesNotContain("🔗 /", text);
        Assert.Matches(@"🔗 https?://\S+/pages/documents\.html\?id=" + docId, text);
    }

    [Fact]
    public async Task ค_migrationคัดลอกค่าตั้งฝั่งรับไปฝั่งจ่าย_เฉพาะบริษัทที่ยังไม่มีแถว_idempotent()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        // A: ตั้งฝั่งรับไว้ (ปิดกลุ่ม LINE · เปิดกระดิ่งฝ่ายบัญชี) — ยังไม่มีแถวฝั่งจ่าย
        var a = await SeedCompanyAsync(db);
        db.NotificationSettings.Add(new NotificationSetting { CompanyId = a, EventKey = NotificationEvents.PaymentReceived, RecipientRole = NotificationRecipientRoles.Accounting, EnableSystem = true });
        // B: ตั้งฝั่งรับ + ตั้งฝั่งจ่ายเองแล้ว (เปิดกลุ่ม) ⇒ ห้ามถูกทับ/เติม
        var b = await SeedCompanyAsync(db);
        db.NotificationSettings.Add(new NotificationSetting { CompanyId = b, EventKey = NotificationEvents.PaymentReceived, RecipientRole = NotificationRecipientRoles.Accounting, EnableSystem = true });
        db.NotificationSettings.Add(new NotificationSetting { CompanyId = b, EventKey = NotificationEvents.PaymentMade, RecipientRole = NotificationRecipientRoles.Owner, EnableLineGroup = true });
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(NotificationEventSplit.SettingsCopySql);
        await db.Database.ExecuteSqlRawAsync(NotificationEventSplit.SettingsCopySql);   // บูตซ้ำ = no-op

        using var db2 = DbTestDatabase.TryCreateContext()!;
        var aMade = await db2.NotificationSettings.AsNoTracking().Where(n => n.CompanyId == a && n.EventKey == NotificationEvents.PaymentMade).ToListAsync();
        var copied = Assert.Single(aMade);
        Assert.Equal(NotificationRecipientRoles.Accounting, copied.RecipientRole);
        Assert.True(copied.EnableSystem);
        Assert.False(copied.EnableLineGroup);

        var bMade = await db2.NotificationSettings.AsNoTracking().Where(n => n.CompanyId == b && n.EventKey == NotificationEvents.PaymentMade).ToListAsync();
        var own = Assert.Single(bMade);
        Assert.Equal(NotificationRecipientRoles.Owner, own.RecipientRole);
        Assert.True(own.EnableLineGroup);

        // ผลที่ engine เห็นสำหรับ A: ไม่ประกาศกลุ่ม (ค่าตั้งเดิมของบริษัทปิดกลุ่ม) — ไม่ถอยไปค่าตั้งต้น
        var (engine, texts) = Engine(db2);
        await engine.DispatchAsync(a, NotificationEvents.PaymentMade, Ctx(Guid.NewGuid()));
        Assert.Empty(texts);
    }
}
