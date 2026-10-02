namespace Accounting.Helpers;

/// <summary>จำนวนแขก "ต่อห้อง" ของหน้าค้นหาห้องว่าง — ช่องค้นหาส่งยอด<b>รวมทุกห้อง</b> (ผู้ใหญ่ 4 · 2 ห้อง) แต่ด่านความจุและราคาต่อห้อง
/// ต้องเทียบกับคนที่ห้องหนึ่งรับจริง (รอบ 202 ทีม LW · ฝ่ายค้าน P1-3)
///
/// <para>ที่มา: <c>SearchAsync</c> เดิมใช้ <c>Math.Max(1, request.Adults)</c> (ยอดรวม) เทียบเพดาน<b>ต่อห้อง</b> ⇒ ผู้ใหญ่ 4 คน 2 ห้อง (ห้องละ 2)
/// ถูกบอกว่า "ห้องนี้รับผู้ใหญ่ได้สูงสุด 2 คน/ห้อง" ทั้งที่จองได้ และราคาต่อห้องคิดแขกเกินจากยอดรวม ⇒ แสดงแพงเกินจริง.
/// ปัดขึ้น = ห้องที่แน่นที่สุดเมื่อแบ่งเท่า ๆ กัน (ทิศปลอดภัย: ไม่บอกว่าจองได้ทั้งที่ห้องที่แน่นที่สุดรับไม่ได้) ·
/// ตัวแบ่งรายห้องจริงตอนจองอยู่ที่หน้าเว็บ (แขกตั้งเอง) และเซิร์ฟเวอร์ตัดสินซ้ำตอน quote (<c>LodgingOccupancy.RoomProblems</c>)</para></summary>
public static class LodgingSearchGuests
{
    /// <summary>ceil(ยอดรวม ÷ จำนวนห้อง) ไม่น้อยกว่า <paramref name="minEach"/> (ผู้ใหญ่ = 1 · เด็ก = 0) · ห้อง ≤ 0 ถือเป็น 1</summary>
    public static int PerRoom(int total, int rooms, int minEach)
    {
        var r = Math.Max(1, rooms);
        var t = Math.Max(0, total);
        return Math.Max(minEach, (t + r - 1) / r);
    }
}
