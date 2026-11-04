using Accounting.Data;
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
/// รอบ 237 (K-1 · DECISIONS 237-1…237-7) บน PostgreSQL จริง — สร้างรายการ → อนุมัติ → วันหมดอายุที่ประทับตามแบบที่ลูกค้าเลือก:
/// แบบ ข เริ่มรอบใหม่ (IsCycleChange + UpgradeMode=RestartCycle · ยอด = ตัวตัดสินเดียว) → เงินเข้า ⇒ แพ็กใหม่ + EndDate = วันชำระ + 12 เดือน
/// <b>แม้แอดมินเปลี่ยนค่าตั้งเป็น "คงวันหมดอายุเท่านั้น" ระหว่างรอเงินเข้า</b> (237-5) · แบบ ก คงวันหมดอายุ (ทิศตรงข้าม) ⇒ EndDate เดิม ·
/// ค่าตั้งไม่เปิดแบบ ข ⇒ ปฏิเสธ ไม่สร้างรายการ · ใบเสนอได้ตัวเลือก 2 แบบ ยอดตรงกับตอนสร้างรายการ
/// <para>ค่าตั้งเป็นแถวเดียวของแพลตฟอร์ม (ฐานร่วม) — เทสต์นี้ใช้เฉพาะ CustomerChooses (ค่าตั้งต้น) กับ KeepExpiryOnly (ค่าเลือกให้ = แบบ ก เหมือนค่าตั้งต้น)
/// และคืนค่าเดิมเสมอ · ฝ่ายค้าน N5: อยู่ใน collection "SubscriptionExpirySweep" (ตัวเดียวกับเทสต์ค่าบริการ/หน้าซื้อที่แก้ <c>SiteSettings</c>) ⇒ ไม่รันขนานกัน</para>
/// <para>ฝ่ายค้าน 237: S1 License ขยับตามแบบ ข (แพ็กตรง/ไม่ตรง) · S2 เครดิต ≥ ราคาเต็มที่ทางสร้างรายการ · S3/237-8 เครดิตไม่ตรงวันชำระ ⇒ พัก · แอดมินต้องระบุเหตุผล ·
/// N4 รายการค้างแถวเก่า (null) ใช้ซ้ำในแบบ ก · N6 ค่าตั้งผิด ⇒ 400 · ตั้งถูก ⇒ audit · หน้าซื้อส่งแบบที่ไม่เปิด ⇒ ปฏิเสธ</para>
/// </summary>
[Trait("Category", "Db")]
[Collection("SubscriptionExpirySweep")]
public class UpgradeModeRound237DbTests
{
    private readonly ITestOutputHelper _out;
    public UpgradeModeRound237DbTests(ITestOutputHelper output) => _out = output;

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private static async Task<(PlanTemplate Low, PlanTemplate High)?> TwoTiersAsync(AccountingDbContext db)
    {
        async Task Ensure(SubscriptionPlan plan, decimal m, decimal a)
        {
            if (await db.PlanTemplates.AnyAsync(t => t.Plan == plan && t.IsActive)) return;
            db.PlanTemplates.Add(new PlanTemplate
            {
                Name = $"แพ็กเกจทดสอบรอบ 237 {plan}", Plan = plan, IsActive = true, MonthlyPrice = m, QuarterlyPrice = m * 3, SemiAnnualPrice = m * 6, AnnualPrice = a,
                MaxUsers = 5, MaxCompanies = 1, MaxDocumentsPerMonth = 500, MaxJournalEntriesPerMonth = 500,
            });
            await db.SaveChangesAsync();
        }
        await Ensure(SubscriptionPlan.Basic, 590m, 6_018m);
        await Ensure(SubscriptionPlan.Pro, 1_490m, 15_198m);
        var low = await db.PlanTemplates.AsNoTracking().FirstAsync(t => t.Plan == SubscriptionPlan.Basic && t.IsActive);
        var high = await db.PlanTemplates.AsNoTracking().FirstAsync(t => t.Plan == SubscriptionPlan.Pro && t.IsActive);
        if (PlatformPlanPricing.Classify(low.MonthlyPrice, high.MonthlyPrice) != PlatformPlanPricing.ChangeKind.Upgrade || high.IsPermanentFree || low.IsPermanentFree
            || PlatformPlanPricing.CyclePrice(high, BillingCycle.Annual) <= PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual)
            || PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual) <= 0m)
            return null;   // ฐานร่วมตั้งราคาไว้ไม่เข้าเงื่อนไข — ข้าม (ไม่แก้แม่แบบของเทสต์อื่น)
        return (low, high);
    }

    private static async Task<(Guid CompanyId, Subscription Sub)> SeedAsync(AccountingDbContext db, int daysLeft = 200)
    {
        var c = new Company { Name = "บริษัททดสอบรอบ 237 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000114", Email = "t237@example.com" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        var sub = new Subscription
        {
            CompanyId = c.Id, Plan = SubscriptionPlan.Basic, Status = SubscriptionStatus.Active, BillingCycle = BillingCycle.Annual, Kind = SubscriptionKind.Paid,
            StartDate = DateTime.UtcNow.AddDays(-100), EndDate = DateTime.UtcNow.AddDays(daysLeft), UsageResetDate = DateTime.UtcNow.AddDays(20),
            LastPaymentDate = DateTime.UtcNow.AddDays(-100),
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        return (c.Id, sub);
    }

    /// <summary>ตั้งค่า "แบบที่เปิด" ชั่วคราวแล้วคืนค่าเดิม (แถวค่าตั้งแพลตฟอร์มแถวเดียว)</summary>
    private static async Task WithOfferAsync(AccountingDbContext db, PlatformUpgradeModeOffer offer, Func<Task> body)
    {
        var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstOrDefaultAsync();
        var created = s == null;
        if (s == null) { s = new SiteSettings(); db.SiteSettings.Add(s); }
        var original = s.PlatformUpgradeModeOffer;
        s.PlatformUpgradeModeOffer = offer;
        await db.SaveChangesAsync();
        try { await body(); }
        finally
        {
            var again = await db.SiteSettings.FirstAsync(x => x.Id == s.Id);
            if (created) db.SiteSettings.Remove(again); else again.PlatformUpgradeModeOffer = original;
            await db.SaveChangesAsync();
        }
    }

    private static async Task SetOfferAsync(AccountingDbContext db, PlatformUpgradeModeOffer offer)
    {
        var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstAsync();
        s.PlatformUpgradeModeOffer = offer;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task แบบขเริ่มรอบใหม่_สร้างรายการ_เปลี่ยนค่าตั้งก่อนเงินเข้า_อนุมัติยังเริ่มรอบใหม่ตามที่ลูกค้าเลือก()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var (low, high) = tiers.Value;
        var svc = new SubscriptionService(db, Notifications());
        var (cid, sub) = await SeedAsync(db);
        var originalEnd = sub.EndDate;   // sub เป็นแถวที่ context ติดตามอยู่ — อนุมัติแล้วถูกแก้ในหน่วยความจำ (CI รอบ 237)

        await WithOfferAsync(db, PlatformUpgradeModeOffer.CustomerChooses, async () =>
        {
            var expected = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual),
                PlatformPlanPricing.CyclePrice(high, BillingCycle.Annual), 12, sub.EndDate, DateTime.UtcNow);
            Assert.Equal(PlatformProration.UpgradeRoute.RestartWithCredit, expected.Route);

            // ใบเสนอ: 2 ตัวเลือก (ค่าเลือกให้ = คงวันหมดอายุ) · ยอดแบบ ข ตรงตัวตัดสิน
            var quote = await svc.QuotePlanChangeAsync(cid, SubscriptionPlan.Pro, BillingCycle.Annual, null);
            Assert.Equal(nameof(PlatformUpgradeMode.KeepExpiry), quote.UpgradeMode);
            Assert.NotNull(quote.UpgradeOptions);
            Assert.Equal(2, quote.UpgradeOptions!.Count);
            var optB = quote.UpgradeOptions.Single(o => o.Mode == nameof(PlatformUpgradeMode.RestartCycle));
            Assert.Equal(expected.Amount, optB.AmountDue);
            Assert.Equal(expected.Credit, optB.Credit);

            var res = await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro, BillingCycle.Annual, UpgradeMode: PlatformUpgradeMode.RestartCycle), "owner");
            Assert.NotNull(res.Payment);
            Assert.Equal(nameof(PlatformUpgradeMode.RestartCycle), res.Payment!.UpgradeMode);   // echo กลับ (กฎ #4 A)
            var pay = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == res.Payment.Id);
            Assert.True(pay.IsCycleChange);
            Assert.False(pay.IsProration);
            Assert.Equal(PlatformUpgradeMode.RestartCycle, pay.UpgradeMode);
            Assert.Equal(12, pay.RequestedPeriodMonths);
            Assert.Equal(expected.Amount, pay.Amount);
            Assert.Equal((decimal?)expected.Credit, pay.CycleChangeCredit);
            Assert.Equal(SubscriptionPlan.Basic, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).Plan);   // ยังไม่สลับจนเงินเข้า

            // 237-5: แอดมินเปลี่ยนค่าตั้งระหว่างรอเงินเข้า ⇒ รายการเดิมยังได้แบบที่ลูกค้าเลือก
            await SetOfferAsync(db, PlatformUpgradeModeOffer.KeepExpiryOnly);
            await svc.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
            var approved = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id);
            var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
            Assert.Equal(SubscriptionPaymentStatus.Approved, approved.Status);
            Assert.Equal(SubscriptionPlan.Pro, after.Plan);
            var wantEnd = PlatformPlanPricing.AddCycleMonths(approved.PaymentDate, 12);   // รอบใหม่ 12 เดือนนับจากวันชำระ
            Assert.True(Math.Abs((after.EndDate - wantEnd).TotalSeconds) < 1, $"EndDate {after.EndDate:O} ≠ วันชำระ + 12 เดือน {wantEnd:O}");
            Assert.True(after.EndDate > originalEnd.AddDays(100));   // ไม่ใช่วันหมดอายุเดิม
            Assert.Equal((DateTime?)after.EndDate, approved.SubscriptionExtendedTo);
            Assert.True(await db.SubscriptionHistories.AsNoTracking().AnyAsync(h => h.SubscriptionId == sub.Id && h.Action == "PlanUpgradeRestartApplied"));
        });
    }

    [Fact]
    public async Task แบบกคงวันหมดอายุ_ทิศตรงข้าม_อนุมัติแล้ววันหมดอายุเดิม_ค่าตั้งไม่เปิดแบบขปฏิเสธไม่สร้างรายการ()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var (low, high) = tiers.Value;
        var svc = new SubscriptionService(db, Notifications());

        // ค่าตั้งไม่เปิดแบบ ข ⇒ ขอแบบ ข = 400 ไม่สร้างรายการ · ใบเสนอ Blocked
        var (cidRefused, subRefused) = await SeedAsync(db);
        await WithOfferAsync(db, PlatformUpgradeModeOffer.KeepExpiryOnly, async () =>
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ChangeSubscriptionAsync(cidRefused,
                new ChangeSubscriptionRequest(SubscriptionPlan.Pro, BillingCycle.Annual, UpgradeMode: PlatformUpgradeMode.RestartCycle), "owner"));
            Assert.Contains("ไม่ได้เปิด", ex.Message);
            Assert.Equal(0, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == subRefused.Id));
            var q = await svc.QuotePlanChangeAsync(cidRefused, SubscriptionPlan.Pro, BillingCycle.Annual, null, null, PlatformUpgradeMode.RestartCycle);
            Assert.NotNull(q.Blocked);
            Assert.False(q.Preview!.Allowed);
            // ทิศตรงข้าม: ไม่ส่งแบบ ⇒ ได้แบบที่เปิด (คงวันหมดอายุ) · ตัวเลือกเดียว
            var q2 = await svc.QuotePlanChangeAsync(cidRefused, SubscriptionPlan.Pro, BillingCycle.Annual, null);
            Assert.Null(q2.Blocked);
            Assert.Equal(nameof(PlatformUpgradeMode.KeepExpiry), q2.UpgradeMode);
            Assert.Single(q2.UpgradeOptions!);
        });

        // แบบ ก (ค่าตั้งต้น · ไม่ส่งแบบ = พฤติกรรมเดิม): ส่วนต่าง · อนุมัติแล้ววันหมดอายุคงเดิม
        var (cid, sub) = await SeedAsync(db);
        var endBefore = sub.EndDate;
        var expected = PlatformProration.Upgrade(PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual), PlatformPlanPricing.CyclePrice(high, BillingCycle.Annual),
            12, sub.EndDate, DateTime.UtcNow);
        Assert.True(expected.Applies);
        await WithOfferAsync(db, PlatformUpgradeModeOffer.CustomerChooses, async () =>
        {
            var res = await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro, BillingCycle.Annual), "owner");
            var pay = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == res.Payment!.Id);
            Assert.True(pay.IsProration);
            Assert.False(pay.IsCycleChange);
            Assert.Equal(PlatformUpgradeMode.KeepExpiry, pay.UpgradeMode);
            Assert.Equal(expected.Amount, pay.Amount);
            await svc.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
        });
        var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Equal(SubscriptionPlan.Pro, after.Plan);
        Assert.True(Math.Abs((after.EndDate - endBefore).TotalSeconds) < 1, "แบบ ก วันหมดอายุต้องคงเดิม");
        Assert.True(await db.SubscriptionHistories.AsNoTracking().AnyAsync(h => h.SubscriptionId == sub.Id && h.Action == "PlanUpgradedProrated"));
    }

    // ═════════ ฝ่ายค้าน 237 ═════════

    private static async Task<User> UserAsync(AccountingDbContext db, string tag)
    {
        var u = new User { Email = $"{tag}-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ทดสอบรอบ 237 " + tag };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    /// <summary>License (AccountSubscription) ของ <paramref name="holder"/> แพ็ก <paramref name="plan"/> ถึง <paramref name="endUtc"/> — แบบเดียวกับ LicensePurchaseGuardRound236DbTests</summary>
    private static async Task<AccountSubscription> LicenseAsync(AccountingDbContext db, User holder, PlanTemplate plan, DateTime endUtc)
    {
        var lic = new AccountSubscription
        {
            OwnerUserId = holder.Id, PlanTemplateId = plan.Id, Status = SubscriptionStatus.Active, StartDate = DateTime.UtcNow.AddDays(-300), EndDate = endUtc,
            MaxCompanies = 10, MaxUsersPerCompany = 999, MaxDocumentsPerMonth = 99999, MaxJournalEntriesPerMonth = 99999,
            EnabledFeatures = plan.EnabledFeatures, GracePeriodDays = 7,
        };
        db.AccountSubscriptions.Add(lic);
        await db.SaveChangesAsync();
        return lic;
    }

    [Fact]
    public async Task S1_แบบข_Licenseแพ็กตรง_ขยับเป็นวันชำระบวก12เดือน_แพ็กไม่ตรงไม่ขยับและมีประวัติ()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var (low, high) = tiers.Value;
        var svc = new SubscriptionService(db, Notifications());

        await WithOfferAsync(db, PlatformUpgradeModeOffer.CustomerChooses, async () =>
        {
            // แพ็กตรง: ผู้ถือ License Pro ต่ออายุผ่านบริษัท (236-6) ด้วยแบบ ข ⇒ License ขยับเป็นวันชำระ + 12 เดือน
            var holder = await UserAsync(db, "lic237");
            var licEnd = DateTime.UtcNow.AddDays(30);
            var lic = await LicenseAsync(db, holder, high, licEnd);
            var (cid, sub) = await SeedAsync(db);
            db.CompanyUsers.Add(new CompanyUser { UserId = holder.Id, CompanyId = cid, Role = UserRole.Owner });
            sub.AccountSubscriptionId = lic.Id;
            await db.SaveChangesAsync();
            var res = await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro, BillingCycle.Annual, UpgradeMode: PlatformUpgradeMode.RestartCycle),
                holder.Id.ToString());
            Assert.Equal(nameof(PlatformUpgradeMode.RestartCycle), res.Payment!.UpgradeMode);
            await svc.ReviewPaymentAsync(res.Payment.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
            var paid = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == res.Payment.Id);
            var licAfter = await db.AccountSubscriptions.AsNoTracking().SingleAsync(a => a.Id == lic.Id);
            var want = PlatformPlanPricing.AddCycleMonths(paid.PaidPeriodStartUtc ?? paid.PaymentDate, 12);
            Assert.True(Math.Abs((licAfter.EndDate - want).TotalSeconds) < 1, $"License {licAfter.EndDate:O} ≠ วันชำระ + 12 เดือน {want:O}");
            Assert.True(await db.SubscriptionHistories.AsNoTracking().AnyAsync(h => h.AccountSubscriptionId == lic.Id && h.Action == "AccountPlanExtendedViaSlip"));
            Assert.NotNull(paid.PaidPeriodStartUtc);   // N2: วันเริ่มรอบที่ประทับ = ตัวที่คิด EndDate
            var subAfter = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
            Assert.True(Math.Abs((subAfter.EndDate - PlatformPlanPricing.AddCycleMonths(paid.PaidPeriodStartUtc!.Value, 12)).TotalSeconds) < 1);

            // แพ็กไม่ตรง: License Enterprise — สร้างรายการก่อนผูก แล้วผูกก่อนเงินเข้า ⇒ License ไม่ขยับ + ประวัติ AccountPlanCascadeSkipped
            var ent = await db.PlanTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Plan == SubscriptionPlan.Enterprise && t.IsActive);
            if (ent == null) { _out.WriteLine("ไม่มีแม่แบบ Enterprise ในฐานร่วม — ข้ามครึ่งหลัง"); return; }
            var holder2 = await UserAsync(db, "lic237b");
            var lic2 = await LicenseAsync(db, holder2, ent, licEnd);
            var (cid2, sub2) = await SeedAsync(db);
            var res2 = await svc.ChangeSubscriptionAsync(cid2, new ChangeSubscriptionRequest(SubscriptionPlan.Pro, BillingCycle.Annual, UpgradeMode: PlatformUpgradeMode.RestartCycle), "owner");
            sub2.AccountSubscriptionId = lic2.Id;
            await db.SaveChangesAsync();
            await svc.ReviewPaymentAsync(res2.Payment!.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
            var lic2After = await db.AccountSubscriptions.AsNoTracking().SingleAsync(a => a.Id == lic2.Id);
            Assert.True(Math.Abs((lic2After.EndDate - licEnd).TotalSeconds) < 1, "แพ็กไม่ตรง License ต้องไม่ขยับ");
            Assert.True(await db.SubscriptionHistories.AsNoTracking().AnyAsync(h => h.AccountSubscriptionId == lic2.Id && h.Action == "AccountPlanCascadeSkipped"));
        });
    }

    [Fact]
    public async Task S2_แบบข_เครดิตมากกว่าราคาเต็ม_สร้างรายการถูกปฏิเสธ_ไม่มีแถวรายการ()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var (_, high) = tiers.Value;
        var svc = new SubscriptionService(db, Notifications());
        var (cid, sub) = await SeedAsync(db);
        // ฐานเครดิต = ยอดที่จ่ายจริงล่าสุดของแพ็กเดิม (แอดมินตั้งยอดสูง 10 เท่าราคา Pro) ⇒ เครดิต 200 วัน > ราคาเต็มรอบ Pro
        db.SubscriptionPayments.Add(new SubscriptionPayment
        {
            SubscriptionId = sub.Id, PaymentNumber = "T237-" + Guid.NewGuid().ToString("N")[..8], PaymentDate = DateTime.UtcNow.AddDays(-165),
            PaymentMethod = PaymentMethod.BankTransfer, RequestedPlan = SubscriptionPlan.Basic, RequestedBillingCycle = BillingCycle.Annual, RequestedPeriodMonths = 12,
            Status = SubscriptionPaymentStatus.Approved, Amount = PlatformPlanPricing.CyclePrice(high, BillingCycle.Annual) * 10m, CreatedBy = "test",
        });
        await db.SaveChangesAsync();
        var before = await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id);
        await WithOfferAsync(db, PlatformUpgradeModeOffer.CustomerChooses, async () =>
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.ChangeSubscriptionAsync(cid,
                new ChangeSubscriptionRequest(SubscriptionPlan.Pro, BillingCycle.Annual, UpgradeMode: PlatformUpgradeMode.RestartCycle), "owner"));
            Assert.Equal(PlatformProration.RestartCreditCoversAllRefusal, ex.Message);
            Assert.Equal(before, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id));   // ไม่มีแถวรายการใหม่
            var opt = (await svc.QuotePlanChangeAsync(cid, SubscriptionPlan.Pro, BillingCycle.Annual, null)).UpgradeOptions!
                .Single(o => o.Mode == nameof(PlatformUpgradeMode.RestartCycle));
            Assert.False(opt.Allowed);
            Assert.Equal(PlatformProration.RestartCreditCoversAllRefusal, opt.RefusalReason);
        });
    }

    [Fact]
    public async Task S3_237_8_เครดิตไม่ตรงวันชำระ_เงินเข้าพักให้แอดมิน_แอดมินไม่ใส่เหตุผลอนุมัติไม่ได้_ใส่แล้วอนุมัติพร้อมaudit()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var svc = new SubscriptionService(db, Notifications());
        var admin = await UserAsync(db, "adm237");
        var (cid, sub) = await SeedAsync(db);
        await WithOfferAsync(db, PlatformUpgradeModeOffer.CustomerChooses, async () =>
        {
            var res = await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro, BillingCycle.Annual, UpgradeMode: PlatformUpgradeMode.RestartCycle), "owner");
            // จำลองรายการที่คิดเครดิตไว้ 3 วันก่อน (เหลือมากกว่าวันนี้ 3 วัน) — เงินเข้าวันนี้
            var row = await db.SubscriptionPayments.SingleAsync(p => p.Id == res.Payment!.Id);
            row.ProrationDays += 3;
            await db.SaveChangesAsync();
            await svc.ReviewPaymentAsync(row.Id, new ReviewSubscriptionPaymentRequest(true, null, null), "system", systemConfirmed: true);
            var held = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == row.Id);
            Assert.Equal(SubscriptionPaymentStatus.UnderReview, held.Status);
            Assert.Contains("เครดิตไม่ตรงวันชำระ", held.AttentionReason);
            Assert.Contains("อัปเกรดแบบเริ่มรอบใหม่", held.AttentionReason);   // N1
            Assert.Equal(SubscriptionPlan.Basic, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).Plan);   // ไม่ประทับผลเอง

            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                svc.ReviewPaymentAsync(row.Id, new ReviewSubscriptionPaymentRequest(true, null, null), admin.Id.ToString()));
            Assert.Equal(PlatformProration.RestartCreditDriftRuleCode, ex.RuleCode);
            await svc.ReviewPaymentAsync(row.Id, new ReviewSubscriptionPaymentRequest(true, "ตกลงกับลูกค้าแล้ว ใช้ยอดเดิม", null), admin.Id.ToString());
            Assert.Equal(SubscriptionPaymentStatus.Approved, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == row.Id)).Status);
            Assert.Equal(SubscriptionPlan.Pro, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).Plan);
            Assert.True(await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().AnyAsync(a => a.EntityId == row.Id.ToString()
                && a.NewValues!.Contains(PlatformProration.RestartCreditDriftRuleCode)));
        });
    }

    [Fact]
    public async Task N4_รายการค้างแถวเก่าไม่มีแบบ_ถือเป็นแบบก_กดซ้ำได้รายการเดิม_แบบขเป็นรายการใหม่()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var svc = new SubscriptionService(db, Notifications());
        var (cid, _) = await SeedAsync(db);
        await WithOfferAsync(db, PlatformUpgradeModeOffer.CustomerChooses, async () =>
        {
            var first = await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro, BillingCycle.Annual), "owner");
            var row = await db.SubscriptionPayments.SingleAsync(p => p.Id == first.Payment!.Id);
            row.UpgradeMode = null;   // แถวก่อนรอบ 237
            await db.SaveChangesAsync();
            var again = await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro, BillingCycle.Annual, UpgradeMode: PlatformUpgradeMode.KeepExpiry), "owner");
            Assert.Equal(row.Id, again.Payment!.Id);   // ใช้ซ้ำ (ไม่สร้างคำขอซ้อน)
            // ทิศตรงข้าม: แบบ ข = คนละคำขอ
            var other = await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Pro, BillingCycle.Annual, UpgradeMode: PlatformUpgradeMode.RestartCycle), "owner");
            Assert.NotEqual(row.Id, other.Payment!.Id);
        });
    }

    [Fact]
    public async Task N6_ค่าตั้งไม่ถูกต้อง400_ตั้งถูกมีaudit_หน้าซื้อส่งแบบที่ไม่เปิดถูกปฏิเสธ()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        if (await db.SiteSettings.AnyAsync() == false) { db.SiteSettings.Add(new SiteSettings()); await db.SaveChangesAsync(); }

        await WithOfferAsync(db, PlatformUpgradeModeOffer.CustomerChooses, async () =>
        {
            var ctl = new Accounting.Controllers.AdminPlatformUpgradeModeController(db)
            {
                ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() },
            };
            var bad = await ctl.Set(new Accounting.Controllers.AdminPlatformUpgradeModeController.SetRequest((PlatformUpgradeModeOffer)99), default);
            Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(bad.Result);
            var since = DateTime.UtcNow.AddSeconds(-5);
            var ok = await ctl.Set(new Accounting.Controllers.AdminPlatformUpgradeModeController.SetRequest(PlatformUpgradeModeOffer.KeepExpiryOnly), default);
            Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(ok.Result);
            Assert.Equal(PlatformUpgradeModeOffer.KeepExpiryOnly, (await db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).FirstAsync()).PlatformUpgradeModeOffer);
            Assert.True(await db.AuditLogs.IgnoreQueryFilters().AsNoTracking().AnyAsync(a => a.Timestamp >= since && a.NewValues!.Contains("platform-upgrade-mode")
                && a.NewValues!.Contains(nameof(PlatformUpgradeModeOffer.KeepExpiryOnly))));

            // หน้าซื้อสาธารณะ: ค่าตั้ง "คงวันหมดอายุเท่านั้น" + ส่งแบบ ข ⇒ ปฏิเสธ ไม่สร้างรายการ
            var owner = await UserAsync(db, "co237");
            var (cid, sub) = await SeedAsync(db);
            db.CompanyUsers.Add(new CompanyUser { UserId = owner.Id, CompanyId = cid, Role = UserRole.Owner });
            var s = await db.SiteSettings.OrderBy(x => x.CreatedAt).FirstAsync();
            var checkoutBefore = s.PublicCheckoutEnabled;
            s.PublicCheckoutEnabled = true;
            await db.SaveChangesAsync();
            try
            {
                var checkout = new Accounting.Services.Platform.PublicCheckoutService(db, new SubscriptionService(db, Notifications()),
                    F4Proxy<Accounting.Services.Payments.IPlatformPaymentService>.Create((name, _) => name == nameof(Accounting.Services.Payments.IPlatformPaymentService.StatusAsync)
                        ? new Accounting.Services.Payments.PlatformPayStatus(false, false, null, null, Array.Empty<string>(), null) : null),
                    F4Proxy<ILegalDocumentService>.Create((_, _) => null), F4Proxy<IEmailService>.Create((_, _) => null),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<Accounting.Services.Platform.PublicCheckoutService>.Instance);
                var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => checkout.CreateOrderAsync(owner.Id, cid, SubscriptionPlan.Pro, BillingCycle.Annual, null,
                    upgradeMode: PlatformUpgradeMode.RestartCycle));
                Assert.Contains("ไม่ได้เปิด", ex.Message);
                Assert.Equal(0, await db.SubscriptionPayments.CountAsync(p => p.SubscriptionId == sub.Id));
            }
            finally
            {
                var again = await db.SiteSettings.FirstAsync(x => x.Id == s.Id);
                again.PublicCheckoutEnabled = checkoutBefore;
                await db.SaveChangesAsync();
            }
        });
    }
}
