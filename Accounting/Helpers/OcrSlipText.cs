using System.Globalization;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **อ่านข้อความ OCR ของสลิปโอนเงินไทย — ตัวตัดสินเดียว** (pure · รอบ 260 คำตัดสิน 260-1-1 · ย้ายมาจากตัวแยกใน <c>SlipOcrAssistService</c> ที่เคยฝังใน service)
/// <para>ผู้เรียก: <c>SlipOcrAssistService</c> (สลิปค่าบริการ — badge "ยอดตรง") · <c>TesseractSlipImageReader</c> (สลิปที่ลูกค้าส่งในแชต LINE OA ของบริษัท) ·
/// <b>ไม่เรียก AI</b> (ตัวเลขล้วน — F2 ข้อ 3: ไม่รู้ = บอกว่าไม่รู้ ไม่เดา)</para>
/// <list type="bullet">
/// <item>ยอด: รูปแบบสลิปไทยมีทศนิยม 2 ตำแหน่งเสมอ (เลขบัญชี/เลขอ้างอิงไม่มีจุด) · ทิ้ง 0.00 (ค่าธรรมเนียม 0) · ปัด 2 ตำแหน่งแบบ AwayFromZero</item>
/// <item><see cref="Reading.Amount"/> ("ยอดที่มั่นใจ") มีค่าเฉพาะเมื่อ (ก) บรรทัดที่มีคำว่า จำนวน/ยอดเงิน/ยอดโอน/Amount (หรือบรรทัดถัดไป) ให้ยอดเดียว
/// หรือ (ข) ทั้งสลิปมียอดที่ไม่ใช่ศูนย์ยอดเดียว — อื่น ๆ = null (ผู้เรียกต้องให้คน/ลูกค้าเลือก ห้ามเลือกยอดสูงสุดเอง)</item>
/// <item>เลขอ้างอิง: ชุดตัวอักษรพิมพ์ใหญ่/ตัวเลข 10–25 ตัวที่มีตัวเลข (ยาวสุด) · วันที่: dd/mm/yy(yy) หรือ "12 พ.ย. 69" (พ.ศ. ⇒ ค.ศ.)</item>
/// </list>
/// </summary>
public static class OcrSlipText
{
    /// <param name="Amounts">ยอดที่ไม่ใช่ศูนย์ทั้งหมดบนสลิป (ไม่ซ้ำ · ลำดับที่พบ)</param>
    /// <param name="Amount">ยอดโอนที่มั่นใจ · null = ไม่มั่นใจ/ไม่มี</param>
    /// <param name="LooksLikeSlip">รอบ 260 ฝ่ายค้าน M3: ภาพมี "สัญญาณว่าเป็นสลิปโอนที่ทำรายการแล้ว" (เลขอ้างอิง หรือคำว่า สำเร็จ/successful/รหัสอ้างอิง/เลขที่รายการ) —
    /// ไม่มี ⇒ ภาพหน้าชำระ/QR ที่แสดงยอดก็มียอดเดียวได้ ผู้เรียกต้องให้ลูกค้ากดยืนยันก่อน (คำตัดสิน 260-1-7)</param>
    public sealed record Reading(IReadOnlyList<decimal> Amounts, decimal? Amount, DateTime? Date, string? Ref, bool LooksLikeSlip = false)
    {
        public static readonly Reading Empty = new(Array.Empty<decimal>(), null, null, null);
        public bool HasAnyAmount => Amounts.Count > 0;
    }

    private static readonly Regex AmountRx = new(@"(?<![\d.,])\d{1,3}(?:,\d{3})*\.\d{2}(?![\d])|(?<![\d.,])\d{4,9}\.\d{2}(?![\d])", RegexOptions.Compiled);
    private static readonly Regex RefRx = new(@"[A-Z0-9]{10,25}", RegexOptions.Compiled);
    private static readonly Regex DateRx = new(@"\b(\d{1,2})[\/\-.](\d{1,2})[\/\-.](\d{2,4})\b", RegexOptions.Compiled);
    private static readonly Regex ThaiDateRx = new(@"(\d{1,2})\s*(ม\.ค\.|ก\.พ\.|มี\.ค\.|เม\.ย\.|พ\.ค\.|มิ\.ย\.|ก\.ค\.|ส\.ค\.|ก\.ย\.|ต\.ค\.|พ\.ย\.|ธ\.ค\.)\s*(\d{2,4})", RegexOptions.Compiled);
    private static readonly Regex AmountKeyword = new(@"จำนวนเงิน|จำนวน|ยอดเงิน|ยอดโอน|โอนเงิน|amount", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SlipKeyword = new(@"สำเร็จ|successful|success|รหัสอ้างอิง|เลขที่รายการ|เลขอ้างอิง|transaction\s*(id|no|ref)|ref\.?\s*no", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] ThaiMonths = { "ม.ค.", "ก.พ.", "มี.ค.", "เม.ย.", "พ.ค.", "มิ.ย.", "ก.ค.", "ส.ค.", "ก.ย.", "ต.ค.", "พ.ย.", "ธ.ค." };

    /// <summary>ยอดเงินทุกตัวในข้อความ (ไม่ซ้ำ · ไม่ใช่ศูนย์ · ปัด 2 ตำแหน่ง AwayFromZero)</summary>
    public static IReadOnlyList<decimal> Amounts(string? text)
    {
        var list = new List<decimal>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (Match m in AmountRx.Matches(text))
        {
            if (!decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) continue;
            d = Math.Round(d, 2, MidpointRounding.AwayFromZero);
            if (d <= 0m || list.Contains(d)) continue;
            list.Add(d);
        }
        return list;
    }

    /// <param name="nowYear">ปี ค.ศ. ปัจจุบัน (ตีความปีสองหลัก — <see cref="TwoDigitYear"/>) · null = นาฬิกาเครื่อง</param>
    public static Reading Parse(string? text, int? nowYear = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return Reading.Empty;
        var all = Amounts(text);
        var reference = ExtractRef(text);
        return new Reading(all, ConfidentAmount(text, all), ExtractDate(text, nowYear), reference,
            LooksLikeSlip: reference != null || SlipKeyword.IsMatch(text));
    }

    /// <summary>รอบ 260 (ฝ่ายค้าน L7): badge "ยอดตรงกับที่แจ้ง" ของสลิปค่าบริการ (<c>SlipOcrAssistService</c>) — ยอดที่แจ้งอยู่บนสลิป (± 0.01) ⇒ (ยอดที่แจ้ง, true) ·
    /// ไม่มี ⇒ (ยอดสูงสุด, false) · ไม่มียอดเลย ⇒ (null, null) — พฤติกรรมเดิมของ service ย้ายมาให้เทสต์ล็อกได้</summary>
    public static (decimal? Chosen, bool? Matches) BadgeAmount(IReadOnlyList<decimal> amounts, decimal expected)
    {
        if (amounts.Count == 0) return (null, null);
        return amounts.Any(a => Math.Abs(a - expected) < 0.01m) ? (expected, true) : (amounts.Max(), false);
    }

    /// <summary>ปีสองหลักบนสลิป — ตัวตีความเดียวของทั้งรูป dd/mm/yy และ "12 พ.ย. 69" (ฝ่ายค้าน L8): มากกว่า (ปี ค.ศ. ปัจจุบัน % 100) + 10 ⇒ พ.ศ. (25yy − 543) ·
    /// อื่น ๆ ⇒ ค.ศ. 20yy (ปี 2026: "69" = 2569 = 2026 · "26" = 2026)</summary>
    private static int TwoDigitYear(int yy, int nowYear)
        => yy > (nowYear % 100) + 10 ? 2500 + yy - 543 : 2000 + yy;

    private static decimal? ConfidentAmount(string text, IReadOnlyList<decimal> all)
    {
        if (all.Count == 0) return null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var keyed = new List<decimal>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!AmountKeyword.IsMatch(lines[i])) continue;
            var onLine = Amounts(lines[i]);
            if (onLine.Count == 0 && i + 1 < lines.Length) onLine = Amounts(lines[i + 1]);
            foreach (var a in onLine) if (!keyed.Contains(a)) keyed.Add(a);
        }
        if (keyed.Count == 1) return keyed[0];
        if (keyed.Count > 1) return null;            // คำว่า "จำนวน" ชี้หลายยอด — ไม่เดา
        return all.Count == 1 ? all[0] : null;
    }

    /// <summary>เลขอ้างอิงรายการ (ชุดตัวอักษร/ตัวเลขยาวสุดที่มีตัวเลข · 10–25 ตัว) · ไม่พบ = null</summary>
    private static string? ExtractRef(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return RefRx.Matches(text).Select(m => m.Value)
            .Where(v => v.Any(char.IsDigit) && v.Length >= 10)
            .OrderByDescending(v => v.Length).FirstOrDefault();
    }

    /// <summary>วันที่บนสลิป (UTC เที่ยงคืน) — dd/mm/yy(yy) ก่อน แล้ว "12 พ.ย. 69" · พ.ศ. ⇒ ค.ศ. · ไม่พบ/ผิดรูป = null</summary>
    public static DateTime? ExtractDate(string? text, int? nowYear = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var year = nowYear ?? DateTime.UtcNow.Year;
        var m = DateRx.Match(text);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var d) && int.TryParse(m.Groups[2].Value, out var mo) && int.TryParse(m.Groups[3].Value, out var y))
        {
            if (y < 100) y = TwoDigitYear(y, year);
            else if (y > 2400) y -= 543;      // พ.ศ. → ค.ศ.
            if (Build(y, mo, d) is DateTime dt) return dt;
        }
        var t = ThaiDateRx.Match(text);
        if (t.Success && int.TryParse(t.Groups[1].Value, out var td) && int.TryParse(t.Groups[3].Value, out var ty))
        {
            var month = Array.IndexOf(ThaiMonths, t.Groups[2].Value) + 1;
            if (ty < 100) ty = TwoDigitYear(ty, year);
            else if (ty > 2400) ty -= 543;
            return Build(ty, month, td);
        }
        return null;
    }

    private static DateTime? Build(int y, int mo, int d)
    {
        if (d is < 1 or > 31 || mo is < 1 or > 12 || y is < 2000 or > 2100) return null;
        if (d > DateTime.DaysInMonth(y, mo)) return null;
        return new DateTime(y, mo, d, 0, 0, 0, DateTimeKind.Utc);
    }
}
