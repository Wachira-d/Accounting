using System.Globalization;
using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ข้อเท็จจริงของ "เงินรอจัดสรร" (แถว Payment) ที่ตัวตัดสินต้องใช้ — อ่านใต้ล็อกแถวโดยผู้เรียก</summary>
/// <param name="IsThb">การชำระเป็นสกุลบาทอัตรา 1 (ใบแรกของการชำระเป็น THB และไม่มีอัตรา ณ วันชำระ)</param>
public sealed record CustomerCreditFacts(
    Guid CompanyId, Guid? ContactId, string PaymentNumber, DateTime PaymentDate, decimal UnappliedAmount,
    bool HasUnappliedAccount, bool IsVoided, bool IsSettlementBatchPayment, bool IsThb,
    // ฝ่ายค้านรอบ 258 M4: แถวก่อนรอบ 258 ที่ backfill ประทับ — ยังไม่มีคนยืนยันว่าเงินยังค้างจริง (อาจถูกล้างด้วยสมุดรายวันมือแล้ว)
    bool NeedsReview = false);

/// <summary>ข้อเท็จจริงของใบปลายทางที่จะรับการจัดสรร — อ่านใต้ล็อกแถวโดยผู้เรียก</summary>
public sealed record CustomerCreditTargetFacts(
    Guid CompanyId, Guid ContactId, string DocumentNumber, DocumentType DocumentType, DocumentStatus Status,
    DateTime DocumentDate, decimal BalanceDue, string? Currency, decimal ExchangeRate, decimal VatAmount = 0m);

/// <summary>เหตุปฏิเสธการจัดสรร (ข้อความถึงผู้ใช้ + รหัสกฎ + HTTP status)</summary>
public sealed record CustomerCreditRefusal(string Message, string RuleCode, int StatusCode);

/// <summary>
/// **ตัวตัดสิน "จัดสรรเงินรอจัดสรรให้ใบนี้ได้ไหม" — ตัวเดียว** (รอบ 258 · erp-review/2026-11-11/team-D-lump-payment.md §3.3 ข้อ 2)
/// <para>ผู้เรียก: <c>CustomerCreditService.AllocateAsync</c> (ก่อนลง JE ทุกครั้ง — <c>tools/required_call_site_check.py</c>) และพรีวิวของหน้าเว็บ
/// (รายการใบที่จัดสรรได้) — ตัวเดียวกัน ⇒ ปุ่มที่หน้าเว็บเสนอ = สิ่งที่การบันทึกรับ</para>
/// <list type="bullet">
/// <item>การชำระยังมีผล · ไม่ใช่ของรอบโอน settlement · มีผังพักและยอดคงเหลือ</item>
/// <item>ผู้ติดต่อ<b>เดียวกัน</b>เท่านั้น (258-1-4 — เลขภาษีเดียวคนละสาขา = คนละผู้ติดต่อ ยังไม่อนุญาต) · ไม่รู้เจ้าของเงิน = ปฏิเสธ</item>
/// <item>ใบฝั่งขายที่ตั้งลูกหนี้ (ใบแจ้งหนี้ · ใบกำกับภาษี · ใบเพิ่มหนี้) สถานะรับชำระได้ · ฝั่งซื้อปิด (258-1-8)</item>
/// <item>สกุลบาททั้งสองฝั่ง · <c>0 &lt; ยอด ≤ คงเหลือ</c> · ไม่ทำให้ใบจ่ายเกิน (ด่านเดิม <see cref="DocumentSettlementState.WouldOverpay"/>)</item>
/// <item>วันที่จัดสรร ≥ วันรับเงิน และ ≥ วันที่ใบ · ไม่เป็นวันอนาคต</item>
/// </list>
/// </summary>
public static class CustomerCreditAllocation
{
    public const string RuleCode = "CC-ALLOC";
    public const string ContactRuleCode = "CC-ALLOC-CONTACT";
    public const string NeedsReviewRuleCode = "CC-LEGACY-REVIEW";
    /// <summary>ยอดคงเหลือที่หน้าจอเห็นไม่ตรงกับของจริงใต้ล็อก (คำขอซ้อน/กดซ้ำ) — ฝ่ายค้านรอบ 258 M7</summary>
    public const string StaleRuleCode = "CC-ALLOC-STALE";

    /// <summary>
    /// กันจัดสรรซ้ำจากการกดซ้ำ/สองแท็บ (M7): หน้าจอส่ง "ยอดคงเหลือที่เห็นตอนกด" มาด้วย — ใต้ล็อกแถว ยอดจริงต้องเท่ากัน ไม่งั้นปฏิเสธให้โหลดใหม่ ·
    /// null = ผู้เรียกไม่ส่งมา (API เดิม — ไม่ตรวจ)
    /// </summary>
    public static CustomerCreditRefusal? StaleCheck(decimal? expectedUnappliedBefore, decimal actualUnapplied, string paymentNumber)
        => expectedUnappliedBefore is decimal expected && Math.Abs(expected - actualUnapplied) > UnappliedCreditBalance.Tolerance
            ? new CustomerCreditRefusal(
                $"เงินรอจัดสรรของ {paymentNumber} เปลี่ยนไปแล้ว (ตอนกดเห็น {Money(expected)} · ตอนนี้ {Money(actualUnapplied)}) — อาจมีการจัดสรรซ้อน/กดซ้ำ · โหลดหน้าใหม่แล้วตรวจยอดก่อนทำต่อ",
                StaleRuleCode, 409)
            : null;
    public const string LegalReference = "ป.รัษฎากร ม.78/1 (จุดความรับผิดเมื่อรับชำระ)";

    /// <summary>ชนิดเอกสารที่รับการจัดสรรได้ — ใบฝั่งขายที่ตั้งลูกหนี้และรับชำระตรงได้</summary>
    public static readonly IReadOnlyList<DocumentType> TargetTypes = new[]
    {
        DocumentType.Invoice, DocumentType.TaxInvoice, DocumentType.DebitNote,
    };

    /// <summary>สถานะของใบที่รับชำระได้ (ชุดเดียวกับเส้นรับชำระ)</summary>
    public static readonly IReadOnlyList<DocumentStatus> OpenStatuses = new[]
    {
        DocumentStatus.Approved, DocumentStatus.Sent, DocumentStatus.PartiallyPaid, DocumentStatus.Overdue,
    };

    private static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    /// <summary>คืนเหตุปฏิเสธ (null = จัดสรรได้)</summary>
    /// <param name="today">วันนี้ตามปฏิทินไทย (<c>ThaiDate.CalendarDateUtc(DateTime.UtcNow)</c>)</param>
    public static CustomerCreditRefusal? Check(CustomerCreditFacts credit, CustomerCreditTargetFacts target,
        decimal amount, DateTime applyDate, DateTime today)
    {
        static CustomerCreditRefusal No(string message, string code = RuleCode, int status = 409) => new(message, code, status);

        if (credit.CompanyId != target.CompanyId)
            return No("ไม่พบเอกสารปลายทางในบริษัทนี้", RuleCode, 404);
        if (credit.IsVoided)
            return No($"การรับชำระ {credit.PaymentNumber} ถูกยกเลิกแล้ว — ไม่มีเงินรอจัดสรรให้ใช้");
        if (credit.IsSettlementBatchPayment)
            return No($"การรับชำระ {credit.PaymentNumber} เป็นของรอบโอนจากผู้ให้บริการ — จัดการที่หน้า “รอบโอน” ไม่ใช่เงินรอจัดสรร");
        if (!credit.HasUnappliedAccount || !UnappliedCreditBalance.HasBalance(credit.UnappliedAmount))
            return No($"การรับชำระ {credit.PaymentNumber} ไม่มีเงินรอจัดสรรคงเหลือ");
        if (credit.NeedsReview)
            return No($"เงินรอจัดสรรของ {credit.PaymentNumber} เป็นรายการก่อนรอบ 258 ที่ยังต้องตรวจ (ระบบไม่รู้ว่าเงินส่วนเกินนี้ถูกล้างด้วยสมุดรายวันไปแล้วหรือยัง) — "
                + "ที่หน้า “เงินรอจัดสรร” กด “ยืนยันว่ายังค้างจริง” ก่อนจัดสรร หรือ “ปิดรายการ” พร้อมเหตุผล", NeedsReviewRuleCode);
        if (credit.ContactId is not Guid owner || owner == Guid.Empty)
            return No($"ระบบไม่ทราบว่าเงินรอจัดสรรของ {credit.PaymentNumber} เป็นของลูกค้ารายใด (รายการก่อนรอบ 258) — "
                + "ยกเลิกการรับชำระนี้แล้วบันทึกใหม่ เพื่อให้เงินผูกกับลูกค้า", ContactRuleCode);
        if (owner != target.ContactId)
            return No($"เงินรอจัดสรรของ {credit.PaymentNumber} เป็นของลูกค้าอีกราย — จัดสรรให้ใบ {target.DocumentNumber} ของลูกค้าคนละรายไม่ได้ "
                + "(รวมถึงเลขผู้เสียภาษีเดียวกันแต่คนละสาขา) · ทางแก้: ยกเลิกการรับชำระแล้วบันทึกใหม่ที่ลูกค้าที่ถูกต้อง", ContactRuleCode);
        if (!TargetTypes.Contains(target.DocumentType))
            return No($"เอกสาร {target.DocumentNumber} ({target.DocumentType}) รับการจัดสรรเงินรอจัดสรรไม่ได้ — "
                + "ใช้ได้กับใบแจ้งหนี้ ใบกำกับภาษี และใบเพิ่มหนี้ฝั่งขายเท่านั้น");
        if (!OpenStatuses.Contains(target.Status))
            return No($"เอกสาร {target.DocumentNumber} สถานะ {target.Status} รับชำระไม่ได้ — ต้องอนุมัติแล้วและยังมียอดค้าง");
        if (!credit.IsThb || !IsThbDocument(target.Currency, target.ExchangeRate))
            return No($"เงินรอจัดสรรใช้ได้กับเอกสารสกุลบาทเท่านั้น — ใบ {target.DocumentNumber} หรือการรับชำระเป็นสกุลเงินต่างประเทศ "
                + "ให้บันทึกรับชำระของใบนั้นตามปกติ");
        if (amount <= 0m)
            return No("ยอดที่จัดสรรต้องมากกว่า 0", RuleCode, 400);
        if (Math.Round(amount, 2, MidpointRounding.AwayFromZero) != amount)
            return No("ยอดที่จัดสรรมีทศนิยมได้ไม่เกิน 2 ตำแหน่ง", RuleCode, 400);
        if (amount > credit.UnappliedAmount)
            return No($"ยอดที่จัดสรร {Money(amount)} มากกว่าเงินรอจัดสรรคงเหลือ {Money(credit.UnappliedAmount)} ของ {credit.PaymentNumber}");
        // ฝ่ายค้านรอบ 258 M5: ใบแจ้งหนี้ที่มี VAT จัดสรรบางส่วนไม่ได้ (ด่านเดียวกับหน้ารับชำระ — ฝั่งเซิร์ฟเวอร์)
        if (VatInvoiceSettlementGate.PartialRefusal(target.DocumentType, target.VatAmount, amount, target.BalanceDue, target.DocumentNumber) is string partialVat)
            return No(partialVat, VatInvoiceSettlementGate.RuleCode);
        if (DocumentSettlementState.WouldOverpay(target.BalanceDue, amount))
            return No($"ยอดที่จัดสรร {Money(amount)} มากกว่ายอดค้างของ {target.DocumentNumber} ({Money(target.BalanceDue)}) — จัดสรรได้ไม่เกินยอดค้าง");
        var d = applyDate.Date;
        if (d > today.Date)
            return No("วันที่จัดสรรต้องไม่เป็นวันในอนาคต", RuleCode, 400);
        if (d < credit.PaymentDate.Date)
            return No($"วันที่จัดสรร {d:dd/MM/yyyy} ก่อนวันรับเงิน {credit.PaymentDate:dd/MM/yyyy} ไม่ได้", RuleCode, 400);
        if (d < target.DocumentDate.Date)
            return No($"วันที่จัดสรร {d:dd/MM/yyyy} ก่อนวันที่ของใบ {target.DocumentNumber} ({target.DocumentDate:dd/MM/yyyy}) ไม่ได้", RuleCode, 400);
        return null;
    }

    /// <summary>เอกสารสกุลบาทอัตรา 1 ไหม (ค่าว่าง = บาท)</summary>
    public static bool IsThbDocument(string? currency, decimal exchangeRate)
        => (string.IsNullOrWhiteSpace(currency) || string.Equals(currency.Trim(), "THB", StringComparison.OrdinalIgnoreCase))
           && (exchangeRate == 1m || exchangeRate == 0m);

    /// <summary>เลขอ้างอิงของ JE การจัดสรรทีหลังลำดับที่ <paramref name="sequence"/> — ขึ้นต้นด้วย <c>{เลขการชำระ}/</c> เพื่อให้การยกเลิกการชำระ
    /// (ตัวเลือก JE เดิม: <c>Reference == เลข หรือ ขึ้นต้นด้วย เลข + "/"</c>) กลับ JE นี้ด้วย</summary>
    public static string JournalReference(string paymentNumber, int sequence)
        => paymentNumber + "/A" + sequence.ToString(CultureInfo.InvariantCulture);
}
