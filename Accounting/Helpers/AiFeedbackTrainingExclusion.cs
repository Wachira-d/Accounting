using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียวว่า "แถว feedback นี้ห้ามใช้สอนนักเรียน" (<c>AiSuggestionFeedback.ExcludedFromTraining</c>) — W9 R-59
///
/// <para>ที่มา: ทีม H (คอมมิต 7e503cf2 · 2026-10-22 15:54:58Z) เปลี่ยน prompt <see cref="AiFeatureKey.DocumentTypeClassification"/> จากขอ
/// "ชนิดกระดาษ" (TaxInvoice/Receipt) เป็น "ชนิดปลายทางที่จะสร้าง" (Expense/PaymentVoucher) ⇒ แถว <c>AiPrimaryAnswer</c> ก่อนวันนั้นเป็นคำตอบ
/// คนละความหมายกับคลังคำตอบของนักเรียน/ผู้ใช้ · <c>GenericFeedbackDistillationModel</c> เรียน "คำตอบครูที่มั่นใจแต่ไม่มีคนแตะ" เป็น weak label
/// ⇒ สอนผิด</para>
///
/// <para>กติกา (G3 ไม่เดา · กฎเหล็ก #1 ห้ามทำให้นักเรียนถดถอย):</para>
/// <list type="bullet">
/// <item>ตัด<b>เฉพาะ</b>แถวก่อน cutoff ที่<b>ไม่มีคำตอบของผู้ใช้</b> — label เดียวของแถวคือคำตอบครูความหมายเก่า</item>
/// <item>แถวก่อน cutoff ที่ผู้ใช้เลือกแล้ว<b>ไม่ตัด</b> — <c>UserChosenAnswer</c> มาจากฟอร์มชนิดปลายทาง (ความหมายปัจจุบัน) อยู่แล้ว =
/// ความจริงของบริษัทที่ห้ามทิ้ง · คำตอบครูเก่าในแถวนั้นถูกใช้เป็น negative example เท่านั้น (ชนิดกระดาษไม่มีวันเป็นคำตอบถูกของ feature นี้)</item>
/// <item><b>ไม่แปลง</b>ชนิดกระดาษ → ชนิดปลายทาง: ไม่มีทางแปลงทางเดียว (ใบกำกับฝั่งซื้ออาจเป็น Expense/PurchaseInvoice/PaymentVoucher
/// แล้วแต่ผู้ใช้) ⇒ แต่งคำตอบ = เดา (F2 #3)</item>
/// <item>แถวหลัง cutoff ไม่ถูกแตะ (เทสต์ทิศตรงข้าม <c>AiFeedbackTrainingExclusionTests</c>)</item>
/// </list>
/// <para>SQL migration (<see cref="DocTypePromptV1BackfillSql"/>) สร้างจากค่าคงที่ชุดเดียวกับ <see cref="IsLegacyDocTypeTeacherOnlyRow"/> —
/// Db test <c>W9HygieneBackfillDbTests</c> ล็อกว่าสองฝั่งตัดสินเหมือนกัน</para>
/// </summary>
public static class AiFeedbackTrainingExclusion
{
    /// <summary>เวลา commit ของทีม H (UTC) — แถวที่ <c>CreatedAt</c> ก่อนเวลานี้คือ prompt รุ่นเก่า</summary>
    public static readonly DateTime DocTypePromptV2CutoffUtc = new(2026, 10, 22, 15, 54, 58, DateTimeKind.Utc);

    /// <summary>รหัสเหตุผลที่ประทับใน <c>ExcludedFromTrainingReason</c></summary>
    public const string ReasonDocTypePromptV1 = "DocTypeClassification:prompt-v1-paper-type";

    /// <summary>แถว DocumentTypeClassification รุ่นเก่าที่มีแค่คำตอบครู — ต้องตัดออกจากการสอน</summary>
    public static bool IsLegacyDocTypeTeacherOnlyRow(string featureKey, DateTime createdAtUtc, DateTime? userChosenAt)
        => string.Equals(featureKey, nameof(AiFeatureKey.DocumentTypeClassification), StringComparison.Ordinal)
           && createdAtUtc < DocTypePromptV2CutoffUtc
           && userChosenAt == null;

    /// <summary>คำสั่ง migration ครั้งเดียว (idempotent — รอบสองไม่มีแถวเข้าเงื่อนไข) · ไม่ลบ ไม่แก้ AiPrimaryAnswer ·
    /// คอลัมน์ <c>CreatedAt</c> เป็น timestamp without time zone เก็บ UTC (เหมือนทุกตาราง) จึงเทียบกับ literal UTC ตรง ๆ</summary>
    public static string DocTypePromptV1BackfillSql()
        => $"""UPDATE "AiSuggestionFeedbacks" SET "ExcludedFromTraining" = true, "ExcludedFromTrainingReason" = '{ReasonDocTypePromptV1}' WHERE "FeatureKey" = '{nameof(AiFeatureKey.DocumentTypeClassification)}' AND "CreatedAt" < TIMESTAMP '{DocTypePromptV2CutoffUtc:yyyy-MM-dd HH:mm:ss}' AND "UserChosenAt" IS NULL AND "ExcludedFromTraining" = false;""";
}
