using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// คำตัดสินข้อ 114 Q2 (ทีมตรวจงานค้าง 2026-10-08): ยกเลิกเงินทดรองแล้วลบใบสำคัญจ่าย<b>ร่าง</b>ตาม · ใบที่ออกแล้ว/รออนุมัติต้องจัดการที่หน้าเอกสารก่อน
/// · เดิมยกเลิกได้ทั้งที่ใบสำคัญจ่ายลงบัญชีแล้ว (ลูกหนี้เงินทดรองค้างไม่มีเจ้าของ) และไม่ตรวจสิทธิ์
/// </summary>
public class SalaryAdvanceVoidPolicyTests
{
    private static readonly Guid Pv = Guid.NewGuid();

    [Fact]
    public void ไม่มีใบสำคัญจ่าย_ยกเลิกได้()
        => Assert.Equal(SalaryAdvanceVoidOutcome.Allow, SalaryAdvanceVoidPolicy.Decide("Approved", null, null, null).Outcome);

    [Fact]
    public void ใบสำคัญจ่ายร่าง_ยกเลิกได้และลบใบร่างตาม()
        => Assert.Equal(SalaryAdvanceVoidOutcome.DeleteDraftVoucher,
            SalaryAdvanceVoidPolicy.Decide("Approved", Pv, DocumentStatus.Draft, "DRAFT-1").Outcome);

    [Theory]
    [InlineData(DocumentStatus.Voided)]
    [InlineData(DocumentStatus.Rejected)]
    public void ใบสำคัญจ่ายถูกยกเลิก_ปฏิเสธไปแล้ว_ยกเลิกได้_ไม่แตะใบนั้น(DocumentStatus s)
        => Assert.Equal(SalaryAdvanceVoidOutcome.Allow, SalaryAdvanceVoidPolicy.Decide("Approved", Pv, s, "PV-1").Outcome);

    [Fact]
    public void ทิศตรงข้าม_ใบสำคัญจ่ายอนุมัติแล้วที่หน้าเอกสาร_ยกเลิกรายการไม่ได้()
    {
        // เคสที่เป็นรูรั่ว: ผู้ใช้อนุมัติ PV เอง (รายการยังเป็น Approved) แล้วกดยกเลิกรายการ ⇒ PV ลงบัญชีแล้วแต่เงินเดือนไม่หักคืน
        var d = SalaryAdvanceVoidPolicy.Decide("Approved", Pv, DocumentStatus.Approved, "PV-2026-0009");
        Assert.Equal(SalaryAdvanceVoidOutcome.Block, d.Outcome);
        Assert.Equal(SalaryAdvanceVoidPolicy.RulePvIssued, d.RuleCode);
        Assert.Contains("PV-2026-0009", d.Message);
        Assert.Contains("หน้าเอกสาร", d.Message);   // ทางไปต่อ
    }

    [Fact]
    public void ทิศตรงข้าม_ใบสำคัญจ่ายรออนุมัติ_กันพร้อมทางไปต่อ()
    {
        var d = SalaryAdvanceVoidPolicy.Decide("Approved", Pv, DocumentStatus.WaitingApproval, "DRAFT-2");
        Assert.Equal(SalaryAdvanceVoidOutcome.Block, d.Outcome);
        Assert.Equal(SalaryAdvanceVoidPolicy.RulePvPending, d.RuleCode);
    }

    [Theory]
    [InlineData("Disbursed")]
    [InlineData("Cleared")]
    [InlineData("Voided")]
    public void ทิศตรงข้าม_จ่ายแล้ว_หักคืนครบ_ยกเลิกแล้ว_ยกเลิกไม่ได้(string status)
        => Assert.Equal(SalaryAdvanceVoidOutcome.Block, SalaryAdvanceVoidPolicy.Decide(status, Pv, DocumentStatus.Approved, "PV-1").Outcome);

    [Fact]
    public void สิทธิ์_ผู้อนุมัติและเจ้าของยกเลิกได้_ผู้ขอยกเลิกได้เฉพาะก่อนอนุมัติ()
    {
        Assert.True(SalaryAdvanceVoidPolicy.CanVoid("Approved", isOwnerOrAdmin: true, hasApproverPermission: false, isRequester: false));
        Assert.True(SalaryAdvanceVoidPolicy.CanVoid("Approved", false, hasApproverPermission: true, false));
        Assert.True(SalaryAdvanceVoidPolicy.CanVoid("Submitted", false, false, isRequester: true));
        Assert.True(SalaryAdvanceVoidPolicy.CanVoid("Draft", false, false, isRequester: true));
    }

    [Fact]
    public void ทิศตรงข้าม_สมาชิกทั่วไป_และผู้ขอหลังอนุมัติ_ยกเลิกไม่ได้()
    {
        Assert.False(SalaryAdvanceVoidPolicy.CanVoid("Submitted", false, false, false));
        Assert.False(SalaryAdvanceVoidPolicy.CanVoid("Approved", false, false, isRequester: true));
    }
}
