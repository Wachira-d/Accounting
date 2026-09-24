namespace Accounting.Helpers;

/// <summary>สัญญาณของสแกนหนึ่งใบที่ใช้จัดลำดับ "คิวรอตรวจ (OCR)"</summary>
/// <param name="Confidence">ความมั่นใจรวมของสแกน (0–1) — ค่านอกช่วงถูกบีบเข้า 0–1</param>
/// <param name="HasMatchedContact">จับคู่ผู้ติดต่อในระบบได้แล้วหรือยัง</param>
/// <param name="VendorSeenCount">จำนวนเอกสารที่อนุมัติแล้วของผู้ติดต่อนี้ใน 12 เดือน (0 ถ้ายังไม่จับคู่)</param>
/// <param name="IsRecent">สแกนภายใน 7 วัน</param>
/// <param name="HasPotentialFixedAsset">มีบรรทัดที่อาจเป็นสินทรัพย์ถาวร รอผู้ใช้ตัดสิน</param>
/// <param name="HasHandwriting">มีลายมือเขียนบนกระดาษ (ยอดเงินอาจอ่านผิด)</param>
/// <param name="IsDuplicate">ระบบสงสัยว่าซ้ำกับสแกนเดิม</param>
/// <param name="UserCorrected">ผู้ใช้แก้ค่าที่ระบบเติมไปแล้วอย่างน้อยหนึ่งช่อง</param>
public readonly record struct OcrReviewQueueSignals(
    decimal Confidence,
    bool HasMatchedContact,
    int VendorSeenCount,
    bool IsRecent,
    bool HasPotentialFixedAsset,
    bool HasHandwriting,
    bool IsDuplicate,
    bool UserCorrected);

/// <summary>ผลการให้คะแนนของสแกนหนึ่งใบ — <see cref="Reasons"/> เป็นข้อความไทยที่หน้าเว็บแสดงตรง ๆ</summary>
public sealed record OcrReviewQueueScore(
    decimal Uncertainty,
    decimal Novelty,
    decimal RecencyFactor,
    decimal Boost,
    decimal Priority,
    IReadOnlyList<string> Reasons);

/// <summary>
/// **สูตรจัดลำดับคิวรอตรวจ (OCR) — ตัวเดียวของระบบ**
///
/// <para>═══ มีไว้ทำไม ═══ หน้า <c>review-queue.html</c> คือ "ที่รวมสแกนที่ยังรอคนตัดสิน"
/// (ยังไม่ได้สร้างเอกสาร/JE หรือมีสินทรัพย์รอลงทะเบียน) เรียงให้ใบที่ "แก้แล้วระบบได้เรียนมากสุด"
/// ขึ้นก่อน — ผลตรวจ 2026-09-06 T4-14 ต่อสายหน้าเข้าเมนูแล้ว แต่สูตรเดิมฝังอยู่ใน
/// <c>ActiveLearningRanker</c> ไม่มีเทสต์ และเหตุผลเป็นภาษาอังกฤษ ("uncertain (40%)",
/// "new vendor", "stale") ที่ผู้ใช้อ่านไม่ออก (เจ้าของรายงานรอบ 190 ข้อ 1)</para>
///
/// <para>═══ สูตร ═══ <c>priority = uncertainty × (1 + novelty) × recency × boost</c>
/// <list type="bullet">
/// <item>uncertainty = 1 − confidence (ระบบไม่มั่นใจ = คำตอบของคนมีค่ามาก)</item>
/// <item>novelty = 1 / (1 + จำนวนเอกสารที่อนุมัติแล้วของผู้ขายนี้) — ผู้ขายใหม่เรียนได้มากสุด</item>
/// <item>recency = 1 (ภายใน 7 วัน) / 0.5 (เก่ากว่า)</item>
/// <item>boost = ×1.3 สินทรัพย์รอตัดสิน · ×1.3 มีลายมือ (เดิม entity เขียนไว้ว่าลายมือ
///   "ทำให้คิวดันขึ้นก่อน" แต่สูตรไม่เคยอ่านธงนี้ — มี ≠ ถูกเรียก) · ×0.5 ผู้ใช้แก้แล้ว
///   (ระบบได้บทเรียนจากใบนี้ไปแล้ว เหลือแค่งานสร้างเอกสาร จึงไม่ควรแย่งที่ใบที่ยังไม่มีใครแตะ)</item>
/// </list></para>
///
/// <para>⚠️ ลำดับของใบที่ไม่มีธงเสริมใด ๆ <b>เหมือนสูตรเดิมทุกประการ</b> (ล็อกด้วยเทสต์) —
/// เพิ่มวิธีคิด ไม่รื้อ (หลักการรอบ 190)</para>
/// </summary>
public static class OcrReviewQueuePriority
{
    public const decimal StaleFactor = 0.5m;
    public const decimal AssetBoost = 1.3m;
    public const decimal HandwritingBoost = 1.3m;
    public const decimal UserCorrectedFactor = 0.5m;
    /// <summary>ความไม่มั่นใจตั้งแต่ค่านี้ขึ้นไปจึงแสดงเป็นเหตุผล</summary>
    public const decimal UncertaintyHintThreshold = 0.4m;

    public static OcrReviewQueueScore Score(OcrReviewQueueSignals s)
    {
        var conf = Math.Clamp(s.Confidence, 0m, 1m);
        var uncertainty = 1m - conf;
        var seen = s.HasMatchedContact ? Math.Max(0, s.VendorSeenCount) : 0;
        var novelty = 1m / (1m + seen);
        var recency = s.IsRecent ? 1m : StaleFactor;

        var boost = 1m;
        if (s.HasPotentialFixedAsset) boost *= AssetBoost;
        if (s.HasHandwriting) boost *= HandwritingBoost;
        if (s.UserCorrected) boost *= UserCorrectedFactor;

        var priority = uncertainty * (1m + novelty) * recency * boost;

        var reasons = new List<string>();
        if (uncertainty >= UncertaintyHintThreshold)
            reasons.Add($"ระบบไม่มั่นใจ {Math.Round(uncertainty * 100m, 0, MidpointRounding.AwayFromZero)}%");
        if (!s.HasMatchedContact)
            reasons.Add("ยังจับคู่ผู้ติดต่อไม่ได้");
        else if (seen == 0)
            reasons.Add("ผู้ขายใหม่ (ยังไม่มีเอกสารที่อนุมัติ)");
        else if (seen <= 3)
            reasons.Add($"เคยอนุมัติเอกสารของผู้ขายนี้ {seen} ใบ");
        if (s.HasPotentialFixedAsset) reasons.Add("อาจเป็นสินทรัพย์ถาวร — รอตัดสิน");
        if (s.HasHandwriting) reasons.Add("มีลายมือเขียน — ตรวจยอดด้วยตา");
        if (s.IsDuplicate) reasons.Add("อาจซ้ำกับสแกนเดิม");
        if (s.UserCorrected) reasons.Add("แก้ข้อมูลแล้ว — รอสร้างเอกสาร");
        if (!s.IsRecent) reasons.Add("สแกนเกิน 7 วันแล้ว");

        return new OcrReviewQueueScore(uncertainty, novelty, recency, boost, priority, reasons);
    }
}
