using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ประเภทข้อความถึงลูกค้าปลายทาง 1 ชนิด — <c>OwnerSwitch</c> = สวิตช์เดิมที่เป็น<b>เจ้าของเรื่องเปิด/ปิด/เวลา</b> ของข้อความนี้
/// (null = ไม่มี ส่งทุกครั้งที่เกิดเหตุการณ์) · ตาราง matrix คุมแค่ "ช่องทาง" (F2 #4 — ห้ามสวิตช์สองตัวคุมเรื่องเดียว)</summary>
public sealed record CustomerNotifyEvent(string Key, string Label, string Group, bool LineCapable, string? OwnerSwitch, string? OwnerSwitchUrl);

/// <summary>
/// **แคตตาล็อกประเภทข้อความถึงลูกค้า — ตัวตั้งเดียว** (รอบ 212 แพ็ก A · คำตัดสินข้อ 171 · <c>erp-review/2026-10-30/NOTIFY_PREFS_QUOTA_DAILY_SUMMARY_DESIGN.md</c> §1.1/§2.1)
/// <list type="bullet">
/// <item>คีย์ + ป้ายไทย + กลุ่ม — หน้าเว็บรับเป็น "ชื่อ" จากเซิร์ฟเวอร์ (F2 #5 · ไม่มีสำเนาใน JS)</item>
/// <item>ทุกจุดที่ส่งถึงลูกค้าส่ง <c>EventKey</c> จากที่นี่ผ่าน <c>IBookingNotifier.SendAsync(CustomerNotifyRequest)</c> (ด่าน <c>tools/required_call_site_check.py</c>)</item>
/// <item>คีย์ <c>shop.*</c> = ข้อความถึง<b>ร้าน</b> (ไม่อยู่ในค่าตั้ง matrix · นับโควตาเมื่อออกช่องกลาง)</item>
/// </list>
/// <b>ห้ามเปลี่ยนค่าคีย์หลังใช้งาน</b> — ผูกกับแถว <c>CustomerNotifyRules</c>/<c>CustomerNotifyLogs</c> ที่บันทึกแล้ว
/// </summary>
public static class CustomerNotifyEvents
{
    /// <summary>แถว "ทั้งหมด" ของค่าตั้ง (ใช้กับทุกประเภทที่ไม่ได้ตั้งเฉพาะ)</summary>
    public const string All = "*";

    public const string BookingConfirmed = "booking.confirmed";
    public const string BookingCancelled = "booking.cancelled";
    public const string BookingRescheduled = "booking.rescheduled";
    public const string BookingReminder = "booking.reminder";
    public const string BookingWaitlistPromoted = "booking.waitlist-promoted";
    public const string ClassCancelled = "class.cancelled";
    public const string ClassSubstitute = "class.substitute";
    public const string OrderConfirmation = "order.confirmation";
    public const string OrderShipped = "order.shipped";
    public const string OrderReady = "order.ready";
    public const string OrderDelivered = "order.delivered";
    public const string CreditExpiring = "credit.expiring";
    public const string LodgingCreated = "lodging.created";
    public const string LodgingSlipRequired = "lodging.slip-required";
    public const string LodgingConfirmed = "lodging.confirmed";
    public const string LodgingSlipRejected = "lodging.slip-rejected";
    public const string CartAbandoned = "cart.abandoned";
    public const string FormAutoReply = "form.autoreply";
    /// <summary>รอบ 212 แพ็ก B: ร้านเปลี่ยน LINE OA ⇒ เชิญลูกค้าที่ผูกไว้ให้ผูกใหม่ (ครั้งเดียวต่อการสลับ · <c>CustomerNotifyOutboxJob</c>)</summary>
    public const string LineRelink = "line.relink";

    /// <summary>ข้อความถึงร้าน (ออเดอร์ใหม่ · การจองใหม่ · สลิป · lead · ที่พัก) — นับโควตาเมื่อออกช่องกลาง · ถูกระงับเมื่อเต็ม</summary>
    public const string ShopNotify = "shop.notify";
    /// <summary>เงินเข้าแต่ยืนยันไม่ได้ (เงินของลูกค้าค้าง) — นับโควตาแต่<b>ไม่ถูกระงับ</b>เมื่อเต็ม (<see cref="IsEssential"/>)</summary>
    public const string ShopPaymentProblem = "shop.payment-problem";

    private const string GBooking = "การจอง";
    private const string GClass = "คลาส";
    private const string GOrder = "คำสั่งซื้อ";
    private const string GCredit = "บัตรคอร์ส/สมาชิก";
    private const string GLodging = "ที่พัก";
    private const string GOther = "อื่น ๆ";

    public static readonly IReadOnlyList<CustomerNotifyEvent> Catalog = new[]
    {
        new CustomerNotifyEvent(BookingConfirmed, "ยืนยันการจอง", GBooking, true, null, null),
        new CustomerNotifyEvent(BookingCancelled, "ร้านยกเลิกการจอง", GBooking, true, null, null),
        new CustomerNotifyEvent(BookingRescheduled, "เลื่อนนัด", GBooking, true, null, null),
        new CustomerNotifyEvent(BookingReminder, "เตือนก่อนนัด", GBooking, true, "เปิด/ปิด + ชั่วโมงเตือน อยู่ที่ค่าตั้งการจองของเว็บ", "/pages/cms-edit.html"),
        new CustomerNotifyEvent(BookingWaitlistPromoted, "ได้คิวจากคิวสำรอง", GBooking, true, null, null),
        new CustomerNotifyEvent(ClassCancelled, "คลาสยกเลิก", GClass, true, "ติ๊ก \"แจ้งลูกค้า\" ทุกครั้งที่สั่งยกเลิกคลาส", null),
        new CustomerNotifyEvent(ClassSubstitute, "เปลี่ยนผู้สอน", GClass, true, "ติ๊ก \"แจ้งลูกค้า\" ทุกครั้งที่เปลี่ยนผู้สอน", null),
        new CustomerNotifyEvent(OrderConfirmation, "ยืนยันคำสั่งซื้อ", GOrder, true, null, null),
        new CustomerNotifyEvent(OrderShipped, "จัดส่งแล้ว", GOrder, true, null, null),
        new CustomerNotifyEvent(OrderReady, "พร้อมให้รับ", GOrder, true, null, null),
        new CustomerNotifyEvent(OrderDelivered, "ส่งมอบแล้ว", GOrder, true, null, null),
        new CustomerNotifyEvent(CreditExpiring, "บัตรใกล้หมดอายุ", GCredit, true, "จำนวนวันก่อนหมดอายุ (0 = ไม่แจ้ง) อยู่ที่แพ็กเกจบัตรแต่ละแบบ", "/pages/course-credits.html"),
        new CustomerNotifyEvent(LodgingCreated, "ที่พัก: รับคำขอจอง", GLodging, false, null, null),
        new CustomerNotifyEvent(LodgingSlipRequired, "ที่พัก: รอสลิปโอนเงิน", GLodging, false, null, null),
        new CustomerNotifyEvent(LodgingConfirmed, "ที่พัก: ยืนยันการจอง", GLodging, false, null, null),
        new CustomerNotifyEvent(LodgingSlipRejected, "ที่พัก: สลิปไม่ผ่าน", GLodging, false, null, null),
        new CustomerNotifyEvent(CartAbandoned, "ตะกร้าค้าง (การตลาด — ส่งเฉพาะลูกค้าที่ยินยอมรับข่าวสาร)", GOther, false, "เปิด/ปิด + ชั่วโมง อยู่ที่ตั้งค่าร้านค้าของเว็บ", "/pages/cms-store-settings.html"),
        new CustomerNotifyEvent(FormAutoReply, "ตอบกลับฟอร์มอัตโนมัติ", GOther, false, "เปิด/ปิดที่ตัวฟอร์มแต่ละฟอร์ม (ตอบกลับอัตโนมัติ)", null),
        new CustomerNotifyEvent(LineRelink, "เชิญผูก LINE ใหม่เมื่อร้านเปลี่ยนบัญชี LINE OA (ส่งครั้งเดียวต่อการเปลี่ยน)", GOther, true, null, null),
    };

    private static bool IsCustomerEvent(string? key) => key != null && Catalog.Any(e => e.Key == key);

    public static bool IsShopEvent(string? key) => key is ShopNotify or ShopPaymentProblem;

    /// <summary>คีย์ที่ส่งได้ (ลูกค้า + ร้าน) — คีย์อื่น = บั๊กของผู้เรียก</summary>
    public static bool IsKnown(string? key) => IsCustomerEvent(key) || IsShopEvent(key);

    /// <summary>ข้อความที่ต้องถึงแม้โควตาช่องกลางเต็ม (เงินลูกค้าค้าง) — ยังนับโควตา</summary>
    public static bool IsEssential(string? key) => key == ShopPaymentProblem;

    /// <summary>คีย์ที่ตั้งค่าได้ในตาราง (แถว "ทั้งหมด" + ทุกประเภทลูกค้า)</summary>
    public static bool IsConfigurable(string? key) => key == All || IsCustomerEvent(key);

    /// <summary>สถานะออเดอร์ที่ลูกค้าได้ข้อความ (ตรงกับ <see cref="CmsNotificationText.CustomerOrderStatus"/>) · null = สถานะนั้นไม่มีข้อความ</summary>
    public static string? OrderStatusKey(SiteOrderStatus status) => status switch
    {
        SiteOrderStatus.Shipped => OrderShipped,
        SiteOrderStatus.ReadyForPickup => OrderReady,
        SiteOrderStatus.Delivered => OrderDelivered,
        _ => null,
    };

    /// <summary>เหตุการณ์ของที่พัก (<c>LodgingService.TryNotifyAsync</c>) → คีย์ข้อความถึงแขก · null = เหตุการณ์นั้นไม่มีข้อความถึงแขก</summary>
    public static string? LodgingGuestKey(string evt) => evt switch
    {
        LodgingGuestConfirmPolicy.EventCreated => LodgingCreated,
        LodgingGuestConfirmPolicy.EventAwaitingSlip => LodgingSlipRequired,
        "confirmed" => LodgingConfirmed,
        "slip-rejected" => LodgingSlipRejected,
        _ => null,
    };
}
