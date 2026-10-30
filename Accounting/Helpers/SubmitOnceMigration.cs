namespace Accounting.Helpers;

/// <summary>รอบ 217 (คำตัดสินข้อ 175) — schema ของ "กดซ้ำไม่บันทึกซ้ำ" + "ตอบเร็ว แจ้งเตือนตามหลัง" <b>บล็อกเดียว · รันทุกบูตได้</b>:
/// <list type="bullet">
///   <item><c>IdempotencyRecords</c> (ย้ายมาจากรายการกลางของ DatabaseMigrationHelper — ตัวตั้งเดียวของตาราง) + คอลัมน์ <c>RequestHash</c>
///     (คีย์เดิมแต่เนื้อต่าง ⇒ 409 · <see cref="IdempotencyKeyPolicy"/>) — แถวเดิมไม่มีแฮช = ถือว่าตรง (ไม่ต้อง backfill · หมดอายุใน 24 ชม.)</item>
///   <item><c>DeferredNotifications</c> — คิวแจ้งเตือนหลังสร้างการจอง/ออเดอร์ (<see cref="DeferredNotifyPolicy"/>) · ชื่อ index ตรงกับ AccountingDbContext</item>
/// </list>
/// ฐานเทสต์ (EnsureCreated) ได้ DeferredNotifications จากโมเดล · IdempotencyRecords ไม่มีในโมเดล ⇒ Db test รัน <see cref="Statements"/> เอง</summary>
public static class SubmitOnceMigration
{
    public static IReadOnlyList<string> Statements() => new[]
    {
        """
        CREATE TABLE IF NOT EXISTS "IdempotencyRecords" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CacheKey" varchar(400) NOT NULL,
            "Status" varchar(16) NOT NULL DEFAULT 'InFlight',
            "StatusCode" integer NOT NULL DEFAULT 0,
            "ContentType" varchar(200) NULL,
            "Body" bytea NULL,
            "RequestHash" varchar(64) NULL,
            "CreatedAt" timestamptz NOT NULL DEFAULT now(),
            "CompletedAt" timestamptz NULL
        );
        """,
        """ALTER TABLE "IdempotencyRecords" ADD COLUMN IF NOT EXISTS "RequestHash" varchar(64) NULL;""",
        """CREATE UNIQUE INDEX IF NOT EXISTS "IX_IdempotencyRecords_Key" ON "IdempotencyRecords" ("CacheKey");""",
        """CREATE INDEX IF NOT EXISTS "IX_IdempotencyRecords_CreatedAt" ON "IdempotencyRecords" ("CreatedAt");""",
        """
        CREATE TABLE IF NOT EXISTS "DeferredNotifications" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "SiteId" uuid NOT NULL,
            "Kind" varchar(32) NOT NULL,
            "EntityId" uuid NOT NULL,
            "DueAt" timestamp with time zone NOT NULL DEFAULT now(),
            "ClaimedUntil" timestamp with time zone NULL,
            "Attempts" integer NOT NULL DEFAULT 0,
            "DoneAt" timestamp with time zone NULL,
            "LastError" varchar(500) NULL,
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp with time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false
        );
        """,
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_DeferredNotifications_Kind_Entity" ON "DeferredNotifications" ("Kind", "EntityId");""",
        """CREATE INDEX IF NOT EXISTS "IX_DeferredNotifications_Pending" ON "DeferredNotifications" ("DoneAt", "DueAt");""",
    };
}
