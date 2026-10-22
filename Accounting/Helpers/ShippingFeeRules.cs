using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>วิธีรับสินค้าของคำสั่งซื้อออนไลน์ — ค่าที่เก็บใน <c>SiteOrder.FulfillmentMethod</c></summary>
public static class FulfillmentMethod
{
    public const string Delivery = "delivery";
    public const string Pickup = "pickup";

    /// <summary>ว่าง = จัดส่ง (พฤติกรรมเดิมของหน้าร้านทุกเว็บ) · ไม่รู้จัก = โยน</summary>
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0 || s == Delivery) return Delivery;
        if (s == Pickup) return Pickup;
        throw new BusinessRuleException($"วิธีรับสินค้า \"{raw}\" ไม่รู้จัก (ได้: จัดส่ง / มารับเอง)", "SHIP-METHOD");
    }

    public static string Label(string? m) => m == Pickup ? "มารับเองที่ร้าน" : "จัดส่ง";
}

/// <summary>ข้อเท็จจริงของอัตราค่าส่งหนึ่งตัว (จาก <c>SiteShippingRate</c>)</summary>
public sealed record ShippingRateFacts(
    ShippingRateType RateType, decimal BaseRate, decimal? PerKgRate, decimal? FreeAboveAmount,
    decimal? MinWeightKg, decimal? MaxWeightKg);

/// <summary>ผลคิดค่าส่ง — <see cref="Applicable"/> false = อัตรานี้ใช้กับน้ำหนักนี้ไม่ได้</summary>
public readonly record struct ShippingQuote(bool Applicable, decimal Fee, bool IsFree);

/// <summary>
/// **ค่าจัดส่งของร้านออนไลน์ — ตัวคิดตัวเดียว** (ตัวเลือกที่หน้าร้านแสดง · ยอดที่ลงคำสั่งซื้อ · บรรทัดค่าส่งในเอกสารขาย)
///
/// <para>═══ ที่มา (ทีม K · 2026-10-22) ═══ เดิม <c>CmsShippingService.CalculateShippingOptionsAsync</c> คิดค่าส่งให้หน้าร้านดู
/// แต่ <c>CreateOrderAsync</c> <b>ไม่เคยตั้ง</b> <c>SiteOrder.ShippingAmount</c> (grep ผู้เขียน = 0) ⇒ ลูกค้าเห็นค่าส่งแต่ไม่ถูกเก็บ ·
/// และค่าตั้ง "ค่าส่งเหมาจ่าย/ส่งฟรีเมื่อครบ" ของร้าน (<c>SiteCommerceConfig.FlatShippingRate/FreeShippingThreshold</c>)
/// ไม่มีผู้อ่าน · ปัดด้วย <c>Math.Round</c> ค่าเริ่มต้น (banker's)</para>
///
/// <para><b>กติกา</b>: เหมาจ่าย = BaseRate · ตามน้ำหนัก = BaseRate + PerKg × น้ำหนัก · ฟรี = 0 ·
/// ส่งฟรีเมื่อยอดสินค้า (หลังส่วนลด) ≥ FreeAboveAmount · น้ำหนักนอกช่วง Min/Max = ใช้อัตรานี้ไม่ได้ ·
/// "ตามโซน" = เลือกอัตราของโซนที่ตรงที่อยู่ (ตัวจับโซนอยู่ใน <c>CmsShippingService</c>) ·
/// ตามระยะทาง (กม.) <b>ยังไม่รองรับ</b> — ระยะทางจาก client เชื่อไม่ได้ และยังไม่มีตัวหาระยะฝั่งเซิร์ฟเวอร์ (backlog)</para>
///
/// <para><b>VAT ของค่าส่ง</b> (<see cref="VatRateForShipping"/>): ค่าขนส่งที่ผู้ขายเรียกเก็บรวมกับการขายสินค้าเป็นส่วนหนึ่งของ
/// "มูลค่าของฐานภาษี" (ป.รัษฎากร §79) ⇒ อัตราตามสินค้าในบิล — เดิมลงอัตรา 0 เสมอ ซึ่ง <c>Pp30SalesClassifier</c>
/// จัดเป็น "ยอดขายอัตราร้อยละ 0 (§80/1 ส่งออก)" ⇒ ค่าส่งในประเทศถูกรายงานเป็นยอดส่งออก</para>
/// </summary>
public static class ShippingFeeRules
{
    private const MidpointRounding R = MidpointRounding.AwayFromZero;

    public static ShippingQuote Quote(ShippingRateFacts rate, decimal goodsSubtotal, decimal weightKg)
    {
        var w = weightKg < 0m ? 0m : weightKg;
        if (rate.MinWeightKg is decimal min && w < min) return new ShippingQuote(false, 0m, false);
        if (rate.MaxWeightKg is decimal max && w > max) return new ShippingQuote(false, 0m, false);

        var free = rate.RateType == ShippingRateType.Free
            || (rate.FreeAboveAmount is decimal f && f > 0m && goodsSubtotal >= f);
        if (free) return new ShippingQuote(true, 0m, true);

        var fee = rate.RateType switch
        {
            ShippingRateType.ByWeight => rate.BaseRate + (rate.PerKgRate ?? 0m) * w,
            _ => rate.BaseRate,
        };
        fee = Math.Round(fee < 0m ? 0m : fee, 2, R);
        return new ShippingQuote(true, fee, fee == 0m);
    }

    /// <summary>ค่าส่งเหมาจ่ายระดับร้าน (ร้านที่ไม่ได้ตั้งโซน) — null = ร้านไม่ได้ตั้งค่าส่งเลย (ส่งฟรี)</summary>
    public static decimal SiteFlatFee(decimal? flatRate, decimal? freeThreshold, decimal goodsSubtotal)
    {
        if (flatRate is not decimal fr || fr <= 0m) return 0m;
        if (freeThreshold is decimal t && t > 0m && goodsSubtotal >= t) return 0m;
        return Math.Round(fr, 2, R);
    }

    /// <summary>อัตรา VAT ของบรรทัดค่าส่ง — ผู้ขายไม่จด VAT = 0 · มีสินค้าอัตราใดมากกว่า 0 = อัตราสูงสุดในบิล ·
    /// สินค้าทั้งบิลอัตรา 0/ยกเว้น = 0 (ค่าส่งตามสถานะของสินค้าที่ขาย)</summary>
    public static decimal VatRateForShipping(bool sellerVatRegistered, IEnumerable<decimal> goodsLineVatRates)
    {
        if (!sellerVatRegistered) return 0m;
        var max = 0m;
        foreach (var r in goodsLineVatRates) if (r > max) max = r;
        return max;
    }
}
