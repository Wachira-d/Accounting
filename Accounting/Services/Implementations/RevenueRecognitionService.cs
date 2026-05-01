using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.RevenueRecognition;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class RevenueRecognitionService : IRevenueRecognitionService
{
    private readonly AccountingDbContext _db;

    public RevenueRecognitionService(AccountingDbContext db)
    {
        _db = db;
    }

    // ===== Contracts =====

    public async Task<RevenueContractResponse> CreateContractAsync(Guid companyId, CreateRevenueContractRequest request)
    {
        var existing = await _db.RevenueContracts
            .AnyAsync(c => c.CompanyId == companyId && c.ContractNumber == request.ContractNumber);
        if (existing)
            throw new InvalidOperationException($"เลขที่สัญญา {request.ContractNumber} ซ้ำ");

        var contact = await _db.Contacts
            .FirstOrDefaultAsync(c => c.Id == request.ContactId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบข้อมูลผู้ติดต่อ");

        var contract = new RevenueContract
        {
            CompanyId = companyId,
            ContractNumber = request.ContractNumber,
            Name = request.Name,
            ContactId = request.ContactId,
            ProjectId = request.ProjectId,
            ContractDate = request.ContractDate,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            TotalContractValue = request.TotalContractValue,
            Status = "Active"
        };

        _db.RevenueContracts.Add(contract);
        await _db.SaveChangesAsync();

        return MapContractToResponse(contract, contact.Name, new List<PerformanceObligationResponse>());
    }

    public async Task<RevenueContractResponse> GetContractAsync(Guid companyId, Guid contractId)
    {
        var contract = await _db.RevenueContracts
            .Include(c => c.Contact)
            .Include(c => c.Obligations)
            .Include(c => c.Project)
            .FirstOrDefaultAsync(c => c.Id == contractId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสัญญา");

        var obligations = contract.Obligations.Select(MapObligationToResponse).ToList();
        return MapContractToResponse(contract, contract.Contact.Name, obligations);
    }

    public async Task<PagedResponse<RevenueContractResponse>> GetContractsAsync(Guid companyId, string? status, PagedRequest request)
    {
        var query = _db.RevenueContracts
            .Include(c => c.Contact)
            .Include(c => c.Obligations)
            .Include(c => c.Project)
            .Where(c => c.CompanyId == companyId);

        if (!string.IsNullOrEmpty(status))
            query = query.Where(c => c.Status == status);

        if (!string.IsNullOrEmpty(request.Search))
            query = query.Where(c => c.ContractNumber.Contains(request.Search) || c.Name.Contains(request.Search));

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(c => c.ContractDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(totalCount / (double)request.PageSize);

        return new PagedResponse<RevenueContractResponse>(
            items.Select(c => MapContractToResponse(c, c.Contact.Name,
                c.Obligations.Select(MapObligationToResponse).ToList())).ToList(),
            totalCount,
            request.Page,
            request.PageSize,
            totalPages);
    }

    public async Task<RevenueContractResponse> UpdateContractAsync(Guid companyId, Guid contractId, UpdateRevenueContractRequest request)
    {
        var contract = await _db.RevenueContracts
            .Include(c => c.Contact)
            .Include(c => c.Obligations)
            .Include(c => c.Project)
            .FirstOrDefaultAsync(c => c.Id == contractId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสัญญา");

        if (request.Name != null) contract.Name = request.Name;
        if (request.EndDate.HasValue) contract.EndDate = request.EndDate.Value;
        if (request.TotalContractValue.HasValue) contract.TotalContractValue = request.TotalContractValue.Value;
        if (request.Status != null) contract.Status = request.Status;
        if (request.ProjectId.HasValue) contract.ProjectId = request.ProjectId;

        await _db.SaveChangesAsync();

        var obligations = contract.Obligations.Select(MapObligationToResponse).ToList();
        return MapContractToResponse(contract, contract.Contact.Name, obligations);
    }

    // ===== Performance Obligations =====

    public async Task<PerformanceObligationResponse> AddObligationAsync(Guid companyId, Guid contractId, CreateObligationRequest request)
    {
        var contract = await _db.RevenueContracts
            .Include(c => c.Obligations)
            .FirstOrDefaultAsync(c => c.Id == contractId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสัญญา");

        var obligation = new PerformanceObligation
        {
            CompanyId = companyId,
            RevenueContractId = contractId,
            Name = request.Name,
            Description = request.Description,
            StandaloneSellingPrice = request.StandaloneSellingPrice,
            RecognitionMethod = request.RecognitionMethod,
            MeasureOfProgress = request.MeasureOfProgress
        };

        // Allocate transaction price based on relative standalone selling prices
        var totalSSP = contract.Obligations.Sum(o => o.StandaloneSellingPrice) + request.StandaloneSellingPrice;
        obligation.AllocatedPrice = totalSSP > 0
            ? contract.TotalContractValue * (request.StandaloneSellingPrice / totalSSP)
            : 0;
        obligation.DeferredRevenue = obligation.AllocatedPrice;

        _db.PerformanceObligations.Add(obligation);

        // Re-allocate existing obligations
        foreach (var existing in contract.Obligations)
        {
            existing.AllocatedPrice = totalSSP > 0
                ? contract.TotalContractValue * (existing.StandaloneSellingPrice / totalSSP)
                : 0;
            existing.DeferredRevenue = existing.AllocatedPrice - existing.RecognizedRevenue;
        }

        await _db.SaveChangesAsync();
        return MapObligationToResponse(obligation);
    }

    public async Task<PerformanceObligationResponse> UpdateProgressAsync(Guid companyId, Guid obligationId, decimal completionPercent)
    {
        var obligation = await _db.PerformanceObligations
            .FirstOrDefaultAsync(o => o.Id == obligationId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบภาระที่ต้องปฏิบัติ");

        if (obligation.IsSatisfied)
            throw new InvalidOperationException("ภาระนี้เสร็จสิ้นแล้ว ไม่สามารถอัปเดตได้");

        obligation.CompletionPercent = completionPercent;

        // Recognize revenue based on completion for over-time obligations
        if (obligation.RecognitionMethod == "OverTime")
        {
            var newRecognized = obligation.AllocatedPrice * (completionPercent / 100m);
            obligation.RecognizedRevenue = newRecognized;
            obligation.DeferredRevenue = obligation.AllocatedPrice - newRecognized;
        }

        if (completionPercent >= 100)
        {
            obligation.IsSatisfied = true;
            obligation.SatisfiedDate = DateTime.UtcNow;
            obligation.RecognizedRevenue = obligation.AllocatedPrice;
            obligation.DeferredRevenue = 0;
        }

        await _db.SaveChangesAsync();
        return MapObligationToResponse(obligation);
    }

    public async Task<PerformanceObligationResponse> SatisfyObligationAsync(Guid companyId, Guid obligationId)
    {
        var obligation = await _db.PerformanceObligations
            .FirstOrDefaultAsync(o => o.Id == obligationId && o.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบภาระที่ต้องปฏิบัติ");

        obligation.IsSatisfied = true;
        obligation.SatisfiedDate = DateTime.UtcNow;
        obligation.CompletionPercent = 100;
        obligation.RecognizedRevenue = obligation.AllocatedPrice;
        obligation.DeferredRevenue = 0;

        await _db.SaveChangesAsync();
        return MapObligationToResponse(obligation);
    }

    // ===== Revenue Schedules =====

    public async Task<List<RevenueScheduleResponse>> GenerateScheduleAsync(Guid companyId, Guid contractId)
    {
        var contract = await _db.RevenueContracts
            .Include(c => c.Obligations)
            .FirstOrDefaultAsync(c => c.Id == contractId && c.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบสัญญา");

        // Remove existing unrecognized schedules
        var existingSchedules = await _db.RevenueSchedules
            .Where(s => s.RevenueContractId == contractId && !s.IsRecognized)
            .ToListAsync();
        _db.RevenueSchedules.RemoveRange(existingSchedules);

        var schedules = new List<RevenueSchedule>();
        var totalMonths = (int)Math.Ceiling((contract.EndDate - contract.StartDate).TotalDays / 30.0);
        if (totalMonths <= 0) totalMonths = 1;

        foreach (var obligation in contract.Obligations.Where(o => !o.IsSatisfied))
        {
            if (obligation.RecognitionMethod == "OverTime")
            {
                // Generate monthly schedule
                var remainingRevenue = obligation.AllocatedPrice - obligation.RecognizedRevenue;
                var monthlyAmount = remainingRevenue / totalMonths;

                for (int i = 0; i < totalMonths; i++)
                {
                    var scheduleDate = contract.StartDate.AddMonths(i);
                    var isLast = i == totalMonths - 1;
                    var amount = isLast ? remainingRevenue - (monthlyAmount * (totalMonths - 1)) : monthlyAmount;

                    var schedule = new RevenueSchedule
                    {
                        CompanyId = companyId,
                        RevenueContractId = contractId,
                        PerformanceObligationId = obligation.Id,
                        ScheduleDate = scheduleDate,
                        Amount = amount
                    };
                    schedules.Add(schedule);
                }
            }
            else // PointInTime
            {
                var schedule = new RevenueSchedule
                {
                    CompanyId = companyId,
                    RevenueContractId = contractId,
                    PerformanceObligationId = obligation.Id,
                    ScheduleDate = contract.EndDate,
                    Amount = obligation.AllocatedPrice - obligation.RecognizedRevenue
                };
                schedules.Add(schedule);
            }
        }

        _db.RevenueSchedules.AddRange(schedules);
        await _db.SaveChangesAsync();

        return schedules.Select(s => new RevenueScheduleResponse(
            s.Id, s.RevenueContractId, s.ScheduleDate, s.Amount, s.IsRecognized, s.JournalEntryId)).ToList();
    }

    public async Task<RevenueScheduleResponse> RecognizeRevenueAsync(Guid companyId, Guid scheduleId)
    {
        var schedule = await _db.RevenueSchedules
            .FirstOrDefaultAsync(s => s.Id == scheduleId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบตารางรับรู้รายได้");

        if (schedule.IsRecognized)
            throw new InvalidOperationException("รับรู้รายได้แล้ว");

        // Create journal entry: Dr Deferred Revenue (218), Cr Revenue (411)
        var revYm = DateTime.UtcNow.ToString("yyyyMM");
        var revPrefix = $"REV-{revYm}-";
        var maxRevNum = await _db.JournalEntries
            .IgnoreQueryFilters()
            .Where(j => j.CompanyId == companyId && j.EntryNumber.StartsWith(revPrefix))
            .Select(j => j.EntryNumber)
            .MaxAsync() as string;
        var revSeq = 1;
        if (maxRevNum != null)
        {
            var lastPart = maxRevNum.Substring(revPrefix.Length);
            if (int.TryParse(lastPart, out var parsed)) revSeq = parsed + 1;
        }

        var deferredRevenueAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "21810" && a.Level >= 4)
            ?? await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("218") && a.Level >= 4);
        var revenueAccount = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == "41100" && a.Level >= 4)
            ?? await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode.StartsWith("411") && a.Level >= 4);

        var entry = new JournalEntry
        {
            CompanyId = companyId,
            EntryNumber = $"{revPrefix}{revSeq:D4}",
            EntryDate = schedule.ScheduleDate,
            Description = $"รับรู้รายได้ตามสัญญา - {schedule.ScheduleDate:yyyy-MM-dd}",
            TotalDebit = schedule.Amount,
            TotalCredit = schedule.Amount,
            Status = JournalEntryStatus.Posted,
            IsAutoGenerated = true
        };

        if (deferredRevenueAccount != null)
            entry.Lines.Add(new JournalEntryLine
            {
                AccountId = deferredRevenueAccount.Id,
                DebitAmount = schedule.Amount,
                CreditAmount = 0,
                Description = "รายได้รอตัดบัญชี"
            });
        if (revenueAccount != null)
            entry.Lines.Add(new JournalEntryLine
            {
                AccountId = revenueAccount.Id,
                DebitAmount = 0,
                CreditAmount = schedule.Amount,
                Description = "รับรู้รายได้"
            });

        _db.JournalEntries.Add(entry);

        schedule.IsRecognized = true;
        schedule.JournalEntryId = entry.Id;

        // Update obligation recognized revenue
        if (schedule.PerformanceObligationId.HasValue)
        {
            var obligation = await _db.PerformanceObligations
                .FirstOrDefaultAsync(o => o.Id == schedule.PerformanceObligationId.Value);
            if (obligation != null)
            {
                obligation.RecognizedRevenue += schedule.Amount;
                obligation.DeferredRevenue -= schedule.Amount;
                if (obligation.DeferredRevenue <= 0)
                {
                    obligation.IsSatisfied = true;
                    obligation.SatisfiedDate = DateTime.UtcNow;
                    obligation.CompletionPercent = 100;
                }
            }
        }

        await _db.SaveChangesAsync();

        return new RevenueScheduleResponse(
            schedule.Id, schedule.RevenueContractId, schedule.ScheduleDate,
            schedule.Amount, schedule.IsRecognized, schedule.JournalEntryId);
    }

    public async Task<List<RevenueScheduleResponse>> ProcessDueRecognitionsAsync(Guid companyId, DateTime asOfDate)
    {
        var dueSchedules = await _db.RevenueSchedules
            .Where(s => s.CompanyId == companyId && !s.IsRecognized && s.ScheduleDate <= asOfDate)
            .OrderBy(s => s.ScheduleDate)
            .ToListAsync();

        var results = new List<RevenueScheduleResponse>();
        foreach (var schedule in dueSchedules)
        {
            var result = await RecognizeRevenueAsync(companyId, schedule.Id);
            results.Add(result);
        }

        return results;
    }

    // ===== Reports =====

    public async Task<DeferredRevenueReportResponse> GetDeferredRevenueReportAsync(Guid companyId, DateTime asOfDate)
    {
        var contracts = await _db.RevenueContracts
            .Include(c => c.Contact)
            .Include(c => c.Obligations)
            .Where(c => c.CompanyId == companyId && c.Status == "Active")
            .ToListAsync();

        var byContract = new List<DeferredRevenueByContract>();
        decimal totalDeferred = 0;
        decimal totalRecognized = 0;

        foreach (var contract in contracts)
        {
            var recognized = contract.Obligations.Sum(o => o.RecognizedRevenue);
            var deferred = contract.Obligations.Sum(o => o.DeferredRevenue);

            totalDeferred += deferred;
            totalRecognized += recognized;

            byContract.Add(new DeferredRevenueByContract(
                contract.Id, contract.Name, contract.Contact.Name,
                contract.TotalContractValue, recognized, deferred, contract.EndDate));
        }

        return new DeferredRevenueReportResponse(asOfDate, totalDeferred, totalRecognized, byContract);
    }

    // ===== Mappers =====

    private static RevenueContractResponse MapContractToResponse(RevenueContract c, string contactName, List<PerformanceObligationResponse> obligations)
    {
        var recognized = c.Obligations?.Sum(o => o.RecognizedRevenue) ?? obligations.Sum(o => o.RecognizedRevenue);
        var deferred = c.Obligations?.Sum(o => o.DeferredRevenue) ?? obligations.Sum(o => o.DeferredRevenue);

        return new RevenueContractResponse(
            c.Id, c.ContractNumber, c.Name, contactName,
            c.ContractDate, c.StartDate, c.EndDate, c.TotalContractValue,
            c.Status, recognized, deferred, obligations,
            c.ProjectId, c.Project?.Name);
    }

    private static PerformanceObligationResponse MapObligationToResponse(PerformanceObligation o) => new(
        o.Id, o.Name, o.Description, o.StandaloneSellingPrice,
        o.AllocatedPrice, o.RecognitionMethod, o.CompletionPercent,
        o.RecognizedRevenue, o.DeferredRevenue, o.IsSatisfied, o.SatisfiedDate);
}
