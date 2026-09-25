using Accounting.Helpers;
using Accounting.Services.Implementations.Ocr;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 197 ทีม K — ใบ Makro (บมจ.ซีพี แอ็กซ์ตร้า) หน้า 3/3 สาขาชลบุรี 00005: "ได้เลขผู้ขายและสาขาถูก แต่ชื่อผิด ('ma ro') ·
/// ผูกผู้ติดต่อสำนักงานใหญ่ · อีเมลผู้ซื้อปนเข้าผู้ติดต่อผู้ขาย" (<c>erp-review/2026-09-25/makro-branch/BRIEF.md</c>)
///
/// <para><see cref="MakroPhoto"/> = <b>ข้อความถอดจากภาพ <c>paper-page3.jpg</c></b> (ภาพถ่าย · ไม่มีข้อความดิบจาก engine ในมือ)
/// เรียงบรรทัดแบบที่ engine อ่านสองคอลัมน์ (ซ้าย/ขวาสลับกันตามแนวตั้ง) — ต่างจาก <c>OcrPaperSamples.MakroPage3of3</c>
/// (รอบ 192) ตรงที่หัวใบพิมพ์ชื่อนิติบุคคลเต็ม + เลขผู้เสียภาษีแบ่งกลุ่ม <b>0 10 7 567 00041 4</b> (1-2-1-3-5-1) ตามกระดาษจริง
/// และมีบล็อก "สถานที่ส่งสินค้า / ผู้รับสินค้า / อีเมล์" ของผู้ซื้อ</para>
///
/// <para>สองครึ่งตาม CLAUDE.md §H: ครึ่งแรก = ใบนี้กลับมาถูก · ครึ่งหลัง = ใบที่ถูกอยู่แล้ว (ชื่อนิติบุคคลอยู่แล้ว · เลขผู้ซื้อ ·
/// เลขบุคคลธรรมดา · ชื่อเรา · อีเมลผู้ขายบนหัวใบ/ท้ายใบ · กระดาษไม่มีป้าย) ไม่ถูกแตะ</para>
/// </summary>
public class OcrMakroBranchVendorTests
{
    /// <summary>ข้อความถอดจากภาพ paper-page3.jpg (ดูหัวคลาส)</summary>
    public const string MakroPhoto =
        "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)\n" +
        "makro\n" +
        "ต้นฉบับลูกค้า\n" +
        "For Customer\n" +
        "สำนักงานใหญ่ โทร. 0-2067-8999\n" +
        "เลขประจำตัวผู้เสียภาษีอากร 0 10 7 567 00041 4\n" +
        "ใบเสร็จรับเงิน/ใบกำกับภาษี\n" +
        "RECEIPT / TAX INVOICE\n" +
        "หน้าที่ 3 จาก 3\n" +
        "ชื่อลูกค้า/Customer Name หจก.แอม แฮปปี้เนส\n" +
        "สาขาที่ออกใบกำกับภาษี/ Branch 00005\n" +
        "รหัสสมาชิกลูกค้า/Customer No. 00596437340722\n" +
        "ที่อยู่/ Address บมจ.ซีพี แอ็กซ์ตร้า สาขาชลบุรี\n" +
        "ที่อยู่/ Address 202/24 ม.5 ซอยบ้านห้วยกุ่ม4 ตำบลบาง\n" +
        "55/3 หมู่ 2 ถ.สุขุมวิท เมืองชลบุรี\n" +
        "พระ อำเภอศรีราชา จังหวัดชลบุรี 20110\n" +
        "ต.เสม็ด อ.เมือง จ.ชลบุรี\n" +
        "เลขประจำตัวผู้เสียภาษี/ Tax ID 0203562005871 สาขา 00000\n" +
        "20000\n" +
        "สถานที่ส่งสินค้า/ Shipping address วชิร ดิลกสัมพันธ์\n" +
        "เลขที่ใบกำกับภาษี/ Tax Invoice No. 005901363513\n" +
        "ที่อยู่/ Address 202/24 ม.5, Tambon Bang Phra ตำบล\n" +
        "วันที่ใบกำกับภาษี/ Tax Invoice Date 09/09/2026\n" +
        "บางพระ อำเภอศรีราชา จังหวัดชลบุรี 20110\n" +
        "วันที่สั่งซื้อ/ Order Date 07/09/2026\n" +
        "ชื่อผู้รับสินค้า/ Receiver วชิร ดิลกสัมพันธ์\n" +
        "เลขที่สั่งซื้อ/ Order No. 8541850095A\n" +
        "อีเมล์/ E-mail taketime.bangphra@gmail.com\n" +
        "วิธีการชำระเงิน/ Payment type CC_PreAuth\n" +
        "เบอร์ติดต่อ/ TEL No. +66942514696\n" +
        "(หน่วย:บาท)\n" +
        "ลำดับที่ รหัสสินค้า รายละเอียด จำนวน/น้ำหนัก หน่วยบรรจุ ราคาต่อหน่วย รหัส ภ.พ. จำนวนเงินรวม\n" +
        "ITEM ARTICLE NO. DESCRIPTION QUANTITY UNIT UNIT PRICE VAT CODE TOTAL\n" +
        "จำนวนชิ้น รหัส ภ.พ. ราคาสินค้า ภาษีมูลค่าเพิ่ม รวม\n" +
        "17 1 6,260.00 0.00 6,260.00\n" +
        "134 2 16,403.97 1,148.28 17,552.25\n" +
        "รวม 22,663.97 1,148.28 23,812.25\n" +
        "จำนวนเงินที่ต้องชำระ สองหมื่นสามพันแปดร้อยสิบสองบาทยี่สิบห้าสตางค์\n" +
        "ราคาสินค้ารวมภาษีมูลค่าเพิ่ม/ TOTAL 24,110.00\n" +
        "หมายเหตุ/ Remark เงื่อนไข/ Condition:\n" +
        "หักส่วนลด/ DISCOUNT 297.75\n" +
        "รหัส ภ.พ. 1=สินค้าได้รับการยกเว้นภาษีมูลค่าเพิ่ม 2=สินค้าที่ต้องเสียภาษีมูลค่าเพิ่ม\n" +
        "จำนวนเงินรวมสุทธิ/ AMOUNT 23,812.25\n" +
        "หักเงินมัดจำ/ DEPOSIT 0.00\n" +
        "ราคาสินค้าที่ต้องชำระ / NET AMOUNT 23,812.25\n" +
        "ประเภทการชำระเงิน / Payment type CC_PreAuth Coupon Voucher ยอดเงินรวม\n" +
        "จำนวนเงิน / Amount 23,812.25 0.00 0.00 23,812.25";

    private const string CpTin = "0107567000414";
    private const string OurTin = "0203562005871";
    private const string OurName = "หจก.แอม แฮปปี้เนส";
    private const string CpAxtra = "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน)";

    private static int Pos(string text, string needle) => text.IndexOf(needle, StringComparison.Ordinal);

    // ═════════ ข้อ 2 — เลขผู้ขายแบ่งกลุ่ม 1-2-1-3-5-1 ⇒ กุญแจพิสูจน์ได้ ⇒ ทะเบียนชนะชื่อโลโก้ ═════════

    [Fact]
    public void LooseGroupingTaxId_WithLabel_IsFoundAndLabelled()
    {
        var cands = SmartFieldExtractor.ExtractTaxIdCandidates(MakroPhoto);
        var cp = cands.Single(c => c.Id == CpTin);                 // เดิม: ไม่มีในรายการเลย (Pattern รู้จักแค่ 1-4-5-2-1)
        Assert.True(cp.Labelled);
        Assert.Equal(Pos(MakroPhoto, "0 10 7 567 00041 4"), cp.Position);
        Assert.Contains(cands, c => c.Id == OurTin && c.Labelled); // เลขผู้ซื้อยังอยู่
        // ลำดับในรายการ = ลำดับบนกระดาษ (เลขหัวใบมาก่อนเลขผู้ซื้อ — ตัวจาก pattern หลวมไม่ไปต่อท้าย)
        Assert.True(cands.FindIndex(c => c.Id == CpTin) < cands.FindIndex(c => c.Id == OurTin));
    }

    [Fact]
    public void Makro_VendorKey_IsNowProven_SoRegistryNameBeatsLogo()
    {
        var cp = SmartFieldExtractor.ExtractTaxIdCandidates(MakroPhoto).Single(c => c.Id == CpTin);
        var labels = OcrPartyLabels.FindAll(MakroPhoto);
        var key = OcrVendorKeyEvidence.Judge(CpTin, OurTin, OurTin, cp.Labelled, cp.Position,
            labels.BuyerPos, labels.SellerPos, MakroPhoto.Length);
        Assert.Equal(VendorKeyEvidence.ProvenSellerKey, key);
        // ผลที่ปลายทาง: "ma ro" ไม่คล้ายชื่อทะเบียนเลย แต่กุญแจพิสูจน์แล้ว ⇒ ทะเบียนชนะ (เดิม KeyLooksWrong ⇒ ชื่อ 50% · เลข 30% บนจอ)
        Assert.Equal(DbdTrustVerdict.KeyVerifiedNameDiffers,
            DbdIdentityGuard.Judge(CpAxtra, "ma ro", 0.0, keyProven: key == VendorKeyEvidence.ProvenSellerKey));
    }

    [Theory]
    [InlineData("17 1 6,260.00 0.00 6,260.00")]                 // แถวตารางรหัส ภ.พ.
    [InlineData("เลขประจำตัวผู้เสียภาษี 0203562005871 00000")]   // เลข + สาขาติดกัน — pattern หลวมไม่เฉือน 13 จาก 18 หลัก
    [InlineData("เลขประจำตัวผู้เสียภาษี 1 2 3 4 5 6 7 8 9 0 1 2 3 4")]   // 14 หลักคั่นเว้นวรรค
    public void LooseGrouping_DoesNotInventNumbers(string text)
    {
        var ids = SmartFieldExtractor.ExtractTaxIdCandidates(text).Select(c => c.Id).ToList();
        Assert.All(ids, id => Assert.Equal(OurTin, id));   // มีได้แค่เลขที่พิมพ์ครบ 13 หลักติดกันจริง
    }

    [Fact]
    public void LooseGrouping_WithoutLabel_IsIgnored()
    {
        // pattern หลวมรับเฉพาะตัวที่มีป้ายกำกับ — เลขลอย ๆ แบ่งกลุ่มแปลก ๆ ในตารางไม่กลายเป็นเลขผู้เสียภาษี
        Assert.Empty(SmartFieldExtractor.ExtractTaxIdCandidates("รหัสอ้างอิง ABC\nรายการ 0 10 7 567 00041 4 ชิ้น"));
    }

    // ═════════ ข้อ 2 — ชื่อโลโก้ "ma ro" → ชื่อนิติบุคคลที่พิมพ์เหนือเลขผู้ขาย ═════════

    [Theory]
    [InlineData("ma ro", true)]
    [InlineData("makro", true)]
    [InlineData("DECATHLON", true)]
    [InlineData("", true)]
    [InlineData(CpAxtra, false)]
    [InlineData("บมจ.ซีพี แอ็กซ์ตร้า", false)]
    [InlineData("หจก.แอม แฮปปี้เนส", false)]
    [InlineData("Wine Pro Co.,Ltd.", false)]
    public void LacksLegalForm(string name, bool expected) => Assert.Equal(expected, OcrVendorLegalName.LacksLegalForm(name));

    private static OcrPrintedLegalName? FindAbove(string text, string tin)
    {
        var c = SmartFieldExtractor.ExtractTaxIdCandidates(text).FirstOrDefault(x => x.Id == tin);
        var labels = OcrPartyLabels.FindAll(text);
        return OcrVendorLegalName.FindAboveTaxId(text, tin, string.IsNullOrEmpty(c.Id) ? -1 : c.Position,
            labels.BuyerPos, labels.SellerPos, new string?[] { OurName, null });
    }

    [Fact]
    public void Makro_PrintedLegalNameAboveTaxId_IsFound()
    {
        var printed = FindAbove(MakroPhoto, CpTin);
        Assert.NotNull(printed);
        Assert.Equal(CpAxtra, printed!.Name);                        // ไม่ใช่ "makro" / "สำนักงานใหญ่ โทร." ที่อยู่ใกล้กว่า
        Assert.Equal(Pos(MakroPhoto, CpAxtra), printed.Position);
    }

    [Fact]
    public void LogoOnSameLineAfterLegalName_IsCutAfterLegalSuffix()
    {
        const string text = "บริษัท ซีพี แอ็กซ์ตร้า จำกัด (มหาชน) makro\nสำนักงานใหญ่ โทร. 0-2067-8999\n"
            + "เลขประจำตัวผู้เสียภาษีอากร 0 10 7 567 00041 4\n";
        Assert.Equal(CpAxtra, FindAbove(text, CpTin)?.Name);
    }

    [Fact]
    public void BuyerTaxId_NeverYieldsAVendorName()
    {
        // ทิศตรงข้าม: เลขในบล็อกผู้ซื้อ — ห้ามเอาบรรทัดเหนือมันมาเป็นชื่อผู้ขาย
        Assert.Null(FindAbove(MakroPhoto, OurTin));
    }

    [Fact]
    public void OurOwnNameAboveTheNumber_IsNotTheVendor()
    {
        const string text = "หจก.แอม แฮปปี้เนส\nเลขประจำตัวผู้เสียภาษีอากร 0 10 7 567 00041 4\n";
        Assert.Null(FindAbove(text, CpTin));
    }

    [Fact]
    public void PersonalTaxId_IsNotTouched()
    {
        // ผู้ขายบุคคลธรรมดาใช้ชื่อร้านได้ตามปกติ — ตัวนี้ทำงานเฉพาะเลขนิติบุคคล (ขึ้นต้น 0)
        Assert.False(OcrVendorLegalName.IsJuristicTaxId("1103700012346"));
        Assert.Null(OcrVendorLegalName.FindAboveTaxId("บริษัท เอ จำกัด\nเลขประจำตัวผู้เสียภาษี 1103700012346", "1103700012346",
            Pos("บริษัท เอ จำกัด\nเลขประจำตัวผู้เสียภาษี 1103700012346", "1103700012346"), null, null, null));
    }

    [Fact]
    public void LegalNameTooFarAbove_IsNotGuessed()
    {
        var text = CpAxtra + "\na\nb\nc\nd\ne\nf\ng\nเลขประจำตัวผู้เสียภาษีอากร 0 10 7 567 00041 4";
        Assert.Null(FindAbove(text, CpTin));       // > MaxLinesAbove บรรทัด = ไม่เดา
    }

    [Fact]
    public void UnknownPosition_ReturnsNull()
        => Assert.Null(OcrVendorLegalName.FindAboveTaxId(MakroPhoto, CpTin, -1, null, null, null));

    [Fact]
    public void KnownLegalName_SkipsLogoNamesThatLeakedIntoContacts()
    {
        Assert.Equal(CpAxtra, OcrVendorLegalName.PickKnownLegalName(CpTin, new[] { "ma ro", null, CpAxtra + " (สำนักงานใหญ่)" }));
        Assert.Null(OcrVendorLegalName.PickKnownLegalName(CpTin, new string?[] { "ma ro", "makro" }));
        Assert.Null(OcrVendorLegalName.PickKnownLegalName("1103700012346", new string?[] { CpAxtra }));
    }

    // ═════════ ข้อ 3 — อีเมล/เบอร์ของผู้ซื้อห้ามเป็นของผู้ขาย ═════════

    [Fact]
    public void Makro_BuyerRecipientEmail_IsNotTheVendorEmail()
    {
        Assert.Null(OcrSellerContactChannel.SellerEmail(MakroPhoto));   // เดิม: taketime.bangphra@gmail.com ⇒ ลงผู้ติดต่อ ซีพี แอ็กซ์ตร้า
        Assert.Contains("taketime.bangphra@gmail.com", OcrSellerContactChannel.BuyerSideEmails(MakroPhoto));
    }

    [Fact]
    public void Makro_HeaderPhoneIsVendor_RecipientPhoneIsBuyer()
    {
        Assert.False(OcrSellerContactChannel.IsBuyerSide(MakroPhoto, Pos(MakroPhoto, "0-2067-8999")));
        Assert.True(OcrSellerContactChannel.IsBuyerSide(MakroPhoto, Pos(MakroPhoto, "+66942514696")));
    }

    [Fact]
    public void VendorEmailInHeader_StillPicked_EvenWhenBuyerEmailExists()
    {
        const string text = "บริษัท เอ จำกัด โทร 02-111-2222 อีเมล sales@a.co.th\n"
            + "ลูกค้า: หจก.แอม แฮปปี้เนส อีเมล me@ours.co.th\n";
        Assert.Equal("sales@a.co.th", OcrSellerContactChannel.SellerEmail(text));
    }

    [Fact]
    public void VendorEmailInFooterFarBelowBuyerBlock_StillPicked()
    {
        // บล็อกผู้ซื้อเป็นกล่องสั้น — อีเมลผู้ขายท้ายกระดาษใต้ตารางสินค้า (> MaxBlockLines บรรทัด) ต้องไม่ถูกทิ้ง
        var rows = string.Concat(Enumerable.Range(1, 12).Select(i => $"{i} สินค้า {i} 1 100.00 100.00\n"));
        var text = "บริษัท เอ จำกัด\nลูกค้า: หจก.แอม แฮปปี้เนส\n" + rows + "สอบถาม sales@a.co.th\n";
        Assert.Equal("sales@a.co.th", OcrSellerContactChannel.SellerEmail(text));
    }

    [Fact]
    public void PaperWithoutPartyLabels_KeepsOldBehaviour_FirstEmail()
        => Assert.Equal("shop@x.com", OcrSellerContactChannel.SellerEmail("ร้าน X\nโทร 081-111-2222\nshop@x.com\n"));

    [Fact]
    public void OurOwnEmail_IsNeverTheVendors()
        => Assert.Null(OcrSellerContactChannel.SellerEmail("ร้าน X\nme@ours.co.th\n", ourEmail: "ME@ours.co.th"));

    [Fact]
    public void CopyMarker_ForCustomer_IsNotABuyerLabel()
    {
        // กล่อง "ต้นฉบับลูกค้า / For Customer" มุมขวาบน — เดิมนับเป็นป้ายผู้ซื้อเหนือเลขผู้ขาย ⇒ กุญแจพิสูจน์ไม่ได้ +
        // เบอร์หัวใบกลายเป็นของผู้ซื้อ (ญาติของ "customer copy"/"สำเนาลูกค้า" ที่กลบไว้แล้ว)
        Assert.Empty(OcrPartyLabels.FindAll("ต้นฉบับลูกค้า\nFor Customer\n").BuyerPos);
        // ทิศตรงข้าม: ป้ายลูกค้าจริงยังเป็นป้ายผู้ซื้อ
        Assert.Single(OcrPartyLabels.FindAll("ต้นฉบับลูกค้า\nลูกค้า: หจก.แอม แฮปปี้เนส\n").BuyerPos);
    }

    [Fact]
    public void RecipientLabels_AreSeparateFromBuyerRoleLabels()
    {
        // ป้ายบล็อกผู้รับไม่ไปปนรายการป้ายผู้ซื้อที่ใช้ตัดสิน "บทบาท" ของกระดาษ (OcrDocumentRoleInferrer)
        const string text = "ร้าน X\nสถานที่ส่งสินค้า/ Shipping address บ้านเลขที่ 1\n";
        Assert.Empty(OcrPartyLabels.FindAll(text).BuyerPos);
        Assert.Equal(2, OcrPartyLabels.FindRecipientAll(text).Count);
    }
}
