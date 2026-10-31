using Accounting.Data;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 ส่วน B ฝ่ายค้าน (S2) — ล็อก advisory ระดับ session รอบงานหนึ่งชิ้น โดย<b>ไม่เปิดธุรกรรม</b>
/// (งานข้างในเปิด/ปิดธุรกรรมของตัวเองได้ — ใช้กับเส้นที่ห่อด้วย <c>pg_advisory_xact_lock</c> ไม่ได้เพราะซ้อนธุรกรรมไม่ได้) ·
/// เปิด connection ค้างไว้ตลอดงาน ⇒ ล็อกและการปลดอยู่ session เดียวกัน · re-entrant ใน session เดียว (PostgreSQL)
/// <para>ใช้กันจ่ายซ้อนสองทาง: แอดมินอนุมัติสลิป ↔ เงินออนไลน์เข้า บนรายการชำระค่าแพ็กเกจ/ส่วนเสริมเดียวกัน</para>
/// </summary>
public static class SessionAdvisoryLock
{
    public const string PlatformSubscriptionPayment = "platform-sub-payment";
    public const string PlatformAddOnPayment = "platform-addon-payment";

    public static async Task<T> RunAsync<T>(AccountingDbContext db, long key, Func<Task<T>> work, CancellationToken ct = default)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock({0})", new object[] { key }, ct);
            try { return await work(); }
            finally { await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock({0})", new object[] { key }, CancellationToken.None); }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    public static Task RunAsync(AccountingDbContext db, long key, Func<Task> work, CancellationToken ct = default)
        => RunAsync<bool>(db, key, async () => { await work(); return true; }, ct);
}
