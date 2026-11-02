namespace Accounting.Helpers;

/// <summary>
/// รอบ 227 (PLATFORM_OMISE_AUDIT C-2/C-3) — ขาเงินเข้า + ฐานก่อน VAT ของเงินค่าบริการ NextAcc ที่รับแล้ว · pure
/// <para><b>ขาเงินเข้า:</b> รับผ่านผู้ให้บริการรับชำระ (gateway ถือเงิน T+n) ⇒ ผังที่ <c>IGatewayAccountResolver</c> ให้ (บัญชีพัก 11340) <b>ชนะ</b>ผังเงินสด/ธนาคาร ·
/// ไม่ใช่ gateway/ผู้ให้บริการโอนเข้าธนาคารทันที/ไม่มีผังพัก (resolver คืน null + log) ⇒ ผังเงินสด/ธนาคารที่ตั้ง (เดิม) · ทั้งคู่ null ⇒ ค่าเริ่มต้นของ DocumentService</para>
/// </summary>
public static class PlatformMoneyInAccount
{
    public static Guid? Pick(Guid? gatewayMoneyInAccountId, Guid? cashAccountId) => gatewayMoneyInAccountId ?? cashAccountId;

    /// <summary>ฝ่ายค้านรอบ 227 S2 — แยก "เงินที่รับแล้ว" เป็นฐาน + VAT โดย <b>ผลรวม = เงินที่รับพอดี</b> (ใบเสร็จ/ใบกำกับขายเงินสดห้ามสูงหรือต่ำกว่าเงินที่รับแม้ 1 สตางค์) ·
    /// จด VAT ⇒ ฐาน = round(A × 100/107, 2, ปัดครึ่งขึ้น) · VAT = A − ฐาน (บังคับผ่าน <c>VatAmountOverride</c> — ไม่ให้เอกสารคิด ฐาน × 7% ใหม่แล้วเหลือเศษ) ·
    /// ไม่จด VAT ⇒ (A, 0)
    /// <para><b>เงินที่รับแล้วถือว่ารวม VAT เสมอ</b> (ไม่ขึ้นกับค่าตั้ง "ราคาไม่รวม VAT"): ยอดเรียกเก็บของแพ็กเกจ/ส่วนเสริมคือราคาตั้งโดยไม่บวก VAT ⇒ ถ้าออกใบ = ราคา + VAT
    /// จะเป็นใบที่ยอดสูงกว่าเงินที่ได้รับ · ค่าตั้ง "ราคาไม่รวม VAT" + จด VAT จึงเป็นด่านเปิดรับเงินออนไลน์ (<see cref="PlatformGatewayReadiness"/>) แทนการบวกยอดเงียบ ๆ</para></summary>
    public static (decimal Base, decimal Vat) SplitReceived(decimal amountReceived, bool vatRegistered)
    {
        if (!vatRegistered) return (amountReceived, 0m);
        var b = decimal.Round(amountReceived * 100m / 107m, 2, MidpointRounding.AwayFromZero);
        return (b, amountReceived - b);
    }
}
