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
    /// <summary>ขนาด/แนวกระดาษตามที่ตั้งไว้ในเทมเพลต — เดิม QuestPDF hardcode
    /// <c>PageSizes.A4</c> ทั้งไฟล์ ทำให้ตั้ง A5/Letter/แนวนอน แล้ว preview (HTML)
    /// เปลี่ยนตาม แต่ PDF จริงยังเป็น A4 แนวตั้ง — ผิดกฎ "ร่าง = ตัวจริง" และ
    /// กระทบของจริงแน่นอนกับเอกสาร e-Tax (เส้นทางนั้นใช้ QuestPDF เสมอ)
    /// รวมถึงตอน Chromium ใช้ไม่ได้แล้ว fallback มา QuestPDF</summary>
    internal static PageSize ResolvePageSize(EntTemplate t)
    {
        var size = (t.PaperSize ?? "A4").Trim().ToUpperInvariant() switch
        {
            "A5" => PageSizes.A5,
            "LETTER" => PageSizes.Letter,
            _ => PageSizes.A4,
        };
        return string.Equals((t.Orientation ?? "").Trim(), "Landscape", StringComparison.OrdinalIgnoreCase)
            ? size.Landscape()
            : size;
    }

    /// <summary>เลือกข้อความตามภาษาเอกสาร — ใช้ค่าภาษาอังกฤษที่ผู้ใช้ตั้งไว้
    /// เมื่อพิมพ์เอกสารภาษาอังกฤษ ถ้าไม่ได้ตั้งไว้ค่อยตกกลับภาษาไทย.
    /// เดิมช่อง "(EN)" ในหน้าปรับแต่ง (หัวข้อคู่ค้า / หมายเหตุท้าย / ป้ายลายเซ็น)
    /// ไม่มี renderer ตัวไหนอ่านเลย — กรอกไปก็ไม่เคยขึ้นบนกระดาษ</summary>
    internal static string? PickLangText(string? th, string? en, string lang)
        => lang == "en" && !string.IsNullOrWhiteSpace(en) ? en : th;

    internal byte[] RenderDocumentPdfNative(EntDoc doc, EntCompany company,
        EntSettings? settings, EntTemplate template, string? watermarkOverride, string? langOverride,
        IReadOnlyList<DocumentSigner>? signers = null, GlPostingSummary? gl = null,
        bool showFreeTierCredit = false,
        bool pdfA = false, string? pdfTitle = null, string? pdfAuthor = null)
    {
        EnsureThaiFontsRegistered();
        var lang = ResolveDocumentLanguage(langOverride, doc, template, settings);
        var L = Accounting.Services.Implementations.Pdf.DocumentLabels.For(lang);
        // ตราประทับบริษัท: ประทับเฉพาะเอกสารที่อนุมัติแล้ว (ผู้มีอำนาจอนุมัติ = ประทับตรา)
        // — เงื่อนไขเดียวกับช่องลายเซ็นผู้อนุมัติ (Draft/รออนุมัติ/ถูกปฏิเสธ = ไม่ประทับ)
        var stampAllowed = doc.Status is not (Accounting.Models.Enums.DocumentStatus.Draft
            or Accounting.Models.Enums.DocumentStatus.WaitingApproval
            or Accounting.Models.Enums.DocumentStatus.Rejected);
        var b = BuildBranding(template, settings, watermarkOverride, stampAllowed);

        var accent = b.AccentColor ?? "#1F2937";
        var primary = b.PrimaryColor ?? "#1F2937";
        var headerBg = b.TableHeaderBg ?? "#1E40AF";
        var headerText = b.TableHeaderText ?? "#FFFFFF";
        var stripe = SanitizeHex(template.TableStripedColor) ?? "#F8FAFC";
        var layout = (template.LayoutStyle ?? "Classic").Trim();
        var fontChain = GetFontFamilyChain(b.FontFamily);
        // หัวเรื่องทุกเคส (พื้นฐาน + เงื่อนไข + มัดจำ) จาก resolver กลาง —
        // ตั้งเองได้ผ่าน settings; ใช้ร่วมกับ HTML renderer กัน logic drift
        var titleText = ComputeDocumentTitle(doc, template, settings, lang);
        // §86/4 เอกสารออกเป็นชุด — ระบุ ต้นฉบับ บนใบกำกับ/ใบเสร็จภาษี. สำเนา
        // ใช้ WatermarkOverride (สำเนา) ตอนสั่งพิมพ์สำเนา → ไม่ต้องมีป้ายซ้อน.
        var isRd864Doc = doc.DocumentType is Accounting.Models.Enums.DocumentType.TaxInvoice
                or Accounting.Models.Enums.DocumentType.DebitNote
                or Accounting.Models.Enums.DocumentType.CreditNote
            || ((doc.DocumentType is Accounting.Models.Enums.DocumentType.Receipt
                    or Accounting.Models.Enums.DocumentType.ReceiptVoucher) && doc.VatAmount > 0);
        var isCopyPrint = !string.IsNullOrWhiteSpace(b.WatermarkText)
            && (b.WatermarkText!.Contains("สำเนา") || b.WatermarkText.Contains("COPY", StringComparison.OrdinalIgnoreCase));
        // ตำแหน่งป้าย ต้นฉบับ/สำเนา ตั้งได้ต่อเทมเพลต: "Watermark" = ลายน้ำกลาง
        // หน้า (พฤติกรรมเดิม), "TopRight"/"TopLeft" = ป้ายกรอบเล็กมุมบนเอกสาร
        var labelPos = (template.CopyLabelPosition ?? "Watermark").Trim();
        var cornerMode = labelPos is "TopRight" or "TopLeft";
        string? cornerLabel = null;
        if (cornerMode)
        {
            cornerLabel = isCopyPrint ? L.CopyDuplicate : L.CopyOriginal;
        }
        // ป้าย ต้นฉบับ แสดงทุกประเภทเอกสาร (กฎหมายบังคับเฉพาะเอกสารชุด
        // §86/4 แต่แนวปฏิบัติ PEAK/Flow พิมพ์ทุกใบ — ผู้ใช้แยกต้นฉบับ/สำเนา
        // ได้ทันทีโดยไม่ต้องเดา); custom title ยังต่อท้ายให้เว้นแต่โหมดมุม
        if (!isCopyPrint && cornerLabel == null)
            titleText += $"  ({L.CopyOriginal})";
        _ = isRd864Doc; // คงตัวแปรไว้ให้อ่าน context ด้านบนง่าย

        try
        {
            var pdf = QuestPDF.Fluent.Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(ResolvePageSize(template));
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

                    // เอกสารที่ถูกยกเลิก (Voided) — ประทับตรา ยกเลิก สีแดง
                    // **Foreground = ทับบนเนื้อหา** เหมือนตราประทับจริง (เดิมใช้
                    // Background → ตราไปอยู่หลังสุด ข้อมูลทับตรา = ดูเหมือนไม่ถูก
                    // ประทับ). สีโปร่ง 20% (#33) → เนื้อหายังอ่านทะลุได้ เก็บเป็น
                    // หลักฐาน/audit ตามเดิม. priority เหนือ watermark ปกติ.
                    if (doc.Status == Models.Enums.DocumentStatus.Voided)
                    {
                        try
                        {
                            page.Foreground().AlignCenter().AlignMiddle()
                                .Text(L.StatusVoided).FontSize(96).FontColor("#33DC2626").Bold();
                        }
                        catch { }
                    }
                    // Watermark behind everything. ค่า opacity (0..1) จาก
                    // template.WatermarkOpacity แปลงเป็น hex 8-digit ARGB
                    // (เดิม hard-code Colors.Grey.Lighten3) — HTML CSS
                    // BuildCss line 1160 ก็เคารพค่านี้.
                    // corner mode + copy print → ป้ายมุมแทนลายน้ำ (ลายน้ำ
                    // custom อื่นของเทมเพลต เช่น DRAFT ยังเป็นลายน้ำตามเดิม)
                    else if (!string.IsNullOrWhiteSpace(b.WatermarkText) && !(cornerMode && isCopyPrint))
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
                        if (cornerLabel != null)
                            Safe(() =>
                            {
                                // ป้าย ต้นฉบับ/สำเนา มุมบน — กรอบเล็กสีตาม accent
                                var badge = col.Item().PaddingBottom(4);
                                var aligned = labelPos == "TopLeft" ? badge.AlignLeft() : badge.AlignRight();
                                aligned.Border(1).BorderColor(accent)
                                    .PaddingVertical(1).PaddingHorizontal(12)
                                    .Text(cornerLabel).FontSize(11).Bold().FontColor(accent);
                            });
                        Safe(() => ComposeHeaderAndTitle(col, layout, doc, company, template, b, accent, headerBg, headerText, titleText, L));
                        Safe(() => ComposeContact(col, doc, template, accent, L));
                        Safe(() => ComposeAdjustmentRef(col, doc, accent, L));
                        Safe(() => ComposeSupplierInvoiceNote(col, doc, accent, L));
                        Safe(() => ComposeCurrencyNote(col, doc, accent, L));
                        Safe(() => ComposeItemsTable(col, doc, template, headerBg, headerText, stripe, L, layout, accent));
                        Safe(() => ComposeSummary(col, doc, template, accent, layout, L));
                        Safe(() => ComposeFooter(col, doc, template, accent, L));
                        Safe(() => ComposeSignatures(col, template, b, signers, lang));
                        Safe(() => ComposeGlPosting(col, gl, lang, L));
                    });

                    page.Footer().AlignRight().Column(f =>
                    {
                        // เครดิต NextAcc — เฉพาะบัญชีแพ็กเกจฟรี. วางเหนือเลขหน้า
                        // มุมขวาล่าง สีจางขนาดเล็ก ไม่แย่งสายตาจากเนื้อหาเอกสาร
                        if (showFreeTierCredit)
                        {
                            f.Item().AlignRight().Text(t =>
                            {
                                t.Span("จัดทำด้วย ").FontSize(7).FontColor(Colors.Grey.Medium);
                                t.Span("NextAcc").FontSize(7).SemiBold().FontColor(Colors.Grey.Darken1);
                                t.Span(" · ระบบบัญชีออนไลน์").FontSize(7).FontColor(Colors.Grey.Medium);
                            });
                            f.Item().AlignRight().PaddingBottom(2).Text(t =>
                                t.Span("เริ่มใช้ฟรีที่ www.nextacc.net").FontSize(7).FontColor(Colors.Grey.Darken1));
                        }
                        f.Item().AlignRight().Text(t =>
                        {
                            t.CurrentPageNumber().FontSize(8).FontColor(Colors.Grey.Medium);
                            t.Span(" / ").FontSize(8).FontColor(Colors.Grey.Medium);
                            t.TotalPages().FontSize(8).FontColor(Colors.Grey.Medium);
                        });
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
                        page.Size(ResolvePageSize(template));
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
                // เส้นสำรองสุดท้ายก็ต้องพิมพ์เครดิตด้วย ไม่งั้นบัญชีฟรีจะหลุด
                // เครดิตไปเงียบ ๆ เฉพาะตอน composition พัง
                var html = BuildDocumentHtml(doc, company, settings, template, watermarkOverride, langOverride,
                    showFreeTierCredit: showFreeTierCredit);
                return ConvertHtmlToPdf(html, template, b);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Header / Title composition — switches on layout style
    // ─────────────────────────────────────────────────────────────────
    private static void ComposeHeaderAndTitle(ColumnDescriptor col, string layout,
        EntDoc doc, EntCompany company, EntTemplate template, PdfBranding b,
        string accent, string headerBg, string headerText, string titleText, Accounting.Services.Implementations.Pdf.DocumentLabels L)
    {
        // cap title ที่ 18pt กันชื่อเอกสารใหญ่เกิน (เดิม 22/20 ใหญ่ไป — ผู้ใช้ขอเล็กลง)
        var titleFontSize = float.TryParse(template.TitleFontSize, out var tf) ? Math.Min(tf, 18f) : 17f;
        // โลโก้บนหัวเอกสาร: คุมด้วย "ความสูง" (ให้พอดีบรรทัดข้อความบริษัท ~14mm)
        // ไม่ใช่ความกว้าง — เดิมใช้ ConstantItem(LogoHeightMm+10) เป็นความกว้าง +
        // FitArea กับโลโก้สี่เหลี่ยม → โลโก้ 40mm ยักษ์ล้นหัว. cap 8–16mm.
        var logoH = Math.Clamp(b.LogoHeightMm, 8f, 16f);

        switch (layout)
        {
            case "BannerHeader":
                // Full-width accent band holds ONLY the logo + company info.
                // The document title sits BELOW the band on white (accent
                // colour) so it never competes with the coloured header and a
                // long Thai label gets the full page width — matches the
                // on-screen view.
                col.Item().Background(accent).PaddingVertical(9).PaddingHorizontal(12).Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(26, Unit.Millimetre).AlignMiddle().MaxHeight(logoH, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, headerText, L));
                });
                col.Item().PaddingTop(10).AlignCenter()
                    .Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                col.Item().PaddingTop(3).PaddingBottom(2).LineHorizontal(1).LineColor(accent);
                col.Item().PaddingTop(6).AlignCenter().Row(r => RenderDocInfoSpans(r, doc, template, accent, L));
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
                        try { r.ConstantItem(26, Unit.Millimetre).AlignMiddle().MaxHeight(logoH, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, "#222", L));
                });
                col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
                ComposeDocInfo(col, doc, template, accent, false, L);
                break;

            case "Letterhead":
                // Centered corporate letterhead with thick accent rule.
                col.Item().AlignCenter().Column(c =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { c.Item().AlignCenter().Height(logoH, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
                    RenderCompanyLines(c, company, template, "#222", L, center: true);
                });
                col.Item().PaddingVertical(4).LineHorizontal(2.5f).LineColor(accent);
                col.Item().PaddingTop(8).Text(titleText)
                    .FontSize(titleFontSize).Bold().FontColor(accent);
                ComposeDocInfo(col, doc, template, accent, false, L);
                break;

            case "CenteredFormal":
                col.Item().AlignCenter().Column(c =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { c.Item().AlignCenter().Height(logoH, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
                    RenderCompanyLines(c, company, template, "#222", L, center: true);
                });
                col.Item().PaddingTop(10).AlignCenter().BorderTop(2.5f).BorderBottom(2.5f).BorderColor(accent)
                    .PaddingVertical(6)
                    .Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                col.Item().PaddingTop(6).AlignCenter().Row(r => RenderDocInfoSpans(r, doc, template, accent, L));
                break;

            case "ModernLeft":
                // Header below, title with bold left accent stripe.
                col.Item().PaddingBottom(8).BorderBottom(2).BorderColor(accent).Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(26, Unit.Millimetre).AlignMiddle().MaxHeight(logoH, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, "#222", L));
                });
                col.Item().PaddingTop(12).BorderLeft(6).BorderColor(accent).PaddingLeft(10)
                    .Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                ComposeDocInfo(col, doc, template, accent, false, L);
                break;

            case "SidebarAccent":
                col.Item().Background(accent).Padding(14).Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(26, Unit.Millimetre).AlignMiddle().MaxHeight(logoH, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, headerText, L));
                });
                col.Item().PaddingTop(10).Background("#F1F5F9").BorderLeft(6).BorderColor(accent)
                    .PaddingVertical(8).PaddingHorizontal(12)
                    .Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                ComposeDocInfo(col, doc, template, accent, false, L);
                break;

            case "SplitHeader":
                col.Item().PaddingBottom(8).BorderBottom(3).BorderColor(accent).Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(26, Unit.Millimetre).AlignMiddle().MaxHeight(logoH, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, "#222", L));
                });
                col.Item().PaddingTop(10).Row(r =>
                {
                    r.RelativeItem().Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                    r.ConstantItem(180).Background("#F8FAFC").BorderLeft(4).BorderColor(accent)
                        .PaddingVertical(8).PaddingHorizontal(10).Column(c =>
                        {
                            if (template.ShowDocumentNumber) c.Item().Text($"{L.DocNumber}: {DisplayDocNumber(doc)}").FontSize(10);
                            if (template.ShowDocumentDate) c.Item().Text($"{L.DocDate}: {L.Date(doc.DocumentDate)}").FontSize(10);
                            if (template.ShowDueDate && doc.DueDate.HasValue)
                                c.Item().Text($"{L.DueDateFor(doc.DocumentType)}: {L.Date(doc.DueDate!.Value)}").FontSize(10);
                            if (template.ShowReference && !string.IsNullOrWhiteSpace(doc.DisplayReference))
                                c.Item().Text($"{L.Reference}: {doc.DisplayReference}").FontSize(10);
                        });
                });
                break;

            case "Compact":
            case "Minimal":
                col.Item().Row(r =>
                {
                    if (b.LogoBytes is { Length: > 0 })
                        try { r.ConstantItem(26, Unit.Millimetre).AlignMiddle().MaxHeight(logoH, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
                    r.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, "#222", L));
                });
                col.Item().PaddingTop(layout == "Compact" ? 6 : 14)
                    .Text(titleText).FontSize(titleFontSize)
                    .Bold().FontColor(layout == "Minimal" ? "#111" : accent);
                if (layout == "Minimal")
                    col.Item().PaddingTop(2).LineHorizontal(1).LineColor("#111");
                ComposeDocInfo(col, doc, template, accent, false, L);
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
                            try { rr.ConstantItem(26, Unit.Millimetre).AlignMiddle().MaxHeight(logoH, Unit.Millimetre).Image(b.LogoBytes).FitArea(); } catch { }
                        rr.RelativeItem().PaddingLeft(12).Column(c => RenderCompanyLines(c, company, template, "#222", L));
                    });
                    r.ConstantItem(220).Column(rc =>
                    {
                        rc.Item().AlignRight().Text(titleText).FontSize(titleFontSize).Bold().FontColor(accent);
                        if (template.ShowDocumentNumber)
                            rc.Item().AlignRight().Text(DisplayDocNumber(doc)).FontSize(13).Bold().FontColor(accent);
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
                        tt.Span($"{L.DocDate}: {L.Date(doc.DocumentDate)}   ").FontSize(10).FontColor("#374151");
                    if (template.ShowDueDate && doc.DueDate.HasValue)
                        tt.Span($"{L.DueDateFor(doc.DocumentType)}: {L.Date(doc.DueDate!.Value)}   ").FontSize(10).FontColor("#374151");
                    if (template.ShowReference && !string.IsNullOrWhiteSpace(doc.DisplayReference))
                        tt.Span($"{L.Reference}: {doc.DisplayReference}").FontSize(10).FontColor("#374151");
                });
                break;
        }
    }

    private static void RenderCompanyLines(ColumnDescriptor c, EntCompany co, EntTemplate t, string color, Accounting.Services.Implementations.Pdf.DocumentLabels L, bool center = false)
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
            var addr = FormatThaiAddress(co.Address, co.BuildingNumber, co.BuildingName, co.Moo, co.StreetName,
                co.SubDistrict, co.District, co.Province, co.PostalCode);
            if (!string.IsNullOrWhiteSpace(addr)) Line(addr);
        }
        if (t.ShowCompanyTaxId && !string.IsNullOrWhiteSpace(co.TaxId))
        {
            var coBranch = FormatBranch(co.BranchCode, co.BranchName, "th");
            Line($"{L.TaxId}: {co.TaxId} ({coBranch})");
        }
        if (t.ShowCompanyPhone && !string.IsNullOrWhiteSpace(co.Phone))
            Line($"{L.Phone}: {co.Phone}");
        if (t.ShowCompanyEmail && !string.IsNullOrWhiteSpace(co.Email))
            Line($"Email: {co.Email}");
    }

    private static void ComposeDocInfo(ColumnDescriptor col, EntDoc doc, EntTemplate t, string accent, bool alignRight, Accounting.Services.Implementations.Pdf.DocumentLabels L)
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
                tt.Span($"{L.DocNumber}: {DisplayDocNumber(doc)}   ").FontSize(10);
            if (t.ShowDocumentDate)
                tt.Span($"{L.DocDate}: {L.Date(doc.DocumentDate)}   ").FontSize(10);
            if (t.ShowDueDate && doc.DueDate.HasValue)
                tt.Span($"{L.DueDateFor(doc.DocumentType)}: {L.Date(doc.DueDate!.Value)}   ").FontSize(10);
            if (t.ShowReference && !string.IsNullOrWhiteSpace(doc.DisplayReference))
                tt.Span($"{L.Reference}: {doc.DisplayReference}").FontSize(10);
        });
    }

    private static void RenderDocInfoSpans(RowDescriptor r, EntDoc doc, EntTemplate t, string accent, Accounting.Services.Implementations.Pdf.DocumentLabels L)
    {
        void Span(string s) => r.AutoItem().PaddingHorizontal(10).Text(s).FontSize(10);
        if (t.ShowDocumentNumber) Span($"{L.DocNumber}: {DisplayDocNumber(doc)}");
        if (t.ShowDocumentDate) Span($"{L.DocDate}: {L.Date(doc.DocumentDate)}");
        if (t.ShowDueDate && doc.DueDate.HasValue) Span($"{L.DueDateFor(doc.DocumentType)}: {L.Date(doc.DueDate!.Value)}");
        if (t.ShowReference && !string.IsNullOrWhiteSpace(doc.DisplayReference)) Span($"{L.Reference}: {doc.DisplayReference}");
    }

    // ─────────────────────────────────────────────────────────────────
    //  Shared section composers (contact / table / summary / etc.)
    // ─────────────────────────────────────────────────────────────────
    /// <summary>Per-doc-type fallback label สำหรับ contact section ใน PDF.
    /// เอกสารฝั่งซื้อ (PV/PO/PI/Expense) ใช้ ผู้รับเงิน / ผู้ขาย — ไม่ใช่
    /// ลูกค้า ที่ทำให้ผู้อ่านสับสน. Sales side ยังคง ลูกค้า ตามเดิม. ค่า
    /// override ที่ template ตั้งไว้ (ContactSectionTitle) มี priority สูงกว่า.</summary>
    private static string DefaultContactLabelFor(Accounting.Models.Enums.DocumentType type, Accounting.Services.Implementations.Pdf.DocumentLabels L) => type switch
    {
        Accounting.Models.Enums.DocumentType.PaymentVoucher => L.PartyPayee,
        Accounting.Models.Enums.DocumentType.PurchaseOrder => L.PartyVendor,
        Accounting.Models.Enums.DocumentType.PurchaseInvoice => L.PartyVendor,
        Accounting.Models.Enums.DocumentType.Expense => L.PartyVendorOrPayee,
        _ => L.PartyCustomer,
    };

    private static void ComposeContact(ColumnDescriptor col, EntDoc doc, EntTemplate t, string accent, Accounting.Services.Implementations.Pdf.DocumentLabels L)
    {
        var c = doc.Contact;
        if (c == null) return;
        // Boxed contact section: left accent stripe (เลียนแบบ HTML view
        // ลูกค้า box) + section title สี accent ตัวหนา → ดูเด่นชัดขึ้น
        // กว่าเดิมที่เป็นเส้นกรอบบางๆ
        // กระชับขึ้น: padding เล็กลง + ชื่อลูกค้าเล็กลง (ผู้ใช้ขอ) ให้ได้พื้นที่คืน
        col.Item().PaddingTop(8).BorderLeft(3).BorderColor(accent).Background("#F8FAFC")
            .PaddingVertical(6).PaddingHorizontal(9).Column(cc =>
        {
            // Template override > smart per-doc-type fallback > ลูกค้า
            // ใบสำคัญจ่ายเป็น ผู้รับเงิน ไม่ใช่ ลูกค้า — เพราะ PV คือ
            // เราซื้อ/จ่ายจากเขา ไม่ใช่เขาเป็นลูกค้าเรา.
            var customContactTitle = PickLangText(t.ContactSectionTitle, t.ContactSectionTitleEn,
                L.IsEnglish ? "en" : "th");
            var label = !string.IsNullOrWhiteSpace(customContactTitle)
                && customContactTitle != L.PartyCustomer
                ? customContactTitle
                : DefaultContactLabelFor(doc.DocumentType, L);
            cc.Item().Text(label).FontSize(9.5f).Bold().FontColor(accent);
            cc.Item().Text(c.Name ?? "").FontSize(11.5f).Bold().FontColor("#111827");
            if (t.ShowContactTaxId && !string.IsNullOrWhiteSpace(c.TaxId))
            {
                // สาขาเป็นเรื่องนิติบุคคล (ประกาศฯ 199) — บุคคลธรรมดาแสดงเฉพาะ
                // เมื่อตั้งรหัสสาขาไว้จริง (บุคคลจด VAT) กันเลขบัตรประชาชนขึ้น
                // "(สำนักงานใหญ่)" ผิดความจริง
                // + ต้องเคารพติ๊ก ShowContactBranch (เดิมติ๊กออกแล้วสาขายังขึ้น
                // ทั้งสอง renderer — ติ๊กนี้ไม่เคยถูกอ่านเลย) ยกเว้นใบกำกับภาษี
                // เต็มรูป §86/4 ที่กฎหมายบังคับให้มี — ติ๊กปิดไม่ได้
                var showBranch = (t.ShowContactBranch || RequiresBuyerBranchOnPrint(doc.DocumentType))
                    && (c.ContactType != Accounting.Models.Enums.ContactType.Individual
                        || (!string.IsNullOrWhiteSpace(c.BranchCode)
                            && c.BranchCode!.Trim().TrimStart('0').Length > 0));
                var cBranch = showBranch ? $" ({FormatBranch(c.BranchCode, c.BranchName, "th")})" : "";
                cc.Item().Text($"{L.TaxIdShort}: {c.TaxId}{cBranch}").FontSize(9).FontColor("#374151");
            }
            if (t.ShowContactAddress)
            {
                var addr = FormatThaiAddress(c.Address, c.BuildingNumber, c.BuildingName, c.Moo, c.StreetName,
                    c.SubDistrict, c.District, c.Province, c.PostalCode);
                if (!string.IsNullOrWhiteSpace(addr)) cc.Item().Text(addr).FontSize(9).FontColor("#374151");
            }
            if (t.ShowContactPhone && !string.IsNullOrWhiteSpace(c.Phone))
                cc.Item().Text($"{L.Phone}: {c.Phone}").FontSize(9).FontColor("#374151");
            if (t.ShowContactEmail && !string.IsNullOrWhiteSpace(c.Email))
                cc.Item().Text($"Email: {c.Email}").FontSize(9).FontColor("#374151");
        });
    }

    /// <summary>กล่องอ้างอิงใบต้นฉบับบนใบลดหนี้/ใบเพิ่มหนี้ — §86/9-10 บังคับแสดง
    /// เลขที่+วันที่ใบกำกับเดิม, มูลค่าตามใบเดิม, มูลค่าที่ถูกต้อง, ผลต่าง (+VAT
    /// ผลต่างอยู่ในตารางสรุปของใบอยู่แล้ว). ข้อมูลจาก transient AdjustmentOriginal*
    /// (โหลดใน ResolveServedAsReceiptAsync) — ใบที่ไม่มี ref จะไม่มีกล่อง.</summary>
    /// <summary>บรรทัด "เลขที่ใบกำกับภาษีของผู้ขาย" บนเอกสารฝั่งซื้อ — ใบกำกับตัวจริง
    /// เป็นของผู้ขาย เลขที่ที่ใช้อ้างกับสรรพากร (และที่พิมพ์ในรายงานภาษีซื้อ ภ.พ.30)
    /// คือเลขของเขา ไม่ใช่เลขรันภายในของเรา — เดิมกระดาษไม่เคยพิมพ์เลขนี้เลย
    /// จับคู่กับใบกำกับของผู้ขายไม่ได้. mirror ฝั่ง HTML</summary>
    private static void ComposeSupplierInvoiceNote(ColumnDescriptor col, EntDoc doc, string accent,
        Accounting.Services.Implementations.Pdf.DocumentLabels L)
    {
        if (!IsPurchaseSideDocType(doc.DocumentType)) return;
        if (string.IsNullOrWhiteSpace(doc.SupplierInvoiceNumber)) return;
        var dt = doc.SupplierTaxInvoiceDate.HasValue ? $"  ·  {L.Date(doc.SupplierTaxInvoiceDate.Value)}" : "";
        col.Item().PaddingTop(4).Text($"{L.SupplierInvoiceLabel}: {doc.SupplierInvoiceNumber}{dt}")
            .FontSize(9.5f).Bold().FontColor(accent);
    }

    /// <summary>บรรทัด "สกุลเงิน" สำหรับเอกสารที่ไม่ใช่บาท — เดิม PDF พิมพ์ตัวเลข
    /// เปล่า ๆ ไม่บอกสกุลเงินเลย ผู้อ่านแยกไม่ออกว่า 1,000 คือบาทหรือดอลลาร์
    /// (และ TFRS บทที่ 19 ต้องเห็นอัตราที่ใช้แปลงค่าด้วย)
    ///
    /// วางเป็นบล็อกเดียวในสายหลัก ไม่ยัดเข้า doc-info ของแต่ละเลย์เอาต์ (มี 4 จุด)
    /// เพื่อให้ขึ้น "ครั้งเดียว" เสมอไม่ว่าเลือกเลย์เอาต์ไหน</summary>
    private static void ComposeCurrencyNote(ColumnDescriptor col, EntDoc doc, string accent,
        Accounting.Services.Implementations.Pdf.DocumentLabels L)
    {
        if (string.IsNullOrWhiteSpace(doc.Currency)
            || string.Equals(doc.Currency, "THB", StringComparison.OrdinalIgnoreCase)) return;
        var rate = doc.ExchangeRate > 0 && doc.ExchangeRate != 1m
            ? $"  ·  {L.FxRateLabel} {doc.ExchangeRate:N4}" : "";
        col.Item().PaddingTop(4).Text($"{L.CurrencyLabel}: {doc.Currency}{rate}")
            .FontSize(9.5f).Bold().FontColor(accent);
    }

    private static void ComposeAdjustmentRef(ColumnDescriptor col, EntDoc doc, string accent, Accounting.Services.Implementations.Pdf.DocumentLabels L)
    {
        if (doc.DocumentType is not (Accounting.Models.Enums.DocumentType.CreditNote
            or Accounting.Models.Enums.DocumentType.DebitNote)) return;
        if (string.IsNullOrWhiteSpace(doc.AdjustmentOriginalNumber)) return;

        var isCn = doc.DocumentType == Accounting.Models.Enums.DocumentType.CreditNote;
        // ใบเดิมอยู่นอกระบบ = รู้แค่เลขที่ ไม่รู้มูลค่า → ซ่อนแถวยอด แทนการพิมพ์
        // 0.00 (ข้อมูลเท็จบนเอกสารภาษี) — mirror ฝั่ง HTML
        var hasOrigAmounts = doc.AdjustmentOriginalSubTotal.HasValue;
        var origBase = doc.AdjustmentOriginalSubTotal ?? 0m;
        var corrected = isCn ? origBase - doc.SubTotal : origBase + doc.SubTotal;
        var origDate = doc.AdjustmentOriginalDate.HasValue
            ? doc.AdjustmentOriginalDate.Value.ToString("dd/MM/yyyy")
            : "-";

        col.Item().PaddingTop(6).Border(1).BorderColor("#D1D5DB").Background("#FFFBEB")
            .PaddingVertical(5).PaddingHorizontal(9).Column(cc =>
        {
            // มี VAT = อ้างใบกำกับภาษีตาม §86/9-10; ไม่มี VAT = ไม่มีใบกำกับให้อ้าง
            // → "เอกสารต้นฉบับ" (mirror ฝั่ง HTML — ห้ามพิมพ์คำว่าใบกำกับทั้งที่ไม่มี)
            cc.Item().Text(doc.AdjustmentOriginalHasVat
                    ? $"อ้างอิงใบกำกับภาษีเดิม (มาตรา 86/{(isCn ? "10" : "9")})"
                    : "อ้างอิงเอกสารต้นฉบับ")
                .FontSize(9.5f).Bold().FontColor(accent);
            cc.Item().Text(string.Format(L.CnOriginalNumber, doc.AdjustmentOriginalNumber, origDate))
                .FontSize(9.5f).FontColor("#374151");
            if (!string.IsNullOrWhiteSpace(doc.AdjustmentOriginalOurNumber))
                cc.Item().Text($"{L.OurDocRefLabel}: {doc.AdjustmentOriginalOurNumber}")
                    .FontSize(9).FontColor("#4B5563");
            if (hasOrigAmounts)
                cc.Item().Row(r =>
                {
                    r.RelativeItem().Text($"{L.CnOriginalValue}: {origBase:N2}").FontSize(9).FontColor("#374151");
                    r.RelativeItem().Text($"{L.CnCorrectedValue}: {corrected:N2}").FontSize(9).FontColor("#374151");
                    r.RelativeItem().Text($"ผลต่าง ({(isCn ? "ลด" : L.CnIncrease)}): {doc.SubTotal:N2}").FontSize(9).Bold().FontColor("#374151");
                });
            else
                cc.Item().Text($"มูลค่าที่{(isCn ? "ลด" : "เพิ่ม")}: {doc.SubTotal:N2}")
                    .FontSize(9).Bold().FontColor("#374151");
            // §86/10 (ลดหนี้) และ §86/9 (เพิ่มหนี้) บังคับระบุเหตุผลบนตัวเอกสาร
            // (mirror ฝั่ง HTML — สองตัวนี้ต้องพิมพ์เหมือนกันเป๊ะ)
            var reasonTxt = isCn
                ? CreditNoteReasonText(doc.CreditNoteReason, L)
                : DebitNoteReasonText(doc.DebitNoteReason, L);
            if (!string.IsNullOrWhiteSpace(reasonTxt))
                cc.Item().Text($"{L.CnReason}: {reasonTxt}").FontSize(9).FontColor("#374151");
        });
    }

    private static void ComposeItemsTable(ColumnDescriptor col, EntDoc doc, EntTemplate t,
        string headerBg, string headerText, string stripe, Accounting.Services.Implementations.Pdf.DocumentLabels L, string layout = "Classic", string accent = "#1F2937")
    {
        // Minimal & Letterhead use a borderless header — no fill, accent-coloured
        // text and a single rule underneath — to match their on-screen look.
        var flatHeader = layout is "Minimal" or "Letterhead";

        col.Item().PaddingTop(8).Table(table =>
        {
            table.ColumnsDefinition(cols =>
            {
                if (t.ShowLineNumber) cols.ConstantColumn(28);
                // รหัสสินค้า / VAT ต่อรายการ / WHT ต่อรายการ — 3 ติ๊กนี้เคยเป็น
                // "ติ๊กแล้วไม่มีอะไรเกิดขึ้น" (ไม่มี renderer ตัวไหนอ่านเลย ทั้ง
                // HTML และ QuestPDF) ผู้ใช้ตั้งค่าแล้วเข้าใจว่าระบบพัง
                if (t.ShowItemCode) cols.ConstantColumn(62);
                cols.RelativeColumn(4);
                cols.ConstantColumn(50);
                if (t.ShowUnit) cols.ConstantColumn(45);
                cols.ConstantColumn(70);
                if (t.ShowDiscount) cols.ConstantColumn(60);
                if (t.ShowVatPerLine) cols.ConstantColumn(46);
                if (t.ShowWithholdingTax) cols.ConstantColumn(46);
                cols.ConstantColumn(80);
            });

            table.Header(h =>
            {
                void Th(string text, string align = "left")
                {
                    var cell = flatHeader
                        ? h.Cell().BorderBottom(2).BorderColor(accent).PaddingVertical(5).PaddingHorizontal(6)
                        : h.Cell().Background(headerBg).PaddingVertical(5).PaddingHorizontal(6);
                    var tx = cell.Text(text).FontSize(9.5f).Bold().FontColor(flatHeader ? accent : headerText);
                    if (align == "right") tx.AlignRight();
                    else if (align == "center") tx.AlignCenter();
                }
                // ราคารวม VAT → label "(รวม VAT)" + แสดง amount แบบรวม VAT
                // ให้ math ในตารางตรวจสอบยอดได้ในตัวเอง (มาตรฐาน OfficeMate/
                // Tesco/Makro/Big C). เดิมราคา/หน่วย incl + จำนวนเงิน ex →
                // ผู้อ่านงงเพราะคำนวณยังไงก็ไม่ตรง.
                var inclVat = doc.PricesIncludeVat;
                if (t.ShowLineNumber) Th("#", "center");
                if (t.ShowItemCode) Th(L.ColItemCode);
                Th(L.ColItem);
                Th(L.ColQty, "right");
                if (t.ShowUnit) Th(L.ColUnit, "center");
                Th(inclVat ? L.ColUnitPriceIncl : L.ColUnitPrice, "right");
                if (t.ShowDiscount) Th(L.ColDiscount, "right");
                if (t.ShowVatPerLine) Th(L.ColVatPerLine, "right");
                if (t.ShowWithholdingTax) Th(L.ColWhtPerLine, "right");
                Th(inclVat ? L.ColAmountIncl : L.ColAmount, "right");
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
                    // แถวกระชับ: padding แนวตั้ง 3 (เดิม 5 ทุกด้าน) → ได้ ~7-8 รายการ/หน้า
                    cell = borderStyle switch
                    {
                        "Full" => cell.Border(0.5f).BorderColor("#E5E7EB").PaddingVertical(3).PaddingHorizontal(5),
                        "None" => cell.PaddingVertical(3).PaddingHorizontal(5),
                        _ => cell.BorderBottom(0.5f).BorderColor("#E5E7EB").PaddingVertical(3).PaddingHorizontal(5), // HeaderOnly
                    };
                    var tx = cell.Text(text).FontSize(9.5f);
                    if (align == "right") tx.AlignRight();
                    else if (align == "center") tx.AlignCenter();
                }
                // ราคารวม VAT: amount ที่แสดง = qty × price − discount (รวม
                // VAT) เพื่อให้สอดคล้องกับ label header. backend Amount เป็น
                // ex-VAT สำหรับ GL/ภพ.30 ไม่กระทบ.
                // มัดจำ VAT พักรอ: พิมพ์ยอดบรรทัดแบบรวม VAT (Amount ex + VatAmount)
                // เพื่อให้บรรทัดรวมเท่ายอดสุทธิที่ลูกค้าจ่ายจริง (ไม่มีบรรทัด VAT
                // แยกให้เห็น)
                var printedAmount = IsDeferredVatDeposit(doc)
                    ? Math.Round(line.Amount + line.VatAmount, 2)
                    : doc.PricesIncludeVat
                        ? Math.Round(line.Quantity * line.UnitPrice - line.DiscountAmount, 2)
                        : line.Amount;
                // ส่วนลดท้ายบิล: line.Amount เก็บยอด L.AfterDiscountAlloc (สำหรับ GL/
                // VAT) แต่บนกระดาษต้องโชว์ยอด L.TotalBeforeBillDiscount — ไม่งั้นบรรทัดขัด
                // กันเอง (1 × 19,650 − ส่วนลด 0 = 17,526.76 ??) และไม่ตรงหน้าแก้ไข.
                // scale กลับด้วยสัดส่วนเดียวกับที่เฉลี่ยลง (Σก่อนหัก / Σหลังหัก) —
                // ส่วนลดท้ายบิลแสดงเป็นแถวเดียวในสรุปท้ายบิล
                // (เฉพาะ path ที่พิมพ์จาก line.Amount — โหมด PricesIncludeVat คิดจาก
                //  qty×price ซึ่งเป็นยอดก่อนหักท้ายบิลอยู่แล้ว ห้าม scale ซ้ำ)
                if (doc.BillDiscountAmount > 0 && doc.SubTotal > 0.005m
                    && !IsDeferredVatDeposit(doc) && !doc.PricesIncludeVat)
                    printedAmount = Math.Round(printedAmount * (doc.SubTotal + doc.BillDiscountAmount) / doc.SubTotal, 2);
                // รายการย่อย/บรรยายงาน (ไม่ระบุราคา): ราคา 0 + ยอด 0 → เว้นช่อง
                // ตัวเลขว่างแทน "0.00" — ใช้แจกแจงงานย่อยใต้บรรทัดแม่ที่ถือราคา
                // (เช่น งานหลัก 5,000 + งานย่อย 4 บรรทัดบอกขอบเขต) คง จำนวน/หน่วย ไว้
                var isDescriptiveLine = line.UnitPrice == 0 && line.Amount == 0 && line.VatAmount == 0;
                if (t.ShowLineNumber) Td(idx.ToString(), "center");
                if (t.ShowItemCode) Td(line.ProductCode ?? "");
                Td(line.Description ?? "");
                Td(isDescriptiveLine && line.Quantity == 1 ? "" : line.Quantity.ToString("N2"), "right");
                if (t.ShowUnit) Td(line.Unit ?? "", "center");
                Td(isDescriptiveLine ? "" : line.UnitPrice.ToString("N2"), "right");
                if (t.ShowDiscount) Td(isDescriptiveLine ? "" : line.DiscountAmount.ToString("N2"), "right");
                // VatRate = -1 คือ "ยกเว้น" (sentinel เดียวกับฟอร์ม) ไม่ใช่ -1%
                if (t.ShowVatPerLine) Td(isDescriptiveLine ? "" : FormatLineVatRate(line.VatRate, L), "right");
                if (t.ShowWithholdingTax) Td(isDescriptiveLine || line.WithholdingTaxRate <= 0
                    ? "" : line.WithholdingTaxRate.ToString("0.##") + "%", "right");
                Td(isDescriptiveLine ? "" : printedAmount.ToString("N2"), "right");
                idx++;
            }
        });
    }

    private static void ComposeSummary(ColumnDescriptor col, EntDoc doc, EntTemplate t, string accent, string layout, Accounting.Services.Implementations.Pdf.DocumentLabels L)
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
            var hideVatBreakdown = IsDeferredVatDeposit(doc);
            // ส่วนลดท้ายบิล: SubTotal = หลังหักท้ายบิล → โชว์ยอดก่อนหัก + บรรทัดส่วนลด
            var preBillSubTotal = doc.SubTotal + doc.BillDiscountAmount;
            if (t.ShowSubTotal && !hideVatBreakdown) Row(L.TotalSubtotal, preBillSubTotal.ToString("N2"));
            if (t.ShowDiscountTotal && doc.DiscountAmount > 0)
                Row(L.TotalDiscount, doc.DiscountAmount.ToString("N2"));
            if (doc.BillDiscountAmount > 0)
            {
                Row(L.TotalBillDiscount, $"({doc.BillDiscountAmount:N2})");
                // ยอดหลังหักส่วนลด = ฐานภาษี — ให้เห็นชัดว่า VAT/WHT คิดจากยอดนี้
                // (ลำดับถูกหลักบัญชี: รวม → หักส่วนลด → ฐานภาษี → VAT → WHT → สุทธิ)
                if (!hideVatBreakdown)
                    Row(L.TotalAfterDiscountBase, doc.SubTotal.ToString("N2"));
            }
            if (t.ShowVatSummary && doc.VatAmount > 0 && !hideVatBreakdown)
                Row("ภาษีมูลค่าเพิ่ม 7%", doc.VatAmount.ToString("N2"));
            if (t.ShowWithholdingTaxSummary && doc.WithholdingTaxAmount > 0)
                Row(L.TotalWht, $"({doc.WithholdingTaxAmount:N2})");
            // หักเงินมัดจำ (display-only): แสดง ยอดรวม → หักมัดจำ → ยอดชำระสุทธิ
            // JE ไม่เกี่ยว (การรับรู้มัดจำทำแยกแล้ว) — บรรทัดขายยังเต็มจำนวน
            if (doc.DepositAppliedAmount > 0)
            {
                Row(L.TotalGrand, doc.TotalAmount.ToString("N2"));
                var depLabel = string.IsNullOrWhiteSpace(doc.DepositAppliedRef)
                    ? L.TotalDepositApplied : $"หักเงินมัดจำ ({doc.DepositAppliedRef})";
                Row(depLabel, $"({doc.DepositAppliedAmount:N2})");
                Row(L.TotalNetPayable, (doc.TotalAmount - doc.DepositAppliedAmount).ToString("N2"), total: true);
            }
            else
                Row(L.TotalNet, doc.TotalAmount.ToString("N2"), total: true);
        });

        // มัดจำ VAT พักรอ — แจ้งชัดว่าไม่ใช่ใบกำกับภาษี (ใบกำกับออกตอนใช้บริการ)
        if (IsDeferredVatDeposit(doc))
            col.Item().PaddingTop(6).Text("* เอกสารนี้ไม่ใช่ใบกำกับภาษี — ใบกำกับภาษีจะออกให้เมื่อมีการใช้บริการ/ชำระครบถ้วน")
                .FontSize(9).Italic().FontColor("#555");

        if (t.ShowAmountInWords)
        {
            var words = t.AmountInWordsLanguage == "en"
                ? ConvertToEnglishWords(doc.TotalAmount)
                : ConvertToThaiWords(doc.TotalAmount);
            col.Item().PaddingTop(8).AlignCenter().Text($"({words})").FontSize(10).Italic().FontColor("#444");
        }
    }

    /// <summary>
    /// กัน OCR diagnostic trace หลุดไปพิมพ์บนเอกสารจริง. เอกสารเก่าที่ handoff
    /// flow เคยยัด processingNotes ทั้งก้อนลง Notes (prefix L.FromOcr +
    /// marker [Zone Analysis]/[Field Confidence]/[Reasoning]/[VendorIntel]/
    /// [AI/DeepSeek] ฯลฯ) จะถูกตัดออกตอน render — เหลือเฉพาะหมายเหตุจริง.
    /// ถ้าทั้งก้อนเป็น diagnostic → คืน null (ไม่พิมพ์ส่วนหมายเหตุเลย).
    /// </summary>
    internal static string? SanitizeNotesForPrint(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var text = notes.Trim();
        // Fast path: OCR dump ขึ้นต้นด้วย L.FromOcr หรือมี marker วงเล็บเหลี่ยม
        // ที่เป็น diagnostic ภายใน — ตัดตั้งแต่ marker ตัวแรกเป็นต้นไป
        var markerRx = new System.Text.RegularExpressions.Regex(
            @"\(จาก OCR\)|\[Zone Analysis\]|\[Field Confidence\]|\[Reasoning\]|\[VendorIntel\]|\[AI/DeepSeek\]|\[Azure DI|\[Buyer\]|\[Role\]|\[Category\]|\[NaiveBayes\]|\[Enrich\]|\[DBD\]|\[Handwriting\]|\[Tier \d|\[AmountTriple\]|\[SmartExtract\]|\[Gateway\]|\[ImagePrep\]|\[RequestPlan\]|\[Swap\]");
        var m = markerRx.Match(text);
        if (m.Success)
            text = text[..m.Index].Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void ComposeFooter(ColumnDescriptor col, EntDoc doc, EntTemplate t, string accent, Accounting.Services.Implementations.Pdf.DocumentLabels L)
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
                cc.Item().Text(L.CertInfo).FontSize(12).Bold().FontColor(accent);
                if (!string.IsNullOrWhiteSpace(doc.CertificateReason))
                    cc.Item().PaddingTop(4).Text(t =>
                    {
                        t.Span(L.CertReason + ": ").Bold().FontSize(10);
                        t.Span(doc.CertificateReason!).FontSize(10);
                    });
                if (doc.PaymentDate.HasValue)
                    cc.Item().PaddingTop(2).Text(t =>
                    {
                        t.Span(L.PaymentDate + ": ").Bold().FontSize(10);
                        t.Span($"{doc.PaymentDate:dd/MM/yyyy}").FontSize(10);
                    });
                cc.Item().PaddingTop(8).Row(r =>
                {
                    r.RelativeItem().Column(c =>
                    {
                        if (!string.IsNullOrWhiteSpace(doc.CertifierName))
                            c.Item().Text(tt => { tt.Span(L.CertCertifier + ": ").Bold().FontSize(10); tt.Span(doc.CertifierName!).FontSize(10); });
                        if (!string.IsNullOrWhiteSpace(doc.CertifierPosition))
                            c.Item().Text(tt => { tt.Span(L.CertPosition + ": ").Bold().FontSize(10); tt.Span(doc.CertifierPosition!).FontSize(10); });
                    });
                    if (!string.IsNullOrWhiteSpace(doc.WitnessName))
                        r.RelativeItem().Column(c =>
                        {
                            c.Item().Text(tt => { tt.Span(L.CertWitness + ": ").Bold().FontSize(10); tt.Span(doc.WitnessName!).FontSize(10); });
                            if (!string.IsNullOrWhiteSpace(doc.WitnessPosition))
                                c.Item().Text(tt => { tt.Span(L.CertPosition + ": ").Bold().FontSize(10); tt.Span(doc.WitnessPosition!).FontSize(10); });
                        });
                });
            });
        }

        // Custom appendix (per-document) — HTML แสดงก่อน bank details
        if (!string.IsNullOrWhiteSpace(doc.CustomAppendix))
            col.Item().PaddingTop(12).Text(doc.CustomAppendix!).FontSize(10).FontColor("#333");

        var bankTxt = PickLangText(t.BankDetailsText, t.BankDetailsTextEn, L.IsEnglish ? "en" : "th");
        if (t.ShowBankDetails && !string.IsNullOrWhiteSpace(bankTxt))
            col.Item().PaddingTop(12).Background("#F8F9FA").Padding(10).Text(tt =>
            {
                tt.Span(L.PaymentInfo + ": ").Bold().FontSize(10);
                tt.Span(bankTxt!).FontSize(10);
            });

        // เงื่อนไขการชำระเงินของใบนี้ — mirror ของ HTML renderer (เดิม
        // ShowPaymentTerms ไม่ถูก render ที่ไหนเลยทั้งสองตัว) เพื่อให้ draft
        // preview กับ PDF ตอนอนุมัติตรงกันทุกจุด
        if (t.ShowPaymentTerms
            && (!string.IsNullOrWhiteSpace(doc.PaymentTerms) || doc.CreditDays > 0))
        {
            // PaymentTerms เก็บได้หลายบรรทัด (1 เงื่อนไข/บรรทัด — ฟอร์มให้เพิ่ม/
            // ลบรายข้อ) บรรทัดเดียวคงรูปแบบเดิม, หลายบรรทัดแตกเป็น bullet ให้
            // อ่านง่ายเท่าฝั่ง HTML (draft preview ต้องตรงกับ PDF ตัวจริง)
            // `doc.CreditDays` เป็น int? — เงื่อนไข `> 0` การันตีว่าไม่ null แล้ว
            // (lifted comparison: null > 0 เป็น false) จึงใช้ .Value ได้ปลอดภัย
            var creditTxt = doc.CreditDays > 0 ? $" ({L.CreditDaysText(doc.CreditDays.Value)})" : "";
            var termLines = (doc.PaymentTerms ?? "")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            col.Item().PaddingTop(8).Background("#F8F9FA").Padding(10).Column(pc =>
            {
                if (termLines.Length <= 1)
                {
                    pc.Item().Text(tt =>
                    {
                        tt.Span($"{L.PaymentTermsLabel}: ").Bold().FontSize(10);
                        tt.Span((termLines.FirstOrDefault() ?? "") + creditTxt).FontSize(10);
                    });
                }
                else
                {
                    pc.Item().Text($"{L.PaymentTermsLabel}:{creditTxt}").Bold().FontSize(10);
                    foreach (var ln in termLines)
                        pc.Item().Text("• " + ln).FontSize(10);
                }
            });
        }

        // หมายเหตุระดับเอกสาร (doc.Notes) ที่ผู้ใช้กรอกตอนสร้าง — เดิมไม่ถูก
        // render บน PDF (แสดงแต่ CustomFooterNotes). QuestPDF Text รองรับ \n →
        // หมายเหตุหลายบรรทัดแสดงครบ.
        var cleanNotes = SanitizeNotesForPrint(doc.Notes);
        if (!string.IsNullOrWhiteSpace(cleanNotes))
            col.Item().PaddingTop(8).Text(tt =>
            {
                tt.Span(L.Notes + ": ").Bold().FontSize(10).FontColor("#555");
                tt.Span(cleanNotes).FontSize(10).FontColor("#555");
            });

        // หมายเหตุท้ายเอกสาร: ของใบนี้ชนะ > ของเทมเพลต (เลือกภาษาตามเอกสาร)
        var footerNotes = !string.IsNullOrWhiteSpace(doc.CustomFooterNotes)
            ? doc.CustomFooterNotes
            : PickLangText(t.FooterNotes, t.FooterNotesEn, L.IsEnglish ? "en" : "th");
        if (!string.IsNullOrWhiteSpace(footerNotes))
            col.Item().PaddingTop(8).Text(footerNotes).FontSize(10).FontColor("#555");

        // T&C override per-document (HTML BuildDocumentHtml บรรทัด 653) —
        // section เงื่อนไข คั่นด้วย border-top + padding ปกติ (ไม่ stack
        // PaddingTop ซ้ำ เพื่อตัดความเสี่ยง QuestPDF API edge case).
        if (!string.IsNullOrWhiteSpace(doc.CustomTermsAndConditions))
            col.Item().PaddingTop(14).BorderTop(0.5f).BorderColor("#EEE").Padding(6).Column(cc =>
            {
                cc.Item().Text(L.Terms).Bold().FontSize(10).FontColor("#555");
                cc.Item().Text(doc.CustomTermsAndConditions!).FontSize(10).FontColor("#555");
            });
    }

    private static void ComposeSignatures(ColumnDescriptor col, EntTemplate t, PdfBranding b,
        IReadOnlyList<DocumentSigner>? signers, string lang = "th")
    {
        if (!t.ShowSignature) return;
        // ป้ายลายเซ็นภาษาอังกฤษที่ผู้ใช้ตั้งไว้ — เดิมไม่เคยถูกอ่าน เอกสารภาษา
        // อังกฤษจึงพิมพ์ป้ายไทยเสมอ (ช่อง "(EN)" ในหน้าปรับแต่งเป็นช่องลม)
        var labels = new List<string>();
        var l1 = PickLangText(t.SignatureLabel1, t.SignatureLabel1En, lang);
        var l2 = PickLangText(t.SignatureLabel2, t.SignatureLabel2En, lang);
        var l3 = PickLangText(t.SignatureLabel3, t.SignatureLabel3En, lang);
        if (!string.IsNullOrWhiteSpace(l1)) labels.Add(l1!);
        if (t.SignatureCount >= 2 && !string.IsNullOrWhiteSpace(l2)) labels.Add(l2!);
        if (t.SignatureCount >= 3 && !string.IsNullOrWhiteSpace(l3)) labels.Add(l3!);
        if (labels.Count == 0) return;

        // เส้นบางๆ แบ่ง summary จาก signature area — HTML view มี
        // margin-top:48px กับเส้นใต้ total → PDF เคยกระชับติดกันจนอ่านยาก
        col.Item().PaddingTop(18).LineHorizontal(0.4f).LineColor("#E5E7EB");

        DocumentSigner? signerAt(int i) => signers != null && i < signers.Count ? signers[i] : null;

        // ตราประทับเหนือช่องลงนาม (จุดที่ผู้อนุมัติเซ็น) — ขนาด/ตำแหน่งตั้งได้.
        // ประทับเฉพาะเอกสารอนุมัติแล้ว (BuildBranding gate ด้วย stampAllowed).
        if (b.StampBytes is { Length: > 0 })
            try
            {
                var stampW = b.StampWidthMm > 0 ? b.StampWidthMm : 32f;
                var stampH = b.StampHeightMm > 0 ? b.StampHeightMm : 32f;
                var cell = col.Item().PaddingTop(14);
                cell = b.StampAlign switch
                {
                    "Left" => cell.AlignLeft(),
                    "Center" => cell.AlignCenter(),
                    _ => cell.AlignRight(),
                };
                cell.Width(stampW, Unit.Millimetre).Height(stampH, Unit.Millimetre)
                    .Image(b.StampBytes).FitArea();
            }
            catch { /* stamp decorative — ห้าม break PDF */ }

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
    private static void ComposeGlPosting(ColumnDescriptor col, GlPostingSummary? gl, string lang, Accounting.Services.Implementations.Pdf.DocumentLabels L)
    {
        if (gl == null || gl.Lines.Count == 0) return;
        var en = lang == "en";
        // การบันทึกบัญชี ขึ้นหน้าใหม่เสมอ — กัน Dr/Cr ถูกตัดคนละหน้า และแยก
        // หน้าเอกสารลูกค้า (หน้า 1) ออกจากส่วนบันทึกบัญชีภายใน (หน้า 2)
        col.Item().PageBreak();
        col.Item().PaddingTop(14).BorderTop(0.6f).BorderColor("#CBD5E1").PaddingTop(5).Column(c =>
        {
            c.Item().Text(t =>
            {
                t.Span($"{(en ? "Posting" : L.GlPosting)} ").FontSize(8.5f).Bold().FontColor("#64748B");
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
