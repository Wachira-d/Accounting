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

        var scanResult = new OcrScanResult
        {
            CompanyId = companyId,
            FileAttachmentId = fileAttachmentId,
            OriginalFileName = file.OriginalFileName,
            ScanStatus = "Processing",
            Confidence = 0m
        };

        _db.Set<OcrScanResult>().Add(scanResult);
        await _db.SaveChangesAsync();

        try
        {
            var extractedText = await ExtractTextAsync(file);
            var extractedData = ParseThaiDocument(extractedText);

            scanResult.DocumentType = extractedData.DocumentType;
            scanResult.Confidence = extractedData.Confidence;
            scanResult.ExtractedDocumentNumber = extractedData.DocumentNumber;
            scanResult.ExtractedDate = extractedData.DocumentDate;
            scanResult.ExtractedVendorName = extractedData.VendorName;
            scanResult.ExtractedVendorTaxId = extractedData.VendorTaxId;
            scanResult.ExtractedSubTotal = extractedData.SubTotal;
            scanResult.ExtractedVatAmount = extractedData.VatAmount;
            scanResult.ExtractedTotalAmount = extractedData.TotalAmount;
            scanResult.ScanStatus = "Completed";
            scanResult.ProcessedAt = DateTime.UtcNow;

            if (!string.IsNullOrEmpty(extractedData.VendorTaxId))
            {
                var matchedContact = await _db.Contacts
                    .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.TaxId == extractedData.VendorTaxId);
                scanResult.MatchedContactId = matchedContact?.Id;
            }
            else if (!string.IsNullOrEmpty(extractedData.VendorName))
            {
                var matchedContact = await _db.Contacts
                    .FirstOrDefaultAsync(c => c.CompanyId == companyId
                        && c.Name.Contains(extractedData.VendorName));
                scanResult.MatchedContactId = matchedContact?.Id;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR processing failed for file {FileId}", fileAttachmentId);
            scanResult.ScanStatus = "Failed";
            scanResult.ProcessedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return MapToResponse(scanResult);
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

    private static OcrResultResponse MapToResponse(OcrScanResult r) => new(
        r.Id,
        r.OriginalFileName,
        r.ScanStatus,
        r.DocumentType,
        r.Confidence,
        r.ExtractedVendorName,
        r.ExtractedVendorTaxId,
        r.ExtractedDocumentNumber,
        r.ExtractedDate,
        r.ExtractedSubTotal,
        r.ExtractedVatAmount,
        r.ExtractedTotalAmount,
        r.MatchedContactId,
        r.CreatedDocumentId,
        r.ProcessedAt);
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
}
