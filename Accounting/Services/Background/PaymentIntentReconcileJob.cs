using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// **ตาข่ายรับของ webhook** — ไล่ถามสถานะสดของรายการที่ยังรอจ่าย
///
/// ═══ ทำไมต้องมี (PAYMENT_GATEWAY_DESIGN.md ข้อสรุปทีม 7) ═══
/// webhook เป็นเส้นเร็ว <b>ไม่ใช่เส้นเดียว</b> — มันหายได้จริงหลายทาง: ผู้ให้บริการส่งพลาด ·
/// เซิร์ฟเวอร์เรารีสตาร์ตพอดี · ผู้ใช้ยังไม่ได้ตั้ง URL ในแดชบอร์ด · ไฟร์วอลล์กั้น ·
/// ถ้าไม่มีตัวนี้ อาการที่ผู้ใช้เจอคือ <b>"ลูกค้าจ่ายเงินแล้วแต่ออเดอร์ยังค้าง"</b> ซึ่ง
/// เจ้าของร้านไม่มีทางรู้จนลูกค้าโทรมา
///
/// ═══ สิ่งที่ทำทุกรอบ ═══
/// <list type="number">
/// <item>รายการที่ยังเปิดอยู่และเพิ่งถามไปนานพอ → ถามสถานะสด</item>
/// <item>รายการที่ QR/ลิงก์หมดอายุแล้ว → ปิดเป็น <c>Expired</c> (ไม่ต้องรบกวนผู้ให้บริการ)
///   — ทำใน <c>RefreshAsync</c> อยู่แล้ว</item>
/// <item>รายการ **โหมดใช้งานจริง** ที่ค้างเกิน 30 นาที → log ระดับ warning
///   (เงียบไว้ = ปัญหาถูกพบตอนปิดบัญชีสิ้นเดือนแทนที่จะเป็นวันนี้)</item>
/// </list>
///
/// <para><b>ล็อกข้าม instance</b> — หลาย instance รันงานนี้พร้อมกันจะยิงถามผู้ให้บริการ
/// ซ้ำโดยไม่จำเป็น (บางเจ้าจำกัดอัตราเรียก) · คีย์ต้อง deterministic ข้าม process
/// จึงใช้ <c>AdvisoryLockKey</c> ไม่ใช่ <c>HashCode.Combine</c></para>
/// </summary>
public class PaymentIntentReconcileJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentIntentReconcileJob> _logger;

    private static readonly TimeSpan Cycle = TimeSpan.FromMinutes(5);
    /// <summary>เว้นระยะก่อนถามซ้ำ — ถามถี่กว่านี้ไม่ช่วยอะไร (ลูกค้าใช้เวลาสแกนจ่ายเป็นนาที)
    /// และเปลืองโควตาเรียก API ของผู้ให้บริการ</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    /// <summary>ค้างนานกว่านี้ในโหมดใช้งานจริง = ต้องมีคนดู</summary>
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(30);
    /// <summary>จำกัดต่อรอบ — กันงานเดียวยึดฐานข้อมูล/โควตา API ทั้งหมดตอนมีคิวยาว</summary>
    private const int MaxPerCycle = 200;

    public PaymentIntentReconcileJob(IServiceScopeFactory scopeFactory,
        ILogger<PaymentIntentReconcileJob> logger)
    { _scopeFactory = scopeFactory; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // หน่วงตอนเริ่ม — ให้ระบบขึ้นให้เสร็จก่อน (migration/seed) แล้วค่อยเริ่มยิง
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "PaymentIntentReconcileJob รอบนี้ล้มเหลว"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var intents = scope.ServiceProvider.GetRequiredService<IPaymentIntentService>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // คีย์ระดับระบบ (ไม่ผูกบริษัท) — งานเดินทีเดียวทุก tenant
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(AdvisoryLockKey.PaymentIntent, "reconcile-tick") }, ct);

        var cutoff = DateTime.UtcNow - PollInterval;
        var due = await db.PaymentIntents.AsNoTracking()
            .Where(i => (i.Status == PaymentIntentStatus.Created || i.Status == PaymentIntentStatus.Pending)
                     && (i.LastPolledAt == null || i.LastPolledAt < cutoff))
            .OrderBy(i => i.LastPolledAt)
            .Take(MaxPerCycle)
            .Select(i => new { i.Id, i.CompanyId, i.ProviderCode, i.CreatedAt, i.ProviderConfigId, i.Amount })
            .ToListAsync(ct);
        await tx.CommitAsync(ct);

        if (due.Count == 0) return;

        var liveConfigs = await db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.Mode == PaymentProviderMode.Live)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var stuck = 0;
        foreach (var row in due)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var updated = await intents.RefreshAsync(row.CompanyId, row.Id, ct);

                // ค้างนานในโหมดจริง = เงินของลูกค้าอาจค้างอยู่ ต้องมีคนดู
                if (PaymentIntentPolicy.IsOpen(updated.Status)
                    && row.ProviderConfigId is Guid cid && liveConfigs.Contains(cid)
                    && DateTime.UtcNow - row.CreatedAt > StuckThreshold)
                {
                    stuck++;
                    _logger.LogWarning(
                        "รายการชำระเงินค้างเกิน {Minutes} นาที: {Intent} บริษัท {Company} "
                        + "ยอด {Amount:N2} ผ่าน {Provider} — ตรวจว่าตั้ง webhook URL ไว้ถูกหรือยัง",
                        StuckThreshold.TotalMinutes, row.Id, row.CompanyId, row.Amount, row.ProviderCode);
                }
            }
            catch (Exception ex)
            {
                // รายการเดียวล้มต้องไม่ทำให้ทั้งรอบตาย — ตัวถัดไปอาจสำเร็จ
                _logger.LogWarning(ex, "ตรวจสถานะรายการชำระเงิน {Intent} ไม่สำเร็จ", row.Id);
            }
        }

        _logger.LogInformation(
            "ตรวจสถานะรายการชำระเงิน {Count} รายการ · ค้างนานผิดปกติ {Stuck} รายการ",
            due.Count, stuck);
    }
}
