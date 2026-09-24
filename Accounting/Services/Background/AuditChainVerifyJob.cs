using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>F14 — Audit log hash chain ตรวจสอบรายสัปดาห์.
/// AuditTrailService.VerifyHashChainAsync re-compute SHA-256 chain ของทุกแถว
/// (per company) แล้วเทียบกับ stored RowHash. ถ้าเจอ mismatch =
/// chain ถูก tamper (แก้/แทรก/ลบหลัง insert) → ส่ง notification ระดับ
/// admin + log error ดังลั่น. ผ่านมาตรฐาน พ.ร.บ.บัญชี ม.11 ทวิ
/// (เก็บข้อมูลอิเล็กทรอนิกส์ที่ตรวจสอบได้).
///
/// Schedule: ทุก 7 วัน (defer first run 5 นาทีเพื่อให้ EF + migrations พร้อม).
/// Idempotent: ตรวจไม่แก้ข้อมูล. Notification ส่งเฉพาะตอนเจอ tamper
/// — สุขภาพดีก็เงียบ.</summary>
public class AuditChainVerifyJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<AuditChainVerifyJob> _logger;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(7);

    public AuditChainVerifyJob(IServiceProvider services, ILogger<AuditChainVerifyJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "AuditChainVerifyJob cycle failed"); }

            try { await Task.Delay(CheckInterval, stoppingToken); } catch { return; }
        }
    }

    /// <summary>กันสอง instance ทำงานรอบเดียวกันพร้อมกัน (ผลตรวจ F-09)
    ///
    /// <para>ใช้ <b>try</b> ไม่ใช่ wait — งานตามตารางที่อีกเครื่องกำลังทำอยู่
    /// การรอคือทำงานเดิมซ้ำเปล่า ๆ ข้ามไปรอบหน้าถูกกว่า</para></summary>
    private async Task RunGuardedAsync(CancellationToken ct)
    {
        using var lockScope = _services.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await Accounting.Helpers.JobLock.RunExclusiveAsync(
            lockDb, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(AuditChainVerifyJob),
            () => RunCycleAsync(ct), _logger, ct: ct);
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditTrailService>();
        var notify = scope.ServiceProvider.GetService<INotificationEngine>();

        var companyIds = await db.AuditLogs.AsNoTracking()
            .Where(a => a.RowHash != null && a.CompanyId != null)
            .Select(a => a.CompanyId!.Value)
            .Distinct()
            .ToListAsync(ct);

        foreach (var companyId in companyIds)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var result = await audit.VerifyHashChainAsync(companyId);
                if (result.ForkCount > 0)
                {
                    // แตกกิ่งจากคำขอพร้อมกัน (ฝ่ายค้านรอบ 193 รอบสอง W2-C1) — ไม่ใช่หลักฐานการแก้ ⇒ ไม่แจ้งลูกค้าว่า "ถูกแก้"
                    // แต่ไม่เงียบ: เป็นข้อบกพร่องของระบบฝั่งเรา (ยังไม่ serialize การประทับ — คำถามเจ้าของใน r193-W.md)
                    _logger.LogWarning(
                        "Audit chain of {CompanyId} has {Forks} fork(s) from concurrent writes (not tampering)",
                        companyId, result.ForkCount);
                }
                if (result.IsValid)
                {
                    _logger.LogInformation(
                        "Audit chain OK for {CompanyId}: {Rows} rows verified",
                        companyId, result.TotalRows);
                    continue;
                }

                _logger.LogError(
                    "🚨 Audit chain integrity findings for {CompanyId}: tampered {Tampered} · dangling {Dangling} (first log {LogId} @ {At}) — {Total} total rows",
                    companyId, result.TamperedCount, result.DanglingCount, result.FirstBrokenLogId,
                    result.FirstBrokenAt, result.TotalRows);

                if (notify != null)
                {
                    try
                    {
                        await notify.DispatchAsync(companyId, NotificationEvents.AuditChainTampered, new NotificationContext
                        {
                            Title = result.TamperedCount > 0
                                ? "🚨 Audit log บางแถวถูกแก้หลังบันทึก — ตรวจสอบด่วน"
                                : "🚨 Audit log ขาดตอน (แถวก่อนหน้าถูกลบหรือถูกแก้) — ตรวจสอบด่วน",
                            // ข้อความตามสาเหตุที่ตรวจพบจริง + รายการแถวทั้งหมด (Helpers/AuditHashChain.AlertMessage) —
                            // เดิมอ้าง "raw SQL" ทุกกรณีและบอกแค่แถวแรก (F2 ข้อ 7)
                            Message = result.AlertMessage ?? "ตรวจพบความไม่ตรงกันใน audit log",
                            ActionUrl = "/pages/audit-log.html",
                            EntityType = "AuditLog", EntityId = companyId,
                        });
                    }
                    catch (Exception nex)
                    {
                        _logger.LogWarning(nex, "AuditChainTampered notification failed for {CompanyId}", companyId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Audit chain verify failed for {CompanyId}", companyId);
            }
        }
    }
}
