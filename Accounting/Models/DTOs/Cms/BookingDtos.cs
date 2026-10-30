using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;
using Accounting.Helpers;

namespace Accounting.Models.DTOs.Cms;

// ==================== Booking Service ====================

public class CreateBookingServiceRequest
{
    [Required, MaxLength(256)]
    public string Name { get; set; } = "";
    [MaxLength(256)]
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? Slug { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public int BufferMinutes { get; set; } = 0;
    public int MaxCapacity { get; set; } = 1;
    public int MaxAdvanceBookingDays { get; set; } = 30;
    public int MinAdvanceBookingHours { get; set; } = 1;
    public decimal Price { get; set; }
    public decimal? DepositAmount { get; set; }
    public decimal? DepositPercent { get; set; }
    public BookingType BookingType { get; set; } = BookingType.Lead;
    public Guid? ProductId { get; set; }
    public string? Category { get; set; }
    public bool AutoConfirm { get; set; } = false;
    public int? FreeCancellationHours { get; set; }
    public decimal? CancellationFeePercent { get; set; }

    // ── รอบที่เปิดให้จอง (Helpers/BookingSlotEngine) — ค่าเริ่มต้น = พฤติกรรมเดิมสำหรับผู้เรียก API ที่ไม่ส่งฟิลด์ ──
    public BookingSlotMode SlotMode { get; set; } = BookingSlotMode.Windows;
    public int? SlotIntervalMinutes { get; set; }
    public int BufferBeforeMinutes { get; set; }
    public BookingCapacityUnit CapacityUnit { get; set; } = BookingCapacityUnit.Bookings;
    public int MaxUnitsPerBooking { get; set; } = 1;
    [MaxLength(8)]
    public string? HalfDaySplitTime { get; set; }
    [MaxLength(64)]
    public string? CapacityGroup { get; set; }
    /// <summary>สวิตช์คุม Slot (true = บล็อกเมื่อเต็ม · false = ยอมรับจองซ้อนได้ถึง OverlapLimit · null = ไม่จำกัด)</summary>
    public bool EnforceSlotCapacity { get; set; } = true;
    public int? OverlapLimit { get; set; }
    public bool WaitlistEnabled { get; set; }
    /// <summary>รอบ 215 เฟส 2: <c>null</c> = ตามค่าเว็บ · ไม่ส่งช่องนี้ (ผู้เรียกเดิม) = None ตามเดิม</summary>
    public BookingResourceMode? ResourceMode { get; set; } = BookingResourceMode.None;
    /// <summary>true = ระยะเวลา/buffer/ล่วงหน้า ใช้ค่าตั้งของไซต์ · false = ค่าของบริการนี้ (override)</summary>
    public bool UseSiteDefaults { get; set; }
    /// <summary>รอบ 210: ต้องเป็นสมาชิกก่อนจอง (Inherit = ตามเว็บ · Required · NotRequired) — <b>null = ไม่แตะค่าเดิม</b> (ผู้เรียก API เดิม · สร้างใหม่ = ตามเว็บ)</summary>
    public BookingMemberAccess? MemberAccess { get; set; }
    /// <summary>เวลาทำการ + วันหยุด — <b>null = ไม่แตะตารางเวลาเดิม</b> (ผู้เรียก API เดิม) · ส่งมา = แทนที่ทั้งชุด</summary>
    public BookingScheduleDto? Schedule { get; set; }
}

/// <summary>ค่าตั้งการจองระดับไซต์ (บริการที่ "ใช้ค่าของไซต์" รับไป)</summary>
public class BookingSiteSettingsDto
{
    // ฝ่ายค้านรอบ 215 เฟส 2: ตอนบันทึก null/ไม่ส่ง = คงค่าเดิม (เดิมไม่ส่ง = รีเซ็ตเป็นค่าเริ่มต้นเงียบ ๆ) · GET มีค่าเสมอ
    public int? DurationMinutes { get; set; }
    public int? BufferBeforeMinutes { get; set; }
    public int? BufferAfterMinutes { get; set; }
    public int? MinAdvanceHours { get; set; }
    public int? MaxAdvanceDays { get; set; }
    /// <summary>จังหวะออกใบกำกับภาษีของบริการแบบ Guaranteed (คำตัดสินข้อ 155) · คำตอบมีค่าเสมอ · ตอนบันทึก null = คงค่าเดิม (ไม่รีเซ็ตเงียบ)</summary>
    public BookingInvoiceTiming? GuaranteedInvoiceTiming { get; set; }
    /// <summary>ตัวเลือก + ผลทางภาษี (server ประกอบจาก <c>BookingInvoiceTimingPolicy.Options</c> · หน้าเว็บแสดงตรง ๆ) — ไม่อ่านตอนบันทึก</summary>
    public List<BookingInvoiceTimingOption> GuaranteedInvoiceTimingOptions { get; set; } = new();

    // ── รอบ 205 W3 ──
    /// <summary>เวลาทำการระดับเว็บ (R-14) — ค่าตั้งต้นของบริการที่ไม่ตั้งเวลาเอง · คำตอบมีค่าเสมอ (ว่าง = ไม่ตั้ง) · ตอนบันทึก null = คงค่าเดิม · ส่งตารางว่าง = ล้างค่า</summary>
    public BookingScheduleDto? Schedule { get; set; }
    /// <summary>คิวสำรองได้คิวแล้วถือรอบให้ยืนยันกี่นาที (0 = ไม่ถือ) (R-13) · ตอนบันทึก null = คงค่าเดิม</summary>
    public int? WaitlistHoldMinutes { get; set; }
    /// <summary>เตือนก่อนนัด เปิด/ปิด (R-12) · ตอนบันทึก null = คงค่าเดิม</summary>
    public bool? ReminderEnabled { get; set; }
    /// <summary>กี่ชั่วโมงก่อนนัด เช่น "24,2" · ตอนบันทึก null = คงค่าเดิม</summary>
    public string? ReminderHoursBefore { get; set; }
    /// <summary>เทมเพลตข้อความเตือน · ตอนบันทึก null = คงค่าเดิม · "" = กลับไปใช้ข้อความระบบ</summary>
    public string? ReminderTemplate { get; set; }
    /// <summary>รอบ 205 W2 R-08 (Q5 = ข): เปิดรับมัดจำออนไลน์ของบริการชำระล่วงหน้า · null ตอนบันทึก = คงค่าเดิม</summary>
    public bool? OnlineDepositEnabled { get; set; }
    /// <summary>ข้อความเริ่มต้นของระบบ + ตัวแทนที่ใช้ได้ (server ประกาศ · หน้าเว็บแสดงเป็นคำใบ้ — ไม่อ่านตอนบันทึก)</summary>
    public string? ReminderDefaultTemplate { get; set; }
    public List<string> ReminderPlaceholders { get; set; } = new();

    // ── รอบ 207 ── (ตอนบันทึก null = คงค่าเดิมทุกช่อง)
    /// <summary>เปิดจัดสรรผู้ให้บริการของไซต์ · คำตอบ = ค่าที่มีผลจริง (แถวเก่า null ⇒ ตัดสินจากข้อมูลเดิม · <c>BookingProviderAllocation.Enabled</c>)</summary>
    public bool? ProviderAllocationEnabled { get; set; }
    /// <summary>ตรรกะแนะนำ/จัดผู้ให้บริการ</summary>
    public BookingRecommendStrategy? ProviderRecommendStrategy { get; set; }
    public List<BookingProviderAllocation.StrategyOption> ProviderStrategyOptions { get; set; } = new();
    /// <summary>ช่องบังคับของฟอร์มจอง (<c>BookingFormFields</c>)</summary>
    public BookingFormFieldsConfig? FormFields { get; set; }
    public List<BookingFormFields.FieldOption> FormFieldOptions { get; set; } = new();
    /// <summary>เอกสารตอนให้บริการเสร็จ (บริการแบบนัดหมาย/Lead ที่มียอด)</summary>
    public BookingCompletionDocument? CompletionDocument { get; set; }
    public List<BookingDocumentFlow.Option> CompletionDocumentOptions { get; set; } = new();
    /// <summary>ตารางสรุป "ออกเอกสารอะไร ขั้นไหน" ต่อชนิดบริการ ตามค่าตั้งปัจจุบัน + สถานะ VAT ของบริษัท (server ประกอบ — หน้าเว็บแสดงตรง)</summary>
    public List<BookingDocumentFlow.StageRow> DocumentFlow { get; set; } = new();

    // ── รอบ 210 (คำตัดสินข้อ 169) ──
    /// <summary>ต้องเข้าสู่ระบบสมาชิกก่อนจอง (ทุกบริการที่ไม่ตั้งเอง) · คำตอบมีค่าเสมอ · ตอนบันทึก null = คงค่าเดิม</summary>
    public bool? RequireMemberToBook { get; set; }
    /// <summary>รอบ 215 เฟส 2: ค่าเริ่มต้นโหมดผู้ให้บริการของเว็บ (บริการ "ตามค่าเว็บ" รับไป) · ส่ง null = คงค่าเดิม · GET = ค่าที่มีผล (ไม่ตั้ง = None)</summary>
    public BookingResourceMode? DefaultResourceMode { get; set; }
    /// <summary>GET เท่านั้น: ลูกค้าเห็นอะไรบนหน้าจองตามค่าเว็บ (BookingProviderAllocation.CustomerPreview — หน้าแค่แสดง)</summary>
    public string? DefaultResourceModePreview { get; set; }
    public bool DefaultResourceModeShowsPicker { get; set; }
    /// <summary>GET เท่านั้น: บริการที่ "กำหนดเอง" (ไม่ตามค่าเว็บ) — แสดงพร้อมปุ่ม "ใช้ตามค่าเว็บ"</summary>
    public List<BookingServiceModeOverride> ResourceModeOverrides { get; set; } = new();
    /// <summary>GET เท่านั้น: จำนวนบริการที่ใช้ "ตามค่าเว็บ"</summary>
    public int ResourceModeInheritingCount { get; set; }
    /// <summary>GET เท่านั้น: ผู้ให้บริการที่ใช้งานอยู่ (0 = ยังไม่มี — หน้าบอกทางไปเพิ่ม)</summary>
    public int ActiveProviderCount { get; set; }
    /// <summary>ตัวเลือก "ต้องเป็นสมาชิก" ต่อบริการ (server ประกาศป้าย · <c>BookingAccessRule.Options</c>) — ไม่อ่านตอนบันทึก</summary>
    public List<BookingAccessRule.Option> MemberAccessOptions { get; set; } = new();
}

/// <summary>รอบ 207: บริการที่ผู้ให้บริการรับได้ + คนหลัก/ลำดับความสำคัญต่อบริการ</summary>
public class BookingResourceServiceLinkDto
{
    public Guid ServiceId { get; set; }
    public bool IsPrimary { get; set; }
    public int Priority { get; set; }
}

/// <summary>พนักงานเปลี่ยน/เลือกผู้ให้บริการของการจอง (R-16) · <see cref="ResourceId"/> null = ให้ระบบจัดให้ใหม่ — server ตัดสินผ่าน <c>BookingSlotEngine.Decide</c>/<c>PickResource</c> ตัวเดียว</summary>
public record AssignBookingResourceRequest(Guid? ResourceId);

/// <summary>ผู้ให้บริการ/ห้อง/อุปกรณ์ — <see cref="ServiceIds"/> = ประเภทบริการที่รับได้ · <see cref="Schedule"/> = เวลาเข้า-ออกงาน + วันหยุด/วันลา
/// (ว่าง = ว่างตลอดเวลาทำการของบริการ)</summary>
public class BookingResourceRequest
{
    [MaxLength(256)]
    public string? Name { get; set; }
    public BookingResourceKind Kind { get; set; } = BookingResourceKind.Staff;
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public int Capacity { get; set; } = 1;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<Guid> ServiceIds { get; set; } = new();
    public BookingScheduleDto Schedule { get; set; } = new();
    /// <summary>รอบ 207: บริการที่รับ + คนหลัก/ลำดับ · ส่งมา (ไม่ null) ⇒ ใช้แทน <see cref="ServiceIds"/> · null = ผู้เรียกเดิม (ServiceIds · ไม่แตะคนหลัก/ลำดับเดิม)</summary>
    public List<BookingResourceServiceLinkDto>? ServiceLinks { get; set; }
    /// <summary>รอบ 207: ผู้ใช้ระบบ (พนักงาน) ของผู้ให้บริการ — POS เติมช่าง/ค่าคอม · null = ไม่ผูก</summary>
    public Guid? StaffUserId { get; set; }
}

public class BookingResourceResponse : BookingResourceRequest
{
    public Guid Id { get; set; }
}

/// <summary>ผู้ให้บริการที่ลูกค้าเห็น (เฉพาะชื่อ/ชนิด/รูป — ไม่เปิดเผยตารางงาน)</summary>
public class BookingResourceBrief
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public BookingResourceKind Kind { get; set; }
    public string? ImageUrl { get; set; }
}

/// <summary>ตารางเวลาของบริการ — แปลงเป็นแถว <c>SiteBookingSlot</c> (รายสัปดาห์ / รายวันที่)</summary>
public class BookingScheduleDto
{
    public List<BookingWeeklyHoursDto> Weekly { get; set; } = new();
    public List<BookingDateExceptionDto> Exceptions { get; set; } = new();
}

/// <summary>ช่วงเวลารายสัปดาห์ — วันเดียวมีได้หลายช่วง (เช่น 09:00–12:00 + 13:00–18:00) · <see cref="IsBlocked"/> = ช่วงพัก</summary>
public class BookingWeeklyHoursDto
{
    public DayOfWeek DayOfWeek { get; set; }
    public string Start { get; set; } = "09:00";
    public string End { get; set; } = "18:00";
    public bool IsBlocked { get; set; }
    /// <summary>ความจุของช่วง (ใช้เฉพาะโหมดช่วงกำหนดเอง) · null = ความจุของบริการ</summary>
    public int? Capacity { get; set; }
}

/// <summary>วันพิเศษ: <see cref="Closed"/> = ปิด (ไม่ระบุเวลา = ทั้งวัน · ระบุ = ปิดบางช่วง) · ไม่ปิด = เวลาทำการพิเศษของวันนั้นแทนรายสัปดาห์</summary>
public class BookingDateExceptionDto
{
    public string Date { get; set; } = "";
    public bool Closed { get; set; } = true;
    public string? Start { get; set; }
    public string? End { get; set; }
    public string? Note { get; set; }
}

/// <summary>แม่แบบบริการตามประเภทธุรกิจ (ตัวตั้ง: <c>CmsBookingServiceSeeder.Presets</c>) — หน้า CMS ใช้เติมฟอร์ม "เพิ่มจากแม่แบบ"</summary>
public class BookingServicePresetResponse
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Description { get; set; }
    public List<CreateBookingServiceRequest> Services { get; set; } = new();
}

public class UpdateBookingServiceRequest : CreateBookingServiceRequest
{
    public bool? IsActive { get; set; }
}

public class BookingServiceResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string Slug { get; set; } = "";
    public int DurationMinutes { get; set; }
    public int BufferMinutes { get; set; }
    public int MaxCapacity { get; set; }
    public int MaxAdvanceBookingDays { get; set; }
    public int MinAdvanceBookingHours { get; set; }
    public decimal Price { get; set; }
    public decimal? DepositAmount { get; set; }
    public decimal? DepositPercent { get; set; }
    public string Currency { get; set; } = "THB";
    public BookingType BookingType { get; set; }
    public Guid? ProductId { get; set; }
    /// <summary>รอบ 207: รหัส/ชื่อสินค้า/บริการที่ผูก + คำเตือน (ยังไม่ผูก · ราคาไม่ตรง)</summary>
    public string? ProductCode { get; set; }
    public string? ProductName { get; set; }
    public string? ProductWarning { get; set; }
    public string? Category { get; set; }
    public bool AutoConfirm { get; set; }
    public int? FreeCancellationHours { get; set; }
    public decimal? CancellationFeePercent { get; set; }
    public BookingSlotMode SlotMode { get; set; }
    public int? SlotIntervalMinutes { get; set; }
    public int BufferBeforeMinutes { get; set; }
    public BookingCapacityUnit CapacityUnit { get; set; }
    public int MaxUnitsPerBooking { get; set; } = 1;
    public string? HalfDaySplitTime { get; set; }
    public string? CapacityGroup { get; set; }
    public bool EnforceSlotCapacity { get; set; } = true;
    public int? OverlapLimit { get; set; }
    public bool WaitlistEnabled { get; set; }
    /// <summary>ค่าที่ตั้งบนบริการ (ฟอร์ม hydrate) · null = ตามค่าเว็บ (รอบ 215)</summary>
    public BookingResourceMode? ResourceMode { get; set; }
    /// <summary>รอบ 215: "site" = ตามค่าเว็บ · "service" = กำหนดเอง (ป้ายบนรายการบริการ)</summary>
    public string ResourceModeSource { get; set; } = "service";
    public bool UseSiteDefaults { get; set; }
    /// <summary>รอบ 210: ค่าที่ตั้งบนบริการ (Inherit/Required/NotRequired — ฟอร์มแก้ไข hydrate จากตัวนี้)</summary>
    public BookingMemberAccess MemberAccess { get; set; }
    /// <summary>รอบ 210: ต้องเข้าสู่ระบบสมาชิกก่อนจองจริงไหม (server ตัดสินจากบริการ + เว็บ — <c>BookingAccessRule.RequiresMember</c>) · หน้าจองอ่านตัวนี้</summary>
    public bool MemberOnly { get; set; }
    /// <summary>ระยะเวลาที่ใช้จริง (ของไซต์เมื่อ UseSiteDefaults) — หน้าเว็บแสดงตัวนี้</summary>
    public int EffectiveDurationMinutes { get; set; }
    /// <summary>รอบ 207: โหมดผู้ให้บริการที่มีผลจริง (ไซต์ปิดจัดสรร ⇒ None) — หน้าจองของลูกค้าอ่านช่องนี้</summary>
    public BookingResourceMode EffectiveResourceMode { get; set; }
    /// <summary>รอบ 207: ช่องบังคับ/ซ่อนของฟอร์มจอง (ค่าของไซต์) — หน้าจองแสดงตาม · ด่านจริงอยู่ที่เซิร์ฟเวอร์</summary>
    public BookingFormFieldsConfig FormFields { get; set; } = BookingFormFields.Default;
    /// <summary>ผู้ให้บริการที่ทำบริการนี้ได้ (เปิดใช้อยู่) — ลูกค้าเลือกเมื่อโหมด "บังคับระบุ"/"ลูกค้าเลือก"</summary>
    public List<BookingResourceBrief> Resources { get; set; } = new();
    public BookingScheduleDto Schedule { get; set; } = new();
    /// <summary>มีเวลาทำการอย่างน้อย 1 ช่วง (ของบริการเอง หรือรับจากเว็บ) — false = ลูกค้าจะไม่เห็นเวลาว่างเลย (หน้า CMS เตือน)</summary>
    public bool HasOpeningHours { get; set; }
    /// <summary>บริการนี้ไม่ตั้งเวลาทำการเอง และรับเวลาทำการระดับเว็บอยู่ (ตัวตัดสิน <c>BookingSlotEngine.ScheduleOf</c> · R-14) — <c>Schedule</c> ข้างบนคือของบริการเอง (ว่าง)</summary>
    public bool UsesSiteHours { get; set; }
    /// <summary>ข้อความความละเอียดรอบที่ server ประกอบ (เช่น "ทุก 30 นาที · ครั้งละ 60 นาที")</summary>
    public string? SlotSummary { get; set; }
    /// <summary>ข้อความนโยบายมัดจำ/ยกเลิกที่ server ประกอบ — หน้าเว็บแสดงตรง ๆ</summary>
    public string? PolicySummary { get; set; }
    public bool IsActive { get; set; }
    public int UpcomingBookingsCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ==================== Booking Slot ====================

public class CreateBookingSlotRequest
{
    public DayOfWeek? DayOfWeek { get; set; }
    [Required]
    public string StartTime { get; set; } = "09:00";
    [Required]
    public string EndTime { get; set; } = "10:00";
    public string? SpecificDate { get; set; }
    public bool IsBlocked { get; set; } = false;
    public int MaxCapacity { get; set; } = 1;
}

public class BookingSlotResponse
{
    public Guid Id { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string? SpecificDate { get; set; }
    public bool IsBlocked { get; set; }
    public int MaxCapacity { get; set; }
    // รอบ 209 P4 (C9): CurrentBookings/AvailableSlots ถูกลบ — ไม่มีผู้เขียน ⇒ ตอบ "ว่าง = ความจุเต็ม" เสมอ (ค่าโกหก) ·
    // ที่ว่างจริงของรอบ = GET …/available-slots (BookingSlotEngine) หรือ GET …/classes (ตารางคลาส)
    public bool IsActive { get; set; }
}

// ==================== Booking ====================

public class CreateBookingRequest
{
    [Required]
    public Guid BookingServiceId { get; set; }
    public Guid? CustomerId { get; set; }
    [Required]
    public DateTime BookingDate { get; set; }
    [Required]
    public string StartTime { get; set; } = "";

    // Guest info (when no customer account)
    public string? GuestName { get; set; }
    public string? GuestEmail { get; set; }
    public string? GuestPhone { get; set; }
    public int GuestCount { get; set; } = 1;
    /// <summary>จำนวนช่วงติดกัน (โหมดทุก N นาที) · ค่าเดิม 1</summary>
    public int Units { get; set; } = 1;
    /// <summary>ผู้ให้บริการที่ลูกค้าเลือก (โหมดบังคับระบุ/ลูกค้าเลือก) · โหมดระบบจัดให้ ⇒ ไม่ใช้ค่านี้</summary>
    public Guid? ResourceId { get; set; }
    public string? CustomerNotes { get; set; }

    // Payment info
    public Guid? PaymentGatewayId { get; set; }
    public string? PaymentReference { get; set; }

    // ── จ่ายด้วยบัตรคอร์ส (รอบ 205 W6 · R-39 · Helpers/CreditBookingLink) ──
    /// <summary>true = กันเครดิตจากบัตรของลูกค้า (ต้องมี CustomerId ที่ผูกผู้ติดต่อ) · ไม่ออกเอกสาร ERP · ยอดเงินการจอง = 0</summary>
    public bool PayWithCredit { get; set; }
    /// <summary>บัตรที่ใช้ · ว่าง = ระบบเลือกบัตรที่หมดอายุก่อน (FEFO) ที่ใช้กับบริการนี้ได้</summary>
    public Guid? CreditPassId { get; set; }

    /// <summary>รอบ 217 (คำตัดสินข้อ 175 ข): ผู้จองตั้งใจจองรอบเดิมเพิ่มอีกที่ (กดยืนยันหลังได้คำเตือน "คุณจองรอบนี้ไว้แล้ว") ⇒ ข้ามด่านซ้ำ
    /// <c>DuplicateSubmitGuard</c> · ความจุ/ด่านอื่นยังตรวจครบ · ค่าเริ่มต้น false</summary>
    public bool ConfirmAdditional { get; set; }

    /// <summary>รอบ 209 P6 (ฑ): ลูกค้าติ๊กยินยอม PDPA บนฟอร์มจอง — บังคับเมื่อเว็บตั้ง RequireDataConsent (ตรวจที่เซิร์ฟเวอร์)</summary>
    public bool ConsentGiven { get; set; }
    /// <summary>รอบ 216 (คำตัดสินข้อ 174): สมาชิกติ๊ก "บันทึกเป็นข้อมูลของฉัน" ⇒ หลังจองสำเร็จบันทึกชื่อ/เบอร์ลงโปรไฟล์ ·
    /// สมาชิก = id ในโทเคนหน้าร้านของเว็บนี้เท่านั้น · แขก/พนักงาน/โทเคนเว็บอื่น ⇒ ไม่มีผล</summary>
    public bool SaveToProfile { get; set; }
}

public class UpdateBookingStatusRequest
{
    [Required]
    public BookingStatus Status { get; set; }
    public string? InternalNotes { get; set; }
    public string? CancellationReason { get; set; }
    /// <summary>รอบ 208: สถานะที่ผู้เรียกคาดว่าเป็นอยู่ (job ปล่อยรอบคิวสำรอง) · ไม่ตรงตอนถือล็อก ⇒ ไม่ทำ · null = ไม่ตรวจ</summary>
    public BookingStatus? ExpectedCurrentStatus { get; set; }
    /// <summary>รอบ 208 (ฝ่ายค้านรอบ 2 ข้อ 1b): job ปล่อยที่ — ตรวจซ้ำใต้ล็อกว่ายังไม่รับเงิน + พ้นเวลาถือที่ ณ เวลาอ้างอิงของ job (เงินเข้าหลังคิวรีของ job ⇒ ไม่ยกเลิก) ·
    /// null = ไม่ตรวจ · ใช้เวลาของ job ไม่ใช่นาฬิกาเครื่อง (job/เทสต์ส่งเวลาอ้างอิงเอง)</summary>
    public DateTime? HoldLapsedAsOfUtc { get; set; }
}

public class BookingResponse
{
    public Guid Id { get; set; }
    public string BookingNumber { get; set; } = "";
    public BookingStatus Status { get; set; }
    public Guid BookingServiceId { get; set; }
    public string ServiceName { get; set; } = "";
    public Guid? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? GuestName { get; set; }
    public string? GuestEmail { get; set; }
    public string? GuestPhone { get; set; }
    public int GuestCount { get; set; }
    public DateTime BookingDate { get; set; }
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public int DurationMinutes { get; set; }
    public int Units { get; set; } = 1;
    public Guid? ResourceId { get; set; }
    public string? ResourceName { get; set; }
    /// <summary>ลำดับคิวสำรอง (สถานะ Waitlisted) · null = ไม่ได้อยู่ในคิว</summary>
    public int? WaitlistPosition { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public string Currency { get; set; } = "THB";
    public BookingType BookingType { get; set; }
    public ErpBookingDocumentType? ErpDocumentType { get; set; }
    public Guid? ErpDocumentId { get; set; }
    public string? CustomerNotes { get; set; }
    public string? InternalNotes { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>รอบ 194 (C-1): ผลของขั้น ERP อัตโนมัติตอนเปลี่ยนสถานะ (ยกเลิก/เสร็จ) ที่ผู้กดต้องรู้ — ใบมัดจำไม่ถูกยกเลิก ·
    /// ต้องออกใบกำกับเอง · ขั้นที่ล้มพร้อมเหตุ · ว่าง = ไม่มีอะไรต้องทำ (ข้อความเดียวกันถูกประทับบน <c>InternalNotes</c> ด้วย)</summary>
    public List<string> ErpNotices { get; set; } = new();
    /// <summary>รอบ 217 (ข้อ 175 ข): คำขอนี้ไม่ได้สร้างใบใหม่ — คืนใบเดิมของผู้จองคนเดิม (กดซ้ำ/สองแท็บ) · หน้าเว็บแสดง "คุณจองรอบนี้ไว้แล้ว"</summary>
    public bool Reused { get; set; }
    /// <summary>เลขที่ใบเดิมเมื่อ <see cref="Reused"/></summary>
    public string? DuplicateOf { get; set; }
    /// <summary>R-39: เลขบัตรคอร์สที่ใช้จ่ายการจองนี้ · null = การจองชำระปกติ</summary>
    public string? CreditPassNumber { get; set; }
    /// <summary>รอบ 209 P4 (คำตัดสินข้อ 168 ญ): นโยบายยกเลิกที่ใช้กับการจองนี้ — "CreditPass" (จองด้วยบัตร ⇒ กติกาของแพ็กเกจชนะ) / "Service" ·
    /// ตัวตัดสินเดียว <c>CreditBookingLink.CancellationPolicy</c> (server ประกอบ หน้าเว็บแสดง)</summary>
    public string CancellationPolicySource { get; set; } = "Service";
    /// <summary>ข้อความนโยบายยกเลิกที่ใช้จริง · null = บริการไม่ได้ตั้งนโยบาย</summary>
    public string? CancellationPolicy { get; set; }
    /// <summary>แสดงปุ่ม "รับเงินแล้ว — ออกใบกำกับภาษี" ไหม (server ตัดสินด้วย <c>BookingInvoiceTimingPolicy.Decide(PaymentReceived)</c> · คำตัดสินข้อ 155)</summary>
    public bool CanIssueTaxInvoiceNow { get; set; }

    // ── รอบ 205 W3 ──
    /// <summary>โหมดผู้ให้บริการของบริการ — None = ไม่มีช่องเลือกผู้ให้บริการ (R-16)</summary>
    public BookingResourceMode ResourceMode { get; set; }
    /// <summary>เปลี่ยน/เลือกผู้ให้บริการได้ไหม (server ตัดสิน: บริการใช้ผู้ให้บริการ + สถานะรอยืนยัน/ยืนยันแล้ว + นัดยังไม่ผ่าน)</summary>
    public bool CanAssignResource { get; set; }
    // ── รอบ 205 W2 (R-08 R-10): เส้นเงิน/ลิงก์ลูกค้า — ค่าทุกตัวจากเซิร์ฟเวอร์ (Helpers/CmsBookingOnlinePayment · BookingCancellationFee) หน้าเว็บแสดงอย่างเดียว
    /// <summary>โทเคนลิงก์ลูกค้า (คืนเฉพาะคำตอบของเส้นสร้าง/เส้น by-token — เส้นพนักงานได้ด้วยเพื่อคัดลอกลิงก์)</summary>
    public string? PublicToken { get; set; }
    /// <summary>มัดจำที่ยังค้าง (PrePayment) · 0 = ไม่มี/รับครบ</summary>
    public decimal OnlineDepositDue { get; set; }
    /// <summary>จ่ายออนไลน์ไม่ได้เพราะอะไร (null = จ่ายได้ หรือไม่มียอด)</summary>
    public string? OnlinePaymentNote { get; set; }
    /// <summary>คำอธิบายค่าธรรมเนียมถ้ายกเลิก "ตอนนี้" (ตัวตัดสิน BookingCancellationFee) · null = ยกเลิกไม่ได้แล้ว</summary>
    public string? CancelPolicyNote { get; set; }
    public bool CanCancelOnline { get; set; }
    /// <summary>ลูกค้าเลื่อนนัดเองได้ (ในหน้าต่างยกเลิกฟรี · รอบ 206)</summary>
    public bool CanRescheduleOnline { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? PaymentReference { get; set; }
    /// <summary>เตือนก่อนนัดส่งถึงจริงล่าสุดเมื่อไร (UTC) · null = ยังไม่ส่ง</summary>
    public DateTime? ReminderSentAt { get; set; }
    /// <summary>ร่องรอยส่งข้อความถึงลูกค้าล้มครั้งล่าสุด (ไม่มีช่องทาง/SMTP/LINE) · null = ไม่มีปัญหา</summary>
    public string? NotifyLastError { get; set; }
    /// <summary>ได้คิวจากคิวสำรอง + แจ้งลูกค้าถึงจริง (UTC)</summary>
    public DateTime? WaitlistPromotedAt { get; set; }
    public DateTime? WaitlistNotifiedAt { get; set; }
    /// <summary>ถือรอบให้ยืนยันถึง (UTC) · พ้นแล้ว job ปล่อยรอบ</summary>
    public DateTime? WaitlistHoldUntil { get; set; }

    // ── รอบ 207 ──
    /// <summary>ไซต์เปิดจัดสรรผู้ให้บริการ (ตัวตัดสินเดียว BookingProviderAllocation) — หน้าทำงานเปิดหน้าต่างเลือกผู้ให้บริการตอนเริ่มให้บริการ</summary>
    public bool ProviderAllocationEnabled { get; set; }
    /// <summary>ผู้ให้บริการที่ลงมือจริง + สัดส่วน % (ว่าง = ยังไม่มอบหมาย · การจองเก่าที่มีผู้ให้บริการคนเดียว ⇒ 1 แถว 100%)</summary>
    public List<BookingAssignmentDto> Assignments { get; set; } = new();
    /// <summary>เอกสารทุกใบของการจองนี้ (ใบยืนยัน · มัดจำ · ใบกำกับ/ใบเสร็จ · ใบลดหนี้ ...) — ค้นจากเลขการจองบนเอกสาร (server ประกอบ)</summary>
    public List<BookingDocumentLinkDto> Documents { get; set; } = new();
    /// <summary>ประวัติรับเงิน (บนการจอง + บนเอกสาร)</summary>
    public List<BookingPaymentLineDto> PaymentLines { get; set; } = new();
    /// <summary>ไทม์ไลน์ของการจอง (สร้าง · ยืนยัน · เริ่ม · เสร็จ · ยกเลิก)</summary>
    public List<BookingTimelineDto> Timeline { get; set; } = new();
    /// <summary>แผนตอนกด "เสร็จสิ้น" (server ตัดสิน — หน้าต่างชำระเงินแสดงตาม): เอกสารที่จะออก · ยอดค้าง · เก็บเงินได้ไหม</summary>
    public BookingCompletionPlanDto? CompletionPlan { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? ServiceProductName { get; set; }
    /// <summary>ป้ายสถานะที่ลูกค้าเห็น (BookingStatusLabels.Customer) — หน้าเว็บ/ใบยืนยันแสดงตรง</summary>
    public string CustomerStatusLabel { get; set; } = "";
}

public class BookingAssignmentDto
{
    public Guid ResourceId { get; set; }
    public string ResourceName { get; set; } = "";
    public decimal SharePercent { get; set; }
    public bool IsPrimary { get; set; }
}

public class BookingDocumentLinkDto
{
    public Guid Id { get; set; }
    public string DocumentNumber { get; set; } = "";
    public string DocumentType { get; set; } = "";
    /// <summary>ชื่อชนิดภาษาไทย (server · หัวที่พิมพ์จริงอยู่ที่หน้าเอกสาร)</summary>
    public string TypeLabel { get; set; } = "";
    public string Status { get; set; } = "";
    public string StatusLabel { get; set; } = "";
    public DateTime DocumentDate { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal BalanceDue { get; set; }
    public bool IsDeposit { get; set; }
    /// <summary>ใบที่ผูกกับการจองเป็นหลัก (ErpDocumentId)</summary>
    public bool IsPrimaryLink { get; set; }
}

public class BookingPaymentLineDto
{
    public DateTime PaidAt { get; set; }
    public decimal Amount { get; set; }
    public string Source { get; set; } = "";
    public string? Reference { get; set; }
    public string? DocumentNumber { get; set; }
}

public class BookingTimelineDto
{
    public DateTime At { get; set; }
    public string Label { get; set; } = "";
}

/// <summary>รอบ 207: ผู้ให้บริการที่เลือกได้ตอนเริ่มให้บริการ — เรียงตามคำแนะนำ (ตัวจัดอันดับเดียว BookingSlotEngine.RankResources)</summary>
public class BookingProviderCandidateDto
{
    public Guid ResourceId { get; set; }
    public string Name { get; set; } = "";
    public bool OnDuty { get; set; }
    public bool Available { get; set; }
    /// <summary>เหตุที่ไม่ว่าง (ไม่เข้างาน/มีงานชนเวลา) · null = ว่าง</summary>
    public string? UnavailableReason { get; set; }
    public bool Recommended { get; set; }
    public string Reason { get; set; } = "";
    public bool IsPrimaryForService { get; set; }
    public bool CurrentlyAssigned { get; set; }
}

public class BookingProviderCandidatesResponse
{
    public bool ProviderAllocationEnabled { get; set; }
    /// <summary>ต้องเลือกอย่างน้อย 1 คนก่อนเริ่ม (ไซต์เปิด + มีคนรับบริการนี้)</summary>
    public bool RequiresProvider { get; set; }
    public string StrategyLabel { get; set; } = "";
    public string? Note { get; set; }
    public List<BookingProviderCandidateDto> Candidates { get; set; } = new();
}

public class BookingAssignmentRequestItem
{
    public Guid ResourceId { get; set; }
    /// <summary>null ทุกคน ⇒ แบ่งเท่ากัน</summary>
    public decimal? SharePercent { get; set; }
}

/// <summary>รอบ 207: เริ่มให้บริการ + ผู้ให้บริการ (ได้หลายคน) · หรือแก้สัดส่วนภายหลัง</summary>
public class StartBookingServiceRequest
{
    public List<BookingAssignmentRequestItem> Assignments { get; set; } = new();
    public string? Note { get; set; }
}

/// <summary>รอบ 207: ผูก/สร้างสินค้า/บริการในระบบให้บริการจอง · <see cref="CreateNew"/> = สร้างสินค้าประเภทบริการจากชื่อ/ราคาของบริการนี้ ·
/// ไม่ส่งทั้งคู่ = เลิกผูก</summary>
public class LinkBookingProductRequest
{
    public Guid? ProductId { get; set; }
    public bool CreateNew { get; set; }
}

/// <summary>รอบ 207: สรุปการทำงาน/รายงานผู้ให้บริการ (ตัวประกอบเดียว Helpers/BookingProviderReport)</summary>
public class BookingProviderReportResponse
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public bool ProviderAllocationEnabled { get; set; }
    public ProviderReportTotals Totals { get; set; } = new(0, 0, 0, 0, 0m);
    public IReadOnlyList<ProviderSummary> Providers { get; set; } = Array.Empty<ProviderSummary>();
}

/// <summary>รอบ 207: แผนตอนเสร็จสิ้น (server ประกอบจาก BookingInvoiceTimingPolicy + ยอดค้าง)</summary>
public class BookingCompletionPlanDto
{
    /// <summary>เอกสารที่จะออกตอนกดเสร็จ + รับชำระ (ป้ายไทย) · null = ไม่ออก</summary>
    public string? DocumentLabel { get; set; }
    /// <summary>เอกสารที่จะออกถ้ากดเสร็จโดยไม่รับชำระ (ตามค่าตั้ง CompletionDocument ของเว็บ)</summary>
    public string? DocumentLabelNoPayment { get; set; }
    public string? Explanation { get; set; }
    /// <summary>ยอดที่ยังต้องเก็บ (ราคาเต็ม − ที่รับแล้ว) — ค่าเริ่มต้นของช่องยอดในหน้าต่างชำระเงิน</summary>
    public decimal AmountDue { get; set; }
    /// <summary>บันทึกรับเงินตอนเสร็จได้ไหม (ไม่ได้ = บิล POS / จ่ายด้วยบัตรคอร์ส / ไม่มีเอกสารให้ลงเงิน)</summary>
    public bool CanCollectPayment { get; set; }
    public string? CollectBlockedNote { get; set; }
    /// <summary>วิธีรับเงินที่เลือกได้ (ชื่อ enum + ป้ายไทย) — โอน/พร้อมเพย์ต้องเลือกบัญชีธนาคาร (LodgingPaymentBank)</summary>
    public List<BookingFormFields.FieldOption> PaymentMethods { get; set; } = new();
}

/// <summary>รอบ 207: กด "เสร็จสิ้น" + รับชำระ (เลือกวิธี/บัญชีรับเงิน · ระบุยอด) · <see cref="CollectPayment"/> false = เสร็จโดยยังไม่รับเงิน</summary>
public class CompleteBookingRequest
{
    public bool CollectPayment { get; set; }
    public decimal? Amount { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.Cash;
    public Guid? BankAccountId { get; set; }
    public DateTime? PaidAt { get; set; }
    public string? Reference { get; set; }
    public string? InternalNotes { get; set; }
}

/// <summary>"รับเงินแล้ว — ออกใบกำกับภาษี" (คำตัดสินข้อ 155) · <see cref="ReceivedDate"/> = วันที่รับเงินจริง (ว่าง = วันนี้ · ห้ามอนาคต) ใช้เป็นวันที่ใบกำกับ</summary>
public record IssueBookingTaxInvoiceRequest(DateTime? ReceivedDate);

public class BookingListResponse
{
    public Guid Id { get; set; }
    /// <summary>R-45 มุมมองทุกไซต์ — เว็บเจ้าของแถว (ปุ่ม action ต้องยิงไปเว็บนี้)</summary>
    public Guid SiteId { get; set; }
    /// <summary>ชื่อเว็บเจ้าของแถว — ใส่เฉพาะมุมมองทุกไซต์</summary>
    public string? SiteName { get; set; }
    public string BookingNumber { get; set; } = "";
    public BookingStatus Status { get; set; }
    public string ServiceName { get; set; } = "";
    public string? CustomerName { get; set; }
    public DateTime BookingDate { get; set; }
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public int GuestCount { get; set; } = 1;
    public string? ResourceName { get; set; }
    public int? WaitlistPosition { get; set; }
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "THB";
    public DateTime CreatedAt { get; set; }
    // ── รอบ 207: หน้าทำงานประจำวัน ──
    public decimal PaidAmount { get; set; }
    public string? GuestPhone { get; set; }
    /// <summary>ผู้ให้บริการทุกคน (หลายคน = "ก 60% · ข 40%")</summary>
    public string? ProvidersLabel { get; set; }
}

// ==================== Available Slots Query ====================

public class AvailableSlotsRequest
{
    [Required]
    public Guid BookingServiceId { get; set; }
    [Required]
    public DateTime Date { get; set; }
    public int GuestCount { get; set; } = 1;
    /// <summary>จำนวนช่วงติดกันที่ต้องการ (โหมดทุก N นาที) · ค่าเดิม 1</summary>
    public int Units { get; set; } = 1;
    /// <summary>ผู้ให้บริการที่ลูกค้าเลือก (ถ้ามี)</summary>
    public Guid? ResourceId { get; set; }
}

public class AvailableSlotResponse
{
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public int AvailableCapacity { get; set; }
    /// <summary>ป้ายรอบ (ช่วงเช้า/ช่วงบ่าย/ทั้งวัน) · null = แสดงเวลา</summary>
    public string? Label { get; set; }
    /// <summary>ยอดของรอบนี้ตามจำนวนช่วง (server คำนวณ)</summary>
    public decimal Price { get; set; }
    /// <summary>รอบเต็ม — จองได้เฉพาะเข้าคิวสำรอง</summary>
    public bool IsFull { get; set; }
    /// <summary>ผู้ให้บริการที่ระบบจะจัดให้ (แสดงเพื่อประกอบ — server ตัดสินอีกครั้งตอนจอง) · บริการที่ใช้ตารางคลาส = ครูของรอบ (รอบ 209 P4)</summary>
    public string? ResourceName { get; set; }
    /// <summary>รอบ 209 P4: รอบเรียน (SiteClassSession) ของเวลานี้ · null = บริการไม่ใช้ตารางคลาส</summary>
    public Guid? ClassSessionId { get; set; }
    /// <summary>รอบ 210: บริการนี้ต้องเข้าสู่ระบบสมาชิกก่อนจอง (server ตัดสิน · ด่านจริงตอนสร้างการจอง)</summary>
    public bool MemberOnly { get; set; }
}

/// <summary>พนักงานบันทึกรับเงินมัดจำของการจอง (รอบ 205 W2 R-08) — ยอด/วันที่/อ้างอิง · ตัวตัดสิน Helpers/CmsBookingOnlinePayment</summary>
public record RecordBookingPaymentRequest(decimal Amount, DateTime? PaidAt = null, string? Reference = null, Guid? BankAccountId = null, string? Note = null);

/// <summary>ลูกค้ายกเลิกการจองเองผ่านลิงก์โทเคน (รอบ 205 W2 R-10)</summary>
public record CustomerCancelBookingRequest(string? Reason = null);

/// <summary>เลื่อนนัด (พนักงาน หรือลูกค้าผ่านโทเคน · รอบ 206) — วันใหม่ + เวลาเริ่ม "HH:mm" จากรายการเวลาว่าง</summary>
public record RescheduleBookingRequest(DateTime BookingDate, string StartTime, string? Reason = null);

/// <summary>รอบ 215 เฟส 2: บริการที่ตั้งโหมดผู้ให้บริการเอง (ไม่ตามค่าเว็บ) — แสดงบนหน้าตั้งค่า › การจอง/บริการ</summary>
public class BookingServiceModeOverride
{
    public Guid ServiceId { get; set; }
    public string Name { get; set; } = "";
    public BookingResourceMode ResourceMode { get; set; }
}

/// <summary>รอบ 215 เฟส 2: body ของ "ใช้ตามค่าเว็บ" — null = ทุกบริการที่กำหนดเอง</summary>
public class InheritResourceModeRequest
{
    public List<Guid>? ServiceIds { get; set; }
}
