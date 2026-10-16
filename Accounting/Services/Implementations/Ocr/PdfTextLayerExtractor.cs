using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Accounting.Services.Implementations.Ocr;

/// <summary>
/// Try to extract text directly from a PDF's embedded text layer, skipping
/// OCR entirely when possible. Most accounting-related PDFs received by
/// Thai SMEs are text-born:
///
///   • Thai e-Tax / e-Receipt invoices (per RD digital invoice standard)
///   • Cloud-billing invoices (AWS, Azure, Google Cloud, DigitalOcean, ...)
///   • Telecom / utility e-bills (AIS, True, PEA, MEA, MWA, ...)
///   • SaaS receipts (Microsoft 365, Adobe, GitHub, ...)
///   • PDFs exported by any accounting software (Express, QuickBooks,
///     Xero, FlowAccount, PeakAccount, ...)
///
/// For these the embedded text layer is:
///   • FREE — no Azure DI billable pages consumed
///   • INSTANT — no network roundtrip + polling
///   • PERFECT — character-accurate, no OCR errors
///
/// Algorithm:
///   1. Open the PDF (PdfPig — pure C#, MIT, no native deps).
///   2. Walk first N pages (default 10 — matches OcrMaxPagesPerScan).
///   3. Concatenate page text in visual reading order — Helpers/PdfPositionalText (letters + coordinates),
///      falling back to PdfPig page.Text (content-stream order) when a page cannot be laid out.
///   4. Confidence check: when extracted text is too short / too sparse
///      for the PDF's filesize, treat as image-only and let OCR run.
/// </summary>
public static class PdfTextLayerExtractor
{
    public record Result(
        bool HasUsableText,
        string Text,
        int PageCount,
        int CharsExtracted,
        string Reason);

    /// <summary>Minimum characters per page to call the text layer
    /// "usable". Below this, the PDF is probably image-only (a scan)
    /// with a thin OCR text layer attached — we'd rather run real OCR
    /// than trust a thin layer.</summary>
    private const int MinCharsPerPage = 50;

    /// <summary>Hard cap on total extracted text — protects against
    /// runaway extraction from catalog PDFs.</summary>
    private const int MaxTotalChars = 500_000;

    /// <summary>ข้อความหน้าเดียวตามพิกัด (ตัวแปลง PdfPig <c>Letter</c> → <see cref="Accounting.Helpers.PdfGlyph"/> ที่นี่ที่เดียว · ตัวประกอบไม่รู้จัก PdfPig) ·
    /// null = ประกอบไม่ได้ ⇒ ผู้เรียกใช้ <c>page.Text</c></summary>
    internal static string? PositionalPageText(Page page)
    {
        IReadOnlyList<Letter> letters;
        try { letters = page.Letters; }
        catch { return null; }   // หน้าที่ PdfPig ถอดพิกัดไม่ได้ ⇒ ข้อความเดิม (ไม่ใช่เส้นเงิน · ข้อความเดิมยังอยู่)
        if (letters == null || letters.Count == 0) return null;
        var glyphs = new List<Accounting.Helpers.PdfGlyph>(letters.Count);
        foreach (var l in letters)
            glyphs.Add(new Accounting.Helpers.PdfGlyph(
                l.Value ?? "", l.StartBaseLine.X, l.EndBaseLine.X, l.StartBaseLine.Y,
                l.PointSize > 0 ? l.PointSize : l.FontSize,
                l.TextOrientation == TextOrientation.Horizontal));
        return Accounting.Helpers.PdfPositionalText.Build(glyphs);
    }

    public static Result TryExtract(byte[] pdfBytes, int maxPages = 10)
    {
        if (pdfBytes == null || pdfBytes.Length < 100)
            return new Result(false, "", 0, 0, "ไฟล์ว่างหรือเล็กเกิน");

        // Quick magic-byte check — bail fast on non-PDF inputs
        if (!(pdfBytes[0] == 0x25 && pdfBytes[1] == 0x50 && pdfBytes[2] == 0x44 && pdfBytes[3] == 0x46))
            return new Result(false, "", 0, 0, "ไม่ใช่ PDF (magic bytes mismatch)");

        try
        {
            using var doc = PdfDocument.Open(pdfBytes);
            int totalPages = doc.NumberOfPages;
            int scanLimit = Math.Min(totalPages, Math.Max(1, maxPages));

            var sb = new System.Text.StringBuilder();
            int charsExtracted = 0;
            for (int pageNum = 1; pageNum <= scanLimit; pageNum++)
            {
                Page page;
                try { page = doc.GetPage(pageNum); }
                catch { continue; }

                // ทีม OCR 10 (2026-10-16): ประกอบข้อความตาม "ตำแหน่งบนกระดาษ" (Helpers/PdfPositionalText — ตัวตั้งตัวเดียว)
                // เดิม page.Text ต่อ glyph ตามลำดับ content stream ⇒ ช่องตารางติดกัน ("1,356.7120,738.21") · ป้ายกับค่าสลับคู่ ·
                // วรรณยุกต์เป็น PUA — ประกอบไม่ได้ (หน้าหมุน/ไม่มีพิกัด) ⇒ page.Text เดิม (ไม่เดาลำดับ)
                var pageText = PositionalPageText(page) ?? page.Text ?? "";
                if (string.IsNullOrWhiteSpace(pageText)) continue;
                sb.AppendLine(pageText);
                charsExtracted += pageText.Length;
                if (charsExtracted >= MaxTotalChars) break;
            }

            var text = sb.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return new Result(false, "", totalPages, 0,
                    "PDF ไม่มี text layer (image-only — ต้อง OCR)");

            // Density check: real digital invoices have 200-2000 chars
            // per page. Below the threshold means OCR is needed.
            var charsPerPage = (double)charsExtracted / scanLimit;
            if (charsPerPage < MinCharsPerPage)
                return new Result(false, text, totalPages, charsExtracted,
                    $"text layer บาง ({charsPerPage:F0} chars/page) — น่าจะ scan paper");

            return new Result(true, text, totalPages, charsExtracted,
                $"text layer ใช้ได้ ({charsPerPage:F0} chars/page, {scanLimit}/{totalPages} pages)");
        }
        catch (Exception ex)
        {
            return new Result(false, "", 0, 0, $"PdfPig error: {ex.Message}");
        }
    }
}
