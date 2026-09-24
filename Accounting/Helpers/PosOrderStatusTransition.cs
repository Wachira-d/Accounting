using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **การเปลี่ยนสถานะบิล POS ผ่านปุ่ม "สถานะ" ทั่วไป** (<c>POST pos/orders/{id}/status</c>) —
/// อนุญาตเฉพาะการสลับระหว่างสถานะที่ยังเปิดอยู่
///
/// ═══ ที่มา (รอบ 193 · E193-1) ═══
/// <c>UpdateOrderStatusAsync</c> เดิมเขียน <c>order.Status = request.Status</c> ตรง ๆ ⇒
/// <list type="bullet">
/// <item>ตั้ง <c>Completed</c> ได้โดย<b>ไม่ตัดสต็อก · ไม่ลง JE · ไม่ออกเลข §86/6 · ไม่ตรวจยอดชำระ</b></item>
/// <item>ตั้ง <c>Voided</c>/<c>Refunded</c> ได้โดยไม่กลับอะไรเลย</item>
/// <item>ดึงบิลที่ปิดแล้วกลับเป็น <c>Open</c> แล้วปิดซ้ำ = ตัดสต็อก + JE สองรอบ</item>
/// </list>
/// หน้าเว็บใช้ปุ่มนี้แค่ พักบิล/เรียกบิลคืน (OnHold ↔ Open) · สถานะปลายทางทุกตัวมีเส้นของตัวเอง
/// ที่ทำงานครบ (ปิดบิล · ยกเลิก · คืนเงิน) ⇒ <b>บล็อก</b> ดีกว่า "ส่งต่อ" เพราะการส่งต่อสร้างทางเข้าที่สอง
/// ของการปิดบิลซึ่งต้องคอยให้ตรงกับเส้นหลักตลอดไป (R5) และคำขอ "ยกเลิก/คืนเงิน" ต้องการข้อมูลที่
/// ปุ่มนี้ไม่มี (บรรทัด · วิธีจ่ายคืน)
/// </summary>
public static class PosOrderStatusTransition
{
    /// <summary>สถานะที่บิลยังเปิดอยู่ (ยังไม่มีสต็อก/JE ผูก)</summary>
    private static bool IsOpenState(PosOrderStatus s) => s is PosOrderStatus.Open
        or PosOrderStatus.InProgress or PosOrderStatus.ReadyToServe or PosOrderStatus.OnHold;

    /// <summary>null = เปลี่ยนได้ · ไม่ null = ข้อความไทยบอกเหตุผล + ทางไปต่อ</summary>
    public static string? Check(PosOrderStatus from, PosOrderStatus to)
    {
        if (!Enum.IsDefined(to))
            return $"สถานะ \"{(int)to}\" ไม่มีในระบบ";
        if (from == to) return null;
        if (!IsOpenState(from))
            return from switch
            {
                PosOrderStatus.Completed => "บิลนี้ปิดแล้ว — เปลี่ยนสถานะไม่ได้ (ใช้ \"ยกเลิกบิล\" หรือ \"คืนเงิน\")",
                PosOrderStatus.Voided => "บิลนี้ถูกยกเลิกแล้ว — เปลี่ยนสถานะไม่ได้",
                _ => "บิลนี้คืนเงินครบแล้ว — เปลี่ยนสถานะไม่ได้",
            };
        if (!IsOpenState(to))
            return to switch
            {
                PosOrderStatus.Completed => "ปิดบิลด้วยปุ่ม \"ชำระเงิน/ปิดบิล\" — การปิดบิลต้องตรวจยอดชำระ ตัดสต็อก และลงบัญชีพร้อมกัน",
                PosOrderStatus.Voided => "ยกเลิกบิลด้วยปุ่ม \"ยกเลิกบิล\" — เพื่อให้คืนสต็อกและกลับรายการบัญชีครบ",
                _ => "คืนเงินด้วยปุ่ม \"คืนเงิน\" — ต้องระบุรายการและวิธีจ่ายคืน",
            };
        return null;
    }
}
