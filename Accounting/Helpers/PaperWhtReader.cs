using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>สิ่งที่<b>กระดาษพิมพ์ไว้</b>เรื่องภาษีหัก ณ ที่จ่าย</summary>
/// <param name="Amount">ยอดหักที่พิมพ์บนกระดาษ (null = กระดาษไม่บอก)</param>
/// <param name="RatePercent">อัตราที่พิมพ์บนกระดาษ (null = กระดาษไม่บอก)</param>
public readonly record struct PaperWht(decimal? Amount, decimal? RatePercent);

/// <summary>
/// **อ่านยอด/อัตราหัก ณ ที่จ่ายจากข้อความบนกระดาษ** (pure, ไม่มี I/O)
///
/// ═══ ทำไมต้องมี (ผลตรวจ 2026-09-06 · T2-02) ═══
/// ด่านตรวจของ <c>OcrConfidenceGateway</c> เขียนไว้ว่า "WhtAmount ≈ SubTotal × Rate/100"
/// — แต่ผู้เรียก<b>คำนวณ</b> <c>whtAmt = SubTotal × Rate/100</c> แล้วส่งค่านั้นเข้าไป
/// ให้ด่านตรวจ ⇒ ด่านเทียบสูตรกับ<b>ผลของสูตรตัวเอง</b> ⇒ **ผ่านทุกครั้ง ตลอดกาล**
/// = ด่านที่ไม่มีอยู่จริง (ญาติของบทเรียน "control ที่ไม่มีใครเรียก = ไม่มี control"
/// — ตัวนี้ถูกเรียกจริงแต่ถูกป้อนข้อมูลที่ทำให้มันไร้ความหมาย)
///
/// <para>ของที่ควรตรวจคือ <b>ยอดที่พิมพ์บนกระดาษ</b> เทียบกับสูตร — ซึ่งจับได้จริง
/// เช่น ผู้ขายคิด 3% จากยอด<b>รวม VAT</b> (ผิด — ต้องคิดจากยอดก่อน VAT) หรือ OCR
/// อ่านอัตราเป็น 5% ทั้งที่กระดาษเขียน 3%</para>
///
/// <para><b>ไม่มีบนกระดาษ = คืน null</b> ห้ามแต่งยอดขึ้นมาให้ด่านมีอะไรตรวจ</para>
/// </summary>
public static class PaperWhtReader
{
    /// <summary>ป้ายที่นำหน้ายอดหัก ณ ที่จ่ายบนใบไทย</summary>
    private static readonly Regex Label = new(
        @"(?:ภาษี\s*)?หัก\s*ณ\s*ที่\s*จ่าย|ภาษี\s*หัก|withholding\s*tax|\bWHT\b|\bW/?T\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>อัตรา % ที่พิมพ์ติดกับป้าย</summary>
    private static readonly Regex RatePattern = new(
        @"(?<r>\d{1,2}(?:\.\d{1,2})?)\s*%", RegexOptions.Compiled);

    /// <summary>จำนวนเงิน — ต้องมีทศนิยม 2 ตำแหน่ง หรือมีคอมมาคั่นหลักพัน
    /// (กันไปหยิบ "3" ของ "3%" หรือเลขลำดับรายการมาเป็นยอด)</summary>
    private static readonly Regex MoneyPattern = new(
        @"(?<!\d)(?<amt>\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+\.\d{2})(?!\d)", RegexOptions.Compiled);

    /// <summary>ระยะที่ยอมให้ยอดอยู่ห่างจากป้าย (ตัวอักษร) — ไกลกว่านี้คือเลขคนละเรื่อง</summary>
    private const int WindowChars = 60;

    /// <param name="rawText">ข้อความทั้งใบ (normalize แล้ว)</param>
    /// <param name="baseAmount">ฐานที่ควรใช้คำนวณ (ยอดก่อน VAT) — ใช้เป็นด่าน
    /// ความสมเหตุสมผล: ยอดหักต้องไม่เกินฐาน · ส่ง <c>null</c> ได้ถ้ายังไม่รู้</param>
    public static PaperWht Read(string? rawText, decimal? baseAmount = null)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return new(null, null);

        decimal? bestAmount = null, bestRate = null;
        foreach (Match label in Label.Matches(rawText))
        {
            var start = label.Index + label.Length;
            if (start >= rawText.Length) continue;
            var window = rawText.Substring(start, Math.Min(WindowChars, rawText.Length - start));
            // ตัดที่ขึ้นบรรทัดใหม่ **ตัวที่สอง** — ใบจำนวนมากพิมพ์ป้ายกับยอดคนละบรรทัด
            // แต่เกินสองบรรทัดไปแล้วคือรายการถัดไป ไม่ใช่ยอดของป้ายนี้
            var nl = window.IndexOf('\n');
            if (nl >= 0)
            {
                var nl2 = window.IndexOf('\n', nl + 1);
                if (nl2 >= 0) window = window[..nl2];
            }

            var rm = RatePattern.Match(window);
            if (rm.Success && decimal.TryParse(rm.Groups["r"].Value, NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var r) && r is > 0m and <= 30m)
                bestRate ??= r;

            // ยอด: หยิบตัวแรกที่ไม่ใช่ตัวเลขของอัตรา และผ่านด่านความสมเหตุสมผล
            foreach (Match mm in MoneyPattern.Matches(window))
            {
                if (rm.Success && mm.Index >= rm.Index && mm.Index < rm.Index + rm.Length) continue;
                if (!decimal.TryParse(mm.Groups["amt"].Value.Replace(",", ""), NumberStyles.Number,
                        CultureInfo.InvariantCulture, out var amt) || amt <= 0m) continue;
                // ยอดหักมากกว่าฐาน = อ่านผิดแน่ (หยิบยอดรวมมา) — ข้าม
                if (baseAmount is > 0m && amt > baseAmount.Value) continue;
                bestAmount ??= amt;
                break;
            }
            if (bestAmount.HasValue && bestRate.HasValue) break;
        }
        return new(bestAmount, bestRate);
    }
}
