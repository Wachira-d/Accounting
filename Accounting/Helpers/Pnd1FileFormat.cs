using System.Globalization;
using System.Text;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตั้งตัวเดียวของไฟล์ ภ.ง.ด.1 (รายเดือน) และ ภ.ง.ด.1ก (รายปี)** — ทั้ง .txt (นำเข้าเว็บสรรพากร/RD Prep) ·
/// CSV · Excel ออกจากฟังก์ชันจัดคอลัมน์ <see cref="Cells"/> ตัวเดียว ⇒ สามรูปแบบห้าม drift กัน
/// (กฎเหล็ก #4 A "สอง renderer ห้าม drift" · รอบ 2026-10-14 ทีม X1 คำถามเจ้าของ "export csv/excel ส่ง ภงด.1")
///
/// <para><b>ทำไมต้องเขียนใหม่</b> — <c>ExportPnd1Async</c> เดิมเขียน <c>H|…</c> / <c>D|…</c> / <c>T|…</c> + วันที่
/// <c>dd/MM/พ.ศ.</c> มี slash + "รหัสคำนำหน้า" ตัวเลข = รูปแบบเดียวกับที่ ภ.ง.ด.3/53 เคยพิสูจน์แล้วว่าผิด
/// (บรรทัด H/T กลายเป็นแถวขยะในหน้านำเข้า · วันที่มี slash map ไม่ได้ — ดู <see cref="PndTextFileFormat"/>)</para>
///
/// <para><b>ข้อตกลงเดียวกับ <see cref="PndTextFileFormat"/></b> (ที่ยืนยันกับหน้านำเข้าจริงของ ภ.ง.ด.3/53 แล้ว):
/// detail rows ล้วน ไม่มี header/trailer · คั่นด้วย <c>|</c> · CRLF · วันที่ <c>ddMMyyyy</c> ค.ศ. ไม่มีตัวคั่น
/// (หน้าเว็บมี radio พ.ศ./ค.ศ. — เลือก ค.ศ.) · เงิน 2 ตำแหน่ง ไม่มีคอมมา · คำนำหน้าเป็น <b>ข้อความไทย</b></para>
///
/// <para>⚠️ <b>ลำดับคอลัมน์ของ ภ.ง.ด.1/1ก ยังไม่ได้ยืนยันกับหน้านำเข้าของกรมสรรพากรจริง</b> (เครื่องที่เขียนเข้าเว็บ
/// rd.go.th ไม่ได้) — เรียงตามช่องของ "ใบแนบ ภ.ง.ด.1/1ก" ฉบับกระดาษ: ลำดับ · เลขประจำตัวผู้เสียภาษี · ชื่อผู้มีเงินได้
/// (คำนำหน้า ชื่อ ชื่อสกุล) · วันเดือนปีที่จ่าย (1ก = ที่อยู่) · จำนวนเงินได้ · ภาษีที่หักและนำส่ง · เงื่อนไข ·
/// ต่อท้ายด้วย "ประเภทเงินได้ (1)–(5)". หน้านำเข้าของ RD ให้ผู้ใช้ map คอลัมน์เอง ⇒ ถ้าลำดับจริงต่าง ผู้ใช้ยังจับคู่ได้
/// และแก้ที่ <see cref="Cells"/> ที่เดียวแล้วทั้งสามรูปแบบตามทัน · รายการสิ่งที่เจ้าของต้องทดสอบอยู่ที่
/// <c>erp-review/2026-10-14/EXPORT_AUDIT.md</c> §ทีม X1</para>
///
/// <para><b>ประเภทเงินได้ 5 ข้อของแบบ ภ.ง.ด.1</b> (บรรทัดบนหน้าแบบ — ต้องแยกยอดต่อข้อ):
/// (1) 40(1) กรณีทั่วไป · (2) 40(1) ได้รับอนุมัติให้หักร้อยละ 3 · (3) 40(1)(2) จ่ายครั้งเดียวเพราะออกจากงาน ·
/// (4) 40(2) ผู้รับอยู่ในประเทศไทย · (5) 40(2) ผู้รับมิได้อยู่ในประเทศไทย —
/// ไฟล์ .txt แยก <b>หนึ่งไฟล์ต่อหนึ่งประเภท</b> (ใบแนบแยกตามประเภท) · CSV มีคอลัมน์ประเภท · Excel แยกชีตต่อประเภท +
/// ชีตสรุปยอดต่อประเภท (จำนวนราย · เงินได้ · ภาษี) สำหรับกรอก/ตรวจหน้าแบบ</para>
/// </summary>
public static class Pnd1FileFormat
{
    /// <summary>จำนวนประเภทเงินได้บนแบบ ภ.ง.ด.1/1ก</summary>
    public const int CategoryCount = 5;

    public const int GeneralSalary = 1;
    public const int ApprovedThreePercent = 2;
    public const int SeveranceOneTime = 3;
    public const int Section40_2Resident = 4;
    public const int Section40_2NonResident = 5;

    /// <summary>ถ้อยคำบรรทัดประเภทเงินได้ตามหน้าแบบ ภ.ง.ด.1</summary>
    public static string CategoryLabel(int category) => category switch
    {
        GeneralSalary => "(1) เงินได้ตามมาตรา 40(1) เงินเดือน ค่าจ้าง ฯลฯ กรณีทั่วไป",
        ApprovedThreePercent => "(2) เงินได้ตามมาตรา 40(1) เงินเดือน ค่าจ้าง ฯลฯ กรณีได้รับอนุมัติจากกรมสรรพากรให้หักอัตราร้อยละ 3",
        SeveranceOneTime => "(3) เงินได้ตามมาตรา 40(1)(2) กรณีนายจ้างจ่ายให้ครั้งเดียวเพราะเหตุออกจากงาน",
        Section40_2Resident => "(4) เงินได้ตามมาตรา 40(2) กรณีผู้รับเงินได้เป็นผู้อยู่ในประเทศไทย",
        Section40_2NonResident => "(5) เงินได้ตามมาตรา 40(2) กรณีผู้รับเงินได้มิได้เป็นผู้อยู่ในประเทศไทย",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "ประเภทเงินได้ ภ.ง.ด.1 มี 1–5"),
    };

    /// <summary>ชื่อชีต Excel ต่อประเภท (≤ 31 ตัวอักษร · ไม่มี : \ / ? * [ ])</summary>
    public static string SheetName(int category) => category switch
    {
        GeneralSalary => "(1) 40(1) ทั่วไป",
        ApprovedThreePercent => "(2) 40(1) อนุมัติ 3%",
        SeveranceOneTime => "(3) ออกจากงาน",
        Section40_2Resident => "(4) 40(2) อยู่ในไทย",
        Section40_2NonResident => "(5) 40(2) ไม่อยู่ในไทย",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "ประเภทเงินได้ ภ.ง.ด.1 มี 1–5"),
    };

    /// <summary>ชื่อชีตสรุปยอด</summary>
    public const string SummarySheetName = "สรุปยอดหน้าแบบ";

    /// <summary>ข้อความเตือนสถานะการยืนยันรูปแบบ — แสดงในชีตสรุปและสรุปผลส่งออก (ห้ามอ้างว่ายืนยันแล้ว)</summary>
    public const string UnverifiedNotice =
        "ลำดับคอลัมน์ของไฟล์ ภ.ง.ด.1/1ก ยังไม่ได้ยืนยันกับหน้านำเข้าของกรมสรรพากรจริง — ตอนนำเข้าให้จับคู่คอลัมน์ตามหัวตาราง และเลือกรูปแบบวันที่ ค.ศ.";

    /// <summary>1 รายการจ่ายเงินได้ (1 บรรทัดในใบแนบ)</summary>
    /// <param name="Category">ประเภทเงินได้ 1–5</param>
    /// <param name="PayeeTaxId">เลขประจำตัวผู้เสียภาษี (บุคคลไทย = เลขบัตรประชาชน) · null/ว่าง = ไม่รู้ (ไฟล์ปล่อยว่าง ไม่แต่งเลข)</param>
    /// <param name="Title">คำนำหน้าข้อความไทย</param>
    /// <param name="PayDate">วันที่จ่าย (รายเดือน) — รายปีไม่ใช้</param>
    /// <param name="Condition">1 หัก ณ ที่จ่าย · 2 ออกให้ตลอดไป · 3 ออกให้ครั้งเดียว</param>
    /// <param name="Address">ที่อยู่ผู้มีเงินได้ (ใบแนบ ภ.ง.ด.1ก)</param>
    /// <param name="SourceRef">อ้างอิงกลับ (รหัสพนักงาน / เลขที่ 50 ทวิ) — CSV/Excel เท่านั้น ไม่อยู่ใน .txt</param>
    public sealed record Row(
        int Category,
        string? PayeeTaxId,
        string? Title,
        string? FirstName,
        string? LastName,
        DateTime PayDate,
        decimal IncomeAmount,
        decimal TaxAmount,
        int Condition = 1,
        string? Address = null,
        string? SourceRef = null);

    /// <summary>ยอดต่อประเภท (บรรทัดบนหน้าแบบ)</summary>
    public sealed record CategoryTotal(int Category, string Label, int Count, decimal Income, decimal Tax);

    /// <summary>หัวชีตสรุป — ข้อมูลผู้มีหน้าที่หักภาษี + งวด</summary>
    public sealed record Header(string? CompanyName, string? CompanyTaxId, string? BranchCode, string PeriodLabel, bool Annual);

    // ─────────────────────────── คอลัมน์ (ตัวตั้งเดียว) ───────────────────────────

    /// <summary>หัวคอลัมน์ตามลำดับเดียวกับ <see cref="Cells"/> — .txt ไม่มีแถวหัว แต่ CSV/Excel ใช้ชุดนี้
    /// (คอลัมน์สุดท้าย "อ้างอิง" มีเฉพาะ CSV/Excel)</summary>
    public static IReadOnlyList<string> Headers(bool annual) => annual
        ? new[] { "ลำดับที่", "เลขประจำตัวผู้เสียภาษีอากร", "คำนำหน้าชื่อ", "ชื่อ", "ชื่อสกุล", "ที่อยู่ของผู้มีเงินได้",
                  "จำนวนเงินได้ที่จ่ายทั้งปี", "จำนวนภาษีที่หักและนำส่งทั้งปี", "เงื่อนไขการหักภาษี", "ประเภทเงินได้", "อ้างอิง" }
        : new[] { "ลำดับที่", "เลขประจำตัวผู้เสียภาษีอากร", "คำนำหน้าชื่อ", "ชื่อ", "ชื่อสกุล", "วันเดือนปีที่จ่าย (ddMMyyyy ค.ศ.)",
                  "จำนวนเงินได้ที่จ่าย", "จำนวนภาษีที่หักและนำส่ง", "เงื่อนไขการหักภาษี", "ประเภทเงินได้", "อ้างอิง" };

    /// <summary>จำนวนคอลัมน์ในไฟล์ .txt (ไม่รวม "อ้างอิง")</summary>
    public const int TextColumnCount = 10;

    /// <summary>ค่าของแต่ละช่องตามลำดับ <see cref="Headers"/> — string / decimal / int ·
    /// ผู้เขียนแต่ละรูปแบบแปลงชนิดเอง แต่ "ช่องไหนอยู่ลำดับไหน มีค่าอะไร" ตัดสินที่นี่ที่เดียว</summary>
    public static object[] Cells(int seq, Row r, bool annual)
    {
        var condition = r.Condition is 1 or 2 or 3 ? r.Condition : 1;
        return new object[]
        {
            seq,                                                          // 1 ลำดับที่
            TaxIdDigits(r.PayeeTaxId),                                    // 2 เลขผู้เสียภาษี 13 หลัก (ว่าง = ไม่รู้)
            Clean(r.Title),                                               // 3 คำนำหน้า (ข้อความไทย)
            Clean(r.FirstName),                                           // 4 ชื่อ
            Clean(r.LastName),                                            // 5 ชื่อสกุล
            annual ? Clean(r.Address)                                     // 6 ที่อยู่ (1ก)
                   : r.PayDate.ToString("ddMMyyyy", CultureInfo.InvariantCulture), //   หรือ วันที่จ่าย ddMMyyyy ค.ศ.
            Money(r.IncomeAmount),                                        // 7 เงินได้ (decimal ปัดแล้ว)
            Money(r.TaxAmount),                                           // 8 ภาษี (decimal ปัดแล้ว)
            condition,                                                    // 9 เงื่อนไข 1/2/3
            r.Category,                                                   // 10 ประเภทเงินได้ 1–5
            Clean(r.SourceRef),                                           // 11 อ้างอิง (CSV/Excel เท่านั้น)
        };
    }

    // ─────────────────────────── .txt (นำเข้าเว็บสรรพากร) ───────────────────────────

    /// <summary>1 บรรทัด .txt (ไม่มี CRLF) — 10 ช่อง คั่นด้วย <c>|</c></summary>
    public static string DetailRow(int seq, Row r, bool annual = false)
        => string.Join("|", Cells(seq, r, annual).Take(TextColumnCount).Select(TextValue));

    /// <summary>ไฟล์ .txt ของประเภทเดียว — detail rows ล้วน · ลำดับเริ่ม 1 ต่อไฟล์ · CRLF</summary>
    private static string BuildText(IEnumerable<Row> rowsOfOneCategory, bool annual = false)
    {
        var sb = new StringBuilder();
        var seq = 1;
        foreach (var r in rowsOfOneCategory)
            sb.Append(DetailRow(seq++, r, annual)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>แยกไฟล์ .txt ต่อประเภทเงินได้ (เฉพาะประเภทที่มีรายการ · เรียง 1→5)</summary>
    public static IReadOnlyList<(int Category, string Text)> BuildTextPerCategory(IEnumerable<Row> rows, bool annual = false)
        => rows.GroupBy(r => r.Category).OrderBy(g => g.Key)
            .Select(g => (g.Key, BuildText(g, annual))).ToList();

    // ─────────────────────────── CSV ───────────────────────────

    /// <summary>CSV ทั้งชุด — UTF-8 มี BOM (Excel ภาษาไทยอ่านถูก) · CRLF · แถวหัวไทย · ลำดับเริ่มใหม่ต่อประเภท (ตรงกับ .txt)
    /// · ข้อความผ่าน <see cref="CsvFieldSafety.Escape"/> · ตัวเลขเขียนดิบ (ไม่มีคอมมา ไม่ถูกเติม <c>'</c>)</summary>
    public static byte[] BuildCsv(IEnumerable<Row> rows, bool annual = false)
    {
        var sb = new StringBuilder();
        sb.Append(CsvFieldSafety.Row(Headers(annual))).Append("\r\n");
        foreach (var g in rows.GroupBy(r => r.Category).OrderBy(g => g.Key))
        {
            var seq = 1;
            foreach (var r in g)
                sb.Append(string.Join(",", Cells(seq++, r, annual).Select(CsvValue))).Append("\r\n");
        }
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    // ─────────────────────────── Excel (MiniExcel) ───────────────────────────

    /// <summary>ยอดต่อประเภท — คืนครบ 5 ประเภทเสมอ (ประเภทที่ไม่มีรายการ = 0) เพื่อให้ตรงบรรทัดบนหน้าแบบ ·
    /// จำนวนราย = จำนวนรายการในใบแนบ (หนึ่งแถวต่อการจ่ายหนึ่งครั้ง)</summary>
    public static IReadOnlyList<CategoryTotal> Summarize(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        return Enumerable.Range(1, CategoryCount).Select(c =>
        {
            var g = list.Where(r => r.Category == c).ToList();
            return new CategoryTotal(c, CategoryLabel(c), g.Count,
                g.Sum(r => Money(r.IncomeAmount)), g.Sum(r => Money(r.TaxAmount)));
        }).ToList();
    }

    /// <summary>ชีตทั้งหมดของไฟล์ Excel (ชื่อชีต → แถว) — ชีตสรุปก่อน แล้วชีตต่อประเภทที่มีรายการ ·
    /// แถวเป็น dictionary คีย์ A,B,C… ให้เขียนด้วย <c>printHeader:false</c> (หัวตารางเป็นแถวแรกของชีตเอง) ·
    /// เลขผู้เสียภาษี/วันที่เป็น <b>ข้อความ</b> (คงเลข 0 นำหน้า) · เงินเป็นตัวเลข</summary>
    private static Dictionary<string, object> BuildSheets(IEnumerable<Row> rows, Header header)
    {
        var list = rows.ToList();
        var headers = Headers(header.Annual);
        var width = headers.Count;
        var sheets = new Dictionary<string, object>();

        var summary = new List<Dictionary<string, object?>>
        {
            SheetRow(width, header.Annual
                ? "ภ.ง.ด.1ก — สรุปยอดตามประเภทเงินได้ (ใช้ตรวจกับหน้าแบบ/หน้าเว็บสรรพากร)"
                : "ภ.ง.ด.1 — สรุปยอดตามประเภทเงินได้ (ใช้ตรวจกับหน้าแบบ/หน้าเว็บสรรพากร)"),
            SheetRow(width, "ผู้มีหน้าที่หักภาษี ณ ที่จ่าย", Clean(header.CompanyName)),
            SheetRow(width, "เลขประจำตัวผู้เสียภาษีอากร", TaxIdDigits(header.CompanyTaxId)),
            SheetRow(width, "เลขที่สาขา", BranchDigits(header.BranchCode)),
            SheetRow(width, header.Annual ? "ปีภาษี" : "เดือนที่จ่ายเงินได้", header.PeriodLabel),
            SheetRow(width),
            SheetRow(width, "ประเภทเงินได้", "จำนวนราย", "เงินได้ทั้งสิ้น", "ภาษีที่นำส่งทั้งสิ้น"),
        };
        var totals = Summarize(list);
        foreach (var t in totals)
            summary.Add(SheetRow(width, t.Label, t.Count, t.Income, t.Tax));
        summary.Add(SheetRow(width, "รวม", totals.Sum(t => t.Count), totals.Sum(t => t.Income), totals.Sum(t => t.Tax)));
        summary.Add(SheetRow(width));
        summary.Add(SheetRow(width, UnverifiedNotice));
        sheets[SummarySheetName] = summary;

        foreach (var g in list.GroupBy(r => r.Category).OrderBy(g => g.Key))
        {
            var sheet = new List<Dictionary<string, object?>> { SheetRow(width, headers.Cast<object?>().ToArray()) };
            var seq = 1;
            foreach (var r in g)
                sheet.Add(SheetRow(width, Cells(seq++, r, header.Annual)));
            sheets[SheetName(g.Key)] = sheet;
        }
        return sheets;
    }

    /// <summary>ไฟล์ .xlsx (MiniExcel — ตามกฎโปรเจกต์ ห้าม ClosedXML/EPPlus)</summary>
    public static byte[] BuildXlsx(IEnumerable<Row> rows, Header header)
    {
        using var ms = new MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(ms, BuildSheets(rows, header), printHeader: false);
        return ms.ToArray();
    }

    // ─────────────────────────── รูปแบบไฟล์ที่ขอ ───────────────────────────

    /// <summary>รูปแบบไฟล์ที่ผู้ใช้ขอ — <c>txt</c> (ค่าตั้งต้น) · <c>csv</c> · <c>xlsx</c> (รับ <c>excel</c> ด้วย) ·
    /// ค่าอื่น = <see cref="BusinessRuleException"/> (ไม่เดา)</summary>
    public static string ParseFormat(string? format)
    {
        var f = (format ?? "").Trim().ToLowerInvariant();
        return f switch
        {
            "" or "txt" or "text" => "txt",
            "csv" => "csv",
            "xlsx" or "excel" => "xlsx",
            _ => throw new BusinessRuleException(
                $"ไม่รู้จักรูปแบบไฟล์ \"{format}\" — ใช้ได้: txt (นำเข้าเว็บสรรพากร) · csv · xlsx", "PND1-FORMAT"),
        };
    }

    // ─────────────────────────── ตัวช่วยค่า ───────────────────────────

    /// <summary>เงิน 2 ตำแหน่ง ปัดแบบ AwayFromZero (กฎเหล็ก #4 E)</summary>
    public static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string TaxIdDigits(string? raw) => ThaiTaxId.Normalize(raw);

    private static string BranchDigits(string? raw)
    {
        var d = new string((raw ?? "").Where(char.IsDigit).ToArray());
        if (d.Length == 0) return "00000";
        return d.Length >= 5 ? d[^5..] : d.PadLeft(5, '0');
    }

    /// <summary>ตัด pipe/บรรทัดใหม่ — .txt คั่นด้วย pipe ไม่มี quoting</summary>
    private static string Clean(string? raw) =>
        (raw ?? "").Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();

    private static string TextValue(object v) => v switch
    {
        decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => v?.ToString() ?? "",
    };

    private static string CsvValue(object v) => v switch
    {
        decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => CsvFieldSafety.Escape(v?.ToString()),
    };

    private static Dictionary<string, object?> SheetRow(int width, params object?[] cells)
    {
        var d = new Dictionary<string, object?>(width);
        for (var i = 0; i < width; i++)
            d[ColumnKey(i)] = i < cells.Length ? cells[i] : null;
        return d;
    }

    private static string ColumnKey(int i) => ((char)('A' + i)).ToString();
}
