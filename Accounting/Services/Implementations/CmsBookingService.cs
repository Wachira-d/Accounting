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
    /// <summary>ช่องส่งข้อความถึงลูกค้า (แจ้งได้คิว · รอบ 205 W3 — <c>CmsBookingService.Notify.cs</c>) · null = ไม่ส่ง</summary>
    private readonly IBookingNotifier? _notifier;
    // รอบ 205 W6 (R-39): สายต่อบัตรคอร์ส — ดู CmsBookingService.Credits.cs · null = ไม่เปิดใช้ (การจองปกติไม่แตะ)
    private readonly ICreditBookingLedger? _creditLedger;
    /// <summary>รอบ 207: สร้างสินค้า/บริการในระบบจากบริการจอง (CmsBookingService.Products.cs) · null = สร้างไม่ได้ (ผูกของเดิมได้)</summary>
    private readonly IProductService? _productService;
    /// <summary>รอบ 209 P3 — แจ้งร้าน (การจองใหม่) + ลูกค้า (ยืนยันการจอง · ร้านยกเลิก) · null = ไม่ส่ง (CmsBookingService.ShopNotify.cs)</summary>
    private readonly ICmsShopNotifier? _shopNotifier;

    public CmsBookingService(AccountingDbContext db, ILogger<CmsBookingService> logger,
        IDocumentService? docService = null, IBookingNotifier? notifier = null, ICreditBookingLedger? creditLedger = null,
        IProductService? productService = null, ICmsShopNotifier? shopNotifier = null)
    {
        _productService = productService;
        _shopNotifier = shopNotifier;
        _db = db;
        _logger = logger;
        _docService = docService;
        _notifier = notifier;
        _creditLedger = creditLedger;
    }

    // ===== Services =====

    public async Task<BookingServiceResponse> CreateServiceAsync(Guid companyId, Guid siteId, CreateBookingServiceRequest request, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var svc = new SiteBookingService { CompanyId = companyId, SiteId = siteId, CreatedBy = userId };
        await EnsureProductOfCompanyAsync(companyId, request.ProductId);   // รอบ 207: ผูกสินค้าข้ามบริษัทไม่ได้
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
        await EnsureProductOfCompanyAsync(companyId, request.ProductId);   // รอบ 207
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
        // รอบ 210: null = ไม่แตะค่าเดิม (ผู้เรียก API เดิมไม่รีเซ็ตเงียบ) · ค่าไม่รู้จัก = ข้อความไทย
        if (r.MemberAccess is BookingMemberAccess memberAccess)
        {
            if (!Enum.IsDefined(memberAccess)) errs.Add("ค่าตั้ง \"ต้องเป็นสมาชิกก่อนจอง\" ไม่ถูกต้อง");
            else svc.MemberOnly = BookingAccessRule.ToOverride(memberAccess);
        }

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
        var svc = await _db.SiteBookingServices.AsNoTracking().Include(s => s.Slots).Include(s => s.Product)
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.SiteId == siteId && s.CompanyId == companyId && !s.IsDeleted);
        if (svc == null) return null;
        var counts = await UpcomingCountsAsync(companyId, siteId, new[] { svc.Id });
        var site = await SiteContextAsync(companyId, siteId);
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
        var list = await _db.SiteBookingServices.AsNoTracking().Include(s => s.Slots).Include(s => s.Product)
            .Where(s => s.SiteId == siteId && s.CompanyId == companyId && !s.IsDeleted && (includeInactive || s.IsActive))
            .OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt)
            .ToListAsync();
        var ids = list.Select(s => s.Id).ToList();
        var counts = await UpcomingCountsAsync(companyId, siteId, ids);
        var site = await SiteContextAsync(companyId, siteId);
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

    private static BookingServiceResponse MapService(SiteBookingService s, int upcoming, SiteBookingContext site, List<BookingResourceBrief> resources)
    {
        var rules = BookingSlotEngine.RulesOf(s, site.Defaults);
        var slots = s.Slots.Where(x => x.IsActive).ToList();
        // R-14: เวลาทำการที่มีผลจริง = ของบริการ หรือรับจากเว็บ (ตัวตัดสินเดียว ScheduleOf) — ป้าย "ยังไม่ตั้งเวลาทำการ" ต้องดูของที่มีผล ไม่ใช่แค่ของบริการ
        var effective = BookingSlotEngine.ScheduleOf(slots.Select(BookingSlotEngine.RowOf).ToList(), site.Hours, out var usesSiteHours);
        return new BookingServiceResponse
        {
            Id = s.Id, Name = s.Name, NameEn = s.NameEn, Description = s.Description,
            ImageUrl = s.ImageUrl, Slug = s.Slug,
            DurationMinutes = s.DurationMinutes, BufferMinutes = s.BufferMinutes,
            MaxCapacity = s.MaxCapacity, MaxAdvanceBookingDays = s.MaxAdvanceBookingDays,
            MinAdvanceBookingHours = s.MinAdvanceBookingHours,
            Price = s.Price, DepositAmount = s.DepositAmount, DepositPercent = s.DepositPercent,
            Currency = s.Currency, BookingType = s.BookingType, ProductId = s.ProductId,
            // รอบ 207: สินค้า/บริการที่ผูก + คำเตือน (server ประกอบ — BookingProductLink)
            ProductCode = s.Product is { IsDeleted: false } lp && lp.CompanyId == s.CompanyId ? lp.Code : null,
            ProductName = s.Product is { IsDeleted: false } lp2 && lp2.CompanyId == s.CompanyId ? lp2.Name : null,
            ProductWarning = BookingProductLink.Warning(s.ProductId != null,
                s.Product is { IsDeleted: false, IsActive: true } lp3 && lp3.CompanyId == s.CompanyId, s.Price,
                s.Product is { IsDeleted: false } lp4 && lp4.CompanyId == s.CompanyId ? lp4.SellingPrice : null),
            Category = s.Category, AutoConfirm = s.AutoConfirm,
            FreeCancellationHours = s.FreeCancellationHours,
            CancellationFeePercent = s.CancellationFeePercent,
            SlotMode = s.SlotMode, SlotIntervalMinutes = s.SlotIntervalMinutes,
            BufferBeforeMinutes = s.BufferBeforeMinutes, CapacityUnit = s.CapacityUnit,
            MaxUnitsPerBooking = s.MaxUnitsPerBooking, HalfDaySplitTime = s.HalfDaySplitTime,
            CapacityGroup = s.CapacityGroup,
            EnforceSlotCapacity = s.EnforceSlotCapacity, OverlapLimit = s.OverlapLimit, WaitlistEnabled = s.WaitlistEnabled,
            ResourceMode = s.ResourceMode, UseSiteDefaults = s.UseSiteDefaults,
            // รอบ 210: ค่าที่ตั้ง (ฟอร์ม) + ค่าที่มีผลจริง (หน้าจอง) — ตัวตัดสินเดียว BookingAccessRule
            MemberAccess = BookingAccessRule.FromOverride(s.MemberOnly),
            MemberOnly = BookingAccessRule.RequiresMember(site.Row?.RequireMemberToBook ?? false, s.MemberOnly),
            // รอบ 207: โหมดที่มีผลจริง (ไซต์ปิดจัดสรรผู้ให้บริการ ⇒ None) — หน้าจองของลูกค้าอ่านช่องนี้ · ResourceMode = ค่าที่ตั้ง (ฟอร์มแก้ไข)
            EffectiveResourceMode = rules.ResourceMode,
            FormFields = BookingFormFields.Parse(site.Row?.FormFieldsJson),
            EffectiveDurationMinutes = rules.DurationMinutes,
            Resources = rules.ResourceMode == BookingResourceMode.None ? new() : resources,
            Schedule = ScheduleDtoOf(slots.Select(BookingSlotEngine.RowOf), s.MaxCapacity),
            HasOpeningHours = effective.Any(x => !x.IsBlocked),
            UsesSiteHours = usesSiteHours,
            SlotSummary = BookingSlotEngine.Summary(rules),
            PolicySummary = BookingSlotEngine.PolicySummary(rules, s.DepositAmount, s.DepositPercent, s.FreeCancellationHours, s.CancellationFeePercent),
            IsActive = s.IsActive,
            UpcomingBookingsCount = upcoming,
            CreatedAt = s.CreatedAt
        };
    }

    // ===== ค่าตั้งการจองระดับไซต์ =====

    /// <summary>ค่าตั้งระดับไซต์ที่ engine/ตัวแสดงผลต้องรู้ — อ่านแถว <c>SiteBookingSettings</c> ครั้งเดียว · <see cref="Hours"/> = เวลาทำการระดับเว็บ
    /// (R-14 · แปลงจาก JSON ผ่าน <c>BookingSiteHours</c> + <c>ParseSchedule</c> · JSON เสีย ⇒ ว่าง ไม่ล้ม) · <see cref="Row"/> null = ไซต์ยังไม่เคยตั้ง</summary>
    private sealed record SiteBookingContext(BookingSiteDefaults Defaults, IReadOnlyList<BookingScheduleRow> Hours, SiteBookingSettings? Row);

    private async Task<SiteBookingContext> SiteContextAsync(Guid companyId, Guid siteId)
    {
        var row = await _db.SiteBookingSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted);
        return new SiteBookingContext(BookingSlotEngine.DefaultsOf(row), SiteHoursRows(row?.BookingHoursJson), row);
    }

    /// <summary>JSON เวลาทำการระดับเว็บ → แถวกลาง · ค่าที่เก็บไว้แล้วอ่านไม่ได้ ⇒ ว่าง + log (ไม่โยน — ไม่ให้ค่าตั้งเสียล้มหน้าเวลาว่างทุกบริการ)</summary>
    private List<BookingScheduleRow> SiteHoursRows(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return ParseSchedule(BookingSiteHours.Deserialize(json), 1, "เวลาทำการของเว็บไซต์"); }
        catch (BusinessRuleException ex)
        {
            _logger.LogError("เวลาทำการระดับเว็บที่เก็บไว้อ่านไม่ได้ — ถือว่าไม่ตั้ง: {Error}", ex.Message);
            return new();
        }
    }

    private async Task<BookingSiteDefaults> SiteDefaultsAsync(Guid companyId, Guid siteId)
        => (await SiteContextAsync(companyId, siteId)).Defaults;

    /// <summary>รอบ 207: ไซต์เปิดจัดสรรผู้ให้บริการไหม (ตัวตัดสินเดียว <see cref="BookingProviderAllocation.Enabled"/>) — ค่าตั้ง null (แถวก่อนรอบ 207)
    /// ⇒ ตัดสินจากข้อมูลเดิม: มีผู้ให้บริการที่ใช้งาน หรือมีบริการที่ตั้งโหมดผู้ให้บริการ</summary>
    private async Task<bool> ProvidersEnabledAsync(Guid companyId, Guid siteId)
    {
        var setting = await _db.SiteBookingSettings.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted)
            .Select(x => x.ProviderAllocationEnabled).FirstOrDefaultAsync();
        if (setting is bool b) return b;
        var hasRes = await _db.SiteBookingResources.AsNoTracking()
            .AnyAsync(r => r.CompanyId == companyId && r.SiteId == siteId && r.IsActive && !r.IsDeleted);
        var anySvc = hasRes || await _db.SiteBookingServices.AsNoTracking()
            .AnyAsync(s => s.CompanyId == companyId && s.SiteId == siteId && !s.IsDeleted && s.ResourceMode != BookingResourceMode.None);
        return BookingProviderAllocation.Enabled(null, hasRes, anySvc);
    }

    public async Task<BookingSiteSettingsDto> GetSiteSettingsAsync(Guid companyId, Guid siteId)
    {
        var ctx = await SiteContextAsync(companyId, siteId);
        var d = ctx.Defaults;
        return new BookingSiteSettingsDto
        {
            DurationMinutes = d.DurationMinutes, BufferBeforeMinutes = d.BufferBeforeMinutes, BufferAfterMinutes = d.BufferAfterMinutes,
            MinAdvanceHours = d.MinAdvanceHours, MaxAdvanceDays = d.MaxAdvanceDays,
            GuaranteedInvoiceTiming = ctx.Row?.GuaranteedInvoiceTiming ?? BookingInvoiceTiming.TaxInvoiceAtBooking,
            GuaranteedInvoiceTimingOptions = BookingInvoiceTimingPolicy.Options().ToList(),
            // รอบ 205 W3 — echo กลับครบทุกช่องที่รับ (กฎ #4 A "เก็บแล้วต้อง echo กลับ")
            Schedule = ScheduleDtoOf(ctx.Hours, 1),
            WaitlistHoldMinutes = ctx.Row?.WaitlistHoldMinutes ?? 0,
            ReminderEnabled = ctx.Row?.ReminderEnabled ?? true,
            ReminderHoursBefore = ctx.Row?.ReminderHoursBefore ?? BookingReminderPlan.DefaultHoursCsv,
            ReminderTemplate = ctx.Row?.ReminderTemplate,
            OnlineDepositEnabled = ctx.Row?.OnlineDepositEnabled ?? false,   // รอบ 205 W2 R-08 (Q5 = ข)
            ReminderDefaultTemplate = BookingReminderPlan.DefaultTemplate,
            ReminderPlaceholders = BookingReminderPlan.Placeholders.ToList(),
            // รอบ 207 — echo กลับครบ + ตัวเลือก/ตารางเอกสารจากเซิร์ฟเวอร์ (หน้าเว็บไม่มีสำเนาป้าย)
            ProviderAllocationEnabled = await ProvidersEnabledAsync(companyId, siteId),
            ProviderRecommendStrategy = ctx.Row?.ProviderRecommendStrategy ?? BookingRecommendStrategy.LeastLoadedDay,
            ProviderStrategyOptions = BookingProviderAllocation.StrategyOptions().ToList(),
            FormFields = BookingFormFields.Parse(ctx.Row?.FormFieldsJson),
            FormFieldOptions = BookingFormFields.RequirementOptions().ToList(),
            CompletionDocument = ctx.Row?.CompletionDocument ?? BookingCompletionDocument.None,
            CompletionDocumentOptions = BookingDocumentFlow.CompletionOptions().ToList(),
            DocumentFlow = BookingDocumentFlow.Describe(
                ctx.Row?.GuaranteedInvoiceTiming ?? BookingInvoiceTiming.TaxInvoiceAtBooking,
                ctx.Row?.CompletionDocument ?? BookingCompletionDocument.None,
                (await Accounting.Helpers.CompanyVatStatus.ProfileAsync(_db, companyId)).Registered).ToList(),
            // รอบ 210 — echo กลับ + ตัวเลือกต่อบริการจากเซิร์ฟเวอร์
            RequireMemberToBook = ctx.Row?.RequireMemberToBook ?? false,
            MemberAccessOptions = BookingAccessRule.Options(ctx.Row?.RequireMemberToBook ?? false).ToList(),
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
        // รอบ 205 W3: เวลาทำการระดับเว็บ (ตรวจด้วยตัวเดียวกับของบริการ — ParseSchedule โยนข้อความไทย) · ถือรอบ/เตือน (BookingReminderPlan.Validate)
        List<BookingScheduleRow>? siteHours = null;
        if (request.Schedule != null)
        {
            try { siteHours = ParseSchedule(request.Schedule, 1, "เวลาทำการของเว็บไซต์"); }
            catch (BusinessRuleException ex) { errs.Add(ex.Message); }
        }
        errs.AddRange(BookingReminderPlan.Validate(request.ReminderHoursBefore, request.WaitlistHoldMinutes ?? 0, request.ReminderTemplate));
        // รอบ 207
        if (request.ProviderRecommendStrategy is BookingRecommendStrategy st && !Enum.IsDefined(st)) errs.Add("ตรรกะแนะนำผู้ให้บริการไม่ถูกต้อง");
        if (request.CompletionDocument is BookingCompletionDocument cd && !Enum.IsDefined(cd)) errs.Add("เอกสารตอนให้บริการเสร็จไม่ถูกต้อง");
        if (request.FormFields != null && BookingFormFields.ValidateConfig(request.FormFields) is string ffErr) errs.Add(ffErr);
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
        // รอบ 205 W3 — null = คงค่าเดิมทุกช่อง (ผู้เรียกเก่าไม่รีเซ็ตเงียบ) · ตารางว่าง = ล้างเวลาทำการของเว็บ · เทมเพลต "" = กลับไปใช้ข้อความระบบ
        if (request.Schedule != null) row.BookingHoursJson = BookingSiteHours.Serialize(ScheduleDtoOf(siteHours!, 1));
        if (request.WaitlistHoldMinutes is int hold) row.WaitlistHoldMinutes = hold;
        if (request.ReminderEnabled is bool remind) row.ReminderEnabled = remind;
        if (request.ReminderHoursBefore != null)
            row.ReminderHoursBefore = string.Join(",", BookingReminderPlan.ParseHours(request.ReminderHoursBefore));
        if (request.ReminderTemplate != null)
            row.ReminderTemplate = string.IsNullOrWhiteSpace(request.ReminderTemplate) ? null : request.ReminderTemplate.Trim();
        if (request.OnlineDepositEnabled is bool onlineDeposit) row.OnlineDepositEnabled = onlineDeposit;   // รอบ 205 W2 R-08
        // รอบ 207 — null = คงค่าเดิม (ผู้เรียกเก่าไม่รีเซ็ตเงียบ)
        if (request.ProviderAllocationEnabled is bool providers) row.ProviderAllocationEnabled = providers;
        if (request.ProviderRecommendStrategy is BookingRecommendStrategy strategy) row.ProviderRecommendStrategy = strategy;
        if (request.FormFields != null) row.FormFieldsJson = BookingFormFields.Serialize(request.FormFields);
        if (request.CompletionDocument is BookingCompletionDocument completion) row.CompletionDocument = completion;
        if (request.RequireMemberToBook is bool memberOnly) row.RequireMemberToBook = memberOnly;   // รอบ 210 — null = คงค่าเดิม
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
        // รอบ 207 — echo กลับ (คนหลัก/ลำดับต่อบริการ · พนักงานที่ผูก)
        ServiceLinks = r.Services.Where(x => !x.IsDeleted)
            .Select(x => new BookingResourceServiceLinkDto { ServiceId = x.BookingServiceId, IsPrimary = x.IsPrimary, Priority = x.Priority }).ToList(),
        StaffUserId = r.StaffUserId,
    };

    public async Task<BookingResourceResponse> SaveResourceAsync(Guid companyId, Guid siteId, Guid? resourceId, BookingResourceRequest request, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var name = (request.Name ?? "").Trim();
        if (name.Length == 0) throw new BusinessRuleException("กรุณากรอกชื่อผู้ให้บริการ/ห้อง/อุปกรณ์", "CMS-BOOKING-RESOURCE");
        if (request.Capacity < 1 || request.Capacity > 1000)
            throw new BusinessRuleException("รับงานพร้อมกันต้องอยู่ระหว่าง 1–1,000", "CMS-BOOKING-RESOURCE");
        // รอบ 207: ServiceLinks (คนหลัก/ลำดับ) ใช้แทน ServiceIds เมื่อส่งมา · ผู้เรียกเดิมส่งแค่ ServiceIds ⇒ คนหลัก/ลำดับเดิมไม่ถูกแตะ
        var links = request.ServiceLinks?.GroupBy(l => l.ServiceId).Select(g => g.First()).ToList();
        var serviceIds = links != null ? links.Select(l => l.ServiceId).ToList() : (request.ServiceIds ?? new()).Distinct().ToList();
        if (links != null && links.Any(l => l.Priority < 0 || l.Priority > 999))
            throw new BusinessRuleException("ลำดับความสำคัญต้องอยู่ระหว่าง 0–999", "CMS-BOOKING-RESOURCE");
        if (request.StaffUserId is Guid staffUid
            && !await _db.CompanyUsers.AsNoTracking().AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == staffUid))
            throw new BusinessRuleException("พนักงานที่เลือกไม่อยู่ในบริษัทนี้", "CMS-BOOKING-RESOURCE");
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
        res.StaffUserId = request.StaffUserId;

        _db.SiteBookingResourceServices.RemoveRange(res.Services.Where(x => !validIds.Contains(x.BookingServiceId)).ToList());
        foreach (var sid in validIds.Where(id => res.Services.All(x => x.BookingServiceId != id)))
        {
            var l = links?.First(x => x.ServiceId == sid);
            _db.SiteBookingResourceServices.Add(new SiteBookingResourceService { CompanyId = companyId, ResourceId = res.Id, BookingServiceId = sid,
                IsPrimary = l?.IsPrimary ?? false, Priority = l?.Priority ?? 0, CreatedBy = userId });
        }
        if (links != null)
            foreach (var existing in res.Services.Where(x => validIds.Contains(x.BookingServiceId)))
            {
                var l = links.First(x => x.ServiceId == existing.BookingServiceId);
                existing.IsPrimary = l.IsPrimary; existing.Priority = l.Priority; existing.UpdatedBy = userId;
            }
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
        List<BookingResourceInfo> Resources)
    {
        /// <summary>รอบ 209 P4: รอบเรียนของวันนั้น (บริการที่ใช้ตารางคลาส — CmsBookingService.Classes.cs) · null = บริการไม่ใช้ตารางคลาส (พฤติกรรมเดิมทุกข้อ)</summary>
        public List<ClassSessionFact>? ClassSessions { get; init; }
    }

    /// <summary>รอบ 207: สถิติต่อผู้ให้บริการที่ตรรกะแนะนำใช้ — <see cref="BookingRecommendStrategy.FairShareMonth"/> นาทีงานของเดือน (ไม่รวมวันที่จัด —
    /// engine บวกงานของวันนั้นเอง) · <see cref="BookingRecommendStrategy.RoundRobin"/> ได้งานล่าสุดเมื่อไร (เวลาสร้าง/มอบหมาย) · โหมดอื่น ⇒ ว่าง (ไม่ query)</summary>
    private async Task<Dictionary<Guid, (int PeriodMinutes, DateTime? LastAssignedUtc)>> ResourceStatsAsync(Guid companyId, Guid siteId,
        BookingRecommendStrategy strategy, IReadOnlyCollection<Guid> resourceIds, DateOnly date)
    {
        var result = new Dictionary<Guid, (int PeriodMinutes, DateTime? LastAssignedUtc)>();
        if (resourceIds.Count == 0 || strategy is not (BookingRecommendStrategy.FairShareMonth or BookingRecommendStrategy.RoundRobin)) return result;
        var live = _db.SiteBookings.AsNoTracking().Where(b => b.CompanyId == companyId && b.SiteId == siteId
            && b.Status != BookingStatus.Cancelled && b.Status != BookingStatus.Waitlisted);
        var assigned = _db.SiteBookingAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && a.SiteId == siteId && !a.IsDeleted && resourceIds.Contains(a.ResourceId));
        if (strategy == BookingRecommendStrategy.FairShareMonth)
        {
            var monthStart = new DateTime(date.Year, date.Month, 1);
            var monthEnd = monthStart.AddMonths(1);
            var day = date.ToDateTime(TimeOnly.MinValue);
            var inMonth = live.Where(b => b.BookingDate >= monthStart && b.BookingDate < monthEnd && b.BookingDate != day);
            var primary = await inMonth.Where(b => b.ResourceId != null && resourceIds.Contains(b.ResourceId.Value))
                .GroupBy(b => b.ResourceId!.Value).Select(g => new { Id = g.Key, M = g.Sum(b => b.DurationMinutes) }).ToListAsync();
            var extra = await assigned
                .Join(inMonth, a => a.BookingId, b => b.Id, (a, b) => new { a.ResourceId, PrimaryId = b.ResourceId, b.DurationMinutes })
                .Where(x => x.PrimaryId == null || x.PrimaryId != x.ResourceId)
                .GroupBy(x => x.ResourceId).Select(g => new { Id = g.Key, M = g.Sum(x => x.DurationMinutes) }).ToListAsync();
            foreach (var id in resourceIds)
                result[id] = ((primary.FirstOrDefault(x => x.Id == id)?.M ?? 0) + (extra.FirstOrDefault(x => x.Id == id)?.M ?? 0), null);
            return result;
        }
        var last = await live.Where(b => b.ResourceId != null && resourceIds.Contains(b.ResourceId.Value))
            .GroupBy(b => b.ResourceId!.Value).Select(g => new { Id = g.Key, T = g.Max(b => b.CreatedAt) }).ToListAsync();
        var lastExtra = await assigned.GroupBy(a => a.ResourceId).Select(g => new { Id = g.Key, T = g.Max(a => a.CreatedAt) }).ToListAsync();
        foreach (var id in resourceIds)
        {
            DateTime? a = last.FirstOrDefault(x => x.Id == id)?.T, b = lastExtra.FirstOrDefault(x => x.Id == id)?.T;
            result[id] = (0, a is null ? b : b is null ? a : (a > b ? a : b));
        }
        return result;
    }

    /// <summary>ตารางเวลา + การจองที่ใช้ทรัพยากรเดียวกันของวันนั้น — บริการเดียวกัน · บริการในกลุ่มทรัพยากรร่วม (<c>CapacityGroup</c>) ·
    /// การจองใด ๆ ของผู้ให้บริการที่ทำบริการนี้ได้ (ผู้ให้บริการคือของที่ใช้ร่วมข้ามบริการ) · ไม่นับที่ยกเลิก/คิวสำรอง ·
    /// buffer ของแต่ละการจองมาจากบริการของการจองนั้น · ผู้ให้บริการ = เปิดใช้ + ผูกบริการนี้ + ตารางงานของตัวเอง</summary>
    private async Task<DayContext> LoadDayAsync(Guid companyId, Guid siteId, SiteBookingService svc, DateOnly date, Guid? excludeBookingId = null,
        bool forProviders = false)
    {
        var siteCtx = await SiteContextAsync(companyId, siteId);
        var site = siteCtx.Defaults;
        var rules = BookingSlotEngine.RulesOf(svc, site);
        var ownRows = (await _db.SiteBookingSlots.AsNoTracking()
            .Where(s => s.BookingServiceId == svc.Id && s.CompanyId == companyId && s.IsActive)
            .ToListAsync()).Select(BookingSlotEngine.RowOf).ToList();
        // R-14: บริการที่ไม่ตั้งเวลาเองรับเวลาทำการระดับเว็บ (ตัวตัดสินเดียว ScheduleOf) · ความจุต่อช่วงของแถวเว็บ = ความจุของบริการ (โหมดช่วงกำหนดเอง)
        var rows = BookingSlotEngine.ScheduleOf(ownRows,
            siteCtx.Hours.Select(r => r with { Capacity = Math.Max(1, svc.MaxCapacity) }).ToList(), out _).ToList();

        var resources = new List<BookingResourceInfo>();
        // รอบ 207: โหมดที่มีผลจริง (ไซต์ปิด ⇒ None) · forProviders = หน้าต่างเลือกผู้ให้บริการตอนเริ่มให้บริการ (โหลดคนที่รับบริการนี้แม้บริการตั้งโหมด None)
        if (rules.ResourceMode != BookingResourceMode.None || forProviders)
        {
            var res = await _db.SiteBookingResources.AsNoTracking().Include(r => r.Slots)
                .Where(r => r.CompanyId == companyId && r.SiteId == siteId && r.IsActive && !r.IsDeleted
                    && r.Services.Any(l => l.BookingServiceId == svc.Id && !l.IsDeleted))
                .ToListAsync();
            // รอบ 207: คนหลัก/ลำดับต่อบริการ + สถิติที่ตรรกะแนะนำของไซต์ต้องใช้ (โหลดเฉพาะโหมดที่อ่าน — โหมดเดิมไม่มี query เพิ่ม)
            var links = await _db.SiteBookingResourceServices.AsNoTracking()
                .Where(l => l.CompanyId == companyId && l.BookingServiceId == svc.Id && !l.IsDeleted)
                .Select(l => new { l.ResourceId, l.IsPrimary, l.Priority }).ToListAsync();
            var stats = await ResourceStatsAsync(companyId, siteId, rules.Strategy, res.Select(r => r.Id).ToList(), date);
            resources = res.Select(r =>
            {
                var link = links.FirstOrDefault(l => l.ResourceId == r.Id);
                var st = stats.GetValueOrDefault(r.Id);
                return new BookingResourceInfo(r.Id, r.Name, Math.Max(1, r.Capacity), r.SortOrder,
                    r.Slots.Where(x => !x.IsDeleted).Select(BookingSlotEngine.RowOf).ToList(),
                    IsPrimary: link?.IsPrimary ?? false, Priority: link?.Priority ?? 0,
                    PeriodLoadMinutes: st.PeriodMinutes, LastAssignedUtc: st.LastAssignedUtc);
            }).ToList();
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
                && (excludeBookingId == null || b.Id != excludeBookingId)   // R-16: เปลี่ยนผู้ให้บริการของใบนี้เอง — ไม่นับตัวเองเป็นคิวที่ชน
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
        // รอบ 207: ผู้ให้บริการคนที่ 2+ ของการจองหลายคน (SiteBookingAssignment) ก็ไม่ว่างช่วงนั้น — แถวเสริมผูกผู้ให้บริการคนนั้น ·
        // ServiceId = Guid.Empty ⇒ ไม่กินความจุของบริการ/เพดานจองซ้อน (นับเฉพาะเวลาของคนนั้น)
        if (resIds.Count > 0)
        {
            var extra = await _db.SiteBookingAssignments.AsNoTracking()
                .Where(a => a.CompanyId == companyId && a.SiteId == siteId && !a.IsDeleted && resIds.Contains(a.ResourceId))
                .Join(_db.SiteBookings.AsNoTracking().Where(b => b.CompanyId == companyId && b.SiteId == siteId
                        && b.BookingDate >= dayStart && b.BookingDate < dayEnd
                        && b.Status != BookingStatus.Cancelled && b.Status != BookingStatus.Waitlisted
                        && (excludeBookingId == null || b.Id != excludeBookingId)),
                    a => a.BookingId, b => b.Id,
                    (a, b) => new { a.ResourceId, PrimaryId = b.ResourceId, b.StartTime, b.EndTime, b.GuestCount,
                        b.BookingService.UseSiteDefaults, b.BookingService.BufferBeforeMinutes, b.BookingService.BufferMinutes })
                .Where(x => x.PrimaryId == null || x.PrimaryId != x.ResourceId)
                .ToListAsync();
            occupied.AddRange(extra.Select(b => new BookingOccupancy(b.StartTime, b.EndTime, b.GuestCount,
                b.UseSiteDefaults ? site.BufferBeforeMinutes : b.BufferBeforeMinutes,
                b.UseSiteDefaults ? site.BufferAfterMinutes : b.BufferMinutes, Guid.Empty, b.ResourceId)));
        }
        // ฝั่งความจุของบริการ/กลุ่ม (ไม่ใช้ผู้ให้บริการ): การจองของบริการอื่นที่มาเพราะ "ใช้ผู้ให้บริการร่วม" ไม่ใช่คิวของบริการนี้
        if (!forProviders && (resources.Count == 0 || !rules.EnforceCapacity))
            occupied = occupied.Where(b => b.ServiceId is Guid sid && poolIds.Contains(sid)).ToList();
        var day = new DayContext(rules, rows, occupied, resources);
        // รอบ 209 P4: บริการที่มีเทอมคลาส ⇒ ตาราง/ที่นั่ง/ครูของวันมาจากรอบเรียน (Classes.cs) · ไม่มีเทอม ⇒ คืนตัวเดิมทุกค่า ·
        // หน้าต่างเลือกผู้ให้บริการตอนเริ่มให้บริการ (forProviders) ไม่ปรับ — คนลงมือจริงหน้างานเลือกได้ตามเดิม
        return forProviders ? day : await ApplyClassSessionsAsync(companyId, svc, date, day);
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
        var requested = ctx.Rules.ResourceMode == BookingResourceMode.Auto ? null : request.ResourceId;
        if (requested is Guid want && ctx.Resources.All(r => r.Id != want)) return new();
        var names = ctx.Resources.ToDictionary(r => r.Id, r => r.Name);
        // รอบ 209 P4: บริการที่ใช้ตารางคลาส ⇒ ประกาศชื่อครูของรอบ (ครูถูกตั้งบนรอบ ไม่ใช่สุ่มตอนจอง) — ชื่อครูที่ไม่อยู่ในรายชื่อของ engine โหลดจากฐาน
        var classNames = ctx.ClassSessions == null ? new Dictionary<Guid, string>() : await ClassInstructorNamesAsync(companyId, ctx.ClassSessions);
        // รอบ 210: ประกาศว่าต้องเป็นสมาชิกก่อนจองไหม (หน้าจองแสดงปุ่มเข้าสู่ระบบ — ด่านจริงตอนสร้างการจอง)
        var memberOnly = await MemberOnlyAsync(companyId, siteId, svc);
        return BookingSlotEngine.Available(ctx.Rules, date, ctx.Rows, ctx.Occupied, BookingSlotEngine.BangkokNow(DateTime.UtcNow),
                Math.Max(1, request.GuestCount), units, ctx.Resources, requested, includeFull: svc.WaitlistEnabled)
            .Select(o =>
            {
                var cls = ClassScheduleGenerator.SessionAt(ctx.ClassSessions, date, o.Start);
                return new AvailableSlotResponse
                {
                    StartTime = BookingSlotEngine.Hhmm(o.StartMinute),
                    EndTime = BookingSlotEngine.Hhmm(o.EndMinute),
                    AvailableCapacity = o.Remaining,
                    Label = o.Label,
                    Price = BookingSlotEngine.TotalPrice(ctx.Rules, svc.Price, units),
                    IsFull = o.IsFull,
                    // โหมดบังคับระบุ/ลูกค้าเลือก แสดงชื่อคนที่จะได้ · ระบบจัดให้ ไม่ประกาศชื่อล่วงหน้า (ตัดสินใหม่ตอนจอง) · คลาส = ครูของรอบ
                    ResourceName = cls?.EffectiveInstructorId is Guid ci ? classNames.GetValueOrDefault(ci)
                        : ctx.Rules.ResourceMode != BookingResourceMode.Auto && o.ResourceId is Guid rid ? names.GetValueOrDefault(rid) : null,
                    ClassSessionId = cls?.Id,
                    MemberOnly = memberOnly,
                };
            })
            .ToList();
    }

    // ===== Bookings =====

    /// <summary>สร้างการจอง — <b>server เป็นผู้ตัดสิน</b>: เวลา/วันต้องเป็นรอบที่ <see cref="BookingSlotEngine.Decide"/> ยอมรับ (เวลาทำการ · วันหยุด ·
    /// ล่วงหน้าขั้นต่ำ/สูงสุด · ตรงรอบ · ความจุพร้อม buffer · ผู้ให้บริการที่ว่างและรับงานนี้ได้) · เวลาจบ/ระยะ/ยอด/มัดจำ/ผู้ให้บริการมาจาก server ·
    /// เต็ม + บริการเปิดคิวสำรอง ⇒ บันทึกเป็นคิวสำรอง (<see cref="BookingStatus.Waitlisted"/> · ไม่ออกเอกสาร ERP) · ตัดสิน + ออกเลข + บันทึก อยู่ใต้
    /// <c>pg_advisory_xact_lock</c> ของไซต์ (ผู้ให้บริการใช้ร่วมข้ามบริการ ⇒ ล็อกระดับไซต์) ⇒ สองคนกดรอบสุดท้ายพร้อมกันได้คนเดียว ·
    /// ขั้น ERP รันหลัง commit (เส้นเอกสารเปิดธุรกรรมของตัวเอง)</summary>
    public async Task<BookingResponse> CreateBookingAsync(Guid companyId, Guid siteId, CreateBookingRequest request, string? userId,
        bool staffOnBehalf = false)
    {
        var svc = await _db.SiteBookingServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.BookingServiceId && s.SiteId == siteId && s.CompanyId == companyId
                && s.IsActive && !s.IsDeleted)
            ?? throw new KeyNotFoundException("Booking service not found or inactive.");

        // รอบ 210 (คำตัดสินข้อ 169): บริการ/เว็บที่ต้องเป็นสมาชิกก่อนจอง — ด่านอยู่ก่อนตัดสินรอบ (Decide) และก่อนบันทึกใด ๆ ·
        // สมาชิก = ลูกค้าจากโทเคนหน้าร้านของเว็บนี้ (controller แปลงแล้ว — body นิรนามตั้ง CustomerId เองไม่ได้) ที่ยังใช้งาน ·
        // พนักงานที่มีสิทธิ์จัดการการจอง (staffOnBehalf) จองแทนได้ · ค่าเริ่มต้นไม่บังคับ = เส้นเดิม
        if (await MemberOnlyAsync(companyId, siteId, svc))
        {
            var activeMember = request.CustomerId is Guid memberId
                && await _db.SiteCustomers.AsNoTracking().AnyAsync(c => c.Id == memberId && c.CompanyId == companyId && c.SiteId == siteId && c.IsActive);
            if (BookingAccessRule.Problem(true, activeMember, staffOnBehalf) is string memberProblem)
                throw new BusinessRuleException(memberProblem, BookingAccessRule.RuleCode, BookingAccessRule.MemberRequiredStatus);
        }

        // รอบ 207 (คำขอเจ้าของข้อ 7): ช่องบังคับของฟอร์มจอง — ด่านจริงอยู่ที่นี่ (หน้าเว็บแสดงตามค่าเดียวกัน) · ตัวตัดสินเดียว BookingFormFields ·
        // พนักงานของบริษัทสร้างเอง (walk-in) และลูกค้าที่ล็อกอิน (CustomerId — ข้อมูลอยู่ที่บัญชีลูกค้า) ไม่ผ่านด่านนี้
        // ฝ่ายค้านรอบ 207 ข้อ 9: CustomerId มาจาก body ของคำขอนิรนาม ⇒ ยกเว้นเฉพาะลูกค้าที่เป็นของเว็บนี้จริง
        var knownCustomer = request.CustomerId is Guid custId
            && await _db.SiteCustomers.AsNoTracking().AnyAsync(c => c.Id == custId && c.CompanyId == companyId && c.SiteId == siteId);
        // ด่านทำงานเมื่อเว็บตั้งค่าฟอร์มแล้วเท่านั้น (FormFieldsJson ไม่ null) — เว็บที่ไม่เคยตั้ง = เซิร์ฟเวอร์ไม่บังคับเหมือนเดิม
        // (ผู้เรียก API/ช่องทางอื่นที่ไม่ส่งเบอร์ไม่พังเงียบหลังอัปเดต · หน้าเว็บเดิมยังบังคับชื่อ+เบอร์ที่ฝั่งฟอร์ม)
        var formJson = (await SiteContextAsync(companyId, siteId)).Row?.FormFieldsJson;
        if (formJson != null && !knownCustomer && !await IsCompanyStaffAsync(companyId, userId))
        {
            var formCfg = BookingFormFields.Parse(formJson);
            var (formErrs, clean) = BookingFormFields.Check(formCfg, request.GuestName, request.GuestPhone, request.GuestEmail, request.CustomerNotes);
            if (formErrs.Count > 0) throw new BusinessRuleException(string.Join(" · ", formErrs), BookingFormFields.RuleCode);
            request.GuestName = clean.Name; request.GuestPhone = clean.Phone; request.GuestEmail = clean.Email; request.CustomerNotes = clean.Notes;
        }

        // รอบ 209 P6 (ฑ): ด่านยินยอม PDPA ก่อนบันทึก (พนักงาน/ลูกค้าที่ยินยอมไว้แล้วยกเว้น — ตัวตัดสินเดียว CmsDataConsent) ·
        // ติ๊กแล้ว ⇒ PdpaConsentRecord บันทึกพร้อมการจอง
        await CmsDataConsent.EnforceAsync(_db, companyId, siteId, CmsDataConsent.BookingPurpose, request.ConsentGiven,
            userId, request.CustomerId, request.GuestEmail, request.GuestPhone, reference: "booking");

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
            var requested = ctx.Rules.ResourceMode == BookingResourceMode.Auto ? null : request.ResourceId;
            var decision = BookingSlotEngine.Decide(ctx.Rules, date, start, ctx.Rows, ctx.Occupied,
                BookingSlotEngine.BangkokNow(DateTime.UtcNow), guests, units, ctx.Resources, requested);
            // รอบ 209 P4: คลาสที่ถูกยกเลิก/ไม่มีคลาสเวลานี้ บอกเหตุตรง ๆ (บริการไม่ใช้ตารางคลาส ⇒ ผลเดิม)
            decision = ClassScheduleGenerator.Explain(decision, ctx.ClassSessions, date, start);
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
                // รอบ 209 P4: บริการไม่ใช้ผู้ให้บริการแต่รอบคลาสตั้งครูไว้ ⇒ บันทึกครูของรอบ (รายงานผู้ให้บริการ/ค่าตอบแทนเห็น)
                ResourceId = ClassScheduleGenerator.ResourceForBooking(waitlist ? requested : slot.ResourceId, ctx.ClassSessions, date, slot.Start),
                GuestName = request.GuestName, GuestEmail = request.GuestEmail,
                GuestPhone = request.GuestPhone, GuestCount = guests,
                TotalAmount = total, DepositAmount = depositAmount,
                Currency = svc.Currency,
                CustomerNotes = request.CustomerNotes,
                PublicToken = CmsBookingOnlinePayment.NewPublicToken(),   // รอบ 205 W2: ลิงก์จ่ายมัดจำ/จัดการการจองของลูกค้า
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
            var creditContactId = await PrepareCreditBookingAsync(companyId, booking, request, userId);
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

        // รอบ 209 P3 (C4/E2): ยืนยันการจองถึงลูกค้า + แจ้งร้าน หลัง commit — ส่งไม่ถึง = ประทับบนการจอง ไม่ล้มการจอง (ShopNotify.cs)
        await NotifyBookingCreatedAsync(companyId, siteId, booking.Id);

        var created = await GetBookingAsync(companyId, siteId, booking.Id) ?? throw new InvalidOperationException("Failed.");
        created.ErpNotices = createNotices;
        // รอบ 207: ลูกค้าหน้าเว็บ (ไม่ใช่พนักงานของบริษัท) ได้มุมมองลูกค้า — ไม่มีหมายเหตุภายใน/เอกสาร/ประวัติรับเงิน (เดิมคืนทั้งก้อน)
        if (!await IsCompanyStaffAsync(companyId, userId)) created = ForCustomer(created);
        return created;
    }

    /// <summary>รอบ 210: บริการนี้ต้องเป็นสมาชิกก่อนจองไหม (ค่าบริการชนะ · null = ค่าของเว็บ) — ตัวตัดสินเดียว <see cref="BookingAccessRule.RequiresMember"/></summary>
    private async Task<bool> MemberOnlyAsync(Guid companyId, Guid siteId, SiteBookingService svc)
    {
        if (svc.MemberOnly is bool own) return BookingAccessRule.RequiresMember(false, own);
        var site = await _db.SiteBookingSettings.AsNoTracking()
            .Where(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted)
            .Select(x => (bool?)x.RequireMemberToBook).FirstOrDefaultAsync();
        return BookingAccessRule.RequiresMember(site ?? false, null);
    }

    /// <summary>รอบ 207: ผู้เรียกเป็นผู้ใช้ของบริษัทนี้ไหม (พนักงานสร้างการจองเอง) — ไม่ใช่ = ลูกค้าหน้าเว็บ</summary>
    private async Task<bool> IsCompanyStaffAsync(Guid companyId, string? userId)
        => Guid.TryParse(userId, out var uid)
            && await _db.CompanyUsers.AsNoTracking().AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == uid);

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
        var holdMinutes = (await SiteContextAsync(companyId, siteId)).Row?.WaitlistHoldMinutes ?? 0;   // R-13 ถือรอบ (0 = ไม่ถือ)
        foreach (var w in queue)
        {
            if (date.ToDateTime(w.StartTime) < now) continue;   // รอบผ่านไปแล้ว — ไม่เลื่อน
            var svc = await _db.SiteBookingServices.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == w.BookingServiceId && s.CompanyId == companyId && s.IsActive && !s.IsDeleted);
            if (svc == null) continue;
            var ctx = await LoadDayAsync(companyId, siteId, svc, date);
            var requested = ctx.Rules.ResourceMode == BookingResourceMode.Auto ? null : w.ResourceId;
            var d = BookingSlotEngine.Decide(ctx.Rules, date, w.StartTime, ctx.Rows, ctx.Occupied, now,
                w.GuestCount, Math.Max(1, w.Units), ctx.Resources, requested, ignoreAdvance: true);
            if (!d.Allowed) continue;
            w.ResourceId = ClassScheduleGenerator.ResourceForBooking(d.Slot!.ResourceId, ctx.ClassSessions, date, w.StartTime);   // รอบ 209 P4: ครูของรอบคลาส
            if (svc.AutoConfirm || svc.BookingType == BookingType.Lead) { w.Status = BookingStatus.Confirmed; w.ConfirmedAt = DateTime.UtcNow; }
            else w.Status = BookingStatus.Pending;
            // R-13: ประทับเวลาได้คิว + ถือรอบ (เฉพาะรอยืนยัน · BookingReminderPlan.HoldUntil) · แจ้งลูกค้า = NotifyWaitlistPromotedAsync (Notify.cs) หลัง commit —
            // WaitlistNotifiedAt ประทับหลังส่งถึงจริงเท่านั้น · ส่งไม่ถึง ⇒ BookingReminderJob ส่งซ้ำ
            w.WaitlistPromotedAt = DateTime.UtcNow;
            w.WaitlistNotifiedAt = null;
            w.WaitlistHoldUntil = BookingReminderPlan.HoldUntil(w.WaitlistPromotedAt.Value, holdMinutes, w.Status);
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

    public Task<BookingResponse> UpdateBookingStatusAsync(Guid companyId, Guid siteId, Guid bookingId, UpdateBookingStatusRequest request, string userId)
        => UpdateBookingStatusCoreAsync(companyId, siteId, bookingId, request, userId, null);

    /// <summary>รอบ 208 (ฝ่ายค้าน W2 ข้อ 1/3): ทุกการเปลี่ยนสถานะเดินใต้ล็อก session ต่อการจอง (ตัวเดียวกับ "เสร็จสิ้น + รับชำระ" — ล็อกซ้อนในการเชื่อมต่อเดียวกันได้)
    /// ⇒ คำขอพร้อมกันเข้าทีละคำขอ แล้วด่าน <see cref="BookingStatusTransition"/> เห็นสถานะจริง</summary>
    private Task<BookingResponse> UpdateBookingStatusCoreAsync(Guid companyId, Guid siteId, Guid bookingId, UpdateBookingStatusRequest request, string userId,
        BookingCompletionMoney? money)
        => WithBookingSessionLockAsync(companyId, BookingStatusLockPart(bookingId),
            () => UpdateBookingStatusLockedAsync(companyId, siteId, bookingId, request, userId, money));

    private static string BookingStatusLockPart(Guid bookingId) => "status:" + bookingId.ToString("N");

    /// <summary>ตัวเปลี่ยนสถานะเดียว (รอบ 207 แยกจากเมธอดสาธารณะ) — <paramref name="money"/> = รับเงินตอนเสร็จ (CompleteBookingAsync — ใบเสร็จขายสด
    /// ได้ผังเงินเข้าที่เลือก) · null = เส้นเดิมทุกตัว · เริ่มให้บริการ ⇒ ด่านผู้ให้บริการ <c>EnsureStartAssignmentsAsync</c> (Workflow.cs)</summary>
    private async Task<BookingResponse> UpdateBookingStatusLockedAsync(Guid companyId, Guid siteId, Guid bookingId, UpdateBookingStatusRequest request, string userId,
        BookingCompletionMoney? money)
    {
        var booking = await _db.SiteBookings.FirstOrDefaultAsync(b => b.Id == bookingId && b.SiteId == siteId && b.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Booking not found.");

        // คิวสำรองตั้งได้โดย engine เท่านั้น (ตอนรอบเต็ม) — ตั้งมือ = การจองที่กินความจุอยู่หายจากการนับโดยไม่มีใครรู้
        if (request.Status == BookingStatus.Waitlisted && booking.Status != BookingStatus.Waitlisted)
            throw new BusinessRuleException("ย้ายการจองเข้าคิวสำรองเองไม่ได้ — คิวสำรองเกิดจากรอบเต็มเท่านั้น", "CMS-BOOKING-WAITLIST-MANUAL");
        var oldStatus = booking.Status;
        // รอบ 208: ผู้เรียกที่อ่านสถานะมาก่อน (job ปล่อยรอบ) ระบุสถานะที่คาด — เปลี่ยนไปแล้ว (ลูกค้าจ่าย/ยืนยันระหว่างทาง) ⇒ ไม่ทำ
        if (request.ExpectedCurrentStatus is BookingStatus expected && expected != oldStatus)
            throw new BusinessRuleException($"สถานะการจองเปลี่ยนไปแล้ว ({oldStatus}) — ไม่ได้ดำเนินการ", BookingStatusTransition.RuleCode, 409);
        if (request.HoldLapsedAsOfUtc is DateTime asOf
            && (booking.PaidAmount > 0m || booking.WaitlistHoldUntil is not DateTime holdUntil || holdUntil > asOf))
            throw new BusinessRuleException("การจองรับเงินแล้ว/ยังอยู่ในเวลาถือที่ — ไม่ปล่อยที่อัตโนมัติ", BookingStatusTransition.RuleCode, 409);
        // รอบ 208 (ฝ่ายค้าน W2 ข้อ 1/3 blocking): กดซ้ำ = ไม่ทำซ้ำ · สถานะปลายทางเปลี่ยนต่อไม่ได้ (ขั้นเงินห้ามรันซ้ำ)
        var (transition, transitionMsg) = BookingStatusTransition.Check(oldStatus, request.Status);
        if (transition == BookingTransitionResult.Rejected)
            throw new BusinessRuleException(transitionMsg!, BookingStatusTransition.RuleCode, 409);
        if (transition == BookingTransitionResult.NoOp)
        {
            var same = await GetBookingAsync(companyId, siteId, bookingId) ?? throw new InvalidOperationException("Failed.");
            same.ErpNotices = new List<string> { BookingStatusTransition.NoOpNotice };
            return same;
        }
        // รอบ 207 (คำขอเจ้าของข้อ 3): เริ่มให้บริการ — ไซต์เปิดจัดสรรผู้ให้บริการ + มีคนรับบริการนี้ ⇒ ต้องมีผู้ให้บริการ (เลือกจากหน้าต่าง หรือคนที่จัดไว้ตอนจอง)
        // ฝ่ายค้านรอบ 207 ข้อ 9: ข้ามจากยืนยันแล้วไปเสร็จเลยก็ต้องมีผู้ให้บริการ (รายงานผู้ให้บริการไม่ตกหล่น)
        if (request.Status is BookingStatus.InProgress or BookingStatus.Completed
            && oldStatus is not (BookingStatus.InProgress or BookingStatus.Completed))
            await EnsureStartAssignmentsAsync(companyId, siteId, booking, userId);
        booking.Status = request.Status;
        // ฝ่ายค้านรอบ 207 ข้อ 6: ต่อท้าย ไม่ทับ — หมายเหตุภายในคือร่องรอยระบบ (ล้มดัง F2 #7) ห้ามหาย
        if (!string.IsNullOrWhiteSpace(request.InternalNotes))
            booking.InternalNotes = DepositPolicyResolver.AppendNoteOnce(booking.InternalNotes, request.InternalNotes.Trim());
        if (request.CancellationReason != null) booking.CancellationReason = request.CancellationReason;

        switch (request.Status)
        {
            case BookingStatus.Confirmed: booking.ConfirmedAt = DateTime.UtcNow; booking.WaitlistHoldUntil = null; break;   // ยืนยันแล้ว = เลิกถือรอบ (R-13)
            case BookingStatus.InProgress: booking.StartedAt ??= DateTime.UtcNow; break;   // รอบ 207: ไทม์ไลน์ + รายงานผู้ให้บริการ
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
                    // รอบ 205 W2 (R-09): ราคาเต็ม > มัดจำ ⇒ ใบส่วนที่เหลือหักฐานมัดจำ · กรณีอื่นเส้นเดิม RealizeDepositOnCompleteAsync (Payment.cs)
                    await SettleDepositOnCompleteAsync(companyId, siteId, bookingId, erpDocId, userId, notices, bookingNotes);
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
            // รอบ 207: รับเงินตอนเสร็จ ⇒ วันที่ใบ = วันรับเงิน + (ไม่จด VAT รับครบ) ใบเสร็จรับเงินขายสดลงผังที่เลือก
            await ApplyInvoiceTimingAsync(companyId, siteId, bookingId, BookingInvoiceEvent.ServiceCompleted, userId, notices, bookingNotes,
                money?.PaidAt, money);
        else if (request.Status == BookingStatus.NoShow && oldStatus != BookingStatus.NoShow)
        {
            await ApplyInvoiceTimingAsync(companyId, siteId, bookingId, BookingInvoiceEvent.NoShow, userId, notices, bookingNotes);
            // รอบ 205 W2 (R-11): ริบมัดจำตามนโยบายของบริการ (BookingCancellationFee · เพดาน = มัดจำ ข้อ 126) — ไม่ตั้ง % = ไม่ริบเอง
            await ForfeitOnNoShowAsync(companyId, siteId, bookingId, userId, notices, bookingNotes);
        }

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
            // R-13: แจ้งลูกค้าที่ได้คิวทันที (อีเมล/LINE · Notify.cs) — ผลจริงอยู่ในข้อความถึงผู้กด · ส่งไม่ถึง ⇒ job ส่งซ้ำ
            if (promoted.Count > 0) notices.AddRange(await NotifyWaitlistPromotedAsync(companyId, siteId, promoted));
        }

        // รอบ 209 P3 (C4): ร้าน/ระบบยกเลิก (ไม่ใช่ลูกค้ายกเลิกเองผ่านลิงก์) ⇒ แจ้งลูกค้า หลังบันทึก · ส่งไม่ถึง = ประทับบนการจอง + บอกผู้กด
        if (request.Status == BookingStatus.Cancelled && oldStatus != BookingStatus.Cancelled && !CreditBookingLink.IsCustomerActor(userId))
        {
            var cancelNotice = await NotifyBookingCancelledByShopAsync(companyId, siteId, bookingId);
            if (cancelNotice != null) notices.Add(cancelNotice);
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
    private async Task<BookingInvoiceContext?> LoadInvoiceFactsAsync(Guid companyId, Guid siteId, Guid bookingId, BookingInvoiceEvent ev,
        bool paidInFullNow = false, bool collectingPayment = false)
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
        // รอบ 207: ค่าตั้ง "เอกสารตอนให้บริการเสร็จ" ของไซต์ (บริการแบบนัดหมาย) · ไม่มีแถว = ไม่ออก (เดิม) ·
        // พนักงานกด "เสร็จสิ้น + รับชำระ" (collectingPayment) = ตั้งใจรับเงิน ⇒ ต้องมีเอกสารรับเงิน ⇒ อัตโนมัติเสมอ
        var completionDoc = collectingPayment ? BookingCompletionDocument.Auto
            : await _db.SiteBookingSettings.AsNoTracking()
                .Where(x => x.CompanyId == companyId && x.SiteId == siteId && !x.IsDeleted)
                .Select(x => (BookingCompletionDocument?)x.CompletionDocument).FirstOrDefaultAsync() ?? BookingCompletionDocument.None;
        var facts = new BookingInvoiceFacts(b.BookingType, await InvoiceTimingAsync(companyId, siteId), ev,
            linkedType, linkedStatus, childId != null, vatRegistered,
            IssuedChildStatus: childStatus, PosBill: posBill, BookingStatus: b.Status,
            Completion: completionDoc, PaidInFullNow: paidInFullNow);
        return new BookingInvoiceContext(facts, b.ErpDocumentId, linkedNo, childId, childType, childNo);
    }

    /// <summary>จังหวะออกใบกำกับหลังจอง (รับเงิน · ให้บริการเสร็จ · ไม่มา) — ตัดสินที่ <see cref="BookingInvoiceTimingPolicy.Decide"/> ตัวเดียว ·
    /// ออกใบด้วยเส้นแปลงเอกสารเดิม (<c>ConvertDocumentAsync</c>/<c>ConvertDocumentPartialAsync</c>: ใบยืนยันการจอง → ใบกำกับ) + อนุมัติด้วยเส้นเดิม (ไม่เขียน posting ใหม่) ·
    /// ล้ม ⇒ ล้มดัง: ข้อความถึงผู้กด + ประทับบนการจอง (สถานะการจองที่กดไว้บันทึกแล้ว ไม่ถูกลาก)</summary>
    private async Task ApplyInvoiceTimingAsync(Guid companyId, Guid siteId, Guid bookingId, BookingInvoiceEvent ev, string userId,
        List<string> notices, List<string> bookingNotes, DateTime? paidOn = null, BookingCompletionMoney? money = null)
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
            await ApplyInvoiceTimingLockedAsync(companyId, siteId, bookingId, ev, userId, notices, bookingNotes, paidOn, money);
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
        List<string> notices, List<string> bookingNotes, DateTime? paidOn, BookingCompletionMoney? money = null)
    {
        var l = await LoadInvoiceFactsAsync(companyId, siteId, bookingId, ev, money?.PaidInFull ?? false, collectingPayment: money != null);
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
                    // รอบ 207: ใบเสร็จรับเงิน (ไม่จด VAT รับครบตอนเสร็จ) = ขายสด ⇒ ผังเงินเข้าตามที่พนักงานเลือก
                    var isCashReceipt = decision.DocumentType == DocumentType.Receipt;
                    var newId = await SyncBookingToErpAsync(companyId, siteId, bookingId, decision.DocumentType!.Value, paidOn,
                        isCashReceipt ? money?.MoneyAccountId : null, isCashReceipt ? money?.BankAccountId : null);
                    var made = newId is Guid nid
                        ? await _db.Documents.AsNoTracking().Where(d => d.Id == nid && d.CompanyId == companyId)
                            .Select(d => new { d.DocumentNumber, d.DocumentType, d.Status, d.DocumentDate }).FirstOrDefaultAsync()
                        : null;
                    if (made != null && made.Status is not (DocumentStatus.Draft or DocumentStatus.WaitingApproval))
                    {
                        var okMade = BookingInvoiceTimingPolicy.IssuedNotice(ev, made.DocumentNumber, made.DocumentType, ThaiDate.CalendarDateUtc(made.DocumentDate), paidOn != null);
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
                    // ฝ่ายค้านรอบ 207 ข้อ 1: แปลงบางบรรทัดไปใบเสร็จไม่ได้ (Receipt ไม่มีแกนส่งมอบ) ⇒ ใบเสร็จแปลงเต็มใบ แล้วตั้งวันที่ด้านล่าง
                    if (paidOn is DateTime docDate && decision.DocumentType != DocumentType.Receipt)
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
                // รอบ 207: ใบเสร็จรับเงินจากใบยืนยันการจอง (ขายสดทั้งก้อน) ⇒ ผังเงินเข้าตามที่พนักงานเลือกก่อนอนุมัติ
                if (decision.DocumentType == DocumentType.Receipt && money != null)
                {
                    var draft = await _db.Documents.FirstOrDefaultAsync(d => d.Id == converted.Id && d.CompanyId == companyId);
                    if (draft != null && draft.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
                    {
                        draft.DocumentDate = money.PaidAt;   // ใบเสร็จ = หลักฐานรับเงิน ⇒ ลงวันที่รับเงิน (§78/1)
                        if (money.MoneyAccountId != null) draft.PaymentAccountId = money.MoneyAccountId;
                        if (money.BankAccountId != null) draft.BankAccountId = money.BankAccountId;
                        await _db.SaveChangesAsync();
                    }
                }
                // ผูกใบใหม่ก่อนอนุมัติ — อนุมัติล้ม ⇒ ครั้งถัดไปเห็นใบร่างที่ผูกอยู่ ⇒ ApprovePendingTaxInvoice (ไม่แปลงซ้ำ)
                await RelinkBookingAsync(companyId, siteId, bookingId, converted.Id, decision.DocumentType!.Value, userId);
                await ApproveBookingTaxDocumentAsync(companyId, converted.Id, converted.DocumentNumber, ev, userId, notices, bookingNotes, paidOn != null);
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
        string userId, List<string> notices, List<string> bookingNotes, bool datedOnPayment = false)
    {
        var baseline = new HashSet<object>(_db.ChangeTracker.Entries().Select(e => e.Entity), ReferenceEqualityComparer.Instance);
        try
        {
            var approved = await _docService!.ApproveDocumentAsync(companyId, documentId, userId, ApprovalAckSource.SystemWorkflow, withAiHints: false);
            var ok = BookingInvoiceTimingPolicy.IssuedNotice(ev, approved.DocumentNumber, approved.DocumentType, ThaiDate.CalendarDateUtc(approved.DocumentDate), datedOnPayment);
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
                PaidAt = b.PaidAt, PaymentReference = b.PaymentReference, PublicToken = b.PublicToken,   // รอบ 205 W2
                BookingType = b.BookingService.BookingType,
                ErpDocumentType = b.ErpDocumentType, ErpDocumentId = b.ErpDocumentId,
                CustomerNotes = b.CustomerNotes, InternalNotes = b.InternalNotes,
                ConfirmedAt = b.ConfirmedAt, CompletedAt = b.CompletedAt,
                CancelledAt = b.CancelledAt, CreatedAt = b.CreatedAt,
                ResourceMode = b.BookingService.ResourceMode,
                ReminderSentAt = b.ReminderSentAt, NotifyLastError = b.NotifyLastError,
                WaitlistPromotedAt = b.WaitlistPromotedAt, WaitlistNotifiedAt = b.WaitlistNotifiedAt, WaitlistHoldUntil = b.WaitlistHoldUntil,
            })
            .FirstOrDefaultAsync();
        // รอบ 207: โหมดที่มีผลจริง (ไซต์ปิดจัดสรรผู้ให้บริการ ⇒ None) · ผู้ให้บริการหลายคน/สัดส่วน (Assignments.cs)
        if (resp != null)
        {
            var siteDefaults = await SiteDefaultsAsync(companyId, siteId);
            resp.ResourceMode = BookingProviderAllocation.EffectiveMode(resp.ResourceMode, siteDefaults.ProvidersEnabled);
            resp.ProviderAllocationEnabled = await ProvidersEnabledAsync(companyId, siteId);
            resp.CanAssignResource = CanAssignResource(resp.ResourceMode, resp.Status, resp.BookingDate, resp.StartTime);
            await FillAssignmentsAsync(companyId, resp);
        }
        if (resp != null && resp.Status == BookingStatus.Waitlisted)
            resp.WaitlistPosition = (await WaitlistPositionsAsync(companyId, siteId, new[] { resp.Id })).GetValueOrDefault(resp.Id);
        // R-39 เลขบัตร + รอบ 209 P4 (ญ): นโยบายยกเลิกที่ใช้จริง (จองด้วยบัตร ⇒ กติกาของแพ็กเกจชนะ) — Credits.cs
        int? creditFreeCancelHours = resp != null ? await FillCreditPolicyAsync(companyId, resp) : null;
        if (resp != null) await FillCustomerFacingAsync(companyId, siteId, resp);   // รอบ 205 W2: มัดจำค้าง/จ่ายออนไลน์/นโยบายยกเลิก — ค่าจากเซิร์ฟเวอร์
        // ญ: "ถ้ายกเลิกตอนนี้" ของการจองด้วยบัตร = ตัวตัดสินของคอร์ส (เส้นยกเลิกจริงใช้ตัวเดียวกัน) ไม่ใช่ค่าธรรมเนียมของบริการ
        if (resp != null && creditFreeCancelHours is int cfh && resp.CanCancelOnline)
            resp.CancelPolicyNote = CreditBookingLink.CancelNowNote(StartUtcOf(resp.BookingDate, resp.StartTime), DateTime.UtcNow, cfh);
        // ปุ่ม "รับเงินแล้ว — ออกใบกำกับภาษี": ตัวตัดสินเดียวกับตอนกดจริง (server ตัดสิน · หน้าเว็บแสดง) · โหลดข้อเท็จจริงเฉพาะการจองที่เป็นไปได้
        // (Guaranteed ที่ผูกใบยืนยันการจอง หรือยังไม่มีเอกสาร) ⇒ โหมด (ก) ไม่มี query เพิ่ม + หน้าจอเดิม (ฝ่ายค้าน ข้อ 6)
        if (resp != null && resp.BookingType == BookingType.Guaranteed
            && (resp.ErpDocumentType == ErpBookingDocumentType.Quotation || resp.ErpDocumentId == null)
            && resp.Status is not (BookingStatus.Cancelled or BookingStatus.Waitlisted or BookingStatus.NoShow)
            && await LoadInvoiceFactsAsync(companyId, siteId, resp.Id, BookingInvoiceEvent.PaymentReceived) is { } lf)
            resp.CanIssueTaxInvoiceNow = BookingInvoiceTimingPolicy.ShowPaymentButton(lf.Facts);
        if (resp != null) await FillWorkspaceAsync(companyId, siteId, resp);   // รอบ 207: เอกสารทุกใบ · ประวัติรับเงิน · ไทม์ไลน์ · แผนตอนเสร็จ
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

    public async Task<PagedResponse<BookingListResponse>> GetBookingsAsync(Guid companyId, Guid siteId, string? status, Guid? serviceId, DateTime? fromDate, DateTime? toDate, int page, int pageSize, string? search = null, Guid? resourceId = null, string? sort = null)
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
        // รอบ 207 (หน้าทำงานผู้ให้บริการ): กรองผู้ให้บริการ (หลัก หรือมอบหมายร่วม) · เรียงตามเวลานัด (asc) สำหรับมุมมองรายวัน
        if (resourceId is Guid rid)
            query = query.Where(b => b.ResourceId == rid
                || _db.SiteBookingAssignments.Any(a => a.CompanyId == companyId && a.BookingId == b.Id && a.ResourceId == rid && !a.IsDeleted));

        var total = await query.CountAsync();
        var ordered = sort == "asc"
            ? query.OrderBy(b => b.BookingDate).ThenBy(b => b.StartTime)
            : query.OrderByDescending(b => b.BookingDate).ThenBy(b => b.StartTime);
        var items = await ordered
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(b => new BookingListResponse
            {
                Id = b.Id, SiteId = b.SiteId, BookingNumber = b.BookingNumber, Status = b.Status,
                ServiceName = b.BookingService.Name,
                CustomerName = b.Customer != null ? b.Customer.FullName : b.GuestName,
                BookingDate = b.BookingDate,
                StartTime = b.StartTime.ToString("HH:mm"),
                EndTime = b.EndTime.ToString("HH:mm"),
                GuestCount = b.GuestCount,
                ResourceName = b.ResourceId == null ? null
                    : _db.SiteBookingResources.Where(r => r.Id == b.ResourceId && r.CompanyId == companyId).Select(r => r.Name).FirstOrDefault(),
                TotalAmount = b.TotalAmount, Currency = b.Currency,
                CreatedAt = b.CreatedAt,
                PaidAmount = b.PaidAmount, GuestPhone = b.GuestPhone,
            })
            .ToListAsync();
        var waitIds = items.Where(i => i.Status == BookingStatus.Waitlisted).Select(i => i.Id).ToList();
        if (waitIds.Count > 0)
        {
            var pos = await WaitlistPositionsAsync(companyId, siteId, waitIds);
            foreach (var i in items) if (pos.TryGetValue(i.Id, out var p)) i.WaitlistPosition = p;
        }
        await FillProvidersLabelAsync(companyId, items);

        return new PagedResponse<BookingListResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    /// <summary>รอบ 207: ป้ายผู้ให้บริการทุกคนของแต่ละแถว (มอบหมายหลายคน = "ก 60% · ข 40%") — batch ต่อหน้า</summary>
    private async Task FillProvidersLabelAsync(Guid companyId, List<BookingListResponse> items)
    {
        var ids = items.Select(i => i.Id).ToList();
        if (ids.Count == 0) return;
        var rows = await _db.SiteBookingAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && ids.Contains(a.BookingId) && !a.IsDeleted)
            .Select(a => new { a.BookingId, a.ResourceName, a.SharePercent }).ToListAsync();
        foreach (var g in rows.GroupBy(r => r.BookingId))
        {
            var item = items.First(i => i.Id == g.Key);
            item.ProvidersLabel = g.Count() == 1 ? g.First().ResourceName
                : BookingProviderShare.Describe(g.OrderByDescending(x => x.SharePercent).Select(x => (x.ResourceName, x.SharePercent)));
        }
        foreach (var i in items.Where(i => i.ProvidersLabel == null)) i.ProvidersLabel = i.ResourceName;
    }

    public async Task<PagedResponse<BookingListResponse>> GetBookingsAcrossSitesAsync(Guid companyId, IReadOnlyCollection<Guid> siteIds, string? status, DateTime? fromDate, DateTime? toDate, string? search, int page, int pageSize)
    {
        page = Accounting.Helpers.CmsSiteScope.ClampPage(page);
        pageSize = Accounting.Helpers.CmsSiteScope.ClampPageSize(pageSize);
        // รายการเว็บว่าง = ผู้ใช้ไม่มีสิทธิ์เว็บใด ⇒ ไม่มีแถว (ห้ามตีความเป็น "ทุกเว็บ")
        if (siteIds.Count == 0) return new PagedResponse<BookingListResponse>(new List<BookingListResponse>(), 0, page, pageSize, 0);
        var ids = siteIds.ToList();
        var query = _db.SiteBookings.AsNoTracking().Where(b => b.CompanyId == companyId && ids.Contains(b.SiteId));
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
        if (fromDate.HasValue) query = query.Where(b => b.BookingDate >= fromDate);
        if (toDate.HasValue) query = query.Where(b => b.BookingDate <= toDate);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(b => b.BookingDate).ThenBy(b => b.StartTime)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(b => new BookingListResponse
            {
                Id = b.Id, SiteId = b.SiteId, SiteName = b.Site.Name,
                BookingNumber = b.BookingNumber, Status = b.Status,
                ServiceName = b.BookingService.Name,
                CustomerName = b.Customer != null ? b.Customer.FullName : b.GuestName,
                BookingDate = b.BookingDate,
                StartTime = b.StartTime.ToString("HH:mm"),
                EndTime = b.EndTime.ToString("HH:mm"),
                GuestCount = b.GuestCount,
                ResourceName = b.ResourceId == null ? null
                    : _db.SiteBookingResources.Where(r => r.Id == b.ResourceId && r.CompanyId == companyId).Select(r => r.Name).FirstOrDefault(),
                TotalAmount = b.TotalAmount, Currency = b.Currency,
                CreatedAt = b.CreatedAt,
                PaidAmount = b.PaidAmount, GuestPhone = b.GuestPhone,
            })
            .ToListAsync();
        // ลำดับคิวสำรองคิดต่อเว็บ (ตัวเดิม WaitlistPositionsAsync รับ siteId เดียว)
        foreach (var g in items.Where(i => i.Status == BookingStatus.Waitlisted).GroupBy(i => i.SiteId))
        {
            var pos = await WaitlistPositionsAsync(companyId, g.Key, g.Select(i => i.Id).ToList());
            foreach (var i in g) if (pos.TryGetValue(i.Id, out var p)) i.WaitlistPosition = p;
        }
        await FillProvidersLabelAsync(companyId, items);   // รอบ 207
        return new PagedResponse<BookingListResponse>(items, total, page, pageSize, (int)Math.Ceiling(total / (double)pageSize));
    }

    // ===== ERP Sync (Event-Driven) =====

    /// <summary>ตัวจริงของเส้นสร้างเอกสาร ERP ของการจอง · <paramref name="taxDocumentNow"/> = ถึง tax point แล้วแต่การจองยังไม่มีเอกสารผูก
    /// (ขั้น ERP ตอนจองล้ม) ⇒ สร้างใบชนิดนี้ (ใบกำกับ/ใบแจ้งหนี้) ด้วยเส้นเดียวกัน แทนชนิดตอนจอง — ตัดสินโดย <see cref="BookingInvoiceTimingPolicy"/> (ข้อ 155)</summary>
    private async Task<Guid?> SyncBookingToErpAsync(Guid companyId, Guid siteId, Guid bookingId, DocumentType? taxDocumentNow, DateTime? documentDate,
        Guid? paymentAccountId = null, Guid? bankAccountId = null)
    {
        // รอบ 207 (คำขอเจ้าของข้อ 1): โหลดสินค้า/บริการที่ผูกด้วย — เดิม Include แค่บริการ ⇒ svc.Product เป็น null เสมอ
        // (รหัสสินค้า/บัญชีรายได้/อัตรา VAT ของสินค้าไม่เคยถึงเอกสาร แม้ผูกไว้แล้ว)
        var booking = await _db.SiteBookings
            .Include(b => b.BookingService).ThenInclude(s => s.Product)
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

        // ผู้ติดต่อ ERP — ตัวหาเดียวของลูกค้าเว็บ (รอบ 209 P6 · D4: เดิมสำเนาที่สามจับอีเมลตรงตัวโดยไม่กรองแถวที่ลบแล้ว)
        Guid? contactId = (await CmsContactResolver.ResolveAsync(_db, companyId,
            CmsContactResolver.ForBooking(booking, "storefront-booking"), booking.Customer)).ContactId;

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
        // รอบ 207: บริการผูกสินค้า/บริการในระบบ ⇒ ใช้บัญชีรายได้ + อัตรา VAT ของสินค้านั้น (ตัวเดียวกับที่ POS/เอกสารมือใช้) ·
        // บริษัทไม่จด VAT ยังได้ 0 เสมอ (§90/2) · ใบมัดจำไม่ใช้บัญชีรายได้ (ลง 217xx ผ่าน IsDeposit)
        var product = svc.Product is { IsDeleted: false } pr && pr.CompanyId == companyId ? pr : null;
        var lineVatRate = BookingProductLink.VatRate(bookingVatRegistered, bookingVatRate, product?.VatRate);

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
                    VatRate: lineVatRate,
                    WithholdingTaxRate: 0m,
                    AccountId: isDeposit ? null : product?.SalesAccountId,
                    ProductCode: product?.Code)
            },
            Currency: svc.Currency,
            PricesIncludeVat: true,           // ราคา CMS เป็น gross
            BookingNumber: booking.BookingNumber,
            IsDeposit: isDeposit,
            DepositKindId: bookingDepositKindId,
            // รอบ 205 W2 (R-08): ใบเสร็จมัดจำที่สร้างหลังเงินเข้า — ผังเงินเข้าจาก gateway/ธนาคารที่พนักงานเลือก (null = ค่าเริ่มต้นเดิม)
            PaymentAccountId: paymentAccountId,
            BankAccountId: bankAccountId,
            // รอบ 209 P6 (ฐ): สาขาของเว็บ ⇒ สาขาที่ออกใบ · เว็บไม่ผูกสาขา = null = สำนักงานใหญ่เดิม
            BranchId: await CmsDocumentBranch.ForSiteAsync(_db, companyId, siteId)
        );

        // รอบ 205 ทีม W1 (R-04): ระบุต้นทาง ⇒ ใบร่างจากการจองไม่กินโควตาจนอนุมัติ (DocumentQuotaPolicy.DefersCountUntilApproval)
        var created = await _docService.CreateDocumentAsync(companyId, request, "storefront-booking",
            originModule: Accounting.Helpers.DocumentQuotaPolicy.CmsBookingOrigin);
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
        IsActive = s.IsActive
    };
}
