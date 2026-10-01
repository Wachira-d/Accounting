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
/// <param name="PartnerHitCount">รอบ 200 S200-3: ครั้งที่คำขอ partner (ส่ง X-Company-Id) / คีย์ API ของ /api/v1 "จะถูกบล็อก"</param>
/// <param name="PartnerBlockedCount">ครั้งที่คำขอ partner / คีย์ API ถูกบล็อกจริง</param>
/// <param name="NewlyGated">คีย์เส้นทางนี้เพิ่งเริ่มมีผลรอบ 200 (<see cref="SubscriptionGatePolicy.NewlyGatedRouteKeys"/>) — เป็นเงาสำหรับทุกผู้เรียกจนกว่าจะกดบังคับ</param>
public record SubscriptionShadowHitDto(
    Guid CompanyId, string CompanyName, string Reason, string? Feature, string? Plan, string? SubscriptionStatus, bool IsTrial,
    string? RouteKey, string Endpoint, string? LastMethod, bool WouldBlock, long HitCount, long BlockedCount,
    DateTime FirstSeenAt, DateTime LastSeenAt, string Description,
    long PartnerHitCount = 0, long PartnerBlockedCount = 0, bool NewlyGated = false);

/// <summary>โหมดที่มีผลจริง + เพราะอะไร (ตัวตัดสินเดียว <see cref="SubscriptionEnforcementResolver"/> — ชุดเดียวกับที่ middleware ใช้)</summary>
/// <param name="EffectiveMode">โหมดที่มีผลจริง (Off/Shadow/Enforce — ชื่อ)</param>
/// <param name="Source">AdminSwitch / AdminSwitchDefault / AdminSwitchUnreadable / EmergencyOverride</param>
/// <param name="AdminSwitchHasEffect">false = มี override ฉุกเฉินทับอยู่ — กดสวิตช์ได้ (บันทึก) แต่ยังไม่มีผลจนกว่าจะลบ override</param>
/// <param name="OverrideKey">ชื่อคีย์ config ของ override ฉุกเฉิน</param>
/// <param name="HeaderWriteGateMode">ด่านบริษัทถูกระงับ/หมดอายุของคำขอ partner ที่ส่ง X-Company-Id เอง (Enforce/LogOnly)</param>
/// <param name="LegacyHeaderEnforce">S200-1: config เดิม <c>Subscription:Enforcement:Mode = Enforce</c> ยังตั้งอยู่ ⇒ partner ยังถูกบังคับด่านเขียนแบบเดิม
/// (หน้าแอดมินแสดงกรอบเตือนแยก) — ลบคีย์เมื่อพร้อม</param>
/// <param name="LegacyKey">ชื่อคีย์ config เดิมที่เลิกใช้</param>
/// <param name="NewlyGatedRouteKeys">คีย์เส้นทางที่เพิ่งเริ่มมีผลรอบ 200 — โหมดเงาสำหรับทุกผู้เรียก (รวม partner) จนกว่าจะกดบังคับ (ข้อ 22)</param>
public record SubscriptionEffectiveModeDto(
    string EffectiveMode, string EffectiveModeLabel, string Source, string Explanation, bool AdminSwitchHasEffect,
    string? OverrideMode, string? OverrideRaw, string OverrideKey, string HeaderWriteGateMode, List<string> Warnings,
    bool LegacyHeaderEnforce = false, string? LegacyKey = null, List<string>? NewlyGatedRouteKeys = null);

/// <summary>สถานะสวิตช์ + โหมดที่มีผลจริง + รายงานเงา</summary>
/// <param name="Mode">ค่าสวิตช์แอดมินที่บันทึกไว้ (Off/Shadow/Enforce — ชื่อเสมอ) · ไม่ใช่โหมดที่มีผลจริงเสมอไป ⇒ ดู <paramref name="Effective"/></param>
/// <param name="CompaniesWouldBlock">จำนวนบริษัทที่มีอย่างน้อย 1 เหตุที่ "จะถูกบล็อก" (โหมดเงา)</param>
/// <param name="CompaniesBlocked">จำนวนบริษัทที่ถูกบล็อกจริงแล้ว (หลังเปิดบังคับ)</param>
/// <param name="TrialBlocks">ลูกค้าทดลองใช้ (จะ) ถูกบล็อกกี่ครั้งเพราะอะไร — เซิร์ฟเวอร์สรุปให้ หน้าเว็บแสดงอย่างเดียว</param>
/// <param name="RetentionDays">แถวที่ไม่ถูกพบซ้ำเกินกี่วันถูกตัดทิ้ง</param>
/// <param name="PrunedRows">จำนวนแถวที่ตัดทิ้งในการเปิดหน้านี้ครั้งนี้</param>
/// <param name="PartnerCompaniesWouldBlock">S200-3: บริษัทที่คำขอ partner/คีย์ API "จะถูกบล็อก" (แยกจากหน้าเว็บ)</param>
/// <param name="PartnerHitsWouldBlock">จำนวนคำขอ partner/คีย์ API ที่ "จะถูกบล็อก"</param>
/// <param name="PartnerHitsBlocked">จำนวนคำขอ partner/คีย์ API ที่ถูกบล็อกจริง</param>
public record SubscriptionEnforcementStatusDto(
    string Mode, SubscriptionEffectiveModeDto Effective, string Guidance,
    int CompaniesWouldBlock, long HitsWouldBlock, int CompaniesBlocked, long HitsBlocked,
    List<TrialBlockSummary> TrialBlocks, List<SubscriptionShadowHitDto> Hits, bool HitsTruncated,
    int RetentionDays, int PrunedRows,
    int PartnerCompaniesWouldBlock = 0, long PartnerHitsWouldBlock = 0, long PartnerHitsBlocked = 0,
    bool OwnerMaskEnforced = false, int OwnerMaskCompaniesWouldBlock = 0, long OwnerMaskHitsWouldBlock = 0);

/// <summary>รอบ 201 ทีม PL (C-3): สวิตช์ด่าน "เจ้าของปิดฟีเจอร์" ระดับ service · null = ไม่ได้ส่ง ⇒ 400 (ห้ามแปลงเป็น false เงียบ ๆ)</summary>
public record SetOwnerMaskEnforcementRequest(bool? Enforced);

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
        "คำขอจาก partner/integration ที่ส่ง X-Company-Id มาเองถูกตัดสินฟีเจอร์ด้วยคีย์เดิมอยู่แล้วตั้งแต่ก่อนรอบ 198 · " +
        "คีย์เส้นทางที่เพิ่งเริ่มมีผลรอบ 200 (รายการในกรอบด้านบน) และคำขอ /api/v1 เป็นเงาสำหรับทุกผู้เรียกจนกว่าจะกดบังคับ — " +
        "คอลัมน์ “partner/API” นับแยกจากหน้าเว็บ";

    /// <summary>โหมดที่มีผลจริง — ตัวตัดสินเดียวกับ middleware (<see cref="SubscriptionEnforcementResolver"/>) · อ่านสวิตช์สด (ไม่ผ่านแคช S200-8)</summary>
    private async Task<SubscriptionEnforcementState> EffectiveAsync(CancellationToken ct)
    {
        var adminSwitch = await _shadow.ReadAdminSwitchFreshAsync(ct);
        return SubscriptionEnforcementResolver.Resolve(adminSwitch, _config);
    }

    private static SubscriptionEffectiveModeDto ToDto(SubscriptionEnforcementState e) => new(
        e.EffectiveMode.ToString(), SubscriptionEnforcementResolver.Label(e.EffectiveMode), e.Source.ToString(), e.Explanation,
        e.AdminSwitchHasEffect, e.OverrideMode?.ToString(), e.OverrideRaw, SubscriptionEnforcementResolver.OverrideKey,
        e.HeaderWriteGateMode.ToString(), e.Warnings.ToList(),
        e.LegacyHeaderEnforce, SubscriptionEnforcementResolver.LegacyKey,
        SubscriptionGatePolicy.NewlyGatedRouteKeys.OrderBy(k => k, StringComparer.Ordinal).ToList());

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
                r.FirstSeenAt, r.LastSeenAt, text,
                r.PartnerHitCount, r.PartnerBlockedCount,
                r.RouteKey != null && SubscriptionGatePolicy.NewlyGatedRouteKeys.Contains(r.RouteKey));
        }).ToList();

        // PL-C1 (ฝ่ายค้านรอบ 201): ยอดสรุป "จะถูกปิด/ถูกปิดแล้ว" ของสวิตช์ <b>แพ็กเกจ</b> ห้ามรวมแถวของด่านเจ้าของปิดฟีเจอร์ (คนละสวิตช์ — นับแยกใน ownerMask ข้างล่าง)
        // เดิมนับรวม ⇒ แอดมินเห็นว่า "เปิดบังคับแพ็กเกจแล้วจะกระทบ N บริษัท" ทั้งที่ N บางส่วนเป็นเจ้าของที่ปิดฟีเจอร์เอง
        static bool IsPlanGate(SubscriptionShadowHitDto h) => h.Reason != nameof(SubscriptionGateReason.OwnerDisabledFeature);
        var wouldBlock = hits.Where(h => h.HitCount > 0 && IsPlanGate(h)).ToList();
        var blocked = hits.Where(h => h.BlockedCount > 0 && IsPlanGate(h)).ToList();
        var partnerWouldBlock = hits.Where(h => h.PartnerHitCount > 0).ToList();
        // C-3: แถวของด่าน "เจ้าของปิดฟีเจอร์" (service) นับแยก — หน้าเว็บแสดงคู่กับสวิตช์ของมันเอง
        var ownerMask = hits.Where(h => h.Reason == nameof(SubscriptionGateReason.OwnerDisabledFeature) && h.HitCount > 0).ToList();
        var ownerMaskEnforced = await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt)
            .Select(s => (bool?)s.OwnerFeatureMaskEnforced).FirstOrDefaultAsync(ct) ?? false;
        var trial = SubscriptionTrialReadiness.SummarizeTrialBlocks(page.Select(r => new SubscriptionShadowTally(
            r.CompanyId, r.Reason, r.Feature, r.Plan, r.SubscriptionStatus, r.HitCount, r.BlockedCount))).ToList();
        return Ok(new ApiResponse<SubscriptionEnforcementStatusDto>(true, new SubscriptionEnforcementStatusDto(
            enforcement.AdminSwitch.ToString(), ToDto(enforcement), Guidance,
            wouldBlock.Select(h => h.CompanyId).Distinct().Count(), wouldBlock.Sum(h => h.HitCount),
            blocked.Select(h => h.CompanyId).Distinct().Count(), blocked.Sum(h => h.BlockedCount),
            trial, hits, truncated, SubscriptionGatePolicy.ShadowRetentionDays, pruned,
            partnerWouldBlock.Select(h => h.CompanyId).Distinct().Count(), partnerWouldBlock.Sum(h => h.PartnerHitCount),
            hits.Sum(h => h.PartnerBlockedCount),
            ownerMaskEnforced, ownerMask.Select(h => h.CompanyId).Distinct().Count(), ownerMask.Sum(h => h.HitCount))));
    }

    /// <summary>รอบ 201 ทีม PL (C-3 · ข้อ 76): เปิด/ปิด "บังคับ" ด่านเจ้าของปิดฟีเจอร์ระดับ service (ค่าตั้งต้น = โหมดเงา) — แอดมินแพลตฟอร์มที่ล็อกอินเท่านั้น</summary>
    [HttpPut("owner-mask")]
    [Authorize(Roles = "SystemAdmin")]
    [Accounting.Filters.RejectApiKey("เปลี่ยนสวิตช์บังคับด่านเจ้าของปิดฟีเจอร์")]
    public async Task<ActionResult<ApiResponse<SubscriptionEnforcementStatusDto>>> SetOwnerMask(
        [FromBody] SetOwnerMaskEnforcementRequest request, CancellationToken ct = default)
    {
        if (request?.Enforced is not bool enforced)
            return BadRequest(new ApiResponse<SubscriptionEnforcementStatusDto>(false, null, "ต้องระบุ enforced เป็น true หรือ false"));
        var settings = await _db.SiteSettings.OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (settings == null)
        {
            settings = new SiteSettings();
            _db.SiteSettings.Add(settings);
        }
        var before = settings.OwnerFeatureMaskEnforced;
        settings.OwnerFeatureMaskEnforced = enforced;
        settings.UpdatedAt = DateTime.UtcNow;
        settings.UpdatedBy = JwtHelper.GetUserIdFromClaims(User).ToString();
        await _db.SaveChangesAsync(ct);   // AuditTrail จับ old/new ของ SiteSettings (hash chain)
        _logger.LogWarning("สวิตช์บังคับด่านเจ้าของปิดฟีเจอร์: {Before} → {After} โดยผู้ใช้ {UserId}", before, enforced, settings.UpdatedBy);
        return await Get(ct);
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
        // S200-8: เครื่องนี้มีผลทันที · เครื่องอื่นตามทันภายใน SubscriptionAdminSwitchCache.Ttl (5 วินาที)
        _shadow.InvalidateAdminSwitchCache();

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
