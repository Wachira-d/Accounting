using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Accounting.Services.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Scope = Accounting.Helpers.PartnerConsent.Scope;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 264 P4 บน PostgreSQL จริง (ข้ามบริษัท) — คำตัดสิน 264-5-10…264-5-13 · ทั้งสองทิศทุกด่าน:
/// รายการเดินบัญชีตรง (statement ของผู้มีสิทธิ์) ⇒ บันทึกเอง + ผูกบรรทัด · บรรทัดพิมพ์มือ ⇒ ไม่บันทึกเอง ไม่แตะบรรทัด ·
/// แจ้งโอน ⇒ PV ของเรา 1 ใบ + รายการรอยืนยันของผู้ขาย 1 รายการ · ผู้ขายยืนยันทีหลัง ⇒ ไม่มี PV ที่สอง · ผู้ขายไม่ยินยอมรับสลิป ⇒ PV อยู่ ไม่ส่ง ·
/// สลิปไม่ใช่บัญชีเรา ⇒ ปฏิเสธ ไม่มี PV · จ่ายจากระบบของเรา: ไม่ยินยอม ⇒ 409 ไม่เปิดรายการ · ยินยอม ⇒ แผน + รายการของผู้ขาย ⇒ การแจ้ง ⇒ บันทึกด้วยบัญชีที่ผู้จ่ายเลือก ·
/// L3: ผู้โอนไม่ใช่บัญชีเรา ⇒ ล้างเลข (เหลือชนิด) · บัญชีเราและยังเปิด ⇒ ไม่ล้าง · จบแล้ว ⇒ ล้าง · ใบซื้อของบริษัทอื่น ⇒ ไม่พบ
/// </summary>
[Trait("Category", "Db")]
public class PartnerOutboundRound264P4DbTests
{
    private readonly ITestOutputHelper _out;
    public PartnerOutboundRound264P4DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static readonly Random Rng = new();
    private static string RandTax() { lock (Rng) return "08" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Rng.Next(10)))); }

    private sealed record Seed(Company Seller, Company Buyer, Guid PartnershipId, Contact BuyerAtSeller, Guid OwnerA, Guid OwnerB, Guid BankA, Guid BankB, Guid BankB2,
        LocalDiskFileStorage Storage);

    private static PartnerConsentService Consent(AccountingDbContext db) => new(db, new PermissionService(db), NullLogger<PartnerConsentService>.Instance);

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        var seller = new Company { Name = "ผู้ขาย P4 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000", PartnerDiscoverable = true };
        var buyer = new Company { Name = "ผู้ซื้อ P4 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000" };
        db.Companies.AddRange(seller, buyer);
        await db.SaveChangesAsync();
        var (a, b) = seller.Id.CompareTo(buyer.Id) < 0 ? (seller.Id, buyer.Id) : (buyer.Id, seller.Id);
        var p = new TradingPartnership { CompanyAId = a, CompanyBId = b, InvitedByCompanyId = seller.Id, Status = TradingPartnershipStatus.Accepted, AcceptedAt = DateTime.UtcNow.AddDays(-1) };
        db.TradingPartnerships.Add(p);
        db.WorkflowAutomationConfigs.Add(new WorkflowAutomationConfig { CompanyId = buyer.Id, CreatePurchaseDraftsFromPartners = true });
        var contact = new Contact { CompanyId = seller.Id, Name = buyer.Name, TaxId = buyer.TaxId, BranchCode = "00000", IsCustomer = true };
        db.Contacts.Add(contact);
        var bankGl = new ChartOfAccount { CompanyId = buyer.Id, AccountCode = "11210", AccountName = "เงินฝากธนาคาร", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        var bankGl2 = new ChartOfAccount { CompanyId = buyer.Id, AccountCode = "11220", AccountName = "เงินฝากธนาคาร 2", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        var expense = new ChartOfAccount { CompanyId = buyer.Id, AccountCode = "53100", AccountName = "ค่าบริการ", AccountType = AccountType.Expense, Level = 4, IsActive = true };
        var sellerGl = new ChartOfAccount { CompanyId = seller.Id, AccountCode = "11210", AccountName = "เงินฝากธนาคาร", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        db.ChartOfAccounts.AddRange(bankGl, bankGl2, expense, sellerGl);
        await db.SaveChangesAsync();
        // ตัวแทนเลขบัญชี (ไม่ใช่เลขจริง)
        var bankA = new BankAccount { CompanyId = seller.Id, AccountName = "รับเงิน", BankName = "กสิกร", AccountNumber = "9876543210", LinkedAccountId = sellerGl.Id };
        var bankB = new BankAccount { CompanyId = buyer.Id, AccountName = "กระแสรายวัน", BankName = "กสิกร", AccountNumber = "1112223334", LinkedAccountId = bankGl.Id };
        var bankB2 = new BankAccount { CompanyId = buyer.Id, AccountName = "ออมทรัพย์", BankName = "กสิกร", AccountNumber = "5556667778", LinkedAccountId = bankGl2.Id };
        db.BankAccounts.AddRange(bankA, bankB, bankB2);
        var ua = new User { Email = $"p4a-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของผู้ขาย P4" };
        var ub = new User { Email = $"p4b-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของผู้ซื้อ P4" };
        db.Users.AddRange(ua, ub);
        db.CompanyUsers.Add(new CompanyUser { UserId = ua.Id, CompanyId = seller.Id, Role = UserRole.Owner });
        db.CompanyUsers.Add(new CompanyUser { UserId = ub.Id, CompanyId = buyer.Id, Role = UserRole.Owner });
        await db.SaveChangesAsync();
        var root = Path.Combine(Path.GetTempPath(), "p4-" + Guid.NewGuid().ToString("N"));
        var storage = new LocalDiskFileStorage(new FileStorageLocator(Path.Combine(root, "uploads"), Path.Combine(root, "wwwroot"), root, root));
        return new Seed(seller, buyer, p.Id, contact, ua.Id, ub.Id, bankA.Id, bankB.Id, bankB2.Id, storage);
    }

    private static async Task<(Document Invoice, Guid PurchaseId)> RoutedAsync(AccountingDbContext db, Seed s)
    {
        var inv = new Document
        {
            CompanyId = s.Seller.Id, ContactId = s.BuyerAtSeller.Id, DocumentType = DocumentType.Invoice, DocumentNumber = "SP4-" + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการรายเดือน", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        var link = await new CrossTenantWorkflowService(db, NullLogger<CrossTenantWorkflowService>.Instance).OnDocumentSentAsync(inv);
        using var c = DbTestDatabase.TryCreateContext()!;
        var pid = await c.Documents.AsNoTracking().Where(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId == link!.Id).Select(d => d.Id).SingleAsync();
        await c.Documents.Where(d => d.Id == pid).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Approved).SetProperty(d => d.BalanceDue, 1_070m));
        return (inv, pid);
    }

    /// <summary>ผู้ขายบันทึกรับชำระ (เข้าบัญชีรับเงินของผู้ขาย) แล้วขั้นหลัง commit แจ้งผู้ซื้อ</summary>
    private static async Task<Guid> SellerPaysAsync(Seed s, Document inv, decimal cash, Guid? intentId = null)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var pay = new Payment
        {
            CompanyId = s.Seller.Id, PaymentNumber = "RCP4-" + Random.Shared.Next(100000, 999999), DocumentId = inv.Id, PaymentDate = DateTime.UtcNow.Date,
            Amount = cash, PaymentMethod = PaymentMethod.BankTransfer, Reference = "MAN-" + Guid.NewGuid().ToString("N")[..6], BankAccountId = s.BankA, PaymentIntentId = intentId,
        };
        c.Payments.Add(pay);
        await c.SaveChangesAsync();
        using var c2 = DbTestDatabase.TryCreateContext()!;
        await PartnerPaymentNoticeService.NotifyForSellerPaymentAsync(c2, null, NullLogger.Instance, s.Seller.Id, pay.Id, PaymentEntrySource.Manual);
        return pay.Id;
    }

    private static async Task GrantAsync(Guid companyId, Guid ownerId, Guid partnershipId, Scope scope, decimal? max, Guid? bank)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var pv = await Consent(c).PreviewAsync(companyId, ownerId, new PartnerConsentService.PreviewRequest(partnershipId, scope.ToString(), max, bank));
        await Consent(c).GrantAsync(companyId, ownerId, new PartnerConsentService.GrantRequest(partnershipId, scope.ToString(), pv.MaxAmount, bank,
            pv.TextVersion, pv.EvidenceHash, PartnerConsent.ConfirmPhrase));
    }

    /// <summary>IDocumentService ปลอม — CreatePaymentAsync เขียนแถว Payment จริงของบริษัทผู้เรียก</summary>
    private static IDocumentService PayingDocs(List<CreatePaymentRequest> calls) => F4Proxy<IDocumentService>.Create((m, args) =>
    {
        if (m != nameof(IDocumentService.CreatePaymentAsync)) return null;
        var companyId = (Guid)args[0]!;
        var req = (CreatePaymentRequest)args[1]!;
        calls.Add(req);
        async Task<PaymentResponse> Run()
        {
            using var other = DbTestDatabase.TryCreateContext()!;
            var pay = new Payment
            {
                CompanyId = companyId, PaymentNumber = "PVP4-" + Random.Shared.Next(100000, 999999), DocumentId = req.DocumentId, PaymentDate = req.PaymentDate,
                Amount = req.Amount, PaymentMethod = req.PaymentMethod, Reference = req.Reference, BankAccountId = req.OverrideBankAccountId,
                WithholdingTaxAmount = req.WithholdingTaxAmount ?? 0m,
            };
            other.Payments.Add(pay);
            await other.SaveChangesAsync();
            await other.Documents.Where(d => d.Id == req.DocumentId).ExecuteUpdateAsync(x => x.SetProperty(d => d.BalanceDue, d => d.BalanceDue - req.Amount - (req.WithholdingTaxAmount ?? 0m)));
            return new PaymentResponse(pay.Id, pay.PaymentNumber, req.DocumentId, req.PaymentDate, req.Amount, req.PaymentMethod, req.Reference, null, req.OverrideBankAccountId, req.Notes, DateTime.UtcNow);
        }
        return Run();
    });

    private sealed class P4LockScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new P4LockScope();
        private sealed class P4LockScope : IServiceScope, IServiceProvider
        {
            private readonly AccountingDbContext _db = DbTestDatabase.TryCreateContext()!;
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type t) => t == typeof(AccountingDbContext) ? _db : t == typeof(PartnerConsentService) ? Consent(_db) : null;
            public void Dispose() => _db.Dispose();
        }
    }

    private static PartnerPayablePipeline Pipe(AccountingDbContext db, IDocumentService docs)
        => new(db, docs, new P4LockScopes(), NullLogger<PartnerPayablePipeline>.Instance, null, null, new BankPaymentLineEvidence(db, new PermissionService(db)));

    private static Task<PartnerPaymentNotice> NoticeAsync(AccountingDbContext db, Guid buyerId, Guid sellerPaymentId)
        => db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters().SingleAsync(n => n.CompanyId == buyerId && n.SellerPaymentId == sellerPaymentId);

    private static async Task<List<string>> EventsAsync(AccountingDbContext db, Guid companyId, string evt)
        => (await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId && a.RowHash != null).Select(a => a.NewValues!).ToListAsync())
            .Where(v => v.Contains("\"event\":\"" + evt + "\"")).ToList();

    private static async Task<Guid> OutflowLineAsync(Seed s, string source, string text, decimal amount = 1_040m)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var t = new BankTransaction
        {
            CompanyId = s.Buyer.Id, BankAccountId = s.BankB, TransactionDate = DateTime.UtcNow.Date, TransactionType = BankTransactionType.Withdrawal, Amount = amount,
            Description = text, EntrySource = source, CreatedByUserId = s.OwnerB,
        };
        c.BankTransactions.Add(t);
        await c.SaveChangesAsync();
        return t.Id;
    }

    [Fact]
    public async Task รายการเดินบัญชีตรงจาก_statement_ของผู้มีสิทธิ์_บันทึกเอง_ผูกบรรทัด_รันซ้ำไม่เพิ่ม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        // ยอด วันที่ บัญชีปลายทาง ตรงทุกข้อ · ค่าตั้งในความยินยอมเป็นอีกบัญชี ⇒ รายการเดินบัญชีชนะ (เงินออกจริง)
        var s2 = await SeedAsync(db);
        var (inv2, _) = await RoutedAsync(db, s2);
        await GrantAsync(s2.Buyer.Id, s2.OwnerB, s2.PartnershipId, Scope.AutoRecordPayment, 5_000m, s2.BankB2);
        var line2 = await OutflowLineAsync(s2, BankReceiptAutoSettle.SourceStatement, "TRANSFER TO X3210 ผู้ขาย", 1_070m);
        var pay2 = await SellerPaysAsync(s2, inv2, 1_070m);
        var calls2 = new List<CreatePaymentRequest>();
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c, PayingDocs(calls2)).RunForCompanyAsync(s2.Buyer.Id, DateTime.UtcNow));
        var call = Assert.Single(calls2);
        Assert.Equal(s2.BankB, call.OverrideBankAccountId);   // บัญชีที่เงินออกจริงชนะบัญชีตั้งต้น
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var n = await NoticeAsync(c, s2.Buyer.Id, pay2);
            Assert.Equal(PartnerPaymentNoticeRule.StatusRecorded, n.Status);
            var line = await c.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == line2);
            Assert.Equal(ReconciliationStatus.Matched, line.ReconciliationStatus);
            Assert.Equal(n.RecordedPaymentId, line.MatchedPaymentId);
            Assert.Equal(BankMatchAttribution.PartnerPayLine, line.ReconciledBy);
            Assert.Single(await EventsAsync(c, s2.Buyer.Id, "BankLinePartnerPaymentMatched"));
            var rec = Assert.Single(await EventsAsync(c, s2.Buyer.Id, "PartnerPaymentAutoRecorded"));
            Assert.Contains("\"origin\":\"BankLine\"", rec);
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await Pipe(c, PayingDocs(calls2)).RunForCompanyAsync(s2.Buyer.Id, DateTime.UtcNow.AddHours(1)));
        Assert.Single(calls2);
    }

    [Fact]
    public async Task บรรทัดพิมพ์มือ_ตรงทุกข้อ_ไม่บันทึกเอง_ไม่แตะบรรทัด_เหตุคือบัญชีตั้งต้นอย่างเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (inv, _) = await RoutedAsync(db, s);
        await GrantAsync(s.Buyer.Id, s.OwnerB, s.PartnershipId, Scope.AutoRecordPayment, 5_000m, s.BankB);
        var lineId = await OutflowLineAsync(s, BankReceiptAutoSettle.SourceManual, "TRANSFER TO X3210 ผู้ขาย", 1_070m);
        var payId = await SellerPaysAsync(s, inv, 1_070m);
        var calls = new List<CreatePaymentRequest>();
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await Pipe(c, PayingDocs(calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow));
        Assert.Empty(calls);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var n = await NoticeAsync(c2, s.Buyer.Id, payId);
        Assert.Equal(PartnerPaymentNoticeRule.StatusOpen, n.Status);
        Assert.Contains(PartnerPaySourceAccount.ConsentDefaultNotAutoReason, n.AutoBlockedReason);
        var line = await c2.BankTransactions.AsNoTracking().SingleAsync(t => t.Id == lineId);
        Assert.Equal(ReconciliationStatus.Unmatched, line.ReconciliationStatus);
        Assert.Null(line.MatchedPaymentId);
        // ผู้ตัดสินตัวเดียวให้คำแนะนำพร้อมเหตุของแหล่ง (ไม่เงียบ)
        var r = await new BankPaymentLineEvidence(c2, new PermissionService(c2)).DecideAsync(s.Buyer.Id, n);
        Assert.Equal(BankPaymentLineMatch.Kind.Suggest, r.Kind);
    }

    private static PartnerOutboundPaymentService Outbound(AccountingDbContext db, Seed s, List<CreatePaymentRequest> calls, ISlipImageReader? reader = null,
        IDocumentPayLinkService? payLinks = null, IPaymentIntentService? gatewayIntents = null)
    {
        var intents = new PaymentIntentService(db, new IPaymentProvider[] { new ManualSlipPaymentProvider() }, Array.Empty<IPaymentCompletionHandler>(),
            NullLogger<PaymentIntentService>.Instance);
        return new PartnerOutboundPaymentService(db, PayingDocs(calls), new P4LockScopes(), NullLogger<PartnerOutboundPaymentService>.Instance, payLinks, gatewayIntents ?? intents,
            new SlipEvidenceService(db, intents, s.Storage), reader, s.Storage, Consent(db));
    }

    private static SlipFile Png()
        => new(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(System.Text.Encoding.UTF8.GetBytes("p4-" + Guid.NewGuid())).ToArray(), "image/png", "slip.png");

    [Fact]
    public async Task แจ้งโอน_PV_ของเราหนึ่งใบ_รายการรอยืนยันของผู้ขายหนึ่งรายการ_ผู้ขายยืนยันทีหลัง_ไม่มี_PV_ที่สอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (inv, purchaseId) = await RoutedAsync(db, s);
        await GrantAsync(s.Seller.Id, s.OwnerA, s.PartnershipId, Scope.ReceiveSlipCopy, null, null);   // ผู้ขายเปิดรับสำเนาสลิป
        var calls = new List<CreatePaymentRequest>();
        PartnerTransferResult r;
        using (var c = DbTestDatabase.TryCreateContext()!)
            r = await Outbound(c, s, calls).NotifyTransferAsync(s.Buyer.Id, s.OwnerB, purchaseId, Png(), s.BankB, "BankTransfer", 1_070m, DateTime.UtcNow.Date, null);
        Assert.True(r.SlipCopySent);
        var call = Assert.Single(calls);
        Assert.Equal(s.BankB, call.OverrideBankAccountId);
        Assert.Equal(PartnerPaymentPlanRule.BuyerReference(r.PlanId), call.Reference);
        Guid sellerIntent;
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var plan = await c.PartnerPaymentPlans.AsNoTracking().SingleAsync(x => x.Id == r.PlanId && x.CompanyId == s.Buyer.Id);
            sellerIntent = plan.SellerIntentId!.Value;
            var intent = await c.PaymentIntents.AsNoTracking().SingleAsync(i => i.Id == sellerIntent);
            Assert.Equal(s.Seller.Id, intent.CompanyId);
            Assert.Equal(PaymentMethodKind.ManualSlip, intent.MethodKind);
            Assert.True(PaymentIntentPolicy.IsOpen(intent.Status));   // ยังไม่ใช่เงินเข้า (F2 ข้อ 3)
            Assert.Empty(await c.Payments.AsNoTracking().Where(p => p.CompanyId == s.Seller.Id && p.DocumentId == inv.Id).ToListAsync());
            Assert.Single(await EventsAsync(c, s.Buyer.Id, "PartnerTransferNotified"));
            Assert.True(await c.Set<FileAttachment>().AnyAsync(f => f.CompanyId == s.Seller.Id && f.EntityId == inv.Id && f.CreatedBy == SlipEvidenceService.ChannelPartnerNextAcc));
            Assert.True(await c.Set<FileAttachment>().AnyAsync(f => f.CompanyId == s.Buyer.Id && f.EntityId == purchaseId));
        }
        // ผู้ขายยืนยันรับเงินจากรายการนั้น ⇒ การแจ้งถึงผู้ซื้อ ⇒ ตาข่ายปิดด้วย PV ของแผน (ผู้ซื้อไม่มีความยินยอมบันทึกจ่าย — ท่อไม่ประเมินแถว)
        var sellerPay = await SellerPaysAsync(s, inv, 1_070m, sellerIntent);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c, PayingDocs(calls)).CloseFromPlansAsync(DateTime.UtcNow));
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var n = await NoticeAsync(c, s.Buyer.Id, sellerPay);
            Assert.Equal(PartnerPaymentNoticeRule.StatusRecorded, n.Status);
            Assert.Equal(r.PaymentId, n.RecordedPaymentId);
            Assert.Single(await c.Payments.AsNoTracking().Where(p => p.CompanyId == s.Buyer.Id && p.DocumentId == purchaseId && !p.IsDeleted).ToListAsync());
        }
        Assert.Single(calls);   // ไม่มี PV ที่สอง
    }

    [Fact]
    public async Task แจ้งโอน_ผู้ขายไม่ยินยอมรับสลิป_PV_ของเราอยู่_ไม่ส่งสำเนา_เหตุบนแผน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (inv, purchaseId) = await RoutedAsync(db, s);
        var calls = new List<CreatePaymentRequest>();
        PartnerTransferResult r;
        using (var c = DbTestDatabase.TryCreateContext()!)
            r = await Outbound(c, s, calls).NotifyTransferAsync(s.Buyer.Id, s.OwnerB, purchaseId, Png(), s.BankB, null, 1_070m, DateTime.UtcNow.Date, null);
        Assert.False(r.SlipCopySent);
        Assert.StartsWith(PartnerPaymentPlanRule.SlipCopyNotConsentedPrefix, r.SlipCopyNote);
        Assert.Single(calls);
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.False(await c2.PaymentIntents.AnyAsync(i => i.CompanyId == s.Seller.Id && i.SourceId == inv.Id));
        Assert.Null((await c2.PartnerPaymentPlans.AsNoTracking().SingleAsync(x => x.Id == r.PlanId)).SellerIntentId);
    }

    [Fact]
    public async Task แจ้งโอน_สลิปไม่ใช่บัญชีของเรา_ปฏิเสธ_ไม่มี_PV_ไม่มีแผน_ใบของบริษัทอื่นไม่พบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (_, purchaseId) = await RoutedAsync(db, s);
        var reading = new OcrSlipText.Reading(new[] { 1_070m }, 1_070m, DateTime.UtcNow.Date, null, LooksLikeSlip: true,
            Payer: new OcrSlipText.SlipParty(OcrSlipText.PartyKind.BankAccount, "004", false, "xxx-x-x9999-x", null, null));
        var reader = F4Proxy<ISlipImageReader>.Create((m, _) => m switch { "get_IsAvailable" => true, nameof(ISlipImageReader.ReadAsync) => reading, _ => null });
        var calls = new List<CreatePaymentRequest>();
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Outbound(c, s, calls, reader).NotifyTransferAsync(s.Buyer.Id, s.OwnerB, purchaseId, Png(), s.BankB, null, 1_070m, DateTime.UtcNow.Date, null));
            Assert.Equal(PartnerPaySourceAccount.SlipNotOursReason, ex.Message);
        }
        Assert.Empty(calls);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(await c.PartnerPaymentPlans.AnyAsync(x => x.CompanyId == s.Buyer.Id));
            // tenant: ใบซื้อของผู้ซื้อเรียกในนามผู้ขาย ⇒ ไม่พบ
            await Assert.ThrowsAsync<KeyNotFoundException>(() => Outbound(c, s, calls).OptionsAsync(s.Seller.Id, purchaseId));
        }
        // ทิศตรงข้าม: สลิปตรงบัญชีที่เลือก ⇒ ผ่าน
        var ours = reading with { Payer = new OcrSlipText.SlipParty(OcrSlipText.PartyKind.BankAccount, "004", false, "xxx-x-xx333-4", null, null) };
        var okReader = F4Proxy<ISlipImageReader>.Create((m, _) => m switch { "get_IsAvailable" => true, nameof(ISlipImageReader.ReadAsync) => ours, _ => null });
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var ok = await Outbound(c, s, calls, okReader).NotifyTransferAsync(s.Buyer.Id, s.OwnerB, purchaseId, Png(), s.BankB, null, 1_070m, DateTime.UtcNow.Date, null);
            Assert.Equal(nameof(PartnerPaySourceAccount.Origin.PayerDeclared), ok.Origin);
        }
        Assert.Single(calls);
    }

    [Fact]
    public async Task จ่ายจากระบบของเรา_ไม่ยินยอม_409_ไม่เปิดรายการ_ยินยอม_แผน_แล้วการแจ้งบันทึกด้วยบัญชีที่ผู้จ่ายเลือก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (inv, purchaseId) = await RoutedAsync(db, s);
        var payLinks = F4Proxy<IDocumentPayLinkService>.Create((m, args) => m == nameof(IDocumentPayLinkService.PartnerPayTargetAsync) && (Guid)args[0]! == s.Seller.Id
            ? new PublicPayTarget(PaymentSourceKind.Document, inv.Id, Guid.Empty, 1_070m, "ใบแจ้งหนี้ " + inv.DocumentNumber, null, null, inv.DocumentNumber,
                AllowedMethods: new[] { "PromptPay" }, ContactId: s.BuyerAtSeller.Id)
            : null);
        var started = 0;
        var gateway = F4Proxy<IPaymentIntentService>.Create((m, args) =>
        {
            if (m != nameof(IPaymentIntentService.StartAsync)) return null;
            started++;
            var companyId = (Guid)args[0]!;
            var req = (StartPaymentRequest)args[1]!;
            async Task<PaymentIntent> Run()
            {
                using var other = DbTestDatabase.TryCreateContext()!;
                var i = new PaymentIntent
                {
                    CompanyId = companyId, ProviderCode = "test", SourceKind = req.SourceKind, SourceId = req.SourceId, Amount = req.Amount, Currency = "THB",
                    MethodKind = req.Method, Status = PaymentIntentStatus.Pending, QrPayload = "data:image/png;base64,AA==", IdempotencyKey = Guid.NewGuid().ToString("N"),
                };
                other.PaymentIntents.Add(i);
                await other.SaveChangesAsync();
                return i;
            }
            return Run();
        });
        var calls = new List<CreatePaymentRequest>();
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Outbound(c, s, calls, null, payLinks, gateway).StartGatewayAsync(s.Buyer.Id, s.OwnerB, purchaseId, s.BankB2, "PromptPay", null));
            Assert.StartsWith(PartnerPaymentPlanRule.ConsentNeededPrefix, ex.Message);
        }
        Assert.Equal(0, started);
        await GrantAsync(s.Buyer.Id, s.OwnerB, s.PartnershipId, Scope.AutoRecordPayment, 5_000m, s.BankB);
        PartnerGatewayStartResult g;
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var opts = await Outbound(c, s, calls, null, payLinks, gateway).OptionsAsync(s.Buyer.Id, purchaseId);
            Assert.True(opts.GatewayOffered);
            Assert.Contains(opts.GatewayMethods, o => o.Value == "PromptPay");
            await Assert.ThrowsAsync<BusinessRuleException>(() =>   // วิธีที่ไม่อยู่ในชุด ⇒ ปฏิเสธ
                Outbound(c, s, calls, null, payLinks, gateway).StartGatewayAsync(s.Buyer.Id, s.OwnerB, purchaseId, s.BankB2, "Card", null));
            g = await Outbound(c, s, calls, null, payLinks, gateway).StartGatewayAsync(s.Buyer.Id, s.OwnerB, purchaseId, s.BankB2, "PromptPay", "https://evil.example");
        }
        Assert.Equal(1, started);
        Assert.Empty(calls);   // ไม่ประทับ "จ่ายแล้ว" ฝั่งเรา — รอการแจ้ง
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var plan = await c.PartnerPaymentPlans.AsNoTracking().SingleAsync(x => x.Id == g.PlanId && x.CompanyId == s.Buyer.Id);
            Assert.Equal(g.IntentId, plan.SellerIntentId);
            Assert.Single(await EventsAsync(c, s.Buyer.Id, "PartnerPaymentPlanStarted"));
        }
        // ผู้ให้บริการยืนยัน ⇒ ผู้ขายบันทึกรับ (intent เดียวกัน) ⇒ การแจ้ง ⇒ ท่อ: ผู้จ่ายเลือก BankB2 ชนะบัญชีตั้งต้น BankB
        var sellerPay = await SellerPaysAsync(s, inv, 1_070m, g.IntentId);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c, PayingDocs(calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow));
        var call = Assert.Single(calls);
        Assert.Equal(s.BankB2, call.OverrideBankAccountId);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var rec = Assert.Single(await EventsAsync(c, s.Buyer.Id, "PartnerPaymentAutoRecorded"));
            Assert.Contains("\"origin\":\"PayerDeclared\"", rec);
            Assert.Equal(PartnerPaymentNoticeRule.StatusRecorded, (await NoticeAsync(c, s.Buyer.Id, sellerPay)).Status);
        }
    }

    [Fact]
    public async Task L3_ผู้โอนไม่ใช่บัญชีเรา_ล้างเลขเหลือชนิด_บัญชีเราและยังเปิด_ไม่ล้าง_ปิดแล้ว_ล้าง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (inv, _) = await RoutedAsync(db, s);
        var (inv2, _) = await RoutedAsync(db, s);
        var notOurs = await SellerPaysAsync(s, inv, 1_070m);
        var ours = await SellerPaysAsync(s, inv2, 1_070m);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            await c.PartnerPaymentNotices.IgnoreQueryFilters().Where(n => n.CompanyId == s.Buyer.Id && n.SellerPaymentId == notOurs)
                .ExecuteUpdateAsync(x => x.SetProperty(n => n.SlipPayerBankCode, "004").SetProperty(n => n.SlipPayerAccountMask, "xxx-x-x9999-x"));
            await c.PartnerPaymentNotices.IgnoreQueryFilters().Where(n => n.CompanyId == s.Buyer.Id && n.SellerPaymentId == ours)
                .ExecuteUpdateAsync(x => x.SetProperty(n => n.SlipPayerBankCode, "004").SetProperty(n => n.SlipPayerAccountMask, "xxx-x-xx333-4"));
        }
        var calls = new List<CreatePaymentRequest>();
        using (var c = DbTestDatabase.TryCreateContext()!)
            await Pipe(c, PayingDocs(calls)).SweepSlipPayerAsync(DateTime.UtcNow);
        Guid oursId;
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var a = await NoticeAsync(c, s.Buyer.Id, notOurs);
            Assert.Null(a.SlipPayerAccountMask);
            Assert.Null(a.SlipPayerBankCode);
            Assert.Equal("NotOurs", a.SlipPayerMatch);
            Assert.Equal(PartnerPaySourceAccount.SlipKind.NotOurs, (await PartnerPayablePipeline.SlipMatchAsync(c, s.Buyer.Id, a, default))!.Kind);   // ตัวอ่านยังได้ผล
            var cleared = Assert.Single(await EventsAsync(c, s.Buyer.Id, "PartnerPaymentNoticeSlipPayerCleared"));
            Assert.DoesNotContain("9999", cleared);   // ไม่มีเลขใน audit
            var b = await NoticeAsync(c, s.Buyer.Id, ours);
            Assert.Equal("xxx-x-xx333-4", b.SlipPayerAccountMask);   // บัญชีเรา + ยังเปิด ⇒ คงไว้ (ตัวตัดสินยังใช้)
            Assert.Equal("Unique", b.SlipPayerMatch);
            oursId = b.Id;
        }
        using (var c = DbTestDatabase.TryCreateContext()!)
            await new PartnerPaymentNoticeService(c, PayingDocs(calls), NullLogger<PartnerPaymentNoticeService>.Instance).DismissAsync(s.Buyer.Id, oursId, s.OwnerB, "ทดสอบ");
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var b = await NoticeAsync(c, s.Buyer.Id, ours);
            Assert.Null(b.SlipPayerAccountMask);   // จบแล้ว ⇒ ล้าง
            Assert.Equal(2, (await EventsAsync(c, s.Buyer.Id, "PartnerPaymentNoticeSlipPayerCleared")).Count);
        }
    }
}
