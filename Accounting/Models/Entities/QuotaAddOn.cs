using Accounting.Models.Enums;

namespace Accounting.Models.Enums
{
    /// <summary>รอบ 229 (QUOTA_ADDON_SUBSCRIPTION_AUDIT §B.5): วิธีซื้อโควตาเสริม — ตัวคิดยอดเดียว <c>Helpers/QuotaAddOnPricing</c></summary>
    [Flags]
    public enum QuotaAddOnMode
    {
        None = 0,
        /// <summary>เดือนนี้เดือนเดียว (Capacity: ถึงสิ้นเดือนไทย · Consumable: ตามอายุแพ็ก) — ราคาเต็มต่อหน่วย ไม่คิดตามวัน</summary>
        OneOffMonth = 1,
        /// <summary>ทุกเดือนจนยกเลิก — เดือนแรกคิดตามวันที่เหลือในเดือนไทย (คำตัดสินข้อ 229-5) · เดือนถัดไปเข้าใบแจ้งหนี้ค่าใช้งานรายเดือน</summary>
        RecurringMonthly = 2,
        /// <summary>ถึงวันหมดอายุแพ็กเกจ — คิดตามวัน (คำตัดสินข้อ 229-4) · ต่ออายุพร้อมแพ็กเกจได้ (ค่าตั้งต้น = ต่อ · คำตัดสินข้อ 229-3)</summary>
        Coterm = 4,
    }

    /// <summary>รอบ 229: สถานะการซื้อโควตาเสริม 1 ครั้ง — <b>ให้สิทธิ์เฉพาะ Active</b> (สลิปรอตรวจยังไม่ให้ — ต่างจากส่วนเสริมสวิตช์: โควตาที่สร้างของไปแล้วถอนไม่ได้
    /// เพราะไม่ลบข้อมูลผู้ใช้ · คำตัดสินข้อ 229-2)</summary>
    public enum QuotaAddOnStatus
    {
        AwaitingPayment = 0,
        PendingReview = 1,
        Active = 2,
        Expired = 3,
        Cancelled = 4,
        Rejected = 5,
    }
}

namespace Accounting.Models.Entities
{
    /// <summary>รอบ 229 (§B.3): เพดานที่แพ็กเกจรวมให้ ต่อทรัพยากรที่<b>ไม่มีช่องเดิม</b>บนแพ็กเกจ (<c>QuotaResources.LegacySource.None</c>/<c>LodgingFirstFree</c>) —
    /// ระดับแพลตฟอร์ม (ไม่ใช่ข้อมูลผู้เช่า) · unique (PlanTemplateId, ResourceKey) · -1 = ไม่จำกัด · ไม่มีแถว = ไม่จำกัด/ค่าเดิม</summary>
    public class PlanQuotaAllowance : BaseEntity
    {
        public Guid PlanTemplateId { get; set; }
        public string ResourceKey { get; set; } = "";
        public int Included { get; set; } = -1;
        /// <summary>ระหว่างทดลองใช้ · null = เท่ากับ <see cref="Included"/></summary>
        public int? TrialIncluded { get; set; }
    }

    /// <summary>รอบ 229 (§B.3): แอดมินแพลตฟอร์มตั้งเพดานรายบริษัท (ชนะแพ็กเกจ) — ต้องมีเหตุผล · ทุกการตั้ง/ลบลง audit chain · -1 = ไม่จำกัด</summary>
    public class CompanyQuotaOverride : TenantEntity
    {
        public string ResourceKey { get; set; } = "";
        public int Limit { get; set; }
        public string Reason { get; set; } = "";
        public string? SetBy { get; set; }
        public DateTime SetAt { get; set; } = DateTime.UtcNow;
    }

    /// <summary>รอบ 229 (§B.3): <b>แคตตาล็อกโควตาเสริม</b> ที่แอดมินตั้ง — ตารางแยกจาก <c>ApiFeature</c> โดยตั้งใจ (ApiFeature ถูกอ่านโดยหน้าสวิตช์/EntitlementService/
    /// มิเตอร์ที่ไม่รู้จักชนิดใหม่ · กฎ E "ห้าม reuse ผิดความหมาย") · ราคา ณ วันซื้อถูก snapshot บนการซื้อ (แก้ราคาภายหลังไม่แก้ย้อนหลัง)</summary>
    public class QuotaAddOnOffer : BaseEntity
    {
        /// <summary>ต้องอยู่ใน <c>QuotaResources.All</c> และ <c>Sellable</c> · 1 ข้อเสนอต่อทรัพยากร (unique)</summary>
        public string ResourceKey { get; set; } = "";
        public string? Name { get; set; }
        /// <summary>1 หน่วยที่ซื้อ = กี่หน่วยของทรัพยากร (เช่น 1 เว็บ · 10 GB · 500 หน้า)</summary>
        public int UnitSize { get; set; } = 1;
        /// <summary>ราคาต่อหน่วยต่อเดือน (บาท · รวม/ไม่รวม VAT ตาม <c>SiteSettings.PlatformPriceIncludesVat</c> เดียวกับค่าบริการอื่น)</summary>
        public decimal MonthlyUnitPrice { get; set; }
        /// <summary>ราคาต่อหน่วยต่อปี · null = เดือน × 12 (คำตัดสินข้อ 229-4)</summary>
        public decimal? AnnualUnitPrice { get; set; }
        public QuotaAddOnMode AllowedModes { get; set; } = QuotaAddOnMode.OneOffMonth | QuotaAddOnMode.RecurringMonthly | QuotaAddOnMode.Coterm;
        /// <summary>ซื้อได้สูงสุดต่อบริษัท (หน่วย · รวมทุกการซื้อที่ยังมีผล) · null = ไม่จำกัด</summary>
        public int? MaxUnitsPerCompany { get; set; }
        /// <summary>โหมดถึงวันหมดอายุ: ยอดต่ำกว่าราคาเดือนหนึ่งเดือน ⇒ คิดหนึ่งเดือน (คำตัดสินข้อ 229-4 · ค่าตั้งต้นเปิด)</summary>
        public bool MinChargeOneMonth { get; set; } = true;
        /// <summary>อายุของการซื้อแบบเดือนเดียว (วัน) · null = ถึงสิ้นเดือนไทย · ใช้กับ Consumable</summary>
        public int? ValidDays { get; set; }
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
    }

    /// <summary>รอบ 229 (§B.3): <b>การซื้อโควตาเสริม 1 ครั้ง</b> — หลายแถวต่อบริษัทได้ (หลายงวด หลายโหมด) · ให้สิทธิ์เมื่อ Active และ now ∈ [PeriodStart, PeriodEnd) ·
    /// ตัวตัดสินช่วงสิทธิ์ <c>QuotaLimitPolicy.IsLive</c> · ไม่เคยลบข้อมูลของผู้ใช้เมื่อหมดอายุ (คำตัดสินข้อ 229-2)</summary>
    public class CompanyQuotaAddOn : TenantEntity
    {
        public Guid OfferId { get; set; }
        public string ResourceKey { get; set; } = "";
        public int Units { get; set; }
        /// <summary>snapshot ของขนาดหน่วย ณ วันซื้อ (แอดมินเปลี่ยนภายหลังไม่กระทบของที่ซื้อแล้ว)</summary>
        public int UnitSize { get; set; } = 1;
        public QuotaAddOnMode Mode { get; set; }
        public DateTime PeriodStartUtc { get; set; }
        /// <summary>null = รายเดือนจนยกเลิก</summary>
        public DateTime? PeriodEndUtc { get; set; }
        public QuotaAddOnStatus Status { get; set; } = QuotaAddOnStatus.AwaitingPayment;
        /// <summary>รายเดือน: ยกเลิกแล้วมีผลสิ้นเดือน (ไม่คืนเงิน)</summary>
        public bool CancelAtPeriodEnd { get; set; }
        /// <summary>Coterm: ต่อพร้อมแพ็กเกจรอบหน้า (ค่าตั้งต้น true · คำตัดสินข้อ 229-3)</summary>
        public bool RenewWithPlan { get; set; } = true;
        /// <summary>จำนวนที่จะต่อ (null = เท่าเดิม) — ผู้เช่าลดได้ที่หน้าต่ออายุ</summary>
        public int? RenewUnits { get; set; }
        /// <summary>ราคาต่อหน่วยต่อรอบที่ออกบนใบแจ้งหนี้ต่ออายุ (snapshot — แอดมินเปลี่ยนราคาหลังออกใบไม่กระทบรอบที่ออกแล้ว)</summary>
        public decimal? RenewUnitPrice { get; set; }
        /// <summary>ใบแจ้งหนี้ต่ออายุที่บรรทัดนี้ถูกรวมไว้ (ตัดชำระใบนี้ ⇒ สร้างการซื้อรอบใหม่)</summary>
        public Guid? RenewalInvoiceDocumentId { get; set; }
        /// <summary>แถวนี้เกิดจากการต่ออายุของแถวไหน (กันต่อซ้ำ)</summary>
        public Guid? RenewedFromId { get; set; }

        /// <summary>ราคาต่อหน่วยบนใบ (หลังคิดตามวัน) · ยอด = round(Units × UnitPriceSnapshot, 2)</summary>
        public decimal UnitPriceSnapshot { get; set; }
        /// <summary>ราคาต่อหน่วยต่อเดือน ณ วันซื้อ (รายเดือนเดือนถัดไปคิดจากค่านี้)</summary>
        public decimal MonthlyUnitPriceSnapshot { get; set; }
        public decimal AmountDue { get; set; }
        public bool VatIncluded { get; set; } = true;
        /// <summary>คำอธิบายช่วงคิดเงิน (เช่น "136/365 วัน") — แสดงบนใบเสร็จ</summary>
        public string? ProrationNote { get; set; }

        public Guid? PaymentIntentId { get; set; }
        public string? SlipPath { get; set; }
        public DateTime? SlipUploadedAt { get; set; }
        public string? SlipReference { get; set; }
        public string? RejectedReason { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewedBy { get; set; }
        public Guid? ReceiptDocumentId { get; set; }
        public string? ReceiptNumber { get; set; }
        /// <summary>รายเดือน: งวด (yyyy-MM ไทย) ล่าสุดที่ออกค่าเดือนแล้ว (กันซ้ำชั้นแรก — ชั้นสองคือ unique IdempotencyKey ของ UsageEvent)</summary>
        public string? LastBilledPeriod { get; set; }
        /// <summary>OCR: แถวเครดิต OCR ที่สร้างให้ตอนเปิดใช้ (ตัวนับเดิม — ไม่ใช่ตัวนับใหม่)</summary>
        public Guid? OcrCreditPurchaseId { get; set; }
        public string? PolicyVersionAccepted { get; set; }
        /// <summary>คีย์กันกดซ้ำของคำขอซื้อ (hash ของ Idempotency-Key)</summary>
        public string? RequestKey { get; set; }
        /// <summary>ฝ่ายค้าน S7: วันที่โอนตามสลิป (วันที่ใบเสร็จ §78/1) · null = ไม่ได้แจ้ง (ใช้วันอนุมัติ)</summary>
        public DateTime? SlipTransferDate { get; set; }
        /// <summary>ฝ่ายค้าน S7: เงินที่รับจริง (ยอดบนใบเสร็จ) · ออนไลน์ = ยอด intent · สลิป = ยอดที่แอดมินยืนยัน</summary>
        public decimal? ReceivedAmount { get; set; }
        /// <summary>ฝ่ายค้าน S2: หลักฐานการรับเงินนอกระบบที่แอดมินระบุเมื่ออนุมัติรายการที่ไม่มีสลิป</summary>
        public string? OfflineReference { get; set; }
        /// <summary>ฝ่ายค้าน B2/S3: เรื่องที่แอดมินต้องจัดการ (เช่น <c>QUOTA-STALE</c> เงินเข้าช้าแต่ช่วงสิทธิ์ใช้ไม่ได้) — null = ไม่มี</summary>
        public string? AttentionRule { get; set; }
        public string? AttentionReason { get; set; }
    }
}
