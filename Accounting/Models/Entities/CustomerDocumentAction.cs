namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 267 แพ็ก P2 — <b>บัญชีหลักฐานการตอบรับเอกสารของลูกค้า</b> (append-only · คำตัดสิน 267-1-4 · DOCUMENT_FLOW §2.12n-P2):
/// หนึ่งแถวต่อการกระทำของผู้ถือลิงก์ (ยอมรับ · ปฏิเสธ · ขอแก้ · เซ็นรับสินค้า) — คอลัมน์เดิมบนใบ (<c>QuotationAccept*</c> ·
/// <c>DeliverySigned*</c> · <c>CustomerResponse</c>) เป็นภาพฉายที่เขียนในธุรกรรมเดียวกัน (ผู้เขียนเดียว <c>CustomerDocumentResponseService</c>)
///
/// <para>แก้/ลบไม่ได้ (trigger ใน <c>Helpers/CustomerSignMigration</c>) ยกเว้นสองกรณี: (1) เติม <c>SignedPdf*</c> ครั้งเดียวหลังสร้างฉบับที่ลูกค้าเซ็น
/// (2) ลบข้อมูลส่วนบุคคลตามคำขอเมื่อพ้นอายุเก็บ (ประทับ <c>ErasedAt</c> — hash/เวลา/ชนิดคงไว้) ·
/// น้ำหนักทางกฎหมาย: พ.ร.บ.ว่าด้วยธุรกรรมทางอิเล็กทรอนิกส์ พ.ศ. 2544 ม.9 (ลายมือชื่อ) · ม.11 (น้ำหนักพยาน) เท่านั้น</para>
/// </summary>
public class CustomerDocumentAction : TenantEntity
{
    public Guid DocumentId { get; set; }
    /// <summary>เลข Rev ของใบที่ผู้ลงนามเห็น ณ ตอนกด (หนึ่งผู้ลงนามต่อ Rev — 267-1-11)</summary>
    public int RevisionNumber { get; set; }
    /// <summary>ชื่อ <c>CustomerSignRule.Kind</c>: Accepted · Declined · ChangeRequested · DeliverySigned · PoAcknowledged · PoDeclined (รอบ 267 P5)</summary>
    public string Action { get; set; } = "";
    /// <summary>ทางที่มา: Link (ลิงก์หน้าเอกสาร) · LegacyLink (ลิงก์ยอมรับ/เซ็นรับที่ออกก่อนรอบ 267) · Api (ระบบภายนอกผ่าน ExternalApprovalController — รอบ 267 P5)</summary>
    public string Channel { get; set; } = "";
    /// <summary>Full = ชื่อ+นามสกุล+ตำแหน่ง+ติ๊กรับรอง · NameOnly = ลิงก์รุ่นก่อน (พิมพ์ชื่ออย่างเดียว — ไม่แต่งช่องที่ไม่ได้เก็บ)</summary>
    public string IdentityLevel { get; set; } = "";

    public string? SignerFirstName { get; set; }
    public string? SignerLastName { get; set; }
    public string? SignerTitle { get; set; }
    /// <summary>ชื่อผู้ซื้อตามเอกสาร ณ ตอนลงนาม (ข้อความรับรอง "เป็นบุคคลของ …" อ้างชื่อนี้)</summary>
    public string? SignerOrgName { get; set; }
    public string? SignerEmail { get; set; }
    /// <summary>อีเมลผ่านรหัสยืนยันแล้ว (เฉพาะเมื่อบริษัทตั้งยอดขั้นต่ำ — 267-1-6) · false = ไม่ได้ยืนยัน (ไม่ใช่ "ยืนยันไม่ผ่าน")</summary>
    public bool EmailVerified { get; set; }

    /// <summary>รุ่นข้อความรับรองที่หน้าแสดง (<c>CustomerSignText.Version</c>) + SHA-256 ของข้อความที่ผู้ลงนามติ๊ก</summary>
    public string? AttestationVersion { get; set; }
    public string? AttestationTextHash { get; set; }

    /// <summary>ไฟล์รูปลายเซ็น (ที่เก็บกลาง นอก allow-list <c>/uploads</c>) + SHA-256 ของไบต์</summary>
    public string? SignatureFilePath { get; set; }
    public string? SignatureSha256 { get; set; }

    /// <summary>รหัสเหตุผล (ปฏิเสธ — รายการปิดใน <c>CustomerSignText.DeclineReasons</c>) + ข้อความของลูกค้า</summary>
    public string? ReasonCode { get; set; }
    public string? Message { get; set; }

    /// <summary><c>DocumentSignedContent.Hash</c> ของเนื้อหาที่ผู้ลงนามเห็น</summary>
    public string ContentHash { get; set; } = "";
    /// <summary>SHA-256 ของ token ที่ใช้ (ไม่เก็บ token)</summary>
    public string TokenHash { get; set; } = "";
    public string? Ip { get; set; }
    public string? UserAgentHash { get; set; }

    /// <summary>ฉบับที่ลูกค้าเซ็น (PDF ณ ตอนนั้น) — เติมครั้งเดียว · SHA-256 ของไฟล์ (ตรวจทุกครั้งที่ส่งไฟล์ออก)</summary>
    public string? SignedPdfPath { get; set; }
    public string? SignedPdfSha256 { get; set; }
    public DateTime? SignedPdfAt { get; set; }

    /// <summary>รอบ 267 P5 (S31): วันส่งของที่ผู้ขายยืนยันตอนรับทราบใบสั่งซื้อ (วันปฏิทิน · ไม่ก่อนวันที่ใบสั่งซื้อ) · ชนิดอื่น = null</summary>
    public DateTime? PromisedDate { get; set; }
    /// <summary>รอบ 267 P5 (267-1-13): โปรไฟล์ผู้ลงนาม (LINE Login) ที่เติมฟอร์มของคำตอบนี้ — อ้างที่มาของค่าที่เติมเท่านั้น (ไม่ใช่หลักฐานตัวตน · โปรไฟล์ถูกลบได้ แถวนี้คงอยู่)</summary>
    public Guid? SignerProfileId { get; set; }

    /// <summary>ลบข้อมูลส่วนบุคคลตามคำขอแล้วเมื่อ (PDPA ม.33 — หลังพ้นอายุเก็บ) · แถวยังอยู่เป็นร่องรอยว่ามีการกระทำ</summary>
    public DateTime? ErasedAt { get; set; }
}

/// <summary>รอบ 267 P2 (267-1-6): รหัสยืนยันอีเมลก่อนยอมรับใบเสนอราคายอดสูง — เก็บ hash เท่านั้น · อายุสั้น · ใช้ครั้งเดียว</summary>
public class CustomerSignOtp : TenantEntity
{
    public Guid DocumentId { get; set; }
    /// <summary>SHA-256 ของอีเมล (ตัวพิมพ์เล็ก) — ไม่เก็บอีเมลตรงในตารางรหัส</summary>
    public string EmailHash { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public DateTime? ConsumedAt { get; set; }
}
