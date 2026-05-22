using Accounting.Data;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public partial class TaxService : ITaxService
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
        // Input validation
        if (request.Year < 2020 || request.Year > DateTime.UtcNow.Year + 1)
            throw new ArgumentException("ปีภาษีไม่ถูกต้อง (ต้องอยู่ระหว่าง 2020 ถึงปีปัจจุบัน+1)");

        if (request.TaxType != TaxType.CorporateIncomeTax)
        {
            if (request.Month < 1 || request.Month > 12)
                throw new ArgumentException("เดือนภาษีไม่ถูกต้อง (ต้องอยู่ระหว่าง 1 ถึง 12)");
        }

        if (await _db.TaxReports.AnyAsync(t =>
            t.CompanyId == companyId && t.TaxType == request.TaxType &&
            t.Year == request.Year && t.Month == request.Month))
            throw new InvalidOperationException("รายงานภาษีเดือนนี้มีอยู่แล้ว");

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            var startDate = new DateTime(request.Year, request.TaxType == TaxType.CorporateIncomeTax ? 1 : request.Month, 1);
            var endDate = startDate.AddMonths(1).AddDays(-1);

            var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
            var companyVatRate = company?.VatRate ?? 7m;

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
                await ApplyVatDeferralsAsync(companyId, request.Year, request.Month, report);
            }
            else if (request.TaxType == TaxType.WithholdingTax3
                  || request.TaxType == TaxType.WithholdingTax53
                  || request.TaxType == TaxType.WithholdingTax1
                  || request.TaxType == TaxType.WithholdingTax54)
            {
                // PND.54 reuses the WHT report generator — the difference is
                // in the form's per-line IncomeTypeCode and the e-Filing export
                // layout (BuildPndAsync handles 54 separately).
                await GenerateWhtReport(companyId, startDate, endDate, report);
            }
            else if (request.TaxType == TaxType.VatPp36)
            {
                // PP.36 — foreign service VAT. Treat like VAT report but
                // pulled only from documents flagged as foreign-supplier
                // (heuristic: contact has non-Thai TaxId or is marked
                // ForeignSupplier). For now reuse the VAT generator;
                // ApplyVatDeferralsAsync skips this branch.
                await GenerateVatReport(companyId, startDate, endDate, report);
            }
            else if (request.TaxType == TaxType.CorporateIncomeTax)
            {
                await GenerateCitReport(companyId, request.Year, report);
            }
            else if (request.TaxType == TaxType.PersonalIncomeTax91)
            {
                await GeneratePnd91Report(companyId, request.Year, report);
            }

            _db.TaxReports.Add(report);
            await _db.SaveChangesAsync();

            await transaction.CommitAsync();

            return await GetTaxReportAsync(companyId, report.Id);
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private async Task GenerateVatReport(Guid companyId, DateTime startDate, DateTime endDate, TaxReport report)
    {
        var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
        var companyVatRate = company?.VatRate ?? 7m;

        var docs = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                && d.VatAmount != 0)
            .ToListAsync();

        // Accounts whose input VAT is prohibited (ภาษีซื้อต้องห้าม, §82/5) —
        // e.g. ค่ารับรอง. VAT on purchase lines posting here is excluded from
        // the claimable ภ.พ.30 input total.
        var nonClaimableAccountIds = (await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && !a.InputVatClaimable)
            .Select(a => a.Id)
            .ToListAsync())
            .ToHashSet();

        decimal outputVat = 0, inputVat = 0;
        decimal vatExemptAmount = 0;
        var lineOrder = 1;

        foreach (var doc in docs)
        {
            // Check for VAT exempt lines (VatRate == -1)
            var exemptLines = doc.Lines.Where(l => l.VatRate == -1).ToList();
            if (exemptLines.Any())
            {
                vatExemptAmount += exemptLines.Sum(l => l.Amount);
            }

            // Output VAT - from tax invoices (ใบกำกับภาษี) per Thai law ภ.พ.30
            if (doc.DocumentType == DocumentType.TaxInvoice)
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
                    TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                    TaxAmount = doc.VatAmount,
                    DocumentId = doc.Id
                });
            }
            // CreditNote — reduces output/input VAT
            else if (doc.DocumentType == DocumentType.CreditNote)
            {
                var label = doc.Lines.Any(l => l.AccountId.HasValue) ? "[ใบลดหนี้-ภาษีซื้อ]" : "[ใบลดหนี้-ภาษีขาย]";
                var isPurchaseSide = doc.RelatedDocumentId.HasValue &&
                    docs.Any(d => d.Id == doc.RelatedDocumentId.Value &&
                        (d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense || d.DocumentType == DocumentType.CertificateInLieu));
                if (isPurchaseSide)
                {
                    inputVat -= doc.VatAmount;
                    label = "[ใบลดหนี้-ภาษีซื้อ]";
                }
                else
                {
                    outputVat -= doc.VatAmount;
                    label = "[ใบลดหนี้-ภาษีขาย]";
                }
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = doc.Contact?.TaxId,
                    TaxPayerName = doc.Contact?.Name ?? "",
                    TransactionDate = doc.DocumentDate,
                    Description = $"{label} {doc.DocumentNumber}",
                    IncomeAmount = -doc.SubTotal,
                    TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                    TaxAmount = -doc.VatAmount,
                    DocumentId = doc.Id
                });
            }
            // DebitNote — increases output/input VAT
            else if (doc.DocumentType == DocumentType.DebitNote)
            {
                var isPurchaseSide = doc.RelatedDocumentId.HasValue &&
                    docs.Any(d => d.Id == doc.RelatedDocumentId.Value &&
                        (d.DocumentType == DocumentType.PurchaseInvoice || d.DocumentType == DocumentType.Expense || d.DocumentType == DocumentType.CertificateInLieu));
                if (isPurchaseSide)
                {
                    inputVat += doc.VatAmount;
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id, LineOrder = lineOrder++,
                        TaxPayerId = doc.Contact?.TaxId, TaxPayerName = doc.Contact?.Name ?? "",
                        TransactionDate = doc.DocumentDate,
                        Description = $"[ใบเพิ่มหนี้-ภาษีซื้อ] {doc.DocumentNumber}",
                        IncomeAmount = doc.SubTotal, TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                        TaxAmount = doc.VatAmount, DocumentId = doc.Id, IncomeTypeCode = "INPUT"
                    });
                }
                else
                {
                    outputVat += doc.VatAmount;
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id, LineOrder = lineOrder++,
                        TaxPayerId = doc.Contact?.TaxId, TaxPayerName = doc.Contact?.Name ?? "",
                        TransactionDate = doc.DocumentDate,
                        Description = $"[ใบเพิ่มหนี้-ภาษีขาย] {doc.DocumentNumber}",
                        IncomeAmount = doc.SubTotal, TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                        TaxAmount = doc.VatAmount, DocumentId = doc.Id
                    });
                }
            }
            // Input VAT - from purchase documents (PurchaseOrder excluded: no VAT obligation)
            else if (doc.DocumentType == DocumentType.PurchaseInvoice
                  || doc.DocumentType == DocumentType.Expense
                  || doc.DocumentType == DocumentType.CertificateInLieu)
            {
                // ----- Rule B: detect prohibited input VAT (ภาษีซื้อต้องห้าม) -----
                // Sum VAT on lines posting to a non-claimable account
                // (falling back to the document's expense category). This is
                // surfaced as a WARNING only — the amount stays in the claimed
                // total; whether to exclude it is the accountant's call (the
                // ภ.พ.30 line is editable).
                decimal lineVatTotal = doc.Lines.Sum(l => l.VatAmount);
                decimal prohibitedVat = 0m;
                if (Math.Abs(lineVatTotal) < 0.01m && doc.VatAmount != 0)
                {
                    if (doc.ExpenseCategoryId.HasValue && nonClaimableAccountIds.Contains(doc.ExpenseCategoryId.Value))
                        prohibitedVat = doc.VatAmount;
                }
                else
                {
                    foreach (var l in doc.Lines)
                    {
                        var acct = l.AccountId ?? doc.ExpenseCategoryId;
                        if (acct.HasValue && nonClaimableAccountIds.Contains(acct.Value))
                            prohibitedVat += l.VatAmount;
                    }
                }

                // ----- Rule A: tax-invoice 6-month age check -----
                var windowEnd = new DateTime(doc.DocumentDate.Year, doc.DocumentDate.Month, 1)
                    .AddMonths(7).AddDays(-1);
                var pastWindow = DateTime.UtcNow.Date > windowEnd;

                // Both rules are advisory — the full VAT stays in the total;
                // the accountant decides whether to keep or remove it.
                inputVat += doc.VatAmount;

                var warnings = new List<string>();
                if (prohibitedVat > 0)
                    warnings.Add($"มีภาษีซื้อต้องห้าม (ค่ารับรอง) {prohibitedVat:N2} โดยปกติเคลมไม่ได้");
                if (pastWindow)
                    warnings.Add("ใบกำกับเกิน 6 เดือน อาจเครดิตภาษีซื้อไม่ได้");

                var desc = $"[ภาษีซื้อ] {doc.DocumentNumber}";
                if (warnings.Count > 0)
                    desc = $"⚠️ {desc} — {string.Join("; ", warnings)} (โปรดตรวจสอบ)";

                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = doc.Contact?.TaxId,
                    TaxPayerName = doc.Contact?.Name ?? "",
                    TransactionDate = doc.DocumentDate,
                    Description = desc,
                    IncomeAmount = doc.SubTotal,
                    TaxRate = doc.Lines.Any(l => l.VatRate > 0) ? doc.Lines.Where(l => l.VatRate > 0).Max(l => l.VatRate) : 0,
                    TaxAmount = doc.VatAmount,
                    DocumentId = doc.Id,
                    IncomeTypeCode = "INPUT"
                });
            }
        }

        // ===== Fallback: scan journal entries that have NO source document =====
        // Handles data imported via /integration/journals or /integration/daily-summary
        // which create JournalEntries without Documents.
        // Detection by BOTH account code AND name to support custom charts:
        //   - Output VAT: code starts with "2191" (ภาษีขาย) OR name contains "ภาษีขาย"
        //   - Input VAT: code starts with "116" or "114" or "115" OR name contains "ภาษีซื้อ"
        var endDateInclusive = endDate.Date.AddDays(1);
        var journalOnlyEntryIds = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= startDate && j.EntryDate < endDateInclusive
                && j.SourceDocumentId == null)
            .Select(j => j.Id)
            .ToListAsync();

        if (journalOnlyEntryIds.Any())
        {
            var vatLines = await _db.JournalEntryLines
                .Include(l => l.Account)
                .Include(l => l.JournalEntry)
                .Where(l => journalOnlyEntryIds.Contains(l.JournalEntryId)
                    && l.Account != null
                    && (l.Account.AccountCode.StartsWith("2191")
                        || l.Account.AccountCode.StartsWith("116")
                        || l.Account.AccountCode.StartsWith("114")
                        || l.Account.AccountCode.StartsWith("115")
                        || l.Account.AccountName.Contains("ภาษีขาย")
                        || l.Account.AccountName.Contains("ภาษีซื้อ")))
                .ToListAsync();

            bool IsOutputVat(Models.Entities.ChartOfAccount a) =>
                a.AccountCode.StartsWith("2191")
                || a.AccountName.Contains("ภาษีขาย");
            bool IsInputVat(Models.Entities.ChartOfAccount a) =>
                a.AccountCode.StartsWith("116") || a.AccountCode.StartsWith("114") || a.AccountCode.StartsWith("115")
                || a.AccountName.Contains("ภาษีซื้อ");

            // Group by JournalEntry to aggregate VAT per entry
            var byEntry = vatLines.GroupBy(l => l.JournalEntryId);
            foreach (var grp in byEntry)
            {
                var je = grp.First().JournalEntry;
                var outputVatLines = grp.Where(l => IsOutputVat(l.Account)).ToList();
                var inputVatLines = grp.Where(l => IsInputVat(l.Account)).ToList();

                // Output VAT: credit balance on liability account = VAT on sales
                var outputVatAmt = outputVatLines.Sum(l => l.CreditAmount - l.DebitAmount);
                if (outputVatAmt > 0)
                {
                    var baseAmount = companyVatRate > 0
                        ? Math.Round(outputVatAmt / (companyVatRate / 100m), 2, MidpointRounding.AwayFromZero)
                        : 0m;
                    outputVat += outputVatAmt;
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TransactionDate = je.EntryDate,
                        Description = $"[JE] {je.EntryNumber} {je.Description}".Trim(),
                        IncomeAmount = baseAmount,
                        TaxRate = companyVatRate,
                        TaxAmount = outputVatAmt,
                        IncomeTypeCode = "JE_OUTPUT"
                    });
                }

                // Input VAT: debit balance on 1140 = VAT on purchases
                var inputVatAmt = inputVatLines.Sum(l => l.DebitAmount - l.CreditAmount);
                if (inputVatAmt > 0)
                {
                    var baseAmount = companyVatRate > 0
                        ? Math.Round(inputVatAmt / (companyVatRate / 100m), 2, MidpointRounding.AwayFromZero)
                        : 0m;
                    inputVat += inputVatAmt;
                    report.Lines.Add(new TaxReportLine
                    {
                        TaxReportId = report.Id,
                        LineOrder = lineOrder++,
                        TransactionDate = je.EntryDate,
                        Description = $"[ภาษีซื้อ-JE] {je.EntryNumber} {je.Description}".Trim(),
                        IncomeAmount = baseAmount,
                        TaxRate = companyVatRate,
                        TaxAmount = inputVatAmt,
                        IncomeTypeCode = "JE_INPUT"
                    });
                }
            }
        }

        // Add summary line for VAT exempt sales/purchases
        if (vatExemptAmount != 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = "ยอดขาย/ซื้อยกเว้นภาษี",
                IncomeAmount = vatExemptAmount,
                TaxRate = 0,
                TaxAmount = 0,
                IncomeTypeCode = "EXEMPT"
            });
        }

        // VAT credit carryforward from previous month
        decimal vatCreditCarryforward = 0;
        var previousMonth = startDate.AddMonths(-1);
        var previousVatReport = await _db.TaxReports
            .Where(t => t.CompanyId == companyId
                && t.TaxType == TaxType.VAT
                && t.Year == previousMonth.Year
                && t.Month == previousMonth.Month
                && t.Status == TaxReportStatus.Filed)
            .FirstOrDefaultAsync();

        if (previousVatReport != null && previousVatReport.NetVat < 0)
        {
            // Previous month had excess input VAT = credit to carry forward
            vatCreditCarryforward = Math.Abs(previousVatReport.NetVat);
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = $"เครดิตภาษีซื้อยกมาจากเดือน {previousMonth.Month:D2}/{previousMonth.Year}",
                IncomeAmount = vatCreditCarryforward,
                TaxRate = 0,
                TaxAmount = -vatCreditCarryforward,
                IncomeTypeCode = "VAT_CREDIT_CF"
            });
        }

        report.OutputVat = outputVat;
        report.InputVat = inputVat + vatCreditCarryforward;
        report.NetVat = outputVat - inputVat - vatCreditCarryforward;
    }

    private async Task GenerateWhtReport(Guid companyId, DateTime startDate, DateTime endDate, TaxReport report)
    {
        var docs = await _db.Documents
            .Include(d => d.Lines)
            .Include(d => d.Contact)
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= startDate && d.DocumentDate <= endDate
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
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

        // Group by vendor (TaxPayerId) and add summary lines
        var vendorGroups = report.Lines
            .Where(l => !string.IsNullOrEmpty(l.TaxPayerId))
            .GroupBy(l => l.TaxPayerId)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in vendorGroups)
        {
            var vendorName = group.First().TaxPayerName;
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                TaxPayerId = group.Key,
                TaxPayerName = vendorName,
                Description = $"[สรุป] {vendorName}",
                IncomeAmount = group.Sum(l => l.IncomeAmount),
                TaxRate = 0,
                TaxAmount = group.Sum(l => l.TaxAmount),
                IncomeTypeCode = "SUMMARY"
            });
        }

        // ===== Fallback: scan journal entries that have NO source document =====
        var endDateInclusive = endDate.Date.AddDays(1);
        var journalOnlyEntryIds = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= startDate && j.EntryDate < endDateInclusive
                && j.SourceDocumentId == null)
            .Select(j => j.Id)
            .ToListAsync();

        if (journalOnlyEntryIds.Any())
        {
            // WHT accounts: 2191x (payable) or 11910 (receivable) or name contains "หัก ณ ที่จ่าย"
            var whtLines = await _db.JournalEntryLines
                .Include(l => l.Account)
                .Include(l => l.JournalEntry)
                .Where(l => journalOnlyEntryIds.Contains(l.JournalEntryId)
                    && l.Account != null
                    && (l.Account.AccountCode.StartsWith("2191")
                        || l.Account.AccountCode.StartsWith("11910")
                        || l.Account.AccountName.Contains("หัก ณ ที่จ่าย")))
                .ToListAsync();

            var byEntry = whtLines.GroupBy(l => l.JournalEntryId);
            foreach (var grp in byEntry)
            {
                var je = grp.First().JournalEntry;
                var whtAmount = grp.Sum(l => l.CreditAmount - l.DebitAmount);
                if (whtAmount <= 0) continue;

                var allLinesInJe = await _db.JournalEntryLines
                    .Where(l => l.JournalEntryId == je.Id)
                    .SumAsync(l => l.DebitAmount);
                var expenseTotal = allLinesInJe - whtAmount;
                var estimatedRate = expenseTotal > 0
                    ? Math.Round(whtAmount / expenseTotal * 100, 2, MidpointRounding.AwayFromZero)
                    : 3m;
                var baseAmount = estimatedRate > 0
                    ? Math.Round(whtAmount / (estimatedRate / 100), 2, MidpointRounding.AwayFromZero)
                    : 0m;

                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TransactionDate = je.EntryDate,
                    Description = $"[JE] {je.EntryNumber} {je.Description}".Trim(),
                    IncomeAmount = baseAmount,
                    TaxRate = estimatedRate,
                    TaxAmount = whtAmount,
                    IncomeTypeCode = "40(8)"
                });
            }
        }

        report.TotalIncome = report.Lines.Where(l => l.IncomeTypeCode != "SUMMARY").Sum(l => l.IncomeAmount);
        report.TotalTaxWithheld = report.Lines.Where(l => l.IncomeTypeCode != "SUMMARY").Sum(l => l.TaxAmount);
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

        // Query fixed assets for tax depreciation
        var activeAssets = await _db.Set<FixedAsset>()
            .Where(a => a.CompanyId == companyId && a.Status == AssetStatus.Active)
            .Select(a => a.Id)
            .ToListAsync();

        var totalDepreciation = 0m;
        if (activeAssets.Any())
        {
            totalDepreciation = await _db.Set<AssetDepreciation>()
                .Where(d => activeAssets.Contains(d.FixedAssetId)
                    && d.Year == year)
                .SumAsync(d => d.Amount);
        }

        // Add depreciation to total expenses
        totalExpenses += totalDepreciation;

        // Net profit before tax
        var netProfitBeforeTax = totalRevenue - totalExpenses;

        // Query previous year's CIT report for tax credit carryforward
        var previousYearCit = await _db.TaxReports
            .Where(t => t.CompanyId == companyId
                && t.TaxType == TaxType.CorporateIncomeTax
                && t.Year == year - 1
                && t.Status == TaxReportStatus.Filed)
            .FirstOrDefaultAsync();

        decimal taxCreditCarryforward = 0;
        if (previousYearCit != null && previousYearCit.NetVat < 0)
        {
            // NetVat is reused for net profit; negative means overpayment
            // TotalTaxWithheld has the CIT amount; if net profit was negative, there's a credit
            taxCreditCarryforward = Math.Abs(previousYearCit.TotalTaxWithheld);
        }

        // Thai CIT progressive rates (for SME companies)
        var citAmount = CalculateThaiCit(netProfitBeforeTax);

        // Apply tax credit carryforward
        var netCitAmount = Math.Max(0, citAmount - taxCreditCarryforward);

        report.TotalIncome = totalRevenue;
        report.TotalTaxWithheld = netCitAmount;
        report.NetVat = netProfitBeforeTax; // Reuse field for net profit

        var lineOrder = 1;
        report.Lines.Add(new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = lineOrder++,
            Description = "รายได้ทั้งปี",
            IncomeAmount = totalRevenue,
            TaxAmount = 0
        });
        report.Lines.Add(new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = lineOrder++,
            Description = "ค่าใช้จ่ายทั้งปี",
            IncomeAmount = totalExpenses - totalDepreciation,
            TaxAmount = 0
        });

        // Depreciation line
        if (totalDepreciation > 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = "ค่าเสื่อมราคาทางภาษี",
                IncomeAmount = totalDepreciation,
                TaxAmount = 0
            });
        }

        report.Lines.Add(new TaxReportLine
        {
            TaxReportId = report.Id,
            LineOrder = lineOrder++,
            Description = "กำไรสุทธิก่อนภาษี",
            IncomeAmount = netProfitBeforeTax,
            TaxAmount = citAmount,
            TaxRate = netProfitBeforeTax > 0 ? (citAmount / netProfitBeforeTax) * 100 : 0
        });

        // Tax credit carryforward line
        if (taxCreditCarryforward > 0)
        {
            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                Description = "เครดิตภาษีจากปีก่อน",
                IncomeAmount = taxCreditCarryforward,
                TaxAmount = -taxCreditCarryforward,
                IncomeTypeCode = "TAX_CREDIT"
            });
        }
    }

    /// <summary>
    /// ภ.ง.ด.91 — สรุปภาษีเงินได้บุคคลธรรมดา (annual personal income tax)
    /// คำนวณจากข้อมูล Payroll ของพนักงานทุกคนในปีภาษี
    /// </summary>
    private async Task GeneratePnd91Report(Guid companyId, int year, TaxReport report)
    {
        // Get all payroll details for the year
        var payrollDetails = await _db.Set<PayrollDetail>()
            .Include(d => d.Employee)
            .Include(d => d.PayrollRun)
            .Where(d => d.CompanyId == companyId
                && d.PayrollRun.Year == year
                && (d.PayrollRun.Status == "Approved" || d.PayrollRun.Status == "Paid"))
            .ToListAsync();

        if (!payrollDetails.Any())
        {
            report.Notes = "ไม่พบข้อมูล Payroll สำหรับปีนี้";
            return;
        }

        var lineOrder = 1;
        decimal totalIncome = 0;
        decimal totalTax = 0;
        decimal totalSso = 0;

        // Group by employee to summarize annual income
        var employeeGroups = payrollDetails.GroupBy(d => d.EmployeeId);
        foreach (var group in employeeGroups)
        {
            var emp = group.First().Employee;
            var annualGross = group.Sum(d => d.GrossIncome);
            var annualTax = group.Sum(d => d.WithholdingTax);
            var annualSso = group.Sum(d => d.SocialSecurityEmployee);
            var annualProvident = group.Sum(d => d.ProvidentFundEmployee);

            // Calculate tax liability using progressive brackets
            // Deductions: SSO + Provident Fund (up to 500K each) + personal allowance 60K
            var personalAllowance = 60_000m;
            var ssoDeduction = Math.Min(annualSso, 9_000m); // SSO max deduction
            var providentDeduction = Math.Min(annualProvident, 500_000m);
            var totalDeductions = personalAllowance + ssoDeduction + providentDeduction;
            var taxableIncome = Math.Max(0, annualGross - totalDeductions);
            var computedTax = CalculateThaiPersonalIncomeTax(taxableIncome);

            report.Lines.Add(new TaxReportLine
            {
                TaxReportId = report.Id,
                LineOrder = lineOrder++,
                TaxPayerId = emp.CitizenId,
                TaxPayerName = $"{emp.TitleTh}{emp.FirstNameTh} {emp.LastNameTh}",
                TransactionDate = new DateTime(year, 12, 31),
                Description = $"รหัส {emp.EmployeeCode} | เงินได้รวม {annualGross:N2} | หักค่าลดหย่อน {totalDeductions:N2}",
                IncomeAmount = annualGross,
                TaxRate = taxableIncome > 0 ? (computedTax / taxableIncome) * 100 : 0,
                TaxAmount = annualTax,
                IncomeTypeCode = "40(1)"
            });

            // Tax difference line if computed != withheld
            var taxDiff = computedTax - annualTax;
            if (Math.Abs(taxDiff) > 1) // threshold of 1 baht
            {
                report.Lines.Add(new TaxReportLine
                {
                    TaxReportId = report.Id,
                    LineOrder = lineOrder++,
                    TaxPayerId = emp.CitizenId,
                    TaxPayerName = $"{emp.FirstNameTh} {emp.LastNameTh}",
                    TransactionDate = new DateTime(year, 12, 31),
                    Description = taxDiff > 0
                        ? $"ภาษีชำระเพิ่มเติม (ภาษีคำนวณ {computedTax:N2} - ภาษีหัก ณ ที่จ่าย {annualTax:N2})"
                        : $"ภาษีชำระเกิน (ภาษีคำนวณ {computedTax:N2} - ภาษีหัก ณ ที่จ่าย {annualTax:N2})",
                    IncomeAmount = 0,
                    TaxRate = 0,
                    TaxAmount = taxDiff,
                    IncomeTypeCode = taxDiff > 0 ? "TAX_ADDITIONAL" : "TAX_REFUND"
                });
            }

            totalIncome += annualGross;
            totalTax += annualTax;
            totalSso += annualSso;
        }

        report.TotalIncome = totalIncome;
        report.TotalTaxWithheld = totalTax;
        report.OutputVat = totalSso; // Reuse field for SSO total
        report.Notes = $"จำนวนพนักงาน: {employeeGroups.Count()} คน | ปีภาษี: {year}";
    }

    /// <summary>Thai personal income tax (PIT) progressive rates</summary>
    private static decimal CalculateThaiPersonalIncomeTax(decimal taxableIncome)
    {
        if (taxableIncome <= 0) return 0;

        var brackets = new (decimal UpperBound, decimal Rate)[]
        {
            (150_000m, 0.00m),
            (300_000m, 0.05m),
            (500_000m, 0.10m),
            (750_000m, 0.15m),
            (1_000_000m, 0.20m),
            (2_000_000m, 0.25m),
            (5_000_000m, 0.30m),
            (decimal.MaxValue, 0.35m)
        };

        decimal tax = 0;
        decimal previousBound = 0;

        foreach (var (upperBound, rate) in brackets)
        {
            if (taxableIncome <= previousBound) break;

            var taxableInBracket = Math.Min(taxableIncome, upperBound) - previousBound;
            tax += taxableInBracket * rate;
            previousBound = upperBound;
        }

        return Math.Round(tax, 2, MidpointRounding.AwayFromZero);
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

        return Math.Round(tax, 2, MidpointRounding.AwayFromZero);
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

        if (report.Status == TaxReportStatus.Filed)
            throw new InvalidOperationException("รายงานภาษีนี้ถูกยื่นแล้ว");

        if (report.TaxType != TaxType.CorporateIncomeTax && !report.Lines.Any())
            throw new InvalidOperationException("รายงานภาษีต้องมีรายการอย่างน้อย 1 รายการ");

        report.Status = TaxReportStatus.Filed;
        report.FiledDate = DateTime.UtcNow;
        // Mark the filing as locked simultaneously — see Task 4 of the
        // ERP upgrade. Once Filed, every linked document/JE is read-only
        // until an explicit UnlockTaxFilingAsync (admin operation).
        report.FilingLockedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return MapToResponse(report);
    }

    public async Task<TaxReportResponse> UpdateTaxReportAsync(Guid companyId, Guid reportId, UpdateTaxReportRequest request)
    {
        var report = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        if (report.Status == TaxReportStatus.Filed)
            throw new InvalidOperationException("ไม่สามารถแก้ไขได้ — รายงานนี้ถูกยื่นแล้ว");

        if (request.Notes != null)
            report.Notes = request.Notes;

        if (request.Lines is { Count: > 0 })
        {
            foreach (var lineUpdate in request.Lines)
            {
                var line = report.Lines.FirstOrDefault(l => l.Id == lineUpdate.Id);
                if (line == null) continue;

                if (lineUpdate.IncomeAmount.HasValue) line.IncomeAmount = lineUpdate.IncomeAmount.Value;
                if (lineUpdate.TaxRate.HasValue) line.TaxRate = lineUpdate.TaxRate.Value;
                if (lineUpdate.TaxAmount.HasValue) line.TaxAmount = lineUpdate.TaxAmount.Value;
                if (lineUpdate.Description != null) line.Description = lineUpdate.Description;
                if (lineUpdate.Excluded.HasValue) line.IsExcluded = lineUpdate.Excluded.Value;
                line.UpdatedAt = DateTime.UtcNow;
            }

            // Totals recalc — lines the accountant excluded (IsExcluded) are
            // kept for audit but do NOT count toward the filed figures.
            if (report.TaxType == TaxType.VAT)
            {
                var active = report.Lines.Where(l => !l.IsExcluded);
                var nonSummaryLines = active.Where(l => l.IncomeTypeCode != "VAT_CREDIT_CF" && l.IncomeTypeCode != "EXEMPT");
                report.OutputVat = nonSummaryLines.Where(l => l.IncomeTypeCode != "INPUT").Sum(l => l.TaxAmount);
                report.InputVat = nonSummaryLines.Where(l => l.IncomeTypeCode == "INPUT").Sum(l => l.TaxAmount);
                var creditCf = active.Where(l => l.IncomeTypeCode == "VAT_CREDIT_CF").Sum(l => Math.Abs(l.TaxAmount));
                report.NetVat = report.OutputVat - report.InputVat - creditCf;
            }
            else
            {
                var active = report.Lines.Where(l => !l.IsExcluded && l.IncomeTypeCode != "SUMMARY");
                report.TotalIncome = active.Sum(l => l.IncomeAmount);
                report.TotalTaxWithheld = active.Sum(l => l.TaxAmount);
            }
        }

        report.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return MapToResponse(report);
    }

    public async Task<object> GetVatDebugAsync(Guid companyId, int year, int month)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);

        var docs = await _db.Documents
            .Where(d => d.CompanyId == companyId
                && d.DocumentDate >= start && d.DocumentDate < end
                && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected)
            .Select(d => new { d.Id, d.DocumentNumber, d.DocumentType, d.DocumentDate, d.VatAmount, d.Status })
            .ToListAsync();

        var jeIds = await _db.JournalEntries
            .Where(j => j.CompanyId == companyId
                && j.Status == JournalEntryStatus.Posted
                && j.EntryDate >= start && j.EntryDate < end)
            .Select(j => new { j.Id, j.EntryNumber, j.EntryDate, j.SourceDocumentId })
            .ToListAsync();

        var vatLines = await _db.JournalEntryLines
            .Include(l => l.Account)
            .Include(l => l.JournalEntry)
            .Where(l => l.JournalEntry.CompanyId == companyId
                && l.JournalEntry.Status == JournalEntryStatus.Posted
                && l.JournalEntry.EntryDate >= start && l.JournalEntry.EntryDate < end
                && l.Account != null
                && (l.Account.AccountCode.StartsWith("2191")
                    || l.Account.AccountCode.StartsWith("116")
                    || l.Account.AccountCode.StartsWith("114")
                    || l.Account.AccountCode.StartsWith("115")
                    || l.Account.AccountName.Contains("ภาษีขาย")
                    || l.Account.AccountName.Contains("ภาษีซื้อ")))
            .Select(l => new
            {
                AccountCode = l.Account.AccountCode,
                AccountName = l.Account.AccountName,
                EntryNumber = l.JournalEntry.EntryNumber,
                EntryDate = l.JournalEntry.EntryDate,
                Debit = l.DebitAmount,
                Credit = l.CreditAmount,
                HasSourceDoc = l.JournalEntry.SourceDocumentId != null
            })
            .ToListAsync();

        var byAccount = vatLines
            .GroupBy(l => new { l.AccountCode, l.AccountName })
            .Select(g => new
            {
                AccountCode = g.Key.AccountCode,
                AccountName = g.Key.AccountName,
                LineCount = g.Count(),
                TotalDebit = g.Sum(l => l.Debit),
                TotalCredit = g.Sum(l => l.Credit),
                Balance = g.Sum(l => l.Credit - l.Debit)
            })
            .OrderBy(x => x.AccountCode)
            .ToList();

        return new
        {
            Period = $"{year}-{month:D2}",
            DateRange = new { Start = start, End = end },
            DocumentsInPeriod = docs.Count,
            DocumentsWithVat = docs.Count(d => d.VatAmount != 0),
            SampleDocuments = docs.Take(5),
            JournalEntriesInPeriod = jeIds.Count,
            JournalEntriesFromApi = jeIds.Count(j => j.SourceDocumentId == null),
            JournalEntriesFromDocuments = jeIds.Count(j => j.SourceDocumentId != null),
            VatAccountsFound = byAccount.Count,
            VatAccountSummary = byAccount,
            VatLinesTotal = vatLines.Count,
            SampleVatLines = vatLines.Take(10)
        };
    }

    public async Task<TaxReportResponse> RegenerateTaxReportAsync(Guid companyId, Guid reportId)
    {
        var existing = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        if (existing.Status == TaxReportStatus.Filed)
            throw new InvalidOperationException("ไม่สามารถสร้างใหม่ได้ — รายงานนี้ถูกยื่นแล้ว");

        var request = new CreateTaxReportRequest(existing.TaxType, existing.Year, existing.Month);

        _db.TaxReportLines.RemoveRange(existing.Lines);
        _db.TaxReports.Remove(existing);
        await _db.SaveChangesAsync();

        return await GenerateTaxReportAsync(companyId, request);
    }

    public async Task DeleteTaxReportAsync(Guid companyId, Guid reportId)
    {
        var existing = await _db.TaxReports
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.Id == reportId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบรายงานภาษี");

        if (existing.Status == TaxReportStatus.Filed)
            throw new InvalidOperationException("ไม่สามารถลบได้ — รายงานนี้ถูกยื่นแล้ว");

        _db.TaxReportLines.RemoveRange(existing.Lines);
        _db.TaxReports.Remove(existing);
        await _db.SaveChangesAsync();
    }

    public async Task<int> AutoRefreshReportsAsync(Guid companyId, int months = 2)
    {
        var now = DateTime.UtcNow;
        var taxTypes = new[] { TaxType.VAT, TaxType.WithholdingTax3, TaxType.WithholdingTax53 };
        int refreshed = 0;

        for (int i = 0; i < months; i++)
        {
            var target = now.AddMonths(-i);
            var year = target.Year;
            var month = target.Month;

            foreach (var taxType in taxTypes)
            {
                var existing = await _db.TaxReports
                    .Include(r => r.Lines)
                    .FirstOrDefaultAsync(r => r.CompanyId == companyId
                        && r.TaxType == taxType && r.Year == year && r.Month == month);

                if (existing is { Status: TaxReportStatus.Filed })
                    continue;

                if (existing != null)
                {
                    _db.TaxReportLines.RemoveRange(existing.Lines);
                    _db.TaxReports.Remove(existing);
                    await _db.SaveChangesAsync();
                }

                try
                {
                    await GenerateTaxReportAsync(companyId, new CreateTaxReportRequest(taxType, year, month));
                    refreshed++;
                }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Failed to regenerate tax report for {taxType} {year}/{month}: {ex.Message}"); }
            }
        }

        return refreshed;
    }

    private static TaxReportResponse MapToResponse(TaxReport r) => new(
        r.Id, r.TaxType, r.Year, r.Month, r.Status, r.FiledDate,
        r.OutputVat, r.InputVat, r.NetVat, r.TotalIncome, r.TotalTaxWithheld,
        r.Lines.OrderBy(l => l.LineOrder).Select(l => new TaxReportLineResponse(
            l.Id, l.LineOrder, l.TaxPayerId, l.TaxPayerName,
            l.TransactionDate, l.Description, l.IncomeAmount,
            l.TaxRate, l.TaxAmount, l.IncomeTypeCode, l.IsExcluded)).ToList(),
        r.Notes);
}
