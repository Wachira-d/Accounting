using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ด่านสิทธิ์ใบเบิกค่าใช้จ่าย (ฝ่ายค้านรอบสอง 193 · R2-C2 P0 → ทีม S2)
/// ครึ่งที่ 1 (ช่องที่ปิด): พนักงานที่ไม่มีคีย์อนุมัติ/จ่ายไม่ได้ · ผู้ยื่นอนุมัติ/ปฏิเสธ/จ่ายใบตัวเองไม่ได้แม้ถือคีย์ ·
/// จ่ายต้องอนุมัติใบสำคัญจ่ายได้ด้วย · ผู้ยื่นยกเลิกใบที่อนุมัติแล้วเองไม่ได้ · ดูใบของคนอื่นต้องมีคีย์
/// ครึ่งที่ 2 (ทิศตรงข้าม): ผู้ยื่นยังดู/แก้/ส่ง/ถอนใบตัวเองได้ · ผู้อนุมัติ/ผู้จ่ายที่ไม่ใช่ผู้ยื่นทำได้ ·
/// เจ้าของกิจการคนเดียว (ไม่เปิดสวิตช์แยกหน้าที่) อนุมัติใบตัวเองได้
/// (จุดเรียกใน service/มือถือ ล็อกด้วย tools/required_call_site_check.py · controller ด้วย write_permission_gate_check)
/// </summary>
public class ExpenseClaimActionPolicyTests
{
    private static ExpenseClaimActionDenial? D(ExpenseClaimAction a, bool owner, ExpenseClaimStatus st,
        bool key, bool selfOk = false, bool pv = true)
        => ExpenseClaimActionPolicy.Decide(a, owner, st, key, selfOk, pv);

    [Theory]
    [InlineData(ExpenseClaimAction.Approve)]
    [InlineData(ExpenseClaimAction.Reject)]
    [InlineData(ExpenseClaimAction.Pay)]
    public void ผู้ยื่นตัดสินใบตัวเองไม่ได้_แม้ถือคีย์(ExpenseClaimAction a)
    {
        var d = D(a, owner: true, ExpenseClaimStatus.Submitted, key: true);
        Assert.NotNull(d);
        Assert.Equal(403, d!.Value.Status);
        Assert.Equal("EXPENSE-SOD-SELF", d.Value.RuleCode);
    }

    [Theory]
    [InlineData(ExpenseClaimAction.Approve)]
    [InlineData(ExpenseClaimAction.Reject)]
    [InlineData(ExpenseClaimAction.Pay)]
    [InlineData(ExpenseClaimAction.Void)]
    [InlineData(ExpenseClaimAction.View)]
    [InlineData(ExpenseClaimAction.Edit)]
    public void คนที่ไม่ใช่ผู้ยื่นและไม่มีคีย์_ทำไม่ได้(ExpenseClaimAction a)
        => Assert.Equal("EXPENSE-PERM", D(a, owner: false, ExpenseClaimStatus.Submitted, key: false)!.Value.RuleCode);

    [Fact]
    public void จ่ายต้องอนุมัติใบสำคัญจ่ายได้ด้วย()
        => Assert.Equal("EXPENSE-PERM-PV",
            D(ExpenseClaimAction.Pay, owner: false, ExpenseClaimStatus.Approved, key: true, pv: false)!.Value.RuleCode);

    [Fact]
    public void ผู้ยื่นยกเลิกใบที่อนุมัติแล้วเองไม่ได้()
        => Assert.NotNull(D(ExpenseClaimAction.Void, owner: true, ExpenseClaimStatus.Approved, key: false));

    [Fact]
    public void คีย์จ่ายและคีย์อนุมัติแยกกัน()
    {
        Assert.Equal(new[] { PermissionKeys.ExpensePay }, ExpenseClaimActionPolicy.ReviewerKeys(ExpenseClaimAction.Pay));
        Assert.DoesNotContain(PermissionKeys.ExpensePay, ExpenseClaimActionPolicy.ReviewerKeys(ExpenseClaimAction.Approve));
    }

    [Fact]
    public void พนักงานที่ถือคีย์_ไม่ได้ข้อยกเว้นเจ้าของกิจการ()
    {
        Assert.False(ExpenseClaimActionPolicy.SelfDecisionAllowed(actorIsCompanyOwner: false, sodBlockSelfApproval: false));
        Assert.False(ExpenseClaimActionPolicy.SelfDecisionAllowed(actorIsCompanyOwner: true, sodBlockSelfApproval: true));
    }

    // ── ครึ่งที่ 2 ──

    [Theory]
    [InlineData(ExpenseClaimAction.View, ExpenseClaimStatus.Paid)]
    [InlineData(ExpenseClaimAction.Edit, ExpenseClaimStatus.Draft)]
    [InlineData(ExpenseClaimAction.Submit, ExpenseClaimStatus.Draft)]
    [InlineData(ExpenseClaimAction.Void, ExpenseClaimStatus.Draft)]
    [InlineData(ExpenseClaimAction.Void, ExpenseClaimStatus.Submitted)]
    public void ทิศตรงข้าม_ผู้ยื่นทำงานของตัวเองได้โดยไม่ต้องมีคีย์(ExpenseClaimAction a, ExpenseClaimStatus st)
        => Assert.Null(D(a, owner: true, st, key: false));

    [Theory]
    [InlineData(ExpenseClaimAction.Approve, ExpenseClaimStatus.Submitted)]
    [InlineData(ExpenseClaimAction.Reject, ExpenseClaimStatus.Submitted)]
    [InlineData(ExpenseClaimAction.Pay, ExpenseClaimStatus.Approved)]
    [InlineData(ExpenseClaimAction.Void, ExpenseClaimStatus.Approved)]
    [InlineData(ExpenseClaimAction.View, ExpenseClaimStatus.Submitted)]
    public void ทิศตรงข้าม_ผู้ตรวจที่ไม่ใช่ผู้ยื่นและถือคีย์_ทำได้(ExpenseClaimAction a, ExpenseClaimStatus st)
        => Assert.Null(D(a, owner: false, st, key: true));

    [Fact]
    public void ทิศตรงข้าม_เจ้าของกิจการคนเดียว_อนุมัติและจ่ายใบตัวเองได้()
    {
        var selfOk = ExpenseClaimActionPolicy.SelfDecisionAllowed(actorIsCompanyOwner: true, sodBlockSelfApproval: false);
        Assert.True(selfOk);
        Assert.Null(D(ExpenseClaimAction.Approve, owner: true, ExpenseClaimStatus.Submitted, key: true, selfOk: selfOk));
        Assert.Null(D(ExpenseClaimAction.Pay, owner: true, ExpenseClaimStatus.Approved, key: true, selfOk: selfOk));
    }

    [Fact]
    public void ผู้ดูรายการได้_ดูใบรายใบได้ด้วย_ชุดคีย์เดียวกับหน้ารายการ()
    {
        var view = ExpenseClaimActionPolicy.ReviewerKeys(ExpenseClaimAction.View);
        Assert.Contains(PermissionKeys.HrAdmin, view);
        Assert.Contains(PermissionKeys.ExpenseApprove, view);
        Assert.Contains(PermissionKeys.ExpensePay, view);
    }
}
