using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **ปิดการเชื่อมต่อ LINE ของลูกค้า — เส้นเดียวของทุกทางเข้า** (รอบ 211 ฝ่ายค้าน ข้อ 2/3):
/// ลูกค้ากดยกเลิก · ผูกใหม่ทับ · DSR ลบ (<c>PdpaService.ApplyCmsErasureAsync</c>) เดินที่นี่ทั้งหมด ⇒ ทุกทางได้ครบ 4 อย่างเหมือนกัน:
/// (1) ถอนความยินยอมของแถวนั้น (2) ปิดลิงก์ + ล้าง LINE userId (<see cref="CustomerLineLinkRule.Close"/>) (3) audit chain (userId แบบปิดบัง)
/// (4) เผารหัสผูกที่ยังค้างของเป้าหมายเดียวกัน (ไม่งั้นรหัสที่ออกก่อนลบยังผูกกลับได้อีก 30 นาที)
/// <para>ผู้เรียก SaveChanges เอง (ข้อ 4 เป็น ExecuteUpdate ทำทันที — ทิศที่ปลอดภัย: เผาเกินดีกว่าค้าง)</para>
/// </summary>
public static class CustomerLineLinkClosure
{
    public static async Task<int> CloseAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<CustomerLineLink> rows,
        string reason, DateTime nowUtc, CancellationToken ct)
    {
        if (rows.Count == 0) return 0;
        var consentIds = rows.Where(r => r.ConsentRecordId != null).Select(r => r.ConsentRecordId!.Value).ToList();
        if (consentIds.Count > 0)
        {
            var consents = await db.Set<PdpaConsentRecord>()
                .Where(c => c.CompanyId == companyId && consentIds.Contains(c.Id) && c.WithdrawnAt == null).ToListAsync(ct);
            foreach (var c in consents) { c.WithdrawnAt = nowUtc; c.WithdrawnReason = $"ยกเลิกการเชื่อมต่อ LINE ({reason})"; }
        }
        var changed = 0;
        foreach (var r in rows)
        {
            var masked = CustomerLineLinkRule.Mask(r.LineUserId);
            var n = CustomerLineLinkRule.Close(r, reason, nowUtc);
            if (n == 0) continue;
            changed += n;
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(CustomerLineLink), EntityId = r.Id.ToString(),
                NewValues = JsonSerializer.Serialize(new { unlinked = true, reason, r.TargetKind, r.TargetId, lineUser = masked, rule = CustomerLineLinkRule.RuleCode }),
            });
        }
        foreach (var t in rows.Select(r => (r.TargetKind, r.TargetId)).Distinct())
            await BurnPendingCodesAsync(db, companyId, t.TargetKind, t.TargetId, nowUtc, ct);
        return changed;
    }

    /// <summary>รหัสผูกที่ยังไม่ถูกใช้ของเป้าหมายนี้ ⇒ ใช้ไม่ได้อีก (ประทับ UsedAt)</summary>
    public static Task<int> BurnPendingCodesAsync(AccountingDbContext db, Guid companyId, string kind, Guid targetId, DateTime nowUtc, CancellationToken ct)
        => db.CustomerLineLinkCodes
            .Where(c => c.CompanyId == companyId && c.TargetKind == kind && c.TargetId == targetId && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, nowUtc), ct);

    /// <summary>
    /// งานเก็บกวาดรายคืน (เรียกจาก <c>PdpaRetentionPurgeJob</c> ใต้ <see cref="JobLock"/>) — ลบรหัสที่หมดอายุเกิน <see cref="CustomerLineLinkRule.CodeRetention"/> ·
    /// ล้าง LINE userId ที่ยังค้างบนลิงก์ที่ปิดแล้ว (แถวรุ่นก่อนรอบ 211 ฝ่ายค้าน) · คืน (รหัสที่ลบ, ลิงก์ที่ล้าง)
    /// </summary>
    public static async Task<(int CodesDeleted, int LinksCleared)> PurgeAsync(AccountingDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var cutoff = CustomerLineLinkRule.CodePurgeCutoff(nowUtc);
        var codes = await db.CustomerLineLinkCodes.IgnoreQueryFilters().Where(c => c.ExpiresAt < cutoff).ExecuteDeleteAsync(ct);
        var links = await db.CustomerLineLinks.IgnoreQueryFilters().Where(l => l.UnlinkedAt != null && l.LineUserId != "")
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.LineUserId, "").SetProperty(l => l.UpdatedAt, nowUtc), ct);
        return (codes, links);
    }
}
