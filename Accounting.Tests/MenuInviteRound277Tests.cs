using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 277 — pure: (A) ตาราง "เมนู → สิทธิ์ที่ API ตรวจ" (<see cref="MenuPermissionMap"/>) ที่เซิร์ฟเวอร์ใช้ซ่อนเมนู/ทางลัด ·
/// (B) กติกาคำเชิญเข้าบริษัท (<see cref="CompanyInvitationPolicy"/>) + ด่านสมาชิกที่ยังไม่ได้รับสิทธิ์ (<see cref="PendingMemberAccess"/>)
/// </summary>
public class MenuInviteRound277Tests
{
    private static readonly DateTime Now = new(2026, 11, 20, 5, 0, 0, DateTimeKind.Utc);

    // ── A: เมนูตามสิทธิ์ ──

    [Fact]
    public void ตารางเมนู_แดชบอร์ดผูกReportsDashboard_รวมเมนูรับชำระของรอบ201()
    {
        Assert.Equal(PermissionKeys.ReportsDashboard, MenuPermissionMap.Keys["dashboard"]);
        Assert.Equal(PermissionKeys.ReportsDashboard, MenuPermissionMap.Keys["executive-reports"]);
        Assert.Equal(PermissionKeys.ReportsFinancial, MenuPermissionMap.Keys["consolidation"]);
        foreach (var kv in PaymentGatewayPermissionScope.MenuPermissionKeys)
            Assert.Equal(kv.Value, MenuPermissionMap.Keys[kv.Key]);
        Assert.All(MenuPermissionMap.Keys.Values, k => Assert.True(PermissionKeys.IsPermissionKey(k)));
        // ทุกคีย์อยู่ในแคตตาล็อก (หน้าบทบาทติ๊กได้ — ไม่งั้นเมนูซ่อนถาวร)
        Assert.All(MenuPermissionMap.Keys.Values, k => Assert.Contains(PermissionKeys.Catalog, c => c.Key == k));
    }

    [Fact]
    public async Task ไม่มีสิทธิ์แดชบอร์ด_เมนูแดชบอร์ดและหน้าที่ใช้คีย์เดียวกันถูกตัด_คีย์อื่นไม่ถูกแตะ_ตรวจคีย์ละครั้ง()
    {
        var asked = new List<string>();
        var denied = await MenuPermissionMap.DeniedMenuIdsAsync(k => { asked.Add(k); return Task.FromResult(k != PermissionKeys.ReportsDashboard); });
        Assert.NotNull(denied);
        Assert.Contains("dashboard", denied!);
        Assert.Contains("fpa", denied);
        Assert.Contains("financial-mgmt", denied);
        Assert.DoesNotContain("consolidation", denied);
        Assert.DoesNotContain("payment-intents", denied);
        Assert.Equal(asked.Count, asked.Distinct().Count());
    }

    [Fact]
    public async Task มีสิทธิ์ครบ_ไม่มีเมนูถูกตัด_null()
        => Assert.Null(await MenuPermissionMap.DeniedMenuIdsAsync(_ => Task.FromResult(true)));

    // ── B: คำเชิญ ──

    [Fact]
    public void Token_สุ่มยาวพอ_ไม่ซ้ำ_hashคงที่hexตัวเล็ก_ว่างไม่มีhash()
    {
        var a = CompanyInvitationPolicy.NewToken();
        var b = CompanyInvitationPolicy.NewToken();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 43);
        Assert.DoesNotContain('+', a); Assert.DoesNotContain('/', a); Assert.DoesNotContain('=', a);
        var h = CompanyInvitationPolicy.HashToken(a)!;
        Assert.Equal(64, h.Length);
        Assert.Equal(h, h.ToLowerInvariant());
        Assert.Equal(h, CompanyInvitationPolicy.HashToken(" " + a + " "));
        Assert.NotEqual(h, CompanyInvitationPolicy.HashToken(b));
        Assert.Null(CompanyInvitationPolicy.HashToken(""));
        Assert.Null(CompanyInvitationPolicy.HashToken(null));
        // ค่าอ้างอิง SHA-256("abc") — สูตรเดียวกับ migration (encode(sha256(convert_to(token,'UTF8')),'hex'))
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", CompanyInvitationPolicy.HashToken("abc"));
    }

    [Theory]
    [InlineData(InvitationStatus.Pending, 3, "a@x.com", "A@X.com", CompanyInvitationPolicy.Verdict.Ok)]
    [InlineData(InvitationStatus.Pending, -1, "a@x.com", "a@x.com", CompanyInvitationPolicy.Verdict.Expired)]
    [InlineData(InvitationStatus.Expired, 3, "a@x.com", "a@x.com", CompanyInvitationPolicy.Verdict.Expired)]
    [InlineData(InvitationStatus.Accepted, 3, "a@x.com", "a@x.com", CompanyInvitationPolicy.Verdict.AlreadyUsed)]
    [InlineData(InvitationStatus.Cancelled, 3, "a@x.com", "a@x.com", CompanyInvitationPolicy.Verdict.Cancelled)]
    [InlineData(InvitationStatus.Pending, 3, "a@x.com", "b@x.com", CompanyInvitationPolicy.Verdict.EmailMismatch)]
    [InlineData(InvitationStatus.Pending, 3, "a@x.com", " ", CompanyInvitationPolicy.Verdict.EmailMismatch)]
    public void ตรวจคำเชิญ(InvitationStatus status, int days, string invited, string actor, CompanyInvitationPolicy.Verdict expected)
    {
        Assert.Equal(expected, CompanyInvitationPolicy.Check(status, Now.AddDays(days), invited, actor, Now));
        if (expected != CompanyInvitationPolicy.Verdict.Ok)
            Assert.False(string.IsNullOrWhiteSpace(CompanyInvitationPolicy.Message(expected, invited, actor)));
    }

    [Fact]
    public void ไม่พบคำเชิญ_404_อีเมลไม่ตรง_403_อื่น409()
    {
        Assert.Equal(CompanyInvitationPolicy.Verdict.NotFound, CompanyInvitationPolicy.Check(null, null, null, "a@x.com", Now));
        Assert.Equal(404, CompanyInvitationPolicy.StatusCode(CompanyInvitationPolicy.Verdict.NotFound));
        Assert.Equal(403, CompanyInvitationPolicy.StatusCode(CompanyInvitationPolicy.Verdict.EmailMismatch));
        Assert.Equal(409, CompanyInvitationPolicy.StatusCode(CompanyInvitationPolicy.Verdict.AlreadyUsed));
    }

    [Fact]
    public void ที่นั่ง_เฉพาะรอตอบรับที่ยังไม่หมดอายุ()
    {
        Assert.True(CompanyInvitationPolicy.HoldsSeat(InvitationStatus.Pending, Now.AddDays(1), Now));
        Assert.False(CompanyInvitationPolicy.HoldsSeat(InvitationStatus.Pending, Now.AddSeconds(-1), Now));
        Assert.False(CompanyInvitationPolicy.HoldsSeat(InvitationStatus.Cancelled, Now.AddDays(1), Now));
        Assert.Equal(InvitationStatus.Expired, CompanyInvitationPolicy.DisplayStatus(InvitationStatus.Pending, Now.AddSeconds(-1), Now));
        Assert.Equal(InvitationStatus.Pending, CompanyInvitationPolicy.DisplayStatus(InvitationStatus.Pending, Now.AddDays(1), Now));
    }

    [Fact]
    public void ไม่เลือกบทบาท_เข้าแบบยังไม่มีสิทธิ์และบทบาทต่ำสุด_เลือกแล้วได้บทบาทนั้น()
    {
        Assert.Equal((UserRole.Viewer, true), CompanyInvitationPolicy.MembershipFor(false, UserRole.Owner));
        Assert.Equal((UserRole.Accountant, false), CompanyInvitationPolicy.MembershipFor(true, UserRole.Accountant));
        Assert.Contains("ยังไม่กำหนด", CompanyInvitationPolicy.RoleLabel(false, UserRole.Viewer));
        Assert.Equal("นักบัญชี · ขาย", CompanyInvitationPolicy.RoleLabel(true, UserRole.Accountant, "ขาย"));
    }

    [Fact]
    public void สมัครด้วยคำเชิญ_ตัวตัดสินเดิมของด่านเปิดรับสมัครใช้กติกาเดียวกัน()
    {
        Assert.True(RegistrationPolicy.IsInvitationUsable(InvitationStatus.Pending, Now.AddDays(1), "a@x.com", "A@x.com", Now));
        Assert.False(RegistrationPolicy.IsInvitationUsable(InvitationStatus.Pending, Now.AddDays(1), "a@x.com", "b@x.com", Now));
    }

    [Theory]
    [InlineData("GET", "/api/company/11111111-1111-1111-1111-111111111111/roles/my-permissions", true)]
    [InlineData("GET", "/api/company/11111111-1111-1111-1111-111111111111/roles/my-permissions/", true)]
    [InlineData("POST", "/api/company/11111111-1111-1111-1111-111111111111/roles/my-permissions", false)]
    [InlineData("GET", "/api/companies/11111111-1111-1111-1111-111111111111/dashboard", false)]
    [InlineData("GET", "/api/company/11111111-1111-1111-1111-111111111111/users", false)]
    [InlineData("DELETE", "/api/company/11111111-1111-1111-1111-111111111111/users/x", false)]
    public void สมาชิกยังไม่ได้รับสิทธิ์_ผ่านได้เฉพาะสิทธิ์ของฉัน(string method, string path, bool allowed)
        => Assert.Equal(allowed, PendingMemberAccess.IsAllowed(method, path));
}
