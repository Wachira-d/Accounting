namespace Accounting.Helpers;

/// <summary>
/// รอบ 254 WP-11 L2 — "LINE OA ของบริษัทพร้อมส่งถึงผู้ติดต่อไหม" ตัวเดียว (pure) · ผู้อ่าน: <c>ContactRecipientResolver.LoadLineAsync</c> (สถานะปลายทาง) ·
/// <c>EmailScheduleService.UpsertRuleAsync</c> (ด่านติ๊ก LINE) · <c>ContactLineBindingService</c> (ปุ่มลิงก์/QR ผูก LINE)
/// <para>พร้อม = เปิด LINE + Channel Access Token ถอดได้ (ใช้ push) + Channel Secret ถอดได้ (webhook ต้องตรวจลายเซ็นได้ ⇒ ผูก/ยกเลิก/รับรู้การบล็อกได้ —
/// ไม่มี secret ⇒ ผู้รับพิมพ์ "หยุด" แล้วระบบไม่รู้ = ส่งต่อทั้งที่ขอหยุด)</para>
/// </summary>
public static class CompanyLineReadiness
{
    public sealed record Result(bool Ready, string? Problem);

    public static Result Evaluate(bool lineEnabled, bool tokenUsable, bool secretUsable)
    {
        if (!lineEnabled) return new(false, "ยังไม่ได้เปิดใช้ LINE OA ของบริษัท");
        if (!tokenUsable) return new(false, "Channel Access Token ของ LINE OA ไม่มีหรือถอดรหัสไม่ได้ — กรอกใหม่");
        if (!secretUsable) return new(false, "Channel Secret ของ LINE OA ไม่มีหรือถอดรหัสไม่ได้ (ต้องใช้ตรวจ webhook เพื่อผูก/ยกเลิกการรับข้อความ) — กรอกใหม่");
        return new(true, null);
    }
}
