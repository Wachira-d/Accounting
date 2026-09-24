using Accounting.Helpers;
using Accounting.Models.Entities;
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

    // ───────── รอบ 193 ทีม C3: แถวสาขาว่าง (≡ สนญ.) ห้ามถูก payload สาขาอื่นอ้าง ─────────
    // เดิม payload สาขา 8 อ้างแถวสาขาว่างได้ (UnspecifiedBranchRow) แล้ว IntegrationService.ProcessCustomerAsync
    // เขียน BranchCode = 00008 + ที่อยู่สาขาทับ ⇒ payload สำนักงานใหญ่ครั้งถัดไปสร้างแถวใหม่ และประวัติลูกหนี้ของ
    // สำนักงานใหญ่ค้างอยู่ที่แถวที่กลายเป็น "สาขา 8"

    [Fact]
    public void LegacyRowWithoutBranch_NotClaimedByBranch8Payload_CallerCreatesBranch8Row()
    {
        var m = ContactTaxBranchKey.Pick(new[] { C(Blank, Tin, null) }, Tin, "00008");
        Assert.False(m.Found);
        Assert.True(m.TaxIdExists);   // ⇒ ผู้เรียกห้ามถอยไปจับด้วยชื่อ/อีเมล (จะได้แถวว่างเดิมกลับมา) — สร้างแถวสาขา 8
    }

    [Fact]
    public void LegacyRowWithoutBranch_NotClaimedByBranchPayload_EvenWhenOnlyDigitsGiven()
    {
        var m = ContactTaxBranchKey.Pick(new[] { C(Blank, Tin, "  ") }, Tin, "8");
        Assert.False(m.Found);
        Assert.True(m.TaxIdExists);
    }

    [Fact]
    public void Sequence_Branch8ThenHq_HqStillOwnsLegacyRow()
    {
        // ลำดับจริงที่ฝ่ายค้านเล่า: สาขา 8 มาก่อน (ได้แถวใหม่) → สำนักงานใหญ่มาทีหลังต้องยังได้แถวเดิมที่ถือประวัติ
        var afterBranch8 = new[] { C(Blank, Tin, null), C(B8, Tin, "00008") };
        Assert.Equal(B8, ContactTaxBranchKey.Pick(afterBranch8, Tin, "00008").ContactId);
        var hq = ContactTaxBranchKey.Pick(afterBranch8, Tin, "00000");
        Assert.Equal(Blank, hq.ContactId);
        Assert.Equal(ContactKeyBasis.ExactBranch, hq.Basis);
        // payload ไม่ระบุสาขา → แถว สนญ./ไม่ระบุ ก่อน ไม่ใช่แถวสาขา 8
        Assert.Equal(Blank, ContactTaxBranchKey.Pick(afterBranch8, Tin, null).ContactId);
    }

    [Theory]
    [InlineData("00000")]
    [InlineData("0")]
    [InlineData("000")]
    public void LegacyRowWithoutBranch_StillClaimedByHqPayload_AnySpelling(string hqCode)
    {
        // ครึ่งที่ต้องยังถูก: payload สำนักงานใหญ่อ้างแถวว่างได้เหมือนเดิม (ไม่สร้างแถว สนญ. ซ้ำ)
        var m = ContactTaxBranchKey.Pick(new[] { C(Blank, Tin, null) }, Tin, hqCode);
        Assert.Equal(Blank, m.ContactId);
        Assert.Equal(ContactKeyBasis.ExactBranch, m.Basis);
        Assert.True(m.MayOverwriteBranch);   // เติม 00000 ให้แถวว่างได้ — ความหมายเดิม
    }

    // ───────── MayOverwriteBranch: เขียนรหัสสาขาของ payload ลงแถวที่จับได้ ได้เฉพาะเมื่อสาขาตรงแล้ว ─────────

    [Fact]
    public void MayOverwriteBranch_False_WhenMalformedBranchFellBackToHq()
    {
        // "8A" = ไม่รู้ → ได้แถว สนญ. แต่ ContactTypeResolver.NormalizeBranchCode("8A") = 00008 ⇒ ถ้าเขียน สนญ. กลายเป็นสาขา 8
        var m = ContactTaxBranchKey.Pick(new[] { C(Hq, Tin, "00000") }, Tin, "8A");
        Assert.Equal(Hq, m.ContactId);
        Assert.False(m.MayOverwriteBranch);
    }

    [Fact]
    public void MayOverwriteBranch_True_ForExactBranch_And_NonTaxMatch()
    {
        Assert.True(ContactTaxBranchKey.Pick(new[] { C(B8, Tin, "00008") }, Tin, "00008").MayOverwriteBranch);
        Assert.True(default(ContactKeyMatch).MayOverwriteBranch);   // จับด้วยชื่อ/อีเมล/สร้างใหม่ = พฤติกรรมเดิม
        Assert.False(ContactTaxBranchKey.Pick(new[] { C(B8, Tin, "00008") }, Tin, null).MayOverwriteBranch);
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

    // ───────── PickContact: ชุดนำเข้า/ change tracker (รอบ 193 ทีม C3) ─────────
    // เดิม CompetitorImportFramework + หน้าพรีวิวนำเข้าใช้ ToDictionaryAsync(c => c.TaxId) ⇒ เลขเดียวกันสองสาขา
    // (ถูกต้องตามประกาศฯ 199) = ArgumentException "same key" ทั้งไฟล์

    private static Contact Row(Guid id, string? tax, string? branch)
        => new() { Id = id, Name = "บริษัท เรดิสัน จำกัด", TaxId = tax, BranchCode = branch };

    [Fact]
    public void PickContact_TwoBranchesSameTaxId_NoThrow_ImportWithoutBranchGetsHq()
    {
        var rows = new List<Contact> { Row(B8, Tin, "00008"), Row(Hq, Tin, "00000") };
        // สาธิตว่าของเดิมพังจริง: ToDictionary ด้วยเลขภาษีโยนทันที
        Assert.Throws<ArgumentException>(() => rows.ToDictionary(c => c.TaxId!));
        Assert.Equal(Hq, ContactTaxBranchKey.PickContact(rows, Tin, branchCode: null)!.Id);
        Assert.Equal(B8, ContactTaxBranchKey.PickContact(rows, Tin, "00008")!.Id);
    }

    [Fact]
    public void PickContact_NoRowForBranch_ReturnsNull_SoCallerCreatesBranchRow()
    {
        var rows = new List<Contact> { Row(Hq, Tin, "00000") };
        Assert.Null(ContactTaxBranchKey.PickContact(rows, Tin, "00008"));
        Assert.Null(ContactTaxBranchKey.PickContact(new List<Contact>(), Tin, null));
        // ครึ่งที่ต้องยังถูก: ไฟล์ที่ไม่มีคอลัมน์สาขา กับเลขที่มีแถวเดียว ได้แถวนั้นเหมือน dictionary เดิม
        Assert.Equal(Hq, ContactTaxBranchKey.PickContact(rows, Tin, null)!.Id);
        Assert.Equal(Hq, ContactTaxBranchKey.PickContact(rows, "0-1055-51136-08-5", null)!.Id);
    }

    // ───────── ด่านกันชน /api/v1/contacts/sync: "เลขนี้เป็นของรหัสอื่น" ต้องเทียบเลข + สาขา ─────────
    // ContactsV1Controller.Sync ส่ง owners (ไม่รวมรหัสเดียวกัน) + สาขาที่มีผล (TaxBranchCode.Normalize) เข้า Pick

    [Fact]
    public void SyncOwnerCheck_Branch8OfSameEntity_IsNotOwnedByHqCode()
    {
        // ERP ส่ง สนญ. (รหัส A มีอยู่แล้ว) แล้วส่งสาขา 8 (รหัส B) — เดิมบล็อก CONTACT-TAXID-OWNED ที่รหัส B เสมอ
        var owners = new[] { C(Hq, Tin, "00000") };
        Assert.False(ContactTaxBranchKey.Pick(owners, Tin, TaxBranchCode.Normalize("00008")).Found);
    }

    [Fact]
    public void SyncOwnerCheck_SameBranchDifferentCode_StillOwned()
    {
        // ครึ่งที่ต้องยังถูก: รหัส B ส่ง สนญ. ของเลขที่รหัส A ถือ สนญ. อยู่ = คู่ค้าสองรายถือคีย์เดียวกัน ⇒ ยังบล็อก
        Assert.Equal(Hq, ContactTaxBranchKey.Pick(new[] { C(Hq, Tin, "00000") }, Tin, TaxBranchCode.Normalize(null)).ContactId);
        Assert.Equal(Blank, ContactTaxBranchKey.Pick(new[] { C(Blank, Tin, null) }, Tin, TaxBranchCode.Normalize("")).ContactId);
    }

    [Fact]
    public void EmptyTaxId_Nothing()
    {
        Assert.False(ContactTaxBranchKey.Pick(new[] { C(Hq, Tin, "00000") }, null, "00000").Found);
        Assert.False(ContactTaxBranchKey.Pick(new[] { C(Hq, Tin, "00000") }, "  ", null).TaxIdExists);
    }

    // ── รอบ 193 ฝ่ายค้าน C3: ขอบเขตการถอยไปจับด้วยอีเมล/เบอร์ (ที่พัก) ──

    [Fact]
    public void SoftMatch_NoTaxId_AnyRow_UnchangedBehaviour()
        => Assert.Equal(ContactSoftMatch.AnyRow, ContactTaxBranchKey.SoftMatchScope(null, default));

    [Fact]
    public void SoftMatch_TaxIdExistsOtherBranch_None()
    {
        var m = ContactTaxBranchKey.Pick(new[] { new ContactKeyCandidate(Guid.NewGuid(), "0105560113122", "00005") },
            "0105560113122", "00000");
        Assert.False(m.Found);
        Assert.True(m.TaxIdExists);
        Assert.Equal(ContactSoftMatch.None, ContactTaxBranchKey.SoftMatchScope("0105560113122", m));
    }

    [Fact]
    public void SoftMatch_NewTaxId_OnlyRowsWithoutTaxId()
    {
        var m = ContactTaxBranchKey.Pick(Array.Empty<ContactKeyCandidate>(), "0105560113122", "00000");
        Assert.Equal(ContactSoftMatch.RowsWithoutTaxId, ContactTaxBranchKey.SoftMatchScope("0105560113122", m));
    }

    [Fact]
    public void SoftMatch_FoundByKey_None()
    {
        var id = Guid.NewGuid();
        var m = ContactTaxBranchKey.Pick(new[] { new ContactKeyCandidate(id, "0105560113122", "00000") }, "0105560113122", "00000");
        Assert.Equal(id, m.ContactId);
        Assert.Equal(ContactSoftMatch.None, ContactTaxBranchKey.SoftMatchScope("0105560113122", m));
    }
}
