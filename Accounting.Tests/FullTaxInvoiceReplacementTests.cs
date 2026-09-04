using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ออกใบกำกับภาษีเต็มรูป "แทน" ใบเสร็จ/ใบกำกับอย่างย่อ (§86/6 → §86/4)
///
/// ═══ ที่มา (ผู้ใช้ถาม 2026-09-03) ═══
/// "ใบเสร็จรับเงิน/ใบกำกับภาษีอย่างย่อ ควรต้องแปลงเอกสารเป็น ใบเสร็จรับเงิน/
/// ใบกำกับภาษี ได้มั้ย" — ได้ แต่**ต้องเป็นใบแทน ไม่ใช่ใบเพิ่ม**: ใบเดิมนับ
/// ภาษีขายเข้า ภ.พ.30 ไปแล้ว (tax point = วันรับเงิน §78/1) การออกใบที่สอง
/// โดยไม่เรียกคืนใบแรก = ภาษีขาย + รายได้ ถูกรายงานสองครั้งจากการขายครั้งเดียว
/// </summary>
public class FullTaxInvoiceReplacementTests
{
    private static FullTaxInvoiceEligibility Check(
        bool issued = true, bool alreadyFull = false, decimal vat = 70m,
        bool replaced = false, bool vatRegistered = true, params string[] missing)
        => FullTaxInvoiceReplacement.Check(issued, alreadyFull, vat, replaced, vatRegistered, missing);

    [Fact]
    public void ใบเสร็จมี_VAT_ผู้ซื้อครบ_ออกใบแทนได้()
    {
        var r = Check();
        Assert.True(r.Allowed);
        Assert.Equal(FullTaxInvoiceBlockReason.None, r.Reason);
        Assert.Null(r.Message);
    }

    [Fact]
    public void บริษัทยังไม่จด_VAT_ห้ามออกใบกำกับชนิดใดเลย()
    {
        // §90/2 — ผู้ไม่จดทะเบียนออกใบกำกับภาษี = ความผิดอาญา
        var r = Check(vatRegistered: false);
        Assert.False(r.Allowed);
        Assert.Equal(FullTaxInvoiceBlockReason.NotVatRegistered, r.Reason);
    }

    [Fact]
    public void ใบต้นทางยังไม่อนุมัติ_ยังไม่มีการขายให้รับรอง()
        => Assert.Equal(FullTaxInvoiceBlockReason.SourceNotIssued,
            Check(issued: false).Reason);

    [Fact]
    public void ใบต้นทางไม่มี_VAT_ไม่มีภาษีขายให้ใบกำกับรับรอง()
        => Assert.Equal(FullTaxInvoiceBlockReason.NoVatOnSource, Check(vat: 0m).Reason);

    [Fact]
    public void ยอด_VAT_ระดับเศษสตางค์_ถือว่าไม่มี_VAT()
    {
        // เศษไม่ถึงครึ่งสตางค์ = ไม่มี VAT จริง (เกณฑ์เดียวกับที่ใช้ทั้งระบบ)
        Assert.Equal(FullTaxInvoiceBlockReason.NoVatOnSource, Check(vat: 0.004m).Reason);
        Assert.Equal(FullTaxInvoiceBlockReason.NoVatOnSource, Check(vat: 0.005m).Reason);
    }

    [Fact]
    public void ยอด_VAT_เกินครึ่งสตางค์_ถือว่ามี_VAT()
        => Assert.True(Check(vat: 0.006m).Allowed);

    [Fact]
    public void ออกใบแทนไปแล้ว_ห้ามออกซ้ำ()
    {
        // การขายครั้งเดียวมีใบกำกับได้ใบเดียว (§86) — ถ้าออกซ้ำได้
        // ใบที่สองจะไปนับ ภ.พ.30 อีกแถวทั้งที่ใบแรกนับแทนใบเสร็จอยู่แล้ว
        var r = Check(replaced: true);
        Assert.False(r.Allowed);
        Assert.Equal(FullTaxInvoiceBlockReason.AlreadyReplaced, r.Reason);
    }

    [Fact]
    public void ใบที่เป็นใบกำกับเต็มรูปอยู่แล้ว_ไม่ต้องออกแทน()
        => Assert.Equal(FullTaxInvoiceBlockReason.AlreadyFullTaxInvoice,
            Check(alreadyFull: true).Reason);

    [Fact]
    public void ผู้ซื้อไม่ครบ_86_4_ต้องบล็อกพร้อมบอกว่าขาดอะไร()
    {
        // ออกไปก็ยังเป็นใบที่ผู้ซื้อเคลมภาษีซื้อไม่ได้ — บล็อกพร้อมทางไปต่อ
        // ดีกว่าออกใบที่ไม่มีประโยชน์แล้วเผาเลขที่ §86/4 ทิ้งหนึ่งเลข
        var r = Check(missing: new[] { "ที่อยู่ผู้ซื้อ", "เลขผู้เสียภาษีผู้ซื้อ 13 หลัก" });
        Assert.False(r.Allowed);
        Assert.Equal(FullTaxInvoiceBlockReason.BuyerIncomplete, r.Reason);
        Assert.Contains("ที่อยู่ผู้ซื้อ", r.Message!);
        Assert.Contains("เลขผู้เสียภาษีผู้ซื้อ 13 หลัก", r.Message!);
    }

    [Fact]
    public void ทุกเหตุที่บล็อก_ต้องมีข้อความอธิบายเสมอ()
    {
        // "ปุ่มหายไปเฉย ๆ" = silent no-op ที่ผู้ใช้ debug ไม่ได้ —
        // ทุกทางปฏิเสธต้องมีข้อความที่เอาไปโชว์ได้
        FullTaxInvoiceEligibility[] blocked =
        {
            Check(vatRegistered: false),
            Check(issued: false),
            Check(alreadyFull: true),
            Check(vat: 0m),
            Check(replaced: true),
            Check(missing: new[] { "ชื่อผู้ซื้อ" }),
        };
        foreach (var r in blocked)
        {
            Assert.False(r.Allowed);
            Assert.NotEqual(FullTaxInvoiceBlockReason.None, r.Reason);
            Assert.False(string.IsNullOrWhiteSpace(r.Message), $"{r.Reason} ไม่มีข้อความอธิบาย");
        }
    }

    [Fact]
    public void ลำดับด่าน_บริษัทไม่จด_VAT_ชนะทุกเหตุอื่น()
    {
        // เหตุที่ "รากที่สุด" ต้องถูกรายงานก่อน ไม่งั้นผู้ใช้ไปแก้ข้อมูลผู้ซื้อ
        // จนครบแล้วยังกดไม่ได้อยู่ดี (ไล่ผิดทางทั้งวัน)
        var r = FullTaxInvoiceReplacement.Check(
            sourceIsIssued: false, sourceIsFullTaxInvoice: true, sourceVatAmount: 0m,
            alreadyReplaced: true, companyIsVatRegistered: false,
            missingBuyerFields: new[] { "ชื่อผู้ซื้อ" });
        Assert.Equal(FullTaxInvoiceBlockReason.NotVatRegistered, r.Reason);
    }

    // ── หมายเหตุที่ต้องปรากฏบนกระดาษ/ในบันทึกภายใน ──

    [Fact]
    public void หมายเหตุบนใบแทน_ต้องอ้างเลขที่และวันที่ใบเดิมเป็น_พ_ศ_()
    {
        var note = FullTaxInvoiceReplacement.ReplacementNote(
            "REC-202608-0007", new DateTime(2026, 8, 20, 0, 0, 0, DateTimeKind.Utc));
        Assert.Contains("REC-202608-0007", note);
        Assert.Contains("20/08/2569", note);   // ค.ศ. 2026 + 543 (แบบยื่น/เอกสารทางการใช้ พ.ศ.)
        Assert.Contains("ยกเลิก", note);        // ผู้ซื้อต้องรู้ว่าใบเดิมใช้ไม่ได้แล้ว
    }

    [Fact]
    public void หมายเหตุบนใบเดิม_ต้องบอกว่าทำไมหายจากรายงานภาษีขาย()
    {
        var note = FullTaxInvoiceReplacement.OriginalRecalledNote("TIV-202608-0031", "ลูกค้าขอเต็มรูป");
        Assert.Contains("TIV-202608-0031", note);
        Assert.Contains("ไม่นับซ้ำในรายงานภาษีขาย", note);
        Assert.Contains("ลูกค้าขอเต็มรูป", note);
    }

    [Fact]
    public void ไม่ระบุเหตุผล_หมายเหตุยังต้องอ่านรู้เรื่อง()
    {
        var note = FullTaxInvoiceReplacement.OriginalRecalledNote("TIV-1", null);
        Assert.DoesNotContain("เหตุผล:", note);
        Assert.Contains("TIV-1", note);
    }
}
