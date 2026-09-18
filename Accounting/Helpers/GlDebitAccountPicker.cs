using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <param name="Code">รหัสผังบัญชี</param>
/// <param name="Name">ชื่อบัญชี</param>
/// <param name="Type">ประเภทบัญชี</param>
public readonly record struct GlAccountCandidate(string Code, string Name, AccountType Type);

/// <summary>
/// **เลือกผังบัญชี "ฝั่งเดบิต" ของใบฝั่งซื้อ — ตัวตัดสินตัวเดียว** (pure ไม่มี I/O)
///
/// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-09-18) ═══ ใบซื้ออุปกรณ์แคมป์ปิ้งถูกเสนอ
/// บัญชีเดบิต <b>21513 "ค่าโทรศัพท์ค้างจ่าย"</b> ซึ่งเป็น <b>หนี้สิน</b>
/// (<c>ChartOfAccountTemplates.cs:118</c> — <c>AccountType.Liability</c> ใต้กลุ่ม
/// 215 ค่าสาธารณูปโภคค้างจ่าย) การเดบิตบัญชีค้างจ่ายคือการ<b>ตัดหนี้ที่ไม่เคย
/// ตั้งไว้</b> — งบแสดงฐานะการเงินเพี้ยนทันที และค่าใช้จ่ายไม่เคยเข้างบกำไรขาดทุน</para>
///
/// <para>═══ ทำไมถึงเลือกผิด ═══ ขั้นสุดท้ายของการจับคู่ผังบัญชี (เมื่อรหัสที่
/// ชั้นก่อนหน้าเสนอไม่มีใน CoA ของบริษัทนั้น) ค้นด้วย<b>คำในชื่อบัญชี</b> แล้ว
/// <c>OrderBy(AccountCode)</c> เอาตัวแรก — <b>ไม่มีตัวกรองประเภทบัญชีเลย</b>
/// ⇒ หนี้สินขึ้นต้น "2" มาก่อนค่าใช้จ่ายขึ้นต้น "5" <b>เสมอ</b> ⇒ ทุกครั้งที่
/// ผังบัญชีของบริษัทมีทั้งคู่ (เช่น "ค่าโทรศัพท์ค้างจ่าย 21513" กับ
/// "ค่าโทรศัพท์และอินเทอร์เน็ต 5304") <b>ตัวที่ผิดชนะโดยโครงสร้าง</b> ไม่ใช่โดยบังเอิญ</para>
///
/// <para>═══ กติกา ═══
/// <list type="number">
/// <item>ตัดประเภทที่<b>เป็นเดบิตของค่าใช้จ่ายไม่ได้</b>ทิ้งก่อน — หนี้สิน · ส่วนของเจ้าของ · รายได้</item>
/// <item>เหลือ <b>ค่าใช้จ่าย</b> (ปกติ) และ <b>สินทรัพย์</b> (ซื้อของเข้าสต๊อก/สินทรัพย์ถาวร)
///   โดยให้ค่าใช้จ่ายมาก่อนเมื่อทั้งคู่เข้าข่าย</item>
/// <item>ในกลุ่มเดียวกัน เลือกตัวที่<b>ใกล้รหัสที่ชั้นก่อนหน้าเสนอ</b>ที่สุด
///   (prefix ร่วมยาวสุด) — ไม่ใช่ตัวที่รหัสน้อยสุด</item>
/// <item>เสมอกันจริง ๆ ค่อยใช้รหัสสั้นกว่า แล้วเรียงรหัส (ให้ผลคงที่ ไม่ขึ้นกับลำดับแถวจากฐาน)</item>
/// </list></para>
///
/// <para>⚠️ คืน <c>null</c> เมื่อไม่มีตัวเลือกที่ผ่านด่าน — <b>ห้ามคืนตัวที่ผิดประเภท
/// เป็นทางเลือกสุดท้าย</b> ปล่อยให้ช่องว่างแล้วให้คนเลือก ดีกว่าเติมบัญชีที่ทำให้งบเพี้ยน
/// (หลักการ "ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ")</para>
/// </summary>
public static class GlDebitAccountPicker
{
    /// <summary>ประเภทที่เป็น "ปลายทางของค่าใช้จ่าย/การซื้อ" ได้ — private เพราะ
    /// ผู้เรียกทุกคนควรถามผ่าน <see cref="Pick"/> ไม่ใช่ประกอบกติกาเอง</summary>
    private static bool CanBeExpenseDebit(AccountType t)
        => t is AccountType.Expense or AccountType.Asset;

    /// <summary>เลือกบัญชีเดบิตที่ดีที่สุดจากผู้สมัคร — null = ไม่มีตัวที่ใช้ได้</summary>
    /// <param name="candidates">ผังบัญชีที่ชั้นค้นหาได้มา (ชื่อมีคำที่ตรง / รหัสขึ้นต้นตรง)</param>
    /// <param name="seededCode">รหัสที่ชั้นก่อนหน้าเสนอ (ใช้วัดความใกล้) — ว่างได้</param>
    public static GlAccountCandidate? Pick(
        IEnumerable<GlAccountCandidate>? candidates, string? seededCode)
    {
        var usable = (candidates ?? Enumerable.Empty<GlAccountCandidate>())
            .Where(c => !string.IsNullOrWhiteSpace(c.Code) && CanBeExpenseDebit(c.Type))
            .ToList();
        if (usable.Count == 0) return null;

        var seed = seededCode?.Trim() ?? "";
        var best = usable
            .OrderBy(c => c.Type == AccountType.Expense ? 0 : 1)          // ค่าใช้จ่ายมาก่อนสินทรัพย์
            .ThenByDescending(c => SharedPrefixLength(c.Code, seed))      // ใกล้รหัสที่เสนอที่สุด
            .ThenBy(c => c.Code.Length)                                   // รหัสสั้นกว่า = กลุ่มกว้างกว่า
            .ThenBy(c => c.Code, StringComparer.Ordinal)                  // ผลคงที่เสมอ
            .First();
        return best;
    }

    /// <summary>จำนวนตัวอักษรต้นที่ตรงกัน — ใช้วัด "ใกล้กันในผังบัญชี"</summary>
    private static int SharedPrefixLength(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }
}
