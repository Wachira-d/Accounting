using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Middleware;

/// <summary>
/// Idempotency-Key middleware — เมื่อระบบภายนอก POST พร้อมหัว
/// "Idempotency-Key: &lt;uuid&gt;" ระบบจะจำผลลัพธ์ไว้ 24 ชม. การยิงซ้ำด้วยคีย์
/// เดิมจะได้ผลลัพธ์เดิมโดยไม่ทำงานซ้ำ — partner retry ตอนเน็ตหลุดได้ปลอดภัย
///
/// ใช้กับ POST/PUT/PATCH บน /api/* เท่านั้น (GET/HEAD ไม่ต้องมีที่เก็บ)
///
/// **ที่เก็บ = PostgreSQL ไม่ใช่ IMemoryCache** — เดิมเก็บ in-process ⇒ พอ
/// deploy มากกว่า 1 instance คีย์ไม่ถูกแชร์ partner retry ไปโดนอีก node แล้ว
/// **ลงเอกสาร/รับเงินซ้ำเงียบ ๆ** ทั้งที่ส่งหัวมาถูกต้อง (ความเสี่ยงทางการเงิน
/// จริง ไม่ใช่แค่ perf). pattern เดียวกับ ChatRateLimiter ที่นับข้าม instance
/// ด้วย atomic upsert ของ Postgres
///
/// กัน 2 กรณี:
///  1. **ยิงซ้ำหลังจบแล้ว** → replay ผลลัพธ์เดิม (Status=Done)
///  2. **ยิงพร้อมกัน (race)** → ตัวแรกจอง InFlight ตัวที่สองได้ 409 ทันที
///     ไม่ใช่ปล่อยให้ทำงานคู่ขนานแล้วเกิดเอกสาร 2 ใบ
/// </summary>
public class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    /// <summary>อายุของ record — เท่ากับสัญญาที่ประกาศกับ partner (24 ชม.)
    /// แถวเก่ากว่านี้ถือว่าหมดอายุและถูกล้างโดย BackgroundJobService</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
    { _next = next; _logger = logger; }

    public async Task InvokeAsync(HttpContext ctx, AccountingDbContext db)
    {
        var method = ctx.Request.Method;
        var path = ctx.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || (method != "POST" && method != "PUT" && method != "PATCH"))
        {
            await _next(ctx);
            return;
        }

        if (!ctx.Request.Headers.TryGetValue("Idempotency-Key", out var headerVal)
            || string.IsNullOrWhiteSpace(headerVal))
        {
            await _next(ctx);
            return;
        }

        var key = headerVal.ToString().Trim();
        if (key.Length < 8 || key.Length > 128)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new
            {
                success = false,
                message = "Idempotency-Key must be 8-128 chars (UUID recommended).",
            });
            return;
        }

        // แยก namespace ตามผู้เรียก เพื่อไม่ให้ replay ผลของกันและกัน
        // ผู้เรียกที่ไม่ได้ยืนยันตัวตนแยกด้วย IP (เดิมยุบเป็น "anon" ก้อนเดียว
        // ⇒ สองคนที่ไม่ล็อกอินยิงคีย์ชนกันแล้วเห็นผลของอีกคนได้)
        var subject = ctx.User?.FindFirst("CompanyId")?.Value
            ?? ctx.User?.Identity?.Name
            ?? $"anon:{ctx.Connection.RemoteIpAddress}";
        var cacheKey = $"idem:{subject}:{method}:{path}:{key}";
        if (cacheKey.Length > 400) cacheKey = cacheKey[..400];

        var cutoff = DateTime.UtcNow - Retention;

        // ── 1) จองคีย์แบบ atomic: INSERT … ON CONFLICT DO NOTHING
        // ได้ 1 แถว = เราเป็นคนแรก → ทำงานจริง
        // ได้ 0 แถว = มีคนถือคีย์นี้อยู่แล้ว → replay (ถ้าเสร็จ) หรือ 409 (ถ้ากำลังทำ)
        int inserted;
        try
        {
            inserted = await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "IdempotencyRecords" ("Id","CacheKey","Status","CreatedAt")
                VALUES (gen_random_uuid(), {0}, 'InFlight', now())
                ON CONFLICT ("CacheKey") DO NOTHING
                """, cacheKey);
        }
        catch (Exception ex)
        {
            // fail-open โดยตั้งใจ: ตารางกันซ้ำใช้ไม่ได้ ≠ เหตุผลที่จะปฏิเสธคำขอ
            // ของลูกค้าทั้งหมด (พฤติกรรมเดียวกับ ChatRateLimiter)
            _logger.LogWarning(ex, "Idempotency store ใช้ไม่ได้ — ปล่อยผ่าน key={Key}", key);
            await _next(ctx);
            return;
        }

        if (inserted == 0)
        {
            var existing = await db.Database
                .SqlQueryRaw<IdemRow>(
                    """
                    SELECT "Status" AS "Status", "StatusCode" AS "StatusCode",
                           "ContentType" AS "ContentType", "Body" AS "Body",
                           "CreatedAt" AS "CreatedAt"
                    FROM "IdempotencyRecords" WHERE "CacheKey" = {0}
                    """, cacheKey)
                .FirstOrDefaultAsync();

            // แถวเก่าเกิน retention → ถือว่าหมดอายุ ยึดคีย์มาทำใหม่
            if (existing != null && existing.CreatedAt < cutoff)
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE "IdempotencyRecords"
                       SET "Status"='InFlight', "StatusCode"=0, "ContentType"=NULL,
                           "Body"=NULL, "CreatedAt"=now(), "CompletedAt"=NULL
                     WHERE "CacheKey" = {0}
                    """, cacheKey);
            }
            else if (existing is { Status: "Done", Body: not null })
            {
                _logger.LogInformation("Idempotency replay key={Key}", key);
                ctx.Response.StatusCode = existing.StatusCode;
                ctx.Response.ContentType = existing.ContentType ?? "application/json";
                ctx.Response.Headers["Idempotency-Replayed"] = "true";
                ctx.Response.Headers["Idempotency-Original-At"] = existing.CreatedAt.ToString("o");
                await ctx.Response.Body.WriteAsync(existing.Body);
                return;
            }
            else
            {
                // ยังทำอยู่ (หรือรอบก่อนล้มกลางคัน) — ตอบ 409 ให้ partner ลองใหม่
                // ทีหลัง ดีกว่าปล่อยให้ทำงานคู่ขนานจนเกิดเอกสาร/การรับเงินซ้ำ
                ctx.Response.StatusCode = 409;
                ctx.Response.Headers["Retry-After"] = "5";
                await ctx.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "คำขอที่ใช้ Idempotency-Key นี้กำลังประมวลผลอยู่ — โปรดลองใหม่อีกครั้งในไม่กี่วินาที",
                });
                return;
            }
        }

        // ── 2) ทำงานจริง แล้วเก็บผลลัพธ์ (tee-stream)
        var original = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        var completed = false;
        try
        {
            await _next(ctx);
            buffer.Position = 0;
            await buffer.CopyToAsync(original);

            // เก็บเฉพาะ 2xx — 4xx/5xx เป็น error ที่ partner ควรแก้แล้วยิงใหม่
            // ไม่ใช่ replay ของเดิม
            if (ctx.Response.StatusCode is >= 200 and < 300)
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE "IdempotencyRecords"
                       SET "Status"='Done', "StatusCode"={1}, "ContentType"={2},
                           "Body"={3}, "CompletedAt"=now()
                     WHERE "CacheKey" = {0}
                    """, cacheKey, ctx.Response.StatusCode,
                    (object?)ctx.Response.ContentType ?? DBNull.Value, buffer.ToArray());
                completed = true;
            }
        }
        finally
        {
            ctx.Response.Body = original;
            // ไม่สำเร็จ (error/exception) → ปล่อยคีย์ทิ้ง เพื่อให้ partner ยิงใหม่ได้
            // ไม่ค้างเป็น InFlight จนติด 409 ไป 24 ชม.
            if (!completed)
            {
                try
                {
                    await db.Database.ExecuteSqlRawAsync(
                        """DELETE FROM "IdempotencyRecords" WHERE "CacheKey" = {0} AND "Status" = 'InFlight'""",
                        cacheKey);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ปล่อยคีย์ idempotency ไม่สำเร็จ key={Key}", key);
                }
            }
        }
    }

    /// <summary>projection ของแถวที่อ่านกลับมา (ใช้กับ SqlQueryRaw)</summary>
    private sealed class IdemRow
    {
        public string Status { get; set; } = "";
        public int StatusCode { get; set; }
        public string? ContentType { get; set; }
        public byte[]? Body { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
