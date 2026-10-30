namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 214 (คำตัดสินข้อ 173) — ฉบับของเอกสารทางกฎหมาย<b>ระดับแพลตฟอร์ม</b> (นโยบายความเป็นส่วนตัว · ข้อกำหนดการใช้งาน ของ NextAcc)
/// ที่ SystemAdmin แก้ได้ที่ <c>/admin/legal.html</c>
///
/// <para><b>Append-only หลังเผยแพร่</b>: แถว <c>Published</c> ห้ามแก้/ลบ (ตัวตัดสิน <c>Helpers/LegalDocumentPolicy.CanEdit</c>) —
/// มันคือหลักฐานว่าผู้ใช้เห็นข้อความอะไรตอนกดยอมรับฉบับนั้น (PDPA ม.19 ภาระพิสูจน์ · <c>PdpaConsentRecord.PolicyVersion</c> อ้างเลขฉบับนี้) ·
/// แก้ = ออกฉบับใหม่ · ร่าง (<c>Draft</c>) มีได้ฉบับเดียวต่อชนิด</para>
/// <para>ไม่มีแถว = ใช้ฉบับตั้งต้นในโค้ด (<c>Helpers/LegalDocumentDefaults</c> 1.0) — พฤติกรรมเดิมก่อนรอบ 214</para>
/// </summary>
public class LegalDocumentVersion : BaseEntity
{
    /// <summary>"privacy" | "terms" (<c>LegalDocumentDefaults.Kinds</c>)</summary>
    public string Kind { get; set; } = "";

    /// <summary>เลขฉบับ เช่น "1.1" — ไม่ซ้ำต่อชนิด (แถวที่ยังไม่ลบ) · ร่างอาจยังไม่มีเลขที่ถูกต้องจนกว่าจะเผยแพร่</summary>
    public string Version { get; set; } = "";

    /// <summary>วันที่มีผล (วันปฏิทินไทย · เก็บ ค.ศ. เวลา 00:00) — อนาคตได้ (ประกาศล่วงหน้า)</summary>
    public DateTime EffectiveDate { get; set; }

    /// <summary>เนื้อความ markdown ชุดย่อย (<c>Helpers/LegalMarkdown</c>) พร้อมโทเคน <c>{{controller.*}}</c></summary>
    public string BodyMarkdown { get; set; } = "";

    /// <summary>สรุปสิ่งที่เปลี่ยน (แสดงในประวัติฉบับ + แถบแจ้งผู้ใช้)</summary>
    public string? ChangeSummary { get; set; }

    /// <summary>"Draft" | "Published" (<c>LegalDocumentPolicy.Status*</c>)</summary>
    public string Status { get; set; } = "Draft";

    public DateTime? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }
}
