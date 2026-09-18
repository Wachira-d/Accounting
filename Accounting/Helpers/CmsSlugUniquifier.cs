using System.Globalization;

namespace Accounting.Helpers;

/// <summary>เติมเลขต่อท้าย slug ที่ชนกับของเดิม (<c>b1</c> → <c>b1-2</c> → <c>b1-3</c>)
///
/// ใช้กับคีย์ที่ **ระบบสร้างให้เอง** เท่านั้น — Site.Slug มาจากชื่อเว็บผ่าน
/// GenerateSlug ผู้ใช้ไม่ได้พิมพ์และไม่มีช่องให้แก้ในฟอร์ม ⇒ ถ้าชนแล้วโยน error
/// ผู้ใช้จะเจอทางตัน ("slug ซ้ำ" ทั้งที่ไม่เคยเห็นคำว่า slug มาก่อน)
/// ต่างจาก Subdomain ที่ผู้ใช้พิมพ์เอง — ตัวนั้นต้องบอกให้เปลี่ยน ห้ามเปลี่ยนให้เงียบ ๆ</summary>
public static class CmsSlugUniquifier
{
    /// <summary>จำนวนครั้งที่ลองเติมเลขก่อนยอมแพ้แล้วใช้ค่าสุ่มแทน</summary>
    public const int MaxNumericAttempts = 999;

    public static string MakeUnique(string? desired, IEnumerable<string?>? taken, int maxLength)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (taken != null)
            foreach (var t in taken)
                if (!string.IsNullOrEmpty(t)) used.Add(t);

        var head = Fit(desired ?? "", maxLength);
        // slug ว่าง (ชื่อเว็บเป็นอักขระที่ตัดทิ้งหมด) ต้องมีอะไรสักอย่างให้ routing จับ
        if (head.Length == 0) head = "site";
        if (!used.Contains(head)) return head;

        for (var n = 2; n <= MaxNumericAttempts; n++)
        {
            var suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
            var candidate = Fit(head, maxLength - suffix.Length) + suffix;
            if (!used.Contains(candidate)) return candidate;
        }

        // ถึงตรงนี้แปลว่ามีชื่อเดียวกันเกิน 999 อัน — ไม่ควรเกิดจริง แต่ห้ามคืนค่าที่ชน
        var tail = "-" + Guid.NewGuid().ToString("N")[..8];
        return Fit(head, maxLength - tail.Length) + tail;
    }

    /// <summary>ตัดให้พอดีคอลัมน์ แล้วเล็มขีดท้ายทิ้ง (กัน <c>ab--2</c> ตอนหัวถูกตัดคาขีด)</summary>
    private static string Fit(string s, int max)
    {
        if (max <= 0) return "";
        if (s.Length > max) s = s[..max];
        return s.TrimEnd('-');
    }
}
