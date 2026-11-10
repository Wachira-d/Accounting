namespace Accounting.Helpers;

/// <summary>รอบ 257 A-M7 — "ธงปิดหนี้อัตโนมัติจากเงินโอนเปลี่ยนจริงไหม" (pure · ตัวตัดสินเดียวของด่าน Bank.Reconcile ใน <c>AutopilotController.UpdateSettings</c>)
/// <para>ที่มา: หน้า Autopilot ส่ง <c>bankAutoSettleEnabled</c> ทุกครั้ง ⇒ ผู้มีแค่ CompanySettings.Edit บันทึกเวลาสรุป/ค่าตั้งใบเสนอราคาไม่ได้ (403) ·
/// ไม่ส่ง (null) หรือส่งค่าเดิม = ไม่ใช่การเปิด/ปิด ⇒ ไม่ต้องมี Bank.Reconcile (controller ถอดค่าเดิมออกจากคำขอ ไม่เขียนทับ)</para></summary>
public static class AutopilotBankSettleToggle
{
    public static bool IsChange(bool? requested, bool current) => requested is bool r && r != current;
}
