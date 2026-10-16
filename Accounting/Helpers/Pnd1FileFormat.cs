using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตั้งตัวเดียวของไฟล์ ภ.ง.ด.1 (รายเดือน) และ ภ.ง.ด.1ก (รายปี)** ตาม
/// <b>"Format กลางโปรแกรมบันทึก(Import)ข้อมูลยื่นด้วยสื่อฯ ภ.ง.ด.1,1ก และ 1ก พิเศษ"</b> ของระบบ WHTSVS กรมสรรพากร
/// (ภาพสเปกที่เจ้าของส่งมา 2026-10-16 — <c>erp-review/2026-10-14/EXPORT_AUDIT.md</c> §"ภ.ง.ด.1 เทียบสเปก WHTSVS (2026-10-16)") ·
/// .txt · Excel · CSV ออกจาก <see cref="Fields"/> ตัวเดียว ⇒ สามรูปแบบห้าม drift กัน (กฎเหล็ก #4 A)
///
/// <para><b>สเปก 20 ช่องต่อหนึ่งรายการ</b> (<see cref="Spec"/>) — ข้อมูลผู้หัก (ช่อง 1–4) ซ้ำทุกแถว · ผู้มีเงินได้ (5–12) ·
/// งวด (13–14 · ภ.ง.ด.1ก เดือน = <c>00</c>) · รหัสเงินได้ 1–5 (15) · วันที่จ่าย <c>DDMMYYYY</c> <b>พ.ศ.</b> (16) · อัตรา (17) ·
/// เงิน 13,2 (18–19) · เงื่อนไข 1/2/3 (20) · ช่องที่ติด <c>*</c> ว่าง = เลข 0 เต็มความยาว · <c>**</c> ต้องมีค่า · <c>***</c> ต้องถูกต้อง</para>
///
/// <para><b>ไฟล์ข้อความ</b>: ชื่อ <see cref="TextFileName"/> · คั่น <c>|</c> · ช่องว่าง = <c>||</c> · เงินทศนิยม 2 ตำแหน่งไม่มีคอมมา ·
/// ไม่มีบรรทัดหัว/ท้าย · ทุกประเภทเงินได้อยู่ไฟล์เดียว (รหัสเงินได้เป็นช่องที่ 15 — เดิมแยก zip ไฟล์ละประเภท) ·
/// <b>Excel</b>: เซลล์เรียงตามสเปก · ความยาวไม่เกินสเปก · <b>ไม่มีแถวหัว</b> · ชีตชื่อ <see cref="ExcelSheetName"/> เท่านั้น ·
/// ค่าในเซลล์เป็นข้อความเท่ากับไฟล์ .txt ทุกช่อง (กันเลข 0 นำหน้าของเลขผู้เสียภาษี/สาขา/เดือน หาย)</para>
///
/// <para>รายงานที่มีหัวคอลัมน์ + ยอดต่อประเภทสำหรับตรวจกับหน้าแบบ = <see cref="BuildReviewXlsx"/> (ปุ่ม "รายงานตรวจทาน") —
/// <b>ไม่ใช่ไฟล์นำเข้า</b> · ไฟล์นำเข้าต้องตรงสเปกเท่านั้น</para>
///
/// <para>เดิม (รอบ 2026-10-14 ทีม X1) เรียงตามช่องของใบแนบกระดาษเพราะยังไม่มีสเปก — 10 ช่อง (ลำดับ · เลข · คำนำหน้า · ชื่อ ·
/// สกุล · วันที่ ค.ศ. · เงิน · ภาษี · เงื่อนไข · ประเภท) ไม่มีข้อมูลผู้หัก/ที่อยู่/รหัสไปรษณีย์/งวด/อัตรา และวันที่เป็น ค.ศ.</para>
/// </summary>
public static class Pnd1FileFormat
{
    /// <summary>จำนวนประเภทเงินได้บนแบบ ภ.ง.ด.1/1ก (= รหัสเงินได้ช่อง 15 ของสเปก)</summary>
    public const int CategoryCount = 5;

    public const int GeneralSalary = 1;
    public const int ApprovedThreePercent = 2;
    public const int SeveranceOneTime = 3;
    public const int Section40_2Resident = 4;
    public const int Section40_2NonResident = 5;

    /// <summary>ถ้อยคำบรรทัดประเภทเงินได้ตามหน้าแบบ ภ.ง.ด.1 — เลขข้อ = รหัสเงินได้ในไฟล์ (สเปกช่อง 15)</summary>
    public static string CategoryLabel(int category) => category switch
    {
        GeneralSalary => "(1) เงินได้ตามมาตรา 40(1) เงินเดือน ค่าจ้าง ฯลฯ กรณีทั่วไป",
        ApprovedThreePercent => "(2) เงินได้ตามมาตรา 40(1) เงินเดือน ค่าจ้าง ฯลฯ กรณีได้รับอนุมัติจากกรมสรรพากรให้หักอัตราร้อยละ 3",
        SeveranceOneTime => "(3) เงินได้ตามมาตรา 40(1)(2) กรณีนายจ้างจ่ายให้ครั้งเดียวเพราะเหตุออกจากงาน",
        Section40_2Resident => "(4) เงินได้ตามมาตรา 40(2) กรณีผู้รับเงินได้เป็นผู้อยู่ในประเทศไทย",
        Section40_2NonResident => "(5) เงินได้ตามมาตรา 40(2) กรณีผู้รับเงินได้มิได้เป็นผู้อยู่ในประเทศไทย",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "ประเภทเงินได้ ภ.ง.ด.1 มี 1–5"),
    };

    // ─────────────────────────── สเปก (ตัวตั้งเดียว) ───────────────────────────

    public enum FieldKind { Character, Numeric }

    /// <summary>หนึ่งช่องของสเปก — <paramref name="Flag"/> = <c>*</c> ว่างแล้วเติม 0 เต็มความยาว · <c>**</c> ต้องมีค่า ·
    /// <c>***</c> ต้องถูกต้อง · ว่าง = ไม่บังคับ · <paramref name="Length"/> ของ Numeric = 13 (ทศนิยม 2 — รวมจุดยาวสุด 16 ตัว)</summary>
    public sealed record FieldSpec(int No, string Flag, string Name, FieldKind Kind, int Length);

    /// <summary>20 ช่องตามภาพสเปก WHTSVS (เรียงตามลำดับในไฟล์)</summary>
    public static readonly IReadOnlyList<FieldSpec> Spec = new FieldSpec[]
    {
        new(1, "*", "ลักษณะการยื่นแบบ", FieldKind.Character, 2),
        new(2, "***", "เลขประจำตัวผู้เสียภาษีอากรของผู้มีหน้าที่หัก ณ ที่จ่าย", FieldKind.Character, 13),
        new(3, "*", "เลขประจำตัวผู้เสียภาษีอากรผู้มีหน้าที่หัก ณ ที่จ่าย (10 หลัก)", FieldKind.Character, 10),
        new(4, "*", "เลขที่สาขา ผู้มีหน้าที่หัก ณ ที่จ่าย", FieldKind.Character, 4),
        new(5, "*", "เลขประจำตัวผู้เสียภาษีอากรของผู้ถูกหัก ณ ที่จ่าย", FieldKind.Character, 13),
        new(6, "*", "เลขประจำตัวผู้เสียภาษีอากรผู้มีเงินได้ (10 หลัก)", FieldKind.Character, 10),
        new(7, "", "คำนำหน้าชื่อผู้มีเงินได้", FieldKind.Character, 40),
        new(8, "**", "ชื่อผู้มีเงินได้", FieldKind.Character, 80),
        new(9, "", "นามสกุลผู้มีเงินได้", FieldKind.Character, 80),
        new(10, "", "ที่อยู่ 1", FieldKind.Character, 80),
        new(11, "", "ที่อยู่ 2", FieldKind.Character, 80),
        new(12, "***", "รหัสไปรษณีย์", FieldKind.Character, 5),
        new(13, "***", "เดือนภาษี", FieldKind.Character, 2),
        new(14, "***", "ปีภาษี", FieldKind.Character, 4),
        new(15, "***", "รหัสเงินได้", FieldKind.Character, 1),
        new(16, "***", "วันที่จ่ายเงินได้", FieldKind.Character, 8),
        new(17, "***", "อัตราภาษีร้อยละ", FieldKind.Character, 3),
        new(18, "", "จำนวนเงินที่จ่าย", FieldKind.Numeric, 13),
        new(19, "", "จำนวนเงินภาษีที่หัก และนำส่ง", FieldKind.Numeric, 13),
        new(20, "***", "เงื่อนไขการหักภาษี ณ ที่จ่าย", FieldKind.Character, 1),
    };

    /// <summary>จำนวนช่องต่อหนึ่งรายการ</summary>
    public const int FieldCount = 20;

    /// <summary>ชื่อแฟ้มข้อความที่สเปกกำหนด (ใช้ทั้ง ภ.ง.ด.1 และ 1ก)</summary>
    public const string TextFileName = "PND1.txt";

    /// <summary>ชื่อชีตเดียวที่สเปกยอมรับ</summary>
    public const string ExcelSheetName = "Sheet1";

    /// <summary>ช่อง 1 ลักษณะการยื่นแบบ — "00" = ยื่นปกติ (ระบบไม่สร้างแบบยื่นเพิ่มเติม)</summary>
    public const string NormalFiling = "00";

    /// <summary>ช่อง 13 ของ ภ.ง.ด.1ก</summary>
    public const string AnnualTaxMonth = "00";

    /// <summary>ที่มาของรูปแบบ — แสดงในสรุปผลส่งออกและรายงานตรวจทาน (ห้ามอ้างว่าทดลองนำเข้าจริงแล้ว)</summary>
    public const string SpecNotice =
        "รูปแบบตาม \"Format กลางโปรแกรมบันทึก(Import)ข้อมูลยื่นด้วยสื่อฯ ภ.ง.ด.1,1ก และ 1ก พิเศษ\" ของ WHTSVS กรมสรรพากร "
        + "(20 ช่อง · ไม่มีแถวหัว · Excel ชีต Sheet1) — ยังไม่ได้ทดลองนำเข้าจริงจากระบบนี้ ถ้าหน้าเว็บไม่รับ ส่งข้อความ error มาให้แก้";

    /// <summary>1 รายการจ่ายเงินได้ (1 บรรทัดในไฟล์)</summary>
    /// <param name="Category">ประเภทเงินได้ 1–5 (= รหัสเงินได้ช่อง 15)</param>
    /// <param name="PayeeTaxId">เลขประจำตัวผู้เสียภาษี 13 หลัก (บุคคลไทย = เลขบัตรประชาชน) · null/ว่าง = ไม่รู้ ⇒ ไฟล์เติม 0 ตามสเปก (ช่อง *) + Notes เตือน</param>
    /// <param name="Title">คำนำหน้าข้อความไทย (นิติบุคคล = ว่าง)</param>
    /// <param name="PayDate">วันที่จ่าย (รายปี = วันจ่ายครั้งสุดท้ายของปี)</param>
    /// <param name="Condition">1 หัก ณ ที่จ่าย · 2 ออกให้ตลอดไป · 3 ออกให้ครั้งเดียว</param>
    /// <param name="Address">ที่อยู่ผู้มีเงินได้ (ข้อความเดียว — <see cref="AddressParts"/> แยกเป็นที่อยู่ 1/2 + รหัสไปรษณีย์)</param>
    /// <param name="SourceRef">อ้างอิงกลับ (รหัสพนักงาน / เลขที่ 50 ทวิ) — รายงานตรวจทานเท่านั้น ไม่อยู่ในไฟล์นำเข้า</param>
    /// <param name="PostalCode">รหัสไปรษณีย์ที่เก็บแยกช่อง (ผู้ติดต่อ) — ว่าง = หาจากท้ายที่อยู่</param>
    /// <param name="TaxRate">อัตราร้อยละ — null = หาค่าไม่ได้ (เงินเดือนอัตราก้าวหน้า) ⇒ "0" ตามข้อ 9 ของสเปก</param>
    /// <param name="PayeeTaxId10">เลขผู้เสียภาษี 10 หลักแบบเก่า — ระบบไม่เก็บ ⇒ null = "0000000000"</param>
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
        string? SourceRef = null,
        string? PostalCode = null,
        decimal? TaxRate = null,
        string? PayeeTaxId10 = null);

    /// <summary>ผู้มีหน้าที่หักภาษี + งวด (ช่อง 1–4 · 13–14 ซ้ำทุกแถว)</summary>
    /// <param name="TaxId">เลขประจำตัวผู้เสียภาษีของบริษัท 13 หลัก</param>
    /// <param name="BranchCode">รหัสสาขา 5 หลักของระบบ ("00000" = สำนักงานใหญ่) — ไฟล์ใช้ 4 หลัก (<see cref="WithholderBranch"/>)</param>
    /// <param name="Year">ปีภาษี ค.ศ. (ไฟล์เขียน พ.ศ.)</param>
    /// <param name="Month">เดือนภาษี 1–12 · null = ภ.ง.ด.1ก (ไฟล์เขียน "00")</param>
    /// <param name="TaxId10">เลข 10 หลักแบบเก่าของบริษัท — ระบบไม่เก็บ ⇒ null = "0000000000"</param>
    public sealed record Filer(string? TaxId, string? BranchCode, int Year, int? Month, string? TaxId10 = null)
    {
        public bool Annual => Month == null;
    }

    /// <summary>ยอดต่อประเภท (บรรทัดบนหน้าแบบ)</summary>
    public sealed record CategoryTotal(int Category, string Label, int Count, decimal Income, decimal Tax);

    /// <summary>หัวรายงานตรวจทาน — ชื่อบริษัท + งวดเป็นถ้อยคำ</summary>
    public sealed record Header(string? CompanyName, string? CompanyTaxId, string? BranchCode, string PeriodLabel, bool Annual);

    // ─────────────────────────── ค่าทั้ง 20 ช่อง (ตัวตั้งเดียว) ───────────────────────────

    /// <summary>ค่าของ 20 ช่องตามสเปก — ข้อความล้วน (เท่ากับที่เขียนลง .txt) · ตัดความยาวตามสเปกแล้ว ·
    /// ผู้เขียนแต่ละรูปแบบ (.txt / Excel / CSV / รายงานตรวจทาน) ห้ามจัดค่าเอง</summary>
    public static string[] Fields(Row r, Filer f)
    {
        var addr = AddressParts(r);
        var condition = r.Condition is 1 or 2 or 3 ? r.Condition : 1;
        if (r.Category is < 1 or > CategoryCount)
            throw new ArgumentOutOfRangeException(nameof(r), r.Category, "ประเภทเงินได้ ภ.ง.ด.1 มี 1–5");
        var v = new[]
        {
            NormalFiling,                                                     // 1  ลักษณะการยื่นแบบ
            ZeroFillIfEmpty(Digits(f.TaxId), 13),                             // 2  เลขผู้หัก 13 หลัก
            ZeroFillIfEmpty(Digits(f.TaxId10), 10),                           // 3  เลขผู้หัก 10 หลัก (ไม่มี = 0×10)
            WithholderBranch(f.BranchCode),                                   // 4  สาขาผู้หัก 4 หลัก
            ZeroFillIfEmpty(Digits(r.PayeeTaxId), 13),                        // 5  เลขผู้ถูกหัก 13 หลัก
            ZeroFillIfEmpty(Digits(r.PayeeTaxId10), 10),                      // 6  เลขผู้มีเงินได้ 10 หลัก
            Clean(r.Title),                                                   // 7  คำนำหน้า
            Clean(r.FirstName),                                               // 8  ชื่อ
            Clean(r.LastName),                                                // 9  นามสกุล
            addr.Line1,                                                       // 10 ที่อยู่ 1
            addr.Line2,                                                       // 11 ที่อยู่ 2
            addr.PostalCode,                                                  // 12 รหัสไปรษณีย์
            f.Month is int m ? m.ToString("00", CultureInfo.InvariantCulture) : AnnualTaxMonth, // 13 เดือนภาษี
            (f.Year + 543).ToString("0000", CultureInfo.InvariantCulture),     // 14 ปีภาษี พ.ศ.
            r.Category.ToString(CultureInfo.InvariantCulture),                // 15 รหัสเงินได้
            ThaiDate(r.PayDate),                                              // 16 วันที่จ่าย DDMMYYYY พ.ศ.
            Rate(r.TaxRate),                                                  // 17 อัตราภาษีร้อยละ
            Amount(r.IncomeAmount),                                           // 18 จำนวนเงินที่จ่าย 13,2
            Amount(r.TaxAmount),                                              // 19 ภาษีที่หักและนำส่ง 13,2
            condition.ToString(CultureInfo.InvariantCulture),                 // 20 เงื่อนไข
        };
        for (var i = 0; i < v.Length; i++)
            if (Spec[i].Kind == FieldKind.Character) v[i] = Fit(v[i], Spec[i].Length);
        return v;
    }

    /// <summary>1 บรรทัด .txt (ไม่มี CRLF) — 20 ช่อง คั่นด้วย <c>|</c> · ช่องว่าง = <c>||</c></summary>
    public static string DetailRow(Row r, Filer f) => string.Join("|", Fields(r, f));

    /// <summary>ไฟล์ .txt ทั้งฉบับ — รายการล้วน · CRLF ทุกบรรทัด · ทุกประเภทเงินได้อยู่ไฟล์เดียว</summary>
    public static string BuildText(IEnumerable<Row> rows, Filer f)
    {
        var sb = new StringBuilder();
        foreach (var r in rows) sb.Append(DetailRow(r, f)).Append("\r\n");
        return sb.ToString();
    }

    // ─────────────────────────── Excel นำเข้า (MiniExcel) ───────────────────────────

    /// <summary>ไฟล์ .xlsx นำเข้า — ชีตเดียวชื่อ <see cref="ExcelSheetName"/> · <b>ไม่มีแถวหัว</b> · 20 คอลัมน์ A–T ·
    /// ทุกเซลล์เป็นข้อความเท่ากับ .txt (MiniExcel — ตามกฎโปรเจกต์ ห้าม ClosedXML/EPPlus)</summary>
    public static byte[] BuildXlsx(IEnumerable<Row> rows, Filer f)
    {
        var sheet = rows.Select(r => SheetRow(FieldCount, Fields(r, f).Cast<object?>().ToArray())).ToList();
        using var ms = new MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(ms, sheet, printHeader: false, sheetName: ExcelSheetName);
        return ms.ToArray();
    }

    // ─────────────────────────── CSV ───────────────────────────

    /// <summary>CSV 20 ช่องเรียงตามสเปก <b>ไม่มีแถวหัว</b> (ค่าเท่ากับ .txt) — UTF-8 มี BOM · CRLF · ช่องข้อความผ่าน
    /// <see cref="CsvFieldSafety.Escape"/> · ⚠️ สเปก WHTSVS กำหนดเฉพาะ Text/Excel — CSV ใช้กับโปรแกรมอื่น</summary>
    public static byte[] BuildCsv(IEnumerable<Row> rows, Filer f)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            var v = Fields(r, f);
            sb.Append(string.Join(",", v.Select((x, i) => Spec[i].Kind == FieldKind.Numeric ? x : CsvFieldSafety.Escape(x))))
              .Append("\r\n");
        }
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    // ─────────────────────────── รายงานตรวจทาน (มีหัวคอลัมน์ — ไม่ใช่ไฟล์นำเข้า) ───────────────────────────

    /// <summary>ชื่อชีตสรุปยอดของรายงานตรวจทาน</summary>
    public const string SummarySheetName = "สรุปยอดหน้าแบบ";

    /// <summary>ชื่อชีตรายการของรายงานตรวจทาน</summary>
    public const string ReviewRowsSheetName = "รายการ (ค่าเดียวกับไฟล์ยื่น)";

    /// <summary>หัวคอลัมน์ของรายงานตรวจทาน — "เลขช่อง. ชื่อช่องตามสเปก" + "อ้างอิง"</summary>
    private static IReadOnlyList<string> ReviewHeaders()
        => Spec.Select(s => $"{s.No}. {s.Name}").Append("อ้างอิง (รหัสพนักงาน/เลขที่ 50 ทวิ)").ToList();

    /// <summary>ยอดต่อประเภท — คืนครบ 5 ประเภทเสมอ (ประเภทที่ไม่มีรายการ = 0) เพื่อให้ตรงบรรทัดบนหน้าแบบ ·
    /// จำนวนราย = จำนวนรายการในไฟล์</summary>
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

    /// <summary>รายงานตรวจทาน .xlsx — ชีตสรุปยอดต่อประเภท (ตรวจกับหน้าแบบ) + ชีตรายการที่มีหัวคอลัมน์ ·
    /// ค่าในชีตรายการมาจาก <see cref="Fields"/> ตัวเดียวกับไฟล์ยื่น (เงินช่อง 18–19 เป็นตัวเลขให้รวมยอดได้)</summary>
    public static byte[] BuildReviewXlsx(IEnumerable<Row> rows, Filer f, Header header)
    {
        var list = rows.ToList();
        var headers = ReviewHeaders();
        var width = headers.Count;
        var sheets = new Dictionary<string, object>();

        var summary = new List<Dictionary<string, object?>>
        {
            SheetRow(width, (header.Annual ? "ภ.ง.ด.1ก" : "ภ.ง.ด.1")
                + " — รายงานตรวจทาน (มีหัวคอลัมน์ · ไม่ใช่ไฟล์นำเข้า — ไฟล์ยื่นใช้ปุ่ม .txt หรือ Excel)"),
            SheetRow(width, "ผู้มีหน้าที่หักภาษี ณ ที่จ่าย", Clean(header.CompanyName)),
            SheetRow(width, "เลขประจำตัวผู้เสียภาษีอากร", ZeroFillIfEmpty(Digits(f.TaxId), 13)),
            SheetRow(width, "เลขที่สาขา (4 หลักตามสเปก)", WithholderBranch(f.BranchCode)),
            SheetRow(width, header.Annual ? "ปีภาษี" : "เดือนที่จ่ายเงินได้", header.PeriodLabel),
            SheetRow(width),
            SheetRow(width, "ประเภทเงินได้ (= รหัสเงินได้ช่อง 15)", "จำนวนราย", "เงินได้ทั้งสิ้น", "ภาษีที่นำส่งทั้งสิ้น"),
        };
        var totals = Summarize(list);
        foreach (var t in totals)
            summary.Add(SheetRow(width, t.Label, t.Count, t.Income, t.Tax));
        summary.Add(SheetRow(width, "รวม", totals.Sum(t => t.Count), totals.Sum(t => t.Income), totals.Sum(t => t.Tax)));
        summary.Add(SheetRow(width));
        summary.Add(SheetRow(width, SpecNotice));
        sheets[SummarySheetName] = summary;

        var detail = new List<Dictionary<string, object?>> { SheetRow(width, headers.Cast<object?>().ToArray()) };
        foreach (var r in list)
        {
            var cells = Fields(r, f).Cast<object?>().ToList();
            cells[17] = Money(r.IncomeAmount);
            cells[18] = Money(r.TaxAmount);
            cells.Add(Clean(r.SourceRef));
            detail.Add(SheetRow(width, cells.ToArray()));
        }
        sheets[ReviewRowsSheetName] = detail;

        using var ms = new MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(ms, sheets, printHeader: false);
        return ms.ToArray();
    }

    // ─────────────────────────── รูปแบบไฟล์ที่ขอ ───────────────────────────

    /// <summary>รูปแบบไฟล์ที่ผู้ใช้ขอ — <c>txt</c> (ค่าตั้งต้น) · <c>xlsx</c> (รับ <c>excel</c>) · <c>csv</c> ·
    /// <c>review</c> (รายงานตรวจทานมีหัวคอลัมน์) · ค่าอื่น = <see cref="BusinessRuleException"/> (ไม่เดา)</summary>
    public static string ParseFormat(string? format)
    {
        var f = (format ?? "").Trim().ToLowerInvariant();
        return f switch
        {
            "" or "txt" or "text" => "txt",
            "csv" => "csv",
            "xlsx" or "excel" => "xlsx",
            "review" => "review",
            _ => throw new BusinessRuleException(
                $"ไม่รู้จักรูปแบบไฟล์ \"{format}\" — ใช้ได้: txt (นำเข้า WHTSVS) · xlsx (นำเข้า WHTSVS) · csv · review (รายงานตรวจทาน)",
                "PND1-FORMAT"),
        };
    }

    // ─────────────────────────── ตัวตัดสินของแต่ละช่อง ───────────────────────────

    /// <summary>เงิน 2 ตำแหน่ง ปัดแบบ AwayFromZero (กฎเหล็ก #4 E)</summary>
    public static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>ช่อง 4 — สาขาผู้หัก <b>4 หลัก</b> จากรหัสสาขา 5 หลักของระบบ (ประกาศอธิบดีฯ ฉบับที่ 199):
    /// <c>00000</c> สำนักงานใหญ่ → <c>0000</c> · <c>00001</c> → <c>0001</c> (ตัดเลข 0 หน้าสุด) · ว่าง → <c>0000</c> ·
    /// สาขาที่ 10000 ขึ้นไปลง 4 หลักไม่ได้ ⇒ <see cref="BusinessRuleException"/> (ไม่ตัดเลขทิ้งเงียบ ๆ = ยื่นผิดสาขา)</summary>
    private static string WithholderBranch(string? branchCode)
    {
        var five = TaxBranchCode.Normalize(branchCode);
        if (five[0] != '0')
            throw new BusinessRuleException(
                $"รหัสสาขาผู้หักภาษี {five} มี 5 หลักเต็ม แต่ช่อง \"เลขที่สาขา ผู้มีหน้าที่หัก ณ ที่จ่าย\" ของไฟล์ ภ.ง.ด.1 รับ 4 หลัก — "
                + "ยื่นด้วยไฟล์นี้ไม่ได้ ติดต่อสรรพากรเรื่องเลขสาขา หรือยื่นในนามสำนักงานใหญ่",
                "PND1-BRANCH-4DIGIT");
        return five[1..];
    }

    /// <summary>ช่อง 16 — <c>DDMMYYYY</c> ปี พ.ศ. (เช่น 31 ม.ค. 2002 → <c>31012545</c>)</summary>
    public static string ThaiDate(DateTime d)
        => d.ToString("ddMM", CultureInfo.InvariantCulture) + (d.Year + 543).ToString("0000", CultureInfo.InvariantCulture);

    /// <summary>ช่อง 17 — อัตราร้อยละ ≤ 3 ตัวอักษร · จำนวนเต็มไม่มีทศนิยม ("3") · หาค่าไม่ได้/ศูนย์/ยาวเกิน 3 ตัว ⇒ "0"
    /// (ข้อ 9 ของสเปก "อัตราภาษีร้อยละ ถ้าหาค่าไม่ได้ให้ใส่เป็น 0")</summary>
    public static string Rate(decimal? rate)
    {
        if (rate is not decimal r || r <= 0) return "0";
        var x = Math.Round(r, 2, MidpointRounding.AwayFromZero);
        var s = x == decimal.Truncate(x)
            ? decimal.Truncate(x).ToString("0", CultureInfo.InvariantCulture)
            : x.ToString("0.##", CultureInfo.InvariantCulture);
        return s.Length <= 3 ? s : "0";
    }

    /// <summary>ช่อง 18–19 — Numeric 13,2: ทศนิยม 2 ตำแหน่งเสมอ ไม่มีคอมมา · เกิน 13 หลักก่อนจุด ⇒ ล้มดัง (ไม่ตัดยอดเงิน)</summary>
    public static string Amount(decimal v)
    {
        var m = Money(v);
        if (Math.Abs(m) >= 10_000_000_000_000m)
            throw new BusinessRuleException($"ยอดเงิน {m:N2} ยาวเกิน 13 หลักที่ไฟล์ ภ.ง.ด.1 รับ", "PND1-AMOUNT-LENGTH");
        return m.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>ที่อยู่ 1 · ที่อยู่ 2 (ช่องละ ≤ 80 ตัว) · รหัสไปรษณีย์ 5 หลัก จากที่อยู่ข้อความเดียว ·
    /// รหัสไปรษณีย์: ช่องที่เก็บแยกก่อน แล้วค่อยตัวเลข 5 หลักตัวสุดท้ายในที่อยู่ (<see cref="ContactDataHygiene.PostalCodeOf"/>) — ถูกตัดออกจากข้อความ ·
    /// แบ่งบรรทัดที่ อ./อำเภอ/เขต ตัวแรก ไม่มีก็ที่ จ./จังหวัด/กรุงเทพ ตัวสุดท้าย (ตรงตัวอย่างในสเปก "78/4 ต.พระองค์ดำ ต.ในเมือง" | "จ.พิษณุโลก") ·
    /// บรรทัด 1 ยาวเกิน ⇒ ตัดที่ช่องว่าง ส่วนเกินไปต้นบรรทัด 2 · บรรทัด 2 ยาวเกิน ⇒ ตัด + <paramref name="Truncated"/> (ผู้เรียกต้องเตือน)</summary>
    public static (string Line1, string Line2, string PostalCode, bool Truncated) AddressParts(Row r)
    {
        var text = Spaces.Replace(Clean(r.Address), " ").Trim();
        var post = ContactDataHygiene.PostalCodeOf(r.PostalCode, text) ?? "";
        if (post.Length == 5)
        {
            var ms = Regex.Matches(text, $@"(?<!\d){post}(?!\d)");
            if (ms.Count > 0)
            {
                var last = ms[^1];
                text = (text[..last.Index] + " " + text[(last.Index + last.Length)..]).Trim();
            }
            text = PostalWord.Replace(text, "").Trim();
            text = Spaces.Replace(text, " ").Trim();
        }
        if (text.Length == 0) return ("", "", post, false);

        var max = Spec[9].Length;
        var cut = -1;
        var d = DistrictMarker.Match(text);
        if (d.Success && d.Index > 0) cut = d.Index;
        else
        {
            var p = ProvinceMarker.Matches(text);
            if (p.Count > 0 && p[^1].Index > 0) cut = p[^1].Index;
        }
        var line1 = cut > 0 ? text[..cut].Trim() : text;
        var line2 = cut > 0 ? text[cut..].Trim() : "";
        if (line1.Length > max)
        {
            var sp = line1.LastIndexOf(' ', max);
            var at = sp > 0 ? sp : max;
            line2 = (line1[at..].Trim() + " " + line2).Trim();
            line1 = line1[..at].Trim();
        }
        var truncated = line2.Length > max;
        return (line1, Fit(line2, max), post, truncated);
    }

    /// <summary>แถวนี้มีข้อความที่ถูกตัดให้พอดีความยาวสเปก (คำนำหน้า 40 · ชื่อ/นามสกุล 80 · ที่อยู่ 2×80) — ผู้เรียกต้องเตือน</summary>
    public static bool Overflows(Row r)
        => Clean(r.Title).Length > Spec[6].Length
        || Clean(r.FirstName).Length > Spec[7].Length
        || Clean(r.LastName).Length > Spec[8].Length
        || AddressParts(r).Truncated;

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DistrictMarker = new(@"(?<=^|\s)(อ\.|อำเภอ|เขต)", RegexOptions.Compiled);
    private static readonly Regex ProvinceMarker = new(@"(?<=^|\s)(จ\.|จังหวัด|กรุงเทพ|กทม)", RegexOptions.Compiled);
    private static readonly Regex PostalWord = new(@"(รหัสไปรษณีย์|ไปรษณีย์|ปณ\.)\s*$", RegexOptions.Compiled);

    private static string Digits(string? raw) => ThaiTaxId.Normalize(raw);

    /// <summary>ช่อง * ที่ว่าง ⇒ เลข 0 เต็มความยาว (ข้อ 5 ของสเปก) · มีค่าแต่ผิดรูป ⇒ คงค่าเดิม (ไม่เติมให้ครบ = ไม่แต่งเลข · Notes เตือน)</summary>
    private static string ZeroFillIfEmpty(string digits, int length) => digits.Length == 0 ? new string('0', length) : digits;

    private static string Fit(string s, int length) => s.Length <= length ? s : s[..length].TrimEnd();

    /// <summary>ตัด pipe/บรรทัดใหม่ — .txt คั่นด้วย pipe ไม่มี quoting</summary>
    private static string Clean(string? raw) =>
        (raw ?? "").Replace("|", " ").Replace("\r", " ").Replace("\n", " ").Trim();

    private static Dictionary<string, object?> SheetRow(int width, params object?[] cells)
    {
        var d = new Dictionary<string, object?>(width);
        for (var i = 0; i < width; i++)
            d[ColumnKey(i)] = i < cells.Length ? cells[i] : null;
        return d;
    }

    private static string ColumnKey(int i) => ((char)('A' + i)).ToString();
}
