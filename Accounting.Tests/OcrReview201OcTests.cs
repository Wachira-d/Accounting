using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 201 ทีม OC — งานคงค้าง OCR (<c>erp-review/2026-10-01/BACKLOG.md</c> §1.7 A-OC1..A-OC5 + หมวด C-18..C-24 · คำตัดสินข้อ 91–97)
///
/// <para>ทุกหัวข้อมีสองครึ่ง (CLAUDE.md §H / F2 ข้อ 8): ครึ่งที่พิสูจน์ว่าเคสที่พังกลับมาถูก และครึ่งที่พิสูจน์ว่าเคสที่ถูกอยู่แล้ว<b>ไม่ถูกแตะ</b> —
/// เทสต์ที่มีแต่ครึ่งแรกผ่านได้ทั้งตอนแก้ถูกและตอน "ปิดด่านทิ้ง"</para>
/// </summary>
public class OcrReview201OcTests
{
    // ═══ A-OC1 — known-good ชื่อ/ที่อยู่ผู้ขาย: จำเฉพาะเมื่อค่าเปลี่ยนจากที่สแกน ═══

    private const string MakroName = "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)";
    private const string MakroBranch5Address = "88/8 หมู่ 3 ต.บ้านสวน อ.เมืองชลบุรี จ.ชลบุรี 20000";

    [Fact]
    public void KnownGood_WebEchoesScannedName_IsNotRemembered()
    {
        // หน้ารีวิวส่ง vendorName กลับมาทุกครั้งที่ช่องมีค่า — ส่งค่าเดิม = ไม่ได้แก้ ⇒ ห้ามบันทึกเป็น UserCorrection (ชนะ Azure ถาวร)
        Assert.False(OcrCorrectedFieldList.ShouldRememberKnownGood(MakroName, MakroName));
        // hydrate ลง input แล้วส่งกลับ (ช่องว่างหัวท้าย/ช่องว่างซ้อน) ≠ แก้
        Assert.False(OcrCorrectedFieldList.ShouldRememberKnownGood("  บริษัท ซีพี  แอ็กซ์ตร้า จำกัด (มหาชน) ", MakroName));
        Assert.False(OcrCorrectedFieldList.ShouldRememberKnownGood(MakroBranch5Address, MakroBranch5Address));
    }

    [Fact]
    public void KnownGood_UserReallyFixesTheName_IsRemembered()
    {
        // OCR อ่าน "แอ็กซ์ตร้า" เป็น "แอ็กซตรา" แล้วผู้ใช้พิมพ์แก้ ⇒ จำ (ทิศตรงข้าม — ด่านต้องไม่ปิดการเรียนรู้ทิ้ง)
        Assert.True(OcrCorrectedFieldList.ShouldRememberKnownGood(MakroName, "บริษัท ซีพี แอ็กซตรา จำกัด (มหาชน)"));
        // สแกนไม่มีค่าเดิม แล้วผู้ใช้พิมพ์เอง = คำแก้
        Assert.True(OcrCorrectedFieldList.ShouldRememberKnownGood(MakroBranch5Address, null));
    }

    [Fact]
    public void KnownGood_ClearedOrUntouched_IsNotRemembered()
    {
        Assert.False(OcrCorrectedFieldList.ShouldRememberKnownGood(null, MakroName));   // ไม่ได้แตะ
        Assert.False(OcrCorrectedFieldList.ShouldRememberKnownGood("", MakroName));     // ล้างช่อง = ไม่มีค่าให้จำ
        Assert.False(OcrCorrectedFieldList.ShouldRememberKnownGood("   ", MakroName));
    }

    // ═══ A-OC4 — ตัวตัดสินที่ย้ายออกจาก OcrService (ตรงตัว) ═══

    [Fact]
    public void CreditNoteReason_ReturnWinsAndIsReadFromThePaper()
    {
        const string paper = "ใบลดหนี้ / CREDIT NOTE\nเลขที่ CN6809-0012\nอ้างถึงใบกำกับภาษีเลขที่ IV6809-0451\n"
            + "สาเหตุ: รับคืนสินค้าชำรุด 2 ชิ้น\nมูลค่าตามใบกำกับเดิม 10,700.00\nส่วนลด 0.00\nผลต่าง 2,000.00";
        // "คืนสินค้า" กระทบสต็อก จึงตัดสินก่อน — แม้หน้ามีคำว่า "ส่วนลด" (แถวฟอร์ม 0.00) ก็ยังเป็นคืนสินค้า
        Assert.Equal(CreditNoteReason.Return, OcrCreditNoteReasonReader.Read(paper));
        Assert.Equal(CreditNoteReason.Return, OcrCreditNoteReasonReader.Read("CREDIT NOTE · Reason: Sales Return"));
    }

    [Theory]
    [InlineData("ใบลดหนี้\nเหตุผล: ลดราคาสินค้าตามโปรโมชั่น", CreditNoteReason.Discount)]
    [InlineData("ใบลดหนี้\nเหตุผล: ตัดหนี้สูญบางส่วนตามมติกรรมการ", CreditNoteReason.Writeoff)]
    [InlineData("ใบลดหนี้\nเหตุผล: ปรับปรุงยอดเนื่องจากคิดราคาคลาดเคลื่อน", CreditNoteReason.Adjustment)]
    [InlineData("CREDIT NOTE\nReason: price adjustment", CreditNoteReason.Adjustment)]
    public void CreditNoteReason_EachClosedListReason(string paper, CreditNoteReason expected)
        => Assert.Equal(expected, OcrCreditNoteReasonReader.Read(paper));

    [Fact]
    public void CreditNoteReason_NoClearReason_StaysUnknown()
    {
        // ไม่เดา — ผู้ใช้เลือกเองบนฟอร์ม (ทิศตรงข้าม: ตัวอ่านต้องไม่ตอบทุกใบ)
        Assert.Null(OcrCreditNoteReasonReader.Read("ใบลดหนี้\nเลขที่ CN-001\nอ้างถึง IV-001\nผลต่าง 500.00"));
        Assert.Null(OcrCreditNoteReasonReader.Read(null));
        Assert.Null(OcrCreditNoteReasonReader.Read("   "));
    }

    [Fact]
    public void PaperDocumentType_AzureTypesPassThroughWhenPaperIsSilent()
    {
        Assert.Equal("CreditNote", OcrPaperDocumentType.FromAzure("creditnote", "prebuilt-invoice", "ACME\nTotal 100.00"));
        Assert.Equal("Invoice", OcrPaperDocumentType.FromAzure("unknown-type", "prebuilt-invoice", null));
    }

    // ═══ A-OC2 — แถวสาขาที่ OCR สร้างแล้วไม่มีอะไรอ้าง ═══

    private static readonly Guid RowWrongCode = Guid.Parse("20100000-0000-0000-0000-000000000001");
    private static readonly Guid RowUsed = Guid.Parse("20100000-0000-0000-0000-000000000002");
    private static readonly Guid RowManual = Guid.Parse("20100000-0000-0000-0000-000000000003");
    private const string CpTin = "0105555123456";

    [Fact]
    public void OrphanOcrBranchRows_ReportsOnlyUnreferencedOcrBranchRows()
    {
        var t0 = new DateTime(2026, 9, 30, 3, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            // ผู้ใช้พิมพ์ 00050 ผิดแทน 00005 แล้วแก้กลับ ⇒ แถว 00050 ค้าง (R4)
            new ContactKeyRow(RowWrongCode, MakroName, CpTin, "00050", ContactDataHygiene.OcrBranchAutoCreateTag, t0),
            // แถวสาขาที่มีเอกสารอ้าง = ของจริง ห้ามรายงาน
            new ContactKeyRow(RowUsed, MakroName, CpTin, "00005", ContactDataHygiene.OcrBranchAutoCreateTag, t0),
            // แถวที่คนสร้าง (ไม่ใช่ OCR) ห้ามรายงาน แม้ไม่มีอะไรอ้าง
            new ContactKeyRow(RowManual, MakroName, CpTin, "00007", "user@example.com", t0),
        };
        var orphans = ContactDataHygiene.OrphanOcrBranchRows(rows, new HashSet<Guid> { RowUsed });
        Assert.Equal(new[] { RowWrongCode }, orphans.Select(r => r.Id).ToArray());
    }

    [Fact]
    public void OrphanRetireBlock_ServerRechecksTagAndReferences()
    {
        Assert.Null(ContactDataHygiene.OrphanRetireBlock(ContactDataHygiene.OcrBranchAutoCreateTag, referenced: false));
        Assert.Contains("อ้างถึง", ContactDataHygiene.OrphanRetireBlock(ContactDataHygiene.OcrBranchAutoCreateTag, referenced: true));
        // แถวที่ OCR แค่เติมข้อมูล (OCR-Enrich) หรือคนสร้าง ⇒ ลบจากหน้านี้ไม่ได้
        Assert.NotNull(ContactDataHygiene.OrphanRetireBlock("OCR-Enrich", referenced: false));
        Assert.NotNull(ContactDataHygiene.OrphanRetireBlock(null, referenced: false));
        Assert.NotNull(ContactDataHygiene.OrphanRetireBlock("OCR-DBD-AutoCreate", referenced: false));   // แถวทะเบียน (ไม่ใช่แถวสาขาที่สแกนสร้าง)
    }

    // ═══ C-18 — คำแก้ WHT ก่อนกติกา baseline (K-10) ไม่นับเป็นหลักฐาน ═══

    [Fact]
    public void WhtLearning_CorrectionsBeforeBaseline_AreUnknown_NotLearned()
    {
        var d = OcrWhtLearningScope.Decide(hasScan: true, paperShowsWht: false,
            userCorrectedFields: "HasWht,VendorBranchCode,VendorName", userCorrectionsPredateBaseline: true);
        Assert.False(d.Learn);
        Assert.Equal(WhtLearningEvidence.UserEditedBeforeBaseline, d.Evidence);
        Assert.Contains("แยกไม่ได้", OcrWhtLearningScope.Explain(d.Evidence));
    }

    [Fact]
    public void WhtLearning_OppositeDirection_PaperAndPostBaselineEditsStillLearn()
    {
        // กระดาษพิมพ์ส่วนหักเอง = หลักฐานชั้นดีที่สุด ไม่ขึ้นกับธงคำแก้
        Assert.Equal((true, WhtLearningEvidence.Paper),
            OcrWhtLearningScope.Decide(true, paperShowsWht: true, "HasWht", userCorrectionsPredateBaseline: true));
        // คำแก้หลังกติกา baseline = คนแก้จริง
        Assert.Equal((true, WhtLearningEvidence.UserEdited),
            OcrWhtLearningScope.Decide(true, paperShowsWht: false, "HasWht", userCorrectionsPredateBaseline: false));
        // เอกสารคีย์มือ (ไม่มีสแกน) ไม่เกี่ยว
        Assert.Equal((true, WhtLearningEvidence.NoScan),
            OcrWhtLearningScope.Decide(false, paperShowsWht: false, null, userCorrectionsPredateBaseline: true));
        // ไม่มีชื่อช่อง WHT ในรายการแก้ = ข้อเสนอของระบบเอง (กติกาเดิม)
        Assert.Equal((false, WhtLearningEvidence.SystemSuggestedOnly),
            OcrWhtLearningScope.Decide(true, paperShowsWht: false, "VendorName", userCorrectionsPredateBaseline: true));
    }

    [Fact]
    public void WhtBaselineMigration_FlagsExistingRowsOnlyInTheStepThatCreatesTheColumn()
    {
        var sql = DatabaseMigrationHelper.WhtCorrectionsPredateBaselineMigrationSql();
        var guard = sql.IndexOf("information_schema.columns", StringComparison.Ordinal);
        var ret = sql.IndexOf("RETURN;", StringComparison.Ordinal);
        var add = sql.IndexOf("ADD COLUMN \"WhtCorrectionsPredateBaseline\"", StringComparison.Ordinal);
        var upd = sql.IndexOf("UPDATE \"OcrScanResults\" SET \"WhtCorrectionsPredateBaseline\" = true", StringComparison.Ordinal);
        Assert.True(guard >= 0 && ret > guard && add > ret && upd > add,
            "ต้องตรวจคอลัมน์ก่อน (มีแล้ว = ออก) แล้วจึงสร้าง + ตีธงในบล็อกเดียว — UPDATE ที่รันทุกบูตจะตีธงแถวที่แก้หลังกติกาไปด้วย");
        Assert.Contains("\"UserCorrectedAt\" IS NOT NULL", sql);
        Assert.DoesNotContain("IF NOT EXISTS", sql);   // ADD COLUMN IF NOT EXISTS + UPDATE แยก = กลับไปเป็นบั๊กที่ว่า
    }

    // ═══ C-19 — สลิป/ใบร้านค้าที่ไม่มีบล็อกผู้ซื้อ + รหัสสาขาเดียวทั้งหน้า ⇒ 0.85 ═══

    /// <summary>แบบสลิปร้านสะดวกซื้อ (รูปแบบที่พบจริง: ชื่อนิติบุคคล + "สาขาที่" ในหัว · ไม่มีบล็อกผู้ซื้อ) — เลขผู้เสียภาษีสมมุติ</summary>
    private const string ConvenienceSlip =
        "ร้านสะดวกซื้อ ตัวอย่าง\n" +
        "บริษัท ร้านสะดวกซื้อตัวอย่าง จำกัด (มหาชน) (สาขาที่ 12345)\n" +
        "TAX ID 0107500000019\n" +
        "ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ\n" +
        "POS#00123  R#0045678\n" +
        "น้ำดื่ม 600 มล.            2 x 7.00     14.00\n" +
        "ขนมปัง                     1 x 25.00    25.00\n" +
        "รวม 2 ชิ้น                              39.00\n" +
        "เงินสด                                  40.00\n" +
        "เงินทอน                                  1.00\n" +
        "VAT INCLUDED";

    /// <summary>แบบสลิปปั๊มน้ำมัน (สาขาเดียวบนกระดาษ)</summary>
    private const string FuelSlip =
        "สถานีบริการน้ำมัน ตัวอย่าง สาขาที่ 00456\n" +
        "เลขประจำตัวผู้เสียภาษี 0105500000011\n" +
        "ใบกำกับภาษีอย่างย่อ/ใบเสร็จรับเงิน\n" +
        "ดีเซล B7  35.50 ลิตร x 32.94       1,169.37\n" +
        "รวมเงิน                              1,169.37\n" +
        "ราคารวมภาษีมูลค่าเพิ่มแล้ว";

    [Fact]
    public void Slip_NoBuyerBlock_SingleBranchCode_IsReliable()
    {
        foreach (var (paper, code) in new[] { (ConvenienceSlip, "12345"), (FuelSlip, "00456"),
                     (OcrPaperSamples.SupermarketMemberDiscount, "00007"), (OcrPaperSamples.WholesaleMixedVat, "00021") })
        {
            var br = BranchCodeExtractor.Extract(paper);
            Assert.Equal(code, br.SellerBranchCode);
            Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.WholePageSingleBranch, br.SellerEvidence);
            Assert.True(OcrVendorBranchContact.IsReliableBranch(br.SellerConfidence));
            Assert.Null(br.BuyerBranchCode);
        }
    }

    [Fact]
    public void Slip_OppositeDirection_TwoEstablishmentsOnPaper_StaysBelowThreshold()
    {
        // หัวสลิปพิมพ์ที่อยู่สำนักงานใหญ่ + สาขาที่ออก ⇒ มีสองสถานประกอบการบนกระดาษ ⇒ คงคะแนนเดิม (K-2)
        const string hqAndBranch = "สถานีบริการน้ำมัน ตัวอย่าง สาขาที่ 00456\nสำนักงานใหญ่ 99 ถ.พหลโยธิน กรุงเทพฯ 10900\nรวมเงิน 500.00";
        var a = BranchCodeExtractor.Extract(hqAndBranch);
        Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.WholePageNoBuyerBlock, a.SellerEvidence);
        Assert.False(OcrVendorBranchContact.IsReliableBranch(a.SellerConfidence));
        // สองรหัสบนหน้า (สาขาผู้ออก + สาขาอ้างอิงอื่น) ⇒ ไม่รู้ว่าตัวไหน
        var b = BranchCodeExtractor.Extract("ร้าน ตัวอย่าง สาขาที่ 00003\nรับคืนสินค้าได้ที่สาขาที่ 00009\nรวม 120.00");
        Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.WholePageNoBuyerBlock, b.SellerEvidence);
        Assert.False(OcrVendorBranchContact.IsReliableBranch(b.SellerConfidence));
        // ประโยคประกาศสาขาขัดกันเอง (ไทย 8 · อังกฤษ 9 — Detect = ไม่รู้) ⇒ ห้ามยกความมั่นใจจาก "สาขาที่ 8" ส่วนอื่นของหน้า
        var c = BranchCodeExtractor.Extract("สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8\nBranch Tax Invoice is Issued no. 9\nรวม 100.00");
        Assert.NotEqual(BranchCodeExtractor.SellerBranchEvidence.WholePageSingleBranch, c.SellerEvidence);
        Assert.False(OcrVendorBranchContact.IsReliableBranch(c.SellerConfidence));
    }

    [Fact]
    public void Slip_OppositeDirection_PapersWithBuyerBlockAreUntouched()
    {
        // ใบจริง Wine Pro มีบล็อก "Customer Info." ⇒ เส้นแยกบล็อกเดิม (ไม่ใช่ทางของ C-19)
        var wine = BranchCodeExtractor.Extract(OcrPaperSamples.WinePro);
        Assert.NotEqual(BranchCodeExtractor.SellerBranchEvidence.WholePageSingleBranch, wine.SellerEvidence);
        Assert.NotEqual(BranchCodeExtractor.SellerBranchEvidence.WholePageNoBuyerBlock, wine.SellerEvidence);
        // ใบ Makro (ประโยคประกาศสาขาผู้ออก) คงเป็นหลักฐานชั้นประโยคประกาศ
        Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.IssuerStatement,
            BranchCodeExtractor.Extract(OcrMakroBranchVendorTests.MakroPhoto).SellerEvidence);
        // สลิปที่มีแต่คำว่าสำนักงานใหญ่ (ไม่มีรหัสตัวเลข) ⇒ คงคะแนนเดิม — คำนี้โผล่ในประโยคอื่นได้
        Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.WholePageNoBuyerBlock,
            BranchCodeExtractor.Extract("ร้าน ตัวอย่าง (สำนักงานใหญ่)\nรวม 80.00").SellerEvidence);
    }

    // ═══ C-20 — ใบรับ/จ่ายเงินจากสแกนสืบทอดผู้ติดต่อของใบต้นทาง ═══

    private static readonly Guid HqRow = Guid.Parse("20200000-0000-0000-0000-000000000001");
    private static readonly Guid BranchRow = Guid.Parse("20200000-0000-0000-0000-000000000005");

    [Fact]
    public void Settlement_PaymentVoucherLinkedToHqInvoice_UsesTheInvoicesContact()
    {
        // ใบแจ้งหนี้ซื้อออกให้แถว สนญ. · สแกนใบจ่ายเงินของสาขาชลบุรีผูกใบนั้น ⇒ เงินต้องอยู่แถวเดียวกับหนี้
        var pick = OcrSettlementCounterparty.Decide(DocumentType.PaymentVoucher, HqRow, "PI6809-0031",
            predecessorContactTaxId: CpTin, scanVendorTaxId: CpTin, currentContactId: BranchRow, userPickedContact: false);
        Assert.Equal(HqRow, pick.ContactId);
        Assert.True(pick.Inherited);
        Assert.Contains("PI6809-0031", pick.Note);
    }

    [Fact]
    public void Settlement_OppositeDirection_UserPickOtherEntityAndNonSettlementTargets()
    {
        // ผู้ใช้เลือกผู้ติดต่อเอง = คำตอบสุดท้าย
        var user = OcrSettlementCounterparty.Decide(DocumentType.PaymentVoucher, HqRow, "PI-1", CpTin, CpTin, BranchRow, userPickedContact: true);
        Assert.Equal(BranchRow, user.ContactId);
        Assert.False(user.Inherited);
        // ใบต้นทางเป็นของนิติบุคคลอื่น ⇒ ไม่สืบทอดเงียบ + บอกผู้ใช้
        var other = OcrSettlementCounterparty.Decide(DocumentType.PaymentVoucher, HqRow, "PI-1", "0105555999995", CpTin, BranchRow, false);
        Assert.Equal(BranchRow, other.ContactId);
        Assert.False(other.Inherited);
        Assert.Contains("คนละนิติบุคคล", other.Note);
        // เอกสารเป้าหมายไม่ใช่ใบรับ/จ่ายเงิน (ใบแจ้งหนี้ซื้อจากใบสั่งซื้อ) ⇒ ตัวตัดสินสาขาเดิมทำงานต่อ
        var pi = OcrSettlementCounterparty.Decide(DocumentType.PurchaseInvoice, HqRow, "PO-1", CpTin, CpTin, BranchRow, false);
        Assert.Equal(BranchRow, pi.ContactId);
        Assert.False(pi.Inherited);
        // ไม่มีใบต้นทาง ⇒ ไม่แตะ
        Assert.Equal(BranchRow, OcrSettlementCounterparty.Decide(DocumentType.PaymentVoucher, null, null, null, CpTin, BranchRow, false).ContactId);
        // ผูกแถวเดียวกันอยู่แล้ว ⇒ ไม่มีโน้ตรบกวน
        Assert.Null(OcrSettlementCounterparty.Decide(DocumentType.PaymentVoucher, HqRow, "PI-1", CpTin, CpTin, HqRow, false).Note);
    }

    // ═══ C-23 — แก้เลขผู้เสียภาษีผู้ขายเป็นเลขของนิติบุคคลอื่น ⇒ ถอดการผูกเดิม ═══

    private const string OldTin = "0105555123450";   // ผ่าน mod-11
    private const string NewTin = "0105555999991";   // ผ่าน mod-11

    [Fact]
    public void MatchedContactOfOldTaxId_IsDetached_WhenUserRetypedTheTaxId()
    {
        Assert.True(OcrVendorBranchContact.MatchedContactIsOtherEntity(OldTin, "0-1055-55999-99-1", userPickedContact: false,
            userTouchedVendorTaxId: true));
        var note = OcrVendorBranchContact.StaleMatchNote(MakroName, OldTin, NewTin);
        Assert.Contains(NewTin, note);
        Assert.Contains(OldTin, note);
        Assert.Contains("ถอดการผูก", note);
    }

    [Fact]
    public void MatchedContact_OppositeDirection_KeepsTheMatch()
    {
        // เลขเดียวกัน (พิมพ์มีขีด) = นิติบุคคลเดียวกัน
        Assert.False(OcrVendorBranchContact.MatchedContactIsOtherEntity(OldTin, "0-1055-55123-45-0", false, true));
        // ผู้ใช้เลือกผู้ติดต่อเอง = ไม่ถอด
        Assert.False(OcrVendorBranchContact.MatchedContactIsOtherEntity(OldTin, NewTin, userPickedContact: true, userTouchedVendorTaxId: true));
        // ผู้ใช้ไม่ได้แตะเลข (OCR อ่านเพี้ยนแต่ผูกถูกรายด้วยชื่อ/AI) = ไม่ถอด — ไม่สร้างผู้ติดต่อเลขเพี้ยน
        Assert.False(OcrVendorBranchContact.MatchedContactIsOtherEntity(OldTin, NewTin, false, userTouchedVendorTaxId: false));
        // เลขใหม่ไม่ผ่าน mod-11 (พิมพ์ผิด) = ไม่มีหลักฐานว่าเป็นนิติบุคคลอื่น
        Assert.False(OcrVendorBranchContact.MatchedContactIsOtherEntity(OldTin, "0105555999995", false, true));
        // แถวที่ผูกไม่มีเลข (เส้นเติมเลขเข้าแถวเดิมตัดสินต่อ) · สแกนเลขไม่ครบ
        Assert.False(OcrVendorBranchContact.MatchedContactIsOtherEntity(null, NewTin, false, true));
        Assert.False(OcrVendorBranchContact.MatchedContactIsOtherEntity(OldTin, "01055559", false, true));
    }

    // ═══ C-24 — walk-in ได้เฉพาะเอกสารที่ไม่ใช่ใบกำกับเต็มรูป ═══

    [Theory]
    [InlineData(DocumentType.TaxInvoice, 0)]
    [InlineData(DocumentType.TaxInvoice, 70)]
    [InlineData(DocumentType.CreditNote, 7)]
    [InlineData(DocumentType.DebitNote, 7)]
    [InlineData(DocumentType.Receipt, 70)]
    [InlineData(DocumentType.ReceiptVoucher, 70)]
    public void WalkIn_FullTaxInvoiceTargets_AreBlockedWithAWayForward(DocumentType target, int vat)
    {
        var block = OcrWalkInBuyer.BlockReason(target, vat);
        Assert.NotNull(block);
        Assert.Contains("§86/4", block);
        Assert.Contains("ชื่อผู้ซื้อ", block);   // ทางไปต่อ
    }

    [Theory]
    [InlineData(DocumentType.Receipt, 0)]
    [InlineData(DocumentType.Invoice, 70)]
    [InlineData(DocumentType.Quotation, 0)]
    [InlineData(DocumentType.DeliveryNote, 0)]
    public void WalkIn_OppositeDirection_NonTaxInvoiceTargets_AreAllowed(DocumentType target, int vat)
        => Assert.Null(OcrWalkInBuyer.BlockReason(target, vat));
}
