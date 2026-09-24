using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>สิ่งที่กระดาษบอกเรื่อง "บรรทัดไหนมี VAT" — ทุกช่อง null/ว่าง = กระดาษไม่บอก</summary>
/// <param name="TaxableAmount">ยอดที่กระดาษพิมพ์ว่า "ต้องเสียภาษี / VATABLE" (null = ไม่พบ)</param>
/// <param name="NonTaxableAmount">ยอดที่กระดาษพิมพ์ว่า "ไม่ต้องเสียภาษี / ยกเว้น / NON-VAT" (null = ไม่พบ)</param>
/// <param name="CodeRates">ความหมายของสัญลักษณ์ท้ายบรรทัดที่<b>กระดาษประกาศเอง</b> (ตารางสรุป VAT
/// หรือคำอธิบาย "V = VATABLE") → อัตรา (<c>7</c> · <c>0</c> · <c>-1</c> ยกเว้น)</param>
/// <param name="Marks">ยอด + สัญลักษณ์ท้ายบรรทัดรายการ ตามลำดับบนกระดาษ</param>
public sealed record OcrPaperVatSplit(
    decimal? TaxableAmount,
    decimal? NonTaxableAmount,
    IReadOnlyDictionary<string, decimal> CodeRates,
    IReadOnlyList<(decimal Amount, string Code)> Marks);

/// <summary>ผลการตัดสินอัตรา VAT รายบรรทัดจากสัญลักษณ์บนกระดาษ</summary>
/// <param name="Applied">ใช้ได้ (พิสูจน์ด้วยยอดบนกระดาษแล้ว) — false = ไม่แตะอะไร</param>
/// <param name="Rates">อัตราต่อบรรทัดตามลำดับเดิม (null = ไม่ตัดสินบรรทัดนั้น)</param>
/// <param name="Note">เหตุผลภาษาไทย — ทั้งตอนใช้และตอนไม่ใช้ (ให้คนตามรอยได้)</param>
public sealed record OcrLineVatAssignment(bool Applied, decimal?[] Rates, string Note);

/// <summary>
/// **ใบเดียวมีทั้งรายการ VAT 7% และไม่มี VAT — อ่าน "สัญลักษณ์ท้ายบรรทัด" ที่กระดาษพิมพ์ไว้**
///
/// ═══ ที่มา (รอบ 190 ข้อ 9 ของเจ้าของ) ═══
/// <para>บิลห้าง/ซูเปอร์มาร์เก็ต/Makro พิมพ์สัญลักษณ์หลังยอดแต่ละบรรทัด (<c>524.00 V</c> ·
/// <c>45.00 N</c>) และตารางสรุปท้ายใบ (<c>V 7 3,357.94 235.06 3,593.00</c>) หรือ
/// "ยอดที่ต้องเสียภาษี / ยอดที่ไม่ต้องเสียภาษี" — <b>ทั้งเรพไม่มีใครอ่านเลย</b>. อัตรารายบรรทัด
/// มาจาก <c>ThaiVatTypeRule</c> ซึ่ง "เดาจากชื่อสินค้า" (ผัก/นม/หนังสือ) ⇒ ไข่ไก่/เนื้อหมู/ปลา
/// ที่ยกเว้น §81 แต่ไม่อยู่ในรายการคำ ถูกตั้ง 7% แล้ว <c>SpreadHeaderVat</c> เฉลี่ย VAT หัวใบ
/// ลงทุกบรรทัด ⇒ <b>ยอดรวมยังตรงกระดาษ</b> จึงเงียบสนิท แต่ฐานภาษี §87 ผิด และพอเปิดแก้แล้ว
/// บันทึก ระบบคิด VAT ใหม่ = 7% × ทุกบรรทัด ⇒ ยอดเอกสารเปลี่ยนเอง ≠ กระดาษ</para>
///
/// ═══ กติกา (DECISION_DOCTRINE §1 — หลักฐานใกล้ของจริงชนะการเดา) ═══
/// <list type="number">
/// <item>ความหมายของสัญลักษณ์: ตารางสรุป VAT บนกระดาษ &gt; คำอธิบายบนกระดาษ ("V = VATABLE") &gt;
///   ความหมายสากลของตัวอักษร (V/T = มี VAT · N/E/X = ไม่มี VAT · Z = 0%) · <c>*</c>/<c>#</c> ไม่มี
///   ความหมายสากล ⇒ ต้องมีคำอธิบายบนกระดาษเท่านั้น</item>
/// <item>จับคู่บรรทัด↔สัญลักษณ์ด้วย<b>ยอดเงินตรงถึงสตางค์ตามลำดับ</b> — บรรทัดที่มีเงินจับคู่ไม่ได้
///   แม้บรรทัดเดียว = ไม่ใช้เลย (ห้ามใช้ครึ่ง ๆ กลาง ๆ)</item>
/// <item><b>ต้องพิสูจน์ด้วยยอดที่พิมพ์บนกระดาษ</b> อย่างน้อยหนึ่งอย่าง: VAT หัวใบ = 7% (หรือ 7/107)
///   ของผลรวมบรรทัดที่มี VAT · หรือยอด "ต้องเสียภาษี/ไม่ต้องเสียภาษี" ตรง · หรือยอดในตารางสรุปตรง
///   (บทเรียน AmountTriple: "ลงตัวทางคณิต" ต้องผูกกับค่าที่อ่านได้จริง)</item>
/// <item>ทุกบรรทัดอัตราเดียวกัน ⇒ <b>ไม่แตะ</b> (เส้นเดิมให้คำตอบเดียวกันอยู่แล้ว — ลดพื้นที่ถดถอย
///   · ใบ Wine Pro ที่ทุกบรรทัดเป็น <c>V</c> จึงไม่ถูกแตะ)</item>
/// <item>อัตรา 0 ในตารางสรุปของบิลในประเทศ = "ไม่มี VAT" ⇒ ยกเว้น §81 (<c>-1</c>) — อัตราศูนย์ §80/1
///   ใช้กับการส่งออก/บริการใช้ต่างประเทศ ไม่ใช่บิลขายปลีกในประเทศ (คำอธิบายบนกระดาษที่ระบุ
///   "0%/zero" ยังได้ <c>0</c> ตามที่พิมพ์)</item>
/// </list>
/// </summary>
public static class OcrLineVatMarks
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Money = @"\d{1,3}(?:,\d{3})+\.\d{2}|\d+\.\d{2}";

    /// <summary>ตารางสรุป VAT ท้ายใบ: <c>V 7 3,357.94 235.06 3,593.00</c> (รหัส · อัตรา · ฐาน · VAT · รวม)</summary>
    private static readonly Regex SummaryRow = new(
        @"^[ \t]*(?<code>[A-Z*#])[ \t]+(?<rate>\d{1,2}(?:\.\d{1,2})?)[ \t]*%?[ \t]+(?<net>" + Money + @")[ \t]+(?<vat>"
        + Money + @")[ \t]+(?<gross>" + Money + @")[ \t]*$", RegexOptions.CultureInvariant);

    /// <summary>คำอธิบายสัญลักษณ์: <c>V = VATABLE</c> · <c>* : สินค้าไม่มีภาษี</c> — บรรทัดเดียวมีได้หลายคู่
    /// (<c>V = VATABLE    N = NON-VAT</c>) ⇒ คำอธิบายหยุดก่อนคู่ถัดไป (ไม่งั้น "V" ได้คำอธิบาย
    /// "VATABLE N = NON-VAT" แล้วถูกตีเป็นไม่มี VAT ทั้งที่กระดาษบอกตรงข้าม)</summary>
    private static readonly Regex LegendRow = new(
        @"(?:^|[ \t(,/|])(?<code>[A-Z]|[*#])[ \t]*[=:][ \t]*(?<desc>.+?)(?=[ \t,/|]+(?:[A-Z]|[*#])[ \t]*[=:]|[ \t)]*$)",
        RegexOptions.CultureInvariant);

    /// <summary>บรรทัดรายการที่ลงท้ายด้วย ยอด + สัญลักษณ์ตัวเดียว</summary>
    private static readonly Regex MarkedLine = new(
        @"(?<![\d.,])(?<amt>" + Money + @")[ \t]*(?<code>(?<![A-Za-z])[A-Z](?![A-Za-z])|[*#])[ \t]*$", RegexOptions.CultureInvariant);

    // ป้ายยอดแยกภาษี — ตรวจฝั่ง "ไม่ต้องเสีย" ก่อนเสมอ ("ไม่ต้องเสียภาษี" มีคำว่า "ต้องเสียภาษี" อยู่ข้างใน)
    private static readonly Regex NonTaxableLabel = new(
        @"ไม่ต้องเสียภาษี|ไม่เสียภาษี|ไม่มีภาษี|ยกเว้นภาษี|ยกเว้น[ \t]*vat|non[- \t]?vat(?:able)?|vat[ \t]*exempt|exempt|non[- \t]?taxable", Opt);
    private static readonly Regex TaxableLabel = new(
        @"ต้องเสียภาษี|ที่เสียภาษี|มีภาษี|vat[ \t]*able|vatable|taxable[ \t]*(?:amount|sales)?", Opt);
    private static readonly Regex MoneyToken = new(@"(?<![\d.,])(" + Money + @")(?![\d])", Opt);

    /// <summary>ตัวอักษรที่มีความหมายสากลบนบิลขายปลีกไทย (ใช้เมื่อกระดาษไม่ประกาศเอง)</summary>
    private static readonly IReadOnlyDictionary<string, decimal> DefaultLetterRates =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
        {
            ["V"] = 7m, ["T"] = 7m,
            ["N"] = ThaiVatTypeRule.ExemptRate, ["E"] = ThaiVatTypeRule.ExemptRate, ["X"] = ThaiVatTypeRule.ExemptRate,
            ["Z"] = 0m,
        };

    /// <summary>อ่านทุกอย่างที่กระดาษบอกเรื่อง VAT รายบรรทัด (pure)</summary>
    public static OcrPaperVatSplit Read(string? rawText)
    {
        var codeRates = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var marks = new List<(decimal, string)>();
        decimal? taxable = null, nonTaxable = null;
        if (string.IsNullOrWhiteSpace(rawText))
            return new OcrPaperVatSplit(null, null, codeRates, marks);

        var legend = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawText.Replace("\r", "").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            var sm = SummaryRow.Match(line);
            if (sm.Success)
            {
                var rate = decimal.Parse(sm.Groups["rate"].Value, CultureInfo.InvariantCulture);
                codeRates[sm.Groups["code"].Value] = rate > 0m ? rate : ThaiVatTypeRule.ExemptRate;
                continue;   // แถวสรุปไม่ใช่แถวรายการ
            }

            // ยอดแยกภาษีท้ายใบ (ต้องอยู่บรรทัดเดียวกับป้าย) — ข้ามแถวรายการที่มีสัญลักษณ์ท้าย
            // ("ผักกาด (ยกเว้นภาษี) 30.00 N" ไม่ใช่ยอดสรุป) และเอาตัว**สุดท้าย** (แถวสรุปอยู่ท้ายใบ)
            var isItemRow = MarkedLine.IsMatch(line);
            if (!isItemRow && NonTaxableLabel.IsMatch(line) && LastMoney(line) is decimal nt)
                nonTaxable = nt;
            else if (!isItemRow && TaxableLabel.IsMatch(line) && LastMoney(line) is decimal tx)
                taxable = tx;

            if (!isItemRow)
            {
                foreach (Match lg in LegendRow.Matches(line))
                {
                    var desc = lg.Groups["desc"].Value;
                    decimal? r = NonTaxableLabel.IsMatch(desc) ? ThaiVatTypeRule.ExemptRate
                        : Regex.IsMatch(desc, @"อัตรา(?:ภาษี)?[ \t]*ศูนย์|zero|\b0[ \t]*%", Opt) ? 0m
                        : Regex.IsMatch(desc, @"vat|ภาษีมูลค่าเพิ่ม|มีภาษี|เสียภาษี|7[ \t]*%", Opt) ? 7m
                        : null;
                    if (r is decimal rr) legend[lg.Groups["code"].Value] = rr;
                }
            }

            var mk = MarkedLine.Match(line);
            if (mk.Success && decimal.TryParse(mk.Groups["amt"].Value.Replace(",", ""),
                    NumberStyles.Number, CultureInfo.InvariantCulture, out var amt))
                marks.Add((amt, mk.Groups["code"].Value.ToUpperInvariant()));
        }
        // ตารางสรุปบนกระดาษชนะคำอธิบาย
        foreach (var (k, v) in legend) codeRates.TryAdd(k, v);
        return new OcrPaperVatSplit(taxable, nonTaxable, codeRates, marks);
    }

    /// <summary>ตัดสินอัตรา VAT รายบรรทัดจากสัญลักษณ์บนกระดาษ — ใช้ได้เฉพาะเมื่อพิสูจน์ด้วยยอดบนกระดาษแล้ว</summary>
    /// <param name="lineAmounts">ยอดของแต่ละบรรทัดตามที่พิมพ์ (ก่อนส่วนลดท้ายบิล) ตามลำดับเดิม</param>
    /// <param name="paper">ผลของ <see cref="Read"/></param>
    /// <param name="headerVat">VAT บนหัวใบ</param>
    public static OcrLineVatAssignment Assign(
        IReadOnlyList<decimal> lineAmounts, OcrPaperVatSplit paper, decimal headerVat)
    {
        var rates = new decimal?[lineAmounts.Count];
        OcrLineVatAssignment No(string why) => new(false, new decimal?[lineAmounts.Count], why);

        if (lineAmounts.Count == 0 || paper.Marks.Count == 0)
            return No("กระดาษไม่มีสัญลักษณ์ VAT ท้ายบรรทัด");

        // 1) จับคู่ตามลำดับ ด้วยยอดตรงถึงสตางค์
        var j = 0;
        for (var i = 0; i < lineAmounts.Count; i++)
        {
            var a = Math.Round(lineAmounts[i], 2, MidpointRounding.AwayFromZero);
            var found = -1;
            for (var k = j; k < paper.Marks.Count; k++)
            {
                // ข้ามตัวอักษรที่ไม่มีทั้งคำอธิบายบนกระดาษและความหมายสากล (เช่น "500.00 B" = บาท)
                // — ไม่ใช่สัญลักษณ์ VAT · ส่วน * / # ยังนับ (แล้วไปตกด่าน "ไม่มีคำอธิบาย" ข้างล่าง)
                var c = paper.Marks[k].Code;
                var known = paper.CodeRates.ContainsKey(c) || DefaultLetterRates.ContainsKey(c) || c is "*" or "#";
                if (known && paper.Marks[k].Amount == a) { found = k; break; }
            }
            if (found < 0)
            {
                if (a == 0m) continue;   // บรรทัดยอด 0 ไม่มีผลต่อ VAT — ไม่ต้องบังคับ
                return No($"บรรทัดที่ {i + 1} (ยอด {a:N2}) หาไม่พบบนกระดาษพร้อมสัญลักษณ์ VAT — ไม่ใช้สัญลักษณ์ทั้งใบ");
            }
            var code = paper.Marks[found].Code;
            if (paper.CodeRates.TryGetValue(code, out var r) || DefaultLetterRates.TryGetValue(code, out r))
                rates[i] = r;
            else
                return No($"สัญลักษณ์ “{code}” บนกระดาษไม่มีคำอธิบายว่าหมายถึงอะไร — ไม่เดา");
            j = found + 1;
        }

        var decided = rates.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        if (decided.Select(x => x > 0m).Distinct().Count() < 2)
            return No("ทุกบรรทัดบนกระดาษอยู่กลุ่ม VAT เดียวกัน — ใช้กติกาเดิม");

        // 2) พิสูจน์ด้วยยอดที่พิมพ์บนกระดาษ
        decimal taxableSum = 0m, nonTaxSum = 0m;
        var taxableCount = 0;
        for (var i = 0; i < lineAmounts.Count; i++)
        {
            if (rates[i] is not decimal r) continue;
            if (r > 0m) { taxableSum += lineAmounts[i]; taxableCount++; }
            else nonTaxSum += lineAmounts[i];
        }
        var slack = Math.Max(0.02m, 0.01m * taxableCount);
        const MidpointRounding R = MidpointRounding.AwayFromZero;
        var proofs = new List<string>();
        if (headerVat > 0m)
        {
            if (Math.Abs(Math.Round(taxableSum * 7m / 107m, 2, R) - headerVat) <= slack)
                proofs.Add($"VAT บนกระดาษ {headerVat:N2} = 7/107 ของยอดที่มี VAT {taxableSum:N2}");
            else if (Math.Abs(Math.Round(taxableSum * 0.07m, 2, R) - headerVat) <= slack)
                proofs.Add($"VAT บนกระดาษ {headerVat:N2} = 7% ของยอดที่มี VAT {taxableSum:N2}");
        }
        if (paper.NonTaxableAmount is decimal pn && Math.Abs(pn - nonTaxSum) <= 0.02m)
            proofs.Add($"ยอดไม่มี VAT บนกระดาษ {pn:N2} ตรงกับผลรวมบรรทัด");
        if (paper.TaxableAmount is decimal pt && Math.Abs(pt - taxableSum) <= 0.02m)
            proofs.Add($"ยอดมี VAT บนกระดาษ {pt:N2} ตรงกับผลรวมบรรทัด");

        if (proofs.Count == 0)
            return No($"สัญลักษณ์บนกระดาษแบ่งเป็นมี VAT {taxableSum:N2} · ไม่มี VAT {nonTaxSum:N2} "
                + $"แต่ไม่ลงตัวกับ VAT บนกระดาษ {headerVat:N2} — ไม่ใช้ (อาจอ่านสัญลักษณ์/ยอดผิด)");

        return new OcrLineVatAssignment(true, rates,
            $"อัตรา VAT รายบรรทัดตามสัญลักษณ์บนกระดาษ: มี VAT {taxableSum:N2} · ไม่มี VAT {nonTaxSum:N2} "
            + $"({string.Join(" · ", proofs)})");
    }

    private static decimal? LastMoney(string text)
    {
        decimal? last = null;
        foreach (Match m in MoneyToken.Matches(text))
            if (decimal.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var v))
                last = v;
        return last;
    }
}
