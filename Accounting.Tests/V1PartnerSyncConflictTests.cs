using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ด่านกันข้อมูลขาเข้าจาก `/api/v1` ทับของเดิม / สร้างซ้ำเงียบ ๆ**
/// (DECISION_AUDIT_2026-09-18 §9.3 D-2 — "ด่านต้องมาก่อน migration เสมอ")
///
/// <para>สองครึ่งตาม G7: ครึ่งแรกพิสูจน์ว่า<b>การชนถูกจับได้และบอกว่าชนกับอะไร</b> ·
/// ครึ่งหลังพิสูจน์ว่า<b>การ sync ปกติไม่ถูกแตะ</b> — ด่านที่ฟ้องทุกแถว
/// = ปิดทางเข้าโดยไม่ตั้งใจ (CLAUDE.md F2 ข้อ 8)</para>
/// </summary>
public class V1PartnerSyncConflictTests
{
    private static string WithCheckDigit(string first12)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (first12[i] - '0') * (13 - i);
        return first12 + (char)('0' + (11 - sum % 11) % 10);
    }

    private static readonly string TaxA = WithCheckDigit("010554013452");
    private static readonly string TaxB = WithCheckDigit("010554013461");
    private static readonly string Citizen = WithCheckDigit("110554013452");

    // ═══ ครึ่งที่ 1 — การชนต้องถูกจับได้ และบอกว่าชนกับอะไร ═══

    [Fact]
    public void เลขผู้เสียภาษีที่คู่ค้ารายอื่นถืออยู่แล้ว_ต้องถูกปฏิเสธพร้อมชี้ตัว()
    {
        var v = PartnerSyncConflict.CheckContact(
            externalId: "V-002", incomingTaxId: TaxA, existingTaxId: null,
            taxIdOwnerExternalId: "V-001", taxIdOwnerName: "บริษัท เอ จำกัด");

        Assert.True(v.IsBlocked);
        Assert.Equal("CONTACT-TAXID-OWNED", v.Code);
        Assert.Contains("V-001", v.Message);              // ชนกับอะไร
        Assert.Contains("บริษัท เอ จำกัด", v.Message);
        Assert.Contains("contacts/map", v.Message);       // ทางไปต่อของผู้ใช้
    }

    [Fact]
    public void เปลี่ยนเลขที่_checksum_ผ่านอยู่แล้ว_ต้องหยุดและบอกวิธียืนยัน()
    {
        var v = PartnerSyncConflict.CheckContact(
            externalId: "V-001", incomingTaxId: TaxB, existingTaxId: TaxA,
            taxIdOwnerExternalId: null, taxIdOwnerName: null);

        Assert.True(v.IsBlocked);
        Assert.Equal("CONTACT-TAXID-CHANGED", v.Code);
        Assert.Contains(PartnerSyncConflict.OverrideFlagName, v.Message);
    }

    [Fact]
    public void ยืนยันแล้วต้องเปลี่ยนได้_ด่านต้องมีทางออกเสมอ()
    {
        var v = PartnerSyncConflict.CheckContact(
            externalId: "V-001", incomingTaxId: TaxB, existingTaxId: TaxA,
            taxIdOwnerExternalId: null, taxIdOwnerName: null, allowTaxIdChange: true);

        Assert.False(v.IsBlocked);
    }

    [Fact]
    public void ส่งเลขที่ใช้ไม่ได้มาทับเลขที่ใช้ได้_เตือนแต่ไม่ล้างของเดิม()
    {
        var v = PartnerSyncConflict.CheckContact(
            externalId: "V-001", incomingTaxId: "12345", existingTaxId: TaxA,
            taxIdOwnerExternalId: null, taxIdOwnerName: null);

        Assert.Equal(PartnerConflictLevel.Warning, v.Level);
        Assert.False(v.IsBlocked);
        Assert.Contains(ThaiTaxId.Normalize(TaxA), v.Message);
    }

    [Fact]
    public void ชนิดที่ประกาศขัดกับรูปเลข_ต้องเตือนแต่ไม่บล็อก()
    {
        var v = PartnerSyncConflict.CheckDeclaredType("V-001", ContactType.Individual, TaxA);
        Assert.Equal(PartnerConflictLevel.Warning, v.Level);
        Assert.Equal("CONTACT-TYPE-VS-TAXID", v.Code);
        Assert.Contains("ภ.ง.ด.3/53", v.Message);
    }

    [Fact]
    public void เอกสารที่เลขใบกำกับผู้ขายซ้ำ_ต้องถูกปฏิเสธพร้อมชี้ใบเดิม()
    {
        var existing = Guid.NewGuid();
        var v = PartnerSyncConflict.CheckDocument("INV-2569-001", "PI-20260901-0007", existing);

        Assert.True(v.IsBlocked);
        Assert.Equal("DOC-SUPPLIER-INVOICE-DUPLICATE", v.Code);
        Assert.Contains("PI-20260901-0007", v.Message);
        Assert.Contains(existing.ToString(), v.Message);
    }

    // ═══ ครึ่งที่ 2 — การ sync ปกติต้องไม่ถูกแตะ ═══

    [Fact]
    public void ยิงชุดเดิมซ้ำ_เลขเดิมทุกอย่าง_ต้องผ่านเงียบ()
    {
        var v = PartnerSyncConflict.CheckContact(
            externalId: "V-001", incomingTaxId: TaxA, existingTaxId: TaxA,
            taxIdOwnerExternalId: null, taxIdOwnerName: null);
        Assert.False(v.HasIssue);
    }

    [Fact]
    public void เจ้าของเลขคือแถวเดียวกัน_ไม่ถือว่าชน()
    {
        // ผู้เรียกกรอง ExternalId ของตัวเองออกก่อนแล้ว — ที่นี่ล็อกว่า null = ไม่ชน
        var v = PartnerSyncConflict.CheckContact(
            externalId: "V-001", incomingTaxId: TaxA, existingTaxId: TaxA,
            taxIdOwnerExternalId: null, taxIdOwnerName: null);
        Assert.Equal(PartnerConflictLevel.None, v.Level);
    }

    [Fact]
    public void คู่ค้าใหม่ที่ยังไม่มีเลขภาษี_ต้องไม่ถูกกัน()
    {
        var v = PartnerSyncConflict.CheckContact(
            externalId: "V-009", incomingTaxId: null, existingTaxId: null,
            taxIdOwnerExternalId: null, taxIdOwnerName: null);
        Assert.False(v.HasIssue);
    }

    [Fact]
    public void เติมเลขภาษีให้แถวที่เดิมไม่มี_ต้องทำได้โดยไม่ต้องยืนยัน()
    {
        var v = PartnerSyncConflict.CheckContact(
            externalId: "V-001", incomingTaxId: TaxA, existingTaxId: null,
            taxIdOwnerExternalId: null, taxIdOwnerName: null);
        Assert.False(v.HasIssue);
    }

    [Fact]
    public void ราชการที่ถือเลขขึ้นต้นศูนย์_ไม่ถือว่าขัดกับรูปเลข()
    {
        // ถ้าถือว่าขัด หน่วยงานราชการทุกแห่งจะติดคำเตือนทุกรอบ sync = ปิดด่านโดยไม่ตั้งใจ
        var v = PartnerSyncConflict.CheckDeclaredType("G-001", ContactType.GovernmentAgency, TaxA);
        Assert.Equal(PartnerConflictLevel.None, v.Level);
    }

    [Fact]
    public void ชนิดตรงกับรูปเลข_หรือยังไม่รู้ชนิด_ต้องไม่เตือน()
    {
        Assert.False(PartnerSyncConflict.CheckDeclaredType("V-001", ContactType.JuristicPerson, TaxA).HasIssue);
        Assert.False(PartnerSyncConflict.CheckDeclaredType("V-002", ContactType.Individual, Citizen).HasIssue);
        Assert.False(PartnerSyncConflict.CheckDeclaredType("V-003", ContactType.Unknown, TaxA).HasIssue);
        // ไม่มีเลขให้เทียบ = เทียบไม่ได้ ⇒ เงียบ (ไม่ใช่เตือนทุกแถว)
        Assert.False(PartnerSyncConflict.CheckDeclaredType("V-004", ContactType.JuristicPerson, null).HasIssue);
    }

    [Fact]
    public void เอกสารที่ไม่มีเลขใบกำกับผู้ขาย_หรือยังไม่เคยมีใบเดิม_ต้องผ่าน()
    {
        Assert.False(PartnerSyncConflict.CheckDocument(null, null, null).HasIssue);
        Assert.False(PartnerSyncConflict.CheckDocument("  ", null, null).HasIssue);
        Assert.False(PartnerSyncConflict.CheckDocument("INV-001", null, null).HasIssue);
    }
}
