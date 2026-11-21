using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// **ใบสำคัญจ่าย "หลักฐานคู่การจ่าย" ของการจ่ายที่ระบบบันทึกเองจากคู่ค้า — ตัวตัดสินเดียว** (รอบ 283 · คำสั่งเจ้าของ · คำตัดสิน 283-1…283-6)
///
/// <para>ที่มา: ท่ออัตโนมัติของผู้ซื้อ (<c>PartnerPayablePipeline</c> · รอบ 264 P3/281) บันทึกจ่ายใบซื้อผ่าน <c>DocumentService.CreatePaymentAsync</c>
/// (JE Dr เจ้าหนี้ / Cr ธนาคาร + Cr ภาษีหักค้างจ่าย · 50 ทวิ ต่องวดจ่าย) แต่<b>ไม่มีใบสำคัญจ่ายเป็นเอกสาร</b> ⇒ ผู้ซื้อไม่มีกระดาษประกอบการจ่ายให้แฟ้ม/ผู้สอบ ·
/// ทางมือแปลงใบซื้อ → ใบสำคัญจ่าย (PV ลง JE เอง + ตัดหนี้ + ออก 50 ทวิ ตอนอนุมัติ) — ถ้าท่อออก PV แบบแปลงเพิ่มจากการจ่าย = <b>ลงบัญชีซ้ำ</b></para>
///
/// <para>ทางที่เลือก (283-1): PV เป็น<b>เอกสารหลักฐานคู่การจ่าย</b> แบบเดียวกับใบเสร็จหลักฐานฝั่งขาย (<c>IsSettlementReceipt</c> + <c>SettlementPaymentId</c> —
/// ธง "หลักฐานคู่ Payment" ที่ทุกเส้นแกนข้ามอยู่แล้ว: อนุมัติไม่ลง JE · ไม่ตัดหนี้ซ้ำ · ไม่นับเป็นเอกสารปลายทาง · ลบพร้อมใบแม่) ⇒ เงินออก 10,400 ลง GL ครั้งเดียว
/// (ที่การจ่าย) · <b>ไม่ถือ VAT/ภาษีหัก</b> (<see cref="VatAmount"/> = <see cref="WithholdingTaxAmount"/> = 0 — เหมือนใบเสร็จหลักฐานโหมดปกติ) ⇒ ผู้อ่านภาษีทุกตัว
/// (ภ.ง.ด.3/53 · ยอดนำส่ง · ลิสต์รอออก 50 ทวิ · ภ.พ.30) ไม่เห็นใบนี้ ⇒ ไม่นับซ้ำ/ไม่ฟ้องว่าขาด 50 ทวิ · ภาษีหักแสดงในบรรทัดพร้อมเลข 50 ทวิ ·
/// 50 ทวิ ใบเดิม (ออกตอนบันทึกจ่าย · <c>SourcePaymentId</c> = การจ่ายนี้) <b>ผูกกับ PV ด้วยกุญแจการจ่ายเดียวกัน</b> — ไม่ย้าย <c>DocumentId</c>
/// (ผู้อ่าน 50 ทวิ ต่องวด · ยกเลิกการจ่าย · ส่ง 50 ทวิ ให้คู่ค้า ใช้คู่ ใบซื้อ+การจ่าย) · <b>ไม่ออก 50 ทวิ ใบใหม่</b></para>
///
/// <para>เลขเอกสาร (283-2): ผู้บันทึก (ผู้ให้ความยินยอม) มีสิทธิ์อนุมัติใบสำคัญจ่าย ⇒ ออกเลขจริงทันทีจากเล่ม PV (gap-free · <c>DocumentNumberGenerator</c>) ·
/// ไม่มีสิทธิ์ ⇒ ร่าง <c>DRAFT-</c> รอผู้มีสิทธิ์อนุมัติ (อนุมัติข้าม JE/ตัดหนี้ของใบหลักฐานแล้ว) · ยกเลิกใบนี้ = ยกเลิกการจ่าย (283-4)</para>
/// </summary>
public static class SettlementPaymentVoucher
{
    public const string RuleCode = "PARTNER-AUTO-PV";
    public const string LegalReference = "พ.ร.บ.การบัญชี 2543 ม.17 (เอกสารประกอบการลงบัญชี) · ท.ป.4/2528 (50 ทวิ ออกในวันจ่าย)";

    /// <summary>ค่า <c>Document.OriginModule</c> ของใบที่ท่อคู่ค้าสร้าง</summary>
    public const string PartnerOrigin = "PartnerAuto";

    /// <summary>หัวหมายเหตุของการจ่ายที่ท่อบันทึกเอง (ไม่มีผู้กด) — ตัวตั้งเดียวของผู้เขียน (<c>PartnerPayablePipeline</c>)</summary>
    public const string PartnerAutoPaymentNotePrefix = "⚙️ ระบบบันทึกเอง (ไม่มีผู้กด)";

    /// <summary>ป้ายบนใบ (server computes · page displays)</summary>
    public const string PartnerOriginLabel = "⚙️ ระบบสร้างจากคู่ค้า · หลักฐานการจ่าย (ไม่ลงบัญชีซ้ำ)";

    /// <summary>ป้ายของใบตามที่มา — null = ไม่ใช่ใบที่ระบบสร้างจากคู่ค้า</summary>
    public static string? OriginLabel(string? originModule)
        => originModule == PartnerOrigin ? PartnerOriginLabel : null;

    /// <summary>รอบ 283 (ผู้ประสานงาน ข้อ 2): การจ่ายนี้ออกใบสำคัญจ่ายหลักฐานไหม — <b>ตัวตัดสินเดียวของทุกทางเข้า</b> ·
    /// ผู้เรียกระบุมา (<c>IssuePaymentVoucherDocument</c>) ชนะ · ไม่ระบุ ⇒ ใบซื้อ/ค่าใช้จ่ายที่มาจากคู่ค้า (<c>PartnerSourceLinkId</c>) ออกเสมอ
    /// (ท่อ · ปุ่มเดียว "บันทึกจ่ายตามที่ผู้ขายรับแล้ว" · แจ้งโอน P4 · บันทึกชำระมือบนใบนั้น — เหมือนกันทุกทาง) · ใบซื้อทั่วไป ⇒ ไม่ออก (พฤติกรรมเดิม)</summary>
    public static bool IssueOnPayment(bool? requested, DocumentType sourceType, bool fromPartner)
        => requested ?? (fromPartner && AppliesTo(sourceType));

    /// <summary>ผู้เรียกขอเองชัดเจน ⇒ ออกไม่ได้ = โยน (การจ่าย rollback) · ค่าตั้งต้นของใบจากคู่ค้า ⇒ ออกไม่ได้ (เช่น ปิดด้วยบรรทัดปรับอย่างเดียว · มี PV แปลงแล้ว) = ข้าม + เหตุในคำตอบ</summary>
    public static bool StrictOnPayment(bool? requested) => requested == true;

    /// <summary>ข้อความ "ขั้นต่อไป" บนใบตั้งหนี้ฝั่งซื้อ (server computes · page displays) — ตรงกับ <see cref="IssueOnPayment"/> ของปุ่มบันทึกชำระ (ไม่ระบุคำขอ)</summary>
    public static string? PayNextStepHint(DocumentType sourceType, bool fromPartner)
        => !AppliesTo(sourceType) ? null
            : IssueOnPayment(null, sourceType, fromPartner)
                ? "ถึงกำหนดจ่ายให้กด “💰 บันทึกชำระเงิน” ด้านล่าง — ระบบบันทึกการจ่าย ล้างยอดเจ้าหนี้ และออกใบสำคัญจ่าย (หลักฐานคู่การจ่าย · ใบจากคู่ค้า) ให้อัตโนมัติ"
                : "ถึงกำหนดจ่ายให้กด “💰 บันทึกชำระเงิน” ด้านล่าง — ระบบบันทึกการจ่ายและล้างยอดเจ้าหนี้ให้อัตโนมัติ (ไม่ออกใบสำคัญจ่ายแยก) · ต้องการใบสำคัญจ่ายเป็นเอกสาร ให้ใช้ “แปลงเอกสาร” → ใบสำคัญจ่าย แทนการบันทึกชำระ";

    /// <summary>ชนิดใบตั้งหนี้ฝั่งซื้อที่ออกใบสำคัญจ่ายหลักฐานได้ (ใบซื้อ · ใบบันทึกค่าใช้จ่าย)</summary>
    public static bool AppliesTo(DocumentType sourceType) => sourceType is DocumentType.PurchaseInvoice or DocumentType.Expense;

    public enum IssueAction
    {
        /// <summary>ออกใบใหม่</summary>
        Issue = 0,
        /// <summary>การจ่ายนี้มีใบสำคัญจ่ายหลักฐานที่ยังมีผลแล้ว (idempotent — ไม่ใช่ความล้มเหลว)</summary>
        AlreadyIssued = 1,
        /// <summary>ออกไม่ได้ (เหตุภาษาไทยใน <see cref="Decision.Reason"/>)</summary>
        NotApplicable = 2,
    }

    /// <param name="ExistingLiveVoucherId">ใบสำคัญจ่ายหลักฐาน (<c>SettlementPaymentId</c> = การจ่ายนี้) ที่ไม่ถูกยกเลิก/ลบ</param>
    /// <param name="OtherVoucherCoversSource">ใบตั้งหนี้มีใบสำคัญจ่ายแบบแปลง (ลง JE เอง) ที่ยังมีผล — ทางมือจ่ายไปแล้ว ⇒ ไม่ออกหลักฐานซ้อน</param>
    public sealed record Facts(
        bool PaymentFound, bool PaymentDeleted, decimal PaymentAmount,
        DocumentType SourceType, DocumentStatus SourceStatus, bool SourceDeleted,
        Guid? ExistingLiveVoucherId, bool OtherVoucherCoversSource);

    public sealed record Decision(IssueAction Action, string Reason, Guid? ExistingVoucherId = null);

    public static Decision Decide(Facts f)
    {
        if (!f.PaymentFound) return new(IssueAction.NotApplicable, "ไม่พบรายการจ่ายนี้");
        if (f.PaymentDeleted) return new(IssueAction.NotApplicable, "การจ่ายนี้ถูกยกเลิกแล้ว — ไม่ออกใบสำคัญจ่าย");
        if (f.ExistingLiveVoucherId is Guid existing) return new(IssueAction.AlreadyIssued, "การจ่ายนี้มีใบสำคัญจ่ายแล้ว (ไม่ออกซ้ำ)", existing);
        if (!AppliesTo(f.SourceType)) return new(IssueAction.NotApplicable, "ออกใบสำคัญจ่ายหลักฐานได้เฉพาะการจ่ายใบซื้อ/ใบบันทึกค่าใช้จ่าย");
        if (f.SourceDeleted || f.SourceStatus is DocumentStatus.Voided or DocumentStatus.Rejected)
            return new(IssueAction.NotApplicable, "ใบตั้งหนี้ถูกยกเลิก/ลบแล้ว — ไม่ออกใบสำคัญจ่าย");
        if (f.OtherVoucherCoversSource)
            return new(IssueAction.NotApplicable, "ใบตั้งหนี้นี้มีใบสำคัญจ่าย (แปลงจากใบ) ที่ยังมีผลอยู่แล้ว — ไม่ออกใบหลักฐานซ้อน (ตรวจว่าจ่ายซ้ำหรือไม่)");
        if (f.PaymentAmount <= 0m) return new(IssueAction.NotApplicable, "การจ่ายนี้ไม่มีเงินออก (ปิดด้วยบรรทัดปรับอย่างเดียว) — ไม่ออกใบสำคัญจ่าย");
        return new(IssueAction.Issue, "ออกใบสำคัญจ่ายหลักฐานของการจ่ายนี้");
    }

    /// <summary>ยอดบนใบ: เงินออกจริงเท่านั้น (VAT/ภาษีหักอยู่ที่ใบตั้งหนี้ + การจ่าย — ใบนี้ไม่ถือ)</summary>
    public static decimal SubTotal(decimal paymentAmount) => Math.Round(paymentAmount, 2, MidpointRounding.AwayFromZero);
    public const decimal VatAmount = 0m;
    public const decimal WithholdingTaxAmount = 0m;

    /// <summary>หมายเหตุภายในของใบ (ไม่พิมพ์)</summary>
    public static string InternalNote(string paymentNumber, bool backfilled)
        => $"[{RuleCode}] " + (backfilled ? "ออกย้อนหลัง" : "ระบบสร้างจากคู่ค้า")
           + $" — หลักฐานคู่การจ่าย {paymentNumber} · ไม่ลงบัญชี/ไม่ตัดหนี้ซ้ำ (JE อยู่ที่การจ่าย) · ยกเลิกใบนี้ = ยกเลิกการจ่าย";

    /// <summary>ฝ่ายค้าน MED-A1: เหตุที่ใบหลักฐานไม่ออก 50 ทวิ</summary>
    public const string EvidenceNoWhtReason = "ใบหลักฐานคู่การจ่าย/รับชำระ — 50 ทวิ ของการจ่ายนี้อยู่ที่ใบตั้งหนี้ (ไม่ออกจากใบนี้)";

    /// <summary>ฝ่ายค้าน MED-A1: ใบหลักฐาน (ใบสำคัญจ่ายหลักฐาน · ใบเสร็จหลักฐานฝั่งขาย) แก้ได้เฉพาะหมายเหตุ — ยอด/ภาษี/บรรทัด/ผู้ติดต่อ/วันที่ มาจากการชำระ</summary>
    public const string EvidenceEditOnlyNotesMessage = "ใบนี้เป็นหลักฐานคู่การชำระ (ออกอัตโนมัติจากการบันทึกชำระ) — แก้ได้เฉพาะหมายเหตุ · ยอด/ภาษี/รายการมาจากการชำระ: ถ้าผิด ให้ยกเลิกการชำระแล้วบันทึกใหม่";
    public const string EvidenceEditRuleCode = "SETTLEMENT-EVIDENCE-EDIT";

    /// <summary>ฝ่ายค้าน MED-A1: คำขอแก้ใบหลักฐานแตะมากกว่าหมายเหตุไหม (ยอด/ภาษี/บรรทัด/ผู้ติดต่อ/วันที่) — ส่งค่าเดิมกลับมา (ฟอร์มส่งทั้งชุด) = ไม่นับว่าแตะ</summary>
    public static bool EvidenceEditTouchesMoney(
        IReadOnlyList<(decimal Qty, decimal UnitPrice, decimal Discount, decimal VatRate, decimal WhtRate)> currentLines,
        IReadOnlyList<(decimal Qty, decimal UnitPrice, decimal Discount, decimal VatRate, decimal WhtRate)>? requestedLines,
        Guid currentContact, Guid? requestedContact, DateTime currentDate, DateTime? requestedDate)
    {
        if (requestedContact is Guid rc && rc != Guid.Empty && rc != currentContact) return true;
        if (requestedDate is DateTime rd && rd.Date != currentDate.Date) return true;
        if (requestedLines == null) return false;
        if (requestedLines.Count != currentLines.Count) return true;
        for (var i = 0; i < currentLines.Count; i++)
            if (requestedLines[i] != currentLines[i]) return true;
        return false;
    }

    /// <summary>ข้อความเมื่อยกเลิกใบหลักฐานที่การจ่ายถูกยกเลิกไปก่อนแล้ว</summary>
    public const string VoidedWithPaymentNote = "ยกเลิกพร้อมการจ่าย";
}
