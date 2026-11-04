using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ฝ่ายค้านรอบ 229 (S9 · B2 · S1 · S2 · S8 · S10) บน PostgreSQL จริง — เส้นเงินของโควตาเสริม:
/// เงินเข้าซ้ำ (webhook ซ้ำ) · สลิปรอตรวจแต่จ่ายออนไลน์ · เงินขาด · จ่ายซ้อนหลังเปิดแล้ว · จ่ายช้า (กำหนดวัน = นับจากวันจ่าย · รายเดือนข้ามเดือน = QUOTA-STALE ไม่เปิด) ·
/// อนุมัติไม่มีสลิปต้องมีหลักฐาน · ยกเลิกต่ออายุหลังออกใบ = 409 · ต่ออายุจากใบซ้ำ = รอบใหม่ครั้งเดียว · เพิกถอน · ที่นั่งสองทิศ
/// </summary>
[Trait("Category", "Db")]
public class QuotaAddOnPaymentDbTests
{
    private readonly ITestOutputHelper _out;
    public QuotaAddOnPaymentDbTests(ITestOutputHelper output) => _out = output;

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private sealed class Sp : IServiceProvider
    {
        private readonly ISubscriptionService _subs;
        public Sp(ISubscriptionService subs) => _subs = subs;
        public object? GetService(Type t) => t == typeof(ISubscriptionService) ? _subs : null;
    }

    private static QuotaAddOnPurchaseService Svc(Accounting.Data.AccountingDbContext db)
    {
        var subs = new SubscriptionService(db, Notifications());
        return new QuotaAddOnPurchaseService(db, subs, new Sp(subs), NullLogger<QuotaAddOnPurchaseService>.Instance);
    }

    private static async Task<Guid> SeedCompanyAsync(Accounting.Data.AccountingDbContext db)
    {
        var c = new Company { Name = "บริษัททดสอบเงินโควตาเสริม " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000004" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        db.Subscriptions.Add(new Subscription
        {
            CompanyId = c.Id, Plan = SubscriptionPlan.Pro, Status = SubscriptionStatus.Active,
            StartDate = DateTime.UtcNow.AddDays(-10), EndDate = DateTime.UtcNow.AddDays(200), UsageResetDate = DateTime.UtcNow.AddDays(20),
        });
        await db.SaveChangesAsync();
        return c.Id;
    }

    private static async Task<CompanyQuotaAddOn> RowAsync(Accounting.Data.AccountingDbContext db, Guid cid, QuotaAddOnMode mode,
        DateTime created, DateTime? end, QuotaAddOnStatus status = QuotaAddOnStatus.AwaitingPayment, decimal due = 199m)
    {
        var r = new CompanyQuotaAddOn
        {
            CompanyId = cid, OfferId = Guid.NewGuid(), ResourceKey = QuotaResources.CmsSites, Units = 1, UnitSize = 1, Mode = mode,
            PeriodStartUtc = created, PeriodEndUtc = end, Status = status, AmountDue = due, UnitPriceSnapshot = due, MonthlyUnitPriceSnapshot = 199m,
            CreatedAt = created,
        };
        db.CompanyQuotaAddOns.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    private static async Task<CompanyQuotaAddOn> ReloadAsync(Guid id)
    {
        await using var db = DbTestDatabase.TryCreateContext()!;
        return await db.CompanyQuotaAddOns.AsNoTracking().FirstAsync(x => x.Id == id);
    }

    [Fact]
    public async Task เงินเข้าซ้ำ_สลิปรอตรวจแต่จ่ายออนไลน์_เงินขาด_จ่ายซ้อนหลังเปิดแล้ว()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var cid = await SeedCompanyAsync(db);
        var now = DateTime.UtcNow;

        // webhook ซ้ำ (intent เดียวกันสองครั้ง) ⇒ เปิดครั้งเดียว ช่วงสิทธิ์ไม่ขยับ
        var a = await RowAsync(db, cid, QuotaAddOnMode.OneOffMonth, now.AddMinutes(-5), now.AddDays(20));
        var intent = Guid.NewGuid();
        await Svc(db).MarkPaidByGatewayAsync(cid, a.Id, intent, 199m, "test");
        var first = await ReloadAsync(a.Id);
        Assert.Equal(QuotaAddOnStatus.Active, first.Status);
        Assert.Equal(199m, first.ReceivedAmount);
        await Svc(db).MarkPaidByGatewayAsync(cid, a.Id, intent, 199m, "test");
        Assert.Equal(first.PeriodStartUtc, (await ReloadAsync(a.Id)).PeriodStartUtc);

        // จ่ายซ้อนด้วย intent อื่นหลังเปิดแล้ว ⇒ ล้มดัง (ไม่เปิดซ้ำ — แอดมินคืนเงิน)
        var dup = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).MarkPaidByGatewayAsync(cid, a.Id, Guid.NewGuid(), 199m, "test"));
        Assert.Equal(PlatformDoublePayment.RuleCode, dup.RuleCode);

        // สลิปรอตรวจอยู่แต่เงินออนไลน์เข้า ⇒ เปิดตามเงินจริง + audit ให้แอดมินปฏิเสธสลิป/คืนเงินโอน
        var b = await RowAsync(db, cid, QuotaAddOnMode.OneOffMonth, now.AddMinutes(-5), now.AddDays(20), QuotaAddOnStatus.PendingReview);
        await Svc(db).MarkPaidByGatewayAsync(cid, b.Id, Guid.NewGuid(), 199m, "test");
        Assert.Equal(QuotaAddOnStatus.Active, (await ReloadAsync(b.Id)).Status);
        Assert.True(await db.AuditLogs.AnyAsync(x => x.CompanyId == cid && x.EntityId == b.Id.ToString() && x.NewValues!.Contains(PlatformDoublePayment.AuditAction)));

        // เงินขาด ⇒ ไม่เปิด
        var c = await RowAsync(db, cid, QuotaAddOnMode.OneOffMonth, now.AddMinutes(-5), now.AddDays(20));
        var under = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).MarkPaidByGatewayAsync(cid, c.Id, Guid.NewGuid(), 100m, "test"));
        Assert.Equal("QUOTA-UNDERPAID", under.RuleCode);
        Assert.Equal(QuotaAddOnStatus.AwaitingPayment, (await ReloadAsync(c.Id)).Status);
    }

    [Fact]
    public async Task จ่ายช้า_กำหนดวันนับจากวันจ่าย_รายเดือนข้ามเดือนไม่เปิดและประทับQUOTA_STALE_รายเดือนเดือนเดียวกันประทับงวดถูก()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db);
        var now = DateTime.UtcNow;

        // เดือนเดียวแบบ 30 วัน คิดราคาไว้ 10 วันก่อน ⇒ ช่วงสิทธิ์ใหม่ = วันจ่าย + 30 วัน (ลูกค้าไม่เสียวัน)
        var fixedDays = await RowAsync(db, cid, QuotaAddOnMode.OneOffMonth, now.AddDays(-10), now.AddDays(20));
        await Svc(db).MarkPaidByGatewayAsync(cid, fixedDays.Id, Guid.NewGuid(), 199m, "test");
        var f = await ReloadAsync(fixedDays.Id);
        Assert.Equal(QuotaAddOnStatus.Active, f.Status);
        Assert.True(f.PeriodEndUtc > now.AddDays(29));

        // รายเดือนคิดเดือนก่อน ⇒ ไม่เปิดสิทธิ์ช่วงที่ใช้ไม่ได้ · ประทับเรื่องค้าง + เงินที่รับไว้ · ล้มดัง
        var lastMonth = QuotaAddOnPricing.NextThaiMonthStartUtc(now).AddMonths(-1).AddDays(-3);
        var rec = await RowAsync(db, cid, QuotaAddOnMode.RecurringMonthly, lastMonth, null);
        var stale = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).MarkPaidByGatewayAsync(cid, rec.Id, Guid.NewGuid(), 199m, "test"));
        Assert.Equal(QuotaAddOnPurchaseService.StaleRuleCode, stale.RuleCode);
        var r = await ReloadAsync(rec.Id);
        Assert.NotEqual(QuotaAddOnStatus.Active, r.Status);
        Assert.Equal("QUOTA-STALE", r.AttentionRule);
        Assert.Equal(199m, r.ReceivedAmount);

        // เริ่มชำระ/แนบสลิปของรายการล้าสมัย ⇒ 409 สร้างรายการใหม่ (ก่อนแตะช่องทางชำระ)
        var rec2 = await RowAsync(db, cid, QuotaAddOnMode.RecurringMonthly, lastMonth, null);
        var refuse = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).StartOnlineAsync(cid, rec2.Id, PaymentMethodKind.PromptPay, null, null));
        Assert.Equal(409, refuse.StatusCode);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).UploadSlipAsync(cid, rec2.Id, "x.png", null, "test"));

        // รายเดือนเดือนเดียวกัน ⇒ เปิด + เดือนแรก = เดือนที่คิดราคา
        var ok = await RowAsync(db, cid, QuotaAddOnMode.RecurringMonthly, now.AddMinutes(-1), null, due: 106.13m);
        await Svc(db).MarkPaidByGatewayAsync(cid, ok.Id, Guid.NewGuid(), 106.13m, "test");
        var o = await ReloadAsync(ok.Id);
        Assert.Equal(QuotaAddOnStatus.Active, o.Status);
        Assert.Equal(AddOnBilling.PeriodOf(o.CreatedAt), o.LastBilledPeriod);
    }

    [Fact]
    public async Task อนุมัติไม่มีสลิปต้องมีหลักฐาน_ยกเลิกต่ออายุหลังออกใบ409_ต่ออายุจากใบซ้ำได้รอบเดียว_เพิกถอน()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db);
        var now = DateTime.UtcNow;

        var a = await RowAsync(db, cid, QuotaAddOnMode.OneOffMonth, now.AddMinutes(-5), now.AddDays(20));
        var ev = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).ApproveAsync(cid, a.Id, "admin"));
        Assert.Equal("QUOTA-APPROVE-EVIDENCE", ev.RuleCode);
        await Svc(db).ApproveAsync(cid, a.Id, "admin", default, "TRX-123", "โอนเข้าบัญชีบริษัท ตรวจแล้ว", 199m);
        var ap = await ReloadAsync(a.Id);
        Assert.Equal(QuotaAddOnStatus.Active, ap.Status);
        Assert.Equal("TRX-123", ap.OfflineReference);

        // S1: อยู่ในใบต่ออายุแล้ว ⇒ เอาออกจากการต่อไม่ได้ (409) — ไม่ใช่ no-op เงียบ
        var sub = await db.Subscriptions.FirstAsync(s => s.CompanyId == cid);
        var invoiceId = Guid.NewGuid();
        var co = await RowAsync(db, cid, QuotaAddOnMode.Coterm, now.AddDays(-5), sub.EndDate, QuotaAddOnStatus.Active);
        co.RenewalInvoiceDocumentId = invoiceId; co.RenewUnitPrice = 1990m; co.RenewUnits = 2;
        // รอบ 230: ล็อกเฉพาะใบที่มีการแจ้งชำระ/ชำระแล้ว (ใบที่ยังไม่มีใครจ่าย ⇒ ออกใบใหม่ — เทสต์ทิศตรงข้ามอยู่ใน PlatformCouponProrationDbTests)
        db.SubscriptionPayments.Add(new SubscriptionPayment
        {
            SubscriptionId = sub.Id, PaymentNumber = "SP-T230-" + Guid.NewGuid().ToString("N")[..8], Amount = 3980m, PaymentDate = now,
            RequestedPlan = sub.Plan, RequestedBillingCycle = sub.BillingCycle, Status = SubscriptionPaymentStatus.Pending, PlatformInvoiceDocumentId = invoiceId,
            SlipFileName = "slip.jpg",
        });
        await db.SaveChangesAsync();
        var locked = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).CancelAsync(cid, co.Id, "owner"));
        Assert.Equal("QUOTA-RENEW-LOCKED", locked.RuleCode);

        // ตัดชำระใบเดียวกันซ้ำ ⇒ รอบใหม่ครั้งเดียว (ทั้งแถวที่บันทึกแล้วและที่ค้างใน context)
        var newEnd = sub.EndDate.AddYears(1);
        Assert.Equal(1, await QuotaRenewalLines.ActivateForInvoiceAsync(db, cid, invoiceId, newEnd, "t", save: false));
        Assert.Equal(0, await QuotaRenewalLines.ActivateForInvoiceAsync(db, cid, invoiceId, newEnd, "t", save: false));
        await db.SaveChangesAsync();
        Assert.Equal(0, await QuotaRenewalLines.ActivateForInvoiceAsync(db, cid, invoiceId, newEnd, "t"));
        var renewed = await db.CompanyQuotaAddOns.AsNoTracking().SingleAsync(x => x.RenewedFromId == co.Id);
        Assert.Equal(2, renewed.Units);
        Assert.Equal(3980m, renewed.AmountDue);
        // PostgreSQL เก็บความละเอียดไมโครวินาที (และไม่คง DateTimeKind) ⇒ เทียบด้วยค่าคลาดเคลื่อน < 1 ms ไม่ใช่เท่ากันเป๊ะ
        Assert.True(Math.Abs((co.PeriodEndUtc!.Value - renewed.PeriodStartUtc).TotalMilliseconds) < 1,
            $"รอบใหม่ต้องเริ่มที่วันสิ้นสุดรอบเดิม: {co.PeriodEndUtc:O} vs {renewed.PeriodStartUtc:O}");

        // S8: เพิกถอน ⇒ ไม่นับในเพดานทันที · แถวยังอยู่ (ไม่ลบข้อมูล) · ต้องมีเหตุผล
        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).RevokeAsync(cid, a.Id, "", "admin"));
        await Svc(db).RevokeAsync(cid, a.Id, "คืนเงินแล้ว", "admin");
        var rv = await ReloadAsync(a.Id);
        Assert.Equal(QuotaAddOnStatus.Cancelled, rv.Status);
        Assert.False(QuotaLimitPolicy.IsLive(rv.Status, rv.PeriodStartUtc, rv.PeriodEndUtc, DateTime.UtcNow));
    }

    [Fact]
    public async Task ที่นั่ง_ใช้ไปNลบหนึ่งรวมคำเชิญค้างยังเชิญได้_ครบNถูกบล็อก()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db);
        var owner = new User { Email = $"owner-{Guid.NewGuid():N}@test.local", FullName = "Owner", PasswordHash = "x" };
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        db.CompanyUsers.Add(new CompanyUser { CompanyId = cid, UserId = owner.Id, Role = UserRole.Owner });
        db.CompanyInvitations.Add(new CompanyInvitation
        {
            CompanyId = cid, Email = $"p-{Guid.NewGuid():N}@test.local", Role = UserRole.Viewer, InvitedByUserId = owner.Id,
            Token = Guid.NewGuid().ToString("N"), Status = InvitationStatus.Pending, ExpiresAt = DateTime.UtcNow.AddDays(7),
        });
        db.CompanyQuotaOverrides.Add(new CompanyQuotaOverride { CompanyId = cid, ResourceKey = QuotaResources.Users, Limit = 3, Reason = "test" });
        await db.SaveChangesAsync();

        CompanyService Company(Accounting.Data.AccountingDbContext d)
        {
            var subs = new SubscriptionService(d, Notifications());
            return new CompanyService(d, null!, subscriptionService: subs, quotaGate: new QuotaGateService(d, new Sp(subs)));
        }
        // ใช้ไป 2/3 (เจ้าของ + คำเชิญค้าง) ⇒ เชิญเพิ่มได้ (ทิศตรงข้าม — ห้ามบล็อกก่อนเต็ม)
        await Company(db).AddUserAsync(cid, owner.Id, new Accounting.Models.DTOs.Company.AddCompanyUserRequest($"n-{Guid.NewGuid():N}@test.local", UserRole.Viewer));
        // ครบ 3/3 ⇒ บล็อกพร้อมทางไปต่อ
        var full = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Company(db).AddUserAsync(cid, owner.Id, new Accounting.Models.DTOs.Company.AddCompanyUserRequest($"m-{Guid.NewGuid():N}@test.local", UserRole.Viewer)));
        Assert.Equal("QUOTA-FULL:users", full.RuleCode);
    }

    /// <summary>รอบ 235 (ฝ่ายค้าน S-3): รายการโควตาเสริมที่หัก ณ ที่จ่ายแต่ผู้จ่ายหมดสิทธิ์หัก (ไม่ใช่นิติบุคคลแล้ว) — สลิป = ปฏิเสธพร้อมทางไปต่อ ·
    /// เงินเข้า = พักให้แอดมิน (เรื่องค้าง · ไม่เปิดสิทธิ์) · แอดมินอนุมัติโดยไม่ล้างภาษีหัก = บอกเหตุ · ล้างเป็น 0 + รับเต็มยอด = ผ่าน ·
    /// ทิศตรงข้าม: รายการที่ไม่ได้หักของผู้จ่ายเดียวกันแนบสลิปได้ตามเดิม</summary>
    [Fact]
    public async Task R235_หักณที่จ่ายแต่หมดสิทธิ์_สลิปปฏิเสธ_เงินเข้าพัก_แอดมินล้างภาษีหักแล้วรับเต็ม()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var cid = await SeedCompanyAsync(db);
        var co = await db.Companies.FirstAsync(c => c.Id == cid);
        co.TaxId = null;   // ผู้จ่ายไม่ใช่นิติบุคคลแล้ว
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        var wht = await RowAsync(db, cid, QuotaAddOnMode.OneOffMonth, now.AddMinutes(-5), now.AddDays(20), due: 193m);
        wht.WithholdingTaxAmount = 6m;
        await db.SaveChangesAsync();

        var slip = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).UploadSlipAsync(cid, wht.Id, "uploads/slips/r235.png", null, "owner"));
        Assert.Equal(PlatformPayerProfileGate.RuleCode, slip.RuleCode);
        Assert.Contains("ซื้อใหม่โดยไม่ติ๊กหัก", slip.Message);

        await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).MarkPaidByGatewayAsync(cid, wht.Id, Guid.NewGuid(), 193m, "test"));
        var held = await ReloadAsync(wht.Id);
        Assert.NotEqual(QuotaAddOnStatus.Active, held.Status);
        Assert.Equal(PlatformPayerProfileGate.RuleCode, held.AttentionRule);

        var refused = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(db).ApproveAsync(cid, wht.Id, "admin", default, "BANK-REF-235", "รับโอนนอกระบบ", 193m));
        Assert.Contains("ตั้งภาษีหัก ณ ที่จ่ายเป็น 0", refused.Message);
        await Svc(db).ApproveAsync(cid, wht.Id, "admin", default, "BANK-REF-235", "รับโอนนอกระบบ", 199m, null, withholdingTaxAmount: 0m);
        var ok = await ReloadAsync(wht.Id);
        Assert.Equal(QuotaAddOnStatus.Active, ok.Status);
        Assert.Equal(0m, ok.WithholdingTaxAmount);
        Assert.Equal(199m, ok.AmountDue);   // ยอดเต็มคงที่ (สุทธิ 193 + ภาษีหัก 6)

        // ทิศตรงข้าม: ไม่ได้หัก ⇒ แนบสลิปได้ตามเดิม
        var plain = await RowAsync(db, cid, QuotaAddOnMode.OneOffMonth, now.AddMinutes(-5), now.AddDays(20));
        await Svc(db).UploadSlipAsync(cid, plain.Id, "uploads/slips/r235b.png", null, "owner");
        Assert.Equal(QuotaAddOnStatus.PendingReview, (await ReloadAsync(plain.Id)).Status);
    }
}
