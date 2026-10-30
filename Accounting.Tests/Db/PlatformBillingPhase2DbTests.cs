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

    // ═══════════════ ขั้น 3: ยกเลิกเมื่อสิ้นรอบ · วิธีชำระส่วนเสริม · ใบต่ออายุที่ล้าสมัย (S9) ═══════════════

    [Fact]
    public async Task Cancel_keeps_access_to_period_end_reactivate_and_expiry_cancels_without_renewal()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await SeedAsync(db);
        var renewal = await InvoiceAsync(db, f, PlatformInvoiceRules.RenewalReferencePrefix + "C", 990m, f.Sub.EndDate);
        var sub = await db.Subscriptions.FirstAsync(s => s.Id == f.Sub.Id);
        sub.RenewalInvoiceDocumentId = renewal.Id; sub.RenewalInvoiceForEndDate = sub.EndDate; sub.RenewalInvoiceNumber = renewal.DocumentNumber;
        await db.SaveChangesAsync();
        var svc = new SubscriptionService(db, Notifications());

        var msg = await svc.CancelSubscriptionAsync(f.BuyerId, "owner");
        Assert.Contains("ใช้งานได้ถึง", msg);
        var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id);
        Assert.Equal(SubscriptionStatus.Active, after.Status);   // ใช้ได้ถึงสิ้นรอบ (ไม่ตัดทันที)
        Assert.True(after.CancelAtPeriodEnd);

        await WithPlatformTenantAsync(db, f.PlatformId, async () =>
        {
            // ใบต่ออายุของรอบที่ยกเลิกไว้ ⇒ จ่ายไม่ได้ (ทางไปต่อ = ใช้ต่อ)
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.SubmitInvoicePaymentAsync(f.BuyerId, renewal.Id,
                new SubmitInvoicePaymentRequest(null, null, null, null), Accept(), "owner"));
            Assert.Equal("PLATFORM-RENEWAL-CANCELLED", ex.RuleCode);
        });

        // ใช้ต่อ ⇒ ล้างการยกเลิก · ใช้ต่อซ้ำ ⇒ 409
        await svc.ReactivateSubscriptionAsync(f.BuyerId, "owner");
        Assert.False((await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).CancelAtPeriodEnd);
        await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ReactivateSubscriptionAsync(f.BuyerId, "owner"));

        // ยกเลิกอีกครั้งแล้วถึงวันครบรอบ ⇒ Cancelled (ไม่ใช่ PastDue)
        await svc.CancelSubscriptionAsync(f.BuyerId, "owner");
        var s2 = await db.Subscriptions.FirstAsync(s => s.Id == f.Sub.Id);
        s2.EndDate = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        await svc.ProcessExpiredSubscriptionsAsync();
        Assert.Equal(SubscriptionStatus.Cancelled, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).Status);
    }

    [Fact]
    public async Task Trial_cancel_is_immediate()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var f = await SeedAsync(db, status: SubscriptionStatus.Trial, plan: SubscriptionPlan.FreeTrial);
        await new SubscriptionService(db, Notifications()).CancelSubscriptionAsync(f.BuyerId, "owner");
        Assert.Equal(SubscriptionStatus.Cancelled, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == f.Sub.Id)).Status);
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
    public async Task Addon_purchase_requires_choice_and_terms_monthly_invoice_skips_upfront()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, code) = await SeedPaidAddOnAsync(db);
        var metering = new UsageMeteringService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageMeteringService>.Instance);

        // ไม่เลือกวิธีชำระ ⇒ 400 · ไม่ติ๊ก ⇒ 400 · ไม่มีแถวเปิดใช้
        var noChoice = await Assert.ThrowsAsync<BusinessRuleException>(() => metering.SetFeatureEnabledAsync(cid, code, true, "owner", null, Accept()));
        Assert.Equal("ADDON-CHOICE-REQUIRED", noChoice.RuleCode);
        await Assert.ThrowsAsync<BusinessRuleException>(() => metering.SetFeatureEnabledAsync(cid, code, true, "owner", AddOnBillingChoice.MonthlyInvoice, Accept(false)));
        Assert.False(await db.CompanyFeatures.AnyAsync(x => x.CompanyId == cid && x.FeatureCode == code && x.IsEnabled));

        // รวมใบรายเดือน ⇒ ใช้ได้ทันที · ไม่มีงวดจ่ายล่วงหน้า · หลักฐานการยอมรับ
        await metering.SetFeatureEnabledAsync(cid, code, true, "owner", AddOnBillingChoice.MonthlyInvoice, Accept());
        var row = await db.CompanyFeatures.AsNoTracking().SingleAsync(x => x.CompanyId == cid && x.FeatureCode == code);
        Assert.Equal(AddOnPaymentStatus.NotRequired, row.PaymentStatus);
        Assert.Null(row.UpfrontPeriod);
        Assert.Equal(AddOnBillingChoice.MonthlyInvoice, row.BillingChoice);
        Assert.True(await db.PlatformPurchaseAcceptances.AnyAsync(a => a.SubjectId == row.Id && a.SubjectKind == PlatformPurchaseTerms.KindAddOn));
        Assert.True(await metering.IsFeatureEnabledAsync(cid, code));
        // ค่าเหมาเดือนนี้เข้าใบรายเดือนเต็ม (ไม่ ฿0 — ไม่ได้จ่ายล่วงหน้า)
        var period = AddOnBilling.PeriodOf(DateTime.UtcNow);
        var charged = await metering.RecordFlatMonthlyAsync(cid, code, period);
        Assert.Equal(299m, charged.ChargedAmount);

        // ปิดแล้วเปิดใหม่เลือกจ่ายล่วงหน้างวดเดียวกัน ⇒ 409 (เก็บซ้ำ)
        await metering.SetFeatureEnabledAsync(cid, code, false, "owner", null, null);
        var dup = await Assert.ThrowsAsync<BusinessRuleException>(() => metering.SetFeatureEnabledAsync(cid, code, true, "owner", AddOnBillingChoice.Upfront, Accept()));
        Assert.Equal(409, dup.StatusCode);
    }

    [Fact]
    public async Task Addon_upfront_choice_keeps_phase0_upfront_flow()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, code) = await SeedPaidAddOnAsync(db);
        var metering = new UsageMeteringService(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<UsageMeteringService>.Instance);
        await metering.SetFeatureEnabledAsync(cid, code, true, "owner", AddOnBillingChoice.Upfront, Accept());
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
}
