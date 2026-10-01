using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม IN — A-IN6 (สีบริษัทตรวจรูปด้วยตัวตรวจเดียวกับเทมเพลต · ค่าเก่าอ่านเป็นค่าปลอดภัย) +
/// A-IN7 (เปลี่ยนประเภทธุรกิจภายหลัง ⇒ เติมประเภทเงินมัดจำเริ่มต้นของธุรกิจใหม่ · ไม่ลบของเดิม)
/// </summary>
public class CompanySettingsInRound201Tests
{
    // ── A-IN6 ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("#4f46e5")]
    [InlineData("#4F46E5")]
    [InlineData("0EA5E9")]
    [InlineData("#abc")]
    [InlineData("")]        // ว่าง = ล้างค่า
    [InlineData(null)]      // ไม่ส่ง = ไม่แก้
    public void สีถูกรูปหรือว่าง_รับได้(string? raw)
        => Assert.Null(DocumentTemplateStyle.ColorRejectReason(raw, "สีหลัก"));

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#4472C4;background:url(x)")]
    [InlineData("expression(alert(1))")]
    public void สีผิดรูป_ปฏิเสธพร้อมชื่อช่องไทย(string raw)
    {
        var why = DocumentTemplateStyle.ColorRejectReason(raw, "สีหลัก");
        Assert.NotNull(why);
        Assert.StartsWith("สีหลัก", why);
    }

    [Fact]
    public void สีที่เก็บก่อนรอบนี้ผิดรูป_อ่านออกเป็นnull_ไม่ไหลเข้าเอกสาร()
    {
        Assert.Null(DocumentTemplateStyle.Hex("#4472C4;}</style><script>"));
        var id = DocumentIssuerIdentity.Resolve(
            DocumentType.Invoice, null, false,
            "บริษัท ทดสอบ จำกัด", null, "0105500000000", null,
            "กรุงเทพฯ", null, null, null, null, "red;}</style>", null);
        Assert.Null(id.PrimaryColor);
    }

    [Fact]
    public void สีบริษัทที่ถูกรูป_ยังไหลเข้าเอกสาร_เป็นรูปมาตรฐาน()
    {
        var id = DocumentIssuerIdentity.Resolve(
            DocumentType.Invoice, null, false,
            "บริษัท ทดสอบ จำกัด", null, "0105500000000", null,
            "กรุงเทพฯ", null, null, null, null, "4472c4", null);
        Assert.Equal("#4472C4", id.PrimaryColor);
    }

    // ── A-IN7 ────────────────────────────────────────────────────────────

    [Fact]
    public void เปลี่ยนเป็นธุรกิจอสังหาฯ_ต้องเติมค่าเช่าล่วงหน้า()
    {
        var added = DepositKindSeed.AddedByIndustryChange(IndustryType.General, IndustryType.RealEstate);
        var row = Assert.Single(added);
        Assert.Equal(DepositKindSeed.RentAdvanceSeedKey, row.SeedKey);
    }

    [Fact]
    public void เปลี่ยนออกจากอสังหาฯ_ไม่มีอะไรต้องเติม_และไม่ลบของเดิม()
        => Assert.Empty(DepositKindSeed.AddedByIndustryChange(IndustryType.RealEstate, IndustryType.General));

    [Fact]
    public void ไม่ได้เปลี่ยนประเภทธุรกิจ_ไม่มีอะไรต้องเติม()
        => Assert.Empty(DepositKindSeed.AddedByIndustryChange(IndustryType.Hotel, IndustryType.Hotel));

    [Fact]
    public void เปลี่ยนเป็นโรงแรม_ชุดกุญแจเดิมครบอยู่แล้ว_ไม่เติมซ้ำ()
        // ชื่อแสดงต่างกัน (มัดจำค่าห้องพัก) แต่กุญแจ seed เดียวกัน ⇒ ไม่สร้างแถวที่สอง
        => Assert.Empty(DepositKindSeed.AddedByIndustryChange(IndustryType.General, IndustryType.Hotel));
}
