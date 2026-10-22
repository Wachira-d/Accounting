namespace Accounting.Helpers;

/// <summary>คอลัมน์/ตารางของ engine รอบจองคิว (CMS booking · <see cref="BookingSlotEngine"/>) — <b>รันทุกบูตได้</b> (IF NOT EXISTS) ·
/// ค่า DEFAULT = พฤติกรรมเดิมทุกตัว ⇒ บริการ/การจองที่มีอยู่ทำงานเหมือนเดิม:
/// <c>SlotMode</c> 0 = ช่วงกำหนดเอง (แต่ละแถวเวลา = 1 รอบ) · <c>BufferBeforeMinutes</c> 0 · <c>CapacityUnit</c> 0 = นับเป็นคิว ·
/// <c>MaxUnitsPerBooking</c> 1 · <c>EnforceSlotCapacity</c> true · <c>WaitlistEnabled</c> false · <c>ResourceMode</c> 0 = ไม่ใช้ผู้ให้บริการ ·
/// <c>UseSiteDefaults</c> false = ใช้ค่าของบริการเอง · ช่วงห่าง/เวลาแบ่ง/กลุ่มทรัพยากร/เพดานซ้อน = NULL · การจองเดิม <c>Units</c> = 1 · <c>ResourceId</c> NULL ·
/// ไม่ UPDATE ข้อมูลเดิม (ไม่แต่งเวลาทำการให้บริการที่ยังไม่ตั้ง — หน้า CMS เตือนแทน) · ตาราง/index ใหม่ต้องตรงกับ <c>AccountingDbContext</c></summary>
public static class BookingEngineMigration
{
    private const string Base = "\"Id\" uuid PRIMARY KEY, \"CompanyId\" uuid NOT NULL, \"CreatedAt\" timestamptz NOT NULL DEFAULT now(), "
        + "\"UpdatedAt\" timestamptz NULL, \"CreatedBy\" text NULL, \"UpdatedBy\" text NULL, \"IsDeleted\" boolean NOT NULL DEFAULT false";

    public static IReadOnlyList<string> Statements() => new[]
    {
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "SlotMode" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "SlotIntervalMinutes" integer NULL;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "BufferBeforeMinutes" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "CapacityUnit" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "MaxUnitsPerBooking" integer NOT NULL DEFAULT 1;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "HalfDaySplitTime" varchar(8) NULL;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "CapacityGroup" varchar(64) NULL;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "EnforceSlotCapacity" boolean NOT NULL DEFAULT true;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "OverlapLimit" integer NULL;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "WaitlistEnabled" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "ResourceMode" integer NOT NULL DEFAULT 0;""",
        """ALTER TABLE "SiteBookingServices" ADD COLUMN IF NOT EXISTS "UseSiteDefaults" boolean NOT NULL DEFAULT false;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "Units" integer NOT NULL DEFAULT 1;""",
        """ALTER TABLE "SiteBookings" ADD COLUMN IF NOT EXISTS "ResourceId" uuid NULL;""",
        // ด่านทับซ้อนอ่านการจองต่อบริการต่อวัน
        """CREATE INDEX IF NOT EXISTS "IX_SiteBookings_Service_Date" ON "SiteBookings" ("BookingServiceId", "BookingDate");""",

        // ค่าตั้งการจองระดับไซต์
        "CREATE TABLE IF NOT EXISTS \"SiteBookingSettings\" (" + Base + ", \"SiteId\" uuid NOT NULL, \"DurationMinutes\" integer NOT NULL DEFAULT 60, "
            + "\"BufferBeforeMinutes\" integer NOT NULL DEFAULT 0, \"BufferAfterMinutes\" integer NOT NULL DEFAULT 0, "
            + "\"MinAdvanceHours\" integer NOT NULL DEFAULT 1, \"MaxAdvanceDays\" integer NOT NULL DEFAULT 30);",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_SiteBookingSettings_Company_Site" ON "SiteBookingSettings" ("CompanyId", "SiteId");""",

        // ผู้ให้บริการ / ห้อง / อุปกรณ์
        "CREATE TABLE IF NOT EXISTS \"SiteBookingResources\" (" + Base + ", \"SiteId\" uuid NOT NULL, \"Name\" varchar(256) NOT NULL, "
            + "\"Kind\" integer NOT NULL DEFAULT 0, \"Description\" text NULL, \"ImageUrl\" text NULL, \"Capacity\" integer NOT NULL DEFAULT 1, "
            + "\"SortOrder\" integer NOT NULL DEFAULT 0, \"IsActive\" boolean NOT NULL DEFAULT true);",
        """CREATE INDEX IF NOT EXISTS "IX_SiteBookingResources_Company_Site" ON "SiteBookingResources" ("CompanyId", "SiteId");""",
        "CREATE TABLE IF NOT EXISTS \"SiteBookingResourceServices\" (" + Base + ", \"ResourceId\" uuid NOT NULL REFERENCES \"SiteBookingResources\"(\"Id\") ON DELETE CASCADE, "
            + "\"BookingServiceId\" uuid NOT NULL);",
        """CREATE UNIQUE INDEX IF NOT EXISTS "UX_SiteBookingResourceServices_Res_Svc" ON "SiteBookingResourceServices" ("ResourceId", "BookingServiceId");""",
        "CREATE TABLE IF NOT EXISTS \"SiteBookingResourceSlots\" (" + Base + ", \"ResourceId\" uuid NOT NULL REFERENCES \"SiteBookingResources\"(\"Id\") ON DELETE CASCADE, "
            + "\"DayOfWeek\" integer NULL, \"SpecificDate\" date NULL, \"StartTime\" time without time zone NOT NULL, "
            + "\"EndTime\" time without time zone NOT NULL, \"IsBlocked\" boolean NOT NULL DEFAULT false);",
        """CREATE INDEX IF NOT EXISTS "IX_SiteBookingResourceSlots_ResourceId" ON "SiteBookingResourceSlots" ("ResourceId");""",
    };
}
