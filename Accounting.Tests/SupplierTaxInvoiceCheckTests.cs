using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations.Tax;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 190 ทีม C · ข้อ 3 ของเจ้าของ — หน้าใบสำคัญจ่าย "มีใบกำกับภาษีซื้อ": ช่องเลขผู้เสียภาษีผู้ขายขึ้น
/// <c>0765560001108</c> (จากผู้ติดต่อ) แต่กล่องแดงบอก "ขาด: เลขผู้เสียภาษีผู้ขาย (จาก contact) ระบบจะพักภาษีซื้อ
/// ไว้ที่ 11640"
///
/// <para>ต้นเหตุ: ตัวตรวจ JS อ่าน "ช่องบนฟอร์ม" ณ เวลาที่ช่องยังว่าง แล้วไม่เคยตรวจใหม่ ส่วนตัวลงบัญชีตอนอนุมัติ
/// อ่าน "ผู้ติดต่อในฐาน" ผ่าน <see cref="TaxInvoiceCompletenessChecker.Evaluate"/>. ตอนนี้หน้าจอถามเซิร์ฟเวอร์
/// (ตัวตรวจตัวเดียวกัน) — เทสต์นี้ล็อกคำตอบของตัวตรวจนั้นด้วยตัวเลขจากเคสจริง ทั้งสองทิศ:
/// ใบที่ครบต้องได้ "เคลมได้" (กล่องต้องเงียบ) และใบที่ไม่ครบต้องบอกให้ถูกว่าแก้ที่ไหน</para>
///
/// <para>ครึ่งหลัง: <see cref="InputVatParkingNotice"/> — คำเตือนก่อนอนุมัติเมื่อภาษีซื้อจะถูกพัก 11640 ทั้งที่
/// ผู้ใช้ติ๊กขอเคลม (เดิมพักเงียบ) และต้อง<b>ไม่</b>เตือนใบที่ครบ/ใบที่ไม่ได้ประกาศเคลม (ทางอนุมัติอัตโนมัติ
/// ของใบเบิก/รายการประจำต้องไม่ล้มเพราะคำเตือนใหม่)</para>
/// </summary>
public class SupplierTaxInvoiceCheckTests
{
    // ผู้ขายของเคสจริง — เลข 0765560001108 (checksum ผ่าน) + ที่อยู่ครบ
    private static Contact Supplier(string? taxId = "0765560001108", string? address = "99 หมู่ 1 ต.ชะอำ อ.ชะอำ จ.เพชรบุรี 76120",
        string? name = "บริษัท ตัวอย่างผู้ขาย จำกัด", string? branch = "00000")
        => new() { Name = name!, TaxId = taxId, Address = address, BranchCode = branch,
                   ContactType = ContactType.JuristicPerson, IsSupplier = true };

    private static Document Pv(string? invoiceNo = "IV6909-0012", DateTime? date = null, string? branch = "00000")
        => new()
        {
            DocumentType = DocumentType.PaymentVoucher,
            HasTaxInvoiceReference = true,
            SupplierInvoiceNumber = invoiceNo,
            SupplierTaxInvoiceDate = date ?? new DateTime(2026, 9, 12),
            SupplierBranchCode = branch,
        };

    // ───────── ครึ่งแรก: ใบที่ "ถูกอยู่แล้ว" ต้องได้คำตอบว่าเคลมได้ (กล่องแดงต้องไม่ขึ้น) ─────────

    [Fact]
    public void OwnerCase_ContactHasTaxId_IsClaimable_NothingMissing()
    {
        var r = TaxInvoiceCompletenessChecker.Evaluate(Pv(), Supplier());
        Assert.True(r.IsClaimable);
        Assert.Empty(r.MissingFields);
        Assert.Empty(r.MissingContactFields);
    }

    [Fact]
    public void BranchOfSupplier_NotHeadOffice_StillClaimable()
    {
        // ใบของสาขาที่ 8 — สาขาบนใบเป็นข้อมูลของใบ ไม่ใช่เหตุให้พัก
        var r = TaxInvoiceCompletenessChecker.Evaluate(Pv(branch: "00008"), Supplier(branch: "00008"));
        Assert.True(r.IsClaimable);
    }

    // ───────── ครึ่งหลัง: ใบที่ไม่ครบ ต้องบอก "ขาดอะไร · แก้ที่ไหน" ให้ตรง ─────────

    [Fact]
    public void ContactWithoutTaxId_MissingAtContact()
    {
        var r = TaxInvoiceCompletenessChecker.Evaluate(Pv(), Supplier(taxId: null));
        Assert.False(r.IsClaimable);
        Assert.Contains(r.MissingContactFields, f => f.StartsWith("เลขผู้เสียภาษีผู้ขาย"));
        Assert.Contains(r.MissingFields, f => f.StartsWith("เลขผู้เสียภาษีผู้ขาย"));
    }

    [Fact]
    public void ContactTaxIdBadChecksum_MissingAtContact()
    {
        // 13 หลักแต่หลักตรวจผิด — เดิม JS นับว่า "มีค่า" แล้วเงียบ ทั้งที่ลงบัญชีพัก 11640
        var r = TaxInvoiceCompletenessChecker.Evaluate(Pv(), Supplier(taxId: "0765560001109"));
        Assert.False(r.IsClaimable);
        Assert.Contains(r.MissingContactFields, f => f.StartsWith("เลขผู้เสียภาษีผู้ขาย"));
    }

    [Fact]
    public void ContactWithoutAddress_MissingAtContact_JsNeverCheckedThis()
    {
        var r = TaxInvoiceCompletenessChecker.Evaluate(Pv(), Supplier(address: null));
        Assert.False(r.IsClaimable);
        Assert.Equal(new[] { "ที่อยู่ผู้ขาย" }, r.MissingContactFields);
    }

    [Fact]
    public void DocumentFieldsMissing_NotBlamedOnContact()
    {
        var doc = Pv(invoiceNo: null);
        doc.SupplierTaxInvoiceDate = null;
        var r = TaxInvoiceCompletenessChecker.Evaluate(doc, Supplier());
        Assert.False(r.IsClaimable);
        Assert.Equal(2, r.MissingFields.Count);
        Assert.Empty(r.MissingContactFields);   // แก้บนฟอร์ม ไม่ใช่ที่ผู้ติดต่อ
    }

    // ───────── InputVatParkingNotice — คำเตือนก่อนอนุมัติ ─────────

    private static readonly string[] NoAddress = { "ที่อยู่ผู้ขาย" };

    private static string? Notice(bool declared = true, bool foreign = false, string? ovr = null,
        decimal vat = 119.35m, string[]? missing = null)
    {
        var m = missing ?? Array.Empty<string>();
        return InputVatParkingNotice.Build(
            isPurchaseInputVatType: true, isForeignService: foreign, inputVatAccountCodeOverride: ovr,
            claimableVat: vat, claimIntentDeclared: declared,
            missingFields: m, missingContactFields: m.Where(x => x == "ที่อยู่ผู้ขาย").ToList());
    }

    [Fact]
    public void Notice_DeclaredClaimButIncomplete_WarnsWithAmountAndWhereToFix()
    {
        var w = Notice(missing: NoAddress);
        Assert.NotNull(w);
        Assert.Contains("119.35", w);
        Assert.Contains("11640", w);
        Assert.Contains("ข้อมูลผู้ติดต่อ: ที่อยู่ผู้ขาย", w);
    }

    [Fact]
    public void Notice_CompleteInvoice_Silent()
        => Assert.Null(Notice());   // ใบที่ครบ = ห้ามเตือน (คำเตือนที่ฟ้องใบถูก = ปิดด่านโดยไม่ตั้งใจ)

    [Fact]
    public void Notice_ClaimNotDeclared_Silent_SoAutoApprovalPathsDoNotBreak()
        // ใบสำคัญจ่ายจากใบเบิกพนักงาน (อนุมัติอัตโนมัติ ไม่ยืนยันคำเตือน) — ต้องไม่ล้มเพราะคำเตือนนี้
        => Assert.Null(Notice(declared: false, missing: NoAddress));

    [Fact]
    public void Notice_OverrideOrForeignOrNoVat_Silent()
    {
        Assert.Null(Notice(ovr: "51000", missing: NoAddress));     // ผู้ใช้เลือกผังเอง
        Assert.Null(Notice(foreign: true, missing: NoAddress));    // ภ.พ.36 มีเส้นของตัวเอง
        Assert.Null(Notice(vat: 0m, missing: NoAddress));          // ไม่มียอดให้พัก
    }

    [Fact]
    public void Notice_NotPurchaseType_Silent()
        => Assert.Null(InputVatParkingNotice.Build(false, false, null, 70m, true,
            new[] { "ที่อยู่ผู้ขาย" }, Array.Empty<string>()));
}
