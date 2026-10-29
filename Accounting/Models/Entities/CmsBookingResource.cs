using Accounting.Helpers;
using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

/// <summary>ค่าตั้งการจองระดับไซต์ (หนึ่งแถวต่อไซต์) — บริการที่ติ๊ก "ใช้ค่าของไซต์" รับระยะเวลา/buffer/ล่วงหน้าจากที่นี่ ·
/// ตัวประกอบ: <c>Helpers/BookingSlotEngine.DefaultsOf</c></summary>
public class SiteBookingSettings : TenantEntity
{
    public Guid SiteId { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public int BufferBeforeMinutes { get; set; }
    public int BufferAfterMinutes { get; set; }
    public int MinAdvanceHours { get; set; } = 1;
    public int MaxAdvanceDays { get; set; } = 30;
    /// <summary>จังหวะออกใบกำกับภาษีของบริการแบบ Guaranteed (คำตัดสินข้อ 155 · ตัวตัดสิน <c>Helpers/BookingInvoiceTimingPolicy</c>) · 0 = เดิม (ใบกำกับตอนจอง)</summary>
    public BookingInvoiceTiming GuaranteedInvoiceTiming { get; set; } = BookingInvoiceTiming.TaxInvoiceAtBooking;

    // ── รอบ 205 แพ็ก W3 (R-12 R-13 R-14) ──
    /// <summary>เวลาทำการระดับเว็บ (JSON รูป <c>BookingScheduleDto</c> · ตัวแปลง <c>Helpers/BookingSiteHours</c>) — ค่าตั้งต้นของบริการที่ไม่ตั้งเวลาเอง
    /// (ตัวตัดสิน <c>BookingSlotEngine.ScheduleOf</c>) · null = ไม่ตั้ง</summary>
    public string? BookingHoursJson { get; set; }
    /// <summary>คิวสำรองได้คิวแล้ว "ถือรอบ" ให้ยืนยันกี่นาที (0 = ไม่ถือ) · หมดเวลาแล้วยังรอยืนยัน ⇒ job ปล่อยรอบ (<c>BookingReminderPlan.HoldExpired</c>)</summary>
    public int WaitlistHoldMinutes { get; set; }
    /// <summary>เปิดเตือนลูกค้าก่อนนัด (อีเมล/LINE) ผ่าน <c>BookingReminderJob</c></summary>
    public bool ReminderEnabled { get; set; } = true;
    /// <summary>กี่ชั่วโมงก่อนนัด คั่นด้วยจุลภาค เช่น "24,2" (<c>BookingReminderPlan.ParseHours</c>)</summary>
    public string ReminderHoursBefore { get; set; } = BookingReminderPlan.DefaultHoursCsv;
    /// <summary>เทมเพลตข้อความเตือน (ตัวแทน <c>BookingReminderPlan.Placeholders</c>) · null = ข้อความเริ่มต้นของระบบ</summary>
    public string? ReminderTemplate { get; set; }
    /// <summary>เปิดให้ลูกค้าจ่ายมัดจำออนไลน์ผ่าน gateway (คำตัดสินเจ้าของ Q5 = ข: ปิดเป็นค่าเริ่มต้น ไซต์เปิดเอง · รอบ 205 W2 R-08)</summary>
    public bool OnlineDepositEnabled { get; set; }

    // ── รอบ 207 (ผู้ให้บริการ · ฟอร์มจอง · เอกสารตอนเสร็จ) ──
    /// <summary>เปิด "จัดสรรผู้ให้บริการ" ของไซต์ · null = แถวก่อนรอบ 207 ⇒ ตัดสินจากข้อมูลเดิม (มีผู้ให้บริการ/บริการที่ใช้ผู้ให้บริการ = เปิด)
    /// — ตัวตัดสินเดียว <c>Helpers/BookingProviderAllocation.Enabled</c></summary>
    public bool? ProviderAllocationEnabled { get; set; }
    /// <summary>ตรรกะแนะนำ/จัดผู้ให้บริการ (<c>BookingSlotEngine.RankResources</c>) · 0 = งานน้อยสุดของวัน (เดิม)</summary>
    public BookingRecommendStrategy ProviderRecommendStrategy { get; set; } = BookingRecommendStrategy.LeastLoadedDay;
    /// <summary>ช่องบังคับ/ไม่บังคับ/ซ่อนของฟอร์มจอง (JSON · <c>Helpers/BookingFormFields</c>) · null = ค่าเดิม (ชื่อ+เบอร์บังคับ · อีเมล/หมายเหตุไม่บังคับ)</summary>
    public string? FormFieldsJson { get; set; }
    /// <summary>เอกสารตอนกดเสร็จ (ไม่รับชำระ) ของบริการแบบนัดหมาย (<c>BookingInvoiceTimingPolicy</c>) · 0 = ไม่ออก (เดิม) · เจ้าของเปิด Auto เอง</summary>
    public BookingCompletionDocument CompletionDocument { get; set; } = BookingCompletionDocument.None;
}

/// <summary>ผู้ให้บริการ / ห้อง / อุปกรณ์ ที่จองได้ (ช่าง · หมอ · เทรนเนอร์ · ห้องประชุม · รถ) ต่อไซต์</summary>
public class SiteBookingResource : TenantEntity
{
    public Guid SiteId { get; set; }
    public string Name { get; set; } = "";
    public BookingResourceKind Kind { get; set; } = BookingResourceKind.Staff;
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    /// <summary>รับงานพร้อมกันได้กี่งาน (ช่าง 1 · ห้อง 1) · คลาสกลุ่มนับที่นั่งจากบริการ ไม่ใช่จากตรงนี้</summary>
    public int Capacity { get; set; } = 1;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>รอบ 207: ผู้ใช้ระบบ (พนักงาน) ของผู้ให้บริการคนนี้ — POS เติมช่างให้บรรทัด/ค่าคอม · null = ไม่ผูก</summary>
    public Guid? StaffUserId { get; set; }

    // ── รอบ 209 · P5 (R-209-C6/D8 · คำตัดสินข้อ 168 ฏ) — ตั้งที่หน้า "สรุปงานผู้ให้บริการ" (endpoint แยก · ฟอร์มผู้ให้บริการเดิมไม่แตะช่องเหล่านี้) ──
    /// <summary>พนักงาน (ทะเบียน HR) ของผู้ให้บริการ — ฐานค่าคอม/ค่าตอบแทน (<c>CommissionService</c> ฐาน BookingServices/InstructorPay) · null = ไม่ผูก</summary>
    public Guid? EmployeeId { get; set; }
    public InstructorPayMode PayMode { get; set; } = InstructorPayMode.None;
    /// <summary>บาทต่อคลาส (PerClass) · บาทต่อหัว (PerHead) · เปอร์เซ็นต์ (PercentOfRevenue)</summary>
    public decimal PayRate { get; set; }
    /// <summary>ขั้นต่ำต่อคลาส (PerHead เท่านั้น · 0 = ไม่มีขั้นต่ำ)</summary>
    public decimal PayMinimumPerClass { get; set; }

    public ICollection<SiteBookingResourceService> Services { get; set; } = new List<SiteBookingResourceService>();
    public ICollection<SiteBookingResourceSlot> Slots { get; set; } = new List<SiteBookingResourceSlot>();
}

/// <summary>"ประเภทบริการที่รับได้" — ผู้ให้บริการคนนี้ทำบริการนี้ได้</summary>
public class SiteBookingResourceService : TenantEntity
{
    public Guid ResourceId { get; set; }
    public SiteBookingResource Resource { get; set; } = null!;
    public Guid BookingServiceId { get; set; }
    /// <summary>รอบ 207: คนหลักของบริการนี้ (โหมดแนะนำ "คนหลักก่อน")</summary>
    public bool IsPrimary { get; set; }
    /// <summary>รอบ 207: ลำดับความสำคัญต่อบริการ (เลขน้อย = ก่อน · โหมด "ตามลำดับความสำคัญ")</summary>
    public int Priority { get; set; }
}

/// <summary>รอบ 207: ผู้ให้บริการที่ลงมือทำการจองนี้จริง (ได้มากกว่า 1 คน) + สัดส่วนงาน % — ตั้งตอน "เริ่มให้บริการ" ·
/// ผลรวม = 100 (ตัวตรวจเดียว <c>Helpers/BookingProviderShare</c>) · ผู้ให้บริการหลัก (สัดส่วนมากสุด) = <c>SiteBooking.ResourceId</c> ·
/// คนอื่นกินเวลาว่างของตัวเองด้วย (<c>LoadDayAsync</c>) · รายงาน <c>Helpers/BookingProviderReport</c></summary>
public class SiteBookingAssignment : TenantEntity
{
    public Guid SiteId { get; set; }
    public Guid BookingId { get; set; }
    public Guid ResourceId { get; set; }
    /// <summary>ชื่อ ณ ตอนมอบหมาย (รายงานย้อนหลังยังอ่านได้แม้ลบผู้ให้บริการ)</summary>
    public string ResourceName { get; set; } = "";
    public decimal SharePercent { get; set; }
}

/// <summary>ตารางงานรายบุคคล — เวลาเข้า-ออกงานรายสัปดาห์ · วันหยุด/วันลา (SpecificDate + IsBlocked) · ไม่มีแถว = ว่างตลอดเวลาทำการของบริการ</summary>
public class SiteBookingResourceSlot : TenantEntity
{
    public Guid ResourceId { get; set; }
    public SiteBookingResource Resource { get; set; } = null!;
    public DayOfWeek? DayOfWeek { get; set; }
    public DateOnly? SpecificDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public bool IsBlocked { get; set; }
}
