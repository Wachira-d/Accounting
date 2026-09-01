using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>ด่านสถานะบัญชีตอนเข้าสู่ระบบ
///
/// บั๊กจริงที่เทสต์ชุดนี้กัน: `User.Status` ถูกตั้งเป็น Inactive จาก 4 ที่ (รวม
/// `PayrollService` ที่ตั้งให้อัตโนมัติเมื่อ**พนักงานลาออก**) แต่ไม่มีทางเข้าไหน
/// อ่านค่านี้เลย ⇒ พนักงานที่ลาออกแล้วยังล็อกอินได้ทั้งรหัสผ่านและ SSO
/// </summary>
public class UserLoginPolicyTests
{
    [Fact]
    public void บัญชีปกติเข้าได้()
    {
        var (can, reason) = UserLoginPolicy.Evaluate(UserStatus.Active, false);
        Assert.True(can);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(UserStatus.Inactive)]
    [InlineData(UserStatus.Suspended)]
    public void บัญชีที่ถูกปิดหรือระงับ_เข้าไม่ได้ทั้งรหัสผ่านและ_SSO(UserStatus status)
    {
        var viaPassword = UserLoginPolicy.Evaluate(status, viaVerifiedSso: false);
        var viaSso = UserLoginPolicy.Evaluate(status, viaVerifiedSso: true);
        Assert.False(viaPassword.Can);
        Assert.False(viaSso.Can);
        // เหตุผลต้องเป็นข้อความที่เอาไปโชว์ผู้ใช้ได้ ไม่ใช่ bool เปล่า
        Assert.False(string.IsNullOrWhiteSpace(viaPassword.Reason));
        Assert.False(string.IsNullOrWhiteSpace(viaSso.Reason));
    }

    [Fact]
    public void รอยืนยันอีเมล_เข้าด้วยรหัสผ่านไม่ได้_แต่ต้องบอกทางไปต่อ()
    {
        var (can, reason) = UserLoginPolicy.Evaluate(UserStatus.PendingVerification, false);
        Assert.False(can);
        // "สถานะปลายทางที่ผู้ใช้ไปต่อไม่ได้ = ฟีเจอร์ที่ยังไม่จบ"
        Assert.Contains("ลืมรหัสผ่าน", reason!);
    }

    [Fact]
    public void รอยืนยันอีเมล_แต่เข้าด้วย_SSO_ที่ยืนยันอีเมลแล้ว_ผ่านและเลื่อนเป็น_Active()
    {
        var (can, _) = UserLoginPolicy.Evaluate(UserStatus.PendingVerification, viaVerifiedSso: true);
        Assert.True(can);
        Assert.True(UserLoginPolicy.ActivatesPendingVerification(UserStatus.PendingVerification, true));
        Assert.False(UserLoginPolicy.ActivatesPendingVerification(UserStatus.PendingVerification, false));
        // บัญชีที่ Active อยู่แล้วต้องไม่ถูกแตะ
        Assert.False(UserLoginPolicy.ActivatesPendingVerification(UserStatus.Active, true));
    }

    [Fact]
    public void บัญชีที่ไม่มีรหัสผ่าน_ต้องบอกชื่อ_provider_ที่ผูกไว้()
    {
        var msg = UserLoginPolicy.PasswordLoginUnavailableMessage("Line");
        Assert.Contains("LINE", msg);           // ใช้ชื่อแบรนด์ที่ผู้ใช้เห็นบนปุ่ม
        Assert.Contains("ลืมรหัสผ่าน", msg);     // ทางออกที่สอง
    }

    [Fact]
    public void บัญชีที่ไม่มีรหัสผ่านและไม่มี_provider_ก็ยังต้องมีทางไปต่อ()
    {
        var msg = UserLoginPolicy.PasswordLoginUnavailableMessage(null);
        Assert.Contains("ลืมรหัสผ่าน", msg);
    }
}
