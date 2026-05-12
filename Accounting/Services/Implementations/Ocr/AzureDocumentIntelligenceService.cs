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

    // ─── Batch rate-limiting ───
    // F0 (free) Azure DI pricing tier is capped at 20 transactions/min and
    // 1 concurrent request. Paid S0 is 15/sec. We cap concurrent submits
    // at MaxConcurrentSubmits (2 — conservative; one extra above F0's
    // strict 1 to ride out brief gaps without hammering). Static semaphore
    // is process-wide so multi-tenant batch uploads share the cap.
    //
    // The 429 handler below ALSO honors the Retry-After header / parsed
    // hint from the error body — so when admin overrides this to 5 for
    // a paid tier and still occasionally hits the limit, requests back
    // off instead of failing the whole upload batch.
    private const int MaxConcurrentSubmits = 2;
    private static readonly SemaphoreSlim _submitGate = new(MaxConcurrentSubmits, MaxConcurrentSubmits);

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
        Models.Entities.SiteSettings? settings = null, string? fileName = null,
        CancellationToken ct = default)
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
        var apiVersion = string.IsNullOrEmpty(settings.AzureDiApiVersion) ? "2024-11-30" : settings.AzureDiApiVersion;

        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", apiKey);
        client.Timeout = TimeSpan.FromMinutes(2);

        // ─── Per-file request planning ───
        // AzureDiRequestPlanner inspects size / format / dimensions /
        // filename and decides which model + locale + features + page
        // range to send for THIS particular file — instead of sending a
        // hardcoded one-size-fits-all set. Skips paying the
        // ocrHighResolution premium on clean PDFs, switches locale to
        // en-US on English-named files, caps multi-page PDFs to the
        // admin-configured MaxPagesPerScan, and chooses prebuilt-receipt
        // / prebuilt-invoice / prebuilt-businessCard based on filename.
        var plan = AzureDiRequestPlanner.Build(fileBytes, contentType, fileName, settings);
        var query = AzureDiRequestPlanner.BuildQueryString(plan, apiVersion);
        var modelId = plan.ModelId;
        _logger.LogInformation("Azure DI plan: {Plan}", string.Join("; ", plan.Reasons));

        var analyzeUrl = $"{endpoint}/documentintelligence/documentModels/{modelId}:analyze{query}";
        using var content = new ByteArrayContent(fileBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);

        // Throttle submits to MaxConcurrentSubmits to avoid the F0-tier
        // 1-concurrent-request limit. Batch uploads of 10 files no longer
        // burst all at once → no immediate 429 on the back of the queue.
        // Release as soon as Azure has accepted the submission — polling
        // doesn't need the submit slot.
        await _submitGate.WaitAsync(ct);
        HttpResponseMessage submitResponse;
        try
        {
            // 429-aware submit with up to 3 retries. Each retry honors
            // the Retry-After header (Azure returns this on 429) so we
            // wait exactly as long as Azure wants us to, no busy-loop.
            submitResponse = await PostWith429RetryAsync(client, analyzeUrl, fileBytes, contentType, ct);
        }
        catch (Exception ex)
        {
            _submitGate.Release();
            _logger.LogError(ex, "Azure DI submit failed");
            return new AzureDiResult { Success = false, ErrorMessage = ex.Message };
        }
        _submitGate.Release();

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

            // 429 on the poll endpoint — F0 tier hits this when many
            // tenants are scanning at once. Sleep for the Retry-After
            // duration Azure provided (capped at 60s) and try again
            // instead of failing the whole scan back to Tesseract.
            if ((int)pollResponse.StatusCode == 429)
            {
                var waitSec = ParseRetryAfter(pollResponse) ?? ExtractRetryFromBody(await pollResponse.Content.ReadAsStringAsync(ct)) ?? 10;
                waitSec = Math.Min(waitSec, 60);
                _logger.LogInformation("Azure DI poll 429 — backing off {Sec}s (attempt {Attempt})", waitSec, attempt);
                await Task.Delay(TimeSpan.FromSeconds(waitSec), ct);
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
            {
                var parsed = ParseResult(root, modelId);
                // Surface the request-planner reasons so the debug panel
                // shows exactly which params we chose for this file.
                if (parsed.Success)
                    parsed.Warnings.Insert(0, $"[RequestPlan] {string.Join(" | ", plan.Reasons)}");

                // ─── Layout fallback ───
                // When prebuilt-invoice returns an empty `documents` array
                // (no fields extracted at all — happens for non-standard
                // receipt layouts, multi-language docs, or scanned forms),
                // retry with prebuilt-layout to at least get the tables +
                // raw content for the SmartFieldExtractor to work on.
                // Single retry only — additional billable page, but the
                // alternative is a useless result.
                if (parsed.Success
                    && modelId.Equals("prebuilt-invoice", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrEmpty(parsed.VendorName)
                    && !parsed.InvoiceTotal.HasValue
                    && parsed.Items.Count == 0)
                {
                    _logger.LogInformation("Azure DI invoice model returned empty result — retrying with prebuilt-layout for {File}", fileName);
                    var layoutResult = await RetryWithLayoutAsync(endpoint, apiKey, apiVersion, fileBytes, contentType, plan, ct);
                    if (layoutResult != null && layoutResult.Success)
                    {
                        layoutResult.Warnings.Insert(0, "[Fallback] prebuilt-invoice returned empty → retried with prebuilt-layout");
                        return layoutResult;
                    }
                }
                return parsed;
            }
            if (status == "failed")
            {
                var err = root.TryGetProperty("error", out var e) ? e.ToString() : "unknown";
                return new AzureDiResult { Success = false, ErrorMessage = $"Analysis failed: {err}" };
            }
        }

        return new AzureDiResult { Success = false, ErrorMessage = "Polling timed out after 2 minutes" };
    }

    /// <summary>Retry the analyze call with prebuilt-layout when
    /// prebuilt-invoice returned an empty document. Layout always returns
    /// the raw content + tables even when no schema fields apply, so the
    /// SmartFieldExtractor at least has text to work with. Single retry,
    /// no further fallback — if layout also fails the caller falls down
    /// to the next cascade tier (Python / Tesseract).</summary>
    private async Task<AzureDiResult?> RetryWithLayoutAsync(
        string endpoint, string apiKey, string apiVersion, byte[] fileBytes,
        string contentType, AzureDiRequestPlanner.AnalysisPlan plan, CancellationToken ct)
    {
        var layoutPlan = plan with { ModelId = "prebuilt-layout", QueryFields = null };
        var query = AzureDiRequestPlanner.BuildQueryString(layoutPlan, apiVersion);
        var url = $"{endpoint}/documentintelligence/documentModels/prebuilt-layout:analyze{query}";

        var retryClient = _httpClientFactory.CreateClient();
        retryClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", apiKey);
        retryClient.Timeout = TimeSpan.FromMinutes(2);
        using var content = new ByteArrayContent(fileBytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
        try
        {
            var submitResp = await retryClient.PostAsync(url, content, ct);
            if (submitResp.StatusCode != System.Net.HttpStatusCode.Accepted) return null;
            var opLoc = submitResp.Headers.GetValues("Operation-Location").FirstOrDefault();
            if (string.IsNullOrEmpty(opLoc)) return null;
            for (var i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(i < 3 ? 1.5 : 3), ct);
                var pollResp = await retryClient.GetAsync(opLoc, ct);
                if (!pollResp.IsSuccessStatusCode) return null;
                var json = await pollResp.Content.ReadAsStringAsync(ct);
                using var docPolled = JsonDocument.Parse(json);
                var st = docPolled.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (st == "succeeded") return ParseResult(docPolled.RootElement, "prebuilt-layout");
                if (st == "failed") return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure DI layout retry failed");
        }
        return null;
    }

    private AzureDiResult ParseResult(JsonElement root, string modelId)
    {
        var result = new AzureDiResult { Success = true, ModelId = modelId };

        if (!root.TryGetProperty("analyzeResult", out var analyze))
            return result;

        // Raw text content
        if (analyze.TryGetProperty("content", out var content))
            result.RawText = content.GetString() ?? "";

        // ─── Key-value pairs from features=keyValuePairs ───
        // Generic K-V extractor catches Thai-anchored fields that don't
        // fit the standard Invoice schema. Common patterns we recover:
        //   "เลขประจำตัวผู้เสียภาษี: 0107544000043"
        //   "เลขที่: INV-2025-001"
        //   "วันที่: 31/01/2569"
        //   "ส่งถึง: ห้างหุ้นส่วน ..."
        if (analyze.TryGetProperty("keyValuePairs", out var kvPairs)
            && kvPairs.ValueKind == JsonValueKind.Array)
        {
            foreach (var kv in kvPairs.EnumerateArray())
            {
                if (!kv.TryGetProperty("key", out var key)
                    || !key.TryGetProperty("content", out var keyContent)) continue;
                var keyText = keyContent.GetString() ?? "";
                string? valueText = null;
                if (kv.TryGetProperty("value", out var val)
                    && val.TryGetProperty("content", out var valContent))
                    valueText = valContent.GetString();
                if (string.IsNullOrEmpty(keyText) || string.IsNullOrEmpty(valueText)) continue;
                result.KeyValuePairs[keyText.Trim()] = valueText.Trim();
            }
        }

        // ─── Barcodes / QR codes from features=barcodes ───
        // RD-issued e-Receipts carry a QR code that encodes the canonical
        // amount + tax ID — when present, it's the most reliable source.
        // Surfaces in result.Barcodes for downstream cross-validation.
        if (analyze.TryGetProperty("pages", out var pagesEl)
            && pagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pagesEl.EnumerateArray())
            {
                if (!page.TryGetProperty("barcodes", out var bcArr) || bcArr.ValueKind != JsonValueKind.Array) continue;
                foreach (var bc in bcArr.EnumerateArray())
                {
                    var kind = bc.TryGetProperty("kind", out var k) ? k.GetString() : null;
                    var bcValue = bc.TryGetProperty("value", out var v) ? v.GetString() : null;
                    if (!string.IsNullOrEmpty(bcValue))
                        result.Barcodes.Add(new AzureDiBarcode { Kind = kind, Value = bcValue });
                }
            }
        }

        // ─── Languages from features=languages ───
        if (analyze.TryGetProperty("languages", out var langArr)
            && langArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var lang in langArr.EnumerateArray())
            {
                var code = lang.TryGetProperty("locale", out var lc) ? lc.GetString() : null;
                if (!string.IsNullOrEmpty(code)) result.DetectedLanguages.Add(code);
            }
        }

        // ─── Handwriting detection from features=styleFont ───
        // analyzeResult.styles[] carries an "isHandwritten" boolean per
        // detected style range with a confidence. Aggregate count + max
        // confidence so downstream can flag amount fields for review.
        if (analyze.TryGetProperty("styles", out var stylesArr)
            && stylesArr.ValueKind == JsonValueKind.Array)
        {
            decimal maxConf = 0;
            int count = 0;
            foreach (var style in stylesArr.EnumerateArray())
            {
                var isHand = style.TryGetProperty("isHandwritten", out var ih) && ih.ValueKind == JsonValueKind.True;
                if (!isHand) continue;
                count++;
                if (style.TryGetProperty("confidence", out var cf) && cf.GetDecimal() > maxConf)
                    maxConf = cf.GetDecimal();
            }
            if (count > 0)
            {
                result.HandwrittenSpanCount = count;
                result.HandwrittenConfidence = maxConf;
                result.Warnings.Add($"[Handwriting] พบ {count} จุดที่เป็นลายมือ (conf {maxConf:P0}) — กรุณาตรวจสอบยอดเงิน");
            }
        }

        // ─── Selection marks (checkboxes / option marks) ───
        // Found per-page; we don't keep their position, just label+state.
        // Useful for RD tax forms (ภงด.1, 3, 53, ภพ.30) where the
        // checkbox state distinguishes filer types.
        if (analyze.TryGetProperty("pages", out var pagesArr2)
            && pagesArr2.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pagesArr2.EnumerateArray())
            {
                if (!page.TryGetProperty("selectionMarks", out var smArr)
                    || smArr.ValueKind != JsonValueKind.Array) continue;
                foreach (var mark in smArr.EnumerateArray())
                {
                    var state = mark.TryGetProperty("state", out var st) ? st.GetString() : null;
                    if (string.IsNullOrEmpty(state)) continue;
                    var markConf = mark.TryGetProperty("confidence", out var c) ? c.GetDecimal() : 0m;
                    result.SelectionMarks.Add(new AzureDiSelectionMark
                    {
                        State = state,
                        Confidence = markConf,
                        NearbyLabel = null,  // would require bounding-box pairing; skip for MVP
                    });
                }
            }
        }

        if (!analyze.TryGetProperty("documents", out var documents) || documents.GetArrayLength() == 0)
        {
            // Even when no structured "documents" came back, line items may
            // still be reconstructible from extracted tables — try fallback.
            TryExtractItemsFromTables(analyze, result);
            return result;
        }

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

        // ─── Fallback: extract line items from tables when the Invoice
        // model didn't surface any. Common for receipts / non-standard
        // layouts where the "Items" array is empty but the visual table
        // is parsed.
        if (result.Items.Count == 0)
            TryExtractItemsFromTables(analyze, result);

        return result;
    }

    /// <summary>
    /// Fallback when prebuilt-invoice returns no Items: parse the
    /// `analyzeResult.tables` array (from layout analysis) and treat the
    /// first table with ≥3 columns as the line-item table. Maps the
    /// columns heuristically by header keywords ("รายการ", "Description",
    /// "Qty", "ราคา", "Amount", ...) so it works on Thai layouts that
    /// don't conform to the invoice schema.
    /// </summary>
    private static void TryExtractItemsFromTables(JsonElement analyze, AzureDiResult result)
    {
        if (!analyze.TryGetProperty("tables", out var tables) || tables.ValueKind != JsonValueKind.Array)
            return;

        foreach (var table in tables.EnumerateArray())
        {
            if (!table.TryGetProperty("rowCount", out var rcEl) || !table.TryGetProperty("columnCount", out var ccEl))
                continue;
            int rowCount = rcEl.GetInt32();
            int colCount = ccEl.GetInt32();
            if (rowCount < 2 || colCount < 3) continue;
            if (!table.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
                continue;

            // Build a [row][col] grid of cell text
            var grid = new string[rowCount, colCount];
            foreach (var cell in cells.EnumerateArray())
            {
                int r = cell.TryGetProperty("rowIndex", out var rEl) ? rEl.GetInt32() : -1;
                int c = cell.TryGetProperty("columnIndex", out var cEl) ? cEl.GetInt32() : -1;
                if (r < 0 || c < 0 || r >= rowCount || c >= colCount) continue;
                var text = cell.TryGetProperty("content", out var tEl) ? tEl.GetString() ?? "" : "";
                grid[r, c] = text;
            }

            // Detect column roles from the first row (headers)
            int descCol = -1, qtyCol = -1, priceCol = -1, amountCol = -1;
            for (int c = 0; c < colCount; c++)
            {
                var h = (grid[0, c] ?? "").ToLowerInvariant();
                if (descCol < 0 && (h.Contains("รายการ") || h.Contains("description") || h.Contains("desc")
                    || h.Contains("สินค้า") || h.Contains("ชื่อ") || h.Contains("รายละเอียด"))) descCol = c;
                else if (qtyCol < 0 && (h.Contains("จำนวน") || h.Contains("qty") || h.Contains("quantity")
                    || h.Contains("จํานวน"))) qtyCol = c;
                else if (priceCol < 0 && (h.Contains("ราคา/หน่วย") || h.Contains("unit price") || h.Contains("ราคาต่อ")
                    || h.Contains("price"))) priceCol = c;
                else if (amountCol < 0 && (h.Contains("รวม") || h.Contains("amount") || h.Contains("total")
                    || h.Contains("จำนวนเงิน") || h.Contains("จํานวนเงิน"))) amountCol = c;
            }
            if (descCol < 0) continue;     // need at least description column
            if (amountCol < 0)
            {
                // Last column is usually the amount when no header matches
                amountCol = colCount - 1;
            }

            // Skip header row; rows where the description is empty / numeric
            // are likely summary rows (subtotal / total / VAT) — drop them.
            for (int r = 1; r < rowCount; r++)
            {
                var desc = (grid[r, descCol] ?? "").Trim();
                if (string.IsNullOrEmpty(desc)) continue;
                // Pure-digit / amount-like descriptions are summary rows
                if (desc.All(ch => !char.IsLetter(ch))) continue;

                decimal? qty = qtyCol >= 0 ? ParseAmount(grid[r, qtyCol]) : null;
                decimal? price = priceCol >= 0 ? ParseAmount(grid[r, priceCol]) : null;
                decimal? amt = ParseAmount(grid[r, amountCol]);
                if (!amt.HasValue && !price.HasValue) continue;

                result.Items.Add(new AzureDiLineItem
                {
                    Description = desc,
                    Quantity = qty,
                    UnitPrice = price,
                    Amount = amt,
                });
            }
            if (result.Items.Count > 0)
            {
                result.Warnings.Add($"Items extracted from layout table fallback ({result.Items.Count} rows)");
                break;   // first matching table wins
            }
        }
    }

    private static decimal? ParseAmount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = raw.Replace(",", "").Trim();
        if (decimal.TryParse(cleaned, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0) return v;
        return null;
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

    /// <summary>POST the analyze submission with built-in 429 retry. Honors
    /// Retry-After header and the "Please retry after N seconds" hint
    /// inside Azure's error JSON. Up to 3 retries; total wait capped so
    /// batch uploads back off without freezing the scan pipeline.</summary>
    private async Task<HttpResponseMessage> PostWith429RetryAsync(
        HttpClient client, string url, byte[] fileBytes, string contentType, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // Each attempt needs a fresh content payload — HttpContent
            // streams once and is disposed after PostAsync. Reusing the
            // outer content would throw on the second call.
            using var body = new ByteArrayContent(fileBytes);
            body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);

            var response = await client.PostAsync(url, body, ct);
            if ((int)response.StatusCode != 429) return response;

            var waitSec = ParseRetryAfter(response)
                ?? ExtractRetryFromBody(await response.Content.ReadAsStringAsync(ct))
                ?? (10 * (attempt + 1));     // exponential-ish: 10s, 20s, 30s
            waitSec = Math.Min(waitSec, 60);
            _logger.LogInformation("Azure DI submit 429 — backing off {Sec}s (attempt {Attempt}/3)", waitSec, attempt + 1);
            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(waitSec), ct);
        }
        // Final attempt — return whatever Azure gives us (likely another
        // 429) and let the caller surface it.
        using var finalBody = new ByteArrayContent(fileBytes);
        finalBody.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
        return await client.PostAsync(url, finalBody, ct);
    }

    /// <summary>Read the Retry-After header. Returns the number of seconds
    /// to wait or null when the header is missing / malformed.</summary>
    private static int? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta.HasValue == true) return (int)ra.Delta.Value.TotalSeconds;
        if (ra?.Date.HasValue == true)
        {
            var s = (int)(ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
            return s > 0 ? s : 1;
        }
        return null;
    }

    /// <summary>Extract the "Please retry after N seconds" hint Azure DI
    /// embeds in its 429 error body. F0-tier limit messages look like
    /// "Please retry after 39 seconds. To increase your rate limit ...".</summary>
    private static int? ExtractRetryFromBody(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(body, @"retry after (\d+)\s*sec", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;
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

    // ─── features=styleFont output ───
    // Number of handwritten spans detected. >0 → flag the scan for
    // manual amount verification because handwritten amounts on a
    // printed form are a common source of fraud / typos.
    public int HandwrittenSpanCount { get; set; } = 0;
    public decimal HandwrittenConfidence { get; set; } = 0m;

    // ─── pages[].selectionMarks output ───
    // Checkboxes / option marks. RD tax forms (ภงด.1, 3, 53, ภพ.30)
    // use them to indicate filer status. Stored as label→state pairs.
    public List<AzureDiSelectionMark> SelectionMarks { get; set; } = new();

    // ─── features=keyValuePairs output ───
    // Generic free-text Key→Value pairs that Azure extracted from anchor
    // patterns. Used as a Thai-aware fallback when the standard Invoice
    // schema misses a field (e.g. "เลขประจำตัวผู้เสียภาษี" anchored
    // tax ID that the schema's VendorTaxId didn't pick up).
    public Dictionary<string, string> KeyValuePairs { get; set; } = new();

    // ─── features=barcodes output ───
    // Barcodes / QR codes found on the page. Thai RD e-receipts encode
    // canonical amount + tax id in a QR — when present, treat as
    // ground-truth for cross-checking the schema-extracted fields.
    public List<AzureDiBarcode> Barcodes { get; set; } = new();

    // ─── features=languages output ───
    // Locale codes Azure detected on the page. Useful for diagnostic /
    // telemetry — e.g. flag "th" docs that came back with mostly en-US
    // field interpretation.
    public List<string> DetectedLanguages { get; set; } = new();
}

public class AzureDiBarcode
{
    /// <summary>QRCode, Code128, EAN13, PDF417, ...</summary>
    public string? Kind { get; set; }
    public string? Value { get; set; }
}

public class AzureDiSelectionMark
{
    /// <summary>"selected" | "unselected"</summary>
    public string? State { get; set; }
    /// <summary>Confidence the state is correct (0–1).</summary>
    public decimal Confidence { get; set; }
    /// <summary>Nearby text label — best guess at what this checkbox is for.</summary>
    public string? NearbyLabel { get; set; }
}

public class AzureDiLineItem
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Amount { get; set; }
    public string? ProductCode { get; set; }
}
