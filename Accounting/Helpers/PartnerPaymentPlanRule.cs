using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 264 P4 (team-264-a2b-design §6.1/§6.2 · คำตัดสิน 264-5-11 · 264-5-12 · 264-5-13 · ฝ่ายค้าน review-264-P4 ⇒ 264-5-14…18) —
/// <b>กติกาเดียวของ "ผู้ซื้อเป็นผู้เริ่มจ่าย" + การล้างผลอ่านสลิปฝั่งผู้ซื้อ</b> · pure · ไม่มีจุดเรียก AI (ตัวเลข/สถานะล้วน) ⇒ ไม่มี AiFeatureKey
/// <list type="bullet">
/// <item><b>ไม่มีสถานะ "ชำระแล้ว"</b> บนแผน (F2 ข้อ 3) — ผู้ขายรับเงินเมื่อผู้ให้บริการ/ผู้ขายยืนยันจริง (การแจ้งที่ตามมา) · PV ของผู้ซื้อในเส้นแจ้งโอนคือการบันทึกของ<b>คน</b>ที่แนบสลิปของตัวเอง</item>
/// <item>บัญชีที่เงินออก: <b>คนเลือกเสมอ</b> — สลิปเป็นตัวตรวจไขว้ผ่านตัวตัดสินแหล่งเงินตัวเดียว (<see cref="PartnerPaySourceAccount.Decide"/>: ผู้จ่ายเลือก × สลิป ขัดกัน ⇒ ปฏิเสธ)</item>
/// <item>แจ้งโอน: กุญแจกันซ้ำคงที่ต่อ (เอกสารของผู้ขาย · แฮชไฟล์สลิป · ยอด) — <see cref="TransferIdempotencyKey"/> (H1)</item>
/// </list>
/// </summary>
public static class PartnerPaymentPlanRule
{
    public const string RuleCode = "XT-PARTNER-PLAN";
    public const string LegalReference = "ท.ป.4/2528 (50 ทวิ ลงวันจ่าย) · พ.ร.บ.การบัญชี 2543 ม.14 (เอกสารประกอบ)";

    public const string KindGateway = "Gateway";
    public const string KindTransfer = "Transfer";

    /// <summary>เพดานการเปิดรายการชำระต่อใบซื้อต่อชั่วโมง (แบบเดียวกับหน้าชำระสาธารณะ — กันกดรัว)</summary>
    public const int MaxStartsPerHour = 5;

    /// <summary>เลขอ้างอิงของ PV ฝั่งผู้ซื้อในเส้นแจ้งโอน (canonical ตัวเดียว: ผู้เขียน + ผู้ตรวจ — ตัวหา PV ของแผนใช้ทั้งค่านี้และ <c>BuyerPaymentId</c>)</summary>
    public static string BuyerReference(Guid planId) => "PARTNER-PLAN-" + planId.ToString("N");

    /// <summary>ฝ่ายค้าน 264-P4 H1 (คำตัดสิน 264-5-15): กุญแจกันซ้ำของ "แจ้งโอน" — คงที่ต่อ (เอกสารของผู้ขาย · แฮชไฟล์สลิป · ยอด) ·
    /// กดซ้ำ/แนบสลิปเดิมอีกครั้งด้วยยอดเดิม ⇒ กุญแจเดิม ⇒ unique index ⇒ PV ใบเดียว · สลิปต่างกันยอดเท่ากัน ⇒ กุญแจต่างกัน (ใบถูกไม่ถูกกัน)</summary>
    public static string TransferIdempotencyKey(Guid sourceDocumentId, string slipSha256, decimal amount)
    {
        var canonical = string.Join("|", "v1", sourceDocumentId.ToString("N"), (slipSha256 ?? "").Trim().ToLowerInvariant(),
            Math.Round(amount, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>ข้อจำกัดที่ต้องบอกบนปุ่ม "จ่ายจากระบบของเรา" (design §6.1 · ฝ่ายค้าน M4)</summary>
    public const string GatewayCaveat = "โมบาย/อินเทอร์เน็ตแบงก์กิ้งผูกบัญชีที่เลือก ⇒ ระบบบันทึกจ่ายเองได้เมื่อผู้ขายยืนยัน (ถ้าให้ความยินยอมไว้) · "
        + "พร้อมเพย์/TrueMoney สแกนจากบัญชีอื่นได้ ⇒ ระบบเลือกบัญชีไว้ให้ แต่ท่านต้องกดยืนยันเอง · รายการเดินบัญชีของเราชนะบัญชีที่เลือกเสมอ";

    /// <summary>ฝ่ายค้าน 264-P4 M4 (คำตัดสิน 264-5-17): ช่องทางที่ผูกบัญชีต้นทาง (ธนาคารยืนยันการตัดเงินจากบัญชีที่ล็อกอิน) — อื่น ๆ (พร้อมเพย์ · วอลเล็ต) ⇒ PayerDeclared ไม่บันทึกเอง</summary>
    public static readonly IReadOnlyList<PaymentMethodKind> AccountBoundMethods = new[] { PaymentMethodKind.MobileBanking, PaymentMethodKind.InternetBanking };

    /// <summary>วิธีของแผน (ชื่อ <c>PaymentMethodKind</c>) ผูกบัญชีต้นทางไหม · ชื่อที่ไม่รู้จัก ⇒ ไม่ผูก (ไม่เดา)</summary>
    public static bool MethodBindsAccount(string? method)
        => Enum.TryParse<PaymentMethodKind>(method, false, out var k) && AccountBoundMethods.Contains(k);

    public const string NoLinkReason = "ใบซื้อนี้ไม่ได้มาจากคู่ค้า NextAcc (ไม่มีลิงก์ใบแจ้งหนี้ของผู้ขาย) — บันทึกจ่ายที่หน้าเอกสารตามปกติ";
    public const string PartnerNotAcceptedReason = "คู่ค้ารายนี้ไม่ได้เชื่อมกันแล้ว (ระงับ/ยกเลิก) — จ่ายผ่านช่องทางที่ผู้ขายแจ้งโดยตรง";
    public const string NotApprovedReason = "ใบซื้อยังไม่อนุมัติ หรือไม่มียอดค้าง — อนุมัติใบซื้อก่อน";
    public const string AccountUnusableReason = "บัญชีที่เลือกใช้จ่ายไม่ได้ (ไม่ใช่ของบริษัท · ปิดใช้งาน · ไม่ใช่บาท · ฝากประจำ · ไม่ผูกผังที่ลงได้) — เลือกบัญชีอื่น";
    public const string NoAccountReason = "เลือกบัญชีที่เงินออกจริง — ระบบไม่เดาบัญชีให้";
    public const string GatewayFallback = " · ใช้ “แจ้งโอนให้ผู้ขาย” แทนได้";
    public const string TooManyStartsReason = "เปิดรายการชำระของใบนี้ถี่เกินไป — รอสักครู่แล้วลองใหม่ หรือใช้ “แจ้งโอนให้ผู้ขาย”";
    public const string SlipCopySent = "ส่งสำเนาสลิปเข้าคิวรอยืนยันของผู้ขายแล้ว (ผู้ขายยังต้องยืนยันรับเงิน)";
    public const string SlipCopyPendingNote = "กำลังส่งสำเนาสลิปให้ผู้ขาย (ระบบลองส่งต่อเองถ้ายังไม่สำเร็จ)";
    public const string SlipCopyFailedPrefix = "ไม่ได้ส่งสำเนาสลิปให้ผู้ขาย — ";
    public const string DuplicateTransferPrefix = "แจ้งโอนนี้บันทึกไปแล้ว (สลิปเดียวกัน ยอดเดียวกัน) — ไม่บันทึกจ่ายซ้ำ · ";

    // ═══════════ ฝ่ายค้าน 264-P4 M2: outbox สำเนาสลิป (สถานะบนแถวแผน) ═══════════

    public const string CopyPending = "Pending";
    public const string CopySent = "Sent";
    public const string CopySkipped = "Skipped";
    public const string CopyFailed = "Failed";
    /// <summary>ลองส่งสำเนาสลิปซ้ำได้กี่ครั้ง (ล้มชั่วคราว: ที่เก็บไฟล์/ฐานข้อมูลของผู้ขาย) ก่อนเป็น Failed + เหตุ</summary>
    public const int MaxCopyAttempts = 5;

    /// <summary>ฝ่ายค้าน 264-P4 M7 (คำตัดสิน 264-5-14): ข้อความ<b>มุมผู้ซื้อ</b>เมื่อผู้ขายยังไม่เปิดรับสลิป — ไม่ส่งเหตุดิบของผู้ขาย (เช่น "ยืนยันใหม่เพื่อเริ่มใช้") ให้ผู้ซื้อ
    /// ซึ่งทำอะไรกับมันไม่ได้ · ฝั่งผู้ขายได้กระดิ่งพร้อมเหตุของเขาเอง</summary>
    public static string SellerNotAcceptingNoteForBuyer(string? code) => SlipCopyFailedPrefix
        + (code is PartnerConsent.Codes.NoConsent or PartnerConsent.Codes.Withdrawn
            ? "ผู้ขายยังไม่เปิดรับสลิปจากคู่ค้าในระบบ NextAcc"
            : "ผู้ขายต้องยืนยันความยินยอมรับสลิปจากคู่ค้าใหม่ที่หน้า “งานอัตโนมัติกับคู่ค้า” ของผู้ขาย (ระบบแจ้งผู้ขายแล้ว)")
        + " — PV ของเราบันทึกแล้ว · แจ้งผู้ขายเองพร้อมสลิป";

    /// <summary>ผู้ขายต้องทำอะไรเองไหม (ยืนยันใหม่/ผู้ให้ไม่มีสิทธิ์แล้ว/ให้ใหม่หลังเชื่อมกลับ) ⇒ แจ้งผู้ขาย · ไม่เคยให้ = แจ้งครั้งแรกด้วย (ผู้ขายไม่รู้ว่ามีสลิปรออยู่)</summary>
    public static bool SellerShouldBeNotified(string? code)
        => code is PartnerConsent.Codes.NoConsent or PartnerConsent.Codes.TextOutdated or PartnerConsent.Codes.GrantedBeforeWired
            or PartnerConsent.Codes.GrantorLostPermission or PartnerConsent.Codes.StaleAfterSuspend or PartnerConsent.Codes.Tampered;

    /// <summary>แจ้งผู้ขายซ้ำได้ห่างกันอย่างน้อยเท่านี้ต่อคู่ค้า (ไม่สแปม)</summary>
    public static readonly TimeSpan SellerNoticeInterval = TimeSpan.FromDays(7);

    /// <summary>ด่านร่วมก่อนเริ่มจ่ายทั้งสองเส้น (ลำดับ = ลำดับที่ผู้ใช้แก้ได้) — null = ผ่าน</summary>
    public static string? StartProblem(bool linkFound, bool partnershipAccepted, bool purchaseApproved, decimal balanceDue)
    {
        if (!linkFound) return NoLinkReason;
        if (!partnershipAccepted) return PartnerNotAcceptedReason;
        if (!purchaseApproved || balanceDue <= PartnerPaymentNoticeRule.Tolerance) return NotApprovedReason;
        return null;
    }

    /// <summary>ฝ่ายค้าน 264-P4 M5 (คำตัดสิน 264-5-16): ยอดที่ผู้ขายเรียกเก็บผ่านช่องทางออนไลน์ เทียบกับแผนของ<b>เรา</b> (ตัวคิดยอด/ภาษีหักตัวเดียว
    /// <c>PartnerPaymentNoticeService.PlanForAsync</c> บนยอดของผู้ขาย) — แผนมีเหตุ/คำเตือน (เช่น ผู้ขายไม่หักภาษี ณ ที่จ่ายที่เราต้องหัก) หรือยอดไม่เท่า ⇒ ปิดปุ่ม · null = ใช้ได้</summary>
    public static string? GatewayAmountProblem(decimal sellerAmountDue, PartnerPaymentNoticeRule.Plan buyerPlan)
    {
        if (buyerPlan.Problem != null) return "ยอดที่ผู้ขายเรียกเก็บออนไลน์ไม่ตรงยอดค้างของเรา: " + buyerPlan.Problem;
        if (buyerPlan.Warning != null || buyerPlan.Amount is not decimal a || Math.Abs(a - sellerAmountDue) > PartnerPaymentNoticeRule.Tolerance)
            return $"ยอดที่ผู้ขายเรียกเก็บออนไลน์ ({sellerAmountDue:N2} บาท) ไม่หักภาษี ณ ที่จ่ายตามกติกาของเรา — จ่ายผ่านช่องทางนี้ไม่ได้ (เราต้องหัก/นำส่งเอง)";
        return null;
    }

    /// <summary>ลิงก์/รูป QR ที่ส่งให้หน้าแสดงได้ (ฝ่ายค้าน L8): รูป data ของภาพแบบ base64 หรือ https เท่านั้น · อื่น ๆ ⇒ null (หน้าไม่แสดง)</summary>
    public static string? SafeQrPayload(string? qr)
    {
        if (string.IsNullOrWhiteSpace(qr)) return null;
        var v = qr.Trim();
        if (v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return v;
        foreach (var t in new[] { "png", "jpeg", "jpg", "gif", "webp" })
            if (v.StartsWith("data:image/" + t + ";base64,", StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    /// <summary>บัญชีของเส้นแจ้งโอน: บัญชีที่คนเลือก ตรวจไขว้กับสลิปผ่านตัวตัดสินแหล่งเงินตัวเดียว · คืน (บัญชี, ที่มา, เหตุที่ปฏิเสธ)</summary>
    public static (Guid? BankAccountId, PartnerPaySourceAccount.Origin Origin, string? Problem) TransferAccount(
        IReadOnlyDictionary<Guid, string> usable, Guid? chosen, PartnerPaySourceAccount.Slip? slip, string? chooserName)
    {
        if (chosen is not Guid c) return (null, PartnerPaySourceAccount.Origin.Unknown, NoAccountReason);
        if (!usable.ContainsKey(c)) return (null, PartnerPaySourceAccount.Origin.Unknown, AccountUnusableReason);
        var d = PartnerPaySourceAccount.Decide(new PartnerPaySourceAccount.Facts(usable, PayerDeclared: c, PayerName: chooserName, SlipMatch: slip));
        return d.BankAccountId == c ? (c, d.Origin, null) : (null, d.Origin, d.Reason);
    }

    // ═══════════ ฝ่ายค้าน 264-P4 M3 (คำตัดสิน 264-5-18): การแจ้งที่ตามมา ⇒ PV ของแผนแจ้งโอนที่มีอยู่ ═══════════

    /// <summary>แผนแจ้งโอนหนึ่งแถวที่มี PV ยังมีผล (ผู้เรียกโหลด: บริษัทเดียวกัน · ใบซื้อเดียวกัน · แผนไม่ถูกลบ) · <paramref name="Claimed"/> = PV ถูกการแจ้งอื่นอ้างไปแล้ว</summary>
    public sealed record TransferPlanFact(Guid PlanId, Guid PaymentId, Guid? SellerIntentId, decimal Amount, DateTime? TransferredAt, bool Claimed);

    /// <summary>กรอบวันที่ที่การแจ้งของผู้ขายจับคู่แผนแจ้งโอนที่ไม่มีรายการชำระของผู้ขาย (วันโอน ↔ วันที่ผู้ขายลงรับ)</summary>
    public const int PlanMatchDays = 3;

    /// <summary>PV ที่การแจ้ง (เงิน · วันที่ · รายการชำระของผู้ขาย) ควรปิดด้วย: (1) แผนที่ผูกรายการชำระเดียวกัน · (2) ไม่มีผูก ⇒ แผนแจ้งโอนที่ผู้ขายไม่ได้รับสำเนาสลิป
    /// (ไม่มีรายการของผู้ขาย) ยังไม่ถูกการแจ้งอื่นอ้าง ยอดเท่ากัน วันห่าง ≤ <see cref="PlanMatchDays"/> และมี<b>ตัวเดียว</b> · หลายตัว/ไม่มี ⇒ null (ไม่เดา — ปุ่มเดียวเตือน)</summary>
    public static Guid? PlanPaymentFor(decimal noticeCash, DateTime noticePaidAt, Guid? noticeIntentId, IReadOnlyList<TransferPlanFact> plans)
    {
        if (noticeIntentId is Guid intent && plans.FirstOrDefault(p => p.SellerIntentId == intent) is { } exact) return exact.PaymentId;
        var cash = Math.Round(noticeCash, 2, MidpointRounding.AwayFromZero);
        var hits = plans.Where(p => p.SellerIntentId == null && !p.Claimed && Math.Round(p.Amount, 2, MidpointRounding.AwayFromZero) == cash
            && p.TransferredAt is DateTime t && Math.Abs((t.Date - noticePaidAt.AddHours(7).Date).TotalDays) <= PlanMatchDays).ToList();
        return hits.Count == 1 ? hits[0].PaymentId : null;
    }

    // ═══════════ ฝ่ายค้าน 264-P2 L3 (คำตัดสิน 264-5-13): ล้างผลอ่านสลิปฝั่งผู้ซื้อ ═══════════

    /// <summary>การแจ้งจบแล้ว (ไม่มีผู้อ่านเลขอีก)</summary>
    private static bool NoticeTerminal(string? status)
        => status is PartnerPaymentNoticeRule.StatusRecorded or PartnerPaymentNoticeRule.StatusDismissed or PartnerPaymentNoticeRule.StatusWithdrawn;

    /// <summary>ล้างธนาคาร + เลขปิดบางของผู้โอนบนการแจ้งไหม: ผู้โอนไม่ใช่บัญชีของบริษัท (ข้อมูลของบุคคลที่สาม) · <b>อ่านไม่ได้</b> (ฝ่ายค้าน 264-P4 L9ก: ไม่รู้ว่าเป็นบัญชีของใคร
    /// และตัวตัดสินไม่ใช้ผลนี้) · หรือการแจ้งจบแล้ว ⇒ ล้าง · ยังเปิดและเป็นบัญชีของเรา (Unique/Many) ⇒ คงไว้ (ตัวตัดสินยังต้องใช้)</summary>
    public static bool ShouldClearSlipPayer(PartnerPaySourceAccount.SlipKind? kind, string? status)
        => kind is PartnerPaySourceAccount.SlipKind.NotOurs or PartnerPaySourceAccount.SlipKind.Unreadable || NoticeTerminal(status);

    /// <summary>ผลที่เก็บแทนเลขหลังล้าง ⇒ ผลของชั้นสลิป (ตัวอ่านเดียว — <c>PartnerPayablePipeline.SlipMatchAsync</c>) · NotOurs คงผล (ยังเปิดตัวเลือก "กรรมการจ่ายแทน") · อื่น ๆ ไม่มีชั้น</summary>
    public static PartnerPaySourceAccount.Slip? SlipFromStoredKind(string? stored)
        => stored == nameof(PartnerPaySourceAccount.SlipKind.NotOurs) ? new PartnerPaySourceAccount.Slip(PartnerPaySourceAccount.SlipKind.NotOurs) : null;
}
