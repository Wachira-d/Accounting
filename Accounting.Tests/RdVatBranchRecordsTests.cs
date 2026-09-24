using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 190 ทีม C · ข้อ 5 — "ดึง DBD แยกสาขาได้ไหม": ทะเบียน VAT กรมสรรพากร (vatserviceRD3) คืนแต่ละช่อง
/// เป็นลิสต์ <c>anyType</c> ที่ดัชนีตรงกัน (ดัชนี i ของทุกช่อง = สถานประกอบการเดียวกัน).
/// ตัวอ่านเดิมหยิบ "ค่าแรกที่ไม่ว่าง" ทีละช่อง — ใช้ได้กับแถวเดียว แต่ถ้ามีหลายแถวจะได้ที่อยู่ปนกันข้ามสาขา.
/// เทสต์ล็อก: ประกอบทั้งแถวจากดัชนีเดียว · เลือกด้วยเลขสาขาจริงเท่านั้น · จัดแถวไม่ได้ = ไม่ตอบ
/// </summary>
public class RdVatBranchRecordsTests
{
    // ตัวอย่างรูปแบบคำตอบ (ข้อมูลใบ B Radisson: สนญ. ปากเกร็ด + สาขาที่ 8 ชะอำ) — ค่าตัวแทนว่างของ RD คือ "-"
    private static string Xml(string branchNumbers, string houses, string thambols)
        => $"""
        <ServiceResponse><ServiceResult>
          <vtitleName><anyType xsi:type="xsd:string">บริษัท</anyType><anyType xsi:type="xsd:string">บริษัท</anyType></vtitleName>
          <vName><anyType>เดสติเนชั่น รีสอร์ทส์ จำกัด</anyType><anyType>เดสติเนชั่น รีสอร์ทส์ จำกัด</anyType></vName>
          <vSurname><anyType>-</anyType><anyType>-</anyType></vSurname>
          <vBranchTitleName><anyType>-</anyType><anyType>สาขา</anyType></vBranchTitleName>
          <vBranchName><anyType>-</anyType><anyType>ชะอำ</anyType></vBranchName>
          <vBranchNumber>{branchNumbers}</vBranchNumber>
          <vHouseNumber>{houses}</vHouseNumber>
          <vThambol>{thambols}</vThambol>
          <vPostCode><anyType>11120</anyType><anyType>76120</anyType></vPostCode>
        </ServiceResult></ServiceResponse>
        """;

    private static readonly string TwoBranches = Xml(
        "<anyType xsi:type=\"xsd:int\">0</anyType><anyType xsi:type=\"xsd:int\">8</anyType>",
        "<anyType>200</anyType><anyType>854/2</anyType>",
        "<anyType>ปากเกร็ด</anyType><anyType>ชะอำ</anyType>");

    [Fact]
    public void TwoEstablishments_EachRowFromItsOwnIndex()
    {
        var rows = RdVatBranchRecords.Parse(TwoBranches);
        Assert.Equal(2, rows.Count);
        Assert.Equal("00000", rows[0].BranchCode);
        Assert.Equal("00008", rows[1].BranchCode);
        Assert.Equal("บริษัท เดสติเนชั่น รีสอร์ทส์ จำกัด", rows[1].Name);   // "-" ของนามสกุลไม่ติดมา
        Assert.Equal("854/2 ตำบล ชะอำ 76120", rows[1].Address);            // ไม่ปนเลขบ้าน 200 ของ สนญ.
        Assert.Equal("ชะอำ", rows[1].BranchName);
    }

    [Fact]
    public void PickBranch_Branch8_ReturnsBranchAddress_NotHeadOffice()
    {
        var b8 = RdVatBranchRecords.PickBranch(RdVatBranchRecords.Parse(TwoBranches), "00008");
        Assert.NotNull(b8);
        Assert.StartsWith("854/2", b8!.Address);
        Assert.Equal(b8, RdVatBranchRecords.PickBranch(RdVatBranchRecords.Parse(TwoBranches), "8"));
    }

    [Fact]
    public void PickBranch_HeadOffice_StillWorks()
    {
        var hq = RdVatBranchRecords.PickBranch(RdVatBranchRecords.Parse(TwoBranches), "00000");
        Assert.StartsWith("200", hq!.Address);
    }

    [Fact]
    public void PickBranch_UnknownBranch_Null_NeverFallsBackToHeadOffice()
        => Assert.Null(RdVatBranchRecords.PickBranch(RdVatBranchRecords.Parse(TwoBranches), "00003"));

    [Fact]
    public void NoBranchNumberFromRd_CannotConfirmBranch()
    {
        // RD ไม่ส่งเลขสาขา — แถวเดียวที่ได้อาจเป็น สนญ. ⇒ ห้ามติดป้ายว่าเป็นสาขาที่ 8
        var xml = Xml("", "<anyType>200</anyType><anyType>854/2</anyType>",
            "<anyType>ปากเกร็ด</anyType><anyType>ชะอำ</anyType>").Replace("<vBranchNumber></vBranchNumber>", "");
        var rows = RdVatBranchRecords.Parse(xml);
        Assert.Equal(2, rows.Count);
        Assert.Null(RdVatBranchRecords.PickBranch(rows, "00008"));
    }

    [Fact]
    public void MisalignedLists_ReturnNothing_NoGuessing()
    {
        // ช่องบ้านเลขที่มี 1 ค่า แต่ชื่อมี 2 ค่า — จับคู่ข้ามแถวไม่ได้ ⇒ ไม่ตอบ
        var xml = Xml("<anyType>0</anyType><anyType>8</anyType>", "<anyType>200</anyType>",
            "<anyType>ปากเกร็ด</anyType><anyType>ชะอำ</anyType>");
        Assert.Empty(RdVatBranchRecords.Parse(xml));
    }

    [Fact]
    public void EmptyOrNoName_Empty()
    {
        Assert.Empty(RdVatBranchRecords.Parse(null));
        Assert.Empty(RdVatBranchRecords.Parse("<soap:Fault>err</soap:Fault>"));
    }
}
