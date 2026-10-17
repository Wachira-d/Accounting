using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **จับผังบัญชีของบริษัทด้วย "คำในชื่อบัญชี"** — ตัวเดียวของชั้นสุดท้ายเมื่อรหัสที่ระบบเสนอไม่มีในผังของบริษัท
/// (ผู้ใช้ตั้งผังเอง ไม่ใช้รหัส 5 หลักของแม่แบบ) · ผู้เรียก: <c>OcrService</c> (ตัวหาเดบิตหลังตัวจัดหมวด + เดบิตแทนของด่านวางฝั่ง)
///
/// <para>═══ ที่มา (ฝ่ายค้านรอบ 5 P1-3 · ทีม EC5 · 2026-10-16) ═══ เดิมคำจับคู่<b>ดึงจากชื่อบัญชีของแม่แบบ</b> (<see cref="DistinctiveKeyword"/>) ·
/// พอ EC4 ย้ายรหัสกฎไปบัญชีย่อยของแม่แบบ 5 หลัก ชื่อเปลี่ยนตาม ⇒ คำเปลี่ยนตาม: ค่าขนส่ง "(ฝ่ายขาย)" ⇒ ตรง <b>"เงินเดือน (ฝ่ายขาย)" / "ค่านายหน้า (ฝ่ายขาย)"</b>
/// (เดบิตเงินเดือน = ลงบัญชีผิด ไม่ใช่ช่องว่าง) · น้ำมัน "เดินทาง" ⇒ "ค่าเดินทาง" · ค่าเช่า "เช่าสำนักงาน" · ที่ปรึกษา "ที่ปรึกษาอื่นๆ" ·
/// วัสดุ "วัสดุสิ้นเปลืองสำนักงาน" · ทำความสะอาด "บริการอื่นๆ" · ประกัน "เบี้ยประกันภัย" ⇒ ไม่ตรงผังที่ตั้งเองชื่อทั่วไปเลย</para>
/// <para>ตอนนี้: แต่ละกฎหมวดระบุ<b>คำจับคู่ของตัวเอง</b> (<see cref="Spec"/>) เรียงจากเจาะจงไปกว้าง — คำแรกที่ได้บัญชีที่เดบิตค่าใช้จ่ายได้ชนะ ·
/// บัญชีเงินเดือน/ค่าจ้างพนักงาน/ค่านายหน้า/สวัสดิการ/ประกันสังคม (<see cref="IsPayrollOrCommissionAccount"/>) <b>ห้ามได้เสมอ</b> (กฎหมวดของสแกนไม่ใช่เงินเดือน) ·
/// ตัวเลือกในแต่ละชั้นผ่าน <see cref="GlDebitAccountPicker"/> (ค่าใช้จ่าย/สินทรัพย์เท่านั้น)</para>
/// </summary>
public static class GlAccountNameMatch
{
    /// <summary>คำจับคู่ชื่อบัญชีของกฎหนึ่ง — <paramref name="Keywords"/> เรียงจากเจาะจงไปกว้าง · <paramref name="Avoid"/> = คำที่ถ้าอยู่ในชื่อบัญชี บัญชีนั้นไม่ใช่ของกฎนี้
    /// (เช่น ค่าขนส่งเข้า ห้ามได้ "ค่าขนส่ง (ฝ่ายขาย)")</summary>
    public sealed record Spec(IReadOnlyList<string> Keywords, IReadOnlyList<string>? Avoid = null);

    /// <summary>ผลการจับ — <paramref name="Keyword"/> = คำที่ตรง · <paramref name="UnusableMatches"/> = จำนวนบัญชีที่ชื่อตรงแต่เดบิตค่าใช้จ่ายไม่ได้
    /// (หนี้สิน/รายได้/ทุน — ผู้เรียกเขียนเหตุผลแทนการเงียบ)</summary>
    public readonly record struct Result(GlAccountCandidate? Picked, string? Keyword, int UnusableMatches);

    /// <summary>คำในชื่อบัญชีเงินเดือน/ค่าจ้างพนักงาน/ค่านายหน้า (ตัวพิมพ์เล็ก) — ห้ามเป็นเดบิตของกฎหมวดสแกน</summary>
    private static readonly string[] PayrollOrCommissionWords =
    {
        "เงินเดือน", "ค่าจ้างพนักงาน", "ค่าแรงพนักงาน", "ค่าล่วงเวลา", "โบนัส", "เบี้ยเลี้ยงพนักงาน", "สวัสดิการพนักงาน",
        "ค่านายหน้า", "คอมมิชชั่น", "คอมมิชชัน", "ประกันสังคม", "กองทุนสำรองเลี้ยงชีพ", "เงินสมทบ", "บำเหน็จ", "ค่าชดเชยพนักงาน",
        "salary", "salaries", "wages", "payroll", "commission", "bonus", "social security", "provident fund",
    };

    /// <summary>ชื่อบัญชีนี้เป็นบัญชีเงินเดือน/ค่าตอบแทนพนักงาน/ค่านายหน้าไหม</summary>
    internal static bool IsPayrollOrCommissionAccount(string? accountName)
    {
        if (string.IsNullOrWhiteSpace(accountName)) return false;
        var n = accountName.ToLowerInvariant();
        return PayrollOrCommissionWords.Any(w => n.Contains(w, StringComparison.Ordinal));
    }

    /// <summary>
    /// เลือกบัญชีเดบิตจากผังของบริษัทด้วยคำของ <paramref name="spec"/> — ไล่ทีละคำ (ชั้น) · ในชั้นหนึ่งกรองบัญชีเงินเดือน/ค่านายหน้า + คำ Avoid
    /// แล้วให้ <see cref="GlDebitAccountPicker.Pick"/> เลือก · ชั้นแรกที่ได้บัญชีชนะ · ไม่มีชั้นไหนได้ ⇒ Picked = null (ไม่แต่ง)
    /// </summary>
    public static Result Pick(IEnumerable<GlAccountCandidate> accounts, Spec? spec, string? seededCode)
    {
        if (spec == null || spec.Keywords.Count == 0) return new Result(null, null, 0);
        var pool = accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.Name) && !IsPayrollOrCommissionAccount(a.Name))
            .Where(a => spec.Avoid is not { Count: > 0 } av
                || !av.Any(w => a.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var unusable = 0;
        string? firstHit = null;
        foreach (var kw in spec.Keywords)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            var hits = pool.Where(a => a.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) continue;
            firstHit ??= kw;
            if (GlDebitAccountPicker.Pick(hits, seededCode) is { } picked) return new Result(picked, kw, 0);
            unusable = Math.Max(unusable, hits.Count);
        }
        return new Result(null, firstHit, unusable);
    }

    /// <summary>ชุดคำจากชื่อบัญชีที่ <b>ไม่ได้มาจากกฎหมวด</b> (ค่าตั้งต้นของชนิดกระดาษ · ผังที่ชั้นอื่นเสนอ) — คำเดียวจาก <see cref="DistinctiveKeyword"/> ·
    /// null = ไม่มีคำที่ใช้ได้</summary>
    public static Spec? FromAccountName(string? name)
        => DistinctiveKeyword(name) is { } kw ? new Spec(new[] { kw }) : null;

    /// <summary>
    /// Pull the most distinctive keyword out of a suggested account name so
    /// it can be matched against a company's custom Chart of Accounts.
    /// Thai account names run words together with no spaces, so we:
    ///   1. drop a leading account code if the caller passed "5306 ค่า…",
    ///   2. cut at the first conjunction ("และ", "หรือ", "/") so
    ///      "ค่าซ่อมแซมและบำรุงรักษา" yields "ซ่อมแซม" — a shorter stem
    ///      matches more CoA variants than the full compound,
    ///   3. strip shared classifier prefixes ("ค่า", "บัญชี") that nearly
    ///      every expense account carries and so add no signal,
    ///   4. fall back to the longest space-separated token for names that
    ///      do use spaces (English or mixed).
    /// Returns null when nothing distinctive (≥3 chars) is left.
    /// <para>⚠️ ใช้เฉพาะชื่อที่ไม่ได้มาจากกฎหมวด — กฎหมวดมีคำของตัวเอง (ฝ่ายค้านรอบ 5 P1-3: "ค่าขนส่ง (ฝ่ายขาย)" ⇒ "(ฝ่ายขาย)")</para>
    /// </summary>
    internal static string? DistinctiveKeyword(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var work = name.Trim();

        // Drop a leading account code passed as "5306 ค่า…".
        work = Regex.Replace(work, @"^\s*\d[\d\-\.]*\s+", "");

        // Cut at the first conjunction / separator — keep the leading concept.
        foreach (var sep in new[] { "และ", "หรือ", "/", ",", "&" })
        {
            var idx = work.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 2) { work = work[..idx]; break; }
        }

        // Strip shared classifier prefixes that carry no signal.
        foreach (var prefix in new[] { "ค่าใช้จ่าย", "ค่า", "บัญชี" })
        {
            if (work.StartsWith(prefix, StringComparison.Ordinal) && work.Length > prefix.Length + 2)
            {
                work = work[prefix.Length..];
                break;
            }
        }

        work = work.Trim();

        // For space-separated names, take the longest meaningful token.
        if (work.Contains(' '))
        {
            var token = work
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length >= 3)
                .OrderByDescending(t => t.Length)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(token)) work = token;
        }

        return work.Length >= 3 ? work : null;
    }
}
