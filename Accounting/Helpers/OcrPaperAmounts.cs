using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ยอดเงินหนึ่งตัวที่<b>พิมพ์อยู่บนกระดาษ</b> พร้อมบรรทัดที่พบ (ให้คนตามรอยได้)</summary>
/// <param name="Amount">ค่าสัมบูรณ์ (กระดาษพิมพ์ติดลบ/วงเล็บก็เก็บเป็นบวก)</param>
/// <param name="LineNo">ลำดับบรรทัดในข้อความ (เริ่ม 0) — ใช้ตัดสิน "อะไรอยู่ก่อน/หลัง"</param>
/// <param name="Text">บรรทัดกระดาษ (ตัดช่องว่างหัวท้าย)</param>
public readonly record struct OcrPrintedAmount(decimal Amount, int LineNo, string Text);

/// <summary>
/// **ตัวอ่าน "ตัวเลขที่พิมพ์บนกระดาษ" ที่ขั้นยึดยอดรวม (Total-first · รอบ 192) ใช้ร่วมกัน**
///
/// <para>ที่มา: ทีม B ไล่พบว่าไม่มีขั้นไหนในระบบถามว่า "ตัวเลขนี้พิมพ์อยู่บนกระดาษไหม · อยู่บนแถวที่มีป้ายอะไร"
/// — ทุกชั้นเชื่อ "ป้ายแรกที่ regex เจอ" ⇒ ใบ Makro ที่ป้าย <c>TOTAL</c> เป็นยอด<b>ก่อน</b>ส่วนลดได้ยอดรวมผิด 297.75 ·
/// <see cref="OcrTotalAnchor"/> (หายอดรวม) กับ <see cref="OcrTotalDecomposer"/> (แตกยอด) ต้องเห็นตัวเลขชุดเดียวกัน
/// จึงอยู่ที่นี่ที่เดียว (สองสำเนาของ regex ยอดเงิน = drift — CLAUDE.md F2 ข้อ 4)</para>
///
/// <para>กติกาเดียวกับตัวอ่านรอบ 190 (<see cref="OcrBillDiscount"/> · <see cref="PaperWhtReader"/>):
/// ยอดเงินต้องมีทศนิยม 2 ตำแหน่งหรือคอมมาหลักพัน (กันเลขจำนวนชิ้น/เลขที่/วันที่) · ตัดเปอร์เซ็นต์ทิ้งก่อน ·
/// อ่านทีละบรรทัด · <b>แถวยอด 0 ไม่ใช่หลักฐาน</b> (RG-02)</para>
/// </summary>
public static class OcrPaperAmounts
{
    /// <summary>ค่าเผื่อ "เลขเดียวกัน" ของตัวเลขที่พิมพ์ — ปัดเศษได้ไม่เกิน 1 สตางค์ต่อการปัด 1 ครั้ง</summary>
    public const decimal ExactTol = 0.02m;

    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex MoneyToken = new(
        @"(?<![\d.,])\(?(?<num>\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+\.\d{2})\)?(?![\d%])", Opt);

    private static readonly Regex PercentToken = new(@"\d+(?:[.,]\d+)?[ \t]*%", Opt);

    /// <summary>ป้าย VAT ที่ตามด้วย "ยอดภาษี"</summary>
    private static readonly Regex VatLabel = new(@"ภาษีมูลค่าเพิ่ม|(?<![A-Za-z])vat(?![A-Za-z])", Opt);

    /// <summary>บรรทัดที่มีคำว่า VAT แต่ตัวเลขเป็น<b>ยอดสินค้า</b> ("รวมภาษีมูลค่าเพิ่ม" · "ไม่รวม" · "ก่อนภาษี" ·
    /// "ยกเว้น" · "Included/Excluded VAT" · "ต้องเสียภาษี") หรือเลขทะเบียน</summary>
    private static readonly Regex VatLabelNotAmount = new(
        @"รวม[ \t]*(?:ภาษี|vat)|ก่อน[ \t]*(?:ภาษี|vat)|ยกเว้น|ต้องเสียภาษี|ไม่เสียภาษี|หลังหัก|"
        + @"incl\w*\.?[ \t]*(?:of[ \t]+)?vat|vat[ \t]*incl|excl\w*\.?[ \t]*(?:of[ \t]+)?vat|vat[ \t]*excl|before[ \t]*vat|"
        + @"non[- \t]?vat|vatable|exempt|regist|reg\.|เลขประจำตัว|เลขทะเบียน|"
        // รอบ 192 ฝ่ายค้าน C2: ยอดของ "ใบเดิม" บนใบลด/เพิ่มหนี้ ("VAT on original invoice 700.00") ไม่ใช่ VAT ของใบนี้
        + OriginalDocWords, Opt);

    /// <summary>คำที่บอกว่าตัวเลขบนแถวเป็นของ<b>เอกสารฉบับเดิม</b> (ใบลด/เพิ่มหนี้อ้างใบกำกับเดิม) — ไม่ใช่ยอดของใบนี้</summary>
    public const string OriginalDocWords =
        @"(?<![A-Za-z])(?:original|previous|prior)(?![A-Za-z])|ใบ(?:กำกับ(?:ภาษี)?|แจ้งหนี้)?เดิม|เดิม";

    /// <summary>แถว "อัตรา VAT" ที่ตัวเลขเป็นอัตรา (VAT RATE 7.00) — ไม่ใช่ยอดภาษี</summary>
    private static readonly Regex RateWord = new(@"(?<![A-Za-z])rate(?![A-Za-z])|อัตรา", Opt);

    /// <summary>สกุลเงินต่างประเทศบนกระดาษ — ใบสองสกุลมีชุดตัวเลขสองชุดที่ต่างก็ "ลงตัว" (ฝ่ายค้าน C2)</summary>
    private static readonly Regex ForeignCurrency = new(
        @"(?<![A-Za-z])(?:USD|EUR|JPY|SGD|CNY|RMB|GBP|HKD|AUD|MYR|KRW|TWD|VND|CHF|INR|IDR|PHP)(?![A-Za-z])|US\$|€|£|¥", Opt);

    private static readonly Regex DiscountLabel = new(@"ส่วนลด[ก-๙]*|(?<![A-Za-z])discounts?(?![A-Za-z])", Opt);

    /// <summary>บรรทัดที่มีคำว่าส่วนลดแต่ตัวเลขเป็นยอดเงินอื่น (กติกาเดียวกับ <see cref="OcrBillDiscount"/>)</summary>
    private static readonly Regex DiscountNotAmount = new(
        @"(?:หลัง|ก่อน)[ \t]*(?:หัก)?[ \t]*ส่วนลด|(?:after|before|less|net[ \t]+of)[ \t]+disc|ไม่มีส่วนลด|no[ \t]+discount", Opt);

    private static readonly Regex DepositLabel = new(@"มัดจำ|(?<![A-Za-z])deposit(?![A-Za-z])", Opt);

    /// <summary>แยกข้อความเป็นบรรทัด (ตัด <c>\r</c>) — ลำดับบรรทัดเป็นตัวตั้งของ "ก่อน/หลัง"</summary>
    public static IReadOnlyList<string> Lines(string? rawText)
        => string.IsNullOrEmpty(rawText) ? Array.Empty<string>() : rawText.Replace("\r", "").Split('\n');

    /// <summary>ยอดเงินบนบรรทัดเดียว (หลังตัดเปอร์เซ็นต์) ตามลำดับซ้าย→ขวา — ค่าสัมบูรณ์</summary>
    public static IReadOnlyList<decimal> MoneyOn(string? line)
    {
        var list = new List<decimal>();
        if (string.IsNullOrEmpty(line)) return list;
        var cleaned = PercentToken.Replace(line, " ");
        foreach (Match m in MoneyToken.Matches(cleaned))
        {
            if (decimal.TryParse(m.Groups["num"].Value.Replace(",", ""), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var v))
                list.Add(Math.Abs(v));
        }
        return list;
    }

    /// <summary>ทุกยอดเงินที่พิมพ์บนกระดาษ (รวมยอด 0) — ใช้ตอบ "เลขนี้มีอยู่บนกระดาษไหม"</summary>
    public static IReadOnlyList<OcrPrintedAmount> AllPrinted(string? rawText)
    {
        var lines = Lines(rawText);
        var list = new List<OcrPrintedAmount>();
        for (var i = 0; i < lines.Count; i++)
            foreach (var a in MoneyOn(lines[i]))
                list.Add(new OcrPrintedAmount(a, i, lines[i].Trim()));
        return list;
    }

    /// <summary>เลข <paramref name="amount"/> พิมพ์อยู่บนกระดาษไหม (±<see cref="ExactTol"/>) · 0 ไม่นับ</summary>
    public static bool IsPrinted(IReadOnlyList<OcrPrintedAmount> printed, decimal amount)
        => amount > 0m && printed.Any(p => Math.Abs(p.Amount - amount) <= ExactTol);

    /// <summary>ยอด VAT ที่พิมพ์บนกระดาษ: แถวที่มีป้าย VAT (ตัวเลขตัวสุดท้ายของแถว · &gt; 0) +
    /// VAT รวมของตารางสรุปตามกลุ่ม (<see cref="OcrLineVatMarks.ReadGroups"/> — ใบห้างพิมพ์ VAT ไว้ในตาราง
    /// ไม่มีป้ายบนแถว)</summary>
    public static IReadOnlyList<OcrPrintedAmount> VatAmounts(string? rawText)
    {
        var lines = Lines(rawText);
        var list = new List<OcrPrintedAmount>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (!VatLabel.IsMatch(line) || VatLabelNotAmount.IsMatch(line)) continue;
            var money = MoneyOn(line);
            if (money.Count == 0 || money[money.Count - 1] <= 0m) continue;
            // "VAT RATE 7.00" / "อัตราภาษี 7.00" — ตัวเลขคืออัตรา (ฝ่ายค้าน P3)
            if (RateWord.IsMatch(line) && money[money.Count - 1] == 7m) continue;
            list.Add(new OcrPrintedAmount(money[money.Count - 1], i, line.Trim()));
        }
        var table = OcrLineVatMarks.ReadGroups(rawText);
        if (table.Found && table.Vat > 0m && !list.Any(v => Math.Abs(v.Amount - table.Vat) <= ExactTol))
            list.Add(new OcrPrintedAmount(table.Vat, table.TotalLineNo, table.Evidence));
        return list;
    }

    /// <summary>
    /// กระดาษมีตัวเลข<b>มากกว่าหนึ่งสกุลเงิน</b> — ขั้นยึดยอด/แตกยอดถอยเป็น "ไม่รู้" (ห้ามตัดสินข้ามสกุล · ฝ่ายค้าน C2)
    /// <para>สัญญาณ: รหัส/สัญลักษณ์สกุลต่างประเทศ (USD/EUR/…/$/€) · คำว่าอัตราแลกเปลี่ยน · หรือบางแถวยอดเงินติดป้าย THB
    /// ขณะที่แถวยอด/VAT อื่นไม่ติด (ใบ "Total 1,070.00 … Total (THB) 36,380.00" — ชุดที่ไม่ติดป้ายคือสกุลอื่น) ·
    /// ใบไทยทั่วไปไม่พิมพ์ THB บนแถวยอด ⇒ ไม่เข้าข้อนี้</para>
    /// </summary>
    public static bool HasForeignCurrency(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText)) return false;
        if (ForeignCurrency.IsMatch(rawText) || ExchangeRateWords.IsMatch(rawText)) return true;
        var lines = Lines(rawText);
        var thbMoney = lines.Any(l => ThbCode.IsMatch(l) && MoneyOn(l).Count > 0);
        return thbMoney && lines.Any(l => !ThbCode.IsMatch(l) && AmountLabelLine.IsMatch(l) && MoneyOn(l).Count > 0);
    }

    private static readonly Regex ThbCode = new(@"(?<![A-Za-z])THB(?![A-Za-z])", Opt);
    private static readonly Regex ExchangeRateWords = new(@"exchange[ \t]*rate|rate[ \t]*of[ \t]*exchange|อัตราแลกเปลี่ยน", Opt);
    private static readonly Regex AmountLabelLine = new(
        @"(?<![A-Za-z])(?:total|amount|vat)(?![A-Za-z])|ภาษีมูลค่าเพิ่ม|รวม", Opt);

    /// <summary>แถวส่วนลดที่มีเงินจริง (ไม่รวมแถว "หลัง/ก่อนหักส่วนลด" · ไม่รวมแถวยอด 0)</summary>
    public static IReadOnlyList<OcrPrintedAmount> DiscountRows(string? rawText)
        => LabelledRows(rawText, DiscountLabel, DiscountNotAmount);

    /// <summary>แถวมัดจำที่มีเงินจริง (แถวฟอร์ม "หักเงินมัดจำ 0.00" ไม่ใช่หลักฐาน — RG-02)</summary>
    public static IReadOnlyList<OcrPrintedAmount> DepositRows(string? rawText)
        => LabelledRows(rawText, DepositLabel, null);

    private static IReadOnlyList<OcrPrintedAmount> LabelledRows(string? rawText, Regex label, Regex? notAmount)
    {
        var lines = Lines(rawText);
        var list = new List<OcrPrintedAmount>();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var m = label.Match(line);
            if (!m.Success) continue;
            if (notAmount != null && notAmount.IsMatch(line)) continue;
            var money = MoneyOn(line[(m.Index + m.Length)..]);
            if (money.Count == 0 || money[money.Count - 1] <= 0m) continue;
            list.Add(new OcrPrintedAmount(money[money.Count - 1], i, line.Trim()));
        }
        return list;
    }
}
