using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// เก็บกวาดสแกนที่ค้างสถานะ <c>Processing</c> — และ **คืนโควตา** ให้บริษัท
///
/// ═══ ที่มา (ผลตรวจไปป์ไลน์ OCR 2026-09-06 · ทีม T5) ═══
/// <para><c>OcrController.Scan</c> หักโควตาก่อนเรียก engine แล้วคืนให้เมื่อ
/// (ก) โยน exception (ข) สถานะ != Completed (ค) เป็นไฟล์ซ้ำ/e-Tax XML — ครบดี
/// **ถ้ากระบวนการยังอยู่**. แต่ถ้า process ถูกฆ่ากลางทาง (deploy · OOM · pod evict ·
/// request timeout ของ reverse proxy) แถวจะค้าง <c>Processing</c> **ตลอดไป** และ
/// โควตาที่หักไปแล้ว **ไม่มีใครคืน** ⇒ ผู้ใช้เสียโควตาให้งานที่ไม่เคยได้ผล และ
/// การ์ดค้างหมุนอยู่บนหน้าจอโดยไม่มีปุ่มอะไรทำต่อได้</para>
///
/// <para>กติกา: แถวที่ <c>Processing</c> นานเกิน <see cref="StuckAfter"/> ถือว่าตายแล้ว
/// (สแกนจริงที่ช้าที่สุด — Azure DI 20 หน้า + python fallback — ยังไม่ถึง 10 นาที)
/// → ตั้ง <c>Failed</c> พร้อมเหตุผลที่ผู้ใช้อ่านรู้เรื่อง + คืนโควตา 1 ครั้ง
/// (กันคืนซ้ำด้วย marker ใน <c>ProcessingNotes</c> — แถวเดิมถูกกวาดได้ครั้งเดียว)</para>
///
/// <para>กันรันซ้ำข้าม instance ด้วย <see cref="Accounting.Helpers.JobLock"/> (try-lock —
/// งานตามตารางที่อีกเครื่องทำอยู่ การรอ = ทำงานเดิมซ้ำเปล่า ๆ)</para>
/// </summary>
public class OcrStuckScanSweepJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<OcrStuckScanSweepJob> _logger;

    /// <summary>สแกนที่ค้างนานกว่านี้ = process ที่ทำอยู่ตายไปแล้ว</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>marker ที่บอกว่าแถวนี้ถูกกวาดและคืนโควตาไปแล้ว — กันคืนซ้ำ</summary>
    public const string SweptMarker = "[SWEPT]";

    public OcrStuckScanSweepJob(IServiceProvider services, ILogger<OcrStuckScanSweepJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // jitter ต่างจาก job อื่นเพื่อไม่ให้ตื่นพร้อมกันตอน start
        try { await Task.Delay(TimeSpan.FromMinutes(4), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "OcrStuckScanSweepJob failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var quota = scope.ServiceProvider.GetRequiredService<IOcrQuotaService>();

        await Accounting.Helpers.JobLock.RunExclusiveAsync(db, "ocr-stuck-sweep", "all", async () =>
        {
            var cutoff = DateTime.UtcNow - StuckAfter;
            var stuck = await db.Set<OcrScanResult>()
                .Where(r => r.ScanStatus == "Processing" && r.CreatedAt < cutoff && !r.IsDeleted)
                .OrderBy(r => r.CreatedAt)
                .Take(200)
                .ToListAsync(ct);
            if (stuck.Count == 0) return;

            foreach (var row in stuck)
            {
                var notes = row.ProcessingNotes ?? "";
                if (notes.Contains(SweptMarker, StringComparison.Ordinal)) continue;

                row.ScanStatus = "Failed";
                row.ProcessedAt = DateTime.UtcNow;
                row.ProcessingNotes = notes
                    + $"\n{SweptMarker} การสแกนถูกขัดจังหวะ (เซิร์ฟเวอร์รีสตาร์ท/หมดเวลา) — "
                    + "คืนโควตาให้แล้ว กด \"สแกนใหม่\" บนการ์ดเพื่อลองอีกครั้ง";

                // คืนโควตาให้บริษัทของแถวนั้น — หน่วยที่หักไปตอนอัปโหลดไม่เคยถูกใช้จริง
                try { await quota.RefundAsync(row.CompanyId); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "คืนโควตา OCR ไม่สำเร็จ (scan {ScanId}, company {Cid})",
                        row.Id, row.CompanyId);
                }
            }

            await db.SaveChangesAsync(ct);
            _logger.LogInformation("OcrStuckScanSweepJob: กวาดสแกนค้าง {Count} แถว", stuck.Count);
        }, _logger, ct: ct);
    }
}
