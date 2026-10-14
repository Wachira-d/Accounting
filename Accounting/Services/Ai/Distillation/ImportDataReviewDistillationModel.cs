using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// นักเรียนของ <see cref="AiFeatureKey.ImportDataReview"/> (ตรวจข้อมูลทั้งชุดก่อนนำเข้า) — รอบ 205 · IMPORT_ETL_REVIEW §2.6
///
/// <para><b>สถานะ: โครง (T0)</b> — ลงทะเบียนใน <see cref="DistillationModelRegistry.BespokeModels"/> แล้ว (ถอดออกจากรายการค้าง) ·
/// <b>ทีม A เป็นเจ้าของสมอง</b>: ห่อ <c>ImportReviewHeuristics.Review</c> (กติกาที่มีอยู่แล้ว) ให้ตอบรูปเดียวกับครูผ่าน
/// <c>StructuredJson</c> · ด่าน: row/field ที่อ้างต้องมีจริง · เรียนจากการรับ/ปฏิเสธคำแนะนำ (<c>RecordUserChoiceAsync</c>)</para>
///
/// <para>ระหว่างนี้ <see cref="PredictAsync"/> คืน <c>null</c> (= "นักเรียนไม่มีคำตอบ") ⇒ ผู้เรียกเดิมใช้
/// <c>ImportReviewHeuristics</c> ของตัวเองต่อเหมือนเดิม — ไม่แต่งคำตอบ (F2 ข้อ 3)</para>
/// </summary>
public sealed class ImportDataReviewDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.ImportDataReview;
    public string Version => "t0-skeleton";

    /// <summary>ยังไม่มีคลังให้ตอบ (ทีม A เติม)</summary>
    public bool IsReady => false;

    public Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct) => Task.CompletedTask;

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
        => Task.FromResult<LocalPrediction?>(null);
}
