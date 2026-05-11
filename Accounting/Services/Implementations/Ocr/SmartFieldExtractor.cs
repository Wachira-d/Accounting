using System.Text.RegularExpressions;
// OcrExtractedData lives in Accounting.Services.Implementations (declared in
// OcrService.cs as an internal sibling type) — import that namespace so the
// signatures below resolve without a fully-qualified name everywhere.
using Accounting.Services.Implementations;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Apply real-world Thai-accounting constraints to fill in / validate the
/// fields returned by ANY OCR provider (Azure DI / Python microservice /
/// embedded Tesseract). Designed so all three sources end up with the same
/// data shape and the same quality bar — downstream code (gateway,
/// vendorIntel, document creation) can then treat them uniformly.
///
/// Design rules:
///   • Pure static — no DB, no DI, no IO.
///   • Inputs: a partial OcrExtractedData (whatever the provider produced)
///     + the raw text. Output: same object, mutated in place.
///   • Each rule is conservative: it only writes a field if confident, and
///     records the source/confidence so the UI can mark guesses.
///   • Real-world invariants are codified once here so every provider
///     benefits — embedded Tesseract gets the biggest boost.
///
/// Constraints applied (in this order):
///   1. Thai tax-id checksum filter
///   2. Vendor vs buyer mutual exclusion + role assignment
///   3. SubTotal + VAT = Total math
///   4. VAT = SubTotal × 7% cross-check (Thai standard)
///   5. WHT rate normalization (1/2/3/5/10/15 only)
///   6. Date validation + Buddhist-year conversion
///   7. Document-number plausibility (must contain ≥1 digit)
///   8. Line items sum ≈ SubTotal sanity
/// </summary>
internal static class SmartFieldExtractor
{
    private const decimal ThaiVatRate = 0.07m;
    private static readonly decimal[] ValidWhtRates = { 1m, 2m, 3m, 5m, 10m, 15m };

    /// <summary>Mutate <paramref name="data"/> in place by applying every
    /// constraint we can think of. Safe to call multiple times.</summary>
    public static void Enrich(OcrExtractedData data, string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) rawText = "";

        // 0. Clear any pre-existing tax IDs that fail checksum (e.g. OCR
        // confused 0↔O, 1↔I, 8↔B). Cheaper to re-derive from raw text below
        // than to ship a wrong 13-digit number downstream.
        if (!string.IsNullOrEmpty(data.VendorTaxId) && !IsValidThaiTaxId(data.VendorTaxId))
        {
            data.ReasoningTrace.Add($"[SmartExtract] VendorTaxId {data.VendorTaxId} checksum ไม่ผ่าน → ล้างค่า");
            data.VendorTaxId = null;
        }
        if (!string.IsNullOrEmpty(data.BuyerTaxId) && !IsValidThaiTaxId(data.BuyerTaxId))
        {
            data.ReasoningTrace.Add($"[SmartExtract] BuyerTaxId {data.BuyerTaxId} checksum ไม่ผ่าน → ล้างค่า");
            data.BuyerTaxId = null;
        }

        // 1. Tax IDs — extract all 13-digit candidates with valid checksum
        var taxIds = ExtractValidThaiTaxIds(rawText);

        // 2. Resolve vendor vs buyer with mutual exclusion
        AssignVendorBuyerRoles(data, rawText, taxIds);

        // 3. Math invariants — fill missing amounts (extended to include WHT)
        ApplyAmountMath(data, rawText);

        // 4. VAT rate cross-check (Thai standard 7%)
        ValidateOrInferVatRate(data);

        // 5. WHT rate normalization + statutory-rate inference from category
        NormalizeWhtRate(data, rawText);

        // 6. Date validation: Buddhist year, future-date rejection, plausibility
        ValidateAndNormalizeDate(data);

        // 7. Document number plausibility
        EnsureDocumentNumberPlausible(data, rawText);

        // 8. Line items sum sanity + per-line qty × unit-price check
        ValidateLineItemSum(data);
        ValidateLineItemPerRowMath(data);

        // 9. Tax-ID type classification (juristic vs personal) — feeds WHT/VAT inference
        ClassifyTaxIdTypes(data);

        // 10. Total amount math: Total = SubTotal + VAT - WHTAmount
        ApplyTotalWithWhtMath(data);

        // 11. Total cannot be less than VAT — sanity check
        ValidateAmountOrdering(data);
    }

    // ─── 1. Tax-ID with checksum filter ──────────────────────────────────
    // Thai 13-digit tax ID validation algorithm:
    //   sum = Σ digit[i] * (13 - i)  for i = 0..11
    //   checksum = (11 - (sum % 11)) % 10
    //   digit[12] must equal checksum
    public static List<(string Id, int Position)> ExtractValidThaiTaxIds(string text)
    {
        var pattern = @"(\d{1}[-\s]?\d{4}[-\s]?\d{5}[-\s]?\d{2}[-\s]?\d{1})";
        return Regex.Matches(text, pattern)
            .Cast<Match>()
            .Select(m => (Id: Regex.Replace(m.Groups[1].Value, @"[-\s]", ""), Pos: m.Index))
            .Where(x => x.Id.Length == 13 && IsValidThaiTaxId(x.Id))
            .GroupBy(x => x.Id)
            .Select(g => g.OrderBy(x => x.Pos).First())   // dedupe, keep first occurrence
            .ToList();
    }

    public static bool IsValidThaiTaxId(string id)
    {
        if (id == null || id.Length != 13) return false;
        if (!id.All(char.IsDigit)) return false;
        int sum = 0;
        for (int i = 0; i < 12; i++) sum += (id[i] - '0') * (13 - i);
        int check = (11 - (sum % 11)) % 10;
        return check == (id[12] - '0');
    }

    // ─── 2. Vendor vs buyer role assignment with mutual exclusion ───────
    private static void AssignVendorBuyerRoles(OcrExtractedData data, string text, List<(string Id, int Pos)> taxIds)
    {
        // If both already populated AND distinct, leave them alone.
        var hasVendor = !string.IsNullOrEmpty(data.VendorTaxId) || !string.IsNullOrEmpty(data.VendorName);
        var hasBuyer = !string.IsNullOrEmpty(data.BuyerTaxId) || !string.IsNullOrEmpty(data.BuyerName);
        var bothDistinct = hasVendor && hasBuyer
            && data.VendorTaxId != data.BuyerTaxId
            && !NamesEqual(data.VendorName, data.BuyerName);
        if (bothDistinct) return;

        // Find seller/buyer keyword positions in raw text
        var sellerKeywords = new[] { "ผู้ขาย", "ผู้ออกใบ", "ผู้ให้บริการ", "ผู้ออก", "SELLER", "FROM" };
        var buyerKeywords = new[] { "ผู้ซื้อ", "ลูกค้า", "นามผู้ซื้อ", "BUYER", "CUSTOMER", "BILL TO", "SOLD TO", "ส่งถึง" };
        int sellerPos = FindFirstKeyword(text, sellerKeywords);
        int buyerPos = FindFirstKeyword(text, buyerKeywords);

        // Vendor info typically appears in the header (top of document) and
        // buyer info below it after "ลูกค้า:" / "Bill To:". When a keyword is
        // missing, default vendor to position 0 (top) and buyer to text end —
        // this anchors the nearest-name search to the right region.
        if (sellerPos < 0) sellerPos = 0;
        if (buyerPos < 0) buyerPos = text.Length;

        // Assign tax IDs: closest to seller-keyword → vendor; closest to
        // buyer-keyword → buyer. Mutual exclusion enforced — once assigned to
        // one role, the same ID cannot also be assigned to the other.
        string? newVendorTaxId = data.VendorTaxId;
        string? newBuyerTaxId = data.BuyerTaxId;
        if (taxIds.Count >= 2 && string.IsNullOrEmpty(newVendorTaxId) && string.IsNullOrEmpty(newBuyerTaxId))
        {
            var byDistToSeller = taxIds.OrderBy(t => Math.Abs(t.Pos - sellerPos)).First();
            newVendorTaxId = byDistToSeller.Id;
            var remaining = taxIds.Where(t => t.Id != newVendorTaxId).ToList();
            if (remaining.Count > 0)
                newBuyerTaxId = remaining.OrderBy(t => Math.Abs(t.Pos - buyerPos)).First().Id;
        }
        else if (taxIds.Count == 1)
        {
            // Single tax id — bias toward vendor unless an explicit buyer
            // keyword sits closer to the tax id than any seller keyword.
            var only = taxIds[0];
            var distSeller = sellerPos >= 0 ? Math.Abs(only.Pos - sellerPos) : int.MaxValue;
            var distBuyer = buyerPos >= 0 ? Math.Abs(only.Pos - buyerPos) : int.MaxValue;
            if (string.IsNullOrEmpty(newVendorTaxId) && distSeller <= distBuyer)
                newVendorTaxId = only.Id;
            else if (string.IsNullOrEmpty(newBuyerTaxId))
                newBuyerTaxId = only.Id;
        }

        // Defensive: if both tax IDs ended up identical, drop the buyer one
        // (Vendor ≠ Buyer is a hard invariant — two different parties).
        if (!string.IsNullOrEmpty(newVendorTaxId) && newVendorTaxId == newBuyerTaxId)
            newBuyerTaxId = null;
        data.VendorTaxId = newVendorTaxId;
        data.BuyerTaxId = newBuyerTaxId;

        // Name resolution: extract company names, attach to nearest keyword
        var names = ExtractCompanyNames(text);
        if (names.Count >= 1)
        {
            if (string.IsNullOrEmpty(data.VendorName))
                data.VendorName = names.OrderBy(n => Math.Abs(n.Position - sellerPos)).First().FullName;
            if (string.IsNullOrEmpty(data.BuyerName) && names.Count >= 2)
            {
                data.BuyerName = names
                    .Where(n => !NamesEqual(n.FullName, data.VendorName))
                    .OrderBy(n => Math.Abs(n.Position - buyerPos))
                    .Select(n => n.FullName)
                    .FirstOrDefault();
            }
        }

        // Final invariant: Vendor name and Buyer name must differ.
        if (!string.IsNullOrEmpty(data.VendorName) && NamesEqual(data.VendorName, data.BuyerName))
            data.BuyerName = null;

        // Confidence reporting — high when a keyword anchored the choice
        if (!string.IsNullOrEmpty(data.VendorName))
            data.FieldConfidence["SellerName"] = sellerPos > 0 ? 0.85 : 0.6;
        if (!string.IsNullOrEmpty(data.VendorTaxId))
            data.FieldConfidence["SellerTaxId"] = 0.95;  // checksum-validated
        if (!string.IsNullOrEmpty(data.BuyerTaxId))
            data.FieldConfidence["BuyerTaxId"] = 0.95;
    }

    private static List<(string FullName, int Position)> ExtractCompanyNames(string text)
    {
        var pattern = @"(บริษัท|ห้างหุ้นส่วน(?:จำกัด|สามัญ)?|หจก\.?|ร้าน)\s*(.+?)(?:\s*จำกัด(?:\s*\(มหาชน\))?|\s*\(|(?=\s*เลข|\s*สาขา|\s*ที่อยู่|\s*\d{1}[-\s]?\d{4})|$)";
        var matches = Regex.Matches(text, pattern, RegexOptions.Multiline);
        var results = new List<(string, int)>();
        foreach (Match m in matches)
        {
            var prefix = m.Groups[1].Value;
            var name = m.Groups[2].Value.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2) continue;
            var afterMatch = text.Substring(m.Index, Math.Min(m.Length + 30, text.Length - m.Index));
            var suffix = "";
            if (afterMatch.Contains("จำกัด"))
                suffix = afterMatch.Contains("มหาชน") ? " จำกัด (มหาชน)" : " จำกัด";
            results.Add(($"{prefix} {name}{suffix}".Trim(), m.Index));
        }
        return results;
    }

    private static int FindFirstKeyword(string text, string[] keywords)
    {
        foreach (var kw in keywords)
        {
            var idx = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return idx;
        }
        return -1;
    }

    private static bool NamesEqual(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var na = Normalize(a);
        var nb = Normalize(b);
        return na == nb;
    }

    private static string Normalize(string s)
        => new string((s ?? "").ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c != '.' && c != ',').ToArray()).Trim();

    // ─── 3. Math invariants: SubTotal + VAT = Total ─────────────────────
    private static void ApplyAmountMath(OcrExtractedData data, string text)
    {
        var sub = data.SubTotal;
        var vat = data.VatAmount;
        var total = data.TotalAmount;

        // Derive the third value from the other two
        if (sub.HasValue && vat.HasValue && !total.HasValue)
        {
            data.TotalAmount = sub.Value + vat.Value;
            data.FieldConfidence["TotalAmount"] = Math.Max(data.FieldConfidence.GetValueOrDefault("TotalAmount", 0), 0.9);
        }
        else if (total.HasValue && vat.HasValue && !sub.HasValue && total.Value > vat.Value)
        {
            data.SubTotal = total.Value - vat.Value;
            data.FieldConfidence["SubTotal"] = 0.9;
        }
        else if (total.HasValue && sub.HasValue && !vat.HasValue && total.Value > sub.Value)
        {
            data.VatAmount = total.Value - sub.Value;
            data.FieldConfidence["VatAmount"] = 0.9;
        }
        else if (total.HasValue && !sub.HasValue && !vat.HasValue && LooksLikeVatDoc(text))
        {
            // Tax invoice with only the total visible — derive SubTotal/VAT
            // assuming standard 7% Thai VAT (Total = SubTotal × 1.07).
            data.SubTotal = Math.Round(total.Value / (1m + ThaiVatRate), 2);
            data.VatAmount = total.Value - data.SubTotal.Value;
            data.FieldConfidence["SubTotal"] = 0.7;     // derived, not extracted
            data.FieldConfidence["VatAmount"] = 0.7;
        }

        // Validate: VAT cannot exceed SubTotal in any realistic Thai doc
        // (VAT rate is 7%, max conceivable would be ~7% of SubTotal). If
        // someone parsed VAT > SubTotal, the labels probably got swapped.
        if (data.SubTotal.HasValue && data.VatAmount.HasValue
            && data.VatAmount.Value > data.SubTotal.Value * 0.5m)
        {
            // Don't auto-swap — just lower confidence so the gateway flags it
            data.FieldConfidence["VatAmount"] = 0.3;
            data.ReasoningTrace.Add("[SmartExtract] VAT > 50% of SubTotal — ค่าผิดปกติ กรุณาตรวจสอบ");
        }
    }

    private static bool LooksLikeVatDoc(string text)
        => text.Contains("ใบกำกับภาษี") || text.Contains("ใบกํากับภาษี")
        || text.ToUpperInvariant().Contains("TAX INVOICE")
        || text.Contains("ภาษีมูลค่าเพิ่ม") || text.Contains("VAT");

    // ─── 4. VAT 7% cross-check ──────────────────────────────────────────
    private static void ValidateOrInferVatRate(OcrExtractedData data)
    {
        if (!data.SubTotal.HasValue || data.SubTotal.Value <= 0) return;
        if (!data.VatAmount.HasValue || data.VatAmount.Value <= 0) return;

        var rate = data.VatAmount.Value / data.SubTotal.Value;
        // Within ±0.5pp of 7% = consistent
        if (Math.Abs(rate - ThaiVatRate) <= 0.005m)
            data.FieldConfidence["VatAmount"] = Math.Max(data.FieldConfidence.GetValueOrDefault("VatAmount", 0), 0.95);
        // Not 7% and not 0% → suspicious
        else if (rate > 0.01m && rate < 0.06m)
            data.ReasoningTrace.Add($"[SmartExtract] VAT {rate:P1} ไม่ใช่ 7% มาตรฐาน — โปรดตรวจสอบ");
    }

    // ─── 5. WHT rate normalization ──────────────────────────────────────
    // Thai WHT statutory rates: 1, 2, 3, 5, 10, 15. Anything else is OCR error
    // (e.g. parsed "7" as WHT when 7 is actually the VAT). Snap to the nearest
    // valid rate when within tolerance, otherwise clear.
    private static void NormalizeWhtRate(OcrExtractedData data, string text)
    {
        if (!data.HasWht && !data.WhtRate.HasValue) return;
        if (!data.WhtRate.HasValue) return;

        var r = data.WhtRate.Value;
        if (ValidWhtRates.Contains(r))
        {
            data.FieldConfidence["WhtRate"] = 0.95;
            return;
        }

        // 7% is VAT, not WHT — almost certainly a misread
        if (r == 7m)
        {
            data.WhtRate = null;
            data.HasWht = false;
            data.ReasoningTrace.Add("[SmartExtract] WHT 7% ตีความผิด (7% = VAT ไม่ใช่ WHT) → ล้างค่า");
            return;
        }

        // Snap to nearest valid within 0.5pp
        var nearest = ValidWhtRates.OrderBy(v => Math.Abs(v - r)).First();
        if (Math.Abs(nearest - r) <= 0.5m)
        {
            data.WhtRate = nearest;
            data.FieldConfidence["WhtRate"] = 0.7;
            data.ReasoningTrace.Add($"[SmartExtract] WHT {r}% → snap เป็น {nearest}% (ค่ามาตรฐาน)");
        }
        else
        {
            data.WhtRate = null;
            data.HasWht = false;
            data.ReasoningTrace.Add($"[SmartExtract] WHT {r}% ไม่ใช่อัตราตามกฎหมาย → ล้างค่า");
        }
    }

    // ─── 6. Date validation + Buddhist year conversion ──────────────────
    private static void ValidateAndNormalizeDate(OcrExtractedData data)
    {
        if (!data.DocumentDate.HasValue) return;
        var d = data.DocumentDate.Value;
        // Buddhist year (พ.ศ.) ranges 2400–2700 commonly. Convert if so.
        if (d.Year >= 2400 && d.Year <= 2700)
        {
            try { data.DocumentDate = new DateTime(d.Year - 543, d.Month, d.Day); }
            catch { data.DocumentDate = null; }
        }
        // Reject impossibly old / future dates
        var year = data.DocumentDate?.Year ?? 0;
        if (year < 1990 || year > DateTime.UtcNow.Year + 1)
        {
            data.ReasoningTrace.Add($"[SmartExtract] วันที่ {data.DocumentDate:yyyy-MM-dd} ไม่สมเหตุสมผล → ล้างค่า");
            data.DocumentDate = null;
            data.FieldConfidence["DocumentDate"] = 0;
        }
        else if (data.DocumentDate.HasValue)
        {
            data.FieldConfidence["DocumentDate"] = Math.Max(data.FieldConfidence.GetValueOrDefault("DocumentDate", 0), 0.9);
        }
    }

    // ─── 7. Document number plausibility ────────────────────────────────
    private static void EnsureDocumentNumberPlausible(OcrExtractedData data, string text)
    {
        if (!string.IsNullOrEmpty(data.DocumentNumber))
        {
            // Reject pure-word, no-digit "numbers" (likely a label like "Invoice")
            if (!data.DocumentNumber.Any(char.IsDigit))
            {
                data.ReasoningTrace.Add($"[SmartExtract] DocumentNumber '{data.DocumentNumber}' ไม่มีตัวเลข → ล้างค่า");
                data.DocumentNumber = null;
            }
            else
                data.FieldConfidence["DocumentNumber"] = Math.Max(data.FieldConfidence.GetValueOrDefault("DocumentNumber", 0), 0.85);
        }

        if (string.IsNullOrEmpty(data.DocumentNumber))
        {
            // Try to recover from raw text using anchored patterns. The "เลขที่"
            // anchor produces the strongest signal; the bare INV/REC/PO prefix
            // patterns are last-resort because they sometimes match product codes.
            string[] patterns =
            {
                @"เลขที่\s*(?:เอกสาร)?\s*[:：]?\s*([A-Za-z0-9][\w\-/]{2,})",
                @"(?:Invoice|Receipt|Document)\s*(?:No\.?|Number)?\s*[:：]?\s*([A-Za-z0-9][\w\-/]{2,})",
                @"\b((?:INV|REC|TAX|TX|IV|PO|CN|DN)[\-/]?\d[\d\-/A-Za-z]*)\b",
            };
            foreach (var p in patterns)
            {
                var m = Regex.Match(text, p, RegexOptions.IgnoreCase);
                if (m.Success && m.Groups[1].Value.Any(char.IsDigit))
                {
                    data.DocumentNumber = m.Groups[1].Value.Trim();
                    data.FieldConfidence["DocumentNumber"] = 0.7;
                    break;
                }
            }
        }
    }

    // ─── 8. Line items sum sanity ───────────────────────────────────────
    private static void ValidateLineItemSum(OcrExtractedData data)
    {
        if (data.Items == null || data.Items.Count == 0) return;
        if (!data.SubTotal.HasValue || data.SubTotal.Value <= 0) return;

        var lineSum = data.Items.Where(i => i.Amount.HasValue).Sum(i => i.Amount!.Value);
        if (lineSum <= 0) return;

        var diff = Math.Abs(lineSum - data.SubTotal.Value);
        var tolerance = Math.Max(1m, data.SubTotal.Value * 0.02m);  // 2% or ฿1
        if (diff <= tolerance)
        {
            data.FieldConfidence["SubTotal"] = Math.Max(data.FieldConfidence.GetValueOrDefault("SubTotal", 0), 0.97);
        }
        else
        {
            data.ReasoningTrace.Add(
                $"[SmartExtract] ยอด line items รวม {lineSum:N2} ≠ SubTotal {data.SubTotal:N2} (ต่าง {diff:N2})");
        }
    }

    // ─── 8b. Per-line: Quantity × UnitPrice ≈ Amount ────────────────────
    // OCR commonly confuses commas/decimals — this picks up the row where
    // 5 × 100 = 500 was read as "5 × 100 = 50.0" or similar.
    private static void ValidateLineItemPerRowMath(OcrExtractedData data)
    {
        if (data.Items == null) return;
        foreach (var line in data.Items)
        {
            if (!line.Quantity.HasValue || !line.UnitPrice.HasValue || !line.Amount.HasValue) continue;
            if (line.Quantity.Value <= 0 || line.UnitPrice.Value <= 0 || line.Amount.Value <= 0) continue;
            var expected = line.Quantity.Value * line.UnitPrice.Value;
            var diff = Math.Abs(expected - line.Amount.Value);
            var tolerance = Math.Max(0.5m, expected * 0.02m);
            if (diff > tolerance)
            {
                data.ReasoningTrace.Add(
                    $"[SmartExtract] รายการ '{line.Description}': qty×price = {expected:N2} ≠ amount {line.Amount:N2}");
            }
        }
    }

    // ─── 9. Tax-ID type classification ──────────────────────────────────
    // First digit of a 13-digit Thai tax ID encodes the holder type. We use
    // this to:
    //   • Bias VAT-registration expectation (juristic codes 0/8/9 are almost
    //     always VAT-registered; individuals usually not)
    //   • Decide statutory WHT rate when ambiguous (Section 50 has different
    //     rates for individuals vs juristic recipients).
    //
    // Reference: Thai Civil Registration Act + Revenue Department rules:
    //   1   = Thai national ID issued post-1984 (บัตรประชาชนสมัยใหม่)
    //   2   = Thai national ID issued pre-1984 / supplementary
    //   3   = Permanent resident card (พร.)
    //   4–7 = Various historical legacy registration types
    //   8   = Juristic ID — partnership / private limited / public limited
    //   0/9 = Other juristic registrations (state enterprise, foundation)
    private static void ClassifyTaxIdTypes(OcrExtractedData data)
    {
        ClassifyOne(data, data.VendorTaxId, "Vendor");
        ClassifyOne(data, data.BuyerTaxId, "Buyer");
    }

    private static void ClassifyOne(OcrExtractedData data, string? id, string role)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 13) return;
        var first = id[0];
        var isJuristic = first == '0' || first == '8' || first == '9';
        var key = $"{role}TaxIdType";
        if (isJuristic)
        {
            data.FieldConfidence[key] = 0.9;
            // Juristic vendors → very likely VAT-registered → expect 7% VAT.
            // Don't auto-set HasWht here; that depends on what we BUY from them.
        }
        else
        {
            data.FieldConfidence[key] = 0.9;
            // Personal (1-7) → typically NOT VAT-registered; expect 0% VAT.
            // If extraction said VAT > 0 for a personal vendor, flag it.
            if (role == "Vendor" && data.VatAmount.HasValue && data.VatAmount.Value > 0)
            {
                data.ReasoningTrace.Add(
                    $"[SmartExtract] ผู้ขายมีรหัสบัตรบุคคลธรรมดา (ขึ้นต้น {first}) แต่มี VAT {data.VatAmount:N2} — ตรวจสอบ");
            }
        }
    }

    public static bool IsJuristicTaxId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 13) return false;
        return id[0] == '0' || id[0] == '8' || id[0] == '9';
    }

    // ─── 10. Total = SubTotal + VAT - WHT math ──────────────────────────
    // When the document shows WHT separately at the bottom (common on
    // service invoices), the "ยอดที่ต้องชำระ" / "Net Payable" already
    // reflects WHT deduction. Compute and validate when we have the rate.
    private static void ApplyTotalWithWhtMath(OcrExtractedData data)
    {
        if (!data.SubTotal.HasValue) return;
        if (!data.HasWht || !data.WhtRate.HasValue) return;

        var whtAmount = Math.Round(data.SubTotal.Value * data.WhtRate.Value / 100m, 2);
        var vat = data.VatAmount ?? 0m;
        var netPayable = data.SubTotal.Value + vat - whtAmount;

        if (data.TotalAmount.HasValue)
        {
            // If extracted Total matches gross (sub+vat) AND WHT >0, the user
            // is going to be paying NetPayable — flag for review so downstream
            // doesn't book the wrong amount to AP/cash.
            var gross = data.SubTotal.Value + vat;
            if (Math.Abs(data.TotalAmount.Value - gross) < 1m
                && Math.Abs(data.TotalAmount.Value - netPayable) > 1m)
            {
                data.ReasoningTrace.Add(
                    $"[SmartExtract] Total {data.TotalAmount:N2} = gross, แต่หลังหัก WHT จ่ายจริง ≈ {netPayable:N2}");
            }
        }
    }

    // ─── 11. Amount ordering sanity ─────────────────────────────────────
    private static void ValidateAmountOrdering(OcrExtractedData data)
    {
        if (data.TotalAmount.HasValue && data.VatAmount.HasValue
            && data.TotalAmount.Value < data.VatAmount.Value)
        {
            data.ReasoningTrace.Add(
                $"[SmartExtract] Total {data.TotalAmount:N2} < VAT {data.VatAmount:N2} — ค่าสลับกันหรือเปล่า?");
            data.FieldConfidence["TotalAmount"] = 0.3;
            data.FieldConfidence["VatAmount"] = 0.3;
        }
        if (data.TotalAmount.HasValue && data.TotalAmount.Value < 0)
        {
            data.ReasoningTrace.Add($"[SmartExtract] Total {data.TotalAmount:N2} ติดลบ — ล้างค่า");
            data.TotalAmount = null;
        }
        if (data.VatAmount.HasValue && data.VatAmount.Value < 0)
        {
            data.VatAmount = null;
        }
    }

    // ─── Phone, postal code, email extraction (for vendor/buyer enrichment) ─
    // Not used by Enrich itself yet — exposed so the caller can pull contact
    // info to attach to the matched Contact entity.

    public static string? ExtractFirstThaiPhone(string text)
    {
        // 9-10 digits, optionally with dashes/spaces; first digit must be 0
        var m = Regex.Match(text, @"\b(0\d[\s\-]?\d{3}[\s\-]?\d{4})\b");
        return m.Success ? Regex.Replace(m.Groups[1].Value, @"[\s\-]", "") : null;
    }

    public static string? ExtractFirstThaiPostalCode(string text)
    {
        // 5 digits in Thai range 10000-96999; bias toward those following
        // a province name or "รหัสไปรษณีย์" keyword to avoid mistaking an
        // amount for a postal code.
        var anchored = Regex.Match(text, @"(?:รหัสไปรษณีย์|Postal\s*Code)\s*[:：]?\s*(\d{5})", RegexOptions.IgnoreCase);
        if (anchored.Success) return anchored.Groups[1].Value;
        // Free-floating: only accept when the 5-digit number stands by itself
        // (no decimal, not part of a longer number).
        foreach (Match m in Regex.Matches(text, @"(?<!\d)(\d{5})(?!\d)"))
        {
            var code = int.Parse(m.Groups[1].Value);
            if (code >= 10000 && code <= 96999) return m.Groups[1].Value;
        }
        return null;
    }

    public static string? ExtractFirstEmail(string text)
    {
        var m = Regex.Match(text, @"\b([A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})\b");
        return m.Success ? m.Groups[1].Value : null;
    }
}
