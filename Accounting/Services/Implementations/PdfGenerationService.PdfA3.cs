using System.Globalization;
using System.IO;
using Accounting.Models.DTOs.Etax;
using QuestPDF.Drawing;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Accounting.Services.Implementations;

/// <summary>
/// PDF/A-3 generator using QuestPDF for proper Thai font rendering.
/// Embeds ETDA Cross Industry Invoice XML as Associated File per ETDA 3-2560 v2.0.
/// </summary>
public partial class PdfGenerationService
{
    private static readonly string[] ThaiFontCandidatePaths =
    {
        "/usr/share/fonts/opentype/tlwg/Loma.otf",
        "/usr/share/fonts/opentype/tlwg/Loma-Bold.otf",
        "/usr/share/fonts/opentype/tlwg/Loma-Oblique.otf",
        "/usr/share/fonts/opentype/tlwg/Loma-BoldOblique.otf",
        "/usr/share/fonts/truetype/tlwg/Loma.ttf",
        "/usr/share/fonts/truetype/Sarabun-Regular.ttf",
        "/usr/share/fonts/truetype/Sarabun-Bold.ttf"
    };

    private static bool _fontsRegistered;
    private static readonly object _fontLock = new();

    private static void EnsureThaiFontsRegistered()
    {
        if (_fontsRegistered) return;
        lock (_fontLock)
        {
            if (_fontsRegistered) return;
            foreach (var path in ThaiFontCandidatePaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        using var stream = File.OpenRead(path);
                        FontManager.RegisterFont(stream);
                    }
                }
                catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Failed to register font from {path}: {ex.Message}"); }
            }
            _fontsRegistered = true;
        }
    }

    public byte[] BuildEtaxPdfA3WithEmbeddedXml(string xmlContent, EtaxPdfMetadata metadata)
    {
        EnsureThaiFontsRegistered();

        var xmlBytes = System.Text.Encoding.UTF8.GetBytes(xmlContent);
        var xmlFileName = $"{metadata.EtaxRefNumber}.xml";

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.5f, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(t => t.FontFamily(GetFontFamilyChain()).FontSize(10));

                page.Header().Element(h => ComposeHeader(h, metadata));
                page.Content().Element(c => ComposeBody(c, metadata));
                page.Footer().Element(f => ComposeFooter(f, metadata, xmlFileName));
            });
        });

        document.WithMetadata(new DocumentMetadata
        {
            Title = $"{metadata.DocumentTypeNameTh} {metadata.DocumentNumber}",
            Author = metadata.SellerName,
            Subject = $"e-Tax XML embedded: {xmlFileName}",
            Keywords = "e-Tax, ETDA, Thailand, Tax Invoice, PDF/A-3",
            Producer = "NextAcc e-Tax PDF/A-3 Generator (QuestPDF)",
            Creator = "NextAcc",
            CreationDate = DateTime.UtcNow,
            ModifiedDate = DateTime.UtcNow
        });

        document.WithSettings(new DocumentSettings { PdfA = true });

        var pdfBytes = document.GeneratePdf();
        var etdaXmp = BuildEtdaXmpMetadata(metadata, xmlFileName);
        return PdfAttachmentInjector.AttachXml(pdfBytes, xmlFileName, xmlBytes,
            "e-Tax XML data per ETDA Recommendation 3-2560 v2.0",
            etdaXmpMetadata: etdaXmp);
    }

    /// <summary>
    /// Build XMP metadata stream conformant with ETDA's PDF/A Extension Schema
    /// (Resources/EDocument_PDFAExtensionSchema.xml in github.com/ETDA/e-TaxInvoice-PDFgen).
    /// Declares the rsm: extension schema with DocumentFileName, DocumentType, Version
    /// properties — required for ETDA validator to recognize the embedded XML payload.
    /// PDF/A part=3, conformance=U (Unicode level for Thai text).
    /// </summary>
    private static string BuildEtdaXmpMetadata(EtaxPdfMetadata m, string xmlFileName)
    {
        // Per ETDA template: namespace URI is fixed regardless of doc type
        // (xmlns:rsm = "urn:etda:uncefact:data:standard:Invoice_CrossIndustryInvoice:2#")
        const string rsmNs = "urn:etda:uncefact:data:standard:Invoice_CrossIndustryInvoice:2#";

        // ETDA uses TypeCode here (388/T03/80/81 etc.), pulled from metadata.DocumentType
        // For our DTO, DocumentType holds the root element name (e.g. "TaxInvoice_CrossIndustryInvoice")
        // — translate to ETDA TypeCode for compatibility with ETDA validator.
        var typeCode = m.DocumentType switch
        {
            "TaxInvoice_CrossIndustryInvoice" => "388",
            "Receipt_CrossIndustryInvoice" => "T03",
            "DebitCreditNote_CrossIndustryInvoice" =>
                m.DocumentTypeNameTh.Contains("เพิ่ม") ? "80" : "81",
            _ => "388"
        };

        var sb = new System.Text.StringBuilder();
        sb.Append("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"NextAcc e-Tax\">");
        sb.Append("<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");

        // Core PDF/A-3 identification
        sb.Append("<rdf:Description rdf:about=\"\"");
        sb.Append(" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"");
        sb.Append(" xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\"");
        sb.Append(" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\"");
        sb.Append(" xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">");
        sb.Append("<dc:format>application/pdf</dc:format>");
        sb.Append("<pdfaid:part>3</pdfaid:part>");
        sb.Append("<pdfaid:conformance>U</pdfaid:conformance>");
        sb.Append("</rdf:Description>");

        // ETDA PDF/A Extension Schema declaration
        sb.Append("<rdf:Description rdf:about=\"\"");
        sb.Append(" xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\"");
        sb.Append(" xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\"");
        sb.Append(" xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\">");
        sb.Append("<pdfaExtension:schemas><rdf:Bag><rdf:li rdf:parseType=\"Resource\">");
        sb.Append("<pdfaSchema:schema>Electronic Tax Invoice PDFA Extension Schema</pdfaSchema:schema>");
        sb.Append($"<pdfaSchema:namespaceURI>{rsmNs}</pdfaSchema:namespaceURI>");
        sb.Append("<pdfaSchema:prefix>rsm</pdfaSchema:prefix>");
        sb.Append("<pdfaSchema:property><rdf:Seq>");
        sb.Append("<rdf:li rdf:parseType=\"Resource\">");
        sb.Append("<pdfaProperty:name>DocumentFileName</pdfaProperty:name>");
        sb.Append("<pdfaProperty:valueType>Text</pdfaProperty:valueType>");
        sb.Append("<pdfaProperty:category>external</pdfaProperty:category>");
        sb.Append("<pdfaProperty:description>Name of the embedded XML invoice file</pdfaProperty:description>");
        sb.Append("</rdf:li>");
        sb.Append("<rdf:li rdf:parseType=\"Resource\">");
        sb.Append("<pdfaProperty:name>DocumentType</pdfaProperty:name>");
        sb.Append("<pdfaProperty:valueType>Text</pdfaProperty:valueType>");
        sb.Append("<pdfaProperty:category>external</pdfaProperty:category>");
        sb.Append("<pdfaProperty:description>Type of the document</pdfaProperty:description>");
        sb.Append("</rdf:li>");
        sb.Append("<rdf:li rdf:parseType=\"Resource\">");
        sb.Append("<pdfaProperty:name>Version</pdfaProperty:name>");
        sb.Append("<pdfaProperty:valueType>Text</pdfaProperty:valueType>");
        sb.Append("<pdfaProperty:category>external</pdfaProperty:category>");
        sb.Append("<pdfaProperty:description>Version of the ETDA XML data</pdfaProperty:description>");
        sb.Append("</rdf:li>");
        sb.Append("</rdf:Seq></pdfaSchema:property>");
        sb.Append("</rdf:li></rdf:Bag></pdfaExtension:schemas>");
        sb.Append("</rdf:Description>");

        // ETDA extension data values
        sb.Append("<rdf:Description rdf:about=\"\"");
        sb.Append($" xmlns:rsm=\"{rsmNs}\">");
        sb.Append($"<rsm:DocumentFileName>{XmlEscape(xmlFileName)}</rsm:DocumentFileName>");
        sb.Append($"<rsm:DocumentType>{typeCode}</rsm:DocumentType>");
        // Normalize: ETDA template uses "2.0", strip leading "v" prefix if present
        var version = (m.XmlVersion ?? "2.0").TrimStart('v', 'V');
        sb.Append($"<rsm:Version>{XmlEscape(version)}</rsm:Version>");
        sb.Append("</rdf:Description>");

        sb.Append("</rdf:RDF></x:xmpmeta><?xpacket end=\"r\"?>");
        return sb.ToString();
    }

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
         .Replace("\"", "&quot;").Replace("'", "&apos;");

    private static string[] GetFontFamilyChain() =>
        new[] { "Loma", "Sarabun", "Noto Sans Thai", "TH Sarabun New", Fonts.Calibri, Fonts.Arial };

    private static void ComposeHeader(IContainer container, EtaxPdfMetadata m)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(m.SellerName).FontSize(14).Bold();
                    if (!string.IsNullOrWhiteSpace(m.SellerAddress))
                        c.Item().Text(m.SellerAddress).FontSize(9);
                    c.Item().Text($"เลขประจำตัวผู้เสียภาษี: {m.SellerTaxId}    " +
                        $"สาขา: {(m.SellerBranch == "00000" || string.IsNullOrEmpty(m.SellerBranch) ? "สำนักงานใหญ่" : m.SellerBranch)}")
                        .FontSize(9);
                    if (!string.IsNullOrEmpty(m.SellerPhone))
                        c.Item().Text($"โทร: {m.SellerPhone}    {m.SellerEmail ?? ""}").FontSize(9);
                });

                row.ConstantItem(180).Column(c =>
                {
                    c.Item().AlignRight().Text(m.DocumentTypeNameTh).FontSize(16).Bold();
                    c.Item().AlignRight().Text("(ต้นฉบับ / ORIGINAL)").FontSize(8).Italic();
                    c.Item().AlignRight().Text($"เลขที่: {m.DocumentNumber}").FontSize(10).Bold();
                    c.Item().AlignRight().Text($"วันที่: {m.DocumentDate:dd/MM/yyyy}").FontSize(10);
                    c.Item().AlignRight().Text($"e-Tax Ref: {m.EtaxRefNumber}").FontSize(8);
                });
            });

            col.Item().PaddingVertical(5).LineHorizontal(0.8f);
        });
    }

    private static void ComposeBody(IContainer container, EtaxPdfMetadata m)
    {
        container.PaddingVertical(5).Column(col =>
        {
            col.Item().Background(Colors.Grey.Lighten4).Padding(8).Column(b =>
            {
                b.Item().Text("ลูกค้า / Customer").FontSize(9).Bold();
                b.Item().Text(m.BuyerName).FontSize(11).Bold();
                if (!string.IsNullOrEmpty(m.BuyerAddress))
                    b.Item().Text(m.BuyerAddress).FontSize(9);
                if (!string.IsNullOrEmpty(m.BuyerTaxId))
                    b.Item().Text($"เลขประจำตัวผู้เสียภาษี: {m.BuyerTaxId}    " +
                        $"สาขา: {(m.BuyerBranch == "00000" || string.IsNullOrEmpty(m.BuyerBranch) ? "สำนักงานใหญ่" : m.BuyerBranch)}")
                        .FontSize(9);
            });

            col.Item().PaddingTop(8).Element(e => ComposeLineItemsTable(e, m));
            col.Item().PaddingTop(8).Element(e => ComposeTotalsBlock(e, m));

            if (!string.IsNullOrWhiteSpace(m.Notes))
            {
                col.Item().PaddingTop(10).Background(Colors.Yellow.Lighten5).Padding(6).Column(n =>
                {
                    n.Item().Text("หมายเหตุ / Remarks").FontSize(9).Bold();
                    n.Item().Text(m.Notes).FontSize(9);
                });
            }

            col.Item().PaddingTop(20).Element(e => ComposeSignatureBlock(e, m));
        });
    }

    private static void ComposeLineItemsTable(IContainer container, EtaxPdfMetadata m)
    {
        var lines = m.LineItems ?? new List<EtaxPdfLineItem>();
        var inv = CultureInfo.InvariantCulture;

        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(28);
                c.RelativeColumn(4);
                c.ConstantColumn(50);
                c.ConstantColumn(40);
                c.ConstantColumn(60);
                c.ConstantColumn(70);
            });

            table.Header(h =>
            {
                static IContainer HeaderCell(IContainer x) =>
                    x.Background(Colors.Grey.Lighten2).Padding(4).DefaultTextStyle(t => t.SemiBold().FontSize(9));

                h.Cell().Element(HeaderCell).AlignCenter().Text("ลำดับ");
                h.Cell().Element(HeaderCell).Text("รายการ");
                h.Cell().Element(HeaderCell).AlignRight().Text("จำนวน");
                h.Cell().Element(HeaderCell).AlignCenter().Text("หน่วย");
                h.Cell().Element(HeaderCell).AlignRight().Text("ราคา/หน่วย");
                h.Cell().Element(HeaderCell).AlignRight().Text("รวมเงิน");
            });

            foreach (var line in lines)
            {
                static IContainer BodyCell(IContainer x) =>
                    x.BorderBottom(0.3f).BorderColor(Colors.Grey.Lighten1).PaddingVertical(3).PaddingHorizontal(4);

                table.Cell().Element(BodyCell).AlignCenter().Text(line.LineNo.ToString()).FontSize(9);
                table.Cell().Element(BodyCell).Column(d =>
                {
                    d.Item().Text(line.Description).FontSize(9);
                    if (!string.IsNullOrEmpty(line.ProductCode))
                        d.Item().Text($"รหัส: {line.ProductCode}").FontSize(8).FontColor(Colors.Grey.Darken1);
                });
                table.Cell().Element(BodyCell).AlignRight().Text(line.Quantity.ToString("N2", inv)).FontSize(9);
                table.Cell().Element(BodyCell).AlignCenter().Text(line.Unit).FontSize(9);
                table.Cell().Element(BodyCell).AlignRight().Text(line.UnitPrice.ToString("N2", inv)).FontSize(9);
                table.Cell().Element(BodyCell).AlignRight().Text(line.Amount.ToString("N2", inv)).FontSize(9);
            }
        });
    }

    private static void ComposeTotalsBlock(IContainer container, EtaxPdfMetadata m)
    {
        var inv = CultureInfo.InvariantCulture;

        container.AlignRight().Width(280).Column(col =>
        {
            void TotalRow(string label, decimal value, bool bold = false, bool border = false)
            {
                col.Item().Element(item =>
                {
                    var styled = border ? item.BorderTop(1f).PaddingTop(3) : item;
                    styled.Row(r =>
                    {
                        var labelText = r.RelativeItem().Text(label).FontSize(10);
                        var valueText = r.ConstantItem(120).AlignRight().Text(value.ToString("N2", inv)).FontSize(10);
                        if (bold) { labelText.Bold(); valueText.Bold(); }
                    });
                });
            }

            TotalRow("ยอดรวม / Subtotal", m.SubTotal);
            if (m.DiscountAmount > 0) TotalRow("ส่วนลด / Discount", -m.DiscountAmount);
            TotalRow("ฐานภาษี / Taxable", m.SubTotal - m.DiscountAmount);
            TotalRow("ภาษีมูลค่าเพิ่ม / VAT", m.VatAmount);
            if (m.WithholdingTaxAmount > 0) TotalRow("หัก ณ ที่จ่าย / WHT", -m.WithholdingTaxAmount);
            TotalRow($"รวมทั้งสิ้น / Grand Total ({m.Currency})", m.TotalAmount, bold: true, border: true);
        });
    }

    private static void ComposeSignatureBlock(IContainer container, EtaxPdfMetadata m)
    {
        container.Row(row =>
        {
            row.RelativeItem().Element(e => SignatureBox(e, "ผู้จัดทำ / Prepared by",
                m.CreatedByName, m.CreatedBySignatureBase64, m.CreatedAt));
            row.ConstantItem(20);
            row.RelativeItem().Element(e => SignatureBox(e, "ผู้อนุมัติ / Approved by",
                m.ApprovedByName, m.ApprovedBySignatureBase64, m.ApprovedAt));
        });
    }

    private static void SignatureBox(IContainer container, string label, string? name, string? signatureBase64, DateTime? at)
    {
        container.Column(col =>
        {
            col.Item().Height(50).AlignCenter().AlignBottom().Element(sig =>
            {
                if (!string.IsNullOrWhiteSpace(signatureBase64))
                {
                    var bytes = ParseImageBase64(signatureBase64);
                    if (bytes != null)
                    {
                        sig.Height(45).Image(bytes).FitArea();
                        return;
                    }
                }
                sig.Text("").FontSize(8);
            });
            col.Item().LineHorizontal(0.5f);
            col.Item().AlignCenter().Text($"({name ?? "................................"})").FontSize(9);
            col.Item().AlignCenter().Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
            if (at.HasValue)
                col.Item().AlignCenter().Text($"วันที่: {at.Value:dd/MM/yyyy}").FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }

    private static byte[]? ParseImageBase64(string s)
    {
        var data = s;
        var commaIdx = s.IndexOf(',');
        if (s.StartsWith("data:") && commaIdx > 0) data = s[(commaIdx + 1)..];
        try { return Convert.FromBase64String(data); } catch (Exception ex) { System.Diagnostics.Trace.TraceWarning($"Failed to parse base64 image data: {ex.Message}"); return null; }
    }

    private static void ComposeFooter(IContainer container, EtaxPdfMetadata m, string xmlFileName)
    {
        container.Column(col =>
        {
            col.Item().LineHorizontal(0.3f).LineColor(Colors.Grey.Lighten2);
            col.Item().PaddingTop(3).Row(row =>
            {
                row.RelativeItem().Text(t =>
                {
                    t.Span("เอกสารนี้เป็น ").FontSize(7);
                    t.Span("e-Tax Invoice (PDF/A-3)").FontSize(7).Bold();
                    t.Span($" มี XML ฝัง: {xmlFileName} ตาม ETDA 3-2560 v2.0").FontSize(7);
                });
                row.ConstantItem(80).AlignRight().Text(t =>
                {
                    t.CurrentPageNumber().FontSize(7);
                    t.Span(" / ").FontSize(7);
                    t.TotalPages().FontSize(7);
                });
            });
        });
    }
}
