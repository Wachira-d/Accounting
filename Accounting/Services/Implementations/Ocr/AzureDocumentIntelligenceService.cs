using System.Text.Json;
using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Azure Document Intelligence v4.0 client.
/// Calls prebuilt-invoice / prebuilt-receipt models, polls until done,
/// and maps Azure's structured response into the project's OcrExtractedData.
/// </summary>
public class AzureDocumentIntelligenceService
{
    private readonly AccountingDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AzureDocumentIntelligenceService> _logger;

    public AzureDocumentIntelligenceService(
        AccountingDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<AzureDocumentIntelligenceService> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Analyze a document using Azure DI. Caller may pass pre-loaded SiteSettings to
    /// avoid a redundant DB roundtrip (OcrService.ScanAsync already loads it).
    /// </summary>
    public async Task<AzureDiResult?> AnalyzeAsync(byte[] fileBytes, string contentType,
        Models.Entities.SiteSettings? settings = null, CancellationToken ct = default)
    {
        settings ??= await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings == null || !settings.AzureDiEnabled
            || string.IsNullOrEmpty(settings.AzureDiEndpoint)
            || string.IsNullOrEmpty(settings.AzureDiApiKey))
        {
            return null;
        }

        var endpoint = settings.AzureDiEndpoint.TrimEnd('/');
        var apiKey = settings.AzureDiApiKey;
        var modelId = string.IsNullOrEmpty(settings.AzureDiModelId) ? "prebuilt-invoice" : settings.AzureDiModelId;
        var apiVersion = string.IsNullOrEmpty(settings.AzureDiApiVersion) ? "2024-11-30" : settings.AzureDiApiVersion;

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", apiKey);
        client.Timeout = TimeSpan.FromMinutes(2);

        var analyzeUrl = $"{endpoint}/documentintelligence/documentModels/{modelId}:analyze?api-version={apiVersion}";
        using var content = new ByteArrayContent(fileBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);

        HttpResponseMessage submitResponse;
        try
        {
            submitResponse = await client.PostAsync(analyzeUrl, content, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure DI submit failed");
            return new AzureDiResult { Success = false, ErrorMessage = ex.Message };
        }

        if (submitResponse.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            var body = await submitResponse.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("Azure DI submit returned {Status}: {Body}", submitResponse.StatusCode, body);
            return new AzureDiResult { Success = false, ErrorMessage = $"HTTP {(int)submitResponse.StatusCode}: {body}" };
        }

        var operationLocation = submitResponse.Headers.GetValues("Operation-Location").FirstOrDefault();
        if (string.IsNullOrEmpty(operationLocation))
            return new AzureDiResult { Success = false, ErrorMessage = "Missing Operation-Location header" };

        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(attempt < 3 ? 1500 : 3000), ct);

            HttpResponseMessage pollResponse;
            try
            {
                pollResponse = await client.GetAsync(operationLocation, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Azure DI poll attempt {Attempt} failed", attempt);
                continue;
            }

            if (!pollResponse.IsSuccessStatusCode)
            {
                var body = await pollResponse.Content.ReadAsStringAsync(ct);
                return new AzureDiResult { Success = false, ErrorMessage = $"Poll HTTP {(int)pollResponse.StatusCode}: {body}" };
            }

            var json = await pollResponse.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (status == "succeeded")
                return ParseResult(root, modelId);
            if (status == "failed")
            {
                var err = root.TryGetProperty("error", out var e) ? e.ToString() : "unknown";
                return new AzureDiResult { Success = false, ErrorMessage = $"Analysis failed: {err}" };
            }
        }

        return new AzureDiResult { Success = false, ErrorMessage = "Polling timed out after 2 minutes" };
    }

    private AzureDiResult ParseResult(JsonElement root, string modelId)
    {
        var result = new AzureDiResult { Success = true, ModelId = modelId };

        if (!root.TryGetProperty("analyzeResult", out var analyze))
            return result;

        // Raw text content
        if (analyze.TryGetProperty("content", out var content))
            result.RawText = content.GetString() ?? "";

        if (!analyze.TryGetProperty("documents", out var documents) || documents.GetArrayLength() == 0)
            return result;

        // Multi-document PDF warning — Azure DI splits multi-invoice PDFs into multiple
        // documents, but we only book the first. Surface this so users know to split
        // the PDF and re-upload pages individually if there are 2+ invoices in one file.
        result.MultiDocumentCount = documents.GetArrayLength();
        if (result.MultiDocumentCount > 1)
        {
            result.Warnings.Add($"พบ {result.MultiDocumentCount} เอกสารใน PDF เดียว — ระบบประมวลผลเฉพาะเอกสารแรก กรุณาแยกไฟล์ก่อนอัปโหลด");
        }

        // Page count for transparency in processing notes
        if (analyze.TryGetProperty("pages", out var pagesArr))
            result.PageCount = pagesArr.GetArrayLength();

        var firstDoc = documents[0];

        if (firstDoc.TryGetProperty("confidence", out var conf))
            result.OverallConfidence = conf.GetDecimal();

        if (firstDoc.TryGetProperty("docType", out var docType))
            result.DocumentType = docType.GetString();

        if (firstDoc.TryGetProperty("fields", out var fields))
        {
            // Vendor / Merchant
            result.VendorName = GetStringField(fields, "VendorName") ?? GetStringField(fields, "MerchantName");
            result.VendorTaxId = GetStringField(fields, "VendorTaxId") ?? GetStringField(fields, "MerchantTaxId");
            result.VendorAddress = GetStringField(fields, "VendorAddress") ?? GetStringField(fields, "MerchantAddress");
            result.VendorPhone = GetStringField(fields, "VendorAddressRecipient") ?? GetStringField(fields, "MerchantPhoneNumber");

            // Customer / Buyer
            result.CustomerName = GetStringField(fields, "CustomerName") ?? GetStringField(fields, "BillingAddressRecipient");
            result.CustomerTaxId = GetStringField(fields, "CustomerTaxId");
            result.CustomerAddress = GetStringField(fields, "CustomerAddress") ?? GetStringField(fields, "BillingAddress");

            // Document
            result.InvoiceId = GetStringField(fields, "InvoiceId") ?? GetStringField(fields, "ReceiptNumber");
            result.PurchaseOrder = GetStringField(fields, "PurchaseOrder");
            result.InvoiceDate = GetDateField(fields, "InvoiceDate") ?? GetDateField(fields, "TransactionDate");
            result.DueDate = GetDateField(fields, "DueDate");

            // Money
            result.SubTotal = GetCurrencyField(fields, "SubTotal");
            result.TotalTax = GetCurrencyField(fields, "TotalTax") ?? GetCurrencyField(fields, "TotalVat");
            result.InvoiceTotal = GetCurrencyField(fields, "InvoiceTotal") ?? GetCurrencyField(fields, "Total");
            result.AmountDue = GetCurrencyField(fields, "AmountDue");
            result.PreviousUnpaidBalance = GetCurrencyField(fields, "PreviousUnpaidBalance");

            // Per-field confidence
            result.FieldConfidence = new Dictionary<string, decimal>();
            foreach (var fieldName in new[] { "VendorName", "VendorTaxId", "MerchantName", "CustomerName",
                "CustomerTaxId", "InvoiceId", "InvoiceDate", "SubTotal", "TotalTax", "InvoiceTotal" })
            {
                if (fields.TryGetProperty(fieldName, out var f) &&
                    f.TryGetProperty("confidence", out var c))
                {
                    result.FieldConfidence[fieldName] = c.GetDecimal();
                }
            }

            // Items
            if (fields.TryGetProperty("Items", out var items) && items.TryGetProperty("valueArray", out var itemArray))
            {
                foreach (var item in itemArray.EnumerateArray())
                {
                    if (!item.TryGetProperty("valueObject", out var itemObj)) continue;
                    var line = new AzureDiLineItem
                    {
                        Description = GetStringField(itemObj, "Description"),
                        Quantity = GetNumberField(itemObj, "Quantity"),
                        UnitPrice = GetCurrencyField(itemObj, "UnitPrice"),
                        Amount = GetCurrencyField(itemObj, "Amount"),
                        ProductCode = GetStringField(itemObj, "ProductCode"),
                    };
                    result.Items.Add(line);
                }
            }
        }

        return result;
    }

    private static string? GetStringField(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var f)) return null;
        if (f.TryGetProperty("valueString", out var v)) return v.GetString();
        if (f.TryGetProperty("content", out var c)) return c.GetString();
        return null;
    }

    private static DateTime? GetDateField(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var f)) return null;
        if (f.TryGetProperty("valueDate", out var v) && DateTime.TryParse(v.GetString(), out var dt))
            return dt;
        if (f.TryGetProperty("content", out var c) && DateTime.TryParse(c.GetString(), out var dt2))
            return dt2;
        return null;
    }

    private static decimal? GetCurrencyField(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var f)) return null;
        if (f.TryGetProperty("valueCurrency", out var cur) &&
            cur.TryGetProperty("amount", out var amt))
            return amt.GetDecimal();
        if (f.TryGetProperty("valueNumber", out var n))
            return n.GetDecimal();
        return null;
    }

    private static decimal? GetNumberField(JsonElement fields, string name)
    {
        if (!fields.TryGetProperty(name, out var f)) return null;
        if (f.TryGetProperty("valueNumber", out var n))
            return n.GetDecimal();
        return null;
    }
}

public class AzureDiResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ModelId { get; set; }
    public string? DocumentType { get; set; }
    public decimal OverallConfidence { get; set; }
    public string RawText { get; set; } = "";

    public string? VendorName { get; set; }
    public string? VendorTaxId { get; set; }
    public string? VendorAddress { get; set; }
    public string? VendorPhone { get; set; }

    public string? CustomerName { get; set; }
    public string? CustomerTaxId { get; set; }
    public string? CustomerAddress { get; set; }

    public string? InvoiceId { get; set; }
    public string? PurchaseOrder { get; set; }
    public DateTime? InvoiceDate { get; set; }
    public DateTime? DueDate { get; set; }

    public decimal? SubTotal { get; set; }
    public decimal? TotalTax { get; set; }
    public decimal? InvoiceTotal { get; set; }
    public decimal? AmountDue { get; set; }
    public decimal? PreviousUnpaidBalance { get; set; }

    public Dictionary<string, decimal> FieldConfidence { get; set; } = new();
    public List<AzureDiLineItem> Items { get; set; } = new();

    /// <summary>Number of separate "document objects" detected in the input file.
    /// > 1 indicates a multi-invoice PDF — only the first is processed.</summary>
    public int MultiDocumentCount { get; set; } = 0;
    public int PageCount { get; set; } = 0;
    public List<string> Warnings { get; set; } = new();
}

public class AzureDiLineItem
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Amount { get; set; }
    public string? ProductCode { get; set; }
}
