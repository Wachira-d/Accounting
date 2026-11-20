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
    [InlineData("POST", C + "ocr/upload")]                                 // ฝ่ายค้าน LOW-12: OCR สร้างร่างของตัวเอง
    [InlineData("POST", C + "ocr/{scanId:guid}/create-document")]
    [InlineData("POST", C + "Document/contacts")]                         // MED-6: สร้างผู้ติดต่อใหม่ได้
    [InlineData("GET", "api/notification")]
    [InlineData("POST", "api/auth/change-password")]
    [InlineData("GET", "api/gov/address/provinces")]
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
    [InlineData("GET", "api/accountant-workspace")]                      // ฝ่ายค้าน HIGH-2: ภาพรวมข้ามบริษัท
    [InlineData("GET", "api/account-subscription")]
    [InlineData("POST", "api/notification/send")]
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
    [InlineData("PUT", C + "Document/contacts/{contactId:guid}")]          // ฝ่ายค้าน MED-6: แก้ผู้ติดต่อเดิมไม่ได้
    [InlineData("POST", C + "ocr/{scanId:guid}/import-stock")]
    [InlineData("POST", "api/account-subscription/attach")]
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

    [Fact]
    public void คำสั่งLINE_policyเดียวกับเว็บ()
    {
        var L = DocumentRestrictionPolicy.LineCommand.CompanySummary;
        Assert.NotNull(DocumentRestrictionPolicy.LineCommandDenied(Own, L));
        Assert.Null(DocumentRestrictionPolicy.LineCommandDenied(DraftOnly, L));               // เว็บ: ผู้เฉพาะร่างอ่านรายงานได้
        Assert.NotNull(DocumentRestrictionPolicy.LineCommandDenied(DraftOnly, DocumentRestrictionPolicy.LineCommand.AskAssistant));
        Assert.NotNull(DocumentRestrictionPolicy.LineCommandDenied(DraftOnly, DocumentRestrictionPolicy.LineCommand.Approve));
        Assert.Null(DocumentRestrictionPolicy.LineCommandDenied(Own, DocumentRestrictionPolicy.LineCommand.CreateDraft));
        foreach (var c in Enum.GetValues<DocumentRestrictionPolicy.LineCommand>())
            Assert.Null(DocumentRestrictionPolicy.LineCommandDenied(DocumentRestrictionPolicy.Restriction.None, c));   // ทิศตรงข้าม
    }

    [Fact]
    public void ผู้สร้าง_idเป็นหลัก_ป้ายระบบในคำขอที่ถูกจำกัดเป็นของผู้ใช้()
    {
        var u = Guid.NewGuid();
        Assert.Equal(u, DocumentCreatorStamp.Resolve(u.ToString(), null));
        Assert.Equal(u, DocumentCreatorStamp.Resolve("ocr-auto", u));
        Assert.Null(DocumentCreatorStamp.Resolve("system:recurring", null));
        Assert.True(DocumentCreatorStamp.LooksLikeEmail("a@b.th"));
        Assert.False(DocumentCreatorStamp.LooksLikeEmail("system"));
    }

    [Fact]
    public void ใบซ้ำของผู้อื่น_คืนแค่ผลตัดสิน()
    {
        var c = new Accounting.Models.DTOs.Document.DuplicateDocumentCandidate(Guid.NewGuid(), "PI-001", "PurchaseInvoice", new DateTime(2026, 1, 1),
            999m, "Approved", "INV-9", "ผู้ขายลับ", "SupplierInvoiceNumber", true);
        var r = RestrictedResponseRedactor.RedactDuplicate(c, new DateTime(2026, 2, 1), 100m);
        Assert.Equal(Guid.Empty, r.Id);
        Assert.DoesNotContain("PI-001", r.DocumentNumber);
        Assert.Null(r.ContactName);
        Assert.Null(r.SupplierInvoiceNumber);
        Assert.Equal(100m, r.TotalAmount);
        Assert.True(r.IsStrong);
    }

    [Fact]
    public void id_ใน_body_ถูกเก็บครบ_รวมบรรทัด()
    {
        var rel = Guid.NewGuid(); var src = Guid.NewGuid();
        var req = new Accounting.Models.DTOs.Document.CreateDocumentRequest(DocumentType.Invoice, DateTime.Today, null, Guid.NewGuid(), null, null,
            new List<Accounting.Models.DTOs.Document.DocumentLineRequest> { new("x", 1, null, 1, 0, 7, 0, null) { SourceLineId = src } },
            RelatedDocumentId: rel);
        var (docs, lines) = Accounting.Filters.DocumentRestrictionFilter.BodyReferences(new object?[] { req, Guid.NewGuid(), "x" });
        Assert.Contains(rel, docs);
        Assert.Contains(src, lines);
        var (none, _) = Accounting.Filters.DocumentRestrictionFilter.BodyReferences(new object?[] { req with { RelatedDocumentId = Guid.Empty } });
        Assert.DoesNotContain(Guid.Empty, none);                       // ล้างค่า ≠ การอ้าง
    }

    [Fact]
    public void ข้อความตอบกลับ_ด่านข้ามเอกสารใช้ข้อความกลาง_เลขของคนอื่นถูกแทน()
    {
        Assert.True(RestrictedResponseRedactor.IsCrossDocumentRule("CONVERT-QTY-DONE-AMOUNT-LEFT"));
        Assert.True(RestrictedResponseRedactor.IsCrossDocumentRule("R276-OVER-CONVERT"));
        Assert.False(RestrictedResponseRedactor.IsCrossDocumentRule("RD-86/4-MISSING"));
        Assert.False(RestrictedResponseRedactor.IsCrossDocumentRule(null));
        var g = RestrictedResponseRedactor.GenericConflictMessage("CONVERT-X");
        Assert.Contains("เอกสารอื่นในบริษัท", g);
        Assert.DoesNotMatch(@"\d{1,3}(,\d{3})*\.\d{2}", g);                // ไม่มียอดเงิน
        var t = RestrictedResponseRedactor.Replace("ซ้ำกับ INV-2026-0012 และ INV-2026-001", new[] { "INV-2026-001", "INV-2026-0012" });
        Assert.DoesNotContain("INV-2026", t);
        Assert.Equal("ใบของฉัน QT-1", RestrictedResponseRedactor.Replace("ใบของฉัน QT-1", new[] { "INV-9" }));   // ทิศตรงข้าม
    }
}
