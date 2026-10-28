namespace Accounting.Helpers;

/// <summary>อายุของ "รูปตัวอย่างที่ลูกค้าแนบในตะกร้า" ที่ไม่เคยกลายเป็นออเดอร์ — <b>ตัวตัดสินตัวเดียว</b>ของงานล้าง
/// <c>CartImageCleanupJob</c> (OWNER file · R-25 · POS_MTO_DESIGN §4 #8 · ฝ่ายค้าน K ข้อ 4)
///
/// ที่มา: รูปแนบตอนอยู่ในตะกร้าถูกเก็บเป็น <c>FileAttachment(EntityType="SiteOrder", EntityId=cartId)</c> แล้วย้ายเป็น orderId ตอนสั่งซื้อ ·
/// ตะกร้าที่ไม่ได้สั่ง ⇒ แถว+ไฟล์ค้างตลอดไป (ไม่มีใครลบ) · ตอนนี้งานเบื้องหลังลบไฟล์ที่ <c>EntityId</c> <b>ไม่ใช่ออเดอร์</b> และอายุเกิน N วัน
/// (ค่าตั้งแพลตฟอร์ม <c>Cms:CartImageRetentionDays</c> · ค่าเริ่มต้น 14) ผ่าน <c>IFileAttachmentService.DeleteAsync</c> ตัวเดียว
///
/// ทิศตรงข้าม (เกณฑ์เสร็จ W4): รูปของตะกร้าที่กลายเป็นออเดอร์ (<c>EntityId</c> อยู่ใน <c>SiteOrders</c>) <b>ไม่ถูกลบ</b> ไม่ว่าอายุเท่าไร —
/// เป็นหลักฐานประกอบรายการขาย · รูปที่ยังไม่ครบอายุก็ไม่ถูกลบ (ลูกค้าอาจกลับมาสั่ง)</summary>
public static class CartImageRetention
{
    public const int DefaultRetentionDays = 14;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 365;

    /// <summary>จำนวนวันที่ใช้จริง — ค่าตั้งขาด/ผิดช่วง ⇒ ค่าเริ่มต้น (ห้ามให้ค่า 0/ติดลบทำให้ลบรูปของตะกร้าที่เพิ่งแนบ)</summary>
    public static int NormalizeDays(int? configured) =>
        configured is int d && d >= MinRetentionDays && d <= MaxRetentionDays ? d : DefaultRetentionDays;

    /// <summary>เวลาตัด — ไฟล์ที่ <c>CreatedAt</c> ก่อนเวลานี้ถือว่าหมดอายุ</summary>
    public static DateTime Cutoff(DateTime nowUtc, int retentionDays) => nowUtc.AddDays(-NormalizeDays(retentionDays));

    /// <summary>ไฟล์นี้ลบได้ไหม — ต้อง<b>ทั้ง</b> "ไม่ใช่ของออเดอร์" <b>และ</b> "เก่ากว่าเวลาตัด"</summary>
    public static bool ShouldDelete(DateTime createdAtUtc, bool belongsToOrder, DateTime cutoffUtc) =>
        !belongsToOrder && createdAtUtc < cutoffUtc;
}
