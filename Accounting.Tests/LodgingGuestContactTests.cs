using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>ฝ่ายค้าน C-7 รอบ 193 — แขกนิติบุคคล (ส่งเลขภาษี/ชื่อบริษัท) ห้ามได้แถวบุคคลธรรมดาที่อีเมล/เบอร์ตรง
/// (ใบกำกับจะออกในชื่อบุคคลโดยไม่มีเลขผู้ซื้อ §86/4) · สองทิศ: แขกทั่วไปยังจับแถวเดิมได้เหมือนเดิม</summary>
public class LodgingGuestContactTests
{
    [Fact]
    public void แขกนิติบุคคลเลขใหม่_แถวบุคคลธรรมดาอีเมลตรง_ห้ามใช้()
        => Assert.False(LodgingGuestContact.SoftCandidateAcceptable(
            "0105556000001", "บริษัท ตัวอย่าง จำกัด", "สมชาย ใจดี", ContactType.Individual));

    [Fact]
    public void แขกนิติบุคคล_แถวนิติบุคคลชื่อคนละบริษัท_ห้ามใช้()
        => Assert.False(LodgingGuestContact.SoftCandidateAcceptable(
            "0105556000001", "บริษัท ตัวอย่าง จำกัด", "บริษัท ตัวอย่างการค้า จำกัด", ContactType.JuristicPerson));

    [Fact]
    public void แขกมีเลขภาษีแต่ไม่มีชื่อบริษัท_ยืนยันตัวไม่ได้_สร้างแถวใหม่()
        => Assert.False(LodgingGuestContact.SoftCandidateAcceptable(
            "1100700000001", null, "สมชาย ใจดี", ContactType.Unknown));

    [Theory]
    [InlineData(ContactType.JuristicPerson)]
    [InlineData(ContactType.Unknown)]
    public void แขกนิติบุคคล_แถวชื่อบริษัทตรง_ใช้ได้_ไม่สนช่องว่างและตัวพิมพ์(ContactType type)
        => Assert.True(LodgingGuestContact.SoftCandidateAcceptable(
            "0105556000001", "บริษัท  Example   จำกัด", " บริษัท example จำกัด ", type));

    [Theory]
    [InlineData(ContactType.Individual)]
    [InlineData(ContactType.JuristicPerson)]
    public void แขกทั่วไปไม่ส่งเลขหรือชื่อบริษัท_พฤติกรรมเดิม_ใช้แถวที่อีเมลตรงได้(ContactType type)
        => Assert.True(LodgingGuestContact.SoftCandidateAcceptable(null, "  ", "สมชาย ใจดี", type));
}
