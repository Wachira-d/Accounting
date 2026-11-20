using System.Reflection;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 276 — ตัวตัดสิน "เห็นเฉพาะเอกสารที่ตัวเองสร้าง / สร้าง-แก้ได้เฉพาะร่าง" (<see cref="DocumentRestrictionPolicy"/>) ·
/// ทิศเข้ม: ผู้ถูกจำกัดเรียกรายงาน/อนุมัติ/ยกเลิก/รับชำระไม่ได้ · ทิศตรงข้าม: ผู้ไม่ถูกจำกัดผ่านทุกทาง (พฤติกรรมเดิม) และผู้ถูกจำกัดยังทำงานร่างได้ครบ
/// </summary>
public class DocumentRestrictionPolicyTests
{
    private static readonly DocumentRestrictionPolicy.Restriction Own = new(true, true);
    private static readonly DocumentRestrictionPolicy.Restriction DraftOnly = new(false, true);
    private const string C = "api/companies/{companyId:guid}/";

    [Fact]
    public void ค่าที่มีผล_เจ้าของไม่ถูกจำกัดเสมอ_เห็นเฉพาะของตัวเองบังคับเฉพาะร่าง_รวมบทบาท()
    {
        Assert.False(DocumentRestrictionPolicy.Effective(UserRole.Owner, true, true, true, true).IsRestricted);
        Assert.False(DocumentRestrictionPolicy.Effective(UserRole.SystemAdmin, true, true, false, false).IsRestricted);
        // เห็นเฉพาะของตัวเอง ⇒ เฉพาะร่าง (คำตัดสิน 276-2)
        Assert.Equal(Own, DocumentRestrictionPolicy.Effective(UserRole.Staff, true, false, false, false));
        // ค่าของบทบาทมีผลแม้ค่าต่อผู้ใช้ปิด
        Assert.Equal(Own, DocumentRestrictionPolicy.Effective(UserRole.Staff, false, false, true, false));
        Assert.Equal(DraftOnly, DocumentRestrictionPolicy.Effective(UserRole.Accountant, false, false, false, true));
        // ทิศตรงข้าม: ค่าตั้งต้น = ไม่จำกัด (ผู้ใช้เดิมทุกคน)
        Assert.False(DocumentRestrictionPolicy.Effective(UserRole.Staff, false, false, false, false).IsRestricted);
        Assert.Null(DocumentRestrictionPolicy.Notice(DocumentRestrictionPolicy.Restriction.None));
        Assert.Contains("เห็นเฉพาะเอกสารที่คุณสร้าง", DocumentRestrictionPolicy.Notice(Own));
        Assert.Contains("เฉพาะเอกสารฉบับร่าง", DocumentRestrictionPolicy.Notice(DraftOnly));
    }

    [Fact]
    public void ตั้งค่าบนเจ้าของหรือตัวเองไม่ได้_สมาชิกอื่นตั้งได้()
    {
        Assert.NotNull(DocumentRestrictionPolicy.SetProblem(UserRole.Owner, false));
        Assert.NotNull(DocumentRestrictionPolicy.SetProblem(UserRole.Staff, true));
        Assert.Null(DocumentRestrictionPolicy.SetProblem(UserRole.Staff, false));
        Assert.Null(DocumentRestrictionPolicy.SetProblem(UserRole.Accountant, false));
    }

    [Theory]
    [InlineData("GET", C + "Document")]
    [InlineData("GET", C + "Document/{documentId:guid}")]
    [InlineData("GET", C + "Document/export")]
    [InlineData("POST", C + "Document")]
    [InlineData("PUT", C + "Document/{documentId:guid}")]
    [InlineData("POST", C + "Document/{documentId:guid}/convert/{targetType}")]
    [InlineData("POST", C + "documents/{sourceId:guid}/clone")]
    [InlineData("POST", C + "document/{documentId:guid}/approval-request")]
    [InlineData("POST", C + "document-templates/generate-pdf")]
    public void ผู้เห็นเฉพาะของตัวเอง_งานร่างทำได้ครบ(string method, string template)
        => Assert.True(DocumentRestrictionPolicy.Decide(Own, method, template).Allowed);

    [Theory]
    [InlineData("GET", C + "Report/profit-loss")]
    [InlineData("GET", C + "accounting/journals")]
    [InlineData("GET", C + "search/quick")]
    [InlineData("GET", C + "document-approval-queue")]
    [InlineData("GET", C + "Document/deposits")]
    [InlineData("GET", C + "Document/payments")]
    [InlineData("GET", "api/mobile/companies/{companyId:guid}/dashboard")]
    [InlineData("POST", C + "Document/{documentId:guid}/approve")]
    public void ผู้เห็นเฉพาะของตัวเอง_รายงานและข้อมูลรวมของบริษัทเปิดไม่ได้(string method, string template)
    {
        var d = DocumentRestrictionPolicy.Decide(Own, method, template);
        Assert.Equal(DocumentRestrictionPolicy.Verdict.DenyOwnScope, d.Verdict);
        Assert.Equal(DocumentRestrictionPolicy.OwnScopeRuleCode, d.RuleCode);
    }

    [Theory]
    [InlineData("POST", C + "Document/{documentId:guid}/approve")]
    [InlineData("POST", C + "Document/bulk-approve")]
    [InlineData("POST", C + "Document/{documentId:guid}/void")]
    [InlineData("POST", C + "Document/payments")]
    [InlineData("POST", C + "Document/{documentId:guid}/write-off-bad-debt")]
    [InlineData("POST", C + "approval/requests/{requestId:guid}/action")]
    [InlineData("POST", C + "document/{documentId:guid}/approval-request/return")]
    [InlineData("POST", "api/mobile/companies/{companyId:guid}/approve")]
    [InlineData("POST", C + "accounting/journals")]
    public void ผู้เฉพาะร่าง_อนุมัติยกเลิกรับชำระลงบัญชีไม่ได้(string method, string template)
        => Assert.Equal(DocumentRestrictionPolicy.Verdict.DenyDraftOnly, DocumentRestrictionPolicy.Decide(DraftOnly, method, template).Verdict);

    [Theory]
    [InlineData("GET", C + "Report/profit-loss")]
    [InlineData("GET", C + "accounting/journals")]
    [InlineData("POST", C + "Document")]
    [InlineData("PUT", C + "Document/{documentId:guid}")]
    [InlineData("POST", C + "document/{documentId:guid}/approval-request/withdraw")]
    public void ผู้เฉพาะร่าง_อ่านได้ตามสิทธิ์เดิม_และเขียนร่างได้(string method, string template)
        => Assert.True(DocumentRestrictionPolicy.Decide(DraftOnly, method, template).Allowed);

    [Theory]
    [InlineData("POST", C + "Document/{documentId:guid}/approve")]
    [InlineData("GET", C + "Report/profit-loss")]
    [InlineData("DELETE", C + "Document/{documentId:guid}/purge")]
    public void ผู้ไม่ถูกจำกัด_ผ่านทุกทาง_พฤติกรรมเดิม(string method, string template)
        => Assert.True(DocumentRestrictionPolicy.Decide(DocumentRestrictionPolicy.Restriction.None, method, template).Allowed);

    [Fact]
    public void แก้ลบแนบไฟล์_ติดด่านสถานะร่าง()
    {
        var put = DocumentRestrictionPolicy.Decide(DraftOnly, "PUT", C + "Document/{documentId:guid}");
        Assert.True(put.Rule!.Checks.HasFlag(DocumentRestrictionPolicy.RouteCheck.DraftStatus));
        Assert.True(DocumentRestrictionPolicy.IsEditableDraftStatus(DocumentStatus.Draft));
        Assert.True(DocumentRestrictionPolicy.IsEditableDraftStatus(DocumentStatus.Rejected));
        Assert.False(DocumentRestrictionPolicy.IsEditableDraftStatus(DocumentStatus.WaitingApproval));
        Assert.False(DocumentRestrictionPolicy.IsEditableDraftStatus(DocumentStatus.Approved));
    }

    /// <summary>กัน drift: ทุกแถวในรายการอนุญาตต้องตรงกับ action จริง (method + route template) — route เปลี่ยนแล้วรายการไม่ตาม = ผู้ถูกจำกัดใช้งานไม่ได้เงียบ ๆ</summary>
    [Fact]
    public void รายการอนุญาตทุกแถวตรงกับ_action_จริง()
    {
        var actual = new HashSet<(string, string)>();
        foreach (var ctl in typeof(Accounting.Controllers.DocumentController).Assembly.GetTypes()
                     .Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var name = ctl.Name.EndsWith("Controller") ? ctl.Name[..^"Controller".Length] : ctl.Name;
            var bases = ctl.GetCustomAttributes<RouteAttribute>(true).Select(r => r.Template.Replace("[controller]", name)).DefaultIfEmpty("").ToList();
            foreach (var m in ctl.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                foreach (var h in m.GetCustomAttributes<HttpMethodAttribute>(true))
                    foreach (var verb in h.HttpMethods)
                        foreach (var b in bases)
                        {
                            var t = h.Template ?? "";
                            var full = t.StartsWith("~/") ? t[2..] : t.StartsWith("api/") || b == "" ? t : string.IsNullOrEmpty(t) ? b : b + "/" + t;
                            actual.Add((verb.ToUpperInvariant(), DocumentRestrictionPolicy.NormalizeTemplate(full)));
                        }
        }
        var missing = DocumentRestrictionPolicy.Routes
            .Where(r => !actual.Contains((r.Method.ToUpperInvariant(), DocumentRestrictionPolicy.NormalizeTemplate(r.Template))))
            .Select(r => r.Method + " " + r.Template).ToList();
        Assert.True(missing.Count == 0, "แถวที่ไม่มี action จริง: " + string.Join(" | ", missing));
    }

    [Fact]
    public void แจ้งขออนุมัติ_ค่าตั้งต้นถึงผู้อนุมัติเอกสาร_ทุกช่อง_กลุ่มเมื่อผูกแล้ว()
    {
        var noGroup = NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.DocumentApprovalRequested, false);
        var row = Assert.Single(noGroup);
        Assert.Equal(NotificationRecipientRoles.DocumentApprover, row.RecipientRole);
        Assert.True(row.EnableSystem && row.EnableEmail && row.EnableLine);
        Assert.False(row.EnableLineGroup);
        Assert.True(Assert.Single(NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.DocumentApprovalRequested, true)).EnableLineGroup);
        Assert.Equal(NotificationAudience.TenantBusiness, NotificationAudiencePolicy.Classify(NotificationEvents.DocumentApprovalRequested));
        // ทิศตรงข้าม: คีย์ตอบกลับผู้ขอ = ผู้รับระบุตัว (ไม่มีค่าตั้งต้นแบบบทบาท ⇒ ไม่ประกาศเข้ากลุ่ม)
        Assert.Empty(NotificationAudiencePolicy.DefaultMatrix(NotificationEvents.DocumentApprovalReturned, true));
        Assert.Contains(NotificationRecipientRoles.DocumentApprover, NotificationRecipientRoles.All);
        Assert.Contains(NotificationEvents.All, c => c.Key == NotificationEvents.DocumentApprovalRequested);
    }
}
