using System.Text.RegularExpressions;
using Accounting.Models.Entities;
using Accounting.Services.Implementations.Ocr;
using static Accounting.Services.Implementations.Ocr.FieldPatternLibrary;

namespace Accounting.Services.Implementations;

/// <summary>
/// Analyzes OCR text by detecting keyword-based zones (seller, buyer, items, totals)
/// and extracting structured fields from the correct zone context.
/// Learns from user corrections to improve over time.
/// </summary>
public static class DocumentZoneAnalyzer
{
    public enum ZoneType { Header, Seller, Buyer, Items, Summary, Payment, Unknown }

    public class TextZone
    {
        public ZoneType Type { get; set; }
        public string Text { get; set; } = "";
        public int Start { get; set; }
        public int End { get; set; }
        public List<string> MatchedKeywords { get; set; } = new();
        public double Score { get; set; }
    }

    public class ZoneAnalysisResult
    {
        public List<TextZone> Zones { get; set; } = new();

        // Header
        public string? DocumentType { get; set; }
        public decimal Confidence { get; set; }
        public string? DocumentNumber { get; set; }
        public DateTime? DocumentDate { get; set; }

        // Seller (vendor)
        public string? SellerName { get; set; }
        public string? SellerTaxId { get; set; }

        // Buyer (our company)
        public string? BuyerName { get; set; }
        public string? BuyerTaxId { get; set; }

        // Amounts
        public decimal? SubTotal { get; set; }
        public decimal? VatAmount { get; set; }
        public decimal? TotalAmount { get; set; }

        // Extra
        public string? ExpenseCategory { get; set; }
        public bool HasWht { get; set; }
        public decimal? WhtRate { get; set; }
        public int? PaymentTermsDays { get; set; }

        public string ZoneSummary { get; set; } = "";

        // Per-field confidence scores (0-1) — for UI to show traffic-light per field
        public Dictionary<string, double> FieldConfidence { get; set; } = new();

        // Trace of reasoning steps for debug panel
        public List<string> ReasoningTrace { get; set; } = new();
    }

    static readonly Dictionary<ZoneType, (string[] Keywords, int Radius)> ZoneDefinitions = new()
    {
        [ZoneType.Seller] = (new[] {
            "ผู้ขาย", "ผู้ออก", "ผู้ให้บริการ", "ผู้ออกใบ", "ออกโดย",
            "SELLER", "SOLD BY", "FROM", "VENDOR", "SUPPLIER"
        }, 400),
        [ZoneType.Buyer] = (new[] {
            "ผู้ซื้อ", "ลูกค้า", "นามผู้ซื้อ", "ชื่อผู้ซื้อ", "ส่งถึง", "จัดส่งถึง",
            "BUYER", "CUSTOMER", "BILL TO", "SOLD TO", "SHIP TO", "DELIVER TO"
        }, 400),
        [ZoneType.Header] = (new[] {
            "ใบกำกับภาษี", "ใบกํากับภาษี", "ใบเสร็จรับเงิน", "ใบแจ้งหนี้", "ใบสั่งซื้อ",
            "ใบลดหนี้", "ใบเพิ่มหนี้", "หนังสือรับรอง", "50 ทวิ",
            "TAX INVOICE", "INVOICE", "RECEIPT", "PURCHASE ORDER",
            "CREDIT NOTE", "DEBIT NOTE"
        }, 300),
        [ZoneType.Items] = (new[] {
            "รายการ", "รายละเอียด", "ลำดับ", "DESCRIPTION", "ITEM", "QTY", "NO."
        }, 600),
        [ZoneType.Summary] = (new[] {
            "รวมเงิน", "ภาษีมูลค่าเพิ่ม", "ยอดรวม", "รวมทั้งสิ้น", "สุทธิ",
            "จำนวนเงินรวม", "ก่อนภาษี",
            "VAT", "TOTAL", "SUBTOTAL", "SUB TOTAL", "GRAND TOTAL", "NET TOTAL", "AMOUNT"
        }, 300),
        [ZoneType.Payment] = (new[] {
            "ชำระเงิน", "โอนเงิน", "ธนาคาร", "เลขบัญชี", "PAYMENT", "BANK", "TRANSFER"
        }, 250),
    };

    /// <summary>
    /// Main entry point: analyze OCR text into zones and extract structured data.
    /// </summary>
    public static ZoneAnalysisResult Analyze(string text,
        List<OcrLearnedPattern>? learnedPatterns = null,
        string? ourCompanyTaxId = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ZoneAnalysisResult { DocumentType = "Receipt", Confidence = 0.3m };

        // Step 1: Detect zones by finding keywords and their surrounding context
        var zones = DetectZones(text);

        // Step 2: Extract document type from header zone or full text
        var result = new ZoneAnalysisResult { Zones = zones };
        DetectDocumentType(text, result);

        // Step 3: Extract fields using pattern library + zone awareness + learned patterns
        ExtractFromZones(text, zones, result, learnedPatterns, ourCompanyTaxId);

        // Step 4: Apply learned patterns to override or fill gaps (legacy support)
        if (learnedPatterns?.Count > 0)
            ApplyLearnedPatterns(text, result, learnedPatterns);

        // Step 5: Build zone summary for debugging
        result.ZoneSummary = BuildZoneSummary(zones, text);

        // Step 6: Calculate overall confidence as weighted average of per-field
        if (result.FieldConfidence.Count > 0)
        {
            var weighted = result.FieldConfidence.Values.Average();
            // Blend with the doc-type confidence
            result.Confidence = Math.Round((decimal)((double)result.Confidence * 0.4 + weighted * 0.6), 2);
        }

        return result;
    }

    static List<TextZone> DetectZones(string text)
    {
        var allMarkers = new List<(int Pos, int KeywordLen, ZoneType Type, string Keyword)>();

        foreach (var (zoneType, (keywords, _)) in ZoneDefinitions)
        {
            foreach (var kw in keywords)
            {
                int idx = 0;
                while (idx < text.Length)
                {
                    int found = text.IndexOf(kw, idx, StringComparison.OrdinalIgnoreCase);
                    if (found < 0) break;
                    allMarkers.Add((found, kw.Length, zoneType, kw));
                    idx = found + kw.Length;
                }
            }
        }

        allMarkers = allMarkers.OrderBy(m => m.Pos).ToList();

        // Build zones: each keyword marks a zone center; the zone extends
        // by its radius OR until the next zone keyword of a different type
        var zones = new List<TextZone>();

        for (int i = 0; i < allMarkers.Count; i++)
        {
            var (pos, kwLen, zoneType, keyword) = allMarkers[i];
            int radius = ZoneDefinitions[zoneType].Radius;

            int zoneStart = Math.Max(0, pos - 50); // keywords usually start the zone
            int zoneEnd = Math.Min(text.Length, pos + radius);

            // Shrink zone if next different-type keyword is closer
            for (int j = i + 1; j < allMarkers.Count; j++)
            {
                if (allMarkers[j].Type != zoneType)
                {
                    zoneEnd = Math.Min(zoneEnd, allMarkers[j].Pos);
                    break;
                }
            }

            // Check if we already have a zone of this type — merge/expand
            var existing = zones.FirstOrDefault(z => z.Type == zoneType &&
                z.Start <= pos && pos <= z.End + 100);
            if (existing != null)
            {
                existing.End = Math.Max(existing.End, zoneEnd);
                existing.Text = text[existing.Start..existing.End];
                if (!existing.MatchedKeywords.Contains(keyword))
                    existing.MatchedKeywords.Add(keyword);
                existing.Score += 1.0;
            }
            else
            {
                zones.Add(new TextZone
                {
                    Type = zoneType,
                    Start = zoneStart,
                    End = zoneEnd,
                    Text = text[zoneStart..zoneEnd],
                    MatchedKeywords = new List<string> { keyword },
                    Score = 1.0,
                });
            }
        }

        return zones;
    }

    static void DetectDocumentType(string text, ZoneAnalysisResult result)
    {
        var upper = text.ToUpperInvariant();
        bool hasTaxInv = text.Contains("ใบกำกับภาษี") || text.Contains("ใบกํากับภาษี") || upper.Contains("TAX INVOICE");
        bool hasCreditNote = text.Contains("ใบลดหนี้") || upper.Contains("CREDIT NOTE");
        bool hasDebitNote = text.Contains("ใบเพิ่มหนี้") || upper.Contains("DEBIT NOTE");
        bool hasWht = text.Contains("หนังสือรับรอง") || text.Contains("50 ทวิ");
        bool hasPO = text.Contains("ใบสั่งซื้อ") || upper.Contains("PURCHASE ORDER");
        bool hasInvoice = text.Contains("ใบแจ้งหนี้") || (upper.Contains("INVOICE") && !upper.Contains("TAX INVOICE"));
        bool hasReceipt = text.Contains("ใบเสร็จรับเงิน") || upper.Contains("RECEIPT");

        if (hasTaxInv)          { result.DocumentType = "TaxInvoice"; result.Confidence = 0.90m; }
        else if (hasCreditNote) { result.DocumentType = "CreditNote"; result.Confidence = 0.88m; }
        else if (hasDebitNote)  { result.DocumentType = "DebitNote"; result.Confidence = 0.88m; }
        else if (hasWht)        { result.DocumentType = "WHT"; result.Confidence = 0.85m; }
        else if (hasPO)         { result.DocumentType = "PurchaseOrder"; result.Confidence = 0.85m; }
        else if (hasInvoice)    { result.DocumentType = "Invoice"; result.Confidence = 0.85m; }
        else if (hasReceipt)    { result.DocumentType = "Receipt"; result.Confidence = 0.80m; }
        else                    { result.DocumentType = "Receipt"; result.Confidence = 0.4m; }
    }

    static void ExtractFromZones(string text, List<TextZone> zones, ZoneAnalysisResult result,
        List<OcrLearnedPattern>? learnedPatterns = null, string? ourCompanyTaxId = null)
    {
        // Build extraction context shared across all field extractions
        var ctx = new FieldExtractor.ExtractionContext
        {
            FullText = text,
            Zones = zones,
            LearnedPatterns = learnedPatterns,
            OurCompanyTaxId = ourCompanyTaxId,
            NegativeExamples = new HashSet<string>(
                (learnedPatterns ?? new()).Where(p => p.IsNegativeExample && p.NegativeValue != null)
                    .Select(p => p.NegativeValue!),
                StringComparer.OrdinalIgnoreCase),
        };

        // --- Tax IDs: extract all candidates, classify by zone (seller/buyer) ---
        var allTaxIds = ExtractCandidates(text, FieldType.TaxId)
            .Where(c => c.IsChecksumValid || c.Score >= 0.4)
            .ToList();

        var sellerZone = zones.FirstOrDefault(z => z.Type == ZoneType.Seller);
        var buyerZone = zones.FirstOrDefault(z => z.Type == ZoneType.Buyer);

        // Tax IDs in seller zone → SellerTaxId; in buyer zone → BuyerTaxId
        var sellerTaxIds = sellerZone != null
            ? allTaxIds.Where(c => c.Position >= sellerZone.Start && c.Position <= sellerZone.End).ToList()
            : new();
        var buyerTaxIds = buyerZone != null
            ? allTaxIds.Where(c => c.Position >= buyerZone.Start && c.Position <= buyerZone.End).ToList()
            : new();

        result.SellerTaxId = sellerTaxIds.OrderByDescending(c => c.Score).FirstOrDefault()?.NormalizedValue;
        result.BuyerTaxId = buyerTaxIds.OrderByDescending(c => c.Score).FirstOrDefault()?.NormalizedValue;

        // If no tax id in seller/buyer zones, use positional heuristic: first one = seller
        if (result.SellerTaxId == null && result.BuyerTaxId == null && allTaxIds.Count > 0)
        {
            var ordered = allTaxIds.OrderBy(c => c.Position).ToList();
            result.SellerTaxId = ordered[0].NormalizedValue;
            if (ordered.Count > 1) result.BuyerTaxId = ordered[1].NormalizedValue;
        }
        else if (result.SellerTaxId == null && allTaxIds.Count > 0)
        {
            // Pick first tax ID outside buyer zone
            var notInBuyer = allTaxIds.Where(c => buyerZone == null || c.Position < buyerZone.Start || c.Position > buyerZone.End).ToList();
            result.SellerTaxId = notInBuyer.FirstOrDefault()?.NormalizedValue;
        }

        // If our own tax ID is detected as seller — it actually means we're the seller (this is OUR invoice)
        // and the real vendor is the buyer
        if (!string.IsNullOrEmpty(ourCompanyTaxId) && result.SellerTaxId == ourCompanyTaxId
            && !string.IsNullOrEmpty(result.BuyerTaxId))
        {
            // swap: real vendor is the buyer
            (result.SellerTaxId, result.BuyerTaxId) = (result.BuyerTaxId, result.SellerTaxId);
        }

        // --- Company names: extract candidates, match to zones ---
        var allCompanies = ExtractCandidates(text, FieldType.CompanyName);
        var sellerCompanies = sellerZone != null
            ? allCompanies.Where(c => c.Position >= sellerZone.Start && c.Position <= sellerZone.End).ToList()
            : new();
        var buyerCompanies = buyerZone != null
            ? allCompanies.Where(c => c.Position >= buyerZone.Start && c.Position <= buyerZone.End).ToList()
            : new();

        result.SellerName = sellerCompanies.OrderByDescending(c => c.Score).FirstOrDefault()?.NormalizedValue;
        result.BuyerName = buyerCompanies.OrderByDescending(c => c.Score).FirstOrDefault()?.NormalizedValue;

        // Fallback: positional
        if (result.SellerName == null && result.BuyerName == null && allCompanies.Count > 0)
        {
            var ordered = allCompanies.OrderBy(c => c.Position).ToList();
            result.SellerName = ordered[0].NormalizedValue;
            if (ordered.Count > 1) result.BuyerName = ordered[1].NormalizedValue;
        }
        else if (result.SellerName == null && allCompanies.Count > 0)
        {
            var notInBuyer = allCompanies.Where(c => buyerZone == null || c.Position < buyerZone.Start || c.Position > buyerZone.End).ToList();
            result.SellerName = notInBuyer.OrderByDescending(c => c.Score).FirstOrDefault()?.NormalizedValue;
        }

        // --- Document number: prefer header zone, exclude tax IDs and phone numbers ---
        var docNumberResult = FieldExtractor.ExtractField(FieldType.DocumentNumber, ctx, ZoneType.Header,
            customFilter: c =>
                !Regex.IsMatch(c.NormalizedValue, @"^\d{13}$") &&         // not tax id
                !Regex.IsMatch(c.NormalizedValue, @"^0\d{8,9}$") &&        // not phone
                c.NormalizedValue != result.SellerTaxId &&                 // not our seller tax id
                c.NormalizedValue != result.BuyerTaxId);
        result.DocumentNumber = docNumberResult.Best?.NormalizedValue;

        // --- Date: prefer header zone ---
        var dateResult = FieldExtractor.ExtractField(FieldType.Date, ctx, ZoneType.Header);
        // ตัวแปลงกลาง (ไม่ขึ้นกับ culture ของ process) — เดิม DateTime.TryParse เปล่า ๆ
        // ⇒ th-TH อ่าน ISO เป็นปฏิทินพุทธ (T2-19 · รอบ 190 ข้อ 11)
        if (dateResult.Best != null
            && Accounting.Helpers.ThaiDate.TryParseFlexible(dateResult.Best.NormalizedValue, out var d))
            result.DocumentDate = d;

        // Amounts: use context-keyword scanning to distinguish subtotal/vat/total
        ExtractAmountsByContext(text, zones, result);

        // Cross-validate and fill missing amounts
        var (sub, vat, total) = CrossValidator.FillMissingAmounts(result.SubTotal, result.VatAmount, result.TotalAmount);
        result.SubTotal = sub;
        result.VatAmount = vat;
        result.TotalAmount = total;

        var amountValidation = CrossValidator.ValidateAmounts(sub, vat, total);
        if (!amountValidation.IsValid)
            result.Confidence *= (decimal)amountValidation.ConfidenceAdjustment;

        // --- Per-field confidence ---
        result.FieldConfidence["SellerName"] = result.SellerName != null ? 0.9 : 0;
        result.FieldConfidence["SellerTaxId"] = result.SellerTaxId != null
            ? (FieldPatternLibrary.ValidateThaiTaxId(result.SellerTaxId) ? 1.0 : 0.5) : 0;
        result.FieldConfidence["BuyerName"] = result.BuyerName != null ? 0.85 : 0;
        result.FieldConfidence["BuyerTaxId"] = result.BuyerTaxId != null
            ? (FieldPatternLibrary.ValidateThaiTaxId(result.BuyerTaxId) ? 1.0 : 0.5) : 0;
        result.FieldConfidence["DocumentNumber"] = docNumberResult.Confidence;
        result.FieldConfidence["DocumentDate"] = dateResult.Confidence;
        result.FieldConfidence["TotalAmount"] = result.TotalAmount.HasValue ? 0.9 : 0;

        // --- WHT detection ---
        var whtMatch = Regex.Match(text, @"หัก\s*ณ\s*ที่จ่าย|ภาษี\s*หัก|WHT|W/?T", RegexOptions.IgnoreCase);
        if (whtMatch.Success)
        {
            result.HasWht = true;
            var area = SafeSubstring(text, whtMatch.Index - 30, whtMatch.Length + 60);
            var rateMatch = Regex.Match(area, @"(\d+)\s*%");
            if (rateMatch.Success)
            {
                var rate = int.Parse(rateMatch.Groups[1].Value);
                if (rate is 1 or 2 or 3 or 5 or 10 or 15) result.WhtRate = rate;
            }
        }

        // --- Payment terms ---
        var termsMatch = Regex.Match(text, @"(?:ชำระ|จ่าย).*?(?:ภายใน|within)\s*(\d+)\s*(?:วัน|days)", RegexOptions.IgnoreCase);
        if (termsMatch.Success)
            result.PaymentTermsDays = int.Parse(termsMatch.Groups[1].Value);

        // --- Reasoning trace ---
        result.ReasoningTrace.Add($"TaxIDs found: {allTaxIds.Count}, valid: {allTaxIds.Count(c => c.IsChecksumValid)}");
        result.ReasoningTrace.Add($"Companies found: {allCompanies.Count}");
        result.ReasoningTrace.Add($"DocNumber: {docNumberResult.Reasoning}");
        result.ReasoningTrace.Add($"Date: {dateResult.Reasoning}");
        if (amountValidation.Issues.Count > 0)
            result.ReasoningTrace.AddRange(amountValidation.Issues.Select(i => "Amount: " + i));

        // --- GL suggestions ---
        AssignAccountSuggestions(result);
    }

    /// <summary>
    /// Extract subtotal/vat/total by scanning context keywords — more reliable
    /// than just picking the largest amount.
    /// </summary>
    static void ExtractAmountsByContext(string text, List<TextZone> zones, ZoneAnalysisResult result)
    {
        var summaryZone = zones.FirstOrDefault(z => z.Type == ZoneType.Summary);
        var searchText = summaryZone?.Text ?? text;

        // Total: look for "รวมทั้งสิ้น" or "GRAND TOTAL" or "NET TOTAL"
        var totalMatch = Regex.Match(searchText,
            @"(?:รวมทั้งสิ้น|รวมเงินทั้งสิ้น|ยอดรวมสุทธิ|จำนวนเงินสุทธิ|GRAND\s*TOTAL|NET\s*TOTAL|รวมเงิน(?=\s|$))\s*[:：]?\s*([\d,]+\.\d{2})",
            RegexOptions.IgnoreCase);
        if (totalMatch.Success)
            result.TotalAmount = ParseAmount(totalMatch.Groups[1].Value);

        // VAT: "ภาษีมูลค่าเพิ่ม 7%" / "VAT 7%"
        var vatMatch = Regex.Match(searchText,
            @"(?:ภาษีมูลค่าเพิ่ม|VAT|Vat)(?:\s*\(?\s*7\s*%?\)?)?\s*[:：]?\s*([\d,]+\.\d{2})",
            RegexOptions.IgnoreCase);
        if (vatMatch.Success)
            result.VatAmount = ParseAmount(vatMatch.Groups[1].Value);

        // SubTotal: "ราคาสินค้า" / "ก่อนภาษี" / "Sub Total"
        var subMatch = Regex.Match(searchText,
            @"(?:ราคาสินค้า|ก่อนภาษี|มูลค่าก่อนภาษี|รวมก่อนภาษี|SUB\s*TOTAL|Subtotal)\s*[:：]?\s*([\d,]+\.\d{2})",
            RegexOptions.IgnoreCase);
        if (subMatch.Success)
            result.SubTotal = ParseAmount(subMatch.Groups[1].Value);

        // If still missing, try full text
        if (result.TotalAmount == null && summaryZone != null)
        {
            totalMatch = Regex.Match(text,
                @"(?:รวมทั้งสิ้น|GRAND\s*TOTAL|TOTAL)\s*[:：]?\s*([\d,]+\.\d{2})",
                RegexOptions.IgnoreCase);
            if (totalMatch.Success)
                result.TotalAmount = ParseAmount(totalMatch.Groups[1].Value);
        }
    }

    static string? ExtractCompanyName(string zoneText)
    {
        var pattern = @"(บริษัท|ห้างหุ้นส่วน(?:จำกัด|สามัญ)?|ร้าน)\s*(.+?)(?:\s*จำกัด(?:\s*\(มหาชน\))?|\s*\(|(?=\s*เลข|\s*สาขา|\s*ที่อยู่|\s*\d{13})|$)";
        var match = Regex.Match(zoneText, pattern, RegexOptions.Multiline);
        if (!match.Success) return null;

        var prefix = match.Groups[1].Value;
        var name = match.Groups[2].Value.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2) return null;

        var afterCtx = SafeSubstring(zoneText, match.Index, match.Length + 30);
        var suffix = "";
        if (afterCtx.Contains("จำกัด"))
            suffix = afterCtx.Contains("มหาชน") ? " จำกัด (มหาชน)" : " จำกัด";

        return $"{prefix} {name}{suffix}".Trim();
    }

    static string? ExtractTaxId(string zoneText)
    {
        var match = Regex.Match(zoneText, Accounting.Helpers.ThaiTaxId.Pattern);
        if (!match.Success) return null;
        var cleaned = Regex.Replace(match.Groups[1].Value, @"[-\s]", "");
        return cleaned.Length == 13 ? cleaned : null;
    }

    static string? ExtractDocNumber(string zoneText)
    {
        string[] patterns = {
            @"เลขที่\s*[:：]?\s*([A-Za-z0-9\-/]+\d+)",
            @"(?:No|เลข(?:ที่)?)\s*\.?\s*[:：]?\s*([A-Za-z0-9\-/]+)",
            @"(?:INV|REC|TAX|TX|IV|PO|CN|DN)[\-/]?\s*(\d[\d\-/]*)",
        };
        foreach (var p in patterns)
        {
            var m = Regex.Match(zoneText, p, RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    static DateTime? ExtractDate(string zoneText)
    {
        string[] patterns = {
            @"(?:วันที่|Date)\s*[:：]?\s*(\d{1,2})\s*[/\-\.]\s*(\d{1,2})\s*[/\-\.]\s*(\d{2,4})",
            @"(\d{1,2})\s*[/\-\.]\s*(\d{1,2})\s*[/\-\.]\s*(\d{4})",
            @"(\d{1,2})\s+(ม\.?ค\.?|ก\.?พ\.?|มี\.?ค\.?|เม\.?ย\.?|พ\.?ค\.?|มิ\.?ย\.?|ก\.?ค\.?|ส\.?ค\.?|ก\.?ย\.?|ต\.?ค\.?|พ\.?ย\.?|ธ\.?ค\.?)\s+(\d{4})",
            @"(\d{1,2})\s*[/\-\.]\s*(\d{1,2})\s*[/\-\.]\s*(\d{2})\b",
        };
        foreach (var p in patterns)
        {
            var m = Regex.Match(zoneText, p);
            if (!m.Success) continue;
            int day = int.Parse(m.Groups[1].Value);
            string monthStr = m.Groups[2].Value;
            int year = int.Parse(m.Groups[3].Value);
            if (year > 2500) year -= 543;
            if (year < 100) year += 2000;
            int month;
            if (!int.TryParse(monthStr, out month))
            {
                var thaiMonths = new Dictionary<string, int>
                {
                    {"ม.ค",1},{"มค",1},{"ก.พ",2},{"กพ",2},{"มี.ค",3},{"มีค",3},
                    {"เม.ย",4},{"เมย",4},{"พ.ค",5},{"พค",5},{"มิ.ย",6},{"มิย",6},
                    {"ก.ค",7},{"กค",7},{"ส.ค",8},{"สค",8},{"ก.ย",9},{"กย",9},
                    {"ต.ค",10},{"ตค",10},{"พ.ย",11},{"พย",11},{"ธ.ค",12},{"ธค",12},
                };
                month = 1;
                foreach (var (key, val) in thaiMonths)
                    if (monthStr.Contains(key)) { month = val; break; }
            }
            if (day >= 1 && day <= 31 && month >= 1 && month <= 12 && year >= 1900)
            {
                try { return new DateTime(year, month, day); } catch { }
            }
        }
        return null;
    }

    static decimal? ExtractTotal(string zoneText)
    {
        string[] patterns = {
            @"(?:รวม(?:เงิน)?(?:ทั้งสิ้น|ทั้งหมด|สุทธิ)|ยอดรวม(?:สุทธิ)?|GRAND\s*TOTAL|NET\s*TOTAL)\s*[:：]?\s*([\d,]+\.?\d*)",
            @"(?:TOTAL)\s*[:：]?\s*([\d,]+\.?\d*)",
            @"(?:รวมเงิน|จำนวนเงินรวม)\s*[:：]?\s*([\d,]+\.?\d*)",
        };
        foreach (var p in patterns)
        {
            var m = Regex.Match(zoneText, p, RegexOptions.IgnoreCase);
            if (m.Success) { var v = ParseAmount(m.Groups[1].Value); if (v > 0) return v; }
        }
        return null;
    }

    static decimal? ExtractVat(string zoneText)
    {
        var m = Regex.Match(zoneText, @"(?:ภาษีมูลค่าเพิ่ม|VAT|Vat)\s*(?:7\s*%?)?\s*[:：]?\s*([\d,]+\.?\d*)", RegexOptions.IgnoreCase);
        return m.Success ? ParseAmount(m.Groups[1].Value) : null;
    }

    static decimal? ExtractSubTotal(string zoneText)
    {
        var m = Regex.Match(zoneText, @"(?:ราคาสินค้า|ก่อนภาษี|รวมเงิน(?!ทั้ง)|SUB\s*TOTAL|Subtotal|ราคารวม)\s*[:：]?\s*([\d,]+\.?\d*)", RegexOptions.IgnoreCase);
        return m.Success ? ParseAmount(m.Groups[1].Value) : null;
    }

    static void FallbackCompanyExtraction(string text, ZoneAnalysisResult result)
    {
        var pattern = @"(บริษัท|ห้างหุ้นส่วน(?:จำกัด|สามัญ)?|ร้าน)\s*(.+?)(?:\s*จำกัด(?:\s*\(มหาชน\))?|\s*\(|(?=\s*เลข|\s*สาขา|\s*ที่อยู่)|$)";
        var matches = Regex.Matches(text, pattern, RegexOptions.Multiline);
        var companies = new List<(string Name, int Pos)>();
        foreach (Match cm in matches)
        {
            var name = ExtractCompanyNameFromMatch(cm, text);
            if (name != null) companies.Add((name, cm.Index));
        }

        var taxIdPattern = Accounting.Helpers.ThaiTaxId.Pattern;
        var taxIds = Regex.Matches(text, taxIdPattern)
            .Cast<Match>()
            .Select(m => (Id: Regex.Replace(m.Groups[1].Value, @"[-\s]", ""), Pos: m.Index))
            .Where(x => x.Id.Length == 13)
            .ToList();

        // First company = seller (issuer), second = buyer
        if (companies.Count >= 2)
        {
            result.SellerName = companies[0].Name;
            result.BuyerName = companies[1].Name;
            if (taxIds.Count >= 2)
            {
                var sorted = taxIds.OrderBy(t => t.Pos).ToList();
                result.SellerTaxId = sorted[0].Id;
                result.BuyerTaxId = sorted[1].Id;
            }
            else if (taxIds.Count == 1)
            {
                // Tax ID closer to which company?
                var tid = taxIds[0];
                if (Math.Abs(tid.Pos - companies[0].Pos) < Math.Abs(tid.Pos - companies[1].Pos))
                    result.SellerTaxId = tid.Id;
                else
                    result.BuyerTaxId = tid.Id;
            }
        }
        else if (companies.Count == 1)
        {
            result.SellerName = companies[0].Name;
            result.SellerTaxId = taxIds.Count > 0 ? taxIds[0].Id : null;
        }
        else if (taxIds.Count > 0)
        {
            result.SellerTaxId = taxIds[0].Id;
        }
    }

    static string? ExtractCompanyNameFromMatch(Match cm, string fullText)
    {
        var prefix = cm.Groups[1].Value;
        var name = cm.Groups[2].Value.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2) return null;
        var afterCtx = SafeSubstring(fullText, cm.Index, cm.Length + 30);
        var suffix = "";
        if (afterCtx.Contains("จำกัด"))
            suffix = afterCtx.Contains("มหาชน") ? " จำกัด (มหาชน)" : " จำกัด";
        return $"{prefix} {name}{suffix}".Trim();
    }

    static void AssignAccountSuggestions(ZoneAnalysisResult r)
    {
        var map = new Dictionary<string, (string Cat, string Dc, string Dn, string Cc, string Cn)>
        {
            ["TaxInvoice"] = ("ค่าสินค้า", "5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า"),
            ["Invoice"] = ("ค่าสินค้า", "5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า"),
            ["Receipt"] = ("ค่าบริการ", "5300", "ค่าใช้จ่ายบริหาร", "1110", "เงินสด"),
            ["PurchaseOrder"] = ("ค่าสินค้า", "1200", "สินค้าคงเหลือ", "2100", "เจ้าหนี้การค้า"),
            ["WHT"] = ("อื่นๆ", "2170", "ภาษีหัก ณ ที่จ่าย", "1110", "เงินสด"),
            ["CreditNote"] = ("ค่าสินค้า", "2100", "เจ้าหนี้การค้า", "5100", "ต้นทุนขาย"),
            ["DebitNote"] = ("ค่าสินค้า", "5100", "ต้นทุนขาย", "2100", "เจ้าหนี้การค้า"),
        };
        if (r.DocumentType != null && map.TryGetValue(r.DocumentType, out var a))
            r.ExpenseCategory = a.Cat;
    }

    // === Learning ===

    /// <summary>
    /// เติมช่องที่ยัง**ว่าง**ใน <see cref="OcrExtractedData"/> ด้วยแพตเทิร์นที่
    /// ระบบเรียนจากการแก้ของผู้ใช้ (และจากผลสแกน Azure) — จุดอ่านของตาราง
    /// <c>OcrLearnedPatterns</c>
    ///
    /// ═══ ที่มา (defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้") ═══
    /// <c>OcrLearnedPattern</c> ถูก**เขียน**ทุกครั้งที่ผู้ใช้แก้ผลสแกน
    /// (<c>OcrService.LearnFromCorrection</c>) และทุกครั้งที่สแกนผ่าน Azure DI
    /// (<c>AzureDiPatternLearner</c> ที่ doc-comment ของตัวเองเขียนว่า
    /// "DocumentZoneAnalyzer.ApplyLearnedPatterns picks them up") — แต่
    /// <see cref="Analyze"/> ซึ่งเป็นทางเข้าเดียวที่อ่านตารางนี้
    /// <b>ไม่มีใครเรียกเลยทั้งเรพ</b> ⇒ เป็น write-only store มาตลอด:
    /// จ่ายค่าเขียนทุกการแก้ แต่ความแม่นไม่เคยดีขึ้นเลยสักครั้ง
    ///
    /// ═══ กติกา ═══
    /// <list type="bullet">
    /// <item>เติมเฉพาะช่องที่เป็น null/ว่าง — <b>ห้ามทับค่าที่ engine อ่านได้</b>
    ///   (แพตเทิร์นเป็นตัวช่วยเติมช่องว่าง ไม่ใช่ตัวตัดสินที่ชนะ OCR)</item>
    /// <item>ข้าม negative example (<c>IsNegativeExample</c>) — แถวพวกนั้นบอกว่า
    ///   "ค่านี้เคยผิด" ไม่ใช่ "ค่านี้ถูก"</item>
    /// <item>ค่าที่เติมมาต้องผ่านด่านความสมเหตุผลของชนิดข้อมูลก่อน
    ///   (เลขภาษี 13 หลัก + checksum · ยอดเงิน &gt; 0)</item>
    /// </list>
    /// คืนชื่อช่องที่เติมได้ ไว้ให้ผู้เรียกเขียนลง ReasoningTrace
    /// </summary>
    internal static List<string> ApplyLearnedPatternsTo(
        OcrExtractedData data, string? rawText, IReadOnlyList<OcrLearnedPattern> patterns)
    {
        var filled = new List<string>();
        if (string.IsNullOrWhiteSpace(rawText) || patterns.Count == 0) return filled;

        foreach (var p in patterns.Where(x => !x.IsNegativeExample)
                                  .OrderByDescending(x => x.TimesConfirmed))
        {
            if (string.IsNullOrEmpty(p.ContextKeyword) || string.IsNullOrEmpty(p.ExtractionRegex))
                continue;

            // ช่องนี้มีค่าแล้ว = ไม่ต้องทำงาน (ประหยัด regex + กันทับ)
            var alreadyHas = p.FieldName switch
            {
                "SellerName" => !string.IsNullOrWhiteSpace(data.VendorName),
                "SellerTaxId" => !string.IsNullOrWhiteSpace(data.VendorTaxId),
                "BuyerName" => !string.IsNullOrWhiteSpace(data.BuyerName),
                "BuyerTaxId" => !string.IsNullOrWhiteSpace(data.BuyerTaxId),
                "DocumentNumber" => !string.IsNullOrWhiteSpace(data.DocumentNumber),
                "TotalAmount" => data.TotalAmount is > 0,
                _ => true,     // ช่องที่ยังไม่รองรับ — ข้าม
            };
            if (alreadyHas) continue;

            int kwPos = rawText.IndexOf(p.ContextKeyword, StringComparison.OrdinalIgnoreCase);
            if (kwPos < 0) continue;

            string value;
            try
            {
                var searchArea = SafeSubstring(rawText, kwPos - 50,
                    p.SearchRadius > 0 ? p.SearchRadius : 300);
                var m = Regex.Match(searchArea, p.ExtractionRegex,
                    RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
                if (!m.Success) continue;
                value = (m.Groups.Count > 1 ? m.Groups[1].Value : m.Value).Trim();
            }
            catch (RegexMatchTimeoutException) { continue; }
            catch (ArgumentException) { continue; }   // regex ที่เก็บไว้เสีย
            if (string.IsNullOrWhiteSpace(value)) continue;

            switch (p.FieldName)
            {
                case "SellerName":
                    data.VendorName = value; filled.Add("ชื่อผู้ขาย"); break;
                case "SellerTaxId":
                    if (!Accounting.Helpers.ThaiTaxId.IsValid(value)) continue;
                    data.VendorTaxId = Accounting.Helpers.ThaiTaxId.Normalize(value);
                    filled.Add("เลขผู้เสียภาษีผู้ขาย"); break;
                case "BuyerName":
                    data.BuyerName = value; filled.Add("ชื่อผู้ซื้อ"); break;
                case "BuyerTaxId":
                    if (!Accounting.Helpers.ThaiTaxId.IsValid(value)) continue;
                    data.BuyerTaxId = Accounting.Helpers.ThaiTaxId.Normalize(value);
                    filled.Add("เลขผู้เสียภาษีผู้ซื้อ"); break;
                case "DocumentNumber":
                    data.DocumentNumber = value; filled.Add("เลขที่เอกสาร"); break;
                case "TotalAmount":
                    if (decimal.TryParse(value.Replace(",", ""),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var ta)
                        && ta > 0)
                    { data.TotalAmount = ta; filled.Add("ยอดรวม"); }
                    break;
            }
        }
        return filled;
    }

    static void ApplyLearnedPatterns(string text, ZoneAnalysisResult result, List<OcrLearnedPattern> patterns)
    {
        foreach (var p in patterns.OrderByDescending(p => p.TimesConfirmed))
        {
            if (string.IsNullOrEmpty(p.ContextKeyword)) continue;

            // Find the context keyword in text
            int kwPos = text.IndexOf(p.ContextKeyword, StringComparison.OrdinalIgnoreCase);
            if (kwPos < 0) continue;

            // Look for the extraction pattern near the keyword
            if (!string.IsNullOrEmpty(p.ExtractionRegex))
            {
                var searchArea = SafeSubstring(text, kwPos - 50, p.SearchRadius > 0 ? p.SearchRadius : 300);
                var m = Regex.Match(searchArea, p.ExtractionRegex, RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var value = m.Groups.Count > 1 ? m.Groups[1].Value.Trim() : m.Value.Trim();
                if (string.IsNullOrEmpty(value)) continue;

                switch (p.FieldName)
                {
                    case "SellerName": result.SellerName ??= value; break;
                    case "SellerTaxId": result.SellerTaxId ??= value; break;
                    case "BuyerName": result.BuyerName ??= value; break;
                    case "BuyerTaxId": result.BuyerTaxId ??= value; break;
                    case "DocumentNumber": result.DocumentNumber ??= value; break;
                    case "TotalAmount":
                        if (result.TotalAmount == null && decimal.TryParse(value.Replace(",", ""), out var ta))
                            result.TotalAmount = ta;
                        break;
                }
                result.Confidence = Math.Min(1m, result.Confidence + 0.02m);
            }
        }
    }

    /// <summary>
    /// Learn patterns from a user correction by finding where corrected values
    /// appear in the raw text and what keywords are nearby. Also record negative
    /// examples for the previously-extracted (wrong) values.
    /// </summary>
    public static List<OcrLearnedPattern> LearnFromCorrection(
        string rawText, Guid companyId,
        string? vendorTaxId,
        string? correctedVendorName, string? correctedTaxId,
        string? correctedDocNumber, decimal? correctedTotal,
        // Previous (wrong) values — used to record negative examples
        string? previousVendorName = null, string? previousTaxId = null,
        string? previousDocNumber = null)
    {
        var patterns = new List<OcrLearnedPattern>();
        if (string.IsNullOrWhiteSpace(rawText)) return patterns;

        // POSITIVE examples: the corrected value is the right one
        if (!string.IsNullOrEmpty(correctedVendorName))
        {
            var learned = FindContextForValue(rawText, correctedVendorName, "SellerName", companyId, vendorTaxId);
            if (learned != null) patterns.Add(learned);
        }
        if (!string.IsNullOrEmpty(correctedTaxId) && correctedTaxId.Length == 13)
        {
            var learned = FindContextForValue(rawText, correctedTaxId, "SellerTaxId", companyId, vendorTaxId);
            if (learned != null) patterns.Add(learned);
        }
        if (!string.IsNullOrEmpty(correctedDocNumber))
        {
            var learned = FindContextForValue(rawText, correctedDocNumber, "DocumentNumber", companyId, vendorTaxId);
            if (learned != null) patterns.Add(learned);
        }

        // NEGATIVE examples: previous values that the user corrected away from
        if (!string.IsNullOrEmpty(previousVendorName) && previousVendorName != correctedVendorName)
            patterns.Add(BuildNegativePattern(companyId, vendorTaxId, "SellerName", previousVendorName));
        if (!string.IsNullOrEmpty(previousTaxId) && previousTaxId != correctedTaxId)
            patterns.Add(BuildNegativePattern(companyId, vendorTaxId, "SellerTaxId", previousTaxId));
        if (!string.IsNullOrEmpty(previousDocNumber) && previousDocNumber != correctedDocNumber)
            patterns.Add(BuildNegativePattern(companyId, vendorTaxId, "DocumentNumber", previousDocNumber));

        return patterns;
    }

    static OcrLearnedPattern BuildNegativePattern(Guid companyId, string? vendorTaxId, string fieldName, string wrongValue)
        => new()
        {
            CompanyId = companyId,
            VendorTaxId = vendorTaxId,
            FieldName = fieldName,
            ContextKeyword = "(negative)",
            IsNegativeExample = true,
            NegativeValue = wrongValue,
            FailureCount = 1,
        };

    /// <summary>แปลงค่าเป็น regex ตามรูปทรง: กลุ่มตัวเลข → <c>\d{n,n+1}</c> · กลุ่มตัวอักษร →
    /// ตัวจริง (ไม่สนตัวพิมพ์) · เครื่องหมาย → ตามตัว · ขอบหน้า/หลังต้องไม่ใช่ตัวอักษร/เลข</summary>
    internal static string ShapeRegex(string value)
    {
        var sb = new System.Text.StringBuilder("(?<![A-Za-z0-9])");
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            var j = i;
            if (char.IsDigit(c)) { while (j < value.Length && char.IsDigit(value[j])) j++; sb.Append($"\\d{{{j - i},{j - i + 1}}}"); }
            // ตัวอักษร = "ชุด" ของเลขที่ (INV vs REC คือคนละเอกสาร) ⇒ ล็อกเป็นตัวจริง ไม่ใช่ class
            // (ยอมต่างตัวพิมพ์ใหญ่-เล็ก เพราะ OCR สลับได้)
            else if (char.IsLetter(c)) { while (j < value.Length && char.IsLetter(value[j])) j++; sb.Append("(?i:").Append(Regex.Escape(value[i..j])).Append(')'); }
            else { j = i + 1; sb.Append(Regex.Escape(c.ToString())); }
            i = j;
        }
        return sb.Append("(?![A-Za-z0-9])").ToString();
    }

    static OcrLearnedPattern? FindContextForValue(string text, string value, string fieldName,
        Guid companyId, string? vendorTaxId)
    {
        int valuePos = text.IndexOf(value, StringComparison.OrdinalIgnoreCase);
        if (valuePos < 0) return null;

        // Look backwards for the nearest keyword that provides context
        var contextArea = SafeSubstring(text, valuePos - 200, 200);
        string? bestKeyword = null;
        int bestDist = int.MaxValue;

        var allKeywords = ZoneDefinitions.Values
            .SelectMany(d => d.Keywords)
            .Concat(new[] { "เลขที่", "No.", "วันที่", "Date", "เลขผู้เสียภาษี", "เลขประจำตัว", "Tax ID" });

        foreach (var kw in allKeywords)
        {
            int kwPos = contextArea.LastIndexOf(kw, StringComparison.OrdinalIgnoreCase);
            if (kwPos >= 0)
            {
                int dist = contextArea.Length - kwPos;
                if (dist < bestDist) { bestDist = dist; bestKeyword = kw; }
            }
        }

        if (bestKeyword == null) return null;

        return new OcrLearnedPattern
        {
            CompanyId = companyId,
            VendorTaxId = vendorTaxId,
            FieldName = fieldName,
            ContextKeyword = bestKeyword,
            ExtractionRegex = BuildExtractionRegex(value),
            SearchRadius = bestDist + 100,
            TimesConfirmed = 1,
        };
    }

    /// <summary>regex สำหรับดึงค่าชนิดเดียวกันจากใบถัดไปของผู้ขายรายเดิม
    ///
    /// <para>⚠️ เดิมเลขที่เอกสารได้ regex กวาดทุกอย่าง <c>([A-Za-z0-9\-/]+)</c> ⇒ บนสแกน
    /// รอบถัดไปมันคว้า token แรกที่เจอในรัศมีค้น ("CASHSALE" หัวแบบฟอร์ม) มาเป็นเลขที่
    /// เอกสาร (สแกนจริง 2026-09-11). เลขที่เอกสารของผู้ขายรายเดียวกันมี<b>รูปทรงคงที่</b>
    /// (INV-2026-0042 → ตัวอักษร 3 · ขีด · เลข 4 · ขีด · เลข 4) ⇒ สร้าง regex จากรูปทรง
    /// ของค่าที่เรียน: ตัวอักษรตามจำนวนเดิม · ตัวเลขยอมให้ยาวขึ้นได้หนึ่งหลัก (เลขรันเกินหลัก)</para></summary>
    internal static string BuildExtractionRegex(string value)
    {
        if (Regex.IsMatch(value, @"^\d{13}$"))
            return Accounting.Helpers.ThaiTaxId.Pattern;
        if (Regex.IsMatch(value, @"^[A-Za-z0-9\-/]+$"))
            return "(" + ShapeRegex(value) + ")";
        if (value.Contains("บริษัท") || value.Contains("ห้างหุ้นส่วน") || value.Contains("ร้าน"))
            return @"((?:บริษัท|ห้างหุ้นส่วน|ร้าน).+?(?:จำกัด(?:\s*\(มหาชน\))?|$))";
        return Regex.Escape(value);
    }

    static string BuildZoneSummary(List<TextZone> zones, string fullText)
    {
        if (zones.Count == 0) return "ไม่พบ zone keyword ในข้อความ — ใช้ fallback extraction";
        var lines = zones.Select(z =>
            $"[{z.Type}] pos:{z.Start}-{z.End} keywords:[{string.Join(",", z.MatchedKeywords)}] " +
            $"text:\"{Truncate(z.Text.Replace("\n", " ").Replace("\r", ""), 100)}\"");
        return string.Join("\n", lines);
    }

    static decimal? ParseAmount(string val)
    {
        var cleaned = val.Replace(",", "").Trim();
        return decimal.TryParse(cleaned, out var r) && r > 0 ? r : null;
    }

    static string SafeSubstring(string text, int start, int length)
    {
        start = Math.Max(0, start);
        length = Math.Min(length, text.Length - start);
        return length > 0 ? text.Substring(start, length) : "";
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}
