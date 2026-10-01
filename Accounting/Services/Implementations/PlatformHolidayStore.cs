using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// ตัวอ่านตารางวันหยุดราชการของแพลตฟอร์ม (<c>PlatformHolidays</c>) — ผู้อ่านเชิงธุรกิจ: กำหนดยื่นในปฏิทินภาษี (<c>TaxCalendarService</c>) ·
/// สถานะ "เลยกำหนด" ของการนำส่ง (<c>StatutoryRemittanceService</c>) · รอบ 201 ทีม PL (B-9)
///
/// <para>อ่านได้ไม่ได้ (ตารางยังไม่ถูกสร้าง/ฐานสะดุด) ⇒ ชุดว่าง = พฤติกรรมเดิม (เสาร์/อาทิตย์) — ทิศที่มองเห็น (เตือนเร็ว) ไม่ใช่เตือนช้า ·
/// ไม่แคชข้าม request (ตารางเล็ก · ไม่มี state ข้ามเครื่อง — กฎเหล็ก #4 D)</para>
/// </summary>
public static class PlatformHolidayStore
{
    /// <summary>วันหยุดตั้งแต่ 1 ม.ค. <paramref name="fromYear"/> ถึง 31 ม.ค. ของปีถัดจาก <paramref name="toYear"/> (กำหนดยื่นงวด ธ.ค. ตกเดือนถัดไป)</summary>
    public static async Task<HashSet<DateTime>> LoadSetAsync(AccountingDbContext db, int fromYear, int toYear,
        ILogger? logger = null, CancellationToken ct = default)
    {
        var from = new DateTime(fromYear, 1, 1);
        var to = new DateTime(toYear + 1, 2, 1);
        try
        {
            var dates = await db.PlatformHolidays.AsNoTracking()
                .Where(h => !h.IsDeleted && h.Date >= from && h.Date < to)
                .Select(h => h.Date)
                .ToListAsync(ct);
            return Accounting.Helpers.BusinessDayCalendar.ToSet(dates);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ไม่มีตาราง = พฤติกรรมเดิม (ไม่ใช่เหตุให้ปฏิทิน/หน้านำส่งล้ม) — แต่ไม่เงียบ
            logger?.LogWarning(ex, "อ่านตารางวันหยุดราชการ (PlatformHolidays) ไม่ได้ — ใช้เสาร์/อาทิตย์อย่างเดียว");
            return new HashSet<DateTime>();
        }
    }
}
