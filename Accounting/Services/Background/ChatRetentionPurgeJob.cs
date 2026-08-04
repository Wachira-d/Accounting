using Accounting.Data;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// PDPA retention ของ chatbot — ลบบทสนทนาที่เลยกำหนดเก็บจริง ๆ.
///
/// ทำไมต้องมี: ห้องแชท public เก็บข้อความที่ผู้เยี่ยมชมพิมพ์เอง ซึ่งมัก
/// หลุดข้อมูลส่วนตัวมาโดยไม่ตั้งใจ (ชื่อ เบอร์ อีเมล ชื่อบริษัท) + เราเก็บ
/// ชื่อ/อีเมลที่เขาฝากไว้ให้ติดต่อกลับ + IpHash. ตั้ง `PurgeAfter` ไว้ตอน
/// สร้างห้อง (90 วัน) แต่ **ถ้าไม่มีใครมาลบจริง ก็คือเก็บถาวร** = ผิด PDPA
/// "retention by purpose" (CLAUDE.md กฎเหล็ก #2 section J)
///
/// นโยบายการลบ (ตรงไปตรงมา ไม่กำกวม):
///   • ห้อง public ที่เลย PurgeAfter → **ลบข้อความทิ้งจริง** (hard delete)
///     แล้ว anonymize ตัวห้อง คงไว้เฉพาะสถิติที่ระบุตัวบุคคลไม่ได้
///     (จำนวนข้อความ/คะแนน/ช่วงเวลา) เพื่อใช้วัดคุณภาพบริการต่อได้
///   • ห้อง tenant ไม่ตั้ง PurgeAfter (เป็นบันทึกการทำงานของกิจการ) —
///     ลบผ่านเส้นทาง DSR ของบริษัทนั้นแทน job นี้จึงไม่แตะ
///   • ล้าง ChatRateBuckets เก่ากว่า 2 วันไปในรอบเดียวกัน (ตารางนับล้วน
///     ไม่มีคุณค่าย้อนหลัง ปล่อยไว้จะโตไม่หยุด)
///
/// รันทุก 24 ชม.
/// </summary>
public class ChatRetentionPurgeJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ChatRetentionPurgeJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public ChatRetentionPurgeJob(IServiceProvider services, ILogger<ChatRetentionPurgeJob> logger)
    {
        _services = services; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // หน่วงตอน start ให้ migration/seed เสร็จก่อน
        try { await Task.Delay(TimeSpan.FromMinutes(11), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "ChatRetentionPurgeJob failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();

        var now = DateTime.UtcNow;
        var expired = await db.ChatConversations
            .Where(c => c.Channel == "Public" && !c.IsDeleted
                && c.PurgeAfter != null && c.PurgeAfter < now)
            .Select(c => c.Id)
            .Take(2000)      // แบ่งรอบ — รอบถัดไปเก็บที่เหลือ ไม่ล็อกตารางยาว
            .ToListAsync(ct);

        var purgedMessages = 0;
        if (expired.Count > 0)
        {
            // ข้อความ = ส่วนที่มีเนื้อหาผู้ใช้พิมพ์ → ลบทิ้งจริง ไม่ใช่ soft-delete
            // (soft-delete = ยังเก็บอยู่ ซึ่งไม่ตอบโจทย์ PDPA)
            purgedMessages = await db.ChatMessages
                .Where(m => expired.Contains(m.ConversationId))
                .ExecuteDeleteAsync(ct);

            // ตัวห้อง: ล้างทุกฟิลด์ที่ระบุตัวบุคคลได้ แล้ว mark ว่าลบแล้ว
            await db.ChatConversations
                .Where(c => expired.Contains(c.Id))
                .ExecuteUpdateAsync(u => u
                    .SetProperty(c => c.VisitorName, (string?)null)
                    .SetProperty(c => c.VisitorEmail, (string?)null)
                    .SetProperty(c => c.IpHash, (string?)null)
                    .SetProperty(c => c.SessionToken, (string?)null)
                    .SetProperty(c => c.Title, "[ลบตามกำหนดเก็บข้อมูล]")
                    .SetProperty(c => c.PendingChallenge, (string?)null)
                    .SetProperty(c => c.Status, "Closed")
                    .SetProperty(c => c.IsDeleted, true)
                    .SetProperty(c => c.UpdatedAt, now), ct);
        }

        // ตารางนับ rate limit — ไม่มีคุณค่าย้อนหลัง
        var rate = scope.ServiceProvider.GetRequiredService<IChatRateLimiter>();
        var buckets = await rate.PurgeOldAsync(ct);

        if (expired.Count > 0 || buckets > 0)
            _logger.LogInformation(
                "Chat purge: {Conv} ห้อง / {Msg} ข้อความ / {Bucket} rate-bucket",
                expired.Count, purgedMessages, buckets);
    }
}
