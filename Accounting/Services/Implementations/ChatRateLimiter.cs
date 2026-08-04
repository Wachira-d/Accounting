using Accounting.Data;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Accounting.Services.Implementations;

/// <summary>
/// Rate limiter ของ chatbot บน PostgreSQL — นับข้าม instance ได้จริง.
///
/// วิธี: 1 แถวต่อ (คีย์, ต้นหน้าต่างเวลา) แล้วใช้
///   INSERT … ON CONFLICT DO UPDATE SET "Count" = "Count" + 1 RETURNING "Count"
/// ซึ่งเป็น atomic ในคำสั่งเดียว — request ที่เข้าพร้อมกันนับไม่หล่นและไม่ต้อง
/// ล็อกแถวเอง. หน้าต่างเป็นแบบ fixed window (ตัดตามนาที/วัน) ไม่ใช่ sliding
/// เพื่อให้คีย์คงที่ + upsert ได้; ข้อเสียคือช่วงรอยต่อนาทีอาจยิงได้ถึง 2 เท่า
/// ของโควตานาทีเดียว ซึ่งยอมรับได้เพราะยังมีเพดานรายวัน + เพดานต่อห้อง + งบ AI
/// คุมอยู่อีกชั้น
/// </summary>
public class ChatRateLimiter : IChatRateLimiter
{
    private readonly AccountingDbContext _db;
    private readonly ILogger<ChatRateLimiter> _logger;

    public ChatRateLimiter(AccountingDbContext db, ILogger<ChatRateLimiter> logger)
    {
        _db = db; _logger = logger;
    }

    public async Task<bool> TryConsumeAsync(string key, int perMinute, int perDay, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var minuteWindow = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);
        var dayWindow = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            // นับนาทีก่อน — ชนเพดานนาทีแล้วไม่ต้องเปลืองการนับรายวัน
            var perMinuteCount = await BumpAsync("m:" + key, minuteWindow, ct);
            if (perMinuteCount > perMinute) return false;
            var perDayCount = await BumpAsync("d:" + key, dayWindow, ct);
            return perDayCount <= perDay;
        }
        catch (Exception ex)
        {
            // fail-open โดยตั้งใจ: ตารางนับพัง ≠ เหตุผลที่จะปิดบริการทั้งระบบ
            // (ด่านอื่นยังทำงาน — เพดานข้อความต่อห้อง/วัน อ่านจาก ChatMessages
            // และ AiBudgetGuard คุมค่าใช้จ่ายจริง)
            _logger.LogWarning(ex, "ChatRateLimiter ใช้ไม่ได้ — ปล่อยผ่านชั่วคราว (key {Key})", key);
            return true;
        }
    }

    public async Task<int> RecordStrikeAsync(string key, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var hourWindow = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        try { return await BumpAsync("s:" + key, hourWindow, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RecordStrike failed for {Key}", key);
            return 0;
        }
    }

    public async Task<int> PurgeOldAsync(CancellationToken ct = default)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-2);
            return await _db.Database.ExecuteSqlRawAsync(
                """DELETE FROM "ChatRateBuckets" WHERE "WindowStart" < {0};""", new object[] { cutoff }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PurgeOldAsync (rate buckets) failed");
            return 0;
        }
    }

    private async Task<int> BumpAsync(string bucketKey, DateTime windowStart, CancellationToken ct)
    {
        const string sql = """
            INSERT INTO "ChatRateBuckets" ("Id", "BucketKey", "WindowStart", "Count")
            VALUES (gen_random_uuid(), @k, @w, 1)
            ON CONFLICT ("BucketKey", "WindowStart")
            DO UPDATE SET "Count" = "ChatRateBuckets"."Count" + 1
            RETURNING "Count";
            """;
        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var pk = new NpgsqlParameter("k", bucketKey);
        var pw = new NpgsqlParameter("w", windowStart);
        cmd.Parameters.Add(pk);
        cmd.Parameters.Add(pw);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int i ? i : Convert.ToInt32(result ?? 0);
    }
}
