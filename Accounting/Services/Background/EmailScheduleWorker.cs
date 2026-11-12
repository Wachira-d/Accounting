using Accounting.Services.Interfaces;

namespace Accounting.Services.Background;

/// <summary>
/// HostedService สำหรับการส่งอีเมลอัตโนมัติ:
///   • ProcessPendingQueueAsync ทุก 1 นาที — ดึงคิวถึงเวลามาส่ง
///   • ScanDueSoonAndOverdueAsync ทุก 1 ชม. — สแกนเอกสารใกล้/เกินกำหนด
/// scoped DI ใช้ผ่าน _services.CreateScope() ตามปกติของ BackgroundService.
/// </summary>
public class EmailScheduleWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<EmailScheduleWorker> _logger;
    private static readonly TimeSpan QueueInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromHours(1);

    public EmailScheduleWorker(IServiceProvider services, ILogger<EmailScheduleWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // หน่วงเริ่มทำงาน 30s ให้ app boot เสร็จก่อน
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (TaskCanceledException) { return; }

        DateTime lastScan = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<IEmailScheduleService>();

                // รอบ 259 (A-L7): คิวอีเมลโยน ⇒ เดิมทั้งรอบหลุด (คิว LINE/งานหลังรับชำระไม่ถูกเรียก) — แยก try ต่อขั้น · ล้มดังใน log ระดับ Error
                try
                {
                    var sent = await svc.ProcessPendingQueueAsync(stoppingToken);
                    if (sent > 0)
                        _logger.LogInformation("EmailScheduleWorker: ส่ง {Count} ฉบับสำเร็จ", sent);
                }
                catch (Exception emailEx) when (emailEx is not TaskCanceledException && emailEx is not OperationCanceledException)
                {
                    _logger.LogError(emailEx, "EmailScheduleWorker: คิวอีเมลรอบนี้ล้ม — ไปต่อคิว LINE/งานหลังรับชำระ");
                }

                // รอบ 254 WP-11 L2: คิว LINE ถึงผู้ติดต่อ — scope ของตัวเอง (แถวหนึ่งพังไม่ลากคิวอีเมล) · จองแถวด้วย SKIP LOCKED (หลายเครื่องไม่ส่งซ้ำ)
                try
                {
                    using var lineScope = _services.CreateScope();
                    var lineQueue = lineScope.ServiceProvider.GetRequiredService<IContactLineQueueService>();
                    var lineSent = await lineQueue.ProcessPendingAsync(stoppingToken);
                    if (lineSent > 0)
                        _logger.LogInformation("EmailScheduleWorker: ส่ง LINE ถึงผู้ติดต่อ {Count} ข้อความ", lineSent);
                }
                catch (Exception lineEx) when (lineEx is not TaskCanceledException && lineEx is not OperationCanceledException)
                {
                    _logger.LogError(lineEx, "EmailScheduleWorker: คิว LINE รอบนี้ล้ม — ไปต่องานหลังรับชำระ");
                }

                // รอบ 241: งานหลังรับชำระ (ออก/ส่งใบเสร็จ) — scope ของตัวเอง (แถวหนึ่งพังไม่ลากคิวอีเมล) · จองแถวด้วย SKIP LOCKED (หลายเครื่องไม่ทำซ้ำ)
                using (var postPayScope = _services.CreateScope())
                {
                    var postPay = postPayScope.ServiceProvider.GetRequiredService<IPostPaymentAutomationService>();
                    var runs = await postPay.ProcessDueAsync(stoppingToken);
                    if (runs > 0)
                        _logger.LogInformation("EmailScheduleWorker: ประมวลผลงานหลังรับชำระ {Count} รายการ", runs);
                }

                if (DateTime.UtcNow - lastScan > ScanInterval)
                {
                    // รอบ 260-4 (ฝ่ายค้าน L9 · คำตัดสิน 260-4-15): สแกนทุกบริษัท ⇒ ครั้งละเครื่องเดียว (JobLock) — สองเครื่องสแกนพร้อมกันแล้วตัวหาผู้รับให้ผลต่างกัน
                    // (กุญแจเครื่องไม่ตรง/ผูก-ถอนระหว่างอ่าน) ได้ทั้งแถว line:none + แถวปลายทาง + อีเมลถอย ⇒ ลูกค้าได้ทั้ง LINE และอีเมล
                    var enq = 0;
                    var scanDb = scope.ServiceProvider.GetRequiredService<Accounting.Data.AccountingDbContext>();
                    await Accounting.Helpers.JobLock.RunExclusiveAsync(scanDb, Accounting.Helpers.AdvisoryLockKey.DueSoonOverdueScan, "global",
                        async () => enq = await svc.ScanDueSoonAndOverdueAsync(stoppingToken), _logger, ct: stoppingToken);
                    if (enq > 0)
                        _logger.LogInformation("EmailScheduleWorker: enqueue {Count} แจ้งเตือนใกล้/เกินกำหนด", enq);

                    var stmt = await svc.ScanMonthlyStatementsAsync(stoppingToken);
                    if (stmt > 0)
                        _logger.LogInformation("EmailScheduleWorker: enqueue {Count} statement รายเดือน", stmt);

                    lastScan = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is not TaskCanceledException)
            {
                _logger.LogError(ex, "EmailScheduleWorker tick failed");
            }

            try { await Task.Delay(QueueInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
