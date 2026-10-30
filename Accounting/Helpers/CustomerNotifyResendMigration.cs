namespace Accounting.Helpers;

/// <summary>รอบ 213 (คำตัดสินข้อ 172) — คอลัมน์เหตุระงับ/ร่องรอยส่งซ้ำของ <c>CustomerNotifyLogs</c> + ขนาด/อายุแพ็กที่แอดมินตั้งของ <c>ApiFeatures</c> ·
/// ต้องรันหลัง <c>DatabaseMigrationHelper.CmsRound212NotifyStatements</c> (สร้างตาราง) · ADD COLUMN/INDEX IF NOT EXISTS ⇒ รันทุกบูตได้ ·
/// ชื่อ index ต้องตรงกับ <c>AccountingDbContext</c> ·
/// <b>ซ่อมแถวเดิม (F2 #9)</b>: แถวที่ระงับก่อนรอบนี้ไม่มีรหัสเหตุ ⇒ เติมจากหมายเหตุที่ระบบเขียนเอง (ข้อความคงที่จากตัวตั้งเดียวกับที่ใช้ตอนส่ง —
/// <see cref="NotifyQuotaPolicy.FullReason"/> · <see cref="CustomerNotifyPolicy.SuppressedByPolicyReason"/> · <see cref="CustomerNotifyPolicy.SuppressedTenantOnlyReason"/>) ·
/// แตะเฉพาะแถว <c>Suppressed</c> ที่ <c>SuppressReason IS NULL</c> ⇒ รันซ้ำไม่เปลี่ยนอะไร</summary>
public static class CustomerNotifyResendMigration
{
    /// <summary>คำนำหน้าของ <see cref="NotifyQuotaPolicy.FullReason"/> (ก่อนวงเล็บตัวเลขเพดาน) — ใช้จับแถวเก่าที่ระงับเพราะโควตา</summary>
    public static string QuotaFullNotePrefix
    {
        get
        {
            var full = NotifyQuotaPolicy.FullReason(0);
            return full[..full.IndexOf('(')];
        }
    }

    private static string Sql(string s) => s.Replace("'", "''");

    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "CustomerNotifyLogs" ADD COLUMN IF NOT EXISTS "SuppressReason" varchar(24) NULL;""",
        """ALTER TABLE "CustomerNotifyLogs" ADD COLUMN IF NOT EXISTS "ResendRequestedAt" timestamp with time zone NULL;""",
        """ALTER TABLE "CustomerNotifyLogs" ADD COLUMN IF NOT EXISTS "ResendCount" integer NOT NULL DEFAULT 0;""",
        """CREATE INDEX IF NOT EXISTS "IX_CustomerNotifyLogs_Company_Status" ON "CustomerNotifyLogs" ("CompanyId", "Status", "CreatedAt");""",
        """ALTER TABLE "ApiFeatures" ADD COLUMN IF NOT EXISTS "PackSize" integer NULL;""",
        """ALTER TABLE "ApiFeatures" ADD COLUMN IF NOT EXISTS "PackValidDays" integer NULL;""",
        $"""UPDATE "CustomerNotifyLogs" SET "SuppressReason" = '{NotifySuppressReason.QuotaFull}' WHERE "Status" = 'Suppressed' AND "SuppressReason" IS NULL AND "Note" LIKE '{Sql(QuotaFullNotePrefix)}%';""",
        $"""UPDATE "CustomerNotifyLogs" SET "SuppressReason" = '{NotifySuppressReason.EventOff}' WHERE "Status" = 'Suppressed' AND "SuppressReason" IS NULL AND "Note" = '{Sql(CustomerNotifyPolicy.SuppressedByPolicyReason)}';""",
        $"""UPDATE "CustomerNotifyLogs" SET "SuppressReason" = '{NotifySuppressReason.TenantOnly}' WHERE "Status" = 'Suppressed' AND "SuppressReason" IS NULL AND "Note" = '{Sql(CustomerNotifyPolicy.SuppressedTenantOnlyReason)}';""",
        // ── ฝ่ายค้านรอบ 213 S3: หน่วย ณ ตอนซื้อบนแถวมิเตอร์ (ใบแจ้งหนี้ไม่อ่านป้ายที่แอดมินแก้ทีหลัง) ──
        """ALTER TABLE "UsageEvents" ADD COLUMN IF NOT EXISTS "UnitLabelSnapshot" varchar(50) NULL;""",
        // แถว notify.topup ที่ยังไม่มี snapshot = ซื้อก่อนรอบ 213 (ตอนนั้นขนาดแพ็กตายตัว 500) — เติมเฉพาะช่องว่างของคอลัมน์ใหม่ (ไม่แตะยอด/ราคา)
        $"""UPDATE "UsageEvents" SET "UnitLabelSnapshot" = '{Sql(NotifyQuotaPolicy.PackUnitLabel(Models.Constants.AddOnCodes.MessagesPerNotifyTopUpPack))}' WHERE "FeatureCode" = '{Models.Constants.AddOnCodes.NotifyTopUp}' AND "UnitLabelSnapshot" IS NULL;""",
        // ชื่อ seed ฝังเลข 500 — แก้เฉพาะเมื่อยังเป็นชื่อ seed เดิม (แอดมินตั้งชื่อเองแล้วไม่แตะ) · ขนาดแพ็กอยู่ที่หน่วยของแต่ละบรรทัด
        $"""UPDATE "ApiFeatures" SET "Name" = 'แพ็กข้อความแจ้งเตือน' WHERE "FeatureCode" = '{Models.Constants.AddOnCodes.NotifyTopUp}' AND "Name" = 'แพ็กข้อความแจ้งเตือน 500 ข้อความ';""",
    };
}
