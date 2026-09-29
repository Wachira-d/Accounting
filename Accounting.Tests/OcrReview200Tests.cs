using Accounting.Helpers;
using Accounting.Models.DTOs.Ocr;
using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม K — OCR ผู้ติดต่อสาขา / ใบ Makro ค้าง (<c>erp-review/2026-09-25/makro-branch/review197.md</c> K-3b · K-4 · K-5 · K-8 · K-9 ·
/// K-10 · K-11 + <c>review-r199-ocr.md</c> A-5 · คำตัดสินเจ้าของข้อ 19 ใน <c>erp-review/2026-09-29/DECISIONS.md</c>)
///
/// <para>สองครึ่งตาม CLAUDE.md §H ทุกหัวข้อ: ครึ่งแรก = ใบ/คำขอที่พังกลับมาถูก · ครึ่งหลัง = ใบ Makro/ใบอื่นที่ถูกอยู่แล้ว ไม่ถูกแตะ
/// (ข้อความกระดาษจริงจาก <see cref="OcrMakroBranchVendorTests.MakroPhoto"/> · <see cref="OcrPaperSamples.MakroPage3of3"/> ·
/// <see cref="OcrPartyZoneRealPaperTests.PaperB"/>) · การต่อสายใน service ล็อกด้วย <c>tools/required_call_site_check.py</c> (บล็อก "รอบ 200 ทีม K")</para>
/// </summary>
public class OcrReview200Tests
{
    private static readonly Guid CpHq = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid CpBranch5 = Guid.Parse("11111111-0000-0000-0000-000000000005");
    private static readonly Guid Other = Guid.Parse("22222222-0000-0000-0000-000000000001");
    private const string CpTin = "0107567000414";

    // ═════════ K-10 (คำตัดสินข้อ 19) — ช่อง WHT นับว่าผู้ใช้แก้เฉพาะเมื่อค่าเปลี่ยนจากที่สแกน ═════════

    private static OcrCorrectionBaseline Base(bool hasWht = false, decimal? rate = null, string? code = null, string? addr = null)
        => new("00005", "00000", new OcrWhtBaseline(hasWht, rate, code), new OcrTextBaseline(addr));

    /// <summary>สิ่งที่หน้ารีวิวส่งทุกครั้ง (<c>_buildReviewCorrection</c>): hasWht = ติ๊กหรือไม่ · whtRate = null เมื่อช่องว่าง · code "" เมื่อไม่เลือก</summary>
    private static OcrCorrectionRequest Web(bool hasWht, decimal? rate, string code, string? address = null)
        => new(HasWht: hasWht, WhtRate: rate, WhtIncomeTypeCode: code, VendorBranchCode: "00005", BuyerBranchCode: "00000",
            VendorAddress: address, TotalAmount: 23812.25m);

    [Fact]
    public void Wht_WebResendsUntouchedValues_IsNotAnEdit_SoHistoryIsNotTaught()
    {
        // เดิม: "HasWht" เข้ารายการทุกใบบนเว็บ ⇒ OcrWhtLearningScope = UserEdited ⇒ ประวัติ WHT ของผู้ขายถูกสอนด้วยค่าที่ไม่มีใครแตะ
        var fields = OcrCorrectedFieldList.From(Web(false, null, ""), Base());
        Assert.DoesNotContain("HasWht", fields);
        Assert.DoesNotContain("WhtRate", fields);
        Assert.DoesNotContain("WhtIncomeTypeCode", fields);
        var scope = OcrWhtLearningScope.Decide(hasScan: true, paperShowsWht: false,
            userCorrectedFields: OcrCorrectedFieldList.Merge(null, fields));
        Assert.False(scope.Learn);
        Assert.Equal(WhtLearningEvidence.SystemSuggestedOnly, scope.Evidence);
    }

    [Fact]
    public void Wht_StoredValuesEchoedBack_InAnotherSpelling_IsNotAnEdit()
    {
        var fields = OcrCorrectedFieldList.From(Web(true, 3.00m, " 40(8) "), Base(true, 3m, "40(8)"));
        Assert.DoesNotContain("HasWht", fields);
        Assert.DoesNotContain("WhtRate", fields);
        Assert.DoesNotContain("WhtIncomeTypeCode", fields);
    }

    [Fact]
    public void Wht_UserTicksOrAppliesSuggestion_IsAnEdit_AndTeaches()
    {
        // ปุ่ม "ใช้อัตรานี้" / ติ๊กเอง: false → true · อัตรา 3 — คนลงมือ ⇒ เรียนได้ (ทิศตรงข้าม)
        var fields = OcrCorrectedFieldList.From(Web(true, 3m, ""), Base());
        Assert.Contains("HasWht", fields);
        Assert.Contains("WhtRate", fields);
        Assert.Equal(WhtLearningEvidence.UserEdited, OcrWhtLearningScope.Decide(true, false,
            OcrCorrectedFieldList.Merge(null, fields)).Evidence);
    }

    [Theory]
    [InlineData(true, 3, "40(8)", false, null, "40(8)", "HasWht")]          // ปลดติ๊ก = แก้จริง
    [InlineData(true, 3, "40(8)", true, 5, "40(8)", "WhtRate")]            // เปลี่ยนอัตรา
    [InlineData(true, 3, "40(8)", true, 3, "40(2)", "WhtIncomeTypeCode")]  // เปลี่ยนประเภทเงินได้
    [InlineData(true, 3, "40(8)", true, 3, "", "WhtIncomeTypeCode")]       // ล้างประเภทเงินได้
    public void Wht_EachChangedField_IsCounted(bool storedHas, int storedRate, string storedCode,
        bool sentHas, int? sentRate, string sentCode, string expected)
        => Assert.Contains(expected, OcrCorrectedFieldList.From(Web(sentHas, sentRate, sentCode),
            Base(storedHas, storedRate, storedCode)));

    [Fact]
    public void Wht_NoBaseline_KeepsTheOldRule_ForOtherCallers()
        => Assert.Contains("HasWht", OcrCorrectedFieldList.From(new OcrCorrectionRequest(HasWht: false)));

    // ═════════ K-9 (ที่อยู่ส่งซ้ำ) — "ผู้ใช้พิมพ์ที่อยู่เอง" ต้องเป็นการเปลี่ยนจริง ═════════

    [Fact]
    public void VendorAddress_EchoedBack_IsNotAnEdit_ButRealEditIs()
    {
        const string stored = "55/3 หมู่ 2 ถ.สุขุมวิท ต.เสม็ด อ.เมือง จ.ชลบุรี 20000";
        Assert.DoesNotContain("VendorAddress", OcrCorrectedFieldList.From(Web(false, null, "", "  55/3 หมู่ 2  ถ.สุขุมวิท ต.เสม็ด อ.เมือง จ.ชลบุรี 20000 "), Base(addr: stored)));
        Assert.Contains("VendorAddress", OcrCorrectedFieldList.From(Web(false, null, "", "99 ถ.บางนา-ตราด กรุงเทพ 10260"), Base(addr: stored)));
        Assert.Contains("VendorAddress", OcrCorrectedFieldList.From(Web(false, null, "", "55/3 ชลบุรี 20000"), Base(addr: null)));
        // ไม่มีค่าก่อนแก้ = กติกาเดิม
        Assert.Contains("VendorAddress", OcrCorrectedFieldList.From(new OcrCorrectionRequest(VendorAddress: stored)));
    }

    // ═════════ K-9 — ที่อยู่แถวสาขาใหม่ในเส้นสร้างเอกสาร: ไม่รู้ = ว่าง ═════════

    private const string RadissonBranch8Address = "854/2 ถนนบุรีรัมย์ ต.ชะอำ อ.ชะอำ จ.เพชรบุรี 76120";
    private const string RadissonHqAddress = "200 อาคารจัสมินอินเตอร์เนชั่นแนลทาวเวอร์ ห้อง 2302A ชั้น 23 หมู่ 8 ถ.แจ้งวัฒนะ ต.ปากเกร็ด อ.ปากเกร็ด จ.นนทบุรี";

    [Fact]
    public void BranchRowAddress_HeaderHeadOfficeAddress_IsNotProven_SoLeftBlank()
    {
        // เดิม: paperIsIssuerBranchAddress:false แต่ ContactAddress ยังคืนที่อยู่จากกระดาษเมื่อสาขาตรง ⇒ แถวสาขาที่ 8 ได้ที่อยู่ นนทบุรี (สนญ.)
        var proven = OcrIssuerBranch.StoredAddressIsIssuerBranch(OcrPartyZoneRealPaperTests.PaperB, "00008", RadissonHqAddress, false);
        Assert.False(proven);
        var (addr, _) = OcrIssuerBranch.ContactAddress("00008", "00008", dbdMatched: false, dbdAddress: null,
            paperAddress: proven ? RadissonHqAddress : null, paperIsIssuerBranchAddress: proven);
        Assert.Null(addr);
    }

    [Fact]
    public void BranchRowAddress_PaperDeclaresThisBranchAddress_IsKept()
    {
        var proven = OcrIssuerBranch.StoredAddressIsIssuerBranch(OcrPartyZoneRealPaperTests.PaperB, "8", RadissonBranch8Address, false);
        Assert.True(proven);
        var (addr, fromRegistry) = OcrIssuerBranch.ContactAddress("00008", "00008", false, null, RadissonBranch8Address, proven);
        Assert.Equal(RadissonBranch8Address, addr);
        Assert.False(fromRegistry);
    }

    [Fact]
    public void BranchRowAddress_UserTypedAddress_IsProven_OtherBranchOrHeadOfficeIsNot()
    {
        Assert.True(OcrIssuerBranch.StoredAddressIsIssuerBranch(OcrMakroBranchVendorTests.MakroPhoto, "00005",
            "55/3 หมู่ 2 ถ.สุขุมวิท ต.เสม็ด อ.เมือง จ.ชลบุรี 20000", userCorrectedAddress: true));
        // ประโยคบอกสาขาที่ 8 แต่ถามสาขาที่ 3 ⇒ ไม่ใช่ที่อยู่ของสาขานั้น
        Assert.False(OcrIssuerBranch.StoredAddressIsIssuerBranch(OcrPartyZoneRealPaperTests.PaperB, "00003", RadissonBranch8Address, false));
        Assert.False(OcrIssuerBranch.StoredAddressIsIssuerBranch(OcrPartyZoneRealPaperTests.PaperB, "00000", RadissonBranch8Address, false));
        Assert.False(OcrIssuerBranch.StoredAddressIsIssuerBranch(OcrPartyZoneRealPaperTests.PaperB, "00008", null, true));
        // ใบ Makro ไม่มีที่อยู่ต่อท้ายประโยคประกาศสาขา (ที่อยู่อยู่อีกคอลัมน์) ⇒ ไม่พิสูจน์เอง (ต้องให้ผู้ใช้ยืนยัน/พิมพ์)
        Assert.False(OcrIssuerBranch.StoredAddressIsIssuerBranch(OcrMakroBranchVendorTests.MakroPhoto, "00005",
            "บมจ.ซีพี แอ็กซ์ตร้า สาขาชลบุรี 55/3 หมู่ 2 ถ.สุขุมวิท เมืองชลบุรี", false));
    }

    // ═════════ K-11 — บล็อกผู้ซื้อใบ Makro: สาขาผู้ซื้อ 00000 (ไม่ใช่ 00005 ของผู้ขาย) ═════════

    [Fact]
    public void Makro_BuyerBranch_IsHeadOffice_NotTheIssuerBranch()
    {
        var br = BranchCodeExtractor.Extract(OcrMakroBranchVendorTests.MakroPhoto);
        Assert.Equal("00000", br.BuyerBranchCode);      // เดิม 00005 (บล็อกผู้ซื้อเริ่มที่ "ต้นฉบับลูกค้า" + ประโยคสาขาผู้ออกใบสลับคอลัมน์)
        // ฝั่งผู้ขายไม่ขยับ (จุดแบ่งฝั่งผู้ขายเดิม · ประโยคประกาศสาขา 0.90)
        Assert.Equal("00005", br.SellerBranchCode);
        Assert.Equal(BranchCodeExtractor.SellerBranchEvidence.IssuerStatement, br.SellerEvidence);
    }

    [Fact]
    public void BuyerBranch_PapersThatWereRight_AreUntouched()
    {
        Assert.Equal("00000", BranchCodeExtractor.Extract(OcrPaperSamples.MakroPage3of3).BuyerBranchCode);
        Assert.Equal("00000", BranchCodeExtractor.Extract(OcrPartyZoneRealPaperTests.PaperB).BuyerBranchCode);
        Assert.Equal("00000", BranchCodeExtractor.Extract(OcrPartyZoneRealPaperTests.PaperA).BuyerBranchCode);
        // ผู้ซื้อสาขาจริงยังอ่านได้ แม้มีป้ายฉบับอยู่บนหัวใบ
        var withCopyMarker = BranchCodeExtractor.Extract(
            "บริษัท เอ จำกัด สำนักงานใหญ่\nต้นฉบับลูกค้า\nใบกำกับภาษี\nลูกค้า: บริษัท บี จำกัด สาขาที่ 00003\nรายการ\n1 สินค้า 100.00\n");
        Assert.Equal("00003", withCopyMarker.BuyerBranchCode);
        Assert.Equal("00000", withCopyMarker.SellerBranchCode);
    }

    [Fact]
    public void MaskStatements_BlanksOnlyIssuerStatements_KeepsLengthAndLines()
    {
        var masked = OcrIssuerBranch.MaskStatements(OcrMakroBranchVendorTests.MakroPhoto);
        Assert.Equal(OcrMakroBranchVendorTests.MakroPhoto.Length, masked.Length);
        Assert.Equal(OcrMakroBranchVendorTests.MakroPhoto.Count(c => c == '\n'), masked.Count(c => c == '\n'));
        Assert.DoesNotContain("Branch 00005", masked);
        Assert.Contains("สาขา 00000", masked);                  // สาขาของผู้ซื้อไม่ถูกกลบ
        var b = OcrIssuerBranch.MaskStatements(OcrPartyZoneRealPaperTests.PaperB);
        Assert.DoesNotContain("Issued no. 8", b);
        Assert.DoesNotContain("สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8", b);
        Assert.Equal("", OcrIssuerBranch.MaskStatements(null));
    }

    [Fact]
    public void MaskCopyNoise_BlanksCopyMarker_KeepsRealCustomerLabel()
    {
        var m = OcrPartyLabels.MaskCopyNoise("ต้นฉบับลูกค้า\nFor Customer\nลูกค้า: หจก.แอม แฮปปี้เนส");
        Assert.DoesNotContain("ต้นฉบับ", m);
        Assert.DoesNotContain("For Customer", m);
        Assert.Contains("ลูกค้า: หจก.แอม แฮปปี้เนส", m);
        Assert.Equal("ต้นฉบับลูกค้า\nFor Customer\nลูกค้า: หจก.แอม แฮปปี้เนส".Length, m.Length);
    }

    // ═════════ K-8 — อีเมล/เบอร์ของผู้ขายใต้ช่องลายเซ็นท้ายบิล ═════════

    private static string Rows(int n) => string.Concat(Enumerable.Range(1, n).Select(i => $"{i} สินค้า {i} 1 100.00 100.00\n"));

    [Fact]
    public void FooterSignatureSlot_VendorEmailBelowIt_IsTheVendors()
    {
        // เดิม: "ผู้รับสินค้า" ในช่องลายเซ็น = ป้ายบล็อกผู้รับ ⇒ อีเมล/เบอร์ผู้ขายภายใน 10 บรรทัดใต้มันถูกนับเป็นของผู้ซื้อ ⇒ null (หายเงียบ)
        var text = "บริษัท เอ จำกัด\nลูกค้า: หจก.แอม แฮปปี้เนส\n" + Rows(12) + "รวม 1,200.00\n"
            + "ลงชื่อ.................ผู้รับสินค้า     ลงชื่อ.................ผู้ส่งสินค้า\n"
            + "ติดต่อ sales@a.co.th โทร 02-111-2222\n";
        Assert.Equal("sales@a.co.th", OcrSellerContactChannel.SellerEmail(text));
        Assert.False(OcrSellerContactChannel.IsBuyerSide(text, text.IndexOf("02-111-2222", StringComparison.Ordinal)));
        Assert.Empty(OcrPartyLabels.FindRecipientAll(text));
    }

    [Theory]
    [InlineData("ลงชื่อ.................ผู้รับสินค้า")]                 // คำลงนาม + เส้น
    [InlineData("(.........................) ผู้รับสินค้า")]            // เส้นให้เซ็น
    [InlineData("ผู้รับสินค้า          ผู้ส่งสินค้า          ผู้รับเงิน")] // แถวหัวช่องลายเซ็นหลายบทบาท
    [InlineData("Receiver Signature")]
    public void SignatureSlotLines_AreSignatureSlots(string line)
        => Assert.True(OcrSignatureSlot.IsSignatureSlot("หัวใบ\n" + line + "\n", 6));

    [Fact]
    public void RoleUnderABareSignatureRule_IsASignatureSlot()
    {
        const string text = "รวม 100.00\n_______________      _______________\n    ผู้รับสินค้า\n";
        Assert.True(OcrSignatureSlot.IsSignatureSlot(text, text.IndexOf("ผู้รับสินค้า", StringComparison.Ordinal)));
    }

    [Fact]
    public void Makro_RecipientBlock_IsStillTheBuyers()
    {
        var t = OcrMakroBranchVendorTests.MakroPhoto;
        // "ชื่อผู้รับสินค้า/ Receiver" = บทบาทเดียวสองภาษา ไม่มีเส้น/คำลงนาม ⇒ ยังเป็นหัวบล็อกผู้รับ
        Assert.False(OcrSignatureSlot.IsSignatureSlot(t, t.IndexOf("ผู้รับสินค้า", StringComparison.Ordinal)));
        Assert.Equal(4, OcrPartyLabels.FindRecipientAll(t).Count);   // สถานที่ส่งสินค้า · Shipping address · ผู้รับสินค้า · Receiver
        Assert.Null(OcrSellerContactChannel.SellerEmail(t));
        Assert.True(OcrSellerContactChannel.IsBuyerSide(t, t.IndexOf("+66942514696", StringComparison.Ordinal)));
    }

    [Fact]
    public void FooterRecipientInfoBlock_NotASignature_StaysBuyerSide()
    {
        // บล็อกข้อมูลผู้รับจริงท้ายใบ (ไม่มีเส้น/คำลงนาม) — อีเมลใต้มันยังเป็นของผู้ซื้อ
        var text = "บริษัท เอ จำกัด\n" + Rows(12) + "ผู้รับสินค้า: คุณวชิร ดิลกสัมพันธ์\nอีเมล me@buyer.co.th\n";
        Assert.Null(OcrSellerContactChannel.SellerEmail(text));
        Assert.Single(OcrPartyLabels.FindRecipientAll(text));
    }

    // ═════════ K-4 — แก้ผลสแกนเปลี่ยนสาขา ⇒ ตัดสินผู้ติดต่อใหม่ (ตัวเดียวกับเส้นสแกน) ═════════

    [Fact]
    public void Redecide_WhenUserChangedVendorKey_OnPurchaseSide()
        => Assert.True(OcrVendorBranchContact.ShouldRedecideOnCorrection(
            vendorKeyChanged: true, userPickedContact: false, documentCreated: false, salesSide: false));

    [Theory]
    [InlineData(false, false, false, false)]   // หน้าเว็บส่งค่าเดิมกลับมา (K-1) ⇒ ไม่แตะผู้ติดต่อที่ผูกไว้
    [InlineData(true, true, false, false)]     // ผู้ใช้เลือกผู้ติดต่อเอง = คำตอบสุดท้าย
    [InlineData(true, false, true, false)]     // สร้างเอกสารแล้ว — แก้ที่เอกสาร
    [InlineData(true, false, false, true)]     // ฝั่งขาย: MatchedContactId คือเรา · คู่ค้าตัดสินจากบล็อกผู้ซื้อตอนสร้าง
    public void NoRedecide_OtherwiseUntouched(bool changed, bool picked, bool created, bool sales)
        => Assert.False(OcrVendorBranchContact.ShouldRedecideOnCorrection(changed, picked, created, sales));

    [Fact]
    public void Makro_UserChangesBranchInReview_FormGetsTheBranchRow()
    {
        // สแกนเก่าผูก สนญ. (00000) · ผู้ใช้แก้ช่องรหัสสาขาเป็น 00005 ในหน้ารีวิว แล้วกด "แก้ในฟอร์มก่อน"
        var req = new OcrCorrectionRequest(VendorBranchCode: "00005", BuyerBranchCode: "00000");
        var fields = OcrCorrectedFieldList.From(req, new OcrCorrectionBaseline("00000", "00000"));
        Assert.Contains("VendorBranchCode", fields);
        Assert.True(OcrVendorBranchContact.ShouldRedecideOnCorrection(
            OcrCorrectedFieldList.BranchChanged(req.VendorBranchCode, "00000"), false, false, false));
        var pick = OcrVendorBranchContact.Decide(new[] { new OcrVendorBranchContact.Candidate(CpHq, "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)", "00000") },
            "00005", branchReliable: OcrVendorBranchContact.IsReliableBranch(0.50, userCorrected: fields.Contains("VendorBranchCode")));
        Assert.Equal(OcrVendorBranchOutcome.NewBranchRow, pick.Outcome);   // เดิมฟอร์มได้ CpHq
    }

    // ═════════ K-5 (คำตัดสินข้อ 19) — ล็อกสร้างผู้ติดต่อต่อ (CompanyId, เลขผู้เสียภาษี) + รายงานแถวซ้ำ ═════════

    [Fact]
    public void LockPart_SameEntity_SameKey_WhateverThePaperGrouping()
    {
        Assert.Equal(CpTin, OcrContactCreateLock.LockPart("0 10 7 567 00041 4"));
        Assert.Equal(CpTin, OcrContactCreateLock.LockPart("0-1075-67000-41-4"));
        var company = Guid.Parse("33333333-0000-0000-0000-000000000001");
        var k1 = AdvisoryLockKey.For(company, AdvisoryLockKey.OcrContactCreate, OcrContactCreateLock.LockPart("0 10 7 567 00041 4")!);
        var k2 = AdvisoryLockKey.For(company, AdvisoryLockKey.OcrContactCreate, OcrContactCreateLock.LockPart(CpTin)!);
        Assert.Equal(k1, k2);                                               // คีย์คงที่ (ไม่ขึ้นกับ process — FNV)
        Assert.NotEqual(k1, AdvisoryLockKey.For(Guid.Parse("33333333-0000-0000-0000-000000000002"),
            AdvisoryLockKey.OcrContactCreate, CpTin));                      // คนละบริษัท = คนละล็อก
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("0000000000000")]   // "ลูกค้าทั่วไป" ไม่ใช่ตัวตน
    [InlineData("12345")]           // อ่านเพี้ยน
    public void LockPart_NotAnIdentity_NoLock(string? taxId) => Assert.Null(OcrContactCreateLock.LockPart(taxId));

    [Fact]
    public void AfterLock_RowAppearedMeanwhile_IsReused_OtherwiseCreate()
    {
        Assert.Equal(CpBranch5, OcrContactCreateLock.ReuseAfterLock(new ContactKeyMatch(CpBranch5, ContactKeyBasis.ExactBranch, true)));
        Assert.Null(OcrContactCreateLock.ReuseAfterLock(new ContactKeyMatch(null, null, true)));   // มีแต่ สนญ. ⇒ สร้างแถวสาขาได้
        Assert.Null(OcrContactCreateLock.ReuseAfterLock(default));
        Assert.Contains("ไม่สร้างผู้ติดต่อซ้ำ", OcrContactCreateLock.ReusedNote("0 10 7 567 00041 4", "00005"));
    }

    [Fact]
    public void DuplicateKey_SameRuleAsContactsPage()
    {
        Assert.Equal(CpTin + "|00005", ContactDataHygiene.DuplicateKey("0-1075-67000-41-4", "5"));
        Assert.Equal(CpTin + "|00000", ContactDataHygiene.DuplicateKey(CpTin, null));
        Assert.Equal(CpTin + "|00000", ContactDataHygiene.DuplicateKey(CpTin, "00000"));
        Assert.Null(ContactDataHygiene.DuplicateKey("010756700041", "00005"));   // 12 หลัก ไม่ตัดสินด้วยเลข
        Assert.Null(ContactDataHygiene.DuplicateKey(null, "00005"));
    }

    [Fact]
    public void DuplicateGroups_ReportsExistingDuplicates_OcrFirst_SinglesIgnored()
    {
        var t0 = new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            new ContactKeyRow(CpBranch5, "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)", CpTin, "00005", "OCR-BranchAutoCreate", t0.AddMinutes(1)),
            new ContactKeyRow(Guid.Parse("11111111-0000-0000-0000-000000000055"), "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)", CpTin, "5", "OCR-BranchAutoCreate", t0.AddMinutes(2)),
            new ContactKeyRow(CpHq, "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)", CpTin, null, "user", t0),
            new ContactKeyRow(Other, "ร้านเดี่ยว", "0105555175590", "00012", "user", t0),
        };
        var groups = ContactDataHygiene.DuplicateKeyGroups(rows);
        var g = Assert.Single(groups);                       // สนญ. แถวเดียว / ร้านเดี่ยว ไม่ใช่กลุ่ม
        Assert.Equal("00005", g.BranchCode);
        Assert.Equal(2, g.Rows.Count);
        Assert.Equal(2, g.OcrCreated);
        Assert.Equal(CpBranch5, g.Rows[0].Id);              // เก่าสุดก่อน
        Assert.Empty(ContactDataHygiene.DuplicateKeyGroups(Array.Empty<ContactKeyRow>()));
    }

    // ═════════ K-3b — alias ที่เรียนบนแถว สนญ. ใช้กับแถวสาขาเลขภาษีเดียวกัน ═════════

    [Fact]
    public void VendorAlias_LearnedOnHeadOffice_CountsForTheBranchRow()
    {
        var same = ContactTaxBranchKey.SameEntityIds(CpBranch5, CpTin, new[]
        {
            new ContactKeyCandidate(CpHq, CpTin, "00000"),
            new ContactKeyCandidate(CpBranch5, CpTin, "00005"),
            new ContactKeyCandidate(Other, "0105555175590", "00000"),
        });
        var ids = OcrVendorAliasScope.VendorIds(CpBranch5, same);
        Assert.Equal(CpBranch5, ids[0]);
        Assert.True(OcrVendorAliasScope.IsVendorAlias(CpHq, ids.ToList()));      // เดิม: alias ของ สนญ. = 0 คะแนนสำหรับใบสาขา
        Assert.False(OcrVendorAliasScope.IsVendorAlias(Other, ids.ToList()));    // ผู้ขายคนละราย
        Assert.False(OcrVendorAliasScope.IsVendorAlias(null, ids.ToList()));     // alias global ตัดสินแยก
    }

    [Fact]
    public void VendorAlias_PlaceholderTaxId_OrNoVendor_StaysSelfOnly()
    {
        var zeros = ContactTaxBranchKey.SameEntityIds(CpBranch5, "0000000000000", new[]
        {
            new ContactKeyCandidate(CpHq, "0000000000000", "00000"),
        });
        Assert.Equal(new[] { CpBranch5 }, OcrVendorAliasScope.VendorIds(CpBranch5, zeros));
        Assert.Empty(OcrVendorAliasScope.VendorIds(null, new[] { CpHq }));
        Assert.Equal(new[] { CpBranch5, CpHq }, OcrVendorAliasScope.VendorIds(CpBranch5, new[] { CpHq }));   // ตัวเองอยู่ในชุดเสมอ
    }

    // ═════════ r199 A-5 — ผูก PO จากเลขบนกระดาษ เทียบ PO ค้างทั้งหมด ═════════

    private static List<OcrOpenPo> Pos(int n) => Enumerable.Range(1, n)
        .Select(i => new OcrOpenPo(Guid.Parse($"44444444-0000-0000-0000-{i:D12}"), $"PO-2026-{i:D4}")).ToList();

    [Fact]
    public void OpenPo_PaperCitesTheSeventh_IsLinked_AndCountIsReal()
    {
        var pos = Pos(7);                                         // ใหม่สุดก่อน — ใบที่ 7 คือเก่าสุด
        var plan = OcrOpenPurchaseOrders.Plan(pos, "ใบกำกับภาษี\nอ้างอิงใบสั่งซื้อ PO-2026-0007\nรวม 1,000.00");
        Assert.Equal(pos[6], plan.AutoLink);                      // เดิม: Take(5) ก่อนเทียบ ⇒ ไม่ผูก
        Assert.Equal(7, plan.Total);                              // เดิม: "ค้าง 5 ใบ"
        Assert.Equal(5, plan.Display.Count);
        Assert.Equal(pos[6], plan.Display[0]);                    // ใบที่ถูกอ้างอยู่หัวรายการ
    }

    [Fact]
    public void OpenPo_Ambiguous_OrShort_OrNone_IsNotLinked()
    {
        var pos = Pos(3);
        Assert.Null(OcrOpenPurchaseOrders.Plan(pos, "PO-2026-0001 และ PO-2026-0002").AutoLink);   // สองใบ = ให้คนเลือก
        Assert.Null(OcrOpenPurchaseOrders.Plan(new[] { new OcrOpenPo(Guid.Empty, "P1") }, "P1 ค่าสินค้า").AutoLink);
        Assert.Null(OcrOpenPurchaseOrders.Plan(pos, "ไม่มีเลขอ้างอิง").AutoLink);
        Assert.Equal(0, OcrOpenPurchaseOrders.Plan(new List<OcrOpenPo>(), "PO-2026-0001").Total);
        // ทิศตรงข้าม: ใบที่อยู่ใน 5 ใบล่าสุดยังผูกเหมือนเดิม · รายการแสดงไม่เกิน 5
        var plan = OcrOpenPurchaseOrders.Plan(pos, "po-2026-0002");
        Assert.Equal(pos[1], plan.AutoLink);
        Assert.Equal(3, plan.Display.Count);
    }
}
