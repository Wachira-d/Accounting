using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ตัวตัดสินเดียว: รายการชำระชนิดไหนคือ "เงินค่าบริการที่ผู้เช่าจ่ายให้ NextAcc"** และใครทำอะไรกับมันได้
/// (รอบ 219 เฟส 0 · PLATFORM_BILLING_AUDIT G-1/G-2/G-3 · PLATFORM_GATEWAY_REFUND_POLICY_DESIGN §2 · คำตัดสินข้อ 178)
///
/// <para><b>ที่มา:</b> ค่าส่วนเสริม/ค่าแพ็กเกจเคยสร้าง <c>PaymentIntent</c> ใต้บริษัทของผู้เช่าเอง ⇒ ใช้ gateway ของผู้เช่า (เงินเข้ากระเป๋าผู้เช่า)
/// และผู้เช่ากด "ยืนยันรับเงินด้วยมือ" ให้ตัวเองได้ ⇒ ได้สิทธิ์/ต่ออายุ + ใบกำกับภาษีในชื่อเราโดยไม่มีเงิน ·
/// ทุกด่าน (สร้าง · ยืนยันมือ · ส่งต่อใหม่ · คืนเงิน · ส่งต่อให้ handler) ต้องถามที่นี่ที่เดียว</para>
///
/// <para><b>กติกา:</b> เงินแพลตฟอร์ม = intent ใน <b>บริษัทผู้ให้บริการ</b> (<c>SiteSettings.PlatformCompanyId</c>) ที่ระบุ
/// <b>บริษัทผู้จ่าย</b> (<c>PaymentIntent.PayerCompanyId</c>) เท่านั้น · งานพนักงานบนรายการเหล่านี้ = แอดมินแพลตฟอร์ม (SystemAdmin)
/// ที่ทำงานใน tenant ผู้ให้บริการเท่านั้น · จ่ายโหมดทดสอบ = ไม่ให้สิทธิ์จริง (ยังไม่มี "บริษัททดสอบ" — เฟส 1)</para>
/// </summary>
public static class PlatformRevenueSource
{
    /// <summary>ข้อความเดียวที่ร้านเห็นเมื่อแตะรายการค่าบริการ NextAcc จากฝั่งร้าน</summary>
    public const string TenantSideMessage =
        "รายการค่าบริการ NextAcc ชำระและตรวจสอบผ่านหน้า \"แพ็กเกจ/ส่วนเสริม\" เท่านั้น — "
        + "ร้านเริ่มรับชำระ ยืนยันรับเงิน ส่งต่อ หรือคืนเงินรายการนี้เองไม่ได้ (ผู้ดูแลระบบ NextAcc เป็นผู้ตรวจสอบ)";

    /// <summary>ชนิดต้นทางที่เป็นรายได้ของแพลตฟอร์ม (ไม่ใช่ของร้าน) — เพิ่มชนิดใหม่ที่จ่ายให้ NextAcc ที่นี่ที่เดียว</summary>
    public static bool IsPlatformRevenue(PaymentSourceKind kind)
        => kind is PaymentSourceKind.SubscriptionPayment or PaymentSourceKind.AddOnPurchase;

    /// <summary>สร้าง intent ได้ไหม (ทุกทางเข้า — <c>PaymentIntentService.StartAsync</c>/<c>RecordExternalSuccessAsync</c>)
    /// · null = ได้ · ชนิดของร้าน = ได้เสมอ (ไม่แตะพฤติกรรมเดิม)</summary>
    public static string? StartDenyReason(PaymentSourceKind kind, Guid companyId, Guid? platformCompanyId, Guid? payerCompanyId)
    {
        if (!IsPlatformRevenue(kind)) return null;
        if (platformCompanyId is not Guid p)
            return "ยังไม่ได้ตั้งบริษัทผู้ให้บริการ (ตั้งค่าเว็บไซต์ → ข้อมูลผู้ขาย) — ยังรับชำระค่าบริการออนไลน์ไม่ได้ กรุณาโอนเงินแล้วแนบสลิป";
        if (companyId != p) return TenantSideMessage;
        if (payerCompanyId is null) return "รายการค่าบริการต้องระบุบริษัทผู้จ่าย";
        return null;
    }

    /// <summary>งานพนักงานบน intent — ชนิดของงาน (ฝ่ายค้านรอบ 219 S6: รายการเก่าใต้บริษัทร้านต้องคืนเงินได้)</summary>
    public enum StaffAction { ConfirmManually, Redispatch, Refund, VerifyRefund, ResolveRefundManually, RecordLegacyRefund }

    /// <summary>งานพนักงานบน intent — null = ได้ · <b>รายการค่าบริการ NextAcc ทุกงาน (รวมคืนเงิน) = SystemAdmin เท่านั้น</b>
    /// ไม่ว่า intent จะอยู่ใต้บริษัทผู้ให้บริการ (ทางที่ถูก) หรือใต้บริษัทร้าน (รายการเก่าก่อนเฟส 0)
    ///
    /// <para>ฝ่ายค้านรอบ 219 รอบ 2 (ข้อ 4 · คำตัดสินเจ้าของ: <b>ค่าบริการไม่คืนเงิน</b> — คืนได้เฉพาะแอดมินทำเป็น goodwill): รอบแรกเปิดให้ร้าน
    /// คืนเงิน/ตรวจผลคืนเงินรายการเก่าใต้บริษัทร้านเอง ⇒ ลงบัญชี "ลูกหนี้รอใบลดหนี้" ในสมุดร้านที่ไม่มีวันล้าง (ไม่มีใบกำกับของร้านให้ลดหนี้) ·
    /// ตอนนี้ปิดฝั่งร้านทั้งหมด · การลงบัญชีคืนเงินของชนิดนี้ไม่แตะลูกหนี้/ใบลดหนี้ของร้าน (<see cref="RefundSkipsStoreReceivable"/>)</para></summary>
    public static string? StaffActionDenyReason(PaymentSourceKind kind, Guid companyId, Guid? platformCompanyId, bool isSystemAdmin,
        StaffAction action = StaffAction.ConfirmManually)
    {
        if (!IsPlatformRevenue(kind)) return null;
        return isSystemAdmin ? null : TenantSideMessage;
    }

    /// <summary>ฝ่ายค้านรอบ 219 รอบ 2 (ข้อ 4): คืนเงินรายการชนิดนี้ <b>ไม่ตั้งลูกหนี้รอใบลดหนี้ในสมุดของบริษัทที่ถือ intent</b> — รายได้ค่าบริการไม่ใช่รายได้ของร้าน
    /// (ไม่มีใบกำกับของร้านให้ออกใบลดหนี้ ⇒ ลูกหนี้ค้างตลอดกาล) · บันทึกยอดคืน + ประวัติรายการ + audit บนแถวสิทธิ์ต้นทางแทน</summary>
    public static bool RefundSkipsStoreReceivable(PaymentSourceKind kind) => IsPlatformRevenue(kind);

    /// <summary>ข้อความในประวัติ/ผลคืนเงินเมื่อไม่ลงบัญชีของร้าน (ข้อความเดียว)</summary>
    public const string RefundNotBookedNote =
        "รายการค่าบริการ NextAcc — ไม่ลงบัญชีลูกหนี้/ใบลดหนี้ในสมุดของบริษัทนี้ (คำตัดสินข้อ 178 รอบ 2) · บันทึกในประวัติสิทธิ์แทน";

    /// <summary>เงินเข้าแล้ว — ให้สิทธิ์ (ต่ออายุ/เปิดส่วนเสริม) ได้ไหม · null = ได้ · ไม่ได้ = ไม่เรียก handler + บันทึกเหตุผลในประวัติ (ล้มดัง)</summary>
    public static string? EntitlementBlockReason(PaymentSourceKind kind, Guid intentCompanyId, Guid? platformCompanyId,
        Guid? payerCompanyId, bool isTestMode)
    {
        if (!IsPlatformRevenue(kind)) return null;
        if (platformCompanyId is not Guid p || p != intentCompanyId)
            return "รายการค่าบริการนี้ไม่ได้รับเงินผ่านบริษัทผู้ให้บริการ (เงินอาจเข้าบัญชีของร้าน) — ไม่ให้สิทธิ์อัตโนมัติ ให้ผู้ดูแลระบบตรวจ";
        if (payerCompanyId is null)
            return "ไม่ทราบบริษัทผู้จ่ายของรายการค่าบริการนี้ — ไม่ให้สิทธิ์อัตโนมัติ";
        if (isTestMode)
            return "ชำระในโหมดทดสอบ — บันทึกว่าทดสอบสำเร็จ แต่ไม่ให้สิทธิ์/ไม่ต่ออายุจริง";
        return null;
    }

    /// <summary>handler: แถวต้นทาง (สิทธิ์/รายการชำระ) เป็นของบริษัทผู้จ่ายจริงไหม — ไม่รู้ผู้จ่าย = ไม่ใช่</summary>
    public static bool SourceBelongsToPayer(Guid? payerCompanyId, Guid sourceCompanyId)
        => payerCompanyId is Guid p && p == sourceCompanyId;
}
