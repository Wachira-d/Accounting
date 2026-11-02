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
    public static Plan Decide(decimal refundAmount, bool originalFound, bool originalVoided, decimal originalTotal,
        decimal alreadyCredited, bool originalHasVat)
    {
        if (refundAmount <= 0m) return new Plan(false, 0m, 0m, "ยอดคืนต้องมากกว่า 0");
        if (!originalFound)
            return new Plan(false, 0m, 0m, "ไม่พบใบกำกับภาษี/ใบเสร็จของรายการนี้ในบริษัทผู้ให้บริการ — ออกใบเสร็จเดิมก่อน (ปุ่ม \"ออกใบเสร็จอีกครั้ง\") แล้วออกใบลดหนี้ที่หน้าเอกสาร");
        if (originalVoided)
            return new Plan(false, 0m, 0m, "ใบเดิมถูกยกเลิกแล้ว — ออกใบลดหนี้อ้างใบที่ยกเลิกไม่ได้ (§86/10) · ตรวจรายการบัญชีคืนเงินด้วยมือ");
        if (alreadyCredited + refundAmount > originalTotal + Tolerance)
            return new Plan(false, 0m, 0m,
                $"ยอดใบลดหนี้รวม {alreadyCredited + refundAmount:N2} จะเกินยอดใบเดิม {originalTotal:N2} (§86/10) — ตรวจใบลดหนี้ที่ออกไปแล้วที่หน้าเอกสาร");
        var (b, v) = PlatformMoneyInAccount.SplitReceived(refundAmount, originalHasVat);
        return new Plan(true, b, v, null);
    }

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
