using System.Security.Claims;
using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Middleware;

/// <summary>
/// Middleware: API Key Authentication
/// รองรับ header: X-Api-Key
/// ตรวจสอบ: status, expiry, IP, rate limit
/// </summary>
public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiKeyMiddleware> _logger;

    public ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    // ── แคชผลตรวจ BCrypt (ผลตรวจ F-11) ──
    // BCrypt ช้าโดยตั้งใจ (~100ms) เพราะออกแบบมาสำหรับรหัสผ่านที่คนพิมพ์
    // ไม่ใช่ header ที่คู่ค้ายิงมาทุก request — 100 req/s = 10 วินาที CPU/วินาที
    // ⇒ thread pool ตัน ทั้งเซิร์ฟเวอร์ช้า ไม่ใช่แค่เส้น API key
    //
    // คีย์แคชเป็น SHA-256 ของ (rawKey + hash) — ไม่เก็บ rawKey ไว้ในหน่วยความจำ
    // และค่าที่เก็บเป็นแค่ true/false ⇒ อ่านแคชได้ก็ยังเดา rawKey ไม่ได้
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>
        _verifiedKeys = new();
    private static readonly TimeSpan VerifyCacheTtl = TimeSpan.FromMinutes(5);
    private const int VerifyCacheMax = 2_000;

    /// <summary>ตรวจคีย์โดยใช้ผลที่แคชไว้ถ้ายังไม่หมดอายุ
    ///
    /// <para>⚠️ แคชเฉพาะ "ลายเซ็นถูกไหม" เท่านั้น — <b>สถานะคีย์ (Revoked /
    /// Expired) · IP allow-list · โควตา ยังตรวจจากฐานข้อมูลทุก request</b>
    /// การเพิกถอนคีย์จึงมีผลทันที ไม่ต้องรอแคชหมดอายุ</para></summary>
    private static bool VerifyKeyCached(string rawKey, string storedHash)
    {
        var cacheKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(rawKey + "|" + storedHash)));

        if (_verifiedKeys.TryGetValue(cacheKey, out var at) && DateTime.UtcNow - at < VerifyCacheTtl)
            return true;

        if (!BCrypt.Net.BCrypt.Verify(rawKey, storedHash)) return false;

        // ไม่แคชผลที่ "ไม่ผ่าน" — ไม่งั้นผู้โจมตีเดาคีย์ผิดไปเรื่อย ๆ ก็ทำให้
        // dict โตได้ฟรี (defect class เดียวกับ F-14)
        _verifiedKeys[cacheKey] = DateTime.UtcNow;
        if (_verifiedKeys.Count > VerifyCacheMax)
        {
            var cutoff = DateTime.UtcNow - VerifyCacheTtl;
            foreach (var kv in _verifiedKeys)
                if (kv.Value < cutoff) _verifiedKeys.TryRemove(kv.Key, out _);
        }
        return true;
    }

    public async Task InvokeAsync(HttpContext context, AccountingDbContext db)
    {
        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyHeader))
        {
            await _next(context);
            return;
        }

        var rawKey = apiKeyHeader.ToString();
        if (rawKey.Length < 8)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Invalid API key format" });
            return;
        }

        var keyPrefix = rawKey[..8];

        // Find by prefix (efficient lookup)
        var apiKey = await db.Set<Models.Entities.ApiKey>()
            .Include(k => k.Company)
            .FirstOrDefaultAsync(k => k.KeyPrefix == keyPrefix && k.Status == ApiKeyStatus.Active);

        if (apiKey == null)
        {
            // No acc_ key matched. Fall back to integration (int_) keys, which
            // live in the ExternalIntegrations table and were historically only
            // usable on the /api/integration/* routes via X-Integration-Key.
            // Accepting them here too lets a single key authenticate across the
            // whole [Authorize] API surface (e.g. /accounting/accounts) through
            // the standard X-Api-Key header.
            if (await TryAuthenticateIntegrationAsync(context, db, rawKey, keyPrefix))
                await _next(context);
            return;
        }

        // Verify the full key hash — แคชผลไว้ 5 นาที (ผลตรวจ F-11)
        //
        // BCrypt ถูกออกแบบให้ **ช้าโดยตั้งใจ** (~100ms/ครั้ง) เพราะใช้กับรหัสผ่าน
        // ที่คนพิมพ์ ไม่ใช่กับ header ที่คู่ค้ายิงมาทุก request ⇒ partner ที่ยิงถี่
        // ทำให้ CPU เต็มและ thread pool ตัน = ทั้งเซิร์ฟเวอร์ช้าไปด้วย ไม่ใช่แค่
        // เส้น API key
        //
        // ปลอดภัย: แคชคีย์ด้วย SHA-256 ของ (rawKey + KeyHash) — ทั้งสองส่วนเป็น
        // ความลับอยู่แล้ว และการรู้ค่า hash ในแคชไม่ช่วยให้เดา rawKey ได้ ·
        // อายุสั้น 5 นาที ⇒ คีย์ที่ถูกเพิกถอนหยุดใช้งานได้ภายในเวลานั้น (สถานะ
        // Revoked/Expired ยังถูกตรวจจาก DB **ทุก request** ไม่ได้อยู่ในแคช)
        if (!VerifyKeyCached(rawKey, apiKey.KeyHash))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Invalid API key" });
            return;
        }

        // Check expiry
        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value < DateTime.UtcNow)
        {
            apiKey.Status = ApiKeyStatus.Expired;
            await db.SaveChangesAsync();
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "API key expired" });
            return;
        }

        // Check IP
        if (!string.IsNullOrEmpty(apiKey.AllowedIpAddresses))
        {
            var clientIp = context.Connection.RemoteIpAddress?.ToString();
            var allowedIps = apiKey.AllowedIpAddresses.Split(',').Select(ip => ip.Trim()).ToHashSet();
            if (clientIp != null && !allowedIps.Contains(clientIp))
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { success = false, message = "IP not allowed for this API key" });
                return;
            }
        }

        // Per-API-key rate limiting — ApiKey.RateLimitPerMinute (when
        // set) is enforced HERE before the global RateLimitMiddleware
        // because key-specific limits are usually MORE restrictive
        // than the platform-wide cap.
        if (!EnforceRateLimit(context, apiKey.Id, apiKey.RateLimitPerMinute))
            return;  // 429 already written

        // Update last used — เขียนอย่างมาก 1 ครั้ง/นาที/คีย์ (ผลตรวจ F-11)
        // เดิม SaveChangesAsync ทุก request ⇒ partner ที่ยิง 100 req/s สร้าง
        // write load 100/s บนตารางเดียวโดยไม่มีใครต้องการความละเอียดระดับนั้น
        // (หน้าจอแสดงแค่ "ใช้ล่าสุดเมื่อไร")
        if (apiKey.LastUsedAt == null || DateTime.UtcNow - apiKey.LastUsedAt.Value > TimeSpan.FromMinutes(1))
        {
            apiKey.LastUsedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        // Create ClaimsPrincipal so [Authorize] attribute passes
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, apiKey.CreatedByUserId.ToString()),
            new("CompanyId", apiKey.CompanyId.ToString()),
            new("ApiKeyId", apiKey.Id.ToString()),
            new("AuthMethod", "ApiKey")
        };
        var identity = new ClaimsIdentity(claims, "ApiKey");
        context.User = new ClaimsPrincipal(identity);

        // Set context items
        context.Items["CompanyId"] = apiKey.CompanyId;
        context.Items["ApiKeyId"] = apiKey.Id;
        context.Items["ApiKeyFeatures"] = apiKey.AllowedFeatures;
        context.Items["ApiKeyCanRead"] = apiKey.CanRead;
        context.Items["ApiKeyCanWrite"] = apiKey.CanWrite;
        context.Items["ApiKeyCanDelete"] = apiKey.CanDelete;
        context.Items["IsApiKeyAuth"] = true;

        await _next(context);
    }

    /// <summary>
    /// Fallback path for integration (int_) keys stored in the
    /// ExternalIntegrations table. Mirrors the acc_ key flow: prefix lookup
    /// → BCrypt hash verify → rate limit → claims/context.
    ///
    /// <para>⚠️ รอบ 193 (G2-01 · คำตัดสินเจ้าของข้อ 37): เดิมคีย์ชนิดนี้ได้
    /// <c>ApiKeyCanRead/Write/Delete = true</c> <b>ตายตัว</b> ⇒ <c>ApiKeyScopeFilter</c> เป็น no-op ·
    /// ตอนนี้อ่านสิทธิ์จากคอลัมน์ของคีย์ผ่าน <c>IntegrationKeyPolicy.EffectiveScopes</c> (คีย์รุ่นเก่าได้สิทธิ์เต็ม
    /// จนถึง <c>LegacyDeprecatesAt</c> · คีย์ใหม่ได้ตามที่เจ้าของเลือก ค่าเริ่มต้นอ่านอย่างเดียว)</para>
    /// This helper fully owns the response on failure: it writes the 401 when
    /// no integration matches, or a 429 when the per-key rate limit is hit.
    /// Returns true only when authentication succeeded and the request should
    /// proceed to the next middleware.
    /// </summary>
    private async Task<bool> TryAuthenticateIntegrationAsync(
        HttpContext context, AccountingDbContext db, string rawKey, string keyPrefix)
    {
        // Multiple integrations could share an 8-char prefix collision, so
        // verify the hash against every active candidate (same as
        // IntegrationService.ValidateApiKeyAsync).
        var candidates = await db.Set<Models.Entities.ExternalIntegration>()
            .Where(i => i.ApiKeyPrefix == keyPrefix && i.IsActive && !i.IsDeleted)
            .Select(i => new { i.Id, i.CompanyId, i.ApiKeyHash, i.RateLimitPerMinute,
                i.CanRead, i.CanWrite, i.CanDelete, i.IsLegacyKey, i.LegacyDeprecatesAt })
            .ToListAsync();

        // ⚠️ ที่มา (ผลตรวจทีม A · SYSTEM_AUDIT_2026-09-07.md A-05): เส้นนี้เรียก
        // `BCrypt.Verify` ตรง ๆ ทุก request ขณะที่เส้น `X-Api-Key` ใช้ตัวแคช
        // (`VerifyKeyCached`) มาตั้งแต่แรก — **สองมาตรฐานในไฟล์เดียวกัน** ·
        // BCrypt ถูกออกแบบให้ช้าโดยตั้งใจ (~100ms) เพราะใช้กับรหัสผ่านที่คนพิมพ์
        // ไม่ใช่ header ที่คู่ค้ายิงมาทุก request ⇒ CPU เต็ม thread pool ตัน
        // ทั้งเซิร์ฟเวอร์ช้า · แคชเฉพาะ "ลายเซ็นถูกไหม" ส่วนสถานะ `IsActive`/
        // `IsDeleted` ยังอ่านจาก DB ทุก request อยู่แล้ว (query ข้างบน) ⇒
        // การปิดการเชื่อมต่อยังมีผลทันที
        var match = candidates.FirstOrDefault(c => VerifyKeyCached(rawKey, c.ApiKeyHash));
        if (match == null)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { success = false, message = "Invalid or revoked API key" });
            return false;
        }

        // Per-key rate limiting — keyed by integration Id (a distinct GUID,
        // no collision with ApiKey ids in the shared window dictionary).
        if (!EnforceRateLimit(context, match.Id, match.RateLimitPerMinute))
            return false;  // 429 already written

        var nowUtc = DateTime.UtcNow;
        var legacyActive = Helpers.IntegrationKeyPolicy.IsLegacyPrivilegeActive(
            match.IsLegacyKey, match.LegacyDeprecatesAt, nowUtc);
        var scopes = Helpers.IntegrationKeyPolicy.EffectiveScopes(
            match.IsLegacyKey, match.LegacyDeprecatesAt,
            new Helpers.IntegrationKeyScopes(match.CanRead, match.CanWrite, match.CanDelete), nowUtc);

        // ── Operator attribution ────────────────────────────────────────
        // The partner can name the real operator in the X-Acting-User header.
        // รอบ 193 (G2-01): attribution เคยกลายเป็น authorization — คีย์ส่งอีเมลเจ้าของมา
        // แล้วได้สิทธิ์เจ้าของ ⇒ ตอนนี้สวมได้เฉพาะผู้ใช้ที่เจ้าของผูกไว้ใน IntegrationUserMapping
        // · เส้น email match เหลือเฉพาะคีย์รุ่นเก่าในช่วงผ่อนผัน และ log + audit ทุกครั้งที่ใช้
        // When unresolved, NameIdentifier stays the IntegrationId (not a member
        // ⇒ permission-gated endpoints deny; creator signature falls back to Owner).
        var actingKey = FirstHeader(context, "X-Acting-User", "X-Operator-Email", "X-Operator");
        var acting = await ResolveActingUserAsync(db, match.CompanyId, match.Id, actingKey, legacyActive);
        var actingUserId = acting.UserId;
        if (acting.Path != Helpers.ActingUserPath.None)
            context.Response.Headers["X-Acting-User-Resolved"] =
                Helpers.IntegrationKeyPolicy.ResolvedHeaderValue(acting.Path);
        if (acting.Path == Helpers.ActingUserPath.LegacyEmailMatch && actingUserId.HasValue)
            await RecordLegacyEmailMatchAsync(context, db, match.CompanyId, match.Id,
                actingUserId.Value, match.LegacyDeprecatesAt);

        var nameId = actingUserId?.ToString() ?? match.Id.ToString();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, nameId),
            new("CompanyId", match.CompanyId.ToString()),
            new("IntegrationId", match.Id.ToString()),
            new("AuthMethod", "IntegrationKey")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));
        if (actingUserId.HasValue) context.Items["ActingUserId"] = actingUserId.Value;

        context.Items["CompanyId"] = match.CompanyId;
        context.Items["IntegrationId"] = match.Id;
        // สิทธิ์จริงของคีย์ (ไม่ใช่ true ตายตัว) — ApiKeyScopeFilter อ่านสามค่านี้ (G2-01)
        context.Items["ApiKeyCanRead"] = scopes.CanRead;
        context.Items["ApiKeyCanWrite"] = scopes.CanWrite;
        context.Items["ApiKeyCanDelete"] = scopes.CanDelete;
        context.Items["IsApiKeyAuth"] = true;

        return true;
    }

    /// <summary>ร่องรอยทุกครั้งที่คีย์รุ่นเก่าใช้เส้น "สวมผู้ใช้ด้วยอีเมล" (คำตัดสินเจ้าของข้อ 37) — warning
    /// ใน log + แถว AuditLog (hash chain คำนวณที่ SaveChanges) · audit ล้มต้อง<b>ไม่</b>ทำ request ล้ม
    /// (ไม่ใช่เส้นเงิน) แต่ต้องถอดแถวที่ค้างออกจาก context ไม่งั้น SaveChanges ของงานจริงจะล้มตาม</summary>
    private async Task RecordLegacyEmailMatchAsync(HttpContext context, AccountingDbContext db,
        Guid companyId, Guid integrationId, Guid actingUserId, DateTime? deprecatesAt)
    {
        _logger.LogWarning(
            "[G2-01] คีย์ integration รุ่นเก่า {IntegrationId} (บริษัท {CompanyId}) สวมผู้ใช้ {UserId} ด้วยการจับคู่อีเมล — "
            + "เส้นนี้จะปิดเมื่อ {DeprecatesAt} · ให้เจ้าของผูกผู้ใช้ในหน้าเชื่อมต่อระบบ",
            integrationId, companyId, actingUserId, deprecatesAt);

        var row = new Models.Entities.AuditLog
        {
            CompanyId = companyId,
            UserId = actingUserId,
            Action = AuditAction.ApiAccess,
            EntityType = "IntegrationActingUser",
            EntityId = integrationId.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                path = Helpers.IntegrationKeyPolicy.ResolvedHeaderValue(Helpers.ActingUserPath.LegacyEmailMatch),
                integrationId,
                legacyDeprecatesAt = deprecatesAt,
                method = context.Request.Method,
                requestPath = context.Request.Path.Value,
            }),
            IpAddress = context.Connection.RemoteIpAddress?.ToString(),
        };
        db.AuditLogs.Add(row);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            db.Entry(row).State = EntityState.Detached;
            _logger.LogError(ex,
                "[G2-01] บันทึก AuditLog ของการสวมผู้ใช้ด้วยอีเมล (integration {IntegrationId}) ไม่สำเร็จ", integrationId);
        }
    }

    /// <summary>First non-empty value among the given header names.</summary>
    private static string? FirstHeader(HttpContext context, params string[] names)
    {
        foreach (var n in names)
        {
            var v = context.Request.Headers[n].ToString();
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return null;
    }

    /// <summary>Resolve the partner-supplied operator key (X-Acting-User) to a
    /// real NextAcc user that belongs to the company. ลำดับและกติกาตัดสินที่
    /// <c>Helpers/IntegrationKeyPolicy.ResolveActingUser</c> ตัวเดียว (G2-01):
    ///   1. แถว IntegrationUserMapping ที่เจ้าของผูกไว้ (ต้องยังเป็นสมาชิก),
    ///   2. email match — <b>เฉพาะคีย์รุ่นเก่าในช่วงผ่อนผัน</b>,
    ///   3. ไม่สวมใคร.
    /// เมธอดนี้ทำแค่ query หลักฐาน · ไม่ตัดสินเอง.</summary>
    private static async Task<Helpers.ActingUserDecision> ResolveActingUserAsync(
        AccountingDbContext db, Guid companyId, Guid integrationId, string? actingKey, bool legacyActive)
    {
        if (string.IsNullOrWhiteSpace(actingKey))
            return Helpers.IntegrationKeyPolicy.ResolveActingUser(false, null, legacyActive, null);
        var key = actingKey.Trim();

        // 1) Explicit mapping configured in NextAcc for this integration.
        Guid? mappedMember = null;
        var mapped = await db.Set<Models.Entities.IntegrationUserMapping>()
            .Where(m => m.IntegrationId == integrationId && m.CompanyId == companyId && !m.IsDeleted
                        && m.ExternalUserKey.ToLower() == key.ToLower())
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync();
        if (mapped.HasValue)
        {
            var isMember = await db.Set<Models.Entities.CompanyUser>()
                .AnyAsync(cu => cu.CompanyId == companyId && cu.UserId == mapped.Value);
            if (isMember) mappedMember = mapped.Value;
        }

        // 2) Direct email match against a company member — legacy keys only.
        Guid? emailMember = null;
        if (Helpers.IntegrationKeyPolicy.ShouldTryLegacyEmailMatch(legacyActive, mappedMember, key))
        {
            emailMember = await (
                from u in db.Set<Models.Entities.User>()
                join cu in db.Set<Models.Entities.CompanyUser>() on u.Id equals cu.UserId
                where cu.CompanyId == companyId && u.Email.ToLower() == key.ToLower()
                select (Guid?)u.Id).FirstOrDefaultAsync();
        }

        return Helpers.IntegrationKeyPolicy.ResolveActingUser(true, mappedMember, legacyActive, emailMember);
    }

    /// <summary>
    /// Sliding-window per-key rate limit. Returns true when the request is
    /// within the limit (and records the hit + sets X-RateLimit headers);
    /// false when the limit is exceeded — in which case a 429 response has
    /// already been written. A non-positive limit means "no per-key limit".
    /// </summary>
    private static bool EnforceRateLimit(HttpContext context, Guid keyId, int limitPerMinute)
    {
        if (limitPerMinute <= 0) return true;

        var now = DateTime.UtcNow;
        var window = _windows.GetOrAdd(keyId, _ => new ApiKeyRateWindow());
        lock (window.Lock)
        {
            while (window.Hits.Count > 0 && now - window.Hits[0] > TimeSpan.FromSeconds(60))
                window.Hits.RemoveAt(0);
            if (window.Hits.Count >= limitPerMinute)
            {
                context.Response.StatusCode = 429;
                context.Response.Headers["Retry-After"] = "60";
                context.Response.Headers["X-RateLimit-Limit"] = limitPerMinute.ToString();
                context.Response.Headers["X-RateLimit-Remaining"] = "0";
                context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    message = $"API key rate limit exceeded ({limitPerMinute}/min). Retry in 60s.",
                }).GetAwaiter().GetResult();
                return false;
            }
            window.Hits.Add(now);
            context.Response.Headers["X-RateLimit-Limit"] = limitPerMinute.ToString();
            context.Response.Headers["X-RateLimit-Remaining"] = (limitPerMinute - window.Hits.Count).ToString();
        }
        return true;
    }

    /// <summary>Sliding-window counter held in-process. Acceptable
    /// for single-node; replace with Redis sorted set when scaling out.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ApiKeyRateWindow> _windows = new();

    private sealed class ApiKeyRateWindow
    {
        public readonly object Lock = new();
        public readonly List<DateTime> Hits = new();
    }
}
