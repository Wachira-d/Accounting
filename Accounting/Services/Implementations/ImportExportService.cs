using System.Text;
using System.Text.Json;
using Accounting.Data;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class ImportExportService : IImportExportService
{
    private readonly AccountingDbContext _db;

    public ImportExportService(AccountingDbContext db)
    {
        _db = db;
    }

    public async Task<ImportResult> ImportAsync(Guid companyId, ImportRequest request, string performedBy)
    {
        var validation = await ValidateImportAsync(companyId, request);
        if (validation.ErrorCount > 0 && validation.SuccessCount == 0)
            return validation;

        var successCount = 0;
        var errors = new List<ImportError>();

        for (int i = 0; i < request.Data.Count; i++)
        {
            try
            {
                var row = request.Data[i];
                switch (request.EntityType.ToLower())
                {
                    case "contacts":
                        await ImportContactAsync(companyId, row);
                        break;
                    case "products":
                        await ImportProductAsync(companyId, row);
                        break;
                    case "chartofaccounts":
                        await ImportAccountAsync(companyId, row);
                        break;
                    case "banktransactions":
                        await ImportBankTransactionAsync(companyId, row);
                        break;
                    default:
                        errors.Add(new ImportError(i + 1, "EntityType", request.EntityType, $"ไม่รองรับการนำเข้า {request.EntityType}"));
                        continue;
                }
                successCount++;
            }
            catch (Exception ex)
            {
                errors.Add(new ImportError(i + 1, "", "", ex.Message));
            }
        }

        await _db.SaveChangesAsync();

        return new ImportResult(request.EntityType, request.Data.Count, successCount,
            errors.Count, request.Data.Count - successCount - errors.Count, errors, DateTime.UtcNow);
    }

    public Task<ImportTemplateResponse> GetImportTemplateAsync(string entityType)
    {
        var template = entityType.ToLower() switch
        {
            "contacts" => new ImportTemplateResponse("contacts", new List<ImportField>
            {
                new("Name", "ชื่อ", "string", true, "ชื่อลูกค้า/ผู้ขาย", null),
                new("TaxId", "เลขผู้เสียภาษี", "string", false, "เลขประจำตัวผู้เสียภาษี 13 หลัก", null),
                new("IsCustomer", "เป็นลูกค้า", "bool", false, "true/false", new List<string> { "true", "false" }),
                new("IsSupplier", "เป็นผู้ขาย", "bool", false, "true/false", new List<string> { "true", "false" }),
                new("Email", "อีเมล", "string", false, null, null),
                new("Phone", "โทรศัพท์", "string", false, null, null),
                new("Address", "ที่อยู่", "string", false, null, null),
                new("ContactPerson", "ผู้ติดต่อ", "string", false, null, null),
            }, new List<Dictionary<string, string>>
            {
                new() { ["Name"] = "บริษัท ตัวอย่าง จำกัด", ["TaxId"] = "0123456789012", ["IsCustomer"] = "true", ["Email"] = "info@example.com" }
            }),

            "products" => new ImportTemplateResponse("products", new List<ImportField>
            {
                new("Code", "รหัสสินค้า", "string", true, null, null),
                new("Name", "ชื่อสินค้า", "string", true, null, null),
                new("ProductType", "ประเภท", "enum", false, null, new List<string> { "Product", "Service", "NonStock" }),
                new("Unit", "หน่วยนับ", "string", true, null, null),
                new("SellingPrice", "ราคาขาย", "decimal", false, null, null),
                new("CostPrice", "ราคาทุน", "decimal", false, null, null),
                new("VatRate", "อัตราภาษี", "decimal", false, null, null),
                new("Category", "หมวดหมู่", "string", false, null, null),
            }, new List<Dictionary<string, string>>
            {
                new() { ["Code"] = "P001", ["Name"] = "สินค้าตัวอย่าง", ["Unit"] = "ชิ้น", ["SellingPrice"] = "100.00", ["CostPrice"] = "60.00" }
            }),

            "chartofaccounts" => new ImportTemplateResponse("chartofaccounts", new List<ImportField>
            {
                new("AccountCode", "รหัสบัญชี", "string", true, null, null),
                new("AccountName", "ชื่อบัญชี", "string", true, null, null),
                new("AccountNameEn", "ชื่อบัญชี (อังกฤษ)", "string", false, null, null),
                new("AccountType", "ประเภทบัญชี", "enum", true, null, new List<string> { "Asset", "Liability", "Equity", "Revenue", "Expense" }),
                new("Description", "คำอธิบาย", "string", false, null, null),
            }, new List<Dictionary<string, string>>
            {
                new() { ["AccountCode"] = "1110", ["AccountName"] = "เงินสด", ["AccountNameEn"] = "Cash", ["AccountType"] = "Asset" }
            }),

            "banktransactions" => new ImportTemplateResponse("banktransactions", new List<ImportField>
            {
                new("BankAccountId", "รหัสบัญชีธนาคาร", "guid", true, null, null),
                new("TransactionDate", "วันที่", "date", true, "yyyy-MM-dd", null),
                new("TransactionType", "ประเภท", "enum", true, null, new List<string> { "Deposit", "Withdrawal", "Transfer", "Fee", "Interest" }),
                new("Amount", "จำนวนเงิน", "decimal", true, null, null),
                new("Description", "คำอธิบาย", "string", false, null, null),
                new("Reference", "อ้างอิง", "string", false, null, null),
            }, new List<Dictionary<string, string>>
            {
                new() { ["TransactionDate"] = "2026-01-15", ["TransactionType"] = "Deposit", ["Amount"] = "50000.00", ["Description"] = "รับชำระเงิน" }
            }),

            _ => throw new InvalidOperationException($"ไม่รองรับ template สำหรับ {entityType}")
        };

        return Task.FromResult(template);
    }

    public async Task<ImportResult> ValidateImportAsync(Guid companyId, ImportRequest request)
    {
        var errors = new List<ImportError>();
        var validCount = 0;

        for (int i = 0; i < request.Data.Count; i++)
        {
            var row = request.Data[i];
            var rowErrors = ValidateRow(request.EntityType, row, i + 1);
            if (rowErrors.Any())
                errors.AddRange(rowErrors);
            else
                validCount++;
        }

        return await Task.FromResult(new ImportResult(request.EntityType, request.Data.Count, validCount,
            errors.Count, 0, errors, DateTime.UtcNow));
    }

    public async Task<ExportResult> ExportAsync(Guid companyId, ExportRequest request)
    {
        var data = request.EntityType.ToLower() switch
        {
            "contacts" => await ExportContactsAsync(companyId, request),
            "products" => await ExportProductsAsync(companyId, request),
            "chartofaccounts" => await ExportAccountsAsync(companyId, request),
            "journalentries" => await ExportJournalEntriesAsync(companyId, request),
            "documents" => await ExportDocumentsAsync(companyId, request),
            _ => throw new InvalidOperationException($"ไม่รองรับการส่งออก {request.EntityType}")
        };

        var csv = BuildCsv(data);
        var fileName = $"{request.EntityType}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv";

        return new ExportResult(request.EntityType, "csv", data.Count,
            fileName, "text/csv", Encoding.UTF8.GetBytes(csv));
    }

    public Task<List<string>> GetExportableEntitiesAsync()
    {
        return Task.FromResult(new List<string>
        {
            "contacts", "products", "chartofaccounts", "journalentries", "documents"
        });
    }

    // ===== Import Helpers =====

    private async Task ImportContactAsync(Guid companyId, Dictionary<string, string> row)
    {
        var contact = new Contact
        {
            CompanyId = companyId,
            Name = row.GetValueOrDefault("Name") ?? throw new InvalidOperationException("Name is required"),
            TaxId = row.GetValueOrDefault("TaxId"),
            IsCustomer = bool.TryParse(row.GetValueOrDefault("IsCustomer"), out var isCust) && isCust,
            IsSupplier = bool.TryParse(row.GetValueOrDefault("IsSupplier"), out var isSup) && isSup,
            Email = row.GetValueOrDefault("Email"),
            Phone = row.GetValueOrDefault("Phone"),
            Address = row.GetValueOrDefault("Address"),
            ContactPerson = row.GetValueOrDefault("ContactPerson")
        };
        _db.Contacts.Add(contact);
    }

    private async Task ImportProductAsync(Guid companyId, Dictionary<string, string> row)
    {
        var code = row.GetValueOrDefault("Code") ?? throw new InvalidOperationException("Code is required");
        if (await _db.Products.AnyAsync(p => p.CompanyId == companyId && p.Code == code))
            throw new InvalidOperationException($"รหัสสินค้า {code} ซ้ำ");

        var product = new Product
        {
            CompanyId = companyId,
            Code = code,
            Name = row.GetValueOrDefault("Name") ?? throw new InvalidOperationException("Name is required"),
            Unit = row.GetValueOrDefault("Unit") ?? "ชิ้น",
            SellingPrice = decimal.TryParse(row.GetValueOrDefault("SellingPrice"), out var sp) ? sp : 0,
            CostPrice = decimal.TryParse(row.GetValueOrDefault("CostPrice"), out var cp) ? cp : 0,
            VatRate = decimal.TryParse(row.GetValueOrDefault("VatRate"), out var vr) ? vr : 7,
            Category = row.GetValueOrDefault("Category"),
            ProductType = Enum.TryParse<ProductType>(row.GetValueOrDefault("ProductType"), true, out var pt) ? pt : ProductType.Product
        };
        _db.Products.Add(product);
    }

    private async Task ImportAccountAsync(Guid companyId, Dictionary<string, string> row)
    {
        var code = row.GetValueOrDefault("AccountCode") ?? throw new InvalidOperationException("AccountCode is required");
        if (await _db.ChartOfAccounts.AnyAsync(a => a.CompanyId == companyId && a.AccountCode == code))
            throw new InvalidOperationException($"รหัสบัญชี {code} ซ้ำ");

        if (!Enum.TryParse<AccountType>(row.GetValueOrDefault("AccountType"), true, out var acctType))
            throw new InvalidOperationException("AccountType ไม่ถูกต้อง");

        var account = new ChartOfAccount
        {
            CompanyId = companyId,
            AccountCode = code,
            AccountName = row.GetValueOrDefault("AccountName") ?? throw new InvalidOperationException("AccountName is required"),
            AccountNameEn = row.GetValueOrDefault("AccountNameEn"),
            AccountType = acctType,
            Description = row.GetValueOrDefault("Description"),
            Level = code.Length <= 4 ? 1 : 2
        };
        _db.ChartOfAccounts.Add(account);
    }

    private async Task ImportBankTransactionAsync(Guid companyId, Dictionary<string, string> row)
    {
        if (!Guid.TryParse(row.GetValueOrDefault("BankAccountId"), out var bankAccountId))
            throw new InvalidOperationException("BankAccountId ไม่ถูกต้อง");

        var account = await _db.BankAccounts.FirstOrDefaultAsync(a => a.Id == bankAccountId && a.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบบัญชีธนาคาร");

        if (!Enum.TryParse<BankTransactionType>(row.GetValueOrDefault("TransactionType"), true, out var txnType))
            throw new InvalidOperationException("TransactionType ไม่ถูกต้อง");

        var amount = decimal.TryParse(row.GetValueOrDefault("Amount"), out var amt) ? amt : throw new InvalidOperationException("Amount ไม่ถูกต้อง");
        var balanceChange = txnType == BankTransactionType.Withdrawal || txnType == BankTransactionType.Fee ? -Math.Abs(amount) : Math.Abs(amount);
        account.CurrentBalance += balanceChange;

        _db.BankTransactions.Add(new BankTransaction
        {
            CompanyId = companyId,
            BankAccountId = bankAccountId,
            TransactionDate = DateTime.TryParse(row.GetValueOrDefault("TransactionDate"), out var dt) ? dt : DateTime.UtcNow,
            TransactionType = txnType,
            Amount = amount,
            BalanceAfter = account.CurrentBalance,
            Description = row.GetValueOrDefault("Description"),
            Reference = row.GetValueOrDefault("Reference")
        });
    }

    // ===== Export Helpers =====

    private async Task<List<Dictionary<string, string>>> ExportContactsAsync(Guid companyId, ExportRequest request)
    {
        var contacts = await _db.Contacts.Where(c => c.CompanyId == companyId).OrderBy(c => c.Name).ToListAsync();
        return contacts.Select(c => new Dictionary<string, string>
        {
            ["Name"] = c.Name, ["TaxId"] = c.TaxId ?? "", ["IsCustomer"] = c.IsCustomer.ToString(),
            ["IsSupplier"] = c.IsSupplier.ToString(), ["Email"] = c.Email ?? "", ["Phone"] = c.Phone ?? "",
            ["Address"] = c.Address ?? "", ["ContactPerson"] = c.ContactPerson ?? ""
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportProductsAsync(Guid companyId, ExportRequest request)
    {
        var products = await _db.Products.Where(p => p.CompanyId == companyId).OrderBy(p => p.Code).ToListAsync();
        return products.Select(p => new Dictionary<string, string>
        {
            ["Code"] = p.Code, ["Name"] = p.Name, ["ProductType"] = p.ProductType.ToString(),
            ["Unit"] = p.Unit, ["SellingPrice"] = p.SellingPrice.ToString("F2"),
            ["CostPrice"] = p.CostPrice.ToString("F2"), ["VatRate"] = p.VatRate.ToString("F2"),
            ["Category"] = p.Category ?? "", ["CurrentStock"] = p.CurrentStock.ToString("F4")
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportAccountsAsync(Guid companyId, ExportRequest request)
    {
        var accounts = await _db.ChartOfAccounts.Where(a => a.CompanyId == companyId).OrderBy(a => a.AccountCode).ToListAsync();
        return accounts.Select(a => new Dictionary<string, string>
        {
            ["AccountCode"] = a.AccountCode, ["AccountName"] = a.AccountName,
            ["AccountNameEn"] = a.AccountNameEn ?? "", ["AccountType"] = a.AccountType.ToString(),
            ["Level"] = a.Level.ToString(), ["IsActive"] = a.IsActive.ToString()
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportJournalEntriesAsync(Guid companyId, ExportRequest request)
    {
        var query = _db.JournalEntries
            .Include(j => j.Lines).ThenInclude(l => l.Account)
            .Where(j => j.CompanyId == companyId);

        if (request.FromDate.HasValue) query = query.Where(j => j.EntryDate >= request.FromDate);
        if (request.ToDate.HasValue) query = query.Where(j => j.EntryDate <= request.ToDate);

        var entries = await query.OrderBy(j => j.EntryDate).ToListAsync();
        var rows = new List<Dictionary<string, string>>();
        foreach (var entry in entries)
        {
            foreach (var line in entry.Lines.OrderBy(l => l.LineOrder))
            {
                rows.Add(new Dictionary<string, string>
                {
                    ["EntryNumber"] = entry.EntryNumber, ["EntryDate"] = entry.EntryDate.ToString("yyyy-MM-dd"),
                    ["Status"] = entry.Status.ToString(), ["Description"] = entry.Description ?? "",
                    ["AccountCode"] = line.Account.AccountCode, ["AccountName"] = line.Account.AccountName,
                    ["Debit"] = line.DebitAmount.ToString("F2"), ["Credit"] = line.CreditAmount.ToString("F2"),
                    ["LineDescription"] = line.Description ?? ""
                });
            }
        }
        return rows;
    }

    private async Task<List<Dictionary<string, string>>> ExportDocumentsAsync(Guid companyId, ExportRequest request)
    {
        var query = _db.Documents.Include(d => d.Contact).Where(d => d.CompanyId == companyId);
        if (request.FromDate.HasValue) query = query.Where(d => d.DocumentDate >= request.FromDate);
        if (request.ToDate.HasValue) query = query.Where(d => d.DocumentDate <= request.ToDate);

        var docs = await query.OrderBy(d => d.DocumentDate).ToListAsync();
        return docs.Select(d => new Dictionary<string, string>
        {
            ["DocumentNumber"] = d.DocumentNumber, ["DocumentType"] = d.DocumentType.ToString(),
            ["DocumentDate"] = d.DocumentDate.ToString("yyyy-MM-dd"), ["Status"] = d.Status.ToString(),
            ["ContactName"] = d.Contact.Name, ["SubTotal"] = d.SubTotal.ToString("F2"),
            ["VatAmount"] = d.VatAmount.ToString("F2"), ["TotalAmount"] = d.TotalAmount.ToString("F2"),
            ["PaidAmount"] = d.PaidAmount.ToString("F2"), ["BalanceDue"] = d.BalanceDue.ToString("F2")
        }).ToList();
    }

    // ===== Validation =====

    private static List<ImportError> ValidateRow(string entityType, Dictionary<string, string> row, int rowNumber)
    {
        var errors = new List<ImportError>();

        switch (entityType.ToLower())
        {
            case "contacts":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Name")))
                    errors.Add(new ImportError(rowNumber, "Name", "", "จำเป็นต้องระบุชื่อ"));
                break;
            case "products":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Code")))
                    errors.Add(new ImportError(rowNumber, "Code", "", "จำเป็นต้องระบุรหัสสินค้า"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Name")))
                    errors.Add(new ImportError(rowNumber, "Name", "", "จำเป็นต้องระบุชื่อสินค้า"));
                break;
            case "chartofaccounts":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("AccountCode")))
                    errors.Add(new ImportError(rowNumber, "AccountCode", "", "จำเป็นต้องระบุรหัสบัญชี"));
                if (!Enum.TryParse<AccountType>(row.GetValueOrDefault("AccountType"), true, out _))
                    errors.Add(new ImportError(rowNumber, "AccountType", row.GetValueOrDefault("AccountType") ?? "", "ประเภทบัญชีไม่ถูกต้อง"));
                break;
        }

        return errors;
    }

    private static string BuildCsv(List<Dictionary<string, string>> data)
    {
        if (data.Count == 0) return "";

        var sb = new StringBuilder();
        var headers = data[0].Keys.ToList();
        sb.AppendLine(string.Join(",", headers.Select(EscapeCsv)));

        foreach (var row in data)
        {
            sb.AppendLine(string.Join(",", headers.Select(h => EscapeCsv(row.GetValueOrDefault(h) ?? ""))));
        }

        return sb.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }
}
