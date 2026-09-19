namespace Accounting.Helpers;

/// <summary>บรรทัดหนึ่งของบิล POS ที่กำลังถูกคืนเงิน (ยอดบนกระดาษ + จำนวนที่คืน)</summary>
/// <param name="LineGross">ยอดรวมของบรรทัด **ก่อน** ส่วนลดระดับบิล (รวม VAT เพราะ POS คิดราคารวมภาษี)</param>
/// <param name="LineVat">VAT ของบรรทัดนั้นตามที่ตรึงไว้ตอนปิดบิล</param>
/// <param name="LineQuantity">จำนวนทั้งหมดของบรรทัด</param>
/// <param name="RefundQuantity">จำนวนที่ขอคืนรอบนี้</param>
public readonly record struct PosRefundLine(
    decimal LineGross,
    decimal LineVat,
    decimal LineQuantity,
    decimal RefundQuantity);

/// <summary>ยอดที่ต้องคืนลูกค้า (ปัดแล้ว) + สัดส่วนที่ใช้คำนวณ เพื่อให้ผู้เรียก/เทสต์ตรวจได้
///
/// <para><paramref name="Gross"/> = เงินที่ออกจากลิ้นชักจริง (รวม VAT **และรวมค่าบริการ**
/// ที่คืนไปกับของ) · <paramref name="ServiceCharge"/> คือส่วนของค่าบริการที่อยู่ใน
/// <paramref name="Gross"/> ก้อนนั้น (แยกมาให้รายงาน/ใบลดหนี้อ่าน ไม่ใช่ยอดเพิ่ม) ⇒
/// <c>Goods + ServiceCharge = Gross</c> เสมอ</para></summary>
public readonly record struct PosRefundAmounts(
    decimal Gross,
    decimal Vat,
    decimal Net,
    decimal DiscountFactor,
    decimal ServiceCharge,
    decimal Goods);

/// <summary>
/// **สูตรคืนเงิน POS ตัวเดียวของระบบ** — ลูกค้าได้คืนตามสัดส่วนของ "เงินที่จ่ายจริง"
/// ไม่ใช่ราคาป้ายรวมรายบรรทัด
///
/// ═══ ที่มา (DECISION_AUDIT_2026-09-18 · D8-2 · P0 เงินจริง) ═══
/// เดิม <c>RefundOrderAsync</c> คิดส่วนลดระดับบิลเป็น
/// <c>DiscountAmount + CouponDiscountAmount</c> แต่ <c>PosService.RecalculateOrder</c>
/// เก็บ <c>DiscountAmount = ส่วนลด% + CouponDiscountAmount</c> อยู่แล้ว ⇒
/// **คูปองถูกหักสองครั้ง** ⇒ <c>discountFactor</c> ต่ำกว่าความจริง ⇒
/// <b>คืนเงินลูกค้าต่ำกว่าที่ควร</b> (บิล 1,000 คูปอง 100 ลูกค้าจ่าย 900 แต่คืนเต็มใบได้แค่ 800)
///
/// ═══ ทำไมต้องเป็นฟังก์ชันบริสุทธิ์ ═══
/// เป็นตัวเลขที่ออกจากลิ้นชักจริง แต่เดิมฝังอยู่กลางเมธอดที่แตะฐานข้อมูล/สต็อก/JE
/// จึงไม่มีเทสต์แม้แต่ตัวเดียว (grep <c>discountFactor</c> ใน Accounting.Tests = 0)
/// — กฎเหล็ก #4 G: logic เงินใหม่ต้อง extract เป็น pure class + เทสต์ในคอมมิตเดียวกัน
///
/// ═══ ขอบเขต ═══
/// <list type="bullet">
/// <item><b>ค่าบริการ (ServiceCharge) คืนไปกับของ</b> (คำตัดสินเจ้าของโปรเจกต์ รอบ 184 —
///   "คืนทั้งหมด") · ค่าบริการคิดจาก<b>ยอดหลังส่วนลด</b> (<c>RecalculateOrder</c> เป็นเจ้าของ
///   นิยาม) ⇒ เงินที่ลูกค้าจ่ายสำหรับของ 1 บรรทัดคือ
///   <c>LineGross × ratio × discountFactor × (1 + ServiceChargePercent/100)</c>
///   <para>เดิมไม่คืนค่าบริการ ⇒ บิล 1,000 ลด 10% ค่าบริการ 10% ลูกค้าจ่าย 990 แต่คืนทั้งใบ
///   ได้แค่ 900 — <b>ร้านเก็บค่าบริการของของที่ส่งคืนไว้เอง</b> และ JE เหลือรายได้/ภาษีขาย
///   ค้างอยู่ 90/5.89 บาทที่ไม่มีวันถูกกลับรายการ</para></item>
/// <item><b>ทิป (TipAmount) ไม่คืน</b> — เป็นเงินที่บริษัทถือแทนพนักงาน (JE เครดิต**หนี้สิน**
///   21814 ไม่ใช่รายได้) และมักจ่ายออกไปแล้วผ่าน <c>TipPayoutService</c> ⇒ การคืนทิปคือการ
///   **ถอนหนี้สินที่อาจถูกเคลียร์ไปแล้ว** ต้องมีเส้นของตัวเอง (ดูรายงานรอบ 184 ข้อ 1)</item>
/// <item><b>ค่าปัดเศษ (RoundingAmount) ไม่คืน</b> — เป็นเศษสตางค์ระดับ**บิล** (≤ 0.50 บาท)
///   ที่ไม่ผูกกับบรรทัดใด: คืนครึ่งบิลแล้วแบ่งเศษปัดครึ่งหนึ่ง = ตัวเลขที่ไม่มีอยู่จริงบน
///   กระดาษ · ผลคือคืนเต็มใบได้ <c>TotalAmount</c> (ไม่ใช่ <c>NetAmount</c>) ส่วนต่าง ≤ 0.50
///   บาทค้างอยู่ในรายได้ตามที่ลงไว้ตอนขาย ซึ่งเป็นทิศที่<b>เห็นได้</b>ในรายงานกะ</item>
/// </list>
/// </summary>
public static class PosRefundMath
{
    /// <summary>สัดส่วน "เงินที่จ่ายจริง ÷ ราคาป้าย" ของบิลใบนั้น
    ///
    /// <para><paramref name="orderDiscountAmount"/> ต้องเป็น <c>PosOrder.DiscountAmount</c>
    /// **ตัวเดียว** — ห้ามบวก <c>CouponDiscountAmount</c> เข้าไปอีก เพราะ
    /// <c>RecalculateOrder</c> รวมคูปองไว้ในค่านั้นแล้ว (นี่คือบั๊ก D8-2)</para>
    ///
    /// <para>ผลลัพธ์ถูกบีบอยู่ในช่วง 0–1: ส่วนลดเกินราคาป้าย = คืน 0 (ลูกค้าไม่ได้จ่าย)
    /// · ส่วนลดติดลบ/ไม่มี = คืนเต็ม 1 เท่า (ห้ามคืนเกินราคาป้าย)</para>
    ///
    /// <para><b>internal โดยตั้งใจ</b> — ทางเข้าสาธารณะของ helper นี้มีตัวเดียวคือ
    /// <see cref="Compute"/> (ผู้เรียกที่ได้แต่ factor มักลืมคูณ ratio รายบรรทัด) ·
    /// ค่าที่คำนวณได้ถูกส่งกลับบน <see cref="PosRefundAmounts.DiscountFactor"/> ให้ผู้เรียก
    /// แสดง/ตรวจได้ · เทสต์เข้าถึงผ่าน <c>InternalsVisibleTo(Accounting.Tests)</c></para></summary>
    internal static decimal DiscountFactor(decimal lineGrossTotal, decimal orderDiscountAmount)
    {
        if (lineGrossTotal <= 0m) return 1m;
        var factor = (lineGrossTotal - orderDiscountAmount) / lineGrossTotal;
        if (factor < 0m) return 0m;
        return factor > 1m ? 1m : factor;
    }

    /// <summary>ตัวคูณค่าบริการ — เงินที่ลูกค้าจ่ายต่อ "ของ 1 บาท"
    ///
    /// <para><c>RecalculateOrder</c> คิด <c>ServiceCharge = afterDiscount × pct/100</c>
    /// แล้วบวกเข้า <c>TotalAmount</c> ⇒ ของทุกบาทบนบิลถูกคิดเงินเป็น <c>1 + pct/100</c> บาท
    /// การคืนจึงต้องคูณกลับด้วยตัวเดียวกัน มิฉะนั้นคืนไม่ครบที่จ่าย</para>
    ///
    /// <para>เปอร์เซ็นต์ติดลบ (ส่วนลดค่าบริการ) ก็คูณกลับตามจริง — ลูกค้าจ่ายน้อยกว่าของ
    /// จึงได้คืนน้อยกว่าของ · บีบที่ 0 เพื่อไม่ให้ "คืนเงินติดลบ" (= เรียกเก็บเพิ่มตอนคืนของ)
    /// ซึ่งไม่มีทางถูกไม่ว่าข้อมูลจะเพี้ยนแค่ไหน</para></summary>
    internal static decimal ServiceChargeFactor(decimal serviceChargePercent)
    {
        var f = 1m + serviceChargePercent / 100m;
        return f < 0m ? 0m : f;
    }

    /// <summary>ยอดคืนรวมของหลายบรรทัด — ปัดทศนิยม**ครั้งเดียวที่ยอดรวม**
    /// (ปัดรายบรรทัดแล้วบวกกัน ทำให้เศษสตางค์สะสมเกินจริง)
    ///
    /// <para><paramref name="serviceChargePercent"/> = <c>PosOrder.ServiceChargePercent</c>
    /// ที่ตรึงไว้ตอนปิดบิล (ออเดอร์ที่ <c>Completed</c> แก้ไม่ได้แล้ว — <c>UpdateOrderAsync</c>
    /// โยนทิ้ง ⇒ เปอร์เซ็นต์กับ <c>ServiceChargeAmount</c> ตรงกันเสมอ)
    /// <b>ไม่มีค่า default โดยตั้งใจ</b> — ให้คอมไพเลอร์บังคับให้ทุกผู้เรียกใหม่ตอบว่า
    /// "บิลนี้มีค่าบริการกี่เปอร์เซ็นต์" แทนที่จะเงียบ ๆ คืนขาดเหมือนเดิม</para>
    ///
    /// <para><b>Invariant</b>: คืนทุกบรรทัดเต็มจำนวนของบิลที่ยอดลงตัวที่ 2 ตำแหน่ง
    /// ⇒ <c>Gross == order.TotalAmount</c> เป๊ะ (ไม่รวม <c>RoundingAmount</c>/<c>TipAmount</c>)
    /// และ <c>Vat == order.VatAmount</c> ⇒ JE คืนเงินกลับรายการของ JE ขายได้ครบทุกบรรทัด</para></summary>
    public static PosRefundAmounts Compute(
        decimal lineGrossTotal,
        decimal orderDiscountAmount,
        decimal serviceChargePercent,
        IEnumerable<PosRefundLine> lines)
    {
        var factor = DiscountFactor(lineGrossTotal, orderDiscountAmount);
        var svcFactor = ServiceChargeFactor(serviceChargePercent);
        decimal goods = 0m, goodsVat = 0m;
        foreach (var line in lines)
        {
            if (line.RefundQuantity <= 0m || line.LineQuantity <= 0m) continue;
            var ratio = line.RefundQuantity / line.LineQuantity;
            goods += line.LineGross * ratio * factor;
            goodsVat += line.LineVat * ratio * factor;
        }
        var gross = Math.Round(goods * svcFactor, 2, MidpointRounding.AwayFromZero);
        var vat = Math.Round(goodsVat * svcFactor, 2, MidpointRounding.AwayFromZero);
        // ส่วนของค่าบริการหักจาก "ยอดของที่ปัดแล้ว" เพื่อให้ Goods + ServiceCharge = Gross
        // เป๊ะเสมอ (ปัดสองก้อนอิสระแล้วบวกกันจะคลาดกับ Gross ได้ 0.01)
        var goodsRounded = Math.Round(goods, 2, MidpointRounding.AwayFromZero);
        return new PosRefundAmounts(gross, vat, gross - vat, factor, gross - goodsRounded, goodsRounded);
    }
}
