using System.Globalization;
using System.Text;
using MiniExcelLibs;

namespace Accounting.Services.Settlement.Adapters;

/// <summary>
/// **อ่านไฟล์ settlement เป็นแถวของข้อความ — ทีละแถว (ไม่โหลดทั้งตารางเข้าหน่วยความจำ)**
///
/// <para>CSV: ถอดรหัส UTF-8 (มี/ไม่มี BOM) · UTF-16 (BOM) · Windows-874/TIS-620 (ไฟล์ Excel ไทยที่ "บันทึกเป็น CSV") —
/// ตัดสินด้วยการถอด UTF-8 แบบเข้มงวดก่อน ห้ามถอดแบบหลวม (อักขระเสีย U+FFFD จะกลายเป็นป้ายประเภทที่ไม่มีใครรู้จัก) ·
/// ตัวคั่น , ; tab ตัดสินจากบรรทัดแรกที่ไม่ว่าง · ค่าในเครื่องหมายคำพูดขึ้นบรรทัดใหม่ได้ · <c>""</c> = คำพูดในค่า</para>
/// <para>Excel (.xlsx): MiniExcel เท่านั้น (CLAUDE.md "Excel ใช้ MiniExcel") · แผ่นแรก · ค่าตัวเลข/วันที่คงรูป invariant
/// (ตัวแปลงค่าใน <see cref="SettlementValueParser"/> อ่านต่อ)</para>
/// </summary>
public static class SettlementFileReader
{
    /// <summary>ขนาดไฟล์สูงสุด — ตรงกับเพดานของ attachment abstraction (25 MB) เพราะไฟล์ต้นฉบับต้องเก็บได้</summary>
    public const long MaxFileBytes = 25L * 1024 * 1024;

    /// <summary>แถวของไฟล์ (ไม่ตัดแถวว่าง — ผู้เรียกข้ามเอง) · ค่าแต่ละช่องตัดช่องว่างหัวท้ายแล้ว</summary>
    public static IEnumerable<IReadOnlyList<string>> ReadRows(SettlementFileInput file)
    {
        if (file.Content.Length == 0)
            throw new SettlementFormatException("empty-file", "ไฟล์ว่างเปล่า — ส่งออกรายงานจากแพลตฟอร์มใหม่แล้วอัปโหลดอีกครั้ง");
        if (file.Content.LongLength > MaxFileBytes)
            throw new SettlementFormatException("file-too-large",
                $"ไฟล์ใหญ่เกิน {MaxFileBytes / (1024 * 1024)} MB — ส่งออกรายงานทีละรอบโอน (ช่วงวันที่สั้นลง) แล้วนำเข้าทีละไฟล์");
        if (file.IsExcel) return ReadExcel(file.Content);
        if (file.IsCsv) return ReadCsv(Decode(file.Content));
        throw new SettlementFormatException("unsupported-type",
            "รองรับเฉพาะไฟล์ .csv และ .xlsx — ถ้าเป็น .xls ให้เปิดใน Excel แล้วบันทึกเป็น .xlsx ก่อน");
    }

    /// <summary>ถอดรหัสข้อความ CSV — ถอดไม่ได้ทั้ง UTF-8 และ Windows-874 ⇒ ล้มดัง (ไม่เดา)</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // ไฟล์ CSV จาก Excel ภาษาไทยรุ่นเก่า = Windows-874 — เรียก provider ตรง ไม่ต้อง RegisterProvider ทั้ง process ·
            // ถอดแบบเข้มงวดเหมือนกัน (ไบต์ที่ไม่มีในตาราง 874 = ไม่ใช่ไฟล์ไทย ⇒ ล้มดัง ไม่ใช่อักขระเสียเงียบ)
            var thai = CodePagesEncodingProvider.Instance.GetEncoding(874, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            if (thai != null)
            {
                try { return thai.GetString(bytes); }
                catch (DecoderFallbackException) { /* ตกไปล้มดังข้างล่าง */ }
            }
            throw new SettlementFormatException("encoding",
                "อ่านอักขระในไฟล์ไม่ได้ (ไม่ใช่ UTF-8 หรือ TIS-620) — เปิดไฟล์ใน Excel แล้ว \"บันทึกเป็น CSV UTF-8\" หรือบันทึกเป็น .xlsx");
        }
    }

    /// <summary>แยก CSV ทีละแถว — ค่าในเครื่องหมายคำพูดข้ามบรรทัดได้ · คำพูดไม่ปิดจนจบไฟล์ ⇒ ล้มดัง</summary>
    public static IEnumerable<IReadOnlyList<string>> ReadCsv(string content)
    {
        if (content.Length > 0 && content[0] == '﻿') content = content[1..];
        var delimiter = DetectDelimiter(content);
        var cell = new StringBuilder();
        var row = new List<string>();
        var inQuotes = false;
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { cell.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                cell.Append(c); i++; continue;
            }
            if (c == '"' && cell.ToString().Trim().Length == 0) { cell.Clear(); inQuotes = true; i++; continue; }
            if (c == delimiter) { row.Add(cell.ToString().Trim()); cell.Clear(); i++; continue; }
            if (c == '\r' || c == '\n')
            {
                row.Add(cell.ToString().Trim()); cell.Clear();
                yield return row;
                row = new List<string>();
                if (c == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                i++; continue;
            }
            cell.Append(c); i++;
        }
        if (inQuotes)
            throw new SettlementFormatException("csv-quote",
                "ไฟล์ CSV มีเครื่องหมายคำพูด (\") ที่ไม่ปิด — ไฟล์อาจถูกตัดกลางทาง ส่งออกรายงานจากแพลตฟอร์มใหม่");
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString().Trim());
            yield return row;
        }
    }

    private static char DetectDelimiter(string content)
    {
        var end = content.IndexOf('\n');
        var first = end < 0 ? content : content[..end];
        var commas = first.Count(ch => ch == ',');
        var tabs = first.Count(ch => ch == '\t');
        var semis = first.Count(ch => ch == ';');
        if (tabs > commas && tabs >= semis) return '\t';
        if (semis > commas) return ';';
        return ',';
    }

    private static IEnumerable<IReadOnlyList<string>> ReadExcel(byte[] bytes)
    {
        // MiniExcel อ่านแบบ lazy (ทีละแถว) — ข้อผิดพลาดของไฟล์โผล่ตอน MoveNext ⇒ แปลงเป็นข้อความไทยทุกจุด
        using var stream = new MemoryStream(bytes, writable: false);
        IEnumerator<IDictionary<string, object>> it;
        try
        {
            it = MiniExcel.Query(stream, useHeaderRow: false).Cast<IDictionary<string, object>>().GetEnumerator();
        }
        catch (Exception ex)
        {
            throw new SettlementFormatException("xlsx-unreadable",
                $"อ่านไฟล์ Excel ไม่ได้ — ตรวจว่าเป็น .xlsx ที่ไม่ได้ตั้งรหัสผ่าน ({ex.Message})");
        }
        using (it)
        {
            while (true)
            {
                bool has;
                IDictionary<string, object>? r = null;
                try
                {
                    has = it.MoveNext();
                    if (has) r = it.Current;
                }
                catch (Exception ex)
                {
                    throw new SettlementFormatException("xlsx-unreadable",
                        $"อ่านไฟล์ Excel ไม่ได้ — ตรวจว่าเป็น .xlsx ที่ไม่ได้ตั้งรหัสผ่าน ({ex.Message})");
                }
                if (!has || r == null) yield break;
                var max = -1;
                foreach (var k in r.Keys) max = Math.Max(max, ColumnIndex(k));
                var cells = new string[max + 1];
                for (var c = 0; c < cells.Length; c++) cells[c] = "";
                foreach (var kv in r)
                {
                    var idx = ColumnIndex(kv.Key);
                    if (idx >= 0) cells[idx] = CellText(kv.Value);
                }
                yield return cells;
            }
        }
    }

    /// <summary>ค่าในช่อง Excel → ข้อความที่ตัวแปลงค่าอ่านต่อได้แน่นอน (ไม่ขึ้นกับ culture ของเครื่อง)</summary>
    internal static string CellText(object? v) => v switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture).Trim(),
        _ => (v.ToString() ?? "").Trim(),
    };

    /// <summary>"A" → 0 · "AB" → 27 · คีย์ที่ไม่ใช่ตัวอักษรคอลัมน์ ⇒ -1</summary>
    internal static int ColumnIndex(string key)
    {
        if (string.IsNullOrEmpty(key)) return -1;
        var n = 0;
        foreach (var ch in key)
        {
            var u = char.ToUpperInvariant(ch);
            if (u < 'A' || u > 'Z') return -1;
            n = n * 26 + (u - 'A' + 1);
        }
        return n - 1;
    }
}
