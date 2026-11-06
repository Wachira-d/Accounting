using Accounting.Data;
using Accounting.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Accounting.Helpers;

/// <summary>รอบ 246 QR — คอลัมน์ "QR ชำระเงิน" ของบัญชีธนาคาร + ธงของเทมเพลตเอกสาร (<c>ADD COLUMN IF NOT EXISTS</c> — idempotent ·
/// ตรงกับโมเดลใน <c>AccountingDbContext</c>) · ผู้เรียก: <c>DatabaseMigrationHelper</c> ·
/// ค่าตั้งต้น: บัญชีไม่มี QR (NULL) ⇒ ไม่มีอะไรพิมพ์เพิ่มจนกว่าจะตั้ง · เทมเพลต <c>ShowPaymentQr</c>/<c>ShowPayLinkQr</c> = true (คำตัดสิน 246-QR-7 "บริษัทเปิดเป็นค่าตั้งต้น")</summary>
public static class PaymentQrMigration
{
    public const string MarkerKey = "r246-template-promptpay-to-bank";
    public const string Actor = "migration:round246";

    public static IReadOnlyList<string> Statements() => new List<string>
    {
        """ALTER TABLE "BankAccounts" ADD COLUMN IF NOT EXISTS "PromptPayId" varchar(13) NULL;""",
        """ALTER TABLE "BankAccounts" ADD COLUMN IF NOT EXISTS "PaymentQrSource" varchar(16) NOT NULL DEFAULT 'auto';""",
        """ALTER TABLE "BankAccounts" ADD COLUMN IF NOT EXISTS "PaymentQrImagePath" text NULL;""",
        """ALTER TABLE "BankAccounts" ADD COLUMN IF NOT EXISTS "PaymentQrUploadedAt" timestamptz NULL;""",
        """ALTER TABLE "DocumentTemplates" ADD COLUMN IF NOT EXISTS "ShowPaymentQr" boolean NOT NULL DEFAULT true;""",
        """ALTER TABLE "DocumentTemplates" ADD COLUMN IF NOT EXISTS "ShowPayLinkQr" boolean NOT NULL DEFAULT true;""",
        """ALTER TABLE "DocumentTemplates" ADD COLUMN IF NOT EXISTS "PaymentQrBankAccountId" uuid NULL;""",
        // ฝ่ายค้าน N3: ถอด ShowQrCode/QrCodeType/QrCodeCustomData ออกจากโมเดล — ฐานเดิม (สร้างด้วย EnsureCreated) มีคอลัมน์ NOT NULL ไม่มี DEFAULT ⇒
        // ตั้ง DEFAULT ก่อน มิฉะนั้น INSERT เทมเพลตใหม่ (ไม่ส่งคอลัมน์) ล้ม · ฐานใหม่ไม่มีคอลัมน์ ⇒ ข้าม (DO block ตรวจ information_schema)
        """
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'DocumentTemplates' AND column_name = 'ShowQrCode') THEN
                ALTER TABLE "DocumentTemplates" ALTER COLUMN "ShowQrCode" SET DEFAULT false;
            END IF;
            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'DocumentTemplates' AND column_name = 'QrCodeType') THEN
                ALTER TABLE "DocumentTemplates" ALTER COLUMN "QrCodeType" SET DEFAULT 'PromptPay';
            END IF;
            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'DocumentTemplates' AND column_name = 'QrCodeCustomData') THEN
                ALTER TABLE "DocumentTemplates" ALTER COLUMN "QrCodeCustomData" DROP NOT NULL;
            END IF;
        END $$;
        """,
        // marker ของงานย้ายครั้งเดียว (ตารางเดียวกับรอบ 245 — สร้างซ้ำได้)
        """CREATE TABLE IF NOT EXISTS "OneShotDataFixes" ("Key" varchar(100) PRIMARY KEY, "AppliedAt" timestamp NOT NULL);""",
    };

    public sealed record LegacyMoveResult(int Moved, int Skipped);

    /// <summary>
    /// ฝ่ายค้าน N3 — ย้าย <c>DocumentTemplate.PromptPayId</c> เดิม (ไม่มีตัวเรนเดอร์อ่านมาก่อน) ไปที่บัญชีธนาคาร <b>ครั้งเดียว</b>
    /// เฉพาะเมื่อ: เทมเพลตของบริษัทมีเลขพร้อมเพย์ที่ใช้ได้<b>ค่าเดียว</b> · บริษัทมีบัญชีธนาคาร (ไม่ลบ) <b>บัญชีเดียว</b> · บัญชีนั้นยังไม่มีเลขพร้อมเพย์ ·
    /// กรณีอื่น (หลายค่า · เลขผิดรูป · หลาย/ไม่มีบัญชี · บัญชีมีเลขแล้ว) = ไม่ย้าย ไม่เดา และจดเหตุใน audit ของบริษัท (ผู้ใช้ตั้งเองที่หน้าบัญชีธนาคาร) ·
    /// คอลัมน์เดิมของเทมเพลตคงไว้ (ไม่มีผู้อ่าน) · marker <see cref="MarkerKey"/> + ล็อกต่อธุรกรรม ⇒ รันซ้ำ/หลายเครื่องพร้อมกันไม่ย้ายซ้ำ
    /// </summary>
    public static async Task<LegacyMoveResult> MoveLegacyTemplatePromptPayAsync(AccountingDbContext db, CancellationToken ct = default)
    {
        try
        {
            return await MoveCoreAsync(db, ct);
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static async Task<LegacyMoveResult> MoveCoreAsync(AccountingDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var markerKey = MarkerKey;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtext({markerKey}))", ct);
        var done = await db.Database
            .SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "OneShotDataFixes" WHERE "Key" = {markerKey}""")
            .SingleAsync(ct);
        if (done > 0)
        {
            await tx.CommitAsync(ct);
            return new LegacyMoveResult(0, 0);
        }

        var legacy = await db.DocumentTemplates.IgnoreQueryFilters().AsNoTracking()
            .Where(t => !t.IsDeleted && t.PromptPayId != null && t.PromptPayId != "")
            .Select(t => new { t.CompanyId, t.PromptPayId })
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        int moved = 0, skipped = 0;
        foreach (var g in legacy.GroupBy(x => x.CompanyId))
        {
            var ids = g.Select(x => PaymentQrPolicy.NormalizePromptPayId(x.PromptPayId).Id).Distinct().ToList();
            var banks = await db.Set<BankAccount>().Where(b => b.CompanyId == g.Key && !b.IsDeleted).ToListAsync(ct);
            string? reason = null;
            if (ids.Count != 1 || ids[0] == null) reason = "เลขพร้อมเพย์ในเทมเพลตมีหลายค่าหรือผิดรูป";
            else if (banks.Count != 1) reason = $"บริษัทมีบัญชีธนาคาร {banks.Count} บัญชี (ย้ายได้เฉพาะเมื่อมีบัญชีเดียว)";
            else if (banks[0].PromptPayId != null) reason = "บัญชีธนาคารมีเลขพร้อมเพย์อยู่แล้ว";
            if (reason == null)
            {
                banks[0].PromptPayId = ids[0];
                banks[0].UpdatedAt = now;
                banks[0].UpdatedBy = Actor;
                moved++;
            }
            else skipped++;
            db.AddChainedAuditLog(new AuditLog
            {
                CompanyId = g.Key, UserEmail = Actor, Action = Accounting.Models.Enums.AuditAction.Update,
                EntityType = nameof(BankAccount), EntityId = reason == null ? banks[0].Id.ToString() : g.Key.ToString(),
                NewValues = System.Text.Json.JsonSerializer.Serialize(new
                {
                    @event = reason == null ? "PaymentQrLegacyTemplatePromptPayMoved" : "PaymentQrLegacyTemplatePromptPaySkipped",
                    promptPayId = reason == null ? ids[0] : null,
                    reason,
                    note = "รอบ 246 QR: ช่อง PromptPayId ของเทมเพลตไม่มีผลบนเอกสารมาก่อน — ย้ายไปบัญชีธนาคารเมื่อชัดเจน · ไม่ย้าย = ตั้งเองที่หน้าบัญชีธนาคาร",
                    ruleCode = PaymentQrPolicy.RuleCode,
                }),
                Timestamp = now,
            });
        }
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "OneShotDataFixes" ("Key", "AppliedAt") VALUES ({markerKey}, (now() at time zone 'utc')) ON CONFLICT DO NOTHING""", ct);
        await tx.CommitAsync(ct);
        return new LegacyMoveResult(moved, skipped);
    }
}
