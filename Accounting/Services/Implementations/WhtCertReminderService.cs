using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>ผลรันหนึ่งบริษัท — เข้าคิว / ข้ามเพราะผู้ติดต่อไม่รับ (บันทึกเหตุแล้ว)</summary>
public sealed record WhtCertReminderRunResult(int Queued, int SkippedOptOut);

/// <summary>
/// รอบ 253 (WP-10 · AUTO_SEND_WHT_DESIGN §4.3 เฟส 245 · คำตัดสินเจ้าของ §11 ข้อ 4) — <b>ทวงหนังสือรับรอง 50 ทวิ อัตโนมัติ</b> (ผู้เรียก: <c>WhtCertReminderJob</c> ใต้ JobLock)
///
/// <para>ต่อบริษัทที่เปิด <c>CompanySettings.WhtCertReminderEnabled</c> (ค่าตั้งต้นปิด) วันละครั้ง (watermark <c>WhtCertReminderLastRunAt</c>):
/// แถวที่ยังต้องการใบจากลูกค้า (ตัวโหลด/ตัวตัดสินเดียวกับป้าย 50 ทวิ — <c>WhtCertTracking</c> + <c>WhtCertIntakeStatus</c>) รวมรายลูกค้า →
/// จังหวะจาก <see cref="WhtCertReminderCadence.Decide"/> → ผู้รับจากตัวหาผู้รับเดียว (<see cref="IContactRecipientResolver"/> เรื่อง 50 ทวิ) · ผู้ติดต่อ
/// "ไม่ส่งอัตโนมัติ" ชนะ (<c>AutopilotSettingsResolver.AutoSendBlock</c>) · ไม่รับ ⇒ แถว "ข้าม" พร้อมเหตุ (ล้มดัง) · รับ ⇒ เข้าคิวอีเมลเดิม
/// (<c>EmailQueue</c> — ผู้ส่งคิวใช้ผู้ส่งในนามบริษัท + โควตา <c>PlatformEmailQuota</c> เดิม · ตรวจซ้ำตอนส่งด้วย <see cref="SendTimeSkipReasonAsync"/> ·
/// ลิงก์อัปโหลดออกตอนส่ง — คิวไม่เก็บ token)</para>
/// <para>ไม่สร้างแถวรอใบเอง (§4.3) · ไม่เรียก AI · LINE: รอบนี้ส่งทางอีเมลเท่านั้น (คิว LINE ผูกการ์ดเอกสาร — คำตัดสิน 253-WP10-5)</para>
/// </summary>
public class WhtCertReminderService
{
    private readonly AccountingDbContext _db;
    private readonly IContactRecipientResolver _recipients;
    private readonly ILogger<WhtCertReminderService> _logger;
    private readonly IConfiguration? _config;

    public WhtCertReminderService(AccountingDbContext db, IContactRecipientResolver recipients, ILogger<WhtCertReminderService> logger,
        IConfiguration? config = null)
    { _db = db; _recipients = recipients; _logger = logger; _config = config; }

    /// <summary>วันเริ่มต้นของ "วันนี้" ตามเวลาไทย (UTC) — watermark ต่อบริษัท: รันแล้วในวันไทยเดียวกัน ⇒ ข้าม</summary>
    internal static DateTime BangkokDayStartUtc(DateTime nowUtc) => nowUtc.AddHours(7).Date.AddHours(-7);

    /// <summary>บริษัทที่เปิดทวงและยังไม่ได้รันวันนี้ (watermark) — เก่าสุดก่อน</summary>
    public async Task<List<Guid>> DueCompaniesAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var dayStart = BangkokDayStartUtc(nowUtc);
        return await _db.CompanySettings.AsNoTracking()
            .Where(s => s.WhtCertReminderEnabled && (s.WhtCertReminderLastRunAt == null || s.WhtCertReminderLastRunAt < dayStart))
            .OrderBy(s => s.WhtCertReminderLastRunAt)
            .Select(s => s.CompanyId).Take(500).ToListAsync(ct);
    }

    /// <summary>หนึ่งรอบของงาน — ทุกบริษัทที่ถึงรอบ · บริษัทหนึ่งล้ม ⇒ log แล้วไปต่อ (watermark ไม่ขยับ = รอบหน้าลองใหม่ · ล้มดังใน log ระดับ Error)</summary>
    public async Task<int> RunDueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var cid in await DueCompaniesAsync(nowUtc, ct))
        {
            if (ct.IsCancellationRequested) break;
            try { total += (await RunCompanyAsync(cid, nowUtc, ct)).Queued; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ทวง 50 ทวิ อัตโนมัติของบริษัท {Company} ล้ม — รอบหน้าลองใหม่", cid);
                _db.ChangeTracker.Clear();
            }
        }
        return total;
    }

    public async Task<WhtCertReminderRunResult> RunCompanyAsync(Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var settings = await _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        if (settings?.WhtCertReminderEnabled != true) return new WhtCertReminderRunResult(0, 0);

        var rows = (await WhtCertTracking.LoadAsync(_db, companyId, nowUtc, statuses: WhtCertTracking.FollowUpStatuses, ct: ct))
            .Where(r => r.PayerContactId != null && r.WithheldSince != null && WhtCertUploadLinkService.AwaitsCustomer(r.Verdict.State)
                && WhtCreditableAmount.Missing(r.Status, r.WhtAmount, r.ReceivedAmount) > 0.005m)
            .ToList();
        var co = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId)
            .Select(c => new { c.Name, c.TaxId, c.BranchCode }).FirstAsync(ct);

        // ฝ่ายค้าน WP-10 M3: กุญแจ token ของลิงก์ในอีเมลทวง (อนุพันธ์จากความลับของเซิร์ฟเวอร์) — ไม่มี ⇒ โยน (ล้มดังทั้งบริษัท · watermark ไม่ขยับ)
        var linkKey = WhtCertUploadLinkPolicy.DeriveQueueKey(_config?["Jwt:Secret"]);
        int queued = 0, skipped = 0;
        foreach (var g in rows.GroupBy(r => r.PayerContactId!.Value))
        {
            var contactId = g.Key;
            var anchor = DateTime.SpecifyKind(g.Min(r => r.WithheldSince!.Value).Date, DateTimeKind.Utc);
            var logs = await _db.WhtCertReminderLogs.AsNoTracking()
                .Where(l => l.CompanyId == companyId && l.ContactId == contactId)
                .Select(l => new { l.Id, l.Step, l.AnchorDate, l.CreatedAt, l.Outcome }).ToListAsync(ct);
            var history = logs.Where(l => l.Outcome == "Queued")
                .Select(l => new WhtCertReminderCadence.Sent(l.Step, l.AnchorDate, l.CreatedAt)).ToList();
            var d = WhtCertReminderCadence.Decide(true, anchor, history, nowUtc);
            if (!d.Send || d.Step is not int step) continue;
            var existing = logs.FirstOrDefault(l => l.Step == step && l.AnchorDate.Date == anchor.Date);
            // ขั้นนี้เข้าคิวแล้ว ⇒ ไม่ทำซ้ำ (ไม่ต้องถามผู้รับ)
            if (existing != null && existing.Outcome != WhtCertReminderCadence.OutcomeSkipped) continue;

            var contact = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
                .Where(c => c.Id == contactId && c.CompanyId == companyId)
                .Select(c => new { c.Name, c.AutoSendDisabled }).FirstOrDefaultAsync(ct);
            // รอบ 259 (258-3-6): ชุดผู้รับเดียว (เรื่อง 50 ทวิ) ⇒ อีเมล + ปลายทาง LINE · ตัวตัดสินช่องทางเดียว LineWhtCertCard.Channels
            var recipientSet = await _recipients.ResolveAsync(companyId, contactId, NotifyTopic.Wht, ct);
            var delivery = ContactRecipientRule.ForEmail(recipientSet);
            var lineEndpoints = ContactLineChannel.LineEndpoints(recipientSet.Options);
            var channels = LineWhtCertCard.Channels(AutopilotSettingsResolver.AutoSendBlock(contact?.AutoSendDisabled == true), recipientSet.ContactDeleted,
                delivery.Deliverable, delivery.OptOutReason, lineEndpoints.Count);
            var optOut = channels.SkipReason;
            if (existing != null)
            {
                // รอบ 257 ชุด 2 (B-L2): ขั้นนี้เคย "ข้าม" (ไม่มีอีเมล/ไม่รับ) — ยังส่งไม่ได้ ⇒ ไม่บันทึกซ้ำทุกวัน · ตอนนี้ส่งได้ (เพิ่มอีเมลแล้ว) ⇒ ลองใหม่ครั้งเดียว:
                // ถอดแถว "ข้าม" เดิม (unique ขั้น/ลูกค้า/วันถูกหัก นับเฉพาะแถวที่ไม่ถูกลบ) แล้วเข้าคิวตามปกติ (เดิมข้ามถาวร)
                if (!WhtCertReminderCadence.RetrySkippedStep(existing.Outcome, optOut == null)) continue;
                var stale = await _db.WhtCertReminderLogs.FirstOrDefaultAsync(l => l.Id == existing.Id && l.CompanyId == companyId, ct);
                if (stale != null)
                {
                    stale.IsDeleted = true;
                    stale.UpdatedAt = nowUtc;
                    await _db.SaveChangesAsync(ct);
                }
            }
            var missing = Math.Round(g.Sum(r => WhtCreditableAmount.Missing(r.Status, r.WhtAmount, r.ReceivedAmount)), 2, MidpointRounding.AwayFromZero);
            var log = new WhtCertReminderLog
            {
                CompanyId = companyId, ContactId = contactId, Step = step, AnchorDate = anchor, MissingAmount = missing,
                CreditIdsJson = System.Text.Json.JsonSerializer.Serialize(g.Select(r => r.CreditId).ToList()), CreatedBy = "wht-cert-reminder",
            };
            if (optOut != null)
            {
                log.Outcome = WhtCertReminderCadence.OutcomeSkipped;
                log.Reason = optOut;
                skipped++;
            }
            else
            {
                var baseKey = WhtCertReminderCadence.QueueKey(contactId, step, anchor);
                if (channels.Email && await _db.EmailQueues.AnyAsync(x => x.CompanyId == companyId && x.IdempotencyKey == baseKey && !x.IsDeleted, ct)) continue;
                // รอบ 259 (258-3-6): การ์ด LINE ต่อปลายทางที่รับเรื่อง 50 ทวิ — แถวคิวชนิด WhtCertRequest + ลิงก์อัปโหลดต่อแถว (ธุรกรรมเดียวกับบันทึกทวง)
                var lineQueued = 0;
                if (channels.Line)
                {
                    foreach (var ep in lineEndpoints)
                    {
                        var key = LineWhtCertCard.QueueKey(baseKey, ep.EndpointId!.Value);
                        if (await _db.LineQueues.AnyAsync(x => x.CompanyId == companyId && x.IdempotencyKey == key, ct)) continue;
                        var lq = new LineQueue
                        {
                            CompanyId = companyId, RuleId = null, EntityType = LineWhtCertCard.QueueEntityType, EntityId = contactId, ContactId = contactId,
                            EndpointId = ep.EndpointId, EndpointKind = ep.Kind, Topic = NotifyTopic.Wht.ToString(), Trigger = LineWhtCertCard.QueueTrigger,
                            ScheduledFor = nowUtc, Status = ContactLineChannel.StatusPending, RetryKey = ContactLineChannel.RetryKey(companyId, key),
                            IdempotencyKey = key, BaseKey = baseKey, FallbackToEmail = false, CreatedBy = "wht-cert-reminder",
                        };
                        _db.LineQueues.Add(lq);
                        if (LineWhtCertCard.UploadLinkAllowed(ep.Kind == NotifyEndpointKind.LineGroup, ep.ShowAmounts))
                            WhtCertUploadLinkService.AddLinkRow(_db, companyId, contactId, null,
                                WhtCertUploadLinkPolicy.TokenHash(WhtCertUploadLinkPolicy.TokenForLineQueue(linkKey, companyId, lq.Id)),
                                "reminder-line", null, "wht-cert-reminder", null, lq.Id);
                        lineQueued++;
                    }
                }
                log.LineQueuedCount = lineQueued;
                log.Reason = d.Reason;
                if (!channels.Email)
                {
                    if (lineQueued == 0) continue;   // ทุกปลายทางเข้าคิวไปแล้ว (อีกเครื่อง/รอบก่อน) — ไม่บันทึกซ้ำ
                    log.Reason = d.Reason + $" · ส่งทาง LINE {lineQueued} ปลายทาง (ไม่มีอีเมลที่รับเรื่อง 50 ทวิ)";
                    queued++;
                }
            }
            if (optOut == null && channels.Email)
            {
                var (to, _) = ContactRecipientRule.CapForSender(delivery.To!, platformSender: true, 0, SignEmailThrottle.MaxRecipients,
                    EmailScheduleService.MaxToEmailLength);
                var items = g.GroupBy(r => r.DocumentId)
                    .Select(x => new WhtCertReminderCadence.Item(x.First().DocumentNumber, x.Min(r => r.WithheldSince),
                        Math.Round(x.Sum(r => WhtCreditableAmount.Missing(r.Status, r.WhtAmount, r.ReceivedAmount)), 2, MidpointRounding.AwayFromZero)))
                    .OrderBy(i => i.WithheldSinceUtc).ToList();
                var q = new EmailQueue
                {
                    CompanyId = companyId, EntityType = WhtCertReminderCadence.QueueEntityType, EntityId = contactId,
                    ToEmail = to,
                    Subject = WhtCertReminderCadence.Subject(co.Name, step),
                    Body = WhtCertReminderCadence.BodyHtml(co.Name, co.TaxId, TaxBranchCode.Label(co.BranchCode), contact?.Name ?? "ลูกค้า", step, items),
                    AttachPdf = false, ScheduledFor = nowUtc, Status = "Pending",
                    IdempotencyKey = WhtCertReminderCadence.QueueKey(contactId, step, anchor), CreatedBy = "wht-cert-reminder",
                };
                _db.EmailQueues.Add(q);
                // ฝ่ายค้าน WP-10 M3: ลิงก์อัปโหลดของฉบับนี้สร้างพร้อมแถวคิว (ธุรกรรมเดียว) — ผู้ส่งคิวคำนวณ token ซ้ำได้ ไม่ต้องเขียนฐานตอนส่ง
                WhtCertUploadLinkService.AddLinkRow(_db, companyId, contactId, null,
                    WhtCertUploadLinkPolicy.TokenHash(WhtCertUploadLinkPolicy.TokenForQueue(linkKey, companyId, q.Id)),
                    "reminder-email", null, "wht-cert-reminder", q.Id);
                log.EmailQueueId = q.Id;
                if (log.LineQueuedCount > 0) log.Reason = d.Reason + $" · ส่งทาง LINE ด้วย {log.LineQueuedCount} ปลายทาง";
                queued++;
            }
            _db.WhtCertReminderLogs.Add(log);
            try { await _db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                // อีกเครื่องบันทึกขั้นนี้ไปก่อน (unique ขั้น/ลูกค้า/วันถูกหัก) — ไม่ส่งซ้ำ
                _db.ChangeTracker.Clear();
                if (optOut == null) queued--; else skipped--;
            }
        }
        // watermark ต่อบริษัท — ขยับหลังประมวลผลครบ (ล้มกลางทาง = รอบหน้าทำต่อ · แถวที่เข้าคิวแล้วกันซ้ำด้วยกุญแจ)
        var fresh = await _db.CompanySettings.FirstAsync(s => s.CompanyId == companyId, ct);
        fresh.WhtCertReminderLastRunAt = nowUtc;
        await _db.SaveChangesAsync(ct);
        return new WhtCertReminderRunResult(queued, skipped);
    }

    /// <summary>
    /// ผู้ส่งคิวตรวจซ้ำตอนส่ง (คลาสเดียวกับ <c>RecheckDocumentAtSendAsync</c>) — บริษัทปิดทวงแล้ว · ผู้ติดต่อตั้ง "ไม่ส่งอัตโนมัติ" · ลูกค้าส่งใบครบ/ส่งแล้วรอตรวจ
    /// ⇒ เหตุ (แถวคิว "ข้าม") · null = ส่งได้ · ทุก query ผูก CompanyId
    /// </summary>
    internal static async Task<string?> SendTimeSkipReasonAsync(AccountingDbContext db, IContactRecipientResolver recipients, EmailQueue item,
        CancellationToken ct)
    {
        if (await CompanyContactSkipReasonAsync(db, item.CompanyId, item.EntityId, ct) is string pre) return pre;
        var delivery = ContactRecipientRule.ForEmail(await recipients.ResolveAsync(item.CompanyId, item.EntityId, NotifyTopic.Wht, ct));
        if (!delivery.Deliverable) return delivery.OptOutReason ?? "ผู้ติดต่อไม่มีอีเมลที่รับเรื่อง 50 ทวิ แล้ว — ไม่ส่งอีเมลทวง";
        var queuedTo = SignEmailThrottle.ParseRecipients(new[] { item.ToEmail }).Valid.Select(e => e.Trim().ToLowerInvariant()).ToList();
        if (queuedTo.Count > 0 && !queuedTo.Any(e => delivery.Emails.Contains(e)))
            return "ผู้รับในคิวไม่รับเรื่อง 50 ทวิ แล้ว (ช่องทางแจ้งเตือนของผู้ติดต่อเปลี่ยน) — ไม่ส่ง · รอบหน้าจะเข้าคิวถึงผู้รับปัจจุบัน";
        return (await AwaitingItemsAsync(db, item.CompanyId, item.EntityId, ct)).Count > 0
            ? null : "ลูกค้าส่งหนังสือรับรองครบแล้ว (หรือส่งแล้วรอตรวจ) — ไม่ส่งอีเมลทวง";
    }

    /// <summary>รอบ 259 (258-3-6): ด่านร่วมของผู้ส่งทั้งสองช่อง (อีเมล · การ์ด LINE) — บริษัทปิดทวง · ผู้ติดต่อถูกลบ · "ไม่ส่งอัตโนมัติ" ⇒ เหตุ · null = ไปต่อ</summary>
    internal static async Task<string?> CompanyContactSkipReasonAsync(AccountingDbContext db, Guid companyId, Guid contactId, CancellationToken ct)
    {
        var enabled = await db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => s.WhtCertReminderEnabled).FirstOrDefaultAsync(ct);
        if (!enabled) return "บริษัทปิดทวง 50 ทวิ อัตโนมัติแล้ว — ไม่ส่ง";
        // ฝ่ายค้าน WP-10 L2: ผู้ติดต่อที่ถูกลบ (soft delete) ⇒ ไม่ส่ง · ยกเลิกรับเรื่อง 50 ทวิ หลังเข้าคิว ⇒ ไม่ส่ง (ถามตัวหาผู้รับเดียวซ้ำ)
        var contact = await db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == contactId && c.CompanyId == companyId).Select(c => new { c.AutoSendDisabled, c.IsDeleted }).FirstOrDefaultAsync(ct);
        if (contact == null || contact.IsDeleted) return ContactRecipientRule.ContactDeletedReason;
        return AutopilotSettingsResolver.AutoSendBlock(contact.AutoSendDisabled);
    }

    /// <summary>รอบ 259 (258-3-6): รายการที่ยังรอใบจากลูกค้าตอนนี้ (ตัวโหลด/ตัวตัดสินเดียวกับงานทวง) รวมรายเอกสาร — ว่าง = ส่งครบ/ส่งแล้วรอตรวจ ⇒ ไม่ทวง ·
    /// ผู้ใช้: ด่านตอนส่งของอีเมล + เนื้อการ์ด LINE (สดตอนส่ง)</summary>
    internal static async Task<List<WhtCertReminderCadence.Item>> AwaitingItemsAsync(AccountingDbContext db, Guid companyId, Guid contactId, CancellationToken ct)
    {
        var rows = await WhtCertTracking.LoadAsync(db, companyId, DateTime.UtcNow, contactId: contactId,
            statuses: WhtCertTracking.FollowUpStatuses, ct: ct);
        return rows.Where(r => WhtCertUploadLinkService.AwaitsCustomer(r.Verdict.State)
                && WhtCreditableAmount.Missing(r.Status, r.WhtAmount, r.ReceivedAmount) > 0.005m)
            .GroupBy(r => r.DocumentId)
            .Select(x => new WhtCertReminderCadence.Item(x.First().DocumentNumber, x.Min(r => r.WithheldSince),
                Math.Round(x.Sum(r => WhtCreditableAmount.Missing(r.Status, r.WhtAmount, r.ReceivedAmount)), 2, MidpointRounding.AwayFromZero)))
            .OrderBy(i => i.WithheldSinceUtc).ToList();
    }
}
