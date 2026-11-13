namespace Accounting.Helpers;

/// <summary>
/// รอบ 264 P4 (team-264-a2b-design §6.1/§6.2 · คำตัดสิน 264-5-11 · 264-5-12 · 264-5-13) — <b>กติกาเดียวของ "ผู้ซื้อเป็นผู้เริ่มจ่าย" + การล้างผลอ่านสลิปฝั่งผู้ซื้อ</b> · pure ·
/// ไม่มีจุดเรียก AI (ตัวเลข/สถานะล้วน) ⇒ ไม่มี AiFeatureKey
/// <list type="bullet">
/// <item><b>ไม่มีสถานะ "ชำระแล้ว"</b> บนแผน (F2 ข้อ 3) — ผู้ขายรับเงินเมื่อผู้ให้บริการ/ผู้ขายยืนยันจริง (การแจ้งที่ตามมา) · PV ของผู้ซื้อในเส้นแจ้งโอนคือการบันทึกของ<b>คน</b>ที่แนบสลิปของตัวเอง</item>
/// <item>บัญชีที่เงินออก: <b>คนเลือกเสมอ</b> — สลิปเป็นตัวตรวจไขว้ผ่านตัวตัดสินแหล่งเงินตัวเดียว (<see cref="PartnerPaySourceAccount.Decide"/>: ผู้จ่ายเลือก × สลิป ขัดกัน ⇒ ปฏิเสธ)</item>
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

    /// <summary>เลขอ้างอิงของ PV ฝั่งผู้ซื้อในเส้นแจ้งโอน — กุญแจกันซ้ำ (canonical ตัวเดียว: ผู้เขียน + ผู้ตรวจ)</summary>
    public static string BuyerReference(Guid planId) => "PARTNER-PLAN-" + planId.ToString("N");

    /// <summary>ข้อจำกัดที่ต้องบอกบนปุ่ม "จ่ายจากระบบของเรา" (design §6.1)</summary>
    public const string GatewayCaveat = "พร้อมเพย์สแกนด้วยบัญชีอื่นได้ — ถ้าไม่ตรง รายการเดินบัญชีของเราจะชนะบัญชีที่เลือกไว้";

    public const string NoLinkReason = "ใบซื้อนี้ไม่ได้มาจากคู่ค้า NextAcc (ไม่มีลิงก์ใบแจ้งหนี้ของผู้ขาย) — บันทึกจ่ายที่หน้าเอกสารตามปกติ";
    public const string PartnerNotAcceptedReason = "คู่ค้ารายนี้ไม่ได้เชื่อมกันแล้ว (ระงับ/ยกเลิก) — จ่ายผ่านช่องทางที่ผู้ขายแจ้งโดยตรง";
    public const string NotApprovedReason = "ใบซื้อยังไม่อนุมัติ หรือไม่มียอดค้าง — อนุมัติใบซื้อก่อน";
    public const string AccountUnusableReason = "บัญชีที่เลือกใช้จ่ายไม่ได้ (ไม่ใช่ของบริษัท · ปิดใช้งาน · ไม่ใช่บาท · ฝากประจำ · ไม่ผูกผังที่ลงได้) — เลือกบัญชีอื่น";
    public const string NoAccountReason = "เลือกบัญชีที่เงินออกจริง — ระบบไม่เดาบัญชีให้";
    public const string ConsentNeededPrefix = "ปุ่มนี้ใช้ได้เมื่อบริษัทของเราให้ความยินยอม “บันทึกจ่ายเอง” กับคู่ค้ารายนี้ (ที่หน้า “งานอัตโนมัติกับคู่ค้า”) — ";
    public const string GatewayFallback = " · ระหว่างนี้จ่ายผ่านลิงก์ชำระที่ผู้ขายส่งมา หรือใช้ “แจ้งโอนให้ผู้ขาย”";
    public const string TooManyStartsReason = "เปิดรายการชำระของใบนี้ถี่เกินไป — รอสักครู่แล้วลองใหม่ หรือใช้ “แจ้งโอนให้ผู้ขาย”";
    public const string SlipCopySent = "ส่งสำเนาสลิปเข้าคิวรอยืนยันของผู้ขายแล้ว (ผู้ขายยังต้องยืนยันรับเงิน)";
    public const string SlipCopyNotConsentedPrefix = "ไม่ได้ส่งสำเนาสลิปให้ผู้ขาย — ผู้ขายยังไม่เปิดรับสำเนาสลิปจากคู่ค้า (";
    public const string SlipCopyFailedPrefix = "ไม่ได้ส่งสำเนาสลิปให้ผู้ขาย — ";

    /// <summary>ด่านร่วมก่อนเริ่มจ่ายทั้งสองเส้น (ลำดับ = ลำดับที่ผู้ใช้แก้ได้) — null = ผ่าน</summary>
    public static string? StartProblem(bool linkFound, bool partnershipAccepted, bool purchaseApproved, decimal balanceDue)
    {
        if (!linkFound) return NoLinkReason;
        if (!partnershipAccepted) return PartnerNotAcceptedReason;
        if (!purchaseApproved || balanceDue <= PartnerPaymentNoticeRule.Tolerance) return NotApprovedReason;
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

    // ═══════════ ฝ่ายค้าน 264-P2 L3 (คำตัดสิน 264-5-13): ล้างผลอ่านสลิปฝั่งผู้ซื้อ ═══════════

    /// <summary>การแจ้งจบแล้ว (ไม่มีผู้อ่านเลขอีก)</summary>
    private static bool NoticeTerminal(string? status)
        => status is PartnerPaymentNoticeRule.StatusRecorded or PartnerPaymentNoticeRule.StatusDismissed or PartnerPaymentNoticeRule.StatusWithdrawn;

    /// <summary>ล้างธนาคาร + เลขปิดบางของผู้โอนบนการแจ้งไหม: ผู้โอนไม่ใช่บัญชีของบริษัท (ข้อมูลของบุคคลที่สาม — กรรมการ/ญาติ/พนักงาน) หรือการแจ้งจบแล้ว ·
    /// ยังเปิดและเป็นบัญชีของเรา (Unique/Many/อ่านไม่ได้) ⇒ คงไว้ (ข้อมูลของ B กลับหา B · ตัวตัดสินยังต้องใช้)</summary>
    public static bool ShouldClearSlipPayer(PartnerPaySourceAccount.SlipKind? kind, string? status)
        => kind == PartnerPaySourceAccount.SlipKind.NotOurs || NoticeTerminal(status);

    /// <summary>ผลที่เก็บแทนเลขหลังล้าง ⇒ ผลของชั้นสลิป (ตัวอ่านเดียว — <c>PartnerPayablePipeline.SlipMatchAsync</c>) · NotOurs คงผล (ยังเปิดตัวเลือก "กรรมการจ่ายแทน") · อื่น ๆ ไม่มีชั้น</summary>
    public static PartnerPaySourceAccount.Slip? SlipFromStoredKind(string? stored)
        => stored == nameof(PartnerPaySourceAccount.SlipKind.NotOurs) ? new PartnerPaySourceAccount.Slip(PartnerPaySourceAccount.SlipKind.NotOurs) : null;
}
