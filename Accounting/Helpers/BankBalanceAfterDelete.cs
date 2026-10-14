namespace Accounting.Helpers;

/// <summary>
/// ยอดการ์ดบัญชีธนาคาร (<c>BankAccount.CurrentBalance</c>) หลังลบบรรทัดรายการธนาคาร — ตัวตัดสินตัวเดียว (pure · ฝ่ายค้านรอบ 1 P1 · 2026-10-14)
///
/// <para>═══ ที่มา (บั๊กจริง) ═══ statement ที่ไม่มีคอลัมน์ยอดคงเหลือเก็บ <c>BalanceAfter = 0</c> แล้วตัวคำนวณใหม่ตอนลบใช้ "บรรทัดล่าสุด" ตรง ๆ
/// ⇒ ลบบรรทัดเดียว / ย้อนการนำเข้าจากศูนย์นำเข้า แล้วยอดการ์ดกลายเป็น 0 · ลบหมดทุกบรรทัดก็ตั้งเป็น 0 ทั้งที่บัญชีมียอดยกมา
/// (ค่าที่แต่งขึ้นอันตรายกว่าไม่ตอบ — CLAUDE.md F2 ข้อ 3)</para>
/// <para>ลำดับ: ยอดของบรรทัดล่าสุดที่<b>รู้ยอดจริง</b> → ยอดตามธนาคารที่นำเข้าไว้ (<c>StatementBalance</c>) → คงยอดเดิม (ไม่รู้ = ไม่แตะ)</para>
/// </summary>
public static class BankBalanceAfterDelete
{
    public static decimal Resolve(decimal? latestKnownBalance, decimal? statementBalance, decimal currentBalance)
        => latestKnownBalance ?? statementBalance ?? currentBalance;
}
