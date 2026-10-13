using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำสั่งเจ้าของ 2026-10-13 "ระบบห้ามล็อกตาย — ผู้ใช้ตัดสิน ระบบเตือน": แหล่งเงินที่ผู้ใช้เลือกเป็นค่าใช้จ่าย/รายได้ตอนแก้ผลสแกน
/// เดิมปฏิเสธถาวร (ไม่มีทางไปต่อ) ⇒ ตอนนี้ถามยืนยัน (409) · ยืนยันแล้วเก็บคู่ตามผู้ใช้ + audit <c>OCR-ACCT-PLACEMENT-OVERRIDE</c>
/// และทุกผู้อ่านคู่ที่เก็บไว้ (จอ · สร้างเอกสาร · JE สินทรัพย์) ต้องไม่ตัดเครดิตทิ้งเงียบ ๆ (silent no-op)
/// </summary>
public class OcrAccountPlacementOverrideTests
{
    private static readonly GlAccountCandidate Supplies52120 = new("52120", "วัสดุสิ้นเปลือง", AccountType.Expense);
    private static readonly GlAccountCandidate RoomRepair51530 = new("51530", "ต้นทุนซ่อมบำรุงห้องพัก", AccountType.Expense);
    private static readonly GlAccountCandidate Sales41100 = new("41100", "รายได้ค่าห้องพัก", AccountType.Revenue);
    private static readonly GlAccountCandidate Cash11111 = new("11111", "เงินสด", AccountType.Asset);
    private static readonly GlAccountCandidate Director21230 = new("21230", "เจ้าหนี้กรรมการ", AccountType.Liability);

    [Fact]
    public void ไม่ยืนยัน_ยังถามยืนยันพร้อมเหตุผลและทางไปต่อ()
    {
        var d = OcrAccountPlacement.DecideCorrection(Supplies52120, RoomRepair51530, true, true);
        Assert.Equal(OcrAccountPlacement.CorrectionAction.RejectCredit, d.Action);
        Assert.Contains("51530", d.Reason);
        Assert.Contains("กดยืนยัน", d.Reason);   // บอกทางไปต่อ — ไม่ใช่ทางตัน
    }

    [Fact]
    public void ยืนยันแล้ว_เก็บคู่ตามผู้ใช้_ไม่สลับไม่ตัด()
    {
        var d = OcrAccountPlacement.DecideCorrection(Supplies52120, RoomRepair51530, true, true, acknowledged: true);
        Assert.Equal(OcrAccountPlacement.CorrectionAction.KeepAcknowledged, d.Action);
        Assert.Equal(("52120", "51530"), (d.DebitCode, d.CreditCode));
        Assert.Contains("ไม่นำคู่นี้ไปสอน", d.Reason);
        // แหล่งเงินเป็นรายได้ (ใบที่จริง ๆ เป็นการรับเงิน) ก็ยืนยันได้เหมือนกัน
        var rev = OcrAccountPlacement.DecideCorrection(Cash11111, Sales41100, false, true, acknowledged: true);
        Assert.Equal(OcrAccountPlacement.CorrectionAction.KeepAcknowledged, rev.Action);
        Assert.Equal(("11111", "41100"), (rev.DebitCode, rev.CreditCode));
    }

    [Fact]
    public void ทิศตรงข้าม_ธงยืนยันไม่เปลี่ยนคำตัดสินของคู่ที่ถูกหรือคู่สลับ()
    {
        // คู่ปกติ + ส่งธงมา ⇒ Keep ธรรมดา (ไม่มี audit override · ไม่ตัดการเรียนรู้)
        Assert.Equal(OcrAccountPlacement.CorrectionAction.Keep,
            OcrAccountPlacement.DecideCorrection(Supplies52120, Cash11111, true, true, acknowledged: true).Action);
        // ส่งทั้งคู่กลับด้านพอดี ⇒ ยังสลับเหมือนเดิม (ธงยืนยันไม่ใช่ใบอนุญาตให้ลงกลับด้าน)
        var swap = OcrAccountPlacement.DecideCorrection(Director21230, RoomRepair51530, true, true, acknowledged: true);
        Assert.Equal(OcrAccountPlacement.CorrectionAction.Swap, swap.Action);
        // ผู้ใช้ไม่ได้ส่งเครดิต (เครดิตของระบบเป็นค่าใช้จ่าย) ⇒ Keep — ไม่ถาม ไม่ audit
        Assert.Equal(OcrAccountPlacement.CorrectionAction.Keep,
            OcrAccountPlacement.DecideCorrection(Supplies52120, RoomRepair51530, true, false, acknowledged: true).Action);
    }

    [Fact]
    public void คู่ที่เก็บไว้ใต้การยืนยัน_ผู้อ่านไม่ตัดเครดิต()
    {
        var acked = OcrAccountPlacement.ResolveStored(Supplies52120, RoomRepair51530, acknowledgedCreditCode: "51530");
        Assert.Equal(OcrAccountPlacement.Verdict.Ok, acked.Verdict);
        Assert.Equal(("52120", "51530"), (acked.DebitCode, acked.CreditCode));
        Assert.Null(acked.Reason);
    }

    [Fact]
    public void ทิศตรงข้าม_การยืนยันผูกกับรหัสเครดิตที่ยืนยัน_เปลี่ยนเครดิตแล้วหมดผล()
    {
        // ยืนยัน 51530 ไว้ แต่ตอนนี้เครดิตเป็น 41100 ⇒ กติกาเดิม (ตัดแหล่งเงินที่เป็นรายได้)
        var stale = OcrAccountPlacement.ResolveStored(Supplies52120, Sales41100, acknowledgedCreditCode: "51530");
        Assert.Equal(OcrAccountPlacement.Verdict.CreditRejected, stale.Verdict);
        Assert.Null(stale.CreditCode);
        // ไม่มีการยืนยัน = พฤติกรรมเดิมทุกตัวอักษร
        Assert.Equal(OcrAccountPlacement.Verdict.CreditRejected,
            OcrAccountPlacement.ResolveStored(Supplies52120, RoomRepair51530).Verdict);
        Assert.Equal(OcrAccountPlacement.Verdict.Swapped,
            OcrAccountPlacement.ResolveStored(Director21230, RoomRepair51530, acknowledgedCreditCode: null).Verdict);
    }

    [Theory]
    [InlineData("51530", "51530", true)]
    [InlineData(" 51530 ", "51530", true)]
    [InlineData("51530", "41100", false)]
    [InlineData(null, "51530", false)]
    [InlineData("51530", null, false)]
    [InlineData("", "", false)]
    public void IsAcknowledgedCredit_ตรงรหัสเท่านั้น(string? credit, string? ack, bool expected)
        => Assert.Equal(expected, OcrAccountPlacement.IsAcknowledgedCredit(credit, ack));
}
