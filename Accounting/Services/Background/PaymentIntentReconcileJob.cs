using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Accounting.Services.Payments;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// **ตาข่ายรับของ webhook** — ไล่ถามสถานะสดของรายการที่ยังรอจ่าย
///
/// ═══ ทำไมต้องมี (PAYMENT_GATEWAY_DESIGN.md ข้อสรุปทีม 7) ═══
/// webhook เป็นเส้นเร็ว <b>ไม่ใช่เส้นเดียว</b> — มันหายได้จริงหลายทาง: ผู้ให้บริการส่งพลาด ·
/// เซิร์ฟเวอร์เรารีสตาร์ตพอดี · ผู้ใช้ยังไม่ได้ตั้ง URL ในแดชบอร์ด · ไฟร์วอลล์กั้น ·
/// ถ้าไม่มีตัวนี้ อาการที่ผู้ใช้เจอคือ <b>"ลูกค้าจ่ายเงินแล้วแต่ออเดอร์ยังค้าง"</b> ซึ่ง
/// เจ้าของร้านไม่มีทางรู้จนลูกค้าโทรมา
///
/// ═══ สิ่งที่ทำทุกรอบ ═══
/// <list type="number">
/// <item>รายการที่ยังเปิดอยู่และเพิ่งถามไปนานพอ → ถามสถานะสด</item>
/// <item>รายการที่ QR/ลิงก์หมดอายุแล้ว → ปิดเป็น <c>Expired</c> (ไม่ต้องรบกวนผู้ให้บริการ)
///   — ทำใน <c>RefreshAsync</c> อยู่แล้ว</item>
/// <item>รายการ **โหมดใช้งานจริง** ที่ค้างเกิน 30 นาที → log ระดับ warning
///   (เงียบไว้ = ปัญหาถูกพบตอนปิดบัญชีสิ้นเดือนแทนที่จะเป็นวันนี้)</item>
/// </list>
///
/// <para><b>ล็อกข้าม instance</b> — หลาย instance รันงานนี้พร้อมกันจะยิงถามผู้ให้บริการ
/// ซ้ำโดยไม่จำเป็น (บางเจ้าจำกัดอัตราเรียก) · คีย์ต้อง deterministic ข้าม process
/// จึงใช้ <c>AdvisoryLockKey</c> ไม่ใช่ <c>HashCode.Combine</c></para>
/// </summary>
public class PaymentIntentReconcileJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PaymentIntentReconcileJob> _logger;

    private static readonly TimeSpan Cycle = TimeSpan.FromMinutes(5);
    /// <summary>เว้นระยะก่อนถามซ้ำ — ถามถี่กว่านี้ไม่ช่วยอะไร (ลูกค้าใช้เวลาสแกนจ่ายเป็นนาที)
    /// และเปลืองโควตาเรียก API ของผู้ให้บริการ</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    /// <summary>ค้างนานกว่านี้ในโหมดใช้งานจริง = ต้องมีคนดู</summary>
    private static readonly TimeSpan StuckThreshold = TimeSpan.FromMinutes(30);
    /// <summary>จำกัดต่อรอบ — กันงานเดียวยึดฐานข้อมูล/โควตา API ทั้งหมดตอนมีคิวยาว</summary>
    private const int MaxPerCycle = 200;

    public PaymentIntentReconcileJob(IServiceScopeFactory scopeFactory,
        ILogger<PaymentIntentReconcileJob> logger)
    { _scopeFactory = scopeFactory; _logger = logger; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // หน่วงตอนเริ่ม — ให้ระบบขึ้นให้เสร็จก่อน (migration/seed) แล้วค่อยเริ่มยิง
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "PaymentIntentReconcileJob รอบนี้ล้มเหลว"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var intents = scope.ServiceProvider.GetRequiredService<IPaymentIntentService>();
        // แจ้งเตือนคนจริง — log ของเซิร์ฟเวอร์ไม่ใช่ช่องทางแจ้งผู้ใช้ (กฎเหล็ก #4 E:
        // "ดังพอ" ต้องดังในที่ที่คนดู) · optional เพราะ engine อาจไม่ได้ register
        var notify = scope.ServiceProvider.GetService<Accounting.Services.Interfaces.INotificationEngine>();

        // **จอง**แถวด้วยคำสั่งเดียว แทนการล็อกรอบนอกแล้ว SELECT เฉย ๆ
        //
        // ⚠️ ที่มา (ผลตรวจทีม G · G-04): เดิมเปิด transaction + `pg_advisory_xact_lock`
        // แล้ว **commit ทิ้งก่อนลูปเริ่ม** — ล็อกแบบ xact ปล่อยตอน commit และการ
        // SELECT ไม่ได้ mark แถวเลย (`LastPolledAt` ยังไม่ขยับ) ⇒ ทุก instance
        // เลือกได้ **ชุดเดียวกันเป๊ะ** (เรียงด้วย `LastPolledAt` เหมือนกัน) แล้วยิง
        // provider ซ้ำทั้งชุดทุก 5 นาที — โควตาและค่าบริการเป็นของเรา
        //
        // รูปนี้ลอกจาก `EmailScheduleService.ProcessPendingQueueAsync`: CTE +
        // `FOR UPDATE SKIP LOCKED` + `RETURNING` ที่ set `LastPolledAt` ในคำสั่ง
        // เดียว ⇒ สองเครื่องได้คนละชุดโดยไม่ต้องรอกัน
        var cutoff = DateTime.UtcNow - PollInterval;
        var claimed = await db.PaymentIntents.FromSqlRaw(
            """
            WITH picked AS (
                SELECT "Id" FROM "PaymentIntents"
                WHERE "Status" IN ({3}, {4})
                  AND ("LastPolledAt" IS NULL OR "LastPolledAt" < {0})
                ORDER BY "LastPolledAt" NULLS FIRST
                LIMIT {1}
                FOR UPDATE SKIP LOCKED
            )
            UPDATE "PaymentIntents" p SET "LastPolledAt" = {2}
            FROM picked WHERE p."Id" = picked."Id"
            RETURNING p.*;
            """, cutoff, MaxPerCycle, DateTime.UtcNow,
            (int)PaymentIntentStatus.Created, (int)PaymentIntentStatus.Pending)
            .AsNoTracking()
            .ToListAsync(ct);

        var due = claimed
            .Select(i => new { i.Id, i.CompanyId, i.ProviderCode, i.CreatedAt, i.ProviderConfigId, i.Amount })
            .ToList();

        if (due.Count == 0) return;

        var liveConfigs = await db.PaymentProviderConfigs.AsNoTracking()
            .Where(c => c.Mode == PaymentProviderMode.Live)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var stuck = 0;
        foreach (var row in due)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                var updated = await intents.RefreshAsync(row.CompanyId, row.Id, ct);

                // ค้างนานในโหมดจริง = เงินของลูกค้าอาจค้างอยู่ ต้องมีคนดู
                if (PaymentIntentPolicy.IsOpen(updated.Status)
                    && row.ProviderConfigId is Guid cid && liveConfigs.Contains(cid)
                    && DateTime.UtcNow - row.CreatedAt > StuckThreshold)
                {
                    stuck++;
                    _logger.LogWarning(
                        "รายการชำระเงินค้างเกิน {Minutes} นาที: {Intent} บริษัท {Company} "
                        + "ยอด {Amount:N2} ผ่าน {Provider} — ตรวจว่าตั้ง webhook URL ไว้ถูกหรือยัง",
                        StuckThreshold.TotalMinutes, row.Id, row.CompanyId, row.Amount, row.ProviderCode);

                    // แจ้ง **ครั้งเดียวต่อรายการ** — job เดินทุก 5 นาที ถ้าแจ้งทุกรอบ
                    // ผู้ใช้จะได้ 12 ข้อความ/ชั่วโมงต่อ 1 รายการ แล้วเลิกอ่านทั้งหมด
                    // (การแจ้งที่ถี่เกินจนถูกเมินมีค่าเท่ากับไม่แจ้ง) · ใช้ประวัติของ
                    // intent เป็นตัวจำ ไม่ต้องมีคอลัมน์/แคชใหม่
                    await NotifyStuckOnceAsync(db, notify, row.CompanyId, row.Id,
                        row.Amount, row.ProviderCode, ct);
                }
            }
            catch (Exception ex)
            {
                // รายการเดียวล้มต้องไม่ทำให้ทั้งรอบตาย — ตัวถัดไปอาจสำเร็จ
                _logger.LogWarning(ex, "ตรวจสถานะรายการชำระเงิน {Intent} ไม่สำเร็จ", row.Id);
            }
        }

        _logger.LogInformation(
            "ตรวจสถานะรายการชำระเงิน {Count} รายการ · ค้างนานผิดปกติ {Stuck} รายการ",
            due.Count, stuck);
    }

    /// <summary>หมายเหตุที่ใช้เป็น "เคยแจ้งไปแล้ว" — canonical string เดียว
    /// ใช้ทั้งตอนเขียนและตอนเช็ค (ห้ามเขียน format สองที่ ไม่งั้นเช็คไม่มีวันเจอ)</summary>
    private const string StuckNotifiedNote = "แจ้งเตือนรายการค้างแล้ว";

    private async Task NotifyStuckOnceAsync(AccountingDbContext db,
        Accounting.Services.Interfaces.INotificationEngine? notify,
        Guid companyId, Guid intentId, decimal amount, string providerCode, CancellationToken ct)
    {
        var alreadyNotified = await db.PaymentIntentEvents.AsNoTracking()
            .AnyAsync(e => e.IntentId == intentId && e.Note != null
                && e.Note.Contains(StuckNotifiedNote), ct);
        if (alreadyNotified) return;

        db.PaymentIntentEvents.Add(new Accounting.Models.Entities.PaymentIntentEvent
        {
            CompanyId = companyId,
            IntentId = intentId,
            At = DateTime.UtcNow,
            Source = PaymentEventSource.System,
            ToStatus = PaymentIntentStatus.Pending,
            Note = $"{StuckNotifiedNote} (ค้างเกิน {StuckThreshold.TotalMinutes:0} นาที)",
        });
        await db.SaveChangesAsync(ct);

        if (notify == null) return;
        try
        {
            await notify.DispatchAsync(companyId,
                Accounting.Models.Constants.NotificationEvents.GatewayPaymentStuck,
                new Accounting.Services.Interfaces.NotificationContext
                {
                    Title = "รับชำระออนไลน์ค้างนานผิดปกติ",
                    Message = $"มีรายการรับชำระ {amount:N2} บาท ผ่าน {providerCode} "
                        + $"ค้างเกิน {StuckThreshold.TotalMinutes:0} นาที — "
                        + "สาเหตุที่พบบ่อยสุดคือยังไม่ได้ตั้ง Webhook URL ในแดชบอร์ดของผู้ให้บริการ "
                        + "(ถ้าลูกค้าจ่ายแล้วจริง ให้เปิดหน้ารายการรับชำระออนไลน์แล้วกด \"ตรวจสถานะสด\")",
                    ActionUrl = "/pages/payment-intents.html",
                    EntityType = "PaymentIntent", EntityId = intentId,
                });
        }
        catch (Exception ex)
        {
            // แจ้งเตือนล้มต้องไม่ทำให้ job ตาย — แต่ต้องรู้ว่าล้ม
            _logger.LogWarning(ex, "ส่งแจ้งเตือนรายการชำระเงินค้าง {Intent} ไม่สำเร็จ", intentId);
        }
    }
}
