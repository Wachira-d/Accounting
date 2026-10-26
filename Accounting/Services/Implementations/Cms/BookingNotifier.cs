using Accounting.Helpers;
using Accounting.Services.Interfaces;

namespace Accounting.Services.Implementations.Cms;

/// <summary>ส่งข้อความถึงลูกค้าของการจอง — อีเมล (<see cref="IEmailService.TrySendAsync"/>) ก่อน · ไม่สำเร็จ/ไม่มีอีเมล ⇒ LINE
/// (<see cref="ILineNotifyService.TryPushToUserAsync"/>) · ทั้งสองล้ม/ไม่มี ⇒ <c>Error</c> พร้อมเหตุ (ไม่โยน — งานเบื้องหลังเดินต่อแถวถัดไป) ·
/// SMS: ยังไม่มีผู้ให้บริการในระบบ (ทะเบียน R-12 ระบุ "อีเมล/LINE/SMS" — SMS รอผู้ให้บริการ · บอกตรง ๆ ในร่องรอยแทนเงียบ)</summary>
public class BookingNotifier : IBookingNotifier
{
    private readonly IEmailService? _email;
    private readonly ILineNotifyService? _line;
    private readonly ILogger<BookingNotifier> _logger;

    public BookingNotifier(ILogger<BookingNotifier> logger, IEmailService? email = null, ILineNotifyService? line = null)
    {
        _logger = logger;
        _email = email;
        _line = line;
    }

    public async Task<BookingSendResult> SendAsync(Guid companyId, BookingContact contact, string subject, string text, CancellationToken ct = default)
    {
        if (!contact.HasChannel) return BookingSendResult.Fail("ไม่มีช่องทางติดต่อลูกค้า (ไม่มีอีเมล/LINE บนการจอง)");
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(contact.Email))
        {
            if (_email == null) errors.Add("อีเมล: ระบบอีเมลไม่พร้อม");
            else if (await _email.TrySendAsync(contact.Email.Trim(), subject, BookingReminderPlan.ToHtml(text))) return BookingSendResult.Ok("email");
            else errors.Add("อีเมล: ส่งไม่สำเร็จหรือยังไม่ตั้งค่า SMTP");
        }
        if (!string.IsNullOrWhiteSpace(contact.LineUserId))
        {
            if (_line == null) errors.Add("LINE: ระบบ LINE ไม่พร้อม");
            else if (await _line.TryPushToUserAsync(companyId, contact.LineUserId.Trim(), text)) return BookingSendResult.Ok("line");
            else errors.Add("LINE: ส่งไม่สำเร็จหรือยังไม่ตั้งค่า channel token");
        }
        var err = string.Join(" · ", errors);
        _logger.LogWarning("ส่งข้อความการจองไม่สำเร็จ (company {CompanyId}): {Error}", companyId, err);
        return BookingSendResult.Fail(err);
    }
}
