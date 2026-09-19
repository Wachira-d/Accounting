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

/// <summary>ยอดที่ต้องคืนลูกค้า (ปัดแล้ว) + สัดส่วนที่ใช้คำนวณ เพื่อให้ผู้เรียก/เทสต์ตรวจได้</summary>
public readonly record struct PosRefundAmounts(
    decimal Gross,
    decimal Vat,
    decimal Net,
    decimal DiscountFactor);

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
/// <item>ค่าบริการ (ServiceCharge) และทิป (Tip) เป็นรายการ**เพิ่ม**บนบิล ไม่คืนตามการคืนสินค้า
///   — ผู้เรียกจึงส่งเฉพาะยอดรายบรรทัดเข้ามา (พฤติกรรมเดิม คงไว้โดยตั้งใจ)</item>
/// <item>ค่าปัดเศษ (RoundingAmount) ไม่ถูกคืน — เป็นเศษสตางค์ระดับบิล ไม่ผูกกับบรรทัด</item>
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

    /// <summary>ยอดคืนรวมของหลายบรรทัด — ปัดทศนิยม**ครั้งเดียวที่ยอดรวม**
    /// (ปัดรายบรรทัดแล้วบวกกัน ทำให้เศษสตางค์สะสมเกินจริง)</summary>
    public static PosRefundAmounts Compute(
        decimal lineGrossTotal,
        decimal orderDiscountAmount,
        IEnumerable<PosRefundLine> lines)
    {
        var factor = DiscountFactor(lineGrossTotal, orderDiscountAmount);
        decimal gross = 0m, vat = 0m;
        foreach (var line in lines)
        {
            if (line.RefundQuantity <= 0m || line.LineQuantity <= 0m) continue;
            var ratio = line.RefundQuantity / line.LineQuantity;
            gross += line.LineGross * ratio * factor;
            vat += line.LineVat * ratio * factor;
        }
        gross = Math.Round(gross, 2, MidpointRounding.AwayFromZero);
        vat = Math.Round(vat, 2, MidpointRounding.AwayFromZero);
        return new PosRefundAmounts(gross, vat, gross - vat, factor);
    }
}
