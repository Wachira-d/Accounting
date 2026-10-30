using Accounting.Models.DTOs.Cms;
using Accounting.Models.DTOs;

namespace Accounting.Services.Interfaces;

public interface ICmsBookingService
{
    // Booking Services
    Task<BookingServiceResponse> CreateServiceAsync(Guid companyId, Guid siteId, CreateBookingServiceRequest request, string userId);
    Task<BookingServiceResponse> UpdateServiceAsync(Guid companyId, Guid siteId, Guid serviceId, UpdateBookingServiceRequest request, string userId);
    Task<BookingServiceResponse?> GetServiceAsync(Guid companyId, Guid siteId, Guid serviceId);
    Task<List<BookingServiceResponse>> GetServicesAsync(Guid companyId, Guid siteId, bool includeInactive = false);
    /// <summary>แม่แบบบริการตามประเภทธุรกิจ (สปา · คลินิก · สนาม · ห้องประชุม · รถเช่า …) — ตัวตั้ง <c>CmsBookingServiceSeeder.Presets</c></summary>
    IReadOnlyList<BookingServicePresetResponse> GetPresets();

    // ค่าตั้งการจองระดับไซต์ (บริการที่ "ใช้ค่าของไซต์" รับระยะเวลา/buffer/ล่วงหน้าจากที่นี่)
    Task<BookingSiteSettingsDto> GetSiteSettingsAsync(Guid companyId, Guid siteId);
    Task<BookingSiteSettingsDto> UpdateSiteSettingsAsync(Guid companyId, Guid siteId, BookingSiteSettingsDto request, string userId);

    // ผู้ให้บริการ / ห้อง / อุปกรณ์ (ตารางงานรายบุคคล + บริการที่รับได้)
    Task<List<BookingResourceResponse>> GetResourcesAsync(Guid companyId, Guid siteId);
    Task<BookingResourceResponse> SaveResourceAsync(Guid companyId, Guid siteId, Guid? resourceId, BookingResourceRequest request, string userId);
    Task<bool> DeleteResourceAsync(Guid companyId, Guid siteId, Guid resourceId);
    Task<bool> DeleteServiceAsync(Guid companyId, Guid siteId, Guid serviceId);

    // Booking Slots
    Task<BookingSlotResponse> CreateSlotAsync(Guid companyId, Guid siteId, Guid serviceId, CreateBookingSlotRequest request, string userId);
    Task<List<BookingSlotResponse>> GetSlotsAsync(Guid companyId, Guid siteId, Guid serviceId);
    Task<bool> DeleteSlotAsync(Guid companyId, Guid siteId, Guid serviceId, Guid slotId);

    // Available Slots (public query)
    Task<List<AvailableSlotResponse>> GetAvailableSlotsAsync(Guid companyId, Guid siteId, AvailableSlotsRequest request);

    // Bookings
    /// <param name="staffOnBehalf">รอบ 210: ผู้เรียกเป็นพนักงานที่มีสิทธิ์จัดการการจอง (controller ตัดสินจากสิทธิ์) — ผ่านด่าน "ต้องเป็นสมาชิก" (จองแทนลูกค้า)</param>
    Task<BookingResponse> CreateBookingAsync(Guid companyId, Guid siteId, CreateBookingRequest request, string? userId = null, bool staffOnBehalf = false);
    Task<BookingResponse> UpdateBookingStatusAsync(Guid companyId, Guid siteId, Guid bookingId, UpdateBookingStatusRequest request, string userId);
    Task<BookingResponse?> GetBookingAsync(Guid companyId, Guid siteId, Guid bookingId);
    /// <summary>พนักงานยืนยัน "รับเงินแล้ว" ⇒ ออกใบกำกับภาษีของการจอง Guaranteed (คำตัดสินข้อ 155 · <c>BookingInvoiceTimingPolicy</c>)</summary>
    Task<BookingResponse> IssueTaxInvoiceOnPaymentAsync(Guid companyId, Guid siteId, Guid bookingId, DateTime? receivedDate, string userId);
    Task<PagedResponse<BookingListResponse>> GetBookingsAsync(Guid companyId, Guid siteId, string? status = null, Guid? serviceId = null, DateTime? fromDate = null, DateTime? toDate = null, int page = 1, int pageSize = 20, string? search = null, Guid? resourceId = null, string? sort = null);
    /// <summary>รอบ 205 W3 R-16: พนักงานเลือก/เปลี่ยนผู้ให้บริการ (null = ระบบจัดให้ใหม่) — ตัดสินผ่าน <c>BookingSlotEngine.Decide</c> ตัวเดียวใต้ล็อกไซต์</summary>
    Task<BookingResponse> AssignResourceAsync(Guid companyId, Guid siteId, Guid bookingId, Guid? resourceId, string userId);
    /// <summary>รอบ 205 W3 R-13: แจ้งลูกค้าที่ได้คิวจากคิวสำรอง (เรียกซ้ำได้ — ใบที่แจ้งถึงแล้วถูกข้าม) · คืนข้อความผลการส่ง · <c>BookingReminderJob</c> เรียกซ้ำให้ใบที่ส่งไม่ถึง</summary>
    Task<List<string>> NotifyWaitlistPromotedAsync(Guid companyId, Guid siteId, IReadOnlyCollection<Guid> bookingIds, CancellationToken ct = default);
    /// <summary>R-45 มุมมองทุกไซต์ — การจองของเว็บใน <paramref name="siteIds"/> (ผู้เรียกกรองสิทธิ์ด้วย <c>Helpers/CmsSiteScope</c> มาแล้ว · ว่าง = ไม่มีแถว) · แบ่งหน้าจริง</summary>
    Task<PagedResponse<BookingListResponse>> GetBookingsAcrossSitesAsync(Guid companyId, IReadOnlyCollection<Guid> siteIds, string? status, DateTime? fromDate, DateTime? toDate, string? search, int page, int pageSize);

    // ── รอบ 205 W2 (R-08 R-10 R-11): เส้นเงินการจองนัดหมาย — CmsBookingService.Payment.cs ──
    /// <summary>บันทึกรับเงินมัดจำ (พนักงาน หรือ gateway ผ่าน SiteBookingPaymentHandler) ⇒ PaidAmount/อ้างอิง · ครบมัดจำ = ยืนยันการจอง + อนุมัติใบเสร็จมัดจำลงวันที่รับเงิน · กันซ้ำด้วยเลขอ้างอิง</summary>
    Task<BookingResponse> RecordDepositPaymentAsync(Guid companyId, Guid siteId, Guid bookingId, RecordBookingPaymentRequest request, string actor,
        Guid? moneyInAccountId = null, bool fromOnlinePayment = false, CancellationToken ct = default);
    /// <summary>มุมมองลูกค้าผ่านโทเคน (ไม่มีหมายเหตุภายใน) · null = โทเคนผิด</summary>
    Task<BookingResponse?> GetBookingByTokenAsync(Guid companyId, Guid siteId, string token);
    /// <summary>ลูกค้ายกเลิกเองผ่านโทเคน — ค่าธรรมเนียมตามนโยบาย (BookingCancellationFee · เพดาน = มัดจำ ข้อ 126)</summary>
    Task<BookingResponse> CancelByCustomerAsync(Guid companyId, Guid siteId, string token, CustomerCancelBookingRequest request);
    /// <summary>รอบ 206: เลื่อนนัด (แถวเดิม · ตัดสินรอบใหม่ด้วย BookingSlotEngine.Decide ไม่นับตัวเอง · เอกสาร/เงินคงผูก) — CmsBookingService.Reschedule.cs</summary>
    Task<BookingResponse> RescheduleAsync(Guid companyId, Guid siteId, Guid bookingId, RescheduleBookingRequest request, string userId);
    Task<BookingResponse> RescheduleByCustomerAsync(Guid companyId, Guid siteId, string token, RescheduleBookingRequest request);

    // ── รอบ 207: หน้าทำงานผู้ให้บริการ — CmsBookingService.Workflow.cs ──
    /// <summary>ผู้ให้บริการที่เลือกได้ตอนเริ่มให้บริการ (รับบริการนี้ได้ · ว่าง/ไม่ว่าง + เหตุ · เรียงตามตรรกะแนะนำของไซต์)</summary>
    Task<BookingProviderCandidatesResponse> GetProviderCandidatesAsync(Guid companyId, Guid siteId, Guid bookingId);
    /// <summary>เริ่มให้บริการ + ผู้ให้บริการ (หลายคน · แบ่ง %) — ตรวจว่างใต้ล็อกไซต์</summary>
    Task<BookingResponse> StartServiceAsync(Guid companyId, Guid siteId, Guid bookingId, StartBookingServiceRequest request, string userId);
    /// <summary>แก้ผู้ให้บริการ/สัดส่วนหลังเริ่ม</summary>
    Task<BookingResponse> SetAssignmentsAsync(Guid companyId, Guid siteId, Guid bookingId, StartBookingServiceRequest request, string userId);
    /// <summary>เสร็จสิ้น (+ รับชำระ: วิธี/บัญชีรับเงิน/ยอด) — เอกสารเดินเส้นเดิมตามค่าตั้งไซต์ + สถานะ VAT</summary>
    Task<BookingResponse> CompleteBookingAsync(Guid companyId, Guid siteId, Guid bookingId, CompleteBookingRequest request, string userId);
    /// <summary>ผูก/สร้าง/เลิกผูกสินค้า/บริการในระบบของบริการจอง (CmsBookingService.Products.cs)</summary>
    Task<BookingServiceResponse> LinkProductAsync(Guid companyId, Guid siteId, Guid serviceId, LinkBookingProductRequest request, string userId);
    /// <summary>สรุปการทำงาน/รายงานผู้ให้บริการของช่วงวันที่</summary>
    Task<BookingProviderReportResponse> GetProviderReportAsync(Guid companyId, Guid siteId, DateTime from, DateTime to);

    // ── รอบ 209 P4: ตารางคลาส (เทอม → รอบเรียน) — CmsBookingService.Classes.cs ──
    Task<List<ClassTermResponse>> GetClassTermsAsync(Guid companyId, Guid siteId, Guid? serviceId);
    Task<ClassTermResponse> SaveClassTermAsync(Guid companyId, Guid siteId, Guid? termId, SaveClassTermRequest request, string userId);
    Task<List<string>> DeleteClassTermAsync(Guid companyId, Guid siteId, Guid termId, string userId);
    Task<ClassGenerateResponse> GenerateClassSessionsAsync(Guid companyId, Guid siteId, Guid termId, GenerateClassSessionsRequest request, string userId);
    Task<List<ClassSessionResponse>> GetClassSessionsAsync(Guid companyId, Guid siteId, DateOnly from, DateOnly to, Guid? serviceId);
    Task<ClassSessionDetailResponse> GetClassSessionAsync(Guid companyId, Guid siteId, Guid sessionId);
    Task<ClassActionResponse> UpdateClassSessionAsync(Guid companyId, Guid siteId, Guid sessionId, UpdateClassSessionRequest request, string userId);
    Task<ClassActionResponse> SetClassSubstituteAsync(Guid companyId, Guid siteId, Guid sessionId, ClassSubstituteRequest request, string userId);
    Task<ClassActionResponse> CancelClassSessionAsync(Guid companyId, Guid siteId, Guid sessionId, ClassCancelRequest request, string userId);
    Task<ClassActionResponse> ReopenClassSessionAsync(Guid companyId, Guid siteId, Guid sessionId, string userId);
    Task<ClassActionResponse> CheckInClassAsync(Guid companyId, Guid siteId, Guid sessionId, ClassCheckInRequest request, string userId);

    // ERP Sync (event-driven)
    Task<Guid?> SyncBookingToErpAsync(Guid companyId, Guid siteId, Guid bookingId);

    /// <summary>รอบ 213: ส่งซ้ำข้อความถึงลูกค้าที่ค้างเพราะโควตา (ยืนยัน/ยกเลิก/เลื่อนนัด) — ประกอบใหม่จากสถานะปัจจุบันผ่านเส้นเดิม · ผู้เรียกเดียว <c>CustomerNotifyResendService</c></summary>
    Task<string?> ResendCustomerNotifyAsync(Guid companyId, Guid bookingId, string eventKey, string messageKey);

    // ── รอบ 218 (คำตัดสินข้อ 177): ชำระล่วงหน้า (ตัวตัดสิน Helpers/BookingPaymentPolicy) ──
    Task<BookingPaymentSettingsDto> GetPaymentSettingsAsync(Guid companyId, Guid siteId);
    Task<BookingPaymentSettingsDto> UpdatePaymentSettingsAsync(Guid companyId, Guid siteId, BookingPaymentSettingsDto request, string userId);
    /// <summary>ก่อนกดจอง: ยอด/บังคับไหม/กำหนดเวลา/ช่องทาง · null = ไม่พบบริการ</summary>
    Task<BookingPaymentPreviewResponse?> PreviewPaymentAsync(Guid companyId, Guid siteId, BookingPaymentPreviewRequest request);
    /// <summary>สถานะสลิปบนการจอง — ผู้เรียกเดียว <c>BookingSlipService</c> (ไฟล์ + PaymentIntent สร้างแล้ว)</summary>
    Task<BookingResponse> MarkSlipUploadedAsync(Guid companyId, Guid siteId, Guid bookingId, Guid attachmentId, Guid intentId, decimal amount);
    Task<BookingResponse> MarkSlipRejectedAsync(Guid companyId, Guid siteId, Guid bookingId, Guid expectedIntentId, string reason, string actor);
    /// <summary>รอบ 217 (ข้อ 175 ง): ส่งแจ้งเตือน "การจองใหม่" (ลูกค้า + ร้าน) — เรียกจาก <c>DeferredNotifyDispatcher</c> เท่านั้น (หลังคำขอจองตอบไปแล้ว)</summary>
    /// <returns>null = ส่งแล้ว (ผลรายผู้รับประทับบนรายการ) · ข้อความ = ข้ามเพราะสถานะ (ยกเลิก ฯลฯ — ฝ่ายค้าน S4)</returns>
    Task<string?> DispatchCreatedNotifyAsync(Guid companyId, Guid siteId, Guid bookingId);
}
