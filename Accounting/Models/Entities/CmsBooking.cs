using Accounting.Models.Enums;

namespace Accounting.Models.Entities;

// ===== Booking Services =====

public class SiteBookingService : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public string Name { get; set; } = "";
    public string? NameEn { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
    public string Slug { get; set; } = "";

    // Duration & capacity
    public int DurationMinutes { get; set; } = 60;
    public int BufferMinutes { get; set; } = 0;
    public int MaxCapacity { get; set; } = 1;
    public int MaxAdvanceBookingDays { get; set; } = 30;
    public int MinAdvanceBookingHours { get; set; } = 1;

    // Pricing
    public decimal Price { get; set; }
    public decimal? DepositAmount { get; set; }
    public decimal? DepositPercent { get; set; }
    public string Currency { get; set; } = "THB";

    // Booking type determines ERP document flow
    public BookingType BookingType { get; set; } = BookingType.Lead;

    // ERP mapping: linked product/service
    public Guid? ProductId { get; set; }
    public Product? Product { get; set; }

    // Categorization
    public string? Category { get; set; }
    public string? Tags { get; set; }

    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }

    // Auto-confirm or require manual approval
    public bool AutoConfirm { get; set; } = false;

    // Cancellation policy
    public int? FreeCancellationHours { get; set; }
    public decimal? CancellationFeePercent { get; set; }

    public ICollection<SiteBookingSlot> Slots { get; set; } = new List<SiteBookingSlot>();
    public ICollection<SiteBooking> Bookings { get; set; } = new List<SiteBooking>();
    public ICollection<SiteBookingServiceTranslation> Translations { get; set; } = new List<SiteBookingServiceTranslation>();
}

public class SiteBookingServiceTranslation : TenantEntity
{
    public Guid BookingServiceId { get; set; }
    public SiteBookingService BookingService { get; set; } = null!;

    public string LanguageCode { get; set; } = "th";
    public string? Name { get; set; }
    public string? Description { get; set; }
}

// ===== Time Slots =====

public class SiteBookingSlot : TenantEntity
{
    public Guid BookingServiceId { get; set; }
    public SiteBookingService BookingService { get; set; } = null!;

    // Recurring schedule (day-of-week based)
    public DayOfWeek? DayOfWeek { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    // Or specific date override
    public DateOnly? SpecificDate { get; set; }
    public bool IsBlocked { get; set; } = false;

    public int MaxCapacity { get; set; } = 1;
    public int CurrentBookings { get; set; }

    public bool IsActive { get; set; } = true;
}

// ===== Bookings =====

public class SiteBooking : TenantEntity
{
    public Guid SiteId { get; set; }
    public Site Site { get; set; } = null!;

    public Guid BookingServiceId { get; set; }
    public SiteBookingService BookingService { get; set; } = null!;

    public Guid? CustomerId { get; set; }
    public SiteCustomer? Customer { get; set; }

    public string BookingNumber { get; set; } = "";
    public BookingStatus Status { get; set; } = BookingStatus.Pending;

    // Schedule
    public DateTime BookingDate { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public int DurationMinutes { get; set; }

    // Guest info (for walk-in without account)
    public string? GuestName { get; set; }
    public string? GuestEmail { get; set; }
    public string? GuestPhone { get; set; }
    public int GuestCount { get; set; } = 1;

    // Amounts
    public decimal TotalAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public string Currency { get; set; } = "THB";

    // Payment
    public string? PaymentReference { get; set; }
    public DateTime? PaidAt { get; set; }

    // ERP Integration (event-driven document creation)
    public Guid? ErpDocumentId { get; set; }
    public Document? ErpDocument { get; set; }
    public ErpBookingDocumentType? ErpDocumentType { get; set; }

    public string? CustomerNotes { get; set; }
    public string? InternalNotes { get; set; }
    public string? CancellationReason { get; set; }

    public DateTime? ConfirmedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }

    // Reminders
    public bool ReminderSent { get; set; } = false;
    public DateTime? ReminderSentAt { get; set; }

    public ICollection<SiteBookingPayment> Payments { get; set; } = new List<SiteBookingPayment>();
}

public class SiteBookingPayment : TenantEntity
{
    public Guid BookingId { get; set; }
    public SiteBooking Booking { get; set; } = null!;

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "THB";
    public PaymentMethod PaymentMethod { get; set; }
    public string? GatewayTransactionId { get; set; }
    public string? Reference { get; set; }
    public string? SlipUrl { get; set; }

    public SitePaymentStatus Status { get; set; } = SitePaymentStatus.Pending;
    public DateTime? PaidAt { get; set; }
    public string? FailureReason { get; set; }

    // Refund tracking
    public bool IsRefund { get; set; } = false;
    public string? RefundReason { get; set; }
}
