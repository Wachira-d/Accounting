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
            try { await RunAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "OverdueDunningJob cycle failed"); }
            try { await Task.Delay(Interval, stoppingToken); } catch { return; }
        }
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
                d.LastDunningSentAt, d.LastDunningLevel, d.ContactId
            })
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var today = DateTime.UtcNow.Date;
        var sent = 0;
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

            if (notify != null)
            {
                try
                {
                    await notify.DispatchAsync(c.CompanyId, eventKey, new NotificationContext
                    {
                        Title = $"AR ค้าง {c.AgingDays} วัน: {c.DocumentNumber} — {contactName ?? "(ไม่ระบุ)"}",
                        Message = $"หนังสือทวงหนี้{levelLabel}. ยอดค้าง ฿{c.BalanceDue:N2}",
                        ActionUrl = $"/pages/documents.html?id={c.Id}",
                        EntityType = "Document", EntityId = c.Id,
                    });
                }
                catch (Exception ex)
                { _logger.LogWarning(ex, "Dunning notify failed for doc {DocId}", c.Id); }
            }

            // mark sent — ใช้ tracked entity เพื่อบันทึก
            var trackedDoc = await db.Documents.FirstOrDefaultAsync(d => d.Id == c.Id, ct);
            if (trackedDoc != null)
            {
                trackedDoc.LastDunningSentAt = DateTime.UtcNow;
                trackedDoc.LastDunningLevel = targetLevel;
                sent++;
            }
        }

        if (sent > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("OverdueDunningJob: dispatched {Count} notices", sent);
        }
    }
}
