using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ===== รอบ 209 · P4 — ตารางคลาสของสตูดิโอ บนเครื่องจอง CMS ตัวเดิม (คำตัดสินข้อ 168 ฌ · ไม่สร้างเครื่องจองที่สอง) =====
// rule (เทอม) → occurrence (รอบเรียนจริง) → การจอง (SiteBooking — เป็น enrollment อยู่แล้ว ไม่สร้างตารางลงทะเบียนแยก)
// ตาราง + index ต้องตรงกับ Helpers/ClassScheduleMigration · ตัวตัดสิน/สร้างรอบ = Helpers/ClassScheduleGenerator (pure)

/// <summary>เทอมคลาส — "จ/พ/ศ 18:00 ตั้งแต่ 1 พ.ย. ถึง 31 ม.ค." ของบริการหนึ่งตัว · กด "สร้างรอบ N สัปดาห์" ⇒ <see cref="SiteClassSession"/> ·
/// บริการที่มีเทอม (ที่ยังไม่ลบ) อย่างน้อย 1 ตัว ⇒ รอบที่จองได้มาจาก <see cref="SiteClassSession"/> เท่านั้น (แถวเวลาทำการรายสัปดาห์ของบริการไม่ใช้)</summary>
public class SiteClassTerm : TenantEntity
{
    public Guid SiteId { get; set; }
    public Guid BookingServiceId { get; set; }
    /// <summary>ชื่อเทอม (เช่น "เทอมหนาว 2569") · ว่าง = ใช้ชื่อบริการ</summary>
    public string? Title { get; set; }
    /// <summary>วันในสัปดาห์ CSV ของเลข <see cref="DayOfWeek"/> (0 = อาทิตย์) เช่น "1,3,5" — ตัวแปลง <c>ClassScheduleGenerator.ParseWeekdays</c></summary>
    public string Weekdays { get; set; } = "";
    /// <summary>เวลาเริ่มคลาส · เวลาจบ = เวลาเริ่ม + ระยะเวลาของบริการ (ตรงกับที่ engine บันทึกบนการจอง)</summary>
    public TimeOnly StartTime { get; set; }
    public DateOnly StartDate { get; set; }
    /// <summary>วันสิ้นสุดเทอม · null = ไม่มีกำหนด (สร้างได้ทีละ N สัปดาห์)</summary>
    public DateOnly? EndDate { get; set; }
    /// <summary>ครูประจำ (SiteBookingResource ที่รับบริการนี้) · null = ระบบจัดผู้ให้บริการตามปกติ</summary>
    public Guid? InstructorResourceId { get; set; }
    /// <summary>ที่นั่งต่อรอบของเทอมนี้ · null = ความจุของบริการ</summary>
    public int? CapacityOverride { get; set; }
    /// <summary>ข้ามวันหยุดบริษัท (<c>PublicHoliday</c>) ตอนสร้างรอบ</summary>
    public bool SkipHolidays { get; set; } = true;
    public ClassTermStatus Status { get; set; } = ClassTermStatus.Active;
    public string? Notes { get; set; }
}

/// <summary>รอบเรียนจริงหนึ่งรอบ — คีย์ = บริการ + วัน + เวลาเริ่ม (คีย์เดียวกับที่ <c>BookingSlotEngine</c> นับ "รอบเดียวกัน" ของคลาสกลุ่ม) ·
/// การจองของรอบนี้ = <c>SiteBooking</c> ที่บริการ/วัน/เวลาเริ่มตรงกัน (ไม่มี FK — ใบเก่าค้นด้วยคีย์เดียวกัน) · ครูแทน/ยกเลิก/หมายเหตุ/ที่นั่ง แปะที่นี่</summary>
public class SiteClassSession : TenantEntity
{
    public Guid SiteId { get; set; }
    public Guid BookingServiceId { get; set; }
    /// <summary>เทอมที่สร้างรอบนี้ · null = รอบที่ไม่ได้มาจากเทอม</summary>
    public Guid? TermId { get; set; }
    public DateOnly SessionDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    /// <summary>ครูประจำรอบ (คัดลอกจากเทอมตอนสร้าง) · null = ระบบจัดผู้ให้บริการตามปกติ</summary>
    public Guid? InstructorResourceId { get; set; }
    /// <summary>ครูแทน — ชนะครูประจำ · การจองทุกใบของรอบตามครูคนนี้ (<c>ClassSessionFact.EffectiveInstructorId</c>)</summary>
    public Guid? SubstituteResourceId { get; set; }
    /// <summary>ที่นั่งของรอบนี้ · null = ความจุของบริการ</summary>
    public int? CapacityOverride { get; set; }
    public ClassSessionStatus Status { get; set; } = ClassSessionStatus.Scheduled;
    public string? CancelReason { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public string? Notes { get; set; }
}
