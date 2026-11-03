using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Subscription;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Background;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 231 (คำตัดสินข้อ 231-8..231-11) บน PostgreSQL จริง — เส้นที่ pure test มองไม่เห็น:
/// ราคาพิเศษรายบริษัทไหลเข้าคำขอแพ็กเกจ (QuotedAmount = ราคาพิเศษ · StandardAmount = ราคาตั้ง) ↔ บริษัทไม่มีราคาพิเศษคงกติกาเดิม ·
/// เปลี่ยนรอบบิลรายเดือน→รายปีกลางรอบ: รายการ IsCycleChange ยอดตามตัวคิด → เงินเข้า ⇒ รอบรายปีใหม่เริ่มวันชำระ ↔ รายปี→รายเดือน = Scheduled* ไม่เก็บเงิน ·
/// backfill Kind ด้วย SQL ของ migration = สูตร SubscriptionKindRule · งานปิดข้อมูลทดลองใช้ไม่แตะ legal hold / ไม่เปิดสวิตช์ และปิดเมื่อเข้าเงื่อนไขครบ
/// </summary>
[Trait("Category", "Db")]
public class PlatformPriceOverrideRound231DbTests
{
    private readonly ITestOutputHelper _out;
    public PlatformPriceOverrideRound231DbTests(ITestOutputHelper output) => _out = output;

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    private static async Task<(PlanTemplate Low, PlanTemplate High)?> TwoTiersAsync(AccountingDbContext db)
    {
        async Task Ensure(SubscriptionPlan plan, decimal m, decimal a)
        {
            if (await db.PlanTemplates.AnyAsync(t => t.Plan == plan && t.IsActive)) return;
            db.PlanTemplates.Add(new PlanTemplate
            {
                Name = $"แพ็กเกจทดสอบรอบ 231 {plan}", Plan = plan, IsActive = true, MonthlyPrice = m, QuarterlyPrice = m * 3, SemiAnnualPrice = m * 6, AnnualPrice = a,
                MaxUsers = 5, MaxCompanies = 1, MaxDocumentsPerMonth = 500, MaxJournalEntriesPerMonth = 500,
            });
            await db.SaveChangesAsync();
        }
        await Ensure(SubscriptionPlan.Basic, 490m, 4_900m);
        await Ensure(SubscriptionPlan.Pro, 990m, 9_900m);
        var low = await db.PlanTemplates.AsNoTracking().FirstAsync(t => t.Plan == SubscriptionPlan.Basic && t.IsActive);
        var high = await db.PlanTemplates.AsNoTracking().FirstAsync(t => t.Plan == SubscriptionPlan.Pro && t.IsActive);
        if (PlatformPlanPricing.Classify(low.MonthlyPrice, high.MonthlyPrice) != PlatformPlanPricing.ChangeKind.Upgrade || high.IsPermanentFree || low.IsPermanentFree
            || PlatformPlanPricing.CyclePrice(high, BillingCycle.Annual) <= PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual)
            || PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual) <= 0m || PlatformPlanPricing.CyclePrice(low, BillingCycle.Monthly) <= 0m)
            return null;   // ฐานร่วมตั้งราคาไว้ไม่เข้าเงื่อนไข — ข้าม (ไม่แก้แม่แบบของเทสต์อื่น)
        return (low, high);
    }

    private static async Task<(Guid CompanyId, Subscription Sub)> SeedAsync(AccountingDbContext db, SubscriptionPlan plan,
        SubscriptionStatus status = SubscriptionStatus.Active, BillingCycle cycle = BillingCycle.Annual, int daysLeft = 200, SubscriptionKind kind = SubscriptionKind.Paid)
    {
        var c = new Company { Name = "บริษัททดสอบรอบ 231 " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000005", Email = "t231@example.com" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        var sub = new Subscription
        {
            CompanyId = c.Id, Plan = plan, Status = status, BillingCycle = cycle, Kind = kind,
            StartDate = DateTime.UtcNow.AddDays(-100), EndDate = DateTime.UtcNow.AddDays(daysLeft), UsageResetDate = DateTime.UtcNow.AddDays(20),
            LastPaymentDate = kind == SubscriptionKind.Paid ? DateTime.UtcNow.AddDays(-100) : null,
        };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        return (c.Id, sub);
    }

    [Fact]
    public async Task ราคาพิเศษไหลเข้าคำขอแพ็กเกจ_QuotedAmountเป็นราคาพิเศษ_บริษัทไม่มีราคาพิเศษคงกติกาเดิม()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var (low, high) = tiers.Value;
        var svc = new SubscriptionService(db, Notifications());

        // บริษัท A: จ่ายราคา Basic ได้ Pro (PriceOfPlan) · ทดลองใช้อยู่ ⇒ ConvertTrial = คำขอแพ็กเกจเต็มรอบ
        var (cidA, subA) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, BillingCycle.Monthly, 10, SubscriptionKind.Trial);
        db.CompanyPlanPriceOverrides.Add(new CompanyPlanPriceOverride
        {
            CompanyId = cidA, PlanTemplateId = high.Id, Mode = PlanPriceOverrideMode.PriceOfPlan, ReferencePlanTemplateId = low.Id,
            EffectiveFromUtc = DateTime.UtcNow.AddDays(-1), Reason = "ข้อตกลงทดสอบ", ApprovedBy = "admin",
        });
        await db.SaveChangesAsync();
        var resA = await svc.ConvertTrialAsync(cidA, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Annual), "owner");
        Assert.NotNull(resA.Payment);
        var payA = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == resA.Payment!.Id);
        Assert.Equal(PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual), payA.Amount);
        Assert.Equal((decimal?)payA.Amount, payA.QuotedAmount);
        Assert.Equal((decimal?)PlatformPlanPricing.CyclePrice(high, BillingCycle.Annual), payA.StandardAmount);
        Assert.NotNull(payA.PriceOverrideId);
        // ดูราคาตรงกับตอนยืนยัน + บอกราคาตั้ง
        var quoteA = await svc.QuotePlanChangeAsync(cidA, SubscriptionPlan.Pro, BillingCycle.Annual, null);
        Assert.Equal(payA.Amount, quoteA.AmountDue);
        Assert.Equal((decimal?)PlatformPlanPricing.CyclePrice(high, BillingCycle.Annual), quoteA.StandardAmount);
        Assert.NotNull(quoteA.SpecialPriceNote);
        // แพ็กเกจปัจจุบันแสดง "ราคาพิเศษสำหรับคุณ" หลังเงินเข้า
        await svc.ReviewPaymentAsync(payA.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
        var afterA = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == subA.Id);
        Assert.Equal(SubscriptionPlan.Pro, afterA.Plan);
        Assert.Equal(SubscriptionKind.Paid, afterA.Kind);
        Assert.Equal(PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual), afterA.PricePerCycle);   // ราคาที่จ่ายจริงบน subscription
        var respA = await svc.GetSubscriptionAsync(cidA);
        Assert.NotNull(respA.SpecialPriceNote);
        Assert.Equal("Paid", respA.Kind);

        // ทิศตรงข้าม — บริษัท B ไม่มีราคาพิเศษ: ราคาแม่แบบ · QuotedAmount null (กติกาเดิม "ต้องเท่าราคาเต็ม") · ไม่มีป้าย
        var (cidB, _) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Trial, BillingCycle.Monthly, 10, SubscriptionKind.Trial);
        var resB = await svc.ConvertTrialAsync(cidB, new ConvertTrialRequest(SubscriptionPlan.Pro, BillingCycle.Annual), "owner");
        var payB = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == resB.Payment!.Id);
        Assert.Equal(PlatformPlanPricing.CyclePrice(high, BillingCycle.Annual), payB.Amount);
        Assert.Null(payB.QuotedAmount);
        Assert.Null(payB.StandardAmount);
        Assert.Null(payB.PriceOverrideId);
        Assert.Null((await svc.QuotePlanChangeAsync(cidB, SubscriptionPlan.Pro, BillingCycle.Annual, null)).SpecialPriceNote);
        Assert.Null((await svc.GetSubscriptionAsync(cidB)).SpecialPriceNote);
    }

    [Fact]
    public async Task เปลี่ยนรอบรายเดือนเป็นรายปีกลางรอบ_จ่ายรอบใหม่หักเครดิต_เงินเข้ารอบใหม่เริ่มวันชำระ_รายปีเป็นรายเดือนมีผลวันครบรอบ()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var tiers = await TwoTiersAsync(db);
        if (tiers == null) { _out.WriteLine("แม่แบบแพ็กเกจในฐานร่วมไม่เข้าเงื่อนไข — ข้าม"); return; }
        var (low, _) = tiers.Value;
        var svc = new SubscriptionService(db, Notifications());

        var (cid, sub) = await SeedAsync(db, SubscriptionPlan.Basic, SubscriptionStatus.Active, BillingCycle.Monthly, daysLeft: 15);
        var expected = PlatformProration.CycleChange(PlatformPlanPricing.CyclePrice(low, BillingCycle.Monthly), 1,
            PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual), 12, sub.EndDate, DateTime.UtcNow);
        Assert.True(expected.Applies);
        var res = await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Basic, BillingCycle.Annual), "owner");
        Assert.NotNull(res.Payment);
        var pay = await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == res.Payment!.Id);
        Assert.True(pay.IsCycleChange);
        Assert.False(pay.IsProration);
        Assert.Equal(12, pay.RequestedPeriodMonths);
        Assert.Equal(expected.Amount, pay.Amount);
        Assert.Equal((decimal?)expected.Credit, pay.CycleChangeCredit);
        Assert.Equal((decimal?)pay.Amount, pay.QuotedAmount);
        Assert.Equal(BillingCycle.Monthly, (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id)).BillingCycle);   // ยังไม่สลับจนเงินเข้า
        var quote = await svc.QuotePlanChangeAsync(cid, SubscriptionPlan.Basic, BillingCycle.Annual, null);
        Assert.Equal("CycleChange", quote.Kind);
        Assert.Equal(expected.Amount, quote.AmountDue);
        Assert.Equal((decimal?)expected.Credit, quote.CycleChangeCredit);

        var before = DateTime.UtcNow;
        await svc.ReviewPaymentAsync(pay.Id, new ReviewSubscriptionPaymentRequest(true, "test", null), "system", systemConfirmed: true);
        var after = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Equal(BillingCycle.Annual, after.BillingCycle);
        Assert.Equal(SubscriptionStatus.Active, after.Status);
        Assert.True(after.EndDate >= before.AddMonths(12).AddMinutes(-1) && after.EndDate <= DateTime.UtcNow.AddMonths(12).AddMinutes(1),
            $"รอบใหม่ต้องเริ่มวันชำระ: {after.EndDate:O}");
        Assert.Equal(PlatformPlanPricing.CyclePrice(low, BillingCycle.Annual), after.PricePerCycle);
        Assert.Equal(SubscriptionPaymentStatus.Approved, (await db.SubscriptionPayments.AsNoTracking().SingleAsync(p => p.Id == pay.Id)).Status);
        Assert.True(await db.SubscriptionHistories.AsNoTracking().AnyAsync(h => h.SubscriptionId == sub.Id && h.Action == "CycleChangeApplied"));

        // ทิศตรงข้าม: รายปี → รายเดือน = ไม่มีรายการชำระ · ตั้งมีผลวันครบรอบ · รอบ/วันหมดอายุไม่ขยับ
        var endBefore = after.EndDate;
        var down = await svc.ChangeSubscriptionAsync(cid, new ChangeSubscriptionRequest(SubscriptionPlan.Basic, BillingCycle.Monthly), "owner");
        Assert.Null(down.Payment);
        var afterDown = await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sub.Id);
        Assert.Equal(BillingCycle.Annual, afterDown.BillingCycle);
        Assert.Equal(BillingCycle.Monthly, afterDown.ScheduledBillingCycle);
        Assert.True(Math.Abs((afterDown.EndDate - endBefore).TotalMilliseconds) < 1);
    }

    [Fact]
    public async Task backfill_Kind_ด้วยSQLของmigration_ตรงสูตรSubscriptionKindRule_และไม่ทับแถวที่เขียนแล้ว()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var (_, free) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Active, BillingCycle.Monthly, 36500, SubscriptionKind.Unknown);
        free.IsPermanentFree = true; free.LastPaymentDate = null;
        var (_, trialExpired) = await SeedAsync(db, SubscriptionPlan.Pro, SubscriptionStatus.Expired, BillingCycle.Monthly, -5, SubscriptionKind.Unknown);
        trialExpired.LastPaymentDate = null; trialExpired.PricePerCycle = 990m;
        db.TrialConfigs.Add(new TrialConfig { SubscriptionId = trialExpired.Id, TrialStatus = TrialStatus.Expired, TrialStartDate = DateTime.UtcNow.AddDays(-35), TrialEndDate = DateTime.UtcNow.AddDays(-5) });
        // ฝ่ายค้าน B1 negative: แพ็กเกจจ่ายเงินหมดอายุ LastPaymentDate ว่าง ไม่มีประวัติทดลอง/รายการอนุมัติ = Paid (ลูกค้าหมดอายุ)
        var (_, paidExpiredNoTrial) = await SeedAsync(db, SubscriptionPlan.Pro, SubscriptionStatus.Expired, BillingCycle.Monthly, -5, SubscriptionKind.Unknown);
        paidExpiredNoTrial.LastPaymentDate = null; paidExpiredNoTrial.PricePerCycle = 990m;
        // มีรายการ Approved แม้ LastPaymentDate ว่าง = เคยจ่าย ⇒ Paid แม้ Plan FreeTrial
        var (_, approvedOnly) = await SeedAsync(db, SubscriptionPlan.FreeTrial, SubscriptionStatus.Expired, BillingCycle.Monthly, -5, SubscriptionKind.Unknown);
        approvedOnly.LastPaymentDate = null;
        db.SubscriptionPayments.Add(new SubscriptionPayment { SubscriptionId = approvedOnly.Id, PaymentNumber = "T231-" + Guid.NewGuid().ToString("N")[..6], Amount = 990m,
            PaymentDate = DateTime.UtcNow.AddDays(-40), RequestedPlan = SubscriptionPlan.Pro, RequestedBillingCycle = BillingCycle.Monthly, RequestedPeriodMonths = 1, Status = SubscriptionPaymentStatus.Approved });
        var (_, paid) = await SeedAsync(db, SubscriptionPlan.Pro, SubscriptionStatus.Active, BillingCycle.Annual, 100, SubscriptionKind.Unknown);
        paid.LastPaymentDate = DateTime.UtcNow.AddDays(-10); paid.PricePerCycle = 9_900m;
        var (_, alreadyWritten) = await SeedAsync(db, SubscriptionPlan.Pro, SubscriptionStatus.Active, BillingCycle.Annual, 100, SubscriptionKind.Trial);   // โค้ดเขียนแล้ว (ค่าตั้งใจ) — ห้ามทับ
        alreadyWritten.LastPaymentDate = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var backfill = PlatformPriceOverrideMigration.Statements().Single(s => s.Contains("UPDATE \"Subscriptions\" SET \"Kind\""));
        await db.Database.ExecuteSqlRawAsync(backfill);

        async Task<SubscriptionKind> KindOf(Guid id) => (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == id)).Kind;
        Assert.Equal(SubscriptionKind.Free, await KindOf(free.Id));
        Assert.Equal(SubscriptionKind.Trial, await KindOf(trialExpired.Id));
        Assert.Equal(SubscriptionKind.Paid, await KindOf(paid.Id));
        Assert.Equal(SubscriptionKind.Trial, await KindOf(alreadyWritten.Id));   // Kind ≠ 0 ไม่ถูกทับ
        Assert.Equal(SubscriptionKind.Paid, await KindOf(paidExpiredNoTrial.Id));   // ฝ่ายค้าน B1 negative
        Assert.Equal(SubscriptionKind.Paid, await KindOf(approvedOnly.Id));         // รายการ Approved = เคยจ่าย
        // SQL = Derive
        Assert.Equal(SubscriptionKindRule.Derive(true, SubscriptionStatus.Active, true, true), await KindOf(free.Id));
        Assert.Equal(SubscriptionKindRule.Derive(false, SubscriptionStatus.Expired, true, false, trialRecordExpired: true), await KindOf(trialExpired.Id));
        Assert.Equal(SubscriptionKindRule.Derive(false, SubscriptionStatus.Expired, true, false, trialRecordExpired: false), await KindOf(paidExpiredNoTrial.Id));
        Assert.Equal(SubscriptionKindRule.Derive(false, SubscriptionStatus.Expired, SubscriptionKindRule.NeverPaid(null, hasApprovedPayment: true), true), await KindOf(approvedOnly.Id));
        Assert.Equal(SubscriptionKindRule.Derive(false, SubscriptionStatus.Active, false, false), await KindOf(paid.Id));
    }

    [Fact]
    public async Task งานปิดข้อมูลทดลองใช้_ไม่แตะlegalHold_ไม่แตะเมื่อไม่เปิดสวิตช์_ปิดเมื่อครบเงื่อนไข()
    {
        await using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน — ข้าม (หลักฐานคือ job db-test)"); return; }
        var now = DateTime.UtcNow;

        async Task<(Guid CompanyId, Subscription Sub)> TrialAsync(bool on, int daysAgo)
        {
            var (cid, sub) = await SeedAsync(db, SubscriptionPlan.Pro, SubscriptionStatus.Expired, BillingCycle.Monthly, -daysAgo, SubscriptionKind.Trial);
            db.TrialConfigs.Add(new TrialConfig
            {
                SubscriptionId = sub.Id, TrialStatus = TrialStatus.Expired, TrialStartDate = now.AddDays(-daysAgo - 30), TrialEndDate = now.AddDays(-daysAgo),
                DeleteDataAfterGracePeriod = on, GracePeriodDays = 7, DataRetentionDays = 30, PurgeNoticeDays = 14,
            });
            await db.SaveChangesAsync();
            return (cid, sub);
        }

        var (cHold, sHold) = await TrialAsync(on: true, daysAgo: 100);
        db.JournalEntries.Add(new JournalEntry { CompanyId = cHold, EntryNumber = "JV-T231-" + Guid.NewGuid().ToString("N")[..6], Status = JournalEntryStatus.Posted, EntryDate = now.AddDays(-120) });
        var (cOff, sOff) = await TrialAsync(on: false, daysAgo: 100);
        var (cNotDue, sNotDue) = await TrialAsync(on: true, daysAgo: 10);
        var (cPurge, sPurge) = await TrialAsync(on: true, daysAgo: 100);
        await db.SaveChangesAsync();

        // รอบแรก (ฝ่ายค้าน B2): ครบกำหนดเก็บแล้ว ⇒ "แจ้งเจ้าของล่วงหน้า 14 วัน" ก่อน — ยังไม่ปิดใคร · legal hold ไม่ถูกแจ้ง
        var r1 = await TrialDataRetentionJob.RunOnceAsync(db, now, null);
        Assert.Equal(0, r1.Purged);
        Assert.True(r1.NoticeSent >= 1);
        Assert.True(r1.LegalHold >= 1);
        Assert.NotNull((await db.TrialConfigs.AsNoTracking().SingleAsync(t => t.SubscriptionId == sPurge.Id)).PurgeNoticeSentAt);
        Assert.Null((await db.TrialConfigs.AsNoTracking().SingleAsync(t => t.SubscriptionId == sHold.Id)).PurgeNoticeSentAt);
        Assert.True(await db.SubscriptionHistories.AsNoTracking().AnyAsync(h => h.SubscriptionId == sPurge.Id && h.Action == "TrialDataPurgeNoticeSent"));
        Assert.Contains("แจ้งเจ้าของรอบนี้", r1.Summary);
        // ยังไม่ครบ 14 วันนับจากวันแจ้ง ⇒ ยังไม่ปิด
        Assert.Equal(0, (await TrialDataRetentionJob.RunOnceAsync(db, now.AddDays(13), null)).Purged);
        // ครบ 14 วัน ⇒ ปิด (เฉพาะแถวที่แจ้งแล้วและไม่ติด hold)
        var purged = (await TrialDataRetentionJob.RunOnceAsync(db, now.AddDays(14), null)).Purged;
        Assert.True(purged >= 1);

        async Task<(bool Deleted, SubscriptionStatus Status)> StateOf(Guid cid, Guid sid)
            => ((await db.Companies.AsNoTracking().IgnoreQueryFilters().SingleAsync(c => c.Id == cid)).IsDeleted,
                (await db.Subscriptions.AsNoTracking().SingleAsync(s => s.Id == sid)).Status);
        Assert.Equal((false, SubscriptionStatus.Expired), await StateOf(cHold, sHold.Id));     // legal hold: มี JE ที่ลงแล้ว
        Assert.Equal((false, SubscriptionStatus.Expired), await StateOf(cOff, sOff.Id));       // ไม่เปิดสวิตช์
        Assert.Equal((false, SubscriptionStatus.Expired), await StateOf(cNotDue, sNotDue.Id)); // ยังไม่ครบ 7+30 วัน
        Assert.Equal((true, SubscriptionStatus.Cancelled), await StateOf(cPurge, sPurge.Id));
        Assert.Null((await db.Companies.AsNoTracking().IgnoreQueryFilters().SingleAsync(c => c.Id == cPurge)).Email);
        Assert.True(await db.SubscriptionHistories.AsNoTracking().AnyAsync(h => h.SubscriptionId == sPurge.Id && h.Action == "TrialDataPurged"));
        Assert.True(await db.AuditLogs.AsNoTracking().AnyAsync(a => a.CompanyId == cPurge && a.EntityType == nameof(Company) && a.NewValues!.Contains(TrialDataRetentionPolicy.RuleCode)));
        // รันซ้ำ = ไม่ทำอะไรเพิ่ม (บริษัทที่ปิดแล้ว / sub Cancelled ไม่เข้าเงื่อนไข)
        Assert.Equal(0, (await TrialDataRetentionJob.RunOnceAsync(db, now.AddDays(14), null)).Purged);
    }
}
