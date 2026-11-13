namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 267 แพ็ก P5 (คำตัดสินเจ้าของ 267-1-13) — <b>ข้อมูลผู้ลงนามที่ผู้ลงนามยินยอมให้จำ</b> ไว้กับบัญชี LINE สำหรับเอกสารของบริษัทนี้ (ต่อบริษัท — ไม่แชร์ข้ามบริษัท) ·
/// ชื่อ/นามสกุล/ตำแหน่ง/อีเมล เข้ารหัสขณะจัดเก็บ (<c>EncryptedColumnConverter</c> — วิธีเดียวกับ PII พนักงาน) · ไม่เก็บ LINE userId (เก็บ HMAC — <see cref="SubjectKey"/>) ·
/// ถอนความยินยอม = ลบแถวจริง · ไม่ได้ใช้ 24 เดือน = งานกลางคืนลบ · ไม่ใช่หลักฐานตัวตนของการลงนาม (ติ๊กรับรองยังต้องทำเองทุกครั้ง)
/// </summary>
public class CustomerSignerProfile : TenantEntity
{
    /// <summary><c>CustomerSignerProfileRule.SubjectKey</c> (HMAC ของช่อง LINE + userId) — หนึ่งแถวต่อบริษัทต่อคน</summary>
    public string SubjectKey { get; set; } = "";
    /// <summary>ผู้ติดต่อของเอกสารที่บันทึกครั้งล่าสุด (คู่ค้าที่ผู้ลงนามสังกัด — ใช้ค้นตอน DSR) · null = ไม่ทราบ</summary>
    public Guid? ContactId { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? Title { get; set; }
    public string? Email { get; set; }
    /// <summary>แถว <c>PdpaConsentRecord</c> ที่ให้ความยินยอม (Purpose <c>customer-signer-prefill</c>)</summary>
    public Guid ConsentRecordId { get; set; }
    /// <summary>เข้าด้วย LINE แล้วเติม/บันทึกครั้งล่าสุด — ไม่ได้ใช้ 24 เดือน ⇒ ลบ</summary>
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}
