using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Security;

/// <summary>
/// รอบ 275 แพ็ก A (T4 M-4 + ฝ่ายค้าน MED-1/2/3) — ผู้อ่าน/ผู้เขียน<b>เดียว</b>ของ <c>SiteSettings.EncryptionKeyFingerprint*</c> และ <c>EncryptionKeyHeartbeats</c> ·
/// ตัวตัดสิน = <see cref="EncryptionKeyFingerprintRule"/>
///
/// <list type="bullet">
/// <item>ทุกเครื่องส่ง heartbeat (ลายนิ้วมือคีย์ของตัวเอง) ทุก <see cref="HeartbeatInterval"/> — ผู้ดูแลเห็นลายนิ้วมือของแต่ละเครื่อง</item>
/// <item><b>หมุนคีย์</b> (ฐานบันทึกคีย์ที่อยู่ในห่วงโซ่เดิมของเครื่องนี้) มีผลเฉพาะเมื่อทุกเครื่องที่ส่ง heartbeat ภายใน <see cref="HeartbeatWindow"/> รายงานคีย์ใหม่แล้ว
///   หรือผู้ดูแลยืนยันลายนิ้วมือ (MED-3) — ยังไม่ครบ ⇒ <see cref="KeyFingerprintVerdict.RotationPending"/> (ไม่ย้ายแถว)</item>
/// <item>ไม่ตรง (Mismatch/RotatedBack) ⇒ ไม่ย้ายแถว + <b>ปิดการเขียนค่าเข้ารหัสใหม่ของเครื่องนี้</b> (<see cref="EncryptionWriteGuard"/> · MED-2) + LogError</item>
/// <item>ผู้ดูแลยืนยัน "ลายนิ้วมือที่ระบุ" — เครื่องที่รับคำขอต้องใช้คีย์นั้นจริง ไม่ตรง ⇒ ปฏิเสธ (MED-1) · เขียนแบบมีเงื่อนไขค่าเดิม</item>
/// </list>
/// </summary>
public static class EncryptionKeyFingerprintService
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    /// <summary>เครื่องที่ส่ง heartbeat ภายในช่วงนี้นับว่า "ยังทำงาน" (3 เท่าของรอบ heartbeat)</summary>
    public static readonly TimeSpan HeartbeatWindow = TimeSpan.FromMinutes(15);

    /// <summary>รหัส process นี้ — เครื่อง:pid:เวลาเริ่ม (รีสตาร์ต = แถวใหม่ · แถวเก่าหมดอายุเอง)</summary>
    public static readonly string InstanceId =
        $"{Environment.MachineName}:{Environment.ProcessId}:{DateTime.UtcNow:yyyyMMddHHmmss}";

    public sealed record Instance(string InstanceId, string Fingerprint, DateTime SeenAt, bool Recent);

    /// <summary>สถานะคีย์ของเครื่องนี้ · ลายนิ้วมือ (ไม่ใช่คีย์) แสดงได้</summary>
    public sealed record Status(KeyFingerprintVerdict Verdict, string? Stored, string? Current, DateTime? StoredAt,
        IReadOnlyList<Instance>? Instances = null)
    {
        public bool AllowsRekey => EncryptionKeyFingerprintRule.AllowsRekey(Verdict);
        public string? AdminMessage => EncryptionKeyFingerprintRule.AdminMessage(Verdict);
        public string? TenantMessage => EncryptionKeyFingerprintRule.TenantMessage(Verdict);
    }

    /// <summary>บันทึก heartbeat ของ process นี้ (upsert) + ล้างแถวที่เงียบเกิน 1 วัน · ตัวแทนในเทสต์ (ไม่มีลายนิ้วมือ) ⇒ ไม่ทำอะไร</summary>
    public static async Task HeartbeatAsync(AccountingDbContext db, ISecretProtector secrets, CancellationToken ct = default, string? instanceId = null)
    {
        var fp = secrets.CurrentKeyFingerprint;
        if (string.IsNullOrEmpty(fp)) return;
        var id = instanceId ?? InstanceId;
        var now = DateTime.UtcNow;
        var prev = string.Join(",", secrets.PreviousKeyFingerprints);
        var n = await db.EncryptionKeyHeartbeats.Where(h => h.InstanceId == id)
            .ExecuteUpdateAsync(u => u.SetProperty(h => h.Fingerprint, fp).SetProperty(h => h.PreviousFingerprints, prev).SetProperty(h => h.SeenAt, now), ct);
        if (n == 0)
        {
            var hb = new EncryptionKeyHeartbeat { InstanceId = id, Fingerprint = fp, PreviousFingerprints = prev, SeenAt = now };
            db.EncryptionKeyHeartbeats.Add(hb);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { /* อีกงานใน process เดียวกันเพิ่งเขียน — แถวมีแล้ว */ }
            finally { db.Entry(hb).State = EntityState.Detached; }   // ไม่ค้างใน tracker (รอบถัดไปใช้ ExecuteUpdate)
        }
        var stale = now.AddDays(-1);
        await db.EncryptionKeyHeartbeats.Where(h => h.SeenAt < stale).ExecuteDeleteAsync(ct);
    }

    public static async Task<IReadOnlyList<Instance>> InstancesAsync(AccountingDbContext db, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow - HeartbeatWindow;
        var rows = await db.EncryptionKeyHeartbeats.AsNoTracking().OrderByDescending(h => h.SeenAt).Take(100).ToListAsync(ct);
        return rows.Select(h => new Instance(h.InstanceId, h.Fingerprint, h.SeenAt, h.SeenAt >= since)).ToList();
    }

    /// <param name="persist">true = งานย้ายคีย์/ตอนบูต: ส่ง heartbeat + บันทึกลายนิ้วมือเมื่อ FirstSeen/หมุนครบ · false = อ่านอย่างเดียว (หน้ารายงาน)</param>
    /// <param name="applyWriteGuard">true = ตั้ง <see cref="EncryptionWriteGuard"/> ของ process นี้ตามผล (บูต/งานเบื้องหลัง — เทสต์ไม่ตั้ง เพราะเป็นสถานะทั้ง process)</param>
    public static async Task<Status> CheckKeyAsync(AccountingDbContext db, ISecretProtector secrets, bool persist, ILogger? logger = null,
        CancellationToken ct = default, bool applyWriteGuard = false, string? instanceId = null)
    {
        var current = secrets.CurrentKeyFingerprint;
        if (persist) await HeartbeatAsync(db, secrets, ct, instanceId);
        var row = await db.SiteSettings.AsNoTracking()
            .Select(s => new { s.Id, s.EncryptionKeyFingerprint, s.EncryptionKeyFingerprintPrevious, s.EncryptionKeyFingerprintAt })
            .FirstOrDefaultAsync(ct);
        var instances = await InstancesAsync(db, ct);
        if (row == null) return Done(new(KeyFingerprintVerdict.NotChecked, null, current, null, instances));
        var verdict = EncryptionKeyFingerprintRule.Evaluate(row.EncryptionKeyFingerprint, row.EncryptionKeyFingerprintPrevious, current,
            secrets.PreviousKeyFingerprints.ToList());
        if (verdict == KeyFingerprintVerdict.Rotated
            && !EncryptionKeyFingerprintRule.AllInstancesOn(current!, instances.Where(i => i.Recent).Select(i => i.Fingerprint).ToList()))
            verdict = KeyFingerprintVerdict.RotationPending;   // MED-3: ยังมีเครื่องที่ใช้คีย์อื่น ⇒ ไม่ย้ายแถว
        if (persist && EncryptionKeyFingerprintRule.ShouldStore(verdict))
        {
            var now = DateTime.UtcNow;
            var old = row.EncryptionKeyFingerprint;
            // เขียนแบบมีเงื่อนไขค่าเดิม — สองเครื่องตรวจพร้อมกัน ⇒ เครื่องเดียวชนะ (อีกเครื่องเห็นค่าใหม่ในรอบถัดไป)
            var n = await db.SiteSettings.Where(s => s.Id == row.Id && s.EncryptionKeyFingerprint == old)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.EncryptionKeyFingerprint, current)
                    .SetProperty(s => s.EncryptionKeyFingerprintPrevious, old)
                    .SetProperty(s => s.EncryptionKeyFingerprintAt, now), ct);
            if (n == 1)
            {
                AddAudit(db, row.Id, verdict == KeyFingerprintVerdict.Rotated ? "EncryptionKeyRotated" : "EncryptionKeyFingerprintRecorded", old, current, null);
                await db.SaveChangesAsync(ct);
                if (verdict == KeyFingerprintVerdict.Rotated)
                    logger?.LogWarning("คีย์เข้ารหัสถูกหมุน (ลายนิ้วมือ {Old} → {New} · ทุกเครื่องรายงานคีย์ใหม่แล้ว) — งานย้ายคีย์จะย้ายข้อมูลไปคีย์ใหม่", old, current);
                return Done(new(verdict, current, current, now, instances));
            }
            return await CheckKeyAsync(db, secrets, persist: false, logger, ct, applyWriteGuard);   // แพ้การแข่ง ⇒ อ่านใหม่ (ไม่บันทึก)
        }
        if (EncryptionKeyFingerprintRule.IsProblem(verdict))
            logger?.LogError("{RuleCode}: {Message} (ฐาน {Stored} · เครื่องนี้ {Current})", EncryptionKeyFingerprintRule.RuleCode,
                EncryptionKeyFingerprintRule.AdminMessage(verdict), row.EncryptionKeyFingerprint, current);
        return Done(new(verdict, row.EncryptionKeyFingerprint, current, row.EncryptionKeyFingerprintAt, instances));

        Status Done(Status s)
        {
            if (applyWriteGuard) EncryptionWriteGuard.Apply(s.Verdict);
            return s;
        }
    }

    /// <summary>ผู้ดูแลแพลตฟอร์มยืนยันว่า <paramref name="fingerprint"/> คือคีย์ที่ถูก (ฝ่ายค้าน MED-1: ยืนยัน "ลายนิ้วมือที่ระบุ" ไม่ใช่ "เครื่องที่รับคำขอ") ·
    /// เครื่องที่รับคำขอไม่ได้ใช้คีย์นั้น ⇒ ปฏิเสธ 409 (ลองใหม่ให้คำขอไปเครื่องที่ใช้คีย์นั้น) · ไม่มีอะไรต้องยืนยัน ⇒ ไม่ทำอะไร ·
    /// เขียนแบบมีเงื่อนไขค่าเดิม (ค่าเปลี่ยนระหว่างกด ⇒ 409) · audit พร้อมเหตุผล</summary>
    public static async Task<Status> AcceptAsync(AccountingDbContext db, ISecretProtector secrets, string fingerprint, Guid? actorId, string? actorEmail,
        string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("ระบุเหตุผลที่ยืนยันลายนิ้วมือคีย์ (บันทึกลงประวัติการตรวจสอบ)", EncryptionKeyFingerprintRule.RuleCode);
        if (string.IsNullOrWhiteSpace(fingerprint)) throw new BusinessRuleException("เลือกลายนิ้วมือคีย์ที่จะยืนยัน", EncryptionKeyFingerprintRule.RuleCode);
        var current = secrets.CurrentKeyFingerprint;
        if (!string.Equals(current, fingerprint.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException($"เครื่องที่รับคำขอนี้ใช้คีย์ลายนิ้วมือ {current ?? "-"} ไม่ใช่ {fingerprint.Trim()} ที่เลือก — ไม่บันทึก · "
                + "กดอีกครั้ง (คำขอจะไปเครื่องอื่น) หรือแก้ ENCRYPTION_KEY ของเครื่องนี้ให้ตรงก่อน", EncryptionKeyFingerprintRule.RuleCode, 409);
        var status = await CheckKeyAsync(db, secrets, persist: false, null, ct);
        if (!EncryptionKeyFingerprintRule.NeedsAdmin(status.Verdict)) return status;
        var row = await db.SiteSettings.AsNoTracking().Select(s => new { s.Id, s.EncryptionKeyFingerprint }).FirstAsync(ct);   // แถวเดียวกับ AdminController (FirstOrDefault)
        var old = row.EncryptionKeyFingerprint;
        var now = DateTime.UtcNow;
        var n = await db.SiteSettings.Where(s => s.Id == row.Id && s.EncryptionKeyFingerprint == old)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.EncryptionKeyFingerprint, current)
                .SetProperty(s => s.EncryptionKeyFingerprintPrevious, old)
                .SetProperty(s => s.EncryptionKeyFingerprintAt, now), ct);
        if (n != 1) throw new BusinessRuleException("ลายนิ้วมือคีย์ในระบบเปลี่ยนระหว่างกด — โหลดหน้าใหม่แล้วตรวจอีกครั้ง", EncryptionKeyFingerprintRule.RuleCode, 409);
        AddAudit(db, row.Id, "EncryptionKeyFingerprintAccepted", old, current, reason.Trim(), actorId, actorEmail);
        await db.SaveChangesAsync(ct);
        return await CheckKeyAsync(db, secrets, persist: false, null, ct);
    }

    private static void AddAudit(AccountingDbContext db, Guid siteId, string action, string? oldFp, string? newFp, string? reason,
        Guid? actorId = null, string? actorEmail = null)
        => db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = null,
            UserId = actorId,
            UserEmail = actorEmail,
            Action = AuditAction.Update,
            EntityType = "SiteSettings",
            EntityId = siteId.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { fingerprint = oldFp }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { action, fingerprint = newFp, reason, ruleCode = EncryptionKeyFingerprintRule.RuleCode }),
            Timestamp = DateTime.UtcNow,
        });
}
