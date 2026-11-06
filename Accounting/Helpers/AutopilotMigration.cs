namespace Accounting.Helpers;

/// <summary>รอบ 252 WP-12A — คอลัมน์ของ Autopilot ส่วน A (อีเมลสถานะ/สรุปประจำวัน · ใบเสนอราคาแปลงอัตโนมัติ · เงินโอนปิดหนี้อัตโนมัติ · สลิปกันซ้ำ) ·
/// <c>ADD COLUMN IF NOT EXISTS</c> ทุกบรรทัด (idempotent · รันทุกบูต) · ค่าตั้งต้นทุกคอลัมน์ = ปิด/NULL = พฤติกรรมเดิม (ไม่มีค่าเก่าต้องซ่อม) ·
/// ผู้เรียก: <c>DatabaseMigrationHelper.GetAlterStatements</c> · ฐานเทสต์ได้คอลัมน์จากโมเดล (EnsureCreated)</summary>
public static class AutopilotMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        // ค่าตั้งบริษัท (Q4 ปิด · Q5 ไม่แปลง · Q7 08:40)
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "BankAutoSettleEnabled" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "QuotationAcceptAutopilotDefault" varchar(32) NULL;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "QuotationAcceptAutopilotSince" timestamp NULL;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "AutopilotDigestLocalTime" varchar(5) NULL;""",
        """ALTER TABLE "CompanySettings" ADD COLUMN IF NOT EXISTS "AutopilotDigestLastSentOn" timestamp NULL;""",
        // ใบเสนอราคา: ค่าตั้งต่อใบ + ผลการตัดสิน (watermark ต่อใบ)
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "QuotationAcceptAutopilot" varchar(32) NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "QuotationAutopilotAt" timestamp NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "QuotationAutopilotOutcome" varchar(32) NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "QuotationAutopilotMessage" text NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "QuotationAutopilotChildId" uuid NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_Documents_QuotationAutopilotPending" ON "Documents" ("CompanyId", "QuotationAcceptedAt") WHERE "QuotationAcceptedAt" IS NOT NULL AND "QuotationAutopilotAt" IS NULL;""",
        // ผู้ติดต่อ: ปิดการปิดหนี้อัตโนมัติรายคน
        """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "BankAutoSettleDisabled" boolean NOT NULL DEFAULT false;""",
        // รายการเดินบัญชี: แหล่ง + คำตัดสิน (แถวเดิม EntrySource = NULL = ไม่รู้ ⇒ ไม่ปิดหนี้อัตโนมัติ — เสนอได้อย่างเดียว)
        """ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "EntrySource" varchar(16) NULL;""",
        """ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "CreatedByUserId" uuid NULL;""",
        """ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "AutoSettleVerdict" varchar(16) NULL;""",
        """ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "AutoSettleReason" text NULL;""",
        """ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "AutoSettleDocumentId" uuid NULL;""",
        """ALTER TABLE "BankTransactions" ADD COLUMN IF NOT EXISTS "AutoSettleEvaluatedAt" timestamp NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_BankTransactions_AutoSettleQueue" ON "BankTransactions" ("CompanyId", "AutoSettleVerdict") WHERE "AutoSettleVerdict" IS NOT NULL;""",
        // สลิป: กันซ้ำด้วยแฮชไฟล์ + เลขอ้างอิงโอน (ไม่ unique ที่ฐาน — สลิปไม่ปิดหนี้เอง · ด่านอยู่ที่ PortalService)
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "SlipFileSha256" varchar(64) NULL;""",
        """ALTER TABLE "PaymentIntents" ADD COLUMN IF NOT EXISTS "SlipBankRef" varchar(64) NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_PaymentIntents_SlipFileSha256" ON "PaymentIntents" ("CompanyId", "SlipFileSha256") WHERE "SlipFileSha256" IS NOT NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_PaymentIntents_SlipBankRef" ON "PaymentIntents" ("CompanyId", "SlipBankRef") WHERE "SlipBankRef" IS NOT NULL;""",
    };
}
