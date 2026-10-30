namespace Accounting.Models.Entities;

/// <summary>
/// รอบ 219 เฟส 2 (PLATFORM_BILLING_AUDIT §5 เฟส 2 · คำตัดสินข้อ 180) — ค่าตั้งการเรียกเก็บค่าบริการ NextAcc ระดับแพลตฟอร์ม <b>แถวเดียวของระบบ</b>
/// (<see cref="SingletonId"/>) · แอดมินแพลตฟอร์มแก้ที่ <c>/admin/platform-billing.html</c> · ไม่มีแถว = ค่าตั้งต้นใน
/// <c>Helpers/PlatformBillingSettingsRules</c> (ตัวเดียวที่แปลงแถว → นโยบาย) · ไม่แคชในหน่วยความจำ (กฎ #4 D — อ่านสดทุกครั้ง แถวเดียว)
/// <para>ผู้อ่านเชิงธุรกิจ: <c>RenewalInvoiceLeadDays</c> → <c>SubscriptionService.ProcessSubscriptionNotificationsAsync</c> (ออกใบต่ออายุล่วงหน้า) ·
/// วันแจ้งเตือน/ระยะผ่อนผัน/ระงับอัตโนมัติ/ช่องทาง → <c>PlatformDunningService</c> · อายุลิงก์ชำระ → <c>PlatformPaymentLinkService</c></para>
/// </summary>
public class PlatformBillingSettings : BaseEntity
{
    /// <summary>Id คงที่ของแถวเดียว — สร้างพร้อมกันสองเครื่องชน PK แทนที่จะได้สองแถว</summary>
    public static readonly Guid SingletonId = new("2190a0b2-0000-4000-8000-000000000219");

    /// <summary>ออกใบแจ้งหนี้ต่ออายุกี่วันก่อนครบรอบ (เดิมค่าคงที่ 15 ใน SubscriptionService)</summary>
    public int RenewalInvoiceLeadDays { get; set; } = 15;
    /// <summary>วันแจ้งเตือนก่อนครบกำหนด (คั่นจุลภาค เช่น "7,3,1")</summary>
    public string ReminderDaysBeforeDue { get; set; } = "7,3,1";
    /// <summary>วันแจ้งเตือนหลังเกินกำหนด (คั่นจุลภาค เช่น "1,3,7")</summary>
    public string ReminderDaysAfterDue { get; set; } = "1,3,7";
    /// <summary>เกินกำหนดกี่วันจึงระงับ (นับวันปฏิทินไทย)</summary>
    public int SuspendGraceDays { get; set; } = 14;
    /// <summary>ระงับอัตโนมัติเมื่อเกินระยะผ่อนผัน — มีผลจริงเฉพาะเมื่อโหมดบังคับแพ็กเกจ = Enforce (โหมดเงา = บันทึกว่า "จะระงับ")</summary>
    public bool AutoSuspendEnabled { get; set; } = true;
    /// <summary>อายุตั้งต้นของลิงก์ชำระที่แอดมินสร้าง (วัน)</summary>
    public int PaymentLinkExpiryDays { get; set; } = 14;
    /// <summary>ส่งใบแจ้งหนี้/แจ้งเตือนทางอีเมล (อีเมลระบบ)</summary>
    public bool ReminderEmailEnabled { get; set; } = true;
    /// <summary>ส่งใบแจ้งหนี้/แจ้งเตือนทาง LINE (OA กลางของ NextAcc · ไม่นับโควตาข้อความของร้าน)</summary>
    public bool ReminderLineEnabled { get; set; } = true;
}

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 2 (G-7) — ใบแจ้งหนี้/แจ้งเตือนค่าบริการที่ส่งถึงผู้เช่า หนึ่งแถวต่อ (คีย์ข้อความ · ช่อง) — <b>unique</b> ⇒ ส่งได้ครั้งเดียวแม้หลายเครื่อง ·
/// จองแถว (Claimed) ก่อนส่งแล้วประทับผลจริง Sent/Failed/Skipped + เหตุ (แอดมินเห็นที่ <c>/admin/platform-billing.html</c>)
/// </summary>
public class PlatformBillingNotice : BaseEntity
{
    public Guid BuyerCompanyId { get; set; }
    public Guid? DocumentId { get; set; }
    /// <summary><c>PlatformDunningPolicy.Key</c> / <c>CompanyKey</c></summary>
    public string MessageKey { get; set; } = "";
    /// <summary>email | line</summary>
    public string Channel { get; set; } = "";
    public string Status { get; set; } = "Claimed";
    public string? Detail { get; set; }
    public DateTime? SentAt { get; set; }
}

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 2 (G-8) — สถานะทวงหนี้ต่อบริษัทผู้เช่า: <b>watermark ต่อบริษัท</b> (<see cref="LastEvaluatedAt"/>) ของ <c>PlatformDunningJob</c> ·
/// ระบบระงับเอง (<see cref="SuspendedByDunningAt"/> + สถานะก่อนระงับ — คืนได้ถูกต้อง · ระงับด้วยเหตุอื่นไม่ถูกแตะ) · "จะระงับ" ในโหมดเงา ·
/// แอดมินพักการระงับ (<see cref="SuspendExemptUntil"/>)
/// </summary>
public class PlatformDunningState : BaseEntity
{
    public Guid CompanyId { get; set; }
    public DateTime? LastEvaluatedAt { get; set; }
    public decimal OverdueAmount { get; set; }
    public DateTime? OldestOverdueDueDate { get; set; }
    public DateTime? SuspendedByDunningAt { get; set; }
    public Accounting.Models.Enums.SubscriptionStatus? StatusBeforeSuspend { get; set; }
    public DateTime? WouldSuspendSince { get; set; }
    public DateTime? SuspendExemptUntil { get; set; }
    public string? LastAction { get; set; }
}

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 4 (PLATFORM_BILLING_AUDIT §4 · คำตัดสินข้อ 180) — <b>ลิงก์ชำระใบแจ้งหนี้ค่าบริการ</b> ที่แอดมินสร้างให้ใบที่มีอยู่แล้ว
/// (ห้ามยอดอิสระ) · เก็บเฉพาะ SHA-256 ของโทเคน 128 บิต (<c>Helpers/PlatformPaymentLinkRules.Hash</c>) · ใบเดียว · ปิดเองเมื่อใบชำระครบ · เพิกถอนได้ ·
/// ทุกการสร้าง/เปิดดู/เพิกถอน/แจ้งชำระลง audit chain
/// </summary>
public class PlatformPaymentLink : BaseEntity
{
    public Guid PlatformCompanyId { get; set; }
    public Guid BuyerCompanyId { get; set; }
    public Guid DocumentId { get; set; }
    public string TokenHash { get; set; } = "";
    /// <summary>ยอดค้าง ณ ตอนสร้าง (อ้างอิง) — ยอดที่จ่ายจริง = ยอดค้างของใบ ณ ตอนแจ้งชำระ</summary>
    public decimal AmountSnapshot { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string? RevokedReason { get; set; }
    public int ViewCount { get; set; }
    public DateTime? LastViewedAt { get; set; }
}
