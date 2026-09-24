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

    // ─────────────────────────────────────────────────────────────────────────
    //  ป้ายสาขาที่ติดท้ายชื่อ · รหัสที่ไม่ใช่ชื่อ (รอบ 190 · สแกนจริงใบ A/B)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>ป้ายสาขาท้ายชื่อ: “(สำนักงานใหญ่)” · “สำนักงานใหญ่” · “Head Office” · “สนญ.” ·
    /// “(สาขาที่ 3)” · “สาขา 00003” · “Branch 00012” · “Branch No. 8” — ต้องอยู่<b>ท้ายสุด</b>ของชื่อ
    /// (ขึ้นต้นด้วยช่องว่างหรือวงเล็บ ⇒ ไม่ไปตัดคำที่มี “สาขา” อยู่กลางชื่อ)</summary>
    private static readonly System.Text.RegularExpressions.Regex BranchSuffixRx = new(
        @"(?:^|[ \t]+|(?=\())\(?[ \t]*(?:สำนักงานใหญ่|สนญ\.?|head[ \t]*office|(?:สาขา(?:ที่|ที)?|branch)[ \t]*(?:no\.?|เลขที่)?[ \t]*[:：]?[ \t]*\d{1,5})[ \t]*\)?[ \t]*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// <b>ตัดป้ายสาขาที่ติดท้ายชื่อคู่ค้าออก</b> — ชื่อนิติบุคคลกับสาขาเป็นคนละช่องตาม §86/4 +
    /// ประกาศอธิบดีฯ 199 (ชื่อ · “สำนักงานใหญ่/สาขาที่ …”)
    ///
    /// <para>═══ ที่มา (สแกนจริง 2026-09-24) ═══ ใบ B พิมพ์ “หจก. แอม แฮปปี้เนส (สำนักงานใหญ่)”
    /// ในช่องผู้ซื้อ ⇒ ชื่อผู้ซื้อบนหน้ารีวิวติด “(สำนักงานใหญ่)” · ใบ A หัวบรรทัดแรกคือ
    /// “Wine Pro Co.,Ltd. Branch 00012” ⇒ ชื่อผู้ขายติด “Branch 00012” และเลข “12” ท้ายรหัส
    /// สาขาไปชนเลขบ้าน “12/861” จนตัวตัดเศษชื่อกินเลขบ้าน (ซ่อมที่รากแล้วใน
    /// <see cref="OcrPartyAddress.StripLeakedNameFragment"/> — ตัวนี้คือการแยกข้อมูลให้ถูกช่อง)</para>
    ///
    /// <para>คืนเฉพาะชื่อที่ตัดแล้ว + ป้ายที่ตัดออก <b>ไม่คืนรหัสสาขา</b> — ตัวอ่านรหัสสาขามีตัวเดียว
    /// (<c>BranchCodeExtractor</c> ซึ่งอ่านคำเดียวกันนี้จากกระดาษอยู่แล้ว) ถ้าตัวนี้เติมรหัสเอง
    /// จะแข่งกับตัวอ่านสาขา และ “Head Office” ท้ายชื่อผู้ขายจะชนะ “สาขาที่ออกใบกำกับคือสาขาที่ 8”
    /// (ใบ B) ซึ่งเป็นหลักฐานที่แรงกว่า</para>
    /// </summary>
    /// <returns><c>(Name, Suffix)</c> — <c>Suffix = null</c> เมื่อไม่มีป้ายให้ตัด (Name = ค่าเดิม)
    /// · ไม่ตัดเมื่อเหลือชื่อสั้นเกิน 3 ตัวอักษร (ช่องนั้นอาจเป็นแค่ป้ายสาขาอย่างเดียว)</returns>
    public static (string? Name, string? Suffix) StripBranchSuffix(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (name, null);
        var s = name.Trim();
        var m = BranchSuffixRx.Match(s);
        if (!m.Success || m.Index == 0) return (s, null);
        var head = s[..m.Index].TrimEnd(' ', '\t', ',', '-', '/');
        if (Normalize(head).Length < 3) return (s, null);
        // วงเล็บต้องครบคู่ — “บริษัท เอ (สาขาที่ 3” ที่ engine ตัดวงเล็บปิดหายก็ยังตัดได้
        // แต่ห้ามทิ้งวงเล็บเปิดค้างไว้ท้ายชื่อ
        head = head.TrimEnd('(').TrimEnd();
        return (head, s[m.Index..].Trim());
    }

    /// <summary>
    /// <b>ค่านี้เป็น “รหัส” ไม่ใช่ “ชื่อ”</b> — เช่น รหัสลูกค้าของร้าน <c>[CZBNG2600843]</c> ·
    /// <c>C-0012</c> · <c>CUST00931</c>
    ///
    /// <para>═══ ที่มา (สแกนจริง 2026-09-24 · ใบ A Wine Pro) ═══ “Customer Info. [CZBNG2600843]”
    /// แล้วบรรทัดถัดไปคือ “หจก.แอม แฮปปี้เนส” — engine หยิบรหัสลูกค้าไปเป็นชื่อผู้ซื้อ</para>
    ///
    /// <para>กติกา: token เดียว (ไม่มีช่องว่างข้างใน) · ไม่มีอักษรไทย · มีตัวเลข · อักษรละติน
    /// ≥ 1 ตัว<b>หรือ</b>ถูกครอบด้วยวงเล็บเหลี่ยม — ชื่อบริษัทจริงมีคำ (ช่องว่าง) หรือเป็นภาษาไทย
    /// เสมอ · ชื่อสั้นอย่าง “3M” ยาวไม่ถึงเกณฑ์ (≥ 5 ตัวหลังตัดวงเล็บ) จึงไม่ถูกตีเป็นรหัส</para>
    /// </summary>
    public static bool LooksLikeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        var bracketed = v.Length >= 2 && ((v[0] == '[' && v[^1] == ']') || (v[0] == '<' && v[^1] == '>'));
        var core = v.Trim('[', ']', '(', ')', '<', '>', '#', ':', ' ').Trim();
        if (core.Length < 5) return false;
        if (core.Any(char.IsWhiteSpace)) return false;
        if (core.Any(c => c >= '฀' && c <= '๿')) return false;
        if (!core.Any(char.IsDigit)) return false;
        if (!core.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '/' or '.')) return false;
        return bracketed || core.Any(char.IsLetter);
    }
}
