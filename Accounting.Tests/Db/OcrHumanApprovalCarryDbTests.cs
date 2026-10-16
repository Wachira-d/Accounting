using Accounting.Helpers;
using Accounting.Models.DTOs.Approval;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Ocr;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ทีม OCR 14 (ฝ่ายค้านรอบ 5 P2-a · P1-1) บน PostgreSQL — ด่านอนุมัติโดยคนของค่าที่ระบบตั้ง:
/// (1) เอกสารลูกที่ก๊อปค่า (คัดลอก/แปลง/รายการเกิดซ้ำ — ไม่มีลิงก์สแกน) ถูกบล็อกตามค่า · แก้แล้วปลด · ไม่มีอะไรถูกบันทึกเมื่อด่านทำงาน ·
/// (2) workflow บอกตั้งแต่ขั้นแรก (ปฏิเสธได้) · (3) บรรทัดมิเตอร์จากผลแกะรุ่นก่อน: บล็อกพร้อมทางไปต่อที่ตรงชนิด · หมายเหตุซ่อมบนบรรทัดมิเตอร์ไม่บล็อก ·
/// (4) ทิศตรงข้าม: ใบทั่วไป · ใบบริษัทอื่นที่ค่าเหมือนกัน (กฎ M)
/// </summary>
[Trait("Category", "Db")]
public class OcrHumanApprovalCarryDbTests
{
    private readonly ITestOutputHelper _out;
    public OcrHumanApprovalCarryDbTests(ITestOutputHelper output) => _out = output;

    private static IPermissionService AllowAll() =>
        F4Proxy<IPermissionService>.Create((name, _) =>
            name == nameof(IPermissionService.HasPermissionAsync) ? (object)true : new List<string>());

    private static INotificationService Notifications() => F4Proxy<INotificationService>.Create((_, _) => null);

    [Fact]
    public async Task OCR14_เอกสารลูกถือค่าที่ระบบตั้ง_บล็อกตามค่า_workflowบอกตั้งแต่ขั้นแรก_บรรทัดมิเตอร์มีทางไปต่อ_ทิศตรงข้ามไม่บล็อก()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var company = new Company { Name = "บริษัทด่านลูก OCR 14", TaxId = "0105556001401", IsVatRegistered = true };
        var other = new Company { Name = "บริษัทอื่น OCR 14", TaxId = "0105556001402", IsVatRegistered = true };
        db.Companies.AddRange(company, other);
        await db.SaveChangesAsync();
        var c = company.Id;
        var contact = new Contact { CompanyId = c, Name = "ร้านน็อตทดสอบ", IsSupplier = true };
        var otherContact = new Contact { CompanyId = other.Id, Name = "ร้านอื่น", IsSupplier = true };
        db.Contacts.AddRange(contact, otherContact);
        await db.SaveChangesAsync();

        Document Draft(Guid cid, Guid contactId, string no, params (string Desc, decimal Q, decimal P, decimal Disc, decimal Amt, string? Unit)[] lines)
        {
            var d = new Document
            {
                CompanyId = cid, ContactId = contactId, DocumentType = DocumentType.PurchaseInvoice, DocumentNumber = no,
                Status = DocumentStatus.Draft, DocumentDate = DateTime.UtcNow.Date, SubTotal = lines.Sum(l => l.Amt), TotalAmount = lines.Sum(l => l.Amt), Currency = "THB",
            };
            var i = 1;
            foreach (var l in lines)
                d.Lines.Add(new DocumentLine { LineOrder = i++, Description = l.Desc, Quantity = l.Q, UnitPrice = l.P, DiscountAmount = l.Disc, Amount = l.Amt, Unit = l.Unit ?? "ชิ้น" });
            return d;
        }
        var nut = ("น็อต M4", 100000m, 0.01m, 500m, 500m, (string?)"ตัว");
        var source = Draft(c, contact.Id, "DRAFT-OCR14-SRC", nut);
        var clone = Draft(c, contact.Id, "DRAFT-OCR14-CLONE", nut, ("ค่าขนส่ง", 1m, 300m, 0m, 300m, null));     // รูปที่ DocumentCloneController ก๊อป
        var plain = Draft(c, contact.Id, "DRAFT-OCR14-PLAIN", ("น็อต M4", 100000m, 0.01m, 0m, 1000m, "ตัว"), ("ปากกา", 2m, 10m, 0m, 20m, "ด้าม"));
        var foreignTwin = Draft(other.Id, otherContact.Id, "DRAFT-OCR14-OTHER", nut);
        var peaV12 = Draft(c, contact.Id, "DRAFT-OCR14-PEA", ("ค่าพลังงาน", 4272m, 4.54m, 13.38m, 19381.50m, null));
        var peaLump = Draft(c, contact.Id, "DRAFT-OCR14-PEA-LUMP", ("ค่าพลังงาน (จำนวน 4,272)", 1m, 19381.50m, 0m, 19381.50m, "รายการ"));
        db.Documents.AddRange(source, clone, plain, foreignTwin, peaV12, peaLump);
        await db.SaveChangesAsync();

        var overCapNote = OcrLineQuantityGuard.Decide(100000m, null, 500m, null, null, "น็อต M4", "ตัว").Note!;
        var v12Note = OcrLineQuantityGuard.Decide(4272m, null, 19381.50m, null, null, "สติกเกอร์ฉลาก", "แผ่น").Note!;   // ร่องรอย ⟦4272×4.54⟧ ของรุ่น 12
        db.OcrScanResults.AddRange(
            new OcrScanResult { CompanyId = c, OriginalFileName = "nut.pdf", ScanStatus = "Completed", ExtractionVersion = 12, CreatedDocumentId = source.Id,
                ProcessingNotes = "OCR อ่านแล้ว\n" + overCapNote },
            new OcrScanResult { CompanyId = c, OriginalFileName = "pea-v12.pdf", ScanStatus = "Completed", ExtractionVersion = 12, CreatedDocumentId = peaV12.Id,
                ExtractedVendorTaxId = "0994000165501", ExtractedVendorName = "การไฟฟ้าส่วนภูมิภาค", ProcessingNotes = v12Note },
            new OcrScanResult { CompanyId = c, OriginalFileName = "pea-v7.pdf", ScanStatus = "Completed", ExtractionVersion = 7, CreatedDocumentId = peaLump.Id,
                ExtractedVendorTaxId = "0994000165501", ExtractedVendorName = "การไฟฟ้าส่วนภูมิภาค",
                ProcessingNotes = OcrLumpSumRepair.ScanNote(7, 1, "ค่าพลังงาน (จำนวน 4,272)", 19381.50m) });
        await db.SaveChangesAsync();

        async Task<string?> Block(Guid companyId, Guid docId)
        {
            using var r = DbTestDatabase.TryCreateContext()!;
            var d = await r.Documents.Include(x => x.Lines).SingleAsync(x => x.Id == docId && x.CompanyId == companyId);
            return await OcrLumpSumRepairService.ApprovalBlockAsync(r, companyId, d);
        }

        // (1) ต้นทาง (ลิงก์สแกน) และใบลูก (ไม่มีลิงก์ — เดิมอนุมัติได้ และ "ส่วนลด" 500 พิมพ์เป็นส่วนลดจริง)
        Assert.Contains("แพ็ก 10", await Block(c, source.Id));
        var childReason = await Block(c, clone.Id);
        Assert.NotNull(childReason);
        Assert.StartsWith(OcrPostingReadiness.CarriedLead, childReason);
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var docs = new DocumentService(req, new AccountingService(req), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => docs.ApproveDocumentAsync(c, clone.Id, "tester", acknowledgeWarnings: true));
            Assert.Equal(OcrPostingReadiness.HumanApprovalRuleCode, ex.RuleCode);
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var d = await check.Documents.AsNoTracking().SingleAsync(x => x.Id == clone.Id);
            Assert.Equal((DocumentStatus.Draft, "DRAFT-OCR14-CLONE"), (d.Status, d.DocumentNumber));   // ไม่มีอะไรถูกบันทึก
        }

        // (4) ทิศตรงข้าม: ใบที่บังเอิญ 100,000 × 0.01 (ไม่มีเศษ) · ใบบริษัทอื่นที่ค่าเหมือนกันทุกตัว ⇒ ไม่บล็อก
        Assert.Null(await Block(c, plain.Id));
        Assert.Null(await Block(other.Id, foreignTwin.Id));

        // (3) บรรทัดมิเตอร์ของผลแกะรุ่น 12 (เศษ 13.38 ในช่องส่วนลด) ⇒ บล็อก + ทางไปต่อแบบมิเตอร์ (แกะใหม่ได้บรรทัดที่ถูก — ไม่ต้องพิมพ์) · หมายเหตุซ่อมบนบรรทัดมิเตอร์ 1 × ยอด ⇒ ไม่บล็อก
        var peaReason = await Block(c, peaV12.Id);
        Assert.Contains("บรรทัดมิเตอร์", peaReason);
        Assert.Contains("แกะใหม่", peaReason);
        Assert.Null(await Block(c, peaLump.Id));

        // (2) workflow: ขั้นที่ 1 จาก 2 ⇒ บอกตั้งแต่ขั้นแรก (409 · ไม่มีอะไรถูกบันทึก) · ปฏิเสธได้
        var requester = new User { Email = $"ocr14-r-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้ขอ", Status = UserStatus.Active };
        var approver1 = new User { Email = $"ocr14-a-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้อนุมัติ 1", Status = UserStatus.Active };
        var approver2 = new User { Email = $"ocr14-b-{Guid.NewGuid():N}@test.local", PasswordHash = "x", FullName = "ผู้อนุมัติ 2", Status = UserStatus.Active };
        db.Users.AddRange(requester, approver1, approver2);
        db.CompanyUsers.AddRange(new CompanyUser { UserId = requester.Id, CompanyId = c, Role = UserRole.Staff },
            new CompanyUser { UserId = approver1.Id, CompanyId = c, Role = UserRole.Staff }, new CompanyUser { UserId = approver2.Id, CompanyId = c, Role = UserRole.Staff });
        var rule = new ApprovalRule { CompanyId = c, Name = "สองขั้น OCR 14", IsActive = true };
        rule.Steps.Add(new ApprovalStep { StepOrder = 1, ApproverUserId = approver1.Id, IsRequired = true });
        rule.Steps.Add(new ApprovalStep { StepOrder = 2, ApproverUserId = approver2.Id, IsRequired = true });
        db.ApprovalRules.Add(rule);
        await db.SaveChangesAsync();
        var wf = new ApprovalRequest
        {
            CompanyId = c, ApprovalRuleId = rule.Id, EntityType = "Document", EntityId = clone.Id,
            RequestedByUserId = requester.Id, OverallStatus = ApprovalStatus.Pending, CurrentStep = 1,
        };
        wf.Actions.Add(new ApprovalAction { StepOrder = 1, ApproverUserId = approver1.Id, Status = ApprovalStatus.Pending });
        wf.Actions.Add(new ApprovalAction { StepOrder = 2, ApproverUserId = approver2.Id, Status = ApprovalStatus.Pending });
        db.ApprovalRequests.Add(wf);
        await db.SaveChangesAsync();
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var map = new F4ServiceMap().Add(AllowAll());
            var approval = new ApprovalService(req, Notifications(), map);
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                approval.SubmitActionAsync(c, wf.Id, approver1.Id, new SubmitApprovalActionRequest(ApprovalStatus.Approved, null)));
            Assert.Equal((OcrPostingReadiness.HumanApprovalRuleCode, 409), (ex.RuleCode, ex.StatusCode));
            Assert.Contains("ปฏิเสธ", ex.Message);
        }
        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var w = await check.ApprovalRequests.AsNoTracking().Include(r => r.Actions).SingleAsync(r => r.Id == wf.Id);
            Assert.Equal((ApprovalStatus.Pending, 1), (w.OverallStatus, w.CurrentStep));
            Assert.All(w.Actions, a => Assert.Equal(ApprovalStatus.Pending, a.Status));
        }

        // แก้บรรทัดในใบลูกตามทางไปต่อ ⇒ ปลด (ด่านเดียวกันทั้งหน้าเอกสารและ workflow)
        using (var edit = DbTestDatabase.TryCreateContext()!)
        {
            var line = await edit.DocumentLines.SingleAsync(l => l.DocumentId == clone.Id && l.LineOrder == 1);
            (line.Quantity, line.UnitPrice, line.DiscountAmount, line.Unit) = (10000m, 0.05m, 0m, "แพ็ก 10");
            await edit.SaveChangesAsync();
        }
        Assert.Null(await Block(c, clone.Id));
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var map = new F4ServiceMap().Add(AllowAll());
            var approval = new ApprovalService(req, Notifications(), map);
            var step1 = await approval.SubmitActionAsync(c, wf.Id, approver1.Id, new SubmitApprovalActionRequest(ApprovalStatus.Approved, null));
            Assert.Equal(2, step1.CurrentStep);
        }
    }
}
