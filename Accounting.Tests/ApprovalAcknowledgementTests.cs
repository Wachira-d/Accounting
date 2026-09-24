using Accounting.Helpers;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 193 (ฝ่ายค้าน C5/C6) — <see cref="ApprovalAcknowledgement"/>: ห้ามระบบ/workflow/API ประทับ "ผู้ใช้รับทราบคำเตือน" แทนคน
///
/// <para><b>ครึ่งที่ 1</b> (ที่เคยผิด): workflow/ลายเซ็นส่ง ack แทนผู้ใช้ ⇒ ตอนนี้ [Σ-GAP] ระบบส่งผ่านไม่ได้ · API ผ่านได้เฉพาะ [Σ-GAP]
/// (คำตัดสินข้อ 12) คำเตือนอื่นยังหยุด · หมายเหตุ/รหัสกฎบอกว่า "ระบบ/API ส่งผ่าน" ไม่ใช่ "ยืนยันโดย"</para>
/// <para><b>ครึ่งที่ 2</b> (ห้ามแตะ): ผู้ใช้กดรับทราบเอง = ผ่านทุกข้อ รหัสกฎเดิม APPROVE-ACK-WARNINGS · ไม่มีใครรับทราบ = หยุดทุกข้อ ·
/// ไม่มีคำเตือน = ไม่มีอะไรหยุด</para>
/// </summary>
public class ApprovalAcknowledgementTests
{
    private static readonly string Gap = OcrApprovalGapWarning.Prefix + ": ผลรวมรายการ 24,110.00 ≠ ยอดรวมทั้งสิ้น 23,812.25";
    private const string Other = "ใบกำกับภาษีซื้อเกิน 6 เดือน (§82/3) — ต้องระบุเหตุผล";
    private static readonly System.DateTime At = new(2026, 9, 24, 3, 0, 0, System.DateTimeKind.Utc);

    [Fact]
    public void ระบบworkflowส่งผ่านคำเตือนยอดสแกนไม่ได้_ต้องมีคนรับทราบ()
    {
        var left = ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.SystemWorkflow, new[] { Gap, Other });
        Assert.Equal(new[] { Gap }, left);
    }

    [Fact]
    public void APIผ่านได้เฉพาะคำเตือนยอดสแกน_คำเตือนอื่นยังหยุด()
    {
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, new[] { Gap }));
        Assert.Equal(new[] { Other }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.ApiClient, new[] { Gap, Other }));
    }

    [Fact]
    public void ร่องรอยของระบบและAPI_ไม่อ้างว่าผู้ใช้รับทราบ()
    {
        var sys = ApprovalAcknowledgement.Note(ApprovalAckSource.SystemWorkflow, new[] { Other }, "external:คุณสมชาย", At);
        Assert.Contains("ไม่มีผู้ใช้เห็น", sys);
        Assert.DoesNotContain("ยืนยันโดย", sys);
        Assert.Equal(ApprovalAcknowledgement.SystemRuleCode, ApprovalAcknowledgement.RuleCode(ApprovalAckSource.SystemWorkflow));
        Assert.False(ApprovalAcknowledgement.AcknowledgedByPerson(ApprovalAckSource.SystemWorkflow));

        var api = ApprovalAcknowledgement.Note(ApprovalAckSource.ApiClient, new[] { Gap }, "api:v1", At);
        Assert.Contains("API", api);
        Assert.DoesNotContain("ยืนยันโดย", api);
        Assert.Equal(ApprovalAcknowledgement.ApiRuleCode, ApprovalAcknowledgement.RuleCode(ApprovalAckSource.ApiClient));
        Assert.False(ApprovalAcknowledgement.AcknowledgedByPerson(ApprovalAckSource.ApiClient));
    }

    // ── ครึ่งที่ 2 (ห้ามแตะ) ────────────────────────────────────────────────

    [Fact]
    public void ผู้ใช้กดรับทราบเอง_ผ่านทุกข้อ_รหัสกฎเดิม()
    {
        Assert.Empty(ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.User, new[] { Gap, Other }));
        Assert.Equal("APPROVE-ACK-WARNINGS", ApprovalAcknowledgement.RuleCode(ApprovalAckSource.User));
        Assert.True(ApprovalAcknowledgement.AcknowledgedByPerson(ApprovalAckSource.User));
        var note = ApprovalAcknowledgement.Note(ApprovalAckSource.User, new[] { Other }, "u-1", At);
        Assert.Contains("รับทราบคำเตือนตอนอนุมัติ", note);
        Assert.Contains("ยืนยันโดย u-1 เมื่อ 2026-09-24 10:00", note);
    }

    [Fact]
    public void ไม่มีใครรับทราบ_หยุดทุกข้อ_ไม่มีคำเตือนไม่มีอะไรหยุด()
    {
        Assert.Equal(new[] { Gap, Other }, ApprovalAcknowledgement.Unacknowledged(ApprovalAckSource.None, new[] { Gap, Other }));
        foreach (var src in new[] { ApprovalAckSource.None, ApprovalAckSource.SystemWorkflow, ApprovalAckSource.ApiClient })
            Assert.Empty(ApprovalAcknowledgement.Unacknowledged(src, System.Array.Empty<string>()));
    }
}
