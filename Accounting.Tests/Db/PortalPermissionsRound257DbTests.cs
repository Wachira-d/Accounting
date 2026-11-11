using System.Text.Encodings.Web;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Filters;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Portal;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
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
/// รอบ 257 ชุด 1 — พอร์ทัลลูกค้า (D-H2 · D-H3 · D-H1):
/// <list type="bullet">
/// <item>D-H3 ธงสิทธิ์ทุกตัวมีผู้อ่าน: ติ๊กออกทีละธง ⇒ เส้นของธงนั้น 403 (เอกสาร · ใบเดี่ยว · PDF · ใบแจ้งยอด · ประวัติชำระ · ตัวเลือกชำระ · แนบสลิป · ส่งออก)
/// และเส้นของธงอื่นยังใช้ได้ (ทิศตรงข้าม) · ปุ่มชำระปิดตามธงชำระ</item>
/// <item>D-H2 ผู้ติดต่อที่เป็นทั้งลูกค้าและผู้ขาย: ใบซื้อไม่อยู่ในรายการ/ใบแจ้งยอด/ส่งออก · เปิดใบเดี่ยว/PDF/ชำระ/สลิปของใบซื้อ = ไม่พบ · ใบแจ้งหนี้ยังเห็น</item>
/// <item>D-H1 ให้/แก้/ปิดสิทธิ์ลง audit chain พร้อมผู้ทำ</item>
/// </list>
/// <para>รอบ 257 ปิดท้าย: ติด <c>Category=Db</c> — เดิมคลาสนี้ไม่มี trait ⇒ job db-test (กรอง Category=Db) ไม่เคยรัน และ job เทสต์ปกติไม่มีฐาน ⇒ ทุกเทสต์ข้ามเงียบ</para>
/// </summary>
[Trait("Category", "Db")]
public class PortalPermissionsRound257DbTests
{
    private readonly ITestOutputHelper _out;
    public PortalPermissionsRound257DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Portal:SigningKey"] = "r257-portal-signing-key-for-db-tests-0123456789",
        ["App:BaseUrl"] = "https://acc.r257.test",
    }).Build();

    private sealed class SchemeOptions : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed record Seeded(Company Shop, Contact Contact, PortalAccess Access, PortalLoginResponse Session, Document Invoice, Document Purchase);

    private static async Task<Seeded> SeedAsync(AccountingDbContext db, PortalService svc)
    {
        var shop = new Company { Name = "ร้านพอร์ทัล 257 " + Guid.NewGuid().ToString("N")[..6], TaxId = "09" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Random.Shared.Next(10)))) };
        db.Companies.Add(shop);
        await db.SaveChangesAsync();
        // ทั้งลูกค้าและผู้ขาย (D-H2)
        var contact = new Contact { CompanyId = shop.Id, Name = "ลูกค้า+ผู้ขาย 257", IsCustomer = true, IsSupplier = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        Document Doc(DocumentType t, string prefix)
        {
            var d = new Document
            {
                CompanyId = shop.Id, ContactId = contact.Id, DocumentType = t, DocumentNumber = prefix + Guid.NewGuid().ToString("N")[..8],
                Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m,
                BalanceDue = 1_070m, Currency = "THB",
            };
            d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "รายการ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatAmount = 70m });
            return d;
        }
        var inv = Doc(DocumentType.Invoice, "INV-");
        var pi = Doc(DocumentType.PurchaseInvoice, "PI-");
        db.Documents.AddRange(inv, pi);
        var email = $"portal257-{Guid.NewGuid():N}@cust.test";
        var access = new PortalAccess
        {
            CompanyId = shop.Id, ContactId = contact.Id, Email = email, IsActive = true,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Portal#257-pass", workFactor: 4),
            CanViewInvoices = true, CanViewStatements = true, CanDownloadPdf = true, CanMakePayment = true,
        };
        db.PortalAccesses.Add(access);
        await db.SaveChangesAsync();
        var session = await svc.LoginAsync(new PortalLoginRequest(email, "Portal#257-pass", shop.Id));
        return new Seeded(shop, contact, access, session, inv, pi);
    }

    private static async Task<PortalPublicController> ControllerAsync(IPortalService portal, string token, Guid companyId)
    {
        var services = new ServiceCollection().AddSingleton(portal).BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Headers.Authorization = "Bearer " + token;
        ctx.Request.RouteValues["companyId"] = companyId.ToString();
        var handler = new PortalAuthenticationHandler(new SchemeOptions(), NullLoggerFactory.Instance, UrlEncoder.Default);
        await handler.InitializeAsync(new AuthenticationScheme(PortalTokenPolicy.Scheme, null, typeof(PortalAuthenticationHandler)), ctx);
        var auth = await handler.AuthenticateAsync();
        Assert.True(auth.Succeeded);
        return new PortalPublicController(portal)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = auth.Principal!, RequestServices = services } },
        };
    }

    private static int? Status(IActionResult? r) => (r as ObjectResult)?.StatusCode ?? (r as StatusCodeResult)?.StatusCode ?? (r is FileResult ? 200 : (int?)null);
    private static int? Status<T>(ActionResult<T> r) => Status(r.Result);

    /// <summary>ผ่านด่านธงแล้วไปล้มชั้นถัดไป (ไม่มีตัวสร้าง PDF/ลิงก์ชำระ/ที่เก็บไฟล์ในเทสต์) = "ผ่านด่าน" (-1) — เทสต์นี้ตรวจเฉพาะด่านธง</summary>
    private const int PassedGate = -1;
    private static async Task<int?> Gate(Func<Task<int?>> call)
    {
        try { return await call(); }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException) { return PassedGate; }
    }

    private static IFormFile Slip()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "slip.png") { Headers = new HeaderDictionary(), ContentType = "image/png" };
    }

    private static async Task SetFlagAsync(Guid accessId, string flag)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var q = c.PortalAccesses.IgnoreQueryFilters().Where(a => a.Id == accessId);
        switch (flag)
        {
            case "inv": await q.ExecuteUpdateAsync(x => x.SetProperty(a => a.CanViewInvoices, false)); break;
            case "st": await q.ExecuteUpdateAsync(x => x.SetProperty(a => a.CanViewStatements, false)); break;
            case "pdf": await q.ExecuteUpdateAsync(x => x.SetProperty(a => a.CanDownloadPdf, false)); break;
            case "pay": await q.ExecuteUpdateAsync(x => x.SetProperty(a => a.CanMakePayment, false)); break;
        }
    }

    [Fact]
    public async Task R257_DH2_ผู้ติดต่อเป็นทั้งลูกค้าและผู้ขาย_ไม่เห็นใบซื้อ_ใบแจ้งหนี้ยังเห็น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var svc = new PortalService(db, Config());
        var s = await SeedAsync(db, svc);

        var list = await svc.GetMyDocumentsAsync(s.Shop.Id, s.Contact.Id);
        Assert.Contains(list, d => d.Id == s.Invoice.Id);
        Assert.DoesNotContain(list, d => d.Id == s.Purchase.Id);
        // ใบเดี่ยว/PDF ของใบซื้อ = ไม่พบ · ใบแจ้งหนี้ยังเปิดได้ (ทิศตรงข้าม)
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.GetDocumentAsync(s.Shop.Id, s.Contact.Id, s.Purchase.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DownloadDocumentPdfAsync(s.Shop.Id, s.Contact.Id, s.Purchase.Id));
        Assert.Equal(s.Invoice.Id, (await svc.GetDocumentAsync(s.Shop.Id, s.Contact.Id, s.Invoice.Id)).Id);
        // ชำระ/แนบสลิปของใบซื้อ = ไม่พบ (ก่อนถึงตัวรับสลิป)
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.GetDocumentPaymentOptionsAsync(s.Shop.Id, s.Contact.Id, s.Purchase.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.UploadDocumentSlipAsync(s.Shop.Id, s.Contact.Id, s.Purchase.Id, Slip(), 100m));
        // ใบแจ้งยอด: ยอดค้างยกมาไม่รวมใบซื้อ · บรรทัดเฉพาะใบแจ้งหนี้
        var today = DateTime.UtcNow.Date;
        var st = await svc.GetMyStatementAsync(s.Shop.Id, s.Contact.Id, today.AddDays(-1), today.AddDays(1));
        Assert.DoesNotContain(st.Lines, l => l.DocumentNumber == s.Purchase.DocumentNumber);
        Assert.Contains(st.Lines, l => l.DocumentNumber == s.Invoice.DocumentNumber);
    }

    [Fact]
    public async Task R257_M2_ยอดยกมา_นับเฉพาะใบแจ้งหนี้_ไม่นับใบวางบิลของใบเดียวกันและใบเสนอราคา()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var svc = new PortalService(db, Config());
        var s = await SeedAsync(db, svc);
        var old = DateTime.UtcNow.Date.AddDays(-40);
        // ย้ายใบแจ้งหนี้ + ใบซื้อของ seed ไปก่อนช่วง · เพิ่มใบวางบิลที่ครอบใบแจ้งหนี้ใบเดียวกัน + ใบเสนอราคา (ยอดค้างเท่ากัน)
        await db.Documents.Where(d => d.Id == s.Invoice.Id || d.Id == s.Purchase.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.DocumentDate, old));
        Document Extra(DocumentType t, string prefix) => new()
        {
            CompanyId = s.Shop.Id, ContactId = s.Contact.Id, DocumentType = t, DocumentNumber = prefix + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = old, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m,
            BalanceDue = 1_070m, Currency = "THB", RelatedDocumentId = t == DocumentType.BillingNote ? s.Invoice.Id : null,
        };
        db.Documents.AddRange(Extra(DocumentType.BillingNote, "BN-"), Extra(DocumentType.Quotation, "QT-"));
        await db.SaveChangesAsync();

        using var c2 = DbTestDatabase.TryCreateContext()!;
        var today = DateTime.UtcNow.Date;
        var st = await new PortalService(c2, Config()).GetMyStatementAsync(s.Shop.Id, s.Contact.Id, today.AddDays(-7), today.AddDays(1));
        Assert.Equal(1_070m, st.OpeningBalance);   // เดิม 4,280 (ใบแจ้งหนี้ + ใบวางบิล + ใบเสนอราคา + ใบซื้อ)
        Assert.Equal(1_070m, st.ClosingBalance);
        Assert.Empty(st.Lines);
    }

    [Fact]
    public async Task R257_L5_สร้างสิทธิ์โดยไม่เปิดธงใดเลย_400()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var svc = new PortalService(db, Config());
        var s = await SeedAsync(db, svc);
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.CreateAccessAsync(s.Shop.Id,
            new CreatePortalAccessRequest(s.Contact.Id, $"none-{Guid.NewGuid():N}@cust.test", null, null, false, false, false, false), Guid.NewGuid()));
        Assert.Equal(400, ex.StatusCode);
        Assert.False(await db.PortalAccesses.IgnoreQueryFilters().AnyAsync(a => a.CompanyId == s.Shop.Id && a.Email.StartsWith("none-")));
    }

    [Fact]
    public async Task R257_DH3_ทุกธงครบ_ทุกเส้นผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var svc = new PortalService(db, Config());
        var s = await SeedAsync(db, svc);
        var ctl = await ControllerAsync(svc, s.Session.AccessToken, s.Shop.Id);
        var today = DateTime.UtcNow.Date;
        Assert.IsType<OkObjectResult>((await ctl.GetDocuments(s.Shop.Id, null)).Result);
        Assert.IsType<OkObjectResult>((await ctl.GetDocument(s.Shop.Id, s.Invoice.Id)).Result);
        Assert.IsType<OkObjectResult>((await ctl.GetStatement(s.Shop.Id, today.AddDays(-1), today.AddDays(1))).Result);
        Assert.IsType<OkObjectResult>((await ctl.GetPayments(s.Shop.Id)).Result);
        Assert.IsType<FileContentResult>(await ctl.ExportAccountant(s.Shop.Id, today.AddDays(-30), today, default));
    }

    [Theory]
    [InlineData("inv")]
    [InlineData("st")]
    [InlineData("pdf")]
    [InlineData("pay")]
    public async Task R257_DH3_ติ๊กออกทีละธง_เส้นของธงนั้น403_เส้นอื่นยังใช้ได้(string flag)
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, new PortalService(db, Config()));
        await SetFlagAsync(s.Access.Id, flag);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var svc = new PortalService(c2, Config());
        var ctl = await ControllerAsync(svc, s.Session.AccessToken, s.Shop.Id);
        var today = DateTime.UtcNow.Date;
        var (from, to) = (today.AddDays(-1), today.AddDays(1));

        var docs = await Gate(async () => Status(await ctl.GetDocuments(s.Shop.Id, null)));
        var doc = await Gate(async () => Status(await ctl.GetDocument(s.Shop.Id, s.Invoice.Id)));
        var pdf = await Gate(async () => Status(await ctl.DownloadPdf(s.Shop.Id, s.Invoice.Id)));
        var stmt = await Gate(async () => Status(await ctl.GetStatement(s.Shop.Id, from, to)));
        var pays = await Gate(async () => Status(await ctl.GetPayments(s.Shop.Id)));
        var payOpt = await Gate(async () => Status(await ctl.GetDocumentPaymentOptions(s.Shop.Id, s.Invoice.Id, default)));
        var slip = await Gate(async () => Status(await ctl.UploadDocumentSlip(s.Shop.Id, s.Invoice.Id, Slip(), 100m)));
        var export = await Gate(async () => Status(await ctl.ExportAccountant(s.Shop.Id, today.AddDays(-30), today, default)));
        // ทิศตรงข้ามของทุกเส้นที่ไม่ใช่ธงนี้: ไม่ใช่ 403 (200 หรือผ่านด่านไปล้มชั้นถัดไป)
        void NotDenied(int? code) => Assert.NotEqual(403, code);

        switch (flag)
        {
            case "inv":
                Assert.Equal(403, docs); Assert.Equal(403, doc); Assert.Equal(403, pdf); Assert.Equal(403, payOpt); Assert.Equal(403, slip); Assert.Equal(403, export);
                Assert.Equal(200, stmt); Assert.Equal(200, pays);   // ทิศตรงข้าม: ใบแจ้งยอดไม่ใช่ธงนี้
                break;
            case "st":
                Assert.Equal(403, stmt); Assert.Equal(403, pays);
                Assert.Equal(200, docs); Assert.Equal(200, doc); Assert.Equal(200, export);
                NotDenied(pdf); NotDenied(payOpt); NotDenied(slip);
                break;
            case "pdf":
                Assert.Equal(403, pdf);
                Assert.Equal(200, docs); Assert.Equal(200, doc); Assert.Equal(200, stmt); Assert.Equal(200, export);
                NotDenied(payOpt); NotDenied(slip);
                break;
            case "pay":
                Assert.Equal(403, payOpt); Assert.Equal(403, slip);
                Assert.Equal(200, docs); Assert.Equal(200, doc); Assert.Equal(200, stmt);
                NotDenied(pdf);
                // ปุ่มชำระปิดตามธง (ไม่โชว์ปุ่มที่กดแล้วได้ 403)
                var list = ((await ctl.GetDocuments(s.Shop.Id, null)).Result as OkObjectResult)!.Value as ApiResponse<List<PortalDocumentResponse>>;
                Assert.All(list!.Data!, d => Assert.False(d.CanPay));
                break;
        }
    }

    [Fact]
    public async Task R257_DH1_ให้แก้ปิดสิทธิ์_ลงaudit_พร้อมผู้ทำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var svc = new PortalService(db, Config());
        var s = await SeedAsync(db, svc);
        var actor = Guid.NewGuid();
        // D-M3: ไม่ตั้งรหัสผ่าน = ลิงก์วิเศษอย่างเดียว (สร้างได้)
        var created = await svc.CreateAccessAsync(s.Shop.Id,
            new CreatePortalAccessRequest(s.Contact.Id, $"magic-{Guid.NewGuid():N}@cust.test", null, null, true, false, true, false), actor);
        Assert.True(created.CanViewInvoices);
        Assert.False(created.CanViewStatements);   // ธงที่ส่งถูกเก็บจริง (เดิมหน้าเว็บส่งรูปที่ไม่ bind ⇒ false ทั้งหมด)
        await svc.UpdateAccessAsync(s.Shop.Id, created.Id, new UpdatePortalAccessRequest(null, true, null, null, null), actor);
        await svc.DeactivateAccessAsync(s.Shop.Id, created.Id, actor);
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == s.Shop.Id && a.EntityType == "PortalAccess" && a.EntityId == created.Id.ToString())
            .ToListAsync();
        // รอบ 257 ปิดท้าย (รันบน PostgreSQL จริงครั้งแรก): DbContext เขียนแถว audit อัตโนมัติของการเปลี่ยนแถว (ไม่มีผู้ทำ) คู่กับแถวของ D-H1 ⇒
        // ข้อที่ต้องจริง = ทุกการให้/แก้/ปิดมีแถวที่ระบุผู้ทำ (ไม่ใช่ "มี 3 แถวพอดี")
        var withActor = rows.Where(r => r.UserId == actor).ToList();
        Assert.Equal(3, withActor.Count);
        Assert.Contains(withActor, r => r.Action == AuditAction.Create);
        Assert.Equal(2, withActor.Count(r => r.Action == AuditAction.Update));
    }

    // ═══════════ รอบ 257 ปิดท้าย LOW: ใบลดหนี้ที่อ้างใบต้นทางไม่ถูกหักซ้ำในใบแจ้งยอด ═══════════

    [Fact]
    public async Task Z_LOW_ใบลดหนี้ที่อ้างใบแจ้งหนี้_ไม่หักซ้ำในยอดยกมา_ทิศตรงข้ามใบลดหนี้ลอย_ยังหัก_และใบลดหนี้ในงวดของใบก่อนงวดไม่ลดซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var svc = new PortalService(db, Config());
        var s = await SeedAsync(db, svc);
        var old = DateTime.UtcNow.Date.AddDays(-40);
        var today = DateTime.UtcNow.Date;
        // ใบแจ้งหนี้ 1,070 ก่อนงวด · ใบลดหนี้ 214 อ้างใบแจ้งหนี้ (อนุมัติแล้ว ⇒ ยอดค้างของใบแจ้งหนี้เหลือ 856 — สิ่งที่ ApplySourceDocumentAdjustmentsAsync ทำ)
        await db.Documents.Where(d => d.Id == s.Invoice.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.DocumentDate, old).SetProperty(d => d.PaidAmount, 214m).SetProperty(d => d.BalanceDue, 856m)
                .SetProperty(d => d.Status, DocumentStatus.PartiallyPaid));
        await db.Documents.Where(d => d.Id == s.Purchase.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.DocumentDate, old));
        Document Cn(string prefix, decimal total, DateTime date, Guid? source) => new()
        {
            CompanyId = s.Shop.Id, ContactId = s.Contact.Id, DocumentType = DocumentType.CreditNote, DocumentNumber = prefix + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = date, SubTotal = Math.Round(total / 1.07m, 2, MidpointRounding.AwayFromZero),
            VatAmount = total - Math.Round(total / 1.07m, 2, MidpointRounding.AwayFromZero), TotalAmount = total, BalanceDue = total, Currency = "THB",
            RelatedDocumentId = source, CnDnPurchaseSideOverride = source == null ? false : null,
        };
        var linked = Cn("CN-L-", 214m, old.AddDays(1), s.Invoice.Id);
        db.Documents.Add(linked);
        await db.SaveChangesAsync();

        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var st = await new PortalService(c, Config()).GetMyStatementAsync(s.Shop.Id, s.Contact.Id, today.AddDays(-7), today.AddDays(1));
            Assert.Equal(856m, st.OpeningBalance);   // เดิม 642 (856 − 214 ซ้ำ)
            Assert.Equal(856m, st.ClosingBalance);
        }

        // ทิศตรงข้าม: ใบลดหนี้ที่ไม่อ้างใบต้นทาง (เครดิตลอย — ไม่เคยลดยอดค้างของใบใด) ยังหักในยอดยกมา
        var floating = Cn("CN-F-", 107m, old.AddDays(2), null);
        db.Documents.Add(floating);
        // ใบลดหนี้ในงวดที่อ้างใบแจ้งหนี้ก่อนงวด (ยอดค้างของใบแจ้งหนี้ลดไปแล้ว ⇒ อยู่ในยอดยกมา) — ขึ้นบรรทัดให้เห็น แต่ไม่ลดยอดซ้ำ
        var inPeriod = Cn("CN-P-", 107m, today, s.Invoice.Id);
        db.Documents.Add(inPeriod);
        await db.SaveChangesAsync();
        await db.Documents.Where(d => d.Id == s.Invoice.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.PaidAmount, 321m).SetProperty(d => d.BalanceDue, 749m));

        using var c2 = DbTestDatabase.TryCreateContext()!;
        var st2 = await new PortalService(c2, Config()).GetMyStatementAsync(s.Shop.Id, s.Contact.Id, today.AddDays(-7), today.AddDays(1));
        Assert.Equal(749m - 107m, st2.OpeningBalance);            // ใบแจ้งหนี้ 749 − เครดิตลอย 107
        var line = Assert.Single(st2.Lines, l => l.DocumentNumber == inPeriod.DocumentNumber);
        Assert.Equal(0m, line.Amount);
        Assert.Equal(642m, st2.ClosingBalance);                    // ไม่ใช่ 535 (หักใบลดหนี้ในงวดซ้ำ)
    }
}
