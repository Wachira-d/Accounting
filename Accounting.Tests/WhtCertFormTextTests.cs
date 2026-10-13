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
/// รอบ 204 (คำตัดสินเจ้าของข้อ 148 "ตามแบบทางการเป๊ะทุกอย่าง ยกเว้นฉบับที่ 3"): ชิ้นส่วนทองคำที่ขยับตามแบบทางการ — ป้ายลงชื่อ "ผู้จ่ายเงิน" ·
/// หัวคอลัมน์ "ประเภทเงินได้พึงประเมินจ่าย" · 4(ข) (1)/(2.2)/(2.3) ข้อความ+จุดตัดบรรทัด · "เงินที่จ่ายเข้า" ตัวหนา · คำเตือนหัวข้อซ้าย ·
/// หมายเหตุข้อ 1 อยู่บรรทัด "หมายถึง" (ส่วนอื่นยังเหมือนเดิมทุกไบต์) ·
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
        => Cert(TaxType.WithholdingTax53, WithholdingTaxCertType.Withhold, lines);

    private static WithholdingTaxCert Cert(TaxType form, WithholdingTaxCertType type, params WithholdingTaxCertLine[] lines)
    {
        var c = new WithholdingTaxCert
        {
            CertificateNumber = "WHT-202610-0002",
            TaxFormType = form,
            TaxYear = 2026, TaxMonth = 10,
            CertificateType = type,
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
        "<th style='width:46%'>ประเภทเงินได้พึงประเมินจ่าย</th><th style='width:14%'>วัน เดือน<br>หรือปีภาษี ที่จ่าย</th><th style='width:20%'>จำนวนเงินที่จ่าย</th><th style='width:20%'>ภาษีที่หัก<br>และนำส่งไว้</th>",
        "<tr><td class='it-type'>4. (ก) ดอกเบี้ย ฯลฯ ตามมาตรา 40 (4) (ก)</td>",
        "&nbsp;&nbsp;&nbsp;(ข) เงินปันผล เงินส่วนแบ่งกำไร ฯลฯ ตามมาตรา 40 (4) (ข)<div class='div-indent' style='margin-top:2px'>(1) กรณีผู้ได้รับเงินปันผลได้รับเครดิตภาษี โดยจ่ายจากกำไรสุทธิของ<br>&nbsp;&nbsp;&nbsp;&nbsp;กิจการที่ได้ต้องเสียภาษีเงินได้นิติบุคคลในอัตรา ดังนี้<div class='div-indent'>(1.1) อัตราร้อยละ 30 ของกำไรสุทธิ<br>(1.2) อัตราร้อยละ 25 ของกำไรสุทธิ<br>(1.3) อัตราร้อยละ 20 ของกำไรสุทธิ<br>(1.4) อัตราอื่น ๆ (ระบุ) .................. ของกำไรสุทธิ</div>(2) กรณีผู้ได้รับเงินปันผลไม่ได้รับเครดิตภาษี เนื่องจากจ่ายจาก<div class='div-indent'>(2.1) กำไรสุทธิของกิจการที่ได้รับยกเว้นภาษีเงินได้นิติบุคคล<br>(2.2) เงินปันผลหรือส่วนแบ่งของกำไรที่ได้รับยกเว้นไม่ต้องนำมารวม<br>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;คำนวณเป็นรายได้เพื่อเสียภาษีเงินได้นิติบุคคล<br>(2.3) กำไรสุทธิที่ได้หักผลขาดทุนสุทธิยกมาไม่เกิน 5 ปีก่อนรอบระยะ<br>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;เวลาบัญชีปีปัจจุบัน<br>(2.4) กำไรที่รับรู้ทางบัญชีโดยวิธีส่วนได้เสีย (equity method)<br>(2.5) อื่น ๆ (ระบุ) ........................................................</div></div></td>",
        "<td class='it-type it-s5'>5. การจ่ายเงินได้ที่ต้องหักภาษี ณ ที่จ่าย ตามคำสั่งกรมสรรพากรที่ออกตามมาตรา 3 เตรส เช่น รางวัล ส่วนลดหรือประโยชน์ใด ๆ เนื่องจากการส่งเสริมการขาย รางวัลในการประกวด การแข่งขัน การชิงโชค ค่าแสดงของนักแสดงสาธารณะ ค่าจ้างทำของ ค่าโฆษณา ค่าเช่า ค่าขนส่ง ค่าบริการ ค่าเบี้ยประกันวินาศภัย ฯลฯ</td>",
        "<tr class='total-row'><td colspan='2' style='text-align:right'>รวมเงินที่จ่ายและภาษีที่หักนำส่ง</td>",
        "<b>รวมเงินภาษีที่หักนำส่ง</b> <i>(ตัวอักษร)</i>",
        "<tr><td class='fund-cell'><b>เงินที่จ่ายเข้า</b> กบข./กสจ./กองทุนสงเคราะห์ครูโรงเรียนเอกชน <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u> บาท กองทุนประกันสังคม <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u> บาท กองทุนสำรองเลี้ยงชีพ <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u> บาท</td></tr>",
        "<td style='white-space:nowrap;width:80px'><b>ผู้จ่ายเงิน</b></td>",
        "<td><span class='ck'>&#9744;</span> (4) อื่น ๆ (ระบุ) <u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u></td>",
        "<td class='bottom-left'><table class='inner' cellspacing='0'><tr><td style='white-space:nowrap;vertical-align:top;padding:0 6px 0 0'><b>คำเตือน</b></td><td style='vertical-align:top;padding:0'>ผู้มีหน้าที่ออกหนังสือรับรองการหักภาษี ณ ที่จ่าย ฝ่าฝืนไม่ปฏิบัติตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร ต้องรับโทษทางอาญาตามมาตรา 35 แห่งประมวลรัษฎากร</td></tr></table></td>",
        "<td class='bottom-right'><div style='margin-bottom:6px'>ขอรับรองว่าข้อความและตัวเลขดังกล่าวข้างต้นถูกต้องตรงกับความจริงทุกประการ</div>",
        "<span class='sig-dots'></span> ผู้จ่ายเงิน</div><div class='sig-date'>",
        "<div style='text-align:center;font-size:11px;color:#444'>(วัน เดือน ปี ที่ออกหนังสือรับรองฯ)</div></div>",
        "<div class='stamp-area'>ประทับตรา<br>นิติบุคคล<br>(ถ้ามี)</div></td>",
        "<div class='footnote'><table class='inner' cellspacing='0' style='width:auto'><tr><td style='white-space:nowrap;vertical-align:top;padding:0 4px 0 0'><b>หมายเหตุ</b>&nbsp; เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)<span style='color:#900'>*</span> หมายถึง</td><td style='vertical-align:top;padding:0'>1. กรณีบุคคลธรรมดาไทย ให้ใช้เลขประจำตัวประชาชนของกรมการปกครอง<br>2. กรณีนิติบุคคล ให้ใช้เลขทะเบียนนิติบุคคลของกรมพัฒนาธุรกิจการค้า<br>3. กรณีอื่น ๆ นอกเหนือจาก 1. และ 2. ให้ใช้เลขประจำตัวผู้เสียภาษีอากร (13 หลัก) ของกรมสรรพากร</td></tr></table></div>",
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
        Assert.Equal("เวลาบัญชีปีปัจจุบัน", WhtCertFormText.Div23b);   // รอบ 204: ตัดบรรทัด "…ก่อนรอบระยะ / เวลาบัญชีปีปัจจุบัน" ตามแบบทางการ
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

    // ───────── รอบ 204 · คำตัดสินเจ้าของข้อ 148: แบบทางการเป๊ะ (ยกเว้นฉบับที่ 3) ─────────

    [Fact]
    public void SignatureLabel_IsPayer_AsOnTheOfficialForm_InAllThreeRenderers()
    {
        Assert.Equal("ผู้จ่ายเงิน", WhtCertFormText.SignRole);
        var html = Html(Line("8"));
        Assert.Equal(3, Regex.Matches(html, "<span class='sig-dots'></span> ผู้จ่ายเงิน</div>").Count);
        Assert.DoesNotContain("ผู้มีหน้าที่หักภาษี ณ ที่จ่าย</div>", html);   // ป้ายรอบ 72 ที่ถูกยกเลิก
        // QuestPDF — ป้ายท้ายช่องลงชื่อมาจากตัวตั้งเดียว (ทั้งกรณีมี/ไม่มีรูปลายเซ็น)
        var qp = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/Services/Implementations/PdfGenerationService.WhtCert.cs")));
        Assert.Equal(2, Regex.Matches(qp, Regex.Escape("t.Span(\" \" + WhtCertFormText.SignRole);")).Count);
        // หน้าพิมพ์ wht.html (renderer ที่ผู้ใช้พิมพ์จริง) — ป้ายหลังเส้นลงชื่อ ไม่ใช่ป้ายรอบ 72
        var seg = PrintSegment();
        Assert.Matches(new Regex(@"</span>\s*ผู้จ่ายเงิน\s*</div>"), seg);
        Assert.DoesNotMatch(new Regex(@"</span>\s*ผู้มีหน้าที่หักภาษี ณ ที่จ่าย\s*</div>"), seg);
    }

    [Theory]
    [InlineData(TaxType.WithholdingTax1, 0)]    // (1) ภ.ง.ด.1ก (ค่าเดิม)
    [InlineData(TaxType.WithholdingTax2, 2)]    // (3) ภ.ง.ด.2 — รอบ 204
    [InlineData(TaxType.WithholdingTax3, 3)]    // (4) ภ.ง.ด.3
    [InlineData(TaxType.WithholdingTax53, 6)]   // (7) ภ.ง.ด.53
    public void FormBoxIndex_MapsFilingFormToTheOfficialBox(TaxType form, int box)
    {
        Assert.Equal(box, WhtCertFormText.FormBoxIndex(form));
        Assert.StartsWith($"({box + 1}) ", WhtCertFormText.FormBoxes[box]);
    }

    [Theory]
    [InlineData(TaxType.WithholdingTax54)]   // ทิศตรงข้าม: แบบที่ไม่มีช่องบน 50 ทวิ ⇒ ไม่ติ๊กช่องใด (ห้ามเดาไปช่องอื่น)
    [InlineData(TaxType.VatPp36)]
    public void FormBoxIndex_IsNull_ForFormsWithoutABox(TaxType form)
        => Assert.Null(WhtCertFormText.FormBoxIndex(form));

    [Theory]
    [InlineData(WithholdingTaxCertType.Withhold, 0)]    // (1) หัก ณ ที่จ่าย
    [InlineData(WithholdingTaxCertType.PayAlways, 1)]   // (2) ออกให้ตลอดไป
    [InlineData(WithholdingTaxCertType.PayOnce, 2)]     // (3) ออกให้ครั้งเดียว — รอบ 204
    public void PayerConditionIndex_MapsCertificateType(WithholdingTaxCertType type, int idx)
    {
        Assert.Equal(idx, WhtCertFormText.PayerConditionIndex(type));
        Assert.StartsWith($"({idx + 1}) ", WhtCertFormText.PayerConditions[idx]);
    }

    [Fact]
    public void Html_Pnd2PayOnce_TicksBox3AndCondition3_Only()
    {
        var html = PdfGenerationService.BuildWithholdingTaxCertHtml(
            Cert(TaxType.WithholdingTax2, WithholdingTaxCertType.PayOnce, Line("4a")), Payer());
        Assert.Equal(3, Regex.Matches(html, Regex.Escape("<span class='ck'>&#9745;</span> <b>(3) ภ.ง.ด.2</b>")).Count);
        Assert.Equal(3, Regex.Matches(html, Regex.Escape("<span class='ck'>&#9745;</span> (3) ออกให้ครั้งเดียว")).Count);
        Assert.Equal(6, Regex.Matches(html, "&#9745;").Count);   // 2 ช่องต่อฉบับ × 3 ฉบับ — ไม่มีช่องอื่นติดมา
        Assert.Contains("<span class='ck'>&#9744;</span> <b>(7) ภ.ง.ด.53</b>", html);
        Assert.Contains("<span class='ck'>&#9744;</span> (1) หัก ณ ที่จ่าย", html);
    }

    [Fact]
    public void Html_Pnd53Withhold_StillTicksBox7AndCondition1()
    {
        // ใบที่ถูกอยู่แล้วต้องไม่ถูกแตะ (F2 ข้อ 8)
        var html = Html(Line("8"));
        Assert.Equal(3, Regex.Matches(html, Regex.Escape("<span class='ck'>&#9745;</span> <b>(7) ภ.ง.ด.53</b>")).Count);
        Assert.Equal(3, Regex.Matches(html, Regex.Escape("<span class='ck'>&#9745;</span> (1) หัก ณ ที่จ่าย")).Count);
        Assert.Equal(6, Regex.Matches(html, "&#9745;").Count);
    }

    [Fact]
    public void Html_FormWithoutABox_TicksNoFormBox()
    {
        var html = PdfGenerationService.BuildWithholdingTaxCertHtml(
            Cert(TaxType.WithholdingTax54, WithholdingTaxCertType.Withhold, Line("8")), Payer());
        Assert.Equal(3, Regex.Matches(html, "&#9745;").Count);   // เหลือแค่ (1) หัก ณ ที่จ่าย ฉบับละช่อง
        Assert.DoesNotContain("&#9745;</span> <b>", html);
    }

    [Fact]
    public void Html_Structure_FollowsTheOfficialForm()
    {
        var html = Html(Line("8"));
        // ช่องเลขเดิม 10 หลักอยู่บรรทัดเดียวกับ "ชื่อ" (ผู้หัก + ผู้ถูกหัก × 3 ฉบับ)
        Assert.Equal(6, Regex.Matches(html,
            "<td class='f-lbl'>ชื่อ</td><td><u class='ul'>[^<]*</u></td><td style='white-space:nowrap;text-align:right;font-size:12px;padding-left:8px'>เลขประจำตัวผู้เสียภาษีอากร <span class='tb'>").Count);
        Assert.DoesNotContain("<td colspan='2' style='text-align:right;font-size:12px;padding-top:2px'>", html);
        // "ลำดับที่ … ในแบบ" อยู่ในกรอบผู้ถูกหัก (ไม่มีแถวกรอบแยก)
        Assert.DoesNotContain("<tr><td class='ftype-cell'>", html);
        var payee = html.IndexOf(WhtCertFormText.PayeeHeading, StringComparison.Ordinal);
        var seq = html.IndexOf(WhtCertFormText.SeqLabel + " <span class='sq'>", payee, StringComparison.Ordinal);
        var incomeTable = html.IndexOf("<table class='IT'", payee, StringComparison.Ordinal);
        Assert.True(payee > 0 && seq > payee && incomeTable > seq);
        Assert.Contains("<div class='ftype-cell' style='padding:4px 0 0'>", html);
    }

    [Fact]
    public void OwnerText_FollowsTheOfficialWording()
    {
        Assert.Equal("ประเภทเงินได้พึงประเมินจ่าย", WhtCertFormText.ColIncomeType);
        Assert.DoesNotContain("เงินส่วนแบ่งของกำไร", WhtCertFormText.Div22a);      // (2.2) "เงินปันผลหรือส่วนแบ่งของกำไร…"
        Assert.DoesNotContain("สุทธิส่วนที่", WhtCertFormText.Div23a);              // (2.3) "กำไรสุทธิที่ได้หัก…"
        Assert.EndsWith("โดยจ่ายจากกำไรสุทธิของ", WhtCertFormText.Div1a);
        Assert.Equal("เงินที่จ่ายเข้า", WhtCertFormText.FundLead);
        Assert.DoesNotContain(WhtCertFormText.FundLead, WhtCertFormText.FundGov);  // ไม่พิมพ์ซ้ำ
    }

    [Fact]
    public void WhtHtmlPage_TicksFromServerIndexes_NotFromEnumNames()
    {
        // server computes · page displays — หน้าไม่จับคู่ชื่อ enum เอง (เดิม ภ.ง.ด.2 ไม่มีวันติ๊กเพราะหน้าเทียบแค่ 1/3/53)
        var seg = PrintSegment();
        Assert.Contains("d.formBoxIndex === i", seg);
        Assert.Contains("d.payerConditionIndex === i", seg);
        Assert.DoesNotContain("d.taxFormType ===", seg);
        Assert.DoesNotContain("d.certificateType ===", seg);
        for (var i = 0; i < 7; i++) Assert.Contains($"${{box({i})}}</span> <b>{WhtCertFormText.FormBoxes[i]}</b>", seg);
        for (var i = 0; i < 4; i++) Assert.Contains($"${{cond({i})}}</span> {WhtCertFormText.PayerConditions[i]}", seg);
        // DTO ส่งดัชนีให้หน้า — MapToResponse (expression-bodied: checker จุดเรียกอ่านไม่ได้) เติมจากตัวตัดสินเดียว
        var t = typeof(WithholdingTaxCertResponse);
        Assert.Equal(typeof(int?), t.GetProperty("FormBoxIndex")?.PropertyType);
        Assert.Equal(typeof(int?), t.GetProperty("PayerConditionIndex")?.PropertyType);
        var svc = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/Services/Implementations/WithholdingTaxCertService.cs"));
        Assert.Contains("FormBoxIndex: Accounting.Helpers.WhtCertFormText.FormBoxIndex(w.TaxFormType)", svc);
        Assert.Contains("PayerConditionIndex: Accounting.Helpers.WhtCertFormText.PayerConditionIndex(w.CertificateType)", svc);
    }

    // ───────── A9 (2026-10-13): "เงินที่จ่ายเข้า" กองทุนบนใบรายปีของพนักงาน (ภ.ง.ด.1ก) ─────────

    private const string FundBlankHtml = "<u class='ul'>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;</u>";
    private const string FundCellBlankHtml =
        "<tr><td class='fund-cell'><b>เงินที่จ่ายเข้า</b> กบข./กสจ./กองทุนสงเคราะห์ครูโรงเรียนเอกชน " + FundBlankHtml
        + " บาท กองทุนประกันสังคม " + FundBlankHtml + " บาท กองทุนสำรองเลี้ยงชีพ " + FundBlankHtml + " บาท</td></tr>";

    [Fact]
    public void EmployeeAnnualFunds_SsoAndPvd_FromTotals_GovAlwaysBlank()
    {
        var f = WhtCertFormText.EmployeeAnnualFunds(9000m, 12000.50m);
        Assert.Null(f.Gov);                       // ระบบไม่มีข้อมูล กบข. — ไม่รู้ = ว่าง (ห้ามแต่ง 0.00)
        Assert.Equal(9000m, f.Sso);
        Assert.Equal(12000.50m, f.Pvd);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    public void EmployeeAnnualFunds_NoContribution_IsBlank_NotZero(int sso, int pvd)   // int — InlineData ส่ง decimal ตรงไม่ได้
    {
        // ทิศตรงข้าม: พนักงานที่ไม่ได้สมทบ เห็นช่องว่าง (เหมือนแบบกรอกมือ) ไม่ใช่ "0.00"
        var f = WhtCertFormText.EmployeeAnnualFunds(sso, pvd);
        Assert.Equal(new WhtCertFundAmounts(null, null, null), f);
    }

    [Theory]
    [InlineData("9000", "9,000.00")]
    [InlineData("1.005", "1.01")]        // AwayFromZero (ไม่ใช่ banker's → 1.00)
    [InlineData("1234567.8", "1,234,567.80")]
    public void FundAmountText_FormatsTwoDecimals_AwayFromZero(string amount, string expected)
        => Assert.Equal(expected, WhtCertFormText.FundAmountText(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void FundAmountText_Null_IsNull_SoRendererPrintsItsBlank()
        => Assert.Null(WhtCertFormText.FundAmountText(null));

    [Fact]
    public void Html_EmployeeAnnualCert_PrintsSsoAndPvdAmounts_GovBlank()
    {
        var cert = Cert(TaxType.WithholdingTax1, WithholdingTaxCertType.Withhold, Line("1", "เงินเดือน เดือน 01/2026"));
        var html = PdfGenerationService.BuildWithholdingTaxCertHtml(cert, Payer(),
            funds: WhtCertFormText.EmployeeAnnualFunds(9000m, 1500m));
        var expected = "<tr><td class='fund-cell'><b>เงินที่จ่ายเข้า</b> กบข./กสจ./กองทุนสงเคราะห์ครูโรงเรียนเอกชน " + FundBlankHtml
            + " บาท กองทุนประกันสังคม <u class='ul'>&nbsp;9,000.00&nbsp;</u> บาท กองทุนสำรองเลี้ยงชีพ <u class='ul'>&nbsp;1,500.00&nbsp;</u> บาท</td></tr>";
        Assert.Equal(3, Regex.Matches(html, Regex.Escape(expected)).Count);   // ทุกฉบับ (1–3) ตรงกัน
        Assert.DoesNotContain(FundCellBlankHtml, html);
    }

    [Fact]
    public void Html_SupplierCert_FundCellStaysBlank_ByteIdentical()
    {
        // ทิศตรงข้าม: ใบผู้รับเงินทั่วไป (ไม่ส่ง funds) และ funds ว่างทุกช่อง ⇒ เส้นว่างเดิมทุกไบต์ — ผลลัพธ์ทั้งหน้าเท่ากัน
        var noFunds = Html(Line("8"));
        Assert.Equal(3, Regex.Matches(noFunds, Regex.Escape(FundCellBlankHtml)).Count);
        var allNull = PdfGenerationService.BuildWithholdingTaxCertHtml(Cert(Line("8")), Payer(),
            funds: new WhtCertFundAmounts(null, null, null));
        Assert.Equal(noFunds, allNull);
    }

    [Fact]
    public void Renderers_FundLine_ShareTheSameFormatter()
    {
        // สอง renderer ฝั่ง C# จัดรูปแบบยอดกองทุนผ่านตัวเดียว (QuestPDF ไม่มีรูปแบบตัวเลขของตัวเอง) · หน้าพิมพ์ wht.html ยังว่าง (ใบผู้รับเงินทั่วไป)
        var qp = StripComments(File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/Services/Implementations/PdfGenerationService.WhtCert.cs")));
        Assert.Contains("BuildFundLine(form, funds);", qp);
        Assert.Contains("WhtCertFormText.FundAmountText(amount)", qp);
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/Services/Implementations/PdfGenerationService.cs"));
        Assert.Contains("return BuildWhtCertPdf(inMemoryCert, company, sigBytes, sigName, funds);", src);
        Assert.Contains("WhtCertFormText.FundAmountText(amount)", src);
    }

    private static string PrintSegment()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "Accounting/wwwroot/pages/wht.html"));
        var a = page.IndexOf("async printCert(id)", StringComparison.Ordinal);
        var b = page.IndexOf("printWin.document.close();", a, StringComparison.Ordinal);
        return page[a..b];
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
