namespace Accounting.Helpers;

/// <summary>
/// รอบ 275 แพ็ก E (T4 H-3 · คำตัดสิน 4.2) — การรับทราบรายคนว่าพนักงานไม่มีเลขประจำตัว 13 หลัก (เหตุ · ผู้กด · เวลา) · ทุกคำสั่ง idempotent ·
/// ไม่เติมย้อนหลัง (ค่าว่าง = ยังไม่รับทราบ = พฤติกรรมเดิม)
/// </summary>
public static class Round275EMigration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "TaxIdWaiverAcknowledgedAt" timestamp with time zone NULL;""",
        """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "TaxIdWaiverAcknowledgedBy" uuid NULL;""",
        """ALTER TABLE "Employees" ADD COLUMN IF NOT EXISTS "TaxIdWaiverReason" character varying(500) NULL;""",
    };
}
