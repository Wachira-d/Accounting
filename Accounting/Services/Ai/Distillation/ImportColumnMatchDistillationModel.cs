using Accounting.Models.Enums;

namespace Accounting.Services.Ai.Distillation;

/// <summary>
/// นักเรียนของ <see cref="AiFeatureKey.ImportColumnMatch"/> (จับคู่หัวคอลัมน์ไฟล์นำเข้า → field ปลายทาง) — รอบ 205 · IMPORT_ETL_REVIEW §2.6
///
/// <para><b>สถานะ: โครง (T0)</b> — ลงทะเบียนใน <see cref="DistillationModelRegistry.BespokeModels"/> แล้ว (ถอดออกจากรายการค้าง) ·
/// <b>ทีม A เป็นเจ้าของสมอง</b>: ตอบรูปเดียวกับครู <c>StructuredJson = {"mappings":[…]}</c> จากชั้น local ทั้งหมดของ
/// <c>ImportColumnMapper</c> (แม่แบบบริษัท → คำพ้อง <c>ImportFieldSynonymSeed</c> → header→field ที่ผู้ใช้ยืนยันแล้ว) ·
/// เรียนจาก <c>AiSuggestionFeedback</c> ที่ <c>SmartImportSession.ColumnMatchAiFeedbackId</c> ปิดลูปไว้ตอน commit/remap</para>
///
/// <para>ระหว่างนี้ <see cref="PredictAsync"/> คืน <c>null</c> (= "นักเรียนไม่มีคำตอบ" ตามสัญญาของ <see cref="ILocalDistillationModel"/>)
/// ⇒ พฤติกรรมเท่าเดิม: ผู้เรียกใช้ heuristic ของตัวเองต่อ (ImportExportService) — ไม่แต่งคำตอบ (F2 ข้อ 3)</para>
/// </summary>
public sealed class ImportColumnMatchDistillationModel : ILocalDistillationModel
{
    public AiFeatureKey FeatureKey => AiFeatureKey.ImportColumnMatch;
    public string Version => "t0-skeleton";

    /// <summary>ยังไม่มีคลังให้ตอบ (ทีม A เติม)</summary>
    public bool IsReady => false;

    public Task LoadFromFeedbackAsync(Guid companyId, CancellationToken ct) => Task.CompletedTask;

    public Task<LocalPrediction?> PredictAsync(Guid companyId, string inputJson, CancellationToken ct)
        => Task.FromResult<LocalPrediction?>(null);
}
