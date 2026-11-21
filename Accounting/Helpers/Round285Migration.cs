namespace Accounting.Helpers;

/// <summary>
/// รอบ 285 (commit ae317b36) — ยืนยันเลขผู้เสียภาษีของบริษัท (<see cref="CompanyTaxIdVerification"/>) · idempotent (<c>IF NOT EXISTS</c>) · ชื่อ index ตรง <c>AccountingDbContext</c> ·
/// บริษัทเดิมทั้งหมด = <b>ยังไม่ยืนยัน</b> (ช่องว่าง · ไม่ประทับเอง — สถานะ "ยืนยันแล้ว" ตั้งได้เฉพาะเมื่อผู้ดูแลแพลตฟอร์มอนุมัติหลักฐานจริง)
/// </summary>
public static class Round285Migration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "TaxIdVerifiedAt" timestamp without time zone NULL;""",
        """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "TaxIdVerifiedFor" character varying(13) NULL;""",
        """ALTER TABLE "Companies" ADD COLUMN IF NOT EXISTS "TaxIdVerificationRequestId" uuid NULL;""",
        """
        CREATE TABLE IF NOT EXISTS "CompanyTaxIdVerificationRequests" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "TaxId" character varying(13) NOT NULL DEFAULT '',
            "BranchCode" character varying(5) NULL,
            "CompanyName" character varying(300) NOT NULL DEFAULT '',
            "EvidenceKind" character varying(32) NOT NULL DEFAULT '',
            "EvidenceLocation" character varying(1000) NULL,
            "EvidenceContentType" character varying(100) NULL,
            "EvidenceFileName" character varying(300) NULL,
            "EvidenceRetainUntil" timestamp without time zone NULL,
            "EvidenceSha256" character varying(64) NULL,
            "RegistryName" character varying(300) NULL,
            "RegistryResult" character varying(16) NOT NULL DEFAULT '',
            "Status" character varying(16) NOT NULL DEFAULT '',
            "SubmittedByUserId" uuid NOT NULL,
            "SubmittedAt" timestamp without time zone NOT NULL,
            "ReviewedByUserId" uuid NULL,
            "ReviewedAt" timestamp without time zone NULL,
            "ReviewNote" character varying(1000) NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_CompanyTaxIdVerificationRequests_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE
        );
        """,
        // ข้อเพิ่มรอบ 285: หลักฐานย้ายจาก FileAttachments ไปที่เก็บไฟล์กลางโดยตรง (ฐานที่รันเวอร์ชันแรกแล้ว ⇒ เติมคอลัมน์ · คอลัมน์ AttachmentId เดิมไม่มีผู้อ่าน)
        """ALTER TABLE "CompanyTaxIdVerificationRequests" ADD COLUMN IF NOT EXISTS "EvidenceLocation" character varying(1000) NULL;""",
        """ALTER TABLE "CompanyTaxIdVerificationRequests" ADD COLUMN IF NOT EXISTS "EvidenceContentType" character varying(100) NULL;""",
        """ALTER TABLE "CompanyTaxIdVerificationRequests" ADD COLUMN IF NOT EXISTS "EvidenceFileName" character varying(300) NULL;""",
        """ALTER TABLE "CompanyTaxIdVerificationRequests" ADD COLUMN IF NOT EXISTS "EvidenceRetainUntil" timestamp without time zone NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_CompanyTaxIdVerificationRequests_Company_Status" ON "CompanyTaxIdVerificationRequests" ("CompanyId", "Status");""",
        """CREATE INDEX IF NOT EXISTS "IX_CompanyTaxIdVerificationRequests_Status" ON "CompanyTaxIdVerificationRequests" ("Status");""",
    };
}
