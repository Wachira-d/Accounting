using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Lodging;
using Accounting.Models.Enums;

namespace Accounting.Services.Interfaces;

/// <summary>โมดูลธุรกิจที่พัก — ตั้งค่า · ค้นหา/เสนอราคา · จอง · มัดจำ · เช็คอิน/เอาต์ ·
/// folio · แม่บ้าน · คำขอแขก · แดชบอร์ด/ปฏิทิน. ทุกเมธอด scope ด้วย companyId (กฎ M)
/// เอกสารบัญชีทุกใบเดินผ่าน IDocumentService (เลข gap-free §86/4 · มัดจำ §78/1)</summary>
public interface ILodgingService
{
    // ── ตั้งค่า: ที่พัก ──
    Task<List<LodgingPropertyDto>> GetPropertiesAsync(Guid companyId);
    Task<LodgingPropertyDto?> GetPropertyAsync(Guid companyId, Guid propertyId);
    Task<LodgingPropertyDto> CreatePropertyAsync(Guid companyId, LodgingPropertyDto dto, string userId);
    Task<LodgingPropertyDto> UpdatePropertyAsync(Guid companyId, Guid propertyId, LodgingPropertyDto dto, string userId);

    // ── ตั้งค่า: ประเภทห้อง / ห้องจริง ──
    Task<List<LodgingRoomTypeDto>> GetRoomTypesAsync(Guid companyId, Guid propertyId, bool includeInactive = false);
    Task<LodgingRoomTypeDto> SaveRoomTypeAsync(Guid companyId, LodgingRoomTypeDto dto, string userId);
    Task<bool> DeleteRoomTypeAsync(Guid companyId, Guid roomTypeId);
    Task<List<LodgingUnitDto>> GetUnitsAsync(Guid companyId, Guid propertyId);
    Task<LodgingUnitDto> SaveUnitAsync(Guid companyId, LodgingUnitDto dto, string userId);
    Task<bool> DeleteUnitAsync(Guid companyId, Guid unitId);
    Task<LodgingUnitDto> SetUnitStatusAsync(Guid companyId, Guid unitId, LodgingUnitStatusRequest request, string userId);

    // ── ตั้งค่า: แผนราคา / ฤดูกาล / ราคารายวัน / นโยบายยกเลิก / บริการเสริม ──
    Task<List<LodgingRatePlanDto>> GetRatePlansAsync(Guid companyId, Guid propertyId);
    Task<LodgingRatePlanDto> SaveRatePlanAsync(Guid companyId, LodgingRatePlanDto dto, string userId);
    Task<bool> DeleteRatePlanAsync(Guid companyId, Guid ratePlanId);
    Task<List<LodgingSeasonDto>> GetSeasonsAsync(Guid companyId, Guid propertyId);
    Task<LodgingSeasonDto> SaveSeasonAsync(Guid companyId, LodgingSeasonDto dto, string userId);
    Task<bool> DeleteSeasonAsync(Guid companyId, Guid seasonId);
    Task<List<LodgingRateOverrideDto>> GetRateOverridesAsync(Guid companyId, Guid propertyId, DateTime from, DateTime to, Guid? roomTypeId = null);
    Task<int> SaveRateOverridesAsync(Guid companyId, LodgingRateOverrideBulkRequest request, string userId);
    Task<List<LodgingCancellationPolicyDto>> GetPoliciesAsync(Guid companyId, Guid propertyId);
    Task<LodgingCancellationPolicyDto> SavePolicyAsync(Guid companyId, LodgingCancellationPolicyDto dto, string userId);
    Task<bool> DeletePolicyAsync(Guid companyId, Guid policyId);
    Task<List<LodgingExtraDto>> GetExtrasAsync(Guid companyId, Guid propertyId, bool includeInactive = false);
    Task<LodgingExtraDto> SaveExtraAsync(Guid companyId, LodgingExtraDto dto, string userId);
    Task<bool> DeleteExtraAsync(Guid companyId, Guid extraId);
    /// <summary>บริการเสริมที่ต้องเลือกวิธีคิดราคา/หมวดใหม่ ทุกที่พักของบริษัท (รอบ 193 #36) — รายงานอย่างเดียว ไม่เดาค่า</summary>
    Task<List<LodgingExtraNeedsReselectItem>> ListExtrasNeedingReselectAsync(Guid companyId);

    // ── หน้าเว็บสาธารณะ (scope ด้วย siteId — ไม่ต้องล็อกอิน) ──
    Task<LodgingPublicInfo?> GetPublicInfoAsync(Guid companyId, Guid siteId);
    Task<Guid?> ResolvePropertyIdForSiteAsync(Guid companyId, Guid siteId);
    Task<List<LodgingSearchResult>> SearchAsync(Guid companyId, Guid propertyId, LodgingSearchRequest request);
    Task<LodgingQuoteResponse> QuoteAsync(Guid companyId, Guid propertyId, LodgingQuoteRequest request);
    Task<LodgingReservationResponse> CreateReservationAsync(Guid companyId, Guid propertyId, LodgingCreateReservationRequest request, LodgingReservationSource source, string actor, Guid? siteId = null);
    Task<LodgingReservationResponse?> GetReservationByTokenAsync(Guid companyId, Guid siteId, string token);
    Task<LodgingReservationResponse?> UploadSlipByTokenAsync(Guid companyId, Guid siteId, string token, IFormFile file, string? reference);

    /// <summary>ปฏิเสธสลิปที่แขกส่งมา (เหตุผลบังคับ) — ล้างสลิปออกให้ส่งใหม่ได้ ·
    /// ต่ออายุ hold · แจ้งแขก · เลือกปิดรับสลิปของใบนี้ได้เมื่อพบสลิปปลอม</summary>
    Task<LodgingReservationResponse?> RejectSlipAsync(
        Guid companyId, Guid reservationId, LodgingRejectSlipRequest request, string userId);
    Task<LodgingReservationResponse?> CancelByTokenAsync(Guid companyId, Guid siteId, string token, LodgingCancelRequest request);
    Task<LodgingGuestRequestDto?> CreateGuestRequestByTokenAsync(Guid companyId, Guid siteId, string token, LodgingGuestRequestCreate request);

    // ── หลังบ้าน: การจอง ──
    Task<PagedResponse<LodgingReservationListItem>> ListReservationsAsync(Guid companyId, Guid? propertyId, string? status, DateTime? from, DateTime? to, string? search, string? view, int page, int pageSize);
    Task<LodgingReservationResponse?> GetReservationAsync(Guid companyId, Guid reservationId);

    /// <summary>พาธไฟล์สลิปบนดิสก์ + content type — <c>null</c> เมื่อไม่มีสลิปหรือหาไฟล์ไม่เจอ
    ///
    /// <para>คืน**พาธ** ไม่ใช่ URL เพราะโฟลเดอร์สลิปไม่ได้เสิร์ฟเป็น static แล้ว
    /// (PII) · ด่านสิทธิ์อยู่ที่ controller: ฝั่งพนักงานใช้ <c>LodgingManage</c>
    /// ฝั่งแขกพิสูจน์ด้วย <c>PublicToken</c> ของตัวเอง</para></summary>
    Task<(string Path, string ContentType)?> GetSlipFileAsync(Guid companyId, Guid reservationId);
    Task<(string Path, string ContentType)?> GetSlipFileByTokenAsync(Guid companyId, Guid siteId, string token);
    Task<LodgingReservationResponse> UpdateReservationAsync(Guid companyId, Guid reservationId, LodgingUpdateReservationRequest request, string userId);
    /// <param name="moneyInAccountId">ผังบัญชีขา "เงินเข้า" ของใบเสร็จมัดจำ — <c>null</c> =
    /// ธนาคาร/เงินสดตามปกติ · มีค่า = บัญชีพัก 11340 (รับผ่าน gateway เงินยังไม่เข้าธนาคาร) ·
    /// ค่านี้มาจาก <c>IGatewayAccountResolver</c> ตัวเดียว — <b>พารามิเตอร์ของเมธอด
    /// ไม่ใช่ช่องใน DTO</b> เหตุผลเดียวกับ originModule</param>
    Task<LodgingReservationResponse> ConfirmAsync(Guid companyId, Guid reservationId,
        LodgingConfirmRequest request, string userId, Guid? moneyInAccountId = null);
    Task<LodgingReservationResponse> AssignUnitAsync(Guid companyId, Guid reservationId, LodgingAssignUnitRequest request, string userId);
    Task<LodgingReservationResponse> CheckInAsync(Guid companyId, Guid reservationId, LodgingCheckInRequest request, string userId);
    Task<LodgingReservationResponse> AddChargeAsync(Guid companyId, Guid reservationId, LodgingAddChargeRequest request, string userId);
    Task<LodgingReservationResponse> CancelChargeAsync(Guid companyId, Guid reservationId, Guid chargeId, string userId);
    Task<LodgingReservationResponse> CheckOutAsync(Guid companyId, Guid reservationId, LodgingCheckOutRequest request, string userId);
    Task<LodgingReservationResponse> CancelAsync(Guid companyId, Guid reservationId, LodgingCancelRequest request, string userId, bool noShow = false);
    /// <summary>ยืนยันว่าโอน/จ่ายคืนแขกแล้วจริง (F-03 รอบ 193) — ลง JE คืนเงิน + ใบลดหนี้ตอนนี้เท่านั้น
    /// (ยกเลิกแค่ตั้ง "ยอดค้างคืน" ไม่แตะเงินสด)</summary>
    Task<LodgingReservationResponse> RecordRefundPaidAsync(Guid companyId, Guid reservationId, LodgingRefundPaidRequest request, string userId);
    Task<LodgingReservationResponse> RescheduleAsync(Guid companyId, Guid reservationId, LodgingRescheduleRequest request, string userId);

    // ── หลังบ้าน: แม่บ้าน / คำขอแขก ──
    Task<List<LodgingHousekeepingTaskDto>> GetTasksAsync(Guid companyId, Guid propertyId, string? status, DateTime? date);
    Task<LodgingHousekeepingTaskDto> CreateTaskAsync(Guid companyId, LodgingHousekeepingTaskDto dto, string userId);
    Task<LodgingHousekeepingTaskDto> UpdateTaskStatusAsync(Guid companyId, Guid taskId, LodgingTaskStatusRequest request, string userId);
    Task<List<LodgingGuestRequestDto>> GetGuestRequestsAsync(Guid companyId, Guid propertyId, bool openOnly);
    Task<LodgingGuestRequestDto> ResolveGuestRequestAsync(Guid companyId, Guid requestId, LodgingGuestRequestResolve request, string userId);

    // ── หลังบ้าน: แดชบอร์ด / ปฏิทิน ──
    Task<LodgingDashboard> GetDashboardAsync(Guid companyId, Guid propertyId, DateTime? date);
    Task<LodgingCalendar> GetCalendarAsync(Guid companyId, Guid propertyId, DateTime from, DateTime to);
}
