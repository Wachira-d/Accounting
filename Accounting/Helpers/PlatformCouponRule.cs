using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §7.1 · คำตัดสินข้อ 230-Q2) — <b>ตัวตัดสินคูปองแพลตฟอร์มตัวเดียว</b> · pure · decimal · <c>MidpointRounding.AwayFromZero</c>
/// <list type="bullet">
/// <item>ส่วนลด % = <c>round(ราคา × % ÷ 100, 2)</c> · ส่วนลดบาท = <c>min(ค่า, ราคา)</c> · ยอดหลังส่วนลด<b>ไม่ต่ำกว่า 0</b> — เหลือ 0 = ใช้ไม่ได้ (ฝ่ายค้าน S4)</item>
/// <item>เงื่อนไข: เปิดใช้ · ช่วงวันที่ [ValidFrom, ValidTo) · ขอบเขต (แพ็กเกจ/โควตาเสริม) · แพ็กเกจ/รอบบิล/ทรัพยากรที่ระบุ · ครั้งแรกเท่านั้น ·
/// เพดานรวม/ต่อบริษัท (จำนวนที่ผู้เรียกนับใต้ล็อก — <c>PlatformCouponStore</c>)</item>
/// <item>ใช้ตอน<b>สร้าง</b>รายการชำระเท่านั้น (ยอดมาจากเซิร์ฟเวอร์เสมอ) · ใบกำกับแสดงส่วนลดเป็นส่วนลดของบรรทัด <b>ก่อน VAT</b> (§86/4 · <see cref="SplitDiscounted"/>)</item>
/// </list>
/// </summary>
public static class PlatformCouponRule
{
    public const string SourceSubscriptionPayment = "SubscriptionPayment";
    public const string SourceQuotaAddOn = "QuotaAddOn";
    /// <summary>รายการค้างไม่จ่ายนับเข้าเพดานได้นานเท่านี้ (เท่าระยะยกเลิกรายการโควตาเสริมที่ค้าง)</summary>
    public const int ReservationDays = 14;
    public const string RuleCode = "PLATFORM-COUPON";
    public const string InvalidCodeMessage = "โค้ดส่วนลดไม่ถูกต้องหรือใช้ไม่ได้แล้ว";

    /// <summary>สิ่งที่กำลังจะซื้อ</summary>
    /// <summary>สิ่งที่กำลังจะซื้อ — รอบ 235 (K-2): <see cref="IsRenewal"/> = ต่ออายุแพ็กเดิม+รอบเดิม (<see cref="IsRenewalTarget"/>) ตรวจกับ <c>PlatformCoupon.PurchaseKinds</c></summary>
    /// <para>ฝ่ายค้าน S-1b (235-1b): <see cref="RenewalSpecialPrice"/> = การต่ออายุนี้ได้ราคาพิเศษ (<c>PlatformPriceOverrideStore</c> · AppliesToRenewals) อยู่แล้ว ⇒ คูปองไม่ลดซ้อน</para>
    public sealed record Target(PlatformCouponScope Scope, string? Plan, string? Cycle, string? AddOnResource, bool IsFirstPayment, bool IsRenewal = false,
        bool RenewalSpecialPrice = false);

    /// <summary>ฝ่ายค้าน S-2 (235-1b): ข้อความเดียวของ "ส่วนลดซ้อนตอนต่ออายุ"</summary>
    public const string RenewalSpecialPriceMessage = "บริษัทนี้ได้ราคาพิเศษสำหรับการต่ออายุอยู่แล้ว — คูปองไม่ลดซ้ำ (ต่ออายุโดยไม่ใส่คูปอง)";

    /// <summary>รอบ 235 (K-2 · คำตัดสิน 235-1): รายการค่าแพ็กเกจนี้นับเป็น "ต่ออายุ" ของคูปองไหม — <b>ตัวเดียว</b>ของ preview (<c>QuotePlanChangeCoreAsync</c> ·
    /// <c>PublicCheckoutService.QuoteAsync</c>) และตอนจอง (<c>RequestPlanPaymentAsync</c>): แพ็กเดิม + รอบเดิม + ไม่ใช่ทดลองใช้/ฟรีถาวร + ไม่ใช่ส่วนต่าง/เปลี่ยนรอบ
    /// (หมดอายุแล้วซื้อแพ็กเดิมรอบเดิม = ต่ออายุช้า ⇒ นับเป็นต่ออายุ) · อื่น ๆ = ซื้อใหม่/อัปเกรด</summary>
    public static bool IsRenewalTarget(SubscriptionStatus status, bool isPermanentFree, SubscriptionPlan currentPlan, BillingCycle currentCycle,
        SubscriptionPlan targetPlan, BillingCycle targetCycle, bool prorated = false, bool cycleChange = false)
        => !prorated && !cycleChange && !isPermanentFree && status != SubscriptionStatus.Trial
           && PlatformPlanPricing.IsRenewalPurchase(currentPlan, targetPlan) && currentCycle == targetCycle;

    /// <summary>ป้ายไทยของ <see cref="PlatformCouponPurchaseKinds"/> (หน้าแอดมิน/ข้อความปฏิเสธ — หน้าเว็บไม่มีตารางป้ายเอง)</summary>
    public static string PurchaseKindsLabel(PlatformCouponPurchaseKinds k) => k switch
    {
        PlatformCouponPurchaseKinds.New => "ซื้อใหม่/อัปเกรดเท่านั้น",
        PlatformCouponPurchaseKinds.Renewal => "ต่ออายุเท่านั้น",
        _ => "ซื้อใหม่และต่ออายุ",
    };

    /// <summary>ผล — <see cref="Ok"/>=false ⇒ <see cref="Problem"/> (ไทย · ไม่คิดเงิน)</summary>
    public sealed record Evaluation(bool Ok, decimal ListAmount, decimal Discount, decimal AmountAfter, string? Problem, string Label);

    public static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>รหัสมาตรฐาน — ตัดช่องว่าง · ตัวพิมพ์ใหญ่ (invariant) · ว่าง = null</summary>
    public static string? Normalize(string? code)
    {
        var c = (code ?? "").Trim().ToUpperInvariant();
        return c.Length == 0 ? null : c;
    }

    /// <summary>ปัญหาของค่าที่แอดมินกรอก (null = บันทึกได้)</summary>
    public static string? AdminProblem(string? code, PlatformCouponKind kind, decimal value, DateTime? fromUtc, DateTime? toUtc,
        int? maxTotal, int? maxPerCompany, PlatformCouponScope scope, PlatformCouponPurchaseKinds purchaseKinds = PlatformCouponPurchaseKinds.Both)
    {
        var c = Normalize(code);
        if (c == null) return "กรอกรหัสคูปอง";
        if (c.Length > 40 || !c.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_'))
            return "รหัสคูปองใช้ได้เฉพาะ A–Z 0–9 - _ ยาวไม่เกิน 40 ตัว";
        if (kind == PlatformCouponKind.Percent && (value <= 0m || value > 100m)) return "ส่วนลดแบบ % ต้องมากกว่า 0 และไม่เกิน 100";
        if (kind == PlatformCouponKind.Fixed && value <= 0m) return "ส่วนลดแบบจำนวนเงินต้องมากกว่า 0";
        if (kind is not (PlatformCouponKind.Percent or PlatformCouponKind.Fixed)) return "ชนิดส่วนลดไม่ถูกต้อง";
        if (fromUtc is DateTime f && toUtc is DateTime t && t <= f) return "วันสิ้นสุดต้องหลังวันเริ่ม";
        if (maxTotal is int mt && mt < 1) return "จำนวนครั้งรวมต้องอย่างน้อย 1 (หรือเว้นว่าง = ไม่จำกัด)";
        if (maxPerCompany is int mc && mc < 1) return "จำนวนครั้งต่อบริษัทต้องอย่างน้อย 1 (หรือเว้นว่าง = ไม่จำกัด)";
        if ((scope & PlatformCouponScope.Both) == PlatformCouponScope.None) return "เลือกว่าคูปองใช้กับค่าแพ็กเกจหรือโควตาเสริม";
        if ((purchaseKinds & PlatformCouponPurchaseKinds.Both) == PlatformCouponPurchaseKinds.None) return "เลือกว่าคูปองใช้กับซื้อใหม่ ต่ออายุ หรือทั้งสอง";
        return null;
    }

    /// <summary>ข้อความส่วนลด (เช่น "คูปอง NEW10 ลด 10%")</summary>
    public static string LabelOf(PlatformCoupon c)
        => c.Kind == PlatformCouponKind.Percent ? $"คูปอง {c.Code} ลด {c.Value:0.##}%" : $"คูปอง {c.Code} ลด {c.Value:N2} บาท";

    private static bool InCsv(string? csv, string? value)
    {
        if (string.IsNullOrWhiteSpace(csv)) return true;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>ตัดสินคูปองกับราคา — <paramref name="usedTotal"/>/<paramref name="usedByCompany"/> = จำนวนที่นับแล้ว (Redeemed + Reserved ที่ยังไม่หมดอายุการจอง · ไม่รวมรายการนี้)</summary>
    public static Evaluation Apply(PlatformCoupon? c, decimal price, Target t, DateTime nowUtc, int usedTotal, int usedByCompany)
    {
        var list = R2(Math.Max(0m, price));
        Evaluation No(string why) => new(false, list, 0m, list, why, c == null ? "" : LabelOf(c));
        // ฝ่ายค้าน N1: ไม่มี/ปิดใช้ = ข้อความเดียวกัน (ไม่บอกว่ารหัสไหนมีอยู่จริง — กันเดารหัส)
        if (c == null || c.IsDeleted || !c.IsActive) return No(InvalidCodeMessage);
        if (c.ValidFromUtc is DateTime f && nowUtc < f) return No($"คูปอง {c.Code} ยังไม่เริ่มใช้ (เริ่ม {f.AddHours(7):dd/MM/yyyy})");
        if (c.ValidToUtc is DateTime to && nowUtc >= to) return No($"คูปอง {c.Code} หมดอายุแล้ว");
        if ((c.AppliesTo & t.Scope) == PlatformCouponScope.None)
            return No(t.Scope == PlatformCouponScope.Subscription ? $"คูปอง {c.Code} ใช้กับค่าแพ็กเกจไม่ได้" : $"คูปอง {c.Code} ใช้กับโควตาเสริมไม่ได้");
        if (t.Scope == PlatformCouponScope.Subscription && !InCsv(c.PlansCsv, t.Plan)) return No($"คูปอง {c.Code} ใช้กับแพ็กเกจนี้ไม่ได้");
        if (t.Scope == PlatformCouponScope.Subscription && !InCsv(c.CyclesCsv, t.Cycle)) return No($"คูปอง {c.Code} ใช้กับรอบบิลนี้ไม่ได้");
        if (t.Scope == PlatformCouponScope.QuotaAddOn && !InCsv(c.AddOnResourcesCsv, t.AddOnResource)) return No($"คูปอง {c.Code} ใช้กับรายการนี้ไม่ได้");
        // รอบ 235 (K-2): ซื้อใหม่/ต่ออายุ — เฉพาะค่าแพ็กเกจ (โควตาเสริมไม่มีความหมายของ "ต่ออายุ" ในเส้นนี้) · ค่าตั้งต้น Both = ใช้ได้ทั้งสอง
        if (t.Scope == PlatformCouponScope.Subscription
            && (c.PurchaseKinds & (t.IsRenewal ? PlatformCouponPurchaseKinds.Renewal : PlatformCouponPurchaseKinds.New)) == PlatformCouponPurchaseKinds.None)
            return No(t.IsRenewal ? $"คูปอง {c.Code} ใช้กับการต่ออายุไม่ได้ (ใช้ได้เฉพาะซื้อใหม่/อัปเกรด) — ต่ออายุโดยไม่ใส่คูปอง"
                                  : $"คูปอง {c.Code} ใช้ได้เฉพาะการต่ออายุแพ็กเกจเดิม");
        // ฝ่ายค้าน S-2: ต่ออายุที่ได้ราคาพิเศษอยู่แล้ว ⇒ ไม่ลดซ้อน (ซื้อใหม่คงพฤติกรรมเดิม 231-8: ราคาพิเศษ → คูปอง)
        if (t.Scope == PlatformCouponScope.Subscription && t.IsRenewal && t.RenewalSpecialPrice) return No(RenewalSpecialPriceMessage);
        if (c.FirstPaymentOnly && !t.IsFirstPayment) return No($"คูปอง {c.Code} ใช้ได้เฉพาะการชำระครั้งแรก");
        if (c.MaxUsesTotal is int mt && usedTotal >= mt) return No($"คูปอง {c.Code} ถูกใช้ครบ {mt:N0} ครั้งแล้ว");
        if (c.MaxUsesPerCompany is int mc && usedByCompany >= mc) return No($"บริษัทนี้ใช้คูปอง {c.Code} ครบ {mc:N0} ครั้งแล้ว");
        if (list <= 0m) return No("รายการนี้ไม่มียอดให้ลด");
        var discount = c.Kind == PlatformCouponKind.Percent
            ? R2(list * Math.Min(100m, Math.Max(0m, c.Value)) / 100m)
            : R2(Math.Min(Math.Max(0m, c.Value), list));
        if (discount > list) discount = list;
        var after = R2(Math.Max(0m, list - discount));
        // ฝ่ายค้าน S4: ลดจนเหลือ 0 = ใช้ไม่ได้ (ตัดสินที่นี่ตัวเดียว ⇒ หน้าดูราคา = ตอนจอง) — ไม่มีเงินให้รับ/ใบกำกับ ⇒ แอดมินบันทึกยกเว้นค่าบริการแทน
        if (after <= 0m) return No($"{LabelOf(c)} ทำให้ยอดเป็น 0 บาท — ติดต่อผู้ดูแลระบบ NextAcc ให้บันทึกยกเว้นค่าบริการแทน");
        return new Evaluation(true, list, discount, after, null, LabelOf(c));
    }

    /// <summary>แยกฐาน/ส่วนลด/VAT ของใบกำกับที่มีส่วนลด (§86/4: ส่วนลดหักก่อน VAT) — <paramref name="receivedGross"/> = เงินที่รับ (รวม VAT) ·
    /// <paramref name="discountGross"/> = ส่วนลด (รวม VAT) · คืน <c>FullBase</c> (ราคาก่อนส่วนลด ก่อน VAT) · <c>DiscountBase</c> · <c>NetBase</c> = FullBase − DiscountBase ·
    /// <c>Vat</c> = receivedGross − NetBase (ไม่มีเศษ — ใบ = เงินที่รับพอดี · ตัวแยกเดียวกับ <see cref="PlatformMoneyInAccount.SplitReceived"/>)</summary>
    public static (decimal FullBase, decimal DiscountBase, decimal NetBase, decimal Vat) SplitDiscounted(decimal receivedGross, decimal discountGross, bool isVat)
    {
        var (netBase, vat) = PlatformMoneyInAccount.SplitReceived(R2(receivedGross), isVat);
        if (discountGross <= 0m) return (netBase, 0m, netBase, vat);
        var (fullBase, _) = PlatformMoneyInAccount.SplitReceived(R2(receivedGross + discountGross), isVat);
        var discountBase = R2(fullBase - netBase);
        if (discountBase < 0m) discountBase = 0m;
        return (R2(netBase + discountBase), discountBase, netBase, vat);
    }
}
