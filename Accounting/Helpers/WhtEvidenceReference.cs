using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 253 (WP-10 · AUDIT_249 §3.3 N4 · EWHT_EVIDENCE_RESEARCH §4) — <b>หลักฐาน e-WHT ขั้นแรก: แนบเป็นใบ 50 ทวิ ธรรมดา</b> (pure)
///
/// <para>ยังไม่รู้รูปแบบหลักฐานที่เจ้าพนักงานรับ (Q-E1) ⇒ <b>ไม่แต่ง</b>: ไม่มีอัตรา 1% อัตโนมัติ (§2 #32) · ไม่ตีความเลขอ้างอิง · ด่าน/การจับคู่/การยืนยัน
/// เหมือนใบกระดาษทุกข้อ · ต่างแค่ป้ายชนิดหลักฐาน (<see cref="WhtEvidenceKind.EWithholding"/>) + ข้อความเลขอ้างอิงที่ผู้ใช้กรอก (ถ้ามี)</para>
/// </summary>
public static class WhtEvidenceReference
{
    public const int MaxLength = 100;

    /// <summary>เลขอ้างอิงที่เก็บ — ตัดช่องว่าง/อักขระควบคุม · ยาวเกิน ⇒ ตัด · ว่าง ⇒ null (ไม่แต่งค่า)</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > MaxLength ? clean[..MaxLength] : clean;
    }

    /// <summary>ชนิดหลักฐานจากตัวเลือกบนฟอร์มอัปโหลด — ติ๊ก "หลักฐาน e-WHT" ⇒ EWithholding · ไม่ติ๊ก ⇒ ใบกระดาษ (เลขอ้างอิงอย่างเดียวไม่เปลี่ยนชนิด)</summary>
    public static WhtEvidenceKind KindFor(bool eWithholding) => eWithholding ? WhtEvidenceKind.EWithholding : WhtEvidenceKind.PaperCertificate;

    /// <summary>ป้ายบนกล่องรับ — e-WHT ⇒ "หลักฐาน e-WHT" (+ เลขอ้างอิง) · อื่น ๆ ⇒ null (ไม่มีป้ายเพิ่ม)</summary>
    public static string? Label(WhtEvidenceKind kind, string? reference)
        => kind != WhtEvidenceKind.EWithholding ? null
            : string.IsNullOrWhiteSpace(reference) ? "หลักฐาน e-WHT" : $"หลักฐาน e-WHT · อ้างอิง {reference}";
}
