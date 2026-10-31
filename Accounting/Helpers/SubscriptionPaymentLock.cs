using Accounting.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 เฟส 2 (ฝ่ายค้าน S3 · ประสานกับ part B S2) — <b>ล็อกเดียว</b>ของการตรวจ/อนุมัติรายการชำระค่าบริการ NextAcc
/// (<c>SubscriptionService.ReviewPaymentAsync</c> เรียกเป็นขั้นแรก) — ทุกทางเข้า (แอดมินกด · bulk · webhook/poll ของช่องทางออนไลน์ · แอดมินบันทึกรับเงิน)
/// เรียงกันต่อ <b>subscription</b> ข้ามเครื่อง (<c>pg_advisory_xact_lock</c> · คีย์ <see cref="AdvisoryLockKey.SubscriptionPaymentReview"/>) ⇒
/// webhook ซ้ำ + แอดมินกดพร้อมกัน ไม่อนุมัติสองครั้ง · สองรายการของใบเดียวไม่ต่ออายุซ้อน — ผู้เรียก <b>ต้องอ่านสถานะรายการใหม่หลังได้ล็อก</b>
/// <para>มีธุรกรรมอยู่แล้ว (ผู้เรียกเปิด) ⇒ ล็อกในธุรกรรมนั้น คืน null (ผู้เรียกเดิม commit) · ไม่มี ⇒ เปิดธุรกรรมใหม่แล้วคืนให้ผู้เรียก commit/dispose</para>
/// </summary>
public static class SubscriptionPaymentLock
{
    public static async Task<IDbContextTransaction?> AcquireAsync(AccountingDbContext db, Guid subscriptionId, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational()) return null;   // InMemory (เทสต์หน่วย) — ไม่มีล็อกให้ถือ
        IDbContextTransaction? own = null;
        if (db.Database.CurrentTransaction == null)
            own = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(AdvisoryLockKey.SubscriptionPaymentReview, subscriptionId.ToString("N")) }, ct);
        return own;
    }
}
