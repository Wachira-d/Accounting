namespace Accounting.Helpers;

/// <summary>
/// รอบ 276 — ผู้ใช้ "เห็นเฉพาะเอกสารที่ตัวเองสร้าง" / "สร้าง/แก้ได้เฉพาะร่าง" + คำขออนุมัติพื้นฐาน · idempotent (<c>IF NOT EXISTS</c>) ·
/// ค่าตั้งต้น false ⇒ ผู้ใช้/บทบาทเดิมไม่ถูกจำกัด (พฤติกรรมเดิมไม่เปลี่ยน · ไม่มี backfill) · ฐานเทสต์ได้ตารางจากโมเดล (EnsureCreated) แล้ว Db test รันชุดนี้ซ้ำ
/// เพื่อยืนยันว่า idempotent
/// </summary>
public static class Round276Migration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "CompanyUsers" ADD COLUMN IF NOT EXISTS "OwnDocumentsOnly" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "CompanyUsers" ADD COLUMN IF NOT EXISTS "DraftDocumentsOnly" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "CompanyRoles" ADD COLUMN IF NOT EXISTS "OwnDocumentsOnly" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "CompanyRoles" ADD COLUMN IF NOT EXISTS "DraftDocumentsOnly" boolean NOT NULL DEFAULT false;""",
        """
        CREATE TABLE IF NOT EXISTS "DocumentApprovalRequests" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "DocumentId" uuid NOT NULL,
            "RequestedByUserId" uuid NOT NULL,
            "RequestedAt" timestamp without time zone NOT NULL,
            "Note" varchar(500) NULL,
            "Status" integer NOT NULL DEFAULT 0,
            "DecidedByUserId" uuid NULL,
            "DecidedAt" timestamp without time zone NULL,
            "DecisionReason" varchar(500) NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "UpdatedAt" timestamp without time zone NULL,
            "CreatedBy" text NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_DocumentApprovalRequests_Company_Doc_Status" ON "DocumentApprovalRequests" ("CompanyId", "DocumentId", "Status");""",
        // ตัวกรองแถว "ของตัวเอง" อ่าน CreatedBy ต่อบริษัท — index บางส่วนไม่จำเป็น (ค่าว่างมีน้อย) · ทั้งคอลัมน์พอ
        """CREATE INDEX IF NOT EXISTS "IX_Documents_CompanyId_CreatedBy" ON "Documents" ("CompanyId", "CreatedBy");""",
        // ฝ่ายค้าน LOW-10: ผู้สร้างเป็น id ผู้ใช้ + เติมแถวเก่าที่จับคู่ได้แน่นอน (CreatedBy = id สมาชิกของบริษัทนั้น · หรืออีเมลที่ตรงสมาชิกคนเดียว) — ทำซ้ำได้ (เฉพาะแถวที่ยัง null)
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "CreatedByUserId" uuid NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_Documents_CompanyId_CreatedByUserId" ON "Documents" ("CompanyId", "CreatedByUserId");""",
        """
        UPDATE "Documents" d SET "CreatedByUserId" = cu."UserId"
        FROM "CompanyUsers" cu
        WHERE d."CreatedByUserId" IS NULL
          AND cu."CompanyId" = d."CompanyId" AND lower(d."CreatedBy") = cu."UserId"::text;
        """,
        """
        UPDATE "Documents" d SET "CreatedByUserId" = m."UserId"
        FROM (SELECT cu."CompanyId", u."Email", MIN(cu."UserId"::text)::uuid AS "UserId", COUNT(*) AS n
              FROM "CompanyUsers" cu JOIN "Users" u ON u."Id" = cu."UserId" GROUP BY cu."CompanyId", u."Email") m
        WHERE d."CreatedByUserId" IS NULL AND m.n = 1
          AND m."CompanyId" = d."CompanyId" AND d."CreatedBy" = m."Email";
        """,
    };
}
