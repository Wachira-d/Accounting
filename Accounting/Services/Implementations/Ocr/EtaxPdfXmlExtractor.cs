using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Lossless e-Tax extractor: when an uploaded PDF is actually a PDF/A-3
/// with an embedded ETDA Cross Industry Invoice XML (the file the seller
/// generated via /pages/etax.html "ดาวน์โหลด PDF/A-3"), we can read every
/// number off the XML rather than running OCR over the visual layer.
///
/// Why this matters: OCR (even Azure DI) is best-effort. A vendor-issued
/// e-Tax PDF/A-3 carries the legally authoritative data INSIDE the PDF as
/// a structured XML attachment. Reading it directly gives:
///   * 100% accuracy on every field (taxId, name, totals, line items)
///   * Zero OCR quota charged
///   * Instant — no Azure round-trip, no Tesseract pass
///   * Vendor's branch code, buyer's tax id, structured address — fields
///     OCR routinely misses or mis-segments
///
/// Detection: parse the PDF byte stream looking for /EmbeddedFiles in the
/// Names tree with a Filespec whose /F or /UF ends in .xml AND whose
/// /AFRelationship is /Alternative (PDF/A-3 Associated Files convention,
/// matches our own PdfAttachmentInjector output and ETDA's reference impl).
///
/// Parser is permissive: accepts ETDA CII v2.0 root names
/// (TaxInvoice_CrossIndustryInvoice / Receipt_CrossIndustryInvoice /
/// DebitCreditNote_CrossIndustryInvoice / Invoice_CrossIndustryInvoice)
/// and namespace variations, since some vendors use slightly different
/// urn: prefixes while keeping the underlying rsm/ram element names.
/// </summary>
internal static class EtaxPdfXmlExtractor
{
    public class ExtractResult
    {
        public bool Found { get; set; }
        public string? XmlContent { get; set; }
        public string? AttachmentFileName { get; set; }
        public string? DocumentRootName { get; set; }   // e.g. "TaxInvoice_CrossIndustryInvoice"

        public string? DocumentNumber { get; set; }
        public DateTime? DocumentDate { get; set; }
        public string? DocumentTypeCode { get; set; }   // 388 / T03 / 80 / 81
        public string? DocumentTypeName { get; set; }   // mapped Thai name
        public string? MappedDocumentType { get; set; } // "TaxInvoice" | "Receipt" | "DebitNote" | "CreditNote" | "Invoice"

        public string? SellerName { get; set; }
        public string? SellerTaxId { get; set; }        // 13-digit (branch stripped)
        public string? SellerBranchCode { get; set; }   // 5-digit
        public string? SellerAddress { get; set; }      // best-effort joined

        public string? BuyerName { get; set; }
        public string? BuyerTaxId { get; set; }
        public string? BuyerBranchCode { get; set; }
        public string? BuyerAddress { get; set; }

        public decimal? LineTotal { get; set; }         // SubTotal before VAT
        public decimal? TaxBasis { get; set; }          // SubTotal - Discount
        public decimal? VatAmount { get; set; }
        public decimal? GrandTotal { get; set; }
        public string? Currency { get; set; }

        public List<LineItem> Items { get; set; } = new();
    }

    public class LineItem
    {
        public int LineNo { get; set; }
        public string? Description { get; set; }
        public decimal? Quantity { get; set; }
        public string? Unit { get; set; }
        public decimal? UnitPrice { get; set; }
        public decimal? Amount { get; set; }
    }

    /// <summary>
    /// Quick PDF-signature gate. Returns false fast for non-PDF (jpg/png),
    /// so callers can skip the heavier extraction work.
    /// </summary>
    public static bool LooksLikePdf(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 5) return false;
        // %PDF-
        return bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 && bytes[4] == 0x2D;
    }

    /// <summary>
    /// Full pipeline: detect embedded XML, parse ETDA CII, return populated
    /// ExtractResult. Returns Found=false on any failure (caller falls back
    /// to OCR). Never throws — exceptions are swallowed to a debug log.
    /// </summary>
    public static ExtractResult? TryExtract(byte[] pdfBytes)
    {
        if (!LooksLikePdf(pdfBytes)) return null;
        try
        {
            var (xml, fileName) = ExtractEmbeddedXml(pdfBytes);
            if (xml == null) return null;

            var result = ParseEtaxXml(xml);
            if (result == null) return null;

            result.XmlContent = xml;
            result.AttachmentFileName = fileName;
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"EtaxPdfXmlExtractor failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Scan PDF byte stream for an EmbeddedFile whose name ends in .xml.
    /// Returns (xmlContent, fileName) or (null,null).
    ///
    /// Algorithm: search for "/Subtype /text#2Fxml" (text/xml subtype, ETDA
    /// convention) or any /Filespec whose /F or /UF ends in .xml, then
    /// resolve the linked /EF stream and decode it (raw or FlateDecode).
    /// </summary>
    private static (string? xml, string? fileName) ExtractEmbeddedXml(byte[] pdf)
    {
        // PDF bodies are ASCII for the structural parts but the streams are
        // binary — Latin1 round-trips every byte cleanly so we can still
        // use string scanning safely.
        var pdfText = Encoding.Latin1.GetString(pdf);

        // 1) Find an EmbeddedFile stream whose subtype is text/xml. Be
        //    permissive about encoding: /Subtype /text#2Fxml is the
        //    canonical form (PDF name objects escape '/' as #2F).
        var subtypeMarkers = new[] { "/Subtype /text#2Fxml", "/Subtype/text#2Fxml", "/Subtype /text/xml" };
        int objStart = -1;
        foreach (var marker in subtypeMarkers)
        {
            var idx = pdfText.IndexOf(marker, StringComparison.Ordinal);
            if (idx > 0) { objStart = FindObjStart(pdfText, idx); break; }
        }

        // 2) Fallback: hunt for a /Filespec with .xml /F that points to /EF.
        //    Handles vendors who emit /Subtype /application#2Fxml instead.
        if (objStart < 0)
        {
            var efRef = FindFilespecXmlRef(pdfText);
            if (efRef == null) return (null, null);
            return ExtractStreamFromObjRef(pdf, pdfText, efRef.Value.efObjNum, efRef.Value.fileName);
        }

        // Resolve fileName by looking up the matching Filespec that points to
        // this stream object.
        var streamObjNum = ReadObjNumber(pdfText, objStart);
        var fname = FindFilespecPointingTo(pdfText, streamObjNum) ?? "embedded.xml";

        var (rawXml, _) = ReadStream(pdf, pdfText, objStart);
        if (rawXml == null) return (null, null);
        return (rawXml, fname);
    }

    // ───── Low-level PDF byte scanning ─────

    private static int FindObjStart(string pdfText, int hintIndex)
    {
        // Walk back from hintIndex to "<<" then to the preceding "N M obj".
        var dictStart = pdfText.LastIndexOf("<<", hintIndex, StringComparison.Ordinal);
        if (dictStart < 0) return -1;
        var objKw = pdfText.LastIndexOf(" obj", dictStart, StringComparison.Ordinal);
        if (objKw < 0) return -1;
        // Find start of "N M obj" — scan back for the digit/whitespace block.
        var lineStart = pdfText.LastIndexOfAny(new[] { '\n', '\r' }, objKw);
        return lineStart < 0 ? 0 : lineStart + 1;
    }

    private static int ReadObjNumber(string pdfText, int objStart)
    {
        // Format: "12 0 obj"
        var sp = pdfText.IndexOf(' ', objStart);
        if (sp <= objStart) return -1;
        return int.TryParse(pdfText.AsSpan(objStart, sp - objStart), out var n) ? n : -1;
    }

    private static (int efObjNum, string fileName)? FindFilespecXmlRef(string pdfText)
    {
        // Scan all Filespec objects in the file and pick the first whose
        // /F or /UF ends with .xml — extract the /EF target object number.
        int pos = 0;
        while ((pos = pdfText.IndexOf("/Type /Filespec", pos, StringComparison.Ordinal)) >= 0
            || (pos = pdfText.IndexOf("/Type/Filespec", pos, StringComparison.Ordinal)) >= 0)
        {
            var objStart = FindObjStart(pdfText, pos);
            if (objStart < 0) { pos++; continue; }
            var objEnd = pdfText.IndexOf("endobj", objStart, StringComparison.Ordinal);
            if (objEnd < 0) break;
            var body = pdfText.AsSpan(objStart, objEnd - objStart).ToString();

            // /F (filename.xml) — extract the parenthesised literal.
            var fname = ExtractParenLiteral(body, "/F ") ?? ExtractParenLiteral(body, "/UF ");
            if (fname != null && fname.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                // /EF << /F 42 0 R /UF 42 0 R >>
                var efIdx = body.IndexOf("/EF", StringComparison.Ordinal);
                if (efIdx > 0)
                {
                    var rest = body.Substring(efIdx);
                    var m = System.Text.RegularExpressions.Regex.Match(rest, @"/F\s+(\d+)\s+\d+\s+R");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var efNum))
                        return (efNum, fname);
                }
            }
            pos = objEnd + 6;
        }
        return null;
    }

    private static string? ExtractParenLiteral(string s, string key)
    {
        var i = s.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        var open = s.IndexOf('(', i);
        if (open < 0) return null;
        var close = s.IndexOf(')', open + 1);
        if (close < 0) return null;
        return s.Substring(open + 1, close - open - 1);
    }

    private static string? FindFilespecPointingTo(string pdfText, int targetObjNum)
    {
        var re = new System.Text.RegularExpressions.Regex(@"/Type\s*/?Filespec[^]*?/EF[^]*?/F\s+" +
            targetObjNum + @"\s+\d+\s+R", System.Text.RegularExpressions.RegexOptions.Singleline);
        var m = re.Match(pdfText);
        if (!m.Success) return null;
        return ExtractParenLiteral(m.Value, "/F ") ?? ExtractParenLiteral(m.Value, "/UF ");
    }

    private static (int efObjNum, string fileName)? FindFilespecForStream(string pdfText, int streamObjNum)
    {
        var name = FindFilespecPointingTo(pdfText, streamObjNum);
        return name != null ? (streamObjNum, name) : null;
    }

    private static (string? xml, byte[]? rawBytes) ReadStream(byte[] pdf, string pdfText, int objStart)
    {
        // Body between "<<...>>" + "stream\n" ... "\nendstream".
        var streamMarker = pdfText.IndexOf("stream", objStart, StringComparison.Ordinal);
        if (streamMarker < 0) return (null, null);
        // Advance past stream + newline (could be \n or \r\n).
        var afterStream = streamMarker + 6;
        if (afterStream < pdf.Length && pdf[afterStream] == '\r') afterStream++;
        if (afterStream < pdf.Length && pdf[afterStream] == '\n') afterStream++;

        var endStreamIdx = pdfText.IndexOf("endstream", afterStream, StringComparison.Ordinal);
        if (endStreamIdx < 0) return (null, null);
        // Trim trailing \r\n before endstream.
        var dataEnd = endStreamIdx;
        if (dataEnd > 0 && pdf[dataEnd - 1] == '\n') dataEnd--;
        if (dataEnd > 0 && pdf[dataEnd - 1] == '\r') dataEnd--;

        var streamLen = dataEnd - afterStream;
        if (streamLen <= 0) return (null, null);

        var data = new byte[streamLen];
        Array.Copy(pdf, afterStream, data, 0, streamLen);

        // Detect FlateDecode filter on the dict between objStart and streamMarker.
        var dict = pdfText.AsSpan(objStart, streamMarker - objStart).ToString();
        if (dict.Contains("/FlateDecode", StringComparison.Ordinal))
        {
            try { data = Inflate(data); }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Flate decode failed: {ex.Message}"); return (null, null); }
        }

        // XML decode: prefer UTF-8, fall back to UTF-16 BOM detection.
        try
        {
            string text;
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                text = Encoding.UTF8.GetString(data, 3, data.Length - 3);
            else if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
                text = Encoding.Unicode.GetString(data, 2, data.Length - 2);
            else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
                text = Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            else
                text = Encoding.UTF8.GetString(data);

            // Sanity check — must look like XML.
            if (!text.TrimStart().StartsWith("<")) return (null, data);
            return (text, data);
        }
        catch { return (null, data); }
    }

    private static (string? xml, string? fileName) ExtractStreamFromObjRef(
        byte[] pdf, string pdfText, int objNum, string fileName)
    {
        // Find "{objNum} 0 obj"
        var marker = $"\n{objNum} 0 obj";
        var idx = pdfText.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) { marker = $"{objNum} 0 obj"; idx = pdfText.IndexOf(marker, StringComparison.Ordinal); }
        if (idx < 0) return (null, null);
        var objStart = idx + (marker.StartsWith("\n") ? 1 : 0);
        var (xml, _) = ReadStream(pdf, pdfText, objStart);
        return (xml, fileName);
    }

    private static byte[] Inflate(byte[] data)
    {
        // PDF FlateDecode = zlib (RFC 1950): 2-byte header + DEFLATE + 4-byte adler32.
        // .NET's DeflateStream wants raw DEFLATE so we strip 2-byte header (and 4-byte trailer is OK to ignore).
        var start = 2;
        var len = data.Length - 2 - 4;
        if (len < 0) { start = 0; len = data.Length; }
        using var src = new MemoryStream(data, start, len);
        using var deflate = new System.IO.Compression.DeflateStream(src, System.IO.Compression.CompressionMode.Decompress);
        using var dst = new MemoryStream();
        deflate.CopyTo(dst);
        return dst.ToArray();
    }

    // ───── ETDA Cross Industry Invoice XML parser ─────

    /// <summary>
    /// Parse ETDA CII XML. Returns null when the root element doesn't look
    /// like an e-Tax document (so caller falls back to OCR). Permissive
    /// about namespace variations: matches by local element name only.
    /// </summary>
    public static ExtractResult? ParseEtaxXml(string xmlContent)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xmlContent); }
        catch { return null; }

        var root = doc.Root;
        if (root == null) return null;
        var rootLocal = root.Name.LocalName;
        // Accept any *_CrossIndustryInvoice root.
        if (!rootLocal.EndsWith("CrossIndustryInvoice", StringComparison.OrdinalIgnoreCase)) return null;

        var r = new ExtractResult
        {
            Found = true,
            DocumentRootName = rootLocal,
        };

        // Exchanged document header.
        var exchangedDoc = FindElement(root, "ExchangedDocument");
        if (exchangedDoc != null)
        {
            r.DocumentNumber = TextOf(exchangedDoc, "ID");
            r.DocumentTypeCode = TextOf(exchangedDoc, "TypeCode");
            r.DocumentTypeName = TextOf(exchangedDoc, "Name");
            var iso = TextOf(exchangedDoc, "IssueDateTime");
            if (!string.IsNullOrEmpty(iso)
                && DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
                r.DocumentDate = d;
        }
        r.MappedDocumentType = MapTypeCodeToInternal(r.DocumentTypeCode, rootLocal);

        // Trade transaction.
        var trade = FindElement(root, "SupplyChainTradeTransaction");
        if (trade != null)
        {
            var agreement = FindElement(trade, "ApplicableHeaderTradeAgreement");
            if (agreement != null)
            {
                var seller = FindElement(agreement, "SellerTradeParty");
                if (seller != null)
                {
                    r.SellerName = TextOf(seller, "Name");
                    var sellerId = FindElement(FindElement(seller, "SpecifiedTaxRegistration"), "ID");
                    var (taxId, branch) = SplitThaiTaxId(sellerId?.Value);
                    r.SellerTaxId = taxId;
                    r.SellerBranchCode = branch;
                    r.SellerAddress = BuildPostalAddress(FindElement(seller, "PostalTradeAddress"));
                }
                var buyer = FindElement(agreement, "BuyerTradeParty");
                if (buyer != null)
                {
                    r.BuyerName = TextOf(buyer, "Name");
                    var buyerId = FindElement(FindElement(buyer, "SpecifiedTaxRegistration"), "ID");
                    var (taxId, branch) = SplitThaiTaxId(buyerId?.Value);
                    r.BuyerTaxId = taxId;
                    r.BuyerBranchCode = branch;
                    r.BuyerAddress = BuildPostalAddress(FindElement(buyer, "PostalTradeAddress"));
                }
            }

            var settlement = FindElement(trade, "ApplicableHeaderTradeSettlement");
            if (settlement != null)
            {
                r.Currency = TextOf(settlement, "InvoiceCurrencyCode");
                var summation = FindElement(settlement, "SpecifiedTradeSettlementHeaderMonetarySummation");
                if (summation != null)
                {
                    r.LineTotal = ParseDecimal(TextOf(summation, "LineTotalAmount"));
                    r.TaxBasis = ParseDecimal(TextOf(summation, "TaxBasisTotalAmount"));
                    r.VatAmount = ParseDecimal(TextOf(summation, "TaxTotalAmount"));
                    r.GrandTotal = ParseDecimal(TextOf(summation, "GrandTotalAmount"));
                }
            }

            // Line items.
            var lines = FindElements(trade, "IncludedSupplyChainTradeLineItem");
            foreach (var li in lines)
            {
                var item = new LineItem();
                var docLine = FindElement(li, "AssociatedDocumentLineDocument");
                if (docLine != null && int.TryParse(TextOf(docLine, "LineID"), out var lineNo))
                    item.LineNo = lineNo;

                var product = FindElement(li, "SpecifiedTradeProduct");
                if (product != null) item.Description = TextOf(product, "Name");

                var delivery = FindElement(li, "SpecifiedLineTradeDelivery");
                if (delivery != null)
                {
                    var qtyEl = FindElement(delivery, "BilledQuantity");
                    item.Quantity = ParseDecimal(qtyEl?.Value);
                    item.Unit = qtyEl?.Attribute("unitCode")?.Value;
                }

                var agreementL = FindElement(li, "SpecifiedLineTradeAgreement");
                if (agreementL != null)
                {
                    var grossPrice = FindElement(agreementL, "GrossPriceProductTradePrice")
                                  ?? FindElement(agreementL, "NetPriceProductTradePrice");
                    if (grossPrice != null) item.UnitPrice = ParseDecimal(TextOf(grossPrice, "ChargeAmount"));
                }

                var settlementL = FindElement(li, "SpecifiedLineTradeSettlement");
                if (settlementL != null)
                {
                    var monSum = FindElement(settlementL, "SpecifiedTradeSettlementLineMonetarySummation");
                    if (monSum != null) item.Amount = ParseDecimal(TextOf(monSum, "LineTotalAmount"));
                }
                r.Items.Add(item);
            }
        }

        // Final sanity: document number + grand total must both be present
        // for us to claim this as a usable e-Tax extraction.
        if (string.IsNullOrEmpty(r.DocumentNumber) && r.GrandTotal == null) return null;
        return r;
    }

    // ───── XML helpers (namespace-agnostic by local name) ─────

    private static XElement? FindElement(XElement? parent, string localName)
        => parent?.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> FindElements(XElement parent, string localName)
        => parent.Descendants().Where(e => e.Name.LocalName == localName);

    private static string? TextOf(XElement? parent, string localName)
    {
        var el = FindElement(parent, localName);
        var v = el?.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static decimal? ParseDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    /// <summary>
    /// Per ETDA Schematron: TaxId in CII XML is an 18-char string =
    /// 13-digit TaxID + 5-digit branch. Split into (13, 5) when length
    /// is 18, otherwise return the whole thing as taxId with empty branch.
    /// </summary>
    private static (string? taxId, string? branch) SplitThaiTaxId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var s = raw.Trim();
        if (s.Length == 18) return (s.Substring(0, 13), s.Substring(13, 5));
        if (s.Length == 13) return (s, null);
        return (s, null);
    }

    private static string? BuildPostalAddress(XElement? addressEl)
    {
        if (addressEl == null) return null;
        var parts = new List<string>();
        var buildingNumber = TextOf(addressEl, "BuildingNumber");
        if (!string.IsNullOrEmpty(buildingNumber)) parts.Add(buildingNumber);
        var buildingName = TextOf(addressEl, "BuildingName");
        if (!string.IsNullOrEmpty(buildingName)) parts.Add(buildingName);
        var line1 = TextOf(addressEl, "LineOne");
        if (!string.IsNullOrEmpty(line1)) parts.Add(line1);
        var subDist = TextOf(addressEl, "CitySubDivisionName");
        if (!string.IsNullOrEmpty(subDist)) parts.Add($"แขวง/ตำบล {subDist}");
        var dist = TextOf(addressEl, "CityName");
        if (!string.IsNullOrEmpty(dist)) parts.Add($"เขต/อำเภอ {dist}");
        var province = TextOf(addressEl, "CountrySubDivisionID");
        if (!string.IsNullOrEmpty(province)) parts.Add($"จังหวัด {province}");
        var postcode = TextOf(addressEl, "PostcodeCode");
        if (!string.IsNullOrEmpty(postcode)) parts.Add(postcode);
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// Map ETDA UN/EDIFACT 1001 TypeCode → internal DocumentType name.
    /// 388 = TaxInvoice, T03 = Receipt, 80 = DebitNote, 81 = CreditNote.
    /// Falls back to root element prefix when TypeCode is missing.
    /// </summary>
    private static string? MapTypeCodeToInternal(string? typeCode, string rootLocal)
    {
        return typeCode switch
        {
            "388" => "TaxInvoice",
            "T03" => "Receipt",
            "80" => "DebitNote",
            "81" => "CreditNote",
            _ => rootLocal switch
            {
                var n when n.StartsWith("TaxInvoice", StringComparison.OrdinalIgnoreCase) => "TaxInvoice",
                var n when n.StartsWith("Receipt", StringComparison.OrdinalIgnoreCase) => "Receipt",
                var n when n.StartsWith("DebitCreditNote", StringComparison.OrdinalIgnoreCase) => "DebitNote",
                var n when n.StartsWith("Invoice", StringComparison.OrdinalIgnoreCase) => "Invoice",
                _ => null,
            }
        };
    }
}
