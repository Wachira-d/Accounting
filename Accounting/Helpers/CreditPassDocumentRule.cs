using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียว "เอกสารขายที่ผูกบัตรคอร์ส ถูกยกเลิก/ลดหนี้ได้ไหม" + "เปิดบัตรจากเอกสารได้ไหม" (ทีม L · ฝ่ายค้านรอบ 1) — pure
///
/// <para><b>ที่มา:</b> ยกเลิกใบขายที่ใช้เครดิตไปแล้ว ⇒ JE กลับ Dr 21713 เต็มยอด แต่รายได้ที่รับรู้แล้วไม่ถูกกลับ ⇒ 21713 ติดลบ + บัตรค้าง "ใช้งานได้" ·
/// ใบเสร็จที่ตัดลูกหนี้ใบแจ้งหนี้ (ไม่ Cr 21713) เคยเปิดบัตรได้จากยอดบรรทัด ⇒ สองบัตรจากเงินก้อนเดียว</para>
///
/// <para><b>กติกา:</b>
/// <list type="bullet">
/// <item>ยกเลิกใบขาย: บัตรยังไม่ถูกใช้เลย (ใช้ 0 · จอง 0 · รับรู้ 0) ⇒ ยกเลิกได้ + ปิดบัตรในธุรกรรมเดียวกัน · ใช้แล้ว ⇒ บล็อก (ทางไปต่อ: ใบลดหนี้ส่วนที่ยังไม่ใช้)</item>
/// <item>ใบลดหนี้อ้างใบขาย (ทุกบรรทัดต้องลงผังรายได้รับล่วงหน้าของบัตร): ยอดสุทธิ<b>เท่ากับ</b>ส่วนที่ยังไม่รับรู้ (±0.01) + ไม่มีการจองค้าง ⇒
///   คืนเงินทั้งหมดที่เหลือ + ปิดบัตร · ยอด<b>น้อยกว่า</b>ส่วนที่ยังไม่รับรู้ (รอบ 205 W6 · R-38 คืนเงินบางส่วน) ⇒ ลดเครดิต
///   <c>floor(ยอด ÷ ราคาต่อเครดิตตามสัญญา)</c> โดยเครดิตคงเหลือหลังลดต้อง<b>มากกว่า</b>ที่ใช้ไปแล้ว+จองไว้ (ห้ามเหลือบัตรที่ใช้ต่อไม่ได้แต่ยังเปิด) ·
///   บัตรยัง Active · ยอดเกินส่วนที่เหลือ / น้อยกว่าราคา 1 เครดิต ⇒ บล็อกพร้อมยอดที่ออกได้</item>
/// <item>ยกเลิกใบลดหนี้ที่ปิดบัตรไปแล้ว ⇒ บล็อก (บัตรเปิดกลับไม่ได้)</item>
/// <item>เปิดบัตรจากเอกสาร: ยอดสัญญา = เครดิตสุทธิของ JE ที่ลงผังรายได้รับล่วงหน้า (บาท) · ≤ 0 ⇒ ไม่เปิด · สายเอกสารเดียวกันมีบัตรแล้ว ⇒ ไม่เปิด</item>
/// </list></para>
/// </summary>
public static class CreditPassDocumentRule
{
    public const string RuleCode = "TFRS-NPAE-6-CREDIT-DOC";

    /// <param name="CreditsTotal">เครดิตทั้งหมดของบัตร (หลังหักที่ถูกลดด้วยใบลดหนี้บางส่วนแล้ว) — ใช้คิดราคาต่อเครดิตของการคืนบางส่วน · 0 = ไม่รองรับคืนบางส่วน</param>
    public sealed record PassFacts(string PassNumber, CreditPassStatus Status, decimal CreditsUsed, decimal CreditsReserved,
        decimal ContractAmount, decimal RecognizedAmount, decimal RefundedAmount,
        decimal CreditsTotal = 0m, CreditUnit Unit = CreditUnit.Session)
    {
        public decimal Deferred => Math.Max(0m, ContractAmount - RecognizedAmount - RefundedAmount);
        public bool Untouched => CreditsUsed <= 0m && CreditsReserved <= 0m && RecognizedAmount <= 0m;
        /// <summary>ราคาต่อเครดิตตามสัญญาปัจจุบัน = (สัญญา − คืนแล้ว) ÷ เครดิตทั้งหมด (ตัวเดียวกับที่ใช้รับรู้รายได้)</summary>
        public decimal PricePerCredit => CourseCreditMath.PricePerCredit(CourseCreditMath.EffectiveContract(ContractAmount, RefundedAmount), CreditsTotal);
        /// <summary>เครดิตที่ลดได้สูงสุดโดยยังเหลือให้ใช้อย่างน้อย 1 หน่วยนับ (คงเหลือ &gt; ใช้แล้ว+จองไว้)</summary>
        public decimal MaxRemovableCredits => Math.Max(0m, CreditsTotal - CreditsUsed - CreditsReserved - CourseCreditMath.CreditStep(Unit));
    }

    public enum DocAction { None = 0, CancelPass = 1, Block = 2, ReduceCredits = 3 }

    /// <param name="CreditsToRemove">เฉพาะ <see cref="DocAction.ReduceCredits"/> — เครดิตที่ตัดออกจากบัตร (บัตรยัง Active)</param>
    public sealed record Decision(DocAction Action, string? Message, decimal CreditsToRemove = 0m)
    {
        public static readonly Decision Nothing = new(DocAction.None, null);
    }

    /// <summary>ยกเลิกใบขายที่บัตร <paramref name="pass"/> ผูกอยู่ (null = ใบนี้ไม่ผูกบัตร / บัตรถูกปิดไปแล้ว)</summary>
    public static Decision OnVoidSale(PassFacts? pass, string documentNumber)
    {
        if (pass == null || pass.Status == CreditPassStatus.Cancelled) return Decision.Nothing;
        if (pass.Untouched) return new(DocAction.CancelPass, null);
        return new(DocAction.Block,
            $"เอกสาร {documentNumber} เป็นใบขายของบัตรคอร์ส {pass.PassNumber} ที่ใช้ไปแล้ว (ใช้ {pass.CreditsUsed:0.##} · จองค้าง {pass.CreditsReserved:0.##} · "
            + $"รับรู้รายได้แล้ว {pass.RecognizedAmount:N2}) — ยกเลิกทั้งใบไม่ได้ (รายได้ที่รับรู้แล้วจะไม่ถูกกลับ · ค่าบริการรับล่วงหน้าติดลบ) · "
            + (pass.Deferred > 0m
                ? $"ทางไปต่อ: {(pass.CreditsReserved > 0m ? "ยกเลิก/ปิดการจองค้างก่อน แล้ว" : "")}ออกใบลดหนี้คืนเงินส่วนที่ยังไม่ใช้ ยอดสุทธิก่อน VAT {pass.Deferred:N2} บาท (ระบบปิดบัตรให้)"
                : "บัตรรับรู้รายได้ครบแล้ว — ไม่มียอดให้คืน"));
    }

    /// <summary>อนุมัติใบลดหนี้ที่อ้างใบขายของบัตร — <paramref name="creditNoteNet"/> = ยอดสุทธิก่อน VAT ของใบลดหนี้ ·
    /// <paramref name="allLinesToUnearned"/> = ทุกบรรทัดลงผังรายได้รับล่วงหน้าของบัตร (หรือไม่ระบุผัง ⇒ ลงกลับผังเดิม)</summary>
    public static Decision OnCreditNote(PassFacts? pass, string sourceNumber, decimal creditNoteNet, bool allLinesToUnearned)
    {
        if (pass == null || pass.Status == CreditPassStatus.Cancelled) return Decision.Nothing;
        var head = $"ใบลดหนี้อ้าง {sourceNumber} ซึ่งเป็นใบขายของบัตรคอร์ส {pass.PassNumber} — ";
        if (pass.Deferred <= 0m)
            return new(DocAction.Block, head + "บัตรรับรู้รายได้ครบแล้ว (ไม่มีค่าบริการรับล่วงหน้าเหลือ) ลดหนี้จากใบนี้ไม่ได้");
        if (pass.CreditsReserved > 0m)
            return new(DocAction.Block, head + $"มีการจองค้าง {pass.CreditsReserved:0.##} เครดิต — ยกเลิก/ปิดการจองก่อนแล้วออกใบลดหนี้ใหม่");
        if (!allLinesToUnearned)
            return new(DocAction.Block, head + "บรรทัดใบลดหนี้ต้องลงผังค่าบริการรับล่วงหน้าของบัตร (ไม่ใช่ผังรายได้) — แก้ผังบรรทัดแล้วอนุมัติใหม่");
        var plan = RefundPreview(pass, creditNoteNet);
        if (plan.Problem != null) return new(DocAction.Block, head + plan.Problem);
        return plan.ClosesPass ? new(DocAction.CancelPass, null) : new(DocAction.ReduceCredits, null, plan.CreditsToRemove);
    }

    /// <summary>ผลของการคืนเงิน <paramref name="Net"/> (สุทธิก่อน VAT) จากบัตร — ปิดบัตร / ลดเครดิต / หรือเหตุที่ทำไม่ได้พร้อมยอดที่ออกได้</summary>
    public sealed record RefundPlan(decimal Net, decimal CreditsToRemove, decimal CreditsRemaining, bool ClosesPass, string? Problem);

    /// <summary>วางแผนคืนเงินจากบัตร — ตัวตัดสินเดียวของทั้งด่านอนุมัติใบลดหนี้ (<see cref="OnCreditNote"/>) และปุ่ม "ออกใบลดหนี้คืนเงิน (ร่าง)" บนหน้าบัตร ·
    /// ยอด = ส่วนที่เหลือ (±0.01) ⇒ ปิดบัตร (ต้องไม่มีการจองค้าง) · ยอดน้อยกว่า ⇒ ลดเครดิต floor(ยอด ÷ ราคาต่อเครดิต) โดยคงเหลือต้อง &gt; ใช้แล้ว+จองไว้ ·
    /// ยอดเกิน / น้อยกว่าราคา 1 หน่วย ⇒ Problem</summary>
    public static RefundPlan RefundPreview(PassFacts pass, decimal net)
    {
        if (pass.Deferred <= 0m) return new(net, 0m, pass.CreditsTotal, false, "บัตรรับรู้รายได้ครบแล้ว (ไม่มีค่าบริการรับล่วงหน้าเหลือ) — ไม่มียอดให้คืน");
        if (net <= 0m) return new(net, 0m, pass.CreditsTotal, false, "ยอดสุทธิก่อน VAT ต้องมากกว่า 0");
        if (net > pass.Deferred + 0.01m)
            return new(net, 0m, pass.CreditsTotal, false,
                $"ยอดสุทธิก่อน VAT {net:N2} เกินส่วนที่ยังไม่ใช้ {pass.Deferred:N2} บาท — ออกได้สูงสุด {pass.Deferred:N2} (คืนทั้งส่วนที่เหลือ ⇒ ระบบปิดบัตร)");
        if (Math.Abs(net - pass.Deferred) <= 0.01m)
        {
            if (pass.CreditsReserved > 0m)
                return new(net, 0m, pass.CreditsTotal, true,
                    $"มีการจองค้าง {pass.CreditsReserved:0.##} เครดิต — ยกเลิก/ปิดการจองก่อนจึงคืนทั้งส่วนที่เหลือได้"
                    + (pass.CreditsTotal > 0m && MaxPartialAmount(pass) > 0m ? $" หรือคืนบางส่วนไม่เกิน {MaxPartialAmount(pass):N2} บาท" : ""));
            return new(net, Math.Max(0m, pass.CreditsTotal - pass.CreditsUsed), 0m, true, null);
        }
        // คืนเงินบางส่วน (R-38)
        if (pass.CreditsTotal <= 0m)
            return new(net, 0m, pass.CreditsTotal, false, $"บัตรนี้ไม่รองรับการคืนเงินบางส่วน — ออกใบลดหนี้เท่าส่วนที่เหลือ {pass.Deferred:N2} บาท (ระบบปิดบัตร)");
        var price = pass.PricePerCredit;
        var credits = CourseCreditMath.CreditsRefundable(net, price, pass.Unit);
        if (credits <= 0m)
            return new(net, 0m, pass.CreditsTotal, false,
                $"ยอด {net:N2} น้อยกว่าราคา {CourseCreditMath.CreditStep(pass.Unit):0.##} เครดิต ({price:N2} บาท/เครดิต) — ไม่มีเครดิตให้ลด · "
                + $"ขั้นต่ำ {Math.Round(price * CourseCreditMath.CreditStep(pass.Unit), 2, MidpointRounding.AwayFromZero):N2} บาท หรือคืนทั้งส่วนที่เหลือ {pass.Deferred:N2} บาท");
        var remaining = pass.CreditsTotal - credits;
        if (remaining <= pass.CreditsUsed + pass.CreditsReserved)
        {
            var maxCredits = pass.MaxRemovableCredits;
            return new(net, 0m, pass.CreditsTotal, false,
                $"ลด {credits:0.##} เครดิตแล้วจะเหลือ {remaining:0.##} ซึ่งไม่เกินที่ใช้แล้ว {pass.CreditsUsed:0.##} + จองไว้ {pass.CreditsReserved:0.##} — "
                + (maxCredits > 0m
                    ? $"คืนบางส่วนได้ไม่เกิน {maxCredits:0.##} เครดิต ({MaxPartialAmount(pass):N2} บาท) หรือคืนทั้งส่วนที่เหลือ {pass.Deferred:N2} บาท (ระบบปิดบัตร)"
                    : $"คืนได้เฉพาะทั้งส่วนที่เหลือ {pass.Deferred:N2} บาท (ระบบปิดบัตร)"));
        }
        return new(net, credits, remaining, false, null);
    }

    /// <summary>ยอดคืนบางส่วนสูงสุด (บาท) = เครดิตที่ลดได้สูงสุด × ราคาต่อเครดิต (ไม่เกินส่วนที่เหลือ)</summary>
    public static decimal MaxPartialAmount(PassFacts pass)
        => Math.Min(pass.Deferred, Math.Round(pass.MaxRemovableCredits * pass.PricePerCredit, 2, MidpointRounding.AwayFromZero));

    /// <summary>ยกเลิกใบลดหนี้ — ใบนี้เป็นตัวปิดบัตรคอร์ส ⇒ บล็อก (ยอดคืนเงินออกไปแล้ว · บัตรเปิดกลับไม่ได้)</summary>
    public static Decision OnVoidCreditNote(string? cancelledPassNumber, string creditNoteNumber)
        => cancelledPassNumber == null
            ? Decision.Nothing
            : new(DocAction.Block, $"ใบลดหนี้ {creditNoteNumber} เป็นใบคืนเงินที่ปิดบัตรคอร์ส {cancelledPassNumber} แล้ว — ยกเลิกไม่ได้ "
                + "(ถ้าออกผิด ให้ออกใบขายคอร์สใหม่ให้ลูกค้า)");

    /// <summary>เหตุที่เปิดบัตรจากเอกสารไม่ได้ · null = เปิดได้ · <paramref name="journalUnearnedCredit"/> = Σ(Cr − Dr) ของ JE ที่มีผลของใบนี้บนผังรายได้รับล่วงหน้า (บาท)</summary>
    public static string? ActivateProblem(string documentNumber, decimal journalUnearnedCredit, string? chainPassNumber)
    {
        if (chainPassNumber != null)
            return $"เอกสาร {documentNumber} อยู่ในสายเอกสารเดียวกับบัตร {chainPassNumber} ที่เปิดไปแล้ว — ห้ามเปิดซ้ำจากเงินก้อนเดียวกัน";
        if (journalUnearnedCredit <= 0m)
            return $"รายการบัญชีของเอกสาร {documentNumber} ไม่ได้ลงเครดิตผังค่าบริการรับล่วงหน้า (เช่นใบเสร็จที่ตัดลูกหนี้ หรือใบที่ลงรายได้ไปแล้ว) — "
                + "เปิดบัตรจากใบนี้ไม่ได้ (จะรับรู้รายได้ซ้ำ) · เปิดจากใบที่ตั้งค่าบริการรับล่วงหน้าจริง";
        return null;
    }
}
