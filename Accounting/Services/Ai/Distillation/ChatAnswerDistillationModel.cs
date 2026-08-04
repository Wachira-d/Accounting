using System.Text.Json;
using Accounting.Data;
using Accounting.Models.Enums;
using Accounting.Services.Ai.Embedding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// Student ของ chatbot (PublicFaqChat / TenantAssistantChat) — คำตอบเป็น
/// free-form essay จึงใช้ GenericFeedbackDistillationModel (exact fingerprint)
/// ไม่ได้: คำถามเดียวกันสะกดต่างกันนิดเดียว fingerprint ก็หลุด.
///
/// แนวทาง: จำคู่ (คำถาม → คำตอบที่ผ่านการยืนยัน) แล้วจับคู่คำถามใหม่ด้วย
/// cosine ของ IEmbeddingService — คำถามใกล้เคียง ≥ 0.90 = ตอบจากความจำ
/// (short-circuit DeepSeek → cost ลดลงเรื่อย ๆ ตามที่ผู้ใช้กด 👍 มากขึ้น).
///
/// แหล่งเรียนรู้ (LoadFromFeedbackAsync):
///   • แถวที่ผู้ใช้กด 👍 (UserChosenAnswer = คำตอบ, UserAcceptedAi = true)
///   • แถว teacher ที่ confidence สูงและไม่ถูก 👎 (pseudo-label)
/// แถวที่โดน 👎 = negative — ลบคู่นั้นออกจากความจำ (คำตอบผิดต้องไม่ถูกทวน)
///
/// Cold start: ไม่มีความจำ → IsReady=false → orchestrator ไปหา provider;
/// provider ล่มด้วย → caller (ChatbotService) ใส่ LocalPrimaryAnswer เป็น
/// คำตอบจาก retrieval ล้วนไว้แล้ว — kill-switch แล้ว chatbot ยังตอบได้เสมอ
/// (กลายเป็น doc-search bot ไม่ใช่บอทใบ้)
/// </summary>
public sealed class ChatAnswerDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey { get; }
    public string Version { get; private set; } = "v0";
    public bool IsReady { get { lock (_lock) return _memory.Count > 0; } }

    private readonly IServiceProvider _services;
    private readonly IEmbeddingService _embed;
    private readonly ILogger<ChatAnswerDistillationModel> _logger;

    private sealed record Memory(Guid CompanyId, string Question, float[] Vector, string Answer, int Upvotes);
    private readonly List<Memory> _memory = new();
    private readonly object _lock = new();

    /// <summary>public chat ใช้ CompanyId = Guid.Empty (ไม่มีบริษัท) —
    /// ความจำ public จึงแชร์กันทุก visitor; tenant แยกต่อบริษัท</summary>
    public ChatAnswerDistillationModel(AiFeatureKey featureKey, IServiceProvider services,
        IEmbeddingService embed, ILogger<ChatAnswerDistillationModel> logger)
    {
        FeatureKey = featureKey;
        _services = services;
        _embed = embed;
        _logger = logger;
    }

    public async Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
        var key = FeatureKey.ToString();

        var rows = await db.AiSuggestionFeedbacks.AsNoTracking()
            .Where(f => f.FeatureKey == key && !f.IsDeleted)
            .OrderByDescending(f => f.CreatedAt)
            .Take(2000)
            .Select(f => new { f.CompanyId, f.PromptJson, f.AiPrimaryAnswer, f.ResponseJson,
                f.AiConfidence, f.UserChosenAnswer, f.UserAcceptedAi })
            .ToListAsync(ct);

        var fresh = new List<Memory>();
        var negatives = new HashSet<string>();
        foreach (var r in rows)
        {
            var question = ExtractQuestion(r.PromptJson);
            if (string.IsNullOrWhiteSpace(question) || question.Length > 600) continue;

            // 👎 = อย่าจำคำตอบของคำถามนี้ (ระบุด้วย UserAcceptedAi=false ไม่มี answer เลือกใหม่)
            if (r.UserAcceptedAi == false && string.IsNullOrWhiteSpace(r.UserChosenAnswer))
            { negatives.Add(Normalize(question)); continue; }

            // คำตอบที่ใช้ได้: ที่ผู้ใช้ยืนยัน > คำตอบ AI ที่มั่นใจสูง
            var answer = !string.IsNullOrWhiteSpace(r.UserChosenAnswer) ? r.UserChosenAnswer
                : (r.UserAcceptedAi == true || (r.AiConfidence ?? 0) >= 0.75m)
                    ? (r.AiPrimaryAnswer ?? ExtractRawAnswer(r.ResponseJson)) : null;
            if (string.IsNullOrWhiteSpace(answer) || answer.Length < 20) continue;

            fresh.Add(new Memory(r.CompanyId, question, _embed.Embed(Normalize(question)),
                answer!, r.UserAcceptedAi == true ? 2 : 1));
        }
        var kept = fresh.Where(m => !negatives.Contains(Normalize(m.Question)))
            // คำถามซ้ำเก็บตัวที่ upvote สูงสุด/ใหม่สุด
            .GroupBy(m => (m.CompanyId, Normalize(m.Question)))
            .Select(g => g.OrderByDescending(m => m.Upvotes).First())
            .ToList();

        lock (_lock)
        {
            _memory.Clear();
            _memory.AddRange(kept);
            Version = $"v{DateTime.UtcNow:yyyyMMddHH}-{kept.Count}";
        }
        _logger.LogInformation("{Feature} chat memory loaded: {N} q/a pairs", key, kept.Count);
    }

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
    {
        var question = ExtractQuestion(inputJson);
        if (string.IsNullOrWhiteSpace(question)) return Task.FromResult<LocalPrediction?>(null);
        var qv = _embed.Embed(Normalize(question));

        Memory? best = null; double bestScore = 0;
        lock (_lock)
        {
            foreach (var m in _memory)
            {
                // public (Guid.Empty) แชร์ทุกคน; tenant ใช้ของบริษัทตัวเอง + ของกลาง
                if (m.CompanyId != companyId && m.CompanyId != Guid.Empty) continue;
                double dot = 0;
                for (var i = 0; i < qv.Length && i < m.Vector.Length; i++) dot += qv[i] * m.Vector[i];
                if (dot > bestScore) { bestScore = dot; best = m; }
            }
        }
        if (best == null || bestScore < 0.90) return Task.FromResult<LocalPrediction?>(null);

        // ความมั่นใจตามความใกล้ + จำนวนการยืนยัน — คำถามตรงเป๊ะที่เคย 👍
        // ทะลุ 0.85 → short-circuit ไม่จ่าย DeepSeek ซ้ำ
        var conf = Math.Min(0.97m, (decimal)bestScore * (best.Upvotes >= 2 ? 1.0m : 0.93m));
        return Task.FromResult<LocalPrediction?>(new LocalPrediction(
            best.Answer, conf, Array.Empty<string>(), best.Upvotes, Version));
    }

    /// <summary>prompt ของ chat คือ {"question":"...","context":[...]} — สนใจ
    /// เฉพาะคำถาม (context เปลี่ยนตาม retrieval ห้ามปนใน key)</summary>
    internal static string ExtractQuestion(string promptJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(promptJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("question", out var q))
                return q.GetString() ?? "";
        }
        catch { /* ไม่ใช่ JSON — ใช้ทั้งก้อน */ }
        return promptJson;
    }

    private static string? ExtractRawAnswer(string? responseJson)
    {
        if (string.IsNullOrWhiteSpace(responseJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement.ValueKind == JsonValueKind.String
                ? doc.RootElement.GetString() : responseJson;
        }
        catch { return responseJson; }
    }

    private static string Normalize(string s) =>
        string.Join(' ', s.Trim().ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
