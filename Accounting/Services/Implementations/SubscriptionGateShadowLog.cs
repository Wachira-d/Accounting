using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <inheritdoc cref="ISubscriptionGateShadowLog"/>
public class SubscriptionGateShadowLog : ISubscriptionGateShadowLog
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<SubscriptionGateShadowLog> _logger;

    public SubscriptionGateShadowLog(AccountingDbContext db, ILogger<SubscriptionGateShadowLog> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<SubscriptionEnforcementMode> GetWebModeAsync(CancellationToken ct = default)
    {
        try
        {
            var mode = await _db.SiteSettings.AsNoTracking()
                .OrderBy(s => s.CreatedAt)
                .Select(s => (SubscriptionEnforcementMode?)s.SubscriptionEnforcementMode)
                .FirstOrDefaultAsync(ct);
            return mode ?? SubscriptionEnforcementMode.Shadow;
        }
        catch (Exception ex)
        {
            // คอลัมน์ยังไม่ถูกสร้าง (migration ยังไม่รัน) / DB สะดุด → ค่าตั้งต้น ไม่บล็อกใคร
            _logger.LogWarning(ex, "อ่านสวิตช์ SubscriptionEnforcementMode ไม่ได้ — ใช้ Shadow");
            return SubscriptionEnforcementMode.Shadow;
        }
    }

    public async Task RecordAsync(SubscriptionGateShadowHit hit, CancellationToken ct = default)
    {
        // คีย์ไม่มีค่าว่างแบบ NULL (unique index ไม่ถือ NULL ว่าซ้ำกัน) ⇒ ไม่มีฟีเจอร์ = ''
        var feature = hit.Feature ?? "";
        var now = DateTime.UtcNow;
        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "SubscriptionGateShadowHits"
                    ("Id", "CompanyId", "Reason", "Feature", "Plan", "RouteKey", "LastMethod", "WouldBlock",
                     "HitCount", "FirstSeenAt", "LastSeenAt")
                VALUES (gen_random_uuid(), {0}, {1}, {2}, {3}, {4}, {5}, {6}, 1, {7}, {7})
                ON CONFLICT ("CompanyId", "Reason", "Feature")
                DO UPDATE SET "HitCount" = "SubscriptionGateShadowHits"."HitCount" + 1,
                              "Plan" = EXCLUDED."Plan",
                              "RouteKey" = EXCLUDED."RouteKey",
                              "LastMethod" = EXCLUDED."LastMethod",
                              "WouldBlock" = EXCLUDED."WouldBlock",
                              "LastSeenAt" = EXCLUDED."LastSeenAt";
                """,
                new object[]
                {
                    hit.CompanyId, hit.Reason.ToString(), feature, (object?)hit.Plan ?? DBNull.Value,
                    (object?)hit.RouteKey ?? DBNull.Value, hit.Method, hit.WouldBlock, now,
                },
                ct);
        }
        catch (Exception ex)
        {
            // fail-open โดยตั้งใจ (โหมดเงาไม่บล็อกอยู่แล้ว) — ตารางพังต้องไม่ทำให้หน้าเว็บของลูกค้าล้ม
            _logger.LogWarning(ex, "บันทึกผลโหมดเงา gate แพ็กเกจไม่ได้ (บริษัท {CompanyId} · {Reason})",
                hit.CompanyId, hit.Reason);
        }
    }

    public async Task<IReadOnlyList<SubscriptionGateShadowRow>> ListAsync(int limit, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, 5000);
        var rows = await _db.Database
            .SqlQueryRaw<ShadowRow>(
                """
                SELECT "CompanyId" AS "CompanyId", "Reason" AS "Reason", "Feature" AS "Feature", "Plan" AS "Plan",
                       "RouteKey" AS "RouteKey", "LastMethod" AS "LastMethod", "WouldBlock" AS "WouldBlock",
                       "HitCount" AS "HitCount", "FirstSeenAt" AS "FirstSeenAt", "LastSeenAt" AS "LastSeenAt"
                FROM "SubscriptionGateShadowHits"
                ORDER BY "LastSeenAt" DESC
                LIMIT {0}
                """, take)
            .ToListAsync(ct);
        return rows
            .Select(r => new SubscriptionGateShadowRow(r.CompanyId, r.Reason, r.Feature, r.Plan, r.RouteKey,
                r.LastMethod, r.WouldBlock, r.HitCount,
                // เขียนเป็น UTC (คอลัมน์ timestamp ไม่มีโซน) — ติด Kind ให้ JSON มี "Z" หน้าเว็บจะได้แปลงเป็นเวลาไทยถูก
                DateTime.SpecifyKind(r.FirstSeenAt, DateTimeKind.Utc),
                DateTime.SpecifyKind(r.LastSeenAt, DateTimeKind.Utc)))
            .ToList();
    }

    public Task<int> ClearAsync(CancellationToken ct = default) =>
        _db.Database.ExecuteSqlRawAsync("""DELETE FROM "SubscriptionGateShadowHits";""", ct);

    private sealed class ShadowRow
    {
        public Guid CompanyId { get; set; }
        public string Reason { get; set; } = "";
        public string Feature { get; set; } = "";
        public string? Plan { get; set; }
        public string? RouteKey { get; set; }
        public string? LastMethod { get; set; }
        public bool WouldBlock { get; set; }
        public long HitCount { get; set; }
        public DateTime FirstSeenAt { get; set; }
        public DateTime LastSeenAt { get; set; }
    }
}
