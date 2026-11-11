using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 258-3 ฝ่ายค้าน M6 (erp-review/2026-11-11/review-258-billing-chain.md) — เส้นเงินข้ามบริษัทบน PostgreSQL จริง:
/// การแจ้ง "ผู้ขายรับเงินแล้ว" (คู่ค้าไม่ Accepted ⇒ 0 · ซ้ำ ⇒ 1 · audit chain ทั้งสองบริษัทสะอาด · ผู้ขายไม่มี id ภายในของผู้ซื้อ) ·
/// กดบันทึกจ่ายสองครั้ง ⇒ การจ่ายเดียว · ใบเสร็จ settlement ผ่าน <c>RouteSentDocumentAsync</c> แนบเข้าใบซื้อเดิม ไม่สร้างใบใหม่ · ใบอย่างย่อไม่ใช่หลักฐานภาษีซื้อ (L6)
/// </summary>
[Trait("Category", "Db")]
public class BillingChainRound258DbTests
{
    private readonly ITestOutputHelper _out;
    public BillingChainRound258DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static readonly Random Rng = new();
    private static string RandTax()
    {
        lock (Rng) return "07" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Rng.Next(10))));
    }

    private static CrossTenantWorkflowService Xt(AccountingDbContext db) => new(db, NullLogger<CrossTenantWorkflowService>.Instance);

    private sealed record Pair(Company Seller, Company Buyer, Guid PartnershipId, Contact BuyerAtSeller);

    private static async Task<Pair> SeedAsync(AccountingDbContext db)
    {
        var seller = new Company { Name = "ผู้ขาย 258 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000", PartnerDiscoverable = true };
        var buyer = new Company { Name = "ผู้ซื้อ 258 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000" };
        db.Companies.AddRange(seller, buyer);
        await db.SaveChangesAsync();
        var (a, b) = seller.Id.CompareTo(buyer.Id) < 0 ? (seller.Id, buyer.Id) : (buyer.Id, seller.Id);
        var p = new TradingPartnership { CompanyAId = a, CompanyBId = b, InvitedByCompanyId = seller.Id, Status = TradingPartnershipStatus.Accepted };
        db.TradingPartnerships.Add(p);
        db.WorkflowAutomationConfigs.Add(new WorkflowAutomationConfig { CompanyId = buyer.Id, CreatePurchaseDraftsFromPartners = true });
        var contact = new Contact { CompanyId = seller.Id, Name = buyer.Name, TaxId = buyer.TaxId, BranchCode = "00000", IsCustomer = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return new Pair(seller, buyer, p.Id, contact);
    }

    private static async Task<Document> SellerDocAsync(AccountingDbContext db, Pair s, DocumentType type, DocumentStatus status, Guid? relatedTo = null,
        bool? byLaw = null, ReceiptForm? form = null)
    {
        var d = new Document
        {
            CompanyId = s.Seller.Id, ContactId = s.BuyerAtSeller.Id, DocumentType = type, DocumentNumber = "S258-" + Guid.NewGuid().ToString("N")[..8],
            Status = status, DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m,
            BalanceDue = status == DocumentStatus.Paid ? 0m : 1_070m, Currency = "THB", RelatedDocumentId = relatedTo, IsTaxInvoiceByLaw = byLaw, ReceiptForm = form,
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการรายเดือน", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    /// <summary>ใบแจ้งหนี้ของผู้ขายเข้าระบบผู้ซื้อ ⇒ ใบซื้อร่าง (เส้นเดิม) · คืน (ใบแจ้งหนี้, ใบซื้อ)</summary>
    private static async Task<(Document Invoice, Guid PurchaseId)> RoutedInvoiceAsync(AccountingDbContext db, Pair s)
    {
        var invoice = await SellerDocAsync(db, s, DocumentType.Invoice, DocumentStatus.Approved);
        var link = await Xt(db).OnDocumentSentAsync(invoice);
        using var c = DbTestDatabase.TryCreateContext()!;
        var purchaseId = await c.Documents.AsNoTracking().Where(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId == link!.Id).Select(d => d.Id).SingleAsync();
        return (invoice, purchaseId);
    }

    /// <summary>IDocumentService ปลอม — CreatePaymentAsync เขียนแถว Payment จริงของผู้ซื้อ (อ้างอิง + ยอดค้างเป็น 0) · นับครั้งที่ถูกเรียก</summary>
    private static IDocumentService PayingDocs(Guid buyerId, List<CreatePaymentRequest> calls) => F4Proxy<IDocumentService>.Create((m, args) =>
    {
        if (m != nameof(IDocumentService.CreatePaymentAsync)) return null;
        var req = (CreatePaymentRequest)args[1]!;
        calls.Add(req);
        using var other = DbTestDatabase.TryCreateContext()!;
        var pay = new Payment
        {
            CompanyId = buyerId, PaymentNumber = "PAY258-" + Random.Shared.Next(100000, 999999), DocumentId = req.DocumentId,
            PaymentDate = req.PaymentDate, Amount = req.Amount, PaymentMethod = req.PaymentMethod, Reference = req.Reference,
        };
        other.Payments.Add(pay);
        other.SaveChanges();
        other.Documents.Where(d => d.Id == req.DocumentId).ExecuteUpdate(x => x.SetProperty(d => d.BalanceDue, 0m).SetProperty(d => d.Status, DocumentStatus.Paid));
        return new PaymentResponse(pay.Id, pay.PaymentNumber, req.DocumentId, req.PaymentDate, req.Amount, req.PaymentMethod, req.Reference, null, null, req.Notes, DateTime.UtcNow);
    });

    private static PartnerPaymentNoticeService Notices(AccountingDbContext db, IDocumentService? docs = null)
        => new(db, docs ?? F4Proxy<IDocumentService>.Create((_, _) => null), NullLogger<PartnerPaymentNoticeService>.Instance);

    private static Task<List<AuditLog>> ChainAsync(AccountingDbContext db, Guid companyId)
        => db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId && a.RowHash != null).OrderBy(a => a.Id).ToListAsync();

    [Fact]
    public async Task M6_คู่ค้าไม่ใช่_Accepted_ไม่มีการแจ้ง_ทิศตรงข้าม_Accepted_ได้หนึ่งแถว_ซ้ำไม่เพิ่ม_chain_สะอาดทั้งสองบริษัท()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (invoice, purchaseId) = await RoutedInvoiceAsync(db, s);

        // ระงับการเชื่อม ⇒ 0 แถว
        await db.TradingPartnerships.Where(p => p.Id == s.PartnershipId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, TradingPartnershipStatus.Suspended));
        using (var c0 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await Notices(c0).NotifySellerReceivedAsync(s.Seller.Id, Guid.NewGuid(), invoice.Id, 1_070m, "PAY-INTENT-x", DateTime.UtcNow, PaymentMethodKind.PromptPay));
        using (var c0b = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await c0b.PartnerPaymentNotices.IgnoreQueryFilters().CountAsync(n => n.CompanyId == s.Buyer.Id));

        // ทิศตรงข้าม: Accepted ⇒ 1 แถว · webhook ซ้ำ (intent เดิม) ⇒ ยัง 1
        await db.TradingPartnerships.Where(p => p.Id == s.PartnershipId)
            .ExecuteUpdateAsync(x => x.SetProperty(p => p.Status, TradingPartnershipStatus.Accepted));
        var intent = Guid.NewGuid();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Notices(c1).NotifySellerReceivedAsync(s.Seller.Id, intent, invoice.Id, 1_070m, "PAY-INTENT-y", DateTime.UtcNow, PaymentMethodKind.PromptPay));
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await Notices(c2).NotifySellerReceivedAsync(s.Seller.Id, intent, invoice.Id, 1_070m, "PAY-INTENT-y", DateTime.UtcNow, PaymentMethodKind.PromptPay));

        using var check = DbTestDatabase.TryCreateContext()!;
        var n = await check.PartnerPaymentNotices.AsNoTracking().SingleAsync(x => x.CompanyId == s.Buyer.Id);
        Assert.Equal(purchaseId, n.TargetDocumentId);
        Assert.Equal(nameof(PaymentMethodKind.PromptPay), n.SourceChannel);
        foreach (var cid in new[] { s.Seller.Id, s.Buyer.Id })
            Assert.False(AuditHashChain.Analyze(await ChainAsync(check, cid)).HasIntegrityFindings);
        // L5: แถว audit ของผู้ขายไม่มี id ภายในของผู้ซื้อ
        var sellerAudit = await check.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == s.Seller.Id && a.EntityType == nameof(PartnerPaymentNotice)).Select(a => a.NewValues!).ToListAsync();
        Assert.NotEmpty(sellerAudit);
        Assert.All(sellerAudit, v =>
        {
            Assert.DoesNotContain(purchaseId.ToString(), v, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(s.Buyer.Id.ToString(), v, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task M6_กดบันทึกจ่ายสองครั้ง_ได้การจ่ายเดียว_ครั้งที่สองปฏิเสธ_409()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (invoice, purchaseId) = await RoutedInvoiceAsync(db, s);
        // ผู้ซื้ออนุมัติใบซื้อแล้ว (ใบร่างจากคู่ค้าไม่อนุมัติเอง)
        await db.Documents.Where(d => d.Id == purchaseId).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Approved)
            .SetProperty(d => d.BalanceDue, 1_070m).SetProperty(d => d.WithholdingTaxAmount, 0m));
        var intent = Guid.NewGuid();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Notices(c1).NotifySellerReceivedAsync(s.Seller.Id, intent, invoice.Id, 1_070m, "PAY-INTENT-z", DateTime.UtcNow, PaymentMethodKind.Card));
        Guid noticeId;
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            noticeId = await c2.PartnerPaymentNotices.AsNoTracking().Where(x => x.CompanyId == s.Buyer.Id).Select(x => x.Id).SingleAsync();

        var calls = new List<CreatePaymentRequest>();
        var user = Guid.NewGuid();
        using (var c3 = DbTestDatabase.TryCreateContext()!)
        {
            var r = await Notices(c3, PayingDocs(s.Buyer.Id, calls)).RecordAsync(s.Buyer.Id, noticeId, user, null, null);
            Assert.Equal(PartnerPaymentNoticeRule.StatusRecorded, r.Status);
        }
        using (var c4 = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Notices(c4, PayingDocs(s.Buyer.Id, calls)).RecordAsync(s.Buyer.Id, noticeId, user, null, null));
            Assert.Equal(409, ex.StatusCode);
        }
        Assert.Single(calls);
        Assert.Equal(PaymentMethod.CreditCard, calls[0].PaymentMethod);   // M4: วิธีจ่ายตั้งต้นตามช่องทางที่ผู้ขายรับ
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.Payments.CountAsync(p => p.CompanyId == s.Buyer.Id && p.Reference == PartnerPaymentNoticeRule.PaymentReference(intent)));
        // ทิศตรงข้าม: บัญชีของบริษัทอื่น ⇒ ปฏิเสธ (ไม่เชื่อ id จากหน้าเว็บ)
        var foreign = new BankAccount { CompanyId = s.Seller.Id, AccountName = "ผู้ขาย", BankName = "ธนาคาร", AccountNumber = "1234567890" };
        check.BankAccounts.Add(foreign);
        await check.SaveChangesAsync();
        var ex2 = await Assert.ThrowsAsync<BusinessRuleException>(() => Notices(check).RecordAsync(s.Buyer.Id, noticeId, user, foreign.Id, null));
        Assert.Equal(400, ex2.StatusCode);
    }

    [Fact]
    public async Task F_H2_ใบเสร็จ_settlement_แนบเข้าใบซื้อเดิม_ไม่สร้างใบใหม่_ทิศตรงข้าม_ใบอย่างย่อไม่เป็นหลักฐานภาษีซื้อ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (invoice, purchaseId) = await RoutedInvoiceAsync(db, s);
        var receipt = await SellerDocAsync(db, s, DocumentType.Receipt, DocumentStatus.Paid, relatedTo: invoice.Id, byLaw: true, form: ReceiptForm.ReceiptTaxInvoiceFull);
        var outcome = await Xt(db).RouteSentDocumentAsync(receipt);
        Assert.True(outcome.Routed);
        Assert.Null(outcome.FailureReason);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.Documents.CountAsync(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId != null));
        var link = await check.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.SourceDocumentId == receipt.Id);
        Assert.Equal((Guid?)purchaseId, link.TargetDocumentId);
        Assert.Equal(receipt.DocumentNumber, await check.Documents.Where(d => d.Id == purchaseId).Select(d => d.SupplierInvoiceNumber).SingleAsync());

        // L6: ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ (§82/5(2)) ⇒ ไม่แนบเป็นหลักฐาน · ไม่สร้างใบซื้อ
        var s2 = await SeedAsync(check);
        var (invoice2, purchase2) = await RoutedInvoiceAsync(check, s2);
        var abbr = await SellerDocAsync(check, s2, DocumentType.Receipt, DocumentStatus.Paid, relatedTo: invoice2.Id, byLaw: true, form: ReceiptForm.ReceiptTaxInvoiceAbbreviated);
        await Xt(check).RouteSentDocumentAsync(abbr);
        using var check2 = DbTestDatabase.TryCreateContext()!;
        Assert.Null((await check2.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.SourceDocumentId == abbr.Id)).TargetDocumentId);
        Assert.Equal(1, await check2.Documents.CountAsync(d => d.CompanyId == s2.Buyer.Id && d.PartnerSourceLinkId != null));
        Assert.NotEqual(abbr.DocumentNumber, await check2.Documents.Where(d => d.Id == purchase2).Select(d => d.SupplierInvoiceNumber).SingleAsync());
    }
}
