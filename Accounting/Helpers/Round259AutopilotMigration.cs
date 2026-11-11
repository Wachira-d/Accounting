namespace Accounting.Helpers;

/// <summary>รอบ 259 ชุด D (Autopilot ส่วนที่เหลือ) — คอลัมน์/ดัชนี (idempotent · <c>ADD COLUMN IF NOT EXISTS</c> · ไม่ใช้ EF Migrations)</summary>
public static class Round259AutopilotMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        // B-L2/L3: กุญแจกันซ้ำ "รายการประจำ + งวด" (Helpers/RecurringRunKey) — ประทับใน INSERT เดียวกับเอกสาร · ใบเดิม = null (ไม่ backfill:
        // ใบงวดก่อน ๆ ไม่มีหลักฐานว่าเป็นงวดใด · ความเสี่ยงคงเท่าเดิมเฉพาะงวดที่ออกไปก่อนรอบนี้)
        $"""ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "RecurringRunKey" character varying({RecurringRunKey.MaxLength}) NULL;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_Documents_RecurringRunKey" ON "Documents" ("CompanyId", "RecurringRunKey") WHERE "RecurringRunKey" IS NOT NULL;""",
        // B-M6: คิวนำส่ง e-Tax ต่อกรมสรรพากร (โหมดส่งตรง) — จำนวนครั้งที่ลอง · ครั้งล่าสุด · ครั้งถัดไป (Helpers/EtaxRdSubmission)
        """ALTER TABLE "EtaxInvoices" ADD COLUMN IF NOT EXISTS "RdSubmitAttempts" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "EtaxInvoices" ADD COLUMN IF NOT EXISTS "RdLastAttemptAt" timestamp NULL;""",
        """ALTER TABLE "EtaxInvoices" ADD COLUMN IF NOT EXISTS "RdNextAttemptAt" timestamp NULL;""",
        // C-L1: ป้ายซื่อสัตย์ของผู้ขายที่ AI จับคู่ (แถวเดิม = false — ไม่รู้ว่ามาจาก AI จริงหรือนักเรียน ⇒ ไม่แต่ง "AI")
        """ALTER TABLE "OcrScanResults" ADD COLUMN IF NOT EXISTS "VendorMatchUsedAi" boolean NOT NULL DEFAULT false;""",
        // ฝ่ายค้าน 259-D M2: กุญแจกันซ้ำ "รายการประจำ + งวด" ของสมุดรายวัน (ประทับใน INSERT เดียวกับใบสำคัญ)
        $"""ALTER TABLE "JournalEntries" ADD COLUMN IF NOT EXISTS "RecurringRunKey" character varying({RecurringRunKey.MaxLength}) NULL;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_JournalEntries_RecurringRunKey" ON "JournalEntries" ("CompanyId", "RecurringRunKey") WHERE "RecurringRunKey" IS NOT NULL;""",
        // B-L11 (คำตัดสิน 259-4-5): ค่าตั้งปิดงวดอัตโนมัติถูกลบจาก entity — คอลัมน์เดิม (NOT NULL จากสคีมาแรก) ต้องมี DEFAULT ไม่งั้น INSERT แถวค่าตั้งใหม่ล้ม ·
        // ฐานที่สร้างหลังรอบนี้ไม่มีคอลัมน์ ⇒ ตรวจก่อน (idempotent)
        """
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'CompanySettings' AND column_name = 'AutoCloseMonthEnd') THEN
                ALTER TABLE "CompanySettings" ALTER COLUMN "AutoCloseMonthEnd" SET DEFAULT false;
            END IF;
            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'CompanySettings' AND column_name = 'MonthEndClosingDay') THEN
                ALTER TABLE "CompanySettings" ALTER COLUMN "MonthEndClosingDay" SET DEFAULT 15;
            END IF;
        END $$;
        """,
    }.Concat(EtaxRdSubmission.LegacyStampRepairSql()).ToList();   // ฝ่ายค้าน 259-D H1 — หลังคอลัมน์คิว (idempotent)
}
