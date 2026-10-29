using Accounting.Data;
using Accounting.Helpers;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations.Cms;

/// <summary>แจ้งเตือนของหน้าเว็บผ่านช่องเดิม <see cref="IBookingNotifier"/> (รอบ 209 P3) — ผู้รับฝั่งร้านจากค่าตั้งของร้าน → อีเมลติดต่อเว็บ → อีเมลบริษัท ·
/// ไม่โยน: ทุกเหตุคืนเป็นข้อความ + log Error (ผู้เรียกประทับ <see cref="CmsNotificationText.FailureTag"/> บนรายการ)</summary>
public class CmsShopNotifier : ICmsShopNotifier
{
    private readonly AccountingDbContext _db;
    private readonly IBookingNotifier _channel;
    private readonly ILogger<CmsShopNotifier> _logger;

    public CmsShopNotifier(AccountingDbContext db, IBookingNotifier channel, ILogger<CmsShopNotifier> logger)
    {
        _db = db;
        _channel = channel;
        _logger = logger;
    }

    public async Task<string?> NotifyShopAsync(Guid companyId, Guid? siteId, CmsNotification message, bool orderEvent, CancellationToken ct = default)
    {
        try
        {
            string? configured = null, siteEmail = null;
            bool? notifyOnOrder = null;
            if (siteId is Guid sid)
            {
                var cfg = await _db.SiteCommerceConfigs.AsNoTracking()
                    .Where(c => c.SiteId == sid && c.CompanyId == companyId && !c.IsDeleted)
                    .Select(c => new { c.NotifyOnNewOrder, c.OrderNotificationEmails })
                    .FirstOrDefaultAsync(ct);
                configured = cfg?.OrderNotificationEmails;
                notifyOnOrder = cfg?.NotifyOnNewOrder;
                siteEmail = await _db.Sites.AsNoTracking().Where(s => s.Id == sid && s.CompanyId == companyId)
                    .Select(s => s.ContactEmail).FirstOrDefaultAsync(ct);
            }
            // ร้านปิดแจ้งเรื่องออเดอร์ไว้ ⇒ ไม่ส่ง (ตั้งใจ — ไม่ใช่ความล้มเหลว)
            if (orderEvent && !CmsNotificationText.ShopOrderNotifyEnabled(notifyOnOrder)) return null;
            var companyEmail = await _db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.Email).FirstOrDefaultAsync(ct);
            var recipients = CmsNotificationText.ShopRecipients(configured, siteEmail, companyEmail);
            if (recipients.Count == 0)
                return Fail(companyId, message, "ไม่มีอีเมลผู้รับฝั่งร้าน (ตั้งอีเมลแจ้งเตือนออเดอร์ของร้าน หรืออีเมลติดต่อของเว็บ/บริษัท)");
            var errors = new List<string>();
            foreach (var to in recipients)
            {
                var r = await _channel.SendAsync(companyId, new BookingContact(to, null), message.Subject, message.Text, ct);
                if (!r.Sent) errors.Add($"{to}: {r.Error}");
            }
            return errors.Count == 0 ? null : Fail(companyId, message, string.Join(" · ", errors));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งร้าน ({Subject}) ล้ม (company {CompanyId})", message.Subject, companyId);
            return "เกิดข้อผิดพลาดภายในระหว่างส่ง (ดูบันทึกระบบ)";
        }
    }

    public async Task<string?> NotifyCustomerAsync(Guid companyId, BookingContact contact, CmsNotification message, CancellationToken ct = default)
    {
        try
        {
            var r = await _channel.SendAsync(companyId, contact, message.Subject, message.Text, ct);
            return r.Sent ? null : Fail(companyId, message, r.Error ?? "ส่งไม่สำเร็จ");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "แจ้งลูกค้า ({Subject}) ล้ม (company {CompanyId})", message.Subject, companyId);
            return "เกิดข้อผิดพลาดภายในระหว่างส่ง (ดูบันทึกระบบ)";
        }
    }

    private string Fail(Guid companyId, CmsNotification message, string error)
    {
        _logger.LogError("แจ้งเตือนหน้าเว็บส่งไม่ถึง ({Subject}) company {CompanyId}: {Error}", message.Subject, companyId, error);
        return error;
    }
}
