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

    // Thai WHT rate table by income type
    private static readonly Dictionary<string, decimal> WhtRateTable = new()
    {
        { "40(1)", 3m },   // เงินเดือน ค่าจ้าง (Salary, Wages)
        { "40(2)", 3m },   // ค่านายหน้า (Commission)
        { "40(3)", 5m },   // ค่าลิขสิทธิ์ (Royalties)
        { "40(4)a", 15m }, // ดอกเบี้ย (Interest)
        { "40(4)b", 10m }, // เงินปันผล (Dividends)
        { "40(5)", 5m },   // ค่าเช่า (Rent - property)
        { "40(6)", 3m },   // วิชาชีพอิสระ (Professional fees)
        { "40(7)", 3m },   // ค่ารับเหมา (Contractors)
        { "40(8)", 3m },   // ค่าจ้างทำของ (Service fees)
        { "3", 3m },       // ค่าบริการทั่วไป (General services)
        { "5", 1m },       // ค่าขนส่ง (Transportation)
        { "6", 2m },       // ค่าประกันภัย (Insurance premiums)
        { "advertising", 2m }, // ค่าโฆษณา
        { "default", 3m }      // Default rate
    };

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
            await GenerateVatReport(companyId, startDate, endDate, report);
        }
        else if (request.TaxType == TaxType.WithholdingTax3
              || request.TaxType == TaxType.WithholdingTax53
              || request.TaxType == TaxType.WithholdingTax1)
        {
            await GenerateWhtReport(companyId, startDate, endDate, report);
        }
        else if (request.TaxType == TaxType.CorporateIncomeTax)
        {
            await GenerateCitReport(companyId, request.Year, report);
        }

        _db.TaxReports.Add(report);
        await _db.SaveChangesAsync();

        return await GetTaxReportAsync(companyId, report.Id);
    }

    private async Task GenerateVatReport(Guid companyId, DateTime startDate, DateTime endDate, TaxReport report)
    {
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
            // Output VAT - from sales documents
            if (doc.DocumentType == DocumentType.Invoice || doc.DocumentType == DocumentType.TaxInvoice)
            {
                outputVat += doc.VatAmount;
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = doc.Contact?.TaxId,
                    TaxPayerName = doc.Contact?.Name ?? "",
                    TransactionDate = doc.DocumentDate,
                    Description = doc.DocumentNumber,
                    IncomeAmount = doc.SubTotal,
                    TaxRate = doc.Lines.Any() ? doc.Lines.Max(l => l.VatRate) : 7,
                    TaxAmount = doc.VatAmount,
                    DocumentId = doc.Id
                });
            }
            // Input VAT - from purchase documents
            else if (doc.DocumentType == DocumentType.PurchaseInvoice
                  || doc.DocumentType == DocumentType.Expense
                  || doc.DocumentType == DocumentType.PurchaseOrder)
            {
                inputVat += doc.VatAmount;
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = doc.Contact?.TaxId,
                    TaxPayerName = doc.Contact?.Name ?? "",
                    TransactionDate = doc.DocumentDate,
                    Description = $"[ภาษีซื้อ] {doc.DocumentNumber}",
                    IncomeAmount = doc.SubTotal,
                    TaxRate = doc.Lines.Any() ? doc.Lines.Max(l => l.VatRate) : 7,
                    TaxAmount = doc.VatAmount,
                    DocumentId = doc.Id,
                    IncomeTypeCode = "INPUT"
                });
            }
        }

        report.OutputVat = outputVat;
        report.InputVat = inputVat;
        report.NetVat = outputVat - inputVat;
    }

    private async Task GenerateWhtReport(Guid companyId, DateTime startDate, DateTime endDate, TaxReport report)
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
                // Determine WHT rate from income type or use line rate
                var whtRate = line.WithholdingTaxRate > 0
                    ? line.WithholdingTaxRate
                    : GetWhtRate(line.IncomeTypeCode);

                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = doc.Contact?.TaxId,
                    TaxPayerName = doc.Contact?.Name ?? "",
                    TransactionDate = doc.DocumentDate,
                    Description = line.Description,
                    IncomeAmount = line.Amount,
                    TaxRate = whtRate,
                    TaxAmount = line.WithholdingTaxAmount,
                    DocumentId = doc.Id,
                    IncomeTypeCode = line.IncomeTypeCode ?? "40(8)"
                });
            }
        }

        report.TotalIncome = report.Lines.Sum(l => l.IncomeAmount);
        report.TotalTaxWithheld = report.Lines.Sum(l => l.TaxAmount);
    }

    private async Task GenerateCitReport(Guid companyId, int year, TaxReport report)
    {
        var startDate = new DateTime(year, 1, 1);
        var endDate = new DateTime(year, 12, 31);

        // Calculate total revenue
        var revenueLines = await _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= startDate
                && l.JournalEntry.EntryDate <= endDate
                && l.Account!.AccountType == AccountType.Revenue)
            .ToListAsync();

        var totalRevenue = revenueLines.Sum(l => l.CreditAmount - l.DebitAmount);

        // Calculate total expenses
        var expenseLines = await _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= startDate
                && l.JournalEntry.EntryDate <= endDate
                && l.Account!.AccountType == AccountType.Expense)
            .ToListAsync();

        var totalExpenses = expenseLines.Sum(l => l.DebitAmount - l.CreditAmount);

        // Net profit before tax
        var netProfitBeforeTax = totalRevenue - totalExpenses;

        // Thai CIT progressive rates (for SME companies)
        var citAmount = CalculateThaiCit(netProfitBeforeTax);

        report.TotalIncome = totalRevenue;
        report.TotalTaxWithheld = citAmount;
        report.NetVat = netProfitBeforeTax; // Reuse field for net profit

        report.Lines.Add(new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = 1,
            Description = "รายได้ทั้งปี",
            IncomeAmount = totalRevenue,
            TaxAmount = 0
        });
        report.Lines.Add(new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = 2,
            Description = "ค่าใช้จ่ายทั้งปี",
            IncomeAmount = totalExpenses,
            TaxAmount = 0
        });
        report.Lines.Add(new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = 3,
            Description = "กำไรสุทธิก่อนภาษี",
            IncomeAmount = netProfitBeforeTax,
            TaxAmount = citAmount,
            TaxRate = netProfitBeforeTax > 0 ? (citAmount / netProfitBeforeTax) * 100 : 0
        });
    }

    /// <summary>
    /// Thai CIT progressive rates for SME (registered capital <= 5M, revenue <= 30M):
    /// Net profit 0 - 300,000: exempt
    /// Net profit 300,001 - 3,000,000: 15%
    /// Net profit > 3,000,000: 20%
    /// Non-SME: flat 20%
    /// </summary>
    private static decimal CalculateThaiCit(decimal netProfit)
    {
        if (netProfit <= 0) return 0;

        decimal tax = 0;

        // SME rates
        if (netProfit <= 300_000m)
        {
            tax = 0; // Exempt
        }
        else if (netProfit <= 3_000_000m)
        {
            tax = (netProfit - 300_000m) * 0.15m;
        }
        else
        {
            tax = (3_000_000m - 300_000m) * 0.15m  // 15% tier
                + (netProfit - 3_000_000m) * 0.20m; // 20% tier
        }

        return Math.Round(tax, 2);
    }

    private static decimal GetWhtRate(string? incomeTypeCode)
    {
        if (string.IsNullOrEmpty(incomeTypeCode))
            return WhtRateTable["default"];

        return WhtRateTable.TryGetValue(incomeTypeCode, out var rate)
            ? rate
            : WhtRateTable["default"];
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
