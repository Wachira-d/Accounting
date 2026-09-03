using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// โควตาเอกสาร — สถานะ · ซื้อเพิ่ม · แลกจากภารกิจ (LODGING_LICENSING_PLAN §11-§12)
///
/// **ทางออกต้องมีเสมอ** คือหลักที่คุมทั้งไฟล์นี้: ผู้ใช้ที่ชนเพดานต้องมีอย่างน้อย
/// หนึ่งทางไปต่อ (ซื้อเพิ่ม · อัปเกรด · แลกจากภารกิจ ถ้าเจ้าของระบบเปิด) — ห้าม
/// ปล่อยให้ตัน. ส่วนเอกสารที่กฎหมายบังคับให้ออกนั้นออกได้เสมออยู่แล้วโดยไม่ต้อง
/// พึ่งหน้านี้ (ดู <see cref="DocumentQuotaPolicy"/>) — หน้านี้มีไว้ให้ "ทำงานต่อ
/// ได้เต็มรูปแบบ" ไม่ใช่ประตูที่ต้องผ่านก่อนถึงจะออกใบกำกับได้
/// </summary>
public class QuotaService : IQuotaService
{
    private readonly AccountingDbContext _db;
    private readonly ISubscriptionService _subscriptions;
    private readonly IUsageMeteringService _metering;
    private readonly ILogger<QuotaService> _logger;

    public QuotaService(AccountingDbContext db, ISubscriptionService subscriptions,
        IUsageMeteringService metering, ILogger<QuotaService> logger)
    { _db = db; _subscriptions = subscriptions; _metering = metering; _logger = logger; }

    // ───────────────────────── สถานะ ─────────────────────────

    public async Task<DocumentQuotaStatus> GetDocumentQuotaAsync(Guid companyId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var eff = await _subscriptions.GetEffectivePlanAsync(companyId);
        var sub = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new { s.CurrentMonthDocuments, s.DocumentBonusQuota, s.DocumentBonusExpiresAt })
            .FirstOrDefaultAsync(ct);

        var used = sub?.CurrentMonthDocuments ?? 0;
        var planLimit = eff?.MaxDocumentsPerMonth ?? 0;
        var bonusAlive = (sub?.DocumentBonusQuota ?? 0) > 0
            && (sub?.DocumentBonusExpiresAt == null || sub.DocumentBonusExpiresAt > now);
        var bonus = bonusAlive ? sub!.DocumentBonusQuota : 0;
        var limit = DocumentQuotaPolicy.EffectiveLimit(planLimit, bonus, sub?.DocumentBonusExpiresAt, now);

        var th = now.AddHours(7);
        var warn = DocumentQuotaPolicy.WarnLevel(used, limit);
        var forecast = DocumentQuotaPolicy.ForecastExhaustionDay(
            used, limit, th.Day, DateTime.DaysInMonth(th.Year, th.Month));

        var overagePlan = await _metering.ResolveEffectivePlanAsync(companyId, AddOnCodes.DocumentsOverage, ct);
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var overageRows = await _db.UsageEvents.AsNoTracking()
            .Where(u => u.CompanyId == companyId && u.FeatureCode == AddOnCodes.DocumentsOverage
                     && u.OccurredAt >= monthStart && !u.IsDeleted)
            .Select(u => new { u.Quantity, u.ChargedAmount })
            .ToListAsync(ct);

        // "auto-overage" ไม่ใช่สวิตช์ที่ปิดได้ — เอกสารที่กฎหมายบังคับต้องออกให้ได้
        // เสมอ ⇒ สิ่งที่ตอบได้จริงคือ "ตอนนี้มีราคาส่วนเกินตั้งไว้ไหม" เท่านั้น
        // (ห้ามแต่งธงที่ผู้ใช้กดแล้วไม่มีผลจริง — defect class "silent no-op")
        var overagePrice = overagePlan?.UnitPrice ?? 0m;

        string? msg = warn switch
        {
            2 when overagePrice > 0 => $"ใช้โควตาเอกสารครบแล้ว ({used}/{limit} ฉบับ) — "
                + $"เอกสารที่กฎหมายบังคับยังออกได้ตามปกติ โดยคิดค่าส่วนเกินฉบับละ {overagePrice:N2} บาท",
            2 => $"ใช้โควตาเอกสารครบแล้ว ({used}/{limit} ฉบับ) — เอกสารที่กฎหมายบังคับยังออกได้ตามปกติ",
            1 when forecast != null => $"ใช้ไปแล้ว {used}/{limit} ฉบับ — คาดว่าจะครบราววันที่ {forecast} ของเดือนนี้",
            1 => $"ใช้ไปแล้ว {used}/{limit} ฉบับ",
            _ => null,
        };

        return new DocumentQuotaStatus(
            Used: used, PlanLimit: planLimit, BonusQuota: bonus,
            BonusExpiresAt: bonus > 0 ? sub?.DocumentBonusExpiresAt : null,
            EffectiveLimit: limit, Remaining: Math.Max(0, limit - used),
            WarnLevel: warn, ForecastExhaustionDay: forecast,
            OverageEnabled: overagePrice > 0, OverageUnitPrice: overagePrice,
            OverageThisMonth: overageRows.Sum(r => r.Quantity),
            OverageChargedThisMonth: overageRows.Sum(r => r.ChargedAmount),
            Message: msg);
    }

    // ───────────────────────── ซื้อเพิ่ม ─────────────────────────

    public async Task<DocumentQuotaStatus> PurchaseTopUpAsync(
        Guid companyId, int packs, string actor, CancellationToken ct = default)
    {
        if (packs <= 0) throw new BusinessRuleException("จำนวนแพ็กต้องมากกว่า 0", "QUOTA-TOPUP");
        if (packs > 50) throw new BusinessRuleException("ซื้อได้ครั้งละไม่เกิน 50 แพ็ก", "QUOTA-TOPUP");

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted, ct);
        if (sub == null) throw new BusinessRuleException("บริษัทนี้ยังไม่มีแพ็กเกจ", "QUOTA-TOPUP");

        // ล็อกต่อบริษัท — ลำดับที่ของการซื้อในงวดคือส่วนหนึ่งของคีย์กันซ้ำ
        // ถ้าสองคำขอนับลำดับพร้อมกันจะได้เลขเดียวกัน แล้วคำขอที่สองถูกตีเป็น
        // "กดซ้ำ" ทั้งที่ลูกค้าตั้งใจซื้อสองครั้งจริง
        //
        // `pg_advisory_xact_lock` ปล่อยล็อกตอนจบ **ธุรกรรม** ⇒ ถ้าไม่เปิดธุรกรรมเอง
        // ล็อกจะหลุดทันทีที่คำสั่งนี้จบ = ไม่กันอะไรเลย (บทเรียนคีย์ล็อกที่สุ่ม
        // ต่อ process — ล็อกที่ "อ่านแล้วเหมือนกันแล้ว" แต่ไม่กันจริง)
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.QuotaGrant, "topup") }, ct);

        var period = AddOnBilling.PeriodOf(DateTime.UtcNow);
        var monthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var seq = await _db.UsageEvents.AsNoTracking()
            .CountAsync(u => u.CompanyId == companyId && u.FeatureCode == AddOnCodes.DocumentsTopUp
                          && u.OccurredAt >= monthStart && !u.IsDeleted, ct) + 1;

        // คิดเงินก่อนให้ของ — ถ้าบันทึกมิเตอร์ไม่สำเร็จต้องไม่แจกโควตาฟรี
        // (ต่างจากมิเตอร์ทั่วไปที่ "ล้มเหลวแล้วปล่อยผ่าน" เพราะที่นั่นงานหลักคือ
        // งานของลูกค้า ส่วนที่นี่งานหลัก**คือ**การซื้อ)
        var res = await _metering.RecordAsync(new UsageRecordRequest(
            CompanyId: companyId,
            FeatureCode: AddOnCodes.DocumentsTopUp,
            Quantity: packs,
            IdempotencyKey: AddOnBilling.TopUpKey(AddOnCodes.DocumentsTopUp, period, seq),
            RefEntityType: "Subscription",
            RefEntityId: sub.Id), ct);

        if (!res.Recorded)
            throw new BusinessRuleException(
                res.Duplicate
                    ? "เพิ่งซื้อโควตาไปเมื่อครู่ — กรุณารอสักครู่แล้วลองใหม่ (ระบบกันการกดซ้ำ)"
                    : $"ซื้อโควตาไม่สำเร็จ: {res.Reason ?? "ไม่ทราบสาเหตุ"}",
                "QUOTA-TOPUP");

        var granted = packs * AddOnCodes.DocumentsPerTopUpPack;
        var expires = DateTime.UtcNow.AddDays(AddOnCodes.TopUpValidDays);
        sub.DocumentBonusQuota += granted;
        // อายุยาวที่สุดชนะ — โบนัสที่ซื้อทีหลังต้องไม่ทำให้ของเดิมหมดอายุเร็วขึ้น
        if (sub.DocumentBonusExpiresAt == null || sub.DocumentBonusExpiresAt < expires)
            sub.DocumentBonusExpiresAt = expires;

        _db.AuditLogs.Add(new AuditLog
        {
            CompanyId = companyId,
            Action = AuditAction.Create,
            EntityType = "Subscription",
            EntityId = sub.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new
            {
                action = "DocumentTopUp", packs, granted,
                charged = res.ChargedAmount, usageEventId = res.UsageEventId,
                expiresAt = expires, by = actor,
            }),
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _logger.LogInformation("บริษัท {Cid} ซื้อโควตาเอกสาร {Packs} แพ็ก ({Docs} ฉบับ) เป็นเงิน {Amt}",
            companyId, packs, granted, res.ChargedAmount);

        return await GetDocumentQuotaAsync(companyId, ct);
    }

    // ───────────────────────── ภารกิจแลกโควตา ─────────────────────────

    /// <summary>สถานะสามชั้นของบริษัทนี้ + จำนวนที่ใช้ไปแล้ว (ใช้ร่วมกันทั้ง
    /// ฝั่งแสดงรายการและฝั่งรับคำขอ — ห้ามคำนวณแยก ไม่งั้นจอบอกได้ เซิร์ฟเวอร์ปฏิเสธ)</summary>
    private async Task<(bool PlanAllows, bool CompanyBlocked, List<QuotaRewardGrant> Recent)>
        LoadRewardContextAsync(Guid companyId, CancellationToken ct)
    {
        var sub = await _db.Subscriptions.AsNoTracking()
            .Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new { s.Plan, s.QuotaRewardBlocked })
            .FirstOrDefaultAsync(ct);

        var planAllows = sub != null && await _db.PlanTemplates.AsNoTracking()
            .Where(p => p.Plan == sub.Plan && p.IsActive)
            .Select(p => p.AllowQuotaReward)
            .FirstOrDefaultAsync(ct);

        var since = DateTime.UtcNow.AddHours(7).Date.AddDays(-31).AddHours(-7);
        var recent = await _db.QuotaRewardGrants.AsNoTracking()
            .Where(g => g.CompanyId == companyId && !g.IsDeleted && g.GrantedAt >= since)
            .ToListAsync(ct);

        return (planAllows, sub?.QuotaRewardBlocked ?? false, recent);
    }

    private static (int Today, int Month) CountUsage(List<QuotaRewardGrant> grants, Guid optionId, DateTime nowUtc)
    {
        var th = nowUtc.AddHours(7);
        var dayStart = th.Date.AddHours(-7);
        var monthStart = new DateTime(th.Year, th.Month, 1).AddHours(-7);
        var mine = grants.Where(g => g.OptionId == optionId).ToList();
        return (mine.Count(g => g.GrantedAt >= dayStart), mine.Count(g => g.GrantedAt >= monthStart));
    }

    public async Task<List<QuotaRewardOptionDto>> ListRewardOptionsAsync(Guid companyId, CancellationToken ct = default)
    {
        var options = await _db.QuotaRewardOptions.AsNoTracking()
            .Where(o => o.IsActive && !o.IsDeleted)
            .OrderBy(o => o.SortOrder).ThenBy(o => o.Title)
            .ToListAsync(ct);

        var (planAllows, blocked, recent) = await LoadRewardContextAsync(companyId, ct);
        var now = DateTime.UtcNow;

        return options.Select(o =>
        {
            var (today, month) = CountUsage(recent, o.Id, now);
            var reason = QuotaRewardPolicy.Evaluate(
                anyActiveOption: true, planAllows: planAllows, companyBlocked: blocked,
                usedToday: today, maxPerDay: o.MaxPerDay,
                usedThisMonth: month, maxPerMonth: o.MaxPerMonth,
                watchedSeconds: -1, requiredSeconds: o.DurationSeconds);
            return new QuotaRewardOptionDto(
                o.Id, o.Kind.ToString(), o.Title, o.Description, o.ImageUrl, o.MediaUrl, o.PartnerUrl,
                o.DurationSeconds, o.RewardDocuments,
                today, o.MaxPerDay, month, o.MaxPerMonth,
                reason == QuotaRewardBlockReason.None,
                reason == QuotaRewardBlockReason.None
                    ? null : QuotaRewardPolicy.Message(reason, o.MaxPerDay, o.MaxPerMonth, o.DurationSeconds));
        }).ToList();
    }

    public async Task<QuotaRewardClaimResult> ClaimRewardAsync(Guid companyId, Guid optionId,
        int watchedSeconds, bool clickedThrough, Guid? userId, CancellationToken ct = default)
    {
        var option = await _db.QuotaRewardOptions.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == optionId && !o.IsDeleted, ct);
        if (option == null)
            return new QuotaRewardClaimResult(false, 0, null, 0, "ไม่พบภารกิจนี้");

        // ล็อกต่อบริษัท: สองแท็บกด "รับโควตา" พร้อมกันต้องได้ครั้งเดียว
        // (เพดานต่อวัน/เดือนเป็นด่านเดียวที่กันการดูรัว ๆ — ถ้าแข่งกันผ่านได้
        // ก็เท่ากับไม่มีเพดาน) — ต้องอยู่ในธุรกรรม ไม่งั้นล็อกหลุดทันที
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.QuotaGrant, "reward") }, ct);

        var (planAllows, blocked, recent) = await LoadRewardContextAsync(companyId, ct);
        var now = DateTime.UtcNow;
        var (today, month) = CountUsage(recent, option.Id, now);

        var reason = QuotaRewardPolicy.Evaluate(
            anyActiveOption: option.IsActive, planAllows: planAllows, companyBlocked: blocked,
            usedToday: today, maxPerDay: option.MaxPerDay,
            usedThisMonth: month, maxPerMonth: option.MaxPerMonth,
            watchedSeconds: Math.Max(0, watchedSeconds), requiredSeconds: option.DurationSeconds);
        if (reason != QuotaRewardBlockReason.None)
            return new QuotaRewardClaimResult(false, 0, null, 0,
                QuotaRewardPolicy.Message(reason, option.MaxPerDay, option.MaxPerMonth, option.DurationSeconds));

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.CompanyId == companyId && !s.IsDeleted, ct);
        if (sub == null)
            return new QuotaRewardClaimResult(false, 0, null, 0, "บริษัทนี้ยังไม่มีแพ็กเกจ");

        var expires = QuotaRewardPolicy.ExpiryOf(option.RewardValidDays, now);
        sub.DocumentBonusQuota += option.RewardDocuments;
        if (sub.DocumentBonusExpiresAt == null || sub.DocumentBonusExpiresAt < expires)
            sub.DocumentBonusExpiresAt = expires;

        _db.QuotaRewardGrants.Add(new QuotaRewardGrant
        {
            CompanyId = companyId,
            OptionId = option.Id,
            UserId = userId,
            Kind = option.Kind,
            GrantedDocuments = option.RewardDocuments,
            GrantedAt = now,
            ExpiresAt = expires,
            ClickedThrough = clickedThrough,
            WatchedSeconds = Math.Max(0, watchedSeconds),
        });

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new QuotaRewardClaimResult(true, option.RewardDocuments, expires, sub.DocumentBonusQuota,
            $"รับโควตาเพิ่ม {option.RewardDocuments} ฉบับแล้ว (ใช้ได้ถึง {expires.AddHours(7):dd/MM/yyyy})");
    }
}
