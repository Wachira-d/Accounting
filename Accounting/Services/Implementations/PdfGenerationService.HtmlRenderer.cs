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
    private static byte[] RenderBlocksWithQuestPdf(List<HtmlBlock> blocks)
    {
        // Register Loma/Sarabun (same Thai fonts the e-Tax PDF/A-3 export uses).
        EnsureThaiFontsRegistered();

        if (blocks.Count == 0)
            blocks.Add(new HtmlBlock(HtmlBlockType.Text, "(ไม่มีเนื้อหา)"));

        var pdf = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontFamily(GetFontFamilyChain()).FontSize(10));

                page.Content().Column(col =>
                {
                    foreach (var block in blocks)
                    {
                        switch (block.Type)
                        {
                            case HtmlBlockType.Title:
                                col.Item().PaddingBottom(6).AlignCenter()
                                    .Text(block.Text).FontSize(16).Bold();
                                break;

                            case HtmlBlockType.Header:
                                col.Item().PaddingTop(6).Text(block.Text).FontSize(12).Bold();
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
                                col.Item().Background(Colors.Grey.Lighten2).Padding(3).Row(r =>
                                {
                                    for (int i = 0; i < cells.Length; i++)
                                        r.RelativeItem(ColWeight(block.ColWidths, i))
                                            .Text(cells[i]).FontSize(9).Bold();
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
                });

                page.Footer().AlignRight().Text(t =>
                {
                    t.CurrentPageNumber().FontSize(8);
                    t.Span(" / ").FontSize(8);
                    t.TotalPages().FontSize(8);
                });
            });
        });

        return pdf.GeneratePdf();
    }

    /// <summary>Relative column weight for cell <paramref name="index"/>,
    /// falling back to an equal share when the parsed width is missing.</summary>
    private static float ColWeight(int[]? colWidths, int index) =>
        colWidths != null && index < colWidths.Length && colWidths[index] > 0
            ? colWidths[index]
            : 1f;
}
