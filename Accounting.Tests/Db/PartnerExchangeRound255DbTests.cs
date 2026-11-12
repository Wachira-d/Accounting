using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 255 WP-12B ข้อ 6–8 — เส้นข้ามบริษัทจริงบน PostgreSQL (ต่อยอด <c>CrossTenantWorkflowService</c> เดิม):
/// ใบขายของผู้ขาย S ที่ส่งถึงผู้ซื้อ B ⇒ ใบซื้อ<b>ร่าง</b>ของ B (สำเนา — แถวใหม่ของ B ทั้งหมด) · <b>ทั้งสองทิศ</b>: B อ่านเอกสาร/ผู้ติดต่อของ S ไม่ได้นอกจากสำเนา ·
/// S ไม่เห็นใบร่างของ B · ค่าตั้งต้นปิด/เกินเพดาน ⇒ ไม่สร้าง (เหตุบนแถวกล่องเข้า) · ยืนยันต้นฉบับ · สถานะการเชื่อมไม่รั่ว ·
/// 50 ทวิ ที่ B ออก ⇒ ประตูเดียวของ S (ประทับส่งแล้วหลังได้ผลจริง) · ลิงก์วิเศษพอร์ทัลใช้ครั้งเดียว
/// <para>seed pitfalls: บริษัทใหม่ทุกเทสต์ · เลขภาษีสุ่ม (ฐานร่วม — เลขคงที่ชนบริษัทของเทสต์อื่น = กำกวม) · กล่องรับ 50 ทวิ ปลอม (ประตูจริงมีเทสต์ของตัวเอง WC252)</para>
/// </summary>
[Trait("Category", "Db")]
public class PartnerExchangeRound255DbTests
{
    private readonly ITestOutputHelper _out;
    public PartnerExchangeRound255DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static readonly Random Rng = new();
    private static string RandTax()
    {
        lock (Rng) return "09" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Rng.Next(10))));
    }

    private static CrossTenantWorkflowService Svc(AccountingDbContext db) => new(db, NullLogger<CrossTenantWorkflowService>.Instance);

    private sealed record Pair(Company Seller, Company Buyer, Guid PartnershipId, Contact BuyerAtSeller, Document Invoice);

    private static async Task<Pair> SeedAsync(AccountingDbContext db, bool buyerDrafts, decimal? buyerLimit = null, bool accepted = true)
    {
        var seller = new Company { Name = "ผู้ขาย 255 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000", Address = "1 ถนนสาทร กรุงเทพฯ" };
        var buyer = new Company { Name = "ผู้ซื้อ 255 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000" };
        db.Companies.AddRange(seller, buyer);
        await db.SaveChangesAsync();
        var (a, b) = seller.Id.CompareTo(buyer.Id) < 0 ? (seller.Id, buyer.Id) : (buyer.Id, seller.Id);
        var p = new TradingPartnership
        {
            CompanyAId = a, CompanyBId = b, InvitedByCompanyId = seller.Id,
            Status = accepted ? TradingPartnershipStatus.Accepted : TradingPartnershipStatus.Pending,
        };
        if (a == buyer.Id) p.PurchaseDraftLimitA = buyerLimit; else p.PurchaseDraftLimitB = buyerLimit;
        db.TradingPartnerships.Add(p);
        db.WorkflowAutomationConfigs.Add(new WorkflowAutomationConfig { CompanyId = buyer.Id, CreatePurchaseDraftsFromPartners = buyerDrafts });
        var contact = new Contact { CompanyId = seller.Id, Name = buyer.Name, TaxId = buyer.TaxId, BranchCode = "00000", IsCustomer = true, Email = "ap@buyer-internal.test" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var inv = new Document
        {
            CompanyId = seller.Id, ContactId = contact.Id, DocumentType = DocumentType.TaxInvoice, DocumentNumber = "TIV255-" + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m,
            Currency = "THB", InternalNotes = "ต้นทุนภายใน 600 — ห้ามข้ามบริษัท",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการวางระบบ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        return new Pair(seller, buyer, p.Id, contact, inv);
    }

    [Fact]
    public async Task R255_ใบขายถึงคู่ค้า_เป็นใบซื้อร่างของผู้รับ_สำเนาเท่านั้น_สองทิศ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, buyerDrafts: true);

        var link = await Svc(db).OnDocumentSentAsync(s.Invoice);
        Assert.NotNull(link);

        using var check = DbTestDatabase.TryCreateContext()!;
        var draft = await check.Documents.AsNoTracking().Include(d => d.Lines)
            .SingleAsync(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId == link!.Id);
        Assert.Equal(DocumentType.PurchaseInvoice, draft.DocumentType);
        Assert.Equal(DocumentStatus.Draft, draft.Status);                 // N3: ร่างเสมอ
        Assert.StartsWith("DRAFT-", draft.DocumentNumber);
        Assert.Equal(s.Invoice.DocumentNumber, draft.SupplierInvoiceNumber);
        Assert.Equal(1_070m, draft.TotalAmount);
        Assert.Equal(PartnerInputVatEvidence.PaperRequired, draft.PartnerInputVatEvidence);   // ไม่มี e-Tax ที่ลงลายมือชื่อ ⇒ ต้องใช้ต้นฉบับ
        Assert.Null(draft.InternalNotes);                                   // หมายเหตุภายในของผู้ขายไม่ข้าม
        // สำเนาเป็นแถวของผู้รับทั้งหมด — ผู้ติดต่อของ B · บรรทัดใหม่ (ไม่ใช่แถวของ S)
        var supplier = await check.Contacts.AsNoTracking().IgnoreQueryFilters().SingleAsync(c => c.Id == draft.ContactId);
        Assert.Equal(s.Buyer.Id, supplier.CompanyId);
        Assert.Equal(s.Seller.TaxId, supplier.TaxId);
        Assert.NotEqual("ap@buyer-internal.test", supplier.Email);   // ข้อมูลผู้ติดต่อของผู้ขายไม่ข้าม
        var srcLineIds = s.Invoice.Lines.Select(l => l.Id).ToHashSet();
        Assert.All(draft.Lines, l => Assert.DoesNotContain(l.Id, srcLineIds));
        Assert.Equal((Guid?)draft.Id, (await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == link!.Id)).TargetDocumentId);

        // ทิศ B → S: ข้อมูลของ B (ตามบริษัท) ไม่มีเอกสาร/ผู้ติดต่อของ S
        Assert.False(await check.Documents.AnyAsync(d => d.CompanyId == s.Buyer.Id && d.Id == s.Invoice.Id));
        Assert.False(await check.Contacts.IgnoreQueryFilters().AnyAsync(c => c.CompanyId == s.Buyer.Id && c.Id == s.BuyerAtSeller.Id));
        Assert.Single(await Svc(check).IncomingPurchaseDraftsAsync(s.Buyer.Id));
        // ทิศ S → B: ผู้ขายไม่เห็นใบร่างของผู้ซื้อ · เอกสารต้นทางไม่ถูกแตะ
        Assert.Empty(await Svc(check).IncomingPurchaseDraftsAsync(s.Seller.Id));
        Assert.False(await check.Documents.AnyAsync(d => d.CompanyId == s.Seller.Id && d.PartnerSourceLinkId != null));
        var src = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == s.Invoice.Id);
        Assert.Equal(s.Seller.Id, src.CompanyId);
        Assert.Equal(DocumentStatus.Approved, src.Status);
        // ยืนยันต้นฉบับข้ามบริษัทไม่ได้ (S ใช้รหัสใบร่างของ B)
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Svc(check).ConfirmPartnerOriginalAsync(s.Seller.Id, draft.Id, Guid.NewGuid()));

        // ส่งซ้ำ ⇒ ไม่มีใบร่างที่สอง
        using var again = DbTestDatabase.TryCreateContext()!;
        await Svc(again).OnDocumentSentAsync(await again.Documents.Include(d => d.Lines).SingleAsync(d => d.Id == s.Invoice.Id));
        Assert.Equal(1, await again.Documents.CountAsync(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId != null));
    }

    [Fact]
    public async Task R255_ทิศตรงข้าม_ผู้รับไม่เปิด_ไม่สร้าง_เกินเพดาน_ไม่สร้างพร้อมเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var off = await SeedAsync(db, buyerDrafts: false);
        var offLink = await Svc(db).OnDocumentSentAsync(off.Invoice);
        Assert.NotNull(offLink);   // กล่องเข้าเดิมยังทำงาน
        var capped = await SeedAsync(db, buyerDrafts: true, buyerLimit: 1_000m);
        var capLink = await Svc(db).OnDocumentSentAsync(capped.Invoice);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.Documents.AnyAsync(d => d.CompanyId == off.Buyer.Id && d.PartnerSourceLinkId != null));
        Assert.False(await check.Documents.AnyAsync(d => d.CompanyId == capped.Buyer.Id && d.PartnerSourceLinkId != null));
        var l = await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(x => x.Id == capLink!.Id);
        Assert.Null(l.TargetDocumentId);
        Assert.Contains("เกินเพดาน", l.Comment);
    }

    private static async Task<Document> SellerDocAsync(AccountingDbContext db, Pair s, DocumentType type, Guid? relatedTo = null, Guid? replaces = null)
    {
        var d = new Document
        {
            CompanyId = s.Seller.Id, ContactId = s.BuyerAtSeller.Id, DocumentType = type, DocumentNumber = "S255-" + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m,
            Currency = "THB", RelatedDocumentId = relatedTo, ReplacesDocumentId = replaces,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการวางระบบ", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    [Fact]
    public async Task R255_ฝ่ายค้าน_H3_ใบกำกับที่แปลง_ออกแทน_จากใบเดิม_ผูกใบซื้อเดิม_ไม่สร้างใบที่สอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, buyerDrafts: true);
        var l1 = await Svc(db).OnDocumentSentAsync(s.Invoice);
        // ใบที่แปลงจากใบเดิม (RelatedDocumentId) + ใบที่ออกแทนใบนั้นอีกชั้น (ReplacesDocumentId) = การขายครั้งเดียวกัน
        var converted = await SellerDocAsync(db, s, DocumentType.TaxInvoice, relatedTo: s.Invoice.Id);
        var l2 = await Svc(db).OnDocumentSentAsync(converted);
        var replacement = await SellerDocAsync(db, s, DocumentType.TaxInvoice, replaces: converted.Id);
        var l3 = await Svc(db).OnDocumentSentAsync(replacement);

        using var check = DbTestDatabase.TryCreateContext()!;
        var drafts = await check.Documents.AsNoTracking().Where(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId != null).ToListAsync();
        var only = Assert.Single(drafts);
        Assert.Equal(only.PartnerSourceLinkId, (Guid?)l1!.Id);
        Assert.Equal(replacement.DocumentNumber, only.SupplierInvoiceNumber);   // ร่างยังแก้ได้ ⇒ เลขใบกำกับจริงของผู้ขาย
        foreach (var l in new[] { l2!, l3! })
            Assert.Equal((Guid?)only.Id, (await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(x => x.Id == l.Id)).TargetDocumentId);
        Assert.True(PartnerInputVatEvidence.BlocksClaim(only.PartnerInputVatEvidence));   // ไม่ปลดด่านเอง (H2)
    }

    [Fact]
    public async Task R255_ฝ่ายค้าน_H3_ทิศตรงข้าม_ใบกำกับใหม่ที่ไม่เกี่ยวกัน_ได้ใบซื้อใหม่()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, buyerDrafts: true);
        await Svc(db).OnDocumentSentAsync(s.Invoice);
        var unrelated = await SellerDocAsync(db, s, DocumentType.TaxInvoice);
        await Svc(db).OnDocumentSentAsync(unrelated);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(2, await check.Documents.CountAsync(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId != null));
    }

    [Fact]
    public async Task R255_ฝ่ายค้าน_M2_ผู้ขายยกเลิกต้นทาง_ใบซื้อติดธง_ยืนยันไม่ได้_ทิศตรงข้ามต้นทางมีผลยืนยันได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, buyerDrafts: true);
        var link = await Svc(db).OnDocumentSentAsync(s.Invoice);
        var other = await SeedAsync(db, buyerDrafts: true);
        var otherLink = await Svc(db).OnDocumentSentAsync(other.Invoice);

        // ยกเลิกโดยไม่ผ่านเส้นแจ้ง ⇒ ด่านยืนยันตรวจต้นทางสดเอง
        await db.Documents.Where(d => d.Id == s.Invoice.Id).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Voided));
        using var c1 = DbTestDatabase.TryCreateContext()!;
        var draftId = await c1.Documents.Where(d => d.PartnerSourceLinkId == link!.Id).Select(d => d.Id).SingleAsync();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(c1).ConfirmPartnerOriginalAsync(s.Buyer.Id, draftId, Guid.NewGuid()));
        Assert.Equal(PartnerInputVatEvidence.SourceVoidedMessage, ex.Message);
        // เส้นแจ้งหลังยกเลิก ⇒ ธง + แถวกล่องเข้า Withdrawn
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await Svc(c2).OnSourceVoidedAsync(s.Seller.Id, s.Invoice.Id));
        using var c3 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(PartnerInputVatEvidence.SourceVoided, await c3.Documents.Where(d => d.Id == draftId).Select(d => d.PartnerInputVatEvidence).SingleAsync());
        Assert.Equal(CrossTenantLinkStatus.Withdrawn, (await c3.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.Id == link!.Id)).Status);
        // ทิศตรงข้าม: บริษัทอื่น/ใบอื่นไม่ถูกแตะ · ต้นทางยังมีผล ⇒ ยืนยันได้
        var otherDraft = await c3.Documents.Where(d => d.PartnerSourceLinkId == otherLink!.Id).Select(d => d.Id).SingleAsync();
        Assert.Equal(PartnerInputVatEvidence.PaperRequired, await c3.Documents.Where(d => d.Id == otherDraft).Select(d => d.PartnerInputVatEvidence).SingleAsync());
        Assert.Equal(PartnerInputVatEvidence.PaperConfirmed, (await Svc(c3).ConfirmPartnerOriginalAsync(other.Buyer.Id, otherDraft, Guid.NewGuid())).Evidence);
        // ผู้ขายแจ้งยกเลิกใบของบริษัทอื่นไม่ได้ (tenant) — ใบที่ไม่ใช่ของตน = 0
        using var c4 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(0, await Svc(c4).OnSourceVoidedAsync(s.Seller.Id, other.Invoice.Id));
    }

    [Fact]
    public async Task R255_ยืนยันต้นฉบับ_ปลดด่าน_ครั้งเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, buyerDrafts: true);
        var link = await Svc(db).OnDocumentSentAsync(s.Invoice);
        using var c1 = DbTestDatabase.TryCreateContext()!;
        var draftId = await c1.Documents.Where(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId == link!.Id).Select(d => d.Id).SingleAsync();
        var outcome = await Svc(c1).ConfirmPartnerOriginalAsync(s.Buyer.Id, draftId, Guid.NewGuid());
        Assert.False(outcome.Approved);   // ยังเป็นร่าง ⇒ ภาษีซื้อลงตามปกติเมื่ออนุมัติ
        Assert.Equal(PartnerInputVatEvidence.PaperConfirmed, outcome.Evidence);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(PartnerInputVatEvidence.PaperConfirmed,
            await c2.Documents.Where(d => d.Id == draftId).Select(d => d.PartnerInputVatEvidence).SingleAsync());
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(c2).ConfirmPartnerOriginalAsync(s.Buyer.Id, draftId, Guid.NewGuid()));
    }

    [Fact]
    public async Task R255_สถานะการเชื่อม_คำเชิญของเราไม่บอกผล_เชื่อมแล้วบอกได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, buyerDrafts: false, accepted: false);   // ผู้ขายเชิญเอง · ผู้ซื้อยังไม่ตอบ
        var pending = await Svc(db).PartnerStatusForContactAsync(s.Seller.Id, s.BuyerAtSeller.Id);
        Assert.Equal("NotLinked", pending.State);
        Assert.False(pending.Discoverable);
        Assert.Null(pending.PartnershipId);
        using var c1 = DbTestDatabase.TryCreateContext()!;
        await c1.TradingPartnerships.Where(p => p.Id == s.PartnershipId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, TradingPartnershipStatus.Accepted));
        var linked = await Svc(c1).PartnerStatusForContactAsync(s.Seller.Id, s.BuyerAtSeller.Id);
        Assert.Equal("Linked", linked.State);
        Assert.Equal((Guid?)s.PartnershipId, linked.PartnershipId);
        // ผู้ติดต่อของบริษัทอื่น ⇒ ไม่พบ
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Svc(c1).PartnerStatusForContactAsync(s.Buyer.Id, s.BuyerAtSeller.Id));
    }

    // ═══════════ ข้อ 8: 50 ทวิ ผ่านคู่ค้า ═══════════

    private sealed class FakeIntake
    {
        public List<(Guid CompanyId, WhtCertSubmission S)> Calls { get; } = new();
        public Exception? Throw { get; set; }
        /// <summary>รอบ 257 ชุด 2 (B-L4): สถานะที่กล่องรับของผู้ขายตอบ (Rejected = ตีกลับ)</summary>
        public string Status { get; set; } = "Pending";
        public IWhtCertIntakeService Proxy() => F4Proxy<IWhtCertIntakeService>.Create((name, args) =>
        {
            if (name != nameof(IWhtCertIntakeService.SubmitAsync)) return null;
            Calls.Add(((Guid)args[0]!, (WhtCertSubmission)args[1]!));
            if (Throw != null) return Task.FromException<WhtCertSubmitResult>(Throw);
            return Task.FromResult(new WhtCertSubmitResult(Guid.NewGuid(), Status, false, false,
                Status == "Rejected" ? "ใบนี้ไม่ได้ออกให้บริษัทเรา — บริษัทเราอยู่ช่องผู้มีหน้าที่หักภาษี ไม่ใช่ผู้ถูกหัก" : "ได้รับแล้ว"));
        });
    }

    private static async Task<(Guid PayerId, Guid PayeeId, Guid CertId)> SeedWhtAsync(AccountingDbContext db, DateTime since, DateTime issued, bool accepted = true)
    {
        var payee = new Company { Name = "ผู้ขาย 50ทวิ " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000" };
        var payer = new Company { Name = "ผู้จ่าย 50ทวิ " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000" };
        db.Companies.AddRange(payee, payer);
        await db.SaveChangesAsync();
        var (a, b) = payee.Id.CompareTo(payer.Id) < 0 ? (payee.Id, payer.Id) : (payer.Id, payee.Id);
        db.TradingPartnerships.Add(new TradingPartnership
        {
            CompanyAId = a, CompanyBId = b, InvitedByCompanyId = payer.Id,
            Status = accepted ? TradingPartnershipStatus.Accepted : TradingPartnershipStatus.Suspended,
        });
        db.WorkflowAutomationConfigs.Add(new WorkflowAutomationConfig { CompanyId = payer.Id, SendWhtCertsToPartners = true, SendWhtCertsToPartnersSince = since });
        var contact = new Contact
        {
            CompanyId = payer.Id, Name = payee.Name, TaxId = payee.TaxId, BranchCode = "00000", IsSupplier = true,
            // รอบ 264 P5 (B12): ที่อยู่ผู้ถูกหักต้องมีก่อนส่ง — ไม่มี ⇒ เหตุบนใบ ไม่ส่ง (เทสต์ WhtCertPartnerReturnRound264DbTests)
            Address = "1 ถนนสาทร กรุงเทพฯ 10120",
            ExternalSystem = PartnerWhtCertExchangeService.ExternalSystem, ExternalId = payee.Id.ToString(),
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var cert = new WithholdingTaxCert
        {
            CompanyId = payer.Id, CertificateNumber = "WHT255-" + Guid.NewGuid().ToString("N")[..6], PayeeContactId = contact.Id,
            TaxFormType = TaxType.WithholdingTax53, TaxYear = issued.Year, TaxMonth = issued.Month, CertificateType = WithholdingTaxCertType.Withhold,
            Status = WithholdingTaxCertStatus.Issued, TotalIncomeAmount = 10_000m, TotalTaxAmount = 300m, IssuedDate = issued,
        };
        cert.Lines.Add(new WithholdingTaxCertLine { LineOrder = 1, IncomeTypeCode = "40(2)", IncomeDescription = "ค่าบริการ", PaymentDate = issued, IncomeAmount = 10_000m, TaxRate = 3m, TaxAmount = 300m });
        db.WithholdingTaxCerts.Add(cert);
        await db.SaveChangesAsync();
        return (payer.Id, payee.Id, cert.Id);
    }

    [Fact]
    public async Task R255_50ทวิที่ผู้จ่ายออก_เข้าประตูเดียวของผู้ขาย_ประทับหลังได้ผล_ไม่ส่งซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var since = DateTime.UtcNow.AddDays(-2);
        var (payer, payee, certId) = await SeedWhtAsync(db, since, DateTime.UtcNow.AddDays(-1));
        var fake = new FakeIntake();
        var svc = new PartnerWhtCertExchangeService(db, fake.Proxy(), NullLogger<PartnerWhtCertExchangeService>.Instance);
        Assert.Equal(1, await svc.DeliverForPayerAsync(payer, since, DateTime.UtcNow));
        var call = Assert.Single(fake.Calls);
        Assert.Equal(payee, call.CompanyId);   // กล่องรับของผู้ขาย (ไม่ใช่ของผู้จ่าย)
        Assert.Equal(WhtCertIntakeChannel.NextAccLink, call.S.Channel);
        Assert.Equal(300m, call.S.WhtAmount);
        Assert.Equal(3m, call.S.WhtRate);
        Assert.Equal(WhtPayerFormType.Pnd53, call.S.PayerFormType);
        using var check = DbTestDatabase.TryCreateContext()!;
        var cert = await check.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.Id == certId);
        Assert.NotNull(cert.PartnerDeliveredAt);
        Assert.NotNull(cert.PartnerIntakeId);
        // รอบถัดไป: ส่งแล้วไม่ส่งซ้ำ
        Assert.Equal(0, await new PartnerWhtCertExchangeService(check, fake.Proxy(), NullLogger<PartnerWhtCertExchangeService>.Instance)
            .DeliverForPayerAsync(payer, since, DateTime.UtcNow));
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task R255_ทิศตรงข้าม_50ทวิ_ก่อนเปิด_คู่ไม่ใช้งาน_ไม่ส่ง_ล้ม_ไม่ประทับส่งแล้ว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var since = DateTime.UtcNow.AddDays(-1);
        var before = await SeedWhtAsync(db, since, DateTime.UtcNow.AddDays(-3));            // ออกก่อนเปิด (ไม่ส่งย้อนหลัง)
        var suspended = await SeedWhtAsync(db, since, DateTime.UtcNow, accepted: false);     // คู่ถูกระงับ
        var failing = await SeedWhtAsync(db, since, DateTime.UtcNow);
        var fake = new FakeIntake();
        var svc = new PartnerWhtCertExchangeService(db, fake.Proxy(), NullLogger<PartnerWhtCertExchangeService>.Instance);
        Assert.Equal(0, await svc.DeliverForPayerAsync(before.PayerId, since, DateTime.UtcNow));
        Assert.Equal(0, await svc.DeliverForPayerAsync(suspended.PayerId, since, DateTime.UtcNow));
        Assert.Empty(fake.Calls);
        fake.Throw = new BusinessRuleException("หนังสือรับรองเลขนี้อยู่ในทะเบียนแล้ว", "RD-50BIS-INTAKE", 409);
        Assert.Equal(0, await svc.DeliverForPayerAsync(failing.PayerId, since, DateTime.UtcNow));
        using var check = DbTestDatabase.TryCreateContext()!;
        var cert = await check.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.Id == failing.CertId);
        Assert.Null(cert.PartnerDeliveredAt);   // ภายนอกไม่ได้รับ ⇒ ไม่ประทับ "ส่งแล้ว"
        Assert.Equal(1, cert.PartnerDeliveryAttempts);
        Assert.Contains("ทะเบียน", cert.PartnerDeliveryError);
    }

    [Fact]
    public async Task R257_L4_กล่องรับของผู้ขายตีกลับ_ไม่ประทับส่งแล้ว_เหตุบนใบ_ไม่ส่งซ้ำ_ทิศตรงข้ามรับแล้วยังประทับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var since = DateTime.UtcNow.AddDays(-2);
        var rejected = await SeedWhtAsync(db, since, DateTime.UtcNow.AddDays(-1));
        var fake = new FakeIntake { Status = "Rejected" };
        var svc = new PartnerWhtCertExchangeService(db, fake.Proxy(), NullLogger<PartnerWhtCertExchangeService>.Instance);
        Assert.Equal(0, await svc.DeliverForPayerAsync(rejected.PayerId, since, DateTime.UtcNow));
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var cert = await check.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.Id == rejected.CertId && w.CompanyId == rejected.PayerId);
            Assert.Null(cert.PartnerDeliveredAt);                       // ผู้ขายไม่ได้ใบ ⇒ ไม่ใช่ "ส่งแล้ว"
            Assert.Null(cert.PartnerIntakeId);
            Assert.Contains("ตีกลับ", cert.PartnerDeliveryError);       // เหตุถึงผู้จ่าย
            Assert.Equal(PartnerExchangeRules.MaxWhtDeliveryAttempts, cert.PartnerDeliveryAttempts);   // ผลเชิงกำหนด ⇒ ไม่ลองซ้ำ
            Assert.StartsWith("ส่งผ่านคู่ค้าไม่สำเร็จ", PartnerExchangeRules.WhtDeliveryLabel(cert.PartnerDeliveredAt, cert.PartnerDeliveryError));
        }
        Assert.Equal(0, await svc.DeliverForPayerAsync(rejected.PayerId, since, DateTime.UtcNow));
        Assert.Single(fake.Calls);                                      // ไม่สร้างใบตีกลับซ้ำในกล่องรับของผู้ขาย

        // ทิศตรงข้าม: กล่องรับรับไว้ (รอตรวจ) ⇒ ยังประทับ "ส่งแล้ว" ตามเดิม
        var ok = await SeedWhtAsync(db, since, DateTime.UtcNow.AddDays(-1));
        fake.Status = "AwaitingConfirm";
        Assert.Equal(1, await svc.DeliverForPayerAsync(ok.PayerId, since, DateTime.UtcNow));
        using var check2 = DbTestDatabase.TryCreateContext()!;
        var okCert = await check2.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.Id == ok.CertId && w.CompanyId == ok.PayerId);
        Assert.NotNull(okCert.PartnerDeliveredAt);
        Assert.Equal("ส่งเข้ากล่องรับของคู่ค้าแล้ว", PartnerExchangeRules.WhtDeliveryLabel(okCert.PartnerDeliveredAt, okCert.PartnerDeliveryError));
    }

    // ═══════════ ข้อ 8: ลิงก์วิเศษพอร์ทัล ═══════════

    private sealed class Mailbox
    {
        public List<(string To, string Html)> Sent { get; } = new();
        public IEmailService Proxy() => F4Proxy<IEmailService>.Create((name, args) =>
        {
            if (name == nameof(IEmailService.TrySendAsync)) { Sent.Add(((string)args[0]!, (string)args[2]!)); return Task.FromResult(true); }
            if (name == nameof(IEmailService.IsSystemEmailConfiguredAsync)) return Task.FromResult(true);
            return Task.CompletedTask;
        });
    }

    private static PortalService Portal(AccountingDbContext db, Mailbox mail) => new(db,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Portal:SigningKey"] = "r255-portal-signing-key-for-db-tests-0123456789",
            ["App:BaseUrl"] = "https://acc.r255.test",
        }).Build(), email: mail.Proxy());

    [Fact]
    public async Task R255_ลิงก์วิเศษ_ส่งเฉพาะอีเมลที่มีสิทธิ์_แลกได้ครั้งเดียว_บริษัทอื่นแลกไม่ได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var shop = new Company { Name = "ร้าน 255 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax() };
        var other = new Company { Name = "ร้านอื่น 255 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax() };
        db.Companies.AddRange(shop, other);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = shop.Id, Name = "ลูกค้าพอร์ทัล", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        var email = $"ap-{Guid.NewGuid():N}@cust.test";
        db.PortalAccesses.Add(new PortalAccess { CompanyId = shop.Id, ContactId = contact.Id, Email = email, PasswordHash = "", IsActive = true });
        await db.SaveChangesAsync();
        var mail = new Mailbox();

        Assert.False(await Portal(db, mail).RequestMagicLinkAsync(shop.Id, "nobody@" + Guid.NewGuid().ToString("N") + ".test"));   // ไม่มีสิทธิ์ ⇒ ไม่ส่ง
        Assert.False(await Portal(db, mail).RequestMagicLinkAsync(other.Id, email));   // อีเมลนี้ไม่มีสิทธิ์ในบริษัทอื่น
        Assert.False(await db.EmailQueues.AnyAsync(q => q.CompanyId == other.Id && q.EntityType == PortalMagicLinkPolicy.QueueEntityType));
        Assert.True(await Portal(db, mail).RequestMagicLinkAsync(shop.Id, email.ToUpperInvariant()));
        // ฝ่ายค้าน M6: ไม่ส่งตรง — เข้าคิว EmailQueue ของบริษัท (ผู้ส่งในนามบริษัท + โควตาเดิม) · คิวเก็บที่หมาย ไม่เก็บ token
        Assert.Empty(mail.Sent);
        var queued = await db.EmailQueues.AsNoTracking()
            .SingleAsync(q => q.CompanyId == shop.Id && q.EntityType == PortalMagicLinkPolicy.QueueEntityType);
        Assert.Equal(email, queued.ToEmail);
        Assert.False(queued.AttachPdf);
        Assert.Contains(PortalMagicLinkPolicy.LinkPlaceholder, queued.Body);
        Assert.DoesNotContain("magic=", queued.Body);
        Assert.Null(await PortalService.MagicLinkSkipReasonAsync(db, queued, default));
        // ตอนส่ง: โทเคนออกที่นี่ (ตัวเดียวกับที่ EmailScheduleService.ComposeQueueMessageAsync เรียก)
        var issued = await PortalService.IssueMagicLinkUrlAsync(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["App:BaseUrl"] = "https://acc.r255.test" }).Build(), shop.Id, queued.EntityId, default);
        Assert.NotNull(issued.Url);
        var token = System.Text.RegularExpressions.Regex.Match(issued.Url!, "magic=([A-Za-z0-9_-]{43})").Groups[1].Value;
        Assert.True(PortalMagicLinkPolicy.LooksValid(token));
        using (var check = DbTestDatabase.TryCreateContext()!)
            Assert.False(await check.PortalMagicLinks.AnyAsync(m => m.TokenHash == token));   // ฐานเก็บแค่ hash

        using var c1 = DbTestDatabase.TryCreateContext()!;
        await Assert.ThrowsAsync<BusinessRuleException>(() => Portal(c1, mail).RedeemMagicLinkAsync(other.Id, token));   // บริษัทอื่น
        var session = await Portal(c1, mail).RedeemMagicLinkAsync(shop.Id, token);
        Assert.Equal(contact.Id, session.ContactId);
        Assert.Equal((Guid?)contact.Id, Portal(c1, mail).ExtractContactIdFromToken(session.AccessToken));   // โทเคนพอร์ทัลเดิม
        using var c2 = DbTestDatabase.TryCreateContext()!;
        await Assert.ThrowsAsync<BusinessRuleException>(() => Portal(c2, mail).RedeemMagicLinkAsync(shop.Id, token));   // ใช้ครั้งเดียว
        // ทิศตรงข้ามของ "ไม่มีรหัสผ่าน": ล็อกอินด้วยรหัสผ่านว่างไม่ได้
        await Assert.ThrowsAsync<InvalidOperationException>(() => Portal(c2, mail).LoginAsync(new Accounting.Models.DTOs.Portal.PortalLoginRequest(email, "", shop.Id)));
        // ฝ่ายค้าน M6 ทิศตรงข้าม: สิทธิ์ถูกปิดหลังเข้าคิว ⇒ ตอนส่งข้ามพร้อมเหตุ
        await c2.PortalAccesses.IgnoreQueryFilters().Where(a => a.Id == queued.EntityId).ExecuteUpdateAsync(x => x.SetProperty(a => a.IsActive, false));
        Assert.NotNull(await PortalService.MagicLinkSkipReasonAsync(c2, queued, default));
    }
}
