using Accounting.Helpers;
using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Distillation;
using Accounting.Services.Ai.Prompts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// ตัวอ่านคำตอบรูป canonical ของ AI (<see cref="AiFeatureResponseParser"/>) — ทีม H 2026-10-22
///
/// <para>ที่มา: หน้าแอดมิน 20/10/2569 ขึ้น InvalidResponse "Schema mismatch" ที่ DocumentTypeClassification ·
/// WhtCategoryInference · ApprovalWarningFixSuggestion. raw content ในเทสต์นี้เขียนตามรูปที่ DeepSeek คืนจริงกับ prompt
/// ปัจจุบัน (ภาษาไทยในเหตุผล · ตัดที่ max_tokens · code fence · ร้อยแก้วนำหน้า) — ทั้งที่ควรอ่านได้และที่ควรเป็น InvalidResponse จริง</para>
/// </summary>
public class AiFeatureResponseParserTests
{
    // ── ควรอ่านได้ ─────────────────────────────────────────────────────

    [Fact]
    public void DocType_JSON_ครบ_อ่านชนิดปลายทาง()
    {
        const string raw = """
            {"primary":"PaymentVoucher","scanned_type":"Receipt","confidence":0.82,"alternatives":["PurchaseInvoice"],
             "risks":[],"compliance_flags":[],"reasoning":"ใบเสร็จรับเงินจากผู้ขาย เราเป็นผู้ซื้อ = จ่ายแล้ว","suggested_actions":[]}
            """;
        var p = AiFeatureResponseParser.Parse(raw);
        Assert.Equal(AiParseProblem.None, p.Problem);
        Assert.Equal("PaymentVoucher", p.PrimaryAnswer);
        Assert.Equal(0.82m, p.Confidence);
        Assert.Equal(new[] { "PurchaseInvoice" }, p.Alternatives);
        Assert.False(p.Salvaged);
    }

    [Fact]
    public void DocType_ขาดกลางเหตุผลภาษาไทย_กู้_primary_กับ_confidence_ที่ปิดครบแล้ว()
    {
        // รูปจริงของ max_tokens=200 กับ reasoning ภาษาไทย — ขาดกลางสตริง
        const string raw = "{\"primary\":\"PaymentVoucher\",\"confidence\":0.78,\"alternatives\":[\"Expense\"],\"risks\":[],"
            + "\"compliance_flags\":[\"ใบเสร็จไม่มีเลขผู้เสียภาษีผู้ขาย อาจใช้เป็นหลักฐานรายจ่ายไม่ได้ตาม";
        var p = AiFeatureResponseParser.Parse(raw);
        Assert.Equal(AiParseProblem.Truncated, p.Problem);
        Assert.True(p.HasAnswer);
        Assert.True(p.Salvaged);
        Assert.Equal("PaymentVoucher", p.PrimaryAnswer);
        Assert.Equal(0.78m, p.Confidence);
        Assert.Equal(new[] { "Expense" }, p.Alternatives);
    }

    [Fact]
    public void ขาดกลางทาง_แบบมี_object_ซ้อนปิดแล้ว_ยังกู้ได้()
    {
        const string raw = "{\"primary\":\"Edit\",\"confidence\":0.7,\"meta\":{\"a\":1},\"reasoning\":\"ผู้ขายไม่มีเลขผู้เสียภาษี ต้อง";
        var p = AiFeatureResponseParser.Parse(raw);
        Assert.Equal("Edit", p.PrimaryAnswer);
        Assert.True(p.Salvaged);
    }

    [Fact]
    public void WHT_primary_null_คือไม่ต้องหัก_เมื่อ_feature_ประกาศความหมาย()
    {
        const string raw = """{"primary":null,"confidence":0.88,"alternatives":[],"reasoning":"ซื้อสินค้าล้วน ไม่ต้องหัก ณ ที่จ่าย"}""";
        var withMeaning = AiFeatureResponseParser.Parse(raw, nullPrimaryMeans: "None");
        Assert.Equal("None", withMeaning.PrimaryAnswer);
        Assert.Equal(AiParseProblem.None, withMeaning.Problem);
        Assert.Equal(0.88m, withMeaning.Confidence);

        // ทิศตรงข้าม: feature อื่น (ไม่ประกาศ) — null คือ "ไม่ตอบ" ห้ามแต่งคำตอบ
        var without = AiFeatureResponseParser.Parse(raw);
        Assert.Null(without.PrimaryAnswer);
        Assert.False(without.HasAnswer);
        Assert.Equal(AiParseProblem.NullPrimary, without.Problem);
        Assert.StartsWith("Null primary", AiFeatureResponseParser.Describe(without, "stop", 40, 600));
    }

    [Fact]
    public void WHT_ในโค้ดเฟนซ์_markdown()
    {
        const string raw = "```json\n{\"primary\": \"40(8)\", \"confidence\": 0.9, \"suggested_actions\": [\"rate_percent: 3\"]}\n```";
        var p = AiFeatureResponseParser.Parse(raw);
        Assert.Equal("40(8)", p.PrimaryAnswer);
        Assert.Equal(new[] { "rate_percent: 3" }, p.SuggestedActions);
    }

    [Fact]
    public void ApprovalWarning_มีร้อยแก้วนำหน้า_JSON()
    {
        const string raw = "นี่คือคำแนะนำ:\n{\"primary\":\"Edit\",\"confidence\":0.74,\"suggested_actions\":[\"โทรหาผู้ขายขอใบกำกับภาษีฉบับถูกต้อง\"]}\nหวังว่าจะช่วยได้";
        var p = AiFeatureResponseParser.Parse(raw);
        Assert.Equal("Edit", p.PrimaryAnswer);
        Assert.Single(p.SuggestedActions);
    }

    [Fact]
    public void GL_primary_เป็นตัวเลข_และ_object()
    {
        Assert.Equal("54430", AiFeatureResponseParser.Parse("""{"primary":54430,"confidence":0.8}""").PrimaryAnswer);
        var obj = AiFeatureResponseParser.Parse(
            """{"primary":{"code":"54430","name":"ค่าวัสดุสิ้นเปลือง"},"confidence":0.8}""");
        Assert.Equal("54430", obj.PrimaryAnswer);
        Assert.Equal(0.8m, obj.Confidence);   // confidence อยู่ที่ราก — ห้ามหายเพราะไปอ่านใน object ของ primary
        Assert.Equal("54430", AiFeatureResponseParser.Parse("""{"primary":["54430","54420"]}""").PrimaryAnswer);
    }

    [Fact]
    public void ชื่อ_key_แบบอื่น_และห่อหนึ่งชั้น_และราก_array()
    {
        var camel = AiFeatureResponseParser.Parse("""{"primaryAnswer":"Return","Confidence":"85%"}""");
        Assert.Equal("Return", camel.PrimaryAnswer);
        Assert.Equal(0.85m, camel.Confidence);

        var wrapped = AiFeatureResponseParser.Parse("""{"result":{"primary":"Acknowledge","confidence":0.7,"reasoning":"ข้อมูลครบ"}}""");
        Assert.Equal("Acknowledge", wrapped.PrimaryAnswer);
        Assert.Equal("ข้อมูลครบ", wrapped.Reasoning);

        var arr = AiFeatureResponseParser.Parse("""[{"primary":"Discount","confidence":0.6}]""");
        Assert.Equal("Discount", arr.PrimaryAnswer);

        var docType = AiFeatureResponseParser.Parse("""{"document_type":"PurchaseInvoice","confidence":0.75}""");
        Assert.Equal("PurchaseInvoice", docType.PrimaryAnswer);
    }

    [Fact]
    public void VendorCanon___NEW___และ_alternatives_อย่างเดียวถือว่ามีคำตอบ()
    {
        Assert.Equal("__NEW__", AiFeatureResponseParser.Parse("""{"primary":"__NEW__","confidence":0.35}""").PrimaryAnswer);
        var altsOnly = AiFeatureResponseParser.Parse("""{"alternatives":["a1","a2"],"confidence":0.5}""");
        Assert.True(altsOnly.HasAnswer);
        Assert.Equal(AiParseProblem.None, altsOnly.Problem);
    }

    [Fact]
    public void confidence_สตริงตัวเลขไม่มีเปอร์เซ็นต์_ไม่แปลง_ให้ด่าน_0ถึง1_ของผู้เรียกตัดสิน()
    {
        Assert.Equal(90m, AiFeatureResponseParser.Parse("""{"primary":"Buyer","confidence":"90"}""").Confidence);
    }

    // ── ควรเป็น InvalidResponse จริง ───────────────────────────────────

    [Fact]
    public void โครง_bulk_ไม่มี_primary_เป็น_NoPrimary_และบอก_key_ที่เห็น()
    {
        const string raw = """{"root_cause":null,"fixes":[{"warningIndex":0,"primary":"Edit","confidence":0.8}]}""";
        var p = AiFeatureResponseParser.Parse(raw);
        Assert.False(p.HasAnswer);
        Assert.Equal(AiParseProblem.NoPrimary, p.Problem);
        var msg = AiFeatureResponseParser.Describe(p, "stop", 120, 600);
        Assert.StartsWith("Schema mismatch", msg);
        Assert.Contains("root_cause, fixes", msg);
    }

    [Fact]
    public void ร้อยแก้วล้วน_เป็น_NotJson()
    {
        var p = AiFeatureResponseParser.Parse("ขออภัย ข้อมูลไม่เพียงพอที่จะระบุประเภทเงินได้ กรุณาระบุรายละเอียดเพิ่มเติม");
        Assert.Equal(AiParseProblem.NotJson, p.Problem);
        Assert.False(p.HasAnswer);
        Assert.StartsWith("Not JSON", AiFeatureResponseParser.Describe(p, "stop", 30, 600));
    }

    [Fact]
    public void ว่าง_เป็น_Empty()
    {
        Assert.Equal(AiParseProblem.Empty, AiFeatureResponseParser.Parse("  ").Problem);
        Assert.Equal(AiParseProblem.Empty, AiFeatureResponseParser.Parse(null).Problem);
    }

    [Fact]
    public void ขาดก่อนค่า_primary_ปิด_ไม่เดาส่วนที่ขาด()
    {
        const string raw = "{\"primary\":\"Payment";
        var p = AiFeatureResponseParser.Parse(raw);
        Assert.Equal(AiParseProblem.Truncated, p.Problem);
        Assert.False(p.HasAnswer);
        Assert.False(p.Salvaged);
        var msg = AiFeatureResponseParser.Describe(p, "length", 200, 200);
        Assert.StartsWith("Truncated", msg);
        Assert.Contains("finish_reason=length", msg);
        Assert.Contains("200/200", msg);
    }

    [Fact]
    public void ขาดกลางทาง_primary_null_ไม่ถูกแปลงเป็น_None_แม้_feature_ประกาศ()
    {
        // ไม่รู้ว่าโมเดลตั้งใจตอบ null หรือถูกตัด — ทิศ "ไม่ต้องหัก" ที่ผิดมองไม่เห็น (§54) ห้ามเดา
        var p = AiFeatureResponseParser.Parse("{\"primary\":null,\"confidence\":0.9,\"reasoning\":\"ซื้อสินค้า", nullPrimaryMeans: "None");
        Assert.Null(p.PrimaryAnswer);
        Assert.False(p.HasAnswer);
    }

    // ── เพดาน token ────────────────────────────────────────────────────

    [Fact]
    public void เพดาน_output_ขั้นต่ำของรูป_canonical()
    {
        Assert.Equal(AiFeatureResponseParser.MinCanonicalOutputTokens, AiFeatureResponseParser.EffectiveMaxTokens(200, false));
        Assert.Equal(1500, AiFeatureResponseParser.EffectiveMaxTokens(1500, false));
        Assert.Null(AiFeatureResponseParser.EffectiveMaxTokens(null, false));          // ใช้ค่าตั้ง provider (1,024)
        Assert.Equal(400, AiFeatureResponseParser.EffectiveMaxTokens(400, true));       // bulk คงตามผู้สร้าง prompt
    }

    // ── prompt ที่ถูกแก้ในรอบนี้ ───────────────────────────────────────

    [Fact]
    public void DocType_prompt_ขอชนิดปลายทาง_ครบทุกค่าใน_enum_และไม่ขอชนิดกระดาษเป็น_primary()
    {
        foreach (var t in Enum.GetValues<DocumentType>())
            Assert.Contains($"- {t} :", DocumentTypeClassifyPrompt.SystemPrompt);
        Assert.DoesNotContain("<physicalType>", DocumentTypeClassifyPrompt.SystemPrompt);
        var req = DocumentTypeClassifyPrompt.Build(Guid.NewGuid(), Guid.NewGuid(), "ใบเสร็จรับเงิน", null, null, null, "PaymentVoucher", 0.45m);
        Assert.True(req.MaxTokensOverride >= AiFeatureResponseParser.MinCanonicalOutputTokens);
    }

    [Fact]
    public void WHT_prompt_ประกาศ_null_คือ_None()
    {
        var req = WhtCategoryPrompt.Build(Guid.NewGuid(), null, "ร้านวัสดุ", null, "JuristicPerson", "ปูนซีเมนต์", 5000m, null, null);
        Assert.Equal("None", req.NullPrimaryAnswer);
        Assert.True(req.MaxTokensOverride >= AiFeatureResponseParser.MinCanonicalOutputTokens);
        // feature อื่นไม่ประกาศ (null = ไม่ตอบ)
        var cn = CreditNoteReasonPrompt.Build(Guid.NewGuid(), Guid.NewGuid(), new { }, null, "Return", 0.5m);
        Assert.Null(cn.NullPrimaryAnswer);
    }
}

/// <summary>orchestrator ตัวจริงกับ provider ปลอม — ล็อกว่าตัวอ่านถูก**ต่อสาย** (ถอดการเรียก Parse/เพดาน/LocalOnly แล้วต้องแดง)</summary>
public class AiOrchestratorResponseHandlingTests
{
    private static readonly Guid Cid = Guid.Parse("0a0a0a0a-0000-0000-0000-00000000020a");

    [Fact]
    public async Task JSON_ขาดกลางทาง_กู้คำตอบได้เป็น_Success_ไม่เก็บแคช_และส่งเพดาน_token_ขั้นต่ำ()
    {
        var provider = new FakeProvider(new AiProviderRawResponse
        {
            Success = true, FinishReason = "length", OutputTokens = 200, MaxTokensSent = 200,
            Content = "{\"primary\":\"PaymentVoucher\",\"confidence\":0.8,\"reasoning\":\"ใบเสร็จจากผู้ขาย เราเป็นผู้",
        });
        var rig = new Rig(provider);
        var resp = await rig.AskAsync(Req(maxTokens: 200));

        Assert.Equal(AiCallStatus.Success, resp.Status);
        Assert.True(resp.UsedAi);
        Assert.Equal("PaymentVoucher", resp.PrimaryAnswer);
        Assert.Equal(0, rig.CachePuts);                                               // ก้อนขาดห้ามเสิร์ฟซ้ำจากแคช
        Assert.Equal(AiFeatureResponseParser.MinCanonicalOutputTokens, provider.LastRequest!.MaxTokensOverride);
        Assert.StartsWith("Salvaged", rig.Records.Last().ErrorMessage);
    }

    [Fact]
    public async Task ร้อยแก้ว_เป็น_InvalidResponse_ระบุสาเหตุ_และตกไปหาคำตอบของผู้เรียก()
    {
        var provider = new FakeProvider(new AiProviderRawResponse
        {
            Success = true, FinishReason = "stop", Content = "ขออภัย ไม่สามารถระบุได้",
        });
        var rig = new Rig(provider);
        var resp = await rig.AskAsync(Req(maxTokens: 600) with { LocalPrimaryAnswer = "Expense", LocalConfidence = 0.4m });

        Assert.Equal(AiCallStatus.InvalidResponse, resp.Status);
        Assert.False(resp.UsedAi);
        Assert.Equal("Expense", resp.PrimaryAnswer);
        Assert.StartsWith("Not JSON", rig.Records.Last().ErrorMessage);
        Assert.Equal(0, rig.CachePuts);
    }

    [Fact]
    public async Task JSON_ครบ_เก็บแคช()
    {
        var rig = new Rig(new FakeProvider(new AiProviderRawResponse
        {
            Success = true, FinishReason = "stop", Content = """{"primary":"PurchaseInvoice","confidence":0.9}""",
        }));
        var resp = await rig.AskAsync(Req(maxTokens: 600));
        Assert.Equal("PurchaseInvoice", resp.PrimaryAnswer);
        Assert.Equal(1, rig.CachePuts);
        Assert.Null(rig.Records.Last().ErrorMessage);
    }

    [Fact]
    public async Task LocalOnly_ไม่ยิง_provider_แม้_provider_พร้อม()
    {
        var provider = new FakeProvider(new AiProviderRawResponse { Success = true, Content = """{"primary":"x"}""" });
        var rig = new Rig(provider, new FixedStudent(AiFeatureKey.FuzzyDuplicateDetection, "dup-id"));
        var resp = await rig.AskAsync(new AiRequest
        {
            FeatureKey = AiFeatureKey.FuzzyDuplicateDetection, CompanyId = Cid,
            SystemPrompt = "dup", UserPromptJson = "{\"document\":{}}", LocalOnly = true,
        });
        Assert.Null(provider.LastRequest);
        Assert.True(resp.FromLocalModel);
        Assert.Equal("dup-id", resp.PrimaryAnswer);

        // ทิศตรงข้าม: ไม่ตั้ง LocalOnly (นักเรียนมั่นใจต่ำกว่าเกณฑ์) ⇒ ยิง provider ตามปกติ
        var provider2 = new FakeProvider(new AiProviderRawResponse { Success = true, Content = """{"primary":"x","confidence":0.9}""" });
        var rig2 = new Rig(provider2, new FixedStudent(AiFeatureKey.FuzzyDuplicateDetection, "dup-id"));
        await rig2.AskAsync(new AiRequest
        {
            FeatureKey = AiFeatureKey.FuzzyDuplicateDetection, CompanyId = Cid,
            SystemPrompt = "dup", UserPromptJson = "{\"document\":{}}",
        });
        Assert.NotNull(provider2.LastRequest);
    }

    private static AiRequest Req(int maxTokens) => new()
    {
        FeatureKey = AiFeatureKey.DocumentTypeClassification,
        CompanyId = Cid,
        SystemPrompt = "doc type",
        UserPromptJson = "{\"task\":\"document_type_classify\",\"n\":" + Random.Shared.Next() + "}",
        MaxTokensOverride = maxTokens,
    };

    private sealed class FakeProvider : IAiProvider
    {
        private readonly AiProviderRawResponse _resp;
        public FakeProvider(AiProviderRawResponse resp) { _resp = resp; }
        public AiRequest? LastRequest { get; private set; }
        public AiProviderType Kind => AiProviderType.DeepSeek;
        public Task<AiProviderRawResponse> CompleteAsync(AiRequest request, AiProviderConfig config, CancellationToken ct)
        { LastRequest = request; return Task.FromResult(_resp); }
        public Task<(bool ok, string? error)> TestConnectionAsync(AiProviderConfig config, CancellationToken ct)
            => Task.FromResult<(bool, string?)>((true, null));
    }

    private sealed class FixedStudent : ILocalDistillationModel
    {
        private readonly string _answer;
        public FixedStudent(AiFeatureKey key, string answer) { FeatureKey = key; _answer = answer; }
        public AiFeatureKey FeatureKey { get; }
        public string Version => "fake";
        public bool IsReady => true;
        public Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct) => Task.CompletedTask;
        public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
            => Task.FromResult<LocalPrediction?>(new LocalPrediction(_answer, 0.50m, Array.Empty<string>(), 3, "fake"));
    }

    private sealed class Rig : AiOrchestrator
    {
        private readonly Cache _cache;
        private readonly Recorder _rec;
        public int CachePuts => _cache.Puts;
        public List<AiFeedbackRecord> Records => _rec.Rows;

        public Rig(IAiProvider provider, params ILocalDistillationModel[] students)
            : this(provider, new Cache(), new Recorder(), students) { }

        private Rig(IAiProvider provider, Cache cache, Recorder rec, ILocalDistillationModel[] students)
            : base(OfflineDb(), new[] { provider }, cache, new AllowBudget(), students, new HybridRouting(), rec,
                new AiPromptSanitizer(), NullLogger<AiOrchestrator>.Instance)
        { _cache = cache; _rec = rec; }

        protected override Task<SiteSettings?> LoadSiteSettingsAsync(CancellationToken ct)
            => Task.FromResult<SiteSettings?>(new SiteSettings { AiAugmentationEnabled = true });

        protected override Task<AiProviderConfig?> LoadActiveProviderAsync(CancellationToken ct)
            => Task.FromResult<AiProviderConfig?>(new AiProviderConfig
            {
                ProviderType = AiProviderType.DeepSeek, IsActive = true, IsEnabled = true, Model = "deepseek-chat",
            });

        private static Accounting.Data.AccountingDbContext OfflineDb()
            => new(new DbContextOptionsBuilder<Accounting.Data.AccountingDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none;Timeout=1").Options);
    }

    private sealed class Cache : IAiResponseCacheService
    {
        public int Puts;
        public Task<(bool hit, string? content, Guid? originalFeedbackId, string? modelVersion, decimal? confidence)>
            TryGetAsync(string promptHash, Guid companyId, CancellationToken ct)
            => Task.FromResult<(bool, string?, Guid?, string?, decimal?)>((false, null, null, null, null));
        public Task PutAsync(string promptHash, string featureKey, string responseJson,
            AiProviderType provider, string? modelVersion, decimal? confidence,
            Guid companyId, int ttlDays, CancellationToken ct)
        { Puts++; return Task.CompletedTask; }
    }

    private sealed class AllowBudget : IAiBudgetGuard
    {
        public Task<BudgetDecision> EvaluateAsync(Guid activeProviderConfigId, int? dailyCallCap,
            decimal? monthlyBudgetUsd, CancellationToken ct)
            => Task.FromResult(new BudgetDecision(true, false, null, 0, 0m));
    }

    private sealed class HybridRouting : IAiFeatureRoutingResolver
    {
        public Task<AiFeatureRoutingDecision> ResolveAsync(AiFeatureKey feature, CancellationToken ct)
            => Task.FromResult(new AiFeatureRoutingDecision(AiFeatureRoutingMode.Hybrid, 0.85m, 0.10m));
        public Task SetAsync(AiFeatureKey feature, AiFeatureRoutingMode mode,
            decimal? threshold, decimal? samplingRate, string? note, string? user, CancellationToken ct)
            => Task.CompletedTask;
        public Task<IReadOnlyList<AiFeatureRoutingConfig>> ListAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AiFeatureRoutingConfig>>(Array.Empty<AiFeatureRoutingConfig>());
        public void InvalidateCache() { }
    }

    private sealed class Recorder : IAiFeedbackRecorder
    {
        public readonly List<AiFeedbackRecord> Rows = new();
        public Task<Guid> RecordCallAsync(AiFeedbackRecord record, CancellationToken ct)
        { Rows.Add(record); return Task.FromResult(Guid.NewGuid()); }
        public Task RecordUserChoiceAsync(Guid feedbackId, string chosenAnswer, bool acceptedAi,
            CancellationToken ct, UserChoiceSource source = UserChoiceSource.Implicit) => Task.CompletedTask;
        public Task<IReadOnlyList<Guid>> RecordChildBatchAsync(IReadOnlyList<AiFeedbackRecord> records, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Guid>>(records.Select(_ => Guid.NewGuid()).ToList());
    }
}
