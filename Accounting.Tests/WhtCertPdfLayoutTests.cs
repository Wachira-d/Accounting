using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using UglyToad.PdfPig;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// 50 ทวิ ไฟล์ PDF (QuestPDF) ต้องหน้าตาเท่าหน้าพิมพ์ (wht.html printCert) — ผู้ใช้รายงาน 2026-10-16:
/// "pdf ที่ให้ download กับหน้ากดพิมพ์ หน้าตาไม่เหมือนกัน · pdf download หน้าล้นด้วย" ·
/// ก่อนแก้: ขอบ 10 มม. + ตัวอักษร/ช่องไฟใหญ่กว่า CSS ⇒ ช่องลงชื่อตกไปหน้าถัดไป ไฟล์ 3 ฉบับกลายเป็น 6 หน้า ·
/// ☐/☑ ไม่มีในฟอนต์ ⇒ กล่องว่างทุกช่อง (ไม่รู้ว่าติ๊กแบบไหน) · ตราประทับสี่เหลี่ยม
/// </summary>
public class WhtCertPdfLayoutTests
{
    private static (WithholdingTaxCert Cert, Company Company) Sample()
    {
        var company = new Company
        {
            Name = "บริษัท ทดสอบการพิมพ์ จำกัด", TaxId = "0105556000005", BranchCode = "00000",
            Address = "99/1", BuildingName = "อาคารสาทรทาวเวอร์", StreetName = "สาทรใต้", SubDistrict = "ยานนาวา",
            District = "สาทร", Province = "กรุงเทพมหานคร", PostalCode = "10120",
        };
        var contact = new Contact
        {
            Name = "สมชาย ใจดี", TitleTh = "นาย", TaxId = "3100500123456", BranchCode = "00000",
            Address = "12", Moo = "3", SubDistrict = "บางพูด", District = "ปากเกร็ด", Province = "นนทบุรี", PostalCode = "11120",
        };
        var cert = new WithholdingTaxCert
        {
            CertificateNumber = "WHT-2026-10-0001", PayeeContact = contact, TaxFormType = TaxType.WithholdingTax3,
            TaxYear = 2026, TaxMonth = 10, CertificateType = WithholdingTaxCertType.Withhold, Status = WithholdingTaxCertStatus.Issued,
            TotalIncomeAmount = 25000m, TotalTaxAmount = 750m, IssuedDate = new DateTime(2026, 10, 15),
        };
        cert.Lines.Add(new WithholdingTaxCertLine
        {
            LineOrder = 1, IncomeTypeCode = "8", IncomeDescription = "ค่าบริการ", PaymentDate = new DateTime(2026, 10, 15),
            IncomeAmount = 25000m, TaxRate = 3m, TaxAmount = 750m,
        });
        return (cert, company);
    }

    private static int Pages(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }

    [Fact]
    public void ใบปกติ_สามฉบับ_สามหน้า_ไม่ล้น()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var (cert, company) = Sample();
        var pdf = PdfGenerationService.BuildWhtCertPdf(cert, company);
        Assert.Equal(WhtCertFormText.Copies.Count, Pages(pdf));   // ก่อนแก้ = 6 (ฉบับละ 2 หน้า)
    }

    [Fact]
    public void ข้อมูลยาวผิดปกติ_ยกเลิก_ปันผลแถวย่อย_แถว6_กองทุน_ลายเซ็น_ยังฉบับละหนึ่งหน้า()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var (cert, company) = Sample();
        company.Name = "บริษัท ทดสอบการพิมพ์หนังสือรับรองการหักภาษี ณ ที่จ่ายชื่อยาวมากเป็นพิเศษเพื่อทดสอบการตัดบรรทัด จำกัด (มหาชน)";
        company.BuildingName = "อาคารสาทรทาวเวอร์ ชั้น 25 ห้อง 2501-2505 โครงการสาทรซิตี้ทาวเวอร์ส";
        cert.PayeeContact.Name = "สมชายผู้มีชื่อยาวมากเป็นพิเศษ ใจดีมีเมตตากรุณาปรานี";
        cert.Status = WithholdingTaxCertStatus.Voided;
        cert.CertificateNumber = "WHT-BR00001-2026-10-000123";
        cert.Lines.Add(new WithholdingTaxCertLine
        {
            LineOrder = 2, IncomeTypeCode = "4b", IncomeDescription = "เงินปันผล", PaymentDate = new DateTime(2026, 10, 1),
            IncomeAmount = 100000m, TaxRate = 10m, TaxAmount = 10000m, DividendSubRow = "1.1",
        });
        cert.Lines.Add(new WithholdingTaxCertLine
        {
            LineOrder = 3, IncomeTypeCode = "99", IncomeDescription = "ค่าตอบแทนพิเศษกรณีอื่น ๆ ที่ไม่เข้าประเภทใดชื่อยาวมาก",
            PaymentDate = new DateTime(2026, 10, 2), IncomeAmount = 5000m, TaxRate = 3m, TaxAmount = 150m,
        });
        cert.TotalIncomeAmount = 130000m;
        cert.TotalTaxAmount = 10900m;
        var sig = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var pdf = PdfGenerationService.BuildWhtCertPdf(cert, company, sig, "ผู้ลงนาม", new WhtCertFundAmounts(null, 750m, 1500m));
        Assert.Equal(WhtCertFormText.Copies.Count, Pages(pdf));
    }

    [Fact]
    public void ไฟล์ใช้ฟอนต์Sarabunเหมือนหน้าพิมพ์_และไม่มีอักขระช่องติ๊กที่ฟอนต์ไม่มี()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var (cert, company) = Sample();
        using var doc = PdfDocument.Open(PdfGenerationService.BuildWhtCertPdf(cert, company));
        var page = doc.GetPage(1);
        // ฟอนต์ฝังมากับแอป (Accounting/Fonts) — เดิมเซิร์ฟเวอร์ไม่มี Sarabun ⇒ ตกไปฟอนต์ระบบ หน้าตาต่างจากหน้าพิมพ์
        Assert.Contains(page.Letters, l => l.FontName.Contains("Sarabun", StringComparison.OrdinalIgnoreCase));
        // ☐/☑ วาดเป็นรูป (SVG) — ตัวอักษรนี้ไม่มีใน Sarabun เดิมพิมพ์เป็นกล่องว่างทั้งช่องติ๊กและช่องไม่ติ๊ก
        Assert.DoesNotContain(page.Letters, l => l.Value is "☐" or "☑");
        // ทิศตรงข้าม: ข้อมูลยังอยู่ครบ (ไม่ได้หายไปกับการย่อ/วาดรูป) · ชื่อ/เลขที่ไม่ถูกแทรกจุดตัด (โปรแกรมอ่าน PDF ดึงไปใช้)
        var text = string.Concat(page.Letters.Select(l => l.Value));
        Assert.Contains("WHT-2026-10-0001", text);
        Assert.Contains("25,000.00", text);
        Assert.Contains("750.00", text);
        Assert.Contains("0-1055-56000-00-5", text);
    }

    [Fact]
    public void ขอบหน้าและฟอนต์ตั้งต้นตรงกับCSSของหน้าพิมพ์()
    {
        var root = RepoRoot();
        var page = File.ReadAllText(Path.Combine(root, "Accounting/wwwroot/pages/wht.html"));
        var qp = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/PdfGenerationService.WhtCert.cs"));
        // ถ้าแก้ CSS ของหน้าพิมพ์ ต้องแก้ QuestPDF คู่กัน (กฎ #4 A สอง renderer ห้าม drift)
        Assert.Contains("@page { size: A4; margin: 4mm 6mm; }", page);
        Assert.Contains("page.MarginVertical(4, Unit.Millimetre);", qp);
        Assert.Contains("page.MarginHorizontal(6, Unit.Millimetre);", qp);
        Assert.Matches(new Regex(@"body \{[^}]*font-size: 13\.5px;[^}]*line-height: 1\.2;", RegexOptions.Singleline), page);
        Assert.Contains("FontSize(Px(13.5f)).LineHeight(1.2f)", qp);
        // ฟอนต์ที่หน้าพิมพ์ใช้ต้องอยู่ในแอป (ไม่พึ่งเครื่องเซิร์ฟเวอร์)
        foreach (var f in new[] { "Sarabun-Regular.ttf", "Sarabun-Bold.ttf", "Sarabun-SemiBold.ttf", "Sarabun-Italic.ttf", "Sarabun-BoldItalic.ttf", "OFL.txt" })
            Assert.True(File.Exists(Path.Combine(root, "Accounting/Fonts", f)), f);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }
}
