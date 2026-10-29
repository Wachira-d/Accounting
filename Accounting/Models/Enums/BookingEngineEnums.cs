namespace Accounting.Models.Enums;

/// <summary>รอบ 209 · P5 (R-209-C6 · คำตัดสินข้อ 168 ฏ) — กติกาค่าตอบแทนผู้สอน/ผู้ให้บริการ ต่อ <c>SiteBookingResource</c> ·
/// ตัวคิดเดียว <c>Helpers/InstructorPayRule</c> · None = ไม่คิด (เดิม)</summary>
public enum InstructorPayMode
{
    None = 0,
    /// <summary>ต่อคลาสที่สอนจริง (รอบที่มีผู้เรียนมา/ไม่มา ≥ 1 — ยกเลิกทั้งรอบไม่นับ)</summary>
    PerClass = 1,
    /// <summary>ต่อหัวผู้เรียนที่มา (การจองเสร็จสิ้น × จำนวนคน) · มีขั้นต่ำต่อคลาสได้</summary>
    PerHead = 2,
    /// <summary>% ของรายได้การจองที่เสร็จ (ก่อน VAT · บัตรคอร์ส = รายได้ที่รับรู้จากบัตร)</summary>
    PercentOfRevenue = 3,
}

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

/// <summary>ตรรกะ "แนะนำ/จัดผู้ให้บริการ" ของไซต์ (รอบ 207 · ตัวตัดสินเดียว <c>Helpers/BookingSlotEngine.RankResources</c>) ·
/// ทุกโหมดเลือกเฉพาะคนที่ <b>รับบริการนี้ได้ + เข้างาน + ว่าง</b> ก่อนเสมอ แล้วจึงเรียงตามโหมด · ค่า 0 = พฤติกรรมเดิม</summary>
public enum BookingRecommendStrategy
{
    /// <summary>งานน้อยสุดของวันนั้น (นาที) → ลำดับที่ตั้ง — พฤติกรรมเดิม</summary>
    LeastLoadedDay = 0,
    /// <summary>กระจายเท่าเทียมสะสมทั้งเดือน (นาทีงานเดือนนี้ + วันนี้) → ลำดับที่ตั้ง</summary>
    FairShareMonth = 1,
    /// <summary>หมุนเวียน — คนที่ได้งานล่าสุดนานที่สุดได้ก่อน</summary>
    RoundRobin = 2,
    /// <summary>คนหลักของบริการได้ก่อนถ้าว่าง (ติ๊ก "หลัก" ที่ผู้ให้บริการ) → ลำดับความสำคัญ → งานน้อยสุดของวัน</summary>
    PrimaryFirst = 3,
    /// <summary>ตามลำดับความสำคัญที่ตั้งต่อบริการ (เลขน้อย = ก่อน) → งานน้อยสุดของวัน</summary>
    PriorityOrder = 4,
}

/// <summary>ช่องข้อมูลในฟอร์มจองของลูกค้า (รอบ 207 · ตัวตัดสินเดียว <c>Helpers/BookingFormFields</c>)</summary>
public enum BookingFieldRequirement
{
    Optional = 0,
    Required = 1,
    Hidden = 2,
}

/// <summary>เอกสารที่ออกตอน "ให้บริการเสร็จ" ด้วยปุ่มสถานะ (ไม่รับชำระ) ของบริการแบบนัดหมาย (รอบ 207 · ตัวตัดสินเดียว <c>Helpers/BookingInvoiceTimingPolicy</c>) ·
/// <b>0 = ไม่ออก = พฤติกรรมเดิม</b> (ฝ่ายค้านรอบ 207 ข้อ 2: ค่าเริ่มต้นเปลี่ยนพฤติกรรมไซต์เดิม — เช่น เสร็จแล้วไปเก็บเงินที่ POS) ·
/// "เสร็จสิ้น + รับชำระ" (พนักงานเลือกรับเงินเอง) ออกเอกสารเสมอไม่ขึ้นกับค่านี้ · การันตี/ชำระล่วงหน้าเดินเส้นเดิมของตัวเอง</summary>
public enum BookingCompletionDocument
{
    /// <summary>ไม่ออกเอกสารตอนกดเสร็จโดยไม่รับชำระ (ออกเองที่หน้าเอกสาร/POS) — พฤติกรรมก่อนรอบ 207</summary>
    None = 0,
    /// <summary>ออกให้อัตโนมัติตามสถานะ VAT: จด VAT ⇒ ใบกำกับภาษี · ไม่จด VAT ⇒ ใบแจ้งหนี้ (รับเงินครบตอนเสร็จ = ใบเสร็จรับเงิน)</summary>
    Auto = 1,
}
