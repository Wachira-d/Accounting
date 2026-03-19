using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Recurring;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class RecurringTransactionService : IRecurringTransactionService
{
    private readonly AccountingDbContext _db;

    public RecurringTransactionService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<RecurringTransactionResponse> CreateAsync(Guid companyId, CreateRecurringTransactionRequest request, string createdBy)
    {
        var recurring = new RecurringTransaction
        {
            CompanyId = companyId,
            Name = request.Name,
            Description = request.Description,
            Frequency = request.Frequency,
            Status = RecurringStatus.Active,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            NextRunDate = request.StartDate,
            MaxRuns = request.MaxRuns,
            TemplateType = request.TemplateType,
            DocumentType = request.DocumentType,
            ContactId = request.ContactId,
            TemplateData = request.TemplateData,
            NotifyBeforeRun = request.NotifyBeforeRun,
            NotifyDaysBefore = request.NotifyDaysBefore,
            AutoApprove = request.AutoApprove,
            CreatedBy = createdBy
        };

        _db.RecurringTransactions.Add(recurring);
        await _db.SaveChangesAsync();
        return MapToResponse(recurring);
    }

    public async Task<RecurringTransactionResponse> GetByIdAsync(Guid companyId, Guid id)
    {
        var recurring = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");
        return MapToResponse(recurring);
    }

    public async Task<PagedResponse<RecurringTransactionResponse>> GetAllAsync(Guid companyId, PagedRequest request)
    {
        var query = _db.RecurringTransactions.Where(r => r.CompanyId == companyId);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(r => r.Name.Contains(request.Search));

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        return new PagedResponse<RecurringTransactionResponse>(
            items.Select(MapToResponse).ToList(),
            total, request.Page, request.PageSize,
            (int)Math.Ceiling(total / (double)request.PageSize));
    }

    public async Task<RecurringTransactionResponse> UpdateAsync(Guid companyId, Guid id, UpdateRecurringTransactionRequest request)
    {
        var recurring = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

        if (request.Name != null) recurring.Name = request.Name;
        if (request.Description != null) recurring.Description = request.Description;
        if (request.Frequency.HasValue) recurring.Frequency = request.Frequency.Value;
        if (request.EndDate.HasValue) recurring.EndDate = request.EndDate.Value;
        if (request.MaxRuns.HasValue) recurring.MaxRuns = request.MaxRuns.Value;
        if (request.TemplateData != null) recurring.TemplateData = request.TemplateData;
        if (request.NotifyBeforeRun.HasValue) recurring.NotifyBeforeRun = request.NotifyBeforeRun.Value;
        if (request.NotifyDaysBefore.HasValue) recurring.NotifyDaysBefore = request.NotifyDaysBefore.Value;
        if (request.AutoApprove.HasValue) recurring.AutoApprove = request.AutoApprove.Value;
        if (request.Status.HasValue) recurring.Status = request.Status.Value;

        await _db.SaveChangesAsync();
        return MapToResponse(recurring);
    }

    public async Task DeleteAsync(Guid companyId, Guid id)
    {
        var recurring = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

        recurring.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task<RecurringTransactionResponse> PauseAsync(Guid companyId, Guid id)
    {
        var recurring = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

        if (recurring.Status != RecurringStatus.Active)
            throw new InvalidOperationException("สามารถหยุดชั่วคราวได้เฉพาะรายการที่ Active เท่านั้น");

        recurring.Status = RecurringStatus.Paused;
        await _db.SaveChangesAsync();
        return MapToResponse(recurring);
    }

    public async Task<RecurringTransactionResponse> ResumeAsync(Guid companyId, Guid id)
    {
        var recurring = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

        if (recurring.Status != RecurringStatus.Paused)
            throw new InvalidOperationException("สามารถ resume ได้เฉพาะรายการที่ Paused เท่านั้น");

        recurring.Status = RecurringStatus.Active;
        if (recurring.NextRunDate < DateTime.UtcNow)
            recurring.NextRunDate = DateTime.UtcNow.Date;

        await _db.SaveChangesAsync();
        return MapToResponse(recurring);
    }

    public async Task<RecurringTransactionResponse> RunNowAsync(Guid companyId, Guid id, string performedBy)
    {
        var recurring = await _db.RecurringTransactions
            .FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายการที่เกิดซ้ำ");

        ExecuteRecurring(recurring);
        await _db.SaveChangesAsync();
        return MapToResponse(recurring);
    }

    public async Task ProcessDueRecurringTransactionsAsync()
    {
        var now = DateTime.UtcNow;
        var dueItems = await _db.RecurringTransactions
            .Where(r => r.Status == RecurringStatus.Active && r.NextRunDate <= now)
            .Where(r => r.EndDate == null || r.EndDate >= now)
            .ToListAsync();

        foreach (var recurring in dueItems)
        {
            if (recurring.MaxRuns.HasValue && recurring.TotalRuns >= recurring.MaxRuns.Value)
            {
                recurring.Status = RecurringStatus.Completed;
                continue;
            }

            ExecuteRecurring(recurring);
        }

        await _db.SaveChangesAsync();
    }

    private void ExecuteRecurring(RecurringTransaction recurring)
    {
        recurring.LastRunDate = DateTime.UtcNow;
        recurring.TotalRuns++;

        // Calculate next run date
        recurring.NextRunDate = recurring.Frequency switch
        {
            RecurringFrequency.Daily => recurring.NextRunDate.AddDays(1),
            RecurringFrequency.Weekly => recurring.NextRunDate.AddDays(7),
            RecurringFrequency.BiWeekly => recurring.NextRunDate.AddDays(14),
            RecurringFrequency.Monthly => recurring.NextRunDate.AddMonths(1),
            RecurringFrequency.Quarterly => recurring.NextRunDate.AddMonths(3),
            RecurringFrequency.SemiAnnual => recurring.NextRunDate.AddMonths(6),
            RecurringFrequency.Annual => recurring.NextRunDate.AddYears(1),
            _ => recurring.NextRunDate.AddMonths(1)
        };

        if (recurring.MaxRuns.HasValue && recurring.TotalRuns >= recurring.MaxRuns.Value)
            recurring.Status = RecurringStatus.Completed;
    }

    private static RecurringTransactionResponse MapToResponse(RecurringTransaction r) =>
        new(r.Id, r.Name, r.Description, r.Frequency, r.Status,
            r.StartDate, r.EndDate, r.NextRunDate, r.LastRunDate,
            r.TotalRuns, r.MaxRuns, r.TemplateType, r.DocumentType,
            r.ContactId, r.TemplateData, r.NotifyBeforeRun,
            r.NotifyDaysBefore, r.AutoApprove, r.CreatedAt);
}
