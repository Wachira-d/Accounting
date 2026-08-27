namespace Accounting.Helpers;

/// <summary>
/// รหัสสาขาสรรพากร 5 หลัก — **resolver กลางตัวเดียวของทั้งระบบ**
///
/// ที่มาของกฎ: ป.รัษฎากร §86/4 บังคับให้ใบกำกับภาษีระบุสถานประกอบการที่ออกใบ
/// และประกาศอธิบดีฯ ฉบับที่ 199 (ลว. 26 ธ.ค. 2556) กำหนดรูปแบบเป็นเลข 5 หลัก
/// โดย <c>00000</c> = สำนักงานใหญ่ · <c>00001</c> ขึ้นไป = สาขาที่ 1, 2, …
/// การพิมพ์บนเอกสารต้องเป็นคำว่า "สำนักงานใหญ่" หรือ "สาขาที่ {n}" ไม่ใช่เลขดิบ
///
/// ⚠️ ห้ามเขียนสูตร <c>code == "00000" ? "สำนักงานใหญ่" : ...</c> เองซ้ำอีก —
/// เดิมมีอยู่ 3 ที่ (DocumentBrandController, PdfA3 ฝั่งผู้ขาย, PdfA3 ฝั่งผู้ซื้อ)
/// ซึ่งให้ผลไม่เหมือนกัน (บางที่โชว์เลขดิบ "00003" แทน "สาขาที่ 3") — defect class
/// "resolver กลาง ห้ามคำนวณเอง" ใน CLAUDE.md ข้อ 4.A
/// </summary>
public static class TaxBranchCode
{
    /// <summary>รหัสของสำนักงานใหญ่ตามประกาศอธิบดีฯ ฉบับที่ 199</summary>
    public const string HeadOffice = "00000";

    /// <summary>
    /// เลขอารบิก 0-9 เท่านั้น — <c>char.IsDigit</c> รับเลขไทย (๑๒๓) ด้วย ซึ่งจะผ่าน
    /// ด่านนี้แล้วไปพังตอนแปลงเป็นตัวเลข/ส่งเข้า XML e-Tax
    /// </summary>
    private static bool IsAsciiDigits(string s)
    {
        foreach (var ch in s) if (ch < '0' || ch > '9') return false;
        return s.Length > 0;
    }

    /// <summary>ว่าง/null ถือเป็นสำนักงานใหญ่ — กิจการสาขาเดียวไม่ต้องกรอกอะไรเลย</summary>
    public static bool IsHeadOffice(string? code)
    {
        var c = (code ?? "").Trim();
        return c.Length == 0 || c.TrimStart('0').Length == 0;
    }

    /// <summary>
    /// จัดรูปรหัสให้เป็น 5 หลัก — <c>"1"</c> → <c>"00001"</c>, ว่าง → null
    /// </summary>
    /// <returns>true = ใช้ได้ (<paramref name="code"/> คือค่าที่จัดรูปแล้ว หรือ null ถ้าเว้นว่าง)</returns>
    public static bool TryNormalize(string? raw, out string? code, out string? error)
    {
        code = null;
        error = null;

        var c = (raw ?? "").Trim();
        if (c.Length == 0) return true;              // ไม่กรอก = ไม่ระบุ (ไม่ใช่ error)

        if (c.Length < 5 && IsAsciiDigits(c))
            c = c.PadLeft(5, '0');                   // พิมพ์ "3" → "00003"

        if (c.Length != 5 || !IsAsciiDigits(c))
        {
            error = $"รหัสสาขาสรรพากร \"{raw?.Trim()}\" ไม่ถูกต้อง — ต้องเป็นตัวเลข 5 หลัก "
                  + "(สำนักงานใหญ่ = 00000, สาขาที่ 1 = 00001) ตามประกาศอธิบดีฯ ฉบับที่ 199";
            return false;
        }

        code = c;
        return true;
    }

    /// <summary>คำที่ต้องพิมพ์บนเอกสาร — "สำนักงานใหญ่" / "สาขาที่ 3"</summary>
    public static string Label(string? code, bool isEnglish = false)
    {
        if (IsHeadOffice(code))
            return isEnglish ? "Head Office" : "สำนักงานใหญ่";

        var c = (code ?? "").Trim();
        // ตัดศูนย์นำหน้าเอง ไม่ผ่าน int.Parse — กัน culture ที่แปลงเลขไทยให้โดยไม่ตั้งใจ
        // เลขที่จัดรูปไม่ได้ (ข้อมูลเก่าเพี้ยน) → โชว์ตามที่เก็บไว้ ดีกว่าโชว์ผิดเป็นสำนักงานใหญ่
        var n = IsAsciiDigits(c) ? c.TrimStart('0') : c;
        return isEnglish ? $"Branch {n}" : $"สาขาที่ {n}";
    }

    /// <summary>คำบนเอกสาร + ชื่อสาขา (ถ้ามี) — "สาขาที่ 3 เชียงใหม่"</summary>
    public static string LabelWithName(string? code, string? name, bool isEnglish = false)
    {
        var label = Label(code, isEnglish);
        var n = (name ?? "").Trim();
        return n.Length == 0 || IsHeadOffice(code) ? label : $"{label} {n}";
    }
}
