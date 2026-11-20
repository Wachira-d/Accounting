namespace Accounting.Helpers;

/// <summary>
/// รอบ 277 — คำเชิญเข้าบริษัท + สมาชิก "ยังไม่ได้รับสิทธิ์" · idempotent ·
/// (1) <c>CompanyUsers.AccessPending</c> ค่าตั้งต้น false ⇒ สมาชิกเดิมพฤติกรรมเดิม ·
/// (2) คำเชิญ: token ดิบในฐาน (ค่าที่ persist ไว้ก่อนแก้ — ฐานรั่ว = เอาไปตอบรับคำเชิญที่ค้างได้) ⇒ ย้ายเป็น SHA-256 hex
/// (สูตรเดียวกับ <see cref="CompanyInvitationPolicy.HashToken"/>: UTF-8 → sha256 → hex ตัวเล็ก) แล้วล้างค่าดิบเป็น NULL ·
/// (commit a1ae70d2 + d93800f2) ลิงก์ในอีเมลที่ส่งไปแล้วยังใช้ได้ (เทียบ hash ของ token ในลิงก์) · คำเชิญเดิม <c>RoleChosen</c> = true (ผู้เชิญเลือกบทบาทไว้เสมอ)
/// </summary>
public static class Round277Migration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "CompanyUsers" ADD COLUMN IF NOT EXISTS "AccessPending" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "CompanyInvitations" ADD COLUMN IF NOT EXISTS "TokenHash" text NULL;""",
        """ALTER TABLE "CompanyInvitations" ADD COLUMN IF NOT EXISTS "RoleChosen" boolean NOT NULL DEFAULT true;""",
        """ALTER TABLE "CompanyInvitations" ADD COLUMN IF NOT EXISTS "CompanyRoleId" uuid NULL;""",
        """ALTER TABLE "CompanyInvitations" ADD COLUMN IF NOT EXISTS "OwnDocumentsOnly" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "CompanyInvitations" ADD COLUMN IF NOT EXISTS "DraftDocumentsOnly" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "CompanyInvitations" ADD COLUMN IF NOT EXISTS "LastSentAt" timestamp without time zone NULL;""",
        """ALTER TABLE "CompanyInvitations" ADD COLUMN IF NOT EXISTS "SendCount" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "CompanyInvitations" ADD COLUMN IF NOT EXISTS "CancelledByUserId" uuid NULL;""",
        """ALTER TABLE "CompanyInvitations" ADD COLUMN IF NOT EXISTS "CancelledAt" timestamp without time zone NULL;""",
        """ALTER TABLE "CompanyInvitations" ALTER COLUMN "Token" DROP NOT NULL;""",
        """
        UPDATE "CompanyInvitations"
           SET "TokenHash" = encode(sha256(convert_to("Token", 'UTF8')), 'hex'), "Token" = NULL
         WHERE "Token" IS NOT NULL AND "TokenHash" IS NULL;
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_CompanyInvitations_TokenHash" ON "CompanyInvitations" ("TokenHash") WHERE "TokenHash" IS NOT NULL;""",
    };
}
