using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Jobs;

/// <summary>งานเบื้องหลังล้าง "รูปตัวอย่างของตะกร้าที่ไม่ได้สั่ง" (R-25 · POS_MTO_DESIGN §4 #8 · ฝ่ายค้าน K ข้อ 4)
///
/// <para>ที่มา: ลูกค้าหน้าร้านแนบรูปตัวอย่างสินค้าตามสั่งตอนอยู่ในตะกร้า ⇒ <c>FileAttachment(EntityType="SiteOrder", EntityId=cartId)</c> ·
/// สั่งซื้อแล้ว EntityId ถูกย้ายเป็น orderId · ตะกร้าที่ไม่ได้สั่ง ⇒ แถว+ไฟล์ค้างตลอดไป</para>
///
/// <para>กติกา (ตัวตัดสิน <see cref="CartImageRetention"/>): ลบเฉพาะไฟล์ชนิด <c>SiteOrder</c> ที่ <c>EntityId</c> <b>ไม่อยู่ใน SiteOrders</b>
/// (รวมออเดอร์ที่ถูก soft-delete — ยังเป็นออเดอร์ ไม่ลบ) และ <c>CreatedAt</c> เกิน N วัน (<c>Cms:CartImageRetentionDays</c> · ค่าเริ่มต้น 14) ·
/// ลบผ่าน <see cref="IFileAttachmentService.DeleteAsync"/> ตัวเดียว (ห้าม <c>File.Delete</c> เอง — กฎเหล็ก #4 D) · กันรันซ้ำข้าม instance ด้วย
/// <see cref="JobLock"/> (F4 #6) · ทำเป็นก้อนละ 200 แถวต่อรอบ (ไม่กวาดทั้งฐานในคำสั่งเดียว)</para>
///
/// <para>ทิศตรงข้าม: รูปของตะกร้าที่กลายเป็นออเดอร์ไม่ถูกลบ · รูปที่ยังไม่ครบอายุไม่ถูกลบ · สลิปออเดอร์ (ชนิด SiteOrder เหมือนกัน แต่ EntityId = orderId)
/// ไม่ถูกลบ</para></summary>
public class CartImageCleanupJob : BackgroundService
{
    public const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<CartImageCleanupJob> _logger;

    public CartImageCleanupJob(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<CartImageCleanupJob> logger)
    {
        _scopeFactory = scopeFactory;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Cms:CartImageCleanup:Enabled", true)) return;
        var interval = TimeSpan.FromHours(Math.Clamp(_config.GetValue("Cms:CartImageCleanup:IntervalHours", 6), 1, 168));

        // หน่วงให้ migration/DB พร้อมก่อน
        try { await Task.Delay(TimeSpan.FromMinutes(7), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceGuardedAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "CartImageCleanupJob ล้ม (จะลองใหม่รอบถัดไป)");
            }
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunOnceGuardedAsync(CancellationToken ct)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(CartImageCleanupJob),
            () => RunOnceAsync(ct), _logger, ct: ct);
    }

    /// <summary>รูปตะกร้าที่ไม่ได้สั่งและครบอายุ — เงื่อนไขทั้งหมดใน SQL (ฝ่ายค้านรอบ 218 รอบ 2 F1) · <c>!IsDeleted</c> ระบุเอง (ไม่พึ่ง global filter)</summary>
    public static IQueryable<Accounting.Models.Entities.FileAttachment> CartImageCandidates(AccountingDbContext db, DateTime cutoff)
        => db.FileAttachments.AsNoTracking()
            .Where(f => !f.IsDeleted && f.EntityType == CmsMediaUrl.OrderEntityType && f.CreatedAt < cutoff
                && !db.SiteOrders.IgnoreQueryFilters().Any(o => o.Id == f.EntityId))
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id);

    /// <summary>สลิปการจองที่ครบอายุเก็บ และไม่ใช่สลิปรอตรวจของการจองใด — เงื่อนไขทั้งหมดใน SQL · ตัวตัดสินอายุ <see cref="BookingSlipRetention"/></summary>
    public static IQueryable<Accounting.Models.Entities.FileAttachment> SlipPurgeCandidates(AccountingDbContext db, DateTime cutoff)
        => db.FileAttachments.AsNoTracking()
            .Where(f => !f.IsDeleted && f.EntityType == BookingSlipRule.AttachmentEntityType && f.CreatedAt < cutoff
                && !db.SiteBookings.IgnoreQueryFilters().Any(b => b.CompanyId == f.CompanyId && b.SlipAttachmentId == f.Id && b.SlipUploadedAt != null))
            .OrderBy(f => f.CreatedAt).ThenBy(f => f.Id);

    /// <summary>หนึ่งรอบ — คืนจำนวนไฟล์ที่ลบ (ให้เทสต์/ผู้เรียกตรวจได้)</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var files = scope.ServiceProvider.GetRequiredService<IFileAttachmentService>();

        var days = CartImageRetention.NormalizeDays(_config.GetValue<int?>("Cms:CartImageRetentionDays"));
        var cutoff = CartImageRetention.Cutoff(DateTime.UtcNow, days);

        // ตะกร้าที่ไม่ได้สั่ง = EntityId ไม่ใช่ออเดอร์ใด ๆ (IgnoreQueryFilters: ออเดอร์ที่ถูก soft-delete ยังนับเป็นออเดอร์ — รูปเป็นหลักฐาน)
        var candidates = await CartImageCandidates(db, cutoff)
            .Take(BatchSize)
            .Select(f => new { f.Id, f.CompanyId, f.EntityId, f.CreatedAt })
            .ToListAsync(ct);

        var deleted = 0;
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            // ตัวตัดสินตัวเดียว (ซ้ำกับ query โดยเจตนา — query คือตัวกรอง · helper คือกติกาที่เทสต์ล็อกไว้)
            if (!CartImageRetention.ShouldDelete(c.CreatedAt, belongsToOrder: false, cutoff)) continue;
            try
            {
                await files.DeleteAsync(c.CompanyId, c.Id);
                deleted++;
            }
            catch (KeyNotFoundException) { /* ถูกลบไปแล้วระหว่างรอบ */ }
        }
        if (deleted > 0)
            _logger.LogInformation("CartImageCleanupJob: ลบรูปตะกร้าที่ไม่ได้สั่ง {Count} ไฟล์ (อายุเกิน {Days} วัน)", deleted, days);

        // ── ฝ่ายค้านรอบ 218 รอบ 2 (F1/F2): สลิปการจองครบอายุเก็บ (BookingSlipRetention — 6 ปี · หลักฐานรับ/คืนเงิน) ⇒ ลบ · สลิปรอตรวจไม่ลบ ·
        // เงื่อนไขทั้งหมดอยู่ใน SQL (ไม่ใช่ Take ก่อนกรองในหน่วยความจำ ⇒ ไม่ค้างที่ 200 แถวแรกที่เก็บไว้) · แถวที่ลบแล้ว (IsDeleted) ไม่ถูกหยิบซ้ำ
        var slipCutoff = BookingSlipRetention.Cutoff(DateTime.UtcNow);
        var slipRows = await SlipPurgeCandidates(db, slipCutoff)
            .Take(BatchSize)
            .Select(f => new { f.Id, f.CompanyId, f.CreatedAt })
            .ToListAsync(ct);
        var slipsDeleted = 0;
        foreach (var r in slipRows)
        {
            ct.ThrowIfCancellationRequested();
            if (!BookingSlipRetention.ShouldPurge(r.CreatedAt, isPendingSlip: false, slipCutoff)) continue;
            try { await files.DeleteAsync(r.CompanyId, r.Id); slipsDeleted++; }
            catch (KeyNotFoundException) { /* ถูกลบไปแล้วระหว่างรอบ */ }
        }
        if (slipsDeleted > 0)
            _logger.LogInformation("CartImageCleanupJob: ลบสลิปการจองที่ครบอายุเก็บ {Count} ไฟล์ (เกิน {Years} ปี)", slipsDeleted, BookingSlipRetention.RetentionYears);
        return deleted + slipsDeleted;
    }
}
