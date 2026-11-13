using System.Net;
using Accounting.Controllers;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 267 แพ็ก P1 — หน้าเอกสารสำหรับลูกค้า บน PostgreSQL จริง (DOCUMENT_FLOW §2.12n · คำตัดสิน 267-1-1..267-1-3):
/// ลิงก์ชำระเดิมเปิดเป็นหน้าเอกสาร (ช่องจ่ายชื่อเดิม) · ลิงก์เดียวต่อใบสำหรับใบที่จ่ายไม่ได้ (ใบเสนอราคา) + เพิกถอน/ออกใหม่ ·
/// หน้าตอบรับเดิมรับ token ของหน้าเอกสาร (ช่วงตอบรับ/เพิกถอนตัดสินสด) · ลิงก์หมดอายุ ⇒ ขอลิงก์ใหม่ครั้งเดียวต่อวัน ·
/// ใบที่เกี่ยวข้อง = ชุดเดียวกับที่ดาวน์โหลดได้ (นอกชุด/ผู้ติดต่ออื่น = ไม่ได้) · migration token ดิบ ⇒ hash (เขียนใน PostgreSQL ค้นด้วย C#)
/// </summary>
[Trait("Category", "Db")]
public class CustomerDocPageRound267DbTests
{
    private readonly ITestOutputHelper _out;
    public CustomerDocPageRound267DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("customer-doc-page-267-db");

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["App:BaseUrl"] = "https://app.nextacc.test" }).Build();

    private static IPaymentProvider Provider(string code) => F4Proxy<IPaymentProvider>.Create((m, _) => m switch
    {
        "get_ProviderCode" => code,
        "get_Capabilities" => new PaymentCapabilities(new HashSet<PaymentMethodKind> { PaymentMethodKind.PromptPay, PaymentMethodKind.ManualSlip }, false, false, true),
        _ => null,
    });

    private sealed record Seed(Guid CompanyId, Guid ContactId, DocumentPayLinkService Links);

    private static async Task<Seed> SeedAsync(Accounting.Data.AccountingDbContext db, string tag, bool gateway = true)
    {
        var company = new Company { Name = "บริษัท 267 " + tag, TaxId = "0105556000007", Email = "office@seller267.co.th" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 267 จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = "0107537000017", Email = "ap@buyer267.co.th",
        };
        db.Contacts.Add(contact);
        const string code = "gw267";
        if (gateway)
            db.PaymentProviderConfigs.Add(new PaymentProviderConfig { CompanyId = company.Id, ProviderCode = code, IsActive = true, Mode = PaymentProviderMode.Test });
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id,
            new DocumentPayLinkService(db, new[] { Provider(code) }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance));
    }

    private static async Task<Document> AddDocAsync(Accounting.Data.AccountingDbContext db, Seed s, DocumentType type, string prefix,
        DocumentStatus status = DocumentStatus.Approved, decimal balance = 1_070m, Guid? contactId = null, DateTime? due = null, Action<Document>? tweak = null)
    {
        var doc = new Document
        {
            CompanyId = s.CompanyId, ContactId = contactId ?? s.ContactId, DocumentType = type,
            DocumentNumber = prefix + "267-" + Random.Shared.Next(100000, 999999), Status = status,
            DocumentDate = DateTime.UtcNow.Date.AddDays(-3), DueDate = due,
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, PaidAmount = 1_070m - balance, BalanceDue = balance, Currency = "THB",
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการติดตั้ง", Quantity = 2, Unit = "งาน", UnitPrice = 500m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        tweak?.Invoke(doc);
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    /// <summary>ผู้ขาย "ส่งให้ลูกค้าตอบรับ" (สิ่งที่ SignRequestService.MarkIssued ทำ) — ฝ่ายค้าน 267 M1: ลิงก์ดูอย่างเดียวยังตอบรับไม่ได้</summary>
    private static Task OpenForResponseAsync(Accounting.Data.AccountingDbContext db, Guid documentId, DateTime? at = null)
        => db.Documents.Where(d => d.Id == documentId).ExecuteUpdateAsync(x => x.SetProperty(d => d.SignLinkIssuedAt, (DateTime?)(at ?? DateTime.UtcNow)));

    /// <summary>hash เนื้อหาที่เส้นดูของหน้ายอมรับส่งให้หน้าเว็บ (ช่อง <c>contentHash</c>)</summary>
    private static string SeenContentHash(IActionResult viewResult)
    {
        var ok = Assert.IsType<OkObjectResult>(viewResult);
        var data = ok.Value!.GetType().GetProperty("Data")!.GetValue(ok.Value)!;
        return (string)data.GetType().GetProperty("contentHash")!.GetValue(data)!;
    }

    private static string TokenOf(string url) => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["t"]!;

    private static PublicQuotationController LegacyPage(Accounting.Data.AccountingDbContext db)
    {
        var root = Path.Combine(Path.GetTempPath(), "cdp267-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        return new PublicQuotationController(db, NullLogger<PublicQuotationController>.Instance, leads: null, pdf: null, storage: storage)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    Connection = { RemoteIpAddress = IPAddress.Parse("203.0.113.9") },
                    Request = { Headers = { UserAgent = "Mozilla/5.0 (test)" } },
                },
            },
        };
    }

    /// <summary>รอบ 267 P2: ผู้ลงนามครบ (ชื่อ · นามสกุล · ตำแหน่ง · ติ๊กรับรองข้อความรุ่นปัจจุบัน) — ลิงก์หน้าเอกสารบังคับ</summary>
    private static CustomerSignRule.SignerInput Sg(string first = "คุณสมหญิง", string last = "ผู้จัดการ")
        => new(first, last, "ผู้จัดการฝ่ายจัดซื้อ", true, CustomerSignText.Version);

    private static CustomerDocPage.PageAction? Act(CustomerDocPageView page, string id) => page.Actions.FirstOrDefault(a => a.Id == id);

    // ───────── S1: ลิงก์ชำระเดิม ⇒ หน้าเอกสารเต็ม + ช่องจ่ายเดิม ─────────

    [Fact]
    public async Task CDP267Db_LegacyPayLinkOpensPage_ลิงก์ชำระเดิมเปิดเป็นหน้าเอกสาร_ช่องจ่ายชื่อเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "s1 " + Guid.NewGuid().ToString("N")[..6]);
        var inv = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var link = await s.Links.EnsureLinkAsync(s.CompanyId, inv.Id, null);          // ทางออกลิงก์ชำระเดิม (รอบ 241)
        Assert.True(link.Ok, link.Refusal);
        var token = TokenOf(link.Url!);

        // ── ฝ่ายค้าน 267 M7 (267-1-30): ลิงก์ชำระที่ออกก่อนรอบ 267 (อายุ 60 วัน) คงขอบเขตเดิม — หัวใบ + ยอด + จ่าย · ไม่ได้บรรทัด/PDF/ใบที่เกี่ยวข้องย้อนหลัง ──
        var issuedAt = DateTime.UtcNow.AddDays(-40);
        await db.Documents.Where(d => d.Id == inv.Id).ExecuteUpdateAsync(x => x
            .SetProperty(d => d.PayLinkIssuedAt, (DateTime?)issuedAt).SetProperty(d => d.PayLinkExpiresAt, (DateTime?)issuedAt.AddDays(60)));
        var old = (await s.Links.PublicViewAsync(s.CompanyId, token))!;
        Assert.True(old.CanPayOnline, old.BlockedNote);                                  // จ่ายได้ตามเดิมจนถึงวันหมดอายุเดิม
        Assert.Equal(inv.DocumentNumber, old.DocumentNumber);
        Assert.Equal(1_070m, old.Target.AmountDue);
        Assert.True(old.Page!.Viewable);
        Assert.Empty(old.Page.Lines);
        Assert.Empty(old.Page.Related);
        Assert.Null(Act(old.Page, CustomerDocPage.ActionDownloadPdf));
        Assert.True(Act(old.Page, CustomerDocPage.ActionPayOnline)!.Enabled);
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, token, null));    // ไม่มีปุ่ม = ดาวน์โหลดไม่ได้ (ไม่ใช่แค่ซ่อนปุ่ม)
        Assert.Contains("ออกก่อนมีหน้าเอกสาร", (await s.Links.StatusAsync(s.CompanyId, inv.Id)).PageLinkNote);
        // ทิศตรงข้าม: ลิงก์ที่ออกหลังรอบ 267 (400 วัน) = หน้าเอกสารเต็ม
        var now = DateTime.UtcNow;
        await db.Documents.Where(d => d.Id == inv.Id).ExecuteUpdateAsync(x => x
            .SetProperty(d => d.PayLinkIssuedAt, (DateTime?)now).SetProperty(d => d.PayLinkExpiresAt, (DateTime?)CustomerDocPage.NewLinkExpiry(now)));

        var view = (await s.Links.PublicViewAsync(s.CompanyId, token))!;
        // ช่องจ่ายเดิม (หน้าชำระรุ่นก่อนที่แคชไว้ยังทำงาน)
        Assert.True(view.CanPayOnline, view.BlockedNote);
        Assert.Equal(inv.DocumentNumber, view.DocumentNumber);
        Assert.Equal(1_070m, view.Target.AmountDue);
        Assert.Equal(PaymentSourceKind.Document, view.Target.SourceKind);
        // ส่วนหน้าเอกสาร
        var page = view.Page!;
        Assert.True(page.Viewable);
        Assert.Equal("link", page.Mode);
        Assert.Equal(nameof(CustomerDocPage.LinkState.Usable), page.LinkState);
        var line = Assert.Single(page.Lines);
        Assert.Equal("ค่าบริการติดตั้ง", line.Description);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(500m, line.UnitPrice);
        Assert.Equal(1_000m, line.Amount);
        Assert.Equal((1_000m, 70m, 1_070m, 1_070m), (page.SubTotal, page.VatAmount, page.TotalAmount, page.BalanceDue));
        Assert.True(Act(page, CustomerDocPage.ActionDownloadPdf)!.Enabled);
        Assert.True(Act(page, CustomerDocPage.ActionPayOnline)!.Enabled);
        Assert.Null(Act(page, CustomerDocPage.ActionAccept));
        Assert.Equal(inv.Id, await s.Links.ResolvePdfDocumentAsync(s.CompanyId, token, null));

        // ทิศตรงข้าม: ชำระครบ ⇒ ยังเปิดดู/ดาวน์โหลดได้ · ปุ่มจ่ายปิดพร้อมเหตุของชั้นจ่าย
        await db.Documents.Where(d => d.Id == inv.Id).ExecuteUpdateAsync(x => x
            .SetProperty(d => d.BalanceDue, 0m).SetProperty(d => d.PaidAmount, 1_070m).SetProperty(d => d.Status, DocumentStatus.Paid));
        var paid = (await s.Links.PublicViewAsync(s.CompanyId, token))!;
        Assert.False(paid.CanPayOnline);
        Assert.True(paid.Page!.Viewable);
        Assert.False(Act(paid.Page, CustomerDocPage.ActionPayOnline)!.Enabled);
        Assert.Contains("ชำระครบ", Act(paid.Page, CustomerDocPage.ActionPayOnline)!.DisabledReason);
        Assert.True(Act(paid.Page, CustomerDocPage.ActionDownloadPdf)!.Enabled);
        Assert.Equal(inv.Id, await s.Links.ResolvePdfDocumentAsync(s.CompanyId, token, null));
    }

    // ───────── ลิงก์เดียวต่อใบสำหรับใบที่จ่ายไม่ได้ + เพิกถอน (259-2-8) ─────────

    [Fact]
    public async Task CDP267Db_QuotationOneLink_ใบเสนอราคาออกลิงก์หน้าเอกสารได้_ลิงก์เดียว_เพิกถอนแล้วไม่ออกทับเงียบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "qt " + Guid.NewGuid().ToString("N")[..6], gateway: false);   // ไม่มี gateway เลย (S37) — ลิงก์หน้าเอกสารไม่ขึ้นกับการรับชำระ
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT");

        // ชั้นจ่ายยังปฏิเสธใบเสนอราคา (ลิงก์ชำระ ≠ ลิงก์หน้าเอกสาร)
        Assert.False((await s.Links.EnsureLinkAsync(s.CompanyId, qt.Id, null)).Ok);
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.PayLinkTokenHash).SingleAsync());

        var a = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, qt.Id, "test");
        Assert.True(a.Ok, a.Refusal);
        Assert.Contains("/pages/pay-document.html?c=" + s.CompanyId.ToString("D"), a.Url);
        var b = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, qt.Id, "test");
        Assert.Equal(a.Url, b.Url);                                                    // หนึ่งลิงก์ต่อใบ — ลิงก์ที่ส่งไปแล้วไม่ถูกทับ
        Assert.Equal(a.Url, await s.Links.CurrentCustomerUrlAsync(s.CompanyId, qt.Id));
        Assert.Null(await s.Links.CurrentUrlAsync(s.CompanyId, qt.Id));                // ชั้นจ่าย: ใบนี้ไม่มีลิงก์ชำระ
        var stored = await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id)
            .Select(d => new { d.PayLinkTokenHash, d.PayLinkTokenProtected, d.PayLinkExpiresAt, d.PayLinkChannelsJson }).SingleAsync();
        Assert.Equal(DocumentPayLinkPolicy.TokenHash(a.Token!), stored.PayLinkTokenHash);
        Assert.DoesNotContain(a.Token!, stored.PayLinkTokenProtected);                 // ฐานไม่มี token ดิบ
        Assert.Null(stored.PayLinkChannelsJson);                                        // ใบที่จ่ายไม่ได้ไม่ตรึงช่องทาง
        Assert.True(stored.PayLinkExpiresAt > DateTime.UtcNow.AddDays(CustomerDocPage.LinkTtlDays - 1));

        var view = (await s.Links.PublicViewAsync(s.CompanyId, a.Token!))!;
        Assert.False(view.CanPayOnline);
        Assert.Null(view.BlockedNote);                                                  // ใบเสนอราคาไม่มีข้อความ "จ่ายไม่ได้เพราะ…"
        Assert.Equal(0m, view.Target.AmountDue);
        Assert.False(view.Target.CanPayNow);                                            // สร้างรายการชำระจากลิงก์ใบเสนอราคาไม่ได้
        Assert.False(view.CanSubmitSlip);
        var page = view.Page!;
        Assert.Null(Act(page, CustomerDocPage.ActionPayOnline));
        // ฝ่ายค้าน 267 M1: ลิงก์ที่ออกเพื่อ "ดู" ยังยอมรับไม่ได้ (ปุ่มปิดพร้อมเหตุ + ทางไปต่อ) — ผู้ขายต้องส่งให้ตอบรับก่อน · ป้ายบนรายการเอกสารยังไม่ขึ้น
        var closed = Act(page, CustomerDocPage.ActionAccept)!;
        Assert.False(closed.Enabled);
        Assert.Contains("ยังไม่ได้ส่งลิงก์ให้ตอบรับ", closed.DisabledReason);
        Assert.Null(closed.Href);
        Assert.True(Act(page, CustomerDocPage.ActionRequestLink)!.Enabled);
        db.ChangeTracker.Clear();
        var blockedAccept = Assert.IsType<ObjectResult>(await LegacyPage(db).Accept(a.Token!, new PublicQuotationController.AcceptRequest("คุณยังไม่ถูกเชิญ", RevisionNumber: 0)));
        Assert.Equal(409, blockedAccept.StatusCode);
        // H2: หน้าพนักงานรู้ว่าใบที่จ่ายไม่ได้มีลิงก์ที่ลูกค้าเปิดได้ (ปุ่มยกเลิกลิงก์ขึ้นกับค่านี้)
        var status = await s.Links.StatusAsync(s.CompanyId, qt.Id);
        Assert.NotNull(status.Refusal);
        Assert.True(status.PageLinkUsable);
        Assert.Equal(a.Url, status.PageUrl);
        // ทิศตรงข้าม: ผู้ขายส่งให้ตอบรับแล้ว ⇒ ปุ่มเปิด
        await OpenForResponseAsync(db, qt.Id);
        var accept = Act((await s.Links.PublicViewAsync(s.CompanyId, a.Token!))!.Page!, CustomerDocPage.ActionAccept)!;
        Assert.True(accept.Enabled, accept.DisabledReason);
        Assert.Equal(CustomerDocPage.RespondPath(s.CompanyId, a.Token!), accept.Href);   // รอบ 267 P2: ปุ่มส่งคำตอบที่เส้นเดียวของหน้า (ฟอร์มผู้ลงนามอยู่บนหน้าเอกสาร)

        // ใบร่าง/ฝั่งซื้อ ⇒ ออกไม่ได้พร้อมเหตุ (ไม่โยน)
        var draft = await AddDocAsync(db, s, DocumentType.Quotation, "QD", DocumentStatus.Draft);
        Assert.Contains("อนุมัติ", (await s.Links.EnsureCustomerLinkAsync(s.CompanyId, draft.Id, "test")).Refusal);
        var bill = await AddDocAsync(db, s, DocumentType.PurchaseInvoice, "PI");
        Assert.False((await s.Links.EnsureCustomerLinkAsync(s.CompanyId, bill.Id, "test")).Ok);
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == bill.Id).Select(d => d.PayLinkTokenHash).SingleAsync());
        Assert.False((await s.Links.EnsureCustomerLinkAsync(Guid.NewGuid(), qt.Id, "test")).Ok);   // ข้ามบริษัทไม่ได้

        // เพิกถอน ⇒ ลิงก์ตายทุกทาง · ผู้เรียกที่ไม่ได้ตั้งใจออกใหม่ถูกปฏิเสธ (ไม่ล้างการเพิกถอนเงียบ) · ไม่มีปุ่มขอลิงก์ใหม่
        await s.Links.RevokeAsync(s.CompanyId, qt.Id, "tester");
        var revoked = (await s.Links.PublicViewAsync(s.CompanyId, a.Token!))!;
        Assert.False(revoked.Page!.Viewable);
        Assert.False(revoked.Page.CanRequestLink);
        Assert.Equal("", revoked.DocumentNumber);
        Assert.Empty(revoked.Page.Lines);
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, a.Token!, null));
        Assert.Null(await s.Links.CurrentCustomerUrlAsync(s.CompanyId, qt.Id));
        Assert.False((await s.Links.RequestNewLinkAsync(s.CompanyId, a.Token!)).Found);
        Assert.False((await s.Links.StatusAsync(s.CompanyId, qt.Id)).PageLinkUsable);
        await Assert.ThrowsAsync<PayLinkRevokedException>(() => s.Links.EnsureCustomerLinkAsync(s.CompanyId, qt.Id, "test"));
        var c = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, qt.Id, "staff-link", default, allowReissueRevoked: true, actor: "tester");
        Assert.True(c.Ok, c.Refusal);
        Assert.NotEqual(a.Url, c.Url);
        Assert.Null(await s.Links.PublicViewAsync(s.CompanyId, a.Token!));             // token เดิมไม่ตรง hash ใหม่ = ไม่พบ
    }

    [Fact]
    public async Task CDP267Db_PageLinkThenPayLink_ลิงก์หน้าเอกสารออกก่อน_ทางจ่ายใช้ลิงก์เดิม_ช่องทางที่ตรึงไว้ไม่ถูกทับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "order " + Guid.NewGuid().ToString("N")[..6]);
        var inv = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var page = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, inv.Id, "test");
        Assert.True(page.Ok, page.Refusal);
        // ฝ่ายค้าน 267 M3: บริษัทไม่ได้เปิด "แนบลิงก์ชำระ" ⇒ ลิงก์หน้าเอกสารไม่ได้ปุ่มจ่าย/บัญชีรับโอน/ฟอร์มสลิป (ดู + ดาวน์โหลดได้) จนกว่าทางจ่ายจะเปิด
        var before = (await s.Links.PublicViewAsync(s.CompanyId, page.Token!))!;
        Assert.False(before.CanPayOnline);
        Assert.Equal(DocumentPayLinkService.PayNotOpenedNote, before.BlockedNote);
        Assert.Null(before.Transfer);
        Assert.False(before.CanSubmitSlip);
        Assert.False(before.Target.CanPayNow);
        Assert.True(Act(before.Page!, CustomerDocPage.ActionDownloadPdf)!.Enabled);
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == inv.Id).Select(d => d.PayLinkChannelsJson).SingleAsync());
        // ฝ่ายค้าน 267 M4: ทางจ่าย (ปุ่มออกลิงก์ชำระ/ตารางส่ง) ตรึงช่องทางครั้งแรกบนลิงก์เดิม
        var pay = await s.Links.EnsureLinkAsync(s.CompanyId, inv.Id, new[] { "PromptPay" });
        Assert.True(pay.Ok, pay.Refusal);
        Assert.Equal(page.Url, pay.Url);                                               // ลำดับไหนก็ได้ลิงก์เดียวกัน
        var view = (await s.Links.PublicViewAsync(s.CompanyId, page.Token!))!;
        Assert.True(view.CanPayOnline, view.BlockedNote);
        Assert.Equal(new[] { "PromptPay" }, view.Target.AllowedMethods);
        // ทิศตรงข้าม (259 C-M1): ช่องทางที่เลือกไว้แล้วไม่ถูกเขียนทับโดยการออกรอบถัดไป
        await s.Links.EnsureLinkAsync(s.CompanyId, inv.Id, null);
        await s.Links.EnsureCustomerLinkAsync(s.CompanyId, inv.Id, "test");
        Assert.Equal(new[] { "PromptPay" }, (await s.Links.PublicViewAsync(s.CompanyId, page.Token!))!.Target.AllowedMethods);
    }

    // ───────── หน้าตอบรับเดิมรับ token ของหน้าเอกสาร (ช่วงเปลี่ยนผ่าน) ─────────

    [Fact]
    public async Task CDP267Db_LegacyAcceptPageTakesPageToken_ยอมรับได้_พ้นวันยืนราคาปฏิเสธ_เพิกถอนไม่พบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "accept " + Guid.NewGuid().ToString("N")[..6], gateway: false);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(5));
        var link = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, qt.Id, "test");
        Assert.True(link.Ok, link.Refusal);
        await OpenForResponseAsync(db, qt.Id);
        db.ChangeTracker.Clear();

        Assert.IsType<OkObjectResult>(await LegacyPage(db).View(link.Token!));
        await s.Links.MarkOpenedAsync(s.CompanyId, link.Token!);
        Assert.NotNull(await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.SignLinkOpenedAt).SingleAsync());
        // ── ฝ่ายค้าน 267 H1: ยอมรับได้เฉพาะฉบับที่ลูกค้าเห็น ──
        // (ก) หน้าไม่ส่งเลข Rev (หน้ารุ่นเก่าที่แคชไว้) ⇒ ปฏิเสธ
        var noRev = Assert.IsType<ObjectResult>(await LegacyPage(db).Accept(link.Token!, new PublicQuotationController.AcceptRequest("คุณสมหญิง ผู้จัดการ")));
        Assert.Equal(409, noRev.StatusCode);
        // (ข) ผู้ขายแก้เอกสารระหว่างที่หน้าลูกค้าค้างอยู่ (Rev 0 → 1) ⇒ ปฏิเสธ "เอกสารถูกแก้ไข" — ไม่บันทึกการยอมรับ Rev ที่ลูกค้าไม่เคยเห็น
        await db.Documents.Where(d => d.Id == qt.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.RevisionNumber, 1));
        var stale = Assert.IsType<ObjectResult>(await LegacyPage(db).Accept(link.Token!, new PublicQuotationController.AcceptRequest("คุณสมหญิง ผู้จัดการ", RevisionNumber: 0)));
        Assert.Equal(409, stale.StatusCode);
        Assert.Contains(CustomerDocPage.RespondStaleMessage, ((Accounting.Models.DTOs.ApiResponse<string>)stale.Value!).Message);
        // (ค) hash เนื้อหาที่เห็นไม่ตรงเนื้อหาปัจจุบัน ⇒ ปฏิเสธ
        var wrongHash = Assert.IsType<ObjectResult>(await LegacyPage(db).Accept(link.Token!,
            new PublicQuotationController.AcceptRequest("คุณสมหญิง ผู้จัดการ", RevisionNumber: 1, ContentHash: new string('0', 64))));
        Assert.Equal(409, wrongHash.StatusCode);
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.QuotationAcceptedAt).SingleAsync());
        // (ง) เส้น revision จริงปิดช่วงตอบรับ (ตัวตั้งเดียว) ⇒ ลิงก์เดิมยังเปิดดู Rev ล่าสุดได้ แต่ยอมรับไม่ได้จนผู้ขายส่งใหม่
        var tracked = await db.Documents.FirstAsync(d => d.Id == qt.Id);
        SignRequestKinds.CloseRespondOnRevision(tracked);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var afterRev = (await s.Links.PublicViewAsync(s.CompanyId, link.Token!))!.Page!;
        Assert.True(afterRev.Viewable);
        Assert.Equal(1, afterRev.RevisionNumber);
        Assert.False(Act(afterRev, CustomerDocPage.ActionAccept)!.Enabled);
        var closedAfterRev = Assert.IsType<ObjectResult>(await LegacyPage(db).Accept(link.Token!, new PublicQuotationController.AcceptRequest("คุณสมหญิง ผู้จัดการ", RevisionNumber: 1)));
        Assert.Equal(409, closedAfterRev.StatusCode);
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.QuotationAcceptedAt).SingleAsync());
        // ทิศตรงข้าม: ผู้ขายส่งให้ตอบรับใหม่ + ลูกค้าเห็น Rev ปัจจุบัน (เลข Rev + hash จากเส้นดู) ⇒ ยอมรับได้
        await OpenForResponseAsync(db, qt.Id);
        db.ChangeTracker.Clear();
        var seenHash = SeenContentHash(await LegacyPage(db).View(link.Token!));   // สิ่งที่หน้ายอมรับได้จากเซิร์ฟเวอร์และส่งกลับตอนกด
        db.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>(await LegacyPage(db).Accept(link.Token!,
            new PublicQuotationController.AcceptRequest("", RevisionNumber: 1, ContentHash: seenHash, Signer: Sg())));
        var row = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == qt.Id);
        Assert.NotNull(row.QuotationAcceptedAt);
        Assert.Equal("คุณสมหญิง ผู้จัดการ", row.QuotationAcceptedBy);
        Assert.Single(await db.SignEventOutbox.Where(e => e.CompanyId == s.CompanyId && e.DocumentId == qt.Id).ToListAsync());
        // หน้าเอกสารเห็นสถานะเดียวกัน: ปุ่มยอมรับปิด "ยอมรับแล้ว" + ชื่อผู้ยอมรับ
        var page = (await s.Links.PublicViewAsync(s.CompanyId, link.Token!))!.Page!;
        Assert.False(Act(page, CustomerDocPage.ActionAccept)!.Enabled);
        Assert.Equal("คุณสมหญิง ผู้จัดการ", page.RespondedBy);
        Assert.Null(Act(page, CustomerDocPage.ActionRequestLink));

        // พ้นวันยืนราคา ⇒ หน้าเอกสารปิดปุ่มพร้อมเหตุ และหน้าตอบรับเดิมปฏิเสธ (ไม่ยอมรับเงียบ) — ตัวตัดสินเดียวกัน
        var late = await AddDocAsync(db, s, DocumentType.Quotation, "QL", due: DateTime.UtcNow.Date.AddDays(-2));
        var lateLink = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, late.Id, "test");
        await OpenForResponseAsync(db, late.Id);
        db.ChangeTracker.Clear();
        var latePage = (await s.Links.PublicViewAsync(s.CompanyId, lateLink.Token!))!.Page!;
        Assert.False(Act(latePage, CustomerDocPage.ActionAccept)!.Enabled);
        Assert.Contains("ยืนราคาถึง", Act(latePage, CustomerDocPage.ActionAccept)!.DisabledReason);
        Assert.True(Act(latePage, CustomerDocPage.ActionRequestLink)!.Enabled);
        var denied = Assert.IsType<ObjectResult>(await LegacyPage(db).Accept(lateLink.Token!, new PublicQuotationController.AcceptRequest("คุณช้า", RevisionNumber: 0)));
        Assert.Equal(409, denied.StatusCode);
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == late.Id).Select(d => d.QuotationAcceptedAt).SingleAsync());

        // เพิกถอนลิงก์ ⇒ หน้าตอบรับเดิมก็ไม่พบ (ลิงก์เดียว ตายทุกทาง)
        var gone = await AddDocAsync(db, s, DocumentType.Quotation, "QR");
        var goneLink = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, gone.Id, "test");
        await s.Links.RevokeAsync(s.CompanyId, gone.Id, "tester");
        db.ChangeTracker.Clear();
        Assert.IsType<NotFoundObjectResult>(await LegacyPage(db).View(goneLink.Token!));
        Assert.IsType<NotFoundObjectResult>(await LegacyPage(db).Accept(goneLink.Token!, new PublicQuotationController.AcceptRequest("คุณเอ", RevisionNumber: 0)));
        // token ของใบแจ้งหนี้ (ไม่ใช่ใบเสนอราคา) ใช้กับหน้ายอมรับไม่ได้
        var inv = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var invLink = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, inv.Id, "test");
        Assert.IsType<NotFoundObjectResult>(await LegacyPage(db).View(invLink.Token!));
    }

    [Fact]
    public async Task CDP267Db_DeliveryNote_ไม่แสดงราคา_เซ็นรับผ่านtokenของหน้าเอกสาร_พรีวิวไม่มีผลข้างเคียง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "dn " + Guid.NewGuid().ToString("N")[..6], gateway: false);
        var dn = await AddDocAsync(db, s, DocumentType.DeliveryNote, "DN");

        // พรีวิวของพนักงาน: ไม่ออกลิงก์ · ไม่ประทับเปิด · ทุกปุ่มปิด
        var preview = (await s.Links.PageForDocumentAsync(s.CompanyId, dn.Id, CustomerDocPage.Mode.Preview))!;
        Assert.Equal("preview", preview.Page!.Mode);
        Assert.All(preview.Page.Actions, a => Assert.False(a.Enabled));
        Assert.False(preview.Target.CanPayNow);
        var untouched = await db.Documents.AsNoTracking().Where(d => d.Id == dn.Id).Select(d => new { d.PayLinkTokenHash, d.SignLinkOpenedAt }).SingleAsync();
        Assert.Null(untouched.PayLinkTokenHash);
        Assert.Null(untouched.SignLinkOpenedAt);
        Assert.Null(await s.Links.PageForDocumentAsync(s.CompanyId, dn.Id, CustomerDocPage.Mode.Link));        // โหมดลิงก์ต้องมาทาง token
        Assert.Null(await s.Links.PageForDocumentAsync(Guid.NewGuid(), dn.Id, CustomerDocPage.Mode.Preview));  // ข้ามบริษัทไม่ได้

        var link = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, dn.Id, "test");
        Assert.True(link.Ok, link.Refusal);
        // M1: ยังไม่ได้ส่งให้เซ็นรับ ⇒ ปุ่มปิด
        Assert.False(Act((await s.Links.PublicViewAsync(s.CompanyId, link.Token!))!.Page!, CustomerDocPage.ActionSignDelivery)!.Enabled);
        await OpenForResponseAsync(db, dn.Id);
        var page = (await s.Links.PublicViewAsync(s.CompanyId, link.Token!))!.Page!;
        Assert.False(page.ShowAmounts);
        var line = Assert.Single(page.Lines);
        Assert.Equal(2m, line.Quantity);
        Assert.Null(line.UnitPrice);
        Assert.Null(line.Amount);
        Assert.Equal(0m, page.TotalAmount);
        var sign = Act(page, CustomerDocPage.ActionSignDelivery)!;
        Assert.True(sign.Enabled, sign.DisabledReason);
        Assert.Equal(CustomerDocPage.RespondPath(s.CompanyId, link.Token!), sign.Href);

        db.ChangeTracker.Clear();
        var sig = QuotationAcceptanceRound239Tests.DataUri(QuotationAcceptanceRound239Tests.DrawnSignature());
        // H1 (ใบส่งของ): ไม่ส่งเลข Rev / Rev ไม่ตรง ⇒ ปฏิเสธ ไม่เซ็นรับฉบับที่ไม่เคยเห็น
        Assert.Equal(409, Assert.IsType<ObjectResult>(await LegacyPage(db).SignDelivery(link.Token!,
            new PublicQuotationController.DeliverySignRequest("ผู้รับของ", sig))).StatusCode);
        db.ChangeTracker.Clear();
        await db.Documents.Where(d => d.Id == dn.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.RevisionNumber, 2));
        var staleDn = Assert.IsType<ObjectResult>(await LegacyPage(db).SignDelivery(link.Token!,
            new PublicQuotationController.DeliverySignRequest("ผู้รับของ", sig, RevisionNumber: 0)));
        Assert.Equal(409, staleDn.StatusCode);
        db.ChangeTracker.Clear();
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == dn.Id).Select(d => d.DeliverySignedAt).SingleAsync());
        // ทิศตรงข้าม: Rev ที่เห็นตรงกับใบ ⇒ เซ็นรับได้
        // ฝ่ายค้าน 267-P2 L5: token ของหน้าเอกสารต้องส่ง hash เนื้อหาที่เห็นด้วย
        var dnNow = await db.Documents.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == dn.Id && d.CompanyId == s.CompanyId);
        db.ChangeTracker.Clear();
        Assert.IsType<OkObjectResult>(await LegacyPage(db).SignDelivery(link.Token!,
            new PublicQuotationController.DeliverySignRequest("", sig, RevisionNumber: 2, Signer: Sg("สมศักดิ์", "รับของ"),
                ContentHash: DocumentSignedContent.Hash(dnNow, dnNow.Lines))));
        db.ChangeTracker.Clear();
        Assert.NotNull(await db.Documents.AsNoTracking().Where(d => d.Id == dn.Id).Select(d => d.DeliverySignedAt).SingleAsync());
        // พ้น 14 วัน ⇒ ปฏิเสธ
        var old = await AddDocAsync(db, s, DocumentType.DeliveryNote, "DO");
        var oldLink = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, old.Id, "test");
        await OpenForResponseAsync(db, old.Id, DateTime.UtcNow.AddDays(-15));
        db.ChangeTracker.Clear();
        var late = Assert.IsType<ObjectResult>(await LegacyPage(db).SignDelivery(oldLink.Token!, new PublicQuotationController.DeliverySignRequest("ผู้รับของ", sig, RevisionNumber: 0)));
        Assert.Equal(409, late.StatusCode);
    }

    // ───────── S27/S28: ลิงก์หมดอายุ ⇒ ขอลิงก์ใหม่ (ไม่ออก token ให้คนนอก) ─────────

    [Fact]
    public async Task CDP267Db_RequestLinkNotifiesOnce_ลิงก์หมดอายุไม่เปิดเผยข้อมูล_ขอลิงก์ใหม่ได้วันละครั้ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "expired " + Guid.NewGuid().ToString("N")[..6]);
        var inv = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var link = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, inv.Id, "test");
        await db.Documents.Where(d => d.Id == inv.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.PayLinkExpiresAt, (DateTime?)DateTime.UtcNow.AddDays(-10)));

        var view = (await s.Links.PublicViewAsync(s.CompanyId, link.Token!))!;
        Assert.False(view.Page!.Viewable);
        Assert.True(view.Page.CanRequestLink);                                          // ไม่ใช่ทางตัน
        Assert.Equal(nameof(CustomerDocPage.LinkState.Expired), view.Page.LinkState);
        Assert.Equal("", view.DocumentNumber);                                          // ไม่เปิดเผยข้อมูลของใบ
        Assert.Null(view.CustomerName);
        Assert.Empty(view.Page.Lines);
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, link.Token!, null));

        var hashBefore = await db.Documents.AsNoTracking().Where(d => d.Id == inv.Id).Select(d => d.PayLinkTokenHash).SingleAsync();
        var first = await s.Links.RequestNewLinkAsync(s.CompanyId, link.Token!);
        Assert.True(first.Found);
        Assert.True(first.Recorded);
        Assert.Equal(inv.Id, first.DocumentId);
        var second = await s.Links.RequestNewLinkAsync(s.CompanyId, link.Token!);
        Assert.True(second.Found);
        Assert.False(second.Recorded);                                                  // วันละครั้งต่อใบ
        Assert.NotEqual(first.Message, second.Message);
        var id = inv.Id.ToString();
        Assert.Equal(1, await db.AuditLogs.AsNoTracking().CountAsync(a => a.CompanyId == s.CompanyId
            && a.EntityType == DocumentPayLinkService.LinkRequestEntity && a.EntityId == id));
        // ไม่ออก token ให้คนนอก: hash ของลิงก์ไม่เปลี่ยน และลิงก์ยังหมดอายุ
        Assert.Equal(hashBefore, await db.Documents.AsNoTracking().Where(d => d.Id == inv.Id).Select(d => d.PayLinkTokenHash).SingleAsync());
        Assert.False((await s.Links.PublicViewAsync(s.CompanyId, link.Token!))!.Page!.Viewable);

        // ฝ่ายค้าน 267 M5: ลิงก์ปกติที่หน้าไม่ได้เสนอปุ่มขอลิงก์ (ใบแจ้งหนี้ที่ยังเปิดได้) ⇒ กดกระดิ่งพนักงาน/เขียน audit ไม่ได้
        var normal = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var normalLink = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, normal.Id, "test");
        Assert.False((await s.Links.RequestNewLinkAsync(s.CompanyId, normalLink.Token!)).Found);
        var normalId = normal.Id.ToString();
        Assert.Equal(0, await db.AuditLogs.AsNoTracking().CountAsync(a => a.CompanyId == s.CompanyId
            && a.EntityType == DocumentPayLinkService.LinkRequestEntity && a.EntityId == normalId));
        // ทิศตรงข้าม: ลิงก์ที่ยังใช้ได้แต่หน้าเสนอปุ่ม (ใบเสนอราคาที่ยังไม่ถูกเปิดให้ตอบรับ) ⇒ รับ
        var viewOnly = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var viewOnlyLink = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, viewOnly.Id, "test");
        Assert.True((await s.Links.RequestNewLinkAsync(s.CompanyId, viewOnlyLink.Token!)).Recorded);
        Assert.False((await s.Links.RequestNewLinkAsync(s.CompanyId, viewOnlyLink.Token!)).Recorded);

        // ทิศตรงข้าม: token มั่ว / ข้ามบริษัท / หมดอายุนานเกินช่วงขอ ⇒ ไม่พบ ไม่บันทึก
        Assert.False((await s.Links.RequestNewLinkAsync(s.CompanyId, DocumentPayLinkPolicy.NewToken())).Found);
        Assert.False((await s.Links.RequestNewLinkAsync(Guid.NewGuid(), link.Token!)).Found);
        var stale = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var staleLink = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, stale.Id, "test");
        await db.Documents.Where(d => d.Id == stale.Id).ExecuteUpdateAsync(x => x
            .SetProperty(d => d.PayLinkExpiresAt, (DateTime?)DateTime.UtcNow.AddDays(-(CustomerDocPage.ExpiredRequestWindowDays + 2))));
        Assert.False((await s.Links.PublicViewAsync(s.CompanyId, staleLink.Token!))!.Page!.CanRequestLink);
        Assert.False((await s.Links.RequestNewLinkAsync(s.CompanyId, staleLink.Token!)).Found);
        // พนักงานออกลิงก์ใหม่ (ลิงก์หมดอายุ ⇒ token ใหม่) ⇒ ลูกค้าเปิดได้อีก
        var fresh = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, inv.Id, "staff-link", default, allowReissueRevoked: true, actor: "tester");
        Assert.NotEqual(link.Url, fresh.Url);
        Assert.True((await s.Links.PublicViewAsync(s.CompanyId, fresh.Token!))!.Page!.Viewable);
    }

    // ───────── S13/S14/S15/S24: ใบที่เกี่ยวข้อง = ชุดเดียวกับที่ดาวน์โหลดได้ ─────────

    [Fact]
    public async Task CDP267Db_RelatedSet_ใบวางบิลแสดงใบในบิล_ใบยกเลิกแสดงใบแทน_นอกชุดและผู้ติดต่ออื่นดาวน์โหลดไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "rel " + Guid.NewGuid().ToString("N")[..6]);
        var other = new Contact { CompanyId = s.CompanyId, Name = "ลูกค้าอื่น 267", IsCustomer = true };
        db.Contacts.Add(other);
        await db.SaveChangesAsync();
        var inv1 = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var inv2 = await AddDocAsync(db, s, DocumentType.Invoice, "INV", DocumentStatus.Paid, balance: 0m);
        var unrelated = await AddDocAsync(db, s, DocumentType.Invoice, "INV");                       // ผู้ติดต่อเดียวกันแต่ไม่อยู่ในบิล
        var foreign = await AddDocAsync(db, s, DocumentType.Invoice, "INV", contactId: other.Id);   // บริษัทเดียวกัน คนละผู้ติดต่อ
        var draftChild = await AddDocAsync(db, s, DocumentType.Invoice, "INV", DocumentStatus.Draft);
        var bn = await AddDocAsync(db, s, DocumentType.BillingNote, "BN", tweak: d =>
        {
            d.Lines.Clear();
            d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ใบแจ้งหนี้ " + inv1.DocumentNumber, Quantity = 1, UnitPrice = 1_070m, Amount = 1_070m, SourceDocumentId = inv1.Id });
            d.Lines.Add(new DocumentLine { LineOrder = 2, Description = "ใบแจ้งหนี้ " + inv2.DocumentNumber, Quantity = 1, UnitPrice = 0m, Amount = 0m, SourceDocumentId = inv2.Id });
            d.Lines.Add(new DocumentLine { LineOrder = 3, Description = "ใบของผู้ติดต่ออื่น (ข้อมูลเสีย)", Quantity = 1, UnitPrice = 1m, Amount = 1m, SourceDocumentId = foreign.Id });
            d.Lines.Add(new DocumentLine { LineOrder = 4, Description = "ใบร่าง", Quantity = 1, UnitPrice = 1m, Amount = 1m, SourceDocumentId = draftChild.Id });
        });
        var link = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, bn.Id, "test");
        Assert.True(link.Ok, link.Refusal);
        var page = (await s.Links.PublicViewAsync(s.CompanyId, link.Token!))!.Page!;
        // S24 (แบบอ่าน): ใบในบิลครบทั้งที่ค้างและที่จ่ายแล้ว · ใบของผู้ติดต่ออื่น/ใบร่าง ไม่ขึ้น
        Assert.Equal(new[] { inv1.Id, inv2.Id }.OrderBy(x => x), page.Related.Select(r => r.RelId).OrderBy(x => x));
        Assert.All(page.Related, r => Assert.Equal("billed", r.Kind));
        Assert.Equal(1_070m, page.Related.Single(r => r.RelId == inv1.Id).BalanceDue);
        Assert.Null(Act(page, CustomerDocPage.ActionPayOnline));                         // ใบวางบิล: ไม่มีปุ่มจ่ายรวมใน P1

        Assert.Equal(bn.Id, await s.Links.ResolvePdfDocumentAsync(s.CompanyId, link.Token!, null));
        Assert.Equal(inv1.Id, await s.Links.ResolvePdfDocumentAsync(s.CompanyId, link.Token!, inv1.Id));
        // S15: บริษัทเดียวกันแต่ไม่ใช่ใบที่เกี่ยว · ผู้ติดต่ออื่น · ใบร่าง · รหัสมั่ว ⇒ ไม่ได้
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, link.Token!, unrelated.Id));
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, link.Token!, foreign.Id));
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, link.Token!, draftChild.Id));
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, link.Token!, Guid.NewGuid()));

        // ฝ่ายค้าน 267 M2: หน้าใบส่งของ (ไม่แสดงราคา · ผู้ถือลิงก์อาจเป็นคนรับของหน้างาน) ⇒ ไม่มีใบที่เกี่ยวข้องชนิดที่มีราคา ทั้งรายการและ PDF
        var dn = await AddDocAsync(db, s, DocumentType.DeliveryNote, "DN", tweak: d => d.RelatedDocumentId = inv1.Id);
        var dnLink = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, dn.Id, "test");
        Assert.Empty((await s.Links.PublicViewAsync(s.CompanyId, dnLink.Token!))!.Page!.Related);
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, dnLink.Token!, inv1.Id));
        Assert.Equal(dn.Id, await s.Links.ResolvePdfDocumentAsync(s.CompanyId, dnLink.Token!, null));
        // ทิศตรงข้าม: ใบแจ้งหนี้ (แสดงราคา) เห็นใบส่งของที่ออกต่อจากมัน
        var inv1PageLink = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, inv1.Id, "test");
        Assert.Contains((await s.Links.PublicViewAsync(s.CompanyId, inv1PageLink.Token!))!.Page!.Related, r => r.RelId == dn.Id && r.TotalAmount == null);
        // โหมด LINE-ดู/พรีวิว: ไม่มีรายการใบที่เกี่ยวข้อง (ใบเดียวเท่าลิงก์การ์ดเดิม · พรีวิวไม่ผ่านด่านชั้นความลับของใบอื่น)
        Assert.Empty((await s.Links.PageForDocumentAsync(s.CompanyId, bn.Id, CustomerDocPage.Mode.LineView))!.Page!.Related);
        Assert.Empty((await s.Links.PageForDocumentAsync(s.CompanyId, bn.Id, CustomerDocPage.Mode.Preview))!.Page!.Related);

        // S13: ใบที่ถูกยกเลิกหลังส่งลิงก์ ⇒ เปิดได้ · ไม่มีปุ่มนอกจากดาวน์โหลด · แสดงใบแทนของผู้ติดต่อเดียวกัน
        var replacement = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var voided = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var voidLink = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, voided.Id, "test");
        await db.Documents.Where(d => d.Id == voided.Id).ExecuteUpdateAsync(x => x
            .SetProperty(d => d.Status, DocumentStatus.Voided).SetProperty(d => d.ReplacedByDocumentId, (Guid?)replacement.Id));
        var voidView = (await s.Links.PublicViewAsync(s.CompanyId, voidLink.Token!))!;
        Assert.True(voidView.Page!.Viewable);
        Assert.True(voidView.Page.Voided);
        Assert.False(voidView.CanPayOnline);
        Assert.Equal(CustomerDocPage.ActionDownloadPdf, Assert.Single(voidView.Page.Actions).Id);
        var rep = Assert.Single(voidView.Page.Related);
        Assert.Equal((replacement.Id, "replacement"), (rep.RelId, rep.Kind));
        // ออกลิงก์ใหม่ให้ใบที่ยกเลิกแล้วไม่ได้
        var voided2 = await AddDocAsync(db, s, DocumentType.Invoice, "INV", DocumentStatus.Voided);
        Assert.False((await s.Links.EnsureCustomerLinkAsync(s.CompanyId, voided2.Id, "test")).Ok);

        // S14: ใบแทนเป็นของผู้ติดต่ออื่น ⇒ ไม่แสดง และดาวน์โหลดไม่ได้
        await db.Documents.Where(d => d.Id == voided.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.ReplacedByDocumentId, (Guid?)foreign.Id));
        var hidden = (await s.Links.PublicViewAsync(s.CompanyId, voidLink.Token!))!;
        Assert.Empty(hidden.Page!.Related);
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, voidLink.Token!, foreign.Id));

        // ใบกลับเป็นร่างหลังส่งลิงก์ ⇒ ลิงก์เดิมไม่เปิดเผยอะไร
        await db.Documents.Where(d => d.Id == inv1.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Approved));
        var inv1Link = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, inv1.Id, "test");
        await db.Documents.Where(d => d.Id == inv1.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Draft));
        var reverted = (await s.Links.PublicViewAsync(s.CompanyId, inv1Link.Token!))!;
        Assert.False(reverted.Page!.Viewable);
        Assert.Equal("", reverted.DocumentNumber);

        // ฝ่ายค้าน 267 M7 (267-1-31): ลบ/ปิดผู้ติดต่อ · DSR erase ⇒ ลิงก์ของทุกใบของผู้ติดต่อรายนั้นถูกเพิกถอน (ของผู้ติดต่ออื่นไม่ถูกแตะ)
        var foreignLink = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, foreign.Id, "test");
        var revokedCount = await DocumentPayLinkService.RevokeForContactAsync(db, s.CompanyId, s.ContactId, "tester", "contact-deactivated");
        await db.SaveChangesAsync();
        Assert.True(revokedCount >= 3);
        Assert.False((await s.Links.PublicViewAsync(s.CompanyId, link.Token!))!.Page!.Viewable);
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, link.Token!, null));
        Assert.True((await s.Links.PublicViewAsync(s.CompanyId, foreignLink.Token!))!.Page!.Viewable);
        Assert.Equal(0, await DocumentPayLinkService.RevokeForContactAsync(db, s.CompanyId, s.ContactId, "tester", "again"));   // รันซ้ำไม่เพิกถอนซ้ำ
    }

    // ───────── 267-1-3: migration token ดิบ ⇒ hash (PostgreSQL เขียน · C# ค้น) ─────────

    [Fact]
    public async Task CDP267Db_LegacyRawTokenMigration_tokenดิบถูกแทนด้วยhash_ลิงก์ที่ลูกค้าถือยังเปิดได้_รันซ้ำไม่เปลี่ยน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "mig " + Guid.NewGuid().ToString("N")[..6], gateway: false);
        var rawQt = DocumentPayLinkPolicy.NewToken();
        var rawDn = DocumentPayLinkPolicy.NewToken().ToUpperInvariant();               // token ที่เก็บตัวพิมพ์ใหญ่ ⇒ hash ของตัวพิมพ์เล็ก (ฝั่งค้นทำเหมือนกัน)
        // สภาพก่อนรอบ 267: token ดิบอยู่ในคอลัมน์
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", tweak: d =>
        {
            d.QuotationAcceptToken = rawQt;
            d.QuotationAcceptTokenExpiresAt = DateTime.UtcNow.AddDays(20);
        });
        var dn = await AddDocAsync(db, s, DocumentType.DeliveryNote, "DN", tweak: d =>
        {
            d.DeliverySignToken = rawDn;
            d.DeliverySignTokenExpiresAt = DateTime.UtcNow.AddDays(10);
        });
        db.ChangeTracker.Clear();
        // ก่อน migration: ฝั่งค้น (hash) ไม่เจอค่าดิบ = ลิงก์เปิดไม่ได้ ⇒ migration จำเป็น (ทิศตรงข้ามของข้อถัดไป)
        Assert.IsType<NotFoundObjectResult>(await LegacyPage(db).View(rawQt));

        async Task RunAsync()
        {
            // สคริปต์เดียวกับตอนบูต จำกัดที่บริษัทของเทสต์นี้ (ไม่แตะแถวของเทสต์อื่นที่รันคู่ขนาน)
            foreach (var sql in CustomerDocPage.LegacyTokenHashSql)
                await db.Database.ExecuteSqlRawAsync(sql.TrimEnd(';') + " AND \"CompanyId\" = {0};", s.CompanyId);
            db.ChangeTracker.Clear();
        }
        await RunAsync();
        var after = await db.Documents.AsNoTracking().Where(d => d.CompanyId == s.CompanyId && (d.Id == qt.Id || d.Id == dn.Id))
            .Select(d => new { d.Id, d.QuotationAcceptToken, d.DeliverySignToken }).ToListAsync();
        Assert.Equal(CustomerDocPage.LegacyStored(rawQt), after.Single(x => x.Id == qt.Id).QuotationAcceptToken);
        Assert.Equal(CustomerDocPage.LegacyStored(rawDn), after.Single(x => x.Id == dn.Id).DeliverySignToken);
        Assert.DoesNotContain(after, x => x.QuotationAcceptToken == rawQt || x.DeliverySignToken == rawDn);   // ฐานไม่เหลือ token ดิบ

        // ลิงก์ที่ลูกค้าถืออยู่ยังเปิด/ยอมรับ/เซ็นรับได้
        Assert.IsType<OkObjectResult>(await LegacyPage(db).View(rawQt));
        Assert.IsType<OkObjectResult>(await LegacyPage(db).Accept(rawQt, new PublicQuotationController.AcceptRequest("คุณเดิม")));
        Assert.IsType<OkObjectResult>(await LegacyPage(db).ViewDelivery(rawDn.ToLowerInvariant()));
        db.ChangeTracker.Clear();
        // ถือ hash ที่รั่วจากฐาน ≠ ถือลิงก์
        var leaked = CustomerDocPage.LegacyStored(rawQt)[CustomerDocPage.LegacyHashPrefix.Length..];
        Assert.IsType<NotFoundObjectResult>(await LegacyPage(db).View(leaked));

        // รันซ้ำ = ไม่เปลี่ยน (ไม่ hash ซ้อน)
        await RunAsync();
        Assert.Equal(CustomerDocPage.LegacyStored(rawQt),
            await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.QuotationAcceptToken).SingleAsync());
    }

    // ───────── เส้นลิงก์เซ็นเดิม ⇒ ลิงก์หน้าเอกสาร (ไม่ออก token ของตัวเอง) ─────────

    [Fact]
    public async Task CDP267Db_SignRequestIssuesPageLink_ไม่มีtokenยอมรับใหม่ในฐาน_พ้นวันยืนราคาไม่ส่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "sign " + Guid.NewGuid().ToString("N")[..6], gateway: false);
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT", due: DateTime.UtcNow.Date.AddDays(7));
        db.ChangeTracker.Clear();
        var svc = new SignRequestService(db, F4Proxy<IEmailSenderFactory>.Create((_, _) => null), Secrets, NullLogger<SignRequestService>.Instance, Config(), links: s.Links);

        var link = await svc.IssueLinkAsync(s.CompanyId, qt.Id, new IssueSignLinkOptions(Regenerate: true), "https://app.nextacc.test");
        Assert.Contains("/pages/pay-document.html?c=" + s.CompanyId.ToString("D"), link.Url);
        Assert.Equal("issued", link.Status!.Code);
        var row = await db.Documents.AsNoTracking().SingleAsync(d => d.Id == qt.Id);
        Assert.Null(row.QuotationAcceptToken);                                          // ไม่ออก token ยอมรับของตัวเองอีก (267-1-3)
        Assert.NotNull(row.SignLinkIssuedAt);
        Assert.Equal(DocumentPayLinkPolicy.TokenHash(link.Token), row.PayLinkTokenHash);
        // ไม่ขอออกใหม่ = ลิงก์เดิม
        db.ChangeTracker.Clear();
        Assert.Equal(link.Url, (await svc.IssueLinkAsync(s.CompanyId, qt.Id, new IssueSignLinkOptions(Regenerate: false), "https://app.nextacc.test")).Url);
        // ฝ่ายค้าน 267 H2 (267-1-24): "ออกใหม่" = หมุน token จริง — ลิงก์เดิม (ส่งผิดคน) ใช้ไม่ได้ทันทีทุกทาง
        db.ChangeTracker.Clear();
        var again = await svc.IssueLinkAsync(s.CompanyId, qt.Id, new IssueSignLinkOptions(Regenerate: true), "https://app.nextacc.test");
        Assert.NotEqual(link.Url, again.Url);
        Assert.Null(await s.Links.PublicViewAsync(s.CompanyId, link.Token));
        db.ChangeTracker.Clear();
        Assert.IsType<NotFoundObjectResult>(await LegacyPage(db).Accept(link.Token, new PublicQuotationController.AcceptRequest("ผู้รับผิดคน", RevisionNumber: 0)));
        Assert.True((await s.Links.PublicViewAsync(s.CompanyId, again.Token))!.Page!.Viewable);
        // ป้ายสถานะบนรายการเอกสารยังทำงานกับลิงก์รุ่นใหม่
        Assert.Equal("issued", (await svc.GetStatusesAsync(s.CompanyId, new[] { qt.Id }))[qt.Id].Code);
        await s.Links.MarkOpenedAsync(s.CompanyId, again.Token);
        Assert.Equal("opened", (await svc.GetStatusesAsync(s.CompanyId, new[] { qt.Id }))[qt.Id].Code);
        // ฝ่ายค้าน 267 M8: พนักงานเพิกถอนแล้ว — คีย์ API (StaffIntent: false) ออกใหม่แทนไม่ได้ · พนักงานกดเองได้
        await s.Links.RevokeAsync(s.CompanyId, qt.Id, "staff");
        db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<PayLinkRevokedException>(() =>
            svc.IssueLinkAsync(s.CompanyId, qt.Id, new IssueSignLinkOptions(Regenerate: false, StaffIntent: false), "https://app.nextacc.test"));
        await Assert.ThrowsAsync<PayLinkRevokedException>(() =>
            svc.IssueLinkAsync(s.CompanyId, qt.Id, new IssueSignLinkOptions(Regenerate: true, StaffIntent: false), "https://app.nextacc.test"));
        db.ChangeTracker.Clear();
        Assert.NotNull(await db.Documents.AsNoTracking().Where(d => d.Id == qt.Id).Select(d => d.PayLinkRevokedAt).SingleAsync());   // การเพิกถอนไม่ถูกล้าง
        var byStaff = await svc.IssueLinkAsync(s.CompanyId, qt.Id, new IssueSignLinkOptions(Regenerate: false), "https://app.nextacc.test");
        Assert.NotEqual(again.Url, byStaff.Url);

        // S5: ใบเสนอราคาที่พ้นวันยืนราคา ⇒ ไม่ส่งลิงก์ที่ลูกค้ากดยอมรับไม่ได้ (ปฏิเสธพร้อมทางไปต่อ)
        var late = await AddDocAsync(db, s, DocumentType.Quotation, "QL", due: DateTime.UtcNow.Date.AddDays(-3));
        db.ChangeTracker.Clear();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            svc.IssueLinkAsync(s.CompanyId, late.Id, new IssueSignLinkOptions(Regenerate: true), "https://app.nextacc.test"));
        Assert.Contains("ยืนราคา", ex.Message);
        // ฝ่ายค้าน 267 L1: คำขอที่ถูกปฏิเสธไม่เปลี่ยนสถานะลิงก์ (ไม่ออก token · ไม่เปิดช่วงตอบรับ)
        var lateRow = await db.Documents.AsNoTracking().Where(d => d.Id == late.Id).Select(d => new { d.SignLinkIssuedAt, d.PayLinkTokenHash }).SingleAsync();
        Assert.Null(lateRow.SignLinkIssuedAt);
        Assert.Null(lateRow.PayLinkTokenHash);
        // ทิศตรงข้าม: ใบส่งของไม่มีวันยืนราคา ⇒ ส่งได้
        var dn = await AddDocAsync(db, s, DocumentType.DeliveryNote, "DN", due: DateTime.UtcNow.Date.AddDays(-3));
        db.ChangeTracker.Clear();
        Assert.Contains("pay-document.html", (await svc.IssueLinkAsync(s.CompanyId, dn.Id, new IssueSignLinkOptions(Regenerate: true), "https://app.nextacc.test")).Url);
    }
}
