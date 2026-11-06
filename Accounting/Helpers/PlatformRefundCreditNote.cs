namespace Accounting.Helpers;

/// <summary>
/// รอบ 228 (PLATFORM_OMISE_AUDIT §5.5 · ป.รัษฎากร §86/10) — <b>ใบลดหนี้อัตโนมัติเมื่อแอดมินแพลตฟอร์มคืนเงินค่าบริการ</b> (ข้อยกเว้นของนโยบาย "ไม่คืนเงิน"
/// คำตัดสินข้อ 178 — เก็บซ้อน/ผิดพลาด · SystemAdmin เท่านั้น) · pure · ตัวเดียวของยอด/ด่าน/เลขอ้างอิง
/// <para><b>ลำดับบัญชีใน tenant ผู้ให้บริการ:</b> ตอนคืนเงิน (ในธุรกรรมคืนเงิน) Dr ลูกหนี้การค้า / Cr บัญชีพักผู้ให้บริการรับชำระ (เส้นเดียวกับของร้าน) →
/// หลัง commit ออกใบลดหนี้อ้างใบกำกับ/ใบเสร็จเดิม (Dr รายได้ + ภาษีขาย / Cr ลูกหนี้) ⇒ ลูกหนี้ล้างเป็นศูนย์ · ภาษีขายลดในเดือนที่ออกใบลดหนี้</para>
/// <para>เงินที่คืน = ยอดรวม VAT เสมอ (เงินที่รับถือว่ารวม VAT — ตัวแยกเดียว <see cref="PlatformMoneyInAccount.SplitReceived"/> เหมือนใบเสร็จต้นทาง)</para>
/// </summary>
public static class PlatformRefundCreditNote
{
    public const string RuleCode = "RD-86/10-PLATFORM-REFUND";
    public const string LegalReference = "ป.รัษฎากร มาตรา 86/10";
    private const decimal Tolerance = 0.005m;

    public sealed record Plan(bool Ok, decimal BaseAmount, decimal VatAmount, string? Problem);

    /// <summary>ข้อความประวัติคืนเงิน (ฝ่ายค้าน B1): ค่าบริการใต้บริษัทผู้ให้บริการ — ไม่ลงใบสำคัญคืนเงิน · ใบลดหนี้คือรายการบัญชีเดียว</summary>
    public const string BookedByCreditNoteNote =
        "ค่าบริการ NextAcc — ลงบัญชีด้วยใบลดหนี้อัตโนมัติ (Dr รายได้ + ภาษีขาย / Cr บัญชีพักผู้ให้บริการรับชำระ) ไม่ลงใบสำคัญคืนเงินซ้ำ";

    /// <summary>เลขกันซ้ำของใบลดหนี้ = เลขคืนเงินของผู้ให้บริการ + ยอดคืนสะสมหลังครั้งนี้ (เพิ่มขึ้นทุกครั้ง ⇒ ไม่ซ้ำต่อการคืนเงินหนึ่งครั้ง แม้เลขภายในของการตรวจผลซ้ำได้)</summary>
    public static string DedupeKey(string refundRef, decimal refundedTotalAfter)
        => $"{(refundRef ?? "").Trim()}/{refundedTotalAfter.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>ยอดของใบลดหนี้ — ไม่ผ่าน (ยอด ≤ 0 · ไม่มีใบเดิม · ใบเดิมถูกยกเลิก · รวมใบลดหนี้เกินใบเดิม §86/10) ⇒ <c>Problem</c> (ข้อความไทยบอกทางไปต่อ)</summary>
    /// <remarks>รอบ 235 (ฝ่ายค้าน S-4): <paramref name="withholdingShare"/> = ภาษีหัก ณ ที่จ่ายส่วนที่เกี่ยวกับเงินที่คืน (<see cref="WithholdingShare"/>) ⇒
    /// ใบลดหนี้ลด<b>เต็มยอด</b> (ฐาน+VAT = เงินที่คืน + ภาษีหักส่วนนั้น) · ยอดสุทธิของใบลดหนี้ (TotalAmount) = เงินที่คืน ⇒ ด่าน Σ ≤ ใบเดิม (ยอดสุทธิ) เดิม</remarks>
    public static Plan Decide(decimal refundAmount, bool originalFound, bool originalVoided, decimal originalTotal,
        decimal alreadyCredited, bool originalHasVat, decimal withholdingShare = 0m)
    {
        if (refundAmount <= 0m) return new Plan(false, 0m, 0m, "ยอดคืนต้องมากกว่า 0");
        if (!originalFound)
            return new Plan(false, 0m, 0m, "ไม่พบใบกำกับภาษี/ใบเสร็จของรายการนี้ในบริษัทผู้ให้บริการ — ออกใบเสร็จเดิมก่อน (ปุ่ม \"ออกใบเสร็จอีกครั้ง\") แล้วออกใบลดหนี้ที่หน้าเอกสาร");
        if (originalVoided)
            return new Plan(false, 0m, 0m, "ใบเดิมถูกยกเลิกแล้ว — ออกใบลดหนี้อ้างใบที่ยกเลิกไม่ได้ (§86/10) · ตรวจรายการบัญชีคืนเงินด้วยมือ");
        if (alreadyCredited + refundAmount > originalTotal + Tolerance)
            return new Plan(false, 0m, 0m,
                $"ยอดใบลดหนี้รวม {alreadyCredited + refundAmount:N2} จะเกินยอดใบเดิม {originalTotal:N2} (§86/10) — ตรวจใบลดหนี้ที่ออกไปแล้วที่หน้าเอกสาร");
        var (b, v) = PlatformMoneyInAccount.SplitReceived(refundAmount + Math.Max(0m, withholdingShare), originalHasVat);
        return new Plan(true, b, v, null);
    }

    /// <summary>รอบ 235 (ฝ่ายค้าน S-4): ภาษีหัก ณ ที่จ่ายส่วนที่ต้องกลับรายการกับเงินที่คืน — ตามสัดส่วน (เงินที่คืน ÷ ยอดสุทธิของใบเดิม) ·
    /// การคืนครั้งสุดท้าย (คืนครบยอดสุทธิ) = ภาษีหักที่เหลือทั้งหมด (ไม่ทิ้งเศษสตางค์ค้าง 11910) · ไม่เกินส่วนที่ยังไม่ถูกกลับ · ปัด AwayFromZero</summary>
    public static decimal WithholdingShare(decimal refundAmount, decimal originalNet, decimal originalWht, decimal alreadyCreditedNet, decimal alreadyCreditedWht)
    {
        if (originalWht <= 0m || refundAmount <= 0m || originalNet <= 0m) return 0m;
        var remaining = Math.Max(0m, R2(originalWht - Math.Max(0m, alreadyCreditedWht)));
        if (alreadyCreditedNet + refundAmount >= originalNet - Tolerance) return remaining;
        return Math.Min(remaining, R2(originalWht * refundAmount / originalNet));
    }

    /// <summary>อัตราภาษีหักบนบรรทัดใบลดหนี้ (4 ตำแหน่ง · คำนวณกลับจากยอดจริง — แบบเดียวกับใบเสร็จ) — ฐาน 0 = 0</summary>
    public static decimal WithholdingRate(decimal withholdingShare, decimal baseAmount)
        => withholdingShare > 0m && baseAmount > 0m ? decimal.Round(withholdingShare / baseAmount * 100m, 4, MidpointRounding.AwayFromZero) : 0m;

    /// <summary>เรื่องค้าง: คืนเงินรายการที่หักภาษีแต่แถวทะเบียน 50 ทวิ ยืนยัน/ใช้เครดิตแล้ว</summary>
    public const string WhtRefundRuleCode = "PLATFORM-WHT-REFUND";

    /// <summary>รอบ 243a (ฝ่ายค้าน S4): <c>UndoWriteOff</c> = แถวตัดสูญพร้อม JE ⇒ กลับ JE ตัดสูญผ่าน <c>WhtCreditService.UndoWriteOffAsync</c> (ใบลดหนี้ Cr 11910 แล้ว · ไม่กลับ = 11910 ติดลบ)</summary>
    public enum RegistryAction { None, Reduce, Expire, AttentionConfirmed, UndoWriteOff }

    /// <summary>ทะเบียนภาษีถูกหักของใบเดิมหลังคืนเงิน — ยัง Pending: เหลือ 0 ⇒ ตัดสิทธิ์ (หมดสิทธิ์เครดิต + เหตุผล) · เหลือ &gt; 0 ⇒ ลดยอดตามสัดส่วน ·
    /// Received/Claimed ⇒ <b>ไม่แตะเอง</b> (ใบจริงของลูกค้าอ้างยอดเดิม) ขึ้นเรื่องค้างให้แอดมินขอ 50 ทวิ ใหม่/ยกเลิก · ไม่มีแถว/หมดอายุแล้ว ⇒ ไม่ทำ ·
    /// ตัดสูญพร้อม JE ที่ยังมีผล (<paramref name="writtenOffWithJournal"/> = <c>WhtCreditWriteOff.NeedsReversal</c>) ⇒ <see cref="RegistryAction.UndoWriteOff"/></summary>
    public static RegistryAction RegistryActionFor(global::Accounting.Models.Enums.WhtCreditStatus? status, decimal share, decimal remainingAfter,
        bool writtenOffWithJournal = false)
    {
        if (share <= 0m || status is null) return RegistryAction.None;
        if (writtenOffWithJournal) return RegistryAction.UndoWriteOff;
        return status switch
        {
            global::Accounting.Models.Enums.WhtCreditStatus.Pending => remainingAfter <= Tolerance ? RegistryAction.Expire : RegistryAction.Reduce,
            global::Accounting.Models.Enums.WhtCreditStatus.Received or global::Accounting.Models.Enums.WhtCreditStatus.Claimed => RegistryAction.AttentionConfirmed,
            _ => RegistryAction.None,
        };
    }

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>เลขอ้างอิงบนใบลดหนี้ = ตัวกันออกซ้ำ (หนึ่งการคืนเงิน = หนึ่งใบ) · เลขคืนเงินของผู้ให้บริการ</summary>
    public static string Reference(string refundRef)
    {
        var r = (refundRef ?? "").Trim();
        var key = "REFUND-" + (r.Length == 0 ? "-" : r);
        return key.Length > 100 ? key[..100] : key;
    }

    /// <summary>คำอธิบายบรรทัดใบลดหนี้ — อ้างใบเดิม + เหตุผลของแอดมิน (ฝ่ายค้าน N4 · ≤ 300 ตัว)</summary>
    public static string LineDescription(string originalNumber, string? reason)
    {
        var r = (reason ?? "").Trim();
        var text = $"คืนเงินค่าบริการ อ้างใบ {originalNumber}" + (r.Length == 0 ? "" : $" — {r}");
        return text.Length > 300 ? text[..300] : text;
    }

    /// <summary>หมายเหตุบนใบลดหนี้ — อ้างเลขที่+วันที่ใบเดิม + ผลต่าง (§86/10 บังคับ)</summary>
    public static string Notes(string originalNumber, DateTime originalDate, decimal originalTotal, decimal refundAmount, string reason)
        => $"คืนเงินค่าบริการ NextAcc (ข้อยกเว้นนโยบายไม่คืนเงิน) · อ้างใบ {originalNumber} ลงวันที่ {ThaiDate.ToThaiDisplayString(originalDate)} "
         + $"ยอดเดิม {originalTotal:N2} · ลดลง {refundAmount:N2} · เหตุผล: {(string.IsNullOrWhiteSpace(reason) ? "-" : reason.Trim())}";
}
