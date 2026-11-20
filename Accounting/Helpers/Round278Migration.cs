namespace Accounting.Helpers;

/// <summary>
/// รอบ 278 — ช่องลายเซ็นผู้จัดทำ ↔ ผู้มีอำนาจลงนาม · idempotent ·
/// (1) ชื่อเล่นต่อบริษัท (ต้องเจ้าของอนุมัติ) · (2) สิทธิ์ "ให้ระบบภายนอกระบุฉันเป็นผู้จัดทำ" ต่อสมาชิก · (3) แช่แข็งผู้ลงนามตอนออกเอกสาร ·
/// (4) <b>ใบที่ออกแล้วก่อนรอบนี้</b> ⇒ snapshot ว่าง (ไม่รวมช่อง) ให้พิมพ์เหมือนวันนี้ก่อนการรวม — ไม่ใช้กติกาใหม่ย้อนหลัง (ฝ่ายค้าน HIGH-3) ·
/// ใบที่ถูกเปลี่ยนสถานะด้วย SQL ตรง (ไม่ผ่าน SaveChanges) ได้ snapshot ว่างที่การบูตถัดไป — ทิศปลอดภัย (ระหว่างนั้น null = ไม่รวมอยู่แล้ว)
/// </summary>
public static class Round278Migration
{
    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SignersFrozenAt" timestamp without time zone NULL;""",
        """ALTER TABLE "Documents" ADD COLUMN IF NOT EXISTS "SignerSnapshotJson" text NULL;""",
        """ALTER TABLE "CompanyUsers" ADD COLUMN IF NOT EXISTS "AllowExternalPreparerBinding" boolean NOT NULL DEFAULT false;""",
        """
        CREATE TABLE IF NOT EXISTS "SignerAliases" ("Id" uuid PRIMARY KEY, "CompanyId" uuid NOT NULL, "UserId" uuid NOT NULL,
          "Alias" varchar(100) NOT NULL, "NormalizedAlias" varchar(100) NOT NULL,
          "ApprovedAt" timestamp without time zone NULL, "ApprovedByUserId" uuid NULL,
          "RevokedAt" timestamp without time zone NULL, "RevokedByUserId" uuid NULL,
          "CreatedAt" timestamp without time zone NOT NULL DEFAULT now(), "UpdatedAt" timestamp without time zone NULL,
          "CreatedBy" text NULL, "UpdatedBy" text NULL, "IsDeleted" boolean NOT NULL DEFAULT false,
          CONSTRAINT "FK_SignerAliases_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE);
        """,
        """CREATE INDEX IF NOT EXISTS "IX_SignerAliases_CompanyId_NormalizedAlias" ON "SignerAliases" ("CompanyId", "NormalizedAlias");""",
        "UPDATE \"Documents\" SET \"SignersFrozenAt\" = COALESCE(\"UpdatedAt\", \"CreatedAt\"), \"SignerSnapshotJson\" = '"
            + SignatureSlotIdentity.EmptySnapshotJson + "' WHERE \"SignerSnapshotJson\" IS NULL AND \"Status\" NOT IN (0, 1, 8);",
    };
}
