using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ===== Booking Services =====

public class SiteBookingService : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string Slug { get; set; } = "";

    // Duration & capacity
    public int DurationMinutes { get; set; } = 60;
    public int BufferMinutes { get; set; } = 0;
    public int MaxCapacity { get; set; } = 1;
    public int MaxAdvanceBookingDays { get; set; } = 30;
    public int MinAdvanceBookingHours { get; set; } = 1;

    // ── รอบที่เปิดให้จอง (ตัวตัดสินเดียว Helpers/BookingSlotEngine · ค่าเริ่มต้นของคอลัมน์ = พฤติกรรมเดิม) ──
    /// <summary>ความละเอียดของรอบ: ช่วงกำหนดเอง (เดิม) · ทุก N นาที · ครึ่งวัน · รายวัน</summary>
    public BookingSlotMode SlotMode { get; set; } = BookingSlotMode.Windows;
    /// <summary>ระยะห่างของเวลาเริ่มแต่ละรอบ (โหมดทุก N นาที) · null = เท่ากับระยะเวลาบริการ</summary>
    public int? SlotIntervalMinutes { get; set; }
    /// <summary>เวลาเตรียมก่อนเริ่ม (นาที) · <see cref="BufferMinutes"/> = เวลาเก็บ/ทำความสะอาดหลังจบ</summary>
    public int BufferBeforeMinutes { get; set; }
    /// <summary><see cref="MaxCapacity"/> นับเป็นคิว (การจอง) หรือจำนวนคน</summary>
    public BookingCapacityUnit CapacityUnit { get; set; } = BookingCapacityUnit.Bookings;
    /// <summary>จองหลายช่วงติดกันได้สูงสุดกี่ช่วง (สนาม/ห้องประชุมรายชั่วโมง) — โหมดทุก N นาทีเท่านั้น</summary>
    public int MaxUnitsPerBooking { get; set; } = 1;
    /// <summary>เวลาแบ่งเช้า/บ่าย "HH:mm" (โหมดครึ่งวัน) · null = 12:00</summary>
    public string? HalfDaySplitTime { get; set; }
    /// <summary>ทรัพยากรร่วม — บริการที่ตั้งชื่อกลุ่มเดียวกัน (เช่น "ห้องประชุม A" แบบครึ่งวัน+รายวัน · "หมอนวด") ใช้ความจุร่วมกัน ·
    /// ว่าง = บริการนี้มีทรัพยากรของตัวเอง</summary>
    public string? CapacityGroup { get; set; }
    /// <summary>สวิตช์ "คุม Slot": true = บล็อกเมื่อเต็ม (ความจุ/ผู้ให้บริการ) · false = ยอมรับจองซ้อนได้ถึง <see cref="OverlapLimit"/></summary>
    public bool EnforceSlotCapacity { get; set; } = true;
    /// <summary>เพดานจองซ้อนต่อรอบเมื่อปิดสวิตช์คุม Slot · null = ไม่จำกัด</summary>
    public int? OverlapLimit { get; set; }
    /// <summary>รอบเต็มแล้วรับเข้าคิวสำรอง (Waiting List) · เลื่อนขึ้นอัตโนมัติเมื่อมีคนยกเลิก</summary>
    public bool WaitlistEnabled { get; set; }
    /// <summary>จัดสรรผู้ให้บริการ/ห้อง/อุปกรณ์: ไม่ใช้ · บังคับระบุ · ลูกค้าเลือก · ระบบจัดให้</summary>
    public BookingResourceMode ResourceMode { get; set; } = BookingResourceMode.None;
    /// <summary>true = ระยะเวลา/buffer/ล่วงหน้า ใช้ค่าตั้งของไซต์ (<see cref="SiteBookingSettings"/>) · false = ใช้ค่าของบริการนี้ (override · ข้อมูลเดิม)</summary>
    public bool UseSiteDefaults { get; set; }
    /// <summary>รอบ 210: ต้องเข้าสู่ระบบสมาชิกก่อนจองบริการนี้ไหม · null = ตามค่าของเว็บ (<see cref="SiteBookingSettings.RequireMemberToBook"/>) ·
    /// ตัวตัดสินเดียว <c>Helpers/BookingAccessRule.RequiresMember</c></summary>
    public bool? MemberOnly { get; set; }

    // Pricing
    public decimal Price { get; set; }
    public decimal? DepositAmount { get; set; }
    public decimal? DepositPercent { get; set; }
    public string Currency { get; set; } = "THB";

    // Booking type determines ERP document flow
    public BookingType BookingType { get; set; } = BookingType.Lead;

    // ERP mapping: linked product/service
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }

    // Categorization
    public string? Category { get; set; }
    // รอบ 209 P4 (C9 · ตัดสิน): Tags ถูกลบ — ไม่มีทางเขียนจาก API/หน้าจอ และไม่มีผู้อ่าน (จัดกลุ่มใช้ Category) · คอลัมน์ nullable เดิมคงอยู่ในฐานโดยไม่มีผล

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    // Auto-confirm or require manual approval
    public bool AutoConfirm { get; set; } = false;

    // Cancellation policy
    public int? FreeCancellationHours { get; set; }
    public decimal? CancellationFeePercent { get; set; }

    public ICollection<SiteBookingSlot> Slots { get; set; } = new List<SiteBookingSlot>();
    public ICollection<SiteBooking> Bookings { get; set; } = new List<SiteBooking>();
    public ICollection<SiteBookingServiceTranslation> Translations { get; set; } = new List<SiteBookingServiceTranslation>();
}

public class SiteBookingServiceTranslation : TenantEntity
{
    public Guid BookingServiceId { get; set; }
    public SiteBookingService BookingService { get; set; } = null!;

    public string LanguageCode { get; set; } = "th";
    public string? Name { get; set; }
    public string? Description { get; set; }
}

// ===== Time Slots =====

public class SiteBookingSlot : TenantEntity
{
    public Guid BookingServiceId { get; set; }
    public SiteBookingService BookingService { get; set; } = null!;

    // Recurring schedule (day-of-week based)
    public DayOfWeek? DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    // Or specific date override
    public DateOnly? SpecificDate { get; set; }
    public bool IsBlocked { get; set; } = false;

    public int MaxCapacity { get; set; } = 1;
    // รอบ 209 P4 (C9): CurrentBookings ถูกลบ (ไม่มีผู้เขียน — echo "ว่าง = ความจุเต็ม" เสมอ) · คอลัมน์เดิมได้ DEFAULT 0 จาก Helpers/ClassScheduleMigration

    public bool IsActive { get; set; } = true;
}

// ===== Bookings =====

public class SiteBooking : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public Guid BookingServiceId { get; set; }
    public SiteBookingService BookingService { get; set; } = null!;

    public Guid? CustomerId { get; set; }
    public SiteCustomer? Customer { get; set; }

    public string BookingNumber { get; set; } = "";
    public BookingStatus Status { get; set; } = BookingStatus.Pending;

    // Schedule
    public DateTime BookingDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int DurationMinutes { get; set; }

    // Guest info (for walk-in without account)
    public string? GuestName { get; set; }
    public string? GuestEmail { get; set; }
    public string? GuestPhone { get; set; }
    public int GuestCount { get; set; } = 1;
    /// <summary>จำนวนช่วงติดกันที่จอง (โหมดทุก N นาที · ยอด = ราคาต่อช่วง × ช่วง) · การจองเดิม = 1</summary>
    public int Units { get; set; } = 1;
    /// <summary>ผู้ให้บริการ/ห้อง/อุปกรณ์ที่ถูกจัดให้ (หรือที่ลูกค้าขอ — คิวสำรอง) · null = บริการไม่ใช้ผู้ให้บริการ</summary>
    public Guid? ResourceId { get; set; }

    // Amounts
    public decimal TotalAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public string Currency { get; set; } = "THB";

    // Payment
    public string? PaymentReference { get; set; }
    public DateTime? PaidAt { get; set; }
    /// <summary>โทเคนสาธารณะที่ลูกค้าถือ (ลิงก์จ่ายมัดจำ/จัดการการจอง) — ออกตอนจอง · ใบเก่า null = ไม่มีลิงก์ (รอบ 205 W2 R-08/R-10)</summary>
    public string? PublicToken { get; set; }

    // ERP Integration (event-driven document creation)
    public Guid? ErpDocumentId { get; set; }
    public Document? ErpDocument { get; set; }
    public ErpBookingDocumentType? ErpDocumentType { get; set; }

    public string? CustomerNotes { get; set; }
    public string? InternalNotes { get; set; }
    public string? CancellationReason { get; set; }

    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    /// <summary>รอบ 207: เวลาเริ่มให้บริการ (กด "เริ่มให้บริการ") · null = การจองก่อนรอบ 207/ยังไม่เริ่ม</summary>
    public DateTime? StartedAt { get; set; }

    // Reminders — ผู้เขียน: Services/Background/BookingReminderJob (รอบ 205 W3 R-12) · ประทับหลังส่งถึงจริงเท่านั้น (F2 #3)
    public bool ReminderSent { get; set; } = false;
    public DateTime? ReminderSentAt { get; set; }
    /// <summary>ครั้งล่าสุดที่พยายามส่งข้อความถึงลูกค้า (เตือน/ได้คิว) — ใช้เว้นระยะลองใหม่ (<c>BookingReminderPlan.MayAttempt</c>)</summary>
    public DateTime? NotifyAttemptedAt { get; set; }
    /// <summary>ร่องรอยส่งล้มครั้งล่าสุด (ไม่มีช่องทาง · SMTP/LINE ล้ม) · null = ครั้งล่าสุดสำเร็จ/ยังไม่เคยส่ง · พนักงานเห็นในหน้าการจอง</summary>
    public string? NotifyLastError { get; set; }

    // คิวสำรองได้คิว (รอบ 205 W3 R-13) — ผู้เขียน: CmsBookingService.PromoteWaitlistAsync / .Notify.cs / BookingReminderJob
    /// <summary>เวลาที่ถูกเลื่อนจากคิวสำรองขึ้นเป็นการจอง (UTC) · null = ไม่เคยอยู่คิวสำรอง/ยังอยู่</summary>
    public DateTime? WaitlistPromotedAt { get; set; }
    /// <summary>แจ้งลูกค้าว่าได้คิวแล้ว "ถึงจริง" เมื่อไร (UTC) · null = ยังไม่ถึง (job ลองใหม่)</summary>
    public DateTime? WaitlistNotifiedAt { get; set; }
    /// <summary>ถือรอบให้ยืนยันถึงเมื่อไร (UTC) · null = ไม่ถือ/ยืนยันแล้ว · พ้นแล้วยังรอยืนยัน ⇒ job ปล่อยรอบ</summary>
    public DateTime? WaitlistHoldUntil { get; set; }

    // ── ดึงคิวเข้า POS (ทีม K · 2026-10-22) ──
    /// <summary>บิล POS ที่เปิดจากการจองนี้ (ตัวจริงของ "เปิดแล้ว" คือ <c>PosOrder.SourceBookingId</c> + unique index — ช่องนี้แสดงผลฝั่งระบบจอง)</summary>
    public Guid? PosOrderId { get; set; }
    /// <summary>ช่าง/พนักงานที่ลูกค้าจองไว้ (UserId) — POS เติมให้ทุกบรรทัด · null = ไม่ระบุ</summary>
    public Guid? AssignedStaffId { get; set; }
    public string? AssignedStaffName { get; set; }

    public ICollection<SiteBookingPayment> Payments { get; set; } = new List<SiteBookingPayment>();
}

public class SiteBookingPayment : TenantEntity
{
    public Guid BookingId { get; set; }
    public SiteBooking Booking { get; set; } = null!;

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "THB";
    public PaymentMethod PaymentMethod { get; set; }
    public string? GatewayTransactionId { get; set; }
    public string? Reference { get; set; }
    public string? SlipUrl { get; set; }

    public SitePaymentStatus Status { get; set; } = SitePaymentStatus.Pending;
    public DateTime? PaidAt { get; set; }
    public string? FailureReason { get; set; }

    // Refund tracking
    public bool IsRefund { get; set; } = false;
    public string? RefundReason { get; set; }
}
