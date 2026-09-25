using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Accounting.Helpers;

/// <summary>
/// ถอยสิ่งที่ "ขั้นที่ล้ม" ทิ้งไว้ใน change tracker <b>โดยไม่แตะ entity ของผู้เรียก</b> (รอบ 194 M1 ก + R2) — แทน <c>ChangeTracker.Clear()</c>
/// ซึ่งปลด entity ของผู้เรียกทิ้งด้วย (DbContext ใช้ร่วมใน scope เดียว ⇒ หมายเหตุที่ที่พักเขียนลงการจองหลังจากนั้นไม่ถูกบันทึก)
/// <para>ส่วนนี้ไม่แตะฐานข้อมูล (ผู้เรียกอ่านค่าจริงจาก DB ใหม่ให้ entity ที่คืนมาเอง — <c>ReloadAsync</c>) ⇒ เทสต์ได้โดยไม่ต้องมี DB</para>
/// <para>R2 ("RevertTrackedChangesSinceAsync คืน collection ได้ แต่ reference navigation ของ entity เดิมที่ชี้ entity ใหม่ไม่ถูกตัด"):
/// ① <b>DetectChanges ก่อน</b> — entity ใหม่ที่เพิ่งถูกผูกผ่าน navigation แต่ยังไม่ถูกตรวจพบ จะถูกดึงกลับเป็นแถวใหม่ตอน SaveChanges ของขั้นล้มดัง
/// ถ้าไม่ถูกตรวจพบ "ก่อน" ปลด · ② ปิด auto-detect ระหว่างถอย (เดิมการเรียก <c>Entries()</c> รอบสองตรวจ graph ใหม่ขณะของที่ปลดแล้วยังค้างใน
/// collection) · ③ ตัด collection และ reference ฝั่ง principal ที่ชี้ของที่ปลดแล้ว (ฝั่ง dependent — FK อยู่บน entity เดิม — ถูกคืนด้วย reload) ·
/// ④ <see cref="DetachStrays"/> ตรวจซ้ำหลัง reload: ไม่มี entity นอกจุดตั้งต้นเหลือถูกติดตาม</para>
/// </summary>
public static class TrackedChangeRevert
{
    /// <summary>ปลด entity ที่ไม่อยู่ใน <paramref name="baseline"/> + ตัดการอ้างถึงมันจาก entity ที่คงอยู่ · คืน entry ที่คงอยู่ (ผู้เรียก reload ต่อ)</summary>
    public static List<EntityEntry> DetachSince(DbContext db, IReadOnlySet<object> baseline)
    {
        db.ChangeTracker.DetectChanges();
        var auto = db.ChangeTracker.AutoDetectChangesEnabled;
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        try
        {
            foreach (var e in db.ChangeTracker.Entries().ToList())
                if (!baseline.Contains(e.Entity)) e.State = EntityState.Detached;
            var kept = db.ChangeTracker.Entries().ToList();
            foreach (var e in kept)
            {
                foreach (var col in e.Collections)
                    if (col.CurrentValue is System.Collections.IList list)
                        for (var i = list.Count - 1; i >= 0; i--)
                            if (list[i] is { } item && !baseline.Contains(item)) list.RemoveAt(i);
                foreach (var r in e.References)
                    if (r.CurrentValue is { } target && !baseline.Contains(target)
                        && r.Metadata is INavigation nav && !nav.IsOnDependent)
                        r.CurrentValue = null;
            }
            return kept;
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = auto;
        }
    }

    /// <summary>ตรวจซ้ำหลังผู้เรียก reload: entity นอก <paramref name="baseline"/> ที่ถูกดึงกลับมาติดตาม (ผ่าน navigation ที่ยังค้าง) ⇒ ปลดอีกครั้ง ·
    /// คืนจำนวนที่ต้องปลด (0 = สะอาด — ผู้เรียก log เมื่อไม่ใช่ 0)</summary>
    public static int DetachStrays(DbContext db, IReadOnlySet<object> baseline)
    {
        var strays = db.ChangeTracker.Entries().Where(e => !baseline.Contains(e.Entity)).ToList();
        foreach (var e in strays) e.State = EntityState.Detached;
        return strays.Count;
    }
}
