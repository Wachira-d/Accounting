using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ผลการรวม "เล่มที่ + เลขที่" — <c>Changed = false</c> เมื่อไม่ต้องแก้อะไร</summary>
/// <param name="DocumentNumber">เลขที่เอกสารหลังรวม (หรือค่าเดิม)</param>
/// <param name="Changed">เปลี่ยนจากค่าเดิมไหม</param>
/// <param name="Reason">เหตุผลภาษาไทยสำหรับ ReasoningTrace (null เมื่อไม่เปลี่ยน)</param>
public readonly record struct OcrBookSerialResult(string? DocumentNumber, bool Changed, string? Reason);

/// <summary>
/// **ใบที่มี “เล่มที่” — เลขที่เอกสารคือ <c>&lt;เล่ม&gt;/&lt;เลขที่&gt;</c>**
///
/// <para>═══ ที่มา (สแกนจริง 2026-09-24 · ใบ B Radisson) ═══ กระดาษพิมพ์
/// “เล่มที่ BOOK NO. 066 … เลขที่ SERIAL NO. 3267” — engine คืนเลขที่ = <c>3267</c> ·
/// เจ้าของ: “เอกสารที่มีเล่มที่ ต้องเอาเล่มที่ขึ้นก่อนเลขที่” ⇒ <c>066/3267</c>
/// เหตุผลทางบัญชี: ใบเสร็จ/ใบกำกับแบบเล่มรันเลขที่<b>ใหม่ทุกเล่ม</b> (เล่ม 065 ก็มีเลขที่ 3267
/// ได้) ⇒ เลขที่เดี่ยว ๆ ไม่ใช่ตัวระบุใบ · ตามใบไม่เจอ · ด่านกันสแกนซ้ำ (เลขที่+ยอด) ชนข้ามเล่ม ·
/// รายงานภาษีซื้อ §87 ต้องอ้างเล่ม/เลขที่ให้ตามใบจริงได้</para>
///
/// <para>═══ ขอบเขตที่ตั้งใจแคบ ═══ รวม<b>เฉพาะ</b>เมื่อ (ก) กระดาษมีป้าย “เล่มที่/Book No.”
/// ตามด้วยตัวเลข และ (ข) เลขที่ที่ engine ให้มา<b>คือ</b>เลขที่ที่อยู่คู่กับเล่มนั้นบนกระดาษ
/// (หรือว่าง/เป็นเลขเล่มเอง) · ใบที่ไม่มีเล่มที่ (ใบ A <c>BA2609-569</c>) ห้ามแตะ · เลขที่ที่มีเล่ม
/// นำหน้าอยู่แล้ว ห้ามแตะ · พบหลายเล่มบนใบเดียวแล้วเลขที่ไม่ตรงคู่ไหน = ไม่รู้ → ไม่แตะ (G3)</para>
/// </summary>
public static class OcrBookSerial
{
    // ตัวคั่นเป็น [ \t] เท่านั้น — \s ครอบ \n แล้วเลขต้นบรรทัดถัดไปจะกลายเป็นเลขเล่ม
    // (บทเรียน regex_line_span_check)
    private static readonly Regex BookRx = new(
        @"(?:เล่มที่|เล่ม[ \t]*ที|\bBOOK[ \t]*NO\b\.?|\bBOOK[ \t]*#)[ \t]*[:：.]?[ \t]*(\d{1,6})(?![\d/])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ป้ายเลขที่ที่คู่กับเล่ม — “เลขที่” (ไม่ตามด้วยคำอื่นที่บอกว่าเป็นเลขอื่น) · “Serial No.” ·
    /// “No.” เดี่ยว (ไม่นับ “Tel No.”/“Fax No.” — คำละตินนำหน้า = เลขอื่น)</summary>
    private static readonly Regex SerialRx = new(
        @"(?:เลขที่(?!ใบ|ผู้|ประจำ|บัญชี|สาขา)|\bSERIAL[ \t]*NO\b\.?|(?<![A-Za-z]|[A-Za-z][ \t])NO\.)[ \t]*[:：]?[ \t]*(\d{1,10})(?![\d/])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>ระยะที่ยอมให้ป้ายเลขที่อยู่ห่างจากเลขเล่ม (ตัวอักษร) — บรรทัดเดียวกันหรือบรรทัดถัดไป
    /// บนแบบฟอร์มเล่ม · กว้างกว่านี้เริ่มไปคว้า “เลขที่” ของที่อยู่</summary>
    public const int MaxPairDistance = 90;

    /// <summary>หาคู่ (เล่ม, เลขที่) บนกระดาษ — เลขที่ต้องอยู่<b>หลัง</b>เล่มภายในบรรทัดเดียวกัน
    /// หรือบรรทัดถัดไป และห่างไม่เกิน <see cref="MaxPairDistance"/></summary>
    internal static IReadOnlyList<(string Book, string Serial)> FindPairs(string? rawText)
    {
        var pairs = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(rawText)) return pairs;
        foreach (Match b in BookRx.Matches(rawText))
        {
            var from = b.Index + b.Length;
            var limit = Math.Min(rawText.Length, from + MaxPairDistance);
            // ไม่เกินหนึ่งบรรทัดถัดไป
            var nl1 = rawText.IndexOf('\n', from);
            if (nl1 >= 0 && nl1 < limit)
            {
                var nl2 = rawText.IndexOf('\n', nl1 + 1);
                if (nl2 >= 0 && nl2 < limit) limit = nl2;
            }
            var window = rawText[from..limit];
            var s = SerialRx.Match(window);
            if (s.Success) pairs.Add((b.Groups[1].Value, s.Groups[1].Value));
        }
        return pairs;
    }

    /// <summary>รวมเล่ม + เลขที่ เป็น <c>เล่ม/เลขที่</c> ตามกติกาใน doc ของคลาส</summary>
    /// <param name="documentNumber">เลขที่ที่ engine/ชั้นก่อนหน้าให้มา (ว่างได้)</param>
    /// <param name="rawText">ข้อความทั้งหน้า</param>
    public static OcrBookSerialResult Combine(string? documentNumber, string? rawText)
    {
        var doc = documentNumber?.Trim() ?? string.Empty;
        var pairs = FindPairs(rawText);
        if (pairs.Count == 0) return new(documentNumber, false, null);

        // เลขที่ที่มีเล่มนำหน้าอยู่แล้ว (“066/3267” · “066-3267”) — ไม่แตะ
        if (!string.IsNullOrEmpty(doc)
            && pairs.Any(p => doc.StartsWith(p.Book + "/", StringComparison.Ordinal)
                           || doc.StartsWith(p.Book + "-", StringComparison.Ordinal)))
            return new(documentNumber, false, null);

        (string Book, string Serial)? pick = null;
        if (string.IsNullOrEmpty(doc))
        {
            if (pairs.Select(p => (p.Book, p.Serial)).Distinct().Count() == 1) pick = pairs[0];
        }
        else
        {
            var bySerial = pairs.Where(p => SameNumber(p.Serial, doc)).Distinct().ToList();
            var byBook = pairs.Where(p => SameNumber(p.Book, doc)).Distinct().ToList();
            if (bySerial.Count == 1) pick = bySerial[0];
            else if (bySerial.Count == 0 && byBook.Count == 1) pick = byBook[0];
        }
        if (pick is not { } p0) return new(documentNumber, false, null);

        var combined = $"{p0.Book}/{p0.Serial}";
        var why = string.IsNullOrEmpty(doc)
            ? $"เลขที่ว่าง แต่กระดาษพิมพ์ เล่มที่ {p0.Book} เลขที่ {p0.Serial} → {combined}"
            : $"ใบแบบเล่ม: เลขที่ “{doc}” อยู่ในเล่มที่ {p0.Book} → {combined} (เลขที่รันใหม่ทุกเล่ม — ต้องมีเล่มจึงระบุใบได้)";
        return new(combined, true, why);
    }

    /// <summary>ตัวเลขเดียวกันไหม (ยอมศูนย์นำหน้าต่างกัน “0066” = “66”) — ต้องเป็นตัวเลขล้วนทั้งคู่</summary>
    private static bool SameNumber(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (!a.All(char.IsAsciiDigit) || !b.All(char.IsAsciiDigit)) return false;
        var ta = a.TrimStart('0'); var tb = b.TrimStart('0');
        return ta == tb;
    }
}
