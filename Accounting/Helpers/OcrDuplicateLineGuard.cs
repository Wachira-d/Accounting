using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Accounting.Helpers;

/// <summary>แถวรายการในรูปกลางที่ตัวตัดสินแถวซ้ำใช้ — engine items · บรรทัดที่โมเดล/นักเรียนแตกจากข้อความ · บรรทัดใน replay harness
/// (ทุกทางเข้าแปลงมาเป็นรูปนี้ แล้วเดินตัวตัดสินตัวเดียว)</summary>
public readonly record struct OcrCandidateRow(
    string? Description, decimal? Quantity, decimal? UnitPrice, decimal? Amount, decimal? LineDiscount = null);

/// <summary>คำตัดสินของ <see cref="OcrDuplicateLineGuard.Decide"/></summary>
/// <param name="Deduped">true = ตัดแถวที่เห็นซ้ำแล้ว (ใช้ <paramref name="KeepIndexes"/>) · false = คงทุกแถว</param>
/// <param name="KeepIndexes">ดัชนีแถว (ตามลำดับเดิม) ที่คงไว้ — เมื่อไม่ตัด = ทุกดัชนี</param>
/// <param name="DroppedCount">จำนวนแถวที่ถูกตัด (0 เมื่อไม่ตัด)</param>
/// <param name="SumAll">Σ ยอดทุกแถวตามที่รับมา</param>
/// <param name="SumDistinct">Σ ยอดเมื่อนับแถวที่เหมือนกันทุกช่องแค่ครั้งเดียว</param>
/// <param name="Reason">เหตุผลภาษาไทย (ไว้เขียน trace — ทั้งตอนตัดและตอนไม่ตัด)</param>
public sealed record OcrDuplicateRowDecision(
    bool Deduped, IReadOnlyList<int> KeepIndexes, int DroppedCount, decimal SumAll, decimal SumDistinct, string Reason);

/// <summary>
/// **"แถวเดียวกันที่ถูกเห็นสองครั้ง" กับ "กระดาษพิมพ์รายการซ้ำจริง" — ตัวตัดสินตัวเดียว (pure)**
///
/// ═══ ที่มา (ผู้ใช้รายงาน 2026-10-09 · ใบแจ้งหนี้/ใบกำกับภาษี BS2026100001 บริษัท บุญทรัพย์ ถาวร จำกัด) ═══
/// <para>PDF จากโปรแกรมออกบิลพิมพ์ <b>หน้า 1 = ต้นฉบับ · หน้า 2 = สำเนา</b> ของใบเดียวกัน (หัวใบ · ตาราง 5 บรรทัด · ยอดท้ายใบ
/// เหมือนกันทุกตัวอักษร) ⇒ engine ที่อ่าน "ทั้งไฟล์เป็นเอกสารเดียว" คืนตารางรายการ 10 แถว (5 แถวเดิมสองรอบ) · ขั้นยุบบรรทัด
/// ชื่อ+ราคาเท่ากัน (<c>OcrService.SanitizeVatSplitArtifacts</c> ข้อ 4 — เจตนาเดิมคือ "สินค้าเดียวที่ถูกแตกเป็น 2 บรรทัด VAT split")
/// <b>บวกจำนวนและยอด</b> ⇒ ทุกบรรทัดจำนวน ×2 (100 → 200 · 50 → 100) ยอด ×2 ราคาต่อหน่วยคงเดิม (ยอด ÷ จำนวน) · Σ บรรทัด 42,010
/// = 2 × ยอดก่อน VAT 21,005 ที่อ่านจากหัวใบ ⇒ ตัวกระทบยอดฟ้อง Ambiguous [Σ-GAP] แต่จำนวนที่ผิดอยู่บนหน้าจอแล้ว —
/// ผู้ใช้ต้องแก้ทุกบรรทัดเอง (ผิดกฎเหล็ก #3 "ยืนยันอย่างเดียว") และถ้าไม่สังเกต = สต็อก/ต้นทุนเข้า 2 เท่า</para>
///
/// ═══ กติกา (ยอดหัวใบเป็นตัวชี้ขาด — ไม่เดา) ═══
/// <list type="number">
/// <item>"แถวซ้ำ" = คำอธิบาย (ตัดช่องว่าง/ตัวพิมพ์) · จำนวน · ราคาต่อหน่วย · ยอด · ส่วนลดบรรทัด <b>เท่ากันทุกช่อง</b> — แค่ชื่อ+ราคาเท่ากัน
///   แต่จำนวนต่าง ไม่ใช่แถวซ้ำ (เป็นสินค้าเดียวที่ซื้อสองรายการ — ปล่อยให้ขั้นยุบเดิมรวม) · แถวไม่มีคำอธิบายไม่ถือว่าซ้ำกับใคร</item>
/// <item><b>รูปแบบ "สำเนาทั้งหน้า" เท่านั้น</b> (ฝ่ายค้าน 2026-10-09 ข้อ 2): ทุกชุดแถวต้องซ้ำ<b>จำนวนครั้งเท่ากัน</b> (k ≥ 2) และต้องมีแถวต่างกัน
///   อย่างน้อย 2 ชุด — "น้ำดื่ม 20 บาท × 3 แถว" ที่หัวใบอ่านเพี้ยนเป็น 20 หรือ "2 แถวซ้ำ + 1 แถวเดี่ยว" ไม่เข้ารูป ⇒ ไม่แตะ ให้คนดู
///   (หัวใบที่อ่านผิดเป็นยอดบรรทัดเดียวจะทำให้ Σ หลังตัด "ตรง" โดยบังเอิญ แล้วเอกสารดูสะอาดพอจะอนุมัติเอง)</item>
/// <item>Σ ทุกแถว ตรงยอดหัวใบตัวใดตัวหนึ่ง (ก่อน VAT · รวม · รวม−VAT · และเมื่อกระดาษพิมพ์ส่วนลดท้ายบิล: +ส่วนลด) ⇒ <b>กระดาษพิมพ์
///   รายการซ้ำจริง</b> (น้ำดื่ม 10 ขวด สองบรรทัด หัวใบ 100) ⇒ คงทุกแถว — ขั้นยุบเดิมจะรวมเป็นจำนวน 20 เหมือนเดิม</item>
/// <item>Σ ทุกแถว ไม่ตรง แต่ Σ หลังนับแถวซ้ำครั้งเดียว ตรงยอดหัวใบ ⇒ <b>แถวเดียวกันถูกเห็นซ้ำ</b> (ต้นฉบับ/สำเนา · หน้าซ้ำ · ตารางถูก
///   อ่านสองรอบ) ⇒ คงแถวแรกของแต่ละชุด · เหตุผลขึ้นต้นด้วย <see cref="Tag"/> ซึ่งเป็นแท็ก<b>ห้ามอนุมัติเอง</b> (<c>OcrPostingReadiness</c>) —
///   เอกสารที่ระบบตัดแถวเองต้องมีคนดูก่อนเสมอ (F2 ข้อ 3)</item>
/// <item>ไม่มียอดหัวใบให้เทียบ หรือไม่ตรงทั้งสองทาง ⇒ <b>คงทุกแถว</b> (ไม่รู้ = ไม่แต่ง — ให้ด่าน [Σ-GAP] บอกคน) · แถวซ้ำบางส่วน/คู่ที่ OCR
///   อ่านเพี้ยนไปช่องเดียว ⇒ ไม่เข้ารูป ⇒ ขั้นยุบเดิมยังรวมคู่ที่เหมือนกัน (×2 บางบรรทัด) แล้ว [Σ-GAP] ฟ้อง — ยอมรับ (จดในตารางครอบคลุมข้อ 9)</item>
/// <item>บรรทัดจาก e-Tax XML ไม่เดินตัวนี้ (ผู้เรียกกรองออกก่อน — เอกสารลงนามไม่มี "ถูกอ่านสองรอบ" และบรรทัดชื่อซ้ำใน XML เป็นบรรทัดจริง)</item>
/// </list>
/// ค่าเผื่อ = <see cref="OcrLineReconciler.Tolerance"/> (ตัวเดียวกับตัวกระทบยอด) · ผู้เรียกทุกทาง: <c>SanitizeVatSplitArtifacts</c>
/// (ตารางจาก engine ทุกตัว · ส่งส่วนลดท้ายบิลที่อ่านได้ <c>data.DiscountAmount</c> ซึ่งถูกล้างแล้วเมื่อเป็นส่วนลดหลังใบกำกับ) ·
/// <see cref="OcrLineSplitGuard.Evaluate"/> (บรรทัดจากข้อความ — ไม่รู้ส่วนลด ส่ง 0) · <c>OcrReplayHarness.BuildLines</c> (golden) —
/// ล็อกด้วย <c>tools/required_call_site_check.py</c>
/// </summary>
public static class OcrDuplicateLineGuard
{
    /// <summary>แท็กใน trace/หมายเหตุเมื่อตัดแถวซ้ำ — อยู่ใน <c>OcrPostingReadiness.BlockingTags</c> (ห้ามอนุมัติเอง)</summary>
    public const string Tag = "[DUP-ROWS]";

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>ตัดสินว่าแถวที่รับมา "ถูกเห็นซ้ำ" หรือ "กระดาษพิมพ์ซ้ำจริง" — ดูกติกาบนคลาส</summary>
    /// <param name="rows">แถวตามลำดับที่ engine/โมเดลคืน</param>
    /// <param name="headerSubTotal">ยอดก่อน VAT บนหัวใบ (null/0 = ไม่รู้)</param>
    /// <param name="headerVat">VAT บนหัวใบ (null/0 = ไม่รู้)</param>
    /// <param name="headerTotal">ยอดรวมบนหัวใบ (null/0 = ไม่รู้)</param>
    /// <param name="headerDiscount">ส่วนลดท้ายบิลที่กระดาษพิมพ์และยังมีผลต่อฐาน (0 = ไม่มี / เป็นส่วนลดหลังใบกำกับ) — Σ บรรทัดของใบที่มีส่วนลด
    /// = ยอดหัวใบ + ส่วนลด (เคส C/E/DiscountOnTotal ของ <see cref="OcrLineReconciler"/>)</param>
    public static OcrDuplicateRowDecision Decide(
        IReadOnlyList<OcrCandidateRow> rows, decimal? headerSubTotal, decimal? headerVat, decimal? headerTotal,
        decimal headerDiscount = 0m)
    {
        var all = Enumerable.Range(0, rows.Count).ToList();
        if (rows.Count < 2)
            return new(false, all, 0, rows.Sum(r => EffectiveAmount(r)), rows.Sum(r => EffectiveAmount(r)), "แถวเดียว — ไม่มีอะไรให้เทียบ");

        var firstBySignature = new Dictionary<string, int>(StringComparer.Ordinal);
        var countBySignature = new Dictionary<string, int>(StringComparer.Ordinal);
        var keep = new List<int>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            var sig = Signature(rows[i], i);
            countBySignature[sig] = countBySignature.TryGetValue(sig, out var c) ? c + 1 : 1;
            if (firstBySignature.ContainsKey(sig)) continue;
            firstBySignature[sig] = i;
            keep.Add(i);
        }
        var sumAll = rows.Sum(r => EffectiveAmount(r));
        var sumDistinct = keep.Sum(i => EffectiveAmount(rows[i]));
        var dropped = rows.Count - keep.Count;
        if (dropped == 0)
            return new(false, all, 0, sumAll, sumDistinct, "ไม่มีแถวที่เหมือนกันทุกช่อง");

        // รูปแบบ "สำเนาทั้งหน้า": ทุกชุดซ้ำ k ครั้งเท่ากัน (k ≥ 2) และมีแถวต่างกัน ≥ 2 ชุด — นอกรูปนี้ไม่ตัด (ให้คนดู)
        var repeatCounts = countBySignature.Values.Distinct().ToList();
        var uniformRepeat = repeatCounts.Count == 1 && repeatCounts[0] >= 2;
        if (!uniformRepeat || keep.Count < 2)
            return new(false, all, 0, sumAll, sumDistinct,
                keep.Count < 2
                    ? $"พบแถวเดียวกัน {rows.Count} แถว แต่ไม่มีแถวอื่นเทียบ (ไม่ใช่รูปแบบสำเนาทั้งหน้า) — คงทุกแถวให้คนดู"
                    : $"พบแถวที่เหมือนกันทุกช่อง {dropped} แถว แต่ซ้ำไม่เท่ากันทุกชุด (ไม่ใช่รูปแบบสำเนาทั้งหน้า — ยกมาบางแถว/อ่านเพี้ยนบางคู่) — คงทุกแถวให้ด่าน Σ ฟ้อง");

        var figures = HeaderFigures(headerSubTotal, headerVat, headerTotal, headerDiscount);
        if (figures.Count == 0)
            return new(false, all, 0, sumAll, sumDistinct,
                $"พบแถวที่เหมือนกันทุกช่อง {dropped} แถว แต่ไม่มียอดหัวใบให้ตัดสินว่าซ้ำจริงหรือถูกอ่านสองรอบ — คงทุกแถว");

        var matchAll = figures.FirstOrDefault(f => Math.Abs(sumAll - f.Value) <= OcrLineReconciler.Tolerance);
        if (matchAll.Label != null)
            return new(false, all, 0, sumAll, sumDistinct,
                $"Σ ทุกแถว {sumAll:N2} ตรง{matchAll.Label} {matchAll.Value:N2} — กระดาษพิมพ์รายการซ้ำจริง คงทุกแถว");

        var matchDistinct = figures.FirstOrDefault(f => Math.Abs(sumDistinct - f.Value) <= OcrLineReconciler.Tolerance);
        if (matchDistinct.Label != null)
            return new(true, keep, dropped, sumAll, sumDistinct,
                $"{Tag} ตัดแถวที่ถูกอ่านซ้ำ {dropped} แถว (ต้นฉบับ/สำเนา · หน้าซ้ำ · ตารางถูกอ่านสองรอบ): Σ ทุกแถว {sumAll:N2} ไม่ตรงยอดหัวใบ "
                + $"แต่ Σ หลังนับแถวที่เหมือนกันครั้งเดียว {sumDistinct:N2} ตรง{matchDistinct.Label} {matchDistinct.Value:N2} — จำนวน/ยอดตามกระดาษ "
                + $"ไม่ใช่ ×{repeatCounts[0]} · ตรวจก่อนอนุมัติว่าตัดถูกแถว");

        return new(false, all, 0, sumAll, sumDistinct,
            $"พบแถวที่เหมือนกันทุกช่อง {dropped} แถว แต่ Σ ไม่ตรงยอดหัวใบทั้งก่อนตัด ({sumAll:N2}) และหลังตัด ({sumDistinct:N2}) — ไม่เดา คงทุกแถวให้ด่าน Σ ฟ้อง");
    }

    /// <summary>ยอดบรรทัดที่เชื่อถือได้ — ยอดที่พิมพ์ถ้า &gt; 0 ไม่งั้น ราคา × จำนวน (เผื่อ engine ส่งแต่ราคา+จำนวน) · <b>สูตรเดียว</b>ของ
    /// <c>SanitizeVatSplitArtifacts</c> (EffAmt) และตัวตัดสินนี้ (ฝ่ายค้าน 2026-10-09 ข้อ 4: เดิมมีสองสูตรที่ต่างกันที่ยอด 0)</summary>
    public static decimal EffectiveAmount(decimal? amount, decimal? unitPrice, decimal? quantity)
        => (amount ?? 0m) > 0m ? amount!.Value : (unitPrice ?? 0m) * (quantity ?? 1m);

    private static decimal EffectiveAmount(OcrCandidateRow r) => EffectiveAmount(r.Amount, r.UnitPrice, r.Quantity);

    private static string Signature(OcrCandidateRow r, int index)
    {
        var desc = Spaces.Replace((r.Description ?? "").Trim(), " ").ToUpperInvariant();
        // แถวไม่มีคำอธิบายไม่ถือว่าซ้ำกับแถวไหน — คีย์เฉพาะตัว
        if (desc.Length == 0) return "\u0000" + index.ToString(CultureInfo.InvariantCulture);
        var ci = CultureInfo.InvariantCulture;
        return string.Join("\u0001",
            desc,
            (r.Quantity ?? 0m).ToString("0.###", ci),
            (r.UnitPrice ?? 0m).ToString("0.####", ci),
            EffectiveAmount(r).ToString("0.00", ci),
            (r.LineDiscount ?? 0m).ToString("0.00", ci));
    }

    private static List<(string Label, decimal Value)> HeaderFigures(decimal? sub, decimal? vat, decimal? total, decimal discount)
    {
        var list = new List<(string, decimal)>();
        if (sub is > 0m) list.Add(("ยอดก่อน VAT", sub.Value));
        if (total is > 0m) list.Add(("ยอดรวม", total.Value));
        if (total is > 0m && vat is > 0m && total.Value - vat.Value > 0m
            && !(sub is > 0m && Math.Abs(total.Value - vat.Value - sub.Value) <= OcrLineReconciler.Tolerance))
            list.Add(("ยอดรวมหัก VAT", total.Value - vat.Value));
        // ใบที่พิมพ์ส่วนลดท้ายบิล: Σ บรรทัดก่อนลด = หัวใบ + ส่วนลด (เคส C: ก่อน VAT · เคส E/DiscountOnTotal: ยอดรวม)
        if (discount > 0m)
        {
            if (sub is > 0m) list.Add(("ยอดก่อน VAT + ส่วนลด", sub.Value + discount));
            if (total is > 0m) list.Add(("ยอดรวม + ส่วนลด", total.Value + discount));
            if (total is > 0m && vat is > 0m && total.Value - vat.Value > 0m)
                list.Add(("ยอดรวมหัก VAT + ส่วนลด", total.Value - vat.Value + discount));
        }
        return list;
    }
}
