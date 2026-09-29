using System.Text;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Settlement.Adapters;
using MiniExcelLibs;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 198 เฟส 1 ทีม B — นำเข้า settlement report: อ่านไฟล์ (CSV/xlsx · หัวไทย/อังกฤษ · พ.ศ. · วงเล็บติดลบ) · คีย์กันซ้ำ ·
/// ตัด PII · จัดประเภท (local ก่อน · ด่านคำตอบ AI · kill-switch) · จับคู่ใบขาย (ไม่เดา) · ผังพัก gateway (R-A1) ·
/// wallet ไม่ใช่ลูกหนี้การค้า (R-A2) — ทุกกลุ่มมีทั้ง "เคสที่ต้องจับ" และ "เคสที่ถูกอยู่แล้วต้องไม่ถูกแตะ"
/// ข้อมูลทดสอบสังเคราะห์ทั้งหมด (ไม่มี PII จริง)
/// </summary>
public class SettlementImportTests
{
    private static SettlementChannel Channel(SettlementChannelKind kind = SettlementChannelKind.Marketplace, string? mapJson = null)
        => new() { CompanyId = Guid.NewGuid(), DisplayName = "ร้านทดสอบ", Kind = kind, ColumnMapJson = mapJson };

    private static SettlementFileInput Csv(string text) => new("report.csv", Encoding.UTF8.GetBytes(text));

    private static readonly string LongMap = new SettlementColumnMap
    {
        Layout = SettlementFileLayout.Long,
        TxnId = "เลขที่รายการ",
        OrderId = "หมายเลขคำสั่งซื้อ",
        Date = "วันที่",
        Type = "ประเภทรายการ",
        Description = "รายละเอียด",
        Amount = "จำนวนเงิน",
    }.ToJson();

    private const string LongCsv =
        "รายงานการโอนเงิน,,,,,\n" +
        "วันที่,เลขที่รายการ,หมายเลขคำสั่งซื้อ,ประเภทรายการ,รายละเอียด,จำนวนเงิน\n" +
        "01/09/2569,T1,O-1,ยอดขาย,\"ลูกค้า: สมมติ ทดสอบ | โทร 081-234-5678\",\"1,070.00\"\n" +
        "01/09/2569,T1,O-1,ค่าคอมมิชชั่น,,(53.50)\n" +
        "02/09/2569,T2,O-2,Refund,,-200\n" +
        "02/09/2569,T3,O-3,ค่าธรรมเนียมถอนเงิน,,0\n" +
        "รวม,,,,,816.50\n";

    // ═════════════════ อ่านไฟล์ ═════════════════

    [Fact]
    public void CSVแบบยาว_หัวไทย_พศ_วงเล็บติดลบ_คั่นหลักพัน_ข้ามแถวรวมและแถวศูนย์()
    {
        var r = new GenericColumnMapAdapter().Parse(Csv(LongCsv), Channel(), LongMap);
        Assert.Equal(3, r.Rows.Count);
        Assert.Equal(new[] { 1070.00m, -53.50m, -200m }, r.Rows.Select(x => x.Amount));
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), r.Rows[0].TxnDate);
        Assert.Equal("O-1", r.Rows[1].ExternalOrderId);
        Assert.Equal("ค่าคอมมิชชั่น", r.Rows[1].RawTypeLabel);
        Assert.Equal(2, r.SkippedRows.Count);                        // แถวยอด 0 + แถว "รวม"
        Assert.Contains(r.SkippedRows, s => s.Contains("สรุปยอด"));
    }

    [Fact]
    public void รูปแบบไฟล์เปลี่ยน_หัวคอลัมน์ที่จับคู่ไว้หาย_ล้มดังทั้งไฟล์พร้อมทางไปต่อ()
    {
        var changed = LongCsv.Replace("ประเภทรายการ", "ชนิด", StringComparison.Ordinal);
        var ex = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter().Parse(Csv(changed), Channel(), LongMap));
        Assert.Equal("header-mismatch", ex.Code);
        Assert.Contains("จับคู่คอลัมน์ใหม่", ex.Message);
        Assert.DoesNotContain("081-234-5678", ex.Message);           // ตัวอย่างแถวในข้อความถูกตัด PII
    }

    [Fact]
    public void ยอดอ่านไม่ได้กลางไฟล์_ล้มทั้งไฟล์ไม่คืนครึ่งไฟล์()
    {
        var bad = LongCsv.Replace("(53.50)", "ห้าสิบ", StringComparison.Ordinal);
        var ex = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter().Parse(Csv(bad), Channel(), LongMap));
        Assert.Equal("amount-invalid", ex.Code);
    }

    [Fact]
    public void ยังไม่จับคู่คอลัมน์_ล้มดังให้ไปตรวจไฟล์()
    {
        var ex = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter().Parse(Csv(LongCsv), Channel(), null));
        Assert.Equal("column-map-missing", ex.Code);
    }

    [Fact]
    public void xlsx_แบบกว้าง_หลายคอลัมน์ยอด_ไม่อ่านคอลัมน์ผู้ซื้อ_คอลัมน์ใหม่ล้มดัง()
    {
        var rows = new List<Dictionary<string, object>>
        {
            new() { ["Order ID"] = "SO-1", ["Merchandise Subtotal"] = 1070m, ["Commission Fee"] = -53.5m, ["Transaction Fee"] = 20m,
                    ["Buyer Name"] = "ผู้ซื้อสมมติ" },
            new() { ["Order ID"] = "SO-2", ["Merchandise Subtotal"] = 535m, ["Commission Fee"] = 0m, ["Transaction Fee"] = 10m,
                    ["Buyer Name"] = "ผู้ซื้อสมมติสอง" },
        };
        using var ms = new MemoryStream();
        MiniExcel.SaveAs(ms, rows, excelType: ExcelType.XLSX);
        var file = new SettlementFileInput("wide.xlsx", ms.ToArray());
        var map = new SettlementColumnMap
        {
            Layout = SettlementFileLayout.Wide,
            OrderId = "Order ID",
            AmountColumns =
            {
                new SettlementAmountColumn { Header = "Merchandise Subtotal" },
                new SettlementAmountColumn { Header = "Commission Fee" },
                new SettlementAmountColumn { Header = "Transaction Fee", Negate = true, VatExclusive = true },
            },
            Ignore = { "Buyer Name" },
        };
        var r = new GenericColumnMapAdapter().Parse(file, Channel(), map.ToJson());
        Assert.Equal(5, r.Rows.Count);                                // SO-1 ×3 · SO-2 ×2 (ค่าคอม 0 ข้าม)
        var fee = r.Rows.First(x => x.RawTypeLabel == "Transaction Fee" && x.ExternalOrderId == "SO-1");
        Assert.Equal(-21.40m, fee.Amount);                            // 20 ก่อน VAT → 21.40 รวม VAT · กลับเครื่องหมาย
        Assert.Equal(-1.40m, fee.VatAmount);
        Assert.DoesNotContain(r.Rows, x => (x.Description ?? "").Contains("ผู้ซื้อ"));

        map.Ignore.Clear();                                           // คอลัมน์ที่ไม่ได้เลือกว่าใช้/ไม่ใช้ = รูปแบบใหม่
        var ex = Assert.Throws<SettlementFormatException>(() => new GenericColumnMapAdapter().Parse(file, Channel(), map.ToJson()));
        Assert.Equal("header-new", ex.Code);
    }

    [Fact]
    public void CSVค่าในเครื่องหมายคำพูดขึ้นบรรทัดใหม่ได้_และคำพูดไม่ปิดล้มดัง()
    {
        var rows = SettlementFileReader.ReadCsv("a,b\n\"x\ny\",\"say \"\"hi\"\"\"\n").ToList();
        Assert.Equal("x\ny", rows[1][0]);
        Assert.Equal("say \"hi\"", rows[1][1]);
        Assert.Throws<SettlementFormatException>(() => SettlementFileReader.ReadCsv("a,b\n\"open,1\n").ToList());
    }

    [Fact]
    public void ไฟล์CSVภาษาไทยแบบWindows874_ถอดรหัสได้()
    {
        var enc = CodePagesEncodingProvider.Instance.GetEncoding(874)!;
        Assert.Equal("ประเภทรายการ,ยอดขาย", SettlementFileReader.Decode(enc.GetBytes("ประเภทรายการ,ยอดขาย")));
        Assert.Equal("abc", SettlementFileReader.Decode(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("abc")).ToArray()));
    }

    [Theory]
    [InlineData("1,234.50", 1234.50)]
    [InlineData("(1,234.50)", -1234.50)]
    [InlineData("1,234.50-", -1234.50)]
    [InlineData("−53.5", -53.5)]
    [InlineData("฿ 2,000", 2000)]
    [InlineData("THB -10.00", -10)]
    [InlineData("๑,๐๗๐.๐๐", 1070)]
    [InlineData("0.125", 0.13)]
    public void ยอดเงินหลายรูปแบบ(string raw, double expected)
    {
        Assert.True(SettlementValueParser.TryParseAmount(raw, out var v));
        Assert.Equal((decimal)expected, v);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12,34,5")]
    [InlineData("1.2.3")]
    public void ข้อความที่ไม่ใช่ยอด_อ่านไม่ผ่าน(string raw)
        => Assert.False(SettlementValueParser.TryParseAmount(raw, out _));

    [Fact]
    public void ช่องว่างและขีด_คือไม่มีค่า_ไม่ใช่ศูนย์()
    {
        Assert.True(SettlementValueParser.TryParseAmount("  ", out var a));
        Assert.Null(a);
        Assert.True(SettlementValueParser.TryParseAmount("-", out var b));
        Assert.Null(b);
    }

    [Theory]
    [InlineData("01/09/2569", 2026, 9, 1)]
    [InlineData("1/9/2026", 2026, 9, 1)]
    [InlineData("2026-09-01", 2026, 9, 1)]
    [InlineData("2026-09-01 23:59:00", 2026, 9, 1)]
    [InlineData("1 ก.ย. 2569", 2026, 9, 1)]
    [InlineData("01-Sep-2026", 2026, 9, 1)]
    [InlineData("Sep 1, 2026", 2026, 9, 1)]
    [InlineData("2026-09-30T20:00:00Z", 2026, 10, 1)]              // 03:00 กรุงเทพ = วันที่ 1 ต.ค. (ฝ่ายค้าน R-A6)
    [InlineData("2026-10-01T02:30:00+07:00", 2026, 10, 1)]
    public void วันที่หลายรูปแบบ_พศเป็นคศ_คืนวันตามปฏิทินไทย(string raw, int y, int m, int d)
    {
        Assert.True(SettlementValueParser.TryParseDate(raw, SettlementDateOrder.DayMonthYear, out var v));
        Assert.Equal(new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc), v);
    }

    [Fact]
    public void ปีสองหลักกำกวม_อ่านไม่ผ่าน_และลำดับวันเดือนตัดสินจากทั้งไฟล์()
    {
        Assert.False(SettlementValueParser.TryParseDate("01/09/69", SettlementDateOrder.DayMonthYear, out _));
        Assert.Equal(SettlementDateOrder.DayMonthYear, SettlementValueParser.DetectDateOrder(new[] { "13/01/2026", "02/03/2026" }));
        Assert.Equal(SettlementDateOrder.MonthDayYear, SettlementValueParser.DetectDateOrder(new[] { "01/13/2026", "02/03/2026" }));
        Assert.Equal(SettlementDateOrder.DayMonthYear, SettlementValueParser.DetectDateOrder(new[] { "01/02/2026" }));
        Assert.Throws<SettlementFormatException>(() => SettlementValueParser.DetectDateOrder(new[] { "13/01/2026", "01/13/2026" }));
    }

    // ═════════════════ คีย์กันซ้ำ (R-A9) ═════════════════

    [Fact]
    public void รายการเดียวกันของแพลตฟอร์ม_ยอดขายกับค่าธรรมเนียม_ได้คีย์คนละตัว()
    {
        var keys = SettlementTxnKey.Assign(new[]
        {
            new SettlementTxnKeyInput("trxn_1", "Charge", "O-1", 1070m, null),
            new SettlementTxnKeyInput("trxn_1", "Fee", "O-1", -41.79m, null),
            new SettlementTxnKeyInput("trxn_1", "Refund", "O-1", -100m, null),
        });
        Assert.Equal(3, keys.Distinct().Count());
    }

    [Fact]
    public void คีย์ขึ้นกับเนื้อหาแถวเท่านั้น_ไฟล์ที่ช่วงทับกันได้คีย์เดิม_นำเข้าซ้ำตรวจเจอ()
    {
        var fee = new SettlementTxnKeyInput("T1", "Commission fee", "O-1", -53.5m, new DateTime(2026, 9, 1));
        var inTwoRowFile = SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput("T1", "Sale", "O-1", 1070m, null), fee })[1];
        var inOneRowFile = SettlementTxnKey.Assign(new[] { fee })[0];
        Assert.Equal(inTwoRowFile, inOneRowFile);

        var noId = new SettlementTxnKeyInput(null, "ค่าบริการ", "O-9", -10m, new DateTime(2026, 9, 2));
        var first = SettlementTxnKey.Assign(new[] { noId, noId });
        Assert.NotEqual(first[0], first[1]);                          // สองรายการจริงที่หน้าตาเหมือนกัน
        Assert.EndsWith("#2", first[1]);
        Assert.Equal(first, SettlementTxnKey.Assign(new[] { noId, noId }));   // idempotent
        Assert.NotEqual(first[0], SettlementTxnKey.Assign(new[] { noId with { Amount = -11m } })[0]);
    }

    [Fact]
    public void คีย์ยาวเกินคอลัมน์_ย่อเป็นแฮช()
    {
        var k = SettlementTxnKey.Assign(new[] { new SettlementTxnKeyInput(new string('x', 250), "fee", null, 1m, null) })[0];
        Assert.StartsWith("h:", k);
        Assert.True(k.Length <= SettlementTxnKey.MaxLength);
    }

    // ═════════════════ ตัด PII ═════════════════

    [Fact]
    public void ตัดชื่อ_เบอร์_อีเมล_ที่อยู่_เลขบัตร()
    {
        var s = SettlementPiiScrubber.Scrub(
            "ลูกค้า: สมมติ ทดสอบ | โทร 081-234-5678 | a.b@example.com | นายทดสอบ สมมติ 99/1 หมู่ 5 ต.บางพลี อ.บางพลี จ.สมุทรปราการ 10540 | 1-2345-67890-12-3")!;
        Assert.DoesNotContain("สมมติ", s);
        Assert.DoesNotContain("081", s);
        Assert.DoesNotContain("example.com", s);
        Assert.DoesNotContain("บางพลี", s);
        Assert.DoesNotContain("10540", s);
        Assert.DoesNotContain("67890", s);
        Assert.Contains(SettlementPiiScrubber.RedactedLabelValue, s);
        Assert.Equal(s, SettlementPiiScrubber.Scrub(s));             // idempotent
    }

    [Theory]
    [InlineData("Commission fee 3% order 2309145ABCDEF")]
    [InlineData("คุณภาพสินค้า เกรด A")]
    [InlineData("ค่านายหน้า")]
    [InlineData("นายหน้าขาย")]
    [InlineData("ส่วนลด 1 ม.ค. 2569 ยอด 1,070.00")]
    [InlineData("Transaction fee (VAT 7%)")]
    public void ข้อความที่ไม่ใช่PII_ไม่ถูกแตะ(string text)
        => Assert.Equal(text, SettlementPiiScrubber.Scrub(text));

    [Fact]
    public void ข้อความว่าง_เป็นnull()
    {
        Assert.Null(SettlementPiiScrubber.Scrub(null));
        Assert.Null(SettlementPiiScrubber.Scrub("   "));
        Assert.Equal(SettlementPiiScrubber.MaxLength, SettlementPiiScrubber.Scrub(new string('ก', 900))!.Length);
    }

    // ═════════════════ จัดประเภท ═════════════════

    [Fact]
    public void ป้ายถูกทำให้เทียบได้_ตัดอัตราและตัวเลข()
    {
        Assert.Equal("transaction fee", SettlementLineClassification.NormalizeLabel("Transaction Fee (3.65%)"));
        Assert.Equal("commission fee", SettlementLineClassification.NormalizeLabel("  COMMISSION_FEE : "));
        Assert.Equal("", SettlementLineClassification.NormalizeLabel(null));
    }

    [Fact]
    public void seedตั้งต้น_ป้ายที่รู้จัก_และป้ายที่ความหมายขึ้นกับชนิดช่องทาง()
    {
        Assert.Equal(SettlementLineType.Commission, SettlementLabelSeed.Lookup("Commission Fee", SettlementChannelKind.Marketplace));
        Assert.Equal(SettlementLineType.SellerVoucher, SettlementLabelSeed.Lookup("โค้ดส่วนลดร้านค้า", SettlementChannelKind.Marketplace));
        Assert.Equal(SettlementLineType.PaymentFee, SettlementLabelSeed.Lookup("Fee", SettlementChannelKind.Gateway));
        Assert.Null(SettlementLabelSeed.Lookup("Fee", SettlementChannelKind.Marketplace));        // กำกวมนอก gateway
        Assert.Null(SettlementLabelSeed.Lookup("Adjustment", SettlementChannelKind.Marketplace)); // ห้าม seed ปรับปรุง
        Assert.Null(SettlementLabelSeed.Lookup("อะไรก็ไม่รู้", SettlementChannelKind.Marketplace));
    }

    [Fact]
    public void ลำดับlocal_adapterก่อน_คลังที่เรียนชนะseed_เครื่องหมายขัดไม่บังคับเข้า()
    {
        var a = SettlementLineClassification.ResolveLocal(SettlementLineType.Sale, SettlementLineType.Commission, null, 100m);
        Assert.Equal((SettlementLineType.Sale, SettlementClassifiedBy.AdapterRule), (a.Type, a.By));
        var l = SettlementLineClassification.ResolveLocal(null, SettlementLineType.ServiceFee, SettlementLineType.Commission, -10m);
        Assert.Equal((SettlementLineType.ServiceFee, SettlementClassifiedBy.Learned), (l.Type, l.By));
        var s = SettlementLineClassification.ResolveLocal(null, null, SettlementLineType.Commission, -10m);
        Assert.Equal((SettlementLineType.Commission, SettlementClassifiedBy.AdapterRule), (s.Type, s.By));
        // ป้าย "Refund" บนยอดบวก ≠ คืนเงิน
        Assert.False(SettlementLineClassification.ResolveLocal(null, null, SettlementLineType.Refund, 50m).Resolved);
        // adapter ประเภทขาย บนยอดลบ ⇒ ตกไปชั้นถัดไป
        var fallback = SettlementLineClassification.ResolveLocal(SettlementLineType.Sale, null, SettlementLineType.Refund, -50m);
        Assert.Equal(SettlementLineType.Refund, fallback.Type);
        // ปรับปรุง (บังคับเหตุผล) ไม่มาจากคลัง/seed
        Assert.False(SettlementLineClassification.ResolveLocal(null, SettlementLineType.Adjustment, null, 5m).Resolved);
    }

    [Fact]
    public void คลังที่เรียน_ต้องชนะขาด_เสียงแตกคือไม่รู้()
    {
        Assert.Equal(SettlementLineType.Commission, SettlementLineClassification.LearnedVote(new[]
            { SettlementLineType.Commission, SettlementLineType.Commission, SettlementLineType.ServiceFee }));
        Assert.Null(SettlementLineClassification.LearnedVote(new[] { SettlementLineType.Commission, SettlementLineType.ServiceFee }));
        Assert.Null(SettlementLineClassification.LearnedVote(Array.Empty<SettlementLineType>()));
    }

    [Fact]
    public void ด่านคำตอบAI_รับเฉพาะชื่อenumที่ลงบัญชีได้_มั่นใจพอ_เครื่องหมายถูก_ไม่ใช่majority()
    {
        decimal fee = -53.5m;
        Assert.Equal(SettlementLineType.Commission,
            SettlementLineClassification.AcceptModelAnswer("Commission", 0.9m, usedAi: true, fromLocalModel: false, "provider-model", fee));
        // นักเรียนตอบ (คลังทันที 0.80) ⇒ รับ
        Assert.Equal(SettlementLineType.Commission,
            SettlementLineClassification.AcceptModelAnswer("commission", 0.8m, false, true, "local:v202609-instant", fee));
        Assert.Null(SettlementLineClassification.AcceptModelAnswer("CommissionFeeX", 0.99m, true, false, null, fee));   // นอกชุด
        Assert.Null(SettlementLineClassification.AcceptModelAnswer("5", 0.99m, true, false, null, fee));                // ตัวเลข
        Assert.Null(SettlementLineClassification.AcceptModelAnswer("Unclassified", 0.99m, true, false, null, fee));
        Assert.Null(SettlementLineClassification.AcceptModelAnswer("Adjustment", 0.99m, true, false, null, fee));        // ต้องมีเหตุผลจากคน
        Assert.Null(SettlementLineClassification.AcceptModelAnswer("Commission", 0.69m, true, false, null, fee));        // ต่ำกว่าเกณฑ์
        Assert.Null(SettlementLineClassification.AcceptModelAnswer("Commission", 0.45m, false, true, null, fee));        // majority (cap 0.45)
        Assert.Null(SettlementLineClassification.AcceptModelAnswer("Commission", 0.9m, false, true, "local:v2-majority", fee));
        Assert.Null(SettlementLineClassification.AcceptModelAnswer("Sale", 0.95m, true, false, null, fee));              // เครื่องหมายขัด
    }

    [Fact]
    public void killswitch_ปิดAIทุกตัว_ป้ายที่รู้จักยังจัดได้_ที่เหลือเป็นUnclassifiedเงียบ()
    {
        // เหมือน FallbackToLocal ตอนปิด provider: ไม่มีคำตอบ ไม่ใช่นักเรียน ไม่ใช่ครู
        SettlementLineType Classify(string label, decimal amount)
        {
            var local = SettlementLineClassification.ResolveLocal(null, null,
                SettlementLabelSeed.Lookup(label, SettlementChannelKind.Marketplace), amount);
            if (local.Resolved) return local.Type;
            return SettlementLineClassification.AcceptModelAnswer(null, null, usedAi: false, fromLocalModel: false, null, amount)
                   ?? SettlementLineType.Unclassified;
        }
        Assert.Equal(SettlementLineType.Commission, Classify("Commission fee", -53.5m));
        Assert.Equal(SettlementLineType.Sale, Classify("ยอดขาย", 1070m));
        Assert.Equal(SettlementLineType.Unclassified, Classify("Mystery charge", -5m));
    }

    [Fact]
    public void payloadของตัวจัดประเภท_ไม่มียอดเงิน_fingerprintชนกันข้ามยอด()
    {
        var a = SettlementLineClassification.BuildPromptPayload("commission fee", SettlementChannelKind.Marketplace, -53.5m);
        var b = SettlementLineClassification.BuildPromptPayload("commission fee", SettlementChannelKind.Marketplace, -1234.25m);
        Assert.DoesNotContain("53.5", a);
        Assert.Contains("negative", a);
        Assert.Equal(AiMemoryKey.Of(a), AiMemoryKey.Of(b));
        Assert.NotEqual(AiMemoryKey.Of(a), AiMemoryKey.Of(
            SettlementLineClassification.BuildPromptPayload("commission fee", SettlementChannelKind.Marketplace, 10m)));
        Assert.DoesNotContain(SettlementLineType.Unclassified, SettlementLineClassification.ClassifierCandidates);
        Assert.DoesNotContain(SettlementLineType.Adjustment, SettlementLineClassification.ClassifierCandidates);
    }

    // ═════════════════ จับคู่ใบขาย ═════════════════

    private static SettlementMatchCandidate Doc(decimal open, bool canReceive = true, bool refund = true)
        => new(SettlementMatchCandidateKind.Document, Guid.NewGuid(), "TaxInvoice INV-1", open, canReceive, refund);

    [Fact]
    public void จับคู่_ตัวเดียวยอดตรง_Matched_ยอดต่าง_AmountMismatchยังผูกเอกสาร()
    {
        var d = Doc(1070m);
        var ok = SettlementSaleMatch.Decide(SettlementLineType.Sale, "O-1", 1070m, new[] { d });
        Assert.Equal((SettlementMatchStatus.Matched, d.Id), (ok.Status, ok.DocumentId));
        var diff = SettlementSaleMatch.Decide(SettlementLineType.Sale, "O-1", 1000m, new[] { d });
        Assert.Equal((SettlementMatchStatus.AmountMismatch, d.Id), (diff.Status, diff.DocumentId));
    }

    [Fact]
    public void จับคู่_หลายผู้สมัครไม่เดา_และมีร่องรอยแต่รับชำระไม่ได้ต้องไม่ตกใบสรุป()
    {
        var many = SettlementSaleMatch.Decide(SettlementLineType.Sale, "O-1", 1070m, new[] { Doc(1070m), Doc(1070m) });
        Assert.Equal(SettlementMatchStatus.Unmatched, many.Status);
        Assert.Null(many.DocumentId);
        Assert.Equal(2, many.Candidates.Count);
        var receipt = SettlementSaleMatch.Decide(SettlementLineType.Sale, "O-1", 1070m, new[] { Doc(0m, canReceive: false) });
        Assert.Equal(SettlementMatchStatus.Unmatched, receipt.Status);          // ไม่ใช่ AutoSummary = กันรายได้ซ้ำ
        var resv = SettlementSaleMatch.Decide(SettlementLineType.Sale, "B-1", 3000m, new[]
            { new SettlementMatchCandidate(SettlementMatchCandidateKind.Reservation, Guid.NewGuid(), "การจอง R1", null, false, false) });
        Assert.Equal(SettlementMatchStatus.Unmatched, resv.Status);
        Assert.NotNull(resv.ReservationId);
    }

    [Fact]
    public void จับคู่_ไม่มีร่องรอย_ขายเข้าใบสรุป_คืนเงินต้องมีใบเดิม_ค่าธรรมเนียมไม่ต้องจับ()
    {
        Assert.Equal(SettlementMatchStatus.AutoSummary, SettlementSaleMatch.Decide(SettlementLineType.Sale, null, 10m, Array.Empty<SettlementMatchCandidate>()).Status);
        Assert.Equal(SettlementMatchStatus.AutoSummary, SettlementSaleMatch.Decide(SettlementLineType.SellerVoucher, "O-9", -5m, Array.Empty<SettlementMatchCandidate>()).Status);
        Assert.Equal(SettlementMatchStatus.Unmatched, SettlementSaleMatch.Decide(SettlementLineType.Refund, "O-9", 0m, Array.Empty<SettlementMatchCandidate>()).Status);
        Assert.Equal(SettlementMatchStatus.NotRequired, SettlementSaleMatch.Decide(SettlementLineType.Commission, "O-1", 0m, new[] { Doc(1m) }).Status);
        var d = Doc(0m, canReceive: false, refund: true);
        var refund = SettlementSaleMatch.Decide(SettlementLineType.Refund, "O-1", 0m, new[] { d });
        Assert.Equal((SettlementMatchStatus.Matched, d.Id), (refund.Status, refund.DocumentId));
        var pi = Guid.NewGuid();
        Assert.Equal(pi, SettlementSaleMatch.Decide(SettlementLineType.Sale, null, 1m, Array.Empty<SettlementMatchCandidate>(), pi).PaymentIntentId);
        var intentNoRefund = new SettlementMatchCandidate(SettlementMatchCandidateKind.PaymentIntent, Guid.NewGuid(), "x", 100m, true, false);
        Assert.Equal(SettlementMatchStatus.Unmatched, SettlementSaleMatch.Decide(SettlementLineType.Refund, "ch_1", 0m, new[] { intentNoRefund }).Status);
    }

    [Fact]
    public void สถานะรอบโอน_จากบรรทัด_และรอบที่ลงบัญชีแล้วแก้ไม่ได้()
    {
        Assert.Equal(SettlementBatchStatus.Imported, SettlementSaleMatch.DeriveImportStatus(new[]
            { (SettlementLineType.Unclassified, SettlementMatchStatus.Unmatched), (SettlementLineType.Sale, SettlementMatchStatus.Matched) }));
        Assert.Equal(SettlementBatchStatus.Classified, SettlementSaleMatch.DeriveImportStatus(new[]
            { (SettlementLineType.Sale, SettlementMatchStatus.Unmatched), (SettlementLineType.Commission, SettlementMatchStatus.NotRequired) }));
        Assert.Equal(SettlementBatchStatus.Matched, SettlementSaleMatch.DeriveImportStatus(new[]
            { (SettlementLineType.Sale, SettlementMatchStatus.AutoSummary), (SettlementLineType.Commission, SettlementMatchStatus.NotRequired) }));
        Assert.True(SettlementSaleMatch.IsEditable(SettlementBatchStatus.Matched));
        Assert.False(SettlementSaleMatch.IsEditable(SettlementBatchStatus.Posted));
        Assert.False(SettlementSaleMatch.IsEditable(SettlementBatchStatus.Voided));
    }

    // ═════════════════ การจับคู่คอลัมน์ ═════════════════

    [Fact]
    public void การจับคู่คอลัมน์_ตรวจความครบ_และJSONพังล้มดัง()
    {
        Assert.NotEmpty(new SettlementColumnMap { Layout = SettlementFileLayout.Long, Amount = "a" }.Validate());
        Assert.NotEmpty(new SettlementColumnMap { Layout = SettlementFileLayout.Wide, OrderId = "o" }.Validate());
        var badType = new SettlementColumnMap
        {
            Layout = SettlementFileLayout.Wide, OrderId = "o",
            AmountColumns = { new SettlementAmountColumn { Header = "x", Type = "Unclassified" } },
        };
        Assert.NotEmpty(badType.Validate());
        Assert.Throws<SettlementFormatException>(() => SettlementColumnMap.Parse("{not json"));
        var round = SettlementColumnMap.Parse(LongMap)!;
        Assert.Empty(round.Validate());
        Assert.Equal("ประเภทรายการ", round.Type);
    }

    [Fact]
    public void เสนอการจับคู่_แบบยาวและแบบกว้าง_คอลัมน์ผู้ซื้อเข้ารายการไม่ใช้()
    {
        var longS = GenericColumnMapAdapter.Suggest(new[] { "Order ID", "Transaction Type", "Amount", "Buyer Name", "Phone" },
            Array.Empty<IReadOnlyList<string>>(), SettlementChannelKind.Marketplace);
        Assert.Equal(SettlementFileLayout.Long, longS.Map.Layout);
        Assert.Equal("Transaction Type", longS.Map.Type);
        Assert.Contains("Buyer Name", longS.Map.Ignore);
        Assert.Contains("Phone", longS.Map.Ignore);
        Assert.Empty(longS.Issues);

        var wide = GenericColumnMapAdapter.Suggest(
            new[] { "Order ID", "Merchandise Subtotal", "Commission Fee", "Transaction Fee", "Buyer Name", "Note X" },
            new IReadOnlyList<string>[] { new[] { "SO-1", "1,070.00", "-53.50", "-20", "ผู้ซื้อ", "abc" } },
            SettlementChannelKind.Marketplace);
        Assert.Equal(SettlementFileLayout.Wide, wide.Map.Layout);
        Assert.Equal(3, wide.Map.AmountColumns.Count);
        Assert.Contains("Buyer Name", wide.Map.Ignore);
        Assert.Contains("Note X", wide.UnmappedHeaders);             // ไม่รู้จัก = ให้ผู้ใช้ตัดสิน ไม่ใส่ไม่ใช้เอง
    }

    // ═════════════════ ประกอบจาก PaymentIntent ═════════════════

    [Fact]
    public void intentใหม่_ขาย_ค่าธรรมเนียม_คืนเงิน_และคืนภายหลังเฉพาะส่วนต่าง()
    {
        var t0 = new DateTime(2026, 9, 1, 3, 0, 0, DateTimeKind.Utc);
        var a = new SettlementIntentSnapshot(Guid.NewGuid(), "ch_a", 1070m, 100m, 41.79m, 0m, t0, false, 0m);
        var b = new SettlementIntentSnapshot(Guid.NewGuid(), "ch_b", 500m, 0m, null, 0m, t0.AddMinutes(1), false, 0m);
        var late = new SettlementIntentSnapshot(Guid.NewGuid(), "ch_c", 800m, 300m, 10m, 0m, t0.AddMinutes(2), true, 100m);
        var done = new SettlementIntentSnapshot(Guid.NewGuid(), "ch_d", 800m, 300m, 10m, 0m, t0.AddMinutes(3), true, 300m);
        var r = PaymentIntentAdapter.BuildRows(new[] { a, b, late, done }, GatewayFeeVatMode.None);
        Assert.All(r.Rows, x => Assert.NotNull(x.PaymentIntentId));
        Assert.Equal(new[] { 1070m, -41.79m, -100m }, r.Rows.Where(x => x.PaymentIntentId == a.Id).Select(x => x.Amount));
        Assert.Equal(1, r.FeeUnknownCount);                          // b ไม่รู้ค่าธรรมเนียม ⇒ ไม่แต่งตัวเลข
        Assert.Equal(-200m, r.Rows.Single(x => x.PaymentIntentId == late.Id).Amount);
        Assert.DoesNotContain(r.Rows, x => x.PaymentIntentId == done.Id);
        Assert.Equal(new[] { a.Id, b.Id }, r.NewIntentIds);
        Assert.Equal(r.Rows.Count, r.Rows.Select(x => x.RawTxnId).Distinct().Count());
    }

    // ═════════════════ ฝ่ายค้าน R-A1 · R-A2 ═════════════════

    [Fact]
    public void RA1_gatewayที่ผูกconfig_ใช้ผังของresolverเสมอ_หาไม่เจอล้มดัง_ไม่สร้าง1134x()
    {
        var cfg = Guid.NewGuid();
        var std11340 = Guid.NewGuid();
        Assert.Equal(SettlementChannelAccounts.GatewayClearingDecision.UseResolved,
            SettlementChannelAccounts.DecideGatewayClearing(SettlementChannelKind.Gateway, cfg, std11340, true, null));
        Assert.Equal(SettlementChannelAccounts.GatewayClearingDecision.FailResolverMissing,
            SettlementChannelAccounts.DecideGatewayClearing(SettlementChannelKind.Gateway, cfg, null, false, null));
        Assert.Equal(SettlementChannelAccounts.GatewayClearingDecision.FailResolverMissing,
            SettlementChannelAccounts.DecideGatewayClearing(SettlementChannelKind.Gateway, cfg, std11340, false, null));
        Assert.Equal(SettlementChannelAccounts.GatewayClearingDecision.FailMismatch,
            SettlementChannelAccounts.DecideGatewayClearing(SettlementChannelKind.Gateway, cfg, std11340, true, Guid.NewGuid()));
        // ทิศตรงข้าม: marketplace / gateway ที่ไม่ผูก config ⇒ เส้นผังย่อยเดิม
        Assert.Equal(SettlementChannelAccounts.GatewayClearingDecision.NotGateway,
            SettlementChannelAccounts.DecideGatewayClearing(SettlementChannelKind.Marketplace, null, null, false, null));
        Assert.Equal(SettlementChannelAccounts.GatewayClearingDecision.NotGateway,
            SettlementChannelAccounts.DecideGatewayClearing(SettlementChannelKind.Gateway, null, std11340, true, null));
    }

    [Fact]
    public void RA2_ผังพักwallet_11341ถึง11349_ไม่ใช่ลูกหนี้การค้า_11310ยังเป็น()
    {
        var none = Array.Empty<Guid>();
        Assert.False(TradeReceivableAccount.IsTradeReceivableControl(Guid.NewGuid(), "11341", "ลูกหนี้แพลตฟอร์ม ร้านทดสอบ", none));
        Assert.False(TradeReceivableAccount.IsTradeReceivableControl(Guid.NewGuid(), "11349", "ลูกหนี้แพลตฟอร์ม X", none));
        var custom = Guid.NewGuid();
        Assert.False(TradeReceivableAccount.IsTradeReceivableControl(custom, "11399", "ลูกหนี้ wallet ที่ผู้ใช้เลือกเอง", new[] { custom }));
        Assert.True(TradeReceivableAccount.IsTradeReceivableControl(Guid.NewGuid(), "11310", "ลูกหนี้การค้า", none));
        Assert.True(TradeReceivableAccount.IsTradeReceivableControl(Guid.NewGuid(), "11330", "ลูกหนี้การค้า-ต่างประเทศ", none));
    }

    [Fact]
    public void ไฟล์ต้นฉบับของรอบโอน_ด่านอ่านและลบต้องมีคีย์settlement_อัปโหลดจากหน้าเว็บไม่ได้()
    {
        // รอบ 198 ทีม D: เดิม Bank.Reconcile ชั่วคราว → คีย์ settlement จาก SettlementPermissionScope (ตัวเดียวกับ controller)
        var rule = AttachmentPermissionScope.Resolve("SettlementBatch");
        Assert.True(rule.IsKnown);
        Assert.False(rule.ClientUploadAllowed);
        Assert.Contains(PermissionKeys.SettlementImport, rule.ReadAnyOf);
        Assert.Contains(PermissionKeys.SettlementPost, rule.ReadAnyOf);
        Assert.Equal(new[] { PermissionKeys.SettlementImport }, rule.WriteAnyOf);
        // ไฟล์ดิบยังไม่ตัด PII ⇒ แค่ "ดูรอบโอน" ไม่พอ · คีย์ธนาคารเดิมไม่เปิดไฟล์นี้อีก
        Assert.DoesNotContain(PermissionKeys.SettlementView, rule.ReadAnyOf);
        Assert.DoesNotContain(PermissionKeys.BankReconcile, rule.ReadAnyOf);
    }

    [Fact]
    public void คีย์ล็อกนำเข้าต่อช่องทาง_คงที่ข้ามprocess()
    {
        var c = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var ch = Guid.Parse("66666666-7777-8888-9999-000000000000").ToString("N");
        Assert.Equal(AdvisoryLockKey.For(c, AdvisoryLockKey.SettlementImport, ch), AdvisoryLockKey.For(c, AdvisoryLockKey.SettlementImport, ch));
        Assert.NotEqual(AdvisoryLockKey.For(c, AdvisoryLockKey.SettlementImport, ch), AdvisoryLockKey.For(c, AdvisoryLockKey.GatewaySettlement, ch));
    }
}
