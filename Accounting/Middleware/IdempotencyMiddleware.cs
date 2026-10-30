using System.Security.Cryptography;
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

    /// <summary>กุญแจลูกของลายนิ้วมือคำขอ (HMAC) — ฝ่ายค้าน S6: ห้ามเก็บแฮชเปล่าของเนื้อคำขอ · ว่าง ⇒ โยนตอนสร้าง (§4C fail-fast)</summary>
    private readonly byte[] _hashKey;

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger, IConfiguration config)
    {
        _next = next;
        _logger = logger;
        _hashKey = IdempotencyKeyPolicy.DeriveHashKey(config["Jwt:Secret"]);
    }

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
        // ฝ่ายค้าน S6: เส้นล็อกอิน/สมัคร/รหัสผ่าน/โทเคน ไม่เข้าระบบคีย์ (ไม่เก็บลายนิ้วมือของรหัสผ่าน · ไม่เก็บโทเคนไว้ replay)
        if (key == null || IdempotencyKeyPolicy.IsCredentialPath(path))
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
            // อายุคำนวณใน SQL (now() - CreatedAt) — ข้อสังเกต 3: ไม่พึ่ง DateTimeKind ของค่าที่อ่านกลับภายใต้ EnableLegacyTimestampBehavior
            var existing = await db.Database
                .SqlQueryRaw<IdemRow>(
                    """
                    SELECT "Status" AS "Status", "StatusCode" AS "StatusCode",
                           "ContentType" AS "ContentType", "Body" AS "Body",
                           "RequestHash" AS "RequestHash", "CreatedAt" AS "CreatedAt",
                           EXTRACT(EPOCH FROM (now() - "CreatedAt"))::float8 AS "AgeSeconds"
                    FROM "IdempotencyRecords" WHERE "CacheKey" = {0}
                    """, cacheKey)
                .FirstOrDefaultAsync();

            var decision = existing == null
                ? IdempotencyKeyPolicy.Decision.InFlight   // แถวถูกปล่อยระหว่างอ่าน (รอบก่อนล้ม) — ให้ลองใหม่
                : IdempotencyKeyPolicy.Decide(existing.Status, existing.RequestHash, existing.Body != null, existing.AgeSeconds, requestHash);

            // ฝ่ายค้าน S5: ยึดคืนแบบมีเงื่อนไขในคำสั่งเดียว (หมดอายุ หรือ InFlight ค้าง) — สองคำขอแย่งกัน ได้ 1 แถวคนเดียว ที่เหลือ = InFlight
            if (decision == IdempotencyKeyPolicy.Decision.Reclaim)
            {
                var reclaimed = await db.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE "IdempotencyRecords"
                       SET "Status"='InFlight', "StatusCode"=0, "ContentType"=NULL,
                           "Body"=NULL, "RequestHash"={1}, "CreatedAt"=now(), "CompletedAt"=NULL
                     WHERE "CacheKey" = {0}
                       AND ("CreatedAt" < now() - make_interval(secs => {2})
                            OR (("Status" <> 'Done' OR "Body" IS NULL) AND "CreatedAt" < now() - make_interval(secs => {3})))
                    """, cacheKey, requestHash, IdempotencyKeyPolicy.Retention.TotalSeconds, IdempotencyKeyPolicy.StaleInFlightAfter.TotalSeconds);
                if (reclaimed != 1) decision = IdempotencyKeyPolicy.Decision.InFlight;
            }

            switch (decision)
            {
                case IdempotencyKeyPolicy.Decision.Reclaim:
                    break;   // ได้คีย์คืนแล้ว — ทำงานจริงด้านล่าง
                case IdempotencyKeyPolicy.Decision.Replay:
                    _logger.LogInformation("Idempotency replay key={Key}", key);
                    ctx.Response.StatusCode = existing!.StatusCode;
                    ctx.Response.ContentType = existing.ContentType ?? "application/json";
                    ctx.Response.Headers["Idempotency-Replayed"] = "true";
                    ctx.Response.Headers[IdempotencyKeyPolicy.StatusHeader] = IdempotencyKeyPolicy.StatusReplayed;
                    ctx.Response.Headers["Idempotency-Original-At"] = existing.CreatedAt.ToString("o");
                    await ctx.Response.Body.WriteAsync(existing.Body!);
                    return;
                case IdempotencyKeyPolicy.Decision.Mismatch:
                    ctx.Response.StatusCode = 409;
                    ctx.Response.Headers[IdempotencyKeyPolicy.StatusHeader] = IdempotencyKeyPolicy.StatusMismatch;
                    await ctx.Response.WriteAsJsonAsync(new { success = false, message = IdempotencyKeyPolicy.MismatchMessage });
                    return;
                default:
                    // ยังทำอยู่ — ตอบ 409 ให้ลองใหม่ทีหลัง ดีกว่าปล่อยให้ทำงานคู่ขนานจนเกิดเอกสาร/การจองซ้ำ ·
                    // หน้าเว็บ (submit-once.js) รอ Retry-After แล้วยิงคีย์เดิม ⇒ ได้ผลเดิม
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

            // เก็บเฉพาะผลสำเร็จ (2xx ที่ไม่ใช่ success:false — ข้อสังเกต 1) · นอกนั้นปล่อยคีย์ให้แก้แล้วยิงใหม่ ไม่ replay ความล้มเหลว
            var bodyBytes = buffer.ToArray();
            if (IdempotencyKeyPolicy.ShouldStore(ctx.Response.StatusCode, ctx.Response.ContentType, bodyBytes))
            {
                await db.Database.ExecuteSqlRawAsync(
                    """
                    UPDATE "IdempotencyRecords"
                       SET "Status"='Done', "StatusCode"={1}, "ContentType"={2},
                           "Body"={3}, "CompletedAt"=now()
                     WHERE "CacheKey" = {0}
                    """, cacheKey, ctx.Response.StatusCode,
                    (object?)ctx.Response.ContentType ?? DBNull.Value, bodyBytes);
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

    /// <summary>ลายนิ้วมือของคำขอ (HMAC) — multipart: ช่อง + เนื้อไฟล์ ไม่รวม boundary (ฝ่ายค้าน S2) · อื่น ๆ: เนื้อดิบแบบสตรีม ·
    /// กรอกสตรีมกลับให้ controller อ่านต่อได้ (multipart อ่านผ่าน ReadFormAsync ⇒ แคชไว้ที่ Form feature ให้ model binding ใช้ต่อ)</summary>
    private async Task<string> HashRequestAsync(HttpRequest request, string method, string pathAndQuery, CancellationToken ct)
    {
        request.EnableBuffering();
        if (IdempotencyKeyPolicy.IsMultipart(request.ContentType))
        {
            var form = await request.ReadFormAsync(ct);
            var fields = form.SelectMany(kv => kv.Value.Select(v => new IdempotencyKeyPolicy.MultipartField(kv.Key, v ?? ""))).ToList();
            var files = new List<IdempotencyKeyPolicy.MultipartFile>();
            foreach (var f in form.Files)
            {
                await using var fs = f.OpenReadStream();
                files.Add(new IdempotencyKeyPolicy.MultipartFile(f.Name, f.FileName, f.ContentType, await SHA256.HashDataAsync(fs, ct)));
            }
            request.Body.Position = 0;
            return IdempotencyKeyPolicy.MultipartHash(_hashKey, method, pathAndQuery, fields, files);
        }
        using var h = IdempotencyKeyPolicy.BeginRequestHash(_hashKey, method, pathAndQuery);
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
        public double AgeSeconds { get; set; }
    }
}
