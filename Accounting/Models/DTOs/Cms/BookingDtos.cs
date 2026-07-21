using System.ComponentModel.DataAnnotations;
using Accounting.Models.Enums;

namespace Accounting.Models.DTOs.Cms;

// ==================== Booking Service ====================

public class CreateBookingServiceRequest
{
    [Required, MaxLength(256)]
    public string Name { get; set; } = "";
    [MaxLength(256)]
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string? Slug { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public int BufferMinutes { get; set; } = 0;
    public int MaxCapacity { get; set; } = 1;
    public int MaxAdvanceBookingDays { get; set; } = 30;
    public int MinAdvanceBookingHours { get; set; } = 1;
    public decimal Price { get; set; }
    public decimal? DepositAmount { get; set; }
    public decimal? DepositPercent { get; set; }
    public BookingType BookingType { get; set; } = BookingType.Lead;
    public Guid? ProductId { get; set; }
    public string? Category { get; set; }
    public bool AutoConfirm { get; set; } = false;
    public int? FreeCancellationHours { get; set; }
    public decimal? CancellationFeePercent { get; set; }
}

public class UpdateBookingServiceRequest : CreateBookingServiceRequest
{
    public bool? IsActive { get; set; }
}

public class BookingServiceResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string Slug { get; set; } = "";
    public int DurationMinutes { get; set; }
    public int BufferMinutes { get; set; }
    public int MaxCapacity { get; set; }
    public int MaxAdvanceBookingDays { get; set; }
    public int MinAdvanceBookingHours { get; set; }
    public decimal Price { get; set; }
    public decimal? DepositAmount { get; set; }
    public decimal? DepositPercent { get; set; }
    public string Currency { get; set; } = "THB";
    public BookingType BookingType { get; set; }
    public Guid? ProductId { get; set; }
    public string? Category { get; set; }
    public bool AutoConfirm { get; set; }
    public int? FreeCancellationHours { get; set; }
    public decimal? CancellationFeePercent { get; set; }
    public bool IsActive { get; set; }
    public int UpcomingBookingsCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

// ==================== Booking Slot ====================

public class CreateBookingSlotRequest
{
    public DayOfWeek? DayOfWeek { get; set; }
    [Required]
    public string StartTime { get; set; } = "09:00";
    [Required]
    public string EndTime { get; set; } = "10:00";
    public string? SpecificDate { get; set; }
    public bool IsBlocked { get; set; } = false;
    public int MaxCapacity { get; set; } = 1;
}

public class BookingSlotResponse
{
    public Guid Id { get; set; }
    public DayOfWeek? DayOfWeek { get; set; }
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string? SpecificDate { get; set; }
    public bool IsBlocked { get; set; }
    public int MaxCapacity { get; set; }
    public int CurrentBookings { get; set; }
    public int AvailableSlots { get; set; }
    public bool IsActive { get; set; }
}

// ==================== Booking ====================

public class CreateBookingRequest
{
    [Required]
    public Guid BookingServiceId { get; set; }
    public Guid? CustomerId { get; set; }
    [Required]
    public DateTime BookingDate { get; set; }
    [Required]
    public string StartTime { get; set; } = "";

    // Guest info (when no customer account)
    public string? GuestName { get; set; }
    public string? GuestEmail { get; set; }
    public string? GuestPhone { get; set; }
    public int GuestCount { get; set; } = 1;
    public string? CustomerNotes { get; set; }

    // Payment info
    public Guid? PaymentGatewayId { get; set; }
    public string? PaymentReference { get; set; }
}

public class UpdateBookingStatusRequest
{
    [Required]
    public BookingStatus Status { get; set; }
    public string? InternalNotes { get; set; }
    public string? CancellationReason { get; set; }
}

public class BookingResponse
{
    public Guid Id { get; set; }
    public string BookingNumber { get; set; } = "";
    public BookingStatus Status { get; set; }
    public Guid BookingServiceId { get; set; }
    public string ServiceName { get; set; } = "";
    public Guid? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerEmail { get; set; }
    public string? GuestName { get; set; }
    public string? GuestEmail { get; set; }
    public string? GuestPhone { get; set; }
    public int GuestCount { get; set; }
    public DateTime BookingDate { get; set; }
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public int DurationMinutes { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public string Currency { get; set; } = "THB";
    public BookingType BookingType { get; set; }
    public ErpBookingDocumentType? ErpDocumentType { get; set; }
    public Guid? ErpDocumentId { get; set; }
    public string? CustomerNotes { get; set; }
    public string? InternalNotes { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class BookingListResponse
{
    public Guid Id { get; set; }
    public string BookingNumber { get; set; } = "";
    public BookingStatus Status { get; set; }
    public string ServiceName { get; set; } = "";
    public string? CustomerName { get; set; }
    public DateTime BookingDate { get; set; }
    public string StartTime { get; set; } = "";
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "THB";
    public DateTime CreatedAt { get; set; }
}

// ==================== Available Slots Query ====================

public class AvailableSlotsRequest
{
    [Required]
    public Guid BookingServiceId { get; set; }
    [Required]
    public DateTime Date { get; set; }
    public int GuestCount { get; set; } = 1;
}

public class AvailableSlotResponse
{
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public int AvailableCapacity { get; set; }
}
