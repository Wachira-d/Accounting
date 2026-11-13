namespace Accounting.Helpers;

/// <summary>
/// **ทบทวนช่องยอดขาย 0% / ยกเว้น ของรายงาน ภ.พ.30 ที่บันทึกไว้ (pure · รอบ 269 · หลักการ #9 "persist แล้วต้องมี migration")**
///
/// <para>ก่อนรอบ 269 <c>TaxService.GenerateVatReport</c> นับบรรทัด VatRate 0/-1 ของ<b>ทุกใบฝั่งขาย</b>เข้าช่อง 0% (§80/1) / ช่องยกเว้น (§81) ในเดือนที่ขาย —
/// ใบเสร็จรับชำระ · ใบวางบิล · ใบส่งของ · ใบเสนอราคา · ใบแจ้งหนี้ที่ใบกำกับแทนแล้ว · ลูกหนี้ยกมา ⇒ แถว <c>ZERO_RATED_SALES</c>/<c>EXEMPT_SALES</c> ที่บันทึกไว้
/// อาจเกินจริง · ระหว่างรอบ 269 กติกาเปลี่ยนอีกสองรุ่น (269-1 · 269-2) ⇒ รายงานที่สร้างในช่วงนั้นเก็บยอดของรุ่นนั้น</para>
///
/// <para><b>สาเหตุที่ระบุต้องเป็นสาเหตุที่ตรวจจริง</b> (หลักการ #7 · ฝ่ายค้าน M2/ชุดสอง M6): ผู้เรียกคำนวณยอดตาม<b>ทุกรุ่นกติกาก่อนหน้า</b>บนข้อมูลวันนี้ไปพร้อมกัน ·
/// ยอดที่บันทึก = รุ่นใด ⇒ <see cref="CauseRuleChange"/> พร้อมชื่อรุ่น · ไม่ตรงรุ่นใดเลย ⇒ <see cref="CauseDataChanged"/> (เอกสารของงวดถูกเพิ่ม/แก้/ยกเลิกหลังบันทึก)</para>
///
/// <para><b>ไม่แก้ตัวเลขเงียบ ๆ</b>: ร่าง ⇒ "สร้างใหม่" (มี audit) · ยื่น/ล็อกแล้ว ⇒ ระบบไม่แตะ บอกให้พิจารณายื่น ภ.พ.30 เพิ่มเติม ·
/// ภาษีขาย/ภาษีที่ต้องชำระไม่เปลี่ยน (ยอด 0%/ยกเว้นไม่มีภาษี) แต่ยอดขายรวม/ยอดขายที่ต้องเสียภาษีในแบบเปลี่ยนตาม</para>
/// </summary>
public static class Pp30SalesBoxReview
{
    public const string RuleCode = "PP30-SALES-BOX-OWNER-269";
    public const string CauseRuleChange = "RuleChange";
    public const string CauseDataChanged = "DataChanged";

    /// <summary>ชื่อรุ่นกติกาที่ตัวทบทวนรู้จัก (ข้อความบนจอ)</summary>
    public const string RulePre269 = "ก่อนรอบ 269 (นับทุกใบฝั่งขายที่มีบรรทัด 0%/ยกเว้น ตามเดือนที่ขาย)";
    public const string Rule2691 = "รอบ 269 รุ่นแรก (ยอดยกมา/ใบสำคัญรับเดี่ยว/ใบแจ้งหนี้ VAT 0 ยังนับ)";
    public const string Rule2692 = "รอบ 269 รุ่นสอง (ช่องยกเว้นนับเฉพาะใบกำกับ/ใบที่มี VAT)";

    public readonly record struct Boxes(decimal ZeroRated, decimal Exempt);

    private static bool Differs(decimal a, decimal b) => Math.Abs(a - b) >= 0.005m;
    private static bool Differs(Boxes a, Boxes b) => Differs(a.ZeroRated, b.ZeroRated) || Differs(a.Exempt, b.Exempt);

    public sealed record Verdict(bool NeedsAttention, string SuggestedAction, string Cause, string? Message, string? MatchedRule = null);

    /// <param name="stored">ยอดที่บันทึกไว้ในรายงาน</param>
    /// <param name="fresh">ยอดตามกติกาปัจจุบันบนข้อมูลวันนี้ (รวมกับแถวที่ยังใช้ของรายงานนั้น)</param>
    /// <param name="priorRules">ยอดตามกติการุ่นก่อนบนข้อมูลวันนี้ — ตามลำดับจากเก่าไปใหม่</param>
    public static Verdict Evaluate(Boxes stored, Boxes fresh, IReadOnlyList<(string Rule, Boxes Boxes)> priorRules, bool alreadyFiled, string periodLabel)
    {
        if (!Differs(stored, fresh))
            return new Verdict(false, TaxReportRecompute.ActionNone, "", null);
        var z = Pp30FormBoxes.Ref(Pp30FormBox.ZeroRatedSales);
        var e = Pp30FormBoxes.Ref(Pp30FormBox.ExemptSales);
        var what = $"{z} (0% §80/1) {stored.ZeroRated:N2} → {fresh.ZeroRated:N2} · {e} (ยกเว้น §81) {stored.Exempt:N2} → {fresh.Exempt:N2}";
        var matched = priorRules.FirstOrDefault(p => !Differs(stored, p.Boxes)).Rule;
        var cause = matched != null ? CauseRuleChange : CauseDataChanged;
        var why = matched != null
            ? $"ยอดที่บันทึกไว้ตรงกับกติการุ่น {matched} เมื่อคำนวณจากข้อมูลวันนี้ — กติกาปัจจุบันนับเฉพาะใบที่ถือการขายตามวันที่ VAT ถึงกำหนด"
            : "ยอดที่บันทึกไว้ไม่ตรงทั้งกติการุ่นก่อนและกติกาปัจจุบันเมื่อคำนวณจากข้อมูลวันนี้ — เอกสารของงวดนี้ถูกเพิ่ม/แก้/ยกเลิกหลังบันทึกรายงาน";
        var boxes = $"{Pp30FormBoxes.Ref(Pp30FormBox.TotalSales)}–{Pp30FormBoxes.Ref(Pp30FormBox.TaxableSales)}";
        return alreadyFiled
            ? new Verdict(true, TaxReportRecompute.ActionConsiderAmendedFiling, cause,
                $"ภ.พ.30 งวด {periodLabel} ยื่น/บันทึกว่ายื่นแล้ว: {what} — {why} · ถ้ากรอกแบบที่ยื่นตามรายงานนี้ ให้พิจารณายื่น ภ.พ.30 เพิ่มเติมเพื่อแก้ {boxes} "
                + $"({Pp30FormBoxes.Name(Pp30FormBox.OutputVat)}/{Pp30FormBoxes.Name(Pp30FormBox.TaxPayable)}ไม่เปลี่ยน — ยอด 0%/ยกเว้นไม่มีภาษี) ระบบไม่แก้ตัวเลขของรายงานที่ยื่นแล้ว",
                matched)
            : new Verdict(true, TaxReportRecompute.ActionRegenerate, cause,
                $"รายงานร่าง ภ.พ.30 งวด {periodLabel}: {what} — {why} · กด \"สร้างใหม่\" เพื่อคำนวณใหม่ (บันทึก audit ยอดเดิม → ยอดใหม่)", matched);
    }
}
