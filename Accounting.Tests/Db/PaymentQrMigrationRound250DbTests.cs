using System.Text.Json;
using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 250 WP-04 (AUDIT_249 §WP-04 ข้อ 1 · E M-3) — งานย้ายข้อมูลตอนบูต <see cref="PaymentQrMigration.MoveLegacyTemplatePromptPayAsync"/> บน PostgreSQL จริง
/// (ลอกแบบ <c>ContactNotifyEndpointRound245DbTests</c> เคส R245g): 4 ทิศ — (1) บริษัทที่ชัดเจน (เทมเพลตมีเลขเดียว · บัญชีเดียว · บัญชียังไม่มีเลข) ย้าย ·
/// (2) บริษัทที่มี 2 บัญชี ไม่ย้าย ไม่เดา (audit บอกเหตุ) · (3) บัญชีที่มีเลขพร้อมเพย์อยู่แล้วไม่ถูกทับ · (4) บูตซ้ำ (marker มีแล้ว) = ไม่ทำอะไร แม้มีบริษัทใหม่ที่เข้าเงื่อนไข ·
/// คีย์ล็อก = <c>AdvisoryLockKey.For(OneShotDataFix, MarkerKey)</c> (เดิม hashtext 32 บิต) · ตาราง marker สร้างที่ <c>DatabaseMigrationHelper</c> ตัวเดียว
/// <para>marker เป็นของทั้งฐาน ⇒ ทุกทิศอยู่ในเทสต์เดียว (คลาสเดียว = รันทีละเทสต์) · ตรวจเฉพาะแถวของบริษัทที่สร้างในเทสต์นี้</para>
/// </summary>
[Trait("Category", "Db")]
public class PaymentQrMigrationRound250DbTests
{
    private readonly ITestOutputHelper _out;
    public PaymentQrMigrationRound250DbTests(ITestOutputHelper output) => _out = output;

    private static async Task<Guid> CompanyAsync(AccountingDbContext db, string tag, string templatePromptPay, params string?[] bankPromptPays)
    {
        var co = new Company { Name = "บริษัทย้าย QR 250 " + tag, TaxId = "0105556000009" };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        db.DocumentTemplates.Add(new DocumentTemplate { CompanyId = co.Id, Name = "แม่แบบเดิม " + tag, DocumentType = DocumentType.Invoice, PromptPayId = templatePromptPay });
        var n = 0;
        foreach (var pp in bankPromptPays)
            db.Set<BankAccount>().Add(new BankAccount
            {
                CompanyId = co.Id, AccountName = "บัญชี " + tag + " " + ++n, BankName = "กสิกรไทย", AccountNumber = "12345678" + n, PromptPayId = pp,
            });
        await db.SaveChangesAsync();
        return co.Id;
    }

    /// <summary>แถว audit ของงานย้าย (ไม่นับแถว Create/Update อัตโนมัติของ EF) — อ่านชื่อเหตุการณ์จาก JSON (ข้อความไทยถูกหนีเป็น \uXXXX)</summary>
    private static async Task<List<JsonElement>> MigrationAuditAsync(AccountingDbContext db, Guid companyId)
        => (await db.AuditLogs.AsNoTracking()
                .Where(a => a.CompanyId == companyId && a.EntityType == nameof(BankAccount) && a.Action == AuditAction.Update
                    && a.UserEmail == PaymentQrMigration.Actor)
                .Select(a => a.NewValues!).ToListAsync())
            .Select(j => JsonDocument.Parse(j).RootElement.Clone()).ToList();

    private static Task<int> MarkerCountAsync(AccountingDbContext db)
    {
        var key = PaymentQrMigration.MarkerKey;   // ตัวแปร ⇒ พารามิเตอร์
        return db.Database.SqlQuery<int>($"""SELECT COUNT(*)::int AS "Value" FROM "OneShotDataFixes" WHERE "Key" = {key}""").SingleAsync();
    }

    [Fact]
    public async Task ย้ายถูกบริษัท_สองบัญชีไม่เดา_บัญชีมีเลขแล้วไม่ทับ_บูตซ้ำไม่ทำอะไร()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL (ACCOUNTING_TEST_PG) — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        // ตาราง marker มาจาก DatabaseMigrationHelper ตัวเดียว (DbTestDatabase รันตอนสร้างฐาน) — ไม่อยู่ใน Statements() ของงานนี้แล้ว
        // (คอลัมน์ของงานนี้มีจากโมเดลแล้ว — ไม่รัน ALTER TABLE ระหว่างเทสต์ขนาน · E M-4)
        Assert.DoesNotContain(PaymentQrMigration.Statements(), sql => sql.Contains("OneShotDataFixes"));

        var tag = Guid.NewGuid().ToString("N")[..6];
        var clear = await CompanyAsync(db, "ชัด " + tag, "081-234-5678", (string?)null);
        var two = await CompanyAsync(db, "สองบัญชี " + tag, "0812345678", null, null);
        var has = await CompanyAsync(db, "มีเลขแล้ว " + tag, "0812345678", "0899999999");
        db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "OneShotDataFixes" WHERE "Key" = {0}""", PaymentQrMigration.MarkerKey);

        var first = await PaymentQrMigration.MoveLegacyTemplatePromptPayAsync(db);
        Assert.True(first.Moved >= 1 && first.Skipped >= 2, $"moved={first.Moved} skipped={first.Skipped}");
        Assert.Empty(db.ChangeTracker.Entries());   // context ตอนบูตใช้ต่อ — ไม่มีแถวค้าง

        using (var check = DbTestDatabase.TryCreateContext()!)
        {
            // (1) ย้าย — เก็บรูปตัวเลขล้วน (ตัวตรวจเดียวกับตอนตั้งที่หน้าบัญชีธนาคาร) · ผู้แก้ = migration
            var moved = await check.Set<BankAccount>().AsNoTracking().SingleAsync(b => b.CompanyId == clear);
            Assert.Equal("0812345678", moved.PromptPayId);
            Assert.Equal(PaymentQrMigration.Actor, moved.UpdatedBy);
            var a1 = Assert.Single(await MigrationAuditAsync(check, clear));
            Assert.Equal("PaymentQrLegacyTemplatePromptPayMoved", a1.GetProperty("event").GetString());
            Assert.Equal("0812345678", a1.GetProperty("promptPayId").GetString());

            // (2) สองบัญชี — ไม่เดา · เหตุอยู่ใน audit
            Assert.All(await check.Set<BankAccount>().AsNoTracking().Where(b => b.CompanyId == two).ToListAsync(), b => Assert.Null(b.PromptPayId));
            var a2 = Assert.Single(await MigrationAuditAsync(check, two));
            Assert.Equal("PaymentQrLegacyTemplatePromptPaySkipped", a2.GetProperty("event").GetString());
            Assert.Contains("2 บัญชี", a2.GetProperty("reason").GetString());

            // (3) บัญชีมีเลขแล้ว — ไม่ทับ
            Assert.Equal("0899999999", (await check.Set<BankAccount>().AsNoTracking().SingleAsync(b => b.CompanyId == has)).PromptPayId);
            var a3 = Assert.Single(await MigrationAuditAsync(check, has));
            Assert.Equal("PaymentQrLegacyTemplatePromptPaySkipped", a3.GetProperty("event").GetString());
            Assert.Contains("มีเลขพร้อมเพย์อยู่แล้ว", a3.GetProperty("reason").GetString());
            Assert.Equal(1, await MarkerCountAsync(check));
        }

        // (4) บูตซ้ำ: marker มีแล้ว ⇒ (0, 0) · บริษัทใหม่ที่เข้าเงื่อนไขก็ไม่ถูกแตะ (งานครั้งเดียว ไม่ใช่งานประจำ)
        using var db2 = DbTestDatabase.TryCreateContext()!;
        var late = await CompanyAsync(db2, "มาทีหลัง " + tag, "0812345678", (string?)null);
        db2.ChangeTracker.Clear();
        Assert.Equal(new PaymentQrMigration.LegacyMoveResult(0, 0), await PaymentQrMigration.MoveLegacyTemplatePromptPayAsync(db2));
        using var after = DbTestDatabase.TryCreateContext()!;
        Assert.Null((await after.Set<BankAccount>().AsNoTracking().SingleAsync(b => b.CompanyId == late)).PromptPayId);
        Assert.Empty(await MigrationAuditAsync(after, late));
        Assert.Equal("0812345678", (await after.Set<BankAccount>().AsNoTracking().SingleAsync(b => b.CompanyId == clear)).PromptPayId);
        Assert.Single(await MigrationAuditAsync(after, clear));   // ไม่มี audit ซ้ำ
    }
}
