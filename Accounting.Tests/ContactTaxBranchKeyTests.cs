using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 · คำตัดสินเจ้าของข้อ 20 — ทางเข้าอื่นที่หาผู้ติดต่อด้วยเลขผู้เสียภาษี (integration · นำเข้า · POS · ที่พัก ·
/// API v1) ต้องใช้คีย์ <b>เลขภาษี + สาขา</b> แบบ <c>FindDuplicateContactAsync</c>.
/// ข้อมูลตัวอย่าง: ใบ B Radisson — เลข <c>0105551136085</c> สำนักงานใหญ่ (ปากเกร็ด) + สาขาที่ 8 (ชะอำ)
///
/// <para>สองครึ่ง (CLAUDE.md §H): ครึ่งแรก = เคสที่พัง (payload สาขา 8 หยิบแถว สนญ. แล้ว integration เขียนสาขาทับ) ·
/// ครึ่งหลัง = ข้อมูลเดิมต้องไม่ถูกสร้างซ้ำ (แถวไม่เคยระบุสาขา · payload ไม่ส่งสาขา · เลขมีขีด · เลขไม่ใช่ 13 หลัก)</para>
/// </summary>
public class ContactTaxBranchKeyTests
{
    private const string Tin = "0105551136085";
    private static readonly Guid Hq = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid B8 = Guid.Parse("00000000-0000-0000-0000-000000000008");
    private static readonly Guid Blank = Guid.Parse("00000000-0000-0000-0000-0000000000bb");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000ff");

    private static ContactKeyCandidate C(Guid id, string? tax, string? branch) => new(id, tax, branch);

    // ───────── ครึ่งแรก: ใบสาขา 8 ต้องไม่ไปจับแถวสำนักงานใหญ่ ─────────

    [Fact]
    public void Branch8Payload_BothRowsExist_PicksBranch8_EvenWhenHqSortsFirst()
    {
        var m = ContactTaxBranchKey.Pick(new[] { C(Hq, Tin, "00000"), C(B8, Tin, "00008") }, Tin, "00008");
        Assert.Equal(B8, m.ContactId);
        Assert.Equal(ContactKeyBasis.ExactBranch, m.Basis);
    }

    [Fact]
    public void Branch8Payload_OnlyExplicitHqRow_NotFound_SoCallerCreatesBranchRow_AndDoesNotOverwriteHq()
    {
        // เดิม: c.TaxId == x → ได้แถว สนญ. แล้ว IntegrationService เขียน BranchCode = 00008 ทับ
        var m = ContactTaxBranchKey.Pick(new[] { C(Hq, Tin, "00000") }, Tin, "8");
        Assert.False(m.Found);
        Assert.True(m.TaxIdExists);   // ⬅ ผู้เรียกต้องไม่ถอยไปจับด้วยชื่อ (จะได้แถว สนญ. กลับมา)
    }

    [Fact]
    public void HqPayload_OnlyBranch8Row_NotFound()
    {
        var m = ContactTaxBranchKey.Pick(new[] { C(B8, Tin, "00008") }, Tin, "00000");
        Assert.False(m.Found);
        Assert.True(m.TaxIdExists);
    }

    [Fact]
    public void HqPayload_PicksHq_NotBranch()
    {
        var m = ContactTaxBranchKey.Pick(new[] { C(B8, Tin, "00008"), C(Hq, Tin, "00000") }, Tin, "00000");
        Assert.Equal(Hq, m.ContactId);
    }

    // ───────── ครึ่งหลัง: ข้อมูลเดิมห้ามถูกสร้างซ้ำ / ห้ามเปลี่ยนคำตอบ ─────────

    [Fact]
    public void LegacyRowWithoutBranch_ClaimedByBranchPayload_NoDuplicate()
    {
        // แถวที่สร้างก่อนทางเข้านี้ส่งสาขา (BranchCode ว่าง) — พฤติกรรมเดิม: ผูกแถวนี้ (ไม่สร้างแถวใหม่)
        var m = ContactTaxBranchKey.Pick(new[] { C(Blank, Tin, null) }, Tin, "00008");
        Assert.Equal(Blank, m.ContactId);
        Assert.Equal(ContactKeyBasis.UnspecifiedBranchRow, m.Basis);
    }

    [Fact]
    public void LegacyRowWithoutBranch_IsHeadOffice_ForHqPayload()
    {
        // ≡ FindDuplicateContactAsync: สาขาว่าง = 00000
        var m = ContactTaxBranchKey.Pick(new[] { C(Blank, Tin, "") }, Tin, "00000");
        Assert.Equal(Blank, m.ContactId);
        Assert.Equal(ContactKeyBasis.ExactBranch, m.Basis);
    }

    [Fact]
    public void ExplicitHq_WinsOverBlankRow_ForHqPayload()
    {
        var m = ContactTaxBranchKey.Pick(new[] { C(Blank, Tin, null), C(Hq, Tin, "00000") }, Tin, "00000");
        Assert.Equal(Hq, m.ContactId);
    }

    [Fact]
    public void MissingBranchInPayload_PrefersHq()
    {
        var m = ContactTaxBranchKey.Pick(new[] { C(B8, Tin, "00008"), C(Hq, Tin, "00000") }, Tin, null);
        Assert.Equal(Hq, m.ContactId);
        Assert.Equal(ContactKeyBasis.HeadOfficeForMissingBranch, m.Basis);
    }

    [Fact]
    public void MissingBranchInPayload_OnlyBranchRow_StillMatches_NoNewRow()
    {
        // payload ไม่ส่งสาขา ≠ "สำนักงานใหญ่" — เลขนี้มีแถวเดียว (สาขา 8) → ผูกตามเดิม ไม่สร้างแถว สนญ. ใหม่
        var m = ContactTaxBranchKey.Pick(new[] { C(B8, Tin, "00008") }, Tin, "  ");
        Assert.Equal(B8, m.ContactId);
        Assert.Equal(ContactKeyBasis.OnlyRowForMissingBranch, m.Basis);
    }

    [Fact]
    public void MissingBranchInPayload_ManyBranchRows_LowestCode_IndependentOfDbOrder()
    {
        var b3 = Guid.Parse("00000000-0000-0000-0000-000000000003");
        var a = ContactTaxBranchKey.Pick(new[] { C(B8, Tin, "00008"), C(b3, Tin, "00003") }, Tin, null);
        var b = ContactTaxBranchKey.Pick(new[] { C(b3, Tin, "00003"), C(B8, Tin, "00008") }, Tin, null);
        Assert.Equal(b3, a.ContactId);
        Assert.Equal(a, b);
        Assert.Equal(ContactKeyBasis.LowestBranchForMissingBranch, a.Basis);
    }

    [Fact]
    public void MalformedBranch_IsUnknown_NotHeadOffice()
    {
        // "8A" ไม่ใช่รหัส 5 หลัก = ไม่รู้ → กติกา "ไม่ระบุสาขา" (ได้ สนญ.) ไม่ใช่ "ไม่พบ" แล้วสร้างแถวเพี้ยน
        var m = ContactTaxBranchKey.Pick(new[] { C(Hq, Tin, "00000"), C(B8, Tin, "00008") }, Tin, "8A");
        Assert.Equal(Hq, m.ContactId);
        Assert.Equal(ContactKeyBasis.HeadOfficeForMissingBranch, m.Basis);
    }

    [Fact]
    public void FormattedTaxId_MatchesDigitsOnly_BothDirections()
    {
        Assert.Equal(B8, ContactTaxBranchKey.Pick(new[] { C(B8, "0-1055-51136-08-5", "00008") }, Tin, "00008").ContactId);
        Assert.Equal(B8, ContactTaxBranchKey.Pick(new[] { C(B8, Tin, "00008") }, "0-1055-51136-08-5", "00008").ContactId);
    }

    [Fact]
    public void OtherTaxId_NeverMatches()
    {
        var m = ContactTaxBranchKey.Pick(new[] { C(Other, "0105500000001", "00000") }, Tin, "00000");
        Assert.False(m.Found);
        Assert.False(m.TaxIdExists);
    }

    [Fact]
    public void NonThirteenDigitTaxId_LegacyExactEquality_IgnoresBranch()
    {
        var m = ContactTaxBranchKey.Pick(new[] { C(Other, "DE123456789", null) }, " DE123456789 ", "00008");
        Assert.Equal(Other, m.ContactId);
        Assert.Equal(ContactKeyBasis.RawTaxIdEquality, m.Basis);
    }

    [Fact]
    public void EmptyTaxId_Nothing()
    {
        Assert.False(ContactTaxBranchKey.Pick(new[] { C(Hq, Tin, "00000") }, null, "00000").Found);
        Assert.False(ContactTaxBranchKey.Pick(new[] { C(Hq, Tin, "00000") }, "  ", null).TaxIdExists);
    }
}
