namespace Accounting.Helpers;

/// <summary>
/// **สลิปโอนเข้าบัญชีบริษัท ⇒ บันทึกรับเงินเองได้ไหม — ตัวตัดสินเดียว** (รอบ 282 · คำตัดสิน 282-1..282-5 · pure · ไม่มี AI ⇒ kill-switch ปลอดภัยโดยโครงสร้าง)
/// <para>ผู้เรียกเดียว <c>SlipAutoRecordService.TryAutoAsync</c> หลังสลิปถูกผูกกับใบเดียวผ่านประตูแจ้งโอนเดิม (<c>SlipEvidenceService.SubmitAsync</c>) —
/// ทุกทางเข้าของสลิปค่าสินค้า/บริการ (แชต 1:1 / กลุ่มของ OA บริษัท · กลุ่มของ OA กลาง · อีเมลขาเข้า) เดินตัวนี้</para>
/// <para>ด่านครบทุกข้อ ⇒ บันทึกรับเงินผ่านเส้นยืนยันตัวเดียว (<c>SlipIntentConfirmService</c> → <c>DocumentPaymentHandler</c> → <c>CreatePaymentAsync</c>) ·
/// ข้อใดไม่ผ่าน ⇒ รายการรอยืนยันพร้อมเหตุ (<see cref="Verdict.Reason"/>) + แจ้งผู้ขาย — <b>ไม่มีขั้นใดประทับ "ชำระแล้ว" เองโดยไม่มีด่านครบหรือคนยืนยัน</b></para>
/// </summary>
public static class SlipAutoRecordRule
{
    public const string RuleCode = "SLIP-AUTO-RECORD";
    public const string LegalReference = "ป.รัษฎากร §78/1 (วันรับเงิน = จุดความรับผิด VAT บริการ) · พ.ร.บ.การบัญชี 2543 ม.8 · คำสั่งเจ้าของ 282";

    /// <summary>สลิปเก่ากว่านี้ (วันไทย) ไม่บันทึกเอง — อาจเป็นสลิปเก่าที่ส่งซ้ำ/ส่งผิดใบ</summary>
    public const int MaxSlipAgeDays = 31;

    public static class Codes
    {
        public const string Ok = "Ok";
        public const string SettingOff = "SettingOff";
        public const string SenderUnverified = "SenderUnverified";
        public const string NotSingle = "NotSingle";
        public const string NoQr = "NoQr";
        public const string DuplicateRef = "DuplicateRef";
        public const string NoDate = "NoDate";
        public const string DateOutOfRange = "DateOutOfRange";
        public const string MoneySource = "MoneySource";
        public const string ReceivingConflict = "ReceivingConflict";
        public const string ConfirmFailed = "ConfirmFailed";
        /// <summary>282-8: ด่านสลิปผ่านครบ แต่ยังไม่มีรายการเดินบัญชียืนยันเงินเข้า (สลิปอย่างเดียวห้ามบันทึกเอง)</summary>
        public const string NoBankLine = "NoBankLine";
        /// <summary>LOW (ฝ่ายค้าน 282): วันที่บนสลิปอยู่ในงวดบัญชีที่ปิด/เดือน ภ.พ.30 ที่ยื่นแล้ว</summary>
        public const string PeriodLocked = "PeriodLocked";
    }

    /// <param name="SettingOn">ค่าตั้งของบริษัทผู้ขาย <c>CompanySettings.SlipAutoRecordEnabled</c> (ค่าตั้งต้นปิด — คำตัดสิน 282-3)</param>
    /// <param name="SenderVerified">อีเมล: ผู้ส่งยืนยันโดเมนได้ (<see cref="InboundEmailRule.SenderVerified"/>) · LINE: true (ลายเซ็น webhook + การผูกกลุ่ม/ผู้ใช้)</param>
    /// <param name="SingleMatch">ตัวจับคู่ของ 260 (<see cref="LineChatSlipRule.Decide"/>) ได้ใบเดียวจากยอดที่มั่นใจ (ยอดตรงทุกสตางค์หลังหัก ณ ที่จ่ายตามใบ)</param>
    /// <param name="QrVerified">QR ตรวจสลิปถอดได้และ CRC ถูก (<see cref="ThaiSlipQr"/>) — เลขอ้างอิงที่ใช้กันซ้ำมาจากธนาคาร ไม่ใช่ OCR</param>
    /// <param name="SlipDateTh">วันที่บนสลิป (วันไทย) · null = อ่านไม่ได้</param>
    /// <param name="DocumentDateTh">วันที่ของเอกสาร (วันไทย) — รับเงินก่อนวันออกใบ = ผิดลำดับ ⇒ ให้คนตรวจ</param>
    /// <param name="Money">แหล่งเงินจากตัวตัดสินเดียว <see cref="SlipMoneySource.Decide"/> (ผู้รับบนสลิป)</param>
    /// <param name="ReceivingBank">บัญชีที่เส้นยืนยัน (<c>SlipReceivingAccount.ResolveAsync</c>) จะใช้ · null + <paramref name="ReceivingProblem"/> null = สืบทอดแหล่งเงินของเอกสาร</param>
    /// <param name="ReceivingDocChannelBank">เอกสารตั้งแหล่งเงินเป็นบัญชีธนาคารนี้ (สืบทอด — ต้องตรงบัญชีบนสลิป)</param>
    /// <param name="RefDuplicate">เลขอ้างอิงจาก QR ซ้ำกับรายการชำระอื่นของบริษัทที่รอตรวจ/รับเงินแล้ว (สลิปเดียวจ่ายสองใบไม่ได้)</param>
    /// <param name="PeriodLocked">วันที่บนสลิปอยู่ในงวดบัญชีที่ปิด หรือเดือน ภ.พ.30 ที่ยื่น/ล็อกแล้ว</param>
    /// <param name="BankLineConfirmed">282-8 (ฝ่ายค้าน HIGH-1): รายการเดินบัญชีของบัญชีบนสลิป ยอดตรง วันตรง ยังไม่จับคู่ <b>และ</b> ตัวตัดสินเงินเข้าเดียว
    /// (<see cref="BankReceiptAutoSettle"/>) ปิดหนี้ได้ — false = สลิปอย่างเดียว ⇒ ห้ามบันทึกเอง</param>
    public sealed record Facts(bool SettingOn, bool SenderVerified, bool SingleMatch, bool QrVerified, bool RefDuplicate, DateTime? SlipDateTh, DateTime DocumentDateTh,
        SlipMoneySource.Verdict Money, Guid? ReceivingBank, Guid? ReceivingDocChannelBank, string? ReceivingProblem, DateTime NowUtc,
        bool PeriodLocked = false, bool BankLineConfirmed = false);

    /// <summary>282-8: ป้ายตรง ๆ (หน้าตั้งค่า/หน้ายืนยัน/เหตุบนรายการ) — QR ไม่มียอด/บัญชี/ผู้รับ · CRC เป็นสูตรสาธารณะ ⇒ แต่งรูปได้</summary>
    public const string QrCaveat = "QR ตรวจได้แค่ว่าเป็นรูปแบบสลิป ไม่ได้ยืนยันว่าเงินเข้า";

    /// <summary>ด่านของสลิปผ่านครบไหม (ยังไม่นับรายการเดินบัญชี) — ผู้เรียกใช้ตัดสินว่าจะลองจับคู่รายการเดินบัญชีหรือไม่</summary>
    public static bool SlipReady(Facts f) => Decide(f with { BankLineConfirmed = true }).Auto;

    public sealed record Verdict(bool Auto, string Code, string Reason)
    {
        public bool UsedAi => false;
    }

    private static readonly TimeSpan Bangkok = TimeSpan.FromHours(7);

    public static Verdict Decide(Facts f)
    {
        if (!f.SettingOn) return No(Codes.SettingOff, "บริษัทยังไม่เปิด “บันทึกรับเงินจากสลิปเอง” — ตรวจและกดยืนยันที่หน้ารายการรับชำระ");
        if (!f.SenderVerified) return No(Codes.SenderUnverified, "ยืนยันผู้ส่งอีเมลไม่ได้ (SPF/DKIM ไม่ตรงโดเมน) — ระบบไม่บันทึกเอง");
        if (!f.SingleMatch) return No(Codes.NotSingle, "ยอดบนสลิปไม่ได้ตรงใบค้างใบเดียวอย่างมั่นใจ");
        if (!f.QrVerified) return No(Codes.NoQr, "ไม่พบ QR ตรวจสลิปของธนาคาร (หรือ CRC ไม่ถูก) — เลขอ้างอิงยืนยันไม่ได้");
        if (f.RefDuplicate) return No(Codes.DuplicateRef, "เลขอ้างอิงธนาคารของสลิปนี้ซ้ำกับรายการชำระอื่นที่รับไว้แล้ว — ตรวจก่อนยืนยัน");
        if (f.SlipDateTh is not DateTime slipDay) return No(Codes.NoDate, "อ่านวันที่บนสลิปไม่ได้ — ระบุวันรับเงินตอนยืนยัน");
        var today = (f.NowUtc + Bangkok).Date;
        if (slipDay.Date > today || slipDay.Date < today.AddDays(-MaxSlipAgeDays) || slipDay.Date < f.DocumentDateTh.Date)
            return No(Codes.DateOutOfRange, $"วันที่บนสลิป {slipDay:dd/MM/yyyy} อยู่นอกช่วงที่บันทึกเองได้ (ก่อนวันเอกสาร/อนาคต/เก่ากว่า {MaxSlipAgeDays} วัน)");
        if (f.PeriodLocked) return No(Codes.PeriodLocked, $"วันที่บนสลิป {slipDay:dd/MM/yyyy} อยู่ในงวดบัญชีที่ปิด/เดือน ภ.พ.30 ที่ยื่นแล้ว — ตรวจแล้วบันทึกเอง (ระบบไม่ลงย้อนงวดที่ปิด)");
        if (!f.Money.Auto) return No(Codes.MoneySource, f.Money.Reason);
        if (f.ReceivingProblem != null) return No(Codes.ReceivingConflict, f.ReceivingProblem);
        // บัญชีที่เส้นยืนยันจะลงต้องเป็นบัญชีเดียวกับที่สลิปชี้ (override ตรง หรือสืบทอดแหล่งเงินของเอกสารที่เป็นบัญชีนั้น)
        var lands = f.ReceivingBank ?? f.ReceivingDocChannelBank;
        if (lands != f.Money.AccountId)
            return No(Codes.ReceivingConflict, "บัญชีบนสลิปไม่ตรงแหล่งเงินที่ตั้งบนเอกสาร — หลักฐานขัดกัน ระบบไม่บันทึกเอง (เลือกบัญชีที่เงินเข้าจริง)");
        // 282-8 (ฝ่ายค้าน HIGH-1): สลิปอย่างเดียวห้ามบันทึกรับเงินเอง — ต้องมีรายการเดินบัญชียืนยันเงินเข้าจริง
        if (!f.BankLineConfirmed)
            return No(Codes.NoBankLine, QrCaveat + " — ยังไม่มีรายการเดินบัญชีที่ยืนยันเงินเข้า (ยอด/วัน/บัญชีตรง) · ตรวจรายการเงินเข้าแล้วกดยืนยัน (ค่าที่ระบบเสนอครบแล้ว) · "
                + "รายการเดินบัญชีเข้ามาภายหลังและตรง ⇒ ระบบตัดชำระให้เองตามค่าตั้งตัดชำระจากรายการเดินบัญชี");
        return new Verdict(true, Codes.Ok, "ผ่านทุกด่าน: ยอดตรงใบเดียว · รายการเดินบัญชียืนยันเงินเข้า · " + f.Money.Reason);
    }

    private static Verdict No(string code, string reason) => new(false, code, reason);

    /// <summary>ป้ายช่องทาง (server computes)</summary>
    public static string ChannelLabel(string? sourceKind) => sourceKind switch
    {
        LineChatSlipRule.Sources.CentralGroup => "กลุ่ม LINE ของ NextAcc",
        LineChatSlipRule.Sources.Email => "อีเมล",
        "group" => "กลุ่ม LINE ของบริษัท",
        _ => "แชต LINE ของบริษัท",
    };
}
