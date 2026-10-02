using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// **Night audit ของที่พัก** — หาการจองที่เลยวันเช็คเอาต์แล้วยังค้าง แล้ว<b>ติดธง "ค้างปิด"</b> (LODGING_LICENSING_PLAN.md §13.2)
///
/// <para><b>รอบ 202 ทีม LO (O-P0-2 · คำตัดสินข้อ 119 · R1)</b>: รุ่นเดิมประทับ <c>CheckedOut</c>/<c>NoShow</c> เอง ⇒ การจองหลุดจากเส้น
/// เช็คเอาต์/ยกเลิกของโมดูล (ด่านสถานะปฏิเสธ) ⇒ ออกใบเช็คเอาต์ไม่ได้ · มัดจำค้างเป็นหนี้สินตลอดไป · ค่าปรับ no-show ไม่เคยถูกคิด —
/// "สถานะปลายทางที่ระบบประทับเองโดยไม่มีของจริงยืนยัน" · รุ่นนี้ <b>ไม่เปลี่ยนสถานะ ไม่แตะห้อง/เงิน/เอกสาร</b>:</para>
/// <list type="bullet">
///   <item>ติดธง <c>OverdueFlaggedAt</c> + หมายเหตุบอกทางไปต่อ <b>ครั้งเดียว</b>ต่อใบ (ตัวตัดสิน <c>Helpers/LodgingOverdueRule</c>)</item>
///   <item>CheckedIn ที่ค้าง = ห้องยังถูกกัน (ตัวนับห้องว่างอ่าน <c>LodgingHoldRule.EffectiveCheckOut</c>) และสถานะแม่บ้านคง "มีแขก" ⇒
///     พนักงานเห็นป้าย "ค้างปิด" ในรายการ แล้วกด "เช็คเอาต์ + ออกบิล" ตามปกติ</item>
///   <item>Confirmed/Pending ที่ไม่เคยเช็คอิน ⇒ พนักงานกด "No-show" (คิดค่าปรับ/ค้างคืนตามนโยบาย) หรือยกเลิก/เลื่อนวัน</item>
///   <item>มิเตอร์ <c>lodging.stay</c> นับเมื่อติดธง<b>เฉพาะใบที่พักจริง/ยืนยันแล้ว/มีมัดจำ</b> (เหตุผลเดิม: ไม่กดเช็คเอาต์ต้องไม่ทำให้ใช้ฟรี ·
///     Pending ที่ไม่มีเงิน = ไม่นับ ตรงกับเส้นยกเลิก) — idempotent ผ่าน MeteredPeriod + IdempotencyKey</item>
///   <item>ผลรอบ (ติดธงใหม่ · ค้างทั้งหมด) บันทึกลงสถานะงาน (<c>IJobRunRecorder</c>)</item>
/// </list>
/// <para>แถวที่รุ่นเดิมประทับไปแล้วไม่ถูกย้ายกลับ — ดูตัวกรอง "ปิดโดยระบบรุ่นเก่า" + ปุ่มออกใบย้อนหลัง/คิดค่าปรับในหน้าการจอง</para>
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
        var todayThai = DateTime.UtcNow.AddHours(7).Date;   // ปฏิทินไทย
        var flagCutoff = todayThai.AddDays(-LodgingOverdueRule.FlagGraceDays);

        // ค้างทั้งหมด (ทุกบริษัท) — ตัวเลขบนสถานะงาน · สถานะยังเปิดอยู่ ⇒ แถวไม่หายจากชุดนี้จนพนักงานปิด
        var outstanding = await db.LodgingReservations.AsNoTracking()
            .CountAsync(r => !r.IsDeleted && r.CheckOutDate < flagCutoff
                && (r.Status == LodgingReservationStatus.CheckedIn || r.Status == LodgingReservationStatus.Confirmed
                    || r.Status == LodgingReservationStatus.Pending), ct);

        // ติดธงเฉพาะใบที่ยังไม่เคยติด (OverdueFlaggedAt == null) — ไม่ต้องโหลด 500 ใบเดิมซ้ำทุกรอบ
        var stale = await db.LodgingReservations
            .Where(r => !r.IsDeleted && r.OverdueFlaggedAt == null
                     && r.CheckOutDate < flagCutoff
                     && (r.Status == LodgingReservationStatus.CheckedIn
                         || r.Status == LodgingReservationStatus.Confirmed
                         || r.Status == LodgingReservationStatus.Pending))
            .OrderBy(r => r.CheckOutDate)
            .Take(500)   // กันงานเดียวกินทั้งฐานเมื่อเปิดใช้ครั้งแรก
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var stays = 0; var arrivals = 0; var meterFailed = 0;
        foreach (var r in stale)
        {
            if (ct.IsCancellationRequested) break;
            var kind = LodgingOverdueRule.Classify(r.Status, r.CheckOutDate, todayThai);
            if (!LodgingOverdueRule.ShouldFlag(kind, r.CheckOutDate, todayThai, r.OverdueFlaggedAt)) continue;

            // ไม่แตะ Status · ไม่แตะห้อง/งานแม่บ้าน · ไม่แตะเงิน/เอกสาร — แค่ติดธง + หมายเหตุครั้งเดียว
            r.OverdueFlaggedAt = now;
            Append(r, LodgingOverdueRule.FlagNote(kind, r.CheckOutDate));
            if (kind == LodgingOverdueKind.StayNotCheckedOut) stays++; else arrivals++;

            // มิเตอร์: 1 การเข้าพัก = 1 หน่วย (กันซ้ำด้วย MeteredPeriod + IdempotencyKey) · มิเตอร์ล้มไม่ทำให้ธงหาย (นับเป็นตัวเลขบนสถานะงาน)
            // ฝ่ายค้านรอบ 202 P1-2: นับเฉพาะใบที่พักจริง/ยืนยันแล้ว/มีมัดจำ — Pending ที่ไม่มีเงินเลยต้องไม่ถูกคิด (กติกาเดียวกับ CancelCoreAsync:
            // จองแล้วยกเลิกฟรีก่อนจ่ายมัดจำไม่นับ) · ตัวตัดสิน LodgingOverdueRule.ShouldMeterOnFlag
            if (r.MeteredPeriod == null && metering != null && LodgingOverdueRule.ShouldMeterOnFlag(r.Status, r.DepositPaid))
            {
                try
                {
                    await metering.RecordAsync(new UsageRecordRequest(
                        CompanyId: r.CompanyId,
                        FeatureCode: Models.Constants.AddOnCodes.LodgingStay,
                        Quantity: 1,
                        IdempotencyKey: $"stay:{r.Id:N}",
                        RefEntityType: "LodgingReservation",
                        RefEntityId: r.Id), ct);
                    r.MeteredPeriod = AddOnBilling.PeriodOf(now);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    meterFailed++;
                    _logger.LogWarning(ex, "night audit: บันทึกมิเตอร์การเข้าพัก {No} ไม่สำเร็จ — ธงค้างปิดยังติด · นับให้ตอนพนักงานปิดการเข้าพัก (MeterStayAsync)", r.ReservationNumber);
                }
            }

            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = r.CompanyId,
                Action = AuditAction.Update,
                EntityType = "LodgingReservation",
                EntityId = r.Id.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    action = "NightAuditFlagOverdue",
                    reservationNumber = r.ReservationNumber,
                    status = r.Status.ToString(),   // ไม่เปลี่ยน — บันทึกไว้ให้เห็นว่า job ไม่ได้ปิดเอง
                    kind = kind.ToString(),
                    billed = r.FinalDocumentId != null,
                }),
            });
        }

        await db.SaveChangesAsync(ct);
        if (stays > 0 || arrivals > 0)
            _logger.LogInformation("Night audit: ติดธงค้างปิดใหม่ — ค้างเช็คเอาต์ {Stays} · ไม่มีการเช็คอิน {Arrivals} · ค้างทั้งหมด {Outstanding}",
                stays, arrivals, outstanding);
        if (recorder != null)
            await recorder.RecordAsync("LodgingNightAudit", meterFailed == 0,
                $"ติดธงค้างปิดใหม่ {stays + arrivals} (ค้างเช็คเอาต์ {stays} · ไม่มีการเช็คอิน {arrivals}) · ค้างปิดทั้งหมด {outstanding}"
                + (meterFailed > 0 ? $" · มิเตอร์ล้ม {meterFailed}" : "") + " — ระบบไม่ปิดสถานะเอง (พนักงานปิดที่หน้าที่พัก ตัวกรอง “ค้างปิด”)",
                stays + arrivals, startedAt);
    }

    private static void Append(LodgingReservation r, string note)
    {
        var stamp = DateTime.UtcNow.AddHours(7).ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var line = $"[{stamp}] {note}";
        r.InternalNotes = string.IsNullOrWhiteSpace(r.InternalNotes) ? line : r.InternalNotes.TrimEnd() + "\n" + line;
    }
}
