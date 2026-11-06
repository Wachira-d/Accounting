using Accounting.Helpers;
using Accounting.Models.DTOs.Bank;
using Accounting.Models.DTOs.DocumentTemplate;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Payments;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 246 QR — เส้นจริงบน PostgreSQL: อัปโหลดรูป QR (ตรวจ → เข้ารหัสใหม่ → ที่เก็บกลางใต้โฟลเดอร์บริษัท · audit chain) ·
/// บริษัทอื่นอ่าน/ลบ/ตั้งค่าไม่ได้ (tenant) · QR พร้อมเพย์บนใบแจ้งหนี้ฝังยอดสุทธิหลังหัก ณ ที่จ่ายจากตัวคิดเดียวกับลิงก์ชำระ ·
/// ปิดในเทมเพลต ⇒ ไม่มี · QR หน้าชำระ: มีลิงก์ที่ใช้ได้ ⇒ มี · เพิกถอนแล้วพิมพ์ใหม่ ⇒ ไม่มี · ตัวเรนเดอร์ HTML จริงวาดกล่อง QR
/// </summary>
[Trait("Category", "Db")]
public class PaymentQrRound246DbTests
{
    private readonly ITestOutputHelper _out;
    public PaymentQrRound246DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static string TempRoot() => Path.Combine(Path.GetTempPath(), "qr246-" + Guid.NewGuid().ToString("N"));
    private static LocalDiskFileStorage MakeStorage(string root)
        => new(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private static readonly SecretProtector Secrets = new("payment-qr-246-db");
    private const string Code = "gw246";

    private static IPaymentProvider Provider() => F4Proxy<IPaymentProvider>.Create((m, _) => m switch
    {
        "get_ProviderCode" => Code,
        "get_Capabilities" => new PaymentCapabilities(new HashSet<PaymentMethodKind> { PaymentMethodKind.PromptPay, PaymentMethodKind.ManualSlip }, false, false, true),
        _ => null,
    });

    private static byte[] Png(int w = 300, int h = 300)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) { c.Clear(SKColors.White); c.DrawRect(20, 20, 100, 100, new SKPaint { Color = SKColors.Black }); }
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed record Seed(Guid CompanyId, BankAccount Bank, Document Invoice, DocumentPayLinkService Links, PaymentQrService Qr, LocalDiskFileStorage Storage);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, string tag, string? promptPay = "0812345678")
    {
        var company = new Company { Name = "บริษัท 246 " + tag, TaxId = "0105556000009", Email = "office@seller246.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 246 จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = "0107537000017", Email = "ap@buyer246.co.th", Address = "1 ถนนสุขุมวิท กรุงเทพฯ",
        };
        db.Contacts.Add(contact);
        var bank = new BankAccount
        {
            CompanyId = company.Id, AccountName = "บริษัท 246 จำกัด", BankName = "กสิกรไทย", AccountNumber = "1234567890",
            PromptPayId = promptPay,
        };
        db.Set<BankAccount>().Add(bank);
        db.PaymentProviderConfigs.Add(new PaymentProviderConfig { CompanyId = company.Id, ProviderCode = Code, IsActive = true, Mode = PaymentProviderMode.Test });
        await db.SaveChangesAsync();
        // ใบจริง: 1,000 + VAT 70 − WHT 30 = ยอดค้าง 1,040 (ยอดค้างเป็นยอดสุทธิแล้ว — DocumentPayLinkPolicy.AmountsFor)
        var inv = new Document
        {
            CompanyId = company.Id, ContactId = contact.Id, DocumentType = DocumentType.Invoice, Status = DocumentStatus.Approved,
            DocumentNumber = "INV246-" + Random.Shared.Next(100000, 999999), DocumentDate = DateTime.UtcNow.Date.AddDays(-1),
            SubTotal = 1_000m, VatAmount = 70m, WithholdingTaxAmount = 30m, TotalAmount = 1_040m, BalanceDue = 1_040m, Currency = "THB",
            BankAccountId = bank.Id,
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        var storage = MakeStorage(TempRoot());
        var links = new DocumentPayLinkService(db, new[] { Provider() }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance, storage);
        var qr = new PaymentQrService(db, storage, new PromptPayService(), links, NullLogger<PaymentQrService>.Instance);
        return new Seed(company.Id, bank, inv, links, qr, storage);
    }

    [Fact]
    public async Task อัปโหลดรูปQR_เก็บใต้โฟลเดอร์บริษัท_เข้ารหัสใหม่_audit_บริษัทอื่นแตะไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var a = await SeedAsync(db, "upload " + Guid.NewGuid().ToString("N")[..6], promptPay: null);
        var b = await SeedAsync(db, "other " + Guid.NewGuid().ToString("N")[..6]);

        var r = await a.Qr.UploadImageAsync(a.CompanyId, a.Bank.Id, Png(), "tester");
        Assert.True(r.HasPaymentQrImage);
        Assert.Equal(PaymentQrPolicy.SourceUpload, r.PaymentQrEffective);
        db.ChangeTracker.Clear();
        var row = await db.Set<BankAccount>().AsNoTracking().SingleAsync(x => x.Id == a.Bank.Id && x.CompanyId == a.CompanyId);
        Assert.True(PaymentQrService.IsOwnLocation(row.PaymentQrImagePath, a.Storage.Locator.AttachmentsRoot, a.CompanyId));
        Assert.DoesNotContain("wwwroot", row.PaymentQrImagePath!);   // ไม่มี URL สาธารณะ
        var stored = (await a.Storage.ReadAllBytesAsync(row.PaymentQrImagePath))!;
        Assert.Equal("image/png", ImageMagicBytes.Detect(stored));
        var audit = await db.AuditLogs.AsNoTracking()
            .Where(x => x.CompanyId == a.CompanyId && x.EntityType == nameof(BankAccount) && x.EntityId == a.Bank.Id.ToString()).ToListAsync();
        Assert.Contains(audit, x => x.NewValues!.Contains("PaymentQrImageUploaded") && x.RowHash != null);

        // ตัวอย่างของบริษัทเจ้าของ = รูปที่อัปโหลด (data URI ของไบต์ที่เข้ารหัสใหม่)
        var pv = await a.Qr.PreviewAsync(a.CompanyId, a.Bank.Id, null, null, null);
        Assert.Equal(PaymentQrPolicy.SourceUpload, pv.Kind);
        Assert.StartsWith("data:image/png;base64,", pv.DataUri);

        // บริษัท B ถือ id บัญชีของ A — อ่าน/ลบ/ตั้งค่า/อัปโหลดทับไม่ได้
        await Assert.ThrowsAsync<KeyNotFoundException>(() => b.Qr.PreviewAsync(b.CompanyId, a.Bank.Id, null, null, null));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => b.Qr.RemoveImageAsync(b.CompanyId, a.Bank.Id, "x"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => b.Qr.UpdateSettingsAsync(b.CompanyId, a.Bank.Id, new UpdateBankPaymentQrRequest("0899999999", null), "x"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => b.Qr.UploadImageAsync(b.CompanyId, a.Bank.Id, Png(), "x"));
        // ค่าในฐานถูกคัดลอกข้ามบริษัท ⇒ ตัวอ่านของ B ไม่อ่านไฟล์ของ A
        Assert.False(PaymentQrService.IsOwnLocation(row.PaymentQrImagePath, b.Storage.Locator.AttachmentsRoot, b.CompanyId));

        // ไฟล์ที่ไม่ใช่รูป ⇒ ปฏิเสธ ไม่แตะแถว
        await Assert.ThrowsAsync<BusinessRuleException>(() => a.Qr.UploadImageAsync(a.CompanyId, a.Bank.Id,
            System.Text.Encoding.UTF8.GetBytes("<html></html>"), "tester"));
        // ลบ ⇒ ไฟล์หาย + ลบซ้ำได้ข้อความ (ไม่สำเร็จเงียบ)
        await a.Qr.RemoveImageAsync(a.CompanyId, a.Bank.Id, "tester");
        Assert.False(await a.Storage.ExistsAsync(row.PaymentQrImagePath));
        await Assert.ThrowsAsync<BusinessRuleException>(() => a.Qr.RemoveImageAsync(a.CompanyId, a.Bank.Id, "tester"));
    }

    [Fact]
    public async Task ตั้งเลขพร้อมเพย์_ผิดรูปถูกปฏิเสธ_ถูกต้องเก็บตัวเลขล้วน_echoกลับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "pp " + Guid.NewGuid().ToString("N")[..6], promptPay: null);
        await Assert.ThrowsAsync<BusinessRuleException>(() => s.Qr.UpdateSettingsAsync(s.CompanyId, s.Bank.Id, new UpdateBankPaymentQrRequest("0105556000007", null), "t"));
        await Assert.ThrowsAsync<BusinessRuleException>(() => s.Qr.UpdateSettingsAsync(s.CompanyId, s.Bank.Id, new UpdateBankPaymentQrRequest(null, "qr"), "t"));
        var r = await s.Qr.UpdateSettingsAsync(s.CompanyId, s.Bank.Id, new UpdateBankPaymentQrRequest("0-1055-56000-00-9", "promptpay"), "t");
        Assert.Equal("0105556000009", r.PromptPayId);
        Assert.Equal(PaymentQrPolicy.SourcePromptPay, r.PaymentQrSource);
        // echo ผ่านรายการบัญชี (หน้าแก้ไข hydrate จากที่นี่)
        db.ChangeTracker.Clear();
        var list = await new BankService(db).GetBankAccountsAsync(s.CompanyId, false);
        var echoed = Assert.Single(list, x => x.Id == s.Bank.Id);
        Assert.Equal("0105556000009", echoed.PromptPayId);
        Assert.Equal(PaymentQrPolicy.SourcePromptPay, echoed.PaymentQrEffective);
        // "" = ล้าง
        var cleared = await s.Qr.UpdateSettingsAsync(s.CompanyId, s.Bank.Id, new UpdateBankPaymentQrRequest("", null), "t");
        Assert.Null(cleared.PromptPayId);
        Assert.Null(cleared.PaymentQrEffective);
    }

    [Fact]
    public async Task ใบแจ้งหนี้_QRพร้อมเพย์ฝังยอดสุทธิ_ปิดในเทมเพลตไม่มี_ใบเสร็จไม่มี()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "inv " + Guid.NewGuid().ToString("N")[..6]);
        var t = new DocumentTemplate { CompanyId = s.CompanyId, DocumentType = DocumentType.Invoice };   // ค่าตั้งต้น = เปิด

        var block = await s.Qr.ResolveForDocumentAsync(s.Invoice, t);
        Assert.NotNull(block?.Bank);
        Assert.Equal(PaymentQrPolicy.Kind.PromptPay, block!.Bank!.Kind);
        Assert.Equal(1_040m, block.Bank.Amount);   // ยอดสุทธิของลิงก์ (WHT ไม่ถูกลบซ้ำ)
        Assert.Null(block.PayLink);                // ยังไม่ออกลิงก์ — พิมพ์ไม่ออกลิงก์ใหม่เอง
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == s.Invoice.Id).Select(d => d.PayLinkTokenHash).SingleAsync());

        t.ShowPaymentQr = false;
        Assert.Null(await s.Qr.ResolveForDocumentAsync(s.Invoice, t));

        // ใบร่าง ⇒ QR ไม่ระบุยอด (ยังไม่มีเลข/ยอดที่เชื่อได้)
        t.ShowPaymentQr = true;
        s.Invoice.Status = DocumentStatus.Draft;
        await db.SaveChangesAsync();
        Assert.Null((await s.Qr.ResolveForDocumentAsync(s.Invoice, t))!.Bank!.Amount);

        // ใบเสร็จ ⇒ ไม่มี QR
        var receipt = new Document { CompanyId = s.CompanyId, ContactId = s.Invoice.ContactId, DocumentType = DocumentType.Receipt, Status = DocumentStatus.Approved,
            DocumentNumber = "RC246-" + Random.Shared.Next(100000, 999999), DocumentDate = DateTime.UtcNow.Date, TotalAmount = 1_040m, BankAccountId = s.Bank.Id };
        db.Documents.Add(receipt);
        await db.SaveChangesAsync();
        Assert.Null(await s.Qr.ResolveForDocumentAsync(receipt, new DocumentTemplate { CompanyId = s.CompanyId, DocumentType = DocumentType.Receipt }));
    }

    [Fact]
    public async Task QRหน้าชำระ_มีลิงก์มีQR_เพิกถอนแล้วพิมพ์ใหม่ไม่มี()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "link " + Guid.NewGuid().ToString("N")[..6]);
        var t = new DocumentTemplate { CompanyId = s.CompanyId, DocumentType = DocumentType.Invoice };
        var link = await s.Links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, new[] { "PromptPay" });
        Assert.True(link.Ok, link.Refusal);

        var withLink = await s.Qr.ResolveForDocumentAsync(s.Invoice, t);
        Assert.NotNull(withLink?.PayLink);
        Assert.NotNull(withLink!.PayLink!.ExpiresAt);

        t.ShowPayLinkQr = false;
        Assert.Null((await s.Qr.ResolveForDocumentAsync(s.Invoice, t))!.PayLink);
        t.ShowPayLinkQr = true;

        await s.Links.RevokeAsync(s.CompanyId, s.Invoice.Id, "tester");
        db.ChangeTracker.Clear();
        var after = await s.Qr.ResolveForDocumentAsync(s.Invoice, t);
        Assert.Null(after?.PayLink);    // เพิกถอน ⇒ ไม่มี QR หน้าชำระบนกระดาษที่พิมพ์ใหม่
        Assert.NotNull(after?.Bank);    // QR บัญชีไม่เกี่ยวกับลิงก์

        // ตัวเรนเดอร์ HTML จริง (resolver ตัวเดียวกับ PDF) วาดกล่อง QR
        var pdf = new PdfGenerationService(db, new DocumentTemplateService(db), storage: s.Storage, paymentQr: s.Qr);
        var html = await pdf.GenerateDocumentHtmlAsync(s.CompanyId, new GeneratePdfRequest(s.Invoice.Id, null, null, null, null));
        Assert.Contains("class='payment-qr'", html);
        Assert.Contains(Accounting.Services.Implementations.Pdf.DocumentLabels.For("th").PayQrAmount(1_040m), html);
    }

    [Fact]
    public async Task ชำระครบไม่มีQR_ชำระบางส่วนฝังยอดที่เหลือ_ใบกำกับอ้างแล้วพิมพ์ซ้ำไม่ได้ยอดเต็ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "paid " + Guid.NewGuid().ToString("N")[..6]);
        var t = new DocumentTemplate { CompanyId = s.CompanyId, DocumentType = DocumentType.Invoice };

        // ชำระบางส่วน: ยอดค้าง 500 ⇒ QR ฝัง 500 (ยอดสุทธิของลิงก์ = ยอดค้าง)
        s.Invoice.Status = DocumentStatus.PartiallyPaid;
        s.Invoice.PaidAmount = 540m;
        s.Invoice.BalanceDue = 500m;
        await db.SaveChangesAsync();
        Assert.Equal(500m, (await s.Qr.ResolveForDocumentAsync(s.Invoice, t))!.Bank!.Amount);

        // ใบกำกับภาษีที่ออกแล้วอ้างใบแจ้งหนี้นี้ (ลูกหนี้ย้ายไปใบกำกับ) ⇒ พิมพ์ซ้ำใบแจ้งหนี้ได้ QR ไม่ระบุยอด + ไม่มี QR หน้าชำระ + คำเตือนบนหน้าเอกสาร
        var ti = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.Invoice.ContactId, DocumentType = DocumentType.TaxInvoice, Status = DocumentStatus.Approved,
            DocumentNumber = "TI246-" + Random.Shared.Next(100000, 999999), DocumentDate = DateTime.UtcNow.Date, RelatedDocumentId = s.Invoice.Id,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_040m, BalanceDue = 1_040m, Currency = "THB",
        };
        db.Documents.Add(ti);
        await db.SaveChangesAsync();
        var link = await s.Links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, new[] { "PromptPay" });
        var reprint = (await s.Qr.ResolveForDocumentAsync(s.Invoice, t))!;
        Assert.Null(reprint.Bank!.Amount);
        Assert.Null(reprint.PayLink);
        Assert.Contains(PaymentQrService.NoteSupersededByTaxInvoice, (await s.Qr.DiagnoseDocumentAsync(s.CompanyId, s.Invoice.Id)).Warnings);
        _out.WriteLine($"ลิงก์ของใบแจ้งหนี้ (รอบ 241 ตัดสินเอง): {link.Refusal ?? "ออกได้"}");

        // ชำระครบ ⇒ ไม่มี QR บัญชีเลย
        ti.Status = DocumentStatus.Voided;
        s.Invoice.Status = DocumentStatus.Paid;
        s.Invoice.PaidAmount = 1_040m;
        s.Invoice.BalanceDue = 0m;
        await db.SaveChangesAsync();
        Assert.Null((await s.Qr.ResolveForDocumentAsync(s.Invoice, t))?.Bank);
    }

    [Fact]
    public async Task ผังเดียวผูกหลายบัญชี_ไม่เดา_หน้าเอกสารบอกเหตุ_บัญชีที่ใบเลือกไม่มีQRบอกเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "gl " + Guid.NewGuid().ToString("N")[..6]);
        var gl = new ChartOfAccount { CompanyId = s.CompanyId, AccountCode = "111299", AccountName = "เงินฝาก 246", AccountType = AccountType.Asset, Level = 4 };
        db.ChartOfAccounts.Add(gl);
        await db.SaveChangesAsync();
        var bank2 = new BankAccount { CompanyId = s.CompanyId, AccountName = "บัญชีสอง", BankName = "กรุงเทพ", AccountNumber = "9999999999", PromptPayId = "0899999999", LinkedAccountId = gl.Id };
        db.Set<BankAccount>().Add(bank2);
        s.Bank.LinkedAccountId = gl.Id;
        s.Invoice.PaymentAccountId = gl.Id;
        await db.SaveChangesAsync();
        var t = new DocumentTemplate { CompanyId = s.CompanyId, DocumentType = DocumentType.Invoice };
        Assert.Null((await s.Qr.ResolveForDocumentAsync(s.Invoice, t))?.Bank);
        Assert.Equal(PaymentQrPolicy.ProblemGlAmbiguous, (await s.Qr.DiagnoseDocumentAsync(s.CompanyId, s.Invoice.Id)).Problem);

        // แหล่งเงินเลิกผูก ⇒ บัญชีของใบ (bank เดิม) · บัญชีนั้นไม่มี QR ⇒ ไม่สลับไป bank2 + เหตุ
        s.Invoice.PaymentAccountId = null;
        s.Bank.PromptPayId = null;
        await db.SaveChangesAsync();
        Assert.Null((await s.Qr.ResolveForDocumentAsync(s.Invoice, t))?.Bank);
        Assert.Equal(PaymentQrPolicy.ProblemChosenNoQr, (await s.Qr.DiagnoseDocumentAsync(s.CompanyId, s.Invoice.Id)).Problem);
    }

    [Fact]
    public async Task บัญชีที่ตั้งQR_แก้ข้อความใต้QRต้องมีสิทธิ์และลงaudit_บัญชีไม่มีQRแก้ได้ตามเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "caption " + Guid.NewGuid().ToString("N")[..6]);
        var noPerm = F4Proxy<Accounting.Services.Interfaces.IPermissionService>.Create((m, _) => m == "HasPermissionAsync" ? (object?)false : null);
        var yesPerm = F4Proxy<Accounting.Services.Interfaces.IPermissionService>.Create((m, _) => m == "HasPermissionAsync" ? (object?)true : null);
        var change = new UpdateBankAccountRequest(null, null, null, null, AccountNumber: "5555555555");

        var denied = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new BankService(db, permissions: noPerm, storage: s.Storage).UpdateBankAccountAsync(s.CompanyId, s.Bank.Id, change));
        Assert.Equal(403, denied.StatusCode);
        db.ChangeTracker.Clear();
        Assert.Equal("1234567890", (await db.Set<BankAccount>().AsNoTracking().SingleAsync(b => b.Id == s.Bank.Id && b.CompanyId == s.CompanyId)).AccountNumber);
        // ไม่ได้แตะข้อความใต้ QR (แก้สาขา) ⇒ ไม่ต้องสิทธิ์พิเศษ
        await new BankService(db, permissions: noPerm, storage: s.Storage).UpdateBankAccountAsync(s.CompanyId, s.Bank.Id,
            new UpdateBankAccountRequest(null, "สาขาสีลม", null, null));

        // ทิศตรงข้าม: บัญชีที่ไม่มี QR แก้เลขได้ตามสิทธิ์เดิม
        var plain = new BankAccount { CompanyId = s.CompanyId, AccountName = "ไม่มี QR", BankName = "กรุงไทย", AccountNumber = "7777777777" };
        db.Set<BankAccount>().Add(plain);
        await db.SaveChangesAsync();
        var r = await new BankService(db, permissions: noPerm, storage: s.Storage).UpdateBankAccountAsync(s.CompanyId, plain.Id, change);
        Assert.Equal("5555555555", r.AccountNumber);

        // มีสิทธิ์ ⇒ แก้ได้ + audit chain
        db.ChangeTracker.Clear();
        await new BankService(db, permissions: yesPerm, storage: s.Storage).UpdateBankAccountAsync(s.CompanyId, s.Bank.Id, change);
        Assert.True(await db.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == s.CompanyId && a.EntityId == s.Bank.Id.ToString()
            && a.NewValues!.Contains("PaymentQrCaptionChanged") && a.RowHash != null));
    }

    [Fact]
    public async Task หน้าชำระสาธารณะ_ใช้บัญชีของใบก่อน_QRพร้อมเพย์ฝังยอดสุทธิ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "public " + Guid.NewGuid().ToString("N")[..6]);
        // ช่องทางโอนของลิงก์ต้องเปิด (CompanyFacts.bank = มีบัญชีรับโอนของเว็บ — ตัวตัดสินรอบ 241 ไม่แตะ)
        var tag = "w246-" + Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = s.CompanyId, Name = "เว็บ 246", Slug = tag, Subdomain = tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        db.Set<SitePaymentGateway>().Add(new SitePaymentGateway
        {
            CompanyId = s.CompanyId, SiteId = site.Id, GatewayType = PaymentGatewayType.BankTransfer, Name = "โอน", BankName = "ธนาคารเว็บ",
            BankAccountNumber = "0000000000", BankAccountName = "เว็บ", IsActive = true,
        });
        await db.SaveChangesAsync();
        var link = await s.Links.EnsureLinkAsync(s.CompanyId, s.Invoice.Id, new[] { "PromptPay", DocumentPayLinkPolicy.BankTransferCode });
        Assert.True(link.Ok, link.Refusal);
        var token = System.Web.HttpUtility.ParseQueryString(new Uri(link.Url!).Query)["t"]!;
        var view = (await s.Links.PublicViewAsync(s.CompanyId, token))!;
        Assert.NotNull(view.Transfer);
        Assert.Equal("1234567890", view.Transfer!.AccountNumber);          // บัญชีของใบ ไม่ใช่บัญชีของเว็บ
        Assert.StartsWith("data:image/png;base64,", view.Transfer.PromptPayQrDataUri);
    }
    // ═════════════ รอบ 250 WP-03 (A G-01 · D §9.1): QR พอร์ทัลลูกค้าสร้างที่เซิร์ฟเวอร์ — ไม่ส่งเลขพร้อมเพย์/ยอดไปบริการภายนอก ═════════════

    private static async Task<(Guid SiteId, PortalService Portal)> PortalWithGatewayAsync(Accounting.Data.AccountingDbContext db, Seed s, string? promptPayId, string? qrUrl)
    {
        var tag = "p250-" + Guid.NewGuid().ToString("N")[..8];
        var site = new Site { CompanyId = s.CompanyId, Name = "เว็บ 250", Slug = tag, Subdomain = tag, Status = SiteStatus.Published };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        db.Set<SitePaymentGateway>().Add(new SitePaymentGateway
        {
            CompanyId = s.CompanyId, SiteId = site.Id, GatewayType = PaymentGatewayType.PromptPay, Name = "พร้อมเพย์", BankName = "ธนาคารเว็บ",
            BankAccountNumber = "0000000000", BankAccountName = "เว็บ", PromptPayId = promptPayId, PromptPayQrUrl = qrUrl, IsActive = true,
        });
        await db.SaveChangesAsync();
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Portal:SigningKey"] = "portal-250-test-signing-key-0123456789" }).Build();
        return (site.Id, new PortalService(db, cfg));
    }

    [Fact]
    public async Task พอร์ทัล_paymentoptions_คืนdataUri_ไม่มีURLภายนอก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "portal " + Guid.NewGuid().ToString("N")[..6]);
        var (_, portal) = await PortalWithGatewayAsync(db, s, "0812345678", "javascript:alert(1)");
        var o = await portal.GetDocumentPaymentOptionsAsync(s.CompanyId, s.Invoice.ContactId, s.Invoice.Id);
        Assert.StartsWith("data:image/png;base64,", o.PromptPayQrDataUri);
        Assert.Null(o.PromptPayQrUrl);   // รูปที่ไม่ใช่ URL ภายใน/https ไม่ถูกส่งต่อ
        Assert.DoesNotContain("qrserver", o.PromptPayQrDataUri!, StringComparison.OrdinalIgnoreCase);
        // ใบของลูกค้าคนอื่น (ผู้ติดต่อในโทเค็นไม่ตรง) ⇒ ไม่พบ — ยอดค้างเป็นข้อมูลของลูกค้ารายนั้น
        await Assert.ThrowsAsync<KeyNotFoundException>(() => portal.GetDocumentPaymentOptionsAsync(s.CompanyId, Guid.NewGuid(), s.Invoice.Id));
    }

    [Fact]
    public async Task พอร์ทัล_paymentoptions_ไม่มีพร้อมเพย์_ไม่มีQR_ไม่มีURL()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "portal-none " + Guid.NewGuid().ToString("N")[..6]);
        var (_, portal) = await PortalWithGatewayAsync(db, s, null, null);
        var o = await portal.GetDocumentPaymentOptionsAsync(s.CompanyId, s.Invoice.ContactId, s.Invoice.Id);
        Assert.Null(o.PromptPayQrDataUri);
        Assert.Null(o.PromptPayQrUrl);
        Assert.True(o.HasPaymentMethod);   // ยังโอนเข้าบัญชีได้ (เลขบัญชีของเว็บ)
    }
}
