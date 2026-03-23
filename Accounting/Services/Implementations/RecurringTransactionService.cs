using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Recurring;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Implementations;

public class RecurringTransactionService : IRecurringTransactionService
{
    private readonly AccountingDbContext _db;
    private readonly IDocumentService _documentService;
    private readonly IAccountingService _accountingService;
    private readonly ILogger<RecurringTransactionService> _logger;
    private readonly IErrorLogService _errorLogService;

    public RecurringTransactionService(
        AccountingDbContext db,
        IDocumentService documentService,
        IAccountingService accountingService,
        ILogger<RecurringTransactionService> logger,
        IErrorLogService errorLogService)
    {
        _db = db;
        _documentService = documentService;
        _accountingService = accountingService;
        _logger = logger;
        _errorLogService = errorLogService;
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

        await ExecuteRecurringAsync(recurring, performedBy);
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

            try
            {
                await ExecuteRecurringAsync(recurring, "System");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute recurring transaction {Id} ({Name})", recurring.Id, recurring.Name);
                await _errorLogService.LogErrorAsync(ex, $"RecurringTransaction.Execute/{recurring.Id}");
            }
        }

        await _db.SaveChangesAsync();
    }

    private async Task ExecuteRecurringAsync(RecurringTransaction recurring, string performedBy)
    {
        // Create actual document or journal entry from template
        if (!string.IsNullOrEmpty(recurring.TemplateData))
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var templateType = recurring.TemplateType?.ToLowerInvariant() ?? "document";

            if (templateType == "journal")
            {
                await CreateJournalFromTemplateAsync(recurring, performedBy, jsonOptions);
            }
            else
            {
                await CreateDocumentFromTemplateAsync(recurring, performedBy, jsonOptions);
            }
        }

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

    private async Task CreateDocumentFromTemplateAsync(RecurringTransaction recurring, string performedBy, JsonSerializerOptions jsonOptions)
    {
        try
        {
            using var doc = JsonDocument.Parse(recurring.TemplateData!);
            var root = doc.RootElement;

            var docType = recurring.DocumentType ?? DocumentType.Invoice;
            var contactId = recurring.ContactId;

            // Extract lines from template
            var lines = new List<Models.DTOs.Document.DocumentLineRequest>();
            if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in linesEl.EnumerateArray())
                {
                    lines.Add(new Models.DTOs.Document.DocumentLineRequest(
                        Description: line.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                        Quantity: line.TryGetProperty("quantity", out var qty) ? qty.GetDecimal() : 1,
                        UnitPrice: line.TryGetProperty("unitPrice", out var up) ? up.GetDecimal() : 0,
                        Unit: line.TryGetProperty("unit", out var unit) ? unit.GetString() : null,
                        DiscountPercent: line.TryGetProperty("discountPercent", out var dp) ? dp.GetDecimal() : 0,
                        VatRate: line.TryGetProperty("vatRate", out var vr) ? vr.GetDecimal() : 7,
                        WithholdingTaxRate: line.TryGetProperty("withholdingTaxRate", out var wt) ? wt.GetDecimal() : 0,
                        AccountId: line.TryGetProperty("accountId", out var aid) && aid.ValueKind == JsonValueKind.String ? Guid.Parse(aid.GetString()!) : null
                    ));
                }
            }

            var request = new Models.DTOs.Document.CreateDocumentRequest(
                DocumentType: docType,
                DocumentDate: DateTime.UtcNow,
                DueDate: root.TryGetProperty("dueDays", out var dd) ? DateTime.UtcNow.AddDays(dd.GetInt32()) : DateTime.UtcNow.AddDays(30),
                ContactId: contactId ?? Guid.Empty,
                Reference: $"AUTO-{recurring.Name}",
                Notes: root.TryGetProperty("notes", out var notes) ? notes.GetString() : null,
                Lines: lines
            );

            var result = await _documentService.CreateDocumentAsync(recurring.CompanyId, request, performedBy);

            // Auto-approve if configured
            if (recurring.AutoApprove && result != null)
            {
                try
                {
                    await _documentService.ApproveDocumentAsync(recurring.CompanyId, result.Id, performedBy);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-approve failed for recurring document {DocId}", result.Id);
                    await _errorLogService.LogErrorAsync(ex, $"RecurringTransaction.AutoApprove/{result.Id}");
                }
            }

            _logger.LogInformation("Created document {DocNumber} from recurring {RecurringId}", result?.DocumentNumber, recurring.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create document from recurring template {RecurringId}", recurring.Id);
            throw;
        }
    }

    private async Task CreateJournalFromTemplateAsync(RecurringTransaction recurring, string performedBy, JsonSerializerOptions jsonOptions)
    {
        try
        {
            using var doc = JsonDocument.Parse(recurring.TemplateData!);
            var root = doc.RootElement;

            var lines = new List<Models.DTOs.Accounting.JournalLineRequest>();
            if (root.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in linesEl.EnumerateArray())
                {
                    lines.Add(new Models.DTOs.Accounting.JournalLineRequest(
                        AccountId: line.TryGetProperty("accountId", out var aid) ? Guid.Parse(aid.GetString()!) : Guid.Empty,
                        DebitAmount: line.TryGetProperty("debitAmount", out var da) ? da.GetDecimal() : 0,
                        CreditAmount: line.TryGetProperty("creditAmount", out var ca) ? ca.GetDecimal() : 0,
                        Description: line.TryGetProperty("description", out var desc) ? desc.GetString() : null
                    ));
                }
            }

            var request = new Models.DTOs.Accounting.CreateJournalEntryRequest(
                EntryDate: DateTime.UtcNow,
                Description: root.TryGetProperty("description", out var d) ? d.GetString() ?? recurring.Name : recurring.Name,
                Reference: $"AUTO-{recurring.Name}",
                Lines: lines
            );

            var result = await _accountingService.CreateJournalEntryAsync(recurring.CompanyId, request, performedBy);

            // Auto-post if configured
            if (recurring.AutoApprove && result != null)
            {
                try
                {
                    await _accountingService.PostJournalEntryAsync(recurring.CompanyId, result.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-post failed for recurring journal {JournalId}", result.Id);
                    await _errorLogService.LogErrorAsync(ex, $"RecurringTransaction.AutoPost/{result.Id}");
                }
            }

            _logger.LogInformation("Created journal entry {EntryNumber} from recurring {RecurringId}", result?.EntryNumber, recurring.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create journal entry from recurring template {RecurringId}", recurring.Id);
            throw;
        }
    }

    private static RecurringTransactionResponse MapToResponse(RecurringTransaction r) =>
        new(r.Id, r.Name, r.Description, r.Frequency, r.Status,
            r.StartDate, r.EndDate, r.NextRunDate, r.LastRunDate,
            r.TotalRuns, r.MaxRuns, r.TemplateType, r.DocumentType,
            r.ContactId, r.TemplateData, r.NotifyBeforeRun,
            r.NotifyDaysBefore, r.AutoApprove, r.CreatedAt);
}
