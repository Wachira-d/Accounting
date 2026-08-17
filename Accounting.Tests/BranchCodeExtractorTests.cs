using Accounting.Services;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// แกะรหัสสาขาผู้ขาย/ผู้ซื้อจากใบกำกับ (ประกาศอธิบดีฯ 199 / §86/4)
///
/// ที่มา: OCR เดิมยิง regex "สาขาที่ …" ทับทั้งหน้าแล้วยัดผลเป็นสาขา **ผู้ขาย**
/// ตัวเดียว ⇒ สาขาผู้ซื้อไม่เคยถูกอ่าน (ตกเป็น 00000 เสมอ) → ขายให้สาขาลูกค้า
/// แล้วรายงานภาษีขายขึ้นเป็นสำนักงานใหญ่ผิดแบบเงียบ ๆ
/// </summary>
public class BranchCodeExtractorTests
{
    private const string TwoSidedInvoice = """
        บริษัท มังกร เซอร์วิส เอ็นจิเนียริ่ง จำกัด
        44 หมู่ 9 ต.หนองเหียง อ.พนัสนิคม จ.ชลบุรี 20140
        เลขประจำตัวผู้เสียภาษี: 0205565017741 (สำนักงานใหญ่)
        ใบกำกับภาษี
        ลูกค้า: บริษัท คิวทีซี อีเอสเอส จำกัด
        เลขผู้เสียภาษี: 0125568018919 สาขาที่ 00003
        2/2 กรุงเทพกรีฑา แขวงหัวหมาก เขตบางกะปิ กรุงเทพมหานคร 10240
        ลำดับ  รายการ  จำนวน  ราคา/หน่วย
        1  งานติดตั้ง  1  289,850.40
        """;

    [Fact]
    public void Reads_both_sides_from_a_two_sided_invoice()
    {
        var r = BranchCodeExtractor.Extract(TwoSidedInvoice);
        Assert.Equal("00000", r.SellerBranchCode);   // "สำนักงานใหญ่" ในบล็อกผู้ขาย
        Assert.Equal("00003", r.BuyerBranchCode);    // "สาขาที่ 00003" ในบล็อกผู้ซื้อ
    }

    [Fact]
    public void Buyer_branch_does_not_leak_into_the_seller()
    {
        // เคสที่พังของเดิม: regex ทั้งหน้าเจอ "สาขาที่ 00003" ของผู้ซื้อก่อน
        // แล้วยัดเป็นสาขาผู้ขาย
        var text = """
            บริษัท ก จำกัด
            เลขประจำตัวผู้เสียภาษี 0105500000001 สาขาที่ 7
            ลูกค้า บริษัท ข จำกัด
            เลขผู้เสียภาษี 0105500000002 สาขาที่ 12
            """;
        var r = BranchCodeExtractor.Extract(text);
        Assert.Equal("00007", r.SellerBranchCode);
        Assert.Equal("00012", r.BuyerBranchCode);
    }

    [Fact]
    public void Head_office_on_the_buyer_side_only_marks_the_buyer()
    {
        var text = """
            บริษัท ก จำกัด
            เลขประจำตัวผู้เสียภาษี 0105500000001 สาขาที่ 5
            ผู้ซื้อ บริษัท ข จำกัด (สำนักงานใหญ่)
            """;
        var r = BranchCodeExtractor.Extract(text);
        Assert.Equal("00005", r.SellerBranchCode);
        Assert.Equal("00000", r.BuyerBranchCode);
    }

    [Fact]
    public void Pads_to_five_digits()
    {
        Assert.Equal("00003", BranchCodeExtractor.FromSegment("สาขาที่ 3"));
        Assert.Equal("00012", BranchCodeExtractor.FromSegment("BRANCH: 12"));
        Assert.Equal("00003", BranchCodeExtractor.FromSegment("สาขา เลขที่ 00003"));
    }

    [Fact]
    public void Unknown_returns_null_not_a_guessed_head_office()
    {
        // ห้ามเดา 00000 ให้ — ผู้เรียกตัดสินเองว่าจะ default หรือให้ผู้ใช้กรอก
        Assert.Null(BranchCodeExtractor.FromSegment("ไม่มีข้อมูลสาขาบนใบนี้"));
        Assert.Null(BranchCodeExtractor.FromSegment(""));
        Assert.Null(BranchCodeExtractor.FromSegment(null));
    }

    [Fact]
    public void Receipt_without_a_buyer_block_keeps_the_old_behaviour()
    {
        // ใบเสร็จร้านค้า/สลิป — ไม่มีบล็อกผู้ซื้อ ⇒ อ่านทั้งหน้าเป็นของผู้ขาย
        var text = "ร้านสะดวกซื้อ สาขาที่ 00021\nรวม 107.00";
        var r = BranchCodeExtractor.Extract(text);
        Assert.Equal("00021", r.SellerBranchCode);
        Assert.Null(r.BuyerBranchCode);
    }

    [Fact]
    public void Numbers_in_the_item_table_are_not_read_as_a_branch()
    {
        // หน้าต่างผู้ซื้อถูกตัดที่หัวตาราง — "ลำดับ 1" ต้องไม่กลายเป็นสาขา 00001
        var text = """
            บริษัท ก จำกัด (สำนักงานใหญ่)
            ลูกค้า บริษัท ข จำกัด
            ลำดับ รายการ
            1 สาขาที่ 9 ของสินค้า
            """;
        var r = BranchCodeExtractor.Extract(text);
        Assert.Equal("00000", r.SellerBranchCode);
        Assert.Null(r.BuyerBranchCode);   // ไม่หยิบ "สาขาที่ 9" ที่อยู่ในตาราง
    }

    [Fact]
    public void English_bill_to_block_works_too()
    {
        var text = """
            ABC Co., Ltd.  HEAD OFFICE
            TAX ID 0105500000001
            BILL TO: XYZ Public Co., Ltd.
            TAX ID 0105500000002  BRANCH 00004
            """;
        var r = BranchCodeExtractor.Extract(text);
        Assert.Equal("00000", r.SellerBranchCode);
        Assert.Equal("00004", r.BuyerBranchCode);
    }

    [Fact]
    public void Empty_input_is_safe()
    {
        var r = BranchCodeExtractor.Extract(null);
        Assert.Null(r.SellerBranchCode);
        Assert.Null(r.BuyerBranchCode);
    }
}
