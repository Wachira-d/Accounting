namespace Accounting.Helpers;

/// <summary>
/// คำนำหน้าชื่อสำหรับแบบราชการไทย — ระบบ e-Service ของประกันสังคมรับเฉพาะ
/// คำนำหน้าไทย (ปฏิเสธทั้งแถวพร้อมข้อความ "รหัสคำนำหน้า Mrs. ไม่ถูกต้อง")
/// ข้อมูลคำนำหน้าอังกฤษหลุดเข้ามาได้ทางหน้า import / sync API (หน้าจอปกติ
/// เป็น dropdown ไทย) จึงต้อง normalize ทั้งตอนบันทึกและตอนสร้างไฟล์ยื่น
/// </summary>
public static class ThaiTitleHelper
{
    /// <summary>คำนำหน้าที่ สปส. ยอมรับในไฟล์เงินสมทบ</summary>
    public static readonly HashSet<string> SsoValidTitles = new()
    { "นาย", "นาง", "นางสาว", "เด็กชาย", "เด็กหญิง" };

    /// <summary>แปลงคำนำหน้าอังกฤษ/ตัวย่อที่พบบ่อย → ไทย. คำที่ไม่รู้จัก
    /// (เช่น "ดร." ที่ผู้ใช้ตั้งใจเลือก) คืนค่าเดิม — ปลอดภัยต่อการเรียกตอน
    /// บันทึกข้อมูลพนักงาน เพราะแตะเฉพาะคำที่แปลได้ชัดเจนเท่านั้น</summary>
    public static string Normalize(string? title)
    {
        var t = (title ?? "").Trim();
        var key = t.TrimEnd('.').Trim().ToUpperInvariant();
        return key switch
        {
            "MR" or "MISTER" => "นาย",
            "MRS" or "MADAM" or "MADAME" => "นาง",
            "MISS" or "MS" => "นางสาว",
            "MASTER" => "เด็กชาย",
            "น.ส" or "นส" => "นางสาว",
            "ด.ช" or "ดช" => "เด็กชาย",
            "ด.ญ" or "ดญ" => "เด็กหญิง",
            _ => t,
        };
    }

    /// <summary>สำหรับไฟล์ยื่น สปส. โดยเฉพาะ: Normalize ก่อน ถ้ายังไม่อยู่ใน
    /// ชุดที่ สปส. รับ ให้เดาจากเพศ (ชาย=นาย, หญิง=นางสาว — สปส. รับ นางสาว
    /// สำหรับผู้หญิงทุกกรณี) ถ้าเดาไม่ได้คืนค่าเดิมให้ผู้เรียกแจ้งเตือน</summary>
    public static string NormalizeForSso(string? title, string? gender = null)
    {
        var t = Normalize(title);
        if (SsoValidTitles.Contains(t)) return t;
        var g = (gender ?? "").Trim().ToUpperInvariant();
        if (g is "M" or "MALE" or "ชาย") return "นาย";
        if (g is "F" or "FEMALE" or "หญิง") return "นางสาว";
        return t;
    }

    /// <summary>true เมื่อคำนำหน้าใช้ยื่น สปส. ได้โดยไม่โดนปฏิเสธ</summary>
    public static bool IsValidForSso(string? title) => SsoValidTitles.Contains((title ?? "").Trim());
}
