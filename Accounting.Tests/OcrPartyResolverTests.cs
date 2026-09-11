using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ขั้นตัดสินกลาง "ชุดข้อมูลไหนคือใคร" — ทดสอบด้วยกระดาษจริง 3 ใบ + ทิศตรงข้ามทุกกติกา
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-09-11 สองรอบ) ═══ บิลเงินสดที่ร้านออกให้เรา ถูกสรุปว่าเรา
/// เป็นผู้ขาย · แก้รอบแรกแล้วชื่อผู้ขายบนจอยังเป็นเรา เพราะการตัดสินกระจาย 5 จุดใน 3 ไฟล์
/// </summary>
public class OcrPartyResolverTests
{
    private static readonly OcrOurIdentity Us = new(
        Name: "หจก. แอม แฮปปี้เนส", NameEn: null, TaxId: "0203562005871",
        BranchCode: "00000", Address: "202/24 ม.5 ซ.บ้านห้วยกุ่ม 4 ต.บางพระ อ.ศรีราชา จ.ชลบุรี 20110");

    /// <summary>ข้อความจริงจาก Azure DI (ลำดับบรรทัดตามที่ engine คืน — ค่ามาก่อนป้าย “นาม”)</summary>
    private const string CashBill =
        "อ๊อฟ พิการ\nเล่มที่\nเลขที่\n177/18 ม.5 ต.บางพระ อ.ศรีราชา จ. ชลบุรี\nบิลเงินสด\nCASHSALE\n"
        + "บจก. แอมแฮปปี้เนส (สำนักงานใหญ่)\nนาม\nวันที่\n6/9 /69\nNAME\nDATE\nที่อยู่\n"
        + "202/24 ม.5 ซ. บ้านห้วยกุ่ม 4\n0203562025871\nADDRESS\nต. บางพระ อ. ศรีราชา จ. ชลบุรี\n"
        + "รีเลขประจำตัวผู้เสียภาษี\nจำนวน\nรายการ\nหน่วยละ\nจำนวนเงิน\nแม็กโคร หรือวัน\n3500\n";

    // ═══ ใบที่พัง: บิลเงินสดที่ร้านออกให้เรา ═══
    [Fact]
    public void บิลเงินสด_engineใส่ชื่อเราในช่องผู้ขาย_ต้องย้ายไปผู้ซื้อและปล่อยผู้ขายว่าง()
    {
        // สถานะหลัง SmartFieldExtractor: ที่อยู่ถูกผ่าแล้ว (ผู้ขาย = กรอบบน · ผู้ซื้อ = ช่อง ที่อยู่)
        var vendor = new OcrPartyBlock("บจก. แอมแฮปปี้เนส (สำนักงานใหญ่)", null,
            "177/18 ม.5 ต.บางพระ อ.ศรีราชา จ. ชลบุรี", "00000");
        var buyer = new OcrPartyBlock(null, null, "202/24 ม.5 ซ. บ้านห้วยกุ่ม 4", null);

        var r = OcrPartyResolver.Resolve(CashBill, Us, vendor, buyer);

        Assert.Equal(OcrSelfSide.Buyer, r.OurSide);
        Assert.Equal(OcrPartyDecision.Swapped, r.Decision);
        Assert.Equal("บจก. แอมแฮปปี้เนส (สำนักงานใหญ่)", r.Buyer.Name);
        Assert.Equal("202/24 ม.5 ซ. บ้านห้วยกุ่ม 4", r.Buyer.Address);   // ของเดิมฝั่งผู้ซื้อต้องไม่ถูกทับ
        Assert.Equal("00000", r.Buyer.BranchCode);                        // “(สำนักงานใหญ่)” เป็นของเรา ย้ายตามชื่อ
        // เลขบนกระดาษ 0203562025871 checksum ตก (อ่านเพี้ยน 1 หลัก) — ไม่ใช้เลขที่อ่านมา แต่เติม
        // เลขจริงของเราจากทะเบียน เพราะรู้แล้วว่าฝั่งนี้คือเรา และกระดาษยืนยันว่าเลขเราอยู่บนใบ
        Assert.Equal("0203562005871", r.Buyer.TaxId);
        Assert.Null(r.Vendor.Name);                                        // ร้าน “อ๊อฟ” เขียนมือ อ่านไม่ได้ = ไม่รู้
        Assert.Equal("177/18 ม.5 ต.บางพระ อ.ศรีราชา จ. ชลบุรี", r.Vendor.Address); // ที่อยู่กรอบบนเป็นของร้าน ต้องคงไว้
        Assert.False(r.ShouldAskAi);                                       // ป้ายชัด ไม่ต้องจ่าย token
        Assert.Contains(r.Reasons, x => x.Contains("0203562025871"));      // เห็นเลขเราที่อ่านเพี้ยนหลักเดียว
    }

    [Fact]
    public void บิลเงินสด_ถ้าที่อยู่ยังไม่ถูกผ่า_ที่อยู่ต้องย้ายตามชื่อไปฝั่งผู้ซื้อ()
    {
        var vendor = new OcrPartyBlock("บจก. แอมแฮปปี้เนส", null, "202/24 ม.5 ซ. บ้านห้วยกุ่ม 4", null);
        var r = OcrPartyResolver.Resolve(CashBill, Us, vendor, OcrPartyBlock.Empty);
        Assert.Equal(OcrPartyDecision.Swapped, r.Decision);
        Assert.Equal("202/24 ม.5 ซ. บ้านห้วยกุ่ม 4", r.Buyer.Address);
        Assert.Null(r.Vendor.Address);
    }

    // ═══ ทิศตรงข้าม: ใบที่เราออกเอง ต้องไม่ถูกกลับทิศ ═══
    [Fact]
    public void ใบกำกับที่เราออกเอง_เลขภาษีผู้ขายคือเรา_ต้องเป็นผู้ขายเต็มร้อย_ไม่แตะช่อง()
    {
        var vendor = new OcrPartyBlock("หจก. แอม แฮปปี้เนส", "0203562005871", Us.Address, "00000");
        var buyer = new OcrPartyBlock("บริษัท ลูกค้าดี จำกัด", "0105551234567", "99 ถ.สุขุมวิท", "00000");
        var r = OcrPartyResolver.Resolve("ใบกำกับภาษี\nผู้ขาย หจก. แอม แฮปปี้เนส\nลูกค้า บริษัท ลูกค้าดี จำกัด\n", Us, vendor, buyer);
        Assert.Equal(OcrSelfSide.Seller, r.OurSide);
        Assert.Equal(1.0m, r.Confidence);
        Assert.Equal(OcrPartyDecision.Unchanged, r.Decision);
        Assert.Equal("บริษัท ลูกค้าดี จำกัด", r.Buyer.Name);
        Assert.False(r.ShouldAskAi);
    }

    [Fact]
    public void ใบซื้อปกติ_เลขภาษีผู้ซื้อคือเรา_ต้องเป็นผู้ซื้อเต็มร้อย()
    {
        // ใบลักกี้ เวย์ (HS6909180) — ผู้ขายคนละราย · ผู้ซื้อคือเรา
        var vendor = new OcrPartyBlock("บริษัท ลักกี้ เวย์ จำกัด", "0105550027134", "เลขที่ 6 ซอยท่าข้าม5", "00000");
        var buyer = new OcrPartyBlock("หจก. แอม แฮปปี้เนส", "0203562005871", "202/24 ม.5 ต.บางพระ", "00000");
        var r = OcrPartyResolver.Resolve("ใบเสร็จรับเงิน/ใบกำกับภาษี\nลูกค้า หจก. แอม แฮปปี้เนส\n", Us, vendor, buyer);
        Assert.Equal(OcrSelfSide.Buyer, r.OurSide);
        Assert.Equal(1.0m, r.Confidence);
        Assert.Equal("บริษัท ลักกี้ เวย์ จำกัด", r.Vendor.Name);
    }

    // ═══ กติกา (ก): คู่ค้าต้องไม่ใช่เรา ═══
    [Fact]
    public void เราอยู่ทั้งสองช่อง_และป้ายบอกว่าเป็นผู้ซื้อ_ต้องล้างช่องผู้ขายที่เป็นเรา()
    {
        var both = new OcrPartyBlock("บจก. แอมแฮปปี้เนส (สำนักงานใหญ่)", null, null, null);
        var r = OcrPartyResolver.Resolve(CashBill, Us, both, both);
        Assert.Equal(OcrPartyDecision.CounterpartyCleared, r.Decision);
        Assert.Null(r.Vendor.Name);
        Assert.Equal(both.Name, r.Buyer.Name);
    }

    [Fact]
    public void เราอยู่ทั้งสองช่อง_ไม่มีป้าย_ต้องบอกว่าขัดกันและส่งให้AI()
    {
        var both = new OcrPartyBlock("หจก. แอม แฮปปี้เนส", null, null, null);
        var r = OcrPartyResolver.Resolve("รวมเงิน 3500\n", Us, both, both);
        Assert.Equal(OcrPartyDecision.Conflict, r.Decision);
        Assert.Equal(OcrSelfSide.Unknown, r.OurSide);
        Assert.True(r.ShouldAskAi);
    }

    // ═══ “ไม่รู้ = บอกว่าไม่รู้” ═══
    [Fact]
    public void ไม่พบเราในช่องไหน_มีชื่อสองฝั่ง_ไม่มีป้าย_ต้องส่งให้AIไม่ใช่เดา()
    {
        var vendor = new OcrPartyBlock("บริษัท ก จำกัด", null, null, null);
        var buyer = new OcrPartyBlock("บริษัท ข จำกัด", null, null, null);
        var r = OcrPartyResolver.Resolve("ใบเสร็จ\nบริษัท ก จำกัด\nบริษัท ข จำกัด\n", Us, vendor, buyer);
        Assert.Equal(OcrSelfSide.Unknown, r.OurSide);
        Assert.True(r.ShouldAskAi);
        Assert.Equal(OcrPartyDecision.Unchanged, r.Decision);
    }

    [Fact]
    public void ชื่อเราอยู่ช่องผู้ขายโดยไม่มีคู่ค้าและไม่มีป้าย_มั่นใจต่ำและควรถามAI()
    {
        var vendor = new OcrPartyBlock("หจก. แอม แฮปปี้เนส", null, null, null);
        var r = OcrPartyResolver.Resolve("หจก. แอม แฮปปี้เนส\nรวมเงิน 3500\n", Us, vendor, OcrPartyBlock.Empty);
        Assert.Equal(OcrSelfSide.Seller, r.OurSide);
        Assert.True(r.Confidence < 0.7m);
        Assert.True(r.ShouldAskAi);
    }

    // ═══ หลักฐานประกอบ ═══
    [Fact]
    public void เลขภาษีที่ต่างจากเราหลักเดียวบนกระดาษ_ต้องถูกจับเป็นหลักฐานว่าเราอยู่บนใบ()
        => Assert.Equal("0203562025871", OcrPartyResolver.OurTaxIdNearMissOnPaper(CashBill, Us.TaxId));

    [Fact]
    public void เลขภาษีที่ตรงเป๊ะ_ไม่นับเป็นnearMiss_และเลขคนละคนไม่นับ()
    {
        Assert.Null(OcrPartyResolver.OurTaxIdNearMissOnPaper("เลข 0203562005871\n", Us.TaxId));
        Assert.Null(OcrPartyResolver.OurTaxIdNearMissOnPaper("เลข 0105550027134\n", Us.TaxId));
    }

    [Fact]
    public void ที่อยู่ที่มีเลขที่บ้านตรงกับเรา_นับเป็นของเรา_เลขบ้านอื่นไม่นับ()
    {
        Assert.True(OcrPartyResolver.AddressLooksLikeOurs("202/24 ม.5 ซ. บ้านห้วยกุ่ม 4", Us.Address));
        Assert.False(OcrPartyResolver.AddressLooksLikeOurs("177/18 ม.5 ต.บางพระ", Us.Address));
        Assert.False(OcrPartyResolver.AddressLooksLikeOurs("1202/245 ถ.สุขุมวิท", Us.Address));   // ห้ามจับ substring กลางเลข
    }

    [Fact]
    public void ฝั่งเราชัดแต่กระดาษไม่มีเลขเราเลย_ต้องไม่เติมเลขภาษี()
    {
        // ผู้ขายไม่ได้กรอกเลขผู้ซื้อ — เติมเองไม่ได้ เพราะกระดาษไม่มีหลักฐาน (ใบกำกับที่ขาดเลข
        // ผู้ซื้อคือใบที่เคลมภาษีซื้อไม่ได้จริง ๆ ห้ามทำให้ดูครบ)
        var vendor = new OcrPartyBlock("บริษัท ก จำกัด", "0105551234567", null, null);
        var buyer = new OcrPartyBlock("หจก. แอม แฮปปี้เนส", null, null, null);
        var r = OcrPartyResolver.Resolve("ใบเสร็จ\nลูกค้า หจก. แอม แฮปปี้เนส\n", Us, vendor, buyer);
        Assert.Equal(OcrSelfSide.Buyer, r.OurSide);
        Assert.Null(r.Buyer.TaxId);
    }

    [Fact]
    public void เราเป็นผู้ขายชัด_กระดาษมีเลขเราตรง_แต่ช่องว่าง_ต้องเติม()
    {
        var vendor = new OcrPartyBlock("หจก. แอม แฮปปี้เนส", null, null, null);
        var buyer = new OcrPartyBlock("บริษัท ลูกค้าดี จำกัด", "0105551234567", null, null);
        var r = OcrPartyResolver.Resolve("ใบกำกับภาษี\nผู้ขาย หจก. แอม แฮปปี้เนส เลขประจำตัวผู้เสียภาษี 0203562005871\nลูกค้า บริษัท ลูกค้าดี จำกัด\n",
            Us, vendor, buyer);
        Assert.Equal(OcrSelfSide.Seller, r.OurSide);
        Assert.Equal("0203562005871", r.Vendor.TaxId);
    }

    // ═══ จากทีมตรวจสวน 2026-09-11 ═══
    [Fact]
    public void ใบที่เราออกเอง_ป้ายลูกค้าอยู่ใกล้หัวเรา_เลขภาษีต้องชนะป้าย_ห้ามสลับ()
    {
        // ใบขายจริงมักไม่พิมพ์ "ผู้ขาย" แต่พิมพ์ "ลูกค้า" ห่างจากหัวเราไม่กี่สิบตัวอักษร —
        // ถ้าให้ป้ายชนะ ใบขายของเราเองจะกลายเป็นใบซื้อ
        var vendor = new OcrPartyBlock("หจก. แอม แฮปปี้เนส", "0203562005871", Us.Address, "00000");
        var buyer = new OcrPartyBlock("บริษัท ลูกค้าดี จำกัด", null, null, null);
        var r = OcrPartyResolver.Resolve(
            "หจก. แอม แฮปปี้เนส\nเลขประจำตัวผู้เสียภาษี 0203562005871\nใบกำกับภาษี\nลูกค้า บริษัท ลูกค้าดี จำกัด\n",
            Us, vendor, buyer);
        Assert.Equal(OcrSelfSide.Seller, r.OurSide);
        Assert.Equal(1.0m, r.Confidence);
        Assert.Equal(OcrPartyDecision.Unchanged, r.Decision);
        Assert.Equal("0203562005871", r.Vendor.TaxId);
    }

    [Fact]
    public void บริษัทในเครือชื่อคล้ายแต่มีเลขภาษีของตัวเอง_ไม่ใช่เรา()
    {
        var affiliate = new OcrPartyBlock("บจก. แอม แฮปปี้เนส เทรดดิ้ง", "0105551234567", null, null);
        Assert.Equal(0m, OcrPartyResolver.SelfStrength(affiliate, Us, out _));
        // และแม้ไม่มีเลขภาษี ชื่อที่ยาวเกินชื่อเรามาก ก็ไม่ใช่เรา
        Assert.Equal(0m, OcrPartyResolver.SelfStrength(
            new OcrPartyBlock("บจก. แอม แฮปปี้เนส เทรดดิ้ง", null, null, null), Us, out _));
    }

    [Fact]
    public void เลขเราตัวเดียวในช่องผู้ขาย_ไม่มีคู่ค้าไม่มีป้าย_ต้องไม่ฟันธงว่าเราออกใบ()
    {
        // บิลร้านไม่จด VAT พิมพ์แค่เลขลูกค้า (= เรา) แล้วตัวสกัดเอนเลขเข้าช่องผู้ขาย
        var vendor = new OcrPartyBlock(null, "0203562005871", null, null);
        var r = OcrPartyResolver.Resolve("บิลเงินสด\n0203562005871\nรวมเงิน 500\n", Us, vendor, OcrPartyBlock.Empty);
        Assert.True(r.Confidence < 0.7m);
        Assert.True(r.ShouldAskAi);
    }

    [Fact]
    public void ApplySide_คำตอบAI_ต้องย้ายบล็อกไม่ใช่แค่ป้าย()
    {
        var vendor = new OcrPartyBlock("หจก. แอม แฮปปี้เนส", null, "202/24 ม.5 ต.บางพระ", "00000");
        var (v, b) = OcrPartyResolver.ApplySide(vendor, OcrPartyBlock.Empty, OcrSelfSide.Buyer, Us);
        Assert.Null(v.Name);
        Assert.Equal("หจก. แอม แฮปปี้เนส", b.Name);
        Assert.Equal("202/24 ม.5 ต.บางพระ", b.Address);
    }

    [Fact]
    public void ค่าว่างทั้งหมด_ต้องไม่พัง()
    {
        var r = OcrPartyResolver.Resolve(null, new OcrOurIdentity(null, null, null, null, null),
            OcrPartyBlock.Empty, OcrPartyBlock.Empty);
        Assert.Equal(OcrSelfSide.Unknown, r.OurSide);
        Assert.False(r.ShouldAskAi);
    }
}
