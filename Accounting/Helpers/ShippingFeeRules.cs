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

/// <summary>ข้อเท็จจริงของคูปองที่ใช้ตัดสิน "ส่งฟรี" (จาก <c>SiteCoupon</c>)</summary>
public sealed record CouponShippingFacts(
    bool IsActive, bool IsDeleted, bool FreeShipping, CouponDiscountType DiscountType,
    DateTime? StartsAt, DateTime? ExpiresAt, int? MaxUses, int CurrentUses, decimal? MinOrderAmount);

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

    /// <summary>
    /// รอบ 209 (R-209-B8) — "ส่งฟรีเมื่อครบยอด" ระดับร้านกับอัตราในโซน: ค่าตั้ง <c>FreeShippingOverridesZones</c> เปิด ⇒ ยอดสินค้า (หลังส่วนลด) ถึงเกณฑ์
    /// ⇒ ค่าส่ง 0 ทุกอัตรา · ปิด (ค่าเริ่มต้น) ⇒ ค่าเดิมของอัตรา (เกณฑ์ระดับร้านใช้เฉพาะร้านที่ไม่ได้ตั้งโซน — พฤติกรรมเดิม)
    /// </summary>
    public static ShippingQuote ApplyShopFreeThreshold(ShippingQuote zoneQuote, decimal? shopThreshold, bool overridesZones, decimal goodsSubtotal)
    {
        if (!zoneQuote.Applicable || !overridesZones) return zoneQuote;
        if (shopThreshold is decimal t && t > 0m && goodsSubtotal >= t) return new ShippingQuote(true, 0m, true);
        return zoneQuote;
    }

    /// <summary>
    /// รอบ 209 (R-209-B7) — คูปองในตะกร้าให้ "ส่งฟรี" จริงไหม (เดิมคูปองชนิดส่งฟรีคืนส่วนลด 0 แล้วค่าส่งไม่ถูกแตะ = ลูกค้าเสียค่าส่งเต็ม) ·
    /// ตรวจซ้ำตอนคิดยอด (คูปองหมดอายุ/ถูกปิด/ครบจำนวนหลังลูกค้ากดใช้ ⇒ ไม่ส่งฟรี) · null = ไม่มีคูปอง/ใช้ไม่ได้
    /// </summary>
    public static bool CouponGrantsFreeShipping(CouponShippingFacts? c, DateTime nowUtc, decimal goodsSubtotal)
    {
        if (c == null || !c.IsActive || c.IsDeleted) return false;
        if (!c.FreeShipping && c.DiscountType != CouponDiscountType.FreeShipping) return false;
        if (c.StartsAt is DateTime s && nowUtc < s) return false;
        if (c.ExpiresAt is DateTime e && nowUtc > e) return false;
        if (c.MaxUses is int max && c.CurrentUses >= max) return false;
        if (c.MinOrderAmount is decimal min && goodsSubtotal < min) return false;
        return true;
    }

    /// <summary>
    /// รอบ 209 (R-209-B8) — น้ำหนักของบรรทัดที่ใช้คิดค่าส่ง = จำนวน × (น้ำหนักของตัวเลือกสินค้า (variant) ที่ตรงกับตัวเลือกในตะกร้า ?? น้ำหนักสินค้า ?? 0) ·
    /// เดิมใช้น้ำหนักสินค้าแม่เสมอ (เสื้อไซซ์ XL หนักกว่า S แต่ค่าส่งเท่ากัน)
    /// </summary>
    public static decimal LineWeightKg(decimal quantity, decimal? productWeightKg, decimal? variantWeightKg)
    {
        var w = variantWeightKg is decimal v && v > 0m ? v : productWeightKg ?? 0m;
        return quantity <= 0m || w <= 0m ? 0m : quantity * w;
    }

    /// <summary>น้ำหนักของ variant ที่ตัวเลือกในตะกร้า (JSON object ชื่อ→ค่า) ตรงกับตัวเลือกของ variant ทุกคู่ (ไม่สนตัวพิมพ์/ลำดับ) ·
    /// ไม่ตรงตัวใด/อ่านไม่ออก/variant ปิดอยู่ = null (ใช้น้ำหนักสินค้าแม่ — ไม่เดา)</summary>
    public static decimal? VariantWeightOf(string? cartOptionsJson, IEnumerable<(string? OptionsJson, decimal? WeightKg, bool IsActive)> variants)
    {
        var want = ParseOptions(cartOptionsJson);
        if (want == null || want.Count == 0) return null;
        foreach (var v in variants)
        {
            if (!v.IsActive || v.WeightKg is not decimal w || w <= 0m) continue;
            var have = ParseOptions(v.OptionsJson);
            if (have != null && have.Count == want.Count && want.All(kv => have.TryGetValue(kv.Key, out var hv) && string.Equals(hv, kv.Value, StringComparison.OrdinalIgnoreCase)))
                return w;
        }
        return null;
    }

    private static Dictionary<string, string>? ParseOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.RootElement.EnumerateObject())
                d[p.Name.Trim()] = (p.Value.ValueKind == System.Text.Json.JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())?.Trim() ?? "";
            return d;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>อัตรา VAT ของบรรทัดค่าส่ง — ตามสถานะของสินค้าที่ขาย (ค่าขนส่งเป็นส่วนของมูลค่าฐานภาษี §79):
    /// <list type="bullet">
    /// <item>ผู้ขายไม่จด VAT ⇒ 0</item>
    /// <item>มีสินค้าอัตรามากกว่า 0 ⇒ อัตราสูงสุดในบิล — <b>บิลปน</b> (7% กับยกเว้น/0%) เลือก 7% เพราะ: กฎหมายไม่แบ่งค่าขนส่งตามสัดส่วน
    ///   ให้ชัด · เลือก "ยกเว้น/0%" กับบิลที่มีของต้องเสียภาษี = เสี่ยงภาษีขายขาด (ประเมินย้อนหลัง + เงินเพิ่ม) ขณะที่ 7% เสี่ยงแค่ผู้ขายเสียเกิน ·
    ///   ผู้ทำบัญชีแยกบิลได้ถ้าต้องการ (บันทึกใน docs/POS_MTO_DESIGN.md)</item>
    /// <item>ไม่มีอัตราบวก แต่มีสินค้า 0% (ส่งออก §80/1) ⇒ 0</item>
    /// <item>สินค้าทั้งบิล<b>ยกเว้น</b> (§81 · −1) ⇒ ยกเว้น (−1) — <b>ไม่ใช่ 0%</b> (0% = ยอดส่งออกใน ภ.พ.30 ช่อง 7)</item>
    /// </list></summary>
    public static decimal VatRateForShipping(bool sellerVatRegistered, IEnumerable<decimal> goodsLineVatRates)
    {
        if (!sellerVatRegistered) return 0m;
        var max = 0m; var anyZero = false; var anyExempt = false;
        foreach (var r in goodsLineVatRates)
        {
            if (r > max) max = r;
            else if (r == ThaiVatTypeRule.ExemptRate) anyExempt = true;
            else if (r == 0m) anyZero = true;
        }
        if (max > 0m) return max;
        if (anyZero) return 0m;
        return anyExempt ? ThaiVatTypeRule.ExemptRate : 0m;
    }

    /// <summary>VAT ที่อยู่ในค่าส่ง (ราคารวม VAT) — อัตรา ≤ 0 (0%/ยกเว้น) = 0 · ปัด 2 ตำแหน่ง AwayFromZero ·
    /// ใช้รวมเข้า <c>SiteOrder.VatAmount</c> ให้ตรงกับใบกำกับ</summary>
    public static decimal VatInShipping(decimal fee, decimal vatRate)
        => fee <= 0m || vatRate <= 0m ? 0m : Math.Round(fee * vatRate / (100m + vatRate), 2, R);

    /// <summary>VAT ในยอดรวม VAT ของบรรทัดสินค้า — อัตรา ≤ 0 (0%/ยกเว้น −1) = 0 (เดิมสูตรตรงกับ −1 ได้ VAT ติดลบ)</summary>
    public static decimal VatInGross(decimal gross, decimal vatRate)
        => vatRate <= 0m ? 0m : gross * vatRate / (100m + vatRate);
}
