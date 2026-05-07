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

    public OcrService(AccountingDbContext db, IHttpClientFactory httpClientFactory,
        IConfiguration configuration, ILogger<OcrService> logger,
        IDbdLookupService dbdLookup,
        AzureDocumentIntelligenceService azureDi,
        ExpenseCategoryLearner categoryLearner)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _dbdLookup = dbdLookup;
        _azureDi = azureDi;
        _categoryLearner = categoryLearner;
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

        // Load SiteSettings ONCE per scan — feeds gateway config + provider routing + Azure DI.
        // Avoids 3 separate roundtrips for the same single-row table.
        var siteSettings = await _db.SiteSettings.AsNoTracking().FirstOrDefaultAsync();
        var effectiveConfig = BuildEffectiveOcrConfig(siteSettings);
        var ocrProvider = effectiveConfig.Provider;
        OcrExtractedData? extractedData = null;

        var scanStartedAt = DateTime.UtcNow;
        _logger.LogInformation(
            "OCR scan started ScanId={ScanId} CompanyId={CompanyId} Provider={Provider} FileSize={FileSize} ContentType={ContentType}",
            scanResult.Id, companyId, ocrProvider, file.FileSize, file.ContentType);

        try
        {
            string extractedText;

            // ─── Strict provider chain: Azure DI (if configured) → Local (PaddleOCR+Tesseract) ───
            // Legacy providers (Google Vision, Azure v3.x, standalone Tesseract) are
            // intentionally removed — their accuracy on Thai invoices is consistently
            // worse than Azure DI v4 and the local PaddleOCR+Tesseract combo.
            //
            // The local microservice (default http://localhost:8501) runs PaddleOCR for
            // primary recognition + Tesseract for verification on low-confidence regions.
            // It serves as the bedrock fallback when Azure DI is unavailable.

            var azureEnabled = siteSettings?.AzureDiEnabled == true
                && !string.IsNullOrEmpty(siteSettings.AzureDiEndpoint)
                && !string.IsNullOrEmpty(siteSettings.AzureDiApiKey);

            if (azureEnabled && ocrProvider != "local")
            {
                var azureResult = await ExtractWithAzureDiAsync(companyId, file, siteSettings);
                if (azureResult.Success)
                {
                    extractedText = azureResult.Text;
                    extractedData = azureResult.Data;
                }
                else
                {
                    _logger.LogWarning("Azure DI failed ({Err}) — falling back to local PaddleOCR+Tesseract", azureResult.Error ?? "unknown");
                    var localResult = await ExtractWithLocalServiceAsync(file);
                    extractedText = localResult.RawText;
                    extractedData = localResult.Data;
                    extractedData.ReasoningTrace.Insert(0, $"[Fallback] Azure DI failed: {azureResult.Error ?? "unknown"} — used local pipeline");
                }
            }
            else
            {
                // Local pipeline (PaddleOCR + Tesseract combined inside the microservice)
                var localResult = await ExtractWithLocalServiceAsync(file);
                extractedText = localResult.RawText;
                extractedData = localResult.Data;
                if (!azureEnabled)
                    extractedData.ReasoningTrace.Insert(0, "[Provider] Azure DI ไม่เปิดใช้ — ใช้ Local (PaddleOCR+Tesseract)");
            }

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
            scanResult.HasWht = extractedData.HasWht;
            scanResult.WhtRate = extractedData.WhtRate;
            scanResult.PaymentTermsDays = extractedData.PaymentTermsDays;

            // Store zone analysis info for debugging
            if (!string.IsNullOrEmpty(extractedData.ZoneSummary))
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n[Zone Analysis]\n" + extractedData.ZoneSummary;
            if (!string.IsNullOrEmpty(extractedData.BuyerName))
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + $"\n[Buyer] {extractedData.BuyerName} TaxID:{extractedData.BuyerTaxId ?? "N/A"}";
            if (extractedData.FieldConfidence.Count > 0)
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n[Field Confidence]\n" +
                    string.Join("\n", extractedData.FieldConfidence.Select(kv => $"  {kv.Key}: {kv.Value:P0}"));
            if (extractedData.ReasoningTrace.Count > 0)
                scanResult.ProcessingNotes = (scanResult.ProcessingNotes ?? "") + "\n[Reasoning]\n" +
                    string.Join("\n", extractedData.ReasoningTrace.Select(r => "  • " + r));

            // ───── Learned category prediction ─────
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
                extractedData.ReasoningTrace.Add($"[Learner] เคยใช้รหัส {learnedCode} กับผู้ขายนี้ (confidence {learnedConf:P0})");

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
                var matchedContact = await _db.Contacts
                    .FirstOrDefaultAsync(c => c.CompanyId == companyId
                        && c.Name.Contains(extractedData.VendorName) && !c.IsDeleted);
                scanResult.MatchedContactId = matchedContact?.Id;
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

    private record EffectiveOcrConfig(string Provider, string? LocalServiceUrl, string? ApiKey, string? AzureEndpoint, decimal AutoCreateThreshold);

    private EffectiveOcrConfig BuildEffectiveOcrConfig(SiteSettings? siteSettings)
    {
        return new EffectiveOcrConfig(
            Provider: (siteSettings?.OcrProvider ?? _configuration["Ocr:Provider"] ?? "local").ToLower(),
            LocalServiceUrl: siteSettings?.OcrLocalServiceUrl ?? _configuration["Ocr:LocalServiceUrl"] ?? "http://localhost:8501",
            ApiKey: _configuration["Ocr:ApiKey"],
            AzureEndpoint: siteSettings?.AzureDiEndpoint ?? _configuration["Ocr:AzureEndpoint"],
            AutoCreateThreshold: siteSettings?.OcrAutoCreateThreshold ?? (decimal.TryParse(_configuration["Ocr:AutoCreateThreshold"], out var t2) ? t2 : 0.85m));
    }

    private record AzureExtractionResult(bool Success, string Text, OcrExtractedData Data, string? Error);

    private async Task<AzureExtractionResult> ExtractWithAzureDiAsync(Guid companyId, FileAttachment file, SiteSettings? siteSettings)
    {
        if (!System.IO.File.Exists(file.StoragePath))
            return new AzureExtractionResult(false, "", new OcrExtractedData(), "Source file missing");

        var fileBytes = await System.IO.File.ReadAllBytesAsync(file.StoragePath);
        var contentType = OcrPreprocessor.EffectiveContentType(file.ContentType ?? "", file.OriginalFileName);

        var preflight = OcrPreprocessor.Check(fileBytes, contentType, file.OriginalFileName);
        if (!preflight.Ok)
            return new AzureExtractionResult(false, "", new OcrExtractedData(), preflight.ErrorMessage);

        var azureResult = await _azureDi.AnalyzeAsync(fileBytes, contentType, siteSettings);
        if (azureResult == null)
            return new AzureExtractionResult(false, "", new OcrExtractedData(), "Azure DI not enabled or not configured");
        if (!azureResult.Success)
            return new AzureExtractionResult(false, "", new OcrExtractedData(), azureResult.ErrorMessage);

        var data = await MapAzureDiToExtractedDataAsync(companyId, azureResult);
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

        // NOTE: Seller/buyer swap is intentionally NOT performed here. The unified
        // swap block in ScanAsync runs AFTER OcrConfidenceGateway, ensuring all
        // OCR providers (local + Azure DI) submit pre-swap data to the gateway —
        // so checksum and math validations are consistent across paths.

        return data;
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
            // Local PaddleOCR+Tesseract microservice unavailable. Without external providers
            // (Google/Azure v3.x), the only thing we can return is the original filename so
            // user can manually re-upload or fix infrastructure.
            _logger.LogError(ex, "Local OCR service unreachable at {Url} — admin must verify the microservice is running",
                _configuration["Ocr:LocalServiceUrl"] ?? "(unset)");
            var fallbackText = ExtractFromFileName(file);
            var stub = ParseThaiDocument(fallbackText);
            stub.ReasoningTrace.Insert(0, "[Critical] Local OCR microservice ไม่ตอบสนอง — กรุณาตรวจสอบการตั้งค่า admin OR พิจารณาเปิด Azure DI");
            return (fallbackText, stub);
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
        if (correction.DocumentType != null) result.DocumentType = correction.DocumentType;
        if (correction.VendorName != null) result.ExtractedVendorName = correction.VendorName;
        if (correction.VendorTaxId != null) result.ExtractedVendorTaxId = correction.VendorTaxId;
        if (correction.DocumentNumber != null) result.ExtractedDocumentNumber = correction.DocumentNumber;
        if (correction.DocumentDate.HasValue) result.ExtractedDate = correction.DocumentDate;
        if (correction.SubTotal.HasValue) result.ExtractedSubTotal = correction.SubTotal;
        if (correction.VatAmount.HasValue) result.ExtractedVatAmount = correction.VatAmount;
        if (correction.TotalAmount.HasValue) result.ExtractedTotalAmount = correction.TotalAmount;

        await _db.SaveChangesAsync();

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

    private static string ExtractFromFileName(FileAttachment file)
    {
        return file.OriginalFileName ?? "";
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

    private async Task<OcrExtractedData> AnalyzeWithZones(Guid companyId, string text)
    {
        List<OcrLearnedPattern>? patterns = null;
        try
        {
            patterns = await _db.OcrLearnedPatterns
                .Where(p => p.CompanyId == companyId && !p.IsDeleted)
                .OrderByDescending(p => p.TimesConfirmed)
                .Take(200)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load learned patterns — table may not exist yet");
        }

        // Load our company's TaxId so analyzer can disambiguate seller vs buyer
        string? ourTaxId = null;
        try
        {
            var company = await _db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId);
            ourTaxId = company?.TaxId;
        }
        catch { /* non-fatal */ }

        var zoneResult = DocumentZoneAnalyzer.Analyze(text, patterns, ourTaxId);

        // GL account suggestions map
        var glMap = new Dictionary<string, (string Dc, string Dn, string Cc, string Cn, string? Vc, string? Vn)>
        {
            ["TaxInvoice"] = ("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า", "1140", "ภาษีซื้อ"),
            ["Invoice"] = ("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า", null, null),
            ["Receipt"] = ("5300", "ค่าใช้จ่ายบริหาร", "1110", "เงินสด", null, null),
            ["PurchaseOrder"] = ("1200", "สินค้าคงเหลือ", "2100", "เจ้าหนี้การค้า", null, null),
            ["WHT"] = ("2170", "ภาษีหัก ณ ที่จ่าย", "1110", "เงินสด", null, null),
            ["CreditNote"] = ("2100", "เจ้าหนี้การค้า", "5100", "ต้นทุนขาย", null, null),
            ["DebitNote"] = ("5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า", null, null),
        };

        var data = new OcrExtractedData
        {
            DocumentType = zoneResult.DocumentType ?? "Receipt",
            Confidence = zoneResult.Confidence,
            DocumentNumber = zoneResult.DocumentNumber,
            DocumentDate = zoneResult.DocumentDate,
            VendorName = zoneResult.SellerName,
            VendorTaxId = zoneResult.SellerTaxId,
            SubTotal = zoneResult.SubTotal,
            VatAmount = zoneResult.VatAmount,
            TotalAmount = zoneResult.TotalAmount,
            ExpenseCategory = zoneResult.ExpenseCategory,
            HasWht = zoneResult.HasWht,
            WhtRate = zoneResult.WhtRate,
            PaymentTermsDays = zoneResult.PaymentTermsDays,
        };

        if (data.DocumentType != null && glMap.TryGetValue(data.DocumentType, out var gl))
        {
            data.DebitAccountCode = gl.Dc;
            data.DebitAccountName = gl.Dn;
            data.CreditAccountCode = gl.Cc;
            data.CreditAccountName = gl.Cn;
            data.VatAccountCode = gl.Vc;
            data.VatAccountName = gl.Vn;
        }

        data.ZoneSummary = zoneResult.ZoneSummary;
        data.BuyerName = zoneResult.BuyerName;
        data.BuyerTaxId = zoneResult.BuyerTaxId;
        data.FieldConfidence = zoneResult.FieldConfidence;
        data.ReasoningTrace = zoneResult.ReasoningTrace;

        return data;
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

        if (hasTaxInvoice) { data.DocumentType = "TaxInvoice"; data.Confidence = 0.95m; }
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

    public async Task DeleteScanAsync(Guid companyId, Guid scanResultId)
    {
        var result = await _db.Set<OcrScanResult>()
            .FirstOrDefaultAsync(r => r.CompanyId == companyId && r.Id == scanResultId)
            ?? throw new InvalidOperationException("OCR scan result not found.");

        if (result.CreatedDocumentId.HasValue)
            throw new InvalidOperationException("Cannot delete: a document has already been created from this scan.");

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
            dbdInfo);
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
