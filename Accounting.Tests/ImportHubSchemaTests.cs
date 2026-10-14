using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// รอบ 205 T0 — สัญญาฐานของศูนย์นำเข้าข้อมูล: migration ตรงกับ model ของ EF (ตาราง · คอลัมน์ · index · เงื่อนไข partial) ·
/// คอลัมน์ PII เข้ารหัส (round-trip — control ที่ไม่มีเทสต์ = ไม่มี control · G) · ค่า enum ที่เก็บในฐานคงที่ · DI ลงทะเบียนครบ + scan target ·
/// ทะเบียน target หาด้วยคีย์ normalize เดียวกับด่านสิทธิ์ และชนิดซ้ำล้มดัง
/// </summary>
public class ImportHubSchemaTests
{
    private static AccountingDbContext OfflineDb() => new(new DbContextOptionsBuilder<AccountingDbContext>()
        .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none").Options);

    private static readonly IReadOnlyList<string> Stmts = DatabaseMigrationHelper.Round205ImportHubStatements();

    [Fact]
    public void Migration_ตารางใหม่_ทุกคอลัมน์และทุก_index_ตรงกับ_model_ของ_EF()
    {
        using var db = OfflineDb();
        var migration = string.Join("\n", Stmts);
        foreach (var t in new[] { typeof(ImportStagedRow), typeof(ImportMappingTemplate) })
        {
            var et = db.Model.FindEntityType(t)!;
            Assert.Contains($"CREATE TABLE IF NOT EXISTS \"{et.GetTableName()}\"", migration);
            foreach (var p in et.GetProperties())
                Assert.Contains($"\"{p.GetColumnName()}\"", migration);
            foreach (var ix in et.GetIndexes())
            {
                var name = ix.GetDatabaseName()!;
                var stmt = Assert.Single(Stmts, s => s.Contains($"INDEX IF NOT EXISTS \"{name}\"", StringComparison.Ordinal));
                Assert.Equal(ix.IsUnique, stmt.StartsWith("CREATE UNIQUE INDEX", StringComparison.Ordinal));
                if (ix.GetFilter() is string f) Assert.Contains("WHERE " + f + ";", stmt);
                foreach (var p in ix.Properties) Assert.Contains($"\"{p.GetColumnName()}\"", stmt);
            }
        }
    }

    [Fact]
    public void Migration_คอลัมน์ใหม่ของ_SmartImportSessions_ครบและไม่ทำให้แถวเดิมเปลี่ยนความหมาย()
    {
        using var db = OfflineDb();
        var et = db.Model.FindEntityType(typeof(SmartImportSession))!;
        foreach (var name in new[]
        {
            "SourceFileAttachmentId", "FileSha256", "SheetName", "SheetNamesJson", "HeaderRowIndex", "HeaderRowSpan", "Encoding",
            "Delimiter", "HeaderSignature", "MappingTemplateId", "SourceSystem", "Phase", "CommitMode", "ProcessedRows", "ClaimedBy",
            "ClaimedAt", "HeartbeatAt", "ColumnMatchAiFeedbackId", "ColumnMatchUsedAi", "OptionsJson", "ReviewSummaryJson", "UndoneAt", "UndoneBy",
        })
        {
            var col = et.FindProperty(name)!.GetColumnName();
            Assert.Single(Stmts, s => s.Contains($"ALTER TABLE \"SmartImportSessions\" ADD COLUMN IF NOT EXISTS \"{col}\"", StringComparison.Ordinal));
        }
        // session เดิม: หัวตาราง 1 แถว · ยังไม่มีขั้น/โหมด · ไม่ได้ใช้ AI
        Assert.Contains(Stmts, s => s.Contains("\"HeaderRowSpan\" integer NOT NULL DEFAULT 1", StringComparison.Ordinal));
        Assert.Contains(Stmts, s => s.Contains("\"ColumnMatchUsedAi\" boolean NOT NULL DEFAULT false", StringComparison.Ordinal));
        foreach (var ix in et.GetIndexes().Where(i => i.GetDatabaseName()!.StartsWith("IX_SmartImportSessions_Status_Heartbeat", StringComparison.Ordinal)
                                                     || i.GetDatabaseName()!.StartsWith("IX_SmartImportSessions_Company_FileSha256", StringComparison.Ordinal)))
            Assert.Single(Stmts, s => s.Contains($"\"{ix.GetDatabaseName()}\"", StringComparison.Ordinal));
        Assert.Equal(2, et.GetIndexes().Count(i => i.GetDatabaseName()!.Contains("Heartbeat") || i.GetDatabaseName()!.Contains("FileSha256")));
    }

    [Fact]
    public void Migration_อยู่ในเส้นหลัก_และรันทุกบูตได้()
    {
        var all = DatabaseMigrationHelper.GetAlterStatements();
        Assert.All(Stmts, s => Assert.Contains(s, all));
        Assert.All(Stmts, s => Assert.Matches("IF NOT EXISTS", s));
        Assert.DoesNotContain(Stmts, s => s.Contains("UPDATE ", StringComparison.Ordinal) || s.Contains("DELETE ", StringComparison.Ordinal));
        // ตารางก่อน index ของตาราง
        var list = Stmts.ToList();
        Assert.True(list.FindIndex(s => s.Contains("CREATE TABLE IF NOT EXISTS \"ImportStagedRows\"")) <
                    list.FindIndex(s => s.Contains("UX_ImportStagedRows_Session_RowNo")));
    }

    [Fact]
    public void คอลัมน์_PII_ของแถวพัก_เข้ารหัส_round_trip_และค่าเก็บไม่ใช่ข้อความเดิม()
    {
        using var db = OfflineDb();
        var et = db.Model.FindEntityType(typeof(ImportStagedRow))!;
        const string plain = "[\"นาย ทดสอบ สมมติ\",\"1234567890123\",\"123-4-56789-0\"]";
        foreach (var name in new[] { "RawJson", "MappedJson", "OriginalMappedJson", "BeforeJson" })
        {
            var conv = et.FindProperty(name)!.GetValueConverter();
            Assert.IsType<EncryptedColumnConverter>(conv);
            var stored = (string?)conv!.ConvertToProvider(plain);
            Assert.NotNull(stored);
            Assert.DoesNotContain("1234567890123", stored);
            Assert.Equal(plain, (string?)conv.ConvertFromProvider(stored));
        }
        // ทิศตรงข้าม: คอลัมน์ที่ต้อง query ได้ไม่ถูกเข้ารหัส
        Assert.Null(et.FindProperty("DisplayKey")!.GetValueConverter());
        Assert.Null(et.FindProperty("IssuesJson")!.GetValueConverter());
    }

    [Fact]
    public void ค่า_enum_ที่เก็บในฐาน_คงที่()
    {
        Assert.Equal(58, (int)AiFeatureKey.ImportAccountMap);
        Assert.Equal(59, (int)AiFeatureKey.ImportEntityMatch);
        Assert.Equal(9, (int)ImportSessionStatus.AwaitingDuplicateResolution);   // ค่าเดิมไม่ขยับ
        Assert.Equal(10, (int)ImportSessionStatus.Queued);
        Assert.Equal(15, (int)ImportSessionStatus.Cancelled);
        Assert.Equal(5, (int)ImportRowStatus.Committed);
        Assert.Equal(3, (int)ImportRowAction.Skip);
        Assert.Equal(7, (int)ImportMapSource.User);
        Assert.Equal(1, (int)ImportCommitMode.AllOrNothing);
    }

    [Fact]
    public void AdvisoryLock_ของ_batch_นำเข้า_คงที่และผูกบริษัท()
    {
        Assert.Equal("import-batch", AdvisoryLockKey.ImportBatch);
        var id = Guid.Parse("0a0a0a0a-0000-0000-0000-000000000205").ToString();
        Assert.NotEqual(AdvisoryLockKey.For(Guid.NewGuid(), AdvisoryLockKey.ImportBatch, id),
                        AdvisoryLockKey.For(Guid.NewGuid(), AdvisoryLockKey.ImportBatch, id));
    }

    // ═══ DI + ทะเบียน target ═══

    [Fact]
    public void AddImportHub_ลงทะเบียนสัญญาทุกตัว_และ_worker()
    {
        var services = new ServiceCollection();
        services.AddImportHub();
        foreach (var t in new[]
        {
            typeof(IImportPipelineService), typeof(IImportRowWriter), typeof(IImportEntityResolver), typeof(IImportMappingTemplateStore),
            typeof(IOpeningBalanceReconciliation), typeof(IDocumentImportBuilder), typeof(IImportTargetRegistry),
        })
            Assert.Contains(services, d => d.ServiceType == t && d.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, d => d.ImplementationType == typeof(ImportBatchWorker) && d.Lifetime == ServiceLifetime.Singleton);
        // target ที่ลงทะเบียน = ผล scan ของ assembly หลักทุกตัว (ทีม B/C/D เพิ่มไฟล์แล้วถูกลงทะเบียนเอง)
        var scanned = ImportHubServiceRegistration.ImportTargetTypes(typeof(IImportTarget).Assembly);
        Assert.Equal(scanned.Count, services.Count(d => d.ServiceType == typeof(IImportTarget)));
    }

    [Fact]
    public void scan_target_เอาเฉพาะ_class_จริง_ไม่เอา_abstract()
    {
        var types = ImportHubServiceRegistration.ImportTargetTypes(typeof(ImportHubSchemaTests).Assembly);
        Assert.Contains(typeof(FakeTarget), types);
        Assert.DoesNotContain(typeof(AbstractFakeTarget), types);
    }

    [Fact]
    public void ทะเบียน_target_หาด้วยคีย์_normalize_เดียวกับด่านสิทธิ์()
    {
        var reg = new ImportTargetRegistry(new IImportTarget[] { new FakeTarget("chart-of-accounts"), new FakeTarget("stock-adjustments") });
        Assert.NotNull(reg.Find("chartofaccounts"));
        Assert.NotNull(reg.Find("Chart_Of_Accounts"));
        Assert.Same(reg.Find("stockadjustments"), reg.Find("stock-adjustments"));
        Assert.Null(reg.Find("contacts"));       // ยังไม่มี target ⇒ null (ผู้เรียกตอบ "ยังไม่รองรับ" ไม่ใช่ 500)
        Assert.Null(reg.Find(null));
        Assert.Equal(2, reg.All.Count);
    }

    [Fact]
    public void ทะเบียน_target_ชนิดซ้ำ_ล้มดัง_ไม่ใช่ตัวแรกชนะ()
        => Assert.Throws<InvalidOperationException>(() =>
            new ImportTargetRegistry(new IImportTarget[] { new FakeTarget("contacts"), new FakeTarget("Contacts") }));

    public abstract class AbstractFakeTarget : IImportTarget
    {
        public abstract string EntityType { get; }
        public string Label => "ทดสอบ";
        public IReadOnlyList<ImportFieldDto> Schema => Array.Empty<ImportFieldDto>();
        public ImportCommitStyle CommitStyle => ImportCommitStyle.SharedTransaction;
        public bool SupportsUpdate => false;
        public Task<IReadOnlyList<ImportRowValidation>> ValidateAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> rows, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ImportRowValidation>>(Array.Empty<ImportRowValidation>());
        public Task<IReadOnlyList<ImportRowCommitResult>> CommitGroupAsync(ImportTargetContext ctx, IReadOnlyList<ImportTargetRow> group, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ImportRowCommitResult>>(Array.Empty<ImportRowCommitResult>());
        public Task<IReadOnlyList<ImportUndoCheck>> CanUndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ImportUndoCheck>>(Array.Empty<ImportUndoCheck>());
        public Task<IReadOnlyList<ImportRowUndoResult>> UndoAsync(ImportTargetContext ctx, IReadOnlyList<ImportCommittedRow> rows, string reason, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<ImportRowUndoResult>>(Array.Empty<ImportRowUndoResult>());
    }

    public sealed class FakeTarget : AbstractFakeTarget
    {
        private readonly string _type;
        public FakeTarget() : this("fake") { }
        public FakeTarget(string type) { _type = type; }
        public override string EntityType => _type;
    }
}
