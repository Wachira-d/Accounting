using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ฝ่ายค้านรอบ 193 รอบสอง W2-C3/W2-C4 — ด่าน "เจ้าของเท่านั้น" ตัวเดียว (<see cref="OwnerGateDecision"/>) ที่
/// <c>[RequireOwner]</c> (คีย์ลับ gateway · สลับ live · สิทธิ์ดูเอกสารลับ) และ <c>SubscriptionController</c> ใช้ร่วมกัน
///
/// <para>ครึ่งแรก = ถูกปฏิเสธ (คีย์แม้ถือตัวตนเจ้าของ · บทบาทอื่นทุกบทบาท · ไม่ใช่สมาชิก) · ครึ่งหลัง (ทิศตรงข้าม) = เจ้าของที่ล็อกอิน
/// และผู้ดูแลแพลตฟอร์มยังทำได้ — ไม่งั้นด่านนี้ปิดงานเจ้าของทั้งระบบ</para>
/// <para>การต่อสายเข้า controller ล็อกด้วย <c>tools/owner_action_wiring_check.py</c> (เทสต์นี้ไม่ได้ผ่าน MVC จริง)</para>
/// </summary>
public class OwnerGateDecisionTests
{
    // ════════ ครึ่งแรก: ปฏิเสธ ════════

    [Fact]
    public void คำขอจากคีย์_ปฏิเสธก่อนดูบทบาท_แม้ตัวตนเป็นเจ้าของ()
    {
        Assert.Equal(OwnerGateDecision.Outcome.DenyApiKey, OwnerGateDecision.Decide(true, false, UserRole.Owner));
        Assert.Equal(OwnerGateDecision.Outcome.DenyApiKey, OwnerGateDecision.Decide(true, true, UserRole.Owner));
    }

    [Theory]
    [InlineData(UserRole.Accountant)]
    [InlineData(UserRole.Viewer)]
    public void บทบาทที่ไม่ใช่เจ้าของ_ถูกปฏิเสธ(UserRole role)
    {
        Assert.Equal(OwnerGateDecision.Outcome.DenyNotOwner, OwnerGateDecision.Decide(false, false, role));
    }

    [Fact]
    public void ไม่ใช่สมาชิกของบริษัท_ไม่รู้บทบาท_ถูกปฏิเสธ_ไม่ใช่ผ่าน()
    {
        Assert.Equal(OwnerGateDecision.Outcome.DenyNotOwner, OwnerGateDecision.Decide(false, false, null));
    }

    [Fact]
    public void ข้อความปฏิเสธบอกว่าทำอะไรไม่ได้_เพราะอะไร_และขอใคร()
    {
        var msg = OwnerGateDecision.NotOwnerMessage("สลับโหมดรับชำระเงิน", "โหมดใช้งานจริงรับเงินของลูกค้าจริง");
        Assert.Contains("สลับโหมดรับชำระเงิน", msg);
        Assert.Contains("เจ้าของบริษัท", msg);
        Assert.Contains("เงินของลูกค้าจริง", msg);
    }

    // ════════ ครึ่งหลัง: ทิศตรงข้าม ════════

    [Theory]
    [InlineData(UserRole.Owner)]
    [InlineData(UserRole.SystemAdmin)]
    public void เจ้าของและผู้ดูแลระบบของบริษัท_ที่ล็อกอิน_ผ่าน(UserRole role)
    {
        Assert.Equal(OwnerGateDecision.Outcome.Allow, OwnerGateDecision.Decide(false, false, role));
    }

    [Fact]
    public void ผู้ดูแลแพลตฟอร์ม_ผ่านโดยไม่ต้องเป็นสมาชิก()
    {
        Assert.Equal(OwnerGateDecision.Outcome.Allow, OwnerGateDecision.Decide(false, true, null));
    }
}

/// <summary>W2-P6 — ข้อความของทางอ่านเอกสารลับต้องเป็นชุดเดียวกับที่หน้าเอกสารแสดง (<see cref="SensitivityAccess"/>)</summary>
public class SensitivityAccessTests
{
    [Fact]
    public void เอกสารปกติ_ไม่ต้องตรวจ_เอกสารลับทุกชนิด_ต้องตรวจ()
    {
        Assert.False(SensitivityAccess.NeedsCheck(SensitivityKind.None));
        foreach (var k in new[] { SensitivityKind.Payroll, SensitivityKind.ExecutivePay, SensitivityKind.HrPersonal, SensitivityKind.Confidential })
            Assert.True(SensitivityAccess.NeedsCheck(k));
    }

    [Fact]
    public void ข้อความปฏิเสธอ้างเหตุผลเดียวกับหน้าเอกสาร_และบอกทางไปต่อ()
    {
        var msg = SensitivityAccess.DeniedMessage(SensitivityKind.Payroll, "ส่งอีเมล");
        Assert.Contains(SensitivityAccess.RedactReason(SensitivityKind.Payroll), msg);
        Assert.Contains("ส่งอีเมล", msg);
        Assert.Contains("เจ้าของบริษัท", msg);
    }
}
