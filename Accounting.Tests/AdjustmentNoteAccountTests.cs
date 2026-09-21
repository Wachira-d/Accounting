using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ผังบัญชีบนบรรทัดใบลดหนี้/ใบเพิ่มหนี้ — เทสต์**สองครึ่ง** ตามกฎเหล็ก #4 H:
/// ครึ่งแรกพิสูจน์ว่าเคสที่ผู้ใช้รายงานกลับมาถูก · ครึ่งหลังพิสูจน์ว่าใบที่ถูกอยู่แล้ว
/// **ไม่ถูกแตะ** (เทสต์ที่มีแต่ครึ่งแรกผ่านได้ทั้งตอนแก้ถูกและตอน "ปิดด่านทิ้ง")
/// </summary>
public class AdjustmentNoteAccountTests
{
    // ═══════════════════════ ครึ่งที่ 1 — ของที่พังต้องกลับมาถูก ═══════════════════════

    [Fact]
    public void ใบลดหนี้ฝั่งซื้อ_ผังหมวดรายได้_43060_ต้องถูกปฏิเสธ()
    {
        var v = AdjustmentNoteAccount.SideViolation(
            isPurchaseSide: true, isCreditNote: true,
            AccountType.Revenue, "43060", "ส่วนลดรับ", "ค่าวัสดุสิ้นเปลือง");
        Assert.NotNull(v);
        // ข้อความต้องบอก "ทางไปต่อ" ไม่ใช่แค่ปฏิเสธ (F2 ข้อ 8)
        Assert.Contains("43060", v);
        Assert.Contains("วัสดุสิ้นเปลือง", v);
        Assert.Contains("เว้นว่าง", v);
    }

    [Fact]
    public void ใบเพิ่มหนี้ฝั่งซื้อ_ผังหมวดรายได้_ต้องถูกปฏิเสธเหมือนใบลดหนี้()
    {
        var v = AdjustmentNoteAccount.SideViolation(
            isPurchaseSide: true, isCreditNote: false,
            AccountType.Revenue, "41000", "รายได้จากการขาย", "ค่าขนส่งเพิ่ม");
        Assert.NotNull(v);
        Assert.Contains("ใบเพิ่มหนี้", v);
    }

    [Fact]
    public void ใบลดหนี้ฝั่งขาย_ผังหมวดค่าใช้จ่าย_ต้องถูกปฏิเสธ()
    {
        var v = AdjustmentNoteAccount.SideViolation(
            isPurchaseSide: false, isCreditNote: true,
            AccountType.Expense, "54420", "ค่าวัสดุสิ้นเปลืองสำนักงาน", "ส่วนลดให้ลูกค้า");
        Assert.NotNull(v);
        Assert.Contains("4xxxx", v);
    }

    [Theory]
    [InlineData(DocumentType.PurchaseInvoice)]
    [InlineData(DocumentType.Expense)]
    [InlineData(DocumentType.PaymentVoucher)]
    [InlineData(DocumentType.CertificateInLieu)]
    public void ใบต้นทางฝั่งซื้อ_ต้องทำให้ใบปรับปรุงเป็นฝั่งซื้อ(DocumentType src)
    {
        Assert.True(AdjustmentNoteAccount.SourceIsPurchaseSide(src));
        // ใบต้นทางชนะ override ที่ผู้ใช้ติ๊กผิด — ใบลดหนี้ของใบซื้อไม่มีทางเป็นฝั่งขาย
        Assert.True(AdjustmentNoteAccount.ResolveSide(src, userOverride: false));
    }

    [Theory]
    [InlineData(DocumentType.TaxInvoice)]
    [InlineData(DocumentType.Invoice)]
    [InlineData(DocumentType.Receipt)]
    [InlineData(DocumentType.ReceiptVoucher)]
    public void ใบต้นทางฝั่งขาย_ต้องไม่ถูกนับเป็นฝั่งซื้อ(DocumentType src)
    {
        Assert.False(AdjustmentNoteAccount.SourceIsPurchaseSide(src));
        Assert.False(AdjustmentNoteAccount.ResolveSide(src, userOverride: true));
    }

    [Fact]
    public void ไม่มีใบต้นทาง_ต้องใช้ฝั่งที่ผู้ใช้เลือก()
    {
        Assert.True(AdjustmentNoteAccount.ResolveSide(null, true));
        Assert.False(AdjustmentNoteAccount.ResolveSide(null, false));
    }

    [Fact]
    public void ไม่มีทั้งใบต้นทางและตัวเลือกของผู้ใช้_ต้องตอบว่าไม่รู้_ไม่ใช่เดา()
    {
        // G3: "ไม่รู้" ต้องเป็นค่าที่เห็นได้ ห้ามคืน false แล้วให้ผู้เรียกเข้าใจว่า "ฝั่งขาย"
        Assert.Null(AdjustmentNoteAccount.ResolveSide(null, null));
    }

    [Fact]
    public void รับคืนสินค้าที่ตัดสต็อก_ผังไม่ใช่บัญชีคุมสต็อก_ต้องเตือน()
    {
        var w = AdjustmentNoteAccount.StockValuationWarning(
            isPurchaseSide: true, lineReturnsGoods: true, productTracksStock: true,
            pickedAccountCode: "54420", inventoryControlAccountCode: "11500",
            lineDescription: "กระดาษ A4");
        Assert.NotNull(w);
        Assert.Contains(AdjustmentNoteAccount.StockRuleCode, w);
        Assert.Contains("11500", w);
        Assert.Contains("54420", w);
    }

    // ═══════════════════ ครึ่งที่ 2 — ของที่ถูกอยู่แล้วห้ามขยับ ═══════════════════

    [Fact]
    public void ยังไม่รู้ฝั่ง_ต้องปล่อยผ่าน_ไม่ล้มเอกสารเก่าและทางเข้า_API()
    {
        // ใบที่ไม่ได้อ้างใบต้นทางและไม่เคยส่งฝั่งมา (CSV · integration · ใบก่อนมีช่องนี้)
        Assert.Null(AdjustmentNoteAccount.SideViolation(
            null, true, AccountType.Revenue, "43060", "ส่วนลดรับ", "อะไรก็ตาม"));
        Assert.Null(AdjustmentNoteAccount.SideViolation(
            null, false, AccountType.Expense, "54420", "ค่าวัสดุ", "อะไรก็ตาม"));
    }

    [Theory]
    [InlineData(AccountType.Expense, "54420", "ค่าวัสดุสิ้นเปลืองสำนักงาน")]
    [InlineData(AccountType.Asset, "11500", "สินค้าคงเหลือ")]
    [InlineData(AccountType.Asset, "11810", "เงินมัดจำ")]
    [InlineData(AccountType.Liability, "21210", "เจ้าหนี้การค้า")]
    public void ใบลดหนี้ฝั่งซื้อ_ผังที่ถูกต้องต้องผ่านทุกตัว(AccountType at, string code, string name)
    {
        Assert.Null(AdjustmentNoteAccount.SideViolation(true, true, at, code, name, "รายการ"));
    }

    [Theory]
    [InlineData(AccountType.Revenue, "41000", "รายได้จากการขาย")]
    [InlineData(AccountType.Revenue, "43060", "ส่วนลดรับ")]
    [InlineData(AccountType.Asset, "11300", "ลูกหนี้การค้า")]
    [InlineData(AccountType.Liability, "21712", "รายได้รับล่วงหน้า")]
    public void ใบลดหนี้ฝั่งขาย_ผังที่ถูกต้องต้องผ่านทุกตัว(AccountType at, string code, string name)
    {
        Assert.Null(AdjustmentNoteAccount.SideViolation(false, true, at, code, name, "รายการ"));
    }

    [Fact]
    public void หมวดสินทรัพย์หนี้สิน_ต้องผ่านทั้งสองฝั่ง_ด่านที่ฟ้องใบถูกทุกใบคือด่านที่ปิดตัวเอง()
    {
        foreach (var side in new[] { true, false })
            foreach (var at in new[] { AccountType.Asset, AccountType.Liability, AccountType.Equity })
                Assert.Null(AdjustmentNoteAccount.SideViolation(
                    side, true, at, "11810", "เงินมัดจำ", "มัดจำคืน"));
    }

    [Fact]
    public void ผังตรงกับบัญชีคุมสต็อกอยู่แล้ว_ต้องไม่เตือน()
    {
        Assert.Null(AdjustmentNoteAccount.StockValuationWarning(
            true, true, true, "11500", "11500", "กระดาษ A4"));
    }

    [Theory]
    // ไม่ใช่ฝั่งซื้อ · ไม่ใช่การรับคืนของ · สินค้าไม่ตัดสต็อก · ไม่ได้เลือกผังเอง
    [InlineData(false, true, true, "54420", "11500")]
    [InlineData(true, false, true, "54420", "11500")]
    [InlineData(true, true, false, "54420", "11500")]
    [InlineData(true, true, true, null, "11500")]
    [InlineData(true, true, true, "54420", null)]
    public void เคสที่ไม่เข้าข่าย_ต้องเงียบ(
        bool purchase, bool returns, bool tracks, string? picked, string? control)
    {
        Assert.Null(AdjustmentNoteAccount.StockValuationWarning(
            purchase, returns, tracks, picked, control, "รายการ"));
    }

    // ═══════════ ฝั่งของใบที่ลงบัญชีไปแล้ว (รายงาน/แบบยื่น) ═══════════

    [Fact]
    public void ผู้ใช้สั่งย้ายฝั่งเอง_ต้องชนะทุกชั้น_เพราะการย้ายฝั่งลง_JE_ใหม่ให้แล้ว()
    {
        var r = AdjustmentNoteAccount.ResolvePostedSide(
            userOverride: false, sourceType: DocumentType.PurchaseInvoice,
            glTouchedInputVat: true, contactIsSupplierOnly: true);
        Assert.False(r.IsPurchase);
        Assert.False(r.Unknown);
    }

    [Fact]
    public void ไม่มีคำสั่งย้าย_ใบต้นทางชนะ_GL()
    {
        var r = AdjustmentNoteAccount.ResolvePostedSide(
            null, DocumentType.TaxInvoice, glTouchedInputVat: true, contactIsSupplierOnly: true);
        Assert.False(r.IsPurchase);
        Assert.False(r.Unknown);
    }

    [Fact]
    public void ไม่มีใบต้นทาง_GL_ชนะบทบาทคู่ค้า()
    {
        var r = AdjustmentNoteAccount.ResolvePostedSide(
            null, null, glTouchedInputVat: false, contactIsSupplierOnly: true);
        Assert.False(r.IsPurchase);   // JE แตะภาษีขาย 2191x = ฝั่งขาย
        Assert.False(r.Unknown);
    }

    [Fact]
    public void เหลือแต่บทบาทคู่ค้า_ผู้ขายอย่างเดียวคือฝั่งซื้อ()
    {
        var r = AdjustmentNoteAccount.ResolvePostedSide(null, null, null, contactIsSupplierOnly: true);
        Assert.True(r.IsPurchase);
        Assert.False(r.Unknown);
    }

    [Fact]
    public void ไม่มีหลักฐานเลย_ต้องเป็น_Unknown_ไม่ใช่เดาว่าฝั่งซื้อ()
    {
        // ทิศที่ผิดของเดิม: `DocumentSide.IsPurchase(CreditNote)` **ไม่รับ ourRole**
        // ⇒ คืน true เสมอ ⇒ ใบลดหนี้ฝั่งขายถูกนับยอดยกเว้นเข้าช่องฝั่งซื้อของ ภ.พ.30
        // ที่นี่ต้องตอบว่า "ไม่รู้" เพื่อให้ผู้เรียก **ไม่นับเข้าช่องใดเลย** (G3)
        var r = AdjustmentNoteAccount.ResolvePostedSide(null, null, null, contactIsSupplierOnly: false);
        Assert.True(r.Unknown);
    }

    [Fact]
    public void ล็อกพฤติกรรมของ_DocumentSide_ที่เป็นเหตุให้ห้ามใช้เดี่ยว_ๆ()
    {
        // เทสต์นี้ไม่ได้บอกว่า `DocumentSide` ผิด — มันถูกตามสัญญาของมันเอง
        // (ชนิดกำกวม + ไม่รู้บทบาท = ฝั่งซื้อ) · ล็อกไว้เพื่อให้คนถัดไปเห็นว่า
        // **ทำไมห้ามเรียกมันเดี่ยว ๆ กับ CN/DN** แล้วไปเดาเอาเองว่ามันตอบถูก
        Assert.True(DocumentSide.IsPurchase(DocumentType.CreditNote));
        Assert.True(DocumentSide.IsPurchase(DocumentType.DebitNote));
        Assert.False(DocumentSide.IsPurchase(DocumentType.CreditNote, "Seller"));
    }

    [Fact]
    public void ชุดชนิดใบต้นทางฝั่งซื้อ_ต้องมี_4_ชนิดพอดี()
    {
        // ล็อกไว้เพื่อให้การเพิ่ม/ลดชนิดเป็นการตัดสินใจที่มองเห็น ไม่ใช่ผลข้างเคียง
        Assert.Equal(4, AdjustmentNoteAccount.PurchaseSourceTypes.Count);
    }
}

/// <summary>คำบรรยายบรรทัดจาก OCR เมื่อกระดาษไม่ได้บอกรายการ —
/// ผู้ใช้รายงานว่าเห็นคำว่า "TaxInvoice" (ชื่อ enum ดิบ) ในช่องรายละเอียดบนฟอร์ม
/// ซึ่งค่าเดียวกันนี้ถูกพิมพ์ลงกระดาษ §86/4 และลงคำบรรยายบรรทัด JE ด้วย</summary>
public class OcrFallbackLineDescriptionTests
{
    [Fact]
    public void มีเลขเอกสาร_ต้องใช้เลขเอกสาร_ไม่ใช่ชื่อชนิดเอกสาร()
    {
        var d = Accounting.Services.Implementations.OcrService.FallbackLineDescription(
            "RT69/00013", "บริษัท ทดสอบ จำกัด");
        Assert.Equal("รายการตามเอกสารเลขที่ RT69/00013", d);
        Assert.DoesNotContain("TaxInvoice", d);
    }

    [Fact]
    public void ไม่มีเลขเอกสารแต่มีชื่อผู้ขาย_ต้องใช้ชื่อผู้ขาย()
    {
        var d = Accounting.Services.Implementations.OcrService.FallbackLineDescription(
            null, "บริษัท ทดสอบ จำกัด");
        Assert.Equal("รายการจาก บริษัท ทดสอบ จำกัด", d);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "  ")]
    public void ไม่มีอะไรเลย_ต้องเขียนให้รู้ว่าต้องแก้_ไม่ใช่คำที่ดูเหมือนมีข้อมูล(
        string? docNo, string? vendor)
    {
        var d = Accounting.Services.Implementations.OcrService.FallbackLineDescription(docNo, vendor);
        Assert.Contains("กรุณาระบุ", d);
        // §86/4 บังคับให้ Description มีอย่างน้อย 1 ตัวอักษร — ต้องไม่ว่างไม่ว่ากรณีใด
        Assert.False(string.IsNullOrWhiteSpace(d));
    }
}
