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

/// <summary>กลุ่มภาษีของตารางสรุปท้ายใบ — <c>Unknown</c> = กระดาษไม่ได้บอกว่ารหัสนี้คืออะไร (ห้ามเดา)</summary>
public enum OcrVatGroupKind
{
    Unknown = 0,
    /// <summary>มี VAT 7%</summary>
    Standard7 = 1,
    /// <summary>อัตราศูนย์ §80/1 (คำอธิบายบนกระดาษระบุ 0%/zero เอง)</summary>
    ZeroRated = 2,
    /// <summary>ยกเว้น §81 / ไม่มี VAT</summary>
    Exempt = 3,
}

/// <summary>แถวหนึ่งของตารางสรุปตามกลุ่มภาษี (ยอด<b>หลัง</b>หักส่วนลดของกลุ่มนั้นตามที่กระดาษพิมพ์)</summary>
/// <param name="PaperCode">รหัสบนกระดาษ ("1" · "2" · "V")</param>
/// <param name="Net">ฐานก่อน VAT ของกลุ่ม</param>
/// <param name="Vat">VAT ของกลุ่ม</param>
/// <param name="Gross">รวมของกลุ่ม (= Net + Vat บนกระดาษ)</param>
/// <param name="ItemCount">จำนวนชิ้นที่กระดาษพิมพ์ (null = ไม่มีคอลัมน์นี้)</param>
/// <param name="Evidence">ทำไมตีเป็นกลุ่มนี้ (คำอธิบายรหัสบนกระดาษ / VAT = 0 / VAT = 7% ของฐาน)</param>
public sealed record OcrVatGroup(
    OcrVatGroupKind Kind, string PaperCode, decimal Net, decimal Vat, decimal Gross, int? ItemCount, string Evidence);

/// <summary>ตารางสรุปตามกลุ่มภาษีทั้งตาราง — <see cref="Found"/> = false แปลว่าไม่มีตารางที่ใช้ได้
/// (ไม่มีเลย หรือผลรวมแถวไม่เท่าแถว "รวม" ⇒ ใช้ไม่ได้ทั้งตาราง ห้ามใช้ครึ่ง ๆ กลาง ๆ)</summary>
/// <param name="Net">ฐานรวม (แถว "รวม" หรือ Σ แถว)</param>
/// <param name="Vat">VAT รวม</param>
/// <param name="Gross">รวมทั้งตาราง = ยอดใบกำกับ (เมื่อตารางครอบทุกกลุ่ม)</param>
/// <param name="TotalLineNo">บรรทัดของแถว "รวม" (หรือแถวสุดท้ายของตาราง) · −1 = ไม่มีตาราง</param>
/// <param name="Evidence">ข้อความสรุปที่มา — ใส่ trace/หมายเหตุได้ตรง ๆ</param>
public sealed record OcrVatGroupTable(
    IReadOnlyList<OcrVatGroup> Groups, decimal Net, decimal Vat, decimal Gross, int TotalLineNo, string Evidence)
{
    public bool Found => Groups.Count > 0;

    /// <summary>ทุกกลุ่มรู้ชนิด (ไม่มี <see cref="OcrVatGroupKind.Unknown"/>) — เงื่อนไขก่อนใช้แยกบรรทัด</summary>
    public bool AllKnown => Groups.Count > 0 && Groups.All(g => g.Kind != OcrVatGroupKind.Unknown);

    public static OcrVatGroupTable None { get; } =
        new(Array.Empty<OcrVatGroup>(), 0m, 0m, 0m, -1, "");
}

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

    // ═══ ตารางสรุปตามกลุ่มภาษี (รอบ 192 · Total-first) ═══════════════════════════════════════

    /// <summary>ตารางสรุปตามรหัส ภ.พ. แบบห้าง: <c>จำนวนชิ้น · รหัส(ตัวเลข) · ฐาน · VAT · รวม</c>
    /// (Makro: <c>17 1 6,260.00 0.00 6,260.00</c>) — <see cref="SummaryRow"/> เดิมรับแต่รหัสตัวอักษร+อัตรา</summary>
    private static readonly Regex NumericCodeRow = new(
        @"^[ \t]*(?<qty>\d{1,6})[ \t]+(?<code>\d)[ \t]+(?<net>" + Money + @")[ \t]+(?<vat>" + Money
        + @")[ \t]+(?<gross>" + Money + @")[ \t]*$", RegexOptions.CultureInvariant);

    /// <summary>แถว "รวม" ของตารางสรุป: <c>รวม 22,663.97 1,148.28 23,812.25</c> (จำนวนชิ้นรวมนำหน้าได้)</summary>
    private static readonly Regex GroupTotalRow = new(
        @"^[ \t]*(?:\d{1,6}[ \t]+)?(?:รวม(?:ทั้งสิ้น)?|total)[ \t]*:?[ \t]+(?<net>" + Money + @")[ \t]+(?<vat>"
        + Money + @")[ \t]+(?<gross>" + Money + @")[ \t]*$", Opt);

    /// <summary>คำอธิบายรหัสตัวเลข: <c>1=สินค้าได้รับการยกเว้นภาษีมูลค่าเพิ่ม · 2=สินค้าที่ต้องเสียภาษีมูลค่าเพิ่ม</c>
    /// — หลายคู่ในบรรทัดเดียว (คำอธิบายหยุดก่อนคู่ถัดไป)</summary>
    private static readonly Regex DigitLegend = new(
        @"(?:^|[ \t(,/|·:])(?<code>\d)[ \t]*=[ \t]*(?<desc>[^=·|]+?)(?=[ \t]*[·,/|]?[ \t]*\d[ \t]*=|[ \t]*[·|]?[ \t]*$)",
        RegexOptions.CultureInvariant);

    private const decimal GroupTol = 0.02m;

    /// <summary>
    /// **อ่านตารางสรุปตามกลุ่มภาษีท้ายใบ** — ทั้งแบบรหัสตัวอักษร (<c>V 7 ฐาน VAT รวม</c>) และแบบห้างที่ใช้รหัส
    /// <b>ตัวเลข</b> + คอลัมน์จำนวนชิ้น (<c>17 1 6,260.00 0.00 6,260.00</c>) · <see cref="Read"/>/<see cref="Assign"/>
    /// เดิม<b>ไม่แตะ</b> (เพิ่มวิธีคิด ไม่รื้อ)
    ///
    /// <para>ที่มา (รอบ 192 · ใบ Makro หน้า 3/3): หน้าสุดท้ายมีแต่ตารางสรุปตามรหัส ภ.พ. — ยกเว้น 6,260.00 ·
    /// มี VAT ฐาน 16,403.97 VAT 1,148.28 · รวม 23,812.25 — แต่ไม่มีตัวอ่าน ⇒ ใบถูกลงเป็นบรรทัดเดียว 7% ทั้งใบ
    /// และเส้น Tesseract แต่ง VAT 7/107 = 1,577.29 ทั้งที่กระดาษพิมพ์ 1,148.28 ไว้</para>
    ///
    /// <para>กติกา (DECISION_DOCTRINE §1): ความหมายของรหัสมาจาก<b>คำอธิบายบนกระดาษ</b>ก่อน · ไม่มีคำอธิบาย ⇒
    /// พิสูจน์ด้วยตัวเลขของแถว (VAT 0.00 = ไม่มี VAT · VAT = 7% ของฐาน = มี VAT) · รหัสที่คำอธิบายไม่บอก/
    /// คำอธิบายขัดกับตัวเลข = <see cref="OcrVatGroupKind.Unknown"/> · ทุกแถวต้อง ฐาน + VAT = รวม และ
    /// Σ แถว = แถว "รวม" (ตารางรหัสตัวเลข<b>ต้องมี</b>แถว "รวม" — กันแถวตัวเลขล้วนอื่นหลุดเข้ามา) ·
    /// ไม่ผ่านข้อใด = <see cref="OcrVatGroupTable.None"/> ทั้งตาราง</para>
    /// </summary>
    public static OcrVatGroupTable ReadGroups(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return OcrVatGroupTable.None;
        var lines = rawText.Replace("\r", "").Split('\n');
        var legend = new Dictionary<string, (OcrVatGroupKind Kind, string Desc)>(StringComparer.Ordinal);
        var numeric = new List<(int Qty, string Code, decimal Net, decimal Vat, decimal Gross, int LineNo)>();
        var letter = new List<(string Code, decimal Rate, decimal Net, decimal Vat, decimal Gross, int LineNo)>();
        decimal totNet = 0m, totVat = 0m, totGross = 0m;
        var totLine = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0) continue;

            var nm = NumericCodeRow.Match(line);
            if (nm.Success)
            {
                numeric.Add((int.Parse(nm.Groups["qty"].Value, CultureInfo.InvariantCulture), nm.Groups["code"].Value,
                    ParseMoney(nm.Groups["net"].Value), ParseMoney(nm.Groups["vat"].Value),
                    ParseMoney(nm.Groups["gross"].Value), i));
                continue;
            }
            var sm = SummaryRow.Match(line);
            if (sm.Success)
            {
                letter.Add((sm.Groups["code"].Value,
                    decimal.Parse(sm.Groups["rate"].Value, CultureInfo.InvariantCulture),
                    ParseMoney(sm.Groups["net"].Value), ParseMoney(sm.Groups["vat"].Value),
                    ParseMoney(sm.Groups["gross"].Value), i));
                continue;
            }
            var tm = GroupTotalRow.Match(line);
            if (tm.Success && (numeric.Count > 0 || letter.Count > 0) && totLine < 0)
            {
                totNet = ParseMoney(tm.Groups["net"].Value);
                totVat = ParseMoney(tm.Groups["vat"].Value);
                totGross = ParseMoney(tm.Groups["gross"].Value);
                totLine = i;
                continue;
            }
            foreach (Match lg in DigitLegend.Matches(line))
            {
                var desc = lg.Groups["desc"].Value.Trim();
                legend[lg.Groups["code"].Value] = (KindFromLegend(desc), desc);
            }
        }

        if (numeric.Count == 0 && letter.Count == 0) return OcrVatGroupTable.None;

        var groups = new List<OcrVatGroup>();
        foreach (var r in numeric)
        {
            if (Math.Abs(r.Net + r.Vat - r.Gross) > GroupTol) return OcrVatGroupTable.None;
            var (kind, why) = NumericKind(r.Code, r.Net, r.Vat, r.Qty, legend);
            groups.Add(new OcrVatGroup(kind, r.Code, r.Net, r.Vat, r.Gross, r.Qty, why));
        }
        foreach (var r in letter)
        {
            if (Math.Abs(r.Net + r.Vat - r.Gross) > GroupTol) return OcrVatGroupTable.None;
            var kind = r.Rate == 7m && Math.Abs(Math.Round(r.Net * 0.07m, 2, MidpointRounding.AwayFromZero) - r.Vat) <= RateSlack(null)
                ? OcrVatGroupKind.Standard7
                : r.Rate == 0m && r.Vat == 0m ? OcrVatGroupKind.Exempt
                : OcrVatGroupKind.Unknown;
            groups.Add(new OcrVatGroup(kind, r.Code, r.Net, r.Vat, r.Gross, null,
                $"แถวสรุป “{r.Code} {r.Rate:0.##}” บนกระดาษ"));
        }

        var sumNet = groups.Sum(g => g.Net);
        var sumVat = groups.Sum(g => g.Vat);
        var sumGross = groups.Sum(g => g.Gross);
        if (totLine >= 0)
        {
            // Σ แถว ≠ แถว "รวม" = อ่านแถวใดแถวหนึ่งผิด/ขาด ⇒ ใช้ไม่ได้ทั้งตาราง
            if (Math.Abs(sumNet - totNet) > GroupTol || Math.Abs(sumVat - totVat) > GroupTol
                || Math.Abs(sumGross - totGross) > GroupTol)
                return OcrVatGroupTable.None;
        }
        else if (numeric.Count > 0)
            return OcrVatGroupTable.None;   // ตารางรหัสตัวเลขต้องมีแถว "รวม" ยืนยัน
        else
        {
            totNet = sumNet; totVat = sumVat; totGross = sumGross;
            totLine = letter[letter.Count - 1].LineNo;
        }

        var evidence = "ตารางสรุปตามกลุ่มภาษีบนกระดาษ: "
            + string.Join(" · ", groups.Select(g => $"รหัส {g.PaperCode} ฐาน {g.Net:N2} VAT {g.Vat:N2}"))
            + $" · รวม {totGross:N2}";
        return new OcrVatGroupTable(groups, totNet, totVat, totGross, totLine, evidence);
    }

    private static (OcrVatGroupKind Kind, string Why) NumericKind(
        string code, decimal net, decimal vat, int qty,
        IReadOnlyDictionary<string, (OcrVatGroupKind Kind, string Desc)> legend)
    {
        var sevenPct = Math.Abs(Math.Round(net * 0.07m, 2, MidpointRounding.AwayFromZero) - vat) <= RateSlack(qty);
        if (legend.Count > 0)
        {
            if (!legend.TryGetValue(code, out var lg))
                return (OcrVatGroupKind.Unknown, $"รหัส {code} ไม่มีคำอธิบายบนกระดาษ — ไม่เดา");
            // คำอธิบายขัดกับตัวเลขของแถว = ไม่รู้ (อ่านรหัส/ตัวเลขผิดสักตัว)
            if (lg.Kind == OcrVatGroupKind.Exempt && vat != 0m)
                return (OcrVatGroupKind.Unknown, $"รหัส {code} “{lg.Desc}” แต่แถวมี VAT {vat:N2} — ขัดกัน ไม่เดา");
            if (lg.Kind == OcrVatGroupKind.Standard7 && !sevenPct)
                return (OcrVatGroupKind.Unknown, $"รหัส {code} “{lg.Desc}” แต่ VAT {vat:N2} ไม่ใช่ 7% ของ {net:N2} — ไม่เดา");
            return (lg.Kind, $"รหัส {code} = “{lg.Desc}” (คำอธิบายบนกระดาษ)");
        }
        if (vat == 0m && net > 0m) return (OcrVatGroupKind.Exempt, $"รหัส {code}: VAT บนกระดาษ 0.00");
        if (vat > 0m && sevenPct) return (OcrVatGroupKind.Standard7, $"รหัส {code}: VAT {vat:N2} = 7% ของ {net:N2}");
        return (OcrVatGroupKind.Unknown, $"รหัส {code}: ไม่มีคำอธิบายและตัวเลขไม่บอกอัตรา — ไม่เดา");
    }

    private static OcrVatGroupKind KindFromLegend(string desc)
        => NonTaxableLabel.IsMatch(desc) ? OcrVatGroupKind.Exempt
         : Regex.IsMatch(desc, @"อัตรา(?:ภาษี)?[ \t]*ศูนย์|zero|\b0[ \t]*%", Opt) ? OcrVatGroupKind.ZeroRated
         : Regex.IsMatch(desc, @"ต้องเสียภาษี|ที่เสียภาษี|vatable|taxable|7[ \t]*%", Opt) ? OcrVatGroupKind.Standard7
         : OcrVatGroupKind.Unknown;

    /// <summary>ค่าเผื่อ "VAT = 7% ของฐาน" ของแถวสรุปที่รวมหลายชิ้น — VAT รายชิ้นปัดได้ครึ่งสตางค์ต่อชิ้น</summary>
    private static decimal RateSlack(int? itemCount)
        => Math.Max(0.10m, 0.005m * (itemCount ?? 0));

    private static decimal ParseMoney(string s)
        => decimal.Parse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture);

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
