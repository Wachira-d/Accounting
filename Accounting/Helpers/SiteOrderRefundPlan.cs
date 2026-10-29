using Accounting.Models.Enums;

namespace Accounting.Helpers;

/// <summary>บรรทัดของใบขายต้นทาง (ข้อมูลที่ใบลดหนี้ต้องลอก) — ไม่ผูก entity เพื่อให้เทสต์ได้</summary>
public sealed record RefundSourceLine(string Description, decimal Quantity, string? Unit, decimal UnitPrice, decimal DiscountPercent,
    decimal DiscountAmount, decimal VatRate, Guid? AccountId, string? ProductCode);

/// <summary>บรรทัดใบลดหนี้ร่างที่ได้ (ยอดคูณสัดส่วนแล้ว)</summary>
public sealed record RefundCreditLine(string Description, decimal Quantity, string? Unit, decimal UnitPrice, decimal DiscountPercent,
    decimal? DiscountAmount, decimal VatRate, Guid? AccountId, string? ProductCode);

/// <summary>ผลแผน — <see cref="Problem"/> ไม่ว่าง = ห้ามเปลี่ยนสถานะ (ข้อความไทยถึงผู้กด + ทางไปต่อ)</summary>
public sealed record RefundCreditPlan(IReadOnlyList<RefundCreditLine> Lines, decimal Amount, decimal Ratio, decimal BillDiscount,
    decimal Refundable, decimal ShippingExcluded, string? Problem);

/// <summary>
/// <b>ตัวตัดสินเดียว</b>ของ "คืนเงินหลังขาย ⇒ ใบลดหนี้ร่างหน้าตาอย่างไร" (รอบ 209 P3 · R-209-E8 · คำตัดสินข้อ 168 ฉ) และบรรทัดใบลดหนี้ของการยกเลิกหลังชำระ (R-07 เดิม)
/// <para><b>ที่มา</b>: สถานะ "คืนเงิน/คืนเงินบางส่วน" เดิมเปลี่ยนแค่ป้าย — ไม่มีใบลดหนี้ ⇒ ภาษีขาย/รายได้ยังเต็มทั้งที่คืนเงินแล้ว (§86/10) ·
/// ยกเลิกหลังจัดส่งเคยร่างใบลดหนี้ "เต็มใบรวมค่าส่ง" ทั้งที่ค่าส่งเกิดขึ้นจริงแล้ว</para>
/// <para><b>กติกา</b>: (1) จัดส่ง/ส่งมอบแล้ว ⇒ ไม่รวมบรรทัดค่าจัดส่ง (ข้อ 168 ฉ — ไม่คืนค่าส่งอัตโนมัติ · ก่อนส่ง = คืนเต็มเดิม) ·
/// (2) ยอดที่คืนได้ = min(เงินที่รับแล้ว, ยอดใบ) − ค่าส่งที่ไม่คืน − ใบลดหนี้ที่ออก/ร่างไว้แล้ว (Σ ใบลดหนี้ ≤ ใบเดิม §86/10) ·
/// (3) คืนเต็ม = ไม่ระบุยอด ⇒ คืนทั้งหมดที่คืนได้ · คืนบางส่วน ⇒ ต้องระบุยอด &gt; 0 และ ≤ ยอดที่คืนได้ ·
/// (4) ใบลดหนี้ = ทุกบรรทัด (ไม่รวมค่าส่งตามข้อ 1) คูณสัดส่วน ยอดคืน ÷ ยอดที่เหลือของใบ — ราคา/ส่วนลดต่อหน่วยปัด 2 ตำแหน่ง AwayFromZero
/// (จำนวนเท่าเดิม · อัตรา VAT ต่อบรรทัดเท่าเดิม ⇒ VAT ผลต่างแยกตามอัตรา) · ยอดจริงของใบร่างอาจต่างเศษสตางค์ ⇒ คนตรวจก่อนอนุมัติ ·
/// (5) <b>ร่างเท่านั้น</b> — ผู้เรียกห้ามอนุมัติเอง (R1: คืนเงินจริงแล้วค่อยอนุมัติ) · สต็อก: ใบลดหนี้เหตุ "ปรับยอด" ไม่คืนสต็อก —
/// ถ้ารับสินค้าคืน พนักงานเปลี่ยนเหตุผลเป็น "คืนสินค้า" ในใบร่างก่อนอนุมัติ (เส้นใบลดหนี้เดิมคืนสต็อกตอนอนุมัติ)</para>
/// </summary>
public static class SiteOrderRefundPlan
{
    public const string RuleCode = "CMS-ORDER-REFUND-CN";
    public const string LegalReference = "RD-86/10";
    public const string DraftTag = "[ERP-REFUND-CN-DRAFT]";
    /// <summary>เหตุผลบนใบลดหนี้ร่างของการคืนเงิน — ปรับยอด (ไม่คืนสต็อก) · รับของคืน ⇒ คนเปลี่ยนเป็น "คืนสินค้า" ก่อนอนุมัติ</summary>
    public const CreditNoteReason DraftReason = CreditNoteReason.Adjustment;
    private const MidpointRounding R = MidpointRounding.AwayFromZero;
    private static decimal R2(decimal v) => Math.Round(v, 2, R);

    /// <summary>ค่าส่งไม่คืนอัตโนมัติเมื่อออเดอร์ผ่านจุดจัดส่ง/ส่งมอบแล้ว (ข้อ 168 ฉ)</summary>
    public static bool ExcludesShipping(DateTime? shippedAt, DateTime? deliveredAt) => shippedAt != null || deliveredAt != null;

    /// <summary>คำอธิบาย+ยอดของบรรทัดค่าส่งที่ใบขายของออเดอร์มี (ตัวแบ่งเดียวกับตอนออกใบ — <see cref="ShippingVatAllocation.Split"/>)</summary>
    public static IReadOnlyList<(string Description, decimal Fee)> ShippingPartsOf(IReadOnlyList<ShippingVatAllocation.Part> parts)
        => parts.Select(p => (ShippingVatAllocation.LineDescription(p, parts.Count), p.Fee)).ToList();

    /// <summary>วางแผนใบลดหนี้ — บรรทัดค่าจัดส่งจับด้วยคำอธิบายตรงกับ <see cref="ShippingVatAllocation.LineDescription"/> + จำนวน 1 + ราคา = ค่าส่งส่วนนั้น (ทีละส่วน)</summary>
    public static RefundCreditPlan Plan(IReadOnlyList<RefundSourceLine> docLines, decimal docTotal, decimal docBillDiscount,
        IReadOnlyCollection<(string Description, decimal Fee)> shippingParts, bool excludeShipping,
        decimal alreadyCredited, decimal paid, decimal? requestedAmount, bool fullRefund)
    {
        var kept = new List<RefundSourceLine>();
        var shippingExcluded = 0m;
        var used = new List<(string Description, decimal Fee)>();
        foreach (var l in docLines)
        {
            if (excludeShipping)
            {
                var match = shippingParts.FirstOrDefault(p => !used.Contains(p) && p.Description == l.Description && l.Quantity == 1m && R2(p.Fee) == R2(l.UnitPrice));
                if (match.Description != null)
                {
                    used.Add(match);
                    shippingExcluded += R2(match.Fee);
                    continue;
                }
            }
            kept.Add(l);
        }
        var eligible = Math.Max(0m, docTotal - shippingExcluded);
        var refundable = Math.Max(0m, Math.Min(Math.Min(paid, docTotal) - shippingExcluded, eligible) - alreadyCredited);
        refundable = R2(refundable);

        RefundCreditPlan Fail(string msg) => new(Array.Empty<RefundCreditLine>(), 0m, 0m, 0m, refundable, shippingExcluded, msg);

        if (kept.Count == 0 || eligible <= 0m)
            return Fail("ใบขายไม่มีบรรทัดที่คืนเงินได้ (เหลือแต่ค่าจัดส่งที่ไม่คืนหลังจัดส่ง) — ถ้าต้องคืนค่าส่ง ออกใบลดหนี้เองที่หน้าเอกสาร");
        if (refundable <= 0.005m)
            return Fail(alreadyCredited > 0m
                ? $"ออกใบลดหนี้ไว้แล้ว {alreadyCredited:N2} บาท เต็มยอดที่คืนได้ — ไม่ร่างซ้ำ · ตรวจใบลดหนี้เดิมที่หน้าเอกสาร"
                : "ยังไม่มีเงินรับที่คืนได้ (หลังหักค่าจัดส่งที่ไม่คืน) — ใช้ \"ยกเลิก\" หรือออกใบลดหนี้เองที่หน้าเอกสาร");
        decimal amount;
        if (requestedAmount is decimal req)
        {
            amount = R2(req);
            if (amount <= 0m) return Fail("ยอดคืนเงินต้องมากกว่า 0");
            if (amount > refundable + 0.005m)
                return Fail($"ยอดคืน {amount:N2} บาท เกินยอดที่คืนได้ {refundable:N2} บาท (รับเงินแล้ว − ค่าส่งที่ไม่คืนหลังจัดส่ง − ใบลดหนี้เดิม) · ลดยอด หรือออกใบลดหนี้เองที่หน้าเอกสาร");
        }
        else if (fullRefund) amount = refundable;
        else return Fail("คืนเงินบางส่วน — กรุณาระบุยอดที่คืน");

        var ratio = amount >= eligible - 0.005m ? 1m : amount / eligible;
        var lines = kept.Select(l => new RefundCreditLine(l.Description, l.Quantity, l.Unit,
                ratio == 1m ? l.UnitPrice : R2(l.UnitPrice * ratio), l.DiscountPercent,
                l.DiscountAmount > 0m ? (ratio == 1m ? l.DiscountAmount : R2(l.DiscountAmount * ratio)) : null,
                l.VatRate, l.AccountId, l.ProductCode))
            .ToList();
        var bill = docBillDiscount > 0m ? (ratio == 1m ? docBillDiscount : R2(docBillDiscount * ratio)) : 0m;
        return new RefundCreditPlan(lines, amount, ratio, bill, refundable, shippingExcluded, null);
    }

    /// <summary>หมายเหตุบนใบลดหนี้ร่าง (§86/10 อ้างเลขที่+วันที่ใบเดิม + เหตุ)</summary>
    public static string DraftNotes(string orderNumber, string invoiceNumber, DateTime invoiceDateUtc, decimal amount, bool partial, bool shippingExcluded, string? reason)
        => $"คืนเงินคำสั่งซื้อ {orderNumber}{(partial ? " (บางส่วน)" : "")} {amount:N2} บาท — ลดหนี้อ้างใบ {invoiceNumber} ลงวันที่ {ThaiDate.CalendarDateUtc(invoiceDateUtc):dd/MM/yyyy}"
           + (shippingExcluded ? " · ไม่รวมค่าจัดส่ง (จัดส่งแล้ว)" : "")
           + (string.IsNullOrWhiteSpace(reason) ? "" : $" · เหตุผล: {reason.Trim()}")
           + " · ร่างโดยระบบ — คืนเงินลูกค้าแล้วจึงอนุมัติ (§86/10) · รับสินค้าคืน ⇒ เปลี่ยนเหตุผลเป็น \"คืนสินค้า\" ก่อนอนุมัติ";

    /// <summary>หมายเหตุบนออเดอร์หลังร่างใบลดหนี้</summary>
    public static string ProposedNote(DateTime atUtc, string creditNoteNumber, string invoiceNumber, decimal amount, bool shippingExcluded)
        => $"{DraftTag} [{atUtc:yyyy-MM-dd HH:mm}Z] คืนเงิน {amount:N2} บาท ⇒ ระบบร่างใบลดหนี้ {creditNoteNumber} อ้างใบ {invoiceNumber} (ยังไม่อนุมัติ)"
           + (shippingExcluded ? " · ไม่รวมค่าจัดส่ง (จัดส่งแล้ว — ข้อ 168 ฉ)" : "")
           + " · คืนเงินลูกค้าจริงแล้วอนุมัติใบลดหนี้ที่หน้าเอกสาร";

    /// <summary>ไม่ร่างใบลดหนี้ (ไม่มีเอกสาร/เอกสารยังไม่รับชำระ) — บอกทางไปต่อบนออเดอร์ (ไม่เงียบ)</summary>
    public static string SkippedNote(DateTime atUtc, string why)
        => $"{DraftTag} [{atUtc:yyyy-MM-dd HH:mm}Z] ตั้งสถานะคืนเงินแล้วแต่ไม่ได้ร่างใบลดหนี้: {why}";

    public static string FailedNote(DateTime atUtc, string invoiceNumber, string reason)
        => $"[ERP-REFUND-CN-FAILED {atUtc:yyyy-MM-dd HH:mm}Z] ตั้งสถานะคืนเงินแล้วแต่ร่างใบลดหนี้อ้างใบ {invoiceNumber} ไม่สำเร็จ: {reason} — ออกใบลดหนี้เองที่หน้าเอกสาร";
}
