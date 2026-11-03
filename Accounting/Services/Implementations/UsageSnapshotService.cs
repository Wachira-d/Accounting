using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>1 หลอดของเดือนหนึ่งในหน้าประวัติ — ทุกช่องคิดที่เซิร์ฟเวอร์ (หน้าเว็บแสดงอย่างเดียว · F2 ข้อ 5) · <see cref="Known"/> false = ยังไม่มีข้อมูล (≠ 0)</summary>
public sealed record UsageHistoryBar(string Resource, string Label, string Unit, bool Known, long Used, long? Limit, bool Unlimited, string UsedText, string LimitText,
    int Percent, int WarnLevel, int Purchased, int OverageUnits, decimal OverageAmount, string? Note, string? BuyUrl,
    /// <summary>ฝ่ายค้าน S6: false = เพดานของเดือนนั้นไม่รู้ (แถว Backfill เติมจากตารางต้นทางซึ่งไม่มีเพดานย้อนหลัง) ⇒ limitText "—" ไม่ใช่ "ไม่จำกัด"</summary>
    bool LimitKnown = true);

public sealed record UsageHistoryInvoice(Guid DocumentId, string DocumentNumber, string StateLabel, decimal BalanceDue);

public sealed record UsageHistoryMonth(string Period, string Label, bool IsCurrent, IReadOnlyList<UsageHistoryBar> Bars, int AiCallsTotal, int AiCallsPaid,
    decimal OverageTotal, IReadOnlyList<UsageHistoryInvoice> Invoices, DateTime? SnapshotAtUtc);

public sealed record UsageHistoryResponse(IReadOnlyList<UsageHistoryMonth> Months, IReadOnlyList<object> Resources, int RetentionMonths);

/// <summary>
/// รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §D · §B.5 · คำตัดสินข้อ 231-5/231-6/231-7) — <b>ผู้เขียน snapshot การใช้งานรายเดือน + ผู้อ่านประวัติ ตัวเดียว</b>
/// <list type="bullet">
/// <item><see cref="WriteRolloverAsync"/> (static — จุดขึ้นเดือนใหม่ทั้ง 3: SubscriptionService.CheckUsageLimitAsync · OcrQuotaService.TryConsumeAsync/ResetMonthlyUsageAsync)
/// เขียนค่าปิดเดือนของตัวนับบน Subscription <b>ก่อน</b> <see cref="SubscriptionUsageRollover.RollIfDue"/> ล้าง — บนคอนเนกชัน/ธุรกรรมของผู้เรียก (ถอยพร้อมกัน) · INSERT … ON CONFLICT (ไม่ล้มเมื่อสองคำขอขึ้นเดือนพร้อมกัน)</item>
/// <item><see cref="WriteDailyAsync"/> (งาน <c>UsageSnapshotJob</c>) — ทุกทรัพยากรผ่านตัวตัดสินเดิม (<see cref="IQuotaGate.SummaryAsync"/>) · Capacity เก็บค่าสูงสุด · เตือนใกล้เต็มตามเกณฑ์ของแอดมิน ครั้งเดียวต่อ (ทรัพยากร·เกณฑ์·เดือน) ·
/// ตัวอย่างพื้นที่รายวันเมื่อเปิดส่วนเกินพื้นที่</item>
/// <item><see cref="HistoryAsync"/> — เดือนนี้ = สด · เดือนก่อน = snapshot (ไม่มี ⇒ เติมย้อนหลังจากตารางต้นทางครั้งเดียว Source=Backfill · ไม่ทับ Rollover) · ส่วนเกิน/ใบจาก UsageEvents · AI = ข้อมูลอย่างเดียว</item>
/// </list>
/// </summary>
public interface IUsageSnapshotService
{
    Task WriteDailyAsync(Guid companyId, CancellationToken ct = default);
    /// <summary>สิ้นเดือนไทย: ส่วนเกินพื้นที่ GB-เดือนของงวด <paramref name="period"/> จากตัวอย่างรายวัน ⇒ 1 UsageEvent (idempotent) · คืน true เมื่อบันทึกใหม่</summary>
    Task<bool> SettleStorageOverageAsync(Guid companyId, string period, CancellationToken ct = default);
    Task<UsageHistoryResponse> HistoryAsync(Guid companyId, int months, CancellationToken ct = default);
}

public class UsageSnapshotService : IUsageSnapshotService
{
    private readonly AccountingDbContext _db;
    private readonly IQuotaGate _gate;
    private readonly IOverageService _overage;
    private readonly INotificationEngine? _notify;
    private readonly ILogger<UsageSnapshotService> _logger;

    /// <summary>ตัวนับเอกสารของเดือนนี้ (ภาพของ Subscription ที่ผู้อ่านประวัติ/งานรายวันต้องใช้)</summary>
    private sealed record SubCounters(int CurrentMonthDocuments, DateTime UsageResetDate, int MaxDocumentsPerMonth, int DocumentBonusQuota, DateTime? DocumentBonusExpiresAt);

    private Task<SubCounters?> SubCountersAsync(Guid companyId, CancellationToken ct)
        => _db.Subscriptions.AsNoTracking().Where(s => s.CompanyId == companyId && !s.IsDeleted)
            .Select(s => new SubCounters(s.CurrentMonthDocuments, s.UsageResetDate, s.MaxDocumentsPerMonth, s.DocumentBonusQuota, s.DocumentBonusExpiresAt))
            .FirstOrDefaultAsync(ct);

    public UsageSnapshotService(AccountingDbContext db, IQuotaGate gate, IOverageService overage, ILogger<UsageSnapshotService> logger, INotificationEngine? notify = null)
    { _db = db; _gate = gate; _overage = overage; _logger = logger; _notify = notify; }

    // ───────── ผู้เขียน 1: ตอนขึ้นเดือนใหม่ ─────────

    private const string UpsertSql = """
        INSERT INTO "UsageMonthlySnapshots" ("Id","CompanyId","Period","ResourceKey","Used","Limit","Purchased","OverageUnits","OverageAmount","Source","SnapshotAtUtc","WarnedPercent","CreatedAt","CreatedBy","IsDeleted")
        VALUES (gen_random_uuid(), {0}, {1}, {2}, {3}, {4}, {5}, 0, 0, {6}, {7}, 0, {7}, {8}, false)
        ON CONFLICT ("CompanyId","Period","ResourceKey") DO UPDATE SET
            "Used" = CASE WHEN {9} THEN GREATEST("UsageMonthlySnapshots"."Used", EXCLUDED."Used") ELSE EXCLUDED."Used" END,
            "Limit" = EXCLUDED."Limit", "Purchased" = EXCLUDED."Purchased",
            "Source" = CASE WHEN "UsageMonthlySnapshots"."Source" = 1 THEN 1 ELSE EXCLUDED."Source" END,
            "SnapshotAtUtc" = EXCLUDED."SnapshotAtUtc", "UpdatedAt" = EXCLUDED."SnapshotAtUtc"
        WHERE "UsageMonthlySnapshots"."Source" <> 1 OR EXCLUDED."Source" = 1;
        """;

    /// <summary>upsert 1 แถว — Rollover (Source=1) ทับได้ทุกแหล่ง · Daily/Backfill ไม่ทับ Rollover · Capacity เก็บค่าสูงสุด</summary>
    public static Task UpsertAsync(AccountingDbContext db, Guid companyId, string period, string resourceKey, long used, long? limit, int purchased,
        UsageSnapshotSource source, DateTime nowUtc, string actor, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync(UpsertSql, new object[]
        {
            companyId, period, resourceKey, used, (object?)limit ?? DBNull.Value, purchased, (int)source, nowUtc, actor, UsageHistoryRule.IsCapacity(resourceKey),
        }, ct);

    /// <summary>ค่าปิดเดือนของตัวนับบน <paramref name="sub"/> (เอกสาร · OCR) สำหรับงวดที่กำลังจะปิด — เรียก<b>ก่อน</b> <c>RollIfDue</c> เสมอ · ไม่ถึงวันรีเซ็ต = ไม่เขียน</summary>
    public static async Task WriteRolloverAsync(AccountingDbContext db, Subscription sub, DateTime nowUtc, CancellationToken ct = default)
    {
        if (!SubscriptionUsageRollover.IsStale(sub.UsageResetDate, nowUtc)) return;
        var period = UsageHistoryRule.ClosingPeriod(sub.UsageResetDate);
        var docLimit = DocumentQuotaPolicy.EffectiveLimit(sub.MaxDocumentsPerMonth, sub.DocumentBonusQuota, sub.DocumentBonusExpiresAt, nowUtc, used: sub.CurrentMonthDocuments);
        var docUnlimited = docLimit <= 0 || docLimit >= UsageBarRule.UnlimitedCountAt;
        await UpsertAsync(db, sub.CompanyId, period, OverageResources.Documents, sub.CurrentMonthDocuments, UsageHistoryRule.StoredLimit(docLimit, docUnlimited),
            0, UsageSnapshotSource.Rollover, nowUtc, "system:rollover", ct);
        var ocrUnlimited = sub.MaxOcrPagesPerMonth < 0;
        await UpsertAsync(db, sub.CompanyId, period, QuotaResources.OcrPagesMonth, sub.CurrentMonthOcrPages,
            UsageHistoryRule.StoredLimit(ocrUnlimited ? null : sub.MaxOcrPagesPerMonth, ocrUnlimited), 0, UsageSnapshotSource.Rollover, nowUtc, "system:rollover", ct);
    }

    // ───────── ผู้เขียน 2: งานรายวัน ─────────

    public async Task WriteDailyAsync(Guid companyId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var period = UsageHistoryRule.PeriodOf(now);
        var settings = await PlatformBillingSettingsService.LoadPolicyAsync(_db, ct);
        var percents = settings.WarnPercents;
        var items = await _gate.SummaryAsync(companyId, ct);
        var sub = await SubCountersAsync(companyId, ct);

        foreach (var it in items)
        {
            if (it.PerSite || it.Source == "unknown") continue;
            var isStorage = it.Resource == QuotaResources.StorageGb;
            long used = isStorage ? (long)(it.Used ?? 0) * 1024L * 1024L : (long)(it.Used ?? 0);
            long? limit = null;
            if (!it.Unlimited && it.Limit is int lm) limit = isStorage ? lm * 1024L * 1024L : lm;
            await UpsertAsync(_db, companyId, period, it.Resource, used, limit, it.Purchased, UsageSnapshotSource.Daily, now, "system:daily", ct);
            if (UsageHistoryRule.DailyWarnApplies(it.Resource))
                await WarnIfDueAsync(companyId, period, it.Resource, it.Label, it.Unit, used, limit, it.UsedText, it.LimitText, it.BuyUrl, percents, ct);
        }
        if (sub != null)
        {
            var docsUsed = SubscriptionUsageRollover.Effective(sub.CurrentMonthDocuments, sub.UsageResetDate, now);
            var docLimit = DocumentQuotaPolicy.EffectiveLimit(sub.MaxDocumentsPerMonth, sub.DocumentBonusQuota, sub.DocumentBonusExpiresAt, now, used: docsUsed);
            var docUnlimited = docLimit <= 0 || docLimit >= UsageBarRule.UnlimitedCountAt;
            await UpsertAsync(_db, companyId, period, OverageResources.Documents, docsUsed, UsageHistoryRule.StoredLimit(docLimit, docUnlimited), 0, UsageSnapshotSource.Daily, now, "system:daily", ct);
        }
        var notify = await _db.NotifyUsageCounters.AsNoTracking().Where(x => x.CompanyId == companyId && x.Period == period)
            .Select(x => new { x.Used, x.BonusUsed }).FirstOrDefaultAsync(ct);
        if (notify != null)
            await UpsertAsync(_db, companyId, period, OverageResources.NotifyMessages, notify.Used + notify.BonusUsed, null, 0, UsageSnapshotSource.Daily, now, "system:daily", ct);

        // ตัวอย่างพื้นที่รายวัน — เก็บเฉพาะวันที่บริษัท "เปิดส่วนเกินพื้นที่อยู่ + ไม่ถูกล็อก + มีราคา" พร้อม snapshot ราคาของวันนั้น (ฝ่ายค้าน B2):
        // สิ้นเดือนคิดจากตัวอย่างเสมอ ไม่ถามนโยบาย ⇒ ปิดส่วนเกินวันที่ 30 ก็ยังโดนคิดวันที่เปิดอยู่ · วันที่ไม่ได้เปิด = ไม่มีตัวอย่าง = ไม่คิด
        var policy = await _overage.GetPolicyViewAsync(companyId, ct);
        if (policy.Mode == OverageMode.AllowOverage && !policy.IsLocked && OveragePolicy.Covers(policy, OverageResources.StorageGb)
            && await _overage.RateForAsync(companyId, OverageResources.StorageGb, ct) is { UnitPrice: > 0m } rate)
        {
            var st = items.FirstOrDefault(i => i.Resource == QuotaResources.StorageGb);
            if (st != null && st.Source != "unknown")
            {
                long usedBytes = (long)(st.Used ?? 0) * 1024L * 1024L;
                long? limitBytes = null;
                if (!st.Unlimited && st.Limit is int lm2) limitBytes = lm2 * 1024L * 1024L;
                await UpsertDailyStorageAsync(_db, companyId, PlatformInvoiceRules.ThaiDate(now), usedBytes, limitBytes, rate.UnitPrice, now, ct);
            }
        }
    }

    /// <summary>ตัวอย่างพื้นที่ 1 วัน (unique บริษัท·วัน·ทรัพยากร) + ราคา/GB-เดือนที่มีผลวันนั้น — ตัวเขียนเดียว (งานรายวัน · เทสต์)</summary>
    public static Task UpsertDailyStorageAsync(AccountingDbContext db, Guid companyId, DateTime thaiDay, long usedBytes, long? limitBytes, decimal unitPrice, DateTime nowUtc, CancellationToken ct = default)
        => db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "UsageDailySnapshots" ("Id","CompanyId","Date","ResourceKey","Used","Limit","UnitPrice","CreatedAt","CreatedBy","IsDeleted")
            VALUES (gen_random_uuid(), {0}, {1}, {2}, {3}, {4}, {5}, {6}, 'system:daily', false)
            ON CONFLICT ("CompanyId","Date","ResourceKey") DO UPDATE SET "Used" = EXCLUDED."Used", "Limit" = EXCLUDED."Limit", "UnitPrice" = EXCLUDED."UnitPrice", "UpdatedAt" = EXCLUDED."CreatedAt";
            """, new object[] { companyId, DateTime.SpecifyKind(thaiDay.Date, DateTimeKind.Utc), QuotaResources.StorageGb, usedBytes, (object?)limitBytes ?? DBNull.Value, unitPrice, nowUtc }, ct);

    /// <summary>แจ้งเจ้าของเมื่อข้ามเกณฑ์ที่สูงกว่าที่เคยแจ้งเดือนนี้ — ประทับ WarnedPercent ก่อนส่ง (UPDATE … WHERE WarnedPercent &lt; new ⇒ หลายเครื่องส่งได้ครั้งเดียว)</summary>
    private async Task WarnIfDueAsync(Guid companyId, string period, string resource, string label, string unit, long used, long? limit, string usedText, string limitText,
        string? buyUrl, IReadOnlyList<int> percents, CancellationToken ct)
    {
        var warned = await _db.UsageMonthlySnapshots.AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.Period == period && s.ResourceKey == resource).Select(s => (int?)s.WarnedPercent).FirstOrDefaultAsync(ct) ?? 0;
        var due = QuotaWarnPolicy.DueAfterWarned(used, limit, warned, percents);
        if (due <= 0) return;
        var stamped = await _db.Database.ExecuteSqlRawAsync("""
            UPDATE "UsageMonthlySnapshots" SET "WarnedPercent" = {3} WHERE "CompanyId" = {0} AND "Period" = {1} AND "ResourceKey" = {2} AND "WarnedPercent" < {3};
            """, new object[] { companyId, period, resource, due }, ct);
        if (stamped != 1 || _notify == null) return;
        try
        {
            // ผู้รับ = เจ้าของ + ผู้มีสิทธิ์ BillingManage (§B.6 · ตัวเดียว OverageService.BillingRecipientsAsync)
            var text = resource == QuotaResources.StorageGb ? $"{usedText}/{limitText}" : $"{used:N0}/{limitText} {unit}";
            foreach (var uid in await OverageService.BillingRecipientsAsync(_db, companyId, ct))
                await _notify.DispatchAsync(companyId, NotificationEvents.QuotaResourceLevel, new NotificationContext
                {
                    Title = QuotaWarnPolicy.Title(label, due),
                    Message = due >= 100
                        ? $"ใช้{label}ครบเพดานแพ็กเกจแล้ว ({text}) — ซื้อเพิ่มหรืออัปเกรดที่หน้า \"แพ็กเกจและการชำระเงิน\" · ของเดิมยังใช้ได้ตามปกติ"
                        : $"ใช้{label}ไปแล้ว {text} (≥ {due}%) — ซื้อเพิ่มหรืออัปเกรดก่อนเต็มที่หน้า \"แพ็กเกจและการชำระเงิน\"",
                    ActionUrl = buyUrl ?? QuotaResources.UsageUrl, EntityType = "Subscription", BellType = NotificationType.System, RecipientUserId = uid,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งเตือนโควตา {Resource} ({Percent}%) ของบริษัท {Company} ไม่สำเร็จ", resource, due, companyId);
        }
    }

    public Task<bool> SettleStorageOverageAsync(Guid companyId, string period, CancellationToken ct = default)
        => SettleStorageOverageAsync(_db, _overage, companyId, period, ct);

    /// <summary>ฝ่ายค้าน B2/S4/S5: ปิดส่วนเกินพื้นที่ของงวดจาก<b>ตัวอย่างรายวันเท่านั้น</b> — ไม่ถาม Mode/Locked/ราคาปัจจุบัน (ตัวอย่างมีเฉพาะวันที่เปิดอยู่ · ราคา snapshot ต่อวัน) ·
    /// หน่วย = ceil(Σ GB เกิน / วันในเดือน) · ราคา = ของตัวอย่างวันสุดท้าย · เพดานเงินตรวจกับยอดของงวดนั้น (ถึงเพดาน ⇒ บันทึกราคา 0 ให้เห็น ไม่คิด) · OccurredAt = วินาทีสุดท้ายของงวด · idempotent ต่อ (บริษัท, งวด)</summary>
    public static async Task<bool> SettleStorageOverageAsync(AccountingDbContext db, IOverageService overage, Guid companyId, string period, CancellationToken ct = default)
    {
        var (start, end) = UsageHistoryRule.PeriodRangeUtc(period);
        var samples = await db.UsageDailySnapshots.AsNoTracking()
            .Where(d => d.CompanyId == companyId && d.ResourceKey == QuotaResources.StorageGb && d.Date >= start.AddHours(7) && d.Date < end.AddHours(7))
            .OrderBy(d => d.Date).Select(d => new { d.Used, d.Limit, d.UnitPrice }).ToListAsync(ct);
        if (samples.Count == 0) return false;
        UsageHistoryRule.TryParsePeriod(period, out var y, out var m);
        var units = OveragePolicy.StorageOverageGbMonth(samples.Select(s => (s.Used, s.Limit)).ToList(), DateTime.DaysInMonth(y, m));
        if (units <= 0) return false;
        var key = $"storage-overage:{period}";
        if (await db.UsageEvents.AsNoTracking().AnyAsync(u => u.CompanyId == companyId && u.IdempotencyKey == key && !u.IsDeleted, ct)) return false;
        var price = samples.Where(s => s.UnitPrice is > 0m).Select(s => s.UnitPrice!.Value).LastOrDefault();
        var amount = Math.Round(units * price, 2, MidpointRounding.AwayFromZero);
        var cap = (await overage.GetPolicyViewAsync(companyId, ct)).MonthlyCapAmount;
        var chargeable = price > 0m && (cap is not decimal c || await overage.SpentInPeriodAsync(companyId, period, ct) + amount <= c);
        var r = await overage.RecordAsync(companyId, OverageResources.StorageGb, units, key, "UsageDailySnapshot", null,
            chargeable: chargeable, unitPriceSnapshot: chargeable ? price : 0m, ct, occurredAtUtc: end.AddSeconds(-1));
        return r.Recorded;
    }

    // ───────── ผู้อ่าน: ประวัติ ─────────

    public async Task<UsageHistoryResponse> HistoryAsync(Guid companyId, int months, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var periods = UsageHistoryRule.Periods(now, months <= 0 ? UsageHistoryRule.DefaultHistoryMonths : months);
        var current = periods[0];
        var oldestStart = UsageHistoryRule.PeriodRangeUtc(periods[^1]).StartUtc;

        var snaps = await _db.UsageMonthlySnapshots.AsNoTracking().Where(s => s.CompanyId == companyId && periods.Contains(s.Period)).ToListAsync(ct);
        var overageEvents = await _db.UsageEvents.AsNoTracking()
            .Where(u => u.CompanyId == companyId && !u.IsDeleted && !u.IsSandbox && u.OccurredAt >= oldestStart
                && (u.FeatureCode.StartsWith(OverageResources.MeterPrefix) || u.FeatureCode == AddOnCodes.DocumentsOverage))
            .Select(u => new { u.FeatureCode, u.OccurredAt, u.Quantity, u.ChargedAmount, u.BilledDocumentId }).ToListAsync(ct);
        var invoiceIds = overageEvents.Where(e => e.BilledDocumentId != null).Select(e => e.BilledDocumentId!.Value).Distinct().ToList();
        var invoices = invoiceIds.Count == 0 ? new List<Document>() : await _db.Documents.AsNoTracking().Where(d => invoiceIds.Contains(d.Id)).ToListAsync(ct);
        var notifyCounters = await _db.NotifyUsageCounters.AsNoTracking().Where(x => x.CompanyId == companyId && periods.Contains(x.Period)).ToListAsync(ct);
        var ai = await _db.AiUsageDailyTenants.AsNoTracking().Where(a => a.CompanyId == companyId && !a.IsSandbox && a.UsageDate >= oldestStart.AddHours(7).Date)
            .Select(a => new { a.UsageDate, a.CallsTotal, a.CallsAi }).ToListAsync(ct);

        List<QuotaSummaryItem>? live = null;
        try { live = await _gate.SummaryAsync(companyId, ct); } catch (Exception ex) { _logger.LogWarning(ex, "อ่านสรุปโควตาสดไม่ได้ (company {Company})", companyId); }
        var sub = await SubCountersAsync(companyId, ct);

        var monthsOut = new List<UsageHistoryMonth>();
        foreach (var period in periods)
        {
            var isCurrent = period == current;
            var bars = new List<UsageHistoryBar>();
            var pEvents = overageEvents.Where(e => UsageHistoryRule.PeriodOf(e.OccurredAt) == period).ToList();
            DateTime? snapAt = null;
            foreach (var (key, label, unit) in UsageHistoryRule.DisplayResources)
            {
                var ov = pEvents.Where(e => OverageResources.FromFeatureCode(e.FeatureCode)?.Key == key).ToList();
                var ovUnits = ov.Sum(e => e.Quantity); var ovAmount = ov.Sum(e => e.ChargedAmount);
                var buyUrl = OverageResources.IsKnown(key) ? OverageResources.BuyUrl(key) : QuotaResources.IsKnown(key) ? QuotaResources.BuyUrl(key) : QuotaResources.UsageUrl;
                if (isCurrent)
                {
                    bars.Add(LiveBar(key, label, unit, live, sub, notifyCounters.FirstOrDefault(n => n.Period == period), now, ovUnits, ovAmount, buyUrl));
                    continue;
                }
                var snap = snaps.FirstOrDefault(s => s.Period == period && s.ResourceKey == key);
                if (snap == null && key == OverageResources.NotifyMessages && notifyCounters.FirstOrDefault(n => n.Period == period) is { } nc)
                {
                    bars.Add(Bar(key, label, unit, true, nc.Used + nc.BonusUsed, null, 0, ovUnits, ovAmount, null, buyUrl));
                    continue;
                }
                if (snap == null && (key == OverageResources.Documents || key == QuotaResources.OcrPagesMonth))
                {
                    snap = await BackfillAsync(companyId, period, key, now, ct);
                }
                if (snap == null) { bars.Add(Bar(key, label, unit, false, 0, null, 0, ovUnits, ovAmount, "ยังไม่มีข้อมูลของเดือนนี้", buyUrl)); continue; }
                snapAt = snapAt is DateTime a && a > snap.SnapshotAtUtc ? a : snap.SnapshotAtUtc;
                // ฝ่ายค้าน S6: แถว Backfill ไม่รู้เพดานย้อนหลัง ⇒ LimitKnown=false (limitText "—" · ไม่ใช่ "ไม่จำกัด")
                bars.Add(Bar(key, label, unit, true, snap.Used, snap.Limit, snap.Purchased, ovUnits, ovAmount,
                    snap.Source == UsageSnapshotSource.Backfill ? "เติมย้อนหลังจากข้อมูลต้นทาง (ไม่ทราบเพดานของเดือนนั้น)" : null, buyUrl,
                    limitKnown: snap.Source != UsageSnapshotSource.Backfill));
            }
            var pAi = ai.Where(a => UsageHistoryRule.PeriodOf(a.UsageDate.AddHours(-7)) == period).ToList();
            var pInvoices = pEvents.Where(e => e.BilledDocumentId != null).Select(e => e.BilledDocumentId!.Value).Distinct()
                .Select(id => invoices.FirstOrDefault(d => d.Id == id)).Where(d => d != null)
                .Select(d => new UsageHistoryInvoice(d!.Id, d.DocumentNumber, PlatformInvoiceRules.StateLabel(
                    PlatformInvoiceRules.StateOf(d.Status, d.BalanceDue, d.DueDate, now), PlatformInvoiceRules.DaysPastDue(d.DueDate, now)), d.BalanceDue)).ToList();
            monthsOut.Add(new UsageHistoryMonth(period, UsageHistoryRule.Label(period), isCurrent, bars, pAi.Sum(a => a.CallsTotal), pAi.Sum(a => a.CallsAi),
                pEvents.Sum(e => e.ChargedAmount), pInvoices, isCurrent ? now : snapAt));
        }
        var resources = UsageHistoryRule.DisplayResources.Select(r => (object)new { key = r.Key, label = r.Label, unit = r.Unit }).ToList();
        resources.Add(new { key = UsageHistoryRule.AiKey, label = "การเรียก AI (ข้อมูลอย่างเดียว · ไม่มีเพดาน · ไม่คิดเงิน)", unit = "ครั้ง" });
        return new UsageHistoryResponse(monthsOut, resources, UsageHistoryRule.RetentionMonths);
    }

    /// <summary>เติมย้อนหลังครั้งเดียวจากตารางต้นทาง (วิธีเดียวกับ AdminCompanyUsageController: เอกสารที่ไม่ใช่ร่าง/ปฏิเสธ · สแกน OCR) · Source=Backfill · ไม่ทับ Rollover</summary>
    private async Task<UsageMonthlySnapshot?> BackfillAsync(Guid companyId, string period, string key, DateTime now, CancellationToken ct)
    {
        var (start, end) = UsageHistoryRule.PeriodRangeUtc(period);
        long used;
        try
        {
            used = key == OverageResources.Documents
                ? await _db.Documents.AsNoTracking().CountAsync(d => d.CompanyId == companyId && !d.IsDeleted && d.CreatedAt >= start && d.CreatedAt < end
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.Rejected, ct)
                : await _db.OcrScanResults.AsNoTracking().CountAsync(o => o.CompanyId == companyId && !o.IsDeleted && o.CreatedAt >= start && o.CreatedAt < end, ct);
            await UpsertAsync(_db, companyId, period, key, used, null, 0, UsageSnapshotSource.Backfill, now, "system:backfill", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "เติม snapshot ย้อนหลัง {Resource} งวด {Period} ของบริษัท {Company} ไม่สำเร็จ", key, period, companyId);
            return null;
        }
        return await _db.UsageMonthlySnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId && s.Period == period && s.ResourceKey == key, ct);
    }

    private static UsageHistoryBar LiveBar(string key, string label, string unit, List<QuotaSummaryItem>? live, SubCounters? sub, NotifyUsageCounter? notify, DateTime now,
        int ovUnits, decimal ovAmount, string? buyUrl)
    {
        if (key == OverageResources.Documents)
        {
            if (sub == null) return Bar(key, label, unit, false, 0, null, 0, ovUnits, ovAmount, "ยังไม่มีแพ็กเกจ", buyUrl);
            var used = SubscriptionUsageRollover.Effective(sub.CurrentMonthDocuments, sub.UsageResetDate, now);
            var lim = DocumentQuotaPolicy.EffectiveLimit(sub.MaxDocumentsPerMonth, sub.DocumentBonusQuota, sub.DocumentBonusExpiresAt, now, used: used);
            var unlimited = lim <= 0 || lim >= UsageBarRule.UnlimitedCountAt;
            return Bar(key, label, unit, true, used, unlimited ? null : DocumentQuotaPolicy.DisplayLimit(used, lim, ovUnits), 0, ovUnits, ovAmount, null, buyUrl);
        }
        if (key == OverageResources.NotifyMessages)
            return Bar(key, label, unit, true, notify == null ? 0 : notify.Used + notify.BonusUsed, null, 0, ovUnits, ovAmount, "เพดานดูที่การ์ดข้อความช่องกลาง", buyUrl);
        var it = live?.FirstOrDefault(i => i.Resource == key);
        if (it == null || it.Source == "unknown") return Bar(key, label, unit, false, 0, null, 0, ovUnits, ovAmount, "โหลดไม่ได้", buyUrl);
        long? liveLimit = !it.Unlimited && it.Limit is int lm ? (key == QuotaResources.StorageGb ? lm * 1024L * 1024L : lm) : null;
        long liveUsed = key == QuotaResources.StorageGb ? (long)(it.Used ?? 0) * 1024L * 1024L : (long)(it.Used ?? 0);
        return Bar(key, label, unit, true, liveUsed, liveLimit, it.Purchased, ovUnits, ovAmount, it.Note, buyUrl);
    }

    private static UsageHistoryBar Bar(string key, string label, string unit, bool known, long used, long? limit, int purchased, int ovUnits, decimal ovAmount, string? note, string? buyUrl,
        bool limitKnown = true)
    {
        var unlimited = known && limitKnown && limit == null;
        var isBytes = key == QuotaResources.StorageGb;
        var usedText = !known ? "—" : isBytes ? BytesText(used) : used.ToString("N0");
        var limitText = !known || !limitKnown ? "—" : unlimited ? "ไม่จำกัด" : isBytes ? BytesText(limit!.Value) : limit!.Value.ToString("N0");
        var pct = known && limitKnown ? QuotaWarnPolicy.PercentUsed(used, limit) : 0;
        return new UsageHistoryBar(key, label, unit, known, used, limit, unlimited, usedText, limitText, Math.Min(100, pct),
            known && limitKnown ? QuotaWarnPolicy.Severity(used, limit) : 0, purchased, ovUnits, ovAmount, note, buyUrl, LimitKnown: limitKnown);
    }

    private static string BytesText(long b)
    {
        if (b >= QuotaResources.BytesPerGb) return $"{b / (double)QuotaResources.BytesPerGb:N2} GB";
        if (b >= 1024 * 1024) return $"{b / (1024.0 * 1024):N1} MB";
        return $"{b / 1024.0:N0} KB";
    }
}
