using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §7.1 "ผลต่อผู้อ่านเดิม") — <b>ยอดเครดิตภาษีถูกหักที่ใช้ได้ของแถวทะเบียนหนึ่งแถว</b> — ตัวคำนวณเดียว
/// ของทุกผู้อ่าน (ภ.ง.ด.50 <c>TaxService</c> · เครื่องคำนวณหน้าบริหารการเงิน · <c>WhtCreditService.CreditableAmountAsync/SummaryAsync</c> ·
/// รายงาน "50 ทวิ ที่ยังขาด") — ห้าม <c>Sum(w =&gt; w.WhtAmount)</c> บนแถวตรงในไฟล์ภาษี (สองสูตร = ตัวเลขสองชุด)
///
/// <para>กติกา (WHT_CREDIT_PLAN L1 — เครดิตได้เฉพาะยอดที่มีหนังสือรับรองจริง · <b>ไม่เกินยอดบนใบ</b>):</para>
/// <list type="bullet">
/// <item>Received / Claimed รุ่นก่อนรอบ 252 (<c>ReceivedAmount</c> = 0 — สถานะคือหลักฐานว่ามีใบครบ) ⇒ เต็มแถว</item>
/// <item>Received / Claimed ที่ได้ใบผ่านกล่องรับ (<c>ReceivedAmount</c> &gt; 0) ⇒ เท่ายอดบนใบ — ใบต่ำกว่ายอดถูกหักไม่เกิน ฿1 (ปัดเศษของผู้จ่าย) ปิดแถวเป็น Received แต่เครดิตไม่เกินใบ (ฝ่ายค้าน WP-09 L2)</item>
/// <item>Pending ⇒ เฉพาะส่วนที่ได้รับใบแล้ว (Σ allocation ที่ยืนยัน) — ผ่อน 3 งวดได้ใบงวดแรก ⇒ เครดิตใบเดียว</item>
/// <item>Expired (ตัดสูญ) ⇒ ส่วนที่ได้ใบแล้วยังเครดิตได้ (ตัดสูญเฉพาะส่วนที่ขาด — ฝ่ายค้าน WP-09 M1) · แถวรุ่นเก่า (ReceivedAmount 0) = 0</item>
/// </list>
/// <para>ติดลบ/เกินไม่ได้ (clamp) · ปัด 2 ตำแหน่ง AwayFromZero</para>
/// </summary>
public static class WhtCreditableAmount
{
    public static decimal Of(WhtCreditStatus status, decimal whtAmount, decimal receivedAmount)
    {
        var wht = Math.Max(0m, whtAmount);
        var got = Math.Min(wht, Math.Max(0m, receivedAmount));
        var v = status switch
        {
            WhtCreditStatus.Received or WhtCreditStatus.Claimed => receivedAmount > 0.005m ? got : wht,
            WhtCreditStatus.Pending or WhtCreditStatus.Expired => got,
            _ => 0m,
        };
        return Math.Round(v, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>สถานะของแถวที่ได้ใบผ่านกล่องรับ (ตัวเดียวของกล่องรับ + การกระทบ GL): ขาดไม่เกิน ฿1 (ปัดเศษของผู้จ่าย · ฝ่ายค้าน WP-09 L2) ⇒ Received ·
    /// ไม่งั้น Pending (รอใบส่วนที่เหลือ)</summary>
    public static WhtCreditStatus StatusForReceived(decimal whtAmount, decimal receivedAmount)
        => Math.Round(whtAmount - receivedAmount, 2, MidpointRounding.AwayFromZero) <= WhtCertMatchPolicy.Tolerance
            ? WhtCreditStatus.Received : WhtCreditStatus.Pending;

    public enum GlResyncKind { None, Grow, Shrink, OverGl }

    /// <summary>ฝ่ายค้าน WP-09 M3 — แถวที่ได้ใบผ่านกล่องรับแล้ว (ReceivedAmount &gt; 0) เมื่อยอด Dr 11910 ตาม GL เปลี่ยน:
    /// GL เพิ่ม (ผ่อนงวดถัดไป) ⇒ ขยาย · GL ลดแต่ไม่ต่ำกว่ายอดที่ได้ใบ (ยกเลิกงวดที่ยังไม่มีใบ) ⇒ ลด · GL ต่ำกว่ายอดที่ได้ใบ ⇒ คงยอดเดิม (ใบจริงชนะ) +
    /// ป้าย "ยอดใบเกินยอดที่ถูกหัก" · ไม่เคยแตะ ReceivedAmount · สถานะใหม่จาก <see cref="StatusForReceived"/></summary>
    public static (GlResyncKind Kind, decimal NewWhtAmount, WhtCreditStatus NewStatus) ResyncWithGl(
        WhtCreditStatus status, decimal whtAmount, decimal receivedAmount, decimal glAmount)
    {
        if (glAmount > whtAmount + 0.005m)
            return (GlResyncKind.Grow, glAmount, StatusForReceived(glAmount, receivedAmount));
        if (glAmount < whtAmount - 0.005m && glAmount >= receivedAmount - 0.005m)
            return (GlResyncKind.Shrink, glAmount, StatusForReceived(glAmount, receivedAmount));
        if (glAmount < receivedAmount - 0.005m)
            return (GlResyncKind.OverGl, whtAmount, status);
        return (GlResyncKind.None, whtAmount, status);
    }

    /// <summary>ยอดที่ยัง "ขาดใบ" ของแถว = ถูกหัก − ได้รับใบแล้ว (เฉพาะ Pending · สถานะอื่น = 0)</summary>
    public static decimal Missing(WhtCreditStatus status, decimal whtAmount, decimal receivedAmount)
        => status == WhtCreditStatus.Pending
            ? Math.Round(Math.Max(0m, whtAmount) - Of(status, whtAmount, receivedAmount), 2, MidpointRounding.AwayFromZero)
            : 0m;
}
