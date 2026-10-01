using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// **เจ้าของการรับชำระ = รอบโอน settlement — คอลัมน์ <c>Payment.SettlementBatchId</c> ตัวเดียว** (รอบ 201 ทีม ST · A-ST1 · ฝ่ายค้าน T C-18 / V2 S3-11(5))
///
/// <para>═══ ที่มา ═══ ตัวหาของกำพร้า · ด่านยกเลิกการรับชำระทีละรายการ (<see cref="SettlementArtifactGuard"/>) · ชิ้นที่ออกแล้วของรอบ · รับชำระข้ามรอบ
/// เคยอ่านป้าย <c>[SETTLEMENT:{id}]</c> จาก <c>Payment.Notes</c> — ข้อความที่เส้นรับชำระทั่วไป/API รับจากผู้ใช้ ⇒ (ก) ปลอมป้ายของรอบโอนที่ยกเลิกแล้ว =
/// การรับชำระธรรมดากลายเป็น "ของกำพร้า" บล็อกช่องทาง (ข) การรับชำระของรอบโอนที่ไม่มีป้ายตรงต้นข้อความถูกตัดสินต่างกันในแต่ละผู้อ่าน · ตอนนี้ผู้อ่านทุกตัว
/// อ่านคอลัมน์ · ป้ายใน Notes เป็นข้อความให้คนอ่านอย่างเดียว (<c>tools/required_call_site_check.py</c> ห้ามอ่านป้ายจาก Notes นอก migration)</para>
///
/// <para>═══ ผู้เขียนตัวเดียว · ธุรกรรมเดียวกับการสร้างแถว ═══ การรับชำระถูกสร้างใน <c>DocumentService.CreatePaymentAsync</c> (ธุรกรรมของมันเอง · F4 ข้อ 7 ห้ามขยาย)
/// ⇒ ผู้ลงบัญชีรอบโอนเปิด <see cref="StampOnSave"/> ครอบการเรียก: ตัวฟัง <c>SavingChanges</c> ของ DbContext (scoped ต่อคำขอ) ประทับรอบโอนลงแถว
/// <c>Payment</c> ที่ "กำลังถูกเพิ่ม" ของใบนั้น <b>ใน SaveChanges เดียวกับที่ INSERT</b> ⇒ ไม่มีช่วงที่แถวเกิดแล้วแต่ยังไม่มีเจ้าของ (ล้มกลางทาง = ไม่มีทั้งคู่) ·
/// ไม่เพิ่มช่องใน <c>CreatePaymentRequest</c> (DTO ที่ controller/API bind จาก body ⇒ ผู้ใช้จะตั้งเจ้าของเองได้ = ช่องโหว่เดิมในอีกรูป)</para>
/// </summary>
public static class SettlementPaymentOwner
{
    /// <summary>
    /// ประทับรอบโอนลงการรับชำระที่กำลังถูกเพิ่มของใบ <paramref name="documentId"/> — แถวที่มีเจ้าของแล้วไม่ถูกแตะ (ห้ามย้ายเจ้าของ) · แถวของใบอื่นไม่ถูกแตะ ·
    /// คืนจำนวนแถวที่ประทับ · pure (เทสต์ <c>SettlementRound201StTests</c>)
    /// </summary>
    internal static int StampAdded(IEnumerable<Payment> added, Guid batchId, Guid documentId)
    {
        var n = 0;
        foreach (var p in added)
        {
            if (p.DocumentId != documentId || p.SettlementBatchId != null) continue;
            p.SettlementBatchId = batchId;
            n++;
        }
        return n;
    }

    /// <summary>
    /// เปิดขอบเขต "การรับชำระที่ถูกเพิ่มของใบนี้ระหว่างนี้เป็นของรอบโอนนี้" — <c>using</c> ครอบการเรียก <c>CreatePaymentAsync</c> ของผู้ลงบัญชีรอบโอน<b>เท่านั้น</b> ·
    /// <c>Dispose</c> ถอดตัวฟัง (ไม่ค้างข้ามการเรียก · DbContext เป็น scoped ต่อคำขอ ⇒ ไม่ใช่สถานะข้ามคำขอ — CLAUDE.md #4 D)
    /// </summary>
    public static IDisposable StampOnSave(AccountingDbContext db, Guid batchId, Guid documentId)
    {
        EventHandler<SavingChangesEventArgs> handler = (_, _) =>
            StampAdded(db.ChangeTracker.Entries<Payment>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList(),
                batchId, documentId);
        db.SavingChanges += handler;
        return new Detach(() => db.SavingChanges -= handler);
    }

    private sealed class Detach : IDisposable
    {
        private Action? _undo;
        public Detach(Action undo) => _undo = undo;
        public void Dispose()
        {
            _undo?.Invoke();
            _undo = null;
        }
    }
}
