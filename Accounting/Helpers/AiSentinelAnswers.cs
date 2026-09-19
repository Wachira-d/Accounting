namespace Accounting.Helpers;

/// <summary>
/// **ค่า "คำตอบ" ที่ไม่ใช่คำตอบ — ตัวตั้งเดียวของทั้งระบบ** (pure ไม่มี I/O)
///
/// ═══ บั๊กจริงที่เป็นที่มา (รอบ 181 · D7-1) ═══
/// ปุ่ม "ใช้ของเดิม" ใน <c>wwwroot/js/ai-suggestion.js</c> ยิง
/// <c>chosenAnswer = "__USER_KEPT_EXISTING__"</c> ไปที่ <c>/ai-feedback/record</c> เพื่อบอกว่า
/// **ผู้ใช้ไม่รับคำตอบของ AI** แต่ฝั่งเซิร์ฟเวอร์ไม่มีใครรู้จักค่านี้เลย
/// (<c>grep '__USER_KEPT_EXISTING__' --include=*.cs</c> = 0 จุด) ⇒ มันไหลเข้าไปเป็น
/// "คำตอบที่ผู้ใช้ยืนยัน" ตลอดสาย:
/// <list type="number">
/// <item><c>AiFeedbackRecorder.LearnInlineAsync</c> → <c>AiSuggestionMemory.LearnedAnswer
/// = "__USER_KEPT_EXISTING__"</c> (คลังทันที tier-0)</item>
/// <item><c>AiFeedbackTrainingJob.TrainGlAccountAsync</c> → <c>INSERT OcrCategoryMapping
/// (AccountCode = "__USER_KEPT_EXISTING__")</c> — <b>รหัสผังบัญชีปลอมในตารางผังบัญชี</b></item>
/// <item><c>GlAccountDistillationModel</c> อ่านตารางนั้นแล้วนับเป็นรหัสบัญชีที่ผู้ใช้ยืนยัน
/// ⇒ เสนอให้ผู้ใช้คนถัดไปลงบัญชีด้วยรหัสที่ไม่มีอยู่จริง</item>
/// </list>
///
/// ═══ ⚠️ <c>__NEW__</c> <b>ไม่ใช่</b> sentinel ═══
/// <c>__NEW__</c> (ใน <c>VendorCanonPrompt</c> · <c>BankAndAnalyticsPrompts</c> ·
/// <c>OcrAiAugmenter</c> · <c>BankAiAugmenter</c>) คือ <b>คำตอบจริง</b> ที่แปลว่า
/// "ไม่มีตัวไหนตรง — ให้สร้างรายการใหม่" ⇒ ต้องเรียนรู้ตามปกติ. นี่คือเหตุผลที่ไฟล์นี้ใช้
/// <b>ลิสต์ปิด</b> ไม่ใช่แพตเทิร์น <c>^__[A-Z_]+__$</c>: แพตเทิร์นจะกิน <c>__NEW__</c> ไปด้วย
/// แล้วปิดการเรียนรู้ของ VendorCanonicalization/BankStatementMatch ทั้งเส้นโดยไม่มีอะไรฟ้อง
/// ("ด่านที่ฟ้องผิด = ด่านที่พัง" — หลักการข้อ 6)
///
/// <para>วิธีเพิ่มค่าใหม่: หน้าเว็บที่จะส่ง sentinel ตัวใหม่ <b>ต้อง</b> มาประกาศที่นี่ก่อน
/// มิฉะนั้นค่านั้นจะกลายเป็นคำตอบที่ระบบเรียนไปจริง ๆ</para>
/// </summary>
public static class AiSentinelAnswers
{
    /// <summary>ผู้ใช้กด "ใช้ของเดิม" — เรารู้ว่า<b>เขาไม่รับคำตอบของ AI</b>
    /// แต่<b>ไม่รู้</b>ว่าค่าที่เขาเก็บไว้คืออะไร (หน้าเว็บไม่ได้ส่งมา)</summary>
    public const string UserKeptExisting = "__USER_KEPT_EXISTING__";

    private static readonly HashSet<string> Known =
        new(StringComparer.Ordinal) { UserKeptExisting };

    /// <summary>ค่านี้เป็น "ธงของ UI" ไม่ใช่คำตอบที่เรียนได้หรือไม่
    ///
    /// <para>เทียบแบบ <b>ตรงตัวพิมพ์</b> หลัง trim — ค่าเหล่านี้เป็นค่าคงที่ที่โค้ดเราเอง
    /// ส่งมา ไม่ใช่สิ่งที่มนุษย์พิมพ์ ⇒ ไม่มี "การสะกดผิด" ให้ต้องเผื่อ (หลักการข้อ 3:
    /// ตัวเลข/ค่ารหัสล้วน ห้าม fuzzy)</para></summary>
    public static bool IsSentinel(string? answer)
        => !string.IsNullOrWhiteSpace(answer) && Known.Contains(answer.Trim());
}
