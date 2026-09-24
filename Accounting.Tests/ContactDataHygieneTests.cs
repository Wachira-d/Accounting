using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 · คำตัดสินเจ้าของข้อ 19 — รายงานผู้ติดต่อข้อมูลเสีย (อ่านอย่างเดียว)
/// ข้อมูลจริง: ใบ A Wine Pro ที่อยู่ <c>12/861</c> ถูกตัดเป็น <c>/861 …</c> (ทีม P รอบ 190) ·
/// ใบ B Radisson สาขาที่ 8 (ชะอำ 76120) กับสำนักงานใหญ่ (ปากเกร็ด นนทบุรี 11120)
///
/// <para>สองครึ่ง: ครึ่งแรก = แถวที่เสียต้องถูกรายงาน · ครึ่งหลัง = แถวที่ถูกต้อง/ผู้ใช้กรอกเอง/รูปแบบที่อยู่ต่างแต่ที่เดียวกัน
/// ต้อง<b>ไม่</b>ถูกรายงาน (คำเตือนที่ฟ้องของถูกทุกแถว = ปิดด่านโดยไม่ตั้งใจ)</para>
/// </summary>
public class ContactDataHygieneTests
{
    private const string HqRegistry = "200 หมู่ 8 ถนน แจ้งวัฒนะ ตำบล ปากเกร็ด อำเภอ ปากเกร็ด จังหวัด นนทบุรี 11120";
    private const string Branch8Paper = "854/2 ถนนบุรีรัมย์ ต.ชะอำ อ.ชะอำ จ.เพชรบุรี 76120";

    // ───────── ครึ่งแรก: ต้องถูกรายงาน ─────────

    [Theory]
    [InlineData("/861 ถนนสุขุมวิท ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110")]
    [InlineData("  / 861 ซอย 5")]
    public void TruncatedHouseNumber_IsReported(string address)
        => Assert.True(ContactDataHygiene.IsTruncatedAddress(address));

    [Fact]
    public void HqRow_FilledFromBranchPaper_RegistryPostalDiffers_Suspect()
    {
        var v = ContactDataHygiene.JudgeHeadOffice("00000", "OCR-DBD-AutoCreate", "OCR-Enrich",
            Branch8Paper, null, HqRegistry,
            new[] { new ContactScanEvidence("00008", Branch8Paper) });
        Assert.True(v.Suspect);
        Assert.True(v.RegistryDiffers);
        Assert.Equal("00008", v.BranchPaperCode);
        Assert.Contains("11120", v.Reason);
        Assert.Contains("สาขาที่ 00008", v.Reason);
    }

    [Fact]
    public void HqRow_BlankBranch_RegistryDiffers_EvenWithoutScanEvidence_Suspect()
    {
        // แถวที่ไม่เคยระบุสาขา = สำนักงานใหญ่ (≡ FindDuplicateContactAsync)
        var v = ContactDataHygiene.JudgeHeadOffice(null, null, "OCR-Enrich", Branch8Paper, "76120", HqRegistry, null);
        Assert.True(v.Suspect);
        Assert.Null(v.BranchPaperCode);
    }

    [Fact]
    public void HqRow_BranchPaperEvidence_RegistryUnavailable_Suspect_Unconfirmed()
    {
        var v = ContactDataHygiene.JudgeHeadOffice("00000", "OCR-FallbackAutoCreate", null, Branch8Paper, null, null,
            new[] { new ContactScanEvidence("8", Branch8Paper) });
        Assert.True(v.Suspect);
        Assert.Null(v.RegistryDiffers);          // "ยังไม่ได้ตรวจ" ไม่ใช่ "ไม่ต่าง"
        Assert.Contains("ยังยืนยันไม่ได้", v.Reason);
    }

    // ───────── ครึ่งหลัง: ต้องไม่ถูกรายงาน ─────────

    [Theory]
    [InlineData("12/861 ถนนสุขุมวิท ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110")]
    [InlineData("ห้อง 5/12 อาคาร A")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalAddress_NotTruncated(string? address)
        => Assert.False(ContactDataHygiene.IsTruncatedAddress(address));

    [Fact]
    public void HqRow_SamePostalAsRegistry_DifferentSpelling_NotSuspect()
    {
        // ทะเบียน "ตำบล ปากเกร็ด" กระดาษ "ต.ปากเกร็ด" — ที่เดียวกัน ห้ามฟ้อง
        var v = ContactDataHygiene.JudgeHeadOffice("00000", "OCR-DBD-AutoCreate", null,
            "200 ม.8 ถ.แจ้งวัฒนะ ต.ปากเกร็ด อ.ปากเกร็ด จ.นนทบุรี 11120", null, HqRegistry, null);
        Assert.False(v.Suspect);
        Assert.False(v.RegistryDiffers);
    }

    [Fact]
    public void HqRow_BranchAtSameAddressAsHq_RegistryConfirmsPostal_NotSuspect()
    {
        // สาขาที่ตั้งอยู่ตึกเดียวกับ สนญ. (พบบ่อย) — ทะเบียนยืนยันรหัสไปรษณีย์ตรง ⇒ ไม่ฟ้อง
        const string same = "200 หมู่ 8 ถนน แจ้งวัฒนะ ตำบล ปากเกร็ด อำเภอ ปากเกร็ด จังหวัด นนทบุรี 11120";
        var v = ContactDataHygiene.JudgeHeadOffice("00000", "OCR-Enrich", null, same, "11120", HqRegistry,
            new[] { new ContactScanEvidence("00002", same) });
        Assert.False(v.Suspect);
    }

    [Fact]
    public void UserEnteredRow_NeverJudged()
    {
        var v = ContactDataHygiene.JudgeHeadOffice("00000", "user-123", "user-123", Branch8Paper, null, HqRegistry,
            new[] { new ContactScanEvidence("00008", Branch8Paper) });
        Assert.False(v.Suspect);
    }

    [Fact]
    public void BranchRow_OutOfScope()
    {
        var v = ContactDataHygiene.JudgeHeadOffice("00008", "OCR-DBD-AutoCreate", null, Branch8Paper, null, HqRegistry, null);
        Assert.False(v.Suspect);
    }

    [Fact]
    public void HeadOfficeScanOnly_NotBranchEvidence()
    {
        var v = ContactDataHygiene.JudgeHeadOffice("00000", "OCR-Enrich", null, HqRegistry, null, null,
            new[] { new ContactScanEvidence("00000", HqRegistry), new ContactScanEvidence(null, HqRegistry) });
        Assert.False(v.Suspect);
    }

    [Theory]
    [InlineData("10110", "อะไรก็ได้ 20110", "10110")]       // ช่อง structured ชนะ
    [InlineData(null, "เลขที่ 0105551136085 ต.ชะอำ 76120", "76120")]   // เลขภาษี 13 หลักไม่ถูกหยิบ
    [InlineData(null, "โทร 02-123-4567", null)]
    public void PostalCodeOf_PicksFiveDigitPostal(string? structured, string? address, string? expected)
        => Assert.Equal(expected, ContactDataHygiene.PostalCodeOf(structured, address));
}
