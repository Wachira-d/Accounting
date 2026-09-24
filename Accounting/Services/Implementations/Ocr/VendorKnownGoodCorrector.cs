using Accounting.Models.Entities;
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
    /// <param name="allowTaxIdRecovery">ยอมให้ "กู้ <c>VendorTaxId</c> จากชื่อที่ใกล้เคียง"
    /// ไหม — <b>เส้น Azure ต้องส่ง false</b>: Azure อ่านเลขบนกระดาษได้แม่น ถ้ามันบอกว่า
    /// ไม่มีเลข แปลว่ากระดาษไม่มี ⇒ การเติมเลขจากประวัติคือการ<b>แต่งเลขที่ไม่ได้อยู่
    /// บนใบนี้</b> ลงช่องผู้ขาย แล้วไหลต่อไปเป็นเลขใน §86/4 และรายงานภาษีซื้อ §87
    /// (หลักการข้อ 3: ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ) · tier 2/3 ยังเปิดไว้เพราะ
    /// ผลอ่านสกปรกจนคุ้มเสี่ยง และเป็นพฤติกรรมเดิมที่มีเทสต์ล็อกอยู่</param>
    internal async Task ApplyAsync(Guid companyId, OcrExtractedData data,
        bool allowTaxIdRecovery = true, CancellationToken ct = default)
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
        if (string.IsNullOrEmpty(taxId) && !string.IsNullOrEmpty(nameNoise) && allowTaxIdRecovery)
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

        // ── ช่องที่ engine อ่าน**ไม่ได้เลย** — เติมจากคลังที่ Azure/ผู้ใช้สอนไว้ (รอบ 190 · ข้อ 11) ──
        // BestMatch ข้างบนซ่อมได้แต่ค่าที่ "มีแต่เพี้ยน" ⇒ engine ในเครื่องที่ไม่คืนที่อยู่เลย (python
        // ไม่มีช่องนี้ · Tesseract บนใบ POS) ไม่เคยได้ใช้ค่าที่ Azure สอนไว้ · ด่านกันคลังเอียง
        // (ต้องยืนยัน/เห็นซ้ำ · ค่าเดียว · สาขาไม่ขัด · ไม่ใช่ที่อยู่เรา) อยู่ใน Helpers/OcrKnownGoodAddressFill
        if (string.IsNullOrWhiteSpace(data.VendorAddress)
            && known.Any(k => k.FieldName == Accounting.Helpers.OcrKnownGoodAddressFill.AddressField))
        {
            var ourAddress = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == companyId && !c.IsDeleted)
                .Select(c => c.Address)
                .FirstOrDefaultAsync(ct);
            var fill = Accounting.Helpers.OcrKnownGoodAddressFill.Decide(
                data.VendorAddress, data.VendorBranchCode,
                known.Select(k => new Accounting.Helpers.OcrKnownGoodRow(
                    k.FieldName, k.Value, k.ConfirmedCount, k.Source)).ToList(),
                ourAddress, data.BuyerAddress);
            if (fill.Value != null)
            {
                data.VendorAddress = fill.Value;
                data.FieldConfidence[Accounting.Helpers.OcrFieldKeys.SellerAddress] = (double)fill.Confidence;
                data.Note(Accounting.Helpers.OcrFieldKeys.SellerAddress, fill.Value,
                    Accounting.Helpers.OcrFieldSource.VendorHistory, fill.Confidence, fill.Reason);
                data.ReasoningTrace.Add("[KnownGood] " + fill.Reason);
                swaps++;
            }
        }

        if (swaps > 0)
        {
            _logger.LogDebug("KnownGood corrector: {Count} field swaps for vendor {Vendor}",
                swaps, taxId);
        }
    }

    /// <summary>
    /// **ปิดวงจรเรียนรู้จากคำแก้ของผู้ใช้** — เขียนค่าที่ผู้ใช้ยืนยันเองลงคลัง
    /// known-good ด้วย <c>Source = "UserCorrection"</c> ซึ่ง <see cref="ApplyAsync"/>
    /// จัดให้<b>ชนะค่าที่ Azure สอนไว้เสมอ</b>
    ///
    /// <para>═══ ที่มา ═══ คลังนี้เดิมมีผู้เขียนแค่ <c>AzureDiPatternLearner</c>
    /// (Source = "AzureDI") ⇒ ตัวจัดอันดับที่เขียนว่า "UserCorrection ชนะ Azure"
    /// **ไม่มีวันได้ใช้** เพราะไม่มีใครเขียนแถว UserCorrection ลงไปเลยจากเส้น OCR
    /// ⇒ ผู้ใช้แก้ชื่อผู้ขายกี่ครั้ง ใบถัดไปก็กลับไปผิดเหมือนเดิม — อาการ
    /// "สอนแล้วระบบไม่จำ" (กฎเหล็ก #1: CAPTURE ที่ไม่มีคนเขียน = ไม่มี CAPTURE ·
    /// หลักการข้อ 2 "มี ≠ ถูกเรียก")</para>
    ///
    /// <para>ใช้ด่านช่องชุดเดียวกับฝั่งเขียนของ Azure (<see cref="Accounting.Helpers.VendorKnownGoodFields"/>)
    /// — เลขที่เอกสารและช่องตัวเลขล้วนยัง<b>ห้าม</b>เข้าคลัง แม้ผู้ใช้จะพิมพ์เอง</para>
    /// </summary>
    public async Task<bool> RememberUserCorrectionAsync(Guid companyId, string? vendorTaxId,
        string fieldName, string? value, CancellationToken ct = default)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return false;
        // ต้องเป็นช่องที่ "คงที่ต่อผู้ขาย" เท่านั้น — กติกาเดียวกับฝั่ง Azure
        if (!Accounting.Helpers.VendorKnownGoodFields.IsCorrectable(fieldName)) return false;
        if (Accounting.Helpers.VendorKnownGoodFields.IsPerDocument(fieldName)) return false;
        var key = NormalizeTaxId(vendorTaxId);
        if (string.IsNullOrEmpty(key))
        {
            // ไม่มีกุญแจ = ฝั่งอ่าน (ApplyAsync) หาแถวนี้ไม่เจอตลอดกาล ⇒ เก็บไปก็ไร้ผล
            // **ห้ามเงียบ** — ผู้ใช้ที่แก้ชื่อผู้ขายบนสแกนที่อ่านเลขภาษีไม่ออก (เคสที่พบ
            // บ่อยที่สุด) จะเข้าใจว่าระบบจำแล้ว ทั้งที่ไม่ได้จำอะไรเลย
            // (หลักการข้อ 7 "ล้มดัง" · ผู้เรียกเอาไปบอกผู้ใช้ต่อ)
            _logger.LogInformation(
                "known-good: ไม่บันทึกคำแก้ช่อง {Field} ของบริษัท {CompanyId} — ยังไม่มีเลขผู้เสียภาษีของผู้ขายเป็นกุญแจ",
                fieldName, companyId);
            return false;
        }

        var existing = await _db.VendorKnownGoodValues
            .FirstOrDefaultAsync(x => x.CompanyId == companyId
                && x.VendorTaxId == key
                && x.FieldName == fieldName
                && x.Value == v
                && !x.IsDeleted, ct);
        if (existing != null)
        {
            existing.ConfirmedCount += 1;
            existing.LastSeenAt = DateTime.UtcNow;
            existing.Source = "UserCorrection";          // เลื่อนขั้นได้ ห้ามลดขั้น
            existing.Confidence = Math.Max(existing.Confidence, 0.98m);
            existing.UpdatedBy = "User-Correction";
        }
        else
        {
            _db.VendorKnownGoodValues.Add(new VendorKnownGoodValue
            {
                CompanyId = companyId,
                VendorTaxId = key,
                FieldName = fieldName,
                Value = v,
                Confidence = 0.98m,
                ConfirmedCount = 1,
                Source = "UserCorrection",
                LastSeenAt = DateTime.UtcNow,
                CreatedBy = "User-Correction",
            });
        }
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Strip non-digit chars and confirm a 13-digit Thai TaxId before using as a lookup key.</summary>
    private static string? NormalizeTaxId(string? taxId)
    {
        if (string.IsNullOrWhiteSpace(taxId)) return null;
        var digits = new string(taxId.Where(char.IsDigit).ToArray());
        return digits.Length == 13 ? digits : null;
    }
}
