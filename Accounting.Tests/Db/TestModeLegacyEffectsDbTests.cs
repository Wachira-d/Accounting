using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Payments;
using Xunit;
using Xunit.Abstractions;
using EffectKind = Accounting.Helpers.TestModeLegacyEffectPolicy.EffectKind;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 235 (คำตัดสิน 235-3) — ผู้โหลด "เงินโหมดทดสอบที่ลงบัญชีไปแล้ว" บน PostgreSQL จริง:
/// intent ทดสอบ (ประทับ true · และไม่ประทับแต่ config เป็น Test) ที่มีการรับชำระผูกอยู่ ⇒ ขึ้นรายงาน + กลับรายการได้ ·
/// ทิศตรงข้าม: เงินจริงที่มีการรับชำระ · intent ทดสอบหลังรอบ 232 ที่ไม่มีผล · ร้านอื่น ⇒ ไม่ขึ้น · ไม่แก้ข้อมูลใด ๆ
/// </summary>
[Trait("Category", "Db")]
public class TestModeLegacyEffectsDbTests
{
    private readonly ITestOutputHelper _out;
    public TestModeLegacyEffectsDbTests(ITestOutputHelper output) => _out = output;

    private static async Task<(Guid CompanyId, Guid ContactId)> CompanyAsync(Accounting.Data.AccountingDbContext db, string name)
    {
        var co = new Company { Name = name, TaxId = "0105556000025", IsVatRegistered = true };
        db.Companies.Add(co);
        await db.SaveChangesAsync();
        var c = new Contact { CompanyId = co.Id, Name = "ลูกค้า", IsCustomer = true, TaxId = "0105556000017", BranchCode = "00000", Address = "1 ถนนทดสอบ กรุงเทพมหานคร 10110" };
        db.Contacts.Add(c);
        await db.SaveChangesAsync();
        return (co.Id, c.Id);
    }

    private static Document Invoice(Guid companyId, Guid contactId) => new()
    {
        CompanyId = companyId, ContactId = contactId, DocumentType = DocumentType.TaxInvoice, Status = DocumentStatus.Paid,
        DocumentNumber = "TIV-T235-" + Guid.NewGuid().ToString("N")[..8], DocumentDate = DateTime.UtcNow.Date, SubTotal = 100m, VatAmount = 7m,
        TotalAmount = 107m, BalanceDue = 0m, PaidAmount = 107m, Currency = "THB", ExchangeRate = 1m,
        Lines = new List<DocumentLine> { new() { Description = "สินค้า", Quantity = 1m, UnitPrice = 100m, Amount = 100m, VatRate = 7m, VatAmount = 7m } },
    };

    private static PaymentIntent Intent(Guid companyId, Guid docId, bool? isTest, Guid? configId = null, string? providerRef = null) => new()
    {
        CompanyId = companyId, SourceKind = PaymentSourceKind.Document, SourceId = docId, ProviderCode = "omise", Amount = 107m,
        Status = PaymentIntentStatus.Succeeded, IsTestMode = isTest, ProviderConfigId = configId, ProviderRef = providerRef,
        ConfirmedAt = TestModeLegacyEffectPolicy.GuardSinceUtc.AddDays(-30), CreatedAt = TestModeLegacyEffectPolicy.GuardSinceUtc.AddDays(-30),   // ก่อนรอบ 232 เสมอ (ไม่ขึ้นกับวันที่รัน CI)
        IdempotencyKey = "t235-" + Guid.NewGuid().ToString("N"),
    };

    private static Payment PaymentFor(Guid companyId, Document doc, PaymentIntent intent) => new()
    {
        CompanyId = companyId, PaymentNumber = "RV-T235-" + Guid.NewGuid().ToString("N")[..8], DocumentId = doc.Id,
        PaymentDate = DateTime.UtcNow.Date, Amount = 107m, PaymentMethod = PaymentMethod.BankTransfer,
        PaymentIntentId = intent.Id, Reference = Accounting.Helpers.GatewayPaymentRecord.Reference(intent.Id),
    };

    [Fact]
    public async Task ทดสอบที่ลงบัญชีแล้วขึ้นรายงาน_ปุ่มเฉพาะที่มีหลักฐาน_เงินจริงและร้านอื่นไม่ขึ้น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, contact) = await CompanyAsync(db, "ร้าน T235 เงินทดสอบ");
        var (otherCid, otherContact) = await CompanyAsync(db, "ร้านอื่น T235");

        // ช่องทาง "ตอนนี้" เป็น Test — แถว null ที่รับเงินตอน Live แล้วสลับกลับเป็น Test ภายหลัง ดูเหมือนทดสอบ (B1)
        var nowTestCfg = new PaymentProviderConfig { CompanyId = cid, ProviderCode = "omise", Mode = PaymentProviderMode.Test };
        var liveCfg = new PaymentProviderConfig { CompanyId = cid, ProviderCode = "omise", Mode = PaymentProviderMode.Live };
        db.PaymentProviderConfigs.AddRange(nowTestCfg, liveCfg);
        await db.SaveChangesAsync();

        // (1) ประทับ true + มีการรับชำระ ⇒ ขึ้น + มีปุ่ม
        var d1 = Invoice(cid, contact); db.Documents.Add(d1);
        var i1 = Intent(cid, d1.Id, true); db.PaymentIntents.Add(i1);
        // (2) null + ช่องทางตอนนี้เป็น Test (เดิม Live) ⇒ ขึ้น แต่ไม่มีปุ่ม (พิสูจน์ไม่ได้)
        var d2 = Invoice(cid, contact); db.Documents.Add(d2);
        var i2 = Intent(cid, d2.Id, null, nowTestCfg.Id, "chrg_5live000"); db.PaymentIntents.Add(i2);
        // (3) null + เลขอ้างอิง chrg_test_ (ช่องทางตอนนี้ Live) ⇒ ขึ้น + มีปุ่ม
        var d3 = Invoice(cid, contact); db.Documents.Add(d3);
        var i3 = Intent(cid, d3.Id, null, liveCfg.Id, "chrg_test_5abc"); db.PaymentIntents.Add(i3);
        // (4) เงินจริง (ประทับ false) + มีการรับชำระ ⇒ ไม่ขึ้น
        var d4 = Invoice(cid, contact); db.Documents.Add(d4);
        var i4 = Intent(cid, d4.Id, false, null, "chrg_test_5zzz"); db.PaymentIntents.Add(i4);
        // (5) ทดสอบหลังรอบ 232 — ไม่มีผล ⇒ ไม่ขึ้น
        var d5 = Invoice(cid, contact); db.Documents.Add(d5);
        var i5 = Intent(cid, d5.Id, true); db.PaymentIntents.Add(i5);
        // (6) ร้านอื่น ทดสอบ + มีผล ⇒ ไม่ขึ้นในรายงานของร้านนี้
        var d6 = Invoice(otherCid, otherContact); db.Documents.Add(d6);
        var i6 = Intent(otherCid, d6.Id, true); db.PaymentIntents.Add(i6);
        await db.SaveChangesAsync();
        db.Payments.AddRange(PaymentFor(cid, d1, i1), PaymentFor(cid, d2, i2), PaymentFor(cid, d3, i3), PaymentFor(cid, d4, i4),
            PaymentFor(otherCid, d6, i6));
        await db.SaveChangesAsync();

        using var read = DbTestDatabase.TryCreateContext()!;
        var report = await TestModeLegacyEffects.ForCompanyAsync(read, cid, CancellationToken.None);
        var rows = report.Rows;
        var ids = rows.Select(r => r.IntentId).ToHashSet();
        Assert.Contains(i1.Id, ids);
        Assert.Contains(i2.Id, ids);
        Assert.Contains(i3.Id, ids);
        Assert.DoesNotContain(i4.Id, ids);
        Assert.DoesNotContain(i5.Id, ids);
        Assert.DoesNotContain(i6.Id, ids);
        Assert.All(rows, r => Assert.Equal(cid, r.CompanyId));
        Assert.False(report.Truncated);

        var e1 = Assert.Single(rows.Single(r => r.IntentId == i1.Id).Effects);
        Assert.Equal(EffectKind.DocumentPayment, e1.Kind);
        Assert.True(e1.CanReverseHere);
        Assert.NotNull(e1.PaymentId);
        Assert.Equal(d1.Id, e1.DocumentId);

        var r2 = rows.Single(r => r.IntentId == i2.Id);
        Assert.Equal(TestModeLegacyEffectPolicy.TestEvidence.ConfigOnly, r2.Evidence);
        var e2 = Assert.Single(r2.Effects);
        Assert.False(e2.CanReverseHere);   // B1: ห้ามมีปุ่มกับเงินที่อาจเป็นเงินจริง
        Assert.Null(e2.PaymentId);
        Assert.Equal(TestModeLegacyEffectPolicy.UnprovenMessage, e2.Hint);

        var r3 = rows.Single(r => r.IntentId == i3.Id);
        Assert.Equal(TestModeLegacyEffectPolicy.TestEvidence.ProviderTestRef, r3.Evidence);
        Assert.True(Assert.Single(r3.Effects).CanReverseHere);

        // ตัดสินซ้ำฝั่งเซิร์ฟเวอร์ใช้เส้นเดียวกัน
        Assert.Equal(TestModeLegacyEffectPolicy.TestEvidence.ConfigOnly,
            (await TestModeLegacyEffects.EvaluateAsync(read, cid, i2.Id, CancellationToken.None))!.Evidence);
        Assert.Null(await TestModeLegacyEffects.EvaluateAsync(read, cid, i4.Id, CancellationToken.None));
        Assert.Null(await TestModeLegacyEffects.EvaluateAsync(read, otherCid, i1.Id, CancellationToken.None));   // tenant

        // แอดมิน: ร้านอื่นถูกนับ (อ่านอย่างเดียว)
        var (companies, intents, proven, _) = await TestModeLegacyEffects.CountAllAsync(read, CancellationToken.None);
        Assert.True(companies >= 2);
        Assert.True(intents >= 4);
        Assert.True(proven >= 3);
    }
}
