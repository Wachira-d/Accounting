using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// สิทธิ์ออกใบกำกับภาษีอย่างย่อบนสลิป POS (§86/6)
///
/// ═══ ที่มา (บั๊กจริง — POS_MULTI_BRANCH_ANALYSIS.md ทีม CPA) ═══
/// <c>pos.html</c> พิมพ์คำว่า <b>"ใบเสร็จรับเงิน / ใบกำกับภาษีอย่างย่อ"</b> เป็น string
/// literal <b>ทุกใบโดยไม่ตรวจอะไรเลย</b> — บริษัทที่ยังไม่ได้รับอนุมัติ ภ.พ.06 (หรือยังไม่
/// จด VAT ด้วยซ้ำ) ก็พิมพ์คำนี้ออกมา = <b>ออกใบกำกับภาษีโดยไม่มีสิทธิ์</b> · ผู้ซื้อที่รับใบไป
/// เคลมภาษีซื้อไม่ได้ตาม §82/5(5) ทั้งที่หน้ากระดาษบอกว่าเป็นใบกำกับ
/// </summary>
public class PosSlipHeaderTests
{
    private static readonly DateTime Approved = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = new(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ครบเงื่อนไข_ออกอย่างย่อได้()
    {
        var r = PosSlipHeader.Resolve(true, true, Approved, 7m, Today, requirePhoR06: true,
            billBelongsToBranch: false, issuerTaxBranchCode: null);
        Assert.True(r.CanIssueAbbreviated);
        Assert.Equal(PosSlipHeader.AbbreviatedTaxInvoice, r.Title);
        Assert.Null(r.Message);
    }

    [Fact]
    public void ยังไม่จด_VAT_ห้ามพิมพ์คำว่าใบกำกับ()
    {
        var r = PosSlipHeader.Resolve(false, true, Approved, 7m, Today, requirePhoR06: true,
            billBelongsToBranch: false, issuerTaxBranchCode: null);
        Assert.False(r.CanIssueAbbreviated);
        Assert.Equal(PosSlipHeader.Receipt, r.Title);
        Assert.DoesNotContain("ใบกำกับ", r.Title);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NotVatRegistered, r.Reason);
    }

    [Fact]
    public void จด_VAT_แต่ไม่มี_ภพ06_ห้ามออก()
    {
        // นี่คือเคสที่ระบบเดิมพลาด — บริษัทจด VAT แล้วจึงดู "ถูกต้อง" ผิวเผิน
        var r = PosSlipHeader.Resolve(true, false, null, 7m, Today, requirePhoR06: true,
            billBelongsToBranch: false, issuerTaxBranchCode: null);
        Assert.False(r.CanIssueAbbreviated);
        Assert.Equal(PosSlipHeader.Receipt, r.Title);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NoPhoR06Approval, r.Reason);
        Assert.Contains("ภ.พ.06", r.Message);
    }

    [Fact]
    public void ติ๊กธงแต่ไม่มีวันที่อนุมัติ_ยังออกไม่ได้()
    {
        // ธง = เจตนา · วันที่ = หลักฐาน — ต้องมีทั้งคู่
        var r = PosSlipHeader.Resolve(true, true, null, 7m, Today, requirePhoR06: true,
            billBelongsToBranch: false, issuerTaxBranchCode: null);
        Assert.False(r.CanIssueAbbreviated);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NoPhoR06Approval, r.Reason);
    }

    [Fact]
    public void บิลลงวันที่ก่อนวันอนุมัติ_ออกไม่ได้()
    {
        var before = Approved.AddDays(-1);
        Assert.False(PosSlipHeader.Resolve(true, true, Approved, 7m, before, requirePhoR06: true,
            billBelongsToBranch: false, issuerTaxBranchCode: null).CanIssueAbbreviated);
        // วันเดียวกับวันอนุมัติ = ออกได้
        Assert.True(PosSlipHeader.Resolve(true, true, Approved, 7m, Approved, requirePhoR06: true,
            billBelongsToBranch: false, issuerTaxBranchCode: null).CanIssueAbbreviated);
    }

    [Fact]
    public void บิลที่ไม่มี_VAT_พิมพ์เป็นใบเสร็จ()
    {
        // สินค้ายกเว้น §81 หรืออัตรา 0% — คำว่า "ใบกำกับภาษี" จะทำให้ผู้ซื้อเข้าใจผิด
        // ว่ามีภาษีซื้อให้เคลม
        var r = PosSlipHeader.Resolve(true, true, Approved, 0m, Today, requirePhoR06: true,
            billBelongsToBranch: false, issuerTaxBranchCode: null);
        Assert.False(r.CanIssueAbbreviated);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NoVatOnBill, r.Reason);
    }

    [Theory]
    [InlineData("00000", "สำนักงานใหญ่")]
    [InlineData("00003", "สาขาที่ 00003")]
    [InlineData("00012", "สาขาที่ 00012")]
    public void รหัสสาขาที่ถูกต้อง_แปลงเป็นข้อความบนกระดาษ(string code, string expected)
        => Assert.Equal(expected, PosSlipHeader.BranchLabel(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3")]        // ไม่ครบ 5 หลัก
    [InlineData("000003")]   // เกิน 5 หลัก
    [InlineData("0000A")]    // ไม่ใช่ตัวเลขล้วน
    public void รหัสสาขาที่ยังไม่รู้_ต้องคืน_null_ห้ามเดาเป็นสำนักงานใหญ่(string? code)
    {
        // "ค่า default ที่แต่งขึ้นอันตรายกว่าการไม่ตอบ" — เดาเป็น 00000 แปลว่าใบของ
        // สาขาที่ 3 จะประกาศตัวเป็นสำนักงานใหญ่ทุกใบ
        Assert.Null(PosSlipHeader.BranchLabel(code));
    }
}

/// <summary>สวิตช์ระดับแพลตฟอร์ม "บังคับ ภ.พ.06 หรือไม่" (คำตัดสินเจ้าของ 2026-09-19)
///
/// <para>ข้อบังคับ ภ.พ.06 เป็น<b>นโยบายที่กรมสรรพากรเปลี่ยนได้</b> — แอดมินแพลตฟอร์มจึงปิด
/// ด่านนี้ได้ทั้งระบบโดยไม่ต้องแก้โค้ด · เทสต์ชุดนี้ล็อก<b>ทั้งสองทิศ</b>: ปิดแล้วต้องออกได้จริง
/// และปิดแล้วต้อง<b>ไม่</b>ทำให้ด่าน "ยังไม่จด VAT" หลุดตามไปด้วย</para></summary>
public class AbbreviatedTaxInvoiceRuleTests
{
    private static readonly DateTime Approved = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Today = new(2026, 9, 19, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void บังคับ_ภพ06_ไม่มีอนุมัติ_ออกไม่ได้()
        => Assert.Equal(AbbreviatedInvoiceBlockReason.NoPhoR06Approval,
            AbbreviatedTaxInvoiceRule.Judge(true, false, null, Today, requirePhoR06: true));

    [Fact]
    public void ปิดสวิตช์_ไม่มี_ภพ06_ก็ออกได้()
        => Assert.Equal(AbbreviatedInvoiceBlockReason.None,
            AbbreviatedTaxInvoiceRule.Judge(true, false, null, Today, requirePhoR06: false));

    [Fact]
    public void ปิดสวิตช์_แต่ยังไม่จด_VAT_ก็ยังออกไม่ได้()
    {
        // ทิศตรงข้าม: สวิตช์นี้ปิดได้เฉพาะด่าน ภ.พ.06 — §77/1 ปิดไม่ได้
        Assert.Equal(AbbreviatedInvoiceBlockReason.NotVatRegistered,
            AbbreviatedTaxInvoiceRule.Judge(false, true, Approved, Today, requirePhoR06: false));
        Assert.Equal(AbbreviatedInvoiceBlockReason.NotVatRegistered,
            AbbreviatedTaxInvoiceRule.Judge(false, false, null, Today, requirePhoR06: false));
    }

    [Fact]
    public void บังคับ_ครบทั้งธงและวันที่_ออกได้()
        => Assert.Equal(AbbreviatedInvoiceBlockReason.None,
            AbbreviatedTaxInvoiceRule.Judge(true, true, Approved, Today, requirePhoR06: true));

    [Fact]
    public void บังคับ_มีแต่ธงไม่มีวันที่_ออกไม่ได้()
        => Assert.Equal(AbbreviatedInvoiceBlockReason.NoPhoR06Approval,
            AbbreviatedTaxInvoiceRule.Judge(true, true, null, Today, requirePhoR06: true));

    [Fact]
    public void บังคับ_ใบลงวันที่ก่อนวันอนุมัติ_ออกไม่ได้()
    {
        var beforeApproval = Approved.AddDays(-1);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NoPhoR06Approval,
            AbbreviatedTaxInvoiceRule.Judge(true, true, Approved, beforeApproval, requirePhoR06: true));
        // ขอบ: วันอนุมัติพอดี = ออกได้
        Assert.Equal(AbbreviatedInvoiceBlockReason.None,
            AbbreviatedTaxInvoiceRule.Judge(true, true, Approved, Approved, requirePhoR06: true));
    }

    [Fact]
    public void ปิดสวิตช์_ไม่สนวันที่อนุมัติย้อนหลัง()
        => Assert.True(AbbreviatedTaxInvoiceRule.CanIssue(
            true, true, Approved, Approved.AddDays(-30), requirePhoR06: false));

    [Fact]
    public void ทุกเหตุผลที่บล็อก_ต้องมีข้อความบอกทางไปต่อ()
    {
        foreach (var reason in new[]
                 {
                     AbbreviatedInvoiceBlockReason.NotVatRegistered,
                     AbbreviatedInvoiceBlockReason.NoPhoR06Approval,
                     AbbreviatedInvoiceBlockReason.NoVatOnBill,
                     AbbreviatedInvoiceBlockReason.BranchTaxCodeMissing,
                 })
            Assert.False(string.IsNullOrWhiteSpace(AbbreviatedTaxInvoiceRule.Message(reason)));
        Assert.Null(AbbreviatedTaxInvoiceRule.Message(AbbreviatedInvoiceBlockReason.None));
    }

    [Fact]
    public void สลิป_POS_ปิดสวิตช์แล้วพิมพ์อย่างย่อได้()
    {
        var r = PosSlipHeader.Resolve(true, false, null, 7m, Today, requirePhoR06: false,
            billBelongsToBranch: false, issuerTaxBranchCode: null);
        Assert.True(r.CanIssueAbbreviated);
        Assert.Equal(PosSlipHeader.AbbreviatedTaxInvoice, r.Title);
    }

    [Fact]
    public void สลิป_POS_ปิดสวิตช์แต่บิลไม่มี_VAT_ยังเป็นใบเสร็จ()
    {
        // ทิศตรงข้าม: สวิตช์ไม่ได้ปิดกติกา "ไม่มี VAT ก็ไม่มีอะไรให้ใบกำกับรับรอง"
        var r = PosSlipHeader.Resolve(true, true, Approved, 0m, Today, requirePhoR06: false,
            billBelongsToBranch: false, issuerTaxBranchCode: null);
        Assert.False(r.CanIssueAbbreviated);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NoVatOnBill, r.Reason);
    }

    // ══════════════════════════════════════════════════════════════════
    //  §86/4(2) — บิลของสาขาที่ยังไม่มีรหัสสาขา (ประกาศอธิบดีฯ ฉบับที่ 199)
    //
    //  ที่มา: ทีม POS รอบ 183 รายงานว่า `IssueAbbreviatedInvoiceNumberAsync`
    //  ตกไปใช้ `"00000"` เมื่อสาขายังไม่กรอก `TaxBranchCode` — ซึ่งไม่ใช่
    //  "ไม่ระบุ" แต่แปลว่า **สำนักงานใหญ่** ⇒ กระดาษประกาศเท็จ **และ** เลขรัน
    //  ของสาขาไปกินเล่มสำนักงานใหญ่ (สองเล่มไม่ gap-free ตาม §86/4)
    // ══════════════════════════════════════════════════════════════════

    [Fact]
    public void บิลของสาขาที่ยังไม่มีรหัสสาขา_ห้ามออกอย่างย่อ()
    {
        var r = PosSlipHeader.Resolve(true, true, Approved, 7m, Today, requirePhoR06: true,
            billBelongsToBranch: true, issuerTaxBranchCode: null);
        Assert.False(r.CanIssueAbbreviated);
        Assert.Equal(AbbreviatedInvoiceBlockReason.BranchTaxCodeMissing, r.Reason);
        Assert.Equal(PosSlipHeader.Receipt, r.Title);
        // ต้องมีทางไปต่อ ไม่ใช่ตันเฉย ๆ (F2 ข้อ 8)
        Assert.Contains("รหัสสาขา", r.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]        // สั้นกว่า 5 หลัก
    [InlineData("001")]
    [InlineData("0000A")]    // มีตัวอักษร
    [InlineData("000001")]   // ยาวเกิน
    public void รหัสสาขาผิดรูปบนบิลของสาขา_ก็ห้ามออก(string code)
    {
        var r = PosSlipHeader.Resolve(true, true, Approved, 7m, Today, requirePhoR06: true,
            billBelongsToBranch: true, issuerTaxBranchCode: code);
        Assert.False(r.CanIssueAbbreviated);
        Assert.Equal(AbbreviatedInvoiceBlockReason.BranchTaxCodeMissing, r.Reason);
    }

    // ── ครึ่งที่ต้อง "ไม่ถูกแตะ" ────────────────────────────────────────

    [Theory]
    [InlineData("00000")]
    [InlineData("00001")]
    [InlineData("00123")]
    public void บิลของสาขาที่มีรหัสครบ_ออกได้ตามปกติ(string code)
    {
        var r = PosSlipHeader.Resolve(true, true, Approved, 7m, Today, requirePhoR06: true,
            billBelongsToBranch: true, issuerTaxBranchCode: code);
        Assert.True(r.CanIssueAbbreviated);
        Assert.Equal(PosSlipHeader.AbbreviatedTaxInvoice, r.Title);
    }

    [Fact]
    public void บริษัทที่ไม่มีสาขาเลย_ยังได้_00000_เหมือนเดิม()
    {
        // ร้านเดี่ยว/บริษัทที่ยังไม่เปิดใช้ Branch = สำนักงานใหญ่โดยนิยาม
        // (ถ้าเทสต์นี้แดง แปลว่าด่านใหม่ไปบล็อกลูกค้าเดิมทั้งหมด)
        Assert.Equal("00000", PosSlipHeader.BranchSeriesCode(false, null));
        Assert.Equal("00000", PosSlipHeader.BranchSeriesCode(false, ""));
        var r = PosSlipHeader.Resolve(true, true, Approved, 7m, Today, requirePhoR06: true,
            billBelongsToBranch: false, issuerTaxBranchCode: null);
        Assert.True(r.CanIssueAbbreviated);
    }

    [Fact]
    public void เลขรันของสาขาต้องไม่ไปปนเล่มสำนักงานใหญ่()
    {
        // หัวใจของบั๊ก: ก่อนแก้ ทั้งสองเคสคืน "00000" เหมือนกัน ⇒ เลขชุดเดียวกัน
        Assert.Equal("00007", PosSlipHeader.BranchSeriesCode(true, "00007"));
        Assert.Null(PosSlipHeader.BranchSeriesCode(true, null));
        Assert.Equal("00000", PosSlipHeader.BranchSeriesCode(false, null));
    }

    [Fact]
    public void ด่านสาขาอยู่หลังด่าน_VAT_และ_ภพ06_เสมอ()
    {
        // ลำดับเหตุผลต้องคงที่: บริษัทที่ยังไม่จด VAT ต้องได้เหตุผล "ยังไม่จด VAT"
        // ไม่ใช่ "ไม่มีรหัสสาขา" (ผู้ใช้จะไปกรอกรหัสสาขาแล้วก็ยังออกไม่ได้อยู่ดี)
        var noVat = PosSlipHeader.Resolve(false, true, Approved, 7m, Today, requirePhoR06: true,
            billBelongsToBranch: true, issuerTaxBranchCode: null);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NotVatRegistered, noVat.Reason);

        var noPhoR06 = PosSlipHeader.Resolve(true, false, null, 7m, Today, requirePhoR06: true,
            billBelongsToBranch: true, issuerTaxBranchCode: null);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NoPhoR06Approval, noPhoR06.Reason);

        var noVatOnBill = PosSlipHeader.Resolve(true, true, Approved, 0m, Today, requirePhoR06: true,
            billBelongsToBranch: true, issuerTaxBranchCode: null);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NoVatOnBill, noVatOnBill.Reason);
    }
}
