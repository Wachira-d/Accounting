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
/// <b>หน้าตาเท่ากับหน้าพิมพ์ (wht.html printCert) — 2026-10-16:</b> ผู้ใช้รายงาน "pdf ที่ download กับหน้ากดพิมพ์หน้าตาไม่เหมือนกัน
/// · pdf หน้าล้น" (ขอบ 10 มม. + ตัวอักษร/ช่องไฟใหญ่กว่า ⇒ ช่องลงชื่อตกไปหน้า 2 · ทั้งไฟล์ 6 หน้า · ☐/☑ ไม่มีในฟอนต์ ⇒ กล่องว่าง ·
/// ตราประทับเป็นสี่เหลี่ยม) ⇒ ทุกขนาดแปลงจาก CSS ของหน้าพิมพ์ตรงตัว (<see cref="Px"/>: 1px = 0.75pt · ขอบหน้า 4×6 มม. ·
/// ช่องติ๊ก/ตรา/เส้นประเป็น SVG) · ฟอนต์ Sarabun ฝังมากับแอป (Accounting/Fonts) · ล็อก "หนึ่งฉบับ = หนึ่งหน้า" ใน WhtCertPdfLayoutTests
/// Three copies per cert: "ฉบับที่ 1" (ผู้ถูกหัก ใช้แนบแบบ) + "ฉบับที่ 2"
/// (ผู้ถูกหัก เก็บเป็นหลักฐาน) + "ฉบับที่ 3" (ผู้หักภาษี เก็บเป็นหลักฐาน),
/// each on its own A4 page.
/// </summary>
public partial class PdfGenerationService
{
    // ── หน่วยจาก CSS ของหน้าพิมพ์ (wht.html printCert) — 1 CSS px = 0.75 pt ─────────────────
    private static float Px(float px) => px * 0.75f;
    private const string WhtInk = "#000000";
    private const string WhtMuted = "#666666";
    private const string WhtHint = "#444444";
    private static readonly string[] WhtFont = { "Sarabun", "TH Sarabun New", "Loma", "Noto Sans Thai", Fonts.Calibri, Fonts.Arial };
    // .f-lbl { font-weight:600 } — ไฟล์ Sarabun SemiBold ลงทะเบียนเป็นตระกูลแยก
    private static readonly string[] WhtSemiFont = { "Sarabun SemiBold", "Sarabun", "TH Sarabun New", "Loma", "Noto Sans Thai", Fonts.Calibri, Fonts.Arial };

    /// <param name="funds">ยอด "เงินที่จ่ายเข้า" กองทุน (A9 · 2026-10-13) — ใบรายปีของพนักงานเท่านั้น · null = เส้นว่างเหมือนเดิม ·
    /// พารามิเตอร์เดียวกับ HTML <c>BuildWithholdingTaxCertHtml</c> (สอง renderer ห้าม drift)</param>
    internal static byte[] BuildWhtCertPdf(WithholdingTaxCert cert, Company company,
        byte[]? signatureBytes = null, string? signerName = null, WhtCertFundAmounts? funds = null)
    {
        EnsureThaiFontsRegistered();
        var lines = cert.Lines.OrderBy(l => l.LineOrder).ToList();
        // Only use the signature if it actually decodes to an image QuestPDF
        // can embed — a corrupt base64 must never blank the whole page.
        var sigImg = LooksLikeImage(signatureBytes) ? signatureBytes : null;

        // ที่อยู่ต้องติดคำนำหน้า ต./อ./จ. (หรือ แขวง/เขต สำหรับ กทม.) — ใช้ FormatThaiAddress ตัวเดียวกับ HTML renderer + เอกสารอื่น
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
                    // @page { size: A4; margin: 4mm 6mm; } · body { font-size:13.5px; line-height:1.2 }
                    page.Size(PageSizes.A4);
                    page.MarginVertical(4, Unit.Millimetre);
                    page.MarginHorizontal(6, Unit.Millimetre);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(t => t.FontFamily(WhtFont).FontSize(Px(13.5f)).LineHeight(1.2f).FontColor(WhtInk));
                    // เอกสารที่ถูกยกเลิก (Voided) — ลายน้ำ "ยกเลิก / CANCELLED" แดงจางเฉียงกลางหน้า (.void-watermark)
                    if (cert.Status == Models.DTOs.Tax.WithholdingTaxCertStatus.Voided)
                    {
                        var wm = WhtCertFormText.VoidWatermark.Split('\n');
                        page.Foreground().AlignCenter().AlignMiddle().Rotate(-30).Column(c =>
                        {
                            c.Item().AlignCenter().Text(wm[0]).FontSize(Px(100)).ExtraBold().LetterSpacing(0.08f).FontColor("#38DC2626");
                            if (wm.Length > 1)
                                c.Item().AlignCenter().Text(wm[1]).FontSize(Px(34)).ExtraBold().LetterSpacing(0.4f).FontColor("#38DC2626");
                        });
                    }
                    // ScaleToFit: ข้อมูลยาวผิดปกติ (ที่อยู่หลายบรรทัด · แถวย่อยปันผลหลายแถว) ย่อทั้งฉบับให้พอดีหน้า — หนึ่งฉบับต้องเป็นหนึ่งหน้าเสมอ
                    // (เดิมล้นไปหน้าถัดไป ⇒ ฉบับเดียวกลายเป็นสองแผ่น · ผู้ใช้รายงาน 2026-10-16)
                    page.Content().ScaleToFit().Column(col =>
                    {
                        BuildCopyHeader(col, copyNum);
                        // .F { border:3px solid } — ทุกหมวดคั่นด้วยเส้น 1.5px
                        col.Item().Border(Px(3)).BorderColor(WhtInk).Column(form =>
                        {
                            BuildTitleBar(form, cert.CertificateNumber);
                            // ชื่อผู้หัก/ผู้ถูกหักผ่านตัวตัดสินเดียว WhtCertFormText.PartyName (ตัวเดียวกับ HTML + หน้าพิมพ์ wht.html · 2026-10-14)
                            BuildPartyBlock(form, WhtCertFormText.PayerHeading, company.TaxId,
                                WhtCertFormText.PartyName(null, company.Name, company.TaxId, company.BranchCode, company.BranchName), fullAddress);
                            // ชื่อผู้ถูกหัก = คำนำหน้า (Contact.TitleTh) + ชื่อ · "ลำดับที่ … ในแบบ" + ช่องแบบยื่นอยู่ในกรอบผู้ถูกหัก ตามแบบทางการ
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

    // ── ชิ้นส่วนที่ CSS วาดแต่ฟอนต์/QuestPDF ไม่มี — SVG ────────────────────────────────

    /// <summary>ข้อความคงที่ของแบบ — แทรกจุดตัดคำที่มองไม่เห็น (QuestPDF ตัดบรรทัดได้แค่ที่ช่องว่าง · เบราว์เซอร์ตัดตามคำ) ⇒ ตัดบรรทัดตรงกับหน้าพิมพ์ ·
    /// ไม่ใช้กับชื่อ/ที่อยู่/เลขที่ (ข้อมูลที่โปรแกรมอ่าน PDF ดึงไปใช้ — จุดตัดกลายเป็นช่องว่างตอนคัดลอกข้อความ)</summary>
    private static string W(string? text) => ThaiWordBreak.Soft(text);

    private static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>border-bottom:1px dotted #000 — เส้นจุดเต็มความกว้างของกล่อง</summary>
    private static string DottedLineSvg(QuestPDF.Infrastructure.Size size)
    {
        var w = Math.Max(size.Width, 1f);
        var h = Math.Max(size.Height, 1f);
        return $"<svg xmlns='http://www.w3.org/2000/svg' width='{F(w)}' height='{F(h)}' viewBox='0 0 {F(w)} {F(h)}'>"
             + $"<line x1='0' y1='{F(h - 0.375f)}' x2='{F(w)}' y2='{F(h - 0.375f)}' stroke='#000' stroke-width='0.75' stroke-dasharray='0.75,0.75'/></svg>";
    }

    /// <summary>กล่องที่มีเส้นจุดใต้ (.ul / .num-val / .fund-blank / .sig-dots) — เนื้อหาเป็นของผู้เรียก</summary>
    private static void Dotted(QuestPDF.Infrastructure.IContainer c, Action<QuestPDF.Infrastructure.IContainer> content)
        => c.Layers(l =>
        {
            l.Layer().AlignBottom().Height(1).Svg(DottedLineSvg);
            l.PrimaryLayer().Element(content);
        });

    /// <summary>☐ / ☑ ของหน้าพิมพ์ (.ck · 15px) — Sarabun ไม่มีอักขระนี้ (เดิมพิมพ์เป็นกล่องว่างทั้งสองแบบ ⇒ ไม่รู้ว่าติ๊กช่องไหน)</summary>
    private static void CheckBox(QuestPDF.Infrastructure.IContainer c, bool on)
    {
        var s = Px(11);
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='{F(s)}' height='{F(s)}' viewBox='0 0 11 11'>"
                + "<rect x='0.6' y='0.6' width='9.8' height='9.8' fill='none' stroke='#000' stroke-width='0.9'/>"
                + (on ? "<polyline points='2.2,5.6 4.4,8 8.8,2.6' fill='none' stroke='#000' stroke-width='1.3' stroke-linecap='round' stroke-linejoin='round'/>" : "")
                + "</svg>";
        c.Width(s).Height(s).Svg(svg);
    }

    // ── Copy header (ฉบับที่ 1 / 2 / 3) — .copy-labels { font-size:11px; line-height:1.2; padding-left:2px } ──
    private static void BuildCopyHeader(QuestPDF.Fluent.ColumnDescriptor col, int copyNum)
    {
        col.Item().PaddingLeft(Px(2)).PaddingBottom(Px(1)).Column(c =>
        {
            for (var i = 0; i < WhtCertFormText.Copies.Count; i++)
            {
                var cl = WhtCertFormText.Copies[i];
                var on = copyNum == i + 1;
                var color = on ? WhtInk : WhtMuted;
                c.Item().Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(Px(11)).FontColor(color));
                    t.Span(cl.Label).Bold();
                    var note = t.Span(" " + W(cl.Note)).Italic();
                    if (on) note.Bold();
                });
            }
        });
    }

    // ── Title bar — .title-cell { padding:3px 8px } · ซ้ายเว้น 160px · ขวา .num-cell 160px ──
    private static void BuildTitleBar(QuestPDF.Fluent.ColumnDescriptor col, string certNum)
    {
        col.Item().BorderBottom(Px(1.5f)).BorderColor(WhtInk).PaddingVertical(Px(3)).PaddingHorizontal(Px(8)).Row(r =>
        {
            r.ConstantItem(Px(160));
            r.RelativeItem().Column(c =>
            {
                c.Item().AlignCenter().Text(WhtCertFormText.Title).FontSize(Px(18)).Bold().LetterSpacing(0.04f);
                c.Item().AlignCenter().Text(WhtCertFormText.Subtitle).FontSize(Px(13));
            });
            r.ConstantItem(Px(160)).PaddingTop(Px(2)).Column(c =>
            {
                c.Item().Row(rr =>
                {
                    rr.AutoItem().PaddingRight(Px(3)).Text(WhtCertFormText.BookNo).FontSize(Px(12)).LineHeight(1.4f);
                    rr.AutoItem().Width(Px(8)).Element(e => Dotted(e, x => x.Text(" ").FontSize(Px(12)).LineHeight(1.4f)));
                });
                var num = certNum ?? "";
                var longNum = num.Length > 14;
                c.Item().Row(rr =>
                {
                    rr.AutoItem().PaddingRight(Px(3)).Text(WhtCertFormText.CertNo).FontSize(Px(12)).LineHeight(1.4f);
                    rr.AutoItem().MaxWidth(Px(120)).Element(e => Dotted(e, x => x.PaddingHorizontal(Px(2))
                        .Text(num).FontSize(Px(longNum ? 10 : 12)).LetterSpacing(longNum ? -0.03f : 0).LineHeight(1.4f).ClampLines(1)));
                });
            });
        });
    }

    // ── Party block (ผู้หัก/ผู้ถูกหัก) — .sec-cell { padding:2px 8px 3px } ──────────────
    private static void BuildPartyBlock(QuestPDF.Fluent.ColumnDescriptor col, string heading,
        string? taxId, string name, string fullAddress, WithholdingTaxCert? formTypeOf = null)
    {
        col.Item().BorderBottom(Px(1.5f)).BorderColor(WhtInk).PaddingTop(Px(2)).PaddingBottom(Px(3)).PaddingHorizontal(Px(8)).Column(c =>
        {
            // หัวข้อซ้าย · เลข 13 หลักชิดขวา (แถวสูงตามกล่องเลข 19px ในบรรทัด 1.2 ของหน้าพิมพ์)
            c.Item().PaddingVertical(Px(2)).Row(r =>
            {
                r.AutoItem().PaddingRight(Px(8)).Text(heading).Bold();
                r.RelativeItem().AlignRight().Row(rr =>
                {
                    rr.AutoItem().AlignMiddle().PaddingRight(Px(4)).Text(t =>
                    {
                        t.Span(WhtCertFormText.TaxId13);
                        t.Span("*").FontColor("#990000");
                    });
                    rr.AutoItem().AlignMiddle().Element(e => DrawTinBoxes(e, taxId));
                });
            });
            // ชื่อ + ช่องเลขเดิม 10 หลักบรรทัดเดียวกัน (ชิดขวา) ตามแบบทางการ
            c.Item().PaddingVertical(Px(2)).Row(r =>
            {
                r.ConstantItem(Px(40)).PaddingRight(Px(4)).AlignMiddle().Text(WhtCertFormText.Name).FontFamily(WhtSemiFont);
                r.RelativeItem().AlignLeft().AlignMiddle().Element(e => Dotted(e, x => x.PaddingHorizontal(Px(2)).Text(" " + (name ?? "") + " ")));
                r.AutoItem().PaddingLeft(Px(8)).PaddingRight(Px(4)).AlignMiddle().Text(WhtCertFormText.TaxId10).FontSize(Px(12));
                r.AutoItem().AlignMiddle().Element(DrawOldTinBoxes);
            });
            c.Item().PaddingLeft(Px(42)).Text(W(WhtCertFormText.NameHint))
                .FontSize(Px(10)).Italic().FontColor(WhtHint).LineHeight(1.25f);
            c.Item().PaddingVertical(Px(1)).Row(r =>
            {
                r.ConstantItem(Px(40)).PaddingRight(Px(4)).Text(WhtCertFormText.Address).FontFamily(WhtSemiFont);
                r.RelativeItem().AlignLeft().Element(e => Dotted(e, x => x.PaddingHorizontal(Px(2)).Text(" " + (fullAddress ?? "") + " ")));
            });
            c.Item().PaddingLeft(Px(42)).Text(W(WhtCertFormText.AddressHint))
                .FontSize(Px(10)).Italic().FontColor(WhtHint).LineHeight(1.25f);
            if (formTypeOf is { } ftCert)
                c.Item().PaddingTop(Px(3)).Element(e => BuildFormTypeRow(e, ftCert));
        });
    }

    /// <summary>.tb { width:15px; height:19px; border:1.2px; font-size:13px; bold; margin:0 0.5px }</summary>
    private static void TinBox(QuestPDF.Fluent.RowDescriptor r, string ch)
        => r.AutoItem().PaddingHorizontal(Px(0.5f)).Width(Px(15)).Height(Px(19)).Border(Px(1.2f)).BorderColor(WhtInk)
            .AlignCenter().AlignMiddle().Text(ch).FontSize(Px(13)).Bold().LineHeight(1f);

    /// <summary>.ts { width:5px; font-size:11px; bold }</summary>
    private static void TinDash(QuestPDF.Fluent.RowDescriptor r)
        => r.AutoItem().Width(Px(5)).AlignMiddle().AlignCenter().Text("-").FontSize(Px(11)).Bold().LineHeight(1f);

    // ── 13 ช่องเลขประจำตัวผู้เสียภาษี กลุ่ม 1-4-5-2-1 ──
    private static void DrawTinBoxes(QuestPDF.Infrastructure.IContainer e, string? taxId)
    {
        var digits = Regex.Replace(taxId ?? "", @"\D", "").PadRight(13);
        e.Row(r =>
        {
            int[][] groups = { new[]{0,1}, new[]{1,5}, new[]{5,10}, new[]{10,12}, new[]{12,13} };
            for (int g = 0; g < groups.Length; g++)
            {
                if (g > 0) TinDash(r);
                for (int i = groups[g][0]; i < groups[g][1]; i++)
                    TinBox(r, i < digits.Length ? digits[i].ToString().Trim() : "");
            }
        });
    }

    // ── ช่องเลขประจำตัวผู้เสียภาษีอากรแบบเดิม 10 หลัก — ว่างเสมอ, กลุ่ม 1-4-4-1 ตามแบบ (ขนาดเดียวกับช่อง 13 หลัก เหมือนหน้าพิมพ์)
    private static void DrawOldTinBoxes(QuestPDF.Infrastructure.IContainer e)
    {
        e.Row(r =>
        {
            var groups = WhtCertFormText.TaxId10Groups;
            for (int g = 0; g < groups.Length; g++)
            {
                if (g > 0) TinDash(r);
                for (int i = 0; i < groups[g]; i++) TinBox(r, "");
            }
        });
    }

    // ── ช่องแบบยื่น (ภ.ง.ด.1ก … 53) — .ftype-cell · ซ้าย 240px "ลำดับที่ … ในแบบ" · ขวาตาราง 4+3 ช่อง
    // ช่องที่ติ๊กจากตัวตัดสินเดียว WhtCertFormText.FormBoxIndex (ตัวเดียวกับ HTML + หน้าพิมพ์ wht.html) — รอบ 204: ภ.ง.ด.2 → (3)
    private static void BuildFormTypeRow(QuestPDF.Infrastructure.IContainer container, WithholdingTaxCert cert)
    {
        var box = WhtCertFormText.FormBoxIndex(cert.TaxFormType);
        // ความกว้างคอลัมน์ตามที่ตาราง auto ของเบราว์เซอร์จัด (วัดจากหน้าพิมพ์ A4)
        float[] colWeights = { 128, 173, 131, 120 };
        container.Row(r =>
        {
            r.ConstantItem(Px(240)).PaddingHorizontal(Px(4)).Column(c =>
            {
                c.Item().Row(rr =>
                {
                    rr.AutoItem().AlignMiddle().PaddingRight(Px(4)).Text(WhtCertFormText.SeqLabel).FontSize(Px(13));
                    // ลำดับในใบแนบ ภ.ง.ด. — ระบบยังคำนวณให้คงที่ไม่ได้ ⇒ ว่าง (.sq min-width:50px; height:17px)
                    rr.AutoItem().AlignMiddle().Width(Px(50)).Height(Px(17)).Border(Px(1)).BorderColor(WhtInk);
                    rr.AutoItem().AlignMiddle().PaddingLeft(Px(4)).Text(WhtCertFormText.SeqInForm).FontSize(Px(13));
                });
                c.Item().PaddingTop(Px(2)).Text(W(WhtCertFormText.SeqHint))
                    .FontSize(Px(10)).Italic().FontColor(WhtHint).LineHeight(1.25f);
            });
            r.RelativeItem().Column(c =>
            {
                c.Item().PaddingVertical(Px(1)).Row(rr =>
                {
                    for (var i = 0; i < 4; i++) DrawCheckOption(rr, box == i, WhtCertFormText.FormBoxes[i], colWeights[i], bold: true, size: 13);
                });
                c.Item().PaddingVertical(Px(1)).Row(rr =>
                {
                    for (var i = 4; i < 7; i++) DrawCheckOption(rr, box == i, WhtCertFormText.FormBoxes[i], colWeights[i - 4], bold: true, size: 13);
                    rr.RelativeItem(colWeights[3]);
                });
            });
        });
    }

    private static void DrawCheckOption(QuestPDF.Fluent.RowDescriptor r, bool on, string label, float weight, bool bold, float size,
        string? tail = null)
    {
        r.RelativeItem(weight).PaddingHorizontal(Px(6)).Row(rr =>
        {
            rr.AutoItem().AlignMiddle().PaddingRight(Px(4)).Element(e => CheckBox(e, on));
            var t = rr.AutoItem().AlignMiddle().Text(label).FontSize(Px(size));
            if (bold) t.Bold();
            if (tail != null)
                rr.AutoItem().AlignMiddle().PaddingLeft(Px(3)).Width(Px(30)).Element(e => Dotted(e, x => x.Text(" ").FontSize(Px(size))));
        });
    }

    // ── ตารางเงินได้ — .IT { font-size:13px } th 12px · ช่อง 1px · padding 1px 4px ─────────────
    private static void BuildIncomeTable(QuestPDF.Fluent.ColumnDescriptor col,
        List<WithholdingTaxCertLine> lines, decimal totalIncome, decimal totalTax)
    {
        var row6Spec = WhtCertFormText.Row6Spec(lines);
        // แถวของแต่ละบรรทัดตัดสินจาก **มาตรา** ผ่านตารางกลางตัวเดียว (`Helpers/ThaiWhtRateTable.CertificateRow`) ไม่ใช่ allow-list
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
        static QuestPDF.Infrastructure.IContainer Cell(QuestPDF.Infrastructure.IContainer c)
            => c.Border(Px(1)).BorderColor(WhtInk).PaddingVertical(Px(1)).PaddingHorizontal(Px(4));

        col.Item().Table(tbl =>
        {
            tbl.ColumnsDefinition(c =>
            {
                c.RelativeColumn(46);   // type description
                c.RelativeColumn(14);   // payment date
                c.RelativeColumn(20);   // income amount
                c.RelativeColumn(20);   // tax withheld
            });
            tbl.Header(h =>
            {
                void Th(string text) => h.Cell().Element(Cell).PaddingVertical(Px(3)).AlignCenter().AlignMiddle().Text(text).FontSize(Px(12)).Bold();
                Th(WhtCertFormText.ColIncomeType);
                Th(WhtCertFormText.ColDate1 + "\n" + WhtCertFormText.ColDate2);
                Th(WhtCertFormText.ColAmount);
                Th(WhtCertFormText.ColTax1 + "\n" + WhtCertFormText.ColTax2);
            });
            // Three numeric cells shared by every row.
            void NumCells((decimal Inc, decimal Tax, DateTime? Date) m)
            {
                tbl.Cell().Element(Cell).AlignCenter().Text(FmtDate(m.Date)).FontSize(Px(12));
                tbl.Cell().Element(Cell).AlignRight().Text(FmtAmt(m.Inc)).FontSize(Px(13));
                tbl.Cell().Element(Cell).AlignRight().Text(FmtAmt(m.Tax)).FontSize(Px(13));
            }
            // .it-type { padding-left:5px; font-size:12px; line-height:1.25 } · .it-s5 { 11px; 1.2 }
            void Row(string label, (decimal Inc, decimal Tax, DateTime? Date) m, float labelPx = 12f, float lh = 1.25f)
            {
                tbl.Cell().Element(Cell).PaddingLeft(Px(1)).Text(W(label)).FontSize(Px(labelPx)).LineHeight(lh);
                NumCells(m);
            }
            Row(WhtCertFormText.Row1, Match("1"));
            Row(WhtCertFormText.Row2, Match("2"));
            Row(WhtCertFormText.Row3, Match("3"));
            Row(WhtCertFormText.Row4a, Match("4a"));
            // .div-indent { padding-left:14px; font-size:11px; line-height:1.3 }
            void DivLine(QuestPDF.Fluent.ColumnDescriptor cell, float indentPx, string text)
                => cell.Item().PaddingLeft(Px(indentPx)).Text(W(text)).FontSize(Px(11)).LineHeight(1.3f);
            // รอบ 204 ทีม D (2f): ผู้ใช้ระบุแถวย่อยปันผล ⇒ ยอดพิมพ์บนแถวย่อยนั้น (ตัวตั้งเดียว WhtCertFormText.DividendBlock — ตัวเดียวกับ HTML/หน้าพิมพ์) ·
            // ไม่ระบุ (null) ⇒ บล็อกเดิมทุกประการ
            var divBlock = WhtCertFormText.DividendBlock(lines);
            if (divBlock != null)
            {
                foreach (var dr in divBlock)
                {
                    tbl.Cell().Element(Cell).PaddingLeft(Px(1)).Column(cell =>
                    {
                        foreach (var dl in dr.Lines)
                            DivLine(cell, (dl.Indent == 0 ? 12 : dl.Indent * 16) + (dl.Continuation ? 12 : 0), dl.Text);
                    });
                    NumCells(dr.HasAmount ? (dr.IncomeAmount, dr.TaxAmount, dr.PaymentDate) : (0m, 0m, (DateTime?)null));
                }
            }
            else
            {
                // แถว 4(ข) เงินปันผล — รายการย่อยครบตามแบบ แบ่งบรรทัดตรงกับหน้าพิมพ์/HTML (คู่ a/b ของ WhtCertFormText)
                tbl.Cell().Element(Cell).PaddingLeft(Px(1)).Column(cell =>
                {
                    cell.Item().PaddingLeft(Px(12)).Text(W(WhtCertFormText.Row4b)).FontSize(Px(12)).LineHeight(1.25f);
                    DivLine(cell, 14, WhtCertFormText.Div1a);
                    DivLine(cell, 14 + 16, WhtCertFormText.Div1b);
                    DivLine(cell, 28, WhtCertFormText.Div11);
                    DivLine(cell, 28, WhtCertFormText.Div12);
                    DivLine(cell, 28, WhtCertFormText.Div13);
                    DivLine(cell, 28, WhtCertFormText.Div14 + " .................. " + WhtCertFormText.Div14Tail);
                    DivLine(cell, 14, WhtCertFormText.Div2);
                    DivLine(cell, 28, WhtCertFormText.Div21);
                    DivLine(cell, 28, WhtCertFormText.Div22a);
                    DivLine(cell, 28 + 24, WhtCertFormText.Div22b);
                    DivLine(cell, 28, WhtCertFormText.Div23a);
                    DivLine(cell, 28 + 24, WhtCertFormText.Div23b);
                    DivLine(cell, 28, WhtCertFormText.Div24);
                    DivLine(cell, 28, WhtCertFormText.Div25 + " ........................................................");
                });
                NumCells(Match("4b"));
            }
            Row(WhtCertFormText.Row5, Match("5"), 11f, 1.2f);
            // แถว 6 อื่น ๆ (ระบุ) — พิมพ์รายการของบรรทัดที่ตกแถวนี้ (แถว 5 ไม่มีช่องระบุบนแบบ) · ไม่มีรายการ = เส้นจุดบรรทัดถัดไป (เหมือนหน้าพิมพ์)
            if (row6Spec != null) Row(WhtCertFormText.Row6 + " " + row6Spec, Match("6"));
            else
            {
                tbl.Cell().Element(Cell).PaddingLeft(Px(1)).Column(cell =>
                {
                    cell.Item().Text(WhtCertFormText.Row6).FontSize(Px(12)).LineHeight(1.25f);
                    cell.Item().Text("........................................................................................").FontSize(Px(12)).LineHeight(1.25f).ClampLines(1);
                });
                NumCells(Match("6"));
            }
            // .total-row { font-weight:700 } td { border-top:2px }
            static QuestPDF.Infrastructure.IContainer TotalCell(QuestPDF.Infrastructure.IContainer c)
                => Cell(c.BorderTop(Px(2)).BorderColor(WhtInk));
            tbl.Cell().ColumnSpan(2).Element(TotalCell).AlignRight().Text(WhtCertFormText.Total).FontSize(Px(13)).Bold();
            tbl.Cell().Element(TotalCell).AlignRight().Text(FmtAmt(totalIncome)).FontSize(Px(13)).Bold();
            tbl.Cell().Element(TotalCell).AlignRight().Text(FmtAmt(totalTax)).FontSize(Px(13)).Bold();
        });
    }

    // .totxt-cell { padding:3px 8px; background:#f0f0f0 } · ค่าในช่องเทา #d9d9d9
    private static void BuildTotalInWords(QuestPDF.Fluent.ColumnDescriptor col, decimal totalTax)
    {
        col.Item().BorderBottom(Px(1.5f)).BorderColor(WhtInk).Background("#F0F0F0")
            .PaddingVertical(Px(3)).PaddingHorizontal(Px(8)).Row(r =>
            {
                r.AutoItem().PaddingRight(Px(6)).AlignMiddle().Text(t =>
                {
                    t.Span(WhtCertFormText.TotalWords + "\u00A0").Bold();
                    t.Span(WhtCertFormText.TotalWordsHint).Italic();
                });
                r.RelativeItem().Background("#D9D9D9").PaddingVertical(Px(1)).PaddingHorizontal(Px(8))
                    .Text(ThaiBahtText.Format(totalTax)).Bold();
            });
    }

    // A9 (2026-10-13): ใบรายปีของพนักงานพิมพ์ยอดประกันสังคม/สำรองเลี้ยงชีพ — ตัวจัดรูปแบบเดียวกับ HTML (WhtCertFormText.FundAmountText) ·
    // ไม่มีค่า (ใบผู้รับเงินทั่วไป · กบข.) = เส้นว่างเดิม · .fund-cell { padding:3px 8px; font-size:10.5px; line-height:1.4; บรรทัดเดียว }
    private static void BuildFundLine(QuestPDF.Fluent.ColumnDescriptor col, WhtCertFundAmounts? funds)
    {
        var size = Px(10.5f);
        col.Item().BorderBottom(Px(1.5f)).BorderColor(WhtInk).PaddingVertical(Px(3)).PaddingHorizontal(Px(8)).Row(r =>
        {
            // ช่องว่างระหว่างคำ = PaddingRight (QuestPDF ตัดช่องว่างท้ายข้อความทิ้ง)
            var gap = Px(3.5f);
            void Txt(string s, bool bold = false)
            {
                var t = r.AutoItem().AlignBottom().PaddingRight(gap).Text(s).FontSize(size).LineHeight(1.4f);
                if (bold) t.Bold();
            }
            // .fund-blank { min-width:46px; border-bottom:1px dotted }
            void Blank(decimal? amount) => r.AutoItem().AlignBottom().PaddingRight(gap).MinWidth(Px(46)).Element(e => Dotted(e, x =>
                x.AlignCenter().Text(WhtCertFormText.FundAmountText(amount) ?? "\u00A0").FontSize(size).LineHeight(1.4f)));
            Txt(WhtCertFormText.FundLead, bold: true);
            Txt(WhtCertFormText.FundGov); Blank(funds?.Gov); Txt(WhtCertFormText.Baht);
            Txt(WhtCertFormText.FundSso); Blank(funds?.Sso); Txt(WhtCertFormText.Baht);
            Txt(WhtCertFormText.FundPvd); Blank(funds?.Pvd); Txt(WhtCertFormText.Baht);
        });
    }

    // .cond-cell { padding:2px 8px; font-size:13px } · หัวข้อ 80px + 4 ช่อง
    private static void BuildConditionsRow(QuestPDF.Fluent.ColumnDescriptor col, WithholdingTaxCert cert)
    {
        // ตัวตัดสินเดียว WhtCertFormText.PayerConditionIndex — รอบ 204: ออกให้ครั้งเดียว (PayOnce) → (3)
        var cond = WhtCertFormText.PayerConditionIndex(cert.CertificateType);
        // ความกว้างคอลัมน์ตามที่ตาราง auto ของเบราว์เซอร์จัด (วัดจากหน้าพิมพ์ A4)
        float[] w = { 157, 186, 188, 216 };
        col.Item().BorderBottom(Px(1.5f)).BorderColor(WhtInk).PaddingVertical(Px(2)).PaddingHorizontal(Px(8)).Row(r =>
        {
            var pc = WhtCertFormText.PayerConditions;
            r.ConstantItem(Px(80)).AlignMiddle().Text(WhtCertFormText.PayerConditionHeading).Bold();
            for (var i = 0; i < 4; i++)
                DrawCheckOption(r, cond == i, pc[i], w[i], bold: false, size: 13, tail: i == 3 ? "" : null);
        });
    }

    // ── คำเตือน (ซ้าย 35%) + ลงชื่อ/ตราประทับ (ขวา 65%) — .bottom-split ───────────────────
    private static void BuildBottomSplit(QuestPDF.Fluent.ColumnDescriptor col, DateTime? issuedDate,
        byte[]? signatureImage = null, string? signerName = null)
    {
        _ = signerName;   // หน้าพิมพ์ไม่พิมพ์ชื่อผู้ลงนาม (มีแต่ลายเซ็น) — คงพารามิเตอร์ไว้ให้ผู้เรียกเดิม
        col.Item().Row(r =>
        {
            // .bottom-left { width:35%; font-size:11px; line-height:1.4; padding:3px 8px }
            r.RelativeItem(35).BorderRight(Px(1.5f)).BorderColor(WhtInk).PaddingVertical(Px(3)).PaddingHorizontal(Px(8)).Row(w =>
            {
                w.AutoItem().PaddingRight(Px(6)).Text(WhtCertFormText.WarningHeading).Bold().FontSize(Px(11)).LineHeight(1.4f);
                w.RelativeItem().Text(W(WhtCertFormText.WarningText)).FontSize(Px(11)).LineHeight(1.4f);
            });
            // .bottom-right { width:65%; font-size:12px; line-height:1.4; padding:3px 84px 3px 8px } · ตราวงกลม 70px ชิดขวา 6px กลางแนวตั้ง
            r.RelativeItem(65).PaddingVertical(Px(3)).PaddingLeft(Px(8)).Row(rr =>
            {
                rr.RelativeItem().Column(c =>
                {
                    c.Item().PaddingBottom(Px(6)).Text(WhtCertFormText.Certify).FontSize(Px(11.5f)).LineHeight(1.4f).ClampLines(1);
                    c.Item().PaddingTop(Px(3)).PaddingHorizontal(Px(12)).Column(sig =>
                    {
                        // .sig-line { text-align:right } · .sig-dots { min-width:200px; height:34px } ลายเซ็นกลางเส้น
                        sig.Item().PaddingBottom(Px(4)).AlignRight().Row(line =>
                        {
                            line.AutoItem().AlignBottom().PaddingRight(Px(3)).Text(WhtCertFormText.SignLead).FontSize(Px(12)).LineHeight(1.4f);
                            line.AutoItem().Width(Px(200)).Height(Px(34)).Element(e => Dotted(e, x =>
                            {
                                if (signatureImage == null) return;
                                x.AlignBottom().AlignCenter().PaddingBottom(Px(1)).MaxHeight(Px(32)).MaxWidth(Px(190)).Image(signatureImage).FitArea();
                            }));
                            line.AutoItem().AlignBottom().PaddingLeft(Px(3)).Text(WhtCertFormText.SignRole).FontSize(Px(12)).LineHeight(1.4f);
                        });
                        // วันที่ออก 3 ช่อง วัน / เดือน / ปี (พ.ศ.) — .sig-date { font-size:13px } .sig-dots-sm { min-width:36px }
                        sig.Item().PaddingTop(Px(3)).AlignCenter().Row(dr =>
                        {
                            void Part(string? v) => dr.AutoItem().MinWidth(Px(36)).Element(e => Dotted(e, x =>
                                x.PaddingHorizontal(Px(4)).AlignCenter().Text(v ?? " ").FontSize(Px(13))));
                            Part(issuedDate?.Day.ToString());
                            dr.AutoItem().PaddingHorizontal(Px(3.5f)).Text("/").FontSize(Px(13));
                            Part(issuedDate?.Month.ToString());
                            dr.AutoItem().PaddingHorizontal(Px(3.5f)).Text("/").FontSize(Px(13));
                            Part(issuedDate.HasValue ? (issuedDate.Value.Year + 543).ToString() : null);
                        });
                        sig.Item().AlignCenter().Text(WhtCertFormText.SignDateHint).FontSize(Px(11)).FontColor(WhtHint).LineHeight(1.4f);
                    });
                });
                rr.ConstantItem(Px(84)).AlignMiddle().AlignRight().PaddingRight(Px(6)).Element(DrawStampCircle);
            });
        });
    }

    // ── "ประทับตรานิติบุคคล (ถ้ามี)" — .stamp-area วงกลม 70px เส้น 1.2px ตัวเอียง 10px ─────────
    private static void DrawStampCircle(QuestPDF.Infrastructure.IContainer e)
    {
        var d = Px(70);
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' width='{F(d)}' height='{F(d)}' viewBox='0 0 70 70'>"
                + "<circle cx='35' cy='35' r='34.4' fill='none' stroke='#000' stroke-width='1.2'/></svg>";
        e.Width(d).Height(d).Layers(l =>
        {
            l.Layer().Svg(svg);
            l.PrimaryLayer().AlignCenter().AlignMiddle()
                .Text(string.Join("\n", WhtCertFormText.StampLines))
                .FontSize(Px(10)).LineHeight(1.3f).Italic().FontColor("#222222").AlignCenter();
        });
    }

    // .footnote { font-size:9.5px; line-height:1.25; margin-top:1px; padding:0 2px }
    private static void BuildFootnote(QuestPDF.Fluent.ColumnDescriptor col)
    {
        // ข้อ 1 อยู่บรรทัดเดียวกับ "หมายถึง" · ข้อ 2–3 ตรงแนวข้อ 1 ตามแบบทางการ
        col.Item().PaddingTop(Px(1)).PaddingHorizontal(Px(2)).Row(r =>
        {
            r.AutoItem().PaddingRight(Px(4)).Text(t =>
            {
                t.DefaultTextStyle(x => x.FontSize(Px(9.5f)).LineHeight(1.25f));
                t.Span(WhtCertFormText.FootnoteHeading).Bold();
                t.Span("  " + WhtCertFormText.FootnoteLead);
                t.Span("*").FontColor("#990000");
                t.Span(" " + WhtCertFormText.FootnoteLeadTail);
            });
            r.RelativeItem().Text(string.Join("\n", WhtCertFormText.FootnoteItems.Select(W))).FontSize(Px(9.5f)).LineHeight(1.25f);
        });
    }
}
