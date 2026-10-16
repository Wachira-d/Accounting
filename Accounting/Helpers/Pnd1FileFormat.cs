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
    public static string[] Fields(Row r, Filer f) => FieldsCore(r, f, forReview: false);

    /// <param name="forReview">รายงานตรวจทาน (ไม่ใช่ไฟล์นำเข้า) — สาขาที่ลง 4 หลักไม่ได้แสดงเลข 5 หลักเดิมแทนการล้ม
    /// (ผู้ใช้ต้องใช้รายงานนี้บันทึกแบบด้วยมือ — <see cref="WithholderBranch"/>)</param>
    private static string[] FieldsCore(Row r, Filer f, bool forReview)
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
            forReview ? ReviewBranch(f.BranchCode) : WithholderBranch(f.BranchCode), // 4  สาขาผู้หัก 4 หลัก
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
        if (forReview) v[3] = ReviewBranch(f.BranchCode);   // รายงานตรวจทาน: สาขา 5 หลักห้ามถูกตัดเหลือ 4 (ไม่ใช่ไฟล์นำเข้า)
        return v;
    }

    /// <summary>1 บรรทัด .txt (ไม่มี CRLF) — 20 ช่อง คั่นด้วย <c>|</c> · ช่องว่าง = <c>||</c></summary>
    public static string DetailRow(Row r, Filer f) => string.Join("|", Fields(r, f));

    /// <summary>ไฟล์ .txt ทั้งฉบับ — รายการล้วน · CRLF ทุกบรรทัด · ทุกประเภทเงินได้อยู่ไฟล์เดียว</summary>
    internal static string BuildText(IEnumerable<Row> rows, Filer f)
    {
        var sb = new StringBuilder();
        foreach (var r in rows) sb.Append(DetailRow(r, f)).Append("\r\n");
        return sb.ToString();
    }

    // ─────────────────────────── การเข้ารหัส .txt (UTF-8 · TIS-620) ───────────────────────────

    /// <summary>รูปแบบ .txt แบบ TIS-620 (code page 874) — ค่าที่ <see cref="ParseFormat"/> คืน</summary>
    public const string Tis620Format = "txt-tis620";

    /// <summary>
    /// <b>⚠️ การเข้ารหัสที่ WHTSVS อ่านยังไม่ได้ยืนยัน</b> (ฝ่ายค้าน ภ.ง.ด.1 P1-2 · EXPORT_AUDIT): ภาพสเปกไม่ระบุ encoding ·
    /// บันทึกเดิม (erp-review/2026-09-05/report-C.md) เขียนว่า "ต้อง verify กับ RD — ยังไม่ยืนยัน" ⇒ ระบบออกได้สองแบบ
    /// (UTF-8 ไม่มี BOM = ค่าตั้งต้นเดิม · TIS-620) และหน้าเว็บแนะนำ <b>ไฟล์ Excel ก่อน</b> (ข้อความในเซลล์ไม่มีเรื่อง encoding)
    /// จนกว่าเจ้าของจะทดลองนำเข้าจริง · ความยาวช่องนับเป็น<b>ตัวอักษร</b> — TIS-620: ไทย 1 ตัว = 1 ไบต์ (เท่ากันเสมอ) ·
    /// UTF-8: ไทย 1 ตัว = 3 ไบต์ (<see cref="ByteLengthOverflowFields"/> เตือนถ้า WHTSVS นับเป็นไบต์)
    /// </summary>
    public static byte[] EncodeTextUtf8(IEnumerable<Row> rows, Filer f) => new UTF8Encoding(false).GetBytes(BuildText(rows, f));

    /// <summary>.txt แบบ TIS-620 (Windows-874) — อักขระที่เข้ารหัสไม่ได้ (เช่น ภาษาจีน · อีโมจิ · อักษรเน้นเสียงยุโรป) ⇒
    /// <see cref="BusinessRuleException"/> บอกรายการ/ช่อง/อักขระ (<b>ไม่แทนด้วย "?" เงียบ ๆ</b> = ชื่อผิดในแบบที่ยื่นแล้วแก้ไม่ได้)</summary>
    public static byte[] EncodeTextTis620(IEnumerable<Row> rows, Filer f)
    {
        var enc = Tis620();
        var sb = new StringBuilder();
        var n = 0;
        foreach (var r in rows)
        {
            n++;
            var v = Fields(r, f);
            for (var i = 0; i < v.Length; i++)
            {
                v[i] = FoldForTis620(v[i], Spec[i].Length);
                var bad = FirstUnencodable(v[i], enc);
                if (bad < 0) continue;
                var pair = char.IsSurrogatePair(v[i], bad);
                var cp = pair ? char.ConvertToUtf32(v[i], bad) : v[i][bad];
                var ch = pair ? v[i].Substring(bad, 2) : v[i][bad].ToString();
                throw new BusinessRuleException(
                    $"สร้าง PND1.txt แบบ TIS-620 ไม่ได้: รายการที่ {n} ({Clean(r.FirstName)} {Clean(r.LastName)}".TrimEnd()
                    + (string.IsNullOrWhiteSpace(r.SourceRef) ? "" : $" · อ้างอิง {Clean(r.SourceRef)}")
                    + $") ช่อง {Spec[i].No}. {Spec[i].Name} มีอักขระ “{ch}” (U+{cp:X4}) ที่ TIS-620 ไม่มี — "
                    + "แก้ข้อความที่ทะเบียนพนักงาน/ผู้ติดต่อ หรือใช้ไฟล์ Excel นำเข้า / PND1.txt แบบ UTF-8 แทน",
                    "PND1-TIS620-UNENCODABLE");
            }
            sb.Append(string.Join("|", v)).Append("\r\n");
        }
        return enc.GetBytes(sb.ToString());
    }

    /// <summary>
    /// อักขระที่ <b>Windows-874 มีแต่ TIS-620 ไม่มี</b> (ไบต์ 0x80–0xA0 — ฝ่ายค้าน W1c P2-3: code page 874 ของ .NET คือ Windows-874 ⇒ ตัวเข้ารหัสเข้มงวด
    /// แต่ยังเขียนไบต์ที่ TIS-620 ไม่มีโดยไม่รายงาน) ที่มี<b>ตัวเทียบ ASCII ความหมายเดิม</b> — … ⇒ "..." · คำพูดโค้ง ⇒ ' " · ขีด en/em ⇒ "-" · NBSP ⇒ ช่องว่าง ·
    /// ตัวที่ไม่มีตัวเทียบ (€ 0x80 · • 0x95) ไม่อยู่ในตาราง ⇒ ล้มดัง <c>PND1-TIS620-UNENCODABLE</c> (<see cref="RoundTrips"/> ไม่รับไบต์ 0x80–0xA0)
    /// </summary>
    private static readonly IReadOnlyDictionary<char, string> Tis620AsciiFold = new Dictionary<char, string>
    {
        ['\u2026'] = "...", ['\u2018'] = "'", ['\u2019'] = "'", ['\u201C'] = "\"", ['\u201D'] = "\"",
        ['\u2013'] = "-", ['\u2014'] = "-", ['\u00A0'] = " ",
    };

    /// <summary>แปลงตาม <see cref="Tis620AsciiFold"/> · ยาวเกินสเปกหลังแปลง ("…" = 3 ตัว) ⇒ ตัดตามความยาวช่องแบบเดียวกับ <see cref="Fields"/></summary>
    private static string FoldForTis620(string s, int length)
    {
        if (!s.Any(Tis620AsciiFold.ContainsKey)) return s;
        var sb = new StringBuilder(s.Length + 4);
        foreach (var ch in s) sb.Append(Tis620AsciiFold.TryGetValue(ch, out var to) ? to : ch.ToString());
        return Fit(sb.ToString(), length);
    }

    /// <summary>ตาราง 874 แบบเข้มงวด (ไม่ best-fit · ไม่แทนด้วย ?) — provider เดียวกับ <see cref="ImportTabularReader"/> (ไม่ RegisterProvider ทั้ง process)</summary>
    private static Encoding Tis620()
        => CodePagesEncodingProvider.Instance.GetEncoding(874, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
           ?? throw new InvalidOperationException("ไม่มีตารางอักขระ 874 (TIS-620) ในเครื่องนี้");

    /// <summary>ตำแหน่งอักขระแรกที่ <paramref name="enc"/> เข้ารหัสแล้วถอดกลับไม่ได้ตัวเดิม · -1 = เข้ารหัสได้ทั้งหมด</summary>
    private static int FirstUnencodable(string s, Encoding enc)
    {
        if (s.Length == 0) return -1;
        if (RoundTrips(s, enc)) return -1;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsSurrogate(s[i])) return i;
            if (!RoundTrips(s[i].ToString(), enc)) return i;
        }
        return -1;
    }

    private static bool RoundTrips(string s, Encoding enc)
    {
        if (s.Any(ImportTabularReader.Is874Undefined)) return false;
        try
        {
            var bytes = enc.GetBytes(s);
            // ไบต์ 0x80–0xA0 = Windows-874 เท่านั้น (TIS-620 ไม่มี) — W1c P2-3: เดิมผ่านเงียบ ๆ (€ … คำพูดโค้ง ขีด NBSP)
            if (bytes.Any(b => b is >= 0x80 and <= 0xA0)) return false;
            return enc.GetString(bytes) == s;
        }
        catch (EncoderFallbackException) { return false; }
        catch (DecoderFallbackException) { return false; }
    }

    /// <summary>ช่องข้อความ (เลขช่องตามสเปก) ที่เข้ารหัสด้วย <paramref name="enc"/> แล้ว<b>จำนวนไบต์ไม่เท่าจำนวนตัวอักษร และเกินความยาวสเปก</b> —
    /// ระบบนับความยาวเป็นตัวอักษร · ถ้า WHTSVS นับเป็นไบต์ ช่องเหล่านี้จะยาวเกิน · TIS-620 คืนว่างเสมอ (1 ตัว = 1 ไบต์)</summary>
    public static IReadOnlyList<int> ByteLengthOverflowFields(Row r, Filer f, Encoding enc)
    {
        var v = Fields(r, f);
        var over = new List<int>();
        for (var i = 0; i < v.Length; i++)
        {
            if (Spec[i].Kind != FieldKind.Character) continue;
            int bytes;
            try { bytes = enc.GetByteCount(v[i]); } catch (EncoderFallbackException) { continue; }
            if (bytes != v[i].Length && bytes > Spec[i].Length) over.Add(Spec[i].No);
        }
        return over;
    }

    /// <summary>ข้อความ .txt สำหรับ "ดูตัวอย่าง" บนหน้าเว็บ — ค่าเดียวกับไฟล์ทุกช่อง ยกเว้นช่อง 5 ปิดบังเมื่อ <paramref name="maskPayeeTaxId"/>
    /// (ผู้ดูไม่มีสิทธิ์ Pii.View — P1-3 · เดิมหน้าเว็บดึงไฟล์ยื่นจริงที่มีเลขเต็มมาแสดง)</summary>
    public static string BuildPreviewText(IEnumerable<Row> rows, Filer f, bool maskPayeeTaxId)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            // สาขาผู้หักแบบรายงานตรวจทาน (4 หลักเมื่อลงได้ = ค่าเดียวกับไฟล์ · ≥ 10000 แสดงเลขเดิม) — พรีวิวห้ามล้มด้วยด่านของไฟล์นำเข้า
            // (W1c P2-4: ด่านนั้นแสดงเป็นคำเตือนในสรุปของพรีวิวแทน)
            var v = FieldsCore(r, f, forReview: true);
            if (maskPayeeTaxId) v[4] = MaskTaxId(v[4]);
            sb.Append(string.Join("|", v)).Append("\r\n");
        }
        return sb.ToString();
    }

    // ─────────────────────────── Excel นำเข้า (MiniExcel) ───────────────────────────

    /// <summary>ไฟล์ .xlsx นำเข้า — ชีตเดียวชื่อ <see cref="ExcelSheetName"/> · <b>ไม่มีแถวหัว</b> · 20 คอลัมน์ A–T ·
    /// ทุกเซลล์เป็นข้อความเท่ากับ .txt (MiniExcel — ตามกฎโปรเจกต์ ห้าม ClosedXML/EPPlus)</summary>
    public static byte[] BuildXlsx(IEnumerable<Row> rows, Filer f)
    {
        var sheet = rows.Select(r => SheetRow(FieldCount, Fields(r, f).Cast<object?>().ToArray())).ToList();
        using var ms = new MemoryStream();
        MiniExcelLibs.MiniExcel.SaveAs(ms, sheet, printHeader: false, sheetName: ExcelSheetName, configuration: ImportXlsxConfig());
        return InlineStrings(ms.ToArray());
    }

    /// <summary>ไฟล์นำเข้า: <b>ไม่ตรึงแถว · ไม่มีตัวกรองอัตโนมัติ · ไม่มีสไตล์ตาราง</b> (ฝ่ายค้าน ภ.ง.ด.1 P2-8 — ค่าตั้งต้นของ MiniExcel
    /// 1.34.2 เขียน frozen pane ySplit=1 + autoFilter A1:T1 แม้ printHeader=false ⇒ แถวข้อมูลแถวแรกดูเหมือนหัวตาราง และโปรแกรมนำเข้าที่
    /// อ่าน autoFilter อาจข้ามแถวแรก)</summary>
    private static MiniExcelLibs.OpenXml.OpenXmlConfiguration ImportXlsxConfig() => new()
    {
        AutoFilter = false,
        FreezeRowCount = 0,
        FreezeColumnCount = 0,
        TableStyles = MiniExcelLibs.OpenXml.TableStyles.None,
    };

    private static readonly Regex StrCell = new(
        @"<(?<p>(?:\w+:)?)c(?<a>[^>]*?) t=""str""(?<b>[^>]*)><(?:\w+:)?v>(?<v>[^<]*)</(?:\w+:)?v></(?:\w+:)?c>",
        RegexOptions.Compiled);

    /// <summary>เซลล์ข้อความจาก MiniExcel เป็น <c>t="str"</c> (ในสเปก OOXML = "ผลลัพธ์ข้อความของสูตร") ⇒ แปลงเป็น
    /// <c>t="inlineStr"</c> + <c>&lt;is&gt;&lt;t&gt;</c> (ข้อความล้วนตามสเปก — โปรแกรมนำเข้าที่เข้มงวดอ่านเป็นข้อความแน่นอน) ·
    /// ค่าในเซลล์ไม่เปลี่ยน (ข้อความที่ escape แล้วย้ายตรง ๆ) · ไฟล์อื่นใน zip คัดลอกทั้งไบต์</summary>
    private static byte[] InlineStrings(byte[] xlsx)
    {
        using var input = new MemoryStream(xlsx);
        using var src = new System.IO.Compression.ZipArchive(input, System.IO.Compression.ZipArchiveMode.Read);
        using var output = new MemoryStream();
        using (var dst = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in src.Entries)
            {
                var ne = dst.CreateEntry(e.FullName, System.IO.Compression.CompressionLevel.Optimal);
                using var r = e.Open();
                using var w = ne.Open();
                if (e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    using var sr = new StreamReader(r, new UTF8Encoding(false));
                    var xml = StrCell.Replace(sr.ReadToEnd(), m =>
                    {
                        var p = m.Groups["p"].Value;
                        var v = m.Groups["v"].Value;
                        var space = v.Length > 0 && (char.IsWhiteSpace(v[0]) || char.IsWhiteSpace(v[^1])) ? " xml:space=\"preserve\"" : "";
                        return $"<{p}c{m.Groups["a"].Value} t=\"inlineStr\"{m.Groups["b"].Value}><{p}is><{p}t{space}>{v}</{p}t></{p}is></{p}c>";
                    });
                    var bytes = new UTF8Encoding(false).GetBytes(xml);
                    w.Write(bytes, 0, bytes.Length);
                }
                else r.CopyTo(w);
            }
        }
        return output.ToArray();
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
    /// <param name="maskPayeeTaxId">ปิดบังเลขผู้มีเงินได้ (ช่อง 5) — ผู้ดาวน์โหลดไม่มีสิทธิ์ <c>Pii.View</c> (PDPA ม.26 · CLAUDE.md §J ·
    /// ฝ่ายค้าน ภ.ง.ด.1 P1-3: รายงานนี้ไม่ใช่ไฟล์ยื่น จึงไม่จำเป็นต้องมีเลขเต็ม — แบบเดียวกับหน้ารายงาน ภ.ง.ด.1 ของหน้าเงินเดือน)</param>
    public static byte[] BuildReviewXlsx(IEnumerable<Row> rows, Filer f, Header header, bool maskPayeeTaxId = false)
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
            SheetRow(width, "เลขที่สาขา (4 หลักตามสเปก)", ReviewBranch(f.BranchCode)),
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
            var cells = FieldsCore(r, f, forReview: true).Cast<object?>().ToList();
            if (maskPayeeTaxId) cells[4] = MaskTaxId(cells[4] as string);
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
            Tis620Format or "tis620" or "tis-620" or "txt874" or "874" or "windows-874" => Tis620Format,
            "csv" => "csv",
            "xlsx" or "excel" => "xlsx",
            "review" => "review",
            _ => throw new BusinessRuleException(
                $"ไม่รู้จักรูปแบบไฟล์ \"{format}\" — ใช้ได้: xlsx (นำเข้า WHTSVS · แนะนำ) · txt (UTF-8) · txt-tis620 (TIS-620) · csv · review (รายงานตรวจทาน)",
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
        if (five[0] != '0') throw BranchBlocker(five);
        return five[1..];
    }

    private static BusinessRuleException BranchBlocker(string five) => new(
        $"รหัสสาขาผู้หักภาษี {five} มี 5 หลักเต็ม แต่ช่อง \"เลขที่สาขา ผู้มีหน้าที่หัก ณ ที่จ่าย\" ของไฟล์ ภ.ง.ด.1 (สเปก WHTSVS) รับ 4 หลัก — "
        + "ระบบไม่ตัดเลขสาขาทิ้งเอง (จะกลายเป็นการยื่นในนามสาขาอื่น) · สอบถามสำนักงานสรรพากรพื้นที่ว่าสาขานี้ยื่นด้วยสื่ออย่างไร "
        + "แล้วยื่นด้วยการบันทึกแบบที่หน้าเว็บสรรพากร (ใช้ยอดจาก \"รายงานตรวจทาน\") · "
        + "การยื่นรวมในนามสำนักงานใหญ่แทนสาขาทำได้เฉพาะเมื่อได้รับอนุมัติจากกรมสรรพากรแล้วเท่านั้น",
        "PND1-BRANCH-4DIGIT");

    /// <summary>ช่อง 4 ของรายงานตรวจทาน — 4 หลักตามสเปกเมื่อลงได้ · สาขา 10000 ขึ้นไปแสดงเลข 5 หลักเดิม (รายงานไม่ใช่ไฟล์นำเข้า ·
    /// ผู้ใช้ต้องเห็นเลขสาขาจริงเพื่อบันทึกแบบด้วยมือ)</summary>
    private static string ReviewBranch(string? branchCode)
    {
        var five = TaxBranchCode.Normalize(branchCode);
        return five[0] != '0' ? five : five[1..];
    }

    /// <summary>เลขผู้มีเงินได้แบบปิดบัง — ตัวปิดบังกลาง <see cref="PiiMask.CitizenId"/> (ตัวเดียวกับหน้ารายงาน ภ.ง.ด.1/ทะเบียนพนักงาน) ·
    /// เลข 0 ล้วน (= ไม่มีเลข ตามสเปกช่อง *) ไม่ใช่ข้อมูลส่วนบุคคล ⇒ คงไว้ให้ผู้ตรวจเห็นว่าแถวนี้ไม่มีเลข</summary>
    private static string MaskTaxId(string? digits)
        => string.IsNullOrEmpty(digits) || digits.All(c => c == '0') ? digits ?? "" : PiiMask.CitizenId(digits) ?? "";

    /// <summary>
    /// <b>ช่อง 2 (<c>***</c> ต้องถูกต้อง)</b> (ฝ่ายค้าน ภ.ง.ด.1 P2-4): เลขผู้เสียภาษีของบริษัทว่าง/ไม่ครบ 13 หลัก/ไม่ผ่าน checksum ⇒ เหตุพร้อมทางไปต่อ ·
    /// เดิมเป็นคำเตือนแล้วยังออกไฟล์ (ช่อง 2 = 0000000000000) ทั้งที่คำเตือนเองยอมรับว่าสรรพากรไม่รับทั้งไฟล์ · ผู้เรียกผ่าน <see cref="ImportFileBlocker"/> เท่านั้น
    /// (W1d: <c>RequireWithholderTaxId</c> ที่โยนตรงถูกลบ — ผู้เรียกตัวเดียวย้ายไปใช้ด่านรวม)
    /// </summary>
    private static BusinessRuleException? WithholderTaxIdBlocker(string? companyTaxId, string formName)
    {
        if (ThaiTaxId.IsValid(companyTaxId)) return null;
        var shown = ThaiTaxId.Normalize(companyTaxId);
        return new BusinessRuleException(
            $"{formName}: เลขประจำตัวผู้เสียภาษีของบริษัท" + (shown.Length == 0 ? "ยังไม่ได้กรอก" : $" ({shown}) ไม่ครบ 13 หลักหรือไม่ผ่านการตรวจหลักสุดท้าย")
            + " — ช่องที่ 2 ของไฟล์ (ผู้มีหน้าที่หักภาษี ณ ที่จ่าย) ต้องถูกต้อง ไม่งั้นสรรพากรไม่รับทั้งไฟล์ · "
            + "ตั้งค่าเลขผู้เสียภาษีบริษัท ที่หน้า ตั้งค่า → ข้อมูลบริษัท (ช่อง \"เลขผู้เสียภาษี 13 หลัก\") แล้วสร้างไฟล์ใหม่ · "
            + "ระหว่างนี้ดูยอดได้จาก \"รายงานตรวจทาน\"",
            "PND1-WITHHOLDER-TAXID");
    }

    /// <summary>
    /// <b>ด่านของไฟล์นำเข้า ภ.ง.ด.1/1ก ที่ขึ้นกับผู้หัก — ตัวตัดสินเดียว</b> (ฝ่ายค้าน W1c P2-4/P2-5): ช่อง 2 เลขผู้เสียภาษีบริษัท
    /// (<see cref="WithholderTaxIdBlocker"/>) แล้วช่อง 4 สาขาผู้หัก 4 หลัก (<see cref="WithholderBranch"/>) · คืนข้อยกเว้น (มี <c>RuleCode</c> +
    /// ข้อความไทยพร้อมทางไปต่อ) โดย<b>ไม่โยน</b> ⇒ ผู้เรียกเลือกได้: ไฟล์นำเข้าโยน · พรีวิว/รายงานตรวจทานแสดงเป็นคำเตือน ·
    /// รอบเงินเดือนบันทึกเหตุที่ไม่มีไฟล์แนบ · null = ออกไฟล์นำเข้าได้
    /// </summary>
    public static BusinessRuleException? ImportFileBlocker(string? companyTaxId, string? branchCode, string formName)
    {
        var taxId = WithholderTaxIdBlocker(companyTaxId, formName);
        if (taxId != null) return taxId;
        var five = TaxBranchCode.Normalize(branchCode);
        return five[0] != '0' ? BranchBlocker(five) : null;
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
    /// รหัสไปรษณีย์: ช่องที่เก็บแยกก่อน แล้วค่อยหาในที่อยู่ด้วยตัวตัดสินเดียว <see cref="ContactDataHygiene.PostalCodeOf"/> (ไม่หยิบบ้านเลขที่/เลขห้อง/เลขในเบอร์โทร) — ถูกตัดออกจากข้อความ ·
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

    /// <summary>ตัด pipe + <b>อักขระควบคุมทุกตัว</b> (แท็บ · ขึ้นบรรทัด · C0/C1 · ตัวคั่นบรรทัด/ย่อหน้า U+2028/2029) เป็นช่องว่างทีละตัว —
    /// .txt คั่นด้วย pipe ไม่มี quoting · ฝ่ายค้าน ภ.ง.ด.1 P2-7: เดิมตัดแค่ <c>|</c> <c>\r</c> <c>\n</c> ⇒ "ใจ\tดี" หลุดเข้าไฟล์ยื่น</summary>
    private static string Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            // zero-width space / BOM กลางข้อความ — ไม่มีความหมาย (พบบ่อยในข้อความไทยที่ก๊อปจากเว็บ) ⇒ ตัดทิ้ง (W1c P2-3 · ทุกรูปแบบไฟล์)
            if (ch is '\u200B' or '\uFEFF') continue;
            sb.Append(ch == '|' || char.IsControl(ch) || ch is '\u2028' or '\u2029' ? ' ' : ch);
        }
        return sb.ToString().Trim();
    }

    private static Dictionary<string, object?> SheetRow(int width, params object?[] cells)
    {
        var d = new Dictionary<string, object?>(width);
        for (var i = 0; i < width; i++)
            d[ColumnKey(i)] = i < cells.Length ? cells[i] : null;
        return d;
    }

    private static string ColumnKey(int i) => ((char)('A' + i)).ToString();
}
