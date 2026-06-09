using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Accounting.Services.Implementations;

// QuestPDF.Fluent exposes a `Document` type that clashes with our entity, so
// we explicitly alias the entity types. The other QuestPDF helpers keep their
// short names for fluent API readability.
using EntDoc = Accounting.Models.Entities.Document;
using EntCompany = Accounting.Models.Entities.Company;
using EntSettings = Accounting.Models.Entities.CompanySettings;
using EntTemplate = Accounting.Models.Entities.DocumentTemplate;
using EntLine = Accounting.Models.Entities.DocumentLine;

/// <summary>
/// QuestPDF renderer that composes the document directly from entities,
/// honouring the template's LayoutStyle (Classic / BannerHeader / Letterhead
/// / ModernLeft / etc.) — so the downloaded PDF matches the configured
/// layout, not just colours. Replaces the lossy HTML→ParseBlocks→QuestPDF
/// path for the GenerateDocumentPdfAsync flow.
///
/// Each layout differs in HEADER composition + TITLE placement + chrome;
/// table / summary / signatures / footer are shared so adding a new layout
/// is a single header method.
/// </summary>
public partial class PdfGenerationService
{
    internal byte[] RenderDocumentPdfNative(EntDoc doc, EntCompany company,
        EntSettings? settings, EntTemplate template, string? watermarkOverride, string? langOverride,
        IReadOnlyList<DocumentSigner>? signers = null, GlPostingSummary? gl = null)
    {
        EnsureThaiFontsRegistered();
        var lang = langOverride ?? template.Language ?? "th";
        var b = BuildBranding(template, settings, watermarkOverride);

        var accent = b.AccentColor ?? "#1F2937";
        var primary = b.PrimaryColor ?? "#1F2937";
        var headerBg = b.TableHeaderBg ?? "#1E40AF";
        var headerText = b.TableHeaderText ?? "#FFFFFF";
        var stripe = SanitizeHex(template.TableStripedColor) ?? "#F8FAFC";
        var layout = (template.LayoutStyle ?? "Classic").Trim();
        var fontChain = GetFontFamilyChain(b.FontFamily);
        var titleText = template.CustomTitle ?? GetDocumentTitle(doc.DocumentType, lang);

        try
        {
            var pdf = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    // QuestPDF 2024 accepts a single Margin call; use the average
                    // of the template's per-side margins so the result is close to
                    // the configured page without relying on side-specific APIs
                    // that may differ across versions.
                    var avgMargin = (float)((template.MarginTop + template.MarginBottom + template.MarginLeft + template.MarginRight) / 4m);
                    if (avgMargin < 8) avgMargin = 15f;     // sensible floor
                    page.Margin(avgMargin, Unit.Millimetre);
                    page.PageColor(Colors.White);
                    var bodyFont = float.TryParse(template.BodyFontSize, out var bf) ? bf : 10f;
                    page.DefaultTextStyle(t => t.FontFamily(fontChain).FontSize(bodyFont).FontColor(primary));

                    // Watermark behind everything.
                    if (!string.IsNullOrWhiteSpace(b.WatermarkText))
                    {
                        try
                        {
                            page.Background().AlignCenter().AlignMiddle()
                                .Text(b.WatermarkText).FontSize(72).FontColor(Colors.Grey.Lighten3).Bold();
                        }
                        catch { }
                    }

                    page.Content().Column(col =>
                    {
                        ComposeHeaderAndTitle(col, layout, doc, company, template, b, accent, headerBg, headerText, titleText);
                        ComposeContact(col, doc, template, accent);
                        ComposeItemsTable(col, doc, template, headerBg, headerText, stripe, layout, accent);
                        ComposeSummary(col, doc, template, accent, layout);
                        ComposeFooter(col, doc, template);
                        ComposeSignatures(col, template, b, signers);
                        ComposeGlPosting(col, gl, lang);
                    });

                    page.Footer().AlignRight().Text(t =>
                    {
                        t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                        t.Span(" / ").FontSize(8).FontColor(Colors.Grey.Medium);
                        t.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                    });
                });
            });
            return pdf.GeneratePdf();
        }
        catch
        {
            // Outermost safety net: fall back to the old HTML-parser path so a
            // composition bug can never break PDF generation entirely.
            var html = BuildDocumentHtml(doc, company, settings, template, watermarkOverride, langOverride);
            return ConvertHtmlToPdf(html, template, b);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Header / Title composition — switches on layout style
    // ─────────────────────────────────────────────────────────────────
    private static void ComposeHeaderAndTitle(ColumnDescriptor col, string layout,
        EntDoc doc, EntCompany company, EntTemplate template, PdfBranding b,
        string accent, string headerBg, string headerText, string titleText)
    {
        var titleFontSize = float.TryParse(template.TitleFontSize, out var tf) ? tf : 22f;

        switch (layout)
        {
            case "BannerHeader":
                // Full-width accent band holds ONLY the logo + company info.
                // The document title sits BELOW the band on white (accent
                // colour) so it never competes with the coloured header and a
                // long Thai label gets the full page width — matches the
                // on-screen view.
                col.Item().Background(accent).Padding(14).Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(b.LogoHeightMm + 10, Unit.Millimetre).Image(b.LogoBytes); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, headerText));
                });
                col.Item().PaddingTop(14).AlignCenter()
                    .Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                col.Item().PaddingTop(4).PaddingBottom(2).LineHorizontal(1).LineColor(accent);
                col.Item().PaddingTop(8).AlignCenter().Row(r => RenderDocInfoSpans(r, doc, template, accent));
                break;

            case "BoldHeader":
                // Prominent title on a colored bar at the very top (uses the
                // configured size — the BoldHeader preset already seeds a
                // larger value so it reads bigger than the other layouts).
                col.Item().Background(accent).Padding(14)
                    .Text(titleText).FontSize(titleFontSize).Bold().FontColor(headerText);
                col.Item().PaddingTop(10).Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(b.LogoHeightMm + 8, Unit.Millimetre).Image(b.LogoBytes); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, "#222"));
                });
                col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                ComposeDocInfo(col, doc, template, accent, alignRight: false);
                break;

            case "Letterhead":
                // Centered corporate letterhead with thick accent rule.
                col.Item().AlignCenter().Column(c =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { c.Item().AlignCenter().Height(b.LogoHeightMm, Unit.Millimetre).Image(b.LogoBytes); } catch { }
                    RenderCompanyLines(c, company, template, "#222", center: true);
                });
                col.Item().PaddingVertical(4).LineHorizontal(2.5f).LineColor(accent);
                col.Item().PaddingTop(8).Text(titleText)
                    .FontSize(titleFontSize).Bold().FontColor(accent);
                ComposeDocInfo(col, doc, template, accent, alignRight: false);
                break;

            case "CenteredFormal":
                col.Item().AlignCenter().Column(c =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { c.Item().AlignCenter().Height(b.LogoHeightMm, Unit.Millimetre).Image(b.LogoBytes); } catch { }
                    RenderCompanyLines(c, company, template, "#222", center: true);
                });
                col.Item().PaddingTop(10).AlignCenter().BorderTop(2.5f).BorderBottom(2.5f).BorderColor(accent)
                    .PaddingVertical(6)
                    .Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                col.Item().PaddingTop(6).AlignCenter().Row(r => RenderDocInfoSpans(r, doc, template, accent));
                break;

            case "ModernLeft":
                // Header below, title with bold left accent stripe.
                col.Item().PaddingBottom(8).BorderBottom(2).BorderColor(accent).Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(b.LogoHeightMm + 10, Unit.Millimetre).Image(b.LogoBytes); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, "#222"));
                });
                col.Item().PaddingTop(12).BorderLeft(6).BorderColor(accent).PaddingLeft(10)
                    .Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                ComposeDocInfo(col, doc, template, accent, alignRight: false);
                break;

            case "SidebarAccent":
                col.Item().Background(accent).Padding(14).Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(b.LogoHeightMm + 8, Unit.Millimetre).Image(b.LogoBytes); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, headerText));
                });
                col.Item().PaddingTop(10).Background("#F1F5F9").BorderLeft(6).BorderColor(accent)
                    .PaddingVertical(8).PaddingHorizontal(12)
                    .Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                ComposeDocInfo(col, doc, template, accent, alignRight: false);
                break;

            case "SplitHeader":
                col.Item().PaddingBottom(8).BorderBottom(3).BorderColor(accent).Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(b.LogoHeightMm + 10, Unit.Millimetre).Image(b.LogoBytes); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, "#222"));
                });
                col.Item().PaddingTop(10).Row(r =>
                {
                    r.RelativeItem().Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                    r.ConstantItem(180).Background("#F8FAFC").BorderLeft(4).BorderColor(accent)
                        .PaddingVertical(8).PaddingHorizontal(10).Column(c =>
                        {
                            if (template.ShowDocumentNumber) c.Item().Text($"เลขที่: {doc.DocumentNumber}").FontSize(10);
                            if (template.ShowDocumentDate) c.Item().Text($"วันที่: {doc.DocumentDate:dd/MM/yyyy}").FontSize(10);
                            if (template.ShowDueDate && doc.DueDate.HasValue)
                                c.Item().Text($"ครบกำหนด: {doc.DueDate:dd/MM/yyyy}").FontSize(10);
                            if (template.ShowReference && !string.IsNullOrWhiteSpace(doc.Reference))
                                c.Item().Text($"อ้างอิง: {doc.Reference}").FontSize(10);
                        });
                });
                break;

            case "Compact":
            case "Minimal":
                col.Item().Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(b.LogoHeightMm + 8, Unit.Millimetre).Image(b.LogoBytes); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, "#222"));
                });
                col.Item().PaddingTop(layout == "Compact" ? 6 : 14)
                    .Text(titleText).FontSize(titleFontSize)
                    .Bold().FontColor(layout == "Minimal" ? "#111" : accent);
                if (layout == "Minimal")
                    col.Item().PaddingTop(2).LineHorizontal(1).LineColor("#111");
                ComposeDocInfo(col, doc, template, accent, alignRight: false);
                break;

            default: // Classic
                col.Item().Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(b.LogoHeightMm + 10, Unit.Millimetre).Image(b.LogoBytes); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, "#222"));
                });
                col.Item().PaddingTop(12).AlignCenter().Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                col.Item().PaddingBottom(4).BorderBottom(2).BorderColor(accent);
                ComposeDocInfo(col, doc, template, accent, alignRight: true);
                break;
        }
    }

    private static void RenderCompanyLines(ColumnDescriptor c, EntCompany co, EntTemplate t, string color, bool center = false)
    {
        void Line(string s, int size = 10, bool bold = false)
        {
            var item = c.Item();
            if (center) item = item.AlignCenter();
            var text = item.Text(s).FontSize(size).FontColor(color);
            if (bold) text.Bold();
        }
        if (t.ShowCompanyName) Line(co.Name ?? "", 14, true);
        if (t.ShowCompanyNameEn && !string.IsNullOrWhiteSpace(co.NameEn)) Line(co.NameEn!, 11);
        if (t.ShowCompanyAddress)
        {
            var addr = FormatThaiAddress(co.Address, co.BuildingNumber, co.Moo, co.StreetName,
                co.SubDistrict, co.District, co.Province, co.PostalCode);
            if (!string.IsNullOrWhiteSpace(addr)) Line(addr);
        }
        if (t.ShowCompanyTaxId && !string.IsNullOrWhiteSpace(co.TaxId))
            Line($"เลขประจำตัวผู้เสียภาษี: {co.TaxId}");
        if (t.ShowCompanyPhone && !string.IsNullOrWhiteSpace(co.Phone))
            Line($"โทร: {co.Phone}");
        if (t.ShowCompanyEmail && !string.IsNullOrWhiteSpace(co.Email))
            Line($"Email: {co.Email}");
    }

    private static void ComposeDocInfo(ColumnDescriptor col, EntDoc doc, EntTemplate t, string accent, bool alignRight)
    {
        col.Item().PaddingTop(10).Row(r =>
        {
            if (alignRight) r.RelativeItem();   // pusher
            r.AutoItem().Row(rr => RenderDocInfoSpans(rr, doc, t, accent));
        });
    }

    private static void RenderDocInfoSpans(RowDescriptor r, EntDoc doc, EntTemplate t, string accent)
    {
        void Span(string s) => r.AutoItem().PaddingHorizontal(10).Text(s).FontSize(10);
        if (t.ShowDocumentNumber) Span($"เลขที่: {doc.DocumentNumber}");
        if (t.ShowDocumentDate) Span($"วันที่: {doc.DocumentDate:dd/MM/yyyy}");
        if (t.ShowDueDate && doc.DueDate.HasValue) Span($"ครบกำหนด: {doc.DueDate:dd/MM/yyyy}");
        if (t.ShowReference && !string.IsNullOrWhiteSpace(doc.Reference)) Span($"อ้างอิง: {doc.Reference}");
    }

    // ─────────────────────────────────────────────────────────────────
    //  Shared section composers (contact / table / summary / etc.)
    // ─────────────────────────────────────────────────────────────────
    private static void ComposeContact(ColumnDescriptor col, EntDoc doc, EntTemplate t, string accent)
    {
        var c = doc.Contact;
        if (c == null) return;
        col.Item().PaddingTop(12).Border(1).BorderColor("#E5E7EB").Padding(10).Column(cc =>
        {
            cc.Item().Text(t.ContactSectionTitle ?? "ผู้ติดต่อ").FontSize(11).Bold().FontColor(accent);
            cc.Item().Text(c.Name ?? "").FontSize(13).Bold();
            if (t.ShowContactTaxId && !string.IsNullOrWhiteSpace(c.TaxId))
                cc.Item().Text($"เลขผู้เสียภาษี: {c.TaxId}").FontSize(10);
            if (t.ShowContactAddress)
            {
                var addr = FormatThaiAddress(c.Address, c.BuildingNumber, c.Moo, c.StreetName,
                    c.SubDistrict, c.District, c.Province, c.PostalCode);
                if (!string.IsNullOrWhiteSpace(addr)) cc.Item().Text(addr).FontSize(10);
            }
            if (t.ShowContactPhone && !string.IsNullOrWhiteSpace(c.Phone))
                cc.Item().Text($"โทร: {c.Phone}").FontSize(10);
            if (t.ShowContactEmail && !string.IsNullOrWhiteSpace(c.Email))
                cc.Item().Text($"Email: {c.Email}").FontSize(10);
        });
    }

    private static void ComposeItemsTable(ColumnDescriptor col, EntDoc doc, EntTemplate t,
        string headerBg, string headerText, string stripe, string layout = "Classic", string accent = "#1F2937")
    {
        // Minimal & Letterhead use a borderless header — no fill, accent-coloured
        // text and a single rule underneath — to match their on-screen look.
        var flatHeader = layout is "Minimal" or "Letterhead";

        col.Item().PaddingTop(12).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                if (t.ShowLineNumber) cols.ConstantColumn(28);
                cols.RelativeColumn(4);
                cols.ConstantColumn(50);
                if (t.ShowUnit) cols.ConstantColumn(45);
                cols.ConstantColumn(70);
                if (t.ShowDiscount) cols.ConstantColumn(60);
                cols.ConstantColumn(80);
            });

            table.Header(h =>
            {
                void Th(string text, string align = "left")
                {
                    var cell = flatHeader
                        ? h.Cell().BorderBottom(2).BorderColor(accent).Padding(6)
                        : h.Cell().Background(headerBg).Padding(6);
                    var tx = cell.Text(text).FontSize(10).Bold().FontColor(flatHeader ? accent : headerText);
                    if (align == "right") tx.AlignRight();
                    else if (align == "center") tx.AlignCenter();
                }
                if (t.ShowLineNumber) Th("#", "center");
                Th("รายการ");
                Th("จำนวน", "right");
                if (t.ShowUnit) Th("หน่วย", "center");
                Th("ราคา/หน่วย", "right");
                if (t.ShowDiscount) Th("ส่วนลด", "right");
                Th("จำนวนเงิน", "right");
            });

            int idx = 1;
            foreach (var line in doc.Lines.OrderBy(l => l.LineOrder))
            {
                var bg = idx % 2 == 0 ? stripe : "#FFFFFF";
                void Td(string text, string align = "left")
                {
                    var cell = table.Cell().Background(bg).BorderBottom(0.3f).BorderColor("#E5E7EB").Padding(5);
                    var tx = cell.Text(text).FontSize(10);
                    if (align == "right") tx.AlignRight();
                    else if (align == "center") tx.AlignCenter();
                }
                if (t.ShowLineNumber) Td(idx.ToString(), "center");
                Td(line.Description ?? "");
                Td(line.Quantity.ToString("N2"), "right");
                if (t.ShowUnit) Td(line.Unit ?? "", "center");
                Td(line.UnitPrice.ToString("N2"), "right");
                if (t.ShowDiscount) Td(line.DiscountAmount.ToString("N2"), "right");
                Td(line.Amount.ToString("N2"), "right");
                idx++;
            }
        });
    }

    private static void ComposeSummary(ColumnDescriptor col, EntDoc doc, EntTemplate t, string accent, string layout)
    {
        col.Item().PaddingTop(8).AlignRight().Width(260).Column(sc =>
        {
            void Row(string label, string value, bool total = false)
            {
                var item = sc.Item().PaddingVertical(3);
                if (total)
                {
                    // SidebarAccent layout: filled bar for the grand total.
                    if (layout == "SidebarAccent")
                        item = item.Background(accent).Padding(6);
                    else
                        item = item.BorderTop(2).BorderBottom(2).BorderColor(accent).PaddingVertical(5);
                }
                else item = item.BorderBottom(0.5f).BorderColor("#EEE");
                item.Row(r =>
                {
                    var lblTxt = r.RelativeItem().Text(label).FontSize(total ? 12 : 10);
                    if (total) { lblTxt.Bold().FontColor(layout == "SidebarAccent" ? "#FFF" : accent); }
                    var valTxt = r.ConstantItem(110).AlignRight().Text(value).FontSize(total ? 12 : 10);
                    if (total) { valTxt.Bold().FontColor(layout == "SidebarAccent" ? "#FFF" : accent); }
                });
            }
            if (t.ShowSubTotal) Row("ยอดรวมก่อน VAT", doc.SubTotal.ToString("N2"));
            if (t.ShowDiscountTotal && doc.DiscountAmount > 0)
                Row("ส่วนลดรวม", doc.DiscountAmount.ToString("N2"));
            if (t.ShowVatSummary && doc.VatAmount > 0)
                Row("ภาษีมูลค่าเพิ่ม 7%", doc.VatAmount.ToString("N2"));
            if (t.ShowWithholdingTaxSummary && doc.WithholdingTaxAmount > 0)
                Row("ภาษีหัก ณ ที่จ่าย", $"({doc.WithholdingTaxAmount:N2})");
            Row("ยอดรวมสุทธิ", doc.TotalAmount.ToString("N2"), total: true);
        });

        if (t.ShowAmountInWords)
        {
            var words = t.AmountInWordsLanguage == "en"
                ? ConvertToEnglishWords(doc.TotalAmount)
                : ConvertToThaiWords(doc.TotalAmount);
            col.Item().PaddingTop(8).AlignCenter().Text($"({words})").FontSize(10).Italic().FontColor("#444");
        }
    }

    private static void ComposeFooter(ColumnDescriptor col, EntDoc doc, EntTemplate t)
    {
        if (t.ShowBankDetails && !string.IsNullOrWhiteSpace(t.BankDetailsText))
            col.Item().PaddingTop(12).Background("#F8F9FA").Padding(10)
                .Text($"ข้อมูลชำระเงิน: {t.BankDetailsText}").FontSize(10);
        var footerNotes = !string.IsNullOrWhiteSpace(doc.CustomFooterNotes) ? doc.CustomFooterNotes : t.FooterNotes;
        if (!string.IsNullOrWhiteSpace(footerNotes))
            col.Item().PaddingTop(8).Text(footerNotes).FontSize(10).FontColor("#555");
    }

    private static void ComposeSignatures(ColumnDescriptor col, EntTemplate t, PdfBranding b,
        IReadOnlyList<DocumentSigner>? signers)
    {
        if (!t.ShowSignature) return;
        var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(t.SignatureLabel1)) labels.Add(t.SignatureLabel1!);
        if (t.SignatureCount >= 2 && !string.IsNullOrWhiteSpace(t.SignatureLabel2)) labels.Add(t.SignatureLabel2!);
        if (t.SignatureCount >= 3 && !string.IsNullOrWhiteSpace(t.SignatureLabel3)) labels.Add(t.SignatureLabel3!);
        if (labels.Count == 0) return;

        DocumentSigner? signerAt(int i) => signers != null && i < signers.Count ? signers[i] : null;

        // Stamp above signatures (optional, defensive).
        if (b.StampBytes is { Length: > 0 })
            try { col.Item().PaddingTop(20).AlignRight().Height(22, Unit.Millimetre).Image(b.StampBytes); } catch { }

        col.Item().PaddingTop(b.StampBytes != null ? 6 : 40).Row(r =>
        {
            for (int i = 0; i < labels.Count; i++)
            {
                var label = labels[i];
                var s = signerAt(i);
                r.RelativeItem().PaddingHorizontal(8).Column(c =>
                {
                    // Signature image, then rule, then role + name + title.
                    // Image rendered at fixed height so a tall signature can't
                    // throw the column off; defensive try/catch on bad bytes.
                    if (s?.SignatureImageBytes is { Length: > 0 })
                    {
                        try { c.Item().AlignCenter().Height(14, Unit.Millimetre).Image(s.SignatureImageBytes); }
                        catch { c.Item().PaddingTop(16); }
                    }
                    else
                    {
                        c.Item().PaddingTop(20);
                    }
                    c.Item().LineHorizontal(0.5f).LineColor("#333");
                    c.Item().PaddingTop(4).AlignCenter().Text(label).FontSize(10).FontColor("#555");
                    if (s != null && !string.IsNullOrWhiteSpace(s.Name))
                        c.Item().AlignCenter().Text(s.Name!).FontSize(10).Bold();
                    if (s != null && !string.IsNullOrWhiteSpace(s.Title))
                        c.Item().AlignCenter().Text(s.Title!).FontSize(9).FontColor("#666");
                });
            }
        });
    }

    /// <summary>การลงบัญชี (Dr./Cr.) summary at the foot of the document — for
    /// internal audit. Rendered only when the company enabled it AND the doc
    /// has a posted journal entry.</summary>
    private static void ComposeGlPosting(ColumnDescriptor col, GlPostingSummary? gl, string lang)
    {
        if (gl == null || gl.Lines.Count == 0) return;
        var en = lang == "en";
        col.Item().PaddingTop(18).BorderTop(0.8f).BorderColor("#94A3B8").PaddingTop(6).Column(c =>
        {
            c.Item().Text($"{(en ? "Accounting entry (for internal audit)" : "การบันทึกบัญชี (สำหรับตรวจสอบภายใน)")} — {gl.EntryNumber} · {gl.EntryDate:dd/MM/yyyy}")
                .FontSize(9).Bold().FontColor("#475569");
            c.Item().PaddingTop(3).Table(tbl =>
            {
                tbl.ColumnsDefinition(cd => { cd.RelativeColumn(60); cd.RelativeColumn(20); cd.RelativeColumn(20); });
                tbl.Header(h =>
                {
                    h.Cell().Background("#F1F5F9").Border(0.5f).BorderColor("#CBD5E1").Padding(3).Text(en ? "Account" : "บัญชี").FontSize(9).Bold();
                    h.Cell().Background("#F1F5F9").Border(0.5f).BorderColor("#CBD5E1").Padding(3).AlignRight().Text(en ? "Debit" : "เดบิต").FontSize(9).Bold();
                    h.Cell().Background("#F1F5F9").Border(0.5f).BorderColor("#CBD5E1").Padding(3).AlignRight().Text(en ? "Credit" : "เครดิต").FontSize(9).Bold();
                });
                foreach (var l in gl.Lines)
                {
                    var name = string.IsNullOrWhiteSpace(l.AccountCode) ? l.AccountName : $"{l.AccountCode} - {l.AccountName}";
                    bool creditOnly = l.Credit != 0 && l.Debit == 0;
                    tbl.Cell().Border(0.5f).BorderColor("#CBD5E1").PaddingVertical(3)
                        .PaddingLeft(creditOnly ? 18 : 6).PaddingRight(6).Text(name).FontSize(9);
                    tbl.Cell().Border(0.5f).BorderColor("#CBD5E1").Padding(3).AlignRight().Text(l.Debit != 0 ? l.Debit.ToString("N2") : "").FontSize(9);
                    tbl.Cell().Border(0.5f).BorderColor("#CBD5E1").Padding(3).AlignRight().Text(l.Credit != 0 ? l.Credit.ToString("N2") : "").FontSize(9);
                }
                tbl.Cell().Background("#F8FAFC").Border(0.5f).BorderColor("#CBD5E1").Padding(3).AlignRight().Text(en ? "Total" : "รวม").FontSize(9).Bold();
                tbl.Cell().Background("#F8FAFC").Border(0.5f).BorderColor("#CBD5E1").Padding(3).AlignRight().Text(gl.TotalDebit.ToString("N2")).FontSize(9).Bold();
                tbl.Cell().Background("#F8FAFC").Border(0.5f).BorderColor("#CBD5E1").Padding(3).AlignRight().Text(gl.TotalCredit.ToString("N2")).FontSize(9).Bold();
            });
        });
    }
}
