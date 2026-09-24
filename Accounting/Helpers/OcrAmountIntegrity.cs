using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>บรรทัดที่ตัวสร้างเอกสารจาก OCR <b>จะเขียนจริง</b> — ยอดก่อน VAT (หลังส่วนลด) · อัตรา · VAT</summary>
public readonly record struct OcrPlannedLine(decimal NetAmount, decimal VatRate, decimal VatAmount);

/// <summary>ชนิดของความไม่ลงตัว — ผู้เรียกใช้กรองข้อความที่ถูกรายงานไปแล้วโดยตัวอื่น</summary>
public enum OcrAmountIntegrityKind
{
    /// <summary>บรรทัดยอดติดลบ (ส่วนลด/คืนของที่ engine อ่านมาเป็นรายการ) — §86/4(5) ห้าม</summary>
    NegativeLine = 1,
    /// <summary>Σ (ก่อน VAT + VAT) ของบรรทัด ≠ ยอดรวมบนกระดาษ</summary>
    TotalMismatch = 2,
    /// <summary>Σ VAT ของบรรทัด ≠ VAT บนกระดาษ (เช่นทุกบรรทัดถูกตั้งเป็นไม่มี VAT)</summary>
    VatMismatch = 3,
    /// <summary>อัตรา VAT รายบรรทัดไม่เข้ากับ VAT บนกระดาษ (ใบผสม VAT/ไม่มี VAT ที่ติดอัตราผิด)</summary>
    VatRateMismatch = 4,
}

/// <summary>ความไม่ลงตัวหนึ่งข้อ — ข้อความภาษาไทยที่มี<b>ตัวเลข</b>ว่าต่างเท่าไร ตรงไหน</summary>
public sealed record OcrAmountIntegrityProblem(OcrAmountIntegrityKind Kind, string Message);

/// <summary>ผลตรวจ</summary>
/// <param name="TotalComparable">รู้ยอดรวมบนกระดาษหรือไม่ — false = เทียบยอดรวมไม่ได้
/// (<b>ไม่ใช่ "ตรง"</b> · ช่องยอดรวมที่ว่างถูกฟ้องโดยด่านช่องบังคับอยู่แล้ว)</param>
public sealed record OcrAmountIntegrityResult(
    bool TotalComparable,
    decimal LinesNet,
    decimal LinesVat,
    decimal LinesTotal,
    IReadOnlyList<OcrAmountIntegrityProblem> Problems)
{
    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// **ด่าน "เอกสารที่จะสร้าง ยอดตรงกับกระดาษไหม" — ตรวจบรรทัดที่จะเขียนจริง ไม่ใช่หัวใบ**
///
/// ═══ ที่มา (รอบ 190 ข้อ 9 ของเจ้าของ: "ยอดตอน OCR มายอดรวมไม่ตรงกับในเอกสาร ไม่ควรปล่อยผ่านมาได้") ═══
/// <para>ไล่ลำดับเดิม: <c>OcrConfidenceGateway</c> ตรวจ<b>หัวใบ</b>ตอนสแกน · <c>OcrLineReconciler</c>
/// ตรวจ Σ บรรทัด<b>ก่อน VAT</b> กับหัวใบ · แล้ว <c>ThaiVatTypeRule.SpreadHeaderVat</c> เฉลี่ย VAT หัวใบ
/// ลงบรรทัดที่ "เดาว่า" มี VAT — <b>ผลรวม VAT จึงตรงกระดาษเสมอโดยการสร้าง</b> แม้อัตรารายบรรทัดผิด
/// ⇒ ไม่มีด่านไหนถามว่า "บรรทัดที่ติด 7% รวม X — 7% ของ X เท่ากับ VAT บนกระดาษไหม" · ส่วน
/// "Σ VAT ≠ VAT หัวใบ" มีแค่ <c>LogWarning</c> (ไม่ใช่การดัง — F2 ข้อ 7) และเคส "Σ บรรทัดตรงยอด
/// ก่อน VAT ที่อ่านมา แต่ยอดนั้นเป็นยอด<b>ก่อน</b>ส่วนลดที่ระบบอ่านไม่ได้" ผ่านเงียบ ๆ ทั้งที่
/// Σ บรรทัด + VAT ≠ ยอดรวมบนกระดาษ</para>
///
/// <para>ผลที่ตามมา: เอกสารฉบับร่างดูเหมือนตรง แต่พอเปิดแก้แล้วบันทึก <c>DocumentService</c> คิด VAT
/// ใหม่จาก "อัตรา × ยอด" ⇒ ยอดเอกสารเปลี่ยนเองและ ≠ กระดาษ · รายงานภาษีซื้อ §87 ฐานผิด</para>
///
/// <para>กติกา: ตรวจ<b>สามข้อที่เป็นอิสระจากกัน</b> บนบรรทัดที่จะเขียนจริง — (1) Σ ก่อน VAT + Σ VAT
/// = ยอดรวมบนกระดาษ (2) Σ VAT = VAT บนกระดาษ (3) VAT ที่ "อัตรา × ยอด" ของแต่ละกลุ่มอัตราให้
/// = VAT บนกระดาษ — พร้อมบรรทัดติดลบ · ด่านนี้<b>ไม่แก้ตัวเลขใด ๆ</b> (ห้ามแต่งให้ลงตัวเงียบ ๆ)
/// ผู้เรียกเขียนผลเป็น <c>[Σ-GAP]</c> ⇒ <c>OcrPostingReadiness</c> ห้ามอนุมัติอัตโนมัติ + หน้ารีวิว/
/// LINE แสดงตัวเลข</para>
///
/// <para>ค่าเผื่อ: ยอดรวม ±<see cref="OcrLineReconciler.Tolerance"/> (เท่ากับตัวกระทบยอด — ค่าปัดเศษ
/// ของบิลเงินสด) · Σ VAT ±max(0.02, 0.01 × จำนวนบรรทัดที่มี VAT) (ปัดเศษรายบรรทัดได้ไม่เกิน 1 สตางค์ต่อบรรทัด)
/// · อัตรา × ยอด ±max(0.10, 0.01 × จำนวนบรรทัดที่มี VAT)</para>
/// </summary>
public static class OcrAmountIntegrity
{
    private const MidpointRounding R = MidpointRounding.AwayFromZero;

    /// <param name="lines">บรรทัดที่จะเขียนจริง</param>
    /// <param name="paperVat">VAT บนกระดาษ (0 = ไม่มี)</param>
    /// <param name="paperTotal">ยอดรวมบนกระดาษ (0 = ไม่รู้)</param>
    /// <param name="paperTaxable">ยอด "ต้องเสียภาษี" ที่กระดาษพิมพ์ (ใช้เป็นข้อมูลในข้อความเท่านั้น)</param>
    /// <param name="paperNonTaxable">ยอด "ไม่ต้องเสียภาษี" ที่กระดาษพิมพ์ (ใช้เป็นข้อมูลในข้อความเท่านั้น)</param>
    public static OcrAmountIntegrityResult Check(
        IReadOnlyList<OcrPlannedLine> lines, decimal paperVat, decimal paperTotal,
        decimal? paperTaxable = null, decimal? paperNonTaxable = null)
    {
        var problems = new List<OcrAmountIntegrityProblem>();
        var net = lines.Sum(l => l.NetAmount);
        var vat = lines.Sum(l => l.VatAmount);
        var total = net + vat;
        if (lines.Count == 0)
            return new OcrAmountIntegrityResult(paperTotal > 0m, 0m, 0m, 0m, problems);

        // (0) บรรทัดติดลบ — ไม่ใช่รายการที่ขาย (§86/4(5) · Helpers/DocumentLineKind)
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].NetAmount < 0m)
                problems.Add(new(OcrAmountIntegrityKind.NegativeLine,
                    $"บรรทัดที่ {i + 1} ยอดติดลบ ({lines[i].NetAmount:N2}) — ส่วนลด/คืนของต้องลงช่องส่วนลด "
                    + "ไม่ใช่บรรทัดติดลบ (ม.86/4(5)) · ลบบรรทัดนี้แล้วใส่เป็นส่วนลดของบรรทัดสินค้า"));
        }

        // (1) ยอดรวม
        var comparable = paperTotal > 0m;
        if (comparable && Math.Abs(total - paperTotal) > OcrLineReconciler.Tolerance)
        {
            var diff = total - paperTotal;
            var hint = diff > 0m
                ? "เอกสารมากกว่ากระดาษ — กระดาษอาจมีส่วนลดที่ระบบอ่านไม่ได้ หรืออ่านตัวเลขรายการเกิน"
                : "เอกสารน้อยกว่ากระดาษ — อาจมีรายการ/ค่าบริการ/ค่าขนส่งที่ระบบอ่านไม่ได้";
            problems.Add(new(OcrAmountIntegrityKind.TotalMismatch,
                $"ยอดรวมจากรายการ {net:N2} + VAT {vat:N2} = {total:N2} ≠ ยอดรวมบนกระดาษ {paperTotal:N2} "
                + $"(ต่าง {diff:+#,##0.00;-#,##0.00}) — {hint}"));
        }

        // (2) Σ VAT ของบรรทัด ≠ VAT บนกระดาษ (เช่นทุกบรรทัดถูกเดาว่าไม่มี VAT ทั้งที่กระดาษมี)
        var taxable = lines.Where(l => l.VatRate > 0m).ToList();
        var vatSlack = Math.Max(0.02m, 0.01m * taxable.Count);
        if (Math.Abs(vat - paperVat) > vatSlack)
        {
            problems.Add(new(OcrAmountIntegrityKind.VatMismatch,
                taxable.Count == 0 && paperVat > 0m
                    ? $"กระดาษมี VAT {paperVat:N2} แต่ทุกบรรทัดถูกตั้งเป็นไม่มี VAT (ยกเว้น/0%) — ตรวจอัตรา VAT รายบรรทัด"
                    : $"VAT รวมของรายการ {vat:N2} ≠ VAT บนกระดาษ {paperVat:N2} (ต่าง {vat - paperVat:+#,##0.00;-#,##0.00})"));
        }

        // (3) อัตรา × ยอด ของแต่ละกลุ่มอัตรา ต้องให้ VAT เท่ากระดาษ — ด่านเดียวที่จับ
        //     "ใบผสม VAT/ไม่มี VAT ที่ติดอัตราผิด" (Σ VAT ตรงเสมอเพราะเฉลี่ยจากหัวใบ)
        if (taxable.Count > 0)
        {
            var expected = taxable.GroupBy(l => l.VatRate)
                .Sum(g => Math.Round(g.Sum(l => l.NetAmount) * g.Key / 100m, 2, R));
            var gap = expected - paperVat;
            // ค่าเผื่อขั้นต่ำ 0.10 — ต่ำกว่านี้คือยอดยกเว้น < 1.43 บาท ซึ่งเกิดจากการปัดเศษ/อ่านสตางค์
            // เพี้ยนได้มากกว่าจากใบผสมจริง (คำเตือนที่ฟ้องใบถูก = ปิดด่านโดยไม่ตั้งใจ)
            var rateSlack = Math.Max(0.10m, 0.01m * taxable.Count);
            if (Math.Abs(gap) > rateSlack)
            {
                var taxableBase = taxable.Sum(l => l.NetAmount);
                var rateText = string.Join("/", taxable.Select(l => l.VatRate).Distinct().Select(r => $"{r:0.##}%"));
                var msg = $"อัตรา VAT รายบรรทัดไม่เข้ากับ VAT บนกระดาษ: บรรทัดที่ติด {rateText} รวม {taxableBase:N2} "
                        + $"→ VAT ควรเป็น {expected:N2} แต่กระดาษพิมพ์ {paperVat:N2} (ต่าง {gap:+#,##0.00;-#,##0.00})";
                if (paperVat > 0m && gap > 0m)
                {
                    var impliedBase = Math.Round(paperVat / 0.07m, 2, R);
                    msg += $" — VAT บนกระดาษบอกว่ายอดที่มี VAT ≈ {impliedBase:N2} ⇒ น่าจะมีรายการยกเว้น/ไม่มี VAT "
                         + $"≈ {taxableBase - impliedBase:N2} ที่ถูกติด 7%";
                }
                else if (gap < 0m)
                    msg += " — น่าจะมีรายการที่มี VAT แต่ถูกตั้งเป็นยกเว้น/0%";
                if (paperTaxable.HasValue || paperNonTaxable.HasValue)
                    msg += $" · กระดาษแยกไว้: ต้องเสียภาษี {(paperTaxable is decimal a ? a.ToString("N2") : "-")}"
                         + $" · ไม่ต้องเสียภาษี {(paperNonTaxable is decimal b ? b.ToString("N2") : "-")}";
                problems.Add(new(OcrAmountIntegrityKind.VatRateMismatch, msg + " — ตรวจอัตรา VAT รายบรรทัดก่อนอนุมัติ"));
            }
        }

        return new OcrAmountIntegrityResult(comparable, net, vat, total, problems);
    }
}
