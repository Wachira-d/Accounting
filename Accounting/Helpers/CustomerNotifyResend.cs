using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>เหตุที่ข้อความถึงลูกค้าถูกระงับ (เก็บที่ <c>CustomerNotifyLogs.SuppressReason</c>) — รอบ 213 (คำตัดสินข้อ 172) ·
/// แยกเหตุได้ ⇒ หน้า "ข้อความที่ยังไม่ได้ส่ง" รู้ว่าแถวไหนค้างเพราะโควตา (ส่งซ้ำได้) กับแถวที่ร้านปิดเอง (ส่งซ้ำไม่ได้)</summary>
public static class NotifySuppressReason
{
    /// <summary>เหลือแต่ช่องกลางของ NextAcc และโควตาเดือนนี้เต็ม (<see cref="NotifyQuotaPolicy.FullReason"/>)</summary>
    public const string QuotaFull = "quota-full";
    /// <summary>ร้านปิดการแจ้งเตือนประเภทนี้/ช่องที่ลูกค้ามี (<see cref="CustomerNotifyPolicy.SuppressedByPolicyReason"/>)</summary>
    public const string EventOff = "event-off";
    /// <summary>ร้านตั้ง "ผู้ส่งอีเมลของร้านเท่านั้น" แต่ยังไม่ได้ตั้ง (<see cref="CustomerNotifyPolicy.SuppressedTenantOnlyReason"/>)</summary>
    public const string TenantOnly = "tenant-only";

    /// <summary>ข้อความที่ <see cref="CustomerNotifyPolicy.SuppressReason"/> คืน → รหัส (ตัวเดียว · ใช้ทั้งตอนส่งและ migration ย้อนหลัง)</summary>
    public static string OfPolicyText(string? text)
        => text == CustomerNotifyPolicy.SuppressedTenantOnlyReason ? TenantOnly : EventOff;

    public static string Label(string? code) => code switch
    {
        QuotaFull => "โควตาข้อความเดือนนี้เต็ม",
        EventOff => "ร้านปิดการแจ้งเตือนประเภทนี้ไว้",
        TenantOnly => "ตั้งให้ส่งผ่านผู้ส่งอีเมลของร้านเท่านั้น แต่ยังไม่ได้ตั้งผู้ส่ง",
        _ => "ไม่ได้ส่ง",
    };
}

/// <summary>สถานะปัจจุบันของรายการปลายทาง (การจอง/คำสั่งซื้อ) ที่ใช้ตัดสินว่าข้อความเก่ายังสมเหตุสมผลไหม ·
/// <c>BookingStartLocal</c> = วัน+เวลาเริ่มนัดตามเวลาไทย</summary>
public sealed record CustomerNotifyResendTarget(bool Exists, Guid? SiteId, string? Label,
    BookingStatus? BookingStatus = null, DateTime? BookingStartLocal = null, SiteOrderStatus? OrderStatus = null)
{
    public static readonly CustomerNotifyResendTarget Missing = new(false, null, null);
}

/// <summary>ข้อเท็จจริงของแถวบันทึก 1 แถว — <c>Superseded</c> = มีแถวที่ใหม่กว่าถึงรายการเดียวกันซึ่งแทนข้อความนี้ (<see cref="CustomerNotifyResend.Supersedes"/> ·
/// ไม่ว่าแถวนั้นจะส่งถึง/ค้าง/ล้ม — ฝ่ายค้านรอบ 213 S2) · <c>EventEnabledNow</c> = ค่าตั้งปัจจุบันยังเปิดอย่างน้อย 1 ช่อง (<see cref="CustomerNotifyPolicy.Resolve"/>) ·
/// <c>ClaimExpired</c> = แถว Claimed ที่ lease หมดแล้ว (ตัวส่งล้มกลางทาง — ฝ่ายค้าน S6) · <c>ResendCount</c> = ร้านเคยกดส่งซ้ำกี่ครั้ง (แถว Failed ที่ส่งซ้ำแล้วล้มยังลองใหม่ได้ — S5)</summary>
public sealed record CustomerNotifyResendInput(string EventKey, string Status, string? SuppressReason, DateTime CreatedAtUtc,
    bool EventEnabledNow, bool Superseded, CustomerNotifyResendTarget Target, bool ClaimExpired = false, int ResendCount = 0);

/// <summary>ผลตัดสิน — <c>Code</c> คงที่ (ใช้ในเทสต์/กรอง) · <c>Reason</c> ภาษาไทยถึงผู้ใช้ (หน้าเว็บแสดงตรง ๆ)</summary>
public sealed record CustomerNotifyResendVerdict(bool Resendable, string Code, string Reason);

/// <summary>
/// **ตัวตัดสิน "ส่งซ้ำข้อความที่ค้างเพราะโควตา" — ตัวเดียว** (รอบ 213 · คำตัดสินข้อ 172 · เจ้าของ: "ถ้าเติมโควต้าแล้ว สามารถส่งซ้ำทีหลังได้")
/// <list type="bullet">
/// <item>ส่งซ้ำได้เฉพาะแถวที่ <see cref="IsPending"/> (ค้างเพราะโควตา · ค้าง Claimed เกิน lease · ส่งซ้ำแล้วล้ม) · อายุ ≤ <see cref="MaxAge"/> · ค่าตั้งปัจจุบันยังเปิด ·
///   ไม่มีแถวใหม่กว่าที่แทนข้อความนี้ (<see cref="Supersedes"/>) · รายการยังอยู่และสถานะยังตรงกับเนื้อข้อความ (<see cref="TargetStillMakesSense"/>)</item>
/// <item>ไม่เก็บเนื้อข้อความ (PDPA · design §2.3) ⇒ เซิร์ฟเวอร์<b>ประกอบใหม่จากสถานะปัจจุบัน</b>ผ่านเส้นส่งเดิม — ประเภทที่ไม่มีเส้นประกอบใหม่ = <see cref="UnsupportedReason"/></item>
/// <item>ส่งซ้ำใช้ <c>MessageKey</c> เดิม ⇒ <c>NotifyQuotaService.ClaimAsync</c> รับช่วงแถวเดิม (กันส่งซ้ำ/กดซ้ำด้วยกลไกเดิม) · จองโควตาตามปกติ (ไม่ใช่ข้อความจำเป็น)</item>
/// </list>
/// </summary>
public static class CustomerNotifyResend
{
    /// <summary>เก่ากว่านี้ไม่ส่ง (ลูกค้าได้ข้อความเก่ามากจะสับสน)</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    /// <summary>กันกดซ้ำ/สองแท็บ: แถวที่กำลังส่งซ้ำถูกจองไว้นานเท่านี้ (เท่ากับ lease ของ claim — ยาวกว่าเวลาส่งทุกขั้นรวมกัน)</summary>
    public static readonly TimeSpan ResendLease = TimeSpan.FromMinutes(10);
    /// <summary>ส่งซ้ำได้ครั้งละไม่เกิน</summary>
    public const int MaxBatch = 200;
    /// <summary>หน้ารายการแสดงย้อนหลัง (เกิน <see cref="MaxAge"/> แสดงเป็น "เก่าเกินไป" อีกช่วงหนึ่ง)</summary>
    public static readonly TimeSpan ListWindow = TimeSpan.FromDays(60);
    /// <summary>ฝ่ายค้านรอบ 213 S1: อ่าน/ตรวจได้ครั้งละไม่เกินนี้ — เรียง<b>ใหม่ก่อน</b>แล้วตัด (จำนวนทั้งหมดนับแยก บอกผู้ใช้ว่าแสดงบางส่วน)</summary>
    public const int ListLimit = 500;

    public const string CodeOk = "ok";
    public const string CodeNotPending = "not-pending";
    public const string CodeNotQuota = "not-quota";
    public const string CodeUnsupported = "unsupported";
    public const string CodeTooOld = "too-old";
    public const string CodeEventOff = "event-off";
    public const string CodeSuperseded = "superseded";
    public const string CodeTargetMissing = "target-missing";
    public const string CodeTargetStale = "target-stale";

    /// <summary>ชนิดรายการปลายทางที่ประกอบข้อความใหม่ได้ (<c>CustomerLineLinkRule.KindBooking/KindOrder</c>) · null = ไม่รองรับ</summary>
    public static string? TargetKindOf(string? eventKey) => eventKey switch
    {
        CustomerNotifyEvents.BookingConfirmed or CustomerNotifyEvents.BookingCancelled or CustomerNotifyEvents.BookingRescheduled
            => CustomerLineLinkRule.KindBooking,
        CustomerNotifyEvents.OrderConfirmation or CustomerNotifyEvents.OrderShipped or CustomerNotifyEvents.OrderReady or CustomerNotifyEvents.OrderDelivered
            => CustomerLineLinkRule.KindOrder,
        _ => null,
    };

    /// <summary>ประเภทที่ส่งซ้ำจากหน้านี้ไม่ได้ + เหตุ (บอกผู้ใช้ตรง ๆ ไม่ซ่อนแถว) · null = รองรับ</summary>
    public static string? UnsupportedReason(string? eventKey)
    {
        if (TargetKindOf(eventKey) != null) return null;
        return eventKey switch
        {
            CustomerNotifyEvents.BookingReminder => "เตือนก่อนนัดผูกกับเวลานัด — ระบบไม่ส่งย้อนหลัง",
            CustomerNotifyEvents.BookingWaitlistPromoted => "ได้คิวจากคิวสำรอง — ระบบลองส่งใหม่เองอัตโนมัติ",
            CustomerNotifyEvents.CartAbandoned => "ข้อความการตลาด — ไม่ส่งย้อนหลัง",
            CustomerNotifyEvents.LineRelink => "คำเชิญผูก LINE — ระบบจัดการเองตามการเปลี่ยน OA",
            _ => "ประเภทนี้ยังส่งซ้ำจากหน้านี้ไม่ได้ — กรุณาติดต่อลูกค้าเอง",
        };
    }

    public static bool IsSupported(string? eventKey) => TargetKindOf(eventKey) != null;

    /// <summary>สถานะของรายการตอนนี้ยังตรงกับเนื้อข้อความไหม (ห้ามส่ง "ยืนยันการจอง" ถึงการจองที่ยกเลิกแล้ว ฯลฯ) · null = ตรง · ไม่งั้นคืนเหตุ</summary>
    private static string? TargetStillMakesSense(string eventKey, CustomerNotifyResendTarget t, DateTime nowLocal)
    {
        var future = t.BookingStartLocal is DateTime start && start > nowLocal;
        switch (eventKey)
        {
            case CustomerNotifyEvents.BookingConfirmed:
                if (t.BookingStatus is not (BookingStatus.Pending or BookingStatus.Confirmed or BookingStatus.Waitlisted))
                    return "การจองไม่อยู่ในสถานะยืนยัน/รอยืนยันแล้ว";
                return future ? null : "เลยเวลานัดแล้ว";
            case CustomerNotifyEvents.BookingRescheduled:
                if (t.BookingStatus is not (BookingStatus.Pending or BookingStatus.Confirmed))
                    return "การจองไม่อยู่ในสถานะยืนยัน/รอยืนยันแล้ว";
                return future ? null : "เลยเวลานัดแล้ว";
            case CustomerNotifyEvents.BookingCancelled:
                if (t.BookingStatus != BookingStatus.Cancelled) return "การจองไม่ได้อยู่ในสถานะยกเลิกแล้ว";
                return future ? null : "เลยเวลานัดเดิมแล้ว";
            case CustomerNotifyEvents.OrderConfirmation:
                // ฝ่ายค้านรอบ 213 (b): ยืนยันคำสั่งซื้อส่งได้เฉพาะก่อนจัดส่ง/พร้อมรับ/ส่งมอบ/ยกเลิก/คืนเงิน (whitelist)
                return t.OrderStatus is SiteOrderStatus.Pending or SiteOrderStatus.Confirmed or SiteOrderStatus.Processing
                    ? null : "คำสั่งซื้อผ่านขั้นยืนยันไปแล้ว (จัดส่ง/ส่งมอบ/ยกเลิก/คืนเงิน)";
            case CustomerNotifyEvents.OrderShipped:
            case CustomerNotifyEvents.OrderReady:
            case CustomerNotifyEvents.OrderDelivered:
                return t.OrderStatus is SiteOrderStatus s && CustomerNotifyEvents.OrderStatusKey(s) == eventKey
                    ? null : "สถานะคำสั่งซื้อเปลี่ยนไปแล้ว";
            default:
                return UnsupportedReason(eventKey);
        }
    }

    /// <summary>แถวนี้อยู่ในรายการ "ค้างส่ง" ไหม — (1) ระงับเพราะโควตาเต็ม (2) ตัวส่งล้มกลางทางจนค้าง Claimed เกิน lease (เหตุยังเป็น quota-full ·
    /// ClaimAsync ไม่ล้างเหตุ) (3) ร้านกดส่งซ้ำแล้วช่องล้ม (Failed + ResendCount &gt; 0) — ลองใหม่ได้ · แถว Failed จากเส้นปกติ (ไม่เคยค้างเพราะโควตา) ไม่อยู่ในรายการ</summary>
    public static bool IsPending(string status, string? suppressReason, bool claimExpired, int resendCount) => status switch
    {
        Accounting.Models.Entities.NotifyLogStatus.Suppressed => suppressReason == NotifySuppressReason.QuotaFull,
        Accounting.Models.Entities.NotifyLogStatus.Claimed => claimExpired && suppressReason == NotifySuppressReason.QuotaFull,
        Accounting.Models.Entities.NotifyLogStatus.Failed => resendCount > 0,
        _ => false,
    };

    /// <summary>ฝ่ายค้านรอบ 213 S2: ข้อความ <paramref name="later"/> (ถึงรายการเดียวกัน · สร้างทีหลัง) แทนข้อความ <paramref name="earlier"/> ไหม —
    /// ส่งเฉพาะฉบับล่าสุด (ไม่ส่ง "เลื่อนจาก A ไป B" ตามด้วย "เลื่อนจาก B ไป C" และไม่เสียโควตา 2 หน่วย) ·
    /// <list type="bullet">
    /// <item>ประเภทเดียวกัน ⇒ แทนเสมอ (เลื่อนนัดหลายรอบ)</item>
    /// <item>ยืนยันการจอง ⇐ เลื่อนนัด · ยกเลิก · ได้คิวจากคิวสำรอง (ข้อความหลังบอกสถานะล่าสุดแล้ว)</item>
    /// <item>เลื่อนนัด ⇐ ยกเลิก</item>
    /// <item>ยืนยันคำสั่งซื้อ ⇐ จัดส่งแล้ว · พร้อมให้รับ · ส่งมอบแล้ว · จัดส่งแล้ว/พร้อมให้รับ ⇐ ส่งมอบแล้ว</item>
    /// </list>
    /// ยกเลิก ไม่ถูกแทนด้วยอะไร (ลูกค้าต้องรู้ว่าถูกยกเลิก — สถานะรายการตัดสินต่อที่ <see cref="TargetStillMakesSense"/>)</summary>
    public static bool Supersedes(string later, string earlier)
    {
        if (later == earlier) return true;
        return earlier switch
        {
            CustomerNotifyEvents.BookingConfirmed => later is CustomerNotifyEvents.BookingRescheduled or CustomerNotifyEvents.BookingCancelled
                or CustomerNotifyEvents.BookingWaitlistPromoted,
            CustomerNotifyEvents.BookingRescheduled => later == CustomerNotifyEvents.BookingCancelled,
            CustomerNotifyEvents.OrderConfirmation => later is CustomerNotifyEvents.OrderShipped or CustomerNotifyEvents.OrderReady or CustomerNotifyEvents.OrderDelivered,
            CustomerNotifyEvents.OrderShipped or CustomerNotifyEvents.OrderReady => later == CustomerNotifyEvents.OrderDelivered,
            _ => false,
        };
    }

    public static CustomerNotifyResendVerdict Evaluate(CustomerNotifyResendInput i, DateTime nowUtc, DateTime nowLocal)
    {
        if (i.Status == Accounting.Models.Entities.NotifyLogStatus.Sent) return new(false, CodeNotPending, "ส่งถึงแล้ว");
        if (!IsPending(i.Status, i.SuppressReason, i.ClaimExpired, i.ResendCount))
            return i.Status == Accounting.Models.Entities.NotifyLogStatus.Suppressed
                ? new(false, CodeNotQuota, NotifySuppressReason.Label(i.SuppressReason) + " — ไม่ได้ค้างเพราะโควตา (ส่งซ้ำจากหน้านี้ไม่ได้)")
                : new(false, CodeNotPending, i.Status == Accounting.Models.Entities.NotifyLogStatus.Claimed ? "กำลังส่งอยู่" : "ไม่ได้ค้างส่ง");
        if (UnsupportedReason(i.EventKey) is string unsupported) return new(false, CodeUnsupported, unsupported);
        if (nowUtc - i.CreatedAtUtc > MaxAge) return new(false, CodeTooOld, $"เก่ากว่า {MaxAge.TotalDays:0} วัน — ไม่ส่งย้อนหลัง");
        if (!i.EventEnabledNow) return new(false, CodeEventOff, "ตอนนี้ปิดการแจ้งเตือนประเภทนี้ไว้ (ตั้งค่า → การแจ้งเตือนลูกค้า)");
        if (i.Superseded) return new(false, CodeSuperseded, "มีข้อความที่ใหม่กว่าถึงรายการนี้แล้ว (ส่งเฉพาะฉบับล่าสุด)");
        if (!i.Target.Exists) return new(false, CodeTargetMissing, "ไม่พบรายการ (ถูกลบแล้ว)");
        if (TargetStillMakesSense(i.EventKey, i.Target, nowLocal) is string stale) return new(false, CodeTargetStale, stale);
        return new(true, CodeOk, "ส่งซ้ำได้");
    }

    /// <summary>สรุปผลการส่งซ้ำถึงผู้ใช้ — บอกทางไปต่อเมื่อโควตายังเต็ม (F2 #8)</summary>
    public static string ResultMessage(int sent, int failed, int skipped, int stillPending, bool stoppedForQuota)
    {
        var parts = new List<string> { $"ส่งซ้ำสำเร็จ {sent} รายการ" };
        if (failed > 0) parts.Add($"ส่งไม่สำเร็จ {failed} รายการ (ดูเหตุในรายการ)");
        if (skipped > 0) parts.Add($"ข้าม {skipped} รายการ");
        if (stoppedForQuota)
            parts.Add($"โควตายังเต็ม — หยุดส่ง เหลือค้าง {stillPending} รายการ · ซื้อแพ็กข้อความเพิ่มที่หน้า ส่วนเสริม แล้วกดส่งซ้ำอีกครั้ง");
        else if (stillPending > 0) parts.Add($"ยังค้าง {stillPending} รายการ");
        return string.Join(" · ", parts);
    }

    /// <summary>ป้ายรายการปลายทางที่เซิร์ฟเวอร์ประกอบ (หน้าเว็บแสดงอย่างเดียว)</summary>
    public static string TargetLabel(string? targetKind, string? number) => targetKind switch
    {
        CustomerLineLinkRule.KindBooking => $"การจอง {number}",
        CustomerLineLinkRule.KindOrder => $"คำสั่งซื้อ {number}",
        _ => "—",
    };
}
