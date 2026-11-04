using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 237 (K-1 · คำตัดสินเจ้าของ 2026-11-04 "ทำให้ระบบรองรับทั้ง 2 ทาง สามารถเลือกได้" · DECISIONS 237-1…237-7) — อัปเกรดกลางรอบ 2 แบบ ตัวตัดสินเดียว
/// <see cref="PlatformProration.UpgradeByMode"/>: แบบ ก คงวันหมดอายุ (ส่วนต่าง) · แบบ ข เริ่มรอบใหม่ (เต็มรอบ − มูลค่าคงเหลือ) · ค่าตั้งแพลตฟอร์มเปิดแบบไหน
/// (<see cref="PlatformProration.ResolveMode"/>) · อนุมัติใช้แบบที่ประทับตอนซื้อ (<see cref="PlatformProration.ApprovalRouteOf"/>) — ตัวเลขตัวอย่างของเจ้าของตรวจมือได้ ·
/// ทิศตรงข้าม: แบบ ก ได้ผลเดิมก่อนรอบ 237 ทุกตัวเลข (ล็อกด้วยตัวเลขชุดเดียวกับ <c>PlanPurchasePreviewRound233Tests</c>)
/// </summary>
public class UpgradeModeRound237Tests
{
    private static DateTime ThaiMidnight(int y, int m, int d) => new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc).AddHours(-7);
    private static DateTime ThaiTen(int y, int m, int d) => new(y, m, d, 3, 0, 0, DateTimeKind.Utc);
    private static string Be(DateTime utc) => QuotaAddOnPricing.ThaiDateText(utc.AddHours(7).Date);

    // ราคาจริงใน Data/SeedPlanTemplates.cs: Starter (Basic) รายปี 6,018 · Professional (Pro) รายปี 15,198
    private const decimal StarterAnnual = 6_018m, ProAnnual = 15_198m;
    private static readonly DateTime End = ThaiMidnight(2027, 1, 1);   // จ่าย Starter 01/01/2569 ⇒ ใช้ได้ถึง 01/01/2570
    private static readonly DateTime Now = ThaiTen(2026, 4, 1);         // ใช้ไป 3 เดือน (01/04/2569) ⇒ เหลือ 275/365 วัน

    private static PlanPurchasePreview.Current Cur() => new(SubscriptionPlan.Basic, "Starter", BillingCycle.Annual, SubscriptionStatus.Active, false, End, 590m,
        false, null, null);
    private static PlanPurchasePreview.Target Tgt() => new(SubscriptionPlan.Pro, "Professional", BillingCycle.Annual, 1_490m);

    // ═════════ ตัวอย่างของเจ้าของ: Standard (Starter) รายปีจ่ายแล้ว ใช้ไป 3 เดือน → Pro ═════════

    [Fact]
    public void เจ้าของ_แบบกคงวันหมดอายุ_จ่าย6916_44_ใช้ได้ถึง01_01_2570()
    {
        var up = PlatformProration.UpgradeByMode(PlatformUpgradeMode.KeepExpiry, StarterAnnual, ProAnnual, 12, End, Now);
        Assert.Equal(PlatformProration.UpgradeRoute.KeepExpiryProration, up.Route);
        Assert.True(up.Allowed);
        Assert.Equal(275, up.DaysRemaining);
        Assert.Equal(365, up.CycleDays);
        Assert.Equal(6_916.44m, up.Amount);          // (15,198 − 6,018) × 275/365 = 6,916.438… ⇒ 6,916.44
        Assert.Equal(4_534.11m, up.Credit);          // 6,018 × 275/365 = 4,534.109… (แสดงในบรรทัดอธิบาย)
        Assert.Equal(End, up.NewEndUtc);             // วันหมดอายุคงเดิม
        Assert.Equal("01/01/2570", Be(up.NewEndUtc));
    }

    [Fact]
    public void เจ้าของ_แบบขเริ่มรอบใหม่_จ่าย10663_89_เครดิต4534_11_ใช้ได้ถึง01_04_2570()
    {
        var up = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, StarterAnnual, ProAnnual, 12, End, Now);
        Assert.Equal(PlatformProration.UpgradeRoute.RestartWithCredit, up.Route);
        Assert.True(up.Allowed);
        Assert.Equal(ProAnnual, up.NewCyclePrice);
        Assert.Equal(4_534.11m, up.Credit);          // เครดิตสูตรเดียวกับแบบ ก (ยอดที่จ่ายจริง × D/C)
        Assert.Equal(10_663.89m, up.Amount);         // 15,198 − 4,534.11
        Assert.Equal(275, up.DaysRemaining);
        Assert.Equal(365, up.CycleDays);
        Assert.Equal(PlatformPlanPricing.AddCycleMonths(Now, 12), up.NewEndUtc);   // รอบใหม่ 12 เดือนนับจากวันชำระ
        Assert.Equal("01/04/2570", Be(up.NewEndUtc));
        // ส่วนต่างสองแบบ = ค่า Pro 90 วันที่แบบ ข ได้เพิ่ม (15,198 × 90/365 = 3,747.45) — มูลค่าต่อวันเท่ากัน (ตาราง K-1 ใน LICENSE_CASE_MATRIX)
        var keep = PlatformProration.UpgradeByMode(PlatformUpgradeMode.KeepExpiry, StarterAnnual, ProAnnual, 12, End, Now);
        Assert.Equal(3_747.45m, up.Amount - keep.Amount);
    }

    [Fact]
    public void เจ้าของ_แบบข_previewบอกยอด_เครดิต_วันหมดอายุใหม่_พศ()
    {
        var up = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, StarterAnnual, ProAnnual, 12, End, Now);
        var pv = PlanPurchasePreview.ForPurchase(Cur(), Tgt(), new PlanPurchasePreview.Money(up.Amount, 0m, up.Amount, CycleChange: true,
            DaysRemaining: up.DaysRemaining, CycleDays: up.CycleDays, CycleChangeCredit: up.Credit, NewCyclePrice: up.NewCyclePrice,
            UpgradeMode: PlatformUpgradeMode.RestartCycle), Now, vatIncluded: true);
        Assert.True(pv.Allowed);
        Assert.Equal(15_198m, pv.ListPrice);
        Assert.Equal(4_534.11m, pv.Credit);
        Assert.Equal(10_663.89m, pv.AmountDue);
        Assert.Equal(pv.AmountDue, pv.ListPrice - pv.Credit);   // ผลรวมบรรทัด = ยอดที่คิดจริงพอดี
        Assert.Equal(up.NewEndUtc, pv.NewEndUtc);                 // วันที่ preview บอก = วันที่ตัวตัดสินคิด (PaidPeriod ตัวเดียว)
        Assert.False(pv.EndUnchanged);
        Assert.Contains("เริ่มรอบใหม่", pv.Headline);
        Assert.Contains("01/04/2570", pv.DateLine);
        Assert.Contains("เดิมถึง 01/01/2570", pv.DateLine);
        Assert.Contains(pv.Lines, l => l.Contains("รวม VAT"));
    }

    // ═════════ 237-3 เครดิต ≥ ราคาเต็มรอบใหม่ (แบบ ข) ⇒ ปฏิเสธ ไม่สร้างยอด 0 ═════════

    [Fact]
    public void แบบข_เครดิตมากกว่าราคาเต็ม_ปฏิเสธ_แบบกยังได้เส้นเดิม()
    {
        // ฐานเครดิต = ยอดที่จ่ายจริง (เช่น แอดมินแก้ยอด/ราคาพิเศษเดิมสูง) 30,000 × 275/365 = 22,602.74 > 15,198
        var rc = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, 30_000m, ProAnnual, 12, End, Now);
        Assert.Equal(PlatformProration.UpgradeRoute.Refused, rc.Route);
        Assert.False(rc.Allowed);
        Assert.Equal(PlatformProration.RestartCreditCoversAllRefusal, rc.RefusalReason);
        Assert.Equal(0m, rc.Amount);
        Assert.Equal(22_602.74m, rc.Credit);
        // ทิศตรงข้าม: แบบ ก ของข้อมูลชุดเดียวกัน = ส่วนต่าง ≤ 0 ⇒ เส้นสำรองเดิม (ราคาเต็ม เริ่มวันชำระ) — ไม่ถูกปฏิเสธ
        var keep = PlatformProration.UpgradeByMode(PlatformUpgradeMode.KeepExpiry, 30_000m, ProAnnual, 12, End, Now);
        Assert.Equal(PlatformProration.UpgradeRoute.FullRestart, keep.Route);
        Assert.True(keep.Allowed);
        Assert.Equal(PlatformProration.Upgrade(30_000m, ProAnnual, 12, End, Now).NotApplicableReason, keep.Note);
        // preview ของแบบ ข ที่ถูกปฏิเสธ: ชำระไม่ได้ + เหตุเดียวกับด่านสร้างรายการ
        var pv = PlanPurchasePreview.ForPurchase(Cur(), Tgt(), new PlanPurchasePreview.Money(0m, 0m, 0m, CycleChange: true,
            DaysRemaining: rc.DaysRemaining, CycleDays: rc.CycleDays, CycleChangeCredit: rc.Credit, NewCyclePrice: rc.NewCyclePrice,
            UpgradeMode: PlatformUpgradeMode.RestartCycle), Now, vatIncluded: true);
        Assert.False(pv.Allowed);
        Assert.Equal(PlatformProration.RestartCreditCoversAllRefusal, pv.RefusalReason);
    }

    [Fact]
    public void แบบข_เครดิตเท่าราคาเต็มพอดี_ปฏิเสธ_เครดิตน้อยกว่า1สตางค์ไม่ปฏิเสธ()
    {
        // วันแรกของรอบ (เหลือ 365/365) + ยอดเดิม = ราคาใหม่ ⇒ เครดิต = ราคาเต็ม ⇒ ยอด 0 ⇒ ปฏิเสธ (ไม่สร้างรายการยอด 0)
        var first = ThaiTen(2026, 1, 1);
        var eq = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, ProAnnual, ProAnnual, 12, End, first);
        Assert.Equal(PlatformProration.UpgradeRoute.Refused, eq.Route);
        Assert.Equal(ProAnnual, eq.Credit);
        // ทิศตรงข้าม: ต่ำกว่าราคาเต็ม 1 สตางค์ ⇒ จ่าย 0.01 ได้
        var under = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, ProAnnual - 0.01m, ProAnnual, 12, End, first);
        Assert.Equal(PlatformProration.UpgradeRoute.RestartWithCredit, under.Route);
        Assert.Equal(0.01m, under.Amount);
    }

    [Fact]
    public void แบบข_ไม่มีมูลค่าคงเหลือให้หัก_เต็มรอบเริ่มวันชำระ_ไม่นับวันหมดอายุค่าแทนเป็นเครดิต()
    {
        // แพ็กเดิมไม่มีวันหมดอายุ (ค่าแทน +100 ปี) ⇒ ห้ามคิดเครดิตเต็มรอบจาก D ที่ถูกบีบเท่า C
        var open = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, StarterAnnual, ProAnnual, 12, Now.AddYears(100), Now);
        Assert.Equal(PlatformProration.UpgradeRoute.FullRestart, open.Route);
        Assert.Equal(0m, open.Credit);
        Assert.Equal(ProAnnual, open.Amount);
        // แพ็กเดิมไม่มีค่าบริการ ⇒ เต็มรอบ
        var free = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, 0m, ProAnnual, 12, End, Now);
        Assert.Equal(PlatformProration.UpgradeRoute.FullRestart, free.Route);
        // ทดลองใช้ ⇒ เต็มรอบ (เหตุจากตัวคิดเดิม)
        var trial = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, StarterAnnual, ProAnnual, 12, End, Now, isTrial: true);
        Assert.Equal(PlatformProration.UpgradeRoute.FullRestart, trial.Route);
        Assert.Contains("ทดลองใช้", trial.Note);
    }

    // ═════════ ทิศตรงข้าม: แบบ ก = ผลเดิมก่อนรอบ 237 ทุกตัวเลข ═════════

    [Theory]
    [InlineData(6018, 15198, 12, 0, false, false)]      // ตัวอย่างเจ้าของ
    [InlineData(590, 1490, 1, 0, false, false)]         // รายเดือน
    [InlineData(15198, 6018, 12, 0, false, false)]      // ส่วนต่าง ≤ 0 (เส้นสำรอง)
    [InlineData(6018, 15198, 12, 0, true, false)]       // ทดลองใช้
    [InlineData(0, 15198, 12, 0, false, false)]         // แพ็กเดิมไม่มีค่าบริการ
    [InlineData(6018, 15198, 12, 275, false, false)]    // หมดอายุวันนี้ (เหลือ < 1 วัน)
    [InlineData(6018, 15198, 12, 0, false, true)]       // ฟรีถาวร
    public void แบบก_ผลเท่ากับตัวคิดเดิมทุกตัวเลข(int oldPrice, int newPrice, int months, int daysLater, bool trial, bool permanentFree)
    {
        var now = Now.AddDays(daysLater);
        var before = PlatformProration.Upgrade(oldPrice, newPrice, months, End, now, isTrial: trial, isPermanentFree: permanentFree);
        var up = PlatformProration.UpgradeByMode(PlatformUpgradeMode.KeepExpiry, oldPrice, newPrice, months, End, now, isTrial: trial, isPermanentFree: permanentFree);
        Assert.Equal(before, up.Proration);
        Assert.Equal(before.Applies ? PlatformProration.UpgradeRoute.KeepExpiryProration : PlatformProration.UpgradeRoute.FullRestart, up.Route);
        if (before.Applies)
        {
            Assert.Equal(before.Amount, up.Amount);
            Assert.Equal(End, up.NewEndUtc);
        }
        else Assert.Equal(before.NotApplicableReason, up.Note);
    }

    [Fact]
    public void เปลี่ยนรอบบิลเดิม_รอบเท่ากันยังไม่ใช่เส้นเปลี่ยนรอบ_เปิดเฉพาะผ่านแบบข()
    {
        // ทิศตรงข้ามของ sameCycleUpgrade: ผู้เรียกเดิม (เปลี่ยนรอบบิล) ยังปฏิเสธรอบเท่าเดิม/สั้นลง
        Assert.False(PlatformProration.CycleChange(StarterAnnual, 12, ProAnnual, 12, End, Now).Applies);
        Assert.False(PlatformProration.CycleChange(StarterAnnual, 12, ProAnnual, 1, End, Now, sameCycleUpgrade: true).Applies);
        Assert.True(PlatformProration.CycleChange(StarterAnnual, 12, ProAnnual, 12, End, Now, sameCycleUpgrade: true).Applies);
        // รายเดือน → รายปี (เส้นรอบ 231) ตัวเลขเดิม
        var cc = PlatformProration.CycleChange(590m, 1, 6_018m, 12, ThaiMidnight(2026, 4, 16), Now);
        Assert.True(cc.Applies);
        Assert.Equal(cc.Amount, PlatformProration.R2(6_018m - cc.Credit));
    }

    // ═════════ 237-1 ค่าตั้งเปิดแบบไหน · ขอแบบที่ไม่เปิด ⇒ ปฏิเสธ ═════════

    [Fact]
    public void ค่าตั้ง_ค่าตั้งต้นลูกค้าเลือก_เลือกแบบกไว้ให้_ขอแบบที่ไม่เปิดถูกปฏิเสธ()
    {
        Assert.Equal(new[] { PlatformUpgradeMode.KeepExpiry, PlatformUpgradeMode.RestartCycle }, PlatformProration.OfferedModes(PlatformUpgradeModeOffer.CustomerChooses));
        Assert.Equal(PlatformUpgradeModeOffer.CustomerChooses, default(PlatformUpgradeModeOffer));   // คอลัมน์ DEFAULT 0 = ลูกค้าเลือก (พฤติกรรมเดิม)
        Assert.Equal((PlatformUpgradeMode?)PlatformUpgradeMode.KeepExpiry, PlatformProration.ResolveMode(PlatformUpgradeModeOffer.CustomerChooses, null).Mode);
        Assert.Equal((PlatformUpgradeMode?)PlatformUpgradeMode.RestartCycle, PlatformProration.ResolveMode(PlatformUpgradeModeOffer.CustomerChooses, PlatformUpgradeMode.RestartCycle).Mode);

        var refused = PlatformProration.ResolveMode(PlatformUpgradeModeOffer.KeepExpiryOnly, PlatformUpgradeMode.RestartCycle);
        Assert.Null(refused.Mode);
        Assert.Contains("ไม่ได้เปิด", refused.Refusal);
        var refused2 = PlatformProration.ResolveMode(PlatformUpgradeModeOffer.RestartCycleOnly, PlatformUpgradeMode.KeepExpiry);
        Assert.Null(refused2.Mode);
        Assert.NotNull(refused2.Refusal);
        // ทิศตรงข้าม: ไม่ส่ง = ตัวที่เปิด · ส่งตัวที่เปิด = ผ่าน
        Assert.Equal((PlatformUpgradeMode?)PlatformUpgradeMode.RestartCycle, PlatformProration.ResolveMode(PlatformUpgradeModeOffer.RestartCycleOnly, null).Mode);
        Assert.Null(PlatformProration.ResolveMode(PlatformUpgradeModeOffer.KeepExpiryOnly, PlatformUpgradeMode.KeepExpiry).Refusal);
    }

    // ═════════ 237-5 อนุมัติใช้แบบที่ประทับตอนซื้อ ไม่ใช่ค่าตั้งปัจจุบัน ═════════

    [Fact]
    public void อนุมัติ_ใช้แบบที่ประทับบนรายการ_ไม่มีพารามิเตอร์ค่าตั้ง()
    {
        // ตัวตัดสินเส้นอนุมัติรับเฉพาะสิ่งที่อยู่บนรายการ — ค่าตั้งเปลี่ยนเป็น "คงวันหมดอายุเท่านั้น" ระหว่างรอเงินเข้าไม่มีทางเข้ามาเปลี่ยนผล
        Assert.Equal(PlatformProration.ApprovalRoute.RestartCycleUpgrade, PlatformProration.ApprovalRouteOf(PlatformUpgradeMode.RestartCycle, false, true));
        Assert.Equal(PlatformProration.ApprovalRoute.KeepExpiryProration, PlatformProration.ApprovalRouteOf(PlatformUpgradeMode.KeepExpiry, true, false));
        // แถวก่อนรอบ 237 (UpgradeMode null) = ตามธงเดิม
        Assert.Equal(PlatformProration.ApprovalRoute.KeepExpiryProration, PlatformProration.ApprovalRouteOf(null, true, false));
        Assert.Equal(PlatformProration.ApprovalRoute.CycleChange, PlatformProration.ApprovalRouteOf(null, false, true));
        Assert.Equal(PlatformProration.ApprovalRoute.General, PlatformProration.ApprovalRouteOf(null, false, false));
        // อัปเกรดที่ตกเส้นราคาเต็ม (ประทับแบบไว้แต่ไม่มีธง) = เส้นซื้อทั่วไป (เริ่มวันชำระ)
        Assert.Equal(PlatformProration.ApprovalRoute.General, PlatformProration.ApprovalRouteOf(PlatformUpgradeMode.RestartCycle, false, false));
        var p = typeof(PlatformProration).GetMethod(nameof(PlatformProration.ApprovalRouteOf))!.GetParameters();
        Assert.DoesNotContain(p, x => x.ParameterType == typeof(PlatformUpgradeModeOffer));
    }

    // ═════════ ข้อความใบเสร็จ/หน้าแอดมิน ═════════

    [Fact]
    public void ข้อความใบเสร็จแบบข_บอกแบบ_ช่วงรอบใหม่_เครดิต_และบรรทัดเทียบของแอดมิน()
    {
        var up = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, StarterAnnual, ProAnnual, 12, End, Now);
        var desc = PlatformProration.RestartUpgradeDescription("Basic", "Pro", 12, up.Credit, up.DaysRemaining, up.CycleDays, End, Now, up.NewEndUtc);
        Assert.Contains("แบบเริ่มรอบใหม่ 12 เดือน", desc);
        Assert.Contains("01/04/2569–01/04/2570", desc);
        Assert.Contains("4,534.11", desc);
        Assert.Contains("275/365", desc);
        Assert.Contains("นับจากวันที่ชำระ", PlatformProration.RestartUpgradeDescription("Basic", "Pro", 12, up.Credit, 275, 365, End));
        Assert.Contains("10,663.89", PlatformProration.ModeSummaryLine(up));
        Assert.Contains("01/04/2570", PlatformProration.ModeSummaryLine(up));
        Assert.Contains("6,916.44", PlatformProration.ModeSummaryLine(PlatformProration.UpgradeByMode(PlatformUpgradeMode.KeepExpiry, StarterAnnual, ProAnnual, 12, End, Now)));
    }

    // ═════════ ฝ่ายค้าน 237 ═════════

    [Fact]
    public void N3_บรรทัดเทียบแบบก_ไม่ขึ้นมูลค่าคงเหลือเป็นยอดที่หัก_และบอกแบบที่ค่าตั้งไม่เปิด()
    {
        var keep = PlatformProration.ModeSummaryLine(PlatformProration.UpgradeByMode(PlatformUpgradeMode.KeepExpiry, StarterAnnual, ProAnnual, 12, End, Now));
        Assert.DoesNotContain("มูลค่าคงเหลือ", keep);
        Assert.Contains("ส่วนต่าง", keep);
        Assert.DoesNotContain("ไม่เปิด", keep);
        var restart = PlatformProration.ModeSummaryLine(PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, StarterAnnual, ProAnnual, 12, End, Now), offered: false);
        Assert.Contains("มูลค่าคงเหลือแพ็กเดิม ฿4,534.11", restart);
        Assert.Contains("ค่าตั้งปัจจุบันไม่เปิด", restart);
    }

    [Fact]
    public void S3_237_8_เครดิตแบบข_เงินเข้าวันเดียวกันผ่าน_เงินเข้าช้า3วันได้เหตุพร้อมยอดเดิมและยอดที่ถูก()
    {
        Assert.Equal((275, 365), PlatformProration.RemainingDays(End, 12, Now));   // ตัวนับเดียวกับที่ Upgrade/CycleChange ใช้
        var quoted = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, StarterAnnual, ProAnnual, 12, End, Now);
        // ทิศตรงข้าม: เงินเข้าวันเดียวกัน (คนละเวลา) ⇒ ผ่าน
        var sameDay = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, StarterAnnual, ProAnnual, 12, End, Now.AddHours(8));
        Assert.Null(PlatformProration.RestartCreditDrift(sameDay, quoted.DaysRemaining, quoted.Credit, quoted.Amount, Now.AddHours(8)));
        // เงินเข้าช้า 3 วัน ⇒ เหลือ 272 วัน · เครดิตที่ถูก 6,018 × 272/365 = 4,484.68 · ยอด 10,713.32 (เดิม 10,663.89)
        var late = Now.AddDays(3);
        var atPay = PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, StarterAnnual, ProAnnual, 12, End, late);
        Assert.Equal(272, atPay.DaysRemaining);
        Assert.Equal(4_484.68m, atPay.Credit);
        var why = PlatformProration.RestartCreditDrift(atPay, quoted.DaysRemaining, quoted.Credit, quoted.Amount, late);
        Assert.NotNull(why);
        Assert.Contains("04/04/2569", why);
        Assert.Contains("10,663.89", why);
        Assert.Contains("10,713.32", why);
        Assert.Contains("4,484.68", why);
        // ณ วันชำระแบบ ข ใช้ไม่ได้แล้ว (เช่น หมดอายุวันนั้น) ⇒ มีเหตุ
        Assert.NotNull(PlatformProration.RestartCreditDrift(
            PlatformProration.UpgradeByMode(PlatformUpgradeMode.RestartCycle, StarterAnnual, ProAnnual, 12, End, End.AddHours(1)), 275, quoted.Credit, quoted.Amount, End.AddHours(1)));
    }

    [Fact]
    public void S4_DecideOrder_อัปเกรดที่แบบใช้ไม่ได้_ไม่ตีเป็นไม่มียอด_ธงไม่ยกเว้นดาวน์เกรด()
    {
        var plain = PublicCheckoutPolicy.DecideOrder(SubscriptionStatus.Active, false, SubscriptionPlan.Basic, BillingCycle.Annual, 590m,
            SubscriptionPlan.Pro, BillingCycle.Annual, 1_490m, 0m, false);
        Assert.Equal(PublicCheckoutPolicy.OrderKind.Refused, plain.Kind);   // ทิศเดิม: ยอด 0 ไม่มีธง = ไม่มียอดต้องชำระ
        Assert.Equal(PublicCheckoutPolicy.NoPaymentChangeRefusal, plain.Refusal);
        var blocked = PublicCheckoutPolicy.DecideOrder(SubscriptionStatus.Active, false, SubscriptionPlan.Basic, BillingCycle.Annual, 590m,
            SubscriptionPlan.Pro, BillingCycle.Annual, 1_490m, 0m, false, upgradeBlocked: true);
        Assert.Equal(PublicCheckoutPolicy.OrderKind.Change, blocked.Kind);
        var down = PublicCheckoutPolicy.DecideOrder(SubscriptionStatus.Active, false, SubscriptionPlan.Pro, BillingCycle.Annual, 1_490m,
            SubscriptionPlan.Basic, BillingCycle.Annual, 590m, 0m, false, upgradeBlocked: true);
        Assert.Equal(PublicCheckoutPolicy.DowngradeRefusal, down.Refusal);
    }
}
