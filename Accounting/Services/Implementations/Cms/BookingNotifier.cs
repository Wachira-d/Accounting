using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Cms;

/// <summary>ส่งข้อความถึงลูกค้าของการจอง/ออเดอร์/บัตรคอร์ส — ลำดับช่องตัดสินที่ <see cref="CustomerNotifyChannel.Order"/> ตัวเดียว:
/// ลูกค้า<b>ผูก LINE เอง</b> (รอบ 211 · <see cref="ICustomerLineLinkService.ResolveLineUserIdAsync"/>) ⇒ LINE ก่อนแล้วอีเมล · ไม่ได้ผูก ⇒ อีเมลก่อน (เดิม) ·
/// อีเมลผ่านผู้ส่งของร้านเมื่อร้านตั้งและทดสอบผ่าน แล้วถอยไปอีเมลของระบบเมื่อล้ม (<see cref="CustomerNotifyChannel.EmailPlan"/> · ประทับคำเตือน) ไม่งั้นอีเมลของระบบ (เดิม) ·
/// LINE ผ่าน OA ที่ <see cref="ILineNotifyService.TryPushToUserAsync"/> ตัดสิน (ของร้าน ⇒ ไม่งั้น NextAcc) ·
/// ทุกช่องล้ม/ไม่มี ⇒ <c>Error</c> พร้อมเหตุ (ไม่โยน — งานเบื้องหลังเดินต่อแถวถัดไป · ผู้เรียกประทับบนรายการ) ·
/// SMS: ยังไม่มีผู้ให้บริการในระบบ (ทะเบียน R-12 ระบุ "อีเมล/LINE/SMS" — SMS รอผู้ให้บริการ · บอกตรง ๆ ในร่องรอยแทนเงียบ)</summary>
public class BookingNotifier : IBookingNotifier
{
    private readonly IEmailService? _email;
    private readonly ILineNotifyService? _line;
    private readonly ILogger<BookingNotifier> _logger;
    private readonly ICustomerLineLinkService? _links;
    private readonly IEmailSenderFactory? _senders;
    private readonly AccountingDbContext? _db;

    public BookingNotifier(ILogger<BookingNotifier> logger, IEmailService? email = null, ILineNotifyService? line = null,
        ICustomerLineLinkService? links = null, IEmailSenderFactory? senders = null, AccountingDbContext? db = null)
    {
        _logger = logger;
        _email = email;
        _line = line;
        _links = links;
        _senders = senders;
        _db = db;
    }

    public async Task<BookingSendResult> SendAsync(Guid companyId, BookingContact contact, string subject, string text, CancellationToken ct = default)
    {
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
        if (!hasEmail && !hasLine) return BookingSendResult.Fail("ไม่มีช่องทางติดต่อลูกค้า (ไม่มีอีเมล/LINE บนการจอง)");

        foreach (var channel in CustomerNotifyChannel.Order(hasEmail, hasLine, lineOptedIn: !string.IsNullOrWhiteSpace(optedLine)))
        {
            if (channel == CustomerNotifyChannel.ChannelEmail)
            {
                var mail = await TrySendEmailAsync(companyId, contact.Email!.Trim(), subject, BookingReminderPlan.ToHtml(text), ct);
                // ส่งถึงทางอีเมลของระบบหลังผู้ส่งของร้านล้ม ⇒ สำเร็จ + Warning (ผู้เรียกประทับบนรายการ — ไม่ใช่แค่ log)
                if (mail.Sent) return BookingSendResult.Ok("email") with { Warning = mail.Warning };
                errors.Add(mail.Error!);
            }
            else
            {
                if (_line == null) errors.Add("LINE: ระบบ LINE ไม่พร้อม");
                else if (await _line.TryPushToUserAsync(companyId, line!.Trim(), text)) return BookingSendResult.Ok("line");
                else errors.Add("LINE: ส่งไม่สำเร็จ (ลูกค้าบล็อก OA / ยังไม่เพิ่มเพื่อน / ยังไม่ตั้งค่า channel token)");
            }
        }
        var all = string.Join(" · ", errors);
        _logger.LogWarning("ส่งข้อความถึงลูกค้าไม่สำเร็จ (company {CompanyId}): {Error}", companyId, all);
        return BookingSendResult.Fail(all);
    }

    /// <summary>ค่าตั้งอีเมลของบริษัท (null = ไม่มี/ไม่มี DB) — แยกเป็นเมธอดให้เทสต์แทนค่าได้โดยไม่ต้องมีฐานข้อมูล</summary>
    protected virtual async Task<Models.Entities.CompanySettings?> LoadEmailSettingsAsync(Guid companyId, CancellationToken ct)
        => _db == null ? null : await _db.CompanySettings.AsNoTracking().FirstOrDefaultAsync(x => x.CompanyId == companyId, ct);

    /// <summary>อีเมล — ลำดับจาก <see cref="CustomerNotifyChannel.EmailPlan"/>: ร้านตั้ง+ทดสอบผ่าน ⇒ ผู้ส่งของร้านก่อน ⇒ ล้ม ⇒ <b>ถอยไปอีเมลของระบบ</b>
    /// (คำตัดสินข้อ 170 แก้ตามฝ่ายค้าน: ลูกค้าต้องไม่ได้อะไรเลยไม่ได้) + คืน Warning ให้ผู้เรียกประทับ <see cref="CmsNotificationText.FallbackTag"/> ·
    /// ไม่ได้ตั้ง ⇒ อีเมลของระบบ (เดิม)</summary>
    private async Task<(bool Sent, string? Error, string? Warning)> TrySendEmailAsync(Guid companyId, string to, string subject, string html, CancellationToken ct)
    {
        Models.Entities.CompanySettings? s = null;
        if (_senders != null) s = await LoadEmailSettingsAsync(companyId, ct);
        string? tenantError = null;
        foreach (var source in CustomerNotifyChannel.EmailPlan(s != null && _senders != null && s.EmailConfigured))
        {
            if (source == CustomerNotifyChannel.EmailTenant)
            {
                try
                {
                    var sender = _senders!.GetSenderForSettings(s!);
                    var msg = new EmailMessage
                    {
                        FromAddress = DocumentEmailService.ResolveFromAddress(s!),
                        FromName = s!.EmailFromName,
                        Subject = subject,
                        HtmlBody = html,
                    };
                    msg.To.Add(to);
                    var r = await sender.SendAsync(msg, ct);
                    if (r.Success) return (true, null, null);
                    tenantError = r.ErrorMessage ?? "ส่งไม่สำเร็จ";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "ส่งอีเมลผ่านผู้ส่งของร้านล้ม (company {CompanyId})", companyId);
                    tenantError = "เกิดข้อผิดพลาด (ดูบันทึกระบบ)";
                }
                _logger.LogError("ผู้ส่งอีเมลของร้านล้ม (company {CompanyId}): {Error} — ถอยไปอีเมลของระบบ", companyId, tenantError);
                continue;
            }
            var systemOk = _email != null && await _email.TrySendAsync(to, subject, html);
            if (systemOk)
                return (true, null, tenantError == null ? null : CustomerNotifyChannel.TenantEmailFallbackWarning(tenantError));
            var sysErr = _email == null ? "อีเมล: ระบบอีเมลไม่พร้อม" : "อีเมล: ส่งไม่สำเร็จหรือยังไม่ตั้งค่า SMTP";
            return (false, tenantError == null ? sysErr : $"อีเมล (ผู้ส่งของร้าน): {tenantError} · {sysErr}", null);
        }
        return (false, "อีเมล: ไม่มีผู้ส่ง", null);
    }
}
