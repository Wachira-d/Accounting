using Accounting.Helpers;
using Accounting.Models.DTOs.Document;
using Accounting.Models.DTOs.Subscription;
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
/// รอบ 234 (คำตัดสิน 234-1…234-6) — เส้นซื้อแพ็กเกจที่ผู้เช่านิติบุคคลติ๊ก "หัก ณ ที่จ่าย" บน PostgreSQL จริง:
/// รายการชำระเก็บ Amount = ยอดสุทธิ (ตัดบัตร/QR/โอน) + WithholdingTaxAmount จากตัวตัดสินเดียว · เปลี่ยนใจบนรายการเดิม · ข้อมูลบริษัทไม่ครบ = ปฏิเสธ ·
/// เงินเข้าออนไลน์ยอดสุทธิ ⇒ อนุมัติอัตโนมัติ + เรื่องค้าง "รอ 50 ทวิ" · ใบเสร็จ/ใบกำกับออกเต็มยอด (ฐาน + VAT) พร้อมอัตราหักบนบรรทัด
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class PlatformWhtRound234DbTests
{
    private readonly ITestOutputHelper _out;
    public PlatformWhtRound234DbTests(ITestOutputHelper output) => _out = output;

    private const string JuristicTaxId = "0105556000114";   // checksum ผ่าน · นิติบุคคล
    private const string Address = "99 ถนนทดสอบ แขวงทดสอบ เขตทดสอบ กรุงเทพฯ 10110";

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    /// <summary>ตั้งบริษัทผู้ให้บริการ (จด VAT · เลขผู้เสียภาษีถูกต้อง ⇒ ราคารวม VAT) ชั่วคราว แล้วคืนค่าเดิมเสมอ</summary>
    private static async Task WithVatPlatformAsync(Accounting.Data.AccountingDbContext db, Guid platformId, Func<Task> body)
    {
        var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        var created = s == null;
        if (s == null) { s = new SiteSettings(); db.SiteSettings.Add(s); }
        var (pid, vat, tax) = (s.PlatformCompanyId, s.PlatformIsVatRegistered, s.PlatformSellerTaxId);
        s.PlatformCompanyId = platformId; s.PlatformIsVatRegistered = true; s.PlatformSellerTaxId = "0105556000009";
        await db.SaveChangesAsync();
        try { await body(); }
        finally
        {
            var again = await db.SiteSettings.FirstAsync(x => x.Id == s.Id);
            if (created) db.SiteSettings.Remove(again);
            else { again.PlatformCompanyId = pid; again.PlatformIsVatRegistered = vat; again.PlatformSellerTaxId = tax; }
            await db.SaveChangesAsync();
        }
    }

    private static async Task EnsurePricedTemplateAsync(Accounting.Data.AccountingDbContext db)
    {
        if (await db.PlanTemplates.AnyAsync(t => t.Plan == SubscriptionPlan.Pro && t.IsActive && t.AnnualPrice >= 2000m)) return;
        db.PlanTemplates.Add(new PlanTemplate
        {
            Name = "แพ็กเกจทดสอบรอบ 234", Plan = SubscriptionPlan.Pro, IsActive = true,
            MonthlyPrice = 1_490m, QuarterlyPrice = 4_200m, SemiAnnualPrice = 8_000m, AnnualPrice = 15_198m,
            MaxUsers = 5, MaxCompanies = 1, MaxDocumentsPerMonth = 500, MaxJournalEntriesPerMonth = 500,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<PlatformBillingPhase2DbTests.Fixture> SeedTrialBuyerAsync(Accounting.Data.AccountingDbContext db, Guid? platformId,
        string taxId, string? address)
    {
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db, platformId, plan: SubscriptionPlan.FreeTrial, status: SubscriptionStatus.Trial);
        var buyer = await db.Companies.FirstAsync(c => c.Id == f.BuyerId);
        buyer.TaxId = taxId; buyer.Address = address; buyer.BranchCode = "00000";
        await db.SaveChangesAsync();
        return f;
    }

    [Fact]
    public async Task Juristic_buyer_ticks_withholding_payment_is_net_plus_wht_and_receipt_is_full_amount()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsurePricedTemplateAsync(db);
        var f = await SeedTrialBuyerAsync(db, null, JuristicTaxId, Address);
        var noTick = await SeedTrialBuyerAsync(db, f.PlatformId, JuristicTaxId, Address);
        var noAddress = await SeedTrialBuyerAsync(db, f.PlatformId, JuristicTaxId, null);
        await WithVatPlatformAsync(db, f.PlatformId, async () =>
        {
            var svc = new SubscriptionService(db, Notifications());
            var res = await svc.ConvertTrialAsync(f.BuyerId, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Annual, WithholdTax: true), "owner");
            var pay = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == res.Payment!.Id);
            var gross = PlatformWhtSuggestion.GrossOf(pay.Amount, pay.WithholdingTaxAmount);
            Assert.True(gross >= 1_070m, "แม่แบบ Pro รายปีต้องเกินเกณฑ์ 1,000 ก่อน VAT");
            var expectedWht = PlatformWhtSuggestion.MaxWithholding(PlatformWhtSuggestion.BaseOfReceived(gross, vatIncluded: true));
            Assert.Equal(expectedWht, pay.WithholdingTaxAmount);   // ม.40(2) 3% ของฐานก่อน VAT (ถอด 7/107)
            Assert.Equal(gross - expectedWht, pay.Amount);         // ยอดตัดบัตร/QR/โอน = สุทธิ
            // ฝ่ายค้าน 234 S2: ติ๊กครั้งเดียว ≠ ค่าตั้ง "หักทุกครั้ง" (ตั้งได้ที่ PUT withholding-preference เท่านั้น)
            Assert.Null((await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).WithholdTaxPreference);

            // ทิศตรงข้าม: ไม่ติ๊ก (null — ผู้เรียกเดิม) ⇒ ยอดเต็ม ไม่หัก · ค่าตั้งไม่ถูกแตะ
            var plain = await svc.ConvertTrialAsync(noTick.BuyerId, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Annual), "owner");
            var plainRow = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == plain.Payment!.Id);
            Assert.Equal(0m, plainRow.WithholdingTaxAmount);
            Assert.Equal(gross, plainRow.Amount);
            Assert.Null((await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == noTick.Sub.Id)).WithholdTaxPreference);

            // เปลี่ยนใจ (ยังไม่จ่าย) ⇒ รายการเดิม คิดยอดใหม่ · ไม่เกิดรายการซ้อน
            var off = await svc.ConvertTrialAsync(f.BuyerId, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Annual, WithholdTax: false), "owner");
            Assert.Equal(pay.Id, off.Payment!.Id);
            var offRow = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id);
            Assert.Equal(0m, offRow.WithholdingTaxAmount); Assert.Equal(gross, offRow.Amount);
            var on = await svc.ConvertTrialAsync(f.BuyerId, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Annual, WithholdTax: true), "owner");
            Assert.Equal(pay.Id, on.Payment!.Id);
            Assert.Equal(1, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == f.Sub.Id));

            // ข้อมูลบริษัทไม่ครบ (ไม่มีที่อยู่) + ขอหัก ⇒ 400 พร้อมสิ่งที่ขาด · ไม่เกิดรายการ (ทางไปต่อ: กรอกการ์ด หรือไม่ติ๊ก)
            var refused = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                svc.ConvertTrialAsync(noAddress.BuyerId, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Annual, WithholdTax: true), "owner"));
            Assert.Equal(PlatformPayerProfileGate.RuleCode, refused.RuleCode);
            Assert.Contains("ที่อยู่", refused.Message);
            Assert.False(await db.SubscriptionPayments.AnyAsync(p => p.SubscriptionId == noAddress.Sub.Id));
            var unticked = await svc.ConvertTrialAsync(noAddress.BuyerId, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Annual, WithholdTax: false), "owner");
            Assert.Equal(gross, unticked.Payment!.Amount);

            // เงินเข้าออนไลน์ "ยอดสุทธิ" ⇒ handler เทียบราคากับยอดเต็ม (สุทธิ + ภาษีหัก) แล้วอนุมัติ + เรื่องค้าง "รอ 50 ทวิ"
            var intent = new PaymentIntent
            {
                CompanyId = f.PlatformId, PayerCompanyId = f.BuyerId, SourceKind = PaymentSourceKind.SubscriptionPayment, SourceId = pay.Id,
                ProviderCode = "test", Amount = gross - expectedWht, Status = PaymentIntentStatus.Succeeded, ConfirmedAt = DateTime.UtcNow,
                IdempotencyKey = "t234-" + Guid.NewGuid().ToString("N"),
            };
            var handler = new Accounting.Services.Payments.Handlers.SubscriptionPaymentHandler(db, svc,
                NullLogger<Accounting.Services.Payments.Handlers.SubscriptionPaymentHandler>.Instance);
            await handler.HandleSucceededAsync(intent);
            var approved = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id);
            Assert.Equal(SubscriptionPaymentStatus.Approved, approved.Status);
            Assert.Equal(PlatformPaymentAttention.RuleWhtCertPending, approved.AttentionRule);

            // ใบเสร็จ/ใบกำกับ: บรรทัดเต็มยอด (ฐาน + VAT = ยอดเต็ม) + อัตราหัก ≈ 3% (ตัวออกใบจดคำขอ — DocumentService ปลอม)
            // ผังผู้ให้บริการต้องมีกลุ่ม 119 ให้ตัวออกใบสร้าง 11910 (ฝ่ายค้าน 234 S4: ไม่มี ⇒ ล้มดังก่อนสร้างใบ — เทสต์ S4_non_standard_chart… ล็อกทิศนั้น)
            db.ChartOfAccounts.Add(new ChartOfAccount
            {
                CompanyId = f.PlatformId, AccountCode = "119", AccountName = "สินทรัพย์หมุนเวียนอื่น", AccountType = AccountType.Asset, Level = 3, IsActive = true,
            });
            await db.SaveChangesAsync();
            var captured = new List<object?>();
            var docs = F4Proxy<IDocumentService>.Create((name, args) =>
            {
                if (name == nameof(IDocumentService.CreateDocumentAsync)) captured.Add(args[1]);
                return null;
            });
            var issuer = new PlatformBillingDocumentIssuer(db, new F4ServiceMap().Add(docs), NullLogger<PlatformBillingDocumentIssuer>.Instance);
            var buyer = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == f.BuyerId);
            await issuer.IssuePaidReceiptAsync(approved, buyer);
            Assert.True(captured.Count == 1, issuer.LastFailureReason);   // ล้มก่อนสร้างใบ ⇒ เห็นเหตุจริง ไม่ใช่ "Sequence contains no elements"
            var req = Assert.IsType<CreateDocumentRequest>(captured.Single());
            var line = req.Lines.Single();
            Assert.Equal(DocumentType.TaxInvoice, req.DocumentType);
            Assert.Equal(gross, line.UnitPrice + (line.VatAmountOverride ?? 0m));   // ใบออกเต็มยอด ไม่ใช่ยอดสุทธิ
            Assert.InRange(line.WithholdingTaxRate, 2.99m, 3.01m);
        });
    }

    // ═══════════ 234-W*: 50 ทวิ จากลูกค้า → ทะเบียนภาษีถูกหักของบริษัทผู้ให้บริการ (ทะเบียนเดียว) ═══════════

    private static async Task<(SubscriptionPayment Pay, WhtCreditReceived Credit)> ApprovedWithGlCreditAsync(Accounting.Data.AccountingDbContext db,
        PlatformBillingPhase2DbTests.Fixture f, decimal wht)
    {
        // จำลองผลของ DocumentService.SyncWhtCreditReceivedAsync: ใบเสร็จ (เอกสารในบริษัทผู้ให้บริการ) ที่มีขา Dr 11910 ⇒ แถว Pending ผูก DocumentId
        // ใบเสร็จต้องมีจริงในตาราง Documents (FK_WhtCreditsReceived_Documents_DocumentId) — ใบอนุมัติแล้วของบริษัทผู้ให้บริการ ผู้ติดต่อ = ผู้เช่า
        var receipt = new Document
        {
            CompanyId = f.PlatformId, ContactId = f.ContactId, DocumentType = DocumentType.Receipt,
            DocumentNumber = "RC-T234-" + Guid.NewGuid().ToString("N")[..8], Status = DocumentStatus.Approved, Reference = "SP-234",
            DocumentDate = DateTime.UtcNow.AddDays(-40), SubTotal = 14_203.74m, VatAmount = 994.26m, TotalAmount = 15_198m, BalanceDue = 0m, Currency = "THB",
        };
        receipt.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าบริการระบบบัญชีออนไลน์ (ทดสอบ 234)", Quantity = 1, UnitPrice = 14_203.74m, Amount = 14_203.74m });
        db.Documents.Add(receipt);
        await db.SaveChangesAsync();
        var receiptId = receipt.Id;
        var pay = new SubscriptionPayment
        {
            SubscriptionId = f.Sub.Id, PaymentNumber = "SP-234-" + Guid.NewGuid().ToString("N")[..6], Amount = 14_771.89m, WithholdingTaxAmount = wht,
            PaymentDate = DateTime.UtcNow, RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Annual, RequestedPeriodMonths = 12,
            Status = SubscriptionPaymentStatus.Approved, ReviewedAt = DateTime.UtcNow.AddDays(-40), PlatformDocumentId = receiptId, ReceiptNumber = "RC-234",
        };
        db.SubscriptionPayments.Add(pay);
        var credit = new WhtCreditReceived
        {
            CompanyId = f.PlatformId, TaxYear = DateTime.UtcNow.Year, PayerName = "ร้านลูกค้า", IncomeAmount = 14_203.74m, WhtRate = 3m,
            WhtAmount = wht <= 0m ? 1m : wht, Status = WhtCreditStatus.Pending, DocumentId = receiptId, Notes = "สร้างอัตโนมัติจาก RC-234",
        };
        db.WhtCreditsReceived.Add(credit);
        await db.SaveChangesAsync();
        return (pay, credit);
    }

    private static Accounting.Services.Platform.PlatformWhtCertificateService WhtCerts(Accounting.Data.AccountingDbContext db)
        => new(db, new WhtCreditService(db, NullLogger<WhtCreditService>.Instance), NullLogger<Accounting.Services.Platform.PlatformWhtCertificateService>.Instance);

    [Fact]
    public async Task Wht_certificate_upload_links_register_confirm_credits_reject_does_not()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db);
        var other = await PlatformBillingPhase2DbTests.SeedAsync(db, f.PlatformId);
        await PlatformBillingPhase2DbTests.WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var (pay, credit) = await ApprovedWithGlCreditAsync(db, f, 426.11m);
            var subs = new SubscriptionService(db, Notifications());
            var svc = WhtCerts(db);

            // ยังไม่ส่ง ⇒ รอส่ง · เกินกำหนด (อนุมัติ 40 วัน > 30) · ยืนยัน/ตีกลับไม่ได้ (ไม่มีไฟล์)
            var row0 = (await svc.ListAsync(30, true)).Single(r => r.PaymentId == pay.Id);
            Assert.Equal(nameof(PlatformWhtCertificateStatus.State.AwaitingUpload), row0.State);
            Assert.True(row0.Overdue);
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ConfirmAsync(pay.Id, "50T-001", null, "admin"));

            // ผู้เช่าส่งไฟล์ ⇒ ผูกแถวทะเบียนเดิม (ไม่สร้างแถวที่สอง) · ยัง Pending = รอตรวจ (เครดิตไม่ได้)
            await subs.AttachWhtCertificateAsync(f.BuyerId, pay.Id, "wht-a.pdf", "owner");
            await subs.AttachWhtCertificateAsync(f.BuyerId, pay.Id, "wht-b.pdf", "owner");   // ส่งซ้ำ ⇒ ไม่ซ้อน
            var linked = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id);
            Assert.Equal(credit.Id, linked.WhtCreditId);
            Assert.Equal(1, await db.WhtCreditsReceived.CountAsync(w => w.CompanyId == f.PlatformId && w.DocumentId == credit.DocumentId));
            Assert.Equal(WhtCreditStatus.Pending, (await db.WhtCreditsReceived.AsNoTracking().SingleAsync(w => w.Id == credit.Id)).Status);
            Assert.Equal(nameof(PlatformWhtCertificateStatus.State.AwaitingReview), (await svc.ListAsync(30, true)).Single(r => r.PaymentId == pay.Id).State);

            // ตีกลับ ⇒ ไม่เครดิต · ไฟล์ถูกปลด · เหตุผลอยู่บนรายการ (ผู้เช่าเห็น)
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RejectAsync(pay.Id, " ", "admin"));   // ไม่มีเหตุผล = ไม่ได้
            await svc.RejectAsync(pay.Id, "ชื่อผู้ถูกหักไม่ตรง", "admin");
            var rejected = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id);
            Assert.Null(rejected.WhtCertificateFileName);
            Assert.Equal("ชื่อผู้ถูกหักไม่ตรง", rejected.WhtCertificateRejectedReason);
            Assert.Equal(WhtCreditStatus.Pending, (await db.WhtCreditsReceived.AsNoTracking().SingleAsync(w => w.Id == credit.Id)).Status);

            // ส่งใหม่ ⇒ ล้างเหตุตีกลับ · ยืนยัน (เลขที่บนใบ) ⇒ Received = เครดิตได้ · ล้างเรื่องค้าง
            await subs.AttachWhtCertificateAsync(f.BuyerId, pay.Id, "wht-c.pdf", "owner");
            Assert.Null((await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).WhtCertificateRejectedReason);
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ConfirmAsync(pay.Id, "", null, "admin"));   // ไม่มีเลขที่ = ไม่ได้
            var confirmed = await svc.ConfirmAsync(pay.Id, "50T-001", new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), "admin");
            Assert.Equal(nameof(PlatformWhtCertificateStatus.State.Confirmed), confirmed!.State);
            var reg = await db.WhtCreditsReceived.AsNoTracking().SingleAsync(w => w.Id == credit.Id);
            Assert.Equal(WhtCreditStatus.Received, reg.Status);
            Assert.Equal("50T-001", reg.CertificateNumber);
            Assert.Equal(PlatformWhtSuggestion.IncomeTypeCode, reg.IncomeTypeCode);
            // ยืนยันแล้วปิดรับไฟล์ (ไม่ทับหลักฐานที่ตรวจแล้ว)
            var closed = await Assert.ThrowsAsync<BusinessRuleException>(() => subs.AttachWhtCertificateAsync(f.BuyerId, pay.Id, "wht-d.pdf", "owner"));
            Assert.Equal(PlatformWhtCertificateStatus.RuleCode, closed.RuleCode);

            // ทิศตรงข้าม: รายการไม่มีภาษีหัก ⇒ ไม่ผูก/ไม่อยู่ในหน้าติดตาม
            var (plain, _) = await ApprovedWithGlCreditAsync(db, other, 0m);
            Assert.Null(await Accounting.Services.Platform.PlatformWhtCertificateService.LinkCoreAsync(db, plain));
            Assert.DoesNotContain(await svc.ListAsync(30, true), r => r.PaymentId == plain.Id);

            // backfill: รายการเก่าที่อนุมัติแล้ว (ยังไม่ผูก) ⇒ ผูก · ไม่ประทับ Received เอง · รันซ้ำได้
            var (old, oldCredit) = await ApprovedWithGlCreditAsync(db, other, 30m);
            var bf = await svc.BackfillAsync("admin");
            Assert.NotNull(bf);
            Assert.Equal(oldCredit.Id, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == old.Id)).WhtCreditId);
            Assert.Equal(WhtCreditStatus.Pending, (await db.WhtCreditsReceived.AsNoTracking().SingleAsync(w => w.Id == oldCredit.Id)).Status);
            Assert.NotNull(await svc.BackfillAsync("admin"));
        });
    }

    // ═══════════ ฝ่ายค้าน 234 S4: DocumentService จริง — ใบเสร็จของรายการที่หัก ⇒ JE + ทะเบียนภาษีถูกหัก · ผังไม่มาตรฐาน ⇒ ล้มดัง ═══════════

    private static async Task WithIssuingPlatformAsync(Accounting.Data.AccountingDbContext db, Guid platformId, Func<Task> body)
    {
        var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        var created = s == null;
        if (s == null) { s = new SiteSettings(); db.SiteSettings.Add(s); }
        var (pid, vat, tax, rev, cash) = (s.PlatformCompanyId, s.PlatformIsVatRegistered, s.PlatformSellerTaxId, s.PlatformRevenueAccountCode, s.PlatformCashAccountCode);
        s.PlatformCompanyId = platformId; s.PlatformIsVatRegistered = true; s.PlatformSellerTaxId = "0105556000009";
        s.PlatformRevenueAccountCode = "41000"; s.PlatformCashAccountCode = "11120";
        await db.SaveChangesAsync();
        try { await body(); }
        finally
        {
            var again = await db.SiteSettings.FirstAsync(x => x.Id == s.Id);
            if (created) db.SiteSettings.Remove(again);
            else
            {
                again.PlatformCompanyId = pid; again.PlatformIsVatRegistered = vat; again.PlatformSellerTaxId = tax;
                again.PlatformRevenueAccountCode = rev; again.PlatformCashAccountCode = cash;
            }
            await db.SaveChangesAsync();
        }
    }

    private static async Task<(PlatformBillingPhase2DbTests.Fixture F, SubscriptionPayment Pay)> SeedIssuingAsync(Accounting.Data.AccountingDbContext db, bool withGroup119)
    {
        var f = await PlatformBillingPhase2DbTests.SeedAsync(db);
        var platformCo = await db.Companies.SingleAsync(c => c.Id == f.PlatformId);
        platformCo.IsVatRegistered = true;
        var buyer = await db.Companies.SingleAsync(c => c.Id == f.BuyerId);
        buyer.TaxId = JuristicTaxId; buyer.Address = Address; buyer.BranchCode = "00000";
        var rows = new List<(string, string, AccountType, int)>
        {
            ("11120", "เงินฝากธนาคาร", AccountType.Asset, 4), ("41000", "รายได้ค่าบริการ", AccountType.Revenue, 4), ("21911", "ภาษีขาย", AccountType.Liability, 4),
        };
        if (withGroup119) rows.Add(("119", "สินทรัพย์หมุนเวียนอื่น", AccountType.Asset, 3));
        foreach (var (code, name, type, level) in rows)
            db.ChartOfAccounts.Add(new ChartOfAccount { CompanyId = f.PlatformId, AccountCode = code, AccountName = name, AccountType = type, Level = level, IsActive = true });
        var pay = new SubscriptionPayment
        {
            SubscriptionId = f.Sub.Id, PaymentNumber = "SP-234S4-" + Guid.NewGuid().ToString("N")[..6], Amount = 14_771.89m, WithholdingTaxAmount = 426.11m,
            PaymentDate = DateTime.UtcNow, RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Annual, RequestedPeriodMonths = 12,
            Status = SubscriptionPaymentStatus.Approved, ReviewedAt = DateTime.UtcNow,
        };
        db.SubscriptionPayments.Add(pay);
        await db.SaveChangesAsync();
        return (f, pay);
    }

    private static PlatformBillingDocumentIssuer RealIssuer(Accounting.Data.AccountingDbContext db)
    {
        var docs = new DocumentService(db, new AccountingService(db), AllowAll<ISubscriptionService>.Create(), null!, null!,
            NullLogger<DocumentService>.Instance, null!,
            new Accounting.Services.Implementations.Ocr.VendorIntelligenceService(db, NullLogger<Accounting.Services.Implementations.Ocr.VendorIntelligenceService>.Instance),
            null!);
        return new PlatformBillingDocumentIssuer(db, new F4ServiceMap().Add<IDocumentService>(docs), NullLogger<PlatformBillingDocumentIssuer>.Instance);
    }

    [Fact]
    public async Task S4_real_receipt_posts_net_cash_plus_11910_full_revenue_and_register_row()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (f, pay) = await SeedIssuingAsync(db, withGroup119: true);
        await WithIssuingPlatformAsync(db, f.PlatformId, async () =>
        {
            var issuer = RealIssuer(db);
            var buyer = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == f.BuyerId);
            // ผู้ติดต่อของผู้เช่าเกิดตอนบิลก่อนกรอกที่อยู่ (seed = ไม่มีที่อยู่ — สภาพจริงของ production) · บริษัทผู้เช่ามีที่อยู่แล้ว (ด่านข้อมูลผู้ซื้อผ่าน)
            Assert.Null((await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == f.ContactId)).Address);
            var res = await issuer.IssuePaidReceiptAsync(pay, buyer);
            Assert.True(res != null, issuer.LastFailureReason);
            // ตัวออกใบเติมที่อยู่จากบริษัทผู้ซื้อให้ผู้ติดต่อ (เดิม: อนุมัติล้ม §86/4 "ขาดที่อยู่ผู้ซื้อ" ⇒ ตกโหมด PDF ไม่ลงบัญชี)
            Assert.Equal(Address, (await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == f.ContactId)).Address);
            using var check = DbTestDatabase.TryCreateContext()!;
            var gl = await (from l in check.JournalEntryLines.AsNoTracking()
                            join e in check.JournalEntries.AsNoTracking() on l.JournalEntryId equals e.Id
                            join a in check.ChartOfAccounts.AsNoTracking() on l.AccountId equals a.Id
                            where e.CompanyId == f.PlatformId && e.Status == JournalEntryStatus.Posted
                            group l by a.AccountCode into g
                            select new { Code = g.Key, Net = g.Sum(x => x.DebitAmount - x.CreditAmount) })
                .ToDictionaryAsync(x => x.Code, x => x.Net);
            Assert.Equal(14_771.89m, gl.GetValueOrDefault("11120"));      // Dr เงินที่รับ (สุทธิ)
            Assert.Equal(426.11m, gl.GetValueOrDefault("11910"));         // Dr ภาษีถูกหัก ณ ที่จ่าย (ผังสร้างให้ใต้กลุ่ม 119)
            Assert.Equal(-14_203.74m, gl.GetValueOrDefault("41000"));     // Cr รายได้เต็มฐาน
            Assert.Equal(-994.26m, gl.GetValueOrDefault("21911"));        // Cr VAT เต็ม
            var reg = await check.WhtCreditsReceived.AsNoTracking().SingleAsync(w => w.CompanyId == f.PlatformId && w.DocumentId == res!.DocumentId);
            Assert.Equal(WhtCreditStatus.Pending, reg.Status);            // ทะเบียนจาก GL — รอ 50 ทวิ
            Assert.Equal(426.11m, reg.WhtAmount);
        });
    }

    [Fact]
    public async Task S4_contact_address_kept_when_present_and_never_invented_when_buyer_has_none()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (f, pay) = await SeedIssuingAsync(db, withGroup119: true);
        // ทิศตรงข้าม (1): ผู้ติดต่อมีที่อยู่อยู่แล้ว ⇒ ไม่ทับ (เอกสารไม่มีสำเนาที่อยู่ของตัวเอง — ทับแล้วใบเก่าพิมพ์ซ้ำได้ที่อยู่ใหม่)
        const string contactAddress = "1 ที่อยู่เดิมบนผู้ติดต่อ กรุงเทพฯ 10200";
        var contact = await db.Contacts.SingleAsync(c => c.Id == f.ContactId);
        contact.Address = contactAddress;
        // ทิศตรงข้าม (2): ผู้เช่าอีกรายในบริษัทผู้ให้บริการเดียวกัน ไม่มีที่อยู่บริษัทเลย · ไม่หัก (ด่านไม่บล็อกการจ่าย) ⇒ ไม่แต่งที่อยู่ · ใบกำกับเต็มรูปไม่ออก (เหตุชัด ไม่โยนขึ้นผู้เรียก)
        var g = await PlatformBillingPhase2DbTests.SeedAsync(db, f.PlatformId);
        var pay2 = new SubscriptionPayment
        {
            SubscriptionId = g.Sub.Id, PaymentNumber = "SP-234S4N-" + Guid.NewGuid().ToString("N")[..6], Amount = 1_070m, WithholdingTaxAmount = 0m,
            PaymentDate = DateTime.UtcNow, RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1,
            Status = SubscriptionPaymentStatus.Approved, ReviewedAt = DateTime.UtcNow,
        };
        db.SubscriptionPayments.Add(pay2);
        await db.SaveChangesAsync();
        await WithIssuingPlatformAsync(db, f.PlatformId, async () =>
        {
            var issuer = RealIssuer(db);
            var buyer = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == f.BuyerId);
            var res = await issuer.IssuePaidReceiptAsync(pay, buyer);
            Assert.True(res != null, issuer.LastFailureReason);
            Assert.Equal(contactAddress, (await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == f.ContactId)).Address);

            var noAddressBuyer = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == g.BuyerId);
            Assert.True(string.IsNullOrWhiteSpace(noAddressBuyer.Address));
            Assert.Null(await issuer.IssuePaidReceiptAsync(pay2, noAddressBuyer));
            Assert.Contains("ที่อยู่ผู้ซื้อ", issuer.LastFailureReason);
            Assert.True(string.IsNullOrWhiteSpace((await db.Contacts.AsNoTracking().SingleAsync(c => c.Id == g.ContactId)).Address));
            Assert.False(await db.Documents.AnyAsync(d => d.CompanyId == f.PlatformId && d.Reference == pay2.PaymentNumber && d.Status != DocumentStatus.Draft));
        });
    }

    [Fact]
    public async Task S4_non_standard_chart_without_119_fails_loudly_no_silent_receipt()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (f, pay) = await SeedIssuingAsync(db, withGroup119: false);
        await WithIssuingPlatformAsync(db, f.PlatformId, async () =>
        {
            var issuer = RealIssuer(db);
            var buyer = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == f.BuyerId);
            Assert.Null(await issuer.IssuePaidReceiptAsync(pay, buyer));
            Assert.Contains("11910", issuer.LastFailureReason);
            Assert.False(await db.Documents.AnyAsync(d => d.CompanyId == f.PlatformId && d.Reference == pay.PaymentNumber && d.Status != DocumentStatus.Draft));
        });
    }

    // ═══════════ ฝ่ายค้าน 234 B1/S1: แอดมินแก้ภาษีหัก (ยอดเต็มคงที่) · ตรวจสิทธิ์หักซ้ำตอนเงินเข้า ═══════════

    [Fact]
    public async Task B1_admin_zero_wht_full_transfer_keeps_gross_and_S1_payer_lost_eligibility_holds()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsurePricedTemplateAsync(db);
        var f = await SeedTrialBuyerAsync(db, null, JuristicTaxId, Address);
        var g = await SeedTrialBuyerAsync(db, f.PlatformId, JuristicTaxId, Address);
        await WithVatPlatformAsync(db, f.PlatformId, async () =>
        {
            var svc = new SubscriptionService(db, Notifications());
            // ผู้ตรวจต้องเป็น User จริง (FK ReviewedByUserId)
            var reviewer = new User { Email = $"r234-admin-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "แอดมินทดสอบ 234", IsSystemAdmin = true };
            db.Users.Add(reviewer);
            await db.SaveChangesAsync();
            var admin = reviewer.Id.ToString();
            // B1: ผู้เช่าติ๊กหักแต่โอนเต็ม ⇒ แอดมินกรอกภาษีหัก 0 + เงินที่รับ = ยอดเต็ม ⇒ ยอดเต็มคงที่ (ใบออกเต็ม)
            var res = await svc.ConvertTrialAsync(f.BuyerId, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Annual, WithholdTax: true), "owner");
            var row = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == res.Payment!.Id);
            var gross = PlatformWhtSuggestion.GrossOf(row.Amount, row.WithholdingTaxAmount);
            Assert.True(row.WithholdingTaxAmount > 0m);
            // ทิศตรงข้าม: เงิน + ภาษีหัก เกินยอดเต็ม ⇒ ปฏิเสธ (ไม่ขยายยอด)
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ReviewPaymentAsync(row.Id,
                new ReviewSubscriptionPaymentRequest(true, "ตรวจสลิป", null, WithholdingTaxAmount: 10m, AmountReceived: gross), admin));
            await svc.ReviewPaymentAsync(row.Id, new ReviewSubscriptionPaymentRequest(true, "โอนเต็ม ไม่หัก", null, WithholdingTaxAmount: 0m, AmountReceived: gross), admin);
            var approved = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == row.Id);
            Assert.Equal(0m, approved.WithholdingTaxAmount);
            Assert.Equal(gross, approved.Amount);   // ใบออกเต็ม (เงินที่รับ + ภาษีหัก = ยอดเต็มเดิม)

            // S1: ติ๊กหักแล้วภายหลังเลขผู้เสียภาษีเปลี่ยนเป็นบุคคล ⇒ เงินเข้าออนไลน์ไม่อนุมัติอัตโนมัติ — พักให้แอดมิน
            var res2 = await svc.ConvertTrialAsync(g.BuyerId, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Annual, WithholdTax: true), "owner");
            var pay2 = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == res2.Payment!.Id);
            var buyer2 = await db.Companies.FirstAsync(c => c.Id == g.BuyerId);
            buyer2.TaxId = "3101000000013";
            await db.SaveChangesAsync();
            var intent = new PaymentIntent
            {
                CompanyId = f.PlatformId, PayerCompanyId = g.BuyerId, SourceKind = PaymentSourceKind.SubscriptionPayment, SourceId = pay2.Id,
                ProviderCode = "test", Amount = pay2.Amount, Status = PaymentIntentStatus.Succeeded, ConfirmedAt = DateTime.UtcNow,
                IdempotencyKey = "t234s1-" + Guid.NewGuid().ToString("N"),
            };
            var handler = new Accounting.Services.Payments.Handlers.SubscriptionPaymentHandler(db, svc,
                NullLogger<Accounting.Services.Payments.Handlers.SubscriptionPaymentHandler>.Instance);
            await handler.HandleSucceededAsync(intent);
            var held = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay2.Id);
            Assert.Equal(SubscriptionPaymentStatus.UnderReview, held.Status);
            Assert.Equal(PlatformPaymentAttention.RuleHeld, held.AttentionRule);
        });
    }
}
