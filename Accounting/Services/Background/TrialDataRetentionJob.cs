using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Accounting.Services.Implementations.Pdpa;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Background;

/// <summary>
/// รอบ 231 (คำตัดสินข้อ 231-10 · LICENSE_USAGE_OVERAGE_AUDIT §A.8 "เก็บ/ลบข้อมูลหลังหมดอายุ" · ฝ่ายค้าน B2) — ปิดข้อมูลของบริษัท<b>ทดลองใช้</b>ที่หมดอายุแล้วพ้นระยะเก็บ
/// ผู้อ่านของ <c>TrialConfig.DeleteDataAfterGracePeriod / DataRetentionDays / GracePeriodDays / PurgeNoticeDays</c> (เดิมไม่มีผู้อ่าน — F2 ข้อ 2) · ตัวตัดสิน pure <see cref="TrialDataRetentionPolicy"/> ·
/// รายวัน · <c>Helpers/JobLock</c> (F4 ข้อ 6) · ผลทุกรอบลง <c>JobRunLog</c> (นับ ผ่าน/แจ้ง/ปิด/legal hold — ใช้เป็น dry-run report ได้แม้ไม่มีใครถูกปิด)
///
/// <para><b>ขั้นตอน:</b> ① กรองในฐาน (Kind=Trial · Expired · เปิดสวิตช์ · <c>EndDate &lt; now</c>) แล้ว<b>ไล่ทั้งชุด</b>ด้วย keyset paging (ไม่ Take ก่อนกรอง) ·
/// ② ข้อเท็จจริงต่อบริษัท (เคยจ่าย · งานค้าง · legal hold 6 เหตุ) → <see cref="TrialDataRetentionPolicy.Decide"/> ·
/// ③ <c>NoticeDue</c> ⇒ แจ้งเจ้าของทุกคน + ประวัติ <c>TrialDataPurgeNoticeSent</c> + audit + ประทับ <c>PurgeNoticeSentAt</c> (ครั้งเดียว) ·
/// ④ <c>Purge</c> ⇒ anonymize ผู้ติดต่อทุกรายผ่าน <see cref="IPdpaService.ApplyErasureAsync"/> (เส้นเดียวกับ DSR erase — กฎ retention 5 ปีของเอกสารอยู่ในนั้น) +
/// soft-delete บริษัท + ลบช่องติดต่อบริษัท + ประวัติ <c>TrialDataPurged</c> + audit chain (RuleCode/LegalReference) · <b>ไม่ DELETE แถว</b> ·
/// ผู้ใช้/พนักงาน/ไฟล์ <b>ไม่แตะ</b> ในรอบนี้ (เส้น user ของ PDPA ต้องผ่านคำขอของเจ้าของข้อมูลเอง — ผู้ใช้อาจเป็นสมาชิกบริษัทอื่น · จดใน DECISIONS 231-10) ·
/// legal hold / เคยจ่าย / ไม่เปิดสวิตช์ ⇒ ไม่แตะ — deploy แล้วไม่มีใครถูกลบโดยไม่ได้ตั้ง</para>
/// </summary>
public class TrialDataRetentionJob : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TrialDataRetentionJob> _logger;
    private static readonly TimeSpan Cycle = TimeSpan.FromHours(24);
    public const string Actor = "system:trial-retention";
    public const string JobName = nameof(TrialDataRetentionJob);
    private const int PageSize = 200;

    public TrialDataRetentionJob(IServiceScopeFactory scopeFactory, ILogger<TrialDataRetentionJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(11), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunGuardedAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "TrialDataRetentionJob cycle failed"); }
            try { await Task.Delay(Cycle, stoppingToken); } catch { return; }
        }
    }

    private async Task RunGuardedAsync(CancellationToken ct)
    {
        using var lockScope = _scopeFactory.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        await JobLock.RunExclusiveAsync(lockDb, AdvisoryLockKey.BackgroundJob, JobName,
            async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
                var pdpa = scope.ServiceProvider.GetService<IPdpaService>();
                var notifications = scope.ServiceProvider.GetService<INotificationService>();
                var started = DateTime.UtcNow;
                var report = await RunOnceAsync(db, started, _logger, pdpa, notifications, ct);
                db.JobRunLogs.Add(new JobRunLog
                {
                    JobName = JobName, StartedAt = started, FinishedAt = DateTime.UtcNow, Success = true,
                    Message = report.Summary, ItemsProcessed = report.Scanned, DurationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds,
                });
                await db.SaveChangesAsync(ct);
            }, _logger, ct: ct);
    }

    /// <summary>ผลรอบเดียว — dry-run report: ทุกบริษัทที่สแกนถูกจำแนกครบ แม้ไม่มีใครถูกปิด</summary>
    public sealed record RunReport(int Scanned, int NotEligible, int NotYetDue, int LegalHold, int NoticeSent, int AwaitingNotice, int Purged)
    {
        public string Summary => $"สแกน {Scanned} · ไม่เข้าเงื่อนไข {NotEligible} · ยังไม่ครบ {NotYetDue} · legal hold {LegalHold} · แจ้งเจ้าของรอบนี้ {NoticeSent} · รอครบวันแจ้ง {AwaitingNotice} · ปิดข้อมูล {Purged}";
    }

    /// <summary>รอบเดียว (เทสต์ Db เรียกตรง) — ตัดสินทุกแถวด้วย <see cref="TrialDataRetentionPolicy.Decide"/> เท่านั้น · <paramref name="pdpa"/> null = ไม่ anonymize ผู้ติดต่อ (เทสต์หน่วย) ·
    /// <paramref name="notifications"/> null = ประทับ PurgeNoticeSentAt + ประวัติ แต่ไม่ส่งแจ้งเตือน</summary>
    public static async Task<RunReport> RunOnceAsync(AccountingDbContext db, DateTime nowUtc, ILogger? logger,
        IPdpaService? pdpa = null, INotificationService? notifications = null, CancellationToken ct = default)
    {
        int scanned = 0, notEligible = 0, notYetDue = 0, legalHold = 0, noticeSent = 0, awaiting = 0, purged = 0;
        DateTime lastEnd = DateTime.MinValue; Guid lastId = Guid.Empty;
        while (true)
        {
            // กรองในฐาน + keyset paging (EndDate, Id) — ไล่ทั้งชุด ไม่ตัด 200 แถวแรกทิ้ง
            var page = await db.Subscriptions
                .Include(s => s.TrialConfig)
                .Where(s => s.Kind == SubscriptionKind.Trial && s.Status == SubscriptionStatus.Expired && !s.IsPermanentFree && !s.IsDeleted
                    && s.TrialConfig != null && s.TrialConfig.DeleteDataAfterGracePeriod && s.EndDate < nowUtc
                    && (s.EndDate > lastEnd || (s.EndDate == lastEnd && s.Id.CompareTo(lastId) > 0)))
                .OrderBy(s => s.EndDate).ThenBy(s => s.Id)
                .Take(PageSize).ToListAsync(ct);
            if (page.Count == 0) break;
            foreach (var sub in page)
            {
                lastEnd = sub.EndDate; lastId = sub.Id;
                scanned++;
                var tc = sub.TrialConfig!;
                var company = await db.Companies.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == sub.CompanyId, ct);
                if (company == null) { notEligible++; continue; }
                var cid = sub.CompanyId;
                // นิยาม "ไม่เคยจ่าย" เดียวกับ SubscriptionKindRule (ฝ่ายค้าน B1 ข)
                var everPaid = !SubscriptionKindRule.NeverPaid(sub.LastPaymentDate,
                    await db.SubscriptionPayments.AsNoTracking().AnyAsync(p => p.SubscriptionId == sub.Id && p.Status == SubscriptionPaymentStatus.Approved, ct));
                var openBilling = await db.SubscriptionPayments.AsNoTracking()
                    .AnyAsync(p => p.SubscriptionId == sub.Id && (p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview), ct);
                var hold = new TrialDataRetentionPolicy.LegalHoldFacts(
                    HasIssuedDocuments: await db.Documents.AsNoTracking().IgnoreQueryFilters().AnyAsync(d => d.CompanyId == cid && d.Status != DocumentStatus.Draft, ct),
                    HasPostedJournals: await db.JournalEntries.AsNoTracking().IgnoreQueryFilters().AnyAsync(j => j.CompanyId == cid && j.Status == JournalEntryStatus.Posted, ct),
                    HasFiledTaxReturns: await db.Set<ComplianceFiling>().AsNoTracking().IgnoreQueryFilters()
                        .AnyAsync(f => f.CompanyId == cid && (f.Status == "Filed" || f.Status == "Accepted" || f.FiledDate != null), ct),
                    HasSubmittedEtax: await db.Set<EtaxInvoice>().AsNoTracking().IgnoreQueryFilters().AnyAsync(x => x.CompanyId == cid && x.SubmittedAt != null, ct),
                    HasIssuedWhtCerts: await db.Set<WithholdingTaxCert>().AsNoTracking().IgnoreQueryFilters()
                        .AnyAsync(w => w.CompanyId == cid && w.Status != Models.DTOs.Tax.WithholdingTaxCertStatus.Draft, ct),
                    HasPaidPayrollRuns: await db.Set<PayrollRun>().AsNoTracking().IgnoreQueryFilters().AnyAsync(r => r.CompanyId == cid && r.Status == "Paid", ct));
                var decision = TrialDataRetentionPolicy.Decide(new TrialDataRetentionPolicy.Facts(
                    sub.Kind, sub.Status, everPaid, tc.DeleteDataAfterGracePeriod, tc.GracePeriodDays, tc.DataRetentionDays, sub.EndDate,
                    hold, company.IsDeleted, tc.PurgeNoticeDays, tc.PurgeNoticeSentAt, openBilling), nowUtc);
                switch (decision.Outcome)
                {
                    case TrialDataRetentionPolicy.Outcome.NotEligible: notEligible++; continue;
                    case TrialDataRetentionPolicy.Outcome.NotYetDue: notYetDue++; continue;
                    case TrialDataRetentionPolicy.Outcome.NoticeSent: awaiting++; continue;
                    case TrialDataRetentionPolicy.Outcome.LegalHold:
                        legalHold++;
                        logger?.LogInformation("TrialDataRetention: บริษัท {CompanyId} พ้นระยะเก็บแต่ติด legal hold — ไม่ลบ ({Why})", cid, decision.Reason);
                        continue;
                    case TrialDataRetentionPolicy.Outcome.NoticeDue:
                        await SendNoticeAsync(db, sub, tc, company, decision, nowUtc, notifications, logger, ct);
                        noticeSent++;
                        continue;
                    case TrialDataRetentionPolicy.Outcome.Purge:
                        break;   // ปิดข้อมูลด้านล่าง — ทางเดียวที่ไปถึง
                    default:
                        notEligible++;
                        continue;
                }
                // Purge — ผู้ติดต่อผ่านเส้น PDPA เดิม (ตัวเดียวกับ DSR) ก่อน แล้วค่อยปิดบริษัท
                if (pdpa != null)
                {
                    var contactIds = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
                        .Where(c => c.CompanyId == cid && !c.IsDeleted).Select(c => c.Id).ToListAsync(ct);
                    foreach (var contactId in contactIds)
                        await pdpa.ApplyErasureAsync(cid, null, contactId, Actor, ct);
                }
                company.IsDeleted = true;
                company.Email = null;
                company.Phone = null;
                company.UpdatedAt = nowUtc;
                company.UpdatedBy = Actor;
                sub.Status = SubscriptionStatus.Cancelled;
                sub.CancelledAt = nowUtc;
                sub.UpdatedAt = nowUtc;
                sub.UpdatedBy = Actor;
                db.SubscriptionHistories.Add(new SubscriptionHistory
                {
                    SubscriptionId = sub.Id, Action = "TrialDataPurged", FromStatus = SubscriptionStatus.Expired, ToStatus = SubscriptionStatus.Cancelled,
                    Notes = $"ปิดข้อมูลทดลองใช้หลังพ้นระยะเก็บ (ผ่อนผัน {tc.GracePeriodDays} วัน + เก็บ {tc.DataRetentionDays} วัน · แจ้งล่วงหน้า {tc.PurgeNoticeDays} วัน เมื่อ {tc.PurgeNoticeSentAt:yyyy-MM-dd} · ปิดได้ตั้งแต่ {decision.DueAtUtc:yyyy-MM-dd})"
                            + $" — {TrialDataRetentionPolicy.RuleCode} · ผู้ติดต่อ anonymize ผ่าน PDPA erase{(pdpa == null ? " (ข้าม — ไม่มี IPdpaService)" : "")}",
                    PerformedBy = Actor,
                });
                db.AddChainedAuditLog(new AuditLog
                {
                    CompanyId = cid, UserEmail = Actor, Action = AuditAction.Delete, EntityType = nameof(Company), EntityId = company.Id.ToString(),
                    OldValues = System.Text.Json.JsonSerializer.Serialize(new { isDeleted = false, subscriptionStatus = SubscriptionStatus.Expired.ToString() }),
                    NewValues = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        isDeleted = true, subscriptionStatus = SubscriptionStatus.Cancelled.ToString(), purgeAtUtc = decision.DueAtUtc, noticeSentAtUtc = tc.PurgeNoticeSentAt,
                        ruleCode = TrialDataRetentionPolicy.RuleCode, legalReference = TrialDataRetentionPolicy.LegalReference,
                    }),
                    Timestamp = nowUtc,
                });
                purged++;
            }
            await db.SaveChangesAsync(ct);
            if (page.Count < PageSize) break;
        }
        return new RunReport(scanned, notEligible, notYetDue, legalHold, noticeSent, awaiting, purged);
    }

    /// <summary>แจ้งเจ้าของล่วงหน้าครั้งเดียว — ประทับ <c>PurgeNoticeSentAt</c> ก่อน (ไม่ส่งซ้ำแม้แจ้งเตือนล้ม) · ประวัติ + audit</summary>
    private static async Task SendNoticeAsync(AccountingDbContext db, Subscription sub, TrialConfig tc, Company company,
        TrialDataRetentionPolicy.Decision decision, DateTime nowUtc, INotificationService? notifications, ILogger? logger, CancellationToken ct)
    {
        tc.PurgeNoticeSentAt = nowUtc;
        tc.UpdatedAt = nowUtc; tc.UpdatedBy = Actor;
        var purgeAt = TrialDataRetentionPolicy.PurgeAt(decision.DueAtUtc ?? nowUtc, nowUtc, tc.PurgeNoticeDays);
        var purgeText = $"{purgeAt.AddHours(7):dd/MM/yyyy}";
        db.SubscriptionHistories.Add(new SubscriptionHistory
        {
            SubscriptionId = sub.Id, Action = "TrialDataPurgeNoticeSent",
            Notes = $"แจ้งเจ้าของล่วงหน้า {tc.PurgeNoticeDays} วัน — ข้อมูลทดลองใช้จะถูกปิดไม่ก่อน {purgeText} (ส่งออกข้อมูลได้ก่อนวันนั้น) — {TrialDataRetentionPolicy.NoticeRuleCode}",
            PerformedBy = Actor,
        });
        db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = sub.CompanyId, UserEmail = Actor, Action = AuditAction.Update, EntityType = nameof(TrialConfig), EntityId = tc.Id.ToString(),
            OldValues = System.Text.Json.JsonSerializer.Serialize(new { purgeNoticeSentAt = (DateTime?)null }),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { purgeNoticeSentAt = nowUtc, purgeAtUtc = purgeAt, ruleCode = TrialDataRetentionPolicy.NoticeRuleCode }),
            Timestamp = nowUtc,
        });
        if (notifications == null) return;
        var owners = await db.CompanyUsers.AsNoTracking()
            .Where(cu => cu.CompanyId == sub.CompanyId && cu.Role == UserRole.Owner).Select(cu => cu.UserId).ToListAsync(ct);
        foreach (var ownerId in owners)
        {
            try
            {
                await notifications.SendAsync(ownerId, sub.CompanyId, NotificationType.System,
                    $"ข้อมูลทดลองใช้ของ {company.Name} จะถูกปิดวันที่ {purgeText}",
                    $"ระยะทดลองใช้หมดอายุและพ้นระยะเก็บข้อมูลแล้ว — ระบบจะปิดข้อมูลบริษัทนี้ไม่ก่อน {purgeText} · ส่งออกข้อมูลได้ที่ศูนย์บิล/ตั้งค่า หรือสมัครแพ็กเกจเพื่อใช้ต่อ",
                    "/pages/billing.html", nameof(Company), company.Id);
            }
            catch (Exception ex) { logger?.LogWarning(ex, "TrialDataRetention: ส่งแจ้งเตือนล่วงหน้าให้ผู้ใช้ {UserId} ไม่สำเร็จ (ประวัติ/audit บันทึกแล้ว)", ownerId); }
        }
    }
}
