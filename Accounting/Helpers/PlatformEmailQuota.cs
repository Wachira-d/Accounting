using Accounting.Data;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวนับของเพดานอีเมลที่ส่งผ่าน SMTP ของ NextAcc — ตัวเดียว** (รอบ 240 · คำตัดสิน 240-6/240-9) · ตัวตัดสินอยู่ที่ <see cref="SignEmailThrottle"/> ·
/// ผู้เรียก: <c>SignRequestService.SendEmailAsync</c> (อีเมลขอเซ็น) · <c>DocumentEmailService.SendDocumentEmailAsync</c> (อีเมลเอกสารบนเส้นแพลตฟอร์ม)
/// <para>นับจาก <c>DocumentEmailLogs</c> (สถานะ Sent · ตั้งแต่ต้นวันเวลาไทย) — สองเครื่องส่งพร้อมกันอาจเกินได้ไม่กี่ฉบับ (ความเสี่ยงที่รับไว้ 240-8)</para>
/// </summary>
public static class PlatformEmailQuota
{
    public sealed record Snapshot(int SentToday, int Cap, bool IdentityVerified, DateTime DayStartUtc);

    public static async Task<Snapshot> ForCompanyAsync(AccountingDbContext db, IConfiguration? config, Guid companyId, DateTime nowUtc, CancellationToken ct = default)
    {
        var dayStart = SignEmailThrottle.BangkokDayStartUtc(nowUtc);
        var sent = await db.DocumentEmailLogs.AsNoTracking()
            .CountAsync(l => l.CompanyId == companyId && l.SenderMode == OnBehalfEmailSender.ModePlatform
                && l.Status == EmailLogStatus.Sent && l.CreatedAt >= dayStart, ct);
        var taxId = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.TaxId).FirstOrDefaultAsync(ct);
        var everPaid = await db.SubscriptionPayments.AsNoTracking()
            .AnyAsync(p => p.Status == SubscriptionPaymentStatus.Approved && p.Subscription.CompanyId == companyId, ct);
        var verified = SignEmailThrottle.IdentityVerified(ThaiTaxId.IsValid(taxId), everPaid);
        var cap = SignEmailThrottle.PlatformCapFor(SignEmailThrottle.EffectiveCap(config?[SignEmailThrottle.ConfigKey]), verified);
        return new Snapshot(sent, cap, verified, dayStart);
    }

    /// <summary>จำนวนอีเมลที่ส่งสำเร็จวันนี้ถึงผู้รับรายนี้ (ช่อง To/Cc/Bcc ใดก็ได้) — ของเอกสารนี้ / ทุกเอกสารของบริษัท</summary>
    public static async Task<(int SameDoc, int Any)> RecipientCountsAsync(AccountingDbContext db, Guid companyId, Guid documentId,
        string recipient, DateTime dayStartUtc, CancellationToken ct = default)
    {
        var like = "%" + recipient.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var q = db.DocumentEmailLogs.AsNoTracking()
            .Where(l => l.CompanyId == companyId && l.Status == EmailLogStatus.Sent && l.CreatedAt >= dayStartUtc
                && (EF.Functions.ILike(l.ToEmail, like) || EF.Functions.ILike(l.CcEmail ?? "", like) || EF.Functions.ILike(l.BccEmail ?? "", like)));
        return (await q.CountAsync(l => l.DocumentId == documentId, ct), await q.CountAsync(ct));
    }
}
