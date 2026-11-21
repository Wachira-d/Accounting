using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;
using Scope = Accounting.Helpers.PartnerConsent.Scope;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 281 (คำตัดสิน 281-1…281-5) — เส้น A↔B ครบทั้งเส้นบน PostgreSQL จริง สองบริษัท (คำขอเจ้าของ: "ได้รับใบแจ้งหนี้ → จ่าย → ฝั่งเขาออกใบเสร็จ/ใบกำกับ
/// อัตโนมัติ → เราออกใบสำคัญจ่ายพร้อม 50 ทวิ → ส่ง 50 ทวิ กลับให้เขา · ทุกขั้นแจ้งตามช่องที่เปิด"):
/// ใบแจ้งหนี้ค่าบริการ 10,700 (VAT 700 · หัก 3% = 300) ⇒ ใบซื้อร่างของ B (แจ้ง B) ⇒ B จ่าย 10,400 ผ่านลิงก์ของ A (ทางเข้า Gateway ของเส้นบันทึกรับชำระจริง
/// <see cref="DocumentService.CreatePaymentAsync"/>) ⇒ A ได้ใบเสร็จ/ใบกำกับ ณ วันรับเงิน + แถวรอ 50 ทวิ ⇒ ใบนั้นแนบเข้าใบซื้อของ B (แจ้งทั้งสองฝั่ง) ⇒
/// B ได้การแจ้ง "ผู้ขายรับเงินแล้ว" ⇒ ท่อของ B (ความยินยอม + หลักฐานรายการเดินบัญชี) บันทึกจ่ายด้วยเส้นจ่ายจริง ⇒ ใบสำคัญจ่าย + 50 ทวิ 300 ⇒
/// ส่งเข้ากล่องรับของ A ⇒ A ยืนยันเองด้วยที่มา (ผู้ติดต่อถูกผูกตอนส่งใบ — 281-4) ⇒ "เส้นทางของใบนี้" ครบทุกขั้นทั้งสองมุม ·
/// ทิศตรงข้าม: ไม่มีความยินยอม ⇒ ไม่บันทึกจ่าย (ค้างพร้อมเหตุ = คิวคน) · ยอดที่ผู้ขายรับไม่ตรงยอดค้าง ⇒ ไม่บันทึก · บริษัทที่สามไม่เห็นอะไรเลย
/// </summary>
[Trait("Category", "Db")]
public class PartnerFlowRound281DbTests
{
    private readonly ITestOutputHelper _out;
    public PartnerFlowRound281DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";
    private static readonly DateTime Today = ThaiDate.CalendarDateUtc(DateTime.UtcNow).Date;

    private static readonly Random Rng = new();
    private static string ValidTax()
    {
        string head;
        lock (Rng) head = "0" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Rng.Next(10))));
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (head[i] - '0') * (13 - i);
        return head + (char)('0' + (11 - sum % 11) % 10);
    }

    /// <summary>เครื่องแจ้งเตือนที่จดทุกการส่ง (บริษัท · คีย์ · หัวข้อ) — แทน NotificationEngine (ตัวตัดสินช่องอยู่ที่ engine เดิม ไม่ใช่ขอบเขตของรอบนี้)</summary>
    internal sealed class Spy : INotificationEngine
    {
        public readonly List<(Guid Company, string Key, string Title)> Sent = new();
        public Task DispatchAsync(Guid companyId, string eventKey, NotificationContext context)
        {
            lock (Sent) Sent.Add((companyId, eventKey, context.Title ?? ""));
            return Task.CompletedTask;
        }
        public bool Has(Guid company, string key) { lock (Sent) return Sent.Any(s => s.Company == company && s.Key == key); }
    }

    internal sealed record Seed(Company Seller, Company Buyer, Guid PartnershipId, Contact BuyerAtSeller, Guid OwnerB, Guid BankA, Guid BankB,
        Guid ArA, Guid RevA, Guid UndueA);

    private static ChartOfAccount Acc(Guid companyId, string code, string name, AccountType type)
        => new() { CompanyId = companyId, AccountCode = code, AccountName = name, AccountType = type, Level = 4, IsActive = true };

    internal static async Task<Seed> SeedAsync(AccountingDbContext db, bool consent = true)
    {
        await DbTestDatabase.ApplyDdlAsync(db, PartnerConsentMigration.Statements());
        var tag = Guid.NewGuid().ToString("N")[..6];
        var seller = new Company { Name = "ผู้ขาย 281 " + tag, TaxId = ValidTax(), BranchCode = "00000", IsVatRegistered = true, FiscalYearStartMonth = 1,
            Address = "1 ถนนสีลม บางรัก กรุงเทพฯ 10500" };
        var buyer = new Company { Name = "ผู้ซื้อ 281 " + tag, TaxId = ValidTax(), BranchCode = "00000", IsVatRegistered = true, FiscalYearStartMonth = 1,
            Address = "2 ถนนพระราม 4 ปทุมวัน กรุงเทพฯ 10330" };
        db.Companies.AddRange(seller, buyer);
        await db.SaveChangesAsync();
        var (a, b) = seller.Id.CompareTo(buyer.Id) < 0 ? (seller.Id, buyer.Id) : (buyer.Id, seller.Id);
        var p = new TradingPartnership { CompanyAId = a, CompanyBId = b, InvitedByCompanyId = seller.Id, Status = TradingPartnershipStatus.Accepted, AcceptedAt = DateTime.UtcNow.AddDays(-2) };
        db.TradingPartnerships.Add(p);
        // ค่าตั้งหน้าคู่ค้าสองข้อที่เจ้าของเปิด (ฝั่งผู้ซื้อ)
        db.WorkflowAutomationConfigs.Add(new WorkflowAutomationConfig
        {
            CompanyId = buyer.Id, CreatePurchaseDraftsFromPartners = true, SendWhtCertsToPartners = true, SendWhtCertsToPartnersSince = DateTime.UtcNow.AddDays(-1),
        });
        var contact = new Contact { CompanyId = seller.Id, Name = buyer.Name, TaxId = buyer.TaxId, BranchCode = "00000", IsCustomer = true,
            ContactType = ContactType.JuristicPerson, Address = buyer.Address };
        db.Contacts.Add(contact);
        // ผังของผู้ขาย (เส้นรับชำระ + ใบกำกับ ณ วันรับเงิน + ภาษีถูกหัก 11910)
        var bankGlA = Acc(seller.Id, "11121", "เงินฝากธนาคาร", AccountType.Asset);
        var arA = Acc(seller.Id, "11310", "ลูกหนี้การค้า", AccountType.Asset);
        var undueA = Acc(seller.Id, "21913", "ภาษีขายรอเรียกเก็บ", AccountType.Liability);
        var revA = Acc(seller.Id, "41100", "รายได้ค่าบริการ", AccountType.Revenue);
        db.ChartOfAccounts.AddRange(Acc(seller.Id, "11111", "เงินสด", AccountType.Asset), bankGlA, arA, undueA, revA,
            Acc(seller.Id, "21911", "ภาษีขาย", AccountType.Liability), Acc(seller.Id, "11910", "ภาษีเงินได้ถูกหัก ณ ที่จ่าย", AccountType.Asset),
            Acc(seller.Id, "54710", "ค่าธรรมเนียมธนาคาร", AccountType.Expense));
        // ผังของผู้ซื้อ (เส้นจ่ายชำระ + ภาษีหักค้างจ่าย)
        var bankGlB = Acc(buyer.Id, "11210", "เงินฝากธนาคาร", AccountType.Asset);
        db.ChartOfAccounts.AddRange(Acc(buyer.Id, "11111", "เงินสด", AccountType.Asset), bankGlB,
            Acc(buyer.Id, "21210", "เจ้าหนี้การค้า", AccountType.Liability), Acc(buyer.Id, "21916", "ภ.ง.ด.3 ค้างจ่าย", AccountType.Liability),
            Acc(buyer.Id, "21917", "ภ.ง.ด.53 ค้างจ่าย", AccountType.Liability), Acc(buyer.Id, "53100", "ค่าบริการ", AccountType.Expense),
            Acc(buyer.Id, "11540", "ภาษีซื้อ", AccountType.Asset), Acc(buyer.Id, "11541", "ภาษีซื้อยังไม่ถึงกำหนด", AccountType.Asset));
        await db.SaveChangesAsync();
        var bankA = new BankAccount { CompanyId = seller.Id, AccountName = "ออมทรัพย์", BankName = "กสิกร", AccountNumber = "1112223330", LinkedAccountId = bankGlA.Id };
        var bankB = new BankAccount { CompanyId = buyer.Id, AccountName = "กระแสรายวัน", BankName = "กรุงเทพ", AccountNumber = "4445556660", LinkedAccountId = bankGlB.Id };
        db.BankAccounts.AddRange(bankA, bankB);
        var u = new User { Email = $"r281-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของผู้ซื้อ 281" };
        db.Users.Add(u);
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = buyer.Id, Role = UserRole.Owner });
        await db.SaveChangesAsync();
        var s = new Seed(seller, buyer, p.Id, contact, u.Id, bankA.Id, bankB.Id, arA.Id, revA.Id, undueA.Id);
        if (consent) await GrantAsync(s, 50_000m, bankB.Id);
        return s;
    }

    private static PartnerConsentService Consent(AccountingDbContext db) => new(db, new PermissionService(db), NullLogger<PartnerConsentService>.Instance);

    private static async Task GrantAsync(Seed s, decimal max, Guid bank)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var pv = await Consent(c).PreviewAsync(s.Buyer.Id, s.OwnerB, new PartnerConsentService.PreviewRequest(s.PartnershipId, Scope.AutoRecordPayment.ToString(), max, bank));
        await Consent(c).GrantAsync(s.Buyer.Id, s.OwnerB, new PartnerConsentService.GrantRequest(s.PartnershipId, Scope.AutoRecordPayment.ToString(), pv.MaxAmount, bank,
            pv.TextVersion, pv.EvidenceHash, PartnerConsent.ConfirmPhrase));
    }

    private static CrossTenantWorkflowService Xt(AccountingDbContext db, Spy spy) => new(db, NullLogger<CrossTenantWorkflowService>.Instance, null, spy);

    /// <summary>DocumentService จริง — ต่อ CrossTenantWorkflowService (ใบเสร็จ ณ วันรับเงิน → ระบบคู่ค้า) + ตัวออก 50 ทวิ จริง + เครื่องแจ้งเตือนจด</summary>
    internal static DocumentService Docs(AccountingDbContext db, Spy spy)
        => new(db, new AccountingService(db), RecurringTransactionDbTests.AllowAll<ISubscriptionService>.Create(),
            new WithholdingTaxCertService(db, null!, null!, NullLogger<WithholdingTaxCertService>.Instance), null!,
            NullLogger<DocumentService>.Instance, null!,
            new Accounting.Services.Implementations.Ocr.VendorIntelligenceService(db, NullLogger<Accounting.Services.Implementations.Ocr.VendorIntelligenceService>.Instance),
            Xt(db, spy), notify: spy);

    /// <summary>ใบแจ้งหนี้ค่าบริการของผู้ขาย 10,000 + VAT 700 = 10,700 · หัก 3% = 300 ⇒ ยอดค้าง (หลังหัก) 10,400 · อนุมัติแล้ว + JE ตั้งลูกหนี้เต็ม 10,700
    /// (เกณฑ์เงินสด — 11910 ลงตอนรับเงิน) · ภาษีขายพัก 21913 (งานบริการ §78/1)</summary>
    internal static async Task<Document> InvoiceAsync(AccountingDbContext db, Seed s)
    {
        var no = "INV-281-" + Guid.NewGuid().ToString("N")[..8];
        var d = new Document
        {
            CompanyId = s.Seller.Id, ContactId = s.BuyerAtSeller.Id, DocumentType = DocumentType.Invoice, DocumentNumber = no,
            Status = DocumentStatus.Approved, DocumentDate = Today, SubTotal = 10_000m, VatAmount = 700m, TotalAmount = 10_400m, BalanceDue = 10_400m,   // ฐาน + VAT − WHT (WhtGrossUp.PayeeTotal)
            WithholdingTaxAmount = 300m, Currency = "THB", ExchangeRate = 1m, WhtCertAutoIssue = false,
        };
        d.Lines.Add(new DocumentLine
        {
            LineOrder = 1, Description = "ค่าบริการดูแลระบบ", Quantity = 1, UnitPrice = 10_000m, Amount = 10_000m, VatRate = 7m, VatAmount = 700m,
            WithholdingTaxRate = 3m, WithholdingTaxAmount = 300m, IncomeTypeCode = "40(2)", AccountId = s.RevA,
        });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        var je = new JournalEntry
        {
            CompanyId = s.Seller.Id, EntryNumber = "SV-" + no, EntryDate = Today, Status = JournalEntryStatus.Posted, JournalType = JournalType.Sales,
            Description = "ออกใบ " + no, Reference = no, TotalDebit = 10_700m, TotalCredit = 10_700m, SourceDocumentId = d.Id, IsAutoGenerated = true, CreatedBy = "tester",
        };
        je.Lines.Add(new JournalEntryLine { AccountId = s.ArA, DebitAmount = 10_700m, LineOrder = 1 });
        je.Lines.Add(new JournalEntryLine { AccountId = s.RevA, CreditAmount = 10_000m, LineOrder = 2 });
        je.Lines.Add(new JournalEntryLine { AccountId = s.UndueA, CreditAmount = 700m, LineOrder = 3 });
        db.JournalEntries.Add(je);
        await db.SaveChangesAsync();
        return d;
    }

    /// <summary>ส่งใบเข้าระบบผู้ซื้อ (ทางเข้าเดียวกับตอนอนุมัติ) ⇒ ใบซื้อร่าง ⇒ ผู้ซื้ออนุมัติ (จำลองผลอนุมัติ: เลขจริง + เปิดออก 50 ทวิ ตอนจ่าย)</summary>
    internal static async Task<Guid> RouteAndApproveAsync(Seed s, Document inv, Spy spy)
    {
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var outcome = await Xt(c, spy).RouteSentDocumentAsync(inv);
            Assert.True(outcome.Routed, outcome.FailureReason);
        }
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var link = await c2.CrossTenantDocumentLinks.AsNoTracking().SingleAsync(l => l.SourceCompanyId == s.Seller.Id && l.SourceDocumentId == inv.Id);
        Assert.True(link.TargetDocumentId != null, "ไม่ได้ใบซื้อร่าง: " + link.Comment);
        var pid = link.TargetDocumentId!.Value;
        var piNo = "PI-281-" + Guid.NewGuid().ToString("N")[..6];
        await c2.Documents.Where(d => d.Id == pid && d.CompanyId == s.Buyer.Id).ExecuteUpdateAsync(x => x
            .SetProperty(d => d.Status, DocumentStatus.Approved)
            .SetProperty(d => d.DocumentNumber, piNo)
            .SetProperty(d => d.WhtCertAutoIssue, true));
        return pid;
    }

    /// <summary>ผู้ซื้อจ่ายผ่านลิงก์ของผู้ขาย — เส้นบันทึกรับชำระจริงของผู้ขาย (ทางเข้า Gateway · หัก 3% ตามที่ผู้จ่ายหัก)</summary>
    internal static async Task<Guid> BuyerPaysThroughLinkAsync(Seed s, Guid invoiceId, decimal cash, decimal wht, Spy spy)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var r = await Docs(c, spy).CreatePaymentAsync(s.Seller.Id,
            new CreatePaymentRequest(invoiceId, Today, cash, PaymentMethod.BankTransfer, "LINK-281", null, null,
                WithholdingTaxAmount: wht, OverrideBankAccountId: s.BankA, IssueReceiptDocument: true),
            "gateway", PaymentEntrySource.Gateway);
        return r.Id;
    }

    private sealed class PipelineScopes : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new PipelineScope();
        private sealed class PipelineScope : IServiceScope, IServiceProvider
        {
            private readonly AccountingDbContext _db = DbTestDatabase.TryCreateContext()!;
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type t) => t == typeof(AccountingDbContext) ? _db : t == typeof(PartnerConsentService) ? Consent(_db) : null;
            public void Dispose() => _db.Dispose();
        }
    }

    /// <summary>หลักฐานรายการเดินบัญชีฝั่งเงินออกของผู้ซื้อ (ชั้นแรกของตัวตัดสินแหล่งเงิน · ตัวจริงอ่านตาราง BankTransactions)</summary>
    private sealed class FixedBankLine(Guid account) : PartnerPayablePipeline.IBankLineEvidence
    {
        public Task<(Guid? BankAccountId, string? Label)> FindAsync(Guid companyId, PartnerPaymentNotice notice, CancellationToken ct)
            => Task.FromResult<(Guid?, string?)>((account, "(เทสต์ 281)"));
    }

    internal static async Task<int> BuyerPipelineAsync(Seed s, Spy spy, bool bankEvidence = true)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var pipe = new PartnerPayablePipeline(c, Docs(c, spy), new PipelineScopes(), NullLogger<PartnerPayablePipeline>.Instance, null, spy,
            bankEvidence ? new FixedBankLine(s.BankB) : null);
        return await pipe.RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow);
    }

    private sealed class KillSwitchAi
    {
        public IAiOrchestrator Proxy() => F4Proxy<IAiOrchestrator>.Create((name, _) => name != nameof(IAiOrchestrator.AskAsync) ? null
            : new AiResponse { Status = AiCallStatus.NoProvider, UsedAi = false, PrimaryAnswer = null, Confidence = null, FeedbackId = null });
    }

    private static async Task<int> ReturnWhtAsync(Seed s, Spy spy)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var intake = new WhtCertIntakeService(c, new KillSwitchAi().Proxy(), F4Proxy<IAiFeedbackRecorder>.Create((_, _) => null),
            new WhtCreditService(c, NullLogger<WhtCreditService>.Instance), NullLogger<WhtCertIntakeService>.Instance, null, spy);
        return await new PartnerWhtCertExchangeService(c, intake, NullLogger<PartnerWhtCertExchangeService>.Instance, spy)
            .DeliverForPayerAsync(s.Buyer.Id, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
    }

    private static async Task<List<PartnerJourneyService.JourneyView>> JourneyAsync(Guid companyId, Guid? docId = null)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await new PartnerJourneyService(c).ListAsync(companyId, true, true, docId);
    }

    [Fact]
    public async Task ครบเส้น_ใบแจ้งหนี้10700_จ่าย10400ผ่านลิงก์_ใบกำกับถึงผู้ซื้อ_ใบสำคัญจ่ายพร้อม50ทวิ300_50ทวิถึงผู้ขาย_แจ้งทุกขั้นทั้งสองฝั่ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var spy = new Spy();
        var s = await SeedAsync(db);
        var inv = await InvoiceAsync(db, s);

        // ก) ใบแจ้งหนี้ ⇒ ใบซื้อร่างของผู้ซื้อ + แจ้งผู้ซื้อ (เดิมไม่แจ้ง) + ผู้ติดต่อฝั่งผู้ขายถูกผูกด้วยกุญแจ (281-4)
        var purchaseId = await RouteAndApproveAsync(s, inv, spy);
        Assert.True(spy.Has(s.Buyer.Id, NotificationEvents.PartnerPurchaseDraftReceived));
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var ct = await c.Contacts.AsNoTracking().SingleAsync(x => x.Id == s.BuyerAtSeller.Id && x.CompanyId == s.Seller.Id);
            Assert.Equal(PartnerWhtCertExchangeService.ExternalSystem, ct.ExternalSystem);
            Assert.Equal(s.Buyer.Id.ToString(), ct.ExternalId);
            var pur = await c.Documents.AsNoTracking().SingleAsync(d => d.Id == purchaseId && d.CompanyId == s.Buyer.Id);
            Assert.Equal(10_400m, pur.TotalAmount);
            Assert.Equal(10_400m, pur.BalanceDue);
            Assert.Equal(700m, pur.VatAmount);
            Assert.Equal(300m, pur.WithholdingTaxAmount);
        }

        // ข)(1) ผู้ซื้อจ่าย 10,400 ผ่านลิงก์ (หัก 300) ⇒ ค) ผู้ขายได้ใบเสร็จ/ใบกำกับ ณ วันรับเงินอัตโนมัติ ⇒ เข้าระบบผู้ซื้อ แนบใบซื้อเดิม
        var sellerPayId = await BuyerPaysThroughLinkAsync(s, inv.Id, 10_400m, 300m, spy);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var pay = await c.Payments.AsNoTracking().SingleAsync(p => p.Id == sellerPayId && p.CompanyId == s.Seller.Id);
            var receipt = await c.Documents.AsNoTracking().SingleAsync(d => d.Id == pay.ReceiptDocumentId && d.CompanyId == s.Seller.Id);
            Assert.True(PartnerExchangeRules.IsTaxInvoiceCarrier(receipt.DocumentType, receipt.IsTaxInvoiceByLaw, receipt.VatAmount, receipt.ReceiptForm));
            Assert.Equal(700m, receipt.VatAmount);
            var rLink = await c.CrossTenantDocumentLinks.AsNoTracking()
                .SingleAsync(l => l.SourceCompanyId == s.Seller.Id && l.SourceDocumentId == receipt.Id && l.LinkType == CrossTenantLinkType.ReceiptFlow);
            Assert.Equal(purchaseId, rLink.TargetDocumentId);   // ไม่สร้างใบซื้อใบที่สอง
            Assert.Equal(1, await c.Documents.CountAsync(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId != null));
            Assert.Equal(300m, await c.WhtCreditsReceived.Where(w => w.CompanyId == s.Seller.Id && w.DocumentId == inv.Id).SumAsync(w => w.WhtAmount));
            var notice = await c.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters().SingleAsync(n => n.CompanyId == s.Buyer.Id && n.SellerPaymentId == sellerPayId);
            Assert.Equal(10_400m, notice.CashAmount);
            Assert.Equal(300m, notice.SellerRecordedWht);
        }
        Assert.True(spy.Has(s.Buyer.Id, NotificationEvents.PartnerTaxInvoiceReceived));
        Assert.True(spy.Has(s.Seller.Id, NotificationEvents.PartnerReceiptDelivered));
        Assert.True(spy.Has(s.Buyer.Id, NotificationEvents.PartnerSellerPaid));
        Assert.True(spy.Has(s.Seller.Id, NotificationEvents.PaymentReceived));

        // ง) ท่อของผู้ซื้อ (ความยินยอม + หลักฐานรายการเดินบัญชี) ⇒ เส้นจ่ายจริง ⇒ ใบสำคัญจ่าย 10,400 + ภาษีหัก 300 + 50 ทวิ 300
        Assert.Equal(1, await BuyerPipelineAsync(s, spy));
        Assert.True(spy.Has(s.Buyer.Id, NotificationEvents.PartnerPaymentAutoRecorded));
        Guid certId;
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var bp = await c.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == s.Buyer.Id && p.DocumentId == purchaseId);
            Assert.Equal(10_400m, bp.Amount);
            Assert.Equal(300m, bp.WithholdingTaxAmount);
            Assert.Equal(s.BankB, bp.BankAccountId);
            var cert = await c.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.CompanyId == s.Buyer.Id && w.DocumentId == purchaseId);
            Assert.Equal(300m, cert.TotalTaxAmount);
            Assert.Equal(WithholdingTaxCertStatus.Issued, cert.Status);
            certId = cert.Id;
        }
        Assert.Equal(0, await BuyerPipelineAsync(s, spy));   // รันซ้ำไม่จ่ายซ้ำ

        // จ) 50 ทวิ กลับเข้าระบบผู้ขาย ⇒ ยืนยันเองด้วยที่มา (ปิด AI ทุกตัว) ⇒ เครดิตภาษีถูกหักของผู้ขายยืนยันแล้ว
        Assert.Equal(1, await ReturnWhtAsync(s, spy));
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var cert = await c.WithholdingTaxCerts.AsNoTracking().SingleAsync(w => w.Id == certId && w.CompanyId == s.Buyer.Id);
            Assert.NotNull(cert.PartnerDeliveredAt);
            var intake = await c.WhtCertIntakes.AsNoTracking().SingleAsync(i => i.Id == cert.PartnerIntakeId && i.CompanyId == s.Seller.Id);
            Assert.Equal(WhtCertIntakeRecordStatus.Confirmed, intake.Status);
            Assert.True(intake.AuthenticityByProvenance);
        }
        Assert.True(spy.Has(s.Buyer.Id, NotificationEvents.PartnerWhtCertDelivered));
        Assert.True(spy.Has(s.Seller.Id, NotificationEvents.PartnerWhtCertReceived));
        Assert.Equal(0, await ReturnWhtAsync(s, spy));   // ส่งซ้ำไม่ได้

        // ฉ) เส้นทางของใบ — ทั้งสองมุมครบทุกขั้น · ผู้ขายไม่เห็นเลขใบสำคัญจ่ายของผู้ซื้อ
        var asBuyer = Assert.Single(await JourneyAsync(s.Buyer.Id, purchaseId));
        foreach (var r in asBuyer.Steps) _out.WriteLine($"B {r.Step} {r.State} {r.Detail}");
        Assert.Equal("Buyer", asBuyer.Direction);
        Assert.Null(asBuyer.CurrentStep);
        var asSeller = Assert.Single(await JourneyAsync(s.Seller.Id, inv.Id));
        foreach (var r in asSeller.Steps) _out.WriteLine($"A {r.Step} {r.State} {r.Detail}");
        Assert.Null(asSeller.CurrentStep);
        Assert.All(asSeller.Steps, r => Assert.Equal(nameof(PartnerJourney.State.Done), r.State));

        // ไม่มีการแจ้งข้ามบริษัทผิดฝั่ง: ทุกคีย์ต่อขั้นถึงเฉพาะสองบริษัทนี้
        Assert.All(spy.Sent.Where(x => PartnerFlowNotificationSplit.Keys.Contains(x.Key)), x => Assert.Contains(x.Company, new[] { s.Seller.Id, s.Buyer.Id }));
        using var chk = DbTestDatabase.TryCreateContext()!;
        foreach (var cid in new[] { s.Seller.Id, s.Buyer.Id })
            Assert.False(AuditHashChain.Analyze(await chk.AuditLogs.AsNoTracking().Where(a => a.CompanyId == cid && a.RowHash != null).OrderBy(a => a.Id).ToListAsync())
                .HasIntegrityFindings);
    }

    [Fact]
    public async Task ไม่มีความยินยอม_ไม่บันทึกจ่ายเอง_ค้างเป็นคิวคนพร้อมเหตุ_เส้นทางแสดงหยุด_ไม่มี50ทวิ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var spy = new Spy();
        var s = await SeedAsync(db, consent: false);
        var inv = await InvoiceAsync(db, s);
        var purchaseId = await RouteAndApproveAsync(s, inv, spy);
        await BuyerPaysThroughLinkAsync(s, inv.Id, 10_400m, 300m, spy);
        Assert.Equal(0, await BuyerPipelineAsync(s, spy));
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(await c.Payments.AnyAsync(p => p.CompanyId == s.Buyer.Id && p.DocumentId == purchaseId));
            Assert.False(await c.WithholdingTaxCerts.AnyAsync(w => w.CompanyId == s.Buyer.Id && w.DocumentId == purchaseId));
            var n = await c.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters().SingleAsync(x => x.CompanyId == s.Buyer.Id && x.TargetDocumentId == purchaseId);
            Assert.Equal(PartnerPaymentNoticeRule.StatusOpen, n.Status);   // ปุ่มเดียว "บันทึกจ่ายตามที่ผู้ขายรับแล้ว" ยังรอคน (ท่อไม่แตะบริษัทที่ไม่ยินยอม)
        }
        Assert.False(spy.Has(s.Buyer.Id, NotificationEvents.PartnerPaymentAutoRecorded));
        Assert.True(spy.Has(s.Buyer.Id, NotificationEvents.PartnerSellerPaid));   // แต่คนรู้ว่าต้องทำ
        var j = Assert.Single(await JourneyAsync(s.Buyer.Id, purchaseId));
        Assert.Equal(nameof(PartnerJourney.Step.BuyerRecorded), j.CurrentStep);
        var cur = j.Steps.Single(r => r.Step == j.CurrentStep);
        Assert.Equal(nameof(PartnerJourney.State.Waiting), cur.State);
        Assert.Contains("บันทึกจ่ายตามที่ผู้ขายรับแล้ว", cur.Detail);   // ทางไปต่อของคน
        // ขั้นก่อนหน้าเสร็จจริง (ใบกำกับถึงผู้ซื้อแล้ว) — ไม่ใช่ทุกอย่าง "รอ"
        Assert.Equal(nameof(PartnerJourney.State.Done), j.Steps.Single(r => r.Step == nameof(PartnerJourney.Step.TaxInvoice)).State);
        // ผู้ขายเห็นว่ารอผู้ซื้อ — ไม่เห็นคำแนะนำภายในของผู้ซื้อ
        var js = Assert.Single(await JourneyAsync(s.Seller.Id, inv.Id));
        Assert.DoesNotContain(cur.Detail!, string.Join("|", js.Steps.Select(r => r.Detail)));
    }

    [Fact]
    public async Task ยอดที่ผู้ขายรับไม่ตรงยอดค้าง_ไม่บันทึกจ่ายเอง_แม้ยินยอม_ทิศตรงข้ามบริษัทที่สามไม่เห็นเส้นทาง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var spy = new Spy();
        var s = await SeedAsync(db);
        var inv = await InvoiceAsync(db, s);
        var purchaseId = await RouteAndApproveAsync(s, inv, spy);
        // ผู้ขายรับ 10,000 ไม่หัก (ไม่ตรงยอดค้าง 10,400 + ภาษีหัก 300 · ใบมี VAT ⇒ ไม่เดาสัดส่วน) ⇒ ท่อไม่บันทึกเอง · คิวคนพร้อมยอดสองข้าง
        await BuyerPaysThroughLinkAsync(s, inv.Id, 10_000m, 0m, spy);
        Assert.Equal(0, await BuyerPipelineAsync(s, spy));
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            Assert.False(await c.Payments.AnyAsync(p => p.CompanyId == s.Buyer.Id && p.DocumentId == purchaseId));
            var n = await c.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters().SingleAsync(x => x.CompanyId == s.Buyer.Id && x.TargetDocumentId == purchaseId);
            Assert.Equal(PartnerPaymentNoticeRule.StatusOpen, n.Status);
            Assert.NotNull(n.AutoBlockedReason);
        }
        // tenant isolation: บริษัทที่สามไม่เห็นเส้นทางของคู่นี้
        var other = new Company { Name = "บริษัทที่สาม 281", TaxId = ValidTax(), BranchCode = "00000" };
        db.Companies.Add(other);
        await db.SaveChangesAsync();
        Assert.Empty(await JourneyAsync(other.Id));
        Assert.Empty(await JourneyAsync(other.Id, purchaseId));
        Assert.Empty(await JourneyAsync(other.Id, inv.Id));
        // สิทธิ์: ผู้ซื้อที่ไม่เห็นเอกสารซื้อ ⇒ ไม่เห็นทิศซื้อ
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Empty(await new PartnerJourneyService(c2).ListAsync(s.Buyer.Id, seeSale: true, seePurchase: false));
    }

    // ═════════ ฝ่ายค้านรอบ 281 ═════════

    /// <summary>การรับชำระของผู้ขายเขียนตรง (แถวจริงในบริษัทผู้ขาย) แล้วขั้นหลัง commit แจ้งผู้ซื้อ — ทางเดียวกับ NotifyPartnerOfSellerPaymentAsync
    /// (ใช้เมื่อเส้นรับชำระจริงของผู้ขายไม่รับยอดนั้น เช่น เงินเกินยอดค้างของผู้ขาย)</summary>
    private static async Task<Guid> SellerRowPaysAsync(Seed s, Document inv, decimal cash, decimal wht)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var pay = new Payment
        {
            CompanyId = s.Seller.Id, PaymentNumber = "RC281-" + Random.Shared.Next(100000, 999999), DocumentId = inv.Id, PaymentDate = Today,
            Amount = cash, WithholdingTaxAmount = wht, PaymentMethod = PaymentMethod.BankTransfer, Reference = "MAN-" + Guid.NewGuid().ToString("N")[..6],
        };
        c.Payments.Add(pay);
        await c.SaveChangesAsync();
        using var c2 = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await PartnerPaymentNoticeService.NotifyForSellerPaymentAsync(c2, null, NullLogger.Instance, s.Seller.Id, pay.Id, PaymentEntrySource.Manual));
        return pay.Id;
    }

    [Fact]
    public async Task ฝ่ายค้านMED1_ผู้ซื้อโอนเต็ม10700ไม่หัก_ใบซื้อหัก300_ไม่จ่ายเกินยอดค้าง_ไม่โยน_ค้างพร้อมเหตุยอดไม่ตรง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var spy = new Spy();
        var s = await SeedAsync(db);
        var inv = await InvoiceAsync(db, s);
        var purchaseId = await RouteAndApproveAsync(s, inv, spy);
        var sellerPay = await SellerRowPaysAsync(s, inv, 10_700m, 0m);
        Assert.Equal(0, await BuyerPipelineAsync(s, spy));   // เดิม: แผน "จ่าย 10,700 หัก 0" ⇒ เส้นจ่ายโยน "มากกว่ายอดค้างชำระ"
        using var c = DbTestDatabase.TryCreateContext()!;
        Assert.False(await c.Payments.AnyAsync(p => p.CompanyId == s.Buyer.Id && p.DocumentId == purchaseId));
        var n = await c.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters().SingleAsync(x => x.CompanyId == s.Buyer.Id && x.SellerPaymentId == sellerPay);
        Assert.Equal(PartnerPaymentNoticeRule.StatusOpen, n.Status);
        var (plan, _) = await PartnerPaymentNoticeService.PlanForAsync(c, s.Buyer.Id, n);
        Assert.Null(plan.Amount);
        Assert.Contains("ยอดไม่ตรง", plan.Problem);   // ปุ่มเดียวแสดงเหตุนี้แทนการเสนอจ่ายเกิน
    }

    [Fact]
    public async Task ฝ่ายค้านMED2_LOW1_มีแถวผูกคู่ค้านี้อยู่แล้ว_ไม่ผูกซ้ำ_ผู้ติดต่อจากเงินเดือน_ไม่ผูก_ทิศตรงข้ามแถวปกติผูก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var spy = new Spy();
        var s = await SeedAsync(db, consent: false);
        // แถวที่สองของผู้ซื้อรายเดิม (เลขเดียวกัน) ผูกคู่ค้านี้ไว้แล้ว ⇒ ส่งใบถึงแถวแรก (ยังไม่ผูก) ต้องไม่ได้แถวผูกที่สอง
        var linked = new Contact { CompanyId = s.Seller.Id, Name = s.Buyer.Name + " (สาขาบัญชี)", TaxId = s.Buyer.TaxId, BranchCode = "00000", IsCustomer = true,
            ExternalSystem = PartnerWhtCertExchangeService.ExternalSystem, ExternalId = s.Buyer.Id.ToString() };
        db.Contacts.Add(linked);
        await db.SaveChangesAsync();
        var inv = await InvoiceAsync(db, s);
        await RouteAndApproveAsync(s, inv, spy);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var first = await c.Contacts.AsNoTracking().SingleAsync(x => x.Id == s.BuyerAtSeller.Id && x.CompanyId == s.Seller.Id);
            Assert.Null(first.ExternalSystem);
            Assert.Equal(1, await c.Contacts.CountAsync(x => x.CompanyId == s.Seller.Id && x.ExternalSystem == PartnerWhtCertExchangeService.ExternalSystem
                && x.ExternalId == s.Buyer.Id.ToString()));
        }

        // LOW-1: ผู้ติดต่อที่เส้นเงินเดือนสร้าง (กุญแจ PayrollJunkPayee) ⇒ ไม่ผูก แม้เลขตรงคู่ค้า
        var spy2 = new Spy();
        var s2 = await SeedAsync(db, consent: false);
        await db.Contacts.Where(x => x.Id == s2.BuyerAtSeller.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.CreatedBy, PayrollJunkPayee.CreatedByPayrollPnd1));
        var inv2 = await InvoiceAsync(db, s2);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.True((await Xt(c, spy2).RouteSentDocumentAsync(inv2)).Routed);
        using (var c = DbTestDatabase.TryCreateContext()!)
            Assert.Null((await c.Contacts.AsNoTracking().SingleAsync(x => x.Id == s2.BuyerAtSeller.Id)).ExternalSystem);
    }

    [Fact]
    public async Task ฝ่ายค้านLOW5_ผู้เรียกถือธุรกรรม_ไม่แจ้งก่อนcommit_flushหลังcommitแจ้ง_ธุรกรรมย้อน_ไม่แจ้ง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db, consent: false);
        // commit
        var spy = new Spy();
        var inv = await InvoiceAsync(db, s);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var xt = Xt(c, spy);
            await using (var tx = await c.Database.BeginTransactionAsync())
            {
                Assert.NotNull(await xt.OnDocumentSentAsync(inv));
                Assert.False(spy.Has(s.Buyer.Id, NotificationEvents.PartnerPurchaseDraftReceived));   // ยังไม่ commit ⇒ ยังไม่แจ้ง
                await tx.CommitAsync();
            }
            await xt.FlushPendingNoticesAsync();
        }
        Assert.True(spy.Has(s.Buyer.Id, NotificationEvents.PartnerPurchaseDraftReceived));
        // rollback ⇒ ใบซื้อร่างไม่อยู่ ⇒ flush ไม่แจ้ง
        var spy2 = new Spy();
        var inv2 = await InvoiceAsync(db, s);
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var xt = Xt(c, spy2);
            await using (var tx = await c.Database.BeginTransactionAsync())
            {
                await xt.OnDocumentSentAsync(inv2);
                await tx.RollbackAsync();
            }
            c.ChangeTracker.Clear();
            await xt.FlushPendingNoticesAsync();
        }
        Assert.Empty(spy2.Sent);
    }

    [Fact]
    public async Task ฝ่ายค้านLOW6_คัดลอกค่าตั้งแจ้งเตือนครั้งเดียวต่อบริษัท_ลบแถวคีย์ใหม่แล้วบูตซ้ำ_ไม่คัดลอกซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var co = new Company { Name = "แจ้งเตือน 281 " + Guid.NewGuid().ToString("N")[..6], TaxId = ValidTax(), BranchCode = "00000" };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        db.NotificationSettings.Add(new NotificationSetting { CompanyId = co.Id, EventKey = NotificationEvents.AutopilotPartnerExchange, RecipientRole = "Owner",
            EnableSystem = true, EnableEmail = false, EnableLine = true, EnableLineGroup = false });
        await db.SaveChangesAsync();
        // ธงของบริษัทอื่นในฐานร่วมอาจมีจากเทสต์ก่อน — ล้างเฉพาะบริษัทนี้ไม่จำเป็น (บริษัทใหม่ยังไม่มีธง)
        await DbTestDatabase.ApplyDdlAsync(db, new[] { PartnerFlowNotificationSplit.MigrationSql });
        using (var c = DbTestDatabase.TryCreateContext()!)
        {
            var copied = await c.NotificationSettings.AsNoTracking().Where(x => x.CompanyId == co.Id && x.EventKey == NotificationEvents.PartnerSellerPaid).ToListAsync();
            var row = Assert.Single(copied);
            Assert.True(row.EnableLine);       // ค่าตั้งเดิมตามมา
            Assert.False(row.EnableEmail);
            Assert.Equal(PartnerFlowNotificationSplit.Keys.Count,
                await c.NotificationSettings.CountAsync(x => x.CompanyId == co.Id && PartnerFlowNotificationSplit.Keys.Contains(x.EventKey)));
            // ผู้ใช้ลบแถวคีย์ใหม่ทิ้ง (เช่น ล้างค่าตั้ง) ⇒ บูตถัดไปต้องไม่คัดลอกกลับมา
            await c.NotificationSettings.Where(x => x.CompanyId == co.Id && x.EventKey == NotificationEvents.PartnerSellerPaid).ExecuteDeleteAsync();
        }
        await DbTestDatabase.ApplyDdlAsync(db, new[] { PartnerFlowNotificationSplit.MigrationSql });
        using var chk = DbTestDatabase.TryCreateContext()!;
        Assert.False(await chk.NotificationSettings.AnyAsync(x => x.CompanyId == co.Id && x.EventKey == NotificationEvents.PartnerSellerPaid));
    }
}
