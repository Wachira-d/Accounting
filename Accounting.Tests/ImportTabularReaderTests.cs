using System.Text;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Import;
using Accounting.Services.Settlement.Adapters;
using MiniExcelLibs;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 T0 — ตัวอ่านไฟล์นำเข้ากลาง <see cref="ImportTabularReader"/> (ย้ายจาก SettlementFileReader) · ทุกกลุ่มล็อกสองทิศ:
/// ไฟล์ที่ต้องจับ/ล้มดัง กับไฟล์ที่ถูกอยู่แล้วต้องไม่ถูกแตะ (CLAUDE.md F2 ข้อ 8) · ข้อมูลทดสอบสังเคราะห์ (ไม่มี PII จริง)
/// </summary>
public class ImportTabularReaderTests
{
    private static readonly Encoding Thai874 = CodePagesEncodingProvider.Instance.GetEncoding(874)!;

    private static byte[] Xlsx(Dictionary<string, object> sheets)
    {
        using var ms = new MemoryStream();
        MiniExcel.SaveAs(ms, sheets, excelType: ExcelType.XLSX);
        return ms.ToArray();
    }

    private static List<Dictionary<string, object>> Rows(params (string Name, decimal Amount)[] rows)
        => rows.Select(r => new Dictionary<string, object> { ["ชื่อ"] = r.Name, ["ยอด"] = r.Amount }).ToList();

    // ═══ encoding ═══

    [Fact]
    public void TIS620_ถอดรหัสได้_และบอกชื่อ_encoding()
    {
        var bytes = Thai874.GetBytes("ชื่อลูกค้า,ยอดเงิน\nร้านทดสอบ,100\n");
        var opened = ImportTabularReader.Open("express.csv", bytes);
        var rows = opened.Rows.ToList();
        Assert.Equal("windows-874", opened.Encoding);
        Assert.Equal("ชื่อลูกค้า", rows[0][0]);
        Assert.Equal("ร้านทดสอบ", rows[1][0]);
    }

    [Fact]
    public void UTF8_ไทยต้องไม่ถูกอ่านเป็น_874()
    {
        // ทิศตรงข้าม: ไบต์ UTF-8 ของไทย "ถอดได้" ด้วย 874 เป็นขยะ — ต้องตัดสินด้วย UTF-8 แบบเข้มงวดก่อนเสมอ
        var bytes = Encoding.UTF8.GetBytes("ชื่อลูกค้า,ยอดเงิน\nร้านทดสอบ,100\n");
        var opened = ImportTabularReader.Open("peak.csv", bytes);
        Assert.Equal("utf-8", opened.Encoding);
        Assert.Equal("ร้านทดสอบ", opened.Rows.ToList()[1][0]);
        Assert.Equal("ชื่อ", ImportTabularReader.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("ชื่อ")).ToArray()));
    }

    [Fact]
    public void ไบต์ที่ไม่ใช่ทั้ง_UTF8_และ_874_ล้มดัง_ไม่เดา()
    {
        var ex = Assert.Throws<ImportFormatException>(() => ImportTabularReader.Decode(new byte[] { 0x41, 0xFC, 0xFD, 0xFE, 0x42 }));
        Assert.Equal("encoding", ex.Code);
        Assert.Contains("UTF-8", ex.Message);
        Assert.IsAssignableFrom<BusinessRuleException>(ex);   // middleware ตอบ 400 ข้อความไทย ไม่ใช่ 500
    }

    [Fact]
    public void ไบต์ขยะ_874_ทีละไบต์_ทุกไบต์ที่ตารางไม่กำหนด_ล้มดัง()
    {
        // .NET ไม่ throw ให้ไบต์เหล่านี้ (best-fit เป็น U+F8C1–F8C8 / C1 control) — ด่านต้องจับเองทุกตัว
        foreach (var b in new byte[] { 0xDB, 0xDC, 0xDD, 0xDE, 0xFC, 0xFD, 0xFE, 0xFF, 0x81, 0x8F, 0x9F })
        {
            var ex = Assert.Throws<ImportFormatException>(() => ImportTabularReader.Decode(Thai874.GetBytes("ชื่อ,").Concat(new[] { b }).ToArray()));
            Assert.Equal("encoding", ex.Code);
        }
    }

    [Fact]
    public void ทิศตรงข้าม_TIS620_ทุกอักขระที่ตารางกำหนด_รวมยูโรและอัญประกาศ_ถอดได้ไม่ถูกปัดตก()
    {
        // ทุกไบต์ที่ Windows-874 กำหนด: ไทย 0xA1–0xDA · 0xDF–0xFB + € … ‘ ’ “ ” • – — ⇒ ต้องไม่ล้ม (ด่านใหม่ห้ามปัดไฟล์ไทยจริง)
        var defined = new List<byte> { 0x80, 0x85, 0x91, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0xA0 };
        for (int b = 0xA1; b <= 0xDA; b++) defined.Add((byte)b);
        for (int b = 0xDF; b <= 0xFB; b++) defined.Add((byte)b);
        var bytes = Encoding.ASCII.GetBytes("x,").Concat(defined).ToArray();
        var text = ImportTabularReader.Decode(bytes);
        Assert.Contains("€", text);
        Assert.Contains("ก", text);
        Assert.Contains("๛", text);
        Assert.DoesNotContain(text, ImportTabularReader.Is874Undefined);
    }

    // ═══ CSV ═══

    [Fact]
    public void อัฒภาค_และค่าในคำพูดขึ้นบรรทัดใหม่ได้()
    {
        var opened = ImportTabularReader.Open("eu.csv", Encoding.UTF8.GetBytes("Name;Note;Amount\n\"ร้าน A\";\"บรรทัด1\nบรรทัด2\";\"1.234,50\"\n"));
        var rows = opened.Rows.ToList();
        Assert.Equal(';', opened.Delimiter);
        Assert.Equal(2, rows.Count);
        Assert.Equal("บรรทัด1\nบรรทัด2", rows[1][1]);
        Assert.Equal("1.234,50", rows[1][2]);   // ตัวอ่านไม่แปลงค่า — ตัวแปลงค่า (ImportValueNormalizer) ตัดสิน
    }

    [Fact]
    public void คำพูดไม่ปิด_ล้มดัง()
    {
        var ex = Assert.Throws<ImportFormatException>(() => ImportTabularReader.ReadCsv("a,b\n\"open,1\n").ToList());
        Assert.Equal("csv-quote", ex.Code);
    }

    [Fact]
    public void จุลภาคธรรมดา_ไม่ถูกแตะ()
    {
        var opened = ImportTabularReader.Open("a.csv", Encoding.UTF8.GetBytes("a,b,c\n1,2,3\n"));
        Assert.Equal(',', opened.Delimiter);
        Assert.Equal(ImportFileKind.Csv, opened.Kind);
        Assert.Equal(new[] { "1", "2", "3" }, opened.Rows.ToList()[1]);
    }

    // ═══ ชนิดไฟล์ — ตัดสินจากเนื้อ ═══

    [Fact]
    public void xls_BIFF8_ล้มดังพร้อมทางไปต่อ_บันทึกเป็น_xlsx()
    {
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };
        Assert.Equal(ImportFileKind.Xls, ImportTabularReader.Sniff("bank.xls", ole));
        var ex = Assert.Throws<ImportFormatException>(() => ImportTabularReader.Open("bank.xls", ole));
        Assert.Equal("xls-legacy", ex.Code);
        Assert.Contains(".xlsx", ex.Message);
    }

    [Fact]
    public void ไฟล์ชื่อ_xls_ที่จริงเป็นข้อความคั่นแท็บ_อ่านได้()
    {
        // ทิศตรงข้ามของข้างบน: ธนาคารหลายแห่งส่งออก "xls" ที่จริงเป็น TSV — ต้องไม่ถูกปฏิเสธเพราะนามสกุล
        var tsv = Encoding.UTF8.GetBytes("วันที่\tรายการ\tยอด\n05/08/2569\tโอนเข้า\t1,000.00\n");
        Assert.Equal(ImportFileKind.Tsv, ImportTabularReader.Sniff("statement.xls", tsv));
        var opened = ImportTabularReader.Open("statement.xls", tsv);
        Assert.Equal('\t', opened.Delimiter);
        Assert.Equal("โอนเข้า", opened.Rows.ToList()[1][1]);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><rsm:TaxInvoice_CrossIndustryInvoice/>", ImportFileKind.Xml, "xml-use-ocr")]
    [InlineData("OFXHEADER:100\nDATA:OFXSGML\n<OFX>", ImportFileKind.Ofx, "ofx-bank-only")]
    [InlineData("!Type:Bank\nD05/08/2026\nT-100.00\n^", ImportFileKind.Qif, "qif-unsupported")]
    [InlineData("<html><body><table><tr><td>1</td></tr></table></body></html>", ImportFileKind.Html, "html-disguised")]
    [InlineData("{\"rows\":[]}", ImportFileKind.Json, "json-unsupported")]
    public void ไฟล์ที่ไม่ใช่ตาราง_ล้มดังพร้อมรหัสและทางไปต่อ(string content, ImportFileKind kind, string code)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        Assert.Equal(kind, ImportTabularReader.Sniff("file.dat", bytes));
        var ex = Assert.Throws<ImportFormatException>(() => ImportTabularReader.Open("file.dat", bytes));
        Assert.Equal(code, ex.Code);
        Assert.Contains("—", ex.Message);   // ข้อความมีส่วนบอกทางไปต่อ
    }

    [Fact]
    public void PDF_ล้มดัง()
    {
        var pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n");
        Assert.Equal(ImportFileKind.Pdf, ImportTabularReader.Sniff("x.pdf", pdf));
        Assert.Equal("pdf-unsupported", Assert.Throws<ImportFormatException>(() => ImportTabularReader.Open("x.pdf", pdf)).Code);
    }

    [Fact]
    public void ไฟล์ว่าง_ล้มดัง()
        => Assert.Equal("empty-file", Assert.Throws<ImportFormatException>(() => ImportTabularReader.Open("a.csv", Array.Empty<byte>())).Code);

    // ═══ xlsx หลายชีต ═══

    [Fact]
    public void xlsx_หลายชีต_เลือกชีตได้_ไม่ระบุ_ได้ชีตแรก()
    {
        var bytes = Xlsx(new Dictionary<string, object>
        {
            ["ลูกค้า"] = Rows(("ร้าน A", 100m)),
            ["สินค้า"] = Rows(("ปากกา", 15m), ("ดินสอ", 5m)),
        });
        Assert.Equal(ImportFileKind.Xlsx, ImportTabularReader.Sniff("book.xlsx", bytes));
        Assert.Equal(new[] { "ลูกค้า", "สินค้า" }, ImportTabularReader.ListSheets(bytes));

        var first = ImportTabularReader.Open("book.xlsx", bytes);
        Assert.Equal("ลูกค้า", first.SheetName);
        Assert.Equal(2, first.Rows.Count());   // หัว + 1 แถว

        var second = ImportTabularReader.Open("book.xlsx", bytes, "สินค้า");
        var rows = second.Rows.ToList();
        Assert.Equal("สินค้า", second.SheetName);
        Assert.Equal(3, rows.Count);
        Assert.Equal("ดินสอ", rows[2][0]);
        Assert.Equal("5", rows[2][1]);      // ตัวเลขคงรูป invariant
    }

    [Fact]
    public void xlsx_ชีตที่ไม่มี_ล้มดังพร้อมรายชื่อชีต()
    {
        var bytes = Xlsx(new Dictionary<string, object> { ["Sheet1"] = Rows(("x", 1m)) });
        var ex = Assert.Throws<ImportFormatException>(() => ImportTabularReader.Open("b.xlsx", bytes, "ไม่มีชีตนี้"));
        Assert.Equal("sheet-not-found", ex.Code);
        Assert.Contains("Sheet1", ex.Message);
    }

    [Fact]
    public void CSV_ไม่มีรายชื่อชีต()
        => Assert.Empty(ImportTabularReader.ListSheets(Encoding.UTF8.GetBytes("a,b\n1,2\n")));

    // ═══ ตัว Extract + wrapper เดิม ═══

    [Fact]
    public void Inspect_ให้ชนิด_encoding_แถวต้นไฟล์_และ_SHA256()
    {
        var bytes = Thai874.GetBytes("รายงานลูกค้า\nชื่อ,ยอด\nร้าน A,1\n");
        var p = ImportSourceInspector.Inspect("r.csv", bytes, previewRows: 2);
        Assert.Equal(ImportFileKind.Csv, p.Kind);
        Assert.Equal("windows-874", p.Encoding);
        Assert.Equal(2, p.HeadRows.Count);
        Assert.Equal(64, p.Sha256.Length);
        Assert.Equal(p.Sha256, ImportSourceInspector.Sha256Hex(bytes));
        Assert.NotEqual(p.Sha256, ImportSourceInspector.Sha256Hex(bytes.Append((byte)0x0A).ToArray()));
    }

    [Fact]
    public void SettlementFileReader_ยังโยนชนิดเดิม_รหัสเดิม_ข้อความของ_settlement()
    {
        // wrapper บาง: ผู้เรียกเดิม (GenericColumnMapAdapter + เทสต์ settlement) ต้องได้ SettlementFormatException เหมือนก่อนรอบ 205
        var quote = Assert.Throws<SettlementFormatException>(() => SettlementFileReader.ReadCsv("a,b\n\"open,1\n").ToList());
        Assert.Equal("csv-quote", quote.Code);
        Assert.Contains("แพลตฟอร์ม", quote.Message);
        var rows = Assert.Throws<SettlementFormatException>(() => SettlementFileReader.Capped(
            Enumerable.Range(0, 6).Select(_ => (IReadOnlyList<string>)new[] { "x" }), 5, 10).ToList());
        Assert.Equal("too-many-rows", rows.Code);
        Assert.Contains("รอบโอน", rows.Message);
        Assert.Equal("ประเภท", SettlementFileReader.Decode(Thai874.GetBytes("ประเภท")));
    }
}
