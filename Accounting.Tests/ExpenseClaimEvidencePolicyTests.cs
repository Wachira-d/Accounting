using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ผู้ยื่นใบเบิกแนบ/ถอดหลักฐานเองได้ถึงเมื่อไร (ฝ่ายค้านรอบ 193 · P1 → ทีม S2)
/// ครึ่งที่ 1: หลังอนุมัติ (Approved · Paid · Rejected · Voided) ผู้ยื่นทำเองไม่ได้ — กันสลับใบเสร็จหลังผู้อนุมัติตรวจแล้ว ·
/// ข้อความบอกทางไปต่อ (ขอผู้อนุมัติ + ชื่อคีย์) · สถานะที่ไม่รู้จัก = ล็อก
/// ครึ่งที่ 2 (ทิศตรงข้าม): ร่าง และรออนุมัติ ผู้ยื่นยังแนบ/ถอดเองได้ — flow §65 ทวิ "ไม่มีใบเสร็จ" บังคับให้แนบหลักฐาน
/// ก่อนส่งอนุมัติ และผู้อนุมัติมักขอหลักฐานเพิ่มระหว่างรอ
/// </summary>
public class ExpenseClaimEvidencePolicyTests
{
    [Theory]
    [InlineData(ExpenseClaimStatus.Approved)]
    [InlineData(ExpenseClaimStatus.Paid)]
    [InlineData(ExpenseClaimStatus.Rejected)]
    [InlineData(ExpenseClaimStatus.Voided)]
    public void หลังอนุมัติ_ผู้ยื่นแนบหรือถอดหลักฐานเองไม่ได้(ExpenseClaimStatus status)
        => Assert.False(ExpenseClaimEvidencePolicy.OwnerMayChange(status));

    [Fact]
    public void สถานะที่ไม่รู้จัก_ล็อกไว้ก่อน()
        => Assert.False(ExpenseClaimEvidencePolicy.OwnerMayChange((ExpenseClaimStatus)99));

    [Fact]
    public void ข้อความล็อก_บอกเหตุผลและให้ขอผู้อนุมัติพร้อมชื่อคีย์()
    {
        var msg = ExpenseClaimEvidencePolicy.LockedMessage(ExpenseClaimStatus.Paid, "ลบไฟล์แนบ");
        Assert.Contains("จ่ายเงินแล้ว", msg);
        Assert.Contains("ลบไฟล์แนบ", msg);
        Assert.Contains("ขอผู้อนุมัติ", msg);
        Assert.Contains("Expense.Approve", msg);
        Assert.Contains("HR.Admin", msg);
        Assert.DoesNotContain("perm:", msg);
    }

    // ── ครึ่งที่ 2 ──

    [Theory]
    [InlineData(ExpenseClaimStatus.Draft)]
    [InlineData(ExpenseClaimStatus.Submitted)]
    public void ก่อนอนุมัติ_ผู้ยื่นยังแนบหรือถอดหลักฐานเองได้(ExpenseClaimStatus status)
        => Assert.True(ExpenseClaimEvidencePolicy.OwnerMayChange(status));
}
