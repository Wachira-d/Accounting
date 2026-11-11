namespace Accounting.Helpers;

/// <summary>
/// รอบ 258 (A-H3 · E-H1): กติการวมค่า "ประกาศเข้ากลุ่ม LINE ของบริษัท" ตอนบันทึกตารางแจ้งเตือน — ตัวตั้งตัวเดียว · pure
///
/// <para>═══ ที่มา ═══ หน้าตั้งค่า › การแจ้งเตือน (ตารางสำเนา 3 ช่อง) ส่งแถวที่ไม่มี <c>enableLineGroup</c> ⇒ DTO ตั้งต้น <c>false</c>
/// ⇒ เซิร์ฟเวอร์เขียน false ทับ ⇒ ประกาศเข้ากลุ่มทีมหายทุกเหตุการณ์โดยไม่มีอะไรบอก</para>
///
/// <para>═══ กติกา ═══ ช่องที่ผู้เรียก<b>ไม่ได้ส่ง</b> (null) = คงค่าที่ระบบใช้อยู่ ไม่ใช่ "ปิด":
/// แถวที่มีอยู่ ⇒ ค่าเดิมของแถว · แถวใหม่ของเหตุการณ์ที่มีแถวอื่นแล้ว ⇒ ตามแถวอื่นของเหตุการณ์ (ค่าเป็นต่อเหตุการณ์ เก็บบนทุกแถวบทบาท) ·
/// แถวใหม่ของเหตุการณ์ที่ยังไม่เคยตั้ง ⇒ ค่าตั้งต้นของระบบ (<see cref="NotificationAudiencePolicy.DefaultMatrix"/>) ·
/// ผู้เรียกที่ส่งค่ามา (true/false) ชนะเสมอ</para>
/// </summary>
public static class NotificationSettingMerge
{
    /// <param name="requested">ค่าที่ผู้เรียกส่ง (null = ไม่ได้ส่งช่องนี้)</param>
    /// <param name="storedOnRow">ค่าของแถวที่มีอยู่ (null = ยังไม่มีแถว)</param>
    /// <param name="eventHasRows">เหตุการณ์นี้มีแถวที่บันทึกไว้แล้วอย่างน้อยหนึ่งแถวไหม</param>
    /// <param name="anyStoredRowOfEventOn">แถวที่บันทึกไว้ของเหตุการณ์นี้มีแถวใดเปิดกลุ่มอยู่ไหม</param>
    /// <param name="systemDefaultOn">ค่าตั้งต้นของระบบสำหรับเหตุการณ์นี้ (เมื่อบริษัทยังไม่เคยตั้ง)</param>
    public static bool LineGroup(bool? requested, bool? storedOnRow, bool eventHasRows, bool anyStoredRowOfEventOn, bool systemDefaultOn)
        => requested ?? storedOnRow ?? (eventHasRows ? anyStoredRowOfEventOn : systemDefaultOn);
}
