using System.Text;
using Accounting.Helpers;
using Accounting.Models.DTOs.Tax;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using MiniExcelLibs;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 2026-10-14 ทีม X1 — ไฟล์ ภ.ง.ด.1/1ก (.txt นำเข้าเว็บสรรพากร · CSV · Excel) จากตัวตั้งเดียว <see cref="Pnd1FileFormat"/>
/// + ตัวประกอบรายการ <see cref="Pnd1FilingData.Build"/> · ล็อกสองทิศ: รูปแบบเก่าที่พิสูจน์แล้วว่าใช้ไม่ได้ (H|/T| · dd/MM/พ.ศ. ·
/// รหัสคำนำหน้าตัวเลข) ต้องไม่กลับมา และสามรูปแบบต้องได้ค่าเดียวกันทุกช่อง · ข้อมูลทดสอบสังเคราะห์ (ไม่มี PII จริง)
/// </summary>
public class Pnd1FileFormatTests
{
    private static Pnd1FileFormat.Row Salary(string code = "E001", decimal income = 50000m, decimal tax = 2291.67m) => new(
        Category: Pnd1FileFormat.GeneralSalary, PayeeTaxId: "1-1017-00001-23-7", Title: "นาย",
        FirstName: "สมชาย", LastName: "ใจดี", PayDate: new DateTime(2026, 9, 30),
        IncomeAmount: income, TaxAmount: tax, Condition: 1, Address: "1 ถนนสุขุมวิท", SourceRef: code);

    // ───────── .txt ─────────

    [Fact]
    public void Txt_ไม่มีบรรทัดH_T_D_และวันที่เป็นddMMyyyy_คศ()
    {
        var body = Assert.Single(Pnd1FileFormat.BuildTextPerCategory(new[] { Salary(), Salary("E002") })).Text;
        var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain(lines, l => l.StartsWith("H|") || l.StartsWith("T|") || l.StartsWith("D|"));
        Assert.EndsWith("\r\n", body);
        var c = lines[0].Split('|');
        Assert.Equal(Pnd1FileFormat.TextColumnCount, c.Length);
        Assert.Equal("1", c[0]);                      // ลำดับ
        Assert.Equal("1101700001237", c[1]);          // เลขผู้เสียภาษี — ตัดขีดเหลือ 13 หลัก
        Assert.Equal("นาย", c[2]);                    // คำนำหน้า = ข้อความไทย (ไม่ใช่รหัสตัวเลข)
        Assert.Equal("สมชาย", c[3]);
        Assert.Equal("ใจดี", c[4]);
        Assert.Equal("30092026", c[5]);               // ddMMyyyy ค.ศ. ไม่มี slash (ไม่ใช่ 30/09/2569)
        Assert.Equal("50000.00", c[6]);
        Assert.Equal("2291.67", c[7]);
        Assert.Equal("1", c[8]);                      // เงื่อนไข หัก ณ ที่จ่าย
        Assert.Equal("1", c[9]);                      // ประเภทเงินได้ (1)
        Assert.Equal("2", lines[1].Split('|')[0]);    // ลำดับเดินต่อ
    }

    [Fact]
    public void Txt_เลขผู้เสียภาษีว่าง_ไม่แต่งเลขศูนย์()
    {
        var c = Pnd1FileFormat.DetailRow(1, Salary() with { PayeeTaxId = null }).Split('|');
        Assert.Equal("", c[1]);   // ไม่รู้ = ว่าง (PndTextFileFormat เดิมเติม 0 13 ตัว = เลขแต่ง)
    }

    [Fact]
    public void Txt_ตัดpipeและขึ้นบรรทัดในข้อความ_เงินปัดAwayFromZero()
    {
        var c = Pnd1FileFormat.DetailRow(1, Salary(income: 100.005m, tax: 0.125m) with { FirstName = "สม|ชาย\r\nx" }).Split('|');
        Assert.Equal(Pnd1FileFormat.TextColumnCount, c.Length);
        Assert.Equal("สม ชาย  x", c[3]);
        Assert.Equal("100.01", c[6]);   // banker's จะได้ 100.00
        Assert.Equal("0.13", c[7]);
    }

    [Fact]
    public void Txt_เงื่อนไขนอก123_ตกเป็น1_และออกให้ตลอดไปคงเป็น2()
    {
        Assert.Equal("1", Pnd1FileFormat.DetailRow(1, Salary() with { Condition = 9 }).Split('|')[8]);
        Assert.Equal("2", Pnd1FileFormat.DetailRow(1, Salary() with { Condition = 2 }).Split('|')[8]);
        Assert.Equal("3", Pnd1FileFormat.DetailRow(1, Salary() with { Condition = 3 }).Split('|')[8]);
    }

    [Fact]
    public void Txt_รายปี_ช่องที่6เป็นที่อยู่()
    {
        var c = Pnd1FileFormat.DetailRow(1, Salary(), annual: true).Split('|');
        Assert.Equal("1 ถนนสุขุมวิท", c[5]);
    }

    [Fact]
    public void Txt_แยกไฟล์ต่อประเภท_ลำดับเริ่มใหม่ทุกไฟล์()
    {
        var rows = new[]
        {
            Salary(), Salary("E002"),
            Salary("WHT-1") with { Category = Pnd1FileFormat.Section40_2Resident },
        };
        var files = Pnd1FileFormat.BuildTextPerCategory(rows);
        Assert.Equal(new[] { 1, 4 }, files.Select(f => f.Category).ToArray());
        var cat4 = files[1].Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Single().Split('|');
        Assert.Equal("1", cat4[0]);
        Assert.Equal("4", cat4[9]);
    }

    // ───────── CSV ─────────

    [Fact]
    public void Csv_มีBOM_หัวไทย_ค่าเท่ากับtxt_ตัวเลขไม่ถูกเติมอัญประกาศ()
    {
        var bytes = Pnd1FileFormat.BuildCsv(new[] { Salary() });
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("ลำดับที่,เลขประจำตัวผู้เสียภาษีอากร,คำนำหน้าชื่อ,ชื่อ,ชื่อสกุล", lines[0]);
        var csv = lines[1].Split(',');
        var txt = Pnd1FileFormat.DetailRow(1, Salary()).Split('|');
        Assert.Equal(txt, csv.Take(Pnd1FileFormat.TextColumnCount).ToArray());   // สองรูปแบบห้าม drift
        Assert.Equal("E001", csv[10]);                                            // อ้างอิงกลับ (CSV/Excel เท่านั้น)
    }

    [Fact]
    public void Csv_ชื่อที่ขึ้นต้นด้วยสูตร_ถูกกัน_แต่ชื่อปกติไม่ถูกแตะ()
    {
        var text = Encoding.UTF8.GetString(Pnd1FileFormat.BuildCsv(new[] { Salary() with { FirstName = "=HYPERLINK(\"x\")" } }));
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\"", text);
        var normal = Encoding.UTF8.GetString(Pnd1FileFormat.BuildCsv(new[] { Salary() }));
        Assert.DoesNotContain("'", normal);
    }

    // ───────── Excel ─────────

    [Fact]
    public void Xlsx_ชีตสรุปมีครบ5ประเภท_ชีตรายการเก็บเลข13หลักเป็นข้อความ()
    {
        var rows = new[]
        {
            Salary() with { PayeeTaxId = "0105556000001" },   // เลขขึ้นต้น 0 ต้องไม่หาย
            Salary("WHT-1", 20000m, 600m) with { Category = Pnd1FileFormat.Section40_2Resident },
        };
        var bytes = Pnd1FileFormat.BuildXlsx(rows, new Pnd1FileFormat.Header("บริษัท ทดสอบ จำกัด", "0105556000001", "00000", "กันยายน 2569", false));
        using var ms = new MemoryStream(bytes);
        var names = MiniExcel.GetSheetNames(ms);
        Assert.Equal(new[] { Pnd1FileFormat.SummarySheetName, Pnd1FileFormat.SheetName(1), Pnd1FileFormat.SheetName(4) }, names.ToArray());

        ms.Position = 0;
        var summary = MiniExcel.Query(ms, useHeaderRow: false, sheetName: Pnd1FileFormat.SummarySheetName)
            .Cast<IDictionary<string, object>>().ToList();
        var categoryRows = summary.Where(r => (r["A"]?.ToString() ?? "").StartsWith("(")).ToList();
        Assert.Equal(5, categoryRows.Count);
        Assert.Equal(1m, Convert.ToDecimal(categoryRows[0]["B"]));
        Assert.Equal(2291.67m, Convert.ToDecimal(categoryRows[0]["D"]));
        Assert.Equal(0m, Convert.ToDecimal(categoryRows[1]["B"]));            // (2) ว่างแต่ต้องมีบรรทัด
        Assert.Equal(600m, Convert.ToDecimal(categoryRows[3]["D"]));
        var total = summary.Single(r => (r["A"]?.ToString() ?? "") == "รวม");
        Assert.Equal(2891.67m, Convert.ToDecimal(total["D"]));

        ms.Position = 0;
        var sheet1 = MiniExcel.Query(ms, useHeaderRow: false, sheetName: Pnd1FileFormat.SheetName(1))
            .Cast<IDictionary<string, object>>().ToList();
        Assert.Equal("เลขประจำตัวผู้เสียภาษีอากร", sheet1[0]["B"]?.ToString());
        Assert.Equal("0105556000001", sheet1[1]["B"]?.ToString());   // ข้อความ — 0 นำหน้าไม่หาย
        Assert.Equal("30092026", sheet1[1]["F"]?.ToString());        // วันที่เป็นข้อความ (01… ไม่กลายเป็น 1…)
        Assert.Equal(50000m, Convert.ToDecimal(sheet1[1]["G"]));      // เงินเป็นตัวเลข
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
    public void ParseFormat_รับค่าที่รู้จัก(string? input, string expected)
        => Assert.Equal(expected, Pnd1FileFormat.ParseFormat(input));

    [Fact]
    public void ParseFormat_ค่าแปลก_ล้มดัง_ไม่เดา()
        => Assert.Throws<BusinessRuleException>(() => Pnd1FileFormat.ParseFormat("pdf"));

    [Fact]
    public void SheetName_ไม่เกิน31ตัว_ไม่มีอักขระต้องห้าม()
    {
        foreach (var n in Enumerable.Range(1, 5).Select(Pnd1FileFormat.SheetName).Append(Pnd1FileFormat.SummarySheetName))
        {
            Assert.True(n.Length <= 31, n);
            Assert.True(n.IndexOfAny(new[] { ':', '\\', '/', '?', '*', '[', ']' }) < 0, n);
        }
    }

    // ───────── Pnd1FilingData.Build (pure) ─────────

    private static (Employee Emp, PayrollDetail Detail) Payroll(string code, string citizen, decimal gross, decimal taxable, decimal tax,
        string status = "Paid", PayrollRun? run = null)
    {
        var emp = new Employee { EmployeeCode = code, TitleTh = "นาย", FirstNameTh = "ชื่อ" + code, LastNameTh = "สกุล" + code, CitizenId = citizen };
        run ??= new PayrollRun { Year = 2026, Month = 9, PayDate = new DateTime(2026, 9, 30), Status = status };
        var d = new PayrollDetail { Employee = emp, EmployeeId = Guid.NewGuid(), PayrollRun = run, GrossIncome = gross, TaxableGross = taxable, WithholdingTax = tax };
        return (emp, d);
    }

    private static WithholdingTaxCert Cert(string no, string? taxId, string name, string incomeCode, decimal income, decimal tax,
        Guid? sourceRun = null, string country = "TH", WithholdingTaxCertType type = WithholdingTaxCertType.Withhold)
    {
        var c = new WithholdingTaxCert
        {
            CertificateNumber = no, TaxFormType = TaxType.WithholdingTax1, TaxYear = 2026, TaxMonth = 9,
            CertificateType = type, Status = WithholdingTaxCertStatus.Issued, TotalIncomeAmount = income, TotalTaxAmount = tax,
            SourcePayrollRunId = sourceRun,
            PayeeContact = new Contact { Name = name, TaxId = taxId, CountryCode = country },
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
        Assert.Equal(2891.67m + 300m, r.TotalTax);
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
    public void Build_ไม่มีเลขผู้เสียภาษี_เตือน_ไม่แต่งเลข()
    {
        var a = Payroll("E001", "", 50000m, 50000m, 2291.67m);
        var r = Pnd1FilingData.Build(new[] { a.Detail }, Array.Empty<WithholdingTaxCert>(), annual: false);
        Assert.Null(r.Rows.Single().PayeeTaxId);
        Assert.Contains(r.Notes, n => n.Contains("ไม่มีเลขประจำตัวผู้เสียภาษี"));
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
}
