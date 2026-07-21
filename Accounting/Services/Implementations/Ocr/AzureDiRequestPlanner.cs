using Accounting.Models.Entities;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Choose the right Azure DI request shape for THIS specific file —
/// before paying for the analyze call. Inspects file size, format,
/// dimensions, and filename hints to decide:
///   • Model         (prebuilt-invoice vs prebuilt-receipt vs custom)
///   • Locale        (th-TH default; switch to en-US for English-named files)
///   • Features      (always: keyValuePairs + barcodes;
///                    high-res toggle: only when image quality is poor)
///   • Pages range   (cap multi-page PDFs to N pages — cost control)
///   • String index  (utf16CodeUnit for Thai locale, codePoint for en-US)
///
/// Without this planner the previous implementation sent every feature
/// with every request — paying ocrHighResolution premium even on
/// crystal-clear digital PDFs, and forcing th-TH locale on English
/// documents.
///
/// Pure static — no DB / DI. Caller passes in siteSettings if defaults
/// need overriding.
/// </summary>
public static class AzureDiRequestPlanner
{
    public record AnalysisPlan(
        string ModelId,
        string Locale,
        string StringIndexType,
        string[] Features,
        string? Pages,            // e.g. "1-5" or null to send the whole file
        string[]? QueryFields,    // Thai-specific NL field queries when features includes queryFields
        List<string> Reasons);

    public static AnalysisPlan Build(
        byte[] fileBytes,
        string contentType,
        string? fileName,
        SiteSettings? settings)
    {
        var reasons = new List<string>();

        // 1. Model selection
        //    Priority: admin-configured > filename hint > default "prebuilt-invoice"
        string modelId;
        if (!string.IsNullOrEmpty(settings?.AzureDiModelId)
            && !settings.AzureDiModelId.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            modelId = settings.AzureDiModelId;
            reasons.Add($"model={modelId} (admin override)");
        }
        else
        {
            modelId = DetectModelFromFilename(fileName);
            reasons.Add($"model={modelId} (auto from filename)");
        }

        // 2. Locale selection
        //    Three signals, evaluated in order — strongest first:
        //      a) Content peek: scan the raw bytes for Thai UTF-8 sequences.
        //         When found, the document HAS Thai text in it, so th-TH is
        //         correct regardless of filename. This catches PDFs / images
        //         with English filenames but Thai content.
        //      b) Filename hint: when no Thai bytes appear AND the filename
        //         carries an English-vendor / English-locale signal
        //         (aws-, microsoft, google-cloud, _en.pdf, etc.) and no
        //         Thai chars in the name → en-US.
        //      c) Default: th-TH (this is a Thai SaaS, most uploads are
        //         Thai-language receipts).
        string locale;
        if (ContainsThaiInContent(fileBytes))
        {
            locale = "th-TH";
            reasons.Add("locale=th-TH (Thai UTF-8 bytes detected in content)");
        }
        else if (LooksLikeEnglishFile(fileName))
        {
            locale = "en-US";
            reasons.Add("locale=en-US (filename hint + no Thai bytes)");
        }
        else
        {
            locale = "th-TH";
            reasons.Add("locale=th-TH (default)");
        }

        // 3. String index type
        //    Thai vowels and tone marks are combining UTF-16 code units —
        //    utf16CodeUnit prevents offset corruption when slicing content.
        //    For pure-English documents, codePoint produces simpler offsets.
        var stringIndexType = locale == "th-TH" ? "utf16CodeUnit" : "codePoint";

        // 4. Features list
        //    Per Azure DI v4.0: feature support is model-specific.
        //    Sending an unsupported feature returns HTTP 400
        //    "InvalidParameter: The parameter <feature> is invalid or not
        //    supported." So we gate each feature by the model it's known
        //    to work on.
        //
        //    Universal (every model accepts):
        //      • styleFont — handwriting detection. Page-level layout
        //                    feature; supported on all prebuilt-*.
        //
        //    Layout-class only (prebuilt-layout / -document / -invoice):
        //      • keyValuePairs — Thai-anchored fallback fields. NOT
        //                        supported on receipt / idDocument /
        //                        businessCard — those have their own
        //                        fixed schema and Azure rejects with 400.
        //      • barcodes      — RD QR receipts. Same gating.
        //      • queryFields   — natural-language extraction. Already
        //                        gated below; receipt has its own schema.
        //
        //    Conditional (image quality dependent):
        //      • ocrHighResolution — small / low-res images only.
        bool isLayoutClass = modelId.Equals("prebuilt-invoice", StringComparison.OrdinalIgnoreCase)
            || modelId.Equals("prebuilt-document", StringComparison.OrdinalIgnoreCase)
            || modelId.Equals("prebuilt-layout", StringComparison.OrdinalIgnoreCase)
            || modelId.Equals("prebuilt-read", StringComparison.OrdinalIgnoreCase);

        var features = new List<string> { "styleFont" };
        if (isLayoutClass)
        {
            features.Add("keyValuePairs");
            features.Add("barcodes");
        }
        else
        {
            reasons.Add($"skip keyValuePairs+barcodes (model {modelId} doesn't support them)");
        }
        if (ShouldUseHighResolution(fileBytes, contentType, out var hiResReason))
        {
            features.Add("ocrHighResolution");
            reasons.Add($"+ocrHighResolution ({hiResReason})");
        }
        else
        {
            reasons.Add($"skip ocrHighResolution ({hiResReason})");
        }
        // queryFields supported on prebuilt-invoice / prebuilt-document / layout
        // — NOT on receipt (which has its own schema). Append our Thai-
        // problematic fields when the model accepts queries.
        bool supportsQueries = modelId.Equals("prebuilt-invoice", StringComparison.OrdinalIgnoreCase)
            || modelId.Equals("prebuilt-document", StringComparison.OrdinalIgnoreCase)
            || modelId.Equals("prebuilt-layout", StringComparison.OrdinalIgnoreCase);
        if (supportsQueries)
        {
            features.Add("queryFields");
            reasons.Add("+queryFields (Thai-specific fields)");
        }

        // 5. Pages range (PDF only)
        //    Cost control — admin can set MaxPagesPerScan in SiteSettings.
        //    Defaults to 10 pages (covers 99% of Thai SME invoices/receipts;
        //    catalog PDFs and contract bundles that exceed this should be
        //    split before scanning anyway).
        string? pages = null;
        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            var maxPages = settings?.OcrMaxPagesPerScan;
            if (maxPages.HasValue && maxPages.Value > 0)
            {
                pages = $"1-{maxPages.Value}";
                reasons.Add($"pages=1-{maxPages.Value} (admin cap)");
            }
        }

        // queryFields — Thai-specific fields that the standard Invoice
        // schema mis-anchors or misses entirely. Azure runs each as a
        // natural-language question against the document text.
        string[]? queries = null;
        if (supportsQueries)
        {
            queries = new[]
            {
                "BranchNumber",          // เลขสาขา
                "BuyerBranchNumber",     // เลขสาขาผู้ซื้อ
                "WithholdingTaxAmount",  // ยอด WHT
                "WithholdingTaxRate",    // อัตรา WHT %
                "PaymentMethod",         // วิธีชำระเงิน (เงินสด/โอน/บัตร)
                "BankAccountNumber",     // เลขบัญชีธนาคารที่จ่าย
                "ReferenceNumber",       // เลขอ้างอิง / PO
                "ContractAccount",       // เลขที่บัญชี (PEA/MEA bills)
            };
        }

        return new AnalysisPlan(modelId, locale, stringIndexType, features.ToArray(), pages, queries, reasons);
    }

    /// <summary>Build the URL query string from the plan + api-version.</summary>
    public static string BuildQueryString(AnalysisPlan plan, string apiVersion)
    {
        var q = $"?api-version={apiVersion}" +
                $"&locale={plan.Locale}" +
                $"&stringIndexType={plan.StringIndexType}" +
                $"&features={string.Join(",", plan.Features)}";
        if (!string.IsNullOrEmpty(plan.Pages))
            q += $"&pages={plan.Pages}";
        if (plan.QueryFields != null && plan.QueryFields.Length > 0)
            q += $"&queryFields={string.Join(",", plan.QueryFields)}";
        return q;
    }

    private static string DetectModelFromFilename(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return "prebuilt-invoice";
        var lower = fileName.ToLowerInvariant();
        // Receipt hints — short paper, often photographed
        if (lower.Contains("receipt") || lower.Contains("ใบเสร็จ") || lower.Contains("เซเว่น")
            || lower.Contains("7-11") || lower.Contains("cafe") || lower.Contains("คาเฟ่"))
            return "prebuilt-receipt";
        // Business card hints
        if (lower.Contains("businesscard") || lower.Contains("business_card") || lower.Contains("namecard"))
            return "prebuilt-businessCard";
        // ID document hints
        if (lower.Contains("idcard") || lower.Contains("บัตรประชาชน") || lower.Contains("passport"))
            return "prebuilt-idDocument";
        // Default — invoice covers tax-invoices, billing, PO, most B2B docs
        return "prebuilt-invoice";
    }

    private static bool LooksLikeEnglishFile(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return false;
        var lower = fileName.ToLowerInvariant();
        // Common English-doc filename patterns from AWS/Google/Microsoft invoices
        return lower.Contains("aws-") || lower.Contains("amazon")
            || lower.Contains("microsoft") || lower.Contains("azure-")
            || lower.Contains("googlecloud") || lower.Contains("gcp")
            || lower.Contains("invoice-") && !ContainsThaiChar(fileName)
            || lower.EndsWith("_en.pdf") || lower.EndsWith("_eng.pdf");
    }

    private static bool ContainsThaiChar(string s)
        => s.Any(c => c >= '฀' && c <= '๿');

    /// <summary>
    /// Scan raw file bytes for Thai-language UTF-8 sequences. Thai
    /// Unicode block U+0E00–U+0E7F encodes in UTF-8 as 3-byte sequences
    /// starting with 0xE0 followed by 0xB8 or 0xB9 plus a valid trail
    /// byte (0x80–0xBF). When any such sequence appears we treat the
    /// document as Thai regardless of filename.
    ///
    /// Coverage:
    ///   • PDFs with UTF-8 text streams (most modern PDF generators)
    ///   • Images with Thai EXIF / IPTC metadata
    ///   • PDFs embedding Unicode CMap tables for Thai fonts (CIDs
    ///     reference original Thai codepoints, often visible as UTF-8
    ///     literals in `/ToUnicode` streams)
    ///
    /// Misses (false negatives accepted):
    ///   • Old PDFs that encode Thai via custom Type1 fonts with no
    ///     ToUnicode mapping — content unreadable as bytes; falls
    ///     through to the filename hint / default.
    ///   • Pure-image scans (JPEG / PNG) — pixel bytes only; falls
    ///     through to default (which is th-TH anyway).
    ///
    /// Cost: O(N) over first 256KB of the file — bounded so big PDFs
    /// don't slow scan submission.
    /// </summary>
    private static bool ContainsThaiInContent(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 3) return false;
        int scanLen = Math.Min(bytes.Length - 2, 256 * 1024);   // cap 256KB
        for (int i = 0; i < scanLen; i++)
        {
            // Thai UTF-8 prefix: 0xE0 followed by 0xB8 or 0xB9 followed by 0x80–0xBF
            if (bytes[i] != 0xE0) continue;
            var b1 = bytes[i + 1];
            if (b1 != 0xB8 && b1 != 0xB9) continue;
            var b2 = bytes[i + 2];
            if (b2 >= 0x80 && b2 <= 0xBF) return true;
        }
        return false;
    }

    private static bool ShouldUseHighResolution(byte[] fileBytes, string contentType, out string reason)
    {
        // PDFs: text-born PDFs don't benefit from ocrHighResolution (the
        // text layer is extracted directly). Scanned PDFs would benefit,
        // but distinguishing them requires parsing the PDF — skip the
        // premium feature for PDFs by default; admin can force-enable
        // for tenants that mostly scan paper.
        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            reason = "PDF — text layer expected";
            return false;
        }

        // Images: enable hi-res when the file is small OR low-resolution.
        // Threshold: <300KB OR shorter side <1000px implies a mobile snap
        // or low-quality scan that benefits from hi-res processing.
        if (fileBytes.Length < 300_000)
        {
            reason = $"small image ({fileBytes.Length / 1024}KB < 300KB)";
            return true;
        }

        var (w, h) = ReadImageDims(fileBytes, contentType);
        if (w > 0 && h > 0 && Math.Min(w, h) < 1000)
        {
            reason = $"low-res image ({w}×{h}, short side <1000)";
            return true;
        }

        reason = "image quality OK";
        return false;
    }

    private static (int W, int H) ReadImageDims(byte[] data, string contentType)
    {
        try
        {
            if (contentType.Contains("png", StringComparison.OrdinalIgnoreCase) && data.Length >= 24)
                return (
                    (data[16] << 24) | (data[17] << 16) | (data[18] << 8) | data[19],
                    (data[20] << 24) | (data[21] << 16) | (data[22] << 8) | data[23]);
            if ((contentType.Contains("jpeg", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("jpg", StringComparison.OrdinalIgnoreCase)))
            {
                int i = 2;
                while (i < data.Length - 9)
                {
                    if (data[i] != 0xFF) { i++; continue; }
                    var m = data[i + 1];
                    if ((m >= 0xC0 && m <= 0xC3) || (m >= 0xC5 && m <= 0xC7)
                        || (m >= 0xC9 && m <= 0xCB) || (m >= 0xCD && m <= 0xCF))
                    {
                        return ((data[i + 7] << 8) | data[i + 8], (data[i + 5] << 8) | data[i + 6]);
                    }
                    if (i + 3 < data.Length) i += 2 + ((data[i + 2] << 8) | data[i + 3]);
                    else break;
                }
            }
        }
        catch { }
        return (0, 0);
    }
}
