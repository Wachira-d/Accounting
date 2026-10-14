namespace Accounting.Services.Import;

/// <summary>
/// งานเบื้องหลังของศูนย์นำเข้าข้อมูล — <b>โครง (T0 รอบ 205)</b> · เจ้าของ: <b>ทีม B</b> · ลงทะเบียน <c>AddHostedService</c> แล้ว (Program.cs)
///
/// <para>ตอนนี้ <see cref="ExecuteAsync"/> จบทันที (ยังไม่มี batch ใดถูกตั้ง Queued เพราะ CreateAsync ยังไม่ต่อสาย) · แบบที่ทีม B ต้องเติม
/// (IMPORT_ETL_REVIEW §2.4): วนทุก ~15 วินาที · claim batch <c>Status = Queued</c> (หรือ Committing ที่ <c>HeartbeatAt</c> เก่ากว่า 2 นาที)
/// ด้วย <c>FOR UPDATE SKIP LOCKED</c> (แบบ <c>EmailScheduleService</c>) · ทำงานใต้
/// <c>JobLock.RunExclusiveAsync(db, AdvisoryLockKey.ImportBatch, batchId.ToString(), …, companyId: cid)</c> (CLAUDE.md F4 ข้อ 6 — ห้ามข้าม) ·
/// เรียก <see cref="IImportPipelineService.ProcessQueuedAsync"/> ทีละ chunk 500 แถว + heartbeat ทุก ≤ 2 นาที · scope ใหม่ต่อรอบ · ไม่มี static state ·
/// purge staged rows ของ batch ที่ commit แล้ว &gt; 90 วัน (คงสรุป + ไฟล์ต้นทาง) · ล้ม ⇒ log + สถานะ batch บอกผู้ใช้ (ล้มดัง 3 ที่ · F2 ข้อ 7)</para>
/// </summary>
public class ImportBatchWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ImportBatchWorker> _logger;

    public ImportBatchWorker(IServiceProvider services, ILogger<ImportBatchWorker> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("ImportBatchWorker: โครง T0 — ยังไม่มีงาน (ทีม B ต่อสาย claim/JobLock รอบ 205) · services {Services}", _services.GetType().Name);
        return Task.CompletedTask;
    }
}
