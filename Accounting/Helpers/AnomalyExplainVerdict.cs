namespace Accounting.Helpers;

/// <summary>คำอธิบายรายการผิดปกติ (<c>ExplainAnomaly</c>) — คำตอบไหน "บันทึกลงรายการได้" (รอบ 200 ทีม R · H-3 · ทีม RF · R200-X8)
///
/// ═══ ที่มา ═══
/// เดิมบันทึกเฉพาะเมื่อ <c>resp.UsedAi</c> ⇒ ตอนปิด provider (kill-switch กฎเหล็ก #1 ข้อ 5) คำตอบของนักเรียน
/// (<c>AnomalyExplanationDistillationModel</c>) ถูกทิ้งทุกครั้ง: คอลัมน์ "AI ว่าอย่างไร" ว่างตลอดกาล และเปิดหน้าใหม่ = ยิงซ้ำ ·
/// และเขียนคำตัดสินโดยไม่ตรวจว่าอยู่ในชุดคำตอบที่ prompt กำหนด (DOCTRINE §2 write-gate ต้องมี candidate set)
///
/// ═══ R200-X8 (ทิศตรงข้ามของ H-3) ═══
/// รุ่นแรกทิ้งคำตอบครูที่ไม่อยู่ในชุด (<c>"likely_error"</c> · คำไทย) ⇒ จ่าย token แล้วไม่เก็บ + เปิดหน้าใหม่ = เรียกครูซ้ำทุกครั้ง ·
/// รุ่นนี้ <see cref="Coerce"/> แปลงเข้าชุดด้วยกติกาชัด: (1) ตรงชุดไม่สนตัวพิมพ์ (2) ตัด <c>_</c>/<c>-</c>/ช่องว่าง แล้วตรงชุด
/// (3) อย่างอื่นทั้งหมด ⇒ <c>NeedReview</c> (= "ให้คนตรวจ" — ทิศที่มองเห็น ไม่เดาความหมายจากคำอิสระ) · ค่าว่าง = ไม่มีคำตอบ (ไม่บันทึก)</summary>
public static class AnomalyExplainVerdict
{
    /// <summary>ชุดคำตอบที่ prompt <c>AnomalyExplainPrompt</c> กำหนด (<c>primary</c>)</summary>
    public static readonly IReadOnlyList<string> Candidates = new[] { "LikelyError", "LikelyLegit", "NeedReview" };

    /// <summary>ค่าเมื่อคำตอบอยู่นอกชุด — ให้คนตรวจ</summary>
    public const string Fallback = "NeedReview";

    /// <summary>ค่ามาตรฐานของคำตอบ (ตัวพิมพ์ตามชุด) หรือ null เมื่อไม่อยู่ในชุดตรงตัว</summary>
    public static string? Normalize(string? primary)
        => string.IsNullOrWhiteSpace(primary) ? null
            : Candidates.FirstOrDefault(c => string.Equals(c, primary.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>คำตอบที่เขียนลงรายการได้เสมอเมื่อมีคำตอบ: ตรงชุด ⇒ ค่ามาตรฐาน · ต่างแค่รูปแบบ (<c>likely_error</c>) ⇒ ค่าในชุด ·
    /// นอกชุด ⇒ <see cref="Fallback"/> · ว่าง/null ⇒ null (ไม่มีคำตอบให้เก็บ)</summary>
    public static string? Coerce(string? primary)
    {
        if (string.IsNullOrWhiteSpace(primary)) return null;
        var exact = Normalize(primary);
        if (exact != null) return exact;
        var squashed = new string(primary.Where(ch => ch != '_' && ch != '-' && !char.IsWhiteSpace(ch)).ToArray());
        return Normalize(squashed) ?? Fallback;
    }

    /// <summary>true = คำตอบตรงชุดหรือต่างแค่รูปแบบ (ความมั่นใจของผู้ตอบใช้ได้) · false = ถูกแปลงเป็น <see cref="Fallback"/> (ความมั่นใจไม่ใช่ของคำนี้)</summary>
    public static bool IsRecognized(string? primary)
    {
        if (string.IsNullOrWhiteSpace(primary)) return false;
        var squashed = new string(primary.Where(ch => ch != '_' && ch != '-' && !char.IsWhiteSpace(ch)).ToArray());
        return Normalize(primary) != null || Normalize(squashed) != null;
    }

    /// <summary>true = บันทึกลงรายการได้: มีผู้ตอบจริง (ครูหรือนักเรียน — ไม่ใช้ UsedAi เป็นด่าน) + มีคำตอบ (แปลงเข้าชุดด้วย <see cref="Coerce"/>)</summary>
    public static bool ShouldPersist(bool usedAi, bool fromLocalModel, string? primary)
        => (usedAi || fromLocalModel) && Coerce(primary) != null;
}
