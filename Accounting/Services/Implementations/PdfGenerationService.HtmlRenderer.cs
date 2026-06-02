using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Accounting.Services.Implementations;

/// <summary>
/// QuestPDF-backed renderer for the structured blocks produced by
/// ParseHtmlToBlocks. Replaces the old hand-rolled PDF byte writer, which
/// declared only Helvetica/WinAnsiEncoding fonts and therefore could not
/// render Thai text — every Thai document came out as a blank page.
///
/// This partial file deliberately does NOT import Accounting.Models.Entities
/// because QuestPDF.Fluent also exposes a `Document` type; keeping the
/// imports separate avoids an ambiguous-reference clash with the Document
/// entity used throughout the main PdfGenerationService.cs file.
/// </summary>
public partial class PdfGenerationService
{
    private static byte[] RenderBlocksWithQuestPdf(List<HtmlBlock> blocks, PdfBranding? branding = null)
    {
        // Register Loma/Sarabun (same Thai fonts the e-Tax PDF/A-3 export uses).
        EnsureThaiFontsRegistered();

        if (blocks.Count == 0)
            blocks.Add(new HtmlBlock(HtmlBlockType.Text, "(ไม่มีเนื้อหา)"));

        // Kept as hex strings (QuestPDF implicitly converts string → Color);
        // mixing a Colors.* Color value here would break the ?? type unify.
        string accent = branding?.AccentColor ?? "#222222";
        string headerBg = branding?.TableHeaderBg ?? "#4472C4";
        string headerText = branding?.TableHeaderText ?? "#FFFFFF";
        var watermark = branding?.WatermarkText;
        var fontChain = GetFontFamilyChain(branding?.FontFamily);
        var logo = LooksLikeImage(branding?.LogoBytes) ? branding!.LogoBytes : null;
        var stamp = LooksLikeImage(branding?.StampBytes) ? branding!.StampBytes : null;
        var sigLabels = branding?.ShowSignature == true ? (branding.SignatureLabels ?? Array.Empty<string>()) : Array.Empty<string>();

        var pdf = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontFamily(fontChain).FontSize(10));

                // Faint centred watermark behind the content when configured.
                if (!string.IsNullOrWhiteSpace(watermark))
                {
                    try
                    {
                        page.Background().AlignCenter().AlignMiddle()
                            .Text(watermark).FontSize(72).FontColor(Colors.Grey.Lighten3).Bold();
                    }
                    catch { /* watermark is decorative — never block the PDF */ }
                }

                page.Content().Column(col =>
                {
                    // Logo at the top — best-effort. A bad image must not blank
                    // the page, so it's fully guarded; failure just omits it.
                    if (logo != null)
                    {
                        try
                        {
                            var slot = col.Item().PaddingBottom(6);
                            var pos = (branding?.LogoPosition ?? "Left").ToLowerInvariant();
                            slot = pos == "center" ? slot.AlignCenter()
                                 : pos == "right" ? slot.AlignRight()
                                 : slot.AlignLeft();
                            slot.Height(branding?.LogoHeightMm ?? 18f, Unit.Millimetre).Image(logo);
                        }
                        catch { /* skip logo on any QuestPDF/image error */ }
                    }

                    foreach (var block in blocks)
                    {
                        switch (block.Type)
                        {
                            case HtmlBlockType.Title:
                                col.Item().PaddingBottom(6).AlignCenter()
                                    .Text(block.Text).FontSize(16).Bold().FontColor(accent);
                                break;

                            case HtmlBlockType.Header:
                                col.Item().PaddingTop(6).Text(block.Text).FontSize(12).Bold().FontColor(accent);
                                break;

                            case HtmlBlockType.BoldText:
                                col.Item().Text(block.Text).FontSize(10).Bold();
                                break;

                            case HtmlBlockType.Text:
                                col.Item().Text(block.Text).FontSize(10);
                                break;

                            case HtmlBlockType.TableHeader:
                            {
                                var cells = block.Cells ?? Array.Empty<string>();
                                if (cells.Length == 0) break;
                                col.Item().Background(headerBg).Padding(3).Row(r =>
                                {
                                    for (int i = 0; i < cells.Length; i++)
                                        r.RelativeItem(ColWeight(block.ColWidths, i))
                                            .Text(cells[i]).FontSize(9).Bold().FontColor(headerText);
                                });
                                break;
                            }

                            case HtmlBlockType.TableRow:
                            {
                                var cells = block.Cells ?? Array.Empty<string>();
                                if (cells.Length == 0) break;
                                col.Item().BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1)
                                    .PaddingVertical(2).Row(r =>
                                {
                                    for (int i = 0; i < cells.Length; i++)
                                        r.RelativeItem(ColWeight(block.ColWidths, i))
                                            .Text(cells[i]).FontSize(9);
                                });
                                break;
                            }

                            case HtmlBlockType.Separator:
                                col.Item().PaddingVertical(4).LineHorizontal(0.7f)
                                    .LineColor(Colors.Grey.Lighten1);
                                break;

                            case HtmlBlockType.Space:
                                col.Item().Height(8);
                                break;
                        }
                    }

                    // Optional company stamp above the signature row.
                    if (stamp != null)
                    {
                        try { col.Item().PaddingTop(16).AlignRight().Height(22, Unit.Millimetre).Image(stamp); }
                        catch { /* stamp is decorative */ }
                    }

                    // Signature blocks at the bottom — a line + label per signer.
                    if (sigLabels.Length > 0)
                    {
                        try
                        {
                            col.Item().PaddingTop(stamp != null ? 6 : 34).Row(r =>
                            {
                                foreach (var label in sigLabels)
                                {
                                    r.RelativeItem().PaddingHorizontal(8).Column(c =>
                                    {
                                        c.Item().PaddingTop(18).LineHorizontal(0.5f).LineColor(Colors.Grey.Darken1);
                                        c.Item().PaddingTop(3).AlignCenter().Text(label).FontSize(9);
                                    });
                                }
                            });
                        }
                        catch { /* signatures are non-critical — keep the PDF */ }
                    }
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.CurrentPageNumber().FontSize(8);
                    t.Span(" / ").FontSize(8);
                    t.TotalPages().FontSize(8);
                });
            });
        });

        // Outermost safety net: if anything in the branded composition fails
        // at layout time (bad font metric, image edge case…), regenerate once
        // with NO branding so the user still gets a valid document instead of
        // a 500. The plain path is the original, proven renderer.
        try
        {
            return pdf.GeneratePdf();
        }
        catch when (branding != null)
        {
            return RenderBlocksWithQuestPdf(blocks, null);
        }
    }

    /// <summary>Relative column weight for cell <paramref name="index"/>,
    /// falling back to an equal share when the parsed width is missing.</summary>
    private static float ColWeight(int[]? colWidths, int index) =>
        colWidths != null && index < colWidths.Length && colWidths[index] > 0
            ? colWidths[index]
            : 1f;

    /// <summary>Font chain with the template's requested family FIRST (when
    /// supplied), falling back to the always-registered Thai chain. QuestPDF
    /// uses the first family it can resolve, so an unregistered name simply
    /// falls through — never an error, never a blank glyph.</summary>
    private static string[] GetFontFamilyChain(string? preferred)
    {
        var baseChain = GetFontFamilyChain();
        if (string.IsNullOrWhiteSpace(preferred)) return baseChain;
        var p = preferred.Trim();
        if (baseChain.Length > 0 && string.Equals(baseChain[0], p, StringComparison.OrdinalIgnoreCase))
            return baseChain;
        var chain = new string[baseChain.Length + 1];
        chain[0] = p;
        Array.Copy(baseChain, 0, chain, 1, baseChain.Length);
        return chain;
    }

    /// <summary>Cheap magic-byte sniff so we only hand real PNG/JPEG/GIF/WebP
    /// bytes to QuestPDF. Anything else (or null/too-short) → false, and the
    /// caller skips the image rather than risk a QuestPDF decode throw.</summary>
    private static bool LooksLikeImage(byte[]? b)
    {
        if (b == null || b.Length < 12) return false;
        // PNG
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
        // JPEG
        if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
        // GIF
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true;
        // WebP: "RIFF"...."WEBP"
        if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46
            && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true;
        return false;
    }
}
