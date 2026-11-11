using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>เหตุปฏิเสธขาออกของเงินรอจัดสรร (ข้อความถึงผู้ใช้ + รหัสกฎ + HTTP status)</summary>
public sealed record CustomerCreditMovementRefusal(string Message, string RuleCode, int StatusCode);

/// <summary>
/// **ตัวตัดสิน "ขาออกของเงินรอจัดสรรที่ไม่ใช่การตัดใบ" — ตัวเดียว** (รอบ 259 แพ็ก P4 · DECISIONS 259-1-x ·
/// erp-review/2026-11-11/team-D-lump-payment.md §3.2 ขั้น 3–5)
/// <list type="bullet">
/// <item><b>คืนเงิน</b> (Refund) — Dr ผังพักที่รับไว้จริง / Cr ธนาคาร · ต้องมีบัญชีธนาคาร + หลักฐานการจ่าย</item>
/// <item><b>ย้ายเป็นเงินมัดจำ</b> (ToDeposit) — ออกใบมัดจำในระบบมัดจำเดิม (ขาเงินเข้า = ผังพัก) · โหมด VAT ตามระบบมัดจำ (ม.78/1)</item>
/// <item><b>ตัดเศษ</b> (WriteOff) — เฉพาะ "เศษทั้งหมดที่เหลือ" และ ≤ เพดานของบริษัท (ค่าตั้งต้น 1.00 · สูงสุด <see cref="MaxWriteOffCap"/>)</item>
/// <item><b>จองให้ใบที่ออก</b> (ToInvoice) — ไม่ใช่เงินออก: ไม่ตัดยอดคงเหลือ (<see cref="ReducesBalance"/> = false) แต่ลด "ยอดที่ใช้ได้"
/// ระหว่างที่ใบยังเป็นร่าง (<see cref="HoldsUsable"/>) — อนุมัติแล้วระบบจัดสรรให้ · ใบถูกลบ/ปฏิเสธ = การจองหมดผลเอง (ไม่ค้างเงินเงียบ ๆ)</item>
/// </list>
/// <para>ผู้เรียก: <c>CustomerCreditService</c> (ก่อนลง JE ทุกครั้ง · ใต้ล็อกแถวการชำระ — <c>tools/required_call_site_check.py</c>) และ
/// <c>CustomerCreditLedger</c> (สูตรยอดคงเหลือ) — หน้าเว็บไม่มีสำเนากติกา</para>
/// </summary>
public static class CustomerCreditMovementRules
{
    public const string RefundRuleCode = "CC-REFUND";
    public const string ToDepositRuleCode = "CC-TO-DEPOSIT";
    public const string WriteOffRuleCode = "CC-WRITEOFF";
    public const string ToInvoiceRuleCode = "CC-TO-INVOICE";
    public const string UndoRuleCode = "CC-MOVE-UNDO";
    public const string VoidBlockRuleCode = "CC-VOID-MOVEMENT";
    public const string LegalReference = "ป.รัษฎากร ม.78/1 · ม.86 · พ.ร.บ.การบัญชี 2543 ม.17 (เอกสารประกอบการลงบัญชี)";

    /// <summary>เพดานตัดเศษตั้งต้นของบริษัท (บาท)</summary>
    public const decimal DefaultWriteOffCap = 1.00m;
    /// <summary>เพดานตัดเศษสูงสุดที่ตั้งได้ — เท่าเกณฑ์ผลต่างกระทบยอดธนาคาร (฿20) · มากกว่านี้ไม่ใช่ "เศษ" ต้องคืนเงิน/ออกใบ</summary>
    public const decimal MaxWriteOffCap = 20.00m;

    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>ชื่อชนิด (ค่าที่เก็บ) → enum · ไม่รู้จัก = null</summary>
    public static PaymentCreditMovementKind? Parse(string? kind)
        => Enum.TryParse<PaymentCreditMovementKind>(kind, ignoreCase: false, out var k) && Enum.IsDefined(k) ? k : null;

    /// <summary>ป้ายภาษาไทยของชนิด</summary>
    public static string LabelOf(PaymentCreditMovementKind kind) => kind switch
    {
        PaymentCreditMovementKind.Refund => "คืนเงินลูกค้า",
        PaymentCreditMovementKind.ToDeposit => "ย้ายเป็นเงินมัดจำ",
        PaymentCreditMovementKind.WriteOff => "ตัดเศษ",
        PaymentCreditMovementKind.ToInvoice => "จองให้ใบแจ้งหนี้ที่ออก",
        _ => kind.ToString(),
    };

    /// <summary>ชนิดนี้ "ตัดยอดคงเหลือ" (เป็นเงินออกจากผังพักจริง) ไหม — สูตรเดียวของ <see cref="UnappliedCreditBalance.Compute"/></summary>
    public static bool ReducesBalance(PaymentCreditMovementKind kind)
        => kind is PaymentCreditMovementKind.Refund or PaymentCreditMovementKind.ToDeposit or PaymentCreditMovementKind.WriteOff;

    /// <summary>รายการยังมีผลไหม (ไม่ถูกยกเลิก · ไม่ถูกปล่อย)</summary>
    public static bool IsLive(bool isDeleted, DateTime? releasedAt) => !isDeleted && releasedAt == null;

    /// <summary>การจองให้ใบ (ToInvoice) ยังกันยอดไว้ไหม — มีผลเฉพาะใบปลายทางยังเป็นร่าง/รออนุมัติ (ใบหาย/ถูกปฏิเสธ/อนุมัติแล้ว = ไม่กัน)</summary>
    public static bool HoldsUsable(PaymentCreditMovementKind kind, bool isDeleted, DateTime? releasedAt, DocumentStatus? targetStatus)
        => kind == PaymentCreditMovementKind.ToInvoice && IsLive(isDeleted, releasedAt)
           && targetStatus is DocumentStatus.Draft or DocumentStatus.WaitingApproval;

    /// <summary>ยอดที่ใช้ได้จริง = คงเหลือ − ยอดที่จองให้ใบร่างอื่น (ไม่ติดลบ)</summary>
    public static decimal Available(decimal unappliedAmount, decimal heldForDrafts)
    {
        var v = Math.Round(unappliedAmount - heldForDrafts, 2, MidpointRounding.AwayFromZero);
        return v <= UnappliedCreditBalance.Tolerance ? 0m : v;
    }

    /// <summary>เลขอ้างอิง JE ของขาออก — <b>ไม่ขึ้นต้นด้วย <c>{เลขการชำระ}/</c></b> (การยกเลิกการชำระกลับ JE ตาม prefix นั้น — ขาออกที่จ่ายเงินจริง
    /// ห้ามถูกกลับไปพร้อมกันเงียบ ๆ · ยกเลิกการชำระถูกบล็อกด้วย <see cref="VoidBlockMessage"/> แทน)</summary>
    public static string JournalReference(string paymentNumber, PaymentCreditMovementKind kind, int sequence)
        => paymentNumber + "#" + (kind switch
        {
            PaymentCreditMovementKind.Refund => "R",
            PaymentCreditMovementKind.WriteOff => "W",
            PaymentCreditMovementKind.ToDeposit => "D",
            _ => "M",
        }) + sequence.ToString(CultureInfo.InvariantCulture);

    /// <summary>เพดานตัดเศษที่ผู้ใช้ตั้ง — null = ใช้ได้ · มีค่า = ข้อความปฏิเสธ</summary>
    public static string? CapProblem(decimal cap)
        => cap < 0m || cap > MaxWriteOffCap || decimal.Round(cap, 2) != cap
            ? $"เพดานตัดเศษต้องอยู่ระหว่าง 0.00–{Money(MaxWriteOffCap)} บาท (ทศนิยมไม่เกิน 2 ตำแหน่ง) — ยอดที่มากกว่านี้ให้คืนเงินลูกค้าหรือออกใบแจ้งหนี้"
            : null;

    /// <summary>
    /// ตัดสินขาออก 1 รายการ (null = ทำได้) — อ่านใต้ล็อกแถวการชำระโดยผู้เรียก
    /// </summary>
    /// <param name="available">ยอดที่ใช้ได้ใต้ล็อก (<see cref="Available"/>) — การจองของใบที่ไม่ใช่ใบนี้หักแล้ว</param>
    /// <param name="writeOffCap">เพดานตัดเศษของบริษัท</param>
    /// <param name="hasBankAccount">คืนเงิน: เลือกบัญชีธนาคารที่ใช้ได้ (ผูกผังแล้ว) แล้ว</param>
    /// <param name="evidence">คืนเงิน: หลักฐานการจ่าย (เลขอ้างอิงการโอน/เช็ค)</param>
    /// <param name="reason">ตัดเศษ: เหตุผล (คำตัดสินเจ้าของ 258-1-10 "≤ ฿20 ต้องมีเหตุผล")</param>
    public static CustomerCreditMovementRefusal? Check(PaymentCreditMovementKind kind, CustomerCreditFacts credit, decimal available,
        decimal amount, DateTime date, DateTime today, decimal writeOffCap = DefaultWriteOffCap, bool hasBankAccount = false, string? evidence = null,
        string? reason = null)
    {
        var code = kind switch
        {
            PaymentCreditMovementKind.Refund => RefundRuleCode,
            PaymentCreditMovementKind.ToDeposit => ToDepositRuleCode,
            PaymentCreditMovementKind.WriteOff => WriteOffRuleCode,
            _ => ToInvoiceRuleCode,
        };
        CustomerCreditMovementRefusal No(string message, int status = 409) => new(message, code, status);

        if (credit.IsVoided)
            return No($"การรับชำระ {credit.PaymentNumber} ถูกยกเลิกแล้ว — ไม่มีเงินรอจัดสรรให้ใช้");
        if (credit.IsSettlementBatchPayment)
            return No($"การรับชำระ {credit.PaymentNumber} เป็นของรอบโอนจากผู้ให้บริการ — จัดการที่หน้า “รอบโอน” ไม่ใช่เงินรอจัดสรร");
        if (!credit.HasUnappliedAccount)
            return No($"การรับชำระ {credit.PaymentNumber} ไม่มีเงินรอจัดสรร");
        if (credit.NeedsReview)
            return No($"รายการ {credit.PaymentNumber} เป็นเงินส่วนเกินก่อนรอบ 258 ที่ต้องตรวจ — กด “ยืนยันว่ายังค้างจริง” ก่อน", 409);
        if (!credit.IsThb)
            return No("เงินรอจัดสรรสกุลต่างประเทศยังไม่รองรับ — ติดต่อผู้ทำบัญชี");
        if (credit.ContactId == null && kind != PaymentCreditMovementKind.WriteOff)
            return No($"ไม่ทราบลูกค้าเจ้าของเงิน {credit.PaymentNumber} — {LabelOf(kind)}ต้องรู้ผู้รับ/ผู้ซื้อ (ป.รัษฎากร ม.65 ตรี (11) · ม.86/4)");
        if (amount <= 0m)
            return No("ยอดต้องมากกว่า 0", 400);
        if (decimal.Round(amount, 2) != amount)
            return No("ยอดมีทศนิยมได้ไม่เกิน 2 ตำแหน่ง", 400);
        if (amount > available + UnappliedCreditBalance.Tolerance)
            return No($"ยอด {Money(amount)} เกินเงินรอจัดสรรที่ใช้ได้ของ {credit.PaymentNumber} ({Money(available)}) — ยอดที่จองให้ใบแจ้งหนี้ร่างไม่นับเป็นยอดที่ใช้ได้");
        if (date.Date > today.Date)
            return No("วันที่ต้องไม่เป็นวันอนาคต", 400);
        if (date.Date < credit.PaymentDate.Date)
            return No($"วันที่ต้องไม่ก่อนวันรับเงิน ({credit.PaymentDate:dd/MM/yyyy})", 400);

        switch (kind)
        {
            case PaymentCreditMovementKind.WriteOff:
                if (Math.Abs(amount - available) > UnappliedCreditBalance.Tolerance)
                    return No($"ตัดเศษได้เฉพาะ “ยอดที่เหลือทั้งหมด” ({Money(available)}) — ยอดบางส่วนให้คืนเงินหรือจัดสรรให้ใบ");
                if (amount > writeOffCap + UnappliedCreditBalance.Tolerance)
                    return No($"ยอดคงเหลือ {Money(amount)} เกินเพดานตัดเศษของบริษัท ({Money(writeOffCap)}) — คืนเงินลูกค้า หรือออกใบแจ้งหนี้ส่วนที่ขาด · "
                        + "เพดานแก้ได้ที่หน้า “เงินรอจัดสรร” (สูงสุด " + Money(MaxWriteOffCap) + ")");
                if ((reason ?? "").Trim().Length < 5)
                    return No("ระบุเหตุผลของการตัดเศษ (อย่างน้อย 5 ตัวอักษร เช่น “ลูกค้าโอนเกินเศษสตางค์”) — คำตัดสิน 258-1-10", 400);
                break;
            case PaymentCreditMovementKind.Refund:
                if (!hasBankAccount)
                    return No("เลือกบัญชีธนาคารที่จ่ายคืน (บัญชีที่ผูกผังบัญชีแล้ว)", 400);
                if ((evidence ?? "").Trim().Length < 3)
                    return No("ระบุหลักฐานการจ่ายคืน (เลขอ้างอิงการโอน/เลขเช็ค อย่างน้อย 3 ตัวอักษร) — ใช้เป็นเอกสารประกอบการลงบัญชี", 400);
                break;
        }
        return null;
    }

    /// <summary>ข้อความเมื่อยกเลิกการรับชำระที่ยังมีขาออกที่มีผล (ต้องยกเลิกขานั้นก่อน — กลับพร้อมกันเงียบ ๆ ไม่ได้)</summary>
    public static string VoidBlockMessage(string paymentNumber, IEnumerable<PaymentCreditMovementKind> liveKinds)
    {
        var labels = liveKinds.Distinct().OrderBy(k => (int)k).Select(LabelOf).ToList();
        return $"ยกเลิกการรับชำระ {paymentNumber} ไม่ได้ — เงินรอจัดสรรของรายการนี้ถูกใช้ไปแล้ว ({string.Join(" · ", labels)}) · "
            + "ยกเลิกรายการนั้นก่อนที่หน้า “เงินรอจัดสรร” (ใบมัดจำ: ยกเลิกใบมัดจำที่หน้าเอกสาร) แล้วจึงยกเลิกการรับชำระ";
    }

    /// <summary>วันรับเงินเป็นจุดความรับผิดของใบมัดจำที่ย้ายมาไหม (ม.78/1) — เงินล่วงหน้า/ค้างออกใบ = ใช่ · โอนเกิน/ยังไม่ทราบ = วันที่ตกลงเปลี่ยนเป็นมัดจำ
    /// (หลักเดียวกับ <see cref="LateReceiptTaxPoint.ReceiptDrivesTaxPoint"/> + เงินล่วงหน้า)</summary>
    public static bool DepositReceiptDrivesTaxPoint(string? storedReason)
        => LateReceiptTaxPoint.ReceiptDrivesTaxPoint(storedReason)
           || string.Equals(storedReason, nameof(UnappliedReason.Advance), StringComparison.Ordinal);

    /// <summary>
    /// ใบมัดจำที่ย้ายจากเงินรอจัดสรรลงวันที่วันนี้ (ห้ามย้อนวัน — เลขที่เรียงตามเวลา §86/4 · §87) แต่ถ้าเงินเป็น "ล่วงหน้า/ค้างออกใบ" ตั้งแต่วันรับ
    /// และวันรับอยู่คนละงวดภาษี ⇒ ภาษีถึงกำหนดตั้งแต่งวดวันรับเงิน — คำเตือน <c>[LATE-VAT]</c> (ระบบไม่ย้ายงวดเอง · null = ไม่มีอะไรต้องบอก)
    /// </summary>
    public static string? DepositLateVatNote(string? reason, DateTime receiptDate, DateTime depositDate, decimal vatAmount, bool vatDeferred)
    {
        if (vatAmount <= UnappliedCreditBalance.Tolerance || vatDeferred) return null;
        if (!DepositReceiptDrivesTaxPoint(reason)) return null;
        if (DepositPolicyResolver.SameVatPeriod(receiptDate, depositDate)) return null;
        var received = receiptDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var month = receiptDate.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        var depMonth = depositDate.ToString("MM/yyyy", CultureInfo.InvariantCulture);
        return $"{DepositPolicyResolver.LateVatMarker} ใบมัดจำนี้ย้ายจากเงินที่รับเมื่อ {received} — ถ้าเป็นเงินล่วงหน้าตั้งแต่วันรับ ภาษีขายถึงกำหนดงวด {month} (ม.78/1) "
            + $"แต่ใบนี้อยู่ในรายงานภาษีขายงวด {depMonth} · ตรวจกับนักบัญชีว่าต้องยื่นเพิ่มเติม/มีเงินเพิ่มมาตรา 89/1 หรือไม่";
    }

    /// <summary>คำอธิบายบรรทัดของใบมัดจำที่ย้ายมา</summary>
    public static string DepositLineDescription(string paymentNumber, DateTime receiptDate)
        => $"เงินมัดจำ/รับล่วงหน้า (ย้ายจากเงินรอจัดสรร {paymentNumber} รับเมื่อ {receiptDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)})";

    /// <summary>คำอธิบายบรรทัดตั้งต้นของใบแจ้งหนี้ส่วนที่ขาด (ผู้ใช้แก้รายการได้ก่อนอนุมัติ)</summary>
    public static string InvoiceLineDescription(string paymentNumber, DateTime receiptDate)
        => $"ค่าสินค้า/บริการ (ตามเงินที่รับเมื่อ {receiptDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} · {paymentNumber}) — แก้รายการให้ตรงงานจริงก่อนอนุมัติ";

    /// <summary>ป้ายบนใบแจ้งหนี้ร่างที่จองเงิน (หมายเหตุภายใน — ไม่ใช่ช่องฟอร์ม)</summary>
    public static string InvoiceHoldNote(string paymentNumber, decimal amount)
        => $"[CC-HOLD] ใบนี้ออกจากเงินรอจัดสรร {paymentNumber} — อนุมัติแล้วระบบจัดสรร {Money(amount)} บาทให้อัตโนมัติ";
}
