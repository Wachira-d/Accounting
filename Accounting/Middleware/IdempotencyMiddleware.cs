using Accounting.Data;
using Accounting.Helpers;
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

    /// <summary>อายุของ record — ตัวตั้งอยู่ที่ <see cref="IdempotencyKeyPolicy.Retention"/> (คงชื่อนี้ไว้ให้ ChatRetentionPurgeJob)</summary>
    public static TimeSpan Retention => IdempotencyKeyPolicy.Retention;

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

        // รอบ 217: กติกาทั้งหมด (รูปคีย์ · namespace · แฮชคำขอ · ตัดสิน) อยู่ใน IdempotencyKeyPolicy ตัวเดียว
        var (key, keyProblem) = IdempotencyKeyPolicy.Normalize(
            ctx.Request.Headers.TryGetValue(IdempotencyKeyPolicy.Header, out var headerVal) ? headerVal.ToString() : null);
        if (keyProblem != null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { success = false, message = keyProblem });
            return;
        }
        if (key == null)
        {
            await _next(ctx);
            return;
        }

        // แยก namespace ตามผู้เรียก เพื่อไม่ให้ replay ผลของกันและกัน — มิดเดิลแวร์นี้อยู่ก่อน UseAuthentication
        // ⇒ claim ของ JWT ยังไม่มี: ใช้แฮชของ credential header (Authorization/X-Storefront-Token) แทน · นิรนาม = IP
        var credential = ctx.Request.Headers.Authorization.FirstOrDefault()
            ?? ctx.Request.Headers[StorefrontCustomerAuth.HeaderName].FirstOrDefault();
        var subject = IdempotencyKeyPolicy.Subject(ctx.User?.FindFirst("CompanyId")?.Value, ctx.User?.Identity?.Name,
            credential, ctx.Connection.RemoteIpAddress?.ToString());
        var cacheKey = IdempotencyKeyPolicy.CacheKey(subject, method, path, key);

        // รอบ 217: แฮชของคำขอ (method + path + query + เนื้อ) — คีย์เดิมแต่เนื้อต่าง ⇒ 409 ไม่ replay ผลของคำขออื่น
        var requestHash = await HashRequestAsync(ctx.Request, method, path + ctx.Request.QueryString.Value, ctx.RequestAborted);

        // ── 1) จองคีย์แบบ atomic: INSERT … ON CONFLICT DO NOTHING
        // ได้ 1 แถว = เราเป็นคนแรก → ทำงานจริง
        // ได้ 0 แถว = มีคนถือคีย์นี้อยู่แล้ว → replay (ถ้าเสร็จ) หรือ 409 (ถ้ากำลังทำ/เนื้อต่าง)
        int inserted;
        try
        {
            inserted = await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "IdempotencyRecords" ("Id","CacheKey","Status","RequestHash","CreatedAt")
                VALUES (gen_random_uuid(), {0}, 'InFlight', {1}, now())
                ON CONFLICT ("CacheKey") DO NOTHING
                """, cacheKey, requestHash);
        }
        catch (Exception ex)
        {
            // fail-open โดยตั้งใจ: ตารางกันซ้ำใช้ไม่ได้ ≠ เหตุผลที่จะปฏิเสธคำขอ
            // ของลูกค้าทั้งหมด (พฤติกรรมเดียวกับ ChatRateLimiter) · ชั้นที่สอง = ด่านซ้ำเชิงความหมายใน service (DuplicateSubmitGuard)
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
                           "RequestHash" AS "RequestHash", "CreatedAt" AS "CreatedAt"
                    FROM "IdempotencyRecords" WHERE "CacheKey" = {0}
                    """, cacheKey)
                .FirstOrDefaultAsync();

            var decision = existing == null
                ? IdempotencyKeyPolicy.Decision.InFlight   // แถวถูกปล่อยระหว่างอ่าน (รอบก่อนล้ม) — ให้ลองใหม่
                : IdempotencyKeyPolicy.Decide(existing.Status, existing.RequestHash, existing.Body != null,
                    DateTime.SpecifyKind(existing.CreatedAt, DateTimeKind.Utc), requestHash, DateTime.UtcNow);

            switch (decision)
            {
                case IdempotencyKeyPolicy.Decision.Reclaim:
                    // แถวเก่าเกิน retention → ถือว่าหมดอายุ ยึดคีย์มาทำใหม่
                    await db.Database.ExecuteSqlRawAsync(
                        """
                        UPDATE "IdempotencyRecords"
                           SET "Status"='InFlight', "StatusCode"=0, "ContentType"=NULL,
                               "Body"=NULL, "RequestHash"={1}, "CreatedAt"=now(), "CompletedAt"=NULL
                         WHERE "CacheKey" = {0}
                        """, cacheKey, requestHash);
                    break;
                case IdempotencyKeyPolicy.Decision.Replay:
                    _logger.LogInformation("Idempotency replay key={Key}", key);
                    ctx.Response.StatusCode = existing!.StatusCode;
                    ctx.Response.ContentType = existing.ContentType ?? "application/json";
                    ctx.Response.Headers["Idempotency-Replayed"] = "true";
                    ctx.Response.Headers[IdempotencyKeyPolicy.StatusHeader] = IdempotencyKeyPolicy.StatusReplayed;
                    ctx.Response.Headers["Idempotency-Original-At"] = existing.CreatedAt.ToString("o");
                    await ctx.Response.Body.WriteAsync(existing.Body);
                    return;
                case IdempotencyKeyPolicy.Decision.Mismatch:
                    ctx.Response.StatusCode = 409;
                    ctx.Response.Headers[IdempotencyKeyPolicy.StatusHeader] = IdempotencyKeyPolicy.StatusMismatch;
                    await ctx.Response.WriteAsJsonAsync(new { success = false, message = IdempotencyKeyPolicy.MismatchMessage });
                    return;
                default:
                    // ยังทำอยู่ (หรือรอบก่อนล้มกลางคัน) — ตอบ 409 ให้ลองใหม่ทีหลัง ดีกว่าปล่อยให้ทำงานคู่ขนาน
                    // จนเกิดเอกสาร/การจองซ้ำ · หน้าเว็บ (submit-once.js) รอ Retry-After แล้วยิงคีย์เดิม ⇒ ได้ผลเดิม
                    ctx.Response.StatusCode = 409;
                    ctx.Response.Headers["Retry-After"] = IdempotencyKeyPolicy.InFlightRetryAfterSeconds.ToString();
                    ctx.Response.Headers[IdempotencyKeyPolicy.StatusHeader] = IdempotencyKeyPolicy.StatusInFlight;
                    await ctx.Response.WriteAsJsonAsync(new { success = false, message = IdempotencyKeyPolicy.InFlightMessage });
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

            // เก็บเฉพาะ 2xx — 4xx/5xx เป็น error ที่ผู้เรียกควรแก้แล้วยิงใหม่ ไม่ใช่ replay ของเดิม
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
            // ไม่สำเร็จ (error/exception) → ปล่อยคีย์ทิ้ง เพื่อให้ยิงใหม่ได้ (แก้ข้อมูลแล้วส่งคีย์เดิมได้)
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

    /// <summary>แฮชเนื้อคำขอแบบสตรีม (ไฟล์แนบใหญ่ไม่ต้องอยู่ในหน่วยความจำก้อนเดียว) แล้วกรอกลับให้ controller อ่านต่อได้</summary>
    private static async Task<string> HashRequestAsync(HttpRequest request, string method, string pathAndQuery, CancellationToken ct)
    {
        using var h = IdempotencyKeyPolicy.BeginRequestHash(method, pathAndQuery);
        request.EnableBuffering();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await request.Body.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0) h.AppendData(buf, 0, n);
        request.Body.Position = 0;
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>projection ของแถวที่อ่านกลับมา (ใช้กับ SqlQueryRaw)</summary>
    private sealed class IdemRow
    {
        public string Status { get; set; } = "";
        public int StatusCode { get; set; }
        public string? ContentType { get; set; }
        public byte[]? Body { get; set; }
        public string? RequestHash { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
