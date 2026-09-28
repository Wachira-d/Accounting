using Accounting.Helpers;
using Accounting.Models.DTOs.Ocr;
using Accounting.Models.Enums;
using Accounting.Services;
using Xunit;
using C = Accounting.Helpers.OcrVendorBranchContact.Candidate;

namespace Accounting.Tests;

/// <summary>
/// รอบ 197 ทีม K2 — แก้ผลฝ่ายค้านของทีม K (<c>erp-review/2026-09-25/makro-branch/review197.md</c>)
///
/// <para>K-1: หน้าเว็บส่งรหัสสาขาผู้ขายทุกครั้ง ⇒ "ส่งมา" ถูกนับเป็น "ผู้ใช้แก้" ⇒ ด่านหลักฐานอ่อนไม่เคยกัน ·
/// K-2: ตัวอ่านสาขาถอยไปอ่านทั้งหน้าแล้วได้ 0.85 = เท่าเกณฑ์สร้างผู้ติดต่อ ⇒ สาขาของผู้ซื้อกลายเป็นผู้ติดต่อผู้ขายถาวร ·
/// K-3: สแกนผูกแถวสาขาแล้ว PO/ใบต้นทางที่ออกให้แถว สนญ. หายจากตัวเสนอ · K-6: ไม่มีคะแนน = "เชื่อได้" ·
/// K-7: เส้นสร้างเอกสารไม่มีด่าน "ผู้ขายคือเราเอง"</para>
///
/// <para>สองครึ่งตาม CLAUDE.md §H: ทุกข้อมีทั้ง "ใบที่พังกลับมาถูก" และ "ใบที่ถูกอยู่แล้วไม่ถูกแตะ"
/// (Makro สาขา 00005 ที่มีป้ายกำกับยังสร้าง/ผูกแถวสาขาได้ · ผู้ใช้แก้สาขาจริงยังนับว่าแก้)</para>
/// </summary>
public class OcrVendorBranchReview197Tests
{
    private static readonly Guid CpHq = Guid.Parse("60fb5887-0f47-4cf7-81cd-89e5f6c7ce76");
    private static readonly Guid Cp5 = Guid.Parse("00000000-0000-0000-0000-000000000005");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000000ee");
    private const string CpAxtra = "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)";
    private const string CpTin = "0107567000414";
    private const string OurTin = "0203562005871";
    private const string OurName = "หจก.แอม แฮปปี้เนส";

    private static readonly C[] OnlyHq = { new(CpHq, CpAxtra, "00000") };

    /// <summary>ต่อสายเหมือนเส้นสแกน: ผลตัวอ่านสาขา → คะแนน → IsReliableBranch → Decide (ช่องที่ฝ่ายค้านบอกว่าไม่มีเทสต์)</summary>
    private static OcrVendorBranchPick DecideFromPaper(string paper, IReadOnlyList<C> contacts)
    {
        var br = BranchCodeExtractor.Extract(paper);
        return OcrVendorBranchContact.Decide(contacts, br.SellerBranchCode,
            branchReliable: OcrVendorBranchContact.IsReliableBranch(br.SellerConfidence));
    }

    // ═════════ K-2 — ถอยอ่านทั้งหน้า ≠ หลักฐานพอสร้างผู้ติดต่อ ═════════

    private const string BuyerBlockOnTop =
        "ลูกค้า บริษัท ข จำกัด สาขาที่ 3\n" +
        "เลขผู้เสียภาษี 0105500000002\n" +
        "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)\n" +
        "เลขประจำตัวผู้เสียภาษี 0107567000414\n" +
        "ลำดับ รายการ จำนวน\n";

    [Fact]
    public void BuyerBlockOnTop_SellerBranchFromWholePage_IsWeak_AndEqualsBuyer()
    {
        var br = BranchCodeExtractor.Extract(BuyerBlockOnTop);
        Assert.Equal("00003", br.SellerBranchCode);                 // ค่ายังเติมฟอร์มได้ (กฎเหล็ก #3) — แต่ไฮไลต์
        Assert.Equal("00003", br.BuyerBranchCode);
        Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.WholePageFallback, br.SellerEvidence);
        Assert.Equal((double?)BranchCodeExtractor.FallbackEqualsBuyerConfidence, br.SellerConfidence);
        Assert.False(OcrVendorBranchContact.IsReliableBranch(br.SellerConfidence));
    }

    [Fact]
    public void BuyerBlockOnTop_DoesNotCreateAPermanentVendorBranchRow()
    {
        // เดิม: 0.85 ⇒ NewBranchRow ⇒ ผู้ติดต่อ "ซีพี แอ็กซ์ตร้า สาขาที่ 3" ถูกสร้างตั้งแต่ตอนสแกนจากสาขาของผู้ซื้อ
        var pick = DecideFromPaper(BuyerBlockOnTop, OnlyHq);
        Assert.Equal(OcrVendorBranchOutcome.OtherBranchRow, pick.Outcome);
        Assert.False(pick.MustCreateBranchRow);
        Assert.Equal(CpHq, pick.ContactId);                        // ผูก สนญ. ชั่วคราว (พฤติกรรมก่อน K)
        Assert.False(pick.MayEnrichMatchedRow);                     // ห้ามเอาสาขา/ที่อยู่จากกระดาษไปทับ สนญ.
        Assert.Contains("รหัสสาขาผู้ขาย", pick.Trace);                // บอกทางไปต่อ: พิมพ์ยืนยัน/แก้ในช่องนี้
    }

    [Fact]
    public void ReceiptWithoutBuyerBlock_WholePage_IsBelowThreshold_ButStillFilled()
    {
        var br = BranchCodeExtractor.Extract("ร้านสะดวกซื้อ สาขาที่ 00021\nรวม 107.00");
        Assert.Equal("00021", br.SellerBranchCode);
        Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.WholePageNoBuyerBlock, br.SellerEvidence);
        Assert.True(br.SellerConfidence < OcrVendorBranchContact.ReliableBranchConfidence);
    }

    [Fact]
    public void NothingRead_HasNoConfidence()
    {
        var br = BranchCodeExtractor.Extract("ร้าน ก\nรวม 100.00");
        Assert.Null(br.SellerBranchCode);
        Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.None, br.SellerEvidence);
        Assert.Null(br.SellerConfidence);
    }

    // ─── ทิศตรงข้าม: สาขาผู้ขายที่มีป้ายกำกับยังสร้าง/ผูกแถวสาขาได้ ───

    [Fact]
    public void Makro00005_IssuerLabelWithEnglishGloss_IsAnIssuerStatement()
    {
        // “สาขาที่ออกใบกำกับภาษี/ Branch 00005” — เดิม “/ Branch” คั่นทำให้ตัวอ่านประโยคประกาศไม่ติด แล้วค่ามาจากการถอยอ่านทั้งหน้า
        var s = OcrIssuerBranch.Detect(OcrMakroBranchVendorTests.MakroPhoto);
        Assert.NotNull(s);
        Assert.Equal("00005", s!.Code);
        var br = BranchCodeExtractor.Extract(OcrMakroBranchVendorTests.MakroPhoto);
        Assert.Equal("00005", br.SellerBranchCode);
        Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.IssuerStatement, br.SellerEvidence);
        Assert.Equal((double?)BranchCodeExtractor.IssuerStatementConfidence, br.SellerConfidence);
    }

    [Fact]
    public void Makro00005_OnlyHeadOfficeContact_StillCreatesTheBranchRow()
    {
        var pick = DecideFromPaper(OcrMakroBranchVendorTests.MakroPhoto, OnlyHq);
        Assert.Equal(OcrVendorBranchOutcome.NewBranchRow, pick.Outcome);
        Assert.True(pick.MustCreateBranchRow);
        Assert.Equal("00005", pick.ScannedBranch);
        Assert.Equal(CpHq, pick.TemplateContactId);
    }

    [Fact]
    public void Makro00005_BranchRowExists_BindsIt()
    {
        var pick = DecideFromPaper(OcrMakroBranchVendorTests.MakroPhoto,
            new[] { new C(CpHq, CpAxtra, "00000"), new C(Cp5, CpAxtra, "00005") });
        Assert.Equal(OcrVendorBranchOutcome.ExactBranch, pick.Outcome);
        Assert.Equal(Cp5, pick.ContactId);
    }

    [Fact]
    public void MakroPage3of3_SellerBlockBranch_IsReliable()
    {
        // ข้อความรอบ 192: “... สาขาชลบุรี สาขาที่ 00005” อยู่ก่อนป้าย “ลูกค้า” = บล็อกผู้ขาย
        var br = BranchCodeExtractor.Extract(OcrPaperSamples.MakroPage3of3);
        Assert.Equal("00005", br.SellerBranchCode);
        Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.SellerBlock, br.SellerEvidence);
        Assert.True(OcrVendorBranchContact.IsReliableBranch(br.SellerConfidence));
        Assert.Equal(OcrVendorBranchOutcome.NewBranchRow, DecideFromPaper(OcrPaperSamples.MakroPage3of3, OnlyHq).Outcome);
    }

    [Fact]
    public void RadissonPaperB_IssuerSentence_UnchangedAndReliable()
    {
        var br = BranchCodeExtractor.Extract(OcrPartyZoneRealPaperTests.PaperB);
        Assert.Equal("00008", br.SellerBranchCode);
        Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.IssuerStatement, br.SellerEvidence);
        Assert.True(OcrVendorBranchContact.IsReliableBranch(br.SellerConfidence));
    }

    [Fact]
    public void PeaBill_ParenthesisedGloss_IsAnIssuerStatement()
        => Assert.Equal("00000", OcrIssuerBranch.Detect("สาขาที่ออกใบกำกับภาษี (Branch No.) 00000\nเลขประจำตัวผู้เสียภาษี (Tax ID No.) 0994000165501\n")?.Code);

    [Theory]
    [InlineData("สาขาที่ 3/ Branch 5")]                 // ไม่มีคำว่า "ออก" = ไม่ใช่ประโยคประกาศผู้ออกใบ
    [InlineData("ลูกค้า สาขาที่ 3 / Branch 00003")]
    [InlineData("ABC Co., Ltd. / Branch 00012")]
    public void GlossWithoutIssuerWording_IsNotAnIssuerStatement(string text)
        => Assert.Null(OcrIssuerBranch.Detect(text));

    // ═════════ K-6 — ไม่มีคะแนน = ไม่รู้ ≠ เชื่อได้ ═════════

    [Fact]
    public void UnknownConfidence_OldScan_BindsExistingRowWithWarning_NoNewRow()
    {
        var pick = OcrVendorBranchContact.Decide(OnlyHq, "00005",
            branchReliable: OcrVendorBranchContact.IsReliableBranch(null));
        Assert.Equal(OcrVendorBranchOutcome.OtherBranchRow, pick.Outcome);
        Assert.Equal(CpHq, pick.ContactId);
        Assert.StartsWith("[Branch] ⚠", pick.Trace);
    }

    [Fact]
    public void UnknownConfidence_ButUserConfirmed_CreatesBranchRow()
    {
        var pick = OcrVendorBranchContact.Decide(OnlyHq, "00005",
            branchReliable: OcrVendorBranchContact.IsReliableBranch(null, userCorrected: true));
        Assert.Equal(OcrVendorBranchOutcome.NewBranchRow, pick.Outcome);
    }

    // ═════════ K-1 — "หน้าเว็บส่งค่าเดิม" ≠ "ผู้ใช้แก้สาขา" ═════════

    private static bool UserCorrectedBranch(OcrCorrectionRequest req, string? storedVendorBranch)
        => OcrCorrectedFieldList.From(req, new OcrCorrectionBaseline(storedVendorBranch, "00000"))
            .Contains("VendorBranchCode");

    [Fact]
    public void WebCreate_ResendsUnchangedBranch_IsNotACorrection_SoWeakEvidenceStillHolds()
    {
        // หน้ารีวิวส่ง vendorBranchCode = ค่าในช่อง (ที่ระบบเติมไว้เอง 00005 · คะแนน 0.50 ขัดกับประโยคบนกระดาษ)
        var req = new OcrCorrectionRequest(VendorBranchCode: "00005", BuyerBranchCode: "00000", TotalAmount: 23812.25m);
        Assert.False(UserCorrectedBranch(req, "00005"));
        var pick = OcrVendorBranchContact.Decide(OnlyHq, "00005",
            branchReliable: OcrVendorBranchContact.IsReliableBranch(0.50, userCorrected: UserCorrectedBranch(req, "00005")));
        Assert.Equal(OcrVendorBranchOutcome.OtherBranchRow, pick.Outcome);   // เดิม: NewBranchRow ทุกครั้งบนเว็บ
    }

    [Theory]
    [InlineData("5", "00005")]
    [InlineData(" 00005 ", "00005")]
    [InlineData("00000", "00000")]
    public void SameBranchInAnotherSpelling_IsNotAChange(string submitted, string stored)
        => Assert.False(UserCorrectedBranch(new OcrCorrectionRequest(VendorBranchCode: submitted), stored));

    [Theory]
    [InlineData("00005", "00000")]      // ผู้ใช้แก้ สนญ. → สาขา 5
    [InlineData("00008", "00005")]
    [InlineData("00000", null)]         // เติมช่องที่ว่าง ("ไม่รู้") = คำตอบของคน
    public void ChangedBranch_IsACorrection_AndCreatesTheRow(string submitted, string? stored)
    {
        var req = new OcrCorrectionRequest(VendorBranchCode: submitted);
        Assert.True(UserCorrectedBranch(req, stored));
        Assert.True(OcrVendorBranchContact.IsReliableBranch(0.50, userCorrected: UserCorrectedBranch(req, stored)));
    }

    [Fact]
    public void UserRetypedSameBranch_ConfirmFlag_CountsAsCorrection()
    {
        // ทางไปต่อของผู้ใช้ (F2 ข้อ 8): สาขาที่ระบบไม่แน่ใจแต่ถูกแล้ว — พิมพ์ยืนยันในช่อง ⇒ นับว่าแก้ ⇒ สร้างแถวสาขาได้
        var req = new OcrCorrectionRequest(VendorBranchCode: "00005", VendorBranchConfirmed: true);
        Assert.True(UserCorrectedBranch(req, "00005"));
        Assert.Equal(OcrVendorBranchOutcome.NewBranchRow, OcrVendorBranchContact.Decide(OnlyHq, "00005",
            branchReliable: OcrVendorBranchContact.IsReliableBranch(0.50, userCorrected: UserCorrectedBranch(req, "00005"))).Outcome);
    }

    [Fact]
    public void BuyerBranch_SameRule_OtherFieldsUnchanged()
    {
        var fields = OcrCorrectedFieldList.From(
            new OcrCorrectionRequest(VendorName: "x", BuyerBranchCode: "00000", VendorBranchCode: "00005"),
            new OcrCorrectionBaseline("00005", "00000"));
        Assert.Equal(new[] { "VendorName" }, fields);               // ช่องอื่นยังกติกาเดิม (ส่งมา = แก้)
        Assert.Contains("BuyerBranchCode", OcrCorrectedFieldList.From(
            new OcrCorrectionRequest(BuyerBranchCode: "00003"), new OcrCorrectionBaseline("00005", "00000")));
    }

    [Fact]
    public void NoBaseline_KeepsTheOldRule_ForOtherCallers()
        => Assert.Contains("VendorBranchCode", OcrCorrectedFieldList.From(new OcrCorrectionRequest(VendorBranchCode: "00005")));

    // ═════════ K-3 — PO/ใบต้นทางของนิติบุคคลเดียวกัน ทุกสาขา ═════════

    [Fact]
    public void SameEntityIds_BranchContact_IncludesHeadOfficeRow_SelfFirst()
    {
        var rows = new[]
        {
            new ContactKeyCandidate(CpHq, CpTin, "00000"),
            new ContactKeyCandidate(Cp5, "0-1075-67000-41-4", "00005"),   // เลขเก่าที่มีขีด = เลขเดียวกัน
            new ContactKeyCandidate(Other, "0105500000001", "00000"),     // คนละนิติบุคคล
        };
        var ids = ContactTaxBranchKey.SameEntityIds(Cp5, CpTin, rows);
        Assert.Equal(new[] { Cp5, CpHq }, ids);                         // PO ที่ออกให้ สนญ. ถูกเสนอให้สแกนของสาขา 00005
        Assert.DoesNotContain(Other, ids);                              // ยังกัน PO ของนิติบุคคลอื่น
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    public void SameEntityIds_AnchorWithoutTaxId_IsOnlyItself(string? anchorTax)
    {
        // ไม่มีเลข = ไม่รู้ว่าใครเป็นนิติบุคคลเดียวกัน ⇒ ห้ามขยาย (แถว "-" ของรายอื่นไม่ใช่พี่น้อง · ฝ่ายค้าน C-8)
        var rows = new[] { new ContactKeyCandidate(Other, "-", null), new ContactKeyCandidate(CpHq, anchorTax, null) };
        Assert.Equal(new[] { CpHq }, ContactTaxBranchKey.SameEntityIds(CpHq, anchorTax, rows));
    }

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice, true)]
    [InlineData(DocumentType.PaymentVoucher, true)]
    [InlineData(DocumentType.TaxInvoice, true)]
    [InlineData(DocumentType.CreditNote, false)]   // §86/9-10 ต้องแถวเดียวกับใบเดิม
    [InlineData(DocumentType.DebitNote, false)]
    public void SiblingBranchSource_NotForCreditOrDebitNotes(DocumentType target, bool expected)
        => Assert.Equal(expected, OcrPredecessorMatcher.AcceptsSiblingBranchSource(target));

    // ═════════ K-7 — ผู้ขายคือเราเอง (ตัวเดียวของเส้นสแกนและเส้นสร้างเอกสาร) ═════════

    [Fact]
    public void IsOurContact_TaxIdOrName()
    {
        Assert.True(OcrSelfPartyGuard.IsOurContact("0-2035-62005-87-1", "ร้านอะไรก็ได้", OurTin, OurName, null));
        Assert.True(OcrSelfPartyGuard.IsOurContact(null, "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส", OurTin, OurName, null));
        Assert.True(OcrSelfPartyGuard.IsOurContact(null, "Am Happiness Ltd., Part.", OurTin, OurName, "Am Happiness Ltd., Part."));
        // ทิศตรงข้าม: ผู้ขายจริง / เราไม่มีข้อมูล ⇒ ไม่ใช่เรา
        Assert.False(OcrSelfPartyGuard.IsOurContact(CpTin, CpAxtra, OurTin, OurName, null));
        Assert.False(OcrSelfPartyGuard.IsOurContact(CpTin, CpAxtra, null, null, null));
    }
}
