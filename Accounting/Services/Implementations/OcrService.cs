using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Accounting.Data;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Ocr;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class OcrService : IOcrService
{
    private readonly AccountingDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OcrService> _logger;
    private readonly IDbdLookupService _dbdLookup;
    private readonly AzureDocumentIntelligenceService _azureDi;
    private readonly ExpenseCategoryLearner _categoryLearner;
    private readonly VendorIntelligenceService _vendorIntel;
    private readonly EmbeddedTesseractOcrService _embeddedOcr;
    private readonly AssociationRuleMiner _ruleMiner;
    private readonly TfIdfNaiveBayesClassifier _nbClassifier;
    private readonly RecurringExpenseDetector _recurringDetector;

    public OcrService(AccountingDbContext db, IHttpClientFactory httpClientFactory,
        IConfiguration configuration, ILogger<OcrService> logger,
        IDbdLookupService dbdLookup,
        AzureDocumentIntelligenceService azureDi,
        ExpenseCategoryLearner categoryLearner,
        VendorIntelligenceService vendorIntel,
        EmbeddedTesseractOcrService embeddedOcr,
        AssociationRuleMiner ruleMiner,
        TfIdfNaiveBayesClassifier nbClassifier,
        RecurringExpenseDetector recurringDetector)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _dbdLookup = dbdLookup;
        _azureDi = azureDi;
        _categoryLearner = categoryLearner;
        _vendorIntel = vendorIntel;
        _embeddedOcr = embeddedOcr;
        _ruleMiner = ruleMiner;
        _nbClassifier = nbClassifier;
        _recurringDetector = recurringDetector;
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
            scanResult.OcrEngine = "Cached";   // copied from a prior scan; no OCR engine ran
            scanResult.ProcessingNotes = $"Duplicate of scan {duplicateOf.Id} (engine: {duplicateOf.OcrEngine ?? "unknown"})";
            scanResult.ProcessedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return MapToResponse(scanResult);
        }

        // Load SiteSettings ONCE per scan — feeds gateway config + provider routing + Azure DI.
        // Avoids 3 separate roundtrips for the same single-row table.
        var siteSettings = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync();
        var ocrProvider = GetEffectiveProvider(siteSettings);
        OcrExtractedData? extractedData = null;

        var scanStartedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "OCR scan started ScanId={ScanId} CompanyId={CompanyId} Provider={Provider} FileSize={FileSize} ContentType={ContentType}",
            scanResult.Id, companyId, ocrProvider, file.FileSize, file.ContentType);

        try
        {
            string extractedText;

            // ─── 3-tier provider chain ───
            //   1. Azure DI v4 (cloud) — highest accuracy, requires API key + internet
            //   2. Python ocr-service (PaddleOCR+EasyOCR) — high accuracy, requires
            //      external Python service running at LocalServiceUrl
            //   3. Embedded Tesseract (in-process, ALWAYS available) — last-resort
            //      fallback so the system never fails when the upper tiers are
            //      unreachable. Pure NuGet + tessdata files in wwwroot — no
            //      external installation needed.
            //
            // Cascade: each tier's failure falls down to the next, with the reason
            // recorded in ReasoningTrace so the user knows which engine produced
            // the result.

            // Diagnose WHY Azure is or isn't usable — admin can see which field is missing
            var azureToggleOn = siteSettings?.AzureDiEnabled == true;
            var azureHasEndpoint = !string.IsNullOrEmpty(siteSettings?.AzureDiEndpoint);
            var azureHasKey = !string.IsNullOrEmpty(siteSettings?.AzureDiApiKey);
            var azureEnabled = azureToggleOn && azureHasEndpoint && azureHasKey;
            string? azureSkipReason = null;
            // The OcrProvider field is the FALLBACK preference (which engine
            // to try when Azure isn't available). It must NOT block Azure
            // when the admin has explicitly enabled and configured it —
            // doing so was confusing for users who left provider="local"
            // from earlier setup and then enabled Azure but couldn't figure
            // out why scans still went to local.
            if (!azureToggleOn)
                azureSkipReason = "AzureDiEnabled = false (ยังไม่เปิด toggle)";
            else if (!azureHasEndpoint)
                azureSkipReason = "Azure DI Endpoint ว่าง — กรุณากรอกใน admin/ocr-config";
            else if (!azureHasKey)
                azureSkipReason = "Azure DI API Key ว่าง — กรุณากรอกใน admin/ocr-config";

            extractedData = null;
            extractedText = "";
            string? lastError = null;
            string? ocrEngineUsed = null;   // surfaced on scanResult.OcrEngine for the debug panel

            // ─── Tier 0: text-born PDF bypass ───
            // Before paying any OCR cost (Azure billable pages or local
            // CPU), check whether the file is a digital PDF with an
            // embedded text layer. Thai e-Tax e-Receipts, cloud-billing
            // invoices, and any system-generated PDF have a usable text
            // layer — extracting it directly is free, instant, and
            // 100% accurate (no OCR errors at all). Image-only PDFs and
            // sparsely-tagged scans fall through to the normal cascade.
            if (file.ContentType?.Contains("pdf", StringComparison.OrdinalIgnoreCase) == true
                || (file.OriginalFileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                try
                {
                    var pdfBytes = await File.ReadAllBytesAsync(file.StoragePath);
                    var maxPages = siteSettings?.OcrMaxPagesPerScan ?? 10;
                    var pdfResult = Ocr.PdfTextLayerExtractor.TryExtract(pdfBytes, maxPages);
                    if (pdfResult.HasUsableText)
                    {
                        // Run the same rule-based ParseThaiDocument + SmartFieldExtractor
                        // pipeline that the embedded Tesseract path uses, so all
                        // downstream layers (gateway, role inferrer, category resolver,
                        // ML stack) work identically on the bypassed text.
                        var normalized = Ocr.ThaiTextNormalizer.Normalize(pdfResult.Text);
                        extractedData = ParseThaiDocument(normalized);
                        extractedText = pdfResult.Text;
                        // Text-layer extraction is character-perfect — start
                        // confidence high; gateway can knock it down if math doesn't add up.
                        extractedData.Confidence = Math.Max(extractedData.Confidence, 0.95m);
                        Ocr.SmartFieldExtractor.Enrich(extractedData, pdfResult.Text);
                        ocrEngineUsed = "PdfTextLayer";
                        extractedData.ReasoningTrace.Insert(0,
                            $"[PdfTextLayer] bypass OCR — {pdfResult.Reason} ({pdfResult.CharsExtracted} chars)");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "PdfTextLayer bypass failed — falling through to OCR cascade");
                }
            }

            // Tier 1: Azure DI — runs whenever the admin has fully configured
            // it (toggle + endpoint + key), regardless of OcrProvider's value.
            // SKIPPED when Tier 0 (text-layer bypass) already succeeded.
            if (extractedData == null && azureEnabled)
            {
                var azureResult = await ExtractWithAzureDiAsync(companyId, file, siteSettings);
                if (azureResult.Success)
                {
                    extractedText = azureResult.Text;
                    extractedData = azureResult.Data;
                    ocrEngineUsed = "AzureDI";
                }
                else
                {
                    lastError = azureResult.Error ?? "unknown";
                    _logger.LogWarning("Azure DI failed ({Err}) — falling back to next tier", lastError);
                }
            }

            // Tier 2: Python local service (PaddleOCR+EasyOCR)
            if (extractedData == null)
            {
                try
                {
                    var localResult = await ExtractWithLocalServiceAsync(file);
                    if (!string.IsNullOrEmpty(localResult.RawText))
                    {
                        extractedText = localResult.RawText;
                        extractedData = localResult.Data;
                        ocrEngineUsed = "LocalPython";
                        if (lastError != null)
                            extractedData.ReasoningTrace.Insert(0, $"[Fallback] Azure DI failed: {lastError} — used Python local pipeline");
                        else if (azureSkipReason != null)
                            extractedData.ReasoningTrace.Insert(0, $"[Provider] ข้าม Azure DI — {azureSkipReason}; ใช้ Local (PaddleOCR+EasyOCR)");
                    }
                }
                catch (Exception ex)
                {
                    lastError = $"Python local: {ex.Message}";
                    _logger.LogWarning(ex, "Python local OCR failed — falling back to embedded Tesseract");
                }
            }

            // Tier 3: Embedded Tesseract (always available, in-process)
            if (extractedData == null)
            {
                var embeddedResult = await ExtractWithEmbeddedTesseractAsync(file);
                extractedText = embeddedResult.RawText;
                extractedData = embeddedResult.Data;
                ocrEngineUsed = "EmbeddedTesseract";
                if (lastError != null)
                    extractedData.ReasoningTrace.Insert(0, $"[Fallback] tiers above failed ({lastError}) — used Embedded Tesseract");
                else
                    extractedData.ReasoningTrace.Insert(0, "[Provider] ใช้ Embedded Tesseract (in-process fallback)");
                // Always make the Azure-skip reason visible even when embedded ran cleanly
                if (azureSkipReason != null)
                    extractedData.ReasoningTrace.Add($"[Azure DI] {azureSkipReason}");
            }

            scanResult.OcrEngine = ocrEngineUsed;

            // Math/confidence gateway — uses pre-loaded SiteSettings (no extra DB hit)
            var gatewayConfig = BuildGatewayConfig(siteSettings);
            var lineItemsForValidation = extractedData.Items
                .Select(i => new OcrConfidenceGateway.LineItemForValidation(i.Quantity, i.UnitPrice, i.Amount))
                .ToList();
            var whtRatePct = extractedData.HasWht && extractedData.WhtRate.HasValue
                ? extractedData.WhtRate.Value
                : (decimal?)null;
            decimal? whtAmt = null;
            // Calculate expected WHT from subTotal × rate when not extracted (for sanity validation)
            if (whtRatePct.HasValue && extractedData.SubTotal.HasValue)
                whtAmt = Math.Round(extractedData.SubTotal.Value * whtRatePct.Value / 100m, 2);

            var gatewayResult = OcrConfidenceGateway.Validate(
                extractedData.Confidence,
                extractedData.DocumentDate,
                extractedData.SubTotal,
                extractedData.VatAmount,
                extractedData.TotalAmount,
                extractedData.VendorTaxId,
                extractedData.BuyerTaxId,
                extractedData.FieldConfidence.ToDictionary(kv => kv.Key, kv => (decimal)kv.Value),
                config: gatewayConfig,
                lineItems: lineItemsForValidation,
                whtAmount: whtAmt,
                whtRatePercent: whtRatePct,
                documentNumber: extractedData.DocumentNumber,
                vendorName: extractedData.VendorName);

            extractedData.Confidence = gatewayResult.AdjustedConfidence;
            foreach (var w in gatewayResult.Warnings)
                extractedData.ReasoningTrace.Add("[Gateway] " + w);

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

            scanResult.ExpenseCategory = extractedData.ExpenseCategory;
            // NOTE: HasWht/WhtRate/PaymentTermsDays/DocumentType/Confidence are re-synced
            // AFTER the vendorPred block below — vendor intelligence may override them.

            // ───── Document role inference (run BEFORE VendorIntel) ─────
            // Determine who we are in this paper (Buyer/Seller) and what doc
            // we should CREATE — independent of what the paper itself shows.
            // VendorIntel below may then refine the TargetDocumentType using
            // learned per-vendor history; placing the inferrer before VendorIntel
            // keeps the scanned-vs-target separation clean.
            // Company context — used by the role inferrer (tax-id match) AND
            // the category resolver (industry-bias weighting). Single query
            // serves both.
            var companyContext = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId && !c.IsDeleted)
                .Select(c => new { c.TaxId, c.Name, c.BusinessType, c.IndustryType })
                .FirstOrDefaultAsync();
            {
                DocumentType? prevScanned = null;
                if (Enum.TryParse<DocumentType>(extractedData.DocumentType, ignoreCase: true, out var prevDt))
                    prevScanned = prevDt;
                // Normalize once: collapse Tesseract's inter-character Thai
                // spacing so every keyword scan (role inferrer, category
                // resolver, basket-rule lookup) sees readable Thai.
                // Original extractedText is preserved for display.
                var normalizedText = Ocr.ThaiTextNormalizer.Normalize(extractedText);
                var role = OcrDocumentRoleInferrer.Infer(
                    rawText: normalizedText,
                    vendorTaxId: extractedData.VendorTaxId,
                    buyerTaxId: extractedData.BuyerTaxId,
                    vendorName: extractedData.VendorName,
                    buyerName: extractedData.BuyerName,
                    companyTaxId: companyContext?.TaxId,
                    companyName: companyContext?.Name,
                    previousScannedType: prevScanned);
                if (role.ScannedDocType.HasValue)
                    extractedData.DocumentType = role.ScannedDocType.Value.ToString();
                extractedData.OurRole = role.OurRole;
                extractedData.TargetDocumentType = role.TargetDocType.ToString();
                foreach (var r in role.Reasons)
                    extractedData.ReasoningTrace.Add("[Role] " + r);
            }

            // Store zone analysis info for debugging — these never change after this point
            if (!string.IsNullOrEmpty(extractedData.ZoneSummary))
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n[Zone Analysis]\n" + extractedData.ZoneSummary;
            if (!string.IsNullOrEmpty(extractedData.BuyerName))
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + $"\n[Buyer] {extractedData.BuyerName} TaxID:{extractedData.BuyerTaxId ?? "N/A"}";
            if (extractedData.FieldConfidence.Count > 0)
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n[Field Confidence]\n" +
                    string.Join("\n", extractedData.FieldConfidence.Select(kv => $"  {kv.Key}: {kv.Value:P0}"));
            // [Reasoning] section is built AFTER vendorPred so VendorIntel/Learner traces are included.

            // ───── Default category resolution (Thai expense classifier) ─────
            // Before consulting per-tenant learned data, run the global
            // Thai-aware classifier to bootstrap fresh tenants. The resolver
            // only fills EMPTY fields, so anything the OCR provider already
            // surfaced (or the user's prior corrections produced) is kept.
            // ExpenseCategoryLearner below may then override with company-
            // specific habits when they exist.
            // Normalize Thai before keyword scanning — same reason as the
            // role inferrer above; the brand/keyword rules in the resolver
            // can't find "ค่าไฟฟ้า" / "การไฟฟ้า" in a "ค ่ า ไฟ ฟ ้ า" /
            // "ก า ร ไฟ ฟ ้ า" string.
            var categoryResolverText = Ocr.ThaiTextNormalizer.Normalize(extractedText);
            var categoryResult = Ocr.ExpenseCategoryResolver.Resolve(
                vendorName: extractedData.VendorName,
                headerDescription: extractedData.ExpenseCategory,
                lineDescriptions: extractedData.Items.Select(i => i.Description),
                rawText: categoryResolverText,
                industry: companyContext?.IndustryType,
                businessType: companyContext?.BusinessType);
            if (categoryResult != null)
                Ocr.ExpenseCategoryResolver.ApplyTo(extractedData, categoryResult);

            // ───── Basket-analysis association rule lookup ─────
            // Apriori-mined rules from approved-doc history across all
            // tenants. A high-lift rule "brand:HomePro + kw:วัสดุ → acct:5305"
            // beats the static keyword resolver because it reflects what
            // real Thai businesses actually book.
            try
            {
                var rule = await _ruleMiner.FindBestMatchAsync(
                    extractedData.VendorName,
                    extractedData.Items.Select(i => i.Description));
                if (rule != null && rule.Confidence >= 0.7m && rule.Lift >= 2m
                    && rule.Consequent.StartsWith("acct:")
                    && (string.IsNullOrEmpty(extractedData.DebitAccountCode)
                        || extractedData.FieldConfidence.GetValueOrDefault("DebitAccount", 0) < 0.85))
                {
                    var code = rule.Consequent.Substring("acct:".Length);
                    extractedData.DebitAccountCode = code;
                    var acctName = await _db.ChartOfAccounts.AsNoTracking()
                        .Where(a => a.CompanyId == companyId && a.AccountCode == code && !a.IsDeleted)
                        .Select(a => a.AccountName)
                        .FirstOrDefaultAsync();
                    if (!string.IsNullOrEmpty(acctName)) extractedData.DebitAccountName = acctName;
                    extractedData.FieldConfidence["DebitAccount"] = (double)Math.Min(0.95m, rule.Confidence);
                    extractedData.ReasoningTrace.Add(
                        $"[BasketRule] {string.Join("+", rule.Antecedent)} → {rule.Consequent} " +
                        $"(conf {rule.Confidence:P0}, lift {rule.Lift:F1}, จาก {rule.TransactionCount} เอกสาร)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Association rule lookup failed (non-fatal)");
            }

            // ───── Naive Bayes + TF-IDF classifier ─────
            // Probabilistic blend of per-tenant historical (vendor +
            // description → account) data. Wins over the keyword learner
            // when there are many low-frequency overlapping tokens that
            // a count-based approach would dilute. Only commits when the
            // log-margin over the runner-up class exceeds 1 nat.
            try
            {
                var nbDesc = extractedData.Items.FirstOrDefault()?.Description
                    ?? extractedData.ExpenseCategory ?? "";
                var nbPred = await _nbClassifier.PredictAsync(
                    companyId, extractedData.VendorTaxId, extractedData.VendorName, nbDesc);
                if (nbPred != null && nbPred.Confidence > 0.7m
                    && extractedData.FieldConfidence.GetValueOrDefault("DebitAccount", 0) < (double)nbPred.Confidence)
                {
                    extractedData.DebitAccountCode = nbPred.AccountCode;
                    if (!string.IsNullOrEmpty(nbPred.AccountName))
                        extractedData.DebitAccountName = nbPred.AccountName;
                    extractedData.FieldConfidence["DebitAccount"] = (double)nbPred.Confidence;
                    extractedData.ReasoningTrace.Add(
                        $"[NaiveBayes] รหัส {nbPred.AccountCode} confidence {nbPred.Confidence:P0} ({nbPred.Reason})");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "NB classifier failed (non-fatal)");
            }

            // ───── Product cross-reference ─────
            // When a line-item description matches an existing Product (by
            // exact name or barcode/SKU substring), prefer the product's
            // configured PurchaseAccount over any inferred account — the
            // product master is the strongest signal: a human set this up
            // intentionally and Thai products almost always have one
            // canonical expense bucket.
            await ApplyProductCrossReferenceAsync(companyId, extractedData);

            // ───── Learned category prediction (per line description) ─────
            // If we have a learned mapping for this vendor, prefer it over generic
            // industry-based defaults. Per-line predictions also override individual
            // line item.SuggestedAccountCode when learner is more confident.
            var bestDescription = extractedData.Items.FirstOrDefault()?.Description
                ?? extractedData.ExpenseCategory;
            var (learnedCode, learnedName, learnedConf) = await _categoryLearner.PredictAsync(
                companyId, extractedData.VendorTaxId, extractedData.VendorName, bestDescription);
            if (learnedCode != null && learnedConf >= 0.55m)
            {
                extractedData.DebitAccountCode = learnedCode;
                extractedData.DebitAccountName = learnedName;
                extractedData.ReasoningTrace.Add($"[Learner] เคยใช้รหัส {learnedCode} กับผู้ขายนี้+คำอธิบายนี้ (confidence {learnedConf:P0})");

                // Per-line override
                foreach (var item in extractedData.Items)
                {
                    if (string.IsNullOrEmpty(item.SuggestedAccountCode))
                    {
                        var (perLineCode, _, perLineConf) = await _categoryLearner.PredictAsync(
                            companyId, extractedData.VendorTaxId, extractedData.VendorName, item.Description);
                        if (perLineCode != null && perLineConf >= 0.55m)
                            item.SuggestedAccountCode = perLineCode;
                    }
                }
            }

            // ───── Vendor intelligence prediction (DocumentType + WHT + amount sanity) ─────
            // Higher-level "what does this supplier usually look like?" cache built from
            // approved Documents history. Used to:
            //   • Auto-select DocumentType when AI/rules are uncertain
            //   • Fill in WHT habits when extraction missed it
            //   • Flag amount anomalies for user review
            //   • Backfill debit account when per-line learner had no match
            var vendorPred = await _vendorIntel.PredictAsync(
                companyId, extractedData.VendorTaxId, extractedData.VendorName, extractedData.TotalAmount);
            if (vendorPred.HasHistory)
            {
                extractedData.ReasoningTrace.Add($"[VendorIntel] พบประวัติ {vendorPred.SampleSize} เอกสารของผู้ขายรายนี้");
                foreach (var reason in vendorPred.Reasons)
                    extractedData.ReasoningTrace.Add("[VendorIntel] " + reason);

                // Auto-apply TargetDocumentType when high confidence.
                // VendorIntel learns from previously-CREATED documents, so its
                // prediction is the doc-type to create — not the paper that was
                // scanned. Override TargetDocumentType (set earlier by the role
                // inferrer); never touch DocumentType (the scanned paper type).
                if (vendorPred.DocumentType.HasValue && vendorPred.DocumentTypeConfidence >= VendorIntelligenceService.HighConfidence)
                {
                    var newTarget = vendorPred.DocumentType.Value.ToString();
                    if (extractedData.TargetDocumentType != newTarget)
                    {
                        extractedData.ReasoningTrace.Add(
                            $"[VendorIntel] เปลี่ยนเอกสารที่จะสร้างจาก {extractedData.TargetDocumentType} → {newTarget} (confidence {vendorPred.DocumentTypeConfidence:P0})");
                        extractedData.TargetDocumentType = newTarget;
                    }
                    extractedData.Confidence = Math.Max(extractedData.Confidence, vendorPred.DocumentTypeConfidence);
                }
                else if (vendorPred.DocumentType.HasValue && vendorPred.DocumentTypeConfidence >= VendorIntelligenceService.MediumConfidence)
                {
                    extractedData.ReasoningTrace.Add(
                        $"[VendorIntel] แนะนำสร้าง {vendorPred.DocumentType.Value} (confidence {vendorPred.DocumentTypeConfidence:P0}) — รอผู้ใช้ยืนยัน");
                }

                // Auto-fill debit account when per-line learner had no result
                if (string.IsNullOrEmpty(extractedData.DebitAccountCode)
                    && !string.IsNullOrEmpty(vendorPred.DebitAccountCode)
                    && vendorPred.DebitAccountConfidence >= VendorIntelligenceService.MediumConfidence)
                {
                    extractedData.DebitAccountCode = vendorPred.DebitAccountCode;
                    extractedData.DebitAccountName = vendorPred.DebitAccountName;
                    extractedData.ReasoningTrace.Add(
                        $"[VendorIntel] เลือกรหัสบัญชี {vendorPred.DebitAccountCode} จากประวัติผู้ขาย (confidence {vendorPred.DebitAccountConfidence:P0})");
                }

                // Auto-fill WHT habits when extraction missed it but vendor typically has WHT
                if (!extractedData.HasWht && vendorPred.HasWht == true && vendorPred.WhtRate.HasValue
                    && vendorPred.WhtConfidence >= VendorIntelligenceService.MediumConfidence)
                {
                    extractedData.HasWht = true;
                    extractedData.WhtRate = vendorPred.WhtRate;
                    extractedData.ReasoningTrace.Add(
                        $"[VendorIntel] ตั้ง WHT = {vendorPred.WhtRate}% จากประวัติผู้ขาย (confidence {vendorPred.WhtConfidence:P0})");
                }

                // Auto-fill payment terms when missing
                if (!extractedData.PaymentTermsDays.HasValue && vendorPred.TypicalPaymentTermsDays.HasValue)
                {
                    extractedData.PaymentTermsDays = vendorPred.TypicalPaymentTermsDays;
                    extractedData.ReasoningTrace.Add(
                        $"[VendorIntel] ตั้ง payment terms = {vendorPred.TypicalPaymentTermsDays} วัน จากประวัติผู้ขาย");
                }

                // ─── Pattern-based confidence boost ───
                // If this vendor's history shows a consistent document-number
                // prefix and the current scan's number also starts with that
                // prefix, raise the DocumentNumber confidence. When extraction
                // produced a number that DOESN'T match the historic prefix it's
                // likely an OCR misread — drop confidence to flag for review.
                if (!string.IsNullOrEmpty(vendorPred.TypicalDocNumberPrefix)
                    && !string.IsNullOrEmpty(extractedData.DocumentNumber)
                    && vendorPred.TypicalDocNumberPrefix.Length >= 2)
                {
                    if (extractedData.DocumentNumber.StartsWith(vendorPred.TypicalDocNumberPrefix,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        extractedData.FieldConfidence["DocumentNumber"] = Math.Max(
                            extractedData.FieldConfidence.GetValueOrDefault("DocumentNumber", 0), 0.95);
                        extractedData.ReasoningTrace.Add(
                            $"[VendorIntel] เลขเอกสาร '{extractedData.DocumentNumber}' ขึ้นต้นตรงกับ pattern '{vendorPred.TypicalDocNumberPrefix}' ของผู้ขายรายนี้");
                    }
                    else
                    {
                        extractedData.FieldConfidence["DocumentNumber"] = Math.Min(
                            extractedData.FieldConfidence.GetValueOrDefault("DocumentNumber", 0.7), 0.5);
                        extractedData.ReasoningTrace.Add(
                            $"[VendorIntel] เลขเอกสาร '{extractedData.DocumentNumber}' ไม่ตรง pattern '{vendorPred.TypicalDocNumberPrefix}' ของผู้ขายรายนี้ — โปรดตรวจสอบ");
                    }
                }

                // Boost ExpenseCategory confidence when the current scan's
                // descriptions share words with the vendor's historical
                // top-keywords. This pushes a marginal category match into
                // high-confidence territory without overriding what the user
                // (or learner) has already trained.
                if (vendorPred.TopLineKeywords != null && vendorPred.TopLineKeywords.Count > 0
                    && !string.IsNullOrEmpty(extractedData.ExpenseCategory))
                {
                    var corpus = string.Join(" ",
                        new[] { extractedData.ExpenseCategory ?? "" }
                            .Concat(extractedData.Items.Select(i => i.Description ?? ""))
                    ).ToLowerInvariant();
                    int hits = vendorPred.TopLineKeywords
                        .Where(kv => corpus.Contains(kv.Key))
                        .Sum(kv => kv.Value);
                    if (hits >= 3)
                    {
                        extractedData.FieldConfidence["ExpenseCategory"] = Math.Max(
                            extractedData.FieldConfidence.GetValueOrDefault("ExpenseCategory", 0), 0.92);
                        extractedData.ReasoningTrace.Add(
                            $"[VendorIntel] รายการตรงกับ keyword ประวัติ {hits} ครั้ง → หมวด '{extractedData.ExpenseCategory}' มั่นใจสูง");
                    }
                }
            }

            // ───── Re-sync mutable fields (extractedData → scanResult) ─────
            // Persist mutations from VendorIntel / Learner / role inferrer
            // back to OcrScanResult so they reach MapToResponse() and
            // AutoCreateDocumentAsync(). DocumentType holds the SCANNED paper
            // type (back-compat); the new TargetDocumentType / OurRole columns
            // carry the role-inferrer's decision and are what AutoCreate
            // consumes when picking a document type to build.
            scanResult.DocumentType = extractedData.DocumentType;
            scanResult.ScannedDocumentType = extractedData.DocumentType;
            scanResult.OurRole = extractedData.OurRole;
            scanResult.TargetDocumentType = extractedData.TargetDocumentType;
            scanResult.HasWht = extractedData.HasWht;
            scanResult.WhtRate = extractedData.WhtRate;
            scanResult.PaymentTermsDays = extractedData.PaymentTermsDays;
            scanResult.Confidence = extractedData.Confidence;

            // Build [Reasoning] section LAST so it includes VendorIntel + Learner traces
            if (extractedData.ReasoningTrace.Count > 0)
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n[Reasoning]\n" +
                    string.Join("\n", extractedData.ReasoningTrace.Select(r => "  • " + r));

            if (extractedData.DebitAccountCode != null || extractedData.CreditAccountCode != null)
            {
                scanResult.SuggestedAccountsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    extractedData.DebitAccountCode, extractedData.DebitAccountName,
                    extractedData.CreditAccountCode, extractedData.CreditAccountName,
                    extractedData.VatAccountCode, extractedData.VatAccountName,
                });
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

            // ───── CONTENT FINGERPRINT (cross-format dedup) ─────
            // Catches the case where same invoice is uploaded as JPG once + PDF later.
            // File hash differs but the semantic content matches. Stored on the row for
            // indexed lookups and surfaced to user as "เคยอัปโหลดมาแล้วในรูปแบบอื่น".
            scanResult.ContentFingerprint = ComputeContentFingerprint(extractedData);

            if (!string.IsNullOrEmpty(scanResult.ContentFingerprint))
            {
                var contentDup = await _db.Set<OcrScanResult>()
                    .Where(r => r.CompanyId == companyId
                        && r.Id != scanResult.Id
                        && r.ContentFingerprint == scanResult.ContentFingerprint
                        && r.ScanStatus == "Completed"
                        && r.CreatedAt > DateTime.UtcNow.AddDays(-90))
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => new { r.Id, r.OriginalFileName, r.CreatedAt })
                    .FirstOrDefaultAsync();
                if (contentDup != null)
                {
                    scanResult.IsDuplicate = true;
                    scanResult.DuplicateOfScanId = contentDup.Id;
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") +
                        $"\n[Content Duplicate] เนื้อหาเอกสารตรงกับไฟล์ที่เคยอัปโหลดเมื่อ {contentDup.CreatedAt:yyyy-MM-dd HH:mm} ({contentDup.OriginalFileName}) — ระบบไม่สร้างเอกสารซ้ำ";
                }
            }

            // Check for duplicate by document number + amount (legacy fallback when fingerprint missing)
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

            // If the seller is our own company, the real vendor is the buyer.
            // Project just the TaxId column (not full Company entity) for efficiency.
            if (!string.IsNullOrEmpty(extractedData.VendorTaxId) || !string.IsNullOrEmpty(extractedData.BuyerTaxId))
            {
                var ourTaxId = await _db.Companies
                    .Where(c => c.Id == companyId)
                    .Select(c => c.TaxId)
                    .FirstOrDefaultAsync();
                if (!string.IsNullOrEmpty(ourTaxId))
                {
                    if (extractedData.VendorTaxId == ourTaxId && !string.IsNullOrEmpty(extractedData.BuyerName))
                    {
                        // Seller = our company → actual vendor is the buyer
                        extractedData.VendorName = extractedData.BuyerName;
                        extractedData.VendorTaxId = extractedData.BuyerTaxId;
                        scanResult.ExtractedVendorName = extractedData.VendorName;
                        scanResult.ExtractedVendorTaxId = extractedData.VendorTaxId;
                        scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n[Swap] Seller is our company — using Buyer as vendor";
                    }
                }
            }

            // === DBD Verification: authoritative company info from กรมพัฒนาธุรกิจการค้า ===
            // If the extracted Tax ID is a 13-digit juristic ID, query DBD to get the
            // canonical company name + address. This is treated as ground truth — if OCR
            // disagrees, we trust DBD and record the OCR mismatch as negative training.
            await EnrichFromDbdAsync(companyId, extractedData, scanResult);

            if (!string.IsNullOrEmpty(extractedData.VendorTaxId))
            {
                var matchedContact = await _db.Contacts
                    .FirstOrDefaultAsync(c => c.CompanyId == companyId && c.TaxId == extractedData.VendorTaxId && !c.IsDeleted);
                scanResult.MatchedContactId = matchedContact?.Id;
            }

            if (!scanResult.MatchedContactId.HasValue && !string.IsNullOrEmpty(extractedData.VendorName))
            {
                // First try the cheap substring match
                var matchedContact = await _db.Contacts
                    .FirstOrDefaultAsync(c => c.CompanyId == companyId
                        && c.Name.Contains(extractedData.VendorName) && !c.IsDeleted);
                scanResult.MatchedContactId = matchedContact?.Id;

                // When substring misses, fall back to Levenshtein-based fuzzy
                // match against every supplier contact. This catches the
                // common case where OCR returns "บริษัท เอบีซี เซอร์วิส จํากัด"
                // but the existing contact is "เอบีซี เซอร์วิส" — normalize
                // both, compute similarity, accept the best ≥0.85.
                if (!scanResult.MatchedContactId.HasValue)
                {
                    var allSuppliers = await _db.Contacts.AsNoTracking()
                        .Where(c => c.CompanyId == companyId && !c.IsDeleted
                            && (c.IsSupplier || !c.IsCustomer))
                        .Select(c => new { c.Id, c.Name })
                        .ToListAsync();
                    var best = allSuppliers
                        .Select(c => new { c.Id, c.Name, Sim = Ocr.FuzzyMatcher.Similarity(c.Name, extractedData.VendorName) })
                        .OrderByDescending(x => x.Sim)
                        .FirstOrDefault();
                    if (best != null && best.Sim >= 0.85)
                    {
                        scanResult.MatchedContactId = best.Id;
                        extractedData.ReasoningTrace.Add(
                            $"[Fuzzy] จับคู่ผู้ติดต่อ '{best.Name}' similarity {best.Sim:P0}");
                    }
                }
            }

            // ═══════ DBD-GATED CONTACT AUTO-CREATION ═══════
            // Only auto-create when we have a verified TaxId from DBD/RD VAT lookup.
            // Without this gate, a misread "12345678901" would spawn a junk Contact.
            // For OCR-only data (no DBD match), we leave Contact unmatched and let the
            // user create it manually after reviewing the scan.
            if (!scanResult.MatchedContactId.HasValue
                && extractedData.DbdMatched
                && !string.IsNullOrEmpty(extractedData.VendorTaxId))
            {
                // Parse address into structured fields when DBD provides a single string.
                var addressParts = ParseAddressIntoParts(extractedData.DbdAddress);

                var newContact = new Contact
                {
                    CompanyId = companyId,
                    Name = extractedData.DbdCanonicalName ?? extractedData.VendorName ?? $"ผู้ขาย (TaxID: {extractedData.VendorTaxId})",
                    TaxId = extractedData.VendorTaxId,
                    IsCustomer = false,
                    IsSupplier = true,
                    ContactType = ContactType.JuristicPerson,
                    Address = extractedData.DbdAddress,
                    BuildingNumber = addressParts.BuildingNumber,
                    StreetName = addressParts.StreetName,
                    SubDistrict = addressParts.SubDistrict,
                    District = addressParts.District,
                    Province = addressParts.Province,
                    PostalCode = addressParts.PostalCode,
                    CountryCode = "TH",
                    CreatedBy = "OCR-DBD-AutoCreate"
                };
                _db.Contacts.Add(newContact);
                await _db.SaveChangesAsync();
                scanResult.MatchedContactId = newContact.Id;
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + $"\n[Auto-Create] สร้าง Contact ใหม่จากข้อมูล DBD: {newContact.Name} (TaxID {newContact.TaxId})";
                _logger.LogInformation("Auto-created DBD-verified contact for company {CompanyId} TaxId={TaxId} Name={Name}",
                    companyId, newContact.TaxId, newContact.Name);
            }
            else if (!scanResult.MatchedContactId.HasValue
                && !string.IsNullOrEmpty(extractedData.VendorTaxId)
                && !extractedData.DbdMatched)
            {
                // Surface to user that no contact was created — they need to review TaxId
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + $"\n[Manual Review Required] ไม่สามารถยืนยัน TaxID {extractedData.VendorTaxId} จาก DBD/RD — กรุณาตรวจสอบและสร้าง Contact ด้วยตนเอง";
            }
            else if (scanResult.MatchedContactId.HasValue && extractedData.DbdCanonicalName != null)
            {
                // Existing contact: enrich missing fields from DBD without overwriting user data
                var existing = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == scanResult.MatchedContactId);
                if (existing != null)
                {
                    bool changed = false;
                    if (string.IsNullOrWhiteSpace(existing.TaxId) && !string.IsNullOrEmpty(extractedData.VendorTaxId))
                    {
                        existing.TaxId = extractedData.VendorTaxId;
                        changed = true;
                    }
                    if (string.IsNullOrWhiteSpace(existing.Address) && !string.IsNullOrEmpty(extractedData.DbdAddress))
                    {
                        existing.Address = extractedData.DbdAddress;
                        changed = true;
                    }
                    if (changed)
                    {
                        existing.UpdatedBy = "OCR-DbdEnrich";
                        scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + $"\n[DBD] Enriched contact {existing.Name}";
                    }
                }
            }

            // ─── Potential Fixed Asset detection (Phase 4) ───
            // Inspect line items for capital-asset signals BEFORE the auto-
            // create gate. When at least one line crosses the threshold, the
            // document is flagged for manual review — the UI surfaces a
            // "Potential Asset" alert with a Register button. Auto-create is
            // suppressed in that case so we don't pre-book the spend as
            // expense and then have to reverse it.
            try
            {
                var assetInput = extractedData.Items
                    .Select(i => (i.Description, i.Quantity, i.UnitPrice, i.Amount))
                    .ToList();
                var allDecisions = Ocr.FixedAssetDetector.Analyze(assetInput);
                var assetCandidates = Ocr.FixedAssetDetector.PotentialAssetsOnly(allDecisions);
                if (assetCandidates.Count > 0)
                {
                    scanResult.HasPotentialFixedAsset = true;
                    // Persist a serialized snapshot so the UI can render
                    // the alert without re-running the detector on every fetch.
                    scanResult.PotentialAssetLinesJson = System.Text.Json.JsonSerializer.Serialize(
                        assetCandidates.Select(d => new
                        {
                            lineIndex = d.LineIndex,
                            description = extractedData.Items.ElementAtOrDefault(d.LineIndex)?.Description,
                            unitPrice = extractedData.Items.ElementAtOrDefault(d.LineIndex)?.UnitPrice,
                            amount = extractedData.Items.ElementAtOrDefault(d.LineIndex)?.Amount,
                            quantity = extractedData.Items.ElementAtOrDefault(d.LineIndex)?.Quantity,
                            suggestedCategory = d.SuggestedCategory,
                            suggestedUsefulLifeMonths = d.SuggestedUsefulLifeMonths,
                            confidenceScore = d.ConfidenceScore,
                            reasons = d.Reasons,
                        }));
                    extractedData.ReasoningTrace.Add(
                        $"[FixedAsset] พบ {assetCandidates.Count} รายการที่อาจเป็นสินทรัพย์ถาวร — รอ user ยืนยัน (Register Asset)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Fixed-asset detection failed (non-fatal)");
            }

            // Auto-create document if confidence >= threshold (85%).
            // Recurring vendors get a lower threshold (75%) — we already know
            // what their docs look like and the user has approved enough of
            // them historically that a "looks right" scan is safe to commit
            // without staging in Draft.
            // SUPPRESS auto-create when a potential asset was detected — the
            // user must register the asset(s) first (Dr: Asset / Cr: AP-or-Cash)
            // before the scan should produce an Expense document. Otherwise
            // the books would double-count: expense from auto-create + asset
            // capitalization from manual register.
            var autoCreateThreshold = decimal.TryParse(_configuration["Ocr:AutoCreateThreshold"], out var t) ? t : 0.85m;
            if (scanResult.HasPotentialFixedAsset)
            {
                autoCreateThreshold = decimal.MaxValue;  // effectively disable auto-create
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + "\n[FixedAsset] auto-create suppressed — โปรดกด Register Asset ก่อนสร้างเอกสาร";
            }
            try
            {
                var recurring = await _recurringDetector.DetectAsync(companyId, scanResult.MatchedContactId);
                if (recurring != null && recurring.Confidence >= 0.7m)
                {
                    autoCreateThreshold = Math.Max(0.75m, autoCreateThreshold - 0.10m);
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + $"\n[Recurring] {recurring.Cadence} pattern ({recurring.SampleSize} docs, ~฿{recurring.TypicalAmount:N0}) — auto-create threshold ลดเป็น {autoCreateThreshold:P0}";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recurring detection failed (non-fatal)");
            }

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
            _logger.LogError(ex,
                "OCR scan failed ScanId={ScanId} CompanyId={CompanyId} FileId={FileId} Provider={Provider} ElapsedMs={ElapsedMs}",
                scanResult.Id, companyId, fileAttachmentId, ocrProvider,
                (int)(DateTime.UtcNow - scanStartedAt).TotalMilliseconds);
            scanResult.ScanStatus = "Failed";
            scanResult.ProcessedAt = DateTime.UtcNow;
            scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + $"\n[Error] {ex.Message}";
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "OCR scan finished ScanId={ScanId} CompanyId={CompanyId} Provider={Provider} Status={Status} Confidence={Confidence:P0} IsDuplicate={IsDuplicate} HasMatch={HasMatch} HasDocument={HasDocument} ElapsedMs={ElapsedMs}",
            scanResult.Id, companyId, ocrProvider, scanResult.ScanStatus,
            scanResult.Confidence, scanResult.IsDuplicate,
            scanResult.MatchedContactId.HasValue, scanResult.CreatedDocumentId.HasValue,
            (int)(DateTime.UtcNow - scanStartedAt).TotalMilliseconds);
        return MapToResponse(scanResult, extractedData);
    }

    private static OcrConfidenceGateway.GatewayConfig BuildGatewayConfig(SiteSettings? settings)
    {
        if (settings == null) return OcrConfidenceGateway.GatewayConfig.Default;
        return new OcrConfidenceGateway.GatewayConfig
        {
            MaxPenalty = ClampPct(settings.OcrGatewayMaxPenalty, 0.20m, 1.0m, 0.60m),
            MathTolerance = settings.OcrGatewayMathTolerance > 0 ? settings.OcrGatewayMathTolerance : 2.0m,
            TaxIdPenalty = ClampPct(settings.OcrGatewayTaxIdPenalty, 0m, 1.0m, 0.15m),
            MathPenalty = ClampPct(settings.OcrGatewayMathPenalty, 0m, 1.0m, 0.20m),
            DatePenalty = ClampPct(settings.OcrGatewayDatePenalty, 0m, 1.0m, 0.15m),
            VatRatePenalty = ClampPct(settings.OcrGatewayVatRatePenalty, 0m, 1.0m, 0.10m),
            LowConfidencePenalty = ClampPct(settings.OcrGatewayLowConfidencePenalty, 0m, 1.0m, 0.05m),
        };
    }

    private static decimal ClampPct(decimal value, decimal min, decimal max, decimal fallback)
        => (value <= 0 || value > 1) ? fallback : Math.Clamp(value, min, max);

    /// <summary>
    /// Best-effort parse of a free-text Thai address into ETDA-compliant structured parts.
    /// We extract what we can with regex; downstream UI lets user fix the rest. Better
    /// to populate partial structure than leave Contact with only a free-text Address.
    /// </summary>
    private static (string? BuildingNumber, string? StreetName, string? SubDistrict, string? District, string? Province, string? PostalCode)
        ParseAddressIntoParts(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return (null, null, null, null, null, null);

        string? Match(string pattern)
        {
            var m = System.Text.RegularExpressions.Regex.Match(address, pattern);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        var building = Match(@"(?:เลขที่\s*)?(\d+(?:/\d+)?)");
        var street = Match(@"ถนน\s*([^\s]+)");
        var subDistrict = Match(@"(?:ตำบล|แขวง)\s*([^\s]+)");
        var district = Match(@"(?:อำเภอ|เขต)\s*([^\s]+)");
        var province = Match(@"(?:จังหวัด)\s*([^\s]+)");
        var postal = Match(@"(\d{5})(?!\d)");

        return (building, street, subDistrict, district, province, postal);
    }

    /// <summary>
    /// Build a content-based fingerprint from extracted fields. Same logical document
    /// uploaded as PDF and JPG produces matching fingerprints (different file hashes).
    /// Returns null when not enough fields are present to make a reliable fingerprint —
    /// avoids false-positive collisions between blank/partial scans.
    /// </summary>
    private static string? ComputeContentFingerprint(OcrExtractedData data)
    {
        if (string.IsNullOrWhiteSpace(data.VendorTaxId) && string.IsNullOrWhiteSpace(data.VendorName))
            return null;
        if (string.IsNullOrWhiteSpace(data.DocumentNumber) && !data.TotalAmount.HasValue)
            return null;

        var vendorKey = string.IsNullOrWhiteSpace(data.VendorTaxId)
            ? (data.VendorName ?? "").Trim().ToLowerInvariant()
            : new string(data.VendorTaxId.Where(char.IsDigit).ToArray());
        var docKey = (data.DocumentNumber ?? "").Trim().ToLowerInvariant();
        var dateKey = data.DocumentDate?.ToString("yyyy-MM-dd") ?? "";
        var amtKey = data.TotalAmount.HasValue
            ? Math.Round(data.TotalAmount.Value, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            : "";
        var input = $"{vendorKey}|{docKey}|{dateKey}|{amtKey}";
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    /// <summary>Resolve the active OCR provider name from SiteSettings → appsettings.json
    /// → "local" default. Returns lowercase. Other config (LocalServiceUrl, AzureEndpoint,
    /// AutoCreateThreshold) is consumed at the point of use directly from SiteSettings.</summary>
    private string GetEffectiveProvider(SiteSettings? siteSettings)
        => (siteSettings?.OcrProvider ?? _configuration["Ocr:Provider"] ?? "").ToLowerInvariant();

    private record AzureExtractionResult(bool Success, string Text, OcrExtractedData Data, string? Error);

    /// <summary>
    /// Match each OCR'd line-item description against the company's Products
    /// table. When a strong match is found, override the line's suggested
    /// account with the product's PurchaseAccount — the product master is
    /// authoritative because a human configured it deliberately.
    ///
    /// Match strategy (in order of strength):
    ///   1. Exact code/SKU/barcode hit anywhere in the description
    ///   2. Exact product name substring (≥3 chars)
    ///   3. Loose Thai name match — first 6 Thai chars of the name appear
    ///
    /// Header-level DebitAccount is updated when ≥2 line items resolve to
    /// the same product account, or when there's only one line.
    /// </summary>
    private async Task ApplyProductCrossReferenceAsync(Guid companyId, OcrExtractedData data)
    {
        if (data.Items == null || data.Items.Count == 0) return;

        // Pull only products that have a PurchaseAccount configured — without
        // an account these rows can't influence the decision and just bloat
        // the in-memory scan.
        var products = await _db.Products.AsNoTracking()
            .Include(p => p.PurchaseAccount)
            .Where(p => p.CompanyId == companyId && !p.IsDeleted
                && p.PurchaseAccountId != null
                && p.PurchaseAccount != null)
            .Select(p => new {
                p.Code, p.Name, p.SKU, p.Barcode,
                AccountCode = p.PurchaseAccount!.AccountCode,
                AccountName = p.PurchaseAccount.AccountName,
            })
            .ToListAsync();
        if (products.Count == 0) return;

        var accountHits = new Dictionary<string, (string Name, int Count)>();
        foreach (var line in data.Items)
        {
            var desc = (line.Description ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(desc)) continue;

            var match = products.FirstOrDefault(p =>
                (!string.IsNullOrEmpty(p.Code) && desc.Contains(p.Code.ToLowerInvariant()))
                || (!string.IsNullOrEmpty(p.Barcode) && desc.Contains(p.Barcode.ToLowerInvariant()))
                || (!string.IsNullOrEmpty(p.SKU) && desc.Contains(p.SKU.ToLowerInvariant()))
                || (!string.IsNullOrEmpty(p.Name) && p.Name.Length >= 3 && desc.Contains(p.Name.ToLowerInvariant())));

            if (match == null) continue;
            line.SuggestedAccountCode = match.AccountCode;
            var entry = accountHits.GetValueOrDefault(match.AccountCode);
            accountHits[match.AccountCode] = (match.AccountName, entry.Count + 1);
            data.ReasoningTrace.Add(
                $"[Product] รายการ '{line.Description}' ตรงกับสินค้า master '{match.Name}' → บัญชี {match.AccountCode}");
        }

        if (accountHits.Count == 0) return;
        // Dominant account across the lines becomes the header-level Debit
        var dominant = accountHits.OrderByDescending(kv => kv.Value.Count).First();
        if (dominant.Value.Count >= Math.Max(2, data.Items.Count / 2))
        {
            data.DebitAccountCode = dominant.Key;
            data.DebitAccountName = dominant.Value.Name;
            data.FieldConfidence["DebitAccount"] = 0.95;
            data.ReasoningTrace.Add(
                $"[Product] บัญชี header → {dominant.Key} ({dominant.Value.Count}/{data.Items.Count} รายการตรงสินค้า master)");
        }
    }

    private async Task<AzureExtractionResult> ExtractWithAzureDiAsync(Guid companyId, FileAttachment file, SiteSettings? siteSettings)
    {
        if (!System.IO.File.Exists(file.StoragePath))
            return new AzureExtractionResult(false, "", new OcrExtractedData(), "Source file missing");

        var fileBytes = await System.IO.File.ReadAllBytesAsync(file.StoragePath);
        var contentType = OcrPreprocessor.EffectiveContentType(file.ContentType ?? "", file.OriginalFileName);

        var preflight = OcrPreprocessor.Check(fileBytes, contentType, file.OriginalFileName);
        if (!preflight.Ok)
            return new AzureExtractionResult(false, "", new OcrExtractedData(), preflight.ErrorMessage);

        // ─── In-process image enhancement before sending to Azure ───
        // ImagePreprocessor: EXIF auto-rotate, grayscale, contrast bump,
        // upscale small images. Returns identical bytes for clean inputs
        // (PDFs / high-res images). Improves Azure DI extraction on
        // phone-camera snaps; reduces payload size for jpegs.
        var prep = Ocr.ImagePreprocessor.Process(fileBytes, contentType, file.OriginalFileName);
        if (prep.StepsApplied.Count > 0)
        {
            fileBytes = prep.Bytes;
            contentType = prep.ContentType;
            _logger.LogInformation("Image preprocessed for {File}: {Steps}",
                file.OriginalFileName, string.Join(", ", prep.StepsApplied));
        }

        var azureResult = await _azureDi.AnalyzeAsync(fileBytes, contentType, siteSettings,
            fileName: file.OriginalFileName);
        if (azureResult == null)
            return new AzureExtractionResult(false, "", new OcrExtractedData(), "Azure DI not enabled or not configured");
        if (!azureResult.Success)
            return new AzureExtractionResult(false, "", new OcrExtractedData(), azureResult.ErrorMessage);

        var data = await MapAzureDiToExtractedDataAsync(companyId, azureResult);
        // Apply universal constraints — keeps the data shape consistent across
        // all three OCR providers and enforces math invariants / tax-id
        // checksum / WHT-rate validity that Azure DI may have missed.
        Ocr.SmartFieldExtractor.Enrich(data, azureResult.RawText ?? "");
        return new AzureExtractionResult(true, azureResult.RawText, data, null);
    }

    private async Task<OcrExtractedData> MapAzureDiToExtractedDataAsync(Guid companyId, AzureDiResult azure)
    {
        var data = new OcrExtractedData
        {
            DocumentType = MapAzureDocType(azure.DocumentType, azure.ModelId),
            Confidence = azure.OverallConfidence,
            DocumentNumber = azure.InvoiceId,
            DocumentDate = azure.InvoiceDate,
            VendorName = azure.VendorName,
            VendorTaxId = ExtractDigits(azure.VendorTaxId, 13),
            BuyerName = azure.CustomerName,
            BuyerTaxId = ExtractDigits(azure.CustomerTaxId, 13),
            SubTotal = azure.SubTotal,
            VatAmount = azure.TotalTax,
            TotalAmount = azure.InvoiceTotal ?? azure.AmountDue,
            ZoneSummary = $"Azure DI {azure.ModelId}: confidence={azure.OverallConfidence:P0}",
        };

        // Per-field confidence (cast decimal → double for OcrExtractedData dictionary)
        foreach (var (k, v) in azure.FieldConfidence)
            data.FieldConfidence[k] = (double)v;

        // Reasoning trace
        data.ReasoningTrace.Add($"[Azure DI] Model: {azure.ModelId} | Pages: {azure.PageCount} | Documents: {azure.MultiDocumentCount}");
        if (!string.IsNullOrEmpty(azure.VendorName))
            data.ReasoningTrace.Add($"[Azure DI] Vendor: {azure.VendorName}");
        if (!string.IsNullOrEmpty(azure.CustomerName))
            data.ReasoningTrace.Add($"[Azure DI] Customer: {azure.CustomerName}");
        // Surface multi-document warnings to user
        foreach (var w in azure.Warnings)
            data.ReasoningTrace.Add($"[Azure DI Warning] {w}");
        if (azure.InvoiceTotal.HasValue)
            data.ReasoningTrace.Add($"[Azure DI] Total: {azure.InvoiceTotal.Value:N2}");

        // Map line items
        foreach (var item in azure.Items)
        {
            data.Items.Add(new OcrExtractedLineItem
            {
                Description = item.Description,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Amount = item.Amount,
            });
        }

        // ─── Recover missing fields from keyValuePairs ───
        // Azure's keyValuePairs feature catches Thai-anchored data that
        // the standard Invoice schema doesn't include. We backfill the
        // gaps here — schema fields take priority, K-V fills the rest.
        ApplyKeyValueFallback(azure, data);

        // ─── Cross-validate with barcodes / QR ───
        // RD-issued e-Receipts encode a canonical amount + tax ID in a
        // QR; when present, raise field confidence to 1.0 on matching
        // schema-extracted values and flag mismatches.
        if (azure.Barcodes.Count > 0)
        {
            data.ReasoningTrace.Add($"[Azure DI] Barcodes: {azure.Barcodes.Count} detected " +
                $"({string.Join(", ", azure.Barcodes.Select(b => b.Kind ?? "?"))})");
            foreach (var bc in azure.Barcodes)
            {
                if (string.IsNullOrEmpty(bc.Value)) continue;
                // Treat any 13-digit run inside the barcode as a tax ID
                // candidate — common for RD QR which embeds buyer tax ID.
                var digits = new string(bc.Value.Where(char.IsDigit).ToArray());
                if (digits.Length >= 13)
                {
                    var taxId = digits.Substring(0, 13);
                    if (Ocr.SmartFieldExtractor.IsValidThaiTaxId(taxId))
                    {
                        if (string.IsNullOrEmpty(data.VendorTaxId)) data.VendorTaxId = taxId;
                        else if (data.VendorTaxId == taxId)
                            data.FieldConfidence["SellerTaxId"] = 1.0;
                        data.ReasoningTrace.Add($"[Azure DI Barcode] Found valid tax id {taxId} in {bc.Kind}");
                    }
                }
            }
        }

        // ─── Language detection telemetry ───
        if (azure.DetectedLanguages.Count > 0)
            data.ReasoningTrace.Add($"[Azure DI] Languages: {string.Join(", ", azure.DetectedLanguages)}");

        // NOTE: Seller/buyer swap is intentionally NOT performed here. The unified
        // swap block in ScanAsync runs AFTER OcrConfidenceGateway, ensuring all
        // OCR providers (local + Azure DI) submit pre-swap data to the gateway —
        // so checksum and math validations are consistent across paths.

        return data;
    }

    /// <summary>
    /// Fill OcrExtractedData fields that the standard Invoice schema
    /// missed using Azure's generic keyValuePairs output. Thai-anchored
    /// fields that benefit most: เลขประจำตัวผู้เสียภาษี → VendorTaxId or
    /// BuyerTaxId (we pick based on proximity in the original document),
    /// เลขที่ → DocumentNumber, วันที่ → DocumentDate, ส่งถึง/ผู้รับ → BuyerName.
    /// Only fills when the schema field is empty — never overwrites a
    /// schema-confirmed value.
    /// </summary>
    private static void ApplyKeyValueFallback(AzureDiResult azure, OcrExtractedData data)
    {
        if (azure.KeyValuePairs.Count == 0) return;

        // Tax-id-shaped keys → fill missing tax ID. We assign by which
        // side of the document the key appeared on (seller anchor vs
        // buyer anchor); approximated by keyword in the K-V's key text.
        foreach (var (key, value) in azure.KeyValuePairs)
        {
            var keyLower = key.ToLowerInvariant();

            // Tax IDs (13 digits)
            if (keyLower.Contains("เลขประจำตัว") || keyLower.Contains("tax id"))
            {
                var taxId = ExtractDigits(value, 13);
                if (!string.IsNullOrEmpty(taxId))
                {
                    var isBuyerSide = keyLower.Contains("ผู้ซื้อ") || keyLower.Contains("ลูกค้า")
                        || keyLower.Contains("customer") || keyLower.Contains("buyer");
                    if (isBuyerSide && string.IsNullOrEmpty(data.BuyerTaxId)) data.BuyerTaxId = taxId;
                    else if (string.IsNullOrEmpty(data.VendorTaxId)) data.VendorTaxId = taxId;
                }
                continue;
            }

            // Document number — "เลขที่ใบกำกับ", "Invoice No.", etc.
            if (string.IsNullOrEmpty(data.DocumentNumber)
                && (keyLower.Contains("เลขที่") || keyLower.Contains("invoice no") || keyLower.Contains("doc no")))
            {
                data.DocumentNumber = value.Trim();
                continue;
            }

            // Date — "วันที่", "Date"
            if (!data.DocumentDate.HasValue
                && (keyLower.Contains("วันที่") || keyLower == "date" || keyLower.Contains("invoice date")))
            {
                if (DateTime.TryParse(value, out var d)) data.DocumentDate = d;
                continue;
            }

            // Buyer name (Customer/ลูกค้า/ผู้ซื้อ)
            if (string.IsNullOrEmpty(data.BuyerName)
                && (keyLower.Contains("ลูกค้า") || keyLower.Contains("ผู้ซื้อ") || keyLower.Contains("customer")
                    || keyLower.Contains("ส่งถึง") || keyLower.Contains("bill to")))
            {
                data.BuyerName = value.Trim();
            }
        }

        if (azure.KeyValuePairs.Count > 0)
            data.ReasoningTrace.Add($"[Azure DI KV] {azure.KeyValuePairs.Count} key-value pairs scanned for fallback");
    }

    private static string MapAzureDocType(string? azureDocType, string? modelId)
    {
        if (modelId?.Contains("receipt", StringComparison.OrdinalIgnoreCase) == true)
            return "Receipt";
        return azureDocType?.ToLowerInvariant() switch
        {
            "invoice" => "Invoice",
            "creditnote" => "CreditNote",
            "debitnote" => "DebitNote",
            "receipt" => "Receipt",
            _ => "Invoice",
        };
    }

    private static string? ExtractDigits(string? value, int expectedLength)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.Length == expectedLength ? digits : (digits.Length > 0 ? digits : null);
    }

    /// <summary>
    /// Embedded Tesseract OCR — runs entirely in-process, no external service.
    /// Used as the last-resort fallback when Azure DI and the Python ocr-service
    /// are both unavailable. Accuracy is lower than the upper tiers but the
    /// reasoning trace + ParseThaiDocument rule-based extraction still produces
    /// usable data (vendor name, tax ID, amounts) that the user can correct.
    /// </summary>
    private async Task<(string RawText, OcrExtractedData Data)> ExtractWithEmbeddedTesseractAsync(FileAttachment file)
    {
        if (!_embeddedOcr.IsAvailable)
        {
            // Both upper tiers + embedded failed — return an empty result with
            // a clear reasoning trace. The user can still re-scan after the
            // admin installs tessdata.
            var emptyData = new OcrExtractedData
            {
                DocumentType = "Receipt",
                Confidence = 0m,
            };
            emptyData.ReasoningTrace.Add("[Embedded] ไม่พบ tessdata — กรุณา download (scripts/download-tessdata.sh)");
            return ("", emptyData);
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(file.StoragePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read file for embedded OCR: {Path}", file.StoragePath);
            var errData = new OcrExtractedData { DocumentType = "Receipt", Confidence = 0m };
            errData.ReasoningTrace.Add($"[Embedded] อ่านไฟล์ไม่ได้: {ex.Message}");
            return ("", errData);
        }

        var result = await _embeddedOcr.ExtractTextAsync(bytes, file.ContentType ?? "", file.OriginalFileName);
        if (!result.Success)
        {
            var errData = new OcrExtractedData { DocumentType = "Receipt", Confidence = 0m };
            errData.ReasoningTrace.Add($"[Embedded] OCR ล้มเหลว: {result.Error ?? "unknown"}");
            return ("", errData);
        }

        // ParseThaiDocument is the same rule-based extractor used by the Python
        // local service — it works on raw text from any source. So Tesseract's
        // output flows through the same field extraction (vendor name, tax ID,
        // amounts, document type detection from keywords).
        // Run the rule-based parser against a normalized copy of the
        // text so the Thai keyword anchors actually match. Tesseract
        // emits "ค ่ า ไฟ ฟ้า" with spaces between every cluster; without
        // collapsing those spaces every Thai regex below silently misses
        // its target. The original text is kept for storage / display.
        var normalizedForParse = Ocr.ThaiTextNormalizer.Normalize(result.Text ?? "");
        var data = ParseThaiDocument(normalizedForParse);
        // Use Tesseract's mean word confidence as the baseline; ParseThaiDocument
        // may bump it up if it found high-signal keywords (e.g. "ใบกำกับภาษี").
        data.Confidence = Math.Max(result.Confidence, data.Confidence);
        data.ReasoningTrace.Add($"[Embedded] Tesseract OCR confidence = {result.Confidence:P0}");
        // Pull every remaining field via real-world constraints (tax-id
        // checksum, vendor≠buyer mutual exclusion, math invariants, WHT
        // normalization, date range, doc-number plausibility) — this is the
        // path that benefits most from the smart extractor.
        Ocr.SmartFieldExtractor.Enrich(data, result.Text ?? "");
        return (result.Text, data);
    }

    private async Task<(string RawText, OcrExtractedData Data)> ExtractWithLocalServiceAsync(FileAttachment file)
    {
        var serviceUrl = _configuration["Ocr:LocalServiceUrl"] ?? "http://localhost:8501";
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(120);

        // All failure paths in this method THROW (rather than return a stub) so the
        // caller in ScanAsync triggers its try/catch and falls through to Tier 3
        // (Embedded Tesseract). Returning a stub here would set extractedData to a
        // non-null value, making the cascade skip the embedded fallback entirely
        // and leave rawText = filename — which is exactly the production bug the
        // user reported on 2026-05-11.
        byte[] fileData;
        try { fileData = await File.ReadAllBytesAsync(file.StoragePath); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Local OCR: ไม่สามารถอ่านไฟล์ต้นทาง ({ex.Message})", ex);
        }

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileData);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            file.ContentType ?? "application/octet-stream");
        form.Add(fileContent, "file", file.OriginalFileName);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync($"{serviceUrl}/ocr/extract", form);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Local OCR service unreachable at {Url}: {Err} — will fall through to embedded Tesseract",
                serviceUrl, ex.Message);
            throw new InvalidOperationException(
                $"Local OCR service ไม่ตอบสนอง ({serviceUrl}): {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Local OCR service returned {Status}", response.StatusCode);
            throw new InvalidOperationException(
                $"Local OCR service ตอบกลับ HTTP {(int)response.StatusCode}");
        }

        try
        {
            var json = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var data = new OcrExtractedData
            {
                DocumentType = root.TryGetProperty("document_type", out var dt) ? (dt.GetString() ?? "Receipt") : "Receipt",
                Confidence = root.TryGetProperty("confidence", out var cf) ? (decimal)cf.GetDouble() : 0.5m,
                VendorName = root.TryGetProperty("vendor_name", out var vn) ? vn.GetString() : null,
                VendorTaxId = root.TryGetProperty("vendor_tax_id", out var vt) ? vt.GetString() : null,
                BuyerName = root.TryGetProperty("buyer_name", out var bn) ? bn.GetString() : null,
                BuyerTaxId = root.TryGetProperty("buyer_tax_id", out var bt) ? bt.GetString() : null,
                DocumentNumber = root.TryGetProperty("document_number", out var dn) ? dn.GetString() : null,
                SubTotal = root.TryGetProperty("subtotal", out var st) && st.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)st.GetDouble() : null,
                VatAmount = root.TryGetProperty("vat_amount", out var va) && va.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)va.GetDouble() : null,
                TotalAmount = root.TryGetProperty("total_amount", out var ta) && ta.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)ta.GetDouble() : null,
                ExpenseCategory = root.TryGetProperty("expense_category", out var ec) ? ec.GetString() : null,
                HasWht = root.TryGetProperty("has_wht", out var hw) && hw.ValueKind == System.Text.Json.JsonValueKind.True,
                WhtRate = root.TryGetProperty("wht_rate", out var wr) && wr.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)wr.GetDouble() : null,
                PaymentTermsDays = root.TryGetProperty("payment_terms_days", out var pt) && pt.ValueKind == System.Text.Json.JsonValueKind.Number ? pt.GetInt32() : null,
                ZoneSummary = root.TryGetProperty("ocr_engine", out var oe) ? $"Local OCR: {oe.GetString()}" : "Local OCR: paddleocr",
            };

            if (root.TryGetProperty("document_date", out var dd) && dd.GetString() is string dateStr && DateTime.TryParse(dateStr, out var parsedDate))
                data.DocumentDate = parsedDate;

            // ── Per-field confidence (parity with Azure DI) ──
            if (root.TryGetProperty("field_confidence", out var fc) && fc.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var prop in fc.EnumerateObject())
                {
                    if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Number)
                        data.FieldConfidence[prop.Name] = prop.Value.GetDouble();
                }
            }

            // ── Reasoning trace ──
            if (root.TryGetProperty("reasoning_trace", out var rtTrace) && rtTrace.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var entry in rtTrace.EnumerateArray())
                {
                    var s = entry.GetString();
                    if (!string.IsNullOrEmpty(s)) data.ReasoningTrace.Add(s);
                }
            }
            else if (root.TryGetProperty("reasoning", out var reason) && reason.GetString() is string reasonStr)
            {
                data.ReasoningTrace.Add($"[Local AI] {reasonStr}");
            }

            // ── Multi-page warnings ──
            if (root.TryGetProperty("warnings", out var wn) && wn.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var w in wn.EnumerateArray())
                {
                    var s = w.GetString();
                    if (!string.IsNullOrEmpty(s)) data.ReasoningTrace.Add($"[Warning] {s}");
                }
            }

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
            // Apply universal constraints — keeps the data shape consistent
            // across all three OCR providers and recovers fields the Python
            // service may have missed (e.g. buyer tax id, math invariants).
            Ocr.SmartFieldExtractor.Enrich(data, rawText);
            // A successful HTTP 200 with empty raw_text is still a "service worked but
            // couldn't read the document" — let the caller decide whether to fall
            // through to embedded. We DON'T throw here; we return the empty result
            // because some PDFs legitimately have no text and the gateway should
            // still get a chance to mark it low-confidence.
            return (rawText, data);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // Malformed JSON or unexpected schema from the microservice — treat as
            // service failure so cascade falls through to embedded.
            _logger.LogError(ex, "Local OCR service returned unparseable response");
            throw new InvalidOperationException(
                $"Local OCR: response parse error ({ex.Message})", ex);
        }
    }

    public async Task SubmitCorrectionAsync(Guid companyId, Guid scanResultId, OcrCorrectionRequest correction)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        // Capture previous values BEFORE updating — used as negative examples
        var prevVendorName = result.ExtractedVendorName;
        var prevVendorTaxId = result.ExtractedVendorTaxId;
        var prevDocNumber = result.ExtractedDocumentNumber;

        // Update the scan result with corrected data
        if (correction.DocumentType != null)
        {
            // DocumentType corrections target the SCANNED paper type — keep the
            // ScannedDocumentType column in lockstep so the two fields don't
            // diverge after user edits.
            result.DocumentType = correction.DocumentType;
            result.ScannedDocumentType = correction.DocumentType;
        }
        if (correction.TargetDocumentType != null) result.TargetDocumentType = correction.TargetDocumentType;
        if (correction.VendorName != null) result.ExtractedVendorName = correction.VendorName;
        if (correction.VendorTaxId != null) result.ExtractedVendorTaxId = correction.VendorTaxId;
        if (correction.DocumentNumber != null) result.ExtractedDocumentNumber = correction.DocumentNumber;
        if (correction.DocumentDate.HasValue) result.ExtractedDate = correction.DocumentDate;
        if (correction.SubTotal.HasValue) result.ExtractedSubTotal = correction.SubTotal;
        if (correction.VatAmount.HasValue) result.ExtractedVatAmount = correction.VatAmount;
        if (correction.TotalAmount.HasValue) result.ExtractedTotalAmount = correction.TotalAmount;

        await _db.SaveChangesAsync();

        // ───── Train VendorIntelligence with the corrected target type ─────
        // When the user changes "เอกสารที่จะสร้าง" we want next scan of the
        // same vendor to predict the same target — that's exactly what
        // TrainFromAdminAsync does (per-tenant, weight 1). Without this the
        // correction would update only the current row, not future scans.
        if (!string.IsNullOrEmpty(correction.TargetDocumentType)
            && Enum.TryParse<DocumentType>(correction.TargetDocumentType, ignoreCase: true, out var corrTarget))
        {
            try
            {
                await _vendorIntel.TrainFromAdminAsync(
                    companyId,
                    correction.VendorTaxId ?? result.ExtractedVendorTaxId,
                    correction.VendorName ?? result.ExtractedVendorName,
                    corrTarget,
                    debitAccountCode: correction.DebitAccountCode,
                    debitAccountName: null,
                    whtRate: correction.WhtRate,
                    paymentTermsDays: null,
                    weight: 1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to train VendorIntel from correction");
            }
        }

        // ───── Train the category learner from this correction ─────
        // When user manually picks a debit account (the expense category) for a vendor,
        // remember it so future scans of the same vendor pre-fill that account.
        if (!string.IsNullOrEmpty(correction.DebitAccountCode))
        {
            var debitName = await _db.ChartOfAccounts
                .Where(a => a.CompanyId == companyId && a.AccountCode == correction.DebitAccountCode && !a.IsDeleted)
                .Select(a => a.AccountName)
                .FirstOrDefaultAsync();
            await _categoryLearner.RecordAsync(
                companyId,
                correction.VendorTaxId ?? result.ExtractedVendorTaxId,
                correction.VendorName ?? result.ExtractedVendorName,
                correction.ExpenseCategory ?? result.ExpenseCategory,
                correction.DebitAccountCode,
                debitName);
        }

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

        // Learn patterns from correction using zone analyzer
        try
        {
            if (!string.IsNullOrEmpty(result.RawTextContent))
            {
                var learnedPatterns = DocumentZoneAnalyzer.LearnFromCorrection(
                    result.RawTextContent, companyId,
                    correction.VendorTaxId ?? result.ExtractedVendorTaxId,
                    correction.VendorName, correction.VendorTaxId,
                    correction.DocumentNumber, correction.TotalAmount,
                    previousVendorName: prevVendorName,
                    previousTaxId: prevVendorTaxId,
                    previousDocNumber: prevDocNumber);

                foreach (var newPattern in learnedPatterns)
                {
                    if (newPattern.IsNegativeExample)
                    {
                        // Negative example: identified by FieldName + NegativeValue
                        var existingNeg = await _db.OcrLearnedPatterns
                            .FirstOrDefaultAsync(p => p.CompanyId == companyId
                                && p.FieldName == newPattern.FieldName
                                && p.IsNegativeExample
                                && p.NegativeValue == newPattern.NegativeValue
                                && (p.VendorTaxId == newPattern.VendorTaxId || (p.VendorTaxId == null && newPattern.VendorTaxId == null)));
                        if (existingNeg != null)
                        {
                            existingNeg.FailureCount++;
                            existingNeg.LastConfirmedAt = DateTime.UtcNow;
                        }
                        else
                        {
                            _db.OcrLearnedPatterns.Add(newPattern);
                        }
                    }
                    else
                    {
                        var existing = await _db.OcrLearnedPatterns
                            .FirstOrDefaultAsync(p => p.CompanyId == companyId
                                && !p.IsNegativeExample
                                && p.FieldName == newPattern.FieldName
                                && p.ContextKeyword == newPattern.ContextKeyword
                                && (p.VendorTaxId == newPattern.VendorTaxId || (p.VendorTaxId == null && newPattern.VendorTaxId == null)));

                        if (existing != null)
                        {
                            existing.TimesConfirmed++;
                            existing.LastConfirmedAt = DateTime.UtcNow;
                            existing.ExtractionRegex = newPattern.ExtractionRegex;
                            existing.SearchRadius = Math.Max(existing.SearchRadius, newPattern.SearchRadius);
                        }
                        else
                        {
                            _db.OcrLearnedPatterns.Add(newPattern);
                        }
                    }
                }
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to learn patterns from correction");
        }
    }

    /// <summary>
    /// Look up the extracted Tax ID against DBD (กรมพัฒนาธุรกิจการค้า) and use the
    /// authoritative result as ground truth. Differences from OCR become training signals.
    /// </summary>
    private async Task EnrichFromDbdAsync(Guid companyId, OcrExtractedData data, OcrScanResult scanResult)
    {
        if (string.IsNullOrEmpty(data.VendorTaxId) || data.VendorTaxId.Length != 13)
            return;

        DbdCompanyResult? dbd = null;
        try
        {
            dbd = await _dbdLookup.GetByJuristicIdAsync(data.VendorTaxId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DBD lookup failed for TaxId {TaxId}", data.VendorTaxId);
        }

        if (dbd == null)
        {
            scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") +
                $"\n[DBD] ไม่พบข้อมูลในกรมพัฒนาธุรกิจการค้า (TaxID: {data.VendorTaxId}) — ใช้ข้อมูลจาก OCR";
            data.DbdLookupAttempted = true;
            return;
        }

        // DBD found — adopt as authoritative
        data.DbdLookupAttempted = true;
        data.DbdMatched = true;
        data.DbdCanonicalName = dbd.NameTh;
        data.DbdAddress = dbd.Address;
        data.DbdJuristicType = dbd.JuristicType;
        data.DbdStatus = dbd.Status;

        // Compare OCR's vendor name with DBD canonical
        var ocrName = data.VendorName?.Trim();
        var dbdName = dbd.NameTh?.Trim();

        bool nameMatches = false;
        if (!string.IsNullOrEmpty(ocrName) && !string.IsNullOrEmpty(dbdName))
        {
            // Normalize: remove "บริษัท ... จำกัด" wrappers and whitespace for comparison
            var normalizedOcr = NormalizeCompanyName(ocrName);
            var normalizedDbd = NormalizeCompanyName(dbdName);
            nameMatches = string.Equals(normalizedOcr, normalizedDbd, StringComparison.OrdinalIgnoreCase)
                       || normalizedDbd.Contains(normalizedOcr, StringComparison.OrdinalIgnoreCase)
                       || normalizedOcr.Contains(normalizedDbd, StringComparison.OrdinalIgnoreCase);
        }

        if (nameMatches)
        {
            // OCR was correct — boost confidence and use DBD's exact form for Contact
            data.FieldConfidence["SellerName"] = 1.0;
            data.FieldConfidence["SellerTaxId"] = 1.0;
            data.VendorName = dbd.NameTh; // use DBD's exact spelling
            data.ReasoningTrace.Add($"[DBD] ✓ ชื่อบริษัทตรงกับ DBD ({dbd.NameTh}) — ใช้ชื่อทางการ");
        }
        else if (string.IsNullOrEmpty(ocrName))
        {
            // OCR didn't find a name but DBD has one — use DBD
            data.VendorName = dbd.NameTh;
            data.FieldConfidence["SellerName"] = 1.0;
            data.ReasoningTrace.Add($"[DBD] ใช้ชื่อจาก DBD: {dbd.NameTh}");
        }
        else
        {
            // OCR mismatch — DBD wins, but record OCR's wrong reading as negative training
            data.ReasoningTrace.Add($"[DBD] ⚠ OCR อ่านได้ '{ocrName}' แต่ DBD ระบุ '{dbd.NameTh}' — ใช้จาก DBD และเรียนรู้");
            await RecordOcrMismatchAsync(companyId, data.VendorTaxId, "SellerName",
                wrongValue: ocrName, correctValue: dbd.NameTh);
            data.VendorName = dbd.NameTh;
            // Confidence stays moderate because OCR misread
            data.FieldConfidence["SellerName"] = 0.95;
        }

        // Update scanResult so subsequent saves use the canonical name
        scanResult.ExtractedVendorName = data.VendorName;

        scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") +
            $"\n[DBD] ✓ พบข้อมูล: {dbd.NameTh}" +
            (string.IsNullOrEmpty(dbd.Status) ? "" : $" (สถานะ: {dbd.Status})") +
            (string.IsNullOrEmpty(dbd.Address) ? "" : $"\n[DBD Address] {dbd.Address}");
    }

    private static string NormalizeCompanyName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var n = name.Trim();
        // Strip common legal-entity prefixes/suffixes for comparison only
        n = System.Text.RegularExpressions.Regex.Replace(n, @"^(บริษัท|ห้างหุ้นส่วนจำกัด|ห้างหุ้นส่วนสามัญ|หจก\.?|บจก\.?|ร้าน)\s*", "");
        n = System.Text.RegularExpressions.Regex.Replace(n, @"\s*จำกัด\s*\(?มหาชน\)?\s*$", "");
        n = System.Text.RegularExpressions.Regex.Replace(n, @"\s*จำกัด\s*$", "");
        n = System.Text.RegularExpressions.Regex.Replace(n, @"\s+", " ");
        return n.Trim();
    }

    /// <summary>
    /// Persist a negative training example: this OCR-extracted value was wrong
    /// (according to DBD) for this vendor.
    /// </summary>
    private async Task RecordOcrMismatchAsync(Guid companyId, string vendorTaxId, string fieldName,
        string? wrongValue, string? correctValue)
    {
        if (string.IsNullOrEmpty(wrongValue)) return;
        try
        {
            var existing = await _db.OcrLearnedPatterns.FirstOrDefaultAsync(p =>
                p.CompanyId == companyId && p.IsNegativeExample &&
                p.FieldName == fieldName && p.NegativeValue == wrongValue &&
                p.VendorTaxId == vendorTaxId);
            if (existing != null)
            {
                existing.FailureCount++;
                existing.LastConfirmedAt = DateTime.UtcNow;
            }
            else
            {
                _db.OcrLearnedPatterns.Add(new OcrLearnedPattern
                {
                    CompanyId = companyId,
                    VendorTaxId = vendorTaxId,
                    FieldName = fieldName,
                    ContextKeyword = "(dbd-mismatch)",
                    IsNegativeExample = true,
                    NegativeValue = wrongValue,
                    FailureCount = 1,
                    CreatedBy = "DBD-Verify"
                });
            }
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record DBD mismatch for {Field}", fieldName);
        }
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

        var upperText = text.ToUpperInvariant();

        // Document type detection — TaxInvoice wins even if Receipt text also present
        bool hasTaxInvoice = text.Contains("ใบกำกับภาษี") || text.Contains("ใบกํากับภาษี") || upperText.Contains("TAX INVOICE");
        bool hasReceipt = text.Contains("ใบเสร็จรับเงิน") || upperText.Contains("RECEIPT");
        bool hasInvoice = text.Contains("ใบแจ้งหนี้") || (upperText.Contains("INVOICE") && !upperText.Contains("TAX INVOICE"));
        bool hasPurchaseOrder = text.Contains("ใบสั่งซื้อ") || upperText.Contains("PURCHASE ORDER");
        bool hasWhtDoc = text.Contains("หนังสือรับรอง") || text.Contains("50 ทวิ") || text.Contains("ภาษีหัก ณ ที่จ่าย");
        bool hasCreditNote = text.Contains("ใบลดหนี้") || upperText.Contains("CREDIT NOTE");
        bool hasDebitNote = text.Contains("ใบเพิ่มหนี้") || upperText.Contains("DEBIT NOTE");
        bool hasCertInLieu = text.Contains("ใบรับรองแทนใบเสร็จ") || upperText.Contains("CERTIFICATE IN LIEU");

        if (hasTaxInvoice) { data.DocumentType = "TaxInvoice"; data.Confidence = 0.95m; }
        else if (hasCertInLieu) { data.DocumentType = "CertificateInLieu"; data.Confidence = 0.90m; }
        else if (hasCreditNote) { data.DocumentType = "CreditNote"; data.Confidence = 0.90m; }
        else if (hasDebitNote) { data.DocumentType = "DebitNote"; data.Confidence = 0.90m; }
        else if (hasWhtDoc) { data.DocumentType = "WHT"; data.Confidence = 0.90m; }
        else if (hasPurchaseOrder) { data.DocumentType = "PurchaseOrder"; data.Confidence = 0.85m; }
        else if (hasInvoice) { data.DocumentType = "Invoice"; data.Confidence = 0.90m; }
        else if (hasReceipt) { data.DocumentType = "Receipt"; data.Confidence = 0.88m; }
        else { data.DocumentType = "Receipt"; data.Confidence = 0.5m; }

        // Extract ALL 13-digit tax IDs
        var taxIdPattern = @"(\d{1}[-\s]?\d{4}[-\s]?\d{5}[-\s]?\d{2}[-\s]?\d{1})";
        var allTaxIds = Regex.Matches(text, taxIdPattern)
            .Cast<Match>()
            .Select(m => Regex.Replace(m.Groups[1].Value, @"[-\s]", ""))
            .Where(id => id.Length == 13)
            .Distinct()
            .ToList();

        // Extract ALL company names with position
        var companyPattern = @"(บริษัท|ห้างหุ้นส่วน(?:จำกัด|สามัญ)?|ร้าน)\s*(.+?)(?:\s*จำกัด(?:\s*\(มหาชน\))?|\s*\(|(?=\s*เลข|\s*สาขา|\s*ที่อยู่|\s*\d{1}[-\s]?\d{4})|$)";
        var companyMatches = Regex.Matches(text, companyPattern, RegexOptions.Multiline);
        var companyNames = new List<(string FullName, int Position)>();
        foreach (Match cm in companyMatches)
        {
            var prefix = cm.Groups[1].Value;
            var name = cm.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2) continue;
            var afterMatch = text.Substring(cm.Index, Math.Min(cm.Length + 30, text.Length - cm.Index));
            var suffix = "";
            if (afterMatch.Contains("จำกัด"))
                suffix = afterMatch.Contains("มหาชน") ? " จำกัด (มหาชน)" : " จำกัด";
            companyNames.Add(($"{prefix} {name}{suffix}".Trim(), cm.Index));
        }

        // Distinguish vendor from our company by seller/buyer section context
        var sellerKeywords = new[] { "ผู้ขาย", "ผู้ออกใบ", "ผู้ให้บริการ", "SELLER", "FROM", "ผู้ออก" };
        var buyerKeywords = new[] { "ผู้ซื้อ", "ลูกค้า", "นามผู้ซื้อ", "BUYER", "CUSTOMER", "BILL TO", "SOLD TO", "ส่งถึง" };

        string? vendorName = null;
        string? vendorTaxId = null;

        if (companyNames.Count >= 2)
        {
            int sellerIdx = -1, buyerIdx = -1;
            foreach (var kw in sellerKeywords)
            {
                var kwPos = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                if (kwPos >= 0) { sellerIdx = kwPos; break; }
            }
            foreach (var kw in buyerKeywords)
            {
                var kwPos = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                if (kwPos >= 0) { buyerIdx = kwPos; break; }
            }

            if (sellerIdx >= 0 && buyerIdx >= 0)
            {
                var sellerCompany = companyNames.OrderBy(c => Math.Abs(c.Position - sellerIdx)).First();
                vendorName = sellerCompany.FullName;
                if (allTaxIds.Count >= 2)
                {
                    var taxIdPositions = Regex.Matches(text, taxIdPattern)
                        .Cast<Match>()
                        .Select(m => (Id: Regex.Replace(m.Groups[1].Value, @"[-\s]", ""), Pos: m.Index))
                        .Where(x => x.Id.Length == 13)
                        .ToList();
                    vendorTaxId = taxIdPositions.OrderBy(x => Math.Abs(x.Pos - sellerIdx)).First().Id;
                }
                else if (allTaxIds.Count == 1)
                    vendorTaxId = allTaxIds[0];
            }
            else
            {
                vendorName = companyNames[0].FullName;
                vendorTaxId = allTaxIds.Count > 0 ? allTaxIds[0] : null;
            }
        }
        else if (companyNames.Count == 1)
        {
            vendorName = companyNames[0].FullName;
            vendorTaxId = allTaxIds.Count > 0 ? allTaxIds[0] : null;
        }
        else if (allTaxIds.Count > 0)
            vendorTaxId = allTaxIds[0];

        data.VendorName = vendorName;
        data.VendorTaxId = vendorTaxId;

        // Document number
        string?[] docNumPatterns = {
            @"เลขที่\s*[:：]?\s*([A-Za-z0-9\-/]+\d+)",
            @"(?:No|เลข(?:ที่)?)\s*\.?\s*[:：]?\s*([A-Za-z0-9\-/]+)",
            @"(?:INV|REC|TAX|TX|IV|PO|CN|DN)[\-/]?\s*(\d[\d\-/]*)",
        };
        foreach (var pattern in docNumPatterns)
        {
            var docNumMatch = Regex.Match(text, pattern!, RegexOptions.IgnoreCase);
            if (docNumMatch.Success) { data.DocumentNumber = docNumMatch.Groups[1].Value.Trim(); break; }
        }

        // Date — multiple patterns including Thai month names
        string[] datePatterns = {
            @"(?:วันที่|Date)\s*[:：]?\s*(\d{1,2})\s*[/\-\.]\s*(\d{1,2})\s*[/\-\.]\s*(\d{2,4})",
            @"(\d{1,2})\s*[/\-\.]\s*(\d{1,2})\s*[/\-\.]\s*(\d{4})",
            @"(\d{1,2})\s+(ม\.?ค\.?|ก\.?พ\.?|มี\.?ค\.?|เม\.?ย\.?|พ\.?ค\.?|มิ\.?ย\.?|ก\.?ค\.?|ส\.?ค\.?|ก\.?ย\.?|ต\.?ค\.?|พ\.?ย\.?|ธ\.?ค\.?)\s+(\d{4})",
            @"(\d{1,2})\s*[/\-\.]\s*(\d{1,2})\s*[/\-\.]\s*(\d{2})\b",
        };
        foreach (var pattern in datePatterns)
        {
            var dateMatch = Regex.Match(text, pattern);
            if (!dateMatch.Success) continue;
            int day = int.Parse(dateMatch.Groups[1].Value);
            string monthStr = dateMatch.Groups[2].Value;
            int year = int.Parse(dateMatch.Groups[3].Value);
            if (year > 2500) year -= 543;
            if (year < 100) year += 2000;
            int month;
            if (int.TryParse(monthStr, out month)) { /* numeric */ }
            else
            {
                var thaiMonths = new Dictionary<string, int>
                {
                    {"ม.ค", 1}, {"มค", 1}, {"ก.พ", 2}, {"กพ", 2},
                    {"มี.ค", 3}, {"มีค", 3}, {"เม.ย", 4}, {"เมย", 4},
                    {"พ.ค", 5}, {"พค", 5}, {"มิ.ย", 6}, {"มิย", 6},
                    {"ก.ค", 7}, {"กค", 7}, {"ส.ค", 8}, {"สค", 8},
                    {"ก.ย", 9}, {"กย", 9}, {"ต.ค", 10}, {"ตค", 10},
                    {"พ.ย", 11}, {"พย", 11}, {"ธ.ค", 12}, {"ธค", 12},
                };
                month = 1;
                foreach (var (key, val) in thaiMonths)
                    if (monthStr.Contains(key)) { month = val; break; }
            }
            if (day >= 1 && day <= 31 && month >= 1 && month <= 12 && year >= 1900)
            {
                try { data.DocumentDate = new DateTime(year, month, day); } catch { }
                break;
            }
        }

        // Amount — total
        string[] totalPatterns = {
            @"(?:รวม(?:เงิน)?(?:ทั้งสิ้น|ทั้งหมด|สุทธิ)|ยอดรวม(?:สุทธิ)?|GRAND\s*TOTAL|NET\s*TOTAL)\s*[:：]?\s*([\d,]+\.?\d*)",
            @"(?:TOTAL)\s*[:：]?\s*([\d,]+\.?\d*)",
            @"(?:รวมเงิน|จำนวนเงินรวม)\s*[:：]?\s*([\d,]+\.?\d*)",
        };
        foreach (var pattern in totalPatterns)
        {
            var totalMatch = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (totalMatch.Success) { data.TotalAmount = ParseDecimal(totalMatch.Groups[1].Value); if (data.TotalAmount > 0) break; }
        }

        // VAT
        var vatMatch2 = Regex.Match(text, @"(?:ภาษีมูลค่าเพิ่ม|VAT|Vat)\s*(?:7\s*%?)?\s*[:：]?\s*([\d,]+\.?\d*)", RegexOptions.IgnoreCase);
        if (vatMatch2.Success) data.VatAmount = ParseDecimal(vatMatch2.Groups[1].Value);

        // SubTotal
        var subMatch2 = Regex.Match(text, @"(?:ราคาสินค้า|ก่อนภาษี|รวมเงิน(?!ทั้ง)|SUB\s*TOTAL|Subtotal|ราคารวม)\s*[:：]?\s*([\d,]+\.?\d*)", RegexOptions.IgnoreCase);
        if (subMatch2.Success) data.SubTotal = ParseDecimal(subMatch2.Groups[1].Value);

        // Calculate missing values
        if (data.TotalAmount > 0 && data.VatAmount > 0 && data.SubTotal == null)
            data.SubTotal = data.TotalAmount - data.VatAmount;
        else if (data.SubTotal > 0 && data.VatAmount > 0 && data.TotalAmount == null)
            data.TotalAmount = data.SubTotal + data.VatAmount;
        else if (data.TotalAmount > 0 && data.SubTotal == null && data.VatAmount == null && hasTaxInvoice)
        {
            data.SubTotal = Math.Round(data.TotalAmount.Value / 1.07m, 2);
            data.VatAmount = data.TotalAmount.Value - data.SubTotal.Value;
        }

        // GL account suggestions
        var accountMap = new Dictionary<string, (string Dc, string Dn, string Cc, string Cn, string Cat)>
        {
            ["TaxInvoice"] = ("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า", "ค่าสินค้า"),
            ["Invoice"] = ("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า", "ค่าสินค้า"),
            ["Receipt"] = ("5300", "ค่าใช้จ่ายบริหาร", "1110", "เงินสด", "ค่าบริการ"),
            ["PurchaseOrder"] = ("1200", "สินค้าคงเหลือ", "2100", "เจ้าหนี้การค้า", "ค่าสินค้า"),
            ["WHT"] = ("2170", "ภาษีหัก ณ ที่จ่าย", "1110", "เงินสด", "อื่นๆ"),
            ["CreditNote"] = ("2100", "เจ้าหนี้การค้า", "5100", "ต้นทุนขาย", "ค่าสินค้า"),
            ["DebitNote"] = ("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า", "ค่าสินค้า"),
        };
        if (accountMap.TryGetValue(data.DocumentType, out var acct))
        {
            data.DebitAccountCode = acct.Dc; data.DebitAccountName = acct.Dn;
            data.CreditAccountCode = acct.Cc; data.CreditAccountName = acct.Cn;
            data.ExpenseCategory = acct.Cat;
            if (data.VatAmount > 0) { data.VatAccountCode = "1400"; data.VatAccountName = "ภาษีซื้อ"; }
        }

        // WHT detection
        var whtMatch = Regex.Match(text, @"หัก\s*ณ\s*ที่จ่าย|ภาษี\s*หัก|WHT|W/?T", RegexOptions.IgnoreCase);
        if (whtMatch.Success)
        {
            data.HasWht = true;
            var whtArea = text.Substring(Math.Max(0, whtMatch.Index - 20),
                Math.Min(whtMatch.Length + 50, text.Length - Math.Max(0, whtMatch.Index - 20)));
            var rateMatch = Regex.Match(whtArea, @"(\d+)\s*%");
            if (rateMatch.Success)
            {
                var rate = int.Parse(rateMatch.Groups[1].Value);
                if (rate is 1 or 2 or 3 or 5 or 10 or 15) data.WhtRate = rate;
            }
        }

        // Payment terms
        var termsMatch = Regex.Match(text, @"(?:ชำระ|จ่าย).*?(?:ภายใน|within)\s*(\d+)\s*(?:วัน|days)", RegexOptions.IgnoreCase);
        if (termsMatch.Success)
            data.PaymentTermsDays = int.Parse(termsMatch.Groups[1].Value);

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

        var rows = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync();

        var items = rows.Select(r => MapToResponse(r)).ToList();

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

        // Prefer the inferred TargetDocumentType (what the role inferrer
        // decided we should book) over the scanned paper type.
        DocumentType docType;
        if (!string.IsNullOrEmpty(result.TargetDocumentType)
            && Enum.TryParse<DocumentType>(result.TargetDocumentType, ignoreCase: true, out var inferredTarget))
        {
            docType = inferredTarget;
        }
        else
        {
            docType = result.DocumentType switch
            {
                "Invoice" or "TaxInvoice" => DocumentType.PurchaseInvoice,
                "Receipt" => DocumentType.PaymentVoucher,
                "CertificateInLieu" => DocumentType.CertificateInLieu,
                _ => DocumentType.Expense
            };
        }

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

        // Re-link scanned file to the created document (orphan prevention)
        await RelinkScanFileToDocumentAsync(companyId, result.FileAttachmentId, document.Id);

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
        // Prefer the inferred TargetDocumentType (set by OcrDocumentRoleInferrer).
        // Fall back to the legacy scanned-type-based mapping for older rows that
        // pre-date the inference step.
        DocumentType docType;
        if (!string.IsNullOrEmpty(scan.TargetDocumentType)
            && Enum.TryParse<DocumentType>(scan.TargetDocumentType, ignoreCase: true, out var inferredTarget))
        {
            docType = inferredTarget;
        }
        else
        {
            docType = scan.DocumentType switch
            {
                "Invoice" or "TaxInvoice" => DocumentType.PurchaseInvoice,
                "Receipt" => DocumentType.PaymentVoucher,   // paid receipt → payment voucher
                "CreditNote" => DocumentType.CreditNote,
                "DebitNote" => DocumentType.DebitNote,
                "CertificateInLieu" => DocumentType.CertificateInLieu,
                _ => DocumentType.Expense
            };
        }

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

        // Re-link the original scanned file to the new Document so users see it as
        // an attachment when they open the document. Without this, the file lives
        // forever orphaned under EntityType="OcrScan" + a placeholder Guid.
        await RelinkScanFileToDocumentAsync(companyId, scan.FileAttachmentId, document.Id);
    }

    /// <summary>
    /// Move the OCR-uploaded file from its placeholder OcrScan entity to the
    /// real Document that just got created. Updates EntityType + EntityId in place
    /// (no physical file move — same StoragePath).
    /// </summary>
    private async Task RelinkScanFileToDocumentAsync(Guid companyId, Guid? fileAttachmentId, Guid documentId)
    {
        if (!fileAttachmentId.HasValue) return;
        var attachment = await _db.FileAttachments
            .FirstOrDefaultAsync(f => f.Id == fileAttachmentId.Value && f.CompanyId == companyId);
        if (attachment == null) return;
        attachment.EntityType = "Document";
        attachment.EntityId = documentId;
        await _db.SaveChangesAsync();
        _logger.LogInformation("Re-linked scan file {FileId} to Document {DocId}", attachment.Id, documentId);
    }

    /// <summary>
    /// Register one OCR'd line item as a FixedAsset, delegating to the
    /// existing FixedAssetService.CreateAsync (no duplication of asset
    /// lifecycle logic) AND posting the initial capitalization Journal
    /// Entry inside a DB transaction so the books stay balanced even if
    /// any step fails.
    ///
    /// Journal entry shape:
    ///   Dr: AssetAccount         <PurchaseCost>
    ///   Cr: AccruedPayables /    <PurchaseCost>     (default credit account)
    ///       Cash
    ///
    /// When the scan's MatchedContact exists, the JE Description records
    /// the vendor. After registration:
    ///   • The scan's HasPotentialFixedAsset flag is cleared and the
    ///     line is removed from PotentialAssetLinesJson — so the UI
    ///     stops alerting on the same scan.
    ///   • Auto-create remains suppressed until the user explicitly
    ///     dismisses (or until all asset candidates have been registered).
    /// </summary>
    public async Task<object> RegisterAssetFromScanAsync(
        Guid companyId, Guid scanResultId,
        Controllers.OcrController.RegisterAssetFromScanRequest req,
        Services.Interfaces.IFixedAssetService assetService,
        string createdBy)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.Id == scanResultId && r.CompanyId == companyId)
            ?? throw new InvalidOperationException("OCR scan result not found.");
        if (scan.ScanStatus != "Completed")
            throw new InvalidOperationException("Scan ยังไม่เสร็จ — ไม่สามารถลงทะเบียนสินทรัพย์");

        // Resolve the line item from the stored ExtractedItemsJson so we
        // can prefill PurchaseCost / Description when the request omits
        // them. The user may also edit any field via the request body.
        OcrLineItemDto? line = null;
        if (!string.IsNullOrEmpty(scan.ExtractedItemsJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(scan.ExtractedItemsJson);
                var arr = doc.RootElement;
                if (arr.ValueKind == System.Text.Json.JsonValueKind.Array
                    && req.LineIndex >= 0 && req.LineIndex < arr.GetArrayLength())
                {
                    var el = arr[req.LineIndex];
                    line = new OcrLineItemDto(
                        el.TryGetProperty("Description", out var d) ? d.GetString() : null,
                        el.TryGetProperty("Quantity", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)q.GetDouble() : null,
                        el.TryGetProperty("UnitPrice", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)u.GetDouble() : null,
                        el.TryGetProperty("Amount", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)a.GetDouble() : null,
                        el.TryGetProperty("SuggestedAccountCode", out var s) ? s.GetString() : null);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse ExtractedItemsJson on scan {Id}", scanResultId);
            }
        }

        var purchaseCost = req.PurchaseCost ?? line?.Amount ?? line?.UnitPrice ?? 0m;
        if (purchaseCost <= 0)
            throw new InvalidOperationException("ราคาซื้อต้องมากกว่า 0");
        var purchaseDate = req.PurchaseDate ?? scan.ExtractedDate ?? DateTime.UtcNow.Date;

        // Single DB transaction so the asset row + journal entry stay
        // mutually-consistent. FixedAssetService.CreateAsync uses the
        // shared DbContext, so its SaveChanges happens INSIDE this txn.
        var strategy = _db.Database.CreateExecutionStrategy();
        Models.DTOs.FixedAsset.FixedAssetResponse? created = null;
        Guid? journalEntryId = null;
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                created = await assetService.CreateAsync(companyId,
                    new Models.DTOs.FixedAsset.CreateFixedAssetRequest(
                        AssetCode: req.AssetCode,
                        Name: req.Name,
                        Description: req.Description ?? line?.Description,
                        Category: req.Category,
                        Location: null,
                        SerialNumber: req.SerialNumber,
                        PurchaseDate: purchaseDate,
                        PurchaseCost: purchaseCost,
                        SalvageValue: req.SalvageValue,
                        UsefulLifeMonths: req.UsefulLifeMonths,
                        DepreciationMethod: req.DepreciationMethod,
                        AssetAccountId: req.AssetAccountId,
                        DepreciationExpenseAccountId: req.DepreciationExpenseAccountId,
                        AccumulatedDepreciationAccountId: req.AccumulatedDepreciationAccountId),
                    createdBy);

                // Initial capitalization journal entry — Dr: Asset / Cr: AP-or-Cash.
                // We only post when both account IDs are known (asset
                // accounts are required for the FixedAsset to depreciate
                // correctly anyway, so the user must supply them).
                if (req.AssetAccountId.HasValue)
                {
                    // Find a default credit account: scan's suggested CreditAccount,
                    // else the first Liability/Equity account configured as "AP"/"Cash"
                    Guid? creditAccountId = null;
                    if (!string.IsNullOrEmpty(scan.SuggestedAccountsJson))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(scan.SuggestedAccountsJson);
                            if (doc.RootElement.TryGetProperty("CreditAccountCode", out var cc) && cc.GetString() is string code)
                            {
                                creditAccountId = await _db.ChartOfAccounts.AsNoTracking()
                                    .Where(a => a.CompanyId == companyId && a.AccountCode == code && !a.IsDeleted)
                                    .Select(a => (Guid?)a.Id).FirstOrDefaultAsync();
                            }
                        }
                        catch { }
                    }
                    if (!creditAccountId.HasValue)
                    {
                        // Fallback: first AP-style account (code starts with 21)
                        creditAccountId = await _db.ChartOfAccounts.AsNoTracking()
                            .Where(a => a.CompanyId == companyId && !a.IsDeleted
                                && a.AccountType == AccountType.Liability
                                && a.AccountCode.StartsWith("21"))
                            .OrderBy(a => a.AccountCode)
                            .Select(a => (Guid?)a.Id).FirstOrDefaultAsync();
                    }

                    if (creditAccountId.HasValue)
                    {
                        var entryNumber = $"JV-AST-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString()[..4].ToUpper()}";
                        var je = new JournalEntry
                        {
                            CompanyId = companyId,
                            EntryNumber = entryNumber,
                            EntryDate = purchaseDate,
                            JournalType = JournalType.General,
                            Description = $"ลงทะเบียนสินทรัพย์ {req.AssetCode} {req.Name} (OCR scan {scanResultId})",
                            Status = JournalEntryStatus.Posted,
                            IsAutoGenerated = true,
                            TotalDebit = purchaseCost,
                            TotalCredit = purchaseCost,
                            CreatedBy = createdBy,
                        };
                        je.Lines.Add(new JournalEntryLine
                        {
                            AccountId = req.AssetAccountId.Value,
                            DebitAmount = purchaseCost,
                            CreditAmount = 0,
                            Description = $"ลงทะเบียนสินทรัพย์ {req.Name}",
                        });
                        je.Lines.Add(new JournalEntryLine
                        {
                            AccountId = creditAccountId.Value,
                            DebitAmount = 0,
                            CreditAmount = purchaseCost,
                            Description = $"เจ้าหนี้ค่าสินทรัพย์ {req.Name}",
                        });
                        // Mandatory double-entry balance check — same
                        // pattern as the depreciation entry in
                        // FixedAssetService.CalculateDepreciationAsync.
                        if (Math.Abs(je.TotalDebit - je.TotalCredit) > 0.01m)
                            throw new InvalidOperationException(
                                $"Journal ไม่สมดุล: Dr={je.TotalDebit:N2} Cr={je.TotalCredit:N2}");
                        _db.JournalEntries.Add(je);
                        journalEntryId = je.Id;
                    }
                }

                // Remove this line from the asset-candidates list. When
                // none remain, clear the alert flag entirely.
                RemoveAssetCandidate(scan, req.LineIndex);
                scan.ProcessingNotes = (scan.ProcessingNotes ?? "")
                    + $"\n[FixedAsset] ลงทะเบียน asset {req.AssetCode} '{req.Name}' จาก line {req.LineIndex} (cost {purchaseCost:N2})";

                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        });

        return new
        {
            assetId = created?.Id,
            assetCode = created?.AssetCode,
            journalEntryId,
            purchaseCost,
            remainingCandidates = scan.HasPotentialFixedAsset,
        };
    }

    /// <summary>Drop a registered line from the JSON candidate list so
    /// the UI alert hides itself once every asset has been registered.</summary>
    private static void RemoveAssetCandidate(OcrScanResult scan, int lineIndex)
    {
        if (string.IsNullOrEmpty(scan.PotentialAssetLinesJson)) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(scan.PotentialAssetLinesJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return;
            var remaining = doc.RootElement.EnumerateArray()
                .Where(el => !(el.TryGetProperty("lineIndex", out var idx)
                    && idx.ValueKind == System.Text.Json.JsonValueKind.Number
                    && idx.GetInt32() == lineIndex))
                .Select(el => System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(el.GetRawText()))
                .ToList();
            scan.PotentialAssetLinesJson = remaining.Count > 0
                ? System.Text.Json.JsonSerializer.Serialize(remaining)
                : null;
            scan.HasPotentialFixedAsset = remaining.Count > 0;
        }
        catch { /* malformed JSON — leave as-is */ }
    }

    public async Task DeleteScanAsync(Guid companyId, Guid scanResultId, bool cascadeCreatedDocument = false)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        // Handle the auto-created document case. Two flows:
        //   • cascadeCreatedDocument = false → bail out (old behavior) so the
        //     user can't accidentally orphan or delete a document they
        //     intend to keep.
        //   • cascadeCreatedDocument = true → delete the document too, but
        //     ONLY while it's still Draft. Approved/Paid documents are
        //     financial records and must not be deleted via the OCR UI —
        //     the user has to void/reverse them through the normal docs UI.
        if (result.CreatedDocumentId.HasValue)
        {
            if (!cascadeCreatedDocument)
                throw new InvalidOperationException(
                    "เอกสารถูกสร้างจาก scan นี้แล้ว — ส่ง cascade=true เพื่อลบทั้งคู่ (เฉพาะกรณี Draft)");

            var doc = await _db.Documents
                .Include(d => d.Lines)
                .FirstOrDefaultAsync(d => d.Id == result.CreatedDocumentId.Value
                    && d.CompanyId == companyId && !d.IsDeleted);
            if (doc != null)
            {
                if (doc.Status != Models.Enums.DocumentStatus.Draft
                    && doc.Status != Models.Enums.DocumentStatus.WaitingApproval
                    && doc.Status != Models.Enums.DocumentStatus.Rejected)
                {
                    throw new InvalidOperationException(
                        $"เอกสารที่สร้างจาก scan นี้อยู่ในสถานะ {doc.Status} — ต้อง void/reverse จากหน้าเอกสารแทน");
                }
                // Soft-delete to preserve audit trail (consistent with how
                // documents are deleted elsewhere). Lines cascade via the
                // entity's IsDeleted filter; FK rows like attachments are
                // re-pointed below.
                doc.IsDeleted = true;
                doc.UpdatedAt = DateTime.UtcNow;
                doc.UpdatedBy = "OCR-DeleteCascade";
                foreach (var line in doc.Lines)
                {
                    line.IsDeleted = true;
                    line.UpdatedAt = DateTime.UtcNow;
                }
            }
        }

        if (result.FileAttachmentId.HasValue)
        {
            var file = await _db.FileAttachments.FirstOrDefaultAsync(f => f.Id == result.FileAttachmentId);
            if (file != null)
            {
                try { if (File.Exists(file.StoragePath)) File.Delete(file.StoragePath); } catch { }
                _db.FileAttachments.Remove(file);
            }
        }

        _db.Set<OcrScanResult>().Remove(result);
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

        // Fall back to stored entity data when data parameter is null
        if (suggestedAccounts == null && !string.IsNullOrEmpty(r.SuggestedAccountsJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(r.SuggestedAccountsJson);
                var sa = doc.RootElement;
                suggestedAccounts = new OcrSuggestedAccountsDto(
                    sa.TryGetProperty("DebitAccountCode", out var dac) ? dac.GetString() : null,
                    sa.TryGetProperty("DebitAccountName", out var dan) ? dan.GetString() : null,
                    sa.TryGetProperty("CreditAccountCode", out var cac) ? cac.GetString() : null,
                    sa.TryGetProperty("CreditAccountName", out var can) ? can.GetString() : null,
                    sa.TryGetProperty("VatAccountCode", out var vac) ? vac.GetString() : null,
                    sa.TryGetProperty("VatAccountName", out var van) ? van.GetString() : null);
            }
            catch { }
        }

        if (items == null && !string.IsNullOrEmpty(r.ExtractedItemsJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(r.ExtractedItemsJson);
                items = doc.RootElement.EnumerateArray().Select(el => new OcrLineItemDto(
                    el.TryGetProperty("Description", out var d) ? d.GetString() : null,
                    el.TryGetProperty("Quantity", out var q) && q.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)q.GetDouble() : null,
                    el.TryGetProperty("UnitPrice", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)u.GetDouble() : null,
                    el.TryGetProperty("Amount", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number ? (decimal)a.GetDouble() : null,
                    el.TryGetProperty("SuggestedAccountCode", out var s) ? s.GetString() : null
                )).ToList();
            }
            catch { }
        }

        var expenseCategory = data?.ExpenseCategory ?? r.ExpenseCategory;
        var hasWht = data?.HasWht ?? r.HasWht;
        var whtRate = data?.WhtRate ?? r.WhtRate;
        var paymentTermsDays = data?.PaymentTermsDays ?? r.PaymentTermsDays;

        OcrDbdInfo? dbdInfo = null;
        if (data?.DbdLookupAttempted == true)
        {
            dbdInfo = new OcrDbdInfo(
                LookupAttempted: true,
                Matched: data.DbdMatched,
                CanonicalName: data.DbdCanonicalName,
                Address: data.DbdAddress,
                JuristicType: data.DbdJuristicType,
                Status: data.DbdStatus);
        }

        return new OcrResultResponse(
            r.Id, r.OriginalFileName, r.ScanStatus, r.DocumentType, r.Confidence,
            r.ExtractedVendorName, r.ExtractedVendorTaxId, r.ExtractedDocumentNumber,
            r.ExtractedDate, r.ExtractedSubTotal, r.ExtractedVatAmount, r.ExtractedTotalAmount,
            r.MatchedContactId, r.CreatedDocumentId, r.ProcessedAt,
            r.IsDuplicate, r.DuplicateOfScanId, r.FileHash, r.ProcessingNotes,
            expenseCategory, suggestedAccounts,
            hasWht, whtRate,
            paymentTermsDays, items,
            r.RawTextContent,
            data?.FieldConfidence,
            data?.BuyerName,
            data?.BuyerTaxId,
            dbdInfo,
            r.ScannedDocumentType,
            r.OurRole,
            r.TargetDocumentType,
            r.OcrEngine,
            r.HasPotentialFixedAsset,
            r.PotentialAssetLinesJson);
    }
}

internal class OcrExtractedData
{
    /// <summary>The kind of paper that was scanned (e.g. "Receipt", "TaxInvoice").
    /// This stays close to what's printed on the page — feed for downstream
    /// rendering and traceability.</summary>
    public string DocumentType { get; set; } = "Receipt";

    /// <summary>The doc type we should CREATE in our books — different from
    /// DocumentType when the workflow shifts perspective (e.g. a supplier's
    /// receipt → we book a PaymentVoucher). Set by OcrDocumentRoleInferrer
    /// and refined by VendorIntelligenceService's high-confidence override.
    /// </summary>
    public string? TargetDocumentType { get; set; }
    public string? OurRole { get; set; }
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
    public string? ZoneSummary { get; set; }
    public string? BuyerName { get; set; }
    public string? BuyerTaxId { get; set; }
    public Dictionary<string, double> FieldConfidence { get; set; } = new();
    public List<string> ReasoningTrace { get; set; } = new();
    // === DBD enrichment ===
    public bool DbdLookupAttempted { get; set; }
    public bool DbdMatched { get; set; }
    public string? DbdCanonicalName { get; set; }
    public string? DbdAddress { get; set; }
    public string? DbdJuristicType { get; set; }
    public string? DbdStatus { get; set; }
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
