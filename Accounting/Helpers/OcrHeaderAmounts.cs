namespace Accounting.Helpers;

/// <summary>
/// **ยอดสามช่องบนหัวใบ (ก่อน VAT · VAT · รวมทั้งสิ้น) — ซ่อมป้ายที่ engine ติดสลับ**
///
/// <para>ที่มา (สแกนจริง 2026-09-10 · บริษัท ลักกี้ เวย์ · HS6909180): กระดาษพิมพ์
/// “จำนวนเงินรวมทั้งสิ้น 667.00” **ก่อน** “จำนวนภาษีมูลค่าเพิ่ม 43.64” และ
/// “ราคาสินค้า 623.36” ซึ่งกลับลำดับจากฟอร์มทั่วไป ⇒ Azure DI ติดป้าย
/// SubTotal=667 / Total=623.36 (สลับกัน) · ด่านคณิตศาสตร์ฟ้อง “667 + 43.64 ≠ 623.36”
/// แต่**ไม่มีใครลองสลับกลับ** ทั้งที่ 623.36 + 43.64 = 667.00 ลงตัวเป๊ะ ⇒ ตัวจำแนก
/// บรรทัดเห็น Σ บรรทัด 667 = SubTotal 667 จึงตีว่า “ราคาแยก VAT” แล้วเอกสารที่สร้าง
/// เอา 239 (ราคารวม VAT บนกระดาษ) ไปเป็นราคาก่อน VAT แล้วบวก VAT ซ้ำ</para>
///
/// <para>กติกา: นี่**ไม่ใช่การแต่งตัวเลข** — ค่าทั้งสามเป็นค่าที่ engine อ่านมาจริง แค่
/// ป้ายกลับด้าน จึงยอมสลับได้เมื่อ (1) ทั้งสามค่าเป็นบวก (2) “SubTotal” มากกว่า
/// “Total” ซึ่งเป็นไปไม่ได้เมื่อมี VAT (3) Total + VAT = SubTotal ภายในค่าเผื่อ
/// ⇒ ยืนยันด้วยคณิตศาสตร์ว่าเป็นการสลับ ไม่ใช่การอ่านผิดคนละเรื่อง</para>
///
/// <para>ใช้ตัวเดียวทั้งขั้นสกัด (SmartFieldExtractor) · ตัวจำแนกบรรทัด
/// (OcrLineReconciler) · เส้นสร้างเอกสาร (สแกนเก่าที่ persist ค่าสลับไว้แล้ว) —
/// ห้ามเขียนเงื่อนไขสลับซ้ำที่อื่น</para>
/// </summary>
public static class OcrHeaderAmounts
{
    /// <summary>ค่าเผื่อปัดเศษ (บาท) — เท่ากับ <see cref="OcrLineReconciler.Tolerance"/></summary>
    public const decimal Tolerance = 1m;

    /// <summary>true เมื่อ (sub, vat, total) เป็นการติดป้ายสลับของ (total, vat, sub)</summary>
    public static bool IsSwapped(decimal? subTotal, decimal? vat, decimal? total)
    {
        if (subTotal is not > 0m || vat is not > 0m || total is not > 0m) return false;
        if (subTotal.Value <= total.Value) return false;
        return Math.Abs(subTotal.Value - (total.Value + vat.Value)) <= Tolerance;
    }

    /// <summary>คืนสามค่าที่ป้ายถูกต้อง — ถ้าไม่ได้สลับ คืนค่าเดิมทุกตัว</summary>
    public static (decimal? SubTotal, decimal? Vat, decimal? Total, bool Swapped) Normalize(
        decimal? subTotal, decimal? vat, decimal? total)
        => IsSwapped(subTotal, vat, total)
            ? (total, vat, subTotal, true)
            : (subTotal, vat, total, false);

    /// <summary>
    /// **ยอดก่อน VAT "หลังหักส่วนลด" ของหัวใบ** — ค่าที่ลง <c>Document.SubTotal</c> (= ฐานภาษี §87)
    ///
    /// <para>ที่มา (รอบ 190 ข้อ 9 · ส่วนลดท้ายบิล): กระดาษไทยพิมพ์ "รวมเงิน 1,000 · ส่วนลด 50 ·
    /// ภาษี 66.50 · รวมทั้งสิ้น 1,016.50" — ตัวเลือกเดิม (<c>OcrService.ResolveHeaderSubTotal</c>)
    /// เห็นว่า 1,000 + 66.50 − 50 = 1,016.50 "ผูกกับยอดรวม" จึงคืน <b>1,000 (ก่อนหักส่วนลด)</b>
    /// ⇒ <c>Document.SubTotal</c> = 1,000 ทั้งที่บรรทัดรวม 950 · รายงานภาษีซื้อ §87 อ่าน
    /// <c>SubTotal</c> เป็นมูลค่าฐาน (<c>TaxService.VatableBase</c>) ⇒ ฐาน 1,000 คู่กับ VAT 66.50
    /// (6.65%) และพอผู้ใช้เปิดแก้แล้วบันทึก <c>DocumentService</c> คิด SubTotal ใหม่เป็น 950
    /// ⇒ "ยอดเปลี่ยนเองตอนกดบันทึก" · สัญญาของระบบคือ <c>SubTotal</c> = ยอด<b>หลัง</b>หักส่วนลด
    /// (Σ NetAmount ของบรรทัด)</para>
    ///
    /// <para>กติกา: มีส่วนลดบนกระดาษ ⇒ ยอดก่อน VAT = ยอดรวม − VAT (ค่าที่อ่านมาจริงสองช่อง —
    /// ไม่ใช่ค่าที่แต่ง) · ไม่มีส่วนลด ⇒ พฤติกรรมเดิมทุกประการ (ใช้ยอดก่อน VAT ที่อ่านได้เมื่อ
    /// ผูกกับยอดรวม · ไม่ผูก ⇒ ถอยจากยอดรวม) · ไม่รู้ยอดรวม ⇒ คืนค่าที่อ่านได้ (ไม่มีหลักฐานให้ถอด)</para>
    /// </summary>
    public static decimal NetSubTotal(decimal? subTotal, decimal? vat, decimal? total, decimal discount)
    {
        var t = total ?? 0m;
        var v = vat ?? 0m;
        if (t <= 0m) return subTotal ?? 0m;
        var fromTotal = Math.Max(0m, t - v);
        if (discount > 0m) return fromTotal;
        var ties = subTotal is > 0m && Math.Abs(subTotal.Value + v - t) <= Tolerance;
        return ties ? subTotal!.Value : fromTotal;
    }

    /// <summary>
    /// **"ยอดก่อน VAT" ที่ถืออยู่คือยอดรวมทั้งสิ้น** (รอบ 242 · ใบเสร็จซูเปอร์มาร์เก็ต 2026-11-06: engine อ่าน SubTotal = Total = 1,961.25 คู่ VAT 30.03 ⇒
    /// ตัวกระทบยอดฟ้อง "น้อยกว่ายอดก่อน VAT 1,961.25" ทั้งที่ฐานจริง 1,931.22) — ยอดก่อน VAT เท่ากับยอดรวมพอดีขณะที่ VAT &gt; 0 เป็นไปไม่ได้ทางเลขคณิต ·
    /// ต่างจาก <see cref="IsSwapped"/> (สองป้ายสลับกัน) — ที่นี่ engine ใช้ตัวเลขเดียวกันสองช่อง · เทียบตรงถึงสตางค์ (ไม่ใช้ค่าเผื่อ 1 บาท — ใบเล็กที่ VAT &lt; 1 บาท
    /// เช่น 10.00 + 0.70 = 10.70 ต้องไม่เข้าเงื่อนไข) · ผู้ใช้: ฐานกระทบยอดของ <see cref="OcrVatCodeLines.Decide"/> · ขั้นเขียนหัวสแกน <see cref="OcrTotalAnchor.Plan"/>
    /// </summary>
    public static bool SubTotalIsGrandTotal(decimal? subTotal, decimal? vat, decimal? total)
        => subTotal is > 0m && vat is > 0.01m && total is > 0m && Math.Abs(subTotal.Value - total.Value) < 0.01m;

    /// <summary>
    /// **ฐานก่อน VAT ที่ใช้กระทบยอดกับ Σ บรรทัด** — ตัวตัดสินเดียวของตัวสร้างบรรทัด (<see cref="OcrVatCodeLines.Decide"/>) และด่านความมั่นใจตอนสแกน
    /// (<c>OcrConfidenceGateway.Validate</c>) · ยอดก่อน VAT = ยอดรวม (<see cref="SubTotalIsGrandTotal"/>) <b>และ</b>ยอดรวมนั้นคือยอดที่กระดาษพิสูจน์แล้ว
    /// (<paramref name="paperTotal"/> จาก <see cref="OcrTotalAnchor.Find"/> Proven/Confirmed) ⇒ ยอดรวม − VAT · อย่างอื่น ⇒ <paramref name="subTotal"/> เดิมทุกตัวอักษร
    /// (ฝ่ายค้านรอบ 242 S3: sub = total = 1,000 · VAT 70 · ยอดจริง 1,070 ที่ไม่ได้พิสูจน์ — "ยอดรวม" ต่างหากที่ผิด ห้ามถอด VAT ออกจากมัน)
    /// </summary>
    public static decimal ReconSubTotal(decimal subTotal, decimal? vat, decimal? total, decimal? paperTotal)
        => SubTotalIsGrandTotal(subTotal, vat, total) && paperTotal is decimal pt && Math.Abs(pt - total!.Value) < 0.01m
            ? total.Value - vat!.Value
            : subTotal;

    /// <summary>ข้อความเดียวที่ทุกจุดใช้บันทึกใน trace/ProcessingNotes เมื่อสลับกลับ
    /// (สองที่แต่งข้อความเองจะ drift)</summary>
    public static string SwapNote(decimal subBefore, decimal totalBefore)
        => $"[Σ-SWAP] engine ติดป้าย SubTotal/Total สลับกัน ({subBefore:N2} ↔ {totalBefore:N2}) — "
         + $"สลับกลับเพราะ {totalBefore:N2} + VAT = {subBefore:N2} ลงตัวพอดี";

    /// <summary>
    /// รอบ 259 (C-M2 · คำตัดสิน 259-4-9): ผู้ใช้แก้<b>เฉพาะยอดรวม</b> (มือถือเบิกค่าใช้จ่าย/หน้ารีวิวส่งแค่ <c>totalAmount</c>) ⇒ ถอดฐาน/VAT ใหม่จากยอดที่ผู้ใช้พิมพ์ ·
    /// เดิมฐาน/VAT เก่าค้าง ⇒ ฐาน = ยอดใหม่ − VAT เก่า (VAT ไม่ใช่ 7% ของฐาน) · กติกา: (1) ยอดไม่เปลี่ยน/ไม่มียอดใหม่ ⇒ คงเดิม
    /// (2) ใบเดิมไม่มี VAT ⇒ ฐาน = ยอดใหม่ · VAT 0 (3) ใบเดิม VAT 7% ของฐาน (±0.05) ⇒ ถอด 7/107 ด้วยสูตรตัวเดียว <see cref="OcrVatBackCalc.SplitInclusive"/>
    /// (4) ใบเดิมไม่ใช่ 7% ล้วน (ผสมยกเว้น/อัตราอื่น) ⇒ คง VAT เดิม ฐาน = ยอดใหม่ − VAT (ไม่เดาสัดส่วน — ด่าน [Σ-GAP] ตอนสร้างฟ้องเมื่อไม่ลง) · pure
    /// </summary>
    public static (decimal? SubTotal, decimal? Vat, string? Note) RederiveAfterTotalEdit(decimal? oldSub, decimal? oldVat, decimal? oldTotal, decimal? newTotal)
    {
        if (newTotal is not > 0m || (oldTotal is decimal ot && ot == newTotal.Value)) return (oldSub, oldVat, null);
        var total = newTotal.Value;
        var vat = oldVat ?? 0m;
        if (vat == 0m)
            return (total, 0m, $"[TOTAL-EDIT] ผู้ใช้แก้ยอดรวมเป็น {total:N2} — ใบไม่มี VAT ⇒ ฐาน = ยอดรวม");
        if (oldSub is decimal os && os > 0m && Math.Abs(Math.Round(os * 0.07m, 2, MidpointRounding.AwayFromZero) - vat) <= 0.05m)
        {
            var (sub, v) = OcrVatBackCalc.SplitInclusive(total);
            return (sub, v, $"[TOTAL-EDIT] ผู้ใช้แก้ยอดรวมเป็น {total:N2} — ถอด VAT 7% ใหม่: ฐาน {sub:N2} · VAT {v:N2}");
        }
        return (total - vat, vat, $"[TOTAL-EDIT] ผู้ใช้แก้ยอดรวมเป็น {total:N2} — VAT เดิม {vat:N2} ไม่ใช่ 7% ของฐานล้วน ⇒ คง VAT ฐาน = ยอดรวม − VAT (ตรวจก่อนยืนยัน)");
    }
}
