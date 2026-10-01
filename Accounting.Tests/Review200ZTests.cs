using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 200 ทีม Z — แก้ผลฝ่ายค้านรอบสอง ด้านสิทธิ์/แพ็กเกจ/security/OCR (<c>erp-review/2026-09-29/review200-round2-sec.md</c>)
///
/// <para>สองครึ่งทุกหัวข้อ (CLAUDE.md §H · F2 ข้อ 8): ครึ่งแรก = ใบ/คำขอที่พังกลับมาถูก · ครึ่งหลัง = ใบ/คำขอที่ถูกอยู่แล้วไม่ถูกแตะ ·
/// การต่อสายใน service/controller ล็อกด้วย <c>tools/required_call_site_check.py</c> (บล็อก "รอบ 200 ทีม Z") ·
/// ฝั่งหน้าเว็บล็อกด้วย <c>tools/page_feature_sim.js</c> (S2-3) + <c>tools/api_feature_denial_sim.js</c> ข้อ 7–8 (RF-3)</para>
/// </summary>
public class Review200ZTests
{
    // ═════════ K2-5b / K2-5a — ชื่อสินค้าที่เป็นคำสกุลเงินไม่ใช่หลักฐานสกุลเงิน ═════════

    /// <summary>ใบเสร็จร้านอาหารไทยที่ไม่พิมพ์คำว่าบาท (POS ส่วนใหญ่) + เมนู "YEN TA FO" (เย็นตาโฟ)</summary>
    private const string NoodleReceipt =
        "ร้านก๋วยเตี๋ยวเจ๊หมวย\nใบเสร็จรับเงิน\n1 YEN TA FO 60.00\n1 น้ำเปล่า 10.00\nTOTAL 70.00\nCASH 100.00\nCHANGE 30.00";

    /// <summary>ใบบาทที่มีจำนวนเงินตัวอักษร "…บาทถ้วน" + สินค้า EURO (คัสตาร์ดเค้ก)</summary>
    private const string BakeryReceipt =
        "ร้านเบเกอรี่\nใบกำกับภาษีอย่างย่อ\nEURO CUSTARD CAKE 60.00\nรวมทั้งสิ้น 60.00\n(หกสิบบาทถ้วน)";

    [Fact]
    public void K2_5b_YenTaFoOnAThaiReceipt_IsBaht_NotJpy()
    {
        // เดิม: "YEN" ที่ไหนก็ได้ + ไม่มีคำว่าบาท ⇒ ForeignOnly ⇒ JPY (ยอดความหมายต่างหลายสิบเท่า — ผิดเงียบ)
        var r = OcrCurrencyEvidence.Read(NoodleReceipt);
        Assert.Null(r.Code);
        Assert.Equal(OcrCurrencyBasis.NoForeign, r.Basis);
        Assert.Null(OcrCurrencyEvidence.Infer("YEN TA FO 60.00\nTOTAL 60.00"));   // ตัวอย่างในผลตรวจ
    }

    [Fact]
    public void K2_5a_EuroCakeOnABahtReceipt_IsNotFlaggedUnsure()
    {
        // เดิม: "EURO" + "บาทถ้วน" + บรรทัดยอดไม่มีคำสกุล ⇒ Ambiguous ⇒ [CURRENCY-UNSURE] ⇒ หยุดอนุมัติอัตโนมัติ
        var r = OcrCurrencyEvidence.Read(BakeryReceipt);
        Assert.Null(r.Code);
        Assert.False(r.Unsure);
        Assert.Equal(OcrCurrencyBasis.NoForeign, r.Basis);
    }

    [Theory]
    [InlineData("1 YEN TA FO 60.00\n2 EURO CAKE 120.00\nรวม 180.00")]    // จำนวนก่อนชื่อสินค้า
    [InlineData("CAKE EURO 60.00\nTOTAL 60.00")]                          // คำก่อนหน้าเป็นคำละติน
    [InlineData("YUAN YANG TEA 45.00\nRMB SET 99.00\nTOTAL 144.00")]
    public void K2_5b_ProductNamesWithCurrencyWords_AreNotEvidence(string paper)
        => Assert.Null(OcrCurrencyEvidence.Infer(paper));

    [Theory]
    [InlineData("Amount 12,000 YEN\nTotal 12,000 YEN", "JPY")]           // ตัวเลข → คำ
    [InlineData("INVOICE\nYEN 5,000\nGRAND TOTAL 5,000", "JPY")]          // คำ → ตัวเลข (ต้นบรรทัด)
    [InlineData("Currency: YEN\nTotal 12,000", "JPY")]                    // ป้ายสกุลเงิน
    [InlineData("Invoice\nTotal amount 1,070.00 EURO", "EUR")]            // บนบรรทัดยอดรวม
    [InlineData("Invoice\nTotal € 1,070.00", "EUR")]                      // สัญลักษณ์ติดตัวเลข (ใหม่)
    [InlineData("Invoice\nTotal £1,070.00", "GBP")]
    [InlineData("INVOICE\nTOTAL USD 1,070.00", "USD")]                    // รหัส ISO ทุกที่ (เดิม)
    [InlineData("Payment in EUR\nTotal 1,070.00", "EUR")]
    public void K2_5b_RealCurrencyEvidence_StillReadsForeign(string paper, string code)
        => Assert.Equal(code, OcrCurrencyEvidence.Infer(paper));

    [Fact]
    public void K2_5_PapersThatWereRight_AreUntouched()
    {
        // ใบ Makro (บาทล้วน) · ใบสองสกุล (ไม่รู้ — ธงคงเดิม) · ใบ USD ที่มีบรรทัดอ้างอิงอัตรา (USD เดิม)
        Assert.Null(OcrCurrencyEvidence.Infer(OcrMakroBranchVendorTests.MakroPhoto));
        var two = "INVOICE\nAmount (USD) 1,000.00\nVAT 7% (USD) 70.00\nTotal (USD) 1,070.00\nExchange rate 34.00\n"
                  + "Amount (THB) 34,000.00\nVAT (THB) 2,380.00\nTotal (THB) 36,380.00";
        Assert.True(OcrCurrencyEvidence.Read(two).Unsure);
        var usd = "Amazon Web Services, Inc.\nTax Invoice\nExchange rate for reference only: 1 USD = 36.50 THB\nTotal amount due USD 1,070.00\n";
        Assert.Equal("USD", OcrCurrencyEvidence.Infer(usd));
        // ใบเยนจริงที่ยอดรวมบอก YEN และมีคำว่าบาทในบรรทัดโอนเงิน ⇒ ยอดรวมชนะ
        var jpyWithThb = "INVOICE\nItem A 10,000\nTOTAL 10,000 YEN\nโอนเงินเข้าบัญชีเงินบาท";
        Assert.Equal("JPY", OcrCurrencyEvidence.Infer(jpyWithThb));
    }

    // ═════════ K2-1 — ทิศตรงข้ามของ C-01: ผู้ติดต่อเดิมที่ไม่ใช่ลูกค้า · ชื่อตรงหลายสาขาของนิติบุคคลเดียว · โน้ตเสมอเมื่อสร้างใหม่ ═════════

    private static readonly Guid AmHq = Guid.Parse("44444444-0000-0000-0000-000000000001");
    private static readonly Guid AmBr3 = Guid.Parse("44444444-0000-0000-0000-000000000003");
    private static readonly Guid Other = Guid.Parse("44444444-0000-0000-0000-000000000009");
    private const string AmTin = "0103565012345";

    [Fact]
    public void K2_1a_ExactNameRow_ThatIsNotTickedAsCustomer_IsUsed_NotDuplicated()
    {
        var pick = OcrCounterpartyMatch.PickBuyerByName("หจก. แอม แฮปปี้เนส",
            new[] { new OcrCounterpartyCandidate(AmHq, "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส", IsCustomer: false) });
        Assert.Equal(AmHq, pick.ContactId);
        Assert.Equal(OcrCounterpartyNameBasis.ExactName, pick.Basis);
    }

    [Fact]
    public void K2_1b_SameNameOnHqAndBranchRowsOfOneTaxpayer_PaperWithoutBranch_PicksHeadOffice()
    {
        // แถวสาขาที่รอบ 197 สร้างด้วยชื่อนิติบุคคลเดียวกัน — เดิม: ชื่อตรง 2 แถว + ลูกค้า ≠ 1 ราย ⇒ กำกวม ⇒ สร้างแถวที่สามทุกครั้งที่สแกน
        var rows = new[]
        {
            new OcrCounterpartyCandidate(AmBr3, "หจก. แอม แฮปปี้เนส", true, AmTin, "00003"),
            new OcrCounterpartyCandidate(AmHq, "หจก. แอม แฮปปี้เนส", true, AmTin, "00000"),
        };
        var pick = OcrCounterpartyMatch.PickBuyerByName("หจก. แอม แฮปปี้เนส", rows);
        Assert.Equal(AmHq, pick.ContactId);
        Assert.Equal(OcrCounterpartyNameBasis.SameEntityBranch, pick.Basis);
        // ใบระบุสาขา 00003 ⇒ แถวสาขานั้น
        Assert.Equal(AmBr3, OcrCounterpartyMatch.PickBuyerByName("หจก. แอม แฮปปี้เนส", rows, "00003").ContactId);
        // ชื่อถูกตัด + สองแถวของนิติบุคคลเดียว ⇒ สำนักงานใหญ่ (ไม่กำกวม)
        Assert.Equal(AmHq, OcrCounterpartyMatch.PickBuyerByName("แอม แฮปปี้", rows).ContactId);
    }

    [Fact]
    public void K2_1b_SameNameOnTwoDifferentTaxpayers_StaysAmbiguous_WithNote()
    {
        // ทิศตรงข้าม: ชื่อตรงแต่คนละเลขผู้เสียภาษี = คนละราย ⇒ ไม่เดา (เหมือนเดิม) · สาขาบนกระดาษไม่มีแถว ⇒ ไม่ผูกสาขาอื่นแทน
        var two = new[]
        {
            new OcrCounterpartyCandidate(AmHq, "หจก. แอม แฮปปี้เนส", true, AmTin, "00000"),
            new OcrCounterpartyCandidate(Other, "หจก. แอม แฮปปี้เนส", true, "0105555000017", "00000"),
        };
        var pick = OcrCounterpartyMatch.PickBuyerByName("หจก. แอม แฮปปี้เนส", two);
        Assert.Null(pick.ContactId);
        Assert.Equal(OcrCounterpartyNameBasis.Ambiguous, pick.Basis);
        Assert.StartsWith("[BUYER]", pick.Note);
        var sameEntity = new[]
        {
            new OcrCounterpartyCandidate(AmHq, "หจก. แอม แฮปปี้เนส", true, AmTin, "00000"),
            new OcrCounterpartyCandidate(AmBr3, "หจก. แอม แฮปปี้เนส", true, AmTin, "00003"),
        };
        Assert.Null(OcrCounterpartyMatch.PickBuyerByName("หจก. แอม แฮปปี้เนส", sameEntity, "00007").ContactId);
    }

    [Fact]
    public void K2_1c_TruncatedName_OnlyANonCustomerContainsIt_CreatesNew_WithANoteNamingTheExistingRow()
    {
        // เดิม: Basis.None ไม่มีโน้ต ⇒ ลูกค้าซ้ำเกิดเงียบ · ตอนนี้ยังไม่ผูกผู้ขายล้วนอัตโนมัติ (อาจคนละราย) แต่บอกชื่อให้เลือกได้
        var pick = OcrCounterpartyMatch.PickBuyerByName("แอม แฮปปี้",
            new[] { new OcrCounterpartyCandidate(AmHq, "หจก. แอม แฮปปี้เนส", IsCustomer: false) });
        Assert.Null(pick.ContactId);
        Assert.Equal(OcrCounterpartyNameBasis.None, pick.Basis);
        Assert.StartsWith("[BUYER]", pick.Note);
        Assert.Contains("หจก. แอม แฮปปี้เนส", pick.Note);
    }

    [Fact]
    public void K2_1_EveryNoMatchOutcome_CarriesABuyerNote()
    {
        var none = OcrCounterpartyMatch.PickBuyerByName("บริษัท ใหม่เอี่ยม จำกัด", Array.Empty<OcrCounterpartyCandidate>());
        Assert.Null(none.ContactId);
        Assert.StartsWith("[BUYER]", none.Note);
        Assert.StartsWith("[BUYER]", OcrCounterpartyMatch.NewBranchNote("หจก. แอม แฮปปี้เนส", AmTin, "00003"));
        Assert.Contains("สาขาที่", OcrCounterpartyMatch.NewBranchNote("หจก. แอม แฮปปี้เนส", AmTin, "00003"));
    }

    [Fact]
    public void K2_1_RightAnswers_AreUntouched()
    {
        // ลูกค้ารายเดียวที่ครอบชื่อที่ถูกตัด (RG-03) · ชื่อตรงรายเดียว — เหมือนเดิม ไม่มีโน้ต
        var one = new[] { new OcrCounterpartyCandidate(AmHq, "หจก. แอม แฮปปี้เนส", true, AmTin, "00000") };
        var truncated = OcrCounterpartyMatch.PickBuyerByName("แอม แฮปปี้", one);
        Assert.Equal(AmHq, truncated.ContactId);
        Assert.Equal(OcrCounterpartyNameBasis.UniqueSuperstring, truncated.Basis);
        Assert.Null(truncated.Note);
        Assert.Null(OcrCounterpartyMatch.PickBuyerByName("หจก. แอม แฮปปี้เนส", one).Note);
    }

    [Theory]
    [InlineData("หจก.แอม แฮปปี้เนส", "แฮปปี้เนส")]           // คำแรกมีรูปนิติบุคคลปน ⇒ ใช้คำที่สะอาด
    [InlineData("บริษัท ซีพี ออลล์ จำกัด (มหาชน)", "ออลล์")]
    [InlineData("บจก.", null)]
    [InlineData("แอม", null)]
    public void K2_1a_PrefilterToken_IsALongWordWithoutLegalForm(string name, string? token)
        => Assert.Equal(token, OcrCounterpartyMatch.PrefilterToken(name));

    // ═════════ RF-6 — ทางเข้าเฉพาะกิจของ "อธิบายรายการผิดปกติ" ใช้ตัวประกอบเดียวกับทางเข้าที่บันทึก ═════════

    private const string StudentJson =
        "{\"primary\":\"LikelyError\",\"confidence\":0.9,\"reasoning\":\"ยอดห่างจากประวัติมาก (MAD z = 8.1)\","
        + "\"suggested_actions\":[\"ตรวจจุดทศนิยม\"],\"risks\":[\"ยอดผิดเข้าสมุดบัญชี\"],\"basis\":\"mad_z_series\"}";

    [Fact]
    public void RF6_KillSwitch_StudentAnswer_ShowsTheStudentExplanation_NotRoutingText()
    {
        var v = AnomalyExplainVerdict.View(usedAi: false, fromLocalModel: true, "LikelyError", 0.9m,
            "AI ไม่พร้อม — ใช้คำตอบ local", Array.Empty<string>(), Array.Empty<string>(), StudentJson);
        Assert.Equal("LikelyError", v.Primary);
        Assert.Equal(0.9m, v.Confidence);
        Assert.Equal("ยอดห่างจากประวัติมาก (MAD z = 8.1)", v.Reasoning);
        Assert.Equal(new[] { "ตรวจจุดทศนิยม" }, v.SuggestedActions);
        Assert.Equal(new[] { "ยอดผิดเข้าสมุดบัญชี" }, v.Risks);
    }

    [Fact]
    public void RF6_TeacherAnswer_OutOfSet_IsCoerced_AndKeepsTeacherText()
    {
        var v = AnomalyExplainVerdict.View(usedAi: true, fromLocalModel: false, "likely_error", 0.8m,
            "ครูอธิบาย", new[] { "a" }, null, StudentJson);
        Assert.Equal("LikelyError", v.Primary);     // รูปแบบต่าง ⇒ ค่าในชุด
        Assert.Equal(0.8m, v.Confidence);
        Assert.Equal("ครูอธิบาย", v.Reasoning);    // ครูตอบ ⇒ ไม่อ่าน JSON ของนักเรียน
        Assert.Empty(v.Risks);
        var free = AnomalyExplainVerdict.View(true, false, "ดูเหมือนปกติ", 0.8m, "x", null, null, null);
        Assert.Equal("NeedReview", free.Primary);  // นอกชุด ⇒ ให้คนตรวจ
        Assert.Null(free.Confidence);              // ความมั่นใจไม่ใช่ของคำนี้
        Assert.Null(AnomalyExplainVerdict.View(false, false, null, null, null, null, null, null).Primary);
    }

    // ═════════ RF-2 — ค่าธีม CMS / สีแบรนด์ที่ต่อเข้า CSS ผ่านตัวตรวจตัวเดียว ═════════

    [Theory]
    [InlineData("red; } body { background:url(//evil) }")]
    [InlineData("#fff</style><script>alert(1)</script>")]
    [InlineData("expression(alert(1))")]
    [InlineData("rgb(1,2,3);x")]
    public void RF2_InjectedColor_IsRejectedAtSave_AndRendersAsDefault(string evil)
    {
        Assert.Null(CssThemeValue.SafeColor(evil));
        Assert.Equal("#4F46E5", CssThemeValue.Color(evil, "#4F46E5"));
        var errs = CssThemeValue.RejectReasons(new CmsThemeStyleInput(evil, null, null, null, null, null, null, null, null, null, 8));
        Assert.Single(errs);
        Assert.Contains("สีหลัก", errs[0]);
    }

    [Theory]
    [InlineData("Inter', sans-serif; } body { color:red } x { y: '")]
    [InlineData("Noto</style>")]
    public void RF2_InjectedFontOrWidth_IsRejected(string evil)
    {
        Assert.Null(CssThemeValue.SafeFontName(evil));
        Assert.Equal("Inter", CssThemeValue.FontName(evil, "Inter"));
        Assert.Null(CssThemeValue.SafeLength(evil));
        Assert.Equal(2, CssThemeValue.RejectReasons(new CmsThemeStyleInput(null, null, null, null, null, null, null, evil, null, evil, 8)).Count);
    }

    [Fact]
    public void RF2_LegitThemeValues_AreUntouched()
    {
        Assert.Equal("#4F46E5", CssThemeValue.SafeColor("#4f46e5"));
        Assert.Equal("rgb(79, 70, 229)", CssThemeValue.SafeColor("rgb(79, 70, 229)"));
        Assert.Equal("rgba(0,0,0,0.5)", CssThemeValue.SafeColor("rgba(0,0,0,0.5)"));
        Assert.Equal("hsl(240 50% 50% / 0.5)", CssThemeValue.SafeColor("hsl(240 50% 50% / 0.5)"));
        Assert.Equal("transparent", CssThemeValue.SafeColor("transparent"));
        Assert.Equal("Noto Sans Thai", CssThemeValue.SafeFontName("Noto Sans Thai"));
        Assert.Equal("IBM Plex Sans Thai Looped", CssThemeValue.SafeFontName("IBM Plex Sans Thai Looped"));
        Assert.Equal("1280px", CssThemeValue.SafeLength("1280px"));
        Assert.Equal("90%", CssThemeValue.SafeLength("90%"));
        Assert.Equal("1.125rem", CssThemeValue.SafeLength("1.125rem"));
        Assert.Equal("none", CssThemeValue.SafeLength("none"));
        Assert.Equal(8, CssThemeValue.Radius(8));
        Assert.Equal(0, CssThemeValue.Radius(-3));
        Assert.Empty(CssThemeValue.RejectReasons(new CmsThemeStyleInput("#4F46E5", "#0EA5E9", "#F59E0B", "#FFFFFF", "#F9FAFB",
            "#111827", "#6B7280", "Inter", "Noto Sans Thai", "1280px", 8)));
        // ช่องว่าง = ใช้ค่าเริ่มต้น (ผ่าน)
        Assert.Empty(CssThemeValue.RejectReasons(new CmsThemeStyleInput("", null, null, null, null, null, null, "", null, "", 0)));
        Assert.Single(CssThemeValue.RejectReasons(new CmsThemeStyleInput(null, null, null, null, null, null, null, null, null, null, 999)));
    }

    // ═════════ S2-6 — /api/v1: กันการเขียน (ข้อ 23) · อ่านไม่ผ่านด่าน · ไม่สร้าง FreeTrial ═════════

    private static readonly TenantCompanyTarget ApiKeyCaller = new(Guid.NewGuid(), TenantCompanySource.ApiKey, false, false);
    private static readonly TenantCompanyTarget WebCaller = new(Guid.NewGuid(), TenantCompanySource.Route, false, false);
    private static readonly TenantCompanyTarget PartnerCaller = new(Guid.NewGuid(), TenantCompanySource.Header, true, false);

    [Fact]
    public void S2_6_PublicApiRead_SkipsTheGate_WriteStillGated()
    {
        Assert.True(SubscriptionGatePolicy.SkipsPublicApiRead(ApiKeyCaller, isWrite: false));
        Assert.False(SubscriptionGatePolicy.SkipsPublicApiRead(ApiKeyCaller, isWrite: true));   // ข้อ 23: เขียนต้องถูกตรวจ
        // ทิศตรงข้าม: หน้าเว็บ/partner อ่านยังเดินด่านเดิม (subscription ถูกยกเลิก = บล็อกทั้งอ่าน)
        Assert.False(SubscriptionGatePolicy.SkipsPublicApiRead(WebCaller, isWrite: false));
        Assert.False(SubscriptionGatePolicy.SkipsPublicApiRead(PartnerCaller, isWrite: false));
    }

    [Fact]
    public void S2_6_PublicApi_NeverCreatesASubscriptionRow_WebStillDoes()
    {
        Assert.False(SubscriptionGatePolicy.MayCreateSubscriptionRow(ApiKeyCaller));
        Assert.True(SubscriptionGatePolicy.MayCreateSubscriptionRow(WebCaller));
        Assert.True(SubscriptionGatePolicy.MayCreateSubscriptionRow(PartnerCaller));
    }

    [Fact]
    public void S2_6_PublicApiWrite_OfASuspendedCompany_IsStillBlockedWhenEnforced()
    {
        // ทิศตรงข้าม: การข้าม "อ่าน" ต้องไม่ทำให้ "เขียน" หลวม — ตัวตัดสินเดิม (Decide) บล็อกเมื่อบังคับ
        var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(
            SubscriptionStatus.Active, FeatureFlags.None, IsWrite: true, SubscriptionWriteGateMode.Enforce, true, true, null));
        Assert.Equal(SubscriptionGateReason.CompanySuspended, v.Block);
    }

    // ═════════ S2-3 — ฟีเจอร์ของหน้า = ฟีเจอร์ของ route ข้อมูลหลัก (เซิร์ฟเวอร์บอก) ═════════

    [Fact]
    public void S2_3_PageMainFeatures_ComeFromTheRouteTable()
    {
        var m = SubscriptionGatePolicy.PageMainFeatures();
        Assert.Equal(nameof(FeatureFlags.CmsEcommerce), m["cms-orders"]);
        Assert.Equal(nameof(FeatureFlags.CmsBooking), m["cms-bookings"]);
        Assert.Equal(nameof(FeatureFlags.DocumentOCR), m["document-scan"]);
        Assert.Null(m["lodging"]);   // route หลักไม่ถูก gate ⇒ 403 ในหน้านั้นเป็นเบื้องหลังเสมอ
        Assert.False(m.ContainsKey("payroll"));   // หน้าอื่น = ฟีเจอร์ของเมนู (พฤติกรรมเดิม)
    }
}
