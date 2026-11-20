using Accounting.Data;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 272 — งานเบื้องหลังของเส้นสลิปภาษี (ทุก 10 นาที · กันสองเครื่องด้วย <c>Helpers/JobLock</c> try-lock ไม่รอ · F4 ข้อ 6):
/// (1) สลิปที่พักรอความยินยอม ⇒ ได้ความยินยอมแล้วประมวลใหม่ทั้งชุดด่าน / หมดอายุ 30 วันเข้าคิวพนักงาน + การ์ด ·
/// (2) ปุ่มเลือกในกลุ่มเกิน 24 ชม. ⇒ คิวพนักงาน · (3) P5 ตรวจทานแถวนำส่งจากสลิปกับรายการเดินบัญชี (ธงเตือนเท่านั้น)
/// </summary>
public class TaxSlipJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<TaxSlipJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    public TaxSlipJob(IServiceProvider services, ILogger<TaxSlipJob> logger) { _services = services; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                var intake = scope.ServiceProvider.GetRequiredService<Accounting.Services.Implementations.TaxSlipIntakeService>();
                var review = scope.ServiceProvider.GetRequiredService<Accounting.Services.Implementations.TaxSlipReviewService>();
                var requests = scope.ServiceProvider.GetRequiredService<Accounting.Services.Implementations.TaxPaymentRequestService>();
                await Accounting.Helpers.JobLock.RunExclusiveAsync(db, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(TaxSlipJob), async () =>
                {
                    var now = DateTime.UtcNow;
                    var moved = await intake.RunHeldAsync(now, stoppingToken);
                    var reviewed = await review.ReviewAsync(now, stoppingToken);
                    // รอบ 275 แพ็ก D (M7): ใบขอชำระที่ Control Code หมดอายุ ⇒ Expired + แจ้งกลุ่ม
                    var expired = await requests.ExpireDueAsync(now, stoppingToken);
                    if (moved + reviewed + expired > 0)
                        _logger.LogInformation("TaxSlipJob: สลิปที่พัก/รอเลือก/อายุคิว {Moved} · ตรวจทานนำส่ง {Reviewed} · ใบขอชำระหมดอายุ {Expired}", moved, reviewed, expired);
                }, _logger, ct: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "TaxSlipJob ล้ม — รอบหน้าลองใหม่");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
