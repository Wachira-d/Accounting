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
        IReadOnlyList<DocumentSigner>? signers = null, GlPostingSummary? gl = null,
        bool pdfA = false, string? pdfTitle = null, string? pdfAuthor = null)
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

                    // เอกสารที่ถูกยกเลิก (Voided) — ประทับลายน้ำ "ยกเลิก"
                    // สีแดงเด่นชัด priority สูงสุด (ทับ watermark ปกติ).
                    // ยังพิมพ์เนื้อหาได้เหมือนเดิม เก็บเป็นหลักฐาน/audit.
                    if (doc.Status == Models.Enums.DocumentStatus.Voided)
                    {
                        try
                        {
                            page.Background().AlignCenter().AlignMiddle()
                                .Text("ยกเลิก").FontSize(96).FontColor("#33DC2626").Bold();
                        }
                        catch { }
                    }
                    // Watermark behind everything. ค่า opacity (0..1) จาก
                    // template.WatermarkOpacity แปลงเป็น hex 8-digit ARGB
                    // (เดิม hard-code Colors.Grey.Lighten3) — HTML CSS
                    // BuildCss line 1160 ก็เคารพค่านี้.
                    else if (!string.IsNullOrWhiteSpace(b.WatermarkText))
                    {
                        try
                        {
                            var op = (double)Math.Clamp(template.WatermarkOpacity, 0.05m, 0.6m);
                            var aa = ((int)Math.Round(op * 255)).ToString("X2");
                            page.Background().AlignCenter().AlignMiddle()
                                .Text(b.WatermarkText).FontSize(72).FontColor($"#{aa}000000").Bold();
                        }
                        catch { }
                    }

                    page.Content().Column(col =>
                    {
                        // Defense-in-depth: ห่อแต่ละ section ด้วย try/catch
                        // ถ้าตัวใดตัวหนึ่ง throw (เช่น QuestPDF API mismatch
                        // ใน edge case) section นั้นจะหายเฉย ๆ — แต่ section
                        // อื่นจะ render ต่อ ไม่ทำให้ทั้ง PDF ตกไป fallback
                        // ConvertHtmlToPdf (HTML→Blocks ที่หน้าตาเรียบเกินไป).
                        void Safe(Action a) { try { a(); } catch { /* skip failed section */ } }
                        Safe(() => ComposeHeaderAndTitle(col, layout, doc, company, template, b, accent, headerBg, headerText, titleText));
                        Safe(() => ComposeContact(col, doc, template, accent));
                        Safe(() => ComposeItemsTable(col, doc, template, headerBg, headerText, stripe, layout, accent));
                        Safe(() => ComposeSummary(col, doc, template, accent, layout));
                        Safe(() => ComposeFooter(col, doc, template, accent));
                        Safe(() => ComposeSignatures(col, template, b, signers));
                        Safe(() => ComposeGlPosting(col, gl, lang));
                    });

                    page.Footer().AlignRight().Text(t =>
                    {
                        t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                        t.Span(" / ").FontSize(8).FontColor(Colors.Grey.Medium);
                        t.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                    });
                });
            });
            // PDF/A-3 conformance (สำหรับ e-Tax ที่ฝัง XML) — ใช้ layout
            // เดียวกับ preview (template-styled สีส้ม) แต่ mark PdfA + ใส่
            // metadata. ETDA ไม่บังคับ layout — แค่ต้อง embed XML +
            // PDF/A metadata. ผลคือ download e-Tax = หน้าตาเหมือน preview.
            if (pdfA)
            {
                pdf.WithMetadata(new QuestPDF.Infrastructure.DocumentMetadata
                {
                    Title = pdfTitle ?? $"{GetDocumentTitle(doc.DocumentType, lang)} {doc.DocumentNumber}",
                    Author = pdfAuthor ?? company.Name,
                    Subject = "e-Tax Invoice (PDF/A-3)",
                    Keywords = "e-Tax, ETDA, Thailand, PDF/A-3",
                    Producer = "NextAcc e-Tax PDF/A-3 Generator (QuestPDF)",
                    Creator = "NextAcc",
                    CreationDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow
                });
                pdf.WithSettings(new QuestPDF.Infrastructure.DocumentSettings { PdfA = true });
            }
            return pdf.GeneratePdf();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"RenderDocumentPdfNative throw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            // Render minimal PDF ที่แสดง exception message — ให้ผู้ใช้/dev
            // เห็น error จริงๆ (แทนที่จะ fallback ลง HTML→Blocks ที่หน้าตา
            // เรียบและกินทุกอย่างไป). ถ้าตัว diagnostic นี้ก็ throw ต่อ —
            // ค่อย fallback ลง HTML→Blocks เป็น last resort.
            try
            {
                return QuestPDF.Fluent.Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        page.Size(PageSizes.A4);
                        page.Margin(20, Unit.Millimetre);
                        page.PageColor(Colors.White);
                        page.DefaultTextStyle(t => t.FontFamily(fontChain).FontSize(10));
                        page.Content().Column(col =>
                        {
                            col.Item().Text("PDF Render Error (native QuestPDF path)")
                                .FontSize(16).Bold().FontColor(Colors.Red.Darken1);
                            col.Item().PaddingTop(8).Text($"Type: {ex.GetType().FullName}")
                                .FontSize(10).FontColor(Colors.Black);
                            col.Item().PaddingTop(4).Text($"Message: {ex.Message}")
                                .FontSize(10).FontColor(Colors.Black);
                            col.Item().PaddingTop(10).Text("Stack Trace:")
                                .FontSize(10).Bold().FontColor(Colors.Black);
                            col.Item().PaddingTop(4).Text(ex.StackTrace ?? "(no stack)")
                                .FontSize(8).FontColor(Colors.Grey.Darken2);
                        });
                    });
                }).GeneratePdf();
            }
            catch
            {
                var html = BuildDocumentHtml(doc, company, settings, template, watermarkOverride, langOverride);
                return ConvertHtmlToPdf(html, template, b);
            }
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
                        try { r.ConstantItem(b.LogoHeightMm + 10, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
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
                        try { r.ConstantItem(b.LogoHeightMm + 8, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
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
                        try { c.Item().AlignCenter().Height(b.LogoHeightMm, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
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
                        try { c.Item().AlignCenter().Height(b.LogoHeightMm, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
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
                        try { r.ConstantItem(b.LogoHeightMm + 10, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
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
                        try { r.ConstantItem(b.LogoHeightMm + 8, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
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
                        try { r.ConstantItem(b.LogoHeightMm + 10, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
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
                        try { r.ConstantItem(b.LogoHeightMm + 8, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
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
                // Header: logo + company info ซ้าย / title + doc number ขวา
                // (เลียนแบบ HTML view ที่ผู้ใช้เห็นจาก "กดดู" — title +
                // เลขที่ติดกัน เด่นชัด เป็นกลุ่มเดียว ไม่กระจัดกระจาย).
                col.Item().Row(r =>
                {
                    r.RelativeItem().Row(rr =>
                    {
                        if (b.LogoBytes is { Length: > 0 })
                            try { rr.ConstantItem(b.LogoHeightMm + 10, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
                        rr.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, "#222"));
                    });
                    r.ConstantItem(220).Column(rc =>
                    {
                        rc.Item().AlignRight().Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                        if (template.ShowDocumentNumber)
                            rc.Item().AlignRight().Text(doc.DocumentNumber).FontSize(13).Bold().FontColor(accent);
                    });
                });
                col.Item().PaddingTop(6).LineHorizontal(2).LineColor(accent);
                // doc info row — ใช้ Text(...) inline spans + AlignRight
                // บน outer container แทน Row+RelativeItem pusher (pusher
                // ไม่มี content method → QuestPDF 2024.12 throw
                // "container has no content" → fallback ลง HTML→Blocks).
                col.Item().PaddingTop(8).AlignRight().Text(tt =>
                {
                    if (template.ShowDocumentDate)
                        tt.Span($"วันที่: {doc.DocumentDate:dd/MM/yyyy}   ").FontSize(10).FontColor("#374151");
                    if (template.ShowDueDate && doc.DueDate.HasValue)
                        tt.Span($"ครบกำหนด: {doc.DueDate:dd/MM/yyyy}   ").FontSize(10).FontColor("#374151");
                    if (template.ShowReference && !string.IsNullOrWhiteSpace(doc.Reference))
                        tt.Span($"อ้างอิง: {doc.Reference}").FontSize(10).FontColor("#374151");
                });
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
        // ใช้ Text(...) inline spans ปลายทาง content method ชัดเจน +
        // AlignRight ที่ outer container — แทน Row + RelativeItem pusher
        // (QuestPDF 2024.12 ไม่ accept Item ที่ไม่มี content method
        // → throw → outer catch fallback ลง HTML→Blocks ที่หน้าตาแย่).
        var item = col.Item().PaddingTop(10);
        if (alignRight) item = item.AlignRight();
        item.Text(tt =>
        {
            if (t.ShowDocumentNumber)
                tt.Span($"เลขที่: {doc.DocumentNumber}   ").FontSize(10);
            if (t.ShowDocumentDate)
                tt.Span($"วันที่: {doc.DocumentDate:dd/MM/yyyy}   ").FontSize(10);
            if (t.ShowDueDate && doc.DueDate.HasValue)
                tt.Span($"ครบกำหนด: {doc.DueDate:dd/MM/yyyy}   ").FontSize(10);
            if (t.ShowReference && !string.IsNullOrWhiteSpace(doc.Reference))
                tt.Span($"อ้างอิง: {doc.Reference}").FontSize(10);
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
    /// <summary>Per-doc-type fallback label สำหรับ contact section ใน PDF.
    /// เอกสารฝั่งซื้อ (PV/PO/PI/Expense) ใช้ "ผู้รับเงิน" / "ผู้ขาย" — ไม่ใช่
    /// "ลูกค้า" ที่ทำให้ผู้อ่านสับสน. Sales side ยังคง "ลูกค้า" ตามเดิม. ค่า
    /// override ที่ template ตั้งไว้ (ContactSectionTitle) มี priority สูงกว่า.</summary>
    private static string DefaultContactLabelFor(Accounting.Models.Enums.DocumentType type) => type switch
    {
        Accounting.Models.Enums.DocumentType.PaymentVoucher => "ผู้รับเงิน",
        Accounting.Models.Enums.DocumentType.PurchaseOrder => "ผู้ขาย",
        Accounting.Models.Enums.DocumentType.PurchaseInvoice => "ผู้ขาย",
        Accounting.Models.Enums.DocumentType.Expense => "ผู้ขาย/ผู้รับเงิน",
        _ => "ลูกค้า",
    };

    private static void ComposeContact(ColumnDescriptor col, EntDoc doc, EntTemplate t, string accent)
    {
        var c = doc.Contact;
        if (c == null) return;
        // Boxed contact section: left accent stripe (เลียนแบบ HTML view
        // "ลูกค้า" box) + section title สี accent ตัวหนา → ดูเด่นชัดขึ้น
        // กว่าเดิมที่เป็นเส้นกรอบบางๆ
        col.Item().PaddingTop(12).BorderLeft(4).BorderColor(accent).Background("#F8FAFC")
            .Padding(10).Column(cc =>
        {
            // Template override > smart per-doc-type fallback > "ลูกค้า"
            // ใบสำคัญจ่ายเป็น "ผู้รับเงิน" ไม่ใช่ "ลูกค้า" — เพราะ PV คือ
            // เราซื้อ/จ่ายจากเขา ไม่ใช่เขาเป็นลูกค้าเรา.
            var label = !string.IsNullOrWhiteSpace(t.ContactSectionTitle)
                && t.ContactSectionTitle != "ลูกค้า"
                ? t.ContactSectionTitle
                : DefaultContactLabelFor(doc.DocumentType);
            cc.Item().Text(label).FontSize(11).Bold().FontColor(accent);
            cc.Item().PaddingTop(2).Text(c.Name ?? "").FontSize(13).Bold().FontColor("#111827");
            if (t.ShowContactTaxId && !string.IsNullOrWhiteSpace(c.TaxId))
                cc.Item().Text($"เลขผู้เสียภาษี: {c.TaxId}").FontSize(10).FontColor("#374151");
            if (t.ShowContactAddress)
            {
                var addr = FormatThaiAddress(c.Address, c.BuildingNumber, c.Moo, c.StreetName,
                    c.SubDistrict, c.District, c.Province, c.PostalCode);
                if (!string.IsNullOrWhiteSpace(addr)) cc.Item().Text(addr).FontSize(10).FontColor("#374151");
            }
            if (t.ShowContactPhone && !string.IsNullOrWhiteSpace(c.Phone))
                cc.Item().Text($"โทร: {c.Phone}").FontSize(10).FontColor("#374151");
            if (t.ShowContactEmail && !string.IsNullOrWhiteSpace(c.Email))
                cc.Item().Text($"Email: {c.Email}").FontSize(10).FontColor("#374151");
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
                        ? h.Cell().BorderBottom(2).BorderColor(accent).PaddingVertical(8).PaddingHorizontal(6)
                        : h.Cell().Background(headerBg).PaddingVertical(8).PaddingHorizontal(6);
                    var tx = cell.Text(text).FontSize(10.5f).Bold().FontColor(flatHeader ? accent : headerText);
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

            // TableBorderStyle ตาม template: Full = กรอบทุกด้าน, HeaderOnly
            // = เส้นใต้ทุกแถว, None = ไม่มีเส้น (HTML CSS line 1184 ก็แยก
            // 3 แบบนี้ — เดิม native ใช้แค่ BorderBottom 0.3pt ทุกแบบ).
            var borderStyle = (t.TableBorderStyle ?? "Full").Trim();
            int idx = 1;
            foreach (var line in doc.Lines.OrderBy(l => l.LineOrder))
            {
                var bg = idx % 2 == 0 ? stripe : "#FFFFFF";
                void Td(string text, string align = "left")
                {
                    var cell = table.Cell().Background(bg);
                    cell = borderStyle switch
                    {
                        "Full" => cell.Border(0.5f).BorderColor("#E5E7EB").Padding(5),
                        "None" => cell.Padding(5),
                        _ => cell.BorderBottom(0.5f).BorderColor("#E5E7EB").Padding(5), // HeaderOnly
                    };
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
        // Layouts ที่ไม่ใช่ Minimal / Letterhead จะใช้ filled accent bar
        // สำหรับ Grand Total — match HTML view ที่มี orange bar เด่นชัด
        // (เดิม native render เป็นแค่ border 2 เส้น ผู้ใช้บอกว่า
        // "หน้าตาไม่เหมือนกับกดดู" → ยอดรวมสุทธิดูไม่เด่น).
        var filledTotal = layout is not ("Minimal" or "Letterhead");

        col.Item().PaddingTop(8).AlignRight().Width(280).Column(sc =>
        {
            void Row(string label, string value, bool total = false)
            {
                var item = sc.Item().PaddingVertical(3);
                if (total)
                {
                    if (filledTotal)
                        item = item.Background(accent).Padding(8);
                    else
                        item = item.BorderTop(2).BorderBottom(2).BorderColor(accent).PaddingVertical(5);
                }
                else item = item.BorderBottom(0.5f).BorderColor("#EEE");
                item.Row(r =>
                {
                    var lblTxt = r.RelativeItem().Text(label).FontSize(total ? 13 : 10);
                    if (total) { lblTxt.Bold().FontColor(filledTotal ? "#FFFFFF" : accent); }
                    var valTxt = r.ConstantItem(120).AlignRight().Text(value).FontSize(total ? 14 : 10);
                    if (total) { valTxt.Bold().FontColor(filledTotal ? "#FFFFFF" : accent); }
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

    private static void ComposeFooter(ColumnDescriptor col, EntDoc doc, EntTemplate t, string accent)
    {
        // CertificateInLieu — เอกสารใบรับรองการจ่ายเงินแทนใบเสร็จ
        // (กรณีจ่ายให้คนไม่จด VAT / ไม่ออกใบเสร็จ) มี metadata block
        // เฉพาะ ที่ HTML view แสดงไว้ที่ BuildDocumentHtml บรรทัด
        // 614-637 — เดิม native renderer ตกหล่นทั้งหมด ทำให้
        // PDF ใบรับรองขาดข้อมูลผู้รับรอง/พยาน/วันที่จ่าย → ใช้ไม่ได้.
        if (doc.DocumentType == Models.Enums.DocumentType.CertificateInLieu)
        {
            col.Item().PaddingTop(14).Border(1).BorderColor("#333").Padding(12).Column(cc =>
            {
                cc.Item().Text("ข้อมูลการรับรอง").FontSize(12).Bold().FontColor(accent);
                if (!string.IsNullOrWhiteSpace(doc.CertificateReason))
                    cc.Item().PaddingTop(4).Text(t =>
                    {
                        t.Span("เหตุผลที่ไม่ได้รับใบเสร็จ: ").Bold().FontSize(10);
                        t.Span(doc.CertificateReason!).FontSize(10);
                    });
                if (doc.PaymentDate.HasValue)
                    cc.Item().PaddingTop(2).Text(t =>
                    {
                        t.Span("วันที่จ่ายเงิน: ").Bold().FontSize(10);
                        t.Span($"{doc.PaymentDate:dd/MM/yyyy}").FontSize(10);
                    });
                cc.Item().PaddingTop(8).Row(r =>
                {
                    r.RelativeItem().Column(c =>
                    {
                        if (!string.IsNullOrWhiteSpace(doc.CertifierName))
                            c.Item().Text(tt => { tt.Span("ผู้รับรอง: ").Bold().FontSize(10); tt.Span(doc.CertifierName!).FontSize(10); });
                        if (!string.IsNullOrWhiteSpace(doc.CertifierPosition))
                            c.Item().Text(tt => { tt.Span("ตำแหน่ง: ").Bold().FontSize(10); tt.Span(doc.CertifierPosition!).FontSize(10); });
                    });
                    if (!string.IsNullOrWhiteSpace(doc.WitnessName))
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().Text(tt => { tt.Span("พยาน: ").Bold().FontSize(10); tt.Span(doc.WitnessName!).FontSize(10); });
                            if (!string.IsNullOrWhiteSpace(doc.WitnessPosition))
                                c.Item().Text(tt => { tt.Span("ตำแหน่ง: ").Bold().FontSize(10); tt.Span(doc.WitnessPosition!).FontSize(10); });
                        });
                });
            });
        }

        // Custom appendix (per-document) — HTML แสดงก่อน bank details
        if (!string.IsNullOrWhiteSpace(doc.CustomAppendix))
            col.Item().PaddingTop(12).Text(doc.CustomAppendix!).FontSize(10).FontColor("#333");

        if (t.ShowBankDetails && !string.IsNullOrWhiteSpace(t.BankDetailsText))
            col.Item().PaddingTop(12).Background("#F8F9FA").Padding(10).Text(tt =>
            {
                tt.Span("ข้อมูลชำระเงิน: ").Bold().FontSize(10);
                tt.Span(t.BankDetailsText!).FontSize(10);
            });

        var footerNotes = !string.IsNullOrWhiteSpace(doc.CustomFooterNotes) ? doc.CustomFooterNotes : t.FooterNotes;
        if (!string.IsNullOrWhiteSpace(footerNotes))
            col.Item().PaddingTop(8).Text(footerNotes).FontSize(10).FontColor("#555");

        // T&C override per-document (HTML BuildDocumentHtml บรรทัด 653) —
        // section "เงื่อนไข" คั่นด้วย border-top + padding ปกติ (ไม่ stack
        // PaddingTop ซ้ำ เพื่อตัดความเสี่ยง QuestPDF API edge case).
        if (!string.IsNullOrWhiteSpace(doc.CustomTermsAndConditions))
            col.Item().PaddingTop(14).BorderTop(0.5f).BorderColor("#EEE").Padding(6).Column(cc =>
            {
                cc.Item().Text("เงื่อนไข").Bold().FontSize(10).FontColor("#555");
                cc.Item().Text(doc.CustomTermsAndConditions!).FontSize(10).FontColor("#555");
            });
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

        // เส้นบางๆ แบ่ง summary จาก signature area — HTML view มี
        // margin-top:48px กับเส้นใต้ total → PDF เคยกระชับติดกันจนอ่านยาก
        col.Item().PaddingTop(18).LineHorizontal(0.4f).LineColor("#E5E7EB");

        DocumentSigner? signerAt(int i) => signers != null && i < signers.Count ? signers[i] : null;

        // Stamp above signatures (optional, defensive).
        if (b.StampBytes is { Length: > 0 })
            try { col.Item().PaddingTop(20).AlignRight().Height(22, Unit.Millimetre).Image(b.StampBytes).FitArea(); } catch { }

        col.Item().PaddingTop(b.StampBytes != null ? 6 : 40).Row(r =>
        {
            for (int i = 0; i < labels.Count; i++)
            {
                var label = labels[i];
                var s = signerAt(i);
                r.RelativeItem().PaddingHorizontal(8).Column(c =>
                {
                    // Reserve the SAME fixed-height signature area in EVERY
                    // column so the rule line sits at an identical height
                    // whether or not the slot is signed. Previously a signed
                    // slot reserved a 14mm image while an unsigned slot padded
                    // only 20pt → the unsigned lines floated higher (the
                    // misalignment reported). Image (when present) renders at
                    // that fixed height; defensive try/catch on bad bytes.
                    // เก็บ Height(14mm) ทุก slot เพื่อเส้นใต้ลายเซ็นอยู่
                    // แนวเดียวกัน + .FitArea() บน Image บังคับ scale ให้
                    // พอดี container ทั้ง W+H (ป้องกัน
                    // DocumentLayoutException: "element requires more
                    // space than available" เมื่อ signature image ยาว
                    // เกินกว่าความกว้างของ column).
                    if (s?.SignatureImageBytes is { Length: > 0 })
                    {
                        try { c.Item().Height(14, Unit.Millimetre).AlignCenter().Image(s.SignatureImageBytes).FitArea(); }
                        catch { c.Item().Height(14, Unit.Millimetre).Text(""); }
                    }
                    else
                    {
                        c.Item().Height(14, Unit.Millimetre).Text("");
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
        col.Item().PaddingTop(14).BorderTop(0.6f).BorderColor("#CBD5E1").PaddingTop(5).Column(c =>
        {
            c.Item().Text(t =>
            {
                t.Span($"{(en ? "Posting" : "การบันทึกบัญชี")} ").FontSize(8.5f).Bold().FontColor("#64748B");
                t.Span($"{gl.EntryNumber} · {gl.EntryDate:dd/MM/yy}").FontSize(8.5f).FontColor("#94A3B8");
            });
            foreach (var l in gl.Lines)
            {
                var isDr = l.Debit != 0;
                var name = string.IsNullOrWhiteSpace(l.AccountCode) ? l.AccountName : $"{l.AccountCode} {l.AccountName}";
                var amt = (isDr ? l.Debit : l.Credit).ToString("N2");
                c.Item().PaddingTop(2).PaddingLeft(isDr ? 0 : 16).Row(r =>
                {
                    r.RelativeItem().Text(t =>
                    {
                        t.Span(isDr ? "Dr " : "Cr ").FontSize(9).Bold().FontColor("#334155");
                        t.Span(name).FontSize(9).FontColor("#334155");
                    });
                    r.ConstantItem(70).AlignRight().Text(amt).FontSize(9).FontColor("#334155");
                });
            }
        });
    }
}
