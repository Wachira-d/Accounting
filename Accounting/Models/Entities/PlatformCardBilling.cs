namespace Accounting.Models.Entities;

// ═══════════════════════════════════════════════════════════════════════
// รอบ 230 (PLATFORM_OMISE_AUDIT §5.2 / §5.3 / §7.1) — บัตรที่บันทึกไว้ + ตัดบัตรอัตโนมัติ + รอบโอนจากผู้ให้บริการ ของ<b>ค่าบริการ NextAcc</b>
// ทุกตารางอยู่ระดับแพลตฟอร์ม (ไม่ใช่ TenantEntity): แถวเป็นของบริษัทผู้ให้บริการ (PlatformCompanyId) + บริษัทผู้จ่าย (PayerCompanyId) —
// แบบเดียวกับ PlatformPaymentLink · ทุก query กรองทั้งสองคีย์เอง (กฎ M tenant isolation) · schema = Helpers/PlatformCardBillingMigration
// PCI-DSS SAQ-A: <b>ไม่มีช่องเลขบัตร/CVV</b> — เก็บเฉพาะเลขอ้างอิงลูกค้า/บัตรของผู้ให้บริการ + ยี่ห้อ + 4 ตัวท้าย + วันหมดอายุ (ข้อมูลแสดงผล)
// ═══════════════════════════════════════════════════════════════════════

/// <summary>บัตรที่ผู้เช่าบันทึกไว้กับผู้ให้บริการรับชำระเงินของ NextAcc — หนึ่งแถวต่อบัตร (ลบ = ประทับ <see cref="RemovedAt"/> ไม่ลบแถว: หลักฐานว่าตัดด้วยบัตรไหน)</summary>
public class PlatformSavedCard : BaseEntity
{
    /// <summary>บริษัทผู้ให้บริการ (SiteSettings.PlatformCompanyId ณ ตอนบันทึก)</summary>
    public Guid PlatformCompanyId { get; set; }
    /// <summary>บริษัทผู้เช่าเจ้าของบัตร</summary>
    public Guid PayerCompanyId { get; set; }
    public string ProviderCode { get; set; } = "";
    public Guid? ProviderConfigId { get; set; }
    /// <summary>บันทึกด้วยคีย์โหมดทดสอบ — บัตรทดสอบห้ามถูกใช้ตัดเงินจริง (และกลับกัน)</summary>
    public bool IsTestMode { get; set; }
    /// <summary>เลขอ้างอิงลูกค้าของผู้ให้บริการ (ต่อบริษัทผู้จ่าย)</summary>
    public string CustomerRef { get; set; } = "";
    /// <summary>เลขอ้างอิงบัตรของผู้ให้บริการ — ไม่ใช่เลขบัตร</summary>
    public string CardRef { get; set; } = "";
    public string? Brand { get; set; }
    /// <summary>4 ตัวท้าย (แสดงผลเท่านั้น)</summary>
    public string? Last4 { get; set; }
    /// <summary>วันสุดท้ายที่บัตรใช้ได้ (วันสิ้นเดือนหมดอายุ ตามที่ผู้ให้บริการรายงาน) — null = ไม่ทราบ</summary>
    public DateTime? ValidUntil { get; set; }
    /// <summary>บัตรตั้งต้นที่ตัดอัตโนมัติ (หนึ่งใบต่อบริษัทผู้จ่าย)</summary>
    public bool IsDefault { get; set; }
    /// <summary>แถว PdpaConsentRecord (บริษัทผู้ให้บริการ) ของความยินยอมตอนบันทึก</summary>
    public Guid? ConsentRecordId { get; set; }
    /// <summary>รายการชำระครั้งแรกที่ใช้บัตรนี้ (ผ่าน 3-D Secure ตามธนาคาร) — สำเร็จ ⇒ <see cref="VerifiedAt"/></summary>
    public Guid? FirstIntentId { get; set; }
    /// <summary>ชำระครั้งแรกสำเร็จแล้ว — ตัดอัตโนมัติใช้ได้เฉพาะบัตรที่ยืนยันแล้ว</summary>
    public DateTime? VerifiedAt { get; set; }
    public DateTime? RemovedAt { get; set; }
    public string? RemovedBy { get; set; }
    public string? RemovedReason { get; set; }
    /// <summary>ฝ่ายค้าน S1: ผู้จ่ายนิติบุคคลรับทราบแยกว่าการตัดอัตโนมัติไม่หักภาษี ณ ที่จ่าย (ตอนบันทึกบัตร) — คัดลอกไปคำสั่งตัดเมื่อเปิดใช้</summary>
    public DateTime? WhtAcknowledgedAt { get; set; }
    /// <summary>ลบที่ผู้ให้บริการไม่สำเร็จ (บัตรยังไม่ถูกใช้ต่อในระบบเรา แต่ยังค้างที่ผู้ให้บริการ) — แอดมินเห็นบนหน้า ops</summary>
    public string? ProviderRemoveProblem { get; set; }
}

/// <summary>คำสั่งตัดบัตรอัตโนมัติของบริษัทผู้จ่ายหนึ่งราย (แถวเดียวต่อบริษัท) — ผู้เช่าเปิด/ปิด · แอดมินระงับได้ต่อบริษัท</summary>
public class PlatformAutoChargeMandate : BaseEntity
{
    public Guid PlatformCompanyId { get; set; }
    public Guid PayerCompanyId { get; set; }
    /// <summary>ผู้เช่าเปิดตัดอัตโนมัติ (ยินยอมแล้ว) — ปิดเมื่อไรก็ได้ที่ศูนย์บิล</summary>
    public bool Enabled { get; set; }
    /// <summary>ความยินยอมที่ใช้อยู่ (PdpaConsentRecord) — ปิด ⇒ ถอนความยินยอม (WithdrawnAt)</summary>
    public Guid? ConsentRecordId { get; set; }
    /// <summary>ฉบับข้อความยินยอม (รวมฉบับนโยบายยกเลิก/คืนเงิน) ที่ยอมรับ — ไม่ตรงฉบับปัจจุบัน ⇒ หยุดตัดจนกว่าจะยืนยันใหม่</summary>
    public string? ConsentVersion { get; set; }
    public DateTime? EnabledAt { get; set; }
    public string? EnabledBy { get; set; }
    public DateTime? DisabledAt { get; set; }
    public string? DisabledBy { get; set; }
    public string? DisabledReason { get; set; }
    /// <summary>แอดมินแพลตฟอร์มระงับการตัดอัตโนมัติของบริษัทนี้ (ไม่แตะความยินยอมของผู้เช่า)</summary>
    public DateTime? AdminBlockedAt { get; set; }
    public string? AdminBlockedBy { get; set; }
    public string? AdminBlockedReason { get; set; }
    /// <summary>ฝ่ายค้าน S1: ผู้จ่ายนิติบุคคลรับทราบแยกว่าตัดเต็มยอดไม่หักภาษี ณ ที่จ่าย — นิติบุคคลที่ยังไม่รับทราบ = ไม่ตัด</summary>
    public DateTime? WhtAcknowledgedAt { get; set; }
    /// <summary>ฝ่ายค้าน S4: watermark ของงานตัดอัตโนมัติ — เรียงเก่าสุดก่อน (บริษัทเกินหนึ่งรอบไม่ถูกข้ามตลอดไป)</summary>
    public DateTime? LastProcessedAt { get; set; }
}

/// <summary>การตัดบัตรอัตโนมัติหนึ่งครั้งของใบแจ้งหนี้หนึ่งใบ — <b>unique (ใบ · ครั้งที่)</b> ⇒ สองเครื่อง/สองรอบตัดครั้งเดียวกันซ้ำไม่ได้ ·
/// จองแถว (Claimed) ก่อนเรียกผู้ให้บริการ แล้วประทับผลจริง</summary>
public class PlatformAutoChargeAttempt : BaseEntity
{
    public Guid PlatformCompanyId { get; set; }
    public Guid PayerCompanyId { get; set; }
    /// <summary>ใบแจ้งหนี้ค่าบริการ (Document ใน tenant ผู้ให้บริการ)</summary>
    public Guid DocumentId { get; set; }
    public int AttemptNo { get; set; }
    public Guid? SubscriptionPaymentId { get; set; }
    public Guid? SavedCardId { get; set; }
    public Guid? IntentId { get; set; }
    public decimal Amount { get; set; }
    /// <summary>ชื่อจาก <c>PlatformAutoChargePolicy.Outcome*</c> (Claimed · Pending · RequiresAction · Succeeded · Failed)</summary>
    public string Outcome { get; set; } = "Claimed";
    public string? Detail { get; set; }
    public DateTime AttemptedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}

/// <summary>สวิตช์ระดับแพลตฟอร์ม (แถวเดียว) — ตัดบัตรอัตโนมัติทั้งระบบ · ค่าตั้งต้น <b>ปิด</b> (แอดมินเปิดเองหลังทดสอบกับ sandbox)</summary>
public class PlatformAutoChargeSettings : BaseEntity
{
    public static readonly Guid SingletonId = new("2300a0c1-0000-4000-8000-000000000230");
    public bool AutoChargeEnabled { get; set; }
    public DateTime? ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
}

/// <summary>รอบโอน (transfer/payout) ที่ดึงจากผู้ให้บริการของช่องทางค่าบริการ NextAcc — <b>unique (ผู้ให้บริการ · เลขรอบโอน)</b> ·
/// จับคู่กับรายการรับชำระแล้วสร้าง<b>ร่าง</b>รอบโอน (SettlementBatch สถานะนำเข้า) — ไม่ลงบัญชีเอง (ผู้ทำบัญชีตรวจแล้วกดลงบัญชีที่หน้ารอบโอน)</summary>
public class PlatformProviderTransfer : BaseEntity
{
    public Guid PlatformCompanyId { get; set; }
    public string ProviderCode { get; set; } = "";
    public Guid? ProviderConfigId { get; set; }
    public bool IsTestMode { get; set; }
    public string TransferRef { get; set; } = "";
    /// <summary>ยอดที่ถูกหักจากยอดคงเหลือที่ผู้ให้บริการ (ยอดโอน + ค่าธรรมเนียมโอน)</summary>
    public decimal WalletDebit { get; set; }
    /// <summary>ยอดที่เข้าธนาคาร (ตัวตั้งของร่างรอบโอน)</summary>
    public decimal NetToBank { get; set; }
    public decimal TransferFee { get; set; }
    public decimal TransferFeeVat { get; set; }
    public string? RawStatus { get; set; }
    public bool Paid { get; set; }
    public DateTime? ProviderCreatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    /// <summary>JSON array ของเลข charge ที่ผู้ให้บริการระบุว่าอยู่ในรอบโอนนี้ — null = ผู้ให้บริการไม่ระบุ (จับคู่ด้วยยอด)</summary>
    public string? ChargeRefsJson { get; set; }
    /// <summary><c>PlatformTransferMatch.Method*</c></summary>
    public string MatchMethod { get; set; } = "None";
    public string? MatchedIntentIdsJson { get; set; }
    /// <summary>ทำไมยังไม่มีร่าง / สิ่งที่ผู้ตรวจต้องรู้ (null = ไม่มีเรื่อง)</summary>
    public string? Problem { get; set; }
    public Guid? SettlementBatchId { get; set; }
    public DateTime? DraftCreatedAt { get; set; }
    public DateTime PulledAt { get; set; }
}
