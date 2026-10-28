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
    public BookingResourceMode ResourceMode { get; set; } = BookingResourceMode.None;
    /// <summary>true = ระยะเวลา/buffer/ล่วงหน้า ใช้ค่าตั้งของไซต์ · false = ค่าของบริการนี้ (override)</summary>
    public bool UseSiteDefaults { get; set; }
    /// <summary>เวลาทำการ + วันหยุด — <b>null = ไม่แตะตารางเวลาเดิม</b> (ผู้เรียก API เดิม) · ส่งมา = แทนที่ทั้งชุด</summary>
    public BookingScheduleDto? Schedule { get; set; }
}

/// <summary>ค่าตั้งการจองระดับไซต์ (บริการที่ "ใช้ค่าของไซต์" รับไป)</summary>
public class BookingSiteSettingsDto
{
    public int DurationMinutes { get; set; } = 60;
    public int BufferBeforeMinutes { get; set; }
    public int BufferAfterMinutes { get; set; }
    public int MinAdvanceHours { get; set; } = 1;
    public int MaxAdvanceDays { get; set; } = 30;
    /// <summary>จังหวะออกใบกำกับภาษีของบริการแบบ Guaranteed (คำตัดสินข้อ 155) · คำตอบมีค่าเสมอ · ตอนบันทึก null = คงค่าเดิม (ไม่รีเซ็ตเงียบ)</summary>
    public BookingInvoiceTiming? GuaranteedInvoiceTiming { get; set; }
    /// <summary>ตัวเลือก + ผลทางภาษี (server ประกอบจาก <c>BookingInvoiceTimingPolicy.Options</c> · หน้าเว็บแสดงตรง ๆ) — ไม่อ่านตอนบันทึก</summary>
    public List<BookingInvoiceTimingOption> GuaranteedInvoiceTimingOptions { get; set; } = new();
}

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
    public BookingResourceMode ResourceMode { get; set; }
    public bool UseSiteDefaults { get; set; }
    /// <summary>ระยะเวลาที่ใช้จริง (ของไซต์เมื่อ UseSiteDefaults) — หน้าเว็บแสดงตัวนี้</summary>
    public int EffectiveDurationMinutes { get; set; }
    /// <summary>ผู้ให้บริการที่ทำบริการนี้ได้ (เปิดใช้อยู่) — ลูกค้าเลือกเมื่อโหมด "บังคับระบุ"/"ลูกค้าเลือก"</summary>
    public List<BookingResourceBrief> Resources { get; set; } = new();
    public BookingScheduleDto Schedule { get; set; } = new();
    /// <summary>มีเวลาทำการอย่างน้อย 1 ช่วง — false = ลูกค้าจะไม่เห็นเวลาว่างเลย (หน้า CMS เตือน)</summary>
    public bool HasOpeningHours { get; set; }
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
    public int CurrentBookings { get; set; }
    public int AvailableSlots { get; set; }
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
}

public class UpdateBookingStatusRequest
{
    [Required]
    public BookingStatus Status { get; set; }
    public string? InternalNotes { get; set; }
    public string? CancellationReason { get; set; }
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
    /// <summary>แสดงปุ่ม "รับเงินแล้ว — ออกใบกำกับภาษี" ไหม (server ตัดสินด้วย <c>BookingInvoiceTimingPolicy.Decide(PaymentReceived)</c> · คำตัดสินข้อ 155)</summary>
    public bool CanIssueTaxInvoiceNow { get; set; }
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
    /// <summary>ผู้ให้บริการที่ระบบจะจัดให้ (แสดงเพื่อประกอบ — server ตัดสินอีกครั้งตอนจอง)</summary>
    public string? ResourceName { get; set; }
}
