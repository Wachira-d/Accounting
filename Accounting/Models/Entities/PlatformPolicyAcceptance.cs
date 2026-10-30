namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 214 (หลัง CI · คำตัดสินข้อ 173 ข้อ ฌ) — หลักฐานการยอมรับ<b>ข้อกำหนดการใช้งาน + นโยบายความเป็นส่วนตัวของแพลตฟอร์ม</b> ระดับ<b>ผู้ใช้</b>
/// (ตอนสมัคร · ตอนกด "รับทราบและยอมรับ" ฉบับใหม่)
///
/// <para><b>ทำไมไม่ใช้ <see cref="PdpaConsentRecord"/></b>: นั่นเป็น <see cref="TenantEntity"/> (ความยินยอมที่<b>ลูกค้าของบริษัท</b>ให้บริษัท ·
/// FK → Companies) — เดิมเขียนด้วย <c>CompanyId = Guid.Empty</c> ⇒ ฐานที่สร้างจากโมเดล (EnsureCreated · มี FK) ปฏิเสธทุกแถว
/// (สมัครสมาชิก: AuthService จับ error แล้ว log ⇒ หลักฐานหายเงียบ · แถบยอมรับฉบับใหม่: 500) · ผู้ใช้ไม่จำเป็นต้องมีบริษัท
/// (สมัครใหม่ · รับคำเชิญ · SystemAdmin) และผู้ควบคุมข้อมูลคือผู้ให้บริการ ไม่ใช่ tenant ⇒ ตารางระดับแพลตฟอร์มแยก ไม่มี CompanyId</para>
/// <para>append-only (ถอนความยินยอม = <see cref="WithdrawnAt"/> ไม่ลบแถว) · แถวเดิมใน PdpaConsentRecords (CompanyId ศูนย์) ถูกคัดลอกมาด้วย Id เดิม
/// (<c>Helpers/LegalDocumentMigration</c>)</para>
/// </summary>
public class PlatformPolicyAcceptance : BaseEntity
{
    public Guid UserId { get; set; }
    /// <summary>อีเมล ณ ตอนยอมรับ (ส่วนหนึ่งของ canonical ของ <see cref="EvidenceHash"/>)</summary>
    public string Email { get; set; } = "";
    /// <summary><c>PdpaPolicy.SignupPurpose</c> | <c>PdpaPolicy.PolicyUpdatePurpose</c></summary>
    public string Purpose { get; set; } = "";
    /// <summary>เวอร์ชันความยินยอม (<c>LegalDocumentPolicy.ConsentVersion</c>) ที่ระบบใช้ ณ ตอนยอมรับ</summary>
    public string PolicyVersion { get; set; } = "";
    public DateTime AcceptedAt { get; set; }
    public string? Channel { get; set; }
    public string? IpAddress { get; set; }
    /// <summary><c>PdpaConsentEvidence.ComputeHash</c> — canonical ตัวเดียวกับเดิม</summary>
    public string? EvidenceHash { get; set; }
    public DateTime? WithdrawnAt { get; set; }

    // ── รอบ 214 ต่อ (คำตัดสินข้อ 173 ฎ): ทุกช่องที่อยู่ใน canonical ของ EvidenceHash ต้องเก็บไว้ ไม่งั้นตรวจกลับไม่ได้ (round-trip) ──
    /// <summary>เวอร์ชันที่หน้าเว็บแสดงตอนกด (หน้าสมัครส่งกลับมา · อาจต่างจาก <see cref="PolicyVersion"/> เมื่อเบราว์เซอร์ค้าง cache)</summary>
    public string? VersionShown { get; set; }
    /// <summary>user-agent (ตัดที่ <c>PlatformPolicyAcceptanceRules.MaxUserAgent</c> ก่อน hash)</summary>
    public string? UserAgent { get; set; }
    /// <summary>ลบข้อมูลตามคำขอ (ม.33) แล้ว — อีเมล/IP/UA ถูกทำให้ไม่ระบุตัว · แถวคงไว้เป็นหลักฐานตามอายุความ
    /// (legal hold · <c>PlatformPolicyAcceptanceRules</c>) แล้วลบจริงเมื่อพ้นกำหนด</summary>
    public DateTime? PseudonymisedAt { get; set; }
}
