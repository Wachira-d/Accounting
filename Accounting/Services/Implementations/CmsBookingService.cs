using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class CmsBookingService : ICmsBookingService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<CmsBookingService> _logger;
    private readonly IDocumentService? _docService;
    // รอบ 205 W6 (R-39): สายต่อบัตรคอร์ส — ดู CmsBookingService.Credits.cs · null = ไม่เปิดใช้ (การจองปกติไม่แตะสมุดเครดิต)
    private readonly ICreditBookingLedger? _creditLedger;

    public CmsBookingService(AccountingDbContext db, ILogger<CmsBookingService> logger,
        IDocumentService? docService = null, ICreditBookingLedger? creditLedger = null)
    {
        _db = db;
        _logger = logger;
        _docService = docService;
        _creditLedger = creditLedger;
    }

    // ===== Services =====

    public async Task<BookingServiceResponse> CreateServiceAsync(Guid companyId, Guid siteId, CreateBookingServiceRequest request, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var svc = new SiteBookingService { CompanyId = companyId, SiteId = siteId, CreatedBy = userId };
        ApplySettings(svc, request);
        svc.Slug = await UniqueSlugAsync(companyId, siteId, svc.Slug, null);
        if (request.Schedule != null)
            foreach (var row in BuildSlotRows(companyId, svc, request.Schedule, userId)) svc.Slots.Add(row);
        _db.SiteBookingServices.Add(svc);
        await _db.SaveChangesAsync();

        return await GetServiceAsync(companyId, siteId, svc.Id) ?? throw new InvalidOperationException("Failed.");
    }

    public async Task<BookingServiceResponse> UpdateServiceAsync(Guid companyId, Guid siteId, Guid serviceId, UpdateBookingServiceRequest request, string userId)
    {
        var svc = await _db.SiteBookingServices.Include(s => s.Slots)
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.SiteId == siteId && s.CompanyId == companyId && !s.IsDeleted)
            ?? throw new KeyNotFoundException("Booking service not found.");

        var oldSlug = svc.Slug;
        ApplySettings(svc, request);
        if (svc.Slug != oldSlug) svc.Slug = await UniqueSlugAsync(companyId, siteId, svc.Slug, svc.Id);
        if (request.IsActive.HasValue) svc.IsActive = request.IsActive.Value;
        // ตารางเวลา: null = ผู้เรียก API เดิมที่ไม่รู้จักฟิลด์นี้ ⇒ ไม่แตะ · ส่งมา = แทนที่ทั้งชุด (การจองเดิมไม่ถูกแตะ — แถวเวลาไม่ใช่ FK ของการจอง)
        if (request.Schedule != null)
        {
            var rows = BuildSlotRows(companyId, svc, request.Schedule, userId);
            _db.SiteBookingSlots.RemoveRange(svc.Slots.Where(x => x.CompanyId == companyId).ToList());
            _db.SiteBookingSlots.AddRange(rows);
        }
        svc.UpdatedBy = userId;

        await _db.SaveChangesAsync();
        return await GetServiceAsync(companyId, siteId, serviceId) ?? throw new InvalidOperationException("Failed.");
    }

    /// <summary>คำขอ → ค่าตั้งของบริการ (สร้าง/แก้ไข/seed ใช้ตัวเดียว) · ตรวจด้วย <see cref="BookingSlotEngine.ValidateRules"/> — ผิด = โยนข้อความไทย</summary>
    public static void ApplySettings(SiteBookingService svc, CreateBookingServiceRequest r)
    {
        var errs = new List<string>();
        if (r.Name.Trim().Length == 0) errs.Add("กรุณากรอกชื่อบริการ");
        if (r.Price < 0) errs.Add("ราคาต้องไม่ติดลบ");
        if (r.DepositAmount is < 0m) errs.Add("มัดจำต้องไม่ติดลบ");
        if (r.DepositPercent is < 0m or > 100m) errs.Add("มัดจำ (%) ต้องอยู่ระหว่าง 0–100");
        if (r.CancellationFeePercent is < 0m or > 100m) errs.Add("ค่าธรรมเนียมยกเลิก (%) ต้องอยู่ระหว่าง 0–100");
        if (r.FreeCancellationHours is < 0) errs.Add("ชั่วโมงยกเลิกฟรีต้องไม่ติดลบ");
        if (!string.IsNullOrWhiteSpace(r.HalfDaySplitTime) && BookingSlotEngine.ParseTime(r.HalfDaySplitTime) == null)
            errs.Add("เวลาแบ่งเช้า/บ่ายต้องเป็นรูปแบบ HH:mm");

        svc.Name = r.Name.Trim(); svc.NameEn = r.NameEn;
        svc.Description = r.Description; svc.ImageUrl = string.IsNullOrWhiteSpace(r.ImageUrl) ? null : r.ImageUrl.Trim();
        if (!string.IsNullOrWhiteSpace(r.Slug)) svc.Slug = r.Slug.Trim();
        else if (string.IsNullOrWhiteSpace(svc.Slug)) svc.Slug = GenerateSlug(string.IsNullOrWhiteSpace(r.NameEn) ? svc.Name : r.NameEn);
        if (string.IsNullOrWhiteSpace(svc.Slug)) svc.Slug = "service-" + svc.Id.ToString("N")[..8];
        svc.DurationMinutes = r.DurationMinutes; svc.BufferMinutes = r.BufferMinutes;
        svc.BufferBeforeMinutes = r.BufferBeforeMinutes;
        svc.MaxCapacity = r.MaxCapacity; svc.CapacityUnit = r.CapacityUnit;
        svc.MaxAdvanceBookingDays = r.MaxAdvanceBookingDays;
        svc.MinAdvanceBookingHours = r.MinAdvanceBookingHours;
        svc.SlotMode = r.SlotMode;
        svc.SlotIntervalMinutes = r.SlotMode == BookingSlotMode.Interval ? r.SlotIntervalMinutes : null;
        svc.MaxUnitsPerBooking = r.SlotMode == BookingSlotMode.Interval ? Math.Max(1, r.MaxUnitsPerBooking) : 1;
        svc.HalfDaySplitTime = r.SlotMode == BookingSlotMode.HalfDay && BookingSlotEngine.ParseTime(r.HalfDaySplitTime) is TimeOnly split
            ? split.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : null;
        svc.CapacityGroup = string.IsNullOrWhiteSpace(r.CapacityGroup) ? null : r.CapacityGroup.Trim();
        svc.Price = r.Price; svc.DepositAmount = r.DepositAmount;
        svc.DepositPercent = r.DepositPercent;
        svc.BookingType = r.BookingType; svc.ProductId = r.ProductId;
        svc.Category = r.Category; svc.AutoConfirm = r.AutoConfirm;
        svc.FreeCancellationHours = r.FreeCancellationHours;
        svc.CancellationFeePercent = r.CancellationFeePercent;
        svc.EnforceSlotCapacity = r.EnforceSlotCapacity;
        svc.OverlapLimit = r.EnforceSlotCapacity ? null : r.OverlapLimit;
        svc.WaitlistEnabled = r.WaitlistEnabled;
        svc.ResourceMode = r.ResourceMode;
        svc.UseSiteDefaults = r.UseSiteDefaults;

        errs.AddRange(BookingSlotEngine.ValidateRules(BookingSlotEngine.RulesOf(svc)));
        if (errs.Count > 0) throw new BusinessRuleException(string.Join(" · ", errs), "CMS-BOOKING-SETTINGS");
    }

    /// <summary>ตารางเวลา (DTO) → แถวกลาง (รายสัปดาห์/รายวันที่) — เวลาผิดรูป/เริ่มไม่ก่อนจบ = โยนข้อความไทย (ไม่ทิ้งเงียบ) ·
    /// ใช้ทั้งเวลาทำการของบริการและตารางงานรายบุคคลของผู้ให้บริการ (<paramref name="what"/> = คำในข้อความ)</summary>
    private static List<BookingScheduleRow> ParseSchedule(BookingScheduleDto schedule, int defaultCapacity, string what)
    {
        var errs = new List<string>();
        var rows = new List<BookingScheduleRow>();
        TimeOnly? T(string? v, string where)
        {
            var t = BookingSlotEngine.ParseTime(v);
            if (t == null) errs.Add($"{where}: เวลา \"{v}\" ต้องเป็นรูปแบบ HH:mm");
            return t;
        }
        foreach (var w in schedule.Weekly ?? new())
        {
            var ws = T(w.Start, what); var we = T(w.End, what);
            if (ws == null || we == null) continue;
            rows.Add(new BookingScheduleRow(w.DayOfWeek, null, ws.Value, we.Value, w.IsBlocked, Math.Max(1, w.Capacity ?? defaultCapacity)));
        }
        foreach (var x in schedule.Exceptions ?? new())
        {
            if (!DateOnly.TryParseExact(x.Date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
            { errs.Add($"วันพิเศษ: วันที่ \"{x.Date}\" ต้องเป็นรูปแบบ yyyy-MM-dd"); continue; }
            TimeOnly xs, xe;
            if (string.IsNullOrWhiteSpace(x.Start) && string.IsNullOrWhiteSpace(x.End))
            {
                if (!x.Closed) { errs.Add($"วันพิเศษ {x.Date}: เปิดพิเศษต้องระบุเวลาเปิด–ปิด"); continue; }
                xs = TimeOnly.MinValue; xe = new TimeOnly(23, 59);   // ปิดทั้งวัน
            }
            else
            {
                var ps = T(x.Start, $"วันพิเศษ {x.Date}"); var pe = T(x.End, $"วันพิเศษ {x.Date}");
                if (ps == null || pe == null) continue;
                xs = ps.Value; xe = pe.Value;
            }
            rows.Add(new BookingScheduleRow(null, d, xs, xe, x.Closed, Math.Max(1, defaultCapacity)));
        }
        errs.AddRange(BookingSlotEngine.ValidateSchedule(rows));
        if (errs.Count > 0) throw new BusinessRuleException(string.Join(" · ", errs.Distinct()), "CMS-BOOKING-SCHEDULE");
        return rows;
    }

    /// <summary>แถวกลาง → DTO (ทั้งของบริการและของผู้ให้บริการ — hydrate ฟอร์ม) · ความจุของแถวคืนเฉพาะเมื่อต่างจากค่าของบริการ</summary>
    private static BookingScheduleDto ScheduleDtoOf(IEnumerable<BookingScheduleRow> rows, int defaultCapacity)
    {
        string Hm(TimeOnly t, bool end) => BookingSlotEngine.Hhmm(end ? BookingSlotEngine.ToEndMinute(t) : BookingSlotEngine.ToMinute(t));
        var list = rows.ToList();
        return new BookingScheduleDto
        {
            Weekly = list.Where(x => x.SpecificDate == null && x.DayOfWeek != null)
                .OrderBy(x => x.DayOfWeek).ThenBy(x => x.IsBlocked).ThenBy(x => x.Start)
                .Select(x => new BookingWeeklyHoursDto
                {
                    DayOfWeek = x.DayOfWeek!.Value, Start = Hm(x.Start, false), End = Hm(x.End, true),
                    IsBlocked = x.IsBlocked, Capacity = x.Capacity != defaultCapacity ? x.Capacity : null,
                }).ToList(),
            Exceptions = list.Where(x => x.SpecificDate != null)
                .OrderBy(x => x.SpecificDate).ThenBy(x => x.Start)
                .Select(x =>
                {
                    var wholeDay = x.IsBlocked && BookingSlotEngine.ToMinute(x.Start) == 0
                        && BookingSlotEngine.ToEndMinute(x.End) == BookingSlotEngine.DayMinutes;
                    return new BookingDateExceptionDto
                    {
                        Date = x.SpecificDate!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                        Closed = x.IsBlocked,
                        Start = wholeDay ? null : Hm(x.Start, false),
                        End = wholeDay ? null : Hm(x.End, true),
                    };
                }).ToList(),
        };
    }

    /// <summary>ตารางเวลา (DTO) → แถว <c>SiteBookingSlot</c> ของบริการ</summary>
    public static List<SiteBookingSlot> BuildSlotRows(Guid companyId, SiteBookingService svc, BookingScheduleDto schedule, string? userId)
        => ParseSchedule(schedule, svc.MaxCapacity, "เวลาทำการ").Select(x => new SiteBookingSlot
        {
            CompanyId = companyId, BookingServiceId = svc.Id, DayOfWeek = x.DayOfWeek, SpecificDate = x.SpecificDate,
            StartTime = x.Start, EndTime = x.End, IsBlocked = x.IsBlocked, MaxCapacity = x.Capacity, CreatedBy = userId,
        }).ToList();

    /// <summary>ตารางงานรายบุคคล (DTO) → แถว <c>SiteBookingResourceSlot</c></summary>
    public static List<SiteBookingResourceSlot> BuildResourceSlotRows(Guid companyId, Guid resourceId, BookingScheduleDto schedule, string? userId)
        => ParseSchedule(schedule, 1, "เวลางาน").Select(x => new SiteBookingResourceSlot
        {
            CompanyId = companyId, ResourceId = resourceId, DayOfWeek = x.DayOfWeek, SpecificDate = x.SpecificDate,
            StartTime = x.Start, EndTime = x.End, IsBlocked = x.IsBlocked, CreatedBy = userId,
        }).ToList();

    public async Task<BookingServiceResponse?> GetServiceAsync(Guid companyId, Guid siteId, Guid serviceId)
    {
        var svc = await _db.SiteBookingServices.AsNoTracking().Include(s => s.Slots)
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.SiteId == siteId && s.CompanyId == companyId && !s.IsDeleted);
        if (svc == null) return null;
        var counts = await UpcomingCountsAsync(companyId, siteId, new[] { svc.Id });
        var site = await SiteDefaultsAsync(companyId, siteId);
        var briefs = await ResourceBriefsAsync(companyId, siteId, new[] { svc.Id });
        return MapService(svc, counts.GetValueOrDefault(svc.Id), site, briefs.GetValueOrDefault(svc.Id) ?? new());
    }

    /// <summary>รายการบริการจองคิวของเว็บ — <b>อ่านอย่างเดียว</b> (endpoint สาธารณะ <c>GET …/storefront/booking-services</c> ใช้ตัวนี้)
    /// <para>รอบ 202 ทีม LW (W-03): เดิมถ้าเว็บยังไม่มีบริการ ตัวนี้ "สร้างบริการเริ่มต้น ฿0" ลงฐานเองทุกครั้งที่ผู้เยี่ยมชมเปิดหน้า
    /// (+ <c>catch {}</c> กลืน error) ⇒ เว็บที่พักที่ยังไม่ผูกที่พักได้การ์ดนัดหมาย ฿0 แทนระบบจองห้อง · เมนู "การจองคิว" โผล่เพราะ
    /// <c>CmsModuleResolver</c> นับแถวที่ระบบสร้างเองเป็นหลักฐาน · GET สาธารณะเขียนฐานได้โดยไม่ล็อกอิน.
    /// บริการตัวอย่างถูก seed ตอนสร้างเว็บ/เติมเทมเพลตแล้ว (<c>CmsBookingServiceSeeder</c> — เฉพาะเว็บที่มีหน้าจองคิว) ·
    /// ว่าง = ว่างจริง (หน้าเว็บบอก "ยังไม่มีบริการ" · เว็บที่พักบอก "ยังไม่เปิดจองออนไลน์") · ข้อมูลเดิมที่ถูกสร้างไว้:
    /// <c>LodgingSiteSeedMigration.AutoSeedServiceCleanupSql</c></para>
    /// <para>ทีม B: คืนค่าตั้งครบทุกฟิลด์ + ตารางเวลา (หน้า CMS hydrate ฟอร์มแก้ไขจากตัวนี้ — เดิมคืนแค่ชื่อ/ราคา/ระยะเวลา ⇒ เปิดแก้แล้วบันทึก
    /// buffer/มัดจำ/ชนิดการจอง/ยืนยันอัตโนมัติ ถูกรีเซ็ตเงียบ ๆ) · <paramref name="includeInactive"/> = หน้า CMS (ผู้เรียกที่ล็อกอิน) เห็นบริการที่ปิดไว้ด้วย</para></summary>
    public async Task<List<BookingServiceResponse>> GetServicesAsync(Guid companyId, Guid siteId, bool includeInactive = false)
    {
        var list = await _db.SiteBookingServices.AsNoTracking().Include(s => s.Slots)
            .Where(s => s.SiteId == siteId && s.CompanyId == companyId && !s.IsDeleted && (includeInactive || s.IsActive))
            .OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt)
            .ToListAsync();
        var ids = list.Select(s => s.Id).ToList();
        var counts = await UpcomingCountsAsync(companyId, siteId, ids);
        var site = await SiteDefaultsAsync(companyId, siteId);
        var briefs = await ResourceBriefsAsync(companyId, siteId, ids);
        return list.Select(s => MapService(s, counts.GetValueOrDefault(s.Id), site, briefs.GetValueOrDefault(s.Id) ?? new())).ToList();
    }

    public IReadOnlyList<BookingServicePresetResponse> GetPresets() => Cms.CmsBookingServiceSeeder.PresetResponses();

    private async Task<Dictionary<Guid, int>> UpcomingCountsAsync(Guid companyId, Guid siteId, IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return new();
        var today = BookingSlotEngine.BangkokNow(DateTime.UtcNow).Date;
        return await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && ids.Contains(b.BookingServiceId)
                && b.BookingDate >= today && b.Status != BookingStatus.Cancelled && b.Status != BookingStatus.Waitlisted)
            .GroupBy(b => b.BookingServiceId)
            .Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);
    }

    /// <summary>ผู้ให้บริการที่เปิดใช้และทำบริการนั้นได้ ต่อบริการ (ชื่อ/ชนิด/รูปเท่านั้น)</summary>
    private async Task<Dictionary<Guid, List<BookingResourceBrief>>> ResourceBriefsAsync(Guid companyId, Guid siteId, IReadOnlyCollection<Guid> serviceIds)
    {
        if (serviceIds.Count == 0) return new();
        var rows = await _db.SiteBookingResourceServices.AsNoTracking()
            .Where(l => l.CompanyId == companyId && serviceIds.Contains(l.BookingServiceId) && !l.IsDeleted
                && l.Resource.SiteId == siteId && l.Resource.IsActive && !l.Resource.IsDeleted)
            .OrderBy(l => l.Resource.SortOrder).ThenBy(l => l.Resource.Name)
            .Select(l => new { l.BookingServiceId, l.Resource.Id, l.Resource.Name, l.Resource.Kind, l.Resource.ImageUrl })
            .ToListAsync();
        return rows.GroupBy(x => x.BookingServiceId)
            .ToDictionary(g => g.Key, g => g.Select(x => new BookingResourceBrief { Id = x.Id, Name = x.Name, Kind = x.Kind, ImageUrl = x.ImageUrl }).ToList());
    }

    private static BookingServiceResponse MapService(SiteBookingService s, int upcoming, BookingSiteDefaults site, List<BookingResourceBrief> resources)
    {
        var rules = BookingSlotEngine.RulesOf(s, site);
        var slots = s.Slots.Where(x => x.IsActive).ToList();
        return new BookingServiceResponse
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
            SlotMode = s.SlotMode, SlotIntervalMinutes = s.SlotIntervalMinutes,
            BufferBeforeMinutes = s.BufferBeforeMinutes, CapacityUnit = s.CapacityUnit,
            MaxUnitsPerBooking = s.MaxUnitsPerBooking, HalfDaySplitTime = s.HalfDaySplitTime,
            CapacityGroup = s.CapacityGroup,
            EnforceSlotCapacity = s.EnforceSlotCapacity, OverlapLimit = s.OverlapLimit, WaitlistEnabled = s.WaitlistEnabled,
            ResourceMode = s.ResourceMode, UseSiteDefaults = s.UseSiteDefaults,
            EffectiveDurationMinutes = rules.DurationMinutes,
            Resources = s.ResourceMode == BookingResourceMode.None ? new() : resources,
            Schedule = ScheduleDtoOf(slots.Select(BookingSlotEngine.RowOf), s.MaxCapacity),
            HasOpeningHours = slots.Any(x => !x.IsBlocked),
            SlotSummary = BookingSlotEngine.Summary(rules),
            PolicySummary = BookingSlotEngine.PolicySummary(rules, s.DepositAmount, s.DepositPercent, s.FreeCancellationHours, s.CancellationFeePercent),
            IsActive = s.IsActive,
            UpcomingBookingsCount = upcoming,
            CreatedAt = s.CreatedAt
        };
    }

    // ===== ค่าตั้งการจองระดับไซต์ =====

    private async Task<BookingSiteDefaults> SiteDefaultsAsync(Guid companyId, Guid siteId)
        => BookingSlotEngine.DefaultsOf(await _db.SiteBookingSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted));

    public async Task<BookingSiteSettingsDto> GetSiteSettingsAsync(Guid companyId, Guid siteId)
    {
        var d = await SiteDefaultsAsync(companyId, siteId);
        return new BookingSiteSettingsDto
        {
            DurationMinutes = d.DurationMinutes, BufferBeforeMinutes = d.BufferBeforeMinutes, BufferAfterMinutes = d.BufferAfterMinutes,
            MinAdvanceHours = d.MinAdvanceHours, MaxAdvanceDays = d.MaxAdvanceDays,
            GuaranteedInvoiceTiming = await InvoiceTimingAsync(companyId, siteId),
            GuaranteedInvoiceTimingOptions = BookingInvoiceTimingPolicy.Options().ToList(),
        };
    }

    /// <summary>จังหวะออกใบกำกับของ Guaranteed ของไซต์ (คำตัดสินข้อ 155) · ไม่มีแถวค่าตั้ง = ค่าเริ่มต้น (ใบกำกับตอนจอง — เดิม)</summary>
    private async Task<BookingInvoiceTiming> InvoiceTimingAsync(Guid companyId, Guid siteId)
        => await _db.SiteBookingSettings.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted)
            .Select(x => (BookingInvoiceTiming?)x.GuaranteedInvoiceTiming)
            .FirstOrDefaultAsync() ?? BookingInvoiceTiming.TaxInvoiceAtBooking;

    public async Task<BookingSiteSettingsDto> UpdateSiteSettingsAsync(Guid companyId, Guid siteId, BookingSiteSettingsDto request, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var errs = BookingSlotEngine.ValidateRules(new BookingSlotRules
        {
            Mode = BookingSlotMode.Interval, DurationMinutes = request.DurationMinutes,
            BufferBeforeMinutes = request.BufferBeforeMinutes, BufferAfterMinutes = request.BufferAfterMinutes,
            MinAdvanceHours = request.MinAdvanceHours, MaxAdvanceDays = request.MaxAdvanceDays,
        }).ToList();
        if (request.GuaranteedInvoiceTiming is BookingInvoiceTiming t && !Enum.IsDefined(t))
            errs.Add("จังหวะออกใบกำกับภาษีไม่ถูกต้อง");
        if (errs.Count > 0) throw new BusinessRuleException(string.Join(" · ", errs), "CMS-BOOKING-SETTINGS");
        var row = await _db.SiteBookingSettings.FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        if (row == null)
        {
            row = new SiteBookingSettings { CompanyId = companyId, SiteId = siteId, CreatedBy = userId };
            _db.SiteBookingSettings.Add(row);
        }
        row.DurationMinutes = request.DurationMinutes; row.BufferBeforeMinutes = request.BufferBeforeMinutes;
        row.BufferAfterMinutes = request.BufferAfterMinutes; row.MinAdvanceHours = request.MinAdvanceHours;
        row.MaxAdvanceDays = request.MaxAdvanceDays; row.UpdatedBy = userId;
        // null = คงค่าเดิม (ผู้เรียกที่ไม่รู้จักช่องนี้ไม่รีเซ็ตเงียบ) · มีผลกับการจองใหม่เท่านั้น — การจองเดิมตัดสินจากเอกสารที่ผูกจริง (BookingInvoiceTimingPolicy)
        if (request.GuaranteedInvoiceTiming is BookingInvoiceTiming timing) row.GuaranteedInvoiceTiming = timing;
        await _db.SaveChangesAsync();
        return await GetSiteSettingsAsync(companyId, siteId);
    }

    // ===== ผู้ให้บริการ / ห้อง / อุปกรณ์ =====

    public async Task<List<BookingResourceResponse>> GetResourcesAsync(Guid companyId, Guid siteId)
    {
        var list = await _db.SiteBookingResources.AsNoTracking().Include(r => r.Services).Include(r => r.Slots)
            .Where(r => r.CompanyId == companyId && r.SiteId == siteId && !r.IsDeleted)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name)
            .ToListAsync();
        return list.Select(MapResource).ToList();
    }

    private static BookingResourceResponse MapResource(SiteBookingResource r) => new()
    {
        Id = r.Id, Name = r.Name, Kind = r.Kind, Description = r.Description, ImageUrl = r.ImageUrl,
        Capacity = r.Capacity, SortOrder = r.SortOrder, IsActive = r.IsActive,
        ServiceIds = r.Services.Where(x => !x.IsDeleted).Select(x => x.BookingServiceId).ToList(),
        Schedule = ScheduleDtoOf(r.Slots.Select(BookingSlotEngine.RowOf), 1),
    };

    public async Task<BookingResourceResponse> SaveResourceAsync(Guid companyId, Guid siteId, Guid? resourceId, BookingResourceRequest request, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var name = (request.Name ?? "").Trim();
        if (name.Length == 0) throw new BusinessRuleException("กรุณากรอกชื่อผู้ให้บริการ/ห้อง/อุปกรณ์", "CMS-BOOKING-RESOURCE");
        if (request.Capacity < 1 || request.Capacity > 1000)
            throw new BusinessRuleException("รับงานพร้อมกันต้องอยู่ระหว่าง 1–1,000", "CMS-BOOKING-RESOURCE");
        var serviceIds = (request.ServiceIds ?? new()).Distinct().ToList();
        // บริการที่อ้างต้องเป็นของไซต์/บริษัทนี้ (กันผูกข้ามบริษัทด้วย id ที่เดามา)
        var validIds = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted && serviceIds.Contains(s.Id))
            .Select(s => s.Id).ToListAsync();
        if (validIds.Count != serviceIds.Count)
            throw new BusinessRuleException("มีบริการที่เลือกไม่อยู่ในเว็บไซต์นี้", "CMS-BOOKING-RESOURCE");

        SiteBookingResource res;
        if (resourceId is Guid rid)
        {
            res = await _db.SiteBookingResources.Include(r => r.Services).Include(r => r.Slots)
                .FirstOrDefaultAsync(r => r.Id == rid && r.CompanyId == companyId && r.SiteId == siteId && !r.IsDeleted)
                ?? throw new KeyNotFoundException("Booking resource not found.");
            res.UpdatedBy = userId;
        }
        else
        {
            res = new SiteBookingResource { CompanyId = companyId, SiteId = siteId, CreatedBy = userId };
            _db.SiteBookingResources.Add(res);
        }
        var slots = BuildResourceSlotRows(companyId, res.Id, request.Schedule ?? new(), userId);
        res.Name = name; res.Kind = request.Kind; res.Description = request.Description;
        res.ImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl) ? null : request.ImageUrl.Trim();
        res.Capacity = request.Capacity; res.SortOrder = request.SortOrder; res.IsActive = request.IsActive;

        _db.SiteBookingResourceServices.RemoveRange(res.Services.Where(x => !validIds.Contains(x.BookingServiceId)).ToList());
        foreach (var sid in validIds.Where(id => res.Services.All(x => x.BookingServiceId != id)))
            _db.SiteBookingResourceServices.Add(new SiteBookingResourceService { CompanyId = companyId, ResourceId = res.Id, BookingServiceId = sid, CreatedBy = userId });
        _db.SiteBookingResourceSlots.RemoveRange(res.Slots.ToList());
        _db.SiteBookingResourceSlots.AddRange(slots);
        await _db.SaveChangesAsync();

        var saved = await _db.SiteBookingResources.AsNoTracking().Include(r => r.Services).Include(r => r.Slots)
            .FirstAsync(r => r.Id == res.Id && r.CompanyId == companyId);
        return MapResource(saved);
    }

    /// <summary>ลบผู้ให้บริการ (soft) — การจองเดิมยังอ้าง ResourceId เดิมได้ (ประวัติ) แต่ไม่ถูกจัดงานใหม่</summary>
    public async Task<bool> DeleteResourceAsync(Guid companyId, Guid siteId, Guid resourceId)
    {
        var res = await _db.SiteBookingResources.FirstOrDefaultAsync(r => r.Id == resourceId && r.CompanyId == companyId && r.SiteId == siteId && !r.IsDeleted);
        if (res == null) return false;
        res.IsDeleted = true; res.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteServiceAsync(Guid companyId, Guid siteId, Guid serviceId)
    {
        var svc = await _db.SiteBookingServices.FirstOrDefaultAsync(s => s.Id == serviceId && s.SiteId == siteId && s.CompanyId == companyId);
        if (svc == null) return false;
        svc.IsActive = false;
        svc.IsDeleted = true;
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Slots =====

    public async Task<BookingSlotResponse> CreateSlotAsync(Guid companyId, Guid siteId, Guid serviceId, CreateBookingSlotRequest request, string userId)
    {
        var svc = await _db.SiteBookingServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.SiteId == siteId && s.CompanyId == companyId && !s.IsDeleted)
            ?? throw new KeyNotFoundException("Booking service not found.");
        var start = BookingSlotEngine.ParseTime(request.StartTime)
            ?? throw new BusinessRuleException("เวลาเริ่มต้องเป็นรูปแบบ HH:mm", "CMS-BOOKING-SCHEDULE");
        var end = BookingSlotEngine.ParseTime(request.EndTime)
            ?? throw new BusinessRuleException("เวลาจบต้องเป็นรูปแบบ HH:mm", "CMS-BOOKING-SCHEDULE");
        DateOnly? date = null;
        if (!string.IsNullOrEmpty(request.SpecificDate))
        {
            if (!DateOnly.TryParseExact(request.SpecificDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
                throw new BusinessRuleException("วันที่ต้องเป็นรูปแบบ yyyy-MM-dd", "CMS-BOOKING-SCHEDULE");
            date = d;
        }
        var slot = new SiteBookingSlot
        {
            CompanyId = companyId, BookingServiceId = svc.Id,
            DayOfWeek = request.DayOfWeek,
            StartTime = start, EndTime = end, SpecificDate = date,
            IsBlocked = request.IsBlocked, MaxCapacity = Math.Max(1, request.MaxCapacity),
            CreatedBy = userId
        };
        var slotErrs = BookingSlotEngine.ValidateSchedule(new[] { BookingSlotEngine.RowOf(slot) });
        if (slotErrs.Count > 0)
            throw new BusinessRuleException(string.Join(" · ", slotErrs), "CMS-BOOKING-SCHEDULE");
        _db.SiteBookingSlots.Add(slot);
        await _db.SaveChangesAsync();

        return MapSlotResponse(slot);
    }

    public async Task<List<BookingSlotResponse>> GetSlotsAsync(Guid companyId, Guid siteId, Guid serviceId)
    {
        var slots = await _db.SiteBookingSlots.AsNoTracking()
            .Where(s => s.BookingServiceId == serviceId && s.CompanyId == companyId && s.IsActive
                && s.BookingService.SiteId == siteId)
            .OrderBy(s => s.DayOfWeek).ThenBy(s => s.StartTime)
            .ToListAsync();

        return slots.Select(MapSlotResponse).ToList();
    }

    public async Task<bool> DeleteSlotAsync(Guid companyId, Guid siteId, Guid serviceId, Guid slotId)
    {
        var slot = await _db.SiteBookingSlots.FirstOrDefaultAsync(s => s.Id == slotId && s.BookingServiceId == serviceId
            && s.CompanyId == companyId && s.BookingService.SiteId == siteId);
        if (slot == null) return false;
        _db.SiteBookingSlots.Remove(slot);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Available Slots (ตัวตัดสินเดียว: Helpers/BookingSlotEngine) =====

    /// <summary>ข้อมูลที่ engine ต้องใช้ตัดสินหนึ่งวันของบริการหนึ่งตัว</summary>
    private sealed record DayContext(BookingSlotRules Rules, List<BookingScheduleRow> Rows, List<BookingOccupancy> Occupied,
        List<BookingResourceInfo> Resources);

    /// <summary>ตารางเวลา + การจองที่ใช้ทรัพยากรเดียวกันของวันนั้น — บริการเดียวกัน · บริการในกลุ่มทรัพยากรร่วม (<c>CapacityGroup</c>) ·
    /// การจองใด ๆ ของผู้ให้บริการที่ทำบริการนี้ได้ (ผู้ให้บริการคือของที่ใช้ร่วมข้ามบริการ) · ไม่นับที่ยกเลิก/คิวสำรอง ·
    /// buffer ของแต่ละการจองมาจากบริการของการจองนั้น · ผู้ให้บริการ = เปิดใช้ + ผูกบริการนี้ + ตารางงานของตัวเอง</summary>
    private async Task<DayContext> LoadDayAsync(Guid companyId, Guid siteId, SiteBookingService svc, DateOnly date)
    {
        var site = await SiteDefaultsAsync(companyId, siteId);
        var rules = BookingSlotEngine.RulesOf(svc, site);
        var rows = await _db.SiteBookingSlots.AsNoTracking()
            .Where(s => s.BookingServiceId == svc.Id && s.CompanyId == companyId && s.IsActive)
            .ToListAsync();

        var resources = new List<BookingResourceInfo>();
        if (svc.ResourceMode != BookingResourceMode.None)
        {
            var res = await _db.SiteBookingResources.AsNoTracking().Include(r => r.Slots)
                .Where(r => r.CompanyId == companyId && r.SiteId == siteId && r.IsActive && !r.IsDeleted
                    && r.Services.Any(l => l.BookingServiceId == svc.Id && !l.IsDeleted))
                .ToListAsync();
            resources = res.Select(r => new BookingResourceInfo(r.Id, r.Name, Math.Max(1, r.Capacity), r.SortOrder,
                r.Slots.Where(x => !x.IsDeleted).Select(BookingSlotEngine.RowOf).ToList())).ToList();
        }
        var resIds = resources.Select(r => r.Id).ToList();

        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var group = svc.CapacityGroup;
        var poolIds = group == null ? new List<Guid> { svc.Id }
            : await _db.SiteBookingServices.AsNoTracking()
                .Where(x => x.CompanyId == companyId && x.SiteId == siteId && x.CapacityGroup == group && !x.IsDeleted)
                .Select(x => x.Id).ToListAsync();
        if (!poolIds.Contains(svc.Id)) poolIds.Add(svc.Id);
        var occupied = (await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId
                && b.BookingDate >= dayStart && b.BookingDate < dayEnd
                && b.Status != BookingStatus.Cancelled && b.Status != BookingStatus.Waitlisted
                && (poolIds.Contains(b.BookingServiceId)
                    || (b.ResourceId != null && resIds.Contains(b.ResourceId.Value))))
            .Select(b => new { b.StartTime, b.EndTime, b.GuestCount, b.BookingServiceId, b.ResourceId,
                b.BookingService.UseSiteDefaults, b.BookingService.BufferBeforeMinutes, b.BookingService.BufferMinutes })
            .ToListAsync())
            // buffer ของบริการของการจองนั้น — บริการที่ใช้ค่าไซต์ได้ buffer ของไซต์ (สูตรเดียวกับ RulesOf)
            .Select(b => new BookingOccupancy(b.StartTime, b.EndTime, b.GuestCount,
                b.UseSiteDefaults ? site.BufferBeforeMinutes : b.BufferBeforeMinutes,
                b.UseSiteDefaults ? site.BufferAfterMinutes : b.BufferMinutes, b.BookingServiceId, b.ResourceId))
            .ToList();
        // ฝั่งความจุของบริการ/กลุ่ม (ไม่ใช้ผู้ให้บริการ): การจองของบริการอื่นที่มาเพราะ "ใช้ผู้ให้บริการร่วม" ไม่ใช่คิวของบริการนี้
        if (resources.Count == 0 || !rules.EnforceCapacity)
            occupied = occupied.Where(b => b.ServiceId is Guid sid && poolIds.Contains(sid)).ToList();
        return new DayContext(rules, rows.Select(BookingSlotEngine.RowOf).ToList(), occupied, resources);
    }

    public async Task<List<AvailableSlotResponse>> GetAvailableSlotsAsync(Guid companyId, Guid siteId, AvailableSlotsRequest request)
    {
        var svc = await _db.SiteBookingServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.BookingServiceId && s.SiteId == siteId && s.CompanyId == companyId
                && s.IsActive && !s.IsDeleted);
        if (svc == null) return new();

        var date = DateOnly.FromDateTime(request.Date);
        var ctx = await LoadDayAsync(companyId, siteId, svc, date);
        var units = Math.Max(1, request.Units);
        var requested = svc.ResourceMode == BookingResourceMode.Auto ? null : request.ResourceId;
        if (requested is Guid want && ctx.Resources.All(r => r.Id != want)) return new();
        var names = ctx.Resources.ToDictionary(r => r.Id, r => r.Name);
        return BookingSlotEngine.Available(ctx.Rules, date, ctx.Rows, ctx.Occupied, BookingSlotEngine.BangkokNow(DateTime.UtcNow),
                Math.Max(1, request.GuestCount), units, ctx.Resources, requested, includeFull: svc.WaitlistEnabled)
            .Select(o => new AvailableSlotResponse
            {
                StartTime = BookingSlotEngine.Hhmm(o.StartMinute),
                EndTime = BookingSlotEngine.Hhmm(o.EndMinute),
                AvailableCapacity = o.Remaining,
                Label = o.Label,
                Price = BookingSlotEngine.TotalPrice(ctx.Rules, svc.Price, units),
                IsFull = o.IsFull,
                // โหมดบังคับระบุ/ลูกค้าเลือก แสดงชื่อคนที่จะได้ · ระบบจัดให้ ไม่ประกาศชื่อล่วงหน้า (ตัดสินใหม่ตอนจอง)
                ResourceName = svc.ResourceMode != BookingResourceMode.Auto && o.ResourceId is Guid rid ? names.GetValueOrDefault(rid) : null,
            })
            .ToList();
    }

    // ===== Bookings =====

    /// <summary>สร้างการจอง — <b>server เป็นผู้ตัดสิน</b>: เวลา/วันต้องเป็นรอบที่ <see cref="BookingSlotEngine.Decide"/> ยอมรับ (เวลาทำการ · วันหยุด ·
    /// ล่วงหน้าขั้นต่ำ/สูงสุด · ตรงรอบ · ความจุพร้อม buffer · ผู้ให้บริการที่ว่างและรับงานนี้ได้) · เวลาจบ/ระยะ/ยอด/มัดจำ/ผู้ให้บริการมาจาก server ·
    /// เต็ม + บริการเปิดคิวสำรอง ⇒ บันทึกเป็นคิวสำรอง (<see cref="BookingStatus.Waitlisted"/> · ไม่ออกเอกสาร ERP) · ตัดสิน + ออกเลข + บันทึก อยู่ใต้
    /// <c>pg_advisory_xact_lock</c> ของไซต์ (ผู้ให้บริการใช้ร่วมข้ามบริการ ⇒ ล็อกระดับไซต์) ⇒ สองคนกดรอบสุดท้ายพร้อมกันได้คนเดียว ·
    /// ขั้น ERP รันหลัง commit (เส้นเอกสารเปิดธุรกรรมของตัวเอง)</summary>
    public async Task<BookingResponse> CreateBookingAsync(Guid companyId, Guid siteId, CreateBookingRequest request, string? userId)
    {
        var svc = await _db.SiteBookingServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.BookingServiceId && s.SiteId == siteId && s.CompanyId == companyId
                && s.IsActive && !s.IsDeleted)
            ?? throw new KeyNotFoundException("Booking service not found or inactive.");

        var start = BookingSlotEngine.ParseTime(request.StartTime)
            ?? throw new BusinessRuleException("กรุณาเลือกเวลาจากรายการเวลาว่าง", "CMS-BOOKING-INVALID");
        var date = DateOnly.FromDateTime(request.BookingDate);
        var units = request.Units;
        var guests = request.GuestCount;

        SiteBooking booking;
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            await LockSiteBookingsAsync(companyId, siteId);

            var ctx = await LoadDayAsync(companyId, siteId, svc, date);
            var requested = svc.ResourceMode == BookingResourceMode.Auto ? null : request.ResourceId;
            var decision = BookingSlotEngine.Decide(ctx.Rules, date, start, ctx.Rows, ctx.Occupied,
                BookingSlotEngine.BangkokNow(DateTime.UtcNow), guests, units, ctx.Resources, requested);
            var waitlist = decision.CanWaitlist(ctx.Rules);
            if (!decision.Allowed && !waitlist)
                throw new BusinessRuleException(decision.Message ?? "จองรอบนี้ไม่ได้",
                    "CMS-BOOKING-" + decision.Reason.ToString().ToUpperInvariant(),
                    decision.Reason == BookingSlotRejection.Full ? 409 : 400);
            var slot = decision.Slot!;

            var total = BookingSlotEngine.TotalPrice(ctx.Rules, svc.Price, units);
            var depositAmount = BookingSlotEngine.Deposit(total, svc.DepositAmount, svc.DepositPercent);

            booking = new SiteBooking
            {
                CompanyId = companyId, SiteId = siteId,
                BookingServiceId = svc.Id,
                CustomerId = request.CustomerId,
                BookingNumber = await GenerateBookingNumber(companyId, siteId),
                BookingDate = date.ToDateTime(TimeOnly.MinValue),
                StartTime = slot.Start, EndTime = slot.BookedEnd,
                DurationMinutes = slot.BookedMinutes,
                Units = units,
                // คิวสำรอง: เก็บผู้ให้บริการที่ลูกค้าขอ (ถ้ามี) — จัดจริงตอนเลื่อนขึ้น · จองได้: ผู้ให้บริการที่ engine จัดให้
                ResourceId = waitlist ? requested : slot.ResourceId,
                GuestName = request.GuestName, GuestEmail = request.GuestEmail,
                GuestPhone = request.GuestPhone, GuestCount = guests,
                TotalAmount = total, DepositAmount = depositAmount,
                Currency = svc.Currency,
                CustomerNotes = request.CustomerNotes,
                CreatedBy = userId
            };

            if (waitlist) booking.Status = BookingStatus.Waitlisted;
            // Auto-confirm for Lead bookings or if service allows it
            else if (svc.AutoConfirm || svc.BookingType == BookingType.Lead)
            {
                booking.Status = BookingStatus.Confirmed;
                booking.ConfirmedAt = DateTime.UtcNow;
            }

            // R-39: จ่ายด้วยบัตรคอร์ส ⇒ ยอดเงิน 0 + กันเครดิตในธุรกรรมเดียวกับ Decide (ล้ม = ไม่มีการจอง) — CmsBookingService.Credits.cs
            var creditContactId = await PrepareCreditBookingAsync(companyId, booking, request);
            _db.SiteBookings.Add(booking);
            await _db.SaveChangesAsync();
            if (creditContactId is Guid creditCid)
                await HoldCreditForBookingAsync(companyId, svc, booking, creditCid, request.CreditPassId, total, userId);
            await tx.CommitAsync();
        }

        _logger.LogInformation("Booking {BookingNumber} created for service {ServiceName} ({Status})", booking.BookingNumber, svc.Name, booking.Status);

        // Auto-sync to ERP based on booking type
        // รอบ 205 ทีม E (BOOKING_REVENUE_AUDIT B-3): ขั้น ERP ล้ม (โควตาใบเสนอราคาเต็ม · ผู้ติดต่อ · ด่านเอกสาร) ⇒ เดิมโยนขึ้นไปหาแขกทั้งที่การจอง
        // บันทึกแล้ว ⇒ แขกเห็น "จองไม่สำเร็จ" แล้วกดจองซ้ำ (การจองซ้อน) · LODGING_LICENSING_PLAN §5 "สร้างการจอง ไม่จำกัด ไม่ gate" ⇒
        // การจองสำเร็จเสมอ + ล้มดัง: หมายเหตุบนการจอง + log Error + ErpNotices ในคำตอบ (ถอยของครึ่งทางออกจาก context ก่อนบันทึกหมายเหตุ)
        var createNotices = new List<string>();
        // คิวสำรองยังไม่มีบริการให้ ⇒ ไม่ออกเอกสารจนกว่าจะถูกเลื่อนขึ้น (ทีม B)
        // R-39: การจองที่จ่ายด้วยบัตรคอร์สไม่ออกเอกสาร ERP (รายได้มาจาก JE รับรู้ของบัตร — CreditBookingLink.ShouldSyncErp)
        if (svc.BookingType != BookingType.Lead && booking.Status != BookingStatus.Waitlisted
            && CreditBookingLink.ShouldSyncErp(request.PayWithCredit))
        {
            try { await SyncBookingToErpAsync(companyId, siteId, booking.Id); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Booking {BookingNumber}: สร้างเอกสาร ERP อัตโนมัติไม่สำเร็จ — การจองบันทึกแล้ว", booking.BookingNumber);
                _db.ChangeTracker.Clear();
                var noted = await _db.SiteBookings.FirstAsync(b => b.Id == booking.Id && b.CompanyId == companyId);
                // ฝ่ายค้านรอบ 205 ข้อ 5: ล้มหลังเส้นเอกสาร commit ใบไปแล้ว ⇒ ผูกใบที่สร้างแล้วกลับ (กันใบกำพร้า/ออกซ้ำ) + บอกเลขใบในหมายเหตุ
                var orphan = noted.ErpDocumentId == null
                    ? await _db.Documents.AsNoTracking()
                        .Where(d => d.CompanyId == companyId && !d.IsDeleted && d.BookingNumber == noted.BookingNumber)
                        .OrderBy(d => d.CreatedAt)
                        .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType })
                        .FirstOrDefaultAsync()
                    : null;
                if (orphan != null)
                {
                    noted.ErpDocumentId = orphan.Id;
                    noted.ErpDocumentType = CmsBookingErpPolicy.ErpTypeOf(orphan.DocumentType);
                }
                var failNote = CmsBookingErpPolicy.SyncFailedNote(ex.Message, orphan?.DocumentNumber);
                noted.InternalNotes = DepositPolicyResolver.AppendNoteOnce(noted.InternalNotes, failNote);
                await _db.SaveChangesAsync();
                // แขกไม่ล็อกอิน (เส้นหน้าเว็บ) ไม่เห็นข้อความภายใน — ข้อความอยู่บนการจองให้พนักงานเห็น · พนักงานที่สร้างเองเห็นในคำตอบ
                if (userId != null) createNotices.Add(failNote);
            }
        }

        var created = await GetBookingAsync(companyId, siteId, booking.Id) ?? throw new InvalidOperationException("Failed.");
        created.ErpNotices = createNotices;
        return created;
    }

    /// <summary>ล็อก "การใช้รอบของไซต์นี้" (ตัดสินแล้วเขียน) — ภายใน transaction ของผู้เรียก</summary>
    private async Task LockSiteBookingsAsync(Guid companyId, Guid siteId)
    {
        var slotLockKey = AdvisoryLockKey.For(companyId, AdvisoryLockKey.CmsBookingSlot, siteId.ToString("N"));
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", slotLockKey);
    }

    /// <summary>
    /// เลื่อนคิวสำรองขึ้นหลังมีที่ว่าง (ยกเลิก/ไม่มา) — ไล่คิวของ <b>วันเดียวกันทั้งไซต์</b> ตามลำดับลงคิว (เวลาลงคิว → Id) แล้วให้
    /// <see cref="BookingSlotEngine.Decide"/> ตัดสินใหม่ทีละใบ (ข้ามด่านล่วงหน้าขั้นต่ำ — ลูกค้าลงคิวไว้ก่อนแล้ว) · ผ่าน ⇒ สถานะรอยืนยัน/ยืนยันแล้ว
    /// ตามบริการ + จัดผู้ให้บริการ · ไม่ผ่าน ⇒ อยู่คิวเดิม (ลำดับขยับขึ้นเองเพราะคิดจากใบที่เหลือ) · คืนใบที่ถูกเลื่อน (ผู้เรียกออกเอกสาร ERP ต่อ)
    /// </summary>
    private async Task<List<Guid>> PromoteWaitlistAsync(Guid companyId, Guid siteId, DateTime bookingDate, string userId)
    {
        var promoted = new List<Guid>();
        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockSiteBookingsAsync(companyId, siteId);
        var date = DateOnly.FromDateTime(bookingDate);
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var queue = await _db.SiteBookings
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.Status == BookingStatus.Waitlisted
                && b.BookingDate >= dayStart && b.BookingDate < dayEnd)
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .ToListAsync();
        var now = BookingSlotEngine.BangkokNow(DateTime.UtcNow);
        foreach (var w in queue)
        {
            if (date.ToDateTime(w.StartTime) < now) continue;   // รอบผ่านไปแล้ว — ไม่เลื่อน
            var svc = await _db.SiteBookingServices.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == w.BookingServiceId && s.CompanyId == companyId && s.IsActive && !s.IsDeleted);
            if (svc == null) continue;
            var ctx = await LoadDayAsync(companyId, siteId, svc, date);
            var requested = svc.ResourceMode == BookingResourceMode.Auto ? null : w.ResourceId;
            var d = BookingSlotEngine.Decide(ctx.Rules, date, w.StartTime, ctx.Rows, ctx.Occupied, now,
                w.GuestCount, Math.Max(1, w.Units), ctx.Resources, requested, ignoreAdvance: true);
            if (!d.Allowed) continue;
            w.ResourceId = d.Slot!.ResourceId;
            if (svc.AutoConfirm || svc.BookingType == BookingType.Lead) { w.Status = BookingStatus.Confirmed; w.ConfirmedAt = DateTime.UtcNow; }
            else w.Status = BookingStatus.Pending;
            w.InternalNotes = DepositPolicyResolver.AppendNoteOnce(w.InternalNotes,
                $"เลื่อนจากคิวสำรองอัตโนมัติ {BookingSlotEngine.BangkokNow(DateTime.UtcNow):dd/MM/yyyy HH:mm} — แจ้งลูกค้า");
            w.UpdatedBy = userId;
            await _db.SaveChangesAsync();   // ใบถัดไปต้องเห็นใบนี้เป็นการจองแล้ว (LoadDayAsync อ่านจากฐาน)
            promoted.Add(w.Id);
        }
        await tx.CommitAsync();
        return promoted;
    }

    private async Task EnsureSiteAsync(Guid companyId, Guid siteId)
    {
        if (!await _db.Sites.AnyAsync(s => s.Id == siteId && s.CompanyId == companyId))
            throw new KeyNotFoundException("Site not found.");
    }

    /// <summary>slug ไม่ซ้ำในเว็บ (unique index SiteId+Slug — รวมแถวที่ลบแล้ว) · ชน ⇒ ต่อท้าย -2, -3 …</summary>
    private async Task<string> UniqueSlugAsync(Guid companyId, Guid siteId, string slug, Guid? selfId)
    {
        var taken = (await _db.SiteBookingServices.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.SiteId == siteId && s.CompanyId == companyId && s.Slug.StartsWith(slug) && (selfId == null || s.Id != selfId))
                .Select(s => s.Slug).ToListAsync())
            .ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(slug)) return slug;
        for (var i = 2; ; i++)
        {
            var cand = $"{slug}-{i}";
            if (!taken.Contains(cand)) return cand;
        }
    }

    public async Task<BookingResponse> UpdateBookingStatusAsync(Guid companyId, Guid siteId, Guid bookingId, UpdateBookingStatusRequest request, string userId)
    {
        var booking = await _db.SiteBookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Booking not found.");

        // คิวสำรองตั้งได้โดย engine เท่านั้น (ตอนรอบเต็ม) — ตั้งมือ = การจองที่กินความจุอยู่หายจากการนับโดยไม่มีใครรู้
        if (request.Status == BookingStatus.Waitlisted && booking.Status != BookingStatus.Waitlisted)
            throw new BusinessRuleException("ย้ายการจองเข้าคิวสำรองเองไม่ได้ — คิวสำรองเกิดจากรอบเต็มเท่านั้น", "CMS-BOOKING-WAITLIST-MANUAL");
        var oldStatus = booking.Status;
        booking.Status = request.Status;
        if (request.InternalNotes != null) booking.InternalNotes = request.InternalNotes;
        if (request.CancellationReason != null) booking.CancellationReason = request.CancellationReason;

        switch (request.Status)
        {
            case BookingStatus.Confirmed: booking.ConfirmedAt = DateTime.UtcNow; break;
            case BookingStatus.Completed: booking.CompletedAt = DateTime.UtcNow; break;
            case BookingStatus.Cancelled: booking.CancelledAt = DateTime.UtcNow; break;
        }

        // รอบ 194 (C-1): บันทึกสถานะการจองก่อน — ขั้น ERP ด้านล่างล้มได้ (งวดยื่นแล้ว · ผังไม่ครบ) และต้องไม่ลากสถานะที่ผู้ใช้กดไปด้วย ·
        // ขั้นที่ล้ม ⇒ ล้าง change tracker (กันสภาพครึ่งทางของ void/realize ถูกบันทึกตาม) แล้วประทับหมายเหตุบนการจอง + ตอบผู้เรียก
        booking.UpdatedBy = userId;
        await _db.SaveChangesAsync();

        var notices = new List<string>();       // ตอบผู้กด (BookingResponse.ErpNotices)
        var bookingNotes = new List<string>();  // ประทับบนการจอง (InternalNotes) — ผู้เปิดดูทีหลังเห็น
        // R-39: การจองที่จ่ายด้วยบัตรคอร์ส ⇒ ปรับสมุดเครดิต (ตัด/คืน) แทนเส้นเอกสาร ERP/tax point ของบริการ — CmsBookingService.Credits.cs
        var paidWithCredit = await IsPaidWithCreditAsync(companyId, bookingId);
        if (paidWithCredit)
            await SettleCreditOnStatusAsync(companyId, bookingId, oldStatus, request.Status, userId, notices, bookingNotes);
        if (!paidWithCredit && _docService != null && booking.ErpDocumentId is Guid erpDocId)
        {
            if (request.Status == BookingStatus.Completed)
            {
                // ฝ่ายค้านทีม K ข้อ 1: การจองที่เปิดบิล POS และปิดบิลแล้ว = รายได้รับรู้ที่ POS แล้ว ⇒ ห้ามรับรู้มัดจำซ้ำ (ตัวตัดสิน Helpers/PosBookingIntake)
                var posBill = await _db.PosOrders.AsNoTracking()
                    .Where(o => o.CompanyId == companyId && !o.IsDeleted
                        && o.SourceBookingType == PosBookingSource.CmsBooking && o.SourceBookingId == booking.Id
                        && (o.Status == PosOrderStatus.Completed || o.Status == PosOrderStatus.Refunded))
                    .Select(o => o.OrderNumber).FirstOrDefaultAsync();
                if (PosBookingIntake.MayRealizeDepositOnComplete(posBill != null))
                    await RealizeDepositOnCompleteAsync(companyId, erpDocId, userId, notices, bookingNotes);
                else
                {
                    var note = PosBookingIntake.SkippedDepositRealizationNote(posBill);
                    notices.Add(note);
                    bookingNotes.Add(note);
                }
            }
            else if (request.Status == BookingStatus.Cancelled)
                await SettleErpDocumentOnCancelAsync(companyId, erpDocId, booking.CancelledAt ?? DateTime.UtcNow, notices, bookingNotes);
        }
        // คำตัดสินข้อ 155: ให้บริการเสร็จ = tax point (§78/1) ของ Guaranteed ที่ยังไม่มีใบกำกับ (รวมการจองที่ไม่มีเอกสารผูกเพราะขั้น ERP ตอนจองล้ม) ·
        // ไม่มา ⇒ ไม่ออกใบกำกับเอง แต่บอกทางไปต่อ — ตัดสินที่ BookingInvoiceTimingPolicy (เมธอดออกเองเมื่อไม่มีเส้นเอกสาร)
        if (paidWithCredit) { /* ภาษีขายเกิดครบตอนขายคอร์ส (§78/1) — ไม่เดิน tax point ของบริการซ้ำ */ }
        else if (request.Status == BookingStatus.Completed && oldStatus != BookingStatus.Completed)
            await ApplyInvoiceTimingAsync(companyId, siteId, bookingId, BookingInvoiceEvent.ServiceCompleted, userId, notices, bookingNotes);
        else if (request.Status == BookingStatus.NoShow && oldStatus != BookingStatus.NoShow)
            await ApplyInvoiceTimingAsync(companyId, siteId, bookingId, BookingInvoiceEvent.NoShow, userId, notices, bookingNotes);

        if (bookingNotes.Count > 0)
        {
            var noted = await _db.SiteBookings.FirstAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId);
            foreach (var n in bookingNotes) noted.InternalNotes = DepositPolicyResolver.AppendNoteOnce(noted.InternalNotes, n);
            noted.UpdatedBy = userId;
            await _db.SaveChangesAsync();
        }

        // แอดมินยืนยันใบในคิวสำรองเอง (นอกคิว) ⇒ ออกเอกสาร ERP เหมือนการจองปกติ
        if (oldStatus == BookingStatus.Waitlisted && request.Status is BookingStatus.Pending or BookingStatus.Confirmed)
            await SyncIfBillableAsync(companyId, siteId, bookingId);

        // ที่ว่างเกิดขึ้น (ยกเลิก/ไม่มา ของใบที่กินความจุอยู่) ⇒ เลื่อนคิวสำรองของวันนั้นขึ้น — engine ตัดสินใหม่ทีละใบ
        if (oldStatus != BookingStatus.Waitlisted && oldStatus != BookingStatus.Cancelled
            && request.Status is BookingStatus.Cancelled or BookingStatus.NoShow)
        {
            var promoted = await PromoteWaitlistAsync(companyId, siteId, booking.BookingDate, userId);
            foreach (var id in promoted) await SyncIfBillableAsync(companyId, siteId, id);
            if (promoted.Count > 0) notices.Add($"เลื่อนคิวสำรองขึ้น {promoted.Count} รายการ — แจ้งลูกค้าที่ได้คิว");
        }

        var result = await GetBookingAsync(companyId, siteId, bookingId) ?? throw new InvalidOperationException("Failed.");
        result.ErpNotices = notices;
        return result;
    }

    /// <summary>พนักงานยืนยัน "รับเงินแล้ว" + วันที่รับเงิน ⇒ ออกใบกำกับภาษีของการจอง Guaranteed ที่ยังมีแค่ใบยืนยันการจอง (tax point §78/1 = วันรับเงิน) ·
    /// ตัวตัดสินเดียว <see cref="BookingInvoiceTimingPolicy"/> · ออกแล้ว/บิล POS/สถานะการจองไม่ให้ ⇒ ปฏิเสธ (server — ไม่ใช่แค่ซ่อนปุ่ม) (คำตัดสินข้อ 155)</summary>
    public async Task<BookingResponse> IssueTaxInvoiceOnPaymentAsync(Guid companyId, Guid siteId, Guid bookingId, DateTime? receivedDate, string userId)
    {
        var exists = await _db.SiteBookings.AsNoTracking()
            .AnyAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId);
        if (!exists) throw new KeyNotFoundException("Booking not found.");
        var (paidOn, dateError) = BookingInvoiceTimingPolicy.PaymentDate(receivedDate, DateTime.UtcNow);
        if (dateError != null) throw new BusinessRuleException(dateError, "CMS-BOOKING-PAYMENT-DATE");
        // ตรวจก่อนเข้าล็อก (ข้อความปฏิเสธถึงผู้กดตรง ๆ) — ใต้ล็อกตัดสินซ้ำอีกรอบด้วยตัวเดียวกัน
        var pre = await LoadInvoiceFactsAsync(companyId, siteId, bookingId, BookingInvoiceEvent.PaymentReceived);
        if (pre != null && BookingInvoiceTimingPolicy.Decide(pre.Facts) is { Action: BookingInvoiceAction.Rejected } no)
            throw new BusinessRuleException(no.Notice ?? "ออกใบกำกับจากการจองนี้ไม่ได้", "CMS-BOOKING-ISSUE-TAX", 409);
        var notices = new List<string>();
        var bookingNotes = new List<string>();
        await ApplyInvoiceTimingAsync(companyId, siteId, bookingId, BookingInvoiceEvent.PaymentReceived, userId, notices, bookingNotes, paidOn);
        if (bookingNotes.Count > 0)
        {
            var noted = await _db.SiteBookings.FirstAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId);
            foreach (var n in bookingNotes) noted.InternalNotes = DepositPolicyResolver.AppendNoteOnce(noted.InternalNotes, n);
            noted.UpdatedBy = userId;
            await _db.SaveChangesAsync();
        }
        var result = await GetBookingAsync(companyId, siteId, bookingId) ?? throw new InvalidOperationException("Failed.");
        result.ErpNotices = notices;
        return result;
    }

    /// <summary>ข้อเท็จจริงของเอกสารที่ผูกกับการจอง (ผลของ <see cref="LoadInvoiceFactsAsync"/>)</summary>
    private sealed record BookingInvoiceContext(BookingInvoiceFacts Facts, Guid? LinkedId, string? LinkedNumber,
        Guid? IssuedChildId, DocumentType? IssuedChildType, string? IssuedChildNumber);

    /// <summary>ข้อเท็จจริงของเอกสารที่ผูกกับการจอง → <see cref="BookingInvoiceFacts"/> (ใช้ทั้งตอนตัดสินจริงและตอนตัดสินว่าจะแสดงปุ่มไหม)</summary>
    private async Task<BookingInvoiceContext?> LoadInvoiceFactsAsync(Guid companyId, Guid siteId, Guid bookingId, BookingInvoiceEvent ev)
    {
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == bookingId && x.SiteId == siteId && x.CompanyId == companyId)
            .Select(x => new { x.ErpDocumentId, x.BookingService.BookingType, x.Status })
            .FirstOrDefaultAsync();
        if (b == null) return null;
        DocumentType? linkedType = null; DocumentStatus? linkedStatus = null; string? linkedNo = null;
        Guid? childId = null; DocumentType? childType = null; string? childNo = null; DocumentStatus? childStatus = null;
        if (b.ErpDocumentId is Guid linkedId)
        {
            var d = await _db.Documents.AsNoTracking()
                .Where(x => x.Id == linkedId && x.CompanyId == companyId)
                .Select(x => new { x.DocumentType, x.Status, x.DocumentNumber })
                .FirstOrDefaultAsync();
            if (d != null) { linkedType = d.DocumentType; linkedStatus = d.Status; linkedNo = d.DocumentNumber; }
            // ใบภาษี/ใบรับเงินที่พนักงานแปลงจากใบยืนยันการจองเอง ⇒ ไม่ออกซ้ำ · หาเฉพาะเมื่อใบที่ผูกเป็นใบยืนยันการจอง (ใบกำกับโหมด ก
            // มีใบเสร็จลูกได้ตามปกติ — ห้ามย้ายการผูกไปที่ใบเสร็จ · ฝ่ายค้านทีม N ข้อ 2)
            if (BookingInvoiceTimingPolicy.ShouldLookForIssuedChild(linkedType))
            {
                var issuedTypes = BookingInvoiceTimingPolicy.IssuedTaxDocumentTypes.ToList();
                var c = await _db.Documents.AsNoTracking()
                    .Where(x => x.CompanyId == companyId && !x.IsDeleted && x.RelatedDocumentId == linkedId
                        && issuedTypes.Contains(x.DocumentType)
                        && x.Status != DocumentStatus.Voided && x.Status != DocumentStatus.Rejected)
                    .OrderBy(x => x.CreatedAt)
                    .Select(x => new { x.Id, x.DocumentType, x.DocumentNumber, x.Status })
                    .FirstOrDefaultAsync();
                if (c != null) { childId = c.Id; childType = c.DocumentType; childNo = c.DocumentNumber; childStatus = c.Status; }
            }
        }
        // บิล POS ของการจอง (ทีม K) — ปิดแล้ว = รายได้อยู่ที่ POS · เปิดอยู่ = จะเก็บเงินที่ POS (ฝ่ายค้านทีม N ข้อ 1)
        var posStatuses = await _db.PosOrders.AsNoTracking()
            .Where(o => o.CompanyId == companyId && !o.IsDeleted
                && o.SourceBookingType == PosBookingSource.CmsBooking && o.SourceBookingId == bookingId
                && o.Status != PosOrderStatus.Voided)
            .Select(o => o.Status).ToListAsync();
        var posBill = posStatuses.Any(s => s is PosOrderStatus.Completed or PosOrderStatus.Refunded) ? BookingPosBillState.Closed
            : posStatuses.Count > 0 ? BookingPosBillState.Open : BookingPosBillState.None;
        var (vatRegistered, _) = await CompanyVatStatus.ProfileAsync(_db, companyId);
        var facts = new BookingInvoiceFacts(b.BookingType, await InvoiceTimingAsync(companyId, siteId), ev,
            linkedType, linkedStatus, childId != null, vatRegistered,
            IssuedChildStatus: childStatus, PosBill: posBill, BookingStatus: b.Status);
        return new BookingInvoiceContext(facts, b.ErpDocumentId, linkedNo, childId, childType, childNo);
    }

    /// <summary>จังหวะออกใบกำกับหลังจอง (รับเงิน · ให้บริการเสร็จ · ไม่มา) — ตัดสินที่ <see cref="BookingInvoiceTimingPolicy.Decide"/> ตัวเดียว ·
    /// ออกใบด้วยเส้นแปลงเอกสารเดิม (<c>ConvertDocumentAsync</c>/<c>ConvertDocumentPartialAsync</c>: ใบยืนยันการจอง → ใบกำกับ) + อนุมัติด้วยเส้นเดิม (ไม่เขียน posting ใหม่) ·
    /// ล้ม ⇒ ล้มดัง: ข้อความถึงผู้กด + ประทับบนการจอง (สถานะการจองที่กดไว้บันทึกแล้ว ไม่ถูกลาก)</summary>
    private async Task ApplyInvoiceTimingAsync(Guid companyId, Guid siteId, Guid bookingId, BookingInvoiceEvent ev, string userId,
        List<string> notices, List<string> bookingNotes, DateTime? paidOn = null)
    {
        if (_docService == null) return;
        // ล็อกระดับ session ต่อการจอง (แบบ LodgingService.WithReservationLockAsync) — กด "รับเงินแล้ว" สองแท็บ / พร้อมกดให้บริการเสร็จ
        // ⇒ คำขอที่สองรอแล้วเห็นใบที่ออกแล้ว (AlreadyIssued) ไม่แปลงซ้ำ
        var key = AdvisoryLockKey.For(companyId, AdvisoryLockKey.CmsBookingInvoice, bookingId.ToString());
        var conn = _db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere) await conn.OpenAsync();
        var acquired = false;
        try
        {
            for (var attempt = 0; attempt < 20 && !acquired; attempt++)
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT pg_try_advisory_lock({key})";
                    acquired = Convert.ToBoolean(await cmd.ExecuteScalarAsync());
                }
                if (!acquired) await Task.Delay(500);
            }
            if (!acquired)
            {
                const string busy = "มีรายการออกใบกำกับของการจองนี้กำลังทำงานอยู่ — รอสักครู่แล้วเปิดการจองดูใหม่ (อาจออกไปแล้ว · ห้ามออกเองซ้ำ)";
                notices.Add(busy); bookingNotes.Add(busy);
                return;
            }
            await ApplyInvoiceTimingLockedAsync(companyId, siteId, bookingId, ev, userId, notices, bookingNotes, paidOn);
        }
        finally
        {
            if (acquired)
            {
                await using var unlock = conn.CreateCommand();
                unlock.CommandText = $"SELECT pg_advisory_unlock({key})";
                await unlock.ExecuteScalarAsync();
            }
            if (openedHere) await conn.CloseAsync();
        }
    }

    private async Task ApplyInvoiceTimingLockedAsync(Guid companyId, Guid siteId, Guid bookingId, BookingInvoiceEvent ev, string userId,
        List<string> notices, List<string> bookingNotes, DateTime? paidOn)
    {
        var l = await LoadInvoiceFactsAsync(companyId, siteId, bookingId, ev);
        if (l == null) return;
        var decision = BookingInvoiceTimingPolicy.Decide(l.Facts);
        switch (decision.Action)
        {
            case BookingInvoiceAction.None:
            case BookingInvoiceAction.Rejected:
                if (decision.Notice is string n) { notices.Add(n); bookingNotes.Add(n); }
                return;

            case BookingInvoiceAction.SettledByPosBill:
                // บิล POS ปิดแล้ว ⇒ ไม่ออกใบกำกับ · ใบยืนยันการจอง (ใบเสนอราคา · ไม่มีผลบัญชี) ยกเลิก ⇒ POS/ศูนย์เอกสารไม่เห็นยอดค้างลวง
                if (decision.VoidConfirmation && l.LinkedId is Guid qtId)
                {
                    try { await _docService!.VoidDocumentAsync(companyId, qtId); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _db.ChangeTracker.Clear();
                        _logger.LogWarning(ex, "ยกเลิกใบยืนยันการจอง {Doc} หลังปิดบิล POS ไม่สำเร็จ", l.LinkedNumber);
                        var voidFail = CmsBookingCancelPolicy.FailureNote("การยกเลิกใบยืนยันการจอง", l.LinkedNumber, ReasonOf(ex));
                        notices.Add(voidFail); bookingNotes.Add(voidFail);
                    }
                }
                if (decision.Notice is string pn) { notices.Add(pn); bookingNotes.Add(pn); }
                return;

            case BookingInvoiceAction.AlreadyIssued:
                if (decision.UseIssuedChild && l.IssuedChildId is Guid issuedId)
                {
                    // พนักงานแปลงเองที่หน้าเอกสาร ⇒ ผูกใบนั้นกับการจอง (ยกเลิกการจองจะได้เดินใบที่มีผลจริง)
                    await RelinkBookingAsync(companyId, siteId, bookingId, issuedId, l.IssuedChildType!.Value, userId);
                    if (ev == BookingInvoiceEvent.PaymentReceived) notices.Add(BookingInvoiceTimingPolicy.AlreadyIssuedNotice(l.IssuedChildNumber ?? ""));
                }
                if (decision.Notice is string an) { notices.Add(an); bookingNotes.Add(an); }
                return;

            case BookingInvoiceAction.ApprovePendingTaxInvoice:
                if (decision.UseIssuedChild && l.IssuedChildId is Guid childId)
                {
                    // ใบที่แปลงเองยังเป็นร่าง = ยังไม่ออก (ฝ่ายค้าน ข้อ 4) ⇒ ผูกแล้วอนุมัติ (ล้ม ⇒ แจ้งดังว่าต้องอนุมัติเอง)
                    await RelinkBookingAsync(companyId, siteId, bookingId, childId, l.IssuedChildType!.Value, userId);
                    await ApproveBookingTaxDocumentAsync(companyId, childId, l.IssuedChildNumber, ev, userId, notices, bookingNotes);
                }
                else
                    await ApproveBookingTaxDocumentAsync(companyId, l.LinkedId!.Value, l.LinkedNumber, ev, userId, notices, bookingNotes);
                return;

            case BookingInvoiceAction.CreateTaxInvoiceNow:
                // ไม่มีเอกสารผูก (ขั้น ERP ตอนจองล้ม) ⇒ เส้นสร้างเอกสารของการจองเดิม (อนุมัติอัตโนมัติ + หมายเหตุเมื่อล้ม อยู่ในเส้นนั้นแล้ว)
                try
                {
                    var newId = await SyncBookingToErpAsync(companyId, siteId, bookingId, decision.DocumentType!.Value, paidOn);
                    var made = newId is Guid nid
                        ? await _db.Documents.AsNoTracking().Where(d => d.Id == nid && d.CompanyId == companyId)
                            .Select(d => new { d.DocumentNumber, d.DocumentType, d.Status, d.DocumentDate }).FirstOrDefaultAsync()
                        : null;
                    if (made != null && made.Status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval))
                    {
                        var okMade = BookingInvoiceTimingPolicy.IssuedNotice(ev, made.DocumentNumber, made.DocumentType, ThaiDate.CalendarDateUtc(made.DocumentDate));
                        notices.Add(okMade); bookingNotes.Add(okMade);
                    }
                    else
                    {
                        var pending = CmsBookingCancelPolicy.FailureNote("การออกใบกำกับภาษีของการจอง", made?.DocumentNumber,
                            "ใบยังไม่ได้อนุมัติ — เปิดเอกสารแล้วกด “อนุมัติ” (ดูหมายเหตุบนการจอง)");
                        notices.Add(pending); bookingNotes.Add(pending);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _db.ChangeTracker.Clear();
                    _logger.LogWarning(ex, "สร้างใบกำกับของการจอง {BookingId} (ไม่มีเอกสารผูก) ไม่สำเร็จ", bookingId);
                    var failNew = CmsBookingCancelPolicy.FailureNote("การออกใบกำกับภาษีของการจอง", null, ReasonOf(ex));
                    notices.Add(failNew); bookingNotes.Add(failNew);
                }
                return;

            case BookingInvoiceAction.IssueTaxInvoiceFromConfirmation:
                Models.DTOs.Document.DocumentResponse converted;
                try
                {
                    // เส้นแปลงเดิม: ด่าน §90/2 · ด่านแปลงซ้ำ/ยอดเกินใบต้นทาง · สืบทอดผู้ซื้อ/สาขา/ราคา — ไม่ประกอบใบเอง ·
                    // รับเงิน ⇒ แปลงทุกบรรทัดเต็มจำนวนลงวันที่รับเงินที่ผู้ใช้ระบุ (ฝ่ายค้าน ข้อ 5) · ให้บริการเสร็จ ⇒ วันนี้
                    if (paidOn is DateTime docDate)
                    {
                        var qtLines = await _db.Documents.AsNoTracking()
                            .Where(d => d.Id == l.LinkedId!.Value && d.CompanyId == companyId)
                            .SelectMany(d => d.Lines)
                            .Select(x => new Models.DTOs.Document.PartialConvertLineRequest(x.Id, x.Quantity))
                            .ToListAsync();
                        converted = await _docService!.ConvertDocumentPartialAsync(companyId, l.LinkedId!.Value, decision.DocumentType!.Value,
                            new Models.DTOs.Document.PartialConvertRequest(qtLines, DocumentDate: docDate), userId);
                    }
                    else
                        converted = await _docService!.ConvertDocumentAsync(companyId, l.LinkedId!.Value, decision.DocumentType!.Value, userId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _db.ChangeTracker.Clear();
                    _logger.LogWarning(ex, "ออกใบกำกับของการจอง {BookingId} จากใบยืนยัน {Doc} ไม่สำเร็จ", bookingId, l.LinkedNumber);
                    var fail = CmsBookingCancelPolicy.FailureNote("การออกใบกำกับภาษีของการจอง", l.LinkedNumber, ReasonOf(ex));
                    notices.Add(fail); bookingNotes.Add(fail);
                    return;
                }
                // ผูกใบใหม่ก่อนอนุมัติ — อนุมัติล้ม ⇒ ครั้งถัดไปเห็นใบร่างที่ผูกอยู่ ⇒ ApprovePendingTaxInvoice (ไม่แปลงซ้ำ)
                await RelinkBookingAsync(companyId, siteId, bookingId, converted.Id, decision.DocumentType!.Value, userId);
                await ApproveBookingTaxDocumentAsync(companyId, converted.Id, converted.DocumentNumber, ev, userId, notices, bookingNotes);
                return;

            default:
                return;
        }
    }

    /// <summary>ผูกเอกสารใหม่กับการจอง (ใบกำกับที่ออกจากใบยืนยัน) — ชนิดบนการจองจาก <see cref="CmsBookingErpPolicy.ErpTypeOf"/> ตัวเดียว</summary>
    private async Task RelinkBookingAsync(Guid companyId, Guid siteId, Guid bookingId, Guid documentId, DocumentType type, string userId)
    {
        var booking = await _db.SiteBookings.FirstAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId);
        booking.ErpDocumentId = documentId;
        booking.ErpDocumentType = CmsBookingErpPolicy.ErpTypeOf(type);
        booking.UpdatedBy = userId;
        await _db.SaveChangesAsync();
    }

    /// <summary>อนุมัติใบกำกับ/ใบแจ้งหนี้ของการจองด้วยเส้นอนุมัติเดิม (SystemWorkflow — ไม่มีคนเห็นคำเตือนในเส้นนี้) · ล้ม ⇒ ถอยของครึ่งทาง + ล้มดัง</summary>
    private async Task ApproveBookingTaxDocumentAsync(Guid companyId, Guid documentId, string? documentNumber, BookingInvoiceEvent ev,
        string userId, List<string> notices, List<string> bookingNotes)
    {
        var baseline = new HashSet<object>(_db.ChangeTracker.Entries().Select(e => e.Entity), ReferenceEqualityComparer.Instance);
        try
        {
            var approved = await _docService!.ApproveDocumentAsync(companyId, documentId, userId, ApprovalAckSource.SystemWorkflow, withAiHints: false);
            var ok = BookingInvoiceTimingPolicy.IssuedNotice(ev, approved.DocumentNumber, approved.DocumentType, ThaiDate.CalendarDateUtc(approved.DocumentDate));
            notices.Add(ok); bookingNotes.Add(ok);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            foreach (var entry in TrackedChangeRevert.DetachSince(_db, baseline))
                await entry.ReloadAsync();
            TrackedChangeRevert.DetachStrays(_db, baseline);
            _logger.LogWarning(ex, "อนุมัติใบกำกับ {Doc} ของการจองไม่สำเร็จ", documentNumber);
            var fail = CmsBookingCancelPolicy.FailureNote("การอนุมัติใบกำกับภาษีของการจอง", documentNumber,
                ReasonOf(ex) + " — ใบยังเป็นร่าง (ยังไม่ออก): เปิดเอกสารแล้วกด “อนุมัติ”");
            notices.Add(fail); bookingNotes.Add(fail);
        }
    }

    /// <summary>ออกเอกสาร ERP ให้การจองที่ไม่ใช่ Lead (ใบที่เพิ่งพ้นคิวสำรอง)</summary>
    private async Task SyncIfBillableAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        var type = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.Id == bookingId && b.CompanyId == companyId && b.SiteId == siteId)
            .Select(b => (BookingType?)b.BookingService.BookingType).FirstOrDefaultAsync();
        if (type is BookingType t && t != BookingType.Lead
            && CreditBookingLink.ShouldSyncErp(await IsPaidWithCreditAsync(companyId, bookingId)))
            await SyncBookingToErpAsync(companyId, siteId, bookingId);
    }

    /// <summary>ให้บริการเสร็จ + เอกสาร ERP เป็นมัดจำ → รับรู้รายได้จากมัดจำ (ตัด 217xx ขายรอรับรู้ → รายได้) ตามนโยบาย
    /// <see cref="CmsBookingCancelPolicy.DecideOnComplete"/> — นี่คือ "ใช้บริการแล้ว" (ไม่ใช่ริบ ⇒ ไม่ส่ง ForfeitAs) ·
    /// มัดจำเต็มยอดของบริษัทที่จด VAT / เงินประกัน ⇒ ไม่รับรู้อัตโนมัติ บอกผู้ใช้ว่าต้องทำอะไร · RealizeDeposit cap ที่คงค้าง ⇒ เรียกซ้ำปลอดภัย</summary>
    private async Task RealizeDepositOnCompleteAsync(Guid companyId, Guid erpDocId, string userId,
        List<string> notices, List<string> bookingNotes)
    {
        var erpDoc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == erpDocId && d.CompanyId == companyId);
        if (erpDoc == null || !erpDoc.IsDeposit) return;

        var (vatRegistered, _) = await CompanyVatStatus.ProfileAsync(_db, companyId);
        var action = CmsBookingCancelPolicy.DecideOnComplete(
            erpDoc.IsDeposit, erpDoc.Status, erpDoc.VatAmount, erpDoc.DepositNature, vatRegistered);
        if (action != CmsBookingCompleteAction.AutoRealize)
        {
            if (CmsBookingCancelPolicy.CompleteNotice(action, erpDoc.DocumentNumber) is string info)
            { notices.Add(info); bookingNotes.Add(info); }
            return;
        }

        var remaining = erpDoc.SubTotal - erpDoc.DepositRealizedAmount;
        if (remaining <= 0.005m) return;
        try
        {
            await _docService!.RealizeDepositAsync(companyId, erpDoc.Id,
                new Models.DTOs.Document.RealizeDepositRequest(remaining, DateTime.UtcNow, null), userId);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(ex, "รับรู้รายได้มัดจำ {Doc} ของการจองไม่สำเร็จ — ต้องรับรู้เอง", erpDoc.DocumentNumber);
            var fail = CmsBookingCancelPolicy.FailureNote("การรับรู้รายได้จากมัดจำ", erpDoc.DocumentNumber, ReasonOf(ex));
            notices.Add(fail); bookingNotes.Add(fail);
        }
    }

    /// <summary>ยกเลิกการจอง → ตัดสินเอกสาร ERP ด้วย <see cref="CmsBookingCancelPolicy.DecideOnCancel"/> (spec S6 C-1):
    /// ใบมัดจำที่ออกแล้ว <b>ไม่ยกเลิก</b> (คงเป็นหนี้สิน · ประทับหมายเหตุบนใบ+การจอง · ผู้ใช้ตัดสินคืน/ริบที่ศูนย์มัดจำ) ·
    /// ใบอื่นยกเลิกเหมือนเดิม (กลับ JE + คืนสต๊อก) · ล้ม ⇒ ประทับบนการจอง + ตอบผู้เรียก (ไม่ใช่แค่ log)</summary>
    private async Task SettleErpDocumentOnCancelAsync(Guid companyId, Guid erpDocId, DateTime cancelledAtUtc,
        List<string> notices, List<string> bookingNotes)
    {
        var erpDoc = await _db.Documents
            .FirstOrDefaultAsync(d => d.Id == erpDocId && d.CompanyId == companyId);
        if (erpDoc == null)
        {
            var missing = CmsBookingCancelPolicy.FailureNote("การยกเลิกเอกสารที่ผูกกับการจอง", null, "ไม่พบเอกสาร ERP ที่ผูกไว้");
            notices.Add(missing); bookingNotes.Add(missing);
            return;
        }

        switch (CmsBookingCancelPolicy.DecideOnCancel(erpDoc.IsDeposit, erpDoc.Status))
        {
            case CmsBookingCancelAction.KeepDepositAsLiability:
                var note = CmsBookingCancelPolicy.CancelNote(cancelledAtUtc);
                erpDoc.InternalNotes = DepositPolicyResolver.AppendNoteOnce(erpDoc.InternalNotes, note);
                await _db.SaveChangesAsync();
                notices.Add(CmsBookingCancelPolicy.KeepDepositNotice(erpDoc.DocumentNumber));
                bookingNotes.Add(note);
                break;
            case CmsBookingCancelAction.VoidDocument:
                try
                {
                    // รอบ 201 ทีม PL: ผลยกเลิก (ธง e-Tax · ภาษีขายที่ถอย) ถึงผู้กดและประทับบนการจอง — เดิมทิ้งเงียบ
                    var voided = await _docService!.VoidDocumentAsync(companyId, erpDoc.Id);
                    foreach (var w in VoidResultNotice.Lines(voided, erpDoc.DocumentNumber))
                    {
                        notices.Add(w); bookingNotes.Add(w);
                    }
                }
                catch (Exception ex)
                {
                    _db.ChangeTracker.Clear();
                    _logger.LogWarning(ex, "ยกเลิกการจองแต่ void เอกสาร ERP {Doc} ไม่สำเร็จ — ต้องกลับรายการเอง", erpDoc.DocumentNumber);
                    var fail = CmsBookingCancelPolicy.FailureNote("การยกเลิกเอกสาร", erpDoc.DocumentNumber, ReasonOf(ex));
                    notices.Add(fail); bookingNotes.Add(fail);
                }
                break;
        }
    }

    /// <summary>เหตุที่แสดงผู้ใช้ได้ — ข้อความของกฎธุรกิจ (ไทย) ผ่าน · ข้อผิดพลาดภายใน (EF/ระบบ) ไม่ echo รายละเอียด</summary>
    private static string ReasonOf(Exception ex) => ex switch
    {
        BusinessRuleException or InvalidOperationException or KeyNotFoundException => ex.Message,
        _ => "เกิดข้อผิดพลาดภายในระบบ (ดูบันทึกระบบ)",
    };

    public async Task<BookingResponse?> GetBookingAsync(Guid companyId, Guid siteId, Guid bookingId)
    {
        var resp = await _db.SiteBookings.AsNoTracking()
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
                DurationMinutes = b.DurationMinutes, Units = b.Units,
                ResourceId = b.ResourceId,
                ResourceName = b.ResourceId == null ? null
                    : _db.SiteBookingResources.Where(r => r.Id == b.ResourceId && r.CompanyId == companyId).Select(r => r.Name).FirstOrDefault(),
                TotalAmount = b.TotalAmount, DepositAmount = b.DepositAmount,
                PaidAmount = b.PaidAmount, Currency = b.Currency,
                BookingType = b.BookingService.BookingType,
                ErpDocumentType = b.ErpDocumentType, ErpDocumentId = b.ErpDocumentId,
                CustomerNotes = b.CustomerNotes, InternalNotes = b.InternalNotes,
                ConfirmedAt = b.ConfirmedAt, CompletedAt = b.CompletedAt,
                CancelledAt = b.CancelledAt, CreatedAt = b.CreatedAt
            })
            .FirstOrDefaultAsync();
        if (resp != null && resp.Status == BookingStatus.Waitlisted)
            resp.WaitlistPosition = (await WaitlistPositionsAsync(companyId, siteId, new[] { resp.Id })).GetValueOrDefault(resp.Id);
        if (resp != null) resp.CreditPassNumber = await CreditPassNumberAsync(companyId, resp.Id);   // R-39
        // ปุ่ม "รับเงินแล้ว — ออกใบกำกับภาษี": ตัวตัดสินเดียวกับตอนกดจริง (server ตัดสิน · หน้าเว็บแสดง) · โหลดข้อเท็จจริงเฉพาะการจองที่เป็นไปได้
        // (Guaranteed ที่ผูกใบยืนยันการจอง หรือยังไม่มีเอกสาร) ⇒ โหมด (ก) ไม่มี query เพิ่ม + หน้าจอเดิม (ฝ่ายค้าน ข้อ 6)
        if (resp != null && resp.BookingType == BookingType.Guaranteed
            && (resp.ErpDocumentType == ErpBookingDocumentType.Quotation || resp.ErpDocumentId == null)
            && resp.Status is not (BookingStatus.Cancelled or BookingStatus.Waitlisted or BookingStatus.NoShow)
            && await LoadInvoiceFactsAsync(companyId, siteId, resp.Id, BookingInvoiceEvent.PaymentReceived) is { } lf)
            resp.CanIssueTaxInvoiceNow = BookingInvoiceTimingPolicy.ShowPaymentButton(lf.Facts);
        return resp;
    }

    /// <summary>ลำดับคิวสำรองของใบที่ระบุ — คิดจากใบที่ยังอยู่ในคิวของรอบเดียวกัน (บริการ · วัน · เวลาเริ่ม) ⇒ มีคนออกจากคิว ลำดับขยับขึ้นเอง ·
    /// สูตรลำดับอยู่ที่ <see cref="BookingSlotEngine.WaitlistPosition"/> ตัวเดียว</summary>
    private async Task<Dictionary<Guid, int>> WaitlistPositionsAsync(Guid companyId, Guid siteId, IReadOnlyCollection<Guid> bookingIds)
    {
        var mine = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && bookingIds.Contains(b.Id) && b.Status == BookingStatus.Waitlisted)
            .Select(b => new { b.Id, b.BookingServiceId, b.BookingDate, b.StartTime, b.CreatedAt })
            .ToListAsync();
        if (mine.Count == 0) return new();
        var serviceIds = mine.Select(x => x.BookingServiceId).Distinct().ToList();
        var dates = mine.Select(x => x.BookingDate).Distinct().ToList();
        var queue = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.Status == BookingStatus.Waitlisted
                && serviceIds.Contains(b.BookingServiceId) && dates.Contains(b.BookingDate))
            .Select(b => new { b.Id, b.BookingServiceId, b.BookingDate, b.StartTime, b.CreatedAt })
            .ToListAsync();
        return mine.ToDictionary(m => m.Id, m => BookingSlotEngine.WaitlistPosition(m,
            queue.Where(q => q.BookingServiceId == m.BookingServiceId && q.BookingDate == m.BookingDate && q.StartTime == m.StartTime),
            q => q.CreatedAt, q => q.Id));
    }

    public async Task<PagedResponse<BookingListResponse>> GetBookingsAsync(Guid companyId, Guid siteId, string? status, Guid? serviceId, DateTime? fromDate, DateTime? toDate, int page, int pageSize, string? search = null)
    {
        var query = _db.SiteBookings.AsNoTracking().Where(b => b.SiteId == siteId && b.CompanyId == companyId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.Trim();
            query = query.Where(b => b.BookingNumber.Contains(q)
                || (b.GuestName != null && b.GuestName.Contains(q))
                || (b.GuestPhone != null && b.GuestPhone.Contains(q))
                || (b.Customer != null && b.Customer.FullName != null && b.Customer.FullName.Contains(q)));
        }

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
                EndTime = b.EndTime.ToString("HH:mm"),
                GuestCount = b.GuestCount,
                ResourceName = b.ResourceId == null ? null
                    : _db.SiteBookingResources.Where(r => r.Id == b.ResourceId && r.CompanyId == companyId).Select(r => r.Name).FirstOrDefault(),
                TotalAmount = b.TotalAmount, Currency = b.Currency,
                CreatedAt = b.CreatedAt
            })
            .ToListAsync();
        var waitIds = items.Where(i => i.Status == BookingStatus.Waitlisted).Select(i => i.Id).ToList();
        if (waitIds.Count > 0)
        {
            var pos = await WaitlistPositionsAsync(companyId, siteId, waitIds);
            foreach (var i in items) if (pos.TryGetValue(i.Id, out var p)) i.WaitlistPosition = p;
        }

        return new PagedResponse<BookingListResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    // ===== ERP Sync (Event-Driven) =====

    /// <summary>ตัวจริงของเส้นสร้างเอกสาร ERP ของการจอง · <paramref name="taxDocumentNow"/> = ถึง tax point แล้วแต่การจองยังไม่มีเอกสารผูก
    /// (ขั้น ERP ตอนจองล้ม) ⇒ สร้างใบชนิดนี้ (ใบกำกับ/ใบแจ้งหนี้) ด้วยเส้นเดียวกัน แทนชนิดตอนจอง — ตัดสินโดย <see cref="BookingInvoiceTimingPolicy"/> (ข้อ 155)</summary>
    private async Task<Guid?> SyncBookingToErpAsync(Guid companyId, Guid siteId, Guid bookingId, DocumentType? taxDocumentNow, DateTime? documentDate)
    {
        var booking = await _db.SiteBookings
            .Include(b => b.BookingService)
            .Include(b => b.Customer)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId);

        if (booking == null || booking.ErpDocumentId.HasValue) return booking?.ErpDocumentId;

        var svc = booking.BookingService;

        // ฝ่ายค้านรอบ 205 ข้อ 6: PrePayment ที่ไม่มียอดมัดจำ ⇒ ไม่มีเงินให้ออกใบเสร็จ (เดิมได้ใบเสร็จ 0.00) — หมายเหตุแทน
        if (!CmsBookingErpPolicy.NeedsErpDocument(svc.BookingType, booking.DepositAmount))
        {
            booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes, CmsBookingErpPolicy.NoDepositNote);
            await _db.SaveChangesAsync();
            return null;
        }

        // ชนิดเอกสารตอนจอง — ตัวตัดสินเดียว BookingInvoiceTimingPolicy (คำตัดสินข้อ 155 · B-2): Guaranteed + ค่าตั้งไซต์ "ออกใบกำกับเมื่อรับเงิน/ให้บริการ"
        // ⇒ ใบยืนยันการจอง (ใบเสนอราคา · ไม่ลงบัญชี) · ค่าเริ่มต้น/ชนิดอื่น = ชนิดเดิมทุกตัว
        var atBooking = BookingInvoiceTimingPolicy.Decide(new BookingInvoiceFacts(
            svc.BookingType, await InvoiceTimingAsync(companyId, siteId), BookingInvoiceEvent.Booked,
            LinkedDocumentType: null, LinkedStatus: null, HasIssuedTaxDocument: false, VatRegistered: true));
        var docType = taxDocumentNow ?? atBooking.DocumentType ?? DocumentType.Quotation;
        var erpDocType = taxDocumentNow is DocumentType forced ? CmsBookingErpPolicy.ErpTypeOf(forced)
            : atBooking.ErpType ?? ErpBookingDocumentType.DraftServiceOrder;

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

        // Route ผ่าน IDocumentService.CreateDocumentAsync = ได้ DocumentNumberGenerator
        // (gap-free §86/4) + tax point + JE auto-post tooling + completeness check
        if (_docService == null)
            throw new InvalidOperationException(
                "Booking → ERP sync ต้องการ IDocumentService — register service ใน DI ก่อน");

        var lineDesc = $"{svc.Name} ({booking.BookingDate:yyyy-MM-dd} {booking.StartTime:HH:mm}-{booking.EndTime:HH:mm})";

        // รอบ 194 (spec S4 · plan §8 ความเสี่ยง "CMS ต่อสายแล้วพฤติกรรมเปลี่ยน"): ส่งประเภทเงินมัดจำเริ่มต้นของบริษัท
        // เข้าเส้นเอกสาร **เฉพาะเมื่อบริษัทตั้งค่ามัดจำเองแล้ว** (ประเภทเริ่มต้นมีโหมด หรือ ตั้งค่า → ภาษี ถูกตั้ง) ⇒ บริษัทที่ไม่เคยแตะ
        // ได้ใบแบบเดิมทุกตัวอักษร (VAT ทันที §78/1) · ตั้งแล้ว ⇒ DocumentService จัดรูปใบผ่าน DepositDocumentShaping ตัวเดียว
        Guid? bookingDepositKindId = null;
        if (svc.BookingType == BookingType.PrePayment)
        {
            var kindCtx = await DepositKindCatalog.LoadContextAsync(_db, companyId);
            // ฝ่ายค้าน C1: ค่าเริ่มต้นที่เป็นเงินประกันถูกข้าม (ใบจองเป็นราคาเสมอ — ไม่งั้นตอนใช้บริการไม่รับรู้รายได้)
            bookingDepositKindId = DepositKindCatalog.PrePaymentKindId(kindCtx);
        }

        // PrePayment booking = ลูกค้าจ่ายมัดจำ → IsDeposit=true → Cr 217xx
        // (ขายรอรับรู้). tax point §78/1 รับชำระแล้ว = เกิดทันที (default
        // DepositOutputVatDeferred=false) → ภาษีขายเข้า ภพ.30 เดือนนี้.
        // เมื่อลูกค้าใช้บริการจริง → ใช้ RealizeDepositAsync ตัด 217xx → 41000
        var isDeposit = svc.BookingType == BookingType.PrePayment;

        // บริษัทไม่จด VAT → ไม่คิด VAT ขายในเอกสารจอง (§90/2) มิฉะนั้นเอกสาร
        // จะติด hard-block ตอน approve และการจองจะ sync ไม่ผ่าน
        // อัตราจาก OutputVatRate ตัวเดียว (เดิม 7 ตายตัว · รอบ 193 S-10) · สถานะ/อัตราจาก CompanyVatStatus ตัวเดียวกับ
        // ด่าน §90/2 ที่จะอนุมัติใบนี้ (ฝ่ายค้าน P-6 — เดิมอ่าน Company ตรง ธงขัดกัน = sync ไม่ผ่าน/คิด VAT ผิดทิศ)
        var (bookingVatRegistered, bookingDefaultRate) = await Accounting.Helpers.CompanyVatStatus.ProfileAsync(_db, companyId);
        var bookingVatRate = Accounting.Helpers.OutputVatRate.ForCompany(bookingVatRegistered, bookingDefaultRate);

        var request = new Models.DTOs.Document.CreateDocumentRequest(
            DocumentType: docType,
            DocumentDate: documentDate ?? DateTime.UtcNow,
            DueDate: null,
            ContactId: contactId!.Value,
            Reference: booking.BookingNumber,
            Notes: $"Booking #{booking.BookingNumber} - {svc.Name}",
            Lines: new List<Models.DTOs.Document.DocumentLineRequest>
            {
                new(
                    Description: lineDesc,
                    // Quantity 1: ยอดจอง (booking.TotalAmount) เป็นราคาต่อ "การจอง"
                    // (flat = svc.Price) ไม่คูณจำนวนแขก. เดิม Quantity=GuestCount →
                    // เอกสาร ERP = ราคา × แขก ไม่ตรงยอดจอง (รายได้/AR สูงเกินจริง N เท่า).
                    // ทีม B: โหมดทุก N นาทีจองหลายช่วงได้ ⇒ ปริมาณ = จำนวนช่วง (ยอด = ราคาต่อช่วง × ช่วง = booking.TotalAmount) ·
                    // ใบมัดจำยังเป็น 1 × ยอดมัดจำ · การจองเดิม Units = 1 ⇒ ใบเหมือนเดิม
                    Quantity: isDeposit ? 1 : Math.Max(1, booking.Units),
                    Unit: !isDeposit && booking.Units > 1 ? "ช่วง" : "ครั้ง",
                    // มัดจำ (PrePayment): เอกสารต้องเป็น "ยอดมัดจำที่รับ" ไม่ใช่ราคาเต็ม
                    // เดิมใช้ svc.Price เต็ม → เงินสด/หนี้สินรอรับรู้สูงเกินจริง.
                    UnitPrice: isDeposit ? booking.DepositAmount : svc.Price,
                    DiscountPercent: 0m,
                    VatRate: bookingVatRate,
                    WithholdingTaxRate: 0m,
                    AccountId: null,
                    ProductCode: svc.Product?.Code)
            },
            Currency: svc.Currency,
            PricesIncludeVat: true,           // ราคา CMS เป็น gross
            BookingNumber: booking.BookingNumber,
            IsDeposit: isDeposit,
            DepositKindId: bookingDepositKindId
        );

        var created = await _docService.CreateDocumentAsync(companyId, request, "storefront-booking");
        booking.ErpDocumentId = created.Id;
        booking.ErpDocumentType = erpDocType;
        if (svc.BookingType == BookingType.Guaranteed && docType == DocumentType.Quotation)
        {
            booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes, BookingInvoiceTimingPolicy.DeferredNote);
            var qtDoc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == created.Id && d.CompanyId == companyId);
            if (qtDoc != null) qtDoc.InternalNotes = DepositPolicyResolver.AppendNoteOnce(qtDoc.InternalNotes, BookingInvoiceTimingPolicy.DeferredNote);
        }

        // Auto-approve: ตัดสินที่ CmsBookingErpPolicy ตัวเดียว (lead = quotation รออนุมัติ — admin ต้อง confirm slot ก่อน) ·
        // รอบ 205 ทีม E (B-1): PrePayment อนุมัติเฉพาะเมื่อเงินเข้าแล้ว — เดิม "prepayment มีเงินจริง" ไม่จริง: การจองหน้าเว็บยังไม่มีใครจ่าย
        // ⇒ ใบเสร็จมัดจำ + JE เงินสด + ภาษีขาย ของเงินที่ไม่เคยได้รับ · ตอนนี้ใบคงเป็นร่าง + หมายเหตุให้พนักงานอนุมัติเมื่อรับเงินจริง
        if (!CmsBookingErpPolicy.AutoApprove(svc.BookingType, booking.DepositAmount, booking.PaidAmount)
            && svc.BookingType == BookingType.PrePayment)
        {
            var waitNote = CmsBookingErpPolicy.AwaitingPaymentNote(booking.DepositAmount);
            booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes, waitNote);
            var draftDoc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == created.Id && d.CompanyId == companyId);
            if (draftDoc != null) draftDoc.InternalNotes = DepositPolicyResolver.AppendNoteOnce(draftDoc.InternalNotes, waitNote);
        }
        else if (CmsBookingErpPolicy.AutoApprove(svc.BookingType, booking.DepositAmount, booking.PaidAmount))
        {
            // รอบ 194 R3 (P-1) — อนุมัติล้ม (เช่น "รอสักครู่" ของล็อกยอดมัดจำ · ด่านอนุมัติ) ⇒ ธุรกรรมอนุมัติ rollback ฝั่ง DB แล้ว
            // แต่ entity ใน context ยังถูกแก้ค้าง (Status=Approved · เลขเอกสาร · JE ที่ Added) ⇒ SaveChanges ด้านล่างเคยบันทึก "ใบ Approved ไม่มี JE"
            // ⇒ จุดตั้งต้นที่ถอยกลับได้: บันทึกของเราก่อน (ErpDocumentId) → ล้มแล้วถอยเฉพาะของที่ขั้นอนุมัติทิ้งไว้ (TrackedChangeRevert ตัวเดียวกับเส้นริบ)
            await _db.SaveChangesAsync();
            var baseline = new HashSet<object>(_db.ChangeTracker.Entries().Select(e => e.Entity), ReferenceEqualityComparer.Instance);
            try
            {
                // ฝ่ายค้านรอบ 201 รอบสาม P2-6: ไม่มีคนเห็นคำเตือนในเส้นนี้ ⇒ SystemWorkflow — ผ่านเหมือนเดิม (ไม่หยุดการออกเอกสาร) แต่ร่องรอยบอกตามจริงว่าไม่ใช่คนรับทราบ (เดิม acknowledgeWarnings: true = ประทับ AcknowledgedByPerson)
                await _docService.ApproveDocumentAsync(companyId, created.Id,
                    "storefront-booking", ApprovalAckSource.SystemWorkflow, withAiHints: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                foreach (var entry in TrackedChangeRevert.DetachSince(_db, baseline))
                    await entry.ReloadAsync();
                TrackedChangeRevert.DetachStrays(_db, baseline);
                // รอบ 200 ทีม R (review194-r4 P4-2): ขั้นหลัง CommitAsync ของการอนุมัติ (ชื่อผู้ติดต่อ/ดึงเอกสาร/webhook) โยนได้ทั้งที่ใบ
                // อนุมัติแล้วจริง ⇒ ข้อความต้องตรวจสถานะจริงจากฐานข้อมูล (F2 #7) ไม่ใช่ประกาศ "ยังเป็นร่าง" เสมอ
                var actual = await _db.Documents.AsNoTracking()
                    .Where(d => d.Id == created.Id && d.CompanyId == companyId)
                    .Select(d => new { d.Status, d.DocumentNumber }).FirstOrDefaultAsync();
                var stillDraft = actual == null || actual.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval;
                booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes, stillDraft
                    ? $"⚠️ อนุมัติเอกสาร ERP {created.DocumentNumber} อัตโนมัติไม่สำเร็จ ({ex.Message}) — เอกสารยังเป็นร่าง ยังไม่ลงบัญชี · "
                      + "เปิดเอกสารแล้วกด “อนุมัติ”"
                    : $"⚠️ เอกสาร ERP {actual!.DocumentNumber} อนุมัติ/ลงบัญชีแล้ว แต่ขั้นหลังอนุมัติล้ม ({ex.Message}) — "
                      + "ตรวจเอกสารใบนั้น (อีเมล/การแจ้งเตือนอาจไม่ถูกส่ง) · ไม่ต้องอนุมัติซ้ำ");
                _logger.LogWarning(ex, "Booking auto-approve failed for {BookingNumber}", booking.BookingNumber);
            }
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation(
            "Booking {BookingNumber} synced as {DocType} (Doc {DocId} {DocNumber}, IsDeposit={IsDeposit})",
            booking.BookingNumber, erpDocType, created.Id, created.DocumentNumber, isDeposit);
        return created.Id;
    }

    public Task<Guid?> SyncBookingToErpAsync(Guid companyId, Guid siteId, Guid bookingId)
        => SyncBookingToErpAsync(companyId, siteId, bookingId, taxDocumentNow: null, documentDate: null);

    // ===== Helpers =====

    private async Task<string> GenerateBookingNumber(Guid companyId, Guid siteId)
    {
        // ล็อก + integer-max ผ่านตัวกลาง (ผลตรวจ F-08) — เดิมไม่มีล็อกเลย
        // ⇒ ลูกค้าสองคนกดจองพร้อมกันบนเว็บได้เลขเดียวกัน (เว็บสาธารณะ =
        // จังหวะชนกันเป็นเรื่องปกติ ไม่ใช่ edge case)
        var prefix = $"BK-{DateTime.UtcNow:yyMM}-";
        return await Accounting.Helpers.SequenceNumber.NextAsync(
            _db, companyId, Accounting.Helpers.AdvisoryLockKey.StorefrontSequence, prefix,
            _db.SiteBookings.IgnoreQueryFilters()
                .Where(b => b.SiteId == siteId && b.BookingNumber.StartsWith(prefix))
                .OrderByDescending(b => b.CreatedAt)
                .Select(b => b.BookingNumber),
            _db.SiteBookings.Local.Select(b => b.BookingNumber),
            lockPart: $"{siteId:N}|{prefix}");
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
