using System.Globalization;

namespace Accounting.Helpers;

/// <summary>ผลตัดสิน "ควรทวง/เตือนลูกค้าเรื่องใบนี้ไหม" เมื่อดูเงินรอจัดสรรของลูกค้า</summary>
/// <param name="SuppressCustomerReminder">true = ห้ามส่งทวง/เตือนถึงลูกค้า (เงินที่รับแล้วพอปิดใบ)</param>
/// <param name="Reason">ข้อความถึงทีมงาน (เหตุที่ไม่ส่ง หรือข้อสังเกตว่ามีเงินรอจัดสรรบางส่วน) — null = ไม่มีเงินรอจัดสรร</param>
public sealed record DunningSuppressionDecision(bool SuppressCustomerReminder, string? Reason);

/// <summary>
/// **ระงับการทวงหนี้เมื่อ "เงินรอจัดสรร" ของลูกค้าพอปิดใบ — ตัวตัดสินเดียว** (รอบ 258 · DECISIONS 258-1-9)
/// <para>ที่มา: ลูกค้าโอนก้อนเดียวปิด 2 ยอด แต่ออกใบไว้ใบเดียว ⇒ เงินอีกส่วนค้างเป็นเงินรอจัดสรร · ใบที่ออกทีหลังยังขึ้น "ค้างชำระ" จนกว่าจะกดจัดสรร
/// ⇒ ลูกค้าที่จ่ายครบแล้วถูกทวง · กติกา: เงินรอจัดสรรของลูกค้า ≥ ยอดค้างของใบ ⇒ <b>ไม่ทวงลูกค้า</b> แจ้งทีมงานให้จัดสรรแทน ·
/// น้อยกว่า ⇒ ทวงตามเดิม (ข้อความภายในบอกยอดเงินรอจัดสรร) · ไม่มีเงินรอจัดสรร ⇒ พฤติกรรมเดิมทุกประการ</para>
/// <para>ผู้เรียก (ล็อกใน <c>tools/required_call_site_check.py</c>): <c>EmailScheduleService.ScanDueSoonAndOverdueAsync</c> (ลูปสแกนรายวันของเตือนครบกำหนด/เกินกำหนด — อีเมล/LINE ถึงลูกค้า · รวมวันที่ย้อนเก็บ) ·
/// <c>OverdueDunningJob</c> (หนังสือทวงตามอายุหนี้)</para>
/// </summary>
public static class DunningSuppression
{
    /// <summary>ใบค้าง 1 ใบของลูกค้าสำหรับ <see cref="Plan"/> — <paramref name="Allocatable"/> = การจัดสรรเงินรอจัดสรรให้ใบนี้ผ่านตัวตัดสิน
    /// (<see cref="CustomerCreditAllocation.Check"/> + มีสมุดรายวันตั้งลูกหนี้) — ใบที่จัดสรรไม่ได้ (สกุลต่างประเทศ · ไม่มี JE) ห้ามถูกระงับ</summary>
    public sealed record OpenDocument(Guid DocumentId, decimal BalanceDue, bool Allocatable);

    /// <summary>
    /// ฝ่ายค้านรอบ 258 M1 — ใบไหนของลูกค้ารายนี้ที่ "เงินรอจัดสรรพอปิด": เดินใบ<b>เก่าสุดก่อน</b> แล้ว<b>หักเงินสะสม</b> (เงินแต่ละบาทนับครั้งเดียว) ·
    /// ใบที่เงินคงเหลือไม่พอปิดเต็ม หรือจัดสรรไม่ได้ ⇒ ไม่ถูกระงับ (ทวงตามเดิม) และไม่กินเงิน
    /// (เดิมเทียบรายใบกับเงินรวม: เงิน 1,000 · ใบ 5 ใบ ใบละ 900 ⇒ ระงับทั้ง 5 ใบ)
    /// </summary>
    /// <param name="openOldestFirst">ใบค้างของลูกค้ารายเดียว เรียงเก่าสุดก่อน</param>
    /// <returns>ใบที่ถูกระงับ → ข้อความถึงทีมงาน</returns>
    public static IReadOnlyDictionary<Guid, string> Plan(decimal contactUnappliedCredit, IEnumerable<OpenDocument> openOldestFirst)
    {
        var held = new Dictionary<Guid, string>();
        var left = contactUnappliedCredit;
        foreach (var doc in openOldestFirst)
        {
            if (!doc.Allocatable) continue;
            var decision = Decide(doc.BalanceDue, left);
            if (!decision.SuppressCustomerReminder) continue;
            held[doc.DocumentId] = decision.Reason ?? "ลูกค้ามีเงินรอจัดสรรพอปิดใบนี้";
            left -= doc.BalanceDue;
        }
        return held;
    }

    public static DunningSuppressionDecision Decide(decimal documentBalanceDue, decimal contactUnappliedCredit)
    {
        if (!UnappliedCreditBalance.HasBalance(contactUnappliedCredit) || documentBalanceDue <= 0m)
            return new DunningSuppressionDecision(false, null);
        var credit = contactUnappliedCredit.ToString("#,##0.00", CultureInfo.InvariantCulture);
        var balance = documentBalanceDue.ToString("#,##0.00", CultureInfo.InvariantCulture);
        if (contactUnappliedCredit + UnappliedCreditBalance.Tolerance >= documentBalanceDue)
            return new DunningSuppressionDecision(true,
                $"ไม่ส่งทวงถึงลูกค้า — ลูกค้ารายนี้มีเงินรอจัดสรร {credit} บาท พอปิดยอดค้างของใบนี้ ({balance} บาท) · "
                + "เปิดใบแล้วกด “ใช้เงินรอจัดสรร” หรือจัดสรรที่หน้า “เงินรอจัดสรร”");
        return new DunningSuppressionDecision(false,
            $"ลูกค้ารายนี้มีเงินรอจัดสรร {credit} บาท (น้อยกว่ายอดค้างของใบ {balance} บาท) — ตรวจว่าควรจัดสรรให้ใบนี้ก่อนทวงหรือไม่");
    }
}
