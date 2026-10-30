using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ใบค้างหนึ่งใบที่ dunning พิจารณา</summary>
/// <param name="Excluded">ไม่ทวง/ไม่นับระงับ — มีรายการแจ้งชำระพร้อมสลิปรอตรวจ · ใบต่ออายุของแพ็กเกจที่ผู้เช่ายกเลิกไว้แล้ว</param>
public sealed record DunningInvoice(Guid DocumentId, string Number, DateTime? DueDateUtc, DateTime IssuedAtUtc, decimal Balance, bool Excluded);

public enum DunningAction
{
    None = 0,
    /// <summary>ระงับจริง (Subscription → Suspended) — เฉพาะโหมดบังคับแพ็กเกจ = Enforce + เปิดระงับอัตโนมัติ</summary>
    Suspend = 1,
    /// <summary>"จะระงับ" — โหมดเงา/ปิดบังคับ: บันทึกให้แอดมินเห็น ไม่แตะสถานะ</summary>
    WouldSuspend = 2,
    /// <summary>ชำระแล้ว/ไม่มีใบเกินระยะผ่อนผัน ⇒ คืนสถานะที่ระบบระงับเอง</summary>
    Restore = 3,
}

/// <summary>แจ้งเตือนหนึ่งฉบับ — <paramref name="MessageKey"/> คงที่ต่อ (ใบ · ชนิด · วัน) ⇒ ส่งซ้ำไม่ได้แม้รันหลายเครื่อง/หลายรอบ</summary>
public sealed record DunningReminder(Guid DocumentId, string Kind, int Days, string MessageKey);

public sealed record DunningDecision(
    IReadOnlyList<DunningReminder> Reminders, DunningAction Action, decimal OverdueAmount, DateTime? OldestOverdueDueUtc,
    int MaxDaysPastDue, string Reason);

/// <summary>
/// รอบ 219 เฟส 2 ขั้น 2 (คำตัดสินข้อ 180 · G-7/G-8) — <b>ตัวตัดสิน dunning ตัวเดียว (pure)</b>: ใบไหนต้องแจ้งอะไรวันนี้ · ต้องระงับ/คืนสถานะไหม ·
/// ค่าตั้งจาก <see cref="PlatformBillingPolicy"/> · เคารพโหมดบังคับแพ็กเกจ (Enforce = ระงับจริง · อื่น = "จะระงับ" บันทึกอย่างเดียว)
/// <para>แจ้งเตือนแบบ "ข้ามเกณฑ์ล่าสุด" ไม่ใช่ "วันตรงเป๊ะ": job หยุดไปหลายวันแล้วกลับมา ⇒ ส่งเฉพาะเกณฑ์ล่าสุดที่ข้ามแล้ว 1 ฉบับ (ไม่ส่งย้อนทุกเกณฑ์ · ไม่หลุด)</para>
/// </summary>
public static class PlatformDunningPolicy
{
    /// <summary>ใบที่ออกเกินกี่วันแล้วไม่ส่ง "ใบแจ้งหนี้ใหม่" (กันส่งย้อนใบเก่าทั้งหมดในวัน deploy)</summary>
    public const int IssuedNoticeMaxAgeDays = 30;

    public const string KindIssued = "issued", KindBefore = "before", KindDue = "due", KindAfter = "after";

    public static string Key(Guid documentId, string kind, int days) =>
        kind is KindIssued or KindDue ? $"inv:{documentId:N}:{kind}" : $"inv:{documentId:N}:{kind}:{days}";

    public static string CompanyKey(Guid companyId, string kind, DateTime nowUtc) =>
        $"co:{companyId:N}:{kind}:{PlatformInvoiceRules.ThaiDate(nowUtc):yyyyMMdd}";

    /// <summary>ชนิดสถานะที่ระบบระงับเพราะค้างชำระได้ (ทดลองใช้/ยกเลิก/หมดอายุ/ระงับด้วยเหตุอื่น = ไม่แตะ)</summary>
    private static bool Suspendable(SubscriptionStatus s) => s is SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    internal static IReadOnlyList<DunningReminder> RemindersFor(DunningInvoice inv, DateTime nowUtc, PlatformBillingPolicy p)
    {
        var list = new List<DunningReminder>();
        if (inv.Excluded || inv.Balance <= PlatformInvoiceRules.Epsilon) return list;
        var ageDays = (PlatformInvoiceRules.ThaiDate(nowUtc) - PlatformInvoiceRules.ThaiDate(inv.IssuedAtUtc)).TotalDays;
        if (ageDays <= IssuedNoticeMaxAgeDays)
            list.Add(new DunningReminder(inv.DocumentId, KindIssued, 0, Key(inv.DocumentId, KindIssued, 0)));
        if (PlatformInvoiceRules.DaysPastDue(inv.DueDateUtc, nowUtc) is not int dpd) return list;
        if (dpd < 0)
        {
            // ใบเพิ่งออกวันนี้ ⇒ "ใบแจ้งหนี้ใหม่" ก็คือการแจ้งแล้ว ไม่ส่งเตือนก่อนครบกำหนดซ้อนในวันเดียวกัน
            if (ageDays < 1) return list;
            var toDue = -dpd;
            var crossed = p.ReminderDaysBeforeDue.Where(b => toDue <= b).DefaultIfEmpty(-1).Min();
            if (crossed > 0) list.Add(new DunningReminder(inv.DocumentId, KindBefore, crossed, Key(inv.DocumentId, KindBefore, crossed)));
        }
        else if (dpd == 0)
            list.Add(new DunningReminder(inv.DocumentId, KindDue, 0, Key(inv.DocumentId, KindDue, 0)));
        else
        {
            var crossed = p.ReminderDaysAfterDue.Where(a => dpd >= a).DefaultIfEmpty(-1).Max();
            if (crossed > 0) list.Add(new DunningReminder(inv.DocumentId, KindAfter, crossed, Key(inv.DocumentId, KindAfter, crossed)));
        }
        return list;
    }

    public static DunningDecision Decide(DateTime nowUtc, IReadOnlyList<DunningInvoice> open, PlatformBillingPolicy p,
        SubscriptionEnforcementMode mode, SubscriptionStatus status, bool suspendedByDunning, DateTime? exemptUntilUtc)
    {
        var reminders = open.SelectMany(i => RemindersFor(i, nowUtc, p)).ToList();
        var counted = open.Where(i => !i.Excluded && i.Balance > PlatformInvoiceRules.Epsilon)
            .Select(i => (Inv: i, Dpd: PlatformInvoiceRules.DaysPastDue(i.DueDateUtc, nowUtc) ?? int.MinValue))
            .ToList();
        var overdue = counted.Where(x => x.Dpd > 0).ToList();
        var beyondGrace = counted.Where(x => x.Dpd > p.SuspendGraceDays).ToList();
        var amount = overdue.Sum(x => x.Inv.Balance);
        var oldest = overdue.Count == 0 ? (DateTime?)null : overdue.Min(x => x.Inv.DueDateUtc);
        var maxDpd = overdue.Count == 0 ? 0 : overdue.Max(x => x.Dpd);

        if (beyondGrace.Count == 0)
            return new DunningDecision(reminders, suspendedByDunning ? DunningAction.Restore : DunningAction.None, amount, oldest, maxDpd,
                suspendedByDunning ? "ไม่มีใบค้างเกินระยะผ่อนผันแล้ว — คืนสถานะ" : "ไม่มีใบค้างเกินระยะผ่อนผัน");

        var why = $"ค้างชำระเกินกำหนด {maxDpd} วัน (ผ่อนผัน {p.SuspendGraceDays} วัน) {beyondGrace.Count} ใบ";
        if (suspendedByDunning) return new DunningDecision(reminders, DunningAction.None, amount, oldest, maxDpd, why + " — ระงับอยู่แล้ว");
        if (!Suspendable(status)) return new DunningDecision(reminders, DunningAction.None, amount, oldest, maxDpd, why + $" — สถานะ {status} ไม่ถูกระงับอัตโนมัติ");
        if (exemptUntilUtc is DateTime ex && ex > nowUtc)
            return new DunningDecision(reminders, DunningAction.None, amount, oldest, maxDpd, why + $" — ผู้ดูแลระบบพักการระงับถึง {PlatformInvoiceRules.ThaiDate(ex):dd/MM/yyyy}");
        if (!p.AutoSuspendEnabled) return new DunningDecision(reminders, DunningAction.None, amount, oldest, maxDpd, why + " — ปิดระงับอัตโนมัติ");
        if (mode != SubscriptionEnforcementMode.Enforce)
            return new DunningDecision(reminders, DunningAction.WouldSuspend, amount, oldest, maxDpd, why + $" — โหมด {mode}: บันทึกว่าจะระงับ (ไม่ระงับจริง)");
        return new DunningDecision(reminders, DunningAction.Suspend, amount, oldest, maxDpd, why);
    }

    /// <summary>สถานะที่คืนให้หลังพ้นการระงับเพราะค้างชำระ — สถานะเดิม แต่ถ้าเดิม Active และรอบหมดไปแล้วระหว่างถูกระงับ ⇒ PastDue (ไม่แต่งว่าจ่ายแล้ว)</summary>
    public static SubscriptionStatus RestoreStatus(SubscriptionStatus? before, DateTime endDateUtc, DateTime nowUtc, bool permanentFree)
    {
        var s = before is SubscriptionStatus b && Suspendable(b) ? b : SubscriptionStatus.Active;
        return s == SubscriptionStatus.Active && !permanentFree && endDateUtc < nowUtc ? SubscriptionStatus.PastDue : s;
    }
}
