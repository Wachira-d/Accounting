using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Accounting.Services.Implementations;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §B.5/§D.1 · คำตัดสินข้อ 231-5/231-6) — <b>snapshot การใช้งานรายวัน + เตือนใกล้เต็มทุกทรัพยากร + ส่วนเกินพื้นที่ GB-เดือน</b>
/// <para>กฎ #4 D: ทีละเครื่อง (<see cref="JobLock"/>) · <b>watermark ต่อบริษัท</b> = <c>UsageMonthlySnapshot.SnapshotAtUtc</c> ของเดือนนี้ (เขียนแล้ววันนี้ = ข้าม) ·
/// รอบละไม่เกิน <see cref="BatchSize"/> บริษัท · แต่ละบริษัท scope ของตัวเอง (บริษัทหนึ่งล้มไม่ลากบริษัทอื่น) · สิ้นเดือนไทย ⇒ ปิดส่วนเกินพื้นที่ของงวดก่อน (idempotent ต่อ (บริษัท, งวด))</para>
/// </summary>
public class UsageSnapshotJob : BackgroundService
{
    public const int BatchSize = 500;
    private static readonly TimeSpan Cycle = TimeSpan.FromHours(6);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<UsageSnapshotJob> _logger;

    public UsageSnapshotJob(IServiceScopeFactory scopes, ILogger<UsageSnapshotJob> logger) { _scopes = scopes; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(7), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lockScope = _scopes.CreateScope();
                var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(UsageSnapshotJob),
                    () => RunOnceAsync(stoppingToken), _logger, ct: stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _logger.LogError(ex, "UsageSnapshotJob รอบนี้ล้ม"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    /// <summary>หนึ่งรอบ — public เพื่อเทสต์/ปุ่มแอดมิน (ต้องถือ JobLock ก่อนเรียกเสมอ)</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        List<Guid> due; List<Guid> settle; string prevPeriod;
        using (var scope = _scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            due = await DueCompaniesAsync(db, started, ct);
            prevPeriod = UsageHistoryRule.Periods(started, 2)[1];
            settle = await StorageSettleCandidatesAsync(db, prevPeriod, ct);
        }
        int done = 0, failed = 0, settled = 0, purged = 0;
        // ฝ่ายค้าน N5: ปิดส่วนเกินพื้นที่ของงวดก่อน "ก่อน" เขียน snapshot รายวัน (UsageEvent ของงวดก่อนต้องเกิดก่อนที่ประวัติ/ใบจะอ่าน) · ล้าง snapshot เก่ากว่า 24 เดือน
        foreach (var companyId in settle)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var scope = _scopes.CreateScope();
                if (await scope.ServiceProvider.GetRequiredService<IUsageSnapshotService>().SettleStorageOverageAsync(companyId, prevPeriod, ct)) settled++;
            }
            catch (Exception ex) { failed++; _logger.LogError(ex, "ปิดส่วนเกินพื้นที่งวด {Period} ของบริษัท {Company} ไม่สำเร็จ", prevPeriod, companyId); }
        }
        foreach (var companyId in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IUsageSnapshotService>().WriteDailyAsync(companyId, ct);
                done++;
            }
            catch (Exception ex) { failed++; _logger.LogError(ex, "snapshot การใช้งานของบริษัท {Company} ไม่สำเร็จ", companyId); }
        }
        try
        {
            using var scope = _scopes.CreateScope();
            purged = await PurgeExpiredAsync(scope.ServiceProvider.GetRequiredService<AccountingDbContext>(), started, ct);
        }
        catch (Exception ex) { failed++; _logger.LogError(ex, "ล้าง snapshot เก่ากว่า {Months} เดือนไม่สำเร็จ", UsageHistoryRule.RetentionMonths); }
        using (var scope = _scopes.CreateScope())
        {
            var rec = scope.ServiceProvider.GetService<IJobRunRecorder>();
            if (rec != null)
                await rec.RecordAsync("UsageSnapshot", failed == 0, $"snapshot {done}/{due.Count} บริษัท · ส่วนเกินพื้นที่งวด {prevPeriod} {settled} บริษัท · ล้างเก่า {purged} แถว · ล้ม {failed}", done + settled, started);
        }
        return done;
    }

    /// <summary>ฝ่ายค้าน N5: ลบ snapshot รายเดือน/รายวันที่เก่ากว่า <see cref="UsageHistoryRule.RetentionMonths"/> เดือน (งวด yyyy-MM เทียบเป็นข้อความได้) · คืนจำนวนแถวที่ลบ</summary>
    public static async Task<int> PurgeExpiredAsync(AccountingDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var periods = UsageHistoryRule.Periods(nowUtc, UsageHistoryRule.RetentionMonths);
        var oldest = periods[^1];
        var (oldestStart, _) = UsageHistoryRule.PeriodRangeUtc(oldest);
        var monthly = await db.UsageMonthlySnapshots.Where(s => string.Compare(s.Period, oldest) < 0).ExecuteDeleteAsync(ct);
        var daily = await db.UsageDailySnapshots.Where(d => d.Date < oldestStart.AddHours(7)).ExecuteDeleteAsync(ct);
        return monthly + daily;
    }

    /// <summary>บริษัทที่มีแพ็กเกจใช้งานอยู่ (ไม่ยกเลิก/ไม่ระงับ) และยังไม่ถูก snapshot วันนี้ (วันไทย) — เก่าสุดก่อน</summary>
    public static async Task<List<Guid>> DueCompaniesAsync(AccountingDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var period = UsageHistoryRule.PeriodOf(nowUtc);
        var todayStartUtc = DateTime.SpecifyKind(PlatformInvoiceRules.ThaiDate(nowUtc), DateTimeKind.Utc).AddHours(-7);
        var active = await db.Subscriptions.AsNoTracking()
            .Where(s => !s.IsDeleted && s.Status != SubscriptionStatus.Cancelled && s.Status != SubscriptionStatus.Suspended)
            .Select(s => s.CompanyId).Distinct().ToListAsync(ct);
        var last = await db.UsageMonthlySnapshots.AsNoTracking()
            .Where(s => s.Period == period && s.ResourceKey == QuotaResources.Users && active.Contains(s.CompanyId))
            .ToDictionaryAsync(s => s.CompanyId, s => s.SnapshotAtUtc, ct);
        return active
            .Where(c => !last.TryGetValue(c, out var at) || at < todayStartUtc)
            .OrderBy(c => last.TryGetValue(c, out var at) ? at : DateTime.MinValue)
            .Take(BatchSize).ToList();
    }

    /// <summary>บริษัทที่มีตัวอย่างพื้นที่รายวันในงวดก่อนและยังไม่มี UsageEvent ส่วนเกินพื้นที่ของงวดนั้น</summary>
    public static async Task<List<Guid>> StorageSettleCandidatesAsync(AccountingDbContext db, string period, CancellationToken ct)
    {
        var (start, end) = UsageHistoryRule.PeriodRangeUtc(period);
        var with = await db.UsageDailySnapshots.AsNoTracking()
            .Where(d => d.ResourceKey == QuotaResources.StorageGb && d.Date >= start.AddHours(7) && d.Date < end.AddHours(7))
            .Select(d => d.CompanyId).Distinct().ToListAsync(ct);
        if (with.Count == 0) return with;
        var key = $"storage-overage:{period}";
        var done = await db.UsageEvents.AsNoTracking().Where(u => u.IdempotencyKey == key && !u.IsDeleted && with.Contains(u.CompanyId))
            .Select(u => u.CompanyId).ToListAsync(ct);
        return with.Except(done).ToList();
    }
}
