using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ไฟล์ .txt สำหรับนำเข้าเว็บสรรพากร (ภ.ง.ด.3/53/54) — เทสต์ format ตามช่องที่
/// หน้า import ของ RD map จริง (badge Col5-Col10 จาก screenshot ผู้ใช้)
/// </summary>
public class PndTextFileFormatTests
{
    private static PndTextFileFormat.Row JuristicRow() => new(
        PayeeTaxId: "0105512345678", BranchCode: "00000",
        PayeeName: "บริษัท เน็ก แอค จำกัด", IsJuristic: true,
        PayDate: new DateTime(2026, 7, 1), IncomeTypeCode: "6",
        IncomeAmount: 15000m, TaxRate: 3m, TaxAmount: 450m);

    [Fact]
    public void No_header_or_trailer_rows()
    {
        var body = PndTextFileFormat.Build(new[] { JuristicRow(), JuristicRow() });
        var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);                       // detail ล้วน
        Assert.DoesNotContain(lines, l => l.StartsWith("H|"));
        Assert.DoesNotContain(lines, l => l.StartsWith("T|"));
        Assert.DoesNotContain(lines, l => l.StartsWith("D|"));
    }

    [Fact]
    public void Column_positions_match_rd_import_page()
    {
        var c = PndTextFileFormat.DetailRow(1, JuristicRow()).Split('|');
        Assert.Equal(12, c.Length);
        Assert.Equal("1", c[0]);                    // Col1 ลำดับ
        Assert.Equal("0105512345678", c[1]);        // Col2 เลขผู้เสียภาษี 13 หลัก
        Assert.Equal("00000", c[2]);                // Col3 สาขา
        Assert.Equal("บริษัท เน็ก แอค จำกัด", c[3]); // Col4 ชื่อ
        Assert.Equal("", c[4]);                     // Col5 ชื่อสกุล — นิติบุคคลว่าง
        Assert.Equal("01072026", c[5]);             // Col6 ddMMyyyy ค.ศ. ไม่มีตัวคั่น
        Assert.Equal("6", c[6]);                    // Col7 ประเภทเงินได้
        Assert.Equal("15000.00", c[7]);             // Col8 เงินได้ (15,2)
        Assert.Equal("3.00", c[8]);                 // Col9 อัตรา (4,2)
        Assert.Equal("450.00", c[9]);               // Col10 ภาษีที่หัก (15,2)
        Assert.Equal("1", c[10]);                   // Col11 เงื่อนไข = หัก ณ ที่จ่าย
        Assert.Equal("บริษัท", c[11]);              // Col12 คำนำหน้า (ข้อความไทย)
    }

    // ───────── Col12 คำนำหน้าชื่อ — ล็อกสองทิศ ─────────

    [Fact]
    public void Col12_uses_confirmed_title_over_guessing()
    {
        // ผู้ใช้กรอกคำนำหน้าไว้ในทะเบียนผู้ติดต่อ → ต้องชนะการเดาจากชื่อเสมอ
        var row = JuristicRow() with
        {
            PayeeName = "สมชาย ใจดี", IsJuristic = false,
            PayeeTaxId = "1234567890123", PayeeTitle = "นาย",
        };
        var c = PndTextFileFormat.DetailRow(1, row).Split('|');
        Assert.Equal("นาย", c[11]);
        Assert.Equal("สมชาย", c[3]);
        Assert.Equal("ใจดี", c[4]);
    }

    [Fact]
    public void Col12_falls_back_to_guessing_for_legacy_rows()
    {
        // ข้อมูลเก่ายังพิมพ์คำนำหน้าติดมากับชื่อ — ต้องยังได้ Col12 ไม่ใช่ช่องว่าง
        var row = JuristicRow() with
        {
            PayeeName = "น.ส.นิภาพร โชติปรีดาศิริกุล", IsJuristic = false,
            PayeeTaxId = "1234567890123",
        };
        var c = PndTextFileFormat.DetailRow(1, row).Split('|');
        Assert.Equal("นางสาว", c[11]);          // คืนรูปเต็มเสมอ ไม่ใช่ "น.ส."
        Assert.Equal("นิภาพร", c[3]);
    }

    [Fact]
    public void Col12_empty_when_no_title_anywhere()
    {
        var row = JuristicRow() with
        {
            PayeeName = "สมชาย ใจดี", IsJuristic = false, PayeeTaxId = "1234567890123",
        };
        Assert.Equal("", PndTextFileFormat.DetailRow(1, row).Split('|')[11]);
    }

    [Theory]
    [InlineData("เด็กชาย สมชาย ใจดี", "เด็กชาย", "สมชาย", "ใจดี")]
    [InlineData("ด.ญ. สมหญิง ใจงาม", "เด็กหญิง", "สมหญิง", "ใจงาม")]
    public void Minor_titles_are_recognised(string full, string title, string first, string last)
    {
        // เดิมลิสต์ของ PndTextFileFormat ไม่มี "เด็กชาย/เด็กหญิง" ⇒ ผู้ถูกหักที่เป็น
        // ผู้เยาว์ (ค่าเช่า/มรดก/นักแสดงเด็ก) ได้ Col4="เด็กชาย" Col5="สมชาย ใจดี"
        var row = JuristicRow() with
        {
            PayeeName = full, IsJuristic = false, PayeeTaxId = "1234567890123",
        };
        var c = PndTextFileFormat.DetailRow(1, row).Split('|');
        Assert.Equal(title, c[11]);
        Assert.Equal(first, c[3]);
        Assert.Equal(last, c[4]);
    }

    [Theory]
    [InlineData("นายช่างการไฟฟ้า")]
    [InlineData("นางเลิ้งพาณิชย์")]
    [InlineData("นายหน้าประกันภัย")]
    public void Shop_names_that_look_like_titles_are_never_split(string shopName)
    {
        // ทิศตรงข้าม: ชื่อกิจการที่บังเอิญขึ้นต้นเหมือนคำนำหน้า ห้ามถูกตัด
        // (ถ้าตัด ชื่อบนไฟล์ยื่น ภ.ง.ด. จะกลายเป็น "ช่างการไฟฟ้า")
        var row = JuristicRow() with
        {
            PayeeName = shopName, IsJuristic = false, PayeeTaxId = "1234567890123",
        };
        var c = PndTextFileFormat.DetailRow(1, row).Split('|');
        Assert.Equal("", c[11]);
        Assert.Equal(shopName, c[3]);
        Assert.Equal("", c[4]);
    }

    [Fact]
    public void Col1_to_Col11_are_unchanged_by_the_new_column()
    {
        // ผู้ใช้ที่บันทึก column-mapping ไว้บนเว็บ RD ต้องไม่ต้อง map ใหม่ —
        // คอลัมน์ใหม่ต่อท้าย ไม่เลื่อนของเดิมแม้แต่ช่องเดียว
        var withTitle = PndTextFileFormat.DetailRow(1, JuristicRow() with { PayeeTitle = "บริษัท" });
        var without  = PndTextFileFormat.DetailRow(1, JuristicRow() with { PayeeTitle = null });
        Assert.Equal(
            string.Join("|", withTitle.Split('|')[..11]),
            string.Join("|", without.Split('|')[..11]));
    }

    [Fact]
    public void Individual_name_splits_into_first_and_last()
    {
        var row = JuristicRow() with
        {
            PayeeName = "นายพงศ์พิทักษ์ ผีลา",
            IsJuristic = false,
            PayeeTaxId = "1234567890123",
        };
        var c = PndTextFileFormat.DetailRow(1, row).Split('|');
        Assert.Equal("พงศ์พิทักษ์", c[3]);   // คำนำหน้าถูกตัด
        Assert.Equal("ผีลา", c[4]);
    }

    [Theory]
    [InlineData("นางสาว อมันดา ปิยภากุลเจริญ", "อมันดา", "ปิยภากุลเจริญ")]
    [InlineData("น.ส.นิภาพร โชติปรีดาศิริกุล", "นิภาพร", "โชติปรีดาศิริกุล")]
    [InlineData("สมชาย", "สมชาย", "")]            // ไม่มีนามสกุล → Col5 ว่าง
    public void Title_prefixes_are_stripped(string full, string first, string last)
    {
        var (f, l) = PndTextFileFormat.SplitName(full, isJuristic: false);
        Assert.Equal(first, f);
        Assert.Equal(last, l);
    }

    [Fact]
    public void Juristic_name_never_splits()
    {
        // ชื่อบริษัทมีช่องว่างเยอะ — ห้ามตัดครึ่งไปใส่ช่องนามสกุล
        var (f, l) = PndTextFileFormat.SplitName("บริษัท สกอลาร์ แอคเคาท์ติ้ง จำกัด", isJuristic: true);
        Assert.Equal("บริษัท สกอลาร์ แอคเคาท์ติ้ง จำกัด", f);
        Assert.Equal("", l);
    }

    [Fact]
    public void TaxId_and_branch_are_normalized()
    {
        var row = JuristicRow() with { PayeeTaxId = "0-1055-12345-67-8", BranchCode = "12" };
        var c = PndTextFileFormat.DetailRow(1, row).Split('|');
        Assert.Equal("0105512345678", c[1]);   // ขีดถูกตัด
        Assert.Equal("00012", c[2]);           // pad 5 หลัก
    }

    [Fact]
    public void Missing_taxid_pads_to_thirteen_zeros()
    {
        var c = PndTextFileFormat.DetailRow(1, JuristicRow() with { PayeeTaxId = null }).Split('|');
        Assert.Equal("0000000000000", c[1]);
        Assert.Equal(13, c[1].Length);
    }

    [Fact]
    public void Amounts_always_two_decimals_no_separator()
    {
        var row = JuristicRow() with
        {
            IncomeAmount = 1234567.891m, TaxRate = 1.5m, TaxAmount = 37037.0367m,
        };
        var c = PndTextFileFormat.DetailRow(1, row).Split('|');
        Assert.Equal("1234567.89", c[7]);       // ไม่มี comma
        Assert.Equal("1.50", c[8]);
        Assert.Equal("37037.04", c[9]);         // AwayFromZero
    }

    [Fact]
    public void Pipe_in_name_cannot_break_the_layout()
    {
        var row = JuristicRow() with { PayeeName = "บริษัท A|B จำกัด" };
        var c = PndTextFileFormat.DetailRow(1, row).Split('|');
        Assert.Equal(11, c.Length);             // ยังเป็น 11 ช่อง
    }

    [Fact]
    public void Condition_falls_back_to_withhold_when_out_of_range()
    {
        var c = PndTextFileFormat.DetailRow(1, JuristicRow() with { Condition = 9 }).Split('|');
        Assert.Equal("1", c[10]);
        var c2 = PndTextFileFormat.DetailRow(1, JuristicRow() with { Condition = 2 }).Split('|');
        Assert.Equal("2", c2[10]);              // ออกให้ตลอดไป
    }

    [Fact]
    public void Sequence_increments_per_row()
    {
        var body = PndTextFileFormat.Build(new[] { JuristicRow(), JuristicRow(), JuristicRow() });
        var lines = body.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("1", lines[0].Split('|')[0]);
        Assert.Equal("2", lines[1].Split('|')[0]);
        Assert.Equal("3", lines[2].Split('|')[0]);
    }
}
