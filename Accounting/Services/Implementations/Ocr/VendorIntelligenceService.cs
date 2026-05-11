using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Self-learning vendor intelligence aggregator.
///
/// What it does:
///   • Aggregates historical Document data per vendor (DocumentType frequency,
///     debit account frequency, WHT habits, amount range, payment terms)
///   • Predicts likely DocumentType / debit account / WHT for new OCR scans
///   • Trains incrementally each time a Document is approved
///   • Backfills the cache from existing Documents (one-shot per company)
///
/// Why it's separate from ExpenseCategoryLearner:
///   ExpenseCategoryLearner is per-(vendor + line description) granularity and
///   only knows about debit account. VendorIntelligence is the higher-level
///   per-vendor "what does this supplier usually look like?" cache used to
///   predict DocumentType, WHT, and to flag amount anomalies.
/// </summary>
public class VendorIntelligenceService
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<VendorIntelligenceService> _logger;

    // Confidence thresholds for auto-apply (>= these → trace as "high confidence override")
    public const decimal HighConfidence = 0.85m;
    public const decimal MediumConfidence = 0.65m;

    // Single source of truth — used by BOTH TrainFromDocumentAsync (filter) and
    // BackfillFromHistoryAsync (filter) so the two methods always agree on which
    // document types contribute to vendor intelligence.
    public static readonly DocumentType[] PurchaseSideTypes = {
        DocumentType.PurchaseInvoice,
        DocumentType.Expense,
        DocumentType.CertificateInLieu,
        DocumentType.PurchaseOrder,
        DocumentType.PaymentVoucher,
    };

    public VendorIntelligenceService(AccountingDbContext db, ILogger<VendorIntelligenceService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────
    // PREDICT — called from OcrService.ScanAsync
    // ─────────────────────────────────────────────────────────────────

    public async Task<VendorPrediction> PredictAsync(Guid companyId, string? vendorTaxId, string? vendorName, decimal? scannedTotalAmount = null)
    {
        var key = NormalizeVendorKey(vendorTaxId, vendorName);
        var prediction = new VendorPrediction();
        if (string.IsNullOrEmpty(key)) return prediction;

        var intel = await _db.OcrVendorIntelligence
            .FirstOrDefaultAsync(v => v.CompanyId == companyId && v.VendorKey == key && !v.IsDeleted);

        // No tenant row → fall back to the SystemAdmin-trained knowledge base
        // (system-wide row, no CompanyId). Tenant data always wins when present.
        var isSystemFallback = false;
        if (intel == null || intel.TotalDocuments == 0)
        {
            var sysIntel = await _db.SystemOcrVendorIntelligence
                .FirstOrDefaultAsync(v => v.VendorKey == key && !v.IsDeleted);
            if (sysIntel == null || sysIntel.TotalDocuments == 0) return prediction;

            intel = new OcrVendorIntelligence
            {
                VendorKey = sysIntel.VendorKey,
                VendorName = sysIntel.VendorName,
                VendorTaxId = sysIntel.VendorTaxId,
                MostCommonDocumentType = sysIntel.MostCommonDocumentType,
                MostCommonDocumentTypeCount = sysIntel.MostCommonDocumentTypeCount,
                TotalDocuments = sysIntel.TotalDocuments,
                DocumentTypeBreakdownJson = sysIntel.DocumentTypeBreakdownJson,
                MostCommonDebitAccountCode = sysIntel.MostCommonDebitAccountCode,
                MostCommonDebitAccountName = sysIntel.MostCommonDebitAccountName,
                MostCommonDebitAccountCount = sysIntel.MostCommonDebitAccountCount,
                DebitAccountBreakdownJson = sysIntel.DebitAccountBreakdownJson,
                TypicallyHasWht = sysIntel.TypicallyHasWht,
                TypicalWhtRate = sysIntel.TypicalWhtRate,
                WhtUsageCount = sysIntel.WhtUsageCount,
                AvgTotalAmount = sysIntel.AvgTotalAmount,
                MinTotalAmount = sysIntel.MinTotalAmount,
                MaxTotalAmount = sysIntel.MaxTotalAmount,
                MedianTotalAmount = sysIntel.MedianTotalAmount,
                TypicalPaymentTermsDays = sysIntel.TypicalPaymentTermsDays,
            };
            isSystemFallback = true;
        }

        prediction.HasHistory = true;
        prediction.SampleSize = intel.TotalDocuments;

        // ─── DocumentType prediction ───
        if (!string.IsNullOrEmpty(intel.MostCommonDocumentType) && intel.TotalDocuments > 0)
        {
            var pct = (decimal)intel.MostCommonDocumentTypeCount / intel.TotalDocuments;
            // Confidence = dominance × sample-size factor
            // 1 sample → max 0.5; 5 samples → max 0.83; 20 samples → max 0.95
            var sizeFactor = 1m - (1m / (1m + 0.4m * intel.TotalDocuments));
            prediction.DocumentTypeConfidence = pct * sizeFactor;
            if (Enum.TryParse<DocumentType>(intel.MostCommonDocumentType, out var dt))
            {
                prediction.DocumentType = dt;
                prediction.Reasons.Add(
                    $"ผู้ขายรายนี้ใช้เอกสารประเภท {ThaiDocLabel(dt)} จำนวน {intel.MostCommonDocumentTypeCount}/{intel.TotalDocuments} ครั้ง ({pct:P0})");
            }
        }

        // ─── Debit account prediction ───
        if (!string.IsNullOrEmpty(intel.MostCommonDebitAccountCode) && intel.TotalDocuments > 0)
        {
            var pct = (decimal)intel.MostCommonDebitAccountCount / intel.TotalDocuments;
            var sizeFactor = 1m - (1m / (1m + 0.4m * intel.TotalDocuments));
            prediction.DebitAccountConfidence = pct * sizeFactor;
            prediction.DebitAccountCode = intel.MostCommonDebitAccountCode;
            prediction.DebitAccountName = intel.MostCommonDebitAccountName;
            prediction.Reasons.Add(
                $"เคยบันทึกเข้ารหัสบัญชี {intel.MostCommonDebitAccountCode}{(string.IsNullOrEmpty(intel.MostCommonDebitAccountName) ? "" : " - " + intel.MostCommonDebitAccountName)} จำนวน {intel.MostCommonDebitAccountCount}/{intel.TotalDocuments} ครั้ง ({pct:P0})");
        }

        // ─── WHT habits ───
        // Confidence is "how sure are we about whichever side (yes/no) we're picking",
        // i.e. the dominance of the majority class. Uniform binary entropy: max(p, 1-p).
        // 50/50 split → 0.5 confidence (genuine uncertainty); 90/10 → 0.9 in either direction.
        if (intel.WhtUsageCount > 0 || intel.TotalDocuments > 0)
        {
            var whtPct = (decimal)intel.WhtUsageCount / intel.TotalDocuments;
            prediction.HasWht = whtPct >= 0.5m;
            prediction.WhtConfidence = Math.Max(whtPct, 1m - whtPct);
            if (prediction.HasWht == true && intel.TypicalWhtRate.HasValue)
            {
                prediction.WhtRate = intel.TypicalWhtRate;
                prediction.Reasons.Add($"ผู้ขายรายนี้มักหักภาษี ณ ที่จ่าย {intel.TypicalWhtRate}% ({intel.WhtUsageCount}/{intel.TotalDocuments} ครั้ง)");
            }
        }

        // ─── Payment terms ───
        if (intel.TypicalPaymentTermsDays.HasValue)
            prediction.TypicalPaymentTermsDays = intel.TypicalPaymentTermsDays;

        // ─── Amount sanity check (z-score on log-amount) ───
        // Robust to heavy-tailed amount distributions: a vendor that mostly
        // bills ฿2,000 but occasionally ฿20,000 has a wide raw-amount range
        // but a tight log-amount distribution → z-score flags ฿200,000
        // correctly while the old min/max ±50% rule would miss it.
        if (scannedTotalAmount.HasValue && scannedTotalAmount.Value > 0
            && intel.LogAmountMean.HasValue && intel.LogAmountVariance.HasValue
            && intel.LogAmountVariance.Value > 0)
        {
            var stddev = (decimal)Math.Sqrt((double)intel.LogAmountVariance.Value);
            var anomaly = AmountAnomalyDetector.CheckZScore(
                scannedTotalAmount.Value,
                mean: intel.AvgTotalAmount,
                stddev: stddev,
                threshold: 3m);
            if (anomaly != null)
            {
                prediction.AmountWithinTypicalRange = !anomaly.IsAnomaly;
                if (anomaly.IsAnomaly)
                    prediction.Reasons.Add($"⚠️ {anomaly.Reason}");
            }
        }
        else if (scannedTotalAmount.HasValue && scannedTotalAmount.Value > 0
            && intel.MinTotalAmount.HasValue && intel.MaxTotalAmount.HasValue)
        {
            // Fallback for vendors with too few samples for z-score
            var lower = intel.MinTotalAmount.Value * 0.5m;
            var upper = intel.MaxTotalAmount.Value * 1.5m;
            prediction.AmountWithinTypicalRange = scannedTotalAmount.Value >= lower && scannedTotalAmount.Value <= upper;
            if (!prediction.AmountWithinTypicalRange.Value)
                prediction.Reasons.Add(
                    $"⚠️ ยอด {scannedTotalAmount.Value:N2} นอกช่วง {intel.MinTotalAmount:N2}–{intel.MaxTotalAmount:N2}");
        }

        if (isSystemFallback)
        {
            prediction.Reasons.Add("ℹ️ ข้อมูลจากระบบกลาง — บริษัทยังไม่มีประวัติกับผู้ขายรายนี้");
        }

        // Surface learned patterns so OcrService can use them downstream
        prediction.TypicalDocNumberPrefix = intel.TypicalDocNumberPrefix;
        if (!string.IsNullOrEmpty(intel.TopLineKeywordsJson))
        {
            try
            {
                prediction.TopLineKeywords = JsonSerializer.Deserialize<Dictionary<string, int>>(intel.TopLineKeywordsJson);
            }
            catch { /* malformed JSON — leave null */ }
        }

        return prediction;
    }

    // ─────────────────────────────────────────────────────────────────
    // TRAIN — called when a Document is approved
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Direct training from admin UI — seed/update the per-vendor cache without
    /// requiring an approved Document. Used by the admin OCR console to teach
    /// "vendor X usually issues PurchaseInvoice with WHT 3% booked to 5402"
    /// before any real documents exist.
    ///
    /// Differs from TrainFromDocumentAsync:
    ///   • No document watermark (admin can re-call to update; not idempotent
    ///     per-document — it's per-vendor)
    ///   • Increments counts by the supplied weight so admin can express
    ///     confidence (default 1 = "I've seen this once")
    ///   • Bypasses Status/PurchaseSideType filters — admin knows what they
    ///     want to teach
    /// </summary>
    public async Task TrainFromAdminAsync(
        Guid companyId,
        string? vendorTaxId,
        string? vendorName,
        Models.Enums.DocumentType? documentType,
        string? debitAccountCode,
        string? debitAccountName,
        decimal? whtRate,
        int? paymentTermsDays,
        int weight = 1)
    {
        var key = NormalizeVendorKey(vendorTaxId, vendorName);
        if (string.IsNullOrEmpty(key) || weight < 1) return;

        var intel = await _db.OcrVendorIntelligence
            .FirstOrDefaultAsync(v => v.CompanyId == companyId && v.VendorKey == key && !v.IsDeleted);

        if (intel == null)
        {
            intel = new OcrVendorIntelligence
            {
                CompanyId = companyId,
                VendorKey = key,
                VendorName = vendorName,
                VendorTaxId = vendorTaxId,
                CreatedBy = "admin-training",
            };
            _db.OcrVendorIntelligence.Add(intel);
        }

        // ─── DocumentType ───
        if (documentType.HasValue)
        {
            var dtBreakdown = ParseBreakdown(intel.DocumentTypeBreakdownJson);
            var dtKey = documentType.Value.ToString();
            dtBreakdown[dtKey] = dtBreakdown.GetValueOrDefault(dtKey) + weight;
            intel.DocumentTypeBreakdownJson = JsonSerializer.Serialize(dtBreakdown);
            var topDt = dtBreakdown.OrderByDescending(kv => kv.Value).First();
            intel.MostCommonDocumentType = topDt.Key;
            intel.MostCommonDocumentTypeCount = topDt.Value;
        }

        // ─── Debit account ───
        if (!string.IsNullOrEmpty(debitAccountCode))
        {
            var debitBreakdown = ParseBreakdown(intel.DebitAccountBreakdownJson);
            debitBreakdown[debitAccountCode] = debitBreakdown.GetValueOrDefault(debitAccountCode) + weight;
            intel.DebitAccountBreakdownJson = JsonSerializer.Serialize(debitBreakdown);
            var topAcc = debitBreakdown.OrderByDescending(kv => kv.Value).First();
            intel.MostCommonDebitAccountCode = topAcc.Key;
            intel.MostCommonDebitAccountCount = topAcc.Value;
            if (topAcc.Key == debitAccountCode && !string.IsNullOrEmpty(debitAccountName))
                intel.MostCommonDebitAccountName = debitAccountName;
        }

        // ─── WHT ───
        if (whtRate.HasValue && whtRate.Value > 0)
        {
            intel.WhtUsageCount += weight;
            if (whtRate.Value is 1m or 2m or 3m or 5m or 10m or 15m)
                intel.TypicalWhtRate = whtRate;
        }

        intel.TotalDocuments += weight;
        intel.TypicallyHasWht = intel.TotalDocuments > 0 && (decimal)intel.WhtUsageCount / intel.TotalDocuments >= 0.5m;

        if (paymentTermsDays.HasValue && paymentTermsDays.Value > 0)
            intel.TypicalPaymentTermsDays = paymentTermsDays;

        intel.LastTrainedAt = DateTime.UtcNow;
        intel.UpdatedAt = DateTime.UtcNow;
        intel.UpdatedBy = "admin-training";

        await _db.SaveChangesAsync();
        _logger.LogInformation("Admin trained vendor intelligence: company={C} vendor={V} weight={W}",
            companyId, key, weight);
    }

    /// <summary>
    /// SystemAdmin variant of TrainFromAdminAsync — writes to the system-wide
    /// SystemOcrVendorIntelligence table (no CompanyId). Used by /admin/ocr-config
    /// to seed a central knowledge base that every tenant falls back to when
    /// they have no prior history with a given vendor.
    /// </summary>
    public async Task TrainFromAdminSystemAsync(
        string? vendorTaxId,
        string? vendorName,
        Models.Enums.DocumentType? documentType,
        string? debitAccountCode,
        string? debitAccountName,
        decimal? whtRate,
        int? paymentTermsDays,
        int weight = 1)
    {
        var key = NormalizeVendorKey(vendorTaxId, vendorName);
        if (string.IsNullOrEmpty(key) || weight < 1) return;

        var intel = await _db.SystemOcrVendorIntelligence
            .FirstOrDefaultAsync(v => v.VendorKey == key && !v.IsDeleted);

        if (intel == null)
        {
            intel = new SystemOcrVendorIntelligence
            {
                VendorKey = key,
                VendorName = vendorName,
                VendorTaxId = vendorTaxId,
                CreatedBy = "system-admin-training",
            };
            _db.SystemOcrVendorIntelligence.Add(intel);
        }

        if (documentType.HasValue)
        {
            var dtBreakdown = ParseBreakdown(intel.DocumentTypeBreakdownJson);
            var dtKey = documentType.Value.ToString();
            dtBreakdown[dtKey] = dtBreakdown.GetValueOrDefault(dtKey) + weight;
            intel.DocumentTypeBreakdownJson = JsonSerializer.Serialize(dtBreakdown);
            var topDt = dtBreakdown.OrderByDescending(kv => kv.Value).First();
            intel.MostCommonDocumentType = topDt.Key;
            intel.MostCommonDocumentTypeCount = topDt.Value;
        }

        if (!string.IsNullOrEmpty(debitAccountCode))
        {
            var debitBreakdown = ParseBreakdown(intel.DebitAccountBreakdownJson);
            debitBreakdown[debitAccountCode] = debitBreakdown.GetValueOrDefault(debitAccountCode) + weight;
            intel.DebitAccountBreakdownJson = JsonSerializer.Serialize(debitBreakdown);
            var topAcc = debitBreakdown.OrderByDescending(kv => kv.Value).First();
            intel.MostCommonDebitAccountCode = topAcc.Key;
            intel.MostCommonDebitAccountCount = topAcc.Value;
            if (topAcc.Key == debitAccountCode && !string.IsNullOrEmpty(debitAccountName))
                intel.MostCommonDebitAccountName = debitAccountName;
        }

        if (whtRate.HasValue && whtRate.Value > 0)
        {
            intel.WhtUsageCount += weight;
            if (whtRate.Value is 1m or 2m or 3m or 5m or 10m or 15m)
                intel.TypicalWhtRate = whtRate;
        }

        intel.TotalDocuments += weight;
        intel.TypicallyHasWht = intel.TotalDocuments > 0 && (decimal)intel.WhtUsageCount / intel.TotalDocuments >= 0.5m;

        if (paymentTermsDays.HasValue && paymentTermsDays.Value > 0)
            intel.TypicalPaymentTermsDays = paymentTermsDays;

        intel.LastTrainedAt = DateTime.UtcNow;
        intel.UpdatedAt = DateTime.UtcNow;
        intel.UpdatedBy = "system-admin-training";

        await _db.SaveChangesAsync();
        _logger.LogInformation("SystemAdmin trained SYSTEM vendor intelligence: vendor={V} weight={W}", key, weight);
    }

    /// <summary>
    /// Best-effort training wrapper used by all approval paths (DocumentService,
    /// SignatureApprovalService, IntegrationService, ECommerceService, MobileApi).
    /// Catches and logs any exception — vendor intelligence is a derived cache
    /// and must never block document approval. On failure, the data can be
    /// recovered via BackfillFromHistoryAsync.
    /// </summary>
    public async Task TryTrainAsync(Guid companyId, Guid documentId)
    {
        try
        {
            await TrainFromDocumentAsync(companyId, documentId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vendor intelligence training failed for document {DocId}", documentId);
        }
    }

    /// <summary>
    /// Update vendor intelligence from a single approved document. Idempotent
    /// per DocumentId — re-approval (Draft → Approved → Rejected → Draft → Approved)
    /// will only train once, tracked via the document's IsTrainedToVendorIntel flag.
    /// Wrapped in a retry on unique-index conflict to handle parallel approvals
    /// for the same vendor.
    /// </summary>
    public Task TrainFromDocumentAsync(Guid companyId, Guid documentId)
        => TrainFromDocumentAsync(companyId, documentId, retryCount: 0);

    private async Task TrainFromDocumentAsync(Guid companyId, Guid documentId, int retryCount)
    {
        var doc = await _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted);
        if (doc == null) return;

        // M3: Idempotent — never train the same document twice. Tracked via the
        // OcrIntelTrainedAt column on Document; if set, this doc has already been
        // counted (re-approval after rejection won't double-count).
        if (doc.OcrIntelTrainedAt.HasValue) return;

        // M7: Only learn from successfully-approved documents. Drafts can change.
        // Voided docs should not be trained from.
        if (doc.Status != DocumentStatus.Approved && doc.Status != DocumentStatus.Paid) return;

        // Only learn from documents where vendor info is meaningful
        var vendorTaxId = doc.Contact?.TaxId;
        var vendorName = doc.Contact?.Name;
        var key = NormalizeVendorKey(vendorTaxId, vendorName);
        if (string.IsNullOrEmpty(key)) return;

        // Only learn from purchase/expense-side docs (where we predict for OCR)
        if (!PurchaseSideTypes.Contains(doc.DocumentType)) return;

        // M2: Filter soft-deleted intel rows so we don't resurrect them
        var intel = await _db.OcrVendorIntelligence
            .FirstOrDefaultAsync(v => v.CompanyId == companyId && v.VendorKey == key && !v.IsDeleted);

        if (intel == null)
        {
            intel = new OcrVendorIntelligence
            {
                CompanyId = companyId,
                VendorKey = key,
                VendorName = vendorName,
                VendorTaxId = vendorTaxId,
                CreatedBy = "training",
            };
            _db.OcrVendorIntelligence.Add(intel);
        }

        // Apply this single document's contribution to the running aggregates.
        // Rather than rebuilding from scratch every time (slow on large companies),
        // we increment counters in-place and re-derive "most common" from the JSON breakdown.

        // ─── DocumentType ───
        var dtBreakdown = ParseBreakdown(intel.DocumentTypeBreakdownJson);
        var dtKey = doc.DocumentType.ToString();
        dtBreakdown[dtKey] = dtBreakdown.GetValueOrDefault(dtKey) + 1;
        intel.DocumentTypeBreakdownJson = JsonSerializer.Serialize(dtBreakdown);
        var topDt = dtBreakdown.OrderByDescending(kv => kv.Value).First();
        intel.MostCommonDocumentType = topDt.Key;
        intel.MostCommonDocumentTypeCount = topDt.Value;

        // ─── Debit account (count ONE dominant account per document) ───
        // Pick the account that appears in the most lines (or the largest amount
        // when tied). This keeps the breakdown count consistent with TotalDocuments
        // so ratios never exceed 100%, and handles multi-line invoices that book
        // mostly to one account with a couple of small fees.
        Guid? dominantAccountId = doc.Lines
            .Where(l => l.AccountId.HasValue)
            .GroupBy(l => l.AccountId!.Value)
            .OrderByDescending(g => g.Sum(l => l.Amount))
            .Select(g => (Guid?)g.Key)
            .FirstOrDefault() ?? doc.ExpenseCategoryId;

        if (dominantAccountId.HasValue)
        {
            var account = await _db.ChartOfAccounts
                .Where(a => a.Id == dominantAccountId.Value && a.CompanyId == companyId)
                .Select(a => new { a.AccountCode, a.AccountName })
                .FirstOrDefaultAsync();
            if (account != null)
            {
                var debitBreakdown = ParseBreakdown(intel.DebitAccountBreakdownJson);
                debitBreakdown[account.AccountCode] = debitBreakdown.GetValueOrDefault(account.AccountCode) + 1;
                intel.DebitAccountBreakdownJson = JsonSerializer.Serialize(debitBreakdown);
                var topAcc = debitBreakdown.OrderByDescending(kv => kv.Value).First();
                intel.MostCommonDebitAccountCode = topAcc.Key;
                intel.MostCommonDebitAccountCount = topAcc.Value;
                // Always refresh name to the latest account label (in case CoA was renamed)
                if (topAcc.Key == account.AccountCode)
                    intel.MostCommonDebitAccountName = account.AccountName;
            }
        }

        // ─── WHT ───
        if (doc.WithholdingTaxAmount > 0)
        {
            intel.WhtUsageCount++;
            // Approximate the rate from the document
            var whtRate = doc.SubTotal > 0 ? Math.Round(doc.WithholdingTaxAmount / doc.SubTotal * 100m, 0) : 0m;
            if (whtRate is 1 or 2 or 3 or 5 or 10 or 15)
                intel.TypicalWhtRate = whtRate;
        }

        // ─── Amount stats (running stats) ───
        intel.TotalDocuments++;
        var n = intel.TotalDocuments;
        var prev = intel.AvgTotalAmount ?? 0;
        intel.AvgTotalAmount = prev + (doc.TotalAmount - prev) / n;
        intel.MinTotalAmount = intel.MinTotalAmount.HasValue
            ? Math.Min(intel.MinTotalAmount.Value, doc.TotalAmount) : doc.TotalAmount;
        intel.MaxTotalAmount = intel.MaxTotalAmount.HasValue
            ? Math.Max(intel.MaxTotalAmount.Value, doc.TotalAmount) : doc.TotalAmount;

        // ─── Log-amount running mean/variance via Welford's algorithm ───
        // Used by AmountAnomalyDetector.CheckZScore — the log transform
        // turns the heavy-tailed amount distribution into something close
        // to Gaussian where z-scores are meaningful.
        if (doc.TotalAmount > 0)
        {
            var logAmt = (decimal)Math.Log((double)doc.TotalAmount);
            var (newMean, newVar) = AmountAnomalyDetector.UpdateWelford(
                intel.LogAmountMean ?? 0,
                intel.LogAmountVariance ?? 0,
                n - 1,
                logAmt);
            intel.LogAmountMean = newMean;
            intel.LogAmountVariance = newVar;
        }

        intel.TypicallyHasWht = (decimal)intel.WhtUsageCount / n >= 0.5m;
        intel.LastTrainedAt = DateTime.UtcNow;
        intel.LastDocumentDate = doc.DocumentDate;
        intel.UpdatedBy = "training";
        intel.UpdatedAt = DateTime.UtcNow;

        // ─── Payment terms ───
        if (doc.DueDate.HasValue)
        {
            var days = (int)(doc.DueDate.Value - doc.DocumentDate).TotalDays;
            if (days >= 0 && days <= 365)
                intel.TypicalPaymentTermsDays = days;
        }

        // ─── Learned doc-number prefix ───
        // Keep the longest digit-friendly prefix shared with prior docs.
        // When a vendor consistently issues numbers like "612724 / 612823 /
        // 613145" we end up with prefix "61" which lets the OCR boost a new
        // candidate that also starts with 61.
        if (!string.IsNullOrEmpty(doc.Reference))
        {
            var current = doc.Reference.Trim();
            if (string.IsNullOrEmpty(intel.TypicalDocNumberPrefix))
                intel.TypicalDocNumberPrefix = current;
            else
                intel.TypicalDocNumberPrefix = CommonPrefix(intel.TypicalDocNumberPrefix, current);
        }

        // ─── Learned line-item keywords (top 30 by count) ───
        // Used by the category resolver / future smart-extract pass to
        // boost confidence when a new scan's description matches one we've
        // seen on prior approved docs from this vendor.
        if (doc.Lines.Count > 0)
        {
            var kwMap = ParseBreakdown(intel.TopLineKeywordsJson);
            foreach (var line in doc.Lines)
            {
                foreach (var tok in TokenizeForLearning(line.Description))
                    kwMap[tok] = kwMap.GetValueOrDefault(tok) + 1;
            }
            // Keep top 30 — the rest is noise and bloats the JSON column
            var top = kwMap.OrderByDescending(kv => kv.Value).Take(30).ToDictionary(kv => kv.Key, kv => kv.Value);
            intel.TopLineKeywordsJson = JsonSerializer.Serialize(top);
        }

        // M3: Mark the document as trained — prevents double-counting on re-approval
        doc.OcrIntelTrainedAt = DateTime.UtcNow;

        // M4: Handle the race where two parallel approvals for the same vendor both
        // see "no existing intel row" and both INSERT, violating the unique
        // (CompanyId, VendorKey) index. On first conflict, reload + retry once.
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex) && retryCount < 2)
        {
            _logger.LogInformation("Concurrent training conflict for vendor {V}; retrying", key);
            // Discard our pending Add — the failed SaveChanges already rolled back
            // doc.OcrIntelTrainedAt in the DB, so the recursive call's early-return
            // guard won't trip. The retry will find the row inserted by the winning
            // thread and do an UPDATE instead of an INSERT.
            _db.ChangeTracker.Clear();
            await TrainFromDocumentAsync(companyId, documentId, retryCount + 1);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        // Postgres unique-violation SQLSTATE = 23505. Use the typed PostgresException
        // when available (Npgsql is a transitive dep) — falls back to type-name check
        // for forward compatibility.
        if (ex.InnerException is Npgsql.PostgresException pg)
            return pg.SqlState == "23505";
        return ex.InnerException?.GetType().Name == "PostgresException"
            && ex.InnerException.Message.Contains("23505");
    }

    // ─────────────────────────────────────────────────────────────────
    // BACKFILL — one-shot rebuild from existing Documents
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuild OcrVendorIntelligence cache from scratch by replaying the last
    /// N months of Documents. Use after deploy to bootstrap the cache for
    /// companies that already have document history.
    /// </summary>
    public async Task<int> BackfillFromHistoryAsync(Guid companyId, int sinceMonths = 24)
    {
        var since = DateTime.UtcNow.AddMonths(-sinceMonths);

        // C5: Wrap the destructive wipe + rebuild in a single transaction with
        // execution strategy. If the rebuild throws, the wipe is rolled back so
        // we never leave the company in a "no intel rows at all" state.
        var strategy = _db.Database.CreateExecutionStrategy();
        var trained = 0;

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync();
            trained = await BackfillCoreAsync(companyId, since);
            await tx.CommitAsync();
        });

        _logger.LogInformation("Backfilled vendor intelligence for company={C}: {N} vendors", companyId, trained);
        return trained;
    }

    private async Task<int> BackfillCoreAsync(Guid companyId, DateTime since)
    {
        // Wipe existing cache for this company so the rebuild is deterministic.
        // Inside transaction: if rebuild throws, the wipe is rolled back.
        var existing = await _db.OcrVendorIntelligence
            .Where(v => v.CompanyId == companyId).ToListAsync();
        _db.OcrVendorIntelligence.RemoveRange(existing);

        // Also clear the per-document training watermark — backfill is a clean
        // rebuild, so subsequent re-approvals shouldn't see "already trained".
        // We re-mark each doc as we count it below.
        await _db.Documents
            .Where(d => d.CompanyId == companyId && d.OcrIntelTrainedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.OcrIntelTrainedAt, (DateTime?)null));

        await _db.SaveChangesAsync();

        // C4: Use the unified PurchaseSideTypes array — same set as TrainFromDocumentAsync
        // M7: Filter to approved-only (consistent with incremental training)
        // M6: Include ExpenseCategory so the header-level account fallback works
        var docs = await _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines).ThenInclude(l => l.Account)
            .Include(d => d.ExpenseCategory)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && PurchaseSideTypes.Contains(d.DocumentType)
                && (d.Status == DocumentStatus.Approved || d.Status == DocumentStatus.Paid)
                && d.DocumentDate >= since)
            .OrderBy(d => d.DocumentDate)
            .ToListAsync();

        var trained = 0;
        // Group by vendor key and aggregate in memory before bulk insert — much
        // faster than calling TrainFromDocumentAsync per-doc (which does a roundtrip each).
        var groups = docs
            .Select(d => new { Doc = d, Key = NormalizeVendorKey(d.Contact?.TaxId, d.Contact?.Name) })
            .Where(x => !string.IsNullOrEmpty(x.Key))
            .GroupBy(x => x.Key);

        foreach (var grp in groups)
        {
            var sample = grp.First().Doc;
            var intel = new OcrVendorIntelligence
            {
                CompanyId = companyId,
                VendorKey = grp.Key,
                VendorName = sample.Contact?.Name,
                VendorTaxId = sample.Contact?.TaxId,
                CreatedBy = "backfill",
                LastTrainedAt = DateTime.UtcNow,
            };

            var dtBreakdown = new Dictionary<string, int>();
            var debitBreakdown = new Dictionary<string, int>();
            var debitNames = new Dictionary<string, string?>();
            var whtRates = new List<decimal>();
            var paymentTerms = new List<int>();
            var amounts = new List<decimal>();
            int whtCount = 0;

            foreach (var x in grp)
            {
                var d = x.Doc;
                var dtKey = d.DocumentType.ToString();
                dtBreakdown[dtKey] = dtBreakdown.GetValueOrDefault(dtKey) + 1;

                // Pick the dominant account for THIS document (by total amount), then
                // increment its bucket once — keeps ratios bounded by TotalDocuments.
                // M6: Falls back to header ExpenseCategoryId when no lines have an
                // account assigned — matches TrainFromDocumentAsync behavior.
                ChartOfAccount? dominantLine = d.Lines
                    .Where(l => l.Account != null)
                    .GroupBy(l => l.AccountId!.Value)
                    .OrderByDescending(g => g.Sum(l => l.Amount))
                    .Select(g => g.First().Account)
                    .FirstOrDefault();
                if (dominantLine == null && d.ExpenseCategoryId.HasValue)
                {
                    // Header-level fallback — find the account from CoA
                    dominantLine = d.ExpenseCategory;  // Eager-loaded if available
                }
                if (dominantLine != null)
                {
                    var code = dominantLine.AccountCode;
                    debitBreakdown[code] = debitBreakdown.GetValueOrDefault(code) + 1;
                    debitNames[code] = dominantLine.AccountName;
                }

                if (d.WithholdingTaxAmount > 0 && d.SubTotal > 0)
                {
                    whtCount++;
                    var rate = Math.Round(d.WithholdingTaxAmount / d.SubTotal * 100m, 0);
                    if (rate is 1 or 2 or 3 or 5 or 10 or 15) whtRates.Add(rate);
                }

                if (d.DueDate.HasValue)
                {
                    var days = (int)(d.DueDate.Value - d.DocumentDate).TotalDays;
                    if (days >= 0 && days <= 365) paymentTerms.Add(days);
                }

                if (d.TotalAmount > 0) amounts.Add(d.TotalAmount);
                if (intel.LastDocumentDate == null || d.DocumentDate > intel.LastDocumentDate)
                    intel.LastDocumentDate = d.DocumentDate;
            }

            intel.TotalDocuments = grp.Count();
            intel.DocumentTypeBreakdownJson = JsonSerializer.Serialize(dtBreakdown);
            var topDt = dtBreakdown.OrderByDescending(kv => kv.Value).First();
            intel.MostCommonDocumentType = topDt.Key;
            intel.MostCommonDocumentTypeCount = topDt.Value;

            if (debitBreakdown.Any())
            {
                intel.DebitAccountBreakdownJson = JsonSerializer.Serialize(debitBreakdown);
                var topAcc = debitBreakdown.OrderByDescending(kv => kv.Value).First();
                intel.MostCommonDebitAccountCode = topAcc.Key;
                intel.MostCommonDebitAccountCount = topAcc.Value;
                intel.MostCommonDebitAccountName = debitNames.GetValueOrDefault(topAcc.Key);
            }

            intel.WhtUsageCount = whtCount;
            intel.TypicallyHasWht = (decimal)whtCount / intel.TotalDocuments >= 0.5m;
            if (whtRates.Any())
                intel.TypicalWhtRate = whtRates.GroupBy(r => r).OrderByDescending(g => g.Count()).First().Key;

            if (paymentTerms.Any())
                intel.TypicalPaymentTermsDays = (int)paymentTerms.GroupBy(d => d).OrderByDescending(g => g.Count()).First().Key;

            if (amounts.Any())
            {
                intel.AvgTotalAmount = amounts.Average();
                intel.MinTotalAmount = amounts.Min();
                intel.MaxTotalAmount = amounts.Max();
                var sorted = amounts.OrderBy(a => a).ToList();
                intel.MedianTotalAmount = sorted[sorted.Count / 2];
            }

            _db.OcrVendorIntelligence.Add(intel);
            trained++;

            // Mark every counted document with the training watermark so future
            // TrainFromDocumentAsync calls (incremental approvals) short-circuit
            // on these docs and don't double-count.
            var trainedAt = DateTime.UtcNow;
            foreach (var x in grp)
                x.Doc.OcrIntelTrainedAt = trainedAt;
        }

        await _db.SaveChangesAsync();
        return trained;
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static string NormalizeVendorKey(string? taxId, string? name)
    {
        if (!string.IsNullOrEmpty(taxId))
        {
            var digits = new string(taxId.Where(char.IsDigit).ToArray());
            if (digits.Length == 13) return $"tax:{digits}";
        }
        if (!string.IsNullOrEmpty(name))
            return $"name:{name.Trim().ToLowerInvariant()}";
        return "";
    }

    private static Dictionary<string, int> ParseBreakdown(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Common prefix of two strings — used to converge on a
    /// vendor's typical document-number pattern across many docs.</summary>
    private static string CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < n && a[i] == b[i]) i++;
        // Trim trailing punctuation so the stored prefix is matchable
        var prefix = a.Substring(0, i).TrimEnd('-', '/', '_', ' ', '.');
        return prefix;
    }

    /// <summary>Tokenize a line-item description for keyword learning.
    /// Same shape as ExpenseCategoryLearner.ExtractTokens but lives here to
    /// avoid a cross-file private dependency. Drops pure-digit tokens and
    /// stopwords; lower-cases everything.</summary>
    private static IEnumerable<string> TokenizeForLearning(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) yield break;
        var raw = description.ToLowerInvariant()
            .Replace(",", " ").Replace(".", " ").Replace("-", " ")
            .Replace("(", " ").Replace(")", " ").Replace("/", " ")
            .Replace(":", " ").Replace(";", " ");
        foreach (var part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 2) continue;
            if (part.All(char.IsDigit)) continue;
            yield return part;
        }
    }

    private static string ThaiDocLabel(DocumentType t) => t switch
    {
        DocumentType.PurchaseInvoice => "ใบแจ้งหนี้ซื้อ",
        DocumentType.Expense => "ใบบันทึกค่าใช้จ่าย",
        DocumentType.CertificateInLieu => "ใบรับรองแทนใบเสร็จ",
        DocumentType.PurchaseOrder => "ใบสั่งซื้อ",
        DocumentType.Invoice => "ใบแจ้งหนี้",
        DocumentType.TaxInvoice => "ใบกำกับภาษี",
        DocumentType.Receipt => "ใบเสร็จ",
        DocumentType.CreditNote => "ใบลดหนี้",
        DocumentType.DebitNote => "ใบเพิ่มหนี้",
        _ => t.ToString()
    };
}

/// <summary>
/// Output of VendorIntelligenceService.PredictAsync — consumed by OcrService.ScanAsync
/// to override / augment AI predictions.
/// </summary>
public class VendorPrediction
{
    public bool HasHistory { get; set; }
    public int SampleSize { get; set; }

    public DocumentType? DocumentType { get; set; }
    public decimal DocumentTypeConfidence { get; set; }

    public string? DebitAccountCode { get; set; }
    public string? DebitAccountName { get; set; }
    public decimal DebitAccountConfidence { get; set; }

    public bool? HasWht { get; set; }
    public decimal? WhtRate { get; set; }
    public decimal WhtConfidence { get; set; }

    public int? TypicalPaymentTermsDays { get; set; }

    public bool? AmountWithinTypicalRange { get; set; }

    // Learned patterns surfaced for OCR boosting:
    //   • TypicalDocNumberPrefix lets the smart extractor prefer a candidate
    //     starting with the same characters this vendor has used in past docs.
    //   • TopLineKeywords lets the category resolver give extra weight to
    //     keywords that occur frequently on this vendor's past invoices.
    public string? TypicalDocNumberPrefix { get; set; }
    public Dictionary<string, int>? TopLineKeywords { get; set; }

    public List<string> Reasons { get; set; } = new();
}
