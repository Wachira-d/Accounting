using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.ReportBuilder;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ReportBuilderService : IReportBuilderService
{
    private readonly AccountingDbContext _db;

    public ReportBuilderService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<CustomReportResponse> CreateAsync(Guid companyId, CreateCustomReportRequest request, string userId)
    {
        var report = new CustomReport
        {
            CompanyId = companyId,
            Name = request.Name,
            Description = request.Description,
            ReportType = ResolveReportType(request.ReportType, request.ChartType),
            Category = ResolveCategory(request.Category),
            DataSourceType = request.DataSourceType,
            FilterJson = request.FilterJson,
            ColumnsJson = request.ColumnsJson,
            SortingJson = request.SortingJson,
            GroupingJson = request.GroupingJson,
            AggregationJson = request.AggregationJson,
            ChartType = request.ChartType,
            ChartConfigJson = request.ChartConfigJson,
            ShowTotals = request.ShowTotals,
            IsPublic = request.IsPublic,
            IsScheduled = request.IsScheduled,
            ScheduleFrequency = request.ScheduleFrequency,
            SendToEmails = request.SendToEmails,
            ExportFormat = request.ExportFormat,
            CreatedByUserId = userId,
            CreatedBy = userId
        };

        _db.Set<CustomReport>().Add(report);
        await _db.SaveChangesAsync();

        return MapToResponse(report);
    }

    /// <summary>ชนิดรายงานที่ไม่ได้ระบุ derive จาก chart — ฟอร์มไม่มีช่องนี้ (รอบ 193 · A09)</summary>
    private static string ResolveReportType(string? reportType, string? chartType)
        => !string.IsNullOrWhiteSpace(reportType) ? reportType.Trim()
         : !string.IsNullOrWhiteSpace(chartType) ? "Chart" : "Table";

    /// <summary>หมวดว่าง = "Custom" (รายงานที่ผู้ใช้สร้างเอง) — ฟอร์มถือว่าช่องนี้ไม่บังคับ</summary>
    private static string ResolveCategory(string? category)
        => string.IsNullOrWhiteSpace(category) ? "Custom" : category.Trim();

    public async Task<CustomReportResponse> GetByIdAsync(Guid companyId, Guid reportId)
    {
        var report = await _db.Set<CustomReport>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == reportId)
            ?? throw new InvalidOperationException("Custom report not found.");

        return MapToResponse(report);
    }

    public async Task<List<CustomReportListResponse>> GetAllAsync(Guid companyId, string? category = null)
    {
        var query = _db.Set<CustomReport>()
            .Where(r => r.CompanyId == companyId);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(r => r.Category == category);

        return await query
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new CustomReportListResponse(
                r.Id, r.Name, r.ReportType, r.Category, r.IsPublic, r.CreatedAt))
            .ToListAsync();
    }

    public async Task<CustomReportResponse> UpdateAsync(Guid companyId, Guid reportId, UpdateCustomReportRequest request)
    {
        var report = await _db.Set<CustomReport>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == reportId)
            ?? throw new InvalidOperationException("Custom report not found.");

        if (request.Name != null) report.Name = request.Name;
        // "" = ล้างค่า (เดิมหน้าเว็บส่ง null เมื่อว่าง ⇒ ล้างคำอธิบาย/กราฟไม่ได้เลย)
        if (request.Description != null) report.Description = request.Description.Length == 0 ? null : request.Description;
        if (request.Category != null) report.Category = ResolveCategory(request.Category);
        if (request.FilterJson != null) report.FilterJson = request.FilterJson;
        if (request.ColumnsJson != null) report.ColumnsJson = request.ColumnsJson;
        if (request.SortingJson != null) report.SortingJson = request.SortingJson;
        if (request.GroupingJson != null) report.GroupingJson = request.GroupingJson;
        if (request.AggregationJson != null) report.AggregationJson = request.AggregationJson;
        if (request.ChartType != null)
        {
            report.ChartType = request.ChartType.Length == 0 ? null : request.ChartType;
            // ชนิดที่ derive จาก chart ต้องตามกันเมื่อเปลี่ยน — Pivot/Dashboard ที่ตั้งผ่าน API ไม่ถูกแตะ
            if (report.ReportType is "Chart" or "Table")
                report.ReportType = ResolveReportType(null, report.ChartType);
        }
        if (request.ShowTotals.HasValue) report.ShowTotals = request.ShowTotals.Value;
        if (request.IsPublic.HasValue) report.IsPublic = request.IsPublic.Value;
        if (request.IsScheduled.HasValue) report.IsScheduled = request.IsScheduled.Value;
        if (request.ScheduleFrequency != null) report.ScheduleFrequency = request.ScheduleFrequency;

        await _db.SaveChangesAsync();
        return MapToResponse(report);
    }

    public async Task DeleteAsync(Guid companyId, Guid reportId)
    {
        var report = await _db.Set<CustomReport>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == reportId)
            ?? throw new InvalidOperationException("Custom report not found.");

        report.IsDeleted = true;
        await _db.SaveChangesAsync();
    }

    public async Task<CustomReportResponse> DuplicateAsync(Guid companyId, Guid reportId, string newName)
    {
        var original = await _db.Set<CustomReport>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == reportId)
            ?? throw new InvalidOperationException("Custom report not found.");

        var copy = new CustomReport
        {
            CompanyId = companyId,
            Name = newName,
            Description = original.Description,
            ReportType = original.ReportType,
            Category = original.Category,
            DataSourceType = original.DataSourceType,
            FilterJson = original.FilterJson,
            ColumnsJson = original.ColumnsJson,
            SortingJson = original.SortingJson,
            GroupingJson = original.GroupingJson,
            AggregationJson = original.AggregationJson,
            ChartType = original.ChartType,
            ChartConfigJson = original.ChartConfigJson,
            ShowTotals = original.ShowTotals,
            IsPublic = false,
            IsScheduled = false,
            CreatedByUserId = original.CreatedByUserId
        };

        _db.Set<CustomReport>().Add(copy);
        await _db.SaveChangesAsync();

        return MapToResponse(copy);
    }

    public async Task<ReportExecutionResponse> ExecuteAsync(Guid companyId, Guid reportId, Dictionary<string, string>? parameters = null)
    {
        var report = await _db.Set<CustomReport>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == reportId)
            ?? throw new InvalidOperationException("Custom report not found.");

        var data = await BuildDynamicQueryAsync(companyId, report, parameters);

        // Calculate totals if configured
        Dictionary<string, object>? totals = null;
        if (report.ShowTotals && data.Count > 0)
        {
            totals = new Dictionary<string, object>();
            foreach (var key in data[0].Keys)
            {
                if (data[0][key] is decimal)
                {
                    var sum = data.Sum(row => row.TryGetValue(key, out var v) && v is decimal d ? d : 0m);
                    totals[key] = sum;
                }
            }
        }

        return new ReportExecutionResponse(
            report.Name,
            report.ReportType,
            data,
            totals,
            data.Count,
            DateTime.UtcNow);
    }

    public async Task<byte[]> ExportAsync(Guid companyId, Guid reportId, string format, Dictionary<string, string>? parameters = null)
    {
        var executionResult = await ExecuteAsync(companyId, reportId, parameters);

        // Build a simple CSV export as the default implementation
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms);

        if (executionResult.Data.Count > 0)
        {
            // Write header
            await writer.WriteLineAsync(string.Join(",", executionResult.Data[0].Keys));

            // Write rows
            foreach (var row in executionResult.Data)
            {
                var values = row.Values.Select(v => $"\"{v?.ToString()?.Replace("\"", "\"\"")}\"");
                await writer.WriteLineAsync(string.Join(",", values));
            }
        }

        await writer.FlushAsync();
        return ms.ToArray();
    }

    public Task<List<ReportDataSourceResponse>> GetDataSourcesAsync()
    {
        var sources = new List<ReportDataSourceResponse>
        {
            new("JournalEntry", "Journal Entries", "General ledger journal entries with debit/credit lines"),
            new("Document", "Documents", "Invoices, receipts, purchase orders, and other business documents"),
            new("BankTransaction", "Bank Transactions", "Bank account deposits, withdrawals, and transfers"),
            new("Product", "Products & Services", "Product catalog with pricing and inventory data"),
            new("Contact", "Contacts", "Customer and supplier contact information"),
            new("Payment", "Payments", "Payment records linked to documents"),
            new("FixedAsset", "Fixed Assets", "Asset register with depreciation data"),
            new("ExpenseClaim", "Expense Claims", "Employee expense claims and reimbursements")
        };

        return Task.FromResult(sources);
    }

    public Task<List<ReportColumnDefinition>> GetColumnsForDataSourceAsync(string dataSourceType)
    {
        var columns = dataSourceType switch
        {
            "JournalEntry" => new List<ReportColumnDefinition>
            {
                new("EntryNumber", "Entry Number", "string", true, true, false),
                new("EntryDate", "Entry Date", "date", true, true, true),
                new("Description", "Description", "string", true, true, false),
                new("Status", "Status", "string", true, true, true),
                new("TotalDebit", "Total Debit", "decimal", true, false, false),
                new("TotalCredit", "Total Credit", "decimal", true, false, false),
                new("AccountCode", "Account Code", "string", true, true, true),
                new("AccountName", "Account Name", "string", true, true, true),
                new("DebitAmount", "Debit Amount", "decimal", true, false, false),
                new("CreditAmount", "Credit Amount", "decimal", true, false, false)
            },
            "Document" => new List<ReportColumnDefinition>
            {
                new("DocumentNumber", "Document Number", "string", true, true, false),
                new("DocumentType", "Document Type", "string", true, true, true),
                new("DocumentDate", "Document Date", "date", true, true, true),
                new("DueDate", "Due Date", "date", true, true, false),
                new("ContactName", "Contact", "string", true, true, true),
                new("SubTotal", "Sub Total", "decimal", true, false, false),
                new("VatAmount", "VAT Amount", "decimal", true, false, false),
                new("TotalAmount", "Total Amount", "decimal", true, false, false),
                new("PaidAmount", "Paid Amount", "decimal", true, false, false),
                new("BalanceDue", "Balance Due", "decimal", true, false, false),
                new("Status", "Status", "string", true, true, true)
            },
            "BankTransaction" => new List<ReportColumnDefinition>
            {
                new("TransactionDate", "Transaction Date", "date", true, true, true),
                new("BankAccountName", "Bank Account", "string", true, true, true),
                new("TransactionType", "Type", "string", true, true, true),
                new("Amount", "Amount", "decimal", true, false, false),
                new("BalanceAfter", "Balance After", "decimal", true, false, false),
                new("Description", "Description", "string", true, true, false),
                new("Reference", "Reference", "string", true, true, false),
                new("ReconciliationStatus", "Reconciliation", "string", true, true, true)
            },
            "Product" => new List<ReportColumnDefinition>
            {
                new("Code", "Product Code", "string", true, true, false),
                new("Name", "Product Name", "string", true, true, false),
                new("ProductType", "Type", "string", true, true, true),
                new("SellingPrice", "Selling Price", "decimal", true, false, false),
                new("CostPrice", "Cost Price", "decimal", true, false, false),
                new("CurrentStock", "Current Stock", "decimal", true, false, false),
                new("IsActive", "Active", "boolean", true, true, true)
            },
            "Contact" => new List<ReportColumnDefinition>
            {
                new("Name", "Contact Name", "string", true, true, false),
                new("TaxId", "Tax ID", "string", true, true, false),
                new("IsCustomer", "Is Customer", "boolean", false, true, true),
                new("IsSupplier", "Is Supplier", "boolean", false, true, true),
                new("Email", "Email", "string", true, true, false),
                new("Phone", "Phone", "string", false, true, false),
                new("IsActive", "Active", "boolean", false, true, true)
            },
            _ => new List<ReportColumnDefinition>()
        };

        return Task.FromResult(columns);
    }

    private async Task<List<Dictionary<string, object>>> BuildDynamicQueryAsync(
        Guid companyId, CustomReport report, Dictionary<string, string>? parameters)
    {
        return report.DataSourceType switch
        {
            "JournalEntry" => await BuildJournalEntryQueryAsync(companyId, parameters),
            "Document" => await BuildDocumentQueryAsync(companyId, parameters),
            "BankTransaction" => await BuildBankTransactionQueryAsync(companyId, parameters),
            "Product" => await BuildProductQueryAsync(companyId, parameters),
            "Contact" => await BuildContactQueryAsync(companyId, parameters),
            "Payment" => await BuildPaymentQueryAsync(companyId, parameters),
            _ => new List<Dictionary<string, object>>()
        };
    }

    private async Task<List<Dictionary<string, object>>> BuildJournalEntryQueryAsync(
        Guid companyId, Dictionary<string, string>? parameters)
    {
        var query = _db.JournalEntryLines
            .Include(l => l.JournalEntry)
            .Include(l => l.Account)
            .Where(l => l.JournalEntry.CompanyId == companyId
                     && l.JournalEntry.Status == JournalEntryStatus.Posted);

        if (parameters != null)
        {
            if (parameters.TryGetValue("fromDate", out var fromStr) && DateTime.TryParse(fromStr, out var fromDate))
                query = query.Where(l => l.JournalEntry.EntryDate >= fromDate);

            if (parameters.TryGetValue("toDate", out var toStr) && DateTime.TryParse(toStr, out var toDate))
                query = query.Where(l => l.JournalEntry.EntryDate <= toDate);

            if (parameters.TryGetValue("accountCode", out var accountCode))
                query = query.Where(l => l.Account.AccountCode.StartsWith(accountCode));

            if (parameters.TryGetValue("accountType", out var acctTypeStr) && Enum.TryParse<AccountType>(acctTypeStr, out var acctType))
                query = query.Where(l => l.Account.AccountType == acctType);
        }

        var data = await query
            .OrderBy(l => l.JournalEntry.EntryDate)
            .ThenBy(l => l.JournalEntry.EntryNumber)
            .ThenBy(l => l.LineOrder)
            .Take(10000)
            .Select(l => new
            {
                l.JournalEntry.EntryNumber,
                l.JournalEntry.EntryDate,
                JournalDescription = l.JournalEntry.Description ?? "",
                l.Account.AccountCode,
                l.Account.AccountName,
                l.DebitAmount,
                l.CreditAmount,
                LineDescription = l.Description ?? ""
            })
            .ToListAsync();

        return data.Select(r => new Dictionary<string, object>
        {
            ["EntryNumber"] = r.EntryNumber,
            ["EntryDate"] = r.EntryDate,
            ["Description"] = r.JournalDescription,
            ["AccountCode"] = r.AccountCode,
            ["AccountName"] = r.AccountName,
            ["DebitAmount"] = r.DebitAmount,
            ["CreditAmount"] = r.CreditAmount,
            ["LineDescription"] = r.LineDescription
        }).ToList();
    }

    private async Task<List<Dictionary<string, object>>> BuildDocumentQueryAsync(
        Guid companyId, Dictionary<string, string>? parameters)
    {
        // ไม่ Include/Select Contact ตรง ๆ (required nav → INNER JOIN ตัดใบที่ contact
        // ถูกลบ). เลือก ContactId แล้ว map ชื่อจาก dict (IgnoreQueryFilters) ทีหลัง
        var query = _db.Documents
            .Where(d => d.CompanyId == companyId);

        if (parameters != null)
        {
            if (parameters.TryGetValue("fromDate", out var fromStr) && DateTime.TryParse(fromStr, out var fromDate))
                query = query.Where(d => d.DocumentDate >= fromDate);

            if (parameters.TryGetValue("toDate", out var toStr) && DateTime.TryParse(toStr, out var toDate))
                query = query.Where(d => d.DocumentDate <= toDate);

            if (parameters.TryGetValue("documentType", out var docTypeStr) && Enum.TryParse<DocumentType>(docTypeStr, out var docType))
                query = query.Where(d => d.DocumentType == docType);

            if (parameters.TryGetValue("status", out var statusStr) && Enum.TryParse<DocumentStatus>(statusStr, out var status))
                query = query.Where(d => d.Status == status);

            if (parameters.TryGetValue("contactId", out var contactIdStr) && Guid.TryParse(contactIdStr, out var contactId))
                query = query.Where(d => d.ContactId == contactId);
        }

        var data = await query
            .OrderByDescending(d => d.DocumentDate)
            .Take(10000)
            .Select(d => new
            {
                d.DocumentNumber,
                DocumentType = d.DocumentType.ToString(),
                d.DocumentDate,
                d.DueDate,
                d.ContactId,
                d.SubTotal,
                d.VatAmount,
                d.TotalAmount,
                d.PaidAmount,
                d.BalanceDue,
                Status = d.Status.ToString()
            })
            .ToListAsync();

        // ชื่อ contact (รวมที่ถูกลบ) จาก dict — กันแถวหายและชื่อไม่ว่าง
        var cids = data.Select(r => r.ContactId).Distinct().ToList();
        var cmap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && cids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        return data.Select(r => new Dictionary<string, object>
        {
            ["DocumentNumber"] = r.DocumentNumber,
            ["DocumentType"] = r.DocumentType,
            ["DocumentDate"] = r.DocumentDate,
            ["DueDate"] = r.DueDate ?? (object)"",
            ["ContactName"] = cmap.GetValueOrDefault(r.ContactId) ?? "",
            ["SubTotal"] = r.SubTotal,
            ["VatAmount"] = r.VatAmount,
            ["TotalAmount"] = r.TotalAmount,
            ["PaidAmount"] = r.PaidAmount,
            ["BalanceDue"] = r.BalanceDue,
            ["Status"] = r.Status
        }).ToList();
    }

    private async Task<List<Dictionary<string, object>>> BuildBankTransactionQueryAsync(
        Guid companyId, Dictionary<string, string>? parameters)
    {
        var query = _db.BankTransactions
            .Include(t => t.BankAccount)
            .Where(t => t.BankAccount.CompanyId == companyId);

        if (parameters != null)
        {
            if (parameters.TryGetValue("fromDate", out var fromStr) && DateTime.TryParse(fromStr, out var fromDate))
                query = query.Where(t => t.TransactionDate >= fromDate);

            if (parameters.TryGetValue("toDate", out var toStr) && DateTime.TryParse(toStr, out var toDate))
                query = query.Where(t => t.TransactionDate <= toDate);

            if (parameters.TryGetValue("bankAccountId", out var baIdStr) && Guid.TryParse(baIdStr, out var bankAccId))
                query = query.Where(t => t.BankAccountId == bankAccId);
        }

        var data = await query
            .OrderByDescending(t => t.TransactionDate)
            .Take(10000)
            .Select(t => new
            {
                t.TransactionDate,
                BankAccountName = t.BankAccount.AccountName,
                TransactionType = t.TransactionType.ToString(),
                t.Amount,
                t.BalanceAfter,
                Description = t.Description ?? "",
                Reference = t.Reference ?? "",
                ReconciliationStatus = t.ReconciliationStatus.ToString()
            })
            .ToListAsync();

        return data.Select(r => new Dictionary<string, object>
        {
            ["TransactionDate"] = r.TransactionDate,
            ["BankAccountName"] = r.BankAccountName,
            ["TransactionType"] = r.TransactionType,
            ["Amount"] = r.Amount,
            ["BalanceAfter"] = r.BalanceAfter,
            ["Description"] = r.Description,
            ["Reference"] = r.Reference,
            ["ReconciliationStatus"] = r.ReconciliationStatus
        }).ToList();
    }

    private async Task<List<Dictionary<string, object>>> BuildProductQueryAsync(
        Guid companyId, Dictionary<string, string>? parameters)
    {
        var query = _db.Products.Where(p => p.CompanyId == companyId);

        if (parameters != null)
        {
            if (parameters.TryGetValue("productType", out var ptStr) && Enum.TryParse<ProductType>(ptStr, out var pt))
                query = query.Where(p => p.ProductType == pt);

            if (parameters.TryGetValue("isActive", out var activeStr) && bool.TryParse(activeStr, out var isActive))
                query = query.Where(p => p.IsActive == isActive);
        }

        var data = await query
            .OrderBy(p => p.Code)
            .Take(10000)
            .Select(p => new
            {
                p.Code,
                p.Name,
                ProductType = p.ProductType.ToString(),
                p.SellingPrice,
                p.CostPrice,
                p.CurrentStock,
                p.IsActive
            })
            .ToListAsync();

        return data.Select(r => new Dictionary<string, object>
        {
            ["Code"] = r.Code,
            ["Name"] = r.Name,
            ["ProductType"] = r.ProductType,
            ["SellingPrice"] = r.SellingPrice,
            ["CostPrice"] = r.CostPrice,
            ["CurrentStock"] = r.CurrentStock,
            ["IsActive"] = r.IsActive
        }).ToList();
    }

    private async Task<List<Dictionary<string, object>>> BuildContactQueryAsync(
        Guid companyId, Dictionary<string, string>? parameters)
    {
        var query = _db.Contacts.Where(c => c.CompanyId == companyId);

        if (parameters != null)
        {
            if (parameters.TryGetValue("isCustomer", out var custStr) && bool.TryParse(custStr, out var isCust))
                query = query.Where(c => c.IsCustomer == isCust);

            if (parameters.TryGetValue("isSupplier", out var suppStr) && bool.TryParse(suppStr, out var isSupp))
                query = query.Where(c => c.IsSupplier == isSupp);
        }

        var data = await query
            .OrderBy(c => c.Name)
            .Take(10000)
            .Select(c => new
            {
                c.Name,
                TaxId = c.TaxId ?? "",
                c.IsCustomer,
                c.IsSupplier,
                Email = c.Email ?? "",
                Phone = c.Phone ?? "",
                c.IsActive
            })
            .ToListAsync();

        return data.Select(r => new Dictionary<string, object>
        {
            ["Name"] = r.Name,
            ["TaxId"] = r.TaxId,
            ["IsCustomer"] = r.IsCustomer,
            ["IsSupplier"] = r.IsSupplier,
            ["Email"] = r.Email,
            ["Phone"] = r.Phone,
            ["IsActive"] = r.IsActive
        }).ToList();
    }

    private async Task<List<Dictionary<string, object>>> BuildPaymentQueryAsync(
        Guid companyId, Dictionary<string, string>? parameters)
    {
        // ไม่ ThenInclude Contact (required → INNER JOIN ตัดแถวที่ contact ถูกลบ) —
        // เลือก ContactId แล้ว map ชื่อจาก dict (IgnoreQueryFilters) ทีหลัง
        var query = _db.Payments
            .Include(p => p.Document)
            .Where(p => p.CompanyId == companyId);

        if (parameters != null)
        {
            if (parameters.TryGetValue("fromDate", out var fromStr) && DateTime.TryParse(fromStr, out var fromDate))
                query = query.Where(p => p.PaymentDate >= fromDate);

            if (parameters.TryGetValue("toDate", out var toStr) && DateTime.TryParse(toStr, out var toDate))
                query = query.Where(p => p.PaymentDate <= toDate);
        }

        var data = await query
            .OrderByDescending(p => p.PaymentDate)
            .Take(10000)
            .Select(p => new
            {
                p.PaymentNumber,
                p.PaymentDate,
                p.Amount,
                PaymentMethod = p.PaymentMethod.ToString(),
                Reference = p.Reference ?? "",
                DocumentNumber = p.Document.DocumentNumber,
                p.Document.ContactId
            })
            .ToListAsync();

        var cids = data.Select(r => r.ContactId).Distinct().ToList();
        var cmap = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == companyId && cids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Name);

        return data.Select(r => new Dictionary<string, object>
        {
            ["PaymentNumber"] = r.PaymentNumber,
            ["PaymentDate"] = r.PaymentDate,
            ["Amount"] = r.Amount,
            ["PaymentMethod"] = r.PaymentMethod,
            ["Reference"] = r.Reference,
            ["DocumentNumber"] = r.DocumentNumber,
            ["ContactName"] = cmap.GetValueOrDefault(r.ContactId) ?? ""
        }).ToList();
    }

    private static CustomReportResponse MapToResponse(CustomReport r) => new(
        r.Id, r.Name, r.Description, r.ReportType, r.Category, r.DataSourceType,
        r.FilterJson, r.ColumnsJson, r.GroupingJson, r.ChartType,
        r.IsPublic, r.IsScheduled, r.CreatedAt, r.ShowTotals);
}
