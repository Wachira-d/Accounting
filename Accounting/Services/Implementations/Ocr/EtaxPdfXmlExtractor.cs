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

    /// <summary>ตำแหน่งของ Filespec ทุกตัวในไฟล์ (ทั้งแบบ <c>/Type /Filespec</c> และ <c>/Type/Filespec</c>) เรียงตามตำแหน่ง</summary>
    private static readonly System.Text.RegularExpressions.Regex FilespecMarker = new(
        @"/Type[ \t\r\n]*/Filespec\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static (int efObjNum, string fileName)? FindFilespecXmlRef(string pdfText)
    {
        // Scan all Filespec objects in the file and pick the first whose
        // /F or /UF ends with .xml — extract the /EF target object number.
        // รอบ 193: เดิมใช้ `IndexOf(a) >= 0 || (pos = IndexOf(b, pos)) >= 0` — พอ a ไม่เจอ pos = −1 แล้ว IndexOf(b, −1)
        // โยน ArgumentOutOfRange ⇒ PDF ที่มี Filespec ตัวแรกไม่ใช่ .xml (เช่นรูปโลโก้) ทั้งไฟล์ถูกทิ้งกลับไป OCR เงียบ ๆ
        foreach (System.Text.RegularExpressions.Match marker in FilespecMarker.Matches(pdfText))
        {
            var objStart = FindObjStart(pdfText, marker.Index);
            if (objStart < 0) continue;
            var objEnd = pdfText.IndexOf("endobj", objStart, StringComparison.Ordinal);
            if (objEnd < 0) break;
            var body = pdfText.AsSpan(objStart, objEnd - objStart).ToString();

            // /F (filename.xml) หรือ /F <hex> (ชื่อไฟล์ UTF-16 ที่ PDFKit/pdfmake เข้ารหัสเมื่อมีอักษรนอก ASCII)
            var fname = ExtractNameLiteral(body, "/F") ?? ExtractNameLiteral(body, "/UF");
            if (fname != null && fname.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                // /EF << /F 42 0 R /UF 42 0 R >>
                var efIdx = body.IndexOf("/EF", StringComparison.Ordinal);
                if (efIdx > 0)
                {
                    var rest = body.Substring(efIdx);
                    var m = System.Text.RegularExpressions.Regex.Match(rest, @"/U?F\s+(\d+)\s+\d+\s+R");
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var efNum))
                        return (efNum, fname);
                }
            }
        }
        return null;
    }

    /// <summary>ค่าของคีย์ชื่อไฟล์ <paramref name="key"/> (<c>/F</c> หรือ <c>/UF</c>) — string literal <c>(…)</c> หรือ hex <c>&lt;…&gt;</c>
    /// · คีย์ต้องตามด้วยช่องว่าง/วงเล็บ/&lt; ทันที (กัน <c>/Filespec</c> · <c>/Filter</c> · <c>/F 12 0 R</c>)</summary>
    private static string? ExtractNameLiteral(string s, string key)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s,
            System.Text.RegularExpressions.Regex.Escape(key)
            + @"[ \t\r\n]*(?:\((?<lit>[^)]*)\)|<(?<hex>[0-9A-Fa-f \t\r\n]*)>(?!>))");
        if (!m.Success) return null;
        if (m.Groups["lit"].Success) return DecodeLiteral(m.Groups["lit"].Value);
        return DecodeHex(m.Groups["hex"].Value);
    }

    /// <summary>ถอด string literal ของ PDF: UTF-16BE ที่ขึ้นต้นด้วย BOM (ไบต์ FE FF) · อื่น ๆ = ตัวอักษรตามไบต์ (PDFDocEncoding ≈ Latin1)
    /// · escape ฐานแปด (สามหลัก) และตัวอักษรหลัง backslash — พอสำหรับชื่อไฟล์</summary>
    private static string DecodeLiteral(string raw)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                var n = raw[i + 1];
                if (n >= '0' && n <= '7')
                {
                    var len = 1;
                    while (len < 3 && i + 1 + len < raw.Length && raw[i + 1 + len] >= '0' && raw[i + 1 + len] <= '7') len++;
                    bytes.Add((byte)Convert.ToInt32(raw.Substring(i + 1, len), 8));
                    i += len;
                    continue;
                }
                bytes.Add((byte)n);
                i++;
                continue;
            }
            bytes.Add((byte)c);
        }
        return DecodePdfTextBytes(bytes.ToArray());
    }

    private static string? DecodeHex(string hex)
    {
        var digits = new string(hex.Where(Uri.IsHexDigit).ToArray());
        if (digits.Length == 0) return null;
        if (digits.Length % 2 == 1) digits += "0";
        var bytes = new byte[digits.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(digits.Substring(i * 2, 2), 16);
        return DecodePdfTextBytes(bytes);
    }

    private static string DecodePdfTextBytes(byte[] b)
        => b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2)
            : Encoding.Latin1.GetString(b);

    private static string? FindFilespecPointingTo(string pdfText, int targetObjNum)
    {
        // รอบ 193: เดิมเขียนคลาสตัวอักษรแบบ JS ("ทุกตัวอักษร") ซึ่ง .NET อ่านเป็นคลาสคนละความหมาย ⇒ ไม่เคยแมตช์ ⇒ ชื่อไฟล์เป็น
        // "embedded.xml" เสมอบนเส้น /Subtype · ไล่ Filespec ทีละตัวแทน (ตัวอ่านชื่อเดียวกับเส้นหลัก)
        foreach (System.Text.RegularExpressions.Match marker in FilespecMarker.Matches(pdfText))
        {
            var objStart = FindObjStart(pdfText, marker.Index);
            if (objStart < 0) continue;
            var objEnd = pdfText.IndexOf("endobj", objStart, StringComparison.Ordinal);
            if (objEnd < 0) break;
            var body = pdfText.AsSpan(objStart, objEnd - objStart).ToString();
            var efIdx = body.IndexOf("/EF", StringComparison.Ordinal);
            if (efIdx < 0) continue;
            if (!System.Text.RegularExpressions.Regex.IsMatch(body.Substring(efIdx),
                    @"/U?F\s+" + targetObjNum + @"\s+\d+\s+R")) continue;
            return ExtractNameLiteral(body, "/F") ?? ExtractNameLiteral(body, "/UF");
        }
        return null;
    }

    private static (int efObjNum, string fileName)? FindFilespecForStream(string pdfText, int streamObjNum)
    {
        var name = FindFilespecPointingTo(pdfText, streamObjNum);
        return name != null ? (streamObjNum, name) : null;
    }

    private static readonly System.Text.RegularExpressions.Regex StreamKeyword = new(
        @">>[ \t\r\n]*stream(?=\r?\n)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static (string? xml, byte[]? rawBytes) ReadStream(byte[] pdf, string pdfText, int objStart)
    {
        // Body between "<<...>>" + "stream\n" ... "\nendstream".
        // รอบ 193: เดิม IndexOf("stream") เฉย ๆ ⇒ dict ที่มี "/Subtype /application#2Foctet-stream" ถูกตัดตรงคำ "octet-stream"
        // แล้วอ่านข้อมูลผิดตำแหน่งทั้งสตรีม · คีย์เวิร์ดจริงต้องตามหลัง ">>" ของ dict และตามด้วยขึ้นบรรทัด
        var sm = StreamKeyword.Match(pdfText, objStart);
        if (!sm.Success) return (null, null);
        var streamMarker = sm.Index + sm.Length - 6;
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
        // ไฟล์ ETDA จริงขึ้นต้นด้วย BOM (U+FEFF) — ถ้าถูกถอดเป็นสตริงโดยไม่ตัด BOM, XDocument.Parse ปฏิเสธทั้งไฟล์
        try { doc = XDocument.Parse(xmlContent.TrimStart('\uFEFF', ' ', '\t', '\r', '\n')); }
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
                    // รอบ 193 (ไฟล์จริงใบ Shopee): ETDA ใช้ NetLineTotalAmount (ยอดก่อน VAT หลังส่วนลดรายบรรทัด) — ไม่มี
                    // LineTotalAmount ⇒ เดิมยอดบรรทัดว่างทุกบรรทัด แล้วตัวสร้างบรรทัดต้องคูณราคา×จำนวนเอง (250.47 × 2 = 500.94
                    // ≠ 500.93 ที่ XML ประกาศ) · ลำดับ: Net ก่อน (ตรงสเปก ขมธอ.3-2560) แล้วค่อยชื่อเดิม
                    if (monSum != null)
                        item.Amount = ParseDecimal(TextOf(monSum, "NetLineTotalAmount"))
                            ?? ParseDecimal(TextOf(monSum, "LineTotalAmount"));
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
    /// Map ETDA TypeCode (ขมธอ.3-2560) → internal paper type name.
    /// <para>รอบ 193 (ไฟล์จริงใบ Shopee <c>T03</c> "ใบกำกับภาษี/ใบเสร็จรับเงิน"): เดิม map T03 = "Receipt" ⇒ กระดาษถูกตีว่า
    /// "ไม่ใช่ใบกำกับภาษี" (เส้นสร้างเอกสารพักภาษีซื้อ 11640 + [TAX-INV-PENDING]) ทั้งที่ T03 คือใบกำกับเต็มรูปที่เป็นใบเสร็จด้วย ·
    /// T02 ใบแจ้งหนี้/ใบกำกับ · T03 ใบเสร็จ/ใบกำกับ · T04 ใบส่งของ/ใบกำกับ · 388 ใบกำกับ ⇒ TaxInvoice ·
    /// T01 ใบรับ (ใบเสร็จ) · T05/T06 ใบกำกับอย่างย่อ (ไม่ใช่เต็มรูป — §82/5(2)) ⇒ Receipt · 380 ใบแจ้งหนี้ ⇒ Invoice ·
    /// 80 ใบเพิ่มหนี้ · 81 ใบลดหนี้ · T07 ใบแจ้งยกเลิก ⇒ ไม่ใช่เอกสารลงบัญชี (null)</para>
    /// Falls back to root element prefix when TypeCode is missing.
    /// </summary>
    internal static string? MapTypeCodeToInternal(string? typeCode, string rootLocal)
    {
        return typeCode?.Trim().ToUpperInvariant() switch
        {
            "388" or "T02" or "T03" or "T04" => "TaxInvoice",
            "T01" or "T05" or "T06" => "Receipt",
            "380" => "Invoice",
            "T07" => null,
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
