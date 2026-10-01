namespace Accounting.Helpers;

/// <summary>
/// **ด่านเขียน (write-gate) ของคำแนะนำผังบัญชีที่จะเติมลงบรรทัดเอกสารให้เอง** — ตัวตัดสินตัวเดียวฝั่งเซิร์ฟเวอร์
/// (รอบ 201 ทีม AI · A-AI5 · H-7)
///
/// <para>เดิมเกณฑ์ "≥ 0.70 ถึงจะเติมให้" อยู่ใน JavaScript ของ <c>documents.html</c> ตัวเดียว ⇒ ทางเข้าอื่นที่เรียก
/// <c>POST document/ai-suggest-pv-accounting</c> (มือถือ/สคริปต์/หน้าใหม่) ได้คำแนะนำที่ไม่มีด่านแล้วเติมลงเอกสารจริง ·
/// หลักการข้อ 5 "Server computes · page displays" + DOCTRINE §2.3 write-gate = ความมั่นใจ ≥ 0.70 <b>และ</b> candidate set</para>
///
/// <para>เติมให้ได้เมื่อครบทุกข้อ: มีคำตอบจริง (โมเดล/กฎ — ไม่ใช่ค่าเดิมของบรรทัดที่ส่งคืนมา) · รหัสอยู่ในผังของบริษัทนี้ ·
/// ความมั่นใจ ≥ <see cref="MinApplyConfidence"/> · คำตอบไม่ใช่ tier-2 ไร้อินพุต (DOCTRINE §2.5 ห้ามใช้ HasModelAnswer เดี่ยว ๆ —
/// เพดาน 0.45 ของนักเรียน generic ต่ำกว่าเกณฑ์นี้อยู่แล้ว)</para>
/// </summary>
public static class GlSuggestionApplyPolicy
{
    /// <summary>ความมั่นใจขั้นต่ำที่ยอมเติมผังให้เอง (เท่าเกณฑ์ write-gate ของ DOCTRINE §2.3)</summary>
    public const decimal MinApplyConfidence = 0.70m;

    /// <param name="accountCode">รหัสผังที่ได้รับคำแนะนำ</param>
    /// <param name="confidence">ความมั่นใจของคำแนะนำ</param>
    /// <param name="hasAnswer">มีผู้ตอบจริง (AI · นักเรียน · กฎ) — ค่าเดิมของบรรทัดที่ส่งกลับมาไม่นับ</param>
    /// <param name="inChartOfAccounts">รหัสอยู่ในผังที่ใช้งานของบริษัทนี้ (candidate set)</param>
    public static bool MayAutoFill(string? accountCode, decimal? confidence, bool hasAnswer, bool inChartOfAccounts)
        => hasAnswer
           && inChartOfAccounts
           && !string.IsNullOrWhiteSpace(accountCode)
           && (confidence ?? 0m) >= MinApplyConfidence;
}
