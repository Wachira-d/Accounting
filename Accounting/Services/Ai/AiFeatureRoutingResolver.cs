using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Ai;

/// <summary>
/// Per-feature routing policy lookup. Reads AiFeatureRoutingConfig
/// rows (one per AiFeatureKey) and caches them in-memory for ~1 minute
/// so the orchestrator doesn't issue a SELECT on every AI call.
///
/// Admin writes through SetAsync; the cache is invalidated by version
/// bump so the next read picks up the change without an app restart.
/// </summary>
public interface IAiFeatureRoutingResolver
{
    Task<AiFeatureRoutingDecision> ResolveAsync(AiFeatureKey feature, CancellationToken ct);
    Task SetAsync(AiFeatureKey feature, AiFeatureRoutingMode mode,
        decimal? threshold, decimal? samplingRate, string? note, string? user,
        CancellationToken ct);
    Task<IReadOnlyList<AiFeatureRoutingConfig>> ListAllAsync(CancellationToken ct);
    /// <summary>Force the next ResolveAsync to re-read from DB. Called
    /// after admin SetAsync; also exposed for tests.</summary>
    void InvalidateCache();
}

/// <summary>
/// Effective routing decision the orchestrator acts on. The defaults
/// (Hybrid, 0.85 threshold, 0.10 sampling) match the global constants
/// the orchestrator used to hard-code — admin-set rows OVERRIDE these.
/// </summary>
public sealed record AiFeatureRoutingDecision(
    AiFeatureRoutingMode Mode,
    decimal LocalConfidenceThreshold,
    decimal ProviderSamplingRate);

public class AiFeatureRoutingResolver : IAiFeatureRoutingResolver
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AiFeatureRoutingResolver> _logger;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(1);

    private const decimal DefaultThreshold = 0.85m;
    private const decimal DefaultSampling = 0.10m;

    /// <summary>อัตราสุ่มถามครูขั้นต่ำ — **ห้ามเป็น 0**
    ///
    /// <para>การสุ่มถามครูคือ<b>ช่องทางเดียว</b>ที่ระบบจะรู้ว่านักเรียนเริ่มตอบผิด:
    /// ถ้าตั้งเป็น 0 เราจะไม่มีคำตอบของครูมาเทียบอีกเลย ⇒ ตัววัด "นักเรียนแม่นแค่ไหน"
    /// กลายเป็นค่าที่ไม่มีวันเปลี่ยน แล้วระบบก็จะดู "สุขภาพดี" ตลอดกาลทั้งที่กำลังแย่ลง
    /// (ทีม T3 รอบ 177 · ญาติของ "ด่านที่ป้อนผลของสูตรที่ตัวเองตรวจ")</para>
    ///
    /// <para>ปิดการถามครูจริง ๆ ให้ใช้ <c>AiFeatureRoutingMode.LocalOnly</c> หรือปิด
    /// provider — ซึ่งเป็น**การตัดสินใจที่มองเห็นได้** ไม่ใช่ตัวเลขที่เลื่อนไปจนสุด</para></summary>
    public const decimal MinSamplingRate = 0.01m;
    private const AiFeatureRoutingMode DefaultMode = AiFeatureRoutingMode.Hybrid;

    /// <summary>บังคับพื้นขั้นต่ำของอัตราสุ่มถามครู — **กติกาตัวเดียวที่ใช้ทั้งฝั่งเขียน
    /// และฝั่งอ่าน** (รอบ 181 · D7-4)
    ///
    /// <para>เดิมพื้นนี้ถูกบังคับเฉพาะตอน <see cref="SetAsync"/> ⇒ แถวที่แอดมินตั้ง
    /// <c>0</c> ไว้**ก่อน**รอบ 178 ยังคงเป็น 0 อยู่ในฐาน และฝั่งอ่าน
    /// (<c>EnsureCacheFreshAsync</c>) ส่งค่า 0 นั้นให้ orchestrator ตรง ๆ ⇒ ครูถูกปิดถาวร
    /// โดยไม่มีใครรู้: นักเรียนจะไม่มีวันได้ตัวอย่างใหม่ และตัววัด "นักเรียนแม่นแค่ไหน"
    /// ก็ไม่มีวันเปลี่ยน = ระบบดู "สุขภาพดี" ตลอดกาล (ด่านที่ป้อนผลของสูตรที่ตัวเองตรวจ)</para>
    ///
    /// <para><b>ทิศตรงข้ามที่ต้องคงไว้</b>: โหมด <see cref="AiFeatureRoutingMode.LocalOnly"/>
    /// คือ**เจตนาปิดครูจริง ๆ ที่มองเห็นได้** — 0 ของโหมดนั้นต้องอยู่เป็น 0 ห้ามยกพื้นให้
    /// (ไม่งั้นกลายเป็นยิง provider ทั้งที่แอดมินสั่งปิด = kill-switch ใช้ไม่ได้จริง)</para></summary>
    public static decimal ClampSamplingRate(AiFeatureRoutingMode mode, decimal rate)
    {
        if (mode == AiFeatureRoutingMode.LocalOnly) return rate;
        if (rate > 1m) return 1m;
        return rate < MinSamplingRate ? MinSamplingRate : rate;
    }

    private Dictionary<string, AiFeatureRoutingDecision> _cache = new();
    private DateTime _cacheLoadedAt = DateTime.MinValue;
    private readonly object _lock = new();

    public AiFeatureRoutingResolver(IServiceProvider services,
        ILogger<AiFeatureRoutingResolver> logger)
    { _services = services; _logger = logger; }

    public async Task<AiFeatureRoutingDecision> ResolveAsync(AiFeatureKey feature, CancellationToken ct)
    {
        await EnsureCacheFreshAsync(ct);
        lock (_lock)
        {
            return _cache.TryGetValue(feature.ToString(), out var dec)
                ? dec
                : new AiFeatureRoutingDecision(DefaultMode, DefaultThreshold, DefaultSampling);
        }
    }

    public async Task SetAsync(AiFeatureKey feature, AiFeatureRoutingMode mode,
        decimal? threshold, decimal? samplingRate, string? note, string? user,
        CancellationToken ct)
    {
        if (threshold.HasValue && (threshold.Value < 0 || threshold.Value > 1))
            throw new ArgumentOutOfRangeException(nameof(threshold), "Must be in [0,1].");
        if (samplingRate.HasValue && (samplingRate.Value < 0 || samplingRate.Value > 1))
            throw new ArgumentOutOfRangeException(nameof(samplingRate), "Must be in [0,1].");
        // พื้นขั้นต่ำ — ดูเหตุผลที่ MinSamplingRate · ผู้ใช้ที่ตั้งใจปิดจริงต้องเลือก
        // โหมด LocalOnly ซึ่งบอกเจตนาตรง ๆ แทนการเลื่อนอัตราไปจนเป็นศูนย์
        // ⚠️ ใช้ ClampSamplingRate **ตัวเดียวกับฝั่งอ่าน** — กติกาสองสำเนาคือที่มาของ D7-4
        if (samplingRate is { } sr) samplingRate = ClampSamplingRate(mode, sr);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var key = feature.ToString();
        var row = await db.AiFeatureRoutingConfigs.FirstOrDefaultAsync(c => c.FeatureKey == key, ct);
        if (row == null)
        {
            row = new AiFeatureRoutingConfig { FeatureKey = key };
            db.AiFeatureRoutingConfigs.Add(row);
        }
        row.Mode = mode;
        row.LocalConfidenceThreshold = threshold;
        row.ProviderSamplingRate = samplingRate;
        row.AdminNote = note;
        row.LastModifiedBy = user;
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        InvalidateCache();
        _logger.LogInformation("AI routing updated by {User}: {Feature} → {Mode}", user, feature, mode);
    }

    public async Task<IReadOnlyList<AiFeatureRoutingConfig>> ListAllAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        return await db.AiFeatureRoutingConfigs.AsNoTracking().OrderBy(c => c.FeatureKey).ToListAsync(ct);
    }

    public void InvalidateCache()
    {
        lock (_lock) _cacheLoadedAt = DateTime.MinValue;
    }

    private async Task EnsureCacheFreshAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _cacheLoadedAt < CacheTtl) return;
        }
        Dictionary<string, AiFeatureRoutingDecision> fresh;
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var rows = await db.AiFeatureRoutingConfigs.AsNoTracking().ToListAsync(ct);
            fresh = rows.ToDictionary(
                r => r.FeatureKey,
                r => new AiFeatureRoutingDecision(
                    r.Mode,
                    r.LocalConfidenceThreshold ?? DefaultThreshold,
                    // ★ D7-4: clamp **ตอนอ่าน** ด้วย — แถวที่เก็บ 0 ไว้ก่อนรอบ 178 ยัง
                    // อยู่ในฐานและไม่มีใครไปแตะ (การแก้ที่ SetAsync มีผลเฉพาะตอนแอดมิน
                    // กดบันทึกใหม่เท่านั้น) ⇒ ถ้าไม่ clamp ที่นี่ feature นั้นจะไม่มีวัน
                    // สุ่มถามครูอีกเลย · แถวที่ยังไม่เคยตั้งค่า (null) ใช้ค่าเริ่มต้น 0.10
                    ClampSamplingRate(r.Mode, r.ProviderSamplingRate ?? DefaultSampling)));
        }
        catch (Exception ex)
        {
            // First-run safety: table may not exist yet on a brand-new DB
            // before EnsureCreated+ApplyMissingColumns has run. Fall back
            // to defaults until next reload.
            _logger.LogWarning(ex, "Routing config load failed; using defaults");
            fresh = new();
        }
        lock (_lock) { _cache = fresh; _cacheLoadedAt = DateTime.UtcNow; }
    }
}
