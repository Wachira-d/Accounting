using System;
using System.Collections.Generic;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>คำตัดสินของชั้น "ตัวเลขหัวใบพิสูจน์อัตราทั้งใบ" — <see cref="Unknown"/> = พิสูจน์ไม่ได้ (ไม่ใช่ "ไม่มี VAT")</summary>
public enum OcrLineVatPlanVerdict
{
    /// <summary>พิสูจน์ไม่ได้/ไม่มีอะไรให้เติม — ผู้เรียกตกไปชั้นถัดไป (ตัวเดาจากชื่อสินค้า) ตามเดิม</summary>
    Unknown = 0,
    /// <summary>ตัวเลขบนกระดาษพิสูจน์ว่า<b>ทุกบรรทัด</b>เสีย VAT 7% (VAT หัวใบ = 7% ของฐานทั้งใบ · ยอดยกเว้นบนกระดาษเป็น 0/ไม่มี)</summary>
    AllStandard7 = 1,
}

/// <summary>ผลของชั้นพิสูจน์ทั้งใบ</summary>
/// <param name="Verdict">คำตัดสิน</param>
/// <param name="Rates">อัตราที่เติมให้ต่อบรรทัดตามลำดับเดิม — เติม<b>เฉพาะ</b>บรรทัดที่ยังไม่มีอัตรา (null = ไม่แตะบรรทัดนั้น)</param>
/// <param name="Reason">เหตุผลภาษาไทยพร้อมตัวเลข — ทั้งตอนตัดสินและตอนไม่ตัดสิน (ให้คนตามรอยได้ · ลง ProcessingNotes)</param>
public sealed record OcrLineVatPlan(OcrLineVatPlanVerdict Verdict, decimal?[] Rates, string Reason)
{
    /// <summary>ตัดสินแล้ว (มีอย่างน้อยหนึ่งบรรทัดถูกเติม)</summary>
    public bool Decided => Verdict != OcrLineVatPlanVerdict.Unknown;
}

/// <summary>
/// **ชั้นหลักฐาน "ตัวเลขหัวใบพิสูจน์อัตรา VAT ทั้งใบ" — อยู่เหนือตัวเดาจากชื่อสินค้า (<see cref="ThaiVatTypeRule"/>)**
///
/// ═══ ที่มา (รอบ 195 · ใบ Scommerce TXE05202609T004679 ของเจ้าของ) ═══
/// <para>กระดาษพิมพ์ "มูลค่าก่อน VAT หลังหักส่วนลด 4,695.33 · VAT 7% 328.67 · ยกเว้น 0.00" — 7% × 4,695.33 = 328.67
/// พอดี ⇒ <b>ทุกบรรทัดเสีย VAT</b> พิสูจน์ด้วยเลขคณิตของกระดาษเอง · แต่ <c>OcrService.BuildScanLinesAsync</c> ให้
/// <see cref="ThaiVatTypeRule.Suggest"/> เดาจากชื่อ "นมผงเอนฟาโกร…" ⇒ คำ "นม" = ยกเว้น §81 ⇒ บรรทัดเดียวที่มียอด
/// ได้ −1 ⇒ <see cref="ThaiVatTypeRule.SpreadHeaderVat"/> ไม่มีฐานให้เฉลี่ย ⇒ VAT บรรทัด 0 · ยอดเอกสาร 4,695.33 ≠
/// กระดาษ 5,024.00 · คำเตือน 3 ข้อ · ถ้ายอมรับ = ภาษีซื้อ 328.67 หาย. ถดถอยจาก <c>5e3a323b</c> (ต่อตัวเดาจากชื่อเข้า
/// เส้น OCR — เดิม <c>headerVat &gt; 0 ? 7 : 0</c> ทุกบรรทัด) · รอบ 192 ทำนายไว้ (<c>total-B.md</c> RC-9) แต่ไม่ได้แก้</para>
///
/// ═══ ลำดับชั้น (DECISION_DOCTRINE §1 — ระยะห่างจากของจริง) ═══
/// <list type="number">
/// <item>ค่าที่ engine อ่าน/ผู้ใช้แก้ในหน้ารีวิว — ชนะเสมอ (ชั้นนี้<b>ไม่แตะ</b>บรรทัดที่มีอัตราแล้ว)</item>
/// <item>สัญลักษณ์ท้ายบรรทัดบนกระดาษ (<see cref="OcrLineVatMarks.Assign"/>) — ใบผสมที่กระดาษบอกเอง</item>
/// <item><b>ชั้นนี้</b> — ตัวเลขหัวใบพิสูจน์ว่าทุกบรรทัดเป็น 7%</item>
/// <item>ตัวเดาจากชื่อสินค้า (<see cref="ThaiVatTypeRule"/>) — เฉพาะบรรทัดที่ยังว่างหลังชั้นนี้</item>
/// </list>
///
/// ═══ เงื่อนไข (ครบทุกข้อ ไม่ครบ = <see cref="OcrLineVatPlanVerdict.Unknown"/> — ห้ามเดา) ═══
/// <list type="bullet">
/// <item>VAT หัวใบ &gt; 0 และมีบรรทัดที่ยังไม่มีอัตรา</item>
/// <item><b>VAT หัวใบพิมพ์บนกระดาษในฐานะ VAT</b> (<see cref="OcrHeaderVatEvidence"/>) — VAT ที่ระบบแยก 7/107 เองผ่านเลขคณิตข้างล่าง
///   ทุกครั้งโดยการสร้าง (ฝ่ายค้าน C1: ใบผัก 1,070 ⇒ VAT แต่ง 70 ⇒ "พิสูจน์" ว่าทั้งใบ 7% ⇒ ภาษีซื้อปลอม)</item>
/// <item>บรรทัดที่มีอัตราแล้วและมียอด ต้องเป็น 7% ทั้งหมด — ถ้าชั้นบนบอกว่ามีบรรทัดไม่มี VAT = หลักฐานขัดกัน ไม่ตัดสิน</item>
/// <item>ยอดยกเว้น/ไม่มี VAT ที่กระดาษพิมพ์ไว้ = ไม่มีหรือ 0 (<see cref="PaperExemptAmount"/>)</item>
/// <item>ราคาก่อน VAT: Σ ยอดบรรทัด (หลังกระจายส่วนลด) ≈ ฐานหัวใบ และ |ปัด(ฐาน × 7%) − VAT หัวใบ| ≤ ค่าเผื่อแคบ ·
///   ราคารวม VAT: Σ ยอดบรรทัด ≈ ยอดรวม และ |ปัด(ยอดรวม × 7/107) − VAT หัวใบ| ≤ ค่าเผื่อแคบ</item>
/// </list>
///
/// <para>ค่าเผื่อ: ผลรวมบรรทัด ±max(0.02, 0.01 × n) · VAT ±max(0.02, 0.005 × n) (n = จำนวนบรรทัดที่มียอด — เศษปัด VAT
/// รายบรรทัดของผู้ขายไม่เกินครึ่งสตางค์ต่อบรรทัด) ⇒ ใบผสมที่มีส่วนยกเว้นเกิน ≈ 0.07 × n บาทไม่มีทางผ่าน
/// (WholesaleMixedVat 7% × 764 = 53.48 ≠ 28 · Makro 7% × 951 = 66.57 ≠ 49)</para>
/// </summary>
public static class OcrLineVatPlanner
{
    /// <summary>อัตรามาตรฐานที่ชั้นนี้พิสูจน์ได้ (§80) — ชั้นนี้ไม่เคยตัดสิน 0%/ยกเว้น</summary>
    public const decimal StandardRate = 7m;

    private const MidpointRounding R = MidpointRounding.AwayFromZero;

    /// <summary>พิสูจน์ "ทุกบรรทัดเสีย VAT 7%" จากตัวเลขหัวใบ แล้วเติมเฉพาะบรรทัดที่ยังไม่มีอัตรา (pure)</summary>
    /// <param name="lineAmounts">ยอดที่จะเขียนจริงต่อบรรทัด (หลังกระจายส่วนลด) — ก่อน VAT หรือรวม VAT ตาม <paramref name="pricesIncludeVat"/></param>
    /// <param name="decidedRates">อัตราที่ชั้นบนตั้งแล้ว (engine/ผู้ใช้/สัญลักษณ์บนกระดาษ) · null = ยังไม่มี</param>
    /// <param name="headerVat">VAT บนกระดาษ</param>
    /// <param name="headerNetBase">ฐานก่อน VAT หลังหักส่วนลดบนกระดาษ (0 = ไม่รู้)</param>
    /// <param name="headerTotal">ยอดรวมทั้งสิ้นบนกระดาษ (0 = ไม่รู้)</param>
    /// <param name="pricesIncludeVat">ตัวกระทบยอดตัดสินว่ายอดบรรทัดรวม VAT แล้ว (<see cref="OcrLineReconciler"/> เคส A/E)</param>
    /// <param name="paperExemptAmount">ยอดยกเว้น/ไม่มี VAT ที่กระดาษพิมพ์ (<see cref="PaperExemptAmount"/>) · null = กระดาษไม่บอก</param>
    /// <param name="vatPrintedOnPaper">VAT หัวใบพิมพ์บนกระดาษ<b>ในฐานะ VAT</b> (<see cref="OcrHeaderVatEvidence.Classify"/> =
    /// <see cref="OcrHeaderVatSource.Labelled"/>) · false = ระบบคำนวณเอง/หาไม่เจอ ⇒ <see cref="OcrLineVatPlanVerdict.Unknown"/> เสมอ
    /// (รอบ 195 ฝ่ายค้าน C1 — ตรวจ VAT ที่ผลิตด้วย 7/107 ด้วยสูตร 7/107 = ผ่านตลอดกาล)</param>
    public static OcrLineVatPlan PlanWholeInvoice(
        IReadOnlyList<decimal> lineAmounts, IReadOnlyList<decimal?> decidedRates,
        decimal headerVat, decimal headerNetBase, decimal headerTotal, bool pricesIncludeVat,
        decimal? paperExemptAmount, bool vatPrintedOnPaper)
    {
        var n = lineAmounts.Count;
        OcrLineVatPlan No(string why) => new(OcrLineVatPlanVerdict.Unknown, new decimal?[n], why);

        if (n == 0 || decidedRates.Count != n) return No("ไม่มีบรรทัด");
        if (headerVat <= 0m) return No("กระดาษไม่มี VAT — ชั้นพิสูจน์ทั้งใบไม่ตัดสิน");
        // ★ ก่อนเลขคณิตทุกข้อ: VAT ที่ระบบคำนวณเอง (7/107 ของยอดรวม) ผ่านเงื่อนไขข้างล่างทุกครั้งโดยการสร้าง ⇒ ไม่ใช่หลักฐาน
        if (!vatPrintedOnPaper)
            return No($"VAT หัวใบ {headerVat:N2} ไม่ได้อ่านจากแถว VAT บนกระดาษ (ระบบคำนวณเอง/หาป้าย VAT ไม่เจอ) — "
                + "ตรวจด้วยสูตรเดียวกับที่ผลิตค่าไม่ได้ ไม่ตัดสิน");

        var openIdx = Enumerable.Range(0, n).Where(i => !decidedRates[i].HasValue).ToList();
        if (openIdx.Count == 0) return No("ทุกบรรทัดมีอัตราจาก engine/ผู้ใช้/สัญลักษณ์บนกระดาษแล้ว — ไม่แตะ");

        for (var i = 0; i < n; i++)
        {
            if (decidedRates[i] is decimal r && r != StandardRate && lineAmounts[i] != 0m)
                return No($"บรรทัดที่ {i + 1} ถูกตั้งเป็น {RateText(r)} โดยชั้นที่ใกล้กระดาษกว่า — หลักฐานขัดกับ \"ทั้งใบ 7%\" ไม่ตัดสิน");
        }

        if (paperExemptAmount is decimal ex && ex > 0m)
            return No($"กระดาษพิมพ์ยอดยกเว้น/ไม่มี VAT {ex:N2} — ใบนี้ไม่ใช่ 7% ทั้งใบ ไม่ตัดสิน");

        var counted = lineAmounts.Count(a => a != 0m);
        if (counted == 0) return No("ทุกบรรทัดยอด 0 — ไม่มีฐานให้พิสูจน์");
        var sum = lineAmounts.Sum();
        var sumSlack = Math.Max(0.02m, 0.01m * counted);
        var vatSlack = Math.Max(0.02m, 0.005m * counted);

        string proof;
        if (pricesIncludeVat)
        {
            if (headerTotal <= 0m) return No("ราคารวม VAT แต่ไม่รู้ยอดรวมบนกระดาษ — ไม่ตัดสิน");
            if (Math.Abs(sum - headerTotal) > sumSlack)
                return No($"ผลรวมบรรทัด {sum:N2} ≠ ยอดรวมบนกระดาษ {headerTotal:N2} — ไม่ตัดสิน");
            var expected = Math.Round(headerTotal * StandardRate / (100m + StandardRate), 2, R);
            if (Math.Abs(expected - headerVat) > vatSlack)
                return No($"7/107 ของยอดรวม {headerTotal:N2} = {expected:N2} ≠ VAT บนกระดาษ {headerVat:N2} — อาจมีรายการยกเว้น ไม่ตัดสิน");
            proof = $"VAT หัวใบ {headerVat:N2} = 7/107 × {headerTotal:N2}";
        }
        else
        {
            if (headerNetBase <= 0m) return No("ไม่รู้ฐานก่อน VAT บนกระดาษ — ไม่ตัดสิน");
            if (Math.Abs(sum - headerNetBase) > sumSlack)
                return No($"ผลรวมบรรทัด {sum:N2} ≠ ฐานก่อน VAT บนกระดาษ {headerNetBase:N2} — ไม่ตัดสิน");
            var expected = OutputVatRate.VatOn(headerNetBase, StandardRate);
            if (Math.Abs(expected - headerVat) > vatSlack)
                return No($"7% × ฐาน {headerNetBase:N2} = {expected:N2} ≠ VAT บนกระดาษ {headerVat:N2} — อาจมีรายการยกเว้น ไม่ตัดสิน");
            proof = $"VAT หัวใบ {headerVat:N2} = 7% × {headerNetBase:N2}";
        }

        var rates = new decimal?[n];
        foreach (var i in openIdx) rates[i] = StandardRate;
        var exemptText = paperExemptAmount is decimal z ? $" · ยอดยกเว้นบนกระดาษ {z:N2}" : "";
        return new OcrLineVatPlan(OcrLineVatPlanVerdict.AllStandard7, rates,
            $"อัตรา VAT บรรทัดพิสูจน์จากตัวเลขหัวใบ: {proof}{exemptText} ⇒ ทุกบรรทัด 7% "
            + $"(เติม {openIdx.Count} บรรทัดที่ยังไม่มีอัตรา · ไม่ใช้การเดาจากชื่อสินค้า)");
    }

    /// <summary>ยอดยกเว้น/ไม่มี VAT ที่กระดาษพิมพ์ไว้ — มากสุดของ (แถว "ยกเว้น/ไม่ต้องเสียภาษี") กับ (Σ ฐานของกลุ่มที่ไม่ใช่ 7%
    /// ในตารางสรุปตามรหัส ภ.พ. รวมรหัสที่ไม่รู้ความหมาย) · null = กระดาษไม่บอกทั้งสองแบบ</summary>
    public static decimal? PaperExemptAmount(OcrPaperVatSplit split, OcrVatGroupTable groups)
    {
        decimal? fromTable = null;
        if (groups.Found)
            fromTable = groups.Groups.Where(g => g.Kind != OcrVatGroupKind.Standard7).Sum(g => g.Net);
        if (split.NonTaxableAmount is decimal nt)
            return fromTable is decimal t ? Math.Max(nt, t) : nt;
        return fromTable;
    }

    /// <summary>คำแนะนำตอนอนุมัติ (P4): บรรทัดของเอกสาร<b>ตอนนี้</b>ไม่เป็น 7% ทั้งหมด แต่ VAT บนกระดาษ = 7% ของฐานทั้งใบพอดี
    /// ⇒ บอกทางแก้เป็นตัวเลข · null = พิสูจน์ไม่ได้ หรือทุกบรรทัดเป็น 7% อยู่แล้ว (ไม่มีอะไรแนะนำ)</summary>
    /// <param name="lines">(ยอดก่อน VAT, อัตรา) ของบรรทัดเอกสารตอนนี้</param>
    /// <param name="netBase">ฐานก่อน VAT ของเอกสาร = Σ ยอดบรรทัด + ผลต่างปัดเศษ</param>
    /// <param name="paperVat">VAT บนกระดาษ</param>
    /// <param name="paperExemptAmount">ยอดยกเว้นบนกระดาษ (<see cref="PaperExemptAmount"/>)</param>
    /// <param name="vatPrintedOnPaper">VAT หัวใบพิมพ์บนกระดาษในฐานะ VAT (<see cref="OcrHeaderVatEvidence.Classify"/>) — false = ไม่แนะนำ
    /// (VAT ที่ระบบแยก 7/107 เอง = 7% ของฐานเสมอ ⇒ คำแนะนำ "ตั้ง 7%" จะชวนให้เคลมภาษีซื้อที่ไม่มีบนกระดาษ · รอบ 195 ฝ่ายค้าน C1)</param>
    public static string? RateAdvice(
        IReadOnlyList<(decimal Net, decimal VatRate)> lines, decimal netBase, decimal paperVat, decimal? paperExemptAmount,
        bool vatPrintedOnPaper)
    {
        if (paperVat <= 0m || netBase <= 0m || !vatPrintedOnPaper) return null;
        if (paperExemptAmount is decimal ex && ex > 0m) return null;
        var withAmount = lines.Where(l => l.Net != 0m).ToList();
        if (withAmount.Count == 0 || withAmount.All(l => l.VatRate == StandardRate)) return null;
        var vatSlack = Math.Max(0.02m, 0.005m * withAmount.Count);
        if (Math.Abs(OutputVatRate.VatOn(netBase, StandardRate) - paperVat) > vatSlack) return null;
        return $"ตั้งอัตรา VAT บรรทัดเป็น 7% (VAT หัวใบ {paperVat:N2} = 7% × {netBase:N2})";
    }

    private static string RateText(decimal r) => r == ThaiVatTypeRule.ExemptRate ? "ยกเว้น" : $"{r:0.##}%";
}
