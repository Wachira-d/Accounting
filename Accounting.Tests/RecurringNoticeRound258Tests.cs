using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 258 (B-M1 · B-M2 · B-M5 · A-M1) — รายการประจำ (pure · สองทิศ):
/// ผู้รับแจ้งเตือน = ผู้สร้างที่ยังเป็นสมาชิก · ข้อความ "ค้างเป็นร่าง" · แผงความพร้อมรู้จักผู้อนุมัติหมดสิทธิ์ ·
/// กฎเมื่ออนุมัติที่ส่งทาง LINE นับว่าส่งแล้ว · LINE ออกแล้วไม่เตือน "ไม่มีอีเมล"
/// </summary>
public class RecurringNoticeRound258Tests
{
    private static readonly Guid Creator = Guid.Parse("25800000-0000-0000-0000-0000000000c1");

    // ───── B-M5 ─────

    [Fact]
    public void ผู้สร้างยังเป็นสมาชิก_ได้รับแบบระบุตัว()
        => Assert.Equal(Creator, RecurringStaffNotice.Recipient(Creator.ToString(), creatorIsActiveMember: true));

    [Fact]
    public void ทิศตรงข้าม_ผู้สร้างออกจากบริษัทแล้ว_ไม่ระบุตัว_ตกไปผู้รับตามตารางของบริษัท()
        => Assert.Null(RecurringStaffNotice.Recipient(Creator.ToString(), creatorIsActiveMember: false));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("System")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void ผู้สร้างไม่ใช่ผู้ใช้_ไม่ระบุตัว(string? createdBy)
    {
        Assert.Null(RecurringStaffNotice.CreatorId(createdBy));
        Assert.Null(RecurringStaffNotice.Recipient(createdBy, creatorIsActiveMember: true));
    }

    // ───── B-M1 ─────

    [Fact]
    public void ข้อความค้างร่าง_บอกชื่อรายการ_เลขใบ_เหตุ_และทางไปต่อ()
    {
        var title = RecurringStaffNotice.StuckDraftTitle("ค่าเช่ารายเดือน", "DRAFT-abc");
        Assert.Contains("ค่าเช่ารายเดือน", title, StringComparison.Ordinal);
        Assert.Contains("ค้างเป็นร่าง", title, StringComparison.Ordinal);
        Assert.Contains("DRAFT-abc", title, StringComparison.Ordinal);
        var msg = RecurringStaffNotice.StuckDraftMessage("ลูกค้าเกินวงเงินเครดิต");
        Assert.StartsWith("ลูกค้าเกินวงเงินเครดิต", msg, StringComparison.Ordinal);
        Assert.Contains("อนุมัติเอง", msg, StringComparison.Ordinal);
        // ไม่มีเหตุ ⇒ ยังบอกสิ่งที่เกิด ไม่ปล่อยว่าง
        Assert.Contains("อนุมัติเอกสารของรอบนี้ไม่ได้", RecurringStaffNotice.StuckDraftMessage(null), StringComparison.Ordinal);
        Assert.DoesNotContain("()", RecurringStaffNotice.StuckDraftTitle("ก", null), StringComparison.Ordinal);
    }

    // ───── B-M2 ─────

    private static AutopilotReadiness.Facts Ready() => new()
    {
        DocumentType = DocumentType.Invoice,
        AutoApprove = true,
        ContactId = Guid.NewGuid(),
        ContactUsable = true,
        ContactEmail = "ap@customer.co.th",
        CompanySenderVerified = true,
        SaleSide = true,
        ApprovalWarnings = Array.Empty<string>(),
    };

    [Fact]
    public void แผงความพร้อม_ผู้รับผิดชอบการอนุมัติหมดสิทธิ์_ขึ้นข้อล้มพร้อมเหตุและทางไปต่อ_ไม่บอกว่าอนุมัติจะผ่าน()
    {
        var report = AutopilotReadiness.Evaluate(Ready() with { AutoApproverRefusal = RecurringApprovePolicyLoader.AutoApproverGoneReason });
        var item = Assert.Single(report.Items, i => i.Code == "APPROVER-GONE");
        Assert.Equal(AutopilotReadiness.Fail, item.Verdict);
        Assert.Equal(RecurringApprovePolicyLoader.AutoApproverGoneReason, item.Why);
        Assert.Equal(AutopilotReadiness.PolicyStepUrl, item.FixUrl);
        Assert.DoesNotContain(report.Items, i => i.Code == "APPROVE" && i.Verdict == AutopilotReadiness.Ok);
        Assert.True(report.FailCount >= 1);
    }

    [Fact]
    public void แผงความพร้อม_ทิศตรงข้าม_ผู้อนุมัติยังมีสิทธิ์_ไม่ขึ้นข้อ_และรายการที่ไม่ได้เปิดอนุมัติอัตโนมัติไม่ขึ้นข้อ()
    {
        var ok = AutopilotReadiness.Evaluate(Ready());
        Assert.DoesNotContain(ok.Items, i => i.Code == "APPROVER-GONE");
        Assert.Contains(ok.Items, i => i.Code == "APPROVE" && i.Verdict == AutopilotReadiness.Ok);
        var manual = AutopilotReadiness.Evaluate(Ready() with { AutoApprove = false, AutoApproverRefusal = RecurringApprovePolicyLoader.AutoApproverGoneReason });
        Assert.DoesNotContain(manual.Items, i => i.Code == "APPROVER-GONE");
    }

    // ───── A-M1 ─────

    [Theory]
    [InlineData(false, false, false)]   // ไม่มีแถวใดของกฎเมื่ออนุมัติ ⇒ เส้นรายการประจำเดินต่อ (ลูกค้าต้องได้หนึ่งฉบับ)
    [InlineData(true, false, true)]     // อีเมลเข้าคิวแล้ว (เดิม)
    [InlineData(false, true, true)]     // กฎติ๊ก LINE อย่างเดียว: แถว LINE เข้าคิวแล้ว ⇒ ไม่ส่งซ้ำจากกฎรายการประจำ (บั๊กเดิม: ส่งซ้ำ)
    [InlineData(true, true, true)]
    public void กฎเมื่ออนุมัติส่งใบนี้แล้ว_นับทั้งอีเมลและLINE(bool emailRow, bool lineRow, bool expected)
    {
        Assert.Equal(expected, EmailEnqueueOutcome.ApprovedRuleDelivered(emailRow, lineRow));
        Assert.Equal(expected, EmailEnqueueOutcome.SkipForApprovedRule(true, true, EmailEnqueueOutcome.ApprovedRuleDelivered(emailRow, lineRow)));
    }

    [Theory]
    [InlineData(false, false, true)]    // ไม่มีอีเมล ไม่มี LINE ⇒ เตือน (เดิม)
    [InlineData(false, true, false)]    // ไม่มีอีเมล แต่ LINE ออกแล้ว ⇒ ไม่เตือน (บั๊กเดิม: เตือนผิด)
    [InlineData(true, false, false)]    // มีอีเมล ⇒ ไม่เตือน
    [InlineData(true, true, false)]
    public void เตือนไม่มีอีเมล_เฉพาะเมื่อไม่ได้ออกทางใดเลย(bool hasEmail, bool lineQueued, bool expected)
        => Assert.Equal(expected, EmailEnqueueOutcome.WarnNoContactEmail(hasEmail, lineQueued));
}
