using System.Text.RegularExpressions;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// แบบ 50 ทวิ — ข้อความของแบบอยู่ที่ <see cref="WhtCertFormText"/> ที่เดียว (เจ้าของ 2026-10-12: "ตัวหนังสือและรูปแบบทำให้ตรง")
///
/// <para>ล็อก: (1) HTML renderer พิมพ์ข้อความแบบ<b>เหมือนเดิมทุกไบต์</b> (ชิ้นส่วนทองคำคัดลอกจาก renderer ก่อนย้ายข้อความ — หน้าตาที่เจ้าของยอมรับ)
/// ยกเว้นจุดแก้สามจุด: "ลำดับที่ ในแบบ" ไม่พิมพ์เลขที่หนังสือรับรอง · ช่องเลขเดิม 10 หลักว่าง กลุ่ม 1-4-4-1 · แถว 6 พิมพ์รายการ ·
/// (2) ไฟล์ QuestPDF ไม่มีข้อความภาษาไทยเป็น literal (= ไม่มีสำเนาที่สอง) · (3) หน้าพิมพ์ใน <c>wht.html</c> (renderer ตัวที่ผู้ใช้พิมพ์จริง)
/// มีข้อความแบบครบทุกคำ</para>
/// </summary>
public class WhtCertFormTextTests
{
    private static Company Payer() => new()
    {
        Name = "หจก.แอม แฮปปี้เนส", TaxId = "0203562005871", BranchCode = "00000",
        Address = "202/24 ซอย บ้านห้วยกุ่ม 4", Province = "ชลบุรี", PostalCode = "20110",
    };

    private static WithholdingTaxCert Cert(params WithholdingTaxCertLine[] lines)
    {
        var c = new WithholdingTaxCert
        {
            CertificateNumber = "WHT-202610-0002",
            TaxFormType = TaxType.WithholdingTax53,
            TaxYear = 2026, TaxMonth = 10,
            CertificateType = WithholdingTaxCertType.Withhold,
            Status = WithholdingTaxCertStatus.Issued,
            IssuedDate = new DateTime(2026, 10, 12),
            PayeeContact = new Contact { Name = "บริษัท ทิพยประกันภัย จำกัด (มหาชน)", TaxId = "0107538000533", Address = "1115 พระราม 3" },
            TotalIncomeAmount = lines.Sum(l => l.IncomeAmount),
            TotalTaxAmount = lines.Sum(l => l.TaxAmount),
        };
        foreach (var l in lines) c.Lines.Add(l);
        return c;
    }

    private static WithholdingTaxCertLine Line(string code, string desc = "", int order = 1) => new()
    {
        LineOrder = order, IncomeTypeCode = code, IncomeDescription = desc,
        PaymentDate = new DateTime(2026, 10, 12), IncomeAmount = 7264m, TaxAmount = 72.64m,
    };

    private static string Html(params WithholdingTaxCertLine[] lines) =>
        PdfGenerationService.BuildWithholdingTaxCertHtml(Cert(lines), Payer());

    // ───────── HTML เหมือนเดิมทุกไบต์ (ชิ้นส่วนคัดลอกจาก renderer ก่อนย้ายข้อความ) ─────────

    public static IEnumerable<object[]> GoldenFragments() => new[]
    {
        "<div class='copy-active'><b>ฉบับที่ 1</b> <i>(สำหรับผู้ถูกหักภาษี ณ ที่จ่าย ใช้แนบพร้อมกับแบบแสดงรายการภาษี)</i></div>",
        "<div><b>ฉบับที่ 3</b> <i>(สำหรับผู้หักภาษี ณ ที่จ่าย เก็บไว้เป็นหลักฐาน)</i></div>",
        "<td style='text-align:center'><div class='title-main'>หนังสือรับรองการหักภาษี ณ ที่จ่าย</div><div class='title-sub'>ตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร</div></td>",
        "<td class='sec-hdr' style='width:210px;vertical-align:top'>ผู้มีหน้าที่หักภาษี ณ ที่จ่าย :-</td><td style='white-space:nowrap;text-align:right'>เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)<span style='color:#900'>*</span> ",
        "<div class='hint-text'>(ให้ระบุว่าเป็น บุคคล นิติบุคคล บริษัท สมาคม หรือคณะบุคคล)</div>",
        "<div class='hint-text'>(ให้ระบุ ชื่ออาคาร/หมู่บ้าน ห้องเลขที่ ชั้นที่ เลขที่ ตรอก/ซอย หมู่ที่ ถนน ตำบล/แขวง อำเภอ/เขต จังหวัด)</div>",
        "<div class='hint-text' style='padding-left:0;margin-top:2px'>(ให้สามารถอ้างอิงหรือสอบยันกันได้ระหว่างลำดับที่ตามหนังสือรับรองฯ กับแบบยื่นรายการภาษีหักที่จ่าย)</div></td>",
        "<td><span class='ck'>&#9744;</span> <b>(2) ภ.ง.ด.1ก พิเศษ</b></td><td><span class='ck'>&#9744;</span> <b>(3) ภ.ง.ด.2</b></td>",
        "<td><span class='ck'>&#9744;</span> <b>(5) ภ.ง.ด.2ก</b></td><td><span class='ck'>&#9744;</span> <b>(6) ภ.ง.ด.3ก</b></td><td><span class='ck'>&#9745;</span> <b>(7) ภ.ง.ด.53</b></td><td></td></tr>",
        "<th style='width:46%'>ประเภทเงินได้พึงประเมินที่จ่าย</th><th style='width:14%'>วัน เดือน<br>หรือปีภาษี ที่จ่าย</th><th style='width:20%'>จำนวนเงินที่จ่าย</th><th style='width:20%'>ภาษีที่หัก<br>และนำส่งไว้</th>",
        "<tr><td class='it-type'>4. (ก) ดอกเบี้ย ฯลฯ ตามมาตรา 40 (4) (ก)</td>",
        "&nbsp;&nbsp;&nbsp;(ข) เงินปันผล เงินส่วนแบ่งกำไร ฯลฯ ตามมาตรา 40 (4) (ข)<div class='div-indent' style='margin-top:2px'>(1) กรณีผู้ได้รับเงินปันผลได้รับเครดิตภาษี โดยจ่ายจาก<br>&nbsp;&nbsp;&nbsp;&nbsp;กำไรสุทธิของกิจการที่ต้องเสียภาษีเงินได้นิติบุคคลในอัตราดังนี้<div class='div-indent'>(1.1) อัตราร้อยละ 30 ของกำไรสุทธิ<br>(1.2) อัตราร้อยละ 25 ของกำไรสุทธิ<br>(1.3) อัตราร้อยละ 20 ของกำไรสุทธิ<br>(1.4) อัตราอื่น ๆ (ระบุ) .................. ของกำไรสุทธิ</div>(2) กรณีผู้ได้รับเงินปันผลไม่ได้รับเครดิตภาษี เนื่องจากจ่ายจาก<div class='div-indent'>(2.1) กำไรสุทธิของกิจการที่ได้รับยกเว้นภาษีเงินได้นิติบุคคล<br>(2.2) เงินปันผลหรือเงินส่วนแบ่งของกำไรที่ได้รับยกเว้น<br>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;ไม่ต้องนำมารวมคำนวณเป็นรายได้เพื่อเสียภาษีเงินได้นิติบุคคล<br>(2.3) กำไรสุทธิส่วนที่ได้หักผลขาดทุนสุทธิยกมาไม่เกิน 5 ปี<br>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;ก่อนรอบระยะเวลาบัญชีปีปัจจุบัน<br>(2.4) กำไรที่รับรู้ทางบัญชีโดยวิธีส่วนได้เสีย (equity method)<br>(2.5) อื่น ๆ (ระบุ) ........................................................</div></div></td>",
        "<td class='it-type it-s5'>5. การจ่ายเงินได้ที่ต้องหักภาษี ณ ที่จ่าย ตามคำสั่งกรมสรรพากรที่ออกตามมาตรา 3 เตรส เช่น รางวัล ส่วนลดหรือประโยชน์ใด ๆ เนื่องจากการส่งเสริมการขาย รางวัลในการประกวด การแข่งขัน การชิงโชค ค่าแสดงของนักแสดงสาธารณะ ค่าจ้างทำของ ค่าโฆษณา ค่าเช่า ค่าขนส่ง ค่าบริการ ค่าเบี้ยประกันวินาศภัย ฯลฯ</td>",
        "<tr class='total-row'><td colspan='2' style='text-align:right'>รวมเงินที่จ่ายและภาษีที่หักนำส่ง</td>",
        "<b>รวมเงินภาษีที่หักนำส่ง</b> <i>(ตัวอักษร)</i>",
        "<tr><td class='fund-cell'>เงินที่จ่ายเข้า กบข./กสจ./กองทุนสงเคราะห์ครูโรงเรียนเอกชน <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u> บาท กองทุนประกันสังคม <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u> บาท กองทุนสำรองเลี้ยงชีพ <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u> บาท</td></tr>",
        "<td style='white-space:nowrap;width:80px'><b>ผู้จ่ายเงิน</b></td>",
        "<td><span class='ck'>&#9744;</span> (4) อื่น ๆ (ระบุ) <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u></td>",
        "<td class='bottom-left'><div style='text-align:center;margin-bottom:2px'><b>คำเตือน</b></div>ผู้มีหน้าที่ออกหนังสือรับรองการหักภาษี ณ ที่จ่าย ฝ่าฝืนไม่ปฏิบัติตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร ต้องรับโทษทางอาญาตามมาตรา 35 แห่งประมวลรัษฎากร</td>",
        "<td class='bottom-right'><div style='margin-bottom:6px'>ขอรับรองว่าข้อความและตัวเลขดังกล่าวข้างต้นถูกต้องตรงกับความจริงทุกประการ</div>",
        " ผู้มีหน้าที่หักภาษี ณ ที่จ่าย</div><div class='sig-date'>",
        "<div style='text-align:center;font-size:11px;color:#444'>(วัน เดือน ปี ที่ออกหนังสือรับรองฯ)</div></div>",
        "<div class='stamp-area'>ประทับตรา<br>นิติบุคคล<br>(ถ้ามี)</div></td>",
        "<div class='footnote'><b>หมายเหตุ</b>&nbsp; เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)<span style='color:#900'>*</span> หมายถึง<div style='padding-left:24px'>1. กรณีบุคคลธรรมดาไทย ให้ใช้เลขประจำตัวประชาชนของกรมการปกครอง<br>2. กรณีนิติบุคคล ให้ใช้เลขทะเบียนนิติบุคคลของกรมพัฒนาธุรกิจการค้า<br>3. กรณีอื่น ๆ นอกเหนือจาก 1. และ 2. ให้ใช้เลขประจำตัวผู้เสียภาษีอากร (13 หลัก) ของกรมสรรพากร</div></div>",
    }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(GoldenFragments))]
    public void Html_StaticFormText_IsByteIdenticalToTheAcceptedRenderer(string fragment)
        => Assert.Contains(fragment, Html(Line("8")), StringComparison.Ordinal);

    [Fact]
    public void Html_PrintsEveryOwnerString_ThreeCopies()
    {
        var html = Html(Line("8"));
        Assert.Empty(WhtCertFormText.AllStaticStrings().Where(s => !html.Contains(s, StringComparison.Ordinal)));
        Assert.Equal(3, Regex.Matches(html, "<div class='wht-page'>").Count);   // ฉบับที่ 3 = สำเนาผู้หัก (รอบ 72) — ยังอยู่
    }

    // ───────── จุดแก้ (ทิศที่เปลี่ยน + ทิศที่ต้องไม่เปลี่ยน) ─────────

    [Fact]
    public void SequenceBox_IsBlank_NotTheCertificateNumber()
    {
        var html = Html(Line("8"));
        Assert.Contains("<div>ลำดับที่ <span class='sq'>&nbsp;</span> ในแบบ</div>", html);
        // เลขที่หนังสือรับรองยังอยู่ที่ "เลขที่" ฉบับละครั้ง (3 ฉบับ) — ไม่ซ้ำในช่องลำดับที่
        Assert.Equal(3, Regex.Matches(html, "WHT-202610-0002").Count);
    }

    [Fact]
    public void LegacyTaxIdBoxes_AreBlank_Grouped_1_4_4_1()
    {
        var html = Html(Line("8"));
        const string box = "<span class='tb'> </span>", sep = "<span class='ts'>&nbsp;</span>";
        var expected = "เลขประจำตัวผู้เสียภาษีอากร " + box + sep + string.Concat(Enumerable.Repeat(box, 4)) + sep
            + string.Concat(Enumerable.Repeat(box, 4)) + sep + box + "</td>";
        Assert.Contains(expected, html);
        Assert.DoesNotContain("<span class='tb'>0</span><span class='tb'>2</span><span class='tb'>0</span><span class='ts'>&nbsp;</span>", html);
        Assert.Equal(new[] { 1, 4, 4, 1 }, WhtCertFormText.TaxId10Groups);
    }

    [Fact]
    public void Row6_PrintsSpecification_ForLinesThatFallToOther()
    {
        var html = Html(Line("ins-x", "ค่าเบี้ยประกันวินาศภัย"));
        Assert.Contains("<td class='it-type'>6. อื่น ๆ (ระบุ) <u class='ul'>&nbsp;ค่าเบี้ยประกันวินาศภัย&nbsp;</u></td>", html);
    }

    [Fact]
    public void Row6_KeepsDottedLine_WhenNothingFallsThere_AndRow5TextStaysVerbatim()
    {
        // แถว 5 ไม่มีช่อง (ระบุ) บนแบบทางการ ⇒ ไม่เติมรายการ แม้บรรทัดจะมีคำอธิบาย
        var html = Html(Line("8", "ค่าเบี้ยประกันวินาศภัย"));
        Assert.Contains("<td class='it-type'>6. อื่น ๆ (ระบุ) ........................................................................................</td>", html);
        Assert.Contains($"<td class='it-type it-s5'>{WhtCertFormText.Row5}</td>", html);
        Assert.DoesNotContain("ค่าเบี้ยประกันวินาศภัย&nbsp;", html);
    }

    [Fact]
    public void Row6Spec_And_LineSpec()
    {
        Assert.Null(WhtCertFormText.Row6Spec(new[] { Line("8"), Line("5") }));
        Assert.Equal("ค่าเบี้ยประกันวินาศภัย, ค่าธรรมเนียมอื่น",
            WhtCertFormText.Row6Spec(new[] { Line("zz", "ค่าเบี้ยประกันวินาศภัย", 1), Line("yy", "ค่าธรรมเนียมอื่น", 2), Line("zz", "ค่าเบี้ยประกันวินาศภัย", 3) }));
        Assert.Equal("ค่าโฆษณา", WhtCertFormText.LineSpec(Line("8ad", "อะไรก็ได้")));   // รหัสที่รู้จัก = ชื่อจากตารางกลาง
        Assert.Equal("qq", WhtCertFormText.LineSpec(Line("qq")));                       // ไม่รู้จัก + ไม่มีคำอธิบาย = รหัส
    }

    [Fact]
    public void Html_EncodesRow6Specification()
    {
        var html = Html(Line("zz", "<script>x</script>"));
        Assert.DoesNotContain("<script>x", html);
        Assert.Contains("&lt;script&gt;x&lt;/script&gt;", html);
    }

    [Fact]
    public void OwnerText_QuestPdfDriftsFixed()
    {
        // เดิม QuestPDF: หมายเหตุข้อ 3 ขาด "นอกเหนือจาก 1. และ 2." · (2.2) ตัดท้าย · (2.3) "ก่อนรอบบัญชี" — ตอนนี้อ่านจากที่เดียวกับ HTML
        Assert.Contains("นอกเหนือจาก 1. และ 2.", WhtCertFormText.FootnoteItems[2]);
        Assert.EndsWith("เพื่อเสียภาษีเงินได้นิติบุคคล", WhtCertFormText.Div22b);
        Assert.Equal("ก่อนรอบระยะเวลาบัญชีปีปัจจุบัน", WhtCertFormText.Div23b);
    }

    // ───────── ไม่มีสำเนาข้อความแบบ ─────────

    [Fact]
    public void QuestPdfRenderer_HasNoThaiStringLiterals()
    {
        var code = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/Services/Implementations/PdfGenerationService.WhtCert.cs")));
        Assert.Empty(ThaiLiterals(code));
    }

    [Fact]
    public void HtmlRenderer_WhtMethod_HasNoThaiStringLiterals()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/Services/Implementations/PdfGenerationService.cs"));
        var a = src.IndexOf("internal static string BuildWithholdingTaxCertHtml(", StringComparison.Ordinal);
        var b = src.IndexOf("private static string ThaiMonthAbbr(", StringComparison.Ordinal);
        Assert.True(a > 0 && b > a);
        Assert.Empty(ThaiLiterals(StripComments(src[a..b])));
    }

    [Fact]
    public void WhtHtmlPage_PrintsTheSameFormText()
    {
        // หน้าพิมพ์ในเว็บ (renderer ตัวที่ผู้ใช้พิมพ์จริง) ยังประกอบฟอร์มใน JS — ข้อความต้องตรงกับตัวตั้งทุกคำ
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/wwwroot/pages/wht.html"));
        var a = page.IndexOf("async printCert(id)", StringComparison.Ordinal);
        var b = page.IndexOf("printWin.document.close();", a, StringComparison.Ordinal);
        var seg = page[a..b];
        var flat = Regex.Replace(seg, @"\s+", " ");
        var missing = WhtCertFormText.AllStaticStrings().Distinct()
            .Where(s => !seg.Contains(s, StringComparison.Ordinal) && !flat.Contains(s, StringComparison.Ordinal)).ToList();
        Assert.Empty(missing);
        Assert.DoesNotContain("<span class=\"sq\">$", seg);   // ช่องลำดับที่ไม่ใช่ค่าจากข้อมูล (เดิมพิมพ์เลขที่หนังสือรับรอง)
    }

    private static string StripComments(string src)
    {
        var code = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(code, @"//[^\n]*", "");
    }

    private static List<string> ThaiLiterals(string code) =>
        Regex.Matches(code, "\"(?:[^\"\\\\\\n]|\\\\.)*[\\u0E00-\\u0E7F](?:[^\"\\\\\\n]|\\\\.)*\"").Select(m => m.Value).ToList();

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Accounting.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("ไม่พบ Accounting.sln");
    }
}
