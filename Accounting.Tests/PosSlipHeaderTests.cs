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
        var r = PosSlipHeader.Resolve(true, true, Approved, 7m, Today);
        Assert.True(r.CanIssueAbbreviated);
        Assert.Equal(PosSlipHeader.AbbreviatedTaxInvoice, r.Title);
        Assert.Null(r.Message);
    }

    [Fact]
    public void ยังไม่จด_VAT_ห้ามพิมพ์คำว่าใบกำกับ()
    {
        var r = PosSlipHeader.Resolve(false, true, Approved, 7m, Today);
        Assert.False(r.CanIssueAbbreviated);
        Assert.Equal(PosSlipHeader.Receipt, r.Title);
        Assert.DoesNotContain("ใบกำกับ", r.Title);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NotVatRegistered, r.Reason);
    }

    [Fact]
    public void จด_VAT_แต่ไม่มี_ภพ06_ห้ามออก()
    {
        // นี่คือเคสที่ระบบเดิมพลาด — บริษัทจด VAT แล้วจึงดู "ถูกต้อง" ผิวเผิน
        var r = PosSlipHeader.Resolve(true, false, null, 7m, Today);
        Assert.False(r.CanIssueAbbreviated);
        Assert.Equal(PosSlipHeader.Receipt, r.Title);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NoPhoR06Approval, r.Reason);
        Assert.Contains("ภ.พ.06", r.Message);
    }

    [Fact]
    public void ติ๊กธงแต่ไม่มีวันที่อนุมัติ_ยังออกไม่ได้()
    {
        // ธง = เจตนา · วันที่ = หลักฐาน — ต้องมีทั้งคู่
        var r = PosSlipHeader.Resolve(true, true, null, 7m, Today);
        Assert.False(r.CanIssueAbbreviated);
        Assert.Equal(AbbreviatedInvoiceBlockReason.NoPhoR06Approval, r.Reason);
    }

    [Fact]
    public void บิลลงวันที่ก่อนวันอนุมัติ_ออกไม่ได้()
    {
        var before = Approved.AddDays(-1);
        Assert.False(PosSlipHeader.Resolve(true, true, Approved, 7m, before).CanIssueAbbreviated);
        // วันเดียวกับวันอนุมัติ = ออกได้
        Assert.True(PosSlipHeader.Resolve(true, true, Approved, 7m, Approved).CanIssueAbbreviated);
    }

    [Fact]
    public void บิลที่ไม่มี_VAT_พิมพ์เป็นใบเสร็จ()
    {
        // สินค้ายกเว้น §81 หรืออัตรา 0% — คำว่า "ใบกำกับภาษี" จะทำให้ผู้ซื้อเข้าใจผิด
        // ว่ามีภาษีซื้อให้เคลม
        var r = PosSlipHeader.Resolve(true, true, Approved, 0m, Today);
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
