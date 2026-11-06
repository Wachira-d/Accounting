using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 254 WP-11 L4 (N8) — เขียนตัวนับต้นทุน OA กลาง (<c>NextAccOaUsageCounters</c>) แบบ atomic (INSERT … ON CONFLICT DO UPDATE · หลาย instance ไม่หาย) ·
/// ผู้เรียก: <c>CustomerLineLinkService.ReplyAsync</c> (reply ของ OA กลาง) · <c>NextAccOaQuotaWatch</c> (ยอด push จาก LINE + ประทับคำเตือน)
/// </summary>
public static class NextAccOaUsage
{
    /// <summary>+1 ของชนิดนี้ในงวดปัจจุบัน — ล้ม ⇒ log Error แล้วไปต่อ (ตัวนับต้นทุนต้องไม่ทำให้การตอบลูกค้าล้ม · ไม่ใช่เส้นเงิน/สต็อก/บัญชี)</summary>
    public static async Task IncrementAsync(AccountingDbContext db, string kind, ILogger logger, CancellationToken ct = default)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "NextAccOaUsageCounters" ("Period","Kind","Count","UpdatedAt") VALUES ({0}, {1}, 1, now())
                ON CONFLICT ("Period","Kind") DO UPDATE SET "Count" = "NextAccOaUsageCounters"."Count" + 1, "UpdatedAt" = now();
                """, new object[] { NextAccOaQuotaRule.PeriodOf(DateTime.UtcNow), kind }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "นับ {Kind} ของ OA กลางไม่สำเร็จ — ตัวเลขเฝ้าต้นทุนเดือนนี้ต่ำกว่าจริง", kind);
        }
    }

    /// <summary>บันทึกยอดล่าสุดที่ LINE รายงาน (ทับค่า — ไม่ใช่การบวก) + เพดาน</summary>
    public static Task SetAsync(AccountingDbContext db, string period, string kind, long count, long? limit, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "NextAccOaUsageCounters" ("Period","Kind","Count","Limit","UpdatedAt") VALUES ({0}, {1}, {2}, {3}, now())
            ON CONFLICT ("Period","Kind") DO UPDATE SET "Count" = {2}, "Limit" = {3}, "UpdatedAt" = now();
            """, new object[] { period, kind, count, limit.HasValue ? limit.Value : DBNull.Value }, ct);

    /// <summary>ประทับ "เตือนแล้ว" ครั้งเดียวต่อชนิดต่องวด — คืน true เฉพาะคำขอที่ประทับได้ (ผู้ส่งคำเตือน)</summary>
    public static async Task<bool> TryMarkWarnedAsync(AccountingDbContext db, string period, string kind, CancellationToken ct = default)
        => await db.Database.ExecuteSqlRawAsync("""
            UPDATE "NextAccOaUsageCounters" SET "WarnedAt" = now() WHERE "Period" = {0} AND "Kind" = {1} AND "WarnedAt" IS NULL;
            """, new object[] { period, kind }, ct) == 1;
}
