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

/// <summary>ข้อเท็จจริงของแถวบันทึก 1 แถว — <c>SupersededBySent</c> = มีข้อความประเภทเดียวกันถึงรายการเดียวกันที่ส่งถึงแล้วหลังแถวนี้ ·
/// <c>EventEnabledNow</c> = ค่าตั้งปัจจุบันยังเปิดอย่างน้อย 1 ช่อง (<see cref="CustomerNotifyPolicy.Resolve"/>)</summary>
public sealed record CustomerNotifyResendInput(string EventKey, string Status, string? SuppressReason, DateTime CreatedAtUtc,
    bool EventEnabledNow, bool SupersededBySent, CustomerNotifyResendTarget Target);

/// <summary>ผลตัดสิน — <c>Code</c> คงที่ (ใช้ในเทสต์/กรอง) · <c>Reason</c> ภาษาไทยถึงผู้ใช้ (หน้าเว็บแสดงตรง ๆ)</summary>
public sealed record CustomerNotifyResendVerdict(bool Resendable, string Code, string Reason);

/// <summary>
/// **ตัวตัดสิน "ส่งซ้ำข้อความที่ค้างเพราะโควตา" — ตัวเดียว** (รอบ 213 · คำตัดสินข้อ 172 · เจ้าของ: "ถ้าเติมโควต้าแล้ว สามารถส่งซ้ำทีหลังได้")
/// <list type="bullet">
/// <item>ส่งซ้ำได้เฉพาะแถว <c>Suppressed</c> + <see cref="NotifySuppressReason.QuotaFull"/> · อายุ ≤ <see cref="MaxAge"/> · ค่าตั้งปัจจุบันยังเปิด ·
///   ยังไม่มีข้อความประเภทเดียวกันส่งถึงรายการนั้นทีหลัง · รายการยังอยู่และสถานะยังตรงกับเนื้อข้อความ (<see cref="TargetStillMakesSense"/>)</item>
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
    public static string? TargetStillMakesSense(string eventKey, CustomerNotifyResendTarget t, DateTime nowLocal)
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
                return t.OrderStatus is SiteOrderStatus.Cancelled or SiteOrderStatus.Refunded ? "คำสั่งซื้อถูกยกเลิก/คืนเงินแล้ว" : null;
            case CustomerNotifyEvents.OrderShipped:
            case CustomerNotifyEvents.OrderReady:
            case CustomerNotifyEvents.OrderDelivered:
                return t.OrderStatus is SiteOrderStatus s && CustomerNotifyEvents.OrderStatusKey(s) == eventKey
                    ? null : "สถานะคำสั่งซื้อเปลี่ยนไปแล้ว";
            default:
                return UnsupportedReason(eventKey);
        }
    }

    public static CustomerNotifyResendVerdict Evaluate(CustomerNotifyResendInput i, DateTime nowUtc, DateTime nowLocal)
    {
        if (i.Status != Accounting.Models.Entities.NotifyLogStatus.Suppressed)
            return new(false, CodeNotPending, i.Status == Accounting.Models.Entities.NotifyLogStatus.Sent ? "ส่งถึงแล้ว" : "ไม่ได้ค้างส่ง");
        if (i.SuppressReason != NotifySuppressReason.QuotaFull)
            return new(false, CodeNotQuota, NotifySuppressReason.Label(i.SuppressReason) + " — ไม่ได้ค้างเพราะโควตา (ส่งซ้ำจากหน้านี้ไม่ได้)");
        if (UnsupportedReason(i.EventKey) is string unsupported) return new(false, CodeUnsupported, unsupported);
        if (nowUtc - i.CreatedAtUtc > MaxAge) return new(false, CodeTooOld, $"เก่ากว่า {MaxAge.TotalDays:0} วัน — ไม่ส่งย้อนหลัง");
        if (!i.EventEnabledNow) return new(false, CodeEventOff, "ตอนนี้ปิดการแจ้งเตือนประเภทนี้ไว้ (ตั้งค่า → การแจ้งเตือนลูกค้า)");
        if (i.SupersededBySent) return new(false, CodeSuperseded, "ลูกค้าได้ข้อความประเภทเดียวกันที่ใหม่กว่าแล้ว");
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
