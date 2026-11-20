using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 280 — แจ้ง "คำขอเชื่อมคู่ค้า" ทางช่องที่ผู้ขอเลือก (ตัวตัดสิน <see cref="PartnerLinkRequestChannels"/> · คำตัดสิน 280-1…280-9) · ลำดับต่อการกด (ล็อกด้วย required_call_site_check):
/// <list type="number">
/// <item>คำขอต้องเป็นของบริษัทนี้ (tenant) และยังรอตอบ (<see cref="PartnerLinkRequestChannels.RequestProblem"/>) · ผู้ติดต่อต้องเป็นของบริษัทนี้ + เลขภาษีตรง (<see cref="PartnerLinkRequestChannels.ContactProblem"/>)</item>
/// <item>ใต้ <c>pg_advisory_xact_lock</c> ต่อคำขอ: กันส่งถี่ (<see cref="PartnerLinkRequestChannels.ThrottleProblem"/>) → ความพร้อมของช่องจากตัวหาผู้รับเดียว
/// (<see cref="IContactRecipientResolver.ResolveAsync"/> เรื่อง <see cref="NotifyTopic.PartnerLink"/>) → จองแถว "กำลังส่ง" แล้ว commit (สองคลิกพร้อมกันไม่ส่งสองครั้ง)</item>
/// <item>ส่ง: LINE กลุ่ม ผ่าน <see cref="ILineNotifyService.TryPushContactTextAsync"/> (OA ที่กลุ่มผูก · ด่านกลุ่มกลางของแอดมิน/บริษัทผู้ให้บริการเดิม) → จำแนกด้วย <see cref="LinePushOutcome.Classify"/> ·
/// อีเมล ผ่านผู้ส่งเดียว <see cref="DocumentEmailService.ResolveOnBehalfSenderAsync"/> (+ เพดาน/โควตาอีเมลของ NextAcc เมื่อส่งแทน)</item>
/// <item>ผลจริงลงแถว (ส่งสำเร็จ = ผู้ให้บริการตอบรับเท่านั้น) + audit chain</item>
/// </list>
/// ช่องในระบบของคู่ค้า (<see cref="PartnerLinkRequestChannels.InApp"/>) = outbox เดิมของรอบ 264 — บันทึกเป็น "เข้าคิว" เสมอ ไม่มีผลที่บอกสมาชิกภาพ
/// </summary>
public class PartnerLinkRequestNotifier
{
    private readonly AccountingDbContext _db;
    private readonly IContactRecipientResolver _recipients;
    private readonly ILineNotifyService _line;
    private readonly ISecretProtector _secrets;
    private readonly IEmailSenderFactory _senders;
    private readonly IConfiguration? _config;
    private readonly INotifyQuotaService? _notifyQuota;
    private readonly ILogger<PartnerLinkRequestNotifier> _logger;

    public PartnerLinkRequestNotifier(AccountingDbContext db, IContactRecipientResolver recipients, ILineNotifyService line, ISecretProtector secrets,
        IEmailSenderFactory senders, ILogger<PartnerLinkRequestNotifier> logger, IConfiguration? config = null, INotifyQuotaService? notifyQuota = null)
    {
        _db = db; _recipients = recipients; _line = line; _secrets = secrets; _senders = senders; _logger = logger; _config = config; _notifyQuota = notifyQuota;
    }

    /// <summary>ช่องในระบบ: <see cref="Record"/> = ตอนส่งคำขอ (outbox ถูกตั้งแล้ว) · <see cref="Requeue"/> = "ส่งแจ้งอีกครั้ง" (ตั้ง outbox ใหม่ · กันถี่)</summary>
    public enum InAppAction { Record, Requeue }

    /// <summary>ผลต่อช่อง (หน้าแสดงตรงตัว) — ไม่มี LINE id · อีเมลปิดบัง</summary>
    public sealed record NoticeResult(string Channel, string ChannelLabel, string Status, string StatusLabel, string? Recipient, string? Reason);

    public const string AuditEntityType = "PartnerLinkRequestNotify";   // ไม่ชนกับแถว audit อัตโนมัติของ entity (ชื่อคลาส)
    public const string EmailPurpose = "partner-link-request";

    // ═══════════ ตัวเลือกช่อง (หน้า "ส่งคำขอเชื่อม" / "ส่งแจ้งอีกครั้ง") ═══════════

    /// <summary>ผู้ติดต่อของเราที่ตรงเลขภาษีนี้ + ความพร้อมของกลุ่ม LINE/อีเมล ของผู้ติดต่อที่เลือก — อ่านเฉพาะข้อมูลของบริษัทนี้ (ไม่แตะบริษัทอื่น ⇒ ไม่รั่วสมาชิกภาพ)</summary>
    public async Task<object> ChannelOptionsAsync(Guid companyId, string? rawTaxId, Guid? contactId, CancellationToken ct = default)
    {
        var taxId = PartnerCompanyMatch.NormalizeTaxId(rawTaxId);
        var contacts = taxId == null ? new List<ContactPick>() : await MatchingContactsAsync(companyId, taxId, ct);
        Guid? selected = contactId ?? (contacts.Count == 1 ? contacts[0].Id : null);
        string? contactProblem = null;
        if (selected is Guid sid)
        {
            var c = await ContactAsync(companyId, sid, ct);
            contactProblem = taxId == null ? null : PartnerLinkRequestChannels.ContactProblem(c != null, c?.TaxId, taxId);
            if (c != null && contacts.All(x => x.Id != sid)) contacts.Insert(0, new ContactPick(c.Id, c.Name, c.TaxId, c.BranchCode));
        }
        else contactProblem = PartnerLinkRequestChannels.NoContactReason;
        var origin = await SystemBaseUrl.ResolveAsync(_db, _config, ct);
        var channels = new List<PartnerLinkRequestChannels.ChannelOption>();
        foreach (var ch in PartnerLinkRequestChannels.Selectable)
            channels.Add(contactProblem != null || selected == null
                ? new PartnerLinkRequestChannels.ChannelOption(ch, PartnerLinkRequestChannels.ChannelLabel(ch), false, contactProblem,
                    selected is Guid g && contactProblem != PartnerLinkRequestChannels.ContactNotFoundReason ? PartnerLinkRequestChannels.ContactNotifyUrl(g)
                        : taxId == null ? null : PartnerLinkRequestChannels.NewContactUrl(taxId), Array.Empty<string>())
                : await AvailabilityAsync(companyId, selected.Value, ch, origin, ct));
        return new
        {
            Contacts = contacts.Select(c => new { c.Id, c.Name, TaxId = c.TaxId, BranchLabel = TaxBranchCode.Label(c.BranchCode) }).ToList(),
            SelectedContactId = selected,
            ContactProblem = contactProblem,
            NewContactUrl = taxId == null ? null : PartnerLinkRequestChannels.NewContactUrl(taxId),
            Channels = channels,
            InApp = new { Label = PartnerLinkRequestChannels.ChannelLabel(PartnerLinkRequestChannels.InApp), Note = PartnerLinkRequestChannels.InAppNote },
        };
    }

    private sealed record ContactPick(Guid Id, string Name, string? TaxId, string? BranchCode);

    private async Task<List<ContactPick>> MatchingContactsAsync(Guid companyId, string taxId, CancellationToken ct)
        => await _db.Contacts.AsNoTracking()
            .Where(c => c.CompanyId == companyId && !c.IsDeleted && c.TaxId != null
                && c.TaxId.Replace("-", "").Replace(" ", "") == taxId)
            .OrderBy(c => c.Name).Take(20)
            .Select(c => new ContactPick(c.Id, c.Name, c.TaxId, c.BranchCode)).ToListAsync(ct);

    private async Task<ContactPick?> ContactAsync(Guid companyId, Guid contactId, CancellationToken ct)
        => await _db.Contacts.AsNoTracking()
            .Where(c => c.Id == contactId && c.CompanyId == companyId && !c.IsDeleted)
            .Select(c => new ContactPick(c.Id, c.Name, c.TaxId, c.BranchCode)).FirstOrDefaultAsync(ct);

    /// <summary>ความพร้อมของช่องหนึ่ง — ตัวหาผู้รับเดียว (เรื่อง PartnerLink) + URL ของระบบ (ข้อความต้องมีลิงก์)</summary>
    private async Task<PartnerLinkRequestChannels.ChannelOption> AvailabilityAsync(Guid companyId, Guid contactId, string channel, string? origin, CancellationToken ct)
    {
        var set = await _recipients.ResolveAsync(companyId, contactId, NotifyTopic.PartnerLink, ct);
        var opt = PartnerLinkRequestChannels.Summarize(channel, contactId, set.Options.Select(o => new PartnerLinkRequestChannels.EndpointView(o.Kind, o.Display, o.Available, o.Reason)));
        if (opt.Available && origin == null)
            return opt with { Available = false, Reason = PartnerLinkRequestChannels.NoBaseUrlReason, FixUrl = null, Targets = Array.Empty<string>() };
        return opt;
    }

    // ═══════════ ส่ง ═══════════

    private sealed record Plan(PartnerLinkRequestNotice Row, List<Guid?> EndpointIds);

    /// <summary>แจ้งคำขอ <paramref name="requestId"/> ทางช่องที่เลือก · คืนผลต่อช่อง (ช่องในระบบอยู่แรกเสมอ) · คำขอไม่ใช่ของบริษัทนี้ ⇒ 404</summary>
    public async Task<IReadOnlyList<NoticeResult>> SendAsync(Guid companyId, Guid requestId, Guid? contactId, IEnumerable<string?>? channels,
        Guid userId, InAppAction inApp, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var selected = PartnerLinkRequestChannels.NormalizeSelection(channels);
        var plans = new List<Plan>();
        Guid? effectiveContact = null;
        string partnerTax, ourName;
        string? partnerBranch;
        DateTime expiresAt;

        var ownTx = _db.Database.CurrentTransaction == null ? await _db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
                new object[] { AdvisoryLockKey.For(companyId, AdvisoryLockKey.PartnerLinkNotify, requestId.ToString("N")) }, ct);
            var req = await _db.PartnerLinkRequests.FirstOrDefaultAsync(r => r.Id == requestId && r.CompanyId == companyId, ct)
                ?? throw new BusinessRuleException("ไม่พบคำขอนี้ในบริษัทที่เลือกอยู่", PartnerLinkRequestChannels.RuleCode, 404);
            partnerTax = req.PartnerTaxId; partnerBranch = req.PartnerBranchCode; expiresAt = req.ExpiresAt;
            ourName = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";
            var history = await _db.PartnerLinkRequestNotices.AsNoTracking()
                .Where(n => n.CompanyId == companyId && n.LinkRequestId == requestId)
                .Select(n => new { n.Channel, n.Status, n.AttemptedAt }).ToListAsync(ct);
            IEnumerable<DateTime> Attempts(string ch) => history.Where(h => h.Channel == ch && PartnerLinkRequestChannels.CountsAsAttempt(h.Status)).Select(h => h.AttemptedAt);
            var requestProblem = PartnerLinkRequestChannels.RequestProblem(req.AcceptedAt, req.ExpiresAt, now);

            PartnerLinkRequestNotice NewRow(string ch, string status, string? reason, string? label = null) => new()
            {
                CompanyId = companyId, LinkRequestId = requestId, Channel = ch, Status = status, Reason = reason,
                RuleCode = status == PartnerLinkRequestChannels.StatusRefused ? PartnerLinkRequestChannels.RuleCode : null,
                ContactId = effectiveContact, RecipientLabel = label, ByUserId = userId, AttemptedAt = now,
                CompletedAt = status is PartnerLinkRequestChannels.StatusSending ? null : now, CreatedBy = userId.ToString(),
            };

            // (ก) ช่องในระบบของคู่ค้า — ไม่มีผลที่บอกสมาชิกภาพ
            if (inApp == InAppAction.Record)
                plans.Add(new Plan(NewRow(PartnerLinkRequestChannels.InApp, PartnerLinkRequestChannels.StatusQueued, null), new()));
            else
            {
                var p = requestProblem ?? PartnerLinkRequestChannels.ThrottleProblem(Attempts(PartnerLinkRequestChannels.InApp), now);
                if (p == null)
                {
                    // outbox เดิมของรอบ 264 (เพดานต่อปลายทางต่อวันยังคุม) — แถวที่ยังรออยู่ไม่ถูกแตะ · ไม่มีแถว (เลขไม่ใช้ NextAcc) = 0 แถว (คำตอบเท่ากัน)
                    await _db.TradingPartnerships
                        .Where(t => t.LinkRequestId == requestId && t.InvitedByCompanyId == companyId && t.Status == TradingPartnershipStatus.Pending
                            && t.InviteNotifiedAt != null)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.InviteNoticeDueAt, (DateTime?)now).SetProperty(t => t.InviteNotifiedAt, (DateTime?)null)
                            .SetProperty(t => t.InviteNoticeResult, (string?)null), ct);
                    plans.Add(new Plan(NewRow(PartnerLinkRequestChannels.InApp, PartnerLinkRequestChannels.StatusQueued, null), new()));
                }
                else plans.Add(new Plan(NewRow(PartnerLinkRequestChannels.InApp, PartnerLinkRequestChannels.StatusRefused, p), new()));
            }

            if (selected.Count > 0)
            {
                // ผู้ติดต่อ: ที่ส่งมา > ที่จำไว้บนคำขอ
                effectiveContact = contactId ?? req.ContactId;
                string? contactProblem;
                if (effectiveContact is Guid cid)
                {
                    var c = await ContactAsync(companyId, cid, ct);
                    contactProblem = PartnerLinkRequestChannels.ContactProblem(c != null, c?.TaxId, req.PartnerTaxId);
                    if (contactProblem == null && req.ContactId != cid) { req.ContactId = cid; req.UpdatedAt = now; req.UpdatedBy = userId.ToString(); }
                }
                else contactProblem = PartnerLinkRequestChannels.NoContactReason;
                var origin = await SystemBaseUrl.ResolveAsync(_db, _config, ct);
                foreach (var ch in selected)
                {
                    var p = requestProblem ?? contactProblem ?? PartnerLinkRequestChannels.ThrottleProblem(Attempts(ch), now);
                    if (p != null) { plans.Add(new Plan(NewRow(ch, PartnerLinkRequestChannels.StatusRefused, p), new())); continue; }
                    var opt = await AvailabilityAsync(companyId, effectiveContact!.Value, ch, origin, ct);
                    if (!opt.Available) { plans.Add(new Plan(NewRow(ch, PartnerLinkRequestChannels.StatusRefused, opt.Reason), new())); continue; }
                    var set = await _recipients.ResolveAsync(companyId, effectiveContact.Value, NotifyTopic.PartnerLink, ct);
                    var kind = ch == PartnerLinkRequestChannels.LineGroup ? NotifyEndpointKind.LineGroup : NotifyEndpointKind.Email;
                    var targets = set.Options.Where(o => o.Available && o.Kind == kind).ToList();
                    var label = string.Join(", ", targets.Select(t => PartnerLinkRequestChannels.RecipientLabel(ch, t.Display)).Distinct());
                    plans.Add(new Plan(NewRow(ch, PartnerLinkRequestChannels.StatusSending, null, Trunc(label, 200)), targets.Select(t => t.EndpointId).ToList()));
                }
            }
            foreach (var p in plans) _db.PartnerLinkRequestNotices.Add(p.Row);
            await _db.SaveChangesAsync(ct);
            if (ownTx != null) await ownTx.CommitAsync(ct);
        }
        finally
        {
            if (ownTx != null) await ownTx.DisposeAsync();
        }

        // ส่งจริงนอกธุรกรรม (แถว "กำลังส่ง" commit แล้ว ⇒ คลิกซ้ำระหว่างนี้ถูกกันส่งถี่)
        var message = PartnerLinkRequestChannels.Compose(ourName, partnerTax, partnerBranch, expiresAt, await SystemBaseUrl.ResolveAsync(_db, _config, ct) ?? "");
        foreach (var p in plans.Where(p => p.Row.Status == PartnerLinkRequestChannels.StatusSending))
        {
            var (ok, reason) = p.Row.Channel == PartnerLinkRequestChannels.LineGroup
                ? await PushGroupsAsync(companyId, effectiveContact!.Value, p.EndpointIds, message.Text, ct)
                : await SendEmailAsync(companyId, effectiveContact!.Value, message, userId, ct);
            p.Row.Status = ok switch { true => PartnerLinkRequestChannels.StatusSent, false => PartnerLinkRequestChannels.StatusFailed, null => PartnerLinkRequestChannels.StatusRefused };
            p.Row.Reason = reason == null ? null : Trunc(reason, 1000);
            p.Row.RuleCode = ok == true ? null : PartnerLinkRequestChannels.RuleCode;
            p.Row.CompletedAt = DateTime.UtcNow;
            if (ok == false) _logger.LogError("แจ้งคำขอเชื่อมคู่ค้า {Request} ทาง {Channel} ไม่สำเร็จ: {Reason}", requestId, p.Row.Channel, reason);
        }

        // audit chain — ช่อง + ผล (ไม่มีผู้รับดิบ) · ช่องในระบบเป็น "เข้าคิว" ทุกกรณี
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Action = AuditAction.Create, EntityType = AuditEntityType, EntityId = requestId.ToString(),
            NewValues = JsonSerializer.Serialize(new
            {
                @event = inApp == InAppAction.Record ? "PartnerLinkRequestNotified" : "PartnerLinkRequestRenotified",
                contactId = effectiveContact,
                notices = plans.Select(p => new { channel = p.Row.Channel, status = p.Row.Status, reason = p.Row.Reason }),
                ruleCode = PartnerLinkRequestChannels.RuleCode,
            }),
            Timestamp = now,
        });
        await _db.SaveChangesAsync(ct);
        return plans.Select(p => new NoticeResult(p.Row.Channel, PartnerLinkRequestChannels.ChannelLabel(p.Row.Channel), p.Row.Status,
            PartnerLinkRequestChannels.StatusLabel(p.Row.Status), p.Row.RecipientLabel, p.Row.Reason)).ToList();
    }

    /// <summary>push ข้อความเข้าทุกกลุ่มที่ใช้ได้ (OA ที่กลุ่มผูก) · สำเร็จ = LINE ตอบรับ (Classify) · คืน (true/false, เหตุรวม)</summary>
    private async Task<(bool? Ok, string? Reason)> PushGroupsAsync(Guid companyId, Guid contactId, List<Guid?> endpointIds, string text, CancellationToken ct)
    {
        var ids = endpointIds.Where(i => i != null).Select(i => i!.Value).ToList();
        var eps = await _db.ContactNotifyEndpoints
            .Where(e => ids.Contains(e.Id) && e.CompanyId == companyId && e.ContactId == contactId && e.Kind == NotifyEndpointKind.LineGroup && e.UnlinkedAt == null)
            .ToListAsync(ct);
        if (eps.Count == 0) return (null, PartnerLinkRequestChannels.NoLineGroupReason);
        var sent = 0;
        var failures = new List<string>();
        foreach (var ep in eps)
        {
            var label = !string.IsNullOrWhiteSpace(ep.Label) ? ep.Label!.Trim() : ep.GroupNameSnapshot ?? "กลุ่ม LINE";
            var to = _secrets.Unprotect(ep.LineGroupId);
            if (string.IsNullOrWhiteSpace(to)) { failures.Add($"{label}: ถอดรหัสปลายทางไม่ได้ (ให้ผูกกลุ่มใหม่)"); continue; }
            LinePushClass cls;
            try
            {
                var r = await _line.TryPushContactTextAsync(companyId, ep.OaScope, LineTargetKind.ContactGroup, to, text, Guid.NewGuid());
                cls = LinePushOutcome.Classify(r.Ok, r.HttpStatus, r.Error, r.RuleCode, r.TransportError);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "push คำขอเชื่อมคู่ค้าเข้ากลุ่ม LINE ของผู้ติดต่อ {Contact} ล้ม", contactId);
                cls = new LinePushClass(LinePushVerdict.Failed, LinePushOutcome.CodeUnknown, "ส่งไม่สำเร็จ: " + ex.Message, false, false, false);
            }
            if (cls.Verdict == LinePushVerdict.Sent) { sent++; ep.LastSentAt = DateTime.UtcNow; ep.LastFailure = null; }
            else { failures.Add($"{label}: {cls.Reason}"); ep.LastFailure = Trunc(cls.Reason, 500); }
        }
        await _db.SaveChangesAsync(ct);
        if (sent > 0 && failures.Count == 0) return (true, null);
        if (sent > 0) return (true, "ส่งไม่ครบทุกกลุ่ม — " + string.Join(" · ", failures));
        return (false, string.Join(" · ", failures));
    }

    /// <summary>อีเมลถึงผู้ติดต่อ (ตัวหาผู้รับเดียว · ผู้ส่งเดียว) · ส่งผ่าน NextAcc ⇒ เพดานรายวัน + โควตารายเดือนเดิม · บันทึกใน DocumentEmailLogs (นับเพดาน)</summary>
    private async Task<(bool? Ok, string? Reason)> SendEmailAsync(Guid companyId, Guid contactId, PartnerLinkRequestChannels.Message m, Guid userId, CancellationToken ct)
    {
        var delivery = ContactRecipientRule.ForEmail(await _recipients.ResolveAsync(companyId, contactId, NotifyTopic.PartnerLink, ct));
        if (!delivery.Deliverable) return (null, delivery.OptOutReason ?? PartnerLinkRequestChannels.NoEmailReason);
        var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
        var (plan, sender) = await DocumentEmailService.ResolveOnBehalfSenderAsync(_db, _senders, companyId, settings, ct);
        NotifyReservation? monthly = null;
        if (plan.OnBehalf)
        {
            var quota = await PlatformEmailQuota.ForCompanyAsync(_db, _config, companyId, DateTime.UtcNow, ct);
            if (quota.SentToday >= quota.Cap)
                return (null, $"ส่งอีเมลผ่าน NextAcc ครบเพดานของวันนี้แล้ว ({quota.Cap} ฉบับ) — ตั้งอีเมลผู้ส่งของบริษัท (ตั้งค่า → อีเมล) หรือส่งพรุ่งนี้");
            if (_notifyQuota != null)
            {
                monthly = await _notifyQuota.ReservePlatformEmailAsync(companyId, ct);
                if (!monthly.Ok) return (null, NotifyQuotaPolicy.EmailMonthlyFullReason(monthly.Limit));
            }
        }
        var msg = new EmailMessage
        {
            FromAddress = plan.FromAddress, FromName = plan.FromName, ReplyTo = plan.ReplyTo,
            Subject = m.Subject, HtmlBody = m.Html, PlainTextBody = m.Text,
        };
        msg.To.AddRange(delivery.Emails);
        EmailSendResult r;
        try { r = await sender.SendAsync(msg, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ส่งอีเมลคำขอเชื่อมคู่ค้าล้ม company={CompanyId}", companyId);
            r = EmailSendResult.Fail("ส่งไม่สำเร็จ: " + ex.Message);
        }
        if (monthly != null)
        {
            if (r.Success) await _notifyQuota!.AlertIfCrossedAsync(companyId, monthly, ct);
            else await _notifyQuota!.ReleaseAsync(companyId, monthly, ct);
        }
        var now = DateTime.UtcNow;
        _db.DocumentEmailLogs.Add(new DocumentEmailLog
        {
            CompanyId = companyId, DocumentId = null, ToEmail = string.Join(", ", delivery.Emails), Subject = m.Subject, Body = m.Html,
            Provider = sender.Provider, Purpose = EmailPurpose, SenderMode = plan.Mode, CreatedBy = userId.ToString(), CreatedAt = now,
            Status = r.Success ? EmailLogStatus.Sent : EmailLogStatus.Failed, SentAt = r.Success ? now : null,
            ErrorMessage = r.Success ? null : r.ErrorMessage, ProviderMessageId = r.MessageId,
        });
        await _db.SaveChangesAsync(ct);
        return r.Success ? (true, null) : (false, r.ErrorMessage ?? "ผู้ให้บริการอีเมลไม่รับข้อความ (ไม่ทราบเหตุ)");
    }

    private static string? Trunc(string? s, int n) => s == null ? null : s.Length <= n ? s : s[..n];
}
