using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Cms;

// ==================== รอบ 209 · P4 — ตารางคลาส (เทอม → รอบเรียน → การจอง) · ตัวตัดสิน Helpers/ClassScheduleGenerator ====================

/// <summary>สร้าง/แก้เทอมคลาส — server ตรวจด้วย <c>ClassScheduleGenerator.ValidateTerm</c> (ผิด = ปฏิเสธพร้อมข้อความไทย)</summary>
public class SaveClassTermRequest
{
    [Required]
    public Guid BookingServiceId { get; set; }
    [MaxLength(200)]
    public string? Title { get; set; }
    /// <summary>เลขวันในสัปดาห์ 0 = อาทิตย์ … 6 = เสาร์</summary>
    public List<int> Weekdays { get; set; } = new();
    /// <summary>"HH:mm"</summary>
    [Required]
    public string StartTime { get; set; } = "18:00";
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public Guid? InstructorResourceId { get; set; }
    public int? CapacityOverride { get; set; }
    public bool SkipHolidays { get; set; } = true;
    public ClassTermStatus Status { get; set; } = ClassTermStatus.Active;
    public string? Notes { get; set; }
}

public class ClassTermResponse
{
    public Guid Id { get; set; }
    public Guid BookingServiceId { get; set; }
    public string ServiceName { get; set; } = "";
    public string? Title { get; set; }
    public List<int> Weekdays { get; set; } = new();
    public string WeekdaysLabel { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string StartDate { get; set; } = "";
    public string? EndDate { get; set; }
    public Guid? InstructorResourceId { get; set; }
    public string? InstructorName { get; set; }
    public int? CapacityOverride { get; set; }
    /// <summary>ที่นั่งต่อรอบที่ใช้จริง (ที่ตั้งบนเทอม หรือความจุของบริการ)</summary>
    public int EffectiveCapacity { get; set; }
    public bool SkipHolidays { get; set; }
    public ClassTermStatus Status { get; set; }
    public string StatusLabel { get; set; } = "";
    public string? Notes { get; set; }
    /// <summary>รอบที่ยังไม่ถึง (ไม่รวมที่ยกเลิก)</summary>
    public int UpcomingSessions { get; set; }
    public string? LastSessionDate { get; set; }
}

/// <summary>สร้างรอบ N สัปดาห์จากเทอม (สร้างซ้ำได้ — ไม่ซ้ำ ไม่ลบรอบที่มีการจอง)</summary>
public class GenerateClassSessionsRequest
{
    /// <summary>เริ่มจากวันที่ (null = วันนี้) · ไม่ก่อนวันเริ่มเทอม/วันนี้</summary>
    public DateTime? FromDate { get; set; }
    public int Weeks { get; set; } = 4;
}

public class ClassSkippedDateDto
{
    public string Date { get; set; } = "";
    public string Reason { get; set; } = "";
}

public class ClassGenerateResponse
{
    public int Created { get; set; }
    public int AlreadyExisting { get; set; }
    public int Removed { get; set; }
    public int KeptWithBookings { get; set; }
    public List<ClassSkippedDateDto> Skipped { get; set; } = new();
    public List<string> Notices { get; set; } = new();
}

/// <summary>รอบเรียนหนึ่งรอบ (ปฏิทินสัปดาห์) — ที่นั่ง/คิวสำรองนับด้วยกติกาเดียวกับ engine (<c>ClassScheduleGenerator.Fill</c>)</summary>
public class ClassSessionResponse
{
    public Guid Id { get; set; }
    public Guid? TermId { get; set; }
    public Guid BookingServiceId { get; set; }
    public string ServiceName { get; set; } = "";
    public string Date { get; set; } = "";
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public ClassSessionStatus Status { get; set; }
    public string StatusLabel { get; set; } = "";
    public string? CancelReason { get; set; }
    public Guid? InstructorResourceId { get; set; }
    public string? InstructorName { get; set; }
    public Guid? SubstituteResourceId { get; set; }
    public string? SubstituteName { get; set; }
    public int? CapacityOverride { get; set; }
    public int Capacity { get; set; }
    public int Taken { get; set; }
    public int Waitlisted { get; set; }
    public string? Notes { get; set; }
    /// <summary>ปุ่มยกเลิกทั้งคลาส (server ตัดสิน <c>ClassScheduleGenerator.CancelProblem</c>) · เหตุที่กดไม่ได้</summary>
    public bool CanCancel { get; set; }
    public string? CancelBlockedReason { get; set; }
}

public class ClassRosterEntryDto
{
    public Guid BookingId { get; set; }
    public string BookingNumber { get; set; } = "";
    public BookingStatus Status { get; set; }
    public string StatusLabel { get; set; } = "";
    public string? CustomerName { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public int GuestCount { get; set; }
    /// <summary>จองด้วยบัตรคอร์ส (สมุดเครดิต ExternalRef ของใบนี้) · null = ชำระปกติ/drop-in</summary>
    public string? CreditPassNumber { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public int? WaitlistPosition { get; set; }
    /// <summary>ปุ่มเช็คอิน (server ตัดสิน — สถานะ + เวลา) · เหตุที่กดไม่ได้</summary>
    public bool CanMarkAttended { get; set; }
    public bool CanMarkNoShow { get; set; }
    public string? CheckInBlockedReason { get; set; }
}

public class ClassInstructorOptionDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public class ClassSessionDetailResponse : ClassSessionResponse
{
    public List<ClassRosterEntryDto> Roster { get; set; } = new();
    /// <summary>ผู้ให้บริการที่รับบริการนี้ (ตัวเลือกครูแทน)</summary>
    public List<ClassInstructorOptionDto> Instructors { get; set; } = new();
}

/// <summary>ผลของการกระทำต่อรอบ (ครูแทน · ยกเลิก · เปิดอีกครั้ง · เช็คอิน) + ข้อความถึงผู้กด (เครดิตคืน · แจ้งลูกค้าสำเร็จ/ล้ม · ใบที่ทำไม่ได้)</summary>
public class ClassActionResponse
{
    public ClassSessionDetailResponse Session { get; set; } = new();
    public List<string> Notices { get; set; } = new();
}

public class ClassSubstituteRequest
{
    /// <summary>null = ล้างครูแทน (กลับเป็นครูประจำ)</summary>
    public Guid? ResourceId { get; set; }
    public bool Notify { get; set; } = true;
}

public class ClassCancelRequest
{
    [MaxLength(500)]
    public string? Reason { get; set; }
    public bool Notify { get; set; } = true;
}

public class ClassCheckInRequest
{
    [Required]
    public Guid BookingId { get; set; }
    /// <summary>true = มาเรียน (สถานะเสร็จสิ้น) · false = ไม่มา</summary>
    public bool Attended { get; set; }
}

public class UpdateClassSessionRequest
{
    /// <summary>ที่นั่งของรอบนี้ · null = ความจุของบริการ</summary>
    public int? CapacityOverride { get; set; }
    public string? Notes { get; set; }
}
