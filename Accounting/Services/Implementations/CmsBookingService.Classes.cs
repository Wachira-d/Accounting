using Accounting.Helpers;
using Accounting.Models.DTOs.Cms;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 · P4 — <b>ตารางคลาสของสตูดิโอ</b> บนเครื่องจอง CMS ตัวเดิม (คำตัดสินข้อ 168 ฌ · CLASS_BOOKING_REVIEW C2/E10 · ไม่สร้างเครื่องจองที่สอง)
/// <list type="bullet">
///   <item><b>เทอม → รอบเรียน</b>: <c>SiteClassTerm</c> สร้าง <c>SiteClassSession</c> N สัปดาห์ (ข้ามวันหยุดบริษัท/ช่วงปิด · สร้างซ้ำไม่ซ้ำ · ไม่ลบรอบที่มีการจอง) —
///     แผนจากตัวตัดสินเดียว <see cref="ClassScheduleGenerator.Plan"/></item>
///   <item><b>ป้อน engine</b> (<see cref="ApplyClassSessionsAsync"/> — เรียกจาก <c>LoadDayAsync</c> ตัวเดียว ⇒ หน้าเวลาว่าง · สร้างการจอง · เลื่อนคิวสำรอง · เลื่อนนัด ·
///     เปลี่ยนผู้ให้บริการ ใช้ตารางเดียวกันหมด): บริการที่มีเทอม ⇒ รอบที่จองได้ = รอบที่ยังไม่ยกเลิก · ที่นั่ง = ของรอบ · ครู = ของรอบ · บริการไม่มีเทอม ⇒ ไม่แตะ</item>
///   <item><b>หน้าเคาน์เตอร์</b>: ครูแทนทั้งรอบ (การจองทุกใบตาม + แจ้งลูกค้า) · ยกเลิกทั้งคลาส (ทุกใบยกเลิกผ่าน <c>UpdateBookingStatusCoreAsync</c> เส้นเดิม —
///     ด่านลำดับสถานะรอบ 208 · คืนเครดิตแบบร้านยกเลิก · มัดจำตามกติกายกเลิกเดิม · แจ้งลูกค้าผ่าน <c>IBookingNotifier</c>) · roster + เช็คอิน มา/ไม่มา ผ่านเส้นสถานะเดิม</item>
/// </list>
/// </summary>
public partial class CmsBookingService
{
    private const string ClassRule = ClassScheduleGenerator.RuleCode;

    private static ClassSessionFact FactOf(SiteClassSession s)
        => new(s.Id, s.SessionDate, s.StartTime, s.EndTime, s.Status == ClassSessionStatus.Cancelled, s.CapacityOverride,
            s.InstructorResourceId, s.SubstituteResourceId);

    private static DateTime ClassNowLocal() => BookingSlotEngine.BangkokNow(DateTime.UtcNow);

    // ─────────────── ป้อน engine ───────────────

    /// <summary>บริการที่มีเทอมคลาส (ที่ยังไม่ลบ) ⇒ ตาราง/ที่นั่ง/ครูของวันมาจากรอบเรียน (<see cref="ClassScheduleGenerator"/>) · ไม่มีเทอม ⇒ คืน <paramref name="day"/> ตัวเดิม</summary>
    private async Task<DayContext> ApplyClassSessionsAsync(Guid companyId, SiteBookingService svc, DateOnly date, DayContext day)
    {
        var hasTerm = await _db.SiteClassTerms.AsNoTracking().AnyAsync(t => t.CompanyId == companyId && t.BookingServiceId == svc.Id);
        if (!hasTerm) return day;
        var sessions = (await _db.SiteClassSessions.AsNoTracking()
                .Where(s => s.CompanyId == companyId && s.BookingServiceId == svc.Id && s.SessionDate == date)
                .ToListAsync())
            .Select(FactOf).ToList();
        var rows = ClassScheduleGenerator.ScheduleRows(sessions, date, Math.Max(1, svc.MaxCapacity)).ToList();
        var resources = day.Resources.Count == 0 ? day.Resources : ClassScheduleGenerator.ResourcesFor(day.Resources, sessions, date);
        return new DayContext(ClassScheduleGenerator.RulesFor(day.Rules), rows, day.Occupied, resources) { ClassSessions = sessions };
    }

    /// <summary>ชื่อครูของรอบ (ครูแทนก่อน) — หน้าเวลาว่างประกาศชื่อครูของคลาส</summary>
    private async Task<Dictionary<Guid, string>> ClassInstructorNamesAsync(Guid companyId, IEnumerable<ClassSessionFact> sessions)
    {
        var ids = sessions.Select(s => s.EffectiveInstructorId).Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        if (ids.Count == 0) return new();
        return await _db.SiteBookingResources.AsNoTracking()
            .Where(r => r.CompanyId == companyId && ids.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name);
    }

    // ─────────────── ตัวช่วยโหลด ───────────────

    private async Task<SiteBookingService> ClassServiceAsync(Guid companyId, Guid siteId, Guid serviceId)
        => await _db.SiteBookingServices.AsNoTracking()
               .FirstOrDefaultAsync(s => s.Id == serviceId && s.SiteId == siteId && s.CompanyId == companyId && !s.IsDeleted)
           ?? throw new KeyNotFoundException("Booking service not found.");

    /// <summary>ครูของเทอม/รอบต้องเป็นผู้ให้บริการที่เปิดใช้ของเว็บนี้ และรับบริการนี้ (engine นับงานอื่นของคนนั้นได้ครบ)</summary>
    private async Task EnsureClassInstructorAsync(Guid companyId, Guid siteId, Guid serviceId, Guid? resourceId)
    {
        if (resourceId is not Guid rid) return;
        var ok = await _db.SiteBookingResourceServices.AsNoTracking()
            .AnyAsync(l => l.CompanyId == companyId && l.ResourceId == rid && l.BookingServiceId == serviceId && !l.IsDeleted
                && l.Resource.CompanyId == companyId && l.Resource.SiteId == siteId && l.Resource.IsActive && !l.Resource.IsDeleted);
        if (!ok)
            throw new BusinessRuleException("ผู้สอนที่เลือกต้องเป็นผู้ให้บริการที่เปิดใช้และรับบริการนี้ (ตั้งที่หน้าผู้ให้บริการ)", ClassRule);
    }

    private async Task<int> ClassDurationAsync(Guid companyId, Guid siteId, SiteBookingService svc)
        => BookingSlotEngine.RulesOf(svc, await SiteDefaultsAsync(companyId, siteId)).DurationMinutes;

    private static ClassTermSpec SpecOf(SiteClassTerm t, int durationMinutes)
        => new(ClassScheduleGenerator.ParseWeekdays(t.Weekdays), t.StartTime, durationMinutes, t.StartDate, t.EndDate,
            t.InstructorResourceId, t.CapacityOverride, t.SkipHolidays);

    /// <summary>การจองของรอบในช่วงวัน (ทุกสถานะ) — คีย์เดียวกับ engine: บริการ + วัน + เวลาเริ่ม</summary>
    private sealed record ClassBookingRow(Guid Id, Guid ServiceId, DateOnly Date, TimeOnly Start, BookingStatus Status, int GuestCount);

    private async Task<List<ClassBookingRow>> ClassBookingsAsync(Guid companyId, Guid siteId, IReadOnlyCollection<Guid> serviceIds, DateOnly from, DateOnly to)
    {
        if (serviceIds.Count == 0) return new();
        var a = from.ToDateTime(TimeOnly.MinValue);
        var b = to.ToDateTime(TimeOnly.MinValue).AddDays(1);
        return (await _db.SiteBookings.AsNoTracking()
                .Where(x => x.CompanyId == companyId && x.SiteId == siteId && serviceIds.Contains(x.BookingServiceId)
                    && x.BookingDate >= a && x.BookingDate < b)
                .Select(x => new { x.Id, x.BookingServiceId, x.BookingDate, x.StartTime, x.Status, x.GuestCount })
                .ToListAsync())
            .Select(x => new ClassBookingRow(x.Id, x.BookingServiceId, DateOnly.FromDateTime(x.BookingDate), x.StartTime, x.Status, x.GuestCount))
            .ToList();
    }

    private static bool InSession(Guid serviceId, DateOnly date, TimeOnly start, SiteClassSession s)
        => serviceId == s.BookingServiceId && date == s.SessionDate && BookingSlotEngine.ToMinute(start) == BookingSlotEngine.ToMinute(s.StartTime);

    private async Task<SiteClassSession> ClassSessionTrackedAsync(Guid companyId, Guid siteId, Guid sessionId)
        => await _db.SiteClassSessions.FirstOrDefaultAsync(x => x.Id == sessionId && x.SiteId == siteId && x.CompanyId == companyId)
           ?? throw new KeyNotFoundException("Class session not found.");

    // ─────────────── เทอม ───────────────

    public async Task<List<ClassTermResponse>> GetClassTermsAsync(Guid companyId, Guid siteId, Guid? serviceId)
    {
        var terms = await _db.SiteClassTerms.AsNoTracking()
            .Where(t => t.CompanyId == companyId && t.SiteId == siteId && (serviceId == null || t.BookingServiceId == serviceId))
            .OrderBy(t => t.StartDate).ThenBy(t => t.StartTime)
            .ToListAsync();
        if (terms.Count == 0) return new();
        var svcIds = terms.Select(t => t.BookingServiceId).Distinct().ToList();
        var services = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && svcIds.Contains(s.Id))
            .ToListAsync();
        var defaults = await SiteDefaultsAsync(companyId, siteId);
        var resIds = terms.Where(t => t.InstructorResourceId != null).Select(t => t.InstructorResourceId!.Value).Distinct().ToList();
        var names = resIds.Count == 0 ? new Dictionary<Guid, string>() : await _db.SiteBookingResources.AsNoTracking()
            .Where(r => r.CompanyId == companyId && resIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name);
        var today = DateOnly.FromDateTime(ClassNowLocal());
        var termIds = terms.Select(t => t.Id).ToList();
        var stats = await _db.SiteClassSessions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.TermId != null && termIds.Contains(s.TermId.Value))
            .GroupBy(s => s.TermId!.Value)
            .Select(g => new
            {
                TermId = g.Key,
                Upcoming = g.Count(s => s.SessionDate >= today && s.Status == ClassSessionStatus.Scheduled),
                Last = g.Max(s => (DateOnly?)s.SessionDate),
            })
            .ToListAsync();
        return terms.Select(t =>
        {
            var svc = services.FirstOrDefault(s => s.Id == t.BookingServiceId);
            var duration = svc == null ? 60 : BookingSlotEngine.RulesOf(svc, defaults).DurationMinutes;
            var days = ClassScheduleGenerator.ParseWeekdays(t.Weekdays);
            var st = stats.FirstOrDefault(x => x.TermId == t.Id);
            return new ClassTermResponse
            {
                Id = t.Id, BookingServiceId = t.BookingServiceId, ServiceName = svc?.Name ?? "",
                Title = t.Title, Weekdays = days.Select(d => (int)d).ToList(), WeekdaysLabel = ClassScheduleGenerator.WeekdaysLabel(days),
                StartTime = t.StartTime.ToString("HH:mm"), EndTime = ClassScheduleGenerator.EndOf(t.StartTime, duration).ToString("HH:mm"),
                StartDate = t.StartDate.ToString("yyyy-MM-dd"), EndDate = t.EndDate?.ToString("yyyy-MM-dd"),
                InstructorResourceId = t.InstructorResourceId,
                InstructorName = t.InstructorResourceId is Guid ir ? names.GetValueOrDefault(ir) : null,
                CapacityOverride = t.CapacityOverride,
                EffectiveCapacity = ClassScheduleGenerator.EffectiveCapacity(t.CapacityOverride, svc?.MaxCapacity ?? 1),
                SkipHolidays = t.SkipHolidays, Status = t.Status, StatusLabel = ClassScheduleGenerator.TermStatusLabel(t.Status), Notes = t.Notes,
                UpcomingSessions = st?.Upcoming ?? 0, LastSessionDate = st?.Last?.ToString("yyyy-MM-dd"),
            };
        }).ToList();
    }

    /// <summary>สร้าง/แก้เทอม — ตรวจด้วย <see cref="ClassScheduleGenerator.ValidateTerm"/> · ครูต้องรับบริการนี้ · ย้ายเทอมข้ามบริการ = ปฏิเสธ (ไม่เงียบ) ·
    /// แก้เทอมไม่แตะรอบที่สร้างแล้ว (ครู/ที่นั่งของรอบแก้ที่รอบ · วัน/เวลาที่เปลี่ยน ⇒ กด "สร้างรอบ" อีกครั้ง: รอบเก่าที่ไม่มีการจองถูกลบ ที่มีการจองคงไว้)</summary>
    public async Task<ClassTermResponse> SaveClassTermAsync(Guid companyId, Guid siteId, Guid? termId, SaveClassTermRequest r, string userId)
    {
        await EnsureSiteAsync(companyId, siteId);
        var svc = await ClassServiceAsync(companyId, siteId, r.BookingServiceId);
        var start = BookingSlotEngine.ParseTime(r.StartTime)
            ?? throw new BusinessRuleException("เวลาเริ่มคลาสต้องเป็นรูปแบบ HH:mm", ClassRule);
        var days = ClassScheduleGenerator.ParseWeekdays(string.Join(",", r.Weekdays ?? new List<int>()));
        var spec = new ClassTermSpec(days, start, await ClassDurationAsync(companyId, siteId, svc), DateOnly.FromDateTime(r.StartDate),
            r.EndDate is DateTime e ? DateOnly.FromDateTime(e) : null, r.InstructorResourceId, r.CapacityOverride, r.SkipHolidays);
        var errs = ClassScheduleGenerator.ValidateTerm(spec);
        if (errs.Count > 0) throw new BusinessRuleException(string.Join(" · ", errs), ClassRule);
        await EnsureClassInstructorAsync(companyId, siteId, svc.Id, r.InstructorResourceId);

        SiteClassTerm term;
        if (termId is Guid id)
        {
            term = await _db.SiteClassTerms.FirstOrDefaultAsync(t => t.Id == id && t.SiteId == siteId && t.CompanyId == companyId)
                ?? throw new KeyNotFoundException("Class term not found.");
            if (term.BookingServiceId != svc.Id)
                throw new BusinessRuleException("ย้ายเทอมไปบริการอื่นไม่ได้ — สร้างเทอมใหม่ที่บริการนั้น", ClassRule);
            term.UpdatedBy = userId;
            term.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            term = new SiteClassTerm { CompanyId = companyId, SiteId = siteId, BookingServiceId = svc.Id, CreatedBy = userId };
            _db.SiteClassTerms.Add(term);
        }
        term.Title = string.IsNullOrWhiteSpace(r.Title) ? null : r.Title.Trim();
        term.Weekdays = ClassScheduleGenerator.WeekdaysCsv(days);
        term.StartTime = start;
        term.StartDate = spec.StartDate;
        term.EndDate = spec.EndDate;
        term.InstructorResourceId = r.InstructorResourceId;
        term.CapacityOverride = r.CapacityOverride;
        term.SkipHolidays = r.SkipHolidays;
        term.Status = r.Status;
        term.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
        await _db.SaveChangesAsync();
        return (await GetClassTermsAsync(companyId, siteId, svc.Id)).First(x => x.Id == term.Id);
    }

    /// <summary>ลบเทอม — รอบที่ยังไม่ถึงของเทอมที่มีการจอง ⇒ ปฏิเสธ (ยกเลิกคลาสก่อน — ลูกค้าได้คืนเครดิต/ได้รับแจ้ง) · รอบที่ยังไม่ถึงที่ไม่มีการจอง ⇒ ลบ ·
    /// รอบที่ผ่านแล้วคงไว้เป็นประวัติ · บริการที่ไม่เหลือเทอม ⇒ กลับไปใช้เวลาทำการรายสัปดาห์ของบริการ (พฤติกรรมเดิม)</summary>
    public async Task<List<string>> DeleteClassTermAsync(Guid companyId, Guid siteId, Guid termId, string userId)
    {
        var notices = new List<string>();
        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockSiteBookingsAsync(companyId, siteId);
        var term = await _db.SiteClassTerms.FirstOrDefaultAsync(t => t.Id == termId && t.SiteId == siteId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Class term not found.");
        var today = DateOnly.FromDateTime(ClassNowLocal());
        var future = await _db.SiteClassSessions
            .Where(s => s.CompanyId == companyId && s.TermId == termId && s.SessionDate >= today)
            .ToListAsync();
        if (future.Count > 0)
        {
            var rows = await ClassBookingsAsync(companyId, siteId, new[] { term.BookingServiceId }, future.Min(s => s.SessionDate), future.Max(s => s.SessionDate));
            var withBookings = future.Where(s => s.Status == ClassSessionStatus.Scheduled
                && rows.Any(b => b.Status != BookingStatus.Cancelled && InSession(b.ServiceId, b.Date, b.Start, s))).ToList();
            if (withBookings.Count > 0)
                throw new BusinessRuleException($"เทอมนี้มี {withBookings.Count} รอบที่ยังไม่ถึงและมีผู้จองแล้ว — ยกเลิกคลาสรอบเหล่านั้นก่อน (ลูกค้าได้คืนเครดิต/ได้รับแจ้ง) แล้วค่อยลบเทอม",
                    ClassRule, 409);
            _db.SiteClassSessions.RemoveRange(future.Where(s => s.Status == ClassSessionStatus.Scheduled));
            notices.Add($"ลบรอบที่ยังไม่ถึงและไม่มีผู้จอง {future.Count(s => s.Status == ClassSessionStatus.Scheduled)} รอบ");
        }
        term.IsDeleted = true;
        term.UpdatedBy = userId;
        term.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        if (!await _db.SiteClassTerms.AsNoTracking().AnyAsync(t => t.CompanyId == companyId && t.BookingServiceId == term.BookingServiceId))
            notices.Add("บริการนี้ไม่เหลือเทอมคลาสแล้ว — รอบที่จองได้กลับไปใช้เวลาทำการรายสัปดาห์ของบริการ");
        return notices;
    }

    /// <summary>สร้างรอบ N สัปดาห์จากเทอม — แผนจาก <see cref="ClassScheduleGenerator.Plan"/> (ข้ามวันหยุดบริษัท/ช่วงปิด · คีย์ที่มีแล้วไม่สร้างซ้ำ ·
    /// รอบเก่าของเทอมที่ไม่ตรงแล้ว: ไม่มีการจอง ⇒ ลบ · มีการจอง ⇒ คงไว้ + แจ้ง) · ทั้งหมดใต้ล็อกไซต์ (ไม่ลบรอบที่มีคนจองเข้ามาระหว่างคำนวณ)</summary>
    public async Task<ClassGenerateResponse> GenerateClassSessionsAsync(Guid companyId, Guid siteId, Guid termId, GenerateClassSessionsRequest req, string userId)
    {
        if (ClassScheduleGenerator.WeeksProblem(req.Weeks) is string wp) throw new BusinessRuleException(wp, ClassRule);
        var today = DateOnly.FromDateTime(ClassNowLocal());
        var from = req.FromDate is DateTime f ? DateOnly.FromDateTime(f) : today;
        var to = from.AddDays(Math.Min(req.Weeks, ClassScheduleGenerator.MaxWeeks) * 7 - 1);

        await using var tx = await _db.Database.BeginTransactionAsync();
        await LockSiteBookingsAsync(companyId, siteId);
        var term = await _db.SiteClassTerms.AsNoTracking().FirstOrDefaultAsync(t => t.Id == termId && t.SiteId == siteId && t.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Class term not found.");
        if (term.Status != ClassTermStatus.Active)
            throw new BusinessRuleException("เทอมนี้สิ้นสุดแล้ว — เปิดสถานะ \"ใช้งาน\" ก่อนสร้างรอบ", ClassRule, 409);
        var svc = await ClassServiceAsync(companyId, siteId, term.BookingServiceId);
        var spec = SpecOf(term, await ClassDurationAsync(companyId, siteId, svc));

        // วันหยุดบริษัท (หน้า "วันหยุด") — วันที่ปฏิทินไทย (ตัวแปลงเดียวกับปฏิทินร้าน)
        var holidays = new Dictionary<DateOnly, string>();
        if (spec.SkipHolidays)
        {
            var hFrom = from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(-2);
            var hTo = to.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddDays(2);
            foreach (var h in await _db.PublicHolidays.AsNoTracking()
                         .Where(h => h.CompanyId == companyId && !h.IsDeleted && h.Date >= hFrom && h.Date <= hTo)
                         .Select(h => new { h.Date, h.NameTh }).ToListAsync())
                holidays.TryAdd(DateOnly.FromDateTime(ThaiDate.CalendarDateUtc(h.Date)), h.NameTh);
        }
        // ช่วงปิดรายวันที่ของตารางที่บริการใช้จริง (ของบริการเอง หรือของเว็บ — ตัวตัดสินเดียว ScheduleOf)
        var ownRows = (await _db.SiteBookingSlots.AsNoTracking()
            .Where(s => s.BookingServiceId == svc.Id && s.CompanyId == companyId && s.IsActive).ToListAsync()).Select(BookingSlotEngine.RowOf).ToList();
        var schedule = BookingSlotEngine.ScheduleOf(ownRows, (await SiteContextAsync(companyId, siteId)).Hours, out _);
        var closures = schedule.Where(x => x.IsBlocked && x.SpecificDate is DateOnly d && d >= from && d <= to)
            .Select(x => new ClassClosure(x.SpecificDate!.Value, x.Start, x.End)).ToList();

        var existing = await _db.SiteClassSessions
            .Where(s => s.CompanyId == companyId && s.BookingServiceId == svc.Id && s.SessionDate >= from && s.SessionDate <= to)
            .ToListAsync();
        var bookings = await ClassBookingsAsync(companyId, siteId, new[] { svc.Id }, from, to);
        var facts = existing.Select(s => new ClassExistingSession(s.Id, s.TermId, s.SessionDate, s.StartTime,
            s.Status == ClassSessionStatus.Cancelled,
            bookings.Count(b => b.Status != BookingStatus.Cancelled && InSession(b.ServiceId, b.Date, b.Start, s)))).ToList();

        var plan = ClassScheduleGenerator.Plan(spec, term.Id, from, req.Weeks, today, holidays, closures, facts);
        foreach (var o in plan.ToCreate)
            _db.SiteClassSessions.Add(new SiteClassSession
            {
                CompanyId = companyId, SiteId = siteId, BookingServiceId = svc.Id, TermId = term.Id,
                SessionDate = o.Date, StartTime = o.Start, EndTime = o.End,
                InstructorResourceId = o.InstructorResourceId, CapacityOverride = o.Capacity, CreatedBy = userId,
            });
        if (plan.ToRemove.Count > 0)
            _db.SiteClassSessions.RemoveRange(existing.Where(s => plan.ToRemove.Contains(s.Id)));
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        var resp = new ClassGenerateResponse
        {
            Created = plan.ToCreate.Count, AlreadyExisting = plan.AlreadyExisting, Removed = plan.ToRemove.Count, KeptWithBookings = plan.KeptWithBookings.Count,
            Skipped = plan.Skipped.Select(s => new ClassSkippedDateDto { Date = s.Date.ToString("yyyy-MM-dd"), Reason = s.Reason }).ToList(),
        };
        if (plan.KeptWithBookings.Count > 0)
            resp.Notices.Add($"มี {plan.KeptWithBookings.Count} รอบเดิมที่ไม่ตรงตารางใหม่แต่มีผู้จองแล้ว — คงไว้ (ยกเลิกคลาสรอบนั้นเองถ้าต้องการ — ลูกค้าได้คืนเครดิต/ได้รับแจ้ง)");
        return resp;
    }

    // ─────────────── รอบเรียน (ปฏิทิน + roster) ───────────────

    public async Task<List<ClassSessionResponse>> GetClassSessionsAsync(Guid companyId, Guid siteId, DateOnly from, DateOnly to, Guid? serviceId)
    {
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber > 62) to = from.AddDays(62);
        var sessions = await _db.SiteClassSessions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.SiteId == siteId && s.SessionDate >= from && s.SessionDate <= to
                && (serviceId == null || s.BookingServiceId == serviceId))
            .OrderBy(s => s.SessionDate).ThenBy(s => s.StartTime)
            .ToListAsync();
        if (sessions.Count == 0) return new();
        var svcIds = sessions.Select(s => s.BookingServiceId).Distinct().ToList();
        var services = await _db.SiteBookingServices.AsNoTracking()
            .Where(s => s.CompanyId == companyId && svcIds.Contains(s.Id)).ToListAsync();
        var bookings = await ClassBookingsAsync(companyId, siteId, svcIds, from, to);
        var names = await ResourceNamesAsync(companyId, sessions);
        var now = ClassNowLocal();
        return sessions.Select(s => MapSession(new ClassSessionResponse(), s, services.FirstOrDefault(x => x.Id == s.BookingServiceId),
            bookings.Where(b => InSession(b.ServiceId, b.Date, b.Start, s)).ToList(), names, now)).ToList();
    }

    private async Task<Dictionary<Guid, string>> ResourceNamesAsync(Guid companyId, IEnumerable<SiteClassSession> sessions)
    {
        var ids = sessions.SelectMany(s => new[] { s.InstructorResourceId, s.SubstituteResourceId })
            .Where(x => x != null).Select(x => x!.Value).Distinct().ToList();
        if (ids.Count == 0) return new();
        return await _db.SiteBookingResources.AsNoTracking()
            .Where(r => r.CompanyId == companyId && ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name);
    }

    private static T MapSession<T>(T dto, SiteClassSession s, SiteBookingService? svc, IReadOnlyList<ClassBookingRow> bookings,
        IReadOnlyDictionary<Guid, string> names, DateTime nowLocal) where T : ClassSessionResponse
    {
        var (taken, wait) = ClassScheduleGenerator.Fill(bookings.Select(b => (b.Status, b.GuestCount)), svc?.CapacityUnit ?? BookingCapacityUnit.Bookings);
        var cancelProblem = ClassScheduleGenerator.CancelProblem(nowLocal, s.SessionDate, s.EndTime, s.Status);
        dto.Id = s.Id; dto.TermId = s.TermId; dto.BookingServiceId = s.BookingServiceId; dto.ServiceName = svc?.Name ?? "";
        dto.Date = s.SessionDate.ToString("yyyy-MM-dd");
        dto.StartTime = s.StartTime.ToString("HH:mm"); dto.EndTime = s.EndTime.ToString("HH:mm");
        dto.Status = s.Status; dto.StatusLabel = ClassScheduleGenerator.SessionStatusLabel(s.Status); dto.CancelReason = s.CancelReason;
        dto.InstructorResourceId = s.InstructorResourceId;
        dto.InstructorName = s.InstructorResourceId is Guid i ? names.GetValueOrDefault(i) : null;
        dto.SubstituteResourceId = s.SubstituteResourceId;
        dto.SubstituteName = s.SubstituteResourceId is Guid su ? names.GetValueOrDefault(su) : null;
        dto.CapacityOverride = s.CapacityOverride;
        dto.Capacity = ClassScheduleGenerator.EffectiveCapacity(s.CapacityOverride, svc?.MaxCapacity ?? 1);
        dto.Taken = taken; dto.Waitlisted = wait; dto.Notes = s.Notes;
        dto.CanCancel = cancelProblem == null; dto.CancelBlockedReason = cancelProblem;
        return dto;
    }

    /// <summary>รอบเดียว + roster หน้าเคาน์เตอร์ — การจองทุกใบของรอบ (คีย์บริการ+วัน+เวลาเริ่ม) ทั้งชำระปกติและจองด้วยบัตร (สมุดเครดิต ExternalRef ของใบ) ·
    /// ปุ่มเช็คอินตัดสินที่ server (<see cref="ClassScheduleGenerator.CheckInProblem"/> + สถานะ)</summary>
    public async Task<ClassSessionDetailResponse> GetClassSessionAsync(Guid companyId, Guid siteId, Guid sessionId)
    {
        var s = await _db.SiteClassSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId && x.SiteId == siteId && x.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Class session not found.");
        var svc = await _db.SiteBookingServices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == s.BookingServiceId && x.CompanyId == companyId);
        var day = s.SessionDate.ToDateTime(TimeOnly.MinValue);
        var startMin = BookingSlotEngine.ToMinute(s.StartTime);
        var raw = await _db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.BookingServiceId == s.BookingServiceId
                && b.BookingDate >= day && b.BookingDate < day.AddDays(1))
            .Select(b => new
            {
                b.Id, b.BookingNumber, b.Status, b.StartTime, b.GuestCount, b.TotalAmount, b.PaidAmount, b.CreatedAt,
                b.GuestName, b.GuestPhone, b.GuestEmail,
                CustomerName = b.Customer != null ? b.Customer.FullName : null,
                CustomerPhone = b.Customer != null ? b.Customer.Phone : null,
                CustomerEmail = b.Customer != null ? b.Customer.Email : null,
            })
            .ToListAsync();
        var mine = raw.Where(b => BookingSlotEngine.ToMinute(b.StartTime) == startMin).ToList();
        var ids = mine.Select(b => b.Id).ToList();
        var passByRef = new Dictionary<string, string>();
        if (_creditLedger != null && ids.Count > 0)
        {
            var refs = ids.Select(CreditBookingLink.Ref).ToList();
            passByRef = (await (from cb in _db.CreditClassBookings.AsNoTracking()
                                join p in _db.CreditPasses.AsNoTracking() on cb.PassId equals p.Id
                                where cb.CompanyId == companyId && p.CompanyId == companyId && cb.ExternalRef != null && refs.Contains(cb.ExternalRef)
                                select new { cb.ExternalRef, p.PassNumber, cb.BookedAt }).ToListAsync())
                .GroupBy(x => x.ExternalRef!).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.BookedAt).First().PassNumber);
        }
        var positions = await WaitlistPositionsAsync(companyId, siteId, mine.Where(b => b.Status == BookingStatus.Waitlisted).Select(b => b.Id).ToList());
        var now = ClassNowLocal();
        var names = await ResourceNamesAsync(companyId, new[] { s });
        var dto = MapSession(new ClassSessionDetailResponse(), s, svc,
            mine.Select(b => new ClassBookingRow(b.Id, s.BookingServiceId, s.SessionDate, b.StartTime, b.Status, b.GuestCount)).ToList(), names, now);
        dto.Roster = mine
            .OrderBy(b => b.Status switch { BookingStatus.Waitlisted => 2, BookingStatus.Cancelled => 3, _ => 0 })
            .ThenBy(b => b.CreatedAt)
            .Select(b =>
            {
                var live = b.Status is BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.InProgress;
                var attendProblem = live ? ClassScheduleGenerator.CheckInProblem(now, s.SessionDate, s.StartTime, true, s.Status) : null;
                var noShowProblem = live ? ClassScheduleGenerator.CheckInProblem(now, s.SessionDate, s.StartTime, false, s.Status) : null;
                return new ClassRosterEntryDto
                {
                    BookingId = b.Id, BookingNumber = b.BookingNumber, Status = b.Status, StatusLabel = BookingStatusLabels.Customer(b.Status),
                    CustomerName = b.CustomerName ?? b.GuestName, Phone = b.CustomerPhone ?? b.GuestPhone, Email = b.CustomerEmail ?? b.GuestEmail,
                    GuestCount = b.GuestCount, TotalAmount = b.TotalAmount, PaidAmount = b.PaidAmount,
                    CreditPassNumber = passByRef.GetValueOrDefault(CreditBookingLink.Ref(b.Id)),
                    WaitlistPosition = positions.TryGetValue(b.Id, out var pos) ? pos : null,
                    CanMarkAttended = live && attendProblem == null,
                    CanMarkNoShow = live && noShowProblem == null,
                    CheckInBlockedReason = live ? attendProblem ?? noShowProblem : null,
                };
            }).ToList();
        dto.Instructors = await _db.SiteBookingResourceServices.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.BookingServiceId == s.BookingServiceId && !l.IsDeleted
                && l.Resource.SiteId == siteId && l.Resource.IsActive && !l.Resource.IsDeleted)
            .OrderBy(l => l.Resource.SortOrder).ThenBy(l => l.Resource.Name)
            .Select(l => new ClassInstructorOptionDto { Id = l.ResourceId, Name = l.Resource.Name })
            .ToListAsync();
        return dto;
    }

    /// <summary>แก้ที่นั่ง/หมายเหตุของรอบ — ที่นั่งน้อยกว่าที่จองแล้ว: ไม่ตัดใครออก (engine แค่ไม่รับเพิ่ม) แต่บอกผู้กด</summary>
    public async Task<ClassActionResponse> UpdateClassSessionAsync(Guid companyId, Guid siteId, Guid sessionId, UpdateClassSessionRequest req, string userId)
    {
        if (req.CapacityOverride is int c && (c < 1 || c > 10000))
            throw new BusinessRuleException("ที่นั่งของรอบต้องอยู่ระหว่าง 1–10,000 (เว้นว่าง = ความจุของบริการ)", ClassRule);
        var s = await ClassSessionTrackedAsync(companyId, siteId, sessionId);
        s.CapacityOverride = req.CapacityOverride;
        s.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        s.UpdatedBy = userId;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        var detail = await GetClassSessionAsync(companyId, siteId, sessionId);
        var notices = new List<string>();
        if (detail.Taken > detail.Capacity)
            notices.Add($"ที่นั่งใหม่ ({detail.Capacity}) น้อยกว่าที่จองแล้ว ({detail.Taken}) — ไม่ตัดผู้จองออก แต่รอบนี้จะไม่รับจองเพิ่ม");
        return new ClassActionResponse { Session = detail, Notices = notices };
    }

    // ─────────────── ครูแทน ───────────────

    /// <summary>ครูแทนทั้งรอบ — ครูต้องรับบริการนี้ + ไม่ติดงานอื่นที่ทับเวลา (การจองอื่น/คลาสอื่นที่สอนอยู่) · การจองที่ยังไม่เริ่มของรอบตามครูคนใหม่ทุกใบ ·
    /// แจ้งลูกค้า (เลือกได้) ผ่าน <c>IBookingNotifier</c> · ส่งล้ม = ไม่ล้มคำสั่ง แต่ประทับบนการจอง + ตอบผู้กด · <c>null</c> = กลับเป็นครูประจำ</summary>
    public async Task<ClassActionResponse> SetClassSubstituteAsync(Guid companyId, Guid siteId, Guid sessionId, ClassSubstituteRequest req, string userId)
    {
        var notices = new List<string>();
        var moved = new List<Guid>();
        string? teacherName = null;
        bool changed;
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            await LockSiteBookingsAsync(companyId, siteId);
            var s = await ClassSessionTrackedAsync(companyId, siteId, sessionId);
            if (s.Status == ClassSessionStatus.Cancelled)
                throw new BusinessRuleException("คลาสรอบนี้ถูกยกเลิกแล้ว — เปลี่ยนผู้สอนไม่ได้", ClassRule, 409);
            if (ClassNowLocal() > s.SessionDate.ToDateTime(s.EndTime))
                throw new BusinessRuleException("คลาสรอบนี้จบไปแล้ว — เปลี่ยนผู้สอนไม่ได้", ClassRule, 409);
            await EnsureClassInstructorAsync(companyId, siteId, s.BookingServiceId, req.ResourceId);
            var newSub = req.ResourceId == s.InstructorResourceId ? null : req.ResourceId;
            var effective = newSub ?? s.InstructorResourceId;
            changed = newSub != s.SubstituteResourceId;

            if (changed && effective is Guid eff)
            {
                var day = s.SessionDate.ToDateTime(TimeOnly.MinValue);
                var clashes = await _db.SiteBookings.AsNoTracking()
                    .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.ResourceId == eff
                        && b.BookingDate >= day && b.BookingDate < day.AddDays(1)
                        && b.Status != BookingStatus.Cancelled && b.Status != BookingStatus.Waitlisted)
                    .Select(b => new { b.BookingNumber, b.BookingServiceId, b.StartTime, b.EndTime })
                    .ToListAsync();
                var clash = clashes.FirstOrDefault(b => !InSession(b.BookingServiceId, s.SessionDate, b.StartTime, s)
                    && ClassScheduleGenerator.Overlaps(b.StartTime, b.EndTime, s.StartTime, s.EndTime));
                if (clash != null)
                    throw new BusinessRuleException($"ผู้สอนที่เลือกติดงานการจอง #{clash.BookingNumber} เวลา {clash.StartTime:HH\\:mm}–{clash.EndTime:HH\\:mm} — เลือกคนอื่น หรือย้ายงานนั้นก่อน",
                        ClassRule, 409);
                var otherClass = (await _db.SiteClassSessions.AsNoTracking()
                        .Where(o => o.CompanyId == companyId && o.SiteId == siteId && o.Id != s.Id && o.SessionDate == s.SessionDate
                            && o.Status == ClassSessionStatus.Scheduled
                            && (o.SubstituteResourceId == eff || (o.SubstituteResourceId == null && o.InstructorResourceId == eff)))
                        .ToListAsync())
                    .FirstOrDefault(o => ClassScheduleGenerator.Overlaps(o.StartTime, o.EndTime, s.StartTime, s.EndTime));
                if (otherClass != null)
                    throw new BusinessRuleException($"ผู้สอนที่เลือกสอนคลาสอื่นเวลา {otherClass.StartTime:HH\\:mm}–{otherClass.EndTime:HH\\:mm} อยู่แล้ว — เลือกคนอื่น", ClassRule, 409);
            }

            if (changed)
            {
                s.SubstituteResourceId = newSub;
                s.UpdatedBy = userId;
                s.UpdatedAt = DateTime.UtcNow;
                teacherName = effective is Guid tn
                    ? await _db.SiteBookingResources.AsNoTracking().Where(r => r.Id == tn && r.CompanyId == companyId).Select(r => r.Name).FirstOrDefaultAsync()
                    : null;
                var day = s.SessionDate.ToDateTime(TimeOnly.MinValue);
                var bookings = (await _db.SiteBookings
                        .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.BookingServiceId == s.BookingServiceId
                            && b.BookingDate >= day && b.BookingDate < day.AddDays(1))
                        .ToListAsync())
                    .Where(b => BookingSlotEngine.ToMinute(b.StartTime) == BookingSlotEngine.ToMinute(s.StartTime))
                    .ToList();
                var stamp = $"[{ClassRule}] ผู้สอนของคลาสรอบนี้เปลี่ยนเป็น {teacherName ?? "(ระบบจัดให้)"} {ClassNowLocal():dd/MM/yyyy HH:mm}";
                foreach (var b in bookings.Where(b => ClassScheduleGenerator.FollowsSubstitute(b.Status)))
                {
                    b.ResourceId = effective;
                    b.InternalNotes = DepositPolicyResolver.AppendNoteOnce(b.InternalNotes, stamp);
                    b.UpdatedBy = userId;
                    moved.Add(b.Id);
                }
                var started = bookings.Count(b => b.Status is BookingStatus.InProgress or BookingStatus.Completed);
                if (started > 0) notices.Add($"การจอง {started} ใบเริ่ม/เสร็จแล้ว — คงผู้ให้บริการที่ลงมือจริงไว้ (แก้ที่หน้าการจองถ้าจำเป็น)");
                await _db.SaveChangesAsync();
            }
            await tx.CommitAsync();
        }
        if (!changed) notices.Add("ผู้สอนของรอบนี้เป็นคนนี้อยู่แล้ว — ไม่ได้เปลี่ยน");
        else
        {
            notices.Add($"เปลี่ยนผู้สอนแล้ว — การจอง {moved.Count} ใบตามผู้สอนคนใหม่");
            if (req.Notify && moved.Count > 0)
                notices.AddRange(await NotifyClassBookingsAsync(companyId, siteId, sessionId, moved, ClassMessageKind.Substitute, teacherName, null));
        }
        return new ClassActionResponse { Session = await GetClassSessionAsync(companyId, siteId, sessionId), Notices = notices };
    }

    // ─────────────── ยกเลิก / เปิดอีกครั้ง ───────────────

    /// <summary>
    /// ร้านยกเลิกทั้งคลาส: ① ปิดรอบใต้ล็อกไซต์ (ไม่มีใครจองเพิ่ม · คิวสำรองเลื่อนเข้ารอบนี้ไม่ได้) ② ยกเลิกการจองทุกใบที่ยังไม่จบผ่าน
    /// <c>UpdateBookingStatusCoreAsync</c> เส้นเดิม (ล็อกต่อใบ + ด่านลำดับสถานะรอบ 208 · บัตรคอร์ส ⇒ <c>Release(byBusiness: true)</c> คืนเครดิตเต็ม ·
    /// มัดจำ/เอกสาร ⇒ กติกายกเลิกเดิม) ③ แจ้งลูกค้า (เลือกได้) · ใบที่ยกเลิกไม่สำเร็จ ⇒ ประทับบนการจอง + ตอบผู้กด (ไม่เงียบ)
    /// </summary>
    public async Task<ClassActionResponse> CancelClassSessionAsync(Guid companyId, Guid siteId, Guid sessionId, ClassCancelRequest req, string userId)
    {
        var notices = new List<string>();
        var reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
        SiteClassSession s;
        await using (var tx = await _db.Database.BeginTransactionAsync())
        {
            await LockSiteBookingsAsync(companyId, siteId);
            s = await ClassSessionTrackedAsync(companyId, siteId, sessionId);
            if (ClassScheduleGenerator.CancelProblem(ClassNowLocal(), s.SessionDate, s.EndTime, s.Status) is string problem)
                throw new BusinessRuleException(problem, ClassRule, 409);
            s.Status = ClassSessionStatus.Cancelled;
            s.CancelReason = reason;
            s.CancelledAt = DateTime.UtcNow;
            s.CancelledBy = userId;
            s.UpdatedBy = userId;
            s.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        var day = s.SessionDate.ToDateTime(TimeOnly.MinValue);
        var targets = (await _db.SiteBookings.AsNoTracking()
                .Where(b => b.CompanyId == companyId && b.SiteId == siteId && b.BookingServiceId == s.BookingServiceId
                    && b.BookingDate >= day && b.BookingDate < day.AddDays(1))
                .Select(b => new { b.Id, b.BookingNumber, b.StartTime, b.Status })
                .ToListAsync())
            .Where(b => BookingSlotEngine.ToMinute(b.StartTime) == BookingSlotEngine.ToMinute(s.StartTime) && ClassScheduleGenerator.CancelsWithSession(b.Status))
            .ToList();
        var cancelled = new List<Guid>();
        var cancelNote = $"[{ClassRule}] ร้านยกเลิกคลาส {s.SessionDate:dd/MM/yyyy} {s.StartTime:HH\\:mm}" + (reason == null ? "" : $" · เหตุผล: {reason}");
        foreach (var t in targets)
        {
            try
            {
                var r = await UpdateBookingStatusCoreAsync(companyId, siteId, t.Id, new UpdateBookingStatusRequest
                {
                    Status = BookingStatus.Cancelled,
                    CancellationReason = reason == null ? "ร้านยกเลิกคลาส" : $"ร้านยกเลิกคลาส: {reason}",
                    InternalNotes = cancelNote,
                }, userId, null);
                cancelled.Add(t.Id);
                notices.AddRange(r.ErpNotices.Select(n => $"#{t.BookingNumber}: {n}"));
            }
            catch (Exception ex) when (ex is BusinessRuleException or KeyNotFoundException)
            {
                // ล้มดัง: ใบนี้ยังเปิดอยู่ในรอบที่ยกเลิกแล้ว ⇒ ประทับบนการจอง + ตอบผู้กด (roster แสดงสถานะจริง)
                _logger.LogError(ex, "ยกเลิกคลาส {SessionId}: ยกเลิกการจอง {BookingNumber} ไม่สำเร็จ", sessionId, t.BookingNumber);
                _db.ChangeTracker.Clear();
                var noted = await _db.SiteBookings.FirstOrDefaultAsync(b => b.Id == t.Id && b.CompanyId == companyId);
                if (noted != null)
                {
                    noted.InternalNotes = DepositPolicyResolver.AppendNoteOnce(noted.InternalNotes,
                        $"[{ClassRule}-FAILED] ร้านยกเลิกคลาสรอบนี้แล้ว แต่ยกเลิกการจองนี้ไม่สำเร็จ: {ex.Message} — จัดการเองที่หน้าการจอง");
                    await _db.SaveChangesAsync();
                }
                notices.Add($"#{t.BookingNumber}: ยกเลิกไม่สำเร็จ — {ex.Message} (จัดการเองที่หน้าการจอง)");
            }
        }
        notices.Insert(0, $"ยกเลิกคลาสแล้ว — ยกเลิกการจอง {cancelled.Count}/{targets.Count} ใบ");
        if (req.Notify && cancelled.Count > 0)
            notices.AddRange(await NotifyClassBookingsAsync(companyId, siteId, sessionId, cancelled, ClassMessageKind.Cancelled, null, reason));
        return new ClassActionResponse { Session = await GetClassSessionAsync(companyId, siteId, sessionId), Notices = notices };
    }

    /// <summary>เปิดรอบที่ยกเลิกอีกครั้ง (กดยกเลิกผิด) — การจองที่ยกเลิกไปแล้วไม่ฟื้น (สถานะปลายทาง · เครดิตคืนแล้ว) ลูกค้าจองใหม่ได้</summary>
    public async Task<ClassActionResponse> ReopenClassSessionAsync(Guid companyId, Guid siteId, Guid sessionId, string userId)
    {
        var s = await ClassSessionTrackedAsync(companyId, siteId, sessionId);
        if (s.Status != ClassSessionStatus.Cancelled) throw new BusinessRuleException("คลาสรอบนี้ยังไม่ได้ยกเลิก", ClassRule, 409);
        if (ClassNowLocal() > s.SessionDate.ToDateTime(s.EndTime)) throw new BusinessRuleException("คลาสรอบนี้จบไปแล้ว — เปิดอีกครั้งไม่ได้", ClassRule, 409);
        s.Notes = DepositPolicyResolver.AppendNoteOnce(s.Notes,
            $"เปิดอีกครั้ง {ClassNowLocal():dd/MM/yyyy HH:mm} (เคยยกเลิก: {s.CancelReason ?? "-"}) — การจองที่ยกเลิกไปแล้วไม่ฟื้น");
        s.Status = ClassSessionStatus.Scheduled;
        s.CancelReason = null; s.CancelledAt = null; s.CancelledBy = null;
        s.UpdatedBy = userId;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new ClassActionResponse
        {
            Session = await GetClassSessionAsync(companyId, siteId, sessionId),
            Notices = new List<string> { "เปิดคลาสรอบนี้อีกครั้งแล้ว — การจองที่ถูกยกเลิกไปแล้วไม่ฟื้น ลูกค้าต้องจองใหม่" },
        };
    }

    // ─────────────── เช็คอิน ───────────────

    /// <summary>เช็คอินหน้าเคาน์เตอร์ — มาเรียน = เสร็จสิ้น · ไม่มา = No-show ผ่าน <c>UpdateBookingStatusCoreAsync</c> เส้นเดิม (บัตรคอร์ส ⇒ ตัดเครดิต + รับรู้รายได้ ·
    /// drop-in ⇒ เส้นเอกสาร/ริบมัดจำเดิม) · การจองต้องอยู่ในรอบนี้ · เวลา/สถานะตัดสินที่ <see cref="ClassScheduleGenerator.CheckInProblem"/></summary>
    public async Task<ClassActionResponse> CheckInClassAsync(Guid companyId, Guid siteId, Guid sessionId, ClassCheckInRequest req, string userId)
    {
        var s = await _db.SiteClassSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId && x.SiteId == siteId && x.CompanyId == companyId)
            ?? throw new KeyNotFoundException("Class session not found.");
        var b = await _db.SiteBookings.AsNoTracking()
            .Where(x => x.Id == req.BookingId && x.SiteId == siteId && x.CompanyId == companyId)
            .Select(x => new { x.BookingServiceId, x.BookingDate, x.StartTime, x.Status })
            .FirstOrDefaultAsync() ?? throw new KeyNotFoundException("Booking not found.");
        if (!InSession(b.BookingServiceId, DateOnly.FromDateTime(b.BookingDate), b.StartTime, s))
            throw new BusinessRuleException("การจองนี้ไม่ได้อยู่ในคลาสรอบนี้", ClassRule);
        if (ClassScheduleGenerator.CheckInProblem(ClassNowLocal(), s.SessionDate, s.StartTime, req.Attended, s.Status) is string problem)
            throw new BusinessRuleException(problem, ClassRule, 409);
        var r = await UpdateBookingStatusCoreAsync(companyId, siteId, req.BookingId, new UpdateBookingStatusRequest
        {
            Status = req.Attended ? BookingStatus.Completed : BookingStatus.NoShow,
            InternalNotes = $"[{ClassRule}] เช็คอินหน้าเคาน์เตอร์: {(req.Attended ? "มาเรียน" : "ไม่มา")}",
        }, userId, null);
        var notices = new List<string> { $"#{r.BookingNumber}: บันทึก{(req.Attended ? "มาเรียน" : "ไม่มา")}แล้ว" };
        notices.AddRange(r.ErpNotices);
        return new ClassActionResponse { Session = await GetClassSessionAsync(companyId, siteId, sessionId), Notices = notices };
    }

    // ─────────────── แจ้งลูกค้า (IBookingNotifier ตัวเดิม · อีเมล → LINE) ───────────────

    private enum ClassMessageKind { Cancelled, Substitute }

    /// <summary>ส่งข้อความถึงลูกค้าของการจองที่ระบุ · ประทับ <c>NotifyAttemptedAt</c>/<c>NotifyLastError</c> บนการจอง (ส่งล้ม = พนักงานเห็นบนการจอง) ·
    /// คืนข้อความถึงผู้กด · ไม่โยน (คำสั่งหลักสำเร็จแล้ว)</summary>
    private async Task<List<string>> NotifyClassBookingsAsync(Guid companyId, Guid siteId, Guid sessionId, IReadOnlyCollection<Guid> bookingIds,
        ClassMessageKind kind, string? teacher, string? reason)
    {
        var notices = new List<string>();
        var s = await _db.SiteClassSessions.AsNoTracking().FirstAsync(x => x.Id == sessionId && x.CompanyId == companyId);
        var siteName = await _db.Sites.AsNoTracking().Where(x => x.Id == siteId && x.CompanyId == companyId).Select(x => x.Name).FirstOrDefaultAsync() ?? "";
        var rows = await _db.SiteBookings
            .Where(b => b.CompanyId == companyId && b.SiteId == siteId && bookingIds.Contains(b.Id))
            .Select(b => new
            {
                Booking = b, ServiceName = b.BookingService.Name,
                CustomerEmail = b.Customer != null ? b.Customer.Email : null,
                CustomerName = b.Customer != null ? b.Customer.FullName : null,
                AuthProvider = b.Customer != null ? b.Customer.AuthProvider : null,
                AuthProviderId = b.Customer != null ? b.Customer.AuthProviderId : null,
            })
            .ToListAsync();
        if (_notifier == null)
        {
            foreach (var x in rows) { x.Booking.NotifyAttemptedAt = DateTime.UtcNow; x.Booking.NotifyLastError = "ระบบส่งข้อความไม่พร้อม — กรุณาแจ้งลูกค้าเอง"; }
            if (rows.Count > 0) await _db.SaveChangesAsync();
            notices.Add($"ระบบส่งข้อความไม่พร้อม — กรุณาแจ้งลูกค้า {rows.Count} รายเอง");
            return notices;
        }
        var sent = 0;
        foreach (var x in rows)
        {
            var b = x.Booking;
            var contact = ContactOf(b.GuestEmail, x.CustomerEmail, x.AuthProvider, x.AuthProviderId, CustomerLineTarget.ForBooking(b.Id, b.CustomerId));
            var name = x.CustomerName ?? b.GuestName ?? "";
            string subject, text;
            if (kind == ClassMessageKind.Cancelled)
            {
                subject = ClassScheduleGenerator.CancelledSubject(x.ServiceName, s.SessionDate, s.StartTime);
                text = ClassScheduleGenerator.CancelledMessage(name, x.ServiceName, siteName, s.SessionDate, s.StartTime, b.BookingNumber, reason,
                    await IsPaidWithCreditAsync(companyId, b.Id), b.PaidAmount > 0m);
            }
            else
            {
                subject = ClassScheduleGenerator.SubstituteSubject(x.ServiceName, s.SessionDate, s.StartTime);
                text = ClassScheduleGenerator.SubstituteMessage(name, x.ServiceName, siteName, s.SessionDate, s.StartTime, b.BookingNumber, teacher);
            }
            var eventKey = kind == ClassMessageKind.Cancelled ? CustomerNotifyEvents.ClassCancelled : CustomerNotifyEvents.ClassSubstitute;
            var result = await _notifier.SendAsync(new CustomerNotifyRequest(companyId, siteId, eventKey,
                CustomerNotifyKeys.Class(sessionId, b.Id, eventKey, teacher), contact, subject, text));
            b.NotifyAttemptedAt = DateTime.UtcNow;
            if (result.Sent)
            {
                b.NotifyLastError = result.Warning;   // รอบ 211 ฝ่ายค้าน: ส่งถึงทางถอย ⇒ ประทับคำเตือน (null = ปกติ)
                b.InternalNotes = DepositPolicyResolver.AppendNoteOnce(b.InternalNotes,
                    $"[{ClassRule}] แจ้งลูกค้า ({(kind == ClassMessageKind.Cancelled ? "คลาสยกเลิก" : "เปลี่ยนผู้สอน")}) ทาง {(result.Channel == "line" ? "LINE" : "อีเมล")} แล้ว");
                sent++;
            }
            else
            {
                b.NotifyLastError = result.Error;
                notices.Add($"#{b.BookingNumber}: แจ้งลูกค้าไม่สำเร็จ ({result.Error}) — กรุณาติดต่อลูกค้าเอง");
            }
        }
        if (rows.Count > 0) await _db.SaveChangesAsync();
        notices.Add($"แจ้งลูกค้าสำเร็จ {sent}/{rows.Count} ราย");
        return notices;
    }
}
