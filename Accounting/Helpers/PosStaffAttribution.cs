namespace Accounting.Helpers;

/// <summary>บรรทัดหนึ่งของบิล POS ที่ระบุช่าง/พนักงานผู้ทำ — ยอดตามที่ตรึงบนบรรทัด (ก่อนส่วนลดระดับบิล · รวม VAT)</summary>
/// <param name="StaffId">ผู้ทำ (UserId) · null = ไม่ระบุ (ไม่นับให้ใคร)</param>
/// <param name="LineGross"><c>PosOrderItem.TotalAmount</c> — ยอดบรรทัดหลังส่วนลดรายการ ก่อนส่วนลดท้ายบิล รวม VAT</param>
/// <param name="Quantity">จำนวนทั้งหมดของบรรทัด</param>
/// <param name="RefundedQuantity">จำนวนที่คืนเงินไปแล้ว (ยอดที่คืนไม่ใช่ยอดขายของช่าง)</param>
public readonly record struct PosStaffLine(
    Guid? StaffId,
    decimal LineGross,
    decimal Quantity,
    decimal RefundedQuantity);

/// <summary>บิล POS หนึ่งใบที่ปิดแล้ว — ค่าระดับบิลที่ตรึงไว้ตอนปิด (ตัวเดียวกับที่ JE ขายใช้)</summary>
/// <param name="LineGrossTotal">Σ <c>TotalAmount</c> ของบรรทัดที่ยังอยู่ (<c>!IsDeleted</c>) — ฐานเดียวกับ <c>RecalculateOrder</c></param>
/// <param name="DiscountAmount"><c>PosOrder.DiscountAmount</c> — <b>รวมคูปองแล้ว</b> ห้ามบวกคูปองซ้ำ (บั๊ก D8-2)</param>
/// <param name="TotalAmount"><c>PosOrder.TotalAmount</c> — หลังส่วนลด + ค่าบริการ รวม VAT</param>
/// <param name="VatAmount"><c>PosOrder.VatAmount</c> — VAT ทั้งบิลที่ลงบัญชีไว้</param>
public sealed record PosStaffOrder(
    decimal LineGrossTotal,
    decimal DiscountAmount,
    decimal TotalAmount,
    decimal VatAmount,
    IReadOnlyList<PosStaffLine> Lines);

/// <summary>ยอดขายสุทธิของช่าง/พนักงานหนึ่งคน</summary>
public readonly record struct PosStaffShare(Guid StaffId, decimal NetSales, int LineCount);

/// <summary>
/// **ยอดขายที่นับให้ช่าง/พนักงานรายบรรทัด** — ตัวตั้งตัวเดียวของ "ใครขาย/ทำได้เท่าไร" จากบิล POS
/// (ฐานคอมมิชชันแผน <c>PosStaffSales</c> ใน <see cref="CommissionPlanRules"/> + รายงานต้นทุนแรงงาน)
///
/// <para>═══ ที่มา (ทีม K · 2026-10-22) ═══ ร้านทำผม/สปา/นวด หนึ่งบิลมีหลายช่าง (สระ = ช่าง A · ตัด = ช่าง B ·
/// ขายแชมพู = แคชเชียร์) แต่เดิมบิลมีแค่ <c>PrimaryStaffId</c> ตัวเดียว และแผนคอมมิชชันนับยอดจาก
/// <c>Document.CreatedBy</c> (คนคีย์เอกสาร ไม่ใช่คนทำงาน) ⇒ ค่าคอมไม่มีทางถูกสำหรับร้านหลายช่าง</para>
///
/// <para><b>นิยามยอดที่นับ</b> (ต้องตรงกับเงินที่ร้านได้จริง ไม่ใช่ราคาป้าย):
/// <list type="number">
/// <item>ส่วนลดท้ายบิล/คูปองเฉลี่ยตามสัดส่วนยอดบรรทัด — ใช้ <see cref="PosRefundMath.DiscountFactor"/> ตัวเดียวกับการคืนเงิน
///   (สูตรสัดส่วนที่สองในระบบ = ยอดคอมกับยอดคืนเงินเพี้ยนกันคนละทาง)</item>
/// <item><b>ไม่รวม VAT</b> — VAT เป็นเงินของรัฐ · สัดส่วนก่อนภาษีคิดจาก <c>(TotalAmount − VatAmount) ÷ TotalAmount</c>
///   ของบิลนั้น (ค่าที่ลงบัญชีไว้จริง ไม่ใช่อัตราวันนี้ — บริษัทที่เพิ่งจด VAT จะไม่ถูกคิดย้อนหลัง)</item>
/// <item><b>ไม่รวมค่าบริการ (service charge) และทิป</b> — ค่าบริการเป็นของร้าน · ทิปเป็นหนี้สินที่ถือแทนพนักงาน
///   (มีเส้นจ่ายของตัวเอง) — นับซ้ำเป็นยอดขายของช่าง = จ่ายสองต่อ</item>
/// <item>หักจำนวนที่คืนเงินไปแล้วตามสัดส่วน — ของที่ลูกค้าคืนไม่ใช่ผลงานที่ได้เงิน</item>
/// <item>ปัดรายบรรทัด 2 ตำแหน่ง <c>AwayFromZero</c> (กฎเหล็ก #4 E) · บรรทัดติดลบนับเป็น 0</item>
/// </list></para>
///
/// <para>ค่าคอมแบบ "ต่อขั้นตอนบริการ" (<c>PosServiceActivity.CommissionAmount</c>) เป็นอีกเส้นที่มีอยู่แล้ว — ตัวนี้ไม่แตะ ·
/// บรรทัดแพ็กเกจที่ระบุช่างจะ<b>เติมช่างลงขั้นตอนที่ยังว่าง</b>ใน service (ไม่เขียนทับขั้นตอนที่ระบุคนแล้ว)</para>
/// </summary>
public static class PosStaffAttribution
{
    private const MidpointRounding R = MidpointRounding.AwayFromZero;

    /// <summary>สัดส่วนก่อน VAT ของบิล — บิลยอด 0 หรือ VAT ไม่สมเหตุผล ⇒ 1 (ไม่มี VAT ให้หัก) / 0 (ไม่มีเงิน)</summary>
    internal static decimal ExVatFactor(decimal totalAmount, decimal vatAmount)
    {
        if (totalAmount <= 0m) return 0m;
        if (vatAmount <= 0m) return 1m;
        var f = (totalAmount - vatAmount) / totalAmount;
        return f < 0m ? 0m : f > 1m ? 1m : f;
    }

    /// <summary>ยอดขายสุทธิ (ก่อน VAT · หลังส่วนลดท้ายบิล · หักที่คืนแล้ว) ของบรรทัดหนึ่งในบิลหนึ่ง</summary>
    internal static decimal LineNetSales(PosStaffOrder order, PosStaffLine line)
    {
        if (line.Quantity <= 0m || line.LineGross <= 0m) return 0m;
        var kept = line.Quantity - (line.RefundedQuantity < 0m ? 0m : line.RefundedQuantity);
        if (kept <= 0m) return 0m;
        var ratio = kept >= line.Quantity ? 1m : kept / line.Quantity;
        var discount = PosRefundMath.DiscountFactor(order.LineGrossTotal, order.DiscountAmount);
        var exVat = ExVatFactor(order.TotalAmount, order.VatAmount);
        return Math.Round(line.LineGross * ratio * discount * exVat, 2, R);
    }

    /// <summary>รวมยอดต่อช่างจากหลายบิล — เรียงตามยอดมากไปน้อย แล้วตาม StaffId (ผลคงที่ ไม่ขึ้นกับลำดับข้อมูล)</summary>
    public static IReadOnlyList<PosStaffShare> ByStaff(IEnumerable<PosStaffOrder> orders)
    {
        var sum = new Dictionary<Guid, (decimal Net, int Count)>();
        foreach (var o in orders)
            foreach (var l in o.Lines)
            {
                if (l.StaffId is not Guid sid || sid == Guid.Empty) continue;
                var net = LineNetSales(o, l);
                sum.TryGetValue(sid, out var cur);
                sum[sid] = (cur.Net + net, cur.Count + 1);
            }
        return sum.Select(kv => new PosStaffShare(kv.Key, kv.Value.Net, kv.Value.Count))
            .OrderByDescending(s => s.NetSales).ThenBy(s => s.StaffId)
            .ToList();
    }

    /// <summary>ยอดรวมของช่างคนเดียว (ฐานคอมมิชชันแผน <c>PosStaffSales</c>)</summary>
    public static decimal NetSalesFor(Guid staffId, IEnumerable<PosStaffOrder> orders)
        => ByStaff(orders).Where(s => s.StaffId == staffId).Select(s => s.NetSales).FirstOrDefault();
}
