using System.Reflection;
using System.Security.Claims;
using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Import;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 205 ทีม E (IMPORT_ETL_REVIEW §2.7) — ด่านของศูนย์นำเข้าข้อมูล (<see cref="ImportHubController"/>) บน PostgreSQL จริง:
/// <para>(1) <b>ทุก endpoint</b> ที่ไม่มีคีย์ของชนิดข้อมูลของชุด ⇒ 403 และ service <b>ไม่ถูกเรียกเลย</b> (ด่านมาก่อน service)</para>
/// <para>(2) ชุดของบริษัทอื่น ⇒ 404 แม้ถือทุกคีย์ (tenant isolation — ไม่แตะ service)</para>
/// <para>(3) ทิศตรงข้าม: ถือคีย์ ⇒ 200 และ service ได้ companyId ของตัวเอง · เปลี่ยนชนิดเป็นพนักงานต้องถือ HR.Admin ด้วย ·
/// อัปโหลดให้ระบบเดาแล้วเดาได้ชนิดที่ไม่มีสิทธิ์ ⇒ ยกเลิกชุด + 403 · ประวัติรวมซ่อนชุดที่ไม่มีสิทธิ์และบอกจำนวน</para>
/// <para>service/ที่เก็บแม่แบบเป็น <see cref="DispatchProxy"/> บันทึกการเรียก — ทีม A/B เพิ่มสมาชิกให้ interface ได้โดยเทสต์นี้ไม่พัง</para>
/// </summary>
[Trait("Category", "Db")]
public class ImportHubControllerGateDbTests
{
    private readonly ITestOutputHelper _out;
    public ImportHubControllerGateDbTests(ITestOutputHelper output) => _out = output;

    // ═════════ proxy บันทึกการเรียก (ไม่ implement interface ตรง ⇒ สมาชิกใหม่ของสัญญาไม่ทำให้เทสต์คอมไพล์ไม่ผ่าน) ═════════

    public class Rec<T> : DispatchProxy where T : class
    {
        public List<(string Name, object?[] Args)> Calls { get; } = new();
        public Func<string, object?[], object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var m = targetMethod!;
            var a = args ?? Array.Empty<object?>();
            Calls.Add((m.Name, a));
            var value = Handler?.Invoke(m.Name, a);
            var rt = m.ReturnType;
            if (rt == typeof(Task)) return Task.CompletedTask;
            if (rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = rt.GetGenericArguments()[0];
                var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner);
                return fromResult.Invoke(null, new[] { value ?? (inner.IsValueType ? Activator.CreateInstance(inner) : null) });
            }
            return value ?? (rt.IsValueType && rt != typeof(void) ? Activator.CreateInstance(rt) : null);
        }

        public static (T Proxy, Rec<T> Rec) Create(Func<string, object?[], object?>? handler = null)
        {
            var p = DispatchProxy.Create<T, Rec<T>>();
            var rec = (Rec<T>)(object)p;
            rec.Handler = handler;
            return (p, rec);
        }
    }

    // ═════════ DTO ตัวอย่าง ═════════

    private static ImportBatchSummaryDto Summary(Guid id, string entityType, string label)
        => new(id, entityType, label, "ผู้ติดต่อ-express.csv", "express", ImportSessionStatus.Staged, "รอตรวจทาน", ImportBatchPhase.Review,
            3, 2, 1, 0, 0, 0, 0, 0, DateTime.UtcNow, "ผู้ทดสอบ", null, null, true, true, true);

    private static ImportBatchDetailDto Detail(Guid id, string entityType, string label = "ผู้ติดต่อ")
        => new(Summary(id, entityType, label),
            new ImportBatchProgressDto(ImportBatchPhase.Review, "รอตรวจทาน", 3, 3, 100m, false, null),
            new ImportSourceInfoDto("ผู้ติดต่อ-express.csv", ImportFileKind.Csv, "windows-874", ",", null, Array.Empty<string>(), 0, 1, null, "express",
                "Express", null, null),
            Array.Empty<ImportColumnPlanDto>(), Array.Empty<ImportFieldDto>(), null, false, Array.Empty<ImportIssueCountDto>(),
            Array.Empty<ImportWarningAckDto>(), Array.Empty<ImportDuplicateFileDto>(), Array.Empty<string>());

    private static ImportTemplateDto Template(Guid id, string entityType)
        => new(id, entityType, "แม่แบบ Express", "sig", "express", new[] { "ชื่อ" }, Array.Empty<ImportTemplateColumnDto>(), 1, null, true, false,
            DateTime.UtcNow);

    private sealed class Harness
    {
        public required ImportHubController Controller { get; init; }
        public required Rec<IImportPipelineService> Pipeline { get; init; }
        public required Rec<IImportMappingTemplateStore> Templates { get; init; }
        public required Rec<IOpeningBalanceReconciliation> Recon { get; init; }
        public string DetectedEntityType { get; set; } = "contacts";
    }

    private static readonly string[] KnownEntityTypes = { "contacts", "products", "employees", "journal-entries", "fixed-assets" };

    private static Harness Build(AccountingDbContext db, Guid userId, IReadOnlyCollection<string> keys, Guid? templateId = null,
        string templateEntityType = "contacts")
    {
        Harness? h = null;
        var (pipe, pipeRec) = Rec<IImportPipelineService>.Create((name, a) => name switch
        {
            nameof(IImportPipelineService.CreateAsync) => Detail(Guid.NewGuid(), h!.DetectedEntityType),
            nameof(IImportPipelineService.GetAsync) or nameof(IImportPipelineService.AnalyzeAsync) or nameof(IImportPipelineService.RemapAsync)
                => Detail((Guid)a[1]!, "contacts"),
            nameof(IImportPipelineService.ListAsync) => new ImportBatchListDto(1, 20, 2, new[]
            {
                Summary(Guid.NewGuid(), "contacts", "ผู้ติดต่อ"),
                Summary(Guid.NewGuid(), "employees", "พนักงาน"),
            }),
            nameof(IImportPipelineService.GetRowsAsync) => new ImportRowPageDto(1, 100, 0, Array.Empty<ImportRowDto>(), Array.Empty<ImportIssueCountDto>()),
            nameof(IImportPipelineService.BulkActionAsync) => new ImportBulkResultDto(0, Summary((Guid)a[1]!, "contacts", "ผู้ติดต่อ")),
            nameof(IImportPipelineService.CommitAsync) => new ImportCommitResultDto((Guid)a[1]!, ImportSessionStatus.Completed, "นำเข้าแล้ว", 3, 0, 0, false,
                Array.Empty<ImportRowFailureDto>(), null),
            nameof(IImportPipelineService.PreviewUndoAsync) => new ImportUndoPreviewDto((Guid)a[1]!, true, 3, 0, Array.Empty<ImportUndoItemDto>(),
                Array.Empty<string>()),
            nameof(IImportPipelineService.UndoAsync) => new ImportUndoResultDto((Guid)a[1]!, ImportSessionStatus.Undone, 3, 0, Array.Empty<ImportRowFailureDto>()),
            nameof(IImportPipelineService.CancelAsync) => Summary((Guid)a[1]!, "contacts", "ผู้ติดต่อ"),
            nameof(IImportPipelineService.ExportErrorRowsCsvAsync) => new byte[] { 0xEF, 0xBB, 0xBF },
            nameof(IImportPipelineService.GetSourceFileAsync) => new ImportSourceFileDto("ผู้ติดต่อ-express.csv", "text/csv", new byte[] { 1 }),
            nameof(IImportPipelineService.GetEntityCatalogueAsync) => new ImportEntityCatalogueDto(
                KnownEntityTypes.Select(t => new ImportEntityInfoDto(t, t, null, "service-ตอบผิด", true, Array.Empty<ImportFieldDto>(),
                    ImportCommitStyle.SharedTransaction, true, true, Array.Empty<string>())).ToList(),
                Array.Empty<ImportMigrationStepDto>()),
            _ => null,
        });
        var (tpl, tplRec) = Rec<IImportMappingTemplateStore>.Create((name, a) => name switch
        {
            nameof(IImportMappingTemplateStore.GetAsync) => templateId is { } tid && (Guid)a[1]! == tid ? Template(tid, templateEntityType) : null,
            nameof(IImportMappingTemplateStore.ListAsync) => new List<ImportTemplateDto>
            {
                Template(Guid.NewGuid(), "contacts"), Template(Guid.NewGuid(), "employees"),
            },
            nameof(IImportMappingTemplateStore.UpdateAsync) => Template((Guid)a[1]!, templateEntityType),
            nameof(IImportMappingTemplateStore.DeleteAsync) => true,
            _ => null,
        });
        var (recon, reconRec) = Rec<IOpeningBalanceReconciliation>.Create((_, _) =>
            new ImportReconciliationReportDto(DateTime.UtcNow.Date, true, Array.Empty<ImportReconciliationLineDto>()));
        var (perms, _) = Rec<IPermissionService>.Create((name, a) => name == nameof(IPermissionService.HasPermissionAsync)
            ? keys.Contains((string)a[2]!)
            : new List<string>(keys));
        var targets = KnownEntityTypes.Select(t => Rec<IImportTarget>.Create((name, _) => name switch
        {
            "get_EntityType" => t,
            "get_Label" => t == "employees" ? "ทะเบียนพนักงาน" : t,
            _ => null,
        }).Proxy).ToList();
        var ctrl = new ImportHubController(pipe, tpl, new ImportTargetRegistry(targets), recon, perms, db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Name, "ผู้ทดสอบนำเข้า"),
                    }, "Test")),
                },
            },
        };
        h = new Harness { Controller = ctrl, Pipeline = pipeRec, Templates = tplRec, Recon = reconRec };
        return h;
    }

    private static int StatusOf(object result)
    {
        var r = result is IConvertToActionResult c ? c.Convert() : (IActionResult)result;
        return r is IStatusCodeActionResult s ? s.StatusCode ?? 200 : 200;
    }

    private static object? DataOf(object result)
    {
        var r = result is IConvertToActionResult c ? c.Convert() : (IActionResult)result;
        return (r as ObjectResult)?.Value;
    }

    private static IFormFile CsvFile()
    {
        // ไฟล์รูปจริง: หัวไทย + เลขผู้เสียภาษี + ปี พ.ศ. (เนื้อหาไม่ถูกอ่านในเทสต์นี้ — service เป็น proxy)
        var bytes = System.Text.Encoding.UTF8.GetBytes("ชื่อ,เลขประจำตัวผู้เสียภาษี,วันที่\nบริษัท รวมมิตร จำกัด,0105556000001,15/01/2569\n");
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "ผู้ติดต่อ-express.csv")
        {
            Headers = new HeaderDictionary(), ContentType = "text/csv",
        };
    }

    /// <summary>ทุก endpoint ที่ระบุ batch — (ชื่อ, เรียก)</summary>
    private static IEnumerable<(string Name, Func<ImportHubController, Guid, Guid, Task<object>> Call)> BatchEndpoints()
    {
        var ct = CancellationToken.None;
        yield return ("GET batches/{id}", async (c, cid, id) => await c.GetBatch(cid, id, ct));
        yield return ("GET rows", async (c, cid, id) => await c.GetRows(cid, id, 1, 100, null, null, null, ct));
        yield return ("POST analyze", async (c, cid, id) => await c.Analyze(cid, id, new ImportAnalyzeRequest(SheetName: "Sheet1"), ct));
        yield return ("PUT mapping", async (c, cid, id) => await c.Remap(cid, id,
            new ImportRemapRequest(new[] { new ImportColumnAssignment(0, "name") }), ct));
        yield return ("PATCH rows/{rowId}", async (c, cid, id) => await c.EditRow(cid, id, Guid.NewGuid(),
            new ImportEditRowRequest(Excluded: true), ct));
        yield return ("POST bulk", async (c, cid, id) => await c.Bulk(cid, id, new ImportBulkActionRequest(ImportBulkOp.ApplySuggestions), ct));
        yield return ("POST commit", async (c, cid, id) => await c.Commit(cid, id, new ImportCommitOptions(), ct));
        yield return ("GET undo-preview", async (c, cid, id) => await c.UndoPreview(cid, id, ct));
        yield return ("POST undo", async (c, cid, id) => await c.Undo(cid, id, new ImportUndoRequest("นำเข้าไฟล์ผิดเดือน"), ct));
        yield return ("POST cancel", async (c, cid, id) => await c.Cancel(cid, id, ct));
        yield return ("GET errors.csv", async (c, cid, id) => await c.ErrorsCsv(cid, id, ct));
        yield return ("GET source-file", async (c, cid, id) => await c.SourceFile(cid, id, ct));
    }

    private static async Task<(Guid CompanyA, Guid CompanyB, Guid ContactsBatch, Guid OtherCompanyBatch, Guid EmployeesBatch)> SeedAsync(
        AccountingDbContext db)
    {
        var a = new Company { Name = "บริษัท นำเข้าทดสอบ จำกัด", TaxId = "0105556000001" };
        var b = new Company { Name = "บริษัท อื่น จำกัด", TaxId = "0105556000002" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        var contacts = new SmartImportSession { CompanyId = a.Id, EntityType = "contacts", FileName = "ผู้ติดต่อ-express.csv", Status = ImportSessionStatus.Staged };
        var employees = new SmartImportSession { CompanyId = a.Id, EntityType = "employees", FileName = "พนักงาน.xlsx", Status = ImportSessionStatus.Staged };
        var other = new SmartImportSession { CompanyId = b.Id, EntityType = "contacts", FileName = "ของบริษัทอื่น.csv", Status = ImportSessionStatus.Staged };
        db.SmartImportSessions.AddRange(contacts, employees, other);
        await db.SaveChangesAsync();
        return (a.Id, b.Id, contacts.Id, other.Id, employees.Id);
    }

    [Fact]
    public async Task ทุกendpointของชุด_ไม่มีคีย์ของชนิดข้อมูล_403_และไม่แตะservice()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var (cid, _, contactsBatch, _, _) = await SeedAsync(db);
        // ถือคีย์อื่นครบ ยกเว้น Contact.Edit ⇒ ชุดผู้ติดต่อต้องปิดทุกทาง
        var keys = new[] { PermissionKeys.ProductEdit, PermissionKeys.JournalManage, PermissionKeys.HrAdmin, PermissionKeys.AssetManage };
        foreach (var (name, call) in BatchEndpoints())
        {
            var h = Build(db, Guid.NewGuid(), keys);
            var result = await call(h.Controller, cid, contactsBatch);
            Assert.True(StatusOf(result) == 403, $"{name}: คาด 403 ได้ {StatusOf(result)}");
            Assert.Empty(h.Pipeline.Calls);
            // ข้อความบอกทางไปต่อ + คีย์ที่ต้องขอ (api.js อ่าน requiredPermission)
            var body = Assert.IsType<Accounting.Models.DTOs.ApiResponse<object>>(DataOf(result));
            Assert.Contains("เพิ่มสิทธิ์", body.Message);
            Assert.Contains(PermissionKeys.ContactEdit, body.Message);
        }
    }

    [Fact]
    public async Task ทุกendpointของชุด_ถือคีย์_ผ่านและserviceได้companyIdของตัวเอง()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _, contactsBatch, _, _) = await SeedAsync(db);
        foreach (var (name, call) in BatchEndpoints())
        {
            var h = Build(db, Guid.NewGuid(), new[] { PermissionKeys.ContactEdit });
            var result = await call(h.Controller, cid, contactsBatch);
            Assert.True(StatusOf(result) == 200, $"{name}: คาด 200 ได้ {StatusOf(result)}");
            var called = Assert.Single(h.Pipeline.Calls);
            Assert.Equal(cid, (Guid)called.Args[0]!);
            Assert.Equal(contactsBatch, (Guid)called.Args[1]!);
        }
    }

    [Fact]
    public async Task ชุดของบริษัทอื่น_404_แม้ถือทุกคีย์_และไม่แตะservice()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _, _, otherBatch, _) = await SeedAsync(db);
        var all = new[] { PermissionKeys.ContactEdit, PermissionKeys.ProductEdit, PermissionKeys.JournalManage, PermissionKeys.HrAdmin };
        foreach (var (name, call) in BatchEndpoints())
        {
            var h = Build(db, Guid.NewGuid(), all);
            var result = await call(h.Controller, cid, otherBatch);
            Assert.True(StatusOf(result) == 404, $"{name}: ชุดของบริษัทอื่นต้อง 404 ได้ {StatusOf(result)}");
            Assert.Empty(h.Pipeline.Calls);
        }
        // ทิศตรงข้าม: id ที่ไม่มีอยู่เลยก็ 404 เหมือนกัน (ไม่บอกว่ามีอยู่ในบริษัทอื่น)
        var h2 = Build(db, Guid.NewGuid(), all);
        Assert.Equal(404, StatusOf(await h2.Controller.GetBatch(cid, Guid.NewGuid(), CancellationToken.None)));
    }

    [Fact]
    public async Task เปลี่ยนชนิดเป็นพนักงาน_ต้องถือHRAdminด้วย_ถือแค่ผู้ติดต่อ403()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _, contactsBatch, _, _) = await SeedAsync(db);
        var h = Build(db, Guid.NewGuid(), new[] { PermissionKeys.ContactEdit });
        var denied = await h.Controller.Analyze(cid, contactsBatch, new ImportAnalyzeRequest(EntityType: "employees"), CancellationToken.None);
        Assert.Equal(403, StatusOf(denied));
        Assert.Empty(h.Pipeline.Calls);

        var h2 = Build(db, Guid.NewGuid(), new[] { PermissionKeys.ContactEdit, PermissionKeys.HrAdmin });
        var ok = await h2.Controller.Analyze(cid, contactsBatch, new ImportAnalyzeRequest(EntityType: "employees"), CancellationToken.None);
        Assert.Equal(200, StatusOf(ok));
        Assert.Equal(nameof(IImportPipelineService.AnalyzeAsync), Assert.Single(h2.Pipeline.Calls).Name);
    }

    [Fact]
    public async Task อัปโหลด_ระบุชนิดที่ไม่มีสิทธิ์403ก่อนอ่านไฟล์_ให้ระบบเดาแล้วเดาได้พนักงาน_ยกเลิกชุดและ403()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _, _, _, _) = await SeedAsync(db);
        var ct = CancellationToken.None;

        // ระบุชนิดเอง: พนักงาน โดยไม่มี HR.Admin ⇒ 403 ก่อนเรียก service
        var h = Build(db, Guid.NewGuid(), new[] { PermissionKeys.ContactEdit });
        var r1 = await h.Controller.CreateBatch(cid, CsvFile(), "employees", null, null, null, null, null, null, ct);
        Assert.Equal(403, StatusOf(r1));
        Assert.Empty(h.Pipeline.Calls);

        // ให้ระบบเดา ⇒ เดาได้พนักงาน ⇒ ยกเลิกชุดที่เพิ่งสร้าง + 403 พร้อมทางไปต่อ
        var h2 = Build(db, Guid.NewGuid(), new[] { PermissionKeys.ContactEdit });
        h2.DetectedEntityType = "employees";
        var r2 = await h2.Controller.CreateBatch(cid, CsvFile(), null, null, null, null, null, null, null, ct);
        Assert.Equal(403, StatusOf(r2));
        Assert.Equal(new[] { nameof(IImportPipelineService.CreateAsync), nameof(IImportPipelineService.CancelAsync) },
            h2.Pipeline.Calls.Select(c => c.Name).ToArray());
        var body = Assert.IsType<Accounting.Models.DTOs.ApiResponse<object>>(DataOf(r2));
        Assert.Contains("ทะเบียนพนักงาน", body.Message);   // ป้ายไทยจากทะเบียน target ไม่ใช่คีย์อังกฤษ
        Assert.Contains("เลือกชนิดข้อมูลเอง", body.Message);

        // ทิศตรงข้าม: เดาได้ผู้ติดต่อ ⇒ 200 · ไม่ถูกยกเลิก · ตัวเลือกของชุด (บัญชีธนาคาร) ถึง service
        var h3 = Build(db, Guid.NewGuid(), new[] { PermissionKeys.ContactEdit });
        var r3 = await h3.Controller.CreateBatch(cid, CsvFile(), null, null, null, null, null, "DayMonthYear",
            "{\"bankAccountId\":\"6f1c3b44-0000-4000-8000-000000000001\"}", ct);
        Assert.Equal(200, StatusOf(r3));
        var create = Assert.Single(h3.Pipeline.Calls);
        var req = Assert.IsType<ImportCreateBatchRequest>(create.Args[2]);
        Assert.Equal(ImportDateOrder.DayMonthYear, req.DateOrder);
        Assert.Equal("6f1c3b44-0000-4000-8000-000000000001", req.Options!["bankAccountId"]);
        var actor = Assert.IsType<ImportActor>(create.Args[3]);
        Assert.Equal("ผู้ทดสอบนำเข้า", actor.Name);

        // ไม่มีสิทธิ์นำเข้าชนิดใดเลย + ให้ระบบเดา ⇒ 403 ก่อนอ่านไฟล์
        var h4 = Build(db, Guid.NewGuid(), Array.Empty<string>());
        Assert.Equal(403, StatusOf(await h4.Controller.CreateBatch(cid, CsvFile(), null, null, null, null, null, null, null, ct)));
        Assert.Empty(h4.Pipeline.Calls);

        // ลำดับวันที่ที่ไม่ใช่ค่าที่รู้จัก (เลขดิบ) ⇒ 400 ไทย ไม่ส่งค่าที่ไม่มีอยู่ใน enum ต่อ
        var h5 = Build(db, Guid.NewGuid(), new[] { PermissionKeys.ContactEdit });
        Assert.Equal(400, StatusOf(await h5.Controller.CreateBatch(cid, CsvFile(), "contacts", null, null, null, null, "7", null, ct)));
        Assert.Empty(h5.Pipeline.Calls);
    }

    [Fact]
    public async Task ประวัติรวม_ซ่อนชุดของชนิดที่ไม่มีสิทธิ์และบอกจำนวน_ระบุชนิดที่ไม่มีสิทธิ์403()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _, _, _, _) = await SeedAsync(db);
        var ct = CancellationToken.None;
        var h = Build(db, Guid.NewGuid(), new[] { PermissionKeys.ContactEdit });
        var list = await h.Controller.ListBatches(cid, 1, 20, null, null, ct);
        Assert.Equal(200, StatusOf(list));
        var dto = Assert.IsType<Accounting.Models.DTOs.ApiResponse<ImportBatchListDto>>(DataOf(list)).Data!;
        Assert.Equal("contacts", Assert.Single(dto.Items).EntityType);
        Assert.Equal(1, dto.HiddenCount);

        Assert.Equal(403, StatusOf(await h.Controller.ListBatches(cid, 1, 20, "employees", null, ct)));
        var tpl = await h.Controller.ListTemplates(cid, null, ct);
        Assert.Equal("contacts", Assert.Single(Assert.IsType<Accounting.Models.DTOs.ApiResponse<IReadOnlyList<ImportTemplateDto>>>(DataOf(tpl)).Data!).EntityType);
        Assert.Equal(403, StatusOf(await h.Controller.ListTemplates(cid, "employees", ct)));
    }

    [Fact]
    public async Task แม่แบบและรายงานกระทบยอด_ไม่มีคีย์403_ถือคีย์ผ่าน()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _, _, _, _) = await SeedAsync(db);
        var ct = CancellationToken.None;
        var tid = Guid.NewGuid();

        var h = Build(db, Guid.NewGuid(), new[] { PermissionKeys.ContactEdit }, tid, "employees");
        Assert.Equal(403, StatusOf(await h.Controller.UpdateTemplate(cid, tid, new ImportTemplateUpsertRequest("x", true), ct)));
        Assert.Equal(403, StatusOf(await h.Controller.DeleteTemplate(cid, tid, ct)));
        Assert.DoesNotContain(h.Templates.Calls, c => c.Name is nameof(IImportMappingTemplateStore.UpdateAsync) or nameof(IImportMappingTemplateStore.DeleteAsync));
        Assert.Equal(404, StatusOf(await h.Controller.DeleteTemplate(cid, Guid.NewGuid(), ct)));
        Assert.Equal(403, StatusOf(await h.Controller.Reconciliation(cid, null, ct)));
        Assert.Empty(h.Recon.Calls);

        var ok = Build(db, Guid.NewGuid(), new[] { PermissionKeys.HrAdmin, PermissionKeys.JournalManage }, tid, "employees");
        Assert.Equal(200, StatusOf(await ok.Controller.UpdateTemplate(cid, tid, new ImportTemplateUpsertRequest("x", true), ct)));
        Assert.Equal(200, StatusOf(await ok.Controller.DeleteTemplate(cid, tid, ct)));
        Assert.Equal(200, StatusOf(await ok.Controller.Reconciliation(cid, null, ct)));
        Assert.Equal(cid, (Guid)Assert.Single(ok.Recon.Calls).Args[0]!);
    }

    [Fact]
    public async Task แคตตาล็อก_UserCanImportมาจากด่านเดียวกับaction_ไม่เชื่อค่าของservice()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, _, _, _, _) = await SeedAsync(db);
        var h = Build(db, Guid.NewGuid(), new[] { PermissionKeys.ContactEdit });
        var res = await h.Controller.Entities(cid, CancellationToken.None);
        var cat = Assert.IsType<Accounting.Models.DTOs.ApiResponse<ImportEntityCatalogueDto>>(DataOf(res)).Data!;
        foreach (var e in cat.Entities)
        {
            Assert.Equal(ImportExportPermissionScope.ForImport(e.EntityType), e.PermissionKey);
            Assert.Equal(e.EntityType == "contacts", e.UserCanImport);
        }
    }

    [Fact]
    public void คีย์ไฟล์ต้นทางของชุด_เท่ากับคีย์นำเข้าทุกชนิด_ด่านดาวน์โหลดไม่แยกทาง()
    {
        // ImportHubController.SourceFile ใช้ด่าน ForImport ของชนิดของชุด — ต้องเท่ากับด่านไฟล์แนบชนิด "ImportBatch" ทุกชนิด (รวมชนิดที่ไม่รู้จัก)
        foreach (var t in new[] { "contacts", "employees", "fixed-assets", "stock-adjustments", "journal-entries", "banktransactions", "ไม่รู้จัก", null })
            Assert.Equal(ImportExportPermissionScope.ForImport(t), AttachmentPermissionScope.ImportBatchKey(t));
    }
}
