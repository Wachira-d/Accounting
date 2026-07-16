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
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "PdpaRetentionPurgeJob failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

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
