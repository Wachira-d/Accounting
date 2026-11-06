namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 245 L1 (<c>erp-review/2026-11-06/LINE_MULTICHANNEL_DESIGN.md</c> §4.1 · คำตอบเจ้าของ §10) — ปลายทางแจ้งเตือนของ<b>ผู้ติดต่อ</b>
/// (ผู้ติดต่อเป็นเจ้าของปลายทาง · ทุกเส้นที่ส่งถึงผู้ติดต่ออ่านผ่าน <c>IContactRecipientResolver</c> ตัวเดียว)
///
/// <para>ชนิด (<c>Helpers/NotifyEndpointKind</c>): <c>Email</c> · <c>LineUser</c> · <c>LineGroup</c> · แต่ละแถวเลือกเรื่องที่รับ (<c>TopicsJson</c> = ชื่อ enum
/// <c>NotifyTopic</c>) · ใช้งานอยู่ = <c>UnlinkedAt == null &amp;&amp; BlockedAt == null</c> (ยกเลิกแล้วผูกใหม่ = แถวใหม่)</para>
///
/// <para><b><c>Contact.Email</c> ยังเป็นอีเมลหลัก</b> (ไม่คัดลอกมาเก็บซ้ำ · §4.3): แถว <c>IsPrimary = true</c> เก็บเฉพาะ "เรื่องที่อีเมลหลักรับ" โดย <c>Email = null</c>
/// — ที่อยู่อ่านสดจาก <c>Contact.Email</c> ทุกครั้ง ⇒ หน้าแก้ไขผู้ติดต่อเปลี่ยนอีเมลแล้วปลายทางตามทันที · ไม่มีแถวหลัก = อีเมลหลักรับทุกเรื่อง (พฤติกรรมเดิม)</para>
///
/// <para>PDPA: <c>LineUserId</c>/<c>LineGroupId</c> เข้ารหัสด้วย <c>ISecretProtector</c> (ตัวระบุบุคคล · ห้าม log เต็ม) · <c>LineIdHash</c> ใช้ค้นตอน webhook ·
/// ปลายทาง LINE ต้องมี <c>ConsentRecordId</c> (1:1 = ผู้รับยินยอมเอง · กลุ่ม = ผู้ดูแลบริษัทรับรองสิทธิ์ของสมาชิก) · ลบตามคำขอ (DSR) ⇒ ปลดทุกแถว + ล้าง id</para>
/// </summary>
public class ContactNotifyEndpoint : TenantEntity
{
    public Guid ContactId { get; set; }
    /// <summary><c>NotifyEndpointKind.Email/LineUser/LineGroup</c></summary>
    public string Kind { get; set; } = "";
    /// <summary>ป้ายที่ผู้ใช้ตั้ง เช่น "ฝ่ายบัญชี"</summary>
    public string? Label { get; set; }
    /// <summary>Kind=Email ที่ไม่ใช่อีเมลหลักเท่านั้น (อีเมลหลักอ่านจาก <c>Contact.Email</c>)</summary>
    public string? Email { get; set; }
    /// <summary>ผู้ใช้พอร์ทัลที่ผูก (Kind=LineUser · เฟส L5)</summary>
    public Guid? PortalAccessId { get; set; }
    /// <summary>LINE userId — <b>ค่าที่เข้ารหัสแล้ว</b> (<c>ISecretProtector.Protect</c>)</summary>
    public string? LineUserId { get; set; }
    /// <summary>LINE groupId — <b>ค่าที่เข้ารหัสแล้ว</b></summary>
    public string? LineGroupId { get; set; }
    /// <summary>SHA-256 ของ (OaDestination + id) — <c>NotifyEndpointKind.LineIdHash</c></summary>
    public string? LineIdHash { get; set; }
    /// <summary><c>nextacc</c> · <c>company</c> · <c>unknown</c> (แถว legacy — ไม่ใช้ส่ง) · <c>""</c> = อีเมล</summary>
    public string OaScope { get; set; } = "";
    /// <summary>bot userId ของ OA ที่รับการผูก</summary>
    public string? OaDestination { get; set; }
    public string? GroupNameSnapshot { get; set; }
    /// <summary>จำนวนสมาชิกกลุ่ม (นับโควตาตามจำนวนสมาชิก · ผูกได้ ≤ 200 คน — คำตอบเจ้าของ Q2)</summary>
    public int? MemberCountSnapshot { get; set; }
    /// <summary>JSON array ชื่อ <c>NotifyTopic</c> — ตัวอ่าน/เขียนเดียว <c>NotifyTopics.Parse/Serialize</c></summary>
    public string TopicsJson { get; set; } = "[]";
    /// <summary>กลุ่ม LINE: แสดงยอดเงินในการ์ด (ค่าตั้งต้น true — คำตอบเจ้าของ Q5 · ปิดได้รายกลุ่ม) · ชนิดอื่นไม่มีผล</summary>
    public bool ShowAmounts { get; set; } = true;
    /// <summary>แถวค่าตั้งของอีเมลหลัก (<c>Contact.Email</c>) — มีได้แถวเดียวต่อผู้ติดต่อ</summary>
    public bool IsPrimary { get; set; }
    /// <summary>กลุ่มที่ผูกด้วยรหัส = true · แถว legacy = false</summary>
    public bool Verified { get; set; }
    /// <summary><c>NotifyEndpointKind.Source*</c></summary>
    public string Source { get; set; } = "";
    /// <summary>แถว <c>PdpaConsentRecord</c> (บังคับสำหรับ LINE)</summary>
    public Guid? ConsentRecordId { get; set; }
    public Guid? BoundByUserId { get; set; }
    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
    /// <summary>ผู้รับบล็อก OA (event unfollow) · follow กลับ ⇒ ล้าง</summary>
    public DateTime? BlockedAt { get; set; }
    /// <summary>ยกเลิกถาวร (ผูกใหม่ = แถวใหม่)</summary>
    public DateTime? UnlinkedAt { get; set; }
    /// <summary><c>NotifyEndpointKind.Unlink*</c></summary>
    public string? UnlinkReason { get; set; }
    public DateTime? LastSentAt { get; set; }
    public string? LastFailure { get; set; }
}
