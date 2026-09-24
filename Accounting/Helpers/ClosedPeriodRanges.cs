using System.Linq.Expressions;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ช่วงวันที่ของงวดที่ปิดแล้ว — ครึ่งเปิด [<paramref name="Start"/>, <paramref name="EndExclusive"/>) ระดับวัน</summary>
public readonly record struct ClosedDateRange(DateTime Start, DateTime EndExclusive);

/// <summary>
/// **"วันที่นี้อยู่ในงวดบัญชีที่ปิดแล้วไหม"** — แปลงแถว <c>FiscalPeriod</c> เป็นช่วงวันที่ แล้วสร้างตัวกรองที่ EF แปลเป็น SQL ได้
/// (EF แปล <see cref="NotInClosed{T}"/> เป็น <c>NOT (d &gt;= s AND d &lt; e) AND …</c>)
///
/// <para>กติกาเดียวกับที่ระบบใช้อยู่ (<c>AccountingService.ValidateFiscalPeriodOpenForDateAsync</c> ·
/// <c>BankService.EnsureFiscalPeriodOpenAsync</c> · <c>IntegrationService.IsPeriodOpenAsync</c>):
/// งวดปิด = <c>Status ∈ {Closed, Locked}</c> · <b>ไม่มีแถวงวดครอบวันนั้น = ถือว่าเปิด</b> ·
/// เทียบระดับ<b>วัน</b> (EndDate ของงวดเก็บเป็นเที่ยงคืนของวันสุดท้าย — เวลา 15:00 ของวันสุดท้ายยังอยู่ในงวด)</para>
///
/// <para>ที่มา (คำตัดสินเจ้าของ รอบ 193 ข้อ 32): คิวรอตรวจเดิมตัดที่ "30 วันล่าสุด" ⇒ สแกนของงวดที่ยังไม่ปิดแต่เก่ากว่า
/// 30 วันหายจากคิวทั้งที่ยังต้องลงบัญชี · ขอบเขตที่ถูกคือ "ทุกใบในงวดที่ยังไม่ปิด"</para>
///
/// <para>G6: pure · ไม่มี I/O</para>
/// </summary>
public static class ClosedPeriodRanges
{
    /// <summary>สถานะที่นับว่า "ปิด"</summary>
    private static bool IsClosedStatus(FiscalPeriodStatus status)
        => status is FiscalPeriodStatus.Closed or FiscalPeriodStatus.Locked;

    /// <summary>รวมงวดที่ปิดเป็นช่วงที่ไม่ซ้อนกัน (งวดเดือนติดกันยุบเป็นช่วงเดียว ⇒ เงื่อนไข SQL สั้น)</summary>
    public static IReadOnlyList<ClosedDateRange> Build(
        IEnumerable<(DateTime StartDate, DateTime EndDate, FiscalPeriodStatus Status)> periods)
    {
        var ranges = periods
            .Where(p => IsClosedStatus(p.Status) && p.EndDate.Date >= p.StartDate.Date)
            .Select(p => new ClosedDateRange(p.StartDate.Date, p.EndDate.Date.AddDays(1)))
            .OrderBy(r => r.Start)
            .ToList();
        var merged = new List<ClosedDateRange>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].EndExclusive)
            {
                var last = merged[^1];
                merged[^1] = new ClosedDateRange(last.Start, r.EndExclusive > last.EndExclusive ? r.EndExclusive : last.EndExclusive);
            }
            else merged.Add(r);
        }
        return merged;
    }

    /// <summary>ตัวกรอง "ไม่อยู่ในงวดที่ปิด" สำหรับ <c>IQueryable.Where</c> — <paramref name="dateOf"/> ต้องเป็นนิพจน์ที่ EF แปลได้
    /// (เช่น <c>r =&gt; r.ExtractedDate ?? r.ProcessedAt ?? r.CreatedAt</c>) · ไม่มีงวดปิด ⇒ ผ่านทุกแถว</summary>
    public static Expression<Func<T, bool>> NotInClosed<T>(
        Expression<Func<T, DateTime>> dateOf, IReadOnlyList<ClosedDateRange> closed)
    {
        var p = dateOf.Parameters[0];
        Expression body = Expression.Constant(true);
        foreach (var r in closed)
        {
            var inRange = Expression.AndAlso(
                Expression.GreaterThanOrEqual(dateOf.Body, Expression.Constant(r.Start)),
                Expression.LessThan(dateOf.Body, Expression.Constant(r.EndExclusive)));
            body = Expression.AndAlso(body, Expression.Not(inRange));
        }
        return Expression.Lambda<Func<T, bool>>(body, p);
    }
}
