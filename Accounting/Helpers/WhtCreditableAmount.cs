using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>
/// รอบ 252 (WP-09 · AUTO_SEND_WHT_DESIGN §7.1 "ผลต่อผู้อ่านเดิม") — <b>ยอดเครดิตภาษีถูกหักที่ใช้ได้ของแถวทะเบียนหนึ่งแถว</b> — ตัวคำนวณเดียว
/// ของทุกผู้อ่าน (ภ.ง.ด.50 <c>TaxService</c> · เครื่องคำนวณหน้าบริหารการเงิน · <c>WhtCreditService.CreditableAmountAsync/SummaryAsync</c> ·
/// รายงาน "50 ทวิ ที่ยังขาด") — ห้าม <c>Sum(w =&gt; w.WhtAmount)</c> บนแถวตรงในไฟล์ภาษี (สองสูตร = ตัวเลขสองชุด)
///
/// <para>กติกา (WHT_CREDIT_PLAN L1 — เครดิตได้เฉพาะยอดที่มีหนังสือรับรองจริง):</para>
/// <list type="bullet">
/// <item>Received / Claimed ⇒ เต็มแถว (แถวรุ่นก่อนรอบนี้ไม่มี <c>ReceivedAmount</c> — สถานะคือหลักฐานว่ามีใบครบ)</item>
/// <item>Pending ⇒ เฉพาะส่วนที่ได้รับใบแล้ว (<c>ReceivedAmount</c> = Σ allocation ที่ยืนยัน) ไม่เกินยอดที่ถูกหัก — ผ่อน 3 งวดได้ใบงวดแรก ⇒ เครดิตใบเดียว</item>
/// <item>Expired (ตัดสูญ) ⇒ 0</item>
/// </list>
/// <para>ติดลบ/เกินไม่ได้ (clamp) · ปัด 2 ตำแหน่ง AwayFromZero</para>
/// </summary>
public static class WhtCreditableAmount
{
    public static decimal Of(WhtCreditStatus status, decimal whtAmount, decimal receivedAmount)
    {
        var wht = Math.Max(0m, whtAmount);
        var v = status switch
        {
            WhtCreditStatus.Received or WhtCreditStatus.Claimed => wht,
            WhtCreditStatus.Pending => Math.Min(wht, Math.Max(0m, receivedAmount)),
            _ => 0m,
        };
        return Math.Round(v, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>ยอดที่ยัง "ขาดใบ" ของแถว = ถูกหัก − ได้รับใบแล้ว (เฉพาะ Pending · สถานะอื่น = 0)</summary>
    public static decimal Missing(WhtCreditStatus status, decimal whtAmount, decimal receivedAmount)
        => status == WhtCreditStatus.Pending
            ? Math.Round(Math.Max(0m, whtAmount) - Of(status, whtAmount, receivedAmount), 2, MidpointRounding.AwayFromZero)
            : 0m;
}
