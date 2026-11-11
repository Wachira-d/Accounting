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
        StatusRecording => "กำลังบันทึกจ่าย",
        _ => string.IsNullOrWhiteSpace(s) ? "ไม่ทราบ" : s!,
    };

    /// <summary>สถานะชั่วคราวระหว่างบันทึก (จองแถวแบบมีเงื่อนไข — สองคนกดพร้อมกันได้คนเดียว · ฝ่ายค้าน L2) · ค้างเกิน <see cref="RecordingStale"/> ⇒ กดใหม่ได้</summary>
    public const string StatusRecording = "Recording";
    public static readonly TimeSpan RecordingStale = TimeSpan.FromMinutes(10);

    /// <summary>ฝ่ายค้าน M5: ช่องทางที่ผู้ขายรับเงิน (ชื่อ <c>PaymentMethodKind</c> ที่เก็บในแถวแจ้ง) — ป้ายในกล่องยืนยันของผู้ซื้อ</summary>
    public static string ChannelLabel(string? methodKind) => methodKind switch
    {
        nameof(PaymentMethodKind.PromptPay) => "พร้อมเพย์ผ่านลิงก์ชำระของผู้ขาย",
        nameof(PaymentMethodKind.Card) => "บัตรผ่านลิงก์ชำระของผู้ขาย",
        nameof(PaymentMethodKind.MobileBanking) or nameof(PaymentMethodKind.InternetBanking) => "ธนาคารออนไลน์ผ่านลิงก์ชำระของผู้ขาย",
        nameof(PaymentMethodKind.TrueMoney) => "TrueMoney ผ่านลิงก์ชำระของผู้ขาย",
        nameof(PaymentMethodKind.ManualSlip) => "โอนเงิน + สลิปที่พนักงานของผู้ขายตรวจแล้ว",
        _ => "ลิงก์ชำระของผู้ขาย",
    };

    /// <summary>ฝ่ายค้าน M4: วิธีจ่ายตั้งต้นของผู้ซื้อตามช่องทางที่ผู้ขายรับ (ผู้ซื้อเปลี่ยนได้ในกล่องยืนยัน)</summary>
    public static PaymentMethod DefaultMethod(string? methodKind) => methodKind switch
    {
        nameof(PaymentMethodKind.Card) => PaymentMethod.CreditCard,
        nameof(PaymentMethodKind.PromptPay) => PaymentMethod.PromptPay,
        nameof(PaymentMethodKind.TrueMoney) => PaymentMethod.EWallet,
        _ => PaymentMethod.BankTransfer,
    };

    /// <summary>วิธีจ่ายที่เลือกได้ในกล่องยืนยัน (ชื่อ enum + ป้าย)</summary>
    public static readonly IReadOnlyList<(PaymentMethod Method, string Label)> Methods = new[]
    {
        (PaymentMethod.BankTransfer, "โอนเงิน"), (PaymentMethod.PromptPay, "พร้อมเพย์"), (PaymentMethod.CreditCard, "บัตรเครดิต/เดบิต"),
        (PaymentMethod.EWallet, "e-Wallet"), (PaymentMethod.Cash, "เงินสด"),
    };

    /// <summary>เลขอ้างอิงของการจ่ายที่บันทึกจากการแจ้ง (canonical ตัวเดียว — ฝั่งเขียน = ฝั่งกันซ้ำ)</summary>
    public static string PaymentReference(Guid paymentIntentId) => $"PARTNER-PAID-{paymentIntentId:N}";

    /// <param name="Amount">เงินที่จ่าย (= ที่ผู้ขายรับ) · null = บันทึกไม่ได้</param>
    /// <param name="Wht">ภาษีหัก ณ ที่จ่ายของการจ่ายนี้ (กติกาของผู้ซื้อ)</param>
    /// <param name="Warning">ฝ่ายค้าน M3: บันทึกได้แต่ต้องบอกผู้กดก่อน (แสดงในกล่องยืนยัน) · null = ไม่มี</param>
    public sealed record Plan(decimal? Amount, decimal? Wht, string? Problem, string? Warning = null);

    /// <summary>ข้อความเตือนเมื่อจ่ายเต็มทั้งที่ใบซื้อมีภาษีหัก ณ ที่จ่ายค้าง (ตัวเดียว — เทสต์อ้างได้)</summary>
    private static string FullPaidWhtWarning(decimal wht, string purchaseNumber)
        => $"จ่ายเต็มยอดผ่านลิงก์ของผู้ขาย — ระบบจะบันทึกภาษีหัก ณ ที่จ่าย 0 บาท แต่ตามกติกาของเรา {purchaseNumber} ต้องหัก {wht:N2} บาท · "
           + "หน้าที่หักและนำส่งยังอยู่ (ม.50 · ผู้จ่ายรับผิดร่วมตาม ม.54) — หลังบันทึกจ่าย เปิดใบซื้อเพื่อบันทึกภาษีหัก/ออก 50 ทวิ แบบภาษีออกแทน หรือเรียกเงินส่วนนั้นคืนจากผู้ขาย";

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
            // ฝ่ายค้าน M3: ใบซื้อมีภาษีหักค้างตามกติกาของเรา แต่จ่ายเต็มผ่านลิงก์ ⇒ บันทึกจ่ายตามเงินจริง (หัก 0) แต่ต้องเตือน: หน้าที่หัก/นำส่งยังอยู่ (ม.50 · ม.54 ผู้จ่ายรับผิดร่วม)
            return new(cashReceivedBySeller, 0m, null, wht > 0m ? FullPaidWhtWarning(wht, no) : null);
        return new(null, null,
            $"ยอดที่ผู้ขายรับ {cashReceivedBySeller:N2} บาท + ภาษีหัก ณ ที่จ่ายตามกติกาของเรา {wht:N2} บาท ไม่ตรงยอดค้างของ {no} ({balanceDue:N2} บาท) — "
            + "ระบบไม่เดา: บันทึกจ่ายเองที่หน้าใบซื้อ (ตรวจยอด/ภาษีหักกับผู้ขาย)");
    }
}
