using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ============================================================================
// โมดูลธุรกิจที่พัก (Lodging) — โรงแรม · รีสอร์ท · โฮสเทล · บ้านพัก
//
// ที่มา: สกัดโดเมนจากระบบ TakeTime (Ipsos-Dev-TH — รีสอร์ท Take Time BangPhra:
// Accommodation/Reservation/PricingSeasons/DynamicPricingRules/HousekeepingTasks/
// Reservation_Product_Charges/Guest portal) แล้วออกแบบใหม่ให้เป็น multi-tenant
// และต่อกับแกนบัญชีของระบบนี้โดยตรง:
//   • มัดจำ → Document(Receipt, IsDeposit) ⇒ Cr 217xx + tax point §78/1 ตอนรับเงิน
//   • เช็คเอาต์ → TaxInvoice ที่ตัดมัดจำ (DepositApplied*) + ค่าใช้จ่ายระหว่างพัก
//   • เลขเอกสารทุกใบผ่าน DocumentNumberGenerator (gap-free §86/4) — โมดูลนี้ไม่ออกเลขเอง
//
// ทำไมไม่ต่อยอด SiteBookingService/SiteBooking ที่มีอยู่: ตัวนั้นเป็น "นัดหมาย
// ตาม slot เวลา" (สปา/คลินิก/ร้านอาหาร — StartTime/EndTime/DurationMinutes ในวันเดียว)
// ส่วนที่พักคือ "ช่วงคืน" (check-in → check-out หลายคืน · จำนวนห้อง · ราคาต่อคืน
// ที่เปลี่ยนตามฤดูกาล/วัน/แผนราคา · มัดจำ+folio+เช็คเอาต์) คนละโดเมน — ยัดรวมกัน
// จะได้ตารางที่ครึ่งหนึ่งของคอลัมน์ไม่มีความหมายกับอีกครึ่ง
//
// ทุก entity สืบ TenantEntity (CompanyId) — query ทุกตัวต้องกรอง CompanyId (กฎ M)
// ============================================================================

/// <summary>ที่พัก 1 แห่ง (โรงแรม/รีสอร์ท/บ้านพัก) — ถือ "การตั้งค่า" ทั้งหมดของที่พักนั้น
/// ผูกกับเว็บไซต์ CMS (Site) ได้ 1:1 และผูกกับสาขา (Branch) ได้ถ้ามีหลายแห่ง</summary>
public class LodgingProperty : TenantEntity
{
    public Guid? SiteId { get; set; }
    public Site? Site { get; set; }
    public Guid? BranchId { get; set; }
    public Branch? Branch { get; set; }

    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Code { get; set; } = "";              // รหัสสั้น ใช้ในเลขจอง
    public LodgingPropertyType PropertyType { get; set; } = LodgingPropertyType.Hotel;
    public string? Description { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? LineId { get; set; }
    public string? MapUrl { get; set; }
    public int StarRating { get; set; } = 0;            // 0 = ไม่ระบุ
    public string? ImagesJson { get; set; }             // ["url", ...]
    public string? AmenitiesJson { get; set; }          // ["wifi","pool","parking",...]

    // ── เวลาเข้า-ออก ──
    public TimeOnly CheckInTime { get; set; } = new(14, 0);
    public TimeOnly CheckOutTime { get; set; } = new(12, 0);
    /// <summary>เข้าพักได้เร็วสุดกี่ชั่วโมงก่อนเวลาเช็คอิน (0 = ไม่อนุญาต) — คิดค่า EarlyCheckInFee</summary>
    public int EarlyCheckInHours { get; set; } = 0;
    public decimal EarlyCheckInFee { get; set; } = 0;
    public int LateCheckOutHours { get; set; } = 0;
    public decimal LateCheckOutFee { get; set; } = 0;

    // ── กติกาการจอง ──
    public int MinNights { get; set; } = 1;
    public int MaxNights { get; set; } = 30;
    /// <summary>จองล่วงหน้าได้สูงสุดกี่วัน</summary>
    public int MaxAdvanceDays { get; set; } = 365;
    /// <summary>ต้องจองล่วงหน้าอย่างน้อยกี่ชั่วโมงก่อนเช็คอิน (0 = จองวันนี้ได้)</summary>
    public int MinAdvanceHours { get; set; } = 0;
    /// <summary>ยืนยันการจองอัตโนมัติเมื่อจ่ายมัดจำแล้ว (false = พนักงานกดยืนยันเอง)</summary>
    public bool AutoConfirmOnDeposit { get; set; } = true;
    /// <summary>ยืนยันทันทีโดยไม่ต้องมัดจำ (สำหรับที่พักที่ไม่เก็บมัดจำ)</summary>
    public bool ConfirmWithoutDeposit { get; set; } = false;
    /// <summary>กันห้องไว้ให้จองที่ยังไม่จ่ายมัดจำนานกี่นาที (หมดเวลา = ปล่อยห้อง)</summary>
    public int PaymentHoldMinutes { get; set; } = 60 * 24;
    /// <summary>อนุญาต overbooking กี่ห้อง (0 = ห้าม)</summary>
    public int OverbookingAllowance { get; set; } = 0;
    public int ChildMaxAge { get; set; } = 11;          // เด็ก = อายุ ≤ ค่านี้
    public int InfantMaxAge { get; set; } = 2;          // ทารก = อายุ ≤ ค่านี้ (ไม่คิดเงิน)
    /// <summary>เปิดให้จองผ่านเว็บ (false = รับจองเฉพาะพนักงาน)</summary>
    public bool OnlineBookingEnabled { get; set; } = true;
    public bool RequireGuestIdNumber { get; set; } = false;   // ต้องกรอกเลขบัตร/พาสปอร์ตตอนจอง

    // ── มัดจำ ──
    /// <summary>% ของยอดรวมที่เรียกเก็บเป็นมัดจำ (0 = ไม่เก็บ)</summary>
    public decimal DepositPercent { get; set; } = 50;
    /// <summary>มัดจำแบบจำนวนเงินคงที่ต่อการจอง (มีค่า = ใช้แทน %)</summary>
    public decimal? DepositFixedAmount { get; set; }
    public decimal DepositMinAmount { get; set; } = 0;
    public decimal? DepositMaxAmount { get; set; }
    /// <summary>ผังบัญชีพักมัดจำ (null = 21712 ค่า default ของระบบ)</summary>
    public string? DepositDeferredAccountCode { get; set; }
    /// <summary>true = มัดจำยังไม่เกิด tax point (พัก 21913 จนเช็คอิน) — ปกติ false:
    /// มัดจำค่าห้องเป็น "ส่วนหนึ่งของราคา" tax point เกิดเมื่อรับเงิน (§78/1)</summary>
    public bool DepositOutputVatDeferred { get; set; } = false;

    // ── ภาษี/ค่าบริการ ──
    /// <summary>ราคาห้องที่ตั้งเป็นราคารวม VAT แล้ว (แสดงลูกค้าแบบ "รวมภาษี")</summary>
    public bool PricesIncludeVat { get; set; } = true;
    /// <summary>คิด VAT ห้องพักหรือไม่ — override ระดับที่พัก (null = ตาม Company.IsVatRegistered)</summary>
    public bool? ChargeVat { get; set; }
    /// <summary>Service charge % (โรงแรมไทยนิยม 10) — 0 = ไม่คิด</summary>
    public decimal ServiceChargePercent { get; set; } = 0;
    /// <summary>ผังบัญชีรายได้ค่าห้อง (null = ผังรายได้ default ของสินค้า/บริษัท)</summary>
    public string? RoomRevenueAccountCode { get; set; }
    public string? ServiceChargeAccountCode { get; set; }

    // ── ราคาตามวัน ──
    /// <summary>ตัวคูณราคาวันศุกร์–เสาร์ (1.00 = ไม่ปรับ) — TakeTime "Weekend Premium"</summary>
    public decimal WeekendMultiplier { get; set; } = 1.00m;
    /// <summary>วันที่ถือเป็น "สุดสัปดาห์" (bitmask: อา=1 จ=2 อ=4 พ=8 พฤ=16 ศ=32 ส=64) default ศ+ส</summary>
    public int WeekendDaysMask { get; set; } = 32 | 64;
    /// <summary>ราคาผู้เข้าพักเกินจำนวนมาตรฐาน ต่อคน ต่อคืน (default ระดับที่พัก)</summary>
    public decimal ExtraGuestPrice { get; set; } = 0;

    // ── นโยบายยกเลิก default ──
    public Guid? DefaultCancellationPolicyId { get; set; }
    public LodgingCancellationPolicy? DefaultCancellationPolicy { get; set; }
    /// <summary>ค่าปรับ no-show เป็น % ของยอดรวม (ปกติ = 100 = ริบทั้งหมด / หรือ 1 คืน ⇒ ใส่ตามนโยบาย)</summary>
    public decimal NoShowChargePercent { get; set; } = 100;
    /// <summary>ให้ระบบออกเอกสารบัญชีให้แค่ไหน (Full/ReceiptOnly/Off) — ดู
    /// <see cref="LodgingAccountingMode"/>. ทุกโหมดนับมิเตอร์การเข้าพักเท่ากัน</summary>
    public LodgingAccountingMode AccountingMode { get; set; } = LodgingAccountingMode.Full;

    /// <summary>ผู้ใช้ยืนยันว่า "ออกใบกำกับภาษีจากระบบอื่น" ตอนเลือกโหมด Off
    /// (บริษัทจด VAT เท่านั้นที่ต้องยืนยัน) — เก็บวัน+ผู้ยืนยันเป็นหลักฐาน</summary>
    public DateTime? AccountingModeAckAt { get; set; }
    public string? AccountingModeAckBy { get; set; }

    /// <summary>ผังบัญชี "รายได้ริบมัดจำ/ค่าปรับยกเลิก" (TakeTime FORFEIT_INCOME 41220) —
    /// null = ลงรายได้ค่าห้อง. ใช้ตอน RealizeDeposit ส่วนที่ริบเมื่อยกเลิก/no-show</summary>
    public string? CancellationFeeAccountCode { get; set; }

    // ── การแจ้งเตือน/ข้อความ ──
    public string? ConfirmationMessage { get; set; }    // ข้อความในอีเมล/หน้ายืนยัน
    public string? HouseRules { get; set; }             // กฎที่พัก (แสดงบนเว็บ + ใบยืนยัน)
    public bool NotifyOwnerOnBooking { get; set; } = true;
    public string? NotifyEmails { get; set; }           // คั่นด้วย , — ผู้รับแจ้งเตือนเมื่อมีจองใหม่

    // ── แม่บ้าน ──
    public int HousekeepingMinutesPerRoom { get; set; } = 30;
    public bool AutoCreateHousekeepingTaskOnCheckout { get; set; } = true;

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public ICollection<LodgingRoomType> RoomTypes { get; set; } = new List<LodgingRoomType>();
    public ICollection<LodgingRatePlan> RatePlans { get; set; } = new List<LodgingRatePlan>();
    public ICollection<LodgingSeason> Seasons { get; set; } = new List<LodgingSeason>();
    public ICollection<LodgingExtra> Extras { get; set; } = new List<LodgingExtra>();
}

/// <summary>ประเภทห้อง (Standard/Deluxe/Suite/บ้านหลัง A) — หน่วยที่ลูกค้าเลือกจอง</summary>
public class LodgingRoomType : TenantEntity
{
    public Guid PropertyId { get; set; }
    public LodgingProperty Property { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Code { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? Description { get; set; }
    public string? ImagesJson { get; set; }
    public string? AmenitiesJson { get; set; }
    public string? BedType { get; set; }                // "1 King" / "2 Twin"
    public decimal? SizeSqm { get; set; }
    public string? ViewType { get; set; }               // วิวทะเล/สวน/เมือง

    // ── ความจุ ──
    public int StandardOccupancy { get; set; } = 2;     // จำนวนคนที่รวมในราคา
    public int MaxAdults { get; set; } = 2;
    public int MaxChildren { get; set; } = 1;
    public int MaxOccupancy { get; set; } = 3;          // รวมเด็ก
    public bool AllowExtraBed { get; set; } = false;
    public int MaxExtraBeds { get; set; } = 0;

    // ── ราคา ──
    public LodgingPricingMode PricingMode { get; set; } = LodgingPricingMode.PerUnit;
    /// <summary>ราคาฐานต่อคืน (PerUnit) หรือต่อคนต่อคืน (PerPerson) — ตามฤดูกาลปกติ</summary>
    public decimal BaseRate { get; set; }
    /// <summary>override ราคาแขกเกิน (null = ใช้ของที่พัก)</summary>
    public decimal? ExtraGuestPrice { get; set; }
    public decimal? ExtraBedPrice { get; set; }
    public int? MinNights { get; set; }                 // override ที่พัก
    public bool IncludesBreakfast { get; set; } = false;

    /// <summary>สินค้า/บริการใน ERP ที่ใช้ลงรายได้ (null = สร้างให้อัตโนมัติตอน seed)</summary>
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    public ICollection<LodgingUnit> Units { get; set; } = new List<LodgingUnit>();
}

/// <summary>ห้อง/หลัง/เตียง จริงที่ assign ให้แขก (หมายเลขห้อง 101, 102, บ้าน A…)</summary>
public class LodgingUnit : TenantEntity
{
    public Guid RoomTypeId { get; set; }
    public LodgingRoomType RoomType { get; set; } = null!;

    public string Number { get; set; } = "";            // "101" / "Villa A"
    public string? Floor { get; set; }
    public string? Building { get; set; }
    public string? Notes { get; set; }
    public LodgingHousekeepingStatus HousekeepingStatus { get; set; } = LodgingHousekeepingStatus.VacantClean;
    /// <summary>ปิดขายชั่วคราว (ซ่อม/ปรับปรุง) — ไม่นับเป็นห้องว่าง</summary>
    public bool IsOutOfService { get; set; } = false;
    public DateTime? OutOfServiceUntil { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>แผนราคา (Rate plan) — Standard / Non-refundable / รวมอาหารเช้า / Long stay</summary>
public class LodgingRatePlan : TenantEntity
{
    public Guid PropertyId { get; set; }
    public LodgingProperty Property { get; set; } = null!;
    /// <summary>null = ใช้ได้กับทุกประเภทห้อง</summary>
    public Guid? RoomTypeId { get; set; }
    public LodgingRoomType? RoomType { get; set; }

    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public LodgingRateAdjustMode AdjustMode { get; set; } = LodgingRateAdjustMode.Base;
    /// <summary>ค่าที่ใช้ตาม AdjustMode: Absolute=ราคา · Multiplier=ตัวคูณ · Delta=บาท</summary>
    public decimal AdjustValue { get; set; } = 0;
    public bool IncludesBreakfast { get; set; } = false;
    public bool IsRefundable { get; set; } = true;
    public Guid? CancellationPolicyId { get; set; }
    public LodgingCancellationPolicy? CancellationPolicy { get; set; }
    public int? MinNights { get; set; }
    public int? MaxNights { get; set; }
    /// <summary>ต้องจองล่วงหน้าอย่างน้อยกี่วัน (Early bird) — null = ไม่จำกัด</summary>
    public int? MinAdvanceDays { get; set; }
    /// <summary>จองล่วงหน้าไม่เกินกี่วัน (Last minute) — null = ไม่จำกัด</summary>
    public int? MaxAdvanceDays { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    /// <summary>bitmask วันเช็คอินที่ใช้ได้ (0 = ทุกวัน) อา=1 จ=2 อ=4 พ=8 พฤ=16 ศ=32 ส=64</summary>
    public int ApplicableDaysMask { get; set; } = 0;
    /// <summary>% มัดจำเฉพาะแผนนี้ (null = ตามที่พัก) — Non-refundable มักเก็บ 100</summary>
    public decimal? DepositPercent { get; set; }
    public bool IsDefault { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>ฤดูกาล/วันหยุด ที่คูณราคาฐาน (TakeTime PricingSeasons) — ช่วงแคบกว่าชนะ</summary>
public class LodgingSeason : TenantEntity
{
    public Guid PropertyId { get; set; }
    public LodgingProperty Property { get; set; } = null!;

    public string Name { get; set; } = "";
    public LodgingSeasonType SeasonType { get; set; } = LodgingSeasonType.High;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    /// <summary>ตัวคูณราคาฐาน เช่น 1.50 = +50%</summary>
    public decimal Multiplier { get; set; } = 1.00m;
    /// <summary>ใช้ซ้ำทุกปี (เทียบเฉพาะเดือน-วัน) — สงกรานต์/ปีใหม่</summary>
    public bool IsRecurringYearly { get; set; } = false;
    /// <summary>ขั้นต่ำกี่คืนในช่วงนี้ (null = ตามที่พัก)</summary>
    public int? MinNights { get; set; }
    /// <summary>จำกัดเฉพาะประเภทห้อง (JSON array ของ RoomTypeId) — null = ทุกห้อง</summary>
    public string? RoomTypeIdsJson { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>ราคา/การขายเฉพาะวันของประเภทห้อง — ปิดขาย · ราคาพิเศษ · จำนวนห้องที่ปล่อยขาย</summary>
public class LodgingRateOverride : TenantEntity
{
    public Guid RoomTypeId { get; set; }
    public LodgingRoomType RoomType { get; set; } = null!;

    public DateTime Date { get; set; }                  // วันเข้าพัก (คืนของวันนั้น)
    /// <summary>ราคาต่อคืนที่ใช้แทนทุกอย่าง (null = ไม่ override ราคา)</summary>
    public decimal? Rate { get; set; }
    public bool StopSell { get; set; } = false;
    /// <summary>จำนวนห้องที่ปล่อยขายวันนั้น (null = ทั้งหมดที่มี)</summary>
    public int? Allotment { get; set; }
    public int? MinNights { get; set; }
    public string? Note { get; set; }
}

/// <summary>นโยบายยกเลิก — กฎเป็นขั้นบันได "ยกเลิกก่อนเข้าพัก N วัน คิด X%"</summary>
public class LodgingCancellationPolicy : TenantEntity
{
    public Guid PropertyId { get; set; }
    public LodgingProperty Property { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>JSON: [{"daysBefore":7,"penaltyPercent":0},{"daysBefore":3,"penaltyPercent":50},{"daysBefore":0,"penaltyPercent":100}]
    /// เรียงจากมากไปน้อย — ใช้ข้อแรกที่ daysBefore ≤ วันที่เหลือ</summary>
    public string RulesJson { get; set; } = "[]";
    /// <summary>true = ไม่คืนเงินทุกกรณี (Non-refundable)</summary>
    public bool NonRefundable { get; set; } = false;
    public bool IsDefault { get; set; } = false;
    public bool IsActive { get; set; } = true;
}

/// <summary>บริการเสริม (add-on) ที่เลือกได้ตอนจอง — อาหารเช้า · เตียงเสริม · รถรับส่ง</summary>
public class LodgingExtra : TenantEntity
{
    public Guid PropertyId { get; set; }
    public LodgingProperty Property { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public LodgingExtraCategory Category { get; set; } = LodgingExtraCategory.Other;
    public LodgingExtraPriceMode PriceMode { get; set; } = LodgingExtraPriceMode.PerStay;
    public decimal Price { get; set; }
    public int? MaxQuantity { get; set; }
    /// <summary>สินค้า/บริการ ERP ที่ใช้ลงรายได้ (null = ลงรายได้ค่าห้อง)</summary>
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }
    public bool ShowOnWebsite { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

/// <summary>การจองที่พัก 1 รายการ (อาจมีหลายห้อง)</summary>
public class LodgingReservation : TenantEntity
{
    public Guid PropertyId { get; set; }
    public LodgingProperty Property { get; set; } = null!;
    public Guid? SiteId { get; set; }

    public string ReservationNumber { get; set; } = "";
    /// <summary>token สาธารณะสำหรับให้แขกเปิดดู/อัปโหลดสลิป/ยกเลิก โดยไม่ต้องล็อกอิน</summary>
    public string PublicToken { get; set; } = "";
    public LodgingReservationStatus Status { get; set; } = LodgingReservationStatus.Pending;
    public LodgingReservationSource Source { get; set; } = LodgingReservationSource.Web;
    public string? SourceReference { get; set; }        // เลขจอง OTA/agent

    public DateTime CheckInDate { get; set; }           // วันที่ (00:00 UTC ตามปฏิทินไทย)
    public DateTime CheckOutDate { get; set; }
    public int Nights { get; set; }
    public int Adults { get; set; } = 2;
    public int Children { get; set; } = 0;
    public int Infants { get; set; } = 0;
    public string? ArrivalTime { get; set; }            // "18:30"
    public string? SpecialRequests { get; set; }

    // ── ผู้เข้าพัก ──
    public Guid? ContactId { get; set; }
    public Contact? Contact { get; set; }
    public Guid? SiteCustomerId { get; set; }
    public string GuestName { get; set; } = "";
    public string? GuestEmail { get; set; }
    public string? GuestPhone { get; set; }
    public string? GuestNationality { get; set; }
    /// <summary>เลขบัตร/พาสปอร์ต — PII: แสดงแบบ mask เสมอ (PDPA ม.26)</summary>
    public string? GuestIdNumber { get; set; }
    public string? GuestAddress { get; set; }
    public string? GuestTaxId { get; set; }             // ถ้าลูกค้าขอใบกำกับในนามบริษัท
    public string? GuestCompanyName { get; set; }

    // ── ราคา (snapshot ตอนจอง) ──
    public Guid? RatePlanId { get; set; }
    public LodgingRatePlan? RatePlan { get; set; }
    public Guid? CancellationPolicyId { get; set; }
    public string? CancellationPolicySnapshotJson { get; set; }   // ตรึงกฎ ณ วันจอง
    public string? PromoCode { get; set; }
    public decimal RoomSubtotal { get; set; }           // ค่าห้องรวมทุกคืนทุกห้อง
    public decimal ExtrasTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ServiceChargeAmount { get; set; }
    public decimal VatAmount { get; set; }              // ส่วน VAT ที่อยู่ในยอดรวม (informational)
    public decimal TotalAmount { get; set; }            // ยอดรวมที่ลูกค้าต้องจ่าย (ค่าห้อง+เสริม+SC[+VAT])
    public decimal FolioTotal { get; set; }             // ค่าใช้จ่ายระหว่างพักที่ค้าง (คำนวณจาก charges)
    public string Currency { get; set; } = "THB";
    public string? PriceBreakdownJson { get; set; }     // รายละเอียดต่อคืน (ให้แขกเห็น + audit)

    // ── การเงิน ──
    public decimal DepositRequired { get; set; }
    public decimal DepositPaid { get; set; }
    public decimal PaidAmount { get; set; }             // รวมทุกการชำระ (มัดจำ+ส่วนที่เหลือ)
    public DateTime? HoldExpiresAt { get; set; }
    /// <summary>ใบเสร็จมัดจำ (Document IsDeposit=true)</summary>
    public Guid? DepositDocumentId { get; set; }
    public Document? DepositDocument { get; set; }
    /// <summary>ใบกำกับภาษี/ใบเสร็จ ตอนเช็คเอาต์ (ตัดมัดจำแล้ว)</summary>
    public Guid? FinalDocumentId { get; set; }
    public Document? FinalDocument { get; set; }
    public string? PaymentSlipUrl { get; set; }
    public string? PaymentReference { get; set; }
    public DateTime? SlipUploadedAt { get; set; }

    // ── lifecycle ──
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public decimal CancellationFee { get; set; }
    public decimal RefundAmount { get; set; }
    public string? InternalNotes { get; set; }
    public string? ConfirmedBy { get; set; }

    /// <summary>งวดที่การเข้าพักนี้ถูกนับเป็น 1 หน่วยมิเตอร์ (yyyy-MM) — null = ยังไม่นับ
    ///
    /// มิเตอร์ของโมดูลที่พักคือ "การเข้าพักที่ปิดสถานะ" ไม่ใช่จำนวนเอกสาร: 1 การเข้าพัก
    /// = 1 หน่วยเสมอ ไม่ว่าจะออกเอกสารกี่ใบหรือไม่ออกเลย (LODGING_LICENSING_PLAN §13.2)
    /// กันนับซ้ำเมื่อสถานะถูกแตะหลายรอบ (เช่น night audit ตามมาทีหลัง)</summary>
    public string? MeteredPeriod { get; set; }

    public ICollection<LodgingReservationRoom> Rooms { get; set; } = new List<LodgingReservationRoom>();
    public ICollection<LodgingReservationExtra> Extras { get; set; } = new List<LodgingReservationExtra>();
    public ICollection<LodgingFolioCharge> Charges { get; set; } = new List<LodgingFolioCharge>();
}

/// <summary>ห้องที่จอง 1 ห้องในการจอง — assign หมายเลขห้องจริงได้ทีหลัง</summary>
public class LodgingReservationRoom : TenantEntity
{
    public Guid ReservationId { get; set; }
    public LodgingReservation Reservation { get; set; } = null!;
    public Guid RoomTypeId { get; set; }
    public LodgingRoomType RoomType { get; set; } = null!;
    public Guid? UnitId { get; set; }
    public LodgingUnit? Unit { get; set; }

    public string RoomTypeName { get; set; } = "";      // snapshot
    public int Adults { get; set; } = 2;
    public int Children { get; set; } = 0;
    public int ExtraBeds { get; set; } = 0;
    /// <summary>JSON: [{"date":"2026-09-10","rate":1500,"season":"High","note":"..."}]</summary>
    public string? NightlyRatesJson { get; set; }
    public decimal Subtotal { get; set; }               // รวมทุกคืนของห้องนี้ (รวมแขกเกิน/เตียงเสริม)
    public string? GuestNames { get; set; }             // ชื่อผู้เข้าพักในห้องนี้
}

public class LodgingReservationExtra : TenantEntity
{
    public Guid ReservationId { get; set; }
    public LodgingReservation Reservation { get; set; } = null!;
    public Guid? ExtraId { get; set; }
    public string Name { get; set; } = "";
    public LodgingExtraPriceMode PriceMode { get; set; }
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal Total { get; set; }
    public Guid? ProductId { get; set; }
}

/// <summary>ค่าใช้จ่ายระหว่างพัก (folio) — มินิบาร์ · รูมเซอร์วิส · ค่าปรับ (TakeTime Reservation_Product_Charges)</summary>
public class LodgingFolioCharge : TenantEntity
{
    public Guid ReservationId { get; set; }
    public LodgingReservation Reservation { get; set; } = null!;
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }

    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal Total { get; set; }
    public decimal VatRate { get; set; } = 7;
    public LodgingChargeSource Source { get; set; } = LodgingChargeSource.Manual;
    public LodgingChargeStatus Status { get; set; } = LodgingChargeStatus.Pending;
    public DateTime ChargedAt { get; set; } = DateTime.UtcNow;
    public string? PosOrderNumber { get; set; }
    public string? Notes { get; set; }
}

/// <summary>งานแม่บ้าน/ซ่อมบำรุง ต่อห้อง (TakeTime HousekeepingTasks)</summary>
public class LodgingHousekeepingTask : TenantEntity
{
    public Guid PropertyId { get; set; }
    public Guid UnitId { get; set; }
    public LodgingUnit Unit { get; set; } = null!;
    public Guid? ReservationId { get; set; }

    public LodgingHousekeepingTaskType TaskType { get; set; } = LodgingHousekeepingTaskType.CheckoutClean;
    public LodgingTaskPriority Priority { get; set; } = LodgingTaskPriority.Normal;
    public LodgingTaskStatus Status { get; set; } = LodgingTaskStatus.Pending;
    public Guid? AssignedEmployeeId { get; set; }
    public string? AssignedToName { get; set; }
    public DateTime? DueAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public int EstimatedMinutes { get; set; } = 30;
    public string? Notes { get; set; }
}

/// <summary>คำขอจากแขก (พอร์ทัลแขก/แชท/พนักงานบันทึก) — housekeeping · concierge · แจ้งซ่อม</summary>
public class LodgingGuestRequest : TenantEntity
{
    public Guid PropertyId { get; set; }
    public Guid ReservationId { get; set; }
    public LodgingReservation Reservation { get; set; } = null!;

    public LodgingGuestRequestType RequestType { get; set; } = LodgingGuestRequestType.Other;
    public string Details { get; set; } = "";
    public LodgingTaskStatus Status { get; set; } = LodgingTaskStatus.Pending;
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ResponseNote { get; set; }
}
