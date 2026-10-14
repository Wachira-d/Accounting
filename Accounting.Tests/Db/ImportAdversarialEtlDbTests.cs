using System.Reflection;
using System.Security.Claims;
using System.Text;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.FileAttachment;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Implementations.Import;
using Accounting.Services.Import;
using Accounting.Services.Import.Targets;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// ฝ่ายค้านรอบ ETL (2026-10-14 · IMPORT_ETL_REVIEW §6) บน PostgreSQL จริง — ทุกเทสต์ล็อกทั้ง "ใบที่พังกลับมาถูก" และ "ใบที่ถูกอยู่แล้วไม่ถูกแตะ"
/// <list type="bullet">
/// <item>P0-1: worker ลงข้อมูลที่ทุกแถวล้มต้องจบ (ไม่วนหยิบแถวที่ล้มซ้ำ) · audit IMPORT-COMMIT ครั้งเดียว · ผู้ใช้สั่งลงใหม่ = ลองแถวที่ล้มอีกครั้ง</item>
/// <item>P0-2: ใบสำคัญที่คร่อมขอบ chunk 500 แถวไม่ถูกตีว่าไม่ลงตัว · แก้บรรทัดเดียว ⇒ ทั้งใบถูกตรวจใหม่ · หน่วยที่สมาชิกยังไม่พร้อมไม่ถูกลงครึ่งใบ</item>
/// <item>P1-1: ย้อนการนำเข้าที่ต่อบรรทัดเข้าใบที่ผู้ใช้ทำเอง ⇒ ลบเฉพาะบรรทัดที่เพิ่ม (ใบของผู้ใช้อยู่ครบ) · ใบที่ชุดสร้างเองถูกลบทั้งใบ</item>
/// <item>P1-2: เอกสารที่มีแต่ชื่อผู้ติดต่อ ⇒ ชื่อตรงตัวไม่ซ้ำ = ผูก + คำเตือน · ชื่อคล้าย = บล็อกตั้งแต่ขั้นตรวจ + ผู้ใช้เลือกแล้วลงได้ · ทางไฟล์เดิมลงได้</item>
/// <item>P1-3: ย้อนผังบัญชีที่ถูกอ้าง (ยอดยกมา · บัญชีธนาคาร · บัญชีย่อยที่ไม่ได้มาจากชุดนี้) ⇒ บล็อกพร้อมเหตุผล · ที่ไม่ถูกอ้างย้อนได้</item>
/// <item>P1-5: แถว/ไฟล์แถวผิด/ไฟล์ต้นทางของทะเบียนพนักงาน ⇒ ไม่มี Pii.View เห็นค่าที่ปิดบัง · มี Pii.View เห็นค่าเต็ม</item>
/// </list>
/// </summary>
[Trait("Category", "Db")]
public class ImportAdversarialEtlDbTests
{
    private readonly ITestOutputHelper _out;
    public ImportAdversarialEtlDbTests(ITestOutputHelper output) => _out = output;

    // ───────────────────────── ของปลอมที่ไม่แตะธุรกิจ ─────────────────────────

    private sealed class NullErrorLog : IErrorLogService
    {
        public Task LogErrorAsync(Exception exception, string source, string? userId = null) => Task.CompletedTask;
    }

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
        public Task<ImportTemplateDto> SaveColumnsAsync(Guid companyId, string entityType, IReadOnlyList<string> headers,
            IReadOnlyList<ImportTemplateColumnDto> columns, string? sourceSystem, string? name, bool confirmedByUser, ImportActor actor,
            CancellationToken ct = default) => throw new NotImplementedException("ไม่ได้ทดสอบแม่แบบในชุดนี้");
        public Task<IReadOnlyList<ImportLearnedHeader>> GetLearnedHeadersAsync(Guid companyId, string? entityType, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ImportLearnedHeader>>(Array.Empty<ImportLearnedHeader>());
        public Task<bool> RecordColumnMatchChoiceAsync(Guid companyId, Guid feedbackId, IReadOnlyList<ImportColumnPlanDto> finalColumns,
            CancellationToken ct = default) => Task.FromResult(false);
    }

    /// <summary>สิทธิ์เฉพาะคีย์ที่ให้ (HasPermissionAsync) — ใช้แยก "มี/ไม่มี Pii.View"</summary>
    public class KeysOnly : DispatchProxy
    {
        public HashSet<string> Keys { get; set; } = new();

        public static IPermissionService Create(params string[] keys)
        {
            var p = DispatchProxy.Create<IPermissionService, KeysOnly>();
            ((KeysOnly)(object)p).Keys = new HashSet<string>(keys, StringComparer.Ordinal);
            return p;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IPermissionService.HasPermissionAsync))
                return Task.FromResult(args != null && args.Length > 2 && args[2] is string key && Keys.Contains(key));
            var rt = targetMethod?.ReturnType ?? typeof(void);
            if (rt == typeof(Task)) return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var arg = rt.GetGenericArguments()[0];
                var value = arg.IsValueType ? Activator.CreateInstance(arg) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(arg).Invoke(null, new[] { value });
            }
            return rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null;
        }
    }

    private static readonly ImportActor Actor = new(Guid.NewGuid(), "ผู้ทดสอบฝ่ายค้าน", "127.0.0.1");

    private static ImportPipelineService Pipeline(AccountingDbContext db, MemFiles files)
    {
        var writer = new ImportRowWriter(new ImportExportService(db, new NullErrorLog()));
        var resolver = new ImportEntityResolver(db, new DuplicateDetector(db));
        var registry = new ImportTargetRegistry(new IImportTarget[]
        {
            new ContactsImportTarget(db, writer, resolver),
            new ChartOfAccountsImportTarget(db, writer, resolver),
            new JournalEntriesImportTarget(db, writer, resolver),
            new EmployeesImportTarget(db, writer, resolver),
        });
        return new ImportPipelineService(db, files, registry, new NoTemplates());
    }

    private static async Task<Guid> SeedCompanyAsync(AccountingDbContext db, params string[] accountCodes)
    {
        var company = new Company { Name = "บริษัทฝ่ายค้านนำเข้า " + Guid.NewGuid().ToString("N")[..6], TaxId = "0105556000001", IsVatRegistered = true };
        db.Companies.Add(company);
        foreach (var code in accountCodes)
            db.ChartOfAccounts.Add(new ChartOfAccount
            {
                CompanyId = company.Id, AccountCode = code, AccountName = "บัญชี " + code, Level = 4, IsActive = true,
                AccountType = code[0] switch { '1' => AccountType.Asset, '2' => AccountType.Liability, '3' => AccountType.Equity, '4' => AccountType.Revenue, _ => AccountType.Expense },
            });
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static ImportUploadedFile Csv(string name, string text) => new(name, "text/csv", Encoding.UTF8.GetBytes(text));

    private const string JeHeader = "วันที่,คำอธิบาย,รหัสบัญชี,เดบิต,เครดิต,อ้างอิง\n";

    private static ImportCommitOptions Ack(ImportBatchDetailDto detail, params string[] except)
        => new(ImportCommitMode.GoodRowsOnly, detail.WarningsToAcknowledge.Select(w => w.Code).Where(c => !except.Contains(c)).ToList(), SaveTemplate: false);

    private static Task<List<ImportStagedRow>> RowsAsync(AccountingDbContext db, Guid cid, Guid batchId)
        => db.ImportStagedRows.AsNoTracking().Where(r => r.SessionId == batchId && r.CompanyId == cid).OrderBy(r => r.RowNo).ToListAsync();

    // ═════════════════════════════ P0-1 ═════════════════════════════

    [Fact]
    public async Task P0_1_worker_ลงข้อมูลที่ทุกแถวล้ม_จบใน2รอบ_ไม่วนหยิบแถวที่ล้มซ้ำ_audit_ครั้งเดียว_แล้วผู้ใช้สั่งลงใหม่ลงครบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await SeedCompanyAsync(db, "1120", "5210");
        var files = new MemFiles();
        // 255 ใบ × 2 บรรทัด = 510 แถว (> 500 แถวต่อรอบ worker) · ทุกใบมีบัญชี 9999 ที่ไม่มีในผัง ⇒ ทุกแถวล้มตอนลง
        var csv = new StringBuilder(JeHeader);
        for (var i = 1; i <= 255; i++)
            csv.Append($"25/08/2569,ค่าใช้จ่ายงวด {i},5210,100.00,,JV-{i:000}\n25/08/2569,ค่าใช้จ่ายงวด {i},9999,,100.00,JV-{i:000}\n");
        var detail = await Pipeline(db, files).CreateAsync(cid, Csv("je-510.csv", csv.ToString()), new ImportCreateBatchRequest("journal-entries"), Actor);
        Assert.Equal(ImportSessionStatus.Staged, detail.Summary.Status);
        var batchId = detail.Summary.Id;

        // จำลองงานเบื้องหลัง: คำสั่งลงอยู่ในคิว (แบบไฟล์ใหญ่) แล้ว worker ทำทีละ 500 แถว
        using (var q = DbTestDatabase.TryCreateContext()!)
        {
            var s = await q.SmartImportSessions.SingleAsync(x => x.Id == batchId && x.CompanyId == cid);
            var st = ImportJson.Deserialize<ImportBatchState>(s.OptionsJson)!;
            st.PendingCommit = Ack(detail);
            st.PendingActor = Actor;
            s.OptionsJson = ImportJson.Serialize(st);
            s.Status = ImportSessionStatus.Queued;
            s.Phase = ImportBatchPhase.Commit;
            await q.SaveChangesAsync();
        }
        var rounds = 0;
        var done = false;
        while (!done && rounds < 6)
        {
            rounds++;
            using var w = DbTestDatabase.TryCreateContext()!;
            done = await Pipeline(w, files).ProcessQueuedAsync(cid, batchId, "worker-test");
        }
        Assert.True(done, "worker ต้องจบ (เดิมวนหยิบแถวที่ล้มซ้ำไม่สิ้นสุด)");
        Assert.Equal(2, rounds);

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            var rows = await RowsAsync(check, cid, batchId);
            Assert.Equal(510, rows.Count);
            Assert.All(rows, r => Assert.Equal(ImportRowStatus.Failed, r.Status));
            Assert.All(rows, r => Assert.False(string.IsNullOrWhiteSpace(r.CommitError)));
            Assert.False(await check.JournalEntries.AnyAsync(j => j.CompanyId == cid));
            var s = await check.SmartImportSessions.AsNoTracking().SingleAsync(x => x.Id == batchId && x.CompanyId == cid);
            Assert.Equal(ImportSessionStatus.Failed, s.Status);
            Assert.Equal(0, s.SkippedCount);                                  // แถวที่ล้ม ≠ ข้าม (เดิมนับซ้ำทุกรอบ)
            Assert.Equal(1, await check.AuditLogs.CountAsync(a => a.CompanyId == cid && a.EntityType == "ImportBatch"
                && a.EntityId == batchId.ToString() && a.NewValues!.Contains("IMPORT-COMMIT")));
        }

        // ทิศตรงข้าม: แก้ผัง (เพิ่ม 9999) แล้วผู้ใช้สั่งลงใหม่ ⇒ แถวที่ล้มกลับมาลงได้ครบ (CommitAsync คืนสถานะตรวจให้ก่อน)
        using (var fix = DbTestDatabase.TryCreateContext()!)
        {
            fix.ChartOfAccounts.Add(new ChartOfAccount { CompanyId = cid, AccountCode = "9999", AccountName = "พักรอตรวจ", AccountType = AccountType.Liability, Level = 4, IsActive = true });
            await fix.SaveChangesAsync();
        }
        using (var req = DbTestDatabase.TryCreateContext()!)
        {
            var res = await Pipeline(req, files).CommitAsync(cid, batchId, Ack(detail), Actor);
            Assert.Equal(510, res.Committed);
            Assert.Equal(ImportSessionStatus.Completed, res.Status);
        }
        using var final = DbTestDatabase.TryCreateContext()!;
        Assert.Equal(255, await final.JournalEntries.CountAsync(j => j.CompanyId == cid));
        Assert.Equal(2, await final.AuditLogs.CountAsync(a => a.CompanyId == cid && a.EntityType == "ImportBatch"
            && a.EntityId == batchId.ToString() && a.NewValues!.Contains("IMPORT-COMMIT")));   // หนึ่งครั้งต่อการสั่งลง
    }

    // ═════════════════════════════ P0-2 ═════════════════════════════

    [Fact]
    public async Task P0_2_ใบสำคัญคร่อมขอบ_chunk_500_แถว_ไม่ถูกตีว่าไม่ลงตัว_ใบที่ไม่ลงตัวจริงยังถูกจับ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db, "1120", "5210", "5220");
        var csv = new StringBuilder(JeHeader);
        for (var i = 1; i <= 249; i++)   // แถว 1–498
            csv.Append($"25/08/2569,ค่าเช่า {i},5210,100.00,,JV-{i:000}\n25/08/2569,ค่าเช่า {i},1120,,100.00,JV-{i:000}\n");
        // แถว 499–502: ใบเดียว 4 บรรทัด คร่อมขอบ 500 (สองบรรทัดแรกเดบิตล้วน · สองบรรทัดหลังเครดิตล้วน) — ทั้งใบลงตัว
        csv.Append("25/08/2569,ปิดงวดสิงหาคม,5210,100.00,,JV-CLOSE\n25/08/2569,ปิดงวดสิงหาคม,5220,50.00,,JV-CLOSE\n");
        csv.Append("25/08/2569,ปิดงวดสิงหาคม,1120,,75.00,JV-CLOSE\n25/08/2569,ปิดงวดสิงหาคม,1120,,75.00,JV-CLOSE\n");
        // แถว 503–504: ใบที่ไม่ลงตัวจริง (ทิศตรงข้าม — ด่านต้องยังจับได้)
        csv.Append("26/08/2569,ค่าไฟ,5220,500.00,,JV-BAD\n26/08/2569,ค่าไฟ,1120,,450.00,JV-BAD\n");
        var detail = await Pipeline(db, new MemFiles()).CreateAsync(cid, Csv("je-span.csv", csv.ToString()), new ImportCreateBatchRequest("journal-entries"), Actor);

        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await RowsAsync(check, cid, detail.Summary.Id);
        Assert.Equal(504, rows.Count);
        bool Unbalanced(ImportStagedRow r) => (r.IssuesJson ?? "").Contains("\"je-unbalanced\"");
        Assert.All(rows.Where(r => r.RowNo is >= 499 and <= 502), r =>
        {
            Assert.False(Unbalanced(r), $"แถว {r.RowNo}: ใบที่คร่อมขอบ chunk ถูกตีว่าไม่ลงตัว (เห็นครึ่งใบ)");
            Assert.NotEqual(ImportRowStatus.Error, r.Status);
        });
        Assert.All(rows.Where(r => r.RowNo is 503 or 504), r =>
        {
            Assert.True(Unbalanced(r));
            Assert.Equal(ImportRowStatus.Error, r.Status);
        });
        Assert.DoesNotContain(rows.Where(r => r.RowNo <= 498), Unbalanced);
    }

    [Fact]
    public async Task P0_2_แก้บรรทัดเดียวของใบที่ลงตัว_ทั้งใบถูกตรวจใหม่_ไม่ลงครึ่งใบ_และแถวที่ยังไม่รับทราบทำให้ทั้งใบรอ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db, "1120", "5210", "5220");
        var files = new MemFiles();
        var csv = JeHeader
            + "25/08/2569,ค่าเช่า ส.ค.,5210,100.00,,JV-A\n25/08/2569,ค่าเช่า ส.ค.,1120,,100.00,JV-A\n"
            + "26/08/2569,ค่าไฟ,5220,200.00,,JV-B\n26/08/2569,ค่าไฟ,1120,,200.00,JV-B\n"
            // ใบ C: บรรทัดแรกมีทั้งเดบิตและเครดิต (คำเตือน je-both-sides) — ทั้งใบลงตัว
            + "27/08/2569,ค่าน้ำ,5220,100.00,50.00,JV-C\n27/08/2569,ค่าน้ำ,1120,,50.00,JV-C\n";
        var detail = await Pipeline(db, files).CreateAsync(cid, Csv("je-edit.csv", csv), new ImportCreateBatchRequest("journal-entries"), Actor);
        var batchId = detail.Summary.Id;
        var rows = await RowsAsync(db, cid, batchId);
        var a1 = rows.Single(r => r.RowNo == 1);

        // แก้เดบิตบรรทัดแรกของใบ A ⇒ ใบ A ไม่ลงตัว — บรรทัดที่สอง (ไม่ได้แก้) ต้องถูกตีด้วย (เดิมยัง "พร้อมลง" แล้วลงเป็นใบไม่ลงตัว)
        using (var e = DbTestDatabase.TryCreateContext()!)
            await Pipeline(e, files).EditRowAsync(cid, batchId, a1.Id, new ImportEditRowRequest(Cells: new Dictionary<string, string?> { ["DebitAmount"] = "120.00" }), Actor);
        using (var c1 = DbTestDatabase.TryCreateContext()!)
        {
            var after = await RowsAsync(c1, cid, batchId);
            Assert.All(after.Where(r => r.RowNo is 1 or 2), r => Assert.Equal(ImportRowStatus.Error, r.Status));
            Assert.All(after.Where(r => r.RowNo is 3 or 4), r => Assert.Equal(ImportRowStatus.Valid, r.Status));   // ใบอื่นไม่ถูกแตะ
        }

        // ลงโดยยังไม่รับทราบ je-both-sides ⇒ ใบ B ลง · ใบ A ไม่ลง (ผิด) · ใบ C บรรทัดที่สองพร้อมแต่ไม่ลงครึ่งใบ (เหตุผล + ทางไปต่อ)
        ImportCommitResultDto res;
        using (var r1 = DbTestDatabase.TryCreateContext()!)
            res = await Pipeline(r1, files).CommitAsync(cid, batchId, Ack(detail, "je-both-sides"), Actor);
        Assert.Equal(2, res.Committed);
        var refused = Assert.Single(res.Failures);
        Assert.Equal(6, refused.RowNo);
        Assert.Contains("ไม่ลงใบครึ่งใบ", refused.Error);
        Assert.True(res.Blocked >= 3);                                           // ใบ A 2 แถว + ใบ C แถวแรก — ยังไม่พร้อม ≠ ข้าม
        Assert.Equal(0, res.Skipped);
        using (var c2 = DbTestDatabase.TryCreateContext()!)
        {
            var jes = await c2.JournalEntries.AsNoTracking().Include(j => j.Lines).Where(j => j.CompanyId == cid).ToListAsync();
            var b = Assert.Single(jes);
            Assert.Equal(2, b.Lines.Count);
            Assert.Equal(b.TotalDebit, b.TotalCredit);
        }

        // แก้ใบ A กลับให้ลงตัว + รับทราบคำเตือน ⇒ ใบ A และใบ C ลงครบทั้งใบ
        using (var e2 = DbTestDatabase.TryCreateContext()!)
            await Pipeline(e2, files).EditRowAsync(cid, batchId, a1.Id, new ImportEditRowRequest(Cells: new Dictionary<string, string?> { ["DebitAmount"] = "100.00" }), Actor);
        using (var r2 = DbTestDatabase.TryCreateContext()!)
            res = await Pipeline(r2, files).CommitAsync(cid, batchId, Ack(detail), Actor);
        Assert.Equal(4, res.Committed);
        Assert.Equal(ImportSessionStatus.Completed, res.Status);
        using var c3 = DbTestDatabase.TryCreateContext()!;
        var all = await c3.JournalEntries.AsNoTracking().Include(j => j.Lines).Where(j => j.CompanyId == cid).ToListAsync();
        Assert.Equal(3, all.Count);
        Assert.All(all, j => { Assert.Equal(2, j.Lines.Count); Assert.Equal(j.TotalDebit, j.TotalCredit); });
    }

    // ═════════════════════════════ P1-1 ═════════════════════════════

    [Fact]
    public async Task P1_1_ย้อนการนำเข้าที่ต่อบรรทัดเข้าใบของผู้ใช้_ลบเฉพาะบรรทัดที่เพิ่ม_ใบที่ชุดสร้างเองลบทั้งใบ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db, "1120", "5210", "5220");
        var a5210 = await db.ChartOfAccounts.Where(a => a.CompanyId == cid && a.AccountCode == "5210").Select(a => a.Id).SingleAsync();
        var a1120 = await db.ChartOfAccounts.Where(a => a.CompanyId == cid && a.AccountCode == "1120").Select(a => a.Id).SingleAsync();
        // ใบที่ผู้ใช้คีย์เอง (ร่าง) — วันที่/คำอธิบาย/อ้างอิงเดียวกับไฟล์ที่จะนำเข้า
        var hand = new JournalEntry
        {
            CompanyId = cid, EntryNumber = "JV-MANUAL-" + Guid.NewGuid().ToString("N")[..6],
            EntryDate = ImportValueNormalizer.Date("2026-08-25").Value!.Value,
            Description = "ปรับปรุงสิ้นเดือน", Reference = "ADJ-08", Status = JournalEntryStatus.Draft, TotalDebit = 300m, TotalCredit = 300m,
            Lines = new List<JournalEntryLine>
            {
                new() { AccountId = a5210, DebitAmount = 300m, Description = "คีย์มือ" },
                new() { AccountId = a1120, CreditAmount = 300m, Description = "คีย์มือ" },
            },
        };
        db.JournalEntries.Add(hand);
        await db.SaveChangesAsync();

        var files = new MemFiles();
        var csv = JeHeader
            + "25/08/2569,ปรับปรุงสิ้นเดือน,5220,100.00,,ADJ-08\n25/08/2569,ปรับปรุงสิ้นเดือน,1120,,100.00,ADJ-08\n"
            + "28/08/2569,ค่าโทรศัพท์,5220,80.00,,JV-TEL\n28/08/2569,ค่าโทรศัพท์,1120,,80.00,JV-TEL\n";
        var detail = await Pipeline(db, files).CreateAsync(cid, Csv("je-append.csv", csv), new ImportCreateBatchRequest("journal-entries"), Actor);
        using (var r = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(4, (await Pipeline(r, files).CommitAsync(cid, detail.Summary.Id, Ack(detail), Actor)).Committed);
        using (var mid = DbTestDatabase.TryCreateContext()!)
        {
            var merged = await mid.JournalEntries.AsNoTracking().Include(j => j.Lines).SingleAsync(j => j.Id == hand.Id && j.CompanyId == cid);
            Assert.Equal(4, merged.Lines.Count);                                 // writer เดิมต่อบรรทัดเข้าใบที่มีอยู่ (พฤติกรรมเดิม)
        }

        ImportUndoPreviewDto preview;
        using (var p = DbTestDatabase.TryCreateContext()!)
            preview = await Pipeline(p, files).PreviewUndoAsync(cid, detail.Summary.Id);
        Assert.All(preview.Items, i => Assert.False(i.Blocked, i.BlockReason));
        Assert.Equal(2, preview.Items.Count(i => i.Kind == ImportUndoKind.RestoreBefore));   // บรรทัดที่ต่อเข้าใบของผู้ใช้ = คืนค่าเดิม
        using (var u = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(4, (await Pipeline(u, files).UndoAsync(cid, detail.Summary.Id, "นำเข้าผิดไฟล์", Actor)).Undone);

        using var check = DbTestDatabase.TryCreateContext()!;
        var kept = await check.JournalEntries.AsNoTracking().Include(j => j.Lines).SingleAsync(j => j.Id == hand.Id && j.CompanyId == cid);
        Assert.False(kept.IsDeleted);                                            // เดิม: ใบของผู้ใช้ถูกลบทั้งใบ
        Assert.Equal(2, kept.Lines.Count);
        Assert.Equal(300m, kept.TotalDebit);
        Assert.Equal(300m, kept.TotalCredit);
        // ทิศตรงข้าม: ใบที่ชุดนี้สร้างเองถูกลบทั้งใบ
        var tel = await check.JournalEntries.IgnoreQueryFilters().AsNoTracking().SingleAsync(j => j.CompanyId == cid && j.Reference == "JV-TEL");
        Assert.True(tel.IsDeleted);
    }

    // ═════════════════════════════ P1-2 ═════════════════════════════

    private static DocumentService DocService(AccountingDbContext db)
        => new(db, new AccountingService(db), AllowAll<ISubscriptionService>.Create(), null!, null!, NullLogger<DocumentService>.Instance, null!, null!, null!);

    private static DocumentImportBuilder Builder(AccountingDbContext db)
        => new(db, new ImportEntityResolver(db, new DuplicateDetector(db)), new ImportAccountMatcher(db));

    private static ImportTargetRow DocRow(int no, string contactName, string line, string price)
        => new(Guid.NewGuid(), no, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["DocumentType"] = "ใบแจ้งหนี้", ["DocumentNumber"] = "IV-OLD-" + no, ["DocumentDate"] = DateTime.UtcNow.AddHours(7).Date.AddDays(-3).ToString("yyyy-MM-dd"),
            ["ContactName"] = contactName, ["LineDescription"] = line, ["LineQuantity"] = "1", ["LineUnitPrice"] = price, ["LineVatRate"] = "7",
        }, ImportRowAction.Create);

    [Fact]
    public async Task P1_2_เอกสารมีแต่ชื่อผู้ติดต่อ_ชื่อตรงตัวไม่ซ้ำผูกได้_ชื่อคล้ายบล็อกตั้งแต่ขั้นตรวจ_ผู้ใช้เลือกแล้วลงได้_ทางไฟล์เดิมลงได้()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db, "11110", "11310", "21210", "41100", "51100");
        var exact = new Contact { CompanyId = cid, Name = "ร้านสมใจการค้า", ContactType = ContactType.JuristicPerson, IsCustomer = true };
        var twinA = new Contact { CompanyId = cid, Name = "ร้านชื่อซ้ำ", ContactType = ContactType.JuristicPerson, IsCustomer = true };
        var twinB = new Contact { CompanyId = cid, Name = "ร้านชื่อซ้ำ", ContactType = ContactType.JuristicPerson, IsCustomer = true };
        var longName = new Contact { CompanyId = cid, Name = "บริษัท ลูกค้าเก่าแก่ จำกัด", ContactType = ContactType.JuristicPerson, IsCustomer = true };
        db.Contacts.AddRange(exact, twinA, twinB, longName);
        await db.SaveChangesAsync();
        var target = new DocumentImportTarget(db, Builder(db), DocService(db));
        var ctx = new ImportTargetContext(cid, Guid.NewGuid(), DocumentImportTarget.Key, Actor, new ImportCommitOptions());

        // (ก) ชื่อตรงตัวไม่ซ้ำ ⇒ ผูกตั้งแต่ขั้นตรวจ + คำเตือนให้รับทราบ (ไม่ใช่ "พร้อมลง" แล้วล้มตอนลง)
        var okRow = DocRow(1, "ร้านสมใจการค้า", "ค่าบริการ", "1000.00");
        var v1 = Assert.Single(await target.ValidateAsync(ctx, new[] { okRow }, CancellationToken.None));
        Assert.Equal(exact.Id, v1.MatchedEntityId);
        Assert.DoesNotContain(v1.Issues, i => i.Severity == ImportIssueSeverity.Error);
        Assert.Contains(v1.Issues, i => i.Code == "doc-contact-name-only" && i.Severity == ImportIssueSeverity.Warning);
        var r1 = await target.CommitGroupAsync(ctx, new[] { okRow with { MatchedEntityId = v1.MatchedEntityId } }, CancellationToken.None);
        Assert.True(r1[0].Success, r1[0].Error);
        Assert.Equal(exact.Id, (await db.Documents.AsNoTracking().SingleAsync(d => d.Id == r1[0].EntityId && d.CompanyId == cid)).ContactId);

        // (ข) ชื่อคล้ายอย่างเดียว ⇒ บล็อกตั้งแต่ขั้นตรวจ (ไม่เลือกให้เอง · ข้อ 151 (5)) พร้อมผู้สมัครให้เลือก
        var fuzzy = DocRow(2, "ลูกค้าเก่าแก่", "ค่าที่ปรึกษา", "500.00");
        var v2 = Assert.Single(await target.ValidateAsync(ctx, new[] { fuzzy }, CancellationToken.None));
        Assert.Null(v2.MatchedEntityId);
        Assert.Contains(v2.Issues, i => i.Code == "contact-not-found" && i.Severity == ImportIssueSeverity.Error && i.Message.Contains("เลือกผู้ติดต่อ"));
        // ผู้ใช้เลือกผู้ติดต่อเอง ⇒ ผ่านขั้นตรวจ + ลงได้
        var picked = fuzzy with { MatchedEntityId = longName.Id, MatchChosenByUser = true };
        var v2b = Assert.Single(await target.ValidateAsync(ctx, new[] { picked }, CancellationToken.None));
        Assert.Equal(longName.Id, v2b.MatchedEntityId);
        Assert.Equal("User", v2b.MatchKind);
        Assert.DoesNotContain(v2b.Issues, i => i.Severity == ImportIssueSeverity.Error);
        var r2 = await target.CommitGroupAsync(ctx, new[] { picked }, CancellationToken.None);
        Assert.True(r2[0].Success, r2[0].Error);

        // (ค) ชื่อตรงตัวแต่ซ้ำสองราย ⇒ ไม่ผูกเอง
        var v3 = Assert.Single(await target.ValidateAsync(ctx, new[] { DocRow(3, "ร้านชื่อซ้ำ", "ค่าส่ง", "50.00") }, CancellationToken.None));
        Assert.Null(v3.MatchedEntityId);
        Assert.Contains(v3.Issues, i => i.Code == "contact-not-found");

        // (ง) ทางไฟล์เดิม (นำเข้าไฟล์ทั่วไป) — ชื่อตรงตัวไม่ซ้ำลงได้เหมือนก่อนมีศูนย์นำเข้า (เดิมรอบ 205 ล้มทุกใบ)
        var legacy = new ImportExportService(db, new NullErrorLog(), documents: DocService(db), documentBuilder: Builder(db));
        var res = await legacy.ImportAsync(cid, new ImportRequest("documents", "csv", true, null, new List<Dictionary<string, string>>
        {
            new() { ["DocumentType"] = "Invoice", ["DocumentNumber"] = "IV-LEGACY-1", ["DocumentDate"] = DateTime.UtcNow.AddHours(7).Date.AddDays(-2).ToString("yyyy-MM-dd"),
                ["ContactName"] = "ร้านสมใจการค้า", ["LineDescription"] = "ค่าบริการรายเดือน", ["LineQuantity"] = "1", ["LineUnitPrice"] = "800.00" },
        }), "tester");
        Assert.Equal(1, res.SuccessCount);
        using var check = DbTestDatabase.TryCreateContext()!;
        var doc = await check.Documents.AsNoTracking().SingleAsync(d => d.CompanyId == cid && d.Reference == "IV-LEGACY-1");
        Assert.Equal(exact.Id, doc.ContactId);
        Assert.Equal(DocumentStatus.Draft, doc.Status);
    }

    // ═════════════════════════════ P1-3 ═════════════════════════════

    [Fact]
    public async Task P1_3_ย้อนผังบัญชีที่ถูกอ้าง_บล็อกพร้อมเหตุผล_ที่ไม่ถูกอ้างย้อนได้_บัญชีย่อยในชุดเดียวกันไม่บล็อกกันเอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db, "1120");
        var files = new MemFiles();
        var csv = "รหัสบัญชี,ชื่อบัญชี,ประเภทบัญชี\n"
            + "1190,เงินฝากกระแสรายวัน ธ.ทดสอบ,Asset\n"
            + "2190,เงินกู้ยืมกรรมการ,Liability\n"
            + "1191,เงินฝากประจำ,Asset\n"
            + "1192,เงินฝากประจำ 6 เดือน,Asset\n"
            + "1193,เงินฝากประจำ 12 เดือน,Asset\n";
        var detail = await Pipeline(db, files).CreateAsync(cid, Csv("coa.csv", csv), new ImportCreateBatchRequest("chart-of-accounts"), Actor);
        using (var r = DbTestDatabase.TryCreateContext()!)
            Assert.Equal(5, (await Pipeline(r, files).CommitAsync(cid, detail.Summary.Id, Ack(detail), Actor)).Committed);

        using (var refs = DbTestDatabase.TryCreateContext()!)
        {
            var ids = await refs.ChartOfAccounts.Where(a => a.CompanyId == cid).ToDictionaryAsync(a => a.AccountCode, a => a.Id);
            var period = new FiscalPeriod { CompanyId = cid, Name = "2569/08", Year = 2026, Month = 8, StartDate = new DateTime(2026, 8, 1), EndDate = new DateTime(2026, 8, 31) };
            refs.FiscalPeriods.Add(period);
            refs.BankAccounts.Add(new BankAccount { CompanyId = cid, AccountName = "กระแสรายวัน", BankName = "ธ.ทดสอบ", AccountNumber = "111-1-11111-1", LinkedAccountId = ids["1190"] });
            refs.OpeningBalances.Add(new OpeningBalance { CompanyId = cid, FiscalPeriodId = period.Id, AccountId = ids["2190"], OpeningCredit = 50_000m });
            // บัญชีย่อยที่ผู้ใช้สร้างเอง (ไม่ได้มาจากชุดนี้) ใต้ 1191 ⇒ 1191 ย้อนไม่ได้
            refs.ChartOfAccounts.Add(new ChartOfAccount { CompanyId = cid, AccountCode = "11911", AccountName = "เงินฝากประจำ สาขาย่อย", AccountType = AccountType.Asset, Level = 5, IsActive = true, ParentAccountId = ids["1191"] });
            // บัญชีย่อยที่มาจากชุดนี้ใต้บัญชีจากชุดนี้ ⇒ ไม่บล็อกกันเอง
            var child = await refs.ChartOfAccounts.SingleAsync(a => a.Id == ids["1193"] && a.CompanyId == cid);
            child.ParentAccountId = ids["1192"];
            await refs.SaveChangesAsync();
        }

        ImportUndoPreviewDto preview;
        using (var p = DbTestDatabase.TryCreateContext()!)
            preview = await Pipeline(p, files).PreviewUndoAsync(cid, detail.Summary.Id);
        ImportUndoItemDto Item(string code) => preview.Items.Single(i => (i.DisplayKey ?? "").StartsWith(code + " "));
        Assert.True(Item("1190").Blocked);
        Assert.Contains("บัญชีธนาคาร", Item("1190").BlockReason);
        Assert.False(string.IsNullOrWhiteSpace(Item("1190").WayForward));
        Assert.True(Item("2190").Blocked);
        Assert.Contains("ยอดยกมา", Item("2190").BlockReason);
        Assert.True(Item("1191").Blocked);
        Assert.Contains("บัญชีย่อย", Item("1191").BlockReason);
        Assert.False(Item("1192").Blocked);                                     // ลูก 1193 ถูกย้อนพร้อมกัน
        Assert.False(Item("1193").Blocked);

        using (var u = DbTestDatabase.TryCreateContext()!)
        {
            var res = await Pipeline(u, files).UndoAsync(cid, detail.Summary.Id, "ผังไม่ถูกต้อง", Actor);
            Assert.Equal(2, res.Undone);
            Assert.Equal(ImportSessionStatus.PartiallyCompleted, res.Status);
        }
        using var check = DbTestDatabase.TryCreateContext()!;
        var after = await check.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().Where(a => a.CompanyId == cid).ToDictionaryAsync(a => a.AccountCode);
        Assert.False(after["1190"].IsDeleted);
        Assert.False(after["2190"].IsDeleted);
        Assert.False(after["1191"].IsDeleted);
        Assert.True(after["1192"].IsDeleted);
        Assert.True(after["1193"].IsDeleted);
    }

    // ═════════════════════════════ P1-5 ═════════════════════════════

    private static string CitizenId(string twelve)
    {
        for (var d = 0; d <= 9; d++)
            if (ThaiTaxIdValidator.Check(twelve + d).IsValid) return twelve + d;
        throw new InvalidOperationException("ไม่มีหลักตรวจที่ถูก");
    }

    private static ImportHubController Controller(ImportPipelineService pipeline, AccountingDbContext db, IPermissionService perms, Guid userId)
        => new(pipeline, new NoTemplates(), new ImportTargetRegistry(new IImportTarget[] { new EmployeesImportTarget(db, null!, null!) }),
            AllowAll<IOpeningBalanceReconciliation>.Create(), perms, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Name, "ผู้ทดสอบ HR"),
                    }, "Test")),
                },
            },
        };

    private static int StatusOf(object result)
    {
        var r = result is IConvertToActionResult c ? c.Convert() : (IActionResult)result;
        return r is IStatusCodeActionResult s ? s.StatusCode ?? 200 : 200;
    }

    [Fact]
    public async Task P1_5_ทะเบียนพนักงาน_ไม่มี_PiiView_เห็นค่าที่ปิดบัง_มี_PiiView_เห็นค่าเต็ม_ไฟล์ต้นทางต้องมี_PiiView()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await SeedCompanyAsync(db);
        var citizen = CitizenId("110170020345");
        var files = new MemFiles();
        var csv = "รหัสพนักงาน,ชื่อ,นามสกุล,เลขบัตร ปชช.,วันเริ่มงาน,เงินเดือนพื้นฐาน,เลขที่บัญชีธนาคาร\n"
            + $"EMP-001,สมชาย,ใจดี,{citizen},01/01/2569,\"25,000.00\",123-4-56789-0\n"
            + $"EMP-002,สมหญิง,,{citizen},01/02/2569,\"18,500.00\",987-6-54321-0\n";   // ไม่มีนามสกุล ⇒ แถวผิด (ไปอยู่ใน errors.csv)
        var detail = await Pipeline(db, files).CreateAsync(cid, Csv("พนักงาน.csv", csv), new ImportCreateBatchRequest("employees"), Actor);
        var batchId = detail.Summary.Id;

        string? Cell(ImportRowDto row, string field) => row.Cells.Single(c => c.Field == field).Value;
        string? RawCell(ImportRowDto row, string field) => row.Cells.Single(c => c.Field == field).Raw;

        // ไม่มี Pii.View (ค่าเริ่มของ service) ⇒ ปิดบังทั้งค่า · ค่าดิบ
        var masked = (await Pipeline(db, files).GetRowsAsync(cid, batchId, new ImportRowQuery())).Rows.Single(r => r.RowNo == 1);
        Assert.Equal(PiiMask.CitizenId(citizen), Cell(masked, "CitizenId"));
        Assert.DoesNotContain(citizen, RawCell(masked, "CitizenId") ?? "");
        Assert.DoesNotContain("25000", Cell(masked, "BaseSalary") ?? "");
        Assert.DoesNotContain("25,000", RawCell(masked, "BaseSalary") ?? "");
        Assert.DoesNotContain("123-4-5", Cell(masked, "BankAccountNumber") ?? "");
        Assert.Equal("สมชาย", Cell(masked, "FirstNameTh"));                        // ช่องที่ไม่อ่อนไหวไม่ถูกแตะ
        var csvMasked = Encoding.UTF8.GetString(await Pipeline(db, files).ExportErrorRowsCsvAsync(cid, batchId));
        Assert.Contains("สมหญิง", csvMasked);
        Assert.DoesNotContain(citizen, csvMasked);
        Assert.DoesNotContain("987-6-54321-0", csvMasked);

        // มี Pii.View ⇒ ค่าเต็ม
        var full = (await Pipeline(db, files).GetRowsAsync(cid, batchId, new ImportRowQuery(), revealPii: true)).Rows.Single(r => r.RowNo == 1);
        Assert.Equal(citizen, Cell(full, "CitizenId"));
        Assert.Contains("25000", Cell(full, "BaseSalary") ?? "");
        Assert.Contains(citizen, Encoding.UTF8.GetString(await Pipeline(db, files).ExportErrorRowsCsvAsync(cid, batchId, revealPii: true)));

        // controller: สิทธิ์นำเข้า (HrAdmin) อย่างเดียว ⇒ แถวปิดบัง + ไฟล์ต้นทาง 403 · มี Pii.View ⇒ ค่าเต็ม + ดาวน์โหลดได้
        var hrOnly = Controller(Pipeline(db, files), db, KeysOnly.Create(PermissionKeys.HrAdmin), Guid.NewGuid());
        var rowsRes = await hrOnly.GetRows(cid, batchId, 1, 100, null, null, null, CancellationToken.None);
        var page = Assert.IsType<OkObjectResult>(rowsRes.Result).Value as Accounting.Models.DTOs.ApiResponse<ImportRowPageDto>;
        Assert.Equal(PiiMask.CitizenId(citizen), Cell(page!.Data!.Rows.Single(r => r.RowNo == 1), "CitizenId"));
        Assert.Equal(403, StatusOf(await hrOnly.SourceFile(cid, batchId, CancellationToken.None)));

        var hrPii = Controller(Pipeline(db, files), db, KeysOnly.Create(PermissionKeys.HrAdmin, PermissionKeys.PiiView), Guid.NewGuid());
        var rowsFull = await hrPii.GetRows(cid, batchId, 1, 100, null, null, null, CancellationToken.None);
        var pageFull = Assert.IsType<OkObjectResult>(rowsFull.Result).Value as Accounting.Models.DTOs.ApiResponse<ImportRowPageDto>;
        Assert.Equal(citizen, Cell(pageFull!.Data!.Rows.Single(r => r.RowNo == 1), "CitizenId"));
        Assert.Equal(200, StatusOf(await hrPii.SourceFile(cid, batchId, CancellationToken.None)));

        // ทิศตรงข้าม: ชนิดที่ไม่มี PII (ผู้ติดต่อ) ไม่ต้องมี Pii.View และไม่ถูกปิดบัง
        Assert.False(ImportPiiFields.HasPii("contacts"));
        Assert.Equal("0105556000001", ImportPiiFields.Mask("contacts", "TaxId", "0105556000001"));
    }
}
