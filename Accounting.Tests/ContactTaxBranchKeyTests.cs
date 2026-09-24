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

    // ── รอบ 193 ทีม C3 หลังฝ่ายค้าน (review193-V-C3) ──

    // C-8: AuthService สร้างบริษัทที่สมัครใหม่ด้วย TaxId = "-" ⇒ เดิม "-" ถูกเทียบตรงตัว = ลูกค้ารายที่สองได้ผู้ติดต่อของรายแรก
    [Theory]
    [InlineData("-")]
    [InlineData(" - ")]
    [InlineData("N/A")]
    public void Placeholder_WithoutDigits_IsNoTaxId_NeverMatches(string placeholder)
    {
        var first = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var m = ContactTaxBranchKey.Pick(new[] { C(first, "-", null) }, placeholder, "00000");
        Assert.False(m.Found);
        Assert.False(m.TaxIdExists);
        Assert.False(ContactTaxBranchKey.HasTaxId(placeholder));
        // ไม่มีเลข ⇒ ถอยไปจับด้วยชื่อได้ทุกแถว (ความหมายเดียวกับ payload ที่ไม่ส่งเลข)
        Assert.Equal(ContactSoftMatch.AnyRow, ContactTaxBranchKey.SoftMatchScope(placeholder, m));
        Assert.Null(ContactTaxBranchKey.PickContact(new List<Contact> { Row(first, "-", null) }, placeholder, null));
    }

    [Fact]
    public void Placeholder_DoesNotBreakRealTaxIds()
    {
        // ครึ่งที่ต้องยังถูก: เลขจริงยังจับได้ (รวมแบบมีขีด) · เลขต่างประเทศที่มีตัวเลขยังเทียบตรงตัวได้
        Assert.True(ContactTaxBranchKey.HasTaxId(Tin));
        Assert.True(ContactTaxBranchKey.HasTaxId("0-1055-51136-08-5"));
        Assert.Equal(Hq, ContactTaxBranchKey.Pick(new[] { C(Hq, "0-1055-51136-08-5", "00000") }, Tin, "00000").ContactId);
        Assert.Equal(Other, ContactTaxBranchKey.Pick(new[] { C(Other, "DE123456789", null) }, "DE123456789", null).ContactId);
    }

    // C-6: หลังคีย์เลขภาษีไม่เจอ การถอยไปจับชื่อ/อีเมล (ชุดในหน่วยความจำ — ตัวเดียวกับ SoftScope บน IQueryable)
    [Fact]
    public void SoftScope_NewTaxId_ExcludesRowsHoldingAnotherTaxId()
    {
        var other = Row(Other, "0105500000001", "00000");      // นิติบุคคลอื่น ชื่อ/อีเมลเดียวกัน
        var noTax = Row(Blank, null, null);
        var dash = Row(Guid.Parse("00000000-0000-0000-0000-0000000000dd"), "-", null);
        var key = ContactTaxBranchKey.Pick(new[] { C(Other, "0105500000001", "00000") }, Tin, "00000");
        Assert.False(key.Found);
        Assert.False(key.TaxIdExists);   // เลขใหม่
        var scope = ContactTaxBranchKey.SoftScope(new List<Contact> { other, noTax, dash }, Tin, key).Select(c => c.Id).ToList();
        Assert.DoesNotContain(Other, scope);   // เดิม: ชื่อตรง ⇒ ได้แถวนี้ แล้ว integration เขียน Tin ทับเลขของรายนี้
        Assert.Contains(Blank, scope);
        Assert.Contains(dash.Id, scope);        // "-" = ยังไม่มีเลข
    }

    [Fact]
    public void SoftScope_NoTaxIdInPayload_AllRows_UnchangedBehaviour()
    {
        var rows = new List<Contact> { Row(Other, "0105500000001", "00000"), Row(Blank, null, null) };
        Assert.Equal(2, ContactTaxBranchKey.SoftScope(rows, null, default).Count());
        Assert.Equal(2, ContactTaxBranchKey.SoftScope(rows, "-", default).Count());
    }

    [Fact]
    public void SoftScope_TaxIdExistsOtherBranch_Empty()
    {
        var m = ContactTaxBranchKey.Pick(new[] { C(Hq, Tin, "00000") }, Tin, "00008");
        Assert.Empty(ContactTaxBranchKey.SoftScope(new List<Contact> { Row(Hq, Tin, "00000"), Row(Blank, null, null) }, Tin, m));
    }

    [Theory]
    [InlineData(null, Tin, true)]                       // แถวยังไม่มีเลข ⇒ เติมได้
    [InlineData("-", Tin, true)]                        // placeholder ⇒ เติมได้
    [InlineData("0-1055-51136-08-5", Tin, false)]       // เลขเดียวกันต่างรูปแบบ ⇒ ไม่ต้องแตะ
    [InlineData("0105500000001", Tin, false)]           // เลขของนิติบุคคลอื่น ⇒ ห้ามทับ (C-6)
    [InlineData(Tin, null, false)]                      // payload ไม่มีเลข ⇒ ไม่แตะ
    [InlineData(Tin, "-", false)]                       // payload "-" ⇒ ไม่แตะ (ห้ามล้างเลขจริงด้วย placeholder)
    public void AdoptTaxId_NeverOverwritesAnotherEntitysTaxId(string? existing, string? incoming, bool expected)
    {
        // เดิมล็อกผ่าน MayWriteTaxId (public) — ตอนนี้เป็น private ของ AdoptTaxId (ผู้เรียกภายนอกมีทางเดียว · dead_helper_check)
        var row = new Contact { Id = Blank, Name = "x", TaxId = existing };
        Assert.Equal(expected, ContactTaxBranchKey.AdoptTaxId(row, incoming, null, ContactMatchKind.Email) == ContactAdoptOutcome.Adopted);
        if (!expected) Assert.Equal(existing, row.TaxId);
    }

    // ── ฝ่ายค้านรอบสอง R2-C5 / R2-C9: "จับได้แล้วต้องเติมเลข" ตัวช่วยเดียวของทุกทางเข้า ──

    [Fact]
    public void AdoptTaxId_RowWithoutTaxId_GetsPayloadTaxIdAndBranch()
    {
        // แถวที่จับได้ด้วยชื่อ/อีเมล (ยังไม่มีเลข) — เดิม 4 ทางเข้าไม่เติม ⇒ ใบกำกับออกให้ผู้ซื้อไม่มีเลข + e-Tax ข้ามเงียบ
        var row = new Contact { Id = Blank, Name = "บริษัท เรดิสัน จำกัด", TaxId = null, BranchCode = null,
            ContactType = Accounting.Models.Enums.ContactType.Unknown };
        Assert.Equal(ContactAdoptOutcome.Adopted, ContactTaxBranchKey.AdoptTaxId(row, "0-1055-51136-08-5", "8", ContactMatchKind.Email));
        Assert.Equal(Tin, row.TaxId);                       // เก็บเป็นตัวเลขล้วน
        Assert.Equal(Accounting.Models.Enums.ContactType.JuristicPerson, row.ContactType);
        Assert.Equal("00008", row.BranchCode);
    }

    [Theory]
    [InlineData("-")]
    [InlineData("")]
    public void AdoptTaxId_PlaceholderRow_IsFilled_BranchDefaultsToHq(string placeholder)
    {
        var row = new Contact { Id = Blank, Name = "บริษัท เรดิสัน จำกัด", TaxId = placeholder };
        Assert.Equal(ContactAdoptOutcome.Adopted, ContactTaxBranchKey.AdoptTaxId(row, Tin, null, ContactMatchKind.Email));
        Assert.Equal(Tin, row.TaxId);
        Assert.Equal("00000", row.BranchCode);
    }

    [Fact]
    public void AdoptTaxId_NeverTouchesRowHoldingAnotherOrSameTaxId()
    {
        // ครึ่งที่ต้องยังถูก: แถวที่มีเลขแล้ว (อื่น/เดียวกัน) ไม่ถูกแตะ · payload ไม่มีเลข ไม่แตะ · null ไม่พัง
        var other = new Contact { Id = Other, Name = "x", TaxId = "0105500000001", BranchCode = "00003" };
        Assert.Equal(ContactAdoptOutcome.Keep, ContactTaxBranchKey.AdoptTaxId(other, Tin, "00008", ContactMatchKind.Email));
        Assert.Equal("0105500000001", other.TaxId);
        Assert.Equal("00003", other.BranchCode);
        var same = new Contact { Id = Hq, Name = "x", TaxId = "0-1055-51136-08-5", BranchCode = "00000" };
        Assert.Equal(ContactAdoptOutcome.Keep, ContactTaxBranchKey.AdoptTaxId(same, Tin, "00008", ContactMatchKind.Email));
        Assert.Equal("00000", same.BranchCode);
        var noTax = new Contact { Id = Blank, Name = "x", TaxId = null };
        Assert.Equal(ContactAdoptOutcome.Keep, ContactTaxBranchKey.AdoptTaxId(noTax, "-", null, ContactMatchKind.Email));
        Assert.Null(noTax.TaxId);
        Assert.Equal(ContactAdoptOutcome.Keep, ContactTaxBranchKey.AdoptTaxId(null, Tin, null, ContactMatchKind.Email));
    }

    [Fact]
    public void AdoptTaxId_Individual_DoesNotGetHeadOfficeSuffix()
    {
        // บุคคลธรรมดา (เลขบัตรขึ้นต้น 1) — ไม่มีโครงสาขา ⇒ ไม่เติม "00000" (ContactTypeResolver.BranchCodeFor)
        var first12 = "110554013452";
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (13 - i);
        var citizen = first12 + (char)('0' + (11 - sum % 11) % 10);
        var row = new Contact { Id = Blank, Name = "นาย ก", TaxId = null,
            ContactType = Accounting.Models.Enums.ContactType.Individual };
        Assert.Equal(ContactAdoptOutcome.Adopted, ContactTaxBranchKey.AdoptTaxId(row, citizen, null, ContactMatchKind.Email));
        Assert.Equal(citizen, row.TaxId);
        Assert.Null(row.BranchCode);
    }

    // ── ฝ่ายค้านรอบสาม R3-2 / B8: เติมเลขได้เฉพาะเมื่อจับด้วยชื่อตรงตัว · เลขต้องใช้ได้จริง · แถวลูกค้าทั่วไปไม่รับเลข ──

    [Fact]
    public void AdoptTaxId_FuzzyNameWithRealTaxId_Rejects_RowUntouched()
    {
        // ไฟล์/พาร์ตเนอร์ส่ง "ABC" + เลขของ ABC · ระบบจับได้ "ABC Trading" แบบ substring/คล้าย ⇒ ห้ามเขียนเลขลงแถวนั้น (สร้างใหม่แทน)
        var abcTrading = new Contact { Id = Other, Name = "ABC Trading", TaxId = null };
        Assert.Equal(ContactMatchKind.FuzzyName, ContactTaxBranchKey.NameMatchKind("ABC", abcTrading.Name));
        Assert.Equal(ContactAdoptOutcome.Reject,
            ContactTaxBranchKey.AdoptTaxId(abcTrading, Tin, null, ContactMatchKind.FuzzyName));
        Assert.Null(abcTrading.TaxId);
        Assert.Null(abcTrading.BranchCode);
    }

    [Fact]
    public void AdoptTaxId_FuzzyNameWithoutTaxId_KeepsRow_UnchangedBehaviour()
    {
        // ทิศตรงข้าม: payload ไม่มีเลข (หรือเลขใช้ไม่ได้) ⇒ ผูกด้วยชื่อคล้ายได้ตามเดิม ไม่ถดถอย
        var row = new Contact { Id = Other, Name = "ABC Trading", TaxId = null };
        Assert.Equal(ContactAdoptOutcome.Keep, ContactTaxBranchKey.AdoptTaxId(row, null, null, ContactMatchKind.FuzzyName));
        Assert.Equal(ContactAdoptOutcome.Keep, ContactTaxBranchKey.AdoptTaxId(row, "-", null, ContactMatchKind.FuzzyName));
        Assert.Equal(ContactAdoptOutcome.Keep, ContactTaxBranchKey.AdoptTaxId(row, "0000000000000", null, ContactMatchKind.FuzzyName));
        Assert.Null(row.TaxId);
    }

    [Theory]
    [InlineData("บริษัท เรดิสัน จำกัด", "เรดิสัน")]
    [InlineData("บริษัท  เรดิสัน  จำกัด (มหาชน)", "บมจ. เรดิสัน")]
    [InlineData("Radisson Co., Ltd.", "RADISSON COMPANY LIMITED")]
    [InlineData("หจก. แอม แฮปปี้เนส", "ห้างหุ้นส่วนจำกัด แอมแฮปปี้เนส")]
    public void NameMatchKind_SameNameAfterNormalize_IsExact(string given, string stored)
        => Assert.Equal(ContactMatchKind.ExactName, ContactTaxBranchKey.NameMatchKind(given, stored));

    [Theory]
    [InlineData("ABC", "ABC Trading")]
    [InlineData("แอม แฮปปี้", "หจก. แอม แฮปปี้เนส")]            // ชื่อถูกตัด ≠ ชื่อเดียวกัน (กฎ #4 H)
    [InlineData("Radisson", "เรดิสัน")]                         // ข้ามภาษา = คล้าย ไม่ใช่ตรงตัว
    [InlineData("", "")]
    public void NameMatchKind_PartialOrCrossLanguage_IsFuzzy(string given, string stored)
        => Assert.Equal(ContactMatchKind.FuzzyName, ContactTaxBranchKey.NameMatchKind(given, stored));

    [Theory]
    [InlineData("0000000000000")]          // ค่ามาตรฐาน "ลูกค้าทั่วไป" ของ POS หลายเจ้า
    [InlineData("0105551136086")]          // checksum ผิด (หลักสุดท้ายของ Tin คือ 5)
    public void AdoptTaxId_InvalidChecksumOrZeros_NotAdopted(string bad)
    {
        var row = new Contact { Id = Blank, Name = "x", TaxId = null };
        Assert.Equal(ContactAdoptOutcome.Keep, ContactTaxBranchKey.AdoptTaxId(row, bad, null, ContactMatchKind.ExactName));
        Assert.Null(row.TaxId);
    }

    [Fact]
    public void AdoptTaxId_WalkInRow_NeverTakesTaxId()
    {
        var walkIn = new Contact { Id = Blank, Name = "ลูกค้าทั่วไป", TaxId = null, IsWalkInCustomer = true };
        // ผู้ซื้อที่มีเลขจริงไม่ใช่ลูกค้าทั่วไป ⇒ ห้ามใช้แถวกลาง (สร้างแถวใหม่)
        Assert.Equal(ContactAdoptOutcome.Reject, ContactTaxBranchKey.AdoptTaxId(walkIn, Tin, null, ContactMatchKind.ExactName));
        // "0000000000000"/ไม่มีเลข ⇒ ยังเป็นลูกค้าทั่วไปได้ตามเดิม แต่ไม่รับเลขปลอม
        Assert.Equal(ContactAdoptOutcome.Keep, ContactTaxBranchKey.AdoptTaxId(walkIn, "0000000000000", null, ContactMatchKind.ExactName));
        Assert.Equal(ContactAdoptOutcome.Keep, ContactTaxBranchKey.AdoptTaxId(walkIn, null, null, ContactMatchKind.ExactName));
        Assert.Null(walkIn.TaxId);
    }

    [Fact]
    public void AdoptTaxId_ExactNameWithRealTaxId_StillAdopts()
    {
        // ครึ่งที่ต้องยังถูก (R2-C5): ชื่อตรงตัว/อีเมล + เลขใช้ได้ ⇒ เติมเลขตามเดิม
        var row = new Contact { Id = Blank, Name = "บริษัท เรดิสัน จำกัด", TaxId = null };
        Assert.Equal(ContactAdoptOutcome.Adopted, ContactTaxBranchKey.AdoptTaxId(row, Tin, null,
            ContactTaxBranchKey.NameMatchKind("เรดิสัน จำกัด", row.Name)));
        Assert.Equal(Tin, row.TaxId);
    }
}
