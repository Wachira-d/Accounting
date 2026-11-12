using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 254 WP-11 L2 (LINE_MULTICHANNEL_DESIGN §3.e) — ผู้ส่งคิว LINE ถึงปลายทางของผู้ติดต่อ · ลำดับต่อแถว (ล็อกด้วย required_call_site_check):
/// <list type="number">
/// <item>จองแถวด้วย <c>FOR UPDATE SKIP LOCKED</c> (หลายเครื่องไม่ส่งซ้ำ) + ยืนยันการจองต่อแถวก่อนยิง · แถวค้าง "กำลังส่ง" เกิน <see cref="StaleSending"/> ⇒ กลับมารอส่ง นับหนึ่งครั้ง (retry key เดิม ⇒ LINE ไม่ส่งสองข้อความ) · ครบเพดาน ⇒ ล้มพร้อมเหตุ</item>
/// <item>ตรวจใบซ้ำตอนส่ง (<see cref="ScheduledEmailSendCheck.SkipReason"/> ตัวเดียวกับคิวอีเมล) — ยกเลิก/กลับเป็นร่าง/ชำระครบ ⇒ ข้ามพร้อมเหตุ</item>
/// <item>ปลายทางยังใช้ได้ไหม — ตัวหาผู้รับเดียว (<see cref="IContactRecipientResolver.ResolveAsync"/>: ยกเลิก · บล็อก · ถอนยินยอม · กลุ่มกลางของแอดมิน · OA เปลี่ยน · OA กลางยังไม่เปิด)</item>
/// <item>การ์ด <see cref="LineDocumentCard"/> (ภาษา/หัวเดียวกับ PDF · ลิงก์ชำระจาก <see cref="IDocumentPayLinkService"/> เท่านั้น)</item>
/// <item>push ผ่าน <see cref="ILineNotifyService.TryPushContactAsync"/> (OA ที่ปลายทางผูก · <c>X-Line-Retry-Key</c> ของแถว) → จำแนกด้วย <see cref="LinePushOutcome"/></item>
/// <item>ล้มถาวร ⇒ ถอยไปอีเมล (Q7 · <see cref="ContactLineChannel.FallbackNeeded"/>) ผ่าน <see cref="IEmailScheduleService.EnqueueLineFallbackEmailAsync"/></item>
/// </list>
/// "ส่งแล้ว" = LINE ตอบรับจริงเท่านั้น (200 หรือ 409 ของ retry key เดิม) — ไม่มีสถานะที่ระบบประทับเอง
/// </summary>
public class ContactLineQueueService : IContactLineQueueService
{
    private readonly AccountingDbContext _db;
    private readonly ILineNotifyService _line;
    private readonly IContactRecipientResolver _recipients;
    private readonly ISecretProtector _secrets;
    private readonly IEmailScheduleService _emailSchedule;
    private readonly IDocumentPayLinkService? _payLinks;
    private readonly ILogger<ContactLineQueueService> _logger;

    /// <summary>แถว "กำลังส่ง" ที่ค้างนานกว่านี้ = เครื่องล้มกลางทาง ⇒ กลับมารอส่ง</summary>
    internal static readonly TimeSpan StaleSending = TimeSpan.FromMinutes(30);   // > BatchSize × LineNotifyService.PushTimeout (50 × 15 วินาที = 12.5 นาที)
    private const int BatchSize = 50;

    /// <summary>รอบ 257 ชุด 4 (C LOW): กระดิ่งถึงเจ้าของเมื่อ OA ของบริษัทใช้ข้อความของเดือนหมด (429 monthly limit) · null = ไม่แจ้ง (เทสต์เดิม) ·
    /// ฝ่ายค้าน LOW-3: ส่งใน DI scope แยก (context ของตัวเอง — <c>NotificationService.SendAsync</c> เรียก SaveChanges ⇒ ไม่บันทึกแถวคิวที่กำลังส่งกลางทาง)</summary>
    private readonly IServiceScopeFactory? _scopes;

    public ContactLineQueueService(AccountingDbContext db, ILineNotifyService line, IContactRecipientResolver recipients, ISecretProtector secrets,
        IEmailScheduleService emailSchedule, ILogger<ContactLineQueueService> logger, IDocumentPayLinkService? payLinks = null,
        IServiceScopeFactory? scopes = null, IConfiguration? config = null)
    {
        _db = db; _line = line; _recipients = recipients; _secrets = secrets; _emailSchedule = emailSchedule; _logger = logger; _payLinks = payLinks;
        _scopes = scopes;
        _config = config;
    }

    /// <summary>รอบ 258-3 (F-H1 · A-L8): ความลับของเซิร์ฟเวอร์ (กุญแจลิงก์ดู PDF — <see cref="LineDocumentViewLink"/>) + URL ของระบบ · null = การ์ดไม่มีปุ่มดู PDF</summary>
    private readonly IConfiguration? _config;

    /// <summary>รอบ 257 ชุด 4 (C LOW): OA ของบริษัทใช้ข้อความฟรีของเดือนหมด (LINE ตอบ 429 monthly limit) ⇒ กระดิ่งถึงเจ้าของ<b>ที่ยังใช้งานอยู่</b>
    /// (ตัวตัดสินสมาชิกเดียว <see cref="CompanyMembership.ActiveMemberIdsAsync"/>) <b>ครั้งเดียวต่อเดือน (ปฏิทินไทย)</b> · OA กลางของ NextAcc ไม่แจ้งบริษัท ·
    /// scope แยก (ฝ่ายค้าน LOW-3) · ล้ม ⇒ LogError (แถวคิวบอกเหตุอยู่แล้ว ไม่ย้อนผลส่ง)</summary>
    private async Task AlertMonthlyLimitAsync(Guid companyId, CancellationToken ct)
    {
        if (_scopes == null) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var notifications = scope.ServiceProvider.GetService<INotificationService>();
            if (notifications == null) return;
            var since = LinePushOutcome.AlertMonthStartUtc(DateTime.UtcNow);
            if (await db.Notifications.AnyAsync(n => n.CompanyId == companyId && n.EntityType == LinePushOutcome.MonthlyLimitAlertEntity && n.CreatedAt >= since, ct))
                return;
            // รอบ 260-4 (ฝ่ายค้าน L8): ผู้รับตัวเดียว — เจ้าของที่ใช้งาน · ไม่มี ⇒ ผู้มีสิทธิ์แก้ค่าตั้งบริษัท
            foreach (var u in await LineAlertRecipients.ForCompanyAsync(db, scope.ServiceProvider.GetService<IPermissionService>(), companyId, ct))
                await notifications.SendAsync(u, companyId, NotificationType.System, LinePushOutcome.MonthlyLimitAlertTitle, LinePushOutcome.MonthlyLimitAlertMessage,
                    "/pages/email-schedule.html", LinePushOutcome.MonthlyLimitAlertEntity, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งเจ้าของบริษัท {Company} เรื่องข้อความ LINE OA ของเดือนหมดไม่สำเร็จ — แถวคิว LINE บอกเหตุแล้ว", companyId);
        }
    }

    public async Task<int> ProcessPendingAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var staleBefore = now - StaleSending;
        // ฝ่ายค้าน M2: แถวค้าง "กำลังส่ง" (เครื่องล้มกลางทาง) ⇒ นับเป็นความพยายามหนึ่งครั้ง (ครบเพดาน = ล้มพร้อมเหตุ ไม่วนไม่จบ) ·
        // เกณฑ์ค้าง (StaleSending) > เวลาทั้งแบตช์ (BatchSize × LineNotifyService.PushTimeout) ⇒ แถวที่ยังรอคิวในแบตช์ของเครื่องอื่นไม่ถูกรีเซ็ต
        await _db.LineQueues
            .Where(q => q.Status == ContactLineChannel.StatusSending && q.UpdatedAt != null && q.UpdatedAt < staleBefore
                && q.RetryCount + 1 >= LinePushOutcome.MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, ContactLineChannel.StatusFailed)
                .SetProperty(q => q.RetryCount, q => q.RetryCount + 1)
                .SetProperty(q => q.ErrorMessage, StaleFailedReason)
                .SetProperty(q => q.UpdatedAt, (DateTime?)now), ct);
        await _db.LineQueues
            .Where(q => q.Status == ContactLineChannel.StatusSending && q.UpdatedAt != null && q.UpdatedAt < staleBefore)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, ContactLineChannel.StatusPending)
                .SetProperty(q => q.RetryCount, q => q.RetryCount + 1)
                .SetProperty(q => q.UpdatedAt, (DateTime?)now), ct);

        // จองแถวแบบเดียวกับคิวอีเมล (EmailScheduleService.ProcessPendingQueueAsync) — สองเครื่องไม่ได้แถวเดียวกัน
        var claimedIds = await _db.Database.SqlQuery<Guid>(
            $@"WITH claimed AS (
                SELECT ""Id""
                FROM ""LineQueues""
                WHERE NOT ""IsDeleted"" AND ""Status"" = 'Pending' AND ""ScheduledFor"" <= {now}
                ORDER BY ""ScheduledFor""
                LIMIT 50
                FOR UPDATE SKIP LOCKED
              )
              UPDATE ""LineQueues"" q
              SET ""Status"" = 'Sending', ""UpdatedAt"" = {now}
              FROM claimed
              WHERE q.""Id"" = claimed.""Id""
              RETURNING q.""Id""").ToListAsync(ct);
        if (claimedIds.Count == 0) return 0;

        var sent = 0;
        // รอบ 260-4 (คำตัดสิน 260-4-1): ค่าตั้งชั่วโมงเงียบต่อบริษัท — อ่านครั้งเดียวต่อบริษัทต่อรอบ (ตัวแปรของรอบนี้ ไม่ใช่ static)
        var quietCache = new Dictionary<Guid, (bool Enabled, int Start, int End)>();
        foreach (var id in claimedIds.Take(BatchSize))
        {
            // ฝ่ายค้าน M1(ข): ทีละแถว — context สะอาดทุกรอบ (บันทึกแถวก่อนหน้าล้ม/Clear แล้ว แถวนี้ยังถูกติดตามและบันทึกได้)
            _db.ChangeTracker.Clear();
            // ฝ่ายค้าน M2: ยืนยันการจองต่อแถวก่อนยิง (ยังเป็น "กำลังส่ง" ของรอบนี้) + ต่ออายุ — ถูกรีเซ็ต/เครื่องอื่นหยิบไปแล้ว ⇒ ข้าม
            var stamp = DateTime.UtcNow;
            var held = await _db.LineQueues
                .Where(q => q.Id == id && q.Status == ContactLineChannel.StatusSending)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.UpdatedAt, (DateTime?)stamp), ct);
            if (held != 1) continue;
            var item = await _db.LineQueues.FirstOrDefaultAsync(q => q.Id == id, ct);
            if (item == null) continue;
            try
            {
                // รอบ 260-4: ชั่วโมงเงียบ ⇒ เลื่อน (กลับเป็นรอส่ง · ไม่นับความพยายาม) — ไม่ยิง LINE · ใบเสร็จหลังชำระ/ส่งเองจากหน้าเอกสารไม่ถูกเลื่อน
                if (await DeferForQuietHoursAsync(item, quietCache, ct)) { }
                else if (await SendOneAsync(item, ct)) sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // ล้มดัง: แถวนี้กลับไปรอส่ง (ครบครั้ง = ล้ม) พร้อมเหตุบนคิว — ไม่ใช่กลืนเงียบ
                _logger.LogWarning(ex, "LineQueue send failed Id={Id}", item.Id);
                // ฝ่ายค้าน fix2 L4: LINE รับไปแล้ว (Sent) ⇒ ข้อผิดพลาดหลังจากนั้น (เช่นอัปเดตปลายทาง) ห้ามลดสถานะกลับไปรอส่ง — จดเหตุไว้เท่านั้น
                if (item.Status == ContactLineChannel.StatusSent)
                {
                    item.ErrorMessage = Trunc(SaveFailureOutcome.SentButNotSavedPrefix + ex.Message, 2000);
                    item.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                item.RetryCount++;
                item.Status = item.RetryCount >= LinePushOutcome.MaxAttempts ? ContactLineChannel.StatusFailed : ContactLineChannel.StatusPending;
                item.ScheduledFor = DateTime.UtcNow + LinePushOutcome.Backoff(item.RetryCount);
                item.ErrorMessage = Trunc("ส่งไม่สำเร็จ: " + ex.Message, 2000);
                item.UpdatedAt = DateTime.UtcNow;
                }
            }
            try { await _db.SaveChangesAsync(ct); }
            catch (Exception saveEx) when (saveEx is not OperationCanceledException)
            {
                // บันทึกแถวหนึ่งล้ม ⇒ ไม่ลากทั้งแบตช์ · ฝ่ายค้าน M1(ก): LINE รับไปแล้ว ⇒ ต้องประทับ "ส่งแล้ว" (ไม่ใช่ล้ม — กด "ส่งอีกครั้ง" = ลูกค้าได้สองข้อความ)
                _logger.LogError(saveEx, "LineQueue save failed Id={Id}", item.Id);
                var outcome = SaveFailureOutcome.For(item.Status, item.SentAt, item.LineRequestId, saveEx.GetBaseException().Message);
                _db.ChangeTracker.Clear();
                var itemId = item.Id;
                await _db.LineQueues.Where(q => q.Id == itemId)
                    .ExecuteUpdateAsync(x => x.SetProperty(q => q.Status, outcome.Status).SetProperty(q => q.ErrorMessage, outcome.Note)
                        .SetProperty(q => q.SentAt, outcome.SentAt).SetProperty(q => q.LineRequestId, outcome.LineRequestId)
                        .SetProperty(q => q.UpdatedAt, (DateTime?)DateTime.UtcNow), ct);
            }
        }
        return sent;
    }

    /// <summary>รอบ 260-4 (team-A L8 · คำตัดสิน 260-4-1/260-4-2): แถวที่จองได้ในช่วงเงียบของบริษัท ⇒ กลับเป็น "รอส่ง" พร้อม <c>ScheduledFor</c> = สิ้นช่วงเงียบ +
    /// หมายเหตุ (ตัวตัดสินเดียว <see cref="LineQuietHours.DeferUntilUtc"/>) · ไม่แตะ RetryCount/RetryKey · คืน true = เลื่อนแล้ว (ผู้เรียกบันทึก)</summary>
    private async Task<bool> DeferForQuietHoursAsync(LineQueue item, Dictionary<Guid, (bool Enabled, int Start, int End)> cache, CancellationToken ct)
    {
        if (LineQuietHours.IsExempt(item.Trigger, item.CustomerInitiated)) return false;
        if (!cache.TryGetValue(item.CompanyId, out var q))
        {
            var row = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == item.CompanyId)
                .Select(s => new { s.LineQuietHoursEnabled, s.LineQuietStartHour, s.LineQuietEndHour }).FirstOrDefaultAsync(ct);
            q = row == null
                ? (LineQuietHours.DefaultEnabled, LineQuietHours.DefaultStartHour, LineQuietHours.DefaultEndHour)
                : (row.LineQuietHoursEnabled, row.LineQuietStartHour, row.LineQuietEndHour);
            cache[item.CompanyId] = q;
        }
        var until = LineQuietHours.DeferUntilUtc(DateTime.UtcNow, q.Enabled, q.Start, q.End, item.Trigger, item.CustomerInitiated);
        if (until is not DateTime at) return false;
        item.Status = ContactLineChannel.StatusPending;
        item.ScheduledFor = at;
        item.ErrorMessage = Trunc(LineQuietHours.DeferNote(at, q.Start, q.End), 2000);
        item.RuleCode = LineQuietHours.RuleCode;
        item.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>รอบ 260-4 (team-A L8 · คำตัดสิน 260-4-4): LINE ล้มถาวรซ้ำ (≥ <see cref="LineFailureAlert.Threshold"/> ใน <see cref="LineFailureAlert.Window"/>) หรือ Token/ช่องของ OA บริษัทเสีย ⇒
    /// กระดิ่งถึงเจ้าของที่ยังใช้งาน <b>ครั้งเดียวต่อ 24 ชม.</b> · กันซ้ำข้ามเครื่องใต้ <c>pg_advisory_xact_lock</c> ต่อบริษัท · โควตาเดือนหมดไม่นับ (มีกระดิ่งของตัวเอง) ·
    /// OA กลางไม่นับ · scope แยก (ไม่บันทึกแถวคิวกลางทาง) · ล้ม ⇒ LogError (แถวคิวบอกเหตุอยู่แล้ว)</summary>
    private async Task AlertRepeatedFailureAsync(Guid companyId, Guid currentRowId, bool channelBroken, string? reason, CancellationToken ct,
        bool decryptFailed = false)
    {
        if (_scopes == null) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var notifications = scope.ServiceProvider.GetService<INotificationService>();
            if (notifications == null) return;
            var now = DateTime.UtcNow;
            var since = now - LineFailureAlert.Window;
            // แถวนี้ยังไม่ได้บันทึกเป็น "ล้มเหลว" (บันทึกหลังคืนจากผู้ส่ง) ⇒ นับแยก +1
            var failures = 1 + await db.LineQueues.AsNoTracking()
                .CountAsync(x => x.CompanyId == companyId && x.Id != currentRowId && x.Status == ContactLineChannel.StatusFailed
                    && x.UpdatedAt != null && x.UpdatedAt >= since
                    && (x.RuleCode == null || x.RuleCode != LinePushOutcome.CodeMonthlyLimit)
                    && (x.OaScope == null || x.OaScope != NotifyEndpointKind.ScopeNextAcc), ct);
            // ฝ่ายค้าน M4: ชนิดของกระดิ่ง (ล้มซ้ำ · Token/ช่องเสีย · ถอดรหัสไม่ได้) — กันซ้ำแยกต่อชนิด (EntityId = Guid คงที่ต่อชนิด)
            if (LineFailureAlert.AlertKind(failures, channelBroken, decryptFailed) is not LineFailureAlert.Kind kind) return;
            var kindId = LineFailureAlert.KindId(kind);
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                    new object[] { AdvisoryLockKey.For(companyId, LineFailureAlert.LockScope, "repeated") }, ct);
                var dedupeSince = now - LineFailureAlert.Dedupe;
                if (await db.Notifications.AnyAsync(n => n.CompanyId == companyId && n.EntityType == LineFailureAlert.EntityType && n.EntityId == kindId
                        && n.CreatedAt >= dedupeSince, ct))
                {
                    await tx.CommitAsync(ct);
                    return;
                }
                // ฝ่ายค้าน L8: ผู้รับตัวเดียว (เจ้าของที่ใช้งาน · ไม่มี ⇒ ผู้มีสิทธิ์แก้ค่าตั้งบริษัท)
                var recipients = await LineAlertRecipients.ForCompanyAsync(db, scope.ServiceProvider.GetService<IPermissionService>(), companyId, ct);
                if (recipients.Count == 0)
                    _logger.LogError("LINE ถึงผู้ติดต่อของบริษัท {Company} ล้ม ({Kind}) แต่ไม่มีผู้รับกระดิ่งที่ใช้งานอยู่", companyId, kind);
                foreach (var u in recipients)
                    await notifications.SendAsync(u, companyId, NotificationType.System, LineFailureAlert.Title(kind),
                        LineFailureAlert.Message(kind, failures, reason), "/pages/email-schedule.html", LineFailureAlert.EntityType, kindId);
                await tx.CommitAsync(ct);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งเจ้าของบริษัท {Company} เรื่อง LINE ล้มซ้ำไม่สำเร็จ — แถวคิว LINE บอกเหตุแล้ว", companyId);
        }
    }

    /// <summary>รอบ 260-4 (ฝ่ายค้าน L7 · คำตัดสิน 260-4-11): LINE OA กลางของ NextAcc ล้มถาวร (Token/ช่องเสีย · โควตาหมด) ⇒ กระดิ่งผู้ดูแลระบบครั้งเดียวต่อวัน
    /// (เรื่องเดียวกระทบทุกบริษัทที่ส่งเข้ากลุ่มผ่าน OA กลาง — บริษัทแก้ไม่ได้) · กันซ้ำใต้ล็อกระดับแพลตฟอร์ม · scope แยก · ล้ม ⇒ LogError</summary>
    private async Task AlertCentralOaFailureAsync(string? code, string? reason, CancellationToken ct)
    {
        if (_scopes == null) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var notifications = scope.ServiceProvider.GetService<INotificationService>();
            if (notifications == null) return;
            var since = DateTime.UtcNow - LineFailureAlert.Dedupe;
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                    new object[] { AdvisoryLockKey.For(LineFailureAlert.LockScope, "central") }, ct);
                if (await db.Notifications.AnyAsync(n => n.CompanyId == null && n.EntityType == LineFailureAlert.CentralEntityType && n.CreatedAt >= since, ct))
                {
                    await tx.CommitAsync(ct);
                    return;
                }
                var admins = await db.Users.AsNoTracking()
                    .Where(u => u.IsSystemAdmin && !u.IsDeleted && u.Status == UserStatus.Active).Select(u => u.Id).Take(20).ToListAsync(ct);
                if (admins.Count == 0) _logger.LogError("LINE OA กลางล้ม ({Code}) แต่ไม่มีผู้ดูแลระบบที่ใช้งานอยู่ให้แจ้ง", code);
                foreach (var u in admins)
                    await notifications.SendAsync(u, null, NotificationType.System, LineFailureAlert.CentralTitle, LineFailureAlert.CentralMessage(code, reason),
                        "/admin/site-settings.html", LineFailureAlert.CentralEntityType, null);
                await tx.CommitAsync(ct);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งผู้ดูแลระบบเรื่อง LINE OA กลางล้มไม่สำเร็จ");
        }
    }

    /// <summary>รอบ 260-4 (ฝ่ายค้าน M1 · คำตัดสิน 260-4-8): ถอด LINE id ของปลายทางไม่ได้ — อาการเดียวกับ "กุญแจเข้ารหัสของเครื่องไม่ตรงกัน" ⇒ ล้มดัง (LogError) ·
    /// ครั้งแรกลองใหม่ (เครื่องอื่นอาจถอดได้) · ครั้งถัดไปล้มพร้อมคำแนะนำที่ถูก (ตรวจกุญแจก่อน "ผูกใหม่") + กระดิ่งชนิด "ถอดรหัสไม่ได้" · ถอยไปอีเมลตามเดิม</summary>
    private async Task<bool> DecryptFailedAsync(LineQueue item, ContactNotifyEndpoint ep, bool fallback, CancellationToken ct)
    {
        _logger.LogError("ถอดรหัส LINE id ของปลายทาง {Endpoint} บริษัท {Company} ไม่ได้ (แถวคิว {Id} ครั้งที่ {Try}) — ตรวจว่าทุกเครื่องใช้ ENCRYPTION_KEY เดียวกัน",
            ep.Id, item.CompanyId, item.Id, item.RetryCount + 1);
        var now = DateTime.UtcNow;
        if (item.RetryCount == 0)
        {
            item.RetryCount = 1;
            item.Status = ContactLineChannel.StatusPending;
            item.ScheduledFor = now + LinePushOutcome.Backoff(1);
            item.ErrorMessage = LineFailureAlert.DecryptRetryNote;
            item.RuleCode = LineFailureAlert.DecryptRuleCode;
            item.UpdatedAt = now;
            return false;
        }
        Finish(item, ContactLineChannel.StatusFailed, LineFailureAlert.DecryptAdvice, LineFailureAlert.DecryptRuleCode);
        if (ep.OaScope == NotifyEndpointKind.ScopeNextAcc) await AlertCentralOaFailureAsync(LineFailureAlert.DecryptRuleCode, LineFailureAlert.DecryptAdvice, ct);
        else await AlertRepeatedFailureAsync(item.CompanyId, item.Id, channelBroken: false, LineFailureAlert.DecryptAdvice, ct, decryptFailed: true);
        if (fallback) await FallbackAsync(item, eligible: true, ct);
        return false;
    }

    /// <summary>เหตุเมื่อแถวค้าง "กำลังส่ง" ครบเพดานความพยายาม</summary>
    internal const string StaleFailedReason = "ค้างสถานะกำลังส่งเกินเวลาครบจำนวนครั้ง (เครื่องล้มกลางทาง) — ไม่รู้ว่า LINE รับแล้วหรือไม่ · ตรวจกับผู้รับก่อนกดส่งอีกครั้ง";

    /// <summary>ส่งแถวเดียว — คืน true เมื่อ LINE รับแล้ว · ทุกทางที่ไม่ส่งประทับสถานะ + เหตุบนแถว</summary>
    private async Task<bool> SendOneAsync(LineQueue item, CancellationToken ct)
    {
        // รอบ 259 (258-3-6): การ์ดทวง 50 ทวิ (ต่อผู้ติดต่อ — ไม่ใช่เอกสาร) ⇒ เส้นของตัวเอง
        if (item.EntityType == LineWhtCertCard.QueueEntityType && item.Trigger == LineWhtCertCard.QueueTrigger && item.EndpointId is Guid whtEndpoint)
            return await SendWhtCertRequestAsync(item, whtEndpoint, ct);
        // รอบ 258-3: trigger ที่ผู้ส่งรู้จัก = กฎตารางส่ง + ใบเสร็จหลังรับชำระ + ส่งเองจากหน้าเอกสาร (ContactLineChannel.SenderSupports ตัวเดียว)
        if (item.EntityType != "Document" || item.EndpointId is not Guid endpointId || !ContactLineChannel.SenderSupports(item.Trigger))
        {
            Finish(item, ContactLineChannel.StatusFailed, "แถวคิว LINE ชนิดนี้ส่งไม่ได้ (รองรับเฉพาะการ์ดเอกสารที่มีปลายทาง)", null);
            return false;
        }
        // 1) ใบซ้ำตอนส่ง — ตัวตัดสินเดียวกับคิวอีเมล (ยกเลิก/ร่าง/ลบ ⇒ ข้าม · ทวงชำระแต่ชำระครบ ⇒ ข้าม)
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == item.EntityId && d.CompanyId == item.CompanyId && !d.IsDeleted, ct);
        var skip = ScheduledEmailSendCheck.SkipReason(item.Trigger, doc != null, doc?.Status ?? DocumentStatus.Draft, doc?.BalanceDue ?? 0m);
        if (skip != null || doc == null)
        {
            Finish(item, ContactLineChannel.StatusSkipped, skip ?? "ไม่พบเอกสาร", null);
            return false;
        }

        // 2) ปลายทางยังใช้ได้ไหม — ตัวหาผู้รับเดียว (tenant · consent · กลุ่มกลางของแอดมิน · OA ของบริษัท/OA เปลี่ยน · OA กลางยังไม่เปิด)
        var topic = Enum.TryParse<NotifyTopic>(item.Topic, out var t) && Enum.IsDefined(t) ? t : NotifyTopics.ForTrigger(item.Trigger);
        var set = await _recipients.ResolveAsync(item.CompanyId, item.ContactId, topic, ct);
        var option = set.Options.FirstOrDefault(o => o.EndpointId == endpointId);
        if (option == null || !option.Available)
        {
            Finish(item, ContactLineChannel.StatusSkipped,
                "ไม่ส่งทาง LINE — " + (option?.Reason ?? "ปลายทางนี้ถูกลบหรือไม่ใช่ของผู้ติดต่อนี้แล้ว"), option?.RuleCode);
            await FallbackAsync(item, eligible: true, ct);
            return false;
        }
        var ep = await _db.ContactNotifyEndpoints.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == endpointId && e.CompanyId == item.CompanyId && e.ContactId == item.ContactId, ct);
        if (ep == null)
        {
            Finish(item, ContactLineChannel.StatusSkipped, "ไม่ส่งทาง LINE — ปลายทางนี้ถูกลบแล้ว", null);
            await FallbackAsync(item, eligible: true, ct);
            return false;
        }
        var isGroup = ep.Kind == NotifyEndpointKind.LineGroup;
        var to = _secrets.Unprotect(isGroup ? ep.LineGroupId : ep.LineUserId);
        if (string.IsNullOrWhiteSpace(to))
            return await DecryptFailedAsync(item, ep, fallback: true, ct);

        // ฝ่ายค้าน 259 L2: ผู้ใช้ติ๊กลิงก์ชำระ (ส่งเอง) แต่ตอนส่งลิงก์ใช้ไม่ได้แล้ว ⇒ ไม่ส่ง (ไม่ใช่การ์ดไม่มีปุ่มแล้วขึ้น "ส่งแล้ว")
        if (item.IncludePayLink == true && doc.BalanceDue > 0 && DocumentLineSendRule.PayLinkAllowed(ep.Kind, ep.ShowAmounts)
            && (_payLinks == null || await _payLinks.CurrentUrlAsync(item.CompanyId, doc.Id, ct) == null))
        {
            Finish(item, ContactLineChannel.StatusSkipped, DocumentLineSendRule.PayLinkGoneAtSend, DocumentLineSendRule.RuleCode);
            return false;
        }
        // 3) การ์ด (ภาษา/หัวเดียวกับ PDF · ยอดซ่อนได้ต่อกลุ่ม — Q5)
        var card = await BuildCardAsync(item, doc, showAmounts: !isGroup || ep.ShowAmounts, isGroup,
            viaNextAcc: ep.OaScope == NotifyEndpointKind.ScopeNextAcc, ct);
        return await PushAsync(item, ep, isGroup, to, card, ct);
    }

    /// <summary>รอบ 259 (258-3-6): ส่งการ์ดทวง 50 ทวิ หนึ่งแถว — ด่านตอนส่งตัวเดียวกับอีเมลทวง (ปิดทวง · ผู้ติดต่อถูกลบ · ไม่ส่งอัตโนมัติ · ส่งใบครบแล้ว) ·
    /// ปลายทางยังรับเรื่อง 50 ทวิ (ตัวหาผู้รับเดียว) · ลิงก์อัปโหลดอ่านอย่างเดียว (สร้างพร้อมแถวคิว) — ไม่มีแถวลิงก์ ⇒ ไม่ส่ง (ไม่ส่งลิงก์เสีย) ·
    /// เนื้อการ์ดจากรายการที่ค้าง ณ ตอนส่ง · ผลส่งจำแนกตัวเดียวกับการ์ดเอกสาร (<see cref="PushAsync"/>) · ไม่ถอยไปอีเมล (อีเมลของขั้นเดียวกันเข้าคิวเองแล้วถ้ามี)</summary>
    private async Task<bool> SendWhtCertRequestAsync(LineQueue item, Guid endpointId, CancellationToken ct)
    {
        if (await WhtCertReminderService.CompanyContactSkipReasonAsync(_db, item.CompanyId, item.ContactId, ct) is string pre)
        {
            Finish(item, ContactLineChannel.StatusSkipped, pre, LineWhtCertCard.RuleCode);
            return false;
        }
        var items = await WhtCertReminderService.AwaitingItemsAsync(_db, item.CompanyId, item.ContactId, ct);
        if (items.Count == 0)
        {
            Finish(item, ContactLineChannel.StatusSkipped, "ลูกค้าส่งหนังสือรับรองครบแล้ว (หรือส่งแล้วรอตรวจ) — ไม่ส่งการ์ดทวง", LineWhtCertCard.RuleCode);
            return false;
        }
        var set = await _recipients.ResolveAsync(item.CompanyId, item.ContactId, NotifyTopic.Wht, ct);
        var option = set.Options.FirstOrDefault(o => o.EndpointId == endpointId);
        if (option == null || !option.Available)
        {
            Finish(item, ContactLineChannel.StatusSkipped,
                "ไม่ส่งทาง LINE — " + (option?.Reason ?? "ปลายทางนี้ถูกลบหรือไม่ใช่ของผู้ติดต่อนี้แล้ว"), option?.RuleCode);
            return false;
        }
        var ep = await _db.ContactNotifyEndpoints.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == endpointId && e.CompanyId == item.CompanyId && e.ContactId == item.ContactId, ct);
        if (ep == null)
        {
            Finish(item, ContactLineChannel.StatusSkipped, "ไม่ส่งทาง LINE — ปลายทางนี้ถูกลบแล้ว", null);
            return false;
        }
        var isGroup = ep.Kind == NotifyEndpointKind.LineGroup;
        var to = _secrets.Unprotect(isGroup ? ep.LineGroupId : ep.LineUserId);
        if (string.IsNullOrWhiteSpace(to))
            return await DecryptFailedAsync(item, ep, fallback: false, ct);
        string? uploadUrl = null;
        if (LineWhtCertCard.UploadLinkAllowed(isGroup, ep.ShowAmounts))
        {
            var (url, problem) = await WhtCertUploadLinkService.LineReminderUrlAsync(_db, _config, item.CompanyId, item.Id, ct);
            if (url == null)
            {
                Finish(item, ContactLineChannel.StatusFailed, problem ?? "สร้างลิงก์อัปโหลดไม่ได้", LineWhtCertCard.RuleCode);
                return false;
            }
            uploadUrl = url;
        }
        var co = await _db.Companies.AsNoTracking().Where(c => c.Id == item.CompanyId)
            .Select(c => new { c.Name, c.TaxId, c.BranchCode }).FirstOrDefaultAsync(ct);
        var contactName = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == item.ContactId && c.CompanyId == item.CompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var card = LineWhtCertCard.Build(new LineWhtCertCard.Input(co?.Name ?? "", co?.TaxId, TaxBranchCode.Label(co?.BranchCode), contactName,
            WhtCertReminderCadence.StepFromQueueKey(item.BaseKey) ?? WhtCertReminderCadence.InitialSteps[0], items,
            ShowAmounts: !isGroup || ep.ShowAmounts, uploadUrl, isGroup));
        return await PushAsync(item, ep, isGroup, to, card, ct);
    }

    /// <summary>push + จำแนกผล + ประทับแถว/ปลายทาง — ตัวเดียวของการ์ดทุกชนิด (เอกสาร · ใบเสร็จ · ทวง 50 ทวิ)</summary>
    private async Task<bool> PushAsync(LineQueue item, ContactNotifyEndpoint ep, bool isGroup, string to, LineDocumentCard.Card card, CancellationToken ct)
    {
        // 4) push (OA ที่ปลายทางผูก · retry key ของแถว) → จำแนกผลตัวเดียว
        var target = isGroup ? LineTargetKind.ContactGroup : LineTargetKind.TenantMember;
        var result = await _line.TryPushContactAsync(item.CompanyId, ep.OaScope, target, to, card.AltText, card.Contents, item.RetryKey);
        var cls = LinePushOutcome.Classify(result.Ok, result.HttpStatus, result.Error, result.RuleCode, result.TransportError);
        item.OaScope = ep.OaScope;
        var now = DateTime.UtcNow;
        if (cls.Verdict == LinePushVerdict.Sent)
        {
            item.Status = ContactLineChannel.StatusSent;
            item.SentAt = now;
            item.LineRequestId = Trunc(result.LineRequestId, 100);
            item.ErrorMessage = null;
            item.RuleCode = cls.Code == LinePushOutcome.CodeAlreadyAccepted ? cls.Code : null;
            item.UpdatedAt = now;
            await _db.ContactNotifyEndpoints.Where(e => e.Id == ep.Id && e.CompanyId == item.CompanyId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastSentAt, (DateTime?)now).SetProperty(e => e.LastFailure, (string?)null), ct);
            return true;
        }
        if (cls.Verdict == LinePushVerdict.Retry && item.RetryCount + 1 < LinePushOutcome.MaxAttempts)
        {
            item.RetryCount++;
            item.Status = ContactLineChannel.StatusPending;
            item.ScheduledFor = now + LinePushOutcome.Backoff(item.RetryCount);
            item.ErrorMessage = Trunc($"ลองใหม่ครั้งที่ {item.RetryCount} (key เดิม — ไม่ส่งซ้ำ): {cls.Reason}", 2000);
            item.RuleCode = cls.Code;
            item.UpdatedAt = now;
            return false;
        }
        if (cls.Verdict == LinePushVerdict.Retry) item.RetryCount++;
        Finish(item, ContactLineChannel.StatusFailed, cls.Reason ?? "LINE ไม่รับข้อความ", result.RuleCode ?? cls.Code);
        var failure = Trunc(cls.Reason, 500);
        if (cls.BlockEndpoint)
            await _db.ContactNotifyEndpoints.Where(e => e.Id == ep.Id && e.CompanyId == item.CompanyId && e.BlockedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.BlockedAt, (DateTime?)now), ct);
        await _db.ContactNotifyEndpoints.Where(e => e.Id == ep.Id && e.CompanyId == item.CompanyId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastFailure, failure), ct);
        if (cls.ChannelBroken)
        {
            // รอบ 256-2 (ฝ่ายค้าน L7): ปลายทางผ่าน OA กลาง ⇒ บอกว่าเป็น OA กลาง (ผู้ดูแลระบบแก้ token กลาง ไม่ใช่บริษัท)
            if (ep.OaScope == NotifyEndpointKind.ScopeNextAcc)
                _logger.LogWarning("LINE OA กลางของ NextAcc ส่งเข้ากลุ่มของบริษัท {Company} ไม่ได้ ({Code}) — แถวคิว {Id}", item.CompanyId, cls.Code, item.Id);
            else
                _logger.LogWarning("LINE OA ของบริษัท {Company} ส่งไม่ได้ ({Code}) — แถวคิว {Id}", item.CompanyId, cls.Code, item.Id);
        }
        if (ep.OaScope == NotifyEndpointKind.ScopeNextAcc)
        {
            // รอบ 260-4 (ฝ่ายค้าน L7): OA กลางเสีย/โควตาหมด ⇒ กระดิ่งผู้ดูแลระบบครั้งเดียวต่อวัน (log อย่างเดียวไม่ใช่การล้มดัง)
            if (cls.ChannelBroken || cls.Code == LinePushOutcome.CodeMonthlyLimit) await AlertCentralOaFailureAsync(cls.Code, cls.Reason, ct);
        }
        else if (cls.Code == LinePushOutcome.CodeMonthlyLimit)
            await AlertMonthlyLimitAsync(item.CompanyId, ct);
        // รอบ 260-4 (260-4-4): ล้มซ้ำ/ช่องเสีย ⇒ กระดิ่งครั้งเดียวต่อวัน (โควตาเดือนหมด = กระดิ่งของตัวเองข้างบน · OA กลาง = ไม่ใช่เรื่องที่บริษัทแก้)
        else
            await AlertRepeatedFailureAsync(item.CompanyId, item.Id, cls.ChannelBroken, cls.Reason, ct);
        await FallbackAsync(item, cls.FallbackEligible, ct);
        return false;
    }

    private async Task<LineDocumentCard.Card> BuildCardAsync(LineQueue item, Document doc, bool showAmounts, bool isGroup, bool viaNextAcc, CancellationToken ct)
    {
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, item.CompanyId, doc.Id);
        var company = await _db.Companies.AsNoTracking().Where(c => c.Id == item.CompanyId)
            .Select(c => new { c.Name, c.NameEn }).FirstOrDefaultAsync(ct);
        var companyName = heading.IsEnglish && !string.IsNullOrWhiteSpace(company?.NameEn) ? company!.NameEn! : company?.Name ?? "";
        var contactName = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == doc.ContactId && c.CompanyId == item.CompanyId).Select(c => c.Name).FirstOrDefaultAsync(ct);
        var outstanding = doc.BalanceDue > 0;
        // ฝ่ายค้าน 259 M1: กลุ่มซ่อนยอดไม่ได้ปุ่มชำระ (หน้าชำระแสดงยอด/รายการ/ชื่อผู้ซื้อ) — ตัวตัดสินเดียวกับปุ่มดู PDF
        var payUrl = outstanding && DocumentLineSendRule.PayLinkAllowed(isGroup ? NotifyEndpointKind.LineGroup : NotifyEndpointKind.LineUser, showAmounts)
            ? await PayUrlAsync(item, doc, ct) : null;
        return LineDocumentCard.Build(new LineDocumentCard.Input(companyName, viaNextAcc, heading.Language, heading.Title, doc.DocumentNumber,
            showAmounts ? (outstanding ? doc.BalanceDue : doc.TotalAmount) : null, doc.Currency, outstanding ? doc.DueDate : null,
            contactName, payUrl, isGroup, outstanding,
            // ฝ่ายค้าน M1: กลุ่ม "ซ่อนยอด" ไม่ได้ปุ่มดู PDF (ตัวตัดสินเดียว LineDocumentViewLink.EndpointAllows — showAmounts = !isGroup || ep.ShowAmounts)
            ViewUrl: LineDocumentViewLink.EndpointAllows(isGroup ? NotifyEndpointKind.LineGroup : NotifyEndpointKind.LineUser, showAmounts, null, null)
                ? await ViewUrlAsync(item, ct) : null,
            PaymentReceived: item.Trigger == ContactLineChannel.ReceiptTrigger));
    }

    /// <summary>รอบ 258-3 (F-H1 · A-L8): ลิงก์ดู/ดาวน์โหลด PDF ของแถวนี้ — token คำนวณซ้ำได้ (ลองใหม่ = URL เดิม) · ไม่มีความลับ/URL ของระบบ ⇒ ไม่มีปุ่ม (ไม่ใช่ลิงก์เสีย)
    /// · ล้มตรงนี้ไม่กันการส่งการ์ด (LogError)</summary>
    private async Task<string?> ViewUrlAsync(LineQueue item, CancellationToken ct)
    {
        if (_config == null || string.IsNullOrWhiteSpace(_config["Jwt:Secret"])) return null;
        try
        {
            var token = LineDocumentViewLink.Token(LineDocumentViewLink.DeriveKey(_config["Jwt:Secret"]), item.CompanyId, item.Id);
            return LineDocumentViewLink.Url(await SystemBaseUrl.ResolveAsync(_db, _config, ct), item.CompanyId, item.Id, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "สร้างลิงก์ดู PDF ของคิว LINE {Id} ไม่สำเร็จ — การ์ดส่งโดยไม่มีปุ่มดู PDF", item.Id);
            return null;
        }
    }

    /// <summary>ลิงก์ชำระ — ตัวตัดสินเดียวกับอีเมลตามตาราง (ค่าตั้งของกฎ › ผู้ติดต่อ › บริษัท) · กฎหาย ⇒ ลิงก์ที่มีอยู่แล้วเท่านั้น · ไม่มี ⇒ ไม่มีปุ่ม</summary>
    private async Task<string?> PayUrlAsync(LineQueue item, Document doc, CancellationToken ct)
    {
        if (_payLinks == null) return null;
        // รอบ 259 (E-H3): ส่งเองจากหน้าเอกสาร — ผู้ใช้เลือกเอง (ติ๊ก = ลิงก์ออกไว้แล้วตอนกด · ไม่ติ๊ก = ไม่มีปุ่ม)
        if (item.IncludePayLink is bool manual) return manual ? await _payLinks.CurrentUrlAsync(item.CompanyId, doc.Id, ct) : null;
        if (item.RuleId is Guid rid)
        {
            var rule = await _db.EmailScheduleRules.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == rid && r.CompanyId == item.CompanyId && !r.IsDeleted, ct);
            if (rule != null) return (await _payLinks.ForScheduledEmailAsync(rule, doc, ct))?.Url;
        }
        return await _payLinks.CurrentUrlAsync(item.CompanyId, doc.Id, ct);
    }

    /// <summary>ล้มถาวร/ข้าม ⇒ ถอยไปอีเมลครั้งเดียวต่อเหตุการณ์ เมื่อกฎตั้งไว้และไม่มีปลายทาง LINE อื่นของใบเดียวกันที่ส่งแล้ว/รอส่ง (Q7)</summary>
    private async Task FallbackAsync(LineQueue item, bool eligible, CancellationToken ct)
    {
        if (!item.FallbackToEmail || !eligible) return;
        var siblings = await _db.LineQueues.AsNoTracking()
            .Where(q => q.CompanyId == item.CompanyId && q.BaseKey == item.BaseKey && q.Id != item.Id)
            .Select(q => new { q.Status, q.FallbackQueuedAt })
            .ToListAsync(ct);
        var already = item.FallbackQueuedAt != null || siblings.Any(s => s.FallbackQueuedAt != null);
        if (!ContactLineChannel.FallbackNeeded(item.FallbackToEmail, eligible, already, siblings.Select(s => s.Status))) return;
        var why = await _emailSchedule.EnqueueLineFallbackEmailAsync(item.CompanyId, item.RuleId, item.EntityId, item.BaseKey, item.Trigger, ct);
        if (why == null) item.FallbackQueuedAt = DateTime.UtcNow;
        item.ErrorMessage = Trunc((item.ErrorMessage ?? "") + (why == null
            ? " ⇒ เข้าคิวอีเมลแทนแล้ว (ค่าตั้งถอยไปอีเมล)"
            : " ⇒ ถอยไปอีเมลไม่ได้: " + why), 2000);
    }

    private static void Finish(LineQueue item, string status, string reason, string? ruleCode)
    {
        item.Status = status;
        item.ErrorMessage = Trunc(reason, 2000);
        item.RuleCode = Trunc(ruleCode, 64);
        item.UpdatedAt = DateTime.UtcNow;
    }

    private static string? Trunc(string? s, int max) => s == null ? null : s.Length > max ? s[..max] : s;

    public async Task<List<LineQueueItemDto>> GetRecentAsync(Guid companyId, int take = 50, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);
        var rows = await _db.LineQueues.AsNoTracking()
            .Where(q => q.CompanyId == companyId && !q.IsDeleted)
            .OrderByDescending(q => q.CreatedAt).Take(take).ToListAsync(ct);
        var docIds = rows.Where(r => r.EntityType == "Document").Select(r => r.EntityId).Distinct().ToList();
        var numbers = docIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Documents.AsNoTracking().IgnoreQueryFilters()
                .Where(d => d.CompanyId == companyId && docIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.DocumentNumber, ct);
        var contactIds = rows.Where(r => r.EntityType == LineWhtCertCard.QueueEntityType).Select(r => r.ContactId).Distinct().ToList();
        var contactNames = contactIds.Count == 0 ? new Dictionary<Guid, string>()
            : await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                .Where(c => c.CompanyId == companyId && contactIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return rows.Select(r =>
        {
            var number = r.EntityType == "Document" && numbers.TryGetValue(r.EntityId, out var n) ? n : null;
            var subject = r.EntityType == LineWhtCertCard.QueueEntityType
                ? "ทวง 50 ทวิ · " + (contactNames.TryGetValue(r.ContactId, out var cn) ? cn : "ผู้ติดต่อ")
                : r.Trigger == ContactLineChannel.ManualTrigger ? "ส่งเอง · " + (number ?? "-") : number;
            return new LineQueueItemDto(r.Id, r.EntityId, number, r.Trigger,
                r.EndpointKind, string.IsNullOrEmpty(r.EndpointKind) ? "LINE (ไม่มีปลายทาง)" : NotifyEndpointKind.Label(r.EndpointKind),
                r.Status, ContactLineChannel.StatusLabel(r.Status), r.ScheduledFor, r.SentAt, r.RetryCount, r.ErrorMessage, r.RuleCode,
                CanRetry: r.Status == ContactLineChannel.StatusFailed && r.EndpointId != null,
                CanCancel: r.Status == ContactLineChannel.StatusPending,
                SubjectLabel: subject,
                RetryNote: ContactLineChannel.RetryNote(r.FallbackQueuedAt));
        }).ToList();
    }

    public async Task RetryFailedAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        var q = await _db.LineQueues.FirstOrDefaultAsync(x => x.Id == id && x.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบคิว LINE");
        // ส่งซ้ำได้เฉพาะแถวที่ล้ม (แถวที่ส่งแล้วไม่ส่งซ้ำ — แบบเดียวกับคิวอีเมล)
        if (q.Status != ContactLineChannel.StatusFailed || q.EndpointId == null)
            throw new BusinessRuleException($"ส่งใหม่ได้เฉพาะคิว LINE ที่ล้ม (สถานะตอนนี้: {ContactLineChannel.StatusLabel(q.Status)})",
                "LINE-QUEUE-RETRY-FAILED-ONLY", 409);
        q.Status = ContactLineChannel.StatusPending;
        q.RetryCount = 0;
        q.RetryKey = Guid.NewGuid();   // ผู้ใช้ตั้งใจส่งอีกครั้ง ⇒ key ใหม่ (DESIGN §3.e "ส่งซ้ำเอง")
        q.ScheduledFor = DateTime.UtcNow;
        q.ErrorMessage = null;
        q.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task CancelPendingAsync(Guid companyId, Guid id, CancellationToken ct = default)
    {
        // ฝ่ายค้าน fix2 L3: ยกเลิกแบบมีเงื่อนไขคำสั่งเดียว — ตัวส่งจองแถวไป (Sending) ระหว่างอ่าน-เขียน ⇒ ไม่ทับสถานะ (0 แถว = ยกเลิกไม่ได้แล้ว)
        var now = DateTime.UtcNow;
        var n = await _db.LineQueues
            .Where(x => x.Id == id && x.CompanyId == companyId && x.Status == ContactLineChannel.StatusPending)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, ContactLineChannel.StatusCancelled).SetProperty(x => x.UpdatedAt, (DateTime?)now), ct);
        if (n != 1) throw new KeyNotFoundException("ไม่พบคิว LINE ที่ยกเลิกได้ (อาจกำลังส่งหรือส่งไปแล้ว)");
    }
}
