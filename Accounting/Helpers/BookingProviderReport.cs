using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>การจองหนึ่งรายการสำหรับรายงานผู้ให้บริการ — <see cref="Providers"/> ว่าง = ยังไม่มอบหมาย</summary>
public sealed record BookingProviderReportRow(
    Guid BookingId, string BookingNumber, DateTime BookingDate, string StartTime, string EndTime, int DurationMinutes,
    BookingStatus Status, Guid ServiceId, string ServiceName, string? CustomerName, decimal TotalAmount,
    IReadOnlyList<(Guid ResourceId, string Name, decimal SharePercent)> Providers,
    // รอบ 209 P5: จ่ายด้วยบัตรคอร์ส — TotalAmount ของแถวต้องเป็นรายได้ที่รับรู้จากบัตร (ผู้ประกอบส่งผ่าน BookingServiceValue.ForReport) · ป้าย "บัตร" ในรายการงาน
    bool PaidWithCredit = false);

public sealed record ProviderServiceLine(Guid ServiceId, string ServiceName, int Completed, int Minutes, decimal Revenue);
public sealed record ProviderDayLine(DateTime Date, int Completed, int Minutes, decimal Revenue);
public sealed record ProviderJobLine(Guid BookingId, string BookingNumber, DateTime BookingDate, string StartTime, string EndTime,
    string ServiceName, string? CustomerName, BookingStatus Status, decimal SharePercent, int ShareMinutes, decimal RevenueShare, string? CoProviders,
    bool PaidWithCredit = false);

public sealed record ProviderSummary(
    Guid? ResourceId, string Name,
    int Assigned, int Completed, int InProgress, int Upcoming, int NoShow, int Cancelled,
    int WorkedMinutes, int ShareMinutes, decimal RevenueShare, decimal AverageRevenuePerJob, decimal ShareOfTotalRevenuePercent,
    IReadOnlyList<ProviderServiceLine> Services, IReadOnlyList<ProviderDayLine> Days, IReadOnlyList<ProviderJobLine> Jobs);

public sealed record ProviderReportTotals(int Bookings, int Completed, int Unassigned, int WorkedMinutes, decimal Revenue);

/// <summary>
/// <b>ตัวประกอบเดียว</b>ของ "สรุปการทำงาน/รายงานผู้ให้บริการ" (รอบ 207 · คำขอเจ้าของข้อ 4) — pure (เทสต์ได้โดยไม่ต้องมีฐานข้อมูล)
/// <para>นิยาม: <b>รายได้</b> = ยอดการจองที่ <i>เสร็จสิ้น</i> แบ่งตาม % งาน (<see cref="BookingProviderShare.SplitAmount"/> — เศษลงคนหลัก ⇒ Σ ทุกคน = ยอดจริง) ·
/// <b>นาทีงาน</b> = ระยะเวลาการจองที่เสร็จ (เต็ม — ทุกคนที่ร่วมอยู่ตลอด) · <b>นาทีตามสัดส่วน</b> = ระยะเวลา × % ·
/// ยกเลิก/ไม่มา นับจำนวนเท่านั้น (ไม่มีรายได้) · การจองที่ยังไม่มอบหมายรวมในแถว "ยังไม่ระบุผู้ให้บริการ" (ไม่ซ่อน)</para>
/// <para>ยอดเป็นราคาการจอง (รวม VAT ตามราคาหน้าเว็บ) ไม่ใช่รายได้ทางบัญชี — รายได้ทางบัญชีอยู่ที่รายงานภาษีขาย/งบ ·
/// รอบ 209 P5 (R-209-C6): การจองที่จ่ายด้วยบัตรคอร์ส (ราคาการจอง 0) ⇒ ผู้ประกอบแถวส่ง<b>รายได้ที่รับรู้จากบัตร</b> (สุทธิก่อน VAT) ผ่าน
/// <see cref="BookingServiceValue.ForReport"/> ตัวเดียว — เดิมครูที่สอนคลาสบัตรล้วนได้ "0 บาท"</para>
/// </summary>
public static class BookingProviderReport
{
    public const string UnassignedName = "ยังไม่ระบุผู้ให้บริการ";

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static (IReadOnlyList<ProviderSummary> Providers, ProviderReportTotals Totals) Build(IReadOnlyList<BookingProviderReportRow> rows)
    {
        var acc = new Dictionary<Guid, Acc>();
        Acc Get(Guid? id, string name)
        {
            var key = id ?? Guid.Empty;
            if (!acc.TryGetValue(key, out var a)) acc[key] = a = new Acc(id, name);
            return a;
        }
        var totalRevenue = rows.Where(r => r.Status == BookingStatus.Completed).Sum(r => R2(r.TotalAmount));
        foreach (var r in rows)
        {
            var parts = r.Providers.Count == 0
                ? new List<(Guid? Id, string Name, decimal Share)> { (null, UnassignedName, 100m) }
                : r.Providers.Select(p => (Id: (Guid?)p.ResourceId, Name: p.Name, Share: p.SharePercent)).ToList();
            var completed = r.Status == BookingStatus.Completed;
            var split = completed && r.Providers.Count > 0
                ? BookingProviderShare.SplitAmount(r.TotalAmount, r.Providers.Select(p => new BookingProviderSharePart(p.ResourceId, p.SharePercent)).ToList())
                : null;
            foreach (var p in parts)
            {
                var a = Get(p.Id, p.Name);
                a.Assigned++;
                switch (r.Status)
                {
                    case BookingStatus.Completed: a.Completed++; break;
                    case BookingStatus.InProgress: a.InProgress++; break;
                    case BookingStatus.Pending or BookingStatus.Confirmed: a.Upcoming++; break;
                    case BookingStatus.NoShow: a.NoShow++; break;
                    case BookingStatus.Cancelled: a.Cancelled++; break;
                }
                var shareMinutes = (int)Math.Round(r.DurationMinutes * p.Share / 100m, MidpointRounding.AwayFromZero);
                var revenue = !completed ? 0m : split != null && p.Id is Guid pid ? split[pid] : R2(r.TotalAmount);
                if (completed)
                {
                    a.WorkedMinutes += r.DurationMinutes;
                    a.ShareMinutes += shareMinutes;
                    a.Revenue += revenue;
                    var s = a.Services.TryGetValue(r.ServiceId, out var sv) ? sv : (a.Services[r.ServiceId] = new SvcAcc(r.ServiceName));
                    s.Completed++; s.Minutes += r.DurationMinutes; s.Revenue += revenue;
                    var d = a.Days.TryGetValue(r.BookingDate.Date, out var dv) ? dv : (a.Days[r.BookingDate.Date] = new DayAcc());
                    d.Completed++; d.Minutes += r.DurationMinutes; d.Revenue += revenue;
                }
                var co = r.Providers.Count > 1
                    ? BookingProviderShare.Describe(r.Providers.Where(x => (Guid?)x.ResourceId != p.Id).Select(x => (x.Name, x.SharePercent)))
                    : null;
                a.Jobs.Add(new ProviderJobLine(r.BookingId, r.BookingNumber, r.BookingDate, r.StartTime, r.EndTime, r.ServiceName, r.CustomerName,
                    r.Status, p.Share, completed ? shareMinutes : 0, revenue, co, r.PaidWithCredit));
            }
        }
        var list = acc.Values
            .Select(a => new ProviderSummary(a.Id, a.Name, a.Assigned, a.Completed, a.InProgress, a.Upcoming, a.NoShow, a.Cancelled,
                a.WorkedMinutes, a.ShareMinutes, R2(a.Revenue),
                a.Completed == 0 ? 0m : R2(a.Revenue / a.Completed),
                totalRevenue == 0m ? 0m : R2(a.Revenue * 100m / totalRevenue),
                a.Services.Select(kv => new ProviderServiceLine(kv.Key, kv.Value.Name, kv.Value.Completed, kv.Value.Minutes, R2(kv.Value.Revenue)))
                    .OrderByDescending(x => x.Revenue).ThenBy(x => x.ServiceName, StringComparer.Ordinal).ToList(),
                a.Days.Select(kv => new ProviderDayLine(kv.Key, kv.Value.Completed, kv.Value.Minutes, R2(kv.Value.Revenue))).OrderBy(x => x.Date).ToList(),
                a.Jobs.OrderBy(j => j.BookingDate).ThenBy(j => j.StartTime, StringComparer.Ordinal).ToList()))
            // ผู้ให้บริการเรียงตามรายได้ · แถว "ยังไม่ระบุ" อยู่ท้ายเสมอ
            .OrderBy(x => x.ResourceId == null ? 1 : 0).ThenByDescending(x => x.RevenueShare).ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
        var totals = new ProviderReportTotals(rows.Count, rows.Count(r => r.Status == BookingStatus.Completed),
            rows.Count(r => r.Providers.Count == 0), rows.Where(r => r.Status == BookingStatus.Completed).Sum(r => r.DurationMinutes), totalRevenue);
        return (list, totals);
    }

    private sealed class Acc(Guid? id, string name)
    {
        public Guid? Id { get; } = id;
        public string Name { get; } = name;
        public int Assigned, Completed, InProgress, Upcoming, NoShow, Cancelled, WorkedMinutes, ShareMinutes;
        public decimal Revenue;
        public Dictionary<Guid, SvcAcc> Services { get; } = new();
        public Dictionary<DateTime, DayAcc> Days { get; } = new();
        public List<ProviderJobLine> Jobs { get; } = new();
    }

    private sealed class SvcAcc(string name)
    {
        public string Name { get; } = name;
        public int Completed, Minutes;
        public decimal Revenue;
    }

    private sealed class DayAcc
    {
        public int Completed, Minutes;
        public decimal Revenue;
    }
}
