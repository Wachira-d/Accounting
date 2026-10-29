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

    /// <summary>หมายเหตุ<b>ภายใน</b>เมื่อมีส่วนลด (มีเหตุผล — ลง InternalNotes ของการจอง + audit เท่านั้น · ห้ามพิมพ์บนเอกสารที่แขกเห็น ·
    /// รอบ 208 ฝ่ายค้าน W5 ข้อ 9 — ใช้ <see cref="GuestNote"/> บนเอกสาร)</summary>
    public static string Note(LodgingCheckoutDiscountPlan p)
        => $"ส่วนลดเช็คเอาต์ {p.Entered:N2}{(p.IsPercent ? "%" : " บาท")} (ฐานก่อน VAT {p.BaseAmount:N2}) — เหตุผล: {p.Reason}";

    /// <summary>ข้อความส่วนลดบน<b>เอกสารที่แขกเห็น</b> (หมายเหตุใบเช็คเอาต์ · ใบแทน · ใบลดหนี้อ้างใบล่วงหน้า · ใบส่วนเพิ่ม) — <b>ไม่มีเหตุผลภายใน</b>
    /// (เหตุผลอยู่ใน InternalNotes ของการจอง + audit) · รับฐานก่อน VAT อย่างเดียว ⇒ ใบแทนที่รู้แค่ <c>BillDiscountAmount</c> ของใบเดิมพิมพ์ข้อความเดียวกันได้
    /// (รอบ 208 ฝ่ายค้าน W5 ข้อ 9)</summary>
    public static string GuestNote(decimal baseAmount) => $"ส่วนลดเช็คเอาต์ (ฐานก่อน VAT {R2(baseAmount):N2})";

    /// <summary>คำบรรยายรายการ folio ติดลบของส่วนลดเช็คเอาต์ในโหมดไม่ออกเอกสาร (รอบ 208 ฝ่ายค้าน W5 ข้อ 3) — แขกเห็นบนใบยืนยันการจอง ⇒ ไม่มีเหตุผลภายใน</summary>
    public const string OffModeChargeDescription = "ส่วนลดเช็คเอาต์";

    /// <summary>
    /// ต้องตรวจ "VAT ใบสุดท้าย = VAT ของฐานหลังหักฐานมัดจำ" ไหม — เฉพาะเมื่อหักฐานมัดจำ (ฐานมัดจำออกใบกำกับที่อัตราเดียว แต่ถูกเฉลี่ยลงบรรทัด 0%/อัตราอื่นด้วย ⇒ VAT เพี้ยนจริง)
    /// <para>รอบ 208 ฝ่ายค้านรอบ 2 ข้อ 7 (กลับคำของ W5 ข้อ 7): <b>ส่วนลดท้ายบิล</b>เฉลี่ยตามสัดส่วนทุกบรรทัดทุกอัตรา = วิธีที่ถูก (ส่วนลดลดฐานของทุกรายการตามสัดส่วน) ⇒
    /// ตรวจเมื่อมีส่วนลดอย่างเดียว = คำเตือนที่ฟ้องใบถูกทุกใบที่มีบรรทัด 0% (สูตรคาดหวังสมมุติอัตราเดียว) — จึงไม่ตรวจ</para>
    /// </summary>
    public static bool NeedsVatMixCheck(decimal depositBaseDeducted) => depositBaseDeducted > 0m;

    /// <summary>เหตุผลของแผนที่ยกจากส่วนลดที่ออกเอกสารไปแล้ว (คำขอกดซ้ำไม่ได้กรอกส่วนลด)</summary>
    public const string ReusedReason = "ส่วนลดเดิมที่ออกเอกสารไปแล้วในการกดเช็คเอาต์ครั้งก่อน";

    /// <summary>
    /// รอบ 208 ฝ่ายค้าน W5 ข้อ 1 — เช็คเอาต์บนใบกำกับล่วงหน้าที่<b>กดซ้ำ</b>หลังใบลดหนี้ส่วนลด commit แล้ว (ใบส่วนเพิ่มอนุมัติล้ม):
    /// ยอดคงเหลือของใบล่วงหน้าหักใบลดหนี้ไปแล้ว ⇒ ถ้าคำขอใหม่ไม่ส่งส่วนลด ส่วนต่างค่าห้องจะกลายเป็น "ส่วนต่างค่าห้องพัก" เรียกเก็บส่วนลดคืน ·
    /// ถ้าส่งส่วนลดต่างจากเดิม จะออกใบลดหนี้ใบที่สอง · ตัวตัดสินนี้บังคับ: ไม่มีส่วนลดที่ใช้ไปแล้ว ⇒ ใช้คำขอ · คำขอเว้นว่าง ⇒ ยกส่วนลดเดิม ·
    /// เท่าเดิม (±0.005) ⇒ ใช้คำขอ · ต่าง ⇒ ปฏิเสธพร้อมทางไปต่อ (ห้ามออกใบลดหนี้ส่วนลดใบที่สอง)
    /// </summary>
    /// <param name="appliedBase">ฐานก่อน VAT ของส่วนลดที่ประทับไว้บนการจอง (<c>LodgingReservation.CheckoutDiscountBase</c>) · null = ยังไม่เคยใช้</param>
    public static (LodgingCheckoutDiscountPlan Plan, string? Problem) ReconcileApplied(LodgingCheckoutDiscountPlan requested, decimal? appliedBase)
    {
        if (appliedBase is not decimal applied || applied <= 0.005m) return (requested, null);
        var a = R2(applied);
        if (!requested.HasDiscount) return (new LodgingCheckoutDiscountPlan(a, false, a, ReusedReason), null);
        if (Math.Abs(requested.BaseAmount - a) <= 0.005m) return (requested, null);
        return (LodgingCheckoutDiscountPlan.None,
            $"การกดเช็คเอาต์ครั้งก่อนออกใบลดหนี้ส่วนลดไปแล้ว (ฐานก่อน VAT {a:N2}) แต่ครั้งนี้ระบุส่วนลดฐาน {requested.BaseAmount:N2} — "
            + "กดซ้ำให้เว้นช่องส่วนลดหรือกรอกเท่าเดิม · ส่วนลดเพิ่ม/ลดหลังจากนี้ปรับด้วยใบลดหนี้/ใบเพิ่มหนี้ที่หน้าเอกสาร (ระบบไม่ออกใบลดหนี้ส่วนลดใบที่สอง)");
    }

    /// <summary>
    /// รอบ 208 ฝ่ายค้าน W5 ข้อ 6 — ฐานส่วนลดของเช็คเอาต์บนใบกำกับล่วงหน้า = ค่าห้อง + folio/ค่าเสียหาย/ค่าเช็คเอาต์ช้า (<b>ฐานเดียวกับเช็คเอาต์ปกติ</b>) ·
    /// แบ่งส่วนลดลงค่าห้องก่อน (ไปเป็นใบลดหนี้อ้างใบล่วงหน้า) ส่วนที่เกินค่าห้องลงใบส่วนเพิ่มเป็นส่วนลดท้ายบิล ·
    /// เลือก "ค่าห้องก่อน" (ไม่เฉลี่ยตามสัดส่วน) เพราะส่วนที่ลงใบลดหนี้ต้อง<b>คงที่</b>เมื่อกดซ้ำแล้ว folio เปลี่ยน — เฉลี่ยตามสัดส่วนจะทำให้
    /// ส่วนแบ่งค่าห้องขยับ ⇒ ส่วนต่างค่าห้องเกิดใหม่ (ใบลดหนี้ซ้ำ/เรียกเก็บคืน) · ยอดลดรวมเท่าเส้นปกติทุกสตางค์ (ต่างเฉพาะการเฉลี่ยลงบรรทัด)
    /// </summary>
    public static (decimal Stay, decimal Folio) SplitStayFirst(decimal discountBase, decimal stayNet, decimal folioNet)
    {
        var d = R2(Math.Max(0m, discountBase));
        var stay = Math.Min(d, Math.Max(0m, stayNet));
        var folio = Math.Min(d - stay, Math.Max(0m, folioNet));
        return (stay, folio);
    }

    /// <summary>
    /// รอบ 208 ฝ่ายค้าน W5 ข้อ 2 — กดเช็คเอาต์ซ้ำเมื่อใบเช็คเอาต์<b>ออกแล้ว</b> (ทำขั้นที่ค้างต่อ) แต่คำขอส่งส่วนลดต่างจากที่ออกไป ⇒ ปฏิเสธ (ห้าม no-op เงียบ) ·
    /// เว้นว่าง = คงตามใบ (null) · หน่วยเดียวกับ <see cref="Resolve(decimal?, decimal?, string?, decimal, bool, decimal)"/> ·
    /// % ต้องรู้ฐานก่อนส่วนลด — ไม่รู้ (ใบบนใบกำกับล่วงหน้า) ⇒ ปฏิเสธพร้อมทางไปต่อ (กรอกเป็นบาท/เว้นว่าง)
    /// </summary>
    /// <param name="appliedBase">ส่วนลดฐานก่อน VAT ที่ออกไปแล้ว (บนการจอง หรือ <c>BillDiscountAmount</c> ของใบ)</param>
    /// <param name="fullNetBeforeDiscount">ฐานก่อน VAT ก่อนส่วนลดของใบ (ใช้กับ %) · null = ไม่รู้</param>
    public static string? ResumeProblem(decimal? amount, decimal? percent, decimal appliedBase, string finalNumber,
        bool pricesIncludeVat, decimal vatRate, decimal? fullNetBeforeDiscount)
    {
        var amt = R2(amount ?? 0m);
        var pct = percent ?? 0m;
        if (Math.Abs(amt) <= 0.005m && pct == 0m) return null;
        var applied = R2(Math.Max(0m, appliedBase));
        var msg = $"ใบ {finalNumber} ออกแล้วพร้อมส่วนลด {applied:N2} (ฐานก่อน VAT) — ปรับด้วยใบลดหนี้/ใบเพิ่มหนี้อ้างใบนั้นที่หน้าเอกสาร "
            + "(กดเช็คเอาต์ซ้ำเพื่อทำขั้นที่ค้างต่อ ให้เว้นช่องส่วนลดหรือกรอกเท่าเดิม)";
        decimal requested;
        if (pct != 0m)
        {
            if (fullNetBeforeDiscount is not decimal fn || pct < 0m || amt != 0m) return msg;
            requested = R2(Math.Max(0m, fn) * pct / 100m);
        }
        else
            requested = amt > 0m && pricesIncludeVat && vatRate > 0m ? LodgingDepositSettlement.SplitInclusive(amt, vatRate).Base : amt;
        return Math.Abs(requested - applied) <= 0.01m ? null : msg;
    }

    /// <summary>ส่วนลดทำให้ยอดเข้าพักต่ำกว่ามัดจำที่รับไว้ — ทางไปต่อตัวเดียวกับมัดจำเกินยอด (ค้างคืน → ใบลดหนี้อ้างใบมัดจำตอนยืนยันคืนเงิน)</summary>
    public static string NegativeNote(decimal excessGross)
        => $"ส่วนลดทำให้ยอดเข้าพักต่ำกว่ามัดจำที่รับไว้ — ส่วนต่าง {excessGross:N2} ค้างคืนแขก · กด “ยืนยันคืนเงินแล้ว” เมื่อโอนคืนจริง "
         + "(ระบบออกใบลดหนี้อ้างใบมัดจำ §86/10 เหตุ “ปรับยอด” ตอนนั้น — ไม่ออกก่อนมีหลักฐานคืนเงิน)";

    /// <summary>รอบ 208 (ฝ่ายค้านรอบ 2 ข้อ 5): โหมดไม่ออกเอกสาร — ส่วนลดทำให้เงินที่รับไว้เกินยอดรวม · ไม่มีใบมัดจำให้อ้าง ⇒ บอกยอดที่ค้างคืน + ให้ลงที่ระบบบัญชีที่ใช้</summary>
    public static string OffModeExcessNote(decimal excess)
        => $"ส่วนลดทำให้เงินที่รับไว้เกินยอดรวม — ส่วนต่าง {excess:N2} ค้างคืนแขก (โหมดไม่ออกเอกสาร: คืนเงินแล้วบันทึกในระบบบัญชีที่ใช้)";
}
