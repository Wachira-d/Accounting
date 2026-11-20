using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Security;

/// <summary>
/// รอบ 275 แพ็ก A (T4 M-4) — ผู้อ่าน/ผู้เขียน<b>เดียว</b>ของ <c>SiteSettings.EncryptionKeyFingerprint*</c> · ตัวตัดสิน = <see cref="EncryptionKeyFingerprintRule"/>
///
/// <para>เรียกจากงานย้ายคีย์ทั้งสอง (<c>SecretReprotectJob</c> · <c>PiiReprotectJob</c>) ทุกรอบ<b>ก่อน</b>ย้าย — ไม่ตรง ⇒ ไม่ย้าย + LogError + JobRunLog ·
/// หน้า "สุขภาพข้อมูลเข้ารหัส" อ่านผล (<see cref="CheckAsync"/> โหมดอ่านอย่างเดียว) · ผู้ดูแลแพลตฟอร์มยืนยันคีย์ปัจจุบันได้ (<see cref="AcceptCurrentAsync"/> · audit)</para>
/// </summary>
public static class EncryptionKeyFingerprintService
{
    /// <summary>สถานะคีย์ของเครื่องนี้ · ลายนิ้วมือ (ไม่ใช่คีย์) แสดงได้</summary>
    public sealed record Status(KeyFingerprintVerdict Verdict, string? Stored, string? Current, DateTime? StoredAt)
    {
        public bool AllowsRekey => EncryptionKeyFingerprintRule.AllowsRekey(Verdict);
        public string? AdminMessage => EncryptionKeyFingerprintRule.AdminMessage(Verdict);
        public string? TenantMessage => EncryptionKeyFingerprintRule.TenantMessage(Verdict);
    }

    /// <param name="persist">true = บันทึกลายนิ้วมือเมื่อ FirstSeen/Rotated (งานย้ายคีย์) · false = อ่านอย่างเดียว (หน้ารายงาน)</param>
    public static async Task<Status> CheckAsync(AccountingDbContext db, ISecretProtector secrets, bool persist, ILogger? logger = null, CancellationToken ct = default)
    {
        var current = secrets.CurrentKeyFingerprint;
        var row = await db.SiteSettings.AsNoTracking().OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.EncryptionKeyFingerprint, s.EncryptionKeyFingerprintPrevious, s.EncryptionKeyFingerprintAt })
            .FirstOrDefaultAsync(ct);
        if (row == null) return new(KeyFingerprintVerdict.NotChecked, null, current, null);
        var verdict = EncryptionKeyFingerprintRule.Evaluate(row.EncryptionKeyFingerprint, row.EncryptionKeyFingerprintPrevious, current,
            secrets.PreviousKeyFingerprints.ToList());
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
                    logger?.LogWarning("คีย์เข้ารหัสถูกหมุน (ลายนิ้วมือ {Old} → {New}) — งานย้ายคีย์จะย้ายข้อมูลไปคีย์ใหม่", old, current);
                return new(verdict, current, current, now);
            }
            // แพ้การแข่ง ⇒ อ่านใหม่แล้วตัดสินอีกครั้ง (ไม่บันทึก)
            return await CheckAsync(db, secrets, persist: false, logger, ct);
        }
        if (EncryptionKeyFingerprintRule.IsProblem(verdict))
            logger?.LogError("{RuleCode}: {Message} (ฐาน {Stored} · เครื่องนี้ {Current})", EncryptionKeyFingerprintRule.RuleCode,
                EncryptionKeyFingerprintRule.AdminMessage(verdict), row.EncryptionKeyFingerprint, current);
        return new(verdict, row.EncryptionKeyFingerprint, current, row.EncryptionKeyFingerprintAt);
    }

    /// <summary>ผู้ดูแลแพลตฟอร์มยืนยันว่า "คีย์ของเครื่องนี้คือคีย์ที่ถูก" (หลังตรวจทุกเครื่องแล้ว) — บันทึกลายนิ้วมือ + audit พร้อมเหตุ ·
    /// สภาพไม่มีปัญหา ⇒ ไม่ทำอะไร (ไม่ประทับ audit เปล่า)</summary>
    public static async Task<Status> AcceptCurrentAsync(AccountingDbContext db, ISecretProtector secrets, Guid? actorId, string? actorEmail, string reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new BusinessRuleException("ระบุเหตุผลที่ยืนยันคีย์ปัจจุบัน (บันทึกลงประวัติการตรวจสอบ)", EncryptionKeyFingerprintRule.RuleCode);
        var status = await CheckAsync(db, secrets, persist: false, null, ct);
        if (!EncryptionKeyFingerprintRule.IsProblem(status.Verdict) || status.Current == null) return status;
        var row = await db.SiteSettings.OrderBy(s => s.Id).FirstAsync(ct);
        var old = row.EncryptionKeyFingerprint;
        row.EncryptionKeyFingerprintPrevious = old;
        row.EncryptionKeyFingerprint = status.Current;
        row.EncryptionKeyFingerprintAt = DateTime.UtcNow;
        AddAudit(db, row.Id, "EncryptionKeyFingerprintAccepted", old, status.Current, reason.Trim(), actorId, actorEmail);
        await db.SaveChangesAsync(ct);
        return await CheckAsync(db, secrets, persist: false, null, ct);
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
