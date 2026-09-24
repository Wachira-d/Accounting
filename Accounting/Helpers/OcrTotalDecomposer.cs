using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ส่วนลดที่พิมพ์บนกระดาษ "อยู่ตรงไหน" เทียบกับ VAT — ตัดสินด้วยตัวเลขที่พิมพ์ ไม่ใช่ป้าย</summary>
public enum OcrDiscountPlacement
{
    /// <summary>ตัดสินไม่ได้ (หลายแบบลงตัวพร้อมกัน หรือไม่มีแบบไหนลงตัว) — ใช้พฤติกรรมเดิม</summary>
    Unknown = 0,
    /// <summary>กระดาษไม่มีส่วนลดที่มีเงิน</summary>
    None = 1,
    /// <summary>ส่วนลดก่อน VAT: ยอดก่อนลด − ส่วนลด = ฐาน · VAT = 7% ของฐาน (ร้านวัสดุ · Lazada)</summary>
    PreVat = 2,
    /// <summary>ส่วนลดในโลกราคารวม VAT: ยอดรวม VAT ก่อนลด − ส่วนลด = ยอดรวมทั้งสิ้น (ซูเปอร์ · Makro TOTAL−DISCOUNT)</summary>
    InclVat = 3,
    /// <summary>ปรับ<b>หลัง</b>ยอดใบกำกับ: VAT ถูกคิดจากยอดรวมทั้งสิ้นก่อนหัก · ยอดรวม − ส่วนลด = ยอดชำระ
    /// (Shopee: 536 − ส่วนลดพิเศษ 98 = 438) — <b>ไม่ใช่ส่วนลดในใบกำกับ</b> ห้ามลดฐาน/VAT</summary>
    PostInvoice = 4,
}

/// <summary>รายการปรับหลังยอดใบกำกับที่กระดาษพิมพ์พร้อมเครื่องหมาย (<c>ค่าจัดส่ง +฿37</c> · <c>Shopee Voucher -฿135</c>)</summary>
public sealed record OcrPaymentAdjustment(string Label, decimal Amount);

/// <summary>ผลของ <see cref="OcrTotalDecomposer.ForScan"/> — ส่วนลดที่ใช้ได้ + หมายเหตุที่สแกนยังขาด (null = ไม่ต้องเติม)</summary>
public readonly record struct OcrScanDiscountDecision(decimal DiscountToSpread, string? NoteToAppend);

/// <summary>ผลการแตกยอด</summary>
/// <param name="Discount">ส่วนลดที่พิมพ์ (0 = ไม่มี)</param>
/// <param name="DiscountToSpread">ส่วนลดที่ตัวสร้างบรรทัด<b>ยอมให้</b>กระจาย/ลดฐาน — PostInvoice = 0 · อื่น ๆ = เท่าเดิม</param>
/// <param name="AmountSettled">ยอดที่ชำระจริงที่พิมพ์ (เฉพาะ PostInvoice)</param>
/// <param name="Adjustments">รายการปรับที่พิมพ์พร้อมเครื่องหมาย — ใส่เฉพาะเมื่อผลรวมเท่ากับส่วนต่างพอดี</param>
/// <param name="Groups">ตารางสรุปตามกลุ่มภาษี (<see cref="OcrLineVatMarks.ReadGroups"/>)</param>
public sealed record OcrTotalDecomposition(
    OcrDiscountPlacement Placement,
    decimal Discount,
    decimal DiscountToSpread,
    decimal? AmountSettled,
    IReadOnlyList<OcrPaymentAdjustment> Adjustments,
    OcrVatGroupTable Groups,
    string Reason);

/// <summary>
/// **ขั้นที่ 2 ของ Total-first: แตกยอดรวมทั้งสิ้นที่ยึดแล้ว ให้ตัวเลขอื่นบนกระดาษอธิบายได้** (pure · ไม่ throw)
///
/// ═══ ที่มา (รอบ 192 · ทีม C §3) ═══
/// <para>ด่านคณิตข้อ 0 (<c>OcrConfidenceGateway</c>) กับเคส E ของ <see cref="OcrLineReconciler"/> ถามแค่ว่า
/// "ตัวเลขลงตัวไหม" — ใบ Shopee (ใบกำกับ 536 · VAT 35.07 · "ส่วนลดพิเศษ 98" หลังยอดรวมและหลัง VAT · จ่าย 438)
/// <b>ลงตัวทางคณิตแต่ผิดเรื่อง</b>: ถ้าเครื่องหยิบ 438 เป็นยอดรวม เคส E กระจายส่วนลด 98 ลงบรรทัด ⇒ ฐานภาษีซื้อ
/// 402.93 ทั้งที่ใบกำกับระบุ 500.93 · คำถามที่ขาดคือ <b>"VAT ถูกคิดจากยอดไหน"</b></para>
///
/// ═══ กติกา ═══
/// <list type="bullet">
/// <item>ตั้งสมมติฐานการวางส่วนลด 3 แบบ แล้วให้<b>ตัวเลขที่พิมพ์บนกระดาษ</b>เป็นกรรมการ (±0.02):
///   ก่อน VAT (ยอดก่อนลดที่พิมพ์ − ส่วนลด = ฐานที่ VAT ปิดยอดรวม) · รวม VAT (ยอดก่อนลดที่พิมพ์ − ส่วนลด = ยอดรวม) ·
///   หลังใบกำกับ (ยอดรวม − ส่วนลด = ยอดที่พิมพ์ · VAT ปิดยอดรวมก่อนหัก · แถวส่วนลดอยู่หลังยอดรวม)</item>
/// <item>ลงตัวแบบเดียว = ตัดสิน · ลงตัวหลายแบบหรือไม่มีแบบไหนลงตัว = <see cref="OcrDiscountPlacement.Unknown"/>
///   ⇒ ส่งส่วนลดเดิมต่อ (พฤติกรรมเดิมทุกประการ)</item>
/// <item><b>เปลี่ยนพฤติกรรมเดียว</b>: <see cref="OcrDiscountPlacement.PostInvoice"/> ⇒ <c>DiscountToSpread = 0</c>
///   (ตัวสร้างบรรทัด/ฐานภาษี/ด่านคณิตไม่เห็นส่วนลดนี้) + แท็ก <c>[PAY≠TOTAL]</c> ห้ามอนุมัติเอง — วิธีลงส่วนต่าง
///   (คูปองแพลตฟอร์ม/ค่าส่ง) เป็นคำถามเจ้าของ</item>
/// </list>
/// </summary>
public static class OcrTotalDecomposer
{
    /// <summary>แท็กห้ามอนุมัติเอง — เจ้าของคือ <see cref="OcrPostingReadiness.BlockingTags"/></summary>
    public const string PayNotTotalTag = "[PAY≠TOTAL]";

    private const decimal Tol = OcrPaperAmounts.ExactTol;

    /// <summary>ยอดบรรทัดที่พิมพ์ชนะ ราคา×จำนวน ได้เมื่อต่างไม่เกิน 1 สตางค์ (เศษปัดของราคาต่อหน่วย)</summary>
    public const decimal LinePrintedTol = 0.01m;
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>รายการปรับที่พิมพ์พร้อมเครื่องหมายและสัญลักษณ์เงิน: <c>ค่าจัดส่ง +฿37</c> · <c>Voucher -฿135</c></summary>
    private static readonly Regex SignedAdjustment = new(
        @"(?<label>[^\n·|:,;]{2,40}?)[ \t]*(?<sign>[+\-−])[ \t]*(?:฿|บาท|thb)[ \t]*(?<num>\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)", Opt);

    private static readonly Regex NotePrefix = new(@"^[ \t]*(?:หมายเหตุ|note|remarks?)[ \t]*[:：]?[ \t]*", Opt);

    /// <param name="rawText">ข้อความทั้งใบ (normalize แล้ว)</param>
    /// <param name="subTotal">ยอดก่อน VAT ที่ถืออยู่ (ข้อมูลประกอบ)</param>
    /// <param name="vat">VAT ที่ถืออยู่</param>
    /// <param name="total">ยอดรวมทั้งสิ้นที่ยึดแล้ว</param>
    /// <param name="billDiscount">ส่วนลดที่อ่านได้ (<see cref="OcrBillDiscount.Read"/>) — 0 = ไม่มี</param>
    public static OcrTotalDecomposition Decompose(
        string? rawText, decimal? subTotal, decimal? vat, decimal? total, decimal billDiscount)
    {
        var groups = OcrLineVatMarks.ReadGroups(rawText);
        var none = Array.Empty<OcrPaymentAdjustment>();
        if (billDiscount <= 0m)
            return new(OcrDiscountPlacement.None, 0m, 0m, null, none, groups, "กระดาษไม่มีส่วนลดที่มีเงิน");
        var d = billDiscount;
        if (string.IsNullOrWhiteSpace(rawText) || total is not > 0m)
            return new(OcrDiscountPlacement.Unknown, d, d, null, none, groups, "ไม่รู้ยอดรวม/ไม่มีข้อความ — ใช้ส่วนลดตามเดิม");

        // ใบหลายสกุลเงิน — ตัวเลขสองชุดลงตัวได้ทั้งคู่ ⇒ ไม่เดาทิศ ส่งส่วนลดเดิมต่อ (ฝ่ายค้าน C2)
        if (OcrPaperAmounts.HasForeignCurrency(rawText))
            return new(OcrDiscountPlacement.Unknown, d, d, null, none, groups, "ใบมีสกุลเงินต่างประเทศ — ใช้ส่วนลดตามเดิม");

        var t = total.Value;
        var printed = OcrPaperAmounts.AllPrinted(rawText);
        var vats = OcrPaperAmounts.VatAmounts(rawText).Select(v => v.Amount).ToList();
        if (vat is > 0m && !vats.Any(v => Math.Abs(v - vat.Value) <= Tol) && OcrPaperAmounts.IsPrinted(printed, vat.Value))
            vats.Add(vat.Value);

        // VAT ปิดยอดรวม t ได้ไหม (ฐานที่พิมพ์ + VAT = t · หรือ VAT = 7/107 ของ t)
        bool VatClosesTotal() => vats.Any(v => v > 0m
            && (OcrPaperAmounts.IsPrinted(printed, t - v)
                || Math.Abs(Math.Round(t * 7m / 107m, 2, MidpointRounding.AwayFromZero) - v) <= Tol));

        // H2 ก่อน VAT: มียอดก่อนลดที่พิมพ์ G โดย G − ส่วนลด = ฐาน (t − VAT) ที่พิมพ์
        var preVat = vats.Any(v => v > 0m && OcrPaperAmounts.IsPrinted(printed, t - v)
                                   && OcrPaperAmounts.IsPrinted(printed, t - v + d));
        // H3 รวม VAT: มียอดก่อนลดที่พิมพ์ G โดย G − ส่วนลด = ยอดรวม
        var inclVat = OcrPaperAmounts.IsPrinted(printed, t + d);
        // H4 หลังใบกำกับ: ยอดรวม − ส่วนลด = ยอดที่พิมพ์ · VAT ปิดยอดรวม "ก่อนหัก" · แถวส่วนลดอยู่หลังยอดรวม
        var discRow = OcrPaperAmounts.DiscountRows(rawText).FirstOrDefault(r => Math.Abs(r.Amount - d) <= Tol);
        var firstTotalLine = printed.Where(p => Math.Abs(p.Amount - t) <= Tol).Select(p => p.LineNo)
            .DefaultIfEmpty(int.MaxValue).Min();
        var paid = t - d;
        var postInvoice = paid > 0m && OcrPaperAmounts.IsPrinted(printed, paid) && VatClosesTotal()
            && discRow.Text is not null && discRow.LineNo > firstTotalLine;

        var fits = (preVat ? 1 : 0) + (inclVat ? 1 : 0) + (postInvoice ? 1 : 0);
        if (fits != 1)
            return new(OcrDiscountPlacement.Unknown, d, d, null, none, groups,
                fits == 0
                    ? $"ส่วนลด {d:N2} ยังอธิบายด้วยตัวเลขบนกระดาษไม่ได้ — ใช้ส่วนลดตามเดิม (ให้ตัวกระทบยอดตัดสิน)"
                    : $"ส่วนลด {d:N2} ลงตัวได้หลายแบบ — ไม่เดาทิศ ใช้ส่วนลดตามเดิม");

        if (preVat)
            return new(OcrDiscountPlacement.PreVat, d, d, null, none, groups,
                $"ส่วนลด {d:N2} หักก่อน VAT (ยอดก่อนลด − ส่วนลด = ฐานที่ VAT ปิดยอดรวม {t:N2})");
        if (inclVat)
            return new(OcrDiscountPlacement.InclVat, d, d, null, none, groups,
                $"ส่วนลด {d:N2} หักจากราคารวม VAT ({t + d:N2} − {d:N2} = ยอดรวมทั้งสิ้น {t:N2})");

        var adjustments = ReadAdjustments(rawText, d);
        return new(OcrDiscountPlacement.PostInvoice, d, 0m, paid, adjustments, groups,
            $"“{discRow.Text}” พิมพ์หลังยอดรวมทั้งสิ้น {t:N2} และ VAT คิดจาก {t:N2} ไม่ใช่ {paid:N2} "
            + "⇒ เป็นการปรับตอนชำระเงิน ไม่ใช่ส่วนลดในใบกำกับ (ไม่ลดฐาน/VAT)");
    }

    /// <summary>
    /// **ส่วนลดที่ตัวสร้างบรรทัด/ฐานภาษีหัวเอกสาร<b>ใช้ได้</b> + หมายเหตุที่สแกนต้องมี** — ตัวตัดสินตัวเดียวของทุกทางเข้า
    /// ฝั่งสร้างเอกสาร (สร้าง · พรีวิว · repopulate)
    ///
    /// <para>ฝ่ายค้าน P1: สแกนเก่า (ก่อนรอบ 192) ที่ persist ส่วนลด 98 ของใบ Shopee ไว้ — ตัวสร้างอ่านข้อความดิบแล้วรู้ว่าเป็น
    /// การปรับตอนชำระ (กระจาย 0) แต่หมายเหตุ <c>[PAY≠TOTAL]</c> ไม่เคยถูกเขียนบนสแกนนั้น ⇒ ด่านอนุมัติเอง
    /// (<see cref="OcrPostingReadiness"/> ที่เว็บ/LINE อ่านจากหมายเหตุของสแกน) ไม่เห็น ⇒ คืนหมายเหตุที่ต้องต่อท้าย
    /// เมื่อยังไม่มี (มีแล้ว = null · ไม่ซ้ำ)</para>
    /// </summary>
    /// <param name="existingNotes">ProcessingNotes ปัจจุบันของสแกน</param>
    public static OcrScanDiscountDecision ForScan(
        string? rawText, decimal? subTotal, decimal? vat, decimal? total, decimal billDiscount, string? existingNotes)
    {
        if (billDiscount <= 0m) return new OcrScanDiscountDecision(billDiscount, null);
        var d = Decompose(rawText, subTotal, vat, total, billDiscount);
        var note = PaymentNote(d, total);
        var missing = note is not null && !(existingNotes ?? "").Contains(PayNotTotalTag, StringComparison.Ordinal);
        return new OcrScanDiscountDecision(d.DiscountToSpread, missing ? note : null);
    }

    /// <summary>ข้อความ <c>[PAY≠TOTAL]</c> พร้อมตัวเลข — null เมื่อยอดชำระไม่ต่างจากยอดใบกำกับ</summary>
    public static string? PaymentNote(OcrTotalDecomposition d, decimal? total)
    {
        if (d.Placement != OcrDiscountPlacement.PostInvoice || d.AmountSettled is not decimal paid || total is not decimal t)
            return null;
        var adj = d.Adjustments.Count == 0 ? ""
            : " · " + string.Join(" · ", d.Adjustments.Select(a => $"{a.Label} {a.Amount:+#,##0.00;−#,##0.00}"));
        return $"{PayNotTotalTag} ยอดตามใบกำกับ {t:N2} · ยอดที่ชำระจริง {paid:N2} (ต่าง −{d.Discount:N2}{adj}) — "
            + $"{d.Reason} · ระบบลงเอกสารที่ {t:N2} ส่วนต่างเป็นเรื่องการชำระเงิน (รอกำหนดวิธีลงบัญชี) ตรวจก่อนอนุมัติ";
    }

    /// <summary>ยอดของบรรทัดที่ใช้กระทบยอด: <b>ยอดที่พิมพ์ชนะ</b>เมื่อต่างจาก ราคา×จำนวน แค่เศษปัดของราคาต่อหน่วย
    /// (Lazada: 1,228.04 × 4 = 4,912.16 แต่พิมพ์ 4,912.15) · ต่างมากกว่านั้น (ส่วนลดรายบรรทัด) = สูตรเดิม ราคา×จำนวน</summary>
    public static decimal LineGross(decimal? quantity, decimal? unitPrice, decimal? amount)
    {
        if (unitPrice is not decimal up) return amount ?? 0m;
        var q = quantity ?? 1m;
        var calc = up * q;
        // เพดาน 1 สตางค์ (ฝ่ายค้าน P5): เดิม 0.005 × จำนวน ⇒ ที่จำนวน 100 กลืนส่วนลดรายบรรทัดจริง 0.50 ได้
        if (amount is decimal a && a > 0m && Math.Abs(calc - a) <= LinePrintedTol)
            return a;
        return calc;
    }

    /// <summary>บรรทัดสรุปรายกลุ่มภาษีเมื่อไฟล์ไม่มีรายการสินค้า (Makro หน้า 3/3) — คืนว่าง = ใช้บรรทัดสรุปใบเดียวแบบเดิม
    ///
    /// <para>ใช้ได้เมื่อ: ตารางมี ≥2 กลุ่ม · ทุกกลุ่มรู้ชนิด · ตาราง "รวม" = ยอดรวมทั้งสิ้น · VAT ตาราง = VAT หัวใบ ·
    /// ฐานตาราง = ยอดก่อน VAT ของหัวเอกสาร — ทุกเลขพิมพ์บนกระดาษ ไม่มีการแต่ง</para></summary>
    public static IReadOnlyList<OcrVatGroup> SummaryGroupLines(
        OcrTotalDecomposition d, decimal? total, decimal? vat, decimal headerSubTotal)
    {
        var g = d.Groups;
        if (!g.Found || g.Groups.Count < 2 || !g.AllKnown || total is not decimal t) return Array.Empty<OcrVatGroup>();
        if (Math.Abs(g.Gross - t) > Tol || Math.Abs(g.Vat - (vat ?? 0m)) > Tol || Math.Abs(g.Net - headerSubTotal) > Tol)
            return Array.Empty<OcrVatGroup>();
        return g.Groups.Where(x => x.Net > 0m).ToList();
    }

    private static IReadOnlyList<OcrPaymentAdjustment> ReadAdjustments(string rawText, decimal discount)
    {
        var list = new List<OcrPaymentAdjustment>();
        foreach (Match m in SignedAdjustment.Matches(rawText))
        {
            if (!decimal.TryParse(m.Groups["num"].Value.Replace(",", ""), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var v)) continue;
            var label = NotePrefix.Replace(m.Groups["label"].Value, "").Trim();
            if (label.Length == 0) continue;
            list.Add(new OcrPaymentAdjustment(label, m.Groups["sign"].Value == "+" ? v : -v));
        }
        // ใช้เฉพาะเมื่อผลรวมอธิบายส่วนต่างได้พอดี — ไม่งั้นเป็นตัวเลขคนละเรื่อง (ไม่แสดงดีกว่าแสดงผิด)
        return list.Count > 0 && Math.Abs(list.Sum(a => a.Amount) + discount) <= Tol
            ? list
            : Array.Empty<OcrPaymentAdjustment>();
    }
}
