namespace Accounting.Helpers;

/// <summary>ตัวเลขของป้าย "50 ทวิ ต้องตรวจ" เมื่อใบลดหนี้ซื้อมาก่อนออก 50 ทวิ (ดู <see cref="WhtCreditNoteRemainder"/>)</summary>
/// <param name="OriginalTax">ภาษีหัก ณ ที่จ่ายของใบต้นทาง (<c>Document.WithholdingTaxAmount</c>) — "ใบเดิม Y"</param>
/// <param name="CreditNoteTax">ภาษีหักที่ใบลดหนี้ <b>ทุกใบที่มีผล</b> ของใบต้นทางลดไป (รวมใบที่กำลังอนุมัติ) — "ใบลดหนี้ Z"</param>
/// <param name="CreditNoteCount">จำนวนใบลดหนี้ที่รวมอยู่ใน Z</param>
/// <param name="Remaining">ภาษีหักที่เหลือหลังใบลดหนี้ = max(0, Y − Z) — "X"</param>
/// <param name="Excess">ใบลดหนี้ลดเกินภาษีหักของใบเดิม (Z − Y เมื่อ &gt; 0 · ปกติ 0) — ต้องตรวจใบลดหนี้ ไม่ใช่ตัวเลขที่ใช้ได้</param>
/// <param name="AlreadyCertified">ภาษีบน 50 ทวิ ที่ยังมีผลของใบต้นทาง (งวดที่จ่ายแล้วในเกณฑ์เงินสด · 0 = ยังไม่มีใบ)</param>
/// <param name="LeftToWithhold">ภาษีหักที่เหลือให้หักตอนจ่ายงวดที่เหลือ = max(0, X − ที่ออกใบแล้ว)</param>
/// <param name="OverCertified">50 ทวิ ที่ออกแล้วเกินยอดที่เหลือหลังใบลดหนี้ (ต้องยกเลิก/ออกใหม่ใบของงวดที่จ่ายแล้ว · ปกติ 0)</param>
public sealed record WhtCreditNoteRemainderFigures(
    decimal OriginalTax, decimal CreditNoteTax, int CreditNoteCount, decimal Remaining, decimal Excess,
    decimal AlreadyCertified, decimal LeftToWithhold, decimal OverCertified);

/// <summary>
/// **ใบลดหนี้ซื้อมาก่อน 50 ทวิ (เกณฑ์เงินสด) — ตัวเลขที่ผู้ใช้ต้องกรอกตอนบันทึกจ่าย** (บอร์ดตัดสิน 2026-10-13 · ทีม B · A3) ·
/// ผู้เรียก: <c>WithholdingTaxCertService.AdjustCertsForCreditNoteAsync</c> สองกิ่ง — ใบต้นทางยังไม่มี 50 ทวิ · ใบต้นทางยังจ่ายไม่ครบ
/// <para>═══ ที่มา ═══ ป้ายเดิมบอกแค่ "ตอนออกใบให้ลดยอดตามใบลดหนี้ (ระบบยังไม่หักให้เอง)" — ผู้ใช้ต้องไปคิดเองว่าภาษีหักที่ถูกคือเท่าไร ·
/// ทีม A (2a · 2026-10-13): หน้าบันทึกจ่ายเติมยอดนี้ให้แล้ว (<see cref="WhtPaymentSuggestion"/>) ⇒ ท้ายป้ายเปลี่ยนจาก "ระบบยังไม่หักให้เอง" เป็น "ระบบเติมยอดแนะนำให้แล้ว แก้ได้"
/// ทั้งที่ระบบมีตัวเลขครบ (ใบเดิม · ใบลดหนี้ทุกใบ · 50 ทวิ ที่ออกแล้ว) ⇒ ป้ายต้องบอกยอดตรง ๆ: "ภาษีหักที่เหลือหลังใบลดหนี้ = X (ใบเดิม Y − ใบลดหนี้ Z)"</para>
/// <para>═══ กติกา ═══ Z รวมใบลดหนี้ทุกใบที่มีผลของใบต้นทาง (ใบที่สองต้องไม่บอกยอดที่ลืมใบแรก) · ลดเกินใบเดิม ⇒ X = 0 พร้อมบอกยอดเกิน
/// (ไม่ใช่ตัวเลขติดลบที่ไปกรอกในช่องภาษีหักไม่ได้) · ปัด 2 ตำแหน่ง AwayFromZero · ภาษีที่ใบลดหนี้ ≤ 0 ไม่นับ</para>
/// </summary>
public static class WhtCreditNoteRemainder
{
    private static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>คิดตัวเลข — <paramref name="creditNoteTaxes"/> = ภาษีหักของใบลดหนี้ทุกใบที่มีผล (รวมใบที่กำลังอนุมัติ)</summary>
    public static WhtCreditNoteRemainderFigures Compute(decimal originalTax, IReadOnlyCollection<decimal> creditNoteTaxes, decimal alreadyCertified = 0m)
    {
        var counted = creditNoteTaxes.Where(t => t > 0m).ToList();
        var cut = R(counted.Sum());
        var y = R(originalTax);
        var raw = y - cut;
        var remaining = raw > 0m ? raw : 0m;
        var excess = raw < 0m ? -raw : 0m;
        var certified = alreadyCertified > 0m ? R(alreadyCertified) : 0m;
        var left = remaining - certified;
        return new WhtCreditNoteRemainderFigures(y, cut, counted.Count, remaining, excess, certified,
            left > 0m ? left : 0m, left < 0m ? -left : 0m);
    }

    /// <summary>"ภาษีหักที่เหลือหลังใบลดหนี้ = X (ใบเดิม Y − ใบลดหนี้ Z)" — ข้อความตัวเลขตัวเดียวของทั้งสองกิ่ง ·
    /// ทีม A (2a): ยอดแนะนำตอนบันทึกจ่าย (<see cref="WhtPaymentSuggestion"/>) ใช้ถ้อยคำเดียวกันนี้ (ห้ามเขียนสำเนา)</summary>
    public static string FiguresText(WhtCreditNoteRemainderFigures f)
    {
        var cn = f.CreditNoteCount > 1 ? $"ใบลดหนี้รวม {f.CreditNoteCount} ใบ {f.CreditNoteTax:N2}" : $"ใบลดหนี้ {f.CreditNoteTax:N2}";
        var excess = f.Excess > 0m ? $" — ใบลดหนี้ลดเกินภาษีหักของใบเดิม {f.Excess:N2} ตรวจยอดใบลดหนี้" : "";
        return $"ภาษีหักที่เหลือหลังใบลดหนี้ = {f.Remaining:N2} (ใบเดิม {f.OriginalTax:N2} − {cn}{excess})";
    }

    /// <summary>กิ่ง "ใบต้นทางยังไม่มี 50 ทวิ" — ใบที่จะออกตอนจ่ายคิดจากช่องภาษีหักที่บันทึกจ่าย ⇒ บอกยอดที่ต้องแก้</summary>
    public static string BeforeAnyCertReason(string prefix, WhtCreditNoteRemainderFigures f)
        => $"{prefix} แต่ใบต้นทางยังไม่มี 50 ทวิ — {FiguresText(f)} — ตอนบันทึกจ่ายให้แก้ช่องภาษีหักเป็น {f.Remaining:N2} (ระบบเติมยอดแนะนำให้แล้ว แก้ได้)";

    /// <summary>กิ่ง "ใบต้นทางยังจ่ายไม่ครบ" — มี 50 ทวิ ของงวดที่จ่ายแล้ว ⇒ บอกยอดที่เหลือให้หักตอนจ่ายงวดที่เหลือ + ยอดที่ออกใบเกิน (ถ้ามี)</summary>
    public static string PartlyPaidReason(string prefix, WhtCreditNoteRemainderFigures f)
    {
        var text = $"{prefix} แต่ใบต้นทางยังจ่ายไม่ครบ — {FiguresText(f)} · 50 ทวิ ของงวดที่จ่ายแล้ว {f.AlreadyCertified:N2} "
            + $"⇒ ตอนบันทึกจ่ายงวดที่เหลือให้แก้ช่องภาษีหักรวมเป็น {f.LeftToWithhold:N2} (ระบบเติมยอดแนะนำให้แล้ว แก้ได้)";
        if (f.OverCertified > 0m)
            text += $" · 50 ทวิ ที่ออกแล้วเกินยอดที่เหลืออยู่ {f.OverCertified:N2} — ยกเลิกแล้วออกใบของงวดที่จ่ายแล้วใหม่ (เดือนที่ยื่นแล้วต้องยื่นแบบเพิ่มเติม)";
        return text;
    }

    /// <summary>ข้อความสั้นท้ายหมายเหตุ <c>[WHT-CN]</c> บนใบลดหนี้ (คำตอบการอนุมัติ)</summary>
    public static string Notice(string sourceNumber, WhtCreditNoteRemainderFigures f)
        => $"{sourceNumber}: ติดป้าย \"50 ทวิ ต้องตรวจ\" — {FiguresText(f)}";
}
