using System.Globalization;
using Accounting.Data;
using Accounting.Models.Constants;
using Accounting.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 260-3 (ต่อ 259-1-9 · คำตัดสิน 260-3-3/260-3-6) — <b>กระดิ่ง "เงินรอจัดสรรค้างเกิน 30 วัน"</b> (<see cref="NotificationEvents.CustomerCreditAged"/>)
/// <para>นิยาม "ค้างนาน" ไม่มีที่นี่ — อ่านจาก <see cref="CustomerCreditWorkQueue.AgedQuery"/> ตัวเดียวกับ issue <c>CustomerCreditAged</c> ของ Autopilot ·
/// ที่นี่ตัดสินแค่ "แถวไหนยังไม่เคยแจ้ง" (<c>Payment.UnappliedAgedNoticeAt</c> — กรองในฝั่ง SQL ก่อนนับ · ฝ่ายค้าน M2)</para>
/// <para>แจ้ง<b>หนึ่งรายการต่อบริษัทต่อรอบ</b> (จำนวน/ยอดรวมนับครบด้วย SQL — ไม่ตัดเงียบ) แล้วประทับแถวชุดเดียวกัน ⇒ การรับชำระหนึ่งรายการถูกแจ้งครั้งเดียว ·
/// <b>ประทับเฉพาะเมื่อเครื่องแจ้งเตือนรายงานว่าส่งแล้ว หรือบริษัทตั้งปิดคีย์นี้เอง</b> (<see cref="ShouldStamp"/> · ฝ่ายค้าน M1) — ส่งล้ม/ไม่มีผู้รับ/ไม่รู้ผล = ไม่ประทับ
/// (รอบถัดไปแจ้งใหม่) · tenant: ทุก query ผูก CompanyId</para>
/// </summary>
public static class CustomerCreditAgedNotice
{
    public const string ActionUrl = "/pages/customer-credits.html";

    public sealed record Summary(int Count, decimal Total, string Title, string Message);

    /// <summary>pure — สรุปจากตัวนับ SQL · ไม่มีแถว = null (ไม่ส่ง)</summary>
    public static Summary? Compose(int count, decimal total, string? oldestNumber, DateTime? oldestDate)
    {
        if (count <= 0) return null;
        var money = total.ToString("#,##0.00", CultureInfo.InvariantCulture);
        var title = $"เงินรอจัดสรรค้างเกิน {CustomerCreditWorkQueue.AgingDays} วัน {count:#,##0} รายการ (฿{money})";
        var message = (oldestNumber == null ? "" : $"เก่าสุด {oldestNumber} รับเมื่อ {ThaiDate.ToThaiDisplayString(oldestDate!.Value)} · ")
            + "จัดสรรให้ใบ · ออกใบส่วนที่ขาด · ย้ายเป็นมัดจำ · หรือคืนเงิน ที่หน้า “เงินรอจัดสรร”";
        return new Summary(count, total, title, message);
    }

    /// <summary>pure — ประทับ "แจ้งแล้ว" ไหม: ส่งแล้ว หรือบริษัทตั้งปิดคีย์เอง (ตั้งใจ · 260-3-3) · อื่น ๆ (ล้ม · ไม่มีผู้รับ · ไม่รู้ผล) = ไม่ประทับ</summary>
    private static bool ShouldStamp(NotificationDispatchOutcome outcome)
    {
        return outcome is NotificationDispatchOutcome.Delivered or NotificationDispatchOutcome.SuppressedByCompany;
    }

    /// <summary>รอบเดียวของทุกบริษัท (ผู้เรียก: <c>OverdueDunningJob</c> ใต้ <c>JobLock</c>) · คืนจำนวนบริษัทที่ประทับ</summary>
    public static async Task<int> RunAsync(AccountingDbContext db, INotificationEngine notify, DateTime nowUtc, ILogger? log, CancellationToken ct = default)
    {
        // ตัวกรองกว้าง (หาบริษัทที่อาจมีแถวต้องแจ้ง) — เกณฑ์อายุจริงอยู่ใน AgedQuery เท่านั้น
        var companyIds = await db.Payments.AsNoTracking()
            .Where(p => !p.IsDeleted && p.UnappliedAccountId != null && p.UnappliedAgedNoticeAt == null
                && p.UnappliedAmount > UnappliedCreditBalance.Tolerance)
            .Select(p => p.CompanyId).Distinct().ToListAsync(ct);
        var today = ThaiDate.CalendarDateUtc(nowUtc).Date;
        var stamped = 0;
        foreach (var companyId in companyIds)
        {
            if (ct.IsCancellationRequested) break;
            // ชุดแถวตายตัวของรอบนี้: สร้างก่อนเวลาเริ่ม ⇒ แถวที่นับ = แถวที่ประทับ
            var pending = CustomerCreditWorkQueue.AgedQuery(db, companyId, today, onlyUnnotified: true).Where(p => p.CreatedAt <= nowUtc);
            var count = await pending.CountAsync(ct);
            if (count == 0) continue;
            var total = await pending.SumAsync(p => p.UnappliedAmount, ct);
            var oldest = await pending.OrderBy(p => p.PaymentDate).ThenBy(p => p.PaymentNumber)
                .Select(p => new { p.PaymentNumber, p.PaymentDate }).FirstAsync(ct);
            var summary = Compose(count, total, oldest.PaymentNumber, oldest.PaymentDate)!;
            var outcome = await notify.DispatchWithOutcomeAsync(companyId, NotificationEvents.CustomerCreditAged, new NotificationContext
            {
                Title = summary.Title,
                Message = summary.Message,
                ActionUrl = ActionUrl,
                EntityType = "CustomerCredit",
            });
            if (!ShouldStamp(outcome))
            {
                log?.LogWarning("กระดิ่งเงินรอจัดสรรค้างนานของบริษัท {CompanyId} ไม่ถูกส่ง ({Outcome}) — ไม่ประทับ รอบถัดไปแจ้งใหม่", companyId, outcome);
                continue;
            }
            await pending.ExecuteUpdateAsync(s => s.SetProperty(p => p.UnappliedAgedNoticeAt, (DateTime?)nowUtc), ct);
            stamped++;
        }
        return stamped;
    }
}
