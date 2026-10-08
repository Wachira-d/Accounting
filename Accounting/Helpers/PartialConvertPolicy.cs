namespace Accounting.Helpers;

/// <summary>
/// ตัวตัดสินเดียวของ "แปลงเอกสารบางส่วน" (ใบเสนอราคา/ใบสั่งซื้อ → ใบแจ้งหนี้/ใบส่งของ ฯลฯ)
///
/// <para><b>คำตัดสินเจ้าของ 2026-10-05 (ข้อ 138):</b> <b>ห้ามล็อกจำนวน</b> — จำนวนที่เกิน "คงเหลือ"
/// เป็นแค่คำเตือน · สิ่งที่สำคัญที่สุดคือ <b>ยอดเงินรวม</b>: ยอดที่แปลงสะสม (ก่อนหน้า + ครั้งนี้) เกินยอดใบต้นทาง
/// ⇒ หยุดถามผู้ใช้หนึ่งครั้ง (422 + ยืนยันด้วย <c>ConfirmOverSourceAmount</c>) ไม่ใช่กำแพง</para>
///
/// <para>ที่มา: ใบเสนอราคา 1 รายการ × 1 ชิ้น 535 บาท — ผู้ใช้ออกใบแจ้งหนี้งวดแรกไปแล้ว อยากออกงวดสองอีกใบ
/// แต่ช่อง "แปลงครั้งนี้" ถูกล็อกที่ 0 เพราะ "คงเหลือ 0 ชิ้น" ⇒ แบ่งงวดเก็บเงินไม่ได้เลย ทั้งที่ยอดเงินยังไม่ครบ</para>
///
/// <para>ยอดเงินของแต่ละบรรทัดคิดแบบ <b>สัดส่วนจาก Amount ของบรรทัดต้นทาง</b> (รองรับทั้งส่วนลด % และส่วนลดบาท
/// โดยไม่ต้องประกอบสูตรราคาซ้ำ) — pure ไม่แตะ DB เพื่อให้เทสต์ล็อกทั้งสองทิศได้</para>
/// </summary>
public static class PartialConvertPolicy
{
    public const string OverAmountRule = "CONVERT-OVER-AMOUNT";
    public const decimal AmountEpsilon = 0.005m;
    public const decimal QtyEpsilon = 0.0001m;

    /// <summary>คำขอแปลงหนึ่งบรรทัด เทียบกับจำนวนที่ยังไม่ถูกแปลงบนแกนนั้น</summary>
    public readonly record struct LineAsk(string Description, string Unit, decimal Requested, decimal Remaining);

    /// <summary>ยอดเงิน (ฐานก่อน VAT) ของ <paramref name="qty"/> ชิ้น คิดเป็นสัดส่วนจากบรรทัดต้นทาง —
    /// บรรทัดที่จำนวน 0 (บรรทัดข้อความ/ค่าบริการเหมา) ให้ยอดเต็มเมื่อขอ ≥ 1 มิฉะนั้น 0</summary>
    public static decimal ProRataBase(decimal lineAmount, decimal lineQty, decimal qty)
    {
        if (qty <= 0m) return 0m;
        if (lineQty <= QtyEpsilon) return lineAmount;
        return Math.Round(lineAmount * qty / lineQty, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>คำเตือน (ไม่กัน) ต่อบรรทัดที่ขอเกินคงเหลือ — ว่าง = ทุกบรรทัดอยู่ในคงเหลือ</summary>
    public static IReadOnlyList<string> QuantityWarnings(IEnumerable<LineAsk> lines, string axisLabel)
    {
        var list = new List<string>();
        foreach (var l in lines)
        {
            if (l.Requested <= l.Remaining + QtyEpsilon) continue;
            list.Add(l.Remaining <= QtyEpsilon
                ? $"รายการ '{l.Description}' ถูก{axisLabel}ครบจำนวนแล้ว — ครั้งนี้ขอเพิ่มอีก {l.Requested:0.##} {l.Unit}".TrimEnd()
                : $"รายการ '{l.Description}' ขอ{axisLabel} {l.Requested:0.##} แต่คงเหลือ {l.Remaining:0.##} {l.Unit}".TrimEnd());
        }
        return list;
    }

    /// <summary>ยอดสะสม (ก่อนหน้า + ครั้งนี้) เกินยอดใบต้นทางหรือไม่ — เท่ากันพอดี = ไม่เกิน ·
    /// <paramref name="roundingLines"/> = จำนวนบรรทัดที่ถูกปัดเศษแยกกัน (บรรทัดลูกเดิม + บรรทัดครั้งนี้): ใบลูกคิดส่วนลด/ยอดใหม่ต่อบรรทัด
    /// แล้วปัดทีละบรรทัด ⇒ ผลรวมคลาดจากยอดต้นทางได้ ≤ 1 สตางค์ต่อบรรทัด (เช่น 3 × 10.03 ลด 15% = 25.58 แต่ 3 ใบ × 8.53 = 25.59)
    /// — ด่านนี้เป็นแค่ "ถามยืนยัน" จึงยอมเศษปัดตามจำนวนบรรทัด ไม่ถามผู้ใช้เพราะเศษสตางค์ที่ระบบปัดเอง (ฝ่ายค้านรอบ 138 P2)</summary>
    public static bool IsOverAmount(decimal convertedBefore, decimal convertingNow, decimal sourceBase, int roundingLines = 0)
        => convertedBefore + convertingNow > sourceBase + Math.Max(AmountEpsilon, 0.01m * roundingLines);

    public static string OverAmountMessage(string documentNumber, string axisLabel,
        decimal convertedBefore, decimal convertingNow, decimal sourceBase)
        => $"ยอด{axisLabel}สะสมของ {documentNumber} (ก่อน VAT) จะเป็น {convertedBefore + convertingNow:N2} "
         + $"(ก่อนหน้า {convertedBefore:N2} + ครั้งนี้ {convertingNow:N2}) เกินยอดใบต้นทาง {sourceBase:N2} "
         + $"อยู่ {convertedBefore + convertingNow - sourceBase:N2} — ตรวจยอดอีกครั้งก่อนยืนยัน";
}
