using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ปุ่ม "ดำเนินการต่อ" หนึ่งปุ่มของออเดอร์ (เซิร์ฟเวอร์ตัดสิน · หน้าเว็บแสดงตามนี้ ห้ามมีตารางสถานะของตัวเอง — F2 #5)</summary>
/// <param name="Status">สถานะปลายทาง (ชื่อ enum)</param>
/// <param name="Label">ป้ายปุ่ม</param>
/// <param name="Primary">ปุ่มหลักของขั้นนี้</param>
/// <param name="AsksTracking">ถามเลขพัสดุ/ผู้ขนส่งก่อนส่ง (จัดส่ง)</param>
/// <param name="AsksRefundAmount">ถามยอดคืนเงิน (คืนเงินบางส่วน)</param>
/// <param name="DisabledReason">ไม่ว่าง = กดไม่ได้ตอนนี้ (เหตุ + ทางไปต่อ — ข้อความเดียวกับที่เซิร์ฟเวอร์ปฏิเสธ)</param>
public sealed record SiteOrderNextAction(string Status, string Label, bool Primary, bool AsksTracking, bool AsksRefundAmount, string? DisabledReason);

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "เปลี่ยนสถานะออเดอร์หน้าเว็บจาก A ไป B ได้ไหม" (รอบ 208 · ฝ่ายค้าน W2 ข้อ 1 blocking → รอบ 209 P3 · R-209-A6 ลำดับจริง)
/// <para>ที่มา: (208) ยกเลิก→ยกเลิก (กดซ้ำ) หรือ ยกเลิก→รอ→ยกเลิก รันขั้นริบมัดจำซ้ำ · (209 A6) ด่านกันแค่ "สถานะปิด" ⇒ ยิง API ข้ามจาก
/// "รอดำเนินการ" ไป "ส่งมอบแล้ว" ได้ทั้งที่ยังไม่ได้เงิน (ปุ่มไม่มีแต่ endpoint ไม่กัน) · จัดส่งแล้วย้อนกลับไป "กำลังเตรียม" ได้ทั้งที่ใบขายออกแล้ว</para>
/// <para>ลำดับงาน: รอดำเนินการ → ยืนยัน → กำลังผลิต/เตรียม → (พร้อมให้รับ | จัดส่งแล้ว) → ส่งมอบแล้ว ·
/// <b>ข้ามขั้นไปข้างหน้าได้</b> (เดิมปุ่ม "ส่งมอบแล้ว (ลูกค้ามารับ)" อยู่ที่ยืนยัน/กำลังเตรียม — คงไว้) · <b>ถอยได้เฉพาะในช่วงเตรียม</b> (แก้กดผิด) ·
/// จัดส่ง/ส่งมอบแล้ว = จุดส่งมอบ §78(1) ใบขายออกแล้ว ⇒ ย้อนไม่ได้ (ทางไปต่อ = คืนเงิน ⇒ ใบลดหนี้ร่าง) ·
/// ส่งมอบเมื่อ<b>ยังไม่ได้ชำระตามเงื่อนไข</b> (ยอดที่ต้องจ่ายก่อนรับของ <see cref="PaymentDue.DueNow"/> &gt; 0) ⇒ ปฏิเสธ + บอกให้บันทึกรับเงินก่อน ·
/// ยกเว้นชำระเมื่อรับ (<c>pay_on_pickup</c> — DueNow = 0 เสมอ) และเก็บเงินปลายทาง (<paramref name="isCod"/>) ·
/// ยกเลิกได้จากสถานะที่ยังไม่ส่งมอบ · คืนเงินต้องมีเงินรับแล้ว · ยกเลิก/คืนเงินเต็มแล้ว = ปิด</para>
/// <para>ไม่ส่ง <paramref name="due"/> (null) = ไม่ตรวจเงิน (ทดสอบกราฟล้วน) — เส้นจริง (<c>UpdateOrderStatusLockedAsync</c>) ส่งเสมอ ·
/// COD (รวมแพ็ก P2): ออเดอร์เก็บเงินปลายทางตรึง <c>PaymentTermMode = pay_on_pickup</c> ⇒ DueNow = 0 ⇒ ผ่านด่านนี้เองโดยไม่ต้องส่ง <paramref name="isCod"/> ·
/// <paramref name="isCod"/> คงไว้สำหรับผู้เรียกที่ไม่มียอดตามเงื่อนไข</para>
/// </summary>
public static class SiteOrderStatusTransition
{
    public const string RuleCode = "CMS-ORDER-TRANSITION";
    public const string LegalReference = "RD-78(1) · RD-86/10";

    /// <summary>ลำดับงาน (ไม่ใช่ค่า enum ที่เก็บในฐาน) — ยกเลิก/คืนเงิน = -1 (นอกลำดับ)</summary>
    internal static int Stage(SiteOrderStatus s) => s switch
    {
        SiteOrderStatus.Pending => 0,
        SiteOrderStatus.Confirmed => 1,
        SiteOrderStatus.Processing => 2,
        SiteOrderStatus.ReadyForPickup => 3,
        SiteOrderStatus.Shipped => 3,
        SiteOrderStatus.Delivered => 4,
        _ => -1,
    };

    /// <summary>ปิดแล้ว (เงิน/เอกสารปิด) — เปลี่ยนต่อไม่ได้</summary>
    public static bool IsClosed(SiteOrderStatus s) => s is SiteOrderStatus.Cancelled or SiteOrderStatus.Refunded;

    /// <summary>ส่งมอบแล้ว (จัดส่ง/ส่งมอบ) — ใบขายส่วนที่เหลือออกแล้ว ⇒ ย้อนไม่ได้ · ชุดเดียวกับ <see cref="SiteOrderDepositInvoicing.IsHandoverStatus"/></summary>
    private static bool IsHandedOver(SiteOrderStatus s) => SiteOrderDepositInvoicing.IsHandoverStatus(s);

    public static (BookingTransitionResult Result, string? Message) Check(SiteOrderStatus from, SiteOrderStatus to, PaymentDue? due = null, bool isCod = false)
    {
        if (from == to) return (BookingTransitionResult.NoOp, null);
        if (IsClosed(from))
            return Reject($"ออเดอร์นี้{(from == SiteOrderStatus.Cancelled ? "ถูกยกเลิก" : "คืนเงินแล้ว")}แล้ว — เปลี่ยนสถานะต่อไม่ได้ (เงิน/เอกสารปิดแล้ว) · ให้ลูกค้าสั่งใหม่ หรือปรับยอดด้วยใบลดหนี้ที่หน้าเอกสาร");

        switch (to)
        {
            case SiteOrderStatus.Pending:
                return Reject("ย้อนกลับเป็น \"รอดำเนินการ\" ไม่ได้ — ถ้ากดยืนยันผิด ให้ยกเลิกออเดอร์แล้วให้ลูกค้าสั่งใหม่");
            case SiteOrderStatus.Cancelled:
                if (from == SiteOrderStatus.Delivered)
                    return Reject("ส่งมอบสินค้าแล้ว — ยกเลิกออเดอร์ไม่ได้ · ถ้าคืนเงินลูกค้า ใช้ \"คืนเงิน\" หรือ \"คืนเงินบางส่วน\" (ระบบร่างใบลดหนี้ให้ ยังไม่อนุมัติ)");
                if (from == SiteOrderStatus.PartiallyRefunded)
                    return Reject("ออเดอร์นี้คืนเงินบางส่วนแล้ว — ยกเลิกไม่ได้ · ใช้ \"คืนเงินทั้งหมด\" เพื่อปิดยอดที่เหลือ");
                return (BookingTransitionResult.Allowed, null);
            case SiteOrderStatus.Refunded:
            case SiteOrderStatus.PartiallyRefunded:
                if (due != null && due.Paid <= 0.005m)
                    return Reject("ออเดอร์นี้ยังไม่ได้รับเงิน — ไม่มียอดให้คืน · ถ้าไม่ขายแล้ว ใช้ \"ยกเลิก\"");
                if (to == SiteOrderStatus.PartiallyRefunded && from == SiteOrderStatus.Refunded)
                    return Reject("คืนเงินเต็มแล้ว — คืนบางส่วนเพิ่มไม่ได้");
                return (BookingTransitionResult.Allowed, null);
        }

        // ปลายทาง = ขั้นงาน (ยืนยัน … ส่งมอบ)
        if (from != SiteOrderStatus.PartiallyRefunded)
        {
            var f = Stage(from);
            var t = Stage(to);
            var backward = t < f || (t == f && from == SiteOrderStatus.Shipped);   // จัดส่งแล้ว → พร้อมให้รับ = ถอยหลังจุดส่งมอบ
            if (backward && IsHandedOver(from))
                return Reject($"ออเดอร์{(from == SiteOrderStatus.Shipped ? "จัดส่ง" : "ส่งมอบ")}แล้ว (จุดส่งมอบ — ใบขายออกแล้ว) — ย้อนสถานะไม่ได้ · ถ้าลูกค้าคืนของ/ขอคืนเงิน ใช้ \"คืนเงิน\" (ระบบร่างใบลดหนี้ให้)");
        }
        if (to == SiteOrderStatus.Delivered && due != null && !isCod && due.DueNow > 0.005m)
            return Reject($"ยังไม่ได้รับชำระตามเงื่อนไข ({PaymentTermMode.Label(due.Mode)}) — ต้องรับอีก {due.DueNow:N2} บาทก่อนส่งมอบ · "
                + "กด \"บันทึกรับเงินหน้าร้าน/ปลายทาง\" (หรือยืนยันสลิป) ก่อน แล้วจึงกดส่งมอบ");
        return (BookingTransitionResult.Allowed, null);
    }

    private static (BookingTransitionResult, string?) Reject(string msg) => (BookingTransitionResult.Rejected, msg);

    /// <summary>
    /// ปุ่ม "ดำเนินการต่อ" ของออเดอร์ — ชุดเดิมของหน้าออเดอร์ (ยืนยัน · เริ่มเตรียม · จัดส่ง · ส่งมอบ (ลูกค้ามารับ) · คืนเงิน) + พร้อมให้รับ (มารับเอง) + คืนเงินบางส่วน ·
    /// แต่ละปุ่มผ่าน <see cref="Check"/> ตัวเดียว (กดไม่ได้ ⇒ <see cref="SiteOrderNextAction.DisabledReason"/> = ข้อความที่เซิร์ฟเวอร์จะปฏิเสธ)
    /// </summary>
    public static IReadOnlyList<SiteOrderNextAction> NextActions(SiteOrderStatus status, string? fulfillmentMethod, PaymentDue? due, bool isCod = false)
    {
        var pickup = fulfillmentMethod == FulfillmentMethod.Pickup;
        var list = new List<(SiteOrderStatus To, string Label, bool Primary)>();
        switch (status)
        {
            case SiteOrderStatus.Pending:
                list.Add((SiteOrderStatus.Confirmed, "ยืนยันคำสั่งซื้อ", true));
                list.Add((SiteOrderStatus.Cancelled, "ปฏิเสธ/ยกเลิก", false));
                break;
            case SiteOrderStatus.Confirmed:
                list.Add((SiteOrderStatus.Processing, "เริ่มผลิต/เตรียมสินค้า", true));
                if (pickup) list.Add((SiteOrderStatus.ReadyForPickup, "พร้อมให้ลูกค้ารับ", false));
                list.Add((SiteOrderStatus.Delivered, "ส่งมอบแล้ว (ลูกค้ามารับ)", false));
                list.Add((SiteOrderStatus.Cancelled, "ยกเลิก", false));
                break;
            case SiteOrderStatus.Processing:
                if (pickup) list.Add((SiteOrderStatus.ReadyForPickup, "พร้อมให้ลูกค้ารับ", true));
                else list.Add((SiteOrderStatus.Shipped, "จัดส่งแล้ว", true));
                list.Add((SiteOrderStatus.Delivered, "ส่งมอบแล้ว (ลูกค้ามารับ)", false));
                list.Add((SiteOrderStatus.Cancelled, "ยกเลิก", false));
                break;
            case SiteOrderStatus.ReadyForPickup:
                list.Add((SiteOrderStatus.Delivered, "ลูกค้ารับแล้ว (ส่งมอบ)", true));
                list.Add((SiteOrderStatus.Shipped, "เปลี่ยนเป็นจัดส่ง", false));
                list.Add((SiteOrderStatus.Cancelled, "ยกเลิก", false));
                break;
            case SiteOrderStatus.Shipped:
                list.Add((SiteOrderStatus.Delivered, "ส่งถึงปลายทาง", true));
                break;
            case SiteOrderStatus.Delivered:
                list.Add((SiteOrderStatus.Refunded, "คืนเงิน", false));
                list.Add((SiteOrderStatus.PartiallyRefunded, "คืนเงินบางส่วน", false));
                break;
            case SiteOrderStatus.PartiallyRefunded:
                list.Add((SiteOrderStatus.Refunded, "คืนเงินทั้งหมด", false));
                break;
        }
        return list.Select(a => new SiteOrderNextAction(a.To.ToString(), a.Label, a.Primary,
                AsksTracking: a.To == SiteOrderStatus.Shipped,
                AsksRefundAmount: a.To is SiteOrderStatus.PartiallyRefunded or SiteOrderStatus.Refunded,
                DisabledReason: Check(status, a.To, due, isCod).Message))
            .ToList();
    }

    /// <summary>รับเงินหน้าร้าน/ปลายทางได้ไหม (R-209-E7) — ออเดอร์ยังไม่ปิด/ไม่อยู่ระหว่างคืนเงิน และยังมียอดคงค้าง · ตัวเดียวกับปุ่มบนหน้าออเดอร์และด่านของ endpoint</summary>
    public static bool AcceptsCounterPayment(SiteOrderStatus s, decimal balance)
        => !IsClosed(s) && s != SiteOrderStatus.PartiallyRefunded && balance > 0.005m;

    /// <summary>สถานะที่ "คิวผลิต" แสดง (ยังไม่ส่งมอบ/ไม่ปิด) — ตัวกรองเดียวของ endpoint คิวผลิต</summary>
    public static readonly IReadOnlyList<SiteOrderStatus> ProductionQueueStatuses = new[]
        { SiteOrderStatus.Pending, SiteOrderStatus.Confirmed, SiteOrderStatus.Processing, SiteOrderStatus.ReadyForPickup };

    public const string NoOpNotice = "ออเดอร์อยู่ในสถานะนี้อยู่แล้ว — ไม่ได้ทำซ้ำ";
}
