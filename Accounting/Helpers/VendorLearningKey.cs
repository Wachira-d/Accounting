namespace Accounting.Helpers;

/// <summary>
/// **คีย์ผู้ขายที่ตัวเรียนรู้ทุกตัวใช้ร่วมกัน** (pure, ไม่มี I/O)
///
/// ═══ ทำไมต้องมีที่เดียว (ผลตรวจ 2026-09-06 · T3-09) ═══
/// สูตรเดียวกันถูก<b>คัดลอกไปเขียนใหม่ 6 ที่</b> (VendorIntelligenceService ·
/// ExpenseCategoryLearner · OcrFullReviewDistillationModel · OcrController ·
/// OcrService ฝั่งเขียน · OcrService ฝั่งอ่าน) — และมี **1 ที่ที่ลืมด่าน 13 หลัก**:
/// ฝั่ง<b>อ่าน</b>ของตัวเรียนรู้ข้ามผู้เช่า (federated) สร้างคีย์เป็น
/// <c>tax:{ตัวเลขที่อ่านได้}</c> ทันทีที่มีเลขอะไรก็ตาม ส่วนฝั่ง<b>เขียน</b>ยอมใช้
/// <c>tax:</c> เฉพาะเลขครบ 13 หลัก ไม่งั้นตกไปใช้ <c>name:</c>
///
/// <para>ผล: ทุกใบที่ OCR อ่านเลขผู้เสียภาษีไม่ครบ (ซึ่งเป็นเคสที่พบบ่อยมาก —
/// ตัวเลขติดตราประทับ/เส้นตาราง) ฝั่งอ่านไปหา <c>tax:0105556</c> ที่<b>ไม่มีใครเคย
/// เขียน</b> ⇒ ความรู้ที่สะสมมาจากผู้เช่าคนอื่นไม่เคยถูกใช้เลยสำหรับใบกลุ่มนั้น
/// — defect class "ฝั่งเขียนกับฝั่งอ่านใช้คีย์คนละชุด" ที่ CLAUDE.md บันทึกไว้แล้ว
/// (FieldConfidence 3 ชุด) เกิดซ้ำอีกรอบ</para>
///
/// <para>กติกา: <b>เลขผู้เสียภาษี 13 หลักชนะชื่อเสมอ</b> (ชื่อสะกดต่างได้ เลขไม่ต่าง)
/// · เลขไม่ครบ 13 = ถือว่า<b>ไม่มีเลข</b> ไม่ใช่ "มีเลขบางส่วน" (เลขบางส่วนคือ
/// ผลอ่านที่ผิด ไม่ใช่ข้อมูลที่ใช้ได้ — "ไม่รู้ = บอกว่าไม่รู้")</para>
/// </summary>
public static class VendorLearningKey
{
    /// <summary>คืนคีย์ หรือสตริงว่างเมื่อไม่มีทั้งเลขภาษีที่ใช้ได้และชื่อ</summary>
    public static string For(string? taxId, string? name)
    {
        var digits = ThaiTaxId.Normalize(taxId);
        if (digits.Length == 13) return "tax:" + digits;
        return string.IsNullOrWhiteSpace(name) ? "" : "name:" + name.Trim().ToLowerInvariant();
    }

    /// <summary>คีย์จากชื่ออย่างเดียว (ตัวป้อน seed ที่ไม่มีเลขภาษี)</summary>
    public static string ForName(string? name)
        => string.IsNullOrWhiteSpace(name) ? "" : "name:" + name.Trim().ToLowerInvariant();
}
