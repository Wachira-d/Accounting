using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// รหัสสาขาสรรพากร 5 หลัก — **resolver กลางตัวเดียวของทั้งระบบ**
///
/// ที่มาของกฎ: ป.รัษฎากร §86/4 บังคับให้ใบกำกับภาษีระบุสถานประกอบการที่ออกใบ
/// และประกาศอธิบดีฯ ฉบับที่ 199 (ลว. 26 ธ.ค. 2556) กำหนดรูปแบบเป็นเลข 5 หลัก
/// โดย <c>00000</c> = สำนักงานใหญ่ · <c>00001</c> ขึ้นไป = สาขาที่ 00001, 00002, …
/// การพิมพ์บนเอกสารต้องเป็นคำว่า "สำนักงานใหญ่" หรือ "สาขาที่ {รหัส 5 หลัก}" ไม่ใช่เลขดิบลอย ๆ
///
/// <para>รอบ 193 (คำตัดสินเจ้าของข้อ 21 · DECISIONS.md): ป้ายสาขาพิมพ์รหัส **5 หลักเต็ม** — "สาขาที่ 00008" /
/// EN "Branch 00008" · เดิมตัดศูนย์นำหน้า ("สาขาที่ 8") ซึ่งต่างจากรหัสที่ผู้ซื้อต้องกรอกลงรายงานภาษีซื้อ
/// และต่างจากสลิป POS/ใบแจ้งหนี้ SaaS/รายงานภาษีที่พิมพ์ 5 หลักอยู่แล้ว ⇒ ทุกเอกสารตรงกันแล้ว</para>
///
/// ⚠️ ห้ามเขียนสูตรแปลงรหัส→ถ้อยคำเองซ้ำอีก — เดิมมีอยู่ 4 ที่และให้ผลไม่เหมือนกัน:
/// `PdfGenerationService.FormatBranch` (ตัวที่สมบูรณ์ที่สุด — ย้ายมาที่นี่แล้ว) ·
/// `PdfA3` ฝั่งผู้ขาย/ผู้ซื้อ (พิมพ์เลขดิบ "00003") · `DocumentBrandController` (สูตรของตัวเอง)
/// — defect class "resolver กลาง ห้ามคำนวณเอง" CLAUDE.md ข้อ 4.A · (ถ้อยคำ 5 หลัก "สาขาที่ 00003" เป็นรูปที่
/// เจ้าของเลือกในรอบ 193 — สิ่งที่ผิดของเดิมคือ "หลายสูตร" ไม่ใช่เลข 5 หลัก)
/// </summary>
public static class TaxBranchCode
{
    /// <summary>รหัสของสำนักงานใหญ่ตามประกาศอธิบดีฯ ฉบับที่ 199</summary>
    public const string HeadOffice = "00000";

    /// <summary>ชื่อสาขาที่แท้จริงหมายถึง "สำนักงานใหญ่" (ทุกสะกดที่พบบ่อย) — กัน
    /// การต่อท้ายชื่อ default ที่ขัดกับรหัสสาขาจริง เช่น "สาขาที่ 3 (สำนักงานใหญ่)"</summary>
    private static bool IsHeadOfficeName(string? name)
    {
        var n = name?.Trim();
        return n is "สำนักงานใหญ่" or "สนญ" or "สนญ." or "สำนักงานใหญ" or "Head Office" or "HeadOffice" or "HO";
    }

    /// <summary>
    /// เลขอารบิก 0-9 เท่านั้น — <c>char.IsDigit</c> รับเลขไทย (๑๒๓) ด้วย ซึ่งจะผ่าน
    /// ด่านนี้แล้วไปพังตอนแปลงเป็นตัวเลข/ส่งเข้า XML e-Tax
    /// </summary>
    private static bool IsAsciiDigits(string s)
    {
        foreach (var ch in s) if (ch < '0' || ch > '9') return false;
        return s.Length > 0;
    }

    /// <summary>ดึงเฉพาะตัวเลขออกจากค่าที่ผู้ใช้/ระบบเก่าเก็บไว้ (อาจมีขีด/ช่องว่างปน)</summary>
    private static string Digits(string? s)
        => new((s ?? "").Where(ch => ch >= '0' && ch <= '9').ToArray());

    /// <summary>ว่าง/ศูนย์ล้วน ถือเป็นสำนักงานใหญ่ — กิจการสาขาเดียวไม่ต้องกรอกอะไรเลย</summary>
    public static bool IsHeadOffice(string? code)
    {
        var d = Digits(code);
        return d.Length == 0 || d.TrimStart('0').Length == 0;
    }

    /// <summary>รหัส 5 หลักที่ใช้เก็บ/ส่งเข้า XML e-Tax — ว่าง/เพี้ยน → "00000"</summary>
    public static string Normalize(string? code)
    {
        var d = Digits(code);
        if (d.Length == 0) return HeadOffice;
        return d.Length >= 5 ? d[^5..] : d.PadLeft(5, '0');
    }

    /// <summary>
    /// จัดรูปรหัสให้เป็น 5 หลักแบบ **เข้มงวด** — ใช้เป็นด่านตอนบันทึกทะเบียนสาขา
    /// (ต่างจาก <see cref="Normalize"/> ที่ยอมรับทุกอย่างเพื่อ "แสดงผล")
    /// <c>"1"</c> → <c>"00001"</c>, ว่าง → null, รูปแบบผิด → false + ข้อความไทย
    /// </summary>
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

    /// <summary>คำที่ต้องพิมพ์บนเอกสาร — "สำนักงานใหญ่" / "สาขาที่ 00003"</summary>
    public static string Label(string? code, bool isEnglish = false)
        => LabelWithName(code, null, isEnglish);

    /// <summary>
    /// คำบนเอกสาร + ชื่อสาขาในวงเล็บถ้ามี — "สาขาที่ 00003 (เชียงใหม่)"
    ///
    /// ใช้กับทั้ง **ผู้ออกเอกสาร** (บริษัท/สาขาของเรา) และ **คู่ค้า** (ผู้ซื้อ/ผู้รับเงิน)
    /// บนใบกำกับ/ใบสำคัญ/50 ทวิ — ที่เดียวของทั้งระบบ
    /// </summary>
    public static string LabelWithName(string? code, string? name, bool isEnglish = false)
    {
        var digits = Digits(code);
        var n = name?.Trim();
        var codeIsHeadOffice = digits.Length == 0 || digits.TrimStart('0').Length == 0;

        if (!codeIsHeadOffice)
        {
            // รหัส 5 หลักเต็ม (คำตัดสินเจ้าของข้อ 21 รอบ 193) — ตรงกับรหัสที่ผู้ซื้อกรอก
            // ลงรายงานภาษีซื้อ · ใช้ Normalize ตัวเดียวกับที่เก็บ/ส่ง XML (เกิน 5 หลัก = 5 ตัวท้าย)
            var seq = Normalize(digits);
            var label = isEnglish ? $"Branch {seq}" : $"สาขาที่ {seq}";
            // แนบชื่อสาขาถ้ามี — ยกเว้นเมื่อชื่อเป็นคำว่า "สำนักงานใหญ่/สนญ"
            // (ค่า default ที่ฟอร์ม auto-เติม) ซึ่งขัดกับรหัสสาขาจริง
            if (!string.IsNullOrWhiteSpace(n) && !IsHeadOfficeName(n))
                label += $" ({n})";
            return label;
        }

        // รหัส = 00000/ว่าง → ปกติ "สำนักงานใหญ่". แต่ถ้า "ชื่อสาขา" เป็นเลขล้วนที่
        // ไม่ใช่ศูนย์ทั้งหมด (เช่น "00001") = ผู้ใช้กรอกเลขสาขาผิดช่อง (ใส่ใน "ชื่อ
        // สาขา" แทน "รหัสสาขา" ซึ่งฟอร์ม default ไว้ 00000) → ถือตามนั้น. เช็คเข้ม
        // ด้วย regex เลขล้วนเพื่อกัน false-positive ("สำนักงานใหญ่ ชั้น 5" ไม่โดนแปลง)
        if (!string.IsNullOrWhiteSpace(n)
            && Regex.IsMatch(n, @"^0*\d{1,5}$") && n.Any(ch => ch != '0'))
        {
            var nd = Normalize(n);
            return isEnglish ? $"Branch {nd}" : $"สาขาที่ {nd}";
        }
        return isEnglish ? "Head Office" : "สำนักงานใหญ่";
    }
}
