using Accounting.Data;
using Accounting.Helpers;
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
/// รอบ 282 (คำตัดสิน 282-1..282-6) บน PostgreSQL จริง — สลิปค่าสินค้า/บริการจากกลุ่ม LINE ของ OA กลาง/อีเมล (<c>LineChatSlipService.HandleForwardedAsync</c> —
/// ทางเข้าเดียวที่ <c>TaxSlipIntakeService.IntakeAsync</c> ส่งต่อหลังจำแนกว่าไม่ใช่สลิปรัฐ):
/// QR + ยอดตรง + บัญชีผู้รับตรง ⇒ บันทึกรับเงินจริง (DocumentService.CreatePaymentAsync ผ่าน handler) ลงบัญชีนั้น ณ วันที่บนสลิป · อีเมลเหมือนกัน ·
/// ผู้ส่งอีเมลยืนยันไม่ได้/บัญชีผู้รับไม่ใช่ของบริษัท/ค่าตั้งปิด ⇒ รอยืนยัน + เหตุ (ไม่มีการชำระ) · ยอดไม่ตรง/สองใบยอดเท่ากัน ⇒ คิวผู้ขาย ·
/// สลิปซ้ำ (ไฟล์ · เลขอ้างอิง QR) ⇒ กัน · คนยืนยันจากหน้ายืนยันแล้วบันทึกครั้งเดียว (กดซ้ำไม่ซ้ำ) · เตือนซ้ำมีเพดาน · tenant isolation ·
/// ไม่มีบริการ AI ใดถูกประกอบในเทสต์นี้ (kill-switch: เส้นนี้ไม่มีจุดเรียก AI)
/// </summary>
[Trait("Category", "Db")]
public class SlipAutoRecordRound282DbTests
{
    private readonly ITestOutputHelper _out;
    public SlipAutoRecordRound282DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("slip-auto-282-db");

    private sealed record Seed(Guid CompanyId, Guid ContactId, Guid BankId, Guid BankGlId, LocalDiskFileStorage Storage);

    private static async Task<Seed> SeedAsync(AccountingDbContext db, bool autoOn = true)
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var company = new Company { Name = "สำนักงาน 282 " + tag, TaxId = "0105556000007", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id, SlipAutoRecordEnabled = autoOn });
        ChartOfAccount Gl(string code, string name, AccountType type)
        {
            var a = new ChartOfAccount { CompanyId = company.Id, AccountCode = code, AccountName = name, AccountType = type, Level = 4, IsActive = true };
            db.ChartOfAccounts.Add(a);
            return a;
        }
        Gl("11111", "เงินสด", AccountType.Asset);
        var glK = Gl("11121", "เงินฝากกสิกร", AccountType.Asset);
        var glS = Gl("11122", "เงินฝากไทยพาณิชย์", AccountType.Asset);
        Gl("11310", "ลูกหนี้การค้า", AccountType.Asset);
        Gl("11910", "ภาษีเงินได้ถูกหัก ณ ที่จ่าย", AccountType.Asset);
        Gl("21911", "ภาษีขาย", AccountType.Liability);
        Gl("21913", "ภาษีขายยังไม่ถึงกำหนด", AccountType.Liability);
        await db.SaveChangesAsync();
        var bankK = new BankAccount { CompanyId = company.Id, AccountName = "ออมทรัพย์ กสิกร", BankName = "กสิกรไทย", AccountNumber = "123-4-50621-9", LinkedAccountId = glK.Id };
        var bankS = new BankAccount { CompanyId = company.Id, AccountName = "ออมทรัพย์ SCB", BankName = "ไทยพาณิชย์", AccountNumber = "222-2-21895-2", LinkedAccountId = glS.Id };
        db.BankAccounts.AddRange(bankK, bankS);
        var buyer = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 282 จำกัด", IsCustomer = true, ContactType = ContactType.Individual, Address = "1 ถนนสีลม กรุงเทพฯ",
        };
        db.Contacts.Add(buyer);
        await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "sar282-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        return new Seed(company.Id, buyer.Id, bankK.Id, glK.Id, storage);
    }

    private static async Task<Document> InvoiceAsync(AccountingDbContext db, Seed s, decimal subTotal = 1_000m, int ageDays = 3)
    {
        var vat = Math.Round(subTotal * 0.07m, 2, MidpointRounding.AwayFromZero);
        var inv = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved,
            DocumentNumber = "INV282-" + Random.Shared.Next(100000, 999999), DocumentDate = DateTime.UtcNow.AddHours(7).Date.AddDays(-ageDays),
            SubTotal = subTotal, VatAmount = vat, TotalAmount = subTotal + vat, BalanceDue = subTotal + vat, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = subTotal, Amount = subTotal, VatRate = 7m, VatAmount = vat });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        return inv;
    }

    private static byte[] Png(string tag)
        => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(System.Text.Encoding.UTF8.GetBytes("slip-282-" + tag + Guid.NewGuid())).ToArray();

    private static readonly DateTime SlipDay = DateTime.UtcNow.AddHours(7).Date.AddDays(-1);

    /// <summary>สลิปจริงจากเครื่องอ่าน: QR ตรวจสลิป (CRC ถูก) + ผู้รับ = กสิกรไทย ••0621 (หรือบัญชีอื่น)</summary>
    private static OcrSlipText.Reading Slip(decimal amount, string qrRef, string payeeMask = "xxx-x-x0621-x", string payeeBank = "004", bool qr = true)
        => new(new[] { amount }, amount, SlipDay, qrRef, LooksLikeSlip: true,
            Payee: new OcrSlipText.SlipParty(OcrSlipText.PartyKind.BankAccount, payeeBank, false, payeeMask, null, null),
            Roles: OcrSlipText.RoleSource.Label, Qr: qr ? new ThaiSlipQr.Payload("014", qrRef) : null);

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private static DocumentService DocSvc(AccountingDbContext db)
        => new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static (LineChatSlipService Svc, SlipAutoRecordService Auto) Svc(AccountingDbContext db, Seed s)
    {
        var handler = new Accounting.Services.Payments.Handlers.DocumentPaymentHandler(db, DocSvc(db), F4Proxy<IGatewayAccountResolver>.Create((_, _) => null),
            NullLogger<Accounting.Services.Payments.Handlers.DocumentPaymentHandler>.Instance, s.Storage);
        var intents = new PaymentIntentService(db, new IPaymentProvider[] { new ManualSlipPaymentProvider() }, new IPaymentCompletionHandler[] { handler },
            NullLogger<PaymentIntentService>.Instance);
        var slips = new SlipEvidenceService(db, intents, s.Storage);
        var payLinks = new DocumentPayLinkService(db, Array.Empty<IPaymentProvider>(), Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance, s.Storage);
        var auto = new SlipAutoRecordService(db, intents, NullLogger<SlipAutoRecordService>.Instance, s.Storage);
        var reader = F4Proxy<ISlipImageReader>.Create((m, _) => m == "get_IsAvailable" ? true : null);
        var line = F4Proxy<ICustomerLineLinkService>.Create((_, _) => null);
        return (new LineChatSlipService(db, Secrets, line, reader, payLinks, slips, s.Storage, NullLogger<LineChatSlipService>.Instance, auto: auto), auto);
    }

    private static ForwardedSlip Fwd(Seed s, OcrSlipText.Reading r, string source = LineChatSlipRule.Sources.CentralGroup, bool verified = true, byte[]? bytes = null,
        IReadOnlyList<Guid>? contacts = null)
        => new(s.CompanyId, contacts ?? new[] { s.ContactId }, source, "grp-" + s.CompanyId.ToString("N")[..12], Guid.NewGuid().ToString("N"), bytes ?? Png(source), r,
            ShowAmounts: true, SenderVerified: verified);

    [Fact]
    public async Task กลุ่มOAกลาง_QRยอดตรงบัญชีผู้รับตรง_บันทึกรับเงินจริงลงบัญชีนั้น_ณวันที่บนสลิป_ตอบว่าบันทึกแล้ว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var inv = await InvoiceAsync(db, s);
        var (svc, _) = Svc(db, s);
        var res = await svc.HandleForwardedAsync(Fwd(s, Slip(1_070m, "0167902468A1ERTY0209")));
        Assert.True(res.Handled);
        Assert.Contains("บันทึกรับเงิน", res.Reply);
        Assert.Contains(inv.DocumentNumber, res.Reply);

        using var c = DbTestDatabase.TryCreateContext()!;
        var intent = await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId);
        Assert.Equal(PaymentIntentStatus.Succeeded, intent.Status);
        Assert.StartsWith(SlipAutoRecordService.Actor, intent.ConfirmedBy);
        Assert.Equal("0167902468A1ERTY0209", intent.SlipBankRef);   // เลขธนาคารจาก QR = ตัวกันซ้ำ
        Assert.Equal(s.BankId, intent.ReceivingBankAccountId);
        Assert.Null(intent.AutoRecordCode);
        var pay = await c.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == inv.Id);
        Assert.Equal(1_070m, pay.Amount);
        Assert.Equal(s.BankId, pay.BankAccountId);
        Assert.Equal(SlipDay, pay.PaymentDate.Date);                      // วันรับเงิน = วันที่บนสลิป (§78/1) ไม่ใช่วันที่ระบบรับรูป
        Assert.Equal(0m, (await c.Documents.AsNoTracking().SingleAsync(d => d.Id == inv.Id)).BalanceDue);
        var row = await c.LineChatSlips.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId);
        Assert.Equal(LineChatSlipRule.Sources.CentralGroup, row.SourceKind);
        Assert.Equal(LineChatSlipRule.Statuses.Submitted, row.Status);
        Assert.True(await c.AuditLogs.AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == intent.Id.ToString() && a.NewValues!.Contains("SlipAutoRecorded")));
    }

    [Fact]
    public async Task อีเมล_ยืนยันผู้ส่งได้_บันทึกเหมือนกัน_ยืนยันไม่ได้_รอยืนยันพร้อมเหตุ_ไม่มีการชำระ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var a = await InvoiceAsync(db, s, 1_000m);
        var b = await InvoiceAsync(db, s, 2_000m);
        var (svc, _) = Svc(db, s);
        var ok = await svc.HandleForwardedAsync(Fwd(s, Slip(1_070m, "EMAILREF000001"), LineChatSlipRule.Sources.Email));
        Assert.Contains("บันทึกรับเงิน", ok.Reply);
        var held = await svc.HandleForwardedAsync(Fwd(s, Slip(2_140m, "EMAILREF000002"), LineChatSlipRule.Sources.Email, verified: false));
        Assert.Equal(LineChatSlipRule.Replies.AwaitingSeller, held.Reply);

        using var c = DbTestDatabase.TryCreateContext()!;
        Assert.True(await c.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == a.Id));
        Assert.False(await c.Payments.AnyAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == b.Id));
        var pending = await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.SourceId == b.Id);
        Assert.True(PaymentIntentPolicy.IsOpen(pending.Status));
        Assert.Equal(SlipAutoRecordRule.Codes.SenderUnverified, pending.AutoRecordCode);
        Assert.NotNull(pending.ConfirmNoticeAt);                                       // วิธีสำรอง: แจ้งให้ยืนยันแล้ว
    }

    [Fact]
    public async Task บัญชีผู้รับไม่ใช่ของบริษัท_หรือไม่มีQR_หรือค่าตั้งปิด_รอยืนยัน_ไม่เดาบัญชี_คนยืนยันแล้วบันทึกครั้งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var inv = await InvoiceAsync(db, s);
        var noQr = await InvoiceAsync(db, s, 3_000m);
        var (svc, _) = Svc(db, s);
        await svc.HandleForwardedAsync(Fwd(s, Slip(1_070m, "NOTOURS0000001", payeeMask: "xxx-x-x7777-x")));
        await svc.HandleForwardedAsync(Fwd(s, Slip(3_210m, "NOQR00000000001", qr: false)));

        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(await c.Payments.AnyAsync(p => p.CompanyId == s.CompanyId));
            var i1 = await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.SourceId == inv.Id);
            Assert.Equal(SlipAutoRecordRule.Codes.MoneySource, i1.AutoRecordCode);
            Assert.Contains("ไม่ใช่บัญชีของบริษัท", i1.AutoRecordReason);
            Assert.Null(i1.ReceivingBankAccountId);                                    // ไม่ประทับบัญชีที่เดา
            Assert.Equal(SlipAutoRecordRule.Codes.NoQr, (await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.SourceId == noQr.Id)).AutoRecordCode);
        }

        // หน้ายืนยัน: ผู้ขายตรวจแล้วเลือกบัญชีที่เงินเข้า ⇒ เส้นยืนยันตัวเดียว · กดซ้ำ ⇒ ไม่บันทึกซ้ำ
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var intent = await c2.PaymentIntents.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId && i.SourceId == inv.Id);
        var (_, auto) = Svc(c2, s);
        var handler = new Accounting.Services.Payments.Handlers.DocumentPaymentHandler(c2, DocSvc(c2), F4Proxy<IGatewayAccountResolver>.Create((_, _) => null),
            NullLogger<Accounting.Services.Payments.Handlers.DocumentPaymentHandler>.Instance, s.Storage);
        var intents = new PaymentIntentService(c2, new IPaymentProvider[] { new ManualSlipPaymentProvider() }, new IPaymentCompletionHandler[] { handler },
            NullLogger<PaymentIntentService>.Instance);
        var confirm = new SlipIntentConfirmService(c2, intents);
        var first = await confirm.ConfirmAsync(s.CompanyId, intent, SlipDay, s.BankId, Guid.NewGuid(), "manual:test", "ตรวจรายการเดินบัญชีแล้ว", null);
        Assert.True(first.Ok, first.Message);
        var again = await confirm.ConfirmAsync(s.CompanyId, intent, SlipDay, s.BankId, Guid.NewGuid(), "manual:test", "กดซ้ำ", null);
        Assert.False(again.Ok);                                                          // กดซ้ำ ⇒ ยอดค้าง 0 แล้ว (Conflict) / ปิดแล้ว — ไม่บันทึกซ้ำ
        Assert.Contains(again.Status, new[] { SlipIntentConfirmService.Status.Conflict, SlipIntentConfirmService.Status.AlreadyClosed });
        using var c3 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await c3.Payments.CountAsync(p => p.CompanyId == s.CompanyId && p.DocumentId == inv.Id));
        _ = auto;
    }

    [Fact]
    public async Task ค่าตั้งปิด_พฤติกรรม260เดิม_รอยืนยัน_ไม่แจ้งซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, autoOn: false);
        var inv = await InvoiceAsync(db, s);
        var (svc, _) = Svc(db, s);
        var r = await svc.HandleForwardedAsync(Fwd(s, Slip(1_070m, "OFF000000000001")));
        Assert.Equal(LineChatSlipRule.Replies.AwaitingSeller, r.Reply);
        using var c = DbTestDatabase.TryCreateContext()!;
        var i = await c.PaymentIntents.AsNoTracking().SingleAsync(x => x.CompanyId == s.CompanyId && x.SourceId == inv.Id);
        Assert.True(PaymentIntentPolicy.IsOpen(i.Status));
        Assert.Equal(SlipAutoRecordRule.Codes.SettingOff, i.AutoRecordCode);
        Assert.Null(i.ConfirmNoticeAt);
        Assert.False(await c.Payments.AnyAsync(p => p.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task ยอดไม่ตรง_และสองใบยอดเท่ากัน_เข้าคิวผู้ขาย_ไม่มีรายการชำระ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        await InvoiceAsync(db, s, 1_000m, ageDays: 4);
        await InvoiceAsync(db, s, 1_000m, ageDays: 2);
        var (svc, _) = Svc(db, s);
        var many = await svc.HandleForwardedAsync(Fwd(s, Slip(1_070m, "MANY0000000001")));
        var none = await svc.HandleForwardedAsync(Fwd(s, Slip(999m, "NONE0000000001")));
        Assert.Equal(LineChatSlipRule.Replies.AwaitingSeller, many.Reply);
        Assert.Equal(LineChatSlipRule.Replies.AwaitingSeller, none.Reply);
        using var c = DbTestDatabase.TryCreateContext()!;
        Assert.False(await c.PaymentIntents.AnyAsync(i => i.CompanyId == s.CompanyId));
        var reasons = await c.LineChatSlips.AsNoTracking().Where(x => x.CompanyId == s.CompanyId).Select(x => x.Reason).ToListAsync();
        Assert.Contains(LineChatSlipRule.Reasons.ManyMatches, reasons);
        Assert.Contains(LineChatSlipRule.Reasons.NoMatch, reasons);
        Assert.All(await c.LineChatSlips.AsNoTracking().Where(x => x.CompanyId == s.CompanyId).ToListAsync(),
            x => Assert.Equal(LineChatSlipRule.Statuses.Unmatched, x.Status));
    }

    [Fact]
    public async Task สลิปซ้ำ_ไฟล์เดิมหรือเลขอ้างอิงQRเดิม_ไม่บันทึกซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var inv = await InvoiceAsync(db, s);
        await InvoiceAsync(db, s, 2_000m);
        var (svc, _) = Svc(db, s);
        var bytes = Png("dup");
        await svc.HandleForwardedAsync(Fwd(s, Slip(1_070m, "DUPREF00000001"), bytes: bytes));
        var sameFile = await svc.HandleForwardedAsync(Fwd(s, Slip(1_070m, "DUPREF00000001"), bytes: bytes));
        Assert.Equal(LineChatSlipRule.Replies.Duplicate, sameFile.Reply);
        // รูปต่าง เลขอ้างอิงธนาคารเดิม (ยอดตรงใบที่สอง) ⇒ ไม่ผูก ไม่บันทึก — คิวผู้ขายเหตุ DuplicateRef
        var sameRef = await svc.HandleForwardedAsync(Fwd(s, Slip(2_140m, "DUPREF00000001")));
        Assert.Equal(LineChatSlipRule.Replies.Duplicate, sameRef.Reply);
        using var c = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await c.Payments.CountAsync(p => p.CompanyId == s.CompanyId));
        Assert.Equal(inv.Id, (await c.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.CompanyId)).DocumentId);
        Assert.True(await c.LineChatSlips.AnyAsync(x => x.CompanyId == s.CompanyId && x.Reason == LineChatSlipRule.Reasons.DuplicateRef));
    }

    [Fact]
    public async Task ผู้ติดต่อของบริษัทอื่น_ไม่ใช่งานของเส้นนี้_ไม่เก็บไฟล์()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var other = await SeedAsync(db);
        await InvoiceAsync(db, other);
        var (svc, _) = Svc(db, s);
        var r = await svc.HandleForwardedAsync(Fwd(s, Slip(1_070m, "TENANT00000001"), contacts: new[] { other.ContactId }));
        Assert.False(r.Handled);
        using var c = DbTestDatabase.TryCreateContext()!;
        Assert.False(await c.LineChatSlips.AnyAsync(x => x.CompanyId == s.CompanyId || x.CompanyId == other.CompanyId));
        Assert.False(await c.PaymentIntents.AnyAsync(i => i.CompanyId == s.CompanyId || i.CompanyId == other.CompanyId));
    }

    [Fact]
    public async Task เตือนซ้ำ_ครบกำหนดส่งครั้งเดียว_ยังไม่ครบไม่ส่ง_มีเพดาน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var inv = await InvoiceAsync(db, s);
        var (svc, auto) = Svc(db, s);
        await svc.HandleForwardedAsync(Fwd(s, Slip(1_070m, "REMIND00000001", payeeMask: "xxx-x-x7777-x")));
        var now = DateTime.UtcNow;
        Assert.Equal(0, await CountMineAsync(auto, s, now));                          // เพิ่งแจ้ง — ยังไม่ถึงรอบเตือน
        await db.PaymentIntents.Where(i => i.CompanyId == s.CompanyId).ExecuteUpdateAsync(u => u.SetProperty(i => i.ConfirmNoticeAt, now.AddDays(-2)));
        Assert.Equal(1, await CountMineAsync(auto, s, now));
        Assert.Equal(0, await CountMineAsync(auto, s, now));                          // รอบเดียวกันไม่ส่งซ้ำ
        Assert.Equal(1, await CountMineAsync(auto, s, now.AddDays(3)));
        Assert.Equal(0, await CountMineAsync(auto, s, now.AddDays(30)));              // เพดาน
        using var c = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(SlipMoneySource.MaxReminders, (await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.SourceId == inv.Id)).ConfirmRemindCount);
    }

    /// <summary>นับเฉพาะรายการของบริษัทในเทสต์นี้ (ฐานร่วม — แถวของเทสต์อื่นอาจถึงรอบพร้อมกัน)</summary>
    private static async Task<int> CountMineAsync(SlipAutoRecordService auto, Seed s, DateTime now)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var before = await c.PaymentIntents.AsNoTracking().Where(i => i.CompanyId == s.CompanyId).SumAsync(i => i.ConfirmRemindCount);
        await auto.RemindPendingAsync(now);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        return await c2.PaymentIntents.AsNoTracking().Where(i => i.CompanyId == s.CompanyId).SumAsync(i => i.ConfirmRemindCount) - before;
    }
}
