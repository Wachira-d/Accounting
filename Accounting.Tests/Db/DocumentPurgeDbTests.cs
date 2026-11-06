using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 242 — ลบเอกสารถาวรบน PostgreSQL จริง (trait <c>Category=Db</c> · กฎ #4 G):
/// (1) เจ้าของ 2026-11-05: <c>DELETE …/purge?force=true&amp;reason=123</c> ⇒ 500 "23001 … violates RESTRICT setting of foreign key constraint
/// FK_DocumentSignatures_Approvals" — purge ลบขั้นอนุมัติก่อนลายเซ็น · เทสต์จำลองรูป FK ของ production (RESTRICT ตามที่ Program.cs สร้าง) แล้วลบ
/// ใบที่มีขั้นอนุมัติ + ลายเซ็น + แถวลูกอื่น ๆ ต้องผ่านและไม่เหลือแถวลูก ·
/// (2) คำตัดสินข้อ 242-A: ลบพร้อมเอกสารที่เกี่ยวข้อง — สายที่เป็นร่างทั้งหมดถูกลบครบ (audit ทุกใบ) · สายที่มีใบกำกับที่ออกแล้วถูกปฏิเสธทั้งคำขอ
/// พร้อมรายชื่อ (ไม่มีอะไรถูกลบ) · "ลบเฉพาะใบนี้" ยังตัดการอ้างอิงตามเดิม · force โดยไม่มีเหตุผลถูกปฏิเสธ ·
/// (3) ความครบ: ทุก FK แบบ RESTRICT/NO ACTION ที่ชี้ Documents/DocumentApprovals/Payments ในฐานจริงต้องอยู่ใน <see cref="DocumentPurgeOrder"/>
/// </summary>
[Trait("Category", "Db")]
public class DocumentPurgeDbTests
{
    private readonly ITestOutputHelper _out;
    public DocumentPurgeDbTests(ITestOutputHelper output) => _out = output;

    private sealed record Seed(Guid CompanyId, Guid ContactId);

    private static async Task<Seed> SeedAsync(AccountingDbContext db)
    {
        var company = new Company { Name = "บริษัททดสอบลบถาวร", TaxId = "0105556000007", IsVatRegistered = true };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = company.Id, Name = "ลูกค้าลบถาวร", IsCustomer = true, IsSupplier = true };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return new Seed(company.Id, contact.Id);
    }

    private static async Task<Document> DocAsync(AccountingDbContext db, Seed s, DocumentType type, string no,
        Guid? parent, DocumentStatus status)
    {
        var d = new Document
        {
            CompanyId = s.CompanyId, ContactId = s.ContactId, DocumentType = type, DocumentNumber = no, Status = status,
            DocumentDate = DateTime.UtcNow.Date, SubTotal = 1_000m, TotalAmount = 1_070m, BalanceDue = 1_070m,
            RelatedDocumentId = parent, Currency = "THB",
        };
        d.Lines.Add(new DocumentLine { LineOrder = 1, Description = "บริการ A", Quantity = 1, UnitPrice = 1_000m, Amount = 1_000m });
        db.Documents.Add(d);
        await db.SaveChangesAsync();
        return d;
    }

    /// <summary>ขั้นอนุมัติ + ลายเซ็น (ลายเซ็นชี้ขั้นอนุมัติ) + ประวัติแก้ไข + การเตือนชำระ + เช็คล่วงหน้า + outbox ลูกค้าเซ็น</summary>
    private static async Task ChildrenAsync(AccountingDbContext db, Seed s, Document d)
    {
        var approval = new DocumentApproval
        {
            CompanyId = s.CompanyId, DocumentId = d.Id, ApprovalType = "Customer", ApproverRole = "Customer", StepOrder = 1,
            Status = ApprovalStatus.Approved, ApprovedAt = DateTime.UtcNow,
        };
        db.DocumentApprovals.Add(approval);
        await db.SaveChangesAsync();
        db.DocumentSignatures.Add(new DocumentSignature
        {
            CompanyId = s.CompanyId, DocumentId = d.Id, DocumentApprovalId = approval.Id, SignerRole = "ผู้ซื้อ", SignerName = "ลูกค้า",
            SignatureData = "data:image/png;base64,AAAA", SignedAt = DateTime.UtcNow,
        });
        db.DocumentRevisions.Add(new DocumentRevision { CompanyId = s.CompanyId, DocumentId = d.Id, RevisionNumber = 1, SnapshotJson = "{}" });
        db.PaymentReminders.Add(new PaymentReminder { CompanyId = s.CompanyId, DocumentId = d.Id, ContactId = s.ContactId, ReminderDate = DateTime.UtcNow });
        db.PostDatedChecks.Add(new PostDatedCheck
        {
            CompanyId = s.CompanyId, Direction = PdcDirection.Inbound, CheckNumber = "PDC-" + d.Id.ToString("N")[..6], BankName = "ธนาคารทดสอบ",
            Amount = 1_070m, CheckDate = DateTime.UtcNow.Date, IssueDate = DateTime.UtcNow.Date, ScheduledDepositDate = DateTime.UtcNow.Date,
            SourceDocumentId = d.Id,
        });
        db.SignEventOutbox.Add(new SignEventOutboxEntry
        {
            CompanyId = s.CompanyId, EventId = Guid.NewGuid(), EventKey = "purge-test:" + d.Id, EventType = "quotation.accepted",
            DocumentId = d.Id, NextAttemptAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>รูป FK ของ production (Program.cs สร้างด้วย SQL ตอนบูต: ON DELETE RESTRICT) — ฐานเทสต์ที่สร้างจากโมเดล (EnsureCreated) ได้ CASCADE
    /// ตามธรรมเนียม EF ⇒ จำลองให้ตรงก่อน มิฉะนั้นเทสต์ผ่านทั้งที่ production ล้ม (idempotent · ฐานเดียวทั้ง job)</summary>
    private static Task ProductionFkShapeAsync(AccountingDbContext db) => db.Database.ExecuteSqlRawAsync(@"
DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT c.conname, c.conrelid::regclass AS t FROM pg_constraint c
           WHERE c.contype = 'f' AND c.confdeltype <> 'r'
             AND ((c.conrelid = '""DocumentSignatures""'::regclass AND c.confrelid IN ('""DocumentApprovals""'::regclass, '""Documents""'::regclass))
               OR (c.conrelid = '""DocumentApprovals""'::regclass AND c.confrelid = '""Documents""'::regclass))
  LOOP
    EXECUTE format('ALTER TABLE %s DROP CONSTRAINT %I', r.t, r.conname);
  END LOOP;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_DocumentSignatures_Approvals') THEN
    ALTER TABLE ""DocumentSignatures"" ADD CONSTRAINT ""FK_DocumentSignatures_Approvals""
      FOREIGN KEY (""DocumentApprovalId"") REFERENCES ""DocumentApprovals""(""Id"") ON DELETE RESTRICT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_DocumentSignatures_Documents') THEN
    ALTER TABLE ""DocumentSignatures"" ADD CONSTRAINT ""FK_DocumentSignatures_Documents""
      FOREIGN KEY (""DocumentId"") REFERENCES ""Documents""(""Id"") ON DELETE RESTRICT;
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_DocumentApprovals_Documents') THEN
    ALTER TABLE ""DocumentApprovals"" ADD CONSTRAINT ""FK_DocumentApprovals_Documents""
      FOREIGN KEY (""DocumentId"") REFERENCES ""Documents""(""Id"") ON DELETE RESTRICT;
  END IF;
END $$;");

    private static DocumentService Svc(AccountingDbContext db) =>
        new(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static async Task<bool> ExistsAsync(AccountingDbContext db, Guid companyId, Guid id)
        => await db.Documents.IgnoreQueryFilters().AsNoTracking().AnyAsync(d => d.Id == id && d.CompanyId == companyId);

    [Fact]
    public async Task เจ้าของ_ลบใบที่มีขั้นอนุมัติและลายเซ็น_พร้อมใบลูกที่เป็นร่าง_ไม่ชนFK_ไม่เหลือแถวลูก_auditทุกใบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        await ProductionFkShapeAsync(db);
        var s = await SeedAsync(db);
        // ใบเสนอราคาที่ลูกค้าเซ็นยอมรับ (ยังไม่ออกเลข) → ใบแจ้งหนี้ร่างที่แปลงไป (มีขั้นอนุมัติ/ลายเซ็นของตัวเองด้วย)
        var qt = await DocAsync(db, s, DocumentType.Quotation, "DRAFT-" + Guid.NewGuid().ToString("N"), null, DocumentStatus.WaitingApproval);
        var inv = await DocAsync(db, s, DocumentType.Invoice, "DRAFT-" + Guid.NewGuid().ToString("N"), qt.Id, DocumentStatus.Draft);
        await ChildrenAsync(db, s, qt);
        await ChildrenAsync(db, s, inv);

        using (var preview = DbTestDatabase.TryCreateContext()!)
        {
            var plan = await Svc(preview).PreviewPurgeAsync(s.CompanyId, qt.Id, cascadeDescendants: true, false, null);
            Assert.True(plan.Allowed);
            Assert.Equal(new[] { inv.Id, qt.Id }, plan.DeleteOrder);   // ลูกก่อน รากท้ายสุด
        }

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).PurgeDocumentAsync(s.CompanyId, qt.Id, null, false, null, cascadeDescendants: true);

        using var check = DbTestDatabase.TryCreateContext()!;
        var ids = new[] { qt.Id, inv.Id };
        Assert.False(await ExistsAsync(check, s.CompanyId, qt.Id));
        Assert.False(await ExistsAsync(check, s.CompanyId, inv.Id));
        Assert.False(await check.DocumentLines.IgnoreQueryFilters().AnyAsync(l => ids.Contains(l.DocumentId)));
        Assert.False(await check.DocumentApprovals.IgnoreQueryFilters().AnyAsync(a => a.CompanyId == s.CompanyId));
        Assert.False(await check.DocumentSignatures.IgnoreQueryFilters().AnyAsync(a => a.CompanyId == s.CompanyId));
        Assert.False(await check.DocumentRevisions.IgnoreQueryFilters().AnyAsync(a => a.CompanyId == s.CompanyId));
        Assert.False(await check.PaymentReminders.IgnoreQueryFilters().AnyAsync(a => a.CompanyId == s.CompanyId));
        Assert.False(await check.SignEventOutbox.IgnoreQueryFilters().AnyAsync(a => a.CompanyId == s.CompanyId));
        // เช็คล่วงหน้าเป็นหลักฐานของจริง (ไม่ใช่ของเอกสาร) — คงไว้ ตัดการอ้างอิง
        var pdcs = await check.PostDatedChecks.IgnoreQueryFilters().AsNoTracking().Where(p => p.CompanyId == s.CompanyId).ToListAsync();
        Assert.Equal(2, pdcs.Count);
        Assert.All(pdcs, p => Assert.Null(p.SourceDocumentId));
        // audit hash chain ต่อใบที่ลบ (2 ใบ) — ระบุใบที่สั่งลบ
        var audits = await check.AuditLogs.AsNoTracking()
            .Where(a => a.CompanyId == s.CompanyId && a.EntityType == "Document" && a.Action == AuditAction.Delete).ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.All(audits, a => Assert.Contains(qt.Id.ToString(), a.NewValues));
        Assert.All(audits, a => Assert.NotNull(a.RowHash));
    }

    [Fact]
    public async Task ทิศตรงข้าม_สายที่มีใบกำกับออกแล้ว_ลบพร้อมเอกสารที่เกี่ยวข้องถูกปฏิเสธพร้อมรายชื่อ_ไม่มีอะไรถูกลบ_ลบเฉพาะใบนี้ยังได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await ProductionFkShapeAsync(db);
        var s = await SeedAsync(db);
        var qt = await DocAsync(db, s, DocumentType.Quotation, "DRAFT-" + Guid.NewGuid().ToString("N"), null, DocumentStatus.Draft);
        var tivNo = "TIV-PRG-" + Guid.NewGuid().ToString("N")[..6];
        var tiv = await DocAsync(db, s, DocumentType.TaxInvoice, tivNo, qt.Id, DocumentStatus.Approved);
        await ChildrenAsync(db, s, qt);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
                Svc(req).PurgeDocumentAsync(s.CompanyId, qt.Id, null, true, "ทดสอบ", cascadeDescendants: true));
            Assert.Equal(DocumentCascadePurgePolicy.RuleDescendantEffective, ex.RuleCode);
            Assert.Equal(409, ex.StatusCode);
            Assert.Contains(tivNo, ex.Message);
            Assert.Contains("ยกเลิก", ex.Message);       // ทางไปต่อ
        }
        using (var mid = DbTestDatabase.TryCreateContext()!)
        {
            Assert.True(await ExistsAsync(mid, s.CompanyId, qt.Id));
            Assert.True(await ExistsAsync(mid, s.CompanyId, tiv.Id));
            Assert.True(await mid.DocumentSignatures.IgnoreQueryFilters().AnyAsync(a => a.DocumentId == qt.Id));
        }

        // ลบเฉพาะใบนี้ (พฤติกรรมเดิม): ใบกำกับคงอยู่ ตัดการอ้างอิง
        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).PurgeDocumentAsync(s.CompanyId, qt.Id, null, false, null);
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await ExistsAsync(check, s.CompanyId, qt.Id));
        var kept = await check.Documents.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.Id == tiv.Id && d.CompanyId == s.CompanyId);
        Assert.Null(kept.RelatedDocumentId);
        Assert.Equal(DocumentStatus.Approved, kept.Status);
    }

    [Fact]
    public async Task ทิศตรงข้าม_ใบออกแล้วในช่วงเก็บรักษา_forceไม่มีเหตุผลถูกปฏิเสธ_มีเหตุผลลบได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var invNo = "INV-PRG-" + Guid.NewGuid().ToString("N")[..6];
        var inv = await DocAsync(db, s, DocumentType.Invoice, invNo, null, DocumentStatus.Voided);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var noForce = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).PurgeDocumentAsync(s.CompanyId, inv.Id, null, false, null));
            Assert.Equal(DocumentCascadePurgePolicy.RuleRetention, noForce.RuleCode);
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var noReason = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).PurgeDocumentAsync(s.CompanyId, inv.Id, null, true, "  "));
            Assert.Equal(DocumentCascadePurgePolicy.RuleReasonRequired, noReason.RuleCode);
        }
        using (var mid = DbTestDatabase.TryCreateContext()!)
            Assert.True(await ExistsAsync(mid, s.CompanyId, inv.Id));

        using (var req = DbTestDatabase.TryCreateContext()!)
            await Svc(req).PurgeDocumentAsync(s.CompanyId, inv.Id, null, true, "ข้อมูลทดสอบ");
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await ExistsAsync(check, s.CompanyId, inv.Id));
        var audit = await check.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.CompanyId == s.CompanyId && a.EntityType == "Document" && a.EntityId == inv.Id.ToString());
        Assert.Contains("ข้อมูลทดสอบ", audit.NewValues);
    }

    [Fact]
    public async Task B1_ใบเสร็จหลักฐานการชำระที่eTaxส่งกรมสรรพากรแล้ว_ลบใบแม่ถูกปฏิเสธ409_ไม่มีอะไรถูกลบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv = await DocAsync(db, s, DocumentType.Invoice, "INV-PRG-" + Guid.NewGuid().ToString("N")[..6], null, DocumentStatus.Voided);
        var recNo = "RE-PRG-" + Guid.NewGuid().ToString("N")[..6];
        var rec = await DocAsync(db, s, DocumentType.Receipt, recNo, inv.Id, DocumentStatus.Voided);
        var recRow = await db.Documents.SingleAsync(d => d.Id == rec.Id && d.CompanyId == s.CompanyId);
        recRow.IsSettlementReceipt = true;
        db.EtaxInvoices.Add(new EtaxInvoice
        {
            CompanyId = s.CompanyId, DocumentId = rec.Id, EtaxRefNumber = recNo, Status = EtaxStatus.Submitted, SubmittedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Svc(req).PurgeDocumentAsync(s.CompanyId, inv.Id, null, true, "ทดสอบ"));
            Assert.Equal(409, ex.StatusCode);
            Assert.Equal(DocumentCascadePurgePolicy.RuleEtaxReached, ex.RuleCode);
            Assert.Contains(recNo, ex.Message);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.True(await ExistsAsync(check, s.CompanyId, inv.Id));
        Assert.True(await ExistsAsync(check, s.CompanyId, rec.Id));
        Assert.True(await check.EtaxInvoices.IgnoreQueryFilters().AnyAsync(e => e.DocumentId == rec.Id && e.CompanyId == s.CompanyId));
    }

    [Fact]
    public async Task N7_ใบอยู่ในรายงานภาษีที่ยื่นแล้ว_ITaxServiceตอบล็อก_409_ไม่มีอะไรถูกลบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var s = await SeedAsync(db);
        var inv = await DocAsync(db, s, DocumentType.Invoice, "INV-PRG-" + Guid.NewGuid().ToString("N")[..6], null, DocumentStatus.Voided);

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            // ITaxService ปลอมที่ตอบ true ทุกคำถาม bool (IsDocumentFilingLockedAsync = ล็อกแล้ว)
            var svc = new DocumentService(req, new AccountingService(req), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!,
                taxService: RecurringTransactionDbTests.AllowAll<Accounting.Services.Interfaces.ITaxService>.Create());
            var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => svc.PurgeDocumentAsync(s.CompanyId, inv.Id, null, true, "ทดสอบ"));
            Assert.Equal(409, ex.StatusCode);
            Assert.Equal(DocumentCascadePurgePolicy.RuleFilingLocked, ex.RuleCode);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.True(await ExistsAsync(check, s.CompanyId, inv.Id));
    }

    public sealed class FkRow
    {
        public string Table { get; set; } = "";
        public string Column { get; set; } = "";
        public string Referenced { get; set; } = "";
    }

    [Fact]
    public async Task ความครบ_ทุกFKแบบRESTRICTหรือNOACTIONที่ชี้เอกสาร_อยู่ในลำดับลบถาวร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        await ProductionFkShapeAsync(db);
        var rows = await db.Database.SqlQueryRaw<FkRow>(@"
SELECT trim(both '""' from c.conrelid::regclass::text) AS ""Table"", a.attname AS ""Column"",
       trim(both '""' from c.confrelid::regclass::text) AS ""Referenced""
FROM pg_constraint c
JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = c.conkey[1]
WHERE c.contype = 'f' AND c.confdeltype IN ('r', 'a')
  AND c.confrelid IN ('""Documents""'::regclass, '""DocumentApprovals""'::regclass, '""Payments""'::regclass)").ToListAsync();
        Assert.NotEmpty(rows);   // อย่างน้อย DocumentLines / Payments / ลายเซ็น — ว่าง = คิวรีผิด (ด่านที่ผ่านตลอดกาล)
        var missing = rows.Where(r => !DocumentPurgeOrder.Covers(r.Table, r.Column, r.Referenced))
            .Select(r => $"{r.Table}.{r.Column} → {r.Referenced}").ToList();
        Assert.True(missing.Count == 0, "FK ที่ purge ไม่ได้จัดการ (เพิ่มใน Helpers/DocumentPurgeOrder): " + string.Join(", ", missing));
        Assert.Contains(rows, r => r.Table == "DocumentSignatures" && r.Referenced == "DocumentApprovals");
    }
}
