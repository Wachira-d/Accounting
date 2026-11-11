namespace Accounting.Helpers;

/// <summary>
/// รอบ 258 (A-M9 · A-H2): สิ่งที่หน้า "เชื่อมต่อ LINE" ของพนักงาน (<c>line-bind.html</c>) ต้องแสดง — เซิร์ฟเวอร์คำนวณ หน้าเว็บแสดงอย่างเดียว · pure
///
/// <para>เดิมหน้าเว็บฝังชื่อบอท "@nextacc-bot" ตายตัว + ชวน "สแกน QR" ที่ไม่มีรูป ⇒ ผู้ใช้หาบอทไม่เจอ/เพิ่มผิด OA
/// (LINE userId เป็นค่าต่อ provider — ผูกกับ OA อื่น = ส่งไม่ถึงเงียบ) · ชื่อจริงอยู่ที่ค่าตั้ง OA กลาง (<c>INextAccLineConfig</c>)</para>
/// </summary>
public static class StaffLineBindView
{
    /// <param name="BotReady">บอทกลางรับข้อความได้ไหม (มี token + secret) — ไม่พร้อม = ไม่ออกรหัส</param>
    /// <param name="BasicId">Basic ID ของ OA กลาง (รูป @xxxx) หรือ null เมื่อยังไม่ได้ตั้ง/รูปแบบผิด</param>
    /// <param name="AddFriendUrl">ลิงก์เพิ่มเพื่อน (เปิดบนคอมพิวเตอร์ = หน้า QR ของ LINE) หรือ null</param>
    /// <param name="Notice">ข้อความที่ต้องบอกผู้ใช้ (เหตุที่ยังผูกไม่ได้ / ยังไม่มีชื่อบอทให้ค้น) · null = ไม่มีอะไรต้องบอก</param>
    public sealed record View(bool BotReady, string? BasicId, string? AddFriendUrl, string? Notice);

    public const string NotReadyNotice =
        "ยังเชื่อมต่อ LINE ไม่ได้ — ผู้ดูแลระบบยังไม่ได้ตั้งค่า LINE ของ NextAcc (บอทยังรับข้อความไม่ได้) กรุณาแจ้งผู้ดูแลระบบ แล้วกลับมาขอรหัสใหม่";

    public const string NoBasicIdNotice =
        "ยังไม่มีชื่อบัญชี LINE ของ NextAcc ให้ค้นหา — ขอลิงก์เพิ่มเพื่อนจากผู้ดูแลระบบ แล้วส่งรหัสด้านบนในแชตนั้น";

    public static View From(bool botReady, string? centralBasicId)
    {
        var basic = CustomerLineLinkRule.NormalizeBasicId(centralBasicId);
        if (!botReady) return new View(false, basic, CustomerLineLinkRule.AddFriendUrl(basic), NotReadyNotice);
        return new View(true, basic, CustomerLineLinkRule.AddFriendUrl(basic), basic == null ? NoBasicIdNotice : null);
    }
}
