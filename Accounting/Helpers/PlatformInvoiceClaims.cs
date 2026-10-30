using System.Linq.Expressions;
using Accounting.Models.Entities;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ฝ่ายค้านรอบสุดท้าย รอบ 219 เฟส 2 (ข้อ 3) — <b>ตัวตัดสินเดียว</b>ของ "ใบแจ้งหนี้นี้มีรายการชำระที่จ่ายแล้วและรอฝั่งเรา" (ใช้ในคำค้น EF ได้):
/// สลิปรอตรวจ · พักให้แอดมิน · อนุมัติแล้วแต่ตัดชำระใบยังไม่สำเร็จ (<c>PlatformDocumentId == null</c>) ⇒ ผู้ใช้ทุกด่านเห็นตรงกัน:
/// ทวงหนี้ไม่นับ (<c>PlatformDunningPolicy.Excluded</c>) · ศูนย์บิลไม่ให้จ่ายซ้ำ · แจ้งชำระ 409 · อนุมัติรายการอื่นของใบเดียวกันถูกพัก/ปฏิเสธ
/// (กันต่ออายุซ้อนจากใบเดียว)
/// </summary>
public static class PlatformInvoiceClaims
{
    /// <summary>อนุมัติแล้ว (เงินเข้าแล้ว) แต่ตัดชำระใบยังไม่สำเร็จ — ใบยังดูเหมือนค้างทั้งที่ลูกค้าจ่ายแล้ว</summary>
    public static readonly Expression<Func<SubscriptionPayment, bool>> PaidAwaitingPlatform =
        p => p.Status == SubscriptionPaymentStatus.Approved && p.PlatformInvoiceDocumentId != null && p.PlatformDocumentId == null;

    /// <summary>ลูกค้าจ่าย/แจ้งจ่ายแล้วและรอฝั่งเรา (สลิปรอตรวจ · พักให้แอดมิน · อนุมัติแล้วรอตัดชำระ)</summary>
    public static readonly Expression<Func<SubscriptionPayment, bool>> AwaitingPlatform =
        p => p.PlatformInvoiceDocumentId != null
             && (((p.Status == SubscriptionPaymentStatus.Pending || p.Status == SubscriptionPaymentStatus.UnderReview)
                    && (p.SlipFileName != null || p.AttentionReason != null))
                 || (p.Status == SubscriptionPaymentStatus.Approved && p.PlatformDocumentId == null));

    /// <summary>รูป pure ของ <see cref="AwaitingPlatform"/> (เทสต์ · ข้อมูลในหน่วยความจำ)</summary>
    public static bool IsAwaitingPlatform(SubscriptionPaymentStatus status, bool linked, bool hasSlip, bool held, bool settled)
        => linked && (((status is SubscriptionPaymentStatus.Pending or SubscriptionPaymentStatus.UnderReview) && (hasSlip || held))
                      || (status == SubscriptionPaymentStatus.Approved && !settled));
}
