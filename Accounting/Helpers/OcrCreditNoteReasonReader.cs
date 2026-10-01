using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **อ่าน "เหตุผลการลดหนี้" จากข้อความบนกระดาษ** (§86/10 บังคับระบุ) — pure, ไม่มี I/O
///
/// <para>═══ ที่มา (รอบ 201 ทีม OC · A-OC4 · team-K2 C-09 ส่วนที่เหลือ) ═══ ตัวตัดสินนี้เคยฝังเป็น
/// <c>OcrService.InferCreditNoteReason</c> โดยไม่มีเทสต์สักตัว (CLAUDE.md §H: "ตรรกะที่ยังฝังใน OcrService.cs คือที่ที่ถดถอยเกิดโดยไม่มี
/// อะไรฟ้อง") · ย้ายมา<b>ตรงตัว</b> (คำและลำดับเดิมทุกตัว — ใบที่เคยได้คำตอบไหนยังได้คำตอบเดิม) แล้วล็อกด้วยเทสต์สองครึ่ง
/// (<c>OcrReview201OcTests.CreditNoteReason_*</c>)</para>
///
/// <para>คืน null เมื่อไม่พบคำบ่งชี้ชัดเจน — ปล่อยให้ผู้ใช้เลือกเอง ดีกว่าเดาผิดแล้วลงบัญชีผิด (เฉพาะ "คืนสินค้า" เท่านั้นที่กระทบสต๊อก
/// อีก 3 แบบไม่กระทบ) · เรียงตามความจำเพาะ: คืนสินค้าเป็นเคสเดียวที่กระทบสต๊อก จึงต้องชัดจริงก่อน</para>
/// </summary>
public static class OcrCreditNoteReasonReader
{
    public static CreditNoteReason? Read(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;
        var t = rawText.ToLowerInvariant();
        if (t.Contains("คืนสินค้า") || t.Contains("รับคืนสินค้า") || t.Contains("สินค้าคืน")
            || t.Contains("goods return") || t.Contains("sales return"))
            return CreditNoteReason.Return;
        if (HasDiscountEvidence(t))
            return CreditNoteReason.Discount;
        if (t.Contains("ตัดหนี้สูญ") || t.Contains("หนี้สูญ") || t.Contains("write-off") || t.Contains("write off"))
            return CreditNoteReason.Writeoff;
        if (t.Contains("ปรับปรุงยอด") || t.Contains("ปรับยอด") || t.Contains("คลาดเคลื่อน")
            || t.Contains("ไม่ครบตามจำนวน") || t.Contains("adjustment"))
            return CreditNoteReason.Adjustment;
        return null;   // ไม่เดา — ผู้ใช้เลือกเองบนฟอร์ม
    }

    /// <summary>บรรทัดฟอร์มที่มีแต่ป้ายส่วนลดกับยอดศูนย์/ขีด ("ส่วนลด 0.00" · "Discount -") — ป้ายพิมพ์สำเร็จของแบบฟอร์ม ไม่ใช่เหตุผลการลดหนี้</summary>
    private static readonly System.Text.RegularExpressions.Regex ZeroDiscountRow = new(
        @"^[ \t]*(?:ส่วนลด|discount)[^0-9\n]{0,20}?(?:(?:0+(?:[.,]0+)?|-+)[ \t]*(?:บาท|baht|thb)?[ \t]*)+$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>คำบ่งชี้ "ส่วนลด" ที่เป็นหลักฐาน — รอบ 201 ทีม OC (คำตัดสินข้อ 103 Q3 · บทเรียน §H "แถวยอด 0 ไม่ใช่หลักฐาน" RG-02):
    /// กลบบรรทัดฟอร์ม "ส่วนลด 0.00" ก่อนค้น (เดิมใบลดหนี้ที่เหตุผลคือ "ปรับปรุงยอด" แต่ฟอร์มมีแถว "ส่วนลด 0.00" ได้ Discount) ·
    /// "ลดราคา" ไม่ใช่ป้ายแถวยอด — คงเดิม · ส่วนลดที่มียอดจริง/อยู่ในประโยคเหตุผล — คงเดิม</summary>
    private static bool HasDiscountEvidence(string lowered)
    {
        if (lowered.Contains("ลดราคา")) return true;
        var masked = ZeroDiscountRow.Replace(lowered.Replace("\r", ""), "");
        return masked.Contains("ส่วนลด") || masked.Contains("discount");
    }
}
