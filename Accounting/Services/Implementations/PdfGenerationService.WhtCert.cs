using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using System.Text.RegularExpressions;
using Accounting.Helpers;

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
///
/// <b>ข้อความของแบบทุกคำมาจาก <see cref="WhtCertFormText"/></b> — ตัวเดียวกับ HTML renderer (<c>BuildWithholdingTaxCertHtml</c>)
/// ห้ามพิมพ์ข้อความแบบเป็น literal ที่นี่ (กฎ #4 A · <c>tools/required_call_site_check.py</c>) · เดิม QuestPDF เพี้ยนจาก HTML:
/// หมายเหตุข้อ 3 ขาด "นอกเหนือจาก 1. และ 2." · (2.2) ตัดท้าย · (2.3) "ก่อนรอบบัญชี" · ช่องเลขเดิม 10 หลักพิมพ์ 10 หลักแรกของเลข 13 หลัก
/// Three copies per cert: "ฉบับที่ 1" (ผู้ถูกหัก ใช้แนบแบบ) + "ฉบับที่ 2"
/// (ผู้ถูกหัก เก็บเป็นหลักฐาน) + "ฉบับที่ 3" (ผู้หักภาษี เก็บเป็นหลักฐาน),
/// each on its own A4 page.
/// </summary>
public partial class PdfGenerationService
{
    /// <param name="funds">ยอด "เงินที่จ่ายเข้า" กองทุน (A9 · 2026-10-13) — ใบรายปีของพนักงานเท่านั้น · null = เส้นว่างเหมือนเดิม ·
    /// พารามิเตอร์เดียวกับ HTML <c>BuildWithholdingTaxCertHtml</c> (สอง renderer ห้าม drift)</param>
    private byte[] BuildWhtCertPdf(WithholdingTaxCert cert, Company company,
        byte[]? signatureBytes = null, string? signerName = null, WhtCertFundAmounts? funds = null)
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
            for (int copy = 1; copy <= WhtCertFormText.Copies.Count; copy++)
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
                                t.Span(WhtCertFormText.VoidWatermark).FontSize(80).Bold()
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
                            // ชื่อผู้หัก/ผู้ถูกหักผ่านตัวตัดสินเดียว WhtCertFormText.PartyName (ตัวเดียวกับ HTML + หน้าพิมพ์ wht.html · 2026-10-14)
                            BuildPartyBlock(form, WhtCertFormText.PayerHeading, company.TaxId,
                                WhtCertFormText.PartyName(null, company.Name, company.TaxId, company.BranchCode, company.BranchName), fullAddress);
                            // ชื่อผู้ถูกหัก = คำนำหน้า (Contact.TitleTh) + ชื่อ — หน้าผู้ติดต่อบอกให้กรอกชื่อ
                            // "ไม่ต้องใส่คำนำหน้า" ⇒ ถ้าไม่ต่อตรงนี้ ใบรับรองพิมพ์ "สมชาย ใจดี" ขณะไฟล์
                            // ภ.ง.ด.3 ประกาศ "นาย" = สอง renderer ของผู้ถูกหักคนเดียวเล่าคนละชื่อ
                            // "ลำดับที่ … ในแบบ" + ช่องแบบยื่นอยู่ในกรอบผู้ถูกหัก ตามแบบทางการ (รอบ 204 · เดิมกรอบแยก)
                            BuildPartyBlock(form, WhtCertFormText.PayeeHeading, cert.PayeeContact.TaxId,
                                WhtCertFormText.PartyName(cert.PayeeContact.TitleTh, cert.PayeeContact.Name, cert.PayeeContact.TaxId,
                                    cert.PayeeContact.BranchCode, cert.PayeeContact.BranchName), payeeAddr,
                                formTypeOf: cert);
                            BuildIncomeTable(form, lines, cert.TotalIncomeAmount, cert.TotalTaxAmount);
                            BuildTotalInWords(form, cert.TotalTaxAmount);
                            BuildFundLine(form, funds);
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
        for (var i = 0; i < WhtCertFormText.Copies.Count; i++)
        {
            var c = WhtCertFormText.Copies[i];
            var on = copyNum == i + 1;
            var item = i == WhtCertFormText.Copies.Count - 1 ? col.Item().PaddingBottom(2) : col.Item();
            item.Text(t =>
            {
                t.Span(c.Label + " ").FontSize(10).Bold().FontColor(on ? Colors.Black : Colors.Grey.Medium);
                t.Span(c.Note).FontSize(8).Italic().FontColor(on ? Colors.Black : Colors.Grey.Medium);
            });
        }
    }

    // ── Title bar with เล่มที่/เลขที่ on right ──────────────────────
    private static void BuildTitleBar(QuestPDF.Fluent.ColumnDescriptor col, string certNum)
    {
        col.Item().BorderBottom(1).BorderColor(Colors.Black).Padding(5).Row(r =>
        {
            r.ConstantItem(110);   // spacer
            r.RelativeItem().AlignCenter().Column(c =>
            {
                c.Item().AlignCenter().Text(WhtCertFormText.Title).FontSize(15).Bold();
                c.Item().AlignCenter().Text(WhtCertFormText.Subtitle).FontSize(10);
            });
            r.ConstantItem(110).Column(c =>
            {
                c.Item().Text(t => { t.Span(WhtCertFormText.BookNo + " "); t.Span("__________"); });
                c.Item().Text(t => { t.Span(WhtCertFormText.CertNo + " "); t.Span(certNum ?? "").Bold(); });
            });
        });
    }

    // ── Party block (payer or payee) — TIN boxes + name + address ────
    private static void BuildPartyBlock(QuestPDF.Fluent.ColumnDescriptor col, string heading,
        string? taxId, string name, string fullAddress, WithholdingTaxCert? formTypeOf = null)
    {
        col.Item().BorderBottom(1).BorderColor(Colors.Black).Padding(5).Column(c =>
        {
            // Row 1: heading on left, 13-digit TIN boxes on right.
            // Widths kept TIGHT so heading + label + 13 fixed boxes never
            // exceed the row (a too-wide fixed element throws QuestPDF's
            // "conflicting size constraints").
            c.Item().Row(r =>
            {
                r.ConstantItem(140).Text(t => t.Span(heading).Bold().FontSize(9.5f));
                r.RelativeItem().AlignRight().Row(rr =>
                {
                    rr.AutoItem().AlignBottom().PaddingRight(3).Text(t =>
                    {
                        t.Span(WhtCertFormText.TaxId13).FontSize(8);
                        t.Span("*").FontColor(Colors.Red.Darken2).FontSize(8);
                    });
                    rr.AutoItem().Element(e => DrawTinBoxes(e, taxId));
                });
            });
            // Row 2: name field with dotted underline + legacy 10-digit TIN boxes on the SAME row (right) —
            // ตามแบบทางการ (รอบ 204 · เดิมช่อง 10 หลักแยกบรรทัดเหนือชื่อ)
            c.Item().PaddingTop(2).Row(r =>
            {
                r.ConstantItem(40).Text(WhtCertFormText.Name).Bold();
                r.RelativeItem().Border(0).BorderBottom(0.5f).BorderColor(Colors.Black)
                    .Text(" " + (name ?? "") + " ").FontSize(10);
                r.AutoItem().PaddingLeft(6).AlignBottom().PaddingRight(3).Text(WhtCertFormText.TaxId10).FontSize(7.5f);
                r.AutoItem().AlignBottom().Element(e => DrawOldTinBoxes(e));
            });
            c.Item().PaddingLeft(40).Text(WhtCertFormText.NameHint)
                .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);
            // Row 3: address
            c.Item().PaddingTop(2).Row(r =>
            {
                r.ConstantItem(40).Text(WhtCertFormText.Address).Bold();
                r.RelativeItem().BorderBottom(0.5f).BorderColor(Colors.Black)
                    .Text(" " + (fullAddress ?? "") + " ").FontSize(10);
            });
            c.Item().PaddingLeft(40).Text(WhtCertFormText.AddressHint)
                .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);
            if (formTypeOf is { } ftCert)
                c.Item().PaddingTop(4).Element(e => BuildFormTypeRow(e, ftCert));
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

    // ── ช่องเลขประจำตัวผู้เสียภาษีอากรแบบเดิม 10 หลัก — ว่างเสมอ, กลุ่ม 1-4-4-1 ตามแบบ (เดิม 3-4-3 และพิมพ์ 10 หลักแรกของเลข 13 หลัก
    // ซึ่งไม่ใช่เลขเดิมของใคร — ตรงกับ HTML renderer)
    private static void DrawOldTinBoxes(QuestPDF.Infrastructure.IContainer e)
    {
        e.Row(r =>
        {
            var groups = WhtCertFormText.TaxId10Groups;
            for (int g = 0; g < groups.Length; g++)
            {
                if (g > 0) r.AutoItem().Width(4);
                for (int i = 0; i < groups[g]; i++)
                    r.AutoItem().Border(0.8f).BorderColor(Colors.Black).Width(10).Height(13);
            }
        });
    }

    // ── Form-type checkbox grid (ภ.ง.ด.1ก / 1ก พิเศษ / 2 / 3 / 2ก / 3ก / 53)
    // ช่องที่ติ๊กจากตัวตัดสินเดียว WhtCertFormText.FormBoxIndex (ตัวเดียวกับ HTML + หน้าพิมพ์ wht.html) — รอบ 204: ภ.ง.ด.2 → (3)
    private static void BuildFormTypeRow(QuestPDF.Infrastructure.IContainer container, WithholdingTaxCert cert)
    {
        var box = WhtCertFormText.FormBoxIndex(cert.TaxFormType);
        container.Row(r =>
        {
            r.ConstantItem(200).Column(c =>
            {
                c.Item().Row(rr =>
                {
                    rr.AutoItem().PaddingRight(4).AlignMiddle().Text(WhtCertFormText.SeqLabel).FontSize(10);
                    // ลำดับในใบแนบ ภ.ง.ด. — ระบบยังคำนวณให้คงที่ไม่ได้ ⇒ ว่าง (เดิมพิมพ์เลขที่หนังสือรับรอง ซึ่งเป็นคนละเลข)
                    rr.AutoItem().Border(1).BorderColor(Colors.Black).MinWidth(70).Height(20);
                    rr.AutoItem().PaddingLeft(4).AlignMiddle().Text(WhtCertFormText.SeqInForm).FontSize(10);
                });
                c.Item().PaddingTop(2).Text(WhtCertFormText.SeqHint)
                    .FontSize(7).Italic().FontColor(Colors.Grey.Darken1);
            });
            r.RelativeItem().Column(c =>
            {
                c.Item().Row(rr =>
                {
                    for (var i = 0; i < 4; i++) DrawCheckOption(rr, box == i, WhtCertFormText.FormBoxes[i]);
                });
                c.Item().PaddingTop(2).Row(rr =>
                {
                    for (var i = 4; i < 7; i++) DrawCheckOption(rr, box == i, WhtCertFormText.FormBoxes[i]);
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
        var row6Spec = WhtCertFormText.Row6Spec(lines);
        // แถวของแต่ละบรรทัดตัดสินจาก **มาตรา** ผ่านตารางกลางตัวเดียว
        // (`Helpers/ThaiWhtRateTable.CertificateRow`) ไม่ใช่ allow-list ของรหัส
        // ที่พิมพ์มือ — เดิมตกรหัส `8ad`/`8tr` ที่ระบบเองสร้าง และแถว "อื่น ๆ"
        // ก็เป็น allow-list จึงไม่ใช่ตาข่ายรับ ⇒ บรรทัดหายจากทุกแถวแต่ยอดรวม
        // ยังเต็ม (ผลตรวจทีม D · D-03)
        var byRow = lines
            .GroupBy(l => Accounting.Helpers.ThaiWhtRateTable.CertificateRow(l.IncomeTypeCode))
            .ToDictionary(g => g.Key, g => g.ToList());

        (decimal Inc, decimal Tax, DateTime? Date) Match(string rowKey)
        {
            if (!byRow.TryGetValue(rowKey, out var hits) || hits.Count == 0) return (0, 0, null);
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
                    .Text(WhtCertFormText.ColIncomeType).FontSize(9).Bold();
                h.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignCenter()
                    .Text(WhtCertFormText.ColDate1 + "\n" + WhtCertFormText.ColDate2).FontSize(9).Bold();
                h.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignCenter()
                    .Text(WhtCertFormText.ColAmount).FontSize(9).Bold();
                h.Cell().Border(1).BorderColor(Colors.Black).Padding(3).AlignCenter()
                    .Text(WhtCertFormText.ColTax1 + "\n" + WhtCertFormText.ColTax2).FontSize(9).Bold();
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
            Row(WhtCertFormText.Row1, Match("1"));
            Row(WhtCertFormText.Row2, Match("2"));
            Row(WhtCertFormText.Row3, Match("3"));
            Row(WhtCertFormText.Row4a, Match("4a"));
            // รอบ 204 ทีม D (2f): ผู้ใช้ระบุแถวย่อยปันผล ⇒ ยอดพิมพ์บนแถวย่อยนั้น (ตัวตั้งเดียว WhtCertFormText.DividendBlock — ตัวเดียวกับ HTML/หน้าพิมพ์) ·
            // ไม่ระบุ (null) ⇒ บล็อกเดิมทุกประการ
            var divBlock = WhtCertFormText.DividendBlock(lines);
            if (divBlock != null)
            {
                foreach (var dr in divBlock)
                {
                    tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3).Column(cell =>
                    {
                        foreach (var dl in dr.Lines)
                            cell.Item().PaddingLeft(dl.Indent == 0 ? 0 : (dl.Indent == 1 ? 10 : 22) + (dl.Continuation ? 8 : 0))
                                .Text((dl.Indent == 0 ? "    " : "") + dl.Text).FontSize(dl.Indent == 0 ? 8 : 7.5f);
                    });
                    NumCells(dr.HasAmount ? (dr.IncomeAmount, dr.TaxAmount, dr.PaymentDate) : (0m, 0m, (DateTime?)null));
                }
            }
            else
            {
            // Row 4(ข) — the dividend block with the full nested sub-list, to
            // match the official RD form verbatim.
            tbl.Cell().Border(1).BorderColor(Colors.Black).Padding(3).Column(cell =>
            {
                // แบ่งบรรทัดตรงกับ HTML renderer (คู่ a/b ของ WhtCertFormText)
                cell.Item().Text("    " + WhtCertFormText.Row4b).FontSize(8);
                cell.Item().PaddingLeft(10).Text(WhtCertFormText.Div1a).FontSize(7.5f);
                cell.Item().PaddingLeft(16).Text(WhtCertFormText.Div1b).FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text(WhtCertFormText.Div11).FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text(WhtCertFormText.Div12).FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text(WhtCertFormText.Div13).FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text(WhtCertFormText.Div14 + " .................. " + WhtCertFormText.Div14Tail).FontSize(7.5f);
                cell.Item().PaddingLeft(10).Text(WhtCertFormText.Div2).FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text(WhtCertFormText.Div21).FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text(WhtCertFormText.Div22a).FontSize(7.5f);
                cell.Item().PaddingLeft(30).Text(WhtCertFormText.Div22b).FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text(WhtCertFormText.Div23a).FontSize(7.5f);
                cell.Item().PaddingLeft(30).Text(WhtCertFormText.Div23b).FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text(WhtCertFormText.Div24).FontSize(7.5f);
                cell.Item().PaddingLeft(22).Text(WhtCertFormText.Div25 + " ............................................").FontSize(7.5f);
            });
            NumCells(Match("4b"));
            }
            Row(WhtCertFormText.Row5, Match("5"), 8f);
            // แถว 6 อื่น ๆ (ระบุ) — พิมพ์รายการของบรรทัดที่ตกแถวนี้ (แถว 5 ไม่มีช่องระบุบนแบบ)
            Row(WhtCertFormText.Row6 + " " + (row6Spec ?? "........................................................"),
                Match("6"));
            // Totals row
            tbl.Cell().ColumnSpan(2).Border(1).BorderColor(Colors.Black).Padding(3).AlignRight()
                .Text(WhtCertFormText.Total).FontSize(9.5f).Bold();
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
                t.Span(WhtCertFormText.TotalWords + " ").Bold();
                t.Span(WhtCertFormText.TotalWordsHint + " ").Italic();
                t.Span(" " + ThaiBahtText.Format(totalTax) + " ").Bold();
            });
    }

    // A9 (2026-10-13): ใบรายปีของพนักงานพิมพ์ยอดประกันสังคม/สำรองเลี้ยงชีพ — ตัวจัดรูปแบบเดียวกับ HTML (WhtCertFormText.FundAmountText) ·
    // ไม่มีค่า (ใบผู้รับเงินทั่วไป · กบข.) = เส้นว่างเดิม
    private static void BuildFundLine(QuestPDF.Fluent.ColumnDescriptor col, WhtCertFundAmounts? funds)
    {
        static string Cell(decimal? amount) => " " + (WhtCertFormText.FundAmountText(amount) ?? "________") + " ";
        col.Item().BorderBottom(1).BorderColor(Colors.Black).Padding(5).Text(t =>
        {
            t.Span(WhtCertFormText.FundLead + " ").Bold().FontSize(9);
            t.Span(WhtCertFormText.FundGov + Cell(funds?.Gov) + WhtCertFormText.Baht + "  " +
                   WhtCertFormText.FundSso + Cell(funds?.Sso) + WhtCertFormText.Baht + "  " +
                   WhtCertFormText.FundPvd + Cell(funds?.Pvd) + WhtCertFormText.Baht).FontSize(9);
        });
    }

    private static void BuildConditionsRow(QuestPDF.Fluent.ColumnDescriptor col, WithholdingTaxCert cert)
    {
        // ตัวตัดสินเดียว WhtCertFormText.PayerConditionIndex — รอบ 204: ออกให้ครั้งเดียว (PayOnce) → (3)
        var cond = WhtCertFormText.PayerConditionIndex(cert.CertificateType);
        col.Item().BorderBottom(1).BorderColor(Colors.Black).Padding(5).Row(r =>
        {
            var pc = WhtCertFormText.PayerConditions;
            r.ConstantItem(75).AlignMiddle().Text(WhtCertFormText.PayerConditionHeading).Bold().FontSize(10);
            DrawCheckOption(r, cond == 0, pc[0]);
            DrawCheckOption(r, cond == 1, pc[1]);
            DrawCheckOption(r, cond == 2, pc[2]);
            DrawCheckOption(r, cond == 3, pc[3] + " ________");
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
            // คำเตือน: หัวข้อซ้าย ข้อความห้อยด้านขวา ตามแบบทางการ (รอบ 204 · เดิมหัวข้อกลางอยู่เหนือข้อความ)
            r.RelativeItem(35).BorderRight(1).BorderColor(Colors.Black).Padding(6).Row(w =>
            {
                w.AutoItem().PaddingRight(4).Text(WhtCertFormText.WarningHeading).Bold().FontSize(9);
                w.RelativeItem().Text(WhtCertFormText.WarningText).FontSize(8);
            });
            // Right cell: signature column on the left + circular stamp pinned
            // to the right edge, vertically centred — matches the official form
            // (HTML: stamp is position:absolute right, top:50%).
            r.RelativeItem(65).Padding(6).Row(rr =>
            {
                rr.RelativeItem().Column(c =>
                {
                    c.Item().Text(WhtCertFormText.Certify)
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
                            t.Span(WhtCertFormText.SignLead + " ");
                            t.Span(string.IsNullOrWhiteSpace(signerName) ? "__________________" : signerName!).Bold();
                            t.Span(" " + WhtCertFormText.SignRole);
                        });
                    }
                    else
                    {
                        c.Item().PaddingTop(18).AlignCenter().Text(t =>
                        {
                            t.Span(WhtCertFormText.SignLead + " ");
                            t.Span("__________________");
                            t.Span(" " + WhtCertFormText.SignRole);
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
                    c.Item().AlignCenter().Text(WhtCertFormText.SignDateHint).FontSize(7).FontColor(Colors.Grey.Darken1);
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
         .Text(string.Join("\n", WhtCertFormText.StampLines))
         .FontSize(8).FontColor(Colors.Grey.Darken1).Italic();
    }

    private static void BuildFootnote(QuestPDF.Fluent.ColumnDescriptor col)
    {
        // ข้อ 1 อยู่บรรทัดเดียวกับ "หมายถึง" · ข้อ 2–3 ตรงแนวข้อ 1 ตามแบบทางการ (รอบ 204 · เดิมทุกข้อขึ้นบรรทัดใหม่)
        col.Item().PaddingTop(3).Row(r =>
        {
            r.AutoItem().PaddingRight(4).Text(t =>
            {
                t.Span(WhtCertFormText.FootnoteHeading + " ").Bold().FontSize(9);
                t.Span(WhtCertFormText.FootnoteLead + " ").FontSize(8.5f);
                t.Span("*").FontColor(Colors.Red.Darken2).FontSize(8.5f);
                t.Span(" " + WhtCertFormText.FootnoteLeadTail).FontSize(8.5f);
            });
            r.RelativeItem().Text(string.Join("\n", WhtCertFormText.FootnoteItems)).FontSize(8);
        });
    }
}
