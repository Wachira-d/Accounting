using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Lodging;

// ============================================================================
// DTO ของโมดูลที่พัก — แบ่งเป็น 3 กลุ่ม:
//   1. ตั้งค่า (Property/RoomType/Unit/RatePlan/Season/Override/Policy/Extra)
//   2. หน้าเว็บสาธารณะ (ค้นหาห้องว่าง → quote → จอง → ดู/อัปโหลดสลิป/ยกเลิกด้วย token)
//   3. หลังบ้าน (รายการจอง · ยืนยัน · เช็คอิน/เอาต์ · folio · แม่บ้าน · แดชบอร์ด)
// กติกา "เก็บแล้วต้อง echo กลับ" (กฎเหล็ก #4 A): ทุก field ที่รับใน Request ต้องมีใน
// Response ตัวเดียวกันเสมอ — Property/RoomType ใช้ record เดียวทั้งสองทาง
// ============================================================================

// ───────────────────────────── 1. ตั้งค่า ─────────────────────────────

/// <summary>ที่พัก — ใช้ทั้ง Create/Update (request) และตอบกลับ (response) เพื่อไม่ให้ drift</summary>
public class LodgingPropertyDto
{
    public Guid? Id { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? BranchId { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Code { get; set; } = "";
    public LodgingPropertyType PropertyType { get; set; } = LodgingPropertyType.Hotel;
    public string? Description { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? LineId { get; set; }
    public string? MapUrl { get; set; }
    public int StarRating { get; set; }
    public List<string> Images { get; set; } = new();
    public List<string> Amenities { get; set; } = new();

    public string CheckInTime { get; set; } = "14:00";
    public string CheckOutTime { get; set; } = "12:00";
    public int EarlyCheckInHours { get; set; }
    public decimal EarlyCheckInFee { get; set; }
    public int LateCheckOutHours { get; set; }
    public decimal LateCheckOutFee { get; set; }

    public int MinNights { get; set; } = 1;
    public int MaxNights { get; set; } = 30;
    public int MaxAdvanceDays { get; set; } = 365;
    public int MinAdvanceHours { get; set; }
    public bool AutoConfirmOnDeposit { get; set; } = true;
    public bool ConfirmWithoutDeposit { get; set; }
    public int PaymentHoldMinutes { get; set; } = 1440;
    public int OverbookingAllowance { get; set; }
    public int ChildMaxAge { get; set; } = 11;
    public int InfantMaxAge { get; set; } = 2;
    public bool OnlineBookingEnabled { get; set; } = true;
    public bool RequireGuestIdNumber { get; set; }

    public decimal DepositPercent { get; set; } = 50;
    public decimal? DepositFixedAmount { get; set; }
    public decimal DepositMinAmount { get; set; }
    public decimal? DepositMaxAmount { get; set; }
    public string? DepositDeferredAccountCode { get; set; }
    public bool DepositOutputVatDeferred { get; set; }
    public LodgingAccountingMode AccountingMode { get; set; } = LodgingAccountingMode.Full;
    /// <summary>ผู้ใช้ติ๊กยืนยันว่าออกใบกำกับจากระบบอื่น (จำเป็นเมื่อเลือก Off + จด VAT)</summary>
    public bool AccountingModeAcknowledged { get; set; }

    public bool PricesIncludeVat { get; set; } = true;
    public bool? ChargeVat { get; set; }
    public decimal ServiceChargePercent { get; set; }
    public string? RoomRevenueAccountCode { get; set; }
    public string? ServiceChargeAccountCode { get; set; }
    public string? CancellationFeeAccountCode { get; set; }

    public decimal WeekendMultiplier { get; set; } = 1.00m;
    public int WeekendDaysMask { get; set; } = 32 | 64;
    public decimal ExtraGuestPrice { get; set; }

    public Guid? DefaultCancellationPolicyId { get; set; }
    public decimal NoShowChargePercent { get; set; } = 100;

    public string? ConfirmationMessage { get; set; }
    public string? HouseRules { get; set; }
    public bool NotifyOwnerOnBooking { get; set; } = true;
    public string? NotifyEmails { get; set; }

    public int HousekeepingMinutesPerRoom { get; set; } = 30;
    public bool AutoCreateHousekeepingTaskOnCheckout { get; set; } = true;

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    // ── response-only (สรุปให้หน้ารายการ) ──
    public int RoomTypeCount { get; set; }
    public int UnitCount { get; set; }
    public string? SiteName { get; set; }
    /// <summary>อัตรา VAT ที่ใช้จริง (คำนวณจาก ChargeVat ?? Company.IsVatRegistered) — หน้าเว็บแสดงอย่างเดียว</summary>
    public decimal EffectiveVatRate { get; set; }
}

public class LodgingRoomTypeDto
{
    public Guid? Id { get; set; }
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Code { get; set; } = "";
    public string? Slug { get; set; }
    public string? Description { get; set; }
    public List<string> Images { get; set; } = new();
    public List<string> Amenities { get; set; } = new();
    public string? BedType { get; set; }
    public decimal? SizeSqm { get; set; }
    public string? ViewType { get; set; }
    public int StandardOccupancy { get; set; } = 2;
    public int MaxAdults { get; set; } = 2;
    public int MaxChildren { get; set; } = 1;
    public int MaxOccupancy { get; set; } = 3;
    public bool AllowExtraBed { get; set; }
    public int MaxExtraBeds { get; set; }
    public LodgingPricingMode PricingMode { get; set; } = LodgingPricingMode.PerUnit;
    public decimal BaseRate { get; set; }
    public decimal? ExtraGuestPrice { get; set; }
    public decimal? ExtraBedPrice { get; set; }
    public int? MinNights { get; set; }
    public bool IncludesBreakfast { get; set; }
    public Guid? ProductId { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    // response-only
    public int UnitCount { get; set; }
    public List<LodgingUnitDto> Units { get; set; } = new();
}

public class LodgingUnitDto
{
    public Guid? Id { get; set; }
    public Guid RoomTypeId { get; set; }
    public string Number { get; set; } = "";
    public string? Floor { get; set; }
    public string? Building { get; set; }
    public string? Notes { get; set; }
    public LodgingHousekeepingStatus HousekeepingStatus { get; set; } = LodgingHousekeepingStatus.VacantClean;
    public bool IsOutOfService { get; set; }
    public DateTime? OutOfServiceUntil { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    // response-only
    public string? RoomTypeName { get; set; }
}

public class LodgingRatePlanDto
{
    public Guid? Id { get; set; }
    public Guid PropertyId { get; set; }
    public Guid? RoomTypeId { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string Code { get; set; } = "";
    public string? Description { get; set; }
    public LodgingRateAdjustMode AdjustMode { get; set; } = LodgingRateAdjustMode.Base;
    public decimal AdjustValue { get; set; }
    public bool IncludesBreakfast { get; set; }
    public bool IsRefundable { get; set; } = true;
    public Guid? CancellationPolicyId { get; set; }
    public int? MinNights { get; set; }
    public int? MaxNights { get; set; }
    public int? MinAdvanceDays { get; set; }
    public int? MaxAdvanceDays { get; set; }
    public DateTime? ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public int ApplicableDaysMask { get; set; }
    public decimal? DepositPercent { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public class LodgingSeasonDto
{
    public Guid? Id { get; set; }
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = "";
    public LodgingSeasonType SeasonType { get; set; } = LodgingSeasonType.High;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal Multiplier { get; set; } = 1.00m;
    public bool IsRecurringYearly { get; set; }
    public int? MinNights { get; set; }
    public List<Guid> RoomTypeIds { get; set; } = new();
    public bool IsActive { get; set; } = true;
}

public class LodgingRateOverrideDto
{
    public Guid? Id { get; set; }
    public Guid RoomTypeId { get; set; }
    public DateTime Date { get; set; }
    public decimal? Rate { get; set; }
    public bool StopSell { get; set; }
    public int? Allotment { get; set; }
    public int? MinNights { get; set; }
    public string? Note { get; set; }
}

/// <summary>บันทึก override หลายวันทีเดียว (ปฏิทินราคา: ลากเลือกช่วง → ตั้งราคา/ปิดขาย)</summary>
public record LodgingRateOverrideBulkRequest(
    Guid RoomTypeId, DateTime FromDate, DateTime ToDate,
    decimal? Rate, bool? StopSell, int? Allotment, int? MinNights, string? Note,
    /// <summary>true = ลบ override ในช่วงนั้นทิ้ง (กลับไปใช้ราคาปกติ)</summary>
    bool Clear = false);

public class LodgingCancellationPolicyDto
{
    public Guid? Id { get; set; }
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public List<LodgingCancellationRuleDto> Rules { get; set; } = new();
    public bool NonRefundable { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
}

public record LodgingCancellationRuleDto(int DaysBefore, decimal PenaltyPercent);

public class LodgingExtraDto
{
    public Guid? Id { get; set; }
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public LodgingExtraCategory Category { get; set; } = LodgingExtraCategory.Other;
    public LodgingExtraPriceMode PriceMode { get; set; } = LodgingExtraPriceMode.PerStay;
    public decimal Price { get; set; }
    public int? MaxQuantity { get; set; }
    public Guid? ProductId { get; set; }
    public bool ShowOnWebsite { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

// ───────────────────────────── 2. หน้าเว็บสาธารณะ ─────────────────────────────

/// <summary>ข้อมูลที่พักที่หน้าเว็บสาธารณะต้องใช้ (ไม่มีผังบัญชี/อีเมลแจ้งเตือน/ค่าภายใน)</summary>
public class LodgingPublicInfo
{
    public Guid PropertyId { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public LodgingPropertyType PropertyType { get; set; }
    public string? Description { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? LineId { get; set; }
    public string? MapUrl { get; set; }
    public int StarRating { get; set; }
    public List<string> Images { get; set; } = new();
    public List<string> Amenities { get; set; } = new();
    public string CheckInTime { get; set; } = "14:00";
    public string CheckOutTime { get; set; } = "12:00";
    public int MinNights { get; set; }
    public int MaxNights { get; set; }
    public int MaxAdvanceDays { get; set; }
    public int MinAdvanceHours { get; set; }
    public int ChildMaxAge { get; set; }
    public int InfantMaxAge { get; set; }
    public bool OnlineBookingEnabled { get; set; }
    public bool RequireGuestIdNumber { get; set; }
    public decimal DepositPercent { get; set; }
    public decimal? DepositFixedAmount { get; set; }
    public bool PricesIncludeVat { get; set; }
    public decimal VatRate { get; set; }
    public decimal ServiceChargePercent { get; set; }
    public string? HouseRules { get; set; }
    public string? ConfirmationMessage { get; set; }
    public int PaymentHoldMinutes { get; set; }
    public string Currency { get; set; } = "THB";
    public List<LodgingRoomTypeDto> RoomTypes { get; set; } = new();
    public List<LodgingExtraDto> Extras { get; set; } = new();
    public List<LodgingRatePlanDto> RatePlans { get; set; } = new();
    public List<LodgingCancellationPolicyDto> Policies { get; set; } = new();
}

public record LodgingSearchRequest(
    DateTime CheckIn, DateTime CheckOut,
    int Adults = 2, int Children = 0, int Infants = 0, int Rooms = 1,
    Guid? RatePlanId = null, string? PromoCode = null);

/// <summary>ผลค้นหาต่อประเภทห้อง — ราคาต่อห้องสำหรับช่วง+จำนวนแขกที่ขอ</summary>
public class LodgingSearchResult
{
    public Guid RoomTypeId { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public List<string> Images { get; set; } = new();
    public List<string> Amenities { get; set; } = new();
    public string? BedType { get; set; }
    public decimal? SizeSqm { get; set; }
    public string? ViewType { get; set; }
    public int StandardOccupancy { get; set; }
    public int MaxAdults { get; set; }
    public int MaxChildren { get; set; }
    public int MaxOccupancy { get; set; }
    public bool AllowExtraBed { get; set; }
    public LodgingPricingMode PricingMode { get; set; }
    public bool IncludesBreakfast { get; set; }
    public int AvailableRooms { get; set; }
    public int MinNights { get; set; }
    /// <summary>ราคาต่อห้องตลอดการเข้าพัก (รวมแขกเกิน) ตามแผนราคา default/ที่เลือก</summary>
    public decimal PricePerRoom { get; set; }
    public decimal AverageNightlyRate { get; set; }
    public List<LodgingNightlyRateDto> Nights { get; set; } = new();
    /// <summary>ราคาตามแผนราคาแต่ละแผนที่ใช้ได้กับช่วงนี้ (ให้ลูกค้าเลือก Non-refundable / รวมอาหารเช้า)</summary>
    public List<LodgingRatePlanQuote> RatePlans { get; set; } = new();
    /// <summary>เหตุผลที่จองไม่ได้ (null = จองได้)</summary>
    public string? UnavailableReason { get; set; }
}

public record LodgingNightlyRateDto(DateTime Date, decimal Rate, string? SeasonName, bool IsWeekend, bool IsOverride);
public record LodgingRatePlanQuote(Guid RatePlanId, string Name, string Code, decimal PricePerRoom, bool IncludesBreakfast, bool IsRefundable, decimal? DepositPercent, string? CancellationPolicyName);

/// <summary>ขอราคารวมก่อนจอง (หน้าเว็บส่งห้อง+แขก+บริการเสริมมา ระบบคิดให้ทั้งหมด — JS ห้ามคิดเอง)</summary>
public record LodgingQuoteRequest(
    DateTime CheckIn, DateTime CheckOut,
    List<LodgingQuoteRoomRequest> Rooms,
    List<LodgingQuoteExtraRequest>? Extras = null,
    Guid? RatePlanId = null, string? PromoCode = null);

public record LodgingQuoteRoomRequest(Guid RoomTypeId, int Adults = 2, int Children = 0, int ExtraBeds = 0);
public record LodgingQuoteExtraRequest(Guid ExtraId, int Quantity = 1);

public class LodgingQuoteResponse
{
    public DateTime CheckIn { get; set; }
    public DateTime CheckOut { get; set; }
    public int Nights { get; set; }
    public Guid? RatePlanId { get; set; }
    public string? RatePlanName { get; set; }
    public List<LodgingQuoteRoomLine> Rooms { get; set; } = new();
    public List<LodgingQuoteExtraLine> Extras { get; set; } = new();
    public decimal RoomSubtotal { get; set; }
    public decimal ExtrasTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ServiceChargeAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal DepositRequired { get; set; }
    public bool PricesIncludeVat { get; set; }
    public decimal VatRate { get; set; }
    public string Currency { get; set; } = "THB";
    public Guid? CancellationPolicyId { get; set; }
    public string? CancellationPolicyName { get; set; }
    public List<LodgingCancellationRuleDto> CancellationRules { get; set; } = new();
    public bool NonRefundable { get; set; }
    /// <summary>ข้อผิดพลาด (ห้องไม่ว่าง/คืนไม่ถึงขั้นต่ำ) — null = จองได้</summary>
    public List<string> Errors { get; set; } = new();
}

public class LodgingQuoteRoomLine
{
    public Guid RoomTypeId { get; set; }
    public string RoomTypeName { get; set; } = "";
    public int Adults { get; set; }
    public int Children { get; set; }
    public int ExtraBeds { get; set; }
    public decimal RoomRate { get; set; }
    public decimal ExtraGuestCharge { get; set; }
    public decimal ExtraBedCharge { get; set; }
    public decimal Subtotal { get; set; }
    public List<LodgingNightlyRateDto> Nights { get; set; } = new();
}

public class LodgingQuoteExtraLine
{
    public Guid ExtraId { get; set; }
    public string Name { get; set; } = "";
    public LodgingExtraPriceMode PriceMode { get; set; }
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal Total { get; set; }
}

/// <summary>สร้างการจอง — จากหน้าเว็บ (Source=Web) หรือพนักงาน (WalkIn/Phone/Agent/Ota)</summary>
public record LodgingCreateReservationRequest(
    DateTime CheckIn, DateTime CheckOut,
    List<LodgingQuoteRoomRequest> Rooms,
    string GuestName,
    string? GuestEmail,
    string? GuestPhone,
    List<LodgingQuoteExtraRequest>? Extras = null,
    Guid? RatePlanId = null,
    string? PromoCode = null,
    int Infants = 0,
    string? ArrivalTime = null,
    string? SpecialRequests = null,
    string? GuestNationality = null,
    string? GuestIdNumber = null,
    string? GuestAddress = null,
    string? GuestTaxId = null,
    string? GuestCompanyName = null,
    bool AcceptHouseRules = false,
    // ── ฝั่งพนักงานเท่านั้น ──
    LodgingReservationSource? Source = null,
    string? SourceReference = null,
    Guid? ContactId = null,
    string? InternalNotes = null,
    /// <summary>ยืนยันทันทีโดยไม่รอมัดจำ (พนักงานรับจองทางโทรศัพท์/OTA ที่จ่ายผ่านช่องทางอื่น)</summary>
    bool ConfirmImmediately = false);

public class LodgingReservationResponse
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public string PropertyName { get; set; } = "";
    public string ReservationNumber { get; set; } = "";
    /// <summary>ส่งเฉพาะให้เจ้าของการจอง (ตอนสร้าง/ตอนเปิดด้วย token) — หน้าหลังบ้านได้ด้วยเพื่อทำลิงก์ให้ลูกค้า</summary>
    public string? PublicToken { get; set; }
    public LodgingReservationStatus Status { get; set; }
    public LodgingReservationSource Source { get; set; }
    public string? SourceReference { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public int Nights { get; set; }
    public int Adults { get; set; }
    public int Children { get; set; }
    public int Infants { get; set; }
    public string? ArrivalTime { get; set; }
    public string? SpecialRequests { get; set; }
    public Guid? ContactId { get; set; }
    public string GuestName { get; set; } = "";
    public string? GuestEmail { get; set; }
    public string? GuestPhone { get; set; }
    public string? GuestNationality { get; set; }
    /// <summary>mask เสมอ (PDPA ม.26) — ตัวเต็มไม่ออกจากเซิร์ฟเวอร์ผ่าน DTO นี้</summary>
    public string? GuestIdNumberMasked { get; set; }
    public string? GuestAddress { get; set; }
    public string? GuestTaxId { get; set; }
    public string? GuestCompanyName { get; set; }
    public Guid? RatePlanId { get; set; }
    public string? RatePlanName { get; set; }
    public Guid? CancellationPolicyId { get; set; }
    public string? CancellationPolicyName { get; set; }
    public List<LodgingCancellationRuleDto> CancellationRules { get; set; } = new();
    public bool NonRefundable { get; set; }
    public string? PromoCode { get; set; }
    public decimal RoomSubtotal { get; set; }
    public decimal ExtrasTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal ServiceChargeAmount { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal FolioTotal { get; set; }
    /// <summary>ยอดที่ต้องจ่ายทั้งหมด = TotalAmount + FolioTotal</summary>
    public decimal GrandTotal { get; set; }
    public decimal DepositRequired { get; set; }
    public decimal DepositPaid { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal BalanceDue { get; set; }
    public string Currency { get; set; } = "THB";
    public DateTime? HoldExpiresAt { get; set; }
    public Guid? DepositDocumentId { get; set; }
    public string? DepositDocumentNumber { get; set; }
    public Guid? FinalDocumentId { get; set; }
    public string? FinalDocumentNumber { get; set; }
    public string? PaymentSlipUrl { get; set; }
    public string? PaymentReference { get; set; }
    public DateTime? SlipUploadedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CheckedInAt { get; set; }
    public DateTime? CheckedOutAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public decimal CancellationFee { get; set; }
    public decimal RefundAmount { get; set; }
    public string? InternalNotes { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<LodgingReservationRoomDto> Rooms { get; set; } = new();
    public List<LodgingReservationExtraDto> Extras { get; set; } = new();
    public List<LodgingFolioChargeDto> Charges { get; set; } = new();
    public List<LodgingGuestRequestDto> Requests { get; set; } = new();
    /// <summary>ข้อความยืนยัน/กติกาที่พัก (ให้หน้าลูกค้าแสดง)</summary>
    public string? ConfirmationMessage { get; set; }
    public string? HouseRules { get; set; }
    public string CheckInTime { get; set; } = "14:00";
    public string CheckOutTime { get; set; } = "12:00";
    public string? PropertyPhone { get; set; }
    public string? PropertyLineId { get; set; }
}

public class LodgingReservationRoomDto
{
    public Guid Id { get; set; }
    public Guid RoomTypeId { get; set; }
    public string RoomTypeName { get; set; } = "";
    public Guid? UnitId { get; set; }
    public string? UnitNumber { get; set; }
    public int Adults { get; set; }
    public int Children { get; set; }
    public int ExtraBeds { get; set; }
    public decimal Subtotal { get; set; }
    public string? GuestNames { get; set; }
    public List<LodgingNightlyRateDto> Nights { get; set; } = new();
}

public class LodgingReservationExtraDto
{
    public Guid Id { get; set; }
    public Guid? ExtraId { get; set; }
    public string Name { get; set; } = "";
    public LodgingExtraPriceMode PriceMode { get; set; }
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal Total { get; set; }
}

public class LodgingFolioChargeDto
{
    public Guid Id { get; set; }
    public Guid? ProductId { get; set; }
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Total { get; set; }
    public decimal VatRate { get; set; }
    public LodgingChargeSource Source { get; set; }
    public LodgingChargeStatus Status { get; set; }
    public DateTime ChargedAt { get; set; }
    public string? PosOrderNumber { get; set; }
    public string? Notes { get; set; }
}

public class LodgingReservationListItem
{
    public Guid Id { get; set; }
    public string ReservationNumber { get; set; } = "";
    public LodgingReservationStatus Status { get; set; }
    public LodgingReservationSource Source { get; set; }
    public string GuestName { get; set; } = "";
    public string? GuestPhone { get; set; }
    public DateTime CheckInDate { get; set; }
    public DateTime CheckOutDate { get; set; }
    public int Nights { get; set; }
    public int RoomCount { get; set; }
    public string RoomSummary { get; set; } = "";     // "Deluxe ×2 · Suite ×1"
    public string? UnitNumbers { get; set; }          // "201, 202"
    public decimal TotalAmount { get; set; }
    public decimal FolioTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal BalanceDue { get; set; }
    public decimal DepositRequired { get; set; }
    public DateTime? HoldExpiresAt { get; set; }
    public bool HasSlip { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record LodgingCancelRequest(string? Reason = null);

// ───────────────────────────── 3. หลังบ้าน ─────────────────────────────

/// <summary>ยืนยันการจอง + บันทึกรับมัดจำ (ถ้ามี) → ออกใบเสร็จมัดจำ (IsDeposit)</summary>
public record LodgingConfirmRequest(
    /// <summary>ยอดมัดจำที่รับจริง (null = ตาม DepositRequired · 0 = ยืนยันโดยไม่รับเงิน)</summary>
    decimal? DepositAmount = null,
    PaymentMethod PaymentMethod = PaymentMethod.BankTransfer,
    string? PaymentReference = null,
    DateTime? PaymentDate = null,
    Guid? BankAccountId = null,
    string? Note = null);

public record LodgingAssignUnitRequest(Guid ReservationRoomId, Guid? UnitId);

public record LodgingCheckInRequest(
    /// <summary>จับคู่ห้องที่จอง → หมายเลขห้องจริง (ถ้ายังไม่ได้ assign ทุกห้องต้องส่งมาให้ครบ)</summary>
    List<LodgingAssignUnitRequest>? Assignments = null,
    string? GuestIdNumber = null,
    string? GuestNationality = null,
    string? Note = null);

public record LodgingAddChargeRequest(
    string Description, decimal Quantity, decimal UnitPrice,
    Guid? ProductId = null, decimal? VatRate = null,
    LodgingChargeSource Source = LodgingChargeSource.Manual,
    string? Notes = null);

/// <summary>เช็คเอาต์ = ออกใบกำกับ/ใบแจ้งหนี้สุดท้าย (ตัดมัดจำ) + รับชำระส่วนที่เหลือ</summary>
public record LodgingCheckOutRequest(
    PaymentMethod PaymentMethod = PaymentMethod.Cash,
    string? PaymentReference = null,
    Guid? BankAccountId = null,
    /// <summary>ค่าเสียหาย/ของหาย (TakeTime Checkout_History.DamageCharge) — เพิ่มเป็น folio charge ก่อนออกบิล</summary>
    decimal? DamageCharge = null,
    string? DamageDescription = null,
    /// <summary>false = ออกใบแจ้งหนี้แต่ยังไม่รับเงิน (ลูกค้าองค์กร/agent เครดิต)</summary>
    bool CollectBalanceNow = true,
    /// <summary>ออกใบกำกับภาษีในนามบริษัทของแขก (ใช้ GuestTaxId/GuestCompanyName)</summary>
    bool IssueTaxInvoiceToCompany = false,
    string? Note = null);

public record LodgingRescheduleRequest(DateTime CheckIn, DateTime CheckOut, string? Reason = null);

public record LodgingUpdateReservationRequest(
    string? GuestName = null, string? GuestEmail = null, string? GuestPhone = null,
    string? GuestNationality = null, string? GuestIdNumber = null, string? GuestAddress = null,
    string? GuestTaxId = null, string? GuestCompanyName = null,
    string? ArrivalTime = null, string? SpecialRequests = null, string? InternalNotes = null,
    int? Infants = null, string? SourceReference = null);

public class LodgingHousekeepingTaskDto
{
    public Guid? Id { get; set; }
    public Guid PropertyId { get; set; }
    public Guid UnitId { get; set; }
    public string? UnitNumber { get; set; }
    public string? RoomTypeName { get; set; }
    public Guid? ReservationId { get; set; }
    public string? ReservationNumber { get; set; }
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
    public DateTime CreatedAt { get; set; }
}

public record LodgingTaskStatusRequest(LodgingTaskStatus Status, string? Note = null, Guid? AssignedEmployeeId = null, string? AssignedToName = null);

public record LodgingUnitStatusRequest(LodgingHousekeepingStatus Status, bool? IsOutOfService = null, DateTime? OutOfServiceUntil = null, string? Note = null);

public class LodgingGuestRequestDto
{
    public Guid? Id { get; set; }
    public Guid ReservationId { get; set; }
    public string? ReservationNumber { get; set; }
    public string? GuestName { get; set; }
    public string? UnitNumbers { get; set; }
    public LodgingGuestRequestType RequestType { get; set; } = LodgingGuestRequestType.Other;
    public string Details { get; set; } = "";
    public LodgingTaskStatus Status { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public string? ResponseNote { get; set; }
    public DateTime CreatedAt { get; set; }
}

public record LodgingGuestRequestCreate(LodgingGuestRequestType RequestType, string Details);
public record LodgingGuestRequestResolve(LodgingTaskStatus Status, string? ResponseNote = null);

/// <summary>แดชบอร์ดประจำวันของที่พัก (front desk)</summary>
public class LodgingDashboard
{
    public DateTime Date { get; set; }
    public int TotalUnits { get; set; }
    public int OccupiedUnits { get; set; }
    public decimal OccupancyPercent { get; set; }
    public int ArrivalsToday { get; set; }
    public int DeparturesToday { get; set; }
    public int InHouse { get; set; }
    public int PendingReservations { get; set; }
    public int PendingSlips { get; set; }
    public int DirtyUnits { get; set; }
    public int OutOfServiceUnits { get; set; }
    public int OpenHousekeepingTasks { get; set; }
    public int OpenGuestRequests { get; set; }
    public decimal RevenueMonthToDate { get; set; }
    public List<LodgingReservationListItem> Arrivals { get; set; } = new();
    public List<LodgingReservationListItem> Departures { get; set; } = new();
    public List<LodgingUnitBoardItem> RoomBoard { get; set; } = new();
}

public class LodgingUnitBoardItem
{
    public Guid UnitId { get; set; }
    public string Number { get; set; } = "";
    public string? Floor { get; set; }
    public string RoomTypeName { get; set; } = "";
    public LodgingHousekeepingStatus HousekeepingStatus { get; set; }
    public bool IsOutOfService { get; set; }
    public Guid? CurrentReservationId { get; set; }
    public string? CurrentReservationNumber { get; set; }
    public string? CurrentGuestName { get; set; }
    public DateTime? CurrentCheckOut { get; set; }
    public Guid? ArrivingReservationId { get; set; }
    public string? ArrivingGuestName { get; set; }
}

/// <summary>ปฏิทินห้อง (tape chart) — แถวต่อห้องจริง + แท่งการจองที่ทับช่วง</summary>
public class LodgingCalendar
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public List<LodgingCalendarRow> Rows { get; set; } = new();
    /// <summary>การจองที่ยังไม่ assign ห้อง (แสดงเป็นแถว "รอจัดห้อง" ต่อประเภทห้อง)</summary>
    public List<LodgingCalendarBar> Unassigned { get; set; } = new();
    public List<LodgingCalendarAvailability> Availability { get; set; } = new();
}

public class LodgingCalendarRow
{
    public Guid UnitId { get; set; }
    public string Number { get; set; } = "";
    public Guid RoomTypeId { get; set; }
    public string RoomTypeName { get; set; } = "";
    public LodgingHousekeepingStatus HousekeepingStatus { get; set; }
    public bool IsOutOfService { get; set; }
    public List<LodgingCalendarBar> Bars { get; set; } = new();
}

public class LodgingCalendarBar
{
    public Guid ReservationId { get; set; }
    public Guid ReservationRoomId { get; set; }
    public string ReservationNumber { get; set; } = "";
    public string GuestName { get; set; } = "";
    public Guid RoomTypeId { get; set; }
    public string RoomTypeName { get; set; } = "";
    public DateTime CheckIn { get; set; }
    public DateTime CheckOut { get; set; }
    public LodgingReservationStatus Status { get; set; }
    public Guid? UnitId { get; set; }
}

public class LodgingCalendarAvailability
{
    public Guid RoomTypeId { get; set; }
    public string RoomTypeName { get; set; } = "";
    public int TotalUnits { get; set; }
    /// <summary>ต่อวัน: ว่างกี่ห้อง + ราคาฐานวันนั้น + stop-sell</summary>
    public List<LodgingCalendarDay> Days { get; set; } = new();
}

public record LodgingCalendarDay(DateTime Date, int Available, decimal Rate, bool StopSell, int? Allotment);
