using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using UglyToad.PdfPig;
using Xunit;

namespace Accounting.Tests;

/// <summary>บั๊กผู้ใช้ (ทีม J 2026-10-22): "ใบ 50 ทวิ ไม่แสดงตราประทับ ทั้งดูบนเว็บ · ดาวน์โหลด PDF · พิมพ์" ·
/// ต้นเหตุ: ทางออก 50 ทวิทั้งสาม (HTML <c>BuildWithholdingTaxCertHtml</c> · QuestPDF <c>BuildWhtCertPdf</c> · หน้าพิมพ์ <c>wht.html</c>)
/// ไม่เคยอ่าน <c>CompanySettings.StampPath</c> — วาดแค่วงกลมว่าง ขณะที่เอกสารขายประทับได้เพราะ <c>BuildBranding</c> อ่านตรา ·
/// เทสต์ล็อกทั้ง "ใบที่ควรมีตรามีตรา" และ "ใบร่าง/บริษัทไม่มีตรา เหมือนเดิมทุกไบต์"</summary>
public class WhtCertStampTests
{
    // PNG 1×1 จริง (≥ 12 ไบต์ ผ่าน LooksLikeImage)
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static Company Payer() => new()
    {
        Name = "บริษัท ตราประทับ จำกัด", TaxId = "0105556000005", BranchCode = "00000",
        Address = "99/1", Province = "กรุงเทพมหานคร", PostalCode = "10120",
    };

    private static WithholdingTaxCert Cert(WithholdingTaxCertStatus status = WithholdingTaxCertStatus.Issued)
    {
        var c = new WithholdingTaxCert
        {
            CertificateNumber = "WHT-2026-10-0009", TaxFormType = TaxType.WithholdingTax53, TaxYear = 2026, TaxMonth = 10,
            CertificateType = WithholdingTaxCertType.Withhold, Status = status, IssuedDate = new DateTime(2026, 10, 20),
            PayeeContact = new Contact { Name = "บริษัท ผู้รับเงิน จำกัด", TaxId = "0107538000533", Address = "1 ถนนพระราม 3" },
            TotalIncomeAmount = 10000m, TotalTaxAmount = 300m,
        };
        c.Lines.Add(new WithholdingTaxCertLine
        {
            LineOrder = 1, IncomeTypeCode = "8", IncomeDescription = "ค่าบริการ", PaymentDate = new DateTime(2026, 10, 20),
            IncomeAmount = 10000m, TaxRate = 3m, TaxAmount = 300m,
        });
        return c;
    }

    private static PdfGenerationService.WhtCertStamp Stamp(decimal w = 24m, decimal x = 0m, decimal y = 0m)
        => new(Png, w, w, x, y);

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }

    private static int ImagesOn(byte[] pdf, int page)
    {
        using var doc = PdfDocument.Open(pdf);
        return doc.GetPage(page).GetImages().Count();
    }

    // ───────── HTML (เส้น Chromium) ─────────

    [Fact]
    public void Html_WithStamp_PrintsTheSealInEveryCopy()
    {
        var html = PdfGenerationService.BuildWithholdingTaxCertHtml(Cert(), Payer(), stamp: Stamp(26m, 2m, -1m));
        Assert.Equal(WhtCertFormText.Copies.Count, Regex.Matches(html, "<div class='stamp-area has-stamp'><img class='stamp-img' src='data:image/png;base64,").Count);
        Assert.Contains("width:26mm;height:26mm;transform:translate(calc(-50% + 2mm), calc(-50% + -1mm))", html);
        Assert.DoesNotContain("<div class='stamp-area'>ประทับตรา", html);   // ไม่วางข้อความทับตรา
    }

    [Fact]
    public void Html_WithoutStamp_KeepsTheOfficialPlaceholderByteForByte()
    {
        var html = PdfGenerationService.BuildWithholdingTaxCertHtml(Cert(), Payer());
        Assert.Equal(WhtCertFormText.Copies.Count, Regex.Matches(html, "<div class='stamp-area'>ประทับตรา<br>นิติบุคคล<br>\\(ถ้ามี\\)</div></td>").Count);
        Assert.DoesNotContain("stamp-img", html);
    }

    [Fact]
    public void Html_Signature_SitsOnTheDottedLine_WithoutPrintingTheName_LikeQuestPdfAndPrintPage()
    {
        var html = PdfGenerationService.BuildWithholdingTaxCertHtml(Cert(), Payer(), Convert.ToBase64String(Png), "ชื่อผู้ลงนาม");
        Assert.Equal(3, Regex.Matches(html, "<span class='sig-dots'><img class='sig-img' src='data:image/png;base64,").Count);
        Assert.DoesNotContain("ชื่อผู้ลงนาม", html);
        // ลายเซ็นที่ไม่ใช่ base64 ล้วน (ค่าเสีย/แทรก attribute) ไม่ถูกพิมพ์ — เส้นจุดว่างแทน
        var bad = PdfGenerationService.BuildWithholdingTaxCertHtml(Cert(), Payer(), "x' onerror='alert(1)", null);
        Assert.DoesNotContain("onerror", bad);
        Assert.Equal(3, Regex.Matches(bad, "<span class='sig-dots'></span> ผู้จ่ายเงิน</div>").Count);
    }

    [Fact]
    public void Html_VoidedCert_HasTheCancelWatermark_LikeQuestPdfAndPrintPage()
    {
        var voided = PdfGenerationService.BuildWithholdingTaxCertHtml(Cert(WithholdingTaxCertStatus.Voided), Payer());
        Assert.Equal(3, Regex.Matches(voided, "<div class='void-watermark'>ยกเลิก<small>CANCELLED</small></div>").Count);
        Assert.DoesNotContain("<div class='void-watermark'>", PdfGenerationService.BuildWithholdingTaxCertHtml(Cert(), Payer()));
    }

    // ───────── QuestPDF (ไฟล์ที่ดาวน์โหลด) ─────────

    [Fact]
    public void QuestPdf_WithStamp_EmbedsTheSealOnEveryPage_StillOnePagePerCopy()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var with = PdfGenerationService.BuildWhtCertPdf(Cert(), Payer(), stamp: Stamp(30m, 3m, 2m));
        var without = PdfGenerationService.BuildWhtCertPdf(Cert(), Payer());
        using var doc = PdfDocument.Open(with);
        Assert.Equal(WhtCertFormText.Copies.Count, doc.NumberOfPages);   // ตราใหญ่กว่าวง (Unconstrained) ไม่ดันฉบับล้นหน้า
        for (var p = 1; p <= doc.NumberOfPages; p++)
            Assert.True(ImagesOn(with, p) > ImagesOn(without, p), $"หน้า {p} ไม่มีรูปตรา");
    }

    // ───────── ตัวตัดสิน + ไฟล์ตราจริง ─────────

    [Fact]
    public void ResolveWhtCertStamp_IssuedWithUploadedSeal_Stamps_DraftOrNoSeal_DoesNot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"seal-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Png);
        try
        {
            var s = new CompanySettings { StampPath = path, StampWidthMm = 40m, StampHeightMm = 40m };
            var issued = PdfGenerationService.ResolveWhtCertStamp(Cert(), s);
            Assert.NotNull(issued);
            Assert.Equal(DocumentStampPolicy.WhtDefaultSizeMm, issued!.WidthMm);
            Assert.Null(PdfGenerationService.ResolveWhtCertStamp(Cert(WithholdingTaxCertStatus.Draft), s));
            Assert.Null(PdfGenerationService.ResolveWhtCertStamp(Cert(), new CompanySettings()));   // ยังไม่อัปโหลดตรา
            Assert.Null(PdfGenerationService.ResolveWhtCertStamp(Cert(), null));
            // กฎตั้งเอง: ปิดตราบน 50 ทวิ ⇒ ไม่ประทับ · เปิดให้ร่าง ⇒ ร่างประทับ
            s.DocumentStampRulesJson = """{"WithholdingTaxCert":{"stamp":[]}}""";
            Assert.Null(PdfGenerationService.ResolveWhtCertStamp(Cert(), s));
            s.DocumentStampRulesJson = """{"WithholdingTaxCert":{"stamp":["Draft"],"widthMm":18,"heightMm":18,"offsetXMm":-4}}""";
            var draft = PdfGenerationService.ResolveWhtCertStamp(Cert(WithholdingTaxCertStatus.Draft), s);
            Assert.NotNull(draft);
            Assert.Equal(18m, draft!.WidthMm);
            Assert.Equal(-4m, draft.OffsetXMm);
        }
        finally { File.Delete(path); }
    }

    // ───────── WYSIWYG: สไตล์ HTML ฝั่งเซิร์ฟเวอร์ = หน้าพิมพ์ wht.html ทุกบรรทัด ─────────

    [Fact]
    public void ServerCss_IsIdenticalTo_PrintPageCss()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/wwwroot/pages/wht.html"));
        var a = page.IndexOf("<style>\n  @page { size: A4; margin: 4mm 6mm; }", StringComparison.Ordinal);
        Assert.True(a > 0, "ไม่พบ <style> ของหน้าพิมพ์ 50 ทวิ");
        var b = page.IndexOf("</style>", a, StringComparison.Ordinal);
        static string[] Norm(string css) => css.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToArray();
        Assert.Equal(Norm(page[(a + "<style>\n".Length)..b]), Norm(PdfGenerationService.WhtCertCss));
        Assert.Contains(".stamp-img", PdfGenerationService.WhtCertCss);
    }

    [Fact]
    public void PrintPage_TakesStampAndSignatureFromTheServer_NotTheViewersProfile()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/wwwroot/pages/wht.html"));
        var a = page.IndexOf("async printCert(id) {", StringComparison.Ordinal);
        var b = page.IndexOf("addLine() {", a, StringComparison.Ordinal);
        Assert.True(a > 0 && b > a);
        var fn = page[a..b];
        Assert.Contains("api.getWhtCertPrintDecoration(id)", fn);
        Assert.Contains("${stampHtml}", fn);
        Assert.DoesNotContain("API.getProfile", fn);   // เดิม: ลายเซ็นของผู้ที่กดพิมพ์ ⇒ คนละคนกับ PDF
    }
}
