using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ส่วนลดท้ายบิลที่<b>พิมพ์อยู่บนกระดาษ</b> — <c>Amount = null</c> = กระดาษไม่บอก (ไม่ใช่ "ศูนย์")</summary>
/// <param name="Amount">ยอดส่วนลดเป็นบาท (บวกเสมอ — กระดาษจะพิมพ์ติดลบ/วงเล็บก็ตาม)</param>
/// <param name="FromTotalRow">มาจากแถว "รวมส่วนลด/ส่วนลดท้ายบิล" (สรุปทั้งใบ) ไม่ใช่แถวส่วนลดย่อย</param>
/// <param name="RowCount">จำนวนแถวส่วนลดที่มีเงินจริงบนกระดาษ</param>
/// <param name="Evidence">บรรทัดกระดาษที่อ่านได้ — ใส่ trace ให้คนตามรอยได้</param>
public sealed record OcrBillDiscountReading(decimal? Amount, bool FromTotalRow, int RowCount, string? Evidence);

/// <summary>
/// **อ่าน "ส่วนลดท้ายบิล" จากข้อความบนกระดาษ — ตัวอ่านตัวเดียวของเส้น OCR**
///
/// ═══ ที่มา (รอบ 190 ข้อ 9 ของเจ้าของ: "OCR ใบกำกับที่มีส่วนลดท้ายใบยังดึงมาไม่ถูก") ═══
/// <para>ตัวอ่านเดิมเป็น regex บรรทัดเดียวฝังใน <c>OcrService.EnrichFromRawText</c>:
/// <c>(?:ส่วนลด(?:รวม|การค้า)?|discount)\s*:?\s*(?:฿|บาท)?\s*([\d,]+(?:\.\d{1,2})?)</c>
/// แล้วหยิบ<b>ตัวแรกที่เจอทั้งหน้า</b> ⇒ ผิดบนกระดาษจริง 5 แบบ:</para>
/// <list type="number">
/// <item>"ส่วนลด 10% 150.00" → ได้ <b>10</b> (เลขของเปอร์เซ็นต์) ไม่ใช่ 150</item>
/// <item>"ยอดรวมหลังหักส่วนลด 900.00" → ได้ <b>900</b> (ยอดเงิน ไม่ใช่ส่วนลด)</item>
/// <item>"ส่วนลดท้ายบิล 150.00" / "ส่วนลดพิเศษ 150.00" → ไม่ตรงป้าย ⇒ <b>หาย</b></item>
/// <item>"Discount -150.00" / "(150.00)" (รูปแบบของเครื่อง POS) → ไม่ตรง ⇒ <b>หาย</b></item>
/// <item><c>\s*</c> ข้ามบรรทัด ⇒ หัวคอลัมน์ "ส่วนลด" ตามด้วยเลขลำดับแถวแรก "1" → ส่วนลด 1 บาท</item>
/// </list>
/// <para>ส่วนลดที่หาย/ผิด ⇒ <c>OcrLineReconciler</c> ตัดสินไม่ได้ (Ambiguous) หรือตัดสินผิด
/// ⇒ ยอดก่อน VAT ของเอกสาร ≠ กระดาษ ⇒ ฐานภาษีซื้อ §87 ผิดทั้งใบ</para>
///
/// ═══ กติกา ═══
/// <list type="bullet">
/// <item>อ่าน<b>ทีละบรรทัด</b> ตัวคั่นป้าย↔ตัวเลขเป็น <c>[ \t]</c> เท่านั้น (ไม่กลืนบรรทัดถัดไป) —
///   ยกเว้นบรรทัดป้ายที่ไม่มีตัวเลขเลย แล้ว<b>บรรทัดถัดไปเป็นตัวเลขล้วน</b> (layout ที่ engine
///   แยกป้าย/ค่าคนละบรรทัด)</item>
/// <item>แถวยอด 0 ไม่ใช่หลักฐาน (บทเรียน RG-02 — แถวฟอร์ม "ส่วนลด 0.00")</item>
/// <item>เปอร์เซ็นต์ไม่ใช่ยอดเงิน — ถ้ามีแต่ % ไม่มียอด = <b>ไม่รู้</b> (ห้ามคำนวณยอดเอง:
///   ไม่รู้ฐานของเปอร์เซ็นต์)</item>
/// <item>แถว "รวมส่วนลด/ส่วนลดท้ายบิล" ชนะแถวย่อย · หลายแถวย่อยไม่มีแถวรวม = ผลรวม</item>
/// <item><b>ตัวอ่านนี้แค่ "เห็นอะไรบนกระดาษ"</b> — ส่วนลดจะถูกใช้จริงก็ต่อเมื่อ
///   <c>OcrLineReconciler</c> พิสูจน์ด้วยยอดหัวใบแล้วว่าลงตัว (ค่าที่อ่านผิดจึงไม่กลายเป็น
///   ตัวเลขในเอกสารเงียบ ๆ แต่ตกเป็น <c>[Σ-GAP]</c> ให้คนดู)</item>
/// </list>
/// </summary>
public static class OcrBillDiscount
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>ป้ายส่วนลดทุกแบบ — จับ "ตำแหน่งจบป้าย" เพื่ออ่านเฉพาะตัวเลขที่อยู่<b>หลัง</b>ป้าย</summary>
    private static readonly Regex AnyLabel = new(
        @"ส่วนลด[ก-๙]*|\bdiscounts?\b|\bdisc\b\.?", Opt);

    /// <summary>ป้ายแถวสรุป "ส่วนลดทั้งใบ"</summary>
    private static readonly Regex TotalLabel = new(
        @"ส่วนลดรวม|รวมส่วนลด|ส่วนลดท้าย(?:บิล|ใบ)|ส่วนลดทั้ง(?:บิล|ใบ)|(?:total|bill|invoice|end)[ \t]*discount|discount[ \t]*total", Opt);

    /// <summary>บรรทัดที่มีคำว่าส่วนลดแต่ตัวเลขเป็น<b>ยอดเงินอื่น</b> — ห้ามอ่านเป็นส่วนลด</summary>
    private static readonly Regex NotDiscountRow = new(
        @"(?:หลัง|ก่อน)[ \t]*(?:หัก)?[ \t]*ส่วนลด|(?:after|before|less|net[ \t]+of)[ \t]+disc|ไม่มีส่วนลด|no[ \t]+discount", Opt);

    private static readonly Regex PercentToken = new(@"\d+(?:[.,]\d+)?[ \t]*%", Opt);

    /// <summary>ยอดเงิน: ต้องมีทศนิยม 2 ตำแหน่งหรือคอมมาหลักพัน หรือเป็นเลขเต็มที่ตามด้วย "บาท/฿"
    /// — กันการหยิบเลขจำนวนชิ้น/เลขลำดับมาเป็นส่วนลด</summary>
    private static readonly Regex MoneyToken = new(
        @"(?<![\d.,])(?<neg>[-−][ \t]*)?\(?(?<num>\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+\.\d{2}|\d+(?=[ \t]*(?:บาท|฿|thb)))\)?(?<trail>-)?(?![\d%])", Opt);

    private static readonly Regex PureMoneyLine = new(
        @"^[ \t]*[-−]?[ \t]*\(?(?:\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+\.\d{2})\)?-?[ \t]*(?:บาท|฿|thb)?[ \t]*$", Opt);

    /// <summary>อ่านส่วนลดท้ายบิลจากข้อความทั้งหน้า</summary>
    /// <param name="rawText">ข้อความดิบจาก engine</param>
    /// <param name="grandTotal">ยอดรวมบนหัวใบ (ถ้ารู้) — ส่วนลดที่ไม่น้อยกว่ายอดรวม = อ่านผิดแถว</param>
    public static OcrBillDiscountReading Read(string? rawText, decimal? grandTotal)
    {
        var none = new OcrBillDiscountReading(null, false, 0, null);
        if (string.IsNullOrWhiteSpace(rawText)) return none;

        var lines = rawText.Replace("\r", "").Split('\n');
        var rows = new List<(decimal Amount, bool IsTotal, int LineNo, string Text)>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var label = AnyLabel.Match(line);
            if (!label.Success) continue;
            if (NotDiscountRow.IsMatch(line)) continue;

            var after = line[(label.Index + label.Length)..];
            // ตัดเปอร์เซ็นต์ทิ้งก่อน — "10%" ไม่ใช่ยอดเงิน
            after = PercentToken.Replace(after, " ");
            decimal? amount = LastMoney(after);
            var evidence = line.Trim();
            // ป้ายอยู่บรรทัดหนึ่ง ค่าอยู่บรรทัดถัดไป (engine แยก cell) — รับเฉพาะเมื่อบรรทัดป้าย
            // ไม่มีตัวเลขใด ๆ หลังป้ายเลย และบรรทัดถัดไปเป็นตัวเลขล้วน (หัวคอลัมน์ตารางไม่ผ่านข้อนี้
            // เพราะบรรทัดถัดไปคือแถวรายการที่มีข้อความปน)
            if (amount is null && !after.Any(char.IsDigit) && i + 1 < lines.Length
                && PureMoneyLine.IsMatch(lines[i + 1]))
            {
                amount = LastMoney(lines[i + 1]);
                evidence = evidence + " / " + lines[i + 1].Trim();
            }
            if (amount is not > 0m) continue;                        // แถวยอด 0 ไม่ใช่หลักฐาน
            if (grandTotal is > 0m && amount.Value >= grandTotal.Value) continue;  // อ่านผิดแถว
            rows.Add((amount.Value, TotalLabel.IsMatch(line), i, evidence));
        }
        if (rows.Count == 0) return none;

        // แถวสรุปทั้งใบชนะ (ตัวสุดท้าย — แถวสรุปอยู่ท้ายใบ)
        for (var k = rows.Count - 1; k >= 0; k--)
            if (rows[k].IsTotal)
                return new OcrBillDiscountReading(rows[k].Amount, true, rows.Count, rows[k].Text);

        if (rows.Count == 1)
            return new OcrBillDiscountReading(rows[0].Amount, false, 1, rows[0].Text);

        // หลายแถวย่อยไม่มีแถวรวม: ถ้าเป็นยอดเดียวกันบนบรรทัดติดกัน = ป้ายสองภาษาของแถวเดียว
        // ("ส่วนลด 50.00" / "Discount 50.00") ไม่ใช่ส่วนลดสองก้อน
        decimal sum = 0m;
        var used = new List<string>();
        for (var k = 0; k < rows.Count; k++)
        {
            if (k > 0 && rows[k].Amount == rows[k - 1].Amount && rows[k].LineNo == rows[k - 1].LineNo + 1)
                continue;
            sum += rows[k].Amount;
            used.Add(rows[k].Text);
        }
        return new OcrBillDiscountReading(sum, false, used.Count, string.Join(" + ", used));
    }

    /// <summary>ยอดเงินตัวสุดท้ายในข้อความ (ค่าสัมบูรณ์) — null เมื่อไม่มี</summary>
    private static decimal? LastMoney(string text)
    {
        decimal? last = null;
        foreach (Match m in MoneyToken.Matches(text))
        {
            if (decimal.TryParse(m.Groups["num"].Value.Replace(",", ""), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var v))
                last = Math.Abs(v);
        }
        return last;
    }
}
