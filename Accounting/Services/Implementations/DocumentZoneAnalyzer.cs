using System.Text.RegularExpressions;
using Accounting.Models.Entities;

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
    public static ZoneAnalysisResult Analyze(string text, List<OcrLearnedPattern>? learnedPatterns = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new ZoneAnalysisResult { DocumentType = "Receipt", Confidence = 0.3m };

        // Step 1: Detect zones by finding keywords and their surrounding context
        var zones = DetectZones(text);

        // Step 2: Extract document type from header zone or full text
        var result = new ZoneAnalysisResult { Zones = zones };
        DetectDocumentType(text, result);

        // Step 3: Extract fields from each zone
        ExtractFromZones(text, zones, result);

        // Step 4: Apply learned patterns to override or fill gaps
        if (learnedPatterns?.Count > 0)
            ApplyLearnedPatterns(text, result, learnedPatterns);

        // Step 5: Calculate missing amounts
        CalculateMissingAmounts(result);

        // Step 6: Build zone summary for debugging
        result.ZoneSummary = BuildZoneSummary(zones, text);

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

    static void ExtractFromZones(string text, List<TextZone> zones, ZoneAnalysisResult result)
    {
        // --- Seller zone: extract company name + tax ID ---
        var sellerZone = zones.FirstOrDefault(z => z.Type == ZoneType.Seller);
        if (sellerZone != null)
        {
            result.SellerName = ExtractCompanyName(sellerZone.Text);
            result.SellerTaxId = ExtractTaxId(sellerZone.Text);
            result.Confidence += 0.05m; // boost confidence if we found seller zone
        }

        // --- Buyer zone: extract company name + tax ID ---
        var buyerZone = zones.FirstOrDefault(z => z.Type == ZoneType.Buyer);
        if (buyerZone != null)
        {
            result.BuyerName = ExtractCompanyName(buyerZone.Text);
            result.BuyerTaxId = ExtractTaxId(buyerZone.Text);
        }

        // --- If no explicit seller/buyer zones, use positional fallback ---
        if (sellerZone == null && buyerZone == null)
        {
            FallbackCompanyExtraction(text, result);
        }
        else if (sellerZone == null && buyerZone != null)
        {
            // We know the buyer but not seller — look for company info outside buyer zone
            var outsideBuyer = text[..buyerZone.Start] +
                (buyerZone.End < text.Length ? text[buyerZone.End..] : "");
            result.SellerName = ExtractCompanyName(outsideBuyer);
            result.SellerTaxId = ExtractTaxId(outsideBuyer);
        }
        else if (sellerZone != null && buyerZone == null)
        {
            var outsideSeller = text[..sellerZone.Start] +
                (sellerZone.End < text.Length ? text[sellerZone.End..] : "");
            result.BuyerName = ExtractCompanyName(outsideSeller);
            result.BuyerTaxId = ExtractTaxId(outsideSeller);
        }

        // --- Header zone: document number + date ---
        var headerZone = zones.FirstOrDefault(z => z.Type == ZoneType.Header);
        var headerText = headerZone?.Text ?? text;

        result.DocumentNumber = ExtractDocNumber(headerText);
        if (result.DocumentNumber == null && headerZone != null)
            result.DocumentNumber = ExtractDocNumber(text);

        result.DocumentDate = ExtractDate(headerText);
        if (!result.DocumentDate.HasValue && headerZone != null)
            result.DocumentDate = ExtractDate(text);

        // --- Summary zone: amounts ---
        var summaryZone = zones.FirstOrDefault(z => z.Type == ZoneType.Summary);
        var summaryText = summaryZone?.Text ?? text;

        result.TotalAmount = ExtractTotal(summaryText);
        result.VatAmount = ExtractVat(summaryText);
        result.SubTotal = ExtractSubTotal(summaryText);

        // If amounts not found in summary zone, try full text
        if (result.TotalAmount == null) result.TotalAmount = ExtractTotal(text);
        if (result.VatAmount == null) result.VatAmount = ExtractVat(text);
        if (result.SubTotal == null) result.SubTotal = ExtractSubTotal(text);

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

        // --- GL suggestions ---
        AssignAccountSuggestions(result);
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
        var match = Regex.Match(zoneText, @"(\d{1}[-\s]?\d{4}[-\s]?\d{5}[-\s]?\d{2}[-\s]?\d{1})");
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

        var taxIdPattern = @"(\d{1}[-\s]?\d{4}[-\s]?\d{5}[-\s]?\d{2}[-\s]?\d{1})";
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

    static void CalculateMissingAmounts(ZoneAnalysisResult r)
    {
        bool isTaxDoc = r.DocumentType is "TaxInvoice" or "Invoice" or "CreditNote" or "DebitNote";
        if (r.TotalAmount > 0 && r.VatAmount > 0 && r.SubTotal == null)
            r.SubTotal = r.TotalAmount - r.VatAmount;
        else if (r.SubTotal > 0 && r.VatAmount > 0 && r.TotalAmount == null)
            r.TotalAmount = r.SubTotal + r.VatAmount;
        else if (r.TotalAmount > 0 && r.SubTotal == null && r.VatAmount == null && isTaxDoc)
        {
            r.SubTotal = Math.Round(r.TotalAmount.Value / 1.07m, 2);
            r.VatAmount = r.TotalAmount.Value - r.SubTotal.Value;
        }
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
    /// appear in the raw text and what keywords are nearby.
    /// </summary>
    public static List<OcrLearnedPattern> LearnFromCorrection(
        string rawText, Guid companyId,
        string? vendorTaxId,
        string? correctedVendorName, string? correctedTaxId,
        string? correctedDocNumber, decimal? correctedTotal)
    {
        var patterns = new List<OcrLearnedPattern>();
        if (string.IsNullOrWhiteSpace(rawText)) return patterns;

        // Find where each corrected value appears in the text
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

        return patterns;
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

    static string BuildExtractionRegex(string value)
    {
        if (Regex.IsMatch(value, @"^\d{13}$"))
            return @"(\d{1}[-\s]?\d{4}[-\s]?\d{5}[-\s]?\d{2}[-\s]?\d{1})";
        if (Regex.IsMatch(value, @"^[A-Za-z0-9\-/]+$"))
            return @"([A-Za-z0-9\-/]+)";
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
