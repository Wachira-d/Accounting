using System.Globalization;
using System.Text;
using Accounting.Models.DTOs.Etax;

namespace Accounting.Services.Implementations;

/// <summary>
/// PDF/A-3 generator with embedded XML — for Thai e-Tax Invoice by Email compliance.
/// Conformance: ISO 19005-3:2012 (PDF/A-3), conformance level "U" (Unicode).
/// Embeds ETDA v2.0 Cross Industry Invoice XML as Associated File (/AFRelationship /Source).
/// XMP metadata includes ETDA extension schema (rsm:DocumentFileName, rsm:DocumentType, rsm:Version).
/// </summary>
public partial class PdfGenerationService
{
    /// <summary>
    /// Build a PDF/A-3 (conformance level U) document with the eTax XML embedded as an Associated File.
    /// The visual content is a simple summary of the invoice (suitable for human reading);
    /// the embedded XML is the legal source of truth.
    /// </summary>
    public byte[] BuildEtaxPdfA3WithEmbeddedXml(string xmlContent, EtaxPdfMetadata metadata)
    {
        var xmlBytes = Encoding.UTF8.GetBytes(xmlContent);
        var xmlFileName = $"{metadata.EtaxRefNumber}.xml";
        var contentStream = BuildEtaxVisualContentStream(metadata);
        var xmpMetadata = BuildXmpMetadata(metadata, xmlFileName);
        var iccProfile = GetMinimalSrgbIccProfile();

        return AssemblePdfA3Document(
            contentStream: contentStream,
            xmlFileName: xmlFileName,
            xmlBytes: xmlBytes,
            xmpMetadata: xmpMetadata,
            iccProfile: iccProfile,
            createdAt: DateTime.UtcNow);
    }

    private static byte[] BuildEtaxVisualContentStream(Models.DTOs.Etax.EtaxPdfMetadata m)
    {
        var sb = new StringBuilder();
        sb.Append("BT\n");

        void Line(string text, int size, bool bold, int x, ref int y)
        {
            var font = bold ? "/F2" : "/F1";
            var pdfStr = EncodePdfString(text);
            sb.Append($"{font} {size} Tf\n");
            sb.Append($"1 0 0 1 {x} {y} Tm\n");
            sb.Append($"{pdfStr} Tj\n");
            y -= size + 6;
        }

        int y = 780;
        Line("e-Tax Invoice / Receipt", 18, true, 60, ref y);
        Line(m.DocumentTypeNameTh, 14, false, 60, ref y);
        y -= 6;
        Line($"Reference: {m.EtaxRefNumber}", 11, false, 60, ref y);
        Line($"Document No: {m.DocumentNumber}", 11, false, 60, ref y);
        Line($"Date: {m.DocumentDate:yyyy-MM-dd}", 11, false, 60, ref y);
        y -= 10;
        Line("Seller / ผู้ออก", 12, true, 60, ref y);
        Line(m.SellerName, 11, false, 60, ref y);
        Line($"Tax ID: {m.SellerTaxId}", 11, false, 60, ref y);
        y -= 10;
        Line("Buyer / ผู้รับ", 12, true, 60, ref y);
        Line(m.BuyerName, 11, false, 60, ref y);
        if (!string.IsNullOrWhiteSpace(m.BuyerTaxId))
            Line($"Tax ID: {m.BuyerTaxId}", 11, false, 60, ref y);
        y -= 16;
        Line($"Total: {m.TotalAmount.ToString("N2", CultureInfo.InvariantCulture)} {m.Currency}",
            14, true, 60, ref y);
        y -= 30;
        Line("This PDF/A-3 document contains an embedded XML attachment", 9, false, 60, ref y);
        Line($"({m.EtaxRefNumber}.xml) which is the legally binding e-Tax data", 9, false, 60, ref y);
        Line("per ETDA Recommendation 3-2560 v2.0 / Thai Revenue Department.", 9, false, 60, ref y);

        sb.Append("ET\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static string EncodePdfString(string text)
    {
        if (text.Any(c => c > 127))
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(text);
            var hex = new StringBuilder("<FEFF");
            foreach (var b in bytes) hex.Append(b.ToString("X2"));
            hex.Append('>');
            return hex.ToString();
        }
        var sb = new StringBuilder("(");
        foreach (var c in text)
        {
            if (c == '(' || c == ')' || c == '\\') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append(')');
        return sb.ToString();
    }
}
