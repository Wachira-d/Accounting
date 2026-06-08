using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using System.Text.RegularExpressions;

namespace Accounting.Services.Implementations;

/// <summary>
/// Dedicated QuestPDF renderer for the หนังสือรับรองการหักภาษี ณ ที่จ่าย
/// (50 ทวิ) — replaces the HTML→block→QuestPDF flatten path that turned the
/// official form layout into a wall of text.
///
/// The official RD form has:
///   • title strip + เล่มที่/เลขที่ on the right
///   • payer block: 13-digit TIN boxes + name + address
///   • payee block: same shape
///   • form-type checkbox grid (7 options across ภ.ง.ด.1ก … 53)
///   • income table (6 numbered rows + total) with three numeric columns
///   • Thai-text total
///   • fund-contribution line
///   • condition checkboxes (4 options)
///   • warning + signature block + stamp area
///   • footnote
///
/// QuestPDF's Row/Column/Table/Border primitives recreate this faithfully.
/// Two copies per cert: "ฉบับที่ 1" (filed with return) + "ฉบับที่ 2" (kept
/// as evidence), each on its own A4 page.
/// </summary>
public partial class PdfGenerationService
{
    private byte[] BuildWhtCertPdf(WithholdingTaxCert cert, Company company)
    {
        EnsureThaiFontsRegistered();
        var fontChain = GetFontFamilyChain(null);
        var lines = cert.Lines.OrderBy(l => l.LineOrder).ToList();

        var fullAddress = string.Join(" ", new[] {
            company.Address, company.SubDistrict, company.District,
            company.Province, company.PostalCode
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var payeeAddr = string.Join(" ", new[] {
            cert.PayeeContact.Address, cert.PayeeContact.SubDistrict,
            cert.PayeeContact.District, cert.PayeeContact.Province,
            cert.PayeeContact.PostalCode
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var pdf = QuestPDF.Fluent.Document.Create(container =>
        {
            for (int copy = 1; copy <= 2; copy++)
            {
                var copyNum = copy;
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(10, Unit.Millimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(t => t.FontFamily(fontChain).FontSize(9.5f).LineHeight(1.15f));
                    page.Content().Column(col =>
                    {
                        BuildCopyHeader(col, copyNum);
                        col.Item().Border(1.5f).BorderColor(Colors.Black).Column(form =>
                        {
                            BuildTitleBar(form, cert.CertificateNumber);
                            BuildPartyBlock(form, "ผู้มีหน้าที่หักภาษี ณ ที่จ่าย", company.TaxId, company.Name, fullAddress);
                            BuildPartyBlock(form, "ผู้ถูกหักภาษี ณ ที่จ่าย", cert.PayeeContact.TaxId, cert.PayeeContact.Name, payeeAddr);
                            BuildFormTypeRow(form, cert);
                            BuildIncomeTable(form, lines, cert.TotalIncomeAmount, cert.TotalTaxAmount);
                            BuildTotalInWords(form, cert.TotalTaxAmount);
                            BuildFundLine(form);
                            BuildConditionsRow(form, cert);
                            BuildBottomSplit(form, cert.IssuedDate);
                        });
                        BuildFootnote(col);
                    });
                });
            }
        });
        return pdf.GeneratePdf();
    }

    // ── Copy header (ฉบับที่ 1 / 2) ──────────────────────────────────
    private static void BuildCopyHeader(QuestPDF.Fluent.ColumnDescriptor col, int copyNum)
    {
        col.Item().Text(t =>
        {
            t.Span("ฉบับที่ 1 ").FontSize(10).Bold().FontColor(copyNum == 1 ? Colors.Black : Colors.Grey.Medium);
            t.Span("(สำหรับผู้ถูกหักภาษี ณ ที่จ่าย ใช้แนบพร้อมกับแบบแสดงรายการภาษี)")
                .FontSize(8).Italic().FontColor(copyNum == 1 ? Colors.Black : Colors.Grey.Medium);
        });
        col.Item().PaddingBottom(2).Text(t =>
        {
            t.Span("ฉบับที่ 2 ").FontSize(10).Bold().FontColor(copyNum == 2 ? Colors.Black : Colors.Grey.Medium);
            t.Span("(สำหรับผู้ถูกหักภาษี ณ ที่จ่าย เก็บไว้เป็นหลักฐาน)")
                .FontSize(8).Italic().FontColor(copyNum == 2 ? Colors.Black : Colors.Grey.Medium);
        });
    }

    // ── Title bar with เล่มที่/เลขที่ on right ──────────────────────
    private static void BuildTitleBar(QuestPDF.Fluent.ColumnDescriptor col, string certNum)
    {
        col.Item().BorderBottom(1).BorderColor(Colors.Black).Padding(5).Row(r =>
        {
            r.ConstantItem(110);   // spacer
            r.RelativeItem().AlignCenter().Column(c =>
            {
                c.Item().AlignCenter().Text("หนังสือรับรองการหักภาษี ณ ที่จ่าย").FontSize(15).Bold().LetterSpacing(0.05f);
                c.Item().AlignCenter().Text("ตามมาตรา 50 ทวิ แห่งประมวลรัษฎากร").FontSize(10);
            });
            r.ConstantItem(110).Column(c =>
            {
                c.Item().Text(t => { t.Span("เล่มที่ "); t.Span("__________"); });
                c.Item().Text(t => { t.Span("เลขที่ "); t.Span(certNum ?? "").Bold(); });
            });
        });
    }

    // ── Party block (payer or payee) — TIN boxes + name + address ────
    private static void BuildPartyBlock(QuestPDF.Fluent.ColumnDescriptor col, string heading,
        string? taxId, string name, string fullAddress)
    {
        col.Item().BorderBottom(1).BorderColor(Colors.Black).Padding(5).Column(c =>
        {
            // Row 1: heading on left, 13-digit TIN boxes on right
            c.Item().Row(r =>
            {
                r.ConstantItem(170).Text(t => t.Span(heading + " :-").Bold().FontSize(10));
                r.RelativeItem().AlignRight().Row(rr =>
                {
                    rr.AutoItem().AlignBottom().PaddingRight(4).Text(t =>
                    {
                        t.Span("เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)").FontSize(9);
                        t.Span("*").FontColor(Colors.Red.Darken2).FontSize(9);
                    });
                    rr.AutoItem().Element(e => DrawTinBoxes(e, taxId));
                });
            });
            // Row 2: name field with dotted underline
            c.Item().PaddingTop(2).Row(r =>
            {
                r.ConstantItem(40).Text("ชื่อ").Bold();
                r.RelativeItem().Border(0).BorderBottom(0.5f).BorderColor(Colors.Black)
                    .Text(" " + (name ?? "") + " ").FontSize(10);
            });
            c.Item().PaddingLeft(40).Text("(ให้ระบุว่าเป็น บุคคล นิติบุคคล บริษัท สมาคม หรือคณะบุคคล)")
                .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);
            // Row 3: address
            c.Item().PaddingTop(2).Row(r =>
            {
                r.ConstantItem(40).Text("ที่อยู่").Bold();
                r.RelativeItem().BorderBottom(0.5f).BorderColor(Colors.Black)
                    .Text(" " + (fullAddress ?? "") + " ").FontSize(10);
            });
            c.Item().PaddingLeft(40).Text("(ให้ระบุ ชื่ออาคาร/หมู่บ้าน ห้องเลขที่ ชั้นที่ เลขที่ ตรอก/ซอย หมู่ที่ ถนน ตำบล/แขวง อำเภอ/เขต จังหวัด)")
                .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);
        });
    }

    // ── 13 boxes for the Thai TIN, grouped 1-4-5-2-1 with hyphens ──
    private static void DrawTinBoxes(QuestPDF.Infrastructure.IContainer e, string? taxId)
    {
        var digits = Regex.Replace(taxId ?? "", @"\D", "").PadRight(13);
        e.Row(r =>
        {
            int[][] groups = { new[]{0,1}, new[]{1,5}, new[]{5,10}, new[]{10,12}, new[]{12,13} };
            for (int g = 0; g < groups.Length; g++)
            {
                if (g > 0) r.AutoItem().AlignMiddle().Text("-").FontSize(10).Bold();
                for (int i = groups[g][0]; i < groups[g][1]; i++)
                {
                    var ch = i < digits.Length ? digits[i].ToString().Trim() : "";
                    r.AutoItem().Border(1).BorderColor(Colors.Black).Width(15).Height(17)
                        .AlignCenter().AlignMiddle().Text(ch).FontSize(10).Bold();
                }
            }
        });
    }

    // ── Form-type checkbox grid (ภ.ง.ด.1ก / 1ก พิเศษ / 2 / 3 / 2ก / 3ก / 53)
    private static void BuildFormTypeRow(QuestPDF.Fluent.ColumnDescriptor col, WithholdingTaxCert cert)
    {
        bool Pnd1k  = cert.TaxFormType == TaxType.WithholdingTax1;
        bool Pnd3   = cert.TaxFormType == TaxType.WithholdingTax3;
        bool Pnd53  = cert.TaxFormType == TaxType.WithholdingTax53;
        col.Item().BorderBottom(1).BorderColor(Colors.Black).Padding(5).Row(r =>
        {
            r.ConstantItem(200).Column(c =>
            {
                c.Item().Row(rr =>
                {
                    rr.AutoItem().PaddingRight(4).AlignMiddle().Text("ลำดับที่").FontSize(10);
                    rr.AutoItem().Border(1).BorderColor(Colors.Black).MinWidth(70).Height(20)
                        .AlignCenter().AlignMiddle().Text(cert.CertificateNumber ?? "").FontSize(9);
                    rr.AutoItem().PaddingLeft(4).AlignMiddle().Text("ในแบบ").FontSize(10);
                });
                c.Item().PaddingTop(2).Text("(ให้สามารถอ้างอิงหรือสอบยันกันได้ระหว่างลำดับที่ตามหนังสือรับรองฯ กับแบบยื่นรายการภาษีหักที่จ่าย)")
                    .FontSize(7).Italic().FontColor(Colors.Grey.Darken1);
            });
            r.RelativeItem().Column(c =>
            {
                c.Item().Row(rr =>
                {
                    DrawCheckOption(rr, Pnd1k, "(1) ภ.ง.ด.1ก");
                    DrawCheckOption(rr, false, "(2) ภ.ง.ด.1ก พิเศษ");
                    DrawCheckOption(rr, false, "(3) ภ.ง.ด.2");
                    DrawCheckOption(rr, Pnd3,  "(4) ภ.ง.ด.3");
                });
                c.Item().PaddingTop(2).Row(rr =>
                {
                    DrawCheckOption(rr, false, "(5) ภ.ง.ด.2ก");
                    DrawCheckOption(rr, false, "(6) ภ.ง.ด.3ก");
                    DrawCheckOption(rr, Pnd53, "(7) ภ.ง.ด.53");
                    rr.RelativeItem();
                });
            });
        });
    }

    private static void DrawCheckOption(QuestPDF.Fluent.RowDescriptor r, bool on, string label)
    {
        r.RelativeItem().Row(rr =>
        {
            rr.AutoItem().PaddingRight(4).AlignMiddle().Text(on ? "☑" : "☐").FontSize(11);
            rr.RelativeItem().AlignMiddle().Text(label).FontSize(9).Bold();
        });
    }

    // ── Income table (6 numbered rows + total) ───────────────────────
    private static void BuildIncomeTable(QuestPDF.Fluent.ColumnDescriptor col,
        List<WithholdingTaxCertLine> lines, decimal totalIncome, decimal totalTax)
    {
        (decimal Inc, decimal Tax, DateTime? Date) Match(params string[] codes)
        {
            var hits = lines.Where(l => codes.Contains(l.IncomeTypeCode)).ToList();
            if (hits.Count == 0) return (0, 0, null);
            return (hits.Sum(l => l.IncomeAmount), hits.Sum(l => l.TaxAmount), hits[0].PaymentDate);
        }
        string FmtDate(DateTime? d) => d.HasValue
            ? $"{d.Value.Day} {ThaiMonthAbbr(d.Value.Month)} {d.Value.Year + 543}" : "";
        string FmtAmt(decimal v) => v == 0 ? "" : v.ToString("N2");

        col.Item().Table(tbl =>
        {
            tbl.ColumnsDefinition(c =>
            {
                c.RelativeColumn(46);   // type description
                c.RelativeColumn(14);   // payment date
                c.RelativeColumn(20);   // income amount
                c.RelativeColumn(20);   // tax withheld
            });
            // Header
            tbl.Header(h =>
            {
                h.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignCenter()
                    .Text("ประเภทเงินได้พึงประเมินที่จ่าย").FontSize(9).Bold();
                h.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignCenter()
                    .Text("วัน เดือน\nหรือปีภาษี ที่จ่าย").FontSize(9).Bold();
                h.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignCenter()
                    .Text("จำนวนเงินที่จ่าย").FontSize(9).Bold();
                h.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignCenter()
                    .Text("ภาษีที่หัก\nและนำส่งไว้").FontSize(9).Bold();
            });
            void Row(string label, (decimal Inc, decimal Tax, DateTime? Date) m, float labelSize = 9f)
            {
                tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3)
                    .Text(label).FontSize(labelSize);
                tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignCenter()
                    .Text(FmtDate(m.Date)).FontSize(9);
                tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignRight()
                    .Text(FmtAmt(m.Inc)).FontSize(9);
                tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignRight()
                    .Text(FmtAmt(m.Tax)).FontSize(9);
            }
            Row("1. เงินเดือน ค่าจ้าง เบี้ยเลี้ยง โบนัส ฯลฯ ตามมาตรา 40 (1)", Match("1", "40(1)"));
            Row("2. ค่าธรรมเนียม ค่านายหน้า ฯลฯ ตามมาตรา 40 (2)", Match("2", "40(2)"));
            Row("3. ค่าแห่งลิขสิทธิ์ ฯลฯ ตามมาตรา 40 (3)", Match("3", "40(3)"));
            Row("4. (ก) ดอกเบี้ย ฯลฯ ตามมาตรา 40 (4) (ก)", Match("4a", "40(4)(a)"));
            Row("    (ข) เงินปันผล เงินส่วนแบ่งกำไร ฯลฯ ตามมาตรา 40 (4) (ข)", Match("4b", "40(4)(b)"));
            Row("5. การจ่ายเงินได้ที่ต้องหักภาษี ณ ที่จ่าย ตามคำสั่งกรมสรรพากร ตามมาตรา 3 เตรส " +
                "เช่น รางวัล ส่วนลด ค่าโฆษณา ค่าเช่า ค่าขนส่ง ค่าบริการ ค่าเบี้ยประกันวินาศภัย ฯลฯ",
                Match("5", "6", "7", "8", "40(5)", "40(6)", "40(7)", "40(8)"), 8f);
            Row("6. อื่น ๆ (ระบุ) ........................................................",
                Match("9", "99", "other"));
            // Totals row
            tbl.Cell().ColumnSpan(2).Border(1).BorderColor(Colors.Black).Padding(3).AlignRight()
                .Text("รวมเงินที่จ่ายและภาษีที่หักนำส่ง").FontSize(9.5f).Bold();
            tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignRight()
                .Text(FmtAmt(totalIncome)).FontSize(9.5f).Bold();
            tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignRight()
                .Text(FmtAmt(totalTax)).FontSize(9.5f).Bold();
        });
    }

    private static void BuildTotalInWords(QuestPDF.Fluent.ColumnDescriptor col, decimal totalTax)
    {
        col.Item().BorderBottom(1).BorderColor(Colors.Black).Background(Colors.Grey.Lighten3)
            .Padding(5).Text(t =>
            {
                t.Span("รวมเงินภาษีที่หักนำส่ง ").Bold();
                t.Span("(ตัวอักษร) ").Italic();
                t.Span(" " + ThaiNumberToText(totalTax) + " ").Bold();
            });
    }

    private static void BuildFundLine(QuestPDF.Fluent.ColumnDescriptor col)
    {
        col.Item().BorderBottom(1).BorderColor(Colors.Black).Padding(5)
            .Text("เงินที่จ่ายเข้า กบข./กสจ./กองทุนสงเคราะห์ครูโรงเรียนเอกชน ________ บาท  " +
                  "กองทุนประกันสังคม ________ บาท  กองทุนสำรองเลี้ยงชีพ ________ บาท")
            .FontSize(9);
    }

    private static void BuildConditionsRow(QuestPDF.Fluent.ColumnDescriptor col, WithholdingTaxCert cert)
    {
        bool isWithhold  = cert.CertificateType == Models.DTOs.Tax.WithholdingTaxCertType.Withhold;
        bool isPayAlways = cert.CertificateType == Models.DTOs.Tax.WithholdingTaxCertType.PayAlways;
        col.Item().BorderBottom(1).BorderColor(Colors.Black).Padding(5).Row(r =>
        {
            r.ConstantItem(75).AlignMiddle().Text("ผู้จ่ายเงิน").Bold().FontSize(10);
            DrawCheckOption(r, isWithhold,  "(1) หัก ณ ที่จ่าย");
            DrawCheckOption(r, isPayAlways, "(2) ออกให้ตลอดไป");
            DrawCheckOption(r, false,       "(3) ออกให้ครั้งเดียว");
            DrawCheckOption(r, false,       "(4) อื่น ๆ (ระบุ) ________");
        });
    }

    private static void BuildBottomSplit(QuestPDF.Fluent.ColumnDescriptor col, DateTime? issuedDate)
    {
        var dd = issuedDate?.Day.ToString() ?? "____";
        var mm = issuedDate?.Month.ToString() ?? "____";
        var yy = issuedDate.HasValue ? (issuedDate.Value.Year + 543).ToString() : "______";
        col.Item().Row(r =>
        {
            r.RelativeItem(35).BorderRight(1).BorderColor(Colors.Black).Padding(6).Column(c =>
            {
                c.Item().AlignCenter().Text("คำเตือน").Bold().FontSize(10);
                c.Item().PaddingTop(2).Text(
                    "ผู้มีหน้าที่ออกหนังสือรับรองการหักภาษี ณ ที่จ่าย ฝ่าฝืนไม่ปฏิบัติตามมาตรา 50 ทวิ " +
                    "แห่งประมวลรัษฎากร ต้องรับโทษทางอาญาตามมาตรา 35 แห่งประมวลรัษฎากร").FontSize(8);
            });
            r.RelativeItem(65).Padding(6).Column(c =>
            {
                c.Item().Text("ขอรับรองว่าข้อความและตัวเลขดังกล่าวข้างต้นถูกต้องตรงกับความจริงทุกประการ")
                    .FontSize(9);
                c.Item().PaddingTop(20).AlignRight().Text(t =>
                {
                    t.Span("ลงชื่อ ");
                    t.Span("__________________________");
                    t.Span(" ผู้จ่ายเงิน");
                });
                c.Item().PaddingTop(4).AlignCenter().Text(t =>
                {
                    t.Span(dd).Bold(); t.Span(" / ");
                    t.Span(mm).Bold(); t.Span(" / ");
                    t.Span(yy).Bold();
                });
                c.Item().AlignCenter().Text("(วัน เดือน ปี ที่ออกหนังสือรับรองฯ)").FontSize(7).FontColor(Colors.Grey.Darken1);
                c.Item().PaddingTop(4).AlignRight().Element(e =>
                    e.Border(1).BorderColor(Colors.Grey.Darken1).Width(70).Height(70)
                     .AlignCenter().AlignMiddle().Text("ประทับตรา\nนิติบุคคล\n(ถ้ามี)")
                     .FontSize(8).FontColor(Colors.Grey.Darken1).Italic());
            });
        });
    }

    private static void BuildFootnote(QuestPDF.Fluent.ColumnDescriptor col)
    {
        col.Item().PaddingTop(3).Text(t =>
        {
            t.Span("หมายเหตุ ").Bold().FontSize(9);
            t.Span("เลขประจำตัวผู้เสียภาษีอากร (13 หลัก) ").FontSize(8.5f);
            t.Span("*").FontColor(Colors.Red.Darken2).FontSize(8.5f);
            t.Span(" หมายถึง").FontSize(8.5f);
        });
        col.Item().PaddingLeft(20).Text(
            "1. กรณีบุคคลธรรมดาไทย ให้ใช้เลขประจำตัวประชาชนของกรมการปกครอง\n" +
            "2. กรณีนิติบุคคล ให้ใช้เลขทะเบียนนิติบุคคลของกรมพัฒนาธุรกิจการค้า\n" +
            "3. กรณีอื่น ๆ ให้ใช้เลขประจำตัวผู้เสียภาษีอากร (13 หลัก) ของกรมสรรพากร")
            .FontSize(8);
    }
}
