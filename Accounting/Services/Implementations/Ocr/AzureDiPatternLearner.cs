using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Free training signal: every time Azure DI succeeds we treat its output
/// as ground truth for the document at hand and feed it back into two
/// stores the local-OCR cascade consults:
///
///   1. <c>OcrLearnedPattern</c> — populated via the same
///      <see cref="DocumentZoneAnalyzer.LearnFromCorrection"/> path that
///      user corrections use. Records WHERE each field appears in the
///      raw text (anchor keyword + search radius) so a later Tesseract
///      scan of the SAME vendor can find the field even when its noisy
///      text would otherwise miss it.
///
///   2. <c>VendorKnownGoodValue</c> — stores the canonical value itself.
///      The local-cascade post-processor (VendorKnownGoodCorrector) then
///      fuzzy-matches its noisy output and substitutes the canonical
///      version when similarity ≥ 0.80.
///
/// Effect: after Azure DI runs on a vendor once, every subsequent
/// fallback scan of that vendor (quota exhausted / Azure unreachable)
/// gets the benefit of Azure-grade accuracy on the names and numbers
/// it has already seen. Pure Python-Tesseract scans don't degrade —
/// they get smarter every time a customer pays for an Azure scan.
/// </summary>
public class AzureDiPatternLearner
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<AzureDiPatternLearner> _logger;

    public AzureDiPatternLearner(AccountingDbContext db, ILogger<AzureDiPatternLearner> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Persist Azure-DI-extracted values as both location patterns
    /// (OcrLearnedPattern) and canonical-value entries
    /// (VendorKnownGoodValue) for the given vendor. Idempotent — repeated
    /// calls bump confirmation counts instead of inserting duplicates.
    /// </summary>
    public async Task LearnAsync(
        Guid companyId,
        string rawText,
        string? vendorTaxId,
        string? vendorName,
        string? documentNumber,
        string? buyerName,
        string? buyerTaxId,
        string? vendorAddress,
        string? vendorPhone,
        string? vendorEmail,
        string? vendorBranchCode,
        decimal sourceConfidence,
        CancellationToken ct = default)
    {
        // 1. Location patterns — reuse the existing learning path so
        //    DocumentZoneAnalyzer.ApplyLearnedPatterns picks them up
        //    on the next Tier-2/3 scan with no further wiring.
        if (!string.IsNullOrEmpty(rawText))
        {
            var patterns = DocumentZoneAnalyzer.LearnFromCorrection(
                rawText, companyId, vendorTaxId,
                correctedVendorName: vendorName,
                correctedTaxId: vendorTaxId,
                correctedDocNumber: documentNumber,
                correctedTotal: null);
            foreach (var p in patterns)
            {
                p.CreatedBy = "AzureDI-Learn";
                await UpsertPatternAsync(p, ct);
            }
        }

        // 2. Canonical values — stored verbatim so the local cascade can
        //    fuzzy-correct its own output back to Azure's spelling.
        await UpsertKnownGoodAsync(companyId, vendorTaxId, "SellerName", vendorName, sourceConfidence, ct);
        await UpsertKnownGoodAsync(companyId, vendorTaxId, "SellerTaxId", vendorTaxId, sourceConfidence, ct);
        await UpsertKnownGoodAsync(companyId, vendorTaxId, "DocumentNumber", documentNumber, sourceConfidence, ct);
        await UpsertKnownGoodAsync(companyId, vendorTaxId, "BuyerName", buyerName, sourceConfidence, ct);
        await UpsertKnownGoodAsync(companyId, vendorTaxId, "BuyerTaxId", buyerTaxId, sourceConfidence, ct);
        await UpsertKnownGoodAsync(companyId, vendorTaxId, "VendorAddress", vendorAddress, sourceConfidence, ct);
        await UpsertKnownGoodAsync(companyId, vendorTaxId, "VendorPhone", vendorPhone, sourceConfidence, ct);
        await UpsertKnownGoodAsync(companyId, vendorTaxId, "VendorEmail", vendorEmail, sourceConfidence, ct);
        await UpsertKnownGoodAsync(companyId, vendorTaxId, "VendorBranchCode", vendorBranchCode, sourceConfidence, ct);

        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("AzureDI learner: vendor {Vendor} for company {Cid} — patterns + known-good values upserted",
            vendorTaxId ?? vendorName ?? "?", companyId);
    }

    private async Task UpsertPatternAsync(OcrLearnedPattern p, CancellationToken ct)
    {
        // Match on (companyId, vendorTaxId, fieldName, contextKeyword) so
        // repeated Azure scans of the same vendor reinforce the pattern
        // rather than spawning duplicates.
        var existing = await _db.OcrLearnedPatterns
            .Where(x => x.CompanyId == p.CompanyId
                && x.VendorTaxId == p.VendorTaxId
                && x.FieldName == p.FieldName
                && x.ContextKeyword == p.ContextKeyword
                && x.IsNegativeExample == p.IsNegativeExample
                && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (existing != null)
        {
            existing.TimesConfirmed += 1;
            existing.LastConfirmedAt = DateTime.UtcNow;
            existing.UpdatedBy = "AzureDI-Learn";
        }
        else
        {
            _db.OcrLearnedPatterns.Add(p);
        }
    }

    private async Task UpsertKnownGoodAsync(
        Guid companyId, string? vendorTaxId, string fieldName, string? value,
        decimal confidence, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // Without a TaxId we can't reliably scope the value to a vendor —
        // skip. The local cascade looks up by TaxId; an entry without
        // one would be effectively unreachable anyway.
        if (string.IsNullOrWhiteSpace(vendorTaxId) && fieldName != "SellerTaxId") return;

        var v = value.Trim();
        if (v.Length == 0) return;

        var existing = await _db.VendorKnownGoodValues
            .Where(x => x.CompanyId == companyId
                && x.VendorTaxId == vendorTaxId
                && x.FieldName == fieldName
                && x.Value == v
                && !x.IsDeleted)
            .FirstOrDefaultAsync(ct);
        if (existing != null)
        {
            existing.ConfirmedCount += 1;
            existing.LastSeenAt = DateTime.UtcNow;
            // User corrections trump Azure on conflict — never demote.
            if (existing.Source != "UserCorrection")
                existing.Confidence = Math.Max(existing.Confidence, confidence);
            existing.UpdatedBy = "AzureDI-Learn";
        }
        else
        {
            _db.VendorKnownGoodValues.Add(new VendorKnownGoodValue
            {
                CompanyId = companyId,
                VendorTaxId = vendorTaxId,
                FieldName = fieldName,
                Value = v,
                Confidence = confidence,
                ConfirmedCount = 1,
                Source = "AzureDI",
                LastSeenAt = DateTime.UtcNow,
                CreatedBy = "AzureDI-Learn",
            });
        }
    }
}
