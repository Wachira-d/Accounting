using Accounting.Models.DTOs.Lodging;

namespace Accounting.Helpers;

/// <summary>ส่วนลดเช็คเอาต์ที่เซิร์ฟเวอร์คิดจากค่าที่พนักงานกรอก (รอบ 205 แพ็ก W5 · R-49)</summary>
/// <param name="Entered">ค่าที่พนักงานกรอก (บาทตามราคาที่แขกเห็น หรือ %)</param>
/// <param name="IsPercent">กรอกเป็น % ของยอดเข้าพัก</param>
/// <param name="BaseAmount">ส่วนลดเป็น<b>ฐานก่อน VAT</b> — ค่าที่ส่งเข้า <c>CreateDocumentRequest.BillDiscountAmount</c>
/// (ตัวเฉลี่ยลงบรรทัดของเอกสารรับหน่วยนี้ · ห้ามส่ง % เพราะ <c>AllocateBillDeductions</c> ปฏิเสธ % คู่กับฐานมัดจำ)</param>
/// <param name="Reason">เหตุผล (บังคับเมื่อมีส่วนลด · ลง audit + หมายเหตุใบ)</param>
public sealed record LodgingCheckoutDiscountPlan(decimal Entered, bool IsPercent, decimal BaseAmount, string? Reason)
{
    public bool HasDiscount => BaseAmount > 0.005m;
    /// <summary>ไม่มีส่วนลด — เส้นเช็คเอาต์เดินตัวเลขเดิมทุกสตางค์ (ทิศตรงข้ามของ R-49)</summary>
    public static readonly LodgingCheckoutDiscountPlan None = new(0m, false, 0m, null);
}

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของส่วนลดตอนเช็คเอาต์ที่พัก (ทะเบียนงานคงค้าง 2026-10-26 R-49 · คำตัดสินข้อ 157 "ยอดลด = ใบลดหนี้อ้างใบเดิม")
/// <para><b>หลักเลขคณิต (ไม่ต้องถามเจ้าของ)</b>: ส่วนลดเป็นส่วนของ<i>ราคา</i> · มัดจำเป็น<i>การชำระ</i> ⇒ ฐานภาษีของใบเช็คเอาต์ =
/// (ราคาเข้าพักหลังส่วนลด) − ฐานมัดจำที่ออกใบกำกับแล้ว · ส่วนลดจึงต้องเข้า <c>PreviewTotals(lines, …, billDiscount)</c> <b>ก่อน</b>วางแผนมัดจำ
/// (<c>LodgingDepositSettlement.PlanCheckout</c> รับความจุ = ฐานหลังส่วนลด) — เดิมหน้าไม่ส่งส่วนลดเลย และถ้าส่งแบบเดิมส่วนลดจะไปปนฐานหักมัดจำ
/// · <b>ติดลบ</b> (ราคาหลังส่วนลด &lt; มัดจำที่ออกใบกำกับแล้ว) ⇒ <c>PlanCheckout</c> หักได้เท่าความจุ ส่วนที่เหลือ = ค้างคืนแขก ⇒ เมื่อกด
/// “ยืนยันคืนเงินแล้ว” เส้นเดิม <c>RefundDepositAsync</c> ออก<b>ใบลดหนี้อ้างใบมัดจำ</b> (§86/10 เหตุ "ปรับยอด") — ไม่ออกใบลดหนี้ก่อนมีหลักฐานคืนเงิน
/// (DECISION_DOCTRINE R1 · ไม่เขียน posting ใหม่)</para>
/// <para>หน่วยที่พนักงานกรอก = ราคาที่แขกเห็น: ที่พักตั้งราคารวม VAT ⇒ บาทที่กรอกเป็นยอดรวม VAT แล้วแยกฐานด้วย
/// <see cref="LodgingDepositSettlement.SplitInclusive"/> (อัตราของที่พัก) · ราคาก่อน VAT ⇒ บาทที่กรอก = ฐานตรง ๆ · % คิดจากฐานเข้าพัก</para>
/// </summary>
public static class LodgingCheckoutDiscount
{
    public const string RuleCode = "LODGING-CHECKOUT-DISCOUNT";
    public const string LegalReference = "RD-86/10";
    public const int ReasonMaxLength = 200;

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>แปลงค่าที่กรอก → แผนส่วนลด · <c>Problem</c> = ข้อความไทยที่ปฏิเสธ (null = ผ่าน) — ไม่กรอก/0 ⇒ <see cref="LodgingCheckoutDiscountPlan.None"/></summary>
    /// <param name="fullNet">ฐานก่อน VAT ของการเข้าพักทั้งหมดก่อนส่วนลด (จาก <c>PreviewTotals(lines, …, 0m).Net</c>)</param>
    public static (LodgingCheckoutDiscountPlan Plan, string? Problem) Resolve(
        decimal? amount, decimal? percent, string? reason, decimal fullNet, bool pricesIncludeVat, decimal vatRate)
    {
        var amt = R2(amount ?? 0m);
        var pct = percent ?? 0m;
        if (amt < 0m || pct < 0m) return (LodgingCheckoutDiscountPlan.None, "ส่วนลดต้องไม่ติดลบ");
        if (amt > 0.005m && pct > 0m) return (LodgingCheckoutDiscountPlan.None, "ระบุส่วนลดเป็นจำนวนเงิน หรือเป็นเปอร์เซ็นต์ อย่างใดอย่างหนึ่ง");
        if (pct > 100m) return (LodgingCheckoutDiscountPlan.None, "ส่วนลดเป็นเปอร์เซ็นต์ต้องไม่เกิน 100");
        if (amt <= 0.005m && pct <= 0m) return (LodgingCheckoutDiscountPlan.None, null);

        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why)) return (LodgingCheckoutDiscountPlan.None, "ระบุเหตุผลของส่วนลด (บันทึกบนใบและ audit)");
        if (why.Length > ReasonMaxLength) why = why[..ReasonMaxLength];

        var isPercent = pct > 0m;
        var baseAmount = isPercent
            ? R2(Math.Max(0m, fullNet) * pct / 100m)
            : (pricesIncludeVat && vatRate > 0m ? LodgingDepositSettlement.SplitInclusive(amt, vatRate).Base : amt);
        if (baseAmount > fullNet + 0.005m)
            return (LodgingCheckoutDiscountPlan.None,
                $"ส่วนลด {amt:N2} (ฐาน {baseAmount:N2}) เกินยอดการเข้าพักก่อน VAT ({fullNet:N2}) — ลดจำนวนส่วนลด หรือใช้ % แทน");
        return (new LodgingCheckoutDiscountPlan(isPercent ? pct : amt, isPercent, baseAmount, why), null);
    }

    /// <summary>ทางเข้าเดียวของเส้นเช็คเอาต์ (ปกติ + บนใบล่วงหน้า) — อ่านช่องส่วนลดจากคำขอที่นี่ที่เดียว (ตัวเรียกไม่แตะช่องเอง)</summary>
    public static (LodgingCheckoutDiscountPlan Plan, string? Problem) Resolve(LodgingCheckOutRequest request, decimal fullNet, bool pricesIncludeVat, decimal vatRate)
        => Resolve(request.BillDiscountAmount, request.BillDiscountPercent, request.BillDiscountReason, fullNet, pricesIncludeVat, vatRate);

    /// <summary>หมายเหตุภายใน/หมายเหตุใบเมื่อมีส่วนลด</summary>
    public static string Note(LodgingCheckoutDiscountPlan p)
        => $"ส่วนลดเช็คเอาต์ {p.Entered:N2}{(p.IsPercent ? "%" : " บาท")} (ฐานก่อน VAT {p.BaseAmount:N2}) — เหตุผล: {p.Reason}";

    /// <summary>ส่วนลดทำให้ยอดเข้าพักต่ำกว่ามัดจำที่รับไว้ — ทางไปต่อตัวเดียวกับมัดจำเกินยอด (ค้างคืน → ใบลดหนี้อ้างใบมัดจำตอนยืนยันคืนเงิน)</summary>
    public static string NegativeNote(decimal excessGross)
        => $"ส่วนลดทำให้ยอดเข้าพักต่ำกว่ามัดจำที่รับไว้ — ส่วนต่าง {excessGross:N2} ค้างคืนแขก · กด “ยืนยันคืนเงินแล้ว” เมื่อโอนคืนจริง "
         + "(ระบบออกใบลดหนี้อ้างใบมัดจำ §86/10 เหตุ “ปรับยอด” ตอนนั้น — ไม่ออกก่อนมีหลักฐานคืนเงิน)";
}
