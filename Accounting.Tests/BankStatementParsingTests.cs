using System.Text;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Helpers;
using MiniExcelLibs;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวแยกไฟล์ statement ธนาคาร (รอบ 205 ทีม C · IMPORT_ETL_REVIEW §1 แถว 6–7) — กระดาษรูปจริง: CSV แบบ KBank หัวไทย วันที่ พ.ศ.
/// เข้ารหัส TIS-620 · UTF-8 ต้องไม่ถูกอ่านเป็น TIS-620 · statement ไม่มียอดคงเหลือ · OFX 1.x (SGML) / 2.x (XML) · QIF ล้มดัง
///
/// <para>ทั้งสองทิศ: ไฟล์ที่เคยพัง (TIS-620 กลายเป็น U+FFFD · ไม่มียอดคงเหลือ = "ไม่พบหัวคอลัมน์" · OFX ไม่บันทึกจริง) อ่านได้แล้ว
/// และไฟล์ที่ถูกอยู่แล้ว (UTF-8 · ร้าน "รวมมิตร") ไม่ถูกแตะ — เส้นที่แตะฐานข้อมูลอยู่ใน <c>Db/BankStatementImportDbTests</c></para>
/// </summary>
public class BankStatementParsingTests
{
    internal const string KBankCsv =
        "รายการเดินบัญชี ธนาคารกสิกรไทย\r\n" +
        "เลขที่บัญชี,123-4-56789-0\r\n" +
        "วันที่,เวลา,รายการ,ถอนเงิน,ฝากเงิน,ยอดคงเหลือ,ช่องทาง,รายละเอียด\r\n" +
        ",,ยอดยกมา,,,\"10,000.00\",,\r\n" +
        "01/08/2569,08:15,รับโอนเงิน,,\"1,500.00\",\"11,500.00\",K PLUS,จาก นาย สมชาย ใจดี\r\n" +
        "02/08/2569,12:30,ชำระค่าสินค้า,\"2,000.00\",,\"9,500.00\",K PLUS,ร้านรวมมิตร\r\n" +
        "15/08/2569,09:00,ค่าธรรมเนียม,15.00,,\"9,485.00\",สาขา,\r\n" +
        ",,รวมรายการ,\"2,015.00\",\"1,500.00\",,,\r\n";

    internal const string NoBalanceCsv =
        "วันที่,รายการ,ถอน,ฝาก\n" +
        "05/08/2026,ค่าบริการรายเดือน,100.00,\n" +
        "06/08/2026,รับชำระจากลูกค้า,,\"2,500.00\"\n";

    internal const string Ofx1 =
        "OFXHEADER:100\r\nDATA:OFXSGML\r\nVERSION:102\r\nSECURITY:NONE\r\nENCODING:USASCII\r\nCHARSET:1252\r\nCOMPRESSION:NONE\r\n" +
        "OLDFILEUID:NONE\r\nNEWFILEUID:NONE\r\n\r\n" +
        "<OFX>\r\n<SIGNONMSGSRSV1>\r\n<SONRS>\r\n<STATUS>\r\n<CODE>0\r\n<SEVERITY>INFO\r\n</STATUS>\r\n<DTSERVER>20260831120000[+7:ICT]\r\n" +
        "<LANGUAGE>THA\r\n</SONRS>\r\n</SIGNONMSGSRSV1>\r\n<BANKMSGSRSV1>\r\n<STMTTRNRS>\r\n<TRNUID>1\r\n<STATUS>\r\n<CODE>0\r\n<SEVERITY>INFO\r\n</STATUS>\r\n" +
        "<STMTRS>\r\n<CURDEF>THB\r\n<BANKACCTFROM>\r\n<BANKID>004\r\n<ACCTID>1234567890\r\n<ACCTTYPE>SAVINGS\r\n</BANKACCTFROM>\r\n" +
        "<BANKTRANLIST>\r\n<DTSTART>20260801\r\n<DTEND>20260831\r\n" +
        "<STMTTRN>\r\n<TRNTYPE>CREDIT\r\n<DTPOSTED>20260801083000[+7:ICT]\r\n<TRNAMT>1500.00\r\n<FITID>KB20260801001\r\n<NAME>SOMCHAI JAIDEE\r\n<MEMO>TRANSFER IN\r\n</STMTTRN>\r\n" +
        "<STMTTRN>\r\n<TRNTYPE>DEBIT\r\n<DTPOSTED>20260802053000[0:GMT]\r\n<TRNAMT>-2000.00\r\n<FITID>KB20260802001\r\n<NAME>RUAMMIT SHOP &amp; CO\r\n</STMTTRN>\r\n" +
        "<STMTTRN>\r\n<TRNTYPE>FEE\r\n<DTPOSTED>20260815\r\n<TRNAMT>-15.00\r\n<FITID>KB20260815001\r\n<NAME>FEE\r\n</STMTTRN>\r\n" +
        "</BANKTRANLIST>\r\n<LEDGERBAL>\r\n<BALAMT>9485.00\r\n<DTASOF>20260831\r\n</LEDGERBAL>\r\n</STMTRS>\r\n</STMTTRNRS>\r\n</BANKMSGSRSV1>\r\n</OFX>\r\n";

    internal const string Ofx2 =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"no\"?>\n" +
        "<?OFX OFXHEADER=\"200\" VERSION=\"220\" SECURITY=\"NONE\" OLDFILEUID=\"NONE\" NEWFILEUID=\"NONE\"?>\n" +
        "<OFX>\n <BANKMSGSRSV1>\n  <STMTTRNRS>\n   <TRNUID>1</TRNUID>\n   <STATUS><CODE>0</CODE><SEVERITY>INFO</SEVERITY></STATUS>\n" +
        "   <STMTRS>\n    <CURDEF>THB</CURDEF>\n" +
        "    <BANKACCTFROM><BANKID>014</BANKID><ACCTID>987-6-54321-0</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM>\n" +
        "    <BANKTRANLIST><DTSTART>20260901</DTSTART><DTEND>20260930</DTEND>\n" +
        "     <STMTTRN><TRNTYPE>CREDIT</TRNTYPE><DTPOSTED>20260903100000.000[+7:ICT]</DTPOSTED><TRNAMT>25000.50</TRNAMT>" +
        "<FITID>SCB-0001</FITID><NAME>บริษัท ลูกค้าดี จำกัด</NAME><MEMO>ชำระใบแจ้งหนี้ INV-001</MEMO></STMTTRN>\n" +
        "     <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20260905</DTPOSTED><TRNAMT>-1250,75</TRNAMT><FITID>SCB-0002</FITID>" +
        "<NAME>ค่าเช่าสำนักงาน</NAME></STMTTRN>\n" +
        "    </BANKTRANLIST>\n    <LEDGERBAL><BALAMT>48749.75</BALAMT><DTASOF>20260930235959[+7:ICT]</DTASOF></LEDGERBAL>\n" +
        "   </STMTRS>\n  </STMTTRNRS>\n </BANKMSGSRSV1>\n</OFX>\n";

    internal static byte[] Tis620(string s) => CodePagesEncodingProvider.Instance.GetEncoding(874)!.GetBytes(s);

    // ═══════════════════════ CSV: encoding ═══════════════════════

    [Fact]
    public void Csv_TIS620_หัวไทย_วันที่พศ_อ่านได้ครบ_ตัดยอดยกมาและแถวรวม()
    {
        var parsed = BankStatementFileReader.Read("CSV", "kbank.csv", Tis620(KBankCsv));

        Assert.Equal("windows-874", parsed.Encoding);
        Assert.Equal(3, parsed.Rows.Count);
        Assert.DoesNotContain(parsed.Rows, r => r.Description.Contains('\uFFFD'));
        Assert.Contains(parsed.Notes, n => n.Contains("TIS-620"));

        var first = parsed.Rows[0];
        Assert.Equal(new DateTime(2026, 8, 1, 8, 15, 0), first.Date);
        Assert.Equal(1500m, first.Deposit);
        Assert.Equal(11500m, first.Balance);
        Assert.Equal("รับโอนเงิน | จาก นาย สมชาย ใจดี", first.Description);
        Assert.Equal("K PLUS", first.Channel);

        // "ร้านรวมมิตร" ไม่ใช่แถวสรุป (คำว่า "รวม" ในชื่อร้าน) — ทิศตรงข้ามของตัวตัดแถวสรุป
        Assert.Equal("ชำระค่าสินค้า | ร้านรวมมิตร", parsed.Rows[1].Description);
        Assert.Equal(2000m, parsed.Rows[1].Withdrawal);
        Assert.Equal(new DateTime(2026, 8, 15, 9, 0, 0), parsed.Rows[2].Date);
    }

    [Fact]
    public void Csv_UTF8_ไม่ถูกอ่านเป็นTIS620()
    {
        var parsed = BankStatementFileReader.Read("CSV", "kbank.csv", Encoding.UTF8.GetBytes(KBankCsv));

        Assert.Equal("utf-8", parsed.Encoding);
        Assert.DoesNotContain(parsed.Notes, n => n.Contains("TIS-620"));
        Assert.Equal(3, parsed.Rows.Count);
        Assert.Equal("รับโอนเงิน | จาก นาย สมชาย ใจดี", parsed.Rows[0].Description);
    }

    [Fact]
    public void Csv_ไม่มีคอลัมน์ยอดคงเหลือ_อ่านได้_ยอดเป็นnull()
    {
        var parsed = BankStatementFileReader.Read("CSV", "no-balance.csv", Encoding.UTF8.GetBytes(NoBalanceCsv));

        Assert.Equal(2, parsed.Rows.Count);
        Assert.All(parsed.Rows, r => Assert.Null(r.Balance));
        Assert.Equal(100m, parsed.Rows[0].Withdrawal);
        Assert.Equal(2500m, parsed.Rows[1].Deposit);
    }

    [Fact]
    public void Csv_ชอบแถวหัวที่มียอดคงเหลือ_ไม่เลือกแถวคำอธิบายด้านบน()
    {
        var rows = new List<List<string>>
        {
            new() { "วันที่พิมพ์ 31/08/2569", "จำนวนเงินรวม" },
            new() { "วันที่", "รายการ", "ถอน", "ฝาก", "ยอดคงเหลือ" },
            new() { "01/08/2569", "ฝากเงินสด", "", "500.00", "500.00" },
        };
        var (idx, map) = BankCsvParser.DetectHeader(rows);
        Assert.Equal(1, idx);
        Assert.Equal(4, map.BalanceCol);
    }

    // ═══════════════════════ ยอดเงิน: วงเล็บ / ลบท้าย / CR-DR ═══════════════════════

    [Theory]
    [InlineData("1,250.00", 1250.00)]
    [InlineData("(1,250.00)", -1250.00)]
    [InlineData("1,250.00-", -1250.00)]
    [InlineData("1,250.00 CR", 1250.00)]
    [InlineData("1,250.00DR", -1250.00)]
    [InlineData("DR 500", -500)]
    [InlineData("๑,๒๕๐.๐๐", 1250.00)]
    public void SignedAmount_อ่านเครื่องหมายของธนาคาร(string raw, double expected)
    {
        var v = BankCsvParser.SignedAmount(raw);
        Assert.True(v.Ok);
        Assert.Equal((decimal)expected, v.Value);
    }

    [Theory]
    [InlineData("(500.00) CR")]
    [InlineData("-500.00 DR")]
    [InlineData("ห้าร้อย")]
    public void SignedAmount_อ่านไม่ได้หรือเครื่องหมายขัดกัน_ไม่เดา(string raw)
    {
        var v = BankCsvParser.SignedAmount(raw);
        Assert.False(v.Ok);
        Assert.Null(v.Value);
    }

    [Fact]
    public void Csv_ช่องจำนวนเงินเดียวแบบDR_เป็นเงินออก_ไม่ใช่เงินเข้า()
    {
        var csv = "Date,Description,Amount,Balance\n01/08/2026,ATM WITHDRAWAL,500.00 DR,9500.00\n02/08/2026,SALARY,\"30,000.00 CR\",39500.00\n";
        var parsed = BankStatementFileReader.Read("CSV", "x.csv", Encoding.UTF8.GetBytes(csv));
        Assert.Equal(500m, parsed.Rows[0].Withdrawal);
        Assert.Equal(0m, parsed.Rows[0].Deposit);
        Assert.Equal(30000m, parsed.Rows[1].Deposit);
    }

    // ═══════════════════════ OFX ═══════════════════════

    [Fact]
    public void Ofx1_SGML_อ่านรายการ_วันที่_เครื่องหมาย_FITID_ยอดปิด()
    {
        var r = BankOfxParser.Parse(Encoding.ASCII.GetBytes(Ofx1));

        Assert.Equal("1.x", r.Version);
        Assert.Equal("1234567890", r.AccountId);
        Assert.Equal("THB", r.Currency);
        Assert.Equal(9485m, r.LedgerBalance);
        Assert.Equal(new DateTime(2026, 8, 31), r.LedgerBalanceDate);
        Assert.Equal(3, r.Rows.Count);

        Assert.Equal(new DateTime(2026, 8, 1, 8, 30, 0), r.Rows[0].Date);
        Assert.Equal(1500m, r.Rows[0].Deposit);
        Assert.Equal("KB20260801001", r.Rows[0].ExternalId);
        Assert.Equal("SOMCHAI JAIDEE | TRANSFER IN", r.Rows[0].Description);
        Assert.Equal("SOMCHAI JAIDEE", r.Rows[0].Payee);

        // 05:30 GMT = 12:30 เวลาไทยวันเดียวกัน · &amp; ถอดเป็น &
        Assert.Equal(new DateTime(2026, 8, 2, 12, 30, 0), r.Rows[1].Date);
        Assert.Equal(2000m, r.Rows[1].Withdrawal);
        Assert.Equal(0m, r.Rows[1].Deposit);
        Assert.Equal("RUAMMIT SHOP & CO", r.Rows[1].Description);

        Assert.Equal(new DateTime(2026, 8, 15), r.Rows[2].Date);
        Assert.Equal(15m, r.Rows[2].Withdrawal);
        Assert.Empty(r.Skipped);
    }

    [Fact]
    public void Ofx2_XML_ภาษาไทย_ทศนิยมจุลภาค_อ่านได้()
    {
        var r = BankOfxParser.Parse(Encoding.UTF8.GetBytes(Ofx2));

        Assert.Equal("2.x", r.Version);
        Assert.Equal("987-6-54321-0", r.AccountId);
        Assert.Equal(2, r.Rows.Count);
        Assert.Equal(new DateTime(2026, 9, 3, 10, 0, 0), r.Rows[0].Date);
        Assert.Equal(25000.50m, r.Rows[0].Deposit);
        Assert.Equal("บริษัท ลูกค้าดี จำกัด | ชำระใบแจ้งหนี้ INV-001", r.Rows[0].Description);
        Assert.Equal(1250.75m, r.Rows[1].Withdrawal);
        Assert.Equal("SCB-0002", r.Rows[1].ExternalId);
        Assert.Equal(48749.75m, r.LedgerBalance);
    }

    [Fact]
    public void Ofx_ไฟล์มีหลายบัญชี_ล้มดังพร้อมทางไปต่อ()
    {
        var two = Ofx1.Replace("</STMTRS>", "</STMTRS>\r\n<STMTRS>\r\n<BANKACCTFROM>\r\n<ACCTID>5555555555\r\n</BANKACCTFROM>\r\n</STMTRS>");
        var ex = Assert.Throws<ImportFormatException>(() => BankOfxParser.Parse(Encoding.ASCII.GetBytes(two)));
        Assert.Equal("ofx-multi-account", ex.Code);
        Assert.Contains("ทีละบัญชี", ex.Message);
    }

    [Fact]
    public void Ofx_ผ่านตัวเลือกตัวแยก_ตัดสินจากเนื้อไฟล์แม้ผู้ใช้เลือกCSV()
    {
        var parsed = BankStatementFileReader.Read("CSV", "statement.csv", Encoding.ASCII.GetBytes(Ofx1));
        Assert.Equal(ImportFileKind.Ofx, parsed.Kind);
        Assert.Equal(3, parsed.Rows.Count);
        Assert.Equal(9485m, parsed.ClosingBalance);
        Assert.Equal("1234567890", parsed.SourceAccountNumber);
        // ไฟล์ต้นทาง OFX เก็บเป็น .txt (ระบบไฟล์แนบรับ) ไม่ใช่ทิ้ง
        var src = BankStatementFileReader.SourceFile(parsed, "statement.ofx", Encoding.ASCII.GetBytes(Ofx1));
        Assert.EndsWith(".txt", src.FileName);
    }

    // ═══════════════════════ Excel หลายชีต ═══════════════════════

    private static byte[] Workbook()
    {
        var cover = new List<Dictionary<string, object>> { new() { ["หัวข้อ"] = "statement เดือนสิงหาคม 2569", ["ค่า"] = "ธนาคารกสิกรไทย" } };
        var lines = new List<Dictionary<string, object>>
        {
            new() { ["วันที่"] = "01/08/2569", ["รายการ"] = "รับโอนเงิน", ["ถอนเงิน"] = "", ["ฝากเงิน"] = 1500m, ["ยอดคงเหลือ"] = 11500m },
            new() { ["วันที่"] = "02/08/2569", ["รายการ"] = "ชำระค่าสินค้า", ["ถอนเงิน"] = 2000m, ["ฝากเงิน"] = "", ["ยอดคงเหลือ"] = 9500m },
        };
        using var ms = new MemoryStream();
        MiniExcel.SaveAs(ms, new Dictionary<string, object> { ["สรุป"] = cover, ["รายการ"] = lines }, excelType: ExcelType.XLSX);
        return ms.ToArray();
    }

    [Fact]
    public void Excel_หลายชีต_ไม่ระบุ_ได้ชีตแรกที่มีหัวตารางstatement_และบอกผู้ใช้()
    {
        var parsed = BankStatementFileReader.Read("EXCEL", "kbank.xlsx", Workbook());
        Assert.Equal("รายการ", parsed.SheetName);
        Assert.Equal(new[] { "สรุป", "รายการ" }, parsed.SheetNames);
        Assert.Equal(2, parsed.Rows.Count);
        Assert.Equal(new DateTime(2026, 8, 1), parsed.Rows[0].Date);
        Assert.Equal(9500m, parsed.Rows[1].Balance);
        Assert.Contains(parsed.Notes, n => n.Contains("\"รายการ\""));
    }

    [Fact]
    public void Excel_ระบุชีตที่ไม่มี_ล้มดังพร้อมรายชื่อชีต()
    {
        var ex = Assert.Throws<ImportFormatException>(() => BankStatementFileReader.Read("EXCEL", "kbank.xlsx", Workbook(), "Sheet9"));
        Assert.Equal("sheet-not-found", ex.Code);
        Assert.Contains("สรุป", ex.Message);
    }

    // ═══════════════════════ ล้มดังพร้อมทางไปต่อ ═══════════════════════

    [Fact]
    public void Qif_ล้มดังด้วยข้อความคำตัดสิน151()
    {
        var qif = Encoding.ASCII.GetBytes("!Type:Bank\nD08/01/2026\nT1500.00\nPSomchai\n^\n");
        var ex = Assert.Throws<ImportFormatException>(() => BankStatementFileReader.Read("CSV", "x.qif", qif));
        Assert.Equal("qif-unsupported", ex.Code);
        Assert.Equal(BankStatementFileReader.QifMessage, ex.Message);
        Assert.Contains("CSV หรือ OFX", ex.Message);

        // ผู้ใช้เลือก "QIF" เอง — ข้อความเดียวกัน แม้เนื้อไฟล์จะเป็นอะไรก็ตาม
        var ex2 = Assert.Throws<ImportFormatException>(() => BankStatementFileReader.Read("QIF", "x.txt", qif));
        Assert.Equal("qif-unsupported", ex2.Code);
    }

    [Fact]
    public void Xls_BIFF8_ล้มดังให้บันทึกเป็นxlsx()
    {
        var biff = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };
        var ex = Assert.Throws<ImportFormatException>(() => BankStatementFileReader.Read("EXCEL", "old.xls", biff));
        Assert.Equal("xls-legacy", ex.Code);
        Assert.Contains(".xlsx", ex.Message);
    }

    [Fact]
    public void ไฟล์ที่ไม่ใช่UTF8หรือTIS620_ล้มดัง_ไม่ใช่อักขระเสียเงียบ()
    {
        // 0xFC–0xFF ไม่มีในตาราง Windows-874 และไม่ใช่ UTF-8 ที่ถูกต้อง
        var junk = Encoding.ASCII.GetBytes("Date,Amount,Balance\n01/08/2026,").Concat(new byte[] { 0xFC, 0xFD, 0xFE }).ToArray();
        var ex = Assert.Throws<ImportFormatException>(() => BankStatementFileReader.Read("CSV", "x.csv", junk));
        Assert.Equal("encoding", ex.Code);
    }
}
