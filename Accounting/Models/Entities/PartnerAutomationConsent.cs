namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 264 P1 (คำตัดสิน 264-2-1…3) — ความยินยอมของบริษัท <see cref="TenantEntity.CompanyId"/> (<b>ฝั่งที่ถูกลงบัญชีเสมอ</b>) ให้<b>ระบบของตัวเอง</b>ทำงานอัตโนมัติ
/// กับคู่ค้า NextAcc หนึ่งราย ต่อหนึ่งขอบเขต (<see cref="Scope"/> = ชื่อ <c>Helpers.PartnerConsent.Scope</c>) ·
/// <b>append-only</b>: แถวที่ให้แล้วไม่ถูกแก้ทับ — เปลี่ยนเพดาน/บัญชี = ถอนแถวเดิม + แถวใหม่ · การเปลี่ยนเดียวที่ทำได้คือประทับ "ถอน" ครั้งเดียว
/// (trigger ใน <c>Helpers/PartnerConsentMigration</c> กันระดับฐาน) · ตัวตัดสินเดียว <c>Helpers/PartnerConsent</c> · ผู้เขียนเดียว <c>PartnerConsentService</c>
/// </summary>
public class PartnerAutomationConsent : TenantEntity
{
    public Guid TradingPartnershipId { get; set; }
    /// <summary>บริษัทคู่ค้า (อีกฝั่งของ <see cref="TradingPartnershipId"/>) — เก็บไว้ให้ค้น/แสดงโดยไม่ต้องคำนวณฝั่ง</summary>
    public Guid PartnerCompanyId { get; set; }
    /// <summary>ชื่อ enum <c>PartnerConsent.Scope</c> (ไม่ใช่ตัวเลข)</summary>
    public string Scope { get; set; } = "";
    /// <summary>เพดานต่อใบ (บาท) · null = ไม่จำกัด (ขอบเขตที่ลงบัญชีอัตโนมัติบังคับต้องมี — <c>PartnerConsent.Def(scope).RequiresCap</c>)</summary>
    public decimal? MaxAmount { get; set; }
    /// <summary>เฉพาะ <c>AutoRecordPayment</c>: "จ่ายคู่ค้ารายนี้จากบัญชี X" (ประกาศของคน) · null = ไม่ตั้ง — ใช้หลักฐานเท่านั้น</summary>
    public Guid? DefaultPayBankAccountId { get; set; }
    /// <summary>เวอร์ชันข้อความยินยอมที่ผู้ให้เห็นจริง (<c>PartnerConsent.TextFor</c>)</summary>
    public int TextVersion { get; set; }
    /// <summary>SHA-256 ของ (บริษัท · คู่ · ขอบเขต · เพดาน · บัญชี · เวอร์ชัน · ข้อความ) จาก <c>PartnerConsent.Hash</c> ตัวเดียว</summary>
    public string EvidenceHash { get; set; } = "";
    public Guid GrantedByUserId { get; set; }
    public DateTime GrantedAt { get; set; }
    public Guid? WithdrawnByUserId { get; set; }
    public DateTime? WithdrawnAt { get; set; }
    public string? WithdrawReason { get; set; }
}
