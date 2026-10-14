using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 204 ทีม D (2f): แถวย่อยเงินปันผล 4(ข) บน 50 ทวิ — ผู้ใช้เลือกแถวย่อย (1.1–1.4 · 2.1–2.5) + อัตราเครดิต (1.4) ได้ (ไม่บังคับ) ·
/// ตัวตั้งเดียว <see cref="WhtCertFormText.DividendBlock"/> ของทั้ง 3 renderer · <b>ไม่ระบุ ⇒ ผลลัพธ์เหมือนเดิมทุกไบต์</b>
/// </summary>
public class WhtDividendSubRowTests
{
    private static Company Payer() => new() { Name = "บริษัท ผู้จ่ายปันผล จำกัด", TaxId = "0105556000025", BranchCode = "00000" };

    private static WithholdingTaxCert Cert(params WithholdingTaxCertLine[] lines)
    {
        var c = new WithholdingTaxCert
        {
            CertificateNumber = "WHT-202610-0009", TaxFormType = TaxType.WithholdingTax2, TaxYear = 2026, TaxMonth = 10,
            CertificateType = WithholdingTaxCertType.Withhold, Status = WithholdingTaxCertStatus.Issued, IssuedDate = new DateTime(2026, 10, 12),
            PayeeContact = new Contact { Name = "นายผู้ถือหุ้น ทดสอบ", TaxId = "3100600123450" },
            TotalIncomeAmount = lines.Sum(l => l.IncomeAmount), TotalTaxAmount = lines.Sum(l => l.TaxAmount),
        };
        foreach (var l in lines) c.Lines.Add(l);
        return c;
    }

    private static WithholdingTaxCertLine L(string code, decimal income, decimal tax, string? sub = null, decimal? rate = null, int order = 1) => new()
    {
        LineOrder = order, IncomeTypeCode = code, IncomeDescription = "เงินปันผล", PaymentDate = new DateTime(2026, 10, 12),
        IncomeAmount = income, TaxAmount = tax, TaxRate = 10m, DividendSubRow = sub, DividendCreditRate = rate,
    };

    private static string Html(WithholdingTaxCert c) => PdfGenerationService.BuildWithholdingTaxCertHtml(c, Payer());

    // ── ตัวตรวจคำขอ ──

    [Fact]
    public void ไม่ส่งแถวย่อย_ผ่านเสมอ()
    {
        Assert.Null(WhtCertFormText.DividendSubRowProblem("4b", null, null));
        Assert.Null(WhtCertFormText.DividendSubRowProblem("8", "", null));
    }

    [Theory]
    [InlineData("1.1")]
    [InlineData("2.5")]
    public void แถวย่อยถูกต้องบนประเภทปันผล_ผ่าน(string sub)
        => Assert.Null(WhtCertFormText.DividendSubRowProblem("4b", sub, null));

    [Fact]
    public void อัตราอื่น14_รับอัตรา_ปัดสองตำแหน่ง()
    {
        Assert.Null(WhtCertFormText.DividendSubRowProblem("4b", "1.4", 15m));
        Assert.Equal(("1.4", (decimal?)12.35m), WhtCertFormText.NormalizeDividendSubRow(" 1.4 ", 12.345m));
        Assert.Equal(("2.1", (decimal?)null), WhtCertFormText.NormalizeDividendSubRow("2.1", null));
        Assert.Equal(((string?)null, (decimal?)null), WhtCertFormText.NormalizeDividendSubRow("  ", null));
    }

    [Theory]
    [InlineData("8", "1.1", null)]       // ไม่ใช่ประเภทปันผล
    [InlineData("4b", "3.1", null)]      // ไม่มีแถวนี้บนแบบ
    [InlineData("4b", "1.1", 15.0)]      // อัตราใช้เฉพาะ (1.4)
    [InlineData("4b", "1.4", 0.0)]       // อัตราต้อง > 0
    [InlineData("4b", "1.4", 100.0)]     // อัตราต้อง < 100
    [InlineData("4b", null, 15.0)]       // อัตราโดยไม่มีแถวย่อย
    public void ส่งผิด_ปฏิเสธพร้อมทางแก้_ไม่ทิ้งเงียบ(string code, string? sub, double? rate)
        => Assert.NotNull(WhtCertFormText.DividendSubRowProblem(code, sub, rate.HasValue ? (decimal?)rate.Value : null));

    // ── บล็อก 4(ข) ──

    [Fact]
    public void ไม่มีบรรทัดระบุแถวย่อย_บล็อกเป็นnull_HTMLเหมือนเดิมทุกไบต์()
    {
        var plain = Cert(L("4b", 10_000m, 1_000m));
        Assert.Null(WhtCertFormText.DividendBlock(plain.Lines));
        // ทิศตรงข้าม: แถวย่อยบนบรรทัดที่ไม่ใช่ปันผล / รหัสที่ไม่มีบนแบบ ⇒ ไม่มีผลกับบล็อก — HTML เท่ากันทุกไบต์
        var ignored = Cert(L("4b", 10_000m, 1_000m), L("8", 0m, 0m, sub: "1.1", order: 2));
        var unknown = Cert(L("4b", 10_000m, 1_000m, sub: "9.9"));
        Assert.Null(WhtCertFormText.DividendBlock(ignored.Lines));
        Assert.Null(WhtCertFormText.DividendBlock(unknown.Lines));
        var html = Html(plain);
        Assert.Equal(html, Html(unknown));
        Assert.Contains("(1.4) อัตราอื่น ๆ (ระบุ) .................. ของกำไรสุทธิ</div>(2) กรณีผู้ได้รับเงินปันผลไม่ได้รับเครดิตภาษี", html);   // บล็อกเดิม
        Assert.DoesNotContain("class='div-sub'", html);
    }

    [Fact]
    public void ระบุแถวย่อย_ยอดพิมพ์บนแถวย่อยนั้น_อัตรา14เติมในช่อง()
    {
        var c = Cert(L("4b", 10_000m, 1_000m, sub: "1.4", rate: 15m), L("4b", 2_000m, 200m, sub: "2.1", order: 2));
        var block = WhtCertFormText.DividendBlock(c.Lines)!;
        Assert.Equal(11, block.Count);
        var r14 = Assert.Single(block, r => r.SubRow == "1.4");
        Assert.True(r14.HasAmount);
        Assert.Equal(10_000m, r14.IncomeAmount);
        Assert.Equal(1_000m, r14.TaxAmount);
        Assert.Contains("15%", r14.Lines[0].Text);
        Assert.Equal(200m, Assert.Single(block, r => r.SubRow == "2.1").TaxAmount);
        Assert.False(block[0].HasAmount);                                  // ทุกบรรทัดระบุแถวย่อย ⇒ แถวหัวว่าง
        Assert.False(Assert.Single(block, r => r.SubRow == "1.1").HasAmount);
        Assert.Equal(1_200m, block.Sum(r => r.TaxAmount));                 // ยอดไม่หาย/ไม่ซ้ำ

        var html = Html(c);
        Assert.DoesNotContain("(1.4) อัตราอื่น ๆ (ระบุ) .................. ของกำไรสุทธิ", html);
        Assert.Matches(new Regex(@"\(1\.4\) อัตราอื่น ๆ \(ระบุ\) 15% ของกำไรสุทธิ</div></td><td class='dtc'>12 [^<]+ 2569</td><td class='amt'>10,000\.00</td><td class='amt'>1,000\.00</td>"), html);
        Assert.Equal(3 * 11, Regex.Matches(html, "<tr class='div-sub'>").Count);   // ทุกฉบับ (1–3)
    }

    [Fact]
    public void บรรทัดปันผลที่ไม่ระบุแถวย่อยปนกัน_ไปอยู่แถวหัว()
    {
        var block = WhtCertFormText.DividendBlock(new[] { L("4b", 1_000m, 100m), L("4b", 500m, 50m, sub: "1.1", order: 2) })!;
        Assert.Equal(100m, block[0].TaxAmount);
        Assert.Equal(50m, Assert.Single(block, r => r.SubRow == "1.1").TaxAmount);
    }

    // ── ทั้ง 3 renderer ใช้ตัวตั้งเดียว ──

    [Fact]
    public void Renderers_ใช้DividendBlockตัวเดียว_หน้าพิมพ์แสดงจากเซิร์ฟเวอร์()
    {
        var root = RepoRoot();
        var qp = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/PdfGenerationService.WhtCert.cs"));
        Assert.Contains("WhtCertFormText.DividendBlock(lines)", qp);
        var html = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/PdfGenerationService.cs"));
        Assert.Contains("WhtCertFormText.DividendBlock(lines)", html);
        var svc = File.ReadAllText(Path.Combine(root, "Accounting/Services/Implementations/WithholdingTaxCertService.cs"));
        Assert.Contains("DividendBlock: Accounting.Helpers.WhtCertFormText.DividendBlock(w.Lines)", svc);
        Assert.Contains("WhtCertFormText.DividendSubRowProblem(", svc);
        var page = File.ReadAllText(Path.Combine(root, "Accounting/wwwroot/pages/wht.html"));
        var a = page.IndexOf("async printCert(id)", StringComparison.Ordinal);
        var b = page.IndexOf("printWin.document.close();", a, StringComparison.Ordinal);
        var seg = page[a..b];
        Assert.Contains("d.dividendBlock", seg);
        Assert.Contains("Layout.esc(l.text)", seg);
        Assert.Contains("divRowsHtml != null ? divRowsHtml :", seg);
        Assert.Contains("dividendSubRow: divSubVal || null", page);   // ฟอร์มส่ง · เซิร์ฟเวอร์ echo กลับ (DTO) · hydrate ตอนแก้ไข
        Assert.Contains("l.dividendSubRow", page);
        Assert.Equal(typeof(string), typeof(WithholdingTaxCertLineResponse).GetProperty("DividendSubRow")?.PropertyType);
        Assert.NotNull(typeof(WithholdingTaxCertResponse).GetProperty("DividendBlock"));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }
}
