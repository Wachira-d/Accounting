using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **ค่าหนึ่งช่องที่ปลอดภัยพอจะเขียนลงไฟล์ CSV ที่ผู้ใช้จะเปิดด้วย Excel/Sheets**
/// — ตัวหนีตัวเดียวของระบบ (กฎเหล็ก #4 C: escape/canonical function ต้องมีตัวเดียว)
///
/// ═══ ทำไมการหนีแค่ <c>,</c> <c>"</c> <c>\n</c> ไม่พอ ═══
/// <para>ไฟล์ที่เราส่งออกมีข้อมูลที่<b>ผู้ใช้ปลายทาง/พาร์ตเนอร์/OCR เป็นคนคุม</b>
/// (ชื่อคู่ค้า · คำอธิบายบรรทัด · หมายเหตุ). Excel และ Google Sheets ตีความช่องที่
/// ขึ้นต้นด้วย <c>= + - @</c> เป็น<b>สูตร</b> และยังรองรับ DDE รูปแบบ
/// <c>=cmd|'/c calc'!A0</c> ⇒ คู่ค้าชื่อ <c>=HYPERLINK("http://…"&amp;A1,"คลิก")</c>
/// ที่ sync เข้ามา กลายเป็นสูตรที่รันบนเครื่องนักบัญชีที่เปิดไฟล์ของเราเอง
/// (CSV injection / formula injection — OWASP)</para>
///
/// <para>วิธีที่ใช้: ใส่ <b>อัญประกาศเดี่ยวนำหน้า</b> (<c>'</c>) ซึ่งทั้ง Excel และ
/// Sheets ตีความว่า "บังคับให้เป็นข้อความ" แล้ว<b>ไม่แสดงตัวอัญประกาศนั้น</b>
/// ⇒ คนอ่านเห็นข้อความเดิม แต่โปรแกรมไม่คำนวณ. ตั้งใจ<b>ไม่</b>ลบอักขระทิ้ง
/// เพราะชื่อคู่ค้าที่ขึ้นต้นด้วย <c>-</c> (เช่น "-ร้านลุงหมี-") มีจริง และการ
/// ลบทำให้ข้อมูลที่ส่งออกไม่ตรงกับที่เก็บ</para>
///
/// <para>ยังหนี <c>\r</c> ด้วย (ของเดิมดูแค่ <c>\n</c>) — ข้อความที่มี CR เดี่ยว ๆ
/// จาก OCR/ระบบเก่า ทำให้ไฟล์ CSV แตกแถวกลางคัน</para>
/// </summary>
public static class CsvFieldSafety
{
    /// <summary>อักขระที่ Excel/Sheets ถือเป็นจุดเริ่มของสูตร</summary>
    public static readonly char[] FormulaLeadChars = { '=', '+', '-', '@' };

    /// <summary>ช่องนี้จะถูก Excel/Sheets ตีความเป็นสูตรไหม
    /// (ตรวจหลังตัดช่องว่าง/ตัวควบคุมนำหน้า — <c>"\t=cmd"</c> ก็ยังเป็นสูตร)
    ///
    /// <para><c>private</c> โดยตั้งใจ: ผู้เรียกต้องใช้ <see cref="Escape"/>/<see cref="Row"/>
    /// เสมอ — การเปิดให้เรียกเดี่ยวจะเกิดเส้นที่ "ตรวจแล้วแต่ลืมหนี"</para></summary>
    private static bool LooksLikeFormula(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var i = 0;
        while (i < value.Length && (value[i] == ' ' || value[i] == '\t'
               || value[i] == '\r' || value[i] == '\n')) i++;
        if (i >= value.Length) return false;
        return Array.IndexOf(FormulaLeadChars, value[i]) >= 0;
    }

    /// <summary>ค่าที่ "ปลอดสูตร" — เติม <c>'</c> นำหน้าเมื่อจำเป็น ไม่แตะค่าอื่น
    /// (<c>private</c> — เข้าถึงผ่าน <see cref="Escape"/> เท่านั้น)</summary>
    private static string Neutralize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        return LooksLikeFormula(value) ? "'" + value : value;
    }

    /// <summary>ค่าหนึ่งช่องพร้อมเขียนลง CSV — กันสูตร **แล้วจึง** ใส่เครื่องหมายคำพูด
    /// (ลำดับสำคัญ: ถ้าใส่เครื่องหมายคำพูดก่อน <c>'</c> จะไปอยู่นอกเครื่องหมาย)</summary>
    public static string Escape(string? value)
    {
        var v = Neutralize(value);
        if (v.IndexOf(',') >= 0 || v.IndexOf('"') >= 0
            || v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    /// <summary>ต่อหนึ่งแถวเป็นบรรทัด CSV (ไม่รวมตัวขึ้นบรรทัด)</summary>
    public static string Row(IEnumerable<string?> fields)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var f in fields)
        {
            if (!first) sb.Append(',');
            sb.Append(Escape(f));
            first = false;
        }
        return sb.ToString();
    }
}
