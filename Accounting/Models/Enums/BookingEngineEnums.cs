namespace Accounting.Models.Enums;

/// <summary>ความละเอียดของ "รอบที่เปิดให้จอง" ต่อบริการ (CMS booking) — ตัวตัดสินเดียวคือ <c>Helpers/BookingSlotEngine</c>
/// <para><b>ค่า 0 = พฤติกรรมเดิม</b> (ก่อนมี engine): แต่ละแถวเวลาทำการ = 1 รอบ · ข้อมูลเก่าได้ค่านี้จาก DEFAULT ของคอลัมน์</para></summary>
public enum BookingSlotMode
{
    /// <summary>ช่วงเวลาที่กำหนดเอง — แต่ละช่วงในตารางเวลา = 1 รอบ (เช่น 09:00–10:30 · 13:00–14:30) · พฤติกรรมเดิม</summary>
    Windows = 0,
    /// <summary>ตัดเป็นรอบทุก N นาทีภายในเวลาทำการ (15/30/60/กำหนดเอง) · ระยะเวลาบริการ = DurationMinutes · จองหลายช่วงติดกันได้ (MaxUnitsPerBooking)</summary>
    Interval = 1,
    /// <summary>ครึ่งวัน — ช่วงเช้า (เปิด → เวลาแบ่ง) / ช่วงบ่าย (เวลาแบ่ง → ปิด)</summary>
    HalfDay = 2,
    /// <summary>รายวัน — ทั้งช่วงเวลาทำการของวันเป็น 1 รอบ</summary>
    FullDay = 3,
}

/// <summary>"ความจุต่อรอบ" นับเป็นอะไร</summary>
public enum BookingCapacityUnit
{
    /// <summary>นับเป็นจำนวนการจอง (คิว/ห้อง/คัน) — 1 การจองใช้ 1 ที่ไม่ว่ามากี่คน · พฤติกรรมเดิม</summary>
    Bookings = 0,
    /// <summary>นับเป็นจำนวนคน (ที่นั่งในคลาส/โต๊ะรวม) — 1 การจองใช้ที่เท่าจำนวนคนที่มา</summary>
    Guests = 1,
}

/// <summary>การจัดสรรผู้ให้บริการ/ห้อง/อุปกรณ์ต่อบริการ (ตัวตัดสิน <c>Helpers/BookingSlotEngine.PickResource</c>)</summary>
public enum BookingResourceMode
{
    /// <summary>ไม่ใช้ผู้ให้บริการ — นับความจุของบริการ (พฤติกรรมเดิม)</summary>
    None = 0,
    /// <summary>บังคับระบุ — ลูกค้าต้องเลือกผู้ให้บริการก่อนจอง</summary>
    Required = 1,
    /// <summary>ลูกค้าเลือกเองได้ — ไม่เลือก = ระบบจัดให้</summary>
    CustomerChoice = 2,
    /// <summary>ระบบจัดสรรอัตโนมัติ — เลือกคนที่ว่าง + ทำบริการนี้ได้ + งานน้อยสุด (ไม่รับค่าจากลูกค้า)</summary>
    Auto = 3,
}

/// <summary>ชนิดทรัพยากรที่จองได้</summary>
public enum BookingResourceKind
{
    Staff = 0,
    Room = 1,
    Equipment = 2,
}
