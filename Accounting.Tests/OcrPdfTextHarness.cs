using System.Globalization;
using System.Text;
using Accounting.Helpers;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using UglyToad.PdfPig;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests;

/// <summary>
/// **ตัวเทียบ PDF จริง "ข้อความเดิม (page.Text) vs ข้อความตามพิกัด" ผ่านตัวอ่านจริงทั้งเส้น** (ทีม OCR 13 · 2026-10-16 · เครื่องมือเทสต์อย่างเดียว)
///
/// <para>═══ ทำไมมี ═══ ทีม OCR 10 เปลี่ยนข้อความ PDF จาก <c>page.Text</c> (ลำดับ content stream) เป็น <see cref="PdfPositionalText"/> ·
/// golden ของ replay พิสูจน์แค่ว่าตัวอ่านทำงานเหมือนเดิม<b>บนข้อความรูปเดิมที่เก็บไว้</b> ไม่ได้บอกว่าข้อความรูปใหม่ของ PDF ใบเดิมทำให้ช่องไหนแย่ลง ·
/// ฝ่ายค้าน OCR 10 รัน PDF จริง 24 ไฟล์แล้วเจอเลขภาษีช่องตัวเลขหาย · ผู้ขาย/ผู้ซื้อสลับ · ตารางบางแถว · ชื่อต่อข้ามคอลัมน์ (กฎเหล็ก #4 §H: ใบที่ดีอยู่แล้วห้ามแย่ลง)</para>
///
/// <para>═══ ไฟล์ PDF ไม่อยู่ในเรพ ═══ PDF ของลูกค้า/เจ้าของมีข้อมูลบริษัทจริง ⇒ <b>ห้าม commit</b> · ตัวเทียบอ่านรายชื่อไฟล์จากตัวแปรแวดล้อม
/// <c>OCR_PDF_HARNESS_LIST</c> (ไฟล์ข้อความ หนึ่ง path ต่อบรรทัด) แล้วเขียนตารางลง <c>OCR_PDF_HARNESS_OUT</c> (หรือ output ของเทสต์) ·
/// ไม่ตั้งตัวแปร = ไม่มีอะไรให้เทียบ (เทสต์บอกใน output ว่าข้าม — ไม่ใช่ "ผ่าน") · ค่าที่ล็อกใน CI อยู่ที่ <c>OcrPdfTextPathTests</c>
/// (ข้อความที่ตัดมาจากกระดาษจริงเท่าที่จำเป็น + e-Tax TXE05202609T004679 ที่อยู่ในเรพแล้ว)</para>
///
/// <para>วิธีใช้: <c>OCR_PDF_HARNESS_LIST=/path/list.txt OCR_PDF_HARNESS_OUT=/path/out.tsv dotnet test --filter Category=PdfHarness</c> ·
/// <c>OCR_PDF_HARNESS_DUMP=/dir</c> เขียนข้อความทั้งสองรูปของทุกไฟล์ (ไว้ตัดเป็นตัวอย่างเทสต์) — ห้าม commit ไฟล์ที่เขียนออกมาทั้งก้อน</para>
/// </summary>
public class OcrPdfTextHarness
{
    private const string TesseractSeparator = "\n\n──── TESSERACT ────\n\n";
    private readonly ITestOutputHelper _out;
    public OcrPdfTextHarness(ITestOutputHelper output) => _out = output;

    /// <summary>ข้อความรูปเดิมก่อนทีม OCR 10 — <c>page.Text</c> ต่อหน้าตามลำดับ (สำเนาของ <c>PdfTextLayerExtractor.TryExtract</c> รุ่น 3f97a009 · เฉพาะในเทสต์)</summary>
    public static string StreamOrderText(byte[] pdf, int maxPages = 10)
    {
        using var doc = PdfDocument.Open(pdf);
        var sb = new StringBuilder();
        for (var p = 1; p <= Math.Min(doc.NumberOfPages, maxPages); p++)
        {
            var t = doc.GetPage(p).Text ?? "";
            if (!string.IsNullOrWhiteSpace(t)) sb.AppendLine(t);
        }
        return sb.ToString();
    }

    /// <summary>ข้อความที่ระบบใช้ตอนนี้ (ตัวสกัดจริง)</summary>
    public static string CurrentText(byte[] pdf) => PdfTextLayerExtractor.TryExtract(pdf).Text;

    /// <summary>ช่องที่ตัวอ่านจริงคืน — ลำดับเท่า <c>OcrPdfTextPathTests.TextPath</c> (Tier 3 hybrid ไม่มี Tesseract) + ตัวแตกบรรทัด + ด่าน Σ + ตัวยึดยอด</summary>
    public static IReadOnlyList<(string Field, string Value)> ReadFields(string pdfText)
    {
        var combined = pdfText + TesseractSeparator;
        var data = OcrService.ParseThaiDocument(ThaiTextNormalizer.Normalize(combined));
        SmartFieldExtractor.Enrich(data, combined);
        var (cleanDocNo, note) = DocumentNumberSanitizer.Sanitize(data.DocumentNumber, combined);
        if (note != null) data.DocumentNumber = cleanDocNo;

        string lineCount = "-", sigma = "-";
        var json = RawTextLineSplitter.SplitToJson(combined, data.SubTotal, data.TotalAmount);
        if (json != null)
        {
            var guard = OcrLineSplitGuard.Evaluate(json, data.SubTotal ?? 0m, data.TotalAmount ?? 0m);
            lineCount = guard.Accepted ? guard.Lines.Count.ToString(CultureInfo.InvariantCulture) : "0";
            sigma = guard.Accepted ? "ok " + guard.Sum.ToString("0.00", CultureInfo.InvariantCulture) : "reject: " + Short(guard.Reason, 50);
        }
        var anchor = OcrTotalAnchor.Find(combined, data.TotalAmount);
        var wht = PaperWhtReader.Read(combined, data.SubTotal);
        static string M(decimal? d) => d?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        return new (string, string)[]
        {
            ("DocType", data.DocumentType),
            ("VendorName", data.VendorName ?? ""),
            ("VendorTaxId", data.VendorTaxId ?? ""),
            ("VendorBranch", data.VendorBranchCode ?? ""),
            ("VendorAddress", Short(data.VendorAddress, 70)),
            ("BuyerName", data.BuyerName ?? ""),
            ("BuyerTaxId", data.BuyerTaxId ?? ""),
            ("BuyerBranch", data.BuyerBranchCode ?? ""),
            ("BuyerAddress", Short(data.BuyerAddress, 70)),
            ("DocNo", data.DocumentNumber ?? ""),
            ("DocDate", data.DocumentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""),
            ("SubTotal", M(data.SubTotal)),
            ("Vat", M(data.VatAmount)),
            ("Total", M(data.TotalAmount)),
            ("HasWht", data.HasWht ? "true:" + (data.WhtRate?.ToString("0.##", CultureInfo.InvariantCulture) ?? "") : "false"),
            ("PaperWht", M(wht.Amount) + (wht.RatePercent is decimal r ? "@" + r.ToString("0.##", CultureInfo.InvariantCulture) : "")),
            ("Anchor", anchor.Verdict.ToString()),
            ("IsDeposit", OcrDepositMarker.Decide(combined, null).IsDeposit ? "true" : "false"),
            ("Lines", lineCount),
            ("Sigma", sigma),
        };
    }

    private static string Short(string? s, int max)
    {
        s = (s ?? "").Replace('\n', ' ').Replace('\t', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>เขียน snapshot ของ replay corpus ลงไฟล์ (<c>OCR_REPLAY_SNAPSHOT_OUT</c>) — ใช้เทียบก่อน/หลังด้วย diff ข้ามรุ่นโค้ด (กฎเหล็ก #4 §H)</summary>
    [Fact]
    [Trait("Category", "PdfHarness")]
    public void สแนปช็อตreplay_เขียนลงไฟล์เมื่อตั้งตัวแปร()
    {
        var outPath = Environment.GetEnvironmentVariable("OCR_REPLAY_SNAPSHOT_OUT");
        if (string.IsNullOrWhiteSpace(outPath)) { _out.WriteLine("ข้าม: ไม่ได้ตั้ง OCR_REPLAY_SNAPSHOT_OUT"); return; }
        File.WriteAllText(outPath, OcrReplayHarness.Snapshot(OcrReplayHarness.Run()));
    }

    [Fact]
    [Trait("Category", "PdfHarness")]
    public void ตารางเทียบ_PDFจริง_ข้อความเดิมกับข้อความตามพิกัด()
    {
        var list = Environment.GetEnvironmentVariable("OCR_PDF_HARNESS_LIST");
        if (string.IsNullOrWhiteSpace(list) || !File.Exists(list))
        {
            _out.WriteLine("ข้าม: ไม่ได้ตั้ง OCR_PDF_HARNESS_LIST (PDF จริงไม่อยู่ในเรพ — ดูหัวคลาส)");
            return;
        }
        var dump = Environment.GetEnvironmentVariable("OCR_PDF_HARNESS_DUMP");
        var sb = new StringBuilder("pdf\tfield\tstream\tcurrent\tsame\n");
        foreach (var path in File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            var name = Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path);   // ชื่อไฟล์ซ้ำได้ข้ามโฟลเดอร์ (before/ · after/)
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (IOException ex) { sb.Append(name).Append("\t(อ่านไฟล์ไม่ได้)\t").Append(ex.Message).Append('\n'); continue; }
            string oldText, newText;
            try { oldText = StreamOrderText(bytes); newText = CurrentText(bytes); }
            catch (Exception ex) { sb.Append(name).Append("\t(PdfPig)\t").Append(ex.Message).Append('\n'); continue; }
            if (!string.IsNullOrEmpty(dump))
            {
                Directory.CreateDirectory(dump);
                File.WriteAllText(Path.Combine(dump, name.Replace("/", "__") + ".stream.txt"), oldText);
                File.WriteAllText(Path.Combine(dump, name.Replace("/", "__") + ".current.txt"), newText);
            }
            var a = ReadFields(oldText);
            var b = ReadFields(newText);
            for (var i = 0; i < a.Count; i++)
                sb.Append(name).Append('\t').Append(a[i].Field).Append('\t').Append(a[i].Value).Append('\t').Append(b[i].Value)
                  .Append('\t').Append(a[i].Value == b[i].Value ? "=" : "≠").Append('\n');
        }
        var outPath = Environment.GetEnvironmentVariable("OCR_PDF_HARNESS_OUT");
        if (!string.IsNullOrEmpty(outPath)) File.WriteAllText(outPath, sb.ToString());
        else _out.WriteLine(sb.ToString());
    }
}
