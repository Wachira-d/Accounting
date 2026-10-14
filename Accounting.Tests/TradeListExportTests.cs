using System.Text;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.DTOs.Document;
using Accounting.Models.Enums;
using MiniExcelLibs;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ไฟล์ส่งออกรายการเอกสาร/บรรทัด/ผู้ติดต่อ/สินค้า (ทีม X2 · EXPORT_AUDIT 2026-10-14) — <see cref="TradeListExport"/>
/// <para>ล็อกบั๊กจริงที่พบ: (1) ปุ่ม Export ได้แค่ 200 แถวเงียบ ๆ (2) ยอดเป็นข้อความคั่นหลักพัน (3) CSV ไม่มี BOM ⇒ ภาษาไทยเพี้ยนใน Excel
/// (4) ยอดติดลบถูกเติม ' (5) ตัวกันสูตรต้องยังทำงานกับข้อความ (6) เลขภาษีขึ้นต้น 0 ต้องคงเป็นข้อความใน Excel</para>
/// </summary>
public class TradeListExportTests
{
    private static readonly IReadOnlyList<TradeListExport.Column> Cols = new List<TradeListExport.Column>
    {
        new("ชื่อ"), new("เลขภาษี"), new("ยอด", TabularCellKind.Money), new("จำนวน", TabularCellKind.Number),
    };

    private static string CsvText(byte[] bytes)
    {
        var bom = Encoding.UTF8.GetPreamble();
        Assert.True(bytes.Length >= 3 && bytes[0] == bom[0] && bytes[1] == bom[1] && bytes[2] == bom[2], "CSV ต้องขึ้นต้นด้วย UTF-8 BOM (Excel ภาษาไทย)");
        return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
    }

    private static byte[] Bytes(TabularFormat f, IEnumerable<object?[]> rows, string name = "ทดสอบ")
        => TradeListExport.Write(f, Cols, rows, name).Content;

    [Fact]
    public void Csv_มีBOM_CRLF_เงินเป็นตัวเลขไม่มีตัวคั่น_ปัดAwayFromZero()
    {
        var csv = CsvText(Bytes(TabularFormat.Csv, new[]
        {
            new object?[] { "ร้านลุงหมี", "0105556000001", 1234.565m, 2.5m },
        }));
        var lines = csv.Split("\r\n");
        Assert.Equal("ชื่อ,เลขภาษี,ยอด,จำนวน", lines[0]);
        Assert.Equal("ร้านลุงหมี,0105556000001,1234.57,2.5", lines[1]);   // AwayFromZero · ไม่มี "1,234.57"
        Assert.Equal("", lines[2]);                                         // ปิดท้ายด้วย CRLF
    }

    [Fact]
    public void Csv_ยอดติดลบไม่ถูกเติมอัญประกาศ_แต่ข้อความที่เป็นสูตรถูกกัน()
    {
        var csv = CsvText(Bytes(TabularFormat.Csv, new[]
        {
            new object?[] { "=HYPERLINK(\"http://x\",\"คลิก\")", "-1", -1500m, -3m },
            new object?[] { "-ร้านลุงหมี-", "", 0m, null },
        }));
        var lines = csv.Split("\r\n");
        // ข้อความ: กันสูตรแล้วค่อยใส่อัญประกาศ (ตัวหนีเดียวของระบบ CsvFieldSafety)
        Assert.StartsWith("\"'=HYPERLINK(", lines[1]);
        // คอลัมน์ข้อความที่ขึ้นต้นด้วย "-" ก็ยังถูกกัน (ไม่ใช่คอลัมน์ตัวเลข)
        Assert.Contains(",'-1,", lines[1]);
        // คอลัมน์ตัวเลข: ติดลบเขียนตรง — นำเข้ากลับได้
        Assert.EndsWith(",-1500.00,-3", lines[1]);
        Assert.Equal("'-ร้านลุงหมี-,,0.00,", lines[2]);
    }

    [Fact]
    public void ค่าจริงเท็จ_เขียนเป็นภาษาไทย()
    {
        var csv = CsvText(Bytes(TabularFormat.Csv, new[] { new object?[] { true, false, 1m, 1m } }));
        Assert.StartsWith("ใช่,ไม่ใช่,", csv.Split("\r\n")[1]);
    }

    [Fact]
    public void Xlsx_เงินเป็นเซลล์ตัวเลข_เลขภาษีขึ้นต้นศูนย์คงเป็นข้อความ()
    {
        var bytes = Bytes(TabularFormat.Xlsx, new[]
        {
            new object?[] { "บริษัท ก", "0105556000001", 1234.565m, 1m },
        });
        using var ms = new MemoryStream(bytes);
        var rows = MiniExcel.Query(ms, useHeaderRow: true).Cast<IDictionary<string, object>>().ToList();
        Assert.Single(rows);
        Assert.Equal("0105556000001", Convert.ToString(rows[0]["เลขภาษี"]));
        Assert.True(rows[0]["ยอด"] is double or decimal, "ยอดเงินต้องเป็นเซลล์ตัวเลข (ไม่ใช่ข้อความ \"1,234.57\")");
        Assert.Equal(1234.57m, Convert.ToDecimal(rows[0]["ยอด"]));
    }

    [Fact]
    public void Xlsx_ไม่มีแถว_ยังมีหัวคอลัมน์()
    {
        var bytes = Bytes(TabularFormat.Xlsx, Array.Empty<object?[]>());
        using var ms = new MemoryStream(bytes);
        var rows = MiniExcel.Query(ms, useHeaderRow: false).Cast<IDictionary<string, object>>().ToList();
        Assert.Single(rows);
        Assert.Equal("ชื่อ", Convert.ToString(rows[0]["A"]));
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("xls")]
    public void รูปแบบไม่รองรับ_โยนข้อความไทย(string fmt)
        => Assert.Throws<BusinessRuleException>(() => TradeListExport.NormalizeFormat(fmt));

    [Fact]
    public void รูปแบบ_ว่างคือxlsx_csvคือcsv_ชื่อไฟล์ไม่มีตัวคั่นโฟลเดอร์()
    {
        Assert.Equal(TabularFormat.Xlsx, TradeListExport.NormalizeFormat(null));
        Assert.Equal(TabularFormat.Csv, TradeListExport.NormalizeFormat("CSV"));
        var f = TradeListExport.Write(TabularFormat.Csv, Cols, Array.Empty<object?[]>(), "ใบแจ้งหนี้/ใบกำกับภาษี");
        Assert.EndsWith(".csv", f.FileName);
        Assert.DoesNotContain("/", f.FileName);
        Assert.StartsWith("text/csv", f.ContentType);
    }

    // ═══════════ วนดึงทุกหน้า — บั๊ก "ได้แค่ 200 แถว" ═══════════

    private static Func<PagedRequest, Task<PagedResponse<int>>> FakeSource(int total) => req =>
    {
        var items = Enumerable.Range(1, total).Skip((req.Page - 1) * req.PageSize).Take(req.PageSize).ToList();
        return Task.FromResult(new PagedResponse<int>(items, total, req.Page, req.PageSize,
            (int)Math.Ceiling(total / (double)req.PageSize)));
    };

    [Fact]
    public async Task ดึงทุกหน้า_ได้ครบเกิน200แถว()
    {
        var all = await TradeListExport.CollectAllAsync(FakeSource(450),
            p => new PagedRequest(p, TradeListExport.FetchPageSize), 1000);
        Assert.Equal(450, all.Count);
        Assert.Equal(Enumerable.Range(1, 450), all);
    }

    [Fact]
    public async Task ทิศตรงข้าม_หน้าเดียวแบบเดิมได้แค่200_เพดานของPagedRequest()
    {
        // ปุ่มเดิมขอ pageSize=10000 — PagedRequest บีบเหลือ 200 ⇒ หน้าแรกหน้าเดียวขาด 250 แถว
        var single = await FakeSource(450)(new PagedRequest(1, 10000));
        Assert.Equal(200, single.Items.Count);
    }

    [Fact]
    public async Task เกินเพดาน_ปฏิเสธพร้อมบอกให้กรอง_ไม่ตัดเงียบ()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => TradeListExport.CollectAllAsync(FakeSource(450),
            p => new PagedRequest(p, TradeListExport.FetchPageSize), 300));
        Assert.Contains("เกินเพดาน", ex.Message);
        Assert.Equal("EXPORT-TOO-LARGE", ex.RuleCode);
    }

    // ═══════════ ฝั่งเอกสาร (ตรงกับ Layout.docSide) ═══════════

    [Fact]
    public void ชนิดตามฝั่ง_รวมชนิดสองฝั่งเสมอ()
    {
        var rev = TradeListExport.TypesForSide("revenue");
        var exp = TradeListExport.TypesForSide("expense");
        Assert.Contains(DocumentType.TaxInvoice, rev);
        Assert.Contains(DocumentType.CreditNote, rev);
        Assert.Contains(DocumentType.CreditNote, exp);
        Assert.Contains(DocumentType.GoodsReceiptNote, exp);
        Assert.DoesNotContain(DocumentType.PaymentVoucher, rev);
        Assert.DoesNotContain(DocumentType.TaxInvoice, exp);
    }

    [Fact]
    public void ใบลดหนี้_ตัดสินฝั่งจากธงก่อน_ไม่รู้ถือฝั่งซื้อ()
    {
        Assert.True(TradeListExport.IsOnSide(DocumentType.CreditNote, true, "expense"));
        Assert.False(TradeListExport.IsOnSide(DocumentType.CreditNote, true, "revenue"));
        Assert.True(TradeListExport.IsOnSide(DocumentType.CreditNote, false, "revenue"));
        Assert.True(TradeListExport.IsOnSide(DocumentType.CreditNote, null, "expense"));
        // ชนิดไม่กำกวม ธงไม่มีผล
        Assert.True(TradeListExport.IsOnSide(DocumentType.TaxInvoice, true, "revenue"));
    }

    // ═══════════ แถวเอกสาร / บรรทัด ═══════════

    private static DocumentResponse Doc(decimal total = 1070m, decimal deposit = 0m, bool? balanceApplies = true,
        bool redacted = false, List<DocumentLineResponse>? lines = null) => new(
        Guid.NewGuid(), "INV-001", DocumentType.TaxInvoice, DocumentStatus.Approved,
        new DateTime(2026, 9, 30, 17, 0, 0, DateTimeKind.Utc), null,
        new ContactBrief(Guid.NewGuid(), "ลูกค้า ก", "0105556000001", "00000"),
        1000m, 0m, 0m, 0m, 70m, 30m, total, 0m, total, "PO-9", null,
        lines ?? new List<DocumentLineResponse>(), new DateTime(2026, 9, 30, 3, 0, 0, DateTimeKind.Utc),
        IsRedacted: redacted, RedactedReason: redacted ? "ซ่อน: เอกสารเงินเดือน" : null)
        with { DepositAppliedAmount = deposit, BalanceDueApplies = balanceApplies, Currency = "USD", ExchangeRate = 35.5m };

    private static object? Cell(object?[] row, string header)
    {
        var i = TradeListExport.DocumentColumns.Select(c => c.Header).ToList().IndexOf(header);
        Assert.True(i >= 0, header);
        return row[i];
    }

    [Fact]
    public void แถวเอกสาร_จำนวนช่องตรงหัว_ค่าจากเซิร์ฟเวอร์()
    {
        var row = TradeListExport.DocumentRow(Doc(deposit: 500m), new TradeListExport.DocumentExtras("สมชาย", "QT-001"));
        Assert.Equal(TradeListExport.DocumentColumns.Count, row.Length);
        Assert.Equal("0105556000001", Cell(row, "เลขประจำตัวผู้เสียภาษีผู้ติดต่อ"));
        Assert.Equal(30m, Cell(row, "หัก ณ ที่จ่าย"));
        Assert.Equal(570m, Cell(row, "รับ/จ่ายสุทธิ"));
        Assert.Equal("USD", Cell(row, "สกุลเงิน"));
        Assert.Equal(35.5m, Cell(row, "อัตราแลกเปลี่ยน"));
        Assert.Equal("QT-001", Cell(row, "เอกสารต้นทาง"));
        Assert.Equal("สมชาย", Cell(row, "ผู้สร้าง"));
        // วันที่/เวลาส่งเป็นค่าดิบ — ตัวเขียนกลางจัดรูป: วันที่ตามปฏิทินไทย (UTC 17:00 = 1 ต.ค. ที่ไทย) · เวลาสร้างเป็นเวลาไทย
        var line = CsvText(TradeListExport.Write(TabularFormat.Csv, TradeListExport.DocumentColumns, new[] { row }, "x").Content).Split("\r\n")[1];
        Assert.Contains(",2026-10-01,", line);
        Assert.Contains(",2026-09-30 10:00:00,", line);
    }

    [Fact]
    public void ยอดค้าง_ชนิดที่ไม่ใช่หนี้เป็นช่องว่าง_เหมือนหน้าจอแสดงขีด()
    {
        Assert.Null(Cell(TradeListExport.DocumentRow(Doc(balanceApplies: false)), "ยอดค้าง"));
        Assert.Equal(1070m, Cell(TradeListExport.DocumentRow(Doc(balanceApplies: true)), "ยอดค้าง"));
    }

    [Fact]
    public void บรรทัด_หนึ่งแถวต่อบรรทัด_เติมรหัสบัญชีจากตารางที่โหลดมา()
    {
        var acc = Guid.NewGuid();
        var d = Doc(lines: new List<DocumentLineResponse>
        {
            new(Guid.NewGuid(), 2, "ค่าบริการ", 1m, "งาน", 500m, 0m, 0m, 500m, 7m, 35m, 3m, 15m, AccountId: acc),
            new(Guid.NewGuid(), 1, "สินค้า A", 2m, "ชิ้น", 250m, 10m, 50m, 450m, 7m, 31.5m, 0m, 0m, ProductCode: "A1",
                AccountCode: "41100", AccountName: "รายได้ขาย"),
        });
        var rows = TradeListExport.DocumentLineRows(d, new Dictionary<Guid, (string Code, string Name)> { [acc] = ("41200", "รายได้บริการ") }).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(TradeListExport.DocumentLineColumns.Count, r.Length));
        var headers = TradeListExport.DocumentLineColumns.Select(c => c.Header).ToList();
        Assert.Equal("สินค้า A", rows[0][headers.IndexOf("รายการ")]);          // เรียงตามลำดับบรรทัด
        Assert.Equal("41100", rows[0][headers.IndexOf("รหัสบัญชี")]);
        Assert.Equal("41200", rows[1][headers.IndexOf("รหัสบัญชี")]);          // จาก lookup (list ไม่ include บัญชี)
        Assert.Equal(15m, rows[1][headers.IndexOf("หัก ณ ที่จ่าย")]);
    }

    [Fact]
    public void บรรทัด_ใบที่ถูกซ่อนได้หนึ่งแถวบอกเหตุผล_ไม่หายจากไฟล์()
    {
        var rows = TradeListExport.DocumentLineRows(Doc(redacted: true)).ToList();
        Assert.Single(rows);
        Assert.Equal(TradeListExport.DocumentLineColumns.Count, rows[0].Length);
        var headers = TradeListExport.DocumentLineColumns.Select(c => c.Header).ToList();
        Assert.Equal("ซ่อน: เอกสารเงินเดือน", rows[0][headers.IndexOf("หมายเหตุการซ่อน")]);
    }

    // ═══════════ ตารางสตริงของศูนย์นำเข้า/ส่งออก ═══════════

    [Fact]
    public void ตารางสตริง_คอลัมน์ทศนิยมเป็นตัวเลข_เลขภาษีโทรศัพท์คงเป็นข้อความ()
    {
        var (cols, rows) = TradeListExport.FromStringTable(new List<Dictionary<string, string>>
        {
            new() { ["TaxId"] = "0105556000001", ["Phone"] = "0812345678", ["Amount"] = "-1500.00", ["Level"] = "4" },
            new() { ["TaxId"] = "", ["Phone"] = "", ["Amount"] = "20.50", ["Level"] = "3" },
        });
        Assert.Equal(TabularCellKind.Text, cols[0].Kind);
        Assert.Equal(TabularCellKind.Text, cols[1].Kind);
        Assert.Equal(TabularCellKind.Number, cols[2].Kind);
        Assert.Equal(TabularCellKind.Text, cols[3].Kind);   // ไม่มีจุด = ไม่เดา
        Assert.Equal(-1500.00m, rows[0][2]);
        var csv = CsvText(TradeListExport.Write(TabularFormat.Csv, cols, rows, "x").Content);
        Assert.Contains("0105556000001,0812345678,-1500,4", csv);          // ติดลบไม่มี ' · ตัวเลขเขียนตรง
    }

    [Theory]
    [InlineData("1500.00", true)]
    [InlineData("-0.5000", true)]
    [InlineData("1500", false)]
    [InlineData("1,500.00", false)]
    [InlineData("0105556000001", false)]
    [InlineData("-", false)]
    [InlineData("1.", false)]
    public void เลขทศนิยมแบบinvariant(string v, bool expected)
        => Assert.Equal(expected, TradeListExport.IsDecimalLiteral(v));
}
