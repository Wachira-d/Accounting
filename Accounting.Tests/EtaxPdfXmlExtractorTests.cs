using System.IO.Compression;
using System.Text;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 (คำตัดสินเจ้าของข้อ 10) — <c>EtaxPdfXmlExtractor</c> กับไฟล์ e-Tax XML <b>จริง</b> ของใบ Shopee
/// (<see cref="EtaxFixtures.ShopeeUptoyouXml"/> · ฝังใน PDF ที่ pdfmake/PDFKit สร้างในชื่อ <c>ETDA-invoice.xml</c>)
///
/// <para><b>ครึ่งที่ 1</b> (ช่องว่างที่พบกับไฟล์จริง):
/// ① BOM นำหน้า ⇒ <c>XDocument.Parse</c> เคยปฏิเสธทั้งไฟล์ · ② ยอดบรรทัดอยู่ใน <c>NetLineTotalAmount</c> (ไม่มี <c>LineTotalAmount</c>) ⇒
/// เดิมยอดบรรทัดว่าง · ③ TypeCode <c>T03</c> (ใบเสร็จ/ใบกำกับ) เคยถูก map เป็น Receipt ⇒ กระดาษถูกตีว่าไม่ใช่ใบกำกับ ·
/// ④ PDF ที่มี Filespec ตัวแรกไม่ใช่ .xml ⇒ <c>IndexOf(…, −1)</c> โยน exception ทิ้งทั้งไฟล์ · ⑤ ชื่อไฟล์ UTF-16 แบบ hex ·
/// ⑥ ชื่อไฟล์บนเส้น <c>/Subtype /text#2Fxml</c> เป็น "embedded.xml" เสมอ (regex ที่ไม่เคยแมตช์) ·
/// ⑦ dict ที่มี <c>octet-stream</c> ทำให้ตัวหาคำ "stream" ตัดสตรีมผิดตำแหน่ง</para>
/// <para><b>ครึ่งที่ 2</b> (ห้ามแตะ): PDF ไม่มีไฟล์แนบ / ไม่ใช่ PDF ⇒ null (ตกไป OCR ตามเดิม) · XML ที่ไม่ใช่ CrossIndustryInvoice ⇒ null ·
/// รหัส 388/80/81/380 ยังได้ชนิดเดิม · ใบอย่างย่อ T05/T06 ไม่กลายเป็นใบเต็มรูป</para>
/// </summary>
public class EtaxPdfXmlExtractorTests
{
    // ── ตัวสร้าง PDF จำลองโครงแบบ PDFKit (ข้อความ ASCII + สตรีม binary · \r\n รอบสตรีมเพื่อไม่ให้การตัดท้ายกินข้อมูล) ──

    private static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    private static byte[] BuildPdf(string xmlSubtype, string fileNameToken, bool withLogoFirst, bool compress = true)
    {
        var xmlBytes = Encoding.UTF8.GetBytes(EtaxFixtures.Bom + EtaxFixtures.ShopeeUptoyouXml);
        var payload = compress ? Zlib(xmlBytes) : xmlBytes;
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.3\n%ÿÿÿÿ\n");
        W("1 0 obj\n<< /Type /Catalog /Names << /EmbeddedFiles 2 0 R >> >>\nendobj\n");
        W("2 0 obj\n<< /Names [(logo.png) 4 0 R (ETDA-invoice.xml) 7 0 R] >>\nendobj\n");
        if (withLogoFirst)
        {
            W("4 0 obj\n<< /Type /Filespec /F (logo.png) /EF << /F 5 0 R >> >>\nendobj\n");
            W("5 0 obj\n<< /Type /EmbeddedFile /Subtype /image#2Fpng /Length 4 >>\nstream\r\nabcd\r\nendstream\nendobj\n");
        }
        W($"6 0 obj\n<< /Type /EmbeddedFile {xmlSubtype}{(compress ? " /Filter /FlateDecode" : "")} /Length {payload.Length} >>\nstream\r\n");
        ms.Write(payload);
        W("\r\nendstream\nendobj\n");
        W($"7 0 obj\n<< /Type /Filespec /F {fileNameToken} /UF {fileNameToken} /EF << /F 6 0 R >> >>\nendobj\n");
        W("trailer\n<< /Root 1 0 R >>\n%%EOF\n");
        return ms.ToArray();
    }

    private static string Utf16Hex(string s)
        => "<FEFF" + string.Concat(Encoding.BigEndianUnicode.GetBytes(s).Select(b => b.ToString("X2"))) + ">";

    private static void AssertShopee(EtaxPdfXmlExtractor.ExtractResult? r)
    {
        Assert.NotNull(r);
        Assert.True(r!.Found);
        Assert.Equal("INV2026090199", r.DocumentNumber);
        Assert.Equal("T03", r.DocumentTypeCode);
        Assert.Equal("TaxInvoice", r.MappedDocumentType);
        Assert.Equal(new DateTime(2026, 9, 17), r.DocumentDate!.Value.Date);
        Assert.Equal("ห้างหุ้นส่วนจำกัด อัพทูยู บีเค", r.SellerName);
        Assert.Equal("0123565003005", r.SellerTaxId);
        Assert.Equal("00000", r.SellerBranchCode);
        Assert.Equal("ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส", r.BuyerName);
        Assert.Equal("0203562005871", r.BuyerTaxId);
        Assert.Equal("00000", r.BuyerBranchCode);
        Assert.Equal(500.93m, r.LineTotal);
        Assert.Equal(500.93m, r.TaxBasis);
        Assert.Equal(35.07m, r.VatAmount);
        Assert.Equal(536.00m, r.GrandTotal);
        var line = Assert.Single(r.Items);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(250.47m, line.UnitPrice);
        Assert.Equal(500.93m, line.Amount);   // NetLineTotalAmount — ไม่ใช่ 250.47 × 2 = 500.94
        Assert.Contains("ไฮยีน", line.Description);
    }

    // ── ครึ่งที่ 1 ──────────────────────────────────────────────────────────

    [Fact]
    public void XMLจริงShopee_อ่านครบทุกช่อง_T03เป็นใบกำกับ_ยอดบรรทัดจากNetLineTotal()
        => AssertShopee(EtaxPdfXmlExtractor.ParseEtaxXml(EtaxFixtures.ShopeeUptoyouXml));

    [Fact]
    public void XMLจริงที่มีBOMนำหน้า_ยังอ่านได้()
        => AssertShopee(EtaxPdfXmlExtractor.ParseEtaxXml(EtaxFixtures.Bom + EtaxFixtures.ShopeeUptoyouXml));

    [Fact]
    public void PDFแบบPDFKit_Filespecตัวแรกเป็นรูป_ไม่มีSubtypeXml_ยังเจอXML()
    {
        var pdf = BuildPdf("/Subtype /application#2Foctet-stream", "(ETDA-invoice.xml)", withLogoFirst: true);
        var r = EtaxPdfXmlExtractor.TryExtract(pdf);
        AssertShopee(r);
        Assert.Equal("ETDA-invoice.xml", r!.AttachmentFileName);
    }

    [Fact]
    public void PDFที่ชื่อไฟล์เป็นUTF16hex_ยังเจอXML()
    {
        var pdf = BuildPdf("", Utf16Hex("ETDA-invoice.xml"), withLogoFirst: true);
        var r = EtaxPdfXmlExtractor.TryExtract(pdf);
        AssertShopee(r);
        Assert.Equal("ETDA-invoice.xml", r!.AttachmentFileName);
    }

    [Fact]
    public void PDFเส้นSubtypeTextXml_ได้ชื่อไฟล์จริงไม่ใช่embedded()
    {
        var pdf = BuildPdf("/Subtype /text#2Fxml", "(ETDA-invoice.xml)", withLogoFirst: true);
        var r = EtaxPdfXmlExtractor.TryExtract(pdf);
        AssertShopee(r);
        Assert.Equal("ETDA-invoice.xml", r!.AttachmentFileName);
    }

    [Fact]
    public void PDFที่สตรีมไม่บีบอัด_ยังอ่านได้()
        => AssertShopee(EtaxPdfXmlExtractor.TryExtract(
            BuildPdf("/Subtype /text#2Fxml", "(ETDA-invoice.xml)", withLogoFirst: false, compress: false)));

    [Theory]
    [InlineData("T02", "TaxInvoice")]
    [InlineData("T03", "TaxInvoice")]
    [InlineData("T04", "TaxInvoice")]
    [InlineData("t03", "TaxInvoice")]
    public void รหัสใบกำกับเต็มรูปทุกแบบ_เป็นTaxInvoice(string code, string expected)
        => Assert.Equal(expected, EtaxPdfXmlExtractor.MapTypeCodeToInternal(code, "TaxInvoice_CrossIndustryInvoice"));

    // ── ครึ่งที่ 2 (ห้ามแตะ) ────────────────────────────────────────────────

    [Theory]
    [InlineData("388", "TaxInvoice")]
    [InlineData("380", "Invoice")]
    [InlineData("80", "DebitNote")]
    [InlineData("81", "CreditNote")]
    [InlineData("T01", "Receipt")]
    [InlineData("T05", "Receipt")]   // ใบกำกับอย่างย่อ ≠ เต็มรูป (§82/5(2))
    [InlineData("T06", "Receipt")]
    public void รหัสอื่น_ได้ชนิดเดิม(string code, string expected)
        => Assert.Equal(expected, EtaxPdfXmlExtractor.MapTypeCodeToInternal(code, "TaxInvoice_CrossIndustryInvoice"));

    [Fact]
    public void ใบแจ้งยกเลิกT07_ไม่ใช่เอกสารลงบัญชี_และไม่มีรหัสใช้รากเอกสาร()
    {
        Assert.Null(EtaxPdfXmlExtractor.MapTypeCodeToInternal("T07", "TaxInvoice_CrossIndustryInvoice"));
        Assert.Equal("DebitNote", EtaxPdfXmlExtractor.MapTypeCodeToInternal(null, "DebitCreditNote_CrossIndustryInvoice"));
        Assert.Equal("Receipt", EtaxPdfXmlExtractor.MapTypeCodeToInternal(null, "Receipt_CrossIndustryInvoice"));
    }

    [Theory]
    [InlineData("T05")]
    [InlineData("t06")]
    public void ใบอย่างย่อT05T06_ปิดเคลมภาษีซื้อ82_5_2(string code)
    {
        // ฝ่ายค้าน P6: map เป็น Receipt แล้วเส้นสร้างเอกสารพัก 11640 รอใบจริง — ใบอย่างย่อไม่มีวันเคลมได้
        var msg = EtaxPdfXmlExtractor.AbbreviatedClaimBlock(code);
        Assert.NotNull(msg);
        Assert.Contains("82/5(2)", msg);
    }

    [Theory]
    [InlineData("T03")]
    [InlineData("388")]
    [InlineData("T01")]
    [InlineData(null)]
    public void ใบเต็มรูปหรือใบรับธรรมดา_ไม่ใช่ใบอย่างย่อ(string? code)
        => Assert.Null(EtaxPdfXmlExtractor.AbbreviatedClaimBlock(code));

    [Fact]
    public void PDFไม่มีไฟล์แนบ_หรือไม่ใช่PDF_คืนnull()
    {
        var noEmbed = Encoding.Latin1.GetBytes("%PDF-1.3\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");
        Assert.Null(EtaxPdfXmlExtractor.TryExtract(noEmbed));
        Assert.Null(EtaxPdfXmlExtractor.TryExtract(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00 }));   // JPEG
    }

    [Fact]
    public void XMLที่ไม่ใช่เอกสารeTax_คืนnull()
    {
        Assert.Null(EtaxPdfXmlExtractor.ParseEtaxXml("<Invoice><ID>1</ID></Invoice>"));
        Assert.Null(EtaxPdfXmlExtractor.ParseEtaxXml("ไม่ใช่ XML"));
    }
}
