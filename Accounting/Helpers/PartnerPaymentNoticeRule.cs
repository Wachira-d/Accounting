using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 258-3 (F-H3 · คำตัดสิน 258-3-7) — <b>"ผู้ขายคู่ค้ารับเงินผ่านลิงก์แล้ว" ⇒ ผู้ซื้อกดบันทึกจ่ายครั้งเดียว</b> (pure · ตัวตัดสินเดียวของฝั่งผู้ซื้อ)
/// <list type="bullet">
/// <item><b>ไม่บันทึกบัญชีแทนผู้ซื้อ</b> (F2 ข้อ 3): ฝั่งผู้ขายสร้างแค่ "การแจ้ง" ในบริษัทผู้ซื้อ — การชำระเกิดเมื่อผู้ซื้อกดยืนยันในระบบของตัวเองเท่านั้น</item>
/// <item>ยอด/ภาษีหัก ณ ที่จ่าย ตามกติกาของผู้ซื้อเอง: ภาษีหักที่ยังเหลือของใบซื้อ (ตัวคิดเดียวกับตัวบันทึกจ่าย — <c>DocumentService.PaymentWhtFiguresAsync</c>) ·
/// เงินที่ผู้ขายรับ + ภาษีหักที่เหลือ = ยอดค้าง ⇒ จ่ายครบพร้อมหัก · เงินที่ผู้ขายรับ = ยอดค้าง ⇒ จ่ายครบไม่หัก · อื่น ๆ ⇒ ไม่เดา (บอกยอดทั้งสองข้าง ให้บันทึกเองที่ใบซื้อ)</item>
/// <item>ใบซื้อร่าง/รออนุมัติ ⇒ ให้อนุมัติก่อน (ใบซื้อร่างจากคู่ค้าไม่อนุมัติเอง — 255 N3) · ยกเลิก/ปฏิเสธ/ชำระครบ ⇒ ปิดการแจ้งได้</item>
/// </list>
/// </summary>
public static class PartnerPaymentNoticeRule
{
    public const string RuleCode = "XT-PARTNER-PAID";

    public const string StatusOpen = "Open";
    public const string StatusRecorded = "Recorded";
    public const string StatusDismissed = "Dismissed";

    public const decimal Tolerance = 0.01m;

    public static string StatusLabel(string? s) => s switch
    {
        StatusOpen => "รอบันทึกจ่าย",
        StatusRecorded => "บันทึกจ่ายแล้ว",
        StatusDismissed => "ปิดการแจ้งแล้ว",
        _ => string.IsNullOrWhiteSpace(s) ? "ไม่ทราบ" : s!,
    };

    /// <summary>เลขอ้างอิงของการจ่ายที่บันทึกจากการแจ้ง (canonical ตัวเดียว — ฝั่งเขียน = ฝั่งกันซ้ำ)</summary>
    public static string PaymentReference(Guid paymentIntentId) => $"PARTNER-PAID-{paymentIntentId:N}";

    /// <param name="Amount">เงินที่จ่าย (= ที่ผู้ขายรับ) · null = บันทึกไม่ได้</param>
    /// <param name="Wht">ภาษีหัก ณ ที่จ่ายของการจ่ายนี้ (กติกาของผู้ซื้อ)</param>
    public sealed record Plan(decimal? Amount, decimal? Wht, string? Problem);

    public static Plan For(DocumentStatus purchaseStatus, decimal balanceDue, decimal whtRemaining, decimal cashReceivedBySeller, string? purchaseNumber)
    {
        var no = string.IsNullOrWhiteSpace(purchaseNumber) ? "ใบซื้อ" : purchaseNumber!.Trim();
        if (purchaseStatus is DocumentStatus.Draft or DocumentStatus.WaitingApproval)
            return new(null, null, $"{no} ยังเป็นร่าง/รออนุมัติ — ตรวจผังบัญชีแล้วอนุมัติใบซื้อก่อน จากนั้นกด “บันทึกจ่ายตามที่ผู้ขายรับแล้ว” อีกครั้ง");
        if (purchaseStatus is DocumentStatus.Voided or DocumentStatus.Rejected)
            return new(null, null, $"{no} ถูกยกเลิก/ปฏิเสธแล้ว — บันทึกจ่ายจากการแจ้งนี้ไม่ได้ (ปิดการแจ้งได้)");
        if (balanceDue <= Tolerance)
            return new(null, null, $"{no} ชำระครบแล้ว — ไม่ต้องบันทึกซ้ำ (ปิดการแจ้งได้)");
        if (cashReceivedBySeller <= 0m)
            return new(null, null, "ยอดที่ผู้ขายรับไม่ถูกต้อง — บันทึกจ่ายเองที่หน้าใบซื้อ");
        var wht = Math.Max(0m, Math.Round(whtRemaining, 2, MidpointRounding.AwayFromZero));
        if (wht > 0m && Math.Abs(cashReceivedBySeller + wht - balanceDue) <= Tolerance)
            return new(cashReceivedBySeller, wht, null);
        if (Math.Abs(cashReceivedBySeller - balanceDue) <= Tolerance)
            return new(cashReceivedBySeller, 0m, null);
        return new(null, null,
            $"ยอดที่ผู้ขายรับ {cashReceivedBySeller:N2} บาท + ภาษีหัก ณ ที่จ่ายตามกติกาของเรา {wht:N2} บาท ไม่ตรงยอดค้างของ {no} ({balanceDue:N2} บาท) — "
            + "ระบบไม่เดา: บันทึกจ่ายเองที่หน้าใบซื้อ (ตรวจยอด/ภาษีหักกับผู้ขาย)");
    }
}
