using Accounting.Data;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Post-OCR cleanup for the Tier-2/3 local cascade. After PaddleOCR or
/// Tesseract produces its extraction, we look up every known-good value
/// for the recognized vendor and fuzzy-match the extractor's output
/// against them. When similarity ≥ 0.80 we substitute the canonical
/// value — this is the "Azure-trained local OCR" effect: noise like
/// "หจก . แอมแฮปปี้เนสล" (Tesseract garbling) becomes "หจก. แอมแฮปปี๊เนส"
/// (Azure-extracted spelling).
///
/// VendorTaxId is the only join key — if the extractor couldn't even
/// produce a valid TaxId we skip correction entirely (we don't know
/// which vendor's known-goods to consult). The TaxId itself is also
/// correctable, but only when the extractor produced a 13-digit value
/// off by a single character; otherwise we'd over-correct.
/// </summary>
public class VendorKnownGoodCorrector
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<VendorKnownGoodCorrector> _logger;

    // Fuzzy threshold for accepting a substitution. 0.80 was tuned to
    // catch "หจก. แอมแฮปปี๊เนส" ↔ "หจก . แอมแฮปปี้เนสล" but reject
    // unrelated vendor names that share a common prefix.
    private const double SimilarityThreshold = 0.80;

    public VendorKnownGoodCorrector(AccountingDbContext db, ILogger<VendorKnownGoodCorrector> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Mutates <paramref name="data"/> in place — substitutes noisy field
    /// values with their canonical equivalents when the local cascade
    /// has previously learned them from Azure DI. Adds reasoning trace
    /// entries so the user can see why a value changed mid-pipeline.
    /// Internal because OcrExtractedData is internal to this assembly.
    /// </summary>
    internal async Task ApplyAsync(Guid companyId, OcrExtractedData data, CancellationToken ct = default)
    {
        if (data == null) return;
        // Need at least vendor TaxId or vendor name to find anything.
        var taxId = NormalizeTaxId(data.VendorTaxId);
        var nameNoise = data.VendorName?.Trim();
        if (string.IsNullOrEmpty(taxId) && string.IsNullOrEmpty(nameNoise)) return;

        // First — if extractor has no TaxId, try to recover it by fuzzy-
        // matching the noisy vendor name against any known-good SellerName
        // that maps to a TaxId. This is the "we knew this vendor before"
        // recovery path; with TaxId restored, downstream corrections can
        // proceed.
        if (string.IsNullOrEmpty(taxId) && !string.IsNullOrEmpty(nameNoise))
        {
            var byName = await _db.VendorKnownGoodValues
                .Where(v => v.CompanyId == companyId
                    && v.FieldName == "SellerName"
                    && v.VendorTaxId != null
                    && !v.IsDeleted)
                .Select(v => new { v.Value, v.VendorTaxId, v.ConfirmedCount })
                .ToListAsync(ct);
            var best = byName
                .Select(v => new { v, sim = FuzzyMatcher.Similarity(v.Value, nameNoise) })
                .Where(x => x.sim >= SimilarityThreshold)
                .OrderByDescending(x => x.sim).ThenByDescending(x => x.v.ConfirmedCount)
                .FirstOrDefault();
            if (best != null)
            {
                taxId = best.v.VendorTaxId;
                data.VendorTaxId = taxId;
                data.VendorName = best.v.Value;
                data.ReasoningTrace.Add(
                    $"[KnownGood] กู้คืน VendorTaxId '{taxId}' จากชื่อใกล้เคียง '{best.v.Value}' (sim {best.sim:P0})");
                // บันทึกที่มา (D1) — ค่ามาจากประวัติผู้ขาย ไม่ใช่จากกระดาษใบนี้
                data.Note(Accounting.Helpers.OcrFieldKeys.SellerTaxId, taxId,
                    Accounting.Helpers.OcrFieldSource.VendorHistory, (decimal)best.sim,
                    $"กู้จากชื่อใกล้เคียง '{best.v.Value}'");
                data.Note(Accounting.Helpers.OcrFieldKeys.SellerName, best.v.Value,
                    Accounting.Helpers.OcrFieldSource.VendorHistory, (decimal)best.sim,
                    $"known-good ยืนยันแล้ว {best.v.ConfirmedCount} ครั้ง");
            }
        }

        if (string.IsNullOrEmpty(taxId)) return;

        // Pull every known-good value for this vendor in one round trip.
        var known = await _db.VendorKnownGoodValues
            .Where(v => v.CompanyId == companyId
                && v.VendorTaxId == taxId
                && !v.IsDeleted)
            .Select(v => new { v.FieldName, v.Value, v.ConfirmedCount, v.Source })
            .ToListAsync(ct);
        if (known.Count == 0) return;

        // Helper: pick the highest-similarity (≥threshold) known-good for
        // the given field, preferring values seen many times and from
        // user corrections (which trump Azure).
        string? BestMatch(string field, string? noisyValue)
        {
            if (string.IsNullOrWhiteSpace(noisyValue)) return null;
            // ด่านกลาง: ช่องที่ไม่คงที่ต่อผู้ขาย หรือเป็นตัวเลขล้วน ห้ามแก้
            // (ดูเหตุผล + ตัวเลขจาก simulation ใน Helpers/VendorKnownGoodFields)
            if (!Accounting.Helpers.VendorKnownGoodFields.IsCorrectable(field)) return null;
            var candidates = known.Where(k => k.FieldName == field);
            return candidates
                .Select(k => new { k.Value, k.Source, k.ConfirmedCount,
                    sim = FuzzyMatcher.Similarity(k.Value, noisyValue) })
                .Where(x => x.sim >= SimilarityThreshold && x.Value != noisyValue)
                .OrderByDescending(x => x.Source == "UserCorrection")
                .ThenByDescending(x => x.sim)
                .ThenByDescending(x => x.ConfirmedCount)
                .Select(x => x.Value)
                .FirstOrDefault();
        }

        // Apply substitutions field-by-field. Each successful swap adds a
        // trace entry so the user can audit why their scan ended up with
        // a value different from what the OCR engine returned.
        var swaps = 0;
        var nameMatch = BestMatch("SellerName", data.VendorName);
        if (nameMatch != null)
        {
            data.ReasoningTrace.Add(
                $"[KnownGood] แทน VendorName '{data.VendorName}' → '{nameMatch}'");
            data.Note(Accounting.Helpers.OcrFieldKeys.SellerName, nameMatch,
                Accounting.Helpers.OcrFieldSource.VendorHistory, (decimal)SimilarityThreshold,
                $"แทนค่าที่ engine อ่านได้ '{data.VendorName}'");
            data.VendorName = nameMatch;
            swaps++;
        }

        // ⛔ **ห้ามแก้เลขที่เอกสาร / รหัสสาขา / เบอร์โทร / อีเมล** — เดิมทำอยู่
        // แล้วเป็นบั๊กจริง: เลขที่เอกสารเป็นค่า "ต่อใบ" ไม่ใช่ "ต่อผู้ขาย" ⇒
        // เลขรันติดกันต่างกันหลักเดียว similarity 0.83–0.92 **เกินเกณฑ์เสมอ**
        // ⇒ ใบใหม่ถือเลขของใบก่อนหน้า (รายงานภาษีซื้อ §87 ยื่นเลขผิด + ด่านกัน
        // สแกนซ้ำตีว่าเป็นใบเดิม) · รหัสสาขา 5 หลักที่ต่างกัน 1 ตัว = **0.80
        // พอดี** ⇒ ผ่านทุกคู่ ⇒ ใบของสาขาถูกเขียนเป็นสำนักงานใหญ่ · เบอร์/อีเมล
        // เป็นตัวเลข/สตริงที่ "ไม่มีการสะกดผิด" — เดาไม่ได้ ต้องถูกหรือไม่ตอบ
        // (ตัวตัดสิน: Helpers/VendorKnownGoodFields — BestMatch กันไว้อีกชั้น)
        var addrMatch = BestMatch("VendorAddress", data.VendorAddress);
        if (addrMatch != null)
        {
            data.ReasoningTrace.Add($"[KnownGood] แทน VendorAddress → ใช้ที่อยู่จาก Azure");
            data.VendorAddress = addrMatch;
            swaps++;
        }

        if (swaps > 0)
        {
            _logger.LogDebug("KnownGood corrector: {Count} field swaps for vendor {Vendor}",
                swaps, taxId);
        }
    }

    /// <summary>Strip non-digit chars and confirm a 13-digit Thai TaxId before using as a lookup key.</summary>
    private static string? NormalizeTaxId(string? taxId)
    {
        if (string.IsNullOrWhiteSpace(taxId)) return null;
        var digits = new string(taxId.Where(char.IsDigit).ToArray());
        return digits.Length == 13 ? digits : null;
    }
}
