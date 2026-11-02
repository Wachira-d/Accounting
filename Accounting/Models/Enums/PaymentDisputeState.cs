namespace Accounting.Models.Enums;

/// <summary>รอบ 228 (PLATFORM_OMISE_AUDIT §5.4): สถานะข้อพิพาท/chargeback ของรายการชำระเงิน (ภาษากลาง — adapter แปลงคำของเจ้าตัวเองมาเป็นค่านี้)</summary>
public enum PaymentDisputeState
{
    /// <summary>ผู้ถือบัตรเปิดข้อพิพาท — รอผู้ขายส่งหลักฐาน</summary>
    Open = 1,
    /// <summary>ส่งหลักฐานแล้ว/ธนาคารกำลังพิจารณา</summary>
    UnderReview = 2,
    /// <summary>ผู้ขายชนะ — เงินคงอยู่</summary>
    Won = 3,
    /// <summary>ผู้ขายแพ้ — เงินถูกดึงคืน (ผู้ให้บริการหักจากยอดโอน)</summary>
    Lost = 4,
    /// <summary>ปิดโดยไม่มีผล/ไม่รู้ผล — ให้คนตรวจที่แดชบอร์ดผู้ให้บริการ</summary>
    Closed = 5,
}
