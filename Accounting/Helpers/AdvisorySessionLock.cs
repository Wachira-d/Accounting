using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// ล็อก advisory ระดับ session (รอบ 208) — สำหรับงานที่เปิดธุรกรรมของตัวเองหลายช่วง (เส้นเอกสาร/ขั้นเงินหลายขั้น) จึงใช้ <c>pg_advisory_xact_lock</c> ไม่ได้ ·
/// ลองซ้ำ 20 ครั้ง × 500ms แล้วล้มดังด้วยข้อความที่ผู้เรียกให้ · ล็อกซ้อนด้วยคีย์เดียวกันในการเชื่อมต่อเดียวกันได้ (PostgreSQL นับซ้อน — ปลดเท่าที่ได้)
/// <para>ผู้เรียก: เส้นสถานะการจอง/เสร็จสิ้น+รับชำระ/ลูกค้ายกเลิก (<c>CmsBookingService</c>) · เส้นสถานะออเดอร์ + ยืนยันรับเงินออเดอร์ (<c>CmsCommerceService.UpdateOrderStatusAsync</c> · <c>ConfirmPaymentAsync</c>)</para>
/// </summary>
public static class AdvisorySessionLock
{
    public static async Task<T> RunAsync<T>(AccountingDbContext db, long key, Func<Task<T>> body, string busyMessage, string ruleCode)
    {
        var conn = db.Database.GetDbConnection();
        var openedHere = conn.State != System.Data.ConnectionState.Open;
        if (openedHere) await conn.OpenAsync();
        var acquired = false;
        try
        {
            for (var attempt = 0; attempt < 20 && !acquired; attempt++)
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT pg_try_advisory_lock({key})";
                    acquired = Convert.ToBoolean(await cmd.ExecuteScalarAsync());
                }
                if (!acquired) await Task.Delay(500);
            }
            if (!acquired) throw new BusinessRuleException(busyMessage, ruleCode, 409);
            return await body();
        }
        finally
        {
            if (acquired)
            {
                await using var unlock = conn.CreateCommand();
                unlock.CommandText = $"SELECT pg_advisory_unlock({key})";
                await unlock.ExecuteScalarAsync();
            }
            if (openedHere) await conn.CloseAsync();
        }
    }
}
