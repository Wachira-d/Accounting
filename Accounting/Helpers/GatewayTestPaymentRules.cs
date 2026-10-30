using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 219 ส่วน B ฝ่ายค้าน (B1/B2) — <b>ตัวตัดสินเดียว</b>ของ "รายการโหมดทดสอบ/ทดลองจ่าย ห้ามแตะเงินจริงในสมุด" · pure
/// <list type="bullet">
/// <item>ทดลองจ่าย (<see cref="PaymentSourceKind.GatewaySelfTest"/>) <b>คืนเงินไม่ได้</b> — ไม่มีเงินจริงให้คืน (เดิมกดคืนแล้วลง PV จริงในสมุดแพลตฟอร์ม)</item>
/// <item>รายการที่ประทับโหมดทดสอบ (<c>IsTestMode == true</c>) หรือทดลองจ่าย ⇒ <b>ไม่ลงบัญชี</b>คืนเงิน และ<b>ไม่เข้ารอบโอน</b>
/// (นิพจน์ EF ใน <c>GatewayBatchIntentRules.UnclaimedForBatch/LateRefundInBatch</c> และ <c>GatewaySettlementService</c> ใช้เงื่อนไขเดียวกันนี้)</item>
/// </list>
/// </summary>
public static class GatewayTestPaymentRules
{
    public const string RuleCode = "PAY-TEST-NO-LEDGER";
    public const string SelfTestRefundMessage =
        "รายการทดลองจ่าย (โหมดทดสอบ) คืนเงินผ่านระบบไม่ได้ — ไม่มีเงินจริงเข้า และระบบไม่ลงบัญชีรายการนี้";
    public const string TestModeNotBookedNote = "โหมดทดสอบ — ไม่ลงบัญชี (ไม่มีเงินจริง)";

    /// <summary>คืนเงินได้ไหม — null = ได้</summary>
    public static string? RefundRefusal(PaymentSourceKind kind)
        => kind == PaymentSourceKind.GatewaySelfTest ? SelfTestRefundMessage : null;

    /// <summary>รายการนี้ไม่มีเงินจริง (ทดลองจ่าย หรือประทับโหมดทดสอบ) ⇒ ไม่ลงบัญชี/ไม่เข้ารอบโอน</summary>
    public static bool IsNoMoney(PaymentSourceKind kind, bool? isTestMode)
        => kind == PaymentSourceKind.GatewaySelfTest || isTestMode == true;
}
