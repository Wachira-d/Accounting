using Accounting.Models.Constants;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **สิทธิ์ของงานบนรายการรับชำระออนไลน์ — ตารางเดียว** (รอบ 198 G-8)
///
/// <para>═══ ที่มา (บั๊กจริง · ทีม S2 รอบ 198 · main agent ยืนยัน) ═══ <c>PaymentGatewayController</c> มีแค่ <c>[Authorize]</c>
/// ระดับคลาส ⇒ สมาชิกคนไหนของบริษัท (รวมบทบาท "ดูอย่างเดียว" และคีย์ API) กด<b>คืนเงินลูกค้าจริง</b>ผ่านผู้ให้บริการได้ ·
/// ยืนยันว่า "เงินเข้าแล้ว" ด้วยมือ (ออกใบเสร็จ/ตัดหนี้/ยืนยันการจองตามมา) ได้ · และลง JE รอบโอนได้ — และไฟล์นี้ไม่อยู่ใน
/// <c>tools/write_permission_gate_check.py</c> จึงไม่มีอะไรฟ้อง</para>
///
/// <para>═══ กติกา ═══ เงินออกจริง (คืนเงิน) = <see cref="PermissionKeys.BankPaymentInit"/> (ระดับเดียวกับสั่งโอนเงินออก) ·
/// ยืนยันเงินเข้าด้วยมือ = <see cref="PermissionKeys.BankReconcile"/> (คนที่เห็นสเตทเมนต์) · ลง JE รอบโอน/แก้ค่าธรรมเนียม
/// ที่ลงบัญชี = <see cref="PermissionKeys.JournalManage"/> · ดูพรีวิวรอบโอน = <see cref="PermissionKeys.BankView"/> ·
/// เริ่มรับชำระ (สร้าง QR/ลิงก์จ่าย) = สิทธิ์ของโมดูลต้นทาง (<see cref="StartKeyFor"/>)</para>
///
/// <para>G6: pure · ไม่มี I/O</para>
/// </summary>
public static class PaymentGatewayPermissionScope
{
    /// <summary>คืนเงินลูกค้าผ่านผู้ให้บริการ — เงินออกจริง</summary>
    public const string Refund = PermissionKeys.BankPaymentInit;
    /// <summary>ยืนยันด้วยมือว่าเงินเข้าแล้ว</summary>
    public const string ConfirmManually = PermissionKeys.BankReconcile;
    /// <summary>บันทึกรอบโอน (ลง JE) · แก้ค่าธรรมเนียมจริงที่จะลงบัญชี</summary>
    public const string PostSettlement = PermissionKeys.JournalManage;
    /// <summary>ดูตัวอย่างรอบโอน (ไม่เขียนอะไร)</summary>
    public const string PreviewSettlement = PermissionKeys.BankView;

    /// <summary>สิทธิ์เริ่มรับชำระของต้นทางแต่ละชนิด — ต้นทางที่ไม่รู้จัก ⇒ สิทธิ์การเงินที่เข้มที่สุด (ไม่ใช่ "ใครก็ได้")</summary>
    public static string StartKeyFor(PaymentSourceKind kind) => kind switch
    {
        PaymentSourceKind.Document => PermissionKeys.DocumentRevenueCreate,
        PaymentSourceKind.SiteOrder => PermissionKeys.CmsOrderManage,
        PaymentSourceKind.LodgingReservation => PermissionKeys.LodgingManage,
        PaymentSourceKind.PosOrder => PermissionKeys.PosCashier,
        PaymentSourceKind.SubscriptionPayment => PermissionKeys.BillingManage,
        PaymentSourceKind.AddOnPurchase => PermissionKeys.BillingManage,
        _ => PermissionKeys.BankPaymentInit,
    };
}
