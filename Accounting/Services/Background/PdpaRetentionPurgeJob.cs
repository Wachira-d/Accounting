using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>PDPA retention purge — "ปิดงาน" การลบข้อมูลที่ผู้ใช้เคยขอลบไปแล้ว
/// เมื่อพ้น legal-hold (พ.ร.บ.บัญชี/สรรพากร 5 ปี).
///
/// บริบท: <c>PdpaService.ApplyErasureAsync</c> ตอบคำขอลบ (DSR erase) ด้วยการ
/// anonymize contact ทันที แต่ถ้ายังมีเอกสารในงวด retention 5 ปี จะ **คงแถวไว้**
/// (Address = "[ANON-RETAINED-FOR-RD-5Y]", IsDeleted = false) เพื่อไม่ให้ผิด
/// กฎหมายเก็บเอกสาร. เดิม **ไม่มีใครกลับมาลบจริง** เมื่อ 5 ปีผ่านไป → ข้อมูลที่
/// ผู้ใช้ขอลบค้างในระบบเกินอายุ (ผิด PDPA "retention by purpose").
///
/// งานนี้เติมช่องว่างนั้นแบบ **อนุรักษ์นิยมที่สุด**:
///  • แตะเฉพาะ contact ที่ถูก anonymize + ติดธง retained ไว้แล้ว (ผู้ใช้ขอลบแล้ว)
///  • ลบจริง (IsDeleted = true) เฉพาะเมื่อเอกสารทุกใบพ้น 5 ปี (ไม่มีใบใน 5 ปี)
///  • **ไม่แตะข้อมูล active** เลย — ไม่มีทางลบข้อมูลที่ผู้ใช้ยังไม่ได้ขอลบ
///
/// รันทุก 24 ชม. อ้างอิง CLAUDE.md กฎเหล็ก #2 section J (retention by purpose).</summary>
public class PdpaRetentionPurgeJob : BackgroundService
{
    private const string RetainedMarker = "[ANON-RETAINED-FOR-RD-5Y]";
    private readonly IServiceProvider _services;
    private readonly ILogger<PdpaRetentionPurgeJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public PdpaRetentionPurgeJob(IServiceProvider services, ILogger<PdpaRetentionPurgeJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(9), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "PdpaRetentionPurgeJob failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
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
            lockDb, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(PdpaRetentionPurgeJob),
            () => RunAsync(ct), _logger, ct: ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        // รอบ 211 ฝ่ายค้าน ข้อ 3: รหัสผูก LINE ที่หมดอายุเกิน 7 วัน (ไม่ให้ hash ค้างใน unique index ตลอดไป) +
        // LINE userId ที่ค้างบนลิงก์ที่ปิดแล้ว — ใต้ JobLock เดียวกับงานนี้ (ไม่เพิ่ม BackgroundService ใหม่ · F4 #6)
        var (codesDeleted, linksCleared) = await Accounting.Helpers.CustomerLineLinkClosure.PurgeAsync(db, DateTime.UtcNow, ct);
        if (codesDeleted + linksCleared > 0)
            _logger.LogInformation("PDPA retention purge: ลบรหัสผูก LINE หมดอายุ {Codes} · ล้าง LINE userId ของลิงก์ที่ปิดแล้ว {Links}", codesDeleted, linksCleared);

        // ฝ่ายค้านรอบ 212 S3: บันทึกการส่งข้อความถึงลูกค้า (กันส่งซ้ำ/ร่องรอย) เก็บ CustomerNotifyLogRule.RetentionDays วันแล้วลบจริง
        // (ไม่ใช่เอกสารบัญชี · ทุกบริษัท — งานเก็บกวาดใต้ JobLock เดียวกับงานนี้)
        var notifyCutoff = Accounting.Helpers.CustomerNotifyLogRule.PurgeCutoff(DateTime.UtcNow);
        var notifyDeleted = await db.CustomerNotifyLogs.IgnoreQueryFilters().Where(l => l.CreatedAt < notifyCutoff).ExecuteDeleteAsync(ct);
        if (notifyDeleted > 0) _logger.LogInformation("PDPA retention purge: ลบบันทึกการส่งข้อความถึงลูกค้าเกิน {Days} วัน {Rows} แถว",
            Accounting.Helpers.CustomerNotifyLogRule.RetentionDays, notifyDeleted);

        // รอบ 214 ต่อ (คำตัดสินข้อ 173 ฎ): หลักฐานยอมรับนโยบายของผู้ใช้ที่ลบตามคำขอแล้ว — พ้น legal hold (10 ปีหลังลบ) ⇒ ลบจริง
        // (บัญชียังอยู่ = ไม่แตะ · ตลอดอายุบัญชี) · ใต้ JobLock เดียวกับงานนี้
        var acceptCutoff = Accounting.Helpers.PlatformPolicyAcceptanceRules.PurgeCutoff(DateTime.UtcNow);
        var acceptsDeleted = await db.PlatformPolicyAcceptances.IgnoreQueryFilters()
            .Where(x => x.PseudonymisedAt != null && x.PseudonymisedAt < acceptCutoff).ExecuteDeleteAsync(ct);
        // ฝ่ายค้านรอบ 214 S1: ต้นฉบับเดิมใน PdpaConsentRecords (ระดับแพลตฟอร์ม) — กติกาเดียวกัน
        acceptsDeleted += await db.PdpaConsentRecords.IgnoreQueryFilters()
            .Where(c => c.CompanyId == Accounting.Helpers.PdpaPolicy.PlatformScopeCompanyId
                        && c.PseudonymisedAt != null && c.PseudonymisedAt < acceptCutoff)
            .ExecuteDeleteAsync(ct);
        if (acceptsDeleted > 0) _logger.LogInformation("PDPA retention purge: ลบหลักฐานยอมรับนโยบายที่พ้น legal hold {Rows} แถว", acceptsDeleted);

        // contact ที่ถูก anonymize + ติดธง retained (ผู้ใช้ขอลบแล้ว แต่ค้างเพราะ legal-hold)
        var retained = await db.Contacts
            .Where(c => !c.IsDeleted && c.Address == RetainedMarker)
            .Select(c => new { c.Id, c.CompanyId })
            .ToListAsync(ct);
        if (retained.Count == 0) return;

        var retentionCutoff = DateTime.UtcNow.AddYears(-5);
        var purged = 0;

        foreach (var r in retained)
        {
            // ยังมีเอกสารในงวด retention 5 ปี → คงไว้ต่อ (legal_hold ยังไม่หมด)
            var stillHeld = await db.Documents.AnyAsync(d =>
                d.ContactId == r.Id && !d.IsDeleted
                && d.DocumentDate > retentionCutoff, ct);
            if (stillHeld) continue;

            // พ้น retention ทุกใบแล้ว → ปิดงานลบที่ผู้ใช้ขอไว้ (ข้อมูลถูก anonymize ไปแล้ว)
            var contact = await db.Contacts.FirstOrDefaultAsync(c => c.Id == r.Id, ct);
            if (contact == null) continue;
            contact.IsDeleted = true;
            contact.Address = null;
            contact.UpdatedBy = "PdpaRetentionPurgeJob";
            purged++;
        }

        if (purged > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "PDPA retention purge: ปิดงานลบ (พ้น legal-hold 5 ปี) {Count} contact", purged);
        }
    }
}
