using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Compliance;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ComplianceService : IComplianceService
{
    private readonly AccountingDbContext _db;

    public ComplianceService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<ComplianceFilingResponse> CreateFilingAsync(Guid companyId, CreateComplianceFilingRequest request)
    {
        var filing = new ComplianceFiling
        {
            CompanyId = companyId,
            FilingType = request.FilingType,
            FormCode = request.FormCode,
            Year = request.Year,
            Month = request.Month,
            DueDate = request.DueDate,
            Notes = request.Notes,
            Status = "NotStarted"
        };

        _db.Set<ComplianceFiling>().Add(filing);
        await _db.SaveChangesAsync();

        return MapToResponse(filing);
    }

    public async Task<ComplianceFilingResponse> GetFilingAsync(Guid companyId, Guid filingId)
    {
        var filing = await _db.Set<ComplianceFiling>()
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Id == filingId && !f.IsDeleted)
            ?? throw new InvalidOperationException("Compliance filing not found.");

        return MapToResponse(filing);
    }

    public async Task<PagedResponse<ComplianceFilingResponse>> GetFilingsAsync(Guid companyId, int? year, string? filingType, PagedRequest request)
    {
        var query = _db.Set<ComplianceFiling>()
            .Where(f => f.CompanyId == companyId && !f.IsDeleted);

        if (year.HasValue)
            query = query.Where(f => f.Year == year.Value);

        if (!string.IsNullOrWhiteSpace(filingType))
            query = query.Where(f => f.FilingType == filingType);

        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(f => f.FormCode.Contains(request.Search)
                                  || f.FilingType.Contains(request.Search)
                                  || (f.Notes != null && f.Notes.Contains(request.Search)));

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderBy(f => f.DueDate)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(f => MapToResponse(f))
            .ToListAsync();

        return new PagedResponse<ComplianceFilingResponse>(
            items, totalCount, request.Page, request.PageSize,
            (int)Math.Ceiling(totalCount / (double)request.PageSize));
    }

    public async Task<ComplianceFilingResponse> UpdateFilingAsync(Guid companyId, Guid filingId, UpdateComplianceFilingRequest request)
    {
        var filing = await _db.Set<ComplianceFiling>()
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Id == filingId && !f.IsDeleted)
            ?? throw new InvalidOperationException("Compliance filing not found.");

        if (request.DueDate.HasValue) filing.DueDate = request.DueDate.Value;
        if (request.Notes != null) filing.Notes = request.Notes;
        if (request.Status != null) filing.Status = request.Status;

        filing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapToResponse(filing);
    }

    public async Task DeleteFilingAsync(Guid companyId, Guid filingId)
    {
        var filing = await _db.Set<ComplianceFiling>()
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Id == filingId && !f.IsDeleted)
            ?? throw new InvalidOperationException("Compliance filing not found.");

        if (filing.Status == "Filed" || filing.Status == "Accepted")
            throw new InvalidOperationException("Cannot delete a filed or accepted compliance filing.");

        filing.IsDeleted = true;
        filing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<ComplianceFilingResponse> ValidateFilingAsync(Guid companyId, Guid filingId)
    {
        var filing = await _db.Set<ComplianceFiling>()
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Id == filingId && !f.IsDeleted)
            ?? throw new InvalidOperationException("Compliance filing not found.");

        var errors = new List<string>();

        // Validate based on filing type
        if (filing.FilingType == "RD_VAT")
        {
            // Check that VAT tax reports exist for the period
            var vatReport = await _db.TaxReports
                .FirstOrDefaultAsync(t => t.CompanyId == companyId
                                       && t.TaxType == TaxType.VAT
                                       && t.Year == filing.Year
                                       && t.Month == (filing.Month ?? 0));

            if (vatReport == null)
                errors.Add($"VAT report for {filing.Year}/{filing.Month} has not been generated.");

            // Check for unposted journal entries in the period
            if (filing.Month.HasValue)
            {
                var unpostedCount = await _db.JournalEntries
                    .CountAsync(j => j.CompanyId == companyId
                                  && j.Status == JournalEntryStatus.Draft
                                  && j.EntryDate.Year == filing.Year
                                  && j.EntryDate.Month == filing.Month.Value);

                if (unpostedCount > 0)
                    errors.Add($"There are {unpostedCount} unposted journal entries for the filing period.");
            }
        }
        else if (filing.FilingType == "RD_WHT")
        {
            // Check that WHT certificates exist for the period
            if (filing.Month.HasValue)
            {
                var whtCertCount = await _db.WithholdingTaxCerts
                    .CountAsync(w => w.CompanyId == companyId
                                  && w.CreatedAt.Year == filing.Year
                                  && w.CreatedAt.Month == filing.Month.Value);

                if (whtCertCount == 0)
                    errors.Add("No withholding tax certificates found for the filing period.");
            }
        }
        else if (filing.FilingType == "RD_CIT")
        {
            // Corporate income tax: check fiscal periods are closed
            var openPeriods = await _db.Set<FiscalPeriod>()
                .CountAsync(p => p.CompanyId == companyId
                              && p.Year == filing.Year
                              && p.Status == FiscalPeriodStatus.Open);

            if (openPeriods > 0)
                errors.Add($"There are {openPeriods} open fiscal periods for the year. Close them before filing CIT.");
        }
        else if (filing.FilingType == "SSO_Contribution")
        {
            // Social security: check payroll has been processed
            // Simplified validation
            if (filing.Month.HasValue)
            {
                var hasPayroll = await _db.Set<PayrollRun>()
                    .AnyAsync(p => p.CompanyId == companyId
                                && p.Year == filing.Year
                                && p.Month == filing.Month.Value);

                if (!hasPayroll)
                    errors.Add("Payroll has not been processed for the filing period.");
            }
        }

        // Prepare tax amount from relevant data
        if (errors.Count == 0 && filing.FilingType == "RD_VAT" && filing.Month.HasValue)
        {
            var vatReport = await _db.TaxReports
                .FirstOrDefaultAsync(t => t.CompanyId == companyId
                                       && t.TaxType == TaxType.VAT
                                       && t.Year == filing.Year
                                       && t.Month == filing.Month.Value);

            if (vatReport != null)
            {
                filing.TaxAmount = vatReport.NetVat;
                filing.FileDataJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    vatReport.OutputVat,
                    vatReport.InputVat,
                    vatReport.NetVat,
                    vatReport.TotalIncome
                });
            }
        }

        if (errors.Count > 0)
        {
            filing.ValidationErrors = string.Join("; ", errors);
            filing.Status = "InProgress";
        }
        else
        {
            filing.ValidationErrors = null;
            filing.Status = "Validated";
        }

        await _db.SaveChangesAsync();

        return MapToResponse(filing);
    }

    public async Task<ComplianceFilingResponse> SubmitFilingAsync(Guid companyId, Guid filingId, string filedBy)
    {
        var filing = await _db.Set<ComplianceFiling>()
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && f.Id == filingId && !f.IsDeleted)
            ?? throw new InvalidOperationException("Compliance filing not found.");

        if (filing.Status != "Validated")
            throw new InvalidOperationException("Filing must be validated before submission. Current status: " + filing.Status);

        // Simulate submission to government e-filing system
        filing.Status = "Filed";
        filing.FiledDate = DateTime.UtcNow;
        filing.FiledBy = filedBy;
        filing.SubmissionReference = $"SUB-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString()[..6].ToUpper()}";
        filing.ConfirmationNumber = $"CONF-{filing.FormCode}-{filing.Year}-{filing.Month:D2}-{Guid.NewGuid().ToString()[..8].ToUpper()}";

        // Check for late filing penalty
        if (filing.FiledDate > filing.DueDate)
        {
            var daysLate = (filing.FiledDate.Value - filing.DueDate).Days;
            // Thai Revenue Department: surcharge of 1.5% per month for late filing
            if (filing.TaxAmount.HasValue && filing.TaxAmount.Value > 0)
            {
                var monthsLate = Math.Ceiling(daysLate / 30.0m);
                filing.PenaltyAmount = filing.TaxAmount.Value * 0.015m * monthsLate;
            }
        }

        await _db.SaveChangesAsync();

        return MapToResponse(filing);
    }

    public async Task<List<ComplianceFilingResponse>> GetPendingFilingsAsync(Guid companyId)
    {
        return await _db.Set<ComplianceFiling>()
            .Where(f => f.CompanyId == companyId
                      && !f.IsDeleted
                      && f.Status != "Filed"
                      && f.Status != "Accepted")
            .OrderBy(f => f.DueDate)
            .Select(f => MapToResponse(f))
            .ToListAsync();
    }

    public async Task InitializeFilingCalendarAsync(Guid companyId, int year)
    {
        // Check if filings already exist for this year
        var existingCount = await _db.Set<ComplianceFiling>()
            .CountAsync(f => f.CompanyId == companyId && f.Year == year && !f.IsDeleted);

        if (existingCount > 0)
            throw new InvalidOperationException($"Filing calendar for {year} already exists with {existingCount} entries.");

        var filings = new List<ComplianceFiling>();

        // Monthly VAT filing (ภ.พ.30) - due 15th of following month, e-filing gets +8 days
        for (int month = 1; month <= 12; month++)
        {
            var dueDate = new DateTime(year, month, 1).AddMonths(1).AddDays(14); // 15th of next month
            if (month == 12) dueDate = new DateTime(year + 1, 1, 15);

            filings.Add(new ComplianceFiling
            {
                CompanyId = companyId,
                FilingType = "RD_VAT",
                FormCode = "PP30",
                Year = year,
                Month = month,
                DueDate = dueDate,
                Status = "NotStarted"
            });
        }

        // Monthly WHT filing (ภ.ง.ด.3 / ภ.ง.ด.53) - due 7th of following month, e-filing +8 days
        for (int month = 1; month <= 12; month++)
        {
            var dueDate = new DateTime(year, month, 1).AddMonths(1).AddDays(6); // 7th of next month
            if (month == 12) dueDate = new DateTime(year + 1, 1, 7);

            filings.Add(new ComplianceFiling
            {
                CompanyId = companyId,
                FilingType = "RD_WHT",
                FormCode = "PND3",
                Year = year,
                Month = month,
                DueDate = dueDate,
                Status = "NotStarted"
            });

            filings.Add(new ComplianceFiling
            {
                CompanyId = companyId,
                FilingType = "RD_WHT",
                FormCode = "PND53",
                Year = year,
                Month = month,
                DueDate = dueDate,
                Status = "NotStarted"
            });
        }

        // Monthly WHT for salary (ภ.ง.ด.1) - due 7th of following month
        for (int month = 1; month <= 12; month++)
        {
            var dueDate = new DateTime(year, month, 1).AddMonths(1).AddDays(6);
            if (month == 12) dueDate = new DateTime(year + 1, 1, 7);

            filings.Add(new ComplianceFiling
            {
                CompanyId = companyId,
                FilingType = "RD_WHT",
                FormCode = "PND1",
                Year = year,
                Month = month,
                DueDate = dueDate,
                Status = "NotStarted"
            });
        }

        // Monthly Social Security (สปส.1-10) - due 15th of following month
        for (int month = 1; month <= 12; month++)
        {
            var dueDate = new DateTime(year, month, 1).AddMonths(1).AddDays(14);
            if (month == 12) dueDate = new DateTime(year + 1, 1, 15);

            filings.Add(new ComplianceFiling
            {
                CompanyId = companyId,
                FilingType = "SSO_Contribution",
                FormCode = "SSO1-10",
                Year = year,
                Month = month,
                DueDate = dueDate,
                Status = "NotStarted"
            });
        }

        // Half-year CIT (ภ.ง.ด.51) - due within 2 months of half-year end
        filings.Add(new ComplianceFiling
        {
            CompanyId = companyId,
            FilingType = "RD_CIT",
            FormCode = "PND51",
            Year = year,
            Month = 6,
            DueDate = new DateTime(year, 8, 31),
            Status = "NotStarted"
        });

        // Annual CIT (ภ.ง.ด.50) - due within 150 days of fiscal year end
        filings.Add(new ComplianceFiling
        {
            CompanyId = companyId,
            FilingType = "RD_CIT",
            FormCode = "PND50",
            Year = year,
            Month = null,
            DueDate = new DateTime(year + 1, 5, 31),
            Status = "NotStarted"
        });

        // Annual DBD Financial Statement - due within 5 months of fiscal year end
        filings.Add(new ComplianceFiling
        {
            CompanyId = companyId,
            FilingType = "DBD_FinancialStatement",
            FormCode = "SBC3",
            Year = year,
            Month = null,
            DueDate = new DateTime(year + 1, 5, 31),
            Status = "NotStarted"
        });

        _db.Set<ComplianceFiling>().AddRange(filings);
        await _db.SaveChangesAsync();
    }

    private static ComplianceFilingResponse MapToResponse(ComplianceFiling f)
    {
        var daysUntilDue = (f.DueDate - DateTime.UtcNow.Date).Days;

        return new ComplianceFilingResponse(
            f.Id, f.FilingType, f.FormCode, f.Year, f.Month,
            f.DueDate, f.FiledDate, f.Status,
            f.SubmissionReference, f.ConfirmationNumber,
            f.TaxAmount, f.PenaltyAmount,
            f.ValidationErrors, daysUntilDue);
    }
}
