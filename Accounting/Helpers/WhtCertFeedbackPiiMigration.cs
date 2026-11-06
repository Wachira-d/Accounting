using Accounting.Data;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>
/// ฝ่ายค้าน WP-09 รอบสอง L7 (PDPA ม.26/ม.37 · F2 ข้อ 9 "persist แล้วต้องมี migration") — งานล้างข้อมูลครั้งเดียวของ feedback ที่เก็บไว้ของ
/// <see cref="AiFeatureKey.WhtCertAuthenticity"/>: คำขอรุ่นก่อน L7 เก็บเลข 13 หลักของผู้จ่ายบุคคลธรรมดาไว้ใน <c>PromptJson</c> (ช่อง payer_tax_id ·
/// ข้อความ OCR · กุญแจ <c>pt-</c> ที่แปลงตัวเลขเป็นตัวอักษร = ย้อนกลับได้) ⇒ ล้างด้วยตัวเดียวกับคำขอใหม่
/// (<c>WhtCertAuthenticityPrompt.ScrubStoredJson</c>: กุญแจ ⇒ <c>pi-</c> แฮชต่อบริษัท · เลขในข้อความ ⇒ ปิดบัง) ·
/// ครั้งเดียวด้วย marker <see cref="MarkerKey"/> ใน <c>OneShotDataFixes</c> + <c>pg_advisory_xact_lock</c> (<see cref="AdvisoryLockKey.OneShotDataFix"/>) ·
/// ล้ม ⇒ ธุรกรรมย้อน ไม่มี marker ⇒ บูตถัดไปทำใหม่ · audit ต่อบริษัท
/// </summary>
public static class WhtCertFeedbackPiiMigration
{
    public const string MarkerKey = "r252-wht-cert-feedback-pii";
    public const string AuditEntityType = "WhtCertFeedbackPiiScrub";

    /// <summary>คืนจำนวนแถวที่ถูกล้าง (marker มีแล้ว = 0)</summary>
    public static async Task<int> RunAsync(AccountingDbContext db, CancellationToken ct = default)
    {
        try
        {
            return await RunCoreAsync(db, ct);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<int> RunCoreAsync(AccountingDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0})",
            new object[] { AdvisoryLockKey.For(AdvisoryLockKey.OneShotDataFix, MarkerKey) }, ct);
        await db.Database.ExecuteSqlRawAsync(Accounting.Data.DatabaseMigrationHelper.OneShotDataFixesTableSql, ct);
        var markerKey = MarkerKey;
        var done = await db.Database
            .SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "OneShotDataFixes" WHERE "Key" = {markerKey}""")
            .SingleAsync(ct);
        if (done > 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        var feature = AiFeatureKey.WhtCertAuthenticity.ToString();
        var rows = await db.AiSuggestionFeedbacks.IgnoreQueryFilters()
            .Where(f => f.FeatureKey == feature)
            .ToListAsync(ct);
        var perCompany = new Dictionary<Guid, int>();
        foreach (var f in rows)
        {
            var prompt = Accounting.Services.Ai.Prompts.WhtCertAuthenticityPrompt.ScrubStoredJson(f.CompanyId, f.PromptJson) ?? "";
            var response = Accounting.Services.Ai.Prompts.WhtCertAuthenticityPrompt.ScrubStoredJson(f.CompanyId, f.ResponseJson);
            if (prompt == f.PromptJson && response == f.ResponseJson) continue;
            f.PromptJson = prompt;
            f.ResponseJson = response;
            perCompany[f.CompanyId] = perCompany.GetValueOrDefault(f.CompanyId) + 1;
        }
        var now = DateTime.UtcNow;
        foreach (var (companyId, count) in perCompany)
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = companyId,
                UserEmail = "system",
                Action = AuditAction.Update,
                EntityType = AuditEntityType,
                EntityId = companyId.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    scrubbedFeedbackRows = count,
                    feature,
                    note = "ล้างเลข 13 หลักของผู้จ่ายบุคคลธรรมดาใน feedback ตรวจของจริง 50 ทวิ (กุญแจ pt- ⇒ pi- · ปิดบังเลขในข้อความ) — ฝ่ายค้าน WP-09 รอบสอง L7",
                    legalReference = "PDPA ม.26 · ม.37",
                }),
                Timestamp = now,
            });
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            """INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ({0}, (now() at time zone 'utc')) ON CONFLICT DO NOTHING""",
            new object[] { MarkerKey }, ct);
        await tx.CommitAsync(ct);
        return perCompany.Values.Sum();
    }
}
