using System.Globalization;
using System.Text;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Settlement.Adapters;
using MiniExcelLibs;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม I — ตัวอ่านไฟล์ settlement + คีย์กันซ้ำ (review198-B R-B7–R-B11 · review198-A R-A9 · review198-S4 S4-3/S4-4)
///
/// ทุกกลุ่มมีสองครึ่ง: ไฟล์/แถวที่เคยถูกอ่านผิดเงียบ ๆ ต้องถูกจับ (ล้มดัง/แก้ถูก) และไฟล์ที่ถูกอยู่แล้วต้องได้ผลเหมือนเดิม ·
/// ข้อมูลสังเคราะห์รูปแบบจริงของรายงานรอบโอน (หัวไทย/อังกฤษ · เลขออเดอร์ 18 หลัก · เวลา UTC · ; คั่นคอลัมน์) — ไม่มี PII จริง ·
/// ไม่มีไฟล์ตัวอย่างของเจ้าใดเจ้าหนึ่ง (DECISIONS ข้อ 12: adapter เฉพาะเจ้าเขียนเมื่อมีไฟล์จริงเท่านั้น)
/// </summary>
public class SettlementReview200ReaderTests
{
    private static readonly DateTime Day = new(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);

    private static SettlementChannel Channel() => new() { CompanyId = Guid.NewGuid(), DisplayName = "ร้านทดสอบ", Kind = SettlementChannelKind.Marketplace };

    private static SettlementFileInput Csv(string text) => new("report.csv", Encoding.UTF8.GetBytes(text));

    private static SettlementFileInput Xlsx(List<Dictionary<string, object>> rows)
    {
        using var ms = new MemoryStream();
        MiniExcel.SaveAs(ms, rows, excelType: ExcelType.XLSX);
        return new SettlementFileInput("report.xlsx", ms.ToArray());
    }

    private static string LongMap(string date = "Date", SettlementDateOrder order = SettlementDateOrder.Auto,
        SettlementFileTimeZone zone = SettlementFileTimeZone.Auto, string? orderCol = null)
        => new SettlementColumnMap
        {
            Layout = SettlementFileLayout.Long, TxnId = "Txn ID", OrderId = orderCol, Date = date, Type = "Type", Amount = "Amount",
            DateOrder = order, TimeZone = zone,
        }.ToJson();

    private static string WideMap() => new SettlementColumnMap
    {
        Layout = SettlementFileLayout.Wide,
        OrderId = "Order ID",
        AmountColumns =
        {
            new SettlementAmountColumn { Header = "Merchandise Subtotal" },
            new SettlementAmountColumn { Header = "Commission Fee" },
            new SettlementAmountColumn { Header = "Transaction Fee", Negate = true },
        },
    }.ToJson();

    // ═════════════════ R-B7 · เลขอ้างอิงยาวที่ Excel ปัดหลัก ═════════════════

    [Theory]
    [InlineData("1.2345678901234568E+17")]   // xlsx: เซลล์ตัวเลข 18 หลัก → double
    [InlineData("1.23457E+17")]              // CSV ที่ Excel บันทึก (แสดง 6 หลัก)
    [InlineData("1E+15")]
    [InlineData(" 5.8E+18 ")]
    public void RB7_เลขอ้างอิงรูปscientificของExcel_คือเสียหลักแล้ว(string raw)
        => Assert.True(SettlementValueParser.IdLostPrecision(raw));

    [Theory]
    [InlineData("123456789012345678")]       // ข้อความ 18 หลัก = ถูกต้อง (CSV ที่แพลตฟอร์มส่งออกตรง)
    [InlineData("123456789012345")]
    [InlineData("SO-2609-0001")]
    [InlineData("5E10")]                     // เลขอ้างอิงตัวอักษรปนตัวเลข — ไม่ใช่รูปที่ Excel พิมพ์
    [InlineData("2E3A7F")]
    [InlineData(null)]
    public void RB7_ทิศตรงข้าม_เลขอ้างอิงปกติไม่ถูกแตะ(string? raw)
        => Assert.False(SettlementValueParser.IdLostPrecision(raw));

    [Fact]
    public void RB7_เซลล์ตัวเลขxlsx_ตั้งแต่1e15คงรูปscientific_เลขสั้นกว่านั้นและยอดเงินเหมือนเดิม()
    {
        Assert.Equal("1.2345678901234568E+17", SettlementFileReader.CellText(123456789012345678d));
        Assert.True(SettlementValueParser.IdLostPrecision(SettlementFileReader.CellText(1e16m)));
        Assert.True(SettlementValueParser.IdLostPrecision(SettlementFileReader.CellText(1_234_567_890_123_456_789L)));
        Assert.Equal("123456789012345", SettlementFileReader.CellText(123456789012345d));   // 15 หลัก = Excel เก็บครบ
        Assert.Equal("1070.5", SettlementFileReader.CellText(1070.5m));
        Assert.Equal("-53.5", SettlementFileReader.CellText(-53.5d));
    }

    [Fact]
    public void RB7_xlsxเลขออเดอร์18หลักเป็นเซลล์ตัวเลข_ล้มดังทั้งไฟล์_เป็นข้อความ_ได้เลขครบทุกหลัก()
    {
        Dictionary<string, object> Row(object order) => new()
        {
            ["Order ID"] = order, ["Merchandise Subtotal"] = 1070m, ["Commission Fee"] = -53.5m, ["Transaction Fee"] = 20m,
        };
        var numeric = Xlsx(new List<Dictionary<string, object>> { Row(123456789012345678d), Row(123456789012345679d) });
        var ex = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter().Parse(numeric, Channel(), WideMap()));
        Assert.Equal("id-precision", ex.Code);
        Assert.Contains("15 หลัก", ex.Message);
        Assert.Contains("Order ID", ex.Message);

        var text = Xlsx(new List<Dictionary<string, object>> { Row("123456789012345678"), Row("123456789012345679") });
        var r = new GenericColumnMapAdapter().Parse(text, Channel(), WideMap());
        Assert.Equal(new[] { "123456789012345678", "123456789012345679" }, r.Rows.Select(x => x.ExternalOrderId).Distinct());
    }

    [Fact]
    public void RB7_CSVที่Excelบันทึกเลขเป็น_E17_ล้มดัง_CSVเลขเต็มอ่านได้()
    {
        const string head = "Date,Txn ID,Type,Amount\n";
        var ex = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter()
            .Parse(Csv(head + "13/09/2026,1.23457E+17,Sale,100\n"), Channel(), LongMap()));
        Assert.Equal("id-precision", ex.Code);
        var ok = new GenericColumnMapAdapter().Parse(Csv(head + "13/09/2026,123456789012345678,Sale,100\n"), Channel(), LongMap());
        Assert.Equal("123456789012345678", Assert.Single(ok.Rows).RawTxnId);
    }

    // ═════════════════ R-B8 · เขตเวลา ═════════════════

    [Theory]
    [InlineData("2026-09-30 18:00:00", SettlementFileTimeZone.Utc, 10, 1)]        // 01:00 ของไทย 1 ต.ค. (เดือนภาษีถัดไป)
    [InlineData("2026-09-30 18:00:00", SettlementFileTimeZone.Bangkok, 9, 30)]
    [InlineData("30/09/2026 18:30", SettlementFileTimeZone.Utc, 10, 1)]
    [InlineData("30/09/2026 06:30", SettlementFileTimeZone.Utc, 9, 30)]
    [InlineData("30/09/2026 6:30 PM", SettlementFileTimeZone.Utc, 10, 1)]
    [InlineData("30/09/2026 12:15 AM", SettlementFileTimeZone.Utc, 9, 30)]
    [InlineData("2026-09-30 18:00:00 GMT", SettlementFileTimeZone.Bangkok, 10, 1)]   // offset ในค่า ชนะค่าตั้ง
    [InlineData("2026-10-01 00:30 +07:00", SettlementFileTimeZone.Utc, 10, 1)]
    [InlineData("30 Sep 2026 17:05", SettlementFileTimeZone.Utc, 10, 1)]
    [InlineData("30/09/2026", SettlementFileTimeZone.Utc, 9, 30)]                    // ไม่มีเวลา = วันตามปฏิทิน (ไม่แปลง)
    [InlineData("30/09/2569 18:00 น.", SettlementFileTimeZone.Bangkok, 9, 30)]
    public void RB8_วันที่มีเวลา_แปลงตามเขตเวลาของไฟล์_offsetในค่าชนะเสมอ(string raw, SettlementFileTimeZone zone, int m, int d)
    {
        Assert.True(SettlementValueParser.TryParseDate(raw, SettlementDateOrder.DayMonthYear, zone, out var v));
        Assert.Equal(new DateTime(2026, m, d, 0, 0, 0, DateTimeKind.Utc), v);
    }

    [Fact]
    public void RB8_ExcelSerialมีเศษวัน_คือเวลา_UTCข้ามวัน_ค่าเดิมที่ไม่มีเวลาไม่เปลี่ยน()
    {
        var serial = new DateTime(2026, 9, 30, 18, 0, 0).ToOADate().ToString(CultureInfo.InvariantCulture);
        Assert.True(SettlementValueParser.TryParseDate(serial, SettlementDateOrder.DayMonthYear, SettlementFileTimeZone.Utc, out var v));
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), v);
        Assert.True(SettlementValueParser.DependsOnZone(serial, SettlementDateOrder.DayMonthYear));
        var dateOnly = new DateTime(2026, 9, 30).ToOADate().ToString(CultureInfo.InvariantCulture);
        Assert.False(SettlementValueParser.DependsOnZone(dateOnly, SettlementDateOrder.DayMonthYear));
    }

    [Fact]
    public void RB8_ค่าใดตกคนละวันระหว่างไทยกับUTC()
    {
        var dmy = SettlementDateOrder.DayMonthYear;
        Assert.True(SettlementValueParser.DependsOnZone("2026-09-30 18:00:00", dmy));
        Assert.True(SettlementValueParser.DependsOnZone("30/09/2026 23:59", dmy));
        Assert.False(SettlementValueParser.DependsOnZone("2026-09-30 10:00:00", dmy));     // 17:00 ไทย วันเดียวกัน
        Assert.False(SettlementValueParser.DependsOnZone("2026-09-30", dmy));
        Assert.False(SettlementValueParser.DependsOnZone("2026-09-30T18:00:00Z", dmy));    // มี offset ในตัว
        Assert.False(SettlementValueParser.DependsOnZone("2026-09-30 18:00 +07:00", dmy));
        Assert.False(SettlementValueParser.DependsOnZone("30/09/2026 (จันทร์)", dmy));      // ท้ายที่ไม่ใช่เวลา
    }

    [Theory]
    [InlineData("Created (UTC)", SettlementFileTimeZone.Utc)]
    [InlineData("Order Time (GMT)", SettlementFileTimeZone.Utc)]
    [InlineData("Settled At (Z)", SettlementFileTimeZone.Utc)]
    [InlineData("เวลาทำรายการ (GMT+7)", SettlementFileTimeZone.Bangkok)]
    [InlineData("Order Time (UTC+07:00)", SettlementFileTimeZone.Bangkok)]
    [InlineData("Payout Time ICT", SettlementFileTimeZone.Bangkok)]
    [InlineData("วันที่ (เวลาไทย)", SettlementFileTimeZone.Bangkok)]
    [InlineData("Date (+07:00)", SettlementFileTimeZone.Bangkok)]
    public void RB8_หัวคอลัมน์ประกาศเขตเวลา(string header, SettlementFileTimeZone expected)
        => Assert.Equal(expected, SettlementFileDecisions.TimeZoneFromHeader(header));

    [Theory]
    [InlineData("Date")]
    [InlineData("Settlement Date")]
    [InlineData("วันที่ทำรายการ")]
    [InlineData("Product Code")]
    [InlineData(null)]
    public void RB8_ทิศตรงข้าม_หัวคอลัมน์ที่ไม่บอกเขตเวลา_ไม่ถูกตีความ(string? header)
        => Assert.Null(SettlementFileDecisions.TimeZoneFromHeader(header));

    [Fact]
    public void RB8_หัวคอลัมน์เขตอื่น_ล้มดัง_ไม่อ่านเป็นไทยหรือUTC()
    {
        var ex = Assert.Throws<SettlementFormatException>(() => SettlementFileDecisions.TimeZoneFromHeader("Order Time (GMT+8)"));
        Assert.Equal("timezone-unsupported", ex.Code);
    }

    [Fact]
    public void RB8_ตัดสินเขตเวลา_ค่าตั้งชนะ_หัวคอลัมน์พิสูจน์_ไม่มีผลใช้ไทย_กำกวมและมีผลล้มดัง()
    {
        var dmy = SettlementDateOrder.DayMonthYear;
        var evening = new List<(int, string?)> { (2, "2026-09-30 10:00:00"), (3, "2026-09-30 18:00:00") };
        var morning = new List<(int, string?)> { (2, "2026-09-30 10:00:00"), (3, "2026-09-30") };

        Assert.Equal(new SettlementFileDecision<SettlementFileTimeZone>(SettlementFileTimeZone.Bangkok, false),
            SettlementFileDecisions.DecideTimeZone(SettlementFileTimeZone.Bangkok, "Created (UTC)", evening, dmy));
        Assert.Equal(new SettlementFileDecision<SettlementFileTimeZone>(SettlementFileTimeZone.Utc, true),
            SettlementFileDecisions.DecideTimeZone(SettlementFileTimeZone.Auto, "Created (UTC)", evening, dmy));
        Assert.Equal(new SettlementFileDecision<SettlementFileTimeZone>(SettlementFileTimeZone.Bangkok, false),
            SettlementFileDecisions.DecideTimeZone(SettlementFileTimeZone.Auto, "Created", morning, dmy));
        var ex = Assert.Throws<SettlementFormatException>(() =>
            SettlementFileDecisions.DecideTimeZone(SettlementFileTimeZone.Auto, "Created", evening, dmy));
        Assert.Equal("timezone-ambiguous", ex.Code);
        Assert.Contains("แถว 3", ex.Message);
        Assert.Contains("เขตเวลาในไฟล์", ex.Message);
    }

    [Fact]
    public void RB8_ไฟล์จริง_หัวUTC_ย้ายวัน_และคืนให้จำ_หัวไม่บอกและมีเวลาเย็น_ล้มดัง_ตั้งไทยไว้_ได้วันเดิม()
    {
        const string rows = "2026-09-30 18:00:00,T1,Sale,100\n2026-09-30 10:00:00,T2,PaymentFee,-5\n";
        var utc = new GenericColumnMapAdapter().Parse(Csv("Created (UTC),Txn ID,Type,Amount\n" + rows), Channel(), LongMap("Created (UTC)"));
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), utc.Rows[0].TxnDate);
        Assert.Equal(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), utc.Rows[1].TxnDate);
        Assert.Equal(SettlementFileTimeZone.Utc, utc.LearnedTimeZone);

        var plain = Csv("Created,Txn ID,Type,Amount\n" + rows);
        var ex = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter().Parse(plain, Channel(), LongMap("Created")));
        Assert.Equal("timezone-ambiguous", ex.Code);

        var bkk = new GenericColumnMapAdapter().Parse(plain, Channel(), LongMap("Created", zone: SettlementFileTimeZone.Bangkok));
        Assert.All(bkk.Rows, r => Assert.Equal(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), r.TxnDate));
        Assert.Null(bkk.LearnedTimeZone);                                   // ค่าที่ผู้ใช้ตั้งไว้แล้ว ไม่ต้องจำซ้ำ

        // ทิศ "ไฟล์ที่ถูกอยู่แล้ว": ไม่มีเวลาที่ข้ามวัน ⇒ ผลเหมือนก่อนแก้ทุกตัว (ไม่ถาม · ไม่จำ)
        var morning = new GenericColumnMapAdapter().Parse(Csv("Created,Txn ID,Type,Amount\n2026-09-30 10:00:00,T2,PaymentFee,-5\n"),
            Channel(), LongMap("Created"));
        Assert.Equal(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc), Assert.Single(morning.Rows).TxnDate);
        Assert.Null(morning.LearnedTimeZone);
    }

    // ═════════════════ R-B9 · ลำดับวัน/เดือน ═════════════════

    [Fact]
    public void RB9_ตัดสินลำดับวันเดือน_ค่าตั้งชนะ_หลักฐานเกิน12_ไม่มีผลใช้วันเดือน_กำกวมล้มดัง()
    {
        List<(int, string?)> D(params string[] v) => v.Select((x, i) => (i + 2, (string?)x)).ToList();
        Assert.Equal(new SettlementFileDecision<SettlementDateOrder>(SettlementDateOrder.MonthDayYear, false),
            SettlementFileDecisions.DecideDateOrder(SettlementDateOrder.MonthDayYear, D("13/09/2026"), "Date", null, null));
        Assert.Equal(new SettlementFileDecision<SettlementDateOrder>(SettlementDateOrder.DayMonthYear, true),
            SettlementFileDecisions.DecideDateOrder(SettlementDateOrder.Auto, D("01/09/2026", "13/09/2026"), "Date", null, null));
        Assert.Equal(new SettlementFileDecision<SettlementDateOrder>(SettlementDateOrder.MonthDayYear, true),
            SettlementFileDecisions.DecideDateOrder(SettlementDateOrder.Auto, D("09/01/2026", "09/13/2026"), "Date", null, null));
        Assert.Equal(new SettlementFileDecision<SettlementDateOrder>(SettlementDateOrder.DayMonthYear, false),
            SettlementFileDecisions.DecideDateOrder(SettlementDateOrder.Auto, D("05/05/2026", "2026-09-01", "09/09/2569"), "Date", null, null));

        var ex = Assert.Throws<SettlementFormatException>(() =>
            SettlementFileDecisions.DecideDateOrder(SettlementDateOrder.Auto, D("01/09/2026", "05/09/2026"), "วันที่", null, null));
        Assert.Equal("date-order-ambiguous", ex.Code);
        Assert.Contains("2026-09-01", ex.Message);                   // ทั้งสองความหมายบนหน้าจอ
        Assert.Contains("2026-01-09", ex.Message);
        Assert.Contains("รูปแบบวันที่", ex.Message);
    }

    [Fact]
    public void RB9_ช่วงวันที่ของรอบโอนเป็นหลักฐาน_รับได้แบบเดียวจึงตัดสิน_รับได้ทั้งคู่ยังล้มดัง()
    {
        var dates = new List<(int, string?)> { (2, "01/09/2026"), (3, "05/09/2026") };
        Assert.Equal(new SettlementFileDecision<SettlementDateOrder>(SettlementDateOrder.DayMonthYear, true),
            SettlementFileDecisions.DecideDateOrder(SettlementDateOrder.Auto, dates, "Date", new DateTime(2026, 9, 1), new DateTime(2026, 9, 7)));
        Assert.Equal(new SettlementFileDecision<SettlementDateOrder>(SettlementDateOrder.MonthDayYear, true),
            SettlementFileDecisions.DecideDateOrder(SettlementDateOrder.Auto, dates, "Date", new DateTime(2026, 1, 1), new DateTime(2026, 5, 31)));
        Assert.Throws<SettlementFormatException>(() => SettlementFileDecisions.DecideDateOrder(SettlementDateOrder.Auto, dates, "Date",
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)));
    }

    private const string ThaiLongCsv =
        "วันที่,เลขที่รายการ,หมายเลขคำสั่งซื้อ,ประเภทรายการ,จำนวนเงิน\n" +
        "01/09/2569,T1,O-1,ยอดขาย,\"1,070.00\"\n" +
        "02/09/2569,T2,O-1,ค่าคอมมิชชั่น,(53.50)\n";

    private static string ThaiMap(SettlementDateOrder order) => new SettlementColumnMap
    {
        Layout = SettlementFileLayout.Long, TxnId = "เลขที่รายการ", OrderId = "หมายเลขคำสั่งซื้อ", Date = "วันที่", Type = "ประเภทรายการ",
        Amount = "จำนวนเงิน", DateOrder = order,
    }.ToJson();

    [Fact]
    public void RB9_ไฟล์ไทยวันที่1ถึง2_ยังไม่จำรูปแบบ_ล้มดัง_ระบุช่วงรอบโอน_อ่านได้และคืนให้จำ_ช่องทางที่จำแล้ว_ผลเดิม()
    {
        var ex = Assert.Throws<SettlementFormatException>(() =>
            new GenericColumnMapAdapter().Parse(Csv(ThaiLongCsv), Channel(), ThaiMap(SettlementDateOrder.Auto)));
        Assert.Equal("date-order-ambiguous", ex.Code);

        var withPeriod = new GenericColumnMapAdapter().Parse(Csv(ThaiLongCsv), Channel(), ThaiMap(SettlementDateOrder.Auto),
            new SettlementParseContext(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7)));
        Assert.Equal(new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc), withPeriod.Rows[1].TxnDate);
        Assert.Equal(SettlementDateOrder.DayMonthYear, withPeriod.LearnedDateOrder);

        var remembered = new GenericColumnMapAdapter().Parse(Csv(ThaiLongCsv), Channel(), ThaiMap(SettlementDateOrder.DayMonthYear));
        Assert.Equal(withPeriod.Rows.Select(r => (r.TxnDate, r.Amount)), remembered.Rows.Select(r => (r.TxnDate, r.Amount)));
        Assert.Null(remembered.LearnedDateOrder);
    }

    [Fact]
    public void RB9_จำสิ่งที่ไฟล์พิสูจน์_เฉพาะช่องที่ยังเป็นAuto_ค่าที่ผู้ใช้เลือกไม่ถูกทับ_และบอกผู้ใช้()
    {
        var auto = new SettlementColumnMap();
        var notes = auto.Learn(SettlementDateOrder.MonthDayYear, SettlementFileTimeZone.Utc);
        Assert.Equal(SettlementDateOrder.MonthDayYear, auto.DateOrder);
        Assert.Equal(SettlementFileTimeZone.Utc, auto.TimeZone);
        Assert.Equal(2, notes.Count);
        Assert.Contains("เดือน/วัน/ปี", notes[0]);
        var roundTrip = SettlementColumnMap.Parse(auto.ToJson())!;
        Assert.Equal(SettlementFileTimeZone.Utc, roundTrip.TimeZone);

        var chosen = new SettlementColumnMap { DateOrder = SettlementDateOrder.DayMonthYear, TimeZone = SettlementFileTimeZone.Bangkok };
        Assert.Empty(chosen.Learn(SettlementDateOrder.MonthDayYear, SettlementFileTimeZone.Utc));
        Assert.Equal(SettlementDateOrder.DayMonthYear, chosen.DateOrder);
        Assert.Equal(SettlementFileTimeZone.Bangkok, chosen.TimeZone);
        Assert.Empty(new SettlementColumnMap().Learn(null, null));
        // JSON เดิมที่ไม่มีช่อง timeZone = Auto (ช่องทางที่จำการจับคู่ไว้ก่อนรอบ 200 อ่านได้เหมือนเดิม)
        Assert.Equal(SettlementFileTimeZone.Auto, SettlementColumnMap.Parse("{\"version\":1,\"layout\":\"Long\"}")!.TimeZone);
    }

    // ═════════════════ R-B10 · วงเล็บ + เครื่องหมายลบ ═════════════════

    [Theory]
    [InlineData("(-100)")]
    [InlineData("(100-)")]
    [InlineData("-100-")]
    [InlineData("(+100)")]
    [InlineData("+100-")]
    [InlineData("(−1,234.00)")]
    public void RB10_เครื่องหมายสองชั้นขัดกันเอง_อ่านไม่ได้(string raw)
        => Assert.False(SettlementValueParser.TryParseAmount(raw, out _));

    [Theory]
    [InlineData("(1,234.00)", -1234.00)]
    [InlineData("฿(1,234.00)", -1234.00)]
    [InlineData("( 1,234.00 )", -1234.00)]
    [InlineData("‒5", -5)]            // U+2012 figure dash
    [InlineData("‐5", -5)]            // U+2010 hyphen
    [InlineData("－5", -5)]           // U+FF0D fullwidth
    [InlineData("﹣5", -5)]           // U+FE63 small hyphen-minus
    [InlineData("−53.50", -53.50)]    // U+2212 (เดิมรองรับแล้ว)
    [InlineData("1,234.50-", -1234.50)]
    [InlineData("+1,070", 1070)]
    [InlineData("1 070.00", 1070)]
    public void RB10_ทิศตรงข้าม_เครื่องหมายชั้นเดียวทุกรูปแบบ_อ่านได้ถูก(string raw, double expected)
    {
        Assert.True(SettlementValueParser.TryParseAmount(raw, out var v));
        Assert.Equal((decimal)expected, v);
    }

    [Fact]
    public void RB10_CSVคั่นด้วยอัฒภาค_ยอด1500ไม่มีทศนิยม_ล้มดัง_มียอดพิสูจน์รูปแบบ_หรือคั่นด้วยจุลภาค_อ่านได้()
    {
        Assert.Equal(';', SettlementFileReader.Open(Csv("Date;Txn ID;Type;Amount\n")).CsvDelimiter);
        Assert.Equal(',', SettlementFileReader.Open(Csv("Date,Txn ID,Type,Amount\n")).CsvDelimiter);

        var semi = Csv("Date;Txn ID;Type;Amount\n13/09/2026;T1;Sale;1,500\n");
        var ex = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter().Parse(semi, Channel(), LongMap()));
        Assert.Equal("decimal-comma", ex.Code);
        Assert.Contains("1.5", ex.Message);

        var proven = new GenericColumnMapAdapter().Parse(Csv("Date;Txn ID;Type;Amount\n13/09/2026;T1;Sale;1,500\n13/09/2026;T2;PaymentFee;-2,000.50\n"),
            Channel(), LongMap());
        Assert.Equal(new[] { 1500m, -2000.50m }, proven.Rows.Select(r => r.Amount));
        var comma = new GenericColumnMapAdapter().Parse(Csv("Date,Txn ID,Type,Amount\n13/09/2026,T1,Sale,\"1,500\"\n"), Channel(), LongMap());
        Assert.Equal(1500m, Assert.Single(comma.Rows).Amount);
        Assert.Null(SettlementFileDecisions.DecimalCommaAmbiguity(';', new List<(int, string, string?)> { (2, "Amount", "1500.00") }));
        Assert.Null(SettlementFileDecisions.DecimalCommaAmbiguity(null, new List<(int, string, string?)> { (2, "Amount", "1,500") }));
    }

    // ═════════════════ R-B11 · แถวสรุปของไฟล์แบบกว้าง/ยาว ═════════════════

    [Fact]
    public void RB11_ตัวตัดสินแถวสรุป_กว้างไม่มีเลขอ้างอิง_ยาวคำสรุปหรือไม่มีป้ายและวันที่()
    {
        var wide = SettlementFileLayout.Wide;
        var lng = SettlementFileLayout.Long;
        Assert.True(SettlementFileDecisions.IsSummaryRow(wide, null, null, null, null, "ยอดสุทธิ"));
        Assert.True(SettlementFileDecisions.IsSummaryRow(wide, null, "  ", null, "13/09/2026", "1605"));   // ช่องแรกว่าง/มีวันที่ก็ยังใช่
        Assert.False(SettlementFileDecisions.IsSummaryRow(wide, null, "SO-1", null, null, "SO-1"));
        Assert.True(SettlementFileDecisions.IsSummaryRow(lng, null, null, null, null, "รวม"));
        Assert.True(SettlementFileDecisions.IsSummaryRow(lng, null, null, "Net total", "13/09/2026", "Net total"));
        Assert.True(SettlementFileDecisions.IsSummaryRow(lng, null, null, null, null, "816.50"));
        // ทิศตรงข้าม: แถวไม่มีเลขที่มีป้าย+วันที่ = รายการจริง (ค่าธรรมเนียมถอนเงิน) · มีเลข = รายการเสมอ
        Assert.False(SettlementFileDecisions.IsSummaryRow(lng, null, null, "ค่าธรรมเนียมถอนเงิน", "13/09/2026", "13/09/2026"));
        Assert.False(SettlementFileDecisions.IsSummaryRow(lng, "T9", null, null, null, "รวม"));
    }

    [Fact]
    public void RB11_xlsxแบบกว้าง_แถวรวมท้ายไฟล์ช่องแรกว่าง_ไม่ถูกนับเป็นรายการ_แถวออเดอร์ได้ผลเดิม()
    {
        Dictionary<string, object> Row(string order, decimal sale, decimal comm, decimal fee) => new()
        {
            ["Order ID"] = order, ["Merchandise Subtotal"] = sale, ["Commission Fee"] = comm, ["Transaction Fee"] = fee,
        };
        var orders = new List<Dictionary<string, object>> { Row("SO-1", 1070m, -53.5m, 20m), Row("SO-2", 535m, 0m, 10m) };
        var before = new GenericColumnMapAdapter().Parse(Xlsx(orders), Channel(), WideMap());

        var withTotals = orders.Append(Row("", 1605m, -53.5m, 30m)).Append(Row("ยอดสุทธิ", 1521.5m, 0m, 0m)).ToList();
        withTotals[^1]["Order ID"] = "";
        var r = new GenericColumnMapAdapter().Parse(Xlsx(withTotals), Channel(), WideMap());
        Assert.Equal(before.Rows.Select(x => (x.ExternalOrderId, x.RawTypeLabel, x.Amount)),
            r.Rows.Select(x => (x.ExternalOrderId, x.RawTypeLabel, x.Amount)));
        Assert.Equal(5, r.Rows.Count);
        Assert.Equal(2, r.SkippedRows.Count(s => s.Contains("แถวสรุปยอด")));
        Assert.Contains(r.SkippedRows, s => s.Contains("1,521.50"));      // ยอดของแถวที่ข้ามบนหน้าจอ
    }

    [Fact]
    public void RB11_CSVแบบยาว_แถวรวมช่องแรกว่างถูกข้ามไม่ล้มทั้งไฟล์_แถวค่าธรรมเนียมไม่มีเลขยังนำเข้า()
    {
        const string csv = "Date,Txn ID,Type,Amount\n" +
                           "13/09/2026,T1,Sale,\"1,070.00\"\n" +
                           "13/09/2026,,WithdrawalFee,-10\n" +
                           ",,,1060.00\n";
        var r = new GenericColumnMapAdapter().Parse(Csv(csv), Channel(), LongMap());
        Assert.Equal(new[] { 1070m, -10m }, r.Rows.Select(x => x.Amount));
        Assert.Null(r.Rows[1].RawTxnId);
        Assert.Contains(r.SkippedRows, s => s.Contains("แถวสรุปยอด") && s.Contains("1,060.00"));
    }

    // ═════════════════ R-A9 · บรรทัดย่อยของรายการเดียวกัน ═════════════════

    [Fact]
    public void RA9_ออเดอร์เดียวหลายคอลัมน์ค่าธรรมเนียม_คีย์ไม่ชนกัน_นำเข้าไฟล์เดิมซ้ำได้คีย์เดิม()
    {
        var file = Xlsx(new List<Dictionary<string, object>>
        {
            new() { ["Order ID"] = "SO-1", ["Merchandise Subtotal"] = 1070m, ["Commission Fee"] = -53.5m, ["Transaction Fee"] = 53.5m },
        });
        var rows = new GenericColumnMapAdapter().Parse(file, Channel(), WideMap()).Rows;
        var inputs = rows.Select(x => new SettlementTxnKeyInput(x.RawTxnId, x.RawTypeLabel, x.ExternalOrderId, x.Amount, x.TxnDate, x.PayoutRef)).ToList();
        var keys = SettlementTxnKey.Assign(inputs);
        Assert.Equal(3, keys.Distinct().Count());                          // ค่าคอม −53.50 กับค่าธรรมเนียม −53.50 ของออเดอร์เดียวกันไม่ชน
        Assert.Equal(keys, SettlementTxnKey.Assign(inputs));               // idempotent — นำเข้าซ้ำตรวจเจอ
        // คืนเงินที่พก id ของรายการขายเดิม ได้คีย์ของตัวเอง
        Assert.NotEqual(SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput("T1", "Sale", "O-1", 500m, Day) })[0],
            SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput("T1", "Refund", "O-1", -500m, Day) })[0]);
    }

    // ═════════════════ S4-3 · เทียบเนื้อหาเฉพาะไฟล์รุ่นก่อนของไฟล์เดียวกัน ═════════════════

    private static string C(string label, decimal amount) => SettlementTxnKey.ContentKey(null, label, amount, Day);

    [Fact]
    public void S43_อีกไฟล์ของรอบเดียวกัน_มีรายการหน้าตาเหมือน_ไม่ถูกกลืน_ไฟล์ฉบับแก้_ยังข้ามแถวที่มีแล้ว()
    {
        var w = C("Withdrawal fee", -10m);
        var x = C("Adjustment", 5m);
        var y = C("Ads fee", -20m);
        var file1 = new[] { new SettlementStoredContent(w, "scope1"), new SettlementStoredContent(x, "scope1") };

        // ไฟล์ 2 = ส่วนที่เหลือของรอบ (มีค่าธรรมเนียมถอนเงินอีกรายการที่หน้าตาเหมือน) ⇒ ไม่ใช่ฉบับแก้ของไฟล์ 1 ⇒ ไม่มีอะไรถูกข้าม
        var rest = new string?[] { w, y };
        var pool = SettlementTxnKey.SplitRevisedFilePool(rest, file1);
        Assert.Empty(pool.SameFile);
        Assert.Equal(2, pool.OtherFiles.Count);
        Assert.Empty(SettlementTxnKey.MatchByContent(rest, pool.SameFile));
        Assert.Single(SettlementTxnKey.MatchByContent(rest, pool.OtherFiles));    // ผู้นำเข้าเตือนรายแถว (ไม่กลืน)

        // ไฟล์ฉบับแก้ของไฟล์ 1 (แถวเดิมครบ + แถวเพิ่ม) ⇒ แถวเดิมข้าม · แถวเพิ่มเข้า (พฤติกรรม S3-4 คงเดิม)
        var revised = new string?[] { w, x, y };
        var pool2 = SettlementTxnKey.SplitRevisedFilePool(revised, file1);
        Assert.Equal(new[] { 0, 1 }, SettlementTxnKey.MatchByContent(revised, pool2.SameFile).OrderBy(i => i));
    }

    [Fact]
    public void S43_นับจำนวน_และบรรทัดก่อนรอบ200ที่ไม่มีscope_ใช้พฤติกรรมเดิม()
    {
        var a = C("Withdrawal fee", -10m);
        var twoA = new[] { new SettlementStoredContent(a, "s1"), new SettlementStoredContent(a, "s1") };
        Assert.Empty(SettlementTxnKey.SplitRevisedFilePool(new string?[] { a, C("x", 1m) }, twoA).SameFile);   // ต้องมี A สองแถว
        Assert.Equal(2, SettlementTxnKey.SplitRevisedFilePool(new string?[] { a, a, null }, twoA).SameFile.Count);
        var legacy = new[] { new SettlementStoredContent(a, null) };
        Assert.Single(SettlementTxnKey.SplitRevisedFilePool(new string?[] { C("อื่น", 3m) }, legacy).SameFile);

        var rows = new[]
        {
            new SettlementTxnKeyInput(null, "Withdrawal fee", null, -10m, Day),
            new SettlementTxnKeyInput(null, "Sale", "O-1", 500m, Day),
        };
        Assert.Equal(SettlementTxnKey.ImportScopeOf(rows), SettlementTxnKey.ImportScopeOf(rows.Reverse().ToList()));
        Assert.NotEqual(SettlementTxnKey.ImportScopeOf(rows),
            SettlementTxnKey.ImportScopeOf(rows.Append(new SettlementTxnKeyInput(null, "Ads", null, -1m, Day)).ToList()));
        Assert.Null(SettlementTxnKey.ImportScopeOf(Array.Empty<SettlementTxnKeyInput>()));
    }

    // ═════════════════ S4-4 · เนื้อหาตรงรอบอื่น = ข้อเท็จจริงของพรีวิว/ลงบัญชี ═════════════════

    [Fact]
    public void S44_เทียบเนื้อหากับรอบโอนอื่น_นับเฉพาะบรรทัดไม่มีid_ที่ถูกอ้างด้วยคีย์ไม่นับ()
    {
        var fee = new SettlementTxnKeyInput(null, "Withdrawal fee", null, -10m, Day);
        var rowKey = SettlementTxnKey.Assign(new[] { fee })[0];
        var idKey = SettlementTxnKey.Assign(new[] { fee with { RawTxnId = "T1" } })[0];
        var others = new List<SettlementContentLine>
        {
            new(Guid.NewGuid(), rowKey, null, "Withdrawal fee", -10m, Day, "PO-1"),
            new(Guid.NewGuid(), idKey, null, "Withdrawal fee", -10m, Day, "PO-9"),     // มีเลขรายการ — กันซ้ำด้วย id แล้ว
        };
        var candidates = new List<(int, string?)> { (2, C("Withdrawal fee", -10m)), (3, C("Withdrawal fee", -11m)), (4, null) };
        var hit = Assert.Single(SettlementContentOverlap.Find(candidates, others));
        Assert.Equal(2, hit.Id);
        Assert.Equal(new[] { "PO-1" }, hit.PayoutRefs);
        Assert.Empty(SettlementContentOverlap.Find(candidates, others, new HashSet<string> { rowKey }));
    }

    [Fact]
    public void S44_คำเตือนติดแผนทุกพรีวิว_ไม่บล็อก_ไม่แตะCanPost_ไม่มีผลตรงคืนแผนเดิม()
    {
        var channel = new SettlementChannel
        {
            CompanyId = Guid.NewGuid(), DisplayName = "ร้าน", Kind = SettlementChannelKind.Marketplace, ClearingAccountId = Guid.NewGuid(),
        };
        var batch = new SettlementBatch { CompanyId = channel.CompanyId, PayoutRef = "PO-2", PayoutDate = Day, NetPayout = 0m };
        var line = new SettlementLine
        {
            Id = Guid.NewGuid(), CompanyId = channel.CompanyId, Seq = 1, LineType = SettlementLineType.WithdrawalFee, Amount = -10m, TxnDate = Day,
            ExternalTxnId = SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput(null, "Withdrawal fee", null, -10m, Day) })[0],
            RawTypeLabel = "Withdrawal fee",
        };
        var lines = new List<SettlementLine> { line };
        var plan = SettlementBatchMath.Plan(batch, lines, channel, false);

        Assert.Same(plan, SettlementContentOverlap.Annotate(plan, Array.Empty<SettlementContentHit<Guid>>(), lines));
        var annotated = SettlementContentOverlap.Annotate(plan, new[] { new SettlementContentHit<Guid>(line.Id, new[] { "PO-1" }) }, lines);
        Assert.Equal(plan.CanPost, annotated.CanPost);
        var issue = Assert.Single(annotated.Issues, i => i.Code == SettlementPlanIssueCode.ContentOverlapElsewhere);
        Assert.False(issue.Blocking);
        Assert.Contains("PO-1", issue.Message);
        Assert.Equal(new[] { line.Id }, issue.LineIds);
        Assert.Equal(-10m, issue.Amount);
        Assert.Equal(plan.Issues.Count + 1, annotated.Issues.Count);
        Assert.Single(SettlementContentOverlap.BatchCandidates(lines));
    }

    // ═════════════════ ข้อมูลอ้างอิงของหน้าเว็บ ═════════════════

    [Fact]
    public void ตัวเลือกเขตเวลาบนหน้าจับคู่คอลัมน์_ครบทุกค่าของenum_ป้ายไทย()
    {
        var r = SettlementReferenceCatalog.Build();
        Assert.Equal(Enum.GetNames<SettlementFileTimeZone>().OrderBy(x => x), r.TimeZones.Select(o => o.Value).OrderBy(x => x));
        Assert.All(r.TimeZones, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
    }
}
