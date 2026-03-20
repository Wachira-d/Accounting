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
                        await ImportAccountAsync(companyId, mappedRow);
                        break;
                    case "banktransactions":
                        await ImportBankTransactionAsync(companyId, mappedRow);
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
                GetTemplateFields("banktransactions")!)
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
            "chartofaccounts" => new List<ImportField>
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
