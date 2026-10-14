using Accounting.Data;
using Accounting.Helpers;
using Accounting.Models.DTOs.Import;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Distillation;
using Accounting.Services.Ai.Prompts;
using Accounting.Services.Import;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Accounting.Tests.Db;

/// <summary>
/// รอบ 205 ทีม A — คลังแม่แบบจับคู่คอลัมน์ (<see cref="ImportMappingTemplateStore"/>) บน PostgreSQL จริง + ลูปเรียนรู้ของกฎเหล็ก #1:
/// ยืนยันการจับคู่ ⇒ <c>RecordUserChoiceAsync</c> ⇒ <c>GetLearnedHeadersAsync</c> ⇒ นักเรียน <c>LoadFromFeedbackAsync</c> ⇒ ตอบเองครั้งถัดไป
/// (round-trip ครบวง · control ที่ไม่มีเทสต์ write→read = ไม่มี control · กฎเหล็ก #4 G) · ทุกเทสต์มีทิศตรงข้าม: บริษัทอื่นมองไม่เห็น/แก้ไม่ได้
/// </summary>
[Trait("Category", "Db")]
public class ImportMappingTemplateStoreDbTests
{
    private readonly ITestOutputHelper _out;
    public ImportMappingTemplateStoreDbTests(ITestOutputHelper output) => _out = output;

    private static readonly ImportActor Actor = new(Guid.NewGuid(), "ผู้ทดสอบนำเข้า");

    private static async Task<Guid> CompanyAsync(AccountingDbContext db, string name)
    {
        var c = new Company { Name = name, TaxId = "0105556000001" };
        db.Companies.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    private static ImportMappingTemplateStore Store(AccountingDbContext db)
        => new(db, new AiFeedbackRecorder(db, NullLogger<AiFeedbackRecorder>.Instance));

    private static ImportColumnPlanDto Final(int idx, string header, string? field)
        => new(idx, header, field, null, ImportMapSource.User, "✋ ผู้ใช้เลือก", 1m, false, Array.Empty<string>(), Array.Empty<ImportColumnCandidateDto>());

    [Fact]
    public async Task จำแม่แบบ_หาเจอแม้สลับคอลัมน์_upsert_ไม่ซ้ำ_และบริษัทอื่นมองไม่เห็น()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม · หลักฐานอยู่ที่ job db-test"); return; }
        var cid = await CompanyAsync(db, "บริษัท นำเข้าแม่แบบ จำกัด");
        var other = await CompanyAsync(db, "บริษัท อื่น จำกัด");
        var store = Store(db);

        var headers = new[] { "รหัสลูกค้า", "ชื่อลูกค้า", "เลขประจำตัวผู้เสียภาษี" };
        var cols = new[]
        {
            new ImportTemplateColumnDto("รหัสลูกค้า", null), new ImportTemplateColumnDto("ชื่อลูกค้า", "Name"),
            new ImportTemplateColumnDto("เลขประจำตัวผู้เสียภาษี", "taxid",
                new ImportColumnTransformDto(ValueMap: new Dictionary<string, string> { ["-"] = "" })),
        };
        var first = await store.SaveColumnsAsync(cid, "Contacts", headers, cols, "express", null, false, Actor);
        Assert.Equal("contacts", first.EntityType);
        Assert.Equal("ผู้ติดต่อ — Express", first.Name);
        Assert.Equal("TaxId", first.Columns.Single(c => c.Header == ImportHeaderSignature.Normalize("เลขประจำตัวผู้เสียภาษี")).TargetField);
        Assert.Equal(1, first.UseCount);
        Assert.False(first.AutoCommitWhenClean);                         // opt-in เท่านั้น (คำตัดสินข้อ 151)

        var sigReordered = ImportHeaderSignature.Compute(new[] { "เลขประจำตัวผู้เสียภาษี", "ชื่อ ลูกค้า", "รหัสลูกค้า" });
        var found = await store.FindAsync(cid, "contacts", sigReordered);
        Assert.Equal(first.Id, found?.Id);
        Assert.Null(await store.FindAsync(other, "contacts", sigReordered));
        Assert.Null(await store.GetAsync(other, first.Id));
        Assert.False(await store.DeleteAsync(other, first.Id, Actor));
        Assert.Null(await store.UpdateAsync(other, first.Id, new ImportTemplateUpsertRequest("แฮ็ก", true), Actor));

        // บันทึกซ้ำ (ไม่ส่ง transform) = แถวเดิม · ใช้ +1 · ยืนยันแล้วติด · transform เดิมของผู้ใช้ไม่หาย
        var again = await store.SaveColumnsAsync(cid, "contacts", Enumerable.Reverse(headers).ToList(),
            cols.Select(c => c with { Transform = null }).ToList(), null, null, true, Actor);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(2, again.UseCount);
        Assert.True(again.ConfirmedByUser);
        Assert.NotNull(again.Columns.Single(c => c.TargetField == "TaxId").Transform);
        Assert.Equal(1, await db.ImportMappingTemplates.CountAsync(t => t.CompanyId == cid && !t.IsDeleted));

        // field ที่ไม่มีใน schema = ล้มดังพร้อมทางไปต่อ (ไม่บันทึกแผนที่เสีย)
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => store.SaveColumnsAsync(cid, "contacts", headers,
            new[] { new ImportTemplateColumnDto("ชื่อลูกค้า", "Salary") }, null, null, false, Actor));
        Assert.Contains("ไม่ใช้คอลัมน์นี้", ex.Message);
    }

    [Fact]
    public async Task แก้แม่แบบ_เปิดลงอัตโนมัติ_แก้ช่อง_หัวนอกแม่แบบถูกปฏิเสธ_ลบแล้วหาไม่เจอ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await CompanyAsync(db, "บริษัท แก้แม่แบบ จำกัด");
        var store = Store(db);
        var headers = new[] { "รหัสสินค้า", "ชื่อสินค้า", "ราคา" };
        var t = await store.SaveColumnsAsync(cid, "products", headers, new[]
        {
            new ImportTemplateColumnDto("รหัสสินค้า", "Code"), new ImportTemplateColumnDto("ชื่อสินค้า", "Name"),
            new ImportTemplateColumnDto("ราคา", "SellingPrice"),
        }, null, "สินค้าจากร้านเดิม", false, Actor);

        var upd = await store.UpdateAsync(cid, t.Id, new ImportTemplateUpsertRequest(null, true,
            new[] { new ImportTemplateColumnDto("ราคา", "CostPrice") }), Actor);
        Assert.NotNull(upd);
        Assert.True(upd!.AutoCommitWhenClean);
        Assert.True(upd.ConfirmedByUser);
        Assert.Equal("CostPrice", upd.Columns.Single(c => c.Header == "ราคา").TargetField);
        Assert.Equal(3, upd.Columns.Count);                             // ส่งคอลัมน์เดียว — อีกสองคอลัมน์ต้องไม่หาย
        Assert.Equal("สินค้าจากร้านเดิม", upd.Name);

        await Assert.ThrowsAsync<BusinessRuleException>(() => store.UpdateAsync(cid, t.Id,
            new ImportTemplateUpsertRequest(null, false, new[] { new ImportTemplateColumnDto("หัวใหม่", "Unit") }), Actor));

        Assert.Single(await store.ListAsync(cid, "PRODUCTS"));
        Assert.True(await store.DeleteAsync(cid, t.Id, Actor));
        Assert.Null(await store.FindAsync(cid, "products", ImportHeaderSignature.Compute(headers)));
        Assert.Empty(await store.ListAsync(cid, null));
    }

    [Fact]
    public async Task ยืนยันการจับคู่_ปิดลูป_feedback_แล้วนักเรียนเรียนรู้_ตอบเองครั้งถัดไป()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await CompanyAsync(db, "บริษัท เรียนรู้คอลัมน์ จำกัด");
        var other = await CompanyAsync(db, "บริษัท ไม่เกี่ยว จำกัด");
        var store = Store(db);
        var targets = ImportPrompts.ToTargetFields(ImportSchemas.For("contacts")!);

        // คำถามที่เคยถามครู/นักเรียน (คอลัมน์ 0,1) — นักเรียนตอบไม่ได้ทั้งคู่
        var prompt = ImportPrompts.BuildColumnMatch(cid, null, "contacts", targets, new[]
        {
            new ImportPrompts.ColumnMatchInput(0, "ผู้ประสานงานฝั่งลูกค้า", Array.Empty<string>(), null, 0),
            new ImportPrompts.ColumnMatchInput(1, "ช่องทางติดต่อ อิเล็กทรอนิกส์", Array.Empty<string>(), null, 0),
        }).UserPromptJson;
        var fb = new AiSuggestionFeedback
        {
            CompanyId = cid, FeatureKey = nameof(AiFeatureKey.ImportColumnMatch), PromptHash = "t", PromptJson = prompt,
            LocalModelAnswer = ImportColumnMapper.CanonicalAnswer(new (int, string?)[] { (0, null), (1, null) }),
            Status = AiCallStatus.NoProvider, ProviderUsed = AiProviderType.None,
        };
        db.AiSuggestionFeedbacks.Add(fb);
        await db.SaveChangesAsync();

        var final = new[]
        {
            Final(0, "ผู้ประสานงานฝั่งลูกค้า", "ContactPerson"), Final(1, "ช่องทางติดต่อ อิเล็กทรอนิกส์", "Email"),
            Final(5, "ชื่อลูกค้า", "Name"),                                // ไม่ได้ถามในรอบนั้น ⇒ ไม่อยู่ในคำตอบ
        };
        Assert.False(await store.RecordColumnMatchChoiceAsync(other, fb.Id, final));   // บริษัทอื่นปิดลูปแทนไม่ได้
        Assert.True(await store.RecordColumnMatchChoiceAsync(cid, fb.Id, final));

        db.ChangeTracker.Clear();
        var row = await db.AiSuggestionFeedbacks.AsNoTracking().SingleAsync(f => f.Id == fb.Id && f.CompanyId == cid);
        Assert.Equal(ImportColumnMapper.CanonicalAnswer(new (int, string?)[] { (0, "ContactPerson"), (1, "Email") }), row.UserChosenAnswer);
        Assert.False(row.UserAcceptedAi);                              // ระบบเสนอ "ไม่รู้" ทั้งคู่ — ผู้ใช้แก้เอง
        Assert.Equal(UserChoiceSource.Explicit, row.UserChoiceOrigin);

        var learned = await store.GetLearnedHeadersAsync(cid, "contacts");
        Assert.Contains(learned, l => l.Header == ImportHeaderSignature.Normalize("ช่องทางติดต่อ อิเล็กทรอนิกส์") && l.TargetField == "Email");
        Assert.Empty(await store.GetLearnedHeadersAsync(other, null));

        // นักเรียน (singleton) โหลดจากที่มาเดียวกัน แล้วตอบคำถามใหม่ที่มีหัวนี้เอง — ไม่ต้องพึ่ง AI
        var services = new ServiceCollection();
        services.AddScoped(_ => DbTestDatabase.TryCreateContext()!);
        services.AddScoped<IAiFeedbackRecorder>(sp => new AiFeedbackRecorder(sp.GetRequiredService<AccountingDbContext>(),
            NullLogger<AiFeedbackRecorder>.Instance));
        services.AddScoped<IImportMappingTemplateStore, ImportMappingTemplateStore>();
        using var sp = services.BuildServiceProvider();
        var student = new ImportColumnMatchDistillationModel(sp, NullLogger<ImportColumnMatchDistillationModel>.Instance);
        var ask = ImportPrompts.BuildColumnMatch(cid, null, "contacts", targets, new[]
        {
            new ImportPrompts.ColumnMatchInput(3, "ช่องทางติดต่อ อิเล็กทรอนิกส์", Array.Empty<string>(), null, 0),
        }).UserPromptJson;
        Assert.Null(await student.PredictAsync(cid, ask, default));    // ก่อนเรียน: ไม่รู้ = ไม่ตอบ (ไม่แต่ง)
        await student.LoadFromFeedbackAsync(cid, default);
        var pred = await student.PredictAsync(cid, ask, default);
        Assert.NotNull(pred);
        Assert.Equal(ImportColumnMapper.CanonicalAnswer(new (int, string?)[] { (3, "Email") }), pred!.PrimaryAnswer);
        Assert.True(pred.Confidence >= 0.85m);
        Assert.Null(await student.PredictAsync(other, ask, default));  // ความรู้ของบริษัทหนึ่งไม่รั่วไปอีกบริษัท
    }

    [Fact]
    public async Task นักเรียนตรวจข้อมูล_เรียนจาก_feedback_จริง_เลิกเสนอเฉพาะบริษัทที่ปฏิเสธ()
    {
        using var db = DbTestDatabase.TryCreateContext();
        if (db == null) { _out.WriteLine("ไม่มีฐาน PostgreSQL — ข้าม"); return; }
        var cid = await CompanyAsync(db, "บริษัท ไม่ชอบเลขไทย จำกัด");
        var other = await CompanyAsync(db, "บริษัท ปกติ จำกัด");
        var targets = ImportPrompts.ToTargetFields(ImportSchemas.For("contacts")!);
        var review = ImportPrompts.BuildDataReview(cid, Guid.NewGuid(), "contacts", targets, new[] { "Name", "Address" },
            new[] { new Dictionary<string, string?> { ["Name"] = "ร้านค้า ๙๙", ["Address"] = "เลขที่ ๑๒ ถ.ราชดำเนิน" } },
            Array.Empty<ImportPrompts.ExistingEntityRef>()).UserPromptJson;
        var chosen = ImportDataReviewDistillationModel.ChoiceJson(Array.Empty<ImportReviewChoice>(),
            new[] { new ImportReviewChoice("Name", "thai-digits") });
        for (var i = 0; i < 3; i++)
            db.AiSuggestionFeedbacks.Add(new AiSuggestionFeedback
            {
                CompanyId = cid, FeatureKey = nameof(AiFeatureKey.ImportDataReview), PromptHash = "t" + i, PromptJson = review,
                UserChosenAnswer = chosen, UserChosenAt = DateTime.UtcNow, UserAcceptedAi = false,
                Status = AiCallStatus.NoProvider, ProviderUsed = AiProviderType.None,
            });
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => DbTestDatabase.TryCreateContext()!);
        using var sp = services.BuildServiceProvider();
        var student = new ImportDataReviewDistillationModel(sp, NullLogger<ImportDataReviewDistillationModel>.Instance);
        await student.LoadFromFeedbackAsync(cid, default);

        static string[] Norms(LocalPrediction? p)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(p!.StructuredJson!);
            return doc.RootElement.GetProperty("normalizations").EnumerateArray().Select(n => n.GetProperty("field").GetString()!).ToArray();
        }
        Assert.Equal(new[] { "Address" }, Norms(await student.PredictAsync(cid, review, default)));        // Name เลิกเสนอ · Address ยังเสนอ
        Assert.Equal(new[] { "Name", "Address" }, Norms(await student.PredictAsync(other, review, default)));
    }
}
