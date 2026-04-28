using System.Globalization;
using System.Text;

namespace Accounting.Services.Implementations;

public partial class PdfGenerationService
{
    /// <summary>
    /// Assemble the final PDF/A-3 binary.
    /// Layout (object numbers):
    ///  1: Catalog (with /Metadata, /OutputIntents, /AF, /Names, /MarkInfo, /Lang)
    ///  2: Pages
    ///  3: Page 1
    ///  4: Page 1 content stream
    ///  5: Font F1 (Helvetica)
    ///  6: Font F2 (Helvetica-Bold)
    ///  7: Metadata (XMP)
    ///  8: ICC profile stream (sRGB)
    ///  9: OutputIntent dict
    /// 10: Embedded XML file stream
    /// 11: FileSpec for embedded XML
    /// 12: Names dictionary
    /// </summary>
    private static byte[] AssemblePdfA3Document(
        byte[] contentStream,
        string xmlFileName,
        byte[] xmlBytes,
        string xmpMetadata,
        byte[] iccProfile,
        DateTime createdAt)
    {
        using var ms = new MemoryStream();
        var offsets = new Dictionary<int, long>();

        void WriteAscii(string s)
        {
            var b = Encoding.ASCII.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        void WriteBytes(byte[] b) => ms.Write(b, 0, b.Length);

        void StartObject(int n)
        {
            offsets[n] = ms.Position;
            WriteAscii($"{n} 0 obj\n");
        }

        void EndObject() => WriteAscii("\nendobj\n");

        // PDF/A header — must include 4-byte binary marker on line 2
        WriteAscii("%PDF-1.7\n");
        WriteBytes(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        var pdfDate = "D:" + createdAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z";

        // Object 1: Catalog
        StartObject(1);
        WriteAscii("<<\n");
        WriteAscii("  /Type /Catalog\n");
        WriteAscii("  /Pages 2 0 R\n");
        WriteAscii("  /Metadata 7 0 R\n");
        WriteAscii("  /OutputIntents [9 0 R]\n");
        WriteAscii("  /AF [11 0 R]\n");
        WriteAscii("  /Names << /EmbeddedFiles 12 0 R >>\n");
        WriteAscii("  /MarkInfo << /Marked true >>\n");
        WriteAscii("  /Lang (th-TH)\n");
        WriteAscii(">>");
        EndObject();

        // Object 2: Pages
        StartObject(2);
        WriteAscii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        EndObject();

        // Object 3: Page 1
        StartObject(3);
        WriteAscii("<<\n");
        WriteAscii("  /Type /Page\n");
        WriteAscii("  /Parent 2 0 R\n");
        WriteAscii("  /MediaBox [0 0 595 842]\n");
        WriteAscii("  /Contents 4 0 R\n");
        WriteAscii("  /Resources << /Font << /F1 5 0 R /F2 6 0 R >> /ProcSet [/PDF /Text] >>\n");
        WriteAscii(">>");
        EndObject();

        // Object 4: Page 1 content stream
        StartObject(4);
        WriteAscii($"<< /Length {contentStream.Length} >>\nstream\n");
        WriteBytes(contentStream);
        WriteAscii("\nendstream");
        EndObject();

        // Object 5: F1 Helvetica
        StartObject(5);
        WriteAscii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        EndObject();

        // Object 6: F2 Helvetica-Bold
        StartObject(6);
        WriteAscii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        EndObject();

        // Object 7: XMP metadata stream
        var xmpBytes = Encoding.UTF8.GetBytes(xmpMetadata);
        StartObject(7);
        WriteAscii($"<< /Type /Metadata /Subtype /XML /Length {xmpBytes.Length} >>\nstream\n");
        WriteBytes(xmpBytes);
        WriteAscii("\nendstream");
        EndObject();

        // Object 8: ICC profile stream (sRGB)
        StartObject(8);
        WriteAscii($"<< /N 3 /Length {iccProfile.Length} >>\nstream\n");
        WriteBytes(iccProfile);
        WriteAscii("\nendstream");
        EndObject();

        // Object 9: OutputIntent
        StartObject(9);
        WriteAscii("<<\n");
        WriteAscii("  /Type /OutputIntent\n");
        WriteAscii("  /S /GTS_PDFA1\n");
        WriteAscii("  /OutputConditionIdentifier (sRGB IEC61966-2.1)\n");
        WriteAscii("  /OutputCondition (sRGB IEC61966-2.1)\n");
        WriteAscii("  /Info (sRGB IEC61966-2.1)\n");
        WriteAscii("  /DestOutputProfile 8 0 R\n");
        WriteAscii(">>");
        EndObject();

        // Object 10: Embedded XML stream
        StartObject(10);
        WriteAscii("<<\n");
        WriteAscii("  /Type /EmbeddedFile\n");
        WriteAscii("  /Subtype /text#2Fxml\n");
        WriteAscii($"  /Length {xmlBytes.Length}\n");
        WriteAscii("  /Params <<\n");
        WriteAscii($"    /ModDate ({pdfDate})\n");
        WriteAscii($"    /Size {xmlBytes.Length}\n");
        WriteAscii("  >>\n");
        WriteAscii(">>\nstream\n");
        WriteBytes(xmlBytes);
        WriteAscii("\nendstream");
        EndObject();

        // Object 11: File specification (Associated File for embedded XML)
        StartObject(11);
        WriteAscii("<<\n");
        WriteAscii("  /Type /Filespec\n");
        WriteAscii($"  /F ({EscapePdfLiteral(xmlFileName)})\n");
        WriteAscii($"  /UF ({EscapePdfLiteral(xmlFileName)})\n");
        WriteAscii("  /AFRelationship /Source\n");
        WriteAscii("  /Desc (e-Tax XML data per ETDA Recommendation 3-2560 v2.0)\n");
        WriteAscii("  /EF << /F 10 0 R /UF 10 0 R >>\n");
        WriteAscii(">>");
        EndObject();

        // Object 12: EmbeddedFiles name tree
        StartObject(12);
        WriteAscii("<<\n");
        WriteAscii($"  /Names [({EscapePdfLiteral(xmlFileName)}) 11 0 R]\n");
        WriteAscii(">>");
        EndObject();

        // xref table
        var xrefOffset = ms.Position;
        WriteAscii("xref\n");
        WriteAscii("0 13\n");
        WriteAscii("0000000000 65535 f \n");
        for (int i = 1; i <= 12; i++)
        {
            var off = offsets[i];
            WriteAscii($"{off:D10} 00000 n \n");
        }

        // Trailer with /ID required for PDF/A
        var idHex = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(xmlBytes))[..32];
        WriteAscii("trailer\n");
        WriteAscii("<<\n");
        WriteAscii("  /Size 13\n");
        WriteAscii("  /Root 1 0 R\n");
        WriteAscii($"  /ID [<{idHex}> <{idHex}>]\n");
        WriteAscii(">>\n");
        WriteAscii($"startxref\n{xrefOffset}\n");
        WriteAscii("%%EOF\n");

        return ms.ToArray();
    }

    private static string EscapePdfLiteral(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c == '(' || c == ')' || c == '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
