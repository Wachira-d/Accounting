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

/// <summary>หนึ่งแถวของรายงานเงา + ชื่อบริษัท + คำอธิบายภาษาไทย — "บริษัทไหน · endpoint ไหน · เพราะอะไร · กี่ครั้ง"</summary>
/// <param name="Endpoint">route template ของ endpoint (ไม่มี id) · ว่าง = แถวก่อนรอบ 200 ที่ยังไม่เก็บ endpoint</param>
/// <param name="HitCount">ครั้งที่ "จะถูกบล็อก" (โหมดเงา)</param>
/// <param name="BlockedCount">ครั้งที่ถูกบล็อกจริง (หน้าเว็บหลังเปิดบังคับ)</param>
/// <param name="IsTrial">ลูกค้าทดลองใช้/แพ็กเกจฟรี (<see cref="SubscriptionTrialReadiness.IsTrialLike(string?, string?)"/>)</param>
public record SubscriptionShadowHitDto(
    Guid CompanyId, string CompanyName, string Reason, string? Feature, string? Plan, string? SubscriptionStatus, bool IsTrial,
    string? RouteKey, string Endpoint, string? LastMethod, bool WouldBlock, long HitCount, long BlockedCount,
    DateTime FirstSeenAt, DateTime LastSeenAt, string Description);

/// <summary>โหมดที่มีผลจริง + เพราะอะไร (ตัวตัดสินเดียว <see cref="SubscriptionEnforcementResolver"/> — ชุดเดียวกับที่ middleware ใช้)</summary>
/// <param name="EffectiveMode">โหมดที่มีผลจริง (Off/Shadow/Enforce — ชื่อ)</param>
/// <param name="Source">AdminSwitch / AdminSwitchDefault / AdminSwitchUnreadable / EmergencyOverride</param>
/// <param name="AdminSwitchHasEffect">false = มี override ฉุกเฉินทับอยู่ — กดสวิตช์ได้ (บันทึก) แต่ยังไม่มีผลจนกว่าจะลบ override</param>
/// <param name="OverrideKey">ชื่อคีย์ config ของ override ฉุกเฉิน</param>
/// <param name="HeaderWriteGateMode">ด่านบริษัทถูกระงับ/หมดอายุของคำขอ partner ที่ส่ง X-Company-Id เอง (Enforce/LogOnly)</param>
public record SubscriptionEffectiveModeDto(
    string EffectiveMode, string EffectiveModeLabel, string Source, string Explanation, bool AdminSwitchHasEffect,
    string? OverrideMode, string? OverrideRaw, string OverrideKey, string HeaderWriteGateMode, List<string> Warnings);

/// <summary>สถานะสวิตช์ + โหมดที่มีผลจริง + รายงานเงา</summary>
/// <param name="Mode">ค่าสวิตช์แอดมินที่บันทึกไว้ (Off/Shadow/Enforce — ชื่อเสมอ) · ไม่ใช่โหมดที่มีผลจริงเสมอไป ⇒ ดู <paramref name="Effective"/></param>
/// <param name="CompaniesWouldBlock">จำนวนบริษัทที่มีอย่างน้อย 1 เหตุที่ "จะถูกบล็อก" (โหมดเงา)</param>
/// <param name="CompaniesBlocked">จำนวนบริษัทที่ถูกบล็อกจริงแล้ว (หลังเปิดบังคับ)</param>
/// <param name="TrialBlocks">ลูกค้าทดลองใช้ (จะ) ถูกบล็อกกี่ครั้งเพราะอะไร — เซิร์ฟเวอร์สรุปให้ หน้าเว็บแสดงอย่างเดียว</param>
/// <param name="RetentionDays">แถวที่ไม่ถูกพบซ้ำเกินกี่วันถูกตัดทิ้ง</param>
/// <param name="PrunedRows">จำนวนแถวที่ตัดทิ้งในการเปิดหน้านี้ครั้งนี้</param>
public record SubscriptionEnforcementStatusDto(
    string Mode, SubscriptionEffectiveModeDto Effective, string Guidance,
    int CompaniesWouldBlock, long HitsWouldBlock, int CompaniesBlocked, long HitsBlocked,
    List<TrialBlockSummary> TrialBlocks, List<SubscriptionShadowHitDto> Hits, bool HitsTruncated,
    int RetentionDays, int PrunedRows);

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

/// <summary>ความพร้อมของลูกค้าช่วงทดลอง/แพ็กเกจฟรี (ข้อ 14) — ตามข้อมูลแพ็กเกจที่แอดมินตั้ง</summary>
/// <param name="Plans">แพ็กเกจที่เปิดใช้ทุกตัว: ลูกค้าทดลองได้ฟีเจอร์อะไร · จะถูกปิดเส้นทางไหนเมื่อเปิดบังคับ · ตั้งไว้ว่างไหม</param>
/// <param name="TrialSubscriptionsEmptySnapshot">subscription ทดลอง/ฟรีที่สำเนาฟีเจอร์ว่าง (ระบบใช้ข้อมูลแพ็กเกจแทนแล้ว)</param>
/// <param name="Problems">ข้อความที่ต้องแก้ก่อนเปิดบังคับ (ว่าง = พร้อม)</param>
public record TrialReadinessDto(List<PlanTrialReadinessRow> Plans, int TrialSubscriptionsEmptySnapshot, List<string> Problems);

/// <summary>ผลตรวจล่วงหน้าทั้งหน้า</summary>
/// <param name="WriteGateMode">ด่านเขียนที่ใช้ตัดสินล่วงหน้า = เหมือน "กดบังคับแล้ว" เสมอ (Enforce) — ตอบคำถาม "ถ้าเปิดวันนี้ใครโดน"</param>
public record SubscriptionGatePrecheckDto(
    string WriteGateMode, int CompaniesChecked, int TotalCompanies, int Skip, int Take,
    int CompaniesBlockedEntirely, int CompaniesWritesBlocked, int CompaniesWithoutSubscription,
    List<SubscriptionFeatureGap> FeatureGaps, List<SubscriptionGatePrecheckRow> Rows, TrialReadinessDto Trial, string Guidance);

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
        "ตรวจรายการ \u201Cจะถูกบล็อก\u201D ให้ครบ (แก้แพ็กเกจ/เปิดฟีเจอร์ให้ลูกค้าที่ควรได้) แล้วค่อยเปลี่ยนเป็น \u201Cบังคับ\u201D · " +
        "สวิตช์นี้ (ฐานข้อมูล) เป็นตัวตัดสินหลักของทั้งการตัดสินฟีเจอร์และด่านบริษัทถูกระงับ/หมดอายุ · " +
        "คำขอจาก partner/integration ที่ส่ง X-Company-Id มาเองถูกตัดสินฟีเจอร์อยู่แล้วตั้งแต่ก่อนรอบ 198 ไม่ขึ้นกับสวิตช์นี้";

    /// <summary>โหมดที่มีผลจริง — ตัวตัดสินเดียวกับ middleware (<see cref="SubscriptionEnforcementResolver"/>)</summary>
    private async Task<SubscriptionEnforcementState> EffectiveAsync(CancellationToken ct)
    {
        var adminSwitch = await _shadow.ReadAdminSwitchAsync(ct);
        return SubscriptionEnforcementResolver.Resolve(adminSwitch, _config);
    }

    private static SubscriptionEffectiveModeDto ToDto(SubscriptionEnforcementState e) => new(
        e.EffectiveMode.ToString(), SubscriptionEnforcementResolver.Label(e.EffectiveMode), e.Source.ToString(), e.Explanation,
        e.AdminSwitchHasEffect, e.OverrideMode?.ToString(), e.OverrideRaw, SubscriptionEnforcementResolver.OverrideKey,
        e.HeaderWriteGateMode.ToString(), e.Warnings.ToList());

    [HttpGet]
    public async Task<ActionResult<ApiResponse<SubscriptionEnforcementStatusDto>>> Get(CancellationToken ct = default)
    {
        var enforcement = await EffectiveAsync(ct);
        // ตารางไม่โตไม่จำกัด: ตัดแถวที่ไม่ถูกพบซ้ำเกินระยะเก็บ (DELETE ตามเวลา · ปลอดภัยข้าม instance)
        var pruned = await _shadow.PruneAsync(SubscriptionGatePolicy.ShadowRetentionDays, ct);
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
                text += " (แถวก่อนรอบ 200: ตอนนั้น config ด่านเขียนเป็น LogOnly — ตอนนี้ด่านนี้บล็อกเมื่อโหมดที่มีผลจริง = บังคับ)";
            return new SubscriptionShadowHitDto(r.CompanyId,
                names.TryGetValue(r.CompanyId, out var n) ? n : "(ไม่พบบริษัท)",
                r.Reason, feature, r.Plan, r.SubscriptionStatus, SubscriptionTrialReadiness.IsTrialLike(r.Plan, r.SubscriptionStatus),
                r.RouteKey, r.Endpoint, r.LastMethod, r.WouldBlock, r.HitCount, r.BlockedCount,
                r.FirstSeenAt, r.LastSeenAt, text);
        }).ToList();

        var wouldBlock = hits.Where(h => h.HitCount > 0).ToList();
        var blocked = hits.Where(h => h.BlockedCount > 0).ToList();
        var trial = SubscriptionTrialReadiness.SummarizeTrialBlocks(page.Select(r => new SubscriptionShadowTally(
            r.CompanyId, r.Reason, r.Feature, r.Plan, r.SubscriptionStatus, r.HitCount, r.BlockedCount))).ToList();
        return Ok(new ApiResponse<SubscriptionEnforcementStatusDto>(true, new SubscriptionEnforcementStatusDto(
            enforcement.AdminSwitch.ToString(), ToDto(enforcement), Guidance,
            wouldBlock.Select(h => h.CompanyId).Distinct().Count(), wouldBlock.Sum(h => h.HitCount),
            blocked.Select(h => h.CompanyId).Distinct().Count(), blocked.Sum(h => h.BlockedCount),
            trial, hits, truncated, SubscriptionGatePolicy.ShadowRetentionDays, pruned)));
    }

    /// <summary>ตรวจล่วงหน้าจากข้อมูลปัจจุบัน — ทีละหน้า (<paramref name="take"/> ≤ 500) เพราะเรียก resolver ต่อบริษัท</summary>
    [HttpGet("precheck")]
    public async Task<ActionResult<ApiResponse<SubscriptionGatePrecheckDto>>> Precheck(
        [FromQuery] int skip = 0, [FromQuery] int take = 200, CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxPrecheckPage);
        // "ถ้ากดบังคับวันนี้ใครโดน" ⇒ ด่านเขียนแบบบังคับเสมอ (เหมือนโหมดเงา — SubscriptionGatePolicy.WriteGateModeFor)
        var writeMode = SubscriptionWriteGateMode.Enforce;

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

        var trial = await TrialReadinessAsync(ct);
        return Ok(new ApiResponse<SubscriptionGatePrecheckDto>(true, new SubscriptionGatePrecheckDto(
            writeMode.ToString(), companies.Count, total, skip, take, blockedAll, writesBlocked, noSub, gaps, rows, trial,
            "ตรวจจากข้อมูลวันนี้ (ไม่ต้องรอการใช้งาน) · \"ฟีเจอร์ที่จะถูกปิด\" มีผลเฉพาะเมื่อผู้ใช้เปิดหน้าที่เรียกเส้นทางนั้น — " +
            "ดูผลโหมดเงาด้านบนเพื่อรู้ว่าใครใช้จริง")));
    }

    /// <summary>ข้อ 14: ลูกค้าช่วงทดลอง/แพ็กเกจฟรีได้ฟีเจอร์อะไรเมื่อเปิดบังคับ — ตามข้อมูลแพ็กเกจที่แอดมินตั้ง (ข้ามบริษัทโดยเจตนา: ข้อมูลแพลตฟอร์ม)</summary>
    private async Task<TrialReadinessDto> TrialReadinessAsync(CancellationToken ct)
    {
        var templates = await _db.PlanTemplates.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Plan).ThenBy(t => t.CreatedAt)
            .Select(t => new { t.Plan, t.Name, t.IsPermanentFree, t.EnabledFeatures, t.TrialFeatures })
            .ToListAsync(ct);
        var plans = templates
            .Select(t => SubscriptionTrialReadiness.CheckTemplate(t.Plan, t.Name, t.IsPermanentFree, t.EnabledFeatures, t.TrialFeatures))
            .ToList();
        var problems = plans.Where(p => p.Problem != null).Select(p => $"{p.Name} ({p.Plan}): {p.Problem}").ToList();
        if (!templates.Any(t => t.Plan == SubscriptionPlan.FreeTrial))
            problems.Add("ไม่มีแพ็กเกจ FreeTrial ที่เปิดใช้ — บริษัทใหม่/บริษัทเก่าที่ยังไม่มีแพ็กเกจจะได้ชุดฟีเจอร์ตั้งต้นของระบบ (TrialFeatures) "
                + "ไม่ใช่ชุดที่แอดมินตั้ง · เปิดใช้/สร้างแพ็กเกจ FreeTrial ก่อนเปิดบังคับ");
        var emptySnapshot = await _db.Subscriptions.AsNoTracking()
            .Where(s => (s.Status == SubscriptionStatus.Trial || s.Plan == SubscriptionPlan.FreeTrial)
                        && s.EnabledFeatures == FeatureFlags.None)
            .CountAsync(ct);
        return new TrialReadinessDto(plans, emptySnapshot, problems);
    }

    /// <summary>เปลี่ยนสวิตช์ (Off/Shadow/Enforce) — แอดมินแพลตฟอร์มที่ล็อกอินเท่านั้น (API key ห้าม)</summary>
    [HttpPut("mode")]
    [Authorize(Roles = "SystemAdmin")]
    [Accounting.Filters.RejectApiKey("เปลี่ยนสวิตช์บังคับแพ็กเกจบนหน้าเว็บ")]
    public async Task<ActionResult<ApiResponse<SubscriptionEnforcementStatusDto>>> SetMode(
        [FromBody] SetSubscriptionEnforcementModeRequest request, CancellationToken ct = default)
    {
        // ตัวอ่านชื่อโหมดตัวเดียวกับ override ฉุกเฉิน (ชื่อเท่านั้น · ตัวเลข/ว่าง/ไม่รู้จัก = 400 — ห้ามให้ "ไม่ส่งค่า" กลายเป็น Off เงียบ ๆ)
        if (SubscriptionEnforcementResolver.ParseMode(request?.Mode) is not SubscriptionEnforcementMode mode)
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
        // ห้าม silent no-op (ข้อ 14): มี override ฉุกเฉินทับอยู่ ⇒ บันทึกแล้วแต่ยังไม่มีผล — บอกในข้อความตอบกลับ (หน้าเว็บแสดงเป็นคำเตือน)
        var enforcement = await EffectiveAsync(ct);
        var result = await Get(ct);
        if (!enforcement.AdminSwitchHasEffect && result.Result is OkObjectResult ok
            && ok.Value is ApiResponse<SubscriptionEnforcementStatusDto> body && body.Data is not null)
            return Ok(new ApiResponse<SubscriptionEnforcementStatusDto>(true, body.Data,
                $"บันทึกสวิตช์เป็น {SubscriptionEnforcementResolver.Label(mode)} แล้ว แต่ยังไม่มีผล — {enforcement.Explanation}"));
        return result;
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
