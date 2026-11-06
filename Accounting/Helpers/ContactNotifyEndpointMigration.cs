using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>ผลของงานย้าย <c>Contact.LineUserId</c> — <paramref name="Skipped"/> = แถวที่ <c>ISecretProtector.Protect</c> ปฏิเสธ (ค่าขึ้นต้น <c>enc1:</c> · ฝ่ายค้าน N1):
/// ไม่สร้างปลายทาง ไม่ล้างคอลัมน์ (คงข้อมูลไว้ให้คนตรวจ) นับลง audit สรุปต่อบริษัท + log ครั้งเดียวที่ผู้เรียก</summary>
public sealed record LegacyLineMigrationResult(int Created, int Skipped);

/// <summary>
/// รอบ 245 L1 (LINE_MULTICHANNEL_DESIGN §4.1/§4.3) — schema ของ <c>ContactNotifyEndpoints</c> + ย้ายข้อมูลเดิมครั้งเดียว
///
/// <para><b>schema</b> (<see cref="Statements"/>): ตารางใหม่ล้วน (IF NOT EXISTS ทุกคำสั่ง · รันทุกบูตได้) · ชื่อ index ตรงกับ <c>AccountingDbContext</c> ·
/// คอลัมน์ครบทั้งของ <c>ContactNotifyEndpoint</c> และ <c>TenantEntity</c>/<c>BaseEntity</c> (Db test เทียบกับโมเดล EF — ฝ่ายค้าน N6) ·
/// <b>ไม่มี unique index</b> บนอีเมลหลัก/LINE id โดยตั้งใจ — การรวมผู้ติดต่อ (<c>MergeContactsAsync</c>) ย้ายทุกคอลัมน์ <c>*ContactId</c> แบบ generic
/// ⇒ unique ระดับฐานทำให้การรวมล้ม · ความไม่ซ้ำคุมใต้ล็อกต่อผู้ติดต่อ: ผู้เขียน <c>ContactNotifyEndpointService</c> + การรวมยกเลิกแถวซ้ำ
/// (<c>ContactRecipientRule.MergeUnlinks</c>) + ตัวเลือกแถวหลักเดียว (<c>ContactRecipientRule.PickPrimary</c>)</para>
///
/// <para><b>ข้อมูลเดิม (F2 ข้อ 9)</b> (<see cref="MigrateLegacyLineUserIdsAsync"/> · marker <see cref="MarkerKey"/> ใน <c>OneShotDataFixes</c>):
/// <c>Contact.LineUserId</c> ที่มีค่า ⇒ แถว <c>LineUser</c> ที่ <c>OaScope = unknown</c> · <c>Verified = false</c> · <b>ไม่ใช้ส่ง</b>
/// (userId ใช้ได้เฉพาะ OA ที่ออกค่านั้น — เดา OA แล้วส่ง = 400 หรือส่งผิดคน) · id เข้ารหัสด้วย <see cref="ISecretProtector"/> (ทำใน SQL ไม่ได้ จึงเป็นงานในโค้ด) ·
/// แล้วล้างคอลัมน์เดิมด้วย SQL ตรง (ไม่ผ่าน ChangeTracker ⇒ ค่าเปล่าไม่ไหลเข้า audit ที่ลบไม่ได้) · ผู้ติดต่อที่ถูกลบแล้ว ⇒ ล้างคอลัมน์อย่างเดียว</para>
/// <para>ผู้เขียนคอลัมน์ <c>Contact.LineUserId</c>: ไม่มีในเรพ (ค่ามาจากการ import/กรอกตรงในฐาน) · ผู้อ่านก่อนรอบนี้: แจ้งบัตรคอร์สใกล้หมดอายุ
/// (<c>CourseCreditService.Membership</c> — ทางสำรองของ <c>BookingNotifier</c>) ถูก<b>ถอดในรอบนี้</b> (ฝ่ายค้าน S1 · เสี่ยงส่งผิดคน) ·
/// <c>DocumentLineDeliveryService</c> อ่านแต่ไม่มีผู้เรียก · PDPA erase ล้างค่า ⇒ หลังรอบนี้ไม่มีเส้นส่งใดใช้คอลัมน์นี้</para>
/// <para><b><c>Contact.Email</c> ไม่ถูกคัดลอก</b> (DESIGN §4.3 · ไม่สร้างความจริงสองแหล่ง): ตัวตัดสินอ่าน <c>Contact.Email</c> สดเป็นอีเมลหลักเสมอ</para>
/// <para>ครั้งเดียวจริง: <c>pg_advisory_xact_lock</c> ทั้งระบบ + ตรวจ marker ในธุรกรรมเดียวกับการย้าย ⇒ หลายเครื่องบูตพร้อมกันทำครั้งเดียว ·
/// ล้ม ⇒ ธุรกรรมย้อน + marker ไม่ถูกเขียน + <b>ล้าง ChangeTracker</b> (ฝ่ายค้าน B1: context ตอนบูตใช้ต่อ — แถวที่ค้างจะหลุดลงฐานกับ SaveChanges ครั้งถัดไป) ·
/// Db test: <c>ContactNotifyEndpointRound245DbTests</c></para>
/// </summary>
public static class ContactNotifyEndpointMigration
{
    public const string MarkerKey = "r245-contact-line-legacy";
    public const string Actor = "migration:r245-contact-notify";
    /// <summary>EntityType ของแถว audit สรุปการย้ายต่อบริษัท</summary>
    public const string AuditEntityType = "ContactNotifyEndpoint.LegacyMigration";

    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """
        CREATE TABLE IF NOT EXISTS "ContactNotifyEndpoints" (
            "Id" uuid NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
            "CompanyId" uuid NOT NULL,
            "ContactId" uuid NOT NULL,
            "Kind" varchar(16) NOT NULL,
            "Label" varchar(100) NULL,
            "Email" varchar(254) NULL,
            "PortalAccessId" uuid NULL,
            "LineUserId" text NULL,
            "LineGroupId" text NULL,
            "LineIdHash" varchar(64) NULL,
            "OaScope" varchar(16) NOT NULL DEFAULT '',
            "OaDestination" varchar(64) NULL,
            "GroupNameSnapshot" varchar(200) NULL,
            "MemberCountSnapshot" integer NULL,
            "TopicsJson" text NOT NULL DEFAULT '[]',
            "ShowAmounts" boolean NOT NULL DEFAULT true,
            "IsPrimary" boolean NOT NULL DEFAULT false,
            "Verified" boolean NOT NULL DEFAULT false,
            "Source" varchar(32) NOT NULL DEFAULT '',
            "ConsentRecordId" uuid NULL,
            "BoundByUserId" uuid NULL,
            "LinkedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "BlockedAt" timestamp without time zone NULL,
            "UnlinkedAt" timestamp without time zone NULL,
            "UnlinkReason" varchar(32) NULL,
            "LastSentAt" timestamp without time zone NULL,
            "LastFailure" varchar(500) NULL,
            "CreatedAt" timestamp without time zone NOT NULL DEFAULT (now() at time zone 'utc'),
            "CreatedBy" text NULL,
            "UpdatedAt" timestamp without time zone NULL,
            "UpdatedBy" text NULL,
            "IsDeleted" boolean NOT NULL DEFAULT false,
            CONSTRAINT "FK_ContactNotifyEndpoints_Companies_CompanyId" FOREIGN KEY ("CompanyId") REFERENCES "Companies"("Id") ON DELETE CASCADE,
            CONSTRAINT "FK_ContactNotifyEndpoints_Contacts_ContactId" FOREIGN KEY ("ContactId") REFERENCES "Contacts"("Id") ON DELETE CASCADE
        );
        """,
        """CREATE INDEX IF NOT EXISTS "IX_ContactNotifyEndpoints_Company_Contact" ON "ContactNotifyEndpoints" ("CompanyId", "ContactId") WHERE "UnlinkedAt" IS NULL;""",
        """CREATE INDEX IF NOT EXISTS "IX_ContactNotifyEndpoints_LineIdHash" ON "ContactNotifyEndpoints" ("LineIdHash");""",
        """CREATE INDEX IF NOT EXISTS "IX_ContactNotifyEndpoints_ContactId" ON "ContactNotifyEndpoints" ("ContactId");""",
        """CREATE TABLE IF NOT EXISTS "OneShotDataFixes" ("Key" varchar(100) PRIMARY KEY, "AppliedAt" timestamp NOT NULL);""",
    };

    /// <summary>ย้าย <c>Contact.LineUserId</c> → แถว legacy (ไม่ใช้ส่ง) ครั้งเดียว · marker มีแล้ว = (0, 0) · ล้มกลางทาง ⇒ โยนต่อ (ผู้เรียก log Error) หลังล้าง ChangeTracker</summary>
    public static async Task<LegacyLineMigrationResult> MigrateLegacyLineUserIdsAsync(AccountingDbContext db, ISecretProtector secrets, CancellationToken ct = default)
    {
        try
        {
            return await MigrateCoreAsync(db, secrets, ct);
        }
        catch
        {
            // ฝ่ายค้าน B1: ธุรกรรมย้อนแล้ว แต่แถวที่ Add ไว้ยังอยู่ใน ChangeTracker ของ context ที่บูตใช้ต่อ ⇒ ทิ้งก่อนโยนต่อ
            db.ChangeTracker.Clear();
            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<LegacyLineMigrationResult> MigrateCoreAsync(AccountingDbContext db, ISecretProtector secrets, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(AdvisoryLockKey.OneShotDataFix, MarkerKey) }, ct);
        var markerKey = MarkerKey;   // ตัวแปร (ไม่ใช่ const) ⇒ เป็นพารามิเตอร์ของ FormattableString แน่นอน
        var done = await db.Database
            .SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "OneShotDataFixes" WHERE "Key" = {markerKey}""")
            .SingleAsync(ct);
        if (done > 0)
        {
            await tx.CommitAsync(ct);
            return new LegacyLineMigrationResult(0, 0);
        }

        var legacy = await db.Contacts.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.LineUserId != null && c.LineUserId != "")
            .Select(c => new { c.Id, c.CompanyId, c.LineUserId, c.IsDeleted })
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        var created = 0;
        var skippedIds = new HashSet<Guid>();
        var perCompany = new Dictionary<Guid, (int Moved, int Skipped)>();
        foreach (var c in legacy.Where(c => !c.IsDeleted))
        {
            var id = c.LineUserId!.Trim();
            if (id.Length == 0) continue;
            var ep = new ContactNotifyEndpoint();
            try { ep.LineUserId = secrets.Protect(id); }   // เขียนลงช่องตรง ๆ ⇒ secret_registry_check เห็นช่องปลายทาง (ลงทะเบียนใน SecretReprotectJob)
            catch (BusinessRuleException)
            {
                // ฝ่ายค้าน N1: ค่าที่ดูเป็น ciphertext อยู่แล้ว (enc1:) — ไม่เดา ไม่ล้าง · นับลง audit/log · งานยังปิดได้
                skippedIds.Add(c.Id);
                var s = perCompany.GetValueOrDefault(c.CompanyId);
                perCompany[c.CompanyId] = (s.Moved, s.Skipped + 1);
                continue;
            }
            ep.CompanyId = c.CompanyId;
            ep.ContactId = c.Id;
            ep.Kind = NotifyEndpointKind.LineUser;
            ep.LineIdHash = NotifyEndpointKind.LineIdHash(null, id);
            ep.OaScope = NotifyEndpointKind.ScopeUnknown;
            ep.Verified = false;
            ep.Source = NotifyEndpointKind.SourceLegacyLineUserId;
            ep.TopicsJson = NotifyTopics.Serialize(NotifyTopics.DefaultsFor(NotifyEndpointKind.LineUser));
            ep.ShowAmounts = true;
            ep.LinkedAt = now;
            ep.CreatedAt = now;
            ep.CreatedBy = Actor;
            db.ContactNotifyEndpoints.Add(ep);
            created++;
            var m = perCompany.GetValueOrDefault(c.CompanyId);
            perCompany[c.CompanyId] = (m.Moved + 1, m.Skipped);
        }
        foreach (var (companyId, count) in perCompany)
        {
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserEmail = Actor,
                Action = Accounting.Models.Enums.AuditAction.Update,
                EntityType = AuditEntityType,
                EntityId = companyId.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    migratedLineUserIds = count.Moved,
                    skippedUnprotectable = count.Skipped,
                    oaScope = NotifyEndpointKind.ScopeUnknown,
                    note = "ย้าย Contact.LineUserId เป็นปลายทาง legacy (ไม่ใช้ส่ง · ต้องผูกใหม่) และล้างคอลัมน์เดิม — รอบ 245 L1 · ค่าที่เข้ารหัสไม่ได้ถูกข้ามและคงไว้",
                }),
                Timestamp = now,
            });
        }
        await db.SaveChangesAsync(ct);
        var ids = legacy.Where(c => !skippedIds.Contains(c.Id)).Select(c => c.Id).ToArray();
        if (ids.Length > 0)
            await db.Database.ExecuteSqlRawAsync("""UPDATE "Contacts" SET "LineUserId" = NULL WHERE "Id" = ANY({0})""", new object[] { ids }, ct);
        await db.Database.ExecuteSqlRawAsync(
            """INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ({0}, (now() at time zone 'utc')) ON CONFLICT DO NOTHING""",
            new object[] { MarkerKey }, ct);
        await tx.CommitAsync(ct);
        return new LegacyLineMigrationResult(created, skippedIds.Count);
    }
}
