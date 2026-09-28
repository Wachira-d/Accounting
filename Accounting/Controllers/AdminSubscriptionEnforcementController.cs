using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Controllers;

/// <summary>หนึ่งแถวของผลโหมดเงา + ชื่อบริษัท + คำอธิบายภาษาไทย</summary>
public record SubscriptionShadowHitDto(
    Guid CompanyId, string CompanyName, string Reason, string? Feature, string? Plan, string? RouteKey,
    string? LastMethod, bool WouldBlock, long HitCount, DateTime FirstSeenAt, DateTime LastSeenAt, string Description);

/// <summary>สถานะสวิตช์ + ผลโหมดเงา</summary>
/// <param name="Mode">ชื่อ enum <see cref="SubscriptionEnforcementMode"/> (Off/Shadow/Enforce — ส่งเป็นชื่อเสมอ)</param>
/// <param name="WriteGateMode">ค่า config <c>Subscription:Enforcement:Mode</c> (Off/LogOnly/Enforce) — ด่านเขียนของบริษัทถูกระงับ/หมดอายุ
/// ใช้ค่านี้กับทุกคำขอ ไม่ใช่สวิตช์เว็บ</param>
/// <param name="CompaniesWouldBlock">จำนวนบริษัทที่มีอย่างน้อย 1 เหตุที่ "จะถูกบล็อก" เมื่อเปิด Enforce</param>
public record SubscriptionEnforcementStatusDto(
    string Mode, string WriteGateMode, string Guidance,
    int CompaniesWouldBlock, long HitsWouldBlock, List<SubscriptionShadowHitDto> Hits, bool HitsTruncated);

/// <summary>คำขอเปลี่ยนสวิตช์ — รับเป็น<b>ชื่อ</b> (Off/Shadow/Enforce) · ตัวเลข/ค่าว่าง/ชื่อที่ไม่รู้จัก = 400
/// (ห้ามให้ "ไม่ส่งค่า" กลายเป็น Off เงียบ ๆ)</summary>
public record SetSubscriptionEnforcementModeRequest(string? Mode);

/// <summary>บริษัทหนึ่งในผลตรวจล่วงหน้า (จากข้อมูลปัจจุบัน ไม่ต้องรอให้มีการใช้งาน)</summary>
/// <param name="Impacts">ผลเมื่อเปิดบังคับ (ข้อความไทย) — สถานะ subscription/บริษัท/หมดอายุ</param>
/// <param name="MissingFeatures">ฟีเจอร์ที่มีเส้นทางถูก gate แต่ไม่อยู่ในแพ็กเกจที่มีผล (รวมที่เจ้าของบริษัทปิดเอง) ·
/// <c>null</c> = ยังไม่มี subscription (ตัดสินไม่ได้จนกว่าระบบจะสร้าง FreeTrial ให้ตอนเข้าใช้ครั้งแรก)</param>
public record SubscriptionGatePrecheckRow(
    Guid CompanyId, string CompanyName, string CompanyStatus, bool HasSubscription,
    string? Plan, string? SubscriptionStatus, bool? PlanActive,
    List<string> Impacts, List<string>? MissingFeatures);

/// <summary>จำนวนบริษัทที่ขาดฟีเจอร์หนึ่ง</summary>
public record SubscriptionFeatureGap(string Feature, int Companies, List<string> RouteKeys);

/// <summary>ผลตรวจล่วงหน้าทั้งหน้า</summary>
public record SubscriptionGatePrecheckDto(
    string WriteGateMode, int CompaniesChecked, int TotalCompanies, int Skip, int Take,
    int CompaniesBlockedEntirely, int CompaniesWritesBlocked, int CompaniesWithoutSubscription,
    List<SubscriptionFeatureGap> FeatureGaps, List<SubscriptionGatePrecheckRow> Rows, string Guidance);

/// <summary>
/// <b>หน้าแอดมิน "บังคับแพ็กเกจบนหน้าเว็บ"</b> — รอบ 198 ข้อ 5 (คำตัดสินเจ้าของ: รายงานก่อน แล้วค่อยเปิดบังคับ)
///
/// <para>(1) ผลโหมดเงา: บริษัทไหนจะถูกบล็อก เพราะอะไร กี่ครั้ง ครั้งแรก/ล่าสุดเมื่อไร (จากการใช้งานจริง) ·
/// (2) ตรวจล่วงหน้าจากข้อมูลปัจจุบัน (สถานะบริษัท/แพ็กเกจ/ฟีเจอร์) โดยไม่ต้องรอการใช้งาน ·
/// (3) สวิตช์ <c>SiteSettings.SubscriptionEnforcementMode</c> ที่เจ้าของกดเปลี่ยนเอง</para>
///
/// <para>ข้ามบริษัทโดยเจตนา — ด่าน <c>SystemAdmin</c> + ปฏิเสธ API key ทุกทางเขียน · ตัวตัดสินอยู่ที่
/// <see cref="SubscriptionGatePolicy"/> ตัวเดียวกับ middleware (ห้ามเขียนเงื่อนไขซ้ำที่นี่)</para>
/// </summary>
[ApiController]
[Route("api/admin/subscription-enforcement")]
[Authorize(Roles = "SystemAdmin")]
public class AdminSubscriptionEnforcementController : ControllerBase
{
    private const int MaxHits = 2000;
    private const int MaxPrecheckPage = 500;

    private readonly AccountingDbContext _db;
    private readonly ISubscriptionService _subs;
    private readonly ISubscriptionGateShadowLog _shadow;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminSubscriptionEnforcementController> _logger;

    public AdminSubscriptionEnforcementController(AccountingDbContext db, ISubscriptionService subs,
        ISubscriptionGateShadowLog shadow, IConfiguration config, ILogger<AdminSubscriptionEnforcementController> logger)
    {
        _db = db;
        _subs = subs;
        _shadow = shadow;
        _config = config;
        _logger = logger;
    }

    private const string Guidance =
        "โหมดเงา (Shadow) = ระบบตัดสินทุกคำขอจากหน้าเว็บเหมือนเปิดบังคับ แต่ไม่บล็อก แค่บันทึกไว้ที่นี่ · " +
        "ตรวจรายการ \"จะถูกบล็อก\" ให้ครบ (แก้แพ็กเกจ/เปิดฟีเจอร์ให้ลูกค้าที่ควรได้) แล้วค่อยเปลี่ยนเป็น \"บังคับ\" · " +
        "คำขอจาก partner/integration ที่ส่ง X-Company-Id มาเองถูกบังคับอยู่แล้วตั้งแต่ก่อนรอบนี้ ไม่ขึ้นกับสวิตช์นี้ · " +
        "บริษัทถูกระงับ/หมดอายุ บล็อกเฉพาะการสร้าง/แก้ไข และทำงานเมื่อค่า config Subscription:Enforcement:Mode = Enforce เท่านั้น";

    private SubscriptionWriteGateMode WriteMode() =>
        SubscriptionGatePolicy.ParseWriteMode(_config["Subscription:Enforcement:Mode"]);

    [HttpGet]
    public async Task<ActionResult<ApiResponse<SubscriptionEnforcementStatusDto>>> Get(CancellationToken ct = default)
    {
        var mode = await _shadow.GetWebModeAsync(ct);
        IReadOnlyList<SubscriptionGateShadowRow> rows;
        try
        {
            rows = await _shadow.ListAsync(MaxHits + 1, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "อ่านผลโหมดเงา gate แพ็กเกจไม่ได้");
            return StatusCode(500, new ApiResponse<SubscriptionEnforcementStatusDto>(false, null,
                "อ่านตารางผลโหมดเงาไม่ได้ (SubscriptionGateShadowHits) — ตรวจว่า migration รันแล้ว"));
        }
        var truncated = rows.Count > MaxHits;
        var page = rows.Take(MaxHits).ToList();

        var ids = page.Select(r => r.CompanyId).Distinct().ToList();
        var names = await _db.Companies.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var hits = page.Select(r =>
        {
            Enum.TryParse<SubscriptionGateReason>(r.Reason, out var reason);
            var feature = string.IsNullOrEmpty(r.Feature) ? null : r.Feature;
            var text = SubscriptionGatePolicy.Describe(reason, feature, r.Plan);
            if (!r.WouldBlock)
                text += " (ตอนนี้ config ด่านเขียนเป็น LogOnly — เปิดสวิตช์นี้อย่างเดียวยังไม่บล็อก)";
            return new SubscriptionShadowHitDto(r.CompanyId,
                names.TryGetValue(r.CompanyId, out var n) ? n : "(ไม่พบบริษัท)",
                r.Reason, feature, r.Plan, r.RouteKey, r.LastMethod, r.WouldBlock, r.HitCount,
                r.FirstSeenAt, r.LastSeenAt, text);
        }).ToList();

        var blocking = hits.Where(h => h.WouldBlock).ToList();
        return Ok(new ApiResponse<SubscriptionEnforcementStatusDto>(true, new SubscriptionEnforcementStatusDto(
            mode.ToString(), WriteMode().ToString(), Guidance,
            blocking.Select(h => h.CompanyId).Distinct().Count(), blocking.Sum(h => h.HitCount),
            hits, truncated)));
    }

    /// <summary>ตรวจล่วงหน้าจากข้อมูลปัจจุบัน — ทีละหน้า (<paramref name="take"/> ≤ 500) เพราะเรียก resolver ต่อบริษัท</summary>
    [HttpGet("precheck")]
    public async Task<ActionResult<ApiResponse<SubscriptionGatePrecheckDto>>> Precheck(
        [FromQuery] int skip = 0, [FromQuery] int take = 200, CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxPrecheckPage);
        var writeMode = WriteMode();

        var total = await _db.Companies.AsNoTracking().CountAsync(ct);
        var companies = await _db.Companies.AsNoTracking()
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Skip(skip).Take(take)
            .Select(c => new { c.Id, c.Name, c.Status })
            .ToListAsync(ct);

        var rows = new List<SubscriptionGatePrecheckRow>();
        var gapCount = new Dictionary<FeatureFlags, int>();
        int blockedAll = 0, writesBlocked = 0, noSub = 0;
        foreach (var c in companies)
        {
            var state = await _subs.GetGateStateAsync(c.Id);
            if (state == null)
            {
                noSub++;
                rows.Add(new SubscriptionGatePrecheckRow(c.Id, c.Name, c.Status.ToString(), false, null, null, null,
                    new List<string> { "ยังไม่มีแพ็กเกจ — ระบบจะสร้าง FreeTrial ให้ตอนเข้าใช้ครั้งแรก แล้วตัดสินตามแพ็กเกจนั้น" },
                    null));
                continue;
            }

            var eff = await _subs.GetEffectivePlanAsync(c.Id);
            var impacts = new List<string>();

            // ด่านเขียน (สถานะ) — ถามตัวตัดสินตัวเดียวกับ middleware ด้วยคำขอเขียนสมมุติ
            var writeVerdict = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(
                state.Status, state.EnabledFeatures, IsWrite: true, writeMode,
                CompanySuspended: c.Status == CompanyStatus.Suspended, PlanActive: eff?.IsActive, RequiredFeature: null));
            if (writeVerdict.Block == SubscriptionGateReason.SubscriptionInactive) blockedAll++;
            else if (writeVerdict.Blocks) writesBlocked++;
            if (writeVerdict.Blocks)
                impacts.Add(SubscriptionGatePolicy.Describe(writeVerdict.Block, null, state.Plan.ToString()));
            foreach (var r in writeVerdict.LogOnly)
                impacts.Add(SubscriptionGatePolicy.Describe(r, null, state.Plan.ToString())
                            + " (config ด่านเขียน = LogOnly — ยังไม่บล็อก)");

            // ฟีเจอร์ที่ถูก gate — ถามตัวตัดสินทีละฟีเจอร์ด้วยคำขออ่านสมุติ (ไม่เขียนเงื่อนไข HasFlag ซ้ำที่นี่)
            var missing = new List<string>();
            if (writeVerdict.Block != SubscriptionGateReason.SubscriptionInactive)
            {
                foreach (var f in SubscriptionGatePolicy.GatedFeatures)
                {
                    var v = SubscriptionGatePolicy.Decide(new SubscriptionGateFacts(
                        state.Status, state.EnabledFeatures, IsWrite: false, writeMode, null, null, f));
                    if (v.Block != SubscriptionGateReason.FeatureNotInPlan) continue;
                    missing.Add(f.ToString());
                    gapCount[f] = gapCount.GetValueOrDefault(f) + 1;
                }
            }

            if (impacts.Count == 0 && missing.Count == 0) continue;
            rows.Add(new SubscriptionGatePrecheckRow(c.Id, c.Name, c.Status.ToString(), true,
                state.Plan.ToString(), state.Status.ToString(), eff?.IsActive, impacts, missing));
        }

        var gaps = gapCount
            .OrderByDescending(g => g.Value).ThenBy(g => g.Key.ToString(), StringComparer.Ordinal)
            .Select(g => new SubscriptionFeatureGap(g.Key.ToString(), g.Value,
                SubscriptionGatePolicy.RouteFeatureMap.Where(m => m.Feature == g.Key).Select(m => m.Path).ToList()))
            .ToList();

        return Ok(new ApiResponse<SubscriptionGatePrecheckDto>(true, new SubscriptionGatePrecheckDto(
            writeMode.ToString(), companies.Count, total, skip, take, blockedAll, writesBlocked, noSub, gaps, rows,
            "ตรวจจากข้อมูลวันนี้ (ไม่ต้องรอการใช้งาน) · \"ฟีเจอร์ที่จะถูกปิด\" มีผลเฉพาะเมื่อผู้ใช้เปิดหน้าที่เรียกเส้นทางนั้น — " +
            "ดูผลโหมดเงาด้านบนเพื่อรู้ว่าใครใช้จริง")));
    }

    /// <summary>เปลี่ยนสวิตช์ (Off/Shadow/Enforce) — แอดมินแพลตฟอร์มที่ล็อกอินเท่านั้น (API key ห้าม)</summary>
    [HttpPut("mode")]
    [Authorize(Roles = "SystemAdmin")]
    [Accounting.Filters.RejectApiKey("เปลี่ยนสวิตช์บังคับแพ็กเกจบนหน้าเว็บ")]
    public async Task<ActionResult<ApiResponse<SubscriptionEnforcementStatusDto>>> SetMode(
        [FromBody] SetSubscriptionEnforcementModeRequest request, CancellationToken ct = default)
    {
        var raw = request?.Mode?.Trim();
        if (string.IsNullOrEmpty(raw) || raw.Any(char.IsDigit)
            || !Enum.TryParse<SubscriptionEnforcementMode>(raw, ignoreCase: true, out var mode)
            || !Enum.IsDefined(mode))
            return BadRequest(new ApiResponse<SubscriptionEnforcementStatusDto>(false, null,
                "โหมดต้องเป็น Off, Shadow หรือ Enforce"));

        var settings = await _db.SiteSettings.OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (settings == null)
        {
            settings = new SiteSettings();
            _db.SiteSettings.Add(settings);
        }
        var before = settings.SubscriptionEnforcementMode;
        settings.SubscriptionEnforcementMode = mode;
        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _db.SaveChangesAsync(ct);   // AuditTrail จับ old/new ของ SiteSettings ให้อัตโนมัติ

        // เปลี่ยนสวิตช์ระดับแพลตฟอร์ม = กระทบทุกบริษัท → ต้องมีร่องรอยใน log ด้วย ไม่ใช่เงียบ
        _logger.LogWarning("สวิตช์บังคับแพ็กเกจบนหน้าเว็บ: {Before} → {After} โดยผู้ใช้ {UserId}",
            before, mode, settings.UpdatedBy);
        return await Get(ct);
    }

    /// <summary>ล้างผลโหมดเงาทั้งหมด (เริ่มนับใหม่หลังแก้แพ็กเกจให้ลูกค้าแล้ว)</summary>
    [HttpDelete("hits")]
    [Authorize(Roles = "SystemAdmin")]
    [Accounting.Filters.RejectApiKey("ล้างผลโหมดเงา")]
    public async Task<ActionResult<ApiResponse<SubscriptionEnforcementStatusDto>>> ClearHits(CancellationToken ct = default)
    {
        var removed = await _shadow.ClearAsync(ct);
        _logger.LogWarning("ล้างผลโหมดเงา gate แพ็กเกจ {Rows} แถว โดยผู้ใช้ {UserId}",
            removed, JwtHelper.GetUserIdFromClaims(User));
        return await Get(ct);
    }
}
