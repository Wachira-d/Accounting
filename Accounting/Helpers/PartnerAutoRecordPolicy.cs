using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 264 P3 (team-264-a2b-design §5.1 · คำตัดสิน 264-3-2/264-3-4) — <b>ด่านเดียวของ "ระบบของผู้ซื้อบันทึกจ่ายใบซื้อเอง"</b> ตามการแจ้ง "ผู้ขายคู่ค้ารับเงินแล้ว" · pure
/// <para>อัตโนมัติได้เมื่อ<b>ทุกช่องมีหลักฐาน</b>: ความยินยอม <c>AutoRecordPayment</c> ของผู้ซื้อใช้ได้ (ตรวจสดในธุรกรรม — <c>PartnerConsentService.CheckForWriteAsync</c>) ·
/// การแจ้งยังรอบันทึก · การรับชำระของผู้ขายยังไม่ถูกยกเลิก (ตรวจสด) · ใบของผู้ขายยังไม่ถูกยกเลิก · ใบซื้ออนุมัติแล้ว · ยอด/ภาษีหักจาก
/// <see cref="PartnerPaymentNoticeRule.For"/> โดย<b>ไม่มีคำเตือน</b> (B3 — จ่ายเต็มทั้งที่ต้องหัก ⇒ ปุ่มเดียวเสมอ) · บาทเท่านั้น · ไม่ใช่ภาษีออกแทน ·
/// แหล่งเงินออกมีหลักฐาน (<see cref="PartnerPaySourceAccount.Decide"/> — <c>AllowsAuto</c>)</para>
/// <para>ไม่ผ่าน ⇒ <see cref="Kind.OneClick"/> (กลับเป็นปุ่ม "บันทึกจ่ายตามที่ผู้ขายรับแล้ว" เดิม + เหตุ) หรือ <see cref="Kind.Refuse"/> (บันทึกจ่ายจากการแจ้งนี้ไม่ได้เลย) ·
/// งวดบัญชีปิด/เส้นจ่ายปฏิเสธ = ตัวบันทึกจ่ายเดิมตัดสิน (ตัวตรวจงวดตัวเดียวกับเส้นจ่าย) แล้วกลับเป็นปุ่มเดียวพร้อมข้อความของมัน ·
/// ไม่มีจุดเรียก AI (กฎ #1: kill-switch ผ่านโดยโครงสร้าง)</para>
/// </summary>
public static class PartnerAutoRecordPolicy
{
    public const string RuleCode = "XT-PARTNER-AUTO-PAY";
    public const string LegalReference = "ป.รัษฎากร ม.50 · ม.69 ทวิ/ตรี (หน้าที่หัก ณ ที่จ่ายของผู้จ่าย) · ท.ป.4/2528 (50 ทวิ ลงวันจ่าย) · ความรับผิดชอบรายการอยู่กับผู้ให้ความยินยอมของบริษัทผู้ซื้อ";

    public enum Kind { Auto, OneClick, Refuse }

    /// <summary>รหัสเหตุ (ชื่อคงที่ — audit "ครั้งแรกต่อเหตุ" เทียบรหัสนี้)</summary>
    public static class Codes
    {
        public const string Ok = "Ok";
        public const string NoticeClosed = "NoticeClosed";
        public const string SellerVoided = "SellerVoided";
        public const string SellerPaymentUnknown = "SellerPaymentUnknown";
        public const string SourceVoided = "SourceVoided";
        public const string Consent = "Consent";
        public const string Plan = "Plan";
        public const string PlanWarning = "PlanWarning";
        public const string Currency = "Currency";
        public const string Abbreviated = "Abbreviated";
        public const string PayerBorne = "PayerBorne";
        public const string VatMismatch = "VatMismatch";
        public const string Source = "Source";
        public const string PostFailed = "PostFailed";
        public const string OtherPayment = "OtherPayment";
        public const string WhtPeriod = "WhtPeriod";
        /// <summary>ใบซื้อปิดแล้ว/ถูกยกเลิก/หาไม่พบ — เหตุถาวร (ท่อประเมินซ้ำห่าง ๆ)</summary>
        public const string PlanFinal = "PlanFinal";

        /// <summary>ฝ่ายค้าน M7: เหตุที่ไม่หายเองในไม่กี่นาที ⇒ ท่อประเมินซ้ำวันละครั้ง (ไม่ใช่ทุก 15 นาที)</summary>
        public static readonly IReadOnlyList<string> Slow = new[]
        {
            SourceVoided, SellerPaymentUnknown, Currency, Abbreviated, PayerBorne, VatMismatch, OtherPayment, WhtPeriod, PlanFinal, PlanWarning,
        };
    }

    /// <param name="NoticeOpen">การแจ้งยังรอบันทึก (ไม่ถูกปิด/ถอน/บันทึกแล้ว)</param>
    /// <param name="SellerPaymentKnown">แถวแจ้งผูกการรับชำระของผู้ขาย (<c>SellerPaymentId</c>) — แถวเก่าที่ migration หาไม่พบ ⇒ false (ไม่อัตโนมัติ · ปุ่มเดียวยังใช้ได้)</param>
    /// <param name="SellerPaymentLive">การรับชำระของผู้ขายยังมีผล ณ ตอนนี้ (ตรวจสดในฐาน — ไม่ถูกยกเลิก)</param>
    /// <param name="SourceDocumentVoided">ใบของผู้ขายถูกยกเลิกแล้ว</param>
    /// <param name="ConsentOk">ผล <c>CheckForWriteAsync(AutoRecordPayment)</c></param>
    /// <param name="ConsentReason">เหตุจากตัวตรวจความยินยอม (โชว์ตรง)</param>
    /// <param name="Plan">ยอด/ภาษีหักของผู้ซื้อ (<see cref="PartnerPaymentNoticeRule.For"/>)</param>
    /// <param name="Currency">สกุลของใบซื้อ</param>
    /// <param name="SourceIsAbbreviated">ใบของผู้ขายเป็นใบกำกับอย่างย่อ (264-2-14)</param>
    /// <param name="PayerBorneWht">ใบซื้อเป็นภาษีออกแทน (ผู้จ่ายรับภาระภาษีหัก) — ตัวคิดของเราไม่ยืนยันการจ่ายผ่านลิงก์</param>
    /// <param name="VatMismatch">VAT ของใบผู้ขายไม่เท่า VAT บนใบซื้อ</param>
    /// <param name="Source">ผลตัวตัดสินแหล่งเงิน</param>
    /// <param name="UnexplainedPurchasePayment">ฝ่ายค้าน H1: ใบซื้อมีการจ่ายที่ยังมีผลซึ่ง<b>ไม่ได้มาจากการแจ้งที่บันทึกแล้วและผู้ขายยังไม่ยกเลิก</b>
    /// (บันทึกเอง · หรือมาจากการแจ้งที่ผู้ขายยกเลิกแล้วบันทึกรับใหม่) ⇒ การแจ้งนี้อาจเป็นเงินก้อนเดียวกัน ⇒ ไม่บันทึกเอง</param>
    /// <param name="WhtDateInPastMonth">ฝ่ายค้าน M4: มีภาษีหัก และวันที่จ่าย (ที่ผู้ขายกรอก) อยู่ในเดือนภาษีก่อนเดือนปัจจุบัน ⇒ 50 ทวิ/ภ.ง.ด.3/53 จะย้อนเข้าเดือนที่อาจยื่นแล้ว ⇒ ให้คนเลือกวันที่</param>
    public sealed record Facts(
        bool NoticeOpen,
        bool SellerPaymentKnown,
        bool SellerPaymentLive,
        bool SourceDocumentVoided,
        bool ConsentOk,
        string? ConsentReason,
        PartnerPaymentNoticeRule.Plan Plan,
        string? Currency,
        bool SourceIsAbbreviated,
        bool PayerBorneWht,
        bool VatMismatch,
        PartnerPaySourceAccount.Decision Source,
        bool UnexplainedPurchasePayment = false,
        bool WhtDateInPastMonth = false);

    public sealed record Verdict(Kind Kind, string Code, string Reason)
    {
        public bool Auto => Kind == Kind.Auto;
    }

    private static Verdict One(string code, string reason) => new(Kind.OneClick, code, reason);

    /// <summary>ฝ่ายค้าน L8: ระบบรู้แค่ว่าผู้ขายยกเลิก<b>รายการ</b> — ไม่รู้ว่าเงินออกจริงหรือไม่ (ผู้ขายอาจแค่แก้รายการแล้วบันทึกรับใหม่) ⇒ ไม่สรุปแทน</summary>
    public const string SellerVoidedReason = "ผู้ขายยกเลิกรายการรับชำระนี้แล้ว — บันทึกจ่ายจากการแจ้งนี้ไม่ได้ · ตรวจกับผู้ขายก่อน (ผู้ขายอาจบันทึกรับใหม่ = จะมีการแจ้งใหม่ · ถ้าเงินออกจากบัญชีเราแล้วและผู้ขายไม่ได้รับ ให้ขอคืน)";

    public const string OtherPaymentReason = "ใบซื้อนี้มีการจ่ายที่ไม่ได้มาจากการแจ้งของผู้ขายที่ยังมีผล (บันทึกเอง หรือผู้ขายยกเลิกแล้วบันทึกรับใหม่) — อาจเป็นเงินก้อนเดียวกัน ระบบไม่บันทึกเอง · ตรวจก่อนกดบันทึกจ่าย (ถ้าซ้ำให้ปิดการแจ้ง)";

    public const string WhtPeriodReason = "การจ่ายนี้มีภาษีหัก ณ ที่จ่าย และวันที่ผู้ขายรับเงินอยู่ในเดือนภาษีก่อนหน้า — ภ.ง.ด.3/53 ของเดือนนั้นอาจยื่นไปแล้ว/เลยกำหนด ระบบไม่เลือกวันที่ให้ · กดบันทึกจ่ายเองแล้วตรวจแบบที่ยื่น (ยื่นเพิ่มเติมถ้าจำเป็น)";

    /// <summary>ฝ่ายค้าน M4 (pure): มีภาษีหัก และเดือน (เวลาไทย) ของวันที่จ่ายก่อนเดือนปัจจุบัน · ไม่มีภาษีหัก = ไม่เกี่ยว</summary>
    public static bool WhtDateInPastMonthOf(DateTime paidAtUtc, decimal? planWht, DateTime nowUtc)
    {
        if ((planWht ?? 0m) <= 0m) return false;
        var paid = paidAtUtc.AddHours(7);
        var now = nowUtc.AddHours(7);
        return paid.Year * 12 + paid.Month < now.Year * 12 + now.Month;
    }

    /// <summary>ลำดับด่าน = ลำดับเหตุที่โชว์ (เหตุแรกที่ไม่ผ่าน) · ทุกเหตุเป็นไทยที่โชว์/ลง audit ได้ตรง</summary>
    public static Verdict Decide(Facts f)
    {
        if (!f.NoticeOpen) return new(Kind.Refuse, Codes.NoticeClosed, "การแจ้งนี้ปิด/ถอน/บันทึกไปแล้ว");
        if (f.SellerPaymentKnown && !f.SellerPaymentLive) return new(Kind.Refuse, Codes.SellerVoided, SellerVoidedReason);
        if (f.SourceDocumentVoided) return new(Kind.Refuse, Codes.SourceVoided, "ผู้ขายยกเลิกใบของการแจ้งนี้แล้ว — ตรวจกับผู้ขายก่อนบันทึกจ่าย");
        // ฝ่ายค้าน L7: เหตุจริงของใบ (ร่าง/ชำระครบ/ยอดไม่ตรง · สกุล) มาก่อนความยินยอม — คู่ที่ไม่ได้ยินยอมต้องเห็นเหตุของใบ ไม่ใช่ "ยังไม่ได้ยินยอม"
        if (f.Plan.Problem != null || f.Plan.Amount is not decimal) return One(Codes.Plan, f.Plan.Problem ?? "คำนวณยอดที่จะบันทึกไม่ได้ — บันทึกเองที่หน้าใบซื้อ");
        if (!string.Equals((f.Currency ?? "").Trim(), "THB", StringComparison.OrdinalIgnoreCase))
            return One(Codes.Currency, "ใบซื้อไม่ใช่สกุลบาท — ระบบไม่บันทึกเอง (อัตราแลกเปลี่ยน ณ วันจ่ายต้องมีคนยืนยัน)");
        if (!f.SellerPaymentKnown)
            return One(Codes.SellerPaymentUnknown, "การแจ้งรุ่นก่อนรอบ 264 ไม่ผูกการรับชำระของผู้ขาย — ระบบตรวจสดไม่ได้ว่าผู้ขายยกเลิกหรือไม่ จึงไม่บันทึกเอง (กดบันทึกจ่ายเองได้)");
        if (!f.ConsentOk) return One(Codes.Consent, "ไม่บันทึกเอง: " + (string.IsNullOrWhiteSpace(f.ConsentReason) ? "ยังไม่ได้ให้ความยินยอม" : f.ConsentReason!.Trim()));
        if (f.UnexplainedPurchasePayment) return One(Codes.OtherPayment, OtherPaymentReason);
        if (f.Plan.Warning != null) return One(Codes.PlanWarning, "ไม่บันทึกเอง — " + f.Plan.Warning);
        if (f.WhtDateInPastMonth) return One(Codes.WhtPeriod, WhtPeriodReason);
        if (f.SourceIsAbbreviated) return One(Codes.Abbreviated, "ใบของผู้ขายเป็นใบกำกับภาษีอย่างย่อ — ระบบไม่บันทึกเอง (ตรวจหลักฐานก่อน)");
        if (f.PayerBorneWht) return One(Codes.PayerBorne, "ใบซื้อเป็นภาษีหัก ณ ที่จ่ายแบบออกแทน — ระบบไม่บันทึกเอง (ตรวจยอดภาษีออกแทนก่อน)");
        if (f.VatMismatch) return One(Codes.VatMismatch, "VAT บนใบของผู้ขายไม่เท่ากับใบซื้อของเรา — ระบบไม่บันทึกเอง (ตรวจใบซื้อก่อน)");
        if (!f.Source.AllowsAuto) return One(Codes.Source, f.Source.Reason);
        return new(Kind.Auto, Codes.Ok, "บันทึกเองได้ — " + f.Source.Reason);
    }
}
