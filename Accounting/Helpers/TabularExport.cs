using System.Globalization;
using System.Text;
using MiniExcelLibs;

namespace Accounting.Helpers;

/// <summary>รูปแบบไฟล์ส่งออกตาราง — ฝั่งเซิร์ฟเวอร์</summary>
public enum TabularFormat { Csv, Xlsx }

/// <summary>ชนิดของช่อง — ตัดสินทั้ง "ค่าใน CSV เป็นข้อความหน้าตาอย่างไร" และ
/// "ช่องใน Excel เป็นตัวเลข/วันที่/ข้อความ" จากที่เดียว (กฎเหล็ก #4 F2 ข้อ 4)</summary>
public enum TabularCellKind
{
    /// <summary>ข้อความ — CSV ผ่าน <see cref="CsvFieldSafety"/> (กันสูตร) · Excel = ช่องข้อความ</summary>
    Text,
    /// <summary>จำนวนเงิน — ปัด 2 ตำแหน่ง AwayFromZero · CSV <c>-1500.00</c> (ไม่มีคั่นหลักพัน ไม่เติม <c>'</c>) · Excel = ตัวเลข</summary>
    Money,
    /// <summary>ตัวเลขทั่วไป (จำนวน/อัตรา/ราคาต่อหน่วย) — ไม่ปัด · CSV invariant · Excel = ตัวเลข</summary>
    Number,
    /// <summary>จำนวนเต็ม (จำนวนวัน/ลำดับ)</summary>
    Integer,
    /// <summary>วันที่ตามปฏิทินไทย ปี ค.ศ. — CSV <c>yyyy-MM-dd</c> (ISO นำเข้ากลับได้) · Excel = ช่องวันที่จริง</summary>
    Date,
    /// <summary>วันที่ <c>dd/MM/yyyy</c> ปี พ.ศ. — ข้อความทั้งสองรูปแบบ (รายงานทางการที่ต้องเป็น พ.ศ.)</summary>
    DateThai,
    /// <summary>เวลาประทับ (UTC ใน DB) → แสดงเวลาไทย <c>yyyy-MM-dd HH:mm:ss</c> ทั้งสองรูปแบบ</summary>
    Timestamp,
}

/// <summary>นิยามคอลัมน์หนึ่งคอลัมน์: หัวตาราง (ไทย) + ตัวดึงค่าจากแถว + ชนิดช่อง</summary>
public sealed record TabularColumn<T>(string Header, Func<T, object?> Value, TabularCellKind Kind = TabularCellKind.Text);

/// <summary>ตารางหนึ่งแผ่นที่แปลงค่าเรียบร้อยแล้ว (ไม่ผูกกับชนิดแถว) — สร้างผ่าน
/// <see cref="TabularExport.Sheet{T}"/> เท่านั้น เพื่อให้หัวตารางกับชนิดช่องเดินคู่กันเสมอ</summary>
public sealed class TabularSheet
{
    internal TabularSheet(string name, IReadOnlyList<string> headers, IReadOnlyList<TabularCellKind> kinds,
        IReadOnlyList<object?[]> rows, IReadOnlyList<object?[]> footer, IReadOnlyList<string> title)
    {
        Name = name; Headers = headers; Kinds = kinds; Rows = rows; Footer = footer; Title = title;
    }

    /// <summary>ชื่อแผ่นงาน (Excel ตัดเหลือ 31 ตัวอักษร)</summary>
    public string Name { get; }
    public IReadOnlyList<string> Headers { get; }
    public IReadOnlyList<TabularCellKind> Kinds { get; }
    /// <summary>แถวข้อมูล (ค่าดิบก่อนจัดรูป — เทสต์เทียบกับผลรายงานได้ตรง ๆ)</summary>
    public IReadOnlyList<object?[]> Rows { get; }
    /// <summary>แถวท้าย (ยอดรวม) — ต่อท้ายตารางทั้ง CSV และ Excel</summary>
    public IReadOnlyList<object?[]> Footer { get; }
    /// <summary>บรรทัดหัวรายงาน (ชื่อบริษัท/ชื่อรายงาน/ช่วงเวลา) — ใส่เฉพาะ Excel
    /// (CSV ขึ้นต้นด้วยหัวตารางเสมอ เพื่อให้เครื่องอ่านต่อได้ — ช่วงเวลาอยู่ในชื่อไฟล์)</summary>
    public IReadOnlyList<string> Title { get; }
}

/// <summary>ไฟล์ที่พร้อมส่งให้ <c>File(...)</c> ของ controller</summary>
public sealed record TabularFile(byte[] Content, string ContentType, string FileName);

/// <summary>
/// **ตัวเขียนไฟล์ตาราง (CSV / Excel) ฝั่งเซิร์ฟเวอร์ตัวเดียวของระบบ** — คอลัมน์ + แถว → ไบต์
///
/// <para>ทำไมต้องมีตัวกลาง: ก่อนหน้านี้แต่ละหน้าส่งออกเองจาก JS (<c>ExportUtil</c>)
/// โดยใช้ "แถวที่หน้าโหลดมาแล้ว" ⇒ ได้แค่หน้าปัจจุบัน (page size 20/50), CSV ไม่กันสูตร,
/// ตัวเลขกลายเป็นข้อความที่จัดรูปแล้ว (<c>1,234.00</c>) และยอดบางหน้าคำนวณใหม่ใน JS
/// ไม่ตรงกับรายงานเซิร์ฟเวอร์. ตัวนี้รับแถวจาก <b>service เดียวกับที่หน้าแสดง</b> แล้วเขียนไฟล์</para>
///
/// <para>กติกา CSV: UTF-8 มี BOM (Excel ไทยเปิดถูก) · ขึ้นบรรทัด CRLF · ข้อความผ่าน
/// <see cref="CsvFieldSafety.Escape"/> (กันสูตร + ใส่อัญประกาศ) · ตัวเลขเขียนตรง invariant
/// <b>ไม่ผ่านตัวกันสูตร</b> — ค่า decimal ไม่มีทางเป็นสูตร และยอดติดลบ <c>-1500.00</c>
/// ต้องเปิดใน Excel แล้วเป็นตัวเลข ไม่ใช่ข้อความ <c>'-1500.00</c></para>
///
/// <para>กติกา Excel (MiniExcel เท่านั้น): หัวรายงาน (ถ้ามี) → บรรทัดว่าง → หัวตาราง →
/// ข้อมูล → แถวท้าย · ตัวเลขเป็นช่องตัวเลข · <see cref="TabularCellKind.Date"/> เป็นช่องวันที่ ·
/// ไม่เติม <c>'</c> หน้าข้อความ (ช่องข้อความใน xlsx ไม่ถูกตีความเป็นสูตร)</para>
/// </summary>
public static class TabularExport
{
    public const string CsvContentType = "text/csv; charset=utf-8";
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>อ่านพารามิเตอร์ <c>format</c> จาก query (<c>csv</c> / <c>xlsx</c> / <c>excel</c>)
    /// — ค่าอื่น = false (controller ตอบ 400 ไม่ใช่เดาเป็น CSV เงียบ ๆ)</summary>
    public static bool TryParseFormat(string? raw, out TabularFormat format)
    {
        switch ((raw ?? "").Trim().ToLowerInvariant())
        {
            case "csv": format = TabularFormat.Csv; return true;
            case "xlsx":
            case "excel": format = TabularFormat.Xlsx; return true;
            default: format = TabularFormat.Csv; return false;
        }
    }

    /// <summary>สร้างแผ่นงานจากคอลัมน์ + แถว · <paramref name="footer"/> = แถวยอดรวม
    /// (ใช้คอลัมน์ชุดเดียวกัน — ยอดต้องมาจาก service ไม่ใช่รวมใหม่ที่นี่)</summary>
    public static TabularSheet Sheet<T>(string name, IReadOnlyList<TabularColumn<T>> columns, IEnumerable<T> rows,
        IEnumerable<T>? footer = null, IEnumerable<string>? title = null)
    {
        if (columns == null || columns.Count == 0)
            throw new ArgumentException("ต้องมีอย่างน้อยหนึ่งคอลัมน์", nameof(columns));
        object?[] Project(T r) => columns.Select(c => c.Value(r)).ToArray();
        return new TabularSheet(
            string.IsNullOrWhiteSpace(name) ? "ข้อมูล" : name,
            columns.Select(c => c.Header).ToList(),
            columns.Select(c => c.Kind).ToList(),
            (rows ?? Enumerable.Empty<T>()).Select(Project).ToList(),
            (footer ?? Enumerable.Empty<T>()).Select(Project).ToList(),
            (title ?? Enumerable.Empty<string>()).Where(t => t != null).ToList());
    }

    /// <summary>ค่าหนึ่งช่องในรูปข้อความ CSV (ยังไม่ใส่อัญประกาศ/กันสูตร — <see cref="ToCsv"/> ทำต่อ)</summary>
    internal static string FormatCell(object? value, TabularCellKind kind)
    {
        if (value == null) return "";
        if (value is string s && kind != TabularCellKind.Text) return s;   // ค่าแทน เช่น "—" ในช่องตัวเลข
        var inv = CultureInfo.InvariantCulture;
        switch (kind)
        {
            case TabularCellKind.Money:
                return Math.Round(ToDecimal(value), 2, MidpointRounding.AwayFromZero).ToString("0.00", inv);
            case TabularCellKind.Number:
                return ToDecimal(value).ToString("0.############", inv);
            case TabularCellKind.Integer:
                return Convert.ToInt64(value, inv).ToString(inv);
            case TabularCellKind.Date:
                return value is DateTime d ? ThaiDate.CalendarDateUtc(d).ToString("yyyy-MM-dd", inv) : Convert.ToString(value, inv) ?? "";
            case TabularCellKind.DateThai:
                return value is DateTime dt ? ThaiDate.ToThaiDisplayString(dt) : Convert.ToString(value, inv) ?? "";
            case TabularCellKind.Timestamp:
                return value is DateTime ts ? ToBangkok(ts).ToString("yyyy-MM-dd HH:mm:ss", inv) : Convert.ToString(value, inv) ?? "";
            default:
                return value switch
                {
                    IFormattable f => f.ToString(null, inv),
                    _ => value.ToString() ?? "",
                };
        }
    }

    /// <summary>CSV หนึ่งแผ่น: BOM + หัวตาราง + ข้อมูล + แถวท้าย · CRLF</summary>
    internal static byte[] ToCsv(TabularSheet sheet)
    {
        var sb = new StringBuilder();
        sb.Append(CsvFieldSafety.Row(sheet.Headers)).Append("\r\n");
        foreach (var row in sheet.Rows.Concat(sheet.Footer))
            sb.Append(CsvRow(row, sheet.Kinds)).Append("\r\n");
        var bom = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var all = new byte[bom.Length + body.Length];
        bom.CopyTo(all, 0);
        body.CopyTo(all, bom.Length);
        return all;
    }

    private static string CsvRow(object?[] row, IReadOnlyList<TabularCellKind> kinds)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < kinds.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var v = i < row.Length ? row[i] : null;
            var kind = kinds[i];
            var text = FormatCell(v, kind);
            // ตัวเลขที่จัดรูปแล้ว (มีแต่ 0-9 . -) เขียนตรง — ไม่ผ่านตัวกันสูตรเพื่อไม่ให้ยอดติดลบกลายเป็นข้อความ
            // ค่าแทนที่เป็นข้อความในช่องตัวเลข (เช่น "—") ยังผ่าน CsvFieldSafety ตามปกติ
            sb.Append(IsNumericKind(kind) && v is not string ? text : CsvFieldSafety.Escape(text));
        }
        return sb.ToString();
    }

    /// <summary>Excel หลายแผ่นในไฟล์เดียว (ชื่อแผ่นซ้ำ → เติมเลขต่อท้าย)</summary>
    internal static byte[] ToXlsx(params TabularSheet[] sheets)
    {
        if (sheets == null || sheets.Length == 0)
            throw new ArgumentException("ต้องมีอย่างน้อยหนึ่งแผ่นงาน", nameof(sheets));
        var book = new Dictionary<string, object>();
        foreach (var sh in sheets)
        {
            var width = sh.Headers.Count;
            var rows = new List<Dictionary<string, object?>>();
            foreach (var t in sh.Title) rows.Add(XRow(width, new object?[] { t }));
            if (sh.Title.Count > 0) rows.Add(XRow(width, Array.Empty<object?>()));
            rows.Add(XRow(width, sh.Headers.Cast<object?>().ToArray()));
            foreach (var r in sh.Rows.Concat(sh.Footer))
                rows.Add(XRow(width, r.Select((v, i) => XlsxValue(v, i < sh.Kinds.Count ? sh.Kinds[i] : TabularCellKind.Text)).ToArray()));
            var name = SheetName(sh.Name);
            var unique = name; var n = 2;
            while (book.ContainsKey(unique)) unique = SheetName(name.Length > 27 ? name[..27] : name) + " " + n++;
            book[unique] = rows;
        }
        using var ms = new MemoryStream();
        // printHeader:false — หัวตารางเราใส่เองเป็นแถว (เพื่อวางหัวรายงานไว้เหนือหัวตารางได้)
        ms.SaveAs(book, printHeader: false, excelType: ExcelType.XLSX);
        return ms.ToArray();
    }

    /// <summary>ไฟล์พร้อมส่ง: CSV ใช้ได้กับแผ่นเดียวเท่านั้น (หลายแผ่น = ผู้เรียกผิด → โยนดัง)</summary>
    public static TabularFile Render(TabularFormat format, string baseName, DateTime? from, DateTime? to, params TabularSheet[] sheets)
    {
        if (sheets == null || sheets.Length == 0)
            throw new ArgumentException("ต้องมีอย่างน้อยหนึ่งแผ่นงาน", nameof(sheets));
        if (format == TabularFormat.Csv)
        {
            if (sheets.Length != 1)
                throw new ArgumentException("CSV ส่งออกได้ทีละแผ่นงาน — ใช้ xlsx สำหรับรายงานหลายแผ่น", nameof(sheets));
            return new TabularFile(ToCsv(sheets[0]), CsvContentType, FileName(baseName, from, to, format));
        }
        return new TabularFile(ToXlsx(sheets), XlsxContentType, FileName(baseName, from, to, format));
    }

    /// <summary>ชื่อไฟล์: <c>{ชื่อ}_{yyyyMMdd}-{yyyyMMdd}.csv</c> (ค.ศ. เรียงตามชื่อได้) · ตัดอักขระที่ใช้ในชื่อไฟล์ไม่ได้ ·
    /// ไม่มีช่วงวันที่ → ใช้วันที่ส่งออกตามเวลาไทย</summary>
    public static string FileName(string baseName, DateTime? from, DateTime? to, TabularFormat format)
    {
        var inv = CultureInfo.InvariantCulture;
        var safe = new string((baseName ?? "").Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '_' || ch is '-' || (ch >= '฀' && ch <= '๿') ? ch : '_').ToArray()).Trim('_');
        if (safe.Length == 0) safe = "export";
        string period;
        if (from.HasValue && to.HasValue)
            period = ThaiDate.CalendarDateUtc(from.Value).ToString("yyyyMMdd", inv) + "-" + ThaiDate.CalendarDateUtc(to.Value).ToString("yyyyMMdd", inv);
        else if (to.HasValue)
            period = ThaiDate.CalendarDateUtc(to.Value).ToString("yyyyMMdd", inv);
        else if (from.HasValue)
            period = ThaiDate.CalendarDateUtc(from.Value).ToString("yyyyMMdd", inv);
        else
            period = ThaiDate.YyyyMmDd(System.DateTime.UtcNow);
        return $"{safe}_{period}.{(format == TabularFormat.Csv ? "csv" : "xlsx")}";
    }

    // ── ภายใน ──

    private static bool IsNumericKind(TabularCellKind k)
        => k is TabularCellKind.Money or TabularCellKind.Number or TabularCellKind.Integer;

    private static decimal ToDecimal(object v) => v switch
    {
        decimal m => m,
        double d => (decimal)d,
        float f => (decimal)f,
        _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
    };

    private static System.DateTime ToBangkok(System.DateTime ts)
    {
        var utc = ts.Kind == DateTimeKind.Utc ? ts : System.DateTime.SpecifyKind(ts, DateTimeKind.Utc);
        return System.DateTime.SpecifyKind(utc.AddHours(7), DateTimeKind.Unspecified);
    }

    private static object? XlsxValue(object? v, TabularCellKind kind)
    {
        if (v == null) return null;
        if (v is string s) return s;   // ค่าแทนในช่องตัวเลข/ข้อความปกติ
        return kind switch
        {
            TabularCellKind.Money => Math.Round(ToDecimal(v), 2, MidpointRounding.AwayFromZero),
            TabularCellKind.Number => ToDecimal(v),
            TabularCellKind.Integer => Convert.ToInt64(v, CultureInfo.InvariantCulture),
            TabularCellKind.Date => v is System.DateTime d
                ? System.DateTime.SpecifyKind(ThaiDate.CalendarDateUtc(d), DateTimeKind.Unspecified)
                : FormatCell(v, kind),
            _ => FormatCell(v, kind),   // Text / DateThai / Timestamp → ข้อความที่จัดรูปแล้ว
        };
    }

    /// <summary>คีย์คอลัมน์ "A".."Z","AA".. — MiniExcel printHeader:false ใช้ลำดับคีย์เป็นลำดับคอลัมน์
    /// · ทุกแถวมีคีย์ครบเท่ากันเพื่อไม่ให้คอลัมน์เลื่อน</summary>
    private static Dictionary<string, object?> XRow(int width, object?[] cells)
    {
        var d = new Dictionary<string, object?>(width);
        for (var i = 0; i < width; i++) d[ColKey(i)] = i < cells.Length ? cells[i] : null;
        return d;
    }

    private static string ColKey(int i)
    {
        var s = "";
        i++;
        while (i > 0) { var m = (i - 1) % 26; s = (char)('A' + m) + s; i = (i - 1) / 26; }
        return s;
    }

    private static string SheetName(string name)
    {
        var cleaned = new string((name ?? "").Select(ch => ch is '\\' or '/' or '?' or '*' or '[' or ']' or ':' ? '_' : ch).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "ข้อมูล";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }
}
