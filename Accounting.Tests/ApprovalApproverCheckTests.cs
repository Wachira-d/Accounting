using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>รอบ 2026-10-14 ทีม F4 (ID#9 · ID#11) — ตัวตัดสิน pure ของผู้อนุมัติที่ออกจากบริษัท · การโอน · คำสั่งมือถือ · สถานะสมาชิก
/// (เส้นจริงบนฐาน: Db/ApprovalWorkflowMembershipDbTests)</summary>
public class ApprovalApproverCheckTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), Req = Guid.NewGuid();

    [Fact]
    public void ส่งคำขอ_ผู้อนุมัติออกแล้ว_บอกขั้นและชื่อพร้อมทางไปต่อ_สมาชิกครบ_null()
    {
        var steps = new List<(int, Guid, string?)> { (1, A, "สมชาย"), (2, B, "สมหญิง") };
        var msg = ApprovalApproverCheck.DepartedStepsProblem("ซื้อเกินแสน", steps, new HashSet<Guid> { A });
        Assert.NotNull(msg);
        Assert.Contains("ขั้นที่ 2: สมหญิง", msg);
        Assert.DoesNotContain("สมชาย", msg);
        Assert.Contains("กฎการอนุมัติ", msg);
        Assert.DoesNotContain(B.ToString(), msg);
        Assert.Null(ApprovalApproverCheck.DepartedStepsProblem("ซื้อเกินแสน", steps, new HashSet<Guid> { A, B }));
    }

    [Fact]
    public void ป้ายค้าง_ไม่มีขั้นค้าง_null_มีขั้นค้าง_บอกโอนผู้อนุมัติ_ชื่อว่างไม่โชว์GUID()
    {
        Assert.Null(ApprovalApproverCheck.StalledNotice(new List<(int, string?)>()));
        var n = ApprovalApproverCheck.StalledNotice(new List<(int, string?)> { (1, null) });
        Assert.Contains("โอนผู้อนุมัติ", n);
        Assert.Contains("ผู้ใช้ที่ถูกลบ", n);
    }

    [Fact]
    public void โอนผู้อนุมัติ_ทุกเงื่อนไข()
    {
        // ผ่าน: คำขอรออยู่ · ขั้นยังไม่ได้ทำ · คนเดิมออกแล้ว · คนใหม่เป็นสมาชิก · ไม่ใช่ผู้ขอ · ยังไม่ได้อนุมัติขั้นอื่นของคำขอนี้
        Assert.Null(ApprovalApproverCheck.ReassignProblem(true, true, false, B, true, Req, false));
        Assert.NotNull(ApprovalApproverCheck.ReassignProblem(false, true, false, B, true, Req, false));     // คำขอปิดแล้ว
        Assert.NotNull(ApprovalApproverCheck.ReassignProblem(true, false, false, B, true, Req, false));     // ขั้นทำไปแล้ว/ไม่มี
        Assert.NotNull(ApprovalApproverCheck.ReassignProblem(true, true, true, B, true, Req, false));       // คนเดิมยังอยู่ ⇒ ไม่ใช่ช่องเปลี่ยนคนตามใจ
        Assert.NotNull(ApprovalApproverCheck.ReassignProblem(true, true, false, Guid.Empty, false, Req, false));
        Assert.NotNull(ApprovalApproverCheck.ReassignProblem(true, true, false, B, false, Req, false));     // คนใหม่ไม่ใช่สมาชิก
        Assert.Contains("ผู้ขอ", ApprovalApproverCheck.ReassignProblem(true, true, false, Req, true, Req, false)); // SoD
        Assert.Contains("สองขั้น", ApprovalApproverCheck.ReassignProblem(true, true, false, B, true, Req, true)); // SoD: คนเดียวผ่านสองขั้น
        // ฝ่ายค้านรอบ 2 P2(f): ผู้อนุมัติขั้นสุดท้ายยังเป็นสมาชิกแต่ไม่มีสิทธิ์อนุมัติเอกสาร ⇒ โอนได้ (คำขอที่ส่งแล้วไม่ตามการแก้กฎ)
        Assert.Null(ApprovalApproverCheck.ReassignProblem(true, true, true, B, true, Req, false, currentApproverLacksFinalPermission: true));
        // ทิศตรงข้าม: คนใหม่ของขั้นสุดท้ายไม่มีสิทธิ์ ⇒ ไม่ให้โอน (โอนแล้วค้างเหมือนเดิม)
        Assert.Contains("อนุมัติเอกสาร", ApprovalApproverCheck.ReassignProblem(true, true, true, B, true, Req, false,
            currentApproverLacksFinalPermission: true, newApproverLacksFinalPermission: true));
        // ข้อความ 403 ของขั้นสุดท้ายชี้ทางที่มีผลกับคำขอที่ส่งแล้ว (โอน) ไม่ใช่แก้กฎ
        Assert.Contains("โอนผู้อนุมัติ", ApprovalApproverCheck.FinalStepNoPermissionMessage("ใบแจ้งหนี้"));
    }

    [Theory]
    [InlineData("approve", ApprovalStatus.Approved)]
    [InlineData("APPROVE", ApprovalStatus.Approved)]
    [InlineData("reject", ApprovalStatus.Rejected)]
    public void คำสั่งมือถือ_รู้จัก(string action, ApprovalStatus expected)
        => Assert.Equal(expected, ApprovalApproverCheck.ParseMobileAction(action));

    [Theory]
    [InlineData("pending")]
    [InlineData("")]
    [InlineData(null)]
    public void คำสั่งมือถือ_ไม่รู้จัก_null(string? action) => Assert.Null(ApprovalApproverCheck.ParseMobileAction(action));

    [Fact]
    public void ข้อความไม่ใช่ผู้อนุมัติ_บอกขั้นและทางไปต่อ()
    {
        var m = ApprovalApproverCheck.NotAssignedMessage(2);
        Assert.Contains("ขั้นที่ 2", m);
        Assert.Contains("รออนุมัติ", m);
    }

    [Theory]
    [InlineData(UserStatus.Active, true)]
    [InlineData(UserStatus.PendingVerification, true)]
    [InlineData(UserStatus.Inactive, false)]
    [InlineData(UserStatus.Suspended, false)]
    public void สมาชิกที่ทำหน้าที่ได้_ตามสถานะบัญชี(UserStatus status, bool canAct)
        => Assert.Equal(canAct, CompanyMembership.CanAct(status));
}
