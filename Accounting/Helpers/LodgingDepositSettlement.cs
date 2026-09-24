using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>ภาพถ่ายของใบมัดจำ 1 ใบ ณ ตอนตัดสิน (อ่านจาก <c>Document</c> ที่ <c>IsDeposit</c>)</summary>
/// <param name="VatPending">VAT ของใบนี้ยังพักอยู่ 21913 (ยังไม่เข้า ภ.พ.30) —
/// <c>DepositOutputVatDeferred && DepositOutputVatRecognizedAt == null</c></param>
/// <param name="RealizedBase">ฐานที่รับรู้/ตัดชำระไปแล้ว (<c>DepositRealizedAmount</c>)</param>
/// <param name="RefundedGross">ยอดที่คืนเงินไปแล้วรวม VAT (<c>DepositRefundedAmount</c>)</param>
/// <param name="AppliedToDocumentId">ใบที่มัดจำนี้ถูกนำไปตัดชำระ (<c>DepositAppliedToDocumentId</c>) — ใช้ตอนทำเช็คเอาต์ที่ค้างต่อ</param>
public sealed record LodgingDepositSnapshot(
    Guid Id, string Number, Guid ContactId,
    decimal SubTotal, decimal VatAmount, decimal TotalAmount,
    bool VatPending, decimal RealizedBase, decimal RefundedGross,
    Guid? AppliedToDocumentId = null);

/// <summary>ยอดคงเหลือของใบมัดจำ 1 ใบ</summary>
public readonly record struct DepositRemaining(decimal Base, decimal Vat, decimal Gross);

/// <summary>ใบมัดจำ 1 ใบที่ "ออกใบกำกับไปแล้ว" → หักฐานออกจากใบสุดท้าย + รับรู้ฐานเป็นรายได้</summary>
public sealed record TaxedDepositDeduction(Guid Id, string Number, decimal Base, decimal Vat, decimal Gross);

/// <summary>ใบมัดจำ 1 ใบที่ VAT ยังพักรอ (หรือไม่มี VAT) → นำเงินไปตัดชำระใบสุดท้าย (ApplyDeposit)</summary>
public sealed record PendingVatDepositApply(Guid Id, string Number, Guid ContactId, decimal Gross);

/// <summary>มัดจำส่วนที่ <b>เกินยอดใบสุดท้าย</b> — เป็นยอดค้างคืนแขก (คืนด้วยกลไก "ยืนยันคืนเงินแล้ว" ชุดเดียวกับการยกเลิก)</summary>
public sealed record LodgingDepositExcess(Guid Id, string Number, decimal Gross);

/// <summary>แผนเช็คเอาต์ — ใบสุดท้ายเอามัดจำแต่ละใบไปใช้อย่างไร และเหลือคืนเท่าไร</summary>
public sealed record LodgingCheckoutDepositPlan(
    IReadOnlyList<TaxedDepositDeduction> Deduct,
    IReadOnlyList<PendingVatDepositApply> Apply,
    IReadOnlyList<LodgingDepositExcess> Excess)
{
    /// <summary>ฐานภาษีที่หักออกจากใบสุดท้าย (= <c>BillDiscountAmount</c>)</summary>
    public decimal BaseDeducted => Deduct.Sum(x => x.Base);
    public decimal VatDeducted => Deduct.Sum(x => x.Vat);
    public decimal GrossDeducted => Deduct.Sum(x => x.Gross);
    public decimal GrossApplied => Apply.Sum(x => x.Gross);
    /// <summary>มัดจำที่เกินยอดใบสุดท้าย — ต้องคืนแขก (0 = ไม่มี)</summary>
    public decimal ExcessGross => Excess.Sum(x => x.Gross);
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

/// <summary>สถานะการคืนเงินของการจอง — ตัดสินจาก "ยอดต้องคืน" กับ "ยอดที่ยืนยันว่าคืนแล้ว" เท่านั้น</summary>
public enum LodgingRefundState
{
    /// <summary>ไม่มีอะไรต้องคืน</summary>
    None,
    /// <summary>ต้องคืนแต่ยังไม่มีหลักฐานว่าคืนแล้ว (ทั้งหมดหรือบางส่วน)</summary>
    Pending,
    /// <summary>ยืนยันแล้วว่าคืนครบ</summary>
    Paid,
    /// <summary>การยกเลิกก่อนรอบ 193 — ระบบเดิมลงบัญชีคืนเงินให้ตอนกดยกเลิกโดยไม่มีหลักฐานการโอน ⇒ "ไม่มีข้อมูลการโอนคืน"
    /// (ห้ามแสดงว่า "คืนแล้ว" และห้ามลงคืนซ้ำ — JE/ใบลดหนี้เดิมลงไปแล้ว)</summary>
    Unknown,
}

/// <summary>
/// ตัวคำนวณ pure ของเงินมัดจำโมดูลที่พัก (รอบ 193 · #34 + F-03 · แก้หลังฝ่ายค้าน C2/C6/C7/C10)
///
/// ═══ เช็คเอาต์ (#34) ═══
/// มัดจำที่ <b>ออกใบกำกับไปแล้ว</b> (VAT 21911 เข้า ภ.พ.30 เดือนที่รับเงิน) ห้ามให้ใบสุดท้ายรายงาน VAT เต็มใบอีก
/// ⇒ ใบสุดท้าย "หักมูลค่ามัดจำที่ออกใบกำกับแล้วออกจากฐาน" แล้วรับรู้ฐานของมัดจำเป็นรายได้ (<c>RealizeDepositAsync</c>) ·
/// มัดจำที่ VAT <b>ยังพักรอ</b> (21913) หรือไม่มี VAT ใช้ <c>ApplyDepositToInvoiceAsync</c> ·
/// <b>มัดจำเกินยอดใบสุดท้าย</b> (เลื่อนวันจนยอดลด · ยกเลิกรายการ folio) ⇒ ใช้เท่าที่ใบสุดท้ายรับได้ ส่วนเกิน = ค้างคืนแขก
///
/// ═══ ยกเลิก (F-03) ═══
/// ค่าปรับ = ส่วนที่ริบ → รับรู้ทันที · ส่วนที่ต้องคืน = <b>ภาระค้างคืน</b> — JE คืนเงิน + ใบลดหนี้เกิดตอนมีหลักฐาน
/// ว่าคืนจริงเท่านั้น (DECISION_DOCTRINE R1)
///
/// ═══ สูตรปัดตัวเดียว (C6) ═══
/// ยอดที่ "คืนได้" ของใบมัดจำคำนวณด้วย <see cref="RefundableGross"/> ตัวเดียว ทั้งตอนวางแผนยกเลิก ตอนแบ่งยอดคืน
/// และตอนคิดส่วนเกินของเช็คเอาต์ — ยึดสูตรแยกฐาน/VAT ของ <c>DocumentService.RefundDepositAsync</c>
/// (vat = round(gross × VAT/Total) AwayFromZero) ⇒ เดิมสองจุดปัดต่างกัน ค้าง 0.01 ถาวรกรณีมัดจำหลายใบ
/// </summary>
public static class LodgingDepositSettlement
{
    /// <summary>ป้ายใน <c>RefundPaidBy</c> ของแถวยกเลิกก่อนรอบ 193 (migration) — ไม่ใช่หลักฐานการโอน</summary>
    public const string LegacyRefundMarker = "legacy:posted-at-cancel";

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>แยก gross ที่คืน → (ฐาน, VAT) ด้วยสูตรเดียวกับ <c>RefundDepositAsync</c>:
    /// vat = round(gross × Vat/Total) · base = gross − vat</summary>
    private static (decimal Base, decimal Vat) SplitRefund(decimal gross, decimal docTotal, decimal docVat)
    {
        var portion = docTotal > 0m ? docVat / docTotal : 0m;
        var vat = R2(gross * portion);
        return (gross - vat, vat);
    }

    /// <summary>ฐานของยอดที่ตัดชำระ — สูตรเดียวกับ <c>ApplyDepositToInvoiceCoreAsync</c>: base = round(gross × (1 − Vat/Total))</summary>
    private static decimal ApplyBase(decimal gross, decimal docTotal, decimal docVat)
        => R2(gross * (1 - (docTotal > 0m ? docVat / docTotal : 0m)));

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

    /// <summary>ยอด gross สูงสุดที่คืนจากใบนี้ได้โดยผ่านด่าน "คืนเกินคงเหลือ" ของ <c>RefundDepositAsync</c>
    /// และล้างฐานคงเหลือหมดพอดี — <b>สูตรปัดตัวเดียว</b> ของการวางแผนยกเลิก · การแบ่งยอดคืน · ส่วนเกินตอนเช็คเอาต์ (C6)
    /// <para>ใบที่ยังไม่ถูกแตะ = ยอดรวมของใบ · ใบที่ถูกรับรู้/คืนบางส่วน = gross ที่มากที่สุดซึ่งแยกฐานแล้ว ≤ ฐานคงเหลือ
    /// (Remaining.Gross ปัด VAT จากฐาน ส่วน RefundDepositAsync ปัดจาก gross ⇒ ต่างกันได้ ±0.01)</para></summary>
    private static decimal RefundableGross(LodgingDepositSnapshot d)
    {
        var rem = Remaining(d);
        if (rem.Base <= 0.005m) return 0m;
        if (d.RealizedBase <= 0m && d.RefundedGross <= 0m) return d.TotalAmount;
        var best = 0m;
        for (var k = -3; k <= 3; k++)
        {
            var g = rem.Gross + 0.01m * k;
            if (g <= 0m) continue;
            if (SplitRefund(g, d.TotalAmount, d.VatAmount).Base <= rem.Base + 0.0001m) best = Math.Max(best, g);
        }
        return best;
    }

    /// <summary>ใบมัดจำนี้ "ออกใบกำกับไปแล้ว" (VAT เข้า ภ.พ.30 แล้ว) หรือไม่</summary>
    private static bool TaxedAtReceipt(LodgingDepositSnapshot d) => d.VatAmount > 0.005m && !d.VatPending;

    /// <summary>มีมัดจำ "ออกใบกำกับแล้ว" ที่ยังเหลือยอดไหม — ใช้ตัดสินก่อนออกใบ (ใบสุดท้ายต้องเป็นใบกำกับจึงหักฐานได้)</summary>
    public static bool HasTaxedRemaining(IEnumerable<LodgingDepositSnapshot> deposits)
        => deposits.Any(d => TaxedAtReceipt(d) && Remaining(d).Base > 0.005m);

    /// <summary>ใบมัดจำที่ต้อง "ตัดชำระ" กับใบสุดท้าย (VAT พัก/เต็มยอด) และยังเหลือยอด — ต้องเป็นลูกค้ารายเดียวกับใบสุดท้าย</summary>
    public static IEnumerable<LodgingDepositSnapshot> ApplyCandidates(IEnumerable<LodgingDepositSnapshot> deposits, Guid? finalDocumentId = null)
        => deposits.Where(d => !TaxedAtReceipt(d) && Remaining(d).Gross > 0.005m
                               && (finalDocumentId == null || d.AppliedToDocumentId != finalDocumentId));

    /// <summary>แผนเช็คเอาต์ — ใช้มัดจำเท่าที่ใบสุดท้ายรับได้ ส่วนเกินเป็นค้างคืน (C2)
    /// <list type="number">
    /// <item>มัดจำออกใบกำกับแล้ว (ใบเก่าสุดก่อน): หักฐานได้ไม่เกิน <paramref name="taxedBaseCapacity"/>
    ///   (ฐานเต็มของใบสุดท้าย) · ส่วนที่เหลือ = ค้างคืน</item>
    /// <item>มัดจำ VAT พัก/เต็มยอด (ใบเก่าสุดก่อน): ตัดชำระได้ไม่เกินยอดใบสุดท้ายหลังหักฐาน
    ///   (<paramref name="balanceAfterDeduction"/> = ตัวคำนวณจริงของเอกสาร) · ส่วนที่เหลือ = ค้างคืน</item>
    /// </list>
    /// ผู้เรียกต้องวางแผน <b>ก่อน</b> ออกเลขใบสุดท้าย — ห้ามรู้ว่าตัดไม่ได้หลังประทับเลข (§86/4 gap-free)</summary>
    /// <param name="taxedBaseCapacity">ฐานที่ใบสุดท้ายหักได้ (ฐานเต็มของใบ · ตอนทำต่อ = ส่วนที่ยังไม่รับรู้)</param>
    /// <param name="balanceAfterDeduction">ยอดค้างของใบสุดท้ายเมื่อหักฐานเท่านี้ (ตัวคำนวณของ DocumentService)</param>
    /// <param name="finalDocumentId">ตอนทำเช็คเอาต์ที่ค้างต่อ: มัดจำที่ตัดชำระใบนี้ไปแล้วไม่ตัดซ้ำ (ส่วนที่เหลือของมันคือส่วนเกิน)</param>
    public static LodgingCheckoutDepositPlan PlanCheckout(
        IReadOnlyList<LodgingDepositSnapshot> deposits, decimal taxedBaseCapacity,
        Func<decimal, decimal> balanceAfterDeduction, Guid? finalDocumentId = null)
    {
        var deduct = new List<TaxedDepositDeduction>();
        var apply = new List<PendingVatDepositApply>();
        var excess = new List<LodgingDepositExcess>();
        var capacity = Math.Max(0m, taxedBaseCapacity);
        foreach (var d in deposits.Where(TaxedAtReceipt))
        {
            var rem = Remaining(d);
            if (rem.Base <= 0.005m) continue;
            var take = Math.Min(rem.Base, capacity);
            capacity -= take;
            if (take > 0.005m)
            {
                var vat = take == rem.Base ? rem.Vat : (d.SubTotal > 0m ? R2(take * d.VatAmount / d.SubTotal) : 0m);
                deduct.Add(new TaxedDepositDeduction(d.Id, d.Number, take, vat, take + vat));
            }
            if (rem.Base - take > 0.005m)
                excess.Add(new LodgingDepositExcess(d.Id, d.Number, RefundableGross(d with { RealizedBase = d.RealizedBase + take })));
        }

        var balance = Math.Max(0m, balanceAfterDeduction(deduct.Sum(x => x.Base)));
        foreach (var d in deposits.Where(x => !TaxedAtReceipt(x)))
        {
            var rem = Remaining(d);
            if (rem.Gross <= 0.005m) continue;
            if (finalDocumentId != null && d.AppliedToDocumentId == finalDocumentId)
            {
                // ตัดชำระใบนี้ไปแล้ว (ทำต่อหลังล้มกลางทาง) — ที่เหลือคือส่วนเกินที่ต้องคืน
                excess.Add(new LodgingDepositExcess(d.Id, d.Number, RefundableGross(d)));
                continue;
            }
            var use = Math.Min(rem.Gross, balance);
            balance -= use;
            if (use > 0.005m) apply.Add(new PendingVatDepositApply(d.Id, d.Number, d.ContactId, use));
            if (rem.Gross - use > 0.005m)
            {
                var after = use > 0.005m ? d with { RealizedBase = d.RealizedBase + ApplyBase(use, d.TotalAmount, d.VatAmount) } : d;
                excess.Add(new LodgingDepositExcess(d.Id, d.Number, RefundableGross(after)));
            }
        }
        return new LodgingCheckoutDepositPlan(deduct, apply, excess.Where(x => x.Gross > 0.005m).ToList());
    }

    /// <summary>ภาษีขายของใบสุดท้ายเมื่อหักฐานมัดจำที่ออกใบกำกับแล้ว (ราคาไม่รวม VAT ของฐานคงเหลือ) —
    /// ใช้ตรวจ/อธิบาย ไม่ใช่ตัวคำนวณจริงของเอกสาร (ตัวจริงคือ DocumentService.ComputeLineAmounts)</summary>
    public static decimal FinalInvoiceVat(decimal fullBase, decimal depositBaseDeducted, decimal vatRate)
        => vatRate <= 0m ? 0m : R2(Math.Max(0m, fullBase - depositBaseDeducted) * vatRate / 100m);

    /// <summary>ส่วนต่างปัดเศษของโหมด VAT ทันที (C7): ยอดใบสุดท้าย − (ยอดเต็ม − มัดจำ gross ที่หัก)
    /// <para>ทิศการปัดที่เลือก (ชัดเจน · ตัวเดียว): ใบสุดท้ายคิด VAT บน<b>ฐานของใบเอง</b> ปัด AwayFromZero เหมือนทุกเอกสาร
    /// (VAT คิดรายใบตามกฎหมาย) และ<b>ฐานไม่เพี้ยน</b> (ฐานมัดจำ + ฐานใบสุดท้าย = ฐานเต็มพอดี) ⇒ ส่วนต่างทั้งหมดอยู่ที่ VAT
    /// ไม่เกิน ±0.01 ต่อใบมัดจำ · ไม่ปรับยอดใบเพื่อ "ให้ลงตัว" (ยอดหักต้องตรงกับใบกำกับมัดจำเสมอ) แต่บันทึกให้เห็น</para></summary>
    public static decimal RoundingDelta(decimal fullTotal, decimal grossDeducted, decimal finalTotal)
        => R2(finalTotal - (fullTotal - grossDeducted));

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
            var refundable = RefundableGross(d);
            if (refundable <= 0.005m) continue;
            var forfeitGross = Math.Min(feeLeft, refundable);
            feeLeft -= forfeitGross;
            var refundGross = refundable - forfeitGross;
            var (refundBase, refundVat) = refundGross > 0m ? SplitRefund(refundGross, d.TotalAmount, d.VatAmount) : (0m, 0m);
            // ฐานที่ริบ = ฐานคงเหลือ − ฐานที่จะคืน (คำนวณด้วยสูตรเดียวกับตอนคืนจริง) ⇒ คืนภายหลังไม่ติดด่าน
            // "คืนเกินคงเหลือ" และ Σ ฐาน = ฐานของใบพอดีไม่มีเศษค้าง 217xx
            var forfeitBase = Math.Max(0m, Remaining(d).Base - refundBase);
            lines.Add(new LodgingCancelDepositLine(d.Id, d.Number, forfeitBase, refundGross, refundBase, refundVat));
            forfeit += forfeitGross; refund += refundGross;
        }
        return new LodgingCancelPlan(fee, forfeit, refund, lines);
    }

    /// <summary>สถานะหลังบันทึกรับมัดจำ (S-06 รอบ 193 · ต่อสาย <c>AutoConfirmOnDeposit</c> ที่เดิมไม่มีใครอ่าน)
    /// <list type="bullet">
    /// <item>พนักงานกด "ยืนยัน" เอง → ยืนยันเสมอ (การกระทำนั้นคือการยืนยัน)</item>
    /// <item>เงินเข้าเองจากช่องทางออนไลน์ หรือพนักงานกด "รับชำระเพิ่ม" → ยืนยันเฉพาะเมื่อที่พักเปิด "ยืนยันอัตโนมัติเมื่อได้รับมัดจำ"</item>
    /// <item>การจองที่ไม่ใช่ "รอมัดจำ" → สถานะไม่เปลี่ยน (เดิมรับชำระเพิ่มบนการจองที่เช็คอินแล้วทำให้ย้อนกลับเป็น "ยืนยันแล้ว")</item>
    /// </list></summary>
    public static LodgingReservationStatus StatusAfterDeposit(
        LodgingReservationStatus current, bool autoConfirmOnDeposit, bool explicitStaffConfirm)
        => current != LodgingReservationStatus.Pending ? current
         : explicitStaffConfirm || autoConfirmOnDeposit ? LodgingReservationStatus.Confirmed
         : LodgingReservationStatus.Pending;

    /// <summary>ยอดที่ยังต้องคืน (ไม่ติดลบ)</summary>
    public static decimal RefundPending(decimal refundDue, decimal refundPaid) => Math.Max(0m, R2(refundDue - refundPaid));

    /// <summary>แถวยกเลิกก่อนรอบ 193 ที่ระบบเดิมลงคืนเงินให้เอง (ไม่มีหลักฐานการโอน)</summary>
    public static bool IsLegacyRefund(string? refundPaidBy)
        => refundPaidBy != null && refundPaidBy.StartsWith(LegacyRefundMarker, StringComparison.Ordinal);

    public static LodgingRefundState RefundStateOf(decimal refundDue, decimal refundPaid, string? refundPaidBy)
        => refundDue <= 0.005m ? LodgingRefundState.None
         : IsLegacyRefund(refundPaidBy) ? LodgingRefundState.Unknown
         : RefundPending(refundDue, refundPaid) <= 0.005m ? LodgingRefundState.Paid
         : LodgingRefundState.Pending;

    /// <summary>ยอดค้างคืนที่แสดง/ให้กดยืนยันได้ — แถว legacy = 0 (ระบบเดิมลงคืนเงินไปแล้ว ห้ามลงซ้ำ)</summary>
    public static decimal RefundPendingOf(decimal refundDue, decimal refundPaid, string? refundPaidBy)
        => IsLegacyRefund(refundPaidBy) ? 0m : RefundPending(refundDue, refundPaid);

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
    /// ⇒ ส่วนที่ต้องคืนกระจุกอยู่ในใบที่เหลือยอดจริง · ต่อใบไม่เกิน <see cref="RefundableGross"/> (สูตรเดียวกับแผนยกเลิก ·
    /// เดิมใบเก่าสุดรับ "เศษทั้งหมด" แต่ใบอื่นใช้ Remaining.Gross ที่ปัดคนละทาง ⇒ ค้าง 0.01 ถาวร)</summary>
    public static IReadOnlyList<(Guid Id, string Number, decimal Gross)> AllocateRefund(
        decimal amount, IReadOnlyList<LodgingDepositSnapshot> depositsOldestFirst)
    {
        var result = new List<(Guid, string, decimal)>();
        var left = R2(amount);
        for (var i = depositsOldestFirst.Count - 1; i >= 0 && left > 0.005m; i--)
        {
            var d = depositsOldestFirst[i];
            var refundable = RefundableGross(d);
            if (refundable <= 0.005m) continue;
            var take = Math.Min(left, refundable);
            result.Add((d.Id, d.Number, take));
            left -= take;
        }
        return result;
    }
}
