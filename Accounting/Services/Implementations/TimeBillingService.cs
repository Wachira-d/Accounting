using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.TimeBilling;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class TimeBillingService : ITimeBillingService
{
    private readonly AccountingDbContext _db;

    public TimeBillingService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Time Entries =====

    public async Task<TimeEntryResponse> CreateTimeEntryAsync(Guid companyId, CreateTimeEntryRequest request, string userId)
    {
        // Resolve billing rate if not specified
        var billingRate = request.BillingRate
            ?? await GetEffectiveRateAsync(companyId, request.EmployeeId, request.ContactId, request.ProjectId);

        var billableAmount = request.Category == "Billable" ? request.Hours * billingRate : 0m;

        var entry = new TimeEntry
        {
            CompanyId = companyId,
            EmployeeId = request.EmployeeId,
            UserId = Guid.TryParse(userId, out var uid) ? uid : null,
            ProjectId = request.ProjectId,
            ProjectTaskId = request.ProjectTaskId,
            ContactId = request.ContactId,
            EntryDate = request.EntryDate,
            Hours = request.Hours,
            Description = request.Description,
            Category = request.Category,
            BillingRate = billingRate,
            BillableAmount = billableAmount,
            Status = "Draft",
            CreatedBy = userId
        };

        _db.Set<TimeEntry>().Add(entry);
        await _db.SaveChangesAsync();

        return await MapToResponseAsync(entry);
    }

    public async Task<TimeEntryResponse> GetTimeEntryAsync(Guid companyId, Guid entryId)
    {
        var entry = await GetEntryWithRelationsAsync(companyId, entryId);
        return await MapToResponseAsync(entry);
    }

    public async Task<PagedResponse<TimeEntryResponse>> GetTimeEntriesAsync(Guid companyId, TimeEntryFilterRequest filter, PagedRequest request)
    {
        var query = _db.Set<TimeEntry>()
            .Where(e => e.CompanyId == companyId && !e.IsDeleted);

        if (filter.EmployeeId.HasValue)
            query = query.Where(e => e.EmployeeId == filter.EmployeeId.Value);

        if (filter.ProjectId.HasValue)
            query = query.Where(e => e.ProjectId == filter.ProjectId.Value);

        if (filter.ContactId.HasValue)
            query = query.Where(e => e.ContactId == filter.ContactId.Value);

        if (!string.IsNullOrWhiteSpace(filter.Category))
            query = query.Where(e => e.Category == filter.Category);

        if (filter.FromDate.HasValue)
            query = query.Where(e => e.EntryDate >= filter.FromDate.Value);

        if (filter.ToDate.HasValue)
            query = query.Where(e => e.EntryDate <= filter.ToDate.Value);

        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(e => (e.Description != null && e.Description.Contains(request.Search)));

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(e => e.EntryDate)
            .ThenByDescending(e => e.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var responses = new List<TimeEntryResponse>();
        foreach (var item in items)
        {
            responses.Add(await MapToResponseAsync(item));
        }

        return new PagedResponse<TimeEntryResponse>(
            responses, totalCount, request.Page, request.PageSize,
            (int)Math.Ceiling(totalCount / (double)request.PageSize));
    }

    public async Task<TimeEntryResponse> UpdateTimeEntryAsync(Guid companyId, Guid entryId, UpdateTimeEntryRequest request)
    {
        var entry = await GetEntryAsync(companyId, entryId);

        if (entry.Status != "Draft")
            throw new InvalidOperationException("Only draft time entries can be updated.");

        if (request.Hours.HasValue) entry.Hours = request.Hours.Value;
        if (request.Description != null) entry.Description = request.Description;
        if (request.Category != null) entry.Category = request.Category;
        if (request.BillingRate.HasValue) entry.BillingRate = request.BillingRate.Value;

        // Recalculate billable amount
        entry.BillableAmount = entry.Category == "Billable" ? entry.Hours * (entry.BillingRate ?? 0) : 0;

        await _db.SaveChangesAsync();

        return await MapToResponseAsync(entry);
    }

    public async Task<TimeEntryResponse> SubmitAsync(Guid companyId, Guid entryId)
    {
        var entry = await GetEntryAsync(companyId, entryId);

        if (entry.Status != "Draft")
            throw new InvalidOperationException("Only draft time entries can be submitted.");

        entry.Status = "Submitted";
        await _db.SaveChangesAsync();

        return await MapToResponseAsync(entry);
    }

    public async Task<TimeEntryResponse> ApproveAsync(Guid companyId, Guid entryId, string approvedBy)
    {
        var entry = await GetEntryAsync(companyId, entryId);

        if (entry.Status != "Submitted")
            throw new InvalidOperationException("Only submitted time entries can be approved.");

        entry.Status = "Approved";
        entry.ApprovedBy = approvedBy;
        await _db.SaveChangesAsync();

        return await MapToResponseAsync(entry);
    }

    public async Task DeleteAsync(Guid companyId, Guid entryId)
    {
        var entry = await GetEntryAsync(companyId, entryId);

        if (entry.IsBilled)
            throw new InvalidOperationException("Billed time entries cannot be deleted.");

        entry.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    // ===== Billing Rates =====

    public async Task<BillingRateResponse> CreateRateAsync(Guid companyId, CreateBillingRateRequest request)
    {
        var rate = new BillingRate
        {
            CompanyId = companyId,
            Name = request.Name,
            EmployeeId = request.EmployeeId,
            Role = request.Role,
            ContactId = request.ContactId,
            ProjectId = request.ProjectId,
            HourlyRate = request.HourlyRate,
            DailyRate = request.DailyRate,
            EffectiveFrom = request.EffectiveFrom,
            EffectiveTo = request.EffectiveTo,
            IsActive = true
        };

        _db.Set<BillingRate>().Add(rate);
        await _db.SaveChangesAsync();

        return await MapToRateResponseAsync(rate);
    }

    public async Task<List<BillingRateResponse>> GetRatesAsync(Guid companyId)
    {
        var rates = await _db.Set<BillingRate>()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted && r.IsActive)
            .OrderBy(r => r.Name)
            .ToListAsync();

        var responses = new List<BillingRateResponse>();
        foreach (var rate in rates)
        {
            responses.Add(await MapToRateResponseAsync(rate));
        }

        return responses;
    }

    public async Task<BillingRateResponse> UpdateRateAsync(Guid companyId, Guid rateId, UpdateBillingRateRequest request)
    {
        var rate = await _db.Set<BillingRate>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == rateId && !r.IsDeleted)
            ?? throw new InvalidOperationException("Billing rate not found.");

        if (request.HourlyRate.HasValue) rate.HourlyRate = request.HourlyRate.Value;
        if (request.DailyRate.HasValue) rate.DailyRate = request.DailyRate.Value;
        if (request.EffectiveTo.HasValue) rate.EffectiveTo = request.EffectiveTo.Value;
        if (request.IsActive.HasValue) rate.IsActive = request.IsActive.Value;

        await _db.SaveChangesAsync();

        return await MapToRateResponseAsync(rate);
    }

    public async Task<decimal> GetEffectiveRateAsync(Guid companyId, Guid? employeeId, Guid? contactId, Guid? projectId)
    {
        var now = DateTime.UtcNow.Date;

        // Priority: project-specific > client-specific > employee-specific > role-based > default
        var rates = await _db.Set<BillingRate>()
            .Where(r => r.CompanyId == companyId
                      && r.IsActive
                      && !r.IsDeleted
                      && r.EffectiveFrom <= now
                      && (r.EffectiveTo == null || r.EffectiveTo >= now))
            .ToListAsync();

        // Project + employee specific
        if (projectId.HasValue && employeeId.HasValue)
        {
            var rate = rates.FirstOrDefault(r => r.ProjectId == projectId && r.EmployeeId == employeeId);
            if (rate != null) return rate.HourlyRate;
        }

        // Project specific
        if (projectId.HasValue)
        {
            var rate = rates.FirstOrDefault(r => r.ProjectId == projectId && r.EmployeeId == null);
            if (rate != null) return rate.HourlyRate;
        }

        // Client + employee specific
        if (contactId.HasValue && employeeId.HasValue)
        {
            var rate = rates.FirstOrDefault(r => r.ContactId == contactId && r.EmployeeId == employeeId);
            if (rate != null) return rate.HourlyRate;
        }

        // Client specific
        if (contactId.HasValue)
        {
            var rate = rates.FirstOrDefault(r => r.ContactId == contactId && r.EmployeeId == null && r.ProjectId == null);
            if (rate != null) return rate.HourlyRate;
        }

        // Employee specific
        if (employeeId.HasValue)
        {
            var rate = rates.FirstOrDefault(r => r.EmployeeId == employeeId && r.ContactId == null && r.ProjectId == null);
            if (rate != null) return rate.HourlyRate;
        }

        // Default rate (no specific assignment)
        var defaultRate = rates
            .FirstOrDefault(r => r.EmployeeId == null && r.ContactId == null && r.ProjectId == null);

        return defaultRate?.HourlyRate ?? 0m;
    }

    // ===== Invoice Generation =====

    public async Task<Guid> GenerateInvoiceAsync(Guid companyId, GenerateTimeInvoiceRequest request, string createdBy)
    {
        // Get billable, approved, unbilled time entries for the contact
        var query = _db.Set<TimeEntry>()
            .Where(e => e.CompanyId == companyId
                      && e.ContactId == request.ContactId
                      && e.Category == "Billable"
                      && e.Status == "Approved"
                      && !e.IsBilled
                      && !e.IsDeleted);

        if (request.FromDate.HasValue)
            query = query.Where(e => e.EntryDate >= request.FromDate.Value);

        if (request.ToDate.HasValue)
            query = query.Where(e => e.EntryDate <= request.ToDate.Value);

        if (request.TimeEntryIds != null && request.TimeEntryIds.Count > 0)
            query = query.Where(e => request.TimeEntryIds.Contains(e.Id));

        var entries = await query
            .OrderBy(e => e.EntryDate)
            .ToListAsync();

        if (entries.Count == 0)
            throw new InvalidOperationException("No billable time entries found matching the criteria.");

        var totalAmount = entries.Sum(e => e.BillableAmount ?? 0);

        // Create the invoice document
        var document = new Document
        {
            CompanyId = companyId,
            // DRAFT- placeholder — เดิม hardcode "TINV-{วันที่}-{GUID}" (prefix ที่
            // ไม่มีในระบบเลขไหนเลย) และเพราะไม่ใช่ DRAFT- ตอนอนุมัติจึงไม่ได้เลข
            // จริงจาก DocumentNumberGenerator = เลขสุ่มติดใบถาวร ไม่เรียงลำดับ
            DocumentNumber = $"DRAFT-{Guid.NewGuid()}",
            DocumentType = DocumentType.Invoice,
            Status = DocumentStatus.Draft,
            DocumentDate = DateTime.UtcNow.Date,
            DueDate = DateTime.UtcNow.Date.AddDays(30),
            ContactId = request.ContactId,
            SubTotal = totalAmount,
            VatAmount = 0,
            TotalAmount = totalAmount,
            BalanceDue = totalAmount,
            Notes = $"Time billing invoice for {entries.Count} time entries",
            CreatedBy = createdBy
        };

        _db.Documents.Add(document);

        // Create document lines from time entries
        var lineOrder = 1;
        foreach (var entry in entries)
        {
            _db.Set<DocumentLine>().Add(new DocumentLine
            {
                DocumentId = document.Id,
                LineOrder = lineOrder++,
                Description = $"{entry.EntryDate:yyyy-MM-dd}: {entry.Description ?? "Time entry"} ({entry.Hours}h @ {entry.BillingRate:N2}/h)",
                Quantity = entry.Hours,
                Unit = "Hours",
                UnitPrice = entry.BillingRate ?? 0,
                Amount = entry.BillableAmount ?? 0,
                VatRate = 0,
                VatAmount = 0
            });

            // Mark time entries as billed
            entry.IsBilled = true;
            entry.Status = "Billed";
            entry.InvoiceDocumentId = document.Id;
        }

        await _db.SaveChangesAsync();

        return document.Id;
    }

    // ===== Reports =====

    public async Task<TimeSummaryResponse> GetTimeSummaryAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var entries = await _db.Set<TimeEntry>()
            .Where(e => e.CompanyId == companyId
                      && !e.IsDeleted
                      && e.EntryDate >= fromDate
                      && e.EntryDate <= toDate)
            .ToListAsync();

        var totalHours = entries.Sum(e => e.Hours);
        var billableHours = entries.Where(e => e.Category == "Billable").Sum(e => e.Hours);
        var nonBillableHours = totalHours - billableHours;
        var billableAmount = entries.Where(e => e.Category == "Billable").Sum(e => e.BillableAmount ?? 0);
        var billedAmount = entries.Where(e => e.IsBilled).Sum(e => e.BillableAmount ?? 0);
        var unbilledAmount = billableAmount - billedAmount;
        var utilizationPercent = totalHours > 0 ? billableHours / totalHours * 100 : 0;

        return new TimeSummaryResponse(
            fromDate, toDate,
            totalHours, billableHours, nonBillableHours,
            billableAmount, billedAmount, unbilledAmount,
            Math.Round(utilizationPercent, 2));
    }

    public async Task<List<UtilizationResponse>> GetUtilizationAsync(Guid companyId, DateTime fromDate, DateTime toDate)
    {
        var entries = await _db.Set<TimeEntry>()
            .Where(e => e.CompanyId == companyId
                      && !e.IsDeleted
                      && e.EntryDate >= fromDate
                      && e.EntryDate <= toDate)
            .ToListAsync();

        var grouped = entries
            .GroupBy(e => e.EmployeeId)
            .ToList();

        var results = new List<UtilizationResponse>();

        foreach (var group in grouped)
        {
            string? employeeName = null;
            if (group.Key.HasValue)
            {
                var employee = await _db.Set<Employee>()
                    .FirstOrDefaultAsync(e => e.Id == group.Key.Value);

                employeeName = employee != null
                    ? $"{employee.FirstNameEn ?? employee.FirstNameTh} {employee.LastNameEn ?? employee.LastNameTh}".Trim()
                    : null;
            }

            var totalHours = group.Sum(e => e.Hours);
            var billableHours = group.Where(e => e.Category == "Billable").Sum(e => e.Hours);
            var billableAmount = group.Where(e => e.Category == "Billable").Sum(e => e.BillableAmount ?? 0);
            var utilizationPercent = totalHours > 0 ? billableHours / totalHours * 100 : 0;

            results.Add(new UtilizationResponse(
                group.Key,
                employeeName ?? "Unassigned",
                totalHours,
                billableHours,
                Math.Round(utilizationPercent, 2),
                billableAmount));
        }

        return results.OrderByDescending(r => r.UtilizationPercent).ToList();
    }

    // ===== Private Helpers =====

    private async Task<TimeEntry> GetEntryAsync(Guid companyId, Guid entryId)
    {
        return await _db.Set<TimeEntry>()
            .FirstOrDefaultAsync(e => e.CompanyId == companyId && e.Id == entryId && !e.IsDeleted)
            ?? throw new InvalidOperationException("Time entry not found.");
    }

    private async Task<TimeEntry> GetEntryWithRelationsAsync(Guid companyId, Guid entryId)
    {
        return await _db.Set<TimeEntry>()
            .FirstOrDefaultAsync(e => e.CompanyId == companyId && e.Id == entryId && !e.IsDeleted)
            ?? throw new InvalidOperationException("Time entry not found.");
    }

    private async Task<TimeEntryResponse> MapToResponseAsync(TimeEntry e)
    {
        string? employeeName = null;
        if (e.EmployeeId.HasValue)
        {
            var employee = await _db.Set<Employee>()
                .FirstOrDefaultAsync(emp => emp.Id == e.EmployeeId.Value);
            if (employee != null)
                employeeName = $"{employee.FirstNameEn ?? employee.FirstNameTh} {employee.LastNameEn ?? employee.LastNameTh}".Trim();
        }

        string? projectName = null;
        if (e.ProjectId.HasValue)
        {
            var project = await _db.Set<Project>()
                .FirstOrDefaultAsync(p => p.Id == e.ProjectId.Value);
            projectName = project?.Name;
        }

        string? contactName = null;
        if (e.ContactId.HasValue)
        {
            var contact = await _db.Contacts
                .FirstOrDefaultAsync(c => c.Id == e.ContactId.Value);
            contactName = contact?.Name;
        }

        return new TimeEntryResponse(
            e.Id, e.EntryDate, e.Hours, e.Description, e.Category,
            e.BillingRate, e.BillableAmount,
            employeeName, projectName, contactName,
            e.IsBilled, e.Status);
    }

    private async Task<BillingRateResponse> MapToRateResponseAsync(BillingRate r)
    {
        string? employeeName = null;
        if (r.EmployeeId.HasValue)
        {
            var employee = await _db.Set<Employee>()
                .FirstOrDefaultAsync(e => e.Id == r.EmployeeId.Value);
            if (employee != null)
                employeeName = $"{employee.FirstNameEn ?? employee.FirstNameTh} {employee.LastNameEn ?? employee.LastNameTh}".Trim();
        }

        string? contactName = null;
        if (r.ContactId.HasValue)
        {
            var contact = await _db.Contacts
                .FirstOrDefaultAsync(c => c.Id == r.ContactId.Value);
            contactName = contact?.Name;
        }

        string? projectName = null;
        if (r.ProjectId.HasValue)
        {
            var project = await _db.Set<Project>()
                .FirstOrDefaultAsync(p => p.Id == r.ProjectId.Value);
            projectName = project?.Name;
        }

        return new BillingRateResponse(
            r.Id, r.Name, employeeName, r.Role, contactName, projectName,
            r.HourlyRate, r.DailyRate, r.EffectiveFrom, r.EffectiveTo, r.IsActive);
    }
}
