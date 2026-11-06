using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 254 WP-11 L4 (คำตอบเจ้าของ N8 · LINE_MULTICHANNEL_DESIGN §5.4) — เฝ้าต้นทุน <b>OA กลาง</b> ทุกชั่วโมง (ใต้ <see cref="JobLock"/> · ไม่รันซ้ำข้าม instance):
/// <list type="number">
/// <item>อ่านยอด push/โควตาของ OA กลางจาก LINE (<see cref="ILineNotifyService.TryReadNextAccQuotaAsync"/>) → เก็บในตัวนับ <c>push</c> ของงวด</item>
/// <item>ตัวนับ <c>reply</c> ของงวด (นับที่ <c>CustomerLineLinkService.ReplyAsync</c>) เทียบเกณฑ์ reply ฟรี (<see cref="NextAccOaQuotaRule.AssumedFreeRepliesPerMonth"/>)</item>
/// <item>ถึง <see cref="NextAccOaQuotaRule.WarnPercent"/>% ⇒ เตือนกลุ่มผู้ดูแลแพลตฟอร์มครั้งเดียวต่อเรื่องต่องวด (<see cref="ILineNotifyService.TryPushPlatformOpsAsync"/>)</item>
/// </list>
/// <b>ไม่ตัดโควตาผู้เช่า</b> · ไม่ส่งอะไรถึงผู้เช่า · LINE ไม่ตอบ/ยังไม่ตั้ง token ⇒ log แล้วรอรอบหน้า (ไม่ประทับว่าเตือนแล้ว)
/// </summary>
public class NextAccOaQuotaWatch : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NextAccOaQuotaWatch> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    public NextAccOaQuotaWatch(IServiceProvider services, ILogger<NextAccOaQuotaWatch> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(13), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var lockScope = _services.CreateScope();
                var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, nameof(NextAccOaQuotaWatch),
                    () => RunOnceAsync(_services, _logger, DateTime.UtcNow, stoppingToken), _logger, ct: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "NextAccOaQuotaWatch ล้ม — รอรอบหน้า");
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>หนึ่งรอบ (เทสต์เรียกตรงได้) — คืนข้อความเตือนที่ส่งจริงในรอบนี้</summary>
    internal static async Task<List<string>> RunOnceAsync(IServiceProvider services, ILogger logger, DateTime nowUtc, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var line = scope.ServiceProvider.GetRequiredService<ILineNotifyService>();
        var period = NextAccOaQuotaRule.PeriodOf(nowUtc);
        var sent = new List<string>();

        // 1) push ของ OA กลางตามที่ LINE รายงาน
        var reading = await line.TryReadNextAccQuotaAsync(ct);
        if (reading == null)
            logger.LogWarning("NextAccOaQuotaWatch: อ่านโควตา OA กลางจาก LINE ไม่ได้ (ยังไม่ตั้ง token หรือ LINE ไม่ตอบ) — ข้ามเรื่อง push รอบนี้");
        else
        {
            await NextAccOaUsage.SetAsync(db, period, NextAccOaQuotaRule.KindPush, reading.Used, reading.Limit, ct);
            if (NextAccOaQuotaRule.Crossed(reading.Used, reading.Limit))
                await AlertOnceAsync(db, line, logger, period, NextAccOaQuotaRule.KindPush,
                    NextAccOaQuotaRule.PushAlert(period, reading.Used, reading.Limit!.Value), sent, ct);
        }

        // 2) reply ของ OA กลาง (ตัวนับของเราเอง)
        var replies = await db.NextAccOaUsageCounters.AsNoTracking()
            .Where(c => c.Period == period && c.Kind == NextAccOaQuotaRule.KindReply).Select(c => (long?)c.Count).FirstOrDefaultAsync(ct) ?? 0;
        if (NextAccOaQuotaRule.Crossed(replies, NextAccOaQuotaRule.AssumedFreeRepliesPerMonth))
            await AlertOnceAsync(db, line, logger, period, NextAccOaQuotaRule.KindReply,
                NextAccOaQuotaRule.ReplyAlert(period, replies, NextAccOaQuotaRule.AssumedFreeRepliesPerMonth), sent, ct);
        return sent;
    }

    /// <summary>ส่งคำเตือนก่อน แล้วค่อยประทับ — ส่งไม่สำเร็จ ⇒ ไม่ประทับ (รอบหน้าลองใหม่ · ไม่เงียบ) · ประทับไม่ได้ (เครื่องอื่นประทับแล้ว) ⇒ ไม่ส่งซ้ำ</summary>
    private static async Task AlertOnceAsync(AccountingDbContext db, ILineNotifyService line, ILogger logger, string period, string kind, string message,
        List<string> sent, CancellationToken ct)
    {
        var already = await db.NextAccOaUsageCounters.AsNoTracking()
            .AnyAsync(c => c.Period == period && c.Kind == kind && c.WarnedAt != null, ct);
        if (already) return;
        var r = await line.TryPushPlatformOpsAsync(message);
        if (!r.Ok)
        {
            logger.LogError("NextAccOaQuotaWatch: ส่งคำเตือน {Kind} ถึงกลุ่มผู้ดูแลไม่สำเร็จ ({Error}) — {Message}", kind, r.Error, message);
            return;
        }
        if (await NextAccOaUsage.TryMarkWarnedAsync(db, period, kind, ct)) sent.Add(message);
    }
}
