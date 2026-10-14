using System.Globalization;
using System.Text;
using Accounting.Models.Enums;
using MiniExcelLibs;

namespace Accounting.Helpers;

/// <summary>ปัญหาของ "ตัวไฟล์" ที่อ่านต่อไม่ได้ — <see cref="Code"/> ให้หน้าจอพาไปทางที่ถูก · ข้อความไทยบอกทางไปต่อเสมอ (F2 ข้อ 7/8) ·
/// เป็น <see cref="BusinessRuleException"/> ⇒ <c>ExceptionMiddleware</c> ตอบ 400 พร้อมข้อความไทย (RuleCode <c>IMPORT-FORMAT</c>) โดยไม่ต้อง catch รายจุด</summary>
public sealed class ImportFormatException : BusinessRuleException
{
    /// <summary>รหัสปัญหา (เช่น <c>"xls-legacy"</c> · <c>"encoding"</c> · <c>"too-many-rows"</c>) — ค่าคงที่ ไม่แปลตามภาษา</summary>
    public string Code { get; }
    /// <summary>ข้อความภาษาไทยถึงผู้ใช้ (= <see cref="Exception.Message"/>)</summary>
    public string ThaiMessage => Message;
    /// <summary>แถวที่พบปัญหา (1-based) — ตัวห่อที่ใช้ข้อความของตัวเอง (<c>SettlementFileReader</c>) ใช้ต่อ</summary>
    public int? RowNumber { get; init; }
    /// <summary>รายละเอียดทางเทคนิคจากตัวอ่าน (เช่นข้อความของ MiniExcel)</summary>
    public string? Detail { get; init; }

    public ImportFormatException(string code, string thaiMessage) : base(thaiMessage, ruleCode: "IMPORT-FORMAT") { Code = code; }
}

/// <summary>ไฟล์ที่เปิดเป็นตารางแล้ว — แถว (lazy · อ่านทีละแถว) + สิ่งที่ตัวอ่านตัดสินได้ (ตัวคั่น · encoding · ชีต)</summary>
/// <param name="Rows">แถวของไฟล์ (ไม่ตัดแถวว่าง) — ค่าแต่ละช่องตัดช่องว่างหัวท้ายแล้ว · เกินเพดาน ⇒ <see cref="ImportFormatException"/> ตอนอ่าน</param>
/// <param name="Kind">ชนิดที่ <see cref="ImportTabularReader.Sniff"/> ตัดสิน (Csv/Tsv/Xlsx)</param>
/// <param name="Delimiter">ตัวคั่นของ CSV · <c>null</c> = xlsx</param>
/// <param name="Encoding">"utf-8" · "utf-16" · "utf-16be" · "windows-874" · <c>null</c> = xlsx</param>
/// <param name="SheetName">ชีตที่อ่าน · <c>null</c> = CSV</param>
/// <param name="SheetNames">ชีตทั้งหมดในไฟล์ (ว่าง = CSV)</param>
public sealed record ImportTabularRows(
    IEnumerable<IReadOnlyList<string>> Rows,
    ImportFileKind Kind,
    char? Delimiter,
    string? Encoding,
    string? SheetName,
    IReadOnlyList<string> SheetNames);

/// <summary>
/// **อ่านไฟล์นำเข้าเป็นแถวของข้อความ — ตัวเดียวของทุกเส้นนำเข้า** (รอบ 205 T0 · IMPORT_ETL_REVIEW §2.3)
///
/// <para>ย้ายเนื้อมาจาก <c>SettlementFileReader</c> (รอบ 198–200 · ตัวที่ดีที่สุดของเรพ) — ตัวนั้นเหลือเป็น wrapper บาง ๆ ที่คง public API +
/// ข้อความของ settlement ไว้เหมือนเดิม · ทุกเส้นนำเข้าใหม่ (ศูนย์นำเข้าข้อมูล · ธนาคาร · โปรแกรมอื่น) ต้องอ่านไฟล์ผ่านที่นี่ ห้ามเขียนตัวอ่านใหม่</para>
///
/// <para>CSV: ถอดรหัส UTF-8 (มี/ไม่มี BOM) · UTF-16 (BOM) · Windows-874/TIS-620 — ตัดสินด้วยการถอด UTF-8 <b>แบบเข้มงวด</b>ก่อน
/// (อักขระเสีย U+FFFD ห้ามหลุดเข้าข้อมูล) · ตัวคั่น , ; tab จากบรรทัดแรกที่ไม่ว่าง · ค่าในเครื่องหมายคำพูดขึ้นบรรทัดใหม่ได้ · <c>""</c> = คำพูดในค่า</para>
/// <para>Excel (.xlsx): MiniExcel เท่านั้น (CLAUDE.md) · เลือกชีตได้ · ค่าตัวเลข/วันที่คงรูป invariant (<see cref="CellText"/>) ·
/// <c>.xls</c> (BIFF8) · XML · PDF · QIF ⇒ ล้มดังพร้อมทางไปต่อ (คำตัดสินข้อ 151) · ตัดสินชนิดจาก<b>เนื้อไฟล์</b> (<see cref="Sniff"/>) —
/// ธนาคารไทยหลายแห่งส่งออก "xls" ที่จริงเป็นข้อความคั่นแท็บ/HTML</para>
/// <para>G6: pure (ไม่มี I/O นอกจาก MemoryStream) · ไม่ขึ้นกับ culture ของเครื่อง</para>
/// </summary>
public static class ImportTabularReader
{
    /// <summary>ขนาดไฟล์สูงสุด — ตรงกับเพดานของ attachment abstraction (25 MB) เพราะไฟล์ต้นทางต้องเก็บได้</summary>
    public const long MaxFileBytes = 25L * 1024 * 1024;
    /// <summary>จำนวนแถวสูงสุดต่อไฟล์ (รวมหัวตาราง) — อ่านทีละแถวแล้วหยุดทันทีที่เกิน</summary>
    public const int MaxRows = 100_000;
    /// <summary>จำนวนคอลัมน์สูงสุดต่อแถว (Excel อ้างคอลัมน์ไกลสุด XFD ได้ ⇒ แถวเดียวกลายเป็น 16,384 ช่อง)</summary>
    public const int MaxColumns = 500;

    // ═══════════════════════ ตัดสินชนิดไฟล์ ═══════════════════════

    /// <summary>
    /// ชนิดไฟล์จาก<b>เนื้อไฟล์</b>ก่อน แล้วค่อยนามสกุล — zip = xlsx · OLE2 = xls (BIFF8) · <c>%PDF</c> · ข้อความที่ขึ้นต้น
    /// <c>OFXHEADER</c>/<c>&lt;OFX</c> = OFX · <c>!Type:</c> = QIF · <c>&lt;html</c>/<c>&lt;table</c> = HTML · <c>&lt;?xml</c>/<c>&lt;</c> = XML ·
    /// วงเล็บปีกกา/วงเล็บเหลี่ยม = JSON · นอกนั้นเป็นตาราง: แท็บมากกว่าจุลภาค/อัฒภาคในบรรทัดแรก (หรือนามสกุล .tsv) = TSV ไม่งั้น CSV ·
    /// ไบต์ไบนารีที่ไม่รู้จัก = <see cref="ImportFileKind.Unknown"/>
    /// </summary>
    public static ImportFileKind Sniff(string? fileName, byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04) return ImportFileKind.Xlsx;
        if (bytes.Length >= 8 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0
            && bytes[4] == 0xA1 && bytes[5] == 0xB1 && bytes[6] == 0x1A && bytes[7] == 0xE1) return ImportFileKind.Xls;
        if (bytes.Length >= 4 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46) return ImportFileKind.Pdf;

        var head = HeadText(bytes);
        if (head == null) return ImportFileKind.Unknown;
        var t = head.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (t.StartsWith("OFXHEADER", StringComparison.OrdinalIgnoreCase)) return ImportFileKind.Ofx;
        if (t.StartsWith("!Type:", StringComparison.OrdinalIgnoreCase) || t.StartsWith("!Account", StringComparison.OrdinalIgnoreCase))
            return ImportFileKind.Qif;
        if (t.StartsWith("<", StringComparison.Ordinal))
        {
            if (t.Contains("<OFX", StringComparison.OrdinalIgnoreCase)) return ImportFileKind.Ofx;
            if (t.StartsWith("<html", StringComparison.OrdinalIgnoreCase) || t.StartsWith("<table", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)) return ImportFileKind.Html;
            return ImportFileKind.Xml;
        }
        // (char)123 = ปีกกาเปิด — เลี่ยงตัวอักษรจริงในสตริงให้ตัวนับปีกกาของ tools/check_all.sh ไม่ฟ้องผิด
        if (t.Length > 0 && (t[0] == (char)123 || t[0] == '[')) return ImportFileKind.Json;

        var end = t.IndexOf('\n');
        var first = end < 0 ? t : t[..end];
        var tabs = first.Count(ch => ch == '\t');
        if ((tabs > 0 && tabs >= first.Count(ch => ch == ',') && tabs >= first.Count(ch => ch == ';'))
            || (fileName ?? "").EndsWith(".tsv", StringComparison.OrdinalIgnoreCase))
            return ImportFileKind.Tsv;
        return ImportFileKind.Csv;
    }

    /// <summary>ข้อความต้นไฟล์ (≤ 4 KB) สำหรับดูลายเซ็น — UTF-16 ตาม BOM · ไบต์ NUL ปนโดยไม่มี BOM = ไบนารี (<c>null</c>)</summary>
    private static string? HeadText(byte[] bytes)
    {
        var n = Math.Min(bytes.Length, 4096);
        if (n >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, (n - 2) & ~1);
        if (n >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, (n - 2) & ~1);
        for (var i = 0; i < n; i++) if (bytes[i] == 0) return null;
        // ลายเซ็นที่ดูเป็น ASCII ทั้งหมด ⇒ ถอดแบบหลวมได้ (ไม่ได้ใช้เป็นข้อมูล)
        return Encoding.UTF8.GetString(bytes, 0, n);
    }

    // ═══════════════════════ ชีต ═══════════════════════

    /// <summary>ชื่อชีตทั้งหมดของไฟล์ xlsx ตามลำดับในไฟล์ · ไฟล์ที่ไม่ใช่ xlsx ⇒ ว่าง · อ่านไม่ได้ ⇒ <see cref="ImportFormatException"/></summary>
    public static IReadOnlyList<string> ListSheets(byte[] bytes)
    {
        if (Sniff(null, bytes) != ImportFileKind.Xlsx) return Array.Empty<string>();
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return MiniExcel.GetSheetNames(stream);
        }
        catch (Exception ex)
        {
            throw XlsxUnreadable(ex);
        }
    }

    // ═══════════════════════ เปิดเป็นตาราง ═══════════════════════

    /// <summary>
    /// เปิดไฟล์เป็นตาราง — ตัดสินชนิดจากเนื้อไฟล์ (<see cref="Sniff"/>) · xlsx เลือก <paramref name="sheetName"/> ได้ (ไม่ระบุ = ชีตแรก ·
    /// ชื่อที่ไม่มีในไฟล์ = ล้มดังพร้อมรายชื่อชีต) · ชนิดที่ไม่ใช่ตาราง ⇒ <see cref="ImportFormatException"/> พร้อมทางไปต่อ
    /// </summary>
    public static ImportTabularRows Open(string? fileName, byte[] bytes, string? sheetName = null)
    {
        if (bytes.Length == 0)
            throw new ImportFormatException("empty-file", "ไฟล์ว่างเปล่า — ส่งออกข้อมูลจากโปรแกรมต้นทางใหม่แล้วอัปโหลดอีกครั้ง");
        if (bytes.LongLength > MaxFileBytes)
            throw new ImportFormatException("file-too-large",
                $"ไฟล์ใหญ่เกิน {MaxFileBytes / (1024 * 1024)} MB — แบ่งไฟล์ (ช่วงวันที่สั้นลง/ทีละชีต) แล้วนำเข้าทีละไฟล์");

        var kind = Sniff(fileName, bytes);
        switch (kind)
        {
            case ImportFileKind.Xlsx:
            {
                var sheets = ListSheets(bytes);
                string? sheet = null;
                if (!string.IsNullOrWhiteSpace(sheetName))
                {
                    sheet = sheets.FirstOrDefault(s => string.Equals(s, sheetName.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (sheet == null)
                        throw new ImportFormatException("sheet-not-found",
                            $"ไม่พบชีต \"{sheetName}\" ในไฟล์ — ชีตที่มี: {string.Join(", ", sheets)} · เลือกชีตใหม่แล้ววิเคราะห์อีกครั้ง");
                }
                else if (sheets.Count > 0) sheet = sheets[0];
                return new ImportTabularRows(Capped(ReadExcel(bytes, sheet), MaxRows, MaxColumns), kind, null, null, sheet, sheets);
            }
            case ImportFileKind.Csv:
            case ImportFileKind.Tsv:
            {
                var (text, encoding) = DecodeWithName(bytes);
                var body = StripBom(text);
                return new ImportTabularRows(Capped(ReadCsv(body), MaxRows, MaxColumns), kind, DetectDelimiter(body), encoding,
                    null, Array.Empty<string>());
            }
            case ImportFileKind.Xls:
                throw new ImportFormatException("xls-legacy",
                    "ไฟล์ .xls (Excel 97–2003) ระบบอ่านไม่ได้ — เปิดไฟล์ใน Excel แล้ว \"บันทึกเป็น\" ชนิด .xlsx (Excel Workbook) แล้วอัปโหลดใหม่");
            case ImportFileKind.Html:
                throw new ImportFormatException("html-disguised",
                    "ไฟล์นี้เป็นหน้าเว็บ (HTML) ที่ตั้งชื่อเหมือนไฟล์ Excel — เปิดไฟล์ใน Excel แล้ว \"บันทึกเป็น\" .xlsx หรือ CSV UTF-8 แล้วอัปโหลดใหม่");
            case ImportFileKind.Xml:
                throw new ImportFormatException("xml-use-ocr",
                    "ไฟล์ XML (เช่น e-Tax Invoice) ไม่ใช่ตาราง — นำเข้าใบกำกับภาษีอิเล็กทรอนิกส์ที่หน้า \"สแกนเอกสาร (OCR)\" ซึ่งอ่าน XML ของ e-Tax ได้");
            case ImportFileKind.Pdf:
                throw new ImportFormatException("pdf-unsupported",
                    "ยังไม่รองรับการนำเข้าตารางจาก PDF — ขอไฟล์ CSV/Excel จากโปรแกรมต้นทางหรือธนาคาร · ถ้าเป็นใบกำกับ/ใบเสร็จ ใช้หน้า \"สแกนเอกสาร (OCR)\"");
            case ImportFileKind.Ofx:
                throw new ImportFormatException("ofx-bank-only",
                    "ไฟล์ OFX เป็น statement ธนาคาร — นำเข้าที่หน้า \"ธนาคาร → นำเข้า statement\" (ไม่ใช่ตาราง)");
            case ImportFileKind.Qif:
                throw new ImportFormatException("qif-unsupported",
                    "ยังไม่รองรับไฟล์ QIF — ส่งออก statement เป็น CSV/Excel หรือ OFX แทนแล้วอัปโหลดใหม่");
            case ImportFileKind.Json:
                throw new ImportFormatException("json-unsupported",
                    "ยังไม่รองรับการนำเข้าไฟล์ JSON ที่หน้านี้ — ใช้ API (v1) สำหรับข้อมูลจากระบบอื่น หรือส่งออกเป็น CSV/Excel");
            default:
                throw new ImportFormatException("unsupported-type",
                    "ไม่รู้จักชนิดไฟล์นี้ — รองรับ .csv · .tsv · .txt (คั่นด้วยจุลภาค/แท็บ) และ .xlsx");
        }
    }

    /// <summary>ตัดการอ่านทันทีที่แถว/คอลัมน์เกินเพดาน (lazy — แถวที่เกินไม่ถูกอ่านต่อ) — ข้อความบอกทางไปต่อ (F2 ข้อ 8)</summary>
    public static IEnumerable<IReadOnlyList<string>> Capped(IEnumerable<IReadOnlyList<string>> rows, int maxRows, int maxColumns)
    {
        var n = 0;
        foreach (var r in rows)
        {
            if (++n > maxRows)
                throw new ImportFormatException("too-many-rows",
                    $"ไฟล์มีมากกว่า {maxRows:N0} แถว — แบ่งไฟล์ (ช่วงวันที่สั้นลง) แล้วนำเข้าทีละไฟล์") { RowNumber = n };
            if (r.Count > maxColumns)
                throw new ImportFormatException("too-many-columns",
                    $"แถวที่ {n:N0} มีมากกว่า {maxColumns:N0} คอลัมน์ — ไฟล์อาจเสีย/มีข้อมูลค้างที่คอลัมน์ไกล ๆ ลบคอลัมน์ที่ไม่ใช้แล้วบันทึกใหม่")
                { RowNumber = n };
            yield return r;
        }
    }

    // ═══════════════════════ ถอดรหัสข้อความ ═══════════════════════

    /// <summary>ถอดรหัสข้อความ CSV — ถอดไม่ได้ทั้ง UTF-8 และ Windows-874 ⇒ ล้มดัง (ไม่เดา)</summary>
    public static string Decode(byte[] bytes) => DecodeWithName(bytes).Text;

    /// <summary>ถอดรหัส + ชื่อ encoding ที่ใช้ ("utf-8" · "utf-16" · "utf-16be" · "windows-874") — ออกทาง <see cref="ImportTabularRows.Encoding"/> ของ <see cref="Open"/></summary>
    private static (string Text, string Encoding) DecodeWithName(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "utf-8");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16be");
        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), "utf-8");
        }
        catch (DecoderFallbackException)
        {
            // ไฟล์ CSV จาก Excel ภาษาไทยรุ่นเก่า/Express = Windows-874 (TIS-620) — เรียก provider ตรง ไม่ต้อง RegisterProvider ทั้ง process ·
            // ถอดแบบเข้มงวดเหมือนกัน (ไบต์ที่ไม่มีในตาราง 874 = ไม่ใช่ไฟล์ไทย ⇒ ล้มดัง ไม่ใช่อักขระเสียเงียบ)
            var thai = CodePagesEncodingProvider.Instance.GetEncoding(874, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            if (thai != null)
            {
                try { return (thai.GetString(bytes), "windows-874"); }
                catch (DecoderFallbackException) { /* ตกไปล้มดังข้างล่าง */ }
            }
            throw new ImportFormatException("encoding",
                "อ่านอักขระในไฟล์ไม่ได้ (ไม่ใช่ UTF-8 หรือ TIS-620) — เปิดไฟล์ใน Excel แล้ว \"บันทึกเป็น CSV UTF-8\" หรือบันทึกเป็น .xlsx");
        }
    }

    // ═══════════════════════ CSV ═══════════════════════

    /// <summary>แยก CSV ทีละแถว — ค่าในเครื่องหมายคำพูดข้ามบรรทัดได้ · คำพูดไม่ปิดจนจบไฟล์ ⇒ ล้มดัง</summary>
    public static IEnumerable<IReadOnlyList<string>> ReadCsv(string content)
    {
        content = StripBom(content);
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
            throw new ImportFormatException("csv-quote",
                "ไฟล์ CSV มีเครื่องหมายคำพูด (\") ที่ไม่ปิด — ไฟล์อาจถูกตัดกลางทาง ส่งออกข้อมูลจากโปรแกรมต้นทางใหม่");
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString().Trim());
            yield return row;
        }
    }

    private static string StripBom(string content) => content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;

    /// <summary>ตัวคั่นจากบรรทัดแรก: แท็บ &gt; จุลภาค และ ≥ อัฒภาค ⇒ แท็บ · อัฒภาค &gt; จุลภาค ⇒ อัฒภาค (ไฟล์รูปแบบยุโรป — จุลภาค = ทศนิยม) · นอกนั้นจุลภาค</summary>
    public static char DetectDelimiter(string content)
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

    // ═══════════════════════ Excel ═══════════════════════

    /// <summary>อ่านชีตของ xlsx ทีละแถว (<paramref name="sheetName"/> = null ⇒ ชีตแรก) — ข้อผิดพลาดของไฟล์แปลงเป็นข้อความไทยทุกจุด</summary>
    public static IEnumerable<IReadOnlyList<string>> ReadExcel(byte[] bytes, string? sheetName = null)
    {
        // MiniExcel อ่านแบบ lazy (ทีละแถว) — ข้อผิดพลาดของไฟล์โผล่ตอน MoveNext ⇒ แปลงเป็นข้อความไทยทุกจุด
        using var stream = new MemoryStream(bytes, writable: false);
        IEnumerator<IDictionary<string, object>> it;
        try
        {
            it = MiniExcel.Query(stream, useHeaderRow: false, sheetName: sheetName).Cast<IDictionary<string, object>>().GetEnumerator();
        }
        catch (Exception ex)
        {
            throw XlsxUnreadable(ex);
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
                    throw XlsxUnreadable(ex);
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

    private static ImportFormatException XlsxUnreadable(Exception ex)
        => new("xlsx-unreadable", $"อ่านไฟล์ Excel ไม่ได้ — ตรวจว่าเป็น .xlsx ที่ไม่ได้ตั้งรหัสผ่าน ({ex.Message})") { Detail = ex.Message };

    /// <summary>ค่าในช่อง Excel → ข้อความที่ตัวแปลงค่าอ่านต่อได้แน่นอน (ไม่ขึ้นกับ culture ของเครื่อง)</summary>
    public static string CellText(object? v) => v switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        // R-B7: เลข ≥ 1e15 ในเซลล์ตัวเลขของ Excel เสียหลักท้ายไปแล้ว (Excel เก็บ 15 หลัก) — คงรูป scientific ให้ด่านเลขอ้างอิงเห็น
        // (SettlementValueParser.IdLostPrecision · ImportValueNormalizer.TaxId) แทนที่จะพิมพ์เลขเต็มที่ดูถูกแต่หลักท้ายผิด
        decimal m when Math.Abs(m) >= 1e15m => ((double)m).ToString("R", CultureInfo.InvariantCulture),
        long l when l >= 1_000_000_000_000_000L || l <= -1_000_000_000_000_000L => ((double)l).ToString("R", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture).Trim(),
        _ => (v.ToString() ?? "").Trim(),
    };

    /// <summary>"A" → 0 · "AB" → 27 · คีย์ที่ไม่ใช่ตัวอักษรคอลัมน์ ⇒ -1</summary>
    public static int ColumnIndex(string key)
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
