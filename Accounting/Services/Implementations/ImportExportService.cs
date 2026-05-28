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
    private readonly IErrorLogService _errorLogService;

    public ImportExportService(AccountingDbContext db, IErrorLogService errorLogService)
    {
        _db = db;
        _errorLogService = errorLogService;
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
                    case "chart-of-accounts":
                        await ImportAccountAsync(companyId, row);
                        break;
                    case "banktransactions":
                        await ImportBankTransactionAsync(companyId, row);
                        break;
                    case "journalentries":
                    case "journal-entries":
                        await ImportJournalEntryAsync(companyId, row, performedBy);
                        break;
                    case "stock-opening":
                        await ImportStockOpeningAsync(companyId, row, performedBy);
                        break;
                    case "opening-ar":
                        await ImportOpeningSubledgerAsync(companyId, row, performedBy, isReceivable: true);
                        break;
                    case "opening-ap":
                        await ImportOpeningSubledgerAsync(companyId, row, performedBy, isReceivable: false);
                        break;
                    case "stock-adjustments":
                        await ImportStockAdjustmentAsync(companyId, row, performedBy);
                        break;
                    case "fixed-assets":
                        await ImportFixedAssetAsync(companyId, row, performedBy);
                        break;
                    case "payments":
                        await ImportPaymentAsync(companyId, row, performedBy);
                        break;
                    case "projects":
                        await ImportProjectAsync(companyId, row, performedBy);
                        break;
                    case "employees":
                        await ImportEmployeeAsync(companyId, row, performedBy);
                        break;
                    case "budgets":
                        await ImportBudgetAsync(companyId, row, performedBy);
                        break;
                    case "documents":
                        await ImportDocumentAsync(companyId, row, performedBy);
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
                await _errorLogService.LogErrorAsync(ex, $"ImportExport.ImportRow/{i + 1}");
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

            "chartofaccounts" or "chart-of-accounts" => new ImportTemplateResponse("chart-of-accounts", new List<ImportField>
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

            "journalentries" or "journal-entries" => new ImportTemplateResponse("journal-entries", new List<ImportField>
            {
                new("Date", "วันที่", "date", true, "yyyy-MM-dd", null),
                new("Description", "คำอธิบาย", "string", true, null, null),
                new("AccountCode", "รหัสบัญชี", "string", true, null, null),
                new("DebitAmount", "เดบิต", "decimal", false, null, null),
                new("CreditAmount", "เครดิต", "decimal", false, null, null),
                new("Reference", "อ้างอิง", "string", false, null, null),
                new("ContactName", "ผู้ติดต่อ", "string", false, null, null),
            }, new List<Dictionary<string, string>>
            {
                new() { ["Date"] = "2026-01-15", ["Description"] = "ค่าเช่าสำนักงาน", ["AccountCode"] = "5210", ["DebitAmount"] = "15000.00", ["CreditAmount"] = "" },
                new() { ["Date"] = "2026-01-15", ["Description"] = "ค่าเช่าสำนักงาน", ["AccountCode"] = "1120", ["DebitAmount"] = "", ["CreditAmount"] = "15000.00" }
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

            "stock-opening" => new ImportTemplateResponse("stock-opening", GetTemplateFields("stock-opening")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["ProductCode"] = "P001", ["Quantity"] = "100", ["UnitCost"] = "60.00", ["OpeningDate"] = "2026-01-01", ["Notes"] = "ยกมาจากระบบเดิม" }
                }),

            "opening-ar" => new ImportTemplateResponse("opening-ar", GetTemplateFields("opening-ar")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["ContactName"] = "บริษัท ลูกค้า จำกัด", ["ContactTaxId"] = "0123456789012", ["InvoiceNumber"] = "INV-2025-001", ["InvoiceDate"] = "2025-11-20", ["DueDate"] = "2025-12-20", ["Amount"] = "53500.00", ["Description"] = "ลูกหนี้ยกมา" }
                }),

            "opening-ap" => new ImportTemplateResponse("opening-ap", GetTemplateFields("opening-ap")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["ContactName"] = "บริษัท ผู้ขาย จำกัด", ["ContactTaxId"] = "0987654321098", ["InvoiceNumber"] = "PINV-2025-044", ["InvoiceDate"] = "2025-11-25", ["DueDate"] = "2025-12-25", ["Amount"] = "21400.00", ["Description"] = "เจ้าหนี้ยกมา" }
                }),

            "stock-adjustments" => new ImportTemplateResponse("stock-adjustments", GetTemplateFields("stock-adjustments")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["ProductCode"] = "P001", ["AdjustmentDate"] = "2026-01-15", ["MovementType"] = "ADJUST", ["Quantity"] = "-2", ["Notes"] = "ของเสียหาย" }
                }),

            "fixed-assets" => new ImportTemplateResponse("fixed-assets", GetTemplateFields("fixed-assets")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["AssetCode"] = "FA-001", ["Name"] = "คอมพิวเตอร์โน้ตบุ๊ก", ["Category"] = "เครื่องใช้สำนักงาน",
                        ["PurchaseDate"] = "2024-03-15", ["PurchaseCost"] = "35000.00", ["SalvageValue"] = "0",
                        ["UsefulLifeMonths"] = "60", ["DepreciationMethod"] = "StraightLine", ["AccumulatedDepreciation"] = "0" }
                }),

            "payments" => new ImportTemplateResponse("payments", GetTemplateFields("payments")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["DocumentNumber"] = "INV-202601-0001", ["PaymentDate"] = "2026-01-20",
                        ["Amount"] = "10700.00", ["PaymentMethod"] = "BankTransfer", ["Reference"] = "TXN-12345" }
                }),

            "projects" => new ImportTemplateResponse("projects", GetTemplateFields("projects")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["Code"] = "PRJ-001", ["Name"] = "โปรเจกต์ตัวอย่าง", ["StartDate"] = "2026-01-01",
                        ["BudgetAmount"] = "500000.00", ["Status"] = "Active", ["BillingMethod"] = "FixedPrice" }
                }),

            "employees" => new ImportTemplateResponse("employees", GetTemplateFields("employees")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["EmployeeCode"] = "EMP-001", ["TitleTh"] = "นาย", ["FirstNameTh"] = "สมชาย",
                        ["LastNameTh"] = "ใจดี", ["StartDate"] = "2024-01-15", ["BaseSalary"] = "25000.00",
                        ["SalaryType"] = "Monthly", ["Department"] = "ฝ่ายขาย" }
                }),

            "budgets" => new ImportTemplateResponse("budgets", GetTemplateFields("budgets")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["BudgetName"] = "งบ 2026", ["FiscalYear"] = "2026", ["AccountCode"] = "5210",
                        ["Month1"] = "10000", ["Month2"] = "10000", ["Month3"] = "10000",
                        ["Month4"] = "12000", ["Month5"] = "12000", ["Month6"] = "12000",
                        ["Month7"] = "12000", ["Month8"] = "12000", ["Month9"] = "12000",
                        ["Month10"] = "15000", ["Month11"] = "15000", ["Month12"] = "15000" }
                }),

            "documents" => new ImportTemplateResponse("documents", GetTemplateFields("documents")!,
                new List<Dictionary<string, string>>
                {
                    new() { ["DocumentNumber"] = "INV-202601-0001", ["DocumentType"] = "Invoice",
                        ["DocumentDate"] = "2026-01-15", ["DueDate"] = "2026-02-14",
                        ["ContactName"] = "บริษัท ลูกค้า จำกัด", ["ContactTaxId"] = "0123456789012",
                        ["LineDescription"] = "บริการที่ปรึกษา", ["LineQuantity"] = "1",
                        ["LineUnitPrice"] = "10000.00", ["LineVatRate"] = "7", ["LineWhtRate"] = "3" },
                    new() { ["DocumentNumber"] = "INV-202601-0001", ["DocumentType"] = "Invoice",
                        ["DocumentDate"] = "2026-01-15", ["DueDate"] = "2026-02-14",
                        ["ContactName"] = "บริษัท ลูกค้า จำกัด", ["ContactTaxId"] = "0123456789012",
                        ["LineDescription"] = "ค่าเดินทาง", ["LineQuantity"] = "1",
                        ["LineUnitPrice"] = "500.00", ["LineVatRate"] = "7" }
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
            "chartofaccounts" or "chart-of-accounts" => await ExportAccountsAsync(companyId, request),
            "journalentries" or "journal-entries" => await ExportJournalEntriesAsync(companyId, request),
            "documents" => await ExportDocumentsAsync(companyId, request),
            "banktransactions" => await ExportBankTransactionsAsync(companyId, request),
            "stock-movements" => await ExportStockMovementsAsync(companyId, request),
            "stock-balances" => await ExportStockBalancesAsync(companyId, request),
            "fixed-assets" => await ExportFixedAssetsAsync(companyId, request),
            "payments" => await ExportPaymentsAsync(companyId, request),
            "projects" => await ExportProjectsAsync(companyId, request),
            "employees" => await ExportEmployeesAsync(companyId, request),
            "budgets" => await ExportBudgetsAsync(companyId, request),
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
            "contacts", "products", "chartofaccounts", "journalentries", "documents",
            "banktransactions", "stock-movements", "stock-balances", "fixed-assets",
            "payments", "projects", "employees", "budgets"
        });
    }

    // ===== Import Helpers =====

    private async Task ImportContactAsync(Guid companyId, Dictionary<string, string> row)
    {
        var name = row.GetValueOrDefault("Name") ?? throw new InvalidOperationException("Name is required");
        var taxId = row.GetValueOrDefault("TaxId");
        var email = row.GetValueOrDefault("Email");

        // Idempotent on re-import: match by TaxId (most reliable for
        // businesses), then by Email when TaxId is blank. Re-import
        // updates the matched row in place instead of creating a
        // duplicate. This makes the typical migration workflow
        // "fix CSV → re-import" safe to repeat without dedup cleanup.
        Contact? existing = null;
        if (!string.IsNullOrWhiteSpace(taxId))
        {
            existing = await _db.Contacts.FirstOrDefaultAsync(c =>
                c.CompanyId == companyId && !c.IsDeleted && c.TaxId == taxId);
        }
        if (existing == null && !string.IsNullOrWhiteSpace(email))
        {
            existing = await _db.Contacts.FirstOrDefaultAsync(c =>
                c.CompanyId == companyId && !c.IsDeleted && c.Email == email);
        }

        var isCustomer = bool.TryParse(row.GetValueOrDefault("IsCustomer"), out var isCust) && isCust;
        var isSupplier = bool.TryParse(row.GetValueOrDefault("IsSupplier"), out var isSup) && isSup;

        if (existing != null)
        {
            existing.Name = name;
            if (!string.IsNullOrWhiteSpace(taxId)) existing.TaxId = taxId;
            if (!string.IsNullOrWhiteSpace(email)) existing.Email = email;
            // OR-merge customer/supplier flags so a row imported as
            // both supplier + customer keeps both flags set on the
            // matched contact (typical Thai SME has same vendor as
            // both for service exchanges).
            existing.IsCustomer = existing.IsCustomer || isCustomer;
            existing.IsSupplier = existing.IsSupplier || isSupplier;
            existing.Phone = row.GetValueOrDefault("Phone") ?? existing.Phone;
            existing.Address = row.GetValueOrDefault("Address") ?? existing.Address;
            existing.ContactPerson = row.GetValueOrDefault("ContactPerson") ?? existing.ContactPerson;
            existing.UpdatedAt = DateTime.UtcNow;
            return;
        }

        var contact = new Contact
        {
            CompanyId = companyId,
            Name = name,
            TaxId = taxId,
            IsCustomer = isCustomer,
            IsSupplier = isSupplier,
            Email = email,
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

    private async Task ImportJournalEntryAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var date = DateTime.TryParse(row.GetValueOrDefault("Date"), out var dt) ? dt : DateTime.UtcNow;
        var accountCode = row.GetValueOrDefault("AccountCode") ?? throw new InvalidOperationException("AccountCode is required");
        var debitAmount = decimal.TryParse(row.GetValueOrDefault("DebitAmount"), out var da) ? da : 0;
        var creditAmount = decimal.TryParse(row.GetValueOrDefault("CreditAmount"), out var ca) ? ca : 0;
        var description = row.GetValueOrDefault("Description") ?? "";
        var reference = row.GetValueOrDefault("Reference");

        var account = await _db.ChartOfAccounts
            .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == accountCode)
            ?? throw new KeyNotFoundException($"ไม่พบบัญชีรหัส {accountCode}");

        Guid? contactId = null;
        var contactName = row.GetValueOrDefault("ContactName");
        if (!string.IsNullOrWhiteSpace(contactName))
        {
            var contact = await _db.Contacts
                .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Name.Contains(contactName));
            contactId = contact?.Id;
        }

        var existing = await _db.JournalEntries
            .Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.CompanyId == companyId
                && j.EntryDate == date
                && j.Description == description
                && j.Reference == reference);

        if (existing != null)
        {
            existing.Lines.Add(new JournalEntryLine
            {
                AccountId = account.Id,
                DebitAmount = debitAmount,
                CreditAmount = creditAmount,
                Description = description
            });
        }
        else
        {
            var series = await _db.NumberSeries
                .FirstOrDefaultAsync(n => n.CompanyId == companyId && n.IsActive && n.Prefix == "JV");
            var nextNum = (series?.CurrentNumber ?? 0) + 1;
            if (series != null) series.CurrentNumber = nextNum;

            var entry = new JournalEntry
            {
                CompanyId = companyId,
                EntryNumber = $"JV-{nextNum:D6}",
                EntryDate = date,
                Description = description,
                Reference = reference,
                Note = contactId.HasValue ? $"ContactId:{contactId}" : null,
                Status = JournalEntryStatus.Draft,
                CreatedBy = performedBy,
                Lines = new List<JournalEntryLine>
                {
                    new()
                    {
                        AccountId = account.Id,
                        DebitAmount = debitAmount,
                        CreditAmount = creditAmount,
                        Description = description
                    }
                }
            };
            _db.JournalEntries.Add(entry);
        }
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

    // ===== Stock import =====

    private async Task ImportStockOpeningAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var code = row.GetValueOrDefault("ProductCode") ?? throw new InvalidOperationException("ProductCode is required");
        var product = await _db.Products.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Code == code)
            ?? throw new KeyNotFoundException($"ไม่พบสินค้ารหัส {code}");

        var qty = decimal.TryParse(row.GetValueOrDefault("Quantity"), out var q) ? q
            : throw new InvalidOperationException("Quantity ไม่ถูกต้อง");
        var unitCost = decimal.TryParse(row.GetValueOrDefault("UnitCost"), out var uc) ? uc : product.CostPrice;
        var openingDate = DateTime.TryParse(row.GetValueOrDefault("OpeningDate"), out var od) ? od : DateTime.UtcNow;

        // Replace any previously imported opening balance so re-running the
        // file twice doesn't double-count. The MovementType OPENING is the
        // unique signal we use to identify migration-time entries.
        var existingOpening = await _db.StockMovements
            .Where(m => m.CompanyId == companyId && m.ProductId == product.Id && m.MovementType == "OPENING")
            .ToListAsync();
        if (existingOpening.Count > 0)
        {
            foreach (var ex in existingOpening) product.CurrentStock -= ex.Quantity;
            _db.StockMovements.RemoveRange(existingOpening);
        }

        product.CurrentStock += qty;
        product.CostPrice = unitCost;

        _db.StockMovements.Add(new StockMovement
        {
            CompanyId = companyId,
            ProductId = product.Id,
            MovementDate = openingDate,
            MovementType = "OPENING",
            Quantity = qty,
            UnitCost = unitCost,
            BalanceAfter = product.CurrentStock,
            Notes = row.GetValueOrDefault("Notes") ?? "สต็อกยกมา (Import)",
            CreatedBy = performedBy
        });
    }

    /// <summary>
    /// Import one opening AR (receivable) or AP (payable) subledger item.
    /// Creates an already-Approved Document flagged IsOpeningBalance so the
    /// item shows up in AR/AP aging + the contact ledger — but it is NEVER
    /// auto-posted to the GL: opening docs are created directly (not via
    /// ApproveDocumentAsync) so no journal entry is generated. The control
    /// account total is carried by the TrialBalance migration's GL opening
    /// balance; posting here too would double-count. Idempotent by a
    /// deterministic DocumentNumber ("OB-AR:"/"OB-AP:" + InvoiceNumber).
    /// </summary>
    private async Task ImportOpeningSubledgerAsync(
        Guid companyId, Dictionary<string, string> row, string performedBy, bool isReceivable)
    {
        var contactName = (row.GetValueOrDefault("ContactName") ?? "").Trim();
        var contactTaxId = row.GetValueOrDefault("ContactTaxId")?.Trim();
        if (string.IsNullOrWhiteSpace(contactName) && string.IsNullOrWhiteSpace(contactTaxId))
            throw new InvalidOperationException("ต้องระบุ ContactName หรือ ContactTaxId");

        var invoiceNumber = (row.GetValueOrDefault("InvoiceNumber") ?? "").Trim();
        if (string.IsNullOrWhiteSpace(invoiceNumber))
            throw new InvalidOperationException("InvoiceNumber is required");

        if (!decimal.TryParse(row.GetValueOrDefault("Amount"), out var amount) || amount <= 0)
            throw new InvalidOperationException("Amount ต้องเป็นตัวเลขมากกว่า 0");

        var invoiceDate = DateTime.TryParse(row.GetValueOrDefault("InvoiceDate"), out var idt)
            ? idt : throw new InvalidOperationException("InvoiceDate ไม่ถูกต้อง");
        DateTime? dueDate = DateTime.TryParse(row.GetValueOrDefault("DueDate"), out var dd) ? dd : null;

        // Resolve or create the contact (migration convenience). The import
        // runs many rows then SaveChanges once at the end — so also scan the
        // change tracker, else two rows for the same new contact would each
        // insert a duplicate.
        Contact? contact = null;
        if (!string.IsNullOrWhiteSpace(contactTaxId))
            contact = await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.TaxId == contactTaxId);
        if (contact == null && !string.IsNullOrWhiteSpace(contactName))
            contact = await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Name == contactName);
        contact ??= _db.ChangeTracker.Entries<Contact>().Select(e => e.Entity)
            .FirstOrDefault(c => c.CompanyId == companyId && (
                (!string.IsNullOrWhiteSpace(contactTaxId) && c.TaxId == contactTaxId) ||
                (!string.IsNullOrWhiteSpace(contactName) && c.Name == contactName)));
        if (contact == null)
        {
            contact = new Contact
            {
                CompanyId = companyId,
                Name = string.IsNullOrWhiteSpace(contactName) ? contactTaxId! : contactName,
                TaxId = string.IsNullOrWhiteSpace(contactTaxId) ? null : contactTaxId,
                IsCustomer = isReceivable,
                IsSupplier = !isReceivable,
                IsActive = true,
                CreatedBy = performedBy,
            };
            _db.Contacts.Add(contact);
        }
        else if (isReceivable) { contact.IsCustomer = true; }
        else { contact.IsSupplier = true; }

        var docNumber = (isReceivable ? "OB-AR:" : "OB-AP:") + invoiceNumber;
        var docType = isReceivable ? DocumentType.Invoice : DocumentType.PurchaseInvoice;
        var notes = row.GetValueOrDefault("Description")
            ?? $"ยอดยกมา (Opening {(isReceivable ? "ลูกหนี้" : "เจ้าหนี้")}) — Import";

        var doc = _db.ChangeTracker.Entries<Document>().Select(e => e.Entity)
            .FirstOrDefault(d => d.CompanyId == companyId && d.DocumentNumber == docNumber)
            ?? await _db.Documents.Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.CompanyId == companyId && d.DocumentNumber == docNumber);

        if (doc == null)
        {
            doc = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNumber,
                DocumentType = docType,
                DocumentDate = invoiceDate,
                DueDate = dueDate,
                ContactId = contact.Id,
                Contact = contact,
                Status = DocumentStatus.Approved,
                IsOpeningBalance = true,
                Reference = invoiceNumber,
                Notes = notes,
                CreatedBy = performedBy,
            };
            _db.Documents.Add(doc);
        }
        else
        {
            // Re-import — refresh in place so re-running the file is safe.
            doc.DocumentDate = invoiceDate;
            doc.DueDate = dueDate;
            doc.ContactId = contact.Id;
            doc.IsOpeningBalance = true;
            doc.Notes = notes;
            _db.DocumentLines.RemoveRange(doc.Lines);
            doc.Lines.Clear();
        }

        // Opening AR/AP is a pure receivable/payable carry-over — single line,
        // no new VAT/WHT (that belonged to the original invoice's period).
        doc.Lines.Add(new DocumentLine
        {
            LineOrder = 1,
            Description = notes,
            Quantity = 1,
            Unit = "งวด",
            UnitPrice = amount,
            Amount = amount,
            VatRate = 0,
            VatAmount = 0,
        });
        doc.SubTotal = amount;
        doc.VatAmount = 0;
        doc.WithholdingTaxAmount = 0;
        doc.TotalAmount = amount;
        doc.PaidAmount = 0;
        doc.BalanceDue = amount;
    }

    private async Task ImportStockAdjustmentAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var code = row.GetValueOrDefault("ProductCode") ?? throw new InvalidOperationException("ProductCode is required");
        var product = await _db.Products.FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Code == code)
            ?? throw new KeyNotFoundException($"ไม่พบสินค้ารหัส {code}");

        var rawType = (row.GetValueOrDefault("MovementType") ?? "ADJUST").ToUpperInvariant();
        if (rawType != "IN" && rawType != "OUT" && rawType != "ADJUST")
            throw new InvalidOperationException($"MovementType ไม่ถูกต้อง: {rawType}");

        var qty = decimal.TryParse(row.GetValueOrDefault("Quantity"), out var q) ? q
            : throw new InvalidOperationException("Quantity ไม่ถูกต้อง");
        // Normalize sign — IN is always positive, OUT always negative; ADJUST
        // honours the sign the user gave (allowing both +/− variance).
        var signed = rawType switch
        {
            "IN" => Math.Abs(qty),
            "OUT" => -Math.Abs(qty),
            _ => qty
        };

        var unitCost = decimal.TryParse(row.GetValueOrDefault("UnitCost"), out var uc) ? uc : product.CostPrice;
        var date = DateTime.TryParse(row.GetValueOrDefault("AdjustmentDate"), out var d) ? d : DateTime.UtcNow;

        product.CurrentStock += signed;

        _db.StockMovements.Add(new StockMovement
        {
            CompanyId = companyId,
            ProductId = product.Id,
            MovementDate = date,
            MovementType = rawType,
            Quantity = signed,
            UnitCost = unitCost,
            BalanceAfter = product.CurrentStock,
            Reference = row.GetValueOrDefault("Reference"),
            Notes = row.GetValueOrDefault("Notes"),
            CreatedBy = performedBy
        });
    }

    // ===== Fixed Asset import =====

    private async Task ImportFixedAssetAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var code = row.GetValueOrDefault("AssetCode") ?? throw new InvalidOperationException("AssetCode is required");
        if (await _db.FixedAssets.AnyAsync(a => a.CompanyId == companyId && a.AssetCode == code))
            throw new InvalidOperationException($"รหัสสินทรัพย์ {code} ซ้ำ");

        var purchaseDate = DateTime.TryParse(row.GetValueOrDefault("PurchaseDate"), out var pd) ? pd
            : throw new InvalidOperationException("PurchaseDate ไม่ถูกต้อง");
        var purchaseCost = decimal.TryParse(row.GetValueOrDefault("PurchaseCost"), out var pc) ? pc
            : throw new InvalidOperationException("PurchaseCost ไม่ถูกต้อง");
        var salvage = decimal.TryParse(row.GetValueOrDefault("SalvageValue"), out var sv) ? sv : 0m;
        var life = int.TryParse(row.GetValueOrDefault("UsefulLifeMonths"), out var lm) ? lm
            : throw new InvalidOperationException("UsefulLifeMonths ไม่ถูกต้อง");
        var accDep = decimal.TryParse(row.GetValueOrDefault("AccumulatedDepreciation"), out var ad) ? ad : 0m;
        var method = Enum.TryParse<DepreciationMethod>(row.GetValueOrDefault("DepreciationMethod"), true, out var dm)
            ? dm : DepreciationMethod.StraightLine;

        _db.FixedAssets.Add(new FixedAsset
        {
            CompanyId = companyId,
            AssetCode = code,
            Name = row.GetValueOrDefault("Name") ?? throw new InvalidOperationException("Name is required"),
            Category = row.GetValueOrDefault("Category"),
            Location = row.GetValueOrDefault("Location"),
            SerialNumber = row.GetValueOrDefault("SerialNumber"),
            PurchaseDate = purchaseDate,
            PurchaseCost = purchaseCost,
            SalvageValue = salvage,
            UsefulLifeMonths = life,
            DepreciationMethod = method,
            AccumulatedDepreciation = accDep,
            NetBookValue = purchaseCost - accDep,
            Status = AssetStatus.Active,
            CreatedBy = performedBy
        });
    }

    // ===== Stock + Asset + Bank exports =====

    private async Task<List<Dictionary<string, string>>> ExportStockMovementsAsync(Guid companyId, ExportRequest request)
    {
        var query = _db.StockMovements
            .Include(m => m.Product)
            .Where(m => m.CompanyId == companyId);
        if (request.FromDate.HasValue) query = query.Where(m => m.MovementDate >= request.FromDate);
        if (request.ToDate.HasValue) query = query.Where(m => m.MovementDate <= request.ToDate);
        var movements = await query.OrderBy(m => m.MovementDate).ToListAsync();

        return movements.Select(m => new Dictionary<string, string>
        {
            ["MovementDate"] = m.MovementDate.ToString("yyyy-MM-dd"),
            ["ProductCode"] = m.Product.Code,
            ["ProductName"] = m.Product.Name,
            ["MovementType"] = m.MovementType,
            ["Quantity"] = m.Quantity.ToString("F4"),
            ["UnitCost"] = m.UnitCost.ToString("F2"),
            ["BalanceAfter"] = m.BalanceAfter.ToString("F4"),
            ["Reference"] = m.Reference ?? "",
            ["Notes"] = m.Notes ?? ""
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportStockBalancesAsync(Guid companyId, ExportRequest request)
    {
        var products = await _db.Products
            .Where(p => p.CompanyId == companyId && p.TrackStock && p.IsActive)
            .OrderBy(p => p.Code)
            .ToListAsync();
        return products.Select(p => new Dictionary<string, string>
        {
            ["Code"] = p.Code,
            ["Name"] = p.Name,
            ["Category"] = p.Category ?? "",
            ["Unit"] = p.Unit,
            ["CurrentStock"] = p.CurrentStock.ToString("F4"),
            ["CostPrice"] = p.CostPrice.ToString("F2"),
            ["StockValue"] = (p.CurrentStock * p.CostPrice).ToString("F2"),
            ["MinimumStock"] = p.MinimumStock.ToString("F4"),
            ["BelowMinimum"] = (p.CurrentStock < p.MinimumStock).ToString()
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportFixedAssetsAsync(Guid companyId, ExportRequest request)
    {
        var assets = await _db.FixedAssets
            .Where(a => a.CompanyId == companyId)
            .OrderBy(a => a.AssetCode)
            .ToListAsync();
        return assets.Select(a => new Dictionary<string, string>
        {
            ["AssetCode"] = a.AssetCode,
            ["Name"] = a.Name,
            ["Category"] = a.Category ?? "",
            ["Location"] = a.Location ?? "",
            ["SerialNumber"] = a.SerialNumber ?? "",
            ["PurchaseDate"] = a.PurchaseDate.ToString("yyyy-MM-dd"),
            ["PurchaseCost"] = a.PurchaseCost.ToString("F2"),
            ["SalvageValue"] = a.SalvageValue.ToString("F2"),
            ["UsefulLifeMonths"] = a.UsefulLifeMonths.ToString(),
            ["DepreciationMethod"] = a.DepreciationMethod.ToString(),
            ["AccumulatedDepreciation"] = a.AccumulatedDepreciation.ToString("F2"),
            ["NetBookValue"] = a.NetBookValue.ToString("F2"),
            ["Status"] = a.Status.ToString()
        }).ToList();
    }

    private async Task<List<Dictionary<string, string>>> ExportBankTransactionsAsync(Guid companyId, ExportRequest request)
    {
        var query = _db.BankTransactions
            .Include(t => t.BankAccount)
            .Where(t => t.CompanyId == companyId);
        if (request.FromDate.HasValue) query = query.Where(t => t.TransactionDate >= request.FromDate);
        if (request.ToDate.HasValue) query = query.Where(t => t.TransactionDate <= request.ToDate);
        var txns = await query.OrderBy(t => t.TransactionDate).ToListAsync();

        return txns.Select(t => new Dictionary<string, string>
        {
            ["TransactionDate"] = t.TransactionDate.ToString("yyyy-MM-dd"),
            ["BankAccount"] = t.BankAccount?.AccountName ?? "",
            ["BankAccountId"] = t.BankAccountId.ToString(),
            ["TransactionType"] = t.TransactionType.ToString(),
            ["Amount"] = t.Amount.ToString("F2"),
            ["BalanceAfter"] = t.BalanceAfter.ToString("F2"),
            ["Description"] = t.Description ?? "",
            ["Reference"] = t.Reference ?? ""
        }).ToList();
    }

    // ===== Payments =====

    private async Task ImportPaymentAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var docNum = row.GetValueOrDefault("DocumentNumber") ?? throw new InvalidOperationException("DocumentNumber is required");
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.DocumentNumber == docNum)
            ?? throw new KeyNotFoundException($"ไม่พบเอกสาร {docNum}");

        var amount = decimal.TryParse(row.GetValueOrDefault("Amount"), out var amt) ? amt
            : throw new InvalidOperationException("Amount ไม่ถูกต้อง");
        var date = DateTime.TryParse(row.GetValueOrDefault("PaymentDate"), out var pd) ? pd : DateTime.UtcNow;
        var method = Enum.TryParse<PaymentMethod>(row.GetValueOrDefault("PaymentMethod"), true, out var pm) ? pm : PaymentMethod.Cash;

        // Reuse the same number-series pattern as the rest of the codebase
        // (PaymentService uses "PAY" prefix per month). We replicate it here
        // rather than reach into PaymentService — single tx, avoids a circular
        // dependency.
        var yearMonth = DateTime.UtcNow.ToString("yyyyMM");
        var paymentPrefix = $"PAY-{yearMonth}-";
        var lastNum = await _db.Payments
            .Where(p => p.CompanyId == companyId && p.PaymentNumber.StartsWith(paymentPrefix))
            .OrderByDescending(p => p.PaymentNumber)
            .Select(p => p.PaymentNumber).FirstOrDefaultAsync();
        var nextSeq = 1;
        if (lastNum != null && int.TryParse(lastNum.Substring(paymentPrefix.Length), out var n)) nextSeq = n + 1;

        doc.PaidAmount += amount;
        doc.BalanceDue = doc.TotalAmount - doc.PaidAmount;
        doc.Status = doc.BalanceDue <= 0 ? DocumentStatus.Paid : DocumentStatus.PartiallyPaid;

        _db.Payments.Add(new Payment
        {
            CompanyId = companyId,
            PaymentNumber = $"{paymentPrefix}{nextSeq:D4}",
            DocumentId = doc.Id,
            PaymentDate = date,
            Amount = amount,
            PaymentMethod = method,
            Reference = row.GetValueOrDefault("Reference"),
            BankAccount = row.GetValueOrDefault("BankAccount"),
            Notes = row.GetValueOrDefault("Notes"),
            CreatedBy = performedBy
        });
    }

    private async Task<List<Dictionary<string, string>>> ExportPaymentsAsync(Guid companyId, ExportRequest request)
    {
        var query = _db.Payments.Include(p => p.Document).Where(p => p.CompanyId == companyId);
        if (request.FromDate.HasValue) query = query.Where(p => p.PaymentDate >= request.FromDate);
        if (request.ToDate.HasValue) query = query.Where(p => p.PaymentDate <= request.ToDate);
        var payments = await query.OrderBy(p => p.PaymentDate).ToListAsync();

        return payments.Select(p => new Dictionary<string, string>
        {
            ["PaymentNumber"] = p.PaymentNumber,
            ["PaymentDate"] = p.PaymentDate.ToString("yyyy-MM-dd"),
            ["DocumentNumber"] = p.Document?.DocumentNumber ?? "",
            ["Amount"] = p.Amount.ToString("F2"),
            ["PaymentMethod"] = p.PaymentMethod.ToString(),
            ["Reference"] = p.Reference ?? "",
            ["BankAccount"] = p.BankAccount ?? "",
            ["Notes"] = p.Notes ?? ""
        }).ToList();
    }

    // ===== Projects =====

    private async Task ImportProjectAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var code = row.GetValueOrDefault("Code") ?? throw new InvalidOperationException("Code is required");
        if (await _db.Projects.AnyAsync(p => p.CompanyId == companyId && p.Code == code))
            throw new InvalidOperationException($"รหัสโปรเจกต์ {code} ซ้ำ");

        _db.Projects.Add(new Project
        {
            CompanyId = companyId,
            Code = code,
            Name = row.GetValueOrDefault("Name") ?? throw new InvalidOperationException("Name is required"),
            NameEn = row.GetValueOrDefault("NameEn"),
            Description = row.GetValueOrDefault("Description"),
            CustomerName = row.GetValueOrDefault("CustomerName"),
            ProjectManagerName = row.GetValueOrDefault("ProjectManagerName"),
            StartDate = DateTime.TryParse(row.GetValueOrDefault("StartDate"), out var sd) ? sd
                : throw new InvalidOperationException("StartDate ไม่ถูกต้อง"),
            EndDate = DateTime.TryParse(row.GetValueOrDefault("EndDate"), out var ed) ? ed : null,
            Status = row.GetValueOrDefault("Status") ?? "Active",
            BudgetAmount = decimal.TryParse(row.GetValueOrDefault("BudgetAmount"), out var ba) ? ba : 0,
            ContractAmount = decimal.TryParse(row.GetValueOrDefault("ContractAmount"), out var ca) ? ca : 0,
            BillingMethod = row.GetValueOrDefault("BillingMethod") ?? "FixedPrice",
            CreatedBy = performedBy
        });
    }

    private async Task<List<Dictionary<string, string>>> ExportProjectsAsync(Guid companyId, ExportRequest request)
    {
        var projects = await _db.Projects.Where(p => p.CompanyId == companyId).OrderBy(p => p.Code).ToListAsync();
        return projects.Select(p => new Dictionary<string, string>
        {
            ["Code"] = p.Code,
            ["Name"] = p.Name,
            ["NameEn"] = p.NameEn ?? "",
            ["CustomerName"] = p.CustomerName ?? "",
            ["ProjectManagerName"] = p.ProjectManagerName ?? "",
            ["StartDate"] = p.StartDate.ToString("yyyy-MM-dd"),
            ["EndDate"] = p.EndDate?.ToString("yyyy-MM-dd") ?? "",
            ["Status"] = p.Status,
            ["BudgetAmount"] = p.BudgetAmount.ToString("F2"),
            ["ContractAmount"] = p.ContractAmount.ToString("F2"),
            ["ActualRevenue"] = p.ActualRevenue.ToString("F2"),
            ["ActualCost"] = p.ActualCost.ToString("F2"),
            ["BilledAmount"] = p.BilledAmount.ToString("F2"),
            ["BillingMethod"] = p.BillingMethod
        }).ToList();
    }

    // ===== Employees =====

    private async Task ImportEmployeeAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var code = row.GetValueOrDefault("EmployeeCode") ?? throw new InvalidOperationException("EmployeeCode is required");
        if (await _db.Employees.AnyAsync(e => e.CompanyId == companyId && e.EmployeeCode == code))
            throw new InvalidOperationException($"รหัสพนักงาน {code} ซ้ำ");

        _db.Employees.Add(new Employee
        {
            CompanyId = companyId,
            EmployeeCode = code,
            TitleTh = row.GetValueOrDefault("TitleTh") ?? "",
            FirstNameTh = row.GetValueOrDefault("FirstNameTh") ?? throw new InvalidOperationException("FirstNameTh is required"),
            LastNameTh = row.GetValueOrDefault("LastNameTh") ?? throw new InvalidOperationException("LastNameTh is required"),
            FirstNameEn = row.GetValueOrDefault("FirstNameEn"),
            LastNameEn = row.GetValueOrDefault("LastNameEn"),
            CitizenId = row.GetValueOrDefault("CitizenId"),
            DateOfBirth = DateTime.TryParse(row.GetValueOrDefault("DateOfBirth"), out var dob) ? dob : null,
            Phone = row.GetValueOrDefault("Phone"),
            Email = row.GetValueOrDefault("Email"),
            Department = row.GetValueOrDefault("Department"),
            Position = row.GetValueOrDefault("Position"),
            StartDate = DateTime.TryParse(row.GetValueOrDefault("StartDate"), out var sd) ? sd
                : throw new InvalidOperationException("StartDate ไม่ถูกต้อง"),
            BaseSalary = decimal.TryParse(row.GetValueOrDefault("BaseSalary"), out var bs) ? bs
                : throw new InvalidOperationException("BaseSalary ไม่ถูกต้อง"),
            SalaryType = row.GetValueOrDefault("SalaryType") ?? "Monthly",
            BankName = row.GetValueOrDefault("BankName"),
            BankAccountNumber = row.GetValueOrDefault("BankAccountNumber"),
            SocialSecurityNumber = row.GetValueOrDefault("SocialSecurityNumber"),
            CreatedBy = performedBy
        });
    }

    private async Task<List<Dictionary<string, string>>> ExportEmployeesAsync(Guid companyId, ExportRequest request)
    {
        var emps = await _db.Employees.Where(e => e.CompanyId == companyId).OrderBy(e => e.EmployeeCode).ToListAsync();
        return emps.Select(e => new Dictionary<string, string>
        {
            ["EmployeeCode"] = e.EmployeeCode,
            ["TitleTh"] = e.TitleTh,
            ["FirstNameTh"] = e.FirstNameTh,
            ["LastNameTh"] = e.LastNameTh,
            ["FirstNameEn"] = e.FirstNameEn ?? "",
            ["LastNameEn"] = e.LastNameEn ?? "",
            ["CitizenId"] = e.CitizenId ?? "",
            ["DateOfBirth"] = e.DateOfBirth?.ToString("yyyy-MM-dd") ?? "",
            ["Phone"] = e.Phone ?? "",
            ["Email"] = e.Email ?? "",
            ["Department"] = e.Department ?? "",
            ["Position"] = e.Position ?? "",
            ["StartDate"] = e.StartDate.ToString("yyyy-MM-dd"),
            ["BaseSalary"] = e.BaseSalary.ToString("F2"),
            ["SalaryType"] = e.SalaryType,
            ["BankName"] = e.BankName ?? "",
            ["BankAccountNumber"] = e.BankAccountNumber ?? "",
            ["SocialSecurityNumber"] = e.SocialSecurityNumber ?? "",
            ["IsActive"] = e.IsActive.ToString()
        }).ToList();
    }

    // ===== Budgets =====
    // Rows are grouped by (BudgetName + FiscalYear). First row creates the
    // header; subsequent rows append BudgetLine entries. Re-running with the
    // same BudgetName + FiscalYear adds lines to the existing budget rather
    // than spawning duplicates.

    private async Task ImportBudgetAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var name = row.GetValueOrDefault("BudgetName") ?? throw new InvalidOperationException("BudgetName is required");
        var year = int.TryParse(row.GetValueOrDefault("FiscalYear"), out var fy) ? fy
            : throw new InvalidOperationException("FiscalYear ไม่ถูกต้อง");
        var acctCode = row.GetValueOrDefault("AccountCode") ?? throw new InvalidOperationException("AccountCode is required");
        var account = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == acctCode)
            ?? throw new KeyNotFoundException($"ไม่พบบัญชี {acctCode}");

        var budget = await _db.Budgets
            .Include(b => b.Lines)
            .FirstOrDefaultAsync(b => b.CompanyId == companyId && b.Name == name && b.FiscalYear == year);
        if (budget == null)
        {
            budget = new Budget { CompanyId = companyId, Name = name, FiscalYear = year, IsActive = true, CreatedBy = performedBy };
            _db.Budgets.Add(budget);
        }

        decimal M(string key) => decimal.TryParse(row.GetValueOrDefault(key), out var v) ? v : 0;
        budget.Lines.Add(new BudgetLine
        {
            AccountId = account.Id,
            Month1 = M("Month1"), Month2 = M("Month2"), Month3 = M("Month3"), Month4 = M("Month4"),
            Month5 = M("Month5"), Month6 = M("Month6"), Month7 = M("Month7"), Month8 = M("Month8"),
            Month9 = M("Month9"), Month10 = M("Month10"), Month11 = M("Month11"), Month12 = M("Month12"),
        });
    }

    private async Task<List<Dictionary<string, string>>> ExportBudgetsAsync(Guid companyId, ExportRequest request)
    {
        var budgets = await _db.Budgets
            .Include(b => b.Lines).ThenInclude(l => l.Account)
            .Where(b => b.CompanyId == companyId)
            .OrderBy(b => b.FiscalYear).ThenBy(b => b.Name)
            .ToListAsync();
        var rows = new List<Dictionary<string, string>>();
        foreach (var b in budgets)
            foreach (var l in b.Lines)
                rows.Add(new Dictionary<string, string>
                {
                    ["BudgetName"] = b.Name, ["FiscalYear"] = b.FiscalYear.ToString(),
                    ["AccountCode"] = l.Account.AccountCode, ["AccountName"] = l.Account.AccountName,
                    ["Month1"] = l.Month1.ToString("F2"), ["Month2"] = l.Month2.ToString("F2"),
                    ["Month3"] = l.Month3.ToString("F2"), ["Month4"] = l.Month4.ToString("F2"),
                    ["Month5"] = l.Month5.ToString("F2"), ["Month6"] = l.Month6.ToString("F2"),
                    ["Month7"] = l.Month7.ToString("F2"), ["Month8"] = l.Month8.ToString("F2"),
                    ["Month9"] = l.Month9.ToString("F2"), ["Month10"] = l.Month10.ToString("F2"),
                    ["Month11"] = l.Month11.ToString("F2"), ["Month12"] = l.Month12.ToString("F2"),
                });
        return rows;
    }

    // ===== Documents (legacy migration) =====
    // 1 row = 1 line in a document. Rows with the same DocumentNumber are
    // appended to the same Document header. The header is created on the
    // FIRST occurrence of a DocumentNumber within this import session;
    // subsequent rows reuse the entity from the tracking cache, so re-running
    // a file with hundreds of rows for one document doesn't issue hundreds
    // of duplicate-key collisions.

    private async Task ImportDocumentAsync(Guid companyId, Dictionary<string, string> row, string performedBy)
    {
        var docNum = row.GetValueOrDefault("DocumentNumber") ?? throw new InvalidOperationException("DocumentNumber is required");

        // Look up first in the change tracker (same session), then in DB.
        var doc = _db.ChangeTracker.Entries<Document>()
            .Select(e => e.Entity)
            .FirstOrDefault(d => d.CompanyId == companyId && d.DocumentNumber == docNum)
            ?? await _db.Documents.FirstOrDefaultAsync(d => d.CompanyId == companyId && d.DocumentNumber == docNum);

        if (doc == null)
        {
            if (!Enum.TryParse<DocumentType>(row.GetValueOrDefault("DocumentType"), true, out var docType))
                throw new InvalidOperationException("DocumentType ไม่ถูกต้อง");
            var docDate = DateTime.TryParse(row.GetValueOrDefault("DocumentDate"), out var dd) ? dd
                : throw new InvalidOperationException("DocumentDate ไม่ถูกต้อง");

            var contactName = row.GetValueOrDefault("ContactName") ?? "";
            var contactTaxId = row.GetValueOrDefault("ContactTaxId");
            // Tax id is a stronger key — try that first.
            Contact? contact = null;
            if (!string.IsNullOrWhiteSpace(contactTaxId))
                contact = await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.TaxId == contactTaxId);
            if (contact == null && !string.IsNullOrWhiteSpace(contactName))
                contact = await _db.Contacts.FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Name.Contains(contactName));
            if (contact == null)
                throw new KeyNotFoundException($"ไม่พบผู้ติดต่อ '{contactName}' (TaxId {contactTaxId ?? "-"})");

            doc = new Document
            {
                CompanyId = companyId,
                DocumentNumber = docNum,
                DocumentType = docType,
                DocumentDate = docDate,
                DueDate = DateTime.TryParse(row.GetValueOrDefault("DueDate"), out var due) ? due : null,
                ContactId = contact.Id,
                Status = DocumentStatus.Draft,
                Reference = row.GetValueOrDefault("Reference"),
                Notes = row.GetValueOrDefault("Notes"),
                CreatedBy = performedBy
            };
            _db.Documents.Add(doc);
        }

        var qty = decimal.TryParse(row.GetValueOrDefault("LineQuantity"), out var q) ? q : 1;
        var price = decimal.TryParse(row.GetValueOrDefault("LineUnitPrice"), out var pr) ? pr : 0;
        var disc = decimal.TryParse(row.GetValueOrDefault("LineDiscountPercent"), out var dp) ? dp : 0;
        var vat = decimal.TryParse(row.GetValueOrDefault("LineVatRate"), out var vr) ? vr : 7;
        var wht = decimal.TryParse(row.GetValueOrDefault("LineWhtRate"), out var wr) ? wr : 0;
        var lineAmt = qty * price * (1 - disc / 100m);
        var vatAmt = Math.Round(lineAmt * vat / 100m, 2, MidpointRounding.AwayFromZero);
        var whtAmt = Math.Round(lineAmt * wht / 100m, 2, MidpointRounding.AwayFromZero);

        Guid? accountId = null;
        var acctCode = row.GetValueOrDefault("LineAccountCode");
        if (!string.IsNullOrWhiteSpace(acctCode))
        {
            var acct = await _db.ChartOfAccounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == acctCode);
            accountId = acct?.Id;
        }

        doc.Lines.Add(new DocumentLine
        {
            LineOrder = doc.Lines.Count + 1,
            Description = row.GetValueOrDefault("LineDescription") ?? "",
            Quantity = qty,
            Unit = "ชิ้น",
            UnitPrice = price,
            DiscountPercent = disc,
            Amount = lineAmt,
            VatRate = vat,
            VatAmount = vatAmt,
            WithholdingTaxRate = wht,
            WithholdingTaxAmount = whtAmt,
            AccountId = accountId,
            ProductCode = row.GetValueOrDefault("LineProductCode")
        });

        // Recompute header totals each line so the header stays consistent
        // even when the run aborts mid-document.
        doc.SubTotal = doc.Lines.Sum(l => l.Amount);
        doc.VatAmount = doc.Lines.Sum(l => l.VatAmount);
        doc.WithholdingTaxAmount = doc.Lines.Sum(l => l.WithholdingTaxAmount);
        doc.TotalAmount = doc.SubTotal + doc.VatAmount - doc.WithholdingTaxAmount;
        doc.BalanceDue = doc.TotalAmount - doc.PaidAmount;
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
            case "chart-of-accounts":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("AccountCode")))
                    errors.Add(new ImportError(rowNumber, "AccountCode", "", "จำเป็นต้องระบุรหัสบัญชี"));
                if (!Enum.TryParse<AccountType>(row.GetValueOrDefault("AccountType"), true, out _))
                    errors.Add(new ImportError(rowNumber, "AccountType", row.GetValueOrDefault("AccountType") ?? "", "ประเภทบัญชีไม่ถูกต้อง"));
                break;
            case "journalentries":
            case "journal-entries":
                if (!DateTime.TryParse(row.GetValueOrDefault("Date"), out _))
                    errors.Add(new ImportError(rowNumber, "Date", row.GetValueOrDefault("Date") ?? "", "วันที่ไม่ถูกต้อง"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("AccountCode")))
                    errors.Add(new ImportError(rowNumber, "AccountCode", "", "จำเป็นต้องระบุรหัสบัญชี"));
                var debit = decimal.TryParse(row.GetValueOrDefault("DebitAmount"), out var dv) ? dv : 0;
                var credit = decimal.TryParse(row.GetValueOrDefault("CreditAmount"), out var cv) ? cv : 0;
                if (debit == 0 && credit == 0)
                    errors.Add(new ImportError(rowNumber, "Amount", "", "จำเป็นต้องระบุยอดเดบิตหรือเครดิต"));
                break;
            case "stock-opening":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("ProductCode")))
                    errors.Add(new ImportError(rowNumber, "ProductCode", "", "จำเป็นต้องระบุรหัสสินค้า"));
                if (!decimal.TryParse(row.GetValueOrDefault("Quantity"), out _))
                    errors.Add(new ImportError(rowNumber, "Quantity", row.GetValueOrDefault("Quantity") ?? "", "จำนวนไม่ถูกต้อง"));
                break;
            case "opening-ar":
            case "opening-ap":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("ContactName"))
                    && string.IsNullOrWhiteSpace(row.GetValueOrDefault("ContactTaxId")))
                    errors.Add(new ImportError(rowNumber, "ContactName", "", "ต้องระบุ ContactName หรือ ContactTaxId"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("InvoiceNumber")))
                    errors.Add(new ImportError(rowNumber, "InvoiceNumber", "", "จำเป็นต้องระบุเลขที่เอกสาร"));
                if (!DateTime.TryParse(row.GetValueOrDefault("InvoiceDate"), out _))
                    errors.Add(new ImportError(rowNumber, "InvoiceDate", row.GetValueOrDefault("InvoiceDate") ?? "", "วันที่เอกสารไม่ถูกต้อง"));
                if (!decimal.TryParse(row.GetValueOrDefault("Amount"), out var amt) || amt <= 0)
                    errors.Add(new ImportError(rowNumber, "Amount", row.GetValueOrDefault("Amount") ?? "", "ยอดคงค้างต้องมากกว่า 0"));
                break;
            case "stock-adjustments":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("ProductCode")))
                    errors.Add(new ImportError(rowNumber, "ProductCode", "", "จำเป็นต้องระบุรหัสสินค้า"));
                var mt = (row.GetValueOrDefault("MovementType") ?? "").ToUpperInvariant();
                if (mt != "IN" && mt != "OUT" && mt != "ADJUST")
                    errors.Add(new ImportError(rowNumber, "MovementType", mt, "ต้องเป็น IN / OUT / ADJUST"));
                if (!decimal.TryParse(row.GetValueOrDefault("Quantity"), out _))
                    errors.Add(new ImportError(rowNumber, "Quantity", row.GetValueOrDefault("Quantity") ?? "", "จำนวนไม่ถูกต้อง"));
                if (!DateTime.TryParse(row.GetValueOrDefault("AdjustmentDate"), out _))
                    errors.Add(new ImportError(rowNumber, "AdjustmentDate", row.GetValueOrDefault("AdjustmentDate") ?? "", "วันที่ไม่ถูกต้อง"));
                break;
            case "fixed-assets":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("AssetCode")))
                    errors.Add(new ImportError(rowNumber, "AssetCode", "", "จำเป็นต้องระบุรหัสสินทรัพย์"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Name")))
                    errors.Add(new ImportError(rowNumber, "Name", "", "จำเป็นต้องระบุชื่อสินทรัพย์"));
                if (!DateTime.TryParse(row.GetValueOrDefault("PurchaseDate"), out _))
                    errors.Add(new ImportError(rowNumber, "PurchaseDate", row.GetValueOrDefault("PurchaseDate") ?? "", "วันที่ซื้อไม่ถูกต้อง"));
                if (!decimal.TryParse(row.GetValueOrDefault("PurchaseCost"), out _))
                    errors.Add(new ImportError(rowNumber, "PurchaseCost", row.GetValueOrDefault("PurchaseCost") ?? "", "ราคาซื้อไม่ถูกต้อง"));
                if (!int.TryParse(row.GetValueOrDefault("UsefulLifeMonths"), out var life) || life <= 0)
                    errors.Add(new ImportError(rowNumber, "UsefulLifeMonths", row.GetValueOrDefault("UsefulLifeMonths") ?? "", "อายุการใช้งานไม่ถูกต้อง"));
                break;
            case "payments":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("DocumentNumber")))
                    errors.Add(new ImportError(rowNumber, "DocumentNumber", "", "จำเป็นต้องระบุเลขเอกสาร"));
                if (!decimal.TryParse(row.GetValueOrDefault("Amount"), out _))
                    errors.Add(new ImportError(rowNumber, "Amount", row.GetValueOrDefault("Amount") ?? "", "จำนวนเงินไม่ถูกต้อง"));
                if (!DateTime.TryParse(row.GetValueOrDefault("PaymentDate"), out _))
                    errors.Add(new ImportError(rowNumber, "PaymentDate", row.GetValueOrDefault("PaymentDate") ?? "", "วันที่ชำระไม่ถูกต้อง"));
                if (!Enum.TryParse<PaymentMethod>(row.GetValueOrDefault("PaymentMethod"), true, out _))
                    errors.Add(new ImportError(rowNumber, "PaymentMethod", row.GetValueOrDefault("PaymentMethod") ?? "", "วิธีชำระไม่ถูกต้อง"));
                break;
            case "projects":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Code")))
                    errors.Add(new ImportError(rowNumber, "Code", "", "จำเป็นต้องระบุรหัสโปรเจกต์"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("Name")))
                    errors.Add(new ImportError(rowNumber, "Name", "", "จำเป็นต้องระบุชื่อโปรเจกต์"));
                if (!DateTime.TryParse(row.GetValueOrDefault("StartDate"), out _))
                    errors.Add(new ImportError(rowNumber, "StartDate", row.GetValueOrDefault("StartDate") ?? "", "วันเริ่มต้นไม่ถูกต้อง"));
                break;
            case "employees":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("EmployeeCode")))
                    errors.Add(new ImportError(rowNumber, "EmployeeCode", "", "จำเป็นต้องระบุรหัสพนักงาน"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("FirstNameTh")))
                    errors.Add(new ImportError(rowNumber, "FirstNameTh", "", "จำเป็นต้องระบุชื่อ"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("LastNameTh")))
                    errors.Add(new ImportError(rowNumber, "LastNameTh", "", "จำเป็นต้องระบุนามสกุล"));
                if (!DateTime.TryParse(row.GetValueOrDefault("StartDate"), out _))
                    errors.Add(new ImportError(rowNumber, "StartDate", row.GetValueOrDefault("StartDate") ?? "", "วันเริ่มงานไม่ถูกต้อง"));
                if (!decimal.TryParse(row.GetValueOrDefault("BaseSalary"), out _))
                    errors.Add(new ImportError(rowNumber, "BaseSalary", row.GetValueOrDefault("BaseSalary") ?? "", "เงินเดือนไม่ถูกต้อง"));
                break;
            case "budgets":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("BudgetName")))
                    errors.Add(new ImportError(rowNumber, "BudgetName", "", "จำเป็นต้องระบุชื่องบประมาณ"));
                if (!int.TryParse(row.GetValueOrDefault("FiscalYear"), out _))
                    errors.Add(new ImportError(rowNumber, "FiscalYear", row.GetValueOrDefault("FiscalYear") ?? "", "ปีงบประมาณไม่ถูกต้อง"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("AccountCode")))
                    errors.Add(new ImportError(rowNumber, "AccountCode", "", "จำเป็นต้องระบุรหัสบัญชี"));
                break;
            case "documents":
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("DocumentNumber")))
                    errors.Add(new ImportError(rowNumber, "DocumentNumber", "", "จำเป็นต้องระบุเลขเอกสาร"));
                if (!Enum.TryParse<DocumentType>(row.GetValueOrDefault("DocumentType"), true, out _))
                    errors.Add(new ImportError(rowNumber, "DocumentType", row.GetValueOrDefault("DocumentType") ?? "", "ประเภทเอกสารไม่ถูกต้อง"));
                if (!DateTime.TryParse(row.GetValueOrDefault("DocumentDate"), out _))
                    errors.Add(new ImportError(rowNumber, "DocumentDate", row.GetValueOrDefault("DocumentDate") ?? "", "วันที่เอกสารไม่ถูกต้อง"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("ContactName"))
                    && string.IsNullOrWhiteSpace(row.GetValueOrDefault("ContactTaxId")))
                    errors.Add(new ImportError(rowNumber, "ContactName", "", "ต้องระบุชื่อหรือเลขผู้เสียภาษีของผู้ติดต่อ"));
                if (string.IsNullOrWhiteSpace(row.GetValueOrDefault("LineDescription")))
                    errors.Add(new ImportError(rowNumber, "LineDescription", "", "จำเป็นต้องระบุรายละเอียดของรายการ"));
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

    // ===== Smart Import - AI Column Matching =====

    public async Task<SmartImportSessionResponse> UploadAndAnalyzeAsync(Guid companyId, SmartImportUploadRequest request, string performedBy)
    {
        var templateFields = GetTemplateFields(request.EntityType);
        if (templateFields == null)
            throw new InvalidOperationException($"ไม่รองรับการนำเข้า {request.EntityType}");

        // สร้าง session
        var session = new SmartImportSession
        {
            CompanyId = companyId,
            EntityType = request.EntityType,
            FileName = request.FileName,
            FileFormat = request.FileFormat,
            HasHeaderRow = request.HasHeaderRow,
            TotalRows = request.HasHeaderRow ? request.RawData.Count - 1 : request.RawData.Count,
            Status = ImportSessionStatus.Analyzing,
            RawDataJson = JsonSerializer.Serialize(request.RawData),
            CreatedBy = performedBy
        };
        _db.SmartImportSessions.Add(session);

        // ดึง header row
        var headers = request.HasHeaderRow && request.RawData.Count > 0
            ? request.RawData[0]
            : Enumerable.Range(0, request.RawData.Count > 0 ? request.RawData[0].Count : 0)
                .Select(i => $"Column{i + 1}").ToList();

        // ดึง sample data (สูงสุด 5 แถว)
        var dataRows = request.HasHeaderRow ? request.RawData.Skip(1).ToList() : request.RawData;
        var sampleRows = dataRows.Take(5).ToList();

        // AI Column Matching
        var mappings = new List<SmartImportColumnMapping>();
        var unmappedCount = 0;

        for (int i = 0; i < headers.Count; i++)
        {
            var sourceHeader = headers[i];
            var sampleValues = sampleRows
                .Where(r => i < r.Count)
                .Select(r => r[i])
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Take(5)
                .ToList();

            var matchResult = AnalyzeColumnMatch(sourceHeader, sampleValues, templateFields);

            var mapping = new SmartImportColumnMapping
            {
                SessionId = session.Id,
                SourceIndex = i,
                SourceHeader = sourceHeader,
                TargetField = matchResult.TargetField,
                MatchType = matchResult.MatchType,
                Confidence = matchResult.Confidence,
                ConfidenceScore = matchResult.ConfidenceScore,
                SampleValuesJson = JsonSerializer.Serialize(sampleValues),
                SuggestionsJson = JsonSerializer.Serialize(matchResult.Suggestions)
            };
            mappings.Add(mapping);

            if (matchResult.MatchType == ColumnMatchType.Unmapped ||
                matchResult.Confidence == ColumnMatchConfidence.Low ||
                matchResult.Confidence == ColumnMatchConfidence.None)
            {
                unmappedCount++;
            }
        }

        _db.SmartImportColumnMappings.AddRange(mappings);

        // อัพเดตสถานะ session
        session.Status = unmappedCount > 0 ? ImportSessionStatus.MappingRequired : ImportSessionStatus.MappingCompleted;
        await _db.SaveChangesAsync();

        return MapToSessionResponse(session, mappings, sampleRows);
    }

    public async Task<SmartImportSessionResponse> GetSessionAsync(Guid companyId, Guid sessionId)
    {
        var session = await _db.SmartImportSessions
            .Include(s => s.ColumnMappings)
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ Import Session");

        var rawData = !string.IsNullOrEmpty(session.RawDataJson)
            ? JsonSerializer.Deserialize<List<List<string>>>(session.RawDataJson) ?? new()
            : new List<List<string>>();

        var dataRows = session.HasHeaderRow ? rawData.Skip(1).Take(5).ToList() : rawData.Take(5).ToList();

        return MapToSessionResponse(session, session.ColumnMappings.OrderBy(m => m.SourceIndex).ToList(), dataRows);
    }

    public async Task<SmartImportSessionResponse> SubmitManualMappingAsync(Guid companyId, ManualMappingRequest request, string performedBy)
    {
        var session = await _db.SmartImportSessions
            .Include(s => s.ColumnMappings)
            .FirstOrDefaultAsync(s => s.Id == request.SessionId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ Import Session");

        if (session.Status != ImportSessionStatus.MappingRequired &&
            session.Status != ImportSessionStatus.MappingCompleted)
            throw new InvalidOperationException("Session ไม่อยู่ในสถานะที่สามารถแก้ไข Mapping ได้");

        var templateFields = GetTemplateFields(session.EntityType)!;

        foreach (var entry in request.Mappings)
        {
            var mapping = session.ColumnMappings.FirstOrDefault(m => m.SourceIndex == entry.SourceIndex);
            if (mapping == null) continue;

            if (string.IsNullOrEmpty(entry.TargetField))
            {
                mapping.TargetField = null;
                mapping.MatchType = ColumnMatchType.Unmapped;
                mapping.Confidence = ColumnMatchConfidence.None;
                mapping.ConfidenceScore = 0;
            }
            else
            {
                var field = templateFields.FirstOrDefault(f =>
                    f.FieldName.Equals(entry.TargetField, StringComparison.OrdinalIgnoreCase));
                if (field == null)
                    throw new InvalidOperationException($"Target field '{entry.TargetField}' ไม่ถูกต้องสำหรับ {session.EntityType}");

                mapping.TargetField = field.FieldName;
                mapping.MatchType = ColumnMatchType.ManualMatch;
                mapping.Confidence = ColumnMatchConfidence.High;
                mapping.ConfidenceScore = 1.0;
            }
        }

        // ตรวจสอบว่ายังมี required field ที่ยังไม่ได้ map หรือไม่
        var mappedTargets = session.ColumnMappings
            .Where(m => !string.IsNullOrEmpty(m.TargetField))
            .Select(m => m.TargetField!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingRequired = templateFields
            .Where(f => f.IsRequired && !mappedTargets.Contains(f.FieldName))
            .ToList();

        session.Status = missingRequired.Count > 0
            ? ImportSessionStatus.MappingRequired
            : ImportSessionStatus.MappingCompleted;
        session.UpdatedBy = performedBy;

        await _db.SaveChangesAsync();

        var rawData = !string.IsNullOrEmpty(session.RawDataJson)
            ? JsonSerializer.Deserialize<List<List<string>>>(session.RawDataJson) ?? new()
            : new List<List<string>>();
        var dataRows = session.HasHeaderRow ? rawData.Skip(1).Take(5).ToList() : rawData.Take(5).ToList();

        return MapToSessionResponse(session, session.ColumnMappings.OrderBy(m => m.SourceIndex).ToList(), dataRows);
    }

    public async Task<SmartImportResult> ConfirmAndImportAsync(Guid companyId, SmartImportConfirmRequest request, string performedBy)
    {
        var session = await _db.SmartImportSessions
            .Include(s => s.ColumnMappings)
            .FirstOrDefaultAsync(s => s.Id == request.SessionId && s.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบ Import Session");

        if (session.Status != ImportSessionStatus.MappingCompleted && session.Status != ImportSessionStatus.Ready)
            throw new InvalidOperationException("Session ยังไม่พร้อม Import — กรุณาตรวจสอบ Column Mapping");

        session.Status = ImportSessionStatus.Importing;
        await _db.SaveChangesAsync();

        var rawData = !string.IsNullOrEmpty(session.RawDataJson)
            ? JsonSerializer.Deserialize<List<List<string>>>(session.RawDataJson) ?? new()
            : new List<List<string>>();

        var dataRows = session.HasHeaderRow ? rawData.Skip(1).ToList() : rawData;
        var activeMappings = session.ColumnMappings
            .Where(m => !string.IsNullOrEmpty(m.TargetField))
            .OrderBy(m => m.SourceIndex)
            .ToList();

        var successCount = 0;
        var errors = new List<ImportError>();
        var dateFormat = request.DateFormat ?? "yyyy-MM-dd";

        for (int i = 0; i < dataRows.Count; i++)
        {
            try
            {
                var rowData = dataRows[i];
                var mappedRow = new Dictionary<string, string>();

                foreach (var mapping in activeMappings)
                {
                    if (mapping.SourceIndex < rowData.Count && !string.IsNullOrEmpty(mapping.TargetField))
                    {
                        var value = rowData[mapping.SourceIndex];
                        mappedRow[mapping.TargetField] = value?.Trim() ?? "";
                    }
                }

                switch (session.EntityType.ToLower())
                {
                    case "contacts":
                        await ImportContactAsync(companyId, mappedRow);
                        break;
                    case "products":
                        await ImportProductAsync(companyId, mappedRow);
                        break;
                    case "chartofaccounts":
                    case "chart-of-accounts":
                        await ImportAccountAsync(companyId, mappedRow);
                        break;
                    case "banktransactions":
                        await ImportBankTransactionAsync(companyId, mappedRow);
                        break;
                    case "journalentries":
                    case "journal-entries":
                        await ImportJournalEntryAsync(companyId, mappedRow, performedBy);
                        break;
                    case "stock-opening":
                        await ImportStockOpeningAsync(companyId, mappedRow, performedBy);
                        break;
                    case "stock-adjustments":
                        await ImportStockAdjustmentAsync(companyId, mappedRow, performedBy);
                        break;
                    case "fixed-assets":
                        await ImportFixedAssetAsync(companyId, mappedRow, performedBy);
                        break;
                    case "payments":
                        await ImportPaymentAsync(companyId, mappedRow, performedBy);
                        break;
                    case "projects":
                        await ImportProjectAsync(companyId, mappedRow, performedBy);
                        break;
                    case "employees":
                        await ImportEmployeeAsync(companyId, mappedRow, performedBy);
                        break;
                    case "budgets":
                        await ImportBudgetAsync(companyId, mappedRow, performedBy);
                        break;
                    case "documents":
                        await ImportDocumentAsync(companyId, mappedRow, performedBy);
                        break;
                    case "opening-ar":
                        await ImportOpeningSubledgerAsync(companyId, mappedRow, performedBy, isReceivable: true);
                        break;
                    case "opening-ap":
                        await ImportOpeningSubledgerAsync(companyId, mappedRow, performedBy, isReceivable: false);
                        break;
                    default:
                        errors.Add(new ImportError(i + 1, "EntityType", session.EntityType,
                            $"ไม่รองรับการนำเข้า {session.EntityType}"));
                        continue;
                }
                successCount++;
            }
            catch (Exception ex)
            {
                errors.Add(new ImportError(i + 1, "", "", ex.Message));
                await _errorLogService.LogErrorAsync(ex, $"ImportExport.ImportRow/{i + 1}");
            }
        }

        await _db.SaveChangesAsync();

        session.SuccessCount = successCount;
        session.ErrorCount = errors.Count;
        session.SkippedCount = dataRows.Count - successCount - errors.Count;
        session.Status = errors.Count == 0 ? ImportSessionStatus.Completed : ImportSessionStatus.Failed;
        session.CompletedAt = DateTime.UtcNow;
        session.ErrorsJson = errors.Count > 0 ? JsonSerializer.Serialize(errors) : null;
        session.UpdatedBy = performedBy;

        // ลบ raw data หลัง import เสร็จเพื่อประหยัดพื้นที่
        session.RawDataJson = null;

        await _db.SaveChangesAsync();

        return new SmartImportResult(session.Id, session.EntityType, session.Status.ToString(),
            dataRows.Count, successCount, errors.Count, session.SkippedCount, errors, DateTime.UtcNow);
    }

    // ===== Template Download =====

    public Task<ImportTemplateDownloadResponse> DownloadTemplateAsync(string entityType, string format)
    {
        var fields = GetTemplateFields(entityType)
            ?? throw new InvalidOperationException($"ไม่รองรับ template สำหรับ {entityType}");

        var csv = new StringBuilder();

        // Header row
        csv.AppendLine(string.Join(",", fields.Select(f => EscapeCsv(f.FieldName))));

        // Sample data row
        var sampleRow = fields.Select(f => EscapeCsv(GetSampleValue(f))).ToList();
        csv.AppendLine(string.Join(",", sampleRow));

        // ส่ง description row (comment)
        var descRow = fields.Select(f => EscapeCsv(
            $"{f.DisplayName}{(f.IsRequired ? " *จำเป็น" : "")}{(f.Description != null ? $" ({f.Description})" : "")}" +
            $"{(f.AllowedValues != null ? $" [{string.Join("/", f.AllowedValues)}]" : "")}")).ToList();
        csv.AppendLine(string.Join(",", descRow));

        var fileName = $"import_template_{entityType}.csv";
        var fileData = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();

        return Task.FromResult(new ImportTemplateDownloadResponse(
            entityType, fileName, "text/csv", fileData, fields));
    }

    public Task<List<ImportableEntityInfo>> GetImportableEntitiesAsync()
    {
        var entities = new List<ImportableEntityInfo>
        {
            new("contacts", "ผู้ติดต่อ", "นำเข้ารายชื่อลูกค้าและผู้ขาย",
                GetTemplateFields("contacts")!),
            new("products", "สินค้า/บริการ", "นำเข้ารายการสินค้าและบริการ",
                GetTemplateFields("products")!),
            new("chartofaccounts", "ผังบัญชี", "นำเข้าผังบัญชี (Chart of Accounts)",
                GetTemplateFields("chartofaccounts")!),
            new("banktransactions", "รายการธนาคาร", "นำเข้ารายการเคลื่อนไหวบัญชีธนาคาร",
                GetTemplateFields("banktransactions")!),
            new("journal-entries", "บันทึกบัญชี", "นำเข้ารายการบันทึกบัญชี (Journal Entries)",
                GetTemplateFields("journal-entries")!),
            new("stock-opening", "สต็อกยกมา (Opening Stock)",
                "นำเข้ายอดสต็อกตั้งต้น — ใช้ตอน migrate จากระบบเดิม",
                GetTemplateFields("stock-opening")!),
            new("opening-ar", "ลูกหนี้ยกมา (Opening AR)",
                "นำเข้าใบแจ้งหนี้ค้างรับรายตัว — เข้ารายงานอายุลูกหนี้ + บัญชีแยกลูกหนี้ (ไม่ลง GL ซ้ำ)",
                GetTemplateFields("opening-ar")!),
            new("opening-ap", "เจ้าหนี้ยกมา (Opening AP)",
                "นำเข้าใบแจ้งหนี้ค้างจ่ายรายตัว — เข้ารายงานอายุเจ้าหนี้ + บัญชีแยกเจ้าหนี้ (ไม่ลง GL ซ้ำ)",
                GetTemplateFields("opening-ap")!),
            new("stock-adjustments", "ปรับสต็อก (Stock Adjustments)",
                "เพิ่ม/ลดสต็อก พร้อมเหตุผล (เสียหาย, ปรับนับ, ฯลฯ)",
                GetTemplateFields("stock-adjustments")!),
            new("fixed-assets", "สินทรัพย์ถาวร (Fixed Assets)",
                "นำเข้าทะเบียนสินทรัพย์ถาวร พร้อมข้อมูลค่าเสื่อม",
                GetTemplateFields("fixed-assets")!),
            new("payments", "การชำระเงิน (Payments)",
                "นำเข้ารายการรับ-จ่ายชำระ ผูกกับเลขเอกสาร",
                GetTemplateFields("payments")!),
            new("projects", "โปรเจกต์ (Projects)",
                "นำเข้าทะเบียนโปรเจกต์ พร้อมงบประมาณ + วันเริ่ม-สิ้นสุด",
                GetTemplateFields("projects")!),
            new("employees", "พนักงาน (Employees)",
                "นำเข้าทะเบียนพนักงาน + เงินเดือนพื้นฐาน",
                GetTemplateFields("employees")!),
            new("budgets", "งบประมาณ (Budgets)",
                "นำเข้างบประมาณรายเดือน (Month1–Month12) ต่อบัญชี",
                GetTemplateFields("budgets")!),
            new("documents", "เอกสาร (Documents)",
                "นำเข้าเอกสารเก่า (Invoice / Receipt / TaxInvoice / ฯลฯ) — 1 แถว = 1 line ของเอกสาร, group ด้วย DocumentNumber",
                GetTemplateFields("documents")!)
        };

        return Task.FromResult(entities);
    }

    // ===== AI Column Matching Logic =====

    private static ColumnAnalysisResult AnalyzeColumnMatch(
        string sourceHeader, List<string> sampleValues, List<ImportField> templateFields)
    {
        var suggestions = new List<SuggestedMappingDto>();
        string? bestTarget = null;
        double bestScore = 0;

        foreach (var field in templateFields)
        {
            double score = 0;
            var reasons = new List<string>();

            // 1. Exact match (header name)
            if (sourceHeader.Equals(field.FieldName, StringComparison.OrdinalIgnoreCase))
            {
                score = 1.0;
                reasons.Add("ชื่อ column ตรงกัน");
            }
            else
            {
                // 2. ชื่อภาษาไทยตรงกัน
                if (sourceHeader.Equals(field.DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    score = 0.95;
                    reasons.Add("ชื่อภาษาไทยตรงกัน");
                }

                // 3. Contains / partial match
                var normalizedSource = NormalizeHeader(sourceHeader);
                var normalizedField = NormalizeHeader(field.FieldName);
                var normalizedDisplay = NormalizeHeader(field.DisplayName);

                if (score < 0.9)
                {
                    if (normalizedSource.Contains(normalizedField) || normalizedField.Contains(normalizedSource))
                    {
                        score = Math.Max(score, 0.8);
                        reasons.Add("ชื่อคล้ายกัน (EN)");
                    }
                    if (normalizedSource.Contains(normalizedDisplay) || normalizedDisplay.Contains(normalizedSource))
                    {
                        score = Math.Max(score, 0.8);
                        reasons.Add("ชื่อคล้ายกัน (TH)");
                    }
                }

                // 4. Alias matching (ชื่อที่มักใช้กันทั่วไป)
                var aliases = GetFieldAliases(field.FieldName);
                foreach (var alias in aliases)
                {
                    if (normalizedSource.Contains(NormalizeHeader(alias)) ||
                        NormalizeHeader(alias).Contains(normalizedSource))
                    {
                        score = Math.Max(score, 0.85);
                        reasons.Add($"ตรงกับชื่อที่ใช้ทั่วไป: {alias}");
                        break;
                    }
                }

                // 5. Data pattern matching (วิเคราะห์ข้อมูลตัวอย่าง)
                if (sampleValues.Count > 0 && score < 0.7)
                {
                    var patternScore = AnalyzeDataPattern(sampleValues, field);
                    if (patternScore > 0.5)
                    {
                        score = Math.Max(score, patternScore * 0.75);
                        reasons.Add("รูปแบบข้อมูลตรงกัน");
                    }
                }
            }

            if (score > 0.3)
            {
                suggestions.Add(new SuggestedMappingDto(
                    field.FieldName, field.DisplayName, score,
                    string.Join(", ", reasons)));
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestTarget = field.FieldName;
            }
        }

        // จัดเรียง suggestions ตาม score
        suggestions = suggestions.OrderByDescending(s => s.Score).Take(3).ToList();

        // กำหนด confidence
        var matchType = bestScore >= 0.9 ? ColumnMatchType.ExactMatch
            : bestScore >= 0.6 ? ColumnMatchType.AiMatched
            : ColumnMatchType.Unmapped;

        var confidence = bestScore >= 0.9 ? ColumnMatchConfidence.High
            : bestScore >= 0.7 ? ColumnMatchConfidence.Medium
            : bestScore >= 0.5 ? ColumnMatchConfidence.Low
            : ColumnMatchConfidence.None;

        // ถ้า confidence ต่ำเกินไป ไม่ auto-assign
        if (confidence == ColumnMatchConfidence.None || confidence == ColumnMatchConfidence.Low)
        {
            bestTarget = null;
            matchType = ColumnMatchType.Unmapped;
        }

        return new ColumnAnalysisResult(bestTarget, matchType, confidence, bestScore, suggestions);
    }

    private static double AnalyzeDataPattern(List<string> values, ImportField field)
    {
        if (values.Count == 0) return 0;

        switch (field.DataType.ToLower())
        {
            case "decimal":
                var decimalCount = values.Count(v => decimal.TryParse(v.Replace(",", ""), out _));
                return (double)decimalCount / values.Count;

            case "date":
                var dateCount = values.Count(v => DateTime.TryParse(v, out _));
                return (double)dateCount / values.Count;

            case "bool":
                var boolPatterns = new[] { "true", "false", "yes", "no", "1", "0", "ใช่", "ไม่ใช่", "y", "n" };
                var boolCount = values.Count(v => boolPatterns.Contains(v.Trim().ToLower()));
                return (double)boolCount / values.Count;

            case "guid":
                var guidCount = values.Count(v => Guid.TryParse(v, out _));
                return (double)guidCount / values.Count;

            case "enum":
                if (field.AllowedValues != null)
                {
                    var enumCount = values.Count(v =>
                        field.AllowedValues.Any(a => a.Equals(v.Trim(), StringComparison.OrdinalIgnoreCase)));
                    return (double)enumCount / values.Count;
                }
                return 0;

            case "string":
                // ตรวจสอบ pattern ของข้อมูลที่เป็นข้อมูลเฉพาะ
                if (field.FieldName.Contains("Email", StringComparison.OrdinalIgnoreCase))
                {
                    var emailCount = values.Count(v => v.Contains('@'));
                    return (double)emailCount / values.Count;
                }
                if (field.FieldName.Contains("Phone", StringComparison.OrdinalIgnoreCase))
                {
                    var phoneCount = values.Count(v => v.Any(char.IsDigit) && v.Length >= 9);
                    return (double)phoneCount / values.Count;
                }
                if (field.FieldName.Contains("TaxId", StringComparison.OrdinalIgnoreCase))
                {
                    var taxCount = values.Count(v => v.All(char.IsDigit) && v.Length == 13);
                    return (double)taxCount / values.Count;
                }
                return 0;

            default:
                return 0;
        }
    }

    private static List<string> GetFieldAliases(string fieldName)
    {
        return fieldName.ToLower() switch
        {
            "name" => new List<string> { "ชื่อ", "customer_name", "supplier_name", "company_name",
                "contact_name", "ชื่อบริษัท", "ชื่อลูกค้า", "ชื่อผู้ขาย", "customer", "supplier", "vendor" },
            "taxid" => new List<string> { "tax_id", "tin", "เลขผู้เสียภาษี", "เลขประจำตัว",
                "tax_number", "vat_id", "tax_identification", "เลขที่ผู้เสียภาษี" },
            "email" => new List<string> { "e_mail", "mail", "อีเมล", "email_address", "e-mail" },
            "phone" => new List<string> { "tel", "telephone", "โทรศัพท์", "เบอร์โทร", "phone_number",
                "mobile", "มือถือ", "contact_number" },
            "address" => new List<string> { "ที่อยู่", "addr", "street", "location", "ที่ตั้ง" },
            "contactperson" => new List<string> { "contact_person", "ผู้ติดต่อ", "person", "contact_name", "representative" },
            "iscustomer" => new List<string> { "is_customer", "customer_flag", "เป็นลูกค้า", "customer_type", "ลูกค้า" },
            "issupplier" => new List<string> { "is_supplier", "supplier_flag", "เป็นผู้ขาย", "vendor_flag", "ผู้ขาย" },
            "code" => new List<string> { "รหัส", "product_code", "item_code", "sku", "รหัสสินค้า",
                "barcode", "item_no", "part_number" },
            "producttype" => new List<string> { "product_type", "type", "ประเภท", "item_type", "ชนิด" },
            "unit" => new List<string> { "หน่วย", "uom", "unit_of_measure", "หน่วยนับ", "measure" },
            "sellingprice" => new List<string> { "selling_price", "sell_price", "price", "ราคาขาย",
                "unit_price", "retail_price", "ราคา" },
            "costprice" => new List<string> { "cost_price", "cost", "ราคาทุน", "purchase_price",
                "buy_price", "ต้นทุน" },
            "vatrate" => new List<string> { "vat_rate", "tax_rate", "vat", "อัตราภาษี", "tax_percent", "ภาษี" },
            "category" => new List<string> { "หมวดหมู่", "group", "กลุ่ม", "product_group", "item_group" },
            "accountcode" => new List<string> { "account_code", "acc_code", "gl_code", "รหัสบัญชี",
                "account_no", "account_number", "เลขที่บัญชี" },
            "accountname" => new List<string> { "account_name", "acc_name", "gl_name", "ชื่อบัญชี",
                "account_description" },
            "accountnameen" => new List<string> { "account_name_en", "english_name", "ชื่อบัญชีอังกฤษ",
                "name_en", "eng_name" },
            "accounttype" => new List<string> { "account_type", "acc_type", "type", "ประเภทบัญชี",
                "gl_type", "account_category" },
            "description" => new List<string> { "desc", "รายละเอียด", "คำอธิบาย", "note", "remark",
                "หมายเหตุ", "memo" },
            "bankaccountid" => new List<string> { "bank_account_id", "bank_account", "บัญชีธนาคาร",
                "account_id" },
            "transactiondate" => new List<string> { "transaction_date", "date", "txn_date", "วันที่",
                "วันที่ทำรายการ", "posting_date", "value_date" },
            "transactiontype" => new List<string> { "transaction_type", "txn_type", "type", "ประเภทรายการ",
                "ประเภท" },
            "amount" => new List<string> { "จำนวนเงิน", "total", "value", "sum", "ยอดเงิน",
                "จำนวน", "money" },
            "reference" => new List<string> { "ref", "ref_no", "reference_no", "อ้างอิง",
                "เลขที่อ้างอิง", "ref_number" },
            _ => new List<string>()
        };
    }

    private static string NormalizeHeader(string header)
    {
        return header.ToLower()
            .Replace(" ", "").Replace("_", "").Replace("-", "")
            .Replace(".", "").Replace("(", "").Replace(")", "");
    }

    private static List<ImportField>? GetTemplateFields(string entityType)
    {
        return entityType.ToLower() switch
        {
            "contacts" => new List<ImportField>
            {
                new("Name", "ชื่อ", "string", true, "ชื่อลูกค้า/ผู้ขาย", null),
                new("TaxId", "เลขผู้เสียภาษี", "string", false, "เลขประจำตัวผู้เสียภาษี 13 หลัก", null),
                new("IsCustomer", "เป็นลูกค้า", "bool", false, "true/false", new List<string> { "true", "false" }),
                new("IsSupplier", "เป็นผู้ขาย", "bool", false, "true/false", new List<string> { "true", "false" }),
                new("Email", "อีเมล", "string", false, null, null),
                new("Phone", "โทรศัพท์", "string", false, null, null),
                new("Address", "ที่อยู่", "string", false, null, null),
                new("ContactPerson", "ผู้ติดต่อ", "string", false, null, null),
            },
            "products" => new List<ImportField>
            {
                new("Code", "รหัสสินค้า", "string", true, null, null),
                new("Name", "ชื่อสินค้า", "string", true, null, null),
                new("ProductType", "ประเภท", "enum", false, null, new List<string> { "Product", "Service", "NonStock" }),
                new("Unit", "หน่วยนับ", "string", true, null, null),
                new("SellingPrice", "ราคาขาย", "decimal", false, null, null),
                new("CostPrice", "ราคาทุน", "decimal", false, null, null),
                new("VatRate", "อัตราภาษี", "decimal", false, null, null),
                new("Category", "หมวดหมู่", "string", false, null, null),
            },
            "chartofaccounts" or "chart-of-accounts" => new List<ImportField>
            {
                new("AccountCode", "รหัสบัญชี", "string", true, null, null),
                new("AccountName", "ชื่อบัญชี", "string", true, null, null),
                new("AccountNameEn", "ชื่อบัญชี (อังกฤษ)", "string", false, null, null),
                new("AccountType", "ประเภทบัญชี", "enum", true, null, new List<string> { "Asset", "Liability", "Equity", "Revenue", "Expense" }),
                new("Description", "คำอธิบาย", "string", false, null, null),
            },
            "banktransactions" => new List<ImportField>
            {
                new("BankAccountId", "รหัสบัญชีธนาคาร", "guid", true, null, null),
                new("TransactionDate", "วันที่", "date", true, "yyyy-MM-dd", null),
                new("TransactionType", "ประเภท", "enum", true, null, new List<string> { "Deposit", "Withdrawal", "Transfer", "Fee", "Interest" }),
                new("Amount", "จำนวนเงิน", "decimal", true, null, null),
                new("Description", "คำอธิบาย", "string", false, null, null),
                new("Reference", "อ้างอิง", "string", false, null, null),
            },
            "stock-opening" => new List<ImportField>
            {
                new("ProductCode", "รหัสสินค้า", "string", true, "ต้องมีอยู่ในระบบแล้ว", null),
                new("Quantity", "ยอดยกมา", "decimal", true, "สต็อกตั้งต้น (จำนวน)", null),
                new("UnitCost", "ต้นทุน/หน่วย", "decimal", false, "ใช้เป็นต้นทุนของสต็อกยกมา (default = CostPrice ของ Product)", null),
                new("OpeningDate", "วันยกมา", "date", false, "default = วันนี้", null),
                new("Notes", "หมายเหตุ", "string", false, null, null),
            },
            "opening-ar" or "opening-ap" => new List<ImportField>
            {
                new("ContactName", "ชื่อลูกค้า/ผู้ขาย", "string", true, "จับคู่ผู้ติดต่อเดิม — ถ้าไม่พบจะสร้างใหม่ให้", null),
                new("ContactTaxId", "เลขผู้เสียภาษี", "string", false, "ใช้จับคู่ผู้ติดต่อ (แม่นกว่าชื่อ)", null),
                new("InvoiceNumber", "เลขที่เอกสารเดิม", "string", true, "เลขใบแจ้งหนี้จากระบบเก่า — ใช้กันการนำเข้าซ้ำ", null),
                new("InvoiceDate", "วันที่เอกสาร", "date", true, "yyyy-MM-dd — ใช้คำนวณอายุหนี้", null),
                new("DueDate", "วันครบกำหนด", "date", false, "yyyy-MM-dd", null),
                new("Amount", "ยอดคงค้าง", "decimal", true, "ยอดที่ยังค้างชำระ ณ วันยกมา", null),
                new("Description", "รายละเอียด", "string", false, null, null),
            },
            "stock-adjustments" => new List<ImportField>
            {
                new("ProductCode", "รหัสสินค้า", "string", true, null, null),
                new("AdjustmentDate", "วันที่ปรับ", "date", true, "yyyy-MM-dd", null),
                new("MovementType", "ประเภท", "enum", true, "IN=เพิ่ม, OUT=ลด, ADJUST=ปรับนับ", new List<string> { "IN", "OUT", "ADJUST" }),
                new("Quantity", "จำนวน", "decimal", true, "บวกสำหรับ IN/ADJUST+, ลบสำหรับ OUT/ADJUST-", null),
                new("UnitCost", "ต้นทุน/หน่วย", "decimal", false, "default = CostPrice ของ Product", null),
                new("Reference", "เลขเอกสารอ้างอิง", "string", false, null, null),
                new("Notes", "หมายเหตุ", "string", false, "เช่น เสียหาย, ปรับนับสิ้นเดือน", null),
            },
            "fixed-assets" => new List<ImportField>
            {
                new("AssetCode", "รหัสสินทรัพย์", "string", true, null, null),
                new("Name", "ชื่อสินทรัพย์", "string", true, null, null),
                new("Category", "หมวดหมู่", "string", false, "เช่น เครื่องใช้สำนักงาน, ยานพาหนะ", null),
                new("PurchaseDate", "วันที่ซื้อ", "date", true, "yyyy-MM-dd", null),
                new("PurchaseCost", "ราคาซื้อ", "decimal", true, null, null),
                new("SalvageValue", "มูลค่าซาก", "decimal", false, "default = 0", null),
                new("UsefulLifeMonths", "อายุการใช้งาน (เดือน)", "decimal", true, "เช่น 60 = 5 ปี", null),
                new("DepreciationMethod", "วิธีคิดค่าเสื่อม", "enum", false, "default = StraightLine",
                    new List<string> { "StraightLine", "DecliningBalance", "UnitsOfProduction" }),
                new("AccumulatedDepreciation", "ค่าเสื่อมสะสมยกมา", "decimal", false, "ใช้ตอน migrate ระหว่างปี", null),
                new("Location", "ตำแหน่ง", "string", false, null, null),
                new("SerialNumber", "หมายเลขเครื่อง", "string", false, null, null),
            },
            "payments" => new List<ImportField>
            {
                new("DocumentNumber", "เลขเอกสาร", "string", true, "เลขเอกสารที่จะตัดชำระ (Invoice / Bill)", null),
                new("PaymentDate", "วันที่ชำระ", "date", true, "yyyy-MM-dd", null),
                new("Amount", "จำนวนเงิน", "decimal", true, null, null),
                new("PaymentMethod", "วิธีชำระ", "enum", true, null,
                    new List<string> { "Cash", "BankTransfer", "CreditCard", "Cheque", "PromptPay", "DirectDebit", "EWallet", "Other" }),
                new("Reference", "อ้างอิง", "string", false, "เลขที่อ้างอิงการโอน/เช็ค", null),
                new("BankAccount", "บัญชีธนาคาร", "string", false, "ชื่อ/เลขบัญชี — เก็บเป็น text", null),
                new("Notes", "หมายเหตุ", "string", false, null, null),
            },
            "projects" => new List<ImportField>
            {
                new("Code", "รหัสโปรเจกต์", "string", true, null, null),
                new("Name", "ชื่อโปรเจกต์", "string", true, null, null),
                new("NameEn", "ชื่อ (อังกฤษ)", "string", false, null, null),
                new("Description", "รายละเอียด", "string", false, null, null),
                new("CustomerName", "ลูกค้า", "string", false, "ชื่อลูกค้า/นายจ้าง", null),
                new("ProjectManagerName", "ผู้จัดการโปรเจกต์", "string", false, null, null),
                new("StartDate", "วันเริ่มต้น", "date", true, "yyyy-MM-dd", null),
                new("EndDate", "วันสิ้นสุด (แผน)", "date", false, null, null),
                new("Status", "สถานะ", "enum", false, "default = Active",
                    new List<string> { "Active", "OnHold", "Completed", "Cancelled" }),
                new("BudgetAmount", "งบประมาณ", "decimal", false, null, null),
                new("ContractAmount", "มูลค่าสัญญา", "decimal", false, null, null),
                new("BillingMethod", "วิธีเรียกเก็บ", "enum", false, "default = FixedPrice",
                    new List<string> { "FixedPrice", "TimeAndMaterial", "Milestone" }),
            },
            "employees" => new List<ImportField>
            {
                new("EmployeeCode", "รหัสพนักงาน", "string", true, null, null),
                new("TitleTh", "คำนำหน้า", "string", false, "นาย / นาง / นางสาว", null),
                new("FirstNameTh", "ชื่อ", "string", true, null, null),
                new("LastNameTh", "นามสกุล", "string", true, null, null),
                new("FirstNameEn", "ชื่อ (อังกฤษ)", "string", false, null, null),
                new("LastNameEn", "นามสกุล (อังกฤษ)", "string", false, null, null),
                new("CitizenId", "เลขบัตร ปชช.", "string", false, "13 หลัก", null),
                new("DateOfBirth", "วันเกิด", "date", false, "yyyy-MM-dd", null),
                new("Phone", "โทรศัพท์", "string", false, null, null),
                new("Email", "อีเมล", "string", false, null, null),
                new("Department", "แผนก", "string", false, null, null),
                new("Position", "ตำแหน่ง", "string", false, null, null),
                new("StartDate", "วันเริ่มงาน", "date", true, "yyyy-MM-dd", null),
                new("BaseSalary", "เงินเดือนพื้นฐาน", "decimal", true, null, null),
                new("SalaryType", "ประเภทเงินเดือน", "enum", false, "default = Monthly",
                    new List<string> { "Monthly", "Daily", "Hourly" }),
                new("BankName", "ธนาคาร", "string", false, null, null),
                new("BankAccountNumber", "เลขที่บัญชีธนาคาร", "string", false, null, null),
                new("SocialSecurityNumber", "เลขประกันสังคม", "string", false, null, null),
            },
            "budgets" => new List<ImportField>
            {
                new("BudgetName", "ชื่องบประมาณ", "string", true, "เช่น 'งบ 2026' — ใช้ group บรรทัดเข้าด้วยกัน", null),
                new("FiscalYear", "ปีงบประมาณ", "decimal", true, "เช่น 2026", null),
                new("AccountCode", "รหัสบัญชี", "string", true, "ต้องมีในผังบัญชีแล้ว", null),
                new("Month1", "ม.ค.", "decimal", false, null, null),
                new("Month2", "ก.พ.", "decimal", false, null, null),
                new("Month3", "มี.ค.", "decimal", false, null, null),
                new("Month4", "เม.ย.", "decimal", false, null, null),
                new("Month5", "พ.ค.", "decimal", false, null, null),
                new("Month6", "มิ.ย.", "decimal", false, null, null),
                new("Month7", "ก.ค.", "decimal", false, null, null),
                new("Month8", "ส.ค.", "decimal", false, null, null),
                new("Month9", "ก.ย.", "decimal", false, null, null),
                new("Month10", "ต.ค.", "decimal", false, null, null),
                new("Month11", "พ.ย.", "decimal", false, null, null),
                new("Month12", "ธ.ค.", "decimal", false, null, null),
            },
            "documents" => new List<ImportField>
            {
                new("DocumentNumber", "เลขเอกสาร", "string", true, "ใช้ group line ต่อเอกสารเดียวกัน", null),
                new("DocumentType", "ประเภทเอกสาร", "enum", true, null,
                    new List<string> { "Quotation", "Invoice", "TaxInvoice", "Receipt", "PurchaseInvoice", "PaymentVoucher", "ReceiptVoucher", "Expense" }),
                new("DocumentDate", "วันที่เอกสาร", "date", true, "yyyy-MM-dd", null),
                new("DueDate", "วันครบกำหนด", "date", false, null, null),
                new("ContactName", "ชื่อผู้ติดต่อ", "string", true, "ต้องมีในระบบแล้ว — match ด้วย Contains", null),
                new("ContactTaxId", "เลขผู้เสียภาษีผู้ติดต่อ", "string", false, "ถ้ามี ใช้จับคู่แม่นกว่าชื่อ", null),
                new("Reference", "อ้างอิง", "string", false, null, null),
                new("Notes", "หมายเหตุ", "string", false, null, null),
                new("LineDescription", "รายละเอียด (line)", "string", true, null, null),
                new("LineQuantity", "จำนวน", "decimal", true, null, null),
                new("LineUnitPrice", "ราคา/หน่วย", "decimal", true, null, null),
                new("LineDiscountPercent", "ส่วนลด %", "decimal", false, "default = 0", null),
                new("LineVatRate", "VAT %", "decimal", false, "default = 7", null),
                new("LineWhtRate", "หัก ณ ที่จ่าย %", "decimal", false, "default = 0", null),
                new("LineProductCode", "รหัสสินค้า", "string", false, null, null),
                new("LineAccountCode", "รหัสบัญชี", "string", false, null, null),
            },
            _ => null
        };
    }

    private static string GetSampleValue(ImportField field)
    {
        return field.FieldName switch
        {
            "Name" => "บริษัท ตัวอย่าง จำกัด",
            "TaxId" => "0123456789012",
            "IsCustomer" => "true",
            "IsSupplier" => "false",
            "Email" => "info@example.com",
            "Phone" => "02-123-4567",
            "Address" => "123 ถ.สุขุมวิท กรุงเทพฯ 10110",
            "ContactPerson" => "คุณสมชาย",
            "Code" => "P001",
            "ProductType" => "Product",
            "Unit" => "ชิ้น",
            "SellingPrice" => "100.00",
            "CostPrice" => "60.00",
            "VatRate" => "7",
            "Category" => "สินค้าทั่วไป",
            "AccountCode" => "1110",
            "AccountName" => "เงินสด",
            "AccountNameEn" => "Cash",
            "AccountType" => "Asset",
            "Description" => "รายละเอียด",
            "BankAccountId" => "00000000-0000-0000-0000-000000000000",
            "TransactionDate" => "2026-01-15",
            "TransactionType" => "Deposit",
            "Amount" => "50000.00",
            "Reference" => "REF001",
            "ProductCode" => "P001",
            "Quantity" => "100",
            "UnitCost" => "60.00",
            "OpeningDate" => "2026-01-01",
            "AdjustmentDate" => "2026-01-15",
            "MovementType" => "ADJUST",
            "Notes" => "ปรับนับสิ้นเดือน",
            "AssetCode" => "FA-001",
            "PurchaseDate" => "2024-03-15",
            "PurchaseCost" => "85000.00",
            "SalvageValue" => "0",
            "UsefulLifeMonths" => "60",
            "DepreciationMethod" => "StraightLine",
            "AccumulatedDepreciation" => "0",
            "Location" => "สำนักงานใหญ่",
            "SerialNumber" => "SN-12345",
            "DocumentNumber" => "INV-202601-0001",
            "PaymentDate" => "2026-01-20",
            "PaymentMethod" => "BankTransfer",
            "BankAccount" => "SCB 123-4-56789-0",
            "CustomerName" => "บริษัท ลูกค้า จำกัด",
            "ProjectManagerName" => "คุณสมหญิง",
            "StartDate" => "2026-01-01",
            "EndDate" => "2026-12-31",
            "Status" => "Active",
            "BudgetAmount" => "500000.00",
            "ContractAmount" => "550000.00",
            "BillingMethod" => "FixedPrice",
            "EmployeeCode" => "EMP-001",
            "TitleTh" => "นาย",
            "FirstNameTh" => "สมชาย",
            "LastNameTh" => "ใจดี",
            "CitizenId" => "1234567890123",
            "DateOfBirth" => "1990-05-15",
            "Department" => "ฝ่ายขาย",
            "Position" => "พนักงานขาย",
            "BaseSalary" => "25000.00",
            "SalaryType" => "Monthly",
            "BankName" => "ธนาคารกสิกรไทย",
            "BankAccountNumber" => "123-4-56789-0",
            "SocialSecurityNumber" => "1234567890",
            "BudgetName" => "งบ 2026",
            "FiscalYear" => "2026",
            "Month1" => "10000", "Month2" => "10000", "Month3" => "10000",
            "Month4" => "12000", "Month5" => "12000", "Month6" => "12000",
            "Month7" => "12000", "Month8" => "12000", "Month9" => "12000",
            "Month10" => "15000", "Month11" => "15000", "Month12" => "15000",
            "DocumentType" => "Invoice",
            "DocumentDate" => "2026-01-15",
            "DueDate" => "2026-02-14",
            "ContactName" => "บริษัท ลูกค้า จำกัด",
            "ContactTaxId" => "0123456789012",
            "LineDescription" => "บริการที่ปรึกษา",
            "LineQuantity" => "1",
            "LineUnitPrice" => "10000.00",
            "LineVatRate" => "7",
            "LineWhtRate" => "3",
            "LineProductCode" => "P001",
            "LineAccountCode" => "4100",
            _ => ""
        };
    }

    private static SmartImportSessionResponse MapToSessionResponse(
        SmartImportSession session, IList<SmartImportColumnMapping> mappings, List<List<string>> previewData)
    {
        var unmappedCount = mappings.Count(m =>
            m.MatchType == ColumnMatchType.Unmapped ||
            m.Confidence == ColumnMatchConfidence.Low ||
            m.Confidence == ColumnMatchConfidence.None);

        var columnDtos = mappings.Select(m =>
        {
            var sampleValues = !string.IsNullOrEmpty(m.SampleValuesJson)
                ? JsonSerializer.Deserialize<List<string>>(m.SampleValuesJson) ?? new()
                : new List<string>();

            var suggestions = !string.IsNullOrEmpty(m.SuggestionsJson)
                ? JsonSerializer.Deserialize<List<SuggestedMappingDto>>(m.SuggestionsJson) ?? new()
                : new List<SuggestedMappingDto>();

            var targetDisplay = "";
            if (!string.IsNullOrEmpty(m.TargetField))
            {
                var fields = GetTemplateFields(session.EntityType);
                targetDisplay = fields?.FirstOrDefault(f =>
                    f.FieldName.Equals(m.TargetField, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? m.TargetField;
            }

            return new SmartColumnMappingDto(
                m.SourceIndex,
                m.SourceHeader,
                m.TargetField,
                targetDisplay,
                m.MatchType.ToString(),
                m.Confidence.ToString(),
                m.ConfidenceScore,
                sampleValues,
                suggestions);
        }).ToList();

        return new SmartImportSessionResponse(
            session.Id,
            session.EntityType,
            session.Status.ToString(),
            columnDtos,
            session.TotalRows,
            unmappedCount,
            unmappedCount > 0,
            previewData);
    }

    private record ColumnAnalysisResult(
        string? TargetField,
        ColumnMatchType MatchType,
        ColumnMatchConfidence Confidence,
        double ConfidenceScore,
        List<SuggestedMappingDto> Suggestions);
}
