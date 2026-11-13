using System.Text;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.FileAttachment;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 267 แพ็ก P4 — "หลังจ่าย" บน PostgreSQL จริง (DOCUMENT_FLOW §2.12n-P4 · คำตัดสิน 267-1-8 · 267-1-44…50 · ทั้งสองทิศ):
/// ชั้นค่าตั้งต่อเอกสารของงานหลังรับชำระ (ใบเปิดทับผู้ติดต่อปิด และกลับกัน · ไม่ตั้ง = ตามชั้นถัดไป) · ปุ่มตั้งค่าของใบที่ออกแล้ว (audit · tenant) ·
/// ใบเสร็จบนหน้าเอกสารเฉพาะของการชำระของใบนั้น · สลิปที่ถูกปฏิเสธแสดงเหตุ (เหตุผลภายในไม่รั่ว) และแนบใหม่ได้ ·
/// ใบวางบิลแสดงเฉพาะใบในบิล + ปุ่มจ่ายรายใบ · 50 ทวิ จากหน้าเอกสารเข้าประตูเดียวของลิงก์อัปโหลดเดิม · แยกบริษัท
/// </summary>
[Trait("Category", "Db")]
public class PostPayRound267DbTests
{
    private readonly ITestOutputHelper _out;
    public PostPayRound267DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly SecretProtector Secrets = new("post-pay-267-db");

    private const string Ours = "0105556123453";
    private const string Payer = "0107556000116";

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = "https://app.nextacc.test", ["Jwt:Secret"] = "post-pay-267-db-test-secret-0123456789abcdef",
        }).Build();

    private static IPaymentProvider Provider(string code) => F4Proxy<IPaymentProvider>.Create((m, _) => m switch
    {
        "get_ProviderCode" => code,
        "get_Capabilities" => new PaymentCapabilities(new HashSet<PaymentMethodKind> { PaymentMethodKind.PromptPay, PaymentMethodKind.ManualSlip }, false, false, true),
        _ => null,
    });

    private sealed record Seed(Guid CompanyId, Guid ContactId, DocumentPayLinkService Links);

    private static async Task<Seed> SeedAsync(AccountingDbContext db, string tag, bool companyPostPay = false, bool? contactPostPay = null)
    {
        var company = new Company { Name = "บริษัท 267P4 " + tag, TaxId = Ours, Email = "office@seller267.co.th", FiscalYearStartMonth = 1 };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        db.CompanySettings.Add(new CompanySettings { CompanyId = company.Id, PostPayEmailReceipt = companyPostPay });
        var contact = new Contact
        {
            CompanyId = company.Id, Name = "บริษัท ลูกค้า 267P4 จำกัด", IsCustomer = true, ContactType = ContactType.JuristicPerson,
            TaxId = Payer, Email = "ap@buyer267.co.th", PostPayEmailReceipt = contactPostPay,
        };
        db.Contacts.Add(contact);
        const string code = "gw267p4";
        db.PaymentProviderConfigs.Add(new PaymentProviderConfig { CompanyId = company.Id, ProviderCode = code, IsActive = true, Mode = PaymentProviderMode.Test });
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id,
            new DocumentPayLinkService(db, new[] { Provider(code) }, Secrets, Config(), NullLogger<DocumentPayLinkService>.Instance));
    }

    private static async Task<Document> AddDocAsync(AccountingDbContext db, Seed s, DocumentType type, string prefix,
        DocumentStatus status = DocumentStatus.Approved, decimal balance = 1_070m, Guid? contactId = null, Action<Document>? tweak = null)
    {
        var doc = new Document
        {
            CompanyId = s.CompanyId, ContactId = contactId ?? s.ContactId, DocumentType = type,
            DocumentNumber = prefix + "267P4-" + Random.Shared.Next(100000, 999999), Status = status,
            DocumentDate = DateTime.UtcNow.Date.AddDays(-3),
            SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, PaidAmount = 1_070m - balance, BalanceDue = balance, Currency = "THB",
        };
        doc.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการ", Quantity = 1, Unit = "งาน", UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        tweak?.Invoke(doc);
        db.Documents.Add(doc);
        await db.SaveChangesAsync();
        return doc;
    }

    private static async Task<Payment> AddPaymentAsync(AccountingDbContext db, Seed s, Document doc, decimal amount = 1_070m)
    {
        var pay = new Payment
        {
            CompanyId = s.CompanyId, PaymentNumber = "PAY267P4-" + Random.Shared.Next(100000, 999999), DocumentId = doc.Id,
            PaymentDate = DateTime.UtcNow.Date, Amount = amount, PaymentMethod = PaymentMethod.BankTransfer,
        };
        db.Payments.Add(pay);
        await db.SaveChangesAsync();
        return pay;
    }

    /// <summary>ใบเสร็จของการชำระ (รูปเดียวกับที่ <c>IssueReceiptForPaymentAsync</c> เขียน: อ้างใบต้นทาง + การชำระ)</summary>
    private static async Task<Document> AddReceiptAsync(AccountingDbContext db, Seed s, Document source, Payment pay,
        DocumentStatus status = DocumentStatus.Paid, Guid? contactId = null, bool linkOnPayment = true)
    {
        var rec = new Document
        {
            CompanyId = s.CompanyId, ContactId = contactId ?? source.ContactId, DocumentType = DocumentType.Receipt,
            DocumentNumber = "REC267P4-" + Random.Shared.Next(100000, 999999), Status = status,
            DocumentDate = pay.PaymentDate, SubTotal = 1_070m, TotalAmount = 1_070m, PaidAmount = 1_070m, Currency = "THB",
            RelatedDocumentId = source.Id, IsSettlementReceipt = true, SettlementPaymentId = pay.Id, IsTaxInvoiceByLaw = false,
        };
        db.Documents.Add(rec);
        await db.SaveChangesAsync();
        if (linkOnPayment)
            await db.Payments.Where(p => p.Id == pay.Id).ExecuteUpdateAsync(x => x.SetProperty(p => p.ReceiptDocumentId, (Guid?)rec.Id));
        return rec;
    }

    private static string TokenOf(string url) => System.Web.HttpUtility.ParseQueryString(new Uri(url).Query)["t"]!;

    private static async Task<string> PageTokenAsync(Seed s, Guid documentId)
    {
        var link = await s.Links.EnsureCustomerLinkAsync(s.CompanyId, documentId, "เทสต์");
        Assert.True(link.Ok, link.Refusal);
        return TokenOf(link.Url!);
    }

    private static PostPaymentAutomationService Automation(AccountingDbContext db)
        => new(db, F4Proxy<IDocumentService>.Create((_, _) => null), null!, null!, NullLogger<PostPaymentAutomationService>.Instance);

    private static Task<int> RunCountAsync(AccountingDbContext db, Guid companyId, Guid paymentId)
        => db.PostPaymentAutomationRuns.AsNoTracking().CountAsync(r => r.CompanyId == companyId && r.PaymentId == paymentId);

    // ───────── S17: ชั้นเอกสาร — ทั้งสองทิศ + ไม่ตั้ง ─────────

    [Fact]
    public async Task PostPay267Db_DocOnOverridesContactOff_ใบเปิด_ผู้ติดต่อและบริษัทปิด_งานหลังรับชำระเข้าคิว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "on " + Guid.NewGuid().ToString("N")[..6], companyPostPay: false, contactPostPay: false);
        var inherit = await AddDocAsync(db, s, DocumentType.Invoice, "INV", DocumentStatus.Paid, balance: 0m);
        var on = await AddDocAsync(db, s, DocumentType.Invoice, "INV", DocumentStatus.Paid, balance: 0m, tweak: d => d.PostPayEmailReceipt = true);
        var payInherit = await AddPaymentAsync(db, s, inherit);
        var payOn = await AddPaymentAsync(db, s, on);

        // ไม่ตั้งที่ใบ ⇒ ตามผู้ติดต่อ (ปิด) — พฤติกรรมเดิม: ไม่มีแถวงาน
        Assert.False((await PostPaymentAutomationService.SettingsForAsync(db, s.CompanyId, inherit.Id, default)).EmailReceipt);
        Assert.False(await PostPaymentAutomationService.EnqueueCoreAsync(db, NullLogger.Instance, s.CompanyId, payInherit.Id, PaymentEntrySource.Manual, "tester", default));
        Assert.Equal(0, await RunCountAsync(db, s.CompanyId, payInherit.Id));

        // ตั้งเปิดที่ใบ ⇒ ชนะผู้ติดต่อ/บริษัทที่ปิด
        var state = await PostPaymentAutomationService.ResolveForAsync(db, s.CompanyId, on.Id, default);
        Assert.True(state.Resolution.Settings.EmailReceipt);
        Assert.Equal(PostPaymentAutomationPolicy.LayerDocument, state.Resolution.EmailReceiptLayer);
        Assert.True(await PostPaymentAutomationService.EnqueueCoreAsync(db, NullLogger.Instance, s.CompanyId, payOn.Id, PaymentEntrySource.Manual, "tester", default));
        var run = await db.PostPaymentAutomationRuns.AsNoTracking().SingleAsync(r => r.CompanyId == s.CompanyId && r.PaymentId == payOn.Id);
        Assert.Equal(PostPaymentAutomationPolicy.StatusPending, run.Status);
        Assert.Equal(on.Id, run.DocumentId);
    }

    [Fact]
    public async Task PostPay267Db_DocOffOverridesContactOn_ใบปิด_ผู้ติดต่อและบริษัทเปิด_ไม่มีงาน_ใบที่ไม่ตั้งยังเดินตามเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "off " + Guid.NewGuid().ToString("N")[..6], companyPostPay: true, contactPostPay: true);
        var inherit = await AddDocAsync(db, s, DocumentType.TaxInvoice, "TIV", DocumentStatus.Paid, balance: 0m);
        var off = await AddDocAsync(db, s, DocumentType.TaxInvoice, "TIV", DocumentStatus.Paid, balance: 0m, tweak: d => d.PostPayEmailReceipt = false);
        var payInherit = await AddPaymentAsync(db, s, inherit);
        var payOff = await AddPaymentAsync(db, s, off);

        Assert.False((await PostPaymentAutomationService.SettingsForAsync(db, s.CompanyId, off.Id, default)).EmailReceipt);
        Assert.False(await PostPaymentAutomationService.EnqueueCoreAsync(db, NullLogger.Instance, s.CompanyId, payOff.Id, PaymentEntrySource.Gateway, "tester", default));
        Assert.Equal(0, await RunCountAsync(db, s.CompanyId, payOff.Id));

        // ทิศตรงข้าม: ใบที่ไม่ตั้ง (ผู้ติดต่อเดียวกัน) ยังเข้าคิวตามผู้ติดต่อ — ชั้นเอกสารไม่กระทบใบอื่น
        var state = await PostPaymentAutomationService.ResolveForAsync(db, s.CompanyId, inherit.Id, default);
        Assert.True(state.Resolution.Settings.EmailReceipt);
        Assert.Equal(PostPaymentAutomationPolicy.LayerContact, state.Resolution.EmailReceiptLayer);
        Assert.True(await PostPaymentAutomationService.EnqueueCoreAsync(db, NullLogger.Instance, s.CompanyId, payInherit.Id, PaymentEntrySource.Gateway, "tester", default));
        Assert.Equal(1, await RunCountAsync(db, s.CompanyId, payInherit.Id));
    }

    [Fact]
    public async Task PostPay267Db_SetDocumentLayer_ปุ่มของใบที่ออกแล้ว_auditครั้งเดียว_ชนิดที่ตั้งไม่ได้ปฏิเสธ_ข้ามบริษัทไม่พบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "set " + Guid.NewGuid().ToString("N")[..6], companyPostPay: false, contactPostPay: false);
        var other = await SeedAsync(db, "set-other " + Guid.NewGuid().ToString("N")[..6]);
        var inv = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var qt = await AddDocAsync(db, s, DocumentType.Quotation, "QT");
        var svc = Automation(db);

        var before = await s.Links.StatusAsync(s.CompanyId, inv.Id);
        Assert.True(before.PostPaySettingApplies);
        Assert.Equal(AutopilotSettingsResolver.Inherit, before.PostPayReceiptMode);
        Assert.Contains("ไม่ออก/ไม่ส่งใบเสร็จอัตโนมัติ", before.PostPayEffectiveLabel);

        await svc.SetDocumentLayerAsync(s.CompanyId, inv.Id, "On", "Off", null, "tester@seller.test");
        var after = await s.Links.StatusAsync(s.CompanyId, inv.Id);
        Assert.Equal(AutopilotSettingsResolver.On, after.PostPayReceiptMode);
        Assert.Equal(AutopilotSettingsResolver.Off, after.PostPayPartialMode);
        Assert.Contains("ตั้งที่เอกสารใบนี้", after.PostPayEffectiveLabel);
        var stored = await db.Documents.AsNoTracking().Where(d => d.Id == inv.Id).Select(d => new { d.PostPayEmailReceipt, d.PostPayIncludePartial, d.DocumentNumber, d.TotalAmount }).SingleAsync();
        Assert.True(stored.PostPayEmailReceipt);
        Assert.False(stored.PostPayIncludePartial);
        Assert.Equal(inv.DocumentNumber, stored.DocumentNumber);          // ไม่แตะเลข/ยอดของใบที่ออกแล้ว
        Assert.Equal(1_070m, stored.TotalAmount);

        // กดซ้ำค่าเดิม = ไม่ลง audit ซ้ำ · ล้างกลับ = อีกหนึ่งแถว
        await svc.SetDocumentLayerAsync(s.CompanyId, inv.Id, "On", "Off", null, "tester@seller.test");
        var entityId = inv.Id.ToString();
        Assert.Equal(1, await db.AuditLogs.AsNoTracking().CountAsync(a => a.CompanyId == s.CompanyId && a.EntityId == entityId
            && a.NewValues != null && a.NewValues.Contains("post-pay-document-layer")));
        await svc.SetDocumentLayerAsync(s.CompanyId, inv.Id, "Inherit", "", null, "tester@seller.test");
        var cleared = await db.Documents.AsNoTracking().Where(d => d.Id == inv.Id).Select(d => new { d.PostPayEmailReceipt, d.PostPayIncludePartial }).SingleAsync();
        Assert.Null(cleared.PostPayEmailReceipt);
        Assert.Null(cleared.PostPayIncludePartial);
        Assert.Equal(2, await db.AuditLogs.AsNoTracking().CountAsync(a => a.CompanyId == s.CompanyId && a.EntityId == entityId
            && a.NewValues != null && a.NewValues.Contains("post-pay-document-layer")));

        // ชนิดที่ไม่รับชำระ: เปิด/ปิดถูกปฏิเสธ (ไม่เก็บค่าที่ไม่มีผู้อ่าน) · "ตามค่าตั้ง" ผ่าน · หน้าไม่แสดงช่อง
        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SetDocumentLayerAsync(s.CompanyId, qt.Id, "On", null, null, "tester"));
        Assert.Equal(400, refused.StatusCode);
        await svc.SetDocumentLayerAsync(s.CompanyId, qt.Id, "Inherit", null, null, "tester");
        Assert.False((await s.Links.StatusAsync(s.CompanyId, qt.Id)).PostPaySettingApplies);
        // ชื่อสถานะไม่รู้จัก = ปฏิเสธ (ไม่เดา)
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SetDocumentLayerAsync(s.CompanyId, inv.Id, "true", null, null, "tester"));
        // แยกบริษัท: ใบของบริษัทอื่น = ไม่พบ · ค่าไม่ถูกแตะ
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.SetDocumentLayerAsync(other.CompanyId, inv.Id, "On", null, null, "tester"));
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == inv.Id).Select(d => d.PostPayEmailReceipt).SingleAsync());
    }

    // ───────── ใบเสร็จบนหน้า: เฉพาะของการชำระของใบนั้น ─────────

    [Fact]
    public async Task CDP267Db_ReceiptOnPageOnlyForOwnDoc_ใบเสร็จของใบนี้เท่านั้น_ร่างและยกเลิกไม่แสดง_ป้ายeTaxเฉพาะส่งแล้ว_แยกบริษัท()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "rcpt " + Guid.NewGuid().ToString("N")[..6]);
        var other = await SeedAsync(db, "rcpt-other " + Guid.NewGuid().ToString("N")[..6]);
        var a = await AddDocAsync(db, s, DocumentType.Invoice, "INV", DocumentStatus.Paid, balance: 0m);
        var b = await AddDocAsync(db, s, DocumentType.Invoice, "INV", DocumentStatus.Paid, balance: 0m);
        var payA = await AddPaymentAsync(db, s, a);
        var payB = await AddPaymentAsync(db, s, b);
        var token = await PageTokenAsync(s, a.Id);

        // S16: รับชำระแล้ว ยังไม่มีใบเสร็จ ⇒ ไม่แต่งใบเสร็จ · บอกตามจริง · การเปิดหน้าไม่ออกใบเสร็จ
        var docsBefore = await db.Documents.CountAsync(d => d.CompanyId == s.CompanyId);
        var none = (await s.Links.PublicViewAsync(s.CompanyId, token))!.Page!;
        Assert.DoesNotContain(none.Related, r => r.Kind == CustomerDocPageAfterPay.KindReceipt);
        Assert.Contains("ยังไม่ได้ออกใบเสร็จ", none.AfterPay!.ReceiptNote);
        Assert.Equal(docsBefore, await db.Documents.CountAsync(d => d.CompanyId == s.CompanyId));
        Assert.Equal(0, await RunCountAsync(db, s.CompanyId, payA.Id));

        var recA = await AddReceiptAsync(db, s, a, payA);
        var recB = await AddReceiptAsync(db, s, b, payB);
        var draft = await AddReceiptAsync(db, s, a, await AddPaymentAsync(db, s, a, 1m), DocumentStatus.Draft);
        var voided = await AddReceiptAsync(db, s, a, await AddPaymentAsync(db, s, a, 1m), DocumentStatus.Voided);
        // ข้อมูลผิดรูป: ใบเสร็จผูกการชำระของใบนี้แต่เป็นของผู้ติดต่ออื่น ⇒ ไม่แสดง
        var stranger = new Contact { CompanyId = s.CompanyId, Name = "ลูกค้าอื่น 267P4", IsCustomer = true, ContactType = ContactType.JuristicPerson, TaxId = "0107537000017" };
        db.Contacts.Add(stranger);
        await db.SaveChangesAsync();
        var foreign = await AddReceiptAsync(db, s, a, await AddPaymentAsync(db, s, a, 1m), contactId: stranger.Id);

        var page = (await s.Links.PublicViewAsync(s.CompanyId, token))!.Page!;
        var receipt = Assert.Single(page.Related, r => r.Kind == CustomerDocPageAfterPay.KindReceipt);
        Assert.Equal(recA.Id, receipt.RelId);
        Assert.Equal(recA.DocumentNumber, receipt.DocumentNumber);
        Assert.Null(receipt.Note);                                        // S20: ยังไม่ส่ง e-Tax ⇒ ไม่มีป้าย
        Assert.False(receipt.PayOffered);
        Assert.Null(page.AfterPay!.ReceiptNote);                          // มีใบเสร็จแล้ว ⇒ ไม่มีข้อความ "ยังไม่ออก"
        foreach (var hidden in new[] { recB.Id, draft.Id, voided.Id, foreign.Id })
            Assert.DoesNotContain(page.Related, r => r.RelId == hidden);

        // ดาวน์โหลด: ใบเสร็จของใบนี้ได้ · ของใบอื่น (ผู้ติดต่อเดียวกัน) ไม่ได้ผ่าน token นี้
        Assert.Equal(recA.Id, await s.Links.ResolvePdfDocumentAsync(s.CompanyId, token, recA.Id));
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, token, recB.Id));
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, token, draft.Id));
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(s.CompanyId, token, foreign.Id));

        // S20: ส่ง e-Tax ทางอีเมลแล้ว ⇒ มีป้าย (วันที่ไทย)
        db.EtaxInvoices.Add(new EtaxInvoice
        {
            CompanyId = s.CompanyId, DocumentId = recA.Id, EtaxRefNumber = "ETAX267P4", XmlContent = "<x/>", SellerName = "ผู้ขาย", SellerTaxId = Ours,
            BuyerName = "ผู้ซื้อ", ByEmailSentAt = new DateTime(2026, 11, 12, 18, 30, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        var sent = (await s.Links.PublicViewAsync(s.CompanyId, token))!.Page!;
        Assert.Contains("13/11/2569", Assert.Single(sent.Related, r => r.Kind == CustomerDocPageAfterPay.KindReceipt).Note);

        // พอร์ทัลเปิดหน้าเดียวกัน: ใบเสร็จของการชำระ (ชุดเดียวกัน) · การ์ด LINE = ไม่มีส่วนหลังจ่าย/ใบที่เกี่ยวข้อง
        var portal = (await s.Links.PageForDocumentAsync(s.CompanyId, a.Id, CustomerDocPage.Mode.Portal))!.Page!;
        Assert.Equal(recA.Id, Assert.Single(portal.Related).RelId);
        Assert.NotNull(portal.AfterPay);
        Assert.Null(portal.AfterPay!.WhtCert);
        var line = (await s.Links.PageForDocumentAsync(s.CompanyId, a.Id, CustomerDocPage.Mode.LineView))!.Page!;
        Assert.Empty(line.Related);
        Assert.Null(line.AfterPay);

        // แยกบริษัท: token ของบริษัทนี้กับรหัสบริษัทอื่น = ไม่พบ
        Assert.Null(await s.Links.PublicViewAsync(other.CompanyId, token));
        Assert.Null(await s.Links.ResolvePdfDocumentAsync(other.CompanyId, token, recA.Id));
    }

    // ───────── S19: สลิปถูกปฏิเสธ ─────────

    [Fact]
    public async Task CDP267Db_RejectedSlipResubmit_รอตรวจไม่ชวนแนบซ้ำ_ปฏิเสธแสดงเหตุถึงลูกค้า_เหตุผลภายในไม่รั่ว_แนบใหม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "slip " + Guid.NewGuid().ToString("N")[..6]);
        var inv = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var otherInv = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var token = await PageTokenAsync(s, inv.Id);
        PaymentIntent Slip(Guid docId, PaymentIntentStatus status) => new()
        {
            CompanyId = s.CompanyId, ProviderCode = "manual_slip", SourceKind = PaymentSourceKind.Document, SourceId = docId, ContactId = s.ContactId,
            Amount = 1_070m, Currency = "THB", Status = status, MethodKind = PaymentMethodKind.ManualSlip,
        };

        Assert.Empty((await s.Links.PublicViewAsync(s.CompanyId, token))!.Page!.AfterPay!.Slips);

        var slip = Slip(inv.Id, PaymentIntentStatus.Pending);
        db.PaymentIntents.Add(slip);
        db.PaymentIntents.Add(Slip(otherInv.Id, PaymentIntentStatus.Failed));     // สลิปของใบอื่น — ต้องไม่ขึ้นหน้านี้
        await db.SaveChangesAsync();
        var pending = (await s.Links.PublicViewAsync(s.CompanyId, token))!.Page!;
        Assert.True(pending.AfterPay!.SlipPending);
        var row = Assert.Single(pending.AfterPay.Slips);
        Assert.Equal(CustomerDocPageAfterPay.SlipPending, row.State);
        Assert.Equal(1_070m, row.Amount);
        // สลิปอย่างเดียวไม่เปลี่ยนสถานะใบ (ยังค้างชำระเต็ม)
        Assert.Equal(1_070m, pending.BalanceDue);
        Assert.Equal(DocumentStatus.Approved, await db.Documents.AsNoTracking().Where(d => d.Id == inv.Id).Select(d => d.Status).SingleAsync());

        // พนักงานปฏิเสธ: เหตุผลภายในลงประวัติ · ข้อความถึงลูกค้าแยกช่อง
        await db.PaymentIntents.Where(i => i.Id == slip.Id).ExecuteUpdateAsync(x => x
            .SetProperty(i => i.Status, PaymentIntentStatus.Failed)
            .SetProperty(i => i.FailureCode, "slip_rejected")
            .SetProperty(i => i.FailureMessage, "สงสัยสลิปปลอม — แจ้งฝ่ายตรวจสอบภายใน")
            .SetProperty(i => i.CustomerVisibleRejectReason, "ยอดโอนไม่ตรงกับยอดในเอกสาร กรุณาโอนส่วนต่างแล้วแนบใหม่"));
        var rejected = (await s.Links.PublicViewAsync(s.CompanyId, token))!.Page!;
        Assert.False(rejected.AfterPay!.SlipPending);                     // ไม่มีสลิปรอตรวจ ⇒ ฟอร์มแนบกลับมา
        var shown = Assert.Single(rejected.AfterPay.Slips);
        Assert.Equal(CustomerDocPageAfterPay.SlipRejected, shown.State);
        Assert.Equal("ยอดโอนไม่ตรงกับยอดในเอกสาร กรุณาโอนส่วนต่างแล้วแนบใหม่", shown.Note);
        Assert.DoesNotContain("สลิปปลอม", shown.Note + shown.Label);
        // ด่านรับสลิปเดิม: ไม่มีรายการรอตรวจแล้ว ⇒ รับสลิปใหม่ได้ (ตัวตัดสินเดียวกับเส้น POST …/slip)
        var openStatuses = PaymentIntentPolicy.OpenStatuses;
        Assert.False(await db.PaymentIntents.AsNoTracking().AnyAsync(i => i.CompanyId == s.CompanyId && i.SourceId == inv.Id
            && i.MethodKind == PaymentMethodKind.ManualSlip && openStatuses.Contains(i.Status)));
        Assert.Null(SlipEvidenceRule.PayLinkSlipProblem(true, false, 1));

        // ทิศตรงข้าม: ปฏิเสธโดยไม่พิมพ์ถึงลูกค้า ⇒ ข้อความกลาง · แนบใหม่ ⇒ แถวใหม่รอตรวจอยู่บนสุด
        await db.PaymentIntents.Where(i => i.Id == slip.Id).ExecuteUpdateAsync(x => x.SetProperty(i => i.CustomerVisibleRejectReason, (string?)null));
        var second = Slip(inv.Id, PaymentIntentStatus.Pending);
        second.CreatedAt = DateTime.UtcNow.AddMinutes(1);
        db.PaymentIntents.Add(second);
        await db.SaveChangesAsync();
        var again = (await s.Links.PublicViewAsync(s.CompanyId, token))!.Page!;
        Assert.True(again.AfterPay!.SlipPending);
        Assert.Equal(2, again.AfterPay.Slips.Count);
        Assert.Contains(again.AfterPay.Slips, x => x.State == CustomerDocPageAfterPay.SlipPending);
        Assert.Equal(CustomerDocPageAfterPay.SlipState(false, PaymentIntentStatus.Failed, null).Note, again.AfterPay.Slips.Single(x => x.State == CustomerDocPageAfterPay.SlipRejected).Note);
    }

    // ───────── S24: ใบวางบิล ─────────

    [Fact]
    public async Task CDP267Db_BillingNoteListsInvoices_เฉพาะใบในบิล_ปุ่มจ่ายเฉพาะใบที่ค้าง_เปิดหน้าของใบนั้นผ่านตัวออกเดียว_ยกเลิกลิงก์แล้วไม่ออกใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "bill " + Guid.NewGuid().ToString("N")[..6]);
        var other = await SeedAsync(db, "bill-other " + Guid.NewGuid().ToString("N")[..6]);
        var inv1 = await AddDocAsync(db, s, DocumentType.Invoice, "INV");
        var inv2 = await AddDocAsync(db, s, DocumentType.Invoice, "INV", DocumentStatus.PartiallyPaid, balance: 500m);
        var paid = await AddDocAsync(db, s, DocumentType.Invoice, "INV", DocumentStatus.Paid, balance: 0m);
        var notBilled = await AddDocAsync(db, s, DocumentType.Invoice, "INV");                       // ผู้ติดต่อเดียวกัน แต่ไม่อยู่ในบิล
        var stranger = new Contact { CompanyId = s.CompanyId, Name = "ลูกค้าอื่น 267P4", IsCustomer = true, ContactType = ContactType.JuristicPerson, TaxId = "0107537000017" };
        db.Contacts.Add(stranger);
        await db.SaveChangesAsync();
        var foreign = await AddDocAsync(db, s, DocumentType.Invoice, "INV", contactId: stranger.Id);  // บรรทัดของบิลชี้ใบของผู้ติดต่ออื่น (ข้อมูลผิดรูป)
        var bill = await AddDocAsync(db, s, DocumentType.BillingNote, "BN", tweak: d =>
        {
            d.Lines.Clear();
            var order = 1;
            foreach (var x in new[] { inv1, inv2, paid, foreign })
                d.Lines.Add(new DocumentLine { LineOrder = order++, Description = "ใบแจ้งหนี้ " + x.DocumentNumber, Quantity = 1, UnitPrice = 1_070m, Amount = 1_070m, SourceDocumentId = x.Id });
        });
        var token = await PageTokenAsync(s, bill.Id);

        var page = (await s.Links.PublicViewAsync(s.CompanyId, token))!.Page!;
        var billed = page.Related.Where(r => r.Kind == "billed").ToList();
        Assert.Equal(new[] { inv1.Id, inv2.Id, paid.Id }.OrderBy(x => x), billed.Select(r => r.RelId).OrderBy(x => x));
        Assert.DoesNotContain(page.Related, r => r.RelId == notBilled.Id || r.RelId == foreign.Id);
        Assert.True(billed.Single(r => r.RelId == inv1.Id).PayOffered);
        Assert.True(billed.Single(r => r.RelId == inv2.Id).PayOffered);
        Assert.Equal(500m, billed.Single(r => r.RelId == inv2.Id).BalanceDue);
        Assert.False(billed.Single(r => r.RelId == paid.Id).PayOffered);                              // จ่ายแล้ว = ไม่มีปุ่ม
        Assert.False(string.IsNullOrWhiteSpace(billed.Single(r => r.RelId == inv1.Id).PayLabel));
        // ใบวางบิลเองไม่มีการจ่ายรวม (267-1-9 v1)
        Assert.DoesNotContain(page.Actions, a => a.Id == CustomerDocPage.ActionPayOnline && a.Enabled);

        // ปุ่ม "ชำระใบนี้": ได้ที่อยู่หน้าเอกสารของใบนั้น (token ของใบนั้นเอง — ผ่านตัวออกเดียว)
        Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == inv1.Id).Select(d => d.PayLinkTokenHash).SingleAsync());
        var open = await s.Links.RelatedPageLinkAsync(s.CompanyId, token, inv1.Id);
        Assert.True(open.Found);
        Assert.StartsWith(DocumentPayLinkPolicy.PagePath + "?c=" + s.CompanyId.ToString("D") + "&t=", open.Path);
        var invToken = open.Path![(open.Path!.IndexOf("&t=", StringComparison.Ordinal) + 3)..];
        Assert.NotEqual(token, invToken);
        var invView = (await s.Links.PublicViewAsync(s.CompanyId, invToken))!;
        Assert.Equal(inv1.DocumentNumber, invView.DocumentNumber);
        Assert.Equal(DocumentPayLinkPolicy.TokenHash(invToken),
            await db.Documents.AsNoTracking().Where(d => d.Id == inv1.Id).Select(d => d.PayLinkTokenHash).SingleAsync());
        // กดซ้ำ = ลิงก์เดิม (ไม่ออก token ใหม่ทุกครั้ง)
        Assert.Equal(open.Path, (await s.Links.RelatedPageLinkAsync(s.CompanyId, token, inv1.Id)).Path);

        // ทิศตรงข้าม: ใบที่จ่ายแล้ว · ใบนอกบิล · ใบของผู้ติดต่ออื่น · รหัสมั่ว · บริษัทอื่น ⇒ ไม่พบ และไม่มีลิงก์ถูกออก
        foreach (var id in new[] { paid.Id, notBilled.Id, foreign.Id, Guid.NewGuid() })
            Assert.False((await s.Links.RelatedPageLinkAsync(s.CompanyId, token, id)).Found);
        Assert.False((await s.Links.RelatedPageLinkAsync(other.CompanyId, token, inv2.Id)).Found);
        Assert.False((await s.Links.RelatedPageLinkAsync(s.CompanyId, DocumentPayLinkPolicy.TokenHash(token), inv2.Id)).Found);
        foreach (var id in new[] { paid.Id, notBilled.Id, foreign.Id, inv2.Id })
            Assert.Null(await db.Documents.AsNoTracking().Where(d => d.Id == id).Select(d => d.PayLinkTokenHash).SingleAsync());

        // ผู้ขายยกเลิกลิงก์ของใบนั้นไว้ ⇒ คำขอของลูกค้าไม่ออกใหม่ทับ (บอกให้ติดต่อผู้ขาย)
        await s.Links.RevokeAsync(s.CompanyId, inv1.Id, "tester");
        var revoked = await s.Links.RelatedPageLinkAsync(s.CompanyId, token, inv1.Id);
        Assert.True(revoked.Found);
        Assert.Null(revoked.Path);
        Assert.Equal(CustomerDocPageAfterPay.RelatedLinkRefusedMessage(false), revoked.Message);
        Assert.NotNull(await db.Documents.AsNoTracking().Where(d => d.Id == inv1.Id).Select(d => d.PayLinkRevokedAt).SingleAsync());
    }

    // ───────── 50 ทวิ จากหน้าเอกสาร ─────────

    private sealed class FilesProbe
    {
        public int Uploads;
        public IFileAttachmentService For(AccountingDbContext db) => F4Proxy<IFileAttachmentService>.Create((m, a) =>
        {
            if (m != nameof(IFileAttachmentService.UploadAnonymousBytesAsync)) return null;
            Uploads++;
            var att = new FileAttachment
            {
                CompanyId = (Guid)a[0]!, EntityType = (string)a[1]!, EntityId = (Guid)a[2]!, FileName = "f", OriginalFileName = (string)a[3]!,
                ContentType = (string)a[4]!, FileSize = ((byte[])a[5]!).LongLength, StoragePath = "mem/" + Guid.NewGuid(),
            };
            db.FileAttachments.Add(att);
            db.SaveChanges();
            return new FileAttachmentResponse(att.Id, att.FileName, att.OriginalFileName, att.ContentType, att.FileSize, att.StoragePath, null,
                att.EntityType, att.EntityId, "", att.CreatedAt);
        });
    }

    private static (WhtCertUploadLinkService Svc, FilesProbe Files) Wht(AccountingDbContext db)
    {
        var intake = new WhtCertIntakeService(db,
            F4Proxy<IAiOrchestrator>.Create((_, _) => new AiResponse { Status = AiCallStatus.NoProvider, UsedAi = false }),
            F4Proxy<IAiFeedbackRecorder>.Create((_, _) => null),
            new WhtCreditService(db, NullLogger<WhtCreditService>.Instance), NullLogger<WhtCertIntakeService>.Instance);
        var files = new FilesProbe();
        var ocrDown = F4Proxy<IOcrService>.Create((m, _) =>
            m == nameof(IOcrService.ScanAsync) ? throw new InvalidOperationException("OCR ล่ม (เทสต์)") : (object?)null);
        return (new WhtCertUploadLinkService(db, intake, ocrDown, files.For(db), NullLogger<WhtCertUploadLinkService>.Instance, Config()), files);
    }

    private static byte[] Pdf(string tag) => Encoding.ASCII.GetBytes("%PDF-1.7\n1 0 obj << /Type /Catalog >> endobj\n% " + tag + "\n%%EOF");

    [Fact]
    public async Task CDP267Db_WhtCertFromPage_ใบที่รอ50ทวิมีฟอร์ม_ไฟล์เข้าประตูเดียวช่องทางลิงก์อัปโหลด_ใบที่ไม่รอปฏิเสธ_แยกบริษัท()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, "wht " + Guid.NewGuid().ToString("N")[..6]);
        var other = await SeedAsync(db, "wht-other " + Guid.NewGuid().ToString("N")[..6]);
        var withheld = await AddDocAsync(db, s, DocumentType.TaxInvoice, "TIV", DocumentStatus.Paid, balance: 0m, tweak: d =>
        {
            d.DocumentDate = DateTime.UtcNow.Date.AddDays(-10);
            d.TotalAmount = 1_040m; d.WithholdingTaxAmount = 30m; d.PaidAmount = 1_040m;
        });
        var plain = await AddDocAsync(db, s, DocumentType.TaxInvoice, "TIV", DocumentStatus.Paid, balance: 0m);
        db.WhtCreditsReceived.Add(new WhtCreditReceived
        {
            CompanyId = s.CompanyId, TaxYear = DateTime.UtcNow.Year, PayerName = "บริษัท ลูกค้า 267P4 จำกัด", PayerTaxId = Payer, PayerContactId = s.ContactId,
            DocumentId = withheld.Id, IncomeAmount = 1_000m, WhtRate = 3m, WhtAmount = 30m, Status = WhtCreditStatus.Pending,
            FirstWithheldAt = DateTime.UtcNow.AddDays(-10),
        });
        await db.SaveChangesAsync();
        var token = await PageTokenAsync(s, withheld.Id);
        var plainToken = await PageTokenAsync(s, plain.Id);
        var (wht, files) = Wht(db);

        var page = (await s.Links.PublicViewAsync(s.CompanyId, token))!.Page!;
        Assert.NotNull(page.AfterPay!.WhtCert);
        Assert.True(page.AfterPay.WhtCert!.CanUpload);
        Assert.Equal(30m, page.AfterPay.WhtCert.Missing);
        var target = await s.Links.WhtCertTargetAsync(s.CompanyId, token);
        Assert.NotNull(target);
        Assert.Equal(withheld.Id, target!.Value.DocumentId);
        Assert.Equal(s.ContactId, target.Value.ContactId);

        // ทิศตรงข้าม: ใบที่ไม่ได้รอ 50 ทวิ ⇒ ไม่มีการ์ด · ไม่มีเป้าหมาย · ส่งไฟล์ถูกปฏิเสธก่อนเก็บ
        Assert.Null((await s.Links.PublicViewAsync(s.CompanyId, plainToken))!.Page!.AfterPay!.WhtCert);
        Assert.Null(await s.Links.WhtCertTargetAsync(s.CompanyId, plainToken));
        var no = await Assert.ThrowsAsync<BusinessRuleException>(() => wht.UploadForDocumentAsync(s.CompanyId, s.ContactId, plain.Id, Pdf("x"), null));
        Assert.Equal(409, no.StatusCode);
        // แยกบริษัท / token ปลอม
        Assert.Null(await s.Links.WhtCertTargetAsync(other.CompanyId, token));
        Assert.Null(await s.Links.WhtCertTargetAsync(s.CompanyId, DocumentPayLinkPolicy.TokenHash(token)));
        Assert.Equal(0, files.Uploads);
        Assert.False(await db.WhtCertIntakes.AnyAsync(i => i.CompanyId == s.CompanyId));

        // ไฟล์ที่ไม่ใช่ PDF/รูป ⇒ ปฏิเสธก่อนเก็บ (ด่านไบต์เดียวกับลิงก์อัปโหลด)
        var bad = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            wht.UploadForDocumentAsync(s.CompanyId, s.ContactId, withheld.Id, Encoding.ASCII.GetBytes("MZ fake exe"), null));
        Assert.Equal(400, bad.StatusCode);
        Assert.Equal(0, files.Uploads);

        var pdf = Pdf(Guid.NewGuid().ToString("N"));
        var r = await wht.UploadForDocumentAsync(s.CompanyId, target.Value.ContactId, target.Value.DocumentId, pdf, "ฝ่ายบัญชีลูกค้า");
        Assert.True(r.Received);
        Assert.False(r.Duplicate);
        var intake = await db.WhtCertIntakes.AsNoTracking().SingleAsync(i => i.CompanyId == s.CompanyId);
        Assert.Equal(WhtCertIntakeChannel.UploadLink, intake.Channel);                // ประตูเดียว · ช่องทางเดียวกับลิงก์อัปโหลด
        Assert.Equal(WhtCertIntakeRecordStatus.AwaitingConfirm, intake.Status);       // รอผู้ขายตรวจ — ไม่ยืนยันเอง
        Assert.False(intake.AutoConfirmed);
        Assert.Equal(WhtCertUploadLinkPolicy.Sha256Hex(pdf), intake.FileSha256);
        Assert.Equal(1, files.Uploads);
        // ทะเบียนเครดิตยังรอใบ (การส่งไฟล์ไม่ประทับ "ได้รับใบแล้ว")
        Assert.Equal(WhtCreditStatus.Pending, await db.WhtCreditsReceived.AsNoTracking().Where(w => w.DocumentId == withheld.Id).Select(w => w.Status).SingleAsync());
        // ไม่มีแถวลิงก์อัปโหลดใหม่ (สิทธิ์ = token ของใบ — ไม่มีระบบลิงก์ที่สอง)
        Assert.False(await db.WhtCertUploadLinks.AnyAsync(l => l.CompanyId == s.CompanyId));
    }
}
