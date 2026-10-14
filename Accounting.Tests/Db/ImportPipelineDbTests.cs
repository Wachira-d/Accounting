using System.Text;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.FileAttachment;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Import;
using Accounting.Services.Import;
using Accounting.Services.Import.Targets;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 205 ทีม B (IMPORT_ETL_REVIEW §2.4) — engine ศูนย์นำเข้าบน PostgreSQL จริง: savepoint ต่อ chunk + ลงซ้ำทีละกลุ่ม (แถวผิดไม่ทิ้งของครึ่ง ๆ) ·
/// AllOrNothing ไม่ลงสักแถว · commit ซ้ำไม่ลงซ้ำ (idempotent) · สอง worker หนึ่ง batch ทำได้เครื่องเดียว (JobLock) · kill-switch (AI ล่ม) ยังลงครบ ·
/// writer เดิม: ที่ดินผ่านไฟล์นำเข้าไม่คิดค่าเสื่อม (FixedAssetImportMethod) · รับชำระจากไฟล์ลง JE (DocumentService) · วันที่ พ.ศ./ยอดคั่นหลักพันไม่ขึ้นกับ culture
/// <para>เทสต์ pure จับไม่ได้ (savepoint/ธุรกรรม/advisory lock อยู่ในฐานจริงเท่านั้น) — หลักฐานคือ job db-test</para>
/// </summary>
[Trait("Category", "Db")]
public class ImportPipelineDbTests
{
    private readonly ITestOutputHelper _out;
    public ImportPipelineDbTests(ITestOutputHelper output) => _out = output;

    // ───────────────────────── ของปลอมที่ไม่แตะธุรกิจ ─────────────────────────

    private sealed class NullErrorLog : IErrorLogService
    {
        public Task LogErrorAsync(Exception exception, string source, string? userId = null) => Task.CompletedTask;
    }

    /// <summary>ที่เก็บไฟล์ในหน่วยความจำ (ไฟล์ต้นทางของ batch) — ใช้ร่วมระหว่าง context ของเทสต์เดียวกัน</summary>
    private sealed class MemFiles : IFileAttachmentService
    {
        private readonly Dictionary<Guid, (Guid CompanyId, FileAttachmentResponse Meta, byte[] Bytes)> _files = new();

        public Task<FileAttachmentResponse> UploadAsync(Guid companyId, string entityType, Guid entityId, string fileName, string originalFileName,
            string contentType, long fileSize, string storagePath, Guid uploadedByUserId) => throw new NotSupportedException();

        public Task<FileAttachmentResponse> UploadBytesAsync(Guid companyId, string entityType, Guid entityId, string originalFileName,
            string contentType, byte[] content, Guid uploadedByUserId)
        {
            var meta = new FileAttachmentResponse(Guid.NewGuid(), originalFileName, originalFileName, contentType, content.LongLength, "mem",
                null, entityType, entityId, "tester", DateTime.UtcNow);
            _files[meta.Id] = (companyId, meta, content);
            return Task.FromResult(meta);
        }

        public Task<List<FileAttachmentResponse>> GetByEntityAsync(Guid companyId, string entityType, Guid entityId)
            => Task.FromResult(_files.Values.Where(f => f.CompanyId == companyId && f.Meta.EntityId == entityId).Select(f => f.Meta).ToList());

        public Task<FileAttachmentResponse?> GetByIdAsync(Guid companyId, Guid attachmentId)
            => Task.FromResult(_files.TryGetValue(attachmentId, out var f) && f.CompanyId == companyId ? f.Meta : null);

        public Task<byte[]?> ReadBytesAsync(Guid companyId, Guid attachmentId, CancellationToken ct = default)
            => Task.FromResult(_files.TryGetValue(attachmentId, out var f) && f.CompanyId == companyId ? f.Bytes : null);

        public Task DeleteAsync(Guid companyId, Guid attachmentId, bool keepPhysicalFile = false)
        {
            _files.Remove(attachmentId);
            return Task.CompletedTask;
        }
    }

    /// <summary>คลังแม่แบบว่าง (ทีม A เป็นเจ้าของตัวจริง) — เทสต์นี้ไม่ทดสอบแม่แบบ</summary>
    private sealed class NoTemplates : IImportMappingTemplateStore
    {
        public Task<ImportMappingTemplate?> FindAsync(Guid companyId, string entityType, string headerSignature, CancellationToken ct = default)
            => Task.FromResult<ImportMappingTemplate?>(null);
        public Task<IReadOnlyList<ImportTemplateDto>> ListAsync(Guid companyId, string? entityType, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ImportTemplateDto>>(Array.Empty<ImportTemplateDto>());
        public Task<ImportTemplateDto?> GetAsync(Guid companyId, Guid templateId, CancellationToken ct = default)
            => Task.FromResult<ImportTemplateDto?>(null);
        public Task<ImportTemplateDto> SaveFromBatchAsync(Guid companyId, Guid batchId, string? name, bool confirmedByUser, ImportActor actor,
            CancellationToken ct = default) => throw new NotImplementedException("ไม่ได้ทดสอบแม่แบบในชุดนี้");
        public Task<ImportTemplateDto?> UpdateAsync(Guid companyId, Guid templateId, ImportTemplateUpsertRequest request, ImportActor actor,
            CancellationToken ct = default) => Task.FromResult<ImportTemplateDto?>(null);
        public Task<bool> DeleteAsync(Guid companyId, Guid templateId, ImportActor actor, CancellationToken ct = default) => Task.FromResult(false);
        public Task MarkUsedAsync(Guid companyId, Guid templateId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>kill-switch: provider ล่ม — ทุกคำขอโยน (ระบบต้องใช้ชั้น local ต่อเงียบ ๆ)</summary>
    private sealed class DeadAi : IAiOrchestrator
    {
        public int Calls;
        public Task<AiResponse> AskAsync(AiRequest request, CancellationToken ct = default)
        {
            Calls++;
            throw new HttpRequestException("provider ปิด (kill-switch)");
        }
        public Task<AiResponse> AskAndRecordChoiceAsync(AiRequest request, string userChoice, CancellationToken ct = default) => AskAsync(request, ct);
        public Task RecordUserChoiceAsync(Guid feedbackId, string chosenAnswer, bool acceptedAi, CancellationToken ct = default,
            UserChoiceSource source = UserChoiceSource.Implicit) => Task.CompletedTask;
    }

    private static ImportPipelineService Pipeline(AccountingDbContext db, MemFiles files, IAiOrchestrator? ai = null)
    {
        var legacy = new ImportExportService(db, new NullErrorLog());
        var writer = new ImportRowWriter(legacy);
        var resolver = new ImportEntityResolver(db, new DuplicateDetector(db), ai);
        var registry = new ImportTargetRegistry(new IImportTarget[]
        {
            new ContactsImportTarget(db, writer, resolver),
            new ChartOfAccountsImportTarget(db, writer, resolver),
            new JournalEntriesImportTarget(db, writer, resolver),
        });
        return new ImportPipelineService(db, files, registry, new NoTemplates());
    }

    private static readonly ImportActor Actor = new(Guid.NewGuid(), "ผู้ทดสอบนำเข้า", "127.0.0.1");

    private static async Task<Guid> SeedCompanyAsync(AccountingDbContext db, params string[] accountCodes)
    {
        var company = new Company { Name = "บริษัททดสอบนำเข้า", TaxId = "0105556000001" };
        db.Companies.Add(company);
        foreach (var code in accountCodes)
            db.ChartOfAccounts.Add(new ChartOfAccount
            {
                CompanyId = company.Id, AccountCode = code, AccountName = "บัญชี " + code, Level = 4, IsActive = true,
                AccountType = code[0] switch { '1' => AccountType.Asset, '2' => AccountType.Liability, '4' => AccountType.Revenue, _ => AccountType.Expense },
            });
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static ImportUploadedFile Csv(string name, string text) => new(name, "text/csv", Encoding.UTF8.GetBytes(text));

    /// <summary>สมุดรายวันสองใบ (ไฟล์จริงแบบไทย: แถวชื่อรายงาน · วันที่ พ.ศ. · ยอดคั่นหลักพัน · แถวรวมท้าย) — ใบ B มีบัญชี 9999 ที่ไม่มีในผัง</summary>
    private const string JournalCsv =
        "รายงานสมุดรายวันนำเข้า,,,,,\n"
        + "วันที่,คำอธิบาย,รหัสบัญชี,เดบิต,เครดิต,อ้างอิง\n"
        + "05/08/2569,ค่าเช่า ส.ค.,5210,\"1,234.50\",,JV-A\n"
        + "05/08/2569,ค่าเช่า ส.ค.,1120,,\"1,234.50\",JV-A\n"
        + "06/08/2569,ค่าไฟ,5220,500.00,,JV-B\n"
        + "06/08/2569,ค่าไฟ,9999,,500.00,JV-B\n"
        + "รวม,,,\"1,734.50\",\"1,734.50\",\n";

    private static ImportCommitOptions Ack(ImportCommitMode mode, ImportBatchDetailDto detail)
        => new(mode, detail.WarningsToAcknowledge.Select(w => w.Code).ToList(), SaveTemplate: false);

    // ───────────────────────── SharedTransaction: savepoint + ลงซ้ำทีละกลุ่ม ─────────────────────────

    [Fact]
    public async Task ลงเฉพาะแถวที่ผ่าน_ใบที่มีบรรทัดผิด_ไม่ทิ้งใบครึ่งใบ_ใบอื่นลงครบ_และลงซ้ำไม่ซ้ำ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedCompanyAsync(db, "1120", "5210", "5220");
        var files = new MemFiles();
        var detail = await Pipeline(db, files).CreateAsync(cid, Csv("journal.csv", JournalCsv), new ImportCreateBatchRequest("journal-entries"), Actor);

        Assert.Equal(ImportSessionStatus.Staged, detail.Summary.Status);
        Assert.Equal(1, detail.Source.HeaderRowIndex);                       // ข้ามแถวชื่อรายงาน
        Assert.Equal(1, detail.Summary.ExcludedCount);                       // แถว "รวม" ถูกพักเป็นตัดออก (ไม่หายเงียบ)
        Assert.Contains(detail.WarningsToAcknowledge, w => w.Code == "date-order-assumed");

        // ยังไม่รับทราบคำเตือน ⇒ ไม่มีแถวพร้อมลง (บล็อกพร้อมทางไปต่อ)
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Pipeline(db, files).CommitAsync(cid, detail.Summary.Id, new ImportCommitOptions(SaveTemplate: false), Actor));

        ImportCommitResultDto result;
        using (var req = DbTestDatabase.TryCreateContext()!)
            result = await Pipeline(req, files).CommitAsync(cid, detail.Summary.Id, Ack(ImportCommitMode.GoodRowsOnly, detail), Actor);
        Assert.Equal(2, result.Committed);
        Assert.Equal(2, result.Failed);
        Assert.Equal(ImportSessionStatus.PartiallyCompleted, result.Status);
        Assert.All(result.Failures, f => Assert.False(string.IsNullOrWhiteSpace(f.Error)));   // ทุกแถวที่ล้มมีเหตุผลภาษาไทย

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var jes = await check.JournalEntries.AsNoTracking().Include(j => j.Lines).Where(j => j.CompanyId == cid).ToListAsync();
            var je = Assert.Single(jes);                                       // ใบ B (บรรทัด 9999 ผิด) ไม่ถูกลงครึ่งใบ
            Assert.Equal(new DateTime(2026, 8, 5), je.EntryDate.Date);         // "05/08/2569" = 5 ส.ค. 2026 ไม่ใช่ 8 พ.ค.
            Assert.Equal(1234.50m, je.TotalDebit);                             // "1,234.50" ไม่ขึ้นกับ culture
            Assert.Equal(2, je.Lines.Count);
            var failed = await check.ImportStagedRows.AsNoTracking()
                .Where(r => r.SessionId == detail.Summary.Id && r.CompanyId == cid && r.Status == ImportRowStatus.Failed).ToListAsync();
            Assert.Equal(2, failed.Count);
            Assert.Contains(failed, r => (r.CommitError ?? "").Contains("9999"));
        }

        // แก้ผัง (เพิ่ม 9999) แล้วลงซ้ำ ⇒ ใบ A ไม่ถูกส่งซ้ำ · ใบ B ลงครบ
        using (var fix = DbTestDatabase.TryCreateContext()!)
        {
            fix.ChartOfAccounts.Add(new ChartOfAccount { CompanyId = cid, AccountCode = "9999", AccountName = "พักรอตรวจ", AccountType = AccountType.Liability, Level = 4 });
            await fix.SaveChangesAsync();
        }
        using (var req2 = DbTestDatabase.TryCreateContext()!)
        {
            var again = await Pipeline(req2, files).CommitAsync(cid, detail.Summary.Id, Ack(ImportCommitMode.GoodRowsOnly, detail), Actor);
            Assert.Equal(2, again.Committed);
            Assert.Equal(ImportSessionStatus.Completed, again.Status);
        }
        using (var check2 = DbTestDatabase.TryCreateContext()!)
        {
            var jes = await check2.JournalEntries.AsNoTracking().Include(j => j.Lines).Where(j => j.CompanyId == cid).ToListAsync();
            Assert.Equal(2, jes.Count);
            Assert.All(jes, j => Assert.Equal(2, j.Lines.Count));              // ไม่มีบรรทัดเงาจากการลงซ้ำ
            Assert.True(await check2.AuditLogs.AnyAsync(a => a.CompanyId == cid && a.EntityType == "ImportBatch"
                && a.EntityId == detail.Summary.Id.ToString() && a.NewValues!.Contains("IMPORT-COMMIT")));
        }
    }

    [Fact]
    public async Task ลงทั้งหมดหรือไม่ลงเลย_มีแถวล้ม_ไม่ลงสักแถว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db, "1120", "5210", "5220");
        var files = new MemFiles();
        var detail = await Pipeline(db, files).CreateAsync(cid, Csv("journal.csv", JournalCsv), new ImportCreateBatchRequest("journal-entries"), Actor);

        ImportCommitResultDto result;
        using (var req = DbTestDatabase.TryCreateContext()!)
            result = await Pipeline(req, files).CommitAsync(cid, detail.Summary.Id, Ack(ImportCommitMode.AllOrNothing, detail), Actor);
        Assert.Equal(0, result.Committed);
        Assert.True(result.Failed > 0);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.False(await check.JournalEntries.AnyAsync(j => j.CompanyId == cid));          // ใบ A ที่ผ่านก็ไม่ลง
        Assert.False(await check.ImportStagedRows.AnyAsync(r => r.SessionId == detail.Summary.Id && r.CompanyId == cid
            && r.Status == ImportRowStatus.Committed));
        Assert.False(await check.AuditLogs.AnyAsync(a => a.CompanyId == cid && a.EntityType == "JournalEntry"));   // ไม่มีร่องรอยของสิ่งที่ไม่เกิด
        var session = await check.SmartImportSessions.AsNoTracking().SingleAsync(s => s.Id == detail.Summary.Id && s.CompanyId == cid);
        Assert.Equal(ImportSessionStatus.Staged, session.Status);
    }

    // ───────────────────────── worker: สองเครื่องหนึ่ง batch ─────────────────────────

    [Fact]
    public async Task สอง_worker_หนึ่ง_batch_ทำได้เครื่องเดียว()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db);
        var files = new MemFiles();
        var csv = new StringBuilder("ชื่อ,อีเมล\n");
        for (var i = 1; i <= ImportPipelineService.InlineRowLimit + 5; i++) csv.Append("ลูกค้าทดสอบ ").Append(i).Append(",c").Append(i).Append("@example.com\n");
        var detail = await Pipeline(db, files).CreateAsync(cid, Csv("big.csv", csv.ToString()), new ImportCreateBatchRequest("contacts"), Actor);
        Assert.Equal(ImportSessionStatus.Queued, detail.Summary.Status);           // ไฟล์ใหญ่ ⇒ คิว worker
        var batchId = detail.Summary.Id;

        using var dbA = DbTestDatabase.TryCreateContext()!;
        using var dbB = DbTestDatabase.TryCreateContext()!;
        var bRan = true;
        var aRan = await JobLock.RunExclusiveAsync(dbA, AdvisoryLockKey.ImportBatch, batchId.ToString(), async () =>
        {
            await Pipeline(dbA, files).ProcessQueuedAsync(cid, batchId, "worker-A");
            bRan = await JobLock.RunExclusiveAsync(dbB, AdvisoryLockKey.ImportBatch, batchId.ToString(), async () =>
            {
                await Pipeline(dbB, files).ProcessQueuedAsync(cid, batchId, "worker-B");
            }, null, cid);
        }, null, cid);
        Assert.True(aRan);
        Assert.False(bRan);

        using var check = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(ImportPipelineService.WorkerChunkRows, await check.ImportStagedRows.CountAsync(r => r.SessionId == batchId && r.CompanyId == cid));
        var s = await check.SmartImportSessions.AsNoTracking().SingleAsync(x => x.Id == batchId && x.CompanyId == cid);
        Assert.Equal(ImportPipelineService.WorkerChunkRows, s.ProcessedRows);
        Assert.Equal("worker-A", s.ClaimedBy);
        Assert.Equal(ImportSessionStatus.Queued, s.Status);
    }

    // ───────────────────────── kill-switch ─────────────────────────

    private static string TaxId(string twelve)
    {
        var sum = 0;
        for (var i = 0; i < 12; i++) sum += (twelve[i] - '0') * (13 - i);
        return twelve + ((11 - sum % 11) % 10).ToString();
    }

    [Fact]
    public async Task ปิด_AI_ทุกตัว_ผู้ติดต่อยังพักและลงครบ_ป้ายไม่อ้าง_AI()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db);
        db.Contacts.Add(new Contact { CompanyId = cid, Name = "ร้านสมใจการค้า", IsCustomer = true });
        db.Contacts.Add(new Contact { CompanyId = cid, Name = "ร้านสมใจการค้า2", IsCustomer = true });
        await db.SaveChangesAsync();
        var tax = TaxId("010555612345");
        var csv = "ชื่อ,เลขผู้เสียภาษี,สาขา,เป็นลูกค้า,อีเมล\n"
            + $"บริษัท สยามทดสอบ จำกัด,{tax},สำนักงานใหญ่,ใช่,siam@example.com\n"
            + "ร้านสมใจการค้า1,,,Y,\n";
        var ai = new DeadAi();
        var files = new MemFiles();
        var detail = await Pipeline(db, files, ai).CreateAsync(cid, Csv("contacts.csv", csv), new ImportCreateBatchRequest("contacts"), Actor);
        Assert.Equal(ImportSessionStatus.Staged, detail.Summary.Status);
        Assert.False(detail.ColumnMatchUsedAi);
        Assert.True(ai.Calls > 0);                                            // ถามครูจริง (ชื่อกำกวม) แต่ provider ล่ม

        var page = await Pipeline(db, files, ai).GetRowsAsync(cid, detail.Summary.Id, new ImportRowQuery());
        Assert.All(page.Rows, r => Assert.False(r.UsedAi));
        Assert.All(page.Rows, r => Assert.NotEqual(ImportRowStatus.Error, r.Status));

        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var res = await Pipeline(req, files, ai).CommitAsync(cid, detail.Summary.Id, Ack(ImportCommitMode.GoodRowsOnly, detail), Actor);
            Assert.Equal(2, res.Committed);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        var siam = await check.Contacts.AsNoTracking().SingleAsync(c => c.CompanyId == cid && c.Name == "บริษัท สยามทดสอบ จำกัด");
        Assert.Equal(tax, siam.TaxId);
        Assert.Equal("00000", siam.BranchCode);                               // "สำนักงานใหญ่" ⇒ 00000
        Assert.True(siam.IsCustomer);                                         // "ใช่" ⇒ true (เดิม bool.TryParse = false)
        var somjai = await check.Contacts.AsNoTracking().SingleAsync(c => c.CompanyId == cid && c.Name == "ร้านสมใจการค้า1");
        Assert.True(somjai.IsCustomer);                                       // "Y" ⇒ true
        Assert.Equal(3, await check.Contacts.CountAsync(c => c.CompanyId == cid && c.Name.StartsWith("ร้านสมใจ")));   // ชื่อคล้ายไม่ถูกรวมเอง
    }

    // ───────────────────────── writer เดิม (เส้นไฟล์เดิม · ImportAsync) ─────────────────────────

    [Fact]
    public async Task ที่ดินผ่านไฟล์นำเข้าทั่วไป_ไม่คิดค่าเสื่อม_สินทรัพย์อื่นยังคิด()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db);
        var svc = new ImportExportService(db, new NullErrorLog(), fixedAssets: new FixedAssetService(db));
        var result = await svc.ImportAsync(cid, new ImportRequest("fixed-assets", "csv", true, null, new List<Dictionary<string, string>>
        {
            new() { ["AssetCode"] = "FA-L1", ["Name"] = "ที่ดินโฉนด 123", ["Category"] = "ที่ดิน", ["PurchaseDate"] = "15/03/2567",
                ["PurchaseCost"] = "2,500,000.00", ["UsefulLifeMonths"] = "" },
            new() { ["AssetCode"] = "FA-C1", ["Name"] = "คอมพิวเตอร์", ["Category"] = "เครื่องใช้สำนักงาน", ["PurchaseDate"] = "2026-01-10",
                ["PurchaseCost"] = "36,000", ["UsefulLifeMonths"] = "36", ["DepreciationMethod"] = "StraightLine" },
        }), "tester");
        Assert.Equal(2, result.SuccessCount);

        using var check = DbTestDatabase.TryCreateContext()!;
        var land = await check.FixedAssets.AsNoTracking().SingleAsync(a => a.CompanyId == cid && a.AssetCode == "FA-L1");
        Assert.Equal(DepreciationMethod.None, land.DepreciationMethod);
        Assert.Equal(new DateTime(2024, 3, 15), land.PurchaseDate.Date);
        Assert.Equal(2_500_000m, land.PurchaseCost);
        Assert.False(await check.AssetDepreciations.AnyAsync(d => d.CompanyId == cid && d.FixedAssetId == land.Id && d.Amount > 0));
        var pc = await check.FixedAssets.AsNoTracking().SingleAsync(a => a.CompanyId == cid && a.AssetCode == "FA-C1");
        Assert.Equal(DepreciationMethod.StraightLine, pc.DepreciationMethod);
        Assert.True(await check.AssetDepreciations.AnyAsync(d => d.CompanyId == cid && d.FixedAssetId == pc.Id && d.Amount > 0));
    }

    [Fact]
    public async Task รับชำระจากไฟล์_ลงสมุดรายวันผ่าน_DocumentService_จ่ายเกินถูกปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db, "11110", "21210");
        var supplier = new Contact { CompanyId = cid, Name = "ผู้ขายทดสอบ", IsSupplier = true };
        db.Contacts.Add(supplier);
        var bill = new Document
        {
            CompanyId = cid, ContactId = supplier.Id, DocumentNumber = "PI-IMP-001", DocumentType = DocumentType.PurchaseInvoice,
            Status = DocumentStatus.Approved, DocumentDate = new DateTime(2026, 1, 10), SubTotal = 1000m, VatAmount = 70m,
            TotalAmount = 1070m, BalanceDue = 1070m,
        };
        db.Documents.Add(bill);
        await db.SaveChangesAsync();
        var docs = new DocumentService(db, new AccountingService(db), null!, null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);
        var svc = new ImportExportService(db, new NullErrorLog(), documents: docs);

        // ทิศตรงข้าม: ยอดเกินค้างชำระ ⇒ แถวล้มพร้อมเหตุผล ไม่มีอะไรลง
        var over = await svc.ImportAsync(cid, new ImportRequest("payments", "csv", true, null, new List<Dictionary<string, string>>
        {
            new() { ["DocumentNumber"] = "PI-IMP-001", ["PaymentDate"] = "20/01/2569", ["Amount"] = "2,000.00", ["PaymentMethod"] = "Cash" },
        }), "tester");
        Assert.Equal(0, over.SuccessCount);
        Assert.Contains("เกินยอดค้างชำระ", over.Errors.Single().ErrorMessage);

        var ok = await svc.ImportAsync(cid, new ImportRequest("payments", "csv", true, null, new List<Dictionary<string, string>>
        {
            new() { ["DocumentNumber"] = "PI-IMP-001", ["PaymentDate"] = "20/01/2569", ["Amount"] = "1,070.00", ["PaymentMethod"] = "Cash" },
        }), "tester");
        Assert.Equal(1, ok.SuccessCount);

        using var check = DbTestDatabase.TryCreateContext()!;
        var payment = await check.Payments.AsNoTracking().SingleAsync(p => p.CompanyId == cid && p.DocumentId == bill.Id);
        Assert.Equal(1070m, payment.Amount);
        Assert.Equal(new DateTime(2026, 1, 20), payment.PaymentDate.Date);
        var ap = await check.ChartOfAccounts.AsNoTracking().SingleAsync(a => a.CompanyId == cid && a.AccountCode == "21210");
        var apDebit = await (from l in check.JournalEntryLines.AsNoTracking()
                             join j in check.JournalEntries.AsNoTracking() on l.JournalEntryId equals j.Id
                             where j.CompanyId == cid && l.AccountId == ap.Id
                             select l.DebitAmount).SumAsync();
        Assert.Equal(1070m, apDebit);                                         // เจ้าหนี้ถูกตัดใน GL (เดิมแก้ PaidAmount ตรง · GL ไม่ขยับ)
        var doc = await check.Documents.AsNoTracking().SingleAsync(d => d.Id == bill.Id && d.CompanyId == cid);
        Assert.Equal(1070m, doc.PaidAmount);
    }

    [Fact]
    public async Task ไฟล์เดิม_วันที่พศ_และยอดคั่นหลักพัน_อ่านไม่ขึ้นกับ_culture()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db, "1120", "5210");
        var prev = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            // เครื่องที่ตั้ง en-US: เดิม DateTime.TryParse("05/08/2569") = 8 พ.ค. ค.ศ. 2569 · ตอนนี้ต้องได้ 5 ส.ค. 2026 ทุกเครื่อง
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("en-US");
            var svc = new ImportExportService(db, new NullErrorLog());
            var res = await svc.ImportAsync(cid, new ImportRequest("journal-entries", "csv", true, null, new List<Dictionary<string, string>>
            {
                new() { ["Date"] = "05/08/2569", ["Description"] = "ค่าเช่า", ["AccountCode"] = "5210", ["DebitAmount"] = "1,234.50", ["Reference"] = "R1" },
                new() { ["Date"] = "05/08/2569", ["Description"] = "ค่าเช่า", ["AccountCode"] = "1120", ["CreditAmount"] = "1,234.50", ["Reference"] = "R1" },
            }), "tester");
            Assert.Equal(2, res.SuccessCount);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prev; }

        using var check = DbTestDatabase.TryCreateContext()!;
        var je = await check.JournalEntries.AsNoTracking().SingleAsync(j => j.CompanyId == cid);
        Assert.Equal(new DateTime(2026, 8, 5), je.EntryDate.Date);
        Assert.Equal(1234.50m, je.TotalDebit);
        Assert.Equal(1234.50m, je.TotalCredit);
    }
}
