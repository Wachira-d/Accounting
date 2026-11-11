namespace Accounting.Helpers;

/// <summary>รอบ 250 (WP-02 §2.4 ข้อ 1 · คำตัดสิน §2 #8 · B F-1): ผลของ "เข้าคิวอีเมลเอกสารของรายการประจำ" — เดิม <c>OnRecurringDocumentCreatedAsync</c>
/// กลืนทุกความผิดพลาดเป็น <c>LogWarning</c> (คืน <c>Task</c> เปล่า) ⇒ ธง <c>RecurringRunFacts.EmailEnqueueFailed</c> ไม่มีทางถูกตั้ง (dead code) ·
/// ตอนนี้คืนผล 3 แบบ ผู้เรียกตั้งธงจากผล (ล้มดัง 3 ที่ — F2 ข้อ 7)</summary>
public enum EmailEnqueueKind
{
    /// <summary>เข้าคิวแล้ว (อย่างน้อย 1 ฉบับ)</summary>
    Enqueued,
    /// <summary>ไม่เข้าคิวโดยตั้งใจ (ใบยังเป็นร่าง · ไม่ได้ตั้งส่ง · ไม่มีอีเมล · ส่งตามกฎ "เมื่ออนุมัติเอกสาร" แล้ว) — ไม่ใช่ความผิดพลาด</summary>
    Skipped,
    /// <summary>เข้าคิวไม่สำเร็จ (ฐานข้อมูล/เทมเพลตล้ม) — ผู้ใช้ต้องส่งเองจากหน้าเอกสาร</summary>
    Failed,
}

/// <summary>ผล + เหตุ (ภาษาไทย · ว่างได้เมื่อ Enqueued)</summary>
public sealed record EmailEnqueueOutcome(EmailEnqueueKind Kind, string? Reason)
{
    public static EmailEnqueueOutcome Enqueued() => new(EmailEnqueueKind.Enqueued, null);
    public static EmailEnqueueOutcome Skipped(string reason) => new(EmailEnqueueKind.Skipped, reason);
    public static EmailEnqueueOutcome Failed(string reason) => new(EmailEnqueueKind.Failed, reason);

    /// <summary>รอบ 250 WP-08: ไม่ส่งเพราะค่าตั้งของผู้ติดต่อ (ยกเลิกรับเรื่อง "เอกสาร" ทุกปลายทาง · "ไม่ส่งอัตโนมัติ") — ตั้งใจ แต่ผู้ใช้ต้องเห็นเหตุ
    /// (<see cref="TellUser"/> ⇒ <see cref="Apply"/> ตั้ง <c>RecurringRunFacts.EmailNotSentReason</c>) · ต่างจาก Skipped ทั่วไปที่ไม่แจ้ง</summary>
    public static EmailEnqueueOutcome NotSent(string reason) => new(EmailEnqueueKind.Skipped, reason) { TellUser = true };

    /// <summary>ผลนี้ต้องบอกผู้ใช้ที่หน้ารายการประจำ (เฉพาะ <see cref="NotSent"/>)</summary>
    public bool TellUser { get; init; }

    /// <summary>เหตุ "ข้าม" เมื่อมีกฎ <c>DocumentApproved</c> ครอบชนิดเอกสารนี้ (คำตัดสิน §2 #10 · B F-4): การอนุมัติอัตโนมัติของรายการประจำเดินเส้น
    /// <c>ApproveDocumentAsync → OnDocumentApprovedAsync</c> ซึ่งเข้าคิวตามกฎนั้นไปแล้ว ⇒ กฎ "เมื่อรายการประจำสร้างเอกสาร"/ส่งอัตโนมัติของรายการไม่เข้าคิวซ้ำ (ทิศเดียว ·
    /// ไม่แตะฝั่งกฎ DocumentApproved ที่ใบคีย์มือพึ่งอยู่)</summary>
    public const string CoveredByApprovedRule =
        "ส่งตามกฎ \"เมื่ออนุมัติเอกสาร\" ในหน้าตารางส่งอีเมลแล้ว — ไม่เข้าคิวซ้ำจากรายการประจำ (กันลูกค้าได้อีเมลสองฉบับ)";

    /// <summary>ตัดสินว่าต้องข้ามเพราะกฎ DocumentApproved ครอบแล้วไหม — <paramref name="approvedRuleCoversType"/> = มีกฎ DocumentApproved ที่ใช้งานอยู่และชนิดตรง/ว่าง ·
    /// <paramref name="documentApproved"/> = ใบผ่านการอนุมัติแล้ว (ไม่ใช่ร่าง/รออนุมัติ/ปฏิเสธ) ·
    /// <paramref name="approvedRowQueued"/> = มีแถวคิวของกฎนั้นสำหรับใบนี้จริง (ฝ่ายค้าน WP-02 ข้อ 6 — ตัวเข้าคิวฝั่งอนุมัติกลืนความผิดพลาด ⇒ "มีกฎ" อย่างเดียวไม่พอ)</summary>
    /// <summary>รอบ 258 (A-M1 ข): เตือน "ไม่ได้ส่งอีเมล เพราะผู้ติดต่อไม่มีอีเมล" ไหม — ไม่มีอีเมลที่ส่งได้ <b>และ</b>ไม่ได้ออกทาง LINE
    /// (ลูกค้าได้เอกสารทาง LINE แล้ว = ไม่ใช่เรื่องที่ต้องตามแก้)</summary>
    public static bool WarnNoContactEmail(bool hasDeliverableEmail, bool lineQueued) => !hasDeliverableEmail && !lineQueued;

    /// <summary>รอบ 258 (A-M1 ก): กฎ "เมื่ออนุมัติเอกสาร" ส่งใบนี้แล้วไหม — แถวอีเมล<b>หรือ</b>แถว LINE ของกุญแจเดียวกัน (กฎที่ติ๊ก LINE อย่างเดียวไม่มีแถวอีเมล
    /// ⇒ เดิมไม่ถูกนับ แล้วกฎ "รายการประจำสร้างเอกสาร" ส่งซ้ำอีกใบ)</summary>
    public static bool ApprovedRuleDelivered(bool emailRowQueued, bool lineRowQueued) => emailRowQueued || lineRowQueued;

    public static bool SkipForApprovedRule(bool approvedRuleCoversType, bool documentApproved, bool approvedRowQueued)
        => approvedRuleCoversType && documentApproved && approvedRowQueued;

    /// <summary>ตั้งธง/ข้อความเรื่องที่ต้องทำต่อจากผล (ผู้เรียก: <c>RecurringTransactionService</c>) — Failed ⇒ <c>EmailEnqueueFailed</c> + ข้อความ (เฉพาะเมื่อรายการตั้งส่งอีเมล
    /// หรือมีกฎส่ง — ผลมาจากการพยายามส่งจริง) · Skipped/Enqueued ⇒ ไม่ตั้งธง (ทิศตรงข้าม)</summary>
    public static (RecurringRunFacts Facts, string? Notice) Apply(RecurringRunFacts facts, EmailEnqueueOutcome outcome, string? documentNumber)
    {
        if (outcome.Kind == EmailEnqueueKind.Skipped && outcome.TellUser && !string.IsNullOrWhiteSpace(outcome.Reason))
            return (facts with { EmailNotSentReason = outcome.Reason }, $"ไม่ได้ส่งอีเมลเอกสาร {documentNumber}: {outcome.Reason}");
        if (outcome.Kind != EmailEnqueueKind.Failed) return (facts, null);
        var notice = $"เข้าคิวส่งอีเมลเอกสาร {documentNumber} ไม่สำเร็จ"
                     + (string.IsNullOrWhiteSpace(outcome.Reason) ? "" : $" ({outcome.Reason})") + " — ส่งเองจากหน้าเอกสาร";
        return (facts with { EmailEnqueueFailed = true }, notice);
    }
}
