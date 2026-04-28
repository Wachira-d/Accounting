using System.Text;
using Accounting.Models.DTOs.Etax;

namespace Accounting.Services.Implementations;

public partial class PdfGenerationService
{
    /// <summary>
    /// Build XMP metadata stream for PDF/A-3 conformance level U.
    /// Includes ETDA extension schema (rsm:DocumentFileName, rsm:DocumentType, rsm:Version)
    /// per Thai Revenue Department spec.
    /// </summary>
    private static string BuildXmpMetadata(EtaxPdfMetadata m, string xmlFileName)
    {
        var createDate = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var title = SafeXml($"{m.DocumentTypeNameTh} {m.DocumentNumber}");
        var producer = "NextAcc e-Tax PDF/A-3 Generator";
        var creator = SafeXml(m.SellerName);

        // ETDA extension schema namespace
        // Use the document-type-specific namespace per ETDA spec
        var rsmNs = $"urn:etda:uncefact:data:standard:{m.DocumentType}:2";

        var sb = new StringBuilder();
        sb.AppendLine("<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>");
        sb.AppendLine("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"NextAcc e-Tax\">");
        sb.AppendLine("  <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");

        // Core PDF/A identification
        sb.AppendLine("    <rdf:Description rdf:about=\"\"");
        sb.AppendLine("        xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">");
        sb.AppendLine("      <pdfaid:part>3</pdfaid:part>");
        sb.AppendLine("      <pdfaid:conformance>U</pdfaid:conformance>");
        sb.AppendLine("    </rdf:Description>");

        // Dublin Core
        sb.AppendLine("    <rdf:Description rdf:about=\"\"");
        sb.AppendLine("        xmlns:dc=\"http://purl.org/dc/elements/1.1/\">");
        sb.AppendLine("      <dc:format>application/pdf</dc:format>");
        sb.AppendLine("      <dc:title>");
        sb.AppendLine("        <rdf:Alt>");
        sb.AppendLine($"          <rdf:li xml:lang=\"x-default\">{title}</rdf:li>");
        sb.AppendLine("        </rdf:Alt>");
        sb.AppendLine("      </dc:title>");
        sb.AppendLine("      <dc:creator>");
        sb.AppendLine("        <rdf:Seq>");
        sb.AppendLine($"          <rdf:li>{creator}</rdf:li>");
        sb.AppendLine("        </rdf:Seq>");
        sb.AppendLine("      </dc:creator>");
        sb.AppendLine("    </rdf:Description>");

        // XMP basic
        sb.AppendLine("    <rdf:Description rdf:about=\"\"");
        sb.AppendLine("        xmlns:xmp=\"http://ns.adobe.com/xap/1.0/\">");
        sb.AppendLine($"      <xmp:CreateDate>{createDate}</xmp:CreateDate>");
        sb.AppendLine($"      <xmp:ModifyDate>{createDate}</xmp:ModifyDate>");
        sb.AppendLine($"      <xmp:CreatorTool>{producer}</xmp:CreatorTool>");
        sb.AppendLine("    </rdf:Description>");

        // PDF properties
        sb.AppendLine("    <rdf:Description rdf:about=\"\"");
        sb.AppendLine("        xmlns:pdf=\"http://ns.adobe.com/pdf/1.3/\">");
        sb.AppendLine($"      <pdf:Producer>{producer}</pdf:Producer>");
        sb.AppendLine("    </rdf:Description>");

        // ETDA extension schema (declares the rsm: namespace as a custom property)
        sb.AppendLine("    <rdf:Description rdf:about=\"\"");
        sb.AppendLine("        xmlns:pdfaExtension=\"http://www.aiim.org/pdfa/ns/extension/\"");
        sb.AppendLine("        xmlns:pdfaSchema=\"http://www.aiim.org/pdfa/ns/schema#\"");
        sb.AppendLine("        xmlns:pdfaProperty=\"http://www.aiim.org/pdfa/ns/property#\">");
        sb.AppendLine("      <pdfaExtension:schemas>");
        sb.AppendLine("        <rdf:Bag>");
        sb.AppendLine("          <rdf:li rdf:parseType=\"Resource\">");
        sb.AppendLine("            <pdfaSchema:schema>ETDA e-Tax Invoice Extension Schema</pdfaSchema:schema>");
        sb.AppendLine($"            <pdfaSchema:namespaceURI>{rsmNs}#</pdfaSchema:namespaceURI>");
        sb.AppendLine("            <pdfaSchema:prefix>rsm</pdfaSchema:prefix>");
        sb.AppendLine("            <pdfaSchema:property>");
        sb.AppendLine("              <rdf:Seq>");
        sb.AppendLine("                <rdf:li rdf:parseType=\"Resource\">");
        sb.AppendLine("                  <pdfaProperty:name>DocumentFileName</pdfaProperty:name>");
        sb.AppendLine("                  <pdfaProperty:valueType>Text</pdfaProperty:valueType>");
        sb.AppendLine("                  <pdfaProperty:category>external</pdfaProperty:category>");
        sb.AppendLine("                  <pdfaProperty:description>Embedded XML file name</pdfaProperty:description>");
        sb.AppendLine("                </rdf:li>");
        sb.AppendLine("                <rdf:li rdf:parseType=\"Resource\">");
        sb.AppendLine("                  <pdfaProperty:name>DocumentType</pdfaProperty:name>");
        sb.AppendLine("                  <pdfaProperty:valueType>Text</pdfaProperty:valueType>");
        sb.AppendLine("                  <pdfaProperty:category>external</pdfaProperty:category>");
        sb.AppendLine("                  <pdfaProperty:description>ETDA document type</pdfaProperty:description>");
        sb.AppendLine("                </rdf:li>");
        sb.AppendLine("                <rdf:li rdf:parseType=\"Resource\">");
        sb.AppendLine("                  <pdfaProperty:name>Version</pdfaProperty:name>");
        sb.AppendLine("                  <pdfaProperty:valueType>Text</pdfaProperty:valueType>");
        sb.AppendLine("                  <pdfaProperty:category>external</pdfaProperty:category>");
        sb.AppendLine("                  <pdfaProperty:description>ETDA standard version</pdfaProperty:description>");
        sb.AppendLine("                </rdf:li>");
        sb.AppendLine("              </rdf:Seq>");
        sb.AppendLine("            </pdfaSchema:property>");
        sb.AppendLine("          </rdf:li>");
        sb.AppendLine("        </rdf:Bag>");
        sb.AppendLine("      </pdfaExtension:schemas>");
        sb.AppendLine("    </rdf:Description>");

        // ETDA extension data values
        sb.AppendLine("    <rdf:Description rdf:about=\"\"");
        sb.AppendLine($"        xmlns:rsm=\"{rsmNs}#\">");
        sb.AppendLine($"      <rsm:DocumentFileName>{xmlFileName}</rsm:DocumentFileName>");
        sb.AppendLine($"      <rsm:DocumentType>{m.DocumentType}</rsm:DocumentType>");
        sb.AppendLine($"      <rsm:Version>{m.XmlVersion}</rsm:Version>");
        sb.AppendLine("    </rdf:Description>");

        sb.AppendLine("  </rdf:RDF>");
        sb.AppendLine("</x:xmpmeta>");
        sb.Append("<?xpacket end=\"w\"?>");

        return sb.ToString();
    }

    private static string SafeXml(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }
}
