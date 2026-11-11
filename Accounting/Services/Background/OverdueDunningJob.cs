using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>ส่งหนังสือทวงหนี้ลูกค้าค้างชำระอัตโนมัติเป็น 3 ระดับ:
/// 30 days = Reminder (เบาๆ — friendly reminder)
/// 60 days = First Notice (คำเตือนชัด — จะมีดอกเบี้ย/ค่าปรับ)
/// 90 days = Final Notice (ส่งทนาย / ตัดหนี้สูญ)
///
/// runs ทุก 24 ชั่วโมง (defer 90s หลัง startup). idempotent:
/// ส่งใหม่ได้เฉพาะเมื่อ AgingDays ข้ามขั้น + ≥ 7 วัน นับจาก LastDunningSentAt
/// → กัน spam customer + log notification ครบใน audit trail.
///
/// คนรับ notification = role Accounting + Owner (ไม่ส่งหา customer ตรง ๆ —
/// admin ตัดสินใจส่ง email/LINE ต่อ ผ่าน NotificationEngine).</summary>
public class OverdueDunningJob : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<OverdueDunningJob> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    public OverdueDunningJob(IServiceProvider services, ILogger<OverdueDunningJob> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "OverdueDunningJob cycle failed"); }
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
            lockDb, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(OverdueDunningJob),
            () => RunAsync(ct), _logger, ct: ct);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var notify = scope.ServiceProvider.GetService<INotificationEngine>();

        var arTypes = new[] { DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote };
        var activeStatuses = new[] { DocumentStatus.Approved, DocumentStatus.Sent,
            DocumentStatus.PartiallyPaid, DocumentStatus.Overdue };

        // ขั้นต่ำ 30 วัน — ไม่ต้องดึงใบใหม่ที่ aging ยังไม่เข้าเกณฑ์
        var candidates = await db.Documents.AsNoTracking()
            .Where(d => !d.IsDeleted
                && arTypes.Contains(d.DocumentType)
                && activeStatuses.Contains(d.Status)
                && d.BalanceDue > 0.005m
                && d.AgingDays != null && d.AgingDays >= 30)
            .Select(d => new {
                d.Id, d.CompanyId, d.DocumentNumber, d.AgingDays, d.BalanceDue,
                d.LastDunningSentAt, d.LastDunningLevel, d.ContactId, d.CreditCoverNoticeAt
            })
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var today = DateTime.UtcNow.Date;
        var sent = 0;
        // รอบ 258 (DECISIONS 258-1-9): เงินรอจัดสรรต่อลูกค้า — โหลดครั้งเดียวต่อบริษัท (ตัวอ่านเดียว CustomerCreditQuery)
        var creditHoldsByCompany = new Dictionary<Guid, IReadOnlyDictionary<Guid, string>>();
        foreach (var c in candidates)
        {
            if (ct.IsCancellationRequested) return;

            // กำหนดระดับที่ควรอยู่ตาม aging
            int targetLevel = c.AgingDays >= 90 ? 3 : c.AgingDays >= 60 ? 2 : 1;
            int currentLevel = c.LastDunningLevel ?? 0;

            // ส่งใหม่เฉพาะถ้า "ข้ามขั้น" (currentLevel < targetLevel) + cooldown 7 วัน
            if (currentLevel >= targetLevel) continue;
            if (c.LastDunningSentAt.HasValue
                && (today - c.LastDunningSentAt.Value.Date).TotalDays < 7)
                continue;

            var contactName = await db.Contacts.AsNoTracking()
                .Where(x => x.Id == c.ContactId).Select(x => x.Name).FirstOrDefaultAsync(ct);

            // รอบ 258: ลูกค้ามีเงินรอจัดสรรพอปิดใบนี้ ⇒ ไม่ออกหนังสือทวง — แจ้งทีมให้จัดสรรแทน (ตัวตัดสินเดียว DunningSuppression)
            // ประทับเฉพาะ LastDunningSentAt (พัก 7 วันกันแจ้งซ้ำทุกรอบ) ไม่ขยับระดับทวง ⇒ เงินรอจัดสรรหมดเมื่อไร การทวงตามอายุหนี้เดินต่อจากระดับเดิม
            // ฝ่ายค้าน M1: แผนหักเงินสะสมเก่าสุดก่อน (DunningSuppression.Plan ผ่าน DunningHoldsAsync) — เฉพาะใบที่จัดสรรได้จริง
            if (!creditHoldsByCompany.TryGetValue(c.CompanyId, out var creditHolds))
                creditHoldsByCompany[c.CompanyId] = creditHolds =
                    await Accounting.Helpers.CustomerCreditQuery.DunningHoldsAsync(db, c.CompanyId, ct);
            if (creditHolds.TryGetValue(c.Id, out var creditHoldReason))
            {
                // ฝ่ายค้าน L2: แจ้งทีมครั้งเดียวต่อช่วงที่ใบถูกระงับ (CreditCoverNoticeAt) — ไม่แจ้งซ้ำทุก 7 วันไม่มีที่สิ้นสุด · ไม่แตะ LastDunningSentAt (ไม่ได้ส่งทวง)
                if (c.CreditCoverNoticeAt != null) continue;
                if (notify == null)
                {
                    _logger.LogWarning("ใบ {Doc} ถูกระงับทวงเพราะมีเงินรอจัดสรร แต่ไม่มีช่องแจ้งเตือนให้แจ้งทีม — รอบถัดไปลองใหม่", c.DocumentNumber);
                    continue;
                }
                try
                {
                    await notify.DispatchAsync(c.CompanyId, NotificationEvents.CustomerCreditCoversInvoice, new NotificationContext
                    {
                        Title = $"มีเงินรอจัดสรรพอปิดใบนี้: {c.DocumentNumber} — {contactName ?? "(ไม่ระบุ)"}",
                        Message = $"ใบค้าง {c.AgingDays} วัน ยอดค้าง ฿{c.BalanceDue:N2} · {creditHoldReason}",
                        ActionUrl = $"/pages/documents.html?id={c.Id}",
                        EntityType = "Document", EntityId = c.Id,
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "แจ้งทีมเรื่องเงินรอจัดสรรพอปิดใบ {DocId} ไม่สำเร็จ — รอบถัดไปแจ้งใหม่", c.Id);
                    continue;
                }
                var heldDoc = await db.Documents.FirstOrDefaultAsync(d => d.Id == c.Id && d.CompanyId == c.CompanyId, ct);
                if (heldDoc != null)
                {
                    heldDoc.CreditCoverNoticeAt = DateTime.UtcNow;
                    try { await db.SaveChangesAsync(ct); }
                    catch (Exception ex) { _logger.LogWarning(ex, "ประทับเวลาแจ้งเงินรอจัดสรรของใบ {DocId} ไม่สำเร็จ", c.Id); }
                }
                continue;
            }

            var eventKey = targetLevel switch
            {
                3 => NotificationEvents.OverdueDunningFinal,
                2 => NotificationEvents.OverdueDunningFirst,
                _ => NotificationEvents.OverdueDunningReminder,
            };
            var levelLabel = targetLevel switch
            {
                3 => "ครั้งสุดท้าย (90 วัน+) — แนะนำส่งทนาย/ตัดหนี้สูญ",
                2 => "รอบที่ 2 (60 วัน+) — แจ้งดอกเบี้ย/ค่าปรับ",
                _ => "รอบที่ 1 (30 วัน+) — friendly reminder",
            };

            // ไม่มี notifier ก็ยังส่งไม่ได้ — "อย่า" mark ว่าส่งแล้ว ไม่งั้นใบนี้จะ
            // ถูกข้ามถาวรที่ระดับนี้ทั้งที่ลูกค้าไม่เคยได้รับหนังสือทวง
            if (notify == null) continue;

            bool dispatched;
            try
            {
                await notify.DispatchAsync(c.CompanyId, eventKey, new NotificationContext
                {
                    Title = $"AR ค้าง {c.AgingDays} วัน: {c.DocumentNumber} — {contactName ?? "(ไม่ระบุ)"}",
                    Message = $"หนังสือทวงหนี้{levelLabel}. ยอดค้าง ฿{c.BalanceDue:N2}",
                    ActionUrl = $"/pages/documents.html?id={c.Id}",
                    EntityType = "Document", EntityId = c.Id,
                });
                dispatched = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Dunning notify failed for doc {DocId}", c.Id);
                dispatched = false;
            }

            // dispatch ล้ม → "อย่า" mark → รอบถัดไปทวงซ้ำได้ (ไม่ทิ้งหนี้เงียบ)
            if (!dispatched) continue;

            // mark + save ทันทีต่อใบ (idempotency) — เดิม dispatch ทั้ง batch ก่อน
            // แล้วค่อย SaveChanges ทีเดียวท้ายรอบ: ถ้า process ตายกลางลูป มาร์คที่ยัง
            // ไม่ persist หายหมด → รอบถัดไปส่งซ้ำทุกใบที่เพิ่งส่งไป (spam ลูกค้า).
            // save ต่อใบ ปิด window นี้ให้เหลือแค่ 1 ใบต่อการล่ม 1 ครั้ง
            var trackedDoc = await db.Documents.FirstOrDefaultAsync(d => d.Id == c.Id, ct);
            if (trackedDoc != null)
            {
                trackedDoc.LastDunningSentAt = DateTime.UtcNow;
                trackedDoc.LastDunningLevel = targetLevel;
                trackedDoc.CreditCoverNoticeAt = null;   // รอบ 258 L2: ทวงตามปกติแล้ว = พ้นช่วงระงับ (ถูกระงับอีกครั้งจะแจ้งทีมใหม่)
                try
                {
                    await db.SaveChangesAsync(ct);
                    sent++;
                }
                catch (Exception ex)
                { _logger.LogWarning(ex, "Dunning mark-sent save failed for doc {DocId}", c.Id); }
            }
        }

        if (sent > 0)
            _logger.LogInformation("OverdueDunningJob: dispatched {Count} notices", sent);
    }
}
