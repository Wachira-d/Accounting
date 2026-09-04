using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// **Night audit ของที่พัก** — ปิดการเข้าพักที่เลยวันเช็คเอาต์แล้วยังค้าง
/// (LODGING_LICENSING_PLAN.md §13.2)
///
/// ทำไมต้องมี: มิเตอร์ของโมดูลที่พักคือ "การเข้าพักที่ปิดสถานะ" ⇒ ถ้าไม่มีอะไร
/// ปิดให้ ผู้ใช้ที่ไม่อยากถูกนับก็แค่**ไม่กดเช็คเอาต์** แล้วใช้ระบบฟรีตลอดไป
/// (ห้องยังถูกกันในปฏิทินด้วย ซึ่งผิดกับความจริงหน้างานอยู่แล้ว) — งานนี้ทำให้
/// มิเตอร์เดินตามความจริง โดย**ไม่บล็อกอะไรเลย**: แค่เปลี่ยนสถานะ + ติดหมายเหตุ
/// ว่ายังไม่ได้ออกบิล แล้วให้ front desk ตามออกเอกสารย้อนหลัง (ซึ่งออกได้เสมอ
/// ตามกฎ "ห้ามบล็อกเอกสารที่กฎหมายบังคับ")
///
/// เกณฑ์: `CheckOutDate + GraceDays` ผ่านไปแล้วและสถานะยังเป็น CheckedIn/Confirmed
///   • CheckedIn → CheckedOut (แขกออกไปแล้วแน่ ๆ) + งานแม่บ้าน + ปลดห้อง
///   • Confirmed ที่ไม่เคยเช็คอิน → NoShow (ตามนโยบาย no-show ของที่พัก)
/// ทั้งสองกรณีนับมิเตอร์ 1 หน่วย และ**ไม่แตะเงิน/เอกสาร** — การคิดค่าปรับ/คืนเงิน
/// ต้องมีคนตัดสิน ไม่ใช่ job (บทเรียน "ค่าที่แต่งขึ้นอันตรายกว่าการไม่ตอบ")
/// </summary>
public class LodgingNightAuditJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LodgingNightAuditJob> _logger;
    private static readonly TimeSpan Cycle = TimeSpan.FromHours(6);

    /// <summary>ผ่อนให้กี่วันหลังวันเช็คเอาต์ก่อนถือว่า "ค้าง" — 2 วันเผื่อที่พัก
    /// ที่ปิดบิลวันถัดไป/สุดสัปดาห์ (สั้นกว่านี้จะไปปิดของที่ยังทำงานอยู่จริง)</summary>
    private const int GraceDays = 2;

    public LodgingNightAuditJob(IServiceScopeFactory scopeFactory, ILogger<LodgingNightAuditJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "LodgingNightAuditJob cycle failed"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    /// <summary>กันสอง instance ทำงานรอบเดียวกันพร้อมกัน (ผลตรวจ F-09)
    ///
    /// <para>ใช้ <b>try</b> ไม่ใช่ wait — งานตามตารางที่อีกเครื่องกำลังทำอยู่
    /// การรอคือทำงานเดิมซ้ำเปล่า ๆ ข้ามไปรอบหน้าถูกกว่า</para></summary>
    private async Task RunGuardedAsync(CancellationToken ct)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await Accounting.Helpers.JobLock.RunExclusiveAsync(
            lockDb, Accounting.Helpers.AdvisoryLockKey.BackgroundJob, nameof(LodgingNightAuditJob),
            () => RunOnce(ct), _logger, ct: ct);
    }

    private async Task RunOnce(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var metering = scope.ServiceProvider.GetService<IUsageMeteringService>();
        var recorder = scope.ServiceProvider.GetService<IJobRunRecorder>();

        var startedAt = DateTime.UtcNow;
        var cutoff = DateTime.UtcNow.AddHours(7).Date.AddDays(-GraceDays);   // ปฏิทินไทย

        var stale = await db.LodgingReservations
            .Include(r => r.Property)
            .Include(r => r.Rooms).ThenInclude(x => x.Unit)
            .Where(r => !r.IsDeleted
                     && r.CheckOutDate < cutoff
                     && (r.Status == LodgingReservationStatus.CheckedIn
                         || r.Status == LodgingReservationStatus.Confirmed))
            .Take(500)   // กันงานเดียวกินทั้งฐานเมื่อเปิดใช้ครั้งแรก
            .ToListAsync(ct);
        if (stale.Count == 0)
        {
            if (recorder != null)
                await recorder.RecordAsync("LodgingNightAudit", true, "ไม่มีการจองค้าง", 0, startedAt);
            return;
        }

        var now = DateTime.UtcNow;
        var closed = 0; var noShow = 0;
        foreach (var r in stale)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var wasCheckedIn = r.Status == LodgingReservationStatus.CheckedIn;
                if (wasCheckedIn)
                {
                    r.Status = LodgingReservationStatus.CheckedOut;
                    r.CheckedOutAt = now;
                    closed++;
                    Append(r, $"ระบบปิดการเข้าพักอัตโนมัติ (เลยวันเช็คเอาต์ {r.CheckOutDate:dd/MM/yyyy} เกิน {GraceDays} วัน)"
                        + (r.FinalDocumentId == null ? " — **ยังไม่ได้ออกบิล** กรุณาออกเอกสารย้อนหลังที่หน้ารายละเอียดการจอง" : ""));
                    foreach (var room in r.Rooms.Where(x => x.Unit != null))
                    {
                        room.Unit!.HousekeepingStatus = LodgingHousekeepingStatus.VacantDirty;
                        if (r.Property.AutoCreateHousekeepingTaskOnCheckout)
                            db.LodgingHousekeepingTasks.Add(new LodgingHousekeepingTask
                            {
                                CompanyId = r.CompanyId, PropertyId = r.PropertyId, UnitId = room.Unit.Id, ReservationId = r.Id,
                                TaskType = LodgingHousekeepingTaskType.CheckoutClean, Priority = LodgingTaskPriority.Normal,
                                Status = LodgingTaskStatus.Pending, DueAt = now,
                                EstimatedMinutes = r.Property.HousekeepingMinutesPerRoom, CreatedBy = "system:night-audit",
                            });
                    }
                }
                else
                {
                    // ยืนยันแล้วแต่ไม่เคยเช็คอิน จนเลยวันออก = ไม่มา
                    r.Status = LodgingReservationStatus.NoShow;
                    r.CancelledAt = now;
                    r.CancellationReason = "ระบบบันทึกอัตโนมัติ: ไม่มาเข้าพักและไม่มีการเช็คอิน";
                    noShow++;
                    Append(r, "ระบบบันทึกเป็นไม่มาเข้าพัก (no-show) อัตโนมัติ — "
                        + "ค่าปรับ/การคืนเงินตามนโยบายต้องทำด้วยมือที่หน้ารายละเอียดการจอง");
                    foreach (var room in r.Rooms) room.UnitId = null;
                }

                // มิเตอร์: 1 การเข้าพัก = 1 หน่วย (กันซ้ำด้วย MeteredPeriod + IdempotencyKey)
                if (r.MeteredPeriod == null)
                {
                    r.MeteredPeriod = AddOnBilling.PeriodOf(now);
                    if (metering != null)
                        await metering.RecordAsync(new UsageRecordRequest(
                            CompanyId: r.CompanyId,
                            FeatureCode: Models.Constants.AddOnCodes.LodgingStay,
                            Quantity: 1,
                            IdempotencyKey: $"stay:{r.Id:N}",
                            RefEntityType: "LodgingReservation",
                            RefEntityId: r.Id), ct);
                }

                db.AuditLogs.Add(new AuditLog
                {
                    CompanyId = r.CompanyId,
                    Action = AuditAction.Update,
                    EntityType = "LodgingReservation",
                    EntityId = r.Id.ToString(),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        action = "NightAudit",
                        reservationNumber = r.ReservationNumber,
                        from = wasCheckedIn ? "CheckedIn" : "Confirmed",
                        to = r.Status.ToString(),
                        billed = r.FinalDocumentId != null,
                    }),
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "night audit การจอง {No} ไม่สำเร็จ", r.ReservationNumber);
            }
        }

        await db.SaveChangesAsync(ct);
        if (closed > 0 || noShow > 0)
            _logger.LogInformation("Night audit: ปิดการเข้าพักค้าง {Closed} · บันทึก no-show {NoShow}", closed, noShow);
        if (recorder != null)
            await recorder.RecordAsync("LodgingNightAudit", true,
                $"ปิดการเข้าพักค้าง {closed} · no-show {noShow}", closed + noShow, startedAt);
    }

    private static void Append(LodgingReservation r, string note)
    {
        var stamp = DateTime.UtcNow.AddHours(7).ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var line = $"[{stamp}] {note}";
        r.InternalNotes = string.IsNullOrWhiteSpace(r.InternalNotes) ? line : r.InternalNotes.TrimEnd() + "\n" + line;
    }
}
