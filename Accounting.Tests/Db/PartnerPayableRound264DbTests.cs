using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
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
/// รอบ 264 P3 — ท่อฝั่งผู้ซื้อบน PostgreSQL จริง (ข้ามบริษัท · M6 รอบ 258): ยินยอม + หลักฐานบัญชี ⇒ บันทึกจ่ายหนึ่งครั้ง (ผู้บันทึก = ผู้ให้ · ภาษีหักเข้าเส้นจ่ายเดิม · รันซ้ำไม่เพิ่ม) ·
/// ไม่มีหลักฐานแหล่งเงิน ⇒ ค้างพร้อมเหตุ (audit ครั้งเดียวต่อเหตุ) · ไม่ยินยอม/เกินเพดาน ⇒ ไม่บันทึก · ถอนระหว่างท่อ ⇒ รอจนท่อจบ (ล็อกถือตลอดการบันทึก) ·
/// ผู้ขายยกเลิกการรับชำระ ⇒ ถอน/ป้ายบนใบซื้อ (ไม่ยกเลิกการจ่ายแทน) · ผู้ขายรับชำระมือ ⇒ มีการแจ้ง · จ่ายบางส่วน · ปิด AI ⇒ ผลเท่าเดิม ·
/// ผังใบร่างจากตัวแนะนำเดิม (ป้ายซื่อสัตย์) · แยกบริษัท · migration กุญแจ SellerPaymentId บนแถวเดิม
/// </summary>
[Trait("Category", "Db")]
public class PartnerPayableRound264DbTests
{
    private readonly ITestOutputHelper _out;
    public PartnerPayableRound264DbTests(ITestOutputHelper output) => _out = output;
    private const string Skip = "ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test";

    private static readonly Random Rng = new();
    private static string RandTax()
    {
        lock (Rng) return "09" + string.Concat(Enumerable.Range(0, 11).Select(_ => (char)('0' + Rng.Next(10))));
    }

    private sealed record Seed(Company Seller, Company Buyer, Guid PartnershipId, Contact BuyerAtSeller, Guid OwnerB, Guid BankB, Guid ExpenseCoaB);

    private static CrossTenantWorkflowService Xt(AccountingDbContext db) => new(db, NullLogger<CrossTenantWorkflowService>.Instance);
    private static PartnerConsentService Consent(AccountingDbContext db) => new(db, new PermissionService(db), NullLogger<PartnerConsentService>.Instance);

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        foreach (var sql in PartnerConsentMigration.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
        var seller = new Company { Name = "ผู้ขาย P3 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000", PartnerDiscoverable = true };
        var buyer = new Company { Name = "ผู้ซื้อ P3 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000" };
        db.Companies.AddRange(seller, buyer);
        await db.SaveChangesAsync();
        var (a, b) = seller.Id.CompareTo(buyer.Id) < 0 ? (seller.Id, buyer.Id) : (buyer.Id, seller.Id);
        var p = new TradingPartnership { CompanyAId = a, CompanyBId = b, InvitedByCompanyId = seller.Id, Status = TradingPartnershipStatus.Accepted, AcceptedAt = DateTime.UtcNow.AddDays(-1) };
        db.TradingPartnerships.Add(p);
        db.WorkflowAutomationConfigs.Add(new WorkflowAutomationConfig { CompanyId = buyer.Id, CreatePurchaseDraftsFromPartners = true });
        var contact = new Contact { CompanyId = seller.Id, Name = buyer.Name, TaxId = buyer.TaxId, BranchCode = "00000", IsCustomer = true };
        db.Contacts.Add(contact);
        var bankGl = new ChartOfAccount { CompanyId = buyer.Id, AccountCode = "11120", AccountName = "เงินฝากธนาคาร", AccountType = AccountType.Asset, Level = 4, IsActive = true };
        var expense = new ChartOfAccount { CompanyId = buyer.Id, AccountCode = "53100", AccountName = "ค่าบริการ", AccountType = AccountType.Expense, Level = 4, IsActive = true };
        db.ChartOfAccounts.AddRange(bankGl, expense);
        await db.SaveChangesAsync();
        var bank = new BankAccount { CompanyId = buyer.Id, AccountName = "กระแสรายวัน", BankName = "กสิกร", AccountNumber = "1112223334", LinkedAccountId = bankGl.Id };
        db.BankAccounts.Add(bank);
        var u = new User { Email = $"p3-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "เจ้าของผู้ซื้อ P3" };
        db.Users.Add(u);
        db.CompanyUsers.Add(new CompanyUser { UserId = u.Id, CompanyId = buyer.Id, Role = UserRole.Owner });
        await db.SaveChangesAsync();
        return new Seed(seller, buyer, p.Id, contact, u.Id, bank.Id, expense.Id);
    }

    /// <summary>ใบแจ้งหนี้ของผู้ขาย → ใบซื้อร่างของผู้ซื้อ (เส้นเดิม) → อนุมัติ (จำลอง) · <paramref name="wht"/> = ภาษีหักของใบซื้อ (อัตรา 3% ทุกบรรทัด)</summary>
    private static async Task<(Document Invoice, Guid PurchaseId)> RoutedAsync(AccountingDbContext db, Seed s, decimal wht = 0m, bool approve = true)
    {
        var inv = new Document
        {
            CompanyId = s.Seller.Id, ContactId = s.BuyerAtSeller.Id, DocumentType = DocumentType.Invoice, DocumentNumber = "SP3-" + Guid.NewGuid().ToString("N")[..8],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, VatAmount = 70m, TotalAmount = 1_070m, BalanceDue = 1_070m, Currency = "THB",
        };
        inv.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการรายเดือน", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m, VatRate = 7m, VatAmount = 70m });
        db.Documents.Add(inv);
        await db.SaveChangesAsync();
        var link = await Xt(db).OnDocumentSentAsync(inv);
        using var c = DbTestDatabase.TryCreateContext()!;
        var pid = await c.Documents.AsNoTracking().Where(d => d.CompanyId == s.Buyer.Id && d.PartnerSourceLinkId == link!.Id).Select(d => d.Id).SingleAsync();
        if (approve)
            await c.Documents.Where(d => d.Id == pid).ExecuteUpdateAsync(x => x.SetProperty(d => d.Status, DocumentStatus.Approved)
                .SetProperty(d => d.BalanceDue, 1_070m).SetProperty(d => d.WithholdingTaxAmount, wht));
        if (wht > 0m)
            await c.DocumentLines.Where(l => l.DocumentId == pid).ExecuteUpdateAsync(x => x.SetProperty(l => l.WithholdingTaxRate, 3m));
        return (inv, pid);
    }

    /// <summary>ผู้ขายบันทึกรับชำระ (แถวจริงในบริษัทผู้ขาย) แล้วขั้นหลัง commit แจ้งผู้ซื้อ — ทางเดียวกับ DocumentService.NotifyPartnerOfSellerPaymentAsync</summary>
    private static async Task<(Guid PaymentId, int Created)> SellerPaysAsync(Seed s, Document inv, decimal cash, decimal wht, PaymentEntrySource source = PaymentEntrySource.Manual)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var pay = new Payment
        {
            CompanyId = s.Seller.Id, PaymentNumber = "RCP3-" + Random.Shared.Next(100000, 999999), DocumentId = inv.Id, PaymentDate = DateTime.UtcNow.Date,
            Amount = cash, WithholdingTaxAmount = wht, PaymentMethod = PaymentMethod.BankTransfer, Reference = "MAN-" + Guid.NewGuid().ToString("N")[..6],
        };
        c.Payments.Add(pay);
        await c.SaveChangesAsync();
        using var c2 = DbTestDatabase.TryCreateContext()!;
        var n = await PartnerPaymentNoticeService.NotifyForSellerPaymentAsync(c2, null, NullLogger.Instance, s.Seller.Id, pay.Id, source);
        return (pay.Id, n);
    }

    private static async Task<Guid> GrantAsync(Seed s, decimal max, Guid? bank)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        var pv = await Consent(c).PreviewAsync(s.Buyer.Id, s.OwnerB, new PartnerConsentService.PreviewRequest(s.PartnershipId, Scope.AutoRecordPayment.ToString(), max, bank));
        var row = await Consent(c).GrantAsync(s.Buyer.Id, s.OwnerB, new PartnerConsentService.GrantRequest(s.PartnershipId, Scope.AutoRecordPayment.ToString(), pv.MaxAmount, bank,
            pv.TextVersion, pv.EvidenceHash, PartnerConsent.ConfirmPhrase));
        return row.Id;
    }

    private sealed record Call(CreatePaymentRequest Req, string CreatedBy, PaymentEntrySource Source);

    /// <summary>IDocumentService ปลอม — CreatePaymentAsync เขียนแถว Payment จริงของผู้ซื้อ · <paramref name="during"/> = งานระหว่างการบันทึก (เช่น ลองถอนความยินยอม)</summary>
    private static IDocumentService PayingDocs(Guid buyerId, List<Call> calls, Func<Task>? during = null) => F4Proxy<IDocumentService>.Create((m, args) =>
    {
        if (m != nameof(IDocumentService.CreatePaymentAsync)) return null;
        var req = (CreatePaymentRequest)args[1]!;
        calls.Add(new Call(req, (string)args[2]!, (PaymentEntrySource)args[3]!));
        async Task<PaymentResponse> Run()
        {
            if (during != null) await during();
            using var other = DbTestDatabase.TryCreateContext()!;
            var pay = new Payment
            {
                CompanyId = buyerId, PaymentNumber = "PVP3-" + Random.Shared.Next(100000, 999999), DocumentId = req.DocumentId, PaymentDate = req.PaymentDate,
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

    /// <summary>scope ใหม่ของท่อ (ธุรกรรมล็อกความยินยอมบน connection แยก) — แบบเดียวกับ DI จริง</summary>
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

    private static PartnerPayablePipeline Pipe(AccountingDbContext db, IDocumentService docs, IOcrAiAugmenter? ai = null)
        => new(db, docs, new PipelineScopes(), NullLogger<PartnerPayablePipeline>.Instance, ai);

    private static Task<PartnerPaymentNotice> NoticeAsync(AccountingDbContext db, Guid buyerId, Guid sellerPaymentId)
        => db.PartnerPaymentNotices.AsNoTracking().IgnoreQueryFilters().SingleAsync(n => n.CompanyId == buyerId && n.SellerPaymentId == sellerPaymentId);

    private static Task ResetWatermarkAsync(AccountingDbContext db, Guid noticeId)
        => db.PartnerPaymentNotices.IgnoreQueryFilters().Where(n => n.Id == noticeId).ExecuteUpdateAsync(x => x.SetProperty(n => n.AutoEvaluatedAt, (DateTime?)null));

    private static Task<List<AuditLog>> ChainAsync(AccountingDbContext db, Guid companyId)
        => db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId && a.RowHash != null).OrderBy(a => a.Id).ToListAsync();

    private static async Task<List<string>> EventsAsync(AccountingDbContext db, Guid companyId, string evt)
        => (await db.AuditLogs.AsNoTracking().Where(a => a.CompanyId == companyId && a.EntityType == nameof(PartnerPaymentNotice)).Select(a => a.NewValues!).ToListAsync())
            .Where(v => v.Contains("\"event\":\"" + evt + "\"")).ToList();

    [Fact]
    public async Task ผู้ขายรับชำระมือ_มีการแจ้ง_ยินยอมและมีบัญชีจ่าย_บันทึกจ่ายครั้งเดียว_ผู้บันทึกคือผู้ให้_ภาษีหักเข้าเส้นเดิม_รันซ้ำไม่เพิ่ม_chain_สะอาด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (inv, purchaseId) = await RoutedAsync(db, s, wht: 30m);
        var consentId = await GrantAsync(s, 5_000m, s.BankB);
        var (payId, created) = await SellerPaysAsync(s, inv, 1_040m, 30m);
        Assert.Equal(1, created);   // C-04: รับชำระมือ (ไม่ผ่าน gateway) ก็แจ้ง
        Assert.Equal(0, (await SellerPaysAsyncAgain(s, payId)));   // ขั้นหลัง commit ซ้ำ ⇒ ไม่แจ้งซ้ำ (กุญแจ = การรับชำระ)

        using (var c0 = DbTestDatabase.TryCreateContext()!)
        {
            var n0 = await NoticeAsync(c0, s.Buyer.Id, payId);
            Assert.Equal(PartnerPaymentNoticeRule.EntryManual, n0.SourceChannel);
            Assert.Null(n0.PaymentIntentId);
            Assert.Equal(purchaseId, n0.TargetDocumentId);
        }
        var calls = new List<Call>();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c1, PayingDocs(s.Buyer.Id, calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow));
        var call = Assert.Single(calls);
        Assert.Equal(s.OwnerB.ToString(), call.CreatedBy);                    // 264-2-3: GUID ผู้ให้ ไม่ใช่สตริงระบบ
        Assert.Equal(PaymentEntrySource.PartnerAuto, call.Source);
        Assert.Equal(s.BankB, call.Req.OverrideBankAccountId);               // ชั้น ConsentDefault (ไม่ใช่เงินสด/บัญชีแรก)
        Assert.Equal(1_040m, call.Req.Amount);
        Assert.Equal(30m, call.Req.WithholdingTaxAmount);                    // ภาษีหักของผู้ซื้อเข้าเส้นจ่ายเดิม ⇒ 50 ทวิ ออกตามเส้นเดิม (WhtCertAutoIssue)
        Assert.Equal(PartnerPaymentNoticeRule.PaymentReferenceForSellerPayment(payId), call.Req.Reference);

        Guid noticeId;
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var n = await NoticeAsync(c2, s.Buyer.Id, payId);
            noticeId = n.Id;
            Assert.Equal(PartnerPaymentNoticeRule.StatusRecorded, n.Status);
            Assert.NotNull(n.RecordedPaymentId);
            var auto = Assert.Single(await EventsAsync(c2, s.Buyer.Id, "PartnerPaymentAutoRecorded"));
            Assert.Contains(consentId.ToString(), auto);
            Assert.Contains("ConsentDefault", auto);
            Assert.Contains(PartnerAutoRecordPolicy.RuleCode, auto);
        }
        // รันซ้ำ (watermark ถูกล้าง) ⇒ ยังการจ่ายเดียว
        using (var c3 = DbTestDatabase.TryCreateContext()!)
        {
            await ResetWatermarkAsync(c3, noticeId);
            Assert.Equal(0, await Pipe(c3, PayingDocs(s.Buyer.Id, calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow));
        }
        Assert.Single(calls);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(1, await check.Payments.CountAsync(p => p.CompanyId == s.Buyer.Id && p.Reference == PartnerPaymentNoticeRule.PaymentReferenceForSellerPayment(payId)));
        foreach (var cid in new[] { s.Seller.Id, s.Buyer.Id })
            Assert.False(AuditHashChain.Analyze(await ChainAsync(check, cid)).HasIntegrityFindings);
    }

    private static async Task<int> SellerPaysAsyncAgain(Seed s, Guid payId)
    {
        using var c = DbTestDatabase.TryCreateContext()!;
        return await PartnerPaymentNoticeService.NotifyForSellerPaymentAsync(c, null, NullLogger.Instance, s.Seller.Id, payId, PaymentEntrySource.Manual);
    }

    [Fact]
    public async Task ยินยอมแต่ไม่มีหลักฐานแหล่งเงิน_ไม่บันทึก_ค้างพร้อมเหตุ_audit_ครั้งเดียวต่อเหตุ_หน้าไม่_preselect()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (inv, _) = await RoutedAsync(db, s);
        await GrantAsync(s, 5_000m, null);   // ไม่ตั้งบัญชีจ่าย — "ใช้หลักฐานเท่านั้น"
        var (payId, _) = await SellerPaysAsync(s, inv, 1_070m, 0m);
        var calls = new List<Call>();
        Guid noticeId;
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await Pipe(c1, PayingDocs(s.Buyer.Id, calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow));
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var n = await NoticeAsync(c2, s.Buyer.Id, payId);
            noticeId = n.Id;
            Assert.Equal(PartnerPaymentNoticeRule.StatusOpen, n.Status);
            Assert.Equal(PartnerPaySourceAccount.UnknownReason, n.AutoBlockedReason);
            Assert.Equal(PartnerAutoRecordPolicy.Codes.Source, n.AutoBlockedCode);
            await ResetWatermarkAsync(c2, noticeId);
        }
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(0, await Pipe(c3, PayingDocs(s.Buyer.Id, calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow));
        Assert.Empty(calls);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Single(await EventsAsync(check, s.Buyer.Id, "PartnerPaymentAutoBlocked"));   // เหตุเดิม ⇒ ไม่เขียนซ้ำ
        // C-01: หน้าไม่ preselect (ไม่มี "บัญชีแรก") · เงินสดเป็นตัวเลือกที่ต้องเลือกเอง
        var docs = F4Proxy<IDocumentService>.Create((_, _) => null);
        var row = (await new PartnerPaymentNoticeService(check, docs, NullLogger<PartnerPaymentNoticeService>.Instance, null, Consent(check)).ListAsync(s.Buyer.Id))
            .Single(x => x.Id == noticeId);
        Assert.Null(row.DefaultBankAccountId);
        Assert.Equal(nameof(PartnerPaySourceAccount.Origin.Unknown), row.SourceOrigin);
        Assert.Contains(row.Accounts!, o => o.Value == PartnerPaymentNoticeService.CashChoice);
        // ปุ่มเดียวโดยไม่เลือกบัญชี/เงินสด ⇒ 400 · เลือกบัญชีเอง ⇒ บันทึกได้ (ทางไปต่อของผู้ใช้)
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            new PartnerPaymentNoticeService(check, PayingDocs(s.Buyer.Id, calls), NullLogger<PartnerPaymentNoticeService>.Instance).RecordAsync(s.Buyer.Id, noticeId, s.OwnerB, null, null));
        Assert.Equal(400, ex.StatusCode);
        using var c4 = DbTestDatabase.TryCreateContext()!;
        var ok = await new PartnerPaymentNoticeService(c4, PayingDocs(s.Buyer.Id, calls), NullLogger<PartnerPaymentNoticeService>.Instance)
            .RecordAsync(s.Buyer.Id, noticeId, s.OwnerB, s.BankB, null);
        Assert.Equal(PartnerPaymentNoticeRule.StatusRecorded, ok.Status);
        Assert.Equal(s.OwnerB.ToString(), Assert.Single(calls).CreatedBy);
        Assert.Equal(PaymentEntrySource.Manual, calls[0].Source);
    }

    [Fact]
    public async Task ไม่ยินยอม_ไม่มีการจ่าย_เกินเพดาน_ไม่มีการจ่าย_พร้อมเหตุ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (inv1, _) = await RoutedAsync(db, s);
        var (pay1, _) = await SellerPaysAsync(s, inv1, 1_070m, 0m);
        var calls = new List<Call>();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            await Pipe(c1, PayingDocs(s.Buyer.Id, calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow);
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            Assert.Contains("ยังไม่ได้ให้ความยินยอม", (await NoticeAsync(c2, s.Buyer.Id, pay1)).AutoBlockedReason);

        await GrantAsync(s, 1_000m, s.BankB);   // เพดาน 1,000 < ใบ 1,070
        var (inv2, _) = await RoutedAsync(db, s);
        var (pay2, _) = await SellerPaysAsync(s, inv2, 1_070m, 0m);
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            await Pipe(c3, PayingDocs(s.Buyer.Id, calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow);
        Assert.Empty(calls);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Contains("เกินเพดาน", (await NoticeAsync(check, s.Buyer.Id, pay2)).AutoBlockedReason);
        Assert.Equal(0, await check.Payments.CountAsync(p => p.CompanyId == s.Buyer.Id));
    }

    [Fact]
    public async Task ถอนระหว่างท่อ_รอจนท่อบันทึกเสร็จ_ล็อกถือตลอดการบันทึก_หลังถอน_แถวถัดไปไม่บันทึก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (inv, _) = await RoutedAsync(db, s);
        var consentId = await GrantAsync(s, 5_000m, s.BankB);
        await SellerPaysAsync(s, inv, 1_070m, 0m);
        var calls = new List<Call>();
        Exception? blocked = null;
        async Task TryWithdraw()
        {
            using var other = DbTestDatabase.TryCreateContext()!;
            await other.Database.OpenConnectionAsync();
            await other.Database.ExecuteSqlRawAsync("SET lock_timeout = '500ms'");
            blocked = await Record.ExceptionAsync(() => Consent(other).WithdrawAsync(s.Buyer.Id, s.OwnerB, consentId, "ระหว่างท่อ"));
        }
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c1, PayingDocs(s.Buyer.Id, calls, TryWithdraw)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow));
        Assert.NotNull(blocked);   // FOR SHARE ของท่อกันการถอน (FOR UPDATE) จนการบันทึก commit (ฝ่ายค้าน P1 M1)

        // หลังท่อจบ ถอนได้ · การแจ้งใบถัดไปไม่บันทึกเอง
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            await Consent(c2).WithdrawAsync(s.Buyer.Id, s.OwnerB, consentId, "ถอนหลังท่อ");
        var (inv2, _) = await RoutedAsync(db, s);
        var (pay2, _) = await SellerPaysAsync(s, inv2, 1_070m, 0m);
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            await Pipe(c3, PayingDocs(s.Buyer.Id, calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow);
        Assert.Single(calls);
        using var check = DbTestDatabase.TryCreateContext()!;
        var n2 = await NoticeAsync(check, s.Buyer.Id, pay2);
        Assert.Equal(PartnerPaymentNoticeRule.StatusOpen, n2.Status);
        Assert.Equal(PartnerAutoRecordPolicy.Codes.Consent, n2.AutoBlockedCode);
    }

    [Fact]
    public async Task ผู้ขายยกเลิกการรับชำระ_แจ้งที่ค้าง_ถอน_บันทึกแล้ว_ป้ายบนใบซื้อ_ไม่ยกเลิกการจ่ายแทน_Autopilot_เห็น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var calls = new List<Call>();

        // (ก) ยังไม่บันทึก ⇒ ถอน · ปุ่มเดียวปฏิเสธ · ท่อไม่หยิบ
        var (inv1, _) = await RoutedAsync(db, s);
        var (pay1, _) = await SellerPaysAsync(s, inv1, 1_070m, 0m);
        await db.Payments.IgnoreQueryFilters().Where(p => p.Id == pay1).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsDeleted, true));
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await PartnerPaymentNoticeService.WithdrawForSellerPaymentAsync(c1, null, NullLogger.Instance, s.Seller.Id, pay1, DateTime.UtcNow));
        Guid n1Id;
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var n1 = await NoticeAsync(c2, s.Buyer.Id, pay1);
            n1Id = n1.Id;
            Assert.Equal(PartnerPaymentNoticeRule.StatusWithdrawn, n1.Status);
            Assert.NotNull(n1.SellerVoidedAt);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                new PartnerPaymentNoticeService(c2, PayingDocs(s.Buyer.Id, calls), NullLogger<PartnerPaymentNoticeService>.Instance).RecordAsync(s.Buyer.Id, n1Id, s.OwnerB, s.BankB, null));
            Assert.Equal(409, ex.StatusCode);
        }

        // (ข) บันทึกแล้ว ⇒ ป้ายบนใบซื้อ + แถว Autopilot · การจ่ายของผู้ซื้อยังอยู่
        await GrantAsync(s, 5_000m, s.BankB);
        var (inv2, purchase2) = await RoutedAsync(db, s);
        var (pay2, _) = await SellerPaysAsync(s, inv2, 1_070m, 0m);
        using (var c3 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c3, PayingDocs(s.Buyer.Id, calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow));
        await db.Payments.IgnoreQueryFilters().Where(p => p.Id == pay2).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsDeleted, true));
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await PartnerPaymentNoticeService.WithdrawForSellerPaymentAsync(c4, null, NullLogger.Instance, s.Seller.Id, pay2, DateTime.UtcNow));
        using (var c5 = DbTestDatabase.TryCreateContext()!)
        {
            var n2 = await NoticeAsync(c5, s.Buyer.Id, pay2);
            Assert.Equal(PartnerPaymentNoticeRule.StatusRecorded, n2.Status);
            Assert.NotNull(n2.SellerVoidedAt);
            var notes = await c5.Documents.AsNoTracking().Where(d => d.Id == purchase2).Select(d => d.InternalNotes).SingleAsync();
            Assert.Contains(PartnerPaymentNoticeRule.SellerVoidedMarker, notes);
            Assert.True(await c5.Payments.AnyAsync(p => p.Id == n2.RecordedPaymentId && !p.IsDeleted));   // E6: ไม่ยกเลิกการจ่ายของผู้ซื้อแทน
            var rows = await PartnerPayablePipeline.BlockedRowsAsync(c5, s.Buyer.Id, DateTime.UtcNow);
            Assert.Contains(rows, r => r.Id == n2.Id && r.SellerVoidedAfterRecord);
            Assert.Single(await EventsAsync(c5, s.Buyer.Id, "PartnerPaymentSellerVoided"));
            // เรียกซ้ำ ⇒ ไม่ซ้ำ
            Assert.Equal(0, await PartnerPaymentNoticeService.WithdrawForSellerPaymentAsync(c5, null, NullLogger.Instance, s.Seller.Id, pay2, DateTime.UtcNow));
        }

        // (ค) ตาข่ายชั้นสอง: ผู้ขายยกเลิกแต่ขั้นถอนไม่ได้ทำงาน ⇒ ท่อตรวจสดแล้วถอนเอง ไม่บันทึกจ่าย
        var (inv3, _) = await RoutedAsync(db, s);
        var (pay3, _) = await SellerPaysAsync(s, inv3, 1_070m, 0m);
        await db.Payments.IgnoreQueryFilters().Where(p => p.Id == pay3).ExecuteUpdateAsync(x => x.SetProperty(p => p.IsDeleted, true));
        var before = calls.Count;
        using (var c6 = DbTestDatabase.TryCreateContext()!)
            await Pipe(c6, PayingDocs(s.Buyer.Id, calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow);
        Assert.Equal(before, calls.Count);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(PartnerPaymentNoticeRule.StatusWithdrawn, (await NoticeAsync(check, s.Buyer.Id, pay3)).Status);
        foreach (var cid in new[] { s.Seller.Id, s.Buyer.Id })
            Assert.False(AuditHashChain.Analyze(await ChainAsync(check, cid)).HasIntegrityFindings);
    }

    [Fact]
    public async Task จ่ายบางส่วน_ไม่มีภาษีหัก_บันทึกเองตามเงินจริง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (inv, _) = await RoutedAsync(db, s);
        await GrantAsync(s, 5_000m, s.BankB);
        await SellerPaysAsync(s, inv, 500m, 0m);
        var calls = new List<Call>();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c1, PayingDocs(s.Buyer.Id, calls)).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow));
        var call = Assert.Single(calls);
        Assert.Equal(500m, call.Req.Amount);
        Assert.Equal(0m, call.Req.WithholdingTaxAmount);
    }

    [Fact]
    public async Task ปิด_AI_ทุกตัว_ท่อบันทึกจ่ายผลเท่าเดิม_ผังใบร่าง_ไม่มีคำตอบ_ใบไม่พัง_ทิศตรงข้าม_นักเรียนตอบ_เติมผังพร้อมป้ายซื่อสัตย์()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        // kill-switch: ตัวแนะนำผังตอบว่าง (AI ปิด + นักเรียนไม่มีคำตอบ) — เส้นเงินไม่ใช้ AI อยู่แล้ว
        var none = F4Proxy<IOcrAiAugmenter>.Create((m, _) => m == nameof(IOcrAiAugmenter.SuggestGlAccountAsync)
            ? new OcrAiAugmentationResult(null, null, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, false, null) : null);
        var (inv, _) = await RoutedAsync(db, s);
        await GrantAsync(s, 5_000m, s.BankB);
        await SellerPaysAsync(s, inv, 1_070m, 0m);
        var calls = new List<Call>();
        using (var c1 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c1, PayingDocs(s.Buyer.Id, calls), none).RunForCompanyAsync(s.Buyer.Id, DateTime.UtcNow));
        Assert.Single(calls);

        var (_, draftNone) = await RoutedAsync(db, s, approve: false);
        using (var c2 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c2, PayingDocs(s.Buyer.Id, calls), none).SuggestDraftGlAsync(s.Buyer.Id, DateTime.UtcNow));
        using (var c3 = DbTestDatabase.TryCreateContext()!)
        {
            var d = await c3.Documents.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == draftNone);
            Assert.Equal(DocumentStatus.Draft, d.Status);
            Assert.All(d.Lines, l => Assert.Null(l.AccountId));
            Assert.Contains(PartnerPayablePipeline.DraftGlMarker, d.InternalNotes);
            Assert.Contains(AiAnswerSource.Label(AiAnswerSource.Kind.None), d.InternalNotes);
            Assert.DoesNotContain("🤖", d.InternalNotes);
            // ถามครั้งเดียวต่อใบ
            Assert.Equal(0, await Pipe(c3, PayingDocs(s.Buyer.Id, calls), none).SuggestDraftGlAsync(s.Buyer.Id, DateTime.UtcNow));
        }

        // ทิศตรงข้าม: นักเรียนตอบผังที่มีในผังของบริษัท (มั่นใจ 0.9) ⇒ เติม + FeedbackId · ผังนอกชุด ⇒ ไม่เติม (anti-hallucination)
        var fid = Guid.NewGuid();
        var student = F4Proxy<IOcrAiAugmenter>.Create((m, _) => m == nameof(IOcrAiAugmenter.SuggestGlAccountAsync)
            ? new OcrAiAugmentationResult("53100", 0.9m, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, false, fid, FromStudent: true) : null);
        var (_, draftStudent) = await RoutedAsync(db, s, approve: false);
        using (var c4 = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(1, await Pipe(c4, PayingDocs(s.Buyer.Id, calls), student).SuggestDraftGlAsync(s.Buyer.Id, DateTime.UtcNow));
        using (var c5 = DbTestDatabase.TryCreateContext()!)
        {
            var d = await c5.Documents.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == draftStudent);
            Assert.Equal(DocumentStatus.Draft, d.Status);   // ไม่อนุมัติเอง
            Assert.All(d.Lines, l => { Assert.Equal(s.ExpenseCoaB, l.AccountId); Assert.Equal(fid, l.GlAccountAiFeedbackId); });
            Assert.Contains(AiAnswerSource.Label(AiAnswerSource.Kind.Student), d.InternalNotes);
        }
        var hallucinated = F4Proxy<IOcrAiAugmenter>.Create((m, _) => m == nameof(IOcrAiAugmenter.SuggestGlAccountAsync)
            ? new OcrAiAugmentationResult("99999", 0.99m, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), null, true, Guid.NewGuid()) : null);
        var (_, draftBad) = await RoutedAsync(db, s, approve: false);
        using (var c6 = DbTestDatabase.TryCreateContext()!)
            await Pipe(c6, PayingDocs(s.Buyer.Id, calls), hallucinated).SuggestDraftGlAsync(s.Buyer.Id, DateTime.UtcNow);
        using var check = DbTestDatabase.TryCreateContext()!;
        var bad = await check.Documents.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.Id == draftBad);
        Assert.All(bad.Lines, l => Assert.Null(l.AccountId));
        Assert.Contains("🤖", bad.InternalNotes);   // เรียก AI จริง ⇒ ป้าย AI (แม้คำตอบถูกทิ้ง)
    }

    [Fact]
    public async Task แยกบริษัท_ท่อของบริษัทอื่นไม่แตะการแจ้งของผู้ซื้อ_รายการของบริษัทอื่นว่าง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var other = new Company { Name = "บริษัทอื่น P3 " + Guid.NewGuid().ToString("N")[..6], TaxId = RandTax(), BranchCode = "00000" };
        db.Companies.Add(other);
        await db.SaveChangesAsync();
        var (inv, _) = await RoutedAsync(db, s);
        await GrantAsync(s, 5_000m, s.BankB);
        var (payId, _) = await SellerPaysAsync(s, inv, 1_070m, 0m);
        var calls = new List<Call>();
        Guid noticeId;
        using (var c0 = DbTestDatabase.TryCreateContext()!)
            noticeId = (await NoticeAsync(c0, s.Buyer.Id, payId)).Id;
        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            Assert.Equal(0, await Pipe(c1, PayingDocs(other.Id, calls)).RunForCompanyAsync(other.Id, DateTime.UtcNow));
            Assert.False(await Pipe(c1, PayingDocs(other.Id, calls)).ProcessAsync(other.Id, noticeId, DateTime.UtcNow));
            Assert.Empty(await new PartnerPaymentNoticeService(c1, PayingDocs(other.Id, calls), NullLogger<PartnerPaymentNoticeService>.Instance).ListAsync(other.Id));
            Assert.Empty(await PartnerPayablePipeline.BlockedRowsAsync(c1, other.Id, DateTime.UtcNow.AddDays(2)));
            Assert.DoesNotContain(s.BankB, (await PartnerPayablePipeline.UsablePayAccountsAsync(c1, other.Id, default)).Keys);
        }
        Assert.Empty(calls);
        using var check = DbTestDatabase.TryCreateContext()!;
        var n = await NoticeAsync(check, s.Buyer.Id, payId);
        Assert.Equal(PartnerPaymentNoticeRule.StatusOpen, n.Status);
        Assert.Null(n.AutoEvaluatedAt);
    }

    [Fact]
    public async Task migration_แถวเดิม_เติมกุญแจ_SellerPaymentId_จากเลขอ้างอิง_intent_ผู้ขายยกเลิกไปแล้ว_ถอน_รันซ้ำได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine(Skip); return; }
        var s = await SeedAsync(db);
        var (inv, purchaseId) = await RoutedAsync(db, s);
        var intentLive = Guid.NewGuid();
        var intentVoid = Guid.NewGuid();
        var live = new Payment { CompanyId = s.Seller.Id, PaymentNumber = "RCM-" + Random.Shared.Next(100000, 999999), DocumentId = inv.Id, PaymentDate = DateTime.UtcNow.Date,
            Amount = 500m, PaymentMethod = PaymentMethod.BankTransfer, Reference = GatewayPaymentRecord.Reference(intentLive) };
        var voided = new Payment { CompanyId = s.Seller.Id, PaymentNumber = "RCM-" + Random.Shared.Next(100000, 999999), DocumentId = inv.Id, PaymentDate = DateTime.UtcNow.Date,
            Amount = 570m, PaymentMethod = PaymentMethod.BankTransfer, Reference = GatewayPaymentRecord.Reference(intentVoid), IsDeleted = true };
        db.Payments.AddRange(live, voided);
        var link = await db.CrossTenantDocumentLinks.AsNoTracking().FirstAsync(l => l.SourceDocumentId == inv.Id);
        PartnerPaymentNotice Legacy(Guid intent, decimal cash) => new()
        {
            CompanyId = s.Buyer.Id, TradingPartnershipId = s.PartnershipId, LinkId = link.Id, SourceCompanyId = s.Seller.Id, SourceDocumentId = inv.Id,
            SourceDocumentNumber = inv.DocumentNumber, TargetDocumentId = purchaseId, PaymentIntentId = intent, CashAmount = cash, PaidAt = DateTime.UtcNow,
            Status = PartnerPaymentNoticeRule.StatusOpen, SourceChannel = nameof(PaymentMethodKind.PromptPay),
        };
        var nLive = Legacy(intentLive, 500m);
        var nVoid = Legacy(intentVoid, 570m);
        db.PartnerPaymentNotices.AddRange(nLive, nVoid);
        await db.SaveChangesAsync();

        for (var i = 0; i < 2; i++)   // idempotent
        {
            await db.Database.ExecuteSqlRawAsync(PartnerPaymentNoticeMigration.BackfillSellerPaymentSql);
            await db.Database.ExecuteSqlRawAsync(PartnerPaymentNoticeMigration.WithdrawVoidedOpenSql);
            await db.Database.ExecuteSqlRawAsync(PartnerPaymentNoticeMigration.FlagVoidedRecordedSql);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        var a = await check.PartnerPaymentNotices.AsNoTracking().SingleAsync(n => n.Id == nLive.Id);
        var b = await check.PartnerPaymentNotices.AsNoTracking().SingleAsync(n => n.Id == nVoid.Id);
        Assert.Equal(live.Id, a.SellerPaymentId);
        Assert.Equal(PartnerPaymentNoticeRule.StatusOpen, a.Status);
        Assert.Equal(voided.Id, b.SellerPaymentId);
        Assert.Equal(PartnerPaymentNoticeRule.StatusWithdrawn, b.Status);
        Assert.NotNull(b.SellerVoidedAt);
        // แถวเดิมที่เติมกุญแจแล้ว: ขั้นหลัง commit ของการรับชำระเดียวกันไม่สร้างแถวที่สอง
        Assert.Equal(0, await SellerPaysAsyncAgain(s, live.Id));
        // เลขอ้างอิงที่จะเขียน = รูปใหม่ · กันซ้ำตรวจทั้งรูปเดิม (เผื่อบันทึกไปแล้วด้วยรูปเดิม)
        Assert.Contains(PartnerPaymentNoticeRule.PaymentReference(intentLive), PartnerPaymentNoticeRule.PaymentReferencesToCheck(a.SellerPaymentId, a.PaymentIntentId));
    }
}
