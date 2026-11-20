namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก E (T4 H-2 · H-3 · คำตัดสิน 4.2 · ฝ่ายค้าน MED-2/MED-3) · ทุกคำสั่ง idempotent · ไม่เติมย้อนหลัง (ค่าว่าง = ยังไม่รับทราบ = พฤติกรรมเดิม):
/// <list type="bullet">
/// <item>การรับทราบรายคนว่าไม่มีเลขประจำตัว 13 หลัก — พนักงาน (<c>Employees</c>) และผู้รับใน ภ.ง.ด.1 ที่ไม่ใช่พนักงาน (<c>Contacts</c>)</item>
/// <item>รายการค้าง "ต้องยื่น ภ.ง.ด.1 เพิ่มเติม" (<c>Pnd1AmendmentsRequired</c>)</item>
/// </list>
/// </summary>
public static class Round275EMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "TaxIdWaiverAcknowledgedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "TaxIdWaiverAcknowledgedBy" uuid NULL;""",
        """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "TaxIdWaiverReason" character varying(500) NULL;""",
        """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "TaxIdWaiverAcknowledgedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "TaxIdWaiverAcknowledgedBy" uuid NULL;""",
        """ALTER TABLE "Contacts" ADD COLUMN IF NOT EXISTS "TaxIdWaiverReason" character varying(500) NULL;""",
        """
        CREATE TABLE IF NOT EXISTS "Pnd1AmendmentsRequired" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "TaxYear" integer NOT NULL,
            "TaxMonth" integer NOT NULL,
            "Reason" varchar(500) NOT NULL DEFAULT '',
            "CertificateNumbers" text NULL,
            "ResolvedAt" timestamp without time zone NULL,
            "ResolvedByUserId" uuid NULL,
            "ResolvedReference" varchar(500) NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "UpdatedAt" timestamp without time zone NULL,
            "CreatedBy" text NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_Pnd1AmendmentsRequired_Company_Period" ON "Pnd1AmendmentsRequired" ("CompanyId", "TaxYear", "TaxMonth");""",
    };
}
