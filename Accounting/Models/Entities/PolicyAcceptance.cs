namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 219 ส่วน B (คำตัดสินข้อ 179) — <b>หลักฐานการติ๊ก "ยอมรับข้อกำหนด + นโยบายการยกเลิกและคืนเงิน" ก่อนชำระเงิน/ยืนยันการซื้อ</b>
/// (append-only · ไม่แก้ไม่ลบ) · หนึ่งแถวต่อการซื้อหนึ่งครั้ง
/// <list type="bullet">
/// <item><c>Scope = "shop"</c>: ลูกค้าของร้านสั่งซื้อ/จองผ่านเว็บร้าน — <see cref="TenantEntity.CompanyId"/> = ร้าน (ผู้ขาย) · ฉบับ = <c>CmsLegalPages.ShopPolicyVersion</c></item>
/// <item><c>Scope = "platform"</c>: ผู้เช่าชำระค่าบริการ NextAcc — <see cref="TenantEntity.CompanyId"/> = บริษัทผู้จ่าย · ฉบับ = <c>LegalDocumentPolicy.PurchasePolicyVersion</c></item>
/// </list>
/// <para><b>ทำไมไม่ใช้ <see cref="PdpaConsentRecord"/></b>: นั่นคือความยินยอม PDPA (ถอนได้ · งานล้างตามอายุของ PDPA) — นี่คือ<b>เงื่อนไขสัญญาซื้อขาย</b>
/// ที่ต้องพิสูจน์ได้ว่าผู้ซื้อเห็นฉบับใด ณ เวลาซื้อ (ภาระพิสูจน์ของผู้ขาย) · ถอนไม่ได้ · เก็บตามอายุเอกสารบัญชีของการซื้อนั้น (5 ปี · กฎ M MAX retention)</para>
/// <para>hash หลักฐาน = <c>PdpaConsentEvidence.ComputeHash</c> (canonical v2 ตัวเดียวของระบบ) · ผู้เขียนตัวเดียว <c>Helpers/PurchasePolicyAcceptance.Create</c></para>
/// </summary>
public class PolicyAcceptance : TenantEntity
{
    /// <summary>"shop" | "platform" (<c>PurchasePolicyAcceptance.ScopeShop/ScopePlatform</c>)</summary>
    public string Scope { get; set; } = "";
    /// <summary>ทางเข้า เช่น "site-order" · "site-booking" · "lodging" · "platform-subscription" · "platform-addon"</summary>
    public string Channel { get; set; } = "";
    /// <summary>แถวต้นทาง (ออเดอร์ · การจอง · รายการชำระ · สิทธิ์ส่วนเสริม) — null = ยังไม่มีแถว ณ ตอนติ๊ก</summary>
    public Guid? SourceId { get; set; }
    public Guid? SiteId { get; set; }
    /// <summary>ฉบับที่เซิร์ฟเวอร์ใช้ ณ ตอนยอมรับ</summary>
    public string PolicyVersion { get; set; } = "";
    /// <summary>ฉบับที่หน้าเว็บแสดงตอนกด (หน้าเว็บส่งกลับมา · null = หน้าเก่าที่ไม่ได้ส่ง — ยอมรับฉบับปัจจุบัน)</summary>
    public string? VersionShown { get; set; }
    /// <summary>ผู้ยอมรับ — อีเมล/เบอร์ของลูกค้าร้าน หรืออีเมลผู้ใช้ระบบ (ส่วนหนึ่งของ canonical)</summary>
    public string? Subject { get; set; }
    public Guid? UserId { get; set; }
    public DateTime AcceptedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? EvidenceHash { get; set; }
}
