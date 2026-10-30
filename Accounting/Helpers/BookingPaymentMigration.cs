namespace Accounting.Helpers;

/// <summary>รอบ 218 (คำตัดสินข้อ 177) — คอลัมน์นโยบายรับชำระล่วงหน้า + สลิปของการจองนัดหมาย · <b>รันทุกบูตได้</b> (IF NOT EXISTS) ·
/// NULL/DEFAULT = พฤติกรรมเดิมทุกตัว: <c>PaymentMode</c> NULL ทั้งเว็บ/บริการ ⇒ แบบเดิมตามชนิดบริการ (<see cref="BookingPaymentPolicy"/> Legacy) ·
/// <c>BankTransferEnabled</c> true (มีบัญชีเมื่อไรใช้ได้) · การจองเดิมไม่มีกำหนดเวลา/สลิป · ไม่ UPDATE ข้อมูลเดิม ·
/// ชนิด/ความยาวต้องตรงกับ <c>AccountingDbContext</c></summary>
public static class BookingPaymentMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "PaymentMode" integer NULL;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "PaymentDepositFixedAmount" numeric(18,2) NULL;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "PaymentDepositPercent" numeric(5,2) NULL;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "PaymentDepositMinAmount" numeric(18,2) NULL;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "PaymentDepositMaxAmount" numeric(18,2) NULL;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "PaymentDeadlineMinutes" integer NULL;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "BankTransferEnabled" boolean NOT NULL DEFAULT true;""",
        """ALTER TABLE "SiteBookingSettings" ADD COLUMN IF NOT EXISTS "PaymentNote" varchar(1000) NULL;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "PaymentMode" integer NULL;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "DepositMinAmount" numeric(18,2) NULL;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "DepositMaxAmount" numeric(18,2) NULL;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "PaymentDeadlineMinutes" integer NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "PaymentHoldUntil" timestamptz NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "SlipAttachmentId" uuid NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "SlipUploadedAt" timestamptz NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "SlipIntentId" uuid NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "SlipRejectedReason" varchar(500) NULL;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "SlipRejectedAt" timestamptz NULL;""",
        // job ยกเลิกใบที่ไม่จ่ายในเวลา สแกนเฉพาะแถวที่ถือ (index บางส่วน)
        """CREATE INDEX IF NOT EXISTS "IX_SiteBookings_PaymentHoldUntil" ON "SiteBookings" ("PaymentHoldUntil") WHERE "PaymentHoldUntil" IS NOT NULL;""",
        """ALTER TABLE "SitePaymentGateways" ADD COLUMN IF NOT EXISTS "BankBranch" varchar(128) NULL;""",
    };
}
