using System.Text;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using MiniExcelLibs;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ไฟล์ ภ.ง.ด.1/1ก ตาม <b>"Format กลางโปรแกรมบันทึก(Import)ข้อมูลยื่นด้วยสื่อฯ ภ.ง.ด.1,1ก และ 1ก พิเศษ"</b> ของ WHTSVS
/// (ภาพสเปกที่เจ้าของส่ง 2026-10-16 · EXPORT_AUDIT §"ภ.ง.ด.1 เทียบสเปก WHTSVS") จากตัวตั้งเดียว <see cref="Pnd1FileFormat"/>
/// + ตัวประกอบรายการ <see cref="Pnd1FilingData.Build"/> · ล็อกสองทิศ: แถวตัวอย่างในสเปกต้องออกมาตรงทุกไบต์ และรูปแบบเดิม
/// (10 ช่อง · ลำดับที่ · วันที่ ค.ศ. · zip ต่อประเภท · Excel มีหัว/หลายชีต) ต้องไม่กลับมา · ข้อมูลทดสอบสังเคราะห์/ตัวอย่างในสเปก (ไม่มี PII จริง)
/// </summary>
public class Pnd1FileFormatTests
{
    // ───────── แถวตัวอย่างในภาพสเปก (ตัวอักษรต่อตัวอักษร) ─────────

    private const string SpecExamplePnd1 =
        "00|0150023365001|3101727494|0000|3301900021121|4598654653|นางสาว|ศิริประภา|ค่าพิมพ์ณ|78/4 ต.พระองค์ดำ ต.ในเมือง|จ.พิษณุโลก|10140|01|2545|1|31012545|3|60550.23|1805.00|1";
    private const string SpecExamplePnd1k =
        "00|0150023365001|3101727494|0000|3301900021121|4598654653|นางสาว|ศิริประภา|ค่าพิมพ์ณ|78/4 ต.พระองค์ดำ ต.ในเมือง|จ.พิษณุโลก|65000|00|2545|1|31122545|3|60550.23|1805.00|1";

    /// <summary>ข้อมูลที่ "เท่ากับ" แถวตัวอย่าง — ที่อยู่เป็นข้อความเดียวพร้อมรหัสไปรษณีย์ (แบบที่ทะเบียนเก็บ) · ปี 2545 = ค.ศ. 2002</summary>
    private static Pnd1FileFormat.Row SpecRow(DateTime payDate, string postcode) => new(
        Category: Pnd1FileFormat.GeneralSalary, PayeeTaxId: "3301900021121", Title: "นางสาว",
        FirstName: "ศิริประภา", LastName: "ค่าพิมพ์ณ", PayDate: payDate,
        IncomeAmount: 60550.23m, TaxAmount: 1805m, Condition: 1,
        Address: $"78/4 ต.พระองค์ดำ ต.ในเมือง จ.พิษณุโลก {postcode}", TaxRate: 3m, PayeeTaxId10: "4598654653");

    private static readonly Pnd1FileFormat.Filer SpecFilerMonth = new("0150023365001", "00000", 2002, 1, TaxId10: "3101727494");
    private static readonly Pnd1FileFormat.Filer SpecFilerYear = new("0150023365001", "00000", 2002, null, TaxId10: "3101727494");

    [Fact]
    public void ตัวอย่างในสเปก_ภงด1_ออกมาตรงทุกไบต์()
    {
        var text = Pnd1FileFormat.BuildText(new[] { SpecRow(new DateTime(2002, 1, 31), "10140") }, SpecFilerMonth);
        Assert.Equal(Encoding.UTF8.GetBytes(SpecExamplePnd1 + "\r\n"), Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void ตัวอย่างในสเปก_ภงด1ก_เดือนภาษี00_ออกมาตรงทุกไบต์()
    {
        var line = Pnd1FileFormat.DetailRow(SpecRow(new DateTime(2002, 12, 31), "65000"), SpecFilerYear);
        Assert.Equal(SpecExamplePnd1k, line);
        Assert.Equal("00", line.Split('|')[12]);
    }

    [Fact]
    public void สเปก20ช่อง_ความยาวตามภาพสเปก()
    {
        Assert.Equal(Pnd1FileFormat.FieldCount, Pnd1FileFormat.Spec.Count);
        Assert.Equal(Enumerable.Range(1, 20), Pnd1FileFormat.Spec.Select(s => s.No));
        Assert.Equal(new[] { 2, 13, 10, 4, 13, 10, 40, 80, 80, 80, 80, 5, 2, 4, 1, 8, 3, 13, 13, 1 },
            Pnd1FileFormat.Spec.Select(s => s.Length).ToArray());
        Assert.Equal(new[] { 18, 19 }, Pnd1FileFormat.Spec.Where(s => s.Kind == Pnd1FileFormat.FieldKind.Numeric).Select(s => s.No));
        Assert.Equal("**", Pnd1FileFormat.Spec[7].Flag);   // ชื่อ ต้องมีค่า
        Assert.Equal("PND1.txt", Pnd1FileFormat.TextFileName);
        Assert.Equal("Sheet1", Pnd1FileFormat.ExcelSheetName);
    }

    // ───────── ทิศตรงข้าม: รูปแบบเดิม (ใบแนบกระดาษ 10 ช่อง) ห้ามกลับมา ─────────

    private static Pnd1FileFormat.Row Salary(string code = "E001", decimal income = 50000m, decimal tax = 2291.67m) => new(
        Category: Pnd1FileFormat.GeneralSalary, PayeeTaxId: "1-1017-00001-23-7", Title: "นาย",
        FirstName: "สมชาย", LastName: "ใจดี", PayDate: new DateTime(2026, 9, 30),
        IncomeAmount: income, TaxAmount: tax, Condition: 1,
        Address: "1 ถ.สุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพมหานคร 10110", SourceRef: code);

    private static readonly Pnd1FileFormat.Filer Sep2026 = new("0105556000001", "00000", 2026, 9);

    [Fact]
    public void ทิศตรงข้าม_ไม่มีลำดับที่_ไม่มีHT_วันที่เป็นพศ_ไม่ใช่คศ()
    {
        var body = Pnd1FileFormat.BuildText(new[] { Salary(), Salary("E002") }, Sep2026);
        var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("\r\n", body);
        Assert.DoesNotContain(lines, l => l.StartsWith("H|") || l.StartsWith("T|") || l.StartsWith("D|"));
        var c = lines[0].Split('|');
        Assert.Equal(20, c.Length);
        Assert.Equal("00", c[0]);                 // ลักษณะการยื่นแบบ ไม่ใช่ "1" (ลำดับที่เดิม)
        Assert.Equal("00", lines[1].Split('|')[0]);
        Assert.Equal("1101700001237", c[4]);      // เลขผู้ถูกหัก — ตัดขีดเหลือ 13 หลัก
        Assert.Equal("นาย", c[6]);                // คำนำหน้าข้อความไทย
        Assert.Equal("09", c[12]);
        Assert.Equal("2569", c[13]);
        Assert.Equal("30092569", c[15]);          // DDMMYYYY พ.ศ.
        Assert.DoesNotContain("30092026", lines[0]);
        Assert.Equal("0", c[16]);                 // อัตราก้าวหน้า หาค่าไม่ได้ ⇒ 0 (ข้อ 9)
        Assert.Equal("50000.00", c[17]);
        Assert.Equal("2291.67", c[18]);
    }

    [Fact]
    public void ไฟล์เดียวทุกประเภทเงินได้_รหัสเงินได้อยู่ช่อง15()
    {
        var rows = new[] { Salary(), Salary("WHT-1", 20000m, 600m) with { Category = Pnd1FileFormat.Section40_2Resident, TaxRate = 3m } };
        var lines = Pnd1FileFormat.BuildText(rows, Sep2026).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(new[] { "1", "4" }, lines.Select(l => l.Split('|')[14]).ToArray());
        Assert.Equal("3", lines[1].Split('|')[16]);
    }

    // ───────── ช่องว่าง = || · ช่อง * เติม 0 · ความยาวไม่เกินสเปก ─────────

    [Fact]
    public void ช่องว่าง_เป็นpipeติดกัน_และช่องดาวเดียวเติมศูนย์เต็มความยาว()
    {
        var r = Salary() with { PayeeTaxId = null, Title = null, LastName = null, Address = null };
        var line = Pnd1FileFormat.DetailRow(r, Sep2026 with { BranchCode = null });
        var c = line.Split('|');
        Assert.Equal(20, c.Length);
        Assert.Equal("0000", c[3]);               // สาขาว่าง ⇒ 0 × 4
        Assert.Equal(new string('0', 13), c[4]);  // เลขผู้ถูกหักว่าง ⇒ 0 × 13 (ช่อง * ตามสเปก — Notes เตือนแยก)
        Assert.Equal(new string('0', 10), c[2]);  // เลข 10 หลักผู้หัก — ระบบไม่เก็บ
        Assert.Equal(new string('0', 10), c[5]);  // เลข 10 หลักผู้มีเงินได้ — ระบบไม่เก็บ
        Assert.Contains("|0000000000||สมชาย||||", line);   // คำนำหน้า · นามสกุล · ที่อยู่ 1–2 · รหัสไปรษณีย์ว่าง = || ติดกัน
        Assert.Equal("", c[11]);                  // รหัสไปรษณีย์ไม่รู้ ⇒ ว่าง (ไม่แต่ง 00000 — ช่อง *** ไม่ใช่ *)
    }

    [Fact]
    public void ความยาวถูกตัดตามสเปก_และแจ้งว่าตัด_ทิศตรงข้ามแถวปกติไม่ถูกแตะ()
    {
        var longName = new string('ก', 100);
        var longAddr = string.Join(" ", Enumerable.Repeat("ถนนยาวมากมาย", 20)) + " จ.ชลบุรี 20000";
        var r = Salary() with { Title = new string('ข', 50), FirstName = longName, LastName = longName, Address = longAddr };
        var c = Pnd1FileFormat.Fields(r, Sep2026);
        for (var i = 0; i < 20; i++)
            if (Pnd1FileFormat.Spec[i].Kind == Pnd1FileFormat.FieldKind.Character)
                Assert.True(c[i].Length <= Pnd1FileFormat.Spec[i].Length, $"ช่อง {i + 1} ยาว {c[i].Length}");
        Assert.Equal(40, c[6].Length);
        Assert.Equal(80, c[7].Length);
        Assert.Equal("20000", c[11]);
        Assert.True(Pnd1FileFormat.Overflows(r));
        Assert.False(Pnd1FileFormat.Overflows(Salary()));
        var normal = Pnd1FileFormat.Fields(Salary(), Sep2026);
        Assert.Equal("สมชาย", normal[7]);
        Assert.Equal("ใจดี", normal[8]);
    }

    [Fact]
    public void ตัดpipeและขึ้นบรรทัดในข้อความ_เงินปัดAwayFromZero()
    {
        var c = Pnd1FileFormat.DetailRow(Salary(income: 100.005m, tax: 0.125m) with { FirstName = "สม|ชาย\r\nx" }, Sep2026).Split('|');
        Assert.Equal(20, c.Length);
        Assert.Equal("สม ชาย  x", c[7]);
        Assert.Equal("100.01", c[17]);   // banker's จะได้ 100.00
        Assert.Equal("0.13", c[18]);
    }

    [Fact]
    public void เงื่อนไขนอก123_ตกเป็น1_และออกให้ตลอดไปคงเป็น2()
    {
        Assert.Equal("1", Pnd1FileFormat.Fields(Salary() with { Condition = 9 }, Sep2026)[19]);
        Assert.Equal("2", Pnd1FileFormat.Fields(Salary() with { Condition = 2 }, Sep2026)[19]);
        Assert.Equal("3", Pnd1FileFormat.Fields(Salary() with { Condition = 3 }, Sep2026)[19]);
    }

    // ───────── ตัวตัดสินรายช่อง ─────────

    [Theory]
    [InlineData(null, "0000")]
    [InlineData("", "0000")]
    [InlineData("00000", "0000")]
    [InlineData("00001", "0001")]
    [InlineData("1", "0001")]
    [InlineData("01234", "1234")]
    public void สาขาผู้หัก_4หลัก_จากรหัส5หลัก(string? code, string expected)
        => Assert.Equal(expected, Pnd1FileFormat.Fields(Salary(), Sep2026 with { BranchCode = code })[3]);

    [Fact]
    public void สาขาผู้หัก_ลง4หลักไม่ได้_ล้มดัง_ไม่ตัดเลขทิ้ง()
        => Assert.Equal("PND1-BRANCH-4DIGIT",
            Assert.Throws<BusinessRuleException>(() => Pnd1FileFormat.Fields(Salary(), Sep2026 with { BranchCode = "10001" })).RuleCode);

    [Theory]
    [InlineData(null, "0")]
    [InlineData("0", "0")]
    [InlineData("3", "3")]
    [InlineData("15", "15")]
    [InlineData("3.5", "3.5")]
    [InlineData("4.58", "0")]      // ยาวเกิน 3 ตัว ⇒ หาค่าที่ลงช่องได้ไม่ได้ ⇒ 0 (ข้อ 9)
    public void อัตราภาษี_ไม่เกิน3ตัว(string? rate, string expected)
        => Assert.Equal(expected, Pnd1FileFormat.Rate(rate == null ? null : decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void เงินเกิน13หลัก_ล้มดัง()
        => Assert.Throws<BusinessRuleException>(() => Pnd1FileFormat.Amount(10_000_000_000_000m));

    [Fact]
    public void วันที่_DDMMYYYY_พศ()
        => Assert.Equal("05012570", Pnd1FileFormat.ThaiDate(new DateTime(2027, 1, 5)));

    [Theory]
    [InlineData("1 ถ.สุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพมหานคร 10110", null, "1 ถ.สุขุมวิท แขวงคลองเตย", "เขตคลองเตย กรุงเทพมหานคร", "10110")]
    [InlineData("44 หมู่ 9 ต.หนองเหียง อ.พนัสนิคม จ.ชลบุรี 20140", null, "44 หมู่ 9 ต.หนองเหียง", "อ.พนัสนิคม จ.ชลบุรี", "20140")]
    [InlineData("9 ถนนพระราม 4", "10500", "9 ถนนพระราม 4", "", "10500")]                 // รหัสไปรษณีย์จากช่องที่เก็บแยก
    [InlineData("12/861 ซอย 5 จ.นนทบุรี รหัสไปรษณีย์ 11120", null, "12/861 ซอย 5", "จ.นนทบุรี", "11120")]
    [InlineData("บ้านเลขที่ 7 ไม่มีจังหวัด", null, "บ้านเลขที่ 7 ไม่มีจังหวัด", "", "")]  // ไม่มีรหัส ⇒ ว่าง (ไม่แต่ง)
    public void ที่อยู่แยก2บรรทัด_และรหัสไปรษณีย์(string address, string? postal, string line1, string line2, string post)
    {
        var p = Pnd1FileFormat.AddressParts(Salary() with { Address = address, PostalCode = postal });
        Assert.Equal(line1, p.Line1);
        Assert.Equal(line2, p.Line2);
        Assert.Equal(post, p.PostalCode);
        Assert.False(p.Truncated);
    }

    [Fact]
    public void ที่อยู่บรรทัดแรกยาวเกิน80_ตัดที่ช่องว่าง_ส่วนเกินไปบรรทัด2_ไม่หาย()
    {
        var head = string.Join(" ", Enumerable.Repeat("หมู่บ้านสวนสวย", 7));   // 7×14 + 6 = 104 ตัว
        var p = Pnd1FileFormat.AddressParts(Salary() with { Address = head + " จ.ระยอง 21000" });
        Assert.True(p.Line1.Length <= 80);
        Assert.True(p.Line2.Length <= 80);
        Assert.EndsWith("จ.ระยอง", p.Line2);
        Assert.Equal(head + " จ.ระยอง", p.Line1 + " " + p.Line2);
        Assert.False(p.Truncated);
    }

    // ───────── Excel นำเข้า: Sheet1 · ไม่มีหัว · ค่าเท่ากับ .txt ─────────

    [Fact]
    public void Xlsx_ชีตเดียวชื่อSheet1_ไม่มีแถวหัว_ทุกเซลล์เท่ากับtxt()
    {
        var rows = new[]
        {
            Salary() with { PayeeTaxId = "0105556000001" },   // เลขขึ้นต้น 0 ต้องไม่หาย
            Salary("WHT-1", 20000m, 600m) with { Category = Pnd1FileFormat.Section40_2Resident, TaxRate = 3m },
        };
        var bytes = Pnd1FileFormat.BuildXlsx(rows, Sep2026);
        using var ms = new MemoryStream(bytes);
        Assert.Equal(new[] { "Sheet1" }, MiniExcel.GetSheetNames(ms).ToArray());
        ms.Position = 0;
        var read = MiniExcel.Query(ms, useHeaderRow: false, sheetName: "Sheet1").Cast<IDictionary<string, object>>().ToList();
        Assert.Equal(2, read.Count);                                      // ไม่มีแถวหัว — แถวแรกคือข้อมูล
        Assert.Equal("00", read[0]["A"]?.ToString());
        var txt = Pnd1FileFormat.BuildText(rows, Sep2026).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < rows.Length; i++)
        {
            var cells = Enumerable.Range(0, 20).Select(c => read[i][((char)('A' + c)).ToString()]?.ToString() ?? "").ToArray();
            Assert.Equal(txt[i].Split('|'), cells);                       // สองรูปแบบห้าม drift
        }
        Assert.Equal("0105556000001", read[0]["E"]?.ToString());
        Assert.Equal("0000", read[0]["D"]?.ToString());
        Assert.Equal("09", read[0]["M"]?.ToString());
        Assert.False(read[0].ContainsKey("U") && read[0]["U"] != null);   // 20 คอลัมน์ ไม่มีคอลัมน์อ้างอิง
    }

    // ───────── CSV: ไม่มีหัว · ค่าเท่ากับ .txt ─────────

    [Fact]
    public void Csv_ไม่มีแถวหัว_มีBOM_ค่าเท่ากับtxt_ตัวเลขไม่ถูกเติมอัญประกาศ()
    {
        var bytes = Pnd1FileFormat.BuildCsv(new[] { Salary() }, Sep2026);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Equal(Pnd1FileFormat.DetailRow(Salary(), Sep2026).Split('|'), lines[0].Split(','));
    }

    [Fact]
    public void Csv_ชื่อที่ขึ้นต้นด้วยสูตร_ถูกกัน_แต่ชื่อปกติไม่ถูกแตะ()
    {
        var text = Encoding.UTF8.GetString(Pnd1FileFormat.BuildCsv(new[] { Salary() with { FirstName = "=HYPERLINK(\"x\")" } }, Sep2026));
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", text);
        var normal = Encoding.UTF8.GetString(Pnd1FileFormat.BuildCsv(new[] { Salary() }, Sep2026));
        Assert.DoesNotContain("'", normal);
    }

    // ───────── รายงานตรวจทาน (มีหัว — ไม่ใช่ไฟล์นำเข้า) ─────────

    [Fact]
    public void รายงานตรวจทาน_มีหัวคอลัมน์ตามสเปก_ชีตสรุปครบ5ประเภท_ไม่ใช่Sheet1()
    {
        var rows = new[]
        {
            Salary(),
            Salary("WHT-1", 20000m, 600m) with { Category = Pnd1FileFormat.Section40_2Resident },
        };
        var bytes = Pnd1FileFormat.BuildReviewXlsx(rows, Sep2026,
            new Pnd1FileFormat.Header("บริษัท ทดสอบ จำกัด", "0105556000001", "00000", "กันยายน 2569", false));
        using var ms = new MemoryStream(bytes);
        var names = MiniExcel.GetSheetNames(ms);
        Assert.Equal(new[] { Pnd1FileFormat.SummarySheetName, Pnd1FileFormat.ReviewRowsSheetName }, names.ToArray());
        Assert.DoesNotContain("Sheet1", names);   // ห้ามสับสนกับไฟล์นำเข้า

        ms.Position = 0;
        var summary = MiniExcel.Query(ms, useHeaderRow: false, sheetName: Pnd1FileFormat.SummarySheetName)
            .Cast<IDictionary<string, object>>().ToList();
        var categoryRows = summary.Where(r => (r["A"]?.ToString() ?? "").StartsWith("(")).ToList();
        Assert.Equal(5, categoryRows.Count);
        Assert.Equal(2291.67m, Convert.ToDecimal(categoryRows[0]["D"]));
        Assert.Equal(0m, Convert.ToDecimal(categoryRows[1]["B"]));            // (2) ว่างแต่ต้องมีบรรทัด
        Assert.Equal(600m, Convert.ToDecimal(categoryRows[3]["D"]));
        Assert.Equal(2891.67m, Convert.ToDecimal(summary.Single(r => (r["A"]?.ToString() ?? "") == "รวม")["D"]));

        ms.Position = 0;
        var detail = MiniExcel.Query(ms, useHeaderRow: false, sheetName: Pnd1FileFormat.ReviewRowsSheetName)
            .Cast<IDictionary<string, object>>().ToList();
        Assert.Equal("1. ลักษณะการยื่นแบบ", detail[0]["A"]?.ToString());
        Assert.Equal("20. เงื่อนไขการหักภาษี ณ ที่จ่าย", detail[0]["T"]?.ToString());
        Assert.Equal("1101700001237", detail[1]["E"]?.ToString());
        Assert.Equal(50000m, Convert.ToDecimal(detail[1]["R"]));          // เงินเป็นตัวเลขให้รวมยอดได้
        Assert.Equal("E001", detail[1]["U"]?.ToString());                  // อ้างอิงกลับ — รายงานตรวจทานเท่านั้น
    }

    [Fact]
    public void Summarize_คืนครบ5ประเภทเสมอ_แม้ไม่มีรายการ()
    {
        var t = Pnd1FileFormat.Summarize(Array.Empty<Pnd1FileFormat.Row>());
        Assert.Equal(5, t.Count);
        Assert.All(t, x => Assert.Equal(0, x.Count));
        Assert.Equal(Enumerable.Range(1, 5), t.Select(x => x.Category));
    }

    [Theory]
    [InlineData(null, "txt")]
    [InlineData("", "txt")]
    [InlineData("TXT", "txt")]
    [InlineData("csv", "csv")]
    [InlineData("excel", "xlsx")]
    [InlineData("xlsx", "xlsx")]
    [InlineData("review", "review")]
    public void ParseFormat_รับค่าที่รู้จัก(string? input, string expected)
        => Assert.Equal(expected, Pnd1FileFormat.ParseFormat(input));

    [Fact]
    public void ParseFormat_ค่าแปลก_ล้มดัง_ไม่เดา()
        => Assert.Throws<BusinessRuleException>(() => Pnd1FileFormat.ParseFormat("pdf"));

    [Fact]
    public void ชื่อชีตรายงานตรวจทาน_ไม่เกิน31ตัว_ไม่มีอักขระต้องห้าม()
    {
        foreach (var n in new[] { Pnd1FileFormat.SummarySheetName, Pnd1FileFormat.ReviewRowsSheetName })
        {
            Assert.True(n.Length <= 31, n);
            Assert.True(n.IndexOfAny(new[] { ':', '\\', '/', '?', '*', '[', ']' }) < 0, n);
        }
    }

    // ───────── Pnd1FilingData.Build (pure) ─────────

    private static (Employee Emp, PayrollDetail Detail) Payroll(string code, string citizen, decimal gross, decimal taxable, decimal tax,
        string status = "Paid", PayrollRun? run = null, string? address = "1 ถ.สุขุมวิท แขวงคลองเตย เขตคลองเตย กรุงเทพมหานคร 10110")
    {
        var emp = new Employee { EmployeeCode = code, TitleTh = "นาย", FirstNameTh = "ชื่อ" + code, LastNameTh = "สกุล" + code, CitizenId = citizen, Address = address };
        run ??= new PayrollRun { Year = 2026, Month = 9, PayDate = new DateTime(2026, 9, 30), Status = status };
        var d = new PayrollDetail { Employee = emp, EmployeeId = Guid.NewGuid(), PayrollRun = run, GrossIncome = gross, TaxableGross = taxable, WithholdingTax = tax };
        return (emp, d);
    }

    private static WithholdingTaxCert Cert(string no, string? taxId, string name, string incomeCode, decimal income, decimal tax,
        Guid? sourceRun = null, string country = "TH", WithholdingTaxCertType type = WithholdingTaxCertType.Withhold,
        ContactType contactType = ContactType.Individual)
    {
        var c = new WithholdingTaxCert
        {
            CertificateNumber = no, TaxFormType = TaxType.WithholdingTax1, TaxYear = 2026, TaxMonth = 9,
            CertificateType = type, Status = WithholdingTaxCertStatus.Issued, TotalIncomeAmount = income, TotalTaxAmount = tax,
            SourcePayrollRunId = sourceRun,
            PayeeContact = new Contact { Name = name, TaxId = taxId, CountryCode = country, ContactType = contactType,
                Address = "9 ถนนพระราม 4", SubDistrict = "สุริยวงศ์", District = "บางรัก", Province = "กรุงเทพมหานคร", PostalCode = "10500" },
        };
        c.Lines.Add(new WithholdingTaxCertLine { LineOrder = 1, IncomeTypeCode = incomeCode, PaymentDate = new DateTime(2026, 9, 15),
            IncomeAmount = income, TaxRate = income == 0 ? 0 : Math.Round(tax / income * 100m, 2, MidpointRounding.AwayFromZero), TaxAmount = tax });
        return c;
    }

    [Fact]
    public void Build_เงินได้ใช้ฐานภาษี_รวมคนภาษีศูนย์_ตัดรอบยังไม่อนุมัติ()
    {
        var a = Payroll("E001", "1101700001237", gross: 52000m, taxable: 50000m, tax: 2291.67m);
        var b = Payroll("E002", "3100500123458", gross: 12000m, taxable: 0m, tax: 0m);          // ข้อมูลเก่า ⇒ Gross
        var pending = Payroll("E009", "1509900045673", gross: 40000m, taxable: 40000m, tax: 1000m, status: "Calculated");
        var r = Pnd1FilingData.Build(new[] { a.Detail, b.Detail, pending.Detail }, Array.Empty<WithholdingTaxCert>(), annual: false);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(new[] { 50000m, 12000m }, r.Rows.Select(x => x.IncomeAmount).ToArray());
        Assert.Equal(0m, r.Rows[1].TaxAmount);
        Assert.Null(r.Rows[0].TaxRate);   // เงินเดือนอัตราก้าวหน้า ⇒ ไฟล์ลง 0
        Assert.True(r.HasPayrollRuns);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public void Build_ใบ50ทวิจากรอบไม่นับซ้ำ_ใบมือของผู้รับนอกรอบเข้าไฟล์_ประเภทตาม40_2และประเทศ()
    {
        var a = Payroll("E001", "1101700001237", 50000m, 50000m, 2291.67m);
        var certs = new[]
        {
            Cert("PND1-202609-E001", "1101700001237", "นาย ชื่อE001 สกุลE001", "1", 50000m, 2291.67m, sourceRun: Guid.NewGuid()),
            Cert("WHT-1", "3700600009996", "นายวิชัย นายหน้า", "2", 20000m, 600m),
            Cert("WHT-2", "3700600009996", "Mr. John Smith", "2", 10000m, 300m, country: "US"),
        };
        var r = Pnd1FilingData.Build(new[] { a.Detail }, certs, annual: false);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal(1, r.PayrollRowCount);
        Assert.Equal(2, r.CertRowCount);
        Assert.Equal(new[] { 1, 4, 5 }, r.Rows.Select(x => x.Category).ToArray());
        Assert.Equal("วิชัย", r.Rows[1].FirstName);
        Assert.Equal("นาย", r.Rows[1].Title);
        Assert.Equal(3m, r.Rows[1].TaxRate);          // อัตราจากบรรทัด 50 ทวิ ⇒ ช่อง 17
        Assert.Equal("10500", r.Rows[1].PostalCode);  // รหัสไปรษณีย์จากช่องที่เก็บแยกของผู้ติดต่อ
        Assert.Equal(2891.67m + 300m, r.TotalTax);
    }

    [Fact]
    public void Build_ผู้รับนิติบุคคล_ชื่อเต็มอยู่ช่องชื่อ_ไม่มีนามสกุลและคำนำหน้า()
    {
        var cert = Cert("WHT-J", "0105556000001", "บริษัท ตัวแทนดี จำกัด", "2", 10000m, 300m, contactType: ContactType.JuristicPerson);
        var row = Assert.Single(Pnd1FilingData.Build(Array.Empty<PayrollDetail>(), new[] { cert }, annual: false).Rows);
        Assert.Equal("บริษัท ตัวแทนดี จำกัด", row.FirstName);
        Assert.Equal("", row.LastName);
        Assert.Equal("", row.Title);
    }

    [Fact]
    public void Build_ใบมือของพนักงานในรอบ_ไม่เพิ่มแถว_แต่ส่งเงื่อนไขออกให้ตลอดไปให้แถวเงินเดือน()
    {
        var a = Payroll("E001", "1101700001237", 50000m, 50000m, 2291.67m);
        var manual = Cert("HR-1", "1101700001237", "นาย ชื่อE001 สกุลE001", "1", 50000m, 2291.67m, type: WithholdingTaxCertType.PayAlways);
        var r = Pnd1FilingData.Build(new[] { a.Detail }, new[] { manual }, annual: false);
        var row = Assert.Single(r.Rows);
        Assert.Equal(2, row.Condition);
    }

    [Fact]
    public void Build_บรรทัดประเภทไม่ใช่40_1หรือ40_2_ไม่ลงไฟล์_และเตือนพร้อมเลขที่ใบ()
    {
        var cert = Cert("WHT-9", "3700600009996", "นายวิชัย นายหน้า", "40(8)", 10000m, 300m);
        var r = Pnd1FilingData.Build(Array.Empty<PayrollDetail>(), new[] { cert }, annual: false);
        Assert.Empty(r.Rows);
        Assert.Contains(r.Notes, n => n.Contains("WHT-9") && n.Contains("300.00"));
        Assert.False(r.HasPayrollRuns);
    }

    [Fact]
    public void Build_ไม่มีเลขผู้เสียภาษี_เตือน_แถวไม่แต่งเลข()
    {
        var a = Payroll("E001", "", 50000m, 50000m, 2291.67m);
        var r = Pnd1FilingData.Build(new[] { a.Detail }, Array.Empty<WithholdingTaxCert>(), annual: false);
        Assert.Null(r.Rows.Single().PayeeTaxId);
        Assert.Contains(r.Notes, n => n.Contains("ไม่มีเลขประจำตัวผู้เสียภาษี"));
    }

    /// <summary>ช่อง 12 รหัสไปรษณีย์เป็น *** (ต้องถูกต้อง) ทั้งรายเดือนและรายปี — ไม่มี ⇒ เตือนพร้อมจำนวน (ไม่แต่ง 00000) ·
    /// ทิศตรงข้าม: ที่อยู่ที่มีรหัสไปรษณีย์ไม่ถูกเตือน</summary>
    [Fact]
    public void Build_ไม่มีรหัสไปรษณีย์_เตือนทั้งรายเดือน_ทิศตรงข้ามมีรหัสไม่เตือน()
    {
        var noPost = Payroll("E001", "1101700001237", 50000m, 50000m, 2291.67m, address: "1 ถนนสุขุมวิท");
        var r = Pnd1FilingData.Build(new[] { noPost.Detail }, Array.Empty<WithholdingTaxCert>(), annual: false);
        Assert.Contains(r.Notes, n => n.Contains("รหัสไปรษณีย์"));

        var ok = Payroll("E002", "3100500123458", 30000m, 30000m, 375m);
        var r2 = Pnd1FilingData.Build(new[] { ok.Detail }, Array.Empty<WithholdingTaxCert>(), annual: false);
        Assert.DoesNotContain(r2.Notes, n => n.Contains("รหัสไปรษณีย์"));
        Assert.Equal("10110", Pnd1FileFormat.AddressParts(r2.Rows.Single()).PostalCode);
    }

    [Fact]
    public void Build_รายปี_รวมยอดทั้งปีต่อคน_และเตือนที่อยู่ว่าง()
    {
        var run9 = new PayrollRun { Year = 2026, Month = 9, PayDate = new DateTime(2026, 9, 30), Status = "Paid" };
        var run10 = new PayrollRun { Year = 2026, Month = 10, PayDate = new DateTime(2026, 10, 31), Status = "Paid" };
        var emp = new Employee { EmployeeCode = "E001", TitleTh = "นาย", FirstNameTh = "สมชาย", LastNameTh = "ใจดี", CitizenId = "1101700001237" };
        var empId = Guid.NewGuid();
        var d9 = new PayrollDetail { Employee = emp, EmployeeId = empId, PayrollRun = run9, GrossIncome = 50000m, TaxableGross = 50000m, WithholdingTax = 2000m };
        var d10 = new PayrollDetail { Employee = emp, EmployeeId = empId, PayrollRun = run10, GrossIncome = 50000m, TaxableGross = 50000m, WithholdingTax = 2000m };
        var r = Pnd1FilingData.Build(new[] { d9, d10 }, Array.Empty<WithholdingTaxCert>(), annual: true);
        var row = Assert.Single(r.Rows);
        Assert.Equal(100000m, row.IncomeAmount);
        Assert.Equal(4000m, row.TaxAmount);
        Assert.Equal(new DateTime(2026, 10, 31), row.PayDate);   // ช่อง 16 ของ 1ก = วันจ่ายครั้งสุดท้าย
        Assert.Contains(r.Notes, n => n.Contains("ไม่มีที่อยู่"));
    }

    /// <summary>ประเภทเงินได้ของใบ 50 ทวิ มือ — ผ่านตารางกลาง ThaiWhtRateTable (0 = ไม่ลงไฟล์ + เตือน)</summary>
    [Theory]
    [InlineData("1", "TH", 1)]
    [InlineData("40(1)", "TH", 1)]
    [InlineData("2", "TH", 4)]
    [InlineData("40(2)", "", 4)]
    [InlineData("2", "SG", 5)]
    [InlineData("8", "TH", 0)]      // ภ.ง.ด.3 ไม่ใช่ ภ.ง.ด.1
    [InlineData("", "TH", 0)]
    public void Build_ประเภทเงินได้ของใบมือ_ใช้ตารางกลาง(string code, string country, int expected)
    {
        var cert = Cert("WHT-C", "3700600009996", "นายวิชัย นายหน้า", code, 10000m, 300m, country: country);
        var r = Pnd1FilingData.Build(Array.Empty<PayrollDetail>(), new[] { cert }, annual: false);
        if (expected == 0)
        {
            Assert.Empty(r.Rows);
            Assert.Contains(r.Notes, n => n.Contains("WHT-C"));
        }
        else
            Assert.Equal(expected, Assert.Single(r.Rows).Category);
    }

    // ───────── ฝ่ายค้านรอบส่งออก 2026-10-15 P1-3: ใบมือของพนักงานที่อยู่ในรอบเงินเดือน ─────────

    /// <summary>ทิศที่พัง: ค่านายหน้า 40(2) ของพนักงานที่อยู่ในรอบเงินเดือนเดือนเดียวกัน เดิมหายจากไฟล์เงียบ ๆ — ต้องลงไฟล์เป็นประเภท (4)</summary>
    [Fact]
    public void Build_ใบมือ40_2ของพนักงานในรอบ_ลงไฟล์เสมอ_ไม่ถูกตัด()
    {
        var a = Payroll("E001", "1101700001237", 50000m, 50000m, 2291.67m);
        var commission = Cert("WHT-COM", "1101700001237", "นาย ชื่อE001 สกุลE001", "2", 20000m, 600m);
        var r = Pnd1FilingData.Build(new[] { a.Detail }, new[] { commission }, annual: false);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(new[] { 1, 4 }, r.Rows.Select(x => x.Category).ToArray());
        Assert.Equal(2291.67m + 600m, r.TotalTax);
        Assert.DoesNotContain(r.Notes, n => n.Contains("WHT-COM"));
    }

    /// <summary>ทิศที่พัง: ใบมือ 40(1) ของเดือนที่คนนั้นไม่มีรอบเงินเดือน (เช่น โบนัสจ่ายนอกรอบ ต.ค.) ต้องลงไฟล์ของเดือนนั้น ·
    /// ทิศตรงข้าม: 40(1) เดือนเดียวกับรอบ = ซ้ำ ⇒ ไม่ลงซ้ำ + แจ้งใน Notes พร้อมเลขที่ใบ</summary>
    [Fact]
    public void Build_ใบมือ40_1_ตัดเฉพาะเดือนที่ซ้ำกับรอบ_และแจ้งทุกครั้งที่ตัด()
    {
        var sepRun = new PayrollRun { Year = 2026, Month = 9, PayDate = new DateTime(2026, 9, 30), Status = "Paid" };
        var a = Payroll("E001", "1101700001237", 50000m, 50000m, 2291.67m, run: sepRun);
        var dupSep = Cert("HR-SEP", "1101700001237", "นาย ชื่อE001 สกุลE001", "1", 50000m, 2291.67m);
        var bonusOct = Cert("HR-OCT", "1101700001237", "นาย ชื่อE001 สกุลE001", "1", 30000m, 1500m);
        bonusOct.TaxMonth = 10;

        var sep = Pnd1FilingData.Build(new[] { a.Detail }, new[] { dupSep }, annual: false);
        Assert.Single(sep.Rows);
        Assert.Contains(sep.Notes, n => n.Contains("HR-SEP") && n.Contains("ไม่ลงซ้ำ"));

        var oct = Pnd1FilingData.Build(Array.Empty<PayrollDetail>(), new[] { bonusOct }, annual: false);
        var row = Assert.Single(oct.Rows);
        Assert.Equal(1500m, row.TaxAmount);
        Assert.DoesNotContain(oct.Notes, n => n.Contains("ไม่ลงซ้ำ"));
    }

    /// <summary>ภ.ง.ด.1ก = ผลรวม ภ.ง.ด.1 ทุกเดือน (เงินได้/ภาษี) — เดิมรายปีตัดใบมือทุกใบของใครก็ตามที่อยู่ในรอบใดในปี</summary>
    [Fact]
    public void Build_รายปี_เท่ากับผลรวมรายเดือน_เมื่อมีใบมือของพนักงาน()
    {
        var sepRun = new PayrollRun { Year = 2026, Month = 9, PayDate = new DateTime(2026, 9, 30), Status = "Paid" };
        var a = Payroll("E001", "1101700001237", 50000m, 50000m, 2291.67m, run: sepRun);
        var dupSep = Cert("HR-SEP", "1101700001237", "นาย ชื่อE001 สกุลE001", "1", 50000m, 2291.67m);
        var commissionSep = Cert("WHT-COM", "1101700001237", "นาย ชื่อE001 สกุลE001", "2", 20000m, 600m);
        var bonusOct = Cert("HR-OCT", "1101700001237", "นาย ชื่อE001 สกุลE001", "1", 30000m, 1500m);
        bonusOct.TaxMonth = 10;
        var allCerts = new[] { dupSep, commissionSep, bonusOct };

        var sep = Pnd1FilingData.Build(new[] { a.Detail }, allCerts.Where(c => c.TaxMonth == 9).ToArray(), annual: false);
        var oct = Pnd1FilingData.Build(Array.Empty<PayrollDetail>(), allCerts.Where(c => c.TaxMonth == 10).ToArray(), annual: false);
        var year = Pnd1FilingData.Build(new[] { a.Detail }, allCerts, annual: true);

        Assert.Equal(sep.TotalTax + oct.TotalTax, year.TotalTax);
        Assert.Equal(sep.TotalIncome + oct.TotalIncome, year.TotalIncome);
        Assert.Equal(2291.67m + 600m + 1500m, year.TotalTax);
        Assert.Contains(year.Notes, n => n.Contains("HR-SEP"));
    }
}
