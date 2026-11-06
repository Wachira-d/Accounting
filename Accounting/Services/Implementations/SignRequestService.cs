using Accounting.Controllers;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// **ลิงก์ให้ลูกค้าเซ็น/ยอมรับเอกสาร: ออกลิงก์ · ส่งอีเมล · ป้ายสถานะ · ค่าตั้ง · งานอัตโนมัติ** (รอบ 240 · DOCUMENT_FLOW §2.12f · คำตัดสิน 240-1..240-8)
///
/// <para>ทุกทางเข้า (หน้าเอกสาร · partner API v1 · งานอัตโนมัติ) เดินเมธอดเดียวกัน — ชนิดเอกสาร/สถานะที่ส่งได้ <see cref="SignRequestKinds"/> ·
/// URL ของ callback <see cref="SignCallbackUrlRule"/> · ผู้ส่งอีเมล <see cref="DocumentEmailService.ResolveOnBehalfSenderAsync"/> →
/// <see cref="OnBehalfEmailSender"/> · เพดาน <see cref="SignEmailThrottle"/> · ข้อความ <see cref="SignRequestEmailText"/> ·
/// บันทึกทุกการส่ง (สำเร็จ/ล้ม/ถูกกัน) ลง <c>DocumentEmailLogs</c> ที่บริษัทเห็นบนหน้าเอกสาร</para>
/// </summary>
public class SignRequestService : ISignRequestService
{
    private readonly AccountingDbContext _db;
    private readonly IEmailSenderFactory _senders;
    private readonly ISecretProtector _secrets;
    private readonly IConfiguration? _config;
    private readonly ILogger<SignRequestService> _logger;
    /// <summary>รอบ 250 WP-08 (L1 ต่อสาย · DESIGN §3.g): ผู้รับตั้งต้นของลิงก์เซ็น = ตัวหาผู้รับเดียว เรื่อง "ลิงก์เซ็น" (ช่องที่ผู้ใช้กรอกเอง/ครั้งก่อนยังชนะ)</summary>
    private readonly IContactRecipientResolver _recipients;
    /// <summary>รอบ 254 WP-11 L4 (Q4): โควตาอีเมลรายเดือนของเส้น SMTP ของ NextAcc · null (สร้างนอก DI) ⇒ ไม่นับรายเดือน</summary>
    private readonly INotifyQuotaService? _notifyQuota;

    public SignRequestService(AccountingDbContext db, IEmailSenderFactory senders, ISecretProtector secrets,
        ILogger<SignRequestService> logger, IConfiguration? config = null, IContactRecipientResolver? recipients = null,
        INotifyQuotaService? notifyQuota = null)
    {
        _db = db;
        _notifyQuota = notifyQuota;
        _senders = senders;
        _secrets = secrets;
        _logger = logger;
        _config = config;
        _recipients = recipients ?? ContactRecipientResolver.EmailOnly(db);
    }


    private async Task<Document> LoadSignableAsync(Guid companyId, Guid documentId, CancellationToken ct)
    {
        var doc = await _db.Documents.FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted, ct)
            ?? throw new KeyNotFoundException("ไม่พบเอกสารนี้ในบริษัทของคุณ");
        var refusal = SignRequestKinds.IssueRefusal(doc.DocumentType, doc.Status);
        if (refusal != null) throw new BusinessRuleException(refusal, "SIGN-LINK-STATUS");
        return doc;
    }

    private Task<CompanySettings?> SettingsAsync(Guid companyId, CancellationToken ct)
        => _db.CompanySettings.FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);

    private static SignLinkResult ToResult(Document doc, string origin, DateTime now)
    {
        var kind = SignRequestKinds.For(doc.DocumentType)!;
        var s = SignRequestKinds.Read(doc);
        return new SignLinkResult(doc.Id, kind.Code, kind.EventType, SignRequestKinds.Url(origin, kind, s.Token!), s.Token!, s.ExpiresAt,
            s.SignedAt, s.SignedBy, doc.SignCallbackUrl, doc.SignCallbackRef, SignRequestKinds.Status(doc, now));
    }

    /// <summary>ตรวจ + ตั้ง webhook ต่อเอกสาร (ผู้เรียก SaveChanges) — ต้องมีรหัสลับลงนามของบริษัทก่อน · โฮสต์ตามรายชื่อที่บริษัทอนุญาต</summary>
    internal static void ApplyCallback(Document doc, CompanySettings? settings, string? callbackUrl, string? callbackRef, bool clear)
    {
        if (clear)
        {
            doc.SignCallbackUrl = null;
            doc.SignCallbackRef = null;
            return;
        }
        if (string.IsNullOrWhiteSpace(callbackUrl))
        {
            if (callbackRef != null) doc.SignCallbackRef = SignCallbackUrlRule.CleanRef(callbackRef);
            return;
        }
        var problem = SignCallbackUrlRule.Problem(callbackUrl, settings?.SignCallbackAllowedHosts);
        if (problem != null) throw new BusinessRuleException(problem, "SIGN-CALLBACK-URL");
        if (string.IsNullOrWhiteSpace(settings?.SignCallbackSecret))
            throw new BusinessRuleException(
                "บริษัทยังไม่มีรหัสลับลงนาม webhook — เจ้าของบริษัทออกรหัสที่ ตั้งค่า → ลิงก์ให้ลูกค้าเซ็น → \"ออกรหัสลับใหม่\" แล้วนำไปตั้งที่ระบบปลายทางก่อนส่ง callbackUrl",
                "SIGN-CALLBACK-SECRET");
        doc.SignCallbackUrl = callbackUrl.Trim();
        doc.SignCallbackRef = SignCallbackUrlRule.CleanRef(callbackRef);
    }

    public async Task<SignLinkResult> IssueLinkAsync(Guid companyId, Guid documentId, IssueSignLinkOptions options, string origin, CancellationToken ct = default)
    {
        var doc = await LoadSignableAsync(companyId, documentId, ct);
        var now = DateTime.UtcNow;
        if (options.CallbackUrl != null || options.CallbackRef != null || options.ClearCallback)
            ApplyCallback(doc, await SettingsAsync(companyId, ct), options.CallbackUrl, options.CallbackRef, options.ClearCallback);
        if (options.Regenerate || !SignRequestKinds.LinkUsable(SignRequestKinds.Read(doc), now))
            SignRequestKinds.SetNewToken(doc, PublicQuotationController.NewToken(), now);
        doc.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return ToResult(doc, origin, now);
    }

    /// <summary>ผู้รับตั้งต้น: ผู้รับที่ผู้ใช้กรอกครั้งก่อน (ทับ) › ปลายทางของผู้ติดต่อที่รับเรื่อง "ลิงก์เซ็น" (ตัวหาผู้รับเดียว · ไม่ตั้ง = Contact.Email เดิม) ·
    /// คืนเหตุเมื่อผู้ติดต่อมีอีเมลแต่ยกเลิกรับเรื่องนี้ทุกปลายทาง (ผู้เรียกบอกผู้ใช้ — ไม่ใช่ "ไม่มีอีเมล")</summary>
    private async Task<(IReadOnlyList<string> To, string? OptOut)> SuggestRecipientsAsync(Document doc, CancellationToken ct)
    {
        var (fromLast, _) = SignEmailThrottle.ParseRecipients(new[] { doc.Contact?.SignLinkLastRecipients });
        if (fromLast.Count > 0) return (fromLast, null);
        var delivery = ContactRecipientRule.ForEmail(await _recipients.ResolveAsync(doc.CompanyId, doc.ContactId, NotifyTopic.Sign, ct));
        return (delivery.Emails, delivery.OptOutReason);
    }

    private async Task<SignRequestEmailText.Output> ComposeAsync(Document doc, string url, string? message, bool reminder,
        OnBehalfEmailSender.Plan plan, string origin, CancellationToken ct)
    {
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, doc.CompanyId, doc.Id);
        var co = await _db.Companies.AsNoTracking().Where(c => c.Id == doc.CompanyId)
            .Select(c => new { c.Name, c.TaxId }).FirstOrDefaultAsync(ct);
        var kind = SignRequestKinds.For(doc.DocumentType)!;
        return SignRequestEmailText.Compose(new SignRequestEmailText.Input(
            kind, heading.IsEnglish, heading.Title, doc.DocumentNumber, doc.DocumentDate, doc.TotalAmount, doc.Currency,
            doc.Contact?.Name, co?.Name, co?.TaxId, plan.ReplyTo, url, SignRequestKinds.Read(doc).ExpiresAt, message, reminder,
            plan.OnBehalf, $"{origin.TrimEnd('/')}/contact.html?topic=email-abuse"));
    }

    public async Task<SignEmailPreview> PreviewEmailAsync(Guid companyId, Guid documentId, string origin, string? message, CancellationToken ct = default)
    {
        var doc = await LoadSignableAsync(companyId, documentId, ct);
        await _db.HydrateContactAsync(companyId, doc);
        var now = DateTime.UtcNow;
        var state = SignRequestKinds.Read(doc);
        // พรีวิวไม่ออกลิงก์ใหม่ (ไม่เปลี่ยนข้อมูล) — ยังไม่มี/หมดอายุ ⇒ แสดงตัวอย่าง URL; ตอนส่งจริงระบบออกให้
        var kind = SignRequestKinds.For(doc.DocumentType)!;
        var url = SignRequestKinds.LinkUsable(state, now) ? SignRequestKinds.Url(origin, kind, state.Token!) : SignRequestKinds.Url(origin, kind, "…");
        var (plan, _) = await DocumentEmailService.ResolveOnBehalfSenderAsync(_db, _senders, companyId, await SettingsAsync(companyId, ct), ct);
        var text = await ComposeAsync(doc, url, message, reminder: false, plan, origin, ct);
        var quota = await PlatformEmailQuota.ForCompanyAsync(_db, _config, companyId, now, ct);
        return new SignEmailPreview(doc.Id, url, text.Subject, text.Html, plan.Mode, plan.FromAddress, plan.FromName, plan.ReplyTo,
            (await SuggestRecipientsAsync(doc, ct)).To, quota.SentToday, quota.Cap, SignRequestKinds.Status(doc, now));
    }

    public async Task<SignEmailResult> SendEmailAsync(Guid companyId, Guid documentId, SendSignEmailOptions options, string origin, string actor, CancellationToken ct = default)
    {
        var doc = await LoadSignableAsync(companyId, documentId, ct);
        await _db.HydrateContactAsync(companyId, doc);
        var now = DateTime.UtcNow;
        var kind = SignRequestKinds.For(doc.DocumentType)!;
        if (SignRequestKinds.Read(doc).SignedAt != null)
            throw new BusinessRuleException($"ลูกค้า{kind.ActionTh}แล้ว — ไม่ต้องส่งลิงก์ซ้ำ", "SIGN-ALREADY-SIGNED");
        var purpose = SignRequestPurposes.IsKnown(options.Purpose) ? options.Purpose : SignRequestPurposes.Manual;
        var reminder = purpose == SignRequestPurposes.Reminder;

        // รอบ 250 WP-08: ผู้รับที่ผู้ใช้กรอกตอนส่งชนะเสมอ · ว่าง ⇒ ตัวหาผู้รับเดียว (เรื่อง "ลิงก์เซ็น")
        IReadOnlyList<string> toList;
        string? optOut = null;
        if (options.To is { Count: > 0 } typed) toList = typed;
        else (toList, optOut) = await SuggestRecipientsAsync(doc, ct);
        var (valid, invalid) = SignEmailThrottle.ParseRecipients(toList);
        if (invalid.Count > 0) throw new BusinessRuleException($"อีเมลผู้รับไม่ถูกต้อง: {string.Join(", ", invalid.Take(3))}", "SIGN-EMAIL-TO");
        if (valid.Count == 0) throw new BusinessRuleException(optOut != null
            ? optOut + " — ใส่อีเมลผู้รับเองแล้วส่งใหม่"
            : "ไม่มีอีเมลผู้รับ — ใส่อีเมลลูกค้า (หรือเพิ่มอีเมลในผู้ติดต่อ) แล้วส่งใหม่", "SIGN-EMAIL-TO");
        if (valid.Count > SignEmailThrottle.MaxRecipients)
            throw new BusinessRuleException($"ส่งได้ครั้งละไม่เกิน {SignEmailThrottle.MaxRecipients} ผู้รับ", "SIGN-EMAIL-TO");

        // ลิงก์: ใช้ฉบับที่ยังใช้ได้ (อีเมลเตือนต้องเป็นลิงก์เดิม) · ไม่มี/หมดอายุ ⇒ ออกใหม่
        if (!SignRequestKinds.LinkUsable(SignRequestKinds.Read(doc), now))
        {
            if (reminder) throw new BusinessRuleException("ลิงก์เดิมหมดอายุแล้ว — ไม่ส่งอีเมลเตือน", "SIGN-REMINDER-EXPIRED");
            SignRequestKinds.SetNewToken(doc, PublicQuotationController.NewToken(), now);
            await _db.SaveChangesAsync(ct);
        }
        var url = SignRequestKinds.Url(origin, kind, SignRequestKinds.Read(doc).Token!);

        var settings = await SettingsAsync(companyId, ct);
        var (plan, sender) = await DocumentEmailService.ResolveOnBehalfSenderAsync(_db, _senders, companyId, settings, ct);
        var text = await ComposeAsync(doc, url, options.Message, reminder, plan, origin, ct);
        var quota = await PlatformEmailQuota.ForCompanyAsync(_db, _config, companyId, now, ct);
        var platformToday = quota.SentToday;
        var cap = quota.Cap;

        var results = new List<SignEmailRecipientResult>();
        var throttledAll = true;
        foreach (var to in valid)
        {
            var (sameDoc, any) = await PlatformEmailQuota.RecipientCountsAsync(_db, companyId, doc.Id, to, quota.DayStartUtc, ct);
            var refusal = SignEmailThrottle.Refusal(plan.OnBehalf, platformToday, cap, sameDoc, any);
            // รอบ 254 WP-11 L4 (Q4): ผ่านเพดานรายวันแล้ว ⇒ เส้นแพลตฟอร์มตัดโควตาอีเมลรายเดือน (1 ผู้รับ = 1 ฉบับ) · เต็ม ⇒ ถูกกันพร้อมเหตุ (ไม่เรียกผู้ให้บริการ)
            NotifyReservation? monthly = null;
            if (refusal == null && plan.OnBehalf && _notifyQuota != null)
            {
                monthly = await _notifyQuota.ReservePlatformEmailAsync(companyId, ct);
                if (!monthly.Ok) { refusal = NotifyQuotaPolicy.EmailMonthlyFullReason(monthly.Limit); monthly = null; }
            }
            var log = new DocumentEmailLog
            {
                CompanyId = companyId, DocumentId = doc.Id, ToEmail = to, Subject = text.Subject, Body = text.Html,
                Provider = sender.Provider, Purpose = purpose, SenderMode = plan.Mode, CreatedBy = actor, CreatedAt = now,
            };
            if (refusal != null)
            {
                // ถูกกัน = ล้มดังบนบันทึกการส่งของเอกสาร (ไม่ใช่เงียบ) — ไม่เรียกผู้ให้บริการอีเมลเลย
                log.Status = EmailLogStatus.Failed;
                log.ErrorMessage = refusal;
                _db.DocumentEmailLogs.Add(log);
                results.Add(new SignEmailRecipientResult(to, false, refusal));
                continue;
            }
            throttledAll = false;
            var msg = new EmailMessage
            {
                FromAddress = plan.FromAddress, FromName = plan.FromName, ReplyTo = plan.ReplyTo,
                Subject = text.Subject, HtmlBody = text.Html, PlainTextBody = text.Text,
            };
            msg.To.Add(to);
            EmailSendResult r;
            try { r = await sender.SendAsync(msg, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "ส่งอีเมลขอเซ็นล้ม doc={DocId} company={CompanyId}", doc.Id, companyId);
                r = EmailSendResult.Fail("ส่งไม่สำเร็จ: " + ex.Message);
            }
            if (monthly != null)
            {
                // ส่งล้ม (รวมโยน — ถูกแปลงเป็นผลล้มด้านบน) ⇒ คืนหน่วย · สำเร็จ ⇒ ประทับคำเตือน 80%/100% ครั้งเดียวต่องวด
                if (r.Success) await _notifyQuota!.AlertIfCrossedAsync(companyId, monthly, ct);
                else await _notifyQuota!.ReleaseAsync(companyId, monthly, ct);
            }
            log.Status = r.Success ? EmailLogStatus.Sent : EmailLogStatus.Failed;
            log.SentAt = r.Success ? DateTime.UtcNow : null;
            log.ErrorMessage = r.Success ? null : (r.ErrorMessage ?? "ส่งไม่สำเร็จ");
            log.ProviderMessageId = r.MessageId;
            _db.DocumentEmailLogs.Add(log);
            if (r.Success && plan.OnBehalf) platformToday++;
            results.Add(new SignEmailRecipientResult(to, r.Success, log.ErrorMessage));
        }

        var sent = results.Where(r => r.Sent).Select(r => r.To).ToList();
        if (sent.Count > 0)
        {
            if (reminder) doc.SignReminderSentAt = now;
            else
            {
                doc.SignLinkEmailedAt ??= now;
                var merged = SignEmailThrottle.ParseRecipients(new[] { doc.SignLinkEmailedTo, string.Join(",", sent) }).Valid;
                doc.SignLinkEmailedTo = Truncate(string.Join(",", merged), 500);
                // ผู้ติดต่อถูก hydrate แบบไม่ติดตาม — เขียนตรงแบบมีเงื่อนไขบริษัท (ไม่ชนกับอินสแตนซ์ที่ผูกกับเอกสาร)
                var last = Truncate(string.Join(",", sent), 500);
                await _db.Contacts
                    .Where(c => c.Id == doc.ContactId && c.CompanyId == companyId)
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.SignLinkLastRecipients, last), ct);
            }
            doc.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
        return new SignEmailResult(doc.Id, url, plan.Mode, plan.FromAddress, plan.FromName, plan.ReplyTo, results)
        {
            Throttled = throttledAll && results.Count > 0,
        };
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    public async Task<Dictionary<Guid, SignRequestKinds.Chip>> GetStatusesAsync(Guid companyId, IReadOnlyCollection<Guid> documentIds, CancellationToken ct = default)
    {
        var ids = documentIds.Distinct().Take(500).ToList();
        var types = SignRequestKinds.All.Select(k => k.DocumentType).ToList();
        // ฉายเฉพาะช่องที่ตัวตัดสินป้ายใช้ (ไม่ดึงรูปลายเซ็นใบส่งของ ≤ 200KB/แถวมาทั้งหน้า)
        var rows = await _db.Documents.AsNoTracking()
            .Where(d => d.CompanyId == companyId && ids.Contains(d.Id) && !d.IsDeleted && types.Contains(d.DocumentType))
            .Select(d => new Document
            {
                Id = d.Id, CompanyId = d.CompanyId, DocumentType = d.DocumentType,
                QuotationAcceptToken = d.QuotationAcceptToken, QuotationAcceptTokenExpiresAt = d.QuotationAcceptTokenExpiresAt,
                QuotationAcceptedAt = d.QuotationAcceptedAt, QuotationAcceptedBy = d.QuotationAcceptedBy,
                DeliverySignToken = d.DeliverySignToken, DeliverySignTokenExpiresAt = d.DeliverySignTokenExpiresAt,
                DeliverySignedAt = d.DeliverySignedAt, DeliverySignedBy = d.DeliverySignedBy,
                SignLinkEmailedAt = d.SignLinkEmailedAt, SignLinkOpenedAt = d.SignLinkOpenedAt, SignReminderSentAt = d.SignReminderSentAt,
            })
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        var map = new Dictionary<Guid, SignRequestKinds.Chip>();
        foreach (var d in rows)
            if (SignRequestKinds.Status(d, now) is { } chip) map[d.Id] = chip;
        return map;
    }

    private SignRequestSettingsDto ToSettings(CompanySettings? s, int platformCap)
    {
        string? hint = null;
        if (!string.IsNullOrWhiteSpace(s?.SignCallbackSecret))
        {
            var clear = _secrets.Unprotect(s.SignCallbackSecret);
            hint = string.IsNullOrEmpty(clear) ? null : "…" + clear[^4..];
        }
        var verified = s != null && s.EmailConfigured && Accounting.Services.Implementations.Email.EmailSenderFactory.HasTenantSender(s);
        return new SignRequestSettingsDto(
            s?.SignLinkAutoEmailOnApprove ?? false, s?.SignLinkAutoEmailSince, s?.SignLinkReminderDays ?? 0, s?.SignCallbackAllowedHosts,
            !string.IsNullOrWhiteSpace(s?.SignCallbackSecret), hint, verified,
            verified ? OnBehalfEmailSender.ModeCompany : OnBehalfEmailSender.ModePlatform, platformCap,
            SignRequestKinds.All.Select(k => k.EventType).ToList());
    }

    private async Task<CompanySettings> SettingsForWriteAsync(Guid companyId, CancellationToken ct)
    {
        var s = await SettingsAsync(companyId, ct);
        if (s != null) return s;
        s = await CompanySettingsFactory.AddNewAsync(_db, companyId);
        return s;
    }

    public async Task<SignRequestSettingsDto> GetSettingsAsync(Guid companyId, CancellationToken ct = default)
        => ToSettings(await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct),
            (await PlatformEmailQuota.ForCompanyAsync(_db, _config, companyId, DateTime.UtcNow, ct)).Cap);

    public const int MaxReminderDays = 30;

    public async Task<SignRequestSettingsDto> UpdateSettingsAsync(Guid companyId, UpdateSignRequestSettings request, CancellationToken ct = default)
    {
        if (request.ReminderDays is < 0 or > MaxReminderDays)
            throw new BusinessRuleException($"จำนวนวันก่อนส่งอีเมลเตือนต้องอยู่ระหว่าง 0 (ปิด) ถึง {MaxReminderDays}", "SIGN-SETTINGS");
        if (request.CallbackAllowedHosts != null && SignCallbackUrlRule.HostsProblem(request.CallbackAllowedHosts) is { } hp)
            throw new BusinessRuleException(hp, "SIGN-SETTINGS");
        var s = await SettingsForWriteAsync(companyId, ct);
        if (request.AutoEmailOnApprove is { } auto)
        {
            // เปิดใหม่ ⇒ ประทับ watermark (ใบที่สร้างก่อนเปิดไม่ถูกส่งย้อนหลัง) · ปิด ⇒ ล้าง
            if (auto && !s.SignLinkAutoEmailOnApprove) s.SignLinkAutoEmailSince = DateTime.UtcNow;
            if (!auto) s.SignLinkAutoEmailSince = null;
            s.SignLinkAutoEmailOnApprove = auto;
        }
        if (request.ReminderDays is { } days) s.SignLinkReminderDays = days;
        if (request.CallbackAllowedHosts != null) s.SignCallbackAllowedHosts = SignCallbackUrlRule.NormalizeHosts(request.CallbackAllowedHosts);
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return ToSettings(s, (await PlatformEmailQuota.ForCompanyAsync(_db, _config, companyId, DateTime.UtcNow, ct)).Cap);
    }

    public async Task<string> RotateCallbackSecretAsync(Guid companyId, CancellationToken ct = default)
    {
        var s = await SettingsForWriteAsync(companyId, ct);
        var secret = WebhookSignature.NewSecret();
        s.SignCallbackSecret = _secrets.Protect(secret);
        s.UpdatedAt = DateTime.UtcNow;
        _db.AddChainedAuditLog(new AuditLog
        {
            CompanyId = companyId, Action = AuditAction.Update, EntityType = nameof(CompanySettings), EntityId = s.Id.ToString(),
            NewValues = System.Text.Json.JsonSerializer.Serialize(new { @event = "SignCallbackSecretRotated" }),
            Timestamp = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return secret;
    }

    internal static SignEventDto ToDto(SignEventOutboxEntry e) => new(
        e.Id, e.EventId, e.EventType, e.DocumentId, e.Status.ToString(), e.Attempts, e.CreatedAt, e.LastAttemptAt,
        e.Status == SignEventStatus.Pending ? e.NextAttemptAt : null, e.LastHttpStatus, e.LastError, e.DeliveredAt,
        Uri.TryCreate(e.CallbackUrl, UriKind.Absolute, out var u) ? u.Host : null, e.CallbackRef,
        e.RegistrationsDispatchedAt != null, e.SellerNotifiedAt != null);

    public async Task<List<SignEventDto>> GetEventsAsync(Guid companyId, Guid? documentId, int take = 50, CancellationToken ct = default)
    {
        var q = _db.SignEventOutbox.AsNoTracking().Where(e => e.CompanyId == companyId);
        if (documentId is { } d) q = q.Where(e => e.DocumentId == d);
        var rows = await q.OrderByDescending(e => e.CreatedAt).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task RetryEventAsync(Guid companyId, Guid outboxId, CancellationToken ct = default)
    {
        var row = await _db.SignEventOutbox.FirstOrDefaultAsync(e => e.Id == outboxId && e.CompanyId == companyId, ct)
            ?? throw new KeyNotFoundException("ไม่พบเหตุการณ์นี้ในบริษัทของคุณ");
        if (row.CallbackUrl == null || row.Status == SignEventStatus.Delivered)
            throw new BusinessRuleException("เหตุการณ์นี้ไม่มี callback ที่ต้องส่งซ้ำ (ส่งสำเร็จแล้ว หรือไม่มี callback ต่อเอกสาร)", "SIGN-EVENT-RETRY");
        // ฝ่ายค้าน N3: payload ถูกล้างตามอายุเก็บแล้ว ⇒ ไม่มีของให้ส่ง (ส่ง "{}" = ปลายทางได้เหตุการณ์เปล่าที่ลงนามถูกต้อง)
        if (string.IsNullOrWhiteSpace(row.PayloadJson) || row.PayloadJson == "{}")
            throw new BusinessRuleException($"ข้อมูลของเหตุการณ์นี้ถูกล้างตามอายุเก็บ ({SignEventOutbox.PayloadRetentionDays} วัน) แล้ว — ส่งซ้ำไม่ได้ · ดึงสถานะล่าสุดผ่าน GET /api/v1/documents/{{id}}/sign-status แทน",
                "SIGN-EVENT-PURGED");
        // ฝ่ายค้าน S2: URL ต้องยังผ่านกติกา + รายชื่อโฮสต์ปัจจุบันของบริษัท (บริษัทอาจถอดโฮสต์ออกแล้ว)
        var hosts = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => s.SignCallbackAllowedHosts).FirstOrDefaultAsync(ct);
        if (SignCallbackUrlRule.Problem(row.CallbackUrl, hosts) is { } problem)
            throw new BusinessRuleException(problem, "SIGN-CALLBACK-URL");
        row.Status = SignEventStatus.Pending;
        row.Attempts = 0;
        row.NextAttemptAt = DateTime.UtcNow;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>หนึ่งรอบของงานอัตโนมัติ (ผู้เรียก <c>SignRequestAutomationJob</c> ใต้ JobLock): (1) ส่งอีเมลขอยอมรับใบเสนอราคาที่อนุมัติแล้ว
    /// (สร้างหลัง watermark · ยังไม่เคยส่ง · ยังไม่เคยลองอัตโนมัติ) (2) อีเมลเตือนครั้งเดียวต่อลิงก์ · ไม่รู้ URL ของระบบ ⇒ ไม่ส่งอะไร (ล้มดังในผลรอบ)</summary>
    public async Task<SignAutomationRunResult> RunAutomationAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var origin = await SystemBaseUrl.ResolveAsync(_db, _config, ct);
        if (origin == null)
        {
            const string why = "ยังไม่ได้ตั้ง URL ของระบบ (ผู้ดูแลระบบ → ตั้งค่าเว็บไซต์) — ไม่ส่งอีเมลลิงก์เซ็นอัตโนมัติ/อีเมลเตือนรอบนี้";
            _logger.LogError("งานส่งลิงก์ให้ลูกค้าเซ็นอัตโนมัติ: {Why}", why);
            return new SignAutomationRunResult(0, 0, 0, 0, why);
        }
        int autoSent = 0, autoFailed = 0, reminded = 0, remindFailed = 0;

        var autoCompanies = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.SignLinkAutoEmailOnApprove && s.SignLinkAutoEmailSince != null)
            .Select(s => new { s.CompanyId, Since = s.SignLinkAutoEmailSince!.Value })
            .ToListAsync(ct);
        foreach (var c in autoCompanies)
        {
            var due = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == c.CompanyId && !d.IsDeleted && d.DocumentType == DocumentType.Quotation
                    && d.CreatedAt >= c.Since && d.QuotationAcceptedAt == null && d.SignLinkEmailedAt == null
                    && d.Status != DocumentStatus.Draft && d.Status != DocumentStatus.WaitingApproval
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Rejected
                    && !_db.DocumentEmailLogs.Any(l => l.CompanyId == c.CompanyId && l.DocumentId == d.Id && l.Purpose == SignRequestPurposes.Auto))
                .OrderBy(d => d.CreatedAt).Select(d => d.Id).Take(20).ToListAsync(ct);
            foreach (var id in due)
            {
                var ok = await TryAutomationSendAsync(c.CompanyId, id, null, SignRequestPurposes.Auto, origin, ct);
                if (ok) autoSent++; else autoFailed++;
            }
        }

        var remindCompanies = await _db.CompanySettings.AsNoTracking()
            .Where(s => s.SignLinkReminderDays > 0)
            .Select(s => new { s.CompanyId, s.SignLinkReminderDays })
            .ToListAsync(ct);
        var types = SignRequestKinds.All.Select(k => k.DocumentType).ToList();
        foreach (var c in remindCompanies)
        {
            var before = nowUtc.AddDays(-c.SignLinkReminderDays);
            var due = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == c.CompanyId && !d.IsDeleted && types.Contains(d.DocumentType)
                    && d.SignLinkEmailedAt != null && d.SignLinkEmailedAt <= before && d.SignReminderSentAt == null
                    && d.SignLinkEmailedTo != null
                    && d.QuotationAcceptedAt == null && d.DeliverySignedAt == null
                    && !_db.DocumentEmailLogs.Any(l => l.CompanyId == c.CompanyId && l.DocumentId == d.Id && l.Purpose == SignRequestPurposes.Reminder
                        && l.CreatedAt >= d.SignLinkEmailedAt))
                .OrderBy(d => d.SignLinkEmailedAt).Select(d => new { d.Id, d.SignLinkEmailedTo }).Take(20).ToListAsync(ct);
            foreach (var d in due)
            {
                var ok = await TryAutomationSendAsync(c.CompanyId, d.Id, d.SignLinkEmailedTo, SignRequestPurposes.Reminder, origin, ct);
                if (ok) reminded++; else remindFailed++;
            }
        }
        return new SignAutomationRunResult(autoSent, autoFailed, reminded, remindFailed);
    }

    /// <summary>ส่งหนึ่งใบจากงานอัตโนมัติ — ล้ม ⇒ บันทึกการส่งของเอกสารเป็น Failed (ไม่ลองซ้ำอัตโนมัติ · ผู้ใช้เห็นบนหน้าเอกสารแล้วส่งเองได้) · ไม่โยน</summary>
    /// <summary>ฝ่ายค้าน MED-1: ผู้รับของเส้นอัตโนมัติ = ผู้รับตั้งต้น (ครั้งก่อน/อีเมลเตือน) <b>ตัดผู้ที่ช่องทางของผู้ติดต่อปฏิเสธเรื่องลิงก์เซ็น</b>
    /// (<see cref="ContactRecipientRule.EmailDelivery.DeclinedEmails"/>) · ไม่มีผู้รับตั้งต้น ⇒ ตามช่องทางของผู้ติดต่อทั้งหมด · ผู้ติดต่อถูกลบ ⇒ ไม่มีใคร ·
    /// ผู้รับที่ผู้ใช้พิมพ์เองตอนส่งครั้งก่อนและไม่ใช่อีเมลใดของผู้ติดต่อ ⇒ <b>คงไว้</b> (ไม่มีค่าตั้งของผู้ติดต่อที่บอกว่าเขาไม่รับ — เดิมตัดทิ้งแล้วบันทึก
    /// "ไม่ได้เลือกรับเรื่องลิงก์เซ็น" ซึ่งไม่จริง และอีเมลเตือนของรอบ 240 ไม่ถึงผู้รับลิงก์เดิม · CI 42232967230 SignRequestRound240DbTests)</summary>
    internal static IReadOnlyList<string> SignAutomationRecipients(IReadOnlyList<string> candidates, ContactRecipientRule.EmailDelivery signDelivery)
    {
        if (candidates.Count == 0) return signDelivery.Emails;
        if (signDelivery.ContactDeleted) return Array.Empty<string>();
        return candidates.Where(c =>
        {
            var k = c.Trim().ToLowerInvariant();
            return signDelivery.Emails.Contains(k) || !signDelivery.DeclinedEmails.Contains(k);
        }).ToList();
    }

    private async Task<bool> TryAutomationSendAsync(Guid companyId, Guid documentId, string? to, string purpose, string origin, CancellationToken ct)
    {
        try
        {
            // รอบ 250 WP-08 (คำตัดสิน 250-29): ผู้ติดต่อ "ไม่ส่งอัตโนมัติ" ⇒ งานอัตโนมัติไม่ส่ง (โยนเหตุ ⇒ บันทึกการส่ง Failed พร้อมเหตุข้างล่าง · ส่งเองได้)
            var who = await _db.Documents.AsNoTracking()
                .Where(d => d.Id == documentId && d.CompanyId == companyId)
                .Join(_db.Contacts.IgnoreQueryFilters().Where(c => c.CompanyId == companyId), d => d.ContactId, c => c.Id,
                    (d, c) => new { c.Id, c.AutoSendDisabled, c.SignLinkLastRecipients })
                .FirstOrDefaultAsync(ct);
            if (AutopilotSettingsResolver.AutoSendBlock(who?.AutoSendDisabled == true) is { } autoSendOff)
                throw new BusinessRuleException(autoSendOff, AutopilotSettingsResolver.AutoSendRuleCode);
            // ฝ่ายค้าน MED-1: เส้นอัตโนมัติ/อีเมลเตือน — ส่งเฉพาะผู้รับที่ "ยังรับเรื่องลิงก์เซ็น" ตามช่องทางของผู้ติดต่อ (ตัดกับ ForEmail(Sign)) ·
            // ผู้รับที่พิมพ์เองตอนส่งด้วยมือยังชนะ (เส้นมือไม่ผ่านตรงนี้) · ไม่เหลือใคร ⇒ ไม่ส่ง + บันทึกเหตุ
            var signDelivery = ContactRecipientRule.ForEmail(await _recipients.ResolveAsync(companyId, who?.Id ?? Guid.Empty, NotifyTopic.Sign, ct));
            var candidates = SignEmailThrottle.ParseRecipients(new[] { to ?? who?.SignLinkLastRecipients }).Valid;
            var allowed = SignAutomationRecipients(candidates, signDelivery);
            if (allowed.Count == 0)
                throw new BusinessRuleException(signDelivery.OptOutReason
                    ?? "ผู้รับลิงก์เซ็นเดิมไม่ได้เลือกรับเรื่อง \"ลิงก์เซ็น\" ในช่องทางแจ้งเตือนของผู้ติดต่อแล้ว — ไม่ส่งอัตโนมัติ (ส่งเองจากหน้าเอกสารได้)", "SIGN-AUTO-OPTOUT");
            var r = await SendEmailAsync(companyId, documentId,
                new SendSignEmailOptions(allowed, null, purpose), origin, "system:sign-automation", ct);
            return r.AnySent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning(ex, "ส่งอีเมลลิงก์เซ็นอัตโนมัติไม่สำเร็จ doc={DocId} company={CompanyId}", documentId, companyId);
            _db.DocumentEmailLogs.Add(new DocumentEmailLog
            {
                CompanyId = companyId, DocumentId = documentId, ToEmail = to ?? "", Subject = "(ไม่ได้ส่ง)", Body = "",
                Purpose = purpose, Status = EmailLogStatus.Failed, CreatedBy = "system:sign-automation",
                ErrorMessage = ex is BusinessRuleException or KeyNotFoundException ? ex.Message : "เกิดข้อผิดพลาด (ดูบันทึกระบบ)",
            });
            await _db.SaveChangesAsync(ct);
            return false;
        }
    }
}
