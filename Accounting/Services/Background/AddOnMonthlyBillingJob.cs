using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// **ออกค่าเหมารายเดือนของ add-on** — ตัวที่ทำให้ "guest portal +100/เดือน" เก็บเงินได้จริง
/// (LODGING_LICENSING_PLAN.md §6 เฟส 1)
///
/// ที่มา: `ApiPricingPlan.Method = FlatMonthly` มีมาตั้งแต่รอบ Connected API และ
/// `UsageMeteringService.ComputeCharge` คืน `0m` ให้มันโดยตั้งใจ (การใช้งานราย
/// ครั้งต้องไม่คิดเงิน) โดยคอมเมนต์บอกว่า "ค่าบริการมาจากรอบบิล" — **แต่ไม่เคยมี
/// รอบบิลไหนในระบบ** ⇒ ฟีเจอร์เหมารายเดือนเปิดใช้ได้แต่ไม่เคยถูกเรียกเก็บเงินสัก
/// บาท (defect class "ของที่สร้างไว้แล้วไม่ได้ถูกเรียกใช้") งานนี้คือรอบบิลนั้น
///
/// กติกา:
///   • เดินทุก 6 ชม. (ไม่ใช่เดือนละครั้ง) เพื่อให้ลูกค้าที่เพิ่งกดเปิดกลางเดือน
///     ถูกคิดค่าเดือนนั้นภายในไม่กี่ชั่วโมง — ตรงกับนโยบาย "ไม่ prorate เปิดวันไหน
///     ก็จ่ายเต็มเดือน" (§7.2) โดยไม่ต้องเขียนเส้นทางเก็บเงินที่สอง
///   • งวด = เดือนปฏิทิน**ไทย** (`AddOnBilling.PeriodOf`) — pure + เทสต์ได้
///   • กันซ้ำสองชั้น: `CompanyFeature.LastBilledPeriod` + unique index บน
///     (CompanyId, IdempotencyKey) ของ UsageEvent
///   • กันหลาย instance ชนกัน: `pg_advisory_xact_lock` คีย์จาก
///     `AdvisoryLockKey.For(...)` (FNV-1a — deterministic ข้าม process
///     ต่างจาก HashCode.Combine ที่สุ่ม seed ต่อ process)
///   • trial หมด + `AutoDisableAfterTrial` = ปิดให้เอง ไม่คิดเงินโดยไม่ถาม
/// </summary>
public class AddOnMonthlyBillingJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AddOnMonthlyBillingJob> _logger;
    private static readonly TimeSpan Cycle = TimeSpan.FromHours(6);

    public AddOnMonthlyBillingJob(IServiceScopeFactory scopeFactory, ILogger<AddOnMonthlyBillingJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // รอให้ migration ทำงานจบก่อน (ตาราง/คอลัมน์ใหม่ต้องมีแล้ว)
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnce(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "AddOnMonthlyBillingJob cycle failed"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var metering = scope.ServiceProvider.GetRequiredService<IUsageMeteringService>();
        var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();

        var startedAt = DateTime.UtcNow;
        var period = AddOnBilling.PeriodOf(startedAt);

        // ล็อกทั้งงวด — instance อื่นที่ตื่นพร้อมกันจะรอแล้วเห็นว่า LastBilledPeriod
        // ถูกอัปเดตไปแล้ว จึงข้ามทุกแถว (ไม่ใช่ทำซ้ำ)
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lockKey = AdvisoryLockKey.For(AdvisoryLockKey.AddOnMonthlyBilling, period);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { lockKey }, ct);

        // ฟีเจอร์ที่คิดแบบเหมารายเดือน ณ ตอนนี้ (ราคายังไม่หมดอายุ)
        var now = DateTime.UtcNow;
        var flatCodes = await db.ApiPricingPlans.AsNoTracking()
            .Where(p => !p.IsDeleted && p.Method == PricingMethod.FlatMonthly
                     && p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now))
            .Select(p => p.FeatureCode).Distinct().ToListAsync(ct);
        if (flatCodes.Count == 0)
        {
            await tx.CommitAsync(ct);
            return;
        }

        var rows = await db.CompanyFeatures
            .Where(f => !f.IsDeleted && f.IsEnabled && flatCodes.Contains(f.FeatureCode)
                     && (f.LastBilledPeriod == null || f.LastBilledPeriod.CompareTo(period) < 0))
            .ToListAsync(ct);

        var billed = 0; var disabled = 0; decimal total = 0m;
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // trial หมดแล้วและตั้งให้ปิดเอง → ปิด ไม่คิดเงิน (ต้องเช็คก่อนออกบิล)
                if (AddOnBilling.ShouldAutoDisable(row.TrialUntil, row.AutoDisableAfterTrial, now))
                {
                    row.IsEnabled = false;
                    row.DisabledAt = now;
                    row.DisabledBy = "system:trial-ended";
                    disabled++;
                    continue;
                }

                var res = await metering.RecordFlatMonthlyAsync(row.CompanyId, row.FeatureCode, period, ct);
                if (res.Recorded) { billed++; total += res.ChargedAmount; }
            }
            catch (Exception ex)
            {
                // แถวเดียวพังต้องไม่ทำให้ทั้งงวดพัง — แถวที่เหลือยังต้องออกบิล
                _logger.LogWarning(ex, "ออกค่าเหมา add-on ไม่สำเร็จ company={Company} feature={Feature}",
                    row.CompanyId, row.FeatureCode);
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (billed > 0 || disabled > 0)
            _logger.LogInformation("AddOn billing งวด {Period}: ออกบิล {Billed} รายการ รวม {Total:N2} บาท · ปิดหลังหมด trial {Disabled} รายการ",
                period, billed, total, disabled);

        if (recorder != null)
            await recorder.RecordAsync("AddOnMonthlyBilling", true,
                $"งวด {period}: ออกบิล {billed} รายการ ({total:N2} บาท), ปิดหลัง trial {disabled}", billed, startedAt);
    }
}
