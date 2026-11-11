using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Services.Implementations;

/// <summary>
/// รอบ 259 แพ็ก B (คำตัดสิน 259-2-7) — บันทึกแถวคิว (อีเมล/LINE) ของรอบสแกนตามวันที่แบบ <b>idempotent</b>
/// <para>ที่มา (CI db-test `LineQueueRound254DbTests`): ด่านกันซ้ำของผู้เข้าคิว (<c>AnyAsync(BaseKey/IdempotencyKey)</c>) ถามฐานเท่านั้น ⇒
/// (ก) ผู้สแกนสองตัวพร้อมกัน (หลายเครื่อง/งานเบื้องหลังซ้อนกับปุ่ม) ผ่านด่านทั้งคู่แล้วชน unique <c>IX_LineQueues_Company_Idempotency</c> ·
/// (ข) ในรอบสแกนเดียว สองกฎ/สองวันที่ย้อนเก็บที่ได้กุญแจเดียวกัน (ใบ + trigger + offset) เพิ่มแถวซ้ำที่ยังไม่บันทึก — ทั้งรอบล้ม
/// (แถวของใบอื่นทั้งหมดในรอบนั้นหายไปด้วย · ลายน้ำไม่ขยับ ⇒ ล้มซ้ำทุกนาที)</para>
/// <para>กติกา: แถวที่ "มีแล้ว" (ในฐาน หรือซ้ำกันเองในรอบ) = ผลเดียวกับที่ต้องการ (กันซ้ำ) ⇒ ถอดแถวใหม่ที่ซ้ำออกแล้วบันทึกที่เหลือ · ข้อผิดพลาดอื่น/ซ้ำแต่หาไม่เจอ ⇒ โยนตามเดิม (ล้มดัง)</para>
/// </summary>
internal static class QueueRowSave
{
    private const int MaxAttempts = 3;

    /// <summary>SaveChanges · ชน unique ของกุญแจคิว ⇒ ถอดแถวใหม่ที่กุญแจมีอยู่แล้ว แล้วลองใหม่ (ไม่เกิน <see cref="MaxAttempts"/> ครั้ง) · คืนจำนวนแถวที่ถอด</summary>
    public static async Task<int> SaveSkippingExistingKeysAsync(AccountingDbContext db, CancellationToken ct)
    {
        var dropped = DropDuplicateAdded(db);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return dropped;
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                var n = await DropExistingAsync(db, ct);
                if (n == 0) throw;   // ชน unique อื่นที่ไม่ใช่กุญแจคิว ⇒ ไม่กลืน
                dropped += n;
            }
        }
    }

    /// <summary>แถวใหม่ที่กุญแจซ้ำกันเองในรอบเดียว (ยังไม่บันทึก) — เก็บตัวแรก ถอดที่เหลือ</summary>
    internal static int DropDuplicateAdded(AccountingDbContext db)
    {
        var dropped = 0;
        var seenLine = new HashSet<(Guid, string)>();
        foreach (var e in db.ChangeTracker.Entries<LineQueue>().Where(e => e.State == EntityState.Added).ToList())
            if (!seenLine.Add((e.Entity.CompanyId, e.Entity.IdempotencyKey))) { e.State = EntityState.Detached; dropped++; }
        var seenEmail = new HashSet<(Guid, string)>();
        foreach (var e in db.ChangeTracker.Entries<EmailQueue>().Where(e => e.State == EntityState.Added && e.Entity.IdempotencyKey != null).ToList())
            if (!seenEmail.Add((e.Entity.CompanyId, e.Entity.IdempotencyKey!))) { e.State = EntityState.Detached; dropped++; }
        return dropped;
    }

    /// <summary>แถวใหม่ที่กุญแจมีในฐานแล้ว (ผู้สแกนอีกตัวบันทึกไปก่อน) — ถอดออก · ทุก query ผูก CompanyId ของแถว</summary>
    private static async Task<int> DropExistingAsync(AccountingDbContext db, CancellationToken ct)
    {
        var dropped = 0;
        foreach (var e in db.ChangeTracker.Entries<LineQueue>().Where(e => e.State == EntityState.Added).ToList())
        {
            var (cid, key) = (e.Entity.CompanyId, e.Entity.IdempotencyKey);
            if (await db.LineQueues.AsNoTracking().AnyAsync(q => q.CompanyId == cid && q.IdempotencyKey == key && !q.IsDeleted, ct))
            { e.State = EntityState.Detached; dropped++; }
        }
        foreach (var e in db.ChangeTracker.Entries<EmailQueue>().Where(e => e.State == EntityState.Added && e.Entity.IdempotencyKey != null).ToList())
        {
            var (cid, key) = (e.Entity.CompanyId, e.Entity.IdempotencyKey);
            if (await db.EmailQueues.AsNoTracking().AnyAsync(q => q.CompanyId == cid && q.IdempotencyKey == key && !q.IsDeleted, ct))
            { e.State = EntityState.Detached; dropped++; }
        }
        return dropped;
    }
}
