namespace Accounting.Helpers;

/// <summary>
/// **ด่านตรวจ "ชื่อที่คนอื่นต้องมองเห็น" — ชั้นที่สอง ไม่ใช่ชั้นเดียว**
///
/// ═══ ที่มา (ผลตรวจ F-01) ═══
/// ชื่อบริษัท · ชื่อ-นามสกุลผู้ใช้ ที่ *ผู้เช่าพิมพ์เอง* ถูกนำไปแสดงใน
/// **หน้าจอของ SystemAdmin** (`/admin/customers.html` · `/admin/users.html`)
/// ซึ่งถือ token ของแพลตฟอร์ม ⇒ XSS ที่นั่น = ผู้เช่ายึดสิทธิ์ทั้งระบบ
///
/// <para><b>ตัวแก้จริงคือการหนีตอนแสดงผล</b> (`Layout.esc` / `AdminLayout.esc`
/// หนีครบ 5 ตัวแล้ว) — ด่านนี้ไม่ได้มาแทน แต่มาเสริมสองอย่างที่การหนีทำให้ไม่ได้:</para>
/// <list type="number">
///   <item>ค่าที่มี <c>&lt; &gt;</c> ไม่เคยเป็นชื่อจริงของบริษัท/คนไทยหรือสากล —
///         ปล่อยเข้ามาเก็บไว้แปลว่ารอวันที่มีใครลืมหนีสักจุดเดียว (เรพนี้เคย
///         ลืมมาแล้ว 170 จุด) และมันจะไหลออกไปทาง <b>PDF · อีเมล · XML e-Tax ·
///         ไฟล์ CSV</b> ที่ไม่ได้ใช้ตัวหนีของหน้าเว็บเลย</item>
///   <item>ความยาวที่ไม่จำกัด = ตารางแอดมินแตกและ PDF ล้นกรอบ</item>
/// </list>
///
/// <para>⚠️ <b>ปฏิเสธ ไม่ใช่ตัดทิ้งเงียบ ๆ</b> — การ "ทำความสะอาด" ชื่อให้เองคือ
/// การแก้ข้อมูลของผู้ใช้โดยเขาไม่รู้ (ชื่อบนใบกำกับต้องตรงกับทะเบียน §86/4)
/// ข้อความปฏิเสธจึงต้องบอกว่าติดตรงไหนเพื่อให้แก้เองได้</para>
/// </summary>
public static class DisplayText
{
    /// <summary>ความยาวสูงสุดของชื่อที่แสดงข้ามระบบ — พอสำหรับชื่อนิติบุคคลไทย
    /// เต็มรูป ("บริษัท … จำกัด (มหาชน)") ที่ยาวที่สุดที่เจอจริง</summary>
    public const int MaxNameLength = 150;

    /// <summary>อักขระที่เปิดแท็ก/entity ได้ — ไม่มีในชื่อจริง</summary>
    public static bool ContainsMarkup(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
            if (c is '<' or '>') return true;
        return false;
    }

    /// <summary>อักขระควบคุม (รวม NUL/ESC ที่ทำให้ log และ CSV เพี้ยน) —
    /// ยกเว้น tab/CR/LF ที่เป็นการจัดหน้าปกติในที่อยู่</summary>
    public static bool ContainsControlChars(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        foreach (var c in value)
            if (char.IsControl(c) && c != '\t' && c != '\r' && c != '\n') return true;
        return false;
    }

    /// <summary>ชื่อนี้ใช้แสดงข้ามระบบได้ไหม (ว่าง = ผ่าน — ความ "จำเป็นต้องมี"
    /// เป็นคนละกฎ ให้ <c>NotEmpty()</c> ของแต่ละฟอร์มตัดสิน)</summary>
    public static bool IsSafeName(string? value, int maxLength = MaxNameLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (value.Length > maxLength) return false;
        return !ContainsMarkup(value) && !ContainsControlChars(value);
    }

    /// <summary>เหตุผลภาษาไทยที่เอาไปโชว์ผู้ใช้ได้ตรง ๆ — ต้องบอกว่าติดตรงไหน
    /// ไม่ใช่ "ข้อมูลไม่ถูกต้อง" ลอย ๆ</summary>
    public static string RejectReason(string fieldLabel, string? value, int maxLength = MaxNameLength)
    {
        if (!string.IsNullOrWhiteSpace(value) && value.Length > maxLength)
            return $"{fieldLabel}ยาวเกิน {maxLength} ตัวอักษร (กรอกมา {value.Length})";
        return $"{fieldLabel}มีอักขระที่ใช้ไม่ได้ (< > หรืออักขระควบคุม) — กรุณาลบออก";
    }
}
