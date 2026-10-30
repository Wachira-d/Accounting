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
/// รอบ 219 เฟส 2 (คำตัดสินเจ้าของข้อ 178 ⑧) — หลักฐาน "ติ๊กยอมรับเงื่อนไขการซื้อ" ทุกครั้งก่อนจ่ายค่าบริการ NextAcc (ชำระใบแจ้งหนี้ ·
/// ลิงก์ชำระ · ซื้อส่วนเสริม) · <b>append-only</b> · hash ด้วย <c>PdpaConsentEvidence.ComputeHash</c> ตัวเดียวของระบบ (ทุกช่องใน canonical ถูกเก็บ
/// ⇒ ตรวจกลับได้) · ไม่มี FK ไป Companies (ผู้จ่ายผ่านลิงก์ไม่มีบัญชีผู้ใช้)
/// <para><b>ชั่วคราว</b> จนตัวช่วยยอมรับนโยบายของทีม part B ลง — ตัวตั้งเงื่อนไข/ฉบับอยู่ที่ <c>Helpers/PlatformPurchaseTerms</c> ที่เดียว ·
/// ย้ายไปตัวช่วยของ part B = เปลี่ยนผู้เขียนแถวนี้ ไม่ต้องย้ายข้อมูล</para>
/// </summary>
public class PlatformPurchaseAcceptance : BaseEntity
{
    public Guid BuyerCompanyId { get; set; }
    /// <summary>ชนิดการซื้อ: invoice-payment · addon · paylink-payment (<c>PlatformPurchaseTerms.Kind*</c>)</summary>
    public string SubjectKind { get; set; } = "";
    /// <summary>แถวที่ยอมรับเพื่อ (SubscriptionPayment.Id / CompanyFeature.Id)</summary>
    public Guid? SubjectId { get; set; }
    /// <summary>ผู้ยอมรับใน canonical: อีเมลผู้ใช้ หรือ <c>paylink:{linkId}</c> (ไม่มีบัญชี)</summary>
    public string Subject { get; set; } = "";
    public string PolicyVersion { get; set; } = "";
    public DateTime AcceptedAt { get; set; }
    public string? Channel { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string EvidenceHash { get; set; } = "";
}
