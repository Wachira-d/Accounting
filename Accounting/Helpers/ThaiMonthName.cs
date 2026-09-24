namespace Accounting.Helpers;

/// <summary>
/// **แปลงชื่อเดือนบนกระดาษ → เลขเดือน** (pure, ไม่มี I/O) — ตัวเดียวของระบบ
///
/// ═══ ทำไมต้องมี (ผลตรวจ 2026-09-06 · T2-13) ═══
/// ตัวอ่านวันที่บนเส้นทางหลัก (<c>OcrService.ParseThaiDocument</c>) รู้จักเฉพาะ
/// <b>ตัวย่อ</b> ("ม.ค"/"มค") — <b>ไม่รู้จักชื่อเดือนเต็ม</b> ("มกราคม"/"สิงหาคม")
/// ซึ่งเป็นรูปที่ใบกำกับ/ใบเสร็จไทยพิมพ์บ่อยที่สุด (สังเกต: "มกราคม" ไม่มีสตริง
/// "ม.ค" และไม่มี "มค" อยู่ข้างในเลย จึงไม่แมตช์ตัวย่อ)
///
/// <para>⚠️ และเมื่อแมตช์ไม่ได้ โค้ดเดิม <b>ตั้งเดือนเป็น 1 (มกราคม)</b> แล้วเดินต่อ
/// ⇒ "15 สิงหาคม 2569" กลายเป็น <b>15 มกราคม 2026</b> เงียบ ๆ ⇒ tax point (§78)
/// และงวด ภ.พ.30 ผิดไป 7 เดือน โดยยอดทุกตัวยังถูก จึงไม่มีอะไรสะดุดตา
/// — "ค่า default ที่แต่งขึ้นเพื่อให้โค้ดเดินต่อได้ อันตรายกว่าการไม่ตอบ" ตรงตัว
/// ⇒ ตัวนี้คืน <c>null</c> เมื่อไม่รู้จัก และผู้เรียกต้องข้ามวันที่นั้นไป</para>
/// </summary>
public static class ThaiMonthName
{
    /// <summary>ชื่อเดือนไทยเต็ม (index 1–12) — ใช้เป็นแหล่งความจริงของทั้งสองทิศ</summary>
    public static readonly string[] FullThai =
    {
        "", "มกราคม", "กุมภาพันธ์", "มีนาคม", "เมษายน", "พฤษภาคม", "มิถุนายน",
        "กรกฎาคม", "สิงหาคม", "กันยายน", "ตุลาคม", "พฤศจิกายน", "ธันวาคม",
    };

    /// <summary>ตัวย่อไทยที่พบบนกระดาษ (ทั้งมีจุดและไม่มีจุด) → เลขเดือน</summary>
    private static readonly (string Token, int Month)[] Tokens =
    {
        // ── ชื่อเต็ม (ยาวสุดต้องมาก่อน เพื่อไม่ให้ตัวย่อชนะแบบผิด ๆ) ──
        ("มกราคม", 1), ("กุมภาพันธ์", 2), ("มีนาคม", 3), ("เมษายน", 4),
        ("พฤษภาคม", 5), ("มิถุนายน", 6), ("กรกฎาคม", 7), ("สิงหาคม", 8),
        ("กันยายน", 9), ("ตุลาคม", 10), ("พฤศจิกายน", 11), ("ธันวาคม", 12),
        // ── ตัวย่อ (มีจุด) ──
        ("ม.ค", 1), ("ก.พ", 2), ("มี.ค", 3), ("เม.ย", 4), ("พ.ค", 5), ("มิ.ย", 6),
        ("ก.ค", 7), ("ส.ค", 8), ("ก.ย", 9), ("ต.ค", 10), ("พ.ย", 11), ("ธ.ค", 12),
        // ── ตัวย่อ (ไม่มีจุด) — "มีค" ต้องมาก่อน "มค" ไม่งั้นชนกัน ──
        ("มีค", 3), ("เมย", 4), ("มิย", 6), ("มค", 1), ("กพ", 2), ("พค", 5),
        ("กค", 7), ("สค", 8), ("กย", 9), ("ตค", 10), ("พย", 11), ("ธค", 12),
        // ── อังกฤษ (ใบของผู้ขายต่างชาติ/ระบบ ERP ต่างประเทศ) ──
        ("january", 1), ("february", 2), ("march", 3), ("april", 4), ("may", 5),
        ("june", 6), ("july", 7), ("august", 8), ("september", 9), ("october", 10),
        ("november", 11), ("december", 12),
        ("jan", 1), ("feb", 2), ("mar", 3), ("apr", 4), ("jun", 6), ("jul", 7),
        ("aug", 8), ("sep", 9), ("oct", 10), ("nov", 11), ("dec", 12),
    };

    /// <summary>
    /// แบบ<b>ตรงทั้งคำ</b> — ใช้เมื่อผู้เรียก "กวาด" คำใดก็ได้ที่อยู่ระหว่างตัวเลขสองก้อน
    /// (ตัวอ่านวันที่ <see cref="OcrDateReader"/>) ซึ่ง <see cref="TryParse"/> แบบ Contains
    /// อันตราย: "5 <b>mar</b>ket 2026" · "3 <b>may</b>or 26" · "1 ห<b>มค</b>..." จะกลายเป็นวันที่
    /// ⇒ ตัดจุด/ช่องว่างแล้วต้อง<b>เท่ากับ</b>ชื่อเดือนในตารางเดียวกันนี้เท่านั้น (+ "sept")
    /// </summary>
    public static int? TryParseExact(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = new string(token.Where(c => c != '.' && !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        if (t.Length == 0) return null;
        if (t == "sept") return 9;
        foreach (var (name, month) in Tokens)
            if (string.Equals(name.Replace(".", ""), t, StringComparison.Ordinal)) return month;
        return null;
    }

    /// <summary>คืนเลขเดือน 1–12 หรือ <c>null</c> เมื่อ<b>ไม่รู้จัก</b>
    /// (ห้ามให้ผู้เรียกเดาเป็นมกราคม — ดูหมายเหตุที่หัวคลาส)</summary>
    public static int? TryParse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = token.Trim().ToLowerInvariant();
        if (int.TryParse(t, out var numeric))
            return numeric is >= 1 and <= 12 ? numeric : null;
        foreach (var (name, month) in Tokens)
            if (t.Contains(name, StringComparison.Ordinal)) return month;
        return null;
    }
}
