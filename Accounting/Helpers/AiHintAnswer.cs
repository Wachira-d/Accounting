namespace Accounting.Helpers;

/// <summary>
/// **"ชั้นเรียนรู้ตอบมาจริงไหม" — ตัวคัดกรองคำตอบก่อนส่งให้หน้าจอ** (pure ไม่มี I/O)
///
/// <para>═══ ที่มา (ผลตรวจ D1-11 รอบ 181) ═══ <c>DocumentAiAugmenter</c> เคยคืน
/// <c>"Acknowledge"</c> พร้อมความมั่นใจที่<b>แต่งขึ้น</b> เมื่อ<b>ไม่มีใครตอบเลย</b>
/// (provider ปิด/ล่ม/ตอบไม่เข้า schema และนักเรียนยังไม่มีคลัง) แล้ว
/// <c>DocumentService</c> เติม <c>?? "Acknowledge"</c> ทับอีกชั้น ⇒ หน้าจอขึ้นป้าย
/// เขียว "🤖 AI: Acknowledge" ⇒ ผู้ใช้กด "ยอมรับและอนุมัติต่อ" ⇒ ระบบบันทึก
/// <c>acceptedAi=true</c> ⇒ คลังฝึกเต็มไปด้วยแถว "AI เห็นด้วยกับการปล่อยผ่าน"
/// ที่ AI <b>ไม่เคยพูด</b> ⇒ นักเรียนเรียนจากคำพูดของตัวเอง (self-confirm loop)</para>
///
/// <para>═══ กติกา ═══ "ไม่มีคำตอบ" ต้องเดินทางถึงหน้าจอในสภาพ<b>ว่าง</b>
/// (ป้ายไม่ขึ้น · ไม่มี <c>FeedbackId</c> ให้บันทึก) ไม่ใช่ถูกแปลงเป็นคำแนะนำ.
/// ส่วน <c>"Acknowledge"</c> ที่<b>โมเดลตอบเองจริง ๆ</b> ยังเป็นคำตอบที่ใช้ได้ตามปกติ
/// — สองอย่างนี้ต้องแยกจากกันให้ขาด (DECISION_DOCTRINE §1: "ไม่รู้" ต้องเป็นค่าที่เห็นได้)</para>
///
/// <para>⚠️ DTO ที่ส่งออกให้หน้าเว็บ (<c>ApprovalWarningAiHintDto.Primary</c>) เป็น
/// <c>string</c> ที่ไม่รับ null จึงใช้ <b>สตริงว่าง</b> เป็นตัวแทนของ "ไม่มีคำตอบ"
/// — ฝั่งหน้าเว็บเช็ค falsy ตัวเดียวได้ทั้ง <c>null</c>/<c>""</c>/ไม่มีฟิลด์</para>
/// </summary>
public static class AiHintAnswer
{
    private static bool HasAnswer(string? answer) => !string.IsNullOrWhiteSpace(answer);

    /// <summary>คำตอบที่ส่งออกไปหน้าจอ — ว่าง = "ไม่มีใครตอบ" (ห้ามแทนด้วยค่าที่แต่งขึ้น)</summary>
    public static string ForTransport(string? answer)
        => HasAnswer(answer) ? answer!.Trim() : string.Empty;

    /// <summary>ความมั่นใจที่ส่งออกไป — ไม่มีคำตอบ = 0 (ไม่ใช่ 0.5 ที่แต่งขึ้น)</summary>
    public static decimal ConfidenceFor(string? answer, decimal? confidence)
        => HasAnswer(answer) ? confidence ?? 0m : 0m;

    /// <summary>รหัส feedback ที่ให้หน้าจอบันทึกผลกลับ — ไม่มีคำตอบ = <c>null</c>
    /// เพื่อให้ "ไม่มีอะไรให้ยอมรับ" กลายเป็น "ไม่มีอะไรให้บันทึก" โดยโครงสร้าง
    /// (กันแถว <c>acceptedAi=true</c> ที่เทียบกับคำตอบที่ไม่มีอยู่จริง)</summary>
    public static Guid? FeedbackIdFor(string? answer, Guid? feedbackId)
        => HasAnswer(answer) ? feedbackId : null;
}
