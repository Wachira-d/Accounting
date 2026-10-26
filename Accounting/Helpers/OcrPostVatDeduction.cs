using System;
using System.Linq;

namespace Accounting.Helpers;

/// <summary>ค่าที่สแกนเติมลงช่อง "หักหลัง VAT" ของเอกสาร</summary>
/// <param name="Deduction">ยอดหักหลัง VAT (= ยอดตามใบกำกับ − ยอดชำระ)</param>
/// <param name="InvoiceTotal">ยอดรวมทั้งสิ้นตามใบกำกับ (ฐาน + VAT — ห้ามบีบ)</param>
/// <param name="AmountDue">ยอดชำระที่พิมพ์</param>
/// <param name="AccountCode">ผังค่าเริ่มต้นของขาหัก (51150 — บริษัทที่ไม่มีผังนี้ตกไปผังค่าใช้จ่ายหลักของใบที่ service)</param>
/// <param name="Detail">รายละเอียดบนกระดาษ (ค่าจัดส่ง +37 · Shopee Voucher −135) หรือ null</param>
public sealed record OcrPostVatDeductionRead(decimal Deduction, decimal InvoiceTotal, decimal AmountDue, string AccountCode, string? Detail);

/// <summary>
/// **สแกน → ช่อง "หักหลัง VAT"** (pure · ไม่ throw) — ต่อยอดจาก <see cref="OcrTotalDecomposer"/> (ตัวตัดสิน "ส่วนลดอยู่หลัง VAT ไหม"
/// ตัวเดียว — ไม่เขียนตัวจับซ้ำ) และ <see cref="OcrSettlementProposal"/> (ข้อเสนอที่ส่งผ่านหมายเหตุสแกน)
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-10-26 · ทีม U) ═══
/// <para>ใบ Shopee: "ส่วนลดพิเศษ 98" พิมพ์หลัง "จำนวนเงินรวมทั้งสิ้น 536" · ยอดชำระ 438 ⇒ <see cref="OcrDiscountPlacement.PostInvoice"/> ·
/// เดิม (รอบ 193) เส้นสร้างเอกสารลงเป็น adjusting lines 51120 +37 / 51150 −135 ซึ่งเส้น "แก้ในฟอร์มก่อน" ยกไปไม่ได้ ⇒ ใบที่สร้างผ่านฟอร์ม
/// ไม่มีขาส่วนต่าง · ผู้ใช้สั่ง (2026-10-26): ให้เป็น<b>ช่องระดับเอกสาร</b> ยอดเดียว (98) ลงผังที่เลือก (ส่วนลดรับ 51150 หรือผังค่าใช้จ่ายของใบ)
/// ⇒ ตัวนี้ย่อยข้อเสนอเป็นยอดหักก้อนเดียว · รายละเอียดเก็บเป็นหมายเหตุ</para>
///
/// <para><b>ทิศตรงข้าม</b>: ส่วนลดก่อน VAT (เคส C/F/G · Rapbizz · Lazada) / ส่วนลดในโลกรวม VAT (เคส E) ⇒ <see cref="OcrDiscountPlacement.PreVat"/>/<see cref="OcrDiscountPlacement.InclVat"/>/<see cref="OcrDiscountPlacement.Unknown"/>
/// ⇒ คืน null (ไม่ใช่หักหลัง VAT — ฐาน/VAT ถูกลดตามกระดาษโดยตัวสร้างบรรทัดเดิม)</para>
/// </summary>
public static class OcrPostVatDeduction
{
    /// <summary>แท็กในหมายเหตุสแกน — เติมช่องหักหลัง VAT ลงเอกสารแล้ว (ปลด <c>[PAY≠TOTAL]</c> เหมือน <see cref="OcrSettlementProposal.SettledTag"/>)</summary>
    public const string FilledTag = "[POST-VAT-DEDUCTION]";

    /// <summary>จากผลแตกยอดของกระดาษ — null เมื่อไม่ใช่ PostInvoice หรือยอดชำระไม่น้อยกว่ายอดใบ</summary>
    public static OcrPostVatDeductionRead? FromDecomposition(OcrTotalDecomposition d, decimal? total)
    {
        if (d.Placement != OcrDiscountPlacement.PostInvoice || d.AmountSettled is not decimal paid || total is not decimal t)
            return null;
        var deduction = Math.Round(t - paid, 2, MidpointRounding.AwayFromZero);
        if (deduction <= 0m) return null;   // จ่ายมากกว่าใบ (ค่าส่งล้วน) ไม่ใช่ "หัก" — ปล่อยให้บรรทัดปรับเดิมจัดการ
        var detail = d.Adjustments.Count == 0 ? null
            : string.Join(" · ", d.Adjustments.Select(a => $"{a.Label} {a.Amount:+#,##0.00;−#,##0.00}"));
        return new(deduction, t, paid, PostVatDeduction.DefaultAccountCode, detail);
    }

    /// <summary>จากข้อเสนอที่อ่านกลับจากหมายเหตุสแกน (<see cref="OcrSettlementProposal.Parse"/>) — null เมื่อไม่มีข้อเสนอ
    /// หรือเงินที่จ่ายไม่น้อยกว่ายอดใบ · ผัง: ถ้าทุกบรรทัดในข้อเสนอเป็นยอดลบและใช้ผังเดียว ⇒ ผังนั้น · ไม่งั้นผังค่าเริ่มต้น</summary>
    public static OcrPostVatDeductionRead? FromPlan(OcrSettlementPlan? plan)
    {
        if (plan is null) return null;
        var deduction = Math.Round(plan.InvoiceTotal - plan.AmountPaid, 2, MidpointRounding.AwayFromZero);
        if (deduction <= 0m) return null;
        var negativeCodes = plan.Lines.Where(l => l.Amount < 0m).Select(l => l.AccountCode).Distinct().ToList();
        var code = plan.Lines.All(l => l.Amount < 0m) && negativeCodes.Count == 1
            ? negativeCodes[0]
            : PostVatDeduction.DefaultAccountCode;
        var detail = plan.Lines.Count == 1 && string.IsNullOrWhiteSpace(plan.Lines[0].Reason) ? null
            : string.Join(" · ", plan.Lines.Select(l =>
                $"{(string.IsNullOrWhiteSpace(l.Reason) ? l.AccountCode : l.Reason)} {l.Amount:+#,##0.00;−#,##0.00}"));
        return new(deduction, plan.InvoiceTotal, plan.AmountPaid, code, detail);
    }

    /// <summary>หมายเหตุ <see cref="FilledTag"/> — เขียนเมื่อเติมช่องลงเอกสารแล้ว</summary>
    public static string FilledNote(OcrPostVatDeductionRead r, string accountCodeUsed, string documentRef)
        => $"{FilledTag} เติมช่อง \"หักหลัง VAT\" {r.Deduction:N2} ลงเอกสาร {documentRef} (ผัง {accountCodeUsed}) — ยอดตามใบกำกับ {r.InvoiceTotal:N2} "
            + $"(ฐาน/VAT คงตามใบ) · ยอดชำระ {r.AmountDue:N2}"
            + (r.Detail is null ? "" : $" · กระดาษ: {r.Detail}")
            + " · ตรวจ/เปลี่ยนผังได้ในฟอร์มก่อนอนุมัติ";
}
