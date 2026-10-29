using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.CourseCredit;

// ===== แพ็กเกจ =====
public record SaveCreditPackageRequest(
    string Code,
    string Name,
    string? Description,
    CreditUnit Unit,
    decimal CreditQuantity,
    int ValidityDays,
    decimal Price,
    bool PriceIncludesVat,
    decimal VatRate,
    CreditClassFormat ClassFormat,
    TrainerAssignmentMode TrainerMode,
    List<Guid>? AllowedServiceIds,
    List<Guid>? TrainerEmployeeIds,
    int FreeCancelHours,
    Guid? UnearnedAccountId,
    Guid? RevenueAccountId,
    bool IsActive = true,
    int SortOrder = 0,
    // รอบ 209 P5 (R-209-C5) — ไม่ส่ง = พฤติกรรมเดิม (แพ็กเกจนับครั้ง · ไม่อนุญาตพัก · ไม่แจ้งเตือน)
    CreditPackageKind Kind = CreditPackageKind.Credits,
    int MaxBookingsPerDay = 1,
    int MaxActiveBookings = 0,
    int MaxFreezeDays = 0,
    int ExpiryReminderDays = 0);

public record CreditPackageResponse(
    Guid Id, string Code, string Name, string? Description,
    CreditUnit Unit, string UnitLabel, decimal CreditQuantity, int ValidityDays,
    decimal Price, bool PriceIncludesVat, decimal VatRate, string VatLabel,
    CreditClassFormat ClassFormat, string ClassFormatLabel,
    TrainerAssignmentMode TrainerMode, string TrainerModeLabel,
    List<Guid> AllowedServiceIds, List<Guid> TrainerEmployeeIds,
    int FreeCancelHours,
    Guid? UnearnedAccountId, string? UnearnedAccountLabel,
    Guid? RevenueAccountId, string? RevenueAccountLabel,
    bool IsActive, int SortOrder, int ActivePassCount,
    // รอบ 209 P5 — echo กลับครบ (กฎ #4 A "เก็บแล้วต้อง echo")
    CreditPackageKind Kind = CreditPackageKind.Credits, string? KindLabel = null,
    int MaxBookingsPerDay = 1, int MaxActiveBookings = 0, int MaxFreezeDays = 0, int ExpiryReminderDays = 0);

// ===== ขาย / เปิดบัตร =====
/// <summary>ขายคอร์ส — ออกใบกำกับภาษี/ใบเสร็จ (ขายเงินสด) ผ่าน DocumentService ตัวเดิม · บรรทัดลงผัง "ค่าบริการรับล่วงหน้า"</summary>
public record SellCreditPassRequest(
    Guid PackageId,
    Guid ContactId,
    DateTime? SaleDate,
    Guid? BankAccountId,
    Guid? PaymentAccountId,
    string? Note,
    // คีย์กันกดซ้ำของหน้าจอ (สร้างใหม่ทุกครั้งที่เปิดหน้าขาย) — ส่งซ้ำด้วยคีย์เดิม = ได้บัตร/ใบเดิม ไม่ออกใบใหม่
    Guid? ClientRequestId = null);

/// <summary>เปิดบัตรจากเอกสารขายที่ออกแล้ว (ช่องทางอื่น / ทางกู้เมื่อเปิดบัตรหลังออกใบไม่สำเร็จ) — ใบต้องมีบรรทัดลงผังหนี้สินรายได้รับล่วงหน้า</summary>
/// <param name="Channel">ช่องทางขายที่ประทับบนบัตร · null = เปิดจากใบเดิม (ExistingDocument) · ร้านออนไลน์ส่ง Online (R-41 B2)</param>
public record ActivateCreditPassRequest(Guid PackageId, Guid DocumentId, string? Note, CreditSaleChannel? Channel = null);

public record CreditPassResponse(
    Guid Id, string PassNumber, Guid PackageId, string PackageName,
    Guid ContactId, string? ContactName,
    CreditUnit Unit, string UnitLabel,
    DateTime PurchasedAt, DateTime? ExpiresOn,
    decimal CreditsTotal, decimal CreditsUsed, decimal CreditsReserved, decimal CreditsAvailable,
    decimal ContractAmount, decimal RecognizedAmount, decimal DeferredBalance, decimal PricePerCredit,
    CreditPassStatus Status, string StatusLabel,
    Guid? SaleDocumentId, string? SaleDocumentNumber,
    // Warning: เลยวันหมดอายุแล้วแต่ยังปิดไม่ได้ (มีการจองค้าง) — ป้ายเตือนบนจอ
    string? Warning,
    // รอบ 209 P5 (R-209-C5) — สมาชิกไม่จำกัด / พัก / โอน / แจ้งเตือนหมดอายุ
    CreditPackageKind Kind = CreditPackageKind.Credits,
    int PeriodDays = 0, DateTime? PeriodStartsOn = null, DateTime? RecognizedThroughDate = null, decimal PeriodDailyAmount = 0m,
    int FrozenDays = 0, int MaxFreezeDays = 0, int MaxBookingsPerDay = 1, int MaxActiveBookings = 0,
    Guid? TransferredFromPassId = null, Guid? TransferredToPassId = null,
    DateTime? ExpiryNotifiedAt = null, string? ExpiryNotifyError = null);

public record CreditLedgerEntryResponse(
    Guid Id, CreditLedgerKind Kind, string KindLabel, decimal Credits, decimal RecognizedAmount,
    Guid? BookingId, Guid? JournalEntryId, string? JournalEntryNumber,
    DateTime OccurredAt, string? Note, string? CreatedBy);

public record CreditPassDetailResponse(
    CreditPassResponse Pass,
    List<CreditLedgerEntryResponse> Ledger,
    List<CreditBookingResponse> Bookings,
    List<CreditFreezeResponse>? Freezes = null);

// ===== รอบ 209 P5 — พักบัตร / ขยายอายุ / โอน (คำตัดสินข้อ 168 ฎ) =====
/// <summary>พักบัตร — ช่วงวันรวมหัวท้าย (วันปฏิทินไทย) · เหตุผลบังคับ</summary>
public record FreezeCreditPassRequest(DateTime FromDate, DateTime ToDate, string? Reason);
/// <summary>ขยายอายุบัตร (ไม่มีเงิน · audit) — จำนวนวัน + เหตุผลบังคับ</summary>
public record ExtendCreditPassRequest(int Days, string? Reason);
/// <summary>โอนบัตรให้ลูกค้าคนอื่น — ปิดบัตรเดิม/เปิดบัตรใหม่ยอดเท่าเดิม · ไม่ออกใบกำกับภาษีใหม่ (โอนภายใน) · เหตุผลบังคับ</summary>
public record TransferCreditPassRequest(Guid ToContactId, string? Reason);
public record CreditFreezeResponse(Guid Id, DateTime FromDate, DateTime ToDate, int Days, string Reason, DateTime? CancelledAt, string? CreatedBy);

/// <summary>ผลงานรับรู้รายได้สมาชิกไม่จำกัด (รายคืน/ปุ่ม)</summary>
public record CreditPeriodRunResult(int Passes, int Failed, decimal Recognized, List<string> Messages);
/// <summary>ผลแจ้งเตือนบัตรใกล้หมดอายุ — Sent นับเฉพาะที่ช่องทางตอบสำเร็จจริง</summary>
public record CreditReminderRunResult(int Sent, int Failed, int Due, List<string> Messages);

// ===== คืนเงินจากบัตร (R-38 · รอบ 205 W6) =====
/// <summary>ระบุอย่างใดอย่างหนึ่ง: ยอดบาทสุทธิก่อน VAT หรือจำนวนเครดิตที่คืน (ระบบแปลงเป็นบาทด้วยราคาต่อเครดิต · คืนเครดิตที่เหลือทั้งหมด = ยอดส่วนที่เหลือ ⇒ ปิดบัตร)</summary>
public record DraftCreditRefundRequest(decimal? AmountNet, decimal? Credits, string? Note);

public record CreditRefundDraftResponse(Guid DocumentId, string DocumentNumber, decimal AmountNet, decimal CreditsToRemove,
    decimal CreditsRemaining, bool ClosesPass, string Message);

public record CreditRefundPreviewResponse(decimal Deferred, decimal PricePerCredit, decimal MaxPartialCredits, decimal MaxPartialAmount, bool HasReservations);

public record CreditSummaryResponse(
    int ActivePasses, decimal TotalDeferredBalance, int ExpiringWithin7Days, int PendingExpiry);

// ===== รอบเรียน / การจอง =====
public record SaveCreditSessionRequest(
    string Title,
    Guid? BookingServiceId,
    CreditClassFormat Format,
    DateTime StartAt,
    DateTime EndAt,
    int Capacity,
    Guid? TrainerEmployeeId,
    decimal CreditCost,
    string? Notes);

public record CreditSessionResponse(
    Guid Id, string Title, Guid? BookingServiceId,
    CreditClassFormat Format, string FormatLabel,
    DateTime StartAt, DateTime EndAt, int Capacity, int BookedCount,
    Guid? TrainerEmployeeId, string? TrainerName, decimal CreditCost,
    CreditSessionStatus Status, string StatusLabel, string? Notes);

/// <summary>จองด้วยเครดิต — ใส่ <c>SessionId</c> (คลาสกลุ่ม/รอบที่ตั้งไว้) หรือ เว้นว่างแล้วใส่เวลา ⇒ สร้างรอบตัวต่อตัวให้
/// (ครูฝึก: ลูกค้าเลือก = ส่ง <c>TrainerEmployeeId</c> · ระบบกำหนด = เว้นว่างตามโหมดของแพ็กเกจ)</summary>
public record BookWithCreditRequest(
    Guid PassId,
    Guid? SessionId,
    DateTime? StartAt,
    DateTime? EndAt,
    Guid? BookingServiceId,
    Guid? TrainerEmployeeId,
    string? Note);

public record SettleCreditBookingRequest(bool Attended, string? Note);
public record CancelCreditBookingRequest(bool ByBusiness, string? Note);

public record CreditBookingResponse(
    Guid Id, Guid? SessionId, string? SessionTitle, string? ExternalRef,
    Guid PassId, string? PassNumber, Guid ContactId, string? ContactName,
    Guid? TrainerEmployeeId, string? TrainerName,
    DateTime StartAt, decimal Credits,
    CreditBookingStatus Status, string StatusLabel,
    decimal RecognizedAmount, DateTime? SettledAt, string? Note);

public record CreditTrainerOption(Guid Id, string Name);

public record CreditExpiryRunResult(int Expired, int SkippedWithReservations, int Failed, decimal BreakageRecognized, List<string> Messages);

// ===== สัญญาบางกับเครื่องจองภายนอก (ICreditBookingLedger) =====
/// <summary>กันเครดิตให้การจองจากเครื่องจองภายนอก (เช่น <c>CmsBookingService</c>) — <c>ExternalRef</c> ไม่ซ้ำต่อบริษัท ·
/// <c>PassId</c> ว่าง ⇒ เลือกบัตรที่หมดอายุก่อน (FEFO) ที่ใช้กับบริการนี้ได้</summary>
public record CreditHoldRequest(
    Guid ContactId,
    Guid? PassId,
    Guid? BookingServiceId,
    string ExternalRef,
    DateTime StartAt,
    int DurationMinutes,
    CreditClassFormat Format,
    decimal? Credits = null,
    int Seats = 1);   // รอบ 209: จำนวนที่นั่งของการจอง (CMS Units) ⇒ เครดิต × ที่นั่ง เมื่อไม่ระบุ Credits

public record CreditHoldResult(Guid BookingId, Guid PassId, string PassNumber, decimal Credits, decimal CreditsAvailableAfter);
