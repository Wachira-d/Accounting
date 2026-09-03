using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>
/// **แคตตาล็อกฟีเจอร์ที่เปิดขาย** — admin คุมว่ามีอะไรให้ลูกค้าเปิดใช้บ้าง
/// (ACCOUNT_STRUCTURE.md §3.2, §7.1)
///
/// แยกจาก <see cref="ApiPricingPlan"/> โดยเจตนา: ฟีเจอร์คือ "ของที่มี"
/// ราคาคือ "ของที่เปลี่ยนตามเวลา" — ฟีเจอร์เดียวมีได้หลายแผนราคาตามช่วงวันที่
/// </summary>
public class ApiFeature : BaseEntity
{
    /// <summary>รหัสคงที่ที่โค้ดอ้าง — เปลี่ยนไม่ได้หลังเปิดขาย (ผูกกับ
    /// UsageEvent ย้อนหลังและ contract ที่ลูกค้าเขียนโค้ดไว้แล้ว)</summary>
    public string FeatureCode { get; set; } = "";

    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Description { get; set; }

    /// <summary>หน่วยที่นับ — แสดงบนใบแจ้งหนี้และหน้า usage ("เอกสาร", "บรรทัด")</summary>
    public string UnitLabel { get; set; } = "รายการ";

    /// <summary>ปิด = หายจากหน้าเลือกของลูกค้าใหม่ทันที **แต่บริษัทที่เปิดใช้
    /// อยู่แล้วยังใช้ต่อได้** — ถอนของที่ลูกค้าพึ่งพาอยู่กลางคันไม่ได้</summary>
    public bool IsPublished { get; set; } = true;

    /// <summary>scope ที่ ApiClient ต้องมีจึงจะเรียกฟีเจอร์นี้ได้
    /// (space-separated เช่น "ocr:write")</summary>
    public string RequiredScopes { get; set; } = "";

    /// <summary>ลำดับแสดงในหน้าเลือกฟีเจอร์</summary>
    public int SortOrder { get; set; }

    // ═══ ส่วนขยายรอบ add-on (LODGING_LICENSING_PLAN.md §6) ═══

    /// <summary>ConnectedApi (เดิม) · BusinessAddOn (ส่วนเสริมในแอป) · SystemMeter
    /// (มิเตอร์ที่ระบบเขียนเอง — ห้ามโผล่เป็นสวิตช์ให้ลูกค้ากดปิด)</summary>
    public ApiFeatureKind Kind { get; set; } = ApiFeatureKind.ConnectedApi;

    /// <summary>ต้องมีแพ็กเกจบัญชีขั้นต่ำอะไรถึงจะเปิด add-on นี้ได้ —
    /// ว่าง/null = ขายได้ทุกแพ็กเกจ · "Pro,Enterprise" = เฉพาะสองแพ็กนี้
    /// (ชื่อตรงกับ <see cref="SubscriptionPlan"/>)</summary>
    public string? MinPlanCsv { get; set; }

    /// <summary>ทดลองใช้ฟรีกี่วันก่อนเริ่มคิดเงิน (0 = ไม่มี trial) — ตั้งต่อ add-on
    /// เพราะบางตัวเห็นคุณค่าใน 1 สัปดาห์ บางตัวต้องรอรอบการจองจริง</summary>
    public int TrialDays { get; set; }

    /// <summary>ไอคอน emoji สำหรับหน้าเลือก add-on (ไม่ใช่ไฟล์ — กัน asset หาย)</summary>
    public string? Icon { get; set; }

    /// <summary>โมดูลที่ add-on นี้สังกัด — ใช้จัดกลุ่มหน้าจอ ("Lodging", "Api")</summary>
    public string? ModuleCode { get; set; }
}

/// <summary>
/// **ฟีเจอร์ที่บริษัทนี้เปิดใช้** — ลูกค้ากดเปิด/ปิดเองใน portal
///
/// เป็น **สวิตช์เงินจริง** ไม่ใช่แค่ซ่อนเมนู: ปิดอยู่ = endpoint ตอบ 403 และ
/// **ไม่เกิด <see cref="UsageEvent"/>** จึงต้อง audit ว่าใครกดเปิดตอนไหนเห็น
/// ราคาเท่าไร (กันข้อพิพาท "ไม่เคยเปิด / ไม่รู้ว่ามีค่าใช้จ่าย")
/// </summary>
public class CompanyFeature : TenantEntity
{
    public string FeatureCode { get; set; } = "";
    public bool IsEnabled { get; set; }

    public DateTime? EnabledAt { get; set; }
    public string? EnabledBy { get; set; }
    public DateTime? DisabledAt { get; set; }
    public string? DisabledBy { get; set; }

    /// <summary>ราคาต่อหน่วยที่ลูกค้า "เห็นและยอมรับ" ตอนกดเปิด — หลักฐานว่า
    /// เคยแจ้งราคาแล้ว (ไม่ใช่ราคาที่ใช้คิดเงินจริง ซึ่งอ่านจาก
    /// <see cref="ApiPricingPlan"/> ณ เวลาที่เกิด usage)</summary>
    public decimal? AcceptedUnitPrice { get; set; }

    // ═══ ส่วนขยายรอบ add-on (LODGING_LICENSING_PLAN.md §6) ═══

    /// <summary>อยู่ในช่วงทดลองใช้ฟรีถึงเมื่อไร — ยังไม่ถึง = ใช้ได้แต่ยอดเป็น ฿0
    /// (job รายเดือนข้ามการคิดเงินให้) · null = ไม่ใช่ trial</summary>
    public DateTime? TrialUntil { get; set; }

    /// <summary>true = พอหมด trial ให้ปิดเอง (ไม่คิดเงินโดยไม่ถาม) ·
    /// false = คิดเงินต่อทันทีตามราคาที่ยอมรับไว้ (ต้องบอกให้ชัดตอนกดเปิด)</summary>
    public bool AutoDisableAfterTrial { get; set; }

    /// <summary>ใครเป็นคนเปิด — ของแถมจาก admin/แพ็กเกจ ไม่คิดเงิน</summary>
    public AddOnGrantSource GrantSource { get; set; } = AddOnGrantSource.OwnerSelfServe;

    /// <summary>ราคาที่ตกลงใช้จริงตลอดสัญญา (ดีลพิเศษ) — null = ใช้ราคามาตรฐาน
    /// ของ <see cref="ApiPricingPlan"/> ณ เวลานั้น. ต่างจาก
    /// <see cref="AcceptedUnitPrice"/> ซึ่งเป็น "ราคาที่ผู้ใช้เห็นตอนกด" (หลักฐาน)</summary>
    public decimal? SnapshotUnitPrice { get; set; }

    /// <summary>งวดล่าสุดที่ job รายเดือนออก UsageEvent ค่าเหมาให้แล้ว (yyyy-MM) —
    /// กันคิดเงินซ้ำเมื่อ job รันหลายรอบ/หลาย instance ในเดือนเดียวกัน
    /// (คู่กับ IdempotencyKey บน UsageEvent — กันสองชั้น)</summary>
    public string? LastBilledPeriod { get; set; }
}

/// <summary>
/// **แผนราคาต่อฟีเจอร์** — admin กำหนดวิธีคิดเงินได้อิสระต่อฟีเจอร์
///
/// มี <see cref="EffectiveFrom"/>/<see cref="EffectiveTo"/> เพื่อให้ราคา
/// "มีอายุ" — ขึ้นราคาแล้วบิลเดือนเก่าต้องไม่ขยับ และตอบลูกค้าได้เสมอว่า
/// ณ วันนั้นราคาเท่าไรด้วยหลักฐานในฐานข้อมูล ไม่ใช่ความจำ
/// </summary>
public class ApiPricingPlan : BaseEntity
{
    public string FeatureCode { get; set; } = "";

    /// <summary>null = ราคามาตรฐาน (ใช้กับทุกคน) · มีค่า = ราคาเฉพาะกลุ่มนี้
    /// (ดีลพิเศษ/enterprise) — resolver เลือกราคาเฉพาะกลุ่มก่อนเสมอ</summary>
    public Guid? BillingAccountId { get; set; }

    public PricingMethod Method { get; set; } = PricingMethod.PerUnit;

    /// <summary>ความหมายขึ้นกับ Method — PerUnit/PerCall = ราคาต่อหน่วย,
    /// FlatMonthly = ค่าเหมาต่อเดือน, Tiered = ราคาชั้นบนสุดถ้า TierJson ว่าง</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>โควตาฟรีต่อเดือน (นับรวมทั้ง BillingAccount ไม่ใช่ต่อบริษัท)</summary>
    public int FreeQuotaPerMonth { get; set; }

    /// <summary>ขั้นบันได `[{"fromQty":0,"unitPrice":10},{"fromQty":1000,"unitPrice":8}]`
    /// — ใช้เมื่อ Method = Tiered. เรียงจากน้อยไปมาก</summary>
    public string? TierJson { get; set; }

    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    /// <summary>หมายเหตุของ admin ว่าทำไมตั้งราคานี้ (ดีลไหน/อนุมัติโดยใคร)</summary>
    public string? AdminNote { get; set; }
}

/// <summary>
/// **หน่วยการใช้งานที่คิดเงินได้ — append-only ห้าม UPDATE/DELETE**
///
/// นี่คือ "หลักฐานการใช้บริการ" ที่ใบแจ้งหนี้อ้างอิง จึงต้องปฏิบัติเหมือน
/// AuditLog: เขียนครั้งเดียวแล้วห้ามแก้ ถ้าคิดเงินผิดให้ออกใบลดหนี้
/// ไม่ใช่ไปแก้ประวัติ
///
/// **ทำไมต้อง snapshot ราคาลงแถว**: ถ้าคำนวณสดตอนออกบิลจากตารางราคาปัจจุบัน
/// การขึ้นราคาจะย้อนไปเปลี่ยนยอดของเดือนที่ผ่านไปแล้ว — ผิดทั้งทางบัญชีและ
/// ทำให้โต้แย้งกับลูกค้าไม่จบ
///
/// **ทำไม BillingAccountId ถึง denormalize**: บริษัทถูกขายออกจากเครือแล้ว
/// (`Company.BillingAccountId` เปลี่ยน) บิลเดือนเก่าต้องยังอยู่กับกลุ่มเดิม
/// การ join สดจะทำให้ประวัติย้ายตามไปด้วย
/// </summary>
public class UsageEvent : TenantEntity
{
    /// <summary>กลุ่มผู้จ่าย ณ ขณะที่เกิดการใช้งาน (stamp ไว้ ไม่ join สด)</summary>
    public Guid? BillingAccountId { get; set; }

    /// <summary>สาขาที่ใช้งาน — สำหรับ breakdown/charge-back ภายในเท่านั้น
    /// **ไม่ใช่หน่วยออกบิล** (บิลจบที่นิติบุคคล)</summary>
    public Guid? BranchId { get; set; }

    /// <summary>มาจาก API key ไหน — null = ใช้ผ่านหน้าเว็บปกติ</summary>
    public Guid? ApiClientId { get; set; }

    public string FeatureCode { get; set; } = "";
    public int Quantity { get; set; } = 1;

    /// <summary>ราคาต่อหน่วย ณ วันที่เกิด (บาท) — คูณกับ Quantity ได้ยอดของแถวนี้</summary>
    public decimal UnitPriceSnapshot { get; set; }

    /// <summary>ยอดที่คิดเงินจริงหลังหักโควตาฟรี (บาท) — เก็บแยกจาก
    /// UnitPrice × Qty เพราะบางแถวตกในโควตาฟรีจึงเป็น 0 ทั้งที่มีราคา</summary>
    public decimal ChargedAmount { get; set; }

    /// <summary>true = แถวนี้ถูกกลืนด้วยโควตาฟรีของเดือนนั้น (ChargedAmount = 0)
    /// — แยกไว้ให้รายงานบอกลูกค้าได้ว่า "ใช้ฟรีไป X ครั้ง"</summary>
    public bool CoveredByFreeQuota { get; set; }

    /// <summary>true = อยู่ใน sandbox → **ไม่เข้าบิล** แต่ยังนับเพื่อดู
    /// พฤติกรรมช่วงทดสอบ</summary>
    public bool IsSandbox { get; set; }

    /// <summary>กันเก็บเงินซ้ำเมื่อ client retry — unique ต่อ (CompanyId, key)</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>ชี้กลับไปที่สิ่งที่เกิดจริง เช่น ("Document", docId) หรือ
    /// ("OcrScanResult", scanId) — ให้ลูกค้าตรวจสอบบิลย้อนได้ว่าค่านี้มาจากงานไหน</summary>
    public string? RefEntityType { get; set; }
    public Guid? RefEntityId { get; set; }

    /// <summary>รอบบิลที่แถวนี้ถูกรวมไปแล้ว (yyyy-MM) — null = ยังไม่ออกบิล.
    /// กันการนับซ้ำเมื่อ gen บิลรอบเดิมใหม่</summary>
    public string? BilledPeriod { get; set; }
    public Guid? BilledDocumentId { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
