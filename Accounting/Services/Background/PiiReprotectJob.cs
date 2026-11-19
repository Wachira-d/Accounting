using System.Data.Common;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 273 — <b>ย้ายคอลัมน์ PII (EncryptedColumnConverter) ไปคีย์ปัจจุบัน</b> หลังเปลี่ยน <c>ENCRYPTION_KEY</c>
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
    public sealed record Result(int Scanned, int Rekeyed, int Undecryptable, int SkippedConcurrent, IReadOnlyDictionary<string, int> PerColumn);

    /// <summary>คอลัมน์ PII หนึ่งช่องจากโมเดล EF (ตาราง · คอลัมน์ · มี CompanyId ไหม)</summary>
    public sealed record PiiColumn(string Table, string Column, string Property, bool HasCompanyId);

    /// <summary>ทุกคอลัมน์ที่ใช้ <see cref="EncryptedColumnConverter"/> ในโมเดล — ตัวเดียวของงานนี้และรายงานแถวเสีย</summary>
    public static IReadOnlyList<PiiColumn> Columns(AccountingDbContext db)
        => db.Model.GetEntityTypes()
            .Where(t => t.GetTableName() != null && t.FindPrimaryKey()?.Properties is [{ Name: "Id" }])
            .SelectMany(t => t.GetProperties()
                .Where(p => p.GetValueConverter() is EncryptedColumnConverter)
                .Select(p => new PiiColumn(t.GetTableName()!, p.GetColumnName(), p.Name, t.FindProperty("CompanyId") != null)))
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
        int scanned = 0, undecryptable = 0, skipped = 0;
        if (previous.Count == 0) return new Result(0, 0, 0, 0, per);   // ไม่มีคีย์เดิมให้ลอง = ไม่มีอะไรย้ายได้

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
                            + $"WHERE \"{col.Column}\" LIKE 'enc1:%'"
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

                    var audits = 0;
                    foreach (var row in batch)
                    {
                        scanned++;
                        var r = EncryptedColumnConverter.Inspect(row.Value, current, previous);
                        if (r.State == PiiCipherState.Undecryptable) { undecryptable++; continue; }
                        if (r.State != PiiCipherState.Previous) continue;
                        if (beforeWrite != null) await beforeWrite(row.Id);
                        var nv = EncryptedColumnConverter.ProtectWithCurrentKey(r.Plain!, current);
                        int n;
                        await using (var up = conn.CreateCommand())
                        {
                            up.CommandText = $"UPDATE \"{col.Table}\" SET \"{col.Column}\" = @nv WHERE \"Id\" = @id AND \"{col.Column}\" = @old";
                            AddParam(up, "nv", nv);
                            AddParam(up, "id", row.Id);
                            AddParam(up, "old", row.Value);
                            n = await up.ExecuteNonQueryAsync(ct);
                        }
                        if (n != 1) { skipped++; continue; }
                        per[$"{col.Table}.{col.Column}"] = per.GetValueOrDefault($"{col.Table}.{col.Column}") + 1;
                        audits++;
                        db.AddChainedAuditLog(new AuditLog
                        {
                            CompanyId = row.CompanyId,
                            Action = AuditAction.Update,
                            EntityType = col.Table,
                            EntityId = row.Id.ToString(),
                            NewValues = System.Text.Json.JsonSerializer.Serialize(new
                            {
                                action = "PiiReprotected", field = col.Property, fromKeyIndex = r.KeyIndex, ruleCode = "PDPA-26-KEY-ROTATE",
                            }),
                            Timestamp = DateTime.UtcNow,
                        });
                    }
                    if (audits > 0) await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                    if (batch.Count < BatchSize) break;
                }
            }
        }
        finally
        {
            if (openedHere) await conn.CloseAsync();
        }
        return new Result(scanned, per.Values.Sum(), undecryptable, skipped, per);
    }

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch { return; }
        var startedAt = DateTime.UtcNow;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();
            await JobLock.RunExclusiveAsync(db, AdvisoryLockKey.BackgroundJob, nameof(PiiReprotectJob), async () =>
            {
                var r = await PiiReprotectPass.RunAsync(db, stoppingToken);
                var summary = $"ตรวจ PII ที่เข้ารหัส {r.Scanned} ค่า · เข้ารหัสใหม่ด้วยคีย์ปัจจุบัน {r.Rekeyed} ค่า"
                    + (r.PerColumn.Count == 0 ? "" : " (" + string.Join(", ", r.PerColumn.Select(kv => $"{kv.Key}={kv.Value}")) + ")")
                    + (r.Undecryptable == 0 ? "" : $" · ⛔ ถอดไม่ออกทุกคีย์ {r.Undecryptable} ค่า — ตั้ง {EncryptionKeyResolver.PreviousKeyEnv} เป็นคีย์เดิม หรือให้ผู้ใช้กรอกใหม่ "
                        + "(รายงาน: หน้าเงินเดือน › เลขประจำตัวที่ต้องแก้)")
                    + (r.SkippedConcurrent == 0 ? "" : $" · ข้าม {r.SkippedConcurrent} ค่าที่ผู้ใช้เพิ่งแก้");
                if (r.Rekeyed > 0 || r.Undecryptable > 0) _logger.LogWarning("PiiReprotect: {Summary}", summary);
                if (recorder != null) await recorder.RecordAsync("PiiReprotect", r.Undecryptable == 0, summary, r.Rekeyed, startedAt);
            }, _logger, ct: stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PiiReprotectJob ล้ม — คอลัมน์ PII ที่เข้ารหัสด้วยคีย์เดิมยังอ่านได้ผ่านคีย์เดิม จะลองใหม่ในการบูตครั้งถัดไป");
        }
    }
}
