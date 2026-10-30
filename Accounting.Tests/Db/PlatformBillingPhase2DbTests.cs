using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    public async Task Invoice_payment_scoped_to_buyer_amount_from_server_and_does_not_extend()
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
            await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.SubmitInvoicePaymentAsync(a.BuyerId, invB.Id, req, "owner"));
            // ใบชำระครบแล้ว ⇒ 409
            var closed = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitInvoicePaymentAsync(a.BuyerId, paid.Id, req, "owner"));
            Assert.Equal(409, closed.StatusCode);
            Assert.Equal(0, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == a.Sub.Id));

            // ผ่าน: ยอดจากใบ · ผูกใบ · ไม่มีเดือนต่ออายุ (ติ๊กยอมรับนโยบายตรวจที่ผู้เรียก — part B · ล็อกด้วย required_call_site_check)
            var pay = await svc.SubmitInvoicePaymentAsync(a.BuyerId, invA.Id, req, "owner");
            Assert.Equal(535m, pay.Amount);
            Assert.Equal(0, pay.RequestedPeriodMonths);
            Assert.Equal(invA.Id, pay.PlatformInvoiceDocumentId);

            // กดซ้ำก่อนแนบสลิป = รายการเดิม (ไม่เกิดรายการซ้อน)
            var again = await svc.SubmitInvoicePaymentAsync(a.BuyerId, invA.Id, req, "owner");
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
            var pay = await svc.SubmitInvoicePaymentAsync(f.BuyerId, inv.Id, new SubmitInvoicePaymentRequest(null, null, null, null), "owner");
            Assert.Equal(1, pay.RequestedPeriodMonths);   // ใบต่ออายุรายเดือน ⇒ 1 เดือน
            await svc.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(true, null, null), "admin", systemConfirmed: true);
            var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id);
            Assert.Equal(endBefore.AddMonths(1), after.EndDate);   // ต่อจากวันหมดอายุเดิม (ไม่เสียวันที่จ่ายไปแล้ว)
        });
    }

    // ═══════════════ ขั้น 2: dunning + ส่งใบ ═══════════════

    private static ISubscriptionGateShadowLog Gate(SubscriptionEnforcementMode mode) =>
        F4Proxy<ISubscriptionGateShadowLog>.Create((name, _) => name == nameof(ISubscriptionGateShadowLog.ReadAdminSwitchAsync)
            ? new SubscriptionAdminSwitchRead(mode, SubscriptionAdminSwitchSource.Stored) : null);

    private sealed class CountingNotifier : IPlatformBillingNotifier
    {
        public readonly List<string> Keys = new();
        public Task InvoiceIssuedAsync(Guid buyerCompanyId, Guid invoiceDocumentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> SendAsync(Guid buyerCompanyId, Guid? documentId, string messageKey, string subject, string body, CancellationToken ct = default)
        { Keys.Add(messageKey); return Task.FromResult(1); }
    }

    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    [Fact]
    public async Task Dunning_suspends_in_enforce_shadow_records_only_and_restores_after_payment()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await SeedAsync(db);
        var inv = await InvoiceAsync(db, f, PlatformInvoiceRules.UsageReferencePrefix + "OLD", 300m, DateTime.UtcNow.AddDays(-20));

        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var notifier = new CountingNotifier();
            // โหมดเงา ⇒ "จะระงับ" อย่างเดียว · สถานะไม่ขยับ
            var shadow = new PlatformDunningService(db, notifier, Gate(SubscriptionEnforcementMode.Shadow), EmptyConfig(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformDunningService>.Instance);
            var r1 = await shadow.EvaluateCompanyAsync(f.BuyerId, sendReminders: true);
            Assert.Equal(DunningAction.WouldSuspend, r1!.Action);
            Assert.Equal(SubscriptionStatus.Active, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).Status);
            Assert.Contains(notifier.Keys, k => k == PlatformDunningPolicy.Key(inv.Id, "after", 7));

            // บังคับ ⇒ ระงับจริง + ประวัติ + audit chain
            var enforce = new PlatformDunningService(db, notifier, Gate(SubscriptionEnforcementMode.Enforce), EmptyConfig(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformDunningService>.Instance);
            var r2 = await enforce.EvaluateCompanyAsync(f.BuyerId, sendReminders: false);
            Assert.Equal(DunningAction.Suspend, r2!.Action);
            Assert.Equal(SubscriptionStatus.Suspended, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).Status);
            Assert.True(await db.AuditLogs.AnyAsync(a => a.CompanyId == f.BuyerId && a.NewValues!.Contains(PlatformDunningService.RuleSuspend)));

            // ชำระครบ ⇒ คืนสถานะ (Active — รอบยังไม่หมด)
            var doc = await db.Documents.FirstAsync(d => d.Id == inv.Id);
            doc.BalanceDue = 0m; doc.PaidAmount = 300m; doc.Status = DocumentStatus.Paid;
            await db.SaveChangesAsync();
            var r3 = await enforce.EvaluateCompanyAsync(f.BuyerId, sendReminders: false);
            Assert.Equal(DunningAction.Restore, r3!.Action);
            Assert.Equal(SubscriptionStatus.Active, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).Status);
            Assert.Null((await db.PlatformDunningStates.AsNoTracking().SingleAsync(s => s.CompanyId == f.BuyerId)).SuspendedByDunningAt);
        });
    }

    [Fact]
    public async Task Dunning_does_not_touch_suspension_by_other_cause_and_admin_exemption_restores()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var other = await SeedAsync(db, status: SubscriptionStatus.Suspended);
        await InvoiceAsync(db, other, PlatformInvoiceRules.UsageReferencePrefix + "X", 300m, DateTime.UtcNow.AddDays(-30));
        var f = await SeedAsync(db, other.PlatformId);
        await InvoiceAsync(db, f, PlatformInvoiceRules.UsageReferencePrefix + "Y", 300m, DateTime.UtcNow.AddDays(-30));

        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var svc = new PlatformDunningService(db, new CountingNotifier(), Gate(SubscriptionEnforcementMode.Enforce), EmptyConfig(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformDunningService>.Instance);
            // ระงับโดยแอดมินอยู่ก่อน ⇒ ระบบไม่ถือว่าเป็นของตน (จ่ายแล้วก็ไม่ปลดให้เอง)
            var r = await svc.EvaluateCompanyAsync(other.BuyerId, false);
            Assert.Equal(DunningAction.None, r!.Action);
            Assert.Null((await db.PlatformDunningStates.AsNoTracking().SingleAsync(s => s.CompanyId == other.BuyerId)).SuspendedByDunningAt);

            Assert.Equal(DunningAction.Suspend, (await svc.EvaluateCompanyAsync(f.BuyerId, false))!.Action);
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ExemptAsync(f.BuyerId, 7, " ", new PlatformBillingActor(null, "a@x", null)));
            await svc.ExemptAsync(f.BuyerId, 7, "ลูกค้าแจ้งโอนแล้วรอตรวจ", new PlatformBillingActor(null, "a@x", null));
            Assert.Equal(SubscriptionStatus.Active, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).Status);
            // ระหว่างพัก ⇒ ไม่ระงับซ้ำ
            Assert.Equal(DunningAction.None, (await svc.EvaluateCompanyAsync(f.BuyerId, false))!.Action);
        });
    }

    [Fact]
    public async Task Notifier_claims_each_key_once_per_channel()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await SeedAsync(db);
        var notifier = new PlatformBillingNotifier(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformBillingNotifier>.Instance);
        var key = "test:" + Guid.NewGuid().ToString("N");
        await notifier.SendAsync(f.BuyerId, null, key, "หัวข้อ", "เนื้อความ");
        await notifier.SendAsync(f.BuyerId, null, key, "หัวข้อ", "เนื้อความ");   // ซ้ำ ⇒ ไม่จองแถวใหม่
        var rows = await db.PlatformBillingNotices.AsNoTracking().Where(n => n.MessageKey == key).ToListAsync();
        Assert.True(rows.Count <= 2);   // ≤ 1 แถวต่อช่อง (อีเมล + LINE ตามค่าตั้ง)
        Assert.Equal(rows.Count, rows.Select(r => r.Channel).Distinct().Count());
        // ไม่มีบริการอีเมล/LINE ⇒ ประทับ "ข้าม" พร้อมเหตุ (ไม่ใช่ "ส่งแล้ว")
        Assert.All(rows, r => Assert.Equal(PlatformBillingNotifier.StatusSkipped, r.Status));
    }

    // ═══════════════ ขั้น 3: ใบต่ออายุของรอบที่ยกเลิก (part B) · วิธีชำระส่วนเสริม · ใบต่ออายุที่ล้าสมัย (S9) ═══════════════

    [Fact]
    public async Task Renewal_invoice_of_cancelled_subscription_is_not_payable_and_not_dunned_until_resumed()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await SeedAsync(db, endInDays: 3);
        var renewal = await InvoiceAsync(db, f, PlatformInvoiceRules.RenewalReferencePrefix + "C", 990m, DateTime.UtcNow.AddDays(-30));
        var sub = await db.Subscriptions.FirstAsync(s => s.Id == f.Sub.Id);
        sub.RenewalInvoiceDocumentId = renewal.Id; sub.RenewalInvoiceForEndDate = sub.EndDate; sub.RenewalInvoiceNumber = renewal.DocumentNumber;
        await db.SaveChangesAsync();
        var svc = new SubscriptionService(db, Notifications());
        await svc.CancelSubscriptionAsync(f.BuyerId, "owner");   // part B: ใช้ได้ถึงสิ้นรอบ
        Assert.True((await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).CancelAtPeriodEnd);

        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitInvoicePaymentAsync(f.BuyerId, renewal.Id,
                new SubmitInvoicePaymentRequest(null, null, null, null), "owner"));
            Assert.Equal("PLATFORM-RENEWAL-CANCELLED", ex.RuleCode);
            var dunning = new PlatformDunningService(db, new CountingNotifier(), Gate(SubscriptionEnforcementMode.Enforce), EmptyConfig(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformDunningService>.Instance);
            Assert.Equal(DunningAction.None, (await dunning.EvaluateCompanyAsync(f.BuyerId, false))!.Action);   // ไม่ระงับเพราะใบของรอบที่ยกเลิก

            // ทิศตรงข้าม: กลับมาใช้ต่อ ⇒ ใบต่ออายุกลับเป็นหนี้ปกติ (จ่ายได้ · นับทวงหนี้)
            await svc.ResumeSubscriptionAsync(f.BuyerId, "owner");
            var pay = await svc.SubmitInvoicePaymentAsync(f.BuyerId, renewal.Id, new SubmitInvoicePaymentRequest(null, null, null, null), "owner");
            Assert.Equal(renewal.Id, pay.PlatformInvoiceDocumentId);
        });
    }

    private static async Task<(Guid CompanyId, string Code)> SeedPaidAddOnAsync(Accounting.Data.AccountingDbContext db)
    {
        var code = "test.p2." + Guid.NewGuid().ToString("N")[..8];
        var company = new Company { Name = "ร้านซื้อส่วนเสริมเฟส 2", TaxId = "0105556000002" };
        db.Companies.Add(company);
        db.ApiFeatures.Add(new ApiFeature { FeatureCode = code, Name = "ส่วนเสริมทดสอบ", IsPublished = true });
        db.ApiPricingPlans.Add(new ApiPricingPlan { FeatureCode = code, Method = PricingMethod.FlatMonthly, UnitPrice = 299m, EffectiveFrom = DateTime.UtcNow.AddDays(-30) });
        await db.SaveChangesAsync();
        return (company.Id, code);
    }

    [Fact]
    public async Task Addon_purchase_requires_choice_monthly_invoice_skips_upfront()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, code) = await SeedPaidAddOnAsync(db);
        var metering = new UsageMeteringService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageMeteringService>.Instance);

        // ไม่เลือกวิธีชำระ ⇒ 400 · ไม่มีแถวเปิดใช้
        var noChoice = await Assert.ThrowsAsync<BusinessRuleException>(() => metering.SetFeatureEnabledAsync(cid, code, true, "owner", (AddOnBillingChoice?)null));
        Assert.Equal("ADDON-CHOICE-REQUIRED", noChoice.RuleCode);
        Assert.False(await db.CompanyFeatures.AnyAsync(x => x.CompanyId == cid && x.FeatureCode == code && x.IsEnabled));

        // รวมใบรายเดือน ⇒ ใช้ได้ทันที · ไม่มีงวดจ่ายล่วงหน้า · หลักฐานการยอมรับ
        await metering.SetFeatureEnabledAsync(cid, code, true, "owner", AddOnBillingChoice.MonthlyInvoice);
        var row = await db.CompanyFeatures.AsNoTracking().SingleAsync(x => x.CompanyId == cid && x.FeatureCode == code);
        Assert.Equal(AddOnPaymentStatus.NotRequired, row.PaymentStatus);
        Assert.Null(row.UpfrontPeriod);
        Assert.Equal(AddOnBillingChoice.MonthlyInvoice, row.BillingChoice);
        Assert.True(await metering.IsFeatureEnabledAsync(cid, code));
        // ค่าเหมาเดือนนี้เข้าใบรายเดือนเต็ม (ไม่ ฿0 — ไม่ได้จ่ายล่วงหน้า)
        var period = AddOnBilling.PeriodOf(DateTime.UtcNow);
        var charged = await metering.RecordFlatMonthlyAsync(cid, code, period);
        Assert.Equal(299m, charged.ChargedAmount);

        // ปิดแล้วเปิดใหม่เลือกจ่ายล่วงหน้างวดเดียวกัน ⇒ 409 (เก็บซ้ำ)
        await metering.SetFeatureEnabledAsync(cid, code, false, "owner", (AddOnBillingChoice?)null);
        var dup = await Assert.ThrowsAsync<BusinessRuleException>(() => metering.SetFeatureEnabledAsync(cid, code, true, "owner", AddOnBillingChoice.Upfront));
        Assert.Equal(409, dup.StatusCode);
    }

    [Fact]
    public async Task Addon_upfront_choice_keeps_phase0_upfront_flow()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, code) = await SeedPaidAddOnAsync(db);
        var metering = new UsageMeteringService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageMeteringService>.Instance);
        await metering.SetFeatureEnabledAsync(cid, code, true, "owner", AddOnBillingChoice.Upfront);
        var row = await db.CompanyFeatures.AsNoTracking().SingleAsync(x => x.CompanyId == cid && x.FeatureCode == code);
        Assert.Equal(AddOnPaymentStatus.AwaitingPayment, row.PaymentStatus);
        Assert.NotNull(row.UpfrontPeriod);
        Assert.Equal(0m, (await metering.RecordFlatMonthlyAsync(cid, code, row.UpfrontPeriod!)).ChargedAmount);   // ยังไม่จ่าย = ไม่ออกค่าเดือน (เฟส 0)
    }

    [Fact]
    public async Task Upgrade_approval_clears_stale_renewal_and_issuer_voids_only_unpaid()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsurePricedTemplateAsync(db, SubscriptionPlan.Pro);
        var f = await SeedAsync(db);
        var unpaid = await InvoiceAsync(db, f, PlatformInvoiceRules.RenewalReferencePrefix + "S9A", 990m, f.Sub.EndDate);
        var partly = await InvoiceAsync(db, f, PlatformInvoiceRules.RenewalReferencePrefix + "S9B", 990m, f.Sub.EndDate, DocumentStatus.PartiallyPaid, 490m);
        var pd = await db.Documents.FirstAsync(d => d.Id == partly.Id); pd.PaidAmount = 500m; await db.SaveChangesAsync();
        var sub = await db.Subscriptions.FirstAsync(s => s.Id == f.Sub.Id);
        sub.RenewalInvoiceDocumentId = unpaid.Id; sub.RenewalInvoiceForEndDate = sub.EndDate; sub.RenewalInvoiceNumber = unpaid.DocumentNumber;
        await db.SaveChangesAsync();

        // ต่ออายุแบบเดิม (ไม่ผูกใบ) อนุมัติ ⇒ ตัวชี้ใบต่ออายุของรอบเดิมถูกล้าง (ใบเดิมไปจัดการต่อ — ไม่ค้างเป็นหนี้เท็จ)
        var pay = new SubscriptionPayment
        {
            SubscriptionId = sub.Id, PaymentNumber = "SP-S9-" + Guid.NewGuid().ToString("N")[..6], Amount = 990m, PaymentDate = DateTime.UtcNow,
            RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1,
            Status = SubscriptionPaymentStatus.Pending,
        };
        db.SubscriptionPayments.Add(pay);
        await db.SaveChangesAsync();
        var svc = new SubscriptionService(db, Notifications());
        await svc.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(true, null, null), "admin", systemConfirmed: true);
        Assert.Null((await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).RenewalInvoiceDocumentId);

        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var voided = new List<Guid>();
            var docs = F4Proxy<IDocumentService>.Create((name, args) =>
            {
                if (name == nameof(IDocumentService.VoidDocumentAsync)) voided.Add((Guid)args[1]!);
                return null;
            });
            var issuer = new PlatformBillingDocumentIssuer(db, new F4ServiceMap().Add(docs),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformBillingDocumentIssuer>.Instance);
            // ยังไม่มีการชำระ ⇒ ยกเลิกผ่านเส้นยกเลิกเอกสารปกติ
            Assert.Equal(StaleRenewalOutcome.Voided, await issuer.VoidStaleRenewalInvoiceAsync(unpaid.Id));
            Assert.Contains(unpaid.Id, voided);
            // มีการชำระแล้ว ⇒ ไม่ยกเลิก — ต้องออกใบลดหนี้ (งานแอดมิน)
            Assert.Equal(StaleRenewalOutcome.NeedsCreditNote, await issuer.VoidStaleRenewalInvoiceAsync(partly.Id));
            Assert.DoesNotContain(partly.Id, voided);

            // ตัวห่อ ⇒ ประวัติ + audit chain
            var billing = new SaasBillingDocumentService(db, F4Proxy<IPdfGenerationService>.Create((_, _) => null),
                F4Proxy<IFileAttachmentService>.Create((_, _) => null),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SaasBillingDocumentService>.Instance, issuer: issuer);
            await billing.HandleStaleRenewalInvoiceAsync(sub.Id, partly.Id, pay.PaymentNumber);
            Assert.True(await db.SubscriptionHistories.AnyAsync(h => h.SubscriptionId == sub.Id && h.Action == "StaleRenewalInvoiceNeedsCreditNote"));
            Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityId == partly.Id.ToString()
                && a.NewValues!.Contains(SaasBillingDocumentService.RuleStaleCreditNote)));
        });
    }

    // ═══════════════ ขั้น 4: ลิงก์ชำระ ═══════════════

    private static ILegalDocumentService Legal() => F4Proxy<ILegalDocumentService>.Create((name, _) =>
        name == nameof(ILegalDocumentService.GetPurchasePolicyVersionAsync) ? new LegalPurchasePolicyVersion("t1", "r1", "v-test", true) : null);

    private static Accounting.Services.Payments.IPlatformPaymentService Platform() => F4Proxy<Accounting.Services.Payments.IPlatformPaymentService>.Create((name, _) =>
        name == nameof(Accounting.Services.Payments.IPlatformPaymentService.StatusAsync)
            ? new Accounting.Services.Payments.PlatformPayStatus(false, false, null, null, Array.Empty<string>(), "ยังไม่ตั้ง") : null);

    private static PlatformPaymentLinkService Links(Accounting.Data.AccountingDbContext db) => new(db, Platform(), Legal());

    [Fact]
    public async Task Payment_link_hash_only_wrong_token_expired_revoked_paid_and_cross_invoice()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await SeedAsync(db);
        var invA = await InvoiceAsync(db, f, PlatformInvoiceRules.UsageReferencePrefix + "LA", 400m, DateTime.UtcNow.AddDays(5));
        var invB = await InvoiceAsync(db, f, PlatformInvoiceRules.UsageReferencePrefix + "LB", 700m, DateTime.UtcNow.AddDays(5));
        var admin = new PlatformBillingActor(null, "admin@example.com", "127.0.0.1");

        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var svc = Links(db);
            var created = await svc.CreateAsync(invA.Id, null, admin);
            var row = await db.PlatformPaymentLinks.AsNoTracking().SingleAsync(l => l.Id == created.Id);
            Assert.NotEqual(created.Token, row.TokenHash);                       // ฐานไม่มีโทเคนจริง
            Assert.Equal(PlatformPaymentLinkRules.Hash(created.Token), row.TokenHash);
            Assert.True(created.Token.Length >= 22);                             // 128 บิต
            Assert.InRange((row.ExpiresAt - DateTime.UtcNow).TotalDays, 13.9, 14.1);   // ค่าตั้งต้น 14 วัน

            // โทเคนผิด/รูปผิด = ไม่พบ
            Assert.Null(await svc.ViewAsync("x" + created.Token[1..], null));
            Assert.Null(await svc.ViewAsync("<script>", null));

            var view = await svc.ViewAsync(created.Token, "10.0.0.9");
            Assert.Equal(nameof(PaymentLinkState.Valid), view!.State);
            Assert.Equal(400m, view.AmountDue);
            Assert.Equal(invA.DocumentNumber, view.InvoiceNumber);   // ใบของลิงก์เท่านั้น
            Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityId == created.Id.ToString() && a.NewValues!.Contains(PlatformPaymentLinkService.RuleView)));

            // แจ้งชำระผ่านลิงก์ ⇒ ใบ A เท่านั้น (ข้ามใบไม่ได้ — ใบมาจากลิงก์ ไม่ใช่ body)
            var target = await svc.ResolveForPaymentAsync(created.Token);
            Assert.Equal(invA.Id, target!.Value.DocumentId);
            // ไม่ติ๊ก/ฉบับเก่า ⇒ ปฏิเสธ (ด่าน part B) · ติ๊กฉบับปัจจุบัน ⇒ หลักฐานผูกใบ ตรวจกลับได้
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.EnforcePolicyAsync(created.Id, f.BuyerId, invA.Id, false, null, null, null));
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.EnforcePolicyAsync(created.Id, f.BuyerId, invA.Id, true, "v-old", null, null));
            await svc.EnforcePolicyAsync(created.Id, f.BuyerId, invA.Id, true, "v-test", "10.0.0.9", "xunit");
            var subs = new SubscriptionService(db, Notifications());
            var pay = await subs.SubmitInvoicePaymentAsync(target.Value.BuyerCompanyId, target.Value.DocumentId,
                new SubmitInvoicePaymentRequest(null, null, null, null), $"paylink:{created.Id}");
            Assert.Equal(invA.Id, pay.PlatformInvoiceDocumentId);
            var acc = await db.PolicyAcceptances.AsNoTracking().SingleAsync(x => x.SourceId == invA.Id && x.Subject == $"paylink:{created.Id}");
            Assert.Equal(PurchasePolicyAcceptance.ChannelPlatformInvoice, acc.Channel);
            Assert.True(PurchasePolicyAcceptance.Verify(acc));

            // ใบชำระครบ ⇒ ลิงก์ปิดเอง
            var doc = await db.Documents.FirstAsync(d => d.Id == invA.Id);
            doc.BalanceDue = 0m; doc.PaidAmount = 400m; doc.Status = DocumentStatus.Paid;
            await db.SaveChangesAsync();
            Assert.Equal(nameof(PaymentLinkState.Paid), (await svc.ViewAsync(created.Token, null))!.State);
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ResolveForPaymentAsync(created.Token));

            // เพิกถอน ⇒ ใช้ไม่ได้ (เหตุผลบังคับ)
            var l2 = await svc.CreateAsync(invB.Id, 3, admin);
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.RevokeAsync(l2.Id, " ", admin));
            await svc.RevokeAsync(l2.Id, "ส่งลิงก์ผิดคน", admin);
            Assert.Equal(nameof(PaymentLinkState.Revoked), (await svc.ViewAsync(l2.Token, null))!.State);
            var revoked = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ResolveForPaymentAsync(l2.Token));
            Assert.Equal(410, revoked.StatusCode);

            // หมดอายุ
            var l3 = await svc.CreateAsync(invB.Id, 1, admin);
            var r3 = await db.PlatformPaymentLinks.FirstAsync(l => l.Id == l3.Id);
            r3.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            Assert.Equal(nameof(PaymentLinkState.Expired), (await svc.ViewAsync(l3.Token, null))!.State);
            Assert.Equal(0m, (await svc.ViewAsync(l3.Token, null))!.AmountDue);   // ลิงก์ที่ใช้ไม่ได้ไม่แสดงยอด/วิธีโอน

            // ใบที่ไม่มียอดค้าง ⇒ สร้างลิงก์ไม่ได้
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.CreateAsync(invA.Id, null, admin));
        });
    }
    // ═══════════════ ฝ่ายค้านรอบ 219 เฟส 2 (B2/N6/S1–S4/S6) ═══════════════

    private const string JuristicTaxId = "0105556000114";   // checksum ผ่าน · หลักแรก 0 = นิติบุคคล

    private static async Task<SubscriptionPayment> LinkedPaymentAsync(Accounting.Data.AccountingDbContext db, Fixture f, Guid invoiceId,
        decimal amount, int months, bool slip = true)
    {
        var p = new SubscriptionPayment
        {
            SubscriptionId = f.Sub.Id, PaymentNumber = "SP-AD-" + Guid.NewGuid().ToString("N")[..8], Amount = amount, PaymentDate = DateTime.UtcNow,
            RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = months,
            PlatformInvoiceDocumentId = invoiceId, Status = SubscriptionPaymentStatus.Pending, SlipFileName = slip ? "s.webp" : null,
        };
        db.SubscriptionPayments.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task Linked_payment_to_closed_or_superseded_invoice_is_held_or_refused_never_double_extends()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsurePricedTemplateAsync(db, SubscriptionPlan.Pro);
        var f = await SeedAsync(db);
        var renewal = await InvoiceAsync(db, f, PlatformInvoiceRules.RenewalReferencePrefix + "B2", 990m, f.Sub.EndDate);
        var voided = await InvoiceAsync(db, f, PlatformInvoiceRules.UsageReferencePrefix + "B2V", 300m, DateTime.UtcNow.AddDays(5), DocumentStatus.Voided);
        // ตัวชี้ใบต่ออายุว่าง (อัปเกรดอนุมัติไปก่อน — S9 ล้างแล้ว) ⇒ ใบต่ออายุนี้ "ถูกแทนที่"
        var endBefore = (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).EndDate;

        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var svc = new SubscriptionService(db, Notifications());
            // แจ้งชำระเองไม่ได้ (N6)
            var sup = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitInvoicePaymentAsync(f.BuyerId, renewal.Id,
                new SubmitInvoicePaymentRequest(null, null, null, null), "owner"));
            Assert.Equal("PLATFORM-RENEWAL-SUPERSEDED", sup.RuleCode);

            // รายการที่แจ้งไว้ก่อนถูกแทนที่ + เงินเข้าออนไลน์ ⇒ พัก (UnderReview + เรื่องค้าง) ไม่ต่ออายุ
            var y = await LinkedPaymentAsync(db, f, renewal.Id, 990m, 1);
            var held = await svc.ReviewPaymentAsync(y.Id, new ReviewSubscriptionPaymentRequest(true, null, null), "payment-gateway", systemConfirmed: true);
            Assert.Equal(SubscriptionPaymentStatus.UnderReview, held.Status);
            var yRow = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == y.Id);
            Assert.Equal(PlatformPaymentAttention.RuleHeld, yRow.AttentionRule);
            Assert.Equal(endBefore, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).EndDate);
            Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityId == y.Id.ToString() && a.NewValues!.Contains(PlatformPaymentAttention.RuleHeld)));

            // แอดมินอนุมัติ ⇒ ตัดหนี้อย่างเดียว (ไม่ต่ออายุซ้ำ) + เรื่องค้างเคลียร์
            var adminId = Guid.NewGuid().ToString();
            var ok = await svc.ReviewPaymentAsync(y.Id, new ReviewSubscriptionPaymentRequest(true, "ตรวจแล้ว", null), adminId);
            Assert.Equal(SubscriptionPaymentStatus.Approved, ok.Status);
            Assert.Equal(endBefore, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).EndDate);
            Assert.Null((await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == y.Id)).AttentionReason);

            // ใบถูกยกเลิก: เงินเข้าออนไลน์ ⇒ พัก · แอดมินกดอนุมัติ ⇒ 409 พร้อมทางไปต่อ
            var z = await LinkedPaymentAsync(db, f, voided.Id, 300m, 0);
            await svc.ReviewPaymentAsync(z.Id, new ReviewSubscriptionPaymentRequest(true, null, null), "payment-gateway", systemConfirmed: true);
            var refuse = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                svc.ReviewPaymentAsync(z.Id, new ReviewSubscriptionPaymentRequest(true, null, null), adminId));
            Assert.Equal(409, refuse.StatusCode);
            // ทิศตรงข้าม: ปฏิเสธได้เสมอ
            var rej = await svc.ReviewPaymentAsync(z.Id, new ReviewSubscriptionPaymentRequest(false, null, "ใบยกเลิกแล้ว — คืนเงิน"), adminId);
            Assert.Equal(SubscriptionPaymentStatus.Rejected, rej.Status);
        });
    }

    [Fact]
    public async Task Stale_renewal_with_pending_linked_payment_is_not_voided()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await SeedAsync(db);
        var r = await InvoiceAsync(db, f, PlatformInvoiceRules.RenewalReferencePrefix + "PV", 990m, f.Sub.EndDate);
        await LinkedPaymentAsync(db, f, r.Id, 990m, 1);
        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var voided = new List<Guid>();
            var docs = F4Proxy<IDocumentService>.Create((name, args) => { if (name == nameof(IDocumentService.VoidDocumentAsync)) voided.Add((Guid)args[1]!); return null; });
            var issuer = new PlatformBillingDocumentIssuer(db, new F4ServiceMap().Add(docs),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformBillingDocumentIssuer>.Instance);
            Assert.Equal(StaleRenewalOutcome.PendingPayment, await issuer.VoidStaleRenewalInvoiceAsync(r.Id));
            Assert.Empty(voided);
        });
    }

    [Fact]
    public async Task Pending_reuse_resyncs_amount_and_open_per_invoice_is_unique()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await SeedAsync(db);
        var inv = await InvoiceAsync(db, f, PlatformInvoiceRules.UsageReferencePrefix + "S3", 800m, DateTime.UtcNow.AddDays(5));
        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var svc = new SubscriptionService(db, Notifications());
            var p1 = await svc.SubmitInvoicePaymentAsync(f.BuyerId, inv.Id, new SubmitInvoicePaymentRequest(null, null, null, null), "owner");
            Assert.Equal(800m, p1.Amount);
            // ใบถูกตัดบางส่วนทางอื่น ⇒ ใช้รายการเดิมซ้ำด้วยยอดค้าง "ตอนนี้"
            var d = await db.Documents.FirstAsync(x => x.Id == inv.Id);
            d.BalanceDue = 500m; d.PaidAmount = 300m; d.Status = DocumentStatus.PartiallyPaid;
            await db.SaveChangesAsync();
            var p2 = await svc.SubmitInvoicePaymentAsync(f.BuyerId, inv.Id, new SubmitInvoicePaymentRequest(null, null, null, null), "owner");
            Assert.Equal(p1.Id, p2.Id);
            Assert.Equal(500m, p2.Amount);
            // ฐานกันรายการเปิดซ้อนบนใบเดียว (unique partial index)
            var dup = new SubscriptionPayment
            {
                SubscriptionId = f.Sub.Id, PaymentNumber = "SP-DUP-" + Guid.NewGuid().ToString("N")[..6], Amount = 500m, PaymentDate = DateTime.UtcNow,
                PlatformInvoiceDocumentId = inv.Id, Status = SubscriptionPaymentStatus.Pending,
            };
            db.SubscriptionPayments.Add(dup);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            db.Entry(dup).State = EntityState.Detached;
        });
    }

    [Fact]
    public async Task Juristic_tenant_withholding_reduces_transfer_individual_refused()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await SeedAsync(db);
        var other = await SeedAsync(db, f.PlatformId);
        var buyer = await db.Companies.FirstAsync(c => c.Id == f.BuyerId);
        buyer.TaxId = JuristicTaxId;
        var person = await db.Companies.FirstAsync(c => c.Id == other.BuyerId);
        person.TaxId = "3101000000013";   // บุคคลธรรมดา
        await db.SaveChangesAsync();
        var inv = await InvoiceAsync(db, f, PlatformInvoiceRules.UsageReferencePrefix + "W", 1070m, DateTime.UtcNow.AddDays(5));
        var invP = await InvoiceAsync(db, other, PlatformInvoiceRules.UsageReferencePrefix + "WP", 1070m, DateTime.UtcNow.AddDays(5));
        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var svc = new SubscriptionService(db, Notifications());
            var tooHigh = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitInvoicePaymentAsync(f.BuyerId, inv.Id,
                new SubmitInvoicePaymentRequest(null, null, null, null, 100m), "owner"));
            Assert.Equal("PLATFORM-WHT-INVALID", tooHigh.RuleCode);
            await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitInvoicePaymentAsync(other.BuyerId, invP.Id,
                new SubmitInvoicePaymentRequest(null, null, null, null, 30m), "owner"));
            var pay = await svc.SubmitInvoicePaymentAsync(f.BuyerId, inv.Id, new SubmitInvoicePaymentRequest(null, null, null, null, 30m), "owner");
            Assert.Equal(1040m, pay.Amount);   // โอน + ภาษีหัก = ยอดค้าง
            Assert.Equal(30m, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).WithholdingTaxAmount);
            // ทิศตรงข้าม: บุคคลธรรมดาไม่หัก ⇒ จ่ายเต็มได้
            var full = await svc.SubmitInvoicePaymentAsync(other.BuyerId, invP.Id, new SubmitInvoicePaymentRequest(null, null, null, null), "owner");
            Assert.Equal(1070m, full.Amount);
        });
    }

    [Fact]
    public async Task Payment_approval_keeps_admin_suspension_but_lifts_expiry_suspension()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await EnsurePricedTemplateAsync(db, SubscriptionPlan.Pro);
        var admin = await SeedAsync(db, status: SubscriptionStatus.Suspended);
        var expired = await SeedAsync(db, admin.PlatformId, status: SubscriptionStatus.Suspended);
        db.SubscriptionHistories.Add(new SubscriptionHistory { SubscriptionId = admin.Sub.Id, Action = "AdminStatusChanged",
            FromStatus = SubscriptionStatus.Active, ToStatus = SubscriptionStatus.Suspended, PerformedBy = "admin" });
        db.SubscriptionHistories.Add(new SubscriptionHistory { SubscriptionId = expired.Sub.Id, Action = PlatformDunningPolicy.ExpirySuspendAction,
            FromStatus = SubscriptionStatus.PastDue, ToStatus = SubscriptionStatus.Suspended, PerformedBy = "System" });
        await db.SaveChangesAsync();
        var svc = new SubscriptionService(db, Notifications());
        var endBefore = admin.Sub.EndDate;
        foreach (var fx in new[] { admin, expired })
        {
            var p = new SubscriptionPayment
            {
                SubscriptionId = fx.Sub.Id, PaymentNumber = "SP-S6-" + Guid.NewGuid().ToString("N")[..6], Amount = 990m, PaymentDate = DateTime.UtcNow,
                RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1, Status = SubscriptionPaymentStatus.Pending,
            };
            db.SubscriptionPayments.Add(p);
            await db.SaveChangesAsync();
            await svc.ReviewPaymentAsync(p.Id, new ReviewSubscriptionPaymentRequest(true, null, null), Guid.NewGuid().ToString());
        }
        var a = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == admin.Sub.Id);
        Assert.Equal(SubscriptionStatus.Suspended, a.Status);                // การจ่ายไม่ปลดคำสั่งระงับของผู้ดูแลระบบ
        Assert.True(a.EndDate > endBefore);                                  // แต่ต่ออายุให้ตามเงินที่จ่าย
        Assert.Equal(SubscriptionStatus.Active, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == expired.Sub.Id)).Status);
    }

    [Fact]
    public async Task Settle_issues_real_receipt_caps_overpayment_and_failure_is_loud_and_not_dunned()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await SeedAsync(db);
        var inv = await InvoiceAsync(db, f, PlatformInvoiceRules.UsageReferencePrefix + "S1", 500m, DateTime.UtcNow.AddDays(-30));
        var inv2 = await InvoiceAsync(db, f, PlatformInvoiceRules.UsageReferencePrefix + "S2", 300m, DateTime.UtcNow.AddDays(-30));
        var receipt = new Document
        {
            CompanyId = f.PlatformId, ContactId = f.ContactId, DocumentType = DocumentType.Receipt, DocumentNumber = "RE-T" + Guid.NewGuid().ToString("N")[..6],
            Status = DocumentStatus.Approved, DocumentDate = DateTime.UtcNow, IsTaxInvoiceByLaw = true, Currency = "THB",
        };
        db.Documents.Add(receipt);
        await db.SaveChangesAsync();

        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            var calls = new List<Accounting.Models.DTOs.Document.CreatePaymentRequest>();
            var docs = F4Proxy<IDocumentService>.Create((name, args) =>
            {
                if (name != nameof(IDocumentService.CreatePaymentAsync)) return null;
                var req = (Accounting.Models.DTOs.Document.CreatePaymentRequest)args[1]!;
                calls.Add(req);
                if (req.DocumentId == inv2.Id) throw new InvalidOperationException("จำลองบันทึกรับเงินล้ม");
                return new Accounting.Models.DTOs.Document.PaymentResponse(Guid.NewGuid(), "RV-1", req.DocumentId, req.PaymentDate, req.Amount,
                    req.PaymentMethod, req.Reference, null, null, null, DateTime.UtcNow, ReceiptDocumentId: receipt.Id, ReceiptDocumentNumber: receipt.DocumentNumber);
            });
            var issuer = new PlatformBillingDocumentIssuer(db, new F4ServiceMap().Add(docs),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformBillingDocumentIssuer>.Instance);
            var billing = new SaasBillingDocumentService(db, F4Proxy<IPdfGenerationService>.Create((_, _) => null),
                F4Proxy<IFileAttachmentService>.Create((_, _) => null),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SaasBillingDocumentService>.Instance, issuer: issuer);

            // รับเกิน 100 ⇒ ตัดเท่ายอดค้าง + ใบเสร็จจริง (ธงใบกำกับ) + เรื่องค้าง "รับเกิน"
            var p = await LinkedPaymentAsync(db, f, inv.Id, 600m, 0);
            p.Status = SubscriptionPaymentStatus.Approved; await db.SaveChangesAsync();
            await billing.SettlePlatformInvoiceAsync(p.Id);
            var c = Assert.Single(calls);
            Assert.Equal(500m, c.Amount);
            Assert.True(c.IssueReceiptDocument);
            var after = await db.SubscriptionPayments.AsNoTracking().SingleAsync(x => x.Id == p.Id);
            Assert.Equal(receipt.Id, after.PlatformDocumentId);
            Assert.Equal(receipt.DocumentNumber, after.ReceiptNumber);    // ใบเสร็จ ไม่ใช่เลขใบแจ้งหนี้
            Assert.True(after.ReceiptIsTaxInvoice);
            Assert.Equal(PlatformPaymentAttention.RuleOverpaid, after.AttentionRule);

            // ตัดชำระล้ม ⇒ เรื่องค้าง + audit (ดัง) · งานทวงหนี้ไม่ระงับเพราะใบนี้ (ลูกค้าจ่ายแล้ว)
            var q = await LinkedPaymentAsync(db, f, inv2.Id, 300m, 0);
            q.Status = SubscriptionPaymentStatus.Approved; await db.SaveChangesAsync();
            await billing.SettlePlatformInvoiceAsync(q.Id);
            var qa = await db.SubscriptionPayments.AsNoTracking().SingleAsync(x => x.Id == q.Id);
            Assert.Null(qa.PlatformDocumentId);
            Assert.Equal(PlatformPaymentAttention.RuleSettleFailed, qa.AttentionRule);
            Assert.True(await db.AuditLogs.AnyAsync(a => a.EntityId == q.Id.ToString() && a.NewValues!.Contains(PlatformPaymentAttention.RuleSettleFailed)));
            var d1 = await db.Documents.FirstAsync(x => x.Id == inv.Id);   // ใบแรกจำลองว่าชำระครบแล้ว (proxy ไม่ได้แตะยอด)
            d1.BalanceDue = 0m; d1.Status = DocumentStatus.Paid; await db.SaveChangesAsync();
            var dunning = new PlatformDunningService(db, new CountingNotifier(), Gate(SubscriptionEnforcementMode.Enforce), EmptyConfig(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlatformDunningService>.Instance);
            Assert.Equal(DunningAction.None, (await dunning.EvaluateCompanyAsync(f.BuyerId, false))!.Action);
        });
    }
    [Fact]
    public async Task Addon_switch_from_unpaid_upfront_to_monthly_invoice_charges_current_period_no_free_month()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, code) = await SeedPaidAddOnAsync(db);
        var metering = new UsageMeteringService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageMeteringService>.Instance);
        var period = AddOnBilling.PeriodOf(DateTime.UtcNow);
        // ลำดับจริงที่ฝ่ายค้านพบ: จ่ายล่วงหน้า (ยังไม่จ่าย) → งานรายเดือนออกค่าเหมา ฿0 → ปิด → เปิดใหม่แบบรวมใบรายเดือน
        await metering.SetFeatureEnabledAsync(cid, code, true, "owner", AddOnBillingChoice.Upfront);
        Assert.Equal(0m, (await metering.RecordFlatMonthlyAsync(cid, code, period)).ChargedAmount);
        await metering.SetFeatureEnabledAsync(cid, code, false, "owner", (AddOnBillingChoice?)null);
        await metering.SetFeatureEnabledAsync(cid, code, true, "owner", AddOnBillingChoice.MonthlyInvoice);
        var charged = await db.UsageEvents.AsNoTracking()
            .Where(u => u.CompanyId == cid && u.FeatureCode == code).SumAsync(u => u.ChargedAmount);
        Assert.Equal(299m, charged);   // เก็บงวดนี้ตามจริง (เดิม ฿0 = ฟรีหนึ่งเดือน)
        // งานรายเดือนรอบถัดไปของงวดเดียวกันไม่เก็บซ้ำ
        await metering.RecordFlatMonthlyAsync(cid, code, period);
        Assert.Equal(299m, await db.UsageEvents.AsNoTracking().Where(u => u.CompanyId == cid && u.FeatureCode == code).SumAsync(u => u.ChargedAmount));
    }
}
