namespace Accounting.Helpers;

/// <summary>รอบ 260-2 — บัญชีที่เงินตามสลิปเข้า (<c>Helpers/SlipReceivingAccount</c>) · idempotent (<c>ADD COLUMN IF NOT EXISTS</c> · ไม่ใช้ EF Migrations) ·
/// แถวเดิม = NULL = "ยังไม่ตัดสิน" ⇒ handler คำนวณค่าแนะนำตอนส่งต่อ (ไม่ backfill: รายการที่สำเร็จแล้วบันทึกรับชำระไปแล้ว — คอลัมน์นี้ไม่ย้อนแก้ JE เดิม)</summary>
public static class Round260SlipReceivingMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "ReceivingBankAccountId" uuid NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "ReceivingAccountOrigin" character varying(32) NULL;""",
    };
}
