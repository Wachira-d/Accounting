using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ที่พัก: วิธีชำระที่เงิน<b>เข้าบัญชีธนาคาร</b> (โอน · พร้อมเพย์) ต้องระบุบัญชีธนาคารที่รับจริง — ตัวตัดสินเดียวของเซิร์ฟเวอร์ (ฝ่ายค้านรอบ 2 P2(g) · 2026-10-14)
///
/// <para>═══ ที่มา ═══ ทีม F3 บังคับไว้ที่หน้าเว็บอย่างเดียว (lodging.html <c>bankMissing</c>) ⇒ API/ระบบเชื่อมต่อ/หน้าเก่าที่ไม่ส่งบัญชี
/// ลงเงินโอนเป็นเงินสดตั้งต้นเงียบ ๆ (สมุดธนาคารกับ GL แยกทางกัน) · บัตร/e-Wallet/เงินสด ลงบัญชีพักของช่องทางหรือเงินสดได้ ⇒ ไม่บังคับ ·
/// รับเงินออนไลน์ (gateway) เซิร์ฟเวอร์ตัดสินบัญชีพักเอง ⇒ ผู้เรียกไม่ต้องตรวจ</para>
/// </summary>
public static class LodgingPaymentBank
{
    public const string RuleCode = "LODGING-BANK-REQUIRED";

    /// <summary>วิธีชำระนี้ = เงินเข้าบัญชีธนาคาร</summary>
    private static bool RequiresBank(PaymentMethod method) => method is PaymentMethod.BankTransfer or PaymentMethod.PromptPay;

    /// <summary>null = ผ่าน · ข้อความ = ต้องเลือกบัญชีธนาคาร (บอกช่อง + ทางไปต่อ)</summary>
    public static string? Problem(PaymentMethod method, decimal amount, Guid? bankAccountId)
        => amount > 0.005m && RequiresBank(method) && (bankAccountId is null || bankAccountId == Guid.Empty)
            ? "เลือก “รับเข้าบัญชีธนาคาร” ก่อน — วิธีชำระเป็นโอน/พร้อมเพย์ เงินต้องลงบัญชีธนาคารที่รับจริง (ระบบไม่ลงเงินสดแทนให้) · "
              + "ยังไม่มีในรายการ ให้เพิ่มที่หน้า “บัญชีธนาคาร” หรือเปลี่ยนวิธีชำระถ้ารับเป็นเงินสด"
            : null;
}
