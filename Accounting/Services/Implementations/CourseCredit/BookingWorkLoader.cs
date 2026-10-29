using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 209 · P5 — ตัวอ่านข้อมูลเดียวของ "งานจากการจอง" ที่รายงานผู้ให้บริการ (<c>CmsBookingService.GetProviderReportAsync</c>) และฐานคอมมิชชัน/ค่าตอบแทน
/// (<c>CommissionService</c>) ใช้ร่วมกัน — มูลค่าต่อการจองผ่าน <see cref="BookingServiceValue"/> ตัวเดียว (ห้ามคำนวณซ้ำที่ผู้เรียก) ·
/// ทุกคิวรีกรอง <c>CompanyId</c> · อ่านอย่างเดียว
/// </summary>
internal static class BookingWorkLoader
{
    /// <summary>รายได้ที่รับรู้จากบัตรคอร์สต่อการจอง CMS (เฉพาะการจองที่จ่ายด้วยบัตร — ความจริงอยู่ที่ <c>CreditClassBooking.ExternalRef</c>) ·
    /// การจองที่ไม่อยู่ในผล = จ่ายปกติ · แถวที่ยกเลิก (เลื่อนนัด/คืนเครดิต) ไม่นับยอด</summary>
    public static async Task<Dictionary<Guid, decimal>> CreditRecognizedAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> bookingIds,
        CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, decimal>();
        if (bookingIds.Count == 0) return result;
        var refs = bookingIds.Select(CreditBookingLink.Ref).ToList();
        var rows = await db.CreditClassBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && b.ExternalRef != null && refs.Contains(b.ExternalRef))
            .Select(b => new { b.ExternalRef, b.Status, b.RecognizedAmount })
            .ToListAsync(ct);
        foreach (var g in rows.GroupBy(r => r.ExternalRef!))
            if (Guid.TryParse(g.Key.Substring(CreditBookingLink.RefPrefix.Length), out var id))
                result[id] = g.Where(x => x.Status != CreditBookingStatus.Cancelled).Sum(x => x.RecognizedAmount);
        return result;
    }

    /// <summary>การจองที่ปิดบิลที่ POS แล้ว — คีย์ดีดซ้ำ = <c>PosOrder.SourceBookingId</c> (ตัวจริงของ "เปิดบิลแล้ว" + unique index) สถานะเสร็จ/คืน
    /// (ตัวเดียวกับที่ <c>PosService.LoadStaffSalesInputAsync</c> นับเข้าฐาน "ยอดที่ช่างทำบน POS") · บิลที่ยกเลิก = ไม่นับว่าปิดที่ POS</summary>
    public static async Task<HashSet<Guid>> PosBilledAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> bookingIds, CancellationToken ct = default)
    {
        if (bookingIds.Count == 0) return new HashSet<Guid>();
        var ids = bookingIds.ToList();
        var rows = await db.PosOrders.AsNoTracking()
            .Where(o => o.CompanyId == companyId && !o.IsDeleted && o.SourceBookingId != null && ids.Contains(o.SourceBookingId.Value)
                && (o.Status == PosOrderStatus.Completed || o.Status == PosOrderStatus.Refunded))
            .Select(o => o.SourceBookingId!.Value).ToListAsync(ct);
        return rows.ToHashSet();
    }

    public sealed record ResourceWork(Guid ResourceId, InstructorWorkBooking Work);

    /// <summary>งานที่เกิดจริง (เสร็จ/ไม่มา) ของผู้ให้บริการชุด <paramref name="resourceIds"/> ในช่วงวันที่ [from, toExclusive) — แยกต่อผู้ให้บริการ × การจอง ·
    /// % งานจาก <c>SiteBookingAssignment</c> (รอบ 207) · ไม่มีการมอบหมาย ⇒ ผู้ให้บริการหลักของการจอง 100% · มูลค่าก่อน VAT (<see cref="BookingServiceValue.ForCommission"/>)</summary>
    public static async Task<List<ResourceWork>> LoadAsync(AccountingDbContext db, Guid companyId, IReadOnlyCollection<Guid> resourceIds,
        DateTime from, DateTime toExclusive, Guid? siteId, decimal vatRatePercent, CancellationToken ct = default)
    {
        var list = new List<ResourceWork>();
        if (resourceIds.Count == 0) return list;
        var res = resourceIds.ToList();
        var f = from.Date; var t = toExclusive.Date;
        var assignedIds = db.SiteBookingAssignments.Where(a => a.CompanyId == companyId && !a.IsDeleted && res.Contains(a.ResourceId)).Select(a => a.BookingId);
        var bookings = await db.SiteBookings.AsNoTracking()
            .Where(b => b.CompanyId == companyId && (siteId == null || b.SiteId == siteId) && b.BookingDate >= f && b.BookingDate < t
                && (b.Status == BookingStatus.Completed || b.Status == BookingStatus.NoShow)
                && ((b.ResourceId != null && res.Contains(b.ResourceId.Value)) || assignedIds.Contains(b.Id)))
            .Select(b => new { b.Id, b.BookingServiceId, b.BookingDate, b.StartTime, b.Status, b.GuestCount, b.TotalAmount, b.ResourceId })
            .ToListAsync(ct);
        var ids = bookings.Select(b => b.Id).ToList();
        var assigns = await db.SiteBookingAssignments.AsNoTracking()
            .Where(a => a.CompanyId == companyId && !a.IsDeleted && ids.Contains(a.BookingId))
            .Select(a => new { a.BookingId, a.ResourceId, a.SharePercent }).ToListAsync(ct);
        var credit = await CreditRecognizedAsync(db, companyId, ids, ct);
        var pos = await PosBilledAsync(db, companyId, ids, ct);
        foreach (var b in bookings)
        {
            var parts = assigns.Where(a => a.BookingId == b.Id).Select(a => (a.ResourceId, a.SharePercent)).ToList();
            if (parts.Count == 0 && b.ResourceId is Guid rid) parts.Add((rid, 100m));
            var value = BookingServiceValue.ForCommission(b.TotalAmount, credit.TryGetValue(b.Id, out var cv) ? cv : null, vatRatePercent);
            foreach (var p in parts.Where(p => res.Contains(p.ResourceId)))
                list.Add(new ResourceWork(p.ResourceId, new InstructorWorkBooking(b.Id, b.BookingServiceId, b.BookingDate, b.StartTime.ToString("HH:mm"),
                    b.Status, Math.Max(1, b.GuestCount), value, p.SharePercent, pos.Contains(b.Id))));
        }
        return list;
    }
}
