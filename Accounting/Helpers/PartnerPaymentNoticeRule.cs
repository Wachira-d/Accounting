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
    /// <summary>รอบ 264 P3 (C-03 · คำตัดสิน 264-3-7): ผู้ขายยกเลิกการรับชำระก่อนผู้ซื้อบันทึกจ่าย ⇒ ถอนการแจ้ง (บันทึกจ่ายจากแถวนี้ไม่ได้อีก)</summary>
    public const string StatusWithdrawn = "Withdrawn";

    /// <summary>รอบ 264 P3: ป้ายบนใบซื้อของผู้ซื้อเมื่อผู้ขายยกเลิกการรับเงินที่ผู้ซื้อบันทึกจ่ายไปแล้ว (ระบบไม่ยกเลิกการจ่ายแทน — E6) · ตัวเดียวทั้งฝั่งเขียน/ฝั่งอ่าน</summary>
    public const string SellerVoidedMarker = "[PARTNER-PAYMENT-VOIDED]";

    /// <summary>ข้อความป้ายบนใบซื้อ (หนึ่งบรรทัด) — ผู้อ่าน: หน้าเอกสาร (หมายเหตุภายใน) + Autopilot</summary>
    public static string SellerVoidedNote(string sellerName, string sourceDocumentNumber, decimal cash, DateTime atUtc)
        => $"{SellerVoidedMarker} {atUtc.AddHours(7):dd/MM/yyyy}: {sellerName} ยกเลิกการรับเงิน {cash:N2} บาท ของ {sourceDocumentNumber} ที่เราบันทึกจ่ายไปแล้ว — "
           + "ตรวจรายการเดินบัญชี แล้วยกเลิกการจ่าย/ขอเงินคืนเอง (ระบบไม่ยกเลิกการจ่ายแทน)";

    public const decimal Tolerance = 0.01m;

    public static string StatusLabel(string? s) => s switch
    {
        StatusOpen => "รอบันทึกจ่าย",
        StatusRecorded => "บันทึกจ่ายแล้ว",
        StatusDismissed => "ปิดการแจ้งแล้ว",
        StatusRecording => "กำลังบันทึกจ่าย",
        StatusWithdrawn => "ผู้ขายยกเลิกการรับเงิน — ถอนการแจ้งแล้ว",
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
        EntryManual => "การรับชำระที่ผู้ขายบันทึกเอง",
        EntryBank => "รายการเดินบัญชีของผู้ขาย (เงินเข้าแล้ว)",
        EntryImport => "การรับชำระที่ผู้ขายนำเข้าจากไฟล์",
        EntryOther => "การรับชำระที่ผู้ขายบันทึก",
        _ => "ลิงก์ชำระของผู้ขาย",
    };

    // รอบ 264 P3 (C-04): ช่องทางของการแจ้งที่ไม่ได้มาจากลิงก์ชำระ (ชื่อคงที่ ≤ 32 ตัว · ตัวเดียวทั้งฝั่งเขียน/อ่าน)
    public const string EntryManual = "Entry:Manual";
    public const string EntryBank = "Entry:BankAutoSettle";
    public const string EntryImport = "Entry:Import";
    public const string EntryOther = "Entry:Other";

    /// <summary>ช่องทางที่เก็บในแถวแจ้ง — มี intent (ลิงก์/สลิป) ⇒ ชื่อ <c>PaymentMethodKind</c> · ไม่มี ⇒ ตามทางเข้าของการรับชำระฝั่งผู้ขาย</summary>
    public static string ChannelOf(PaymentMethodKind? intentKind, PaymentEntrySource source) => intentKind is PaymentMethodKind k ? k.ToString() : source switch
    {
        PaymentEntrySource.Manual => EntryManual,
        PaymentEntrySource.BankAutoSettle => EntryBank,
        PaymentEntrySource.Import or PaymentEntrySource.ImportSendReceipt => EntryImport,
        _ => EntryOther,
    };

    /// <summary>intent ของการรับชำระฝั่งผู้ขายจากเลขอ้างอิง (รูปตาม <see cref="GatewayPaymentRecord.Reference"/> ตัวเดียว) · ไม่ใช่รูปนั้น ⇒ null (ไม่เดา)</summary>
    public static Guid? IntentFromSellerReference(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var prefix = GatewayPaymentRecord.Reference(Guid.Empty)[..^32];
        var r = reference.Trim();
        return r.Length == prefix.Length + 32 && r.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(r[prefix.Length..], "N", out var g) ? g : null;
    }

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

    /// <summary>เลขอ้างอิงของการจ่ายที่บันทึกจากการแจ้ง (canonical ตัวเดียว — ฝั่งเขียน = ฝั่งกันซ้ำ) · รูปเดิม (แถวที่มีแต่ intent)</summary>
    public static string PaymentReference(Guid paymentIntentId) => $"PARTNER-PAID-{paymentIntentId:N}";

    /// <summary>รอบ 264 P3 (264-3-6): รูปใหม่ — กุญแจ = การรับชำระของผู้ขาย</summary>
    internal static string PaymentReferenceForSellerPayment(Guid sellerPaymentId) => $"PARTNER-PAID-P{sellerPaymentId:N}";

    /// <summary>เลขอ้างอิงที่<b>เขียน</b>สำหรับการแจ้งนี้ — แถวที่รู้การรับชำระของผู้ขาย ⇒ รูปใหม่ · แถวเก่าที่มีแต่ intent ⇒ รูปเดิม · ไม่มีทั้งคู่ ⇒ null (บันทึกไม่ได้)</summary>
    public static string? PaymentReferenceFor(Guid? sellerPaymentId, Guid? paymentIntentId)
        => sellerPaymentId is Guid sp ? PaymentReferenceForSellerPayment(sp) : paymentIntentId is Guid i ? PaymentReference(i) : null;

    /// <summary>เลขอ้างอิงทุกรูปที่<b>กันซ้ำ</b>ต้องตรวจ (แถวที่ migration เติม SellerPaymentId อาจบันทึกจ่ายไปแล้วด้วยรูปเดิม)</summary>
    public static IReadOnlyList<string> PaymentReferencesToCheck(Guid? sellerPaymentId, Guid? paymentIntentId)
    {
        var list = new List<string>();
        if (sellerPaymentId is Guid sp) list.Add(PaymentReferenceForSellerPayment(sp));
        if (paymentIntentId is Guid i) list.Add(PaymentReference(i));
        return list;
    }

    /// <param name="Amount">เงินที่จ่าย (= ที่ผู้ขายรับ) · null = บันทึกไม่ได้</param>
    /// <param name="Wht">ภาษีหัก ณ ที่จ่ายของการจ่ายนี้ (กติกาของผู้ซื้อ)</param>
    /// <param name="Warning">ฝ่ายค้าน M3: บันทึกได้แต่ต้องบอกผู้กดก่อน (แสดงในกล่องยืนยัน) · null = ไม่มี</param>
    public sealed record Plan(decimal? Amount, decimal? Wht, string? Problem, string? Warning = null);

    /// <summary>ข้อความเตือนเมื่อจ่ายเต็มทั้งที่ใบซื้อมีภาษีหัก ณ ที่จ่ายค้าง (ตัวเดียว — เทสต์อ้างได้)</summary>
    private static string FullPaidWhtWarning(decimal wht, string purchaseNumber)
        => $"จ่ายเต็มยอดผ่านลิงก์ของผู้ขาย — ระบบจะบันทึกภาษีหัก ณ ที่จ่าย 0 บาท แต่ตามกติกาของเรา {purchaseNumber} ต้องหัก {wht:N2} บาท · "
           + "หน้าที่หักและนำส่งยังอยู่ (ม.50 · ผู้จ่ายรับผิดร่วมตาม ม.54) — หลังบันทึกจ่าย เปิดใบซื้อเพื่อบันทึกภาษีหัก/ออก 50 ทวิ แบบภาษีออกแทน หรือเรียกเงินส่วนนั้นคืนจากผู้ขาย";

    /// <param name="sellerRecordedWht">รอบ 264 P3 (264-3-5 · D2): ภาษีหักที่ผู้ขายบันทึกในการรับชำระนี้ — ใช้<b>ยืนยันไขว้</b>เท่านั้น (ไม่คัดลอกเป็นคำตอบ) · null = ไม่ใช้กติกาจ่ายบางส่วนที่มีภาษีหัก</param>
    /// <param name="singleWhtRatePercent">อัตราหักของใบซื้อเมื่อทั้งใบมีอัตราเดียว (เช่น 3) · null = หลายอัตรา/ไม่มี ⇒ ไม่เดาสัดส่วน</param>
    public static Plan For(DocumentStatus purchaseStatus, decimal balanceDue, decimal whtRemaining, decimal cashReceivedBySeller, string? purchaseNumber,
        decimal? sellerRecordedWht = null, decimal? singleWhtRatePercent = null)
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
        if (PartialPlan(balanceDue, wht, cashReceivedBySeller, sellerRecordedWht, singleWhtRatePercent) is Plan partial)
            return partial;
        return new(null, null,
            $"ยอดที่ผู้ขายรับ {cashReceivedBySeller:N2} บาท + ภาษีหัก ณ ที่จ่ายตามกติกาของเรา {wht:N2} บาท ไม่ตรงยอดค้างของ {no} ({balanceDue:N2} บาท) — "
            + "ระบบไม่เดา: บันทึกจ่ายเองที่หน้าใบซื้อ (ตรวจยอด/ภาษีหักกับผู้ขาย)");
    }

    /// <summary>
    /// รอบ 264 P3 (คำตัดสิน 264-3-5 · C-05 · D1/D2) — <b>จ่ายบางส่วน</b> (เงินที่ผู้ขายรับ + ภาษีหัก &lt; ยอดค้าง) · ยอดเต็มตัดสินข้างบนก่อนเสมอ (ไม่เปลี่ยน):
    /// ไม่มีภาษีหักค้าง ⇒ จ่ายตามเงินจริง หัก 0 · มีภาษีหักค้าง ⇒ เฉพาะเมื่อใบซื้อมี<b>อัตราเดียว</b> r และ <c>round(เงิน × r/(100−r), 2)</c> ตรงกับภาษีหักที่ผู้ขายบันทึก
    /// (สองฝั่งยืนยันกัน) และไม่เกินภาษีหักที่เหลือ ⇒ หักยอดนั้น · อื่น ๆ ⇒ null (ไม่เดาสัดส่วน)
    /// </summary>
    internal static Plan? PartialPlan(decimal balanceDue, decimal whtRemaining, decimal cash, decimal? sellerRecordedWht, decimal? singleWhtRatePercent)
    {
        if (cash <= 0m) return null;
        if (whtRemaining <= 0m)
            return cash < balanceDue - Tolerance ? new Plan(cash, 0m, null) : null;
        if (sellerRecordedWht is not decimal sellerWht || sellerWht <= 0m || singleWhtRatePercent is not decimal r || r <= 0m || r >= 100m) return null;
        var wht = Math.Round(cash * r / (100m - r), 2, MidpointRounding.AwayFromZero);
        if (Math.Abs(wht - sellerWht) > Tolerance) return null;
        if (wht > whtRemaining + Tolerance) return null;
        if (cash + wht >= balanceDue - Tolerance) return null;
        return new Plan(cash, wht, null);
    }
}
