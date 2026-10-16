using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>ตัวเลขหนึ่งตัวบนข้อความกระดาษ (ตามลำดับที่อ่าน) — <see cref="IsPercent"/> = มี "%" ตามหลัง</summary>
public readonly record struct OcrPaperNumber(decimal Value, bool IsPercent);

/// <summary>สิ่งที่ตัวกันจำนวนระเบิดทำกับบรรทัดหนึ่ง</summary>
public enum OcrLineQuantityAction
{
    /// <summary>ไม่แตะ (ลงตัวอยู่แล้ว · ข้อมูลไม่พอ · ยอด ≤ 0)</summary>
    Keep = 0,
    /// <summary>ไม่มีราคาต่อหน่วย แต่มีจำนวน + ยอด ⇒ ราคา = ยอด ÷ จำนวน (เติมช่องว่าง ไม่ได้เปลี่ยนค่าที่อ่านได้)</summary>
    FillUnitPrice,
    /// <summary>กระดาษพิมพ์ส่วนลดของบรรทัดนี้ (ราคา … ส่วนลด … ยอด บนแถวเดียวกัน · round(จำนวน × ราคา) − ส่วนลด = ยอด ทุกสตางค์)
    /// ⇒ ลงเป็นส่วนลดของบรรทัด · จำนวน/ราคาคงตามกระดาษ</summary>
    PrintedDiscount,
    /// <summary>(เคส ก เดิม) ราคาต่อหน่วย ≈ ยอดทั้งบรรทัดทั้งที่จำนวน &gt; 1 ⇒ engine อ่านยอดมาใส่ช่องราคา ⇒ ราคา = ยอด ÷ จำนวน</summary>
    FixUnitPrice,
    /// <summary>(เคส ข / เดิม) จำนวนไม่ได้พิมพ์บนแถวของกระดาษ (หรือเป็นยอดที่อ่านหลงคอลัมน์) ⇒ จำนวน = ยอด ÷ ราคา</summary>
    FixQuantity,
    /// <summary>จำนวนที่ engine อ่าน<b>พิมพ์อยู่บนแถวของกระดาษ</b> แต่ จำนวน × ราคา ≠ ยอด และกระดาษไม่มีส่วนลดอธิบาย ⇒
    /// คงจำนวน/ราคาตามกระดาษ (ห้ามแต่งจำนวน) + แท็กให้คนตรวจ — ตัวสร้างบรรทัดรายงาน [Σ-GAP] ต่อเอง</summary>
    KeepPrintedGap,
}

/// <summary>ผลของ <see cref="OcrLineQuantityGuard.Decide"/> — ค่าที่ผู้เรียกต้องเขียนลงบรรทัด (null = ไม่เปลี่ยนช่องนั้น)</summary>
/// <param name="Note">ข้อความ (ขึ้นต้นด้วย <see cref="OcrLineQuantityGuard.Tag"/>) เมื่อระบบเปลี่ยน/คงค่าที่ขัดกัน — ผู้เรียกต้องส่งต่อให้คนเห็น · null = ไม่มีอะไรต้องบอก</param>
public sealed record OcrLineQuantityDecision(
    OcrLineQuantityAction Action, decimal? Quantity, decimal? UnitPrice, decimal? LineDiscount, string? Note);

/// <summary>
/// **ตัวกัน "จำนวนระเบิด" ของบรรทัดสแกน + หลักฐานจากแถวบนกระดาษ** (pure · ตัวตัดสินตัวเดียวของ <c>OcrService.SanitizeVatSplitArtifacts</c> ข้อ 1b
/// และ replay harness)
///
/// <para>═══ ที่มา (ผู้ใช้รายงาน 2026-10-16 · RE-20261000111 พรามี่ ควอลิตี้) ═══ "จำนวนสินค้า ทำไมระบบเปลี่ยนไปจากเอกสาร ในเอกสารขึ้นเป็นส่วนลด
/// ตามรายการ ระบบต้องจัดการบันทึกให้ถูกส่วน ไม่ใช่มาปรับจำนวนรายการ" — กระดาษพิมพ์ จำนวน 1.00 · ราคา 1,330.00 · ส่วนลด 293.00 · VAT 7% ·
/// มูลค่ารวมภาษี 1,037.00 · engine คืนบรรทัด (1, 1,330, 1,037) โดยไม่มีช่องส่วนลด (ช่องส่วนลดรายบรรทัดมีแหล่งเดียวคือ e-Tax XML) ⇒ ตัวกันเดิม
/// เห็น 1 × 1,330 ≠ 1,037 แล้ว<b>หารจำนวนใหม่</b> = 1,037 ÷ 1,330 = 0.78 · ส่วนลดหาย · ยอดรวมยังตรง จึงเงียบ · เปิดแก้แล้วบันทึก
/// round(0.78 × 1,330) = 1,037.40 ≠ กระดาษอีกชั้น · ตัวกันนี้มีมาตั้งแต่ก่อนประวัติเรพ (นำเข้า 350a394d 2026-09-03) เพื่อบรรทัดที่ engine
/// "อ่านยอดมาใส่ช่องจำนวน" (จำนวน 1,180 = ยอด) — ไม่เคยถามกระดาษว่าจำนวนนั้นพิมพ์อยู่จริงไหม</para>
///
/// <para>═══ กติกา (ลำดับ) ═══
/// <list type="number">
/// <item>ส่วนลดที่เอกสารประกาศแล้ว (<see cref="OcrEtaxLineNormalizer.ProvenLineDiscount"/>) อธิบายยอดได้ = ไม่แตะ (เดิม)</item>
/// <item><b>ส่วนลดที่กระดาษพิมพ์</b>: บนข้อความกระดาษมี ราคาต่อหน่วย → (ตัวเลข ≤ 4 ตัว) → ยอดบรรทัด และตัวเลขระหว่างนั้นตัวหนึ่ง d ทำให้
///   round(จำนวน × ราคา) − d = ยอด <b>ทุกสตางค์</b> (หรือ d% ของยอดก่อนลด = ส่วนต่างทุกสตางค์ · ไม่รับ 7% — ชนกับคอลัมน์ VAT) ⇒
///   <see cref="OcrLineQuantityAction.PrintedDiscount"/> · ทำก่อนเกณฑ์ ±2% ของตัวกันเดิม (ส่วนลดเล็กที่พิมพ์จริงก็เป็นส่วนลด ไม่ใช่เศษ)</item>
/// <item>ลงตัวในเกณฑ์เดิม (±2% หรือ ฿1) = ไม่แตะ (เดิม)</item>
/// <item>(เคส ก เดิม) ราคา ≈ ยอด · จำนวน &gt; 1 ⇒ แก้ราคา (จำนวนคือมิเตอร์จริง)</item>
/// <item>จำนวน ≈ ยอด (อ่านหลงคอลัมน์) ⇒ แก้จำนวน (เดิม)</item>
/// <item><b>จำนวนพิมพ์อยู่บนแถว</b> (ตัวเลขก่อนราคาติดกัน หรืออยู่ระหว่างราคากับยอด เท่าจำนวนที่ engine อ่าน) ⇒ <b>ห้ามหารจำนวนใหม่</b> —
///   คงตามกระดาษ + <see cref="Tag"/> (ส่วนต่างที่ไม่มีอะไรอธิบาย = ให้คนดู ไม่ใช่แต่งจำนวน)</item>
/// <item>อื่น ๆ (ไม่มีข้อความกระดาษ · หาแถวไม่เจอ · จำนวนไม่ได้พิมพ์บนแถว เช่นสินค้าชั่งน้ำหนักที่ engine เติมจำนวน 1) ⇒ แก้จำนวน = ยอด ÷ ราคา
///   (พฤติกรรมเดิม) แต่<b>ไม่เงียบ</b> — คืน <see cref="Tag"/> ให้ผู้เรียกเขียนลงหมายเหตุ (ห้ามอนุมัติเอง)</item>
/// </list></para>
///
/// <para>ไม่ทำ (จดตรง ๆ): ไม่อ่านหัวคอลัมน์ "ส่วนลด" (engine ภาพคืนตารางคนละรูป — ตัวตัดสินคือเลขคณิตของแถว ไม่ใช่ชื่อหัว) · ส่วนลดที่พิมพ์
/// <b>หลัง</b>ยอดบรรทัด · ราคา/ยอดที่ OCR อ่านเพี้ยนจนหาแถวไม่เจอ ⇒ ตกกติกาสุดท้าย (เดิม + แท็ก)</para>
/// </summary>
public static class OcrLineQuantityGuard
{
    /// <summary>แท็กใน ProcessingNotes เมื่อระบบเปลี่ยนจำนวน/ราคาที่ engine อ่าน หรือคงค่าที่ขัดกันไว้ (ห้ามอนุมัติเอง — <c>OcrPostingReadiness.BlockingTags</c>)</summary>
    public const string Tag = "[LINE-QTY]";

    /// <summary>จำนวนตัวเลขที่ยอมให้อยู่ระหว่างราคาต่อหน่วยกับยอดบรรทัดบนแถวเดียวกัน (ส่วนลด · อัตรา VAT · ราคาหลังลด · ส่วนลด %)</summary>
    public const int MaxNumbersBetween = 4;

    // ลำดับ alternation สำคัญ: เงินมีจุลภาค 2 ตำแหน่ง → จำนวนเต็มมีจุลภาค → ทศนิยม 2 ตำแหน่ง → ทศนิยมอื่น → จำนวนเต็ม ·
    // ไม่มี lookahead ห้ามตัวเลขตาม ⇒ ข้อความ PDF ที่ช่องติดกัน ("1,330.00293.007%1,037.00" — PdfPig ของใบ RE-20261000111) แตกได้ถูกตัว
    private static readonly Regex NumberRe = new(
        @"(?<n>\d{1,3}(?:,\d{3})+\.\d{2}|\d{1,3}(?:,\d{3})+(?![\d.])|\d+\.\d{2}|\d+\.\d{1,4}|\d+)(?<pct>\s?%)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static decimal R2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>ตัวเลขทุกตัวบนข้อความกระดาษตามลำดับ (เรียกครั้งเดียวต่อสแกน แล้วส่งให้ <see cref="Decide"/> ทุกบรรทัด) · ข้อความว่าง = ว่าง</summary>
    public static IReadOnlyList<OcrPaperNumber> Numbers(string? paperText)
    {
        if (string.IsNullOrWhiteSpace(paperText)) return Array.Empty<OcrPaperNumber>();
        var list = new List<OcrPaperNumber>();
        foreach (Match m in NumberRe.Matches(paperText))
        {
            if (!decimal.TryParse(m.Groups["n"].Value.Replace(",", ""), System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)) continue;
            list.Add(new OcrPaperNumber(v, m.Groups["pct"].Success));
        }
        return list;
    }

    /// <summary>ตัดสินบรรทัดหนึ่ง — ดูกติกาที่หัวคลาส · <paramref name="paperNumbers"/> ว่าง/null = ไม่มีหลักฐานจากกระดาษ (พฤติกรรมเดิม + แท็ก)</summary>
    public static OcrLineQuantityDecision Decide(
        decimal? quantity, decimal? unitPrice, decimal? amount, decimal? lineDiscount, IReadOnlyList<OcrPaperNumber>? paperNumbers)
    {
        var amt = amount ?? 0m;
        var up = unitPrice ?? 0m;
        var qty = quantity ?? 0m;
        if (amt <= 0m) return Keep;

        // ราคา/หน่วยหาย แต่มียอด+จำนวน → หารหาให้ (บิลสาธารณูปโภคพิมพ์แต่ปริมาณกับยอดรวม ไม่พิมพ์ราคาต่อหน่วย) — เดิมทุกตัวอักษร
        if (up <= 0m && qty > 0m)
            return new(OcrLineQuantityAction.FillUnitPrice, null,
                Math.Round(amt / qty, 4, MidpointRounding.AwayFromZero), null, null);
        if (up <= 0m || qty <= 0m) return Keep;

        var gross = R2(up * qty);
        var proven = OcrEtaxLineNormalizer.ProvenLineDiscount(qty, up, amt, lineDiscount);
        var tol = Math.Max(1m, Math.Abs(amt) * 0.02m);

        // ★ ส่วนลดที่กระดาษพิมพ์ — ก่อนเกณฑ์ ±2% (ส่วนลด 15 บาทของสินค้า 1,000 ก็เป็นส่วนลดจริง)
        var row = proven > 0m ? default : FindRow(paperNumbers, qty, up, amt);
        if (proven == 0m && row.Discount is decimal printed)
            return new(OcrLineQuantityAction.PrintedDiscount, null, null, printed, null);

        var computed = gross - proven;
        if (Math.Abs(computed - amt) <= tol) return Keep;   // ตรงอยู่แล้ว (เกณฑ์เดิม)

        // ⚠️ เคส ก (บิลค่าไฟ 59 ล้าน — เดิม): ราคา ≈ ยอดทั้งที่จำนวน > 1 ⇒ ตัวผิดคือราคา ไม่ใช่จำนวน (3,611 หน่วยคือมิเตอร์จริง)
        var qtyLooksLikeAmount = Math.Abs(qty - amt) <= tol;
        if (qty > 1m && !qtyLooksLikeAmount && Math.Abs(up - amt) <= tol)
        {
            var price = Math.Round(amt / qty, 4, MidpointRounding.AwayFromZero);
            return new(OcrLineQuantityAction.FixUnitPrice, null, price, null,
                $"{Tag} ราคาต่อหน่วย {up:N2} เท่ากับยอดทั้งบรรทัด {amt:N2} (จำนวน {qty:0.###}) — ระบบตั้งราคาต่อหน่วย = ยอด ÷ จำนวน = {price:0.####} · ตรวจกับกระดาษก่อนอนุมัติ");
        }

        // ★ จำนวนพิมพ์อยู่บนแถว + ไม่มีส่วนลดบนกระดาษอธิบาย ⇒ ห้ามแต่งจำนวน (เคส ข "จำนวน = ยอด" ยังแก้ตามเดิม — จำนวนนั้นไม่ใช่จำนวน)
        if (!qtyLooksLikeAmount && row.QuantityPrinted)
            return new(OcrLineQuantityAction.KeepPrintedGap, null, null, null,
                $"{Tag} จำนวน {qty:0.###} × ราคา {up:N2} = {gross:N2} แต่ยอดบรรทัด {amt:N2} (ต่าง {gross - amt:N2}) และกระดาษไม่พิมพ์ส่วนลดที่อธิบายส่วนต่าง — "
                + "ระบบคงจำนวน/ราคาตามกระดาษ (ไม่หารจำนวนใหม่) · ตรวจบรรทัดนี้กับกระดาษก่อนอนุมัติ");

        var fixedQty = Math.Round(amt / up, 3, MidpointRounding.AwayFromZero);
        var newQty = fixedQty > 0m ? fixedQty : 1m;
        return new(OcrLineQuantityAction.FixQuantity, newQty, null, null,
            $"{Tag} จำนวน {qty:0.###} × ราคา {up:N2} ≠ ยอดบรรทัด {amt:N2} และไม่พบจำนวนนี้บนแถวของกระดาษ — ระบบตั้งจำนวน = ยอด ÷ ราคา = {newQty:0.###} · ตรวจกับกระดาษก่อนอนุมัติ");
    }

    private static readonly OcrLineQuantityDecision Keep = new(OcrLineQuantityAction.Keep, null, null, null, null);

    /// <summary>หาแถวของบรรทัดบนข้อความกระดาษ: ราคาต่อหน่วย แล้วยอดบรรทัดตามมาภายใน <see cref="MaxNumbersBetween"/> ตัว ·
    /// คืน (ส่วนลดที่พิมพ์ · จำนวนพิมพ์อยู่บนแถวไหม) · ส่วนลดต้องเป็นค่าเดียวทุกแถวที่เจอ (สองค่าต่างกัน = ไม่รู้ = null)</summary>
    private static (decimal? Discount, bool QuantityPrinted) FindRow(IReadOnlyList<OcrPaperNumber>? nums, decimal qty, decimal up, decimal amt)
    {
        if (nums is null || nums.Count == 0) return (null, false);
        var gross = R2(up * qty);
        var gap = gross - amt;
        var discounts = new HashSet<decimal>();
        var qtyPrinted = false;
        for (var i = 0; i < nums.Count; i++)
        {
            if (nums[i].IsPercent || nums[i].Value != up) continue;
            // จำนวนพิมพ์ติดก่อนราคา (ข้ามคำ/หน่วยนับได้ — ตัวเลขตัวก่อนหน้า)
            var qtyAt = i > 0 && !nums[i - 1].IsPercent && nums[i - 1].Value == qty ? i - 1 : -1;
            var j = -1;
            for (var k = i + 1; k < nums.Count && k <= i + MaxNumbersBetween + 1; k++)
                if (!nums[k].IsPercent && nums[k].Value == amt) { j = k; break; }
            if (j < 0)
            {
                if (qtyAt >= 0) qtyPrinted = true;
                continue;
            }
            if (qtyAt < 0)
                for (var k = i + 1; k < j; k++)
                    if (!nums[k].IsPercent && nums[k].Value == qty) { qtyAt = k; break; }
            if (qtyAt >= 0) qtyPrinted = true;
            if (gap < 0.01m) continue;
            for (var k = i + 1; k < j; k++)
            {
                if (k == qtyAt) continue;   // ตัวเลขเดียวกันนับเป็นทั้งจำนวนและส่วนลดไม่ได้
                var v = nums[k].Value;
                if (!nums[k].IsPercent)
                {
                    // ตัวเลขที่เท่า VAT 7% ของบรรทัดพอดี (ราคารวม VAT · VAT · ยอดก่อน VAT = "107.00 7.00 100.00") คือคอลัมน์ VAT ได้เท่ากัน ⇒ ไม่รู้ = ไม่ตีเป็นส่วนลด
                    if (v > 0m && v < gross && R2(gross - v) == amt && v != R2(amt * 0.07m) && v != R2(gross * 7m / 107m))
                        discounts.Add(v);
                }
                else if (v > 0m && v < 100m && v != 7m && R2(gross * v / 100m) == gap)
                    discounts.Add(gap);
            }
        }
        return (discounts.Count == 1 ? discounts.First() : null, qtyPrinted);
    }

    /// <summary>ใบที่อ่านส่วนลดรายบรรทัดจากกระดาษได้ (<see cref="OcrLineQuantityAction.PrintedDiscount"/> ≥ 1 บรรทัด) และเลขหัวใบพิสูจน์ว่าราคาบนกระดาษ<b>รวม VAT</b>
    /// (ตัวจำแนกตัวเดียว <see cref="OcrLineReconciler.Classify"/> = เคส A บน Σ ยอดหลังลด) ⇒ ราคา · ส่วนลด · ยอด ของทุกบรรทัดอยู่ฐานรวม VAT
    /// (ธง <c>PriceIncludesVat</c> ของบรรทัด — ป้ายส่วนลดในหน้ารีวิว · ทุนสต็อก/สินทรัพย์ถอด VAT) · ไม่มีส่วนลดจากกระดาษ = false (ใบเดิมไม่ถูกแตะ —
    /// ตัวสร้างบรรทัดตัดสินเคส A เองเหมือนเดิม)</summary>
    public static bool PaperDiscountPricesIncludeVat(
        bool anyPaperDiscount, decimal lineSum, decimal? headerSubTotal, decimal? headerVat, decimal? headerTotal, decimal? headerDiscount)
    {
        if (!anyPaperDiscount || headerVat is not > 0m || headerTotal is not > 0m) return false;
        return OcrLineReconciler.Classify(lineSum, headerSubTotal ?? 0m, headerVat.Value, headerTotal.Value, headerDiscount ?? 0m).Case
            == OcrLineReconcileCase.PricesIncludeVat;
    }
}
