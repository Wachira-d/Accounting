using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// W9 R-58/R-59 — migration ของเสียที่ persist แล้ว (F2 #9) บน PostgreSQL จริง (<c>DatabaseMigrationHelper.W9HygieneStatements</c>)
/// <para>R-58: ใบ LINE เดิม (Reference "LINE-bot" · OriginModule NULL) ถูกประทับ "LineBot" · ทิศตรงข้าม: Reference อื่น / ใบที่มี OriginModule
/// อยู่แล้วไม่ถูกแตะ · รันซ้ำไม่เปลี่ยนอะไร</para>
/// <para>R-59: แถว DocumentTypeClassification ก่อน cutoff ที่มีแค่คำตอบครู ⇒ ExcludedFromTraining=true + เหตุผล · AiPrimaryAnswer คงเดิม (ไม่ลบ ไม่แก้) ·
/// ทิศตรงข้าม: แถวหลัง cutoff · แถวที่ผู้ใช้เลือกแล้ว · feature อื่น ไม่ถูกแตะ · SQL ตัดสินตรงกับ <see cref="AiFeedbackTrainingExclusion.IsLegacyDocTypeTeacherOnlyRow"/></para>
/// </summary>
[Trait("Category", "Db")]
public class W9HygieneBackfillDbTests
{
    private readonly ITestOutputHelper _out;
    public W9HygieneBackfillDbTests(ITestOutputHelper output) => _out = output;

    private static async Task MigrateAsync()
    {
        using var mig = DbTestDatabase.TryCreateContext()!;
        foreach (var sql in DatabaseMigrationHelper.W9HygieneStatements())
            await mig.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task<(Guid CompanyId, Guid ContactId)> SeedCompanyAsync(AccountingDbContext db, string name)
    {
        var company = new Company { Name = name, TaxId = "0105556000009" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var contact = new Contact { CompanyId = company.Id, Name = "ร้านทดสอบ LINE", IsSupplier = true, IsCustomer = false };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();
        return (company.Id, contact.Id);
    }

    private static Document Doc(Guid cid, Guid contactId, string no, string? reference, string? origin) => new()
    {
        CompanyId = cid, ContactId = contactId, DocumentType = DocumentType.Expense, DocumentNumber = no, Status = DocumentStatus.Draft,
        DocumentDate = DateTime.UtcNow.Date, SubTotal = 250m, TotalAmount = 250m, BalanceDue = 250m, Currency = "THB",
        Reference = reference, OriginModule = origin, CreatedBy = "line-user@test.local",
    };

    [Fact]
    public async Task ใบ_LINE_เดิม_ถูก_backfill_เป็น_LineBot_ใบอื่นไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var (cid, contactId) = await SeedCompanyAsync(db, "W9 backfill LINE");
        db.Documents.Add(Doc(cid, contactId, "DRAFT-line-1", DocumentCreationChannelRule.LineBotReference, null));
        db.Documents.Add(Doc(cid, contactId, "DRAFT-line-2", DocumentCreationChannelRule.LineBotReference, null));
        db.Documents.Add(Doc(cid, contactId, "DRAFT-other", "PO-0001", null));                 // Reference อื่น
        db.Documents.Add(Doc(cid, contactId, "DRAFT-lodging", DocumentCreationChannelRule.LineBotReference, "Lodging"));   // มี OriginModule แล้ว
        db.Documents.Add(Doc(cid, contactId, "DRAFT-none", null, null));
        await db.SaveChangesAsync();

        await MigrateAsync();
        await MigrateAsync();   // idempotent

        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.Documents.AsNoTracking().Where(d => d.CompanyId == cid)
            .Select(d => new { d.DocumentNumber, d.OriginModule }).ToDictionaryAsync(x => x.DocumentNumber, x => x.OriginModule);
        Assert.Equal(DocumentCreationChannelRule.LineBotOrigin, rows["DRAFT-line-1"]);
        Assert.Equal(DocumentCreationChannelRule.LineBotOrigin, rows["DRAFT-line-2"]);
        Assert.Null(rows["DRAFT-other"]);
        Assert.Equal("Lodging", rows["DRAFT-lodging"]);
        Assert.Null(rows["DRAFT-none"]);
        // ผู้อ่าน (รายงานตรวจสอบการใช้งาน) เห็นเป็นช่องทาง LINE
        Assert.Equal(DocumentCreationChannel.LineBot,
            DocumentCreationChannelRule.Classify(new DocumentOriginProbe(rows["DRAFT-line-1"], DocumentCreationChannelRule.MarkerEmail)));
    }

    private static AiSuggestionFeedback Fb(Guid cid, DateTime createdAt, string feature = nameof(AiFeatureKey.DocumentTypeClassification),
        DateTime? chosenAt = null, string? chosen = null) => new()
    {
        CompanyId = cid, FeatureKey = feature, PromptHash = Guid.NewGuid().ToString("N"), PromptJson = "{\"t\":\"x\"}",
        AiPrimaryAnswer = "TaxInvoice", AiConfidence = 0.9m, Status = AiCallStatus.Success, ProviderUsed = AiProviderType.DeepSeek,
        UserChosenAt = chosenAt, UserChosenAnswer = chosen, CreatedAt = createdAt,
    };

    [Fact]
    public async Task feedback_DocType_ความหมายเก่า_ติด_Excluded_ไม่ลบ_แถวอื่นไม่ถูกแตะ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cutoff = AiFeedbackTrainingExclusion.DocTypePromptV2CutoffUtc;
        var company = new Company { Name = "W9 feedback exclusion", TaxId = "0105556000010" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cid = company.Id;

        var legacyTeacherOnly = Fb(cid, cutoff.AddDays(-3));
        var legacyUserChosen = Fb(cid, cutoff.AddDays(-3), chosenAt: cutoff.AddDays(-2), chosen: "PaymentVoucher");
        var afterCutoff = Fb(cid, cutoff.AddMinutes(5));
        var otherFeature = Fb(cid, cutoff.AddDays(-3), feature: nameof(AiFeatureKey.DocumentRoleInference));
        db.AiSuggestionFeedbacks.AddRange(legacyTeacherOnly, legacyUserChosen, afterCutoff, otherFeature);
        await db.SaveChangesAsync();

        await MigrateAsync();
        await MigrateAsync();   // idempotent

        using var check = DbTestDatabase.TryCreateContext()!;
        var rows = await check.AiSuggestionFeedbacks.AsNoTracking().Where(f => f.CompanyId == cid).ToDictionaryAsync(f => f.Id);
        Assert.Equal(4, rows.Count);   // ไม่ลบ

        var ex = rows[legacyTeacherOnly.Id];
        Assert.True(ex.ExcludedFromTraining);
        Assert.Equal(AiFeedbackTrainingExclusion.ReasonDocTypePromptV1, ex.ExcludedFromTrainingReason);
        Assert.Equal("TaxInvoice", ex.AiPrimaryAnswer);   // คำตอบครูคงเดิม (audit/บิล)

        // ทิศตรงข้าม
        Assert.False(rows[legacyUserChosen.Id].ExcludedFromTraining);
        Assert.Equal("PaymentVoucher", rows[legacyUserChosen.Id].UserChosenAnswer);
        Assert.False(rows[afterCutoff.Id].ExcludedFromTraining);
        Assert.False(rows[otherFeature.Id].ExcludedFromTraining);

        // SQL กับตัวตัดสิน pure ตัดสินเหมือนกันทุกแถว
        foreach (var r in rows.Values)
            Assert.Equal(AiFeedbackTrainingExclusion.IsLegacyDocTypeTeacherOnlyRow(r.FeatureKey, r.CreatedAt, r.UserChosenAt), r.ExcludedFromTraining);
    }
}
