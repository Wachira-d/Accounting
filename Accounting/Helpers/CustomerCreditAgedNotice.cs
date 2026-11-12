using System.Globalization;
using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 260-3 (ต่อ 259-1-9 · คำตัดสิน 260-3-3) — <b>กระดิ่ง "เงินรอจัดสรรค้างเกิน 30 วัน"</b> (<see cref="NotificationEvents.CustomerCreditAged"/>)
/// <para>นิยาม "ค้างนาน" ไม่มีที่นี่ — อ่านจาก <see cref="CustomerCreditWorkQueue.AgedAsync"/> ตัวเดียวกับ issue <c>CustomerCreditAged</c> ของ Autopilot
/// (ตัวเลขบนกระดิ่ง = รายการบนหน้า Autopilot) · ที่นี่ตัดสินแค่ "แถวไหนยังไม่เคยแจ้ง" (<c>Payment.UnappliedAgedNoticeAt</c>)</para>
/// <para>แจ้ง<b>หนึ่งรายการต่อบริษัทต่อรอบ</b> (สรุปจำนวน/ยอดรวม) แล้วประทับทุกแถวที่อยู่ในสรุป ⇒ การรับชำระหนึ่งรายการถูกแจ้งครั้งเดียว ไม่แจ้งซ้ำทุกวัน ·
/// ส่งไม่สำเร็จ = ไม่ประทับ (รอบถัดไปแจ้งใหม่) · tenant: ทุก query ผูก CompanyId</para>
/// </summary>
public static class CustomerCreditAgedNotice
{
    public const string ActionUrl = "/pages/customer-credits.html";

    public sealed record Summary(int Count, decimal Total, IReadOnlyList<Guid> PaymentIds, string Title, string Message);

    /// <summary>pure — แถวค้างนานที่ยังไม่เคยแจ้ง ⇒ สรุปหนึ่งรายการ · ไม่มี = null (ไม่ส่ง)</summary>
    public static Summary? Compose(IReadOnlyList<CustomerCreditWorkQueue.AgedRow> aged)
    {
        var pending = aged.Where(a => a.AgedNoticeAt == null).OrderBy(a => a.PaymentDate).ThenBy(a => a.PaymentNumber, StringComparer.Ordinal).ToList();
        if (pending.Count == 0) return null;
        var total = pending.Sum(a => a.Unapplied);
        var oldest = pending[0];
        var money = total.ToString("#,##0.00", CultureInfo.InvariantCulture);
        var title = $"เงินรอจัดสรรค้างเกิน {CustomerCreditWorkQueue.AgingDays} วัน {pending.Count} รายการ (฿{money})";
        var message = $"เก่าสุด {oldest.PaymentNumber} รับเมื่อ {ThaiDate.ToThaiDisplayString(oldest.PaymentDate)} · "
            + "จัดสรรให้ใบ · ออกใบส่วนที่ขาด · ย้ายเป็นมัดจำ · หรือคืนเงิน ที่หน้า “เงินรอจัดสรร”";
        return new Summary(pending.Count, total, pending.Select(a => a.PaymentId).ToList(), title, message);
    }

    /// <summary>รอบเดียวของทุกบริษัท (ผู้เรียก: <c>OverdueDunningJob</c> ใต้ <c>JobLock</c>) · คืนจำนวนบริษัทที่แจ้ง</summary>
    public static async Task<int> RunAsync(AccountingDbContext db, INotificationEngine notify, DateTime nowUtc, ILogger? log, CancellationToken ct = default)
    {
        // ตัวกรองกว้าง (หาบริษัทที่อาจมีแถวต้องแจ้ง) — เกณฑ์อายุจริงอยู่ใน AgedAsync เท่านั้น
        var companyIds = await db.Payments.AsNoTracking()
            .Where(p => !p.IsDeleted && p.UnappliedAccountId != null && p.UnappliedAgedNoticeAt == null
                && p.UnappliedAmount > UnappliedCreditBalance.Tolerance)
            .Select(p => p.CompanyId).Distinct().ToListAsync(ct);
        var today = ThaiDate.CalendarDateUtc(nowUtc).Date;
        var notified = 0;
        foreach (var companyId in companyIds)
        {
            if (ct.IsCancellationRequested) break;
            var summary = Compose(await CustomerCreditWorkQueue.AgedAsync(db, companyId, today, ct));
            if (summary == null) continue;
            try
            {
                await notify.DispatchAsync(companyId, NotificationEvents.CustomerCreditAged, new NotificationContext
                {
                    Title = summary.Title,
                    Message = summary.Message,
                    ActionUrl = ActionUrl,
                    EntityType = "CustomerCredit",
                });
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "แจ้งเงินรอจัดสรรค้างนานของบริษัท {CompanyId} ไม่สำเร็จ — รอบถัดไปแจ้งใหม่", companyId);
                continue;
            }
            var ids = summary.PaymentIds.ToList();
            await db.Payments.Where(p => p.CompanyId == companyId && ids.Contains(p.Id) && p.UnappliedAgedNoticeAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.UnappliedAgedNoticeAt, (DateTime?)nowUtc), ct);
            notified++;
        }
        return notified;
    }
}
