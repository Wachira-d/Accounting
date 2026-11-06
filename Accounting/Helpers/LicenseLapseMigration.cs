namespace Accounting.Helpers;

/// <summary>รอบ 238 (คำตัดสิน 236-3) — ซ่อมข้อมูลที่งานเตือน License (<c>AccountPlanExpiryReminderJob</c>) ก่อนรอบนี้ประทับผิด · <b>รันครั้งเดียว</b> (ฝ่ายค้าน B1/S4)
/// <para>ครั้งเดียวจริง: ตาราง marker <c>OneShotDataFixes</c> (คีย์ <see cref="MarkerKey"/>) — DO-block ทำงานเฉพาะเมื่อยังไม่มีแถว marker แล้วใส่ marker ในก้อนเดียวกัน ⇒
/// บูตถัดไปไม่สแกน/ไม่เปลี่ยนอะไร (กันทับสถานะที่แอดมิน/ผู้ใช้ตั้งภายหลัง) · ไม่ขึ้นกับวันปล่อย</para>
/// <list type="number">
/// <item><b>แถวบริษัทที่ถูก cascade</b> (<see cref="RestoreCascadedSql"/>): งานเตือนเดิมตั้งแถว <c>Subscriptions</c> ของบริษัทใต้ License เป็น Expired ตอน License หมดอายุ
/// (ประวัติ <c>CascadeExpiredFromLicense</c>) · คืน <c>FromStatus</c> <b>เฉพาะ</b>แถวที่ประวัติการเปลี่ยนสถานะล่าสุดยังเป็น cascade + ยัง Expired <b>และปลอดภัย</b> (ฝ่ายค้าน B2):
/// ยังผูก License อยู่ (สถานะแถวไม่มีผลจนถอด — ตอนถอด <c>CompanyStatusAfterDetach</c> ตั้งปลายทางที่ตรงจริง) หรือฟรีถาวร หรือวันหมดอายุของแถวยังไม่ถึง ·
/// แถวที่ถอดแล้วและหมดอายุจริง ⇒ คง Expired (อ่านอย่างเดียว · ซื้อได้) · เขียนประวัติ <c>CascadeExpiredRestored</c></item>
/// <item><b>License ที่ Expired ทั้งที่ยังอยู่ในระยะผ่อนผัน</b> (<see cref="GraceLicenseSql"/>) ⇒ PastDue (สูตรเดียวกับ <see cref="PlanIdentityResolver.LicenseStatusAfter"/>) ·
/// <b>ยกเว้น</b> License ที่แอดมินเคยเปลี่ยนสถานะเอง (<c>LicenseStatusChanged</c> ใน audit/ประวัติ — <c>AdminAccountSubscriptionController.ChangeStatus</c>) = คงตามที่แอดมินตั้ง (ฝ่ายค้าน B1)</item>
/// </list>
/// Db test: <c>LicenseLapseRound238DbTests</c></summary>
public static class LicenseLapseMigration
{
    /// <summary>ผู้ประทับในคอลัมน์ UpdatedBy/PerformedBy</summary>
    public const string Actor = "migration:r238-license-lapse";

    /// <summary>ชื่อประวัติที่ migration เขียนเมื่อคืนสถานะแถวบริษัท</summary>
    public const string RestoredAction = "CascadeExpiredRestored";

    /// <summary>คีย์ marker ใน <c>OneShotDataFixes</c></summary>
    public const string MarkerKey = "r238-license-lapse";

    private static readonly string Expired = ((int)Accounting.Models.Enums.SubscriptionStatus.Expired).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string PastDue = ((int)Accounting.Models.Enums.SubscriptionStatus.PastDue).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>(1) คืนสถานะแถวบริษัทที่ถูก cascade — เฉพาะแถวปลอดภัย (ผู้เรียก: <see cref="Statements"/> ใน DO-block + Db test)</summary>
    internal static string RestoreCascadedSql()
    {
        return $"""
        WITH latest AS (
            SELECT DISTINCT ON (h."SubscriptionId") h."SubscriptionId", h."Action", h."FromStatus", h."AccountSubscriptionId"
            FROM "SubscriptionHistories" h
            WHERE h."IsDeleted" = false
              AND h."Action" NOT LIKE 'AccountPlanReminder.%'
              AND h."Action" NOT LIKE 'License%'
            ORDER BY h."SubscriptionId", h."CreatedAt" DESC, h."Id" DESC
        ), restored AS (
            UPDATE "Subscriptions" s
            SET "Status" = l."FromStatus", "UpdatedAt" = (now() at time zone 'utc'), "UpdatedBy" = '{Actor}'
            FROM latest l
            WHERE l."SubscriptionId" = s."Id"
              AND l."Action" = 'CascadeExpiredFromLicense'
              AND l."FromStatus" IS NOT NULL AND l."FromStatus" <> {Expired}
              AND s."Status" = {Expired} AND s."IsDeleted" = false
              AND (s."AccountSubscriptionId" IS NOT NULL OR s."IsPermanentFree" = true OR s."EndDate" > (now() at time zone 'utc'))
            RETURNING s."Id", l."FromStatus", l."AccountSubscriptionId"
        )
        INSERT INTO "SubscriptionHistories" ("Id", "SubscriptionId", "AccountSubscriptionId", "Action", "FromStatus", "ToStatus", "Notes", "PerformedBy", "CreatedAt", "IsDeleted")
        SELECT gen_random_uuid(), r."Id", r."AccountSubscriptionId", '{RestoredAction}', {Expired}, r."FromStatus",
               'รอบ 238 (คำตัดสิน 236-3): คืนสถานะแพ็กเกจของบริษัทที่งานเตือน License เคยตั้งเป็นหมดอายุตาม License (แถวบริษัทที่ License คุมไม่ถูกเดินสถานะแล้ว)',
               '{Actor}', (now() at time zone 'utc'), false
        FROM restored r
        """;
    }

    /// <summary>(2) License ที่ Expired ในระยะผ่อนผัน ⇒ PastDue · ไม่แตะ License ที่แอดมินเปลี่ยนสถานะเอง (ผู้เรียก: <see cref="Statements"/> + Db test)</summary>
    internal static string GraceLicenseSql()
    {
        return $"""
        UPDATE "AccountSubscriptions" a
        SET "Status" = {PastDue}, "UpdatedAt" = (now() at time zone 'utc'), "UpdatedBy" = '{Actor}'
        WHERE a."Status" = {Expired} AND a."IsDeleted" = false
          AND (now() at time zone 'utc') > a."EndDate"
          AND (now() at time zone 'utc') <= a."EndDate" + make_interval(days => GREATEST(a."GracePeriodDays", 0))
          AND NOT EXISTS (SELECT 1 FROM "SubscriptionHistories" h
                          WHERE h."AccountSubscriptionId" = a."Id" AND h."Action" = 'LicenseStatusChanged')
          AND NOT EXISTS (SELECT 1 FROM "AuditLogs" al
                          WHERE al."EntityType" = 'AccountSubscription' AND al."EntityId" = a."Id"::text
                            AND al."NewValues" LIKE '%LicenseStatusChanged%')
        """;
    }

    /// <summary>คำสั่งบูต: DO-block ที่ซ่อมครั้งเดียวแล้วประทับ marker ในก้อนเดียวกัน · ตาราง marker <c>OneShotDataFixes</c> สร้างที่
    /// <c>DatabaseMigrationHelper.OneShotDataFixesTableSql</c> ตัวเดียว (อยู่ก่อนบล็อกนี้ใน <c>GetAlterStatements</c> · ฐานเทสต์สร้างตอนเริ่ม) —
    /// เดิมบล็อกนี้สร้างตารางซ้ำเอง = สองตัวตั้งของ schema เดียว (CI 42232967230 · GateOppositeDirectionRound250Tests)</summary>
    public static IReadOnlyList<string> Statements()
    {
        return new List<string>
    {
        $"""
        DO $r238$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM "OneShotDataFixes" WHERE "Key" = '{MarkerKey}') THEN
                {RestoreCascadedSql()};
                {GraceLicenseSql()};
                INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ('{MarkerKey}', (now() at time zone 'utc')) ON CONFLICT DO NOTHING;
            END IF;
        END $r238$;
        """,
        };
    }
}
