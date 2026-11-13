namespace Accounting.Helpers;

/// <summary>รอบ 264 แพ็ก P2 — ผลอ่านสลิปในเครื่อง (QR ตรวจสลิป + คู่กรณี) · idempotent (<c>ADD COLUMN IF NOT EXISTS</c> · ไม่ใช้ EF Migrations) ·
/// แถวเดิม = NULL = "ไม่มีผลอ่าน" ⇒ ตัวตัดสินทุกตัวให้ผลเท่าก่อนรอบนี้ (ไม่ backfill: รูปสลิปเก่าไม่ถูกอ่านซ้ำ — ไม่มีของเสียที่ persist ไว้ต้องซ่อม)</summary>
public static class Round264SlipReadMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "SlipReadJson" text NULL;""",
        """ALTER TABLE "LineChatSlips" ADD COLUMN IF NOT EXISTS "SlipReadJson" text NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SlipPayerBankCode" character varying(3) NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SlipPayerAccountMask" character varying(32) NULL;""",
    };
}
