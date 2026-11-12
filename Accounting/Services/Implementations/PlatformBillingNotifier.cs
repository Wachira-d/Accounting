using System.Net;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 2 (G-7 · คำตัดสินข้อ 180) — <b>ผู้ส่งใบแจ้งหนี้/แจ้งเตือนค่าบริการ NextAcc ถึงผู้เช่าตัวเดียว</b> (แพลตฟอร์ม → เจ้าของบริษัท)
/// <list type="bullet">
/// <item>ช่อง: อีเมลระบบ (<c>IEmailService.TrySendAsync</c>) · LINE ผ่าน OA กลางของ NextAcc (<c>TryPushViaNextAccAsync</c> — <b>ไม่นับโควตาข้อความ</b>
/// ของร้าน: เป็นข้อความของแพลตฟอร์มเอง) · เปิด/ปิดตามค่าตั้ง</item>
/// <item><b>idempotent</b>: จองแถว <c>PlatformBillingNotices</c> (unique MessageKey+Channel · <c>INSERT … ON CONFLICT DO NOTHING</c>) <b>ก่อน</b>ส่ง ⇒ หลายเครื่อง/รันซ้ำ
/// ส่งได้ครั้งเดียว · ล้มกลางทาง = แถวค้าง Claimed (ยอมพลาด 1 ฉบับดีกว่าส่งซ้ำ) · ผลจริงประทับ Sent/Failed/Skipped + เหตุ (แอดมินเห็น — ล้มดัง)</item>
/// <item>เนื้อความ HtmlEncode ทุกช่องที่มาจากข้อมูล (กฎ #4 C)</item>
/// </list>
/// </summary>
public interface IPlatformBillingNotifier
{
    /// <summary>ส่ง "ใบแจ้งหนี้ใหม่" ของใบนี้ (คีย์ issued — ส่งครั้งเดียวต่อใบ) · ไม่โยน</summary>
    Task InvoiceIssuedAsync(Guid buyerCompanyId, Guid invoiceDocumentId, CancellationToken ct = default);

    /// <summary>ส่งข้อความตามคีย์ (ทุกช่องที่เปิด) · คืนจำนวนช่องที่ส่งถึง · ไม่โยน</summary>
    Task<int> SendAsync(Guid buyerCompanyId, Guid? documentId, string messageKey, string subject, string body, CancellationToken ct = default);

    /// <summary>รอบ 262 (คำตัดสิน 262-1-5): ใบเสร็จ/ใบกำกับค่าบริการออกแล้ว ⇒ การ์ดใบเสร็จถึงปลายทาง LINE ของผู้ติดต่อของผู้เช่า (คิว LINE ของบริษัทผู้ให้บริการ) ·
    /// ครั้งเดียวต่อใบ · ไม่โยน (เงินเข้าแล้ว การแจ้งต้องไม่ทำให้การอนุมัติพัง)</summary>
    Task ReceiptIssuedAsync(Guid buyerCompanyId, Guid receiptDocumentId, CancellationToken ct = default);
}

public class PlatformBillingNotifier : IPlatformBillingNotifier
{
    public const string ChannelEmail = "email", ChannelLine = "line";
    /// <summary>รอบ 262: การ์ดเอกสารผ่านคิว LINE ของผู้ติดต่อ (บริษัทผู้ให้บริการ · OA กลาง) — แถวจองนี้บอกว่า "เข้าคิวแล้ว" (Queued) ไม่ใช่ "ส่งแล้ว" ·
    /// ผลส่งจริงอยู่บนแถวคิว LINE (ส่งแล้ว = LINE ตอบรับเท่านั้น)</summary>
    public const string ChannelLineContact = "line-contact";
    public const string StatusClaimed = "Claimed", StatusSent = "Sent", StatusFailed = "Failed", StatusSkipped = "Skipped", StatusQueued = "Queued";
    private const string SystemActor = "system:platform-billing";
    public const string BillingPath = "/pages/billing.html";

    private readonly AccountingDbContext _db;
    private readonly IEmailService? _email;
    private readonly ILineNotifyService? _line;
    private readonly ILogger<PlatformBillingNotifier> _logger;
    /// <summary>รอบ 262: ตัวหาผู้รับเดียว (ปลายทาง LINE ของผู้ติดต่อ + ความยินยอม + ด่าน OA) · null (นอก DI/เทสต์เดิม) ⇒ ไม่มีการ์ดผ่านคิว (พฤติกรรมเดิม)</summary>
    private readonly IContactRecipientResolver? _recipients;
    private readonly ISecretProtector? _secrets;

    public PlatformBillingNotifier(AccountingDbContext db, ILogger<PlatformBillingNotifier> logger,
        IEmailService? email = null, ILineNotifyService? line = null, IContactRecipientResolver? recipients = null, ISecretProtector? secrets = null)
    { _db = db; _logger = logger; _email = email; _line = line; _recipients = recipients; _secrets = secrets; }

    public async Task InvoiceIssuedAsync(Guid buyerCompanyId, Guid invoiceDocumentId, CancellationToken ct = default)
    {
        try
        {
            var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);
            if (platformId == null) return;
            var inv = await PlatformInvoiceQueries.ForBuyer(_db, platformId.Value, buyerCompanyId)
                .Where(d => d.Id == invoiceDocumentId).AsRows(buyerCompanyId).FirstOrDefaultAsync(ct);
            if (inv == null) return;
            var (subject, body) = PlatformBillingMessages.For(PlatformDunningPolicy.KindIssued, 0, inv);
            await SendAsync(buyerCompanyId, inv.Id, PlatformDunningPolicy.Key(inv.Id, PlatformDunningPolicy.KindIssued, 0), subject, body, ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "ส่งใบแจ้งหนี้ค่าบริการไม่สำเร็จ (company {Company} · ใบ {Doc})", buyerCompanyId, invoiceDocumentId); }
    }

    public async Task<int> SendAsync(Guid buyerCompanyId, Guid? documentId, string messageKey, string subject, string body, CancellationToken ct = default)
    {
        var delivered = 0;
        try
        {
            var policy = await PlatformBillingSettingsService.LoadPolicyAsync(_db, ct);
            var owners = await _db.CompanyUsers.AsNoTracking()
                .Where(cu => cu.CompanyId == buyerCompanyId && cu.Role == UserRole.Owner)
                .Join(_db.Users.AsNoTracking(), cu => cu.UserId, u => u.Id, (cu, u) => new { u.Email, u.FullName, u.LineUserId })
                .ToListAsync(ct);
            var baseUrl = (await _db.SiteSettings.AsNoTracking().OrderBy(s => s.CreatedAt).Select(s => s.AppBaseUrl).FirstOrDefaultAsync(ct))?.TrimEnd('/');
            var link = (baseUrl ?? "") + ProviderCentralOa.BillingPath(buyerCompanyId);   // ฝ่ายค้าน L4: ศูนย์บิลของบริษัทนี้

            if (policy.ReminderEmailEnabled)
            {
                var claim = await ClaimAsync(buyerCompanyId, documentId, messageKey, ChannelEmail, ct);
                if (claim is Guid id)
                {
                    var to = owners.Where(o => !string.IsNullOrWhiteSpace(o.Email)).ToList();
                    if (_email == null || to.Count == 0) await FinishAsync(id, StatusSkipped, _email == null ? "ไม่มีบริการอีเมล" : "ไม่มีอีเมลของเจ้าของบริษัท", ct);
                    else
                    {
                        var ok = 0;
                        var html = $"<div style='font-family:sans-serif;max-width:600px'><h2 style='color:#4F46E5'>{WebUtility.HtmlEncode(subject)}</h2>"
                            + $"<p style='white-space:pre-line'>{WebUtility.HtmlEncode(body)}</p>"
                            + $"<p><a href='{WebUtility.HtmlEncode(link)}'>เปิดศูนย์บิลค่าบริการ</a></p></div>";
                        foreach (var o in to) if (await _email.TrySendAsync(o.Email!, subject, html)) ok++;
                        await FinishAsync(id, ok > 0 ? StatusSent : StatusFailed, ok > 0 ? $"ส่งถึง {ok}/{to.Count}" : "SMTP ไม่ได้ตั้ง/ส่งล้ม", ct);
                        if (ok > 0) delivered++;
                    }
                }
            }
            if (policy.ReminderLineEnabled)
            {
                // รอบ 262 (คำตัดสิน 262-1-4/262-1-5): การ์ดเอกสาร (ปุ่มชำระ = ศูนย์บิล) ถึงปลายทาง LINE ของผู้ติดต่อของผู้เช่าก่อน แล้วข้อความตรงถึงเจ้าของ
                // เฉพาะคนที่ยังไม่ได้การ์ด (LINE id เดียวกันผ่าน OA กลาง ⇒ ไม่ส่งซ้ำ — ช่องเดียว)
                var cards = await QueueContactCardsAsync(buyerCompanyId, documentId, messageKey,
                    ProviderCentralOa.TriggerForKind(PlatformDunningPolicy.KindOfKey(messageKey)), customerInitiated: false, ct);
                // ฝ่ายค้าน L6: การ์ดที่เข้าคิว = ช่อง LINE ทำงานแล้ว (ผลส่งจริงอยู่บนคิว) — ตัวนับของการทวงไม่ขึ้น 0 เมื่อไปทางการ์ดอย่างเดียว
                if (cards.Queued > 0) delivered++;
                var claim = await ClaimAsync(buyerCompanyId, documentId, messageKey, ChannelLine, ct);
                if (claim is Guid id)
                {
                    var withLine = owners.Where(o => !string.IsNullOrWhiteSpace(o.LineUserId)).ToList();
                    var to = ProviderCentralOa.OwnersNotCovered(withLine, o => o.LineUserId, cards.LineIds);
                    if (_line == null || to.Count == 0)
                        await FinishAsync(id, StatusSkipped, _line == null ? "ไม่มีบริการ LINE"
                            : withLine.Count > 0 ? $"เจ้าของบริษัทได้การ์ดเอกสารทางคิว LINE ของผู้ติดต่อแล้ว ({cards.LineIds.Count} ปลายทาง) — ไม่ส่งข้อความซ้ำ"
                            : "เจ้าของบริษัทยังไม่ผูก LINE", ct);
                    else
                    {
                        var ok = 0; string? err = null;
                        foreach (var o in to)
                        {
                            // รอบ 232 (232-Notify · ฝ่ายค้าน N1): เรื่องบัญชีของผู้เช่ากับแพลตฟอร์ม → เจ้าของบริษัทนั้นผ่าน OA กลาง = PlatformAccount (NOTIFY-ACCOUNT-OWNER-NEXTACC)
                            var r = await _line.TryPushViaNextAccAsync(o.LineUserId!, new[] { $"{subject}\n\n{body}\n\n🔗 {link}" }, NotificationAudience.PlatformAccount);
                            if (r.Ok) ok++; else err = r.Error;
                        }
                        await FinishAsync(id, ok > 0 ? StatusSent : StatusFailed, ok > 0 ? $"ส่งถึง {ok}/{to.Count}" : (err ?? "ส่งไม่สำเร็จ"), ct);
                        if (ok > 0) delivered++;
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogError(ex, "ส่งแจ้งเตือนค่าบริการไม่สำเร็จ (company {Company} · คีย์ {Key})", buyerCompanyId, messageKey); }
        return delivered;
    }

    public async Task ReceiptIssuedAsync(Guid buyerCompanyId, Guid receiptDocumentId, CancellationToken ct = default)
    {
        try
        {
            var policy = await PlatformBillingSettingsService.LoadPolicyAsync(_db, ct);
            if (!policy.ReminderLineEnabled) return;   // ช่อง LINE ของการแจ้งค่าบริการปิด ⇒ ใบเสร็จไปทางอีเมลตามเดิมเท่านั้น
            // ผู้เช่าเพิ่งจ่าย ⇒ ใบเสร็จไม่รอชั่วโมงเงียบ (customerInitiated — ตัวตัดสินเดียว LineQuietHours.IsExempt)
            await QueueContactCardsAsync(buyerCompanyId, receiptDocumentId, ProviderCentralOa.ReceiptMessageKey(receiptDocumentId),
                ContactLineChannel.ReceiptTrigger, customerInitiated: true, ct);
        }
        catch (Exception ex) { _logger.LogError(ex, "เข้าคิวการ์ดใบเสร็จค่าบริการทาง LINE ไม่สำเร็จ (company {Company} · ใบ {Doc})", buyerCompanyId, receiptDocumentId); }
    }

    private sealed record ContactCards(int Queued, IReadOnlyCollection<string> LineIds)
    {
        public static ContactCards None { get; } = new(0, Array.Empty<string>());
    }

    /// <summary>รอบ 262 (คำตัดสิน 262-1-4/262-1-5) — การ์ดเอกสารค่าบริการเข้าคิว LINE ของผู้ติดต่อของผู้เช่าในบริษัทผู้ให้บริการ:
    /// <list type="number">
    /// <item>ใบต้องเป็นของผู้ซื้อรายนี้ (ผู้ติดต่อผูก tenant = <paramref name="buyerCompanyId"/> · บริษัทผู้ให้บริการเท่านั้น) — ใบของบริษัทอื่นไม่มีทางถึงผู้ติดต่อรายนี้</item>
    /// <item>จองคีย์ข้อความช่อง <see cref="ChannelLineContact"/> (ข้ามเครื่อง/รันซ้ำเข้าคิวครั้งเดียว) → ผู้ติดต่อ "ไม่ส่งอัตโนมัติ" ชนะ → ตัวหาผู้รับเดียว (เรื่องตาม trigger)</item>
    /// <item>หนึ่งแถวต่อปลายทาง · กุญแจกันซ้ำ <see cref="ContactLineChannel.QueueKey"/> · ปลายทางที่ได้การ์ด trigger เดียวกันจากทางอื่นแล้ว (กฎตารางส่ง) ไม่เข้าคิวซ้ำ</item>
    /// <item>ผู้ส่ง = <c>ContactLineQueueService</c> ตัวเดิม (ชั่วโมงเงียบ · ตรวจใบ/ปลายทางซ้ำตอนส่ง · กระดิ่งล้ม · ปุ่มชำระ = ศูนย์บิล) · ไม่ถอยไปอีเมล (ช่องอีเมลของงานนี้ตัดสินเอง)</item>
    /// </list>
    /// คืน LINE id (OA กลาง) ของปลายทางผู้ใช้ที่ได้การ์ด — ผู้เรียกไม่ส่งข้อความตรงซ้ำถึงคนเดียวกัน</summary>
    private async Task<ContactCards> QueueContactCardsAsync(Guid buyerCompanyId, Guid? documentId, string messageKey, string? trigger, bool customerInitiated,
        CancellationToken ct)
    {
        if (_recipients == null || documentId is not Guid docId || trigger == null) return ContactCards.None;
        var platformId = await PlatformInvoiceQueries.PlatformTenantIdAsync(_db, ct);
        if (platformId is not Guid pid) return ContactCards.None;
        var key = buyerCompanyId.ToString();
        var doc = await (from d in _db.Documents.AsNoTracking()
                         join c in _db.Contacts.AsNoTracking() on d.ContactId equals c.Id
                         where d.Id == docId && d.CompanyId == pid && !d.IsDeleted
                               && c.CompanyId == pid && c.ExternalSystem == PlatformInvoiceRules.TenantContactSystem && c.ExternalId == key
                         select new { d.Id, d.ContactId }).FirstOrDefaultAsync(ct);
        if (doc == null) return ContactCards.None;
        var claim = await ClaimAsync(buyerCompanyId, docId, messageKey, ChannelLineContact, ct);
        if (claim is not Guid claimId) return ContactCards.None;   // เข้าคิวไปแล้ว (ข้อความตรงของขั้นนี้ก็จองไปแล้วเช่นกัน)

        // ฝ่ายค้าน M2 (262-1-11): ผู้เช่าหนึ่งรายมีผู้ติดต่อได้หลายแถว (เปลี่ยนเลขภาษี/สาขา ⇒ แถวใหม่) ⇒ รวมปลายทางของ<b>ทุกแถว</b>ที่ ExternalId = ผู้เช่า
        // (แถวคิวถือผู้ติดต่อเจ้าของปลายทาง — ผู้ส่งตรวจปลายทางกับผู้ติดต่อนั้น) · ผู้ติดต่อ "ไม่ส่งอัตโนมัติ" ชนะต่อแถว
        var contactIds = await PlatformInvoiceQueries.BuyerContactIdsAsync(_db, pid, buyerCompanyId, ct);
        if (!contactIds.Contains(doc.ContactId)) contactIds.Insert(0, doc.ContactId);
        var autoOff = await _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.CompanyId == pid && contactIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.AutoSendDisabled, ct);
        var targets = new List<(Guid ContactId, ContactRecipientRule.RecipientOption Endpoint)>();
        string? notSent = null;
        foreach (var cid in contactIds)
        {
            var set = await _recipients.ResolveAsync(pid, cid, NotifyTopics.ForTrigger(trigger), ct);
            var plan = ContactLineChannel.Plan(channelEmail: false, lineUser: true, lineGroup: true, fallbackToEmail: false, set.Options,
                AutopilotSettingsResolver.AutoSendBlock(autoOff.GetValueOrDefault(cid)));
            if (plan.Endpoints.Count == 0) { if (cid == doc.ContactId) notSent = plan.NotSent; continue; }
            targets.AddRange(plan.Endpoints.Where(e => targets.All(t => t.Endpoint.EndpointId != e.EndpointId)).Select(e => (cid, e)));
        }
        if (targets.Count == 0)
        {
            await FinishAsync(claimId, StatusSkipped, notSent ?? "ผู้ติดต่อยังไม่มีปลายทาง LINE ที่รับเรื่องนี้", ct);
            return ContactCards.None;
        }
        var baseKey = ProviderCentralOa.QueueBaseKey(docId, trigger, messageKey);
        var endpointIds = targets.Select(t => t.Endpoint.EndpointId!.Value).ToList();
        // ฝ่ายค้าน M3 (262-1-12): กันซ้ำเฉพาะการ์ดของ<b>ทางอื่น</b> (กฎตารางส่ง/ส่งเอง) — การ์ดค่าบริการขั้นก่อนของงานนี้ไม่กลบขั้นถัดไป (ก่อนครบ → ครบวันนี้)
        var already = await PlatformInvoiceQueries.CardedEndpointsAsync(_db, pid, docId, trigger, endpointIds, fromPlatform: false, ct);
        var now = DateTime.UtcNow;
        var added = 0;
        foreach (var (cid, ep) in targets)
        {
            if (already.Contains(ep.EndpointId!.Value)) continue;
            var queueKey = ContactLineChannel.QueueKey(baseKey, ep.EndpointId);
            _db.LineQueues.Add(new LineQueue
            {
                CompanyId = pid, RuleId = null, EntityType = "Document", EntityId = docId, ContactId = cid,
                EndpointId = ep.EndpointId, EndpointKind = ep.Kind, Topic = NotifyTopics.ForTrigger(trigger).ToString(), Trigger = trigger,
                ScheduledFor = now, Status = ContactLineChannel.StatusPending, RetryKey = ContactLineChannel.RetryKey(pid, queueKey),
                IdempotencyKey = queueKey, BaseKey = baseKey, FallbackToEmail = false, CreatedBy = SystemActor, CustomerInitiated = customerInitiated,
            });
            added++;
        }
        if (added > 0) await _db.SaveChangesAsync(ct);
        // ปลายทางผู้ใช้ที่ได้การ์ด (ใหม่หรือจากกฎตารางส่ง) — LINE id ของ OA กลางเท่านั้น (เทียบกับ User.LineUserId ที่ผูกกับบอทกลาง)
        var covered = new List<string>();
        if (_secrets != null)
        {
            var userEps = targets.Where(t => t.Endpoint.Kind == NotifyEndpointKind.LineUser).Select(t => t.Endpoint.EndpointId!.Value).ToList();
            var protectedIds = await _db.ContactNotifyEndpoints.AsNoTracking()
                .Where(e => e.CompanyId == pid && userEps.Contains(e.Id) && e.OaScope == NotifyEndpointKind.ScopeNextAcc)
                .Select(e => e.LineUserId).ToListAsync(ct);
            covered.AddRange(protectedIds.Select(v => _secrets.Unprotect(v)).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()));
        }
        await FinishAsync(claimId, StatusQueued, added > 0
            ? $"เข้าคิว LINE ของผู้ติดต่อ {added} ปลายทาง (ผลส่งจริง: บริษัทผู้ให้บริการ → ตารางส่ง › คิว LINE)"
            : "ปลายทาง LINE ของผู้ติดต่อได้การ์ดเรื่องนี้จากกฎตารางส่ง/การส่งเองของบริษัทผู้ให้บริการแล้ว — ไม่เข้าคิวซ้ำ", ct);
        return new ContactCards(added, covered);
    }

    /// <summary>จองสิทธิ์ส่ง — คืน id ของแถวที่จองได้ · null = ส่งไปแล้ว/อีกเครื่องกำลังส่ง</summary>
    private async Task<Guid?> ClaimAsync(Guid buyerCompanyId, Guid? documentId, string key, string channel, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var n = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""PlatformBillingNotices"" (""Id"", ""BuyerCompanyId"", ""DocumentId"", ""MessageKey"", ""Channel"", ""Status"", ""CreatedAt"", ""IsDeleted"")
            VALUES ({id}, {buyerCompanyId}, {documentId}, {key}, {channel}, {StatusClaimed}, {now}, false)
            ON CONFLICT (""MessageKey"", ""Channel"") DO NOTHING", ct);
        return n == 1 ? id : null;
    }

    private async Task FinishAsync(Guid id, string status, string? detail, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var d = detail is { Length: > 500 } ? detail[..500] : detail;
        DateTime? sentAt = status == StatusSent ? now : null;
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""PlatformBillingNotices"" SET ""Status"" = {status}, ""Detail"" = {d}, ""SentAt"" = {sentAt}, ""UpdatedAt"" = {now} WHERE ""Id"" = {id}", ct);
    }
}

/// <summary>ข้อความไทยของแต่ละชนิดแจ้งเตือน — ตัวเดียว (pure)</summary>
public static class PlatformBillingMessages
{
    public static (string Subject, string Body) For(string kind, int days, PlatformInvoiceRow inv)
    {
        var amount = $"{inv.BalanceDue:N2} บาท";
        var due = inv.DueDate is DateTime d ? PlatformInvoiceRules.ThaiDate(d).ToString("dd/MM/yyyy") : "-";
        return kind switch
        {
            PlatformDunningPolicy.KindIssued => ($"ใบแจ้งหนี้ค่าบริการ NextAcc {inv.DocumentNumber}",
                $"ใบแจ้งหนี้เลขที่ {inv.DocumentNumber} ยอด {amount} ครบกำหนดชำระ {due}\nชำระได้ที่ศูนย์บิลค่าบริการ (โอนแล้วแนบสลิป)"),
            PlatformDunningPolicy.KindBefore => ($"แจ้งเตือน: ใบแจ้งหนี้ {inv.DocumentNumber} ครบกำหนดในอีก {days} วัน",
                $"ใบแจ้งหนี้เลขที่ {inv.DocumentNumber} ยอดค้าง {amount} ครบกำหนด {due}"),
            PlatformDunningPolicy.KindDue => ($"ใบแจ้งหนี้ {inv.DocumentNumber} ครบกำหนดชำระวันนี้",
                $"ใบแจ้งหนี้เลขที่ {inv.DocumentNumber} ยอดค้าง {amount} ครบกำหนดวันนี้ ({due})"),
            _ => ($"ค้างชำระ: ใบแจ้งหนี้ {inv.DocumentNumber} เกินกำหนด {days} วัน",
                $"ใบแจ้งหนี้เลขที่ {inv.DocumentNumber} ยอดค้าง {amount} เกินกำหนดตั้งแต่ {due}\nกรุณาชำระเพื่อหลีกเลี่ยงการระงับการใช้งาน"),
        };
    }
}
