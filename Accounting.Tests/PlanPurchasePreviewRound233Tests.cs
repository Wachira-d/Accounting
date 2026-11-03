using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 233 (คำตัดสิน 233-1…233-8 · erp-review/2026-11-03/LICENSE_CASE_MATRIX.md) — "สิ่งที่ลูกค้าจะได้ถ้าจ่ายเงินนี้" (<see cref="PlanPurchasePreview"/>)
/// <para>เจ้าของ: "ต่ออายุไปอีก 1 ปีจากวันหมดอายุเดิม จะเปลี่ยนวันหมดอายุเป็นวันที่เท่าไหร่ · ถ้าเลือกแพ็คต่ำกว่าต้องขึ้นแจ้งว่าชำระไม่ได้เพราะอะไร ·
/// standard มา 1 ปี ใช้ไป 3 เดือน แล้วกด pro ราคาต้องคำนวณให้ถูกต้อง" — ทุกเทสต์ใช้เวลาคงที่ · วันที่ไทย พ.ศ. ตรวจมือได้ · มีทิศตรงข้ามทุกด่าน</para>
/// </summary>
public class PlanPurchasePreviewRound233Tests
{
    // ไทย 00:00 ของวันที่ (y,m,d) ในรูป UTC
    private static DateTime ThaiMidnight(int y, int m, int d) => new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc).AddHours(-7);
    // ไทย 10:00 ของวันที่ (y,m,d) ในรูป UTC
    private static DateTime ThaiTen(int y, int m, int d) => new(y, m, d, 3, 0, 0, DateTimeKind.Utc);

    // แม่แบบเทสต์ = ราคาจริงใน Data/SeedPlanTemplates.cs: Starter (Basic) รายปี 6,018 · Professional (Pro) รายปี 15,198 · Enterprise 4,990/เดือน
    private const decimal StarterAnnual = 6_018m, ProAnnual = 15_198m;

    private static PlanPurchasePreview.Current Cur(SubscriptionPlan plan, string name, BillingCycle cycle, SubscriptionStatus status, DateTime end,
        decimal monthly, bool permanentFree = false, bool cancelAtEnd = false, SubscriptionPlan? scheduled = null)
        => new(plan, name, cycle, status, permanentFree, end, monthly, cancelAtEnd, scheduled, scheduled != null ? end : null);

    private static PlanPurchasePreview.Target Tgt(SubscriptionPlan plan, string name, BillingCycle cycle, decimal monthly) => new(plan, name, cycle, monthly);

    // ═════════ ตัวอย่างของเจ้าของ: Starter รายปีจ่ายแล้ว ใช้ไป 3 เดือน → Pro ═════════

    [Fact]
    public void เจ้าของ_Starterรายปี_ใช้3เดือน_อัปเกรดPro_จ่ายส่วนต่าง_6916_44_วันหมดอายุคงเดิม()
    {
        var end = ThaiMidnight(2027, 1, 1);          // จ่าย 01/01/2569 ⇒ ใช้ได้ถึง 01/01/2570
        var now = ThaiTen(2026, 4, 1);               // ใช้ไป 3 เดือน (01/04/2569)
        var pr = PlatformProration.Upgrade(StarterAnnual, ProAnnual, 12, end, now);
        Assert.True(pr.Applies);
        Assert.Equal(275, pr.DaysRemaining);         // 01/04 → 01/01 = 275 วัน
        Assert.Equal(365, pr.CycleDays);
        Assert.Equal(6_916.44m, pr.Amount);          // (15,198 − 6,018) × 275/365 = 6,916.438… ⇒ 6,916.44
        Assert.Equal(StarterAnnual, pr.OldPricePerCycle);

        var pv = PlanPurchasePreview.ForPurchase(
            Cur(SubscriptionPlan.Basic, "Starter", BillingCycle.Annual, SubscriptionStatus.Active, end, 590m),
            Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, 1_490m),
            new PlanPurchasePreview.Money(pr.Amount, 0m, pr.Amount, Prorated: true, OldPriceBasis: pr.OldPricePerCycle,
                DaysRemaining: pr.DaysRemaining, CycleDays: pr.CycleDays), now, vatIncluded: true);
        Assert.Equal("Upgrade", pv.Kind);
        Assert.True(pv.Allowed);
        Assert.True(pv.EndUnchanged);
        Assert.Equal(end, pv.NewEndUtc);              // วันหมดอายุคงเดิม (คำตัดสินเจ้าของข้อ 178 · 233-2 แบบ ก)
        Assert.Equal(4_534.11m, pv.Credit);           // เครดิต Starter = 6,018 × 275/365 = 4,534.109… ⇒ 4,534.11
        Assert.Equal(11_450.55m, pv.ListPrice);       // ค่า Pro ส่วนที่เหลือ = 15,198 × 275/365 = 11,450.547… ⇒ 11,450.55
        Assert.Equal(pv.AmountDue, pv.ListPrice - pv.Credit);   // ผลรวมบรรทัด = ยอดที่คิดจริงพอดี (ไม่มีเศษสตางค์หลุด)
        Assert.Equal(6_916.44m, pv.AmountDue);
        Assert.Contains("01/01/2570", pv.DateLine);
        Assert.Contains("คงเดิม", pv.DateLine);
        Assert.Contains(pv.Lines, l => l.Contains("รวม VAT"));
    }

    [Fact]
    public void เจ้าของ_ทางเลือกข_เริ่มรอบใหม่หักเครดิต_ตัวเลขเทียบ_มูลค่าเท่ากัน()
    {
        // 233-2 (บันทึกตัวเลขทางเลือก ข ไว้ให้เจ้าของเทียบ — ไม่ใช่ค่าตั้งต้น): Pro เต็มปี 15,198 − เครดิต 4,534.11 = 10,663.89 ได้ถึง 01/04/2570
        var credit = PlatformProration.R2(StarterAnnual * 275 / 365);
        Assert.Equal(10_663.89m, ProAnnual - credit);
        // ส่วนต่างของสองแบบ = ค่า Pro 90 วันที่แบบ ข ได้เพิ่ม (15,198 × 90/365 = 3,747.45) — มูลค่าต่อวันเท่ากัน
        Assert.Equal(3_747.45m, (ProAnnual - credit) - 6_916.44m);
        Assert.Equal(3_747.45m, PlatformProration.R2(ProAnnual * 90 / 365));
    }

    // ═════════ 233-1 ต่ออายุแพ็กเดิม ═════════

    [Fact]
    public void ต่ออายุก่อนหมด_ต่อจากวันหมดอายุเดิม_ไม่เสียวันที่เหลือ()
    {
        var end = ThaiMidnight(2027, 1, 1);
        var now = ThaiTen(2026, 11, 3);
        var pv = PlanPurchasePreview.ForPurchase(
            Cur(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, SubscriptionStatus.Active, end, 1_490m),
            Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, 1_490m),
            new PlanPurchasePreview.Money(ProAnnual, 0m, ProAnnual), now, vatIncluded: false);
        Assert.Equal("Renewal", pv.Kind);
        Assert.Equal(end, pv.NewStartUtc);
        Assert.Equal(ThaiMidnight(2028, 1, 1), pv.NewEndUtc);
        Assert.Equal("ใช้ได้ถึง 01/01/2570 → 01/01/2571", pv.DateLine);
        Assert.Contains(pv.Notes, n => n.Contains("วันที่เหลือ 59 วันไม่หาย"));   // 03/11 → 01/01 = 59 วัน
    }

    [Fact]
    public void ต่ออายุหลังหมด_ช่วงผ่อนผัน_นับจากวันชำระ()
    {
        var end = ThaiMidnight(2026, 10, 25);
        var now = ThaiTen(2026, 11, 3);
        var pv = PlanPurchasePreview.ForPurchase(
            Cur(SubscriptionPlan.Pro, "Professional", BillingCycle.Monthly, SubscriptionStatus.PastDue, end, 1_490m),
            Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Monthly, 1_490m),
            new PlanPurchasePreview.Money(1_490m, 0m, 1_490m), now, vatIncluded: false);
        Assert.Equal("Renewal", pv.Kind);
        Assert.Equal(now, pv.NewStartUtc);
        Assert.Equal(now.AddMonths(1), pv.NewEndUtc);
        Assert.StartsWith("ใช้ได้ตั้งแต่วันที่ชำระ 03/11/2569 ถึง 03/12/2569", pv.DateLine);
        Assert.Contains(pv.Notes, n => n.Contains("หมดอายุแล้ว"));
    }

    // ═════════ 233-7 ทดลองใช้ → ซื้อ: เริ่มวันชำระ (ไม่ต่อท้ายวันทดลอง) ═════════

    [Fact]
    public void ทดลองใช้แพ็กเดียวกัน_ซื้อแล้วเริ่มวันชำระ_ไม่ต่อท้ายวันทดลอง_reproduce()
    {
        var trialEnd = ThaiMidnight(2026, 11, 20);
        var now = ThaiTen(2026, 11, 3);
        // บั๊กเดิม: ทดลอง Pro แล้วซื้อ Pro ⇒ ต่อท้ายวันทดลอง (แต่ทดลอง Pro แล้วซื้อ Enterprise ⇒ เริ่มวันนี้) และหน้าชำระบอก "เริ่มรอบใหม่จากวันที่ชำระ"
        Assert.Equal(now, PlatformPlanPricing.PeriodStart(PlatformPlanPricing.ChangeKind.Same, true, false, trialEnd, now, isTrial: true));
        // ทิศตรงข้าม: Active ต่ออายุแพ็กเดิม ⇒ ยังต่อจากวันหมดอายุเดิม
        Assert.Equal(trialEnd, PlatformPlanPricing.PeriodStart(PlatformPlanPricing.ChangeKind.Same, true, false, trialEnd, now, isTrial: false));

        var pv = PlanPurchasePreview.ForPurchase(
            Cur(SubscriptionPlan.Pro, "Professional", BillingCycle.Monthly, SubscriptionStatus.Trial, trialEnd, 1_490m),
            Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, 1_490m),
            new PlanPurchasePreview.Money(ProAnnual, 0m, ProAnnual), now, vatIncluded: true);
        Assert.Equal("NewPurchase", pv.Kind);
        Assert.Equal(now, pv.NewStartUtc);
        Assert.Equal(now.AddMonths(12), pv.NewEndUtc);
        Assert.Contains(pv.Notes, n => n.Contains("วันทดลองที่เหลือ 17 วันไม่ถูกนำไปต่อท้าย"));   // ฝ่ายค้าน N6
    }

    // ═════════ E-2 วันหมดอายุค่าแทน (ExpiryNeedsReview) ═════════

    [Fact]
    public void วันหมดอายุค่าแทน100ปี_ต่ออายุเริ่มวันชำระ_ไม่ใช่ต่อจากปี2126_reproduce()
    {
        var now = ThaiTen(2026, 11, 3);
        var placeholder = SubscriptionExpiry.OpenEnded(now.AddDays(-30));
        // เดิม: PeriodStart คืนปี 2126 ⇒ รายการชำระ/แจ้งเตือนบอก "ต่ออายุถึง 2670"
        Assert.Equal(now, PlatformPlanPricing.PeriodStart(PlatformPlanPricing.ChangeKind.Same, true, false, placeholder, now));
        var (start, end) = PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Same, true, false, false, false, false, placeholder, now, 12);
        Assert.Equal(now, start);
        Assert.Equal(now.AddMonths(12), end);
        // ทิศตรงข้าม: วันหมดอายุจริงในอนาคต (เช่น 2 ปี) ⇒ ยังต่อจากวันนั้น
        var real = now.AddYears(2);
        Assert.Equal(real, PlatformPlanPricing.PeriodStart(PlatformPlanPricing.ChangeKind.Same, true, false, real, now));
    }

    // ═════════ E-3 ส่วนต่างจากแพ็กเกจราคา 0 ═════════

    [Fact]
    public void แพ็กเดิมราคา0_ไม่คิดส่วนต่าง_จ่ายเต็มรอบ_reproduce()
    {
        var now = ThaiTen(2026, 11, 3);
        var trialLikeEnd = ThaiMidnight(2026, 11, 20);
        // เดิม: Applies=true ⇒ จ่าย 15,198 × D/C แล้ว "คงวันหมดอายุเดิม" (ถึงวันหมดทดลอง)
        var r = PlatformProration.Upgrade(0m, ProAnnual, 1, trialLikeEnd, now);
        Assert.False(r.Applies);
        Assert.Contains("ไม่มีค่าบริการ", r.NotApplicableReason);
        // ทิศตรงข้าม: แพ็กเดิมมีราคา ⇒ ยังคิดส่วนต่าง
        Assert.True(PlatformProration.Upgrade(590m, 1_490m, 1, trialLikeEnd, now).Applies);
    }

    // ═════════ รอบบิล: ยาวขึ้น (เครดิต) / ตัวคำนวณวันที่ ═════════

    [Fact]
    public void รอบยาวขึ้น_รายเดือนเป็นรายปี_เริ่มวันชำระ_หักเครดิต()
    {
        var end = ThaiMidnight(2026, 11, 18);   // รอบรายเดือน 18/10 → 18/11 (31 วัน) เหลือ 15 วัน
        var now = ThaiTen(2026, 11, 3);
        var cc = PlatformProration.CycleChange(1_490m, 1, ProAnnual, 12, end, now);
        Assert.True(cc.Applies);
        Assert.Equal(15, cc.DaysRemaining);
        Assert.Equal(31, cc.CycleDays);
        Assert.Equal(720.97m, cc.Credit);               // 1,490 × 15/31 = 720.967… ⇒ 720.97
        Assert.Equal(14_477.03m, cc.Amount);
        var pv = PlanPurchasePreview.ForPurchase(
            Cur(SubscriptionPlan.Pro, "Professional", BillingCycle.Monthly, SubscriptionStatus.Active, end, 1_490m),
            Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, 1_490m),
            new PlanPurchasePreview.Money(cc.Amount, 0m, cc.Amount, CycleChange: true, DaysRemaining: cc.DaysRemaining, CycleDays: cc.CycleDays,
                CycleChangeCredit: cc.Credit), now, vatIncluded: true);
        Assert.Equal("CycleChange", pv.Kind);
        Assert.Equal(now.AddMonths(12), pv.NewEndUtc);
        Assert.Equal(cc.NewEndUtc, pv.NewEndUtc);       // ตัวคิดเครดิตและตัวคำนวณวันที่ตรงกัน
        Assert.Equal(ProAnnual, pv.ListPrice);
        Assert.Equal(720.97m, pv.Credit);
        Assert.Equal("ใช้ได้ถึง 18/11/2569 → 03/11/2570", pv.DateLine);
    }

    [Theory]
    [InlineData(2028, 1, 31, 1, 2028, 2, 29)]   // ปีอธิกสุรทิน: 31 ม.ค. + 1 เดือน = 29 ก.พ.
    [InlineData(2027, 1, 31, 1, 2027, 2, 28)]   // ปีปกติ: 28 ก.พ.
    [InlineData(2027, 3, 31, 1, 2027, 4, 30)]   // สิ้นเดือน 31 → 30
    [InlineData(2027, 8, 31, 6, 2028, 2, 29)]   // 6 เดือนข้ามปีอธิกสุรทิน
    [InlineData(2028, 2, 29, 12, 2029, 2, 28)]  // รายปีจาก 29 ก.พ.
    public void ตัวคำนวณวันที่_สิ้นเดือนและปีอธิกสุรทิน(int y, int m, int d, int months, int ey, int em, int ed)
    {
        var paid = ThaiTen(y, m, d);
        var (start, end) = PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Upgrade, false, false, false, false, false, paid.AddDays(-400), paid, months);
        Assert.Equal(paid, start);
        Assert.Equal(ThaiTen(ey, em, ed), end);
    }

    [Fact]
    public void วันที่ไทย_29กพ_ไม่กลายเป็น28กพ_reproduce()
    {
        // เดิม ThaiDateText ใช้ AddYears(543) ⇒ ค.ศ. 2571 ไม่ใช่ปีอธิกสุรทิน ⇒ "28/02/2571" (ผิดหนึ่งวันบนใบเสร็จ/หน้าชำระ)
        Assert.Equal("29/02/2571", QuotaAddOnPricing.ThaiDateText(new DateTime(2028, 2, 29)));
        Assert.Contains("29/02/2571", PlanPurchasePreview.ForAnonymous(Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Monthly, 1_490m), 1_490m,
            ThaiTen(2028, 1, 29)).DateLine);   // 29/01/2571 + 1 เดือน = 29/02/2571 (ข้อความ preview ผ่านตัวแปลงเดียวกัน)
        // ทิศตรงข้าม: วันปกติไม่เปลี่ยน
        Assert.Equal("01/01/2570", QuotaAddOnPricing.ThaiDateText(new DateTime(2027, 1, 1)));
    }

    [Fact]
    public void ส่วนต่างอัปเกรด_ตัวคำนวณวันที่_คงวันหมดอายุเดิม()
    {
        var end = ThaiMidnight(2027, 1, 1);
        var paid = ThaiTen(2026, 4, 1);
        var (start, newEnd) = PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Upgrade, false, false, false, true, false, end, paid, 0);
        Assert.Equal(paid, start);
        Assert.Equal(end, newEnd);
    }

    // ═════════ 233-3 ดาวน์เกรด/รอบสั้นลง — ปฏิเสธพร้อมเหตุ + วันที่ + ทางไปต่อ ═════════

    [Fact]
    public void ดาวน์เกรด_ปฏิเสธ_บอกแพ็กปัจจุบัน_วันหมดอายุ_วันมีผล_และทางไปต่อ()
    {
        var end = ThaiMidnight(2027, 3, 1);
        var now = ThaiTen(2026, 11, 3);
        var cur = Cur(SubscriptionPlan.Enterprise, "Enterprise", BillingCycle.Annual, SubscriptionStatus.Active, end, 4_990m);
        var tgt = Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, 1_490m);
        var decision = PublicCheckoutPolicy.DecideOrder(cur.Status, false, cur.Plan, cur.Cycle, cur.MonthlyPrice, tgt.Plan, tgt.Cycle, tgt.MonthlyPrice, 0m, false, false);
        Assert.Equal(PublicCheckoutPolicy.OrderKind.Refused, decision.Kind);
        var pv = PlanPurchasePreview.Refused(cur, tgt, decision.Refusal, now);
        Assert.False(pv.Allowed);
        Assert.Equal("Refused", pv.Kind);
        Assert.Equal(0m, pv.AmountDue);
        Assert.Contains("บริษัทนี้ใช้ Enterprise ถึง 01/03/2570", pv.RefusalReason);
        Assert.Contains("ลดเป็น Professional ได้ที่หน้าแพ็กเกจในระบบ มีผล 01/03/2570", pv.RefusalReason);
        Assert.Equal(PlanPurchasePreview.PlanTabUrl, pv.NextStepUrl);
        Assert.Equal(end, pv.EffectiveAtUtc);
    }

    [Fact]
    public void รอบสั้นลง_ปฏิเสธ_บอกวันมีผล()
    {
        var end = ThaiMidnight(2027, 3, 1);
        var cur = Cur(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, SubscriptionStatus.Active, end, 1_490m);
        var pv = PlanPurchasePreview.Refused(cur, Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Monthly, 1_490m),
            PublicCheckoutPolicy.ShorterCycleRefusal, ThaiTen(2026, 11, 3));
        Assert.Contains("รายเดือน", pv.RefusalReason);
        Assert.Contains("มีผล 01/03/2570", pv.RefusalReason);
    }

    [Fact]
    public void ต่ออายุพร้อมคูปอง_ปฏิเสธ_บอกให้ลบคูปองและวันที่จะได้()
    {
        var end = ThaiMidnight(2027, 1, 1);
        var cur = Cur(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, SubscriptionStatus.Active, end, 1_490m);
        var pv = PlanPurchasePreview.Refused(cur, Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, 1_490m),
            PublicCheckoutPolicy.RenewalCouponRefusal, ThaiTen(2026, 11, 3));
        Assert.Contains("ลบรหัสคูปอง", pv.RefusalReason);
        Assert.Contains("01/01/2570 → 01/01/2571", pv.RefusalReason);
    }

    [Fact]
    public void ใบต่ออายุออกแล้ว_ปฏิเสธ_พาไปใบแจ้งหนี้()
    {
        var end = ThaiMidnight(2027, 1, 1);
        var cur = Cur(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, SubscriptionStatus.Active, end, 1_490m);
        var pv = PlanPurchasePreview.Refused(cur, Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, 1_490m),
            PublicCheckoutPolicy.RenewalInvoiceIssuedRefusal, ThaiTen(2026, 11, 3));
        Assert.Equal(PlanPurchasePreview.DocsTabUrl, pv.NextStepUrl);
        Assert.Contains("ชำระแล้วใช้ได้ถึง 01/01/2571", pv.RefusalReason);
    }

    [Fact]
    public void เหตุอื่น_ไม่แต่งข้อความ_ส่งต่อเหตุเดิม()
    {
        var cur = Cur(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, SubscriptionStatus.Active, ThaiMidnight(2027, 1, 1), 1_490m);
        var pv = PlanPurchasePreview.Refused(cur, Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, 1_490m), "เหตุเฉพาะ X", ThaiTen(2026, 11, 3));
        Assert.Equal("เหตุเฉพาะ X", pv.RefusalReason);
    }

    [Fact]
    public void ทิศตรงข้าม_อัปเกรด_ไม่ถูกปฏิเสธ()
    {
        var d = PublicCheckoutPolicy.DecideOrder(SubscriptionStatus.Active, false, SubscriptionPlan.Basic, BillingCycle.Annual, 590m,
            SubscriptionPlan.Pro, BillingCycle.Annual, 1_490m, 6_916.44m, false, false);
        Assert.Equal(PublicCheckoutPolicy.OrderKind.Change, d.Kind);
    }

    // ═════════ ผลข้างเคียง + เคสอื่น ═════════

    [Fact]
    public void ต่ออายุขณะตั้งยกเลิกสิ้นรอบและมีดาวน์เกรดรอ_บอกว่าจะถูกยกเลิก()
    {
        var end = ThaiMidnight(2027, 1, 1);
        var pv = PlanPurchasePreview.ForPurchase(
            Cur(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, SubscriptionStatus.Active, end, 1_490m, cancelAtEnd: true, scheduled: SubscriptionPlan.Basic),
            Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, 1_490m),
            new PlanPurchasePreview.Money(ProAnnual, 0m, ProAnnual), ThaiTen(2026, 11, 3), vatIncluded: false);
        Assert.Contains(pv.Notes, n => n.Contains("การยกเลิกเมื่อสิ้นรอบ"));
        Assert.Contains(pv.Notes, n => n.Contains("การเปลี่ยนเป็น Basic"));
    }

    [Fact]
    public void ฟรีถาวร_ซื้อแพ็กจ่ายเงิน_เริ่มวันชำระ_ไม่แสดงปี2669()
    {
        var now = ThaiTen(2026, 11, 3);
        var pv = PlanPurchasePreview.ForPurchase(
            Cur(SubscriptionPlan.FreeTrial, "Free Edition", BillingCycle.Monthly, SubscriptionStatus.Active, SubscriptionExpiry.OpenEnded(now), 0m, permanentFree: true),
            Tgt(SubscriptionPlan.Enterprise, "Enterprise", BillingCycle.Annual, 4_990m),
            new PlanPurchasePreview.Money(50_898m, 0m, 50_898m), now, vatIncluded: true);
        Assert.Equal("NewPurchase", pv.Kind);
        Assert.Null(pv.CurrentEndUtc);
        Assert.Equal(now.AddMonths(12), pv.NewEndUtc);
        Assert.Equal("ใช้ได้ตั้งแต่วันที่ชำระ 03/11/2569 ถึง 03/11/2570", pv.DateLine);
        Assert.DoesNotContain(pv.Lines, l => l.Contains("2669"));
    }

    [Fact]
    public void ดาวน์เกรดในหน้าแพ็กเกจ_ไม่ต้องจ่าย_มีผลวันครบรอบ_และใบต่ออายุออกแล้ว_ทำไม่ได้()
    {
        var end = ThaiMidnight(2027, 1, 1);
        var cur = Cur(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, SubscriptionStatus.Active, end, 1_490m);
        var tgt = Tgt(SubscriptionPlan.Basic, "Starter", BillingCycle.Annual, 590m);
        var ok = PlanPurchasePreview.ForScheduled(cur, tgt, end, null, ThaiTen(2026, 11, 3));
        Assert.True(ok.Allowed);
        Assert.Equal(0m, ok.AmountDue);
        Assert.Contains("01/01/2570", ok.DateLine);
        var blocked = PlanPurchasePreview.ForScheduled(cur, tgt, end, "ออกใบแจ้งหนี้ต่ออายุแล้ว", ThaiTen(2026, 11, 3));
        Assert.False(blocked.Allowed);
        Assert.Equal(PlanPurchasePreview.DocsTabUrl, blocked.NextStepUrl);
    }

    [Fact]
    public void ยังไม่ล็อกอิน_ซื้อใหม่นับจากวันนี้()
    {
        var now = ThaiTen(2026, 11, 3);
        var pv = PlanPurchasePreview.ForAnonymous(Tgt(SubscriptionPlan.Enterprise, "Enterprise", BillingCycle.Annual, 4_990m), 50_898m, now);
        Assert.Equal(now.AddMonths(12), pv.NewEndUtc);
        Assert.Contains("03/11/2569 ถึง 03/11/2570", pv.DateLine);
    }

    [Fact]
    public void ใบกำกับ_เครดิตเปลี่ยนรอบเป็นส่วนลดก่อนVAT_ส่วนต่างอัปเกรดไม่ซ้ำ()
    {
        Assert.Equal(820.97m, PlanPurchasePreview.InvoiceDiscountGross(100m, true, 720.97m));
        Assert.Equal(100m, PlanPurchasePreview.InvoiceDiscountGross(100m, false, 720.97m));   // ทิศตรงข้าม: ไม่ใช่เปลี่ยนรอบ ⇒ คูปองอย่างเดียว
        Assert.Equal(0m, PlanPurchasePreview.InvoiceDiscountGross(0m, true, null));
        // ฐานบนใบ: เงินรับ 14,477.03 + เครดิต 720.97 = ราคารอบใหม่ 15,198 (รวม VAT) ⇒ ฐานเต็ม 14,203.74 · ส่วนลด 673.81 · ฐานสุทธิ 13,529.93 · VAT 947.10
        var (full, disc, net, vat) = PlatformCouponRule.SplitDiscounted(14_477.03m, PlanPurchasePreview.InvoiceDiscountGross(0m, true, 720.97m), true);
        Assert.Equal(14_203.74m, full);
        Assert.Equal(673.81m, disc);
        Assert.Equal(13_529.93m, net);
        Assert.Equal(947.10m, vat);
        Assert.Equal(net + vat, 14_477.03m);
        Assert.Equal(full - disc, net);
    }

    // ═════════ ฝ่ายค้านรอบ 233 ═════════

    [Fact]
    public void B2_วันรับเงินย้อนหลังจนรอบจบก่อนอนุมัติ_นับจากวันอนุมัติ()
    {
        var approved = ThaiTen(2026, 11, 3);
        var paid = approved.AddDays(-40);                 // สลิปลงวันที่ย้อน 40 วัน (แอดมินบันทึกเอง)
        var oldEnd = approved.AddDays(-60);               // หมดอายุไปแล้ว 60 วัน
        var r = PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Same, true, false, false, false, false, oldEnd, paid, 1, approvedAtUtc: approved);
        Assert.True(r.StartedAtApproval);                 // เดิม: วันรับเงิน + 1 เดือน = 10 วันก่อนอนุมัติ ⇒ จ่ายแล้วได้รอบที่หมดไปแล้ว
        Assert.Equal(approved, r.StartUtc);
        Assert.Equal(approved.AddMonths(1), r.EndUtc);
        Assert.Contains("นับจากวันอนุมัติ", PlanPurchasePreview.BackdatedNote);
        // เปลี่ยนรอบบิลก็ใช้กติกาเดียวกัน
        Assert.True(PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Same, false, false, false, false, true, oldEnd, paid, 1, approvedAtUtc: approved).StartedAtApproval);
    }

    [Fact]
    public void B2_ทิศตรงข้าม_โอนก่อนหมดอายุ_อนุมัติหลังหมด_ยังต่อจากวันหมดอายุเดิม_233_1b()
    {
        var approved = ThaiTen(2026, 11, 3);
        var oldEnd = approved.AddDays(-3);                // หมดอายุ 3 วันก่อนแอดมินตรวจ
        var paid = approved.AddDays(-5);                  // โอนก่อนหมดอายุ
        var r = PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Same, true, false, false, false, false, oldEnd, paid, 1, approvedAtUtc: approved);
        Assert.False(r.StartedAtApproval);
        Assert.Equal(oldEnd, r.StartUtc);                 // ต่อจากวันหมดอายุเดิม (ตรงกับบัญชี — ไม่มีช่องว่าง/ไม่ซ้อน)
        Assert.Equal(oldEnd.AddMonths(1), r.EndUtc);
        // ส่วนต่างอัปเกรดไม่ใช้กติกานี้ (รอบที่คิดจบแล้ว = StaleReason พักให้แอดมิน)
        Assert.False(PlanPurchasePreview.PaidPeriod(PlatformPlanPricing.ChangeKind.Upgrade, false, false, false, true, false, oldEnd, paid, 0, approvedAtUtc: approved).StartedAtApproval);
    }

    [Fact]
    public void S2_ฐานเครดิต_ก่อนหักณที่จ่าย_ตัวอย่างเจ้าของหัก3เปอร์เซ็นต์_ยังได้6916_44()
    {
        var end = ThaiMidnight(2027, 1, 1);
        var now = ThaiTen(2026, 4, 1);
        // ลูกค้านิติบุคคลหัก ณ ที่จ่าย 3% ของฐาน 5,624.30 = 168.73 ⇒ เงินเข้า 5,849.27
        var net = 5_849.27m; var wht = 168.73m;
        var basis = PlatformProration.OldPriceBasis(net + wht, 0m);
        Assert.Equal(StarterAnnual, basis);
        Assert.Equal(6_916.44m, PlatformProration.Upgrade(basis, ProAnnual, 12, end, now).Amount);
        // บั๊กเดิม: ฐาน = เงินที่เข้า ⇒ คิดส่วนต่างเกิน
        Assert.Equal(7_043.56m, PlatformProration.Upgrade(PlatformProration.OldPriceBasis(net, 0m), ProAnnual, 12, end, now).Amount);
    }

    [Fact]
    public void S3_รายการApprovedยอด0_เครดิต0_ไม่ตกไปราคาแม่แบบ()
    {
        Assert.Equal(0m, PlatformProration.OldPriceBasis(0m, 6_018m));
        Assert.Equal(6_018m, PlatformProration.OldPriceBasis(null, 6_018m));   // ทิศตรงข้าม: ไม่มีรายการ Approved เลย ⇒ ราคาแม่แบบ
        var cc = PlatformProration.CycleChange(PlatformProration.OldPriceBasis(0m, 590m), 1, ProAnnual, 12, ThaiMidnight(2026, 11, 18), ThaiTen(2026, 11, 3));
        Assert.Equal(0m, cc.Credit);
        Assert.Equal(ProAnnual, cc.Amount);
    }

    [Fact]
    public void N2_เครดิตเกินค่ารอบใหม่_แสดงราคาจริงและบอกว่าทำไม่ได้()
    {
        var pv = PlanPurchasePreview.ForPurchase(
            Cur(SubscriptionPlan.Pro, "Professional", BillingCycle.Monthly, SubscriptionStatus.Active, ThaiMidnight(2026, 11, 30), 1_490m),
            Tgt(SubscriptionPlan.Pro, "Professional", BillingCycle.Quarterly, 1_490m),
            new PlanPurchasePreview.Money(0m, 0m, 0m, CycleChange: true, DaysRemaining: 27, CycleDays: 31, CycleChangeCredit: 5_000m, NewCyclePrice: 4_248m),
            ThaiTen(2026, 11, 3), vatIncluded: true);
        Assert.Equal(4_248m, pv.ListPrice);       // ไม่ใช่ ฿5,000 (= ยอด 0 + เครดิต)
        Assert.Equal(4_248m, pv.Credit);
        Assert.False(pv.Allowed);
        Assert.Equal(PlanPurchasePreview.CreditCoversAllRefusal, pv.RefusalReason);
        Assert.Contains(pv.Notes, n => n.Contains("ส่วนเกินไม่คืน"));
    }

    [Fact]
    public void N3_ออกใบกำกับภาษีเมื่อจดVATและเลขผู้เสียภาษีถูกต้องเท่านั้น()
    {
        Assert.True(PlanPurchasePreview.IssuesTaxInvoice(true, "0105556000009"));
        Assert.False(PlanPurchasePreview.IssuesTaxInvoice(true, "0105556000002"));   // checksum ผิด ⇒ ตัวออกใบออกใบเสร็จธรรมดา ⇒ preview ต้องไม่บอก "รวม VAT"
        Assert.False(PlanPurchasePreview.IssuesTaxInvoice(false, "0105556000009"));
    }

    [Fact]
    public void N7_วันหมดอายุค่าแทน_ดาวน์เกรดมีผลทันที_ไม่เลื่อนไปปี2126_และไม่ตั้งเวลา()
    {
        var now = ThaiTen(2026, 11, 3);
        var placeholder = now.AddYears(100);
        Assert.Equal(now, PlatformPlanPricing.ScheduledChangeEffectiveAt(false, placeholder, now));          // เดิม: ปี 2126
        Assert.False(PlatformPlanPricing.DeferToCycleEnd(PlatformPlanPricing.ChangeKind.Downgrade, SubscriptionStatus.Active, placeholder, now));
        // ทิศตรงข้าม: วันครบรอบจริง ⇒ ยังเลื่อนไปวันนั้น
        Assert.Equal(now.AddDays(20), PlatformPlanPricing.ScheduledChangeEffectiveAt(false, now.AddDays(20), now));
        Assert.True(PlatformPlanPricing.DeferToCycleEnd(PlatformPlanPricing.ChangeKind.Downgrade, SubscriptionStatus.Active, now.AddDays(20), now));
    }

    [Fact]
    public void S5_resumeรายการส่วนต่างเก่า_ยอดไม่ตรงคิดใหม่_มีintentแล้วใช้รายการเดิม()
    {
        Assert.True(PublicCheckoutPolicy.MustRequote(true, 7_000m, 6_916.44m, hasIntent: false));
        Assert.False(PublicCheckoutPolicy.MustRequote(true, 7_000m, 6_916.44m, hasIntent: true));    // เงินอาจกำลังเข้า — ห้ามสร้างซ้อน
        Assert.False(PublicCheckoutPolicy.MustRequote(true, 6_916.44m, 6_916.44m, hasIntent: false)); // ยอดเดิมยังถูก ⇒ resume
        Assert.False(PublicCheckoutPolicy.MustRequote(false, 15_198m, 1m, hasIntent: false));         // ราคาเต็ม/ต่ออายุ ไม่ใช่เส้นนี้
    }

    [Fact]
    public void N5_ข้อความป้ายต่ำกว่าแพ็กปัจจุบัน_จากเซิร์ฟเวอร์_มีวันมีผล()
    {
        var note = PlanPurchasePreview.LowerPlanNote("บริษัท ก", "Enterprise", false, ThaiMidnight(2027, 3, 1), ThaiTen(2026, 11, 3));
        Assert.Contains("บริษัท ก ใช้ Enterprise", note);
        Assert.Contains("มีผล 01/03/2570", note);
    }
}
