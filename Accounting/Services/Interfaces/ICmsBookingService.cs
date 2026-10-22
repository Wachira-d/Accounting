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
    Task<BookingResponse> CreateBookingAsync(Guid companyId, Guid siteId, CreateBookingRequest request, string? userId = null);
    Task<BookingResponse> UpdateBookingStatusAsync(Guid companyId, Guid siteId, Guid bookingId, UpdateBookingStatusRequest request, string userId);
    Task<BookingResponse?> GetBookingAsync(Guid companyId, Guid siteId, Guid bookingId);
    Task<PagedResponse<BookingListResponse>> GetBookingsAsync(Guid companyId, Guid siteId, string? status = null, Guid? serviceId = null, DateTime? fromDate = null, DateTime? toDate = null, int page = 1, int pageSize = 20, string? search = null);

    // ERP Sync (event-driven)
    Task<Guid?> SyncBookingToErpAsync(Guid companyId, Guid siteId, Guid bookingId);
}
