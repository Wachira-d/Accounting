using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Cms;

/// <summary>ส่งข้อความถึงลูกค้า (และร้าน) ของการจอง/ออเดอร์/บัตรคอร์ส/ที่พัก/ตะกร้า/ฟอร์ม — <b>จุดตัดสินเดียว</b> (รอบ 212 แพ็ก A · คำตัดสินข้อ 171):
/// <list type="number">
/// <item>กันส่งซ้ำ: claim <c>MessageKey</c> (<see cref="INotifyQuotaService.ClaimAsync"/>) — ส่งถึงแล้ว ⇒ <see cref="BookingSendResult.AlreadySent"/> (ไม่ส่งซ้ำ)</item>
/// <item>ค่าตั้งต่อประเภท × ช่อง: <see cref="CustomerNotifyPolicy.Resolve"/> (ไม่มีแถว = เปิดทั้งสอง = เดิม) · ปิดหมด ⇒ <see cref="BookingSendResult.Suppressed"/></item>
/// <item>ลำดับขั้น: <see cref="CustomerNotifyPolicy.Plan"/> (ภายในเรียก <see cref="CustomerNotifyChannel.Order"/> เดิม — ลูกค้าผูก LINE เอง ⇒ LINE ก่อน · ไม่ผูก ⇒ อีเมลก่อน) ·
///   อีเมล: ผู้ส่งของร้าน ⇒ ของระบบ ตาม <see cref="CustomerNotifyChannel.EmailPlan"/> + <c>CompanySettings.CustomerEmailFallback</c></item>
/// <item>ขั้นที่ออกช่องกลาง (OA/SMTP ของ NextAcc) ⇒ จองโควตาก่อนส่ง (<see cref="INotifyQuotaService.TryReserveAsync"/>) · เต็ม ⇒ ข้ามไปช่องของร้าน (+ Warning) ·
///   ไม่มีช่องของร้าน ⇒ Suppressed (ไม่ส่ง) · ส่งล้ม ⇒ คืนหน่วย</item>
/// </list>
/// ทุกช่องล้ม/ไม่มี ⇒ <c>Error</c> พร้อมเหตุ (ไม่โยน — ผู้เรียกประทับบนรายการ) · SMS: ยังไม่มีผู้ให้บริการ</summary>
public class BookingNotifier : IBookingNotifier
{
    private readonly IEmailService? _email;
    private readonly ILineNotifyService? _line;
    private readonly ILogger<BookingNotifier> _logger;
    private readonly ICustomerLineLinkService? _links;
    private readonly IEmailSenderFactory? _senders;
    private readonly AccountingDbContext? _db;
    private readonly INotifyQuotaService? _quota;

    public BookingNotifier(ILogger<BookingNotifier> logger, IEmailService? email = null, ILineNotifyService? line = null,
        ICustomerLineLinkService? links = null, IEmailSenderFactory? senders = null, AccountingDbContext? db = null,
        INotifyQuotaService? quota = null)
    {
        _logger = logger;
        _email = email;
        _line = line;
        _links = links;
        _senders = senders;
        _db = db;
        _quota = quota;
    }

    public async Task<BookingSendResult> SendAsync(CustomerNotifyRequest request, CancellationToken ct = default)
    {
        var companyId = request.CompanyId;
        var contact = request.Contact;
        if (!CustomerNotifyEvents.IsKnown(request.EventKey))
            _logger.LogError("ประเภทข้อความ {EventKey} ไม่อยู่ในแคตตาล็อก CustomerNotifyEvents — บั๊กของผู้เรียก (ยังส่งตามค่าเริ่มต้น)", request.EventKey);

        // 1) กันส่งซ้ำ (ไม่มีคีย์/ไม่มีบริการ = ไม่กันซ้ำ เหมือนเดิม)
        var claim = _quota == null ? NotifyClaim.Untracked
            : await _quota.ClaimAsync(companyId, request.MessageKey, request.EventKey,
                request.TargetKind ?? contact.LineTarget?.RecordKind, request.TargetId ?? contact.LineTarget?.RecordId, ct);
        if (!claim.Claimed)
            return claim.AlreadySent
                ? BookingSendResult.AlreadySent(claim.PreviousChannel)
                : BookingSendResult.Suppressed("ข้อความนี้กำลังถูกส่งโดยคำขออื่น — ไม่ส่งซ้ำ (ระบบจะลองใหม่ถ้ายังไม่ถึง)");

        var result = await DeliverAsync(request, ct);
        await CompleteAsync(companyId, claim.LogId, result, ct);
        return result.Result;
    }

    private sealed record Delivery(BookingSendResult Result, string? Source, bool Metered);

    private async Task CompleteAsync(Guid companyId, Guid? logId, Delivery d, CancellationToken ct)
    {
        if (_quota == null || logId == null) return;
        var status = d.Result.Sent ? NotifyLogStatus.Sent : d.Result.IsSuppressed ? NotifyLogStatus.Suppressed : NotifyLogStatus.Failed;
        await _quota.CompleteAsync(companyId, logId, status, d.Result.Channel, d.Source, d.Metered, d.Result.Error ?? d.Result.Warning, ct);
    }

    private async Task<Delivery> DeliverAsync(CustomerNotifyRequest request, CancellationToken ct)
    {
        var companyId = request.CompanyId;
        var contact = request.Contact;
        var errors = new List<string>();

        // LINE ที่ลูกค้าผูกเอง (opt-in) ชนะ LINE ที่ผู้เรียกให้มา · หาไม่ได้/ล้ม ⇒ ไม่ล้มการส่ง (ยังมีอีเมล) แต่บอกในร่องรอย
        string? optedLine = null;
        if (contact.LineTarget != null && _links != null)
        {
            try { optedLine = await _links.ResolveLineUserIdAsync(companyId, contact.LineTarget, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "หา LINE ที่ลูกค้าผูกไว้ไม่สำเร็จ (company {CompanyId})", companyId);
                errors.Add("LINE: อ่านการเชื่อมต่อ LINE ของลูกค้าไม่สำเร็จ");
            }
        }
        var line = !string.IsNullOrWhiteSpace(optedLine) ? optedLine : contact.LineUserId;
        var hasEmail = !string.IsNullOrWhiteSpace(contact.Email);
        var hasLine = !string.IsNullOrWhiteSpace(line);
        if (!hasEmail && !hasLine) return new Delivery(BookingSendResult.Fail("ไม่มีช่องทางติดต่อลูกค้า (ไม่มีอีเมล/LINE บนการจอง)"), null, false);

        // 2) ค่าตั้งต่อประเภทข้อความ × ช่อง (ตัวตัดสินเดียว)
        var policy = CustomerNotifyPolicy.Resolve(await LoadRulesAsync(companyId, request.SiteId, ct), request.SiteId, request.EventKey);
        var settings = await LoadEmailSettingsAsync(companyId, ct);
        var emailPlan = CustomerNotifyChannel.EmailPlan(settings != null && _senders != null && settings.EmailConfigured, settings?.CustomerEmailFallback);
        var suppress = CustomerNotifyPolicy.SuppressReason(policy, hasEmail, hasLine, emailPlan);
        if (suppress != null) return new Delivery(BookingSendResult.Suppressed(suppress), null, false);

        // 3) ลำดับขั้น + ช่องจริง (OA ที่ push ใช้ = ตัวตัดสินเดียวกับ LineNotifyService)
        // รอบ 212 แพ็ก B: คำเชิญผูกใหม่ผ่าน OA เดิม (NextAcc) — userId ผูกกับ OA กลาง ⇒ scope กลาง (นับโควตา) + ถือเป็นช่องที่ลูกค้าผูกเอง (LINE ก่อน)
        string? lineScope = null;
        if (hasLine && policy.LineOn)
            lineScope = request.LineViaNextAcc ? CustomerNotifyChannel.ScopeNextAcc : await ResolveLineScopeAsync(companyId, ct);
        var steps = CustomerNotifyPolicy.Plan(policy, hasEmail, hasLine,
            lineOptedIn: !string.IsNullOrWhiteSpace(optedLine) || request.LineViaNextAcc, lineScope, emailPlan);
        var essential = CustomerNotifyEvents.IsEssential(request.EventKey);
        var html = request.Html ?? BookingReminderPlan.ToHtml(request.Text);
        string? tenantError = null;
        int? quotaFullLimit = null;

        foreach (var step in steps)
        {
            // 4) ช่องกลาง ⇒ จองโควตาก่อนส่ง · เต็ม ⇒ ข้ามไปขั้นถัดไป (ช่องของร้าน) · ส่งล้ม ⇒ คืนหน่วย
            NotifyReservation? reservation = null;
            if (step.Metered && _quota != null)
            {
                reservation = await _quota.TryReserveAsync(companyId, essential, ct);
                if (!reservation.Ok)
                {
                    quotaFullLimit = reservation.Limit;
                    await _quota.AlertIfCrossedAsync(companyId, reservation, ct);
                    continue;
                }
            }

            bool sent;
            string? stepError;
            if (step.Channel == CustomerNotifyChannel.ChannelLine)
            {
                if (_line == null) { sent = false; stepError = "LINE: ระบบ LINE ไม่พร้อม"; }
                else if (request.LineViaNextAcc)
                {
                    // OA เดิมของลูกค้า = OA กลาง (ระบุชัด ไม่เลือกตามค่าตั้งบริษัท — บริษัทเปลี่ยนไปใช้ OA ของตัวเองแล้ว)
                    var pushed = await _line.TryPushViaNextAccAsync(line!.Trim(), new[] { request.Text });
                    sent = pushed.Ok;
                    stepError = sent ? null : $"LINE (OA ของ NextAcc): {pushed.Error ?? "ส่งไม่สำเร็จ"}";
                }
                else
                {
                    sent = await _line.TryPushToUserAsync(companyId, line!.Trim(), request.Text);
                    stepError = sent ? null : "LINE: ส่งไม่สำเร็จ (ลูกค้าบล็อก OA / ยังไม่เพิ่มเพื่อน / ยังไม่ตั้งค่า channel token)";
                }
            }
            else if (step.Source == CustomerNotifyPolicy.SrcTenantSmtp)
            {
                tenantError = await TrySendTenantEmailAsync(companyId, settings!, contact.Email!.Trim(), request.Subject, html, ct);
                sent = tenantError == null;
                stepError = sent ? null : $"อีเมล (ผู้ส่งของร้าน): {tenantError}";
                if (!sent) _logger.LogError("ผู้ส่งอีเมลของร้านล้ม (company {CompanyId}): {Error}", companyId, tenantError);
            }
            else
            {
                var mail = await TrySendEmailAsync(contact.Email!.Trim(), request.Subject, html, ct);
                sent = mail.Sent;
                stepError = mail.Error;
            }

            if (reservation != null && _quota != null)
            {
                if (sent) await _quota.AlertIfCrossedAsync(companyId, reservation, ct);
                else await _quota.ReleaseAsync(companyId, reservation, ct);
            }

            if (sent)
            {
                // ถึงทางถอย ⇒ Warning ให้ผู้เรียกประทับ (ไม่ใช่แค่ log): ผู้ส่งของร้านล้มแล้วไปอีเมลระบบ · หรือโควตาช่องกลางเต็มแล้วไปช่องของร้าน
                var warning = step.Source == CustomerNotifyPolicy.SrcSystemSmtp && tenantError != null
                    ? CustomerNotifyChannel.TenantEmailFallbackWarning(tenantError)
                    : quotaFullLimit is int full ? NotifyQuotaPolicy.RoutedToTenantWarning(full) : null;
                return new Delivery(BookingSendResult.Ok(step.Channel) with { Warning = warning }, step.Source, step.Metered);
            }
            errors.Add(stepError ?? "ส่งไม่สำเร็จ");
        }

        // เหลือแต่ช่องกลางที่โควตาเต็ม (ไม่มีขั้นอื่นที่ลองแล้วล้ม) ⇒ ระงับโดยตั้งใจ + บอกทางไปต่อ
        if (quotaFullLimit is int limit && errors.Count == 0)
            return new Delivery(BookingSendResult.Suppressed(NotifyQuotaPolicy.FullReason(limit)), null, false);
        if (quotaFullLimit is int limit2) errors.Add(NotifyQuotaPolicy.FullReason(limit2));
        if (errors.Count == 0) errors.Add("ไม่มีช่องทางที่ส่งได้");
        var all = string.Join(" · ", errors);
        _logger.LogWarning("ส่งข้อความถึงลูกค้าไม่สำเร็จ (company {CompanyId} event {EventKey}): {Error}", companyId, request.EventKey, all);
        return new Delivery(BookingSendResult.Fail(all), null, false);
    }

    /// <summary>ค่าตั้งอีเมลของบริษัท (null = ไม่มี/ไม่มี DB) — แยกเป็นเมธอดให้เทสต์แทนค่าได้โดยไม่ต้องมีฐานข้อมูล</summary>
    protected virtual async Task<CompanySettings?> LoadEmailSettingsAsync(Guid companyId, CancellationToken ct)
        => _db == null ? null : await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId, ct);

    /// <summary>แถวค่าตั้งของบริษัท + เว็บนี้ (ว่าง = ค่าเริ่มต้น) — ทุก query กรอง <c>CompanyId</c></summary>
    protected virtual async Task<IReadOnlyList<CustomerNotifyRuleRow>> LoadRulesAsync(Guid companyId, Guid? siteId, CancellationToken ct)
    {
        if (_db == null) return Array.Empty<CustomerNotifyRuleRow>();
        return await _db.CustomerNotifyRules.AsNoTracking()
            .Where(r => r.CompanyId == companyId && (r.SiteId == null || r.SiteId == siteId))
            .Select(r => new CustomerNotifyRuleRow(r.SiteId, r.EventKey, r.LineEnabled, r.EmailEnabled))
            .ToListAsync(ct);
    }

    /// <summary>OA ที่ push จะใช้จริง (ตัดสินที่ <see cref="ICustomerLineLinkService.ResolveChannelAsync"/> ตัวเดียว) · ไม่รู้ ⇒ ถือเป็น OA กลาง (นับโควตา — ด้านปลอดภัย)</summary>
    protected virtual async Task<string?> ResolveLineScopeAsync(Guid companyId, CancellationToken ct)
    {
        if (_links == null) return CustomerNotifyChannel.ScopeNextAcc;
        try
        {
            var ch = await _links.ResolveChannelAsync(companyId, ct);
            return ch?.Scope ?? CustomerNotifyChannel.ScopeNextAcc;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ตัดสิน OA ของบริษัท {CompanyId} ไม่สำเร็จ — นับเป็น OA กลาง", companyId);
            return CustomerNotifyChannel.ScopeNextAcc;
        }
    }

    /// <summary>ผู้ส่งอีเมลของร้าน (ตั้ง+ทดสอบผ่าน) — ผู้ส่งจาก <see cref="DocumentEmailService.ResolveFromAddress"/> ตัวเดียวกับเอกสาร · null = สำเร็จ · ไม่งั้นคืนเหตุ</summary>
    private async Task<string?> TrySendTenantEmailAsync(Guid companyId, CompanySettings s, string to, string subject, string html, CancellationToken ct)
    {
        try
        {
            var sender = _senders!.GetSenderForSettings(s);
            var msg = new EmailMessage
            {
                FromAddress = DocumentEmailService.ResolveFromAddress(s),
                FromName = s.EmailFromName,
                Subject = subject,
                HtmlBody = html,
            };
            msg.To.Add(to);
            var r = await sender.SendAsync(msg, ct);
            return r.Success ? null : (r.ErrorMessage ?? "ส่งไม่สำเร็จ");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "ส่งอีเมลผ่านผู้ส่งของร้านล้ม (company {CompanyId})", companyId);
            return "เกิดข้อผิดพลาด (ดูบันทึกระบบ)";
        }
    }

    /// <summary>อีเมลของระบบ (SMTP ของ NextAcc) — ถึงแล้ว ⇒ Sent · ไม่งั้นคืนเหตุ</summary>
    private async Task<(bool Sent, string? Error)> TrySendEmailAsync(string to, string subject, string html, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var systemOk = _email != null && await _email.TrySendAsync(to, subject, html);
        if (systemOk) return (true, null);
        return (false, _email == null ? "อีเมล: ระบบอีเมลไม่พร้อม" : "อีเมล: ส่งไม่สำเร็จหรือยังไม่ตั้งค่า SMTP");
    }
}
