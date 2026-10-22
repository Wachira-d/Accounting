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
/// <item>ใบลดหนี้อ้างใบขาย: ยอดสุทธิต้อง<b>เท่ากับ</b>ส่วนที่ยังไม่รับรู้ (คืนเงินทั้งหมดที่เหลือ) + ทุกบรรทัดลงผังรายได้รับล่วงหน้าของบัตร + ไม่มีการจองค้าง ⇒
///   ปิดบัตร · อื่น ๆ ⇒ บล็อกพร้อมยอดที่ออกได้</item>
/// <item>ยกเลิกใบลดหนี้ที่ปิดบัตรไปแล้ว ⇒ บล็อก (บัตรเปิดกลับไม่ได้)</item>
/// <item>เปิดบัตรจากเอกสาร: ยอดสัญญา = เครดิตสุทธิของ JE ที่ลงผังรายได้รับล่วงหน้า (บาท) · ≤ 0 ⇒ ไม่เปิด · สายเอกสารเดียวกันมีบัตรแล้ว ⇒ ไม่เปิด</item>
/// </list></para>
/// </summary>
public static class CreditPassDocumentRule
{
    public const string RuleCode = "TFRS-NPAE-6-CREDIT-DOC";

    public sealed record PassFacts(string PassNumber, CreditPassStatus Status, decimal CreditsUsed, decimal CreditsReserved,
        decimal ContractAmount, decimal RecognizedAmount, decimal RefundedAmount)
    {
        public decimal Deferred => Math.Max(0m, ContractAmount - RecognizedAmount - RefundedAmount);
        public bool Untouched => CreditsUsed <= 0m && CreditsReserved <= 0m && RecognizedAmount <= 0m;
    }

    public enum DocAction { None = 0, CancelPass = 1, Block = 2 }

    public sealed record Decision(DocAction Action, string? Message)
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
        if (Math.Abs(creditNoteNet - pass.Deferred) > 0.01m)
            return new(DocAction.Block, head + $"ยอดสุทธิก่อน VAT ต้องเท่ากับส่วนที่ยังไม่ใช้ {pass.Deferred:N2} บาท (ใบนี้ {creditNoteNet:N2}) — "
                + "ระบบคืนเงินได้เฉพาะทั้งส่วนที่เหลือแล้วปิดบัตร");
        return new(DocAction.CancelPass, null);
    }

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
