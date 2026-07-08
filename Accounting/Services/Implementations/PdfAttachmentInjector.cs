using System.Globalization;
using System.Text;

namespace Accounting.Services.Implementations;

/// <summary>
/// Post-processes a QuestPDF-generated PDF to add an embedded XML attachment as
/// Associated File (/AFRelationship /Alternative) — required for ETDA Thai e-Tax PDF/A-3.
///
/// Per ETDA reference implementation (github.com/ETDA/e-TaxInvoice-PDFgen,
/// PDFA3Invoice.cs.EmbeddedAttachment) the relationship MUST be /Alternative
/// (the embedded XML is an alternative representation of the visual content).
///
/// QuestPDF generates a valid PDF (with PDF/A=true also adds OutputIntent + XMP),
/// but does not natively expose Associated Files / EmbeddedFiles. We append new
/// PDF objects via incremental update, then point the Catalog at them.
/// </summary>
internal static class PdfAttachmentInjector
{
    public static byte[] AttachXml(byte[] pdfBytes, string xmlFileName, byte[] xmlBytes, string description,
        string? etdaXmpMetadata = null)
    {
        var pdf = pdfBytes;
        var pdfText = Encoding.Latin1.GetString(pdf);

        // Find the catalog object number from the trailer's /Root
        var rootObj = FindRootObjectNumber(pdfText);
        if (rootObj <= 0) return pdfBytes;

        // Find the highest existing object number from xref tables — fallback ไป
        // สแกน object header ถ้าอ่าน xref ไม่ได้ (กัน bail เงียบ = ไม่ฝัง XML)
        var maxObj = FindMaxObjectNumber(pdfText);
        if (maxObj <= 0) maxObj = FindMaxObjNumberByHeaders(pdfText);
        if (maxObj <= 0) return pdfBytes;

        var efObj = maxObj + 1;       // EmbeddedFile stream
        var fsObj = maxObj + 2;       // Filespec dict
        var nameTreeObj = maxObj + 3; // Names tree (EmbeddedFiles)
        var xmpObj = etdaXmpMetadata != null ? maxObj + 4 : 0; // XMP metadata (optional)
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
        // Per ETDA reference: relationship is /Alternative, not /Source.
        // The XML is the legally authoritative version; PDF visual is the alternate
        // representation. PDF/A-3 spec defines /Alternative for this exact case.
        WriteAscii("/AFRelationship /Alternative ");
        WriteAscii($"/Desc ({EscapeLiteral(description)}) ");
        WriteAscii($"/EF << /F {efObj} 0 R /UF {efObj} 0 R >> >>\n");
        WriteAscii("endobj\n");

        // Object: EmbeddedFiles name tree
        newOffsets[nameTreeObj] = ms.Position;
        WriteAscii($"{nameTreeObj} 0 obj\n");
        WriteAscii($"<< /Names [({EscapeLiteral(xmlFileName)}) {fsObj} 0 R] >>\n");
        WriteAscii("endobj\n");

        // Object: ETDA XMP metadata stream (overrides QuestPDF's default XMP)
        // Per ETDA Resources/EDocument_PDFAExtensionSchema.xml — declares the
        // rsm: extension schema with DocumentFileName/DocumentType/Version properties.
        if (xmpObj > 0)
        {
            var xmpBytes = Encoding.UTF8.GetBytes(etdaXmpMetadata!);
            newOffsets[xmpObj] = ms.Position;
            WriteAscii($"{xmpObj} 0 obj\n");
            WriteAscii($"<< /Type /Metadata /Subtype /XML /Length {xmpBytes.Length} >>\nstream\n");
            WriteBytes(xmpBytes);
            WriteAscii("\nendstream\nendobj\n");
        }

        // New Catalog (same object number, overrides original)
        // Merge original catalog body with our additions: /AF, /Names, /Metadata, /MarkInfo
        var mergedCatalog = MergeCatalog(originalCatalog, fsObj, nameTreeObj, xmpObj);
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

        // Trailer with /Prev pointing to previous xref. **PDF/A บังคับต้องมี /ID**
        // และ incremental update ต้องคง /ID เดิม (ต้องตรงกับไฟล์ต้นทาง) — เดิมไม่ใส่
        // → veraPDF/ETDA validator reject. ดึง /ID เดิมจาก trailer ก่อนหน้ามาใส่ต่อ.
        //
        // 🔴 /Size ต้อง ≥ object number สูงสุด + 1 เสมอ. **บั๊กเดิม**: ใช้
        // Math.Max(maxObj, newCatalogObj)+1 = maxObj+1 (newCatalogObj คือ Root เลขต่ำ)
        // แต่เราเพิ่ง add object เลข maxObj+1..maxObj+4 (EmbeddedFile/Filespec/
        // NameTree/XMP) → เลขพวกนี้ ≥ /Size → parser (iText ที่สรรพากรใช้) ถือว่า
        // object นอกช่วง หา embedded XML ไม่เจอ → "ประมวลผลเอกสารแนบไม่ได้". แก้เป็น
        // เลข object สูงสุดที่เขียนจริง + 1.
        var newSize = newOffsets.Keys.Max() + 1;
        var trailerId = FindTrailerId(pdfText);
        WriteAscii("trailer\n");
        WriteAscii(trailerId != null
            ? $"<< /Size {newSize} /Root {newCatalogObj} 0 R /Prev {prevStartXref} /ID {trailerId} >>\n"
            : $"<< /Size {newSize} /Root {newCatalogObj} 0 R /Prev {prevStartXref} >>\n");
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

    /// <summary>ดึงค่า /ID [ &lt;hex&gt; &lt;hex&gt; ] จาก trailer ตัวสุดท้าย (คืนรวมวงเล็บ [])
    /// เพื่อคง file identifier เดิมใน incremental update (PDF/A บังคับ). null = ไม่พบ.</summary>
    private static string? FindTrailerId(string pdf)
    {
        var idx = pdf.LastIndexOf("/ID", StringComparison.Ordinal);
        if (idx < 0) return null;
        var open = pdf.IndexOf('[', idx);
        if (open < 0 || open - idx > 8) return null;   // /ID ต้องตามด้วย [ ใกล้ ๆ
        var close = pdf.IndexOf(']', open);
        if (close < 0) return null;
        return pdf.Substring(open, close - open + 1);   // "[<..> <..>]"
    }

    /// <summary>สแกน object header "N G obj" หา object number สูงสุด — ใช้เป็น
    /// fallback เมื่ออ่าน xref ไม่ได้ (กัน XML ไม่ถูกฝังเงียบ ๆ). ทนทานทุกรูปแบบ xref.</summary>
    private static int FindMaxObjNumberByHeaders(string pdf)
    {
        var max = 0;
        var idx = 0;
        while ((idx = pdf.IndexOf(" obj", idx, StringComparison.Ordinal)) >= 0)
        {
            // ถอยหลังอ่าน "N G" ก่อน " obj"
            var j = idx - 1;
            while (j >= 0 && (pdf[j] == ' ' || char.IsDigit(pdf[j]))) j--;   // ข้าม G + ช่องว่าง
            // ตอนนี้ j ชี้ก่อน token; หา N (ตัวเลขชุดแรกจาก j+1)
            var seg = pdf.Substring(j + 1, idx - (j + 1)).Trim();
            var parts = seg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && int.TryParse(parts[0], out var n) && n > max) max = n;
            idx += 4;
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

    private static string MergeCatalog(string originalCatalog, int fsObj, int nameTreeObj, int xmpObj)
    {
        // Strip leading << and trailing >>
        var body = originalCatalog.Trim();
        if (body.StartsWith("<<")) body = body.Substring(2);
        if (body.EndsWith(">>")) body = body.Substring(0, body.Length - 2);
        body = body.Trim();

        // Remove existing entries we will replace
        body = StripDictKey(body, "/AF");
        body = StripDictKey(body, "/Names");
        body = StripDictKey(body, "/AFRelationship");
        if (xmpObj > 0)
            body = StripDictKey(body, "/Metadata");

        var sb = new StringBuilder();
        sb.Append("<< ");
        sb.Append(body);
        sb.Append(' ');
        sb.Append($"/AF [{fsObj} 0 R] ");
        sb.Append($"/Names << /EmbeddedFiles {nameTreeObj} 0 R >> ");
        if (xmpObj > 0)
            sb.Append($"/Metadata {xmpObj} 0 R ");
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
            // Simple token — แต่ **ต้องรองรับ indirect reference "N G R" (3 tokens)**
            // เช่น "/Metadata 2 0 R". บั๊กเดิม: ตัดแค่ token แรก ("2") เหลือ " 0 R"
            // ค้างใน catalog → dictionary พัง → iText (สรรพากร) "ประมวลผลไม่ได้"
            // (pikepdf/qpdf ยอมรับได้เพราะ lenient แต่ iText strict).
            static bool IsWs(char c) => c == ' ' || c == '\r' || c == '\n' || c == '\t';
            var j = i;
            while (j < body.Length && !IsWs(body[j]) && body[j] != '/') j++;   // token 1 (obj num)
            // ถ้าตามด้วย "<ws><digits><ws>R" → เป็น indirect ref กินต่อให้ครบ
            var k = j;
            while (k < body.Length && IsWs(body[k])) k++;
            var g = k;
            while (g < body.Length && char.IsDigit(body[g])) g++;
            if (g > k)
            {
                var r = g;
                while (r < body.Length && IsWs(body[r])) r++;
                if (r < body.Length && body[r] == 'R') j = r + 1;   // กิน "N G R" ครบ
            }
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
