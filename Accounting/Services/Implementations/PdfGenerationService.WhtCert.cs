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
/// Three copies per cert: "ฉบับที่ 1" (ผู้ถูกหัก ใช้แนบแบบ) + "ฉบับที่ 2"
/// (ผู้ถูกหัก เก็บเป็นหลักฐาน) + "ฉบับที่ 3" (ผู้หักภาษี เก็บเป็นหลักฐาน),
/// each on its own A4 page.
/// </summary>
public partial class PdfGenerationService
{
    private byte[] BuildWhtCertPdf(WithholdingTaxCert cert, Company company,
        byte[]? signatureBytes = null, string? signerName = null)
    {
        EnsureThaiFontsRegistered();
        // ใบหัก ณ ที่จ่าย — ให้ใช้ฟอนต์ตระกูล Sarabun ก่อน (ให้ตรงกับไฟล์ที่
        // download จากเบราว์เซอร์ซึ่งใช้ Sarabun). ถ้าไม่มี Sarabun/TH Sarabun New
        // ถูก register ค่อย fallback Loma → system. (เดิมใช้ chain รวมที่ขึ้นต้น
        // Loma → Windows ไม่มี Loma เลย fallback Leelawadee หน้าตาต่างจาก download).
        var fontChain = new[] { "Sarabun", "TH Sarabun New", "Loma", "Noto Sans Thai", Fonts.Calibri, Fonts.Arial };
        var lines = cert.Lines.OrderBy(l => l.LineOrder).ToList();
        // Only use the signature if it actually decodes to an image QuestPDF
        // can embed — a corrupt base64 must never blank the whole page.
        var sigImg = LooksLikeImage(signatureBytes) ? signatureBytes : null;

        // ที่อยู่ต้องติดคำนำหน้า ต./อ./จ. (หรือ แขวง/เขต สำหรับ กทม.) — เอกสาร
        // 50 ทวิ เป็นเอกสารราชการ ที่อยู่ต้องอ่านออกชัดว่าส่วนไหนตำบล/อำเภอ/จังหวัด.
        // ใช้ FormatThaiAddress ตัวเดียวกับ HTML renderer + เอกสารอื่น (เดิม QuestPDF
        // native path นี้ join ด้วยช่องว่างเฉย ๆ → ที่อยู่ไม่มีคำนำหน้า ผู้ตรวจ/
        // สรรพากรอ่านไม่ออก) — helper จัดการ กทม.→แขวง/เขต + กันซ้ำ + parse free-text
        var fullAddress = FormatThaiAddress(
            company.Address, company.BuildingNumber, company.BuildingName, company.Moo, company.StreetName,
            company.SubDistrict, company.District, company.Province, company.PostalCode);
        var payeeAddr = FormatThaiAddress(
            cert.PayeeContact.Address, cert.PayeeContact.BuildingNumber, cert.PayeeContact.BuildingName,
            cert.PayeeContact.Moo, cert.PayeeContact.StreetName,
            cert.PayeeContact.SubDistrict, cert.PayeeContact.District,
            cert.PayeeContact.Province, cert.PayeeContact.PostalCode);

        var pdf = QuestPDF.Fluent.Document.Create(container =>
        {
            for (int copy = 1; copy <= 3; copy++)
            {
                var copyNum = copy;
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(10, Unit.Millimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(t => t.FontFamily(fontChain).FontSize(9.5f));
                    // เอกสารที่ถูกยกเลิก (Voided) — ประทับลายน้ำ "ยกเลิก /
                    // CANCELLED" สีแดงเฉียงกลางหน้า ให้เห็นเด่นชัด แต่ยัง
                    // พิมพ์เนื้อหาเอกสารได้เหมือนเดิม (เก็บเป็นหลักฐาน/audit).
                    if (cert.Status == Models.DTOs.Tax.WithholdingTaxCertStatus.Voided)
                    {
                        try
                        {
                            page.Background().AlignCenter().AlignMiddle().Text(t =>
                            {
                                t.Span("ยกเลิก\nCANCELLED").FontSize(80).Bold()
                                    .FontColor("#33DC2626");   // แดง ~20% opacity (ARGB)
                            });
                        }
                        catch { /* watermark เป็นของตกแต่ง — ห้าม block เอกสาร */ }
                    }
                    page.Content().Column(col =>
                    {
                        BuildCopyHeader(col, copyNum);
                        // Heavy outer frame (matches the official form's 2.5px border).
                        col.Item().Border(2f).BorderColor(Colors.Black).Column(form =>
                        {
                            BuildTitleBar(form, cert.CertificateNumber);
                            BuildPartyBlock(form, "ผู้มีหน้าที่หักภาษี ณ ที่จ่าย", company.TaxId,
                                company.Name + CertBranchSuffix(company.TaxId, company.BranchCode, company.BranchName), fullAddress);
                            BuildPartyBlock(form, "ผู้ถูกหักภาษี ณ ที่จ่าย", cert.PayeeContact.TaxId,
                                cert.PayeeContact.Name + CertBranchSuffix(cert.PayeeContact.TaxId, cert.PayeeContact.BranchCode, cert.PayeeContact.BranchName), payeeAddr);
                            BuildFormTypeRow(form, cert);
                            BuildIncomeTable(form, lines, cert.TotalIncomeAmount, cert.TotalTaxAmount);
                            BuildTotalInWords(form, cert.TotalTaxAmount);
                            BuildFundLine(form);
                            BuildConditionsRow(form, cert);
                            BuildBottomSplit(form, cert.IssuedDate, sigImg, signerName);
                        });
                        BuildFootnote(col);
                    });
                });
            }
        });
        return pdf.GeneratePdf();
    }

    // ── Copy header (ฉบับที่ 1 / 2 / 3) ──────────────────────────────
    private static void BuildCopyHeader(QuestPDF.Fluent.ColumnDescriptor col, int copyNum)
    {
        col.Item().Text(t =>
        {
            t.Span("ฉบับที่ 1 ").FontSize(10).Bold().FontColor(copyNum == 1 ? Colors.Black : Colors.Grey.Medium);
            t.Span("(สำหรับผู้ถูกหักภาษี ณ ที่จ่าย ใช้แนบพร้อมกับแบบแสดงรายการภาษี)")
                .FontSize(8).Italic().FontColor(copyNum == 1 ? Colors.Black : Colors.Grey.Medium);
        });
        col.Item().Text(t =>
        {
            t.Span("ฉบับที่ 2 ").FontSize(10).Bold().FontColor(copyNum == 2 ? Colors.Black : Colors.Grey.Medium);
            t.Span("(สำหรับผู้ถูกหักภาษี ณ ที่จ่าย เก็บไว้เป็นหลักฐาน)")
                .FontSize(8).Italic().FontColor(copyNum == 2 ? Colors.Black : Colors.Grey.Medium);
        });
        col.Item().PaddingBottom(2).Text(t =>
        {
            t.Span("ฉบับที่ 3 ").FontSize(10).Bold().FontColor(copyNum == 3 ? Colors.Black : Colors.Grey.Medium);
            t.Span("(สำหรับผู้หักภาษี ณ ที่จ่าย เก็บไว้เป็นหลักฐาน)")
                .FontSize(8).Italic().FontColor(copyNum == 3 ? Colors.Black : Colors.Grey.Medium);
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
                c.Item().AlignCenter().Text("หนังสือรับรองการหักภาษี ณ ที่จ่าย").FontSize(15).Bold();
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
            // Row 1: heading on left, 13-digit TIN boxes on right.
            // Widths kept TIGHT so heading + label + 13 fixed boxes never
            // exceed the row (a too-wide fixed element throws QuestPDF's
            // "conflicting size constraints").
            c.Item().Row(r =>
            {
                r.ConstantItem(140).Text(t => t.Span(heading + " :-").Bold().FontSize(9.5f));
                r.RelativeItem().AlignRight().Row(rr =>
                {
                    rr.AutoItem().AlignBottom().PaddingRight(3).Text(t =>
                    {
                        t.Span("เลขประจำตัวผู้เสียภาษีอากร (13 หลัก)").FontSize(8);
                        t.Span("*").FontColor(Colors.Red.Darken2).FontSize(8);
                    });
                    rr.AutoItem().Element(e => DrawTinBoxes(e, taxId));
                });
            });
            // Row 1b: legacy 10-digit TIN row (matches the official form which
            // still prints the old format underneath the 13-digit boxes).
            c.Item().PaddingTop(2).Row(r =>
            {
                r.RelativeItem().AlignRight().Row(rr =>
                {
                    rr.AutoItem().AlignBottom().PaddingRight(3).Text("เลขประจำตัวผู้เสียภาษีอากร").FontSize(7.5f);
                    rr.AutoItem().Element(e => DrawOldTinBoxes(e, taxId));
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
                if (g > 0) r.AutoItem().Width(4).AlignMiddle().AlignCenter().Text("-").FontSize(9).Bold();
                for (int i = groups[g][0]; i < groups[g][1]; i++)
                {
                    var ch = i < digits.Length ? digits[i].ToString().Trim() : "";
                    r.AutoItem().Border(1).BorderColor(Colors.Black).Width(12).Height(15)
                        .AlignCenter().AlignMiddle().Text(ch).FontSize(9).Bold();
                }
            }
        });
    }

    // ── Legacy 10-digit TIN boxes, grouped 3-4-3 ─────────────────────
    private static void DrawOldTinBoxes(QuestPDF.Infrastructure.IContainer e, string? taxId)
    {
        var dg = Regex.Replace(taxId ?? "", @"\D", "");
        if (dg.Length > 10) dg = dg[..10];
        dg = dg.PadRight(10);
        e.Row(r =>
        {
            int[][] groups = { new[]{0,3}, new[]{3,7}, new[]{7,10} };
            for (int g = 0; g < groups.Length; g++)
            {
                if (g > 0) r.AutoItem().Width(4);
                for (int i = groups[g][0]; i < groups[g][1]; i++)
                {
                    var ch = i < dg.Length ? dg[i].ToString().Trim() : "";
                    r.AutoItem().Border(0.8f).BorderColor(Colors.Black).Width(10).Height(13)
                        .AlignCenter().AlignMiddle().Text(ch).FontSize(8);
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
            // Three numeric cells shared by every row.
            void NumCells((decimal Inc, decimal Tax, DateTime? Date) m)
            {
                tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignCenter()
                    .Text(FmtDate(m.Date)).FontSize(9);
                tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignRight()
                    .Text(FmtAmt(m.Inc)).FontSize(9);
                tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignRight()
                    .Text(FmtAmt(m.Tax)).FontSize(9);
            }
            void Row(string label, (decimal Inc, decimal Tax, DateTime? Date) m, float labelSize = 9f)
            {
                tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3)
                    .Text(label).FontSize(labelSize);
                NumCells(m);
            }
            Row("1. เงินเดือน ค่าจ้าง เบี้ยเลี้ยง โบนัส ฯลฯ ตามมาตรา 40 (1)", Match("1", "40(1)"));
            Row("2. ค่าธรรมเนียม ค่านายหน้า ฯลฯ ตามมาตรา 40 (2)", Match("2", "40(2)"));
            Row("3. ค่าแห่งลิขสิทธิ์ ฯลฯ ตามมาตรา 40 (3)", Match("3", "40(3)"));
            Row("4. (ก) ดอกเบี้ย ฯลฯ ตามมาตรา 40 (4) (ก)", Match("4a", "40(4)(a)"));
            // Row 4(ข) — the dividend block with the full nested sub-list, to
            // match the official RD form verbatim.
            tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3).Column(cell =>
            {
                cell.Item().Text("    (ข) เงินปันผล เงินส่วนแบ่งกำไร ฯลฯ ตามมาตรา 40 (4) (ข)").FontSize(8);
                cell.Item().PaddingLeft(10).Text("(1) กรณีผู้ได้รับเงินปันผลได้รับเครดิตภาษี โดยจ่ายจาก").FontSize(7.5f);
                cell.Item().PaddingLeft(16).Text("กำไรสุทธิของกิจการที่ต้องเสียภาษีเงินได้นิติบุคคลในอัตราดังนี้").FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text("(1.1) อัตราร้อยละ 30 ของกำไรสุทธิ").FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text("(1.2) อัตราร้อยละ 25 ของกำไรสุทธิ").FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text("(1.3) อัตราร้อยละ 20 ของกำไรสุทธิ").FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text("(1.4) อัตราอื่น ๆ (ระบุ) ............... ของกำไรสุทธิ").FontSize(7.5f);
                cell.Item().PaddingLeft(10).Text("(2) กรณีผู้ได้รับเงินปันผลไม่ได้รับเครดิตภาษี เนื่องจากจ่ายจาก").FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text("(2.1) กำไรสุทธิของกิจการที่ได้รับยกเว้นภาษีเงินได้นิติบุคคล").FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text("(2.2) เงินปันผลหรือเงินส่วนแบ่งของกำไรที่ได้รับยกเว้นไม่ต้องนำมารวมคำนวณเป็นรายได้").FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text("(2.3) กำไรสุทธิส่วนที่ได้หักผลขาดทุนสุทธิยกมาไม่เกิน 5 ปีก่อนรอบบัญชีปีปัจจุบัน").FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text("(2.4) กำไรที่รับรู้ทางบัญชีโดยวิธีส่วนได้เสีย (equity method)").FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text("(2.5) อื่น ๆ (ระบุ) ............................................").FontSize(7.5f);
            });
            NumCells(Match("4b", "40(4)(b)"));
            Row("5. การจ่ายเงินได้ที่ต้องหักภาษี ณ ที่จ่าย ตามคำสั่งกรมสรรพากรที่ออกตามมาตรา 3 เตรส " +
                "เช่น รางวัล ส่วนลดหรือประโยชน์ใด ๆ เนื่องจากการส่งเสริมการขาย รางวัลในการประกวด การแข่งขัน " +
                "การชิงโชค ค่าแสดงของนักแสดงสาธารณะ ค่าจ้างทำของ ค่าโฆษณา ค่าเช่า ค่าขนส่ง ค่าบริการ " +
                "ค่าเบี้ยประกันวินาศภัย ฯลฯ",
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

    private static void BuildBottomSplit(QuestPDF.Fluent.ColumnDescriptor col, DateTime? issuedDate,
        byte[]? signatureImage = null, string? signerName = null)
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
            // Right cell: signature column on the left + circular stamp pinned
            // to the right edge, vertically centred — matches the official form
            // (HTML: stamp is position:absolute right, top:50%).
            r.RelativeItem(65).Padding(6).Row(rr =>
            {
                rr.RelativeItem().Column(c =>
                {
                    c.Item().Text("ขอรับรองว่าข้อความและตัวเลขดังกล่าวข้างต้นถูกต้องตรงกับความจริงทุกประการ")
                        .FontSize(9);
                    // Signature image drawn ABOVE the "ลงชื่อ" line when present.
                    if (signatureImage != null)
                    {
                        c.Item().PaddingTop(6).AlignCenter().Element(e =>
                        {
                            try { e.Height(15, Unit.Millimetre).Image(signatureImage); }
                            catch { /* decorative — never block the PDF */ }
                        });
                        c.Item().AlignCenter().Text(t =>
                        {
                            t.Span("ลงชื่อ ");
                            t.Span(string.IsNullOrWhiteSpace(signerName) ? "__________________" : signerName!).Bold();
                            t.Span(" ผู้มีหน้าที่หักภาษี ณ ที่จ่าย");
                        });
                    }
                    else
                    {
                        c.Item().PaddingTop(18).AlignCenter().Text(t =>
                        {
                            t.Span("ลงชื่อ ");
                            t.Span("__________________");
                            t.Span(" ผู้มีหน้าที่หักภาษี ณ ที่จ่าย");
                        });
                        if (!string.IsNullOrWhiteSpace(signerName))
                            c.Item().AlignCenter().Text(t => { t.Span("( ").FontSize(8); t.Span(signerName!).Bold().FontSize(8); t.Span(" )").FontSize(8); });
                    }
                    c.Item().PaddingTop(4).AlignCenter().Text(t =>
                    {
                        t.Span(dd).Bold(); t.Span(" / ");
                        t.Span(mm).Bold(); t.Span(" / ");
                        t.Span(yy).Bold();
                    });
                    c.Item().AlignCenter().Text("(วัน เดือน ปี ที่ออกหนังสือรับรองฯ)").FontSize(7).FontColor(Colors.Grey.Darken1);
                });
                // Company-seal placeholder, vertically centred. ConstantItem
                // must be WIDER than the seal box + its left padding, or the
                // fixed-width seal overflows → "conflicting size constraints".
                rr.ConstantItem(80).AlignMiddle().PaddingLeft(4).Element(DrawStampCircle);
            });
        });
    }

    // ── "ประทับตรานิติบุคคล (ถ้ามี)" seal area ──────────────────────
    // QuestPDF (this version) has no border-radius and Canvas/SkiaSharp isn't
    // referenced elsewhere in the project, so to stay build-safe we draw a
    // bordered square positioned to the RIGHT of the signature, vertically
    // centred — matching the official form's stamp PLACEMENT (the previous
    // version pushed it to the bottom of the column). The caption keeps the
    // standard 3-line italic text.
    private static void DrawStampCircle(QuestPDF.Infrastructure.IContainer e)
    {
        e.Width(72).Height(72)
         .Border(1f).BorderColor(Colors.Grey.Darken1)
         .AlignCenter().AlignMiddle()
         .Text("ประทับตรา\nนิติบุคคล\n(ถ้ามี)")
         .FontSize(8).FontColor(Colors.Grey.Darken1).Italic();
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
