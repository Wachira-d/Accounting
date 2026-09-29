using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>วิธีคิดค่าเสื่อมของแถวนำเข้าทะเบียนสินทรัพย์ — ตัวตัดสินเดียว (รอบ 200 ทีม R · E-03)
///
/// ═══ ที่มา ═══
/// <c>FixedAssetService.ImportAsync</c> แปลงข้อความวิธีคิดด้วย switch ที่ไม่มีเคส "ไม่คิดค่าเสื่อม" และ <c>_ =&gt; StraightLine</c>
/// ⇒ แถวที่ดิน/งานระหว่างก่อสร้างที่ย้ายระบบเข้ามา ถูกสร้างเป็นค่าเสื่อมเส้นตรง 60 เดือน + ตารางค่าเสื่อมทันที
/// ⇒ ลง JE ค่าเสื่อมที่ดินทุกเดือน (พ.ร.ฎ.145 หักไม่ได้ ต้องบวกกลับ ภ.ง.ด.50) ขณะที่เส้นสร้างด้วยมือบล็อกแล้ว ·
/// และข้อความที่สะกดผิด ("straigth") กลายเป็นเส้นตรงเงียบ ๆ
///
/// ═══ กติกา ═══
/// ว่าง = เส้นตรง (พฤติกรรมเดิม) ยกเว้นหมวดที่ดิน/งานระหว่างก่อสร้าง = ไม่คิดค่าเสื่อม · หมวดที่ดิน/CIP + วิธีอื่น = ปฏิเสธแถว
/// พร้อมทางแก้ (เหมือนเส้นมือ — ไม่แก้ค่าให้เงียบ ๆ) · ข้อความที่ไม่รู้จัก = ปฏิเสธแถว (ไม่เดา)</summary>
public static class FixedAssetImportMethod
{
    private static readonly string[] NonDepreciableCategories =
    {
        "ที่ดิน", "land", "งานระหว่างก่อสร้าง", "construction in progress", "cip",
    };

    /// <summary>true = หมวดนี้คิดค่าเสื่อมไม่ได้ (ที่ดิน/งานระหว่างก่อสร้าง) — เทียบทั้งคำ (ไม่ใช่ "ปรับปรุงที่ดิน")</summary>
    private static bool IsNonDepreciableCategory(string? category)
    {
        var c = Norm(category);
        return c.Length > 0 && NonDepreciableCategories.Contains(c);
    }

    /// <summary>ผลตัดสิน: <c>Method</c> เมื่อรับได้ · <c>Error</c> (ข้อความไทยพร้อมทางแก้) เมื่อต้องปฏิเสธแถว</summary>
    public static (DepreciationMethod? Method, string? Error) Resolve(string? methodText, string? category)
    {
        var landLike = IsNonDepreciableCategory(category);
        var m = Norm(methodText);
        var known = true;
        DepreciationMethod? parsed = null;
        switch (m)
        {
            case "": break;
            case "none" or "ไม่คิดค่าเสื่อม" or "ไม่คิด" or "-": parsed = DepreciationMethod.None; break;
            case "straightline" or "straight line" or "straight" or "sl" or "เส้นตรง": parsed = DepreciationMethod.StraightLine; break;
            case "decliningbalance" or "declining balance" or "declining" or "ยอดลดลง": parsed = DepreciationMethod.DecliningBalance; break;
            case "doubledecliningbalance" or "double declining balance" or "doubledeclining" or "ddb" or "ยอดลดลงทวีคูณ":
                parsed = DepreciationMethod.DoubleDecliningBalance; break;
            default: known = false; break;
        }
        if (!known)
            return (null, $"ไม่รู้จักวิธีคิดค่าเสื่อม “{methodText?.Trim()}” — ใช้ StraightLine / DecliningBalance / "
                + "DoubleDecliningBalance / None (ไม่คิดค่าเสื่อม) หรือเว้นว่าง = เส้นตรง");
        if (landLike)
        {
            if (parsed is null or DepreciationMethod.None) return (DepreciationMethod.None, null);
            return (null, $"หมวด “{category?.Trim()}” คิดค่าเสื่อมราคาไม่ได้ (ค่าเสื่อมที่ดิน/งานระหว่างก่อสร้างหักเป็นรายจ่าย"
                + "ทางภาษีไม่ได้ — พ.ร.ฎ.145) — แก้คอลัมน์วิธีคิดค่าเสื่อมเป็น None หรือเว้นว่าง แล้วนำเข้าแถวนี้อีกครั้ง");
        }
        return (parsed ?? DepreciationMethod.StraightLine, null);
    }

    private static string Norm(string? s)
        => string.Join(' ', (s ?? "").Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
