using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

public class EmailScheduleService : IEmailScheduleService
{
    private readonly AccountingDbContext _db;
    private readonly IEmailSenderFactory _senderFactory;
    private readonly IPdfGenerationService _pdf;
    private readonly ILogger<EmailScheduleService> _logger;
    /// <summary>รอบ 241: ลิงก์ชำระเงินในอีเมลตามตาราง (null = สร้างนอก DI — ไม่แนบลิงก์)</summary>
    private readonly IDocumentPayLinkService? _payLinks;
    /// <summary>รอบ 241 ฝ่ายค้าน B2: เพดานอีเมลของแพลตฟอร์ม + URL ของระบบ (null = ค่าตั้งต้น)</summary>
    private readonly IConfiguration? _config;
    /// <summary>รอบ 244: ทาง e-Tax by Email ของใบกำกับในคิว (<c>SendDocumentRoutedAsync</c>) · null (สร้างนอก DI) ⇒ ใบที่ต้องไปทาง e-Tax ล้มดังบนคิว (ไม่ส่งเป็นอีเมลธรรมดา)</summary>
    private readonly IDocumentEmailService? _docEmail;
    /// <summary>รอบ 250 WP-08 (L1 ต่อสาย · DESIGN §3.e/§3.g): ผู้รับของทุกเส้นในไฟล์นี้มาจากตัวหาผู้รับเดียว (ปลายทางของผู้ติดต่อ + เรื่องที่รับ) —
    /// ห้ามอ่าน <c>Contact.Email</c> เป็นผู้รับเอง (required_call_site_check R250L1) · null (สร้างนอก DI) ⇒ ตัวเดียวกันแบบไม่ถอด LINE</summary>
    private readonly IContactRecipientResolver _recipients;
    /// <summary>รอบ 254 WP-11 L4 (Q4): โควตาอีเมลรายเดือนของเส้น SMTP ของ NextAcc · null (สร้างนอก DI) ⇒ ไม่นับรายเดือน</summary>
    private readonly INotifyQuotaService? _notifyQuota;
    /// <summary>รอบ 260-4 (A-L6): กระดิ่ง "อนุมัติแล้วเข้าคิวส่งอัตโนมัติไม่สำเร็จ" — scope แยก (context ของคำขออนุมัติอาจค้างแถวที่ล้ม) · null = ไม่แจ้ง (เทสต์เดิม)</summary>
    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? _scopes;

    public EmailScheduleService(AccountingDbContext db, IEmailSenderFactory senderFactory,
        IPdfGenerationService pdf, ILogger<EmailScheduleService> logger, IDocumentPayLinkService? payLinks = null,
        IConfiguration? config = null, IDocumentEmailService? docEmail = null, IContactRecipientResolver? recipients = null,
        INotifyQuotaService? notifyQuota = null, Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? scopes = null)
    {
        _db = db;
        _scopes = scopes;
        _notifyQuota = notifyQuota;
        _senderFactory = senderFactory;
        _pdf = pdf;
        _logger = logger;
        _payLinks = payLinks;
        _config = config;
        _docEmail = docEmail;
        _recipients = recipients ?? ContactRecipientResolver.EmailOnly(db);
    }

    /// <summary>รอบ 250 WP-08: อีเมลของผู้ติดต่อสำหรับเรื่องนี้ (ตัวตัดสินเดียว <see cref="ContactRecipientRule.ForEmail"/>)</summary>
    private async Task<ContactRecipientRule.EmailDelivery> EmailForAsync(Guid companyId, Guid contactId, NotifyTopic topic, CancellationToken ct)
    {
        var set = await _recipients.ResolveAsync(companyId, contactId, topic, ct);
        return ContactRecipientRule.ForEmail(set);
    }

    /// <summary>รอบ 250 WP-08 (คำตัดสิน 250-29): ค่าตั้ง "ไม่ส่งอัตโนมัติ" ของผู้ติดต่อ (ผู้ติดต่อที่ถูกลบยังอ่านได้ — ใบเก่าอ้างอยู่)</summary>
    private Task<bool> ContactAutoSendOffAsync(Guid companyId, Guid contactId, CancellationToken ct)
        => _db.Contacts.AsNoTracking().IgnoreQueryFilters()
            .Where(c => c.Id == contactId && c.CompanyId == companyId)
            .Select(c => c.AutoSendDisabled).FirstOrDefaultAsync(ct);

    /// <summary>รอบ 250 WP-08: ผู้ติดต่อมีอีเมลแต่ไม่รับเรื่องนี้ ⇒ แถวคิว "ข้าม" พร้อมเหตุ (ล้มดัง — หน้าคิวเห็นว่าทำไมไม่ส่ง) ·
    /// กุญแจกันซ้ำเดิม ⇒ สแกนรอบถัดไปไม่เขียนซ้ำ · ไม่มีผู้รับ/ไม่มีไฟล์แนบ (ผู้ส่งคิวไม่หยิบแถวสถานะนี้)</summary>
    private void AddNotSentRow(Guid companyId, Guid? ruleId, string entityType, Guid entityId, string subject, DateTime sendUtc, string idem, string reason)
        => _db.EmailQueues.Add(new EmailQueue
        {
            CompanyId = companyId,
            RuleId = ruleId == Guid.Empty ? null : ruleId,
            EntityType = entityType,
            EntityId = entityId,
            ToEmail = "",
            Subject = subject,
            Body = "",
            AttachPdf = false,
            ScheduledFor = sendUtc,
            IdempotencyKey = idem,
            Status = ScheduledEmailSendCheck.SkippedStatus,
            ErrorMessage = reason.Length > 2000 ? reason[..2000] : reason,
        });

    /// <summary>รอบ 241: ลิงก์ชำระเงินของใบนี้ตามตาราง (ค่าตาราง ?? ค่าบริษัท · ช่องทางตาราง ∩ ที่บริษัทเปิด — ตัวตัดสิน <c>DocumentPayLinkPolicy</c>) ·
    /// null = ไม่แนบ · แนบแล้ว: <c>{PayLink}</c> ในเทมเพลต = URL · เทมเพลตไม่มี placeholder = ต่อบล็อกท้ายเนื้อ</summary>
    internal async Task<PayLinkResult?> PayLinkFor(EmailScheduleRule rule, Document doc, CancellationToken ct)
        => _payLinks == null ? null : await _payLinks.ForScheduledEmailAsync(rule, doc, ct);

    /// <summary>รอบ 250 (WP-02 §2.4 ข้อ 2): ข้อความบนแถวคิวเมื่อตั้งให้แนบลิงก์ชำระแต่ออกไม่ได้ — null = แนบได้/ไม่ได้ตั้งแนบ</summary>
    internal static string? PayLinkSkipNote(PayLinkResult? link)
        => link is { Ok: false } && !string.IsNullOrWhiteSpace(link.Refusal)
            ? PayLinkSkipPrefix + link.Refusal
            : null;

    internal const string PayLinkSkipPrefix = "ส่งได้แต่ไม่แนบลิงก์ชำระ: ";

    /// <summary>ส่งสำเร็จ ⇒ ล้างข้อความผิดพลาดเดิม แต่คงหมายเหตุ "ไม่แนบลิงก์เพราะ…" ไว้ให้เห็นหลังส่ง (รอบ 250)</summary>
    internal static string? AfterSentNote(string? current)
        => current != null && KeptNotePrefixes.Any(p => current.StartsWith(p, StringComparison.Ordinal)) ? current : null;

    /// <summary>รอบ 250 WP-08: หมายเหตุที่คงไว้หลังส่งสำเร็จ (ส่งได้แต่ไม่ครบ/ไม่แนบลิงก์) — ข้อความผิดพลาดอื่นถูกล้าง</summary>
    internal static readonly string[] KeptNotePrefixes =
        { PayLinkSkipPrefix, ContactRecipientRule.EtaxPrimaryOnlyPrefix, ContactRecipientRule.CappedPrefix, ContactLineChannel.EtaxForcedEmailNote };

    /// <summary>รวมหมายเหตุของแถวคิว (null ตัดทิ้ง · ≤ 2000 ตัวอักษร) · ตัวแรกกำหนดว่าคงไว้หลังส่งไหม</summary>
    internal static string? JoinNotes(params string?[] notes)
    {
        var parts = notes.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (parts.Count == 0) return null;
        var joined = string.Join(" · ", parts);
        return joined.Length > 2000 ? joined[..2000] : joined;
    }

    /// <summary>รอบ 250 WP-08 ฝ่ายค้าน MED-3: ผู้ส่งของบริษัทนี้เป็นอีเมลผ่าน NextAcc ไหม (ตัวตัดสินเดียว ResolveOnBehalfSenderAsync · แคชต่อบริษัทต่อรอบ)</summary>
    private readonly Dictionary<Guid, bool> _platformSender = new();

    private async Task<(string To, string? Note)> CapRecipientsAsync(Guid companyId, string to, string? bcc, CancellationToken ct)
    {
        if (!_platformSender.TryGetValue(companyId, out var onBehalf))
        {
            var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == companyId, ct);
            onBehalf = (await DocumentEmailService.ResolveOnBehalfSenderAsync(_db, _senderFactory, companyId, settings, ct)).Plan.OnBehalf;
            _platformSender[companyId] = onBehalf;
        }
        var others = SignEmailThrottle.ParseRecipients(new[] { bcc }).Valid.Count;
        return ContactRecipientRule.CapForSender(to, onBehalf, others, SignEmailThrottle.MaxRecipients, MaxToEmailLength);
    }

    /// <summary>ความยาวช่อง <c>EmailQueues.ToEmail</c>/<c>DocumentEmailLogs.ToEmail</c> (varchar 500)</summary>
    internal const int MaxToEmailLength = 500;

    internal static string WithPayLink(string body, string? template, PayLinkResult? link, bool isEn)
    {
        if (link?.Url == null || link.Amounts == null || DocumentPayLinkPolicy.HasPlaceholder(template)) return body;
        // คิวเก็บที่หมาย {PayLink} ไม่ใช่ URL (ไม่มี token ตรงใน EmailQueues · ฝ่ายค้าน S1) — URL ประกอบตอนส่งใน ComposeQueueMessageAsync
        return body + DocumentPayLinkPolicy.EmailBlock(DocumentPayLinkPolicy.QueuePlaceholder, link.Amounts, isEn);
    }

    // ── เวลาท้องถิ่นไทย — เก็บใน DB เป็น UTC, แต่ ScheduledFor คำนวณจาก
    // SendAtHour ในมุมมอง Bangkok (UTC+7). ใช้ TimeZoneInfo ถ้ามี.
    private static DateTime BangkokNow() => DateTime.UtcNow.AddHours(7);
    private static DateTime ScheduledUtc(DateTime localDate, int hour)
        => new DateTime(localDate.Year, localDate.Month, localDate.Day, hour, 0, 0, DateTimeKind.Unspecified).AddHours(-7);

    // ═══════════════════════════════════════════════════════════════
    // Triggers
    // ═══════════════════════════════════════════════════════════════

    public async Task OnDocumentApprovedAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
    {
        try
        {
            var doc = await _db.Documents.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct);
            if (doc == null) return;
            await _db.HydrateContactAsync(companyId, doc);  // กัน INNER JOIN ตัดใบที่ contact ถูกลบ

            var rules = await _db.EmailScheduleRules.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.IsActive && !r.IsDeleted
                    && r.Trigger == "DocumentApproved"
                    && (r.DocumentType == null || r.DocumentType == doc.DocumentType.ToString()))
                .ToListAsync(ct);

            foreach (var rule in rules)
                await EnqueueDocumentAsync(rule, doc, daysOffset: rule.OffsetDays, suffix: "approved", ct);
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // รอบ 260-4 (team-A L6 · คำตัดสิน 260-4-6): เดิม LogWarning อย่างเดียว ⇒ ลูกค้าไม่ได้เอกสารและไม่มีใครรู้ · ตอนนี้ LogError + กระดิ่ง (ต่อใบ) + Autopilot
            // (การอนุมัติไม่ล้ม — ผู้เรียกตั้งใจ fail-safe) · ฝ่ายค้าน M3: ถอดแถวคิวที่เพิ่มในรอบนี้ออกก่อน (ไม่ให้ SaveChanges ถัดไปของคำขอเดียวกันบันทึกตามทีหลัง
            // ⇒ ลูกค้าได้สองฉบับเมื่อผู้ใช้ส่งเองตามกระดิ่ง · หรือพาการจัดสรรเงินรอจัดสรรล้มตาม) · timeout ภายใน (TaskCanceled ที่ผู้เรียกไม่ได้ยกเลิก) ก็ดังด้วย
            await EnqueueFailedAsync(LineFailureAlert.EnqueueSource.DocumentApproved, companyId, documentId, ex, ct);
        }
    }

    /// <summary>รอบ 260-4 (A-L6 · ฝ่ายค้าน M2/M3): เข้าคิวส่งอัตโนมัติล้ม (อนุมัติเอกสาร · จ่ายเงินเดือน · ออก 50 ทวิ) — ตัวเดียวของทั้งสามทาง:
    /// ถอดแถวคิวอีเมล/LINE ที่ยังไม่บันทึกออกจาก context ของคำขอ → LogError → กระดิ่งต่อรายการ</summary>
    private async Task EnqueueFailedAsync(LineFailureAlert.EnqueueSource source, Guid companyId, Guid entityId, Exception ex, CancellationToken ct)
    {
        foreach (var e in _db.ChangeTracker.Entries()
                     .Where(x => x.State == EntityState.Added && (x.Entity is EmailQueue || x.Entity is LineQueue)).ToList())
            e.State = EntityState.Detached;
        _logger.LogError(ex, "{Source} enqueue failed Entity={Entity}", source, entityId);
        await NotifyEnqueueFailedAsync(source, companyId, entityId, ex.GetBaseException().Message, CancellationToken.None);
    }

    /// <summary>รอบ 260-4 (A-L6 · M2): กระดิ่งครั้งเดียวต่อรายการ (<see cref="LineFailureAlert.ApproveEnqueueEntityType"/> + EntityId = รายการ) ถึงผู้รับตัวเดียว
    /// <see cref="LineAlertRecipients"/> (เจ้าของที่ใช้งาน · ไม่มี ⇒ ผู้มีสิทธิ์แก้ค่าตั้ง) · scope แยก · ล้ม ⇒ LogError</summary>
    private async Task NotifyEnqueueFailedAsync(LineFailureAlert.EnqueueSource source, Guid companyId, Guid entityId, string? reason, CancellationToken ct)
    {
        if (_scopes == null) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var sp = scope.ServiceProvider;
            var db = (AccountingDbContext)sp.GetService(typeof(AccountingDbContext))!;
            if (sp.GetService(typeof(INotificationService)) is not INotificationService notifications) return;
            if (await db.Notifications.AnyAsync(n => n.CompanyId == companyId && n.EntityType == LineFailureAlert.ApproveEnqueueEntityType && n.EntityId == entityId, ct))
                return;
            var number = source switch
            {
                LineFailureAlert.EnqueueSource.PayrollPaid => await db.PayrollRuns.AsNoTracking().Where(r => r.Id == entityId && r.CompanyId == companyId)
                    .Select(r => r.Name).FirstOrDefaultAsync(ct),
                LineFailureAlert.EnqueueSource.WhtCertIssued => await db.WithholdingTaxCerts.AsNoTracking().Where(c => c.Id == entityId && c.CompanyId == companyId)
                    .Select(c => c.CertificateNumber).FirstOrDefaultAsync(ct),
                _ => await db.Documents.AsNoTracking().Where(d => d.Id == entityId && d.CompanyId == companyId)
                    .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct),
            };
            var recipients = await LineAlertRecipients.ForCompanyAsync(db, sp.GetService(typeof(IPermissionService)) as IPermissionService, companyId, ct);
            if (recipients.Count == 0)
                _logger.LogError("เข้าคิวส่งอัตโนมัติล้ม ({Source}) บริษัท {Company} แต่ไม่มีผู้รับกระดิ่งที่ใช้งานอยู่", source, companyId);
            foreach (var u in recipients)
                await notifications.SendAsync(u, companyId, NotificationType.System, LineFailureAlert.EnqueueTitle(source),
                    LineFailureAlert.EnqueueMessage(source, number, reason), LineFailureAlert.EnqueueActionUrl(source, entityId),
                    LineFailureAlert.ApproveEnqueueEntityType, entityId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งเรื่องเข้าคิวส่งอัตโนมัติล้ม ({Source}) Entity={Entity} ไม่สำเร็จ", source, entityId);
        }
    }

    public async Task OnPayrollPaidAsync(Guid companyId, Guid payrollRunId, CancellationToken ct = default)
    {
        try
        {
            var rules = await _db.EmailScheduleRules.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.IsActive && !r.IsDeleted
                    && r.Trigger == "PayrollPaid")
                .ToListAsync(ct);
            if (rules.Count == 0) return;

            var details = await _db.PayrollDetails.AsNoTracking()
                .Include(d => d.Employee)
                .Include(d => d.PayrollRun)
                .Where(d => d.PayrollRunId == payrollRunId && d.CompanyId == companyId)
                .ToListAsync(ct);

            foreach (var rule in rules)
            {
                var sendUtc = ScheduledUtc(BangkokNow().Date.AddDays(rule.OffsetDays), rule.SendAtHour);
                foreach (var d in details)
                {
                    var email = ResolveEmployeeEmail(d.Employee);
                    if (string.IsNullOrWhiteSpace(email)) continue;
                    var idem = $"payslip:{payrollRunId}:{d.EmployeeId}";
                    if (await _db.EmailQueues.AnyAsync(q => q.CompanyId == companyId
                        && q.IdempotencyKey == idem && !q.IsDeleted, ct)) continue;

                    var ctx = new Dictionary<string, string?>
                    {
                        ["EmployeeName"] = $"{d.Employee.FirstNameTh} {d.Employee.LastNameTh}",
                        ["Period"] = $"{d.PayrollRun.Month:D2}/{d.PayrollRun.Year}",
                        ["NetPay"] = d.NetPay.ToString("N2"),
                    };
                    _db.EmailQueues.Add(new EmailQueue
                    {
                        CompanyId = companyId,
                        RuleId = rule.Id,
                        EntityType = "PayrollDetail",
                        EntityId = d.Id,
                        ToEmail = email!,
                        BccEmail = rule.BccEmails,
                        Subject = Render(rule.SubjectTemplate ?? "สลิปเงินเดือน {Period}", ctx),
                        Body = Render(rule.BodyTemplate ?? DefaultPayslipBody(), ctx, htmlEncodeValues: true),
                        AttachPdf = true,
                        ScheduledFor = sendUtc,
                        IdempotencyKey = idem,
                    });
                }
            }
            await _db.SaveChangesAsync(ct);
        }
        // ฝ่ายค้าน 260-4 M2: ทางเดียวกับ A-L6 (ถอดแถวที่ค้าง · LogError · กระดิ่ง · Autopilot)
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await EnqueueFailedAsync(LineFailureAlert.EnqueueSource.PayrollPaid, companyId, payrollRunId, ex, ct);
        }
    }

    public async Task OnWhtCertIssuedAsync(Guid companyId, Guid certId, CancellationToken ct = default)
    {
        try
        {
            var rules = await _db.EmailScheduleRules.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.IsActive && !r.IsDeleted
                    && r.Trigger == "WhtCertIssued")
                .ToListAsync(ct);
            if (rules.Count == 0) return;

            var cert = await _db.WithholdingTaxCerts.AsNoTracking()
                // ไม่ Include PayeeContact — hydrate แยก (กัน INNER JOIN ทำ cert=null → 50 ทวิ ไม่ส่งเมล)
                .FirstOrDefaultAsync(c => c.Id == certId && c.CompanyId == companyId, ct);
            await _db.HydratePayeeContactAsync(companyId, cert);
            if (cert?.PayeeContact == null) return;

            // รอบ 250 WP-08: ผู้รับเรื่อง "50 ทวิ" ตามช่องทางแจ้งเตือนของผู้ติดต่อ (ไม่ตั้ง = อีเมลหลักเหมือนเดิม)
            var delivery = await EmailForAsync(companyId, cert.PayeeContact.Id, NotifyTopics.ForTrigger("WhtCertIssued"), ct);
            var whtNotSent = AutopilotSettingsResolver.AutoSendBlock(cert.PayeeContact.AutoSendDisabled) ?? delivery.OptOutReason;
            if (!delivery.Deliverable && whtNotSent == null) return;

            foreach (var rule in rules)
            {
                var idem = $"wht-cert:{certId}";
                if (await _db.EmailQueues.AnyAsync(q => q.CompanyId == companyId
                    && q.IdempotencyKey == idem && !q.IsDeleted, ct)) continue;

                var sendUtc = ScheduledUtc(BangkokNow().Date.AddDays(rule.OffsetDays), rule.SendAtHour);
                var ctx = new Dictionary<string, string?>
                {
                    ["DocNumber"] = cert.CertificateNumber,
                    ["ContactName"] = cert.PayeeContact.Name,
                    ["Amount"] = cert.TotalTaxAmount.ToString("N2"),
                };
                if (whtNotSent != null)
                {
                    AddNotSentRow(companyId, rule.Id, "WithholdingTaxCert", certId,
                        Render(rule.SubjectTemplate ?? "หนังสือรับรองหัก ณ ที่จ่าย {DocNumber}", ctx), sendUtc, idem, whtNotSent);
                    continue;
                }
                var whtTo = await CapRecipientsAsync(companyId, delivery.To!, rule.BccEmails, ct);   // ฝ่ายค้าน MED-3
                _db.EmailQueues.Add(new EmailQueue
                {
                    CompanyId = companyId,
                    RuleId = rule.Id,
                    EntityType = "WithholdingTaxCert",
                    EntityId = certId,
                    ToEmail = whtTo.To,
                    BccEmail = rule.BccEmails,
                    Subject = Render(rule.SubjectTemplate ?? "หนังสือรับรองหัก ณ ที่จ่าย {DocNumber}", ctx),
                    Body = Render(rule.BodyTemplate ?? DefaultWhtBody(), ctx, htmlEncodeValues: true),
                    AttachPdf = true,
                    ScheduledFor = sendUtc,
                    IdempotencyKey = idem,
                    ErrorMessage = whtTo.Note,
                });
            }
            await _db.SaveChangesAsync(ct);
        }
        // ฝ่ายค้าน 260-4 M2: ทางเดียวกับ A-L6
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await EnqueueFailedAsync(LineFailureAlert.EnqueueSource.WhtCertIssued, companyId, certId, ex, ct);
        }
    }

    /// <summary>รอบ 258 (A-M1 ข): เอกสารนี้มีแถวคิว LINE ถึงผู้ติดต่อที่ไม่ใช่ "ข้าม" ไหม (ทุกกฎ) — รายการประจำใช้ตัดสินว่ายังต้องเตือน "ไม่มีอีเมล" ไหม</summary>
    public Task<bool> DocumentLineQueuedAsync(Guid companyId, Guid documentId, CancellationToken ct = default)
        => LineRowQueuedAsync(companyId, documentId, null, ct);

    /// <summary>รอบ 258 (A-M1): แถวคิว LINE ของเอกสาร (ไม่ใช่ "ข้าม") — <paramref name="baseKey"/> ระบุ = เฉพาะกุญแจนั้น (กฎหนึ่ง) · กรองบริษัทเสมอ</summary>
    private async Task<bool> LineRowQueuedAsync(Guid companyId, Guid documentId, string? baseKey, CancellationToken ct)
    {
        return await _db.LineQueues.AsNoTracking().AnyAsync(q => q.CompanyId == companyId && !q.IsDeleted
            && q.EntityType == "Document" && q.EntityId == documentId
            && (baseKey == null || q.BaseKey == baseKey)
            && q.Status != ContactLineChannel.StatusSkipped, ct);
    }

    public async Task<EmailEnqueueOutcome> OnRecurringDocumentCreatedAsync(Guid companyId, Guid documentId, Guid recurringId, CancellationToken ct = default)
    {
        try
        {
            var doc = await _db.Documents.AsNoTracking()
                .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId, ct);
            if (doc == null) return EmailEnqueueOutcome.Skipped("ไม่พบเอกสาร");
            await _db.HydrateContactAsync(companyId, doc);  // กัน INNER JOIN ตัดใบที่ contact ถูกลบ

            // ห้ามส่งอีเมลเอกสาร Draft/รออนุมัติ — เลขจริง §86/4 ออกตอน Approve
            // เท่านั้น (DocumentEmailService บล็อกอยู่แล้ว จะทำให้คิว fail). ถ้า
            // recurring ตั้ง AutoApprove=true เอกสารจะ Approved ตั้งแต่ตอนสร้าง →
            // ส่งได้; ถ้า false ให้รอ trigger "DocumentApproved" ตอน user อนุมัติ.
            if (doc.Status is DocumentStatus.Draft or DocumentStatus.WaitingApproval
                or DocumentStatus.Rejected)
            {
                _logger.LogInformation(
                    "Recurring doc {Doc} ยังไม่อนุมัติ ({Status}) — ข้ามการส่งอีเมลอัตโนมัติ (กัน DRAFT ถึงลูกค้า)",
                    documentId, doc.Status);
                return EmailEnqueueOutcome.Skipped("ใบยังไม่ได้อนุมัติ (ใบร่างยังไม่มีเลขที่จริง §86/4)");
            }

            // รอบ 250 WP-08 (คำตัดสิน 250-29 · AUTO_SEND §9 เฟส 243 ข้อ 7): ชั้นผู้ติดต่อ "ไม่ส่งอัตโนมัติ" ชนะกฎบริษัท/รายการประจำ (ตัวตัดสินเดียว
            // AutopilotSettingsResolver) — ตรวจก่อนด่าน "กฎเมื่ออนุมัติครอบแล้ว" (แถว "ข้าม" ของกฎนั้นไม่ใช่การส่ง ⇒ เหตุที่ผู้ใช้เห็นต้องเป็นค่าตั้งนี้)
            if (AutopilotSettingsResolver.AutoSendBlock(await ContactAutoSendOffAsync(companyId, doc.ContactId, ct)) is { } autoSendOff)
                return EmailEnqueueOutcome.NotSent(autoSendOff);

            // รอบ 250 (WP-02 §2.4 ข้อ 3 · คำตัดสิน §2 #10 · B F-4): การอนุมัติอัตโนมัติของรายการประจำเดิน ApproveDocumentAsync → OnDocumentApprovedAsync ⇒
            // กฎ "เมื่ออนุมัติเอกสาร" ที่ครอบชนิดนี้เข้าคิวไปแล้ว ⇒ ข้ามกฎ "เมื่อรายการประจำสร้างเอกสาร"/กฎเสมือน (ทิศเดียว — ไม่แตะฝั่ง DocumentApproved)
            var approvedRuleCovers = await _db.EmailScheduleRules.AsNoTracking()
                .AnyAsync(r => r.CompanyId == companyId && r.IsActive && !r.IsDeleted
                    && r.Trigger == "DocumentApproved"
                    && (r.DocumentType == null || r.DocumentType == doc.DocumentType.ToString()), ct);
            // ฝ่ายค้าน WP-02 ข้อ 6: OnDocumentApprovedAsync กลืนความผิดพลาด (LogWarning) ⇒ "มีกฎ" ≠ "เข้าคิวแล้ว" — ข้ามเฉพาะเมื่อมีแถวคิวของกฎนั้นจริง
            // (กุญแจเดียวกับฝั่งเขียน) · ไม่มีแถว ⇒ เดินเส้นรายการประจำต่อ (ลูกค้าต้องได้อีเมลหนึ่งฉบับ ไม่ใช่ศูนย์)
            var approvedKey = DocumentQueueKey(doc.Id, "DocumentApproved", "approved");
            var approvedQueued = approvedRuleCovers && await _db.EmailQueues.AsNoTracking()
                .AnyAsync(q => q.CompanyId == companyId && q.IdempotencyKey == approvedKey && !q.IsDeleted
                    // ฝ่ายค้าน MED-6: แถว "ข้าม" (ไม่ส่งเพราะค่าตั้งผู้ติดต่อ/ใบเปลี่ยน) ไม่ใช่การส่ง ⇒ ไม่นับว่ากฎนั้นครอบแล้ว
                    && q.Status != ScheduledEmailSendCheck.SkippedStatus, ct);
            // รอบ 258 (A-M1 ก): กฎเมื่ออนุมัติที่ติ๊ก LINE อย่างเดียวมีแถวใน LineQueues (ไม่มีแถวอีเมล) ⇒ นับด้วย — ไม่งั้นลูกค้าได้การ์ด LINE สองใบ/LINE + อีเมล
            approvedQueued = approvedRuleCovers && EmailEnqueueOutcome.ApprovedRuleDelivered(approvedQueued,
                await LineRowQueuedAsync(companyId, doc.Id, approvedKey, ct));
            if (EmailEnqueueOutcome.SkipForApprovedRule(approvedRuleCovers, documentApproved: true, approvedQueued))
                return EmailEnqueueOutcome.Skipped(EmailEnqueueOutcome.CoveredByApprovedRule);

            var rules = await _db.EmailScheduleRules.AsNoTracking()
                .Where(r => r.CompanyId == companyId && r.IsActive && !r.IsDeleted
                    && r.Trigger == "RecurringInvoiceCreated"
                    && (r.DocumentType == null || r.DocumentType == doc.DocumentType.ToString()))
                .ToListAsync(ct);
            // รอบ 248 (AUTOPILOT §5.1 · A-1): ช่องทางรับเงินของรายการประจำเป็นชั้นใต้ตารางส่ง (ตาราง › รายการ › ผู้ติดต่อ › บริษัท — AutopilotSettingsResolver) ·
            // ช่องที่ตารางไม่ได้ตั้ง (null) รับค่าจากรายการ · ชั้นผู้ติดต่อ/บริษัทต่อใน DocumentPayLinkService.ForScheduledEmailAsync
            var recurringLayer = await RecurringPayLayerAsync(companyId, recurringId, ct);
            if (rules.Count > 0)
            {
                // recurringId เข้า suffix → idempotency: ทุก recurring run ไม่ทับกัน
                string? notSent = null;
                var anyQueued = false;
                foreach (var rule in rules.Select(r => WithRecurringPayLayer(r, recurringLayer)))
                {
                    var one = await EnqueueDocumentAsync(rule, doc, daysOffset: rule.OffsetDays,
                        suffix: $"recurring-{recurringId}", ct);
                    anyQueued |= one.Queued || one.Duplicate;
                    notSent ??= one.NotSent;
                }
                await _db.SaveChangesAsync(ct);
                return !anyQueued && notSent != null ? EmailEnqueueOutcome.NotSent(notSent) : EmailEnqueueOutcome.Enqueued();
            }

            // ── ไม่มีกฎในหน้า "ตารางส่งอีเมล" → ใช้ธง AutoSendEmail บนตัว
            // recurring แทน. เจตนา: ผู้ใช้ติ๊กในฟอร์ม "สร้างรายการประจำ" ครั้ง
            // เดียวจบ ไม่ต้องไปประกอบกฎเองอีกหน้า (เดิมติ๊กแล้วไม่มีอะไรเกิดขึ้น
            // = silent no-op). ถ้ามีกฎอยู่แล้วให้กฎชนะ — กันส่งซ้ำ 2 ฉบับ
            var autoSend = await _db.RecurringTransactions.AsNoTracking()
                .Where(r => r.Id == recurringId && r.CompanyId == companyId)
                .Select(r => (bool?)r.AutoSendEmail).FirstOrDefaultAsync(ct);
            if (autoSend != true) return EmailEnqueueOutcome.Skipped("รายการนี้ไม่ได้ตั้งส่งอีเมลอัตโนมัติ และไม่มีกฎส่งในหน้าตารางส่งอีเมล");

            // รอบ 250 WP-08: ผู้รับจากตัวหาผู้รับเดียว (ไม่อ่าน Contact.Email ตรง) — ไม่มีอีเมลเลย ⇒ ผู้เรียกแจ้งด้วย RecurringEmailReadiness (EmailSkippedNoContactEmail) ·
            // มีอีเมลแต่ไม่รับเรื่อง "เอกสาร" ⇒ แถวคิว "ข้าม" (เขียนใน EnqueueDocumentAsync) + คืนเหตุให้ผู้เรียกแสดง (EmailNotSentReason)
            var inlineDelivery = await EmailForAsync(companyId, doc.ContactId, NotifyTopics.ForTrigger("RecurringInvoiceCreated"), ct);
            // รอบ 258-3 (F-H1): ธงส่งของรายการประจำ ⇒ LINE ตามปลายทางของผู้ติดต่อที่รับเรื่อง "เอกสาร" (ตัวหาผู้รับเดียว · ตัวตัดสิน ContactLineChannel.InlineChannels) —
            // เส้นเข้าคิวเดียวกับกฎตารางส่ง (EnqueueDocumentAsync → EnqueueLineAsync) ไม่มีสำเนากติกา · ผู้ติดต่อที่มีแต่ LINE ⇒ ไม่หยุดที่ "ไม่มีอีเมล"
            var inlineLine = ContactLineChannel.InlineChannels(
                (await _recipients.ResolveAsync(companyId, doc.ContactId, NotifyTopics.ForTrigger("RecurringInvoiceCreated"), ct)).Options);
            if (!inlineDelivery.Deliverable && inlineDelivery.OptOutReason == null && !inlineLine.LineUser && !inlineLine.LineGroup)
                return EmailEnqueueOutcome.Skipped(RecurringEmailReadiness.NotSentNoEmail);

            // rule เสมือน (ไม่บันทึกลง DB) — EmailQueue.RuleId เป็น null ได้
            // Id = Guid.Empty เพื่อให้ EnqueueDocumentAsync ใส่ RuleId = null
            // (ไม่งั้น FK ชี้ไปกฎที่ไม่มีจริง)
            var inlineRule = new EmailScheduleRule
            {
                Id = Guid.Empty,
                CompanyId = companyId,
                Trigger = "RecurringInvoiceCreated",
                OffsetDays = 0,
                SendAtHour = 9,
                // รอบ 248: ชั้นรายการประจำ (null = ตามผู้ติดต่อ › บริษัท — พฤติกรรมเดิม)
                AttachPayLink = recurringLayer?.Attach,
                PayLinkChannelsJson = DocumentPayLinkPolicy.SerializeChannels(recurringLayer?.Channels),
                ChannelLineUser = inlineLine.LineUser,
                ChannelLineGroup = inlineLine.LineGroup,
            };
            // ส่งทันที ไม่รอ 09:00 ของวัน — เจตนาคือ "ออกเอกสารแล้วส่งเลย"
            var inlineResult = await EnqueueDocumentAsync(inlineRule, doc, daysOffset: 0,
                suffix: $"autosend-{recurringId}", ct, overrideSendUtc: DateTime.UtcNow);
            await _db.SaveChangesAsync(ct);
            return inlineResult.NotSent != null ? EmailEnqueueOutcome.NotSent(inlineResult.NotSent) : EmailEnqueueOutcome.Enqueued();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // รอบ 250 (WP-02 §2.4 ข้อ 1): ล้มดัง — คืน Failed ให้ผู้เรียกตั้งธง EmailEnqueueFailed + เรื่องที่ต้องทำต่อบนแถวรายการประจำ (ไม่ใช่ LogWarning อย่างเดียว)
            _logger.LogError(ex, "OnRecurringDocumentCreated enqueue failed Doc={Doc}", documentId);
            return EmailEnqueueOutcome.Failed(ex.Message);
        }
    }

    /// <summary>รอบ 248: ชั้นช่องทางรับเงินของรายการประจำ (null = ไม่ได้ตั้ง) — ตัวต่อชั้นเดียว <see cref="AutopilotSettingsResolver.FromMode"/></summary>
    private async Task<AutopilotSettingsResolver.Layer?> RecurringPayLayerAsync(Guid companyId, Guid recurringId, CancellationToken ct)
    {
        var rec = await _db.RecurringTransactions.AsNoTracking()
            .Where(r => r.Id == recurringId && r.CompanyId == companyId)
            .Select(r => new { r.PaymentMode, r.PayLinkChannelsJson })
            .FirstOrDefaultAsync(ct);
        if (rec == null || (rec.PaymentMode == null && rec.PayLinkChannelsJson == null) || _payLinks == null) return null;
        var available = DocumentPayLinkPolicy.CompanyAvailable(await _payLinks.CompanyFactsAsync(companyId, ct));
        var layer = AutopilotSettingsResolver.FromMode(rec.PaymentMode, DocumentPayLinkPolicy.ParseChannels(rec.PayLinkChannelsJson), available);
        return layer.Attach == null && layer.Channels == null ? null : layer;
    }

    /// <summary>รอบ 248: ตารางส่ง (ชั้นบนสุด) ทับรายการประจำ — เติมเฉพาะช่องที่ตารางว่าง · ⚠️ แก้บนแถวที่อ่านแบบ AsNoTracking เท่านั้น
    /// (ไม่ถูกติดตาม ⇒ ไม่ถูกบันทึกกลับเป็นค่าของกฎ)</summary>
    internal static EmailScheduleRule WithRecurringPayLayer(EmailScheduleRule rule, AutopilotSettingsResolver.Layer? layer)
    {
        if (layer == null) return rule;
        rule.AttachPayLink ??= layer.Attach;
        rule.PayLinkChannelsJson ??= DocumentPayLinkPolicy.SerializeChannels(layer.Channels);
        return rule;
    }

    /// <summary>สรุปรายการที่เกิดขึ้นกับลูกค้าในเดือนที่แล้ว — invoice ที่
    /// ออก/ที่ชำระ/ที่ยังค้าง. ส่งวันที่ DayOfMonth ของเดือนปัจจุบัน
    /// (default 5 = ส่งวันที่ 5 รวมยอดเดือนก่อน). Idempotency: ป้องกัน
    /// ส่งซ้ำต่อ contact ต่อ rule ต่อรอบ year+month ที่เป็น "ของเดือนที่แล้ว".</summary>
    public async Task<int> ScanMonthlyStatementsAsync(CancellationToken ct = default)
    {
        var todayBkk = BangkokNow().Date;
        var rules = await _db.EmailScheduleRules.AsNoTracking()
            .Where(r => r.IsActive && !r.IsDeleted && r.Trigger == "MonthlyStatement")
            .ToListAsync(ct);
        if (rules.Count == 0) return 0;

        var scannedRules = new List<(EmailScheduleRule Rule, string? CatchUpNote)>();
        int enqueued = 0, notSentRows = 0;
        foreach (var rule in rules)
        {
            // ส่งเฉพาะวันที่ DayOfMonth ของเดือนปัจจุบัน
            var sendDay = Math.Clamp(rule.DayOfMonth <= 0 ? 5 : rule.DayOfMonth, 1, 28);
            // รอบ 258 (B-M7): ระบบไม่ได้ทำงานในวันที่ตั้งไว้ ⇒ ส่งย้อนหลังได้ไม่เกิน 3 วันในเดือนเดียวกัน (หลักฐาน = ลายน้ำต่อกฎ · กุญแจกันซ้ำเดิม)
            var monthlyPlan = ScheduleCatchUp.Monthly(todayBkk, sendDay, rule.LastDayScanBkk);
            scannedRules.Add((rule, monthlyPlan.Late ? ScheduleCatchUp.MonthlyNote(monthlyPlan.TargetDay, todayBkk) : null));
            if (!monthlyPlan.Send) continue;

            // ยอดเดือนที่แล้ว
            var prevMonthStart = new DateTime(todayBkk.Year, todayBkk.Month, 1).AddMonths(-1);
            var prevMonthEnd = prevMonthStart.AddMonths(1).AddDays(-1);
            var period = $"{prevMonthStart:yyyy-MM}";
            var sendUtc = ScheduledUtc(todayBkk, rule.SendAtHour);

            // หา contact ที่มีรายการในเดือนที่แล้ว (ฝั่งขายเท่านั้น — AR statement)
            var arTypes = new[] { DocumentType.Invoice, DocumentType.TaxInvoice,
                DocumentType.Receipt, DocumentType.BillingNote, DocumentType.CreditNote, DocumentType.DebitNote };
            var contactRows = await _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == rule.CompanyId && !d.IsDeleted
                    && arTypes.Contains(d.DocumentType)
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                    && d.DocumentDate >= prevMonthStart && d.DocumentDate <= prevMonthEnd
                    && d.ContactId != Guid.Empty)
                .GroupBy(d => d.ContactId)
                .Select(g => new { ContactId = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            if (contactRows.Count == 0) continue;

            var contactIds = contactRows.Select(x => x.ContactId).ToList();
            var contacts = await _db.Contacts.AsNoTracking()
                .Where(c => c.CompanyId == rule.CompanyId && contactIds.Contains(c.Id) && !c.IsDeleted)
                .ToListAsync(ct);

            // company name สำหรับ template
            var companyName = await _db.Companies.AsNoTracking()
                .Where(c => c.Id == rule.CompanyId)
                .Select(c => c.Name).FirstOrDefaultAsync(ct) ?? "";

            foreach (var contact in contacts)
            {
                // รอบ 250 WP-08: ใบแจ้งยอด = เรื่อง "ทวงชำระ/ใบแจ้งยอด" ตามช่องทางแจ้งเตือนของผู้ติดต่อ (ตัวหาผู้รับเดียว)
                var delivery = await EmailForAsync(rule.CompanyId, contact.Id, NotifyTopics.ForTrigger(rule.Trigger), ct);
                var stmtNotSent = AutopilotSettingsResolver.AutoSendBlock(contact.AutoSendDisabled) ?? delivery.OptOutReason;
                if (!delivery.Deliverable && stmtNotSent == null) continue;
                var idem = $"statement:{contact.Id}:{period}:{rule.Id}";
                if (await _db.EmailQueues.AnyAsync(q => q.CompanyId == rule.CompanyId
                    && q.IdempotencyKey == idem && !q.IsDeleted, ct)) continue;
                if (stmtNotSent != null)
                {
                    AddNotSentRow(rule.CompanyId, rule.Id, "MonthlyStatement", contact.Id,
                        Render(rule.SubjectTemplate ?? "สรุปรายการเดือน {Period} — {CompanyName}",
                            new Dictionary<string, string?> { ["Period"] = period, ["CompanyName"] = companyName, ["ContactName"] = contact.Name }),
                        sendUtc, idem, stmtNotSent);
                    notSentRows++;
                    continue;
                }

                // รวมยอดของ contact นี้ในเดือน
                var monthDocs = await _db.Documents.AsNoTracking()
                    .Where(d => d.CompanyId == rule.CompanyId && !d.IsDeleted
                        && d.ContactId == contact.Id
                        && arTypes.Contains(d.DocumentType)
                        && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                        && d.DocumentDate >= prevMonthStart && d.DocumentDate <= prevMonthEnd)
                    .Select(d => new { d.DocumentNumber, d.DocumentDate, d.DocumentType,
                        d.TotalAmount, d.BalanceDue, d.DueDate })
                    .OrderBy(d => d.DocumentDate).ToListAsync(ct);
                if (monthDocs.Count == 0) continue;

                var totalAmount = monthDocs.Sum(d => d.TotalAmount);
                var totalOutstanding = monthDocs.Sum(d => d.BalanceDue);
                // รอบ 260-3 (คำตัดสิน 260-3-2): เงินรอจัดสรรของลูกค้า (ตัวอ่านเดียว · ตัวเลขเดียวกับใบแจ้งยอดบนหน้า/CSV/พอร์ทัล) — บรรทัดแยก ไม่หักจากยอดค้าง
                var unappliedCredit = await CustomerCreditQuery.UnappliedForContactAsync(_db, rule.CompanyId, contact.Id, ct);
                var hasUnapplied = unappliedCredit > UnappliedCreditBalance.Tolerance;
                var unappliedHtml = hasUnapplied
                    ? $"<p style='background:#eff6ff;padding:8px 12px;border-radius:6px'>เงินที่ชำระไว้แล้วรอนำไปหักกับใบ (เงินรอจัดสรร) <strong>{unappliedCredit:N2} บาท</strong> — ยังไม่หักจากยอดค้างชำระด้านบน</p>"
                    : "";
                var ctx = new Dictionary<string, string?>
                {
                    // ฝ่ายค้าน 260-3 L3: ไม่มีเงินรอจัดสรร = ว่าง (ไม่ใช่ "0.00" ที่ทำให้เข้าใจผิด) · ประโยคสำเร็จรูปสำหรับแม่แบบของผู้ใช้ (ถูก HtmlEncode ตอน Render)
                    ["UnappliedCredit"] = hasUnapplied ? unappliedCredit.ToString("N2") : "",
                    ["UnappliedCreditLine"] = hasUnapplied ? $"เงินที่ชำระไว้แล้วรอนำไปหักกับใบ (เงินรอจัดสรร) {unappliedCredit:N2} บาท — ยังไม่หักจากยอดค้างชำระ" : "",
                    ["ContactName"] = contact.Name,
                    ["Period"] = period,
                    ["Amount"] = totalAmount.ToString("N2"),
                    ["BalanceDue"] = totalOutstanding.ToString("N2"),
                    ["CompanyName"] = companyName,
                    ["DocCount"] = monthDocs.Count.ToString(),
                };

                // ตาราง HTML สำหรับ default body
                var rowsHtml = string.Concat(monthDocs.Select(d =>
                    $"<tr><td style='padding:6px 10px;border-bottom:1px solid #e5e7eb'>{d.DocumentDate:dd/MM/yyyy}</td>"
                  + $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;font-family:monospace'>{System.Net.WebUtility.HtmlEncode(d.DocumentNumber)}</td>"
                  + $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;text-align:right'>{d.TotalAmount:N2}</td>"
                  + $"<td style='padding:6px 10px;border-bottom:1px solid #e5e7eb;text-align:right;color:{(d.BalanceDue > 0 ? "#dc2626" : "#10b981")}'>{d.BalanceDue:N2}</td></tr>"));

                var subject = Render(rule.SubjectTemplate ?? "สรุปรายการเดือน {Period} — {CompanyName}", ctx);
                var body = !string.IsNullOrEmpty(rule.BodyTemplate)
                    ? Render(rule.BodyTemplate, ctx, htmlEncodeValues: true)
                    : $@"<div style='font-family:sans-serif;max-width:680px'>
                        <p>เรียน คุณ{System.Net.WebUtility.HtmlEncode(contact.Name)}</p>
                        <p>สรุปรายการของเดือน <strong>{period}</strong> จำนวน {monthDocs.Count} รายการ ยอดรวม <strong>{totalAmount:N2} บาท</strong>
                        ค้างชำระ <strong style='color:#dc2626'>{totalOutstanding:N2} บาท</strong></p>
                        <table style='width:100%;border-collapse:collapse;font-size:13px;margin-top:10px'>
                          <thead><tr style='background:#f8fafc'>
                            <th style='padding:8px;text-align:left'>วันที่</th>
                            <th style='padding:8px;text-align:left'>เลขที่</th>
                            <th style='padding:8px;text-align:right'>ยอดรวม</th>
                            <th style='padding:8px;text-align:right'>ค้างชำระ</th>
                          </tr></thead>
                          <tbody>{rowsHtml}</tbody>
                        </table>
                        {unappliedHtml}
                        <p style='color:#64748b;font-size:13px;margin-top:14px'>ส่งโดย {System.Net.WebUtility.HtmlEncode(companyName)} โดยอัตโนมัติ</p>
                      </div>";

                var stmtTo = await CapRecipientsAsync(rule.CompanyId, delivery.To!, rule.BccEmails, ct);   // ฝ่ายค้าน MED-3
                _db.EmailQueues.Add(new EmailQueue
                {
                    CompanyId = rule.CompanyId,
                    RuleId = rule.Id,
                    EntityType = "MonthlyStatement",
                    EntityId = contact.Id,
                    ErrorMessage = stmtTo.Note,
                    ToEmail = stmtTo.To,
                    BccEmail = rule.BccEmails,
                    Subject = subject,
                    Body = body,
                    AttachPdf = false,    // statement render inline ใน body ไม่มี PDF
                    ScheduledFor = sendUtc,
                    IdempotencyKey = idem,
                });
                enqueued++;
            }
        }
        if (enqueued > 0 || notSentRows > 0) await _db.SaveChangesAsync(ct);
        await StampScanDayAsync(scannedRules, todayBkk, ct);   // รอบ 258 (B-M7): หลังบันทึกคิวสำเร็จเท่านั้น (ล้มกลางทาง = รอบถัดไปย้อนเก็บใหม่)
        return enqueued;
    }

    /// <summary>รอบ 258 (B-M7): ประทับลายน้ำ "สแกนกฎนี้ของวันนี้แล้ว" (วันที่ไทย) + บันทึกที่ผู้ใช้เห็นเมื่อมีการย้อนเก็บ — วันละครั้งต่อกฎ ·
    /// tenant: กรองด้วย CompanyId ของกฎ · ผู้อ่าน: <see cref="ScheduleCatchUp"/> (รอบถัดไป) + หน้าตารางส่ง (บรรทัด "ส่งย้อนหลัง")</summary>
    private async Task StampScanDayAsync(IEnumerable<(EmailScheduleRule Rule, string? CatchUpNote)> scanned, DateTime todayBkk, CancellationToken ct)
    {
        var stamp = todayBkk.Date;
        foreach (var (rule, note) in scanned)
        {
            if (rule.LastDayScanBkk?.Date == stamp && note == null) continue;
            var ruleId = rule.Id;
            var companyId = rule.CompanyId;
            if (note == null)
                await _db.EmailScheduleRules.Where(r => r.Id == ruleId && r.CompanyId == companyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastDayScanBkk, (DateTime?)stamp), ct);
            else
                await _db.EmailScheduleRules.Where(r => r.Id == ruleId && r.CompanyId == companyId)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastDayScanBkk, (DateTime?)stamp)
                        .SetProperty(r => r.LastCatchUpNote, note), ct);
            rule.LastDayScanBkk = stamp;
        }
    }

    public async Task<int> ScanDueSoonAndOverdueAsync(CancellationToken ct = default)
    {
        var realToday = BangkokNow().Date;
        var rules = await _db.EmailScheduleRules.AsNoTracking()
            .Where(r => r.IsActive && !r.IsDeleted
                && (r.Trigger == "DocumentDueSoon" || r.Trigger == "DocumentOverdue"))
            .ToListAsync(ct);
        if (rules.Count == 0) return 0;

        // รอบ 258 (B-M7): วันที่ระบบไม่ได้สแกน (ลายน้ำต่อกฎ · ย้อนไม่เกิน 3 วัน) เก็บก่อน แล้วค่อยสแกนวันนี้ — ตัวตัดสินเดียว ScheduleCatchUp
        var total = 0;
        var missedDays = rules.SelectMany(r => ScheduleCatchUp.MissedDays(realToday, r.LastDayScanBkk)).Distinct().OrderBy(d => d).ToList();
        foreach (var day in missedDays) total += await ScanDayAsync(day);
        total += await ScanDayAsync(realToday);
        await StampScanDayAsync(rules.Select(r => (r, ScheduleCatchUp.Note(
            ScheduleCatchUp.MissedDays(realToday, r.LastDayScanBkk)
                .Where(d => ScheduleCatchUp.DueRuleCatchesUp(r.Trigger, r.OffsetDays, r.RepeatEveryDays, d, realToday, r.LastDayScanBkk)).ToList(),
            realToday))).ToList(), realToday, ct);
        return total;

        // สแกนของ "วัน today" · today ≠ realToday = ย้อนเก็บวันที่พลาด (เฉพาะกฎที่ ScheduleCatchUp.DueRuleCatchesUp ตอบใช่ · ทวงเฉพาะครั้งแรกของใบ ·
        // เวลาส่งตามวันนี้จริง) · local function โดยตั้งใจ: ลูปเดิมอยู่ในเมธอดชื่อเดิม (กติกา call-site เดิมผูกชื่อนี้) และเยื้องเท่าเดิม (ลด diff กับงานที่แก้ลูปนี้พร้อมกัน)
        async Task<int> ScanDayAsync(DateTime today)
        {
        var catchUp = today != realToday;
        int enqueued = 0, notSentRows = 0;
        // รอบ 258 (DECISIONS 258-1-9): เงินรอจัดสรรต่อลูกค้า โหลดครั้งเดียวต่อบริษัท · กุญแจแถว "ข้าม" ที่เพิ่มในรอบสแกนนี้ (สองกฎของใบเดียวกันห้ามเพิ่มซ้ำ — ดัชนี unique)
        var creditHoldsByCompany = new Dictionary<Guid, IReadOnlyDictionary<Guid, string>>();
        var creditHoldKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (catchUp && !ScheduleCatchUp.DueRuleCatchesUp(rule.Trigger, rule.OffsetDays, rule.RepeatEveryDays, today, realToday, rule.LastDayScanBkk)) continue;
            // DueSoon: OffsetDays ติดลบ — เช่น -3 = ส่งล่วงหน้า 3 วัน
            // → DueDate ของเอกสารต้องเท่ากับ today + |OffsetDays|.
            // Overdue: OffsetDays บวก — DueDate เท่ากับ today − OffsetDays.
            var target = rule.Trigger == "DocumentDueSoon"
                ? today.AddDays(Math.Abs(rule.OffsetDays))
                : today.AddDays(-rule.OffsetDays);
            // รอบ 250 WP-04 ต่อยอด: "เกินกำหนด" + ส่งซ้ำทุก N วัน ⇒ ใบที่เกินกำหนดครบ Offset แล้วทุกใบ แล้วคัดด้วยตัวตัดสินเดียว (OverdueReminderSchedule) ·
            // ไม่ตั้งส่งซ้ำ (0) ⇒ เท่ากับวันเป้าหมายตามเดิม
            var repeating = !catchUp && rule.Trigger == "DocumentOverdue" && rule.RepeatEveryDays > 0;   // รอบ 258: ย้อนเก็บ = เฉพาะครั้งแรกของใบ

            // ฝ่ายค้าน H1: ครั้งซ้ำไม่เกิน MaxOverdueDays ⇒ ไม่ดึงใบเก่ากว่านั้น
            var oldest = today.AddDays(-Math.Max(Accounting.Helpers.OverdueReminderSchedule.MaxOverdueDays, rule.OffsetDays));
            // ฝ่ายค้าน M3: ทวง/เตือนครบกำหนดเฉพาะเอกสารฝั่งลูกหนี้ (ตัวตั้งเดียว ArApScope) — ไม่ส่งอีเมลทวงหาผู้ขายจากใบซื้อที่เราค้างจ่าย
            var receivable = Accounting.Helpers.ArApScope.ReceivableTypes;
            var docsQuery = _db.Documents.AsNoTracking()
                .Where(d => d.CompanyId == rule.CompanyId
                    && !d.IsDeleted
                    && receivable.Contains(d.DocumentType)
                    && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft
                    && d.BalanceDue > 0
                    && d.DueDate != null
                    && (repeating ? d.DueDate.Value.Date <= target && d.DueDate.Value.Date >= oldest : d.DueDate.Value.Date == target));
            if (!string.IsNullOrEmpty(rule.DocumentType)
                && Enum.TryParse<DocumentType>(rule.DocumentType, out var dt))
                docsQuery = docsQuery.Where(d => d.DocumentType == dt);

            var docs = await docsQuery.ToListAsync(ct);
            if (repeating)
            {
                DateTime? activatedOn = rule.RepeatActivatedAt is DateTime act ? Accounting.Helpers.ThaiDate.CalendarDateUtc(act).Date : null;
                docs = docs.Where(d => Accounting.Helpers.OverdueReminderSchedule.SendToday(
                    (today - d.DueDate!.Value.Date).Days, rule.OffsetDays, rule.RepeatEveryDays,
                    d.DueDate.Value.Date.AddDays(rule.OffsetDays), activatedOn)).ToList();
            }
            await _db.HydrateContactsAsync(rule.CompanyId, docs);  // กัน INNER JOIN ตัดใบที่ contact ถูกลบ
            var sendUtc = ScheduledUtc(realToday, rule.SendAtHour);   // รอบ 258: ย้อนเก็บส่งตามชั่วโมงของวันนี้จริง (เลยชั่วโมงแล้ว = รอบถัดไป)
            // ฝ่ายค้านรอบ 258 M1: ใบที่ถูกระงับ = แผนหักเงินสะสมเก่าสุดก่อน (เงินแต่ละบาทนับครั้งเดียว · เฉพาะใบที่จัดสรรได้จริง) — ตัวอ่านเดียว DunningHoldsAsync
            if (!creditHoldsByCompany.TryGetValue(rule.CompanyId, out var creditHolds))
                creditHoldsByCompany[rule.CompanyId] = creditHolds =
                    await Accounting.Helpers.CustomerCreditQuery.DunningHoldsAsync(_db, rule.CompanyId, ct);
            var creditHoldSuffix = $"credit-hold-{rule.Id:N}";
            // M1 (การเตือนแบบตรงวันไม่หาย): ใบที่กฎนี้เคยระงับไว้ และตอนนี้ไม่ถูกระงับแล้วแต่ยังค้าง ⇒ ส่งการเตือนที่ถูกข้ามไป (ครั้งเดียว) แล้วปิดแถวระงับ
            if (!catchUp)
            {
                var heldRows = await _db.EmailQueues
                    .Where(q => q.CompanyId == rule.CompanyId && !q.IsDeleted && q.EntityType == "Document"
                        && q.IdempotencyKey != null && q.IdempotencyKey.EndsWith(":" + creditHoldSuffix))
                    .ToListAsync(ct);
                foreach (var heldRow in heldRows.Where(r => !creditHolds.ContainsKey(r.EntityId)))
                {
                    heldRow.IsDeleted = true;
                    heldRow.UpdatedAt = DateTime.UtcNow;
                    notSentRows++;
                    if (docs.Any(d => d.Id == heldRow.EntityId)) continue;   // วันนี้กฎนี้หยิบใบนี้เองอยู่แล้ว
                    var released = await _db.Documents.AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Id == heldRow.EntityId && d.CompanyId == rule.CompanyId && !d.IsDeleted
                            && d.Status != DocumentStatus.Voided && d.Status != DocumentStatus.Draft && d.BalanceDue > 0 && d.DueDate != null, ct);
                    if (released == null) continue;
                    if (rule.Trigger == "DocumentDueSoon" && released.DueDate!.Value.Date < today) continue;   // เลยกำหนดแล้ว — "ใกล้ครบกำหนด" ไม่มีความหมาย
                    await _db.HydrateContactsAsync(rule.CompanyId, new List<Document> { released });
                    var late = await EnqueueDocumentAsync(rule, released, daysOffset: 0, suffix: "credit-release", ct, overrideSendUtc: sendUtc);
                    if (late.Queued) enqueued++;
                    else if (late.NotSent != null) notSentRows++;
                }
            }
            foreach (var doc in docs)
            {
                var overdueDays = (today - doc.DueDate!.Value.Date).Days;
                // รอบ 258: ลูกค้ามีเงินรอจัดสรรพอปิดใบนี้ (โอนมาแล้วแต่ยังไม่ได้ผูกกับใบ) ⇒ ไม่เตือน/ทวงลูกค้า (อีเมลและ LINE) ·
                // แถว "ข้าม" พร้อมเหตุครั้งเดียวต่อใบต่อ trigger (ทีมงานเห็นในประวัติการส่ง + OverdueDunningJob แจ้งทีมให้จัดสรร)
                if (creditHolds.TryGetValue(doc.Id, out var creditHoldReason))
                {
                    var holdKey = DocumentQueueKey(doc.Id, rule.Trigger, creditHoldSuffix);
                    if (creditHoldKeys.Add(holdKey)
                        && !await _db.EmailQueues.AnyAsync(q => q.CompanyId == doc.CompanyId && q.IdempotencyKey == holdKey && !q.IsDeleted, ct))
                    {
                        AddNotSentRow(doc.CompanyId, rule.Id, "Document", doc.Id, $"แจ้งเตือนการชำระ {doc.DocumentNumber}", sendUtc, holdKey,
                            creditHoldReason);
                        notSentRows++;
                    }
                    continue;
                }
                var queued = await EnqueueDocumentAsync(rule, doc, daysOffset: 0,
                    suffix: rule.Trigger == "DocumentDueSoon" ? $"due-{rule.OffsetDays}"
                        : Accounting.Helpers.OverdueReminderSchedule.Suffix(rule.OffsetDays, overdueDays, today),
                    ct, overrideSendUtc: sendUtc,
                    // ฝ่ายค้าน M4: ผู้ติดต่อปิดรับ ⇒ แถว "ข้าม" เฉพาะครั้งแรก (ไม่สะสมทุกรอบส่งซ้ำ)
                    recordNotSent: !(rule.Trigger == "DocumentOverdue" && Accounting.Helpers.OverdueReminderSchedule.IsRepeat(overdueDays, rule.OffsetDays)));
                if (queued.Queued) enqueued++;
                else if (queued.NotSent != null) notSentRows++;
            }
        }
        // แถว "ข้าม" (ยกเลิกรับเรื่องทวงชำระ) ต้องถูกบันทึกด้วย — ไม่งั้นเหตุหาย
        // รอบ 259 (259-2-7): บันทึกแบบ idempotent — ผู้สแกนอีกตัวบันทึกกุญแจเดียวกันไปก่อน / สองกฎ-สองวันได้กุญแจเดียวกันในรอบนี้
        // ⇒ ถอดแถวซ้ำ (ผลเดียวกับที่ต้องการ) แทนที่ทั้งรอบจะล้ม (เดิม: 23505 ⇒ แถวของทุกใบในรอบหาย + ลายน้ำไม่ขยับ ⇒ ล้มซ้ำทุกนาที)
        if (enqueued > 0 || notSentRows > 0 || _db.ChangeTracker.HasChanges())
            await QueueRowSave.SaveSkippingExistingKeysAsync(_db, ct);
        return enqueued;
        }
    }

    /// <summary>รอบ 258 (B-H2): แถวที่ค้าง "กำลังส่ง" เกิน <see cref="EmailQueueStaleSending.StaleAfter"/> (ระบบหยุด/ deploy กลางแบตช์) ⇒ <b>Failed พร้อมเหตุ</b> —
    /// ไม่คืนเป็น Pending เอง (ผู้ให้บริการอาจรับอีเมลไปแล้ว = ส่งซ้ำ) · คนกด "ลองใหม่"/"ยกเลิก" ได้ และโผล่ในเรื่องค้างของ Autopilot ·
    /// งานเบื้องหลังข้ามบริษัท (เหมือนการจองคิวด้านล่าง) — คำสั่งเดียวแบบมีเงื่อนไข ปลอดภัยเมื่อหลายเครื่องรันพร้อมกัน · คืนจำนวนแถวที่กู้</summary>
    internal async Task<int> RecoverStaleSendingAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var cutoff = EmailQueueStaleSending.Cutoff(nowUtc);
        // รอบ 258 ฝ่ายค้าน R-M3 (เส้นแบ่ง "ค้าง" = EmailQueueStaleSending.Cutoff): (ก) จองแล้วแต่ยังไม่เคยเริ่มส่ง (SendStartedAt ว่าง) ⇒ คืนเข้าคิว —
        // ระบบรู้แน่ว่าไม่เคยเรียกผู้ให้บริการ จึงไม่มีทางส่งซ้ำ · ล้างโทเคนผู้จอง (เครื่องเดิมที่ยังถือแถวนี้ในหน่วยความจำจะไม่ผ่านด่านเริ่มส่ง)
        var requeued = await _db.EmailQueues
            .Where(q => q.Status == EmailQueueStaleSending.StatusSending && q.SendStartedAt == null && (q.UpdatedAt ?? q.CreatedAt) < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, EmailQueueStaleSending.StatusPending)
                .SetProperty(q => q.ClaimToken, (Guid?)null)
                .SetProperty(q => q.UpdatedAt, (DateTime?)nowUtc), ct);
        // (ข) เริ่มส่งแล้วตาย ⇒ Failed พร้อมเหตุ — ไม่รู้ว่าส่งถึงหรือไม่ คนตรวจแล้วลองใหม่/ยกเลิก (คำตัดสิน 258-2-4)
        var n = await _db.EmailQueues
            .Where(q => q.Status == EmailQueueStaleSending.StatusSending && q.SendStartedAt != null && (q.UpdatedAt ?? q.CreatedAt) < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, EmailQueueStaleSending.StatusFailed)
                .SetProperty(q => q.ErrorMessage, EmailQueueStaleSending.StaleReason)
                .SetProperty(q => q.ClaimToken, (Guid?)null)
                .SetProperty(q => q.UpdatedAt, (DateTime?)nowUtc), ct);
        if (requeued > 0) _logger.LogWarning("EmailQueue: คืน {Count} แถวที่จองไว้แต่ยังไม่เคยเริ่มส่งกลับเข้าคิว (ตัวส่งหยุดกลางแบตช์)", requeued);
        if (n > 0) _logger.LogError("EmailQueue: พบ {Count} แถวที่เริ่มส่งแล้วค้างเกิน {Minutes} นาที — ประทับล้มพร้อมเหตุ (รอคนตรวจแล้วลองใหม่/ยกเลิก)",
            n, (int)EmailQueueStaleSending.StaleAfter.TotalMinutes);
        return n + requeued;
    }

    public async Task<int> ProcessPendingQueueAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        await RecoverStaleSendingAsync(now, ct);   // รอบ 258 (B-H2): ก่อนจองรอบใหม่
        var claimToken = Guid.NewGuid();   // รอบ 258 ฝ่ายค้าน R-M3: ผู้จองรอบนี้ — ด่านเริ่มส่งต่อแถวเทียบโทเคนนี้

        // Atomic claim with SKIP LOCKED — required for multi-instance correctness.
        // Without this, two pods read the same top-50 "Pending" rows, both flip to
        // "Sending", and the customer receives the invoice/statement twice. The
        // CTE locks the chosen rows, updates them to "Sending" in one statement,
        // and returns the locked ids. Subsequent workers SKIP those rows.
        var claimedIds = await _db.Database.SqlQuery<Guid>(
            $@"WITH claimed AS (
                SELECT ""Id""
                FROM ""EmailQueues""
                WHERE NOT ""IsDeleted"" AND ""Status"" = 'Pending' AND ""ScheduledFor"" <= {now}
                ORDER BY ""ScheduledFor""
                LIMIT 50
                FOR UPDATE SKIP LOCKED
              )
              UPDATE ""EmailQueues"" q
              SET ""Status"" = 'Sending', ""UpdatedAt"" = {now}, ""SendStartedAt"" = NULL, ""ClaimToken"" = {claimToken}
              FROM claimed
              WHERE q.""Id"" = claimed.""Id""
              RETURNING q.""Id""").ToListAsync(ct);
        if (claimedIds.Count == 0) return 0;

        // แถวที่ context นี้ติดตามอยู่ก่อนแล้ว (สถานะในหน่วยความจำยังเป็น "Pending") ต้องปล่อยก่อนโหลด — ไม่งั้น EF คืนตัวเก่า (identity resolution)
        // แล้ว DeferOrFail ตั้ง "Pending" = ค่าเดิมในหน่วยความจำ ⇒ ไม่เขียนลงฐาน ⇒ แถวค้าง "Sending" ตลอดไป (ไม่ถูกส่งอีก) · CI 2026-10-14
        foreach (var tracked in _db.ChangeTracker.Entries<EmailQueue>().Where(e => claimedIds.Contains(e.Entity.Id)).ToList())
            tracked.State = EntityState.Detached;

        var batch = await _db.EmailQueues
            .Where(q => claimedIds.Contains(q.Id))
            .ToListAsync(ct);

        int sent = 0;
        foreach (var item in batch)
        {
            // รอบ 254 ฝ่ายค้าน M1(ข): แถวก่อนหน้าบันทึกล้มแล้วล้าง context ⇒ แถวที่เหลือของแบตช์หลุดการติดตาม (แก้แล้วไม่ถูกบันทึก ค้าง "กำลังส่ง") ⇒ ผูกกลับก่อนแตะ
            if (_db.Entry(item).State == EntityState.Detached) _db.EmailQueues.Attach(item);
            // รอบ 258 (B-H2): ประทับเวลาก่อนส่งแถวนี้ (เส้นแบ่ง "ค้าง" นับจากแถว ไม่ใช่จากตอนจองทั้งแบตช์) แบบมีเงื่อนไข —
            // แถวที่ตัวกู้ประทับล้มไปแล้ว/ถูกยกเลิก (แบตช์ช้าเกินเส้นแบ่ง) ไม่ถูกส่ง: คนอาจกดลองใหม่ไปแล้ว = ส่งซ้ำ
            var heartbeatId = item.Id;
            var stillMine = await _db.EmailQueues
                // รอบ 258 ฝ่ายค้าน R-M3: เงื่อนไขเจ้าของ (โทเคนของรอบจองนี้) — แถวที่ถูกกู้แล้วเครื่องอื่นจองใหม่ยังเป็น Sending แต่ไม่ใช่ของเรา ⇒ ไม่ส่ง ·
                // ประทับ SendStartedAt = "เริ่มส่งแล้ว" (ตัวกู้ใช้แยกแถวที่ไม่เคยลองส่งออกจากแถวที่ไม่รู้ผล)
                .Where(q => q.Id == heartbeatId && q.Status == EmailQueueStaleSending.StatusSending && q.ClaimToken == claimToken)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.UpdatedAt, (DateTime?)DateTime.UtcNow)
                    .SetProperty(q => q.SendStartedAt, (DateTime?)DateTime.UtcNow), ct);
            if (stillMine != 1) { _db.Entry(item).State = EntityState.Detached; continue; }
            try
            {
                // ID#12: เอกสารเปลี่ยนไปหลังเข้าคิว (ยกเลิก · กลับเป็นร่าง · ชำระครบ) ⇒ ข้ามพร้อมเหตุผลบนคิว — ไม่ส่งใบที่ไม่มีผลแล้วถึงลูกค้า
                // ฝ่ายค้าน F4: 50 ทวิ ที่ถูกยกเลิกหลังเข้าคิวก็ต้องไม่ถูกส่ง (ไฟล์แนบเป็นใบที่ไม่มีผลแล้ว) — คลาสเดียวกัน
                var skipReason = item.EntityType == "Document" ? await RecheckDocumentAtSendAsync(item, ct)
                    : item.EntityType == "WithholdingTaxCert" ? await RecheckWhtCertAtSendAsync(item, ct)
                    // รอบ 253 (WP-10): อีเมลทวง 50 ทวิ — บริษัทปิดทวง/ผู้ติดต่อไม่ส่งอัตโนมัติ/ลูกค้าส่งใบครบแล้ว ⇒ ข้ามพร้อมเหตุ
                    : item.EntityType == Accounting.Helpers.WhtCertReminderCadence.QueueEntityType
                        ? await WhtCertReminderService.SendTimeSkipReasonAsync(_db, _recipients, item, ct)
                    // รอบ 255 ฝ่ายค้าน M6: ลิงก์วิเศษพอร์ทัล — สิทธิ์ถูกปิด/คำขอค้างนานเกิน ⇒ ข้ามพร้อมเหตุ
                    : item.EntityType == Accounting.Helpers.PortalMagicLinkPolicy.QueueEntityType
                        ? await PortalService.MagicLinkSkipReasonAsync(_db, item, ct)
                    // รอบ 259-3 ฝ่ายค้าน M3: คำเชิญพอร์ทัลผู้ขาย — ลิงก์ต้นทางถูกเพิกถอน/หมดอายุระหว่างรอ ⇒ ข้ามพร้อมเหตุ
                    : item.EntityType == Accounting.Helpers.VendorPortalTokenRules.QueueEntityType
                        ? await Accounting.Services.Implementations.Portal.VendorPortalService.InviteSkipReasonAsync(_db, item, ct)
                    : null;
                if (skipReason != null)
                {
                    item.Status = ScheduledEmailSendCheck.SkippedStatus;
                    item.ErrorMessage = skipReason;
                    item.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                // รอบ 244: ใบกำกับของบริษัทโหมด e-Tax by Email ⇒ ทาง e-Tax เท่านั้น (EtaxDeliveryRouter ตัวเดียว) — ด่านปฏิเสธ = คิว Failed พร้อมเหตุ ·
                // ห้ามตกไปเส้นอีเมลธรรมดาข้างล่าง (ลูกค้าจะได้ PDF/A-3 ที่ไม่มีวันถูกประทับเวลา)
                if (item.EntityType == "Document" && await SendViaEtaxIfRequiredAsync(item, ct) is bool etaxSent)
                {
                    if (etaxSent) sent++;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }

                // รอบ 241 ฝ่ายค้าน B2 (คลาสเดียวกับ 240 B1): ผู้ส่งจากตัวตัดสินเดียว — บริษัทตั้ง+ทดสอบผ่าน ⇒ SMTP บริษัท (พฤติกรรมเดิม) ·
                // ไม่งั้น ⇒ SMTP ของ NextAcc ในนามบริษัท = เพดาน (บริษัท/วัน · ผู้รับ · To+Cc+Bcc ≤ 5) + เทมเพลตคงที่ (ข้อความผู้ใช้เป็นข้อความล้วน)
                var compose = await ComposeQueueMessageAsync(item, ct);
                if (compose.Refusal != null)
                {
                    // เกินเพดาน/ผู้รับไม่ถูกต้อง ⇒ ไม่เรียกผู้ให้บริการ · ล้มพร้อมเหตุผลบนคิว + บันทึกการส่ง (กด "ลองใหม่" ได้)
                    item.Status = "Failed";
                    item.ErrorMessage = compose.Refusal;
                    item.UpdatedAt = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    continue;
                }
                var (msg, sender, log) = (compose.Message!, compose.Sender!, compose.Log);

                if (item.AttachPdf)
                {
                    // ID#12: สร้างไฟล์แนบไม่สำเร็จ ⇒ ไม่ส่งอีเมลเปล่าแล้วประทับ "Sent" (เดิมกลืน error เป็น LogWarning) — ลองใหม่/ล้มพร้อมเหตุผล
                    EmailAttachment? att;
                    try { att = await BuildAttachmentAsync(item, ct); }
                    catch (Exception attEx) when (attEx is not OperationCanceledException)
                    {
                        DeferOrFail(item, ScheduledEmailSendCheck.AttachmentFailure(attEx.Message, willRetry: item.RetryCount < 3));
                        _logger.LogWarning(attEx, "EmailQueue attachment build failed Id={Id}", item.Id);
                        await _db.SaveChangesAsync(ct);
                        continue;
                    }
                    if (att != null) msg.Attachments.Add(att);
                }

                // รอบ 254 WP-11 L4 (Q4): เส้นแพลตฟอร์ม (log != null) ตัดโควตาอีเมลรายเดือน — เต็ม ⇒ ล้มพร้อมเหตุบนคิว (ซื้อแพ็กแล้วกด "ลองใหม่") ไม่เรียกผู้ให้บริการ ·
                // ส่งล้ม/โยน ⇒ คืนหน่วย
                NotifyReservation? monthly = null;
                if (log != null && _notifyQuota != null)
                {
                    monthly = await _notifyQuota.ReservePlatformEmailAsync(item.CompanyId, ct);
                    if (!monthly.Ok)
                    {
                        var full = NotifyQuotaPolicy.EmailMonthlyFullReason(monthly.Limit);
                        item.Status = "Failed";
                        item.ErrorMessage = full;
                        item.UpdatedAt = DateTime.UtcNow;
                        log.Status = EmailLogStatus.Failed;
                        log.ErrorMessage = full;
                        _db.DocumentEmailLogs.Add(log);
                        await _db.SaveChangesAsync(ct);
                        continue;
                    }
                }
                EmailSendResult result;
                try { result = await sender.SendAsync(msg, ct); }
                catch (Exception) when (monthly != null)
                {
                    // ฝ่ายค้าน fix2 L8: รวมการยกเลิก (OperationCanceledException) — คืนหน่วยด้วย token ที่ยังไม่ถูกยกเลิก
                    await _notifyQuota!.ReleaseAsync(item.CompanyId, monthly, CancellationToken.None);
                    throw;
                }
                if (monthly != null)
                {
                    if (result.Success) await _notifyQuota!.AlertIfCrossedAsync(item.CompanyId, monthly, ct);
                    else await _notifyQuota!.ReleaseAsync(item.CompanyId, monthly, ct);
                }
                if (log != null)
                {
                    // เส้นแพลตฟอร์ม: ทุกฉบับลงบันทึก (ฐานนับเพดานรายวัน PlatformEmailQuota) · เนื้อในบันทึกคง {PayLink} (ไม่มี token ตรง — ฝ่ายค้าน S1)
                    log.Status = result.Success ? EmailLogStatus.Sent : EmailLogStatus.Failed;
                    log.SentAt = result.Success ? DateTime.UtcNow : null;
                    log.ErrorMessage = result.ErrorMessage;
                    log.ProviderMessageId = result.MessageId;
                    _db.DocumentEmailLogs.Add(log);
                }
                if (result.Success)
                {
                    item.Status = "Sent";
                    item.SentAt = DateTime.UtcNow;
                    item.ErrorMessage = AfterSentNote(item.ErrorMessage);
                    sent++;
                }
                else
                {
                    DeferOrFail(item, result.ErrorMessage);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DeferOrFail(item, ex.Message);
                _logger.LogWarning(ex, "EmailQueue send failed Id={Id}", item.Id);
            }
            // ฝ่ายค้าน MED-3: บันทึกผลของแถวนี้ล้ม (เช่น ค่ายาวเกินคอลัมน์) ⇒ ห้ามลากทั้งแบตช์ (แถวที่จองไว้ค้าง "Sending" ตลอดไป) —
            // ทิ้งการเปลี่ยนที่ค้าง แล้วประทับแถวนี้ Failed พร้อมเหตุด้วยคำสั่งตรง (ล้มดังบนคิว) · แถวถัดไปเดินต่อ
            try { await _db.SaveChangesAsync(ct); }
            catch (Exception saveEx) when (saveEx is not OperationCanceledException)
            {
                _logger.LogError(saveEx, "EmailQueue save failed Id={Id}", item.Id);
                // รอบ 254 ฝ่ายค้าน M1(ก): ผู้ให้บริการรับอีเมลไปแล้ว ⇒ ประทับ "ส่งแล้ว" (ไม่ใช่ล้ม — กดลองใหม่ = ลูกค้าได้สองฉบับ) · ตัวตัดสินเดียว SaveFailureOutcome
                var outcome = SaveFailureOutcome.For(item.Status, item.SentAt, null, saveEx.GetBaseException().Message);
                _db.ChangeTracker.Clear();
                var itemId = item.Id;
                await _db.EmailQueues.Where(q => q.Id == itemId)
                    .ExecuteUpdateAsync(x => x.SetProperty(q => q.Status, outcome.Status).SetProperty(q => q.ErrorMessage, outcome.Note)
                        .SetProperty(q => q.SentAt, outcome.SentAt).SetProperty(q => q.UpdatedAt, (DateTime?)DateTime.UtcNow), ct);
            }
        }
        return sent;
    }

    /// <summary>
    /// รอบ 244 — ใบในคิวที่ต้องส่งแบบ e-Tax by Email (<see cref="DocumentEmailService.RouteForAsync"/> → <c>EtaxDeliveryRouter</c>) ส่งผ่าน
    /// <c>IDocumentEmailService.SendDocumentRoutedAsync</c> (ด่านผู้ส่ง/รูปแบบ/สถานะ + หัวเรื่องตามกรมสรรพากร + CC ETDA) ·
    /// คืน null = ใบนี้ไม่ใช่ทาง e-Tax (ผู้เรียกเดินเส้นเดิม) · true = ส่งแล้ว · false = ไม่ส่ง (คิว Failed พร้อมเหตุ / ผู้ให้บริการล้ม ⇒ ลองใหม่)
    /// <para>ด่านปฏิเสธ (ค่าตั้งผู้ส่ง · CC/BCC ของตาราง · ไม่แนบ PDF · ใบยกเลิก ฯลฯ) ลองใหม่ไม่ช่วย ⇒ Failed ทันที (กด "ลองใหม่" หลังแก้ได้) ·
    /// ข้อความในคิว: <c>{PayLink}</c> แทนด้วย URL ตอนส่ง (ไม่เก็บ token) · หัวเรื่องของตารางไม่ถูกใช้ (หัวเรื่องตามกรมสรรพากร)</para>
    /// </summary>
    internal async Task<bool?> SendViaEtaxIfRequiredAsync(EmailQueue item, CancellationToken ct)
    {
        var route = await DocumentEmailService.RouteForAsync(_db, item.CompanyId, item.EntityId, ct);
        if (route.Channel != EtaxEmailChannel.EtaxByEmail) return null;
        if (_docEmail == null)
        {
            item.Status = "Failed";
            item.ErrorMessage = "ใบนี้ต้องส่งแบบ e-Tax by Email แต่บริการส่ง e-Tax ไม่พร้อม — ยังไม่ส่ง (ไม่ส่งเป็นอีเมลธรรมดาแทน)";
            item.UpdatedAt = DateTime.UtcNow;
            return false;
        }
        string? payUrl = null;
        if (_payLinks != null && DocumentPayLinkPolicy.HasPlaceholder(item.Body))
            payUrl = await _payLinks.CurrentUrlAsync(item.CompanyId, item.EntityId, ct);
        var body = DocumentPayLinkPolicy.ResolvePlaceholder(item.Body, payUrl, html: true);
        try
        {
            var log = await _docEmail.SendDocumentRoutedAsync(item.CompanyId, item.EntityId,
                new Models.DTOs.Email.SendDocumentEmailRequest(item.ToEmail, item.CcEmail, item.BccEmail, null, body, AttachPdf: item.AttachPdf),
                Accounting.Helpers.SystemActor.EmailSchedule);   // รอบ 259 (B-L4)
            if (log.Status == EmailLogStatus.Sent)
            {
                item.Status = "Sent";
                item.SentAt = DateTime.UtcNow;
                item.ErrorMessage = AfterSentNote(item.ErrorMessage);
                item.UpdatedAt = DateTime.UtcNow;
                return true;
            }
            DeferOrFail(item, log.ErrorMessage);
            return false;
        }
        catch (BusinessRuleException ex) when (ex.RuleCode is { } rc && rc.StartsWith("ETAX-", StringComparison.Ordinal))
        {
            // แถว e-Tax ที่ถูก Add ค้างจากการออกที่ล้ม ห้ามค้างใน context ของรอบคิวนี้
            foreach (var e in _db.ChangeTracker.Entries<EtaxInvoice>().Where(e => e.State == EntityState.Added).ToList())
                e.State = EntityState.Detached;
            item.Status = "Failed";
            var reason = "ส่ง e-Tax by Email ไม่ได้ (ยังไม่ส่ง): " + ex.Message;
            item.ErrorMessage = reason.Length > 2000 ? reason[..2000] : reason;
            item.UpdatedAt = DateTime.UtcNow;
            return false;
        }
    }

    internal sealed record QueueCompose(EmailMessage? Message, IEmailSender? Sender, DocumentEmailLog? Log, string? Refusal);

    /// <summary>
    /// รอบ 241 ฝ่ายค้าน B2 — ประกอบอีเมลของคิวตามผู้ส่งจริง (ตัวตัดสินเดียว <see cref="DocumentEmailService.ResolveOnBehalfSenderAsync"/>):
    /// <list type="bullet">
    /// <item>SMTP ของบริษัท (ตั้ง+ทดสอบผ่าน) ⇒ เนื้อเดิมของคิว (พฤติกรรมเดิม) · ไม่ลงบันทึกเพิ่ม</item>
    /// <item>SMTP ของ NextAcc ในนามบริษัท ⇒ ผู้รับรวม To+Cc+Bcc ≤ 5 · เพดานบริษัท/วัน + ต่อผู้รับ (<see cref="PlatformEmailQuota"/>) ก่อนเรียกผู้ให้บริการ ·
    ///   เนื้อ = เทมเพลตคงที่ (<see cref="SignRequestEmailText.WrapPlatformDocumentEmail"/>) — เทมเพลตที่ผู้ใช้เขียนเอง = ข้อความล้วน (HTML ถูกหนี) ·
    ///   ค่าตั้งต้นของเซิร์ฟเวอร์ + บล็อกลิงก์ชำระคงเป็น HTML · From = แพลตฟอร์ม · Reply-To = บริษัท · คืนบันทึก (ผู้เรียกเติมผลแล้วเก็บ)</item>
    /// </list>
    /// ลิงก์ชำระ: คิวเก็บ <c>{PayLink}</c> · URL ประกอบตอนนี้จาก token ฉบับเข้ารหัส (ฝ่ายค้าน S1) ใส่เฉพาะข้อความที่ส่ง ไม่ถูกเก็บลงคิว/บันทึก
    /// </summary>
    internal async Task<QueueCompose> ComposeQueueMessageAsync(EmailQueue item, CancellationToken ct)
    {
        var settings = await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(s => s.CompanyId == item.CompanyId, ct);
        var (plan, sender) = await DocumentEmailService.ResolveOnBehalfSenderAsync(_db, _senderFactory, item.CompanyId, settings, ct);

        string? payUrl = null;
        if (item.EntityType == "Document" && _payLinks != null
            && (DocumentPayLinkPolicy.HasPlaceholder(item.Body) || DocumentPayLinkPolicy.HasPlaceholder(item.Subject)))
            payUrl = await _payLinks.CurrentUrlAsync(item.CompanyId, item.EntityId, ct);

        var subject = item.Subject;
        var body = item.Body;
        // รอบ 250 WP-08: ToEmail อาจมีหลายผู้รับ (ปลายทางของผู้ติดต่อ · คั่น ",") — แยกเป็นรายตัว (Graph/Gmail ไม่รับสตริงรวม)
        var msg = new EmailMessage();
        msg.To.AddRange(SignEmailThrottle.ParseRecipients(new[] { item.ToEmail }).Valid);
        if (msg.To.Count == 0) msg.To.Add(item.ToEmail);
        if (!string.IsNullOrWhiteSpace(item.CcEmail)) msg.Cc.Add(item.CcEmail);
        if (!string.IsNullOrWhiteSpace(item.BccEmail))
            foreach (var b in item.BccEmail.Split(',', StringSplitOptions.RemoveEmptyEntries))
                msg.Bcc.Add(b.Trim());

        DocumentEmailLog? log = null;
        if (plan.OnBehalf)
        {
            var (valid, invalid) = SignEmailThrottle.ParseRecipients(new[] { item.ToEmail, item.CcEmail, item.BccEmail });
            var refusal = invalid.Count > 0
                ? $"อีเมลผู้รับไม่ถูกต้อง: {string.Join(", ", invalid.Take(3))}"
                : SignEmailThrottle.RecipientCountRefusal(valid.Count);   // BCC นับรวม (ฝ่ายค้าน B2)
            if (refusal == null)
            {
                var quota = await PlatformEmailQuota.ForCompanyAsync(_db, _config, item.CompanyId, DateTime.UtcNow, ct);
                foreach (var r in valid)
                {
                    var (sameDoc, any) = await PlatformEmailQuota.RecipientCountsAsync(_db, item.CompanyId, item.EntityId, r, quota.DayStartUtc, ct);
                    refusal = SignEmailThrottle.Refusal(true, quota.SentToday, quota.Cap, sameDoc, any);
                    if (refusal != null) break;
                }
            }
            log = new DocumentEmailLog
            {
                CompanyId = item.CompanyId,
                DocumentId = item.EntityType == "Document" ? item.EntityId : null,
                ToEmail = item.ToEmail, CcEmail = item.CcEmail, BccEmail = item.BccEmail,
                AttachedPdf = item.AttachPdf, Provider = sender.Provider, SenderMode = plan.Mode,
                Purpose = "scheduled", CreatedBy = Accounting.Helpers.SystemActor.EmailSchedule,
            };
            if (refusal != null)
            {
                log.Subject = SignRequestEmailText.SafeSubject(subject);
                log.Body = "";
                log.Status = EmailLogStatus.Failed;
                log.ErrorMessage = refusal;
                _db.DocumentEmailLogs.Add(log);
                return new QueueCompose(null, null, null, refusal);
            }

            // เทมเพลตของผู้ใช้ = ข้อความล้วน (ค่าที่แทนถูกหนีตอนเข้าคิวแล้ว ⇒ ถอดก่อน แล้วหนีทั้งก้อนครั้งเดียว) · บล็อกลิงก์ชำระของเซิร์ฟเวอร์แยกออกมาคงเป็น HTML
            var userAuthored = item.RuleId is Guid rid && await _db.EmailScheduleRules.AsNoTracking()
                .AnyAsync(r => r.Id == rid && r.CompanyId == item.CompanyId && r.BodyTemplate != null && r.BodyTemplate != "", ct);
            var (rest, block) = DocumentPayLinkPolicy.SplitBlock(body);
            var inner = (userAuthored ? SignRequestEmailText.PlainTextToHtml(System.Net.WebUtility.HtmlDecode(rest)) : rest) + (block ?? "");
            var isEn = false;
            var title = subject;
            string? number = null;
            if (item.EntityType == "Document")
            {
                var heading = await HeadingForAsync(item.CompanyId, item.EntityId);
                isEn = heading.IsEnglish;
                title = heading.Title;
                number = await _db.Documents.AsNoTracking().Where(d => d.Id == item.EntityId && d.CompanyId == item.CompanyId)
                    .Select(d => d.DocumentNumber).FirstOrDefaultAsync(ct);
            }
            var co = await _db.Companies.AsNoTracking().Where(c => c.Id == item.CompanyId).Select(c => new { c.Name, c.TaxId }).FirstOrDefaultAsync(ct);
            var origin = await SystemBaseUrl.ResolveAsync(_db, _config, ct);
            body = SignRequestEmailText.WrapPlatformDocumentEmail(isEn, co?.Name, co?.TaxId, plan.ReplyTo, title, number, inner,
                origin == null ? null : $"{origin}/contact.html?topic=email-abuse");
            subject = SignRequestEmailText.SafeSubject(subject);
            msg.FromAddress = plan.FromAddress;
            msg.FromName = plan.FromName;
            msg.ReplyTo = plan.ReplyTo;
            log.Subject = subject;
            log.Body = body;   // ยังเป็น {PayLink} — ไม่เก็บ token
        }
        msg.Subject = DocumentPayLinkPolicy.ResolvePlaceholder(subject, payUrl, html: false);
        msg.HtmlBody = DocumentPayLinkPolicy.ResolvePlaceholder(body, payUrl, html: true);
        // รอบ 253 (WP-10): อีเมลทวง 50 ทวิ — ลิงก์อัปโหลดออกตอนส่ง (คิว/บันทึกเก็บที่หมาย ไม่เก็บ token) · ไม่มีที่อยู่เว็บของระบบ ⇒ ไม่ส่ง (ไม่ส่งลิงก์เสีย)
        if (item.EntityType == Accounting.Helpers.WhtCertReminderCadence.QueueEntityType
            && msg.HtmlBody.Contains(Accounting.Helpers.WhtCertReminderCadence.UploadLinkPlaceholder, StringComparison.Ordinal))
        {
            // ฝ่ายค้าน WP-10 M3: อ่านอย่างเดียว (แถวลิงก์สร้างพร้อมแถวคิว · token คำนวณซ้ำได้) — ไม่ SaveChanges บน _db กลางทางประกอบ · ลองใหม่ = URL เดิม
            var (linkUrl, linkProblem) = await WhtCertUploadLinkService.ReminderUrlAsync(_db, _config, item.CompanyId, item.Id, ct);
            if (linkUrl == null) return new QueueCompose(null, null, null, linkProblem ?? "สร้างลิงก์อัปโหลดไม่ได้");
            msg.HtmlBody = msg.HtmlBody.Replace(Accounting.Helpers.WhtCertReminderCadence.UploadLinkPlaceholder,
                System.Net.WebUtility.HtmlEncode(linkUrl), StringComparison.Ordinal);
        }
        // รอบ 255 ฝ่ายค้าน M6: ลิงก์วิเศษพอร์ทัล — โทเคนออกตอนส่ง (คิว/บันทึกเก็บที่หมาย ไม่เก็บ token) · ไม่มีที่อยู่เว็บ ⇒ ไม่ส่ง
        if (item.EntityType == Accounting.Helpers.PortalMagicLinkPolicy.QueueEntityType
            && msg.HtmlBody.Contains(Accounting.Helpers.PortalMagicLinkPolicy.LinkPlaceholder, StringComparison.Ordinal))
        {
            var magic = await PortalService.IssueMagicLinkUrlAsync(_db, _config, item.CompanyId, item.EntityId, ct);
            if (magic.Url == null) return new QueueCompose(null, null, null, magic.Problem ?? "สร้างลิงก์เข้าพอร์ทัลไม่ได้");
            msg.HtmlBody = msg.HtmlBody.Replace(Accounting.Helpers.PortalMagicLinkPolicy.LinkPlaceholder,
                System.Net.WebUtility.HtmlEncode(magic.Url), StringComparison.Ordinal);
        }
        // รอบ 259-3 ฝ่ายค้าน M3: คำเชิญพอร์ทัลผู้ขาย — token ออกตอนส่ง (คิว/บันทึกเก็บที่หมาย ไม่เก็บ token ดิบ) · ไม่มีที่อยู่เว็บ ⇒ ไม่ส่ง
        if (item.EntityType == Accounting.Helpers.VendorPortalTokenRules.QueueEntityType
            && msg.HtmlBody.Contains(Accounting.Helpers.VendorPortalTokenRules.LinkPlaceholder, StringComparison.Ordinal))
        {
            var invite = await Accounting.Services.Implementations.Portal.VendorPortalService.IssueInviteUrlAsync(_db, _config, item, ct);
            if (invite.Url == null) return new QueueCompose(null, null, null, invite.Problem ?? "สร้างลิงก์พอร์ทัลผู้ขายไม่ได้");
            msg.HtmlBody = msg.HtmlBody.Replace(Accounting.Helpers.VendorPortalTokenRules.LinkPlaceholder,
                System.Net.WebUtility.HtmlEncode(invite.Url), StringComparison.Ordinal);
        }
        return new QueueCompose(msg, sender, log, null);
    }

    /// <summary>ไฟล์แนบของคิว — <b>โยน</b>เมื่อสร้างไม่สำเร็จ (ผู้เรียกเลื่อนไปลองใหม่ ไม่ส่งอีเมลเปล่า · ID#12) ·
    /// คืน null เฉพาะชนิดที่ยังไม่มีตัวสร้างไฟล์ (สลิป/50 ทวิรายปี — ตั้งใจส่งเนื้ออีเมลอย่างเดียว)</summary>
    private async Task<EmailAttachment?> BuildAttachmentAsync(EmailQueue item, CancellationToken ct)
    {
        if (item.EntityType == "Document")
        {
            var resp = await _pdf.GenerateDocumentPdfAsync(item.CompanyId,
                new Models.DTOs.DocumentTemplate.GeneratePdfRequest(item.EntityId, null, null, null, null));
            if (resp?.PdfData is not { Length: > 0 })
                throw new InvalidOperationException("ไฟล์ PDF ที่สร้างได้ว่างเปล่า");
            return new EmailAttachment { FileName = resp.FileName, ContentType = "application/pdf", Content = resp.PdfData };
        }
        if (item.EntityType == "WithholdingTaxCert")
        {
            var resp = await _pdf.GenerateWithholdingTaxCertPdfAsync(item.CompanyId, item.EntityId);
            if (resp?.PdfData is not { Length: > 0 })
                throw new InvalidOperationException("ไฟล์ PDF ที่สร้างได้ว่างเปล่า");
            return new EmailAttachment { FileName = resp.FileName, ContentType = "application/pdf", Content = resp.PdfData };
        }
        // Payslip / annual WHT cert ผ่าน payroll service — ทำใน
        // path future expansion. ส่งไม่มีไฟล์แนบดีกว่า fail ทั้งฉบับ.
        return null;
    }

    /// <summary>กุญแจกันซ้ำของคิวอีเมลเอกสาร — ตัวเดียวของฝั่งเขียน (EnqueueDocumentAsync) และฝั่งตรวจ "กฎเมื่ออนุมัติเข้าคิวจริงแล้วไหม" (ฝ่ายค้าน WP-02 ข้อ 6)</summary>
    internal static string DocumentQueueKey(Guid documentId, string trigger, string suffix) => $"doc:{documentId}:{trigger}:{suffix}";

    /// <summary>รอบ 250 WP-08: ผลเข้าคิวของใบหนึ่งตามกฎหนึ่ง — <see cref="NotSent"/> = ผู้ติดต่อไม่รับเรื่องนี้ (เขียนแถว "ข้าม" พร้อมเหตุแล้ว)</summary>
    internal sealed record DocQueueResult(bool Queued, bool Duplicate, string? NotSent);

    /// <summary>รอบ 254 WP-11 L2: ผลเข้าคิว LINE ของใบหนึ่ง — <paramref name="FallbackToEmail"/> = ไม่มีปลายทาง LINE ⇒ ส่งอีเมลแทน (Q7)</summary>
    internal sealed record LineEnqueueResult(int Queued, bool Duplicate, bool FallbackToEmail, string? NotSent)
    {
        public static LineEnqueueResult None { get; } = new(0, false, false, null);
    }

    private async Task<DocQueueResult> EnqueueDocumentAsync(EmailScheduleRule rule, Document doc,
        int daysOffset, string suffix, CancellationToken ct, DateTime? overrideSendUtc = null, bool recordNotSent = true, bool forceEmail = false)
    {
        var idem = DocumentQueueKey(doc.Id, rule.Trigger, suffix);
        // รอบ 254 WP-11 L2 (DESIGN §3.d/§3.e · Q7): ช่องทางต่อกฎ — เข้าคิว LINE ก่อน (ปลายทางจากตัวหาผู้รับเดียว) แล้วตัดสินว่าอีเมลยังต้องส่งไหม
        // (ติ๊กอีเมล · หรือถอยมาจาก LINE เพราะไม่มีปลายทาง) · forceEmail = เส้นถอยไปอีเมลตอนส่ง (ไม่เข้าคิว LINE ซ้ำ)
        // ฝ่ายค้าน M3: ใบที่ต้องส่งแบบ e-Tax by Email ⇒ เส้นอีเมลเดินเสมอแม้กฎติ๊ก LINE อย่างเดียว (การ์ด LINE ไม่ใช่การส่งใบกำกับภาษีอิเล็กทรอนิกส์ ·
        // ปิดอีเมล = ใบกำกับไม่ถึงผู้ซื้อตามกฎหมาย) · ถามเส้น e-Tax เฉพาะเมื่ออีเมลอาจไม่ถูกส่ง (ไม่เพิ่มคำค้นให้กฎเดิม · ถามครั้งเดียวต่อใบ)
        bool? etaxRoute = null;
        async Task<bool> EtaxByEmailAsync() => etaxRoute ??= (await DocumentEmailService.RouteForAsync(_db, doc.CompanyId, doc.Id, ct)).Channel == EtaxEmailChannel.EtaxByEmail;
        // รอบ 257 ชุด 4 (C-M3): อีเมลถูกบังคับด้วย e-Tax by Email อยู่แล้ว ⇒ แถว LINE ห้ามถอยไปอีเมลตอนล้ม (เดิม FallbackToEmail=true ⇒ ลูกค้าได้อีเมลสองฉบับ + นับโควตาสองครั้ง)
        var etaxForcesEmail = !forceEmail && ContactLineChannel.LineFallbackMayDuplicate(rule.ChannelEmail, rule.LineFallbackToEmail) && await EtaxByEmailAsync();
        var line = forceEmail ? LineEnqueueResult.None
            : await EnqueueLineAsync(rule, doc, idem, overrideSendUtc ?? ScheduledUtc(BangkokNow().Date.AddDays(daysOffset), rule.SendAtHour), recordNotSent, ct,
                etaxForcesEmail);
        DocQueueResult Merge(DocQueueResult r) => line.Queued > 0 ? r with { Queued = true, NotSent = null } : r;
        var etaxForced = !ContactLineChannel.EmailWanted(rule.ChannelEmail, line.FallbackToEmail, forceEmail) && await EtaxByEmailAsync();
        if (!ContactLineChannel.EmailWanted(rule.ChannelEmail, line.FallbackToEmail, forceEmail, etaxByEmail: etaxForced))
            return new DocQueueResult(line.Queued > 0, line.Duplicate, line.Queued > 0 ? null : line.NotSent);

        // ฝ่ายค้าน LOW: กันซ้ำก่อน (สแกนรายวันไม่ต้องหาผู้รับของใบที่เข้าคิวแล้ว)
        if (await _db.EmailQueues.AnyAsync(q => q.CompanyId == doc.CompanyId
            && q.IdempotencyKey == idem && !q.IsDeleted, ct)) return Merge(new DocQueueResult(false, true, null));

        // รอบ 250 WP-08 (L1 ต่อสาย · DESIGN §3.e): ผู้รับจากตัวหาผู้รับเดียว — ปลายทางของผู้ติดต่อที่รับเรื่องของ trigger นี้ (ไม่ตั้ง = Contact.Email เดิมทุกเรื่อง) ·
        // ไม่มีอีเมลเลย ⇒ ไม่เข้าคิว (เดิม) · มีอีเมลแต่ไม่รับเรื่องนี้/ผู้ติดต่อถูกลบ ⇒ แถว "ข้าม" พร้อมเหตุ
        var delivery = await EmailForAsync(doc.CompanyId, doc.ContactId, NotifyTopics.ForTrigger(rule.Trigger), ct);
        // คำตัดสิน 250-29: ผู้ติดต่อ "ไม่ส่งอัตโนมัติ" ชนะกฎบริษัททุก trigger ของตารางส่ง (ตัวตัดสินเดียว AutopilotSettingsResolver.AutoSendBlock)
        var notSentReason = AutopilotSettingsResolver.AutoSendBlock(await ContactAutoSendOffAsync(doc.CompanyId, doc.ContactId, ct)) ?? delivery.OptOutReason;
        if (!delivery.Deliverable && notSentReason == null) return Merge(new DocQueueResult(false, false, null));
        // ฝ่ายค้าน HIGH-1: ใบที่ต้องไปทาง e-Tax by Email ⇒ ถึงอีเมลหลักเท่านั้น (ไม่มีสำเนา) · อีเมลเพิ่มเติมบันทึกว่าไม่ได้ส่ง (ตัวตัดสิน ForChannel)
        ContactRecipientRule.ChannelRecipients? channel = null;
        if (notSentReason == null)
        {
            var etax = (await DocumentEmailService.RouteForAsync(_db, doc.CompanyId, doc.Id, ct)).Channel == EtaxEmailChannel.EtaxByEmail;
            channel = ContactRecipientRule.ForChannel(delivery, etax);
            notSentReason = channel.Problem;
        }

        var sendUtc = overrideSendUtc ?? ScheduledUtc(BangkokNow().Date.AddDays(daysOffset), rule.SendAtHour);
        // ภาษา + หัวเอกสารของเนื้ออีเมล default = ตัวกลางตัวเดียวกับ PDF ที่แนบ (S-12 รอบ 193) — เดิมคำนวณ
        // `doc ?? company` เอง (ข้ามภาษาของเทมเพลต) และหัวเรื่องเขียนแค่ "เอกสาร {DocNumber}"
        // (rule ที่ผู้ใช้เขียน template เองไม่ถูกแตะ — ได้ placeholder {DocTitle} เพิ่มให้ใช้)
        // รอบ 201 ทีม PL (A-PL9 · P-3 N+1): ค่าระดับบริษัทโหลดครั้งเดียวต่อบริษัท · หัวของใบเดียวกันคำนวณครั้งเดียวต่อรอบ
        // (หลาย rule ของใบเดียว = เดิมคำนวณซ้ำทุก rule) — ตัวตัดสินหัวยังเป็น ResolveDocumentHeadingAsync ตัวเดียว
        var heading = await HeadingForAsync(doc.CompanyId, doc.Id);
        var isEn = heading.IsEnglish;
        var ctx = DocumentEmailContext(doc, heading, _headingCompanies[doc.CompanyId].Company);
        if (notSentReason != null && !recordNotSent) return Merge(new DocQueueResult(false, false, null));   // ครั้งซ้ำ: ไม่สะสมแถว "ข้าม"
        if (notSentReason != null)
        {
            ctx["PayLink"] = "";
            AddNotSentRow(doc.CompanyId, rule.Id, "Document", doc.Id,
                Render(rule.SubjectTemplate ?? DefaultDocSubject(rule.Trigger, isEn), ctx), sendUtc, idem, notSentReason);
            return Merge(new DocQueueResult(false, false, notSentReason));
        }
        // รอบ 241: ลิงก์ชำระเงิน (ช่องทางตามตาราง ∩ ที่บริษัทเปิด) — หนึ่งลิงก์ต่อใบ · {PayLink} ในเทมเพลต หรือบล็อกท้ายเนื้อ
        var payLink = await PayLinkFor(rule, doc, ct);
        ctx["PayLink"] = payLink?.Url != null ? DocumentPayLinkPolicy.QueuePlaceholder : "";   // ฝ่ายค้าน S1: ที่หมาย ไม่ใช่ URL
        // ตารางแรกที่ส่งใบนี้ = ค่าทับของงานหลังรับชำระ (ผู้อ่าน: PostPaymentAutomationService.SettingsForAsync)
        if (rule.Id != Guid.Empty && doc.AutoSendRuleId == null)
            await _db.Documents.Where(d => d.Id == doc.Id && d.CompanyId == doc.CompanyId && d.AutoSendRuleId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.AutoSendRuleId, (Guid?)rule.Id), ct);

        // ฝ่ายค้าน MED-3: เพดานผู้รับของอีเมลผ่าน NextAcc + ความยาวช่อง ⇒ ตัดตอนเข้าคิวพร้อมหมายเหตุ (ไม่ปล่อยให้ผู้ส่งปฏิเสธทั้งฉบับ)
        var recipients = await CapRecipientsAsync(doc.CompanyId, channel!.To!, rule.BccEmails, ct);
        _db.EmailQueues.Add(new EmailQueue
        {
            CompanyId = doc.CompanyId,
            // rule เสมือน (Id ว่าง = ไม่ได้บันทึกลง DB เช่น AutoSendEmail ของ
            // recurring) → RuleId ต้องเป็น null ไม่งั้น FK ชี้ไปแถวที่ไม่มีจริง
            RuleId = rule.Id == Guid.Empty ? null : rule.Id,
            EntityType = "Document",
            EntityId = doc.Id,
            ToEmail = recipients.To,
            BccEmail = rule.BccEmails,
            Subject = Render(rule.SubjectTemplate ?? DefaultDocSubject(rule.Trigger, isEn), ctx),
            Body = WithPayLink(Render(rule.BodyTemplate ?? DefaultDocBody(rule.Trigger, isEn), ctx, htmlEncodeValues: true), rule.BodyTemplate, payLink, isEn),
            AttachPdf = true,
            ScheduledFor = sendUtc,
            IdempotencyKey = idem,
            // รอบ 250 (WP-02 §2.4 ข้อ 2): ตั้งให้แนบลิงก์แต่ออกไม่ได้ (เช่น ยังไม่ได้ตั้ง URL ของระบบ) ⇒ ฉบับนี้ส่งได้ แต่หน้าคิวบอกเหตุที่ไม่มีลิงก์ (ไม่เงียบ)
            ErrorMessage = JoinNotes(etaxForced ? ContactLineChannel.EtaxForcedEmailNote : null, channel.Note, recipients.Note, PayLinkSkipNote(payLink)),
        });
        return new DocQueueResult(true, false, null);
    }

    /// <summary>
    /// รอบ 254 WP-11 L2 — เข้าคิว LINE ของใบหนึ่งตามช่องทางที่กฎติ๊ก (DESIGN §3.e): ปลายทางจากตัวหาผู้รับเดียว (<see cref="IContactRecipientResolver.ResolveAsync"/> ·
    /// LINE เปิดเฉพาะปลายทางที่ผูกกับ OA ของบริษัทที่พร้อม — คำตัดสิน N8 · กลุ่มกลางของแอดมินไม่มีวันอยู่ในชุด — Q1(b)) · แผนจาก <see cref="ContactLineChannel.Plan"/> ตัวเดียว ·
    /// หนึ่งแถวต่อปลายทาง (กุญแจ <see cref="ContactLineChannel.QueueKey"/> · retry key คงที่ต่อแถว) · ไม่มีปลายทาง ⇒ แถว "ข้าม" พร้อมเหตุ (ล้มดัง) + ถอยไปอีเมลถ้ากฎตั้งไว้ ·
    /// ไม่เก็บ LINE id ในคิว (อ้าง EndpointId · ถอดรหัสตอนส่ง)
    /// </summary>
    private async Task<LineEnqueueResult> EnqueueLineAsync(EmailScheduleRule rule, Document doc, string baseKey, DateTime sendUtc,
        bool recordNotSent, CancellationToken ct, bool etaxForcesEmail = false)
    {
        if (!ContactLineChannel.WantsLine(rule.ChannelLineUser, rule.ChannelLineGroup) || !ContactLineChannel.SupportsLine(rule.Trigger))
            return LineEnqueueResult.None;
        if (await _db.LineQueues.AnyAsync(q => q.CompanyId == doc.CompanyId && q.BaseKey == baseKey && !q.IsDeleted, ct))
            return new LineEnqueueResult(0, true, false, null);
        // รอบ 259 (259-2-7): แถวของเหตุการณ์เดียวกันที่เพิ่มในรอบนี้แต่ยังไม่บันทึก (สองกฎ trigger/offset เดียวกัน · วันย้อนเก็บที่ได้กุญแจเดียวกัน) ⇒ มีแล้ว
        if (_db.LineQueues.Local.Any(q => q.CompanyId == doc.CompanyId && q.BaseKey == baseKey && !q.IsDeleted))
            return new LineEnqueueResult(0, true, false, null);

        var topic = NotifyTopics.ForTrigger(rule.Trigger);
        var set = await _recipients.ResolveAsync(doc.CompanyId, doc.ContactId, topic, ct);
        var block = AutopilotSettingsResolver.AutoSendBlock(await ContactAutoSendOffAsync(doc.CompanyId, doc.ContactId, ct));
        var plan = ContactLineChannel.Plan(rule.ChannelEmail, rule.ChannelLineUser, rule.ChannelLineGroup, rule.LineFallbackToEmail, set.Options, block);
        var ruleId = rule.Id == Guid.Empty ? (Guid?)null : rule.Id;
        if (plan.Endpoints.Count == 0)
        {
            if (recordNotSent && plan.NotSent != null)
            {
                var noneKey = ContactLineChannel.QueueKey(baseKey, null);
                _db.LineQueues.Add(new LineQueue
                {
                    CompanyId = doc.CompanyId, RuleId = ruleId, EntityType = "Document", EntityId = doc.Id, ContactId = doc.ContactId,
                    EndpointId = null, EndpointKind = "", Topic = topic.ToString(), Trigger = rule.Trigger, ScheduledFor = sendUtc,
                    Status = ContactLineChannel.StatusSkipped, RetryKey = ContactLineChannel.RetryKey(doc.CompanyId, noneKey),
                    IdempotencyKey = noneKey, BaseKey = baseKey, ErrorMessage = plan.NotSent.Length > 2000 ? plan.NotSent[..2000] : plan.NotSent,
                });
            }
            return new LineEnqueueResult(0, false, plan.FallbackNow, plan.NotSent);
        }
        // ถอยไปอีเมลตอนส่ง (ล้มถาวร) — เฉพาะกฎที่ไม่ได้ติ๊กอีเมล (ติ๊กอีเมล = อีเมลออกอยู่แล้ว)
        // รอบ 257 ชุด 4 (C-M3): อีเมลถูกบังคับด้วย e-Tax by Email ⇒ ไม่ถอย (ตัวตัดสินเดียว ContactLineChannel.LineFallbackAtSend)
        var fallbackAtSend = ContactLineChannel.LineFallbackAtSend(rule.ChannelEmail, rule.LineFallbackToEmail, etaxForcesEmail);
        // รอบ 262 ฝ่ายค้าน L1 (คำตัดสิน 262-1-13): ปลายทางที่ได้การ์ดค่าบริการ (บริษัทผู้ให้บริการ) ของใบ+เรื่องเดียวกันแล้ว ⇒ กฎไม่เข้าคิวซ้ำ (ตัวเทียบเดียวกับฝั่งการ์ดค่าบริการ)
        var platformCarded = await PlatformInvoiceQueries.CardedEndpointsAsync(_db, doc.CompanyId, doc.Id, rule.Trigger,
            plan.Endpoints.Select(e => e.EndpointId!.Value).ToList(), fromPlatform: true, ct);
        var queuedNow = 0;
        foreach (var ep in plan.Endpoints)
        {
            if (platformCarded.Contains(ep.EndpointId!.Value)) continue;
            queuedNow++;
            var key = ContactLineChannel.QueueKey(baseKey, ep.EndpointId);
            _db.LineQueues.Add(new LineQueue
            {
                CompanyId = doc.CompanyId, RuleId = ruleId, EntityType = "Document", EntityId = doc.Id, ContactId = doc.ContactId,
                EndpointId = ep.EndpointId, EndpointKind = ep.Kind, Topic = topic.ToString(), Trigger = rule.Trigger, ScheduledFor = sendUtc,
                Status = ContactLineChannel.StatusPending, RetryKey = ContactLineChannel.RetryKey(doc.CompanyId, key),
                IdempotencyKey = key, BaseKey = baseKey, FallbackToEmail = fallbackAtSend,
            });
        }
        return queuedNow == 0 ? new LineEnqueueResult(0, true, false, null) : new LineEnqueueResult(queuedNow, false, false, null);
    }

    public async Task<string?> EnqueueLineFallbackEmailAsync(Guid companyId, Guid? ruleId, Guid documentId, string baseKey, string trigger,
        CancellationToken ct = default)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == documentId && d.CompanyId == companyId && !d.IsDeleted, ct);
        if (doc == null) return "ไม่พบเอกสาร — ไม่ถอยไปอีเมล";
        await _db.HydrateContactAsync(companyId, doc);
        var rule = ruleId is Guid rid
            ? await _db.EmailScheduleRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid && r.CompanyId == companyId && !r.IsDeleted, ct)
            : null;
        // กฎถูกลบหลังเข้าคิว/กฎเสมือนของรายการประจำ ⇒ กฎเสมือน (หัวเรื่อง/เนื้อค่าตั้งต้น · RuleId = null)
        rule ??= new EmailScheduleRule { Id = Guid.Empty, CompanyId = companyId, Trigger = trigger, SendAtHour = 9 };
        var suffix = ContactLineChannel.FallbackSuffix(baseKey, doc.Id, rule.Trigger);
        if (suffix == null) return "กุญแจคิว LINE ไม่ตรงกับเอกสาร — ไม่ถอยไปอีเมล";
        // รอบ 257 ชุด 4 (C-M3 · แถว LINE ที่เข้าคิวก่อนแก้ยังถือ FallbackToEmail=true): อีเมลหลักของใบนี้ (กุญแจเดียวกับ baseKey) เข้าคิวแล้ว
        // (เช่น e-Tax by Email บังคับอีเมล) ⇒ ไม่ถอยซ้ำ — ลูกค้าไม่ได้อีเมลสองฉบับ ไม่นับโควตาซ้ำ
        // ฝ่ายค้าน LOW-2: แถวหลักที่ "ข้าม/ยกเลิก/ล้ม" ไม่ใช่การส่ง ⇒ ตอบเหตุ (ห้ามรายงานว่าเข้าคิวแล้ว) · ไม่ถอยเพิ่ม (ตัวตัดสินเดียว ContactLineChannel.MainEmailFallbackOutcome)
        var mainEmail = await _db.EmailQueues.AsNoTracking()
            .Where(q => q.CompanyId == companyId && q.IdempotencyKey == baseKey && !q.IsDeleted)
            .Select(q => new { q.Status, q.ErrorMessage }).FirstOrDefaultAsync(ct);
        if (mainEmail != null)
            return ContactLineChannel.MainEmailFallbackOutcome(mainEmail.Status, mainEmail.ErrorMessage);
        if (ScheduledEmailSendCheck.RecurringIdFromIdempotencyKey(baseKey) is { } recurringId)
            rule = WithRecurringPayLayer(rule, await RecurringPayLayerAsync(companyId, recurringId, ct));
        var r = await EnqueueDocumentAsync(rule, doc, daysOffset: 0, suffix: suffix, ct, overrideSendUtc: DateTime.UtcNow, recordNotSent: true, forceEmail: true);
        await _db.SaveChangesAsync(ct);
        return r.Queued || r.Duplicate ? null : (r.NotSent ?? "ผู้ติดต่อไม่มีอีเมลที่รับเรื่องนี้ — ถอยไปอีเมลไม่ได้");
    }

    // แคชของรอบนี้ (service เป็น scoped — หนึ่งคำขอ/หนึ่งรอบของ worker) · คีย์บริษัท/เอกสาร ไม่ข้าม tenant
    private readonly Dictionary<Guid, PdfGenerationService.HeadingCompanyContext> _headingCompanies = new();
    private readonly Dictionary<Guid, Models.DTOs.DocumentTemplate.DocumentHeading> _headings = new();

    private async Task<Models.DTOs.DocumentTemplate.DocumentHeading> HeadingForAsync(Guid companyId, Guid documentId)
    {
        if (!_headingCompanies.TryGetValue(companyId, out var shared))
        {
            shared = await PdfGenerationService.LoadHeadingCompanyContextAsync(_db, companyId);
            _headingCompanies[companyId] = shared;
        }
        if (_headings.TryGetValue(documentId, out var cached)) return cached;
        var heading = await PdfGenerationService.ResolveDocumentHeadingAsync(_db, companyId, documentId, shared);
        _headings[documentId] = heading;
        return heading;
    }

    /// <summary>ค่าที่แทนลงหัวเรื่อง/เนื้ออีเมลของเอกสาร — ตัวเดียวของตอนเข้าคิวและตอนส่ง (ID#12: ยอดค้างชำระ ณ เวลาส่ง)</summary>
    private static Dictionary<string, string?> DocumentEmailContext(Document doc,
        Models.DTOs.DocumentTemplate.DocumentHeading heading, Company companyRow)
    {
        var company = (heading.IsEnglish && !string.IsNullOrWhiteSpace(companyRow.NameEn)
            ? companyRow.NameEn : companyRow.Name) ?? "";
        return new Dictionary<string, string?>
        {
            ["DocNumber"] = doc.DocumentNumber,
            ["DocTitle"] = heading.Title,
            ["ContactName"] = doc.Contact?.Name ?? "",
            ["DueDate"] = doc.DueDate?.ToString("dd/MM/yyyy") ?? "",
            ["Amount"] = doc.TotalAmount.ToString("N2"),
            ["BalanceDue"] = doc.BalanceDue.ToString("N2"),
            ["CompanyName"] = company,
        };
    }

    /// <summary>ID#12 (รอบ 2026-10-14 ทีม F4): ตรวจเอกสารซ้ำตอนส่ง — ยกเลิก/กลับเป็นร่าง/ลบ ⇒ ข้าม · ชำระครบ ⇒ ไม่ทวง ·
    /// ส่งได้ + มีกฎจริง ⇒ เขียนหัวเรื่อง/เนื้อใหม่ด้วยค่า ณ เวลาส่ง (ยอดค้างชำระปัจจุบัน ไม่ใช่ยอดตอนเข้าคิว) · คืนเหตุผลที่ข้าม หรือ null</summary>
    // รอบ 251 (WP-07 ข้อ 5): internal ให้ Db test MED-5 เรียกเส้น "ตรวจซ้ำตอนส่ง" ตัวจริงได้โดยไม่ต้องรันคิวทั้งระบบ
    internal async Task<string?> RecheckDocumentAtSendAsync(EmailQueue item, CancellationToken ct)
    {
        var doc = await _db.Documents.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == item.EntityId && d.CompanyId == item.CompanyId && !d.IsDeleted, ct);
        var rule = item.RuleId is { } ruleId
            ? await _db.EmailScheduleRules.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == item.CompanyId && !r.IsDeleted, ct)
            : null;
        var trigger = rule?.Trigger ?? ScheduledEmailSendCheck.TriggerFromIdempotencyKey(item.IdempotencyKey);
        var skip = ScheduledEmailSendCheck.SkipReason(trigger, doc != null,
            doc?.Status ?? DocumentStatus.Draft, doc?.BalanceDue ?? 0m);
        if (skip != null || doc == null) return skip;
        if (rule != null)
        {
            await _db.HydrateContactAsync(item.CompanyId, doc);
            var heading = await HeadingForAsync(doc.CompanyId, doc.Id);
            var ctx = DocumentEmailContext(doc, heading, _headingCompanies[doc.CompanyId].Company);
            // รอบ 248 (ฝ่ายค้าน MED-5): กฎที่โหลดใหม่ตอนส่งไม่มีชั้นรายการประจำ ⇒ ต่อชั้นเดียวกับตอนเข้าคิว (รหัสรายการจากกุญแจกันซ้ำ)
            if (ScheduledEmailSendCheck.RecurringIdFromIdempotencyKey(item.IdempotencyKey) is { } sendRecurringId)
                rule = WithRecurringPayLayer(rule, await RecurringPayLayerAsync(item.CompanyId, sendRecurringId, ct));
            // รอบ 241: ลิงก์ ณ เวลาส่ง (ยอดสุทธิปัจจุบัน · ใบชำระครบ/ยกเลิกแล้ว = ไม่แนบ)
            var payLink = await PayLinkFor(rule, doc, ct);
            ctx["PayLink"] = payLink?.Url != null ? DocumentPayLinkPolicy.QueuePlaceholder : "";   // ฝ่ายค้าน S1: คิวเก็บที่หมาย
            item.Subject = Render(rule.SubjectTemplate ?? DefaultDocSubject(rule.Trigger, heading.IsEnglish), ctx);
            item.Body = WithPayLink(Render(rule.BodyTemplate ?? DefaultDocBody(rule.Trigger, heading.IsEnglish), ctx, htmlEncodeValues: true),
                rule.BodyTemplate, payLink, heading.IsEnglish);
            // รอบ 250 (WP-02 §2.4 ข้อ 2): เหตุที่ไม่แนบลิงก์ ณ เวลาส่ง (ตั้ง URL แล้วหลังเข้าคิว ⇒ หมายเหตุหาย)
            item.ErrorMessage = PayLinkSkipNote(payLink);
        }
        return null;
    }

    /// <summary>50 ทวิ ตอนส่ง — ถูกลบ/ยกเลิกหลังเข้าคิว ⇒ ข้ามพร้อมเหตุผล (ตัวตัดสินเดียว ScheduledEmailSendCheck.WhtCertSkipReason)</summary>
    private async Task<string?> RecheckWhtCertAtSendAsync(EmailQueue item, CancellationToken ct)
    {
        var status = await _db.WithholdingTaxCerts.AsNoTracking()
            .Where(c => c.Id == item.EntityId && c.CompanyId == item.CompanyId && !c.IsDeleted)
            .Select(c => (Models.DTOs.Tax.WithholdingTaxCertStatus?)c.Status)
            .FirstOrDefaultAsync(ct);
        return ScheduledEmailSendCheck.WhtCertSkipReason(status != null,
            status == Models.DTOs.Tax.WithholdingTaxCertStatus.Voided);
    }

    /// <summary>ส่งไม่สำเร็จ ⇒ ลองใหม่ใน 15 นาที (ครบ 3 ครั้ง = Failed) พร้อมเหตุผลที่ผู้ใช้เห็นบนคิว</summary>
    private static void DeferOrFail(EmailQueue item, string? reason)
    {
        // ฝ่ายค้าน fix2 L4: ผู้ให้บริการรับไปแล้ว (Sent) ⇒ ข้อผิดพลาดหลังจากนั้นห้ามลดสถานะ (ลองใหม่ = ลูกค้าได้สองฉบับ) — จดเหตุไว้เท่านั้น
        if (item.Status == "Sent")
        {
            item.ErrorMessage = SaveFailureOutcome.SentButNotSavedPrefix + reason;
            return;
        }
        item.Status = item.RetryCount >= 3 ? "Failed" : "Pending";
        item.RetryCount++;
        item.ErrorMessage = reason;
        item.ScheduledFor = DateTime.UtcNow.AddMinutes(15);
    }

    private static string ResolveEmployeeEmail(Employee e) => e.Email ?? "";

    /// <summary>แทน placeholder · <paramref name="htmlEncodeValues"/> = true สำหรับ<b>เนื้อ</b>อีเมล (HTML) — ค่าที่แทนลงไป
    /// (ชื่อลูกค้า/พนักงาน/เลขเอกสาร/หัวเอกสาร) ผู้ใช้คุมได้ ต้องหนีก่อนต่อเข้า HTML (กฎ #4 C) · หัวเรื่องเป็นข้อความล้วน ไม่หนี</summary>
    internal static string Render(string template, Dictionary<string, string?> ctx, bool htmlEncodeValues = false)
    {
        if (string.IsNullOrEmpty(template)) return "";
        var result = template;
        foreach (var (k, v) in ctx)
            result = result.Replace("{" + k + "}",
                htmlEncodeValues ? System.Net.WebUtility.HtmlEncode(v ?? "") : v ?? "");
        return result;
    }

    // default (ตอน rule ไม่มี template ของตัวเอง) เลือกภาษาตามใบ — เนื้ออีเมล
    // ไทยครอบไฟล์แนบอังกฤษ = ลูกค้าต่างชาติอ่านไม่ออกว่าโดนทวงอะไร
    // ชื่อชนิดเอกสารมาจาก {DocTitle} (= หัวที่ PDF แนบพิมพ์จริง) — ห้ามมีตารางชื่อชนิดเอกสารของตัวเอง (S-12)
    internal static string DefaultDocSubject(string trigger, bool isEn = false) => trigger switch
    {
        "DocumentDueSoon" => isEn ? "Payment due soon: {DocTitle} No. {DocNumber}" : "ครบกำหนดชำระเร็ว ๆ นี้: {DocTitle} เลขที่ {DocNumber}",
        "DocumentOverdue" => isEn ? "Payment overdue: {DocTitle} No. {DocNumber}" : "เกินกำหนดชำระ: {DocTitle} เลขที่ {DocNumber}",
        _ => isEn ? "{DocTitle} No. {DocNumber}" : "{DocTitle} เลขที่ {DocNumber}",
    };

    internal static string DefaultDocBody(string trigger, bool isEn = false)
    {
        var heading = trigger switch
        {
            "DocumentDueSoon" => isEn
                ? "Dear {ContactName},<br>{DocTitle} No. {DocNumber} is due on {DueDate}.<br>Outstanding balance: <strong>THB {BalanceDue}</strong>"
                : "เรียน คุณ{ContactName}<br>{DocTitle} เลขที่ {DocNumber} ใกล้ครบกำหนดชำระวันที่ {DueDate}<br>ยอดค้างชำระ <strong>{BalanceDue} บาท</strong>",
            "DocumentOverdue" => isEn
                ? "Dear {ContactName},<br>{DocTitle} No. {DocNumber} has been overdue since {DueDate}.<br>Outstanding balance: <strong>THB {BalanceDue}</strong>. Please arrange payment at your earliest convenience."
                : "เรียน คุณ{ContactName}<br>{DocTitle} เลขที่ {DocNumber} เกินกำหนดชำระตั้งแต่ {DueDate}<br>ยอดค้างชำระ <strong>{BalanceDue} บาท</strong> กรุณาดำเนินการชำระโดยเร็ว",
            _ => isEn
                ? "Dear {ContactName},<br>Please find attached {DocTitle} No. {DocNumber} for a total of THB {Amount}."
                : "เรียน คุณ{ContactName}<br>แนบ{DocTitle} เลขที่ {DocNumber} ยอดรวม {Amount} บาท",
        };
        var footer = isEn ? "Sent automatically by {CompanyName}" : "ส่งโดย {CompanyName} โดยอัตโนมัติ";
        return $@"<div style='font-family:sans-serif;max-width:600px'>
            <p>{heading}</p>
            <p style='color:#64748b;font-size:13px'>{footer}</p>
        </div>";
    }

    private static string DefaultPayslipBody() => @"<div style='font-family:sans-serif;max-width:600px'>
        <p>เรียน {EmployeeName}</p>
        <p>แนบสลิปเงินเดือนประจำงวด {Period} ยอดสุทธิ <strong>{NetPay} บาท</strong></p>
        <p style='color:#64748b;font-size:13px'>กรุณาเก็บไว้เป็นหลักฐาน</p>
    </div>";

    private static string DefaultWhtBody() => @"<div style='font-family:sans-serif;max-width:600px'>
        <p>เรียน {ContactName}</p>
        <p>แนบหนังสือรับรองหัก ณ ที่จ่ายเลขที่ {DocNumber} จำนวนภาษี <strong>{Amount} บาท</strong></p>
    </div>";

    // ── CRUD rules + queue management ───────────────────────────────

    public async Task<LineQuietHours.RuleWarnings> LineQuietWarningsAsync(Guid companyId, CancellationToken ct = default)
    {
        var q = await _db.CompanySettings.AsNoTracking().Where(s => s.CompanyId == companyId)
            .Select(s => new { s.LineQuietHoursEnabled, s.LineQuietStartHour, s.LineQuietEndHour }).FirstOrDefaultAsync(ct);
        return q == null ? LineQuietHours.RuleWarningsByHour(false, LineQuietHours.DefaultStartHour, LineQuietHours.DefaultEndHour)
            : LineQuietHours.RuleWarningsByHour(q.LineQuietHoursEnabled, q.LineQuietStartHour, q.LineQuietEndHour);
    }

    public async Task<List<EmailScheduleRule>> GetRulesAsync(Guid companyId)
        => await _db.EmailScheduleRules.AsNoTracking()
            .Where(r => r.CompanyId == companyId && !r.IsDeleted)
            .OrderBy(r => r.Trigger).ThenBy(r => r.OffsetDays).ToListAsync();

    public async Task<EmailScheduleRule> UpsertRuleAsync(Guid companyId, EmailScheduleRule rule, string actor)
    {
        // รอบ 241: เปิดแนบลิงก์/เลือกช่องทางได้เฉพาะที่บริษัทเปิดจริง (เซิร์ฟเวอร์ตัดสิน — ไม่เก็บค่าที่ใช้ไม่ได้เงียบ ๆ)
        if (_payLinks != null && (rule.AttachPayLink == true || rule.PayLinkChannelsJson != null))
        {
            var facts = await _payLinks.CompanyFactsAsync(companyId);
            if (rule.AttachPayLink == true && DocumentPayLinkPolicy.UnavailableReason(facts) is { } why)
                throw new BusinessRuleException(why, DocumentPayLinkPolicy.RuleCode);
            var bad = DocumentPayLinkPolicy.Unknown(DocumentPayLinkPolicy.CompanyAvailable(facts), DocumentPayLinkPolicy.ParseChannels(rule.PayLinkChannelsJson));
            if (bad.Count > 0)
                throw new BusinessRuleException("ช่องทางที่บริษัทยังไม่ได้เปิด: " + string.Join(", ", bad), DocumentPayLinkPolicy.RuleCode);
        }
        // ฝ่ายค้าน M2: ส่งซ้ำเฉพาะกฎเกินกำหนด (ทางเข้าอื่นนอก controller ก็ไม่เก็บค่าที่ไม่มีผล)
        if (OverdueReminderSchedule.RepeatProblem(rule.Trigger, rule.RepeatEveryDays) is string repeatErr)
            throw new BusinessRuleException(repeatErr, "EMAIL-SCHEDULE-RANGE");
        // รอบ 254 WP-11 L2 (DESIGN §3.d ด่านตอนบันทึก): ติ๊กช่องทางอย่างน้อย 1 · LINE เฉพาะ trigger ที่รองรับ · สลิปห้ามเข้า LINE ผู้ติดต่อ ·
        // ไม่มีช่อง LINE ของบริษัท ⇒ ปฏิเสธพร้อมทางไปต่อ (ห้ามเก็บติ๊กที่ไม่มีผลเงียบ ๆ)
        var lineChannel = ContactLineChannel.WantsLine(rule.ChannelLineUser, rule.ChannelLineGroup)
            ? await _recipients.LineChannelAsync(companyId)
            : ContactRecipientRule.LineOpenness.Closed;
        if (ContactLineChannel.RuleProblem(rule.Trigger, rule.ChannelEmail, rule.ChannelLineUser, rule.ChannelLineGroup,
                lineChannel.Open && lineChannel.CompanyOaReady, lineChannel.CompanyOaProblem,
                centralGroupOpen: lineChannel.Open && lineChannel.NextAccGroupOpen
                    && await _recipients.CentralGroupProblemAsync(companyId) == null,
                // รอบ 262 (คำตัดสิน 262-1-1): บริษัทผู้ให้บริการ — OA กลางคือ OA ของบริษัท ⇒ ติ๊ก LINE ผู้ใช้ได้เมื่อ OA กลางพร้อม (token + secret)
                centralUserOpen: lineChannel.Open && lineChannel.NextAccUserOpen
                    && await _recipients.CentralUserProblemAsync(companyId) == null) is string channelErr)
            throw new BusinessRuleException(channelErr, ContactLineChannel.RuleCode);
        if (rule.Id == Guid.Empty)
        {
            rule.CompanyId = companyId;
            rule.CreatedByName = actor;
            rule.RepeatActivatedAt = OverdueReminderSchedule.NextActivatedAt(0, null, rule.RepeatEveryDays, DateTime.UtcNow);
            _db.EmailScheduleRules.Add(rule);
        }
        else
        {
            var existing = await _db.EmailScheduleRules
                .FirstOrDefaultAsync(r => r.Id == rule.Id && r.CompanyId == companyId)
                ?? throw new KeyNotFoundException("ไม่พบกฎ");
            // รอบ 258 (B-M7): เปิดกฎที่ปิดไว้/เปลี่ยนตาราง ⇒ ล้างลายน้ำ (ไม่ย้อนเก็บตามตารางเก่าหรือช่วงที่ตั้งใจปิด)
            existing.LastDayScanBkk = ScheduleCatchUp.WatermarkAfterEdit(existing.LastDayScanBkk, existing.IsActive, rule.IsActive,
                existing.Trigger, rule.Trigger, existing.OffsetDays, rule.OffsetDays, existing.DayOfMonth, rule.DayOfMonth);
            existing.Trigger = rule.Trigger;
            existing.IsActive = rule.IsActive;
            existing.DocumentType = rule.DocumentType;
            existing.OffsetDays = rule.OffsetDays;
            existing.SendAtHour = rule.SendAtHour;
            existing.RepeatActivatedAt = OverdueReminderSchedule.NextActivatedAt(existing.RepeatEveryDays, existing.RepeatActivatedAt,
                rule.RepeatEveryDays, DateTime.UtcNow);   // ฝ่ายค้าน H1-ข: ประทับเมื่อเปิด (0 → > 0) — ก่อนเขียนค่าใหม่
            existing.RepeatEveryDays = rule.RepeatEveryDays;
            existing.SubjectTemplate = rule.SubjectTemplate;
            existing.BodyTemplate = rule.BodyTemplate;
            existing.BccEmails = rule.BccEmails;
            existing.DayOfMonth = rule.DayOfMonth;
            // AudienceFilter: เลิกใช้ (รอบ 250 WP-04 ต่อยอด — ไม่มีหน้า/ผู้อ่าน · คอลัมน์คงไว้เพราะ schema) ⇒ ไม่รับค่า
            // รอบ 241: ค่าทับต่อตาราง (null = ตามบริษัท) — ไม่ลอกไม่ได้ (ห้าม silent no-op)
            existing.AttachPayLink = rule.AttachPayLink;
            existing.PayLinkChannelsJson = rule.PayLinkChannelsJson;
            existing.PostPayEmailReceipt = rule.PostPayEmailReceipt;
            existing.PostPayIncludePartial = rule.PostPayIncludePartial;
            // รอบ 254 WP-11 L2: ช่องทางต่อกฎ (ผ่านด่านข้างบนแล้ว · echo กลับผ่าน entity ที่ GetRulesAsync คืน)
            existing.ChannelEmail = rule.ChannelEmail;
            existing.ChannelLineUser = rule.ChannelLineUser;
            existing.ChannelLineGroup = rule.ChannelLineGroup;
            existing.LineFallbackToEmail = rule.LineFallbackToEmail;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return rule;
    }

    public async Task DeleteRuleAsync(Guid companyId, Guid ruleId)
    {
        var rule = await _db.EmailScheduleRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบกฎ");
        rule.IsDeleted = true;
        rule.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<(int Pending, int Sent, int Failed)> GetQueueStatsAsync(Guid companyId)
    {
        var stats = await _db.EmailQueues.AsNoTracking()
            .Where(q => q.CompanyId == companyId && !q.IsDeleted
                && q.CreatedAt > DateTime.UtcNow.AddDays(-30))
            .GroupBy(q => q.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();
        return (
            stats.FirstOrDefault(s => s.Status == "Pending")?.Count ?? 0,
            stats.FirstOrDefault(s => s.Status == "Sent")?.Count ?? 0,
            stats.FirstOrDefault(s => s.Status == "Failed")?.Count ?? 0);
    }

    public async Task<List<EmailQueue>> GetRecentQueueAsync(Guid companyId, int take = 50)
        => await _db.EmailQueues.AsNoTracking()
            .Where(q => q.CompanyId == companyId && !q.IsDeleted)
            .OrderByDescending(q => q.CreatedAt).Take(take).ToListAsync();

    public async Task RetryFailedAsync(Guid companyId, Guid queueId)
    {
        var q = await _db.EmailQueues
            .FirstOrDefaultAsync(x => x.Id == queueId && x.CompanyId == companyId)
            ?? throw new KeyNotFoundException("ไม่พบคิว");
        // รอบ 250 WP-03 (ฝ่ายค้าน L7): ลองใหม่ได้เฉพาะแถวที่ล้ม — เดิมรีเซ็ตทุกสถานะ ⇒ แถวที่ส่งแล้ว (Sent) กลับเป็น Pending = ลูกค้าได้อีเมลซ้ำ ·
        // แถวที่ยกเลิก/กำลังรอ ไม่ต้องลองใหม่ (ยกเลิกแล้ว = ตั้งกฎ/ส่งใหม่ที่เอกสาร)
        // รอบ 258 (B-H2): รับแถวที่ค้าง "กำลังส่ง" ด้วย (ตัวตัดสินเดียว EmailQueueStaleSending) — แถวที่กำลังส่งจริง/ส่งแล้ว/รอส่ง ยังไม่รับ
        if (!EmailQueueStaleSending.CanRetry(q.Status, q.UpdatedAt, q.CreatedAt, DateTime.UtcNow))
            throw new Accounting.Helpers.BusinessRuleException(
                $"ลองส่งใหม่ได้เฉพาะคิวที่ล้ม (สถานะตอนนี้: {q.Status}) — คิวที่ส่งแล้วจะไม่ถูกส่งซ้ำ", "EMAIL-QUEUE-RETRY-FAILED-ONLY", 409);
        q.Status = "Pending";
        q.SendStartedAt = null;   // รอบ 258 ฝ่ายค้าน R-M3: รอบจองใหม่เริ่มจาก "ยังไม่เคยลองส่ง" + ไม่มีผู้จอง
        q.ClaimToken = null;
        q.RetryCount = 0;
        q.ScheduledFor = DateTime.UtcNow;
        q.ErrorMessage = null;
        await _db.SaveChangesAsync();
    }

    public async Task CancelPendingAsync(Guid companyId, Guid queueId)
    {
        // ฝ่ายค้าน fix2 L3: คำสั่งเดียวแบบมีเงื่อนไข — แถวที่ตัวส่งจองไปแล้ว (Sending) ไม่ถูกทับเป็นยกเลิก
        // รอบ 258 (B-H2): แถวที่ค้าง "กำลังส่ง" เกินเส้นแบ่ง (ตัวส่งตายกลางคัน) ยกเลิกได้ — เดิมไม่มีทางออกของคน
        var staleCutoff = EmailQueueStaleSending.Cutoff(DateTime.UtcNow);
        var n = await _db.EmailQueues
            .Where(x => (x.Id == queueId && x.CompanyId == companyId && x.Status == EmailQueueStaleSending.StatusPending)
                || (x.Id == queueId && x.CompanyId == companyId && x.Status == EmailQueueStaleSending.StatusSending
                    && (x.UpdatedAt ?? x.CreatedAt) < staleCutoff))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "Cancelled").SetProperty(x => x.UpdatedAt, (DateTime?)DateTime.UtcNow));
        if (n != 1) throw new KeyNotFoundException("ไม่พบคิวที่ยกเลิกได้ (อาจกำลังส่งหรือส่งไปแล้ว)");
    }
}
