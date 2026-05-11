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
        if (intel == null || intel.TotalDocuments == 0) return prediction;

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
        if (intel.WhtUsageCount > 0)
        {
            var whtPct = (decimal)intel.WhtUsageCount / intel.TotalDocuments;
            prediction.HasWht = whtPct >= 0.5m;
            prediction.WhtConfidence = Math.Min(whtPct, 1m - whtPct) > 0.2m ? whtPct : Math.Max(whtPct, 1m - whtPct);
            if (prediction.HasWht == true && intel.TypicalWhtRate.HasValue)
            {
                prediction.WhtRate = intel.TypicalWhtRate;
                prediction.Reasons.Add($"ผู้ขายรายนี้มักหักภาษี ณ ที่จ่าย {intel.TypicalWhtRate}% ({intel.WhtUsageCount}/{intel.TotalDocuments} ครั้ง)");
            }
        }

        // ─── Payment terms ───
        if (intel.TypicalPaymentTermsDays.HasValue)
            prediction.TypicalPaymentTermsDays = intel.TypicalPaymentTermsDays;

        // ─── Amount sanity check ───
        if (scannedTotalAmount.HasValue && scannedTotalAmount.Value > 0
            && intel.MinTotalAmount.HasValue && intel.MaxTotalAmount.HasValue && intel.AvgTotalAmount.HasValue)
        {
            // Allow ±50% of the historical range as "typical"
            var lower = intel.MinTotalAmount.Value * 0.5m;
            var upper = intel.MaxTotalAmount.Value * 1.5m;
            prediction.AmountWithinTypicalRange = scannedTotalAmount.Value >= lower && scannedTotalAmount.Value <= upper;
            if (!prediction.AmountWithinTypicalRange.Value)
            {
                prediction.Reasons.Add(
                    $"⚠️ ยอดเงิน {scannedTotalAmount.Value:N2} ผิดปกติ (ปกติอยู่ระหว่าง {intel.MinTotalAmount:N2}–{intel.MaxTotalAmount:N2}, เฉลี่ย {intel.AvgTotalAmount:N2}) — โปรดตรวจสอบ");
            }
        }

        return prediction;
    }

    // ─────────────────────────────────────────────────────────────────
    // TRAIN — called when a Document is approved
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Update vendor intelligence from a single approved document. Idempotent:
    /// if called twice for the same DocumentId, the second call short-circuits
    /// (recorded via LastDocumentDate watermark + DocumentId check via the doc's UpdatedAt).
    /// Called from DocumentService.ApproveDocumentAsync after the journal posts.
    /// </summary>
    public async Task TrainFromDocumentAsync(Guid companyId, Guid documentId)
    {
        var doc = await _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted);
        if (doc == null) return;

        // Only learn from documents where vendor info is meaningful
        var vendorTaxId = doc.Contact?.TaxId;
        var vendorName = doc.Contact?.Name;
        var key = NormalizeVendorKey(vendorTaxId, vendorName);
        if (string.IsNullOrEmpty(key)) return;

        // Only learn from purchase/expense-side docs (where we predict for OCR)
        if (!IsPurchaseSideType(doc.DocumentType)) return;

        var intel = await _db.OcrVendorIntelligence
            .FirstOrDefaultAsync(v => v.CompanyId == companyId && v.VendorKey == key);

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

        await _db.SaveChangesAsync();
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
        var purchaseTypes = new[] {
            DocumentType.PurchaseInvoice, DocumentType.Expense,
            DocumentType.CertificateInLieu, DocumentType.PurchaseOrder
        };

        // Wipe existing cache for this company so the rebuild is deterministic
        var existing = await _db.OcrVendorIntelligence
            .Where(v => v.CompanyId == companyId).ToListAsync();
        _db.OcrVendorIntelligence.RemoveRange(existing);
        await _db.SaveChangesAsync();

        var docs = await _db.Documents
            .Include(d => d.Contact)
            .Include(d => d.Lines).ThenInclude(l => l.Account)
            .Where(d => d.CompanyId == companyId && !d.IsDeleted
                && purchaseTypes.Contains(d.DocumentType)
                && d.Status != DocumentStatus.Voided
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
                var dominantLine = d.Lines
                    .Where(l => l.Account != null)
                    .GroupBy(l => l.AccountId!.Value)
                    .OrderByDescending(g => g.Sum(l => l.Amount))
                    .Select(g => g.First().Account)
                    .FirstOrDefault();
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
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Backfilled vendor intelligence for company={C}: {N} vendors from {D} documents",
            companyId, trained, docs.Count);
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

    private static bool IsPurchaseSideType(DocumentType t) => t is
        DocumentType.PurchaseInvoice or DocumentType.Expense
        or DocumentType.CertificateInLieu or DocumentType.PurchaseOrder
        or DocumentType.PaymentVoucher;

    private static Dictionary<string, int> ParseBreakdown(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new(); }
        catch { return new(); }
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

    public List<string> Reasons { get; set; } = new();
}
