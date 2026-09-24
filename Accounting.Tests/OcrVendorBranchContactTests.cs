using Accounting.Helpers;
using Xunit;
using C = Accounting.Helpers.OcrVendorBranchContact.Candidate;

namespace Accounting.Tests;

/// <summary>
/// รอบ 190 ทีม C · ข้อ 5 ของเจ้าของ — "ใบ B เป็นสาขาที่ 8 ต้องดึงเลขสาขาไปสร้างข้อมูลให้ถูก"
///
/// <para>ใบ B (Radisson Hua Hin) พิมพ์ว่า <c>Branch Tax Invoice is Issued no. 8</c> /
/// <c>สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8</c> โดยเลขผู้เสียภาษี <c>0105551136085</c> — เดิมถ้าในระบบมีผู้ติดต่อ
/// เลขนี้ <b>แถวเดียว</b> (สำนักงานใหญ่) ระบบหยิบแถวนั้นทันทีโดยไม่ดูสาขา และตัวเติม <c>[Enrich]</c> อาจเอา
/// ที่อยู่ของใบไปทับแถวสำนักงานใหญ่</para>
///
/// <para>สองครึ่งตาม CLAUDE.md §H: ครึ่งแรก = ใบที่พังกลับมาถูก (สาขาที่ 8 เลือกแถวของตัวเองเมื่อมี ·
/// ไม่มี = ผูกแถวเดิมแต่ห้ามเอาที่อยู่สาขาไปทับ) ·
/// ครึ่งหลัง = ใบที่ถูกอยู่แล้วต้องได้คำตอบเดิม (แถวเดียวไม่มีสาขา · อ่านสาขาไม่ได้ · สาขาตรงอยู่แล้ว ·
/// ใบ A Wine Pro <c>Branch 00012</c> ที่มีแถวสาขา 12 อยู่แล้ว)</para>
/// </summary>
public class OcrVendorBranchContactTests
{
    private static readonly Guid Hq = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid B8 = Guid.Parse("00000000-0000-0000-0000-000000000008");
    private static readonly Guid B12 = Guid.Parse("00000000-0000-0000-0000-000000000012");
    private static readonly Guid Legacy = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private const string Radisson = "บริษัท เดสติเนชั่น รีสอร์ทส์ จำกัด";

    // ───────── ครึ่งแรก: ใบสาขาที่ 8 ─────────

    [Fact]
    public void RadissonBranch8_OnlyHeadOfficeRow_BindsHeadOfficeButNeverEnrichesIt()
    {
        var pick = OcrVendorBranchContact.Decide(new[] { new C(Hq, Radisson, "00000") }, "00008");
        Assert.Equal(OcrVendorBranchOutcome.OtherBranchRow, pick.Outcome);
        Assert.Equal(Hq, pick.ContactId);             // ผูกแถวเดิม (พฤติกรรมเดิม — ไม่สร้างแถวเองจนกว่าเจ้าของตัดสิน)
        Assert.Equal("00008", pick.ScannedBranch);    // สาขาของใบยังไปที่เอกสาร
        Assert.False(pick.MayEnrichMatchedRow);       // ⬅ เดิม: ที่อยู่สาขาที่ 8 ทับแถว สนญ. ได้
        Assert.Contains("สาขาที่ 8", pick.Trace);
        Assert.Contains("สำนักงานใหญ่", pick.Trace);
    }

    [Fact]
    public void RadissonBranch8_RowAlreadyExists_PicksIt_EvenIfHeadOfficeSortsFirst()
    {
        var pick = OcrVendorBranchContact.Decide(
            new[] { new C(Hq, Radisson, "00000"), new C(B8, Radisson, "00008") }, "8");
        Assert.Equal(OcrVendorBranchOutcome.ExactBranch, pick.Outcome);
        Assert.Equal(B8, pick.ContactId);
        Assert.True(pick.MayEnrichMatchedRow);
    }

    [Fact]
    public void HeadOfficePaper_OnlyBranchRowExists_DoesNotEnrichBranchRow()
    {
        // ทิศกลับ: ใบของ สนญ. แต่มีแต่แถวสาขาที่ 8 — ที่อยู่ สนญ. ต้องไม่ทับแถวสาขา
        var pick = OcrVendorBranchContact.Decide(new[] { new C(B8, Radisson, "00008") }, "00000");
        Assert.Equal(OcrVendorBranchOutcome.OtherBranchRow, pick.Outcome);
        Assert.Equal(B8, pick.ContactId);
        Assert.False(pick.MayEnrichMatchedRow);
    }

    // ───────── ครึ่งหลัง: ใบที่ถูกอยู่แล้ว ต้องได้คำตอบเดิม ─────────

    [Fact]
    public void WinePro_Branch00012_RowExists_Exact()
    {
        var pick = OcrVendorBranchContact.Decide(
            new[] { new C(Hq, "Wine Pro Co.,Ltd.", "00000"), new C(B12, "Wine Pro Co.,Ltd.", "00012") }, "00012");
        Assert.Equal(OcrVendorBranchOutcome.ExactBranch, pick.Outcome);
        Assert.Equal(B12, pick.ContactId);
    }

    [Fact]
    public void LegacyRowWithoutBranch_IsAdoptedAsBefore()
    {
        // แถวเก่าที่ไม่เคยกรอกสาขา — เดิมผูกแถวนี้ แล้วตัวเติมเติมสาขาจากกระดาษให้ (คงพฤติกรรม)
        var pick = OcrVendorBranchContact.Decide(new[] { new C(Legacy, Radisson, null) }, "00008");
        Assert.Equal(OcrVendorBranchOutcome.AdoptBlankBranchRow, pick.Outcome);
        Assert.Equal(Legacy, pick.ContactId);
        Assert.True(pick.MayEnrichMatchedRow);
    }

    [Fact]
    public void BranchNotRead_SingleRow_PicksIt_Silently()
    {
        var pick = OcrVendorBranchContact.Decide(new[] { new C(Hq, Radisson, "00000") }, null);
        Assert.Equal(OcrVendorBranchOutcome.BranchNotRead, pick.Outcome);
        Assert.Equal(Hq, pick.ContactId);
        Assert.Equal("", pick.Trace);
        Assert.True(pick.MayEnrichMatchedRow);
    }

    [Fact]
    public void BranchNotRead_ManyRows_HeadOfficeFirst_Deterministic()
    {
        var a = OcrVendorBranchContact.Decide(new[] { new C(B8, Radisson, "00008"), new C(Hq, Radisson, "00000") }, "");
        var b = OcrVendorBranchContact.Decide(new[] { new C(Hq, Radisson, "00000"), new C(B8, Radisson, "00008") }, "");
        Assert.Equal(Hq, a.ContactId);
        Assert.Equal(Hq, b.ContactId);   // ลำดับที่ฐานคืนมาไม่มีผล
    }

    [Fact]
    public void HeadOfficePaper_BlankRow_CountsAsHeadOffice()
    {
        var pick = OcrVendorBranchContact.Decide(new[] { new C(Legacy, Radisson, "") }, "00000");
        Assert.Equal(OcrVendorBranchOutcome.ExactBranch, pick.Outcome);
        Assert.Equal(Legacy, pick.ContactId);
    }

    [Fact]
    public void GarbageBranch_IsUnknown_NotHeadOffice()
    {
        // อ่านได้ "8A" — ผิดรูป = ไม่รู้ ห้ามกลายเป็น 00000 แล้วไปจับ/สร้างผิดแถว
        var pick = OcrVendorBranchContact.Decide(new[] { new C(Hq, Radisson, "00000") }, "8A");
        Assert.Equal(OcrVendorBranchOutcome.BranchNotRead, pick.Outcome);
        Assert.Equal(Hq, pick.ContactId);
    }

    [Fact]
    public void NoCandidates_NoDecision_OldNameMatchingKeepsEnriching()
    {
        var pick = OcrVendorBranchContact.Decide(Array.Empty<C>(), "00008");
        Assert.Equal(OcrVendorBranchOutcome.NoTaxIdMatch, pick.Outcome);
        Assert.Null(pick.ContactId);
        Assert.True(pick.MayEnrichMatchedRow);
    }
}
