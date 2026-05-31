using Accounting.Data;
using Accounting.Models.DTOs.Hr;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public interface ICompensationProfileService
{
    Task<CompanyCompensationDefaultsResponse> GetDefaultsAsync(Guid companyId, CancellationToken ct = default);
    Task<CompanyCompensationDefaultsResponse> UpdateDefaultsAsync(Guid companyId, CompanyCompensationDefaultsRequest req, CancellationToken ct = default);

    Task<CompensationProfileResponse> GetProfileAsync(Guid companyId, Guid employeeId, CancellationToken ct = default);
    Task<CompensationProfileResponse> UpsertProfileAsync(Guid companyId, Guid employeeId, CompensationProfileRequest req, CancellationToken ct = default);
    Task DeleteProfileAsync(Guid companyId, Guid employeeId, CancellationToken ct = default);

    /// <summary>Resolve effective rates for an employee — merges
    /// per-employee profile over company defaults. Used by payroll
    /// calc + the API response.</summary>
    Task<CompensationEffectiveRates> ResolveEffectiveAsync(Guid companyId, Guid employeeId, CancellationToken ct = default);

    /// <summary>What-if preview: read EmployeeProjectTime rows in the
    /// window and apply the employee's effective rates to compute OT
    /// pay + per-diem + accommodation + OT-meal allowance. Doesn't
    /// post anything. Useful for the HR review screen before running
    /// the actual payroll.</summary>
    Task<AttendancePayPreview> PreviewAttendancePayAsync(Guid companyId, Guid employeeId,
        DateTime from, DateTime to, CancellationToken ct = default);
}

public class CompensationProfileService : ICompensationProfileService
{
    private readonly AccountingDbContext _db;

    public CompensationProfileService(AccountingDbContext db) { _db = db; }

    public async Task<CompanyCompensationDefaultsResponse> GetDefaultsAsync(Guid companyId, CancellationToken ct = default)
    {
        var d = await GetOrCreateDefaultsAsync(companyId, ct);
        return Map(d);
    }

    public async Task<CompanyCompensationDefaultsResponse> UpdateDefaultsAsync(Guid companyId, CompanyCompensationDefaultsRequest req, CancellationToken ct = default)
    {
        var d = await GetOrCreateDefaultsAsync(companyId, ct);
        if (req.OvertimeRateMultiplierWeekday.HasValue) d.OvertimeRateMultiplierWeekday = req.OvertimeRateMultiplierWeekday.Value;
        if (req.OvertimeRateMultiplierHoliday.HasValue) d.OvertimeRateMultiplierHoliday = req.OvertimeRateMultiplierHoliday.Value;
        if (req.PerDiemRate.HasValue) d.PerDiemRate = req.PerDiemRate.Value;
        if (req.AccommodationAllowance.HasValue) d.AccommodationAllowance = req.AccommodationAllowance.Value;
        if (req.OvertimeMealAllowance.HasValue) d.OvertimeMealAllowance = req.OvertimeMealAllowance.Value;
        if (req.StandardWorkHoursPerDay.HasValue) d.StandardWorkHoursPerDay = req.StandardWorkHoursPerDay.Value;
        if (req.StandardWorkDaysPerMonth.HasValue) d.StandardWorkDaysPerMonth = req.StandardWorkDaysPerMonth.Value;
        d.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Map(d);
    }

    public async Task<CompensationProfileResponse> GetProfileAsync(Guid companyId, Guid employeeId, CancellationToken ct = default)
    {
        var emp = await GetEmployeeBriefAsync(companyId, employeeId, ct);
        var prof = await _db.EmployeeCompensationProfiles
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.EmployeeId == employeeId && !p.IsDeleted, ct);
        var effective = await ResolveEffectiveAsync(companyId, employeeId, ct);
        return new CompensationProfileResponse(
            employeeId, emp.Code, emp.Name,
            prof?.OvertimeRateMultiplierWeekday,
            prof?.OvertimeRateMultiplierHoliday,
            prof?.PerDiemRate,
            prof?.AccommodationAllowance,
            prof?.OvertimeMealAllowance,
            prof?.CustomBenefitsJson,
            effective);
    }

    public async Task<CompensationProfileResponse> UpsertProfileAsync(Guid companyId, Guid employeeId, CompensationProfileRequest req, CancellationToken ct = default)
    {
        var emp = await GetEmployeeBriefAsync(companyId, employeeId, ct);
        var prof = await _db.EmployeeCompensationProfiles
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.EmployeeId == employeeId && !p.IsDeleted, ct);
        if (prof == null)
        {
            prof = new EmployeeCompensationProfile { CompanyId = companyId, EmployeeId = employeeId };
            _db.EmployeeCompensationProfiles.Add(prof);
        }
        prof.OvertimeRateMultiplierWeekday = req.OvertimeRateMultiplierWeekday;
        prof.OvertimeRateMultiplierHoliday = req.OvertimeRateMultiplierHoliday;
        prof.PerDiemRate = req.PerDiemRate;
        prof.AccommodationAllowance = req.AccommodationAllowance;
        prof.OvertimeMealAllowance = req.OvertimeMealAllowance;
        prof.CustomBenefitsJson = req.CustomBenefitsJson;
        prof.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return await GetProfileAsync(companyId, employeeId, ct);
    }

    public async Task DeleteProfileAsync(Guid companyId, Guid employeeId, CancellationToken ct = default)
    {
        var prof = await _db.EmployeeCompensationProfiles
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.EmployeeId == employeeId && !p.IsDeleted, ct);
        if (prof == null) return;
        prof.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<CompensationEffectiveRates> ResolveEffectiveAsync(Guid companyId, Guid employeeId, CancellationToken ct = default)
    {
        var defaults = await GetOrCreateDefaultsAsync(companyId, ct);
        var prof = await _db.EmployeeCompensationProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.CompanyId == companyId && p.EmployeeId == employeeId && !p.IsDeleted, ct);
        return new CompensationEffectiveRates(
            prof?.OvertimeRateMultiplierWeekday ?? defaults.OvertimeRateMultiplierWeekday,
            prof?.OvertimeRateMultiplierHoliday ?? defaults.OvertimeRateMultiplierHoliday,
            prof?.PerDiemRate ?? defaults.PerDiemRate,
            prof?.AccommodationAllowance ?? defaults.AccommodationAllowance,
            prof?.OvertimeMealAllowance ?? defaults.OvertimeMealAllowance,
            defaults.StandardWorkHoursPerDay,
            defaults.StandardWorkDaysPerMonth);
    }

    public async Task<AttendancePayPreview> PreviewAttendancePayAsync(Guid companyId, Guid employeeId,
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var emp = await _db.Employees
            .Where(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            .Select(e => new { e.Id, e.EmployeeCode, e.FirstNameTh, e.LastNameTh, e.BaseSalary, e.SalaryType })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");

        var effective = await ResolveEffectiveAsync(companyId, employeeId, ct);

        // Hourly base — for Monthly salary, derive from defaults; for
        // Daily, hourly = daily / hoursPerDay; for Hourly, BaseSalary
        // IS the hourly rate.
        var hourly = emp.SalaryType switch
        {
            "Hourly" => emp.BaseSalary,
            "Daily" => effective.StandardWorkHoursPerDay > 0
                ? emp.BaseSalary / effective.StandardWorkHoursPerDay : 0,
            _ /* Monthly */ => (effective.StandardWorkDaysPerMonth * effective.StandardWorkHoursPerDay) > 0
                ? emp.BaseSalary / (effective.StandardWorkDaysPerMonth * effective.StandardWorkHoursPerDay) : 0,
        };

        var rows = await _db.EmployeeProjectTimes
            .Where(t => t.CompanyId == companyId
                && t.EmployeeId == employeeId
                && t.WorkDate >= from.Date && t.WorkDate <= to.Date
                && !t.IsDeleted)
            .OrderBy(t => t.WorkDate)
            .ToListAsync(ct);

        decimal regular = 0, otWeekday = 0, otHoliday = 0;
        int perDiemDays = 0, accomNights = 0, otMealDays = 0;
        var days = new List<AttendanceDayDetail>();

        // Group per day so a multi-row day (split across projects) counts
        // per-diem / OT-meal / accommodation flags at most once.
        foreach (var grp in rows.GroupBy(r => r.WorkDate.Date))
        {
            var dayHours = grp.Sum(r => r.Hours);
            var dayOt = grp.Sum(r => r.OvertimeHours ?? 0);
            var dayRegular = Math.Max(0, dayHours - dayOt);
            var dayIsHoliday = grp.Any(r => r.IsHoliday);
            var dayPerDiem = grp.Any(r => r.HasPerDiem);
            var dayAccom = grp.Any(r => r.HasAccommodation);
            var dayOtMeal = grp.Any(r => r.HasOvertimeMeal);

            regular += dayRegular;
            if (dayIsHoliday) otHoliday += dayOt; else otWeekday += dayOt;
            var dayOtPay = dayOt * hourly * (dayIsHoliday
                ? effective.OvertimeRateMultiplierHoliday
                : effective.OvertimeRateMultiplierWeekday);
            var dayAllowance = (dayPerDiem ? effective.PerDiemRate : 0)
                + (dayAccom ? effective.AccommodationAllowance : 0)
                + (dayOtMeal ? effective.OvertimeMealAllowance : 0);
            if (dayPerDiem) perDiemDays++;
            if (dayAccom) accomNights++;
            if (dayOtMeal) otMealDays++;

            days.Add(new AttendanceDayDetail(
                grp.Key, dayHours, dayOt, dayIsHoliday,
                dayPerDiem, dayAccom, dayOtMeal,
                Math.Round(dayOtPay, 2, MidpointRounding.AwayFromZero),
                Math.Round(dayAllowance, 2, MidpointRounding.AwayFromZero)));
        }

        var otPay = Math.Round(otWeekday * hourly * effective.OvertimeRateMultiplierWeekday
            + otHoliday * hourly * effective.OvertimeRateMultiplierHoliday, 2, MidpointRounding.AwayFromZero);
        var perDiemTotal = perDiemDays * effective.PerDiemRate;
        var accomTotal = accomNights * effective.AccommodationAllowance;
        var otMealTotal = otMealDays * effective.OvertimeMealAllowance;

        return new AttendancePayPreview(
            employeeId, emp.EmployeeCode, $"{emp.FirstNameTh} {emp.LastNameTh}".Trim(),
            from.Date, to.Date, emp.BaseSalary,
            Math.Round(hourly, 2, MidpointRounding.AwayFromZero),
            effective, regular, otWeekday, otHoliday, otPay,
            perDiemDays, perDiemTotal,
            accomNights, accomTotal,
            otMealDays, otMealTotal,
            otPay + perDiemTotal + accomTotal + otMealTotal,
            days);
    }

    private async Task<CompanyCompensationDefaults> GetOrCreateDefaultsAsync(Guid companyId, CancellationToken ct)
    {
        var d = await _db.CompanyCompensationDefaults
            .FirstOrDefaultAsync(x => x.CompanyId == companyId && !x.IsDeleted, ct);
        if (d == null)
        {
            d = new CompanyCompensationDefaults { CompanyId = companyId };
            _db.CompanyCompensationDefaults.Add(d);
            await _db.SaveChangesAsync(ct);
        }
        return d;
    }

    private async Task<(string Code, string Name)> GetEmployeeBriefAsync(Guid companyId, Guid employeeId, CancellationToken ct)
    {
        var emp = await _db.Employees
            .Where(e => e.Id == employeeId && e.CompanyId == companyId && !e.IsDeleted)
            .Select(e => new { e.EmployeeCode, e.FirstNameTh, e.LastNameTh })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("ไม่พบพนักงาน");
        return (emp.EmployeeCode, $"{emp.FirstNameTh} {emp.LastNameTh}".Trim());
    }

    private static CompanyCompensationDefaultsResponse Map(CompanyCompensationDefaults d) =>
        new(d.OvertimeRateMultiplierWeekday, d.OvertimeRateMultiplierHoliday,
            d.PerDiemRate, d.AccommodationAllowance, d.OvertimeMealAllowance,
            d.StandardWorkHoursPerDay, d.StandardWorkDaysPerMonth);
}
