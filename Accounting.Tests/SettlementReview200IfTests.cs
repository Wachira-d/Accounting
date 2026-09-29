using System.Text;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Settlement;
using Accounting.Services.Settlement.Adapters;
using MiniExcelLibs;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม IF — แก้ผลฝ่ายค้านทีม I (review200-I.md I-1 … I-11 · DECISIONS ข้อ 39)
///
/// ทุกกลุ่มมีสองครึ่ง: ของที่ฝ่ายค้านพิสูจน์ว่าพัง (คีย์ drift · ข้อความสัญญาเกินจริง · แถวจริงหาย · ยอดแถวสรุปผิดเครื่องหมาย …) ต้องกลับมาถูก
/// และของที่ถูกอยู่แล้วต้องไม่ถูกแตะ · ข้อมูลสังเคราะห์รูปแบบรายงานรอบโอน — ไม่มีไฟล์ของเจ้าใดเจ้าหนึ่ง (DECISIONS ข้อ 12)
/// </summary>
public class SettlementReview200IfTests
{
    private static readonly DateTime Sep30 = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Oct1 = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static SettlementChannel Channel() => new() { CompanyId = Guid.NewGuid(), DisplayName = "ร้านทดสอบ", Kind = SettlementChannelKind.Marketplace };

    private static SettlementFileInput Csv(string text) => new("report.csv", Encoding.UTF8.GetBytes(text));

    private static SettlementFileInput Xlsx(List<Dictionary<string, object>> rows)
    {
        using var ms = new MemoryStream();
        MiniExcel.SaveAs(ms, rows, excelType: ExcelType.XLSX);
        return new SettlementFileInput("report.xlsx", ms.ToArray());
    }

    private static string LongMap(string date = "Date", string? txn = "Txn ID", SettlementDateOrder order = SettlementDateOrder.Auto,
        SettlementFileTimeZone zone = SettlementFileTimeZone.Auto)
        => new SettlementColumnMap
        {
            Layout = SettlementFileLayout.Long, TxnId = txn, Date = date, Type = "Type", Amount = "Amount", DateOrder = order, TimeZone = zone,
        }.ToJson();

    private static SettlementColumnMap WideMapObj(string? date = "Date") => new()
    {
        Layout = SettlementFileLayout.Wide,
        OrderId = "Order ID",
        Date = date,
        AmountColumns =
        {
            new SettlementAmountColumn { Header = "Merchandise Subtotal" },
            new SettlementAmountColumn { Header = "Commission Fee" },
            new SettlementAmountColumn { Header = "Transaction Fee", Negate = true },
        },
    };

    private static List<SettlementTxnKeyInput> Inputs(IEnumerable<SettlementParsedRow> rows, Func<SettlementParsedRow, DateTime?>? date = null)
        => rows.Select(x => new SettlementTxnKeyInput(x.RawTxnId, x.RawTypeLabel, x.ExternalOrderId, x.Amount, date?.Invoke(x) ?? x.TxnDate, x.PayoutRef))
            .ToList();

    // ═════════════════ I-1 · คีย์กันซ้ำไม่ drift เมื่อการอ่านเขตเวลาเปลี่ยน ═════════════════

    [Theory]
    [InlineData("2026-09-30 20:00:00", SettlementFileTimeZone.Utc)]              // หัวคอลัมน์ "(UTC)" — ตัวอ่านเดิมทิ้งเวลา
    [InlineData("2026-09-30 20:00:00 UTC", SettlementFileTimeZone.Bangkok)]      // offset แบบข้อความ — ตัวอ่านเดิมทิ้งส่วนท้าย
    [InlineData("30/09/2026 20:00 GMT", SettlementFileTimeZone.Bangkok)]
    public void I1_วันที่ตามตัวอักษร_คือวันที่ตัวอ่านก่อนรอบ200อ่าน_ต่างจากวันที่ที่แปลงเขตเวลาแล้ว(string raw, SettlementFileTimeZone zone)
    {
        Assert.True(SettlementValueParser.TryParseDate(raw, SettlementDateOrder.DayMonthYear, zone, out var now));
        Assert.Equal(Oct1, now);
        Assert.True(SettlementValueParser.TryParseLiteralDate(raw, SettlementDateOrder.DayMonthYear, out var literal));
        Assert.Equal(Sep30, literal);
    }

    [Theory]
    [InlineData("2026-09-30T20:00:00Z")]      // ISO มี offset — ตัวอ่านเดิมแปลงเป็นวันไทยอยู่แล้ว ⇒ วันที่ตามตัวอักษร = วันที่วันนี้
    [InlineData("30/09/2026")]
    [InlineData("2026-09-30 10:00:00")]
    public void I1_ทิศตรงข้าม_ค่าที่ตัวอ่านเดิมอ่านเหมือนวันนี้_วันที่ตามตัวอักษรไม่ต่าง(string raw)
    {
        SettlementValueParser.TryParseDate(raw, SettlementDateOrder.DayMonthYear, SettlementFileTimeZone.Utc, out var now);
        Assert.True(SettlementValueParser.TryParseLiteralDate(raw, SettlementDateOrder.DayMonthYear, out var literal));
        Assert.Equal(now, literal);
    }

    [Fact]
    public void I1_ไฟล์หัวUTCที่นำเข้าก่อนdeploy_คีย์เดิมอยู่ในคีย์รุ่นก่อนของแถวเดียวกันหลังdeploy_ทั้งแถวมีidและไม่มีid()
    {
        const string csv = "Created (UTC),Txn ID,Type,Amount\n" +
                           "2026-09-30 20:00:00,T1,Sale,100\n" +
                           "2026-09-30 21:00:00,,WithdrawalFee,-10\n" +
                           "2026-09-30 09:00:00,T2,PaymentFee,-5\n";
        var parsed = new GenericColumnMapAdapter().Parse(Csv(csv), Channel(), LongMap("Created (UTC)"));
        Assert.Equal(new DateTime?[] { Oct1, Oct1, Sep30 }, parsed.Rows.Select(r => r.TxnDate));
        Assert.All(parsed.Rows, r => Assert.Equal(Sep30, r.LiteralDates![0]));

        // คีย์ที่ตัวอ่านก่อน deploy เก็บไว้ = กติกาปัจจุบันกับวันที่ตามตัวอักษร (ไฟล์ไม่มีคอลัมน์รอบโอน ⇒ แถวไม่มี id ใช้ลายนิ้วมือเนื้อหาไฟล์)
        var stored = SettlementTxnKey.Assign(Inputs(parsed.Rows, r => r.LiteralDates![0]));
        var now = Inputs(parsed.Rows);
        var current = SettlementTxnKey.Assign(now);
        Assert.NotEqual(stored[0], current[0]);                                        // บั๊ก: คีย์ drift จริง
        Assert.NotEqual(stored[1], current[1]);
        var sets = new List<IReadOnlyList<DateTime?>> { parsed.Rows.Select(r => r.LiteralDates![0]).ToList() };
        var legacy = SettlementTxnKey.LegacyKeys(now, "PO-1", sets);
        for (var i = 0; i < parsed.Rows.Count; i++)
            Assert.True(stored[i] == current[i] || legacy[i].Contains(stored[i]), $"แถว {i}: คีย์ก่อน deploy ต้องถูกจับว่าซ้ำ");

        // ทิศตรงข้าม: ไม่ส่งชุดวันที่ตามตัวอักษร ⇒ ผลเท่ากับก่อนแก้ (ไม่มีคีย์เพิ่ม) · ชุดที่เท่ากับวันที่จริงไม่เพิ่มอะไร
        Assert.Equal(SettlementTxnKey.LegacyKeys(now, "PO-1"), SettlementTxnKey.LegacyKeys(now, "PO-1",
            new List<IReadOnlyList<DateTime?>> { now.Select(r => r.Date).ToList() }));
    }

    [Fact]
    public void I1_ทิศตรงข้าม_รายการจริงคนละวัน_ไม่ชนคีย์รุ่นก่อน()
    {
        var sept = new[] { new SettlementTxnKeyInput("T1", "Sale", "O-1", 100m, Sep30) };
        var oct = new[] { new SettlementTxnKeyInput("T1", "Sale", "O-1", 100m, Oct1) };
        var storedSept = SettlementTxnKey.Assign(sept)[0];
        // แถว 1 ต.ค. ที่วันที่ตามตัวอักษรก็ 1 ต.ค. (ไม่ใช่เวลา UTC) ⇒ ไม่มีคีย์ใดชนรายการ 30 ก.ย.
        var legacy = SettlementTxnKey.LegacyKeys(oct, null, new List<IReadOnlyList<DateTime?>> { new DateTime?[] { Oct1 } });
        Assert.DoesNotContain(storedSept, legacy[0]);
        Assert.NotEqual(storedSept, SettlementTxnKey.Assign(oct)[0]);
    }

    [Fact]
    public void I1_ไฟล์ไม่มีหลักฐานลำดับวันเดือน_ตัวอ่านเดิมเดาวันเดือน_คีย์รุ่นก่อนครอบทั้งสองลำดับ()
    {
        var dates = new List<(int, string?)> { (2, "01/09/2026"), (3, "05/09/2026") };
        Assert.Equal(new[] { SettlementDateOrder.MonthDayYear, SettlementDateOrder.DayMonthYear },
            SettlementFileDecisions.LegacyReadOrders(SettlementDateOrder.MonthDayYear, dates));
        Assert.Equal(new[] { SettlementDateOrder.DayMonthYear }, SettlementFileDecisions.LegacyReadOrders(SettlementDateOrder.DayMonthYear, dates));
        // ทิศตรงข้าม: ไฟล์มีหลักฐาน (13/09) ⇒ ตัวอ่านเดิมก็ใช้ลำดับเดียวกับวันนี้ ⇒ ชุดเดียว
        Assert.Single(SettlementFileDecisions.LegacyReadOrders(SettlementDateOrder.DayMonthYear,
            new List<(int, string?)> { (2, "13/09/2026") }));
    }

    [Fact]
    public void I1_เลขรายการเดียวกับบรรทัดเดิมแต่เนื้อหาต่าง_ถูกชี้เหตุ_เลขใหม่ไม่ถูกชี้()
    {
        var storedKeys = new[]
        {
            SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput("T1", "Sale", null, 100m, Sep30) })[0],
            "T9|sale",                                                                                      // v1
            SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput(null, "Withdrawal fee", null, -10m, Sep30) })[0],   // แถวไม่มี id
            SettlementTxnKey.ForPaymentIntent(Guid.NewGuid(), "sale"),
        };
        var hit = SettlementTxnKey.SharesRawIdWith(new string?[] { "T1", "T2", " T9 ", null, "v2" }, storedKeys);
        Assert.Equal(new[] { 0, 2 }, hit.OrderBy(i => i));

        var posted = SettlementImportService.PostedBatchNewRowsMessage("PO-1", new[] { 2, 3 }, new[] { 2 });
        Assert.Contains("เขตเวลาในไฟล์", posted);
        Assert.Contains("แถวที่ 2", posted);
        var fresh = SettlementImportService.PostedBatchNewRowsMessage("PO-1", new[] { 5 }, Array.Empty<int>());
        Assert.DoesNotContain("เขตเวลาในไฟล์", fresh);                 // เลขใหม่จริง ⇒ ไม่โทษการอ่านวันที่
        Assert.Contains("ไม่เคยอยู่ในรอบนี้", fresh);
    }

    // ═════════════════ I-2 · ถามครั้งเดียว + บอกตามจริงว่าจำได้ไหม ═════════════════

    [Fact]
    public void I2_ข้อความจำ_ตามสิทธิ์จริง()
    {
        var yes = SettlementFileDecisions.MemoryClause(new SettlementParseContext(null, null, true));
        Assert.Contains("ระบบจะจำ", yes);
        var noPerm = SettlementFileDecisions.MemoryClause(new SettlementParseContext(null, null, false,
            SettlementPermissionScope.ColumnMapMemoryBlocker(false, false)));
        Assert.Contains("ไม่จำ", noPerm);
        Assert.Contains("ไม่มีสิทธิ์", noPerm);
        Assert.DoesNotContain("ระบบจะจำค่า", noPerm);
        var unticked = SettlementFileDecisions.MemoryClause(new SettlementParseContext(null, null, false));
        Assert.Contains("ไม่ได้ติ๊ก", unticked);

        Assert.Null(SettlementPermissionScope.ColumnMapMemoryBlocker(false, true));
        Assert.Contains("คีย์ API", SettlementPermissionScope.ColumnMapMemoryBlocker(true, true));
        // ตัวตัดสินเดียว: ColumnMapMemory ต้องให้ผลสอดคล้อง (ติ๊กจำ + จำได้ ⇔ ไม่มีเหตุขวาง)
        foreach (var api in new[] { false, true })
            foreach (var perm in new[] { false, true })
                Assert.Equal(SettlementPermissionScope.ColumnMapMemoryBlocker(api, perm) == null,
                    SettlementPermissionScope.ColumnMapMemory(true, api, perm).Remember);
    }

    [Fact]
    public void I2_ไฟล์กำกวมทั้งลำดับวันเดือนและเขตเวลา_ถามพร้อมกันครั้งเดียว_ผู้ไม่มีสิทธิ์รู้ว่าไม่จำ()
    {
        const string csv = "Date,Txn ID,Type,Amount\n01/09/2026 18:30,T1,Sale,100\n05/09/2026 09:00,T2,PaymentFee,-5\n";
        var ctx = new SettlementParseContext(null, null, false, SettlementPermissionScope.ColumnMapMemoryBlocker(false, false));
        var ex = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter().Parse(Csv(csv), Channel(), LongMap(), ctx));
        Assert.Equal("date-order-timezone-ambiguous", ex.Code);
        Assert.Contains("รูปแบบวันที่", ex.Message);
        Assert.Contains("เขตเวลาในไฟล์", ex.Message);
        Assert.Contains("ไม่จำ", ex.Message);
        Assert.DoesNotContain("จำไว้ให้ช่องทางนี้)", ex.Message);

        // ตอบทั้งสองในรอบเดียวแล้วอ่านได้ (ทางไปต่อ 1 ครั้ง ไม่ใช่ 3 ครั้ง)
        var ok = new GenericColumnMapAdapter().Parse(Csv(csv), Channel(),
            LongMap(order: SettlementDateOrder.DayMonthYear, zone: SettlementFileTimeZone.Bangkok), ctx);
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), ok.Rows[0].TxnDate);

        // ทิศตรงข้าม: กำกวมเรื่องเดียว ⇒ รหัสเดิม (หน้าจอ/ผู้เรียก API ที่อิงรหัสยังใช้ได้)
        var onlyOrder = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter()
            .Parse(Csv("Date,Txn ID,Type,Amount\n01/09/2026,T1,Sale,100\n05/09/2026,T2,PaymentFee,-5\n"), Channel(), LongMap(),
                new SettlementParseContext(null, null, true)));
        Assert.Equal("date-order-ambiguous", onlyOrder.Code);
        Assert.Contains("ระบบจะจำ", onlyOrder.Message);
    }

    // ═════════════════ I-4 · ยอดของแถวสรุปคิดแบบเดียวกับแถวจริง ═════════════════

    [Fact]
    public void I4_แถวสรุปแบบยาวยอดเข้าออก_ยอดสุทธิหักยอดออก_ไม่รวมค่าดิบ()
    {
        var map = new SettlementColumnMap
        {
            Layout = SettlementFileLayout.Long, TxnId = "Txn ID", Date = "Date", Type = "Type", AmountIn = "In", AmountOut = "Out",
        }.ToJson();
        const string csv = "Date,Txn ID,Type,In,Out\n13/09/2026,T1,Sale,1000,\n13/09/2026,T2,Fee,,50\nรวม,,,1000,50\n";
        var r = new GenericColumnMapAdapter().Parse(Csv(csv), Channel(), map);
        Assert.Equal(new[] { 1000m, -50m }, r.Rows.Select(x => x.Amount));
        var notice = Assert.Single(r.Warnings!);
        Assert.Contains("950.00", notice);                               // เดิมแสดงเฉพาะคอลัมน์ยอดเข้า = 1,000.00
        Assert.DoesNotContain("1,000.00", notice);
    }

    // ═════════════════ I-5 · CSV ; ยอดเป็นบาทเต็ม ═════════════════

    [Fact]
    public void I5_CSVอัฒภาคยอดบาทเต็ม_ยืนยันจุลภาคคั่นหลักพันแล้วอ่านได้_ไม่ยืนยันยังล้มดังพร้อมทางไปต่อที่ทำได้()
    {
        var semi = Csv("Date;Txn ID;Type;Amount\n13/09/2026;T1;Sale;1,500\n13/09/2026;T2;PaymentFee;-30\n");
        var ex = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter().Parse(semi, Channel(), LongMap()));
        Assert.Equal("decimal-comma", ex.Code);
        Assert.Contains(".xlsx", ex.Message);
        Assert.Contains("คั่นหลักพัน", ex.Message);
        Assert.DoesNotContain("ส่งออกรายงานใหม่ให้ใช้จุด", ex.Message);   // ทางที่ทำไม่ได้กับไฟล์ที่ไม่มีทศนิยม

        var confirmed = SettlementColumnMap.Parse(LongMap())!;
        confirmed.CommaIsThousands = true;
        var r = new GenericColumnMapAdapter().Parse(semi, Channel(), confirmed.ToJson());
        Assert.Equal(new[] { 1500m, -30m }, r.Rows.Select(x => x.Amount));
        Assert.True(SettlementColumnMap.Parse(confirmed.ToJson())!.CommaIsThousands);           // round-trip ผ่าน JSON ที่จำไว้
        Assert.False(SettlementColumnMap.Parse("{\"version\":1,\"layout\":\"Long\"}")!.CommaIsThousands);   // การจับคู่เดิม = ไม่ยืนยัน
    }

    // ═════════════════ I-6 · คอลัมน์เลขออเดอร์ว่างทั้งไฟล์ ═════════════════

    [Fact]
    public void I6_ไฟล์แบบกว้างคอลัมน์เลขออเดอร์ว่างทุกแถว_ล้มดังชี้คอลัมน์เลขออเดอร์_มีเลขบางแถวไม่ล้ม()
    {
        Dictionary<string, object> Row(string order, string date, decimal sale) => new()
        {
            ["Order ID"] = order, ["Date"] = date, ["Merchandise Subtotal"] = sale, ["Commission Fee"] = 0m, ["Transaction Fee"] = 0m,
        };
        var blank = Xlsx(new List<Dictionary<string, object>> { Row("", "13/09/2026", 100m), Row("", "14/09/2026", 200m) });
        var ex = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter().Parse(blank, Channel(), WideMapObj().ToJson()));
        Assert.Equal("id-column-empty", ex.Code);
        Assert.Contains("Order ID", ex.Message);
        Assert.DoesNotContain("คอลัมน์ยอดเงินถูกต้อง", ex.Message);

        var some = Xlsx(new List<Dictionary<string, object>> { Row("SO-1", "13/09/2026", 100m), Row("", "14/09/2026", 200m) });
        var r = new GenericColumnMapAdapter().Parse(some, Channel(), WideMapObj().ToJson());
        Assert.Equal(2, r.Rows.Count);
        Assert.Null(SettlementFileDecisions.WideIdColumnsEmpty(SettlementFileLayout.Long, null, "Txn", new List<(string?, string?)> { (null, null) }));
    }

    // ═════════════════ I-7 → DECISIONS ข้อ 39 · ข้ามเฉพาะแถวสรุปที่พิสูจน์ได้ ═════════════════

    [Fact]
    public void D39_แถวไม่มีเลขที่มีวันที่และยอดไม่ใช่ผลรวม_นำเข้าพร้อมคำเตือน_แถวรวมที่พิสูจน์ได้ข้ามพร้อมยอด()
    {
        Dictionary<string, object> Row(string order, string date, decimal sale, decimal comm, decimal fee) => new()
        {
            ["Order ID"] = order, ["Date"] = date, ["Merchandise Subtotal"] = sale, ["Commission Fee"] = comm, ["Transaction Fee"] = fee,
        };
        var rows = new List<Dictionary<string, object>>
        {
            Row("SO-1", "13/09/2026", 1070m, -53.5m, 20m),
            Row("", "13/09/2026", 300m, -15m, 5m),               // แถวสินค้าที่สองของ SO-1 (xlsx รวมเซลล์เลขออเดอร์) — รายการจริง
            Row("SO-2", "14/09/2026", 535m, 0m, 10m),
            Row("", "14/09/2026", 1905m, -68.5m, 35m),           // แถวรวม = ผลรวมของทุกแถวที่มีเลข? ไม่ (1070+535=1605) ⇒ ต้องไม่ถูกกลืนเงียบ
            Row("", "", 1605m, -53.5m, 30m),                     // ไม่มีวันที่ ⇒ แถวสรุป
        };
        var r = new GenericColumnMapAdapter().Parse(Xlsx(rows), Channel(), WideMapObj().ToJson());
        // แถวที่ 3 (ไม่มีเลข · 300/−15/5) และแถวที่ 5 (1905 — ไม่ใช่ผลรวมของแถวที่มีเลข) นำเข้าเป็นรายการ
        Assert.Equal(new[] { 2, 3, 4, 5 }, r.Rows.Select(x => x.SourceRow).Distinct().OrderBy(x => x));
        Assert.Contains(r.Warnings!, w => w.Contains("แถวที่ 3, 5") && w.Contains("ไม่มีเลขออเดอร์"));
        Assert.Contains(r.Warnings!, w => w.Contains("แถว 6") && w.Contains("แถวสรุปยอด") && w.Contains("1,521.50"));
        Assert.DoesNotContain(r.SkippedRows, s => s.Contains("แถวสรุป"));    // ยอดที่ข้ามไม่ใช่บรรทัดเทา
    }

    [Fact]
    public void D39_แถวรวมที่ยอดเท่าผลรวมของแถวที่มีเลข_ข้ามแม้มีวันที่_ทิศตรงข้ามแถวเดียวที่เท่ากันไม่ใช่หลักฐาน()
    {
        var ids = new List<IReadOnlyList<decimal?>> { new decimal?[] { 1070m, -53.5m, 20m }, new decimal?[] { 535m, null, 10m } };
        Assert.True(SettlementFileDecisions.TotalsIdRows(new decimal?[] { 1605m, -53.5m, 30m }, ids));
        Assert.False(SettlementFileDecisions.TotalsIdRows(new decimal?[] { 1605m, -53.5m, 31m }, ids));
        Assert.False(SettlementFileDecisions.TotalsIdRows(new decimal?[] { 0m, null, 0m }, ids));
        // มีแถวที่มีเลขแถวเดียว: แถวสินค้าแถวที่สองที่ยอดเท่ากันก็ "เท่าผลรวม" — ห้ามถือเป็นหลักฐาน
        Assert.False(SettlementFileDecisions.TotalsIdRows(new decimal?[] { 100m }, new List<IReadOnlyList<decimal?>> { new decimal?[] { 100m } }));

        Dictionary<string, object> Row(string order, string date, decimal sale) => new()
        {
            ["Order ID"] = order, ["Date"] = date, ["Merchandise Subtotal"] = sale, ["Commission Fee"] = 0m, ["Transaction Fee"] = 0m,
        };
        var r = new GenericColumnMapAdapter().Parse(Xlsx(new List<Dictionary<string, object>>
        {
            Row("SO-1", "13/09/2026", 100m), Row("SO-2", "13/09/2026", 200m), Row("", "13/09/2026", 300m),
        }), Channel(), WideMapObj().ToJson());
        Assert.Equal(new[] { 100m, 200m }, r.Rows.Select(x => x.Amount));
        Assert.Contains(r.Warnings!, w => w.Contains("ผลรวม") && w.Contains("300.00"));
    }

    // ═════════════════ I-8 · ไฟล์รุ่นก่อน: หักจำนวน + สืบลายนิ้วมือ ═════════════════

    private static string C(string label, decimal amount) => SettlementTxnKey.ContentKey(null, label, amount, Sep30);

    [Fact]
    public void I8_บรรทัดที่เติมจากไฟล์ฉบับแก้สืบลายนิ้วมือไฟล์รุ่นก่อน_ไฟล์ส่วนที่เหลือของรอบไม่ถูกกลืน()
    {
        var w = C("Withdrawal fee", -10m);
        var x = C("Adjustment", 5m);
        var y = C("Ads fee", -20m);
        var file1 = new List<SettlementStoredContent> { new(w, "S1"), new(x, "S1") };

        // นำเข้าไฟล์ 1 ฉบับแก้ (w · x · ถอนเงิน −10 เพิ่ม) ⇒ เป็นฉบับแก้ของ S1 ⇒ บรรทัดที่เพิ่มต้องเก็บ scope S1
        var revised = new string?[] { w, x, w };
        var pool = SettlementTxnKey.SplitRevisedFilePool(revised, file1);
        Assert.Equal("S1", pool.RevisedScope);
        Assert.Equal(new[] { 0, 1 }, SettlementTxnKey.MatchByContent(revised, pool.SameFile).OrderBy(i => i));

        // หลังเติม: กลุ่ม S1 = {w, x, w} · ไฟล์ 2 (ส่วนที่เหลือของรอบ) มี "ถอนเงิน −10" จริงอีกรายการ ⇒ ไม่ครอบ S1 ⇒ ไม่กลืน
        var afterRevise = file1.Append(new SettlementStoredContent(w, pool.RevisedScope)).ToList();
        var file2 = new string?[] { w, y };
        var pool2 = SettlementTxnKey.SplitRevisedFilePool(file2, afterRevise);
        Assert.Empty(pool2.SameFile);
        Assert.Null(pool2.RevisedScope);
        Assert.Empty(SettlementTxnKey.MatchByContent(file2, pool2.SameFile));

        // ทิศตรงข้าม (บั๊กเดิม): ถ้าบรรทัดที่เติมเก็บ scope ของไฟล์ฉบับแก้เอง (กลุ่มเล็ก {w}) ⇒ ไฟล์ 2 ครอบได้ ⇒ กลืน — เหตุที่ต้องสืบ scope
        var buggy = file1.Append(new SettlementStoredContent(w, "S1-revised")).ToList();
        Assert.Single(SettlementTxnKey.MatchByContent(file2, SettlementTxnKey.SplitRevisedFilePool(file2, buggy).SameFile));
    }

    [Fact]
    public void I8_หักจำนวนระหว่างกลุ่ม_แถวเดียวของไฟล์ใหม่ครอบได้กลุ่มเดียว_ผลไม่ขึ้นกับลำดับ()
    {
        var a = C("Withdrawal fee", -10m);
        var stored = new List<SettlementStoredContent> { new(a, "S2"), new(a, "S1") };
        var pool = SettlementTxnKey.SplitRevisedFilePool(new string?[] { a }, stored);
        Assert.Single(pool.SameFile);
        Assert.Single(pool.OtherFiles);
        Assert.Equal("S1", pool.RevisedScope);                                 // เท่ากัน ⇒ เรียงตามลายนิ้วมือ (deterministic)
        var reversed = SettlementTxnKey.SplitRevisedFilePool(new string?[] { a }, stored.AsEnumerable().Reverse().ToList());
        Assert.Equal(pool.RevisedScope, reversed.RevisedScope);
        // ทิศตรงข้าม: ไฟล์ใหม่มี a สองแถว ⇒ ครอบได้ทั้งสองกลุ่ม · บรรทัดก่อนรอบ 200 (scope null) = พฤติกรรมเดิม
        Assert.Equal(2, SettlementTxnKey.SplitRevisedFilePool(new string?[] { a, a }, stored).SameFile.Count);
        var legacy = SettlementTxnKey.SplitRevisedFilePool(new string?[] { C("อื่น", 1m) }, new[] { new SettlementStoredContent(a, null) });
        Assert.Single(legacy.SameFile);
        Assert.Null(legacy.RevisedScope);
    }

    // ═════════════════ I-9 · ส่วนท้ายเวลา ═════════════════

    [Theory]
    [InlineData("30/09/2026 18:30 ICT", SettlementFileTimeZone.Utc, 9, 30)]              // ICT = +7 ชนะค่าตั้ง
    [InlineData("2026-09-30 18:00:00 (GMT+07:00)", SettlementFileTimeZone.Utc, 9, 30)]
    [InlineData("2026-09-30 18:00:00 (UTC)", SettlementFileTimeZone.Bangkok, 10, 1)]
    [InlineData("30/09/2569 18:30 น", SettlementFileTimeZone.Bangkok, 9, 30)]
    [InlineData("30/09/2026 13:05 PM", SettlementFileTimeZone.Utc, 9, 30)]               // 13:05 UTC = 20:05 ไทย
    [InlineData("30/09/2026 18:30 PM", SettlementFileTimeZone.Utc, 10, 1)]
    [InlineData("30/09/2026 24:00", SettlementFileTimeZone.Bangkok, 9, 30)]              // สิ้นวันที่เขียน
    [InlineData("30/09/2026 24:00:00", SettlementFileTimeZone.Utc, 10, 1)]
    public void I9_ส่วนท้ายเวลาที่ตีความได้ชัด_อ่านได้(string raw, SettlementFileTimeZone zone, int m, int d)
    {
        Assert.True(SettlementValueParser.TryParseDate(raw, SettlementDateOrder.DayMonthYear, zone, out var v));
        Assert.Equal(new DateTime(2026, m, d, 0, 0, 0, DateTimeKind.Utc), v);
    }

    [Theory]
    [InlineData("30/09/2026 18:30 AM")]      // ขัดกันเอง
    [InlineData("30/09/2026 24:30")]
    [InlineData("30/09/2026 25:00")]
    [InlineData("30/09/2026 24:00 PM")]
    public void I9_ทิศตรงข้าม_เวลาที่ขัดกันเอง_ยังอ่านไม่ได้(string raw)
        => Assert.False(SettlementValueParser.TryParseDate(raw, SettlementDateOrder.DayMonthYear, SettlementFileTimeZone.Bangkok, out _));

    [Fact]
    public void I9_ค่าที่บอกเขตเวลาแล้ว_ไม่ถูกถามเขตเวลา()
    {
        var dmy = SettlementDateOrder.DayMonthYear;
        Assert.False(SettlementValueParser.DependsOnZone("30/09/2026 18:30 ICT", dmy));
        Assert.False(SettlementValueParser.DependsOnZone("2026-09-30 18:00:00 (GMT+07:00)", dmy));
        Assert.True(SettlementValueParser.DependsOnZone("30/09/2026 18:30 น", dmy));       // ไม่มีเขต ⇒ ยังต้องถาม (ทิศเดิม)
        var r = new GenericColumnMapAdapter().Parse(Csv("Date,Txn ID,Type,Amount\n30/09/2026 18:30 ICT,T1,Sale,100\n"), Channel(), LongMap());
        Assert.Equal(Sep30, Assert.Single(r.Rows).TxnDate);
    }

    // ═════════════════ I-10 · ค่าที่จำไว้ขัดกับไฟล์ ═════════════════

    [Fact]
    public void I10_รูปแบบวันที่ที่จำไว้ขัดกับไฟล์_ล้มดังบอกว่ามาจากค่าที่จำ_และทางล้าง_ทิศตรงข้ามตรงกันผ่าน_เขตเวลาที่ตั้งยังชนะหัวคอลัมน์()
    {
        var dates = new List<(int, string?)> { (2, "01/09/2026"), (3, "13/09/2026") };
        var ex = Assert.Throws<SettlementFormatException>(() =>
            SettlementFileDecisions.DecideDates(SettlementDateOrder.MonthDayYear, SettlementFileTimeZone.Auto, "Date", dates, null));
        Assert.Equal("date-order-conflict", ex.Code);
        Assert.Contains("จำไว้", ex.Message);
        Assert.Contains("อัตโนมัติ", ex.Message);
        Assert.Contains("แถว 3", ex.Message);
        Assert.Equal(SettlementDateOrder.DayMonthYear,
            SettlementFileDecisions.DecideDates(SettlementDateOrder.DayMonthYear, SettlementFileTimeZone.Auto, "Date", dates, null).Order.Value);

        // เขตเวลา: หัวคอลัมน์เป็นแค่คำประกาศ ⇒ ค่าที่ผู้ใช้ตั้งยังชนะ (ผู้ใช้ที่รู้ว่าหัวคอลัมน์ผิดต้องมีทางไปต่อ — ไม่ทำเป็นทางตัน)
        Assert.Equal(SettlementFileTimeZone.Bangkok, SettlementFileDecisions.DecideDates(SettlementDateOrder.Auto, SettlementFileTimeZone.Bangkok,
            "Created (UTC)", new List<(int, string?)> { (2, "2026-09-30 18:00") }, null).Zone.Value);
    }

    // ═════════════════ I-11 · เตือนเนื้อหาตรงรอบอื่น ═════════════════

    [Fact]
    public void I11_คำเตือนบอกรอบที่ลงบัญชีแล้ว_ไม่ชักชวนให้ยกเลิกรอบนั้น_ทิศตรงข้ามรอบยังไม่ลง()
    {
        var fee = new SettlementTxnKeyInput(null, "Withdrawal fee", null, -10m, Sep30);
        var rowKey = SettlementTxnKey.Assign(new[] { fee })[0];
        var content = SettlementTxnKey.ContentKey(null, "Withdrawal fee", -10m, Sep30);
        var postedOther = new List<SettlementContentLine> { new(Guid.NewGuid(), rowKey, null, "Withdrawal fee", -10m, Sep30, "PO-1", SettlementBatchStatus.Posted) };
        var hits = SettlementContentOverlap.Find(new List<(int, string?)> { (2, content) }, postedOther);
        Assert.Equal(new[] { "PO-1" }, Assert.Single(hits).PostedPayoutRefs);
        Assert.Contains("PO-1 (ลงบัญชีแล้ว)", SettlementContentOverlap.RefsWithStatus(hits));
        Assert.Contains("ห้ามยกเลิก/กลับรายการรอบที่ลงบัญชีแล้ว", SettlementContentOverlap.WhatToDo(hits));

        var openOther = new List<SettlementContentLine> { new(Guid.NewGuid(), rowKey, null, "Withdrawal fee", -10m, Sep30, "PO-1", SettlementBatchStatus.Matched) };
        var openHits = SettlementContentOverlap.Find(new List<(int, string?)> { (2, content) }, openOther);
        Assert.Empty(Assert.Single(openHits).PostedPayoutRefs!);
        Assert.DoesNotContain("ลงบัญชีแล้ว", SettlementContentOverlap.RefsWithStatus(openHits));
        Assert.Contains("นำเข้าทีหลัง", SettlementContentOverlap.WhatToDo(openHits));
    }

    [Theory]
    [InlineData("v2:row:abc", true)]
    [InlineData("v2:rowc:abc", true)]
    [InlineData("row:abc", true)]
    [InlineData("v2:T1:abc", false)]
    [InlineData("T1|sale", false)]
    [InlineData("pi:abc:sale", false)]
    public void I11_คำนำหน้าคีย์ในSQL_ชุดเดียวกับIsRowKey(string key, bool rowKey)
    {
        Assert.Equal(rowKey, SettlementTxnKey.IsRowKey(key));
        Assert.Equal(rowKey, key.StartsWith(SettlementContentOverlap.RowKeyPrefix, StringComparison.Ordinal)
                             || key.StartsWith(SettlementContentOverlap.LegacyRowKeyPrefix, StringComparison.Ordinal));
    }
}
