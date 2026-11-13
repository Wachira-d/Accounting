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
            SubTotal = subTotal, VatAmount = vat, WithholdingTaxAmount = wht, TotalAmount = subTotal + vat - wht, BalanceDue = subTotal + vat - wht, Currency = "THB",
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

    /// <summary>สลิปจริง (มีคำว่าสำเร็จ/เลขอ้างอิง — ฝ่ายค้าน M3: ภาพที่ไม่ดูเป็นสลิปไม่ผูกเอง)</summary>
    private static OcrSlipText.Reading Sure(decimal amount, string? reference = null) => new(new[] { amount }, amount, DateTime.UtcNow.Date, reference, LooksLikeSlip: true);

    // ไม่ escape อักษรไทย (ค่าตั้งต้นของ System.Text.Json แปลงเป็น \uXXXX — ข้อความ LINE ถูกต้องทั้งสองแบบ แต่เทสต์ค้นคำไทย)
    private static readonly JsonSerializerOptions Raw = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Json(IReadOnlyList<object>? reply) => JsonSerializer.Serialize(reply, Raw);

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
        Assert.Null(intent.SlipBankRef);                                     // ฝ่ายค้าน M1: เลขอ้างอิงจาก OCR ไม่ถูกประทับ (กันซ้ำเท็จทุกช่องทาง)
        Assert.Equal(1070m, (await c2.Documents.AsNoTracking().SingleAsync(d => d.Id == inv.Id)).BalanceDue);
        Assert.False(await c2.Payments.AnyAsync(p => p.CompanyId == s.CompanyId));
        Assert.False(await c2.PaymentIntents.AnyAsync(i => i.SourceId == theirs.Id));
        var row = await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId);
        Assert.Equal(LineChatSlipRule.Statuses.Submitted, row.Status);
        Assert.Equal(intent.Id, row.PaymentIntentId);
        Assert.NotNull(row.FileDeletedAt);                                   // ฝ่ายค้าน M4: สำเนาในแชตลบแล้ว (หลักฐานอยู่ที่ไฟล์แนบเอกสาร)
        Assert.False(System.IO.File.Exists(row.StoragePath));
        Assert.True(await c2.PdpaProcessingActivities.AnyAsync(r => r.CompanyId == s.CompanyId && r.Purpose == LineChatSlipRule.RopaPurpose));
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
        Assert.True(due.Gross > due.Net);   // ผู้ซื้อนิติบุคคลหัก ณ ที่จ่าย ⇒ ยอดก่อนหัก > ยอดที่ต้องโอน

        // ทิศตรงข้าม: โอนยอดเต็มก่อนหัก ⇒ ไม่ผูกเอง (เกินยอดค้าง) · คิวพนักงานพร้อมเหตุ
        await Svc(db, s, Png("gross"), Sure(due.Gross)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "100003");
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(await c.PaymentIntents.AnyAsync(i => i.CompanyId == s.CompanyId));
            Assert.Equal(LineChatSlipRule.Reasons.GrossBeforeWht, (await c.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId)).Reason);
        }

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
        // ฝ่ายค้าน L1: LINE ส่งข้อความเดิมซ้ำ (messageId เดิม) ⇒ ตอบซ้ำ ไม่ดาวน์โหลด/ไม่มีแถว
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(LineChatSlipRule.Replies.Duplicate,
                Json(await Svc(c, s, Png("redelivery"), Sure(1070m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "300001")));
        // ฝ่ายค้าน M1 (ทิศตรงข้าม): รูปต่างแต่เลขอ้างอิงที่ OCR อ่านได้เดิม ⇒ ไม่ทิ้ง — เข้าคิวพนักงานเหตุ DuplicateRef (ไม่ผูกเอง)
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(LineChatSlipRule.Replies.StaffWillCheck,
                Json(await Svc(c, s, Png("dup2"), Sure(1070m, "REF260DUP0001")).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "300003")));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(2, await c2.LineChatSlips.CountAsync(x => x.CompanyId == s.CompanyId));
        Assert.Equal(LineChatSlipRule.Reasons.DuplicateRef,
            (await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.LineMessageId == "300003")).Reason);
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
        using (var c3 = DbTestDatabase.TryCreateContext()!)
        {
            var closed = await c3.LineChatSlips.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.NotNull(closed.FileDeletedAt);                            // ฝ่ายค้าน M4: ปิดแล้วลบรูป
            Assert.False(System.IO.File.Exists(closed.StoragePath));
            Assert.Null(await Svc(c3, s, Png("x"), Sure(1070m)).ReadFileAsync(s.CompanyId, row.Id));
        }
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

    /// <summary>ที่เก็บที่อ่านไฟล์ล้ม (ดิสก์/S3 ขัดข้อง) — เมธอดอื่นเรียกที่เก็บจริง</summary>
    private static IFileStorage FailingRead(IFileStorage real) => F4Proxy<IFileStorage>.Create((m, a) =>
    {
        if (m == nameof(IFileStorage.ReadAllBytesAsync)) throw new System.IO.IOException("ดิสก์ขัดข้อง (เทสต์)");
        var mi = typeof(IFileStorage).GetMethods().First(x => x.Name == m && x.GetParameters().Length == a.Length
            && x.GetParameters().Select((p, i) => a[i] == null || p.ParameterType.IsInstanceOfType(a[i])).All(ok => ok));
        return mi.Invoke(real, a);
    });

    [Fact]
    public async Task ฝ่ายค้าน_M2_ปุ่มเลือกใบแล้วที่เก็บล้ม_แถวกลับคิวพนักงาน_ไม่ค้างกำลังแจ้งโอน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (s, _, b, reply, row) = await ManyAsync(db, "m2");
        var pickB = PostbackData(reply).Single(d => d.Contains(b.Id.ToString("N")));
        using var c = DbTestDatabase.TryCreateContext()!;
        var intents = new PaymentIntentService(c, new IPaymentProvider[] { new ManualSlipPaymentProvider() },
            Array.Empty<IPaymentCompletionHandler>(), NullLogger<PaymentIntentService>.Instance);
        var failing = FailingRead(s.Storage);
        var svc = new LineChatSlipService(c, Secrets, Line(Png("x")), Reader(Sure(535m)), PayLinks(c, s), new SlipEvidenceService(c, intents, failing),
            failing, NullLogger<LineChatSlipService>.Instance);
        Assert.Contains(LineChatSlipRule.Replies.StaffWillCheck, Json(await svc.HandlePostbackAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), pickB)));
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var after = await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(LineChatSlipRule.Statuses.Unmatched, after.Status);
        Assert.Equal(LineChatSlipRule.Reasons.SystemError, after.Reason);
        Assert.False(await c2.PaymentIntents.AnyAsync(i => i.CompanyId == s.CompanyId));
        Assert.Equal(1, await Svc(c2, s, Png("x"), Sure(535m)).OpenCountAsync(s.CompanyId));
    }

    [Fact]
    public async Task ฝ่ายค้าน_M4_งานเก็บรักษา_ลบรูปที่ไม่ได้ผูกเกิน90วัน_ทิศตรงข้ามรูปใหม่ไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, bind: false);
        await Svc(db, s, Png("old"), Sure(1070m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "600001");
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Svc(c, s, Png("new"), Sure(1070m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "600002");
        await db.LineChatSlips.Where(x => x.CompanyId == s.CompanyId && x.LineMessageId == "600001")
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddDays(-(LineChatSlipRule.UnmatchedRetentionDays + 1))));
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.True(await LineChatSlipService.PurgeExpiredAsync(c, s.Storage, NullLogger.Instance, DateTime.UtcNow) >= 1);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var old = await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.LineMessageId == "600001");
        var fresh = await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.LineMessageId == "600002");
        Assert.Equal(LineChatSlipRule.Statuses.Dismissed, old.Status);
        Assert.NotNull(old.FileDeletedAt);
        Assert.False(System.IO.File.Exists(old.StoragePath));
        Assert.Equal(LineChatSlipRule.Statuses.Unmatched, fresh.Status);
        Assert.Null(fresh.FileDeletedAt);
        Assert.True(System.IO.File.Exists(fresh.StoragePath));
    }

    private static SlipEvidenceService PortalSlips(AccountingDbContext db, Seed s)
        => new(db, new PaymentIntentService(db, new IPaymentProvider[] { new ManualSlipPaymentProvider() },
            Array.Empty<IPaymentCompletionHandler>(), NullLogger<PaymentIntentService>.Instance), s.Storage);

    [Fact]
    public async Task ฝ่ายค้าน_L4_ใบที่สลิปรอตรวจเต็มยอดแล้วไม่เป็นผู้สมัคร_ทิศตรงข้ามรอตรวจบางส่วนเป็นผู้สมัครด้วยยอดที่เหลือ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var full = await InvoiceAsync(db, s.CompanyId, s.ContactId, 1000m, ageDays: 3);      // ยอดค้าง 1,070
        var partial = await InvoiceAsync(db, s.CompanyId, s.ContactId, 2000m, ageDays: 2);   // ยอดค้าง 2,140
        // สลิปพอร์ทัลรอตรวจ: ใบแรกเต็มยอด · ใบที่สองบางส่วน 1,000
        await PortalSlips(db, s).SubmitAsync(s.CompanyId, full.Id, s.ContactId, new SlipFile(Png("portal-full"), "image/png", "a.png"),
            1070m, null, SlipEvidenceService.ChannelPortal);
        using (var c = DbTestDatabase.TryCreateContext()!)
            await PortalSlips(c, s).SubmitAsync(s.CompanyId, partial.Id, s.ContactId, new SlipFile(Png("portal-part"), "image/png", "b.png"),
                1000m, null, SlipEvidenceService.ChannelPortal);

        // สลิปแชตยอด 1,070 (เท่ายอดค้างของใบแรก) ⇒ ใบแรกไม่เป็นผู้สมัคร (รอตรวจเต็มแล้ว) ⇒ ไม่ตรง — ไม่มีรายการที่สองบนใบแรก
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(LineChatSlipRule.Replies.StaffWillCheck,
                Json(await Svc(c, s, Png("chat-full"), Sure(1070m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "700001")));
        // ทิศตรงข้าม: สลิปแชตยอดที่เหลือของใบที่สอง (2,140 − 1,000 = 1,140) ⇒ ผูกเองกับใบที่สอง
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Contains(partial.DocumentNumber,
                Json(await Svc(c, s, Png("chat-rest"), Sure(1140m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "700002")));

        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await c2.PaymentIntents.CountAsync(i => i.CompanyId == s.CompanyId && i.SourceId == full.Id));
        Assert.Equal(new[] { 1000m, 1140m }, (await c2.PaymentIntents.AsNoTracking().Where(i => i.CompanyId == s.CompanyId && i.SourceId == partial.Id)
            .Select(i => i.Amount).ToListAsync()).OrderBy(a => a).ToArray());
        Assert.Equal(LineChatSlipRule.Reasons.NoMatch,
            (await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.LineMessageId == "700001")).Reason);
    }

    [Fact]
    public async Task ฝ่ายค้าน_L6_พนักงานผูกใบของผู้ติดต่ออื่น_มีคำเตือนและauditข้ามผู้ติดต่อ_ทิศตรงข้ามใบของเจ้าของแชตไม่เตือน_ชนิดที่รับชำระไม่ได้ไม่พบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var other = new Contact { CompanyId = s.CompanyId, Name = "ลูกค้าอื่น 260 L6", IsCustomer = true, ContactType = ContactType.JuristicPerson, TaxId = "0105556000017" };
        db.Contacts.Add(other);
        await db.SaveChangesAsync();
        var mine = await InvoiceAsync(db, s.CompanyId, s.ContactId, 300m);
        var theirs = await InvoiceAsync(db, s.CompanyId, other.Id, 400m);
        var quotation = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Quotation, Status = DocumentStatus.Approved,
            DocumentNumber = "QT260-" + Random.Shared.Next(100000, 999999), DocumentDate = DateTime.UtcNow.Date, SubTotal = 999m, TotalAmount = 999m,
            BalanceDue = 999m, Currency = "THB",
        };
        db.Documents.Add(quotation);
        await db.SaveChangesAsync();
        // สองสลิปจากแชตของเจ้าของ (ผูก LINE) ยอดไม่ตรงใบใด ⇒ คิวพนักงาน
        await Svc(db, s, Png("l6a"), Sure(12.34m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "800001");
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Svc(c, s, Png("l6b"), Sure(56.78m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "800002");
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var rowA = await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.LineMessageId == "800001");
        var rowB = await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.LineMessageId == "800002");
        Assert.Equal(s.ContactId, rowA.ContactId);

        // ชนิดที่รับชำระไม่ได้ (ใบเสนอราคา) ⇒ ไม่พบ (ไม่ผูกกับใบผิดชนิด) · แถวยังอยู่ในคิว
        var notPayable = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Svc(c2, s, Png("x"), Sure(1m)).AssignAsync(s.CompanyId, rowA.Id, new AssignLineChatSlipRequest(null, quotation.DocumentNumber, 100m), "user:test"));
        Assert.Equal(404, notPayable.StatusCode);

        using var c3 = DbTestDatabase.TryCreateContext()!;
        var cross = await Svc(c3, s, Png("x"), Sure(1m)).AssignAsync(s.CompanyId, rowA.Id, new AssignLineChatSlipRequest(null, theirs.DocumentNumber, 100m), "user:test");
        Assert.True(cross.CrossContact);
        using var c4 = DbTestDatabase.TryCreateContext()!;
        var same = await Svc(c4, s, Png("x"), Sure(1m)).AssignAsync(s.CompanyId, rowB.Id, new AssignLineChatSlipRequest(mine.Id, null, 100m), "user:test");
        Assert.False(same.CrossContact);

        using var c5 = DbTestDatabase.TryCreateContext()!;
        var audits = await c5.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == s.CompanyId && a.EntityType == LineChatSlipRule.AuditEntityType).ToListAsync();
        Assert.Contains(audits, a => a.EntityId == rowA.Id.ToString() && a.NewValues!.Contains("\"crossContact\":true"));
        Assert.Contains(audits, a => a.EntityId == rowB.Id.ToString() && a.NewValues!.Contains("\"crossContact\":false"));
        // ไฟล์แนบที่พนักงานผูก ติดป้ายพนักงาน (ฝ่ายค้าน L9)
        Assert.True(await c5.Set<FileAttachment>().AnyAsync(f => f.CompanyId == s.CompanyId && f.EntityId == theirs.Id
            && f.CreatedBy == SlipEvidenceService.ChannelLineChatStaff));
        // เลขที่ซ้ำหลายใบ ⇒ 409: ฐานเทสต์มี unique (CompanyId, DocumentNumber) ทั้งตาราง และฐานจริงมี partial unique ของใบที่ยังไม่ลบ ⇒
        // สร้างกรณีนี้ในฐานไม่ได้ — กิ่ง 409 เป็นด่านสำรองของข้อมูลเก่า (ระบุในรายงาน)
    }

    // ═══ รอบ 268 (คำตัดสิน 268-1-4): แจ้งลูกค้าหลังพนักงานผูก · สลิป PDF ═══

    private sealed record Push(string To, string Text, Guid RetryKey, string OaScope);

    private static LineChatSlipService SvcWithPush(AccountingDbContext db, Seed s, List<Push> pushes, bool ok = true)
    {
        var intents = new PaymentIntentService(db, new IPaymentProvider[] { new ManualSlipPaymentProvider() },
            Array.Empty<IPaymentCompletionHandler>(), NullLogger<PaymentIntentService>.Instance);
        var line = F4Proxy<ILineNotifyService>.Create((m, a) =>
        {
            if (m != nameof(ILineNotifyService.TryPushContactTextAsync)) return null;
            pushes.Add(new Push((string)a[3]!, (string)a[4]!, (Guid)a[5]!, (string)a[1]!));
            return ok ? LinePushResult.Success : LinePushResult.Fail("LINE 429");
        });
        return new LineChatSlipService(db, Secrets, Line(Png("x")), Reader(Sure(535m)), PayLinks(db, s), new SlipEvidenceService(db, intents, s.Storage), s.Storage,
            NullLogger<LineChatSlipService>.Instance, null, line);
    }

    [Fact]
    public async Task C268_1_4_พนักงานผูกสลิป_แจ้งลูกค้าในแชตเดิม_เลขเอกสารและยอด_retry_key_คือแถว_ทิศตรงข้าม_ส่งล้มบอกพนักงาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (s, a, _, reply, row) = await ManyAsync(db, "notify268");
        var decline = PostbackData(reply).Single(d => d.Contains("|" + LineChatSlipRule.DeclineToken + "|"));
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Svc(c, s, Png("x"), Sure(535m)).HandlePostbackAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), decline);
        var pushes = new List<Push>();
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var r = await SvcWithPush(c2, s, pushes).AssignAsync(s.CompanyId, row.Id, new AssignLineChatSlipRequest(null, a.DocumentNumber, null), "user:test");
        var p = Assert.Single(pushes);
        Assert.Equal(s.UserId, p.To);                          // แชตที่ส่งสลิป (ปลายทางที่ผูกด้วยกุญแจเดียวกับตอนรับรูป)
        Assert.Equal(NotifyEndpointKind.ScopeCompany, p.OaScope);
        Assert.Contains(a.DocumentNumber, p.Text);
        Assert.Contains("535.00", p.Text);                     // แสดงยอดได้ (ปลายทาง 1:1 เปิดแสดงยอด)
        Assert.Equal(row.Id, p.RetryKey);
        Assert.Equal(LineChatSlipRule.LinkedNotice.Sent, r.CustomerNotice);
        using var c3 = DbTestDatabase.TryCreateContext()!;
        Assert.Contains(await c3.AuditLogs.AsNoTracking().Where(x => x.CompanyId == s.CompanyId && x.EntityType == LineChatSlipRule.AuditEntityType && x.EntityId == row.Id.ToString())
            .Select(x => x.NewValues!).ToListAsync(), v => v.Contains("customerNotice"));
        // ทิศตรงข้าม: LINE ตอบล้ม ⇒ การผูกยังสำเร็จ · คำตอบบอกพนักงานว่าแจ้งไม่สำเร็จ (ไม่เงียบ)
        var (s2, a2, _, reply2, row2) = await ManyAsync(db, "notify268-fail");
        var decline2 = PostbackData(reply2).Single(d => d.Contains("|" + LineChatSlipRule.DeclineToken + "|"));
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            await Svc(c4, s2, Png("x"), Sure(535m)).HandlePostbackAsync(s2.CompanyId, Bot, LineChatSource.User(s2.UserId), decline2);
        using var c5 = DbTestDatabase.TryCreateContext()!;
        var failed = await SvcWithPush(c5, s2, new List<Push>(), ok: false).AssignAsync(s2.CompanyId, row2.Id, new AssignLineChatSlipRequest(a2.Id, null, null), "user:test");
        Assert.StartsWith("แจ้งลูกค้าในแชตไม่สำเร็จ", failed.CustomerNotice);
        Assert.Equal(a2.Id, failed.DocumentId);
    }

    [Fact]
    public async Task C268_1_4_แชตไม่ได้ผูก_ผูกสลิปได้แต่ไม่ส่ง_บอกเหตุ_สลิป_PDF_เข้าเส้นเดียวกับรูป()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, bind: false);
        var inv = await InvoiceAsync(db, s.CompanyId, s.ContactId, 500m);
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 slip-268 " + Guid.NewGuid());
        await Svc(db, s, pdf, Sure(535m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "268001");
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var row = await c2.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId);
        Assert.Equal("application/pdf", row.ContentType);       // PDF ผ่านตัวตรวจไบต์ + ที่เก็บเดียวกับรูป
        Assert.Equal(LineChatSlipRule.Statuses.Unmatched, row.Status);
        var pushes = new List<Push>();
        var r = await SvcWithPush(c2, s, pushes).AssignAsync(s.CompanyId, row.Id, new AssignLineChatSlipRequest(inv.Id, null, 535m), "user:test");
        Assert.Empty(pushes);                                    // ไม่มี LINE ID ของแชตที่ไม่ได้ผูก — ไม่เดาผู้รับ
        Assert.Equal(LineChatSlipRule.LinkedNotice.Unbound, r.CustomerNotice);
        Assert.True(LineChatSlipRule.IsSlipMessage("file", "slip.PDF"));
        Assert.False(LineChatSlipRule.IsSlipMessage("file", "slip.xlsx"));
        Assert.False(LineChatSlipRule.IsSlipMessage("video", null));
    }

    [Fact]
    public async Task C268_1_7_แชตผูกสองผู้ติดต่อ_พนักงานผูกใบของรายที่สาม_ไม่ส่งเลขเอกสารเข้าแชต_ทิศตรงข้าม_ใบของรายที่ผูก_ส่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var second = new Contact { CompanyId = s.CompanyId, Name = "ลูกค้าคนที่สอง 268", IsCustomer = true, ContactType = ContactType.JuristicPerson };
        var third = new Contact { CompanyId = s.CompanyId, Name = "ลูกค้าคนที่สาม 268", IsCustomer = true, ContactType = ContactType.JuristicPerson };
        db.Contacts.AddRange(second, third);
        await db.SaveChangesAsync();
        // สำนักบัญชีใช้ LINE เดียวผูกสองลูกค้า ⇒ แถวสลิปไม่มี ContactId
        db.ContactNotifyEndpoints.Add(new ContactNotifyEndpoint
        {
            CompanyId = s.CompanyId, ContactId = second.Id, Kind = NotifyEndpointKind.LineUser, LineUserId = Secrets.Protect(s.UserId),
            LineIdHash = NotifyEndpointKind.LineIdHash(Bot, s.UserId), OaScope = NotifyEndpointKind.ScopeCompany, OaDestination = Bot,
            Verified = true, Source = "test", ShowAmounts = true, TopicsJson = NotifyTopics.Serialize(NotifyTopics.All),
        });
        await db.SaveChangesAsync();
        var theirs = await InvoiceAsync(db, s.CompanyId, third.Id, 500m);
        var mine = await InvoiceAsync(db, s.CompanyId, second.Id, 700m);
        await Svc(db, s, Png("m1"), Sure(999m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "268701");
        await Svc(db, s, Png("m1b"), Sure(998m)).HandleImageAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), "268702");
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var rows = await c2.LineChatSlips.AsNoTracking().Where(x => x.CompanyId == s.CompanyId).OrderBy(x => x.LineMessageId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Null(r.ContactId));
        var pushes = new List<Push>();
        var r1 = await SvcWithPush(c2, s, pushes).AssignAsync(s.CompanyId, rows[0].Id, new AssignLineChatSlipRequest(theirs.Id, null, 535m), "user:test");
        Assert.Empty(pushes);
        Assert.Equal(LineChatSlipRule.LinkedNotice.CrossContact, r1.CustomerNotice);
        using var c3 = DbTestDatabase.TryCreateContext()!;
        var r2 = await SvcWithPush(c3, s, pushes).AssignAsync(s.CompanyId, rows[1].Id, new AssignLineChatSlipRequest(mine.Id, null, 749m), "user:test");
        Assert.Single(pushes);
        Assert.Contains(mine.DocumentNumber, pushes[0].Text);
        Assert.Equal(LineChatSlipRule.LinkedNotice.Sent, r2.CustomerNotice);
    }

    [Fact]
    public async Task C268_1_8_ผูกนานหลังสลิปเข้าในชั่วโมงเงียบ_ไม่ส่งตอนนี้_เข้าคิวเลื่อน_คิวส่งครั้งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var (s, a, _, reply, row) = await ManyAsync(db, "quiet268");
        var decline = PostbackData(reply).Single(d => d.Contains("|" + LineChatSlipRule.DeclineToken + "|"));
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Svc(c, s, Png("x"), Sure(535m)).HandlePostbackAsync(s.CompanyId, Bot, LineChatSource.User(s.UserId), decline);
        // สลิปเข้ามาเมื่อ 3 ชม.ก่อน · ชั่วโมงเงียบ = ชั่วโมงปัจจุบัน (เวลาไทย) ถึงอีก 2 ชม. ⇒ ตอนนี้อยู่ในช่วงเงียบแน่นอน
        var hourTh = DateTime.UtcNow.AddHours(7).Hour;
        await db.LineChatSlips.Where(x => x.Id == row.Id).ExecuteUpdateAsync(x => x.SetProperty(r => r.CreatedAt, DateTime.UtcNow.AddHours(-3)));
        await db.CompanySettings.Where(x => x.CompanyId == s.CompanyId).ExecuteUpdateAsync(x => x.SetProperty(c => c.LineQuietHoursEnabled, true)
            .SetProperty(c => c.LineQuietStartHour, hourTh).SetProperty(c => c.LineQuietEndHour, (hourTh + 2) % 24));
        var pushes = new List<Push>();
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var r = await SvcWithPush(c2, s, pushes).AssignAsync(s.CompanyId, row.Id, new AssignLineChatSlipRequest(a.Id, null, null), "user:test");
        Assert.Empty(pushes);
        Assert.StartsWith("จะแจ้งลูกค้าในแชตหลังชั่วโมงเงียบ", r.CustomerNotice);
        using var c3 = DbTestDatabase.TryCreateContext()!;
        var q = await c3.DeferredNotifications.AsNoTracking().SingleAsync(d => d.CompanyId == s.CompanyId && d.Kind == DeferredNotifyPolicy.LineChatSlipLinked && d.EntityId == row.Id);
        Assert.True(q.DueAt > DateTime.UtcNow);
        // ถึงคิว (เครื่องเบื้องหลังเรียก) ⇒ ส่งครั้งเดียว · ล้ม ⇒ ปิดแถวพร้อมเหตุ (ไม่วน)
        Assert.Null(await SvcWithPush(c3, s, pushes).DispatchLinkedNoticeAsync(s.CompanyId, row.Id));
        Assert.Single(pushes);
        Assert.NotNull(await SvcWithPush(c3, s, new List<Push>(), ok: false).DispatchLinkedNoticeAsync(s.CompanyId, row.Id));
    }
}
