using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Accounting.Data;

/// <summary>
/// ประทับ audit hash chain <b>ตอน commit</b> (รอบ 201 ทีม PL · คำตัดสินข้อ 104 หลังฝ่ายค้าน PL-X1..X4) — ก่อน commit จริงภายในธุรกรรมเดียวกัน:
/// lock_timeout → ล็อกทุกบริษัทที่เกี่ยวเรียงคีย์ → ปลาย chain → Seal → INSERT (<see cref="AccountingDbContext.SealDeferredAuditAtCommitAsync"/>) ·
/// rollback/commit ล้ม ⇒ ทิ้งแถวที่รอ (ไม่มีร่องรอยของสิ่งที่ไม่เกิด)
///
/// <para>ทำไมตอน commit: ล็อก audit ต้องเป็นล็อก "สุดท้าย" ของทุกธุรกรรม (ลำดับกลาง ใบตัวเอง → ใบต้นทาง → เลข JE → audit · V1I-X1) — ถ้าล็อกตั้งแต่
/// SaveChanges แรก ธุรกรรมที่ขอเลข JE/ล็อกสินค้าทีหลังจะวนรอกับเส้นที่ขอของพวกนั้นก่อน (deadlock) และถือล็อกข้ามการเรียก HTTP ภายนอก</para>
///
/// <para>ลงทะเบียนใน <c>AccountingDbContext.OnConfiguring</c> (ทุกทางที่สร้าง context — เว็บ · job · เทสต์ DB) · ตัวเดียวทั้งระบบ (ไม่มี state ของตัวเอง)</para>
/// </summary>
public sealed class AuditChainCommitInterceptor : DbTransactionInterceptor
{
    public static readonly AuditChainCommitInterceptor Instance = new();

    private AuditChainCommitInterceptor() { }

    public override InterceptionResult TransactionCommitting(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        if (eventData.Context is AccountingDbContext db) db.SealDeferredAuditAtCommit(eventData.TransactionId, transaction.IsolationLevel);
        return result;
    }

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
        InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AccountingDbContext db) await db.SealDeferredAuditAtCommitAsync(eventData.TransactionId, transaction.IsolationLevel, cancellationToken);
        return result;
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (eventData.Context is AccountingDbContext db) db.DropDeferredAudit(eventData.TransactionId);
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AccountingDbContext db) db.DropDeferredAudit(eventData.TransactionId);
        return Task.CompletedTask;
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        if (eventData.Context is AccountingDbContext db) db.DropDeferredAudit(eventData.TransactionId);
    }

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is AccountingDbContext db) db.DropDeferredAudit(eventData.TransactionId);
        return Task.CompletedTask;
    }
}
