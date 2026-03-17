using Accounting.Data;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class TaxService : ITaxService
{
    private readonly AccountingDbContext _db;

    public TaxService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<TaxReportResponse> GenerateTaxReportAsync(Guid companyId, CreateTaxReportRequest request)
    {
        if (await _db.TaxReports.AnyAsync(t =>
            t.CompanyId == companyId && t.TaxType == request.TaxType &&
            t.Year == request.Year && t.Month == request.Month))
            throw new InvalidOperationException("รายงานภาษีเดือนนี้มีอยู่แล้ว");

        var startDate = new DateTime(request.Year, request.Month, 1);
        var endDate = startDate.AddMonths(1).AddDays(-1);

        var report = new TaxReport
        {
            CompanyId = companyId,
            TaxType = request.TaxType,
            Year = request.Year,
            Month = request.Month
        };

        if (request.TaxType == TaxType.VAT)
        {
            // Get all documents with VAT in the period
            var docs = await _db.Documents
                .Include(d => d.Lines)
                .Include(d => d.Contact)
                .Where(d => d.CompanyId == companyId
                    && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                    && d.VatAmount != 0)
                .ToListAsync();

            decimal outputVat = 0, inputVat = 0;
            var lineOrder = 1;

            foreach (var doc in docs)
            {
                if (doc.DocumentType == DocumentType.Invoice || doc.DocumentType == DocumentType.TaxInvoice)
                {
                    outputVat += doc.VatAmount;
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TaxPayerId = doc.Contact.TaxId,
                        TaxPayerName = doc.Contact.Name,
                        TransactionDate = doc.DocumentDate,
                        Description = doc.DocumentNumber,
                        IncomeAmount = doc.SubTotal,
                        TaxRate = 7,
                        TaxAmount = doc.VatAmount,
                        DocumentId = doc.Id
                    });
                }
            }

            report.OutputVat = outputVat;
            report.InputVat = inputVat;
            report.NetVat = outputVat - inputVat;
        }
        else // WHT
        {
            var docs = await _db.Documents
                .Include(d => d.Lines)
                .Include(d => d.Contact)
                .Where(d => d.CompanyId == companyId
                    && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided
                    && d.WithholdingTaxAmount != 0)
                .ToListAsync();

            var lineOrder = 1;
            foreach (var doc in docs)
            {
                foreach (var line in doc.Lines.Where(l => l.WithholdingTaxAmount > 0))
                {
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TaxPayerId = doc.Contact.TaxId,
                        TaxPayerName = doc.Contact.Name,
                        TransactionDate = doc.DocumentDate,
                        Description = line.Description,
                        IncomeAmount = line.Amount,
                        TaxRate = line.WithholdingTaxRate,
                        TaxAmount = line.WithholdingTaxAmount,
                        DocumentId = doc.Id
                    });
                }
            }

            report.TotalIncome = report.Lines.Sum(l => l.IncomeAmount);
            report.TotalTaxWithheld = report.Lines.Sum(l => l.TaxAmount);
        }

        _db.TaxReports.Add(report);
        await _db.SaveChangesAsync();

        return await GetTaxReportAsync(companyId, report.Id);
    }

    public async Task<TaxReportResponse> GetTaxReportAsync(Guid companyId, Guid reportId)
    {
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        return MapToResponse(report);
    }

    public async Task<List<TaxReportResponse>> GetTaxReportsAsync(Guid companyId, TaxType? taxType = null, int? year = null)
    {
        var query = _db.TaxReports
            .Include(r => r.Lines)
            .Where(r => r.CompanyId == companyId);

        if (taxType.HasValue) query = query.Where(r => r.TaxType == taxType.Value);
        if (year.HasValue) query = query.Where(r => r.Year == year.Value);

        var reports = await query.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).ToListAsync();
        return reports.Select(MapToResponse).ToList();
    }

    public async Task<TaxReportResponse> FileTaxReportAsync(Guid companyId, Guid reportId)
    {
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        report.Status = TaxReportStatus.Filed;
        report.FiledDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapToResponse(report);
    }

    private static TaxReportResponse MapToResponse(TaxReport r) => new(
        r.Id, r.TaxType, r.Year, r.Month, r.Status, r.FiledDate,
        r.OutputVat, r.InputVat, r.NetVat, r.TotalIncome, r.TotalTaxWithheld,
        r.Lines.OrderBy(l => l.LineOrder).Select(l => new TaxReportLineResponse(
            l.Id, l.LineOrder, l.TaxPayerId, l.TaxPayerName,
            l.TransactionDate, l.Description, l.IncomeAmount,
            l.TaxRate, l.TaxAmount, l.IncomeTypeCode)).ToList());
}
