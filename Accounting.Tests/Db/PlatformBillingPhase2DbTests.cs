using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 219 เฟส 2 (PLATFORM_BILLING_AUDIT §5 · คำตัดสินข้อ 180) — "เก็บเงินได้จริง" บน PostgreSQL จริง: ค่าตั้งการเรียกเก็บ (audit) ·
/// แจ้งชำระใบแจ้งหนี้ผ่านศูนย์บิล (ขอบเขตผู้จ่าย · ยอดจากเซิร์ฟเวอร์ · ติ๊กยอมรับบังคับ · ใบค่าใช้งานไม่ต่ออายุ / ใบต่ออายุต่ออายุ)
/// <para>ทุกเทสต์อยู่ในคลาสเดียว (xUnit รันเรียงในคลาส) เพราะตั้ง <c>SiteSettings.PlatformCompanyId</c> / ค่าตั้งแถวเดียวของระบบชั่วคราวแล้วคืนค่า</para>
/// </summary>
[Trait("Category", "Db")]
public class PlatformBillingPhase2DbTests
{
    private readonly ITestOutputHelper _out;
    public PlatformBillingPhase2DbTests(ITestOutputHelper output) => _out = output;

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private static PlatformPurchaseAcceptanceInput Accept(bool ok = true, string? version = null) =>
        new(ok, version ?? PlatformPurchaseTerms.Version, "owner@example.com", "billing-center", "10.0.0.1", "xunit");

    internal sealed record Fixture(Guid PlatformId, Guid BuyerId, Subscription Sub, Guid ContactId);

    /// <summary>ตั้งบริษัทผู้ให้บริการชั่วคราว แล้วคืนค่าเดิมเสมอ (แถวเดียวของระบบ)</summary>
    internal static async Task WithPlatformTenantAsync(Accounting.Data.AccountingDbContext db, Guid platformId, Func<Task> body)
    {
        var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        var created = s == null;
        if (s == null) { s = new SiteSettings(); db.SiteSettings.Add(s); }
        var original = s.PlatformCompanyId;
        s.PlatformCompanyId = platformId;
        await db.SaveChangesAsync();
        try { await body(); }
        finally
        {
            var again = await db.SiteSettings.FirstAsync(x => x.Id == s.Id);
            if (created) db.SiteSettings.Remove(again); else again.PlatformCompanyId = original;
            await db.SaveChangesAsync();
        }
    }

    internal static async Task<Fixture> SeedAsync(Accounting.Data.AccountingDbContext db, Guid? platformId = null,
        SubscriptionPlan plan = SubscriptionPlan.Pro, SubscriptionStatus status = SubscriptionStatus.Active, int endInDays = 20)
    {
        Guid pid;
        if (platformId is Guid p) pid = p;
        else
        {
            var platform = new Company { Name = "NextAcc ผู้ให้บริการ (ทดสอบเฟส 2)", TaxId = "0105556000001" };
            db.Companies.Add(platform);
            await db.SaveChangesAsync();
            pid = platform.Id;
        }
        var buyer = new Company { Name = "ร้านลูกค้าเฟส 2", TaxId = "0105556000002" };
        db.Companies.Add(buyer);
        await db.SaveChangesAsync();
        var sub = new Subscription
        {
            CompanyId = buyer.Id, Plan = plan, Status = status, BillingCycle = BillingCycle.Monthly, PricePerCycle = 990m,
            StartDate = DateTime.UtcNow.AddDays(-10), EndDate = DateTime.UtcNow.AddDays(endInDays), UsageResetDate = DateTime.UtcNow.AddDays(20),
        };
        db.Subscriptions.Add(sub);
        var contact = new Contact
        {
            CompanyId = pid, Name = buyer.Name, IsCustomer = true,
            ExternalSystem = PlatformInvoiceRules.TenantContactSystem, ExternalId = buyer.Id.ToString(),
        };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return new Fixture(pid, buyer.Id, sub, contact.Id);
    }

    internal static async Task<Document> InvoiceAsync(Accounting.Data.AccountingDbContext db, Fixture f, string reference, decimal amount,
        DateTime due, DocumentStatus status = DocumentStatus.Approved, decimal? balance = null)
    {
        var d = new Document
        {
            CompanyId = f.PlatformId, ContactId = f.ContactId, DocumentType = DocumentType.Invoice,
            DocumentNumber = "INV-T" + Guid.NewGuid().ToString("N")[..8], Status = status, Reference = reference,
            DocumentDate = DateTime.UtcNow.AddDays(-1), DueDate = due, SubTotal = amount, TotalAmount = amount,
            BalanceDue = balance ?? amount, Currency = "THB",
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "ค่าใช้งานทดสอบ", Quantity = 1, UnitPrice = amount, Amount = amount });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    private static async Task EnsurePricedTemplateAsync(Accounting.Data.AccountingDbContext db, SubscriptionPlan plan)
    {
        if (await db.PlanTemplates.AnyAsync(t => t.Plan == plan && t.IsActive && t.MonthlyPrice > 0)) return;
        db.PlanTemplates.Add(new PlanTemplate
        {
            Name = "แพ็กเกจทดสอบเฟส 2", Plan = plan, IsActive = true,
            MonthlyPrice = 990m, QuarterlyPrice = 2800m, SemiAnnualPrice = 5400m, AnnualPrice = 9900m,
            MaxUsers = 5, MaxCompanies = 1, MaxDocumentsPerMonth = 500, MaxJournalEntriesPerMonth = 500,
        });
        await db.SaveChangesAsync();
    }

    // ═══════════════ ขั้น 1: ค่าตั้งการเรียกเก็บ ═══════════════

    [Fact]
    public async Task Settings_update_round_trips_writes_audit_chain_and_rejects_invalid()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม"); return; }
        var before = await db.PlatformBillingSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == PlatformBillingSettings.SingletonId);
        var svc = new PlatformBillingSettingsService(db);
        var actor = new PlatformBillingActor(null, "admin@example.com", "127.0.0.1");
        try
        {
            var dto = await svc.UpdateAsync(new UpdatePlatformBillingSettingsRequest(10, "5, 2", "2,9", 21, false, 30, true, false),
                actor, SubscriptionEnforcementMode.Shadow);
            Assert.Equal("5,2", dto.ReminderDaysBeforeDue);
            Assert.False(dto.IsDefault);
            var p = await PlatformBillingSettingsService.LoadPolicyAsync(db);
            Assert.Equal(10, p.RenewalInvoiceLeadDays);
            Assert.Equal(new[] { 2, 9 }, p.ReminderDaysAfterDue);
            Assert.Equal(21, p.SuspendGraceDays);
            Assert.False(p.AutoSuspendEnabled);
            Assert.False(p.ReminderLineEnabled);
            Assert.Equal(1, await db.PlatformBillingSettings.CountAsync());   // แถวเดียวเสมอ
            Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityType == nameof(PlatformBillingSettings)
                && a.NewValues!.Contains(PlatformBillingSettingsService.AuditRuleCode)));

            // ทิศตรงข้าม: ค่าผิด ⇒ ปฏิเสธ ไม่เขียนทับ
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.UpdateAsync(
                new UpdatePlatformBillingSettingsRequest(0, "7", "1", 14, true, 14, true, true), actor, SubscriptionEnforcementMode.Shadow));
            Assert.Equal(10, (await PlatformBillingSettingsService.LoadPolicyAsync(db)).RenewalInvoiceLeadDays);
        }
        finally
        {
            var row = await db.PlatformBillingSettings.FirstOrDefaultAsync(x => x.Id == PlatformBillingSettings.SingletonId);
            if (row != null)
            {
                if (before == null) db.PlatformBillingSettings.Remove(row);
                else
                {
                    row.RenewalInvoiceLeadDays = before.RenewalInvoiceLeadDays; row.ReminderDaysBeforeDue = before.ReminderDaysBeforeDue;
                    row.ReminderDaysAfterDue = before.ReminderDaysAfterDue; row.SuspendGraceDays = before.SuspendGraceDays;
                    row.AutoSuspendEnabled = before.AutoSuspendEnabled; row.PaymentLinkExpiryDays = before.PaymentLinkExpiryDays;
                    row.ReminderEmailEnabled = before.ReminderEmailEnabled; row.ReminderLineEnabled = before.ReminderLineEnabled;
                }
                await db.SaveChangesAsync();
            }
        }
    }

    // ═══════════════ ขั้น 1: แจ้งชำระใบแจ้งหนี้ (ศูนย์บิล) ═══════════════

    [Fact]
    public async Task Invoice_payment_scoped_to_buyer_amount_from_server_terms_required_and_does_not_extend()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var a = await SeedAsync(db);
        var b = await SeedAsync(db, a.PlatformId);
        var invA = await InvoiceAsync(db, a, PlatformInvoiceRules.UsageReferencePrefix + "2026-09-A", 535m, DateTime.UtcNow.AddDays(7));
        var invB = await InvoiceAsync(db, b, PlatformInvoiceRules.UsageReferencePrefix + "2026-09-B", 999m, DateTime.UtcNow.AddDays(7));
        var paid = await InvoiceAsync(db, a, PlatformInvoiceRules.UsageReferencePrefix + "2026-08-A", 100m, DateTime.UtcNow.AddDays(-20),
            DocumentStatus.Paid, 0m);
        var endBefore = a.Sub.EndDate;

        await WithPlatformTenantAsync(db, a.PlatformId, async () =>
        {
            var svc = new SubscriptionService(db, Notifications());
            var req = new SubmitInvoicePaymentRequest(DateTime.UtcNow, "REF-1", "กสิกร", null);

            // ใบของบริษัทอื่น = ไม่พบ (ไม่บอกว่ามีอยู่)
            await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.SubmitInvoicePaymentAsync(a.BuyerId, invB.Id, req, Accept(), "owner"));
            // ไม่ติ๊กยอมรับ ⇒ 400 · ฉบับเก่า ⇒ 409 · ไม่เกิดรายการ
            var notAccepted = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitInvoicePaymentAsync(a.BuyerId, invA.Id, req, Accept(false), "owner"));
            Assert.Equal(400, notAccepted.StatusCode);
            var stale = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitInvoicePaymentAsync(a.BuyerId, invA.Id, req, Accept(true, "2020-01-01.1"), "owner"));
            Assert.Equal(409, stale.StatusCode);
            // ใบชำระครบแล้ว ⇒ 409
            var closed = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitInvoicePaymentAsync(a.BuyerId, paid.Id, req, Accept(), "owner"));
            Assert.Equal(409, closed.StatusCode);
            Assert.Equal(0, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == a.Sub.Id));

            // ผ่าน: ยอดจากใบ · ผูกใบ · ไม่มีเดือนต่ออายุ · หลักฐานการยอมรับตรวจกลับได้
            var pay = await svc.SubmitInvoicePaymentAsync(a.BuyerId, invA.Id, req, Accept(), "owner");
            Assert.Equal(535m, pay.Amount);
            Assert.Equal(0, pay.RequestedPeriodMonths);
            Assert.Equal(invA.Id, pay.PlatformInvoiceDocumentId);
            var row = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id);
            var acc = await db.PlatformPurchaseAcceptances.AsNoTracking().SingleAsync(x => x.Id == row.PolicyAcceptanceId);
            Assert.Equal(PlatformPurchaseTerms.KindInvoicePayment, acc.SubjectKind);
            Assert.Equal(PdpaConsentEvidence.EvidenceCheck.Valid, PdpaConsentEvidence.Check(acc.EvidenceHash, acc.Subject,
                PlatformPurchaseTerms.Purpose, acc.PolicyVersion, acc.AcceptedAt, acc.Channel, acc.IpAddress, acc.UserAgent));

            // กดซ้ำก่อนแนบสลิป = รายการเดิม (ไม่เกิดรายการซ้อน)
            var again = await svc.SubmitInvoicePaymentAsync(a.BuyerId, invA.Id, req, Accept(), "owner");
            Assert.Equal(pay.Id, again.Id);

            // แอดมินอนุมัติ ⇒ ตัดหนี้อย่างเดียว: วันหมดอายุ/แพ็กเกจไม่ขยับ
            await svc.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(true, "ok", null), "admin", systemConfirmed: true);
            var subAfter = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == a.Sub.Id);
            Assert.Equal(endBefore, subAfter.EndDate);
            Assert.Equal(SubscriptionPlan.Pro, subAfter.Plan);
            Assert.Equal(SubscriptionPaymentStatus.Approved, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).Status);
        });
    }

    [Fact]
    public async Task Renewal_invoice_payment_extends_from_current_end_date()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsurePricedTemplateAsync(db, SubscriptionPlan.Pro);
        var f = await SeedAsync(db);
        var inv = await InvoiceAsync(db, f, PlatformInvoiceRules.RenewalReferencePrefix + "T", 990m, f.Sub.EndDate);
        var sub = await db.Subscriptions.FirstAsync(s => s.Id == f.Sub.Id);
        sub.RenewalInvoiceDocumentId = inv.Id; sub.RenewalInvoiceNumber = inv.DocumentNumber; sub.RenewalInvoiceForEndDate = sub.EndDate;
        await db.SaveChangesAsync();
        var endBefore = sub.EndDate;

        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var svc = new SubscriptionService(db, Notifications());
            var pay = await svc.SubmitInvoicePaymentAsync(f.BuyerId, inv.Id, new SubmitInvoicePaymentRequest(null, null, null, null), Accept(), "owner");
            Assert.Equal(1, pay.RequestedPeriodMonths);   // ใบต่ออายุรายเดือน ⇒ 1 เดือน
            await svc.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(true, null, null), "admin", systemConfirmed: true);
            var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id);
            Assert.Equal(endBefore.AddMonths(1), after.EndDate);   // ต่อจากวันหมดอายุเดิม (ไม่เสียวันที่จ่ายไปแล้ว)
        });
    }
}
