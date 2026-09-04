using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// A local model that learns FROM DeepSeek responses + user confirmations
/// — the user's "use DeepSeek to teach local model first" insight made
/// concrete. Each implementation owns one AiFeatureKey:
///
///   • LoadFromFeedbackAsync — mine AiSuggestionFeedback rows where the
///     user confirmed a choice and re-build the model state in memory
///     (or persist it back to a learned-pattern table, e.g.
///     OcrCategoryMapping / VendorKnownGoodValue).
///
///   • PredictAsync — given the same input shape DeepSeek would have
///     received, return the local model's best guess + confidence. The
///     orchestrator calls this BEFORE deciding whether to hit DeepSeek
///     so a confident local prediction can skip the expensive call.
///
///   • Version — bumped each time LoadFromFeedbackAsync rebuilds. The
///     LocalModelHealth row stores this so accuracy stats can be
///     attributed to a specific model version (drift detection).
///
/// Lifecycle:
///   - Job loads all enabled trainers nightly + calls
///     LoadFromFeedbackAsync on each.
///   - Orchestrator injects IEnumerable&lt;ILocalDistillationModel&gt;
///     and picks by FeatureKey before each AskAsync call.
///   - Predictions surface to the user UNDER the AI response — high-
///     confidence local + low-confidence (or absent) AI = local wins.
///
/// This is the closed-loop teacher–student pattern: DeepSeek (the
/// teacher) bootstraps labels we couldn't otherwise get; the local
/// student approximates DeepSeek behaviour; when accuracy crosses a
/// threshold the system routes around DeepSeek (cost saver) but keeps
/// a calibration sample firing so health stats stay current.
/// </summary>
public interface ILocalDistillationModel
{
    AiFeatureKey FeatureKey { get; }
    string Version { get; }

    /// <summary>True after LoadFromFeedbackAsync has run at least once
    /// AND found enough training rows to produce a useful prediction.</summary>
    bool IsReady { get; }

    /// <summary>Rebuild the model from accumulated user-confirmed
    /// feedback rows. Called nightly by the training job + on first use.</summary>
    Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct);

    /// <summary>Predict for the SAME input shape DeepSeek would receive
    /// (each implementation knows the schema of its FeatureKey's prompt).
    /// Returns null when the local model isn't ready or can't confidently
    /// match — orchestrator then falls through to DeepSeek.</summary>
    Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct);
}

public sealed record LocalPrediction(
    string PrimaryAnswer,
    decimal Confidence,                          // 0.0–1.0
    IReadOnlyList<string> Alternatives,
    int SupportingSamples,                       // how many training rows backed this
    string ModelVersion)
{
    /// <summary>คำตอบแบบ **มีโครง** ของนักเรียน — รูปเดียวกับ JSON ที่ AI จะคืน
    /// สำหรับ feature นั้น · <c>null</c> = นักเรียนตอบได้แค่คำตอบเดี่ยว
    ///
    /// <para>⚠️ ที่มา (ผลตรวจ E-AI-01): feature ที่ผลลัพธ์เป็น **โครงสร้าง**
    /// (ตรวจใบทั้งใบ · แตกบรรทัด · จับคู่คอลัมน์นำเข้า) หน้าเว็บวาดจาก
    /// <c>StructuredJson</c> ซึ่งมาจาก <c>AiResponse.RawResponseJson</c> —
    /// แต่ทั้งเส้น local (<c>ReturnLocalAsync</c> ตอน short-circuit และ
    /// <c>FallbackToLocal</c> ตอน AI ล่ม) **ไม่มีช่องให้ส่งค่านี้เลย** ⇒ ค่าเป็น
    /// null เสมอ ⇒ ผู้ใช้เห็น "ตรวจสอบเสร็จ" คู่กับแผงว่าง</para>
    ///
    /// <para>นี่คือการละเมิดกฎเหล็ก #1 ข้อ "local ต้องทดแทน AI ได้ 100%" โดยตรง:
    /// นักเรียนที่เขียนไว้เพื่อ kill-switch ถูกทิ้งคำตอบทุกครั้งที่ตอบเป็นโครงสร้าง ·
    /// นักเรียนเดิมที่คืนคำตอบเดี่ยวไม่กระทบ (ค่า default = null = พฤติกรรมเดิม)</para></summary>
    public string? StructuredJson { get; init; }
}
