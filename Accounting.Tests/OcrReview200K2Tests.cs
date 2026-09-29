using Accounting.Helpers;
using Accounting.Models.DTOs.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม K2 — แก้ผลฝ่ายค้านทีม K (<c>erp-review/2026-09-29/review200-K.md</c> R1–R8 · คำตัดสินข้อ 28/29) +
/// ข้อ OCR ที่ทีม R ส่งต่อ (<c>erp-review/2026-09-21/report-C-ocr.md</c> C-01..C-03 · C-05..C-10)
///
/// <para>สองครึ่งตาม CLAUDE.md §H ทุกหัวข้อ: ครึ่งแรก = คำขอ/ใบที่พังกลับมาถูก · ครึ่งหลัง = ใบ/คำขอที่ถูกอยู่แล้ว ไม่ถูกแตะ ·
/// ข้อความกระดาษจริง: <see cref="OcrMakroBranchVendorTests.MakroPhoto"/> (ใบ Makro สาขาชลบุรี · ผู้ซื้อ หจก.แอม แฮปปี้เนส) ·
/// ใบ AWS อังกฤษล้วน USD และใบสองสกุล (ชุดเดียวกับ <c>OcrDateReaderTests</c>/<c>OcrTotalAnchorTests</c>) ·
/// การต่อสายใน service ล็อกด้วย <c>tools/required_call_site_check.py</c> (บล็อก "รอบ 200 ทีม K2")</para>
/// </summary>
public class OcrReview200K2Tests
{
    private static readonly Guid CpHq = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid AmHappy = Guid.Parse("33333333-0000-0000-0000-000000000001");
    private static readonly Guid AmHappyFood = Guid.Parse("33333333-0000-0000-0000-000000000002");
    private static readonly Guid CpSupplier = Guid.Parse("33333333-0000-0000-0000-000000000003");
    private const string CpTin = "0107567000414";

    // ═════════ R1 — พิมพ์ยืนยันรหัสสาขาเดิม ⇒ "แก้ในฟอร์มก่อน" ต้องได้แถวสาขาเหมือนปุ่ม "สร้างเอกสาร" ═════════

    [Fact]
    public void R1_Makro_UserRetypesTheSameBranch_Redecides_AndFormGetsTheBranchRow()
    {
        // สแกนเก็บ 00005 ด้วยคะแนน 0.50 (ขัดประโยคบนกระดาษ) ⇒ OtherBranchRow ผูก สนญ. + ข้อความสั่งให้ "พิมพ์รหัสสาขาใหม่"
        // ผู้ใช้พิมพ์ 00005 ซ้ำ (ค่าเดิม ⇒ vendorBranchConfirmed: true) แล้วกด "แก้ในฟอร์มก่อน"
        var req = new OcrCorrectionRequest(VendorBranchCode: "00005", BuyerBranchCode: "00000", VendorBranchConfirmed: true);
        var baseline = new OcrCorrectionBaseline("00005", "00000");
        var touched = OcrVendorBranchContact.VendorKeyTouched(req.VendorBranchCode, baseline.VendorBranchCode,
            req.VendorBranchConfirmed, req.VendorTaxId, CpTin);
        Assert.True(touched);   // เดิม false ⇒ ไม่ตัดสินใหม่ ⇒ ฟอร์มได้แถว สนญ.
        Assert.True(OcrVendorBranchContact.ShouldRedecideOnCorrection(touched, false, false, false));
        var fields = OcrCorrectedFieldList.From(req, baseline);
        var pick = OcrVendorBranchContact.Decide(
            new[] { new OcrVendorBranchContact.Candidate(CpHq, "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)", "00000") },
            "00005", branchReliable: OcrVendorBranchContact.IsReliableBranch(0.50, userCorrected: fields.Contains("VendorBranchCode")));
        Assert.Equal(OcrVendorBranchOutcome.NewBranchRow, pick.Outcome);   // = ผลของปุ่ม "สร้างเอกสาร" บนใบเดียวกัน
    }

    [Theory]
    [InlineData("00005", null)]        // หน้าเว็บส่งค่าเดิมกลับมาโดยไม่แตะช่อง
    [InlineData("5", false)]           // รูปต่าง ค่าเดียวกัน
    [InlineData(null, null)]           // ไม่ส่งช่องสาขามาเลย
    public void R1_WebEchoesTheStoredBranch_WithoutConfirming_DoesNotRedecide(string? submitted, bool? confirmed)
        => Assert.False(OcrVendorBranchContact.VendorKeyTouched(submitted, "00005", confirmed, null, CpTin));

    [Fact]
    public void R1_TaxIdKey_ChangedDigitsCount_SameDigitsInAnotherGroupingDoNot()
    {
        Assert.False(OcrVendorBranchContact.VendorKeyTouched(null, "00005", null, "0 10 7 567 00041 4", CpTin));
        Assert.True(OcrVendorBranchContact.VendorKeyTouched(null, "00005", null, "0105536000001", CpTin));
        Assert.True(OcrVendorBranchContact.VendorKeyTouched("00008", "00005", null, null, CpTin));
    }

    // ═════════ R5 — สแกนที่ลง JE อย่างเดียวห้ามเปลี่ยนผู้ติดต่อ ═════════

    [Fact]
    public void R5_JeOnlyScan_IsAlreadyPosted_SoCorrectionDoesNotRedecide()
    {
        var posted = OcrVendorBranchContact.ScanAlreadyPosted(createdDocumentId: null, createdJournalEntryId: Guid.NewGuid());
        Assert.True(posted);
        Assert.False(OcrVendorBranchContact.ShouldRedecideOnCorrection(true, false, posted, false));
        Assert.True(OcrVendorBranchContact.ScanAlreadyPosted(Guid.NewGuid(), null));
    }

    [Fact]
    public void R5_ScanNotPostedYet_StillRedecides()
    {
        var posted = OcrVendorBranchContact.ScanAlreadyPosted(null, null);
        Assert.False(posted);
        Assert.True(OcrVendorBranchContact.ShouldRedecideOnCorrection(true, false, posted, false));
    }

    // ═════════ R2 → คำตัดสินข้อ 28 — WHT ที่คนแก้ในฟอร์มเอกสาร เรียนตอนอนุมัติ ═════════

    private static OcrPostedWhtLine L(decimal rate, string? code = null) => new(rate, code);

    [Fact]
    public void Wht28_UserTicksWhtInDocumentForm_IsAnEdit_AndTeachesAtApproval()
    {
        // ใบค่าบริการที่กระดาษไม่พิมพ์ WHT · ผู้ใช้กด "แก้ในฟอร์มก่อน" → ติ๊กหัก 3% (40(8)) ในฟอร์ม → อนุมัติ
        var touched = OcrPostedTruth.WhtTouched(new OcrWhtBaseline(false, null, null), new[] { L(3m, "40(8)"), L(3m, "40(8)") });
        Assert.Equal(new[] { "HasWht", "WhtRate", "WhtIncomeTypeCode" }, touched);
        var scope = OcrWhtLearningScope.Decide(hasScan: true, paperShowsWht: false,
            userCorrectedFields: OcrCorrectedFieldList.Merge("SubTotal", touched));
        Assert.True(scope.Learn);                                            // เดิม SystemSuggestedOnly ⇒ ไม่เคยเรียน
        Assert.Equal(WhtLearningEvidence.UserEdited, scope.Evidence);
    }

    [Fact]
    public void Wht28_UserChangesRate_OrClearsIncomeType_OrRemovesWht_Counts()
    {
        var scan = new OcrWhtBaseline(true, 3m, "40(8)");
        Assert.Equal(new[] { "WhtRate" }, OcrPostedTruth.WhtTouched(scan, new[] { L(5m, "40(8)") }));
        Assert.Equal(new[] { "WhtIncomeTypeCode" }, OcrPostedTruth.WhtTouched(scan, new[] { L(3m, null) }));
        Assert.Equal(new[] { "HasWht", "WhtRate", "WhtIncomeTypeCode" }, OcrPostedTruth.WhtTouched(scan, new[] { L(0m) }));
    }

    [Fact]
    public void Wht28_DocumentCreatedFromScanUntouched_IsNotAnEdit_NoSelfTeaching()
    {
        // เส้นสร้างเอกสารเติม WHT = scan.HasWht && WhtRate > 0 ทุกบรรทัด + รหัสของสแกน ⇒ ไม่มีคนแตะ = ว่าง (ไม่สอนตัวเอง · ข้อ 19)
        Assert.Empty(OcrPostedTruth.WhtTouched(new OcrWhtBaseline(true, 3m, "40(8)"), new[] { L(3.00m, " 40(8) "), L(3m, "40(8)") }));
        Assert.Empty(OcrPostedTruth.WhtTouched(new OcrWhtBaseline(false, null, null), new[] { L(0m), L(0m) }));
        // สแกนติดธง HasWht แต่ไม่มีอัตรา ⇒ เส้นสร้างเอกสารไม่หัก ⇒ เอกสารไม่มี WHT = ตรง baseline
        Assert.Empty(OcrPostedTruth.WhtTouched(new OcrWhtBaseline(true, null, "40(8)"), new[] { L(0m) }));
        // ข้อเสนอระบบ (SuggestedWhtRate) ไม่เคยถูกหักให้เอง — ไม่ใช่ baseline · ไม่มีคนติ๊ก = ไม่นับ
        var scope = OcrWhtLearningScope.Decide(true, false, OcrCorrectedFieldList.Merge(null,
            OcrPostedTruth.WhtTouched(new OcrWhtBaseline(false, null, null), Array.Empty<OcrPostedWhtLine>())));
        Assert.Equal(WhtLearningEvidence.SystemSuggestedOnly, scope.Evidence);
    }

    // ═════════ R3 → คำตัดสินข้อ 29 — "ผู้ใช้พิมพ์ที่อยู่" นับเฉพาะกติกา baseline ═════════

    private const string MakroHeaderHqAddress = "บมจ.ซีพี แอ็กซ์ตร้า สาขาชลบุรี 55/3 หมู่ 2 ถ.สุขุมวิท เมืองชลบุรี";

    [Fact]
    public void Addr29_LegacyRowWithVendorAddressInCorrectedFields_IsUnknown_SoBranchRowAddressIsLeftBlank()
    {
        // แถวก่อนรอบ 200: "VendorAddress" เข้ารายการเพราะหน้าเว็บส่งทุกครั้ง (ไม่มี baseline) ⇒ ธงใหม่ = false (ไม่รู้)
        var legacyFields = OcrCorrectedFieldList.From(new OcrCorrectionRequest(VendorAddress: MakroHeaderHqAddress));
        Assert.Contains("VendorAddress", legacyFields);
        Assert.False(OcrCorrectedFieldList.VendorAddressTyped(legacyFields, before: null));
        // ⇒ ตัวอ่านได้ false ⇒ ที่อยู่หัวกระดาษ (สนญ.) ไม่ลงแถวสาขาใหม่ (เดิม true ⇒ ที่อยู่ สนญ. ลงแถวสาขา 00005)
        Assert.False(OcrIssuerBranch.StoredAddressIsIssuerBranch(OcrMakroBranchVendorTests.MakroPhoto, "00005",
            MakroHeaderHqAddress, userCorrectedAddress: false));
    }

    [Fact]
    public void Addr29_UserReallyTypesAnAddress_UnderBaseline_IsProven()
    {
        var baseline = new OcrCorrectionBaseline("00005", "00000", VendorAddress: new OcrTextBaseline(MakroHeaderHqAddress));
        var typed = "55/3 หมู่ 2 ถ.สุขุมวิท ต.เสม็ด อ.เมือง จ.ชลบุรี 20000";
        var fields = OcrCorrectedFieldList.From(new OcrCorrectionRequest(VendorAddress: typed), baseline);
        Assert.True(OcrCorrectedFieldList.VendorAddressTyped(fields, baseline));
        Assert.True(OcrIssuerBranch.StoredAddressIsIssuerBranch(OcrMakroBranchVendorTests.MakroPhoto, "00005", typed, userCorrectedAddress: true));
    }

    [Fact]
    public void Addr29_WebEchoesTheStoredAddress_UnderBaseline_IsNotTyped()
    {
        var baseline = new OcrCorrectionBaseline("00005", "00000", VendorAddress: new OcrTextBaseline(MakroHeaderHqAddress));
        var fields = OcrCorrectedFieldList.From(new OcrCorrectionRequest(VendorAddress: "  " + MakroHeaderHqAddress + " "), baseline);
        Assert.False(OcrCorrectedFieldList.VendorAddressTyped(fields, baseline));
    }

    // ═════════ R6 — เติมเลขภาษีเข้าแถวเดิมใต้ล็อก K-5 ═════════

    [Fact]
    public void R6_AnotherRowAlreadyHoldsTheKey_DoNotAdopt()
    {
        var adopting = Guid.NewGuid();
        Assert.False(OcrContactCreateLock.MayAdoptAfterLock(Guid.NewGuid(), adopting));   // อีกคำขอเพิ่งสร้างแถว T|สาขา
        var note = OcrContactCreateLock.AdoptSkippedNote("0 10 7 567 00041 4", "00005", "ซีพี แอ็กซ์ตร้า (ชื่อจากกระดาษ)");
        Assert.Contains(CpTin, note);
        Assert.Contains("ไม่เติมเลขผู้เสียภาษี", note);
    }

    [Fact]
    public void R6_NoOtherRow_OrOnlyItself_Adopts()
    {
        var adopting = Guid.NewGuid();
        Assert.True(OcrContactCreateLock.MayAdoptAfterLock(null, adopting));
        Assert.True(OcrContactCreateLock.MayAdoptAfterLock(adopting, adopting));
    }

    // ═════════ C-01 — ลูกค้าฝั่งขายจากชื่อ: superstring ของลูกค้ารายเดียว ไม่ใช่ substring ดิบ "ชื่อสั้นสุดชนะ" ═════════

    private static readonly OcrCounterpartyCandidate[] Contacts =
    {
        new(AmHappy, "หจก. แอม แฮปปี้เนส", IsCustomer: true),
        new(CpSupplier, "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)", IsCustomer: false),
    };

    [Fact]
    public void C01_TruncatedBuyerName_PicksTheOnlyCustomerThatContainsIt()
    {
        // RG-03: OCR ตัด "หจก. แอม แฮปปี้เนส" เหลือ "แอม แฮปปี้" — ลูกค้ารายเดียวที่ครอบชื่อ ⇒ ใช้ได้ (ไม่ถดถอยจากเดิม)
        var pick = OcrCounterpartyMatch.PickBuyerByName("แอม แฮปปี้", Contacts);
        Assert.Equal(AmHappy, pick.ContactId);
        Assert.Equal(OcrCounterpartyNameBasis.UniqueSuperstring, pick.Basis);
        Assert.Equal(ContactMatchKind.FuzzyName, ContactTaxBranchKey.NameMatchKind("แอม แฮปปี้", "หจก. แอม แฮปปี้เนส"));   // ⇒ ห้ามเติมเลข (AdoptTaxId)
    }

    [Fact]
    public void C01_ExactNameInAnotherSpellingOfTheLegalForm_IsExact()
    {
        var pick = OcrCounterpartyMatch.PickBuyerByName("หจก.แอม แฮปปี้เนส",
            new[] { new OcrCounterpartyCandidate(AmHappy, "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส", false) });
        Assert.Equal(AmHappy, pick.ContactId);   // ชื่อตรงกันทุกตัว = รายเดียวกัน แม้ยังไม่ติดธงลูกค้า
        Assert.Equal(OcrCounterpartyNameBasis.ExactName, pick.Basis);
    }

    [Theory]
    [InlineData("บริษัท")]
    [InlineData("จำกัด")]
    [InlineData("บจก.")]
    [InlineData("แอม")]
    public void C01_LegalFormFragments_OrTooShortNames_DoNotMatchAnyone(string scanned)
    {
        // เดิม Name.Contains("บริษัท") จับผู้ติดต่อชื่อสั้นสุดได้ทันที ⇒ ใบกำกับออกให้ผู้ซื้อผิดราย
        var pick = OcrCounterpartyMatch.PickBuyerByName(scanned, Contacts);
        Assert.Null(pick.ContactId);
        Assert.Equal(OcrCounterpartyNameBasis.TooShort, pick.Basis);
        Assert.StartsWith("[BUYER]", pick.Note);
    }

    [Fact]
    public void C01_TwoCustomersContainTheName_IsAmbiguous_NotGuessed()
    {
        var two = Contacts.Append(new OcrCounterpartyCandidate(AmHappyFood, "บริษัท แอม แฮปปี้ ฟู้ด จำกัด", true)).ToArray();
        var pick = OcrCounterpartyMatch.PickBuyerByName("แอม แฮปปี้", two);
        Assert.Null(pick.ContactId);   // เดิม: หยิบ "ชื่อสั้นสุด" เงียบ ๆ
        Assert.Equal(OcrCounterpartyNameBasis.Ambiguous, pick.Basis);
        Assert.Contains("2 ราย", pick.Note);
    }

    [Fact]
    public void C01_SupplierOnlyRow_IsNotASalesCustomer_BySuperstring()
    {
        var pick = OcrCounterpartyMatch.PickBuyerByName("ซีพี แอ็กซ์ตร้า", Contacts);
        Assert.Null(pick.ContactId);
        Assert.Equal(OcrCounterpartyNameBasis.None, pick.Basis);
    }

    // ═════════ C-03 — ไม่รู้คู่ค้า = ข้อความไทยที่บอกทางไปต่อ (ไม่ใช่อังกฤษที่ถูกปิดบัง) ═════════

    [Theory]
    [InlineData(true, "“ชื่อผู้ซื้อ”")]
    [InlineData(false, "“ชื่อผู้ขาย”")]
    public void C03_NoCounterpartyMessage_IsThai_AndPointsAtTheReviewFields(bool salesSide, string label)
    {
        var msg = OcrCounterpartyMatch.NoCounterpartyMessage(salesSide);
        Assert.Contains(label, msg);
        Assert.Contains("“จับคู่ผู้ติดต่อ”", msg);
        Assert.True(msg.Any(ch => ch is >= '฀' and <= '๿'));   // ExceptionMiddleware.LooksUserFacing ⇒ ไม่ถูกปิดบัง
        Assert.DoesNotContain("Cannot create document", msg);
    }

    // ═════════ C-06 — เส้น 1-click ปิดลูปบทบาทเรา แต่ไม่ลดชั้นคำตอบของคน ═════════

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("VendorName,SubTotal", true)]
    [InlineData("OurRole,VendorName", false)]
    [InlineData(" OurRole ", false)]
    public void C06_ImplicitRoleConfirmation_OnlyWhenTheUserDidNotEditTheRole(string? fields, bool mayRecord)
        => Assert.Equal(mayRecord, OcrAiLabelScope.ImplicitMayRecord(fields, "OurRole"));

    [Fact]
    public void C06_RoleLabel_ComparesWithTheAiAnswer_NotTheFinalValue()
    {
        Assert.True(OcrAiLabelScope.AcceptedAi("Buyer", "Buyer"));
        Assert.False(OcrAiLabelScope.AcceptedAi(null, "Buyer"));   // ไม่มีคำตอบ AI = ไม่มีทาง "AI ถูก"
        Assert.False(OcrAiLabelScope.AcceptedAi("Seller", "Buyer"));
    }

    // ═════════ C-09 — สกุลเงิน: ดูบริเวณยอดรวมเมื่อหน้ามีทั้งบาทและสกุลต่างประเทศ ═════════

    private const string AwsLike =
        "Amazon Web Services, Inc.\nTax Invoice\nInvoice Number: 1234567890\nInvoice Date: 05/08/2026\n"
        + "Bill to: Example Co., Ltd.\nTotal amount due USD 12.34\n";

    private const string TwoCurrencyPaper =
        "INVOICE\nAmount (USD) 1,000.00\nVAT 7% (USD) 70.00\nTotal (USD) 1,070.00\nExchange rate 34.00\n"
        + "Amount (THB) 34,000.00\nVAT (THB) 2,380.00\nTotal (THB) 36,380.00";

    [Fact]
    public void C09_UsdInvoice_WithAReferenceExchangeRateLine_IsUsd_NotBaht()
    {
        // เดิม: เจอ "THB" ในบรรทัดอ้างอิงอัตรา ⇒ บาท ⇒ 1,070 บันทึกเป็น 1,070 บาท (ต่างจริง ~36 เท่า)
        var paper = AwsLike.Replace("Total amount due USD 12.34\n",
            "Exchange rate for reference only: 1 USD = 36.50 THB\nTotal amount due USD 1,070.00\n");
        var r = OcrCurrencyEvidence.Read(paper);
        Assert.Equal("USD", r.Code);
        Assert.Equal(OcrCurrencyBasis.TotalAreaForeign, r.Basis);
    }

    [Fact]
    public void C09_TwoCurrencyPaper_IsUnknown_KeepsBaht_ButFlagsAndStopsAutoApproval()
    {
        var r = OcrCurrencyEvidence.Read(TwoCurrencyPaper);
        Assert.Null(r.Code);   // ค่าเดิม (บาท) — ไม่เดาใหม่
        Assert.True(r.Unsure);
        var note = OcrCurrencyEvidence.UnsureNote(TwoCurrencyPaper);
        Assert.StartsWith(OcrCurrencyEvidence.UnsureTag, note);
        Assert.False(OcrPostingReadiness.Evaluate(note, hasUsableDate: true).CanAutoApprove);
        Assert.Contains(OcrCurrencyEvidence.UnsureTag, OcrScanSnapshot.DecisionNoteTags);   // ติดไปกับสแกนสำเนา
    }

    [Fact]
    public void C09_PapersThatWereRight_AreUntouched()
    {
        // ใบ USD ล้วน (AWS) ⇒ USD เหมือนเดิม
        Assert.Equal("USD", OcrCurrencyEvidence.Infer(AwsLike));
        Assert.Equal(OcrCurrencyBasis.ForeignOnly, OcrCurrencyEvidence.Read(AwsLike).Basis);
        // ใบไทย (Makro) ไม่มีสกุลต่างประเทศ ⇒ บาท
        Assert.Null(OcrCurrencyEvidence.Infer(OcrMakroBranchVendorTests.MakroPhoto));
        // ใบบาทที่มีบัญชี USD ในบรรทัดโอนเงิน — ยอดรวมบอกบาท ⇒ บาท (ไม่ติดธง)
        var thb = "ใบกำกับภาษี\nค่าบริการ 1,000.00\nภาษีมูลค่าเพิ่ม 70.00\nยอดรวมทั้งสิ้น 1,070.00 บาท\n"
                  + "โอนเข้าบัญชี USD เลขที่ 123-4-56789-0";
        var r = OcrCurrencyEvidence.Read(thb);
        Assert.Null(r.Code);
        Assert.Equal(OcrCurrencyBasis.TotalAreaThb, r.Basis);
        Assert.False(r.Unsure);
        Assert.Null(OcrCurrencyEvidence.Infer(null));
    }
}
