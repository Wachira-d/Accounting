namespace Accounting.Helpers;

/// <summary>
/// รอบ 282 — สลิปค่าสินค้า/บริการจากกลุ่ม LINE/อีเมล ⇒ บันทึกรับเงินเอง/หน้ายืนยัน + วิธีสำรองแจ้งเตือนให้ยืนยันแหล่งเงิน (ทั้งสองฝั่ง) ·
/// idempotent (<c>ADD COLUMN IF NOT EXISTS</c>) · ค่าตั้งต้น <c>SlipAutoRecordEnabled = false</c> (บริษัทเดิมไม่เปลี่ยนพฤติกรรมเงียบ ๆ) ·
/// แถวเดิมไม่มีของเสียที่ persist ไว้ต้องซ่อม (คอลัมน์ใหม่ทั้งหมด = "ยังไม่ประเมิน/ยังไม่เคยแจ้ง")
/// </summary>
public static class Round282Migration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "SlipAutoRecordEnabled" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "AutoRecordCode" text NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "AutoRecordReason" text NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "ConfirmNoticeAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "ConfirmRemindCount" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "ConfirmRemindedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SourceNoticeAt" timestamp without time zone NULL;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SourceRemindCount" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "PartnerPaymentNotices" ADD COLUMN IF NOT EXISTS "SourceRemindedAt" timestamp without time zone NULL;""",
    };
}
