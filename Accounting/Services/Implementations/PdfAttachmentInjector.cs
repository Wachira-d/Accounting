using System.Globalization;
using System.Text;

namespace Accounting.Services.Implementations;

/// <summary>
/// Post-processes a QuestPDF-generated PDF to add an embedded XML attachment as
/// Associated File (/AFRelationship /Source) — required for ETDA Thai e-Tax PDF/A-3.
///
/// QuestPDF generates a valid PDF (with PDF/A=true also adds OutputIntent + XMP),
/// but does not natively expose Associated Files / EmbeddedFiles. We append new
/// PDF objects via incremental update, then point the Catalog at them.
///
/// The approach uses an incremental update (append-only) so the original document
/// (and its xref/trailer) stays intact — a new xref + updated trailer is appended
/// at the end, referencing the original /Root catalog with our additions overlaid.
/// </summary>
internal static class PdfAttachmentInjector
{
    public static byte[] AttachXml(byte[] pdfBytes, string xmlFileName, byte[] xmlBytes, string description)
    {
        var pdf = pdfBytes;
        var pdfText = Encoding.Latin1.GetString(pdf);

        // Find the catalog object number from the trailer's /Root
        var rootObj = FindRootObjectNumber(pdfText);
        if (rootObj <= 0) return pdfBytes;

        // Find the highest existing object number from xref tables
        var maxObj = FindMaxObjectNumber(pdfText);
        if (maxObj <= 0) return pdfBytes;

        var efObj = maxObj + 1;       // EmbeddedFile stream
        var fsObj = maxObj + 2;       // Filespec dict
        var nameTreeObj = maxObj + 3; // Names tree (EmbeddedFiles)
        var newCatalogObj = rootObj;  // We re-emit the catalog with same number (override)

        // Read original catalog dict
        var originalCatalog = ExtractObject(pdfText, rootObj);

        using var ms = new MemoryStream();
        ms.Write(pdf, 0, pdf.Length);

        // Ensure trailing newline before our incremental section
        if (pdf[pdf.Length - 1] != (byte)'\n') ms.WriteByte((byte)'\n');

        var newOffsets = new SortedDictionary<int, long>();

        void WriteAscii(string s)
        {
            var b = Encoding.Latin1.GetBytes(s);
            ms.Write(b, 0, b.Length);
        }

        void WriteBytes(byte[] b) => ms.Write(b, 0, b.Length);

        // Object: EmbeddedFile stream
        var pdfDate = "D:" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z";
        newOffsets[efObj] = ms.Position;
        WriteAscii($"{efObj} 0 obj\n");
        WriteAscii("<< /Type /EmbeddedFile /Subtype /text#2Fxml ");
        WriteAscii($"/Length {xmlBytes.Length} ");
        WriteAscii($"/Params << /ModDate ({pdfDate}) /Size {xmlBytes.Length} >> >>\n");
        WriteAscii("stream\n");
        WriteBytes(xmlBytes);
        WriteAscii("\nendstream\nendobj\n");

        // Object: Filespec
        newOffsets[fsObj] = ms.Position;
        WriteAscii($"{fsObj} 0 obj\n");
        WriteAscii("<< /Type /Filespec ");
        WriteAscii($"/F ({EscapeLiteral(xmlFileName)}) ");
        WriteAscii($"/UF ({EscapeLiteral(xmlFileName)}) ");
        WriteAscii("/AFRelationship /Source ");
        WriteAscii($"/Desc ({EscapeLiteral(description)}) ");
        WriteAscii($"/EF << /F {efObj} 0 R /UF {efObj} 0 R >> >>\n");
        WriteAscii("endobj\n");

        // Object: EmbeddedFiles name tree
        newOffsets[nameTreeObj] = ms.Position;
        WriteAscii($"{nameTreeObj} 0 obj\n");
        WriteAscii($"<< /Names [({EscapeLiteral(xmlFileName)}) {fsObj} 0 R] >>\n");
        WriteAscii("endobj\n");

        // New Catalog (same object number, overrides original)
        // Merge original catalog body with our additions: /AF, /Names, /MarkInfo
        var mergedCatalog = MergeCatalog(originalCatalog, fsObj, nameTreeObj);
        newOffsets[newCatalogObj] = ms.Position;
        WriteAscii($"{newCatalogObj} 0 obj\n{mergedCatalog}\nendobj\n");

        // Find previous startxref (last one in file)
        var prevStartXref = FindPreviousStartXref(pdfText);
        if (prevStartXref < 0) return pdfBytes;

        // Append new xref (in subsection format)
        var xrefOffset = ms.Position;
        WriteAscii("xref\n");
        // Group consecutive object numbers into subsections
        foreach (var subsection in GroupConsecutive(newOffsets.Keys))
        {
            WriteAscii($"{subsection.Start} {subsection.Count}\n");
            for (int i = 0; i < subsection.Count; i++)
            {
                var off = newOffsets[subsection.Start + i];
                WriteAscii($"{off:D10} 00000 n \n");
            }
        }

        // Trailer with /Prev pointing to previous xref
        var newSize = Math.Max(maxObj, newCatalogObj) + 1; // total object count after additions
        WriteAscii("trailer\n");
        WriteAscii($"<< /Size {newSize} /Root {newCatalogObj} 0 R /Prev {prevStartXref} >>\n");
        WriteAscii($"startxref\n{xrefOffset}\n");
        WriteAscii("%%EOF\n");

        return ms.ToArray();
    }

    private static int FindRootObjectNumber(string pdf)
    {
        var idx = pdf.LastIndexOf("/Root", StringComparison.Ordinal);
        if (idx < 0) return -1;
        var slice = pdf.Substring(idx, Math.Min(64, pdf.Length - idx));
        var parts = slice.Split(new[] { ' ', '\r', '\n', '\t', '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == "Root") continue;
            if (int.TryParse(parts[i], out var num) && num > 0) return num;
        }
        return -1;
    }

    private static int FindMaxObjectNumber(string pdf)
    {
        // Scan all xref subsection headers "N M" (start count) for max start+count-1
        var max = 0;
        var idx = 0;
        while ((idx = pdf.IndexOf("\nxref\n", idx, StringComparison.Ordinal)) >= 0)
        {
            idx += 6;
            var end = pdf.IndexOf("trailer", idx, StringComparison.Ordinal);
            if (end < 0) break;
            var section = pdf.Substring(idx, end - idx);
            var lines = section.Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[0], out var start) && int.TryParse(parts[1], out var count))
                {
                    var top = start + count - 1;
                    if (top > max) max = top;
                }
            }
            idx = end;
        }
        return max;
    }

    private static long FindPreviousStartXref(string pdf)
    {
        var idx = pdf.LastIndexOf("startxref", StringComparison.Ordinal);
        if (idx < 0) return -1;
        var rest = pdf.Substring(idx + 9).TrimStart('\r', '\n', ' ', '\t');
        var end = 0;
        while (end < rest.Length && char.IsDigit(rest[end])) end++;
        if (end == 0) return -1;
        return long.Parse(rest.Substring(0, end), CultureInfo.InvariantCulture);
    }

    private static string ExtractObject(string pdf, int objNum)
    {
        var marker = $"{objNum} 0 obj";
        var start = pdf.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return "<< /Type /Catalog /Pages 2 0 R >>";
        start += marker.Length;
        var end = pdf.IndexOf("endobj", start, StringComparison.Ordinal);
        if (end < 0) return "<< /Type /Catalog /Pages 2 0 R >>";
        return pdf.Substring(start, end - start).Trim();
    }

    private static string MergeCatalog(string originalCatalog, int fsObj, int nameTreeObj)
    {
        // Strip leading << and trailing >>
        var body = originalCatalog.Trim();
        if (body.StartsWith("<<")) body = body.Substring(2);
        if (body.EndsWith(">>")) body = body.Substring(0, body.Length - 2);
        body = body.Trim();

        // Remove any existing /AF or /Names entries (we replace them)
        body = StripDictKey(body, "/AF");
        body = StripDictKey(body, "/Names");
        body = StripDictKey(body, "/AFRelationship");

        var sb = new StringBuilder();
        sb.Append("<< ");
        sb.Append(body);
        sb.Append(' ');
        sb.Append($"/AF [{fsObj} 0 R] ");
        sb.Append($"/Names << /EmbeddedFiles {nameTreeObj} 0 R >> ");
        sb.Append(">>");
        return sb.ToString();
    }

    private static string StripDictKey(string body, string key)
    {
        var idx = body.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return body;
        // Find end of value: handle [...] or <<...>> or simple token
        var i = idx + key.Length;
        while (i < body.Length && (body[i] == ' ' || body[i] == '\r' || body[i] == '\n' || body[i] == '\t')) i++;
        if (i >= body.Length) return body;

        int valEnd;
        if (body[i] == '[')
        {
            var close = body.IndexOf(']', i);
            valEnd = close < 0 ? body.Length : close + 1;
        }
        else if (i + 1 < body.Length && body[i] == '<' && body[i + 1] == '<')
        {
            // Find matching >>
            var depth = 1;
            var j = i + 2;
            while (j < body.Length - 1 && depth > 0)
            {
                if (body[j] == '<' && body[j + 1] == '<') { depth++; j += 2; }
                else if (body[j] == '>' && body[j + 1] == '>') { depth--; j += 2; }
                else j++;
            }
            valEnd = j;
        }
        else
        {
            // Simple token (until whitespace/slash)
            var j = i;
            while (j < body.Length && body[j] != ' ' && body[j] != '\r' && body[j] != '\n' && body[j] != '\t' && body[j] != '/') j++;
            valEnd = j;
        }

        return (body.Substring(0, idx) + body.Substring(valEnd)).Replace("  ", " ").Trim();
    }

    private static IEnumerable<(int Start, int Count)> GroupConsecutive(IEnumerable<int> nums)
    {
        var list = nums.OrderBy(x => x).ToList();
        if (list.Count == 0) yield break;
        var start = list[0];
        var count = 1;
        for (int i = 1; i < list.Count; i++)
        {
            if (list[i] == list[i - 1] + 1) count++;
            else { yield return (start, count); start = list[i]; count = 1; }
        }
        yield return (start, count);
    }

    private static string EscapeLiteral(string s)
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
