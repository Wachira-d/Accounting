namespace Accounting.Helpers;

/// <summary>คำอธิบายรายการผิดปกติ (<c>ExplainAnomaly</c>) — คำตอบไหน "บันทึกลงรายการได้" (รอบ 200 ทีม R · H-3)
///
/// ═══ ที่มา ═══
/// เดิมบันทึกเฉพาะเมื่อ <c>resp.UsedAi</c> ⇒ ตอนปิด provider (kill-switch กฎเหล็ก #1 ข้อ 5) คำตอบของนักเรียน
/// (<c>AnomalyExplanationDistillationModel</c>) ถูกทิ้งทุกครั้ง: คอลัมน์ "AI ว่าอย่างไร" ว่างตลอดกาล และเปิดหน้าใหม่ = ยิงซ้ำ ·
/// และเขียนคำตัดสินโดยไม่ตรวจว่าอยู่ในชุดคำตอบที่ prompt กำหนด (DOCTRINE §2 write-gate ต้องมี candidate set)</summary>
public static class AnomalyExplainVerdict
{
    /// <summary>ชุดคำตอบที่ prompt <c>AnomalyExplainPrompt</c> กำหนด (<c>primary</c>)</summary>
    public static readonly IReadOnlyList<string> Candidates = new[] { "LikelyError", "LikelyLegit", "NeedReview" };

    /// <summary>ค่ามาตรฐานของคำตอบ (ตัวพิมพ์ตามชุด) หรือ null เมื่อไม่อยู่ในชุด</summary>
    public static string? Normalize(string? primary)
        => string.IsNullOrWhiteSpace(primary) ? null
            : Candidates.FirstOrDefault(c => string.Equals(c, primary.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>true = บันทึกลงรายการได้: มีผู้ตอบจริง (ครูหรือนักเรียน — ไม่ใช้ UsedAi เป็นด่าน) + อยู่ในชุดคำตอบ</summary>
    public static bool ShouldPersist(bool usedAi, bool fromLocalModel, string? primary)
        => (usedAi || fromLocalModel) && Normalize(primary) != null;
}
