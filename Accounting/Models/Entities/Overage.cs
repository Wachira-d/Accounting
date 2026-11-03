namespace Accounting.Models.Enums
{
    /// <summary>รอบ 231 (คำตัดสินข้อ 231-1): เมื่อโควตาของแพ็กเกจเต็ม — ค่าตั้งต้น <see cref="HardLock"/> (พฤติกรรมเดิม) · ผู้เช่า (Owner/BillingManage) เปิด <see cref="AllowOverage"/> เอง</summary>
    public enum OverageMode
    {
        /// <summary>หยุดส่วนที่เกิน — ใบที่กฎหมายบังคับยังออกได้โดยไม่คิดเงิน (DocumentQuotaPolicy) · OCR/ข้อความ/สื่อ CMS บล็อกตามเดิม</summary>
        HardLock = 1,
        /// <summary>ใช้ต่อได้ คิดค่าส่วนเกินตามราคาของแอดมิน (ตารางราคาต่อทรัพยากร/แพ็กเกจ) · เข้าใบแจ้งหนี้ค่าใช้งานรายเดือน</summary>
        AllowOverage = 2,
    }

    /// <summary>รอบ 231 (คำตัดสินข้อ 231-4): ค้างชำระใบส่วนเกินเกินผ่อนผันแล้วทำอะไร</summary>
    public enum OverageUnpaidAction
    {
        /// <summary>ขั้น 1 อย่างเดียว — ปิดสิทธิ์ส่วนเกิน (กลับเป็นล็อก) · ใบ USAGE- ไม่นับเข้าการระงับทั้งบริษัท</summary>
        LockOverageOnly = 1,
        /// <summary>ขั้น 1 ปิดสิทธิ์ส่วนเกิน ⇒ ขั้น 2 เข้า dunning เดิม (ระงับเมื่อเกิน SuspendGraceDays และโหมด Enforce)</summary>
        LockThenDunning = 2,
    }

    /// <summary>รอบ 231 (§D.1): ที่มาของ snapshot การใช้งานรายเดือน</summary>
    public enum UsageSnapshotSource
    {
        /// <summary>เขียนตอนขึ้นเดือนใหม่ก่อนล้างตัวนับ (ค่าปิดเดือนจริง)</summary>
        Rollover = 1,
        /// <summary>งานรายวัน (เดือนปัจจุบัน · ทรัพยากร Capacity เก็บค่าสูงสุด)</summary>
        Daily = 2,
        /// <summary>เติมย้อนหลังครั้งเดียวจากตารางต้นทาง (Documents/OcrScanResults/UsageEvents) — ไม่ทับแถว Rollover</summary>
        Backfill = 3,
    }
}

namespace Accounting.Models.Entities
{
    /// <summary>
    /// รอบ 231 (§B.2 · คำตัดสินข้อ 231-3): <b>ราคาส่วนเกินต่อหน่วย</b> — ระดับแพลตฟอร์ม (ไม่ใช่ข้อมูลผู้เช่า) · แถวที่ระบุแพ็กเกจชนะแถวทั่วไป (null) ·
    /// มีประวัติด้วย <see cref="EffectiveFromUtc"/>/<see cref="EffectiveToUtc"/> (แก้ราคา = ปิดแถวเดิม + แถวใหม่ ไม่แก้ทับ · ราคาถูก snapshot บน UsageEvent ตอนเกิด) ·
    /// <b>ไม่มีราคา = เปิดส่วนเกินทรัพยากรนั้นไม่ได้</b> (ไม่ใช่คิด 0 แบบ <c>documents.overage</c> เดิม) · ตัวตัดสิน <c>Helpers/OverageRateResolver.Pick</c>
    /// </summary>
    public class OverageRate : BaseEntity
    {
        /// <summary>ต้องอยู่ใน <c>OverageResources.All</c></summary>
        public string ResourceKey { get; set; } = "";
        /// <summary>null = ทุกแพ็กเกจ · ระบุ = เฉพาะแม่แบบแพ็กเกจนั้น (ชนะ)</summary>
        public Guid? PlanTemplateId { get; set; }
        /// <summary>1 หน่วยราคา = กี่หน่วยทรัพยากร (ตั้งต้น 1 — 1 ฉบับ · 1 หน้า · 1 ข้อความ · 1 GB-เดือน)</summary>
        public int UnitSize { get; set; } = 1;
        /// <summary>ราคาต่อหน่วย (บาท · รวม/ไม่รวม VAT ตาม <c>SiteSettings.PlatformPriceIncludesVat</c> เดียวกับค่าบริการอื่น)</summary>
        public decimal UnitPrice { get; set; }
        public DateTime EffectiveFromUtc { get; set; } = DateTime.UtcNow;
        public DateTime? EffectiveToUtc { get; set; }
        public bool IsActive { get; set; } = true;
        public string? SetBy { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// รอบ 231 (§B.2 · คำตัดสินข้อ 231-1/231-3/231-4): <b>นโยบายส่วนเกินของบริษัท</b> — 1 แถวต่อบริษัท · ไม่มีแถว = <see cref="OverageMode.HardLock"/> (พฤติกรรมเดิม) ·
    /// เปิดโดยผู้มีสิทธิ์ BillingManage พร้อมยอมรับเงื่อนไข (<see cref="AcceptedPolicyVersion"/>) · ระบบปิดสิทธิ์เมื่อค้างชำระ (<see cref="LockedByUnpaidAt"/>) — ผู้เช่าเปิดเองไม่ได้จนกว่าจะชำระ ·
    /// ทุกการเปลี่ยนลง audit chain <c>PLATFORM-OVERAGE-POLICY</c>
    /// </summary>
    public class CompanyOveragePolicy : TenantEntity
    {
        public OverageMode Mode { get; set; } = OverageMode.HardLock;
        /// <summary>เพดานค่าส่วนเกินต่อเดือน (บาท · null = ไม่จำกัด) — ถึงเพดาน ⇒ กลับเป็นล็อกจนสิ้นเดือน + แจ้ง</summary>
        public decimal? MonthlyCapAmount { get; set; }
        /// <summary>ทรัพยากรที่ยอมให้เกิน (คั่นจุลภาค) · ว่าง = ทุกทรัพยากรที่มีราคา</summary>
        public string? ResourcesCsv { get; set; }
        public string? AcceptedPolicyVersion { get; set; }
        public string? AcceptedBy { get; set; }
        public DateTime? AcceptedAtUtc { get; set; }
        /// <summary>ระบบปิดสิทธิ์ส่วนเกินเพราะใบส่วนเกินค้างชำระเกินผ่อนผัน (ขั้น 1 ของคำตัดสินข้อ 231-4) · null = ไม่ถูกล็อก</summary>
        public DateTime? LockedByUnpaidAt { get; set; }
        public string? LockedReason { get; set; }
        /// <summary>งวด "yyyy-MM" ที่แจ้ง <c>overage.capped</c> (ถึงเพดานเงิน/ไม่มีราคา) ไปแล้ว — ครั้งเดียวต่อเดือน (UPDATE … WHERE ≠ งวด ⇒ หลายคำขอพร้อมกันส่งครั้งเดียว)</summary>
        public string? CapNotifiedPeriod { get; set; }
        /// <summary>งวดที่แจ้ง <c>overage.started</c> ไปแล้ว (ฝ่ายค้าน N2 — แทนการเดาจาก "ยอดก่อนหน้าเป็น 0")</summary>
        public string? StartedNotifiedPeriod { get; set; }
        /// <summary>งวดที่แจ้ง "ออกใบที่กฎหมายบังคับเกินโควตาโดยไม่คิดเงิน — ควรอัปเกรด" ไปแล้ว (คำตัดสิน S8)</summary>
        public string? FreeOverageNotifiedPeriod { get; set; }
    }

    /// <summary>
    /// รอบ 231 (§D.1 · คำตัดสินข้อ 231-6): <b>snapshot การใช้งานรายเดือน</b> — unique (CompanyId, Period, ResourceKey) · เก็บ 24 เดือน ·
    /// ผู้เขียน 3 แหล่ง (<see cref="Models.Enums.UsageSnapshotSource"/>) · <see cref="Limit"/> null = ไม่จำกัด · <see cref="WarnedPercent"/> = เกณฑ์เตือนสูงสุดที่แจ้งแล้วเดือนนี้
    /// (กันแจ้งซ้ำ — คำตัดสินข้อ 231-5)
    /// </summary>
    public class UsageMonthlySnapshot : TenantEntity
    {
        /// <summary>"yyyy-MM"</summary>
        public string Period { get; set; } = "";
        public string ResourceKey { get; set; } = "";
        public long Used { get; set; }
        public long? Limit { get; set; }
        public int Purchased { get; set; }
        public int OverageUnits { get; set; }
        public decimal OverageAmount { get; set; }
        public UsageSnapshotSource Source { get; set; } = UsageSnapshotSource.Daily;
        public DateTime SnapshotAtUtc { get; set; } = DateTime.UtcNow;
        public int WarnedPercent { get; set; }
    }

    /// <summary>
    /// รอบ 231 (§B.3/§B.5): ตัวอย่างพื้นที่รายวัน — ใช้คิดส่วนเกิน <c>storage.gb</c> เป็น GB-เดือน (สิ้นเดือน: ceil(เฉลี่ย max(ใช้ − เพดาน, 0) รายวัน)) ·
    /// unique (CompanyId, Date, ResourceKey) · เก็บเฉพาะบริษัทที่เปิดส่วนเกินพื้นที่ (งาน <c>UsageSnapshotJob</c>)
    /// </summary>
    public class UsageDailySnapshot : TenantEntity
    {
        /// <summary>วันตามปฏิทินไทย (date เท่านั้น · UTC kind)</summary>
        public DateTime Date { get; set; }
        public string ResourceKey { get; set; } = "";
        public long Used { get; set; }
        public long? Limit { get; set; }
        /// <summary>ราคาส่วนเกิน/GB-เดือน ที่มีผล ณ วันนั้น (snapshot — ฝ่ายค้าน B2: สิ้นเดือนคิดจากตัวอย่างเสมอ ไม่ถามนโยบาย/ราคาปัจจุบัน) · null = วันนั้นไม่ได้เปิด/ไม่มีราคา (ไม่เก็บตัวอย่าง)</summary>
        public decimal? UnitPrice { get; set; }
    }
}
