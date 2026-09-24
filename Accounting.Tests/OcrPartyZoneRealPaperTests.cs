using Accounting.Helpers;
using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **กระดาษจริงสองใบของรอบ 190 — โซนผู้ขาย/ผู้ซื้อ · ที่อยู่ · สาขา** (ทีม P · `erp-review/2026-09-24/BRIEF.md`)
///
/// ใบ A (Wine Pro · POS ภาษาอังกฤษ + ผู้ซื้อไทย): ที่อยู่ผู้ขายขาด “12” (“12/861” → “/861”) ·
/// ชื่อผู้ซื้อ = รหัสลูกค้า “[CZBNG2600843]” แทน “หจก.แอม แฮปปี้เนส” (= เรา)
/// ใบ B (Radisson Hua Hin · เขียนมือ · หัวสองภาษา · ออกโดยสาขาที่ 8): สาขาผู้ขาย 00000 (ควร 00008) ·
/// ชื่อผู้ซื้อติด “(สำนักงานใหญ่)”
///
/// ทุกกลุ่มมี<b>สองครึ่ง</b> (CLAUDE.md §H): ใบที่พังต้องกลับมาถูก + ค่าที่ถูกอยู่แล้วของใบเดียวกัน/ใบอื่น
/// ต้องไม่ถูกแตะ — เทสต์ที่มีแต่ครึ่งแรกผ่านได้ทั้งตอนแก้ถูกและตอน “ปิดด่านทิ้ง”
/// </summary>
public class OcrPartyZoneRealPaperTests
{
    /// <summary>ตัวตนบริษัทเรา (จากฐานข้อมูล) — ชื่อจดทะเบียนรูปเต็ม ต่างจากที่กระดาษย่อ “หจก.”</summary>
    private static readonly OcrOurIdentity Us = new(
        Name: "ห้างหุ้นส่วนจำกัด แอม แฮปปี้เนส", NameEn: null, TaxId: "0203562005871",
        BranchCode: "00000", Address: "202/24 หมู่ที่ 5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110");

    /// <summary>ใบ A — ข้อความถอดจากกระดาษจริง (BRIEF ใบ A)</summary>
    internal const string PaperA =
        "Wine Pro Co.,Ltd. Branch 00012\n"
        + "12/861 Moo 15 Bangkaew,\n"
        + "Bangplee, Samutprakarn 10540\n"
        + "Tel : 02-100-6401\n"
        + "VAT Registration No.: 0105555175590\n"
        + "POS Terminal ID.: E051120003A1433\n"
        + "Receipt / Tax Invoice (Original)\n"
        + "ใบเสร็จรับเงิน/ใบกำกับภาษี (ต้นฉบับ)\n"
        + "Tax Inv No.:  BA2609-569\n"
        + "Slip: 0000000BN2000000413\n"
        + "Staff: Kade\n"
        + "Date: 18/09/26 6:35 PM\n"
        + "Customer Info. [CZBNG2600843]\n"
        + "หจก.แอม แฮปปี้เนส\n"
        + "VAT Reg. No.: 0203562005871\n"
        + "Branch: สำนักงานใหญ่\n"
        + "Address\n"
        + "202/24 หมู่ที่ 5 ซอยบ้านห้วยกุ่ม 4 ต.บาง\n"
        + "พระ อ.ศรีราชา จ.ชลบุรี 20110\n"
        + "Description                                   Amount\n"
        + "BELCOLLE Moscato d' Asti DOCG\n"
        + "  1 bottle x 524.00                        524.00 V\n"
        + "VINA TOLDOS Red\n"
        + "  12 bottle x 255.75                     3,069.00 V\n"
        + "Total                                    3,593.00\n"
        + "   VAT%    Net.Amt     VAT      Amount\n"
        + "V   7    3,357.94   235.06   3,593.00\n"
        + "*** VAT INCLUDED ***\n";

    /// <summary>ใบ B — ข้อความถอดจากกระดาษจริง (BRIEF ใบ B)</summary>
    internal const string PaperB =
        "Radisson RESORT & SPA HUA HIN\n"
        + "Destination Resorts Co.,Ltd  Head Office\n"
        + "200 Justmine International Tower Building, Unit2302A, 23Floor Moo 8 Chaeng Watthana Road,\n"
        + "Pak Kret, Nonthaburi   Branch Tax Invoice is Issued no. 8\n"
        + "854/2 Buriram Road, Cha-Am, Petchburi 76120 Thailand   Tel (66 32) 708 300 Fax (66 32) 708310\n"
        + "บริษัท เดสติเนชั่น รีสอร์ทส์ จำกัด  สำนักงานใหญ่ เลขที่ 200 อาคารจัสมินอินเตอร์เนชั่นแนลทาวเวอร์ ห้อง 2302A ชั้น 23\n"
        + "หมู่ 8 ถ.แจ้งวัฒนะ ต.ปากเกร็ด อ.ปากเกร็ด จ.นนทบุรี  สาขาที่ออกใบกำกับภาษีคือ สาขาที่ 8\n"
        + "854/2 ถนนบุรีรัมย์ ต.ชะอำ อ.ชะอำ จ.เพชรบุรี 76120\n"
        + "ใบเสร็จรับเงิน/ใบกำกับภาษี  OFFICIAL RECEIPT / TAX INVOICE          วันที่ DATE 12/09/26\n"
        + "เลขประจำตัวผู้เสียภาษี / TAX ID. 0105551136085\n"
        + "เล่มที่ BOOK NO. 066                                             เลขที่ SERIAL NO. 3267\n"
        + "ได้รับเงินจาก Receipt From  หจก. แอม แฮปปี้เนส (สำนักงานใหญ่)\n"
        + "ที่อยู่ Address  202/24 หมู่ที่5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110\n"
        + "เลขประจำตัวผู้เสียภาษีอากร TAX ID NUMBER  0203562005871   ☑ สำนักงานใหญ่ Head Office  ☐ สาขา Branch\n"
        + "รายการ Description                                              จำนวนเงิน Amount\n"
        + "ค่าอาหารและเครื่องดื่ม                                                   1,705.00\n";

    // ═══════════════════ 1. ที่อยู่ผู้ขายใบ A ขาด “12” ═══════════════════

    [Fact]
    public void ใบA_ที่อยู่ขึ้นต้นเลขบ้าน12_ห้ามถูกตีเป็นเศษท้ายชื่อ_Branch00012()
    {
        // reproduce: ชื่อ engine “… Branch 00012” + ที่อยู่ “12/861 …” — เดิมคืน “/861 Moo 15 …”
        Assert.Null(OcrPartyAddress.StripLeakedNameFragment(
            "12/861 Moo 15 Bangkaew ,Bangplee, Samutprakarn 10540", "Wine Pro Co.,Ltd. Branch 00012"));
    }

    [Fact]
    public void ใบA_ที่อยู่ไทยที่ขึ้นต้นด้วยเลขที่ชื่อลงท้ายด้วยเลขเดียวกัน_ก็ห้ามตัด()
        => Assert.Null(OcrPartyAddress.StripLeakedNameFragment(
            "99/1 ถ.สุขุมวิท ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110", "ร้านวัสดุ สาขา 199"));

    [Fact]
    public void ทิศตรงข้าม_เศษชื่อที่เป็นตัวอักษร_ยังต้องตัดได้เหมือนเดิม()
        // ใบลักกี้เวย์ (HS6909180) — ด่านตัวเลขต้องไม่ปิดการซ่อมเศษชื่อจริง
        => Assert.Equal("202/24 ม.5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี20110",
            OcrPartyAddress.StripLeakedNameFragment("เนส202/24 ม.5 ต.บางพระ อ.ศรีราชา จ.ชลบุรี20110", "หจก. แอม แฮปปี้เนส"));

    // ═══════════════════ 2. ป้ายสาขาท้ายชื่อ ═══════════════════

    [Theory]
    [InlineData("หจก. แอม แฮปปี้เนส (สำนักงานใหญ่)", "หจก. แอม แฮปปี้เนส")]        // ใบ B ผู้ซื้อ
    [InlineData("Wine Pro Co.,Ltd. Branch 00012", "Wine Pro Co.,Ltd.")]             // ใบ A ผู้ขาย
    [InlineData("Destination Resorts Co.,Ltd  Head Office", "Destination Resorts Co.,Ltd")]
    [InlineData("บริษัท ก จำกัด (สาขาที่ 3)", "บริษัท ก จำกัด")]
    [InlineData("บริษัท ก จำกัด สาขา 00003", "บริษัท ก จำกัด")]
    [InlineData("ABC Co., Ltd. Branch No. 8", "ABC Co., Ltd.")]
    public void ป้ายสาขาท้ายชื่อ_ต้องถูกตัดออก(string raw, string expected)
    {
        var (name, suffix) = OcrPartyName.StripBranchSuffix(raw);
        Assert.Equal(expected, name);
        Assert.NotNull(suffix);
    }

    [Theory]
    [InlineData("หจก.แอม แฮปปี้เนส")]
    [InlineData("บริษัท สาขาทอง จำกัด")]                         // “สาขา” อยู่กลางชื่อ ไม่ใช่ป้าย
    [InlineData("ธนาคารกรุงไทย จำกัด (มหาชน) สาขาสีลม")]          // ชื่อสาขาเป็นคำ ไม่ใช่รหัส — ไม่ตัดเดา
    [InlineData("7-Eleven")]
    [InlineData("สำนักงานใหญ่")]                                 // ทั้งช่องเป็นป้าย = ไม่ใช่ชื่อ ห้ามตัดจนว่าง
    public void ชื่อที่ไม่มีป้ายสาขาท้าย_ต้องไม่ถูกแตะ(string raw)
    {
        var (name, suffix) = OcrPartyName.StripBranchSuffix(raw);
        Assert.Equal(raw, name);
        Assert.Null(suffix);
    }

    [Theory]
    [InlineData("[CZBNG2600843]", true)]     // ใบ A — รหัสลูกค้าของร้าน
    [InlineData("CUST00931", true)]
    [InlineData("C-0012A", true)]
    [InlineData("หจก.แอม แฮปปี้เนส", false)]
    [InlineData("Wine Pro Co.,Ltd.", false)]
    [InlineData("3M", false)]                // ชื่อจริงสั้น — ต่ำกว่าเกณฑ์
    [InlineData("KFC", false)]               // ไม่มีตัวเลข
    [InlineData("", false)]
    public void รหัสลูกค้า_ไม่ใช่ชื่อ(string value, bool expected)
        => Assert.Equal(expected, OcrPartyName.LooksLikeCode(value));

    // ═══════════════════ 3. ชื่อผู้ซื้อ = ชื่อบริษัทเรา ═══════════════════

    [Fact]
    public void ใบA_ชื่อผู้ซื้อเป็นรหัสลูกค้า_เลขภาษีคือเรา_ต้องได้ชื่อบริษัทเราจากทะเบียน()
    {
        var vendor = new OcrPartyBlock("Wine Pro Co.,Ltd.", "0105555175590",
            "12/861 Moo 15 Bangkaew, Bangplee, Samutprakarn 10540", "00012");
        var buyer = new OcrPartyBlock("[CZBNG2600843]", "0203562005871",
            "202/24 หมู่ที่ 5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110", "00000");

        var r = OcrPartyResolver.Resolve(PaperA, Us, vendor, buyer);

        Assert.Equal(OcrSelfSide.Buyer, r.OurSide);
        Assert.Equal(Us.Name, r.Buyer.Name);
        Assert.Contains(r.Reasons, x => x.Contains("[CZBNG2600843]"));      // ผู้แพ้ถูกบันทึก (G4)
        // ของที่ถูกอยู่แล้วของใบ A ต้องไม่ถูกแตะ
        Assert.Equal("0203562005871", r.Buyer.TaxId);
        Assert.Equal("202/24 หมู่ที่ 5 ซอยบ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110", r.Buyer.Address);
        Assert.Equal("Wine Pro Co.,Ltd.", r.Vendor.Name);
        Assert.Equal("0105555175590", r.Vendor.TaxId);
        Assert.Equal("00012", r.Vendor.BranchCode);
    }

    [Fact]
    public void ใบB_ชื่อผู้ซื้อที่เป็นเราอยู่แล้ว_ต้องคงรูปเดิม_ไม่ถูกแทนด้วยรูปทะเบียน()
    {
        var buyer = new OcrPartyBlock("หจก. แอม แฮปปี้เนส", "0203562005871", null, "00000");
        var vendor = new OcrPartyBlock("บริษัท เดสติเนชั่น รีสอร์ทส์ จำกัด", "0105551136085", null, "00008");
        var r = OcrPartyResolver.Resolve(PaperB, Us, vendor, buyer);
        Assert.Equal(OcrSelfSide.Buyer, r.OurSide);
        Assert.Equal("หจก. แอม แฮปปี้เนส", r.Buyer.Name);
        Assert.Equal("บริษัท เดสติเนชั่น รีสอร์ทส์ จำกัด", r.Vendor.Name);
    }

    [Fact]
    public void ชื่อจริงที่ไม่ใช่เรา_และกระดาษไม่มีชื่อเรา_ต้องไม่ถูกแทน_แม้เลขภาษีจะเป็นเรา()
    {
        // G3: เลขภาษีบอกว่าบล็อกนี้คือเรา แต่ชื่อที่อ่านได้เป็นชื่อจริง (ผู้ติดต่อ?) และกระดาษไม่มีชื่อเรา
        // ⇒ ไม่มีหลักฐานพอจะทิ้งชื่อที่อ่านได้
        var vendor = new OcrPartyBlock("บริษัท ก จำกัด", "0105551234567", null, null);
        var buyer = new OcrPartyBlock("คุณสมชาย ใจดี", "0203562005871", null, null);
        var r = OcrPartyResolver.Resolve("ใบกำกับภาษี\nลูกค้า คุณสมชาย ใจดี\nเลขประจำตัวผู้เสียภาษี 0203562005871\n",
            Us, vendor, buyer);
        Assert.Equal(OcrSelfSide.Buyer, r.OurSide);
        Assert.Equal("คุณสมชาย ใจดี", r.Buyer.Name);
    }

    [Fact]
    public void รหัสในช่องชื่อ_แต่กระดาษไม่มีทั้งชื่อและเลขภาษีเรา_ต้องไม่แต่งชื่อ()
    {
        // เลขภาษีในบล็อกมาจากที่อื่น (ไม่อยู่บนกระดาษใบนี้) — ไม่มีหลักฐานว่าเราอยู่บนใบ
        var buyer = new OcrPartyBlock("[C0001234]", "0203562005871", null, null);
        var r = OcrPartyResolver.Resolve("ใบเสร็จ\nรวม 100.00\n", Us, OcrPartyBlock.Empty, buyer);
        Assert.Equal("[C0001234]", r.Buyer.Name);
    }

    [Fact]
    public void ฝั่งเราไม่ชัด_ต้องไม่แตะชื่อ()
    {
        var vendor = new OcrPartyBlock("บริษัท ก จำกัด", null, null, null);
        var buyer = new OcrPartyBlock("[CZBNG2600843]", null, null, null);
        var r = OcrPartyResolver.Resolve("ใบเสร็จ\nบริษัท ก จำกัด\n[CZBNG2600843]\n", Us, vendor, buyer);
        Assert.Equal("[CZBNG2600843]", r.Buyer.Name);
    }

    [Fact]
    public void ชื่อเราบนกระดาษ_ตรวจด้วยกติกาเทียบชื่อกลาง()
    {
        Assert.True(OcrPartyResolver.OurNameOnPaper(PaperA, Us));      // “หจก.แอม แฮปปี้เนส” = รูปย่อของเรา
        Assert.True(OcrPartyResolver.OurNameOnPaper(PaperB, Us));
        Assert.False(OcrPartyResolver.OurNameOnPaper("บริษัท ก จำกัด\nรวม 100\n", Us));
    }

    // ═══════════════════ 4. สาขาผู้ขาย/ผู้ซื้อ (BranchCodeExtractor) ═══════════════════

    [Fact]
    public void ใบB_ผู้ออกใบคือสาขาที่8_ไม่ใช่สำนักงานใหญ่_และช่องติ๊กสำนักงานใหญ่เป็นของผู้ซื้อ()
    {
        var r = BranchCodeExtractor.Extract(PaperB);
        Assert.Equal("00008", r.SellerBranchCode);
        Assert.Equal("00000", r.BuyerBranchCode);   // ☑ สำนักงานใหญ่ + “(สำนักงานใหญ่)” ในบล็อกผู้ซื้อ
    }

    [Fact]
    public void ใบB_ถ้าOCRทำไม้เอกหล่น_สาขาที_8_ก็ยังต้องได้00008()
    {
        var dropped = PaperB.Replace("สาขาที่ 8", "สาขาที 8").Replace("Branch Tax Invoice is Issued no. 8", "");
        Assert.Equal("00008", BranchCodeExtractor.Extract(dropped).SellerBranchCode);
    }

    [Fact]
    public void ใบA_สาขาผู้ขาย00012_และผู้ซื้อสำนักงานใหญ่_ต้องเหมือนเดิม()
    {
        var r = BranchCodeExtractor.Extract(PaperA);
        Assert.Equal("00012", r.SellerBranchCode);
        Assert.Equal("00000", r.BuyerBranchCode);
    }

    [Fact]
    public void BranchNo_แบบหัวกระดาษอังกฤษ_ต้องอ่านเลขได้()
        => Assert.Equal("00008", BranchCodeExtractor.FromSegment("ABC Hotel Co., Ltd. Branch No. 8"));

    [Fact]
    public void ใบเสร็จที่มีป้ายได้รับเงินจาก_สาขาที่ของผู้ซื้อ_ห้ามรั่วไปเป็นของผู้ขาย()
    {
        // ขั้นสำรอง: ป้ายชุดกลาง OcrPartyLabels ตัดบล็อกผู้ซื้อ เมื่อรายการคำเดิมหาไม่เจอ
        var text = "ร้าน ก การค้า\nสำนักงานใหญ่\nใบเสร็จรับเงิน\nได้รับเงินจาก บริษัท ข จำกัด สาขาที่ 5\nรายการ\n1 ค่าสินค้า 100.00\n";
        var r = BranchCodeExtractor.Extract(text);
        Assert.Equal("00000", r.SellerBranchCode);
        Assert.Equal("00005", r.BuyerBranchCode);
    }
}
