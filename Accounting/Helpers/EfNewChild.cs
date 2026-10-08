using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// ตัวเดียวของ "เพิ่มแถวลูกใหม่ใต้ parent ที่ EF ติดตามอยู่แล้ว" (บทเรียน 2026-10-08 · docs/lessons/general-design.md)
///
/// <para>สองกับดักที่ต้องหลบพร้อมกัน:</para>
/// <list type="number">
/// <item>ใส่ลงคอลเลกชันอย่างเดียว ⇒ <c>BaseEntity.Id</c> ตั้งไว้ก่อน ⇒ EF ตีเป็น Modified ⇒ UPDATE 0 แถว ⇒
/// <c>DbUpdateConcurrencyException</c> (บั๊ก "เพิ่มรายการเงินเดือน" 500)</item>
/// <item><c>DbSet.Add</c> แล้ว <c>List.Add</c> ซ้ำ ⇒ เมื่อแถวลูกตั้ง FK ไว้แล้ว relationship fixup ของ EF ใส่ลงคอลเลกชันให้เองแล้ว ⇒
/// List มีแถวเดียวกันสองครั้ง ⇒ ยอดที่ Sum จากคอลเลกชัน (ยอดรอบเงินเดือน · folio · ใบเบิก · 50 ทวิ · ภ.พ.30) <b>เบิ้ล</b>
/// (ฝ่ายค้านรอบ 2026-10-08 P1)</item>
/// </list>
/// ⇒ Add เข้า DbSet (สถานะ Added) แล้วใส่คอลเลกชัน <b>เฉพาะเมื่อยังไม่อยู่</b> (เทียบ reference) — ถูกทั้งกรณีตั้ง FK และไม่ตั้ง
/// </summary>
public static class EfNewChild
{
    public static T AddNewChild<T>(this DbContext db, ICollection<T> parentCollection, T row) where T : class
    {
        db.Set<T>().Add(row);
        if (!parentCollection.Any(x => ReferenceEquals(x, row)))
            parentCollection.Add(row);
        return row;
    }
}
