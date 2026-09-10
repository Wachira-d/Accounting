namespace Accounting.Helpers;

/// <summary>
/// **ชื่อคู่ค้าที่ engine ตัดสั้น — ขยายด้วยบรรทัดจริงบนกระดาษ**
///
/// <para>ที่มา (สแกนจริง 2026-09-10): กระดาษพิมพ์ “หจก. แอม แฮปปี้เนส” แต่ Azure DI
/// คืน CustomerName = “แอม แฮปปี้” (ตัดคำนำหน้านิติบุคคลและท้ายชื่อ) และทุกตัวสำรอง
/// ในระบบเป็นแบบ “เติมเฉพาะช่องว่าง” จึงไม่มีใครซ่อม · ผลคือ Contact ที่สร้าง/จับคู่
/// ผิดชื่อ และใบกำกับที่ออกต่อพิมพ์ชื่อผู้ซื้อไม่ครบ (§86/4 ข้อ 3)</para>
///
/// <para>กติกา: ยอมแทนค่าเดิมด้วยผู้สมัครจากกระดาษก็ต่อเมื่อ ผู้สมัคร**ครอบ**ค่าเดิม
/// (normalize แล้ว) และยาวกว่า — คือค่าเดิมเป็น “ส่วนหนึ่ง” ของบรรทัดจริง ไม่ใช่คนละชื่อ
/// · ไม่แทนเมื่อค่าเดิมสั้นเกินจะเป็นหลักฐาน (&lt; 3 ตัวอักษรหลัง normalize)
/// · ไม่แทนด้วยชื่อที่ตรงกับอีกฝั่ง (ผู้ขาย ≠ ผู้ซื้อ)</para>
/// </summary>
public static class OcrPartyName
{
    public static string Normalize(string? s)
        => new string((s ?? string.Empty).ToLowerInvariant()
            .Where(c => !char.IsWhiteSpace(c) && c != '.' && c != ',' && c != '(' && c != ')').ToArray());

    /// <param name="current">ชื่อที่ engine ให้มา (อาจถูกตัด)</param>
    /// <param name="candidates">ชื่อนิติบุคคลเต็มบรรทัดที่สกัดจากข้อความ</param>
    /// <param name="exclude">ชื่ออีกฝั่ง (ห้ามขยายไปชนกัน) — null ได้</param>
    /// <returns>ชื่อที่ขยายแล้ว หรือ null เมื่อไม่มีผู้สมัครที่เข้าเกณฑ์</returns>
    public static string? ExpandTruncated(string? current, IEnumerable<string> candidates, string? exclude = null)
    {
        var cur = Normalize(current);
        if (cur.Length < 3) return null;
        var ex = Normalize(exclude);

        string? best = null;
        foreach (var cand in candidates)
        {
            var n = Normalize(cand);
            if (n.Length <= cur.Length) continue;
            if (!n.Contains(cur, StringComparison.Ordinal)) continue;
            if (ex.Length > 0 && n == ex) continue;
            // เลือกตัวที่ยาวสุดที่ยังครอบค่าเดิม (บรรทัดเต็มชนะเศษบรรทัด)
            if (best == null || Normalize(best).Length < n.Length) best = cand.Trim();
        }
        return best;
    }
}
