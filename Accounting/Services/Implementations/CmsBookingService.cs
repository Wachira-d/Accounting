using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class CmsBookingService : ICmsBookingService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CmsBookingService> _logger;

    public CmsBookingService(AccountingDbContext db, ILogger<CmsBookingService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ===== Services =====

    public async Task<BookingServiceResponse> CreateServiceAsync(Guid companyId, Guid siteId, CreateBookingServiceRequest request, string userId)
    {
        var slug = !string.IsNullOrWhiteSpace(request.Slug) ? request.Slug : GenerateSlug(request.Name);

        var svc = new SiteBookingService
        {
            CompanyId = companyId, SiteId = siteId, Name = request.Name, NameEn = request.NameEn,
            Description = request.Description, ImageUrl = request.ImageUrl, Slug = slug,
            DurationMinutes = request.DurationMinutes, BufferMinutes = request.BufferMinutes,
            MaxCapacity = request.MaxCapacity, MaxAdvanceBookingDays = request.MaxAdvanceBookingDays,
            MinAdvanceBookingHours = request.MinAdvanceBookingHours,
            Price = request.Price, DepositAmount = request.DepositAmount, DepositPercent = request.DepositPercent,
            BookingType = request.BookingType, ProductId = request.ProductId,
            Category = request.Category, AutoConfirm = request.AutoConfirm,
            FreeCancellationHours = request.FreeCancellationHours,
            CancellationFeePercent = request.CancellationFeePercent,
            CreatedBy = userId
        };
        _db.SiteBookingServices.Add(svc);
        await _db.SaveChangesAsync();

        return await GetServiceAsync(companyId, siteId, svc.Id) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<BookingServiceResponse> UpdateServiceAsync(Guid companyId, Guid siteId, Guid serviceId, UpdateBookingServiceRequest request, string userId)
    {
        var svc = await _db.SiteBookingServices.FirstOrDefaultAsync(s => s.Id == serviceId && s.SiteId == siteId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Booking service not found.");

        svc.Name = request.Name; svc.NameEn = request.NameEn;
        svc.Description = request.Description; svc.ImageUrl = request.ImageUrl;
        if (request.Slug != null) svc.Slug = request.Slug;
        svc.DurationMinutes = request.DurationMinutes; svc.BufferMinutes = request.BufferMinutes;
        svc.MaxCapacity = request.MaxCapacity;
        svc.MaxAdvanceBookingDays = request.MaxAdvanceBookingDays;
        svc.MinAdvanceBookingHours = request.MinAdvanceBookingHours;
        svc.Price = request.Price; svc.DepositAmount = request.DepositAmount;
        svc.DepositPercent = request.DepositPercent;
        svc.BookingType = request.BookingType; svc.ProductId = request.ProductId;
        svc.Category = request.Category; svc.AutoConfirm = request.AutoConfirm;
        svc.FreeCancellationHours = request.FreeCancellationHours;
        svc.CancellationFeePercent = request.CancellationFeePercent;
        if (request.IsActive.HasValue) svc.IsActive = request.IsActive.Value;
        svc.UpdatedBy = userId;

        await _db.SaveChangesAsync();
        return await GetServiceAsync(companyId, siteId, serviceId) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<BookingServiceResponse?> GetServiceAsync(Guid companyId, Guid siteId, Guid serviceId)
    {
        return await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.Id == serviceId && s.SiteId == siteId && s.CompanyId == companyId)
            .Select(s => new BookingServiceResponse
            {
                Id = s.Id, Name = s.Name, NameEn = s.NameEn, Description = s.Description,
                ImageUrl = s.ImageUrl, Slug = s.Slug,
                DurationMinutes = s.DurationMinutes, BufferMinutes = s.BufferMinutes,
                MaxCapacity = s.MaxCapacity, MaxAdvanceBookingDays = s.MaxAdvanceBookingDays,
                MinAdvanceBookingHours = s.MinAdvanceBookingHours,
                Price = s.Price, DepositAmount = s.DepositAmount, DepositPercent = s.DepositPercent,
                Currency = s.Currency, BookingType = s.BookingType, ProductId = s.ProductId,
                Category = s.Category, AutoConfirm = s.AutoConfirm,
                FreeCancellationHours = s.FreeCancellationHours,
                CancellationFeePercent = s.CancellationFeePercent,
                IsActive = s.IsActive,
                UpcomingBookingsCount = s.Bookings.Count(b => b.BookingDate >= DateTime.UtcNow && b.Status != BookingStatus.Cancelled),
                CreatedAt = s.CreatedAt
            })
            .FirstOrDefaultAsync();
    }

    public async Task<List<BookingServiceResponse>> GetServicesAsync(Guid companyId, Guid siteId)
    {
        // Lazy auto-seed — when a site has zero booking services but
        // pages were seeded with BookingCalendar blocks, the storefront
        // shows "ยังไม่มีบริการให้จองในขณะนี้" and customers can't book.
        // Create one default service so the flow works out of the box;
        // owner edits or adds more from the admin UI.
        var any = await _db.SiteBookingServices
            .AsNoTracking()
            .AnyAsync(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted);
        if (!any)
        {
            await EnsureDefaultBookingServiceAsync(companyId, siteId);
        }

        return await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.SiteId == siteId && s.CompanyId == companyId && !s.IsDeleted && s.IsActive)
            .OrderBy(s => s.SortOrder)
            .Select(s => new BookingServiceResponse
            {
                Id = s.Id, Name = s.Name, NameEn = s.NameEn, Description = s.Description,
                Slug = s.Slug, DurationMinutes = s.DurationMinutes, Price = s.Price,
                Currency = s.Currency, BookingType = s.BookingType,
                Category = s.Category, IsActive = s.IsActive,
                UpcomingBookingsCount = s.Bookings.Count(b => b.BookingDate >= DateTime.UtcNow && b.Status != BookingStatus.Cancelled),
                CreatedAt = s.CreatedAt
            })
            .ToListAsync();
    }

    /// <summary>One-shot default-service creator: idempotent at the
    /// usual call site (anyExists check upstream). Seeds a single
    /// "นัดหมาย / จอง" Lead-type service so the storefront BookingCalendar
    /// block hydrates with at least one bookable option instead of an
    /// empty grid. Owner edits the name/price/duration in
    /// /pages/cms-bookings (services tab) afterwards.</summary>
    private async Task EnsureDefaultBookingServiceAsync(Guid companyId, Guid siteId)
    {
        _db.SiteBookingServices.Add(new SiteBookingService
        {
            CompanyId = companyId,
            SiteId = siteId,
            Name = "นัดหมาย / จอง",
            Description = "บริการเริ่มต้น — เจ้าของเว็บปรับแต่งชื่อ / ราคา / เวลาได้ที่หน้าจัดการ",
            Slug = "default",
            DurationMinutes = 60,
            BufferMinutes = 0,
            MaxCapacity = 1,
            MaxAdvanceBookingDays = 60,
            MinAdvanceBookingHours = 1,
            Price = 0m,
            Currency = "THB",
            BookingType = BookingType.Lead,
            IsActive = true,
            SortOrder = 0,
            CreatedBy = "auto-seed"
        });
        try { await _db.SaveChangesAsync(); }
        catch { /* concurrent creation — fine, the next read picks it up */ }
    }

    public async Task<bool> DeleteServiceAsync(Guid companyId, Guid siteId, Guid serviceId)
    {
        var svc = await _db.SiteBookingServices.FirstOrDefaultAsync(s => s.Id == serviceId && s.SiteId == siteId);
        if (svc == null) return false;
        svc.IsActive = false;
        svc.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Slots =====

    public async Task<BookingSlotResponse> CreateSlotAsync(Guid companyId, Guid siteId, Guid serviceId, CreateBookingSlotRequest request, string userId)
    {
        var slot = new SiteBookingSlot
        {
            CompanyId = companyId, BookingServiceId = serviceId,
            DayOfWeek = request.DayOfWeek,
            StartTime = TimeOnly.Parse(request.StartTime),
            EndTime = TimeOnly.Parse(request.EndTime),
            SpecificDate = !string.IsNullOrEmpty(request.SpecificDate) ? DateOnly.Parse(request.SpecificDate) : null,
            IsBlocked = request.IsBlocked, MaxCapacity = request.MaxCapacity,
            CreatedBy = userId
        };
        _db.SiteBookingSlots.Add(slot);
        await _db.SaveChangesAsync();

        return MapSlotResponse(slot);
    }

    public async Task<List<BookingSlotResponse>> GetSlotsAsync(Guid companyId, Guid siteId, Guid serviceId)
    {
        var slots = await _db.SiteBookingSlots.AsNoTracking()
            .Where(s => s.BookingServiceId == serviceId && s.IsActive)
            .OrderBy(s => s.DayOfWeek).ThenBy(s => s.StartTime)
            .ToListAsync();

        return slots.Select(MapSlotResponse).ToList();
    }

    public async Task<bool> DeleteSlotAsync(Guid companyId, Guid siteId, Guid serviceId, Guid slotId)
    {
        var slot = await _db.SiteBookingSlots.FirstOrDefaultAsync(s => s.Id == slotId && s.BookingServiceId == serviceId);
        if (slot == null) return false;
        _db.SiteBookingSlots.Remove(slot);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Available Slots =====

    public async Task<List<AvailableSlotResponse>> GetAvailableSlotsAsync(Guid companyId, Guid siteId, AvailableSlotsRequest request)
    {
        var svc = await _db.SiteBookingServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.BookingServiceId && s.SiteId == siteId);
        if (svc == null) return new();

        var date = DateOnly.FromDateTime(request.Date);
        var dayOfWeek = request.Date.DayOfWeek;

        // Get applicable slots (specific date overrides + recurring day-of-week)
        var slots = await _db.SiteBookingSlots.AsNoTracking()
            .Where(s => s.BookingServiceId == request.BookingServiceId && s.IsActive && !s.IsBlocked &&
                ((s.SpecificDate == date) || (s.SpecificDate == null && s.DayOfWeek == dayOfWeek)))
            .OrderBy(s => s.StartTime)
            .ToListAsync();

        // Get existing bookings for that date
        var existingBookings = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.BookingServiceId == request.BookingServiceId &&
                b.BookingDate.Date == request.Date.Date &&
                b.Status != BookingStatus.Cancelled)
            .ToListAsync();

        var available = new List<AvailableSlotResponse>();
        foreach (var slot in slots)
        {
            var booked = existingBookings.Count(b =>
                b.StartTime >= slot.StartTime && b.StartTime < slot.EndTime);

            var remaining = slot.MaxCapacity - booked;
            if (remaining >= request.GuestCount)
            {
                available.Add(new AvailableSlotResponse
                {
                    StartTime = slot.StartTime.ToString("HH:mm"),
                    EndTime = slot.EndTime.ToString("HH:mm"),
                    AvailableCapacity = remaining
                });
            }
        }

        return available;
    }

    // ===== Bookings =====

    public async Task<BookingResponse> CreateBookingAsync(Guid companyId, Guid siteId, CreateBookingRequest request, string? userId)
    {
        var svc = await _db.SiteBookingServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.BookingServiceId && s.SiteId == siteId && s.IsActive)
            ?? throw new KeyNotFoundException("Booking service not found or inactive.");

        var startTime = TimeOnly.Parse(request.StartTime);
        var endTime = startTime.AddMinutes(svc.DurationMinutes);

        var depositAmount = svc.DepositAmount ?? (svc.DepositPercent.HasValue ? svc.Price * svc.DepositPercent.Value / 100m : 0m);

        var booking = new SiteBooking
        {
            CompanyId = companyId, SiteId = siteId,
            BookingServiceId = request.BookingServiceId,
            CustomerId = request.CustomerId,
            BookingNumber = await GenerateBookingNumber(companyId, siteId),
            BookingDate = request.BookingDate,
            StartTime = startTime, EndTime = endTime,
            DurationMinutes = svc.DurationMinutes,
            GuestName = request.GuestName, GuestEmail = request.GuestEmail,
            GuestPhone = request.GuestPhone, GuestCount = request.GuestCount,
            TotalAmount = svc.Price, DepositAmount = depositAmount,
            Currency = svc.Currency,
            CustomerNotes = request.CustomerNotes,
            CreatedBy = userId
        };

        // Auto-confirm for Lead bookings or if service allows it
        if (svc.AutoConfirm || svc.BookingType == BookingType.Lead)
        {
            booking.Status = BookingStatus.Confirmed;
            booking.ConfirmedAt = DateTime.UtcNow;
        }

        _db.SiteBookings.Add(booking);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Booking {BookingNumber} created for service {ServiceName}", booking.BookingNumber, svc.Name);

        // Auto-sync to ERP based on booking type
        if (svc.BookingType != BookingType.Lead)
            await SyncBookingToErpAsync(companyId, siteId, booking.Id);

        return await GetBookingAsync(companyId, siteId, booking.Id) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<BookingResponse> UpdateBookingStatusAsync(Guid companyId, Guid siteId, Guid bookingId, UpdateBookingStatusRequest request, string userId)
    {
        var booking = await _db.SiteBookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Booking not found.");

        booking.Status = request.Status;
        if (request.InternalNotes != null) booking.InternalNotes = request.InternalNotes;
        if (request.CancellationReason != null) booking.CancellationReason = request.CancellationReason;

        switch (request.Status)
        {
            case BookingStatus.Confirmed: booking.ConfirmedAt = DateTime.UtcNow; break;
            case BookingStatus.Completed: booking.CompletedAt = DateTime.UtcNow; break;
            case BookingStatus.Cancelled: booking.CancelledAt = DateTime.UtcNow; break;
        }

        booking.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        return await GetBookingAsync(companyId, siteId, bookingId) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<BookingResponse?> GetBookingAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        return await _db.SiteBookings.AsNoTracking()
            .Where(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
            .Select(b => new BookingResponse
            {
                Id = b.Id, BookingNumber = b.BookingNumber, Status = b.Status,
                BookingServiceId = b.BookingServiceId,
                ServiceName = b.BookingService.Name,
                CustomerId = b.CustomerId,
                CustomerName = b.Customer != null ? b.Customer.FullName : b.GuestName,
                CustomerEmail = b.Customer != null ? b.Customer.Email : b.GuestEmail,
                GuestName = b.GuestName, GuestEmail = b.GuestEmail,
                GuestPhone = b.GuestPhone, GuestCount = b.GuestCount,
                BookingDate = b.BookingDate,
                StartTime = b.StartTime.ToString("HH:mm"),
                EndTime = b.EndTime.ToString("HH:mm"),
                DurationMinutes = b.DurationMinutes,
                TotalAmount = b.TotalAmount, DepositAmount = b.DepositAmount,
                PaidAmount = b.PaidAmount, Currency = b.Currency,
                BookingType = b.BookingService.BookingType,
                ErpDocumentType = b.ErpDocumentType, ErpDocumentId = b.ErpDocumentId,
                CustomerNotes = b.CustomerNotes, InternalNotes = b.InternalNotes,
                ConfirmedAt = b.ConfirmedAt, CompletedAt = b.CompletedAt,
                CancelledAt = b.CancelledAt, CreatedAt = b.CreatedAt
            })
            .FirstOrDefaultAsync();
    }

    public async Task<PagedResponse<BookingListResponse>> GetBookingsAsync(Guid companyId, Guid siteId, string? status, Guid? serviceId, DateTime? fromDate, DateTime? toDate, int page, int pageSize)
    {
        var query = _db.SiteBookings.AsNoTracking().Where(b => b.SiteId == siteId && b.CompanyId == companyId);

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<BookingStatus>(status, out var st))
            query = query.Where(b => b.Status == st);
        if (serviceId.HasValue) query = query.Where(b => b.BookingServiceId == serviceId);
        if (fromDate.HasValue) query = query.Where(b => b.BookingDate >= fromDate);
        if (toDate.HasValue) query = query.Where(b => b.BookingDate <= toDate);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(b => b.BookingDate).ThenBy(b => b.StartTime)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(b => new BookingListResponse
            {
                Id = b.Id, BookingNumber = b.BookingNumber, Status = b.Status,
                ServiceName = b.BookingService.Name,
                CustomerName = b.Customer != null ? b.Customer.FullName : b.GuestName,
                BookingDate = b.BookingDate,
                StartTime = b.StartTime.ToString("HH:mm"),
                TotalAmount = b.TotalAmount, Currency = b.Currency,
                CreatedAt = b.CreatedAt
            })
            .ToListAsync();

        return new PagedResponse<BookingListResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    // ===== ERP Sync (Event-Driven) =====

    public async Task<Guid?> SyncBookingToErpAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        var booking = await _db.SiteBookings
            .Include(b => b.BookingService)
            .Include(b => b.Customer)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId);

        if (booking == null || booking.ErpDocumentId.HasValue) return booking?.ErpDocumentId;

        var svc = booking.BookingService;

        // Determine ERP document type based on BookingType
        var (docType, erpDocType) = svc.BookingType switch
        {
            BookingType.Lead => (DocumentType.Quotation, ErpBookingDocumentType.DraftServiceOrder),
            BookingType.Appointment => (DocumentType.Quotation, ErpBookingDocumentType.DraftServiceOrder),
            BookingType.Guaranteed => (DocumentType.TaxInvoice, ErpBookingDocumentType.TaxInvoice),
            BookingType.PrePayment => (DocumentType.Receipt, ErpBookingDocumentType.AdvanceReceipt),
            _ => (DocumentType.Quotation, ErpBookingDocumentType.DraftServiceOrder)
        };

        // Find or create ERP Contact
        Guid? contactId = booking.Customer?.ContactId;
        if (contactId == null)
        {
            var email = booking.Customer?.Email ?? booking.GuestEmail;
            var name = booking.Customer?.FullName ?? booking.GuestName ?? "Walk-in Customer";

            if (!string.IsNullOrEmpty(email))
            {
                var existingContact = await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Email == email);
                if (existingContact != null)
                {
                    contactId = existingContact.Id;
                    if (booking.Customer != null) booking.Customer.ContactId = contactId;
                }
            }

            if (contactId == null)
            {
                var newContact = new Contact
                {
                    CompanyId = companyId, Name = name,
                    Email = email, Phone = booking.Customer?.Phone ?? booking.GuestPhone,
                    IsCustomer = true
                };
                _db.Contacts.Add(newContact);
                contactId = newContact.Id;
                if (booking.Customer != null) booking.Customer.ContactId = contactId;
            }
        }

        var doc = new Document
        {
            CompanyId = companyId,
            DocumentType = docType,
            Status = svc.BookingType == BookingType.Lead ? DocumentStatus.Draft : DocumentStatus.Approved,
            DocumentDate = DateTime.UtcNow,
            ContactId = contactId!.Value,
            Currency = svc.Currency,
            SubTotal = booking.TotalAmount,
            TotalAmount = booking.TotalAmount,
            Notes = $"Booking #{booking.BookingNumber} - {svc.Name}",
            Reference = booking.BookingNumber
        };

        _db.Documents.Add(doc);

        _db.DocumentLines.Add(new DocumentLine
        {
            DocumentId = doc.Id,
            LineOrder = 1,
            ProductCode = svc.Product?.Code ?? "",
            Description = $"{svc.Name} ({booking.BookingDate:yyyy-MM-dd} {booking.StartTime:HH:mm}-{booking.EndTime:HH:mm})",
            Quantity = booking.GuestCount,
            Unit = "ครั้ง",
            UnitPrice = svc.Price,
            Amount = booking.TotalAmount
        });

        booking.ErpDocumentId = doc.Id;
        booking.ErpDocumentType = erpDocType;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Booking {BookingNumber} synced to ERP as {DocType} (Doc {DocId})", booking.BookingNumber, erpDocType, doc.Id);
        return doc.Id;
    }

    // ===== Helpers =====

    private async Task<string> GenerateBookingNumber(Guid companyId, Guid siteId)
    {
        var prefix = $"BK-{DateTime.UtcNow:yyMM}";
        var last = await _db.SiteBookings
            .Where(b => b.SiteId == siteId && b.BookingNumber.StartsWith(prefix))
            .OrderByDescending(b => b.BookingNumber)
            .Select(b => b.BookingNumber)
            .FirstOrDefaultAsync();

        var seq = 1;
        if (last != null && last.Length > prefix.Length + 1)
        {
            if (int.TryParse(last[(prefix.Length + 1)..], out var lastSeq))
                seq = lastSeq + 1;
        }
        return $"{prefix}-{seq:D4}";
    }

    private static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant().Trim();
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"[^a-z0-9฀-๿\s-]", "");
        slug = System.Text.RegularExpressions.Regex.Replace(slug, @"[\s]+", "-");
        return slug.Trim('-');
    }

    private static BookingSlotResponse MapSlotResponse(SiteBookingSlot s) => new()
    {
        Id = s.Id, DayOfWeek = s.DayOfWeek,
        StartTime = s.StartTime.ToString("HH:mm"),
        EndTime = s.EndTime.ToString("HH:mm"),
        SpecificDate = s.SpecificDate?.ToString("yyyy-MM-dd"),
        IsBlocked = s.IsBlocked, MaxCapacity = s.MaxCapacity,
        CurrentBookings = s.CurrentBookings,
        AvailableSlots = s.MaxCapacity - s.CurrentBookings,
        IsActive = s.IsActive
    };
}
