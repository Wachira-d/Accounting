using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ผู้ยื่นใบเบิกแนบ/ถอดหลักฐานเองได้ถึงเมื่อไร + ด่าน §65 ทวิ ตอนอนุมัติ (ฝ่ายค้านรอบ 193 · P1 / S2-P1 / S2-P2 → ทีม S2)
/// ครึ่งที่ 1: หลังอนุมัติผู้ยื่นทำเองไม่ได้ · ช่วงรออนุมัติถอดไม่ได้ (ถอดหลังผ่านด่านตอนส่ง) · ผู้ยื่นที่ถือคีย์ผู้ตรวจเองก็ไม่ได้ (SoD) ·
/// ใบไม่มีใบเสร็จที่หลักฐานหายไปแล้วอนุมัติไม่ได้ · สถานะที่ไม่รู้จัก = ล็อก
/// ครึ่งที่ 2 (ทิศตรงข้าม): ร่างแนบ/ถอดได้ · รออนุมัติยังเพิ่มหลักฐานได้ · ผู้ตรวจคนอื่นใช้คีย์ได้ · ใบมีใบเสร็จ/มีหลักฐานอนุมัติได้
/// </summary>
public class ExpenseClaimEvidencePolicyTests
{
    [Theory]
    [InlineData(ExpenseClaimStatus.Approved)]
    [InlineData(ExpenseClaimStatus.Paid)]
    [InlineData(ExpenseClaimStatus.Rejected)]
    [InlineData(ExpenseClaimStatus.Voided)]
    public void หลังอนุมัติ_ผู้ยื่นแนบหรือถอดหลักฐานเองไม่ได้(ExpenseClaimStatus status)
    {
        Assert.False(ExpenseClaimEvidencePolicy.OwnerMayChange(status, isRemoval: false));
        Assert.False(ExpenseClaimEvidencePolicy.OwnerMayChange(status, isRemoval: true));
    }

    [Fact]
    public void รออนุมัติ_ผู้ยื่นถอดหลักฐานไม่ได้()
        // S2-P1: เดิม Submitted ถอดได้ ⇒ ผ่านด่าน §65 ทวิ ตอนส่งแล้วถอดทิ้ง ใบถูกอนุมัติโดยไม่มีหลักฐาน
        => Assert.False(ExpenseClaimEvidencePolicy.OwnerMayChange(ExpenseClaimStatus.Submitted, isRemoval: true));

    [Fact]
    public void ผู้ยื่นที่ถือคีย์ผู้ตรวจเอง_ใช้คีย์นั้นแก้ใบตัวเองไม่ได้()
        // S2-P2: "อนุมัติเอง แก้เอง"
        => Assert.False(ExpenseClaimEvidencePolicy.ReviewerKeyApplies(isClaimOwner: true));

    [Fact]
    public void สถานะที่ไม่รู้จัก_ล็อกไว้ก่อน()
        => Assert.False(ExpenseClaimEvidencePolicy.OwnerMayChange((ExpenseClaimStatus)99, isRemoval: false));

    [Fact]
    public void ใบไม่มีใบเสร็จที่หลักฐานถูกถอดหมด_อนุมัติไม่ได้_และบอกทางไปต่อ()
    {
        var msg = ExpenseClaimEvidencePolicy.MissingEvidenceMessage(noReceipt: true, attachmentCount: 0, atApproval: true);
        Assert.NotNull(msg);
        Assert.Contains("อนุมัติไม่ได้", msg);
        Assert.Contains("ปฏิเสธ", msg);   // ทางไปต่อของผู้อนุมัติ
        Assert.NotNull(ExpenseClaimEvidencePolicy.MissingEvidenceMessage(true, 0, atApproval: false));
    }

    [Fact]
    public void ข้อความล็อก_บอกเหตุผลและให้ขอผู้อนุมัติคนอื่นพร้อมชื่อคีย์()
    {
        var msg = ExpenseClaimEvidencePolicy.LockedMessage(ExpenseClaimStatus.Paid, "ลบไฟล์แนบ");
        Assert.Contains("จ่ายเงินแล้ว", msg);
        Assert.Contains("ลบไฟล์แนบ", msg);
        Assert.Contains("ขอผู้อนุมัติคนอื่น", msg);
        Assert.Contains("Expense.Approve", msg);
        Assert.Contains("HR.Admin", msg);
        Assert.DoesNotContain("perm:", msg);
        Assert.Contains("ถอดไม่ได้", ExpenseClaimEvidencePolicy.LockedMessage(ExpenseClaimStatus.Submitted, "ลบไฟล์แนบ"));
    }

    // ── ครึ่งที่ 2 ──

    [Fact]
    public void ร่าง_ผู้ยื่นแนบและถอดหลักฐานได้()
    {
        Assert.True(ExpenseClaimEvidencePolicy.OwnerMayChange(ExpenseClaimStatus.Draft, isRemoval: false));
        Assert.True(ExpenseClaimEvidencePolicy.OwnerMayChange(ExpenseClaimStatus.Draft, isRemoval: true));
    }

    [Fact]
    public void รออนุมัติ_ผู้ยื่นยังเพิ่มหลักฐานที่ผู้อนุมัติขอได้()
        => Assert.True(ExpenseClaimEvidencePolicy.OwnerMayChange(ExpenseClaimStatus.Submitted, isRemoval: false));

    [Fact]
    public void ผู้ตรวจคนอื่น_ยังใช้คีย์แก้หลักฐานให้ได้()
        => Assert.True(ExpenseClaimEvidencePolicy.ReviewerKeyApplies(isClaimOwner: false));

    [Theory]
    [InlineData(false, 0)]   // มีใบเสร็จ — ไม่ต้องมีไฟล์ตามด่านนี้
    [InlineData(true, 1)]    // ไม่มีใบเสร็จแต่มีหลักฐาน
    [InlineData(true, 3)]
    public void ใบที่มีใบเสร็จหรือมีหลักฐาน_ผ่านด่านอนุมัติ(bool noReceipt, int count)
        => Assert.Null(ExpenseClaimEvidencePolicy.MissingEvidenceMessage(noReceipt, count, atApproval: true));
}
