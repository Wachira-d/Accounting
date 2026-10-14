using Accounting.Helpers;

namespace Accounting.Services.Settlement.Adapters;

/// <summary>
/// **อ่านไฟล์ settlement เป็นแถวของข้อความ — ทีละแถว (ไม่โหลดทั้งตารางเข้าหน่วยความจำ)**
///
/// <para>รอบ 205 T0 (IMPORT_ETL_REVIEW §2.3): เนื้อตัวอ่าน (ถอดรหัส · CSV · xlsx · เพดาน · รูปค่าในเซลล์) ย้ายไปเป็น owner เดียวที่
/// <see cref="ImportTabularReader"/> ให้ทุกเส้นนำเข้าใช้ร่วม — ไฟล์นี้เหลือเป็น <b>wrapper บาง</b> ที่คง public API + พฤติกรรมของ settlement ไว้เหมือนเดิม:
/// ตัดสินชนิดจากนามสกุล (<see cref="SettlementFileInput.IsExcel"/>/<see cref="SettlementFileInput.IsCsv"/>) · ชีตแรก · โยน
/// <see cref="SettlementFormatException"/> ด้วยรหัสเดิมและข้อความเดิมของ settlement (<see cref="Translate(ImportFormatException, int, int)"/>)</para>
///
/// <para>CSV: UTF-8 (มี/ไม่มี BOM) · UTF-16 (BOM) · Windows-874/TIS-620 — ถอด UTF-8 แบบเข้มงวดก่อน · ตัวคั่น , ; tab จากบรรทัดแรก ·
/// Excel (.xlsx): MiniExcel เท่านั้น · ค่าตัวเลข/วันที่คงรูป invariant (ตัวแปลงค่าใน <see cref="SettlementValueParser"/> อ่านต่อ)</para>
/// </summary>
public static class SettlementFileReader
{
    /// <summary>ขนาดไฟล์สูงสุด — ตรงกับเพดานของ attachment abstraction (25 MB) เพราะไฟล์ต้นฉบับต้องเก็บได้</summary>
    public const long MaxFileBytes = ImportTabularReader.MaxFileBytes;

    /// <summary>จำนวนแถวสูงสุดต่อไฟล์ (รวมหัวตาราง) — review198-D D-P4: xlsx 25 MB คลายได้หลาย GB / CSV แถวสั้นนับแสน ⇒ ทุกแถวเข้า list +
    /// ธุรกรรมเดียว · อ่านทีละแถวแล้วหยุดทันทีที่เกิน (ไม่อ่านส่วนที่เหลือของไฟล์) · รายงานรอบโอนจริงต่อรอบอยู่ในหลักพัน–หมื่นแถว</summary>
    public const int MaxRows = ImportTabularReader.MaxRows;

    /// <summary>จำนวนคอลัมน์สูงสุดต่อแถว (Excel อ้างคอลัมน์ไกลสุด XFD ได้ ⇒ แถวเดียวกลายเป็นอาร์เรย์ 16,384 ช่อง)</summary>
    public const int MaxColumns = ImportTabularReader.MaxColumns;

    /// <summary>แถวของไฟล์ (ไม่ตัดแถวว่าง — ผู้เรียกข้ามเอง) · ค่าแต่ละช่องตัดช่องว่างหัวท้ายแล้ว · เกิน <see cref="MaxRows"/>/<see cref="MaxColumns"/> ⇒ ล้มดังภาษาไทย</summary>
    public static IEnumerable<IReadOnlyList<string>> ReadRows(SettlementFileInput file) => Open(file).Rows;

    /// <summary>
    /// เปิดไฟล์ — แถว (อ่านทีละแถว) + ตัวคั่นคอลัมน์ของ CSV (<c>null</c> = xlsx) · ตัวคั่น <c>;</c> = สัญญาณไฟล์รูปแบบยุโรป (จุลภาค = ทศนิยม) ที่
    /// ตัวอ่านยอดต้องรู้ (review198-B R-B10 · <see cref="SettlementFileDecisions.DecimalCommaAmbiguity"/>)
    /// </summary>
    public static SettlementFileRows Open(SettlementFileInput file)
    {
        if (file.Content.Length == 0)
            throw new SettlementFormatException("empty-file", "ไฟล์ว่างเปล่า — ส่งออกรายงานจากแพลตฟอร์มใหม่แล้วอัปโหลดอีกครั้ง");
        if (file.Content.LongLength > MaxFileBytes)
            throw new SettlementFormatException("file-too-large",
                $"ไฟล์ใหญ่เกิน {MaxFileBytes / (1024 * 1024)} MB — ส่งออกรายงานทีละรอบโอน (ช่วงวันที่สั้นลง) แล้วนำเข้าทีละไฟล์");
        if (file.IsExcel)
            return new SettlementFileRows(Capped(ImportTabularReader.ReadExcel(file.Content), MaxRows, MaxColumns), null);
        if (file.IsCsv)
        {
            var text = Decode(file.Content);
            return new SettlementFileRows(Capped(ReadCsv(text), MaxRows, MaxColumns), ImportTabularReader.DetectDelimiter(StripBom(text)));
        }
        throw new SettlementFormatException("unsupported-type",
            "รองรับเฉพาะไฟล์ .csv และ .xlsx — ถ้าเป็น .xls ให้เปิดใน Excel แล้วบันทึกเป็น .xlsx ก่อน");
    }

    /// <summary>ตัดการอ่านทันทีที่แถว/คอลัมน์เกินเพดาน (lazy — แถวที่เกินไม่ถูกอ่านต่อ) — ข้อความบอกทางไปต่อ (F2 ข้อ 8)</summary>
    public static IEnumerable<IReadOnlyList<string>> Capped(IEnumerable<IReadOnlyList<string>> rows, int maxRows, int maxColumns)
        => Translated(ImportTabularReader.Capped(rows, maxRows, maxColumns), maxRows, maxColumns);

    /// <summary>ถอดรหัสข้อความ CSV — ถอดไม่ได้ทั้ง UTF-8 และ Windows-874 ⇒ ล้มดัง (ไม่เดา)</summary>
    public static string Decode(byte[] bytes)
    {
        try { return ImportTabularReader.Decode(bytes); }
        catch (ImportFormatException ex) { throw Translate(ex, MaxRows, MaxColumns); }
    }

    /// <summary>แยก CSV ทีละแถว — ค่าในเครื่องหมายคำพูดข้ามบรรทัดได้ · คำพูดไม่ปิดจนจบไฟล์ ⇒ ล้มดัง</summary>
    public static IEnumerable<IReadOnlyList<string>> ReadCsv(string content)
        => Translated(ImportTabularReader.ReadCsv(content), MaxRows, MaxColumns);

    /// <summary>ค่าในช่อง Excel → ข้อความที่ตัวแปลงค่าอ่านต่อได้แน่นอน (ไม่ขึ้นกับ culture ของเครื่อง) — owner = <see cref="ImportTabularReader.CellText"/></summary>
    internal static string CellText(object? v) => ImportTabularReader.CellText(v);

    /// <summary>"A" → 0 · "AB" → 27 · คีย์ที่ไม่ใช่ตัวอักษรคอลัมน์ ⇒ -1 — owner = <see cref="ImportTabularReader.ColumnIndex"/></summary>
    internal static int ColumnIndex(string key) => ImportTabularReader.ColumnIndex(key);

    private static string StripBom(string content) => content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;

    /// <summary>ห่อการอ่านแบบ lazy — <see cref="ImportFormatException"/> ที่โผล่ตอน MoveNext แปลงเป็น <see cref="SettlementFormatException"/>
    /// (รหัสเดิม · ข้อความของ settlement) · ข้อผิดพลาดชนิดอื่นไหลผ่านตามเดิม</summary>
    private static IEnumerable<IReadOnlyList<string>> Translated(IEnumerable<IReadOnlyList<string>> rows, int maxRows, int maxColumns)
    {
        using var it = rows.GetEnumerator();
        while (true)
        {
            bool has;
            try { has = it.MoveNext(); }
            catch (ImportFormatException ex) { throw Translate(ex, maxRows, maxColumns); }
            if (!has) break;
            yield return it.Current;
        }
    }

    /// <summary>รหัสเดียวกับตัวอ่านกลาง · ข้อความของ settlement (พูดถึง "รายงานจากแพลตฟอร์ม/รอบโอน") คงเหมือนก่อนรอบ 205</summary>
    internal static SettlementFormatException Translate(ImportFormatException ex, int maxRows, int maxColumns) => ex.Code switch
    {
        "too-many-rows" => new SettlementFormatException(ex.Code,
            $"ไฟล์มีมากกว่า {maxRows:N0} แถว — ส่งออกรายงานทีละรอบโอน (ช่วงวันที่สั้นลง) แล้วนำเข้าทีละไฟล์"),
        "too-many-columns" => new SettlementFormatException(ex.Code,
            $"แถวที่ {(ex.RowNumber ?? 0):N0} มีมากกว่า {maxColumns:N0} คอลัมน์ — ไฟล์อาจเสีย/มีข้อมูลค้างที่คอลัมน์ไกล ๆ ส่งออกรายงานจากแพลตฟอร์มใหม่"),
        "encoding" => new SettlementFormatException(ex.Code,
            "อ่านอักขระในไฟล์ไม่ได้ (ไม่ใช่ UTF-8 หรือ TIS-620) — เปิดไฟล์ใน Excel แล้ว \"บันทึกเป็น CSV UTF-8\" หรือบันทึกเป็น .xlsx"),
        "csv-quote" => new SettlementFormatException(ex.Code,
            "ไฟล์ CSV มีเครื่องหมายคำพูด (\") ที่ไม่ปิด — ไฟล์อาจถูกตัดกลางทาง ส่งออกรายงานจากแพลตฟอร์มใหม่"),
        "xlsx-unreadable" => new SettlementFormatException(ex.Code,
            $"อ่านไฟล์ Excel ไม่ได้ — ตรวจว่าเป็น .xlsx ที่ไม่ได้ตั้งรหัสผ่าน ({ex.Detail ?? ex.Message})"),
        _ => new SettlementFormatException(ex.Code, ex.Message),
    };
}

/// <summary>ไฟล์ที่เปิดแล้ว — แถว (lazy) + ตัวคั่นคอลัมน์ของ CSV (<c>null</c> = xlsx)</summary>
public sealed record SettlementFileRows(IEnumerable<IReadOnlyList<string>> Rows, char? CsvDelimiter);
