using Accounting.Models.Entities;
using Accounting.Models.Enums;
using Accounting.Services.Ai;
using Accounting.Services.Ai.Distillation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Accounting.Tests;

/// <summary>
/// **Kill-switch ผ่าน <see cref="AiOrchestrator"/> ตัวจริง** — รอบ 201 ทีม AI · A-AI6 (report-H H-8)
///
/// <para><c>AiKillSwitchTests</c> เดิมเรียกแต่ pure helper ⇒ "ผ่านตลอดกาล" (ด่านที่ป้อนผลของสูตรที่ตัวเองตรวจ · F2 ข้อ 6)
/// ชุดนี้ประกอบ orchestrator จริง (เส้นตัดสินเส้นทางทั้งหมดของ <c>AskInternalAsync</c>) ที่ provider ทุกตัว
/// <c>IsActive=false</c> แล้วยืนยันว่า (ก) คำตอบนักเรียนออกมาถึงผู้เรียก (ข) ทะเบียนนักเรียน<b>จริง</b>
/// (<see cref="DistillationModelRegistry"/> — เมธอดเดียวกับที่ <c>Program.cs</c> เรียก) ครบทุก <see cref="AiFeatureKey"/>
/// ยกเว้นรายการค้างที่ยืนยันแล้ว (ratchet) · และ negative: ถอดนักเรียนแล้วต้องล้ม</para>
/// </summary>
public class AiKillSwitchOrchestratorTests
{
    private static readonly Guid Cid = Guid.Parse("0a0a0a0a-0000-0000-0000-000000000201");

    private static List<AiProviderConfig> AllProvidersOff() => new()
    {
        new() { ProviderType = AiProviderType.DeepSeek, IsActive = false, IsEnabled = true },
        new() { ProviderType = AiProviderType.OpenAi, IsActive = false, IsEnabled = true },
        // เปิดอยู่แต่ถูกปิดใช้ (IsEnabled=false) = ใช้ไม่ได้เหมือนกัน
        new() { ProviderType = AiProviderType.LocalLlama, IsActive = true, IsEnabled = false },
    };

    private static AiRequest Req(AiFeatureKey key) => new()
    {
        FeatureKey = key,
        CompanyId = Cid,
        SystemPrompt = "kill-switch",
        UserPromptJson = "{\"q\":\"kill-switch\"}",
    };

    [Fact]
    public async Task ปิด_provider_ทุกตัว_นักเรียนของทุก_feature_ที่ลงทะเบียน_ต้องตอบถึงผู้เรียกผ่าน_orchestrator()
    {
        var registered = KillSwitchRig.RegisteredFeatureKeys();
        Assert.NotEmpty(registered);
        foreach (var key in registered)
        {
            var orch = new KillSwitchRig.Orchestrator(new[] { new KillSwitchRig.Student(key, "ANS-" + key) },
                AllProvidersOff());
            var resp = await orch.AskAsync(Req(key));
            Assert.True(resp.FromLocalModel, $"{key}: ปิด provider แล้วคำตอบนักเรียนต้องออกมา (FromLocalModel)");
            Assert.False(resp.UsedAi, $"{key}: ห้ามติดป้ายว่าใช้ AI");
            Assert.Equal("ANS-" + key, resp.PrimaryAnswer);
            Assert.Equal(AiCallStatus.NoProvider, resp.Status);
        }
    }

    [Fact]
    public async Task ถอดนักเรียนออก_orchestrator_ต้องไม่แต่งคำตอบ_และป้ายต้องไม่บอกว่านักเรียนตอบ()
    {
        // negative ของเทสต์ข้างบน — ถ้าเทสต์ข้างบนยังผ่านได้ในสภาพไม่มีนักเรียน = เทสต์ไม่มีความหมาย
        var orch = new KillSwitchRig.Orchestrator(Array.Empty<ILocalDistillationModel>(), AllProvidersOff());
        var resp = await orch.AskAsync(Req(AiFeatureKey.BankStatementMatch));
        Assert.False(resp.FromLocalModel);
        Assert.Null(resp.PrimaryAnswer);
        Assert.Equal(AiCallStatus.NoProvider, resp.Status);
    }

    [Fact]
    public async Task ฐานข้อมูลล่มระหว่างหา_provider_คำตอบนักเรียนต้องรอดออกมา()
    {
        // เดิม outer-catch ใช้ request ตั้งต้น (ยังไม่มีคำตอบนักเรียน) ⇒ ฐานข้อมูล/provider ล่ม = คำตอบหายทั้งที่ทำนายไว้แล้ว
        var key = AiFeatureKey.GlAccountSuggestion;
        var orch = new KillSwitchRig.Orchestrator(new[] { new KillSwitchRig.Student(key, "51100") },
            AllProvidersOff(), throwOnProviderLookup: true);
        var resp = await orch.AskAsync(Req(key));
        Assert.Equal(AiCallStatus.Failed, resp.Status);
        Assert.True(resp.FromLocalModel);
        Assert.Equal("51100", resp.PrimaryAnswer);
    }

    [Fact]
    public void ตัวกรอง_provider_ที่เปิดใช้_รับเฉพาะ_IsActive_และ_IsEnabled()
    {
        Assert.Null(AllProvidersOff().AsQueryable().FirstOrDefault(AiOrchestrator.ActiveProviderFilter));
        var on = AllProvidersOff();
        on[0].IsActive = true;
        Assert.Same(on[0], on.AsQueryable().FirstOrDefault(AiOrchestrator.ActiveProviderFilter));
    }

    [Fact]
    public void ทะเบียนนักเรียนจริง_ครบทุก_AiFeatureKey_ยกเว้นรายการค้างที่ยืนยันแล้ว()
    {
        var missing = DistillationModelRegistry.MissingStudents(KillSwitchRig.RegisteredFeatureKeys());
        Assert.True(missing.Count == 0,
            "feature ที่ไม่มีนักเรียน (กฎเหล็ก #1 ข้อ 2): " + string.Join(", ", missing)
            + " — ลงทะเบียนนักเรียนใน DistillationModelRegistry (ห้ามเพิ่มแถวในรายการค้าง)");
    }

    [Fact]
    public void รายการค้างต้องไม่ซ้อนกับนักเรียนที่ลงทะเบียนแล้ว()
    {
        // เพิ่มนักเรียนให้ feature ไหนแล้วต้องลบแถวค้างของมันในคอมมิตเดียวกัน — ไม่งั้นรายการค้างโกหก (ratchet สองทิศ)
        var registered = KillSwitchRig.RegisteredFeatureKeys();
        var stale = DistillationModelRegistry.KnownGapsWithoutStudent.Keys.Where(registered.Contains).ToList();
        Assert.True(stale.Count == 0, "รายการค้างที่มีนักเรียนแล้ว: " + string.Join(", ", stale));
    }

    [Fact]
    public void รายการค้างที่ไม่มีนักเรียน_ลดได้ทางเดียว_ห้ามเพิ่มแถว()
    {
        // ฝ่ายค้าน X-8: ratchet ของจำนวน — รอบ 201 = 38 (มีจุดเรียก AI 29 + ไม่มีจุดเรียก 9) · เพิ่มนักเรียนแล้วลดตัวเลขนี้ลงในคอมมิตเดียวกัน
        const int Round201Baseline = 38;
        Assert.True(DistillationModelRegistry.KnownGapsWithoutStudent.Count <= Round201Baseline,
            $"รายการค้างโต {DistillationModelRegistry.KnownGapsWithoutStudent.Count} > {Round201Baseline} — feature ใหม่ต้องมีนักเรียน ห้ามเพิ่มแถวค้าง");
    }

    [Fact]
    public void ถอดนักเรียน_1_ตัวออกจากทะเบียน_ตัวตรวจต้องฟ้อง()
    {
        // negative test ของตัวตรวจทะเบียน — ตัวตรวจที่ไม่ฟ้องเมื่อถอดนักเรียน = ตัวตรวจที่พัง
        var without = KillSwitchRig.RegisteredFeatureKeys()
            .Where(k => k != AiFeatureKey.BankStatementMatch && k != AiFeatureKey.PaymentVoucherAccountingSuggestion)
            .ToList();
        var missing = DistillationModelRegistry.MissingStudents(without);
        Assert.Contains(AiFeatureKey.BankStatementMatch, missing);
        Assert.Contains(AiFeatureKey.PaymentVoucherAccountingSuggestion, missing);
    }

    [Fact]
    public void ใบสำคัญจ่ายทั้งใบ_ต้องมีนักเรียนแบบมีโครง_ไม่ใช่_generic()
    {
        // A-AI4: generic ตอบรูป {"lines":[…]} ไม่ได้ ⇒ ลงทะเบียน generic ซ้ำ = กลับไปเป็นบั๊กเดิม
        Assert.DoesNotContain(AiFeatureKey.PaymentVoucherAccountingSuggestion, DistillationModelRegistry.GenericFeatureKeys);
        Assert.Contains(typeof(PaymentVoucherAccountingDistillationModel), DistillationModelRegistry.BespokeModels);
    }
}

/// <summary>ชุดประกอบเทสต์ kill-switch — orchestrator ตัวจริงที่ป้อนค่าตั้ง/รายชื่อ provider แทนฐานข้อมูล
/// (seam <c>LoadSiteSettingsAsync</c>/<c>LoadActiveProviderAsync</c>) · ตัวอื่นเป็นของปลอมที่ "ห้ามถูกเรียก" บนเส้น provider ปิด</summary>
internal static class KillSwitchRig
{
    public static HashSet<AiFeatureKey> RegisteredFeatureKeys()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Accounting.Services.Ai.Embedding.IEmbeddingService>(
            new Accounting.Services.Ai.Embedding.HashingEmbeddingService());
        DistillationModelRegistry.AddLocalDistillationModels(services);
        using var sp = services.BuildServiceProvider();
        return sp.GetServices<ILocalDistillationModel>().Select(m => m.FeatureKey).ToHashSet();
    }

    public sealed class Student : ILocalDistillationModel
    {
        private readonly string _answer;
        public Student(AiFeatureKey key, string answer) { FeatureKey = key; _answer = answer; }
        public AiFeatureKey FeatureKey { get; }
        public string Version => "fake";
        public bool IsReady => true;
        public Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct) => Task.CompletedTask;
        // ความมั่นใจต่ำกว่าเกณฑ์ short-circuit (0.85) ⇒ orchestrator ต้องเดินไปถึงขั้นหา provider จริง
        public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
            => Task.FromResult<LocalPrediction?>(new LocalPrediction(_answer, 0.50m, Array.Empty<string>(), 3, "fake"));
    }

    public sealed class Orchestrator : AiOrchestrator
    {
        private readonly List<AiProviderConfig> _configs;
        private readonly bool _throw;

        public Orchestrator(IEnumerable<ILocalDistillationModel> students, List<AiProviderConfig> configs,
            bool throwOnProviderLookup = false)
            : base(OfflineDb(), Array.Empty<IAiProvider>(), new NoCache(), new NoBudget(),
                students, new HybridRouting(), new Recorder(), new NoSanitizer(),
                NullLogger<AiOrchestrator>.Instance)
        { _configs = configs; _throw = throwOnProviderLookup; }

        protected override Task<SiteSettings?> LoadSiteSettingsAsync(CancellationToken ct)
            => Task.FromResult<SiteSettings?>(new SiteSettings { AiAugmentationEnabled = true });

        protected override Task<AiProviderConfig?> LoadActiveProviderAsync(CancellationToken ct)
        {
            if (_throw) throw new InvalidOperationException("ฐานข้อมูลล่ม (จำลอง)");
            return Task.FromResult(_configs.AsQueryable().FirstOrDefault(ActiveProviderFilter));
        }

        private static Accounting.Data.AccountingDbContext OfflineDb()
            => new(new DbContextOptionsBuilder<Accounting.Data.AccountingDbContext>()
                .UseNpgsql("Host=127.0.0.1;Port=1;Database=offline;Username=none;Password=none").Options);
    }

    private sealed class NoCache : IAiResponseCacheService
    {
        public Task<(bool hit, string? content, Guid? originalFeedbackId, string? modelVersion, decimal? confidence)>
            TryGetAsync(string promptHash, Guid companyId, CancellationToken ct)
            => throw new InvalidOperationException("ไม่ควรถึงแคช");
        public Task PutAsync(string promptHash, string featureKey, string responseJson,
            AiProviderType provider, string? modelVersion, decimal? confidence,
            Guid companyId, int ttlDays, CancellationToken ct)
            => throw new InvalidOperationException("ไม่ควรถึงแคช");
    }

    private sealed class NoBudget : IAiBudgetGuard
    {
        public Task<BudgetDecision> EvaluateAsync(Guid activeProviderConfigId, int? dailyCallCap,
            decimal? monthlyBudgetUsd, CancellationToken ct)
            => throw new InvalidOperationException("ไม่ควรถึงด่านงบ");
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
        public Task<Guid> RecordCallAsync(AiFeedbackRecord record, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
        public Task RecordUserChoiceAsync(Guid feedbackId, string chosenAnswer, bool acceptedAi,
            CancellationToken ct, UserChoiceSource source = UserChoiceSource.Implicit) => Task.CompletedTask;
        public Task<IReadOnlyList<Guid>> RecordChildBatchAsync(IReadOnlyList<AiFeedbackRecord> records, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Guid>>(records.Select(_ => Guid.NewGuid()).ToList());
    }

    private sealed class NoSanitizer : IAiPromptSanitizer
    {
        public string Sanitize(string userPromptJson, bool stripPii) => throw new InvalidOperationException("ไม่ควรถึงขั้นส่งออก");
        public string Sanitize(string userPromptJson, bool stripPii, bool keepTaxIds)
            => throw new InvalidOperationException("ไม่ควรถึงขั้นส่งออก");
        public string ComputePromptHash(string sanitizedUserPromptJson, string systemPrompt, string model)
            => throw new InvalidOperationException("ไม่ควรถึงขั้นส่งออก");
    }
}
