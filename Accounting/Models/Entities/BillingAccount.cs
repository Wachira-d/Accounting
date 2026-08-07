using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// **องค์กรผู้จ่ายเงิน** — ชั้นบนสุดของโครง BillingAccount → Company → Branch
/// (ดู ACCOUNT_STRUCTURE.md §1)
///
/// ทำไมต้องมีทั้งที่ <see cref="AccountSubscription"/> ทำงานได้อยู่แล้ว:
/// AccountSubscription ผูกกับ **คน** (`OwnerUserId`) ซึ่งเป็นสมอที่หลุดง่าย —
/// เจ้าของลาออก/เปลี่ยนมือ/เสียชีวิต แล้วทั้งกลุ่มบริษัทไม่มีเจ้าของสัญญา
/// อีกทั้งใบกำกับภาษีค่าบริการต้องออกให้ **นิติบุคคล** ไม่ใช่บุคคล
/// BillingAccount จึงเป็นสมอที่ถูกต้อง ส่วนคนกลายเป็น
/// <see cref="BillingAccountAdmin"/> ที่ถอด/เพิ่มได้โดยไม่กระทบสัญญา
///
/// **ขอบเขต**: ชั้นนี้เป็นเรื่อง *เงินและสิทธิ์บริหาร* เท่านั้น —
/// ห้ามใช้เป็นทางลัดเห็นข้อมูลบัญชีข้ามบริษัท (tenant isolation ยังอยู่ที่
/// `CompanyId` ทุก query เหมือนเดิม). งบรวมของกลุ่มไปทาง
/// <see cref="ConsolidationGroup"/> ซึ่งเป็น opt-in และเป็นคนละเรื่องกัน
///
/// **ความเข้ากันได้**: เป็น layer เสริมล้วน ๆ — ระบบเดิม resolve โควตาผ่าน
/// `Subscription.AccountSubscriptionId` ต่อไปได้ตามปกติทุกประการ แถวนี้ถูก
/// backfill ให้ทุก AccountSubscription ที่มีอยู่ (1:1) ตอน migrate
/// </summary>
public class BillingAccount : BaseEntity
{
    /// <summary>ชื่อกลุ่ม/องค์กรที่แสดงในพอร์ทัลและบนใบแจ้งหนี้ เช่น "เครือ ABC กรุ๊ป"</summary>
    public string Name { get; set; } = "";

    /// <summary>เลขผู้เสียภาษีของนิติบุคคล **ผู้รับใบกำกับภาษีค่าบริการ**
    /// (โหมด Centralized). null ได้ในช่วง onboarding ที่ยังไม่ถึงขั้นออกบิล
    /// — ต้องมีก่อนออกใบกำกับใบแรกเสมอ (§86/4 บังคับเลขผู้ซื้อเมื่อเป็นนิติบุคคล)</summary>
    public string? TaxId { get; set; }

    public string? BillingAddress { get; set; }
    public string? BillingBranchCode { get; set; } = "00000";
    public string? BillingEmail { get; set; }
    public string? ContactPhone { get; set; }

    /// <summary>ออกใบกำกับใบเดียวให้กลุ่ม (แยกบรรทัดต่อบริษัท) หรือแยกใบต่อ
    /// นิติบุคคล — ดู ACCOUNT_STRUCTURE.md §6.1</summary>
    public BillingMode BillingMode { get; set; } = BillingMode.Centralized;

    /// <summary>เติมเครดิตล่วงหน้า (default — ตัดปัญหาตามเก็บเงิน) หรือใช้ก่อน
    /// จ่ายทีหลัง (เฉพาะลูกค้าที่มีสัญญา + วงเงินที่ admin อนุมัติ)</summary>
    public PaymentModel PaymentModel { get; set; } = PaymentModel.Prepaid;

    /// <summary>เครดิตคงเหลือ (โหมด Prepaid) — ตัดตาม UsageEvent.
    /// **หน่วยเป็นบาท** ไม่ใช่ "แต้ม" เพื่อให้กระทบยอดกับใบเสร็จได้ตรง ๆ</summary>
    public decimal CreditBalance { get; set; }

    /// <summary>วงเงินค้างชำระสูงสุด (โหมด Postpaid) — เกินแล้วหยุดรับงานใหม่
    /// 0 = ไม่จำกัด (ใช้กับลูกค้าที่มีสัญญาเท่านั้น)</summary>
    public decimal PostpaidCreditLimit { get; set; }

    /// <summary>วันผ่อนผันหลังบิลเกินกำหนด — บริษัทใต้กลุ่มยังทำงานได้ในช่วงนี้
    /// (ค่าเดียวกับ AccountSubscription.GracePeriodDays เพื่อไม่ให้ผู้ใช้งง
    /// ว่ามีเลข grace 2 ชุด)</summary>
    public int GracePeriodDays { get; set; } = 7;

    /// <summary>ทั้ง account เป็นพื้นที่ทดสอบ — ใช้ตอน POC ก่อนเซ็นสัญญา
    /// ข้อมูลลบทิ้งได้ และ **ไม่เข้าระบบคิดเงิน**</summary>
    public bool IsSandbox { get; set; }

    public BillingAccountStatus Status { get; set; } = BillingAccountStatus.Active;

    /// <summary>เหตุผลที่ระงับ — โชว์ให้ผู้ใช้ทุกบริษัทใต้กลุ่มเห็น ไม่ใช่แค่ผู้จ่าย
    /// (คนทำงานจริงอยู่ที่บริษัทย่อย ต้องรู้ว่าทำไมใช้ไม่ได้)</summary>
    public string? SuspendReason { get; set; }
    public DateTime? SuspendedAt { get; set; }

    public ICollection<BillingAccountAdmin> Admins { get; set; } = new List<BillingAccountAdmin>();
    public ICollection<Company> Companies { get; set; } = new List<Company>();
}

/// <summary>
/// ผู้ดูแลกลุ่ม (M:N ระหว่าง BillingAccount กับ User)
///
/// **สิทธิ์ที่ได้**: ดูภาพรวมการใช้งานทุกบริษัทในกลุ่ม, จัดการบิล/เครดิต/
/// API key, เพิ่ม-ถอดบริษัท
/// **สิทธิ์ที่ไม่ได้**: เปิดสมุดบัญชีของบริษัทใด ๆ — ต้องมี
/// <see cref="CompanyUser"/> ของบริษัทนั้นแยกต่างหาก (แยก "บริหารกลุ่ม"
/// ออกจาก "เห็นบัญชี" — ผู้บริหารกลุ่มไม่ควรเปิดสมุดย่อยได้อัตโนมัติ)
/// </summary>
public class BillingAccountAdmin : BaseEntity
{
    public Guid BillingAccountId { get; set; }
    public BillingAccount BillingAccount { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>ผู้ดูแลหลัก — ผู้รับอีเมลบิล/แจ้งเตือนหลัก และถอดตัวเองไม่ได้
    /// (กัน account กลายเป็นไม่มีผู้ดูแลเลย)</summary>
    public bool IsPrimary { get; set; }
}
