using System.Data.Common;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 274 — <b>ย้ายคอลัมน์ PII (EncryptedColumnConverter) ไปคีย์ปัจจุบัน</b> หลังเปลี่ยน <c>ENCRYPTION_KEY</c>
///
/// <para><b>ที่มา (บั๊ก production):</b> ผู้ใช้ตั้ง <c>ENCRYPTION_KEY</c> หลังมีพนักงานแล้ว ⇒ เลขบัตร/เลขผู้เสียภาษีที่เข้ารหัสด้วย dev key ถอดไม่ออก ⇒
/// converter คืน ciphertext ⇒ ไฟล์ ภ.ง.ด.1/1ก และ 50 ทวิ ได้ตัวเลขที่ตัดจาก base64 (5–14 หลัก) · ตอนนี้ converter ลองคีย์เดิม
/// (<see cref="EncryptionKeyResolver.PiiPreviousKeys"/>) ⇒ อ่านถูกทันที · งานนี้เข้ารหัสแถวเหล่านั้นใหม่ด้วยคีย์ปัจจุบัน
/// เพื่อให้วันหนึ่งถอดคีย์เดิมออกได้โดยข้อมูลไม่หาย</para>
///
/// <para><b>ทะเบียนคอลัมน์ = โมเดล EF</b> (ทุก property ที่ใช้ <see cref="EncryptedColumnConverter"/>) — เพิ่มคอลัมน์ PII ใหม่แล้วถูกย้ายด้วยโดยไม่ต้องแก้ไฟล์นี้ ·
/// อ่านค่าดิบด้วย SQL (ข้าม converter) ทีละชุดตาม Id · เขียนแบบมีเงื่อนไขค่าเดิม (<c>WHERE col = @เดิม</c> — ผู้ใช้บันทึกระหว่างงานวิ่ง ⇒ ของผู้ใช้ชนะ) ·
/// audit chain ต่อแถว (ชื่อช่องเท่านั้น ไม่มีค่า · PDPA) · ค่าที่ถอดไม่ออกทุกคีย์ <b>ไม่ถูกแตะ</b> (นับไว้ — รายงาน
/// <see cref="Accounting.Helpers.EmployeeIdentityHealth"/> บอกผู้ใช้ว่าแถวไหนต้องกรอกใหม่ · ห้ามแต่งค่า)</para>
/// </summary>
public static class PiiReprotectPass
{
    public const int BatchSize = 200;

    /// <param name="Undecryptable">ค่า enc1: ที่ถอดไม่ออกทุกคีย์ (กู้ไม่ได้จนกว่าจะได้คีย์เดิม หรือผู้ใช้กรอกใหม่)</param>
    /// <param name="SkippedConcurrent">ค่าถูกเปลี่ยนระหว่างอ่าน→เขียน ⇒ ไม่เขียนทับ</param>
    /// <param name="Unwrapped">รอบ 274-10: ค่าเข้ารหัสซ้อนที่แกะได้และเขียนใหม่เป็นชั้นเดียว (นับรวมใน <paramref name="Rekeyed"/> ด้วย)</param>
    /// <param name="NestedUnresolved">รอบ 274-10: ค่าเข้ารหัสซ้อนที่ชั้นในใช้ไม่ได้ — ไม่แตะ (ผู้ใช้ต้องกรอกใหม่)</param>
    public sealed record Result(int Scanned, int Rekeyed, int Undecryptable, int SkippedConcurrent, IReadOnlyDictionary<string, int> PerColumn,
        int Unwrapped = 0, int NestedUnresolved = 0);

    /// <summary>คอลัมน์ PII หนึ่งช่องจากโมเดล EF (ตาราง · คอลัมน์ · มี CompanyId ไหม)</summary>
    public sealed record PiiColumn(string Table, string Column, string Property, bool HasCompanyId, PiiValueKind Kind = PiiValueKind.Text);

    /// <summary>ทุกคอลัมน์ที่ใช้ <see cref="EncryptedColumnConverter"/> ในโมเดล — ตัวเดียวของงานนี้และรายงานแถวเสีย</summary>
    public static IReadOnlyList<PiiColumn> Columns(AccountingDbContext db)
        => db.Model.GetEntityTypes()
            .Where(t => t.GetTableName() != null && t.FindPrimaryKey()?.Properties is [{ Name: "Id" }])
            .SelectMany(t => t.GetProperties()
                .Where(p => p.GetValueConverter() is EncryptedColumnConverter)
                .Select(p => new PiiColumn(t.GetTableName()!, p.GetColumnName(), p.Name, t.FindProperty("CompanyId") != null,
                    ((EncryptedColumnConverter)p.GetValueConverter()!).Kind)))
            .DistinctBy(c => (c.Table, c.Column))
            .OrderBy(c => c.Table, StringComparer.Ordinal).ThenBy(c => c.Column, StringComparer.Ordinal)
            .ToList();

    /// <param name="currentKey">คีย์ที่ใช้เขียน · null = คีย์ของ converter (production)</param>
    /// <param name="previousKeys">คีย์เดิมที่ลองถอด · null = ชุดของ converter</param>
    /// <param name="onlyCompanyId">จำกัดบริษัท (เทสต์บนฐานร่วม/ซ่อมรายบริษัท) · null = ทุกแถว</param>
    /// <param name="beforeWrite">จุดสอดของเทสต์ — หลังอ่านก่อนเขียน (จำลองผู้ใช้บันทึกพร้อมกัน)</param>
    public static async Task<Result> RunAsync(AccountingDbContext db, CancellationToken ct = default,
        string? currentKey = null, IReadOnlyList<string>? previousKeys = null, Guid? onlyCompanyId = null,
        Func<Guid, Task>? beforeWrite = null)
    {
        var current = currentKey ?? EncryptedColumnConverter.Keys.Current;
        var previous = previousKeys ?? EncryptedColumnConverter.Keys.Previous;
        var per = new Dictionary<string, int>(StringComparer.Ordinal);
        int scanned = 0, undecryptable = 0, skipped = 0, unwrapped = 0, nestedUnresolved = 0;
        // รอบ 274-10: ไม่ตัดจบเมื่อไม่มีคีย์เดิม — แถวเข้ารหัสซ้อนบนคีย์ปัจจุบันทั้งสองชั้นยังต้องถูกแกะเป็นชั้นเดียว

        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere) await conn.OpenAsync(ct);
        try
        {
            foreach (var col in Columns(db))
            {
                if (onlyCompanyId != null && !col.HasCompanyId) continue;
                Guid? last = null;
                while (true)
                {
                    var batch = new List<(Guid Id, Guid? CompanyId, string Value)>();
                    await using (var cmd = conn.CreateCommand())
                    {
                        var company = col.HasCompanyId ? "\"CompanyId\"" : "NULL::uuid";
                        cmd.CommandText = $"SELECT \"Id\", {company}, \"{col.Column}\" FROM \"{col.Table}\" "
                            + $"WHERE {CandidateSql(col.Column)}"
                            + (last != null ? " AND \"Id\" > @last" : "")
                            + (onlyCompanyId != null ? " AND \"CompanyId\" = @cid" : "")
                            + $" ORDER BY \"Id\" LIMIT {BatchSize}";
                        if (last != null) AddParam(cmd, "last", last.Value);
                        if (onlyCompanyId != null) AddParam(cmd, "cid", onlyCompanyId.Value);
                        await using var rd = await cmd.ExecuteReaderAsync(ct);
                        while (await rd.ReadAsync(ct))
                            batch.Add((rd.GetGuid(0), rd.IsDBNull(1) ? null : rd.GetGuid(1), rd.GetString(2)));
                    }
                    if (batch.Count == 0) break;
                    last = batch[^1].Id;

                    // ฝ่ายค้าน LOW-2: UPDATE ของชุด + audit ของชุด อยู่ในธุรกรรมเดียว (ล้มกลางชุด ⇒ ไม่มีแถวที่เปลี่ยนโดยไม่มี audit)
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    var dbTx = Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(tx);
                    var audits = 0;
                    var batchPer = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var row in batch)
                    {
                        scanned++;
                        var r = EncryptedColumnConverter.Inspect(row.Value, current, previous, col.Kind);
                        if (r.State == PiiCipherState.Undecryptable) { undecryptable++; continue; }
                        // รอบ 274-10: ชั้นในใช้ไม่ได้ ⇒ ไม่แกะ (ห้ามแต่งค่า) · ถ้าชั้นนอกอยู่บนคีย์เดิม ยังย้ายชั้นนอกไปคีย์ปัจจุบันด้วย plaintext ชั้นนอก<b>ตรงตัว</b>
                        // (ไม่ใช้ r.Plain — ค่าที่ผู้ใช้ส่งมามีเครื่องหมายที่ต้องคงไว้)
                        if (r.NestedUnresolved) nestedUnresolved++;
                        if (r.State is not (PiiCipherState.Previous or PiiCipherState.Nested)) continue;
                        var plainToWrite = r.NestedUnresolved ? EncryptionHelper.Decrypt(row.Value, previous[r.KeyIndex]) : r.Plain!;
                        if (beforeWrite != null) await beforeWrite(row.Id);
                        var nv = EncryptedColumnConverter.ProtectWithCurrentKey(plainToWrite, current);
                        int n;
                        await using (var up = conn.CreateCommand())
                        {
                            up.Transaction = dbTx;
                            up.CommandText = $"UPDATE \"{col.Table}\" SET \"{col.Column}\" = @nv WHERE \"Id\" = @id AND \"{col.Column}\" = @old";
                            AddParam(up, "nv", nv);
                            AddParam(up, "id", row.Id);
                            AddParam(up, "old", row.Value);
                            n = await up.ExecuteNonQueryAsync(ct);
                        }
                        if (n != 1) { skipped++; continue; }
                        batchPer[$"{col.Table}.{col.Column}"] = batchPer.GetValueOrDefault($"{col.Table}.{col.Column}") + 1;
                        audits++;
                        var nested = r.State == PiiCipherState.Nested;
                        if (nested) unwrapped++;
                        db.AddChainedAuditLog(new AuditLog
                        {
                            CompanyId = row.CompanyId,
                            Action = AuditAction.Update,
                            EntityType = col.Table,
                            EntityId = row.Id.ToString(),
                            NewValues = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                // รอบ 274-10: แถวซ้อน = "PiiNestedUnwrapped" + จำนวนชั้น · ไม่มีค่า (PDPA)
                                action = nested ? "PiiNestedUnwrapped" : "PiiReprotected", field = col.Property, fromKeyIndex = r.KeyIndex,
                                fromDevKey = r.KeyIndex >= 0 && previous[r.KeyIndex] == EncryptionKeyResolver.DevKey,
                                nestedLayers = r.NestedLayers,
                                ruleCode = nested ? "PDPA-26-NESTED-UNWRAP" : "PDPA-26-KEY-ROTATE",
                            }),
                            Timestamp = DateTime.UtcNow,
                        });
                    }
                    if (audits > 0) await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    foreach (var kv in batchPer) per[kv.Key] = per.GetValueOrDefault(kv.Key) + kv.Value;
                    db.ChangeTracker.Clear();
                    if (batch.Count < BatchSize) break;
                }
            }
        }
        finally
        {
            if (openedHere) await conn.CloseAsync();
        }
        return new Result(scanned, per.Values.Sum(), undecryptable, skipped, per, unwrapped, nestedUnresolved);
    }

    /// <summary>แถวที่อาจเป็น ciphertext — enc1 หรือรุ่นก่อนไม่มีคำนำหน้า (base64 ล้วน ≥ 40 ตัว · ฝ่ายค้าน MED-1 เดิมเลือกแค่ enc1) ·
    /// ตัดสินจริงด้วย <see cref="EncryptedColumnConverter.Inspect"/> ตัวเดียว</summary>
    public static string CandidateSql(string column)
        => $"(\"{column}\" LIKE 'enc1:%' OR (length(\"{column}\") >= 40 AND \"{column}\" ~ '^[A-Za-z0-9+/]+={{0,2}}$'))";

    private static void AddParam(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }
}

/// <summary>รันรอบเดียวหลังบูต (รอ migration 3 นาที) ใต้ <see cref="JobLock"/> — idempotent (แถวที่อยู่บนคีย์ปัจจุบันแล้วถูกข้าม) ·
/// ผลลง <see cref="IJobRunRecorder"/> · ล้ม = LogError (ไม่ล้มแอป — ฝั่งอ่านยังถอดด้วยคีย์เดิมได้ จะลองใหม่ในการบูตครั้งถัดไป)</summary>
public class PiiReprotectJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PiiReprotectJob> _logger;

    public PiiReprotectJob(IServiceScopeFactory scopeFactory, ILogger<PiiReprotectJob> logger)
    { _scopeFactory = scopeFactory; _logger = logger; }

    /// <summary>ชื่องานใน JobRunLog — รายงาน <see cref="EmployeeIdentityHealth"/> อ่านผลรันล่าสุดจากชื่อนี้</summary>
    public const string JobName = "PiiReprotect";
    /// <summary>ฝ่ายค้าน MED-2: รันเป็นระยะ (ไม่ใช่แค่ตอนบูต) — แถวที่เพิ่งตั้งคีย์เดิมถูกย้ายโดยไม่ต้องรีสตาร์ตอีกรอบ</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        var startedAt = DateTime.UtcNow;
        IJobRunRecorder? recorder = null;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
            await JobLock.RunExclusiveAsync(db, AdvisoryLockKey.BackgroundJob, nameof(PiiReprotectJob), async () =>
            {
                var r = await PiiReprotectPass.RunAsync(db, stoppingToken);
                var summary = $"ตรวจ PII ที่เข้ารหัส {r.Scanned} ค่า · เข้ารหัสใหม่ด้วยคีย์ปัจจุบัน {r.Rekeyed} ค่า"
                    + (r.PerColumn.Count == 0 ? "" : " (" + string.Join(", ", r.PerColumn.Select(kv => $"{kv.Key}={kv.Value}")) + ")")
                    + (r.Undecryptable == 0 ? "" : $" · ⛔ ถอดไม่ออกทุกคีย์ {r.Undecryptable} ค่า — ตั้ง {EncryptionKeyResolver.PreviousKeyEnv} เป็นคีย์เดิม หรือให้ผู้ใช้กรอกใหม่ "
                        + "(รายงาน: GET payroll/tax-identity-health)")
                    + (r.Unwrapped == 0 ? "" : $" · แกะค่าเข้ารหัสซ้อนเป็นชั้นเดียว {r.Unwrapped} ค่า")
                    + (r.NestedUnresolved == 0 ? "" : $" · ⛔ เข้ารหัสซ้อนแต่ชั้นในใช้ไม่ได้ {r.NestedUnresolved} ค่า — ให้ผู้ใช้กรอกใหม่ (รายงาน: GET payroll/tax-identity-health)")
                    + (r.SkippedConcurrent == 0 ? "" : $" · ข้าม {r.SkippedConcurrent} ค่าที่ผู้ใช้เพิ่งแก้");
                if (r.Rekeyed > 0 || r.Undecryptable > 0 || r.NestedUnresolved > 0) _logger.LogWarning("PiiReprotect: {Summary}", summary);
                // สำเร็จ = งานรันจบ (ค่าที่ถอดไม่ออกเป็นงานของผู้ใช้ — อยู่ในข้อความ + รายงานรายแถว)
                if (recorder != null) await recorder.RecordAsync(JobName, true, summary, r.Rekeyed, startedAt);
            }, _logger, ct: stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // ล้มดัง 3 ที่: LogError · JobRunLog (สถานะงาน) · รายงาน tax-identity-health แสดง "งานย้ายคีย์ล้ม" แทน "ไม่ต้องทำอะไร"
            _logger.LogError(ex, "PiiReprotectJob ล้ม — คอลัมน์ PII ที่เข้ารหัสด้วยคีย์เดิมยังอ่านได้ผ่านคีย์เดิม จะลองใหม่ในรอบถัดไป");
            if (recorder != null) await recorder.RecordAsync(JobName, false, "ล้ม: " + ex.Message, 0, startedAt);
        }
    }
}
