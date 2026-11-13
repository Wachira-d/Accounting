namespace Accounting.Helpers;

/// <summary>
/// **ทบทวนช่อง 7/8 ของรายงาน ภ.พ.30 ที่บันทึกไว้ (pure · รอบ 269 · หลักการ #9 "persist แล้วต้องมี migration")**
///
/// <para>ก่อนรอบ 269 <c>TaxService.GenerateVatReport</c> นับบรรทัด VatRate 0/-1 ของ<b>ทุกใบฝั่งขาย</b>เข้าช่อง 7 (0% §80/1) / ช่อง 8 (ยกเว้น §81) —
/// ใบเสร็จรับชำระ ("รับชำระเงินตาม…" VAT 0) · ใบวางบิล · ใบส่งของ · ใบเสนอราคา · ใบแจ้งหนี้ที่ใบกำกับแทนแล้ว ⇒ แถว <c>ZERO_RATED_SALES</c> /
/// <c>EXEMPT_SALES</c> ที่บันทึกไว้เกินจริง (เคสผู้ใช้ ก.ย. 2569: ช่อง 7 = 1,235,494.24 ทั้งที่ไม่มีขาย 0%) และช่อง 5 บน PDF ถูกหักด้วยยอด 0% ปลอม
/// (<see cref="VatReportLineKind.StandardBase"/>)</para>
///
/// <para><b>ไม่แก้ตัวเลขเงียบ ๆ</b> (แบบเดียวกับ <see cref="TaxReportRecompute"/>): ร่าง ⇒ ผู้ใช้กด "สร้างใหม่" (มี audit) ·
/// ยื่น/ล็อกแล้ว ⇒ ระบบไม่แตะ บอกให้พิจารณายื่น ภ.พ.30 เพิ่มเติม — <b>ภาษีที่ต้องชำระไม่เปลี่ยน</b> (ยอด 0%/ยกเว้นไม่มีภาษี ·
/// ภาษีขายมาจากแถว §87 ซึ่งตัดสินด้วย <see cref="OutputVatOwnership.Decide"/> อยู่แล้ว) จึงเป็นการแก้ยอดในแบบ ไม่ใช่ภาษีขาดนำส่ง</para>
/// </summary>
public static class Pp30SalesBoxReview
{
    public const string RuleCode = "PP30-SALES-BOX-OWNER-269";

    /// <summary>ต่างกันเกินครึ่งสตางค์ = ต่าง (ยอดรวมทั้งงวด ไม่ปัดที่ผู้เรียก)</summary>
    private static bool Differs(decimal stored, decimal fresh) => Math.Abs(stored - fresh) >= 0.005m;

    public sealed record Verdict(bool NeedsAttention, string SuggestedAction, string? Message);

    public static Verdict Evaluate(decimal storedZeroRated, decimal freshZeroRated, decimal storedExempt, decimal freshExempt,
        bool alreadyFiled, string periodLabel)
    {
        if (!Differs(storedZeroRated, freshZeroRated) && !Differs(storedExempt, freshExempt))
            return new Verdict(false, TaxReportRecompute.ActionNone, null);
        var what = $"ช่อง 7 (0% §80/1) {storedZeroRated:N2} → {freshZeroRated:N2} · ช่อง 8 (ยกเว้น §81) {storedExempt:N2} → {freshExempt:N2}";
        return alreadyFiled
            ? new Verdict(true, TaxReportRecompute.ActionConsiderAmendedFiling,
                $"ภ.พ.30 งวด {periodLabel} ยื่น/บันทึกว่ายื่นแล้ว และยอดช่องยอดขายที่บันทึกไว้นับใบที่ไม่ได้ถือการขาย (ใบเสร็จรับชำระ/ใบวางบิล/ใบส่งของ/ใบเสนอราคา/"
                + $"ใบแจ้งหนี้ที่ใบกำกับแทนแล้ว): {what} — ถ้ากรอกช่อง 7/8 ในแบบที่ยื่นตามรายงานนี้ ให้พิจารณายื่น ภ.พ.30 เพิ่มเติมเพื่อแก้ยอดช่อง 5–8 "
                + "(ภาษีขาย/ภาษีที่ต้องชำระไม่เปลี่ยน — ยอด 0%/ยกเว้นไม่มีภาษี) ระบบไม่แก้ตัวเลขของรายงานที่ยื่นแล้ว")
            : new Verdict(true, TaxReportRecompute.ActionRegenerate,
                $"รายงานร่าง ภ.พ.30 งวด {periodLabel} คำนวณช่องยอดขายด้วยกติกาเดิม: {what} — กด \"สร้างใหม่\" เพื่อคำนวณใหม่ (บันทึก audit ยอดเดิม → ยอดใหม่)");
    }
}
