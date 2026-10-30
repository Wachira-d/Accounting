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

    public enum ChangeKind { Upgrade, Downgrade, Same }

    /// <summary>เปลี่ยนแพ็กเกจเป็นอัปเกรดหรือดาวน์เกรด — เทียบราคาต่อเดือนของแพ็กเกจ (ไม่ใช่ลำดับ enum)
    /// · อัปเกรด = จ่ายก่อนแล้วมีผลทันทีหลังเงินเข้า · ดาวน์เกรด/เปลี่ยนรอบ = มีผลเมื่อครบรอบปัจจุบัน</summary>
    public static ChangeKind Classify(decimal currentMonthlyPrice, decimal newMonthlyPrice)
        => newMonthlyPrice > currentMonthlyPrice ? ChangeKind.Upgrade
         : newMonthlyPrice < currentMonthlyPrice ? ChangeKind.Downgrade
         : ChangeKind.Same;

    /// <summary>อนุมัติการชำระที่เป็นดาวน์เกรดระหว่างรอบที่ยังใช้งาน ⇒ เลื่อนไปมีผลวันครบรอบเดิม (ไม่ตัดสิทธิ์แพ็กเกจที่จ่ายไปแล้ว)</summary>
    public static bool DeferToCycleEnd(ChangeKind kind, SubscriptionStatus status, DateTime currentEndUtc, DateTime nowUtc)
        => kind != ChangeKind.Upgrade && status == SubscriptionStatus.Active && currentEndUtc > nowUtc;

    /// <summary>ฝ่ายค้านรอบ 219 B1 (นโยบายชั่วคราวจนกว่าจะมี proration · คำตัดสินข้อ 178 ภาคผนวก): วันเริ่มรอบที่จ่าย —
    /// <b>ต่ออายุแพ็กเกจเดิม</b>/ดาวน์เกรดที่รอวันครบรอบ ⇒ ต่อจากวันหมดอายุเดิม (ถ้ายังไม่หมด) · <b>อัปเกรดทันที</b> หรือ sub <b>ฟรีถาวร</b>
    /// (EndDate +100 ปี) ⇒ เริ่มจากวันนี้ (ส่วนที่เหลือของรอบเดิมสละ) · เดิมทุกกรณีต่อจาก EndDate ⇒ ฟรีถาวรอัปเกรด ฿990 ได้ Pro 100 ปี</summary>
    public static DateTime PeriodStart(ChangeKind kind, bool samePlan, bool currentIsPermanentFree, DateTime currentEndUtc, DateTime nowUtc)
    {
        if (currentIsPermanentFree) return nowUtc;
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
    public static DateTime ScheduledChangeEffectiveAt(bool isPermanentFree, DateTime endUtc, DateTime nowUtc)
        => isPermanentFree || endUtc <= nowUtc ? nowUtc : endUtc;

    /// <summary>รายงานแอดมิน: แพ็กเกจที่ผู้เช่าเปิดเอง (Convert/เปลี่ยนแพ็กเกจรุ่นก่อนเฟส 0) หลังการชำระที่อนุมัติครั้งล่าสุด = ยังไม่ได้จ่าย</summary>
    public static bool IsUnpaidSelfActivation(DateTime? lastSelfActivationAt, DateTime? lastApprovedPaymentAt)
        => lastSelfActivationAt is DateTime a && (lastApprovedPaymentAt is not DateTime p || p < a);
}
