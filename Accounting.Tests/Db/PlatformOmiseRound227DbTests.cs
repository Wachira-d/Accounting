using Accounting.Models.DTOs.Document;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Accounting.Services.Payments.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 227 (erp-review/2026-11-02/PLATFORM_OMISE_AUDIT.md C-2/C-3) — เงินค่าบริการที่รับผ่าน gateway ลง <b>บัญชีพักผู้ให้บริการรับชำระ (11340)</b>
/// ไม่ใช่ธนาคาร บน PostgreSQL จริง (เดิม Dr ธนาคารเสมอ ⇒ รอบโอนของบริษัทผู้ให้บริการ Cr 11340 ที่ไม่เคยถูก Dr ⇒ บัญชีพักติดลบ + ธนาคารเกินสองเท่า) ·
/// ทิศตรงข้าม: สลิป (ไม่มี intent) ยังลงผังธนาคารที่ตั้ง · ใบเสร็จค่าส่วนเสริมกันซ้ำด้วยเลขอ้างอิง
/// <para>อยู่ collection เดียวกับเทสต์ที่ตั้ง <c>SiteSettings.PlatformCompanyId</c> (แถวเดียวของระบบ) — ไม่รันขนานกัน</para>
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class PlatformOmiseRound227DbTests
{
    private readonly ITestOutputHelper _out;
    public PlatformOmiseRound227DbTests(ITestOutputHelper output) => _out = output;

    private const string CashCode = "11120";

    /// <summary>ตั้งบริษัทผู้ให้บริการ + ผังธนาคารรับโอนชั่วคราว แล้วคืนค่าเดิมเสมอ</summary>
    private static async Task WithPlatformAsync(Accounting.Data.AccountingDbContext db, Guid platformId, Func<Task> body, bool vatRegistered = false)
    {
        var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        var created = s == null;
        if (s == null) { s = new SiteSettings(); db.SiteSettings.Add(s); }
        var (pid, cash, vat, tax) = (s.PlatformCompanyId, s.PlatformCashAccountCode, s.PlatformIsVatRegistered, s.PlatformSellerTaxId);
        s.PlatformCompanyId = platformId; s.PlatformCashAccountCode = CashCode; s.PlatformIsVatRegistered = vatRegistered;
        s.PlatformSellerTaxId = "0105556000009";   // checksum mod-11 ผ่าน (ThaiTaxId.IsValid)
        await db.SaveChangesAsync();
        try { await body(); }
        finally
        {
            var again = await db.SiteSettings.FirstAsync(x => x.Id == s.Id);
            if (created) db.SiteSettings.Remove(again);
            else { again.PlatformCompanyId = pid; again.PlatformCashAccountCode = cash; again.PlatformIsVatRegistered = vat; again.PlatformSellerTaxId = tax; }
            await db.SaveChangesAsync();
        }
    }

    private sealed record Seeded(PlatformBillingPhase2DbTests.Fixture F, Guid ClearingId, Guid CashId);

    private static async Task<Seeded> SeedAsync(Accounting.Data.AccountingDbContext db)
    {
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db);
        var clearing = new ChartOfAccount { CompanyId = f.PlatformId, AccountCode = GatewayAccountResolver.StandardClearingCode, AccountName = "ลูกหนี้ผู้ให้บริการรับชำระเงิน", AccountType = AccountType.Asset, Level = 4 };
        var cash = new ChartOfAccount { CompanyId = f.PlatformId, AccountCode = CashCode, AccountName = "ธนาคาร", AccountType = AccountType.Asset, Level = 4 };
        db.ChartOfAccounts.AddRange(clearing, cash);
        await db.SaveChangesAsync();
        return new Seeded(f, clearing.Id, cash.Id);
    }

    private static PaymentIntent SucceededIntent(Guid platformId, Guid buyerId, PaymentSourceKind kind, Guid sourceId, decimal amount) => new()
    {
        CompanyId = platformId, PayerCompanyId = buyerId, SourceKind = kind, SourceId = sourceId,
        ProviderCode = OmisePaymentProvider.Code, Amount = amount, Status = PaymentIntentStatus.Succeeded, IsTestMode = false,
        ConfirmedAt = DateTime.UtcNow, IdempotencyKey = "t227-" + Guid.NewGuid().ToString("N"),
    };

    /// <summary>ตัวออกเอกสารจริง + DocumentService ปลอมที่จดคำขอไว้ (คืน null ⇒ ตัวออกล้มหลังจด — ไม่โยน)</summary>
    private static PlatformBillingDocumentIssuer Issuer(Accounting.Data.AccountingDbContext db, List<object?> captured)
    {
        var docs = F4Proxy<IDocumentService>.Create((name, args) =>
        {
            if (name is nameof(IDocumentService.CreateDocumentAsync) or nameof(IDocumentService.CreatePaymentAsync)) captured.Add(args[1]);
            return null;
        });
        var resolver = new GatewayAccountResolver(db,
            new IPaymentProvider[] { new OmisePaymentProvider(null!, null!, NullLogger<OmisePaymentProvider>.Instance) },
            NullLogger<GatewayAccountResolver>.Instance);
        return new PlatformBillingDocumentIssuer(db, new F4ServiceMap().Add(docs), NullLogger<PlatformBillingDocumentIssuer>.Instance, resolver);
    }

    [Fact]
    public async Task C2_ใบเสร็จค่าแพ็กเกจ_จ่ายผ่านgateway_ลงบัญชีพัก_สลิปลงธนาคาร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var buyer = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == s.F.BuyerId);
        await WithPlatformAsync(db, s.F.PlatformId, async () =>
        {
            var paidOnline = new SubscriptionPayment { Id = Guid.NewGuid(), SubscriptionId = s.F.Sub.Id, PaymentNumber = "SP-227-A", Amount = 990m,
                PaymentDate = DateTime.UtcNow, RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1 };
            db.PaymentIntents.Add(SucceededIntent(s.F.PlatformId, s.F.BuyerId, PaymentSourceKind.SubscriptionPayment, paidOnline.Id, 990m));
            await db.SaveChangesAsync();

            var captured = new List<object?>();
            Assert.Null(await Issuer(db, captured).IssuePaidReceiptAsync(paidOnline, buyer));   // DocumentService ปลอมคืน null ⇒ ไม่โยน
            Assert.Equal(s.ClearingId, Assert.IsType<CreateDocumentRequest>(captured.Single()).PaymentAccountId);

            // ทิศตรงข้าม: สลิป (ไม่มี intent ที่รับเงิน) ⇒ ผังธนาคารที่ตั้ง (เดิม)
            var bySlip = new SubscriptionPayment { Id = Guid.NewGuid(), SubscriptionId = s.F.Sub.Id, PaymentNumber = "SP-227-B", Amount = 990m,
                PaymentDate = DateTime.UtcNow, RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1 };
            captured.Clear();
            await Issuer(db, captured).IssuePaidReceiptAsync(bySlip, buyer);
            Assert.Equal(s.CashId, Assert.IsType<CreateDocumentRequest>(captured.Single()).PaymentAccountId);

            // intent โหมดทดสอบที่ยังไม่สำเร็จไม่นับ (รอ/ล้ม ⇒ ไม่ใช่เงินที่รับผ่าน gateway)
            var pendingOnly = new SubscriptionPayment { Id = Guid.NewGuid(), SubscriptionId = s.F.Sub.Id, PaymentNumber = "SP-227-C", Amount = 990m,
                PaymentDate = DateTime.UtcNow, RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1 };
            var pend = SucceededIntent(s.F.PlatformId, s.F.BuyerId, PaymentSourceKind.SubscriptionPayment, pendingOnly.Id, 990m);
            pend.Status = PaymentIntentStatus.Pending; pend.ConfirmedAt = null;
            db.PaymentIntents.Add(pend);
            await db.SaveChangesAsync();
            captured.Clear();
            await Issuer(db, captured).IssuePaidReceiptAsync(pendingOnly, buyer);
            Assert.Equal(s.CashId, Assert.IsType<CreateDocumentRequest>(captured.Single()).PaymentAccountId);
        });
    }

    [Fact]
    public async Task C2_ตัดชำระใบแจ้งหนี้_รายการที่จ่ายผ่านgateway_ลงบัญชีพัก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv = await PlatformBillingPhase2DbTests.InvoiceAsync(db, s.F, "USG-227-" + Guid.NewGuid().ToString("N")[..6], 500m, DateTime.UtcNow.AddDays(5));
        await WithPlatformAsync(db, s.F.PlatformId, async () =>
        {
            var spId = Guid.NewGuid();
            db.PaymentIntents.Add(SucceededIntent(s.F.PlatformId, s.F.BuyerId, PaymentSourceKind.SubscriptionPayment, spId, 500m));
            await db.SaveChangesAsync();
            var captured = new List<object?>();
            await Issuer(db, captured).SettleInvoiceAsync(inv.Id, 500m, 0m, DateTime.UtcNow, "SET-227-" + Guid.NewGuid().ToString("N")[..6], "x", spId);
            Assert.Equal(s.ClearingId, Assert.IsType<CreatePaymentRequest>(captured.Single()).OverridePaymentAccountId);
            // ทิศตรงข้าม: ไม่ระบุรายการชำระ (ผู้เรียกเดิม) ⇒ ผังธนาคาร
            captured.Clear();
            await Issuer(db, captured).SettleInvoiceAsync(inv.Id, 500m, 0m, DateTime.UtcNow, "SET-227-" + Guid.NewGuid().ToString("N")[..6], "x");
            Assert.Equal(s.CashId, Assert.IsType<CreatePaymentRequest>(captured.Single()).OverridePaymentAccountId);
        });
    }

    [Fact]
    public async Task C3_ใบเสร็จค่าส่วนเสริม_gatewayลงบัญชีพัก_และกันซ้ำด้วยเลขอ้างอิง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        await WithPlatformAsync(db, s.F.PlatformId, async () =>
        {
            var intent = SucceededIntent(s.F.PlatformId, s.F.BuyerId, PaymentSourceKind.AddOnPurchase, Guid.NewGuid(), 300m);
            db.PaymentIntents.Add(intent);
            await db.SaveChangesAsync();
            var reference = AddOnPurchaseService.ReceiptReference(intent.SourceId, intent.Id, null);

            var captured = new List<object?>();
            await Issuer(db, captured).IssueAddOnReceiptAsync(s.F.BuyerId, "ค่าส่วนเสริม ทดสอบ", 300m, DateTime.UtcNow, reference, intent.Id);
            var req = Assert.IsType<CreateDocumentRequest>(captured.Single());
            Assert.Equal(s.ClearingId, req.PaymentAccountId);
            Assert.Equal(DocumentType.Receipt, req.DocumentType);   // ไม่จด VAT ⇒ ใบเสร็จธรรมดา
            Assert.Equal(300m, req.Lines.Single().UnitPrice);
            Assert.Equal(reference, req.Reference);

            // มีใบที่อ้างอิงนี้แล้ว (ส่งต่อซ้ำ/แอดมินกดซ้ำ) ⇒ คืนใบเดิม ไม่สร้างใบที่สอง
            var existing = new Document
            {
                CompanyId = s.F.PlatformId, ContactId = s.F.ContactId, DocumentType = DocumentType.Receipt, DocumentNumber = "RC-227-" + Guid.NewGuid().ToString("N")[..6],
                Status = DocumentStatus.Approved, Reference = reference, DocumentDate = DateTime.UtcNow, SubTotal = 300m, TotalAmount = 300m, Currency = "THB",
            };
            db.Documents.Add(existing);
            await db.SaveChangesAsync();
            captured.Clear();
            var again = await Issuer(db, captured).IssueAddOnReceiptAsync(s.F.BuyerId, "ค่าส่วนเสริม ทดสอบ", 300m, DateTime.UtcNow, reference, intent.Id);
            Assert.Empty(captured);
            Assert.Equal(existing.Id, again!.DocumentId);
            Assert.False(again.IsTaxInvoice);

            // สลิปที่อนุมัติ (ไม่มี intent) ⇒ ผังธนาคาร
            captured.Clear();
            await Issuer(db, captured).IssueAddOnReceiptAsync(s.F.BuyerId, "ค่าส่วนเสริม ทดสอบ", 300m, DateTime.UtcNow,
                AddOnPurchaseService.ReceiptReference(Guid.NewGuid(), null, DateTime.UtcNow), null);
            Assert.Equal(s.CashId, Assert.IsType<CreateDocumentRequest>(captured.Single()).PaymentAccountId);
        });
    }

    [Fact]
    public async Task S1_intentคืนเต็ม_หรือโหมดทดสอบ_ไม่ใช่หลักฐานเงินผ่านgateway_ลงธนาคาร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var buyer = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == s.F.BuyerId);
        await WithPlatformAsync(db, s.F.PlatformId, async () =>
        {
            foreach (var mutate in new Action<PaymentIntent>[] { i => i.Status = PaymentIntentStatus.Refunded, i => i.IsTestMode = true })
            {
                var pay = new SubscriptionPayment { Id = Guid.NewGuid(), SubscriptionId = s.F.Sub.Id, PaymentNumber = "SP-227-N" + Guid.NewGuid().ToString("N")[..4],
                    Amount = 990m, PaymentDate = DateTime.UtcNow, RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1 };
                var intent = SucceededIntent(s.F.PlatformId, s.F.BuyerId, PaymentSourceKind.SubscriptionPayment, pay.Id, 990m);
                mutate(intent);
                db.PaymentIntents.Add(intent);
                await db.SaveChangesAsync();
                var captured = new List<object?>();
                await Issuer(db, captured).IssuePaidReceiptAsync(pay, buyer);
                Assert.Equal(s.CashId, Assert.IsType<CreateDocumentRequest>(captured.Single()).PaymentAccountId);
            }
        });
    }

    [Fact]
    public async Task S2_S6_จดVAT_ใบเท่าเงินที่รับ_และร่างค้างไม่นับเป็นใบเดิม()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        await WithPlatformAsync(db, s.F.PlatformId, async () =>
        {
            var reference = AddOnPurchaseService.ReceiptReference(Guid.NewGuid(), Guid.NewGuid(), null);
            // ร่างที่อนุมัติล้มค้างอยู่ด้วยเลขอ้างอิงเดียวกัน ⇒ ไม่ใช่ "ใบเดิม" (S6)
            db.Documents.Add(new Document
            {
                CompanyId = s.F.PlatformId, ContactId = s.F.ContactId, DocumentType = DocumentType.TaxInvoice, DocumentNumber = "DRAFT-" + Guid.NewGuid().ToString("N"),
                Status = DocumentStatus.Draft, Reference = reference, DocumentDate = DateTime.UtcNow, SubTotal = 1.64m, TotalAmount = 1.76m, Currency = "THB",
            });
            await db.SaveChangesAsync();
            var captured = new List<object?>();
            await Issuer(db, captured).IssueAddOnReceiptAsync(s.F.BuyerId, "ค่าส่วนเสริม", 1.76m, DateTime.UtcNow, reference, null);
            var req = Assert.IsType<CreateDocumentRequest>(captured.Single());
            Assert.Equal(DocumentType.TaxInvoice, req.DocumentType);
            var line = req.Lines.Single();
            Assert.Equal(1.64m, line.UnitPrice);
            Assert.Equal(0.12m, line.VatAmountOverride);   // ไม่ใช่ 1.64 × 7% = 0.11 (ใบ 1.75 ≠ เงิน 1.76)
            Assert.Equal(1.76m, line.UnitPrice + line.VatAmountOverride);
        }, vatRegistered: true);
    }
}
