using Accounting.Data;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// §82/3 — ภาษีซื้อที่พักไว้ที่ 11640 ("ภาษีซื้อยังไม่ถึงกำหนด") เพราะใบกำกับ
/// ยังไม่ครบ §86/4 เมื่อ **พ้น 6 เดือน** จะเคลม ภ.พ.30 ไม่ได้อีกแล้ว ต้อง
/// reclassify เป็นค่าใช้จ่าย (Dr ค่าใช้จ่าย "ภาษีซื้อขอคืนไม่ได้" / Cr 11640)
///
/// ที่มา: <c>DocumentService.ReclassifyExpiredUndueInputVatAsync</c> เขียนไว้
/// ครบแล้ว **แต่ไม่มีใครเรียก** — ไม่ได้อยู่ใน interface และไม่มี job/endpoint
/// ⇒ ยอด 11640 ค้างเป็น "สินทรัพย์ลอย" ในงบตลอดไป ทั้งที่เคลมไม่ได้แล้ว
/// (งบแสดงฐานะการเงินเกินจริง + ผู้ใช้ไม่รู้ว่าต้องทำอะไร)
///
/// กันรันซ้ำข้าม instance ด้วย <c>pg_advisory_xact_lock</c> ต่อรอบงาน —
/// deploy หลาย node แล้ว JE ปรับปรุงจะไม่ถูก post ซ้ำ (pattern เดียวกับ
/// การออกเลขเอกสารใน FinancialManagementService)
///
/// รันทุก 24 ชม.
/// </summary>
public class UndueInputVatExpiryJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<UndueInputVatExpiryJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>คีย์ advisory lock ของงานนี้ (คงที่ — ทั้งระบบมีผู้รันได้ทีละราย)</summary>
    private const long LockKey = 828_003L;   // §82/3

    public UndueInputVatExpiryJob(IServiceProvider services, ILogger<UndueInputVatExpiryJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // jitter ต่างจาก job อื่นเพื่อไม่ให้ชนกันตอน start
        try { await Task.Delay(TimeSpan.FromMinutes(9), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "UndueInputVatExpiryJob failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var docs = scope.ServiceProvider.GetRequiredService<IDocumentService>();

        // ล็อกทั้งรอบงานไว้ใน transaction เดียว — instance อื่นที่ตื่นพร้อมกัน
        // จะรอ (ไม่ใช่รันซ้อน) แล้วเจอว่าไม่มีอะไรเหลือให้ทำเพราะ
        // InputVatExpiredAt ถูกตั้งไปแล้ว (idempotent อีกชั้น)
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})", new object[] { LockKey }, ct);

        // เฉพาะบริษัทที่ยังมีเอกสารค้าง 11640 จริง — ไม่สแกนทั้งฐานทุกคืน
        var companyIds = await db.Documents.AsNoTracking()
            .Where(d => d.InputVatPostedAsUndue
                && d.InputVatBecameClaimableAt == null
                && d.InputVatExpiredAt == null)
            .Select(d => d.CompanyId)
            .Distinct()
            .ToListAsync(ct);

        var total = 0;
        foreach (var companyId in companyIds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var n = await docs.ReclassifyExpiredUndueInputVatAsync(companyId, "system:undue-vat-expiry");
                total += n;
                if (n > 0)
                    _logger.LogInformation(
                        "§82/3 ล้างภาษีซื้อพ้น 6 เดือน {Count} ใบ (บริษัท {CompanyId})", n, companyId);
            }
            catch (Exception ex)
            {
                // บริษัทหนึ่งพัง ต้องไม่ทำให้บริษัทอื่นไม่ได้รัน
                _logger.LogError(ex, "ล้าง undue VAT ไม่สำเร็จ (บริษัท {CompanyId})", companyId);
            }
        }

        await tx.CommitAsync(ct);
        if (total > 0)
            _logger.LogInformation("UndueInputVatExpiryJob: จัดการทั้งหมด {Total} ใบ", total);
    }
}
