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

    public ICollection<SiteBookingResourceService> Services { get; set; } = new List<SiteBookingResourceService>();
    public ICollection<SiteBookingResourceSlot> Slots { get; set; } = new List<SiteBookingResourceSlot>();
}

/// <summary>"ประเภทบริการที่รับได้" — ผู้ให้บริการคนนี้ทำบริการนี้ได้</summary>
public class SiteBookingResourceService : TenantEntity
{
    public Guid ResourceId { get; set; }
    public SiteBookingResource Resource { get; set; } = null!;
    public Guid BookingServiceId { get; set; }
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
