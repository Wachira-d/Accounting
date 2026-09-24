using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ข้อเสนอลงส่วนต่าง "ยอดตามใบกำกับ ↔ ยอดชำระจริง" ที่อ่านได้จากกระดาษ</summary>
/// <param name="InvoiceTotal">ยอดตามใบกำกับ (ยอดตั้งหนี้ — ห้ามแก้)</param>
/// <param name="AmountPaid">ยอดที่ชำระจริงที่พิมพ์บนกระดาษ</param>
/// <param name="Lines">บรรทัดปรับ — Σ = ยอดชำระจริง − ยอดตามใบกำกับ พอดี</param>
public sealed record OcrSettlementPlan(decimal InvoiceTotal, decimal AmountPaid, IReadOnlyList<SettlementAdjustmentLine> Lines);

/// <summary>
/// **แปลงแถวปรับหลังยอดรวมทั้งสิ้นบนกระดาษ ([PAY≠TOTAL]) เป็นข้อเสนอบรรทัดปรับตอนชำระ** — pure · ไม่ throw
///
/// ═══ ที่มา (คำตัดสินเจ้าของ รอบ 193 ข้อ 1 · 3) ═══
/// <para><see cref="OcrTotalDecomposer"/> รอบ 192 รู้แล้วว่าใบ Shopee "ส่วนลดพิเศษ 98" เป็นการปรับตอนชำระ แต่หยุดไว้ที่แท็ก
/// <c>[PAY≠TOTAL]</c> เพราะวิธีลงบัญชียังเป็นคำถามเจ้าของ — ตอนนี้เจ้าของตัดสินแล้ว: ค่าส่ง → 51120 · คูปอง/ส่วนลดหลังยอดรวม →
/// 51150 · "ส่วนลดพิเศษ X" ที่ไม่มีรายละเอียด = ส่วนลดการค้าก้อนเดียว 51150 · ตัวนี้แค่<b>เสนอ</b> ผู้ใช้ยืนยันด้วยการอนุมัติ</para>
///
/// <para><b>ไม่รู้ = ไม่เสนอ</b> (DECISION_DOCTRINE §1 G3): แถวปรับที่ป้ายไม่ใช่ค่าส่ง/ส่วนลด (เช่น "ค่าธรรมเนียม +20") ⇒ ไม่มีข้อเสนอ
/// ทั้งชุด — ห้ามเดาผังบัญชีจากป้ายที่ไม่รู้จัก · <c>[PAY≠TOTAL]</c> ยังหยุดการอนุมัติเองตามเดิม</para>
///
/// <para>หมายเหตุ <see cref="PlanTag"/> เป็นช่องส่งต่อ "ข้อเสนอ" จากตอนสแกน (ที่มีข้อความบนหน้า PDF) ไปถึงตอนสร้างเอกสาร
/// (ที่ข้อความดิบของใบ e-Tax คือ XML ซึ่งไม่มีแถวชำระ) — เขียนด้วย <see cref="Note"/> อ่านด้วย <see cref="Parse"/> คู่เดียว
/// (canonical write/read — กฎเหล็ก #4 C) · <see cref="SettledTag"/> = บันทึกบรรทัดปรับลงเอกสารแล้ว ⇒
/// <see cref="OcrPostingReadiness"/> ไม่นับ <c>[PAY≠TOTAL]</c> เป็นตัวหยุดอีก</para>
/// </summary>
public static class OcrSettlementProposal
{
    /// <summary>ข้อเสนอบรรทัดปรับ (ยังไม่ได้บันทึก)</summary>
    public const string PlanTag = "[PAY-PLAN]";

    /// <summary>บันทึกบรรทัดปรับลงเอกสารแล้ว — แท็ก <c>[PAY≠TOTAL]</c> เลิกหยุดการอนุมัติเอง</summary>
    public const string SettledTag = "[PAY-SETTLED]";

    /// <summary>เอกสารตั้งหนี้ (ใบแจ้งหนี้ซื้อ/ค่าใช้จ่าย) ที่ส่วนต่าง "รอลงที่ขั้นบันทึกการชำระ" — ยอดเอกสารตามใบกำกับถูกแล้ว (ข้อ 1)
    /// จึงไม่มีอะไรต้องแก้ก่อนอนุมัติ ⇒ <c>[PAY≠TOTAL]</c> เลิกหยุดการอนุมัติเอง (ฝ่ายค้าน C8 รอบ 193: เดิมไม่มีใครเขียนแท็กปลดให้
    /// ใบตั้งหนี้ และชำระก่อนอนุมัติไม่ได้ ⇒ อนุมัติเองไม่ได้ตลอดไป) · ไม่ติดไปกับการอัปไฟล์ซ้ำ (ขึ้นกับชนิดเอกสารที่สร้าง)</summary>
    public const string DeferredTag = "[PAY-AT-PAYMENT]";

    // ค่าเผื่อตัวเดียวกับ AutoPost/ด่านเอกสาร (ฝ่ายค้าน P3 — เดิม 0.02 กับ 0.005)
    private const decimal Tol = PaymentSettlementAdjustment.MatchTolerance;
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>ป้ายค่าขนส่งที่แพลตฟอร์ม/ขนส่งเก็บ (ไม่อยู่ในใบกำกับของผู้ขาย) ⇒ 51120</summary>
    private static readonly Regex FreightLabel = new(
        @"ค่า(?:จัด)?ส่ง|ค่าขนส่ง|shipping|delivery|freight", Opt);

    /// <summary>ป้ายคูปอง/ส่วนลดที่ลดยอดชำระ ⇒ 51150 (ลดต้นทุน)</summary>
    private static readonly Regex DiscountLabel = new(
        @"voucher|coupon|คูปอง|โค้ด|code|ส่วนลด|discount|cash ?back|เงินคืน|coins?|promo", Opt);

    /// <summary>ข้อเสนอจากผลแตกยอดของ <see cref="OcrTotalDecomposer.Decompose"/> — null = ไม่มีข้อเสนอ</summary>
    /// <param name="d">ผลแตกยอด</param>
    /// <param name="total">ยอดตามใบกำกับที่ยึดแล้ว</param>
    public static OcrSettlementPlan? FromDecomposition(OcrTotalDecomposition d, decimal? total)
    {
        if (d.Placement != OcrDiscountPlacement.PostInvoice || d.AmountSettled is not decimal paid
            || total is not decimal t || paid <= 0m)
            return null;

        var lines = new List<SettlementAdjustmentLine>();
        if (d.Adjustments.Count == 0)
        {
            // "ส่วนลดพิเศษ X" หลังยอดรวม ไม่มีรายละเอียด = ส่วนลดการค้าก้อนเดียว ลดต้นทุน (ข้อ 3)
            lines.Add(new SettlementAdjustmentLine(PaymentSettlementAdjustment.PurchaseDiscountAccountCode,
                -d.Discount, "ส่วนลดหลังยอดรวมทั้งสิ้น (กระดาษไม่แยกรายละเอียด)"));
        }
        else
        {
            foreach (var a in d.Adjustments)
            {
                var code = a.Amount > 0m && FreightLabel.IsMatch(a.Label) ? PaymentSettlementAdjustment.FreightInAccountCode
                    : a.Amount < 0m && DiscountLabel.IsMatch(a.Label) ? PaymentSettlementAdjustment.PurchaseDiscountAccountCode
                    : null;
                if (code is null) return null;   // ป้ายที่ไม่รู้จัก — ไม่เดาผังบัญชี
                lines.Add(new SettlementAdjustmentLine(code, a.Amount, Clean(a.Label)));
            }
        }

        // ต้องอธิบายส่วนต่างพอดี (ยอดชำระ = ยอดใบกำกับ + Σ บรรทัดปรับ) — ไม่งั้นเป็นตัวเลขคนละเรื่อง
        if (Math.Abs(t + lines.Sum(l => l.Amount) - paid) > Tol) return null;
        return new OcrSettlementPlan(t, paid, lines);
    }

    /// <summary>ข้อเสนอสำหรับสแกนเก่า/ทางเข้าที่อ่านข้อความดิบตอนสร้าง — คืนหมายเหตุที่ต้องต่อท้าย
    /// (null = ไม่มีข้อเสนอ หรือสแกนมีหมายเหตุนี้แล้ว)</summary>
    public static string? ForScan(
        string? rawText, decimal? subTotal, decimal? vat, decimal? total, decimal billDiscount, string? existingNotes)
    {
        if (billDiscount <= 0m || (existingNotes ?? "").Contains(PlanTag, StringComparison.Ordinal)) return null;
        var d = OcrTotalDecomposer.Decompose(rawText, subTotal, vat, total, billDiscount);
        return FromDecomposition(d, total) is { } plan ? Note(plan) : null;
    }

    /// <summary>หมายเหตุ <see cref="PlanTag"/> — ส่วนหลังเครื่องหมาย ":" เป็นรูปแบบที่ <see cref="Parse"/> อ่านกลับ
    /// (ตัวเลขแบบ invariant ไม่มีจุลภาค · เครื่องหมาย ASCII)</summary>
    public static string Note(OcrSettlementPlan plan)
    {
        var ci = CultureInfo.InvariantCulture;
        var parts = new List<string>
        {
            "ใบกำกับ=" + plan.InvoiceTotal.ToString("0.00", ci),
            "ชำระ=" + plan.AmountPaid.ToString("0.00", ci),
        };
        parts.AddRange(plan.Lines.Select(l =>
            $"{l.AccountCode}={(l.Amount >= 0m ? "+" : "-")}{Math.Abs(l.Amount).ToString("0.00", ci)} {Clean(l.Reason)}".TrimEnd()));
        return $"{PlanTag} ข้อเสนอลงส่วนต่างตอนชำระ (ยอดเอกสารคงตามใบกำกับ): " + string.Join(" · ", parts);
    }

    private static readonly Regex NoteLine = new(
        @"^\[PAY-PLAN\][^:\n]*:[ \t]*(?<body>[^\n]+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex KeyValue = new(
        @"^(?<key>ใบกำกับ|ชำระ|\d{3,6})=(?<sign>[+\-]?)(?<num>\d+(?:\.\d{1,2})?)(?:[ \t]+(?<label>.+))?$",
        RegexOptions.CultureInvariant);

    /// <summary>อ่านข้อเสนอกลับจากหมายเหตุของสแกน (บรรทัด <see cref="PlanTag"/> สุดท้าย) — null เมื่อไม่มี/อ่านไม่ได้/ไม่ลงตัว</summary>
    public static OcrSettlementPlan? Parse(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var matches = NoteLine.Matches(notes);
        if (matches.Count == 0) return null;
        var body = matches[^1].Groups["body"].Value.Trim();

        decimal? total = null, paid = null;
        var lines = new List<SettlementAdjustmentLine>();
        foreach (var raw in body.Split(" · ", StringSplitOptions.RemoveEmptyEntries))
        {
            var m = KeyValue.Match(raw.Trim());
            if (!m.Success) return null;
            if (!decimal.TryParse(m.Groups["num"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v))
                return null;
            var key = m.Groups["key"].Value;
            if (key == "ใบกำกับ") total = v;
            else if (key == "ชำระ") paid = v;
            else
            {
                var signed = m.Groups["sign"].Value == "-" ? -v : v;
                var label = m.Groups["label"].Success ? m.Groups["label"].Value.Trim() : null;
                lines.Add(new SettlementAdjustmentLine(key, signed, string.IsNullOrEmpty(label) ? null : label));
            }
        }
        if (total is not decimal t || paid is not decimal p || lines.Count == 0) return null;
        if (Math.Abs(t + lines.Sum(l => l.Amount) - p) > Tol) return null;
        return new OcrSettlementPlan(t, p, lines);
    }

    /// <summary>หมายเหตุ <see cref="SettledTag"/> — เขียนเมื่อบรรทัดปรับถูกบันทึกลงเอกสารแล้ว</summary>
    public static string SettledNote(OcrSettlementPlan plan, string documentRef)
        => $"{SettledTag} บันทึกส่วนต่างยอดชำระลงเอกสาร {documentRef} แล้ว — ยอดเอกสาร {plan.InvoiceTotal:N2} · "
            + $"ชำระจริง {plan.AmountPaid:N2} · "
            + string.Join(" · ", plan.Lines.Select(l =>
                $"{l.AccountCode} {(l.Amount >= 0m ? "+" : "−")}{Math.Abs(l.Amount):N2}{(string.IsNullOrEmpty(l.Reason) ? "" : " " + l.Reason)}"))
            + " (ตรวจ/แก้ได้ที่หน้า 'ปรับปรุงรายการบัญชี' ก่อนอนุมัติ)";

    /// <summary>หมายเหตุ <see cref="DeferredTag"/> — เอกสารตั้งหนี้ ยอดตามใบกำกับ · ส่วนต่างลงตอนบันทึกการชำระ
    /// (หน้าบันทึกการชำระเติมเงินที่จ่ายจริง + บรรทัดปรับชุดเดียวกันให้)</summary>
    /// <param name="plan">ข้อเสนอที่ลงตัวกับยอดเอกสาร — null = กระดาษอธิบายส่วนต่างไม่ครบ/มีหัก ณ ที่จ่าย (ผู้ใช้ใส่บรรทัดปรับเองตอนชำระ)</param>
    /// <param name="documentTotal">ยอดเอกสาร (ตามใบกำกับ)</param>
    public static string DeferredNote(OcrSettlementPlan? plan, decimal documentTotal)
        => plan is null
            ? $"{DeferredTag} เอกสารตั้งหนี้ลงยอดตามใบกำกับ {documentTotal:N2} (ถูกต้อง อนุมัติได้ตามปกติ) — "
              + "ส่วนต่างกับเงินที่จ่ายจริงลงที่ขั้นบันทึกการชำระ (กระดาษอธิบายส่วนต่างไม่ครบ — ใส่บรรทัดปรับเองในหน้าบันทึกการชำระ)"
            : $"{DeferredTag} เอกสารตั้งหนี้ลงยอดตามใบกำกับ {plan.InvoiceTotal:N2} (ถูกต้อง อนุมัติได้ตามปกติ) — "
              + $"ส่วนต่างกับเงินที่จ่ายจริง {plan.AmountPaid:N2} ลงที่ขั้นบันทึกการชำระ: "
              + string.Join(" · ", plan.Lines.Select(l =>
                  $"{l.AccountCode} {(l.Amount >= 0m ? "+" : "−")}{Math.Abs(l.Amount):N2}{(string.IsNullOrEmpty(l.Reason) ? "" : " " + l.Reason)}"))
              + " (หน้าบันทึกการชำระเติมให้)";

    /// <summary>ป้ายสั้น ๆ ที่ไม่มีตัวคั่นของรูปแบบหมายเหตุ ("·" · ขึ้นบรรทัด)</summary>
    private static string Clean(string? label)
        => string.IsNullOrWhiteSpace(label) ? ""
            : Regex.Replace(label.Replace("·", " "), @"\s+", " ").Trim();
}
