namespace Accounting.Helpers;

/// <summary>
/// รอบ 259 (ฝ่ายค้าน 259 B-L5 · 259 C-M1 · คำตัดสิน 259-2-8) — ลิงก์ชำระของเอกสารถูก<b>เพิกถอน</b>โดยผู้มีสิทธิ์ (ด่าน Void) แล้วมีผู้เรียกที่
/// <b>ไม่ใช่การออกลิงก์โดยพนักงานโดยตรง</b> (หน้าต่างส่งเอง LINE/อีเมล · พอร์ทัลลูกค้า) ขอลิงก์ ⇒ ไม่ออกใหม่ทับ — โยนชนิดนี้
/// (<c>DocumentPayLinkService.EnsureLinkAsync</c> ตัวเดียว) · <see cref="Exception.Message"/> = ข้อความถึงพนักงาน ·
/// <see cref="CustomerMessage"/> = ข้อความถึงลูกค้าปลายทาง (พอร์ทัล — ไม่บอกรายละเอียดภายใน)
/// </summary>
public sealed class PayLinkRevokedException : BusinessRuleException
{
    public const string Code = "PAY-LINK-REVOKED";

    public const string StaffMessage =
        "ลิงก์ชำระของใบนี้ถูกเพิกถอนไว้ — การส่งเองไม่ออกลิงก์ใหม่ทับ (ออกใหม่ได้ที่ส่วนลิงก์ชำระของเอกสาร โดยผู้มีสิทธิ์) · ส่งโดยไม่ติ๊กลิงก์ชำระได้";

    public const string CustomerMessage = "ยังชำระออนไลน์สำหรับเอกสารนี้ไม่ได้ — กรุณาติดต่อผู้ขาย";

    public PayLinkRevokedException() : base(StaffMessage, Code, 409) { }
}
