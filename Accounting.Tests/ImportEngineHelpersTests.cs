using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 ทีม B — ตัวช่วยของ engine ศูนย์นำเข้า: <see cref="ImportHeaderDetector"/> (แถวหัวตาราง + แถวยอดรวม) · <see cref="ThaiLegalNameKey"/> ·
/// <see cref="ImportRowConfidence"/> · รูปแบบวันที่ของ Smart Import (<c>DateOrderFromFormat</c>) · ทุกกลุ่มล็อกสองทิศ
/// </summary>
public class ImportEngineHelpersTests
{
    private static IReadOnlyList<string> R(params string[] cells) => cells;

    private static readonly IReadOnlyList<ImportFieldDto> ContactFields = new[]
    {
        new ImportFieldDto("Name", "ชื่อ", ImportFieldType.Text, true),
        new ImportFieldDto("TaxId", "เลขผู้เสียภาษี", ImportFieldType.TaxId, false),
        new ImportFieldDto("Phone", "โทรศัพท์", ImportFieldType.Phone, false),
        new ImportFieldDto("Email", "อีเมล", ImportFieldType.Email, false),
    };

    // ═══ แถวยอดรวม ═══

    [Theory]
    [InlineData("รวม")]
    [InlineData("รวมทั้งสิ้น")]
    [InlineData("รวม 15 รายการ")]
    [InlineData("Grand Total:")]
    [InlineData("ยอดยกมา")]
    [InlineData("Sub-total")]
    public void แถวยอดรวม_ถูกจับ(string label)
        => Assert.True(ImportHeaderDetector.IsSummaryRow(R("", label, "", "1,734.50")));

    [Theory]
    [InlineData("ขนมรวมมิตร")]
    [InlineData("รวมมิตร")]
    [InlineData("Total care shampoo")]
    [InlineData("รวมเงินสดย่อย")]
    [InlineData("ค่าเช่า ส.ค.")]
    public void ชื่อที่ขึ้นต้นคล้ายป้ายยอดรวม_ไม่ถูกตัด(string label)
        => Assert.False(ImportHeaderDetector.IsSummaryRow(R("05/08/2569", label, "5210", "100.00")));

    [Fact]
    public void แถวว่าง_ไม่ใช่แถวยอดรวม()
        => Assert.False(ImportHeaderDetector.IsSummaryRow(R("", "", "")));

    // ═══ แถวหัวตาราง ═══

    [Fact]
    public void ข้ามแถวชื่อรายงาน_แล้วเจอหัวตาราง()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            R("รายชื่อลูกค้า ณ 31/12/2568", "", ""),
            R("", "", ""),
            R("ชื่อ", "เลขผู้เสียภาษี", "อีเมล"),
            R("บริษัท เอ จำกัด", "0105556000001", "a@example.com"),
        };
        var det = ImportHeaderDetector.Detect(rows, ContactFields);
        Assert.Equal(2, det.HeaderRowIndex);
        Assert.Equal(1, det.HeaderRowSpan);
        Assert.Equal(new[] { "ชื่อ", "เลขผู้เสียภาษี", "อีเมล" }, det.Headers);
        Assert.True(det.Confidence > 0.5m);
    }

    [Fact]
    public void ทิศตรงข้าม_ไฟล์ที่หัวตารางอยู่แถวแรก_ไม่ถูกเลื่อน()
    {
        var rows = new List<IReadOnlyList<string>> { R("Name", "TaxId", "Email"), R("ร้าน ก", "", "k@example.com") };
        var det = ImportHeaderDetector.Detect(rows, ContactFields);
        Assert.Equal(0, det.HeaderRowIndex);
    }

    [Fact]
    public void หัวสองชั้น_เซลล์ผสาน_รวมเป็นชื่อเดียว()
    {
        var rows = new List<IReadOnlyList<string>>
        {
            R("วันที่", "ยอดเงิน", ""),
            R("", "เดบิต", "เครดิต"),
            R("05/08/2569", "100.00", ""),
        };
        Assert.Equal(new[] { "วันที่", "ยอดเงิน เดบิต", "ยอดเงิน เครดิต" }, ImportHeaderDetector.MergeHeaders(rows, 0, 2));
    }

    [Fact]
    public void ไม่มีชื่อคอลัมน์ที่รู้จัก_เดาแถวข้อความแรกแบบความมั่นใจต่ำ()
    {
        var rows = new List<IReadOnlyList<string>> { R("ก", "ข", "ค"), R("1", "2", "3") };
        var det = ImportHeaderDetector.Detect(rows, ContactFields);
        Assert.Equal(0, det.HeaderRowIndex);
        Assert.True(det.Confidence < 0.5m);
    }

    // ═══ ชื่อนิติบุคคล ═══

    [Fact]
    public void ชื่อนิติบุคคลเดียวกัน_รูปย่อกับรูปเต็ม()
        => Assert.True(ThaiLegalNameKey.SameLegalName("บริษัท เอบีซี จำกัด", "บจก.เอบีซี"));

    [Fact]
    public void ทิศตรงข้าม_คนละรูปนิติบุคคล_ไม่ใช่รายเดียวกัน()
        => Assert.False(ThaiLegalNameKey.SameLegalName("บริษัท เอบีซี จำกัด", "ห้างหุ้นส่วนจำกัด เอบีซี"));

    [Fact]
    public void ชื่อบุคคล_เว้นวรรคต่างกัน_เท่ากัน_แต่ชื่อต่างกันไม่เท่า()
    {
        Assert.True(ThaiLegalNameKey.SameLegalName("ร้านสมใจ", "ร้าน สมใจ"));
        Assert.False(ThaiLegalNameKey.SameLegalName("ร้านสมใจ", "ร้านสมใจ 2"));
        Assert.Equal("", ThaiLegalNameKey.Key("  "));
    }

    // ═══ คะแนนแถว ═══

    [Fact]
    public void คะแนนแถว_ไม่มีปัญหา_เท่าความมั่นใจของคอลัมน์()
        => Assert.Equal(0.97m, ImportRowConfidence.Compute(0.97m, Array.Empty<ImportIssueDto>()));

    [Fact]
    public void คะแนนแถว_มีข้อผิดพลาด_ไม่เกิน_0_3_และคำเตือนหัก()
    {
        var err = new ImportIssueDto("Name", "required-missing", ImportIssueSeverity.Error, "x");
        var warn = new ImportIssueDto("Date", "date-order-assumed", ImportIssueSeverity.Warning, "y");
        Assert.Equal(0.3m, ImportRowConfidence.Compute(1m, new[] { err }));
        Assert.Equal(0.9m, ImportRowConfidence.Compute(1m, new[] { warn }));
        Assert.True(ImportRowConfidence.BelowReviewThreshold(ImportRowConfidence.Compute(1m, new[] { warn }, matchScore: 0.8m)));
        Assert.False(ImportRowConfidence.BelowReviewThreshold(0.9m));
    }

    // ═══ รูปแบบวันที่ของ Smart Import (เดิมรับแล้วไม่ใช้) ═══

    [Theory]
    [InlineData("MM/dd/yyyy", ImportDateOrder.MonthDayYear)]
    [InlineData("dd/MM/yyyy", ImportDateOrder.DayMonthYear)]
    [InlineData("yyyy-MM-dd", ImportDateOrder.Auto)]
    [InlineData(null, ImportDateOrder.Auto)]
    public void รูปแบบวันที่ที่ผู้ใช้เลือก_แปลงเป็นลำดับวันเดือน(string? format, ImportDateOrder expected)
        => Assert.Equal(expected, ImportExportService.DateOrderFromFormat(format));
}
