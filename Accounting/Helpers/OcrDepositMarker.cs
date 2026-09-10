using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสิน “ใบนี้เป็นใบมัดจำ/จ่ายล่วงหน้าจริงไหม” — ตัวเดียวทั้งฝั่งซื้อและฝั่งขาย**
///
/// <para>ที่มา (สแกนจริง 2026-09-10 · ลักกี้ เวย์): ฟอร์มใบเสร็จพิมพ์แถว
/// “หักเงินมัดจำ 0.00” ไว้ทุกใบไม่ว่าจะมีมัดจำหรือไม่ — regex เดิมค้นคำว่า “มัดจำ”
/// บน**ข้อความทั้งหน้า** จึงติดธง [DEPOSIT-BUY] และลง Dr 11810 (สินทรัพย์) แทน
/// ค่าใช้จ่าย ทั้งที่ใบนี้คือใบซื้อของธรรมดา · เป็นบทเรียนเดิมของ
/// <c>ExpenseCategoryResolver</c> (“แถวยอด 0 ไม่ใช่หลักฐาน”) ที่ตัวตรวจมัดจำยังไม่ได้ใช้</para>
///
/// <para>กติกา 3 ข้อ (pure — มีเทสต์ด้วยข้อความจริง):
/// <list type="number">
/// <item>คำว่ามัดจำที่อยู่บนบรรทัดที่ขึ้นต้นด้วย **หัก/Less** = ใบสุดท้ายที่หักมัดจำเดิม
/// ⇒ ไม่ใช่ใบมัดจำ (ความหมายตรงข้าม)</item>
/// <item>คำว่ามัดจำต้องมี**จำนวนเงิน &gt; 0** บนบรรทัดเดียวกันหรือบรรทัดถัดไป — แถว 0.00
/// คือช่องว่างของฟอร์ม ไม่ใช่เหตุการณ์</item>
/// <item>หรือรายการสินค้าที่มีคำว่ามัดจำและ Amount &gt; 0</item>
/// </list>
/// คำยกเว้น (เงินประกัน/Security deposit) ยังตัดทิ้งทั้งใบเหมือนเดิม</para>
/// </summary>
public static class OcrDepositMarker
{
    /// <summary>คำที่บอกว่าเอกสารเป็น “มัดจำ/รับ-จ่ายล่วงหน้า” — “เงินประกัน” ไม่รวม
    /// (หลักประกันสัญญา คนละบัญชี)</summary>
    public static readonly Regex Keyword = new(
        @"เงินมัดจำ|ค่ามัดจำ|มัดจำ|เงินจอง|ค่าจอง|รับล่วงหน้า|เงินล่วงหน้า|ชำระล่วงหน้า"
        + @"|DEPOSIT|ADVANCE[ \t]*(?:PAYMENT|RECEIVED)|PAYMENT[ \t]*IN[ \t]*ADVANCE|PRE-?PAYMENT"
        + @"|DOWN[ \t]*PAYMENT|BOOKING[ \t]*FEE|RESERVATION[ \t]*FEE",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>เอกสารที่**ไม่ใช่**มัดจำแม้มีคำว่า DEPOSIT (เงินประกัน · ฝากธนาคาร · เงินฝากประจำ)</summary>
    public static readonly Regex Exclusion = new(
        @"เงินประกัน|SECURITY[ \t]*DEPOSIT|GUARANTEE[ \t]*DEPOSIT|RENTAL[ \t]*DEPOSIT"
        + @"|DAMAGE[ \t]*DEPOSIT|CASH[ \t]*DEPOSIT|DEPOSIT[ \t]*TO[ \t]*A/?C|FIXED[ \t]*DEPOSIT",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>บรรทัดที่ขึ้นต้นด้วย “หัก…” / “Less …” = การหักมัดจำเดิมออก ไม่ใช่การรับ/จ่ายมัดจำ</summary>
    private static readonly Regex NegationPrefix = new(
        @"^\s*(?:\(?-?\)?\s*)?(?:หัก|ลบ|LESS|MINUS|DEDUCT)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>จำนวนเงินบนบรรทัด — ตัวคั่นหลักพันด้วย , เท่านั้น ห้าม \s (regex_line_span_check)</summary>
    private static readonly Regex Money = new(@"(?<![\d.])(\d{1,3}(?:,\d{3})+|\d+)(?:\.(\d{1,2}))?(?![\d])",
        RegexOptions.Compiled);

    public sealed record Decision(bool IsDeposit, string Reason);

    /// <param name="rawText">ข้อความทั้งหน้าจาก OCR</param>
    /// <param name="items">รายการสินค้าที่สกัดได้ (คำอธิบาย, ยอด) — null ได้</param>
    public static Decision Decide(string? rawText, IEnumerable<(string? Description, decimal? Amount)>? items)
    {
        var text = rawText ?? string.Empty;
        if (text.Length == 0 && items == null) return new(false, "ไม่มีข้อความ");
        if (Exclusion.IsMatch(text)) return new(false, "พบคำยกเว้น (เงินประกัน/เงินฝาก) — ไม่ใช่มัดจำ");

        // (3) รายการที่มีคำว่ามัดจำและมีเงินจริง — หลักฐานชั้นเต็ม
        if (items != null)
        {
            foreach (var (desc, amount) in items)
            {
                if (amount is > 0m && !string.IsNullOrWhiteSpace(desc) && Keyword.IsMatch(desc)
                    && !NegationPrefix.IsMatch(desc))
                    return new(true, $"รายการ “{desc.Trim()}” ยอด {amount.Value:N2}");
            }
        }

        // (1)+(2) คำบนกระดาษ — ต้องไม่ใช่บรรทัด “หัก…” และต้องมีเงิน > 0 บนบรรทัดนั้นหรือถัดไป
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sawZeroOnly = false;
        var sawNegation = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!Keyword.IsMatch(line)) continue;
            if (NegationPrefix.IsMatch(line)) { sawNegation = true; continue; }

            var amt = FirstPositiveMoney(line) ?? (i + 1 < lines.Length ? FirstPositiveMoney(lines[i + 1]) : null);
            if (amt is > 0m)
                return new(true, $"บรรทัด “{line.Trim()}” มียอด {amt.Value:N2}");
            sawZeroOnly = true;
        }

        if (sawNegation && !sawZeroOnly)
            return new(false, "พบแต่บรรทัด “หัก…มัดจำ” — เป็นใบสุดท้ายที่หักมัดจำเดิม ไม่ใช่ใบมัดจำ");
        if (sawZeroOnly)
            return new(false, "คำว่ามัดจำอยู่บนแถวฟอร์มที่ยอด 0.00/ไม่มียอด — ไม่ใช่หลักฐานว่าใบนี้เป็นมัดจำ");
        return new(false, "ไม่พบคำว่ามัดจำ");
    }

    private static decimal? FirstPositiveMoney(string line)
    {
        foreach (Match m in Money.Matches(line))
        {
            var raw = m.Value.Replace(",", "");
            if (decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0m)
                return v;
        }
        return null;
    }
}
