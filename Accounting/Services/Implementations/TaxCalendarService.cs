using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class TaxCalendarService : ITaxCalendarService
{
    private readonly AccountingDbContext _db;

    public TaxCalendarService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<List<TaxCalendarEventResponse>> GetEventsAsync(Guid companyId, int year, int? month = null)
    {
        var query = _db.Set<TaxCalendarEvent>()
            .Where(e => e.CompanyId == companyId && e.Year == year && !e.IsDeleted);

        if (month.HasValue)
            query = query.Where(e => e.Month == month.Value);

        var events = await query
            .OrderBy(e => e.DueDate)
            .ToListAsync();

        return events.Select(MapToResponse).ToList();
    }

    public async Task<TaxCalendarEventResponse> GetEventAsync(Guid companyId, Guid eventId)
    {
        var evt = await _db.Set<TaxCalendarEvent>()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการปฏิทินภาษี");

        return MapToResponse(evt);
    }

    public async Task InitializeYearAsync(Guid companyId, int year)
    {
        // Check if already initialized
        var existing = await _db.Set<TaxCalendarEvent>()
            .AnyAsync(e => e.CompanyId == companyId && e.Year == year);
        if (existing)
            throw new InvalidOperationException($"ปฏิทินภาษีปี {year} ถูกสร้างไว้แล้ว");

        // Thai tax form definitions: (code, name, isMonthly, dayOfMonth, eFilingExtraDays)
        var monthlyForms = new[]
        {
            ("ภ.พ.30", "แบบแสดงรายการภาษีมูลค่าเพิ่ม", 15, 8),
            ("ภ.พ.36", "แบบนำส่ง VAT จากการจ่ายค่าบริการต่างประเทศ", 7, 8),
            ("ภ.ง.ด.1", "แบบยื่นภาษีเงินได้หัก ณ ที่จ่าย (เงินเดือน)", 7, 8),
            ("ภ.ง.ด.3", "แบบยื่นภาษีเงินได้หัก ณ ที่จ่าย (บุคคลธรรมดา)", 7, 8),
            ("ภ.ง.ด.53", "แบบยื่นภาษีเงินได้หัก ณ ที่จ่าย (นิติบุคคล)", 7, 8),
            ("สปส.1-10", "แบบรายการแสดงการส่งเงินสมทบประกันสังคม", 15, 0),
        };

        var events = new List<TaxCalendarEvent>();

        // Create monthly events for all 12 months
        foreach (var (code, name, dueDay, eFilingExtra) in monthlyForms)
        {
            for (var month = 1; month <= 12; month++)
            {
                // Due date is in the following month (tax for Jan is due in Feb)
                var dueMonth = month == 12 ? 1 : month + 1;
                var dueYear = month == 12 ? year + 1 : year;
                var maxDay = DateTime.DaysInMonth(dueYear, dueMonth);
                var actualDueDay = Math.Min(dueDay, maxDay);
                var dueDate = new DateTime(dueYear, dueMonth, actualDueDay);

                // Adjust if due date falls on weekend
                if (dueDate.DayOfWeek == DayOfWeek.Saturday)
                    dueDate = dueDate.AddDays(2);
                else if (dueDate.DayOfWeek == DayOfWeek.Sunday)
                    dueDate = dueDate.AddDays(1);

                DateTime? eFilingDueDate = null;
                if (eFilingExtra > 0)
                    eFilingDueDate = dueDate.AddDays(eFilingExtra);

                events.Add(new TaxCalendarEvent
                {
                    CompanyId = companyId,
                    TaxFormCode = code,
                    TaxFormName = name,
                    Year = year,
                    Month = month,
                    DueDate = dueDate,
                    EFilingDueDate = eFilingDueDate,
                    Status = "Pending",
                    IsRecurring = true,
                    ReminderDaysBefore = 7
                });
            }
        }

        // Annual forms
        var annualForms = new[]
        {
            ("ภ.ง.ด.50", "แบบแสดงรายการภาษีเงินได้นิติบุคคล (ประจำปี)", new DateTime(year + 1, 5, 31), 8),
            ("ภ.ง.ด.51", "แบบแสดงรายการภาษีเงินได้นิติบุคคลครึ่งปี", new DateTime(year, 8, 31), 8),
            ("ภ.ง.ด.1ก", "แบบสรุปภาษีเงินได้หัก ณ ที่จ่าย (ประจำปี)", new DateTime(year + 1, 2, 28), 8),
            ("สบช.3", "แบบนำส่งงบการเงิน (กรมพัฒนาธุรกิจการค้า)", new DateTime(year + 1, 5, 31), 0),
            ("ภ.ง.ด.2ก", "แบบสรุป WHT เงินปันผล/ดอกเบี้ย ประจำปี", new DateTime(year + 1, 1, 31), 8),
            ("ภ.ง.ด.3ก", "แบบสรุป WHT บุคคลธรรมดา ประจำปี", new DateTime(year + 1, 1, 31), 8),
            ("ภ.ง.ด.53ก", "แบบสรุป WHT นิติบุคคล ประจำปี", new DateTime(year + 1, 1, 31), 8),
        };

        foreach (var (code, name, dueDate, eFilingExtra) in annualForms)
        {
            var adjustedDueDate = dueDate;
            if (adjustedDueDate.DayOfWeek == DayOfWeek.Saturday)
                adjustedDueDate = adjustedDueDate.AddDays(2);
            else if (adjustedDueDate.DayOfWeek == DayOfWeek.Sunday)
                adjustedDueDate = adjustedDueDate.AddDays(1);

            DateTime? eFilingDueDate = null;
            if (eFilingExtra > 0)
                eFilingDueDate = adjustedDueDate.AddDays(eFilingExtra);

            events.Add(new TaxCalendarEvent
            {
                CompanyId = companyId,
                TaxFormCode = code,
                TaxFormName = name,
                Year = year,
                Month = 0, // annual
                DueDate = adjustedDueDate,
                EFilingDueDate = eFilingDueDate,
                Status = "Pending",
                IsRecurring = true,
                ReminderDaysBefore = 14 // longer reminder for annual forms
            });
        }

        _db.Set<TaxCalendarEvent>().AddRange(events);
        await _db.SaveChangesAsync();
    }

    public async Task<TaxCalendarEventResponse> UpdateEventAsync(Guid companyId, Guid eventId, UpdateTaxCalendarEventRequest request)
    {
        var evt = await _db.Set<TaxCalendarEvent>()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.CompanyId == companyId && !e.IsDeleted)
            ?? throw new KeyNotFoundException("ไม่พบรายการปฏิทินภาษี");

        if (request.Status != null) evt.Status = request.Status;
        if (request.FiledDate.HasValue) evt.FiledDate = request.FiledDate.Value;
        if (request.FilingReference != null) evt.FilingReference = request.FilingReference;
        if (request.TaxAmount.HasValue) evt.TaxAmount = request.TaxAmount.Value;
        if (request.Notes != null) evt.Notes = request.Notes;
        if (request.ReminderDaysBefore.HasValue) evt.ReminderDaysBefore = request.ReminderDaysBefore.Value;

        evt.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapToResponse(evt);
    }

    public async Task<List<TaxCalendarEventResponse>> GetUpcomingAsync(Guid companyId, int daysAhead = 30)
    {
        var today = DateTime.UtcNow.Date;
        var cutoff = today.AddDays(daysAhead);

        var events = await _db.Set<TaxCalendarEvent>()
            .Where(e => e.CompanyId == companyId
                && !e.IsDeleted
                && e.Status != "Filed"
                && e.Status != "NotApplicable"
                && e.DueDate >= today
                && e.DueDate <= cutoff)
            .OrderBy(e => e.DueDate)
            .ToListAsync();

        return events.Select(MapToResponse).ToList();
    }

    public async Task<List<TaxCalendarEventResponse>> GetOverdueAsync(Guid companyId)
    {
        var today = DateTime.UtcNow.Date;

        var events = await _db.Set<TaxCalendarEvent>()
            .Where(e => e.CompanyId == companyId
                && !e.IsDeleted
                && e.Status != "Filed"
                && e.Status != "NotApplicable"
                && e.DueDate < today)
            .OrderBy(e => e.DueDate)
            .ToListAsync();

        // Mark overdue events
        foreach (var evt in events.Where(e => e.Status == "Pending" || e.Status == "InProgress"))
        {
            evt.Status = "Overdue";
        }

        if (events.Any(e => e.Status == "Overdue"))
            await _db.SaveChangesAsync();

        return events.Select(MapToResponse).ToList();
    }

    public async Task ProcessRemindersAsync()
    {
        var today = DateTime.UtcNow.Date;

        var events = await _db.Set<TaxCalendarEvent>()
            .Where(e => !e.IsDeleted
                && !e.ReminderSent
                && e.Status != "Filed"
                && e.Status != "NotApplicable"
                && e.DueDate >= today)
            .ToListAsync();

        foreach (var evt in events)
        {
            var reminderDate = evt.DueDate.AddDays(-evt.ReminderDaysBefore);
            if (today >= reminderDate)
            {
                evt.ReminderSent = true;
                // In production, this would trigger a notification via INotificationService
            }
        }

        await _db.SaveChangesAsync();
    }

    // ===== Mapping =====

    private static TaxCalendarEventResponse MapToResponse(TaxCalendarEvent e)
    {
        var daysUntilDue = (int)(e.DueDate.Date - DateTime.UtcNow.Date).TotalDays;
        return new TaxCalendarEventResponse(
            e.Id, e.TaxFormCode, e.TaxFormName, e.Year, e.Month,
            e.DueDate, e.EFilingDueDate, e.Status, e.FiledDate,
            e.FilingReference, e.TaxAmount, e.ReminderDaysBefore,
            e.ReminderSent, daysUntilDue);
    }
}
