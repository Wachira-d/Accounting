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
    // Pre-compiled vendor-detail regexes used by ParseThaiDocument — hot path,
    // 4-5 invocations per scan, so the compilation cost is worth amortizing.
    private static readonly Regex VendorPhoneRegex = new(
        @"(?:โทร(?:ศัพท์)?\.?|TEL\.?|TELEPHONE|PHONE|มือถือ)\s*[:：]?\s*([0-9][\d\-\s\.()]{7,18}\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VendorEmailRegex = new(
        @"[\w\.\-]+@[\w\.\-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled);
    private static readonly Regex VendorBranchRegex = new(
        @"(?:สาขา(?:ที่)?|BRANCH)\s*(?:เลข(?:ที่)?\s*)?[:：]?\s*(\d{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeadOfficeRegex = new(
        @"สำนักงานใหญ่|HEAD\s*OFFICE",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VendorAddressRegex = new(
        @"(?:ที่อยู่|ADDRESS)\s*[:：]?\s*((?:[^\n]+\n?){1,4}?)(?=\n\s*(?:โทร|TEL|เลขประจำตัว|TAX\s*ID|อีเมล|EMAIL|FAX|$))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
    private readonly Services.Interfaces.IOcrQuotaService _quota;
    private readonly Ocr.AzureDiPatternLearner _azureLearner;
    private readonly Ocr.VendorKnownGoodCorrector _knownGoodCorrector;
    private readonly Ocr.RdComplianceValidator? _rdComplianceValidator;
    private readonly Ocr.GlobalDocWorkflowLearner? _docWorkflowLearner;
    private readonly Services.Ai.IOcrAiAugmenter? _aiAugmenter;
    private readonly Services.Interfaces.IAccountingService? _accounting;

    public OcrService(AccountingDbContext db, IHttpClientFactory httpClientFactory,
        IConfiguration configuration, ILogger<OcrService> logger,
        IDbdLookupService dbdLookup,
        AzureDocumentIntelligenceService azureDi,
        ExpenseCategoryLearner categoryLearner,
        VendorIntelligenceService vendorIntel,
        EmbeddedTesseractOcrService embeddedOcr,
        AssociationRuleMiner ruleMiner,
        TfIdfNaiveBayesClassifier nbClassifier,
        RecurringExpenseDetector recurringDetector,
        Services.Interfaces.IOcrQuotaService quota,
        Ocr.AzureDiPatternLearner azureLearner,
        Ocr.VendorKnownGoodCorrector knownGoodCorrector,
        Ocr.RdComplianceValidator? rdComplianceValidator = null,
        Ocr.GlobalDocWorkflowLearner? docWorkflowLearner = null,
        Services.Ai.IOcrAiAugmenter? aiAugmenter = null,
        Services.Interfaces.IAccountingService? accounting = null)
    {
        _docWorkflowLearner = docWorkflowLearner;
        _aiAugmenter = aiAugmenter;
        _accounting = accounting;
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
        _quota = quota;
        _azureLearner = azureLearner;
        _knownGoodCorrector = knownGoodCorrector;
        _rdComplianceValidator = rdComplianceValidator;
    }

    public async Task<OcrResultResponse> ScanAsync(Guid companyId, Guid fileAttachmentId, string? preferredEngine = null)
    {
        // Normalize the user's engine preference into one of three modes.
        // "auto" = current cascade; "azure" = Tier 1 only (no local fallback
        // when user explicitly wants Azure accuracy); "local" = skip Tier 1
        // entirely. Tier 0 e-Tax XML always runs regardless.
        var enginePref = (preferredEngine ?? "auto").Trim().ToLowerInvariant();
        if (enginePref != "azure" && enginePref != "local") enginePref = "auto";

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
            // Tier-outcome ledger — every cascade step writes ONE line so the
            // debug panel can show the full story regardless of which tier
            // finally produced the result. Without this, an Azure failure
            // got silently overwritten when Python local also failed, and
            // the user had no way to see why Azure was bypassed.
            var tierTrace = new List<string>();
            string? azureOutcome = null;     // "skipped: ...", "failed: ...", "ok"
            // PDF text-layer side channel — extracted lazily once (when we
            // need it as a hybrid signal in Tier 3). We no longer use it
            // as a standalone Tier-0 bypass because (a) it misses table
            // layout and column alignment on receipts so extraction is
            // imperfect, and (b) when Azure DI quota exists it's a strict
            // upgrade. We DO merge it into Tier 3 (Tesseract) so PDF scans
            // that fall through to local get both signals — clean digital
            // characters + spatial OCR — fed into ParseThaiDocument.
            string? pdfTextLayer = null;
            string? pdfTextLayerReason = null;

            // Pre-tier diagnostic: WHY would Azure not even be tried?
            //   • toggle off / endpoint or key missing → azureSkipReason
            //     was set above based on AzureDiEnabled / Endpoint / Key.
            //   • quota exhausted → resolved below.
            // Capture all three branches in azureOutcome so the trace is
            // always populated.

            // ─── Tier 0: e-Tax embedded XML shortcut ───
            // If the upload is a PDF/A-3 that carries an ETDA Cross Industry
            // Invoice XML in its EmbeddedFiles tree, the legally authoritative
            // values live INSIDE the file. Read them directly — no OCR engine
            // needed, no quota consumed, 100% accuracy on every field. This
            // is the single biggest accuracy win available and runs FIRST.
            //
            // Only attempts on PDF content-type. Any failure (no embed,
            // unknown XML schema, broken parse) falls through to Tier 1.
            if (extractedData == null
                && (file.ContentType?.Contains("pdf", StringComparison.OrdinalIgnoreCase) == true
                    || (file.OriginalFileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true)))
            {
                try
                {
                    var pdfBytes = await File.ReadAllBytesAsync(file.StoragePath);
                    var etax = Ocr.EtaxPdfXmlExtractor.TryExtract(pdfBytes);
                    if (etax != null && etax.Found)
                    {
                        extractedData = MapEtaxToOcrData(etax);
                        extractedText = etax.XmlContent ?? "";
                        ocrEngineUsed = "EtaxXml";
                        tierTrace.Add($"Tier 0 — e-Tax XML embedded → ใช้ค่าจาก XML โดยตรง " +
                            $"(เอกสาร {etax.MappedDocumentType} {etax.DocumentNumber}, " +
                            $"{etax.Items.Count} รายการ, ยอดรวม {etax.GrandTotal:N2})");
                        _logger.LogInformation(
                            "OCR Tier 0 (e-Tax XML) hit: ScanId={ScanId} Company={Cid} DocNum={DocNum} Total={Total}",
                            scanResult.Id, companyId, etax.DocumentNumber, etax.GrandTotal);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "e-Tax XML extraction attempt failed — falling through to Tier 1");
                }
            }

            // Tier 1: Azure DI — runs whenever the admin has fully configured
            // it (toggle + endpoint + key), regardless of OcrProvider's value.
            // Per-engine quota gate: even when Azure DI is enabled +
            // configured, the tenant's subscription may have exhausted
            // its Azure budget this month. When that happens, skip Tier 1
            // and route directly to local OCR (when FallbackToLocal is on
            // for this tenant). The legacy single-budget mode is unchanged.
            var (azureQuotaAllowed, quotaSkipReason) = await _quota.CheckAzureQuotaAsync(companyId);
            if (!azureQuotaAllowed)
            {
                // Fold the precise quota reason into the existing skip-reason
                // pipeline so it ends up in ProcessingNotes alongside the
                // toggle/endpoint/key reasons. Distinguishes "plan = 0 pages"
                // from "quota หมด" so the admin knows whether to upgrade
                // plan vs buy credits vs wait for next month.
                azureSkipReason = quotaSkipReason ?? "Azure DI quota หมดสำหรับเดือนนี้";
                _logger.LogInformation("Azure DI skipped for {Cid}: {Reason}", companyId, azureSkipReason);
            }

            // Per-scan user preference: when the caller picked "local",
            // bypass Tier 1 entirely so no Azure cost is incurred — even
            // if Azure is enabled and has quota.
            if (enginePref == "local")
            {
                azureSkipReason = "ผู้ใช้เลือก Local OCR — ข้าม Azure DI ตามคำสั่ง";
            }

            if (extractedData == null && azureEnabled && azureQuotaAllowed && enginePref != "local")
            {
                var azureResult = await ExtractWithAzureDiAsync(companyId, file, siteSettings);
                if (azureResult.Success)
                {
                    extractedText = azureResult.Text;
                    extractedData = azureResult.Data;
                    ocrEngineUsed = "AzureDI";
                    azureOutcome = $"ok (confidence {extractedData.Confidence:P0})";
                    // Engine-specific accounting: record the Azure page usage
                    // so the next CanUseAzureAsync correctly reflects the new total.
                    await _quota.RecordEngineUsageAsync(companyId, "Azure");
                    // Free training signal: feed Azure's authoritative output
                    // into OcrLearnedPattern + VendorKnownGoodValue stores so
                    // future Tier-2/3 fallback scans of this same vendor
                    // benefit from Azure-grade accuracy on names/numbers.
                    try
                    {
                        await _azureLearner.LearnAsync(
                            companyId, extractedText ?? "",
                            vendorTaxId: extractedData.VendorTaxId,
                            vendorName: extractedData.VendorName,
                            documentNumber: extractedData.DocumentNumber,
                            buyerName: extractedData.BuyerName,
                            buyerTaxId: extractedData.BuyerTaxId,
                            vendorAddress: extractedData.VendorAddress,
                            vendorPhone: extractedData.VendorPhone,
                            vendorEmail: extractedData.VendorEmail,
                            vendorBranchCode: extractedData.VendorBranchCode,
                            sourceConfidence: extractedData.Confidence);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Azure-DI pattern learning failed (non-fatal)");
                    }
                }
                else
                {
                    lastError = azureResult.Error ?? "unknown";
                    azureOutcome = $"failed: {lastError}";
                    _logger.LogWarning("Azure DI failed ({Err}) — falling back to next tier", lastError);
                }
            }
            else if (azureSkipReason != null)
            {
                azureOutcome = $"skipped: {azureSkipReason}";
            }
            else if (!azureEnabled)
            {
                azureOutcome = "skipped: AzureDI ไม่ได้เปิดใช้งาน (ตรวจสอบ admin/ocr-config — toggle + endpoint + key)";
            }
            tierTrace.Add($"[Tier 1 Azure DI] {azureOutcome ?? "(not attempted)"}");

            // Tier 2: Python local service (PaddleOCR+EasyOCR).
            // When the user explicitly chose "azure", do NOT silently fall
            // back to local — they wanted Azure-grade accuracy. The scan
            // surfaces failure (Tier 3 still runs as a last-resort below
            // only when azure isn't the explicit choice).
            string? pythonOutcome = null;
            if (extractedData == null && enginePref != "azure")
            {
                try
                {
                    var localResult = await ExtractWithLocalServiceAsync(file);
                    if (!string.IsNullOrEmpty(localResult.RawText))
                    {
                        extractedText = localResult.RawText;
                        extractedData = localResult.Data;
                        ocrEngineUsed = "LocalPython";
                        pythonOutcome = $"ok (confidence {extractedData.Confidence:P0})";
                        await _quota.RecordEngineUsageAsync(companyId, "Local");
                        if (lastError != null)
                            extractedData.ReasoningTrace.Insert(0, $"[Fallback] Azure DI failed: {lastError} — used Python local pipeline");
                        else if (azureSkipReason != null)
                            extractedData.ReasoningTrace.Insert(0, $"[Provider] ข้าม Azure DI — {azureSkipReason}; ใช้ Local (PaddleOCR+EasyOCR)");
                        // Local-cascade output is noisy — substitute known-good
                        // canonical values that Azure DI captured for this
                        // vendor on a previous scan, when fuzzy match passes.
                        await _knownGoodCorrector.ApplyAsync(companyId, extractedData);
                    }
                    else
                    {
                        pythonOutcome = "no text returned";
                    }
                }
                catch (Exception ex)
                {
                    lastError = $"Python local: {ex.Message}";
                    pythonOutcome = $"failed: {ex.Message}";
                    _logger.LogWarning(ex, "Python local OCR failed — falling back to embedded Tesseract");
                }
            }
            else
            {
                pythonOutcome = "not attempted (Tier 1 succeeded)";
            }
            tierTrace.Add($"[Tier 2 Python local] {pythonOutcome}");

            // Tier 3: Embedded Tesseract (always available, in-process) —
            // hybrid with PDF text-layer when the input file is a digital
            // PDF. Strategy: extract the text layer (free, character-
            // perfect digital text) and concatenate it with Tesseract's
            // output (noisy but layout-aware). Feed the merged text into
            // ParseThaiDocument so both signals contribute to extraction.
            // Tesseract-alone still runs for images and PDFs without a
            // text layer.
            //
            // Skipped when user explicitly chose "azure" — they wanted
            // Azure-grade accuracy, not noisy Tesseract output. Failure
            // surfaces below as scan-status Failed so they know to retry.
            if (extractedData == null && enginePref != "azure")
            {
                var embeddedResult = await ExtractWithEmbeddedTesseractAsync(file);

                // Try to fold in a PDF text-layer side-channel for digital
                // PDFs. Cheap (in-memory) so we do it once here even if
                // the Tier-0 short-circuit is gone.
                var isPdf = file.ContentType?.Contains("pdf", StringComparison.OrdinalIgnoreCase) == true
                            || (file.OriginalFileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ?? false);
                if (isPdf && pdfTextLayer == null)
                {
                    try
                    {
                        var pdfBytes = await File.ReadAllBytesAsync(file.StoragePath);
                        var maxPages = siteSettings?.OcrMaxPagesPerScan ?? 10;
                        var pdfResult = Ocr.PdfTextLayerExtractor.TryExtract(pdfBytes, maxPages);
                        if (pdfResult.HasUsableText)
                        {
                            pdfTextLayer = pdfResult.Text;
                            pdfTextLayerReason = $"{pdfResult.Reason} ({pdfResult.CharsExtracted} chars)";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "PdfTextLayer extraction failed during Tier-3 merge");
                    }
                }

                if (!string.IsNullOrEmpty(pdfTextLayer))
                {
                    // Combine both signals before parsing. Order: text-layer
                    // first (so ParseThaiDocument sees the clean characters)
                    // then Tesseract output (so any field the text-layer
                    // missed has a second chance from the OCR).
                    var combined = pdfTextLayer + "\n\n──── TESSERACT ────\n\n" + (embeddedResult.RawText ?? "");
                    var normalized = Ocr.ThaiTextNormalizer.Normalize(combined);
                    extractedData = ParseThaiDocument(normalized);
                    extractedText = combined;
                    // Hybrid is more reliable than Tesseract alone but less
                    // than character-perfect text-layer-only — 0.90 splits
                    // the difference. Gateway can still knock it down.
                    extractedData.Confidence = Math.Max(extractedData.Confidence,
                        Math.Max(0.90m, embeddedResult.Data.Confidence));
                    Ocr.SmartFieldExtractor.Enrich(extractedData, combined);
                    ocrEngineUsed = "PdfTextLayer+Tesseract";
                    extractedData.ReasoningTrace.Insert(0,
                        $"[Hybrid] รวม PDF text-layer ({pdfTextLayerReason}) + Tesseract OCR เข้าด้วยกัน");
                }
                else
                {
                    // Image input, or PDF with no extractable text layer
                    // (image-only scans). Tesseract-alone path.
                    extractedText = embeddedResult.RawText;
                    extractedData = embeddedResult.Data;
                    ocrEngineUsed = "EmbeddedTesseract";
                }

                await _quota.RecordEngineUsageAsync(companyId, "Local");
                if (lastError != null)
                    extractedData.ReasoningTrace.Insert(0, $"[Fallback] tiers above failed ({lastError}) — used local engine");
                // Tesseract output is the noisiest of the engines — run
                // the known-good corrector here so any prior Azure-DI scan
                // of this vendor cleans up the obvious garbling before it
                // reaches the gateway / book.
                await _knownGoodCorrector.ApplyAsync(companyId, extractedData);
                // Always make the Azure-skip reason visible even when local ran
                // cleanly. Surface to ProcessingNotes too (ReasoningTrace is in-
                // memory only). Common case: admin tested the connection but
                // forgot to flip the AzureDiEnabled toggle.
                if (azureSkipReason != null)
                {
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + $"\n[Azure DI ข้าม] {azureSkipReason}";
                }
            }
            tierTrace.Add($"[Tier 3 Local hybrid] {(ocrEngineUsed == "PdfTextLayer+Tesseract" ? "PDF text-layer + Tesseract" : ocrEngineUsed == "EmbeddedTesseract" ? "Tesseract only" : enginePref == "azure" ? "skipped (user เลือก Azure-only)" : "not attempted")}");

            // Azure-only path that failed: no engine produced data. Surface
            // an empty result with a Failed status so the user knows to
            // either retry or switch engines. We refund quota in the
            // controller via the OcrEngine == null / status != Completed gate.
            if (extractedData == null)
            {
                extractedData = new OcrExtractedData { Confidence = 0m };
                ocrEngineUsed = ocrEngineUsed ?? "None";
                extractedData.ReasoningTrace.Add(
                    enginePref == "azure"
                        ? $"[Azure-only] ผู้ใช้เลือก Azure DI แต่สแกนไม่สำเร็จ — {lastError ?? azureSkipReason ?? "ไม่ทราบสาเหตุ"}. กรุณาลองอีกครั้งหรือเลือก Engine อื่น."
                        : $"[ทุก Tier ล้มเหลว] {lastError ?? "ไม่มี engine ที่ทำงานได้"}");
                scanResult.ScanStatus = "Failed";
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n" + extractedData.ReasoningTrace[^1];
            }

            // Prepend the tier-outcome ledger to the reasoning trace so the
            // debug panel shows EVERY tier's status — no more silent
            // "Azure was bypassed" mystery when both Azure and Python fail.
            for (int i = tierTrace.Count - 1; i >= 0; i--)
                extractedData.ReasoningTrace.Insert(0, tierTrace[i]);

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
                // AwayFromZero matches the convention used throughout the rest
                // of the system (PayrollService, TaxService, JE balance check).
                // .NET's default Math.Round uses banker's rounding which would
                // shift the WHT amount by 1 satang on half-baht boundaries and
                // break ภงด.3 cross-tick reconciliation.
                whtAmt = Math.Round(extractedData.SubTotal.Value * whtRatePct.Value / 100m, 2, MidpointRounding.AwayFromZero);

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
            // Per-company preference for Buyer + TaxInvoice flow target.
            // Defaults to PaymentVoucher (cash-basis, most Thai SMEs);
            // accrual-basis companies set it to PurchaseInvoice in
            // CompanySettings.
            var buyerInvoiceTarget = await _db.Set<CompanySettings>().AsNoTracking()
                .Where(c => c.CompanyId == companyId && !c.IsDeleted)
                .Select(c => (DocumentType?)c.OcrBuyerInvoiceDefaultTarget)
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
                    previousScannedType: prevScanned,
                    buyerInvoiceDefaultTarget: buyerInvoiceTarget);
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
            // Persist buyer fields so subsequent loads (list endpoint,
            // page reload) still have them. Without this, MapToResponse
            // would re-read the entity, see NULL on Buyer*, and the
            // RD-compliance UI would warn "ควรระบุเลขผู้เสียภาษีของผู้ซื้อ"
            // every time the user opened the scan even though OCR did
            // pick the value up correctly on the initial run.
            scanResult.BuyerName = extractedData.BuyerName;
            scanResult.BuyerTaxId = extractedData.BuyerTaxId;
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
                Ocr.ExpenseCategoryResolver.ApplyTo(extractedData, categoryResult, categoryResolverText);

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
                // Federated fallback — when per-tenant VendorIntel has no
                // confident prediction (new vendor for this tenant, but a
                // vendor the SaaS as a whole has seen before), consult the
                // cross-tenant pool. Only fires when:
                //   1. We have NO TargetDocumentType yet, AND
                //   2. VendorIntel's confidence stayed below MediumConfidence,
                //   3. And the scanned-paper type was identifiable.
                // The federated pattern needs k=3 distinct contributing
                // tenants to be "active" — under the floor, this is silent.
                if (_docWorkflowLearner != null
                    && string.IsNullOrEmpty(extractedData.TargetDocumentType)
                    && (vendorPred.DocumentTypeConfidence < VendorIntelligenceService.MediumConfidence)
                    && !string.IsNullOrEmpty(extractedData.DocumentType))
                {
                    try
                    {
                        var fedVKey = !string.IsNullOrEmpty(extractedData.VendorTaxId)
                            ? $"tax:{new string(extractedData.VendorTaxId.Where(char.IsDigit).ToArray())}"
                            : !string.IsNullOrEmpty(extractedData.VendorName)
                                ? $"name:{extractedData.VendorName.Trim().ToLowerInvariant()}"
                                : null;
                        if (!string.IsNullOrEmpty(fedVKey))
                        {
                            var (fedTarget, fedTenants) = await _docWorkflowLearner.PredictAsync(fedVKey, extractedData.DocumentType);
                            if (!string.IsNullOrEmpty(fedTarget))
                            {
                                extractedData.TargetDocumentType = fedTarget;
                                extractedData.ReasoningTrace.Add(
                                    $"[Federated] 🌐 ระบบกลางแนะนำสร้าง {fedTarget} (จาก {fedTenants} ลูกค้าที่ใช้ vendor เดียวกัน)");
                            }
                        }
                    }
                    catch { /* federation outage — silent */ }
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

            // ── Credit-account auto-fill by target document type ──
            // The Category resolver fills the DEBIT side (expense
            // account). Credit depends on the bookkeeping flow:
            //   PaymentVoucher / Expense / ReceiptVoucher → cash (1110)
            //   PurchaseInvoice                           → A/P (2100)
            //   Invoice / TaxInvoice (Seller)             → A/R (1130)
            // We pick the first matching account in the company's CoA
            // by code prefix — accommodates Thai SMEs that use 1110,
            // 1111, 11110, etc. Falls through silently if none exists.
            if (string.IsNullOrEmpty(extractedData.CreditAccountCode)
                && !string.IsNullOrEmpty(extractedData.TargetDocumentType))
            {
                string[] creditPrefixes = extractedData.TargetDocumentType switch
                {
                    "PaymentVoucher" or "Expense" => new[] { "1111", "1110", "111" },        // Cash / Bank
                    "ReceiptVoucher"             => new[] { "1111", "1110", "111" },        // Cash received
                    "PurchaseInvoice"            => new[] { "2110", "2100", "211" },        // A/P
                    "Invoice" or "TaxInvoice"    => new[] { "1130", "1131", "113" },        // A/R
                    _ => Array.Empty<string>()
                };
                foreach (var pfx in creditPrefixes)
                {
                    var creditAcct = await _db.ChartOfAccounts
                        .Where(a => a.CompanyId == companyId && a.AccountCode.StartsWith(pfx)
                                && a.IsActive && !a.IsDeleted)
                        .OrderBy(a => a.AccountCode)
                        .Select(a => new { a.AccountCode, a.AccountName })
                        .FirstOrDefaultAsync();
                    if (creditAcct != null)
                    {
                        extractedData.CreditAccountCode = creditAcct.AccountCode;
                        extractedData.CreditAccountName = creditAcct.AccountName;
                        extractedData.ReasoningTrace.Add(
                            $"[Credit] เลือกบัญชีเครดิต {creditAcct.AccountCode} จากประเภทเอกสาร {extractedData.TargetDocumentType}");
                        break;
                    }
                }
            }

            // Re-sync ExpenseCategory after the resolver / vendor-intel /
            // basket-rule miners have all written to extractedData. The
            // earlier sync at line ~477 ran BEFORE the resolver, leaving
            // the scan row showing "Expense Category: null" even when
            // the resolver matched "ค่าน้ำประปา" with score 8.0.
            if (!string.IsNullOrEmpty(extractedData.ExpenseCategory))
                scanResult.ExpenseCategory = extractedData.ExpenseCategory;

            // Match GL accounts from suggestions against company's chart of accounts
            if (!string.IsNullOrEmpty(extractedData.DebitAccountCode))
            {
                // Resolve the suggested code against the company's actual
                // Chart of Accounts. Strategy:
                //   1. Exact match → use it.
                //   2. Prefix match → tenant uses a sub-coded version
                //      of the standard account (e.g. 54430 instead of
                //      5306 — common when CoA was extended for branch /
                //      cost-center detail). Prefer the active account
                //      with the longest matching prefix.
                //   3. Keyword match on account name → handles custom
                //      CoA where the prefix doesn't follow standard
                //      Thai SME ranges (e.g. tenant has no 5306-style
                //      code but their "ค่าซ่อมรถยนต์" account is 6101).
                // Without this fallback, the resolver suggests a generic
                // 5306 that doesn't exist in the company's CoA and the
                // UI dropdown can't auto-select anything.
                var seeded = extractedData.DebitAccountCode;
                var debitAccount = await _db.ChartOfAccounts
                    .FirstOrDefaultAsync(a => a.CompanyId == companyId && a.AccountCode == seeded && !a.IsDeleted);
                if (debitAccount == null)
                {
                    // Prefix match — longest first so 53061 beats 5306x
                    debitAccount = await _db.ChartOfAccounts
                        .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                            && a.AccountCode.StartsWith(seeded))
                        .OrderByDescending(a => a.AccountCode.Length)
                        .ThenBy(a => a.AccountCode)
                        .FirstOrDefaultAsync();
                }
                if (debitAccount == null && !string.IsNullOrEmpty(extractedData.DebitAccountName))
                {
                    // Keyword match on account name — pull the most
                    // distinctive Thai keyword from the suggested name
                    // (skip stopword-class words like "ค่า") and look
                    // for an account whose name contains it.
                    var name = extractedData.DebitAccountName;
                    var keyword = ExtractDistinctiveKeyword(name);
                    if (!string.IsNullOrEmpty(keyword))
                    {
                        debitAccount = await _db.ChartOfAccounts
                            .Where(a => a.CompanyId == companyId && a.IsActive && !a.IsDeleted
                                && a.AccountName.Contains(keyword))
                            .OrderBy(a => a.AccountCode)
                            .FirstOrDefaultAsync();
                    }
                }
                if (debitAccount != null)
                {
                    if (debitAccount.AccountCode != seeded)
                    {
                        extractedData.ReasoningTrace.Add(
                            $"[CoA] {seeded} ไม่มีใน CoA — เลือก {debitAccount.AccountCode} {debitAccount.AccountName} แทน");
                    }
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

            // Serialise the suggestion AFTER all CoA resolution above so the
            // UI receives the company's real account codes — not the generic
            // 5306-style codes the category resolver seeds. Serialising
            // earlier (before the exact→prefix→keyword pass) was the reason
            // "AI แนะนำ 5306" still showed even though 54430 exists in the CoA.
            if (extractedData.DebitAccountCode != null || extractedData.CreditAccountCode != null)
            {
                scanResult.SuggestedAccountsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    extractedData.DebitAccountCode, extractedData.DebitAccountName,
                    extractedData.CreditAccountCode, extractedData.CreditAccountName,
                    extractedData.VatAccountCode, extractedData.VatAccountName,
                });
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

            // ───── AI Augmentation: vendor canonicalisation ─────
            // Runs only when local matchers (TaxId + substring + fuzzy
            // ≥0.85) ALL missed. The augmenter sees the local pick
            // (none, in this branch) + a fresh candidate list and may
            // produce a match that fuzzy edit-distance couldn't (Thai
            // ↔ English company names, abbreviation expansion, missing
            // legal suffix, OCR-introduced character noise).
            //
            // ALL failure paths fall through cleanly — the local
            // auto-create logic below still runs unchanged. AI down /
            // disabled / over-budget = behave like AI wasn't there.
            if (!scanResult.MatchedContactId.HasValue && _aiAugmenter != null
                && (!string.IsNullOrEmpty(extractedData.VendorName)
                    || !string.IsNullOrEmpty(extractedData.VendorTaxId)))
            {
                try
                {
                    using var aiCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var aiResult = await _aiAugmenter.CanonicaliseVendorAsync(
                        companyId, scanResult.Id,
                        extractedData.VendorName, extractedData.VendorTaxId,
                        extractedData.VendorAddress,
                        localBestContactId: null, localConfidence: 0m,
                        aiCts.Token);
                    if (aiResult.UsedAi
                        && !string.IsNullOrEmpty(aiResult.Answer) && aiResult.Answer != "__NEW__"
                        && (aiResult.Confidence ?? 0m) >= 0.70m
                        && Guid.TryParse(aiResult.Answer, out var aiContactId))
                    {
                        // Verify the contact still exists + belongs to
                        // the tenant — AI could hallucinate a GUID.
                        var verified = await _db.Contacts.AsNoTracking()
                            .AnyAsync(c => c.Id == aiContactId && c.CompanyId == companyId && !c.IsDeleted);
                        if (verified)
                        {
                            scanResult.MatchedContactId = aiContactId;
                            scanResult.AiSuggestedContactId = aiContactId;
                            scanResult.AiSuggestionFeedbackId = aiResult.FeedbackId;
                            extractedData.ReasoningTrace.Add(
                                $"[AI] vendor canon → contact {aiContactId} ({aiResult.Confidence:P0})"
                                + (string.IsNullOrEmpty(aiResult.Reasoning) ? "" : ": " + aiResult.Reasoning));
                            foreach (var risk in aiResult.Risks)
                                extractedData.ReasoningTrace.Add("[AI risk] " + risk);
                        }
                    }
                    else if (!aiResult.UsedAi && aiResult.FeedbackId.HasValue)
                    {
                        // AI was attempted but unavailable — store the
                        // feedback row id anyway so the UI shows "AI
                        // tried, fell back to local" badge.
                        scanResult.AiSuggestionFeedbackId = aiResult.FeedbackId;
                    }
                }
                catch (Exception aiEx)
                {
                    // Belt-and-braces — augmenter catches internally,
                    // but if anything else throws (DI / context-disposed
                    // race) we still continue the OCR pipeline.
                    _logger.LogWarning(aiEx, "AI vendor canon hook failed; continuing without AI suggestion");
                }
            }

            // ═══════ CONTACT AUTO-CREATION — DBD preferred, OCR fallback ═══════
            // Decision tree when no existing contact matched:
            //   1. DBD verified  → create with DBD canonical name + address (best case)
            //   2. DBD failed BUT TaxId checksum-valid + ≥1 supporting field
            //      (vendor name OR address OR phone) → create from OCR data,
            //      tag as Unverified so user knows to confirm. Better than
            //      "Manual Review Required" — saves the user from re-typing.
            //   3. No TaxId at all but strong vendor name + address → create
            //      with TaxId=null, ContactType=JuristicPerson (best guess).
            //   4. Nothing actionable → leave Contact null + surface note.
            // contactJustCreated short-circuits the enrichment block below
            // (the freshly-created contact already has every field we'd fill).
            bool contactJustCreated = false;
            if (!scanResult.MatchedContactId.HasValue
                && extractedData.DbdMatched
                && !string.IsNullOrEmpty(extractedData.VendorTaxId))
            {
                // ───── Branch 1: DBD verified ─────
                var addressParts = ParseAddressIntoParts(extractedData.DbdAddress);

                var newContact = new Contact
                {
                    CompanyId = companyId,
                    Name = extractedData.DbdCanonicalName ?? extractedData.VendorName ?? $"ผู้ขาย (TaxID: {extractedData.VendorTaxId})",
                    TaxId = extractedData.VendorTaxId,
                    BranchCode = extractedData.VendorBranchCode,
                    IsCustomer = false,
                    IsSupplier = true,
                    ContactType = ContactType.JuristicPerson,
                    Address = extractedData.DbdAddress,
                    Phone = extractedData.VendorPhone,
                    Email = extractedData.VendorEmail,
                    BuildingNumber = addressParts.BuildingNumber,
                    Moo = addressParts.Moo,
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
                contactJustCreated = true;
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + $"\n[Auto-Create] สร้าง Contact ใหม่จากข้อมูล DBD: {newContact.Name} (TaxID {newContact.TaxId})";
                _logger.LogInformation("Auto-created DBD-verified contact for company {CompanyId} TaxId={TaxId} Name={Name}",
                    companyId, newContact.TaxId, newContact.Name);
            }
            else if (!scanResult.MatchedContactId.HasValue)
            {
                // ───── Branches 2-4: DBD unavailable / failed ─────
                var hasValidTaxId = !string.IsNullOrEmpty(extractedData.VendorTaxId)
                    && Ocr.SmartFieldExtractor.IsValidThaiTaxId(extractedData.VendorTaxId);
                var hasVendorName = !string.IsNullOrWhiteSpace(extractedData.VendorName)
                    && extractedData.VendorName.Trim().Length >= 3;
                var hasAddress = !string.IsNullOrWhiteSpace(extractedData.VendorAddress);
                var hasPhone = !string.IsNullOrWhiteSpace(extractedData.VendorPhone);
                // Quality gate — we want at least ONE supporting field beyond
                // the TaxId, OR (when no TaxId) a strong name+address combo.
                // This keeps a misread "12345678901" with no other data from
                // spawning a junk contact, while still helping the user when
                // OCR has captured meaningful detail.
                var canCreateFromOcr =
                    (hasValidTaxId && (hasVendorName || hasAddress || hasPhone)) ||
                    (hasVendorName && hasAddress);

                if (canCreateFromOcr)
                {
                    var addressParts = ParseAddressIntoParts(extractedData.VendorAddress);
                    var fallbackName = hasVendorName
                        ? extractedData.VendorName!.Trim()
                        : (hasValidTaxId
                            ? $"ผู้ขาย (TaxID: {extractedData.VendorTaxId})"
                            : "ผู้ขายจาก OCR (ยังไม่ได้ยืนยัน)");

                    var newContact = new Contact
                    {
                        CompanyId = companyId,
                        Name = fallbackName,
                        TaxId = hasValidTaxId ? extractedData.VendorTaxId : null,
                        BranchCode = extractedData.VendorBranchCode,
                        IsCustomer = false,
                        IsSupplier = true,
                        // ContactType heuristic: 13-digit TaxId starting with 0 = juristic;
                        // starting with 1-8 = individual NID. When unsure default to
                        // JuristicPerson — most OCR'd receipts are from companies, and
                        // the user can flip the type if it turns out to be a person.
                        ContactType = hasValidTaxId
                            ? (extractedData.VendorTaxId!.StartsWith("0") ? ContactType.JuristicPerson : ContactType.Individual)
                            : ContactType.JuristicPerson,
                        Address = extractedData.VendorAddress,
                        Phone = extractedData.VendorPhone,
                        Email = extractedData.VendorEmail,
                        BuildingNumber = addressParts.BuildingNumber,
                        Moo = addressParts.Moo,
                        StreetName = addressParts.StreetName,
                        SubDistrict = addressParts.SubDistrict,
                        District = addressParts.District,
                        Province = addressParts.Province,
                        PostalCode = addressParts.PostalCode,
                        CountryCode = "TH",
                        // Suffix tags the source so admins can later filter "needs
                        // verification" contacts and follow up. Keep it short —
                        // CreatedBy is shown in the audit log column.
                        CreatedBy = "OCR-FallbackAutoCreate"
                    };
                    _db.Contacts.Add(newContact);
                    await _db.SaveChangesAsync();
                    scanResult.MatchedContactId = newContact.Id;
                    contactJustCreated = true;
                    var verifyHint = extractedData.DbdLookupAttempted
                        ? "DBD ยืนยันไม่ได้"
                        : "ไม่ได้ตรวจกับ DBD";
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + $"\n[Auto-Create (Unverified)] สร้าง Contact จาก OCR: {newContact.Name}"
                        + (newContact.TaxId != null ? $" (TaxID {newContact.TaxId})" : "")
                        + $" — {verifyHint}, กรุณาตรวจสอบในภายหลัง";
                    _logger.LogInformation(
                        "Auto-created OCR-only contact (DBD failed) for company {CompanyId} TaxId={TaxId} Name={Name}",
                        companyId, newContact.TaxId, newContact.Name);
                }
                else if (!string.IsNullOrEmpty(extractedData.VendorTaxId))
                {
                    // Have a TaxId but nothing else trustworthy — keep the
                    // original "Manual Review Required" note so user can
                    // resolve in the UI.
                    scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                        + $"\n[Manual Review Required] ไม่สามารถยืนยัน TaxID {extractedData.VendorTaxId} จาก DBD/RD และข้อมูลผู้ขายไม่เพียงพอ — กรุณาตรวจสอบและสร้าง Contact ด้วยตนเอง";
                }
            }
            if (!contactJustCreated
                && scanResult.MatchedContactId.HasValue
                && (extractedData.DbdCanonicalName != null
                    || !string.IsNullOrWhiteSpace(extractedData.VendorPhone)
                    || !string.IsNullOrWhiteSpace(extractedData.VendorAddress)))
            {
                // Existing contact: enrich missing fields from DBD + OCR without
                // overwriting user-entered data. We only fill blanks, never
                // replace — the user's manual edits are always authoritative.
                var existing = await _db.Contacts.FirstOrDefaultAsync(c => c.Id == scanResult.MatchedContactId);
                if (existing != null)
                {
                    bool changed = false;
                    if (string.IsNullOrWhiteSpace(existing.TaxId) && !string.IsNullOrEmpty(extractedData.VendorTaxId))
                    { existing.TaxId = extractedData.VendorTaxId; changed = true; }
                    if (string.IsNullOrWhiteSpace(existing.Address))
                    {
                        var addr = extractedData.DbdAddress ?? extractedData.VendorAddress;
                        if (!string.IsNullOrEmpty(addr)) { existing.Address = addr; changed = true; }
                    }
                    if (string.IsNullOrWhiteSpace(existing.Phone) && !string.IsNullOrWhiteSpace(extractedData.VendorPhone))
                    { existing.Phone = extractedData.VendorPhone; changed = true; }
                    if (string.IsNullOrWhiteSpace(existing.Email) && !string.IsNullOrWhiteSpace(extractedData.VendorEmail))
                    { existing.Email = extractedData.VendorEmail; changed = true; }
                    if (string.IsNullOrWhiteSpace(existing.BranchCode) && !string.IsNullOrWhiteSpace(extractedData.VendorBranchCode))
                    { existing.BranchCode = extractedData.VendorBranchCode; changed = true; }
                    if (changed)
                    {
                        existing.UpdatedBy = extractedData.DbdMatched ? "OCR-DbdEnrich" : "OCR-Enrich";
                        scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + $"\n[Enrich] อัปเดตข้อมูล Contact ที่ว่างของ {existing.Name}";
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
            // Handwriting detection: if Azure's styleFont reported
            // hand-written spans on this doc, capture the flag onto the
            // scan record and suppress auto-create so the user verifies
            // amounts manually. Handwritten amounts on a printed form
            // are a common source of OCR errors AND fraud.
            if (extractedData.FieldConfidence.TryGetValue("Handwriting", out var handConf))
            {
                scanResult.HasHandwriting = true;
                scanResult.HandwritingConfidence = (decimal)handConf;
                autoCreateThreshold = decimal.MaxValue;
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + $"\n[Handwriting] ✋ ตรวจพบลายมือ (conf {handConf:P0}) — auto-create suppressed, โปรดตรวจสอบยอดเงิน";
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

            // Critical-fields hard gate: even if Confidence and contact match
            // both look good, refuse to auto-create when the OCR couldn't
            // extract a usable date, document number, or total. These three
            // are the bookkeeping minimum — without them the created
            // document is just noise the user has to delete + redo.
            var hasUsableTotal = (extractedData.TotalAmount ?? 0) > 0m;
            var hasUsableDate = extractedData.DocumentDate.HasValue;
            var hasUsableDocNumber = !string.IsNullOrWhiteSpace(extractedData.DocumentNumber);
            var criticalFieldsOk = hasUsableTotal && hasUsableDate && hasUsableDocNumber;

            if (scanResult.Confidence >= autoCreateThreshold
                && scanResult.MatchedContactId.HasValue
                && !scanResult.IsDuplicate
                && !scanResult.CreatedDocumentId.HasValue
                && criticalFieldsOk)
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
            else if (scanResult.Confidence >= autoCreateThreshold
                  && scanResult.MatchedContactId.HasValue
                  && !criticalFieldsOk)
            {
                // Surface why we declined to auto-create — admin opens the
                // scan and sees exactly which fields the OCR missed.
                var missing = new List<string>();
                if (!hasUsableTotal) missing.Add("ยอดรวม");
                if (!hasUsableDate) missing.Add("วันที่");
                if (!hasUsableDocNumber) missing.Add("เลขที่เอกสาร");
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "")
                    + $"\n[Auto-create ระงับ] ข้อมูลสำคัญยังขาด: {string.Join(", ", missing)} — กรุณากรอกใน \"ตรวจสอบ & สอนระบบ\" ก่อนสร้างเอกสาร";
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
    /// Delegates to ThaiAddressParser — the canonical parser handles หมู่ที่ +
    /// Thai numerals + Bangkok aliases + multi-prefix variants which the inline
    /// regex here used to miss.
    /// </summary>
    private static (string? BuildingNumber, string? StreetName, string? SubDistrict, string? District, string? Province, string? PostalCode, string? Moo)
        ParseAddressIntoParts(string? address)
    {
        var p = ThaiAddressParser.Parse(address);
        return (p.BuildingNumber, p.StreetName, p.SubDistrict, p.District, p.Province, p.PostalCode, p.Moo);
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

    /// <summary>
    /// Pull the most distinctive keyword out of a suggested account name so
    /// it can be matched against a company's custom Chart of Accounts.
    /// Thai account names run words together with no spaces, so we:
    ///   1. drop a leading account code if the caller passed "5306 ค่า…",
    ///   2. cut at the first conjunction ("และ", "หรือ", "/") so
    ///      "ค่าซ่อมแซมและบำรุงรักษา" yields "ซ่อมแซม" — a shorter stem
    ///      matches more CoA variants than the full compound,
    ///   3. strip shared classifier prefixes ("ค่า", "บัญชี") that nearly
    ///      every expense account carries and so add no signal,
    ///   4. fall back to the longest space-separated token for names that
    ///      do use spaces (English or mixed).
    /// Returns null when nothing distinctive (≥3 chars) is left.
    /// </summary>
    private static string? ExtractDistinctiveKeyword(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var work = name.Trim();

        // Drop a leading account code passed as "5306 ค่า…".
        work = Regex.Replace(work, @"^\s*\d[\d\-\.]*\s+", "");

        // Cut at the first conjunction / separator — keep the leading concept.
        foreach (var sep in new[] { "และ", "หรือ", "/", ",", "&" })
        {
            var idx = work.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 2) { work = work[..idx]; break; }
        }

        // Strip shared classifier prefixes that carry no signal.
        foreach (var prefix in new[] { "ค่าใช้จ่าย", "ค่า", "บัญชี" })
        {
            if (work.StartsWith(prefix, StringComparison.Ordinal) && work.Length > prefix.Length + 2)
            {
                work = work[prefix.Length..];
                break;
            }
        }

        work = work.Trim();

        // For space-separated names, take the longest meaningful token.
        if (work.Contains(' '))
        {
            var token = work
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length >= 3)
                .OrderByDescending(t => t.Length)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(token)) work = token;
        }

        return work.Length >= 3 ? work : null;
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
        // Surface preprocessing trace alongside Azure's own warnings so the
        // debug panel shows the full processing chain. Insert first so it
        // appears at the top of the trace.
        if (azureResult != null && prep.StepsApplied.Count > 0)
            azureResult.Warnings.Insert(0, $"[ImagePrep] {string.Join(", ", prep.StepsApplied)}");
        if (azureResult == null)
            return new AzureExtractionResult(false, "", new OcrExtractedData(), "Azure DI not enabled or not configured");
        if (!azureResult.Success)
            return new AzureExtractionResult(false, "", new OcrExtractedData(), azureResult.ErrorMessage);

        var data = await MapAzureDiToExtractedDataAsync(companyId, azureResult);
        // Apply universal constraints — keeps the data shape consistent across
        // all three OCR providers and enforces math invariants / tax-id
        // checksum / WHT-rate validity that Azure DI may have missed.
        Ocr.SmartFieldExtractor.Enrich(data, azureResult.RawText ?? "");
        return new AzureExtractionResult(true, azureResult.RawText ?? "", data, null);
    }

    private Task<OcrExtractedData> MapAzureDiToExtractedDataAsync(Guid companyId, AzureDiResult azure)
    {
        var data = new OcrExtractedData
        {
            DocumentType = MapAzureDocType(azure.DocumentType, azure.ModelId),
            Confidence = azure.OverallConfidence,
            DocumentNumber = azure.InvoiceId,
            DocumentDate = azure.InvoiceDate,
            VendorName = azure.VendorName,
            VendorTaxId = ExtractDigits(azure.VendorTaxId, 13),
            VendorAddress = azure.VendorAddress,
            VendorPhone = azure.VendorPhone,
            BuyerName = azure.CustomerName,
            BuyerTaxId = ExtractDigits(azure.CustomerTaxId, 13),
            BuyerAddress = azure.CustomerAddress,
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
        // Handwriting detection — flag scan for manual amount verification
        // when any field appears to be hand-written on a printed form.
        if (azure.HandwrittenSpanCount > 0)
        {
            data.ReasoningTrace.Add(
                $"[Azure DI] ⚠️ ตรวจพบลายมือ {azure.HandwrittenSpanCount} จุด (conf {azure.HandwrittenConfidence:P0}) — กรุณาตรวจสอบยอดเงิน");
            // Dock confidence slightly so the gateway flags it
            data.FieldConfidence["Handwriting"] = (double)azure.HandwrittenConfidence;
        }
        // Selection-mark count surfaced for tax forms
        if (azure.SelectionMarks.Count > 0)
        {
            var selected = azure.SelectionMarks.Count(m => m.State == "selected");
            data.ReasoningTrace.Add(
                $"[Azure DI] Selection marks: {selected} เลือก / {azure.SelectionMarks.Count} ทั้งหมด");
        }

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

        return Task.FromResult(data);
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
                if (DateTime.TryParse(value, out var d))
                    // Pin Kind=Utc with same y/m/d — see BUGFIX note in
                    // SmartFieldExtractor.ValidateAndNormalizeDate.
                    data.DocumentDate = new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Utc);
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
        return (result.Text ?? "", data);
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

            if (root.TryGetProperty("document_date", out var dd) && dd.GetString() is string dateStr
                && DateTime.TryParse(dateStr, out var parsedDate))
            {
                // Pin to Kind=Utc using the same y/m/d so JSON/DB
                // round-trip doesn't shift to the previous calendar day
                // (the "29 พค → 28 พค" bug). DocumentDate is a calendar
                // date, not a moment in time.
                data.DocumentDate = new DateTime(parsedDate.Year, parsedDate.Month, parsedDate.Day, 0, 0, 0, DateTimeKind.Utc);
            }

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
        // Persist remaining user edits back to the scan row — without this
        // the user changes debit/credit account or WHT, clicks save, but
        // the scan-detail panel reopens showing the original suggestions.
        // (The previous code only fed corrections to the learners; it
        // never updated the displayed scan record.)
        if (correction.ExpenseCategory != null) result.ExpenseCategory = correction.ExpenseCategory;
        if (correction.HasWht.HasValue) result.HasWht = correction.HasWht.Value;
        if (correction.WhtRate.HasValue) result.WhtRate = correction.WhtRate;
        if (correction.DebitAccountCode != null || correction.CreditAccountCode != null)
        {
            // SuggestedAccountsJson is the canonical store for the
            // displayed Dr/Cr codes. Merge the user's edits into whatever
            // the auto-suggester wrote so admin-Dr-edit doesn't blow
            // away the auto-Cr suggestion (and vice versa).
            string? debitCode = correction.DebitAccountCode;
            string? creditCode = correction.CreditAccountCode;
            string? debitName = null;
            string? creditName = null;
            if (!string.IsNullOrEmpty(debitCode))
            {
                debitName = await _db.ChartOfAccounts
                    .Where(a => a.CompanyId == companyId && a.AccountCode == debitCode && !a.IsDeleted)
                    .Select(a => a.AccountName).FirstOrDefaultAsync();
            }
            if (!string.IsNullOrEmpty(creditCode))
            {
                creditName = await _db.ChartOfAccounts
                    .Where(a => a.CompanyId == companyId && a.AccountCode == creditCode && !a.IsDeleted)
                    .Select(a => a.AccountName).FirstOrDefaultAsync();
            }
            // Preserve any side the admin didn't touch from the existing JSON.
            string? existingDebit = null, existingCredit = null;
            string? existingDebitName = null, existingCreditName = null;
            if (!string.IsNullOrEmpty(result.SuggestedAccountsJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(result.SuggestedAccountsJson);
                    var r = doc.RootElement;
                    if (r.TryGetProperty("DebitAccountCode", out var d)) existingDebit = d.GetString();
                    if (r.TryGetProperty("CreditAccountCode", out var c)) existingCredit = c.GetString();
                    if (r.TryGetProperty("DebitAccountName", out var dn)) existingDebitName = dn.GetString();
                    if (r.TryGetProperty("CreditAccountName", out var cn)) existingCreditName = cn.GetString();
                }
                catch { /* malformed — overwrite */ }
            }
            result.SuggestedAccountsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                DebitAccountCode = debitCode ?? existingDebit,
                DebitAccountName = debitName ?? existingDebitName,
                CreditAccountCode = creditCode ?? existingCredit,
                CreditAccountName = creditName ?? existingCreditName,
            });
        }

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

        // Federated doc-workflow learning — when the user confirms what
        // TargetDocumentType to create for a given (Vendor, ScannedType),
        // contribute that mapping to the cross-tenant pool. Lets the
        // next tenant who scans the SAME vendor's SAME scanned doc type
        // (e.g. "all utility receipts from MEA become PaymentVouchers")
        // get the right TargetDocumentType pre-selected.
        if (_docWorkflowLearner != null)
        {
            var scannedDocType = correction.DocumentType ?? result.ScannedDocumentType ?? result.DocumentType;
            var targetDocType = correction.TargetDocumentType ?? result.TargetDocumentType;
            if (!string.IsNullOrEmpty(scannedDocType) && !string.IsNullOrEmpty(targetDocType))
            {
                // Inline normalization (same shape ExpenseCategoryLearner uses):
                // tax-id wins when valid 13-digit; else lowercased name.
                var taxId = correction.VendorTaxId ?? result.ExtractedVendorTaxId;
                var name = correction.VendorName ?? result.ExtractedVendorName;
                string vKey = "";
                if (!string.IsNullOrEmpty(taxId))
                {
                    var digits = new string(taxId.Where(char.IsDigit).ToArray());
                    if (digits.Length == 13) vKey = $"tax:{digits}";
                }
                if (string.IsNullOrEmpty(vKey) && !string.IsNullOrEmpty(name))
                    vKey = $"name:{name.Trim().ToLowerInvariant()}";

                if (!string.IsNullOrEmpty(vKey))
                {
                    try
                    {
                        await _docWorkflowLearner.RecordConfirmAsync(companyId, vKey, scannedDocType, targetDocType);
                        await _db.SaveChangesAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Federated doc-workflow contribution write failed (non-fatal)");
                    }
                }
            }
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

        // Vendor contact details — anchored to Thai keywords so a random
        // 13-digit TaxId or 5-digit postal code can't be misread as a phone.
        var phoneMatch = VendorPhoneRegex.Match(text);
        if (phoneMatch.Success)
        {
            var raw = phoneMatch.Groups[1].Value;
            var digitCount = raw.Count(char.IsDigit);
            if (digitCount >= 9 && digitCount <= 11)
                data.VendorPhone = raw.Trim();
        }

        var emailMatch = VendorEmailRegex.Match(text);
        if (emailMatch.Success)
            data.VendorEmail = emailMatch.Value.Trim().TrimEnd('.', ',', ';');

        // Branch code — e-Tax spec requires 5-digit zero-padded; "00000" = HQ.
        var branchMatch = VendorBranchRegex.Match(text);
        if (branchMatch.Success)
            data.VendorBranchCode = branchMatch.Groups[1].Value.PadLeft(5, '0');
        else if (HeadOfficeRegex.IsMatch(text))
            data.VendorBranchCode = "00000";

        var addrMatch = VendorAddressRegex.Match(text);
        if (addrMatch.Success)
        {
            var raw = addrMatch.Groups[1].Value
                .Replace("\r", " ").Replace("\n", " ")
                .Trim();
            // >250 chars almost certainly means the regex bled through into
            // the next section — Thai vendor addresses fit in ~120 chars.
            if (raw.Length >= 10 && raw.Length <= 250)
                data.VendorAddress = raw;
        }

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
            // BUGFIX (2025-05): Many Thai SMEs print "ใบเสร็จ/ใบกำกับภาษี"
            // on a single template even when they are NOT VAT-registered
            // and cannot issue a real tax invoice. Auto-splitting the
            // TotalAmount as if it had 7% VAT in that case poisons the
            // ledger with imaginary input-VAT receivable.
            //
            // Per ประมวลรัษฎากร §86, only persons registered for VAT
            // (มี Tax ID 13 หลัก) may issue an invoice with VAT. Gate the
            // back-calculation on a valid 13-digit vendor tax ID + a
            // sanity rounding match. When in doubt, leave both fields
            // null so the gateway flags it as low-confidence + the user
            // sees the original total verbatim.
            var vendorTaxIdValid = !string.IsNullOrEmpty(data.VendorTaxId)
                && new string(data.VendorTaxId.Where(char.IsDigit).ToArray()).Length == 13;
            if (vendorTaxIdValid)
            {
                data.SubTotal = Math.Round(data.TotalAmount.Value / 1.07m, 2, MidpointRounding.AwayFromZero);
                data.VatAmount = data.TotalAmount.Value - data.SubTotal.Value;
                data.ReasoningTrace.Add(
                    "[VAT back-calc] vendor มี Tax ID 13 หลัก + เอกสารเป็น TaxInvoice → แยก VAT 7% จากยอดรวม");
            }
            else
            {
                data.ReasoningTrace.Add(
                    "[VAT skip] เอกสารพูดถึง 'ใบกำกับภาษี' แต่ vendor ไม่มี Tax ID 13 หลัก " +
                    "— ไม่สามารถ back-calc VAT ได้ (vendor ไม่จด VAT). " +
                    "ใส่ TotalAmount ตามที่อ่านมา, SubTotal/VatAmount = null");
            }
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

    public async Task<OcrResultResponse> CreateDocumentFromScanAsync(Guid companyId, Guid scanResultId, string createdBy, string? targetTypeOverride = null)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        // Normalise createdBy to a real user GUID so the document's creator
        // signature resolves (caller may pass an email/empty for headless/API
        // paths). Prefer the passed user, else the scan's operator, else owner.
        createdBy = await ResolveOcrCreatorAsync(companyId,
            Guid.TryParse(createdBy, out _) ? createdBy : result.CreatedBy, createdBy);

        if (result.ScanStatus != "Completed")
            throw new InvalidOperationException("OCR scan is not yet completed.");

        if (result.CreatedDocumentId.HasValue)
            throw new InvalidOperationException("A document has already been created from this scan.");

        // Document type precedence: explicit caller override (the user's live
        // dropdown pick in the review modal) → persisted inferred
        // TargetDocumentType → fallback mapping off the scanned paper type.
        DocumentType docType;
        if (!string.IsNullOrWhiteSpace(targetTypeOverride)
            && Enum.TryParse<DocumentType>(targetTypeOverride, ignoreCase: true, out var overrideTarget))
        {
            docType = overrideTarget;
            // Persist the user's choice so re-opening the scan reflects it.
            result.TargetDocumentType = overrideTarget.ToString();
        }
        else if (!string.IsNullOrEmpty(result.TargetDocumentType)
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

        // Due date from the OCR-read credit terms (doc date + Net N). PaymentDate
        // is only meaningful for a payment voucher (we scanned a paid receipt) —
        // the cash actually moved on the document date.
        var docDate = result.ExtractedDate ?? DateTime.UtcNow.Date;
        DateTime? dueDate = result.PaymentTermsDays.HasValue
            ? docDate.AddDays(result.PaymentTermsDays.Value)
            : null;
        DateTime? paymentDate = docType == DocumentType.PaymentVoucher ? docDate : null;

        // Transaction holds the per-tenant advisory lock for the duration
        // of the sequence-number assignment + insert, so concurrent OCR
        // creations don't collide.
        await using var txn = await _db.Database.BeginTransactionAsync();
        var docNumber = await Accounting.Helpers.DocumentNumberGenerator.NextAsync(_db, companyId, docType);
        var document = new Document
        {
            CompanyId = companyId,
            DocumentNumber = docNumber,
            DocumentType = docType,
            Status = DocumentStatus.Draft,
            DocumentDate = docDate,
            DueDate = dueDate,
            PaymentDate = paymentDate,
            ContactId = contactId.Value,
            SubTotal = result.ExtractedSubTotal ?? 0,
            VatAmount = result.ExtractedVatAmount ?? 0,
            TotalAmount = result.ExtractedTotalAmount ?? 0,
            BalanceDue = result.ExtractedTotalAmount ?? 0,
            Reference = result.ExtractedDocumentNumber,
            Notes = $"Created from OCR scan: {result.OriginalFileName}",
            CreatedBy = createdBy
        };

        // Build document lines from the persisted extracted items (the same
        // data AutoCreateDocumentAsync uses). Without this the document was
        // created with a header but ZERO lines, so it opened completely empty
        // in the editor — the user couldn't see what was scanned.
        var items = new List<OcrExtractedLineItem>();
        if (!string.IsNullOrWhiteSpace(result.ExtractedItemsJson))
        {
            try
            {
                items = System.Text.Json.JsonSerializer
                    .Deserialize<List<OcrExtractedLineItem>>(result.ExtractedItemsJson) ?? new();
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "OCR ExtractedItemsJson parse failed for scan {ScanId}", result.Id);
            }
        }

        if (items.Count > 0)
        {
            int lineOrder = 1;
            foreach (var item in items)
            {
                Guid? lineAccountId = null;
                if (!string.IsNullOrEmpty(item.SuggestedAccountCode))
                {
                    var lineAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode == item.SuggestedAccountCode && !a.IsDeleted);
                    lineAccountId = lineAccount?.Id;
                }
                document.Lines.Add(new DocumentLine
                {
                    LineOrder = lineOrder++,
                    Description = item.Description ?? result.DocumentType ?? "รายการจาก OCR",
                    Quantity = item.Quantity ?? 1,
                    UnitPrice = item.UnitPrice ?? item.Amount ?? 0,
                    Amount = item.Amount ?? 0,
                    VatRate = result.ExtractedVatAmount > 0 ? 7 : 0,
                    AccountId = lineAccountId,
                    ProjectId = item.ProjectId,
                });
            }
        }
        else
        {
            // No itemised lines were extracted — fall back to a single summary
            // line from the header totals so the document still has content.
            document.Lines.Add(new DocumentLine
            {
                LineOrder = 1,
                Description = result.DocumentType ?? "รายการจาก OCR",
                Quantity = 1,
                UnitPrice = result.ExtractedSubTotal ?? result.ExtractedTotalAmount ?? 0,
                Amount = result.ExtractedSubTotal ?? result.ExtractedTotalAmount ?? 0,
                VatRate = result.ExtractedVatAmount > 0 ? 7 : 0,
                VatAmount = result.ExtractedVatAmount ?? 0,
            });
        }

        _db.Documents.Add(document);

        result.CreatedDocumentId = document.Id;
        result.MatchedContactId = contactId;

        await _db.SaveChangesAsync();
        await txn.CommitAsync();

        // Re-link scanned file to the created document (orphan prevention)
        await RelinkScanFileToDocumentAsync(companyId, result.FileAttachmentId, document.Id);

        // Run RD-compliance + tenant-mismatch validation on the freshly
        // created document (Task 5 of ERP upgrade). Failures don't roll
        // back the document creation — they surface as
        // Document.RdComplianceStatus badges + OcrValidationLog rows.
        if (_rdComplianceValidator != null)
        {
            try
            {
                var ocrResultDto = MapToResponse(result);
                await _rdComplianceValidator.EvaluateAndPersistAsync(
                    companyId, document.Id, ocrResultDto, result.RawTextContent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RdComplianceValidator failed for document {DocId}", document.Id);
            }
        }

        return MapToResponse(result);
    }

    /// <summary>
    /// Re-populate the line items of a document that was created from an OCR
    /// scan but ended up with no lines (e.g. created before the line-building
    /// fix). Finds the scan via its CreatedDocumentId, rebuilds DocumentLines
    /// from the persisted ExtractedItemsJson, and recomputes the header totals.
    /// Refuses if the document already has real (non-blank) lines so manual
    /// work is never clobbered.
    /// </summary>
    public async Task<OcrResultResponse> RepopulateDocumentLinesFromScanAsync(
        Guid companyId, Guid documentId, string performedBy)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.CreatedDocumentId == documentId)
            ?? throw new InvalidOperationException("เอกสารนี้ไม่ได้ถูกสร้างจากการสแกน OCR");

        var document = await _db.Documents
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId)
            ?? throw new InvalidOperationException("ไม่พบเอกสาร");

        var hasRealLines = document.Lines.Any(l => l.Amount != 0 || !string.IsNullOrWhiteSpace(l.Description));
        if (hasRealLines)
            throw new InvalidOperationException("เอกสารมีรายการอยู่แล้ว — ลบรายการเดิมก่อนหากต้องการดึงจาก OCR ใหม่");

        // Drop any blank placeholder lines before rebuilding.
        if (document.Lines.Count > 0)
        {
            _db.Set<DocumentLine>().RemoveRange(document.Lines.ToList());
            document.Lines.Clear();
        }

        var items = new List<OcrExtractedLineItem>();
        if (!string.IsNullOrWhiteSpace(result.ExtractedItemsJson))
        {
            try
            {
                items = System.Text.Json.JsonSerializer
                    .Deserialize<List<OcrExtractedLineItem>>(result.ExtractedItemsJson) ?? new();
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex, "OCR ExtractedItemsJson parse failed for scan {ScanId}", result.Id);
            }
        }

        if (items.Count > 0)
        {
            int lineOrder = 1;
            foreach (var item in items)
            {
                Guid? lineAccountId = null;
                if (!string.IsNullOrEmpty(item.SuggestedAccountCode))
                {
                    var lineAccount = await _db.ChartOfAccounts.FirstOrDefaultAsync(a =>
                        a.CompanyId == companyId && a.AccountCode == item.SuggestedAccountCode && !a.IsDeleted);
                    lineAccountId = lineAccount?.Id;
                }
                document.Lines.Add(new DocumentLine
                {
                    LineOrder = lineOrder++,
                    Description = item.Description ?? result.DocumentType ?? "รายการจาก OCR",
                    Quantity = item.Quantity ?? 1,
                    UnitPrice = item.UnitPrice ?? item.Amount ?? 0,
                    Amount = item.Amount ?? 0,
                    VatRate = result.ExtractedVatAmount > 0 ? 7 : 0,
                    AccountId = lineAccountId,
                    ProjectId = item.ProjectId,
                });
            }
        }
        else
        {
            document.Lines.Add(new DocumentLine
            {
                LineOrder = 1,
                Description = result.DocumentType ?? "รายการจาก OCR",
                Quantity = 1,
                UnitPrice = result.ExtractedSubTotal ?? result.ExtractedTotalAmount ?? 0,
                Amount = result.ExtractedSubTotal ?? result.ExtractedTotalAmount ?? 0,
                VatRate = result.ExtractedVatAmount > 0 ? 7 : 0,
                VatAmount = result.ExtractedVatAmount ?? 0,
            });
        }

        // Recompute header totals from the rebuilt lines so the document is
        // self-consistent even before the user opens + saves it.
        var subTotal = document.Lines.Sum(l => l.Amount);
        var vat = document.Lines.Sum(l =>
            l.VatAmount != 0 ? l.VatAmount : Math.Round(l.Amount * l.VatRate / 100m, 2));
        document.SubTotal = subTotal;
        document.VatAmount = vat;
        document.TotalAmount = subTotal + vat;
        document.BalanceDue = subTotal + vat;
        document.UpdatedAt = DateTime.UtcNow;
        document.UpdatedBy = performedBy;

        await _db.SaveChangesAsync();
        return MapToResponse(result);
    }

    /// <summary>
    /// Record a balanced Journal Entry straight from a scan — the "บันทึก JE
    /// เท่านั้น" path for when the real document was issued in an external
    /// system and only the GL effect needs to land here. Builds Dr/Cr lines
    /// from the extracted amounts + the user's two account picks, auto-adding
    /// balanced VAT and WHT lines when those accounts resolve, then delegates
    /// to the standard CreateJournalEntryAsync (same validation + numbering).
    /// </summary>
    public async Task<Guid> CreateJournalEntryFromScanAsync(Guid companyId, Guid scanResultId,
        Models.DTOs.Ocr.CreateJeFromScanRequest request, string performedBy)
    {
        if (_accounting == null)
            throw new InvalidOperationException("Accounting service unavailable");

        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("ไม่พบผลการสแกน");
        if (result.ScanStatus != "Completed")
            throw new InvalidOperationException("สแกนยังไม่เสร็จ");
        if (result.CreatedJournalEntryId.HasValue)
            throw new InvalidOperationException("สแกนนี้บันทึก JE ไปแล้ว");
        if (result.CreatedDocumentId.HasValue)
            throw new InvalidOperationException("สแกนนี้สร้างเอกสารไปแล้ว — ไม่ต้องบันทึก JE ซ้ำ");

        // Amounts. Derive subtotal from total−vat when only the total was read.
        var vat = (request.PostVat ? result.ExtractedVatAmount : 0m) ?? 0m;
        var subtotal = result.ExtractedSubTotal ?? 0m;
        var total = result.ExtractedTotalAmount ?? (subtotal + vat);
        if (subtotal == 0m && total > 0m) subtotal = total - vat;
        if (total <= 0m)
            throw new InvalidOperationException("ไม่มียอดเงินที่อ่านได้ — กรุณาตรวจสอบยอดในหน้ารีวิวก่อน");

        decimal wht = 0m;
        if (request.PostWht && result.HasWht && result.WhtRate is > 0m)
            wht = Math.Round(subtotal * result.WhtRate!.Value / 100m, 2, MidpointRounding.AwayFromZero);

        var debitAcc = await ResolveAccountIdByCodeAsync(companyId, request.DebitAccountCode)
            ?? throw new InvalidOperationException($"ไม่พบบัญชีเดบิตรหัส {request.DebitAccountCode}");
        var creditAcc = await ResolveAccountIdByCodeAsync(companyId, request.CreditAccountCode)
            ?? throw new InvalidOperationException($"ไม่พบบัญชีเครดิตรหัส {request.CreditAccountCode}");

        var isSeller = string.Equals(result.OurRole, "Seller", StringComparison.OrdinalIgnoreCase);

        Guid? vatAcc = null;
        if (request.PostVat && vat > 0m)
            vatAcc = isSeller
                ? await ResolveTaxAccountAsync(companyId, new[] { "21911", "21910", "2192" }, AccountType.Liability, "ภาษีขาย")
                : await ResolveTaxAccountAsync(companyId, new[] { "11511", "1151", "115" }, AccountType.Asset, "ภาษีซื้อ");

        Guid? whtAcc = null;
        if (wht > 0m)
            whtAcc = await ResolveTaxAccountAsync(companyId, new[] { "21701", "2161", "2162" }, AccountType.Liability, "หัก ณ ที่จ่าย");

        // Build a guaranteed-balanced set of lines. Optional VAT/WHT lines only
        // appear when their account resolves; the primary side absorbs the rest
        // so total debits always equal total credits.
        var lines = new List<Accounting.Models.DTOs.Accounting.JournalLineRequest>();
        if (isSeller)
        {
            var vatLine = vatAcc != null ? vat : 0m;
            var revenueAmt = total - vatLine;
            lines.Add(new(debitAcc, total, 0m, "ลูกหนี้/เงินรับ"));
            lines.Add(new(creditAcc, 0m, revenueAmt, "รายได้"));
            if (vatLine > 0m) lines.Add(new(vatAcc!.Value, 0m, vatLine, "ภาษีขาย"));
        }
        else
        {
            var vatLine = vatAcc != null ? vat : 0m;
            var whtLine = whtAcc != null ? wht : 0m;
            var expenseAmt = total - vatLine;
            var creditAmt = total - whtLine;
            lines.Add(new(debitAcc, expenseAmt, 0m, "ค่าใช้จ่าย/สินทรัพย์"));
            if (vatLine > 0m) lines.Add(new(vatAcc!.Value, vatLine, 0m, "ภาษีซื้อ"));
            if (whtLine > 0m) lines.Add(new(whtAcc!.Value, 0m, whtLine, "ภาษีหัก ณ ที่จ่าย"));
            lines.Add(new(creditAcc, 0m, creditAmt, "เจ้าหนี้/เงินจ่าย"));
        }

        var entryDate = request.EntryDate ?? result.ExtractedDate ?? DateTime.UtcNow.Date;
        var description = !string.IsNullOrWhiteSpace(request.Description)
            ? request.Description!
            : $"บันทึกจากสแกน OCR: {result.OriginalFileName}";

        var jeReq = new Accounting.Models.DTOs.Accounting.CreateJournalEntryRequest(
            EntryDate: entryDate,
            Description: description,
            Reference: result.ExtractedDocumentNumber,
            Lines: lines,
            JournalType: isSeller ? JournalType.Sales : JournalType.Purchase);

        var je = await _accounting.CreateJournalEntryAsync(companyId, jeReq, performedBy);

        result.CreatedJournalEntryId = je.Id;
        result.ProcessingNotes = (result.ProcessingNotes ?? "") + $" JE recorded: {je.EntryNumber}.";
        await _db.SaveChangesAsync();

        // Re-link the scanned file to nothing extra — it stays attached to the
        // scan; the JE references it via the scan in the audit trail.
        return je.Id;
    }

    /// <summary>Resolve a company account by exact code → Id (active only).</summary>
    private async Task<Guid?> ResolveAccountIdByCodeAsync(Guid companyId, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive && a.AccountCode == code)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>Resolve a VAT/WHT account by trying a list of standard codes,
    /// then falling back to the first active account of the right type whose
    /// name contains the keyword. Returns null when nothing matches (caller
    /// then folds the amount into the primary line to keep the JE balanced).</summary>
    private async Task<Guid?> ResolveTaxAccountAsync(Guid companyId, string[] codes, AccountType type, string nameKeyword)
    {
        foreach (var c in codes)
        {
            var hit = await ResolveAccountIdByCodeAsync(companyId, c);
            if (hit != null) return hit;
        }
        return await _db.ChartOfAccounts
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && a.IsActive
                && a.AccountType == type && a.AccountName!.Contains(nameKeyword))
            .OrderBy(a => a.AccountCode)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync();
    }

    public async Task SetExtractedLineProjectAsync(Guid companyId, Guid scanResultId,
        int lineIndex, Guid? projectId, string? projectName)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");
        if (string.IsNullOrEmpty(scan.ExtractedItemsJson))
            throw new InvalidOperationException("Scan ไม่มีรายการสินค้าใน OCR result.");
        if (lineIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));

        // Verify the project exists in this company when assigning.
        if (projectId.HasValue)
        {
            var exists = await _db.Projects.AnyAsync(
                p => p.Id == projectId.Value && p.CompanyId == companyId && !p.IsDeleted);
            if (!exists) throw new InvalidOperationException("ไม่พบ project ที่ระบุ");
        }

        List<OcrExtractedLineItem> items;
        try
        {
            items = System.Text.Json.JsonSerializer
                .Deserialize<List<OcrExtractedLineItem>>(scan.ExtractedItemsJson) ?? new();
        }
        catch
        {
            throw new InvalidOperationException("ExtractedItemsJson เสียหาย — ไม่สามารถ parse");
        }
        if (lineIndex >= items.Count)
            throw new ArgumentOutOfRangeException(nameof(lineIndex), "lineIndex เกินจำนวนรายการที่สแกนได้");

        items[lineIndex].ProjectId = projectId;
        items[lineIndex].ProjectName = projectName;
        scan.ExtractedItemsJson = System.Text.Json.JsonSerializer.Serialize(items);
        scan.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task SetAllExtractedLineProjectsAsync(Guid companyId, Guid scanResultId,
        Guid? projectId, string? projectName, bool onlyEmpty)
    {
        var scan = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");
        if (string.IsNullOrEmpty(scan.ExtractedItemsJson))
            throw new InvalidOperationException("Scan ไม่มีรายการสินค้าใน OCR result.");

        if (projectId.HasValue)
        {
            var exists = await _db.Projects.AnyAsync(
                p => p.Id == projectId.Value && p.CompanyId == companyId && !p.IsDeleted);
            if (!exists) throw new InvalidOperationException("ไม่พบ project ที่ระบุ");
        }

        List<OcrExtractedLineItem> items;
        try
        {
            items = System.Text.Json.JsonSerializer
                .Deserialize<List<OcrExtractedLineItem>>(scan.ExtractedItemsJson) ?? new();
        }
        catch
        {
            throw new InvalidOperationException("ExtractedItemsJson เสียหาย — ไม่สามารถ parse");
        }

        // Preserve user's prior per-line overrides when onlyEmpty=true.
        // The UI uses this when the user clicks "apply main" AFTER
        // already overriding some rows individually — we don't want
        // to clobber their work.
        foreach (var item in items)
        {
            if (onlyEmpty && item.ProjectId.HasValue) continue;
            item.ProjectId = projectId;
            item.ProjectName = projectName;
        }
        scan.ExtractedItemsJson = System.Text.Json.JsonSerializer.Serialize(items);
        scan.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
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

    /// <summary>Resolve a REAL user GUID to stamp as a document's CreatedBy so
    /// signature resolution works (ResolveSignersAsync parses CreatedBy as a
    /// Guid → looks up the user's signature). OCR auto-create previously stamped
    /// a literal like "OCR-AutoCreate" which isn't a GUID, so the creator's
    /// signature slot (e.g. ผู้จ่ายเงิน on a Payment Voucher) stayed blank.
    /// Order: the scan's own creator (the operator who ran OCR) → the company
    /// Owner (covers API/headless scans where no operator is known) → the
    /// literal fallback only if neither exists.</summary>
    private async Task<string> ResolveOcrCreatorAsync(Guid companyId, string? scanCreatedBy, string fallback)
    {
        if (Guid.TryParse(scanCreatedBy, out var opId))
        {
            var ok = await _db.Set<CompanyUser>().AsNoTracking()
                .AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == opId);
            if (ok) return opId.ToString();
        }
        var ownerId = await _db.Set<CompanyUser>().AsNoTracking()
            .Where(cu => cu.CompanyId == companyId && cu.Role == Models.Enums.UserRole.Owner)
            .Select(cu => (Guid?)cu.UserId)
            .FirstOrDefaultAsync();
        return ownerId?.ToString() ?? fallback;
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

        var autoDocDate = scan.ExtractedDate ?? DateTime.UtcNow.Date;
        DateTime? autoDueDate = scan.PaymentTermsDays.HasValue
            ? autoDocDate.AddDays(scan.PaymentTermsDays.Value)
            : null;
        DateTime? autoPaymentDate = docType == DocumentType.PaymentVoucher ? autoDocDate : null;

        await using var txn = await _db.Database.BeginTransactionAsync();
        var docNumber = await Accounting.Helpers.DocumentNumberGenerator.NextAsync(_db, companyId, docType);
        var document = new Document
        {
            CompanyId = companyId,
            DocumentNumber = docNumber,
            DocumentType = docType,
            Status = DocumentStatus.Draft,
            DocumentDate = autoDocDate,
            DueDate = autoDueDate,
            PaymentDate = autoPaymentDate,
            ContactId = scan.MatchedContactId!.Value,
            SubTotal = scan.ExtractedSubTotal ?? 0,
            VatAmount = scan.ExtractedVatAmount ?? 0,
            TotalAmount = scan.ExtractedTotalAmount ?? 0,
            BalanceDue = scan.ExtractedTotalAmount ?? 0,
            Reference = scan.ExtractedDocumentNumber,
            Notes = $"Auto-created from OCR scan (confidence: {scan.Confidence:P0}): {scan.OriginalFileName}",
            CreatedBy = await ResolveOcrCreatorAsync(companyId, scan.CreatedBy, "OCR-AutoCreate")
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
                    // Per-line project allocation from the OCR review UI —
                    // user picks the project in /pages/ocr-review.html
                    // before clicking "Create document". When set, this
                    // line books costs against the right job/project.
                    ProjectId = item.ProjectId,
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
        await txn.CommitAsync();

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

        // Side-effect: if the user assigned a project to this line via
        // the review UI (POST /line-project), promote it into the scope
        // so RegisterAssetAsync (and any future per-line writer) can
        // honour the allocation. Kept inline next to the resolver so
        // the read+write path stays bookended.

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
                        el.TryGetProperty("SuggestedAccountCode", out var s) ? s.GetString() : null,
                        el.TryGetProperty("ProjectId", out var pid) && pid.ValueKind == System.Text.Json.JsonValueKind.String
                            && Guid.TryParse(pid.GetString(), out var pg) ? pg : null,
                        el.TryGetProperty("ProjectName", out var pn) ? pn.GetString() : null);
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
                    i.Description, i.Quantity, i.UnitPrice, i.Amount, i.SuggestedAccountCode,
                    i.ProjectId, i.ProjectName)).ToList();
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
                    el.TryGetProperty("SuggestedAccountCode", out var s) ? s.GetString() : null,
                    el.TryGetProperty("ProjectId", out var pid) && pid.ValueKind == System.Text.Json.JsonValueKind.String
                        && Guid.TryParse(pid.GetString(), out var pg) ? pg : (Guid?)null,
                    el.TryGetProperty("ProjectName", out var pn) ? pn.GetString() : null
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
            // Prefer the in-memory extraction (fresh scan path) but
            // fall back to the persisted entity values when remapping
            // a list row or a page reload where `data` is null.
            data?.BuyerName ?? r.BuyerName,
            data?.BuyerTaxId ?? r.BuyerTaxId,
            dbdInfo,
            r.ScannedDocumentType,
            r.OurRole,
            r.TargetDocumentType,
            r.OcrEngine,
            r.HasPotentialFixedAsset,
            r.PotentialAssetLinesJson,
            Quality: BuildQualityDto(r),
            HasHandwriting: r.HasHandwriting,
            HandwritingConfidence: r.HandwritingConfidence);
    }

    private static OcrQualityGradeDto? BuildQualityDto(OcrScanResult r)
    {
        if (r.ScanStatus != "Completed") return null;
        var grade = Ocr.ScanQualityGrader.Compute(r);
        return new OcrQualityGradeDto(grade.Letter, grade.Score, grade.Color);
    }

    /// <summary>
    /// Map ETDA e-Tax XML extraction result onto the OcrExtractedData shape
    /// the rest of the pipeline (gateway, role inferrer, vendor enrichment)
    /// consumes. Confidence is hard-pinned to 100% because every value came
    /// from the legally authoritative XML — not heuristic OCR text. Each
    /// field also gets a 1.0 FieldConfidence so the gateway treats them as
    /// gold and won't try to "correct" them.
    /// </summary>
    private static OcrExtractedData MapEtaxToOcrData(Ocr.EtaxPdfXmlExtractor.ExtractResult etax)
    {
        var data = new OcrExtractedData
        {
            DocumentType = etax.MappedDocumentType ?? "TaxInvoice",
            Confidence = 1.0m,
            DocumentNumber = etax.DocumentNumber,
            DocumentDate = etax.DocumentDate,
            VendorName = etax.SellerName,
            VendorTaxId = etax.SellerTaxId,
            VendorBranchCode = etax.SellerBranchCode,
            VendorAddress = etax.SellerAddress,
            BuyerName = etax.BuyerName,
            BuyerTaxId = etax.BuyerTaxId,
            BuyerBranchCode = etax.BuyerBranchCode,
            BuyerAddress = etax.BuyerAddress,
            SubTotal = etax.LineTotal ?? etax.TaxBasis,
            VatAmount = etax.VatAmount,
            TotalAmount = etax.GrandTotal,
        };

        foreach (var k in new[] { "DocumentNumber", "DocumentDate", "VendorName", "VendorTaxId",
            "VendorAddress", "BuyerName", "BuyerTaxId", "SubTotal", "VatAmount", "TotalAmount" })
        {
            data.FieldConfidence[k] = 1.0;
        }

        foreach (var li in etax.Items)
        {
            data.Items.Add(new OcrExtractedLineItem
            {
                Description = li.Description,
                Quantity = li.Quantity,
                UnitPrice = li.UnitPrice,
                Amount = li.Amount,
            });
        }

        data.ReasoningTrace.Add(
            $"[Tier 0] ดึงค่าจาก e-Tax XML ที่ฝังใน PDF/A-3 — เอกสาร {etax.DocumentTypeName} " +
            $"({etax.DocumentTypeCode}) เลขที่ {etax.DocumentNumber}, ยอดรวม {etax.GrandTotal:N2} {etax.Currency}");
        return data;
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
    // ─── Vendor contact details (used by Contact auto-create fallback when DBD fails) ───
    // Azure DI populates VendorAddress/VendorPhone directly; ParseThaiDocument
    // best-effort extracts them from raw text via regex on Thai phone/postal patterns.
    public string? VendorAddress { get; set; }
    public string? VendorPhone { get; set; }
    public string? VendorEmail { get; set; }
    public string? VendorBranchCode { get; set; }
    public string? BuyerAddress { get; set; }
    public string? BuyerPhone { get; set; }
    public string? BuyerEmail { get; set; }
    public string? BuyerBranchCode { get; set; }
}

internal class OcrExtractedLineItem
{
    public string? Description { get; set; }
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Amount { get; set; }
    public string? SuggestedAccountCode { get; set; }
    /// <summary>Per-line project assignment captured in the review UI.
    /// Persisted so re-opening the review after a crash preserves the
    /// user's allocation work + auto-create uses it.</summary>
    public Guid? ProjectId { get; set; }
    public string? ProjectName { get; set; }
}
