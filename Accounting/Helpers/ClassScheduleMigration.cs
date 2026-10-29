namespace Accounting.Helpers;

/// <summary>รอบ 209 · P4 — ตารางคลาส (<c>SiteClassTerms</c> · <c>SiteClassSessions</c>) + ล้างคอลัมน์ตาย (C9) · <b>รันทุกบูตได้</b> (IF NOT EXISTS) ·
/// ไม่แตะข้อมูลเดิม: บริการที่ไม่มีเทอม = พฤติกรรมเดิมทุกข้อ · ตาราง/index ต้องตรงกับ <c>AccountingDbContext</c> ·
/// คีย์รอบ = (CompanyId, BookingServiceId, SessionDate, StartTime) — ตรงกับที่ <see cref="BookingSlotEngine"/> นับ "รอบเดียวกัน"</summary>
public static class ClassScheduleMigration
{
    private const string Base = "\"Id\" uuid PRIMARY KEY, \"CompanyId\" uuid NOT NULL, \"CreatedAt\" timestamptz NOT NULL DEFAULT now(), "
        + "\"UpdatedAt\" timestamptz NULL, \"CreatedBy\" text NULL, \"UpdatedBy\" text NULL, \"IsDeleted\" boolean NOT NULL DEFAULT false";

    public static IReadOnlyList<string> Statements() => new[]
    {
        "CREATE TABLE IF NOT EXISTS \"SiteClassTerms\" (" + Base + ", \"SiteId\" uuid NOT NULL, \"BookingServiceId\" uuid NOT NULL, "
            + "\"Title\" varchar(200) NULL, \"Weekdays\" varchar(32) NOT NULL DEFAULT '', \"StartTime\" time without time zone NOT NULL, "
            + "\"StartDate\" date NOT NULL, \"EndDate\" date NULL, \"InstructorResourceId\" uuid NULL, \"CapacityOverride\" integer NULL, "
            + "\"SkipHolidays\" boolean NOT NULL DEFAULT true, \"Status\" integer NOT NULL DEFAULT 0, \"Notes\" text NULL);",
        """CREATE INDEX IF NOT EXISTS "IX_SiteClassTerms_Company_Service" ON "SiteClassTerms" ("CompanyId", "BookingServiceId");""",
        "CREATE TABLE IF NOT EXISTS \"SiteClassSessions\" (" + Base + ", \"SiteId\" uuid NOT NULL, \"BookingServiceId\" uuid NOT NULL, "
            + "\"TermId\" uuid NULL, \"SessionDate\" date NOT NULL, \"StartTime\" time without time zone NOT NULL, \"EndTime\" time without time zone NOT NULL, "
            + "\"InstructorResourceId\" uuid NULL, \"SubstituteResourceId\" uuid NULL, \"CapacityOverride\" integer NULL, "
            + "\"Status\" integer NOT NULL DEFAULT 0, \"CancelReason\" text NULL, \"CancelledAt\" timestamptz NULL, \"CancelledBy\" text NULL, \"Notes\" text NULL);",
        // หนึ่งรอบต่อคีย์ (สร้างซ้ำ = ไม่ซ้ำ) · แถวที่ลบแล้วไม่กันคีย์
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_SiteClassSessions_Company_Service_Date_Start" ON "SiteClassSessions" ("CompanyId", "BookingServiceId", "SessionDate", "StartTime") WHERE "IsDeleted" = false;""",
        """CREATE INDEX IF NOT EXISTS "IX_SiteClassSessions_Company_Site_Date" ON "SiteClassSessions" ("CompanyId", "SiteId", "SessionDate");""",
        // C9: SiteBookingSlots.CurrentBookings ไม่มีผู้เขียน/ผู้อ่านแล้ว (property ถูกลบ) — คอลัมน์เดิม NOT NULL ไม่มี DEFAULT ⇒ ใส่ DEFAULT 0 ให้ INSERT ใหม่ไม่ล้ม ·
        // ฐานที่สร้างใหม่ไม่มีคอลัมน์นี้ ⇒ ตรวจก่อน (ไม่ log ความล้มเหลวทุกบูต)
        "DO $c9$ BEGIN IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'SiteBookingSlots' AND column_name = 'CurrentBookings') "
            + "THEN ALTER TABLE \"SiteBookingSlots\" ALTER COLUMN \"CurrentBookings\" SET DEFAULT 0; END IF; END $c9$;",
    };
}
