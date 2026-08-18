using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Accounting.Services.Ai;

/// <summary>ป้ายกำกับ "ใครเป็นคนทำให้เกิด AI call นี้" — ติดไปกับทุกแถว
/// <see cref="Models.Entities.AiSuggestionFeedback"/> เพื่อให้รายงานแยก
/// รายลูกค้า/ช่องทาง/คีย์ API ได้</summary>
public sealed record AiUsageAttribution(
    AiUsageChannel Channel,
    Guid? BillingAccountId,
    Guid? BranchId,
    Guid? ApiClientId,
    Guid? UserId,
    bool IsSandbox)
{
    /// <summary>ค่าเริ่มต้นเมื่อ resolve ไม่ได้ — ต้องไม่ทำให้ AI call ล้ม</summary>
    public static readonly AiUsageAttribution Unknown =
        new(AiUsageChannel.Unknown, null, null, null, null, false);
}

public interface IAiUsageAttributionResolver
{
    Task<AiUsageAttribution> ResolveAsync(Guid companyId, CancellationToken ct);
}

/// <summary>
/// อ่านบริบทของ request ปัจจุบันแล้วบอกว่า AI call นี้มาจากไหน
///
/// <para><b>กติกา</b> (นิยามเดียวกับ <c>UsageEvent</c> เป๊ะ ๆ เพื่อให้รายงาน
/// การใช้ AI กับบิลกระทบยอดกันได้):</para>
/// <list type="bullet">
///   <item>ไม่มี HttpContext → <b>Background</b> — งานกลางคืน/cron ของระบบเอง
///     ไม่ใช่การใช้งานของลูกค้า และไม่ควรเอาไปคิดเงิน</item>
///   <item>claim <c>AuthMethod = ApiKey</c> → <b>ApiKey</b> + เก็บ
///     <c>ApiClientId</c> ⇒ นี่คือกลุ่ม "ลูกค้าที่ใช้ API อย่างเดียว"</item>
///   <item>นอกนั้น (JWT ผู้ใช้) → <b>Web</b> + เก็บ <c>UserId</c></item>
/// </list>
///
/// <para><b>ทำไมต้อง snapshot BillingAccountId ลงแถว</b>: บริษัทถูกขายออกจาก
/// เครือแล้ว <c>Company.BillingAccountId</c> เปลี่ยน — ถ้ารายงาน join สด
/// ประวัติการใช้ทั้งหมดจะย้ายไปอยู่กับกลุ่มใหม่ ทำให้บิลเดือนเก่าที่ออกไปแล้ว
/// อธิบายไม่ได้. บทเรียนเดียวกับที่ <c>UsageEvent</c> เขียนไว้</para>
///
/// <para>เป็น <b>scoped</b> service — cache แผนที่ company → billing account
/// ไว้ในตัวเองได้ปลอดภัย เพราะอายุเท่ากับ 1 request (งาน bulk เช่นจับคู่
/// ธนาคารทั้งงวดยิง AI หลายสิบครั้งใน request เดียว). ห้ามทำเป็น static/
/// IMemoryCache ตามข้อ D ของกฎเหล็ก #4 (multi-instance readiness)</para>
/// </summary>
public class AiUsageAttributionResolver : IAiUsageAttributionResolver
{
    private readonly AccountingDbContext _db;
    private readonly IHttpContextAccessor? _http;
    private readonly ILogger<AiUsageAttributionResolver>? _logger;

    /// <summary>cache อายุเท่า request เดียว — key = companyId</summary>
    private readonly Dictionary<Guid, Guid?> _billingAccountCache = new();

    /// <summary>cache ข้อมูลคีย์ API ของ request นี้ (คีย์เดียวตลอด request)</summary>
    private (Guid Id, Guid? BillingAccountId, Guid? BranchId, bool IsSandbox)? _apiKeyCache;

    public AiUsageAttributionResolver(
        AccountingDbContext db,
        IHttpContextAccessor? http = null,
        ILogger<AiUsageAttributionResolver>? logger = null)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public async Task<AiUsageAttribution> ResolveAsync(Guid companyId, CancellationToken ct)
    {
        try
        {
            var user = _http?.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                // ไม่มี request = งานเบื้องหลัง. ยังผูกกลุ่มบิลไว้ให้ เพราะ
                // แอดมินอยากเห็นว่า "งานกลางคืนของลูกค้ารายนี้กินเท่าไร"
                return new AiUsageAttribution(
                    AiUsageChannel.Background,
                    await ResolveBillingAccountAsync(companyId, ct),
                    null, null, null, false);
            }

            var isApiKey = string.Equals(user.FindFirst("AuthMethod")?.Value, "ApiKey",
                StringComparison.OrdinalIgnoreCase);
            if (isApiKey && Guid.TryParse(user.FindFirst("ApiKeyId")?.Value, out var apiKeyId))
            {
                var key = await LoadApiKeyAsync(apiKeyId, ct);
                return new AiUsageAttribution(
                    AiUsageChannel.ApiKey,
                    // คีย์ที่ผูกกลุ่มบิลเองชนะ (สัญญาที่เรียกเก็บข้ามกลุ่ม) —
                    // ลำดับเดียวกับที่ ApiKey.BillingAccountId อธิบายไว้
                    key?.BillingAccountId ?? await ResolveBillingAccountAsync(companyId, ct),
                    key?.BranchId,
                    apiKeyId,
                    UserId: null,
                    IsSandbox: key?.IsSandbox ?? false);
            }

            Guid? userId = null;
            var sub = user.FindFirst("sub")?.Value ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (Guid.TryParse(sub, out var uid)) userId = uid;

            return new AiUsageAttribution(
                AiUsageChannel.Web,
                await ResolveBillingAccountAsync(companyId, ct),
                BranchId: null,
                ApiClientId: null,
                UserId: userId,
                IsSandbox: false);
        }
        catch (Exception ex)
        {
            // ป้ายกำกับพลาดต้องไม่ทำให้ AI call ล้ม — เสียแค่มิติในรายงาน
            _logger?.LogDebug(ex, "ResolveAiUsageAttribution ล้มเหลว (ไม่กระทบการเรียก AI)");
            return AiUsageAttribution.Unknown;
        }
    }

    private async Task<Guid?> ResolveBillingAccountAsync(Guid companyId, CancellationToken ct)
    {
        if (companyId == Guid.Empty) return null;
        if (_billingAccountCache.TryGetValue(companyId, out var cached)) return cached;
        var id = await _db.Companies.AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => c.BillingAccountId)
            .FirstOrDefaultAsync(ct);
        _billingAccountCache[companyId] = id;
        return id;
    }

    private async Task<(Guid Id, Guid? BillingAccountId, Guid? BranchId, bool IsSandbox)?>
        LoadApiKeyAsync(Guid apiKeyId, CancellationToken ct)
    {
        if (_apiKeyCache?.Id == apiKeyId) return _apiKeyCache;
        var row = await _db.Set<Models.Entities.ApiKey>().AsNoTracking()
            .Where(k => k.Id == apiKeyId)
            .Select(k => new { k.Id, k.BillingAccountId, k.BranchId, k.IsSandbox })
            .FirstOrDefaultAsync(ct);
        if (row == null) return null;
        _apiKeyCache = (row.Id, row.BillingAccountId, row.BranchId, row.IsSandbox);
        return _apiKeyCache;
    }
}
