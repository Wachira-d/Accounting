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
        // e-Tax XML: ชื่อแท็ก/ชื่อช่องของแม่แบบไม่ใช่คำบนกระดาษ — ตัดสินจากโครงสร้างที่ผู้ออกลงนาม (ดู DecideEtaxXml)
        if (OcrEtaxXmlText.IsEtaxXml(text)) return DecideEtaxXml(text, items);
        if (Exclusion.IsMatch(text)) return new(false, "พบคำยกเว้น (เงินประกัน/เงินฝาก) — ไม่ใช่มัดจำ");

        // (3) รายการที่มีคำว่ามัดจำและมีเงินจริง — หลักฐานชั้นเต็ม
        var itemHit = DepositItem(items);
        if (itemHit != null) return itemHit;

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

    /// <summary>กติกา (3) — รายการที่มีคำว่ามัดจำ (ไม่ใช่ "หัก…") และยอด &gt; 0 · null = ไม่พบ</summary>
    private static Decision? DepositItem(IEnumerable<(string? Description, decimal? Amount)>? items)
    {
        if (items == null) return null;
        foreach (var (desc, amount) in items)
        {
            if (amount is > 0m && !string.IsNullOrWhiteSpace(desc) && Keyword.IsMatch(desc)
                && !NegationPrefix.IsMatch(desc))
                return new(true, $"รายการ “{desc.Trim()}” ยอด {amount.Value:N2}");
        }
        return null;
    }

    private static readonly Regex XmlExchangedDocument = new(
        @"<(?:\w+:)?ExchangedDocument>([\s\S]*?)</(?:\w+:)?ExchangedDocument>", RegexOptions.Compiled);
    private static readonly Regex XmlName = new(@"<(?:\w+:)?Name>([^<]*)</(?:\w+:)?Name>", RegexOptions.Compiled);
    private static readonly Regex XmlPurpose = new(@"<(?:\w+:)?Purpose>([^<]*)</(?:\w+:)?Purpose>", RegexOptions.Compiled);
    private static readonly Regex XmlAllowance = new(
        @"<(?:\w+:)?SpecifiedTradeAllowanceCharge>([\s\S]*?)</(?:\w+:)?SpecifiedTradeAllowanceCharge>", RegexOptions.Compiled);
    private static readonly Regex XmlReason = new(@"<(?:\w+:)?Reason>([^<]*)</(?:\w+:)?Reason>", RegexOptions.Compiled);
    private static readonly Regex XmlActualAmount = new(
        @"<(?:\w+:)?ActualAmount[^>]*>([^<]*)</(?:\w+:)?ActualAmount>", RegexOptions.Compiled);

    /// <summary>
    /// **e-Tax XML (ETDA)** — ตัดสินจากช่องที่ผู้ออกลงนาม ไม่ใช่จากคำที่ปรากฏในไฟล์
    ///
    /// <para>ที่มา (สแกนจริง f1690d11 · ซีอาร์ซี ไทวัสดุ SRCIE26100075384): แม่แบบ XML ของผู้ขายมี
    /// <c>SpecifiedTradeAllowanceCharge</c> “หักเงินมัดจำ” ยอด 0.00 และช่อง <c>DepositAllowance…1..3</c> ทุกใบ ⇒
    /// ตัวอ่านแบบข้อความเห็น “DepositAllowanceChargeInd<b>1</b>” = คำว่า DEPOSIT + เงิน 1 บาทบนบรรทัดเดียวกัน
    /// ⇒ ใบซื้อแอร์/อุปกรณ์ 3,201 บาทติด [DEPOSIT-BUY] และลง Dr 11810</para>
    ///
    /// <para>ใบมัดจำจริงบน e-Tax คือ (ก) รายการที่เป็นมัดจำและมียอด หรือ (ข) ชื่อเอกสาร/วัตถุประสงค์ที่ระบุมัดจำ ·
    /// “หักเงินมัดจำ” ในส่วนลดระดับหัวใบ = ใบสุดท้ายที่หักมัดจำเดิม (ความหมายตรงข้าม) ไม่ว่ายอดเท่าไร</para>
    /// </summary>
    private static Decision DecideEtaxXml(string xml, IEnumerable<(string? Description, decimal? Amount)>? items)
    {
        var content = OcrEtaxXmlText.ContentOnly(xml);
        if (Exclusion.IsMatch(content)) return new(false, "พบคำยกเว้น (เงินประกัน/เงินฝาก) — ไม่ใช่มัดจำ");

        var itemHit = DepositItem(items);
        if (itemHit != null) return itemHit;

        var head = XmlExchangedDocument.Match(xml);
        if (head.Success)
        {
            foreach (var re in new[] { XmlName, XmlPurpose })
            {
                var m = re.Match(head.Groups[1].Value);
                if (!m.Success) continue;
                var v = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                if (Keyword.IsMatch(v) && !NegationPrefix.IsMatch(v) && !Exclusion.IsMatch(v))
                    return new(true, $"ชื่อเอกสาร e-Tax “{v}” ระบุมัดจำ/รับล่วงหน้า");
            }
        }

        var deducted = 0m;
        var sawTemplateZero = false;
        foreach (Match a in XmlAllowance.Matches(xml))
        {
            var reason = XmlReason.Match(a.Groups[1].Value);
            if (!reason.Success || !Keyword.IsMatch(reason.Groups[1].Value)) continue;
            var amtMatch = XmlActualAmount.Match(a.Groups[1].Value);
            if (amtMatch.Success
                && decimal.TryParse(amtMatch.Groups[1].Value.Trim(), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var amt)
                && amt > 0m)
                deducted += amt;
            else
                sawTemplateZero = true;
        }
        if (deducted > 0m)
            return new(false, $"e-Tax XML ระบุ “หักเงินมัดจำ” {deducted:N2} — เป็นใบที่หักมัดจำเดิมออก ไม่ใช่ใบมัดจำ");
        if (sawTemplateZero)
            return new(false, "e-Tax XML: แม่แบบของผู้ขายตั้งแถว “หักเงินมัดจำ” ยอด 0.00 ไว้ — ค่าตั้งต้นของแม่แบบ ไม่ใช่เหตุการณ์");
        if (Keyword.IsMatch(content))
            return new(false, "e-Tax XML: คำว่ามัดจำไม่ได้อยู่ในชื่อเอกสารหรือรายการที่มียอด — ไม่ถือเป็นใบมัดจำ");
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
