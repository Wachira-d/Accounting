using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบสอง (2026-10-12) หลังแก้ M4 + L3 — ด่าน "เอกสารฝั่งผู้จ่ายเลือก gov1/1 ไม่ได้" ห้ามทำให้เอกสารลูก
/// (แปลง/คัดลอก/รายการประจำ) ล้มด้วยค่าที่ผู้ใช้ไม่ได้เลือกในคำขอนี้ · แก้ไขใบแล้วค่าเดิมบันทึกซ้ำได้แม้สลับ/แก้คำอธิบาย ·
/// คำเตือน WHT ที่ผ่านโดยไม่มีคนอ้างมาตราหัก ณ ที่จ่าย ไม่ใช่ §86/§82/5(1)
/// </summary>
public class WhtDerivedIncomeTypeTests
{
    [Theory]
    [InlineData("1", true, null)]          // เทมเพลต/PO เก็บ "เงินเดือน" → PV: ทิ้ง (ไม่ใช่ล้มทั้งใบ)
    [InlineData("40(1)", true, null)]
    [InlineData("gov1", true, null)]
    [InlineData("8ins", true, "8ins")]     // ทิศตรงข้าม: ประเภทปกติตามไปครบ
    [InlineData("40(8)", true, "8")]
    [InlineData("gov1", false, "gov1")]    // ฝั่งรับเงิน: ใบแจ้งหนี้ให้หน่วยงานรัฐคงค่าเดิม
    [InlineData("1", false, "1")]
    [InlineData(null, true, null)]
    public void Inherit_ลงเอกสารฝั่งผู้จ่าย_ทิ้งประเภทต้องห้าม(string? stored, bool payerSide, string? expected)
        => Assert.Equal(expected, DocumentLineIncomeType.Inherit(stored, payerSide));

    [Fact]
    public void ค่าที่สืบทอดลงฝั่งผู้จ่าย_ผ่านด่านL3เสมอ()
    {
        foreach (var t in ThaiWhtRateTable.All)
        {
            var inherited = DocumentLineIncomeType.Inherit(t.Code, targetIsPayerSide: true);
            Assert.Null(DocumentLineIncomeType.PayerSideProblem(true, inherited, null));
        }
    }

    [Theory]
    [InlineData("gov1", null)]
    [InlineData("1", null)]
    [InlineData("8ins", "8ins")]
    [InlineData("40(8)", "40(8)")]         // ไม่ normalize ค่าที่เขียนเอง
    [InlineData("zz", "zz")]               // ค่าที่ชี้ขาดไม่ได้คงไว้ให้คนเห็น
    [InlineData(null, null)]
    public void OCRเขียนบรรทัดฝั่งผู้จ่าย_ทิ้งเฉพาะประเภทต้องห้าม(string? code, string? expected)
        => Assert.Equal(expected, DocumentLineIncomeType.ForSystemWrittenPayerLine(code));

    [Fact]
    public void แก้ไขใบ_ค่าเดิมบนบรรทัดใดก็บันทึกซ้ำได้_แม้แก้คำอธิบายหรือแทรกบรรทัด()
    {
        var prev = new List<(string? Description, string? Code)> { ("ค่าบริการ", "8"), ("เงินอุดหนุน", "gov1") };
        // แทรกบรรทัดข้างบน ⇒ "gov1" เลื่อนไป index 2 + แก้คำอธิบาย — ยังเป็นค่าเดิมของเอกสาร
        var kept = DocumentLineIncomeType.KeptForPayerSideCheck(prev, 2, "เงินอุดหนุน (แก้)", "gov1");
        Assert.Equal("gov1", kept);
        Assert.Null(DocumentLineIncomeType.PayerSideProblem(true, "gov1", kept));
    }

    [Fact]
    public void ทิศตรงข้าม_เลือกgov1ใหม่บนใบที่ไม่เคยมี_ยังถูกปฏิเสธ()
    {
        var prev = new List<(string? Description, string? Code)> { ("ค่าบริการ", "8") };
        var kept = DocumentLineIncomeType.KeptForPayerSideCheck(prev, 0, "ค่าบริการ", "gov1");
        Assert.Equal("8", kept);
        Assert.NotNull(DocumentLineIncomeType.PayerSideProblem(true, "gov1", kept));
    }

    [Fact]
    public void คำเตือนWHTที่ผ่าน_อ้างมาตราหักณที่จ่าย()
    {
        var rate = WhtLineRateWarning.Prefix + " อัตราไม่ตรงประเภท";
        var payee = WhtCertIssuePlan.ApprovalWarningPrefix + " ผู้ถูกหักไม่มีเลขผู้เสียภาษี";
        Assert.Equal(ApprovalAcknowledgement.WhtWarningsLegalReference, ApprovalAcknowledgement.LegalReference(new[] { rate, payee }));
        // ทิศตรงข้าม: คำเตือนทั่วไปยังอ้างมาตราเดิม
        Assert.Equal(ApprovalAcknowledgement.GeneralWarningsLegalReference, ApprovalAcknowledgement.LegalReference(new[] { "อื่น ๆ" }));
    }
}
