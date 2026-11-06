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

    /// <summary>เหตุ "ข้าม" เมื่อมีกฎ <c>DocumentApproved</c> ครอบชนิดเอกสารนี้ (คำตัดสิน §2 #10 · B F-4): การอนุมัติอัตโนมัติของรายการประจำเดินเส้น
    /// <c>ApproveDocumentAsync → OnDocumentApprovedAsync</c> ซึ่งเข้าคิวตามกฎนั้นไปแล้ว ⇒ กฎ "เมื่อรายการประจำสร้างเอกสาร"/ส่งอัตโนมัติของรายการไม่เข้าคิวซ้ำ (ทิศเดียว ·
    /// ไม่แตะฝั่งกฎ DocumentApproved ที่ใบคีย์มือพึ่งอยู่)</summary>
    public const string CoveredByApprovedRule =
        "ส่งตามกฎ \"เมื่ออนุมัติเอกสาร\" ในหน้าตารางส่งอีเมลแล้ว — ไม่เข้าคิวซ้ำจากรายการประจำ (กันลูกค้าได้อีเมลสองฉบับ)";

    /// <summary>ตัดสินว่าต้องข้ามเพราะกฎ DocumentApproved ครอบแล้วไหม — <paramref name="approvedRuleCoversType"/> = มีกฎ DocumentApproved ที่ใช้งานอยู่และชนิดตรง/ว่าง ·
    /// <paramref name="documentApproved"/> = ใบผ่านการอนุมัติแล้ว (ไม่ใช่ร่าง/รออนุมัติ/ปฏิเสธ)</summary>
    public static bool SkipForApprovedRule(bool approvedRuleCoversType, bool documentApproved)
        => approvedRuleCoversType && documentApproved;

    /// <summary>ตั้งธง/ข้อความเรื่องที่ต้องทำต่อจากผล (ผู้เรียก: <c>RecurringTransactionService</c>) — Failed ⇒ <c>EmailEnqueueFailed</c> + ข้อความ (เฉพาะเมื่อรายการตั้งส่งอีเมล
    /// หรือมีกฎส่ง — ผลมาจากการพยายามส่งจริง) · Skipped/Enqueued ⇒ ไม่ตั้งธง (ทิศตรงข้าม)</summary>
    public static (RecurringRunFacts Facts, string? Notice) Apply(RecurringRunFacts facts, EmailEnqueueOutcome outcome, string? documentNumber)
    {
        if (outcome.Kind != EmailEnqueueKind.Failed) return (facts, null);
        var notice = $"เข้าคิวส่งอีเมลเอกสาร {documentNumber} ไม่สำเร็จ"
                     + (string.IsNullOrWhiteSpace(outcome.Reason) ? "" : $" ({outcome.Reason})") + " — ส่งเองจากหน้าเอกสาร";
        return (facts with { EmailEnqueueFailed = true }, notice);
    }
}
