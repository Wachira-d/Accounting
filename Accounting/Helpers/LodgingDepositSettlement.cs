namespace Accounting.Helpers;

/// <summary>ภาพถ่ายของใบมัดจำ 1 ใบ ณ ตอนตัดสิน (อ่านจาก <c>Document</c> ที่ <c>IsDeposit</c>)</summary>
/// <param name="VatPending">VAT ของใบนี้ยังพักอยู่ 21913 (ยังไม่เข้า ภ.พ.30) —
/// <c>DepositOutputVatDeferred && DepositOutputVatRecognizedAt == null</c></param>
/// <param name="RealizedBase">ฐานที่รับรู้/ตัดชำระไปแล้ว (<c>DepositRealizedAmount</c>)</param>
/// <param name="RefundedGross">ยอดที่คืนเงินไปแล้วรวม VAT (<c>DepositRefundedAmount</c>)</param>
public sealed record LodgingDepositSnapshot(
    Guid Id, string Number, Guid ContactId,
    decimal SubTotal, decimal VatAmount, decimal TotalAmount,
    bool VatPending, decimal RealizedBase, decimal RefundedGross);

/// <summary>ยอดคงเหลือของใบมัดจำ 1 ใบ</summary>
public readonly record struct DepositRemaining(decimal Base, decimal Vat, decimal Gross);

/// <summary>ใบมัดจำ 1 ใบที่ "ออกใบกำกับไปแล้ว" → หักฐานออกจากใบสุดท้าย + รับรู้ฐานเป็นรายได้</summary>
public sealed record TaxedDepositDeduction(Guid Id, string Number, decimal Base, decimal Vat, decimal Gross);

/// <summary>ใบมัดจำ 1 ใบที่ VAT ยังพักรอ (หรือไม่มี VAT) → นำเงินไปตัดชำระใบสุดท้าย (ApplyDeposit)</summary>
public sealed record PendingVatDepositApply(Guid Id, string Number, Guid ContactId, decimal Gross);

/// <summary>แผนเช็คเอาต์ — ใบสุดท้ายเอามัดจำแต่ละใบไปใช้อย่างไร</summary>
public sealed record LodgingCheckoutDepositPlan(
    IReadOnlyList<TaxedDepositDeduction> Deduct,
    IReadOnlyList<PendingVatDepositApply> Apply)
{
    /// <summary>ฐานภาษีที่หักออกจากใบสุดท้าย (= <c>BillDiscountAmount</c>)</summary>
    public decimal BaseDeducted => Deduct.Sum(x => x.Base);
    public decimal VatDeducted => Deduct.Sum(x => x.Vat);
    public decimal GrossDeducted => Deduct.Sum(x => x.Gross);
    public decimal GrossApplied => Apply.Sum(x => x.Gross);
    /// <summary>เลขใบมัดจำที่ออกใบกำกับแล้ว (คั่นจุลภาค) → <c>DepositAppliedRef</c> ของใบสุดท้าย · null = ไม่มี</summary>
    public string? DeductionRef => Deduct.Count == 0 ? null : string.Join(", ", Deduct.Select(x => x.Number));
}

/// <summary>ยกเลิก/no-show: มัดจำแต่ละใบถูกริบ (รับรู้รายได้ทันที) เท่าไร และต้องคืนเท่าไร</summary>
public sealed record LodgingCancelDepositLine(
    Guid Id, string Number, decimal ForfeitBase, decimal RefundGross, decimal RefundBase, decimal RefundVat);

public sealed record LodgingCancelPlan(
    decimal Fee, decimal Forfeit, decimal Refund, IReadOnlyList<LodgingCancelDepositLine> Lines)
{
    /// <summary>ค่าปรับส่วนที่เกินมัดจำ — ไม่ได้เรียกเก็บ (บันทึกไว้ให้เห็น ไม่แต่งเอกสาร)</summary>
    public decimal UncollectedFee => Math.Max(0m, Fee - Forfeit);
}

/// <summary>สถานะการคืนเงินของการจองที่ยกเลิก — ตัดสินจาก "ยอดต้องคืน" กับ "ยอดที่ยืนยันว่าคืนแล้ว" เท่านั้น</summary>
public enum LodgingRefundState
{
    /// <summary>ไม่มีอะไรต้องคืน</summary>
    None,
    /// <summary>ต้องคืนแต่ยังไม่มีหลักฐานว่าคืนแล้ว (ทั้งหมดหรือบางส่วน)</summary>
    Pending,
    /// <summary>ยืนยันแล้วว่าคืนครบ</summary>
    Paid,
}

/// <summary>
/// ตัวคำนวณ pure ของเงินมัดจำโมดูลที่พัก (รอบ 193 · #34 + F-03)
///
/// ═══ เช็คเอาต์ (#34) ═══
/// มัดจำที่ <b>ออกใบกำกับไปแล้ว</b> (VAT 21911 เข้า ภ.พ.30 เดือนที่รับเงิน) ห้ามให้ใบสุดท้ายรายงาน VAT เต็มใบอีก
/// (VAT ซ้ำ + ผู้ซื้อ B2B เคลมภาษีซื้อซ้ำ) ⇒ ใช้ทางที่ DOCUMENT_FLOW §2.3 "รูปแบบ B" + ข้อความของด่าน
/// <c>ApplyDepositToInvoiceCoreAsync</c> ทางที่ ① ระบุไว้แล้ว: ใบสุดท้าย "หักมูลค่ามัดจำที่ออกใบกำกับแล้วออกจากฐาน"
/// (ฐาน VAT ลดลง) แล้วรับรู้ฐานของมัดจำเป็นรายได้ด้วย <c>RealizeDepositAsync</c> (Dr 217xx / Cr รายได้ — VAT มัดจำ
/// อยู่ที่เดิมในงวดเดิม) · มัดจำที่ VAT <b>ยังพักรอ</b> (21913) หรือไม่มี VAT ใช้เส้นเดิม <c>ApplyDepositToInvoiceAsync</c>
/// (Dr 217xx + 21913 / Cr ลูกหนี้ — ใบสุดท้ายรายงาน VAT เต็มครั้งเดียว)
///
/// ═══ ยกเลิก (F-03) ═══
/// ค่าปรับ = ส่วนที่ริบ → รับรู้ทันที (เหตุการณ์เกิดแล้วจริงตอนยกเลิก) · ส่วนที่ต้องคืน = <b>ภาระค้างคืน</b> ยังไม่ลงบัญชี
/// เงินสด (เงินยังไม่ออกจากบัญชี) — JE คืนเงิน + ใบลดหนี้เกิดตอนมีหลักฐานว่าคืนจริงเท่านั้น (DECISION_DOCTRINE R1)
/// · สูตรแยกฐาน/VAT ตรงกับ <c>DocumentService.RefundDepositAsync</c> เป๊ะ ⇒ ด่าน "คืนเกินคงเหลือ" ผ่านพอดีไม่เหลือเศษ
/// </summary>
public static class LodgingDepositSettlement
{
    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>แยก gross ที่คืน → (ฐาน, VAT) ด้วยสูตรเดียวกับ <c>RefundDepositAsync</c>:
    /// vat = round(gross × Vat/Total) · base = gross − vat</summary>
    private static (decimal Base, decimal Vat) SplitRefund(decimal gross, decimal docTotal, decimal docVat)
    {
        var portion = docTotal > 0m ? docVat / docTotal : 0m;
        var vat = R2(gross * portion);
        return (gross - vat, vat);
    }

    /// <summary>แยก gross รวม VAT → (ฐาน, VAT) ตามอัตรา — ราคารวม VAT แบบที่ใบมัดจำที่พักออก
    /// (<c>PricesIncludeVat=true</c>): base = round(gross × 100/(100+rate)) · vat = gross − base
    /// (1,000 @7% → 934.58 / 65.42)</summary>
    public static (decimal Base, decimal Vat) SplitInclusive(decimal gross, decimal vatRate)
    {
        if (vatRate <= 0m) return (gross, 0m);
        var b = R2(gross * 100m / (100m + vatRate));
        return (b, gross - b);
    }

    /// <summary>ยอดคงเหลือของใบมัดจำ — ฐานใช้สูตรเดียวกับด่านใน DocumentService
    /// (SubTotal − รับรู้แล้ว − round(คืนแล้ว × (1−VAT/Total)))</summary>
    public static DepositRemaining Remaining(LodgingDepositSnapshot d)
    {
        if (d.RealizedBase <= 0m && d.RefundedGross <= 0m)
            return new DepositRemaining(d.SubTotal, d.VatAmount, d.TotalAmount);
        var portion = d.TotalAmount > 0m ? d.VatAmount / d.TotalAmount : 0m;
        var refundedBase = R2(d.RefundedGross * (1 - portion));
        var remBase = Math.Max(0m, d.SubTotal - d.RealizedBase - refundedBase);
        var remVat = d.SubTotal > 0m ? R2(remBase * d.VatAmount / d.SubTotal) : 0m;
        return new DepositRemaining(remBase, remVat, remBase + remVat);
    }

    /// <summary>ใบมัดจำนี้ "ออกใบกำกับไปแล้ว" (VAT เข้า ภ.พ.30 แล้ว) หรือไม่</summary>
    private static bool TaxedAtReceipt(LodgingDepositSnapshot d) => d.VatAmount > 0.005m && !d.VatPending;

    /// <summary>แผนเช็คเอาต์ — มัดจำที่ยังเหลือทุกใบถูกใช้ครบ (ไม่มีมัดจำค้างหลังเช็คเอาต์)</summary>
    public static LodgingCheckoutDepositPlan PlanCheckout(IEnumerable<LodgingDepositSnapshot> deposits)
    {
        var deduct = new List<TaxedDepositDeduction>();
        var apply = new List<PendingVatDepositApply>();
        foreach (var d in deposits)
        {
            var rem = Remaining(d);
            if (rem.Base <= 0.005m && rem.Gross <= 0.005m) continue;
            if (TaxedAtReceipt(d)) deduct.Add(new TaxedDepositDeduction(d.Id, d.Number, rem.Base, rem.Vat, rem.Gross));
            else apply.Add(new PendingVatDepositApply(d.Id, d.Number, d.ContactId, rem.Gross));
        }
        return new LodgingCheckoutDepositPlan(deduct, apply);
    }

    /// <summary>ภาษีขายของใบสุดท้ายเมื่อหักฐานมัดจำที่ออกใบกำกับแล้ว (ราคาไม่รวม VAT ของฐานคงเหลือ) —
    /// ใช้ตรวจ/อธิบาย ไม่ใช่ตัวคำนวณจริงของเอกสาร (ตัวจริงคือ DocumentService.ComputeLineAmounts)</summary>
    public static decimal FinalInvoiceVat(decimal fullBase, decimal depositBaseDeducted, decimal vatRate)
        => vatRate <= 0m ? 0m : R2(Math.Max(0m, fullBase - depositBaseDeducted) * vatRate / 100m);

    /// <summary>แผนยกเลิก/no-show — ริบก่อน (ใบเก่าสุดก่อน) ที่เหลือคืน
    /// <para>ไม่มีใบมัดจำ (โหมดไม่ออกเอกสาร) → ใช้ยอดมัดจำบนการจอง แต่ไม่มีบรรทัดเอกสาร</para></summary>
    /// <param name="fee">ค่าปรับตามนโยบาย (gross)</param>
    /// <param name="depositPaidOnReservation">มัดจำที่รับไว้ตามการจอง (ใช้เมื่อไม่มีเอกสาร)</param>
    public static LodgingCancelPlan PlanCancellation(
        decimal fee, decimal depositPaidOnReservation, IReadOnlyList<LodgingDepositSnapshot> deposits)
    {
        fee = Math.Max(0m, R2(fee));
        if (deposits.Count == 0)
        {
            var dep = Math.Max(0m, depositPaidOnReservation);
            var forfeitNoDoc = Math.Min(dep, fee);
            return new LodgingCancelPlan(fee, forfeitNoDoc, dep - forfeitNoDoc, Array.Empty<LodgingCancelDepositLine>());
        }

        var lines = new List<LodgingCancelDepositLine>();
        var feeLeft = fee;
        decimal forfeit = 0m, refund = 0m;
        foreach (var d in deposits)
        {
            var rem = Remaining(d);
            if (rem.Gross <= 0.005m) continue;
            var forfeitGross = Math.Min(feeLeft, rem.Gross);
            feeLeft -= forfeitGross;
            var refundGross = rem.Gross - forfeitGross;
            var (refundBase, refundVat) = refundGross > 0m ? SplitRefund(refundGross, d.TotalAmount, d.VatAmount) : (0m, 0m);
            // ฐานที่ริบ = ฐานคงเหลือ − ฐานที่จะคืน (คำนวณด้วยสูตรเดียวกับตอนคืนจริง) ⇒ คืนภายหลังไม่ติดด่าน
            // "คืนเกินคงเหลือ" และ Σ ฐาน = ฐานของใบพอดีไม่มีเศษค้าง 217xx
            var forfeitBase = Math.Max(0m, rem.Base - refundBase);
            lines.Add(new LodgingCancelDepositLine(d.Id, d.Number, forfeitBase, refundGross, refundBase, refundVat));
            forfeit += forfeitGross; refund += refundGross;
        }
        return new LodgingCancelPlan(fee, forfeit, refund, lines);
    }

    /// <summary>ยอดที่ยังต้องคืน (ไม่ติดลบ)</summary>
    public static decimal RefundPending(decimal refundDue, decimal refundPaid) => Math.Max(0m, R2(refundDue - refundPaid));

    public static LodgingRefundState RefundStateOf(decimal refundDue, decimal refundPaid)
        => refundDue <= 0.005m ? LodgingRefundState.None
         : RefundPending(refundDue, refundPaid) <= 0.005m ? LodgingRefundState.Paid
         : LodgingRefundState.Pending;

    /// <summary>ตรวจยอดที่ขอบันทึกว่า "คืนแล้ว" — null = ใช้ได้ · ข้อความไทย = เหตุผลที่ปฏิเสธ</summary>
    public static string? ValidateRefundPayment(decimal amount, decimal refundDue, decimal refundPaid)
    {
        var pending = RefundPending(refundDue, refundPaid);
        if (pending <= 0.005m) return "การจองนี้ไม่มียอดค้างคืนเงิน";
        if (amount <= 0m) return "ยอดที่คืนต้องมากกว่า 0";
        if (amount > pending + 0.005m) return $"ยอดที่คืน ({amount:N2}) เกินยอดค้างคืน ({pending:N2})";
        return null;
    }

    /// <summary>แบ่งยอดที่คืนจริงลงใบมัดจำ — ใบล่าสุดก่อน (ตรงข้ามกับการริบที่เริ่มจากใบเก่าสุด)
    /// ⇒ ส่วนที่ต้องคืนกระจุกอยู่ในใบที่เหลือยอดจริง</summary>
    public static IReadOnlyList<(Guid Id, string Number, decimal Gross)> AllocateRefund(
        decimal amount, IReadOnlyList<LodgingDepositSnapshot> depositsOldestFirst)
    {
        var result = new List<(Guid, string, decimal)>();
        var left = R2(amount);
        for (var i = depositsOldestFirst.Count - 1; i >= 0 && left > 0.005m; i--)
        {
            var d = depositsOldestFirst[i];
            var rem = Remaining(d).Gross;
            if (rem <= 0.005m) continue;
            var take = i == 0 ? left : Math.Min(left, rem);   // ใบสุดท้ายที่เหลือรับเศษทั้งหมด (ด่านปลายทางตรวจซ้ำ)
            result.Add((d.Id, d.Number, take));
            left -= take;
        }
        return result;
    }
}
