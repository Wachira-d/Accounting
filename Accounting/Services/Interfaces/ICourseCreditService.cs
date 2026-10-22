using Accounting.Models.DTOs;
using Accounting.Models.DTOs.CourseCredit;

namespace Accounting.Services.Interfaces;

/// <summary>คอร์ส / Class Credits / Hour Pass (ทีม L · <c>docs/COURSE_CREDIT_DESIGN.md</c>) — นิยามแพ็กเกจ · ขาย · ยอดคงเหลือ ·
/// จอง/เข้าเรียน/ยกเลิก · หมดอายุ · ตัวเลขทั้งหมดจาก <c>Helpers/CourseCreditMath</c> · JE ผ่าน <c>JournalEntryBuilder</c> ·
/// เอกสารขายผ่าน <c>IDocumentService</c> (ไม่มี posting layer ของตัวเอง)</summary>
public interface ICourseCreditService
{
    Task<List<CreditPackageResponse>> GetPackagesAsync(Guid companyId, bool includeInactive, CancellationToken ct = default);
    Task<CreditPackageResponse> CreatePackageAsync(Guid companyId, SaveCreditPackageRequest request, string actor, CancellationToken ct = default);
    Task<CreditPackageResponse> UpdatePackageAsync(Guid companyId, Guid packageId, SaveCreditPackageRequest request, string actor, CancellationToken ct = default);

    Task<CreditPassResponse> SellAsync(Guid companyId, SellCreditPassRequest request, string actor, CancellationToken ct = default);
    Task<CreditPassResponse> ActivateFromDocumentAsync(Guid companyId, ActivateCreditPassRequest request, string actor, CancellationToken ct = default);
    Task<PagedResponse<CreditPassResponse>> GetPassesAsync(Guid companyId, Guid? contactId, string? status, string? search, int page, int pageSize, CancellationToken ct = default);
    Task<CreditPassDetailResponse> GetPassAsync(Guid companyId, Guid passId, CancellationToken ct = default);
    Task<CreditSummaryResponse> GetSummaryAsync(Guid companyId, CancellationToken ct = default);

    Task<List<CreditSessionResponse>> GetSessionsAsync(Guid companyId, DateTime? from, DateTime? to, CancellationToken ct = default);
    Task<CreditSessionResponse> CreateSessionAsync(Guid companyId, SaveCreditSessionRequest request, string actor, CancellationToken ct = default);
    Task<CreditSessionResponse> CancelSessionAsync(Guid companyId, Guid sessionId, string actor, CancellationToken ct = default);
    Task<List<CreditBookingResponse>> GetSessionBookingsAsync(Guid companyId, Guid sessionId, CancellationToken ct = default);
    Task<List<CreditTrainerOption>> GetTrainersAsync(Guid companyId, CancellationToken ct = default);
    /// <summary>บริการของเครื่องจอง (<c>SiteBookingService</c>) ทุกเว็บของบริษัท — ตัวเลือก "บริการที่ใช้ได้" ของแพ็กเกจ</summary>
    Task<List<CreditTrainerOption>> GetBookableServicesAsync(Guid companyId, CancellationToken ct = default);

    Task<CreditBookingResponse> BookAsync(Guid companyId, BookWithCreditRequest request, string actor, CancellationToken ct = default);
    Task<CreditBookingResponse> SettleBookingAsync(Guid companyId, Guid bookingId, SettleCreditBookingRequest request, string actor, CancellationToken ct = default);
    Task<CreditBookingResponse> CancelBookingAsync(Guid companyId, Guid bookingId, CancelCreditBookingRequest request, string actor, CancellationToken ct = default);

    /// <summary>ปิดบัตรที่หมดอายุ + รับรู้ส่วนคงเหลือเป็นรายได้ (breakage) — บัตรเดียว (ปุ่มบนจอ)</summary>
    Task<CreditPassResponse> ExpirePassAsync(Guid companyId, Guid passId, string actor, CancellationToken ct = default);
    /// <summary>ทุกบัตรของบริษัทที่เลยวันหมดอายุ (งานกลางคืน) — บัตรที่มีการจองค้าง ข้าม + นับไว้ในผลลัพธ์</summary>
    Task<CreditExpiryRunResult> ExpireDueAsync(Guid companyId, string actor, CancellationToken ct = default);
}

/// <summary>
/// <b>สัญญาบาง</b>สำหรับเครื่องจองภายนอก (เช่น <c>CmsBookingService</c> ของทีม B) — เครื่องจองเป็นเจ้าของช่องเวลา/ความจุ ·
/// โมดูลเครดิตเป็นเจ้าของยอดเครดิตและการรับรู้รายได้ · อ้างการจองด้วย <c>externalRef</c> (เช่น <c>"SiteBooking:{id}"</c>) ⇒
/// ไม่ต้องแก้ schema ของเครื่องจอง · เรียกซ้ำด้วย ref เดิม = idempotent (คืนผลเดิม)
/// </summary>
public interface ICreditBookingLedger
{
    /// <summary>กันเครดิตตอนจอง (ยังไม่รับรู้รายได้)</summary>
    Task<CreditHoldResult> HoldAsync(Guid companyId, CreditHoldRequest request, string actor, CancellationToken ct = default);
    /// <summary>ยกเลิกการจอง — ลูกค้ายกเลิกตามนโยบาย (ทันเวลา = คืน · ช้า = ริบ/รับรู้รายได้) · <paramref name="byBusiness"/> = ร้านยกเลิก (คืนเสมอ)</summary>
    Task<CreditBookingResponse> ReleaseAsync(Guid companyId, string externalRef, bool byBusiness, string actor, CancellationToken ct = default);
    /// <summary>ปิดการจอง — มาเรียน หรือ no-show (ตัดเครดิต + รับรู้รายได้ทั้งสองกรณี)</summary>
    Task<CreditBookingResponse> SettleAsync(Guid companyId, string externalRef, bool attended, string actor, CancellationToken ct = default);
}
