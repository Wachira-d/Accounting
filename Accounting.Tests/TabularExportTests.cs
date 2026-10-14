using System.Text;
using Accounting.Helpers;
using MiniExcelLibs;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบส่งออก 2026-10-14 (ทีม X3) — ตัวเขียนไฟล์ตารางกลาง <see cref="TabularExport"/>
/// · ล็อกสองทิศ (CLAUDE.md F2 ข้อ 8): ค่าที่ต้องถูกกัน (สูตร/ตัวคั่น/บรรทัดใหม่) ถูกกัน
/// และค่าที่ถูกอยู่แล้ว (ยอดติดลบ/ข้อความไทยปกติ/ตัวเลขใน Excel) ไม่ถูกแตะ
/// </summary>
public class TabularExportTests
{
    private sealed record Line(string Code, string Name, decimal Debit, decimal Credit, DateTime Date);

    private static readonly IReadOnlyList<TabularColumn<Line>> Cols = new[]
    {
        new TabularColumn<Line>("วันที่", l => l.Date, TabularCellKind.Date),
        new TabularColumn<Line>("รหัสบัญชี", l => l.Code),
        new TabularColumn<Line>("ชื่อบัญชี", l => l.Name),
        new TabularColumn<Line>("เดบิต", l => l.Debit, TabularCellKind.Money),
        new TabularColumn<Line>("เครดิต", l => l.Credit, TabularCellKind.Money),
    };

    private static string Csv(TabularSheet s)
    {
        var bytes = TabularExport.ToCsv(s);
        Assert.Equal(0xEF, bytes[0]); Assert.Equal(0xBB, bytes[1]); Assert.Equal(0xBF, bytes[2]);   // BOM
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }

    [Fact]
    public void CSV_มี_BOM_CRLF_หัวตารางไทย_และยอดติดลบเป็นตัวเลขไม่เติมอัญประกาศ()
    {
        var s = TabularExport.Sheet("GL", Cols, new[]
        {
            new Line("11100", "เงินสด", 1234.5m, 0m, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
            new Line("41000", "รายได้", 0m, -1500m, new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc)),
        });
        var text = Csv(s);
        var lines = text.Split("\r\n");
        Assert.Equal("วันที่,รหัสบัญชี,ชื่อบัญชี,เดบิต,เครดิต", lines[0]);
        Assert.Equal("2026-03-01,11100,เงินสด,1234.50,0.00", lines[1]);
        Assert.Equal("2026-03-02,41000,รายได้,0.00,-1500.00", lines[2]);   // ไม่ใช่ '-1500.00 และไม่มีคั่นหลักพัน
        Assert.Equal("", lines[^1]);                                       // จบด้วย CRLF
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public void CSV_ข้อความที่เป็นสูตร_ยังถูกกัน_และตัวคั่น_อัญประกาศ_ถูกครอบ()
    {
        var s = TabularExport.Sheet("x", Cols, new[]
        {
            new Line("-1500", "=HYPERLINK(\"http://x\")", 0m, 0m, new DateTime(2026, 1, 1)),
            new Line("1", "บริษัท ก, จำกัด", 0m, 0m, new DateTime(2026, 1, 1)),
        });
        var lines = Csv(s).Split("\r\n");
        // ข้อความในช่องข้อความที่ขึ้นต้นด้วย - หรือ = ถูกเติม ' (ช่องข้อความ ≠ ช่องตัวเลข)
        Assert.Equal("2026-01-01,'-1500,\"'=HYPERLINK(\"\"http://x\"\")\",0.00,0.00", lines[1]);
        Assert.Equal("2026-01-01,1,\"บริษัท ก, จำกัด\",0.00,0.00", lines[2]);
    }

    [Fact]
    public void ปัดเงินแบบ_AwayFromZero_ไม่ใช่_banker()
    {
        Assert.Equal("0.13", TabularExport.FormatCell(0.125m, TabularCellKind.Money));
        Assert.Equal("-0.13", TabularExport.FormatCell(-0.125m, TabularCellKind.Money));
        Assert.Equal("2.5", TabularExport.FormatCell(2.5000m, TabularCellKind.Number));
        Assert.Equal("", TabularExport.FormatCell(null, TabularCellKind.Money));
    }

    [Fact]
    public void วันที่_พศ_และเวลาประทับ_แสดงตามเวลาไทย()
    {
        // 2026-02-28 17:30 UTC = 2026-03-01 00:30 เวลาไทย
        var ts = new DateTime(2026, 2, 28, 17, 30, 0, DateTimeKind.Utc);
        Assert.Equal("01/03/2569", TabularExport.FormatCell(ts, TabularCellKind.DateThai));
        Assert.Equal("2026-03-01", TabularExport.FormatCell(ts, TabularCellKind.Date));
        Assert.Equal("2026-03-01 00:30:00", TabularExport.FormatCell(ts, TabularCellKind.Timestamp));
    }

    [Fact]
    public void ค่าแทนที่เป็นข้อความในช่องตัวเลข_ยังผ่านตัวกันสูตร()
    {
        var cols = new[] { new TabularColumn<object?>("ยอด", v => v, TabularCellKind.Money) };
        var lines = Csv(TabularExport.Sheet("x", cols, new object?[] { "-1+2", 5m })).Split("\r\n");
        Assert.Equal("'-1+2", lines[1]);
        Assert.Equal("5.00", lines[2]);
    }

    [Fact]
    public void แถวท้าย_ต่อท้ายข้อมูลทั้ง_CSV()
    {
        var s = TabularExport.Sheet("TB", Cols,
            new[] { new Line("1", "ก", 10m, 0m, new DateTime(2026, 1, 1)) },
            footer: new[] { new Line("", "รวม", 10m, 10m, new DateTime(2026, 1, 1)) },
            title: new[] { "งบทดลอง" });
        var lines = Csv(s).Split("\r\n");
        Assert.Equal("วันที่,รหัสบัญชี,ชื่อบัญชี,เดบิต,เครดิต", lines[0]);   // CSV ไม่มีหัวรายงาน — เริ่มที่หัวตารางเสมอ
        Assert.Equal("2026-01-01,,รวม,10.00,10.00", lines[2]);
    }

    [Fact]
    public void Excel_หัวรายงาน_หัวตาราง_ตัวเลขเป็นตัวเลข_ข้อความคงเดิม()
    {
        var s = TabularExport.Sheet("งบทดลอง", Cols,
            new[] { new Line("41000", "-ร้านลุงหมี-", 0m, -1500.555m, new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc)) },
            footer: new[] { new Line("", "รวม", 0m, -1500.56m, new DateTime(2026, 3, 2)) },
            title: new[] { "บริษัท ทดสอบ จำกัด", "งบทดลอง" });
        var bytes = TabularExport.ToXlsx(s);
        using var ms = new MemoryStream(bytes);
        Assert.Equal(new[] { "งบทดลอง" }, MiniExcel.GetSheetNames(ms).ToArray());
        ms.Position = 0;
        var rows = MiniExcel.Query(ms, useHeaderRow: false, sheetName: "งบทดลอง")
            .Cast<IDictionary<string, object>>().ToList();
        Assert.Equal("บริษัท ทดสอบ จำกัด", rows[0]["A"]);
        Assert.Equal("งบทดลอง", rows[1]["A"]);
        var h = rows.FindIndex(r => Equals(r["A"], "วันที่"));
        Assert.True(h >= 2, "หัวตารางต้องอยู่ใต้หัวรายงาน");
        Assert.Equal("เครดิต", rows[h]["E"]);
        Assert.Equal("-ร้านลุงหมี-", rows[h + 1]["C"]);                // ข้อความใน Excel ไม่ถูกเติม '
        Assert.IsNotType<string>(rows[h + 1]["E"]);                     // ช่องเงินเป็นตัวเลข
        Assert.Equal(-1500.56m, Convert.ToDecimal(rows[h + 1]["E"]));   // ปัด AwayFromZero
        Assert.IsNotType<string>(rows[h + 1]["A"]);                     // วันที่เป็นช่องวันที่/serial ไม่ใช่ข้อความ
        Assert.Equal("รวม", rows[h + 2]["C"]);
    }

    [Fact]
    public void CSV_หลายแผ่นงาน_ถูกปฏิเสธดัง_ส่วน_Excel_รับได้()
    {
        var a = TabularExport.Sheet("ก", Cols, Array.Empty<Line>());
        var b = TabularExport.Sheet("ก", Cols, Array.Empty<Line>());
        Assert.Throws<ArgumentException>(() => TabularExport.Render(TabularFormat.Csv, "x", null, null, a, b));
        var f = TabularExport.Render(TabularFormat.Xlsx, "x", null, null, a, b);
        using var ms = new MemoryStream(f.Content);
        Assert.Equal(2, MiniExcel.GetSheetNames(ms).Count);   // ชื่อซ้ำถูกเติมเลข ไม่ทับกัน
        Assert.Equal(TabularExport.XlsxContentType, f.ContentType);
    }

    [Fact]
    public void ชื่อไฟล์_มีช่วงวันที่_ตัดอักขระอันตราย_เก็บภาษาไทย()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("บัญชีแยกประเภท_20260101-20260131.csv", TabularExport.FileName("บัญชีแยกประเภท", from, to, TabularFormat.Csv));
        Assert.Equal("TB_x_20260131.xlsx", TabularExport.FileName("TB/x", null, to, TabularFormat.Xlsx));
        Assert.StartsWith("export_", TabularExport.FileName("../", null, null, TabularFormat.Csv));
    }

    [Theory]
    [InlineData("csv", true, TabularFormat.Csv)]
    [InlineData("XLSX", true, TabularFormat.Xlsx)]
    [InlineData("excel", true, TabularFormat.Xlsx)]
    [InlineData("pdf", false, TabularFormat.Csv)]
    [InlineData(null, false, TabularFormat.Csv)]
    public void อ่านพารามิเตอร์รูปแบบ_ค่าแปลกถูกปฏิเสธ(string? raw, bool ok, TabularFormat expected)
    {
        Assert.Equal(ok, TabularExport.TryParseFormat(raw, out var f));
        Assert.Equal(expected, f);
    }
}
