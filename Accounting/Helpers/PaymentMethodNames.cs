using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>รอบ 249 — ชื่อวิธีชำระเงิน (ไทย) สำหรับข้อความถึงคน (แจ้งเตือน · ข้อความถึงลูกค้า) · ตารางเดียว
/// <para>ผู้เรียก: <see cref="PaymentNotificationText"/> · <c>CmsBookingService.PaymentMethodLabel</c> (เดิมเป็นตารางของตัวเอง — ย้ายมาเรียกที่นี่) ·
/// ข้อยกเว้นที่ตั้งใจ: <c>PosService.PaymentMethodThaiLabel</c> ("โอนธนาคาร") ถูกเขียนลงคำอธิบาย JE ขาย POS ที่ persist แล้ว และเส้นคืนเงินอ่านข้อความนั้นกลับ —
/// เปลี่ยนคำ = JE ใหม่/เก่าไม่ตรงกัน จึงไม่ยุบในรอบนี้</para></summary>
public static class PaymentMethodNames
{
    public static string Thai(PaymentMethod m) => m switch
    {
        PaymentMethod.Cash => "เงินสด",
        PaymentMethod.BankTransfer => "โอนเงิน",
        PaymentMethod.CreditCard => "บัตรเครดิต",
        PaymentMethod.Cheque => "เช็ค",
        PaymentMethod.PromptPay => "พร้อมเพย์",
        PaymentMethod.DirectDebit => "หักบัญชี",
        PaymentMethod.EWallet => "e-Wallet",
        _ => "อื่น ๆ",
    };
}
