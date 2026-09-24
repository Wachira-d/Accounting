using System;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **ปิดรับสมัคร (<c>SiteSettings.RegistrationEnabled</c>) ต้องกันที่เซิร์ฟเวอร์ทุกทางที่สร้างผู้ใช้/บริษัทใหม่ ·
/// คำเชิญเข้าบริษัทที่มีอยู่ต้องยังใช้ได้** (ผลตรวจ S-08 · รอบ 193)
///
/// <para>═══ บั๊กที่ล็อกไว้ ═══ มีแค่ register.html ที่ซ่อนฟอร์ม · <c>RegisterAsync</c>/สมัครผ่าน SSO/<c>CompanyService.CreateAsync</c>
/// ไม่ตรวจ ⇒ ยิง API ตรงสร้างบัญชี+บริษัทได้ · และหน้าเว็บซ่อนฟอร์มแม้มาจากลิงก์คำเชิญ</para>
///
/// <para>ครึ่งแรก = ปิดแล้วได้ 403 + ข้อความไทย · ครึ่งหลัง (ทิศตรงข้าม) = เปิดอยู่ทุกทางผ่านเหมือนเดิม และคนที่ถูกเชิญยังสมัครได้</para>
/// </summary>
public class RegistrationPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 3, 0, 0, DateTimeKind.Utc);

    // ════════ ครึ่งแรก: ปิดรับสมัคร ════════

    [Fact]
    public void ปิดรับสมัคร_ไม่มีคำเชิญ_สมัครไม่ได้_403ข้อความไทย()
    {
        var d = RegistrationPolicy.EvaluateNewAccount(false, hasUsableInvitation: false);
        Assert.False(d.Allowed);
        Assert.False(d.MayCreateCompany);
        Assert.Equal(403, d.StatusCode);
        Assert.Equal(RegistrationPolicy.ClosedMessage, d.Message);
        Assert.Contains("ปิดรับสมัคร", d.Message);
        Assert.Contains("คำเชิญ", d.Message);        // บอกทางไปต่อ
        Assert.Equal(RegistrationPolicy.RuleCode, d.RuleCode);
    }

    [Fact]
    public void ปิดรับสมัคร_ผู้ใช้เดิมเปิดบริษัทใหม่ไม่ได้()
    {
        var d = RegistrationPolicy.EvaluateNewCompany(false, actorIsPlatformAdmin: false);
        Assert.False(d.Allowed);
        Assert.Equal(403, d.StatusCode);
        Assert.Equal(RegistrationPolicy.NewCompanyClosedMessage, d.Message);
    }

    [Fact]
    public void ปิดรับสมัคร_มีคำเชิญ_สมัครได้แต่ห้ามงอกบริษัท()
    {
        var d = RegistrationPolicy.EvaluateNewAccount(false, hasUsableInvitation: true);
        Assert.True(d.Allowed);
        Assert.False(d.MayCreateCompany);
    }

    [Theory]
    [InlineData(InvitationStatus.Accepted)]
    [InlineData(InvitationStatus.Expired)]
    [InlineData(InvitationStatus.Cancelled)]
    public void คำเชิญที่ใช้ไปแล้วหรือยกเลิก_ไม่ใช่ใบผ่าน(InvitationStatus status)
        => Assert.False(RegistrationPolicy.IsInvitationUsable(status, Now.AddDays(3), "a@x.com", "a@x.com", Now));

    [Fact]
    public void คำเชิญหมดอายุ_ไม่ใช่ใบผ่าน()
        => Assert.False(RegistrationPolicy.IsInvitationUsable(InvitationStatus.Pending, Now, "a@x.com", "a@x.com", Now));

    [Fact]
    public void คำเชิญของอีเมลอื่น_ไม่ใช่ใบผ่าน_กันเอาลิงก์คนอื่นมาสมัครบัญชีตัวเอง()
        => Assert.False(RegistrationPolicy.IsInvitationUsable(InvitationStatus.Pending, Now.AddDays(3), "a@x.com", "b@x.com", Now));

    [Fact]
    public void อีเมลว่าง_ไม่ใช่ใบผ่าน()
        => Assert.False(RegistrationPolicy.IsInvitationUsable(InvitationStatus.Pending, Now.AddDays(3), "a@x.com", " ", Now));

    // ════════ ครึ่งหลัง (ทิศตรงข้าม): เปิดอยู่ = เหมือนเดิม · คำเชิญยังใช้ได้ ════════

    [Theory]
    [InlineData(true)]
    [InlineData(null)]   // ไม่มีแถว SiteSettings = เปิด (entity default · /api/site/landing ตอบ ?? true)
    public void เปิดรับสมัคร_สมัครได้และสร้างบริษัทได้(bool? enabled)
    {
        var d = RegistrationPolicy.EvaluateNewAccount(enabled, hasUsableInvitation: false);
        Assert.True(d.Allowed);
        Assert.True(d.MayCreateCompany);
        Assert.Null(d.Message);
        Assert.Equal(200, d.StatusCode);
        Assert.True(RegistrationPolicy.EvaluateNewCompany(enabled, actorIsPlatformAdmin: false).Allowed);
    }

    [Fact]
    public void คำเชิญที่ยังใช้ได้_อีเมลตรงไม่สนตัวพิมพ์_เป็นใบผ่าน()
        => Assert.True(RegistrationPolicy.IsInvitationUsable(
            InvitationStatus.Pending, Now.AddDays(3), "Staff@Example.com ", "staff@example.com", Now));

    [Fact]
    public void ปิดรับสมัคร_ผู้ดูแลแพลตฟอร์มยังเปิดบริษัทให้ลูกค้าได้()
        => Assert.True(RegistrationPolicy.EvaluateNewCompany(false, actorIsPlatformAdmin: true).Allowed);
}
