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
    public void RadissonBranch8_OnlyHeadOfficeRow_CreatesBranchRow_NotBindHeadOffice()
    {
        // รอบ 197 (คำตัดสินเจ้าของ: "คนละสาขา คนละที่อยู่ ต้องเป็นผู้ติดต่อคนละอัน") — เดิมรอบ 190 ผูกแถว สนญ. ไว้ก่อนรอตัดสิน
        var pick = OcrVendorBranchContact.Decide(new[] { new C(Hq, Radisson, "00000") }, "00008");
        Assert.Equal(OcrVendorBranchOutcome.NewBranchRow, pick.Outcome);
        Assert.Null(pick.ContactId);                  // ห้ามผูก สนญ.
        Assert.Equal(Hq, pick.TemplateContactId);     // แม่แบบชื่อนิติบุคคล = แถว สนญ. ของเลขเดียวกัน
        Assert.True(pick.MustCreateBranchRow);
        Assert.Equal("00008", pick.ScannedBranch);
        Assert.False(pick.MayEnrichMatchedRow);       // ไม่มีแถวให้เติม — ที่อยู่สาขาต้องไม่ไปทับแถว สนญ.
        Assert.Contains("สาขาที่ 00008", pick.Trace);   // ป้ายจาก TaxBranchCode.Label — รอบ 193 ข้อ 21 = 5 หลักเต็ม
        Assert.Contains("สำนักงานใหญ่", pick.Trace);
    }

    [Fact]
    public void RadissonBranch8_WeakBranchEvidence_KeepsOldBindingWithoutEnrich()
    {
        // ทิศตรงข้าม: รหัสสาขาที่ "ขัดกับประโยคบนกระดาษ" (คะแนน 0.50) ไม่ใช่หลักฐานพอจะสร้างผู้ติดต่อ — ไม่เดาสาขาใหม่
        var pick = OcrVendorBranchContact.Decide(new[] { new C(Hq, Radisson, "00000") }, "00008",
            branchReliable: OcrVendorBranchContact.IsReliableBranch(0.50));
        Assert.Equal(OcrVendorBranchOutcome.OtherBranchRow, pick.Outcome);
        Assert.Equal(Hq, pick.ContactId);
        Assert.False(pick.MustCreateBranchRow);
        Assert.False(pick.MayEnrichMatchedRow);
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
    public void HeadOfficePaper_OnlyBranchRowExists_CreatesHeadOfficeRow()
    {
        // ทิศกลับ: ใบของ สนญ. แต่มีแต่แถวสาขาที่ 8 — คนละสถานประกอบการเหมือนกัน ⇒ สร้างแถว สนญ. (ไม่ทับแถวสาขา)
        var pick = OcrVendorBranchContact.Decide(new[] { new C(B8, Radisson, "00008") }, "00000");
        Assert.Equal(OcrVendorBranchOutcome.NewBranchRow, pick.Outcome);
        Assert.Null(pick.ContactId);
        Assert.Equal(B8, pick.TemplateContactId);
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
    public void LegacyRowWithoutBranch_BranchPaper_CreatesBranchRow_SameRuleAsContactTaxBranchKey()
    {
        // เปลี่ยนจงใจ (รอบ 197): แถวที่ไม่เคยกรอกสาขา ≡ สำนักงานใหญ่ ตามตัวจับคู่กลาง ContactTaxBranchKey ข้อ 1 —
        // เดิมใบสาขาที่ 8 อ้างแถวนี้แล้ว [Enrich] ประทับ 00008 ทับแถวที่อาจถือประวัติ สนญ. อยู่ (ช่องเดียวกับที่ทีม C3 ปิดใน integration)
        var pick = OcrVendorBranchContact.Decide(new[] { new C(Legacy, Radisson, null) }, "00008");
        Assert.Equal(OcrVendorBranchOutcome.NewBranchRow, pick.Outcome);
        Assert.Null(pick.ContactId);
        Assert.Equal(Legacy, pick.TemplateContactId);
        Assert.Equal(ContactTaxBranchKey.Pick(new[] { new ContactKeyCandidate(Legacy, "0105551136085", null) }, "0105551136085", "00008").Found,
            pick.ContactId.HasValue);   // สองทางเข้า ตัดสินเหมือนกัน
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

    // ───────── รอบ 197 ทีม K: ใบ Makro (บมจ.ซีพี แอ็กซ์ตร้า) สาขาชลบุรี 00005 ─────────

    private static readonly Guid CpHq = Guid.Parse("60fb5887-0f47-4cf7-81cd-89e5f6c7ce76");   // ID บนภาพ screen-28
    private static readonly Guid Cp5 = Guid.Parse("00000000-0000-0000-0000-000000000005");
    private const string CpAxtra = "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)";

    [Fact]
    public void Makro00005_OnlyHeadOfficeContact_CreatesBranchRow()
    {
        // ผู้ใช้: "ได้เลขผู้ขายและสาขาถูก ... สร้างเอกสารใช้ผู้ติดต่อผิด (สำนักงานใหญ่ 00000)" — เดิม OtherBranchRow ⇒ ผูก 60fb5887
        var pick = OcrVendorBranchContact.Decide(new[] { new C(CpHq, CpAxtra, "00000") }, "00005");
        Assert.Equal(OcrVendorBranchOutcome.NewBranchRow, pick.Outcome);
        Assert.NotEqual(CpHq, pick.ContactId);
        Assert.Equal(CpHq, pick.TemplateContactId);
        Assert.Equal("00005", pick.ScannedBranch);
        Assert.Equal(CpAxtra, OcrVendorBranchContact.NewRowName(null, CpAxtra, "ma ro"));   // ชื่อนิติบุคคล ไม่ใช่ชื่อโลโก้
    }

    [Fact]
    public void Makro00005_SecondScan_BindsTheBranchRowCreatedByTheFirst()
    {
        // ทิศตรงข้าม: สแกนหน้า 1/3, 2/3 ของใบเดียวกัน (หรือเดือนถัดไป) ต้องผูกแถวสาขาเดิม — ไม่สร้างซ้ำทุกสแกน
        var pick = OcrVendorBranchContact.Decide(
            new[] { new C(CpHq, CpAxtra, "00000"), new C(Cp5, CpAxtra, "00005") }, "00005");
        Assert.Equal(OcrVendorBranchOutcome.ExactBranch, pick.Outcome);
        Assert.Equal(Cp5, pick.ContactId);
        Assert.True(pick.MayEnrichMatchedRow);
    }

    [Fact]
    public void MakroHeadOfficePaper_BindsHeadOffice_Unchanged()
    {
        var pick = OcrVendorBranchContact.Decide(
            new[] { new C(CpHq, CpAxtra, "00000"), new C(Cp5, CpAxtra, "00005") }, "00000");
        Assert.Equal(OcrVendorBranchOutcome.ExactBranch, pick.Outcome);
        Assert.Equal(CpHq, pick.ContactId);
    }

    [Fact]
    public void MakroBranchNotRead_KeepsHeadOfficeRule_NoNewRow()
    {
        // ทิศตรงข้าม: อ่านสาขาไม่ได้ = พฤติกรรมเดิม (สำนักงานใหญ่ก่อน) ไม่เดาสาขาใหม่
        var pick = OcrVendorBranchContact.Decide(
            new[] { new C(Cp5, CpAxtra, "00005"), new C(CpHq, CpAxtra, "00000") }, null);
        Assert.Equal(OcrVendorBranchOutcome.BranchNotRead, pick.Outcome);
        Assert.Equal(CpHq, pick.ContactId);
        Assert.False(pick.MustCreateBranchRow);
    }

    [Theory]
    [InlineData(null, false, true)]     // ไม่มีคะแนนแยกช่อง = ค่าที่ engine/e-Tax อ่านจากกระดาษ
    [InlineData(0.85, false, true)]     // BranchCodeExtractor
    [InlineData(0.90, false, true)]     // ประโยคประกาศสาขาผู้ออกใบ
    [InlineData(0.50, false, false)]    // ขัดกับประโยคบนกระดาษ
    [InlineData(0.30, false, false)]    // อ่านไม่ได้
    [InlineData(0.50, true, true)]      // ผู้ใช้แก้รหัสสาขาเองในฟอร์ม = หลักฐานสูงสุด
    public void IsReliableBranch_Thresholds(double? conf, bool userCorrected, bool expected)
        => Assert.Equal(expected, OcrVendorBranchContact.IsReliableBranch(conf, userCorrected));

    [Fact]
    public void NewRowName_RegistryFirst_ThenTemplateWithoutBranchSuffix_ThenPaper()
    {
        Assert.Equal("บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)",
            OcrVendorBranchContact.NewRowName("บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)", "ซีพี (สำนักงานใหญ่)", "ma ro"));
        Assert.Equal("บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)",
            OcrVendorBranchContact.NewRowName(null, "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน) (สำนักงานใหญ่)", "ma ro"));
        Assert.Equal("ma ro", OcrVendorBranchContact.NewRowName(null, null, "ma ro"));
        Assert.Null(OcrVendorBranchContact.NewRowName(" ", null, ""));
    }

    [Theory]
    [InlineData("00005")]
    [InlineData("00000")]
    [InlineData("00008")]
    [InlineData(null)]
    [InlineData("8A")]
    public void Decide_AgreesWithContactTaxBranchKey_OnWhichRowIsTheSameEstablishment(string? scanned)
    {
        // ตัวตั้งตัวเดียว (หลักการข้อ 4): เส้น OCR กับทุกทางเข้าอื่นต้องตอบ "แถวไหนคือสถานประกอบการนี้" เหมือนกัน
        const string tin = "0107567000414";
        var rows = new[] { new C(CpHq, CpAxtra, "00000"), new C(Cp5, CpAxtra, "00005"), new C(Legacy, CpAxtra, null) };
        var ocr = OcrVendorBranchContact.Decide(rows, scanned);
        var key = ContactTaxBranchKey.Pick(rows.Select(r => new ContactKeyCandidate(r.Id, tin, r.BranchCode)), tin, scanned);
        Assert.Equal(key.ContactId, ocr.ContactId);
    }
}
