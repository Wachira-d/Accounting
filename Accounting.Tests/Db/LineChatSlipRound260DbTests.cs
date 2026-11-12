using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Line;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 260 (คำตัดสิน 260-1-1) บน PostgreSQL จริง — สลิปจากแชต LINE OA ของบริษัท:
/// ยอดตรงใบเดียว ⇒ รายการรอยืนยันบนใบนั้นอัตโนมัติ (ไม่ประทับชำระแล้ว · ยอดค้างไม่ขยับ) · ยอดหลังหัก ณ ที่จ่าย · ใบของผู้ติดต่ออื่นไม่เป็นผู้สมัคร ·
/// หลายใบ ⇒ ปุ่มเลือก (ถูก · แก้ data · แหล่งแชตอื่น · หมดอายุ · กดซ้ำ · ไม่ใช่ใบเหล่านี้) · สลิปซ้ำ ⇒ ไม่มีแถวใหม่ · ไม่ได้ผูก LINE ⇒ คิวพนักงาน ·
/// ปิดสวิตช์ ⇒ ไม่ใช่งานของเส้นนี้ · พนักงานผูกด้วยเลขที่เอกสาร
/// </summary>
[Trait("Category", "Db")]
public class LineChatSlipRound260DbTests
{
    private readonly ITestOutputHelper _out;
    public LineChatSlipRound260DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("line-chat-slip-260-db");
    private const string Bot = "Ubot260000000000000000000000000001";
    private const string ChannelSecret = "channel-secret-260";

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["App:BaseUrl"] = "https://app.nextacc.test",
    }).Build();

    private sealed record Seed(Guid CompanyId, Guid ContactId, string UserId, LocalDiskFileStorage Storage);

    private static byte[] Png(string tag)
    {
        var head = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        return head.Concat(System.Text.Encoding.UTF8.GetBytes("slip-260-" + tag + Guid.NewGuid())).ToArray();
    }

    private static async Task<Seed> SeedAsync(AccountingDbContext db, bool intake = true, bool bind = true)
    {
        var company = new Company { Name = "บริษัท 260 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000009", Email = "office@seller260.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings
        {
            CompanyId = company.Id, LineEnabled = true, LineChannelAccessToken = Secrets.Protect("token-260"),
            LineChannelSecret = Secrets.Protect(ChannelSecret), LineOaDestination = Bot, LineChatSlipIntakeEnabled = intake,
        });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 260 จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = "0107537000017", Email = "ap@buyer260.co.th", Address = "1 ถนนสุขุมวิท กรุงเทพฯ",
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var userId = "U" + Guid.NewGuid().ToString("N");
        if (bind)
            db.ContactNotifyEndpoints.Add(new ContactNotifyEndpoint
            {
                CompanyId = company.Id, ContactId = contact.Id, Kind = NotifyEndpointKind.LineUser, LineUserId = Secrets.Protect(userId),
                LineIdHash = NotifyEndpointKind.LineIdHash(Bot, userId), OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = Bot,
                Verified = true, Source = "test", ShowAmounts = true, TopicsJson = NotifyTopics.Serialize(NotifyTopics.All),
            });
        await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "lcs260-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        return new Seed(company.Id, contact.Id, userId, storage);
    }

    private static async Task<Document> InvoiceAsync(AccountingDbContext db, Guid companyId, Guid contactId, decimal subTotal, decimal wht = 0m, int ageDays = 1)
    {
        var vat = Math.Round(subTotal * 0.07m, 2, MidpointRounding.AwayFromZero);
        var inv = new Document
        {
            CompanyId = companyId, ContactId = contactId, DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved,
            DocumentNumber = "INV260-" + Random.Shared.Next(100000, 999999), DocumentDate = DateTime.UtcNow.Date.AddDays(-ageDays),
            SubTotal = subTotal, VatAmount = vat, WithholdingTaxAmount = wht, TotalAmount = subTotal + vat, BalanceDue = subTotal + vat, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = subTotal, Amount = subTotal, VatRate = 7m, VatAmount = vat });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        return inv;
    }

    private static DocumentPayLinkService PayLinks(AccountingDbContext db, Seed s)
        => new(db, Array.Empty<IPaymentProvider>(), Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance, s.Storage);

    private static ISlipImageReader Reader(OcrSlipText.Reading reading)
        => F4Proxy<ISlipImageReader>.Create((m, _) => m switch
        {
            "get_IsAvailable" => true,
            nameof(ISlipImageReader.ReadAsync) => reading,
            _ => null,
        });

    private static ICustomerLineLinkService Line(byte[] content)
        => F4Proxy<ICustomerLineLinkService>.Create((m, _) => m == nameof(ICustomerLineLinkService.DownloadMessageContentAsync) ? content : null);

    private static LineChatSlipService Svc(AccountingDbContext db, Seed s, byte[] content, OcrSlipText.Reading reading)
    {
        var intents = new PaymentIntentService(db, new IPaymentProvider[] { new ManualSlipPaymentProvider() },
            Array.Empty<IPaymentCompletionHandler>(), NullLogger<PaymentIntentService>.Instance);
        var slips = new SlipEvidenceService(db, intents, s.Storage);
        return new LineChatSlipService(db, Secrets, Line(content), Reader(reading), PayLinks(db, s), slips, s.Storage, NullLogger<LineChatSlipService>.Instance);
    }

    private static OcrSlipText.Reading Sure(decimal amount, string? reference = null) => new(new[] { amount }, amount, DateTime.UtcNow.Date, reference);

    private static string Json(IReadOnlyList<object>? reply) => JsonSerializer.Serialize(reply);

    private static List<string> PostbackData(IReadOnlyList<object>? reply)
    {
        using var doc = JsonDocument.Parse(Json(reply));
        return doc.RootElement[0].GetProperty("quickReply").GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("action").GetProperty("data").GetString()!).ToList();
    }

    [Fact]
    public async Task ยอดตรงใบเดียว_สร้างรายการรอยืนยันบนใบนั้นเอง_ไม่ประทับชำระแล้ว_ใบผู้ติดต่ออื่นยอดเท่ากันไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var inv = await InvoiceAsync(db, s.CompanyId, s.ContactId, 1000m);
        var stranger = new Contact { CompanyId = s.CompanyId, Name = "ลูกค้าอื่น 260", IsCustomer = true, ContactType = ContactType.JuristicPerson };
        db.Contacts.Add(stranger);
        await db.SaveChangesAsync();
        var theirs = await InvoiceAsync(db, s.CompanyId, stranger.Id, 1000m, ageDays: 5);

        var reply = await Svc(db, s, Png("one"), Sure(1070m, "REF260ONE0001")).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "100001");
        Assert.Contains(inv.DocumentNumber, Json(reply));

        using var c2 = DbTestDatabase.TryCreateContext()!;
        var intent = await c2.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId);
        Assert.Equal(inv.Id, intent.SourceId);
        Assert.Equal(PaymentMethodKind.ManualSlip, intent.MethodKind);
        Assert.True(PaymentIntentPolicy.IsOpen(intent.Status));             // รอยืนยัน — ไม่ใช่สำเร็จ (F2 ข้อ 3)
        Assert.Equal(1070m, intent.Amount);
        Assert.Equal("REF260ONE0001", intent.SlipBankRef);                  // เลขอ้างอิงให้ BankReceiptAutoSettle จับเมื่อรายการเดินบัญชีเข้า
        Assert.Equal(1070m, (await c2.Documents.AsNoTracking().SingleAsync(d => d.Id == inv.Id)).BalanceDue);
        Assert.False(await c2.Payments.AnyAsync(p => p.CompanyId == s.CompanyId));
        Assert.False(await c2.PaymentIntents.AnyAsync(i => i.SourceId == theirs.Id));
        var row = await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId);
        Assert.Equal(LineChatSlipRule.Statuses.Submitted, row.Status);
        Assert.Equal(intent.Id, row.PaymentIntentId);
        Assert.True(await c2.Set<FileAttachment>().AnyAsync(f => f.CompanyId == s.CompanyId && f.EntityId == inv.Id && f.CreatedBy == SlipEvidenceService.ChannelLineChat));
    }

    [Fact]
    public async Task ยอดหลังหักณที่จ่าย_ตรงใบเดียว_แจ้งด้วยยอดสุทธิจากตัวคิดของหน้าชำระ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var inv = await InvoiceAsync(db, s.CompanyId, s.ContactId, 1000m, wht: 30m);
        var due = (await PayLinks(db, s).DueAmountsForDocumentAsync(s.CompanyId, inv.Id))!;
        Assert.Null(due.Refusal);
        Assert.True(due.Net < inv.BalanceDue);
        await Svc(db, s, Png("net"), Sure(due.Net)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "100002");
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var intent = await c2.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId);
        Assert.Equal(inv.Id, intent.SourceId);
        Assert.Equal(due.Net, intent.Amount);
    }

    private async Task<(Seed S, Document A, Document B, IReadOnlyList<object>? Reply, LineChatSlip Row)> ManyAsync(AccountingDbContext db, string tag)
    {
        var s = await SeedAsync(db);
        var a = await InvoiceAsync(db, s.CompanyId, s.ContactId, 500m, ageDays: 3);
        var b = await InvoiceAsync(db, s.CompanyId, s.ContactId, 500m, ageDays: 2);
        var reply = await Svc(db, s, Png(tag), Sure(535m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "200001");
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var row = await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId);
        return (s, a, b, reply, row);
    }

    [Fact]
    public async Task หลายใบ_ปุ่มเลือก_กดใบที่ถูก_สร้างรายการรอยืนยันบนใบนั้น_กดซ้ำไม่ซ้อน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (s, a, b, reply, row) = await ManyAsync(db, "many");
        Assert.Equal(LineChatSlipRule.Statuses.AwaitingChoice, row.Status);
        Assert.False(await db.PaymentIntents.AnyAsync(i => i.CompanyId == s.CompanyId));   // ยังไม่ผูกจนลูกค้าเลือก
        var data = PostbackData(reply);
        Assert.Equal(3, data.Count);   // สองใบ + "ไม่ใช่ใบเหล่านี้"

        using var c2 = DbTestDatabase.TryCreateContext()!;
        var pickB = data.Single(d => d.Contains(b.Id.ToString("N")));
        var picked = await Svc(c2, s, Png("x"), Sure(535m)).HandlePostbackAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), pickB);
        Assert.Contains(b.DocumentNumber, Json(picked));
        using var c3 = DbTestDatabase.TryCreateContext()!;
        var intent = await c3.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId);
        Assert.Equal(b.Id, intent.SourceId);
        Assert.Equal(535m, intent.Amount);
        Assert.Equal(LineChatSlipRule.Statuses.Submitted, (await c3.LineChatSlips.AsNoTracking().SingleAsync(x => x.Id == row.Id)).Status);

        // กดซ้ำ (ปุ่มเดิม/ปุ่มอีกใบ) ⇒ ใช้ไม่ได้ ไม่มีรายการที่สอง
        using var c4 = DbTestDatabase.TryCreateContext()!;
        var again = await Svc(c4, s, Png("x"), Sure(535m)).HandlePostbackAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), data.Single(d => d.Contains(a.Id.ToString("N"))));
        Assert.Contains(LineChatSlipRule.Replies.ChoiceInvalid, Json(again));
        Assert.Equal(1, await c4.PaymentIntents.CountAsync(i => i.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task ปุ่มเลือก_แก้data_แหล่งแชตอื่น_หมดอายุ_ไม่ผ่าน_ไม่มีรายการรอยืนยัน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (s, a, b, reply, row) = await ManyAsync(db, "tamper");
        var pickA = PostbackData(reply).Single(d => d.Contains(a.Id.ToString("N")));

        // แก้ data: เปลี่ยนใบเป็นใบอื่น (ลายเซ็นเดิม)
        var tampered = pickA.Replace(a.Id.ToString("N"), b.Id.ToString("N"));
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(LineChatSlipRule.Replies.ChoiceInvalid,
                Json(await Svc(c, s, Png("x"), Sure(535m)).HandlePostbackAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), tampered)));
        // ผู้ใช้ LINE อื่น (ผู้ติดต่ออื่น/คนแปลกหน้า) ส่ง data เดียวกัน
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(LineChatSlipRule.Replies.ChoiceInvalid,
                Json(await Svc(c, s, Png("x"), Sure(535m)).HandlePostbackAsync(s.CompanyId, Bot, LineChatSource.User("U" + Guid.NewGuid().ToString("N")), pickA)));
        // บริษัทอื่น
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(LineChatSlipRule.Replies.ChoiceInvalid,
                Json(await Svc(c, s, Png("x"), Sure(535m)).HandlePostbackAsync(Guid.NewGuid(), Bot, LineChatSource.User(s.UserId), pickA)));
        Assert.False(await db.PaymentIntents.AnyAsync(i => i.CompanyId == s.CompanyId));

        // หมดอายุ ⇒ ใช้ไม่ได้ + เข้าคิวพนักงาน (ลูกค้าไม่ได้เลือก)
        await db.LineChatSlips.Where(x => x.Id == row.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.ChoiceExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(LineChatSlipRule.Replies.ChoiceInvalid,
                Json(await Svc(c, s, Png("x"), Sure(535m)).HandlePostbackAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), pickA)));
        using var c5 = DbTestDatabase.TryCreateContext()!;
        var after = await c5.LineChatSlips.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(LineChatSlipRule.Statuses.Unmatched, after.Status);
        Assert.Equal(LineChatSlipRule.Reasons.ChoiceExpired, after.Reason);
        Assert.False(await c5.PaymentIntents.AnyAsync(i => i.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task ปุ่มไม่ใช่ใบเหล่านี้_เข้าคิวพนักงาน_พนักงานผูกด้วยเลขที่เอกสาร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (s, a, _, reply, row) = await ManyAsync(db, "decline");
        var decline = PostbackData(reply).Single(d => d.Contains("|" + LineChatSlipRule.DeclineToken + "|"));
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(LineChatSlipRule.Replies.Declined,
                Json(await Svc(c, s, Png("x"), Sure(535m)).HandlePostbackAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), decline)));

        using var c2 = DbTestDatabase.TryCreateContext()!;
        var svc = Svc(c2, s, Png("x"), Sure(535m));
        var list = await svc.ListOpenAsync(s.CompanyId);
        var item = Assert.Single(list.Items);
        Assert.Equal(LineChatSlipRule.Reasons.Label(LineChatSlipRule.Reasons.CustomerDeclined), item.ReasonLabel);
        Assert.Contains(item.Options, o => o.DocumentId == a.Id && o.AmountMatches);
        var r = await svc.AssignAsync(s.CompanyId, row.Id, new AssignLineChatSlipRequest(null, a.DocumentNumber, null), "user:test");
        Assert.Equal(a.Id, r.DocumentId);
        Assert.Equal(535m, r.Amount);
        using var c3 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(a.Id, (await c3.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == r.PaymentIntentId)).SourceId);
        Assert.Equal(LineChatSlipRule.Statuses.Assigned, (await c3.LineChatSlips.AsNoTracking().SingleAsync(x => x.Id == row.Id)).Status);
        // ผูกแล้วผูกซ้ำ/ปิดไม่ได้
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(c3, s, Png("x"), Sure(535m)).DismissAsync(s.CompanyId, row.Id, "ซ้ำ", "user:test"));
    }

    [Fact]
    public async Task สลิปซ้ำ_รูปเดิมหรือเลขอ้างอิงเดิม_ตอบว่าได้รับแล้ว_ไม่มีแถวใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        await InvoiceAsync(db, s.CompanyId, s.ContactId, 1000m);
        var bytes = Png("dup");
        await Svc(db, s, bytes, Sure(1070m, "REF260DUP0001")).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "300001");
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(LineChatSlipRule.Replies.Duplicate,
                Json(await Svc(c, s, bytes, Sure(1070m, "REF260DUP0001")).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "300002")));
        // รูปต่าง (ถ่ายหน้าจอใหม่) แต่เลขอ้างอิงเดิม
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(LineChatSlipRule.Replies.Duplicate,
                Json(await Svc(c, s, Png("dup2"), Sure(1070m, "REF260DUP0001")).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "300003")));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await c2.LineChatSlips.CountAsync(x => x.CompanyId == s.CompanyId));
        Assert.Equal(1, await c2.PaymentIntents.CountAsync(i => i.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task ไม่ได้ผูกLINE_เข้าคิวพนักงานพร้อมเหตุ_ไม่มีรายการรอยืนยัน_ปิดรายการต้องมีเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, bind: false);
        await InvoiceAsync(db, s.CompanyId, s.ContactId, 1000m);
        var reply = await Svc(db, s, Png("unbound"), Sure(1070m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "400001");
        Assert.Contains(LineChatSlipRule.Replies.StaffWillCheck, Json(reply));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var row = await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId);
        Assert.Equal(LineChatSlipRule.Statuses.Unmatched, row.Status);
        Assert.Equal(LineChatSlipRule.Reasons.Unbound, row.Reason);
        Assert.Null(row.ContactId);
        Assert.False(await c2.PaymentIntents.AnyAsync(i => i.CompanyId == s.CompanyId));
        var svc = Svc(c2, s, Png("x"), Sure(1070m));
        Assert.Equal(1, await svc.OpenCountAsync(s.CompanyId));
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.DismissAsync(s.CompanyId, row.Id, "  ", "user:test"));
        await svc.DismissAsync(s.CompanyId, row.Id, "ไม่ใช่สลิป", "user:test");
        Assert.Equal(0, await Svc(DbTestDatabase.TryCreateContext()!, s, Png("x"), Sure(1070m)).OpenCountAsync(s.CompanyId));
        // บริษัทอื่นมองไม่เห็นแถวนี้
        Assert.Empty((await svc.ListOpenAsync(Guid.NewGuid())).Items);
    }

    [Fact]
    public async Task ทิศตรงข้าม_ปิดสวิตช์_ไม่ใช่งานของเส้นนี้_ไม่ดึงรูป_ไม่มีแถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, intake: false);
        await InvoiceAsync(db, s.CompanyId, s.ContactId, 1000m);
        Assert.Null(await Svc(db, s, Png("off"), Sure(1070m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "500001"));
        // กลุ่มที่ไม่ได้ผูก ⇒ เส้นกลุ่มเดิม (null) แม้สวิตช์เปิด
        var s2 = await SeedAsync(db);
        Assert.Null(await Svc(db, s2, Png("grp"), Sure(1070m)).HandleImageAsync(s2.CompanyId, Bot, LineChatSource.Group("C" + Guid.NewGuid().ToString("N")), "500002"));
        Assert.False(await db.LineChatSlips.AnyAsync(x => x.CompanyId == s.CompanyId || x.CompanyId == s2.CompanyId));
        // postback ที่ไม่ใช่ของเส้นนี้
        Assert.Null(await Svc(db, s2, Png("x"), Sure(1m)).HandlePostbackAsync(s2.CompanyId, Bot, LineChatSource.User(s2.UserId), "approve:" + Guid.NewGuid()));
    }
}
