using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ราคาแพ็กเกจ NextAcc — ตัวตั้งเดียว** (รอบ 219 เฟส 0 · PLATFORM_BILLING_AUDIT G-0/G-4 · คำตัดสินข้อ 178)
///
/// <para>เดิม switch "รอบบิล → ราคา" ถูกเขียนซ้ำ 5 ที่ (Convert · Change · Review · แอดมินเปลี่ยนแพ็กเกจ · ใบแจ้งหนี้ต่ออายุ)
/// และยอดของ <c>SubscriptionPayment</c> มาจาก body ของผู้เช่า ⇒ ฿1 ได้แพ็กเกจหลายปีบนเส้นยืนยันอัตโนมัติ ·
/// ตอนนี้เซิร์ฟเวอร์คำนวณยอดจากที่นี่ที่เดียว และผู้เช่า "ขอแพ็กเกจ" ได้ แต่ <b>ได้สิทธิ์เมื่อเงินเข้าแล้วเท่านั้น</b></para>
/// </summary>
public static class PlatformPlanPricing
{
    /// <summary>ราคาต่อรอบตามตารางแพ็กเกจ</summary>
    public static decimal CyclePrice(PlanTemplate t, BillingCycle cycle) => cycle switch
    {
        BillingCycle.Monthly => t.MonthlyPrice,
        BillingCycle.Quarterly => t.QuarterlyPrice,
        BillingCycle.SemiAnnual => t.SemiAnnualPrice,
        BillingCycle.Annual => t.AnnualPrice,
        _ => t.MonthlyPrice,
    };

    /// <summary>จำนวนเดือนของรอบบิล (ค่า enum = จำนวนเดือน) · ค่าที่ไม่รู้จัก = 0 (ให้ผู้เรียกปฏิเสธ)</summary>
    public static int MonthsOf(BillingCycle cycle) => cycle switch
    {
        BillingCycle.Monthly => 1,
        BillingCycle.Quarterly => 3,
        BillingCycle.SemiAnnual => 6,
        BillingCycle.Annual => 12,
        _ => 0,
    };

    /// <summary>ยอดที่ต้องจ่ายของคำขอซื้อแพ็กเกจ — ราคารอบหักส่วนลด % (ถ้ามี) · ปัด 2 ตำแหน่ง AwayFromZero</summary>
    public static decimal AmountDue(PlanTemplate t, BillingCycle cycle, decimal? discountPercent = null)
    {
        var price = CyclePrice(t, cycle);
        if (discountPercent is decimal d && d > 0m && d <= 100m)
            price *= (1m - d / 100m);
        return Math.Round(Math.Max(0m, price), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ยอดที่ผู้เช่าแจ้งมาตรงกับยอดที่ต้องจ่ายไหม · ≤ 0 = ไม่ได้แจ้ง (ใช้ยอดของเซิร์ฟเวอร์)</summary>
    public static bool ClaimedAmountAcceptable(decimal claimed, decimal due)
        => claimed <= 0m || Math.Abs(claimed - due) <= 0.01m;

    /// <summary>รอบ 230 (คำตัดสินข้อ 230-Q1/230-Q2 · ข้อ 178 (ฉ)): ยอดที่ช่องทางออนไลน์<b>อนุมัติอัตโนมัติได้</b> — รายการที่เซิร์ฟเวอร์เสนอยอดเอง
    /// (ส่วนต่างอัปเกรด · คูปอง · แอดมินแก้ยอด: <c>SubscriptionPayment.QuotedAmount</c>) ⇒ ค่านั้น · รายการแบบเดิม ⇒ ราคาเต็มของแพ็กเกจ (กติกาเดิม) ·
    /// เดิมด่านนี้รู้จักแค่ราคาเต็ม ⇒ รายการที่มีส่วนลดจากเซิร์ฟเวอร์เองถูกพักให้แอดมินทุกใบ</summary>
    public static decimal AutoApproveExpected(decimal? quotedAmount, decimal listPrice)
        => quotedAmount is decimal q && q > 0m ? q : listPrice;

    public enum ChangeKind { Upgrade, Downgrade, Same }

    /// <summary>เปลี่ยนแพ็กเกจเป็นอัปเกรดหรือดาวน์เกรด — เทียบราคาต่อเดือนของแพ็กเกจ (ไม่ใช่ลำดับ enum)
    /// · อัปเกรด = จ่ายก่อนแล้วมีผลทันทีหลังเงินเข้า · ดาวน์เกรด/เปลี่ยนรอบ = มีผลเมื่อครบรอบปัจจุบัน</summary>
    public static ChangeKind Classify(decimal currentMonthlyPrice, decimal newMonthlyPrice)
        => newMonthlyPrice > currentMonthlyPrice ? ChangeKind.Upgrade
         : newMonthlyPrice < currentMonthlyPrice ? ChangeKind.Downgrade
         : ChangeKind.Same;

    /// <summary>อนุมัติการชำระที่เป็นดาวน์เกรดระหว่างรอบที่ยังใช้งาน ⇒ เลื่อนไปมีผลวันครบรอบเดิม (ไม่ตัดสิทธิ์แพ็กเกจที่จ่ายไปแล้ว)</summary>
    /// <remarks>รอบ 233 ฝ่ายค้าน N7: วันหมดอายุค่าแทน "ไม่หมดอายุ" (ExpiryNeedsReview) ไม่ใช่วันครบรอบจริง ⇒ ไม่เลื่อน (เปลี่ยนทันทีตามรอบที่จ่าย) —
    /// เดิมเลื่อนไป "วันเริ่มรอบที่จ่าย" ซึ่งคือวันชำระ (ในอดีต) หรือปี 2126</remarks>
    public static bool DeferToCycleEnd(ChangeKind kind, SubscriptionStatus status, DateTime currentEndUtc, DateTime nowUtc)
        => kind != ChangeKind.Upgrade && status == SubscriptionStatus.Active && currentEndUtc > nowUtc
           && !SubscriptionExpiry.IsOpenEnded(currentEndUtc, nowUtc);

    /// <summary>ฝ่ายค้านรอบ 219 B1 (นโยบายชั่วคราวจนกว่าจะมี proration · คำตัดสินข้อ 178 ภาคผนวก): วันเริ่มรอบที่จ่าย —
    /// <b>ต่ออายุแพ็กเกจเดิม</b>/ดาวน์เกรดที่รอวันครบรอบ ⇒ ต่อจากวันหมดอายุเดิม (ถ้ายังไม่หมด) · <b>อัปเกรดทันที</b> หรือ sub <b>ฟรีถาวร</b>
    /// (EndDate +100 ปี) ⇒ เริ่มจากวันนี้ (ส่วนที่เหลือของรอบเดิมสละ) · เดิมทุกกรณีต่อจาก EndDate ⇒ ฟรีถาวรอัปเกรด ฿990 ได้ Pro 100 ปี</summary>
    /// <para>รอบ 233 (คำตัดสิน 233-1/233-7 · ผู้เรียกผ่าน <see cref="PlanPurchasePreview.PaidPeriod"/> ตัวเดียว): <paramref name="isTrial"/> (สถานะ Trial) ⇒ เริ่มวันชำระ
    /// (เดิม: ทดลอง Pro แล้วซื้อ Pro = ต่อท้ายวันทดลองที่เหลือ แต่ทดลอง Pro แล้วซื้อ Enterprise = เริ่มวันนี้ — ไม่สม่ำเสมอ และหน้าชำระบอก "เริ่มรอบใหม่จากวันที่ชำระ") ·
    /// วันหมดอายุเป็นค่าแทน "ไม่หมดอายุ" (<see cref="SubscriptionExpiry.IsOpenEnded(DateTime, DateTime)"/> · แถว ExpiryNeedsReview) ⇒ เริ่มวันชำระ
    /// (เดิม: ต่ออายุต่อจากปี 2126 ⇒ ใบเสร็จ/แจ้งเตือนบอก "ใช้ได้ถึง 2670" ก่อน ApplyTemplate ปิดรอบทับ)</para>
    public static DateTime PeriodStart(ChangeKind kind, bool samePlan, bool currentIsPermanentFree, DateTime currentEndUtc, DateTime nowUtc, bool isTrial = false)
    {
        if (currentIsPermanentFree || SubscriptionExpiry.IsOpenEnded(currentEndUtc, nowUtc)) return nowUtc;
        if (isTrial) return nowUtc;
        if (!samePlan && kind == ChangeKind.Upgrade) return nowUtc;
        return currentEndUtc > nowUtc ? currentEndUtc : nowUtc;
    }

    /// <summary>ฝ่ายค้านรอบ 219 S5: การชำระของบริษัทต่ออายุแพลนระดับบัญชีได้เฉพาะเมื่อแพ็กเกจตรงกัน (ไม่รู้แพ็กเกจของแพลนบัญชี = ไม่ต่อ)</summary>
    public static bool MayCascadeToAccountPlan(SubscriptionPlan paidPlan, SubscriptionPlan? accountPlan)
        => accountPlan is SubscriptionPlan a && a == paidPlan;

    /// <summary>ฝ่ายค้านรอบ 219 S9: ใบแจ้งหนี้ต่ออายุของรอบนี้ออกแล้ว (ราคาแพ็กเกจเดิม) ⇒ ไม่รับการเปลี่ยนที่รอวันครบรอบ — คืนข้อความบอกว่าเลือกได้เมื่อไร ·
    /// null = ทำได้ · ทางเลือกที่ถูกกว่า (ยกเลิก/ออกใบแจ้งหนี้ใหม่ผ่านใบลดหนี้) เป็นงานเฟส 1–3</summary>
    public static string? ScheduleBlockedByIssuedRenewal(DateTime? renewalInvoiceForEndUtc, DateTime currentEndUtc)
        => renewalInvoiceForEndUtc is DateTime f && f == currentEndUtc
            ? $"ออกใบแจ้งหนี้ต่ออายุรอบถัดไปตามแพ็กเกจปัจจุบันแล้ว — เปลี่ยนแพ็กเกจที่ถูกลงได้หลังวันครบรอบ {currentEndUtc.AddHours(7):dd/MM/yyyy} "
              + "(หรือติดต่อผู้ดูแลระบบให้ออกใบแจ้งหนี้ใหม่)"
            : null;

    /// <summary>ฝ่ายค้านรอบ 219 S4: ทดลองใช้ Account Plan ได้ครั้งเดียวต่อเจ้าของต่อระดับแพ็กเกจ (เคยมีแถวของระดับนี้ ไม่ว่าสถานะใด = ไม่ได้)</summary>
    public static bool MayStartAccountTrial(IEnumerable<SubscriptionPlan> previousPlans, SubscriptionPlan requested)
        => !previousPlans.Contains(requested);

    /// <summary>ฝ่ายค้านรอบ 219 (หมายเหตุ): วันรับเงินของรายการค่าแพ็กเกจ (เป็นวันที่บนใบเสร็จ/ใบกำกับ) — ค่าว่าง/อนาคต ⇒ <paramref name="nowUtc"/> ·
    /// ย้อนเกิน 31 วันก่อน <paramref name="createdUtc"/> ⇒ ตัดที่ 31 วัน (กันลงวันที่ย้อนหลังเกินจริง)</summary>
    public static DateTime ClampPaymentDate(DateTime requestedUtc, DateTime createdUtc, DateTime nowUtc)
    {
        if (requestedUtc == default || requestedUtc > nowUtc) return nowUtc;
        var floor = createdUtc.AddDays(-31);
        return requestedUtc < floor ? floor : requestedUtc;
    }

    /// <summary>ฝ่ายค้านรอบ 219 รอบ 2 (ข้อ 5): <b>วันรับเงินตอนอนุมัติ</b> (= วันที่บนใบเสร็จ/ใบกำกับ = tax point §78/1) — ตัวตัดสินเดียว
    /// <list type="bullet">
    ///   <item>ระบบยืนยันจาก gateway ⇒ เวลาที่เงินเข้าจริง (<c>PaymentIntent.PaidAt</c>) — เดิมคงวันที่ตอนเลือกแพ็กเกจ (อาจหลายวันก่อนจ่าย)</item>
    ///   <item>แอดมินบันทึกเอง/ยกเว้น ⇒ วันที่แอดมินกรอก (ตรวจที่ทางเข้าแล้ว: ไม่เป็นอนาคต + งวดไม่ปิด) — เดิมถูกบีบเงียบ ๆ เมื่อย้อนเกิน 31 วัน</item>
    ///   <item>ลูกค้าแจ้งโอน ⇒ บีบ (<see cref="ClampPaymentDate"/>) ตามเดิม</item>
    /// </list></summary>
    public static DateTime ApprovedPaymentDate(bool systemConfirmed, DateTime? gatewayPaidAtUtc, bool adminEntered,
        DateTime recordedUtc, DateTime createdUtc, DateTime nowUtc)
    {
        if (systemConfirmed)
            return gatewayPaidAtUtc is DateTime p && p != default && p <= nowUtc ? p : nowUtc;
        if (adminEntered)
            return recordedUtc == default || recordedUtc > nowUtc ? nowUtc : recordedUtc;
        return ClampPaymentDate(recordedUtc, createdUtc, nowUtc);
    }

    /// <summary>ฝ่ายค้านรอบ 219 รอบ 2 (ข้อ 5): วันรับเงินที่แอดมินกรอก — อนาคต = ปฏิเสธพร้อมข้อความ (null = ใช้ได้ · ค่าว่าง = วันนี้)</summary>
    public static string? AdminPaymentDateProblem(DateTime requestedUtc, DateTime nowUtc)
        => requestedUtc != default && requestedUtc > nowUtc.AddMinutes(5)
            ? $"วันรับเงิน {requestedUtc.AddHours(7):dd/MM/yyyy HH:mm} อยู่ในอนาคต — กรอกวันที่ได้รับเงินจริง"
            : null;

    /// <summary>ฝ่ายค้านรอบ 219 รอบ 2 (S2): วันมีผลของการเปลี่ยนแพ็กเกจที่รอครบรอบ — sub ฟรีถาวร (EndDate +100 ปี) / หมดรอบแล้ว ⇒ มีผลทันที
    /// (เดิมตั้งวันมีผล = EndDate ⇒ ฟรีถาวรรอ 100 ปี) · อื่น ๆ = วันครบรอบ</summary>
    /// <remarks>รอบ 233 ฝ่ายค้าน N7: วันหมดอายุค่าแทน (แถว ExpiryNeedsReview ที่ธงฟรีถาวรหลุด) = ไม่มีวันครบรอบ ⇒ มีผลทันทีเหมือนฟรีถาวร (เดิมตั้งมีผลปี 2126)</remarks>
    public static DateTime ScheduledChangeEffectiveAt(bool isPermanentFree, DateTime endUtc, DateTime nowUtc)
        => isPermanentFree || endUtc <= nowUtc || SubscriptionExpiry.IsOpenEnded(endUtc, nowUtc) ? nowUtc : endUtc;

    /// <summary>รายงานแอดมิน: แพ็กเกจที่ผู้เช่าเปิดเอง (Convert/เปลี่ยนแพ็กเกจรุ่นก่อนเฟส 0) หลังการชำระที่อนุมัติครั้งล่าสุด = ยังไม่ได้จ่าย</summary>
    public static bool IsUnpaidSelfActivation(DateTime? lastSelfActivationAt, DateTime? lastApprovedPaymentAt)
        => lastSelfActivationAt is DateTime a && (lastApprovedPaymentAt is not DateTime p || p < a);

    // ═════════ รอบ 231 (คำตัดสินข้อ 231-8 · LICENSE_USAGE_OVERAGE_AUDIT §C.2) — ราคาพิเศษรายบริษัท: ตัวตัดสินเดียว ═════════

    public const string SourceList = "list";
    public const string SourceOverridePlan = "override:PriceOfPlan";
    public const string SourceOverrideFixed = "override:Fixed";
    public const string SourceOverridePercent = "override:Percent";

    /// <summary>ข้อมูลของแถวราคาพิเศษ (pure — ไม่ผูก entity · <c>PlatformPriceOverrideStore.ToInput</c> แปลงให้)</summary>
    public sealed record PriceOverrideInput(Guid Id, PlanPriceOverrideMode Mode, Guid? ReferencePlanTemplateId,
        decimal? FixedMonthly, decimal? FixedQuarterly, decimal? FixedSemiAnnual, decimal? FixedAnnual, decimal? PercentOff,
        DateTime EffectiveFromUtc, DateTime? EffectiveToUtc, bool IsActive, bool AppliesToUpgrades, bool AppliesToRenewals);

    /// <summary>ผลของ <see cref="EffectiveCyclePrice"/> — <see cref="Price"/> = ราคาที่ใช้จริง · <see cref="ListPrice"/> = ราคาแม่แบบ · <see cref="Applied"/> = ใช้ราคาพิเศษ</summary>
    public sealed record EffectivePrice(decimal Price, decimal ListPrice, string Source, Guid? OverrideId, string? Note)
    {
        public bool Applied => OverrideId != null;
    }

    /// <summary>แถวราคาพิเศษใช้ได้ ณ เวลานี้กับงานชนิดนี้ไหม — active · อยู่ในช่วง [From, To) · ไม่ถูกปิด · ตรงชนิดงาน (ต่ออายุ/อัปเกรด)</summary>
    public static bool OverrideLive(PriceOverrideInput? ov, DateTime nowUtc, bool forRenewal)
        => ov != null && ov.IsActive
           && ov.EffectiveFromUtc <= nowUtc
           && (ov.EffectiveToUtc == null || nowUtc < ov.EffectiveToUtc.Value)
           && (forRenewal ? ov.AppliesToRenewals : ov.AppliesToUpgrades);

    /// <summary><b>ราคาต่อรอบที่มีผลจริง</b> — ตัวเดียวของ ใบต่ออายุ · ส่วนต่างอัปเกรด (ทั้งสองฝั่ง) · ดูราคา · คำขอแพ็กเกจ/แปลงจากทดลองใช้ · อนุมัติอัตโนมัติ · แอดมินเปลี่ยนแพ็กเกจ ·
    /// ลำดับ: ราคาพิเศษที่ active และอยู่ในช่วง ?? ราคาแม่แบบ · ปัด 2 ตำแหน่ง AwayFromZero · ห้ามติดลบ ·
    /// <c>PriceOfPlan</c> ต้องมี <paramref name="reference"/> (ไม่มี = ตกกลับราคาแม่แบบ พร้อม Note — ไม่แต่งราคาเอง · F2 ข้อ 3) ·
    /// <c>FixedPerCycle</c> รอบที่เว้นว่าง = ราคาแม่แบบรอบนั้น · <c>PercentOff</c> นอกช่วง (0, 100] = ตกกลับ ·
    /// ซ้อน: ราคาพิเศษ (แทนราคาตั้ง) → คูปอง (<see cref="AmountDue(decimal, decimal?)"/> ลดจากราคาพิเศษ) → แอดมินแก้ยอดครั้งเดียว (ทับสุดท้าย)</summary>
    public static EffectivePrice EffectiveCyclePrice(PlanTemplate target, BillingCycle cycle, PriceOverrideInput? ov, PlanTemplate? reference,
        DateTime nowUtc, bool forRenewal = false)
    {
        var list = R2(CyclePrice(target, cycle));
        if (!OverrideLive(ov, nowUtc, forRenewal))
            return new EffectivePrice(list, list, SourceList, null, null);
        switch (ov!.Mode)
        {
            case PlanPriceOverrideMode.PriceOfPlan:
                if (reference == null || ov.ReferencePlanTemplateId == null || reference.Id != ov.ReferencePlanTemplateId)
                    return new EffectivePrice(list, list, SourceList, null, "ราคาพิเศษอ้างแพ็กเกจที่ไม่พบ — ใช้ราคาแม่แบบ");
                // ฝ่ายค้าน S6: แม่แบบอ้างอิงถูกปิด/ลบ ⇒ ไม่ grandfather ราคาเก่า — ตกกลับราคาแม่แบบพร้อมเหตุ (แอดมินตั้งแถวใหม่)
                if (!reference.IsActive || reference.IsDeleted)
                    return new EffectivePrice(list, list, SourceList, null, $"ราคาพิเศษอ้างแพ็กเกจ {reference.Name} ที่ปิด/ลบแล้ว — ใช้ราคาแม่แบบ (ตั้งราคาพิเศษใหม่)");
                var refPrice = R2(CyclePrice(reference, cycle));
                return new EffectivePrice(refPrice, list, SourceOverridePlan, ov.Id, $"ราคาตามข้อตกลง: ราคาของแพ็กเกจ {reference.Name}");
            case PlanPriceOverrideMode.FixedPerCycle:
                decimal? fixedPrice = cycle switch
                {
                    BillingCycle.Monthly => ov.FixedMonthly,
                    BillingCycle.Quarterly => ov.FixedQuarterly,
                    BillingCycle.SemiAnnual => ov.FixedSemiAnnual,
                    BillingCycle.Annual => ov.FixedAnnual,
                    _ => null,
                };
                if (fixedPrice is not decimal f || f < 0m)
                    return new EffectivePrice(list, list, SourceList, null, "ราคาพิเศษไม่ได้กำหนดรอบนี้ — ใช้ราคาแม่แบบ");
                return new EffectivePrice(R2(f), list, SourceOverrideFixed, ov.Id, "ราคาตามข้อตกลง");
            case PlanPriceOverrideMode.PercentOff:
                if (ov.PercentOff is not decimal p || p <= 0m || p > 100m)
                    return new EffectivePrice(list, list, SourceList, null, "ราคาพิเศษกำหนด % ไม่ถูกต้อง — ใช้ราคาแม่แบบ");
                var discounted = R2(Math.Max(0m, list * (1m - p / 100m)));
                return new EffectivePrice(discounted, list, SourceOverridePercent, ov.Id, $"ราคาตามข้อตกลง: ลด {p:0.##}% จากราคาตั้ง");
            default:
                return new EffectivePrice(list, list, SourceList, null, null);
        }
    }

    /// <summary>ยอดที่ต้องจ่ายจาก<b>ราคาที่มีผลแล้ว</b> (ราคาพิเศษหรือแม่แบบ) หักส่วนลด % (โปรโมชันแปลงทดลองใช้) — คูปองซ้อนบนผลนี้อีกชั้นใน <c>PlatformCouponStore</c> ·
    /// ปัด 2 ตำแหน่ง AwayFromZero · ไม่ติดลบ</summary>
    public static decimal AmountDue(decimal effectiveCyclePrice, decimal? discountPercent)
    {
        var price = effectiveCyclePrice;
        if (discountPercent is decimal d && d > 0m && d <= 100m)
            price *= (1m - d / 100m);
        return R2(Math.Max(0m, price));
    }

    /// <summary>ข้อความ "ราคาพิเศษสำหรับคุณ" บนหน้าแพ็กเกจ/ดูราคา — null เมื่อไม่มีราคาพิเศษ (หน้าเว็บแสดงอย่างเดียว · F2 ข้อ 5)</summary>
    public static string? SpecialPriceNote(EffectivePrice p, BillingCycle cycle)
        => p.Applied ? $"ราคาพิเศษสำหรับคุณ: ฿{p.Price:N2}/{CycleLabel(cycle)} (ราคาตั้ง ฿{p.ListPrice:N2})" : null;

    /// <summary>ฝ่ายค้าน S5: วันสิ้นสุดราคาพิเศษที่แอดมินกรอกเป็น "วันที่" ⇒ เก็บเป็น<b>สิ้นวันไทย</b> (23:59:59.999 Asia/Bangkok) ในรูป UTC
    /// — ช่วง [From, To) จึงครอบวันที่กรอกทั้งวัน และตารางแสดงวันเดียวกับที่กรอก</summary>
    public static DateTime EndOfBangkokDayUtc(DateTime anyUtc)
    {
        var bkk = DateTime.SpecifyKind(anyUtc, DateTimeKind.Utc).AddHours(7).Date;
        return DateTime.SpecifyKind(bkk.AddDays(1).AddMilliseconds(-1).AddHours(-7), DateTimeKind.Utc);
    }

    /// <summary>ฝ่ายค้าน S4/N1: ซื้อแพ็กเกจเดิมซ้ำ (ต่ออายุ/เปลี่ยนรอบของแพ็กเกจเดิม) = งาน "ต่ออายุ" · แพ็กเกจต่าง = "อัปเกรด" — ตัวเดียวของทุกจุดเรียก resolver</summary>
    public static bool IsRenewalPurchase(SubscriptionPlan currentPlan, SubscriptionPlan targetPlan) => currentPlan == targetPlan;

    public static string CycleLabel(BillingCycle cycle) => cycle switch
    {
        BillingCycle.Monthly => "เดือน",
        BillingCycle.Quarterly => "3 เดือน",
        BillingCycle.SemiAnnual => "6 เดือน",
        BillingCycle.Annual => "ปี",
        _ => cycle.ToString(),
    };

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
}
