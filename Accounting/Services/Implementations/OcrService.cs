using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class OcrService : IOcrService
{
    private readonly AccountingDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OcrService> _logger;

    public OcrService(AccountingDbContext db, IHttpClientFactory httpClientFactory,
        IConfiguration configuration, ILogger<OcrService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<OcrResultResponse> ScanAsync(Guid companyId, Guid fileAttachmentId)
    {
        var file = await _db.FileAttachments
            .FirstOrDefaultAsync(f => f.Id == fileAttachmentId && f.CompanyId == companyId)
            ?? throw new InvalidOperationException("File attachment not found.");

        // Compute file hash for duplicate detection
        string? fileHash = null;
        try
        {
            if (File.Exists(file.StoragePath))
            {
                var bytes = await File.ReadAllBytesAsync(file.StoragePath);
                fileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
        }
        catch { /* hash is optional */ }

        // Check for duplicate by file hash
        OcrScanResult? duplicateOf = null;
        if (!string.IsNullOrEmpty(fileHash))
        {
            duplicateOf = await _db.Set<OcrScanResult>()
                .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.FileHash == fileHash && r.ScanStatus == "Completed");
        }

        var scanResult = new OcrScanResult
        {
            CompanyId = companyId,
            FileAttachmentId = fileAttachmentId,
            OriginalFileName = file.OriginalFileName,
            ScanStatus = "Processing",
            Confidence = 0m,
            FileHash = fileHash,
            IsDuplicate = duplicateOf != null,
            DuplicateOfScanId = duplicateOf?.Id
        };

        _db.Set<OcrScanResult>().Add(scanResult);
        await _db.SaveChangesAsync();

        if (duplicateOf != null)
        {
            scanResult.ScanStatus = "Completed";
            scanResult.DocumentType = duplicateOf.DocumentType;
            scanResult.Confidence = duplicateOf.Confidence;
            scanResult.ExtractedDocumentNumber = duplicateOf.ExtractedDocumentNumber;
            scanResult.ExtractedDate = duplicateOf.ExtractedDate;
            scanResult.ExtractedVendorName = duplicateOf.ExtractedVendorName;
            scanResult.ExtractedVendorTaxId = duplicateOf.ExtractedVendorTaxId;
            scanResult.ExtractedSubTotal = duplicateOf.ExtractedSubTotal;
            scanResult.ExtractedVatAmount = duplicateOf.ExtractedVatAmount;
            scanResult.ExtractedTotalAmount = duplicateOf.ExtractedTotalAmount;
            scanResult.MatchedContactId = duplicateOf.MatchedContactId;
            scanResult.ProcessingNotes = $"Duplicate of scan {duplicateOf.Id}";
            scanResult.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return MapToResponse(scanResult);
        }

        var ocrProvider = _configuration["Ocr:Provider"]?.ToLower();
        OcrExtractedData? extractedData = null;

        try
        {
            string extractedText;

            if (ocrProvider == "local")
            {
                var localResult = await ExtractWithLocalServiceAsync(file);
                extractedText = localResult.RawText;
                extractedData = localResult.Data;
            }
            else
            {
                extractedText = await ExtractTextAsync(file);
                extractedData = ParseThaiDocument(extractedText);
            }

            scanResult.DocumentType = extractedData.DocumentType;
            scanResult.Confidence = extractedData.Confidence;
            scanResult.ExtractedDocumentNumber = extractedData.DocumentNumber;
            scanResult.ExtractedDate = extractedData.DocumentDate;
            scanResult.ExtractedVendorName = extractedData.VendorName;
            scanResult.ExtractedVendorTaxId = extractedData.VendorTaxId;
            scanResult.ExtractedSubTotal = extractedData.SubTotal;
            scanResult.ExtractedVatAmount = extractedData.VatAmount;
            scanResult.ExtractedTotalAmount = extractedData.TotalAmount;
            scanResult.RawTextContent = extractedText;
            scanResult.ScanStatus = "Completed";
            scanResult.ProcessedAt = DateTime.UtcNow;

            // Store extracted items and account suggestions
            if (extractedData.Items.Count > 0)
            {
                scanResult.ExtractedItemsJson = System.Text.Json.JsonSerializer.Serialize(
                    extractedData.Items.Select(i => new { i.Description, i.Quantity, i.UnitPrice, i.Amount, i.SuggestedAccountCode }));
            }

            // Match GL accounts from suggestions against company's chart of accounts
            if (!string.IsNullOrEmpty(extractedData.DebitAccountCode))
            {
                var debitAccount = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == extractedData.DebitAccountCode && !a.IsDeleted);
                if (debitAccount != null)
                {
                    extractedData.DebitAccountCode = debitAccount.AccountCode;
                    extractedData.DebitAccountName = debitAccount.AccountName;
                }
            }
            if (!string.IsNullOrEmpty(extractedData.CreditAccountCode))
            {
                var creditAccount = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == extractedData.CreditAccountCode && !a.IsDeleted);
                if (creditAccount != null)
                {
                    extractedData.CreditAccountCode = creditAccount.AccountCode;
                    extractedData.CreditAccountName = creditAccount.AccountName;
                }
            }

            // Check for duplicate by document number + amount
            if (!string.IsNullOrEmpty(extractedData.DocumentNumber) && extractedData.TotalAmount.HasValue)
            {
                var docDuplicate = await _db.Set<OcrScanResult>()
                    .FirstOrDefaultAsync(r => r.CompanyId == companyId
                        && r.Id != scanResult.Id
                        && r.ExtractedDocumentNumber == extractedData.DocumentNumber
                        && r.ExtractedTotalAmount == extractedData.TotalAmount
                        && r.ScanStatus == "Completed");
                if (docDuplicate != null)
                {
                    scanResult.IsDuplicate = true;
                    scanResult.DuplicateOfScanId = docDuplicate.Id;
                    scanResult.ProcessingNotes = $"Possible duplicate: same doc number {extractedData.DocumentNumber} and amount {extractedData.TotalAmount}";
                }
            }

            if (!string.IsNullOrEmpty(extractedData.VendorTaxId))
            {
                var matchedContact = await _db.Contacts
                    .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.TaxId == extractedData.VendorTaxId && !c.IsDeleted);
                scanResult.MatchedContactId = matchedContact?.Id;
            }

            if (!scanResult.MatchedContactId.HasValue && !string.IsNullOrEmpty(extractedData.VendorName))
            {
                var matchedContact = await _db.Contacts
                    .FirstOrDefaultAsync(c => c.CompanyId == companyId
                        && c.Name.Contains(extractedData.VendorName) && !c.IsDeleted);
                scanResult.MatchedContactId = matchedContact?.Id;
            }

            // Auto-create contact if not found but we have vendor info
            if (!scanResult.MatchedContactId.HasValue
                && (!string.IsNullOrEmpty(extractedData.VendorName) || !string.IsNullOrEmpty(extractedData.VendorTaxId)))
            {
                var newContact = new Contact
                {
                    CompanyId = companyId,
                    Name = extractedData.VendorName ?? $"ผู้ขาย (TaxID: {extractedData.VendorTaxId})",
                    TaxId = extractedData.VendorTaxId,
                    IsCustomer = false,
                    IsSupplier = true,
                    CreatedBy = "OCR-AutoCreate"
                };
                _db.Contacts.Add(newContact);
                await _db.SaveChangesAsync();
                scanResult.MatchedContactId = newContact.Id;
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + $" Auto-created contact: {newContact.Name}";
            }

            // Auto-create document if confidence >= threshold (85%)
            var autoCreateThreshold = decimal.TryParse(_configuration["Ocr:AutoCreateThreshold"], out var t) ? t : 0.85m;
            if (scanResult.Confidence >= autoCreateThreshold
                && scanResult.MatchedContactId.HasValue
                && !scanResult.IsDuplicate
                && !scanResult.CreatedDocumentId.HasValue)
            {
                try
                {
                    await AutoCreateDocumentAsync(companyId, scanResult, extractedData);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Auto-create document failed for scan {ScanId}", scanResult.Id);
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + $" Auto-create failed: {ex.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR processing failed for file {FileId}", fileAttachmentId);
            scanResult.ScanStatus = "Failed";
            scanResult.ProcessedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return MapToResponse(scanResult, ocrProvider == "local" ? extractedData : null);
    }

    private async Task<(string RawText, OcrExtractedData Data)> ExtractWithLocalServiceAsync(FileAttachment file)
    {
        var serviceUrl = _configuration["Ocr:LocalServiceUrl"] ?? "http://localhost:8501";
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(120);

        byte[] fileData;
        try { fileData = await File.ReadAllBytesAsync(file.StoragePath); }
        catch { return (ExtractFromFileName(file), new OcrExtractedData { Confidence = 0.3m }); }

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileData);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            file.ContentType ?? "application/octet-stream");
        form.Add(fileContent, "file", file.OriginalFileName);

        try
        {
            var response = await client.PostAsync($"{serviceUrl}/ocr/extract", form);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Local OCR service returned {Status}", response.StatusCode);
                return (ExtractFromFileName(file), new OcrExtractedData { Confidence = 0.3m });
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var data = new OcrExtractedData
            {
                DocumentType = root.TryGetProperty("document_type", out var dt) ? dt.GetString() : null,
                Confidence = root.TryGetProperty("confidence", out var cf) ? (decimal)cf.GetDouble() : 0.5m,
                VendorName = root.TryGetProperty("vendor_name", out var vn) ? vn.GetString() : null,
                VendorTaxId = root.TryGetProperty("vendor_tax_id", out var vt) ? vt.GetString() : null,
                DocumentNumber = root.TryGetProperty("document_number", out var dn) ? dn.GetString() : null,
                SubTotal = root.TryGetProperty("subtotal", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)st.GetDouble() : null,
                VatAmount = root.TryGetProperty("vat_amount", out var va) && va.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)va.GetDouble() : null,
                TotalAmount = root.TryGetProperty("total_amount", out var ta) && ta.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)ta.GetDouble() : null,
                ExpenseCategory = root.TryGetProperty("expense_category", out var ec) ? ec.GetString() : null,
                HasWht = root.TryGetProperty("has_wht", out var hw) && hw.ValueKind == System.Text.Json.JsonValueKind.True,
                WhtRate = root.TryGetProperty("wht_rate", out var wr) && wr.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)wr.GetDouble() : null,
                PaymentTermsDays = root.TryGetProperty("payment_terms_days", out var pt) && pt.ValueKind == System.Text.Json.JsonValueKind.Number ? pt.GetInt32() : null,
            };

            if (root.TryGetProperty("document_date", out var dd) && dd.GetString() is string dateStr && DateTime.TryParse(dateStr, out var parsedDate))
                data.DocumentDate = parsedDate;

            // Parse suggested accounts
            if (root.TryGetProperty("suggested_accounts", out var sa) && sa.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                data.DebitAccountCode = sa.TryGetProperty("debit_account_code", out var dac) ? dac.GetString() : null;
                data.DebitAccountName = sa.TryGetProperty("debit_account_name", out var dan) ? dan.GetString() : null;
                data.CreditAccountCode = sa.TryGetProperty("credit_account_code", out var cac) ? cac.GetString() : null;
                data.CreditAccountName = sa.TryGetProperty("credit_account_name", out var can) ? can.GetString() : null;
                data.VatAccountCode = sa.TryGetProperty("vat_account_code", out var vac) ? vac.GetString() : null;
                data.VatAccountName = sa.TryGetProperty("vat_account_name", out var van) ? van.GetString() : null;
            }

            // Parse line items
            if (root.TryGetProperty("items", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    data.Items.Add(new OcrExtractedLineItem
                    {
                        Description = item.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                        Quantity = item.TryGetProperty("quantity", out var qty) && qty.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)qty.GetDouble() : null,
                        UnitPrice = item.TryGetProperty("unit_price", out var up) && up.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)up.GetDouble() : null,
                        Amount = item.TryGetProperty("amount", out var amt) && amt.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)amt.GetDouble() : null,
                        SuggestedAccountCode = item.TryGetProperty("suggested_account_code", out var sac) ? sac.GetString() : null,
                    });
                }
            }

            var rawText = root.TryGetProperty("raw_text", out var rt) ? rt.GetString() ?? "" : "";
            return (rawText, data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Local OCR service unavailable, falling back to built-in");
            var text = await ExtractTextAsync(file);
            return (text, ParseThaiDocument(text));
        }
    }

    public async Task SubmitCorrectionAsync(Guid companyId, Guid scanResultId, OcrCorrectionRequest correction)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        // Update the scan result with corrected data
        if (correction.DocumentType != null) result.DocumentType = correction.DocumentType;
        if (correction.VendorName != null) result.ExtractedVendorName = correction.VendorName;
        if (correction.VendorTaxId != null) result.ExtractedVendorTaxId = correction.VendorTaxId;
        if (correction.DocumentNumber != null) result.ExtractedDocumentNumber = correction.DocumentNumber;
        if (correction.DocumentDate.HasValue) result.ExtractedDate = correction.DocumentDate;
        if (correction.SubTotal.HasValue) result.ExtractedSubTotal = correction.SubTotal;
        if (correction.VatAmount.HasValue) result.ExtractedVatAmount = correction.VatAmount;
        if (correction.TotalAmount.HasValue) result.ExtractedTotalAmount = correction.TotalAmount;

        await _db.SaveChangesAsync();

        // Forward correction to local AI service for learning
        var serviceUrl = _configuration["Ocr:LocalServiceUrl"] ?? "http://localhost:8501";
        try
        {
            var client = _httpClientFactory.CreateClient();
            var payload = new
            {
                original_text = result.RawTextContent ?? "",
                original_result = new { document_type = result.DocumentType, confidence = result.Confidence },
                corrected_result = new
                {
                    document_type = correction.DocumentType ?? result.DocumentType,
                    vendor_name = correction.VendorName ?? result.ExtractedVendorName,
                    vendor_tax_id = correction.VendorTaxId ?? result.ExtractedVendorTaxId,
                    document_number = correction.DocumentNumber ?? result.ExtractedDocumentNumber,
                    document_date = (correction.DocumentDate ?? result.ExtractedDate)?.ToString("yyyy-MM-dd"),
                    subtotal = correction.SubTotal ?? result.ExtractedSubTotal,
                    vat_amount = correction.VatAmount ?? result.ExtractedVatAmount,
                    total_amount = correction.TotalAmount ?? result.ExtractedTotalAmount,
                },
                document_type = correction.DocumentType ?? result.DocumentType
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            await client.PostAsync($"{serviceUrl}/ocr/correct", content);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit correction to learning service");
        }
    }

    private async Task<string> ExtractTextAsync(FileAttachment file)
    {
        var ocrProvider = _configuration["Ocr:Provider"];
        var apiKey = _configuration["Ocr:ApiKey"];

        if (!string.IsNullOrEmpty(ocrProvider) && !string.IsNullOrEmpty(apiKey))
        {
            return ocrProvider.ToLower() switch
            {
                "google" => await ExtractWithGoogleVisionAsync(file, apiKey),
                "azure" => await ExtractWithAzureOcrAsync(file, apiKey),
                "tesseract" => await ExtractWithTesseractAsync(file),
                _ => ExtractFromFileName(file)
            };
        }

        return ExtractFromFileName(file);
    }

    private async Task<string> ExtractWithGoogleVisionAsync(FileAttachment file, string apiKey)
    {
        var client = _httpClientFactory.CreateClient();
        var url = $"https://vision.googleapis.com/v1/images:annotate?key={apiKey}";

        byte[] fileData;
        try { fileData = await File.ReadAllBytesAsync(file.StoragePath); }
        catch { return ExtractFromFileName(file); }
        var base64 = Convert.ToBase64String(fileData);

        var payload = new
        {
            requests = new[]
            {
                new
                {
                    image = new { content = base64 },
                    features = new[] { new { type = "TEXT_DETECTION" } }
                }
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await client.PostAsync(url, content);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Google Vision API returned {Status}", response.StatusCode);
            return ExtractFromFileName(file);
        }

        var result = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(result);
        var annotations = doc.RootElement.GetProperty("responses")[0];
        if (annotations.TryGetProperty("textAnnotations", out var texts) && texts.GetArrayLength() > 0)
            return texts[0].GetProperty("description").GetString() ?? "";

        return "";
    }

    private async Task<string> ExtractWithAzureOcrAsync(FileAttachment file, string apiKey)
    {
        var endpoint = _configuration["Ocr:AzureEndpoint"] ?? "";
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", apiKey);

        byte[] fileData;
        try { fileData = await File.ReadAllBytesAsync(file.StoragePath); }
        catch { return ExtractFromFileName(file); }
        var content = new ByteArrayContent(fileData);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");

        var response = await client.PostAsync($"{endpoint}/vision/v3.2/read/analyze", content);
        if (!response.IsSuccessStatusCode)
            return ExtractFromFileName(file);

        var resultUrl = response.Headers.GetValues("Operation-Location").FirstOrDefault();
        if (string.IsNullOrEmpty(resultUrl)) return "";

        await Task.Delay(2000);
        var readResult = await client.GetStringAsync(resultUrl);
        using var doc = System.Text.Json.JsonDocument.Parse(readResult);
        var pages = doc.RootElement.GetProperty("analyzeResult").GetProperty("readResults");
        var text = string.Join("\n", pages.EnumerateArray()
            .SelectMany(p => p.GetProperty("lines").EnumerateArray())
            .Select(l => l.GetProperty("text").GetString()));

        return text;
    }

    private Task<string> ExtractWithTesseractAsync(FileAttachment file)
    {
        return Task.FromResult(ExtractFromFileName(file));
    }

    private static string ExtractFromFileName(FileAttachment file)
    {
        return file.OriginalFileName ?? "";
    }

    private static OcrExtractedData ParseThaiDocument(string text)
    {
        var data = new OcrExtractedData();

        if (string.IsNullOrWhiteSpace(text))
        {
            data.DocumentType = "Receipt";
            data.Confidence = 0.3m;
            return data;
        }

        // Document type detection
        if (text.Contains("ใบกำกับภาษี") || text.Contains("TAX INVOICE"))
        {
            data.DocumentType = "TaxInvoice";
            data.Confidence = 0.95m;
        }
        else if (text.Contains("ใบแจ้ง��นี้") || text.Contains("INVOICE"))
        {
            data.DocumentType = "Invoice";
            data.Confidence = 0.90m;
        }
        else if (text.Contains("ใบเสร็จรับเงิน") || text.Contains("RECEIPT"))
        {
            data.DocumentType = "Receipt";
            data.Confidence = 0.88m;
        }
        else
        {
            data.DocumentType = "Receipt";
            data.Confidence = 0.6m;
        }

        // Tax ID extraction (13-digit)
        var taxIdMatch = Regex.Match(text, @"\b(\d{1}[-\s]?\d{4}[-\s]?\d{5}[-\s]?\d{2}[-\s]?\d{1})\b");
        if (taxIdMatch.Success)
            data.VendorTaxId = Regex.Replace(taxIdMatch.Groups[1].Value, @"[-\s]", "");

        // Document number
        var docNumMatch = Regex.Match(text, @"(?:เลขที่|No\.?|INV|IV|RE|TX)[\s:]*([A-Z0-9][-A-Z0-9/]+)", RegexOptions.IgnoreCase);
        if (docNumMatch.Success)
            data.DocumentNumber = docNumMatch.Groups[1].Value.Trim();

        // Date (Thai format: dd/mm/yyyy or dd-mm-yyyy)
        var dateMatch = Regex.Match(text, @"(\d{1,2})[/\-.](\d{1,2})[/\-.](\d{2,4})");
        if (dateMatch.Success)
        {
            int day = int.Parse(dateMatch.Groups[1].Value);
            int month = int.Parse(dateMatch.Groups[2].Value);
            int year = int.Parse(dateMatch.Groups[3].Value);
            if (year > 2500) year -= 543; // Convert Buddhist Era
            if (year < 100) year += 2000;
            if (day >= 1 && day <= 31 && month >= 1 && month <= 12)
                data.DocumentDate = new DateTime(year, month, day);
        }

        // Amount patterns (Thai: ยอดรวม, รวมทั้งสิ้น, TOTAL)
        var totalMatch = Regex.Match(text, @"(?:ยอดรวม|รวมทั้งสิ้น|รวมเงิน|TOTAL|Grand\s*Total)[\s:]*([0-9,]+\.?\d*)", RegexOptions.IgnoreCase);
        if (totalMatch.Success)
            data.TotalAmount = ParseDecimal(totalMatch.Groups[1].Value);

        // VAT amount
        var vatMatch = Regex.Match(text, @"(?:ภาษีมูลค่าเพิ่ม|VAT|ภาษี\s*7%)[\s:]*([0-9,]+\.?\d*)", RegexOptions.IgnoreCase);
        if (vatMatch.Success)
            data.VatAmount = ParseDecimal(vatMatch.Groups[1].Value);

        // SubTotal
        var subMatch = Regex.Match(text, @"(?:ราคาสินค้า|ก่อนภาษี|Subtotal|Sub\s*Total)[\s:]*([0-9,]+\.?\d*)", RegexOptions.IgnoreCase);
        if (subMatch.Success)
            data.SubTotal = ParseDecimal(subMatch.Groups[1].Value);

        // Calculate missing values
        if (data.TotalAmount > 0 && data.VatAmount > 0 && data.SubTotal == null)
            data.SubTotal = data.TotalAmount - data.VatAmount;
        else if (data.SubTotal > 0 && data.VatAmount > 0 && data.TotalAmount == null)
            data.TotalAmount = data.SubTotal + data.VatAmount;
        else if (data.TotalAmount > 0 && data.SubTotal == null && data.VatAmount == null)
        {
            data.SubTotal = Math.Round(data.TotalAmount.Value / 1.07m, 2);
            data.VatAmount = data.TotalAmount.Value - data.SubTotal.Value;
        }

        // Vendor name: look for company-like names
        var vendorMatch = Regex.Match(text, @"(?:บริษัท|ห้างหุ้นส่วน|ร้าน)\s+(.+?)(?:\s*จำกัด|\s*\(|$)", RegexOptions.Multiline);
        if (vendorMatch.Success)
            data.VendorName = vendorMatch.Groups[1].Value.Trim() + (text.Contains("จำกัด") ? " จำกัด" : "");

        return data;
    }

    private static decimal? ParseDecimal(string value)
    {
        var cleaned = value.Replace(",", "").Trim();
        return decimal.TryParse(cleaned, out var result) ? result : null;
    }

    public async Task<OcrResultResponse> GetResultAsync(Guid companyId, Guid scanResultId)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        return MapToResponse(result);
    }

    public async Task<PagedResponse<OcrResultResponse>> GetResultsAsync(Guid companyId, string? status, PagedRequest request)
    {
        var query = _db.Set<OcrScanResult>()
            .Where(r => r.CompanyId == companyId);

        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(r => r.ScanStatus == status);

        if (!string.IsNullOrWhiteSpace(request.Search))
            query = query.Where(r => r.OriginalFileName.Contains(request.Search)
                                  || (r.ExtractedVendorName != null && r.ExtractedVendorName.Contains(request.Search))
                                  || (r.ExtractedDocumentNumber != null && r.ExtractedDocumentNumber.Contains(request.Search)));

        var totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(r => MapToResponse(r))
            .ToListAsync();

        return new PagedResponse<OcrResultResponse>(
            items, totalCount, request.Page, request.PageSize,
            (int)Math.Ceiling(totalCount / (double)request.PageSize));
    }

    public async Task<OcrResultResponse> CreateDocumentFromScanAsync(Guid companyId, Guid scanResultId, string createdBy)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        if (result.ScanStatus != "Completed")
            throw new InvalidOperationException("OCR scan is not yet completed.");

        if (result.CreatedDocumentId.HasValue)
            throw new InvalidOperationException("A document has already been created from this scan.");

        // Determine the document type from OCR result
        var docType = result.DocumentType switch
        {
            "Invoice" or "TaxInvoice" => DocumentType.PurchaseInvoice,
            "Receipt" => DocumentType.Expense,
            _ => DocumentType.Expense
        };

        // Resolve contact if matched
        Guid? contactId = result.MatchedContactId;
        if (!contactId.HasValue && !string.IsNullOrWhiteSpace(result.ExtractedVendorTaxId))
        {
            var contact = await _db.Contacts
                .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.TaxId == result.ExtractedVendorTaxId);
            contactId = contact?.Id;
        }

        if (!contactId.HasValue && !string.IsNullOrWhiteSpace(result.ExtractedVendorName))
        {
            // Create a new supplier contact from extracted data
            var newContact = new Contact
            {
                CompanyId = companyId,
                Name = result.ExtractedVendorName,
                TaxId = result.ExtractedVendorTaxId,
                IsCustomer = false,
                IsSupplier = true,
                CreatedBy = createdBy
            };
            _db.Contacts.Add(newContact);
            contactId = newContact.Id;
        }

        if (!contactId.HasValue)
            throw new InvalidOperationException("Cannot create document: no contact could be resolved from OCR data.");

        var document = new Document
        {
            CompanyId = companyId,
            DocumentNumber = $"OCR-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}",
            DocumentType = docType,
            Status = DocumentStatus.Draft,
            DocumentDate = result.ExtractedDate ?? DateTime.UtcNow.Date,
            ContactId = contactId.Value,
            SubTotal = result.ExtractedSubTotal ?? 0,
            VatAmount = result.ExtractedVatAmount ?? 0,
            TotalAmount = result.ExtractedTotalAmount ?? 0,
            BalanceDue = result.ExtractedTotalAmount ?? 0,
            Reference = result.ExtractedDocumentNumber,
            Notes = $"Created from OCR scan: {result.OriginalFileName}",
            CreatedBy = createdBy
        };

        _db.Documents.Add(document);

        result.CreatedDocumentId = document.Id;
        result.MatchedContactId = contactId;

        await _db.SaveChangesAsync();

        return MapToResponse(result);
    }

    public async Task<OcrResultResponse> MatchContactAsync(Guid companyId, Guid scanResultId, Guid contactId)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        var contact = await _db.Contacts
            .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.Id == contactId)
            ?? throw new InvalidOperationException("Contact not found.");

        result.MatchedContactId = contactId;
        await _db.SaveChangesAsync();

        return MapToResponse(result);
    }

    private async Task AutoCreateDocumentAsync(Guid companyId, OcrScanResult scan, OcrExtractedData? extractedData = null)
    {
        var docType = scan.DocumentType switch
        {
            "Invoice" or "TaxInvoice" => DocumentType.PurchaseInvoice,
            "Receipt" => DocumentType.Expense,
            "CreditNote" => DocumentType.CreditNote,
            "DebitNote" => DocumentType.DebitNote,
            _ => DocumentType.Expense
        };

        // Resolve expense account from suggestions
        Guid? expenseAccountId = null;
        if (extractedData?.DebitAccountCode != null)
        {
            var account = await _db.ChartOfAccounts
                .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == extractedData.DebitAccountCode && !a.IsDeleted);
            expenseAccountId = account?.Id;
        }

        var document = new Document
        {
            CompanyId = companyId,
            DocumentNumber = $"OCR-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..6].ToUpper()}",
            DocumentType = docType,
            Status = DocumentStatus.Draft,
            DocumentDate = scan.ExtractedDate ?? DateTime.UtcNow.Date,
            ContactId = scan.MatchedContactId!.Value,
            SubTotal = scan.ExtractedSubTotal ?? 0,
            VatAmount = scan.ExtractedVatAmount ?? 0,
            TotalAmount = scan.ExtractedTotalAmount ?? 0,
            BalanceDue = scan.ExtractedTotalAmount ?? 0,
            Reference = scan.ExtractedDocumentNumber,
            Notes = $"Auto-created from OCR scan (confidence: {scan.Confidence:P0}): {scan.OriginalFileName}",
            CreatedBy = "OCR-AutoCreate"
        };

        // Create document lines from extracted items or a single line
        if (extractedData?.Items.Count > 0)
        {
            int lineOrder = 1;
            foreach (var item in extractedData.Items)
            {
                Guid? lineAccountId = expenseAccountId;
                if (!string.IsNullOrEmpty(item.SuggestedAccountCode))
                {
                    var lineAccount = await _db.ChartOfAccounts
                        .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == item.SuggestedAccountCode && !a.IsDeleted);
                    if (lineAccount != null) lineAccountId = lineAccount.Id;
                }

                document.Lines.Add(new DocumentLine
                {
                    LineOrder = lineOrder++,
                    Description = item.Description ?? scan.DocumentType ?? "รายการจาก OCR",
                    Quantity = item.Quantity ?? 1,
                    UnitPrice = item.UnitPrice ?? item.Amount ?? 0,
                    Amount = item.Amount ?? 0,
                    VatRate = scan.ExtractedVatAmount > 0 ? 7 : 0,
                    AccountId = lineAccountId,
                });
            }
        }
        else
        {
            document.Lines.Add(new DocumentLine
            {
                LineOrder = 1,
                Description = extractedData?.ExpenseCategory ?? scan.DocumentType ?? "รายการจาก OCR",
                Quantity = 1,
                UnitPrice = scan.ExtractedSubTotal ?? scan.ExtractedTotalAmount ?? 0,
                Amount = scan.ExtractedSubTotal ?? scan.ExtractedTotalAmount ?? 0,
                VatRate = scan.ExtractedVatAmount > 0 ? 7 : 0,
                VatAmount = scan.ExtractedVatAmount ?? 0,
                WithholdingTaxRate = extractedData?.HasWht == true && extractedData.WhtRate.HasValue ? extractedData.WhtRate.Value : 0,
                AccountId = expenseAccountId,
            });
        }

        _db.Documents.Add(document);
        scan.CreatedDocumentId = document.Id;
        scan.ProcessingNotes = (scan.ProcessingNotes ?? "") + " Auto-created document with lines.";
        await _db.SaveChangesAsync();
    }

    private OcrResultResponse MapToResponse(OcrScanResult r, OcrExtractedData? data = null)
    {
        OcrSuggestedAccountsDto? suggestedAccounts = null;
        List<OcrLineItemDto>? items = null;

        if (data != null)
        {
            if (data.DebitAccountCode != null || data.CreditAccountCode != null)
            {
                suggestedAccounts = new OcrSuggestedAccountsDto(
                    data.DebitAccountCode, data.DebitAccountName,
                    data.CreditAccountCode, data.CreditAccountName,
                    data.VatAccountCode, data.VatAccountName);
            }
            if (data.Items.Count > 0)
            {
                items = data.Items.Select(i => new OcrLineItemDto(
                    i.Description, i.Quantity, i.UnitPrice, i.Amount, i.SuggestedAccountCode)).ToList();
            }
        }

        return new OcrResultResponse(
            r.Id, r.OriginalFileName, r.ScanStatus, r.DocumentType, r.Confidence,
            r.ExtractedVendorName, r.ExtractedVendorTaxId, r.ExtractedDocumentNumber,
            r.ExtractedDate, r.ExtractedSubTotal, r.ExtractedVatAmount, r.ExtractedTotalAmount,
            r.MatchedContactId, r.CreatedDocumentId, r.ProcessedAt,
            r.IsDuplicate, r.DuplicateOfScanId, r.FileHash, r.ProcessingNotes,
            data?.ExpenseCategory, suggestedAccounts,
            data?.HasWht ?? false, data?.WhtRate,
            data?.PaymentTermsDays, items);
    }
}

internal class OcrExtractedData
{
    public string DocumentType { get; set; } = "Receipt";
    public decimal Confidence { get; set; }
    public string? DocumentNumber { get; set; }
    public DateTime? DocumentDate { get; set; }
    public string? VendorName { get; set; }
    public string? VendorTaxId { get; set; }
    public decimal? SubTotal { get; set; }
    public decimal? VatAmount { get; set; }
    public decimal? TotalAmount { get; set; }
    public string? ExpenseCategory { get; set; }
    public string? DebitAccountCode { get; set; }
    public string? DebitAccountName { get; set; }
    public string? CreditAccountCode { get; set; }
    public string? CreditAccountName { get; set; }
    public string? VatAccountCode { get; set; }
    public string? VatAccountName { get; set; }
    public bool HasWht { get; set; }
    public decimal? WhtRate { get; set; }
    public int? PaymentTermsDays { get; set; }
    public List<OcrExtractedLineItem> Items { get; set; } = new();
}

internal class OcrExtractedLineItem
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Amount { get; set; }
    public string? SuggestedAccountCode { get; set; }
}
