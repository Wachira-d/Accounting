using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ความพยายามตัดครั้งก่อนของใบหนึ่ง (ข้อเท็จจริงที่ตัวตัดสินใช้)</summary>
public sealed record AutoChargeAttemptFact(int AttemptNo, DateTime AttemptedAtUtc, string Outcome);

/// <summary>ผลตัดสิน — <c>AttemptNo</c> null = วันนี้ไม่ตัดใบนี้ (เหตุผลใน <c>Reason</c>)</summary>
public sealed record AutoChargeDecision(int? AttemptNo, string Reason);

/// <summary>
/// รอบ 230 (PLATFORM_OMISE_AUDIT §5.2) — <b>ตัวตัดสินเดียว (pure)</b> ว่าวันนี้ตัดบัตรอัตโนมัติของใบแจ้งหนี้ค่าบริการใบไหน ครั้งที่เท่าไร
/// <list type="bullet">
/// <item>ครั้งที่ 1 = วันครบกำหนด (หรือวันแรกที่งานเห็นหลังครบกำหนด) · ครั้งถัดไป = ตามวัน "เตือนหลังเกินกำหนด" ของนโยบายทวงหนี้เดิม
///   (<c>PlatformBillingSettings.ReminderDaysAfterDue</c> — ค่าตั้งเดียว ไม่มีตารางลองใหม่ชุดที่สอง) · ครบจำนวนครั้ง ⇒ หยุด (เหลือทวงหนี้/ระงับตามเดิม)</item>
/// <item>ใบที่ทวงหนี้ไม่นับ (<see cref="PlatformDunningPolicy.Excluded"/> — สลิปรอตรวจ · ยกเลิกสิ้นรอบ · ใบต่ออายุถูกแทนที่) ⇒ ไม่ตัด</item>
/// <item>มีความพยายามที่ยังไม่จบ (จองแล้ว/รอผู้ให้บริการ/รอยืนยันกับธนาคาร) ⇒ รอ (ไม่ตัดซ้อน) · ตัดสำเร็จแล้ว ⇒ ไม่ตัดอีก · วันละครั้งต่อใบ</item>
/// <item>สถานะแพ็กเกจ: ทดลองใช้ · ใช้งาน · ค้างชำระ · ระงับ<b>โดยระบบทวงหนี้</b> เท่านั้น — ยกเลิก/หมดอายุ/ระงับโดยแอดมิน ⇒ ไม่ตัด</item>
/// </list>
/// </summary>
public static class PlatformAutoChargePolicy
{
    public const string OutcomeClaimed = "Claimed", OutcomePending = "Pending", OutcomeRequiresAction = "RequiresAction",
        OutcomeSucceeded = "Succeeded", OutcomeFailed = "Failed",
        // ฝ่ายค้านรอบ 230 (nice): ผู้ให้บริการปฏิเสธเพราะต้องยืนยันตัวตนกับธนาคาร — จบแล้ว (ไม่ใช่รอผล) และลองใหม่อัตโนมัติไม่ช่วย ⇒ หยุดตัดใบนี้
        OutcomeActionRequired = "ActionRequired";

    /// <summary>ผลที่ยังไม่จบ — ห้ามเริ่มครั้งใหม่ทับ</summary>
    public static bool InFlight(string outcome) => outcome is OutcomeClaimed or OutcomePending or OutcomeRequiresAction;

    /// <summary>ป้ายไทยของผล (หน้าศูนย์บิล/แอดมินแสดงตามนี้ — ไม่มีตารางป้ายใน JS)</summary>
    public static string OutcomeLabel(string outcome) => outcome switch
    {
        OutcomeClaimed => "กำลังตัด",
        OutcomePending => "รอผลจากผู้ให้บริการ",
        OutcomeRequiresAction => "ธนาคารขอยืนยันตัวตน — ชำระด้วยตนเองที่ศูนย์บิล",
        OutcomeSucceeded => "ตัดสำเร็จ",
        OutcomeFailed => "ตัดไม่สำเร็จ",
        OutcomeActionRequired => "ธนาคารขอยืนยันตัวตน — ตัดอัตโนมัติไม่ได้ ชำระด้วยตนเองที่ศูนย์บิล",
        _ => outcome,
    };

    /// <summary>วันลองใหม่ (เกินกำหนดกี่วัน) — จากวันเตือนหลังเกินกำหนดของนโยบายทวงหนี้ (บวก · ไม่ซ้ำ · เรียง) · ว่าง ⇒ ไม่ลองใหม่</summary>
    public static IReadOnlyList<int> RetryDays(IReadOnlyList<int> reminderDaysAfterDue)
        => reminderDaysAfterDue.Where(d => d > 0).Distinct().OrderBy(d => d).ToList();

    public static int MaxAttempts(IReadOnlyList<int> retryDays) => 1 + retryDays.Count;

    /// <summary>สถานะแพ็กเกจที่ตัดได้ — ยกเลิก/หมดอายุ/ระงับโดยเหตุอื่น ⇒ ไม่ตัด</summary>
    /// <summary>ฝ่ายค้าน S7: "ระงับโดยระบบทวงหนี้" ต้องเป็นทั้งธงของงานทวงหนี้ <b>และ</b>การเปลี่ยนเป็นระงับครั้งล่าสุดในประวัติเป็นของงานทวงหนี้ —
    /// แอดมินระงับทับ (หรือประวัติไม่ชัด) ⇒ ไม่ถือว่าเป็นของทวงหนี้ (ไม่ตัด)</summary>
    public const string DunningSuspendAction = "PlatformDunningSuspended";

    public static bool SuspendedByDunning(bool dunningStateFlag, string? lastSuspendAction)
        => dunningStateFlag && lastSuspendAction == DunningSuspendAction;

    /// <summary>รอบ 231 (LICENSE_USAGE_OVERAGE_AUDIT §B.5 · คำตัดสินข้อ 231-4): ใบค่าใช้งาน/ส่วนเกิน (<c>USAGE-</c>) เข้าคิวตัดบัตรอัตโนมัติเฉพาะเมื่อแอดมินเปิด
    /// <c>PlatformBillingSettings.OverageAutoChargeSavedCard</c> — ใบชนิดอื่น (ต่ออายุ/แอดมินออกเอง) ไม่ขึ้นกับค่าตั้งนี้ · ผู้อ่านตัวเดียวของค่าตั้ง (F2 ข้อ 2)</summary>
    public static bool AutoChargeAllowedForKind(PlatformInvoiceKind kind, bool overageAutoChargeSavedCard)
        => kind != PlatformInvoiceKind.Usage || overageAutoChargeSavedCard;

    public static bool SubscriptionChargeable(SubscriptionStatus status, bool suspendedByDunning)
        => status is SubscriptionStatus.Trial or SubscriptionStatus.Active or SubscriptionStatus.PastDue
           || (status == SubscriptionStatus.Suspended && suspendedByDunning);

    public static AutoChargeDecision Decide(DateTime? dueDateUtc, DateTime nowUtc, decimal balanceDue, bool excluded,
        IReadOnlyList<AutoChargeAttemptFact> attempts, IReadOnlyList<int> retryDays)
    {
        if (balanceDue <= PlatformInvoiceRules.Epsilon) return new(null, "ไม่มียอดค้าง");
        if (excluded) return new(null, "ใบนี้ไม่ตัดอัตโนมัติ (มีการแจ้งชำระรอตรวจ/ยกเลิกสิ้นรอบ/ใบต่ออายุถูกแทนที่)");
        if (PlatformInvoiceRules.DaysPastDue(dueDateUtc, nowUtc) is not int dpd) return new(null, "ใบไม่มีวันครบกำหนด");
        if (dpd < 0) return new(null, "ยังไม่ถึงวันครบกำหนด");
        if (attempts.Any(a => a.Outcome == OutcomeSucceeded)) return new(null, "ตัดสำเร็จแล้ว");
        if (attempts.Any(a => a.Outcome == OutcomeActionRequired)) return new(null, "ธนาคารขอยืนยันตัวตน — ใบนี้ต้องชำระด้วยตนเองที่ศูนย์บิล");
        if (attempts.Any(a => InFlight(a.Outcome))) return new(null, "มีการตัดที่ยังรอผล");
        var max = MaxAttempts(retryDays);
        var done = attempts.Count == 0 ? 0 : attempts.Max(a => a.AttemptNo);
        if (done >= max) return new(null, $"ตัดไม่สำเร็จครบ {max} ครั้งแล้ว — เหลือการแจ้งเตือน/ทวงหนี้ตามเดิม");
        var next = done + 1;
        var threshold = next == 1 ? 0 : retryDays[next - 2];
        if (dpd < threshold) return new(null, $"รอตัดครั้งที่ {next} เมื่อเกินกำหนด {threshold} วัน");
        var today = PlatformInvoiceRules.ThaiDate(nowUtc);
        if (attempts.Any(a => PlatformInvoiceRules.ThaiDate(a.AttemptedAtUtc) == today)) return new(null, "ตัดใบนี้ไปแล้ววันนี้");
        return new(next, $"ตัดครั้งที่ {next}/{max}");
    }

    /// <summary>ผลของความพยายามจากสถานะรายการชำระ (intent) — ตัวแปลเดียว (pure)</summary>
    public static string OutcomeOf(PaymentIntentStatus status, bool hasAuthorizeUrl, string? failureCode = null) => status switch
    {
        PaymentIntentStatus.Succeeded or PaymentIntentStatus.PartiallyRefunded or PaymentIntentStatus.Refunded => OutcomeSucceeded,
        PaymentIntentStatus.Failed when failureCode == SavedCardChargeOutcome.ActionRequiredCode => OutcomeActionRequired,
        PaymentIntentStatus.Failed or PaymentIntentStatus.Expired => OutcomeFailed,
        _ => hasAuthorizeUrl ? OutcomeRequiresAction : OutcomePending,
    };

    /// <summary>คีย์ข้อความถึงผู้เช่า (unique ต่อใบ · ครั้งที่ · ชนิด ⇒ ส่งครั้งเดียวแม้หลายเครื่อง)</summary>
    public static string NoticeKey(Guid documentId, int attemptNo, string kind) => $"autocharge:{documentId:N}:{attemptNo}:{kind}";
}
